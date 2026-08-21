# Самостоятельная сборка Windows-патчера

Эта инструкция предназначена для игроков, аудиторов и разработчиков, которые не хотят запускать готовый EXE и предпочитают собрать InvokersRu из проверенного исходного кода. Обычная сборка, публикация payload и создание установщика **не читают и не изменяют игру**. Обычный `dotnet build` пишет `bin\`/`obj\` внутри клона, publish/installer — в `work\`, а .NET при необходимости заполняет пользовательский кэш SDK/NuGet. Файлы игры меняются лишь позже, когда пользователь сам запускает собранный патчер, закрывает игру и подтверждает установку русификации.

Ниже отдельно описаны:

- обычная диагностическая сборка без возможности записи в игру;
- self-contained Windows payload с контролируемой возможностью записи;
- необязательный установщик Inno Setup;
- проверка хэшей и Authenticode;
- официальный и собственный каналы подписанных обновлений;
- устройство перевода и передача проекта разработчикам игры.

## 1. Что именно вы проверяете

Windows-версия состоит из двух соседних программ:

- `InvokersRu.Gui.exe` — графическая оболочка;
- `InvokersRu.Cli.exe` — проверка файлов, композиция LOC1, транзакционная установка и восстановление.

Обычный `dotnet build` компилирует Core без production-возможности изменять игру. Возможность записи появляется только в специальной Release-сборке, которую создаёт `scripts\publish-windows-preview.ps1`: скрипт передаёт закрытые от произвольной подмены MSBuild-параметры, встраивает точный профиль совместимости и проверяет опубликованный CLI до упаковки.

Self-contained означает, что игроку не требуется отдельно устанавливать .NET Runtime. Это каталог из нескольких файлов, а не упакованный single-file EXE. Нельзя копировать только два EXE и выбрасывать соседние DLL/JSON: такой набор неполон.

Есть две независимые подписи:

1. **Подпись данных обновления ECDSA P-256** защищает удалённый каталог перевода и профили совместимости. Её публичный ключ встраивается в CLI. Она бесплатна и не является подписью EXE.
2. **Authenticode** подтверждает издателя EXE/установщика для Windows и SmartScreen. Для локальной проверки она необязательна; публичная неподписанная сборка всё равно может вызвать предупреждение Windows.

## 2. Требования

Нужны:

- Windows x64;
- Git;
- .NET SDK версии из корневого `global.json` — сейчас `10.0.302`;
- Windows PowerShell 5.1 или PowerShell 7 (`pwsh`).

Для создания установщика дополнительно нужен **Inno Setup 6**. Скрипт сам ищет `ISCC.exe` в стандартных каталогах; нестандартный путь передаётся параметром `-IsccPath`.

Только для Authenticode нужны Windows SDK с `signtool.exe`, сертификат code signing и RFC 3161 timestamp URL. Не используйте случайный самоподписанный сертификат как доказательство публичного издателя: он годится лишь для локального эксперимента.

Проверьте окружение из PowerShell:

```powershell
git --version
dotnet --version
powershell.exe -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
```

Если `pwsh` не установлен, достаточно Windows PowerShell 5.1. Если `dotnet --version` сообщает, что SDK из `global.json` не найден, установите именно этот SDK с официального сайта .NET. Не меняйте `global.json` только ради обхода проверки: это изменит воспроизводимое окружение.

## 3. Чистый клон и фиксация исходной версии

```powershell
git clone https://github.com/Braintfy/ruslocal-invokers.git
Set-Location .\ruslocal-invokers
git status --short
git rev-parse HEAD
```

`git status --short` после клонирования должен быть пустым. Сохраните значение `git rev-parse HEAD`: это точный commit, из которого получатся ваши бинарные файлы. Для проверки конкретного релиза начните с нового клона и переключитесь на его опубликованный тег или commit, а не смешивайте исходники разных версий.

До сборки полезно проверить, что Git не отслеживает игровые и секретные файлы:

```powershell
git ls-files work
git ls-files '*.bin' '*.bin.br' '*.exe' '*.dll'
git status --short
```

В чистом репозитории эти команды не должны показывать извлечённые EN/UK таблицы, готовые бинарники или содержимое `work\`. `.gitignore` отдельно запрещает `*.bin`, `*.bin.br`, `manifest.dat`, приватные fixture-файлы, локальное состояние и исполняемые результаты.

## 4. Обычная диагностическая сборка

Сначала соберите всё решение без права записи в игру:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --fixture-free-only
```

