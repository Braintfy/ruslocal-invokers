# Канал обновления переводов для Windows-патчера

Этот набор инструментов формирует неизменяемый Brotli-каталог и подписанный ECDSA P-256 envelope, который проверяется тем же `SignedUpdateVerifier`, что используется патчером. Подпись данных не является Authenticode-подписью EXE: она отдельно защищает переводы и метаданные от подмены.

Для запуска нужен .NET SDK 10.0.302 (или совместимый 10.0.x) и Windows PowerShell 5.1 либо PowerShell 7. Скрипты сначала ищут SDK в `work\dotnet-10`, затем в `PATH`. Нестандартный путь можно передать только на время текущего окна: `$env:INVOKERSRU_DOTNET = 'D:\dotnet-10\dotnet.exe'`.

## Что входит в один выпуск

Новая папка выпуска содержит ровно следующие публичные артефакты:

- `ru_RU.jsonl.br` — точные байты каталога, сжатые Brotli;
- `update-manifest.payload.json` — точные подписанные байты манифеста для аудита;
- `update-envelope.json` — публикуемый подписанный envelope;
- `update-release-receipt.json` — контрольные суммы и количества для оператора.

`release_id` обязан совпадать с именем тега/релиза GitHub. URL каталога всегда строится как `https://github.com/Braintfy/ruslocal-invokers/releases/download/<release_id>/ru_RU.jsonl.br`; произвольный сервер указать нельзя.

## Однократное создание ключа

Ключ создаётся только отдельной явной командой. Папка должна быть новой, располагаться вне репозитория и вне любой синхронизируемой/общей папки:

```powershell
New-Item -ItemType Directory -Path 'D:\InvokersRu-secrets'
powershell -ExecutionPolicy Bypass -File .\scripts\update-channel\New-UpdateSigningKey.ps1 `
  -OutputDirectory 'D:\InvokersRu-secrets\update-signing-v1'
```

Команда создаёт:

- `update-signing-private.pem` — приватный ключ PKCS#8, никогда не выводится в консоль;
- `update-signing-public.json` — публичный SPKI, его SHA-256 и производный `key_id`;
- `update-signing-state.json` — append-only состояние последовательностей.

На Windows каталог и секретные файлы получают ACL только текущего пользователя. Сделайте отдельную зашифрованную резервную копию всей папки. Не копируйте приватный ключ или signing state в Git. `key_id` вычисляется как `p256-` плюс первые 24 строчные hex-цифры SHA-256 от DER SubjectPublicKeyInfo.

Публичный SPKI и `key_id` из `update-signing-public.json` должны быть закреплены в патчере до включения сетевого канала. Потеря приватного ключа требует выпуска нового патчера с новым закреплённым ключом.

## Файл совместимости

`Compatibility` — строгий UTF-8 JSON без BOM, корнем которого является массив объектов существующей схемы `SignedUpdateCompatibilityProfile`. Неизвестные/пропущенные поля, дубли свойств и повторяющиеся `profile_id` отклоняются. Каждый элемент массива является exact-профилем конкретного уже исследованного набора файлов игры. Сам подписанный каталог также может использоваться режимом `compatible-revision`, но только внутри content GUID family, уже аутентифицированной встроенным или подписанным exact-профилем.

Пример формы одного элемента (хэши и значения здесь намеренно не являются рабочими):

```json
[
  {
    "profile_id": "runtime-cache-win64-0.60.1247-prod68",
    "mode": "exact",
    "game_version": "0.60.1247",
    "stamp_sha256": "<64 UPPERCASE HEX>",
    "stamp_value": "0.60.1247",
    "content_guid": "<lowercase canonical GUID>",
    "loc1_schema": 4,
    "ordered_keyset_sha256": "<64 UPPERCASE HEX>",
    "english": {
      "sha256": "<64 UPPERCASE HEX>",
      "content_version": "Prod_0.60.0_68",
      "locale_id": 1,
      "locale_revision_hex": "<8 UPPERCASE HEX>",
      "release_revision": 68,
      "entry_count": 41292
    },
    "base": {
      "sha256": "<64 UPPERCASE HEX>",
      "content_version": "Prod_0.60.0_68",
      "locale_id": 8,
      "locale_revision_hex": "<8 UPPERCASE HEX>",
      "release_revision": 68,
      "entry_count": 41292
    },
    "composition": {
      "applied_ru": 41037,
      "english_fallback": 1,
      "base_fallback": 254,
      "missing_catalog": 1,
      "stale_catalog": 0,
      "rejected_catalog": 0,
      "needs_review_fallback": 0,
      "policy_fallback": 0,
      "validation_errors": 0,
      "validation_warnings": 5355,
      "output_raw_sha256": "<64 UPPERCASE HEX>"
    }
  }
]
```

Новый проверенный exact-профиль предпочтителен, но версия 3.1.1 не требует его для каждой текстовой ревизии. Без exact-профиля патчер локально закрепляет текущие EN/base/stamp, keyset, каталог, output и counts; применяются только записи с точными `source_sha256` и `hint_sha256`, без fuzzy matching. Новый exact-профиль нужен для полного сертифицированного покрытия или новой content family. Старый профиль можно оставить в массиве только после повторной композиции и проверки этого же каталога на старом корпусе.

### Генерация необязательного exact-профиля

Не составляйте compatibility JSON вручную. Сначала скопируйте в отдельную рабочую папку точные файлы одной официальной установки после выбора украинского языка и полного закрытия игры: EN LOC1, исходный UK/base LOC1 и его stamp. Постройте русскую версию командой CLI `build` в **новый** файл вместе с `build-report.json`; используйте тот же `ru_RU.jsonl`, который будет опубликован. Для preview runtime policy нужны `--include-draft --raw` (без `--exclude-needs-review`), для прошедшего release gate — `--release --raw`. При разных content version EN/UK добавьте `--per-locale-content-version`.

Пример воспроизводимой preview-композиции из snapshot-файлов (оба output должны ещё не существовать):

```powershell
dotnet run --project .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release -- build `
  --english '.\work\profile-review\dl_en_US.bin' `
  --base '.\work\profile-review\dl_uk_UA.bin' `
  --translations '.\translations\ru_RU.jsonl' `
  --output '.\work\profile-review\dl_uk_UA.ru.bin' `
  --report '.\work\profile-review\build-report.json' `
  --include-draft --per-locale-content-version --raw
