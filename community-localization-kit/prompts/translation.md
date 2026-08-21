# Translation prompt — generic target language

Prompt version: `community-translation-v1`

## Variables

- `{{TARGET_LANGUAGE_NAME}}`: human-readable language name
- `{{TARGET_LANGUAGE_BCP47}}`: BCP 47 language tag
- `{{STYLE_GUIDE}}`: reviewed target-language style guide
- `{{GLOSSARY_JSON}}`: reviewed glossary JSON
- `{{JOBS_JSONL}}`: one deterministic batch produced by `InvokersRu.Cli jobs`

## Instructions

Translate each `english` value into {{TARGET_LANGUAGE_NAME}} (`{{TARGET_LANGUAGE_BCP47}}`). The optional `ukrainian_hint` is context only; English is authoritative. Apply `{{STYLE_GUIDE}}` and `{{GLOSSARY_JSON}}` consistently.

Return JSON Lines only: exactly one compact JSON object for every input job, in the same order, with no Markdown fence and no commentary. Each output object must contain exactly:

```json
{"job_id":"COPY_FROM_JOB","translation":"TARGET TEXT","model":"MODEL_OR_MANUAL","prompt_version":"community-translation-v1","confidence":"high|medium|low","needs_review":false,"issue_codes":[]}
```

Non-negotiable rules:

1. Copy `job_id` byte-for-byte. Never invent, merge, omit, or reorder jobs.
2. Preserve every item in `protected_tokens` exactly, including case and multiplicity.
3. Preserve all literal numbers, percentages, compact mechanic units (`10s`, `6m`, `2x`, and similar values), URLs, email addresses, placeholders, rich-text tags, escaped line breaks, and literal line-break count.
4. Output Unicode NFC text. Do not introduce NUL, bidirectional controls, zero-width characters, or unsupported fields.
5. Do not translate names or terms against the glossary. Do not infer missing gameplay facts.
6. Set `needs_review` to `true` for ambiguous context, lore uncertainty, likely UI overflow, grammar uncertainty, mechanics uncertainty, or any sensitive/legal/account/payment string. Use concise ASCII issue codes such as `ambiguous_context`, `terminology`, `lore`, `ui_length`, `grammar`, or `mechanics`.
7. A model result is a draft. Never claim human approval, screenshot QA, legal approval, or release readiness.
8. Do not call an external translation API. Work only from the supplied batch and local project guidance.

## Input

`{{JOBS_JSONL}}`
