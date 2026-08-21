# Target-language style guide

Target language: `{{TARGET_LANGUAGE_NAME}}` (`{{TARGET_LANGUAGE_BCP47}}`)

## Voice

- Use clear, compact game-interface language.
- Prefer established RPG terminology used by native speakers.
- Keep button labels short and use one consistent capitalization style.
- Do not add facts, jokes, lore, or mechanical meaning that is absent from the source.

## Non-negotiable technical rules

- Preserve every placeholder, rich-text tag, URL, email address, escaped newline, literal number, percentage, and compact mechanic unit exactly.
- Preserve the number of literal line breaks.
- Return Unicode NFC text in strict UTF-8.
- Never translate proper names unless the glossary explicitly requires it.
- Mark ambiguous, legal, privacy, account, payment, or context-dependent text for human review.

## Typography

Document the target language's quotation marks, spaces around punctuation, decimal conventions, and capitalization here. Do not change numeric tokens merely to follow a locale convention; the validator requires the source numbers byte-for-byte.

## Length

Aim for the source length where practical. Flag a string when a natural translation is likely to overflow a button, card, tooltip, or narrow mobile layout.

## Review policy

Define who may change a record from `draft` to `reviewed` or `approved`, and record screenshot QA for context-sensitive strings. Model output is always a draft.