```

Затем создайте массив из одного exact-профиля:

```powershell
New-Item -ItemType Directory -Path '.\work\release-input' -Force

powershell -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\New-SignedUpdateCompatibilityProfile.ps1 `
  -RuntimeProfile '.\work\profiles\runtime-cache-profile.new.json' `
  -EnglishLoc1 '.\work\profile-review\dl_en_US.bin' `
  -BaseLoc1 '.\work\profile-review\dl_uk_UA.bin' `
  -Stamp '.\work\profile-review\dl_uk_UA.bin.ver' `
  -Catalog '.\translations\ru_RU.jsonl' `
  -BuiltLoc1 '.\work\profile-review\dl_uk_UA.ru.bin' `
  -BuildReport '.\work\profile-review\build-report.json' `
  -Output '.\work\release-input\compatibility.json'
```

У команды нет параметров для ручного ввода хешей, revisions, entry/composition counts или output pin. Она:

- повторно читает strict UTF-8 runtime profile/report/catalog и отклоняет BOM, неизвестные/дублированные JSON-поля и невалидный JSONL;
- парсит фактические EN, base и built LOC1, сверяет schema/GUID/content versions/locale IDs/revisions/release revisions/entry count, а также реальные SHA-256 контейнеров, raw LOC1, stamp и каталога;
- вычисляет `ordered_keyset_sha256` только через production `SignedUpdateRuntimeProfileAdapter` (`ID_HEX16 + LF` в LOC1-порядке);
- заново запускает production validator/composer с policy из CLI report и требует точного совпадения всех composition counts, warnings, итоговых raw-байтов и `validation.errors = 0`;
- прогоняет полученный профиль через настоящий `SignedUpdateVerifier` и `SignedUpdateRuntimeProfileAdapter` с одноразовым P-256 ключом только в памяти;
- атомарно создаёт ровно один новый strict UTF-8 файл без BOM и никогда не перезаписывает существующий output.

Генератор не ищет установленную игру, не запускает её и не изменяет её файлы: он читает только явно переданные snapshot-файлы и пишет только новый `-Output`. Для другого каталога каждый сохраняемый старый профиль нужно заново воспроизвести тем же способом на его точном EN/base корпусе; простое копирование старых counts недопустимо.

## Сборка подписанного выпуска

`sequence` всегда задаётся явно и должен быть ровно на единицу больше `ExpectedPreviousSequence`. Инструмент сверяет ожидаемое значение с защищённым signing state. Перед созданием публичной папки номер резервируется атомарно; аварийно зарезервированный номер нельзя использовать повторно — следующий выпуск получает следующий номер.

```powershell
New-Item -ItemType Directory -Path '.\work\update-release'
powershell -ExecutionPolicy Bypass -File .\scripts\update-channel\New-SignedUpdateRelease.ps1 `
  -Catalog '.\translations\ru_RU.jsonl' `
  -Compatibility '.\work\release-input\compatibility.json' `
  -PrivateKey 'D:\InvokersRu-secrets\update-signing-v1\update-signing-private.pem' `
  -SigningState 'D:\InvokersRu-secrets\update-signing-v1\update-signing-state.json' `
  -OutputDirectory '.\work\update-release\invokersru-data-00000001' `
  -ReleaseId 'invokersru-data-00000001' `
  -ArtifactId 'ru-RU-data-00000001' `
  -Sequence 1 `
  -ExpectedPreviousSequence 0 `
  -IssuedUtc '2026-08-21T12:00:00Z' `
  -ExpiresUtc '2026-09-20T12:00:00Z' `
  -MinimumPatcherVersion '3.1.1' `
  -LatestPatcherVersion '3.1.1' `
  -TranslationPolicy 'validated-preview-v1' `
  -Notes 'Обновлён перевод интерфейса.'
```

