# Translation review prompt — generic target language

Prompt version: `community-review-v1`

## Variables

- `{{TARGET_LANGUAGE_NAME}}`
- `{{TARGET_LANGUAGE_BCP47}}`
- `{{STYLE_GUIDE}}`
- `{{GLOSSARY_JSON}}`
- `{{JOBS_JSONL}}`: authoritative source jobs
- `{{DRAFT_RESULTS_JSONL}}`: draft rows using `model-result.schema.json`

## Instructions

Review each draft in {{TARGET_LANGUAGE_NAME}} (`{{TARGET_LANGUAGE_BCP47}}`) against its job. English is authoritative; `ukrainian_hint` is context only. Correct meaning, terminology, grammar, register, consistency, and likely UI length without changing gameplay facts.

Return JSON Lines only, in job order, using exactly the same seven fields as the draft-result schema. Keep each `job_id` unchanged. Set `model` to the reviewer identity or tool name and `prompt_version` to `community-review-v1`.

The following are hard blockers, not stylistic suggestions:

- every protected token must match the source exactly;
- every literal number, percentage, compact mechanic unit, URL, email, placeholder, rich-text tag, escaped newline, and literal line break must be preserved;
- output must be strict UTF-8, Unicode NFC, and contain no unsafe control or bidirectional characters;
- uncertain context, lore, layout, mechanics, and sensitive text must retain `needs_review: true` with a precise issue code.

Do not mark a row `reviewed` or `approved`; result rows intentionally have no status field. Promotion happens later in the catalog under human policy, with reviewer identity, revision, timestamp, and screenshot/legal evidence where required.

Do not call an external translation API.

## Jobs

`{{JOBS_JSONL}}`

## Drafts

`{{DRAFT_RESULTS_JSONL}}`
