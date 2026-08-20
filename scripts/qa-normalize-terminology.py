#!/usr/bin/env python3
"""Screenshot-QA normaliser for translations/ru_RU.jsonl.

Rewrites the deterministic half of the defects found in the 2026-08-19 screenshot
pass: Cyrillic look-alikes of Latin stat abbreviations, drifting names of bracketed
buff/debuff effects, the order of the numbered damage lines, and the short UI
labels. Everything it touches is decided by localization/terminology.ru.json, so a
term is changed in one place and applied everywhere.

Numbers, placeholders, rich-text tags and compact units are never added or removed
-- only reordered inside a string, which the validator compares as a sorted multiset.

Usage:
  qa-normalize-terminology.py --pairs PAIRS.jsonl --catalog translations/ru_RU.jsonl
                              [--terms localization/terminology.ru.json] [--apply]

Without --apply it reports what would change and writes nothing.
"""
import argparse
import json
import re
import sys
from collections import Counter

TAG = re.compile(r"<[^>]*>")


def load_terms(path):
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    for section in ("stat_abbreviations", "numbered_lines", "effects", "mechanic_names",
                    "ui_labels", "phrases", "corruptions", "overrides"):
        data[section].pop("comment", None)
    return data


# ---------------------------------------------------------------- effects ----

VALUE_HEAD = re.compile(r"^\s*(\(.+?\)|\{\d+\}\s*%?|\d+(?:\.\d+)?\s*%?)\s*")


def split_effect(segment):
    """Split an English bracket body into (leading value, effect name).

    '{2}% Weaken' -> ('{2}%', 'Weaken'); '1.05% Bleed' -> ('1.05%', 'Bleed');
    '{0} HP Shield' -> ('{0} HP', 'Shield'); 'Burn' -> ('', 'Burn').
    """
    match = VALUE_HEAD.match(segment)
    value = ""
    rest = segment.strip()
    if match:
        value = match.group(1).strip()
        rest = segment[match.end():].strip()
    if rest.startswith("HP "):
        value = (value + " HP").strip()
        rest = rest[3:].strip()
    return value, rest


def render_effect(english_segment, effects):
    value, name = split_effect(english_segment)
    target = effects.get(name)
    if target is None:
        return None
    if not value:
        return target
    value = value.replace("Damage Dealt", "нанесенного урона")
    return f"{target} {value}"


def fix_effects(source, translation, effects, stats):
    src = re.findall(r"\[([^\]]*)\]", source)
    dst = list(re.finditer(r"\[([^\]]*)\]", translation))
    if not src or len(src) != len(dst):
        return translation, 0
    out = []
    last = 0
    changed = 0
    for english, match in zip(src, dst):
        rendered = render_effect(english, effects)
        out.append(translation[last:match.start()])
        if rendered is None:
            out.append(match.group(0))
        else:
            out.append("[" + rendered + "]")
            if match.group(1) != rendered:
                changed += 1
        last = match.end()
    out.append(translation[last:])
    return "".join(out), changed


# --------------------------------------------------------- numbered lines ----


def fix_numbered_lines(source, translation, labels):
    """Force the source order '#N Label:' and the canonical Russian label."""
    changed = 0
    for english, russian in sorted(labels.items(), key=lambda kv: -len(kv[0])):
        if f" {english}:" not in source:
            continue
        variants = [
            re.compile(r"#\s?(\d+)\s+" + re.escape(english) + r"\s*:"),
            re.compile(r"#\s?(\d+)\s+" + re.escape(russian) + r"\s*:"),
            re.compile(re.escape(russian) + r"\s*(?:#|№)\s?(\d+)\s*:"),
        ]
        for pattern in variants:
            translation, count = pattern.subn(lambda m: f"#{m.group(1)} {russian}:", translation)
            changed += count
    # The same labels also appear without a #N prefix on single-hit skills.
    for english, russian in sorted(labels.items(), key=lambda kv: -len(kv[0])):
        if not re.search(r"(?:<br>|<i>)\s*" + re.escape(english) + r"\s*:", source):
            continue
        pattern = re.compile(r"((?:<br>|<i>)\s*)" + re.escape(english) + r"\s*:")
        translation, count = pattern.subn(lambda m: m.group(1) + russian + ":", translation)
        changed += count
    return translation, changed


NUMERO = re.compile(r"№\s?(\d+)")


def fix_numero(source, translation):
    if "№" not in translation or "№" in source:
        return translation, 0
    fixed, count = NUMERO.subn(lambda m: f"#{m.group(1)}", translation)
    return fixed, count


# --------------------------------------------------------- accuracy line ----

ACCURACY_EN = re.compile(r"Accuracy required for:\s*(.+?)(?=<br>|\Z)", re.S)
ACCURACY_RU = re.compile(
    r"(?:Требуется|Требует|Требуемая|Необходима|Нужна|Точность|точность|Accuracy)"
    r"[^:<]{0,32}:\s*(.+?)(?=<br>|\Z)", re.S)