Для `release-approved-v1` каждая строка каталога обязана иметь статус `approved`. `validated-preview-v1` допускает текущий проверенный preview-каталог. Срок действия манифеста не может превышать 45 дней; время имеет строгий вид `YYYY-MM-DDTHH:mm:ssZ`.

После успеха:

1. Сверьте четыре файла с `update-release-receipt.json`.
2. Создайте **неизменяемый data release** с новым тегом, точно равным `release_id`. Не отмечайте его как `Latest`: ссылка патчера на `/releases/latest` должна вести на отдельный player release с установщиком, а не на служебные данные.
3. Загрузите без переименования именно сырые `ru_RU.jsonl.br` и `update-envelope.json`. Нельзя заменять `.br` ZIP-архивом: GitHub не раздаёт файл внутри ZIP по URL, закреплённому в manifest. Payload и receipt рекомендуется приложить отдельными raw assets для прозрачного аудита.
4. Анонимно откройте точный URL каталога из envelope и проверьте HTTP 200, размер и SHA-256 по receipt. Никогда не заменяйте ассеты этого data release: любое изменение получает новый `release_id` и новый `sequence`.
5. Только после полной проверки data release обновите **фиксированный указатель канала**: точные байты нового `update-envelope.json` опубликуйте под именем `update-envelope.v1.json` в отдельном release/tag `invokersru-update-channel-v1`. Этот release также не должен становиться `Latest`. Закреплённый в патчере URL остаётся постоянным: `https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json`.
6. В fixed channel release изменяется только `update-envelope.v1.json`; это не новый манифест и не новая подпись, а байт-в-байт копия envelope из неизменяемого data release. Сначала публикуется data release, указатель переключается последним. Подпись, anti-rollback sequence и cache last-known-good защищают клиент при подмене, откате или временной недоступности указателя.
7. Отдельно создайте player release для новой версии GUI/CLI, приложите installer и `.sha256`, укажите точный commit/tag и только его при необходимости пометьте `Latest`. Не смешивайте player, data и fixed-pointer assets в одном release.
8. Храните историю signing state и резервную копию вместе с ключом. Не редактируйте JSON состояния вручную.

## Самопроверка инструмента

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1
```

Тест создаёт только одноразовый временный ключ и синтетические LOC1/catalog/report в `%TEMP%`. Он проверяет подпись через production `SignedUpdateVerifier`, генерацию exact-профиля через production adapter, атомарный CreateNew, отклонение подменённых report/catalog hash, built LOC1 и runtime profile, а также невозможность повторного использования sequence. Затем временная папка удаляется; production-ключ команда не создаёт.
