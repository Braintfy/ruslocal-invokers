# Архитектура PC-патчера

## Release gates

`apply` разрешается только когда одновременно выполнено всё:

1. ровно один встроенный или проверенный подписанный runtime-профиль совпал с exact tuple;
2. профиль имеет `readiness=ready` и `certified=true`;
3. pinned EN/UK raw LOC1 и version-stamp hashes совпали;
4. content GUID, content versions, locale ID/revision, entry count и schema совпали;
5. exact translation catalog SHA-256 закреплён в certified entry;
6. release profile принял только approved каталог с полным несекретным coverage;
7. игра и launcher закрыты;
8. нет активного transaction journal;
9. verified immutable backup создан вне game tree;
10. raw SHA-256 ожидаемого результата закреплён в runtime-профиле;
11. staged output повторно распарсен и сравнен с закреплённым hash;
12. весь pinned tuple повторно проверен под lock непосредственно перед atomic replace.

Не предусмотрено fuzzy matching, nearest build, `--force`, отключение updater или снятие блокировки вручную. В unsigned dev binary `apply`, `restore` и `recover` дополнительно выключены общим compile-time gate независимо от редактируемого JSON. `plan` при выключенном gate никогда не сообщает `READY_TO_APPLY`.

Исторический первый контролируемый тест использовал compile-time профиль `supervised-safe-drafts` и ровно 576 допустимых переводов для `0.60.1239`. Этот этап сохранён в receipts как доказательство ранней транзакции, но не является текущим публичным профилем.

Windows 3.1 содержит резервный exact-профиль `0.60.1247` и может выбирать более новый exact-профиль с каталогом из фиксированного подписанного канала. Endpoint и публичный ECDSA-ключ встроены в CLI; manifest проходит проверку подписи, срока, монотонного `sequence`, отзыва, размеров и SHA-256. Профиль новой версии всё равно должен быть подготовлен и опубликован: fuzzy/nearest-build установка не появляется. Текущий `community-preview-all-drafts` устанавливает 41 037 source-bound машинных строк из 41 292 и остаётся preview до человеческой вычитки.

## Транзакция

State хранится в `%LOCALAPPDATA%/InvokersRussian/runtime-cache/`, вне runtime-кэша игры:

```text
state.v1.json
patcher.lock
backups/<profile-id>/<source-sha>.dl_uk_UA.bin
journals/<transaction-id>.json
```

Apply проходит фазы:

```text
Prepared
→ BackupVerified
→ StagedVerified
→ PreCommitVerified
→ ReplacementCommitted
→ DisplacedVerified
→ PostCommitVerified
→ StateCommitted
→ Completed
```

Journal и state пишутся через temporary file + flush-to-disk + atomic replace. Целевой raw cache staging-ится в том же каталоге/volume и меняется через `File.Replace`; реально вытеснённый файл сначала остаётся в уникальном quarantine и проверяется по hash. Неатомарного fallback нет. Named mutex и lockfile блокируют второй патчер.

Перед транзакцией патчер перечисляет процессы и отказывает, если image path любого процесса находится внутри canonical game/launcher roots. Для процессов, чей путь недоступен, дополнительно проверяются известные имена официальной игры и launcher. Патчер ничего не завершает сам.

Текущий write-профиль допускает только готовый локальный fixed NTFS volume. Существующие компоненты game root, целевого файла и state root проверяются на `ReparsePoint`; сетевые диски, junction/symlink-пути и не-NTFS не получают небезопасного fallback.

`recover` не доверяет фазе как источнику истины. Он сравнивает target с `source_sha256` и `expected_output_sha256`:

- target == source: commit не случился, journal очищается;
- target == expected output: state завершается или restore фиксируется;
- иначе: отказ и официальный repair flow.

## LOC1 schema 4

Все числа little-endian:

| Offset | Поле |
|---|---|
| `0x00` | magic `LOC1` |
| `0x04` | schema `4` |
| `0x08` | language ID |
| `0x0C` | opaque locale revision/content ID |
| `0x10` | opaque locale revision/content ID |
| `0x14` | .NET ticks |
| `0x1C` | entry count |
| `0x20` | index offset/header size |
| `0x28` | data offset |
| `0x30` | data length |
| `0x38` | collision area/count, текущая сборка all-zero |
| `0x40` | collision offset |
| `0x48` | key-blob offset |
| `0x50` | length-prefixed content UUID |
| following | length-prefixed content release |

Index record: `u64 keyHash + u32 valueOffset + u32 UTF8ByteLength`. Sentinel отсутствующего значения: `FFFFFFFF/0`.

`keyHash` — это **FNV-1a 64-bit от UTF-8-представления строкового ключа локализации** (разгадано 20.08.2026 сопоставлением ключей из IL2CPP-метаданных Android-сборки со значениями в `dl_en_US.bin`):

```python
h = 0xCBF29CE484222325
for byte in key.encode("utf-8"):
    h = ((h ^ byte) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
```

Проверка: `ui-multibattle-exit-popup-title` → `"Stop Multi-Battle?"`, `ui-player-profile-avatar-frame-unlock-vip-level` → `"Unlocks at VIP Pass Level {0}"`. Это даёт возможность восстанавливать осмысленное имя строки по её хэшу, что полезно для контекста при переводе и для экранного QA: префикс ключа указывает на экран. Патчеру функция не нужна — он работает с готовым набором хэшей, — но для инструментов анализа она снимает главное белое пятно формата. Подробности: [android-client.md](android-client.md).

Текущий conservative writer поддерживает только пустой collision/key-blob layout, строго возрастающие уникальные hashes и не заполняет sentinel. Для профиля `0.60.1247` сохраняются locale ID `8`, закреплённая ревизия, content version `Prod_0.60.0_68` и 41 292 ключа. String pool пакуется детерминированно; release tooling повторно проверяет полный raw SHA-256 и композицию до публикации exact-профиля.

## Что ещё нужно до stable/beta

- вычитать и провести экранный QA для 23 434 записей с `needs_review=true`;
- повторять loader acceptance test и публиковать новый signed exact-профиль после каждого содержательного обновления игры;
- добавить защищённый Windows CI/release workflow и воспроизводимую provenance/attestation;
- получить Authenticode-подпись GUI, CLI и установщика;
- при необходимости добавить Restart Manager и дополнительный path-by-handle hardening, не ослабляя текущие fail-closed проверки.
