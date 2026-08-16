# InvokersRu

Windows-first community localization tooling for **Invokers: Titan Legacy**.

Состояние на 16 августа 2026 года: **контролируемый runtime-тест на Windows успешно пройден** для версии игры `0.60.1239`. Клиент загрузил изменённый локальный кэш, а пользовательские скриншоты подтвердили появление русского текста без вмешательства в процессы игры. Preview содержит 576 консервативно отобранных строк; остальной интерфейс ожидаемо остаётся смешанным EN/UK. Это всё ещё технический preview, а не полная локализация, официальный релиз или подтверждение одобрения разработчиками. Подробности: [docs/runtime-test-receipt.md](docs/runtime-test-receipt.md).

Проект неофициальный и не содержит оригинальных файлов игры. Правовой и брендовый статус описан в [NOTICE.md](NOTICE.md).

## Что уже доказано

- PC-игра использует Unity/IL2CPP, но актуальные таблицы локализации загружает из пользовательского runtime-кэша, а не напрямую из встроенных `StreamingAssets/i18n/*.bin.br`.
- Английский и украинский runtime-файлы версии `0.60.1239` содержат по 41 290 стабильных 64-битных ID.
- Парсер и writer дают побайтово идентичный round-trip для EN и UK.
- Runtime writer сохраняет locale identity, UUID, release, hash order и поддерживаемый collision layout; меняет только таблицу значений и требуемую длину данных.
- Патчер сверяет exact SHA-256 EN/UK runtime-кэша, версионного stamp, translation overlay и ожидаемого результата.
- Неизвестная или изменённая сборка получает отказ; `--force` отсутствует.
- Клиент действительно прочитал собранный `dl_uk_UA.bin`: русский UI подтверждён наблюдаемым запуском и скриншотами 16 августа 2026 года.
- Оригинальный runtime-файл сохраняется в content-addressed backup и проверяется по SHA-256; транзакционный restore протестирован побайтово.
- Приватные EN/UK задания отделены от публичного RU overlay: оригинальные строки и игровые assets не должны попадать в репозиторий или релиз.
- Обычная сборка не имеет права записи. Изолированные smoke-тесты проверяют runtime apply/recovery/restore, конкурентную замену файла и отказ на несовместимой версии.

## Архитектура локализации

Первый POC использует существующий слот `uk_UA`: ключи, locale ID `8` и identity-поля текущего runtime-файла сохраняются, русские значения подставляются по hash-ID. Цель — `%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n\dl_uk_UA.bin`; файлы в каталоге установки игры не изменяются. Непроверенные записи получают официальный английский fallback, а отдельные vendor/server строки могут остаться украинскими. Поэтому в игре нужно выбирать украинский язык, а текущий результат является смешанным RU/EN/UK preview.

Публичный overlay `translations/ru_RU.jsonl` хранит только:

```json
{"id":"16_HEX","source_sha256":"64_HEX","translation":"Русский текст","status":"draft","model":"gpt-5.6-terra","prompt_version":"ru-v2","updated_at":"2026-08-15T00:00:00Z"}
```

`source_sha256` автоматически инвалидирует перевод, если английская фраза изменилась в следующем обновлении. Полного английского текста в overlay нет.

## Команды

Чтение и анализ:

```powershell
InvokersRu.Cli.exe inspect dl_uk_UA.bin
InvokersRu.Cli.exe roundtrip dl_uk_UA.bin
InvokersRu.Cli.exe cache-status
InvokersRu.Cli.exe cache-plan
```

Приватный переводческий конвейер:

```powershell
InvokersRu.Cli.exe jobs `
  --english dl_en_US.bin `
  --ukrainian dl_uk_UA.bin `
  --translations ru_RU.jsonl `
  --output Prod_0.60-runtime.private.jsonl

InvokersRu.Cli.exe import-results `
  --english dl_en_US.bin `
  --jobs Prod_0.60-runtime.private.jsonl `
  --results model-results.jsonl `
  --translations ru_RU.jsonl `
  --output ru_RU.next.jsonl

