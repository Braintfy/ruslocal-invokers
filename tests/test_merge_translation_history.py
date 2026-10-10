"""Producer regression tests; synthetic files stay in a disposable work folder."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/update-channel/merge_translation_history.py"
SPEC = importlib.util.spec_from_file_location("merge_translation_history", SCRIPT)
MERGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MERGE)


def record(identifier="0000000000000001", source="A", hint="B", translation="Перевод"):
    return {
        "id": identifier, "source_sha256": source * 64, "hint_sha256": hint * 64,
        "translation": translation, "status": "draft", "needs_review": False,
        "model": "test-model", "confidence": "high", "issue_codes": [],
        "risk_flags": [], "reviewer_ids": ["test-reviewer"],
        "updated_at": "2026-10-10T12:00:00Z",
    }


class MergeHistoryTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "work").mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="history-merge-test-", dir=ROOT / "work")
        self.folder = Path(self.temporary.name)
        self.counter = 0

    def tearDown(self):
        self.temporary.cleanup()

    def write(self, name, rows):
        path = self.folder / name
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows), encoding="utf-8")
        return path

    def merge(self, current, previous, **kwargs):
        self.counter += 1
        current_path = self.write(f"current-{self.counter}.jsonl", current)
        previous_path = self.write(f"previous-{self.counter}.jsonl", previous)
        output = self.folder / f"merged-{self.counter}.jsonl"
        digest = hashlib.sha256(previous_path.read_bytes()).hexdigest()
        result = MERGE.merge_catalogs(current_path, previous_path, output, kwargs.get("digest", digest))
        return result, [json.loads(line) for line in output.read_text(encoding="utf-8").split("\n") if line]

    def test_preserves_complete_pairs_without_cross_product(self):
        old = record(source="A", hint="B", translation="Старый текст")
        old["variants"] = [record(source="C", hint="D", translation="Другой старый текст")]
        new = record(source="E", hint="F", translation="Новый текст")
        report, rows = self.merge([new], [old])
        self.assertEqual(report["historical_variants"], 2)
        pairs = {MERGE.record_pair(row) for row in [rows[0], *rows[0]["variants"]]}
        self.assertEqual(pairs, {("A" * 64, "B" * 64), ("C" * 64, "D" * 64), ("E" * 64, "F" * 64)})
        self.assertEqual(rows[0]["variants"][0]["translation"], "Старый текст")
        self.assertEqual(rows[0]["variants"][1], old["variants"][0])
        self.assertFalse(report["signature_verified_by_this_script"])

    def test_current_correction_wins_identical_tuple(self):
        old = record(translation="Опечатка")
        old["variants"] = [record(source="C", hint="D", translation="Исторический текст")]
        new = record(translation="Исправленный текст")
        report, rows = self.merge([new], [old])
        self.assertEqual(report["historical_variants"], 1)
        self.assertEqual(rows[0]["translation"], "Исправленный текст")
        self.assertEqual(rows[0]["variants"], old["variants"])

    def test_previous_only_keys_survive_for_old_game_files(self):
        old = record(identifier="0000000000000002")
        old["variants"] = [record(identifier=old["id"], source="C", hint="D")]
        report, rows = self.merge([record()], [old])
        self.assertEqual(report["records"], 2)
        self.assertEqual(report["previous_only_ids_retained"], 1)
        self.assertEqual(rows[1], old)

    def test_duplicate_ids_and_case_insensitive_pairs_rejected(self):
        row = record(identifier="A000000000000001")
        duplicate = copy.deepcopy(row)
        duplicate["id"] = duplicate["id"].lower()
        with self.assertRaisesRegex(MERGE.CatalogError, "Duplicate catalog id"):
            self.merge([row], [row, duplicate])
        duplicate = copy.deepcopy(row)
        duplicate["source_sha256"] = duplicate["source_sha256"].lower()
        row["variants"] = [duplicate]
        with self.assertRaisesRegex(MERGE.CatalogError, "Duplicate source/context"):
            self.merge([record()], [row])

    def test_current_variants_nested_variants_and_foreign_ids_rejected(self):
        row = record()
        row["variants"] = []
        with self.assertRaisesRegex(MERGE.CatalogError, "Current catalog must be plain"):
            self.merge([row], [record()])
        old = record()
        variant = record(source="C", hint="D")
        variant["variants"] = None
        old["variants"] = [variant]
        with self.assertRaisesRegex(MERGE.CatalogError, "nested"):
            self.merge([record()], [old])
        old["variants"] = [record(identifier="0000000000000002", source="C", hint="D")]
        with self.assertRaisesRegex(MERGE.CatalogError, "differs from its parent"):
            self.merge([record()], [old])

    def test_duplicate_and_unknown_json_properties_rejected(self):
        for data in ('{"id":"0000000000000001","id":"0000000000000002"}\n', '{"id":"0000000000000001","ID":"0000000000000002"}\n'):
            path = self.folder / "duplicate.jsonl"
            path.write_text(data, encoding="utf-8")
            with self.assertRaisesRegex(MERGE.CatalogError, "Duplicate JSON property"):
                MERGE.read_catalog(path, plain=False)
        row = record()
        row["extra_field"] = "Unexpected"
        with self.assertRaisesRegex(MERGE.CatalogError, "only known"):
            self.merge([record()], [row])

    def test_merged_per_id_global_and_record_limits(self):
        old = record(source="A", hint="B")
        old["variants"] = [record(source="C", hint="D")]
        new = record(source="E", hint="F")
        with patch.object(MERGE, "MAX_VARIANTS_PER_ID", 1):
            with self.assertRaisesRegex(MERGE.CatalogError, "per-id limit"):
                self.merge([new], [old])
        with patch.object(MERGE, "MAX_HISTORICAL_VARIANTS", 1):
            with self.assertRaisesRegex(MERGE.CatalogError, "global variant limit"):
                self.merge([new], [old])
        with patch.object(MERGE, "MAX_RECORDS", 1):
            with self.assertRaisesRegex(MERGE.CatalogError, "record limit"):
                self.merge([new], [record(identifier="0000000000000002")])

    def test_input_variant_limits(self):
        old = record()
        old["variants"] = [record(source="C", hint="D"), record(source="E", hint="F")]
        with patch.object(MERGE, "MAX_VARIANTS_PER_ID", 1):
            with self.assertRaisesRegex(MERGE.CatalogError, "bounded array"):
                self.merge([record()], [old])
        with patch.object(MERGE, "MAX_HISTORICAL_VARIANTS", 1):
            with self.assertRaisesRegex(MERGE.CatalogError, "global historical"):
                self.merge([record()], [old])

    def test_input_and_output_byte_limits(self):
        with patch.object(MERGE, "MAX_BYTES", 10):
            with self.assertRaisesRegex(MERGE.CatalogError, "byte limit"):
                self.merge([record()], [record()])
        with patch.object(MERGE, "MAX_LINE_BYTES", 10):
            with self.assertRaisesRegex(MERGE.CatalogError, "byte limit"):
                self.merge([record()], [record()])
        new = record(source="C", hint="D", translation="Новый" * 100)
        old = record(translation="Старый" * 100)
        ceiling = max(len((json.dumps(row, ensure_ascii=False) + "\n").encode("utf-8")) for row in (new, old)) + 10
        with patch.object(MERGE, "MAX_BYTES", ceiling):
            with self.assertRaisesRegex(MERGE.CatalogError, "Merged catalog exceeds a byte limit"):
                self.merge([new], [old])
        self.assertFalse((self.folder / f"merged-{self.counter}.jsonl").exists())

    def test_previous_hash_pin_required(self):
        with self.assertRaisesRegex(MERGE.CatalogError, "authenticated SHA-256"):
            self.merge([record()], [record()], digest="F" * 64)
        self.assertFalse((self.folder / "merged-1.jsonl").exists())

    def test_create_new_and_output_boundary(self):
        current = self.write("current.jsonl", [record()])
        previous = self.write("previous.jsonl", [record()])
        digest = hashlib.sha256(previous.read_bytes()).hexdigest()
        existing = self.folder / "existing.jsonl"
        existing.write_bytes(b"KEEP")
        for output in (existing, current, ROOT / "translations/should-not-be-created.jsonl", self.folder / "existing.jsonl:stream"):
            with self.assertRaises(MERGE.CatalogError):
                MERGE.merge_catalogs(current, previous, output, digest)
        self.assertEqual(existing.read_bytes(), b"KEEP")
        self.assertFalse((ROOT / "translations/should-not-be-created.jsonl").exists())

    def test_dotnet_fractional_timestamps_preserve_original_provenance(self):
        for digits in range(1, 8):
            row = record()
            row["updated_at"] = "2026-10-10T12:00:00." + "1" * digits + "+00:00"
            _, rows = self.merge([row], [record()])
            self.assertEqual(rows[0]["updated_at"], row["updated_at"])

    def test_invalid_utf8_bom_nonfinite_and_null_types(self):
        for data in (b"\xff\n", b"\xef\xbb\xbf{}\n", b'{"id":NaN}\n'):
            path = self.folder / "invalid.jsonl"
            path.write_bytes(data)
            with self.assertRaises(MERGE.CatalogError):
                MERGE.read_catalog(path, plain=False)
        for name, value in (("source_sha256", None), ("translation", None), ("status", None), ("issue_codes", None), ("needs_review", 1), ("confidence", []), ("updated_at", "2026-10-10")):
            row = record()
            row[name] = value
            with self.assertRaises(MERGE.CatalogError, msg=name):
                self.merge([record()], [row])

    def test_actual_data14_retains_all_67_variants_if_private_fixture_exists(self):
        current = ROOT / "work/update-2026-10-10/ru_RU.plain14.jsonl"
        previous = ROOT / "work/update-2026-10-10/ru_RU.enriched14.jsonl"
        if not current.exists() or not previous.exists():
            self.skipTest("Private data14 producer evidence is not part of the repository")
        output = self.folder / "actual-data14-merged.jsonl"
        digest = hashlib.sha256(previous.read_bytes()).hexdigest()
        report = MERGE.merge_catalogs(current, previous, output, digest)
        before, _ = MERGE.read_catalog(previous, plain=False)
        after, _ = MERGE.read_catalog(output, plain=False)
        self.assertEqual(report["records"], 42767)
        self.assertEqual(report["historical_variants"], 67)
        self.assertEqual(before, after)


if __name__ == "__main__":
    unittest.main()
