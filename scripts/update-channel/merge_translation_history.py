#!/usr/bin/env python3
"""Carry authentic historical record pairs into the next Windows data catalog.

This producer does NOT verify signatures. Before calling it, independently verify
the previous signed envelope/catalog and pass its authenticated uncompressed
SHA-256. The output still requires production validation, exact-profile rebuilds
and signing; preserving history alone does not certify a game-file combination.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import re
import stat
from datetime import datetime
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
MAX_BYTES = 128 * 1024 * 1024
MAX_LINE_BYTES = 4 * 1024 * 1024
MAX_RECORDS = 100_000
MAX_VARIANTS_PER_ID = 16
MAX_HISTORICAL_VARIANTS = 100_000
FIELDS = {
    "id", "source_sha256", "hint_sha256", "translation", "status", "model",
    "prompt_version", "confidence", "needs_review", "issue_codes", "risk_flags",
    "review_stage", "reviewer_ids", "reviewed_at", "review_revision",
    "screenshot_qa", "legal_approved", "updated_at", "notes", "variants",
}
OPTIONAL_STRINGS = {
    "model", "prompt_version", "confidence", "review_stage", "review_revision", "notes",
}


class CatalogError(ValueError):
    pass


def strict_object(pairs):
    result = {}
    seen = set()
    for key, value in pairs:
        folded = key.casefold()
        if folded in seen:
            raise CatalogError(f"Duplicate JSON property: {key}")
        seen.add(folded)
        result[key] = value
    return result


def fail_constant(value):
    raise CatalogError(f"Invalid JSON constant: {value}")


def assert_regular_path(path: Path, label: str):
    for component in (path, *path.parents):
        try:
            info = component.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise CatalogError(f"{label} cannot use symlinks or reparse points: {component}")


def validate_string(value, name, nullable=False):
    if nullable and value is None:
        return
    if not isinstance(value, str):
        raise CatalogError(f"{name} must be a string" + (" or null" if nullable else ""))
    try:
        value.encode("utf-8", errors="strict")
    except UnicodeEncodeError as error:
        raise CatalogError(f"{name} contains an invalid Unicode surrogate") from error


def validate_record(record, expected_id=None):
    if not isinstance(record, dict) or set(record) - FIELDS:
        raise CatalogError("Record must be an object with only known TranslationRecord fields")
    identifier = record.get("id")
    if not isinstance(identifier, str) or re.fullmatch(r"[0-9A-Fa-f]{16}", identifier) is None:
        raise CatalogError("id must contain exactly 16 hexadecimal digits")
    identifier = identifier.upper()
    if expected_id is not None and identifier != expected_id:
        raise CatalogError("Historical variant id differs from its parent")
    record["id"] = identifier
    source = record.get("source_sha256")
    hint = record.get("hint_sha256")
    for name, value in (("source_sha256", source), ("hint_sha256", hint)):
        if name == "hint_sha256" and value is None:
            continue
        if not isinstance(value, str) or re.fullmatch(r"[0-9A-Fa-f]{64}", value) is None:
            raise CatalogError(f"{name} must contain exactly 64 hexadecimal digits")
        record[name] = value.upper()
    validate_string(record.get("translation"), "translation")
    status_value = record.get("status")
    if not isinstance(status_value, str) or status_value.lower() not in {"draft", "reviewed", "approved"}:
        raise CatalogError("status must be draft, reviewed or approved")
    for name in OPTIONAL_STRINGS & record.keys():
        validate_string(record[name], name, nullable=True)
    if record.get("confidence") is not None and record["confidence"] not in {"high", "medium", "low"}:
        raise CatalogError("confidence must be high, medium, low or null")
    for name in ("needs_review", "screenshot_qa", "legal_approved"):
        if name in record and type(record[name]) is not bool:
            raise CatalogError(f"{name} must be boolean")
    for name in ("issue_codes", "risk_flags", "reviewer_ids"):
        if name in record:
            if not isinstance(record[name], list):
                raise CatalogError(f"{name} must be an array of strings")
            for value in record[name]:
                validate_string(value, name)
    for name in ("updated_at", "reviewed_at"):
        if name in record and record[name] is not None:
            validate_string(record[name], name)
            timestamp = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})", record[name])
            if timestamp is None:
                raise CatalogError(f"{name} must be an ISO timestamp with a timezone")
            # Python 3.10 accepts only 3/6 fractional digits, while the .NET
            # catalog legitimately has 1..7. Normalize only for validation;
            # preserve the original provenance string in the output.
            fraction = timestamp.group(2)
            normalized = timestamp.group(1) + ("." + fraction.ljust(6, "0")[:6] if fraction else "") + timestamp.group(3).replace("Z", "+00:00")
            try:
                parsed = datetime.fromisoformat(normalized)
            except ValueError as error:
                raise CatalogError(f"{name} must be an ISO timestamp") from error
            if parsed.utcoffset() is None:
                raise CatalogError(f"{name} must include a timezone")
        elif name == "updated_at" and name in record:
            raise CatalogError("updated_at cannot be null")
    return identifier


def record_pair(record):
    return record["source_sha256"].upper(), (record.get("hint_sha256") or "-").upper()


def read_catalog(path: Path, *, plain: bool):
    path = Path(os.path.abspath(path))
    assert_regular_path(path, "Catalog input")
    records = {}
    historical_count = 0
    byte_count = 0
    digest = hashlib.sha256()
    with path.open("rb") as source:
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
            raise CatalogError("Catalog input must be a regular file")
        if os.fstat(source.fileno()).st_size > MAX_BYTES:
            raise CatalogError("Catalog input exceeds the byte limit")
        line_number = 0
        while True:
            raw = source.readline(MAX_LINE_BYTES + 1)
            if not raw:
                break
            line_number += 1
            byte_count += len(raw)
            if len(raw) > MAX_LINE_BYTES or byte_count > MAX_BYTES:
                raise CatalogError("Catalog input exceeds a byte limit")
            digest.update(raw)
            try:
                line = raw.decode("utf-8", errors="strict")
                if line_number == 1 and line.startswith("\ufeff"):
                    raise CatalogError("Catalog input must not contain a UTF-8 BOM")
                if not line.strip():
                    continue
                record = json.loads(line, object_pairs_hook=strict_object, parse_constant=fail_constant)
            except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as error:
                raise CatalogError(f"Invalid UTF-8/JSON at line {line_number}") from error
            identifier = validate_record(record)
            if identifier in records:
                raise CatalogError(f"Duplicate catalog id: {identifier}")
            if plain and "variants" in record:
                raise CatalogError("Current catalog must be plain and omit variants")
            variants = record.get("variants", [])
            if not isinstance(variants, list) or len(variants) > MAX_VARIANTS_PER_ID:
                raise CatalogError("Historical variants must be a bounded array")
            seen = {record_pair(record)}
            for variant in variants:
                if not isinstance(variant, dict) or "variants" in variant:
                    raise CatalogError("Null or nested historical variant")
                validate_record(variant, expected_id=identifier)
                pair = record_pair(variant)
                if pair in seen:
                    raise CatalogError("Duplicate source/context variant tuple")
                seen.add(pair)
            historical_count += len(variants)
            if historical_count > MAX_HISTORICAL_VARIANTS:
                raise CatalogError("Catalog exceeds the global historical variant limit")
            records[identifier] = record
            if len(records) > MAX_RECORDS:
                raise CatalogError("Catalog exceeds the record limit")
    if not records:
        raise CatalogError("Catalog must contain at least one record")
    return records, digest.hexdigest().upper()


def merge_records(current, previous):
    result = {}
    historical_count = 0
    for identifier in sorted(current.keys() | previous.keys()):
        primary = copy.deepcopy(current.get(identifier, previous.get(identifier)))
        primary.pop("variants", None)
        seen = {record_pair(primary)}
        historical = []
        if identifier in previous:
            prior = previous[identifier]
            # Copy complete existing pairs only. Never combine a source from one
            # row with a hint or Russian text from a different row.
            for candidate in [prior, *prior.get("variants", [])]:
                pair = record_pair(candidate)
                if pair in seen:
                    continue  # Current correction wins for an identical pair.
                variant = copy.deepcopy(candidate)
                variant.pop("variants", None)
                historical.append(variant)
                seen.add(pair)
        if len(historical) > MAX_VARIANTS_PER_ID:
            raise CatalogError(f"Merged history exceeds the per-id limit: {identifier}")
        historical_count += len(historical)
        if historical_count > MAX_HISTORICAL_VARIANTS:
            raise CatalogError("Merged history exceeds the global variant limit")
        if historical:
            primary["variants"] = historical
        result[identifier] = primary
        if len(result) > MAX_RECORDS:
            raise CatalogError("Merged catalog exceeds the record limit")
    return result, historical_count


def merge_catalogs(current_path, previous_path, output_path, previous_sha256):
    if re.fullmatch(r"[0-9A-Fa-f]{64}", previous_sha256) is None:
        raise CatalogError("Expected previous SHA-256 must contain 64 hexadecimal digits")
    current_path = Path(os.path.abspath(current_path))
    previous_path = Path(os.path.abspath(previous_path))
    output_path = Path(os.path.abspath(output_path))
    if any(":" in component for component in output_path.parts[1:]):
        raise CatalogError("Output cannot use an alternate data stream")
    work_root = REPOSITORY_ROOT / "work"
    assert_regular_path(output_path, "Catalog output")
    try:
        relative = output_path.relative_to(work_root)
    except ValueError as error:
        raise CatalogError("Output must be a new file under this repository's work directory") from error
    if relative == Path(".") or output_path in {current_path, previous_path} or output_path.exists():
        raise CatalogError("Output must be new and different from the inputs")
    current, current_hash = read_catalog(current_path, plain=True)
    previous, previous_hash = read_catalog(previous_path, plain=False)
    if previous_hash != previous_sha256.upper():
        raise CatalogError("Previous catalog differs from the independently authenticated SHA-256")
    merged, historical_count = merge_records(current, previous)
    output_lines = []
    output_size = 0
    output_digest = hashlib.sha256()
    for record in merged.values():
        raw = (json.dumps(record, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")
        output_size += len(raw)
        if len(raw) > MAX_LINE_BYTES or output_size > MAX_BYTES:
            raise CatalogError("Merged catalog exceeds a byte limit")
        output_lines.append(raw)
        output_digest.update(raw)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    assert_regular_path(output_path, "Catalog output")
    # All parsing/merge/size validation precedes CreateNew; no input is modified.
    with output_path.open("xb") as destination:
        for raw in output_lines:
            destination.write(raw)
        destination.flush()
        os.fsync(destination.fileno())
    return {
        "output": str(output_path), "output_sha256": output_digest.hexdigest().upper(),
        "output_bytes": output_size, "records": len(merged), "historical_variants": historical_count,
        "previous_only_ids_retained": len(previous.keys() - current.keys()),
        "current_sha256": current_hash, "previous_sha256": previous_hash,
        "signature_verified_by_this_script": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current", type=Path, required=True, help="Reviewed latest plain JSONL catalog")
    parser.add_argument("--previous-verified", type=Path, required=True, help="Previously authenticated enriched JSONL catalog")
    parser.add_argument("--previous-sha256", required=True, help="Uncompressed catalog hash from its independently verified signed manifest")
    parser.add_argument("--output", type=Path, required=True, help="New enriched JSONL file under repository work/")
    args = parser.parse_args()
    try:
        report = merge_catalogs(args.current, args.previous_verified, args.output, args.previous_sha256)
    except (CatalogError, OSError) as error:
        parser.exit(2, f"ERROR: {error}\n")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
