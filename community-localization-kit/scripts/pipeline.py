#!/usr/bin/env python3
"""Offline community-localization pipeline for Invokers LOC1 data.

This wrapper never modifies an installed game. It delegates LOC1 parsing, job binding,
token/number/tag validation, composition, and round-trip verification to InvokersRu.Cli.
Original EN/base files and private jobs stay below this repository's ignored ``work/`` directory.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Any, Iterable


SCRIPT_PATH = Path(__file__).resolve()
KIT_ROOT = SCRIPT_PATH.parents[1]
REPOSITORY_ROOT = SCRIPT_PATH.parents[2]
WORK_ROOT = REPOSITORY_ROOT / "work"
DEFAULT_CLI = REPOSITORY_ROOT / "src" / "InvokersRu.Cli" / "bin" / "Release" / "net10.0" / "InvokersRu.Cli.dll"
SAFE_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{1,63}$")
SAFE_BCP47 = re.compile(r"^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$")
CONFIG_KEYS = {
    "schema", "pack_id", "target_language", "injection_slot", "catalog_policy",
    "fallback", "allow_per_locale_content_version",
}
TARGET_KEYS = {"name", "bcp47"}
SLOT_KEYS = {"locale", "file", "stamp_file", "locale_id"}


class PipelineError(RuntimeError):
    pass


def _no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise PipelineError(f"duplicate JSON member: {key}")
        value[key] = item
    return value


def read_json(path: Path, label: str) -> dict[str, Any]:
    regular_file(path, label)
    try:
        text = path.read_bytes().decode("utf-8", errors="strict")
        value = json.loads(text, object_pairs_hook=_no_duplicate_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PipelineError(f"{label} is not strict UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise PipelineError(f"{label} must contain one JSON object")
    return value


def write_new_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    try:
        with path.open("x", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    except FileExistsError as exc:
        raise PipelineError(f"refusing to overwrite existing output: {path}") from exc


def regular_file(path: Path, label: str) -> Path:
    candidate = path.expanduser()
    if candidate.is_symlink() or not candidate.is_file():
        raise PipelineError(f"{label} must be an existing regular file, not a symlink: {candidate}")
    if candidate.stat().st_size <= 0:
        raise PipelineError(f"{label} is empty: {candidate}")
    return candidate.resolve()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def read_json_snapshot(path: Path, label: str) -> tuple[dict[str, Any], str]:
    resolved = regular_file(path, label)
    raw = resolved.read_bytes()
    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(text, object_pairs_hook=_no_duplicate_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PipelineError(f"{label} is not strict UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise PipelineError(f"{label} must contain one JSON object")
    return value, sha256_bytes(raw)


def _canonical_work_root() -> Path:
    root = Path(os.path.abspath(WORK_ROOT))
    if root.exists() or root.is_symlink():
        if root.is_symlink() or not root.is_dir():
            raise PipelineError(f"repository work root must be a regular directory, not a link: {root}")
    else:
        root.mkdir(exist_ok=False)
    resolved = root.resolve()
    if resolved != root:
        raise PipelineError(f"repository work root resolves through a link or junction: {root} -> {resolved}")
    return resolved


def _prepare_private_output(path: Path, label: str) -> tuple[Path, Path]:
    root = _canonical_work_root()
    candidate = Path(os.path.abspath(path.expanduser()))
    try:
        relative = candidate.relative_to(root)
    except ValueError as exc:
        raise PipelineError(f"{label} must stay below the repository work directory: {root}") from exc
    if not relative.parts:
        raise PipelineError(f"{label} must not be the work root itself")
    if candidate.exists() or candidate.is_symlink():
        raise PipelineError(f"refusing to overwrite existing {label}: {candidate}")

    current = root
    for component in relative.parts[:-1]:
        current = current / component
        if current.exists() or current.is_symlink():
            if current.is_symlink() or not current.is_dir():
                raise PipelineError(f"{label} parent contains a link or non-directory: {current}")
        else:
            current.mkdir(exist_ok=False)
        resolved = current.resolve()
        try:
            resolved.relative_to(root)
        except ValueError as exc:
            raise PipelineError(f"{label} parent escapes work through a link or junction: {current}") from exc
    return candidate, root


def ensure_new_workspace(path: Path) -> Path:
    candidate, root = _prepare_private_output(path, "private workspace")
    candidate.mkdir(exist_ok=False)
    resolved = candidate.resolve()
    try:
        resolved.relative_to(root)
    except ValueError as exc:
        raise PipelineError(f"created workspace escapes work through a link or junction: {candidate}") from exc
    return resolved


def ensure_new_private_file(path: Path, label: str) -> Path:
    candidate, _ = _prepare_private_output(path, label)
    return candidate


def require_exact_keys(value: dict[str, Any], expected: set[str], label: str) -> None:
    missing = expected - set(value)
    unknown = set(value) - expected
    if missing or unknown:
        raise PipelineError(
            f"{label} members differ from the schema; missing={sorted(missing)}, unknown={sorted(unknown)}"
        )


def validate_config(value: dict[str, Any]) -> dict[str, Any]:
    require_exact_keys(value, CONFIG_KEYS, "language config")
    if value["schema"] != 1:
        raise PipelineError("language config schema must be 1")
    pack_id = value["pack_id"]
    if not isinstance(pack_id, str) or not SAFE_ID.fullmatch(pack_id):
        raise PipelineError("pack_id must be 2-64 lowercase ASCII letters, digits, dots, underscores, or hyphens")
    target = value["target_language"]
    slot = value["injection_slot"]
    if not isinstance(target, dict) or not isinstance(slot, dict):
        raise PipelineError("target_language and injection_slot must be JSON objects")
    require_exact_keys(target, TARGET_KEYS, "target_language")
    require_exact_keys(slot, SLOT_KEYS, "injection_slot")
    target_name = target["name"]
    if (not isinstance(target_name, str) or not (1 <= len(target_name) <= 80)
            or target_name != target_name.strip()
            or any(ord(character) < 32 or ord(character) == 127 for character in target_name)):
        raise PipelineError("target_language.name must be a trimmed, printable name of 1-80 characters")
    if not isinstance(target["bcp47"], str) or not SAFE_BCP47.fullmatch(target["bcp47"]):
        raise PipelineError("target_language.bcp47 is not a conservative BCP 47 tag")
    # Today the audited cache mutation boundary supports one existing Cyrillic slot only.
    # A different slot is not made safe by changing a filename in JSON.
    expected_slot = {
        "locale": "uk_UA",
        "file": "dl_uk_UA.bin",
        "stamp_file": "dl_uk_UA.bin.ver",
        "locale_id": 8,
    }
    if slot != expected_slot:
        raise PipelineError(
            "this kit currently supports only the existing uk_UA slot (locale id 8); "
            "another slot needs code-level path/schema certification"
        )
    if value["catalog_policy"] not in ("preview-drafts", "release-approved"):
        raise PipelineError("catalog_policy must be preview-drafts or release-approved")
    if value["fallback"] != "english":
        raise PipelineError("fallback must be english; missing or stale translations fail closed to English")
    if not isinstance(value["allow_per_locale_content_version"], bool):
        raise PipelineError("allow_per_locale_content_version must be a boolean")
    return value


def resolve_dotnet(explicit: str | None) -> Path:
    if explicit:
        return regular_file(Path(explicit), ".NET host")
    bundled = REPOSITORY_ROOT / "work" / "dotnet-10" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    if bundled.is_file() and not bundled.is_symlink():
        return bundled.resolve()
    found = shutil.which("dotnet")
    if not found:
        raise PipelineError(".NET 10 SDK host not found; pass --dotnet or install the SDK pinned by global.json")
    return Path(found).resolve()


def resolve_cli(explicit: str | None) -> Path:
    path = Path(explicit) if explicit else DEFAULT_CLI
    try:
        return regular_file(path, "InvokersRu CLI")
    except PipelineError as exc:
        if explicit:
            raise
        raise PipelineError(
            f"{exc}. Build it first: dotnet build src/InvokersRu.Cli/InvokersRu.Cli.csproj -c Release"
        ) from exc


def run_cli(dotnet: Path, cli: Path, arguments: Iterable[str], *, expect_json: bool = False) -> Any:
    command = [str(dotnet), str(cli), *map(str, arguments)]
    environment = os.environ.copy()
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    completed = subprocess.run(
        command,
        cwd=REPOSITORY_ROOT,
        env=environment,
        text=True,
        encoding="utf-8",
        errors="strict",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout).strip()
        raise PipelineError(f"InvokersRu.Cli failed ({completed.returncode}): {detail}")
    if expect_json:
        try:
            value = json.loads(completed.stdout, object_pairs_hook=_no_duplicate_object)
        except json.JSONDecodeError as exc:
            raise PipelineError(f"CLI returned invalid JSON: {exc}") from exc
        if not isinstance(value, dict):
            raise PipelineError("CLI JSON response must be an object")
        return value
    if completed.stdout.strip():
        print(completed.stdout.rstrip())
    return None


def validate_source_tuple(config: dict[str, Any], english_info: dict[str, Any], base_info: dict[str, Any]) -> None:
    if english_info.get("schema") != base_info.get("schema"):
        raise PipelineError("EN and base LOC1 schemas differ")
    if english_info.get("locale_id") != 1:
        raise PipelineError("the source file is not the en_US locale slot (locale id 1)")
    if base_info.get("locale_id") != config["injection_slot"]["locale_id"]:
        raise PipelineError("the base file does not match the configured existing locale slot")
    if english_info.get("content_guid") != base_info.get("content_guid"):
        raise PipelineError("EN and base LOC1 content GUIDs differ")
    if english_info.get("entries") != base_info.get("entries"):
        raise PipelineError("EN and base LOC1 entry counts differ")
    if (not config["allow_per_locale_content_version"]
            and english_info.get("content_version") != base_info.get("content_version")):
        raise PipelineError("EN and base content versions differ and the config does not allow that")


def command_prepare(args: argparse.Namespace) -> None:
    config_path = regular_file(Path(args.config), "language config")
    config = validate_config(read_json(config_path, "language config"))
    english = regular_file(Path(args.english), "English LOC1")
    base = regular_file(Path(args.base), "base-slot LOC1")
    stamp = regular_file(Path(args.stamp), "base-slot version stamp")
    if len({english, base, stamp}) != 3:
        raise PipelineError("English LOC1, base LOC1, and stamp must be different files")
    existing = regular_file(Path(args.existing_catalog), "existing catalog") if args.existing_catalog else None
    dotnet = resolve_dotnet(args.dotnet)
    cli = resolve_cli(args.cli)
    workspace = ensure_new_workspace(Path(args.workspace))

    english_info = run_cli(dotnet, cli, ["inspect", str(english)], expect_json=True)
    base_info = run_cli(dotnet, cli, ["inspect", str(base)], expect_json=True)
    validate_source_tuple(config, english_info, base_info)
    run_cli(dotnet, cli, ["roundtrip", str(english)])
    run_cli(dotnet, cli, ["roundtrip", str(base)])

    try:
        stamp_text = stamp.read_bytes().decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise PipelineError(f"version stamp is not strict UTF-8: {exc}") from exc
    if not (1 <= len(stamp_text) <= 64) or any(character.isspace() or ord(character) < 32 for character in stamp_text):
        raise PipelineError("version stamp must be one bare 1-64 character value")
    safe_stamp = re.sub(r"[^A-Za-z0-9._-]", "-", stamp_text)
    profile_id = f"community-{config['pack_id']}-{safe_stamp}"[:128]

    profile_path = workspace / "source-profile.json"
    run_cli(dotnet, cli, [
        "cache-profile", "--cache-root", str(workspace),
        "--english", str(english), "--base", str(base), "--stamp", str(stamp),
        "--id", profile_id, "--output", str(profile_path),
    ])
    jobs_path = workspace / "private.jobs.jsonl"
    job_arguments = [
        "jobs", "--english", str(english), "--ukrainian", str(base),
        "--output", str(jobs_path), "--max-chars", str(args.max_chars),
    ]
    if existing:
        job_arguments.extend(["--translations", str(existing)])
    run_cli(dotnet, cli, job_arguments)

    receipt = {
        "schema": 1,
        "kind": "community-localization-prepare",
        "pack_id": config["pack_id"],
        "target_language": config["target_language"],
        "injection_slot": config["injection_slot"],
        "game_version": stamp_text,
        "english": {
            "sha256": sha256_file(english),
            "raw_sha256": english_info["raw_sha256"],
            "schema": english_info["schema"],
            "locale_id": english_info["locale_id"],
            "entries": english_info["entries"],
            "content_guid": english_info["content_guid"],
            "content_version": english_info["content_version"],
        },
        "base": {
            "sha256": sha256_file(base),
            "raw_sha256": base_info["raw_sha256"],
            "schema": base_info["schema"],
            "locale_id": base_info["locale_id"],
            "entries": base_info["entries"],
            "content_guid": base_info["content_guid"],
            "content_version": base_info["content_version"],
        },
        "stamp_sha256": sha256_file(stamp),
        "source_profile_sha256": sha256_file(profile_path),
        "private_jobs_sha256": sha256_file(jobs_path),
        "private_work_items": True,
    }
    write_new_json(workspace / "prepare-receipt.json", receipt)
    print(f"Prepared private translation workspace: {workspace}")
    print("Do not commit private.jobs.jsonl or original game files.")


def validation_arguments(config: dict[str, Any], english: Path, base: Path, catalog: Path) -> list[str]:
    arguments = [
        "validate", "--english", str(english), "--ukrainian", str(base),
        "--translations", str(catalog),
    ]
    if config["catalog_policy"] == "preview-drafts":
        arguments.extend(["--profile", "preview", "--include-draft"])
    else:
        arguments.extend(["--profile", "release"])
    if config["allow_per_locale_content_version"]:
        arguments.append("--per-locale-content-version")
    return arguments


def command_import(args: argparse.Namespace) -> None:
    config = validate_config(read_json(Path(args.config), "language config"))
    english = regular_file(Path(args.english), "English LOC1")
    base = regular_file(Path(args.base), "base-slot LOC1")
    jobs = regular_file(Path(args.jobs), "private jobs")
    results = regular_file(Path(args.results), "model/manual results")
    existing = regular_file(Path(args.existing_catalog), "existing catalog") if args.existing_catalog else None
    output = ensure_new_private_file(Path(args.output_catalog), "catalog output")
    dotnet = resolve_dotnet(args.dotnet)
    cli = resolve_cli(args.cli)
    import_arguments = [
        "import-results", "--english", str(english), "--jobs", str(jobs),
        "--results", str(results), "--output", str(output.resolve()),
    ]
    if existing:
        import_arguments.extend(["--translations", str(existing)])
    if args.allow_partial:
        import_arguments.append("--allow-partial")
    run_cli(dotnet, cli, import_arguments)
    run_cli(dotnet, cli, validation_arguments(config, english, base, output.resolve()))
    print(f"Imported catalog verified: {output.resolve()}")
    print("The CLI verified job/source hashes, protected tokens, numbers, units, tags, newlines, and UTF-8/NFC rules.")


def certify_profile(
    config: dict[str, Any], profile: dict[str, Any], report: dict[str, Any],
    english: Path, base: Path, stamp: Path, catalog: Path, output: Path,
) -> dict[str, Any]:
    if profile.get("schema") != 1 or profile.get("english_locale_id") != 1 or profile.get("base_locale_id") != 8:
        raise PipelineError("source profile is not the supported exact EN/uk_UA tuple")
    if profile.get("certified") is not False or profile.get("readiness") != "blocked":
        raise PipelineError("source profile must be the blocked snapshot produced by cache-profile")
    if profile.get("english_sha256", "").upper() != sha256_file(english):
        raise PipelineError("source profile English hash does not match")
    if profile.get("base_sha256", "").upper() != sha256_file(base):
        raise PipelineError("source profile base hash does not match")
    if profile.get("stamp_sha256", "").upper() != sha256_file(stamp):
        raise PipelineError("source profile stamp hash does not match")
    if report.get("schema") != 1 or not isinstance(report.get("source"), dict):
        raise PipelineError("CLI build report schema is unsupported")
    source = report["source"]
    target = report.get("target")
    composition = report.get("composition")
    validation = report.get("validation")
    options = report.get("build_options")
    built = report.get("output")
    if not all(isinstance(value, dict) for value in (target, composition, validation, options, built)):
        raise PipelineError("CLI build report is incomplete")
    exact_sources = (
        source.get("english_container_sha256", "").upper() == sha256_file(english)
        and source.get("base_container_sha256", "").upper() == sha256_file(base)
        and source.get("translations_sha256", "").upper() == sha256_file(catalog)
        and target.get("locale_id") == profile["base_locale_id"]
        and target.get("entries") == profile["entry_count"]
    )
    if not exact_sources:
        raise PipelineError("CLI build report does not match the exact profile/source/catalog tuple")
    if validation.get("errors") != 0 or options.get("container") != "raw":
        raise PipelineError("build must have zero validation errors and a raw LOC1 output")
    preview = config["catalog_policy"] == "preview-drafts"
    if bool(options.get("release")) == preview or bool(options.get("include_draft")) != preview:
        raise PipelineError("CLI build policy does not match language config")
    if bool(options.get("per_locale_content_version")) != config["allow_per_locale_content_version"]:
        raise PipelineError("CLI per-locale content-version option does not match language config")
    if built.get("container_sha256", "").upper() != sha256_file(output):
        raise PipelineError("built LOC1 file does not match its report")
    if built.get("raw_sha256", "").upper() != sha256_file(output):
        raise PipelineError("raw LOC1 hash does not match its report")
    count_names = ("applied_ru", "english_fallback", "base_fallback", "needs_review_fallback")
    if any(not isinstance(composition.get(name), int) or composition[name] < 0 for name in count_names):
        raise PipelineError("CLI composition counts are invalid")
    if composition["applied_ru"] <= 0:
        raise PipelineError("a certified community profile must apply at least one translation")
    if composition["applied_ru"] + composition["english_fallback"] + composition["base_fallback"] != profile["entry_count"]:
        raise PipelineError("CLI composition does not partition the exact LOC1 entry count")

    certified = dict(profile)
    certified.update({
        "readiness": "ready",
        "certified": True,
        "blocked_reason": None,
        "translation_catalog_sha256": sha256_file(catalog),
        "expected_output_sha256": sha256_file(output),
        "minimum_applied_translations": composition["applied_ru"],
        "expected_applied_translations": composition["applied_ru"],
        "expected_english_fallbacks": composition["english_fallback"],
        "expected_base_fallbacks": composition["base_fallback"],
        "expected_needs_review_fallbacks": composition["needs_review_fallback"],
        "translation_policy": "community-preview-all-drafts" if preview else "release-approved",
    })
    return certified


def command_build(args: argparse.Namespace) -> None:
    config_value, config_sha256 = read_json_snapshot(Path(args.config), "language config")
    config = validate_config(config_value)
    profile = read_json(Path(args.source_profile), "blocked source profile")
    english = regular_file(Path(args.english), "English LOC1")
    base = regular_file(Path(args.base), "base-slot LOC1")
    stamp = regular_file(Path(args.stamp), "base-slot version stamp")
    catalog = regular_file(Path(args.catalog), "translation catalog")
    output_dir = ensure_new_workspace(Path(args.output_directory))
    dotnet = resolve_dotnet(args.dotnet)
    cli = resolve_cli(args.cli)
    run_cli(dotnet, cli, validation_arguments(config, english, base, catalog))

    output = output_dir / f"{config['pack_id']}.loc1.bin"
    report_path = output_dir / "build-report.json"
    build_arguments = [
        "build", "--english", str(english), "--base", str(base),
        "--translations", str(catalog), "--output", str(output),
        "--report", str(report_path), "--raw",
    ]
    if config["catalog_policy"] == "preview-drafts":
        build_arguments.append("--include-draft")
    else:
        build_arguments.append("--release")
    if config["allow_per_locale_content_version"]:
        build_arguments.append("--per-locale-content-version")
    run_cli(dotnet, cli, build_arguments)
    report = read_json(report_path, "CLI build report")
    certified = certify_profile(config, profile, report, english, base, stamp, catalog, output)
    certified_path = output_dir / "certified-runtime-profile.json"
    write_new_json(certified_path, certified)
    receipt = {
        "schema": 2,
        "kind": "community-localization-exact-build",
        "pack_id": config["pack_id"],
        "target_language": config["target_language"],
        "injection_slot": config["injection_slot"],
        "catalog_policy": config["catalog_policy"],
        "fallback": config["fallback"],
        "allow_per_locale_content_version": config["allow_per_locale_content_version"],
        "language_config_sha256": config_sha256,
        "profile_id": certified["id"],
        "game_version": certified["game_version"],
        "catalog_sha256": sha256_file(catalog),
        "output_raw_sha256": sha256_file(output),
        "profile_sha256": sha256_file(certified_path),
        "entry_count": certified["entry_count"],
        "applied_translations": certified["expected_applied_translations"],
        "english_fallbacks": certified["expected_english_fallbacks"],
        "base_fallbacks": certified["expected_base_fallbacks"],
        "needs_review_fallbacks": certified["expected_needs_review_fallbacks"],
        "policy": certified["translation_policy"],
        "officially_signed": False,
    }
    write_new_json(output_dir / "build-receipt.json", receipt)
    print(f"Exact LOC1 and self-certified local profile built in: {output_dir}")
    print("This is a community build receipt, not an official project signature or developer certification.")


def command_self_test(_: argparse.Namespace) -> None:
    sample = validate_config(read_json(KIT_ROOT / "templates" / "language-config.example.json", "sample config"))
    if sample["fallback"] != "english":
        raise PipelineError("sample fallback invariant failed")
    invalid = json.loads(json.dumps(sample))
    invalid["injection_slot"]["file"] = "dl_fr_FR.bin"
    try:
        validate_config(invalid)
    except PipelineError:
        pass
    else:
        raise PipelineError("unsupported injection slot was not rejected")
    invalid_name = json.loads(json.dumps(sample))
    invalid_name["target_language"]["name"] = "French\nInjected"
    try:
        validate_config(invalid_name)
    except PipelineError:
        pass
    else:
        raise PipelineError("control characters in the target language name were not rejected")
    schema = read_json(KIT_ROOT / "templates" / "model-result.schema.json", "result schema")
    required = set(schema.get("required", []))
    if required != {"job_id", "translation", "model", "prompt_version", "confidence", "needs_review", "issue_codes"}:
        raise PipelineError("result schema required members drifted")
    job_id_pattern = schema.get("properties", {}).get("job_id", {}).get("pattern")
    if job_id_pattern != r"^ru-[a-f0-9]{24}$":
        raise PipelineError("result schema job_id pattern drifted from the production exporter")
    for unsafe_output in (
        REPOSITORY_ROOT / "translations" / "community-private-output.jsonl",
        WORK_ROOT / ".." / "translations" / "community-private-output.jsonl",
    ):
        try:
            ensure_new_private_file(unsafe_output, "self-test output")
        except PipelineError:
            pass
        else:
            raise PipelineError(f"private output outside repository work was not rejected: {unsafe_output}")
    for relative in ("prompts/translation.md", "prompts/review.md", "templates/style-guide.example.md"):
        text = regular_file(KIT_ROOT / relative, relative).read_bytes().decode("utf-8", errors="strict")
        if "{{TARGET_LANGUAGE_NAME}}" not in text:
            raise PipelineError(f"generic language variable missing from {relative}")
    print("Community localization kit self-test: PASS")


def add_common_tool_options(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--dotnet", help="path to a .NET 10 dotnet host")
    parser.add_argument("--cli", help="path to a built InvokersRu.Cli.dll")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Offline, fail-closed community localization pipeline; never writes to the game."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    prepare = subparsers.add_parser("prepare", help="verify private LOC1 inputs and export deterministic jobs")
    prepare.add_argument("--config", required=True)
    prepare.add_argument("--english", required=True)
    prepare.add_argument("--base", required=True)
    prepare.add_argument("--stamp", required=True)
    prepare.add_argument("--workspace", required=True)
    prepare.add_argument("--existing-catalog")
    prepare.add_argument("--max-chars", type=int, default=8000, choices=range(100, 100001), metavar="100..100000")
    add_common_tool_options(prepare)
    prepare.set_defaults(handler=command_prepare)

    importer = subparsers.add_parser("import-results", help="bind result JSONL and validate source/tokens/numbers/tags")
    importer.add_argument("--config", required=True)
    importer.add_argument("--english", required=True)
    importer.add_argument("--base", required=True)
    importer.add_argument("--jobs", required=True)
    importer.add_argument("--results", required=True)
    importer.add_argument("--existing-catalog")
    importer.add_argument("--output-catalog", required=True)
    importer.add_argument("--allow-partial", action="store_true")
    add_common_tool_options(importer)
    importer.set_defaults(handler=command_import)

    builder = subparsers.add_parser("build", help="validate, compose exact raw LOC1, and pin a local compatibility profile")
    builder.add_argument("--config", required=True)
    builder.add_argument("--source-profile", required=True)
    builder.add_argument("--english", required=True)
    builder.add_argument("--base", required=True)
    builder.add_argument("--stamp", required=True)
    builder.add_argument("--catalog", required=True)
    builder.add_argument("--output-directory", required=True)
    add_common_tool_options(builder)
    builder.set_defaults(handler=command_build)

    self_test = subparsers.add_parser("self-test", help="validate checked-in templates without game data")
    self_test.set_defaults(handler=command_self_test)
    return parser


def main() -> int:
    try:
        arguments = build_parser().parse_args()
        arguments.handler(arguments)
        return 0
    except (PipelineError, OSError, subprocess.SubprocessError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