Эквивалентный запуск через PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --fixture-free-only
```

`--fixture-free-only` запускает проверки формата, валидатора, профилей, GUI-контракта и запрета мутаций без проприетарных EN/UK файлов. Специализированные fixture-free проверки signed-update protocol и адаптера exact-профиля запускаются отдельно:

```powershell
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-profile-adapter-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-coordinator-only
dotnet run --project .\src\InvokersRu.SmokeTests\InvokersRu.SmokeTests.csproj `
  --configuration Release --no-build -- --signed-update-lkg-hardening-only
```

Полные corpus-тесты требуют приватных файлов из собственной установки и намеренно не могут быть воспроизведены одним публичным клоном.

Отдельно проверьте инструмент выпуска подписанных данных. Команда создаёт одноразовый ключ только во временной папке, проверяет подпись production-верификатором, отклонение подмены и запрет повторного `sequence`, затем удаляет временные данные:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1
pwsh -NoProfile -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1
```

Ни одна команда этого раздела не обращается к кэшу игры и не может установить перевод.

## 5. Self-contained payload с графическим патчером

Выберите уникальную локальную версию и новый выходной каталог. Скрипт специально отказывается перезаписывать существующий результат.

```powershell
$version = '3.1.0-local.1'
$payload = ".\work\publish\windows-$version"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory $payload `
  -AppVersion $version `
  -TranslationCatalog '.\translations\ru_RU.jsonl' `
  -RuntimeCacheProfile '.\config\runtime-cache-profile.0.60.1247.json' `
  -SignedUpdateChannelConfig '.\config\signed-update-channel.v1.json'
```

Для PowerShell 7 замените начало последней команды на:

```powershell
pwsh -NoProfile -File .\scripts\publish-windows-preview.ps1 `
  -OutputDirectory $payload `
  -AppVersion $version `
  -TranslationCatalog '.\translations\ru_RU.jsonl' `
  -RuntimeCacheProfile '.\config\runtime-cache-profile.0.60.1247.json' `
  -SignedUpdateChannelConfig '.\config\signed-update-channel.v1.json'
```

Профиль в примере — встроенный резервный профиль текущей проверенной версии. Для другой версии используйте только реально сертифицированный профиль, относящийся к выбранному каталогу. Нельзя заменить хэш в JSON «на глаз»: publish-скрипт сверяет каталог, профиль, embedded resource и ожидаемые свойства CLI.

Скрипт выполняет restore runtime pack с официального NuGet endpoint, публикует обычный multi-file .NET runtime, оставляет только два разрешённых EXE, запрещает скрипты в player payload и создаёт:

- `BUILD-RECEIPT.json` — версия SDK, точные хэши GUI/CLI/каталога/профиля и состояние Authenticode;
- `PAYLOAD-SHA256.json` — полный allowlist путей, размеров и SHA-256 установочного payload;
- `translations\ru_RU.jsonl` и `profiles\...json` — публичные данные, а не оригинальные файлы игры.

Проверьте опубликованный каталог:

```powershell
& "$payload\InvokersRu.Cli.exe" trusted-runtime-cache-info
& "$payload\InvokersRu.Cli.exe" update-status --json
Get-Content -Raw -LiteralPath "$payload\BUILD-RECEIPT.json"
Get-Content -Raw -LiteralPath "$payload\PAYLOAD-SHA256.json"
Get-FileHash -Algorithm SHA256 -LiteralPath "$payload\InvokersRu.Gui.exe"
Get-FileHash -Algorithm SHA256 -LiteralPath "$payload\InvokersRu.Cli.exe"
```

`update-status` читает только уже проверенный локальный update cache. Сетевое обновление выполняет отдельная команда `update-refresh` или соответствующее действие GUI; патчер не скачивает и не запускает новый EXE.

Запустите упаковочные проверки в обеих оболочках. Они проверяют manifest, allowlist, защиту фиксированного каталога установки и отрицательные случаи; установщик и игра не запускаются:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\test-windows-packaging.ps1 `
  -PayloadDirectory $payload -AppVersion $version

pwsh -NoProfile -File .\scripts\test-windows-packaging.ps1 `
  -PayloadDirectory $payload -AppVersion $version
