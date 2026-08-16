# Translation catalogs

`ru_RU.jsonl` is the catalog used by the last supervised runtime test. Its exact
SHA-256 is pinned by `config/runtime-cache-profile.0.60.1239.json`; do not change
it without rebuilding and testing a new runtime package.

`ru_RU.next.jsonl` is the source-free working catalog for the full translation.
Machine-produced waves are imported here as `draft`. A Git merge does not make
them reviewed, approved, or runtime-certified.

Wave 1 imported 684 new IDs and grew the working catalog from 1,842 to 2,526
draft records. Its source-free receipt is stored under `translations/waves/`.
The current validator selects 994 records for a local preview build, but that
build has not been installed or runtime-certified.

After reclassifying account/auth and English/Ukrainian context risks, the
remaining private queue contains 24,467 deduplicated jobs for 38,602 IDs. The
101 sensitive IDs stay on the official English fallback until separate human
review. Records with pre-guard risk metadata are fail-closed and re-queued;
they are not silently promoted by this wave.

Private English/Ukrainian source packages, jobs, model result shards, screenshots,
and generated LOC1 files stay under ignored `work/` paths and must never be
committed. Public catalogs contain only hashed string IDs, source/context hashes,
Russian text, and QA metadata.
