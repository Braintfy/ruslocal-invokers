# Продолжение перевода на другой машине

Этот документ фиксирует безопасную точку передачи после Wave 1. На другой машине
выполняются перевод, статическая проверка и коммиты. На ПК с установленной игрой
после получения коммитов выполняется отдельная runtime-проверка.

Точка передачи закреплена в
[`translations/waves/continuation-after-wave-0001.json`](../translations/waves/continuation-after-wave-0001.json).
В репозитории нет оригинальных EN/UK строк, файлов игры, runtime-кэша или приватных
job/result-файлов.

## 1. Получить исходники

```powershell
git clone https://github.com/Braintfy/ruslocal-invokers.git
cd ruslocal-invokers
git checkout codex/full-translation-wave-01
git pull --ff-only

$sdk = dotnet --version
if (-not $sdk.StartsWith('10.')) {
  throw "Нужен .NET 10 SDK, найден: $sdk"
}

dotnet restore InvokersRu.sln --configfile NuGet.Config
dotnet build InvokersRu.sln -c Release --no-restore
dotnet run --project src/InvokersRu.SmokeTests -c Release --no-build --no-restore
```

Нужен .NET 10 SDK. Перед началом работы каталог
`translations/ru_RU.next.jsonl` должен иметь SHA-256:

```text
B209A7FCA0E8D27EE58635D574603E7CD9FC5741678C939113943147C83F07F6
```

## 2. Подготовить приватные EN/UK файлы

На другой машине нужно один раз запустить собственный клиент версии `0.60.1239`
и получить его runtime-файлы `dl_en_US.bin` и `dl_uk_UA.bin`. Их можно также
перенести с тестового ПК приватно, но нельзя добавлять в Git.

Ожидаемые SHA-256:

```text
dl_en_US.bin  ECC39461923BA6C8DDF339B5EBFE719220FC0B4EC19E5469C1FAE427CD0CA6EA
dl_uk_UA.bin  0EF3FB568835E746FCE88094B5E7B48F13D7E7ABE182FFC47958671391DE5A46
```

Скопируйте их в игнорируемый каталог:

```text
work/private-runtime/dl_en_US.bin
work/private-runtime/dl_uk_UA.bin
```

Если хэш отличается, не продолжайте старую очередь: это другая версия корпуса,
для которой нужно заново оценить совместимость.

## 3. Восстановить очередь побайтово

```powershell
$dotnet = 'dotnet'
$cli = 'src\InvokersRu.Cli\bin\Release\net10.0\InvokersRu.Cli.dll'
$run = 'work\full-translation\0.60.1239-after-wave1-guarded'

& $dotnet $cli jobs `
  --english work\private-runtime\dl_en_US.bin `
  --ukrainian work\private-runtime\dl_uk_UA.bin `
  --translations translations\ru_RU.next.jsonl `
  --output "$run\missing.jobs.jsonl"

$queueSha = (Get-FileHash "$run\missing.jobs.jsonl" -Algorithm SHA256).Hash
if ($queueSha -ne 'E9BE23E480358CFFCF4D24DBFCE3F1AEAE872FB9DF2493D6AC255DC14306285F') {
  throw "Очередь не совпала с точкой передачи: $queueSha"
}
```

Ожидаемый результат: 24 467 дедуплицированных заданий для 38 602 ID;
101 чувствительный ID остаётся на официальном английском.

## 4. Восстановить lanes и первый набор чанков

На чистом `work/`, где указанные выходные каталоги ещё не существуют:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\partition-translation-jobs.ps1 `
  -InputJobs "$run\missing.jobs.jsonl" `
  -OutputDirectory "$run\lanes"

powershell -ExecutionPolicy Bypass -File scripts\split-work-items.ps1 `
  -InputJobs "$run\lanes\lane-01-short-plain.jobs.jsonl" `
  -OutputDirectory "$run\chunks-lane-01" `
  -PromptPath prompts\translation-system.ru-v2.md `
  -GlossaryPath localization\glossary.ru.json `
  -ChunkSize 150 `
  -MaxCharacters 60000
```

Ожидается 41 pending-чанк. SHA-256 `chunks.manifest.json`:

```text
D93D6788FFC5574F003D7881FB7CBF6E160B9CBC78A30B248EC517F87A030E53
```

Первый незавершённый чанк — `chunk-0001`. После обрыва прошлой задачи готовых,
но не импортированных result-файлов не осталось.

## 5. Перевод и публикация следующей волны

- Один чанк обрабатывается одной задачей Codex; массовый draft делает Terra.
- `job_id`, placeholders, теги, числа, единицы и переносы сохраняются точно.
- Модель всегда выставляет `draft`; `reviewed/approved` назначает только человек.
- Jobs, checkpoints и results остаются в `work/` и никогда не коммитятся.
- После нескольких полностью проверенных чанков используется manifest-driven импорт
  из [translation-workflow.md](translation-workflow.md).
- В Git попадают только обновлённый `translations/ru_RU.next.jsonl`, source-free
  receipt/QA-файл и при необходимости glossary/docs.

Перед push:

```powershell
dotnet build InvokersRu.sln -c Release --no-restore
dotnet run --project src/InvokersRu.SmokeTests -c Release --no-build --no-restore
dotnet src\InvokersRu.Cli\bin\Release\net10.0\InvokersRu.Cli.dll validate `
  --english work\private-runtime\dl_en_US.bin `
  --ukrainian work\private-runtime\dl_uk_UA.bin `
  --translations translations\ru_RU.next.jsonl `
  --include-draft `
  --per-locale-content-version
powershell -ExecutionPolicy Bypass -File scripts\test-import-translation-wave.ps1
git status --short
```

Проверьте, что `git status` не показывает `work/`, `*.bin`, игровые assets,
скриншоты или приватные jobs/results.

Коммит следующей волны делается точным списком файлов, без `git add .`:

```powershell
git add -- translations/ru_RU.next.jsonl `
  translations/waves/wave-0002.import.receipt.json `
  translations/waves/wave-0002.qa.json
git diff --cached --check
git commit -m "Add Russian translation wave 2"
git push origin codex/full-translation-wave-01
```

Если имена receipt-файлов отличаются, подставьте фактические source-free файлы,
которые создала и проверила текущая волна.

## 6. Получить перевод на игровом ПК

После push с переводческой машины:

```powershell
git checkout codex/full-translation-wave-01
git pull --ff-only
```

Новый каталог сначала проходит build/smoke/validate и только затем получает новый
exact runtime-профиль. Старый профиль нельзя применять к новому SHA каталога.
`cache-apply` не запускается автоматически: установку и скриншот-QA выполняем
отдельно на ПК с клиентом после проверки новой сборки.