```

## 6. Необязательный установщик

Без Inno Setup можно запускать `InvokersRu.Gui.exe` прямо из полного `$payload`. Для обычного Setup установите Inno Setup 6 и выполните:

```powershell
$installerOutput = ".\work\installer-output-$version"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-installer.ps1 `
  -InputDirectory $payload `
  -AppVersion $version `
  -OutputDirectory $installerOutput `
  -InstallerBaseName "InvokersRu-$version-win-x64"
```

Если `ISCC.exe` установлен нестандартно, добавьте `-IsccPath` с полным путём. Скрипт сначала заново проверяет каждый файл по `PAYLOAD-SHA256.json`, копирует только allowlist в новый staging-каталог, компилирует Inno Setup, повторно проверяет stage и создаёт рядом `.sha256`.

Установщик **не запускается автоматически**. Его назначение ограничено копированием payload в `%LOCALAPPDATA%\Programs\InvokersRu`, созданием ярлыка и стандартного per-user uninstaller. В нём нет службы, задачи планировщика, автозапуска патчера, запроса администратора или автоматического закрытия игры.

Локальный неподписанный installer — нормальный результат для самостоятельной проверки. Windows может показать `Unknown publisher`, SmartScreen или репутационное предупреждение. Это не устраняется параметром компилятора и не означает наличие подписи.

## 7. Пересборка после изменений

Скрипты публикации намеренно не перезаписывают готовый payload или installer. Для повторной сборки задайте новый `AppVersion`, новый `OutputDirectory` и новый `InstallerBaseName`. Это сохраняет предыдущий результат для сравнения и исключает смесь старых и новых DLL.

Перед пересборкой определите, что изменилось:

- **Только код GUI/CLI:** пересоберите payload и installer с новой версией, затем повторите smoke/package/hash checks.
- **Только русский каталог:** старый embedded exact-профиль больше не подходит, потому что закрепляет SHA-256 каталога, итогового LOC1 и counts. Подготовьте и подпишите новый data release либо заново сертифицируйте embedded-профиль; простая замена `ru_RU.jsonl` должна завершаться отказом сборки.
- **Новая версия игры:** создайте новый blocked profile, воспроизведите композицию на приватном EN/UK корпусе, проверьте hashes/counts и выпустите новый подписанный exact-профиль. Совместимый уже установленный EXE после этого пересобирать не требуется.
- **Новый update key, endpoint, URL policy или LOC1 writer:** требуется новый EXE, потому что это часть границы доверия/кода.

Не очищайте старый output «поверх» новой сборки и не переносите файлы вручную. Сравнивайте `BUILD-RECEIPT.json`, `PAYLOAD-SHA256.json`, SHA-256 и commit между двумя отдельными каталогами.

## 8. Хэши и Authenticode

Проверьте полученный installer и sidecar:

```powershell
$installer = Get-Item -LiteralPath `
  (Join-Path $installerOutput "InvokersRu-$version-win-x64.exe")

(Get-FileHash -Algorithm SHA256 -LiteralPath $installer.FullName).Hash
Get-Content -LiteralPath ($installer.FullName + '.sha256')
Get-AuthenticodeSignature -LiteralPath $installer.FullName | `
  Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
