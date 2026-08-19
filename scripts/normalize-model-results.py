#!/usr/bin/env python3
"""Repairs the one systematic artifact bulk models produce on this corpus.

Compact mechanic units must survive byte-for-byte: `6m` stays `6m`, `9s` stays `9s`. Models
translating into Russian routinely transliterate the unit letter into a Cyrillic lookalike
(`6м`, `9с`), which the validator correctly rejects as mechanic-unit-mismatch.

A replacement is only ever made when the repaired token is a compact unit that actually appears
in the English source for that job, so this can restore the source spelling but never invent a
unit that was not there.

Usage: normalize-model-results.py <jobs.jsonl> <results.jsonl> <out.jsonl>
"""
import json
import re
import sys

UNIT_RE = re.compile(r"[-+]?\d+(?:[.,]\d+)?(?:ms|s|m|h|d|px|x|%)\b", re.IGNORECASE)
# Cyrillic letters models pick for each Latin unit suffix.
CYRILLIC_TO_LATIN = {
    "м": "m", "М": "m",
    "с": "s", "С": "s",
    "х": "x", "Х": "x",
    "ч": "h", "Ч": "h",
    "д": "d", "Д": "d",
    "мс": "ms", "МС": "ms", "Мс": "ms",
    "пкс": "px", "ПКС": "px",
}
# Longest first so "мс" wins over "м".
SUFFIXES = sorted(CYRILLIC_TO_LATIN, key=len, reverse=True)
CANDIDATE_RE = re.compile(
    r"([-+]?\d+(?:[.,]\d+)?)\s?(" + "|".join(re.escape(s) for s in SUFFIXES) + r")(?![\w])"
)


def repair(translation: str, source_units: set[str]) -> tuple[str, int]:
    fixes = 0

    def substitute(match: re.Match) -> str:
        nonlocal fixes
        number, cyrillic = match.group(1), match.group(2)
        candidate = number + CYRILLIC_TO_LATIN[cyrillic]
        if candidate.casefold() in source_units:
            fixes += 1
            return candidate
        return match.group(0)

    return CANDIDATE_RE.sub(substitute, translation), fixes


def main() -> int:
    jobs_path, results_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]

    units_by_job: dict[str, set[str]] = {}
    with open(jobs_path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            job = json.loads(line)
            units_by_job[job["job_id"]] = {
                match.group(0).casefold() for match in UNIT_RE.finditer(job["english"])
            }

    repaired_rows = 0
    repaired_tokens = 0
    total = 0
    with open(results_path, encoding="utf-8") as source, open(out_path, "w", encoding="utf-8") as out:
        for line in source:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            total += 1
            units = units_by_job.get(row["job_id"])
            if units:
                fixed, count = repair(row["translation"], units)
                if count:
                    row["translation"] = fixed
                    repaired_rows += 1
                    repaired_tokens += count
            out.write(json.dumps(row, ensure_ascii=False) + "\n")

    print(f"rows={total} repaired_rows={repaired_rows} repaired_units={repaired_tokens}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
