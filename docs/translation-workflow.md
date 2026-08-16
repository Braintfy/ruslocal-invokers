# Переводческий конвейер EN → RU внутри Codex

## Принцип

Проект не использует внешний API-клиент, API-ключи или Batch-загрузки. Оригинальные EN/UK строки остаются в `work/` и обрабатываются последовательными задачами Codex. Публичные `translations/ru_RU.jsonl` и `translations/ru_RU.next.jsonl` содержат только hash-ID, source/hint hashes, русский текст и структурированный provenance. Первый файл закреплён за последним runtime-тестом, второй накапливает полный перевод и не считается протестированным пакетом.

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
  --translations translations\ru_RU.next.jsonl --output work\private.jsonl

.\work\mvp\select-mvp.ps1 -InputJobs work\private.jsonl `
  -OutputJobs work\mvp\Prod_0.60.0_26\mvp.jobs.jsonl

.\scripts\split-work-items.ps1 -InputJobs work\mvp\Prod_0.60.0_26\mvp.jobs.jsonl `
  -OutputDirectory work\mvp\Prod_0.60.0_26\chunks-ru-v2 `
  -PromptPath prompts\translation-system.ru-v2.md `
  -GlossaryPath localization\glossary.ru.json
```

Для полного корпуса сначала создаются детерминированные очереди. Они оставляют
короткий UI отдельно от структурированных формул и длинных описаний:

```powershell
.\scripts\partition-translation-jobs.ps1 `
  -InputJobs work\full-translation\0.60.1239\missing.jobs.jsonl `
  -OutputDirectory work\full-translation\0.60.1239\lanes
```

Каждый checkpoint закрепляет SHA-256 входного чанка, prompt и glossary. Состояния:

```text
pending → terra_done → validated → needs_sol → human_review → approved
```

Уже завершённый SHA не переводится повторно. Результаты с механической ошибкой не сливаются. Один merge-процесс обновляет overlay атомарно.

## Безопасный импорт волны

Working draft должен сохранять и строки `needs_review`: именно из него затем строится очередь Sol. Это не делает такие строки допустимыми для runtime/release. Для импорта явно перечисляется завершённая волна; glob и автоматический выбор всех найденных result-файлов не используются.

```powershell
.\scripts\mark-checkpoint.ps1 `
  -CheckpointPath work\full-translation\0.60.1239\chunks\lane-01\chunk-0001.checkpoint.json `
  -PromptPath prompts\translation-system.ru-v2.md `
  -ReviewPromptPath prompts\translation-review.ru-v1.md `
  -GlossaryPath localization\glossary.ru.json `
  -ValidationErrors 0

$baseSha = (Get-FileHash translations\ru_RU.next.jsonl -Algorithm SHA256).Hash

.\scripts\new-translation-wave-selection.ps1 `
  -Chunks work\full-translation\0.60.1239\chunks\lane-01 `
  -CompleteChunkId chunk-0001,chunk-0002 `
  -BaseOverlay translations\ru_RU.next.jsonl `
  -ExpectedBaseOverlaySha256 $baseSha `
  -PromptPath prompts\translation-system.ru-v2.md `
  -ReviewPromptPath prompts\translation-review.ru-v1.md `
  -GlossaryPath localization\glossary.ru.json `
  -PromptVersion ru-v2 `
  -ReviewPromptVersion ru-review-v1 `
  -Output work\full-translation\0.60.1239\wave-01.selection.json

.\scripts\import-translation-wave.ps1 `
  -Chunks work\full-translation\0.60.1239\chunks\lane-01 `
  -SelectionManifest work\full-translation\0.60.1239\wave-01.selection.json `
  -English work\private\dl_en_US.bin `
  -BaseOverlay translations\ru_RU.next.jsonl `
  -ExpectedBaseOverlaySha256 $baseSha `
  -PromptPath prompts\translation-system.ru-v2.md `
  -ReviewPromptPath prompts\translation-review.ru-v1.md `
  -GlossaryPath localization\glossary.ru.json `
  -CliPath path\to\InvokersRu.Cli.dll `
  -Output translations\ru_RU.wave-01.jsonl `
  -ReceiptPath translations\ru_RU.wave-01.receipt.json
