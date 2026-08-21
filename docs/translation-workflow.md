# Переводческий конвейер EN → RU внутри Codex

## Принцип

Проект не использует внешний API-клиент, API-ключи или Batch-загрузки. Оригинальные EN/UK строки остаются в `work/` и обрабатываются последовательными задачами Codex. Публичный `translations/ru_RU.jsonl` содержит только hash-ID, source/hint hashes, русский текст и структурированный provenance.

Это не offline inference: модели работают внутри Codex. Координатор сообщил о согласии HitZone на текущий контролируемый POC без вмешательства в процессы игры; публичный релиз и передача за пределы review-контура всё равно требуют отдельного одобрения.

## Роли

1. Terra переводит небольшие детерминированные чанки и всегда создаёт `draft`.
2. Validator блокирует потерянные placeholders/tags/URL/email, числа, проценты, compact units, переносы, invalid Unicode, NUL и bidi/invisible controls.
3. Low/medium confidence, `needs_review`, long text и context-required группы уходят в Sol.
4. Community reviewer проверяет смысл, glossary и UI; только человек назначает `reviewed/approved`.
5. Legal/privacy/account/payment и legacy-control строки по умолчанию не переводятся и сохраняют официальный English fallback до отдельного согласования.

## Локальные файлы

Приватный work item:

```json
{"schema":1,"job_id":"ru-...","content_guid":"...","content_version":"...","ids":["..."],"source_sha256":"...","hint_sha256":"...","english":"...","ukrainian_hint":"...","protected_tokens":["{0}"],"risk_flags":["context_required"],"deduplicated":true}
```

Результат задачи Codex:

```json
{"job_id":"ru-...","translation":"...","model":"gpt-5.6-terra","prompt_version":"ru-v2","confidence":"high","needs_review":false,"issue_codes":[]}
```

Importer заново проверяет canonical `job_id`, content GUID/version, hash каждого source/hint, точное соответствие English каждому ID, глобальную уникальность ID, protected tokens и risk flags. Неполный чанк отклоняется, если явно не указан диагностический `--allow-partial`. Свободные model notes в публичный overlay не переносятся.

## MVP и checkpoints

```powershell
InvokersRu.Cli.exe jobs --english en_US.bin.br --ukrainian uk_UA.bin.br `
  --translations ru_RU.jsonl --output work\private.jsonl

.\work\mvp\select-mvp.ps1 -InputJobs work\private.jsonl `
  -OutputJobs work\mvp\Prod_0.60.0_26\mvp.jobs.jsonl

.\scripts\split-work-items.ps1 -InputJobs work\mvp\Prod_0.60.0_26\mvp.jobs.jsonl `
  -OutputDirectory work\mvp\Prod_0.60.0_26\chunks-ru-v2 `
  -PromptPath prompts\translation-system.ru-v2.md `
  -GlossaryPath localization\glossary.ru.json
```

Каждый checkpoint закрепляет SHA-256 входного чанка, prompt и glossary. Состояния:

```text
pending → terra_done → validated → needs_sol → human_review → approved
```

Уже завершённый SHA не переводится повторно. Результаты с механической ошибкой не сливаются. Один merge-процесс обновляет overlay атомарно.

## Release gate

`validate --profile release` требует Ukrainian hint package, только `approved` записи, reviewer/timestamp/revision metadata, 100% покрытия несекретного корпуса, screenshot QA для context-required строк и отдельное двойное подтверждение sensitive-текста. `build --release` применяет только approved записи. Установочный `apply` дополнительно требует catalog SHA-256, закреплённый certified compatibility entry.

## Текущий корпус

- Текущий Windows runtime tuple версии `0.60.1247`: 41 292 ID.
- Публичный overlay: 41 037 source-bound draft-записей.
- Текущий community preview применяет 41 037 строк; 1 строка остаётся английской, 254 значения пусты/служебны в исходном корпусе.
- 23 434 записи имеют `needs_review=true`: preview позволяет увидеть их в игре, но это не равнозначно статусу `reviewed/approved` и не проходит строгий release gate выше.
- Исторический safe-preview для `0.60.1239` содержал 576 применяемых записей из раннего корпуса; его receipts сохранены только как воспроизводимое свидетельство этапа разработки.

Dedup экономит работу, но не доказывает одинаковый UI-контекст. Такие группы помечаются `context_required`, не могут пройти release без screenshot QA и при необходимости получают per-ID override.

## Работа через Git

GitHub хранит код, source-free overlay, glossary, prompts и отчёты QA. Оригинальные EN/UK таблицы, приватные work items, пользовательские скриншоты и собранный игровой cache-файл не коммитятся.

Для community-проверки удобнее создавать issue или pull request с hash-ID, предлагаемым русским вариантом, кратким описанием экрана и обезличенным контекстом. Maintainer сверяет предложение с приватным EN/UK source, прогоняет validator и обновляет provenance. Статус `reviewed/approved` назначается только после человеческой проверки; одно только принятие pull request не делает строку release-ready.