```

Для неподписанной локальной сборки ожидаем `NotSigned`; сравнение SHA-256 всё равно доказывает, что файл не изменился после вашей сборки. После окончательной проверки исходников также выполните:

```powershell
git status --short
git diff --exit-code
git rev-parse HEAD
```

Для настоящей Authenticode-сборки publish-скрипту одновременно передаются `-SignToolPath`, `-CertificateThumbprint` и `-TimestampUrl`. Если указан только один или два параметра, сборка отклоняется. После этого в Inno Setup заранее настраивается signing-tool с выбранным именем, а installer собирается с `-InnoSignToolName` и тем же `-ExpectedSignerThumbprint`. `build-installer.ps1` проверяет подписи payload и итогового Setup; в режиме `-VerifyOnly` он намеренно не принимает `-InnoSignToolName`, потому что installer ещё не создаётся.

Подробная модель выпуска: [windows-release-safety.md](windows-release-safety.md).

## 9. Официальный канал обновлений и собственный ключ

`config\signed-update-channel.v1.json` содержит только публичный trust anchor:

- фиксированный HTTPS URL подписанного envelope;
- `key_id`;
- публичный ключ ECDSA P-256 в DER SPKI/Base64.

Приватного ключа в репозитории, payload или installer нет. Если вы собираете с официальным config, ваш бинарник принимает те же подписанные каталоги и exact-профили, что официальный патчер. Вам не нужен официальный приватный ключ.

Удалённый manifest не может заменить встроенный endpoint или ключ. Патчер проверяет подпись, срок, монотонный `sequence`, отозванные выпуски, размеры и SHA-256 Brotli/распакованного каталога, минимальную версию патчера и exact-профиль файлов игры. После удачной проверки данные кладутся в content-addressed cache `%LOCALAPPDATA%\InvokersRu\updates`; при сбое сети используется только ранее проверенный last-known-good или встроенный профиль.

Для собственного экспериментального канала сначала проверьте release-tool, затем создайте ключ **в новой внешней папке вне репозитория и синхронизируемых каталогов**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\Test-UpdateReleaseTool.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\New-UpdateSigningKey.ps1 `
  -OutputDirectory $externalNewSigningDirectory
```

Родитель `$externalNewSigningDirectory` должен существовать, а сама конечная папка — ещё нет. Инструмент создаёт `update-signing-private.pem`, `update-signing-public.json` и `update-signing-state.json`, ограничивает ACL текущим пользователем и никогда не печатает приватный ключ.

Из `update-signing-public.json` перенесите только `key_id` и `subject_public_key_info_b64` в отдельный channel config схемы `invokers-ru-update-channel`; второе поле в channel config называется `public_key_spki_base64`:

```json
{
  "schema": 1,
  "kind": "invokers-ru-update-channel",
  "envelope_url": "https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json",
  "key_id": "PUBLIC_KEY_ID_FROM_KEYGEN",
  "public_key_spki_base64": "PUBLIC_SPKI_BASE64_FROM_KEYGEN"
}
```

Это только форма файла, не рабочие ключевые значения. Standard publish проверяет и закрепляет показанный fixed endpoint; произвольный `latest/download` URL будет отклонён. Сохраните config как строгий UTF-8 без BOM внутри репозитория или `work\`, затем передайте его `publish-windows-preview.ps1` через `-SignedUpdateChannelConfig`. Никогда не копируйте PEM или signing state внутрь репозитория.

Текущая URL-политика намеренно принимает update assets только из Releases `Braintfy/ruslocal-invokers`. Одной заменой config нельзя доверить произвольный сервер или fork. Для собственного репозитория разработчик должен изменить и отдельно проверить `SignedUpdateUrlPolicy`, redirect allowlist и генератор URL в release-tool, после чего выпустить новый патчер с новым публичным ключом. Уже собранный патчер не может удалённо заменить свой корневой ключ.

Создание подписанного data release описано в [update-channel.md](update-channel.md). Это обычное обновление перевода/профилей и не требует пересборки EXE, пока не меняются формат LOC1, фиксированные пути, протокол, URL-политика или ключ доверия.

## 10. Как работает перевод

Игра хранит загруженную локализацию Windows в:

```text
%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n
```

Патчер работает с фиксированной тройкой:

```text
dl_en_US.bin      английский источник
dl_uk_UA.bin      украинская ячейка и единственный изменяемый файл
dl_uk_UA.bin.ver  stamp версии клиента
```

Поэтому до проверки игрок должен выбрать **украинский язык**, дождаться загрузки и полностью закрыть игру и лаунчер. Без этого `dl_uk_UA.bin` может отсутствовать или обновляться параллельно.

Поддерживаемый контейнер — строгий `LOC1`, schema 4. Для текущей семьи профилей EN имеет locale ID `1`, UK — `8`. Таблицы должны иметь одинаковые content GUID, schema и полный упорядоченный набор 64-битных ключей. ID записи записывается в overlay как 16 заглавных hex-цифр.

Публичный `translations\ru_RU.jsonl` — source-free JSON Lines. Одна строка выглядит концептуально так:

```json
{"id":"16_HEX","source_sha256":"64_HEX","hint_sha256":"64_HEX_OR_NULL","translation":"Русский текст","status":"draft"}
```

- `id` связывает перевод с ключом записи, а не с позицией строки;
- `source_sha256` — SHA-256 точного UTF-8 английского текста; если EN изменился, запись становится stale и не применяется;
- `hint_sha256` — SHA-256 точной украинской подсказки контекста; приватный export/import и validator используют его для обнаружения изменившегося контекста;
- `translation` — русский plaintext; английский и украинский plaintext в overlay отсутствуют;
- `status` (`draft`, `reviewed`, `approved`) и остальные поля хранят provenance, риск и состояние вычитки.

`hint_sha256` не заменяет `source_sha256`: runtime-композиция привязана к EN, а exact-профиль дополнительно закрепляет весь UK base и итоговый raw LOC1. При подготовке release украинский hint надо передавать валидатору, чтобы изменение контекста не прошло незамеченным.

Композиция идёт по упорядоченным записям UK base:

1. Если EN-значение отсутствует/пусто, остаётся базовое UK/служебное значение (`base_fallback`).
2. Если найдены ID, актуальный `source_sha256`, разрешённый статус/политика и нет механических ошибок, записывается русский текст (`applied_ru`).
3. Отсутствующий, stale, запрещённый политикой или не прошедший validator перевод заменяется точным английским текстом (`english_fallback`), а причина считается отдельно.
4. Writer сохраняет header, locale identity и порядок ключей UK, пересобирает только значения и обязательную длину data section, затем повторно читает результат.
5. Exact-профиль закрепляет SHA-256 EN, UK, stamp, каталога, ожидаемого raw output и все composition counts. Несовпадение блокирует запись.

Постоянная цель внутри кэша игры только одна — `dl_uk_UA.bin`; `dl_en_US.bin` и stamp читаются, но не меняются. Во время атомарной транзакции рядом с target могут кратковременно появляться строго именованные temp/displaced/rollback-файлы; при гонке или сбое они сохраняются для контролируемого recovery, а не скрываются. Патчер не внедряется в процесс, не ставит hook/driver/service, не меняет EXE, DLL, память, аккаунт, сетевой протокол или подписанный пакет игры. Immutable backup, state и журналы транзакции хранятся отдельно под `%LOCALAPPDATA%\InvokersRussian\runtime-cache`.

## 11. Новая версия игры и новый exact-профиль

После обновления игры сначала выберите украинский язык, дождитесь загрузки и закройте игру. Обычная CLI-сборка может безопасно снять read-only профиль:

```powershell
dotnet build .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release
New-Item -ItemType Directory -Path '.\work\profiles' -Force | Out-Null

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\new-runtime-cache-profile.ps1 `
  -OutputProfile '.\work\profiles\runtime-cache-profile.new.json'
```