```

Сначала можно добавить `-DryRun` и не указывать `-CliPath`; это проверяет цепочку manifest/checkpoint/result, точное покрытие и формирует source-free receipt без создания overlay. Боевой запуск единожды вызывает `import-results` без `--allow-partial`. Для локального .NET host можно передать `-DotnetPath work\dotnet-10\dotnet.exe`.

Selection закрепляет SHA-256 chunks manifest, base overlay, Terra prompt, Sol review prompt, glossary, каждого checkpoint и result. Checkpoint хранит точный отсортированный набор фактических моделей; допустимы только пары `gpt-5.6-terra`/`ru-v2` и `gpt-5.6-sol`/`ru-review-v1`. Допускаются только явно выбранные состояния `terra_done`, `validated` и `needs_sol`; для проверенных checkpoints набор эскалаций заново выводится из confidence/needs_review и рисков `context_required`/`long_text`. Jobs/results и временные агрегаты остаются приватными и удаляются после вызова CLI. Receipt содержит только hashes, counts, chunk IDs и имя нового overlay — исходных EN/UK строк или переводческих result-файлов в нём нет. Любой существующий output/receipt приводит к отказу без перезаписи.

## Release gate

`validate --profile release` требует Ukrainian hint package, только `approved` записи, reviewer/timestamp/revision metadata, 100% покрытия несекретного корпуса, screenshot QA для context-required строк и отдельное двойное подтверждение sensitive-текста. `build --release` применяет только approved записи. Установочный `apply` дополнительно требует catalog SHA-256, закреплённый certified compatibility entry.

## Текущий корпус

- Текущий runtime tuple версии `0.60.1239`: 41 290 ID.
- Замороженный протестированный overlay: 1 842 draft-записи.
- Исторический runtime preview: 576 применённых записей. После усиления sensitive- и EN↔UK context-политик тот же каталог даёт 539 безопасных кандидатов, поэтому старый build-профиль намеренно не проходит exact gate.
- Первая полная волна: 684 новых ID; рабочий `ru_RU.next.jsonl` содержит 2 526 draft-записей.
- Локальная проверочная сборка рабочего каталога: 994 применяемых записи, 1 495 `needs_review` fallback и 255 пустых значений базового locale. Она ещё не проходила runtime QA.
- Исходная полная приватная очередь: 25 016 дедуплицированных заданий для 39 153 ещё не покрытых ID.
- После первой волны и повторной классификации: 24 467 дедуплицированных заданий для 38 602 ID; записи с устаревшей risk-метаинформацией снова поставлены в очередь.
- 101 чувствительный ID остаётся в официальном английском до отдельной двойной проверки; 255 пустых ID не требуют перевода.
- Полный рабочий overlay ведётся как `translations/ru_RU.next.jsonl`; certified-профиль не перепинивается до новой сборки и runtime QA.
- Исторический стратифицированный MVP начинался с 1 000 work items / 1 820 ID в 20 детерминированных чанках; после миграции на фактически загружаемый runtime-кэш добавлены и повторно проверены изменившиеся ID.

Dedup экономит работу, но не доказывает одинаковый UI-контекст. Такие группы помечаются `context_required`, не могут пройти release без screenshot QA и при необходимости получают per-ID override.

## Работа через Git

GitHub хранит код, source-free overlay, glossary, prompts и отчёты QA. Оригинальные EN/UK таблицы, приватные work items, пользовательские скриншоты и собранный игровой cache-файл не коммитятся.

Для community-проверки удобнее создавать issue или pull request с hash-ID, предлагаемым русским вариантом, кратким описанием экрана и обезличенным контекстом. Maintainer сверяет предложение с приватным EN/UK source, прогоняет validator и обновляет provenance. Статус `reviewed/approved` назначается только после человеческой проверки; одно только принятие pull request не делает строку release-ready.