InvokersRu.Cli.exe validate --english dl_en_US.bin --ukrainian dl_uk_UA.bin --translations ru_RU.next.jsonl --include-draft --per-locale-content-version
InvokersRu.Cli.exe build --english dl_en_US.bin --base dl_uk_UA.bin --translations ru_RU.next.jsonl --output dl_uk_UA.preview.bin --report preview.json --include-draft --exclude-needs-review --raw --per-locale-content-version
```

`jobs` дедуплицирует одинаковые пары EN+UK. Полный job-файл содержит оригинальный текст, поэтому остаётся приватным и попадает под `.gitignore`; публичный overlay содержит только hashes и русский перевод.

Даже одинаковые EN+UK фразы теоретически могут требовать разного перевода в разном UI-контексте. Дедуплицированные группы поэтому автоматически получают `context_required` и не могут попасть в финальный release без экранной проверки или per-ID override.

Команды записи в обычной unsigned dev-сборке безусловно выключены на уровне Core — редактирование внешнего JSON это не обходит. Контролируемый binary собирается только с exact hashes готового preview и неизменяемым runtime-профилем внутри assembly. Он заменяет только пользовательский `dl_uk_UA.bin`: без изменений игровых assets, DLL/EXE, launcher или сети, без injection, hooks, process kill и фонового вмешательства. Полная процедура описана в [docs/supervised-test.md](docs/supervised-test.md).

## Перевод внутри Codex

- Массовые локальные чанки: `gpt-5.6-terra`.
- Неоднозначные строки, lore, критические механики и независимый QA: `gpt-5.6-sol`.
- Разработка и security review патчера: `gpt-5.6-sol`, high reasoning.
- `reviewed/approved` выставляет только человек из community-команды.

Отдельного API-клиента, API-ключа и Batch-загрузки нет. `jobs` создаёт приватный локальный корпус, `work/mvp/select-mvp.ps1` выбирает 1 000 заданий / 1 820 ID для связного MVP, а `scripts/split-work-items.ps1` создаёт 20 детерминированных чанков и checkpoint-файлы. Перевод выполняется в задачах Codex; это не означает offline inference, но исключает отдельную интеграцию со сторонним API.

Актуальный runtime overlay содержит 1 842 draft-записи. В подтверждённый conservative preview попадают 576 записей с высокой уверенностью и без `needs_review`; 1 266 строк исключены до контекстной проверки. Terra делает массовый draft, validator немедленно отбрасывает механические ошибки, Sol получает только эскалации и QA-выборку. Подробнее: [docs/translation-workflow.md](docs/translation-workflow.md).

## Новые версии игры

Патчер никогда не пытается «угадать» совместимость:

1. официальный launcher обновляет игру как обычно;
2. `status` видит новый SHA-256 и возвращает `UnknownBuild`;
3. maintainer извлекает новый EN/UK, строит diff по ID и source SHA;
4. неизменённые переводы остаются, изменённые становятся stale, новые уходят в jobs;
5. после QA и loader-теста добавляется новая подписанная compatibility entry;
6. пользователь обновляет патчер/каталог и применяет локализацию заново.

Старый backup никогда не накатывается поверх новой официальной сборки.

## Ограничения и разрешение

Актуальные Terms HitZone требуют осторожности с модификацией клиента. Патчер не распространяет APK, игровые assets или оригинальные таблицы. Координатор проекта сообщил о согласии разработчиков на наблюдаемый тест без вмешательства в процессы; это относится к текущему локальному POC, но не заменяет финальное одобрение публичного пакета. Первый runtime-тест успешно пройден; перед публичным релизом результат всё равно должен пройти community-проверку и отдельное согласование с разработчиками через Discord.

Текущий dev `.exe` не подписан Authenticode; для публичной раздачи нужен reproducible build, release signing, signed compatibility catalog и проверяемые checksums.

## Сборка

Проект на .NET 10 LTS, внешних NuGet-зависимостей нет.

```powershell
dotnet restore InvokersRu.sln --configfile NuGet.Config
dotnet build InvokersRu.sln -c Release --no-restore
dotnet run --project src/InvokersRu.SmokeTests -c Release --no-build -- PATH_TO_PRIVATE_FIXTURES
```

Оригинальные `*.bin`, `*.bin.br` и `manifest.dat` не входят в проект и должны оставаться локальными.
