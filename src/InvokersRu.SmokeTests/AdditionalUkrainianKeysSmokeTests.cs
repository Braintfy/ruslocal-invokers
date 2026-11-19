using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

namespace InvokersRu.SmokeTests
{
    internal static class AdditionalUkrainianKeysSmokeTests
    {
        internal static void Run(Action<string> passed)
        {
            string root = Path.Combine(Path.GetTempPath(), "invokersru-extra-uk-" + Guid.NewGuid().ToString("N"));
            string cache = Path.Combine(root, "cache");
            string state = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            try
            {
                string enPath = Path.Combine(cache, "dl_en_US.bin");
                string ukPath = Path.Combine(cache, "dl_uk_UA.bin");
                string stampPath = Path.Combine(cache, "dl_uk_UA.bin.ver");
                string catalogPath = Path.Combine(root, "ru_RU.jsonl");
                byte[] english = Make(1, new[] { "Open", "Exit" }, new ulong[] { 2, 4 });
                byte[] ukrainian = Make(8, new[] { "Подія", "Відкрити", "Порожнє", "Вийти" }, new ulong[] { 1, 2, 3, 4 });
                Loc1Document uk = Loc1Codec.Parse(ukrainian);
                uk.Entries[2].Value = null;
                ukrainian = Loc1Codec.BuildRaw(uk);
                File.WriteAllBytes(enPath, english);
                File.WriteAllBytes(ukPath, ukrainian);
                File.WriteAllText(stampPath, "0.60.1247", new UTF8Encoding(false));
                TranslationCatalog.WriteJsonLines(catalogPath, new[] {
                    Record(2, "Open", "Відкрити", "Открыть"),
                    // Even a catalogue candidate for a target-only key has no current EN authority.
                    Record(1, "Event", "Подія", "Событие") });
                byte[] catalog = File.ReadAllBytes(catalogPath);
                string pin = Hashing.Sha256Bytes(catalog);
                string family = Loc1Codec.Parse(english).ContentGuid;
                CompatibleRevisionProfileBuild Build() => CompatibleRevisionProfileBuilder.Build(
                    enPath, ukPath, stampPath, family, catalog, pin, "community-preview-all-drafts");
                RequireThrows(() => Loc1Compatibility.RequireComposableCorpus(Loc1Codec.Parse(english),
                    Loc1Codec.Parse(ukrainian), allowPerLocaleContentVersion: true),
                    "Default exact corpus rule silently accepted extra target keys.");
                CompatibleRevisionProfileBuild prepared = Build();
                Require(prepared.Profile.EntryCount == 4 && prepared.Composition.AppliedTranslations == 1
                    && prepared.Composition.BaseFallbacks == 2 && prepared.Composition.EnglishFallbacks == 1,
                    "Target-superset counts or key-based matching are incorrect.");
                Require(RuntimeCacheService.DescribeTuple(enPath, ukPath, stampPath, "extra-uk-observed").EntryCount == 4,
                    "Observed entry_count must describe the full Ukrainian target.");
                passed("compatible UK supersets retain target-only text/nulls; exact corpus rules remain strict and counts describe the complete target");

                foreach (byte[] invalid in new[] {
                    Make(8, new[] { "Відкрити" }, new ulong[] { 2 }),
                    Make(8, new[] { "Відкрити", "Інше" }, new ulong[] { 2, 5 }),
                    Make(8, new[] { "Подія", "Відкрити", "Інше" }, new ulong[] { 1, 2, 5 }) })
                {
                    File.WriteAllBytes(ukPath, invalid);
                    RequireThrows(() => Build(), "A target missing an English key was accepted.");
                }
                byte[] wrongLocale = (byte[])ukrainian.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(wrongLocale.AsSpan(8, 4), 2);
                File.WriteAllBytes(ukPath, wrongLocale);
                RequireThrows(() => Build(), "A non-Ukrainian target was accepted.");
                File.WriteAllBytes(ukPath, ukrainian);
                passed("extra UK support still rejects missing/replaced source keys and wrong target locale");

                if (!MutationCapability.IsTestWriteBuild) return;
                MutationPolicy.BindTestRuntimePaths(cache, state);
                RuntimeCacheInspection ready = RuntimeCacheService.Inspect(cache, prepared.Profile, state);
                Require(ready.Status == InstallationStatus.CompatibleOriginal, "Full superset inspection failed: " + ready.Message);
                Loc1Document changed = Loc1Codec.Parse(ukrainian);
                changed.Entries[0].Value = "Змінена подія";
                byte[] changedRaw = Loc1Codec.BuildRaw(changed);
                File.WriteAllBytes(ukPath, changedRaw);
                RequireThrows(() => RuntimeCacheService.Apply(ready, catalogPath, state), "Changed additional key evaded tuple pins.");
                Require(File.ReadAllBytes(ukPath).SequenceEqual(changedRaw) && !File.Exists(state), "Rejected write changed current files.");
                File.WriteAllBytes(ukPath, ukrainian);
                RuntimeCacheService.Apply(RuntimeCacheService.Inspect(cache, prepared.Profile, state), catalogPath, state);
                RuntimeCacheInspection installed = RuntimeCacheService.Inspect(cache, prepared.Profile, state);
                Require(installed.Status == InstallationStatus.PatchedByThisTool && installed.State != null,
                    "Installed superset was not recognized: " + installed.Message);
                Loc1Document output = Loc1Codec.ReadRawFile(ukPath);
                Require(output.Entries[0].Value == "Подія" && output.Entries[1].Value == "Открыть"
                    && output.Entries[2].Value == null && output.Entries[3].Value == "Exit"
                    && output.Entries.Select(e => e.KeyHash).SequenceEqual(Loc1Codec.Parse(ukrainian).Entries.Select(e => e.KeyHash))
                    && File.ReadAllBytes(installed.State!.BackupPath).SequenceEqual(ukrainian),
                    "Writer lost target-only keys, filled a null, matched by index, or changed the backup.");
                RuntimeCacheService.Restore(state, prepared.Profile);
                Require(File.ReadAllBytes(ukPath).SequenceEqual(ukrainian) && !File.Exists(state), "Superset restore is not byte-exact.");
                passed("UK-superset install rechecks additional-key tamper, survives recheck and restores the full original byte-for-byte");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static TranslationRecord Record(ulong id, string source, string hint, string translated) => new()
        {
            Id = id.ToString("X16"), SourceSha256 = Hashing.Sha256Text(source), HintSha256 = Hashing.Sha256Text(hint),
            Translation = translated, Status = "draft", Model = "synthetic", PromptVersion = "extra-uk-v1",
            Confidence = "high", NeedsReview = false, RiskFlags = Array.Empty<string>(), UpdatedAt = DateTimeOffset.UtcNow
        };

        private static byte[] Make(uint locale, string[] values, ulong[] keys)
        {
            byte[] raw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(locale, 68, locale + 100, values);
            int entries = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x20, 8)));
            for (int index = 0; index < keys.Length; index++)
                BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(entries + index * 16, 8), keys[index]);
            return raw;
        }
        private static void RequireThrows(Action action, string message)
        {
            try { action(); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; }
            throw new InvalidOperationException(message);
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
