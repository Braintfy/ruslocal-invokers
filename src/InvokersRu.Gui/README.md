# InvokersRu GUI

`InvokersRu.Gui.exe` — русскоязычная Windows-оболочка над version-aware runtime-cache патчером. GUI не изменяет файлы игры самостоятельно: он вызывает соседний supervised `InvokersRu.Cli.exe`, получает строгий JSON-план и разрешает только точную операцию, подтверждённую встроенным профилем.

> **Перед русификацией:** выберите в игре **украинский язык**, дождитесь загрузки текста, затем полностью закройте игру и лаунчер, включая его значок в системном трее.

## Что делает оболочка

1. Автоматически проверяет стандартный кэш `%USERPROFILE%\AppData\LocalLow\Hit_Zone\Invokers\i18n`.
2. Показывает обнаруженную версию игры отдельно от версии, поддерживаемой патчером.
3. Сверяет EN/UK LOC1, stamp версии, каталог перевода и ожидаемый результат по SHA-256.
4. Разрешает `cache-apply`, `cache-restore` или `cache-recover` только при однозначном состоянии и закрытых игре/лаунчере.
5. Не имеет `force`-режима, не завершает процессы игры, не требует администратора и не внедряется в игру.

В текущем профиле `0.60.1247 / Prod_0.60.0_68` русский занимает украинский языковой слот. Перед установкой интерфейс показывает точное покрытие и предупреждает, что перевод сообщества ещё редактируется.

## Файлы поставки

- `InvokersRu.Gui.exe` — интерфейс;
- `InvokersRu.Cli.exe` и его self-contained зависимости — проверяемая транзакционная логика;
- `translations\ru_RU.jsonl` — каталог, закреплённый встроенным профилем CLI;
- `config\runtime-cache-profile.0.60.1247.json` — публичная копия профиля для аудита;
- `BUILD-RECEIPT.json`, `PAYLOAD-SHA256.json`, `PREVIEW-README.txt` — сведения о сборке и хэшах.

## Сборка

Обычный `dotnet build` создаёт диагностическую сборку без права записи:

```powershell
dotnet build .\src\InvokersRu.Gui\InvokersRu.Gui.csproj -c Release
```

Полный self-contained Windows-пакет с supervised CLI создаётся только корневым скриптом:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-windows-preview.ps1
```

Старый `src\InvokersRu.Gui\publish.ps1` намеренно завершает работу с ошибкой, чтобы случайно не собрать несовместимый StreamingAssets-пакет.