Генератор фиксирует stamp, GUID, версии контента, locale/release revisions, entry count и SHA-256 EN/UK/stamp, но намеренно создаёт `readiness=blocked` и `certified=false`. Это наблюдение, а не разрешение на запись.

Для сертификации нужны приватные файлы **из собственной официальной установки**, но они остаются только в `work\`:

- exact EN и UK контейнеры и stamp;
- schema `4`, content GUID/versions, locale IDs/revisions и release revisions;
- полный ordered key set и его SHA-256: для каждого target ID в LOC1-порядке берутся ASCII-байты `ID_HEX16 + "\n"`, включая LF после последнего ID, затем считается SHA-256;
- SHA-256 source containers и каталога;
- отчёт validator без blocking errors;
- counts `applied_ru`, `english_fallback`, `base_fallback`, missing/stale/rejected/policy/needs-review;
- SHA-256 итогового raw LOC1.

Проверочный output можно построить read-only командой CLI в новый файл, не указывая путь игры:

```powershell
dotnet run --project .\src\InvokersRu.Cli\InvokersRu.Cli.csproj -c Release -- build `
  --english $privateEnglish `
  --base $privateUkrainian `
  --translations .\translations\ru_RU.jsonl `
  --output .\work\profile-review\dl_uk_UA.ru.bin `
  --report .\work\profile-review\build-report.json `
  --include-draft --raw
