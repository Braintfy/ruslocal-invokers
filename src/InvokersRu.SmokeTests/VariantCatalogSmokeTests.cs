using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.SmokeTests
{
    internal static class VariantCatalogSmokeTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        internal static void Run(Action<string> passed)
        {
            HistoricalSelectionAndFallback();
            passed("variant catalogs preserve exact historical source/context pairs and policy metadata");
            MalformedVariants();
            passed("variant catalogs reject conflicting tuples, foreign IDs, nesting, and per-ID/global overflow");
            HistoricalProfile();
            passed("variant profiles compose known older sources while ignoring IDs added in a newer corpus");
        }

        private static void HistoricalSelectionAndFallback()
        {
            TranslationRecord current = Record(1, "Gain 20%", "Отримати 20%", "Получить 20%");
            TranslationRecord historical = Record(1, "Gain 10%", "Отримати 10%", "Получить 10%");
            historical.Status = "reviewed";
            historical.NeedsReview = true;
            historical.ReviewerIds = new[] { "historical-reviewer" };
            historical.ReviewRevision = "historical-r1";
            current.Variants = new[] { historical };
            TranslationRecord open = Record(2, "Open", "Відкрити нове", "Открыть");
            open.Variants = new[] { Record(2, "Open", "Відкрити", "Открыть") };
            TranslationRecord removed = Record(3, "Previously known", "Відоме", "Известное");
            TranslationRecord invalid = Record(4, "Value {0}", "Значення {0}", "Значение");
            TranslationCatalog catalog = Load(current, open, removed, invalid);
            Require(catalog.Count == 4 && catalog.VariantCount == 2 && catalog.AllRecords.Count() == 6,
                "Signed top-level record counts were inflated by historical variants.");
            Require(catalog.TryGetUsableForHint(1, "Gain 10%", "Отримати 10%", true,
                out TranslationRecord? selected, out _, requireExactHint: true)
                && selected!.Translation == "Получить 10%"
                && selected.NeedsReview && selected.Status == "reviewed"
                && selected.ReviewerIds.SequenceEqual(new[] { "historical-reviewer" })
                && selected.ReviewRevision == "historical-r1",
                "Historical context selection borrowed current review metadata.");
            Require(!catalog.TryGetUsableForHint(1, "Gain 10%", "Отримати 20%", true,
                out _, out string mismatch, requireExactHint: true) && mismatch == "stale-hint",
                "A historical source borrowed a hint belonging to the newer English source.");
            Require(!catalog.TryGetUsableForHint(1, "Gain 30%", "Отримати 10%", true,
                out _, out string unknown, requireExactHint: true) && unknown == "stale-source",
                "An unknown English source was overwritten by a historical translation.");

            Loc1Document english = Document(1, "Gain 10%", "Open", "Never translated", "Value {0}");
            Loc1Document target = Document(8, "Отримати 10%", "Відкрити", "Нове", "Значення {0}");
            CompositionSummary composition = TranslationComposer.Apply(english, target, catalog,
                includeDraft: true, requireExactHint: true);
            Require(composition.AppliedTranslations == 2 && composition.EnglishFallbacks == 2
                && composition.StaleCatalogRecords == 1 && composition.RejectedCatalogRecords == 1
                && target.Entries[0].Value == "Получить 10%"
                && target.Entries[1].Value == "Открыть"
                && target.Entries[2].Value == "Never translated"
                && target.Entries[3].Value == "Value {0}",
                "Historical composition applied unknown or token-invalid translations.");
            target = Document(8, "Отримати 10%", "Відкрити", "Нове", "Значення {0}");
            composition = TranslationComposer.Apply(english, target, catalog, includeDraft: true,
                excludeNeedsReview: true, requireExactHint: true);
            Require(composition.AppliedTranslations == 1 && composition.NeedsReviewFallbacks == 1,
                "A historical needs-review flag was bypassed by the primary record's policy.");

            TranslationRecord context = Record(1, "Open", "Двері", "Открыть дверь");
            context.Variants = new[] { Record(1, "Open", "Скриня", "Открыть сундук") };
            TranslationCatalog contexts = Load(context);
            Require(contexts.TrySelectRecord(1, "Open", "Скриня", false, out selected, out _)
                && selected!.Translation == "Открыть сундук"
                && !contexts.TrySelectRecord(1, "Open", "Невідоме", false, out _, out string ambiguous)
                && ambiguous == "ambiguous-source",
                "Exact composition selected an arbitrary contextual variant without its hint.");
            TranslationRecord plain = Record(1, "Open", "Старе", "Открыть");
            TranslationCatalog legacy = Load(plain);
            Require(legacy.TryGetUsable(1, "Open", true, out selected, out _)
                && selected!.Translation == "Открыть" && legacy.VariantCount == 0
                && !JsonSerializer.Serialize(plain, JsonOptions).Contains("variants", StringComparison.Ordinal),
                "Legacy records acquired an incompatible variants property or lost no-hint composition.");
        }

        private static void MalformedVariants()
        {
            TranslationRecord parent = Record(1, "Open", "Відкрити", "Открыть");
            parent.Variants = new[] { Record(1, "Open", "Відкрити", "Конфликт") };
            Reject(() => Load(parent));
            parent.Variants = new[] { Record(2, "Exit", "Вийти", "Выйти") };
            Reject(() => Load(parent));
            TranslationRecord nested = Record(1, "Exit", "Вийти", "Выйти");
            nested.Variants = Array.Empty<TranslationRecord>();
            parent.Variants = new[] { nested };
            Reject(() => Load(parent));
            parent.Variants = new TranslationRecord[] { null! };
            Reject(() => Load(parent));
            parent.Variants = Enumerable.Range(0, TranslationCatalog.MaximumVariantsPerRecord + 1)
                .Select(index => Record(1, "Source " + index, "Hint " + index, "Текст " + index)).ToArray();
            Reject(() => Load(parent));

            // The total bound prevents a valid per-ID array from multiplying the signed record cap.
            var oversized = new StringBuilder();
            int parentCount = TranslationCatalog.MaximumHistoricalVariants / TranslationCatalog.MaximumVariantsPerRecord + 1;
            for (int index = 1; index <= parentCount; index++)
            {
                TranslationRecord row = Record((ulong)index, "Primary", "Primary", "Текст");
                row.Variants = Enumerable.Range(0, TranslationCatalog.MaximumVariantsPerRecord)
                    .Select(variant => Record((ulong)index, "Variant " + variant, "Hint", "Текст")).ToArray();
                oversized.AppendLine(JsonSerializer.Serialize(row, JsonOptions));
            }
            byte[] oversizedBytes = Encoding.UTF8.GetBytes(oversized.ToString());
            Reject(() => TranslationCatalog.LoadJsonLinesBytes(oversizedBytes));
        }

        private static void HistoricalProfile()
        {
            string root = Path.Combine(Path.GetTempPath(), "invokersru-variant-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                byte[] english = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(1, 3, 123,
                    new[] { "Gain 10%" });
                byte[] ukrainian = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(8, 5, 456,
                    new[] { "Отримати 10%" });
                string englishPath = Path.Combine(root, "dl_en_US.bin");
                string basePath = Path.Combine(root, "dl_uk_UA.bin");
                string stampPath = Path.Combine(root, "dl_uk_UA.bin.ver");
                File.WriteAllBytes(englishPath, english);
                File.WriteAllBytes(basePath, ukrainian);
                File.WriteAllText(stampPath, "0.61.synthetic", new UTF8Encoding(false));
                Loc1Document source = Loc1Codec.Parse(english);
                TranslationRecord current = Record(source.Entries[0].KeyHash, "Gain 20%", "Отримати 20%", "Получить 20%");
                current.Variants = new[] { Record(source.Entries[0].KeyHash, "Gain 10%", "Отримати 10%", "Получить 10%") };
                TranslationRecord newerKey = Record(ulong.MaxValue - 1, "New key", "Нове", "Новое");
                byte[] catalogBytes = Serialize(current, newerKey);
                TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(catalogBytes);
                ValidationReport validation = TranslationValidator.Validate(source, catalog, true,
                    Loc1Codec.Parse(ukrainian), allowPerLocaleContentVersion: true);
                Require(validation.ErrorCount == 0 && validation.FreshRecords == 1
                    && validation.UsableRecords == 1 && validation.MissingSourceIds == 1,
                    "A known older source was treated as stale or blocked by a newer catalog-only ID.");
                CompatibleRevisionProfileBuild build = CompatibleRevisionProfileBuilder.Build(
                    englishPath, basePath, stampPath, source.ContentGuid, catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes), "community-preview-all-drafts");
                Require(build.Composition.AppliedTranslations == 1 && build.Profile.Certified
                    && build.Profile.Readiness == "ready" && build.Validation.ErrorCount == 0,
                    "Historical source/context materialization did not produce a certified partial profile.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static TranslationRecord Record(ulong id, string source, string hint, string translation) => new TranslationRecord
        {
            Id = id.ToString("X16"),
            SourceSha256 = Hashing.Sha256Text(source),
            HintSha256 = Hashing.Sha256Text(hint),
            Translation = translation,
            Status = "draft",
            RiskFlags = TranslationValidator.ClassifyRisks(source).ToArray(),
            UpdatedAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z")
        };

        private static Loc1Document Document(uint locale, params string[] values) => new Loc1Document(
            Array.Empty<byte>(), 4, locale, 10, locale, 0, 0, "0.61.2", "Prod_0.61.2_10",
            values.Select((value, index) => new Loc1Entry(index, (ulong)index + 1, 0, 0, value)).ToArray());

        private static TranslationCatalog Load(params TranslationRecord[] records) =>
            TranslationCatalog.LoadJsonLinesBytes(Serialize(records));

        private static byte[] Serialize(params TranslationRecord[] records) => Encoding.UTF8.GetBytes(
            string.Join("\n", records.Select(record => JsonSerializer.Serialize(record, JsonOptions))) + "\n");

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Malformed or unbounded historical variants were accepted.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