TEXT_NODE = re.compile(r">([^<>]+)<")


def fix_accuracy_line(source, translation, names, lead_in):
    """Rebuild the 'Accuracy required for:' line from the source vocabulary.

    The payload is a closed comma-separated list of mechanic names, so it can be
    regenerated instead of repaired -- that is what removes the drift between
    identical skills of different Invokers. The source markup is reused verbatim,
    so the protected-token multiset never changes.
    """
    english = ACCURACY_EN.search(source)
    if not english:
        return translation, 0
    payload_en = english.group(1)
    parts = [part.strip() for part in TAG.sub("", payload_en).split(",")]
    if not parts or not all(part in names for part in parts):
        return translation, 0

    queue = list(parts)

    def swap(match):
        chunk = match.group(1)
        stripped = chunk.strip()
        if not stripped or stripped == ",":
            return match.group(0)
        if queue and stripped == queue[0]:
            return ">" + chunk.replace(stripped, names[queue.pop(0)]) + "<"
        return match.group(0)

    payload_ru = TEXT_NODE.sub(swap, payload_en)
    if queue:
        return translation, 0

    russian = ACCURACY_RU.search(translation)
    if not russian:
        return translation, 0
    fixed = (translation[:russian.start()] + lead_in + " " + payload_ru
             + translation[russian.end():])
    return (fixed, 1) if fixed != translation else (translation, 0)


# ------------------------------------------------------------ stat labels ----


def fix_stats(source, translation, stats):
    changed = 0
    for cyrillic, latin in stats.items():
        if latin not in source:
            continue
        pattern = re.compile(r"(?<![А-Яа-яЁё])" + re.escape(cyrillic) + r"(?![А-Яа-яЁё])")
        translation, count = pattern.subn(latin, translation)
        changed += count
    return translation, changed


# ------------------------------------------------------------- ui labels ----


def fix_ui_label(source, translation, labels):
    target = labels.get(source.strip())
    if target is None or translation.strip() == target:
        return translation, 0
    return target, 1


# --------------------------------------------------------------- overrides ----


def fix_override(source, translation, overrides):
    """Exact-string replacements for compact UI labels and families that no rule covers."""
    target = overrides.get(source.strip())
    if target is None or translation.strip() == target:
        return translation, 0
    return target, 1


# ---------------------------------------------------------------- period ----


def fix_trailing_period(source, translation):
    if source.rstrip().endswith((".", "!", "?", ":", ">")):
        return translation, 0
    if not translation.rstrip().endswith("."):
        return translation, 0
    if TAG.sub("", source).strip().endswith("."):
        return translation, 0
    return translation.rstrip()[:-1], 1


# ------------------------------------------------------------------ main ----


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pairs", required=True)
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--terms", default="localization/terminology.ru.json")
    parser.add_argument("--report", default="")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()

    terms = load_terms(args.terms)
    english = {}
    with open(args.pairs, encoding="utf-8") as handle:
        for line in handle:
            row = json.loads(line)
            english[row["id"]] = row["en"]

    counts = Counter()
    touched = []
    records = []
    with open(args.catalog, encoding="utf-8") as handle:
        for line in handle:
            record = json.loads(line)
            source = english.get(record["id"])
            before = record["translation"]
            if source is None or not before:
                records.append(record)
                continue
            text = before
            for name, function in (
                ("effects", lambda s, t: fix_effects(s, t, terms["effects"], terms["stat_abbreviations"])),
                ("numbered_lines", lambda s, t: fix_numbered_lines(s, t, terms["numbered_lines"])),
                ("accuracy_line", lambda s, t: fix_accuracy_line(s, t, terms["mechanic_names"], terms["accuracy_lead_in"])),
                ("numero", fix_numero),
                ("stat_abbreviations", lambda s, t: fix_stats(s, t, terms["stat_abbreviations"])),
                ("ui_labels", lambda s, t: fix_ui_label(s, t, terms["ui_labels"])),
                ("trailing_period", fix_trailing_period),
                # Last on purpose: an exact-string override is a deliberate decision about one label,
                # and the heuristics above have no business trimming it afterwards.
                ("overrides", lambda s, t: fix_override(s, t, terms["overrides"])),
            ):
                text, hits = function(source, text)
                if hits:
                    counts[name] += hits
            if text != before:
                counts["records"] += 1
                touched.append({"id": record["id"], "en": source, "before": before, "after": text})
                record["translation"] = text
            records.append(record)

    for name, value in sorted(counts.items()):
        print(f"{value:7d}  {name}")

    if args.report:
        with open(args.report, "w", encoding="utf-8") as handle:
            for row in touched:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        print(f"report: {args.report}")

    if args.apply:
        with open(args.catalog, "w", encoding="utf-8") as handle:
            for record in records:
                handle.write(json.dumps(record, ensure_ascii=False) + "\n")
        print(f"applied to {args.catalog}")
    else:
        print("dry run - nothing written (pass --apply)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