```

После проверки output создайте exact compatibility input без ручного переноса хэшей и счётчиков:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\update-channel\New-SignedUpdateCompatibilityProfile.ps1 `
  -RuntimeProfile '.\work\profiles\runtime-cache-profile.new.json' `
  -EnglishLoc1 $privateEnglish `
  -BaseLoc1 $privateUkrainian `
  -Stamp $privateStamp `
  -Catalog '.\translations\ru_RU.jsonl' `
  -BuiltLoc1 '.\work\profile-review\dl_uk_UA.ru.bin' `
  -BuildReport '.\work\profile-review\build-report.json' `
  -Output '.\work\profile-review\signed-compatibility.json'
```

Генератор заново сверяет tuple, каталог, report, итоговый LOC1, composition counts и ordered keyset, затем прогоняет результат через production verifier/adapter. Он принимает только snapshots, пишет только новый output и не ищет установленную игру. Для публичного preview точные flags должны соответствовать выбранной policy; для настоящего release используется только прошедший человеческое review набор и release gate. Готовый JSON передаётся в подписанный data release по схеме из [update-channel.md](update-channel.md). Никогда не объявляйте профиль certified, пока не воспроизведены catalog hash, output hash и counts.

Новый подписанный data release позволяет уже установленному совместимому патчеру получить поддержку новой версии игры без скачивания нового EXE. Новый EXE нужен, если игра меняет пути, schema/layout LOC1, locale model, протокол обновлений, доверенный ключ или требуется исправление кода.

## 12. Передача перевода разработчикам игры

Репозиторий распространяется по MIT License, но не содержит и не предоставляет права на торговые марки или игровые assets. Разработчикам доступны два пути.

### Импорт overlay в официальный pipeline

1. Взять авторитетную EN-таблицу из собственного build pipeline.
2. Сопоставить 64-битный ключ с `id` из `translations\ru_RU.jsonl`.
3. Вычислить SHA-256 точных UTF-8 EN-байтов и применять запись только при совпадении `source_sha256`.
4. Выбрать допустимые статусы и провести человеческую вычитку `needs_review`, sensitive и context-required строк.
5. Для отсутствующих/stale записей оставить английский fallback и вернуть список ID сообществу.
6. Сформировать официальный RU asset в нативном pipeline и прогнать собственные UI/screenshot/functional тесты.

Если внутренняя система разработчиков не использует те же 64-битные ключи, нужен одноразовый mapping из их авторитетной таблицы. В репозитории нет plaintext EN, поэтому восстанавливать mapping только по хэшу нельзя и не нужно.

### Официальная локаль `ru_RU`

Предпочтительный продуктовый вариант — выделить отдельную русскую локаль, locale ID и `dl_ru_RU.bin`, добавить `ru_RU` в manifest/language selector и раздавать файл с официального CDN. Тогда украинский слот больше не переиспользуется, а community-патчер не нужен. Русские значения и review metadata можно импортировать из overlay, но header/revision/signing/distribution должны создаваться официальным pipeline.

Перед импортом разработчикам стоит получить:

- commit SHA и SHA-256 `translations\ru_RU.jsonl`;
- glossary/style guide и выбранную policy статусов;
- EN content GUID/version и ordered keyset hash, против которых сделан export;
- composition/validation report;
- список fallback/stale/missing/context-required ID;
- подтверждение лицензии и атрибуцию из `LICENSE`/`NOTICE.md`.

В Git не должны попадать vendor EN/UK таблицы, пользовательские кэши, собранный `dl_uk_UA.bin`, приватные translation jobs, signing PEM/state или персональные пути. Перед публикацией всегда проверяйте `git status --short` и список staged-файлов.

## 13. Короткий чек-лист аудитора

- Клон чистый, commit SHA записан.
- SDK совпадает с `global.json`.
- Обычная сборка и fixture-free smoke tests прошли.
- Signed-update self-test прошёл в PowerShell 5.1 и PowerShell 7 (если обе оболочки доступны).
- Payload создан только через `publish-windows-preview.ps1` в новом `work\...`.
- Встроены ожидаемые catalog/profile/public update config; приватных ключей нет.
- `BUILD-RECEIPT.json` и `PAYLOAD-SHA256.json` проверены.
- `test-windows-packaging.ps1` прошёл.
- Installer hash совпадает с `.sha256`; Authenticode трактуется честно как `Valid` или `NotSigned`.
- Сборка не запускала игру и не изменяла её файлы.
- Перед реальным применением выбран украинский язык, загрузка завершена, игра и лаунчер закрыты.
