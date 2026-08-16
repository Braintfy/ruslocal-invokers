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

namespace InvokersRu.SmokeTests
{
    internal static class Program
    {
        private static readonly List<string> Passed = new List<string>();

        public static int Main(string[] args)
        {
            try
            {
                string fixtureDirectory = args.Length > 0
                    ? Path.GetFullPath(args[0])
                    : Path.GetFullPath(Path.Combine("work", "private-fixtures", "Prod_0.60.0_26"));
                string enCompressed = Path.Combine(fixtureDirectory, "en_US.bin.br");
                string ukCompressed = Path.Combine(fixtureDirectory, "uk_UA.bin.br");
                string enRawFallback = Path.GetFullPath(Path.Combine("work", "i18n-analysis", "en_US.bin"));
                string ukRawFallback = Path.GetFullPath(Path.Combine("work", "i18n-analysis", "uk_UA.bin"));

                string englishPath = File.Exists(enCompressed) ? enCompressed : enRawFallback;
                string ukrainianPath = File.Exists(ukCompressed) ? ukCompressed : ukRawFallback;
                Require(File.Exists(englishPath) && File.Exists(ukrainianPath), "Private EN/UK fixtures are required.");

                RoundTrip(englishPath, "A76878590A54A9232F7ADA73653171E1E467238DFE0801BD264304541BF2724D", 1, 0xC3CCA66A);
                RoundTrip(ukrainianPath, "617EDC140A4495B40F51FD5AC07E8FCCDC17A834161AB767FF4BF9AB63BE825F", 8, 0xF7BC8460);
                MutationRoundTrip(ukrainianPath);
                ValidatorChecks();
                string runtimeEnglishPath = Path.GetFullPath(Path.Combine("work", "hash-analysis", "runtime-cache-official", "dl_en_US.bin"));
                string runtimeUkrainianPath = Path.GetFullPath(Path.Combine("work", "hash-analysis", "runtime-cache-official", "dl_uk_UA.bin"));
                SensitiveAccountRiskClassificationChecks(File.Exists(runtimeEnglishPath) ? runtimeEnglishPath : englishPath);
                HintStructureSafetyChecks(
                    File.Exists(runtimeEnglishPath) && File.Exists(runtimeUkrainianPath) ? runtimeEnglishPath : englishPath,
                    File.Exists(runtimeEnglishPath) && File.Exists(runtimeUkrainianPath) ? runtimeUkrainianPath : ukrainianPath);
                SafeDraftEligibilityChecks();
                CompatibilityManifestSafetyChecks();
                RuntimeCacheProfileParsingCheck();
                CompositionCheck(englishPath, ukrainianPath);
                PerLocaleVersionCompatibilityCheck(englishPath, ukrainianPath);
                ResultImportCheck(englishPath);
#if INVOKERSRU_MUTATION_SMOKES
                TransactionalPatcherCheck(englishPath, ukrainianPath, Path.Combine(fixtureDirectory, "manifest.dat"));
                string runtimeFixtureRoot = Path.GetFullPath(Path.Combine("work", "hash-analysis", "runtime-cache-official"));
                RuntimeCacheTransactionalCheck(
                    Path.Combine(runtimeFixtureRoot, "dl_en_US.bin"),
                    Path.Combine(runtimeFixtureRoot, "dl_uk_UA.bin"));
#else
                OrdinaryCoreMutationGateCheck();
#endif

                Console.WriteLine($"PASS: {Passed.Count} smoke tests");
                foreach (string message in Passed) Console.WriteLine($"  {message}");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: {exception}");
                return 1;
            }
        }

        private static void RoundTrip(string path, string expectedRawHash, uint expectedLocale, uint expectedLocaleRevision)
        {
            byte[] original = Loc1Codec.ReadContainer(path);
            Require(Hashing.FixedEqualsHex(Hashing.Sha256Bytes(original), expectedRawHash), $"Unexpected raw fixture hash for {Path.GetFileName(path)}.");
            Loc1Document document = Loc1Codec.Parse(original);
            Require(document.LocaleId == expectedLocale, "Unexpected locale id.");
            Require(document.LocaleRevision == expectedLocaleRevision, "Unexpected locale revision.");
            Require(document.Entries.Count == 41282, "Unexpected entry count.");
            Require(document.HeaderSize == 136 && document.DataOffset == 660648, "Unexpected LOC1 boundaries.");
            byte[] rebuilt = Loc1Codec.BuildRaw(document);
            Require(original.SequenceEqual(rebuilt), $"Raw rebuild is not byte-identical for locale {expectedLocale}.");
            Passed.Add($"byte-identical LOC1 round-trip locale {expectedLocale}");
        }

        private static void MutationRoundTrip(string path)
        {
            Loc1Document document = Loc1Codec.ReadFile(path);
            Loc1Entry target = document.Entries.First(entry => entry.Value != null && entry.Value.Length > 3 && TranslationValidator.ExtractProtectedTokens(entry.Value).Count == 0);
            string changed = target.Value + " ТЕСТ";
            target.Value = changed;
            byte[] rebuilt = Loc1Codec.BuildRaw(document);
            Loc1Document parsed = Loc1Codec.Parse(rebuilt);
            Require(parsed.IndexByHash()[target.KeyHash].Value == changed, "Mutated value did not survive rebuild.");
            byte[] compressed = Loc1Codec.Compress(rebuilt);
            string temp = Path.Combine(Path.GetTempPath(), $"invokersru-{Guid.NewGuid():N}.bin.br");
            try
            {
                File.WriteAllBytes(temp, compressed);
                Require(Loc1Codec.ReadContainer(temp).SequenceEqual(rebuilt), "Brotli round-trip changed LOC1 bytes.");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            Passed.Add("edited value and Brotli round-trip");
        }

        private static void RuntimeCacheProfileParsingCheck()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "runtime-cache-profile-under-test.json");
            Require(File.Exists(path), "Runtime-cache profile-under-test was not copied to the smoke output.");
            string json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
            RuntimeCacheCompatibility profile = RuntimeCacheCompatibility.Parse(json);
            Require(profile.EnglishLocaleId == 1
                && profile.EnglishLocaleRevision == 0xF458F128
                && profile.EnglishReleaseRevision == 57
                && profile.BaseLocaleId == 8
                && string.Equals(profile.StampValue, profile.GameVersion, StringComparison.Ordinal),
                "Runtime-cache profile-under-test lost the fixed EN/UK/stamp identity pins.");
            Passed.Add($"runtime-cache profile parses with fixed EN identity ({profile.Id})");
        }

        private static void ValidatorChecks()
        {
            var valid = new List<TranslationIssue>();
            TranslationValidator.ValidatePair("0000000000000001", "Deal 25% <b>{0}</b> damage\\nNow", "Нанести 25% <b>{0}</b> урона\\nСейчас", valid);
            Require(valid.All(issue => issue.Severity != ValidationSeverity.Error), "Valid protected tokens were rejected.");
            var invalid = new List<TranslationIssue>();
            TranslationValidator.ValidatePair("0000000000000002", "Deal <b>{0}</b> damage", "Нанести {1} урона", invalid);
            Require(invalid.Any(issue => issue.Code == "protected-token-mismatch" && issue.Severity == ValidationSeverity.Error), "Broken protected tokens were accepted.");
            var invalidNumbersAndNesting = new List<TranslationIssue>();
            TranslationValidator.ValidatePair("0000000000000003", "Deal 25% <b><i>now</i></b>", "Нанести 30% <b><i>сейчас</b></i>", invalidNumbersAndNesting);
            Require(invalidNumbersAndNesting.Any(issue => issue.Code == "numeric-token-mismatch"), "Changed literal number was accepted.");
            Require(invalidNumbersAndNesting.Any(issue => issue.Code == "rich-text-nesting"), "Broken rich-text nesting was accepted.");
            Require(!TranslationValidator.RequiresContextReview("Deal 25% <b>{0}</b>\nNow", "Завдати 25% <b>{0}</b>\nЗараз"), "Structurally matching locale hint was escalated.");
            Require(TranslationValidator.RequiresContextReview("Ready", "Подія {0}"), "A placeholder mismatch between locale revisions was accepted.");
            Require(TranslationValidator.RequiresContextReview("First {0}, then {1}", "Спочатку {1}, потім {0}"), "Protected-token order mismatch was accepted.");
            Require(TranslationValidator.RequiresContextReview("<b>Ready</b>", "<i>Готово</i>"), "Rich-tag mismatch between locale revisions was accepted.");
            Require(TranslationValidator.RequiresContextReview("Chance 25%", "Шанс 30%"), "Literal-number mismatch between locale revisions was accepted.");
            Require(TranslationValidator.RequiresContextReview("Ready in 3s", "Готово через 3m"), "Mechanic-unit mismatch between locale revisions was accepted.");
            Require(TranslationValidator.RequiresContextReview("First\nSecond", "Перший Другий"), "Literal-newline mismatch between locale revisions was accepted.");
            Passed.Add("placeholder and rich-text validation");
        }

        private static void SafeDraftEligibilityChecks()
        {
            TranslationRecord Eligible(string source) => new TranslationRecord
            {
                Id = "0000000000000001",
                SourceSha256 = Hashing.Sha256Text(source),
                Translation = "Открыть",
                Status = "draft",
                Model = "gpt-5.6-terra",
                PromptVersion = "ru-runtime-v1",
                Confidence = "high",
                NeedsReview = false,
                RiskFlags = TranslationValidator.ClassifyRisks(source).ToArray(),
                ReviewStage = "terra_done"
            };

            const string source = "Open";
            Require(RuntimeSafeDraftPolicy.IsEligible(Eligible(source), source, null, out _), "Valid supervised safe draft was rejected.");
            TranslationRecord low = Eligible(source);
            low.Confidence = "medium";
            Require(!RuntimeSafeDraftPolicy.IsEligible(low, source, null, out _), "Medium-confidence safe draft was accepted.");
            TranslationRecord provenance = Eligible(source);
            provenance.Model = "external-model";
            Require(!RuntimeSafeDraftPolicy.IsEligible(provenance, source, null, out _), "Unknown draft provenance was accepted.");
            const string sensitiveSource = "Delete your account";
            Require(!RuntimeSafeDraftPolicy.IsEligible(Eligible(sensitiveSource), sensitiveSource, null, out _), "Sensitive safe draft was accepted.");
            TranslationRecord reviewedSensitive = Eligible(sensitiveSource);
            reviewedSensitive.Status = "reviewed";
            Require(!RuntimeSafeDraftPolicy.IsPreviewEligible(reviewedSensitive, sensitiveSource, null, out _), "Reviewed sensitive text without legal and dual-review approval was preview-eligible.");
            reviewedSensitive.LegalApproved = true;
            reviewedSensitive.ReviewerIds = new[] { "reviewer-a", "reviewer-b" };
            Require(RuntimeSafeDraftPolicy.IsPreviewEligible(reviewedSensitive, sensitiveSource, null, out _), "Legally approved dual-reviewed sensitive text was rejected from preview.");
            TranslationRecord riskMismatch = Eligible(source);
            riskMismatch.RiskFlags = new[] { "numeric" };
            Require(!RuntimeSafeDraftPolicy.IsEligible(riskMismatch, source, null, out _), "Mismatched risk metadata was accepted.");
            TranslationRecord context = Eligible(source);
            context.RiskFlags = new[] { "context_required" };
            Require(!RuntimeSafeDraftPolicy.IsEligible(context, source, null, out _), "Context-required draft without screenshot QA was accepted.");
            TranslationRecord boundHint = Eligible(source);
            boundHint.HintSha256 = Hashing.Sha256Text("Контекст");
            Require(!RuntimeSafeDraftPolicy.IsEligible(boundHint, source, null, out string missingHintReason)
                && missingHintReason == "hint-binding", "A hint-bound record was accepted without the current hint.");
            TranslationRecord unboundHint = Eligible(source);
            Require(!RuntimeSafeDraftPolicy.IsEligible(unboundHint, source, "Контекст", out string unexpectedHintReason)
                && unexpectedHintReason == "hint-binding", "A record without a hint binding accepted a current hint.");
            TranslationRecord mismatchedHint = Eligible(source);
            mismatchedHint.HintSha256 = Hashing.Sha256Text("Контекст A");
            Require(!RuntimeSafeDraftPolicy.IsEligible(mismatchedHint, source, "Контекст B", out string mismatchedHintReason)
                && mismatchedHintReason == "hint-binding", "A record accepted a different current hint than its binding.");
            TranslationRecord staleHintRisk = Eligible(source);
            staleHintRisk.HintSha256 = Hashing.Sha256Text("Контекст {0}");
            Require(!RuntimeSafeDraftPolicy.IsEligible(staleHintRisk, source, "Контекст {0}", out _), "Structurally divergent hint bypassed stale context-risk metadata.");
            TranslationRecord checkedHint = Eligible(source);
            checkedHint.HintSha256 = Hashing.Sha256Text("Контекст {0}");
            checkedHint.RiskFlags = new[] { "context_required" };
            checkedHint.ScreenshotQa = true;
            Require(RuntimeSafeDraftPolicy.IsEligible(checkedHint, source, "Контекст {0}", out _), "Screenshot-verified context draft was rejected.");
            TranslationRecord issue = Eligible(source);
            issue.IssueCodes = new[] { "terminology" };
            Require(!RuntimeSafeDraftPolicy.IsEligible(issue, source, null, out _), "Unresolved issue code was accepted.");
            TranslationRecord resolved = Eligible(source);
            resolved.IssueCodes = new[] { "source_problem" };
            resolved.ScreenshotQa = true;
            Require(RuntimeSafeDraftPolicy.IsEligible(resolved, source, null, out _), "Screenshot-resolved source_problem was rejected.");
            Passed.Add("supervised safe-draft eligibility is deterministic and fail-closed");
        }

        private static void SensitiveAccountRiskClassificationChecks(string englishPath)
        {
            var fixtures = new (string Id, string SourceSha256, bool Sensitive)[]
            {
                ("0628DEA113EAF635", "63ABBE943A7A2A0E7330C4AF5B89F1935D7ADE514F7F8CA5877E0669F82F71BE", true),
                ("0BC88EE7A8BDEFC0", "D72C00517F71BCBCB81A79B427C4E44C8495CEEA413D3DE62C73972DA1D486F3", true),
                ("0F7AFA14C1DA5908", "A7D4916921EDDC4A23277143A755EAA470EC986905FA885F0602F5064E688D60", true),
                ("1A6613F14AAD1196", "92907D8C70891118212793AC5635B1C644647E078772E85CDBDDD7A2409FF2A3", true),
                ("211922B9EFD36BDE", "DF48DC8A553DA310E77B33DE273585F859BDE0792918743920DB76200FFFA8D7", true),
                ("458234F325AE3150", "FF1C9AA7A7B39C559BFE5225448BD5D7A748ADAE41A8A30AAC748CCA015DF961", true),
                ("6C8AEA44B1B10633", "0EAC11B5532C8604C9F56BED77AB76A601203856DE44D8C907C1088457890C74", true),
                ("711D15F8BEAE84A8", "2F26D99F6884B97AD84DFA917C3B18F2928E8183B5FCA7292FF5FF72DDFF8E2A", true),
                ("83A824F22F676F53", "E225ADD370A8852024A16E596443B147D73B85479F10ADD3C45A2000C5D00B7D", true),
                ("9716D3CCBCDE6A4A", "C6E293A81B8C9CC1680C997E67BE727851D9902A0C6D8F785AE6C341A64349BA", true),
                ("AA2D8F7E81C9BDE9", "2EED7F798CF52E4D705AD214AC42F6577F7050A8EB51204DF922143E83A0BFC8", true),
                ("CA20AF10031B78EB", "9043AE4F1C05BAFB5C4CE8B07415C3F7B51A5B2A09A8A0694768456225805439", true),
                ("CD7203F764AA07C8", "FD95616ED13DE2A75FEA968EA191B54D347ABBF4D48BAD53684E31F34C24D18E", true),
                ("F83647B1AB194521", "6F26AD81717AE22B4A952E4E7D9A37D6A095F17B68A1AE0925C4D09B48A61D98", true),
                ("F9704F7B3430C7B5", "DF659094F79E24FAD161D5AC390B6CDB04EEAE16C4FDC7BEB03CE7F77C396A7D", true),
                ("025A401CFA8CAFEA", "5F9D47C2D93507010BBAADD24A9B37C9F553C6CA57CF83C19D85C225B34E5226", false),
                ("1B0C8D587AC75BD9", "D696A35BDD1883DA07A8D6C41BB7A3153381B23AA197629EE273479A6EAA5A9C", false),
                ("23CC48CD129B88FC", "912E86393C8ED8A85823C68B6A1A5D3F24988FFB31C01CCE4FB3EF6B7366EBCB", false),
                ("7EEEB6613585B206", "ACBDE9183E349DDD8BBAC11D005CD487DDCCBEB69D5E91103B9DB14143DACC4C", false)
            };

            Dictionary<ulong, Loc1Entry> sourceByHash = Loc1Codec.ReadFile(englishPath).IndexByHash();

            TranslationRecord Draft(string id, string source, string[] risks) => new TranslationRecord
            {
                Id = id,
                SourceSha256 = Hashing.Sha256Text(source),
                Translation = "Проверка",
                Status = "draft",
                Model = "gpt-5.6-terra",
                PromptVersion = "ru-runtime-v1",
                Confidence = "high",
                NeedsReview = false,
                RiskFlags = risks,
                ReviewStage = "terra_done"
            };

            foreach ((string id, string sourceSha256, bool sensitive) in fixtures)
            {
                ulong keyHash = Convert.ToUInt64(id, 16);
                if (!sourceByHash.TryGetValue(keyHash, out Loc1Entry? entry) || entry.Value is not string source)
                {
                    throw new InvalidDataException($"Runtime source fixture {id} is missing.");
                }
                Require(Hashing.FixedEqualsHex(Hashing.Sha256Text(source), sourceSha256), $"Runtime source fixture {id} no longer matches its 0.60.1239 SHA-256 pin.");
                string[] risks = TranslationValidator.ClassifyRisks(source).ToArray();
                Require(risks.Contains("account_or_payment", StringComparer.Ordinal) == sensitive, $"Runtime source {id} account sensitivity classification differs from the pinned expectation.");
                if (sensitive)
                {
                    string[] staleRisks = risks.Where(value => !string.Equals(value, "account_or_payment", StringComparison.Ordinal)).ToArray();
                    Require(!RuntimeSafeDraftPolicy.IsEligible(Draft(id, source, staleRisks), source, null, out _), $"Sensitive runtime source {id} bypassed the fail-closed stale-risk gate.");
                    Require(!RuntimeSafeDraftPolicy.IsEligible(Draft(id, source, risks), source, null, out _), $"Sensitive runtime source {id} was eligible after accurate risk classification.");
                }
                else
                {
                    Require(RuntimeSafeDraftPolicy.IsEligible(Draft(id, source, risks), source, null, out _), $"Benign runtime source {id} was rejected by the safe-draft policy.");
                }
            }

            Passed.Add("15 sensitive account flows fail closed; 4 account/profile/login UI strings remain eligible");
        }

        private static void HintStructureSafetyChecks(string englishPath, string ukrainianPath)
        {
            const ulong syntheticId = 1;
            const string syntheticSource = "Ready";
            const string syntheticHint = "Подія {0}";
            var syntheticEnglish = new Loc1Document(
                Array.Empty<byte>(), 1, 1, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                new[] { new Loc1Entry(0, syntheticId, 0, 0, syntheticSource) });
            var syntheticUkrainian = new Loc1Document(
                Array.Empty<byte>(), 1, 8, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                new[] { new Loc1Entry(0, syntheticId, 0, 0, syntheticHint) });
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-hint-structure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string jobsPath = Path.Combine(root, "jobs.jsonl");
                JobExportSummary export = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    syntheticUkrainian,
                    TranslationCatalog.Empty(),
                    jobsPath,
                    includeAlreadyTranslated: false,
                    deduplicate: true);
                TranslationJob? job = JsonSerializer.Deserialize<TranslationJob>(File.ReadAllText(jobsPath, new UTF8Encoding(false)));
                Require(export.ItemCount == 1
                    && job != null
                    && job.RiskFlags.Contains("context_required", StringComparer.Ordinal),
                    "Job export did not fail closed on divergent EN/UK structure.");

                var preGuardRecord = new TranslationRecord
                {
                    Id = syntheticId.ToString("X16"),
                    SourceSha256 = Hashing.Sha256Text(syntheticSource),
                    HintSha256 = Hashing.Sha256Text(syntheticHint),
                    Translation = "Готово",
                    Status = "reviewed",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-v2",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.ClassifyRisks(syntheticSource).ToArray(),
                    ReviewStage = "terra_done"
                };
                string preGuardCatalogPath = Path.Combine(root, "pre-guard-catalog.jsonl");
                TranslationCatalog.WriteJsonLines(preGuardCatalogPath, new[] { preGuardRecord });
                TranslationCatalog preGuardCatalog = TranslationCatalog.LoadJsonLines(preGuardCatalogPath);
                string preGuardNormalPath = Path.Combine(root, "pre-guard-normal.jsonl");
                JobExportSummary normalReExport = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    syntheticUkrainian,
                    preGuardCatalog,
                    preGuardNormalPath,
                    includeAlreadyTranslated: false,
                    deduplicate: true);
                JobExportSummary reviewReExport = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    syntheticUkrainian,
                    preGuardCatalog,
                    Path.Combine(root, "pre-guard-review.jsonl"),
                    includeAlreadyTranslated: false,
                    deduplicate: true,
                    reviewQueue: true);
                Require(normalReExport.ItemCount == 1 && reviewReExport.ItemCount == 1,
                    "A pre-guard record with stale derived risks was skipped by a normal or review export queue.");

                TranslationJob preGuardJob = JsonSerializer.Deserialize<TranslationJob>(
                    File.ReadAllText(preGuardNormalPath, new UTF8Encoding(false)))
                    ?? throw new InvalidDataException("Pre-guard retranslation job was not serialized.");
                string preGuardResultsPath = Path.Combine(root, "pre-guard-results.jsonl");
                string preGuardImportedPath = Path.Combine(root, "pre-guard-imported.jsonl");
                var preGuardResult = new TranslationResult
                {
                    JobId = preGuardJob.JobId,
                    Translation = "Готово",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-v2",
                    Confidence = "high",
                    NeedsReview = false
                };
                File.WriteAllText(
                    preGuardResultsPath,
                    JsonSerializer.Serialize(preGuardResult) + Environment.NewLine,
                    new UTF8Encoding(false));
                ImportSummary preGuardImport = TranslationCatalog.ImportDraftResults(
                    syntheticEnglish,
                    preGuardNormalPath,
                    preGuardResultsPath,
                    preGuardCatalog,
                    preGuardImportedPath);
                TranslationCatalog importedPreGuardCatalog = TranslationCatalog.LoadJsonLines(preGuardImportedPath);
                TranslationRecord importedPreGuard = importedPreGuardCatalog.Records.Single();
                Require(preGuardImport.PreservedReviewedIds == 0
                    && preGuardImport.ImportedIds == 1
                    && preGuardImport.NeedsReviewIds == 1
                    && importedPreGuard.Status == "draft"
                    && importedPreGuard.NeedsReview
                    && importedPreGuard.RiskFlags.Contains("context_required", StringComparer.Ordinal),
                    "Import preserved stale reviewed risk metadata instead of creating a review-required current draft.");
                JobExportSummary postImportExport = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    syntheticUkrainian,
                    importedPreGuardCatalog,
                    Path.Combine(root, "post-import-normal.jsonl"),
                    includeAlreadyTranslated: false,
                    deduplicate: true);
                Require(postImportExport.ItemCount == 0,
                    "A re-imported record with current derived risks remained in the normal missing queue.");

                preGuardRecord.RiskFlags = TranslationValidator.DeriveJobRiskFlags(
                    syntheticSource, syntheticHint, deduplicated: false).ToArray();
                string contextCatalogPath = Path.Combine(root, "context-catalog.jsonl");
                TranslationCatalog.WriteJsonLines(contextCatalogPath, new[] { preGuardRecord });
                TranslationCatalog contextCatalog = TranslationCatalog.LoadJsonLines(contextCatalogPath);
                JobExportSummary currentRiskExport = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    syntheticUkrainian,
                    contextCatalog,
                    Path.Combine(root, "current-risk-normal.jsonl"),
                    includeAlreadyTranslated: false,
                    deduplicate: true);
                Require(currentRiskExport.ItemCount == 0,
                    "A binding with current derived risk metadata was unexpectedly re-exported as missing.");
                ValidationReport contextValidation = TranslationValidator.Validate(
                    syntheticEnglish,
                    contextCatalog,
                    includeDraft: true,
                    syntheticUkrainian,
                    ValidationProfile.Preview);
                CompositionSummary contextComposition = TranslationComposer.Apply(
                    syntheticEnglish,
                    syntheticUkrainian,
                    contextCatalog,
                    includeDraft: true,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(contextValidation.UsableRecords == 0
                    && contextComposition.AppliedTranslations == contextValidation.UsableRecords
                    && contextComposition.PolicyFallbacks == 1,
                    "A recorded context_required draft without screenshot QA made validate/build counts diverge.");

                const string matchingHint = "Готово";
                Loc1Document MatchingUkrainian() => new Loc1Document(
                    Array.Empty<byte>(), 1, 8, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                    new[] { new Loc1Entry(0, syntheticId, 0, 0, matchingHint) });
                void RequireUnsafeReviewedRoundTrip(
                    string scenario,
                    TranslationCatalog unsafeReviewedCatalog,
                    string reviewJobsPath)
                {
                    TranslationJob unsafeReviewedJob = JsonSerializer.Deserialize<TranslationJob>(
                        File.ReadAllText(reviewJobsPath, new UTF8Encoding(false)))
                        ?? throw new InvalidDataException($"{scenario} review job was not serialized.");
                    string resultsPath = Path.Combine(root, $"{scenario}-results.jsonl");
                    string importedPath = Path.Combine(root, $"{scenario}-imported.jsonl");
                    var result = new TranslationResult
                    {
                        JobId = unsafeReviewedJob.JobId,
                        Translation = "Готово",
                        Model = "gpt-5.6-terra",
                        PromptVersion = "ru-v2",
                        Confidence = "high",
                        NeedsReview = false
                    };
                    File.WriteAllText(
                        resultsPath,
                        JsonSerializer.Serialize(result) + Environment.NewLine,
                        new UTF8Encoding(false));
                    ImportSummary import = TranslationCatalog.ImportDraftResults(
                        syntheticEnglish,
                        reviewJobsPath,
                        resultsPath,
                        unsafeReviewedCatalog,
                        importedPath);
                    TranslationCatalog importedCatalog = TranslationCatalog.LoadJsonLines(importedPath);
                    TranslationRecord importedRecord = importedCatalog.Records.Single();
                    JobExportSummary normalExport = TranslationCatalog.ExportPrivateJobs(
                        syntheticEnglish,
                        MatchingUkrainian(),
                        importedCatalog,
                        Path.Combine(root, $"{scenario}-normal-after-import.jsonl"),
                        includeAlreadyTranslated: false,
                        deduplicate: true);
                    JobExportSummary reviewExport = TranslationCatalog.ExportPrivateJobs(
                        syntheticEnglish,
                        MatchingUkrainian(),
                        importedCatalog,
                        Path.Combine(root, $"{scenario}-review-after-import.jsonl"),
                        includeAlreadyTranslated: false,
                        deduplicate: true,
                        reviewQueue: true);
                    Require(import.PreservedReviewedIds == 0
                        && import.ImportedIds == 1
                        && import.NeedsReviewIds == 1
                        && importedRecord.Status == "draft"
                        && importedRecord.NeedsReview
                        && importedRecord.RiskFlags.SequenceEqual(
                            TranslationValidator.DeriveJobRiskFlags(
                                syntheticSource, matchingHint, deduplicated: false),
                            StringComparer.Ordinal)
                        && normalExport.ItemCount == 0
                        && reviewExport.ItemCount == 1,
                        $"Unsafe reviewed {scenario} record was preserved or left the expected review lifecycle.");
                }

                var reviewedRecord = new TranslationRecord
                {
                    Id = syntheticId.ToString("X16"),
                    SourceSha256 = Hashing.Sha256Text(syntheticSource),
                    HintSha256 = Hashing.Sha256Text(matchingHint),
                    Translation = "Готово",
                    Status = "reviewed",
                    NeedsReview = true,
                    RiskFlags = TranslationValidator.ClassifyRisks(syntheticSource).ToArray()
                };
                string reviewedNeedsReviewPath = Path.Combine(root, "reviewed-needs-review.jsonl");
                TranslationCatalog.WriteJsonLines(reviewedNeedsReviewPath, new[] { reviewedRecord });
                TranslationCatalog reviewedNeedsReview = TranslationCatalog.LoadJsonLines(reviewedNeedsReviewPath);
                string needsReviewJobsPath = Path.Combine(root, "reviewed-needs-review-queue.jsonl");
                JobExportSummary needsReviewQueue = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    MatchingUkrainian(),
                    reviewedNeedsReview,
                    needsReviewJobsPath,
                    includeAlreadyTranslated: false,
                    deduplicate: true,
                    reviewQueue: true);
                ValidationReport needsReviewValidation = TranslationValidator.Validate(
                    syntheticEnglish, reviewedNeedsReview, includeDraft: true, MatchingUkrainian(), ValidationProfile.Preview);
                CompositionSummary needsReviewComposition = TranslationComposer.Apply(
                    syntheticEnglish,
                    MatchingUkrainian(),
                    reviewedNeedsReview,
                    includeDraft: true,
                    excludeNeedsReview: false,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(needsReviewValidation.UsableRecords == 0
                    && needsReviewQueue.ItemCount == 1
                    && needsReviewComposition.AppliedTranslations == 0
                    && needsReviewComposition.NeedsReviewFallbacks == 0
                    && needsReviewComposition.PolicyFallbacks == 1,
                    "Reviewed NeedsReview=true bypassed preview policy when excludeNeedsReview was false.");
                RequireUnsafeReviewedRoundTrip("reviewed-needs-review", reviewedNeedsReview, needsReviewJobsPath);

                reviewedRecord.NeedsReview = false;
                reviewedRecord.IssueCodes = new[] { "ambiguous_context" };
                string reviewedIssuePath = Path.Combine(root, "reviewed-ambiguous-context.jsonl");
                TranslationCatalog.WriteJsonLines(reviewedIssuePath, new[] { reviewedRecord });
                TranslationCatalog reviewedIssue = TranslationCatalog.LoadJsonLines(reviewedIssuePath);
                string issueReviewJobsPath = Path.Combine(root, "reviewed-ambiguous-context-queue.jsonl");
                JobExportSummary issueReviewQueue = TranslationCatalog.ExportPrivateJobs(
                    syntheticEnglish,
                    MatchingUkrainian(),
                    reviewedIssue,
                    issueReviewJobsPath,
                    includeAlreadyTranslated: false,
                    deduplicate: true,
                    reviewQueue: true);
                ValidationReport issueValidation = TranslationValidator.Validate(
                    syntheticEnglish, reviewedIssue, includeDraft: true, MatchingUkrainian(), ValidationProfile.Preview);
                CompositionSummary issueComposition = TranslationComposer.Apply(
                    syntheticEnglish,
                    MatchingUkrainian(),
                    reviewedIssue,
                    includeDraft: true,
                    excludeNeedsReview: false,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(issueValidation.UsableRecords == 0
                    && issueReviewQueue.ItemCount == 1
                    && issueComposition.AppliedTranslations == 0
                    && issueComposition.PolicyFallbacks == 1,
                    "Reviewed unresolved ambiguous_context bypassed preview policy.");
                RequireUnsafeReviewedRoundTrip("reviewed-ambiguous-context", reviewedIssue, issueReviewJobsPath);

                reviewedRecord.IssueCodes = Array.Empty<string>();
                string reviewedReleasePath = Path.Combine(root, "reviewed-release.jsonl");
                TranslationCatalog.WriteJsonLines(reviewedReleasePath, new[] { reviewedRecord });
                ValidationReport reviewedReleaseValidation = TranslationValidator.Validate(
                    syntheticEnglish,
                    TranslationCatalog.LoadJsonLines(reviewedReleasePath),
                    includeDraft: false,
                    MatchingUkrainian(),
                    ValidationProfile.Release);
                Require(reviewedReleaseValidation.UsableRecords == 0,
                    "Release validation counted a reviewed-only record as usable.");

                const ulong invalidId = 3;
                const string tokenSource = "Deal 25% {0}";
                const string tokenHint = "Завдати 25% {0}";
                var tokenEnglish = new Loc1Document(
                    Array.Empty<byte>(), 1, 1, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                    new[] { new Loc1Entry(0, invalidId, 0, 0, tokenSource) });
                var tokenUkrainian = new Loc1Document(
                    Array.Empty<byte>(), 1, 8, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                    new[] { new Loc1Entry(0, invalidId, 0, 0, tokenHint) });
                var invalidTokenRecord = new TranslationRecord
                {
                    Id = invalidId.ToString("X16"),
                    SourceSha256 = Hashing.Sha256Text(tokenSource),
                    HintSha256 = Hashing.Sha256Text(tokenHint),
                    Translation = "Нанести 30%",
                    Status = "reviewed",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.ClassifyRisks(tokenSource).ToArray()
                };
                string invalidTokenPath = Path.Combine(root, "invalid-token-catalog.jsonl");
                TranslationCatalog.WriteJsonLines(invalidTokenPath, new[] { invalidTokenRecord });
                TranslationCatalog invalidTokenCatalog = TranslationCatalog.LoadJsonLines(invalidTokenPath);
                ValidationReport invalidTokenValidation = TranslationValidator.Validate(
                    tokenEnglish, invalidTokenCatalog, includeDraft: true, tokenUkrainian, ValidationProfile.Preview);
                CompositionSummary invalidTokenComposition = TranslationComposer.Apply(
                    tokenEnglish,
                    tokenUkrainian,
                    invalidTokenCatalog,
                    includeDraft: true,
                    excludeNeedsReview: false,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(invalidTokenValidation.UsableRecords == 0
                    && invalidTokenValidation.Issues.Any(issue => issue.Code == "protected-token-mismatch")
                    && invalidTokenValidation.Issues.Any(issue => issue.Code == "numeric-token-mismatch")
                    && invalidTokenComposition.AppliedTranslations == invalidTokenValidation.UsableRecords
                    && invalidTokenComposition.RejectedCatalogRecords == 1,
                    "Pair-invalid numeric/protected tokens made validate/build usable counts diverge.");

                const ulong sensitiveId = 2;
                const string sensitiveSource = "Delete your account";
                const string sensitiveHint = "Видалити обліковий запис";
                var sensitiveEnglish = new Loc1Document(
                    Array.Empty<byte>(), 1, 1, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                    new[] { new Loc1Entry(0, sensitiveId, 0, 0, sensitiveSource) });
                var sensitiveUkrainian = new Loc1Document(
                    Array.Empty<byte>(), 1, 8, 1, 1, 0, 0, "synthetic-guid", "synthetic-version",
                    new[] { new Loc1Entry(0, sensitiveId, 0, 0, sensitiveHint) });
                var sensitiveRecord = new TranslationRecord
                {
                    Id = sensitiveId.ToString("X16"),
                    SourceSha256 = Hashing.Sha256Text(sensitiveSource),
                    HintSha256 = Hashing.Sha256Text(sensitiveHint),
                    Translation = "Удалить учетную запись",
                    Status = "draft",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-v2",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.DeriveJobRiskFlags(
                        sensitiveSource, sensitiveHint, deduplicated: false).ToArray(),
                    ReviewStage = "terra_done"
                };
                string sensitiveCatalogPath = Path.Combine(root, "sensitive-catalog.jsonl");
                TranslationCatalog.WriteJsonLines(sensitiveCatalogPath, new[] { sensitiveRecord });
                string sensitiveJobsPath = Path.Combine(root, "sensitive-review.jsonl");
                JobExportSummary sensitiveReview = TranslationCatalog.ExportPrivateJobs(
                    sensitiveEnglish,
                    sensitiveUkrainian,
                    TranslationCatalog.LoadJsonLines(sensitiveCatalogPath),
                    sensitiveJobsPath,
                    includeAlreadyTranslated: false,
                    deduplicate: true,
                    reviewQueue: true,
                    includeSensitive: true);
                TranslationJob sensitiveJob = JsonSerializer.Deserialize<TranslationJob>(
                    File.ReadAllText(sensitiveJobsPath, new UTF8Encoding(false)))
                    ?? throw new InvalidDataException("Sensitive review job was not serialized.");
                Require(sensitiveReview.ItemCount == 1
                    && sensitiveJob.RiskFlags.Any(TranslationValidator.IsSensitiveRisk),
                    "A high-confidence sensitive record was skipped by the review queue.");

                string sensitiveResultsPath = Path.Combine(root, "sensitive-results.jsonl");
                string sensitiveOutputPath = Path.Combine(root, "sensitive-output.jsonl");
                var sensitiveResult = new TranslationResult
                {
                    JobId = sensitiveJob.JobId,
                    Translation = "Удалить учетную запись",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-v2",
                    Confidence = "high",
                    NeedsReview = false
                };
                File.WriteAllText(sensitiveResultsPath, JsonSerializer.Serialize(sensitiveResult) + Environment.NewLine, new UTF8Encoding(false));
                ImportSummary sensitiveImport = TranslationCatalog.ImportDraftResults(
                    sensitiveEnglish,
                    sensitiveJobsPath,
                    sensitiveResultsPath,
                    TranslationCatalog.Empty(),
                    sensitiveOutputPath);
                TranslationRecord importedSensitive = TranslationCatalog.LoadJsonLines(sensitiveOutputPath).Records.Single();
                Require(sensitiveImport.NeedsReviewIds == 1 && importedSensitive.NeedsReview,
                    "Import left a derived sensitive risk at high confidence without mandatory review.");

                const string pinnedId = "3CB578510F72B19E";
                const string pinnedEnglishSha256 = "B8F291487A0C82EB3A266C76043D219D2A8141C9931395B5494677B8412D4127";
                const string pinnedUkrainianSha256 = "BDEFA136DE7BC57ACF96663573CEA9139C8DCB84290313115A89679B88A1A098";
                Loc1Document english = Loc1Codec.ReadFile(englishPath);
                Loc1Document ukrainian = Loc1Codec.ReadFile(ukrainianPath);
                ulong keyHash = Convert.ToUInt64(pinnedId, 16);
                english.IndexByHash().TryGetValue(keyHash, out Loc1Entry? sourceEntry);
                ukrainian.IndexByHash().TryGetValue(keyHash, out Loc1Entry? hintEntry);
                string? source = sourceEntry?.Value;
                string? hint = hintEntry?.Value;
                Require(source != null
                    && Hashing.FixedEqualsHex(Hashing.Sha256Text(source), pinnedEnglishSha256),
                    $"Pinned runtime source {pinnedId} is missing or changed.");
                Require(hint != null
                    && Hashing.FixedEqualsHex(Hashing.Sha256Text(hint), pinnedUkrainianSha256),
                    $"Pinned runtime hint {pinnedId} is missing or changed.");
                Require(TranslationValidator.RequiresContextReview(source!, hint!),
                    $"Pinned runtime EN/UK structure mismatch {pinnedId} was not detected.");

                var staleRecord = new TranslationRecord
                {
                    Id = pinnedId,
                    SourceSha256 = pinnedEnglishSha256,
                    HintSha256 = pinnedUkrainianSha256,
                    Translation = "Проверка",
                    Status = "draft",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-runtime-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.ClassifyRisks(source!).ToArray(),
                    ReviewStage = "terra_done"
                };
                string catalogPath = Path.Combine(root, "catalog.jsonl");
                TranslationCatalog.WriteJsonLines(catalogPath, new[] { staleRecord });
                TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(catalogPath);
                ValidationReport validation = TranslationValidator.Validate(
                    english,
                    catalog,
                    includeDraft: true,
                    ukrainian,
                    ValidationProfile.Preview,
                    allowPerLocaleContentVersion: true);
                Require(validation.UsableRecords == 0
                    && validation.Issues.Any(issue => issue.Id == pinnedId && issue.Code == "risk-metadata-mismatch")
                    && validation.Issues.Any(issue => issue.Id == pinnedId && issue.Code == "screenshot-qa-required"),
                    "Validation treated stale context metadata as preview-safe.");

                CompositionSummary composition = TranslationComposer.Apply(
                    english,
                    ukrainian,
                    catalog,
                    includeDraft: true,
                    allowPerLocaleContentVersion: true,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(composition.AppliedTranslations == 0 && composition.PolicyFallbacks == 1,
                    "Composition applied a structurally divergent hint without screenshot QA.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Passed.Add("EN/UK structural divergence requires context review and screenshot QA");
        }

        private static void CompositionCheck(string englishPath, string ukrainianPath)
        {
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document ukrainian = Loc1Codec.ReadFile(ukrainianPath);
            Loc1Entry source = english.Entries.First(entry => !string.IsNullOrEmpty(entry.Value) && TranslationValidator.ExtractProtectedTokens(entry.Value!).Count == 0);
            string temp = Path.Combine(Path.GetTempPath(), $"invokersru-{Guid.NewGuid():N}.jsonl");
            try
            {
                string record = "{\"id\":\"" + source.Id + "\",\"source_sha256\":\"" + Hashing.Sha256Text(source.Value!) + "\",\"translation\":\"Проверка\",\"status\":\"reviewed\"}";
                File.WriteAllText(temp, record + Environment.NewLine, new UTF8Encoding(false));
                TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(temp);
                ValidationReport validation = TranslationValidator.Validate(english, catalog, includeDraft: false);
                Require(validation.ErrorCount == 0 && validation.UsableRecords == 1, "A fresh reviewed record was not usable.");
                CompositionSummary summary = TranslationComposer.Apply(english, ukrainian, catalog, includeDraft: false);
                Require(summary.AppliedTranslations == 1, "Translation composer did not apply exactly one Russian record.");
                Require(ukrainian.IndexByHash()[source.KeyHash].Value == "Проверка", "Composed translation is missing.");

                Loc1Document reviewFilteredBase = Loc1Codec.ReadFile(ukrainianPath);
                TranslationRecord reviewRecord = catalog.Records.Single();
                reviewRecord.NeedsReview = true;
                CompositionSummary filtered = TranslationComposer.Apply(english, reviewFilteredBase, catalog, includeDraft: false, excludeNeedsReview: true);
                Require(filtered.AppliedTranslations == 0 && filtered.NeedsReviewFallbacks == 1
                    && reviewFilteredBase.IndexByHash()[source.KeyHash].Value == source.Value,
                    "Needs-review preview filtering did not use the English fallback.");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            Passed.Add("catalog validation and EN fallback composition");
        }

        private static void PerLocaleVersionCompatibilityCheck(string englishPath, string basePath)
        {
            Loc1Document english = ParseWithContentVersion(englishPath, "Prod_0.60.0_57");
            Loc1Document baseLocale = ParseWithContentVersion(basePath, "Prod_0.60.0_58");
            TranslationCatalog empty = TranslationCatalog.Empty();
            bool strictRejected = false;
            try
            {
                TranslationComposer.Apply(english, baseLocale, empty, includeDraft: false);
            }
            catch (InvalidOperationException)
            {
                strictRejected = true;
            }
            Require(strictRejected, "Strict composition accepted differing per-locale ContentVersion values.");

            Loc1Document acceptedBase = ParseWithContentVersion(basePath, "Prod_0.60.0_58");
            TranslationComposer.Apply(english, acceptedBase, empty, includeDraft: false, allowPerLocaleContentVersion: true);
            TranslationValidator.Validate(english, empty, includeDraft: false, acceptedBase,
                allowPerLocaleContentVersion: true);

            Loc1Document wrongGuid = CloneWithHeaderMutation(acceptedBase, bytes => bytes[0x52] ^= 1);
            bool guidRejected = false;
            try
            {
                TranslationComposer.Apply(english, wrongGuid, empty, includeDraft: false, allowPerLocaleContentVersion: true);
            }
            catch (InvalidOperationException)
            {
                guidRejected = true;
            }
            Require(guidRejected, "Per-locale ContentVersion mode accepted a different content GUID.");

            Loc1Document wrongKeys = ParseWithContentVersion(basePath, "Prod_0.60.0_58");
            byte[] wrongKeyRaw = Loc1Codec.BuildRaw(wrongKeys);
            int firstRecord = checked((int)wrongKeys.HeaderSize);
            wrongKeyRaw[firstRecord] ^= 1;
            Loc1Document wrongKeyDocument = Loc1Codec.Parse(wrongKeyRaw);
            bool keyRejected = false;
            try
            {
                TranslationValidator.Validate(english, empty, includeDraft: false, wrongKeyDocument,
                    allowPerLocaleContentVersion: true);
            }
            catch (InvalidOperationException)
            {
                keyRejected = true;
            }
            Require(keyRejected, "Per-locale ContentVersion mode accepted a different key set.");
            Passed.Add("per-locale ContentVersion accepted only for identical GUID/schema/key corpus");
        }

        private static void CompatibilityManifestSafetyChecks()
        {
            var unsafeBuild = new CompatibleBuild
            {
                Id = "..",
                ContentGuid = "synthetic-guid",
                ContentVersion = "synthetic-version"
            };
            bool rejected = false;
            try
            {
                unsafeBuild.Validate();
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("build id", StringComparison.OrdinalIgnoreCase))
            {
                rejected = true;
            }

            Require(rejected, "An unsafe build id was accepted for backup path derivation.");
            Passed.Add("unsafe compatibility build ids are rejected");
        }

        private static void ResultImportCheck(string englishPath)
        {
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Entry source = english.Entries.First(entry => !string.IsNullOrEmpty(entry.Value) && TranslationValidator.ExtractProtectedTokens(entry.Value!).Count == 0);
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string jobs = Path.Combine(root, "jobs.jsonl");
            string results = Path.Combine(root, "results.jsonl");
            string output = Path.Combine(root, "ru.jsonl");
            try
            {
                var job = new TranslationJob
                {
                    ContentGuid = english.ContentGuid,
                    ContentVersion = english.ContentVersion,
                    Ids = new[] { source.Id },
                    SourceSha256 = Hashing.Sha256Text(source.Value!),
                    English = source.Value!,
                    ProtectedTokens = TranslationValidator.ExtractProtectedTokens(source.Value!).ToArray(),
                    RiskFlags = TranslationValidator.ClassifyRisks(source.Value!).ToArray(),
                    Deduplicated = false,
                    MvpCategory = "basic_ui",
                    MvpRank = 1,
                    MvpScope = "smoke-mvp-v1"
                };
                job.JobId = TranslationCatalog.ComputeJobId(job);
                var result = new TranslationResult
                {
                    JobId = job.JobId,
                    Translation = "Проверка импорта",
                    Model = "test-model",
                    PromptVersion = "test-v1",
                    Confidence = "high"
                };
                File.WriteAllText(jobs, JsonSerializer.Serialize(job) + Environment.NewLine, new UTF8Encoding(false));
                File.WriteAllText(results, JsonSerializer.Serialize(result) + Environment.NewLine, new UTF8Encoding(false));
                ImportSummary summary = TranslationCatalog.ImportDraftResults(english, jobs, results, TranslationCatalog.Empty(), output);
                Require(summary.ImportedIds == 1, "Result importer did not expand one job to one id.");
                TranslationCatalog imported = TranslationCatalog.LoadJsonLines(output);
                Require(imported.TryGetUsable(source.KeyHash, source.Value!, includeDraft: true, out TranslationRecord? importedRecord, out _)
                    && importedRecord!.Translation == "Проверка импорта", "Imported draft is not usable.");

                string contextJobs = Path.Combine(root, "context-jobs.jsonl");
                string contextResults = Path.Combine(root, "context-results.jsonl");
                string contextOutput = Path.Combine(root, "context-ru.jsonl");
                var contextJob = new TranslationJob
                {
                    ContentGuid = english.ContentGuid,
                    ContentVersion = english.ContentVersion,
                    Ids = new[] { source.Id },
                    SourceSha256 = Hashing.Sha256Text(source.Value!),
                    HintSha256 = Hashing.Sha256Text("Синтетичний контекст {0}"),
                    English = source.Value!,
                    UkrainianHint = "Синтетичний контекст {0}",
                    ProtectedTokens = TranslationValidator.ExtractProtectedTokens(source.Value!).ToArray(),
                    RiskFlags = TranslationValidator.ClassifyRisks(source.Value!).ToArray(),
                    Deduplicated = false
                };
                contextJob.JobId = TranslationCatalog.ComputeJobId(contextJob);
                var contextResult = new TranslationResult
                {
                    JobId = contextJob.JobId,
                    Translation = "Проверка импорта",
                    Model = "test-model",
                    PromptVersion = "test-v1",
                    Confidence = "high"
                };
                File.WriteAllText(contextJobs, JsonSerializer.Serialize(contextJob) + Environment.NewLine, new UTF8Encoding(false));
                File.WriteAllText(contextResults, JsonSerializer.Serialize(contextResult) + Environment.NewLine, new UTF8Encoding(false));
                bool staleRiskRejected = false;
                try
                {
                    TranslationCatalog.ImportDraftResults(english, contextJobs, contextResults, TranslationCatalog.Empty(), contextOutput);
                }
                catch (InvalidDataException exception) when (exception.Message.Contains("risk_flags", StringComparison.Ordinal))
                {
                    staleRiskRejected = true;
                }
                Require(staleRiskRejected && !File.Exists(contextOutput), "Importer accepted stale source-only risk metadata for a divergent hint.");

                contextJob.RiskFlags = TranslationValidator.DeriveJobRiskFlags(source.Value!, contextJob.UkrainianHint, deduplicated: false).ToArray();
                contextJob.JobId = TranslationCatalog.ComputeJobId(contextJob);
                contextResult.JobId = contextJob.JobId;
                File.WriteAllText(contextJobs, JsonSerializer.Serialize(contextJob) + Environment.NewLine, new UTF8Encoding(false));
                File.WriteAllText(contextResults, JsonSerializer.Serialize(contextResult) + Environment.NewLine, new UTF8Encoding(false));
                ImportSummary contextSummary = TranslationCatalog.ImportDraftResults(
                    english, contextJobs, contextResults, TranslationCatalog.Empty(), contextOutput);
                TranslationRecord contextRecord = TranslationCatalog.LoadJsonLines(contextOutput).Records.Single();
                Require(contextSummary.ImportedIds == 1
                    && contextSummary.NeedsReviewIds == 1
                    && contextRecord.NeedsReview
                    && contextRecord.RiskFlags.Contains("context_required", StringComparer.Ordinal),
                    "Importer did not propagate re-derived context review metadata.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Passed.Add("model-result import to source-free public overlay");
        }

        private static void TransactionalPatcherCheck(string englishPath, string ukrainianPath, string manifestFixture)
        {
            Require(File.Exists(manifestFixture), "manifest.dat fixture is required for patcher smoke test.");
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-patcher-{Guid.NewGuid():N}");
            string game = Path.Combine(root, "game");
            string i18n = Path.Combine(game, "Invokers_Data", "StreamingAssets", "i18n");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(i18n);
            string en = Path.Combine(i18n, "en_US.bin.br");
            string uk = Path.Combine(i18n, "uk_UA.bin.br");
            string manifest = Path.Combine(i18n, "manifest.dat");
            string dummyExe = Path.Combine(game, "dummy.exe");
            string translations = Path.Combine(root, "translation.jsonl");
            try
            {
                File.Copy(englishPath, en);
                File.Copy(ukrainianPath, uk);
                File.Copy(manifestFixture, manifest);
                File.WriteAllBytes(dummyExe, Encoding.ASCII.GetBytes("synthetic-game-file"));
                Loc1Document english = Loc1Codec.ReadFile(en);
                Loc1Document baseLocale = Loc1Codec.ReadFile(uk);
                Dictionary<ulong, Loc1Entry> baseByHash = baseLocale.IndexByHash();
                Loc1Entry source = english.Entries.First(entry => entry.Value != null
                    && baseByHash.TryGetValue(entry.KeyHash, out Loc1Entry? target)
                    && target.Value != null
                    && TranslationValidator.ExtractProtectedTokens(entry.Value).Count == 0
                    && !TranslationValidator.RequiresContextReview(entry.Value, target.Value)
                    && !TranslationValidator.ClassifyRisks(entry.Value).Any(TranslationValidator.IsSensitiveRisk));
                var releaseRecords = new List<TranslationRecord>();
                DateTimeOffset reviewedAt = DateTimeOffset.UtcNow;
                foreach (Loc1Entry englishEntry in english.Entries.Where(entry => entry.Value != null
                    && !TranslationValidator.ClassifyRisks(entry.Value!).Any(TranslationValidator.IsSensitiveRisk)))
                {
                    baseByHash.TryGetValue(englishEntry.KeyHash, out Loc1Entry? hintEntry);
                    string[] risks = TranslationValidator.DeriveJobRiskFlags(
                        englishEntry.Value!, hintEntry?.Value, deduplicated: false).ToArray();
                    releaseRecords.Add(new TranslationRecord
                    {
                        Id = englishEntry.Id,
                        SourceSha256 = Hashing.Sha256Text(englishEntry.Value!),
                        HintSha256 = hintEntry?.Value == null ? null : Hashing.Sha256Text(hintEntry.Value),
                        Translation = englishEntry.KeyHash == source.KeyHash ? "Проверка патчера" : englishEntry.Value!,
                        Status = "approved",
                        Model = "gpt-5.6-terra",
                        PromptVersion = "ru-runtime-v1",
                        Confidence = "high",
                        RiskFlags = risks,
                        ReviewStage = "human_review",
                        ReviewerIds = new[] { "smoke-reviewer" },
                        ReviewedAt = reviewedAt,
                        ReviewRevision = "smoke-v1",
                        ScreenshotQa = risks.Contains("context_required", StringComparer.Ordinal),
                        UpdatedAt = reviewedAt
                    });
                }
                TranslationCatalog.WriteJsonLines(translations, releaseRecords);
                TranslationCatalog releaseCatalog = TranslationCatalog.LoadJsonLines(translations);
                Loc1Document expectedOutputDocument = Loc1Codec.ReadFile(uk);
                CompositionSummary expectedComposition = TranslationComposer.Apply(english, expectedOutputDocument, releaseCatalog, includeDraft: false, approvedOnly: true);
                Require(expectedComposition.AppliedTranslations == releaseRecords.Count, "Synthetic expected output did not apply the complete release catalog.");
                byte[] expectedOutputRaw = Loc1Codec.BuildRaw(expectedOutputDocument);
                byte[] expectedOutputContainer = Loc1Codec.Compress(expectedOutputRaw);
                var build = new CompatibleBuild
                {
                    Id = "synthetic-ready-build",
                    GameVersion = "test",
                    ContentGuid = english.ContentGuid,
                    ContentVersion = english.ContentVersion,
                    Readiness = "ready",
                    Certified = true,
                    EnglishRelativePath = Path.Combine("Invokers_Data", "StreamingAssets", "i18n", "en_US.bin.br"),
                    EnglishSha256 = Hashing.Sha256File(en),
                    EnglishRawSha256 = Hashing.Sha256Bytes(Loc1Codec.ReadContainer(en)),
                    BaseRelativePath = Path.Combine("Invokers_Data", "StreamingAssets", "i18n", "uk_UA.bin.br"),
                    BaseSha256 = Hashing.Sha256File(uk),
                    BaseRawSha256 = Hashing.Sha256Bytes(Loc1Codec.ReadContainer(uk)),
                    BaseLocaleId = baseLocale.LocaleId,
                    BaseLocaleRevision = baseLocale.LocaleRevision,
                    EntryCount = baseLocale.Entries.Count,
                    TranslationCatalogSha256 = Hashing.Sha256File(translations),
                    MinimumAppliedTranslations = 1,
                    ExpectedOutputSha256 = Hashing.Sha256Bytes(expectedOutputContainer),
                    ExpectedOutputRawSha256 = Hashing.Sha256Bytes(expectedOutputRaw),
                    ManifestRelativePath = Path.Combine("Invokers_Data", "StreamingAssets", "i18n", "manifest.dat"),
                    ManifestSha256 = Hashing.Sha256File(manifest),
                    GameFiles = new List<KnownFile>
                    {
                        new KnownFile { RelativePath = "dummy.exe", Size = new FileInfo(dummyExe).Length, Sha256 = Hashing.Sha256File(dummyExe) }
                    }
                };
                var compatibility = new CompatibilityManifest { Schema = 1, Builds = new List<CompatibleBuild> { build } };
                InstallationInspection original = PatchPlanner.Inspect(game, compatibility, statePath);
                Require(original.Status == InstallationStatus.CompatibleOriginal, "Synthetic build was not recognized as original.");
                PatchApplyResult applied = PatchService.Apply(original, translations, statePath, includeDraft: false);
                Require(applied.Composition.AppliedTranslations == releaseRecords.Count, "Synthetic patch did not apply the complete approved release catalog.");
                Require(Loc1Codec.ReadFile(uk).IndexByHash()[source.KeyHash].Value == "Проверка патчера", "Synthetic patch did not install the Russian test value.");
                Require(File.Exists(applied.State.BackupPath), "Immutable backup was not created.");
                InstallationInspection patched = PatchPlanner.Inspect(game, compatibility, statePath);
                Require(patched.Status == InstallationStatus.PatchedByThisTool, "Patched synthetic build was not recognized from state and hashes.");

                string journalDirectory = Path.Combine(Path.GetDirectoryName(statePath)!, "journals");
                Directory.CreateDirectory(journalDirectory);
                File.Delete(statePath);
                var journal = new PatchJournal
                {
                    TransactionId = "0123456789abcdef0123456789abcdef",
                    Operation = "apply",
                    Phase = "ReplacementCommitted",
                    BuildId = applied.State.BuildId,
                    GameRoot = applied.State.GameRoot,
                    TargetPath = applied.State.TargetPath,
                    BackupPath = applied.State.BackupPath,
                    QuarantinePath = Path.Combine(Path.GetDirectoryName(applied.State.TargetPath)!, ".uk_UA.bin.br.0123456789abcdef0123456789abcdef.displaced"),
                    RollbackPath = Path.Combine(Path.GetDirectoryName(applied.State.TargetPath)!, ".uk_UA.bin.br.0123456789abcdef0123456789abcdef.rollback"),
                    DisplacedSha256 = applied.State.OriginalSha256,
                    SourceSha256 = applied.State.OriginalSha256,
                    ExpectedOutputSha256 = applied.State.PatchedSha256,
                    TranslationsSha256 = applied.State.TranslationsSha256,
                    AppliedTranslations = applied.State.AppliedTranslations,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                File.Copy(applied.State.BackupPath, journal.QuarantinePath);
                File.WriteAllText(Path.Combine(journalDirectory, journal.TransactionId + ".json"), JsonSerializer.Serialize(journal));
                string recovery = PatchService.Recover(statePath, compatibility);
                Require(recovery.Contains("Recovered", StringComparison.Ordinal) && File.Exists(statePath), "Hash-based journal recovery did not recreate state.");
                PatchService.Restore(statePath, compatibility);
                Require(!File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(uk), build.BaseSha256), "Restore did not return the synthetic target to exact original bytes.");

                string previewTranslations = Path.Combine(root, "translation-preview.jsonl");
                baseByHash.TryGetValue(source.KeyHash, out Loc1Entry? previewHint);
                var previewRecord = new TranslationRecord
                {
                    Id = source.Id,
                    SourceSha256 = Hashing.Sha256Text(source.Value!),
                    HintSha256 = previewHint?.Value == null ? null : Hashing.Sha256Text(previewHint.Value),
                    Translation = "Контролируемый предпросмотр",
                    Status = "draft",
                    Model = "gpt-5.6-terra",
                    PromptVersion = "ru-v2",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.DeriveJobRiskFlags(source.Value!, previewHint?.Value, deduplicated: false).ToArray(),
                    ReviewStage = "terra_done",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                TranslationCatalog.WriteJsonLines(previewTranslations, new[] { previewRecord });
                TranslationCatalog previewCatalog = TranslationCatalog.LoadJsonLines(previewTranslations);
                Loc1Document expectedPreviewDocument = Loc1Codec.ReadFile(uk);
                CompositionSummary expectedPreviewComposition = TranslationComposer.Apply(
                    english,
                    expectedPreviewDocument,
                    previewCatalog,
                    includeDraft: true,
                    approvedOnly: false,
                    excludeNeedsReview: false,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                Require(expectedPreviewComposition.AppliedTranslations == 1, "Synthetic supervised preview did not compose its draft record.");
                byte[] expectedPreviewRaw = Loc1Codec.BuildRaw(expectedPreviewDocument);
                byte[] expectedPreviewContainer = Loc1Codec.Compress(expectedPreviewRaw);
                build.Id = "synthetic-supervised-preview";
                build.PatchMode = "supervised_preview";
                build.ExcludeNeedsReview = false;
                build.TranslationCatalogSha256 = Hashing.Sha256File(previewTranslations);
                build.ExpectedOutputSha256 = Hashing.Sha256Bytes(expectedPreviewContainer);
                build.ExpectedOutputRawSha256 = Hashing.Sha256Bytes(expectedPreviewRaw);
                var previewCompatibility = new CompatibilityManifest { Schema = 1, Builds = new List<CompatibleBuild> { build } };
                InstallationInspection previewOriginal = PatchPlanner.Inspect(game, previewCompatibility, statePath);
                Require(previewOriginal.Status == InstallationStatus.CompatibleOriginal, "Synthetic supervised preview build was not recognized as original.");
                bool previewGateRefused = false;
                try
                {
                    PatchService.Apply(previewOriginal, previewTranslations, statePath, includeDraft: false);
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("--include-draft", StringComparison.Ordinal))
                {
                    previewGateRefused = true;
                }
                Require(previewGateRefused && !File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(uk), build.BaseSha256),
                    "Supervised preview write was not refused before mutation without the explicit draft gate.");
                PatchApplyResult previewApplied = PatchService.Apply(previewOriginal, previewTranslations, statePath, includeDraft: true);
                Require(previewApplied.Composition.AppliedTranslations == 1, "Synthetic supervised preview did not apply the pinned draft catalog.");
                Require(Loc1Codec.ReadFile(uk).IndexByHash()[source.KeyHash].Value == "Контролируемый предпросмотр", "Synthetic supervised preview value was not installed.");

                File.Copy(previewApplied.State.BackupPath, uk, overwrite: true);
                File.WriteAllBytes(dummyExe, Encoding.ASCII.GetBytes("simulated-partial-official-update"));
                bool partialUpdateRefused = false;
                try
                {
                    PatchService.Restore(statePath, previewCompatibility);
                }
                catch (IOException)
                {
                    partialUpdateRefused = true;
                }
                Require(partialUpdateRefused && File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(uk), build.BaseSha256),
                    "Restore cleared state or rewrote the target during a partial official update.");

                File.WriteAllBytes(dummyExe, Encoding.ASCII.GetBytes("synthetic-game-file"));
                PatchService.Restore(statePath, previewCompatibility);
                Require(!File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(uk), build.BaseSha256),
                    "Already-restored target did not complete safe no-op restore cleanup.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Passed.Add("transactional apply, recovery, draft gate, partial-update refusal, and exact/no-op restore");
        }

        private static void RuntimeCacheTransactionalCheck(string englishPath, string basePath)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-runtime-cache-{Guid.NewGuid():N}");
            string cache = Path.Combine(root, "cache");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            string translationsPath = Path.Combine(root, "translations.jsonl");
            Directory.CreateDirectory(cache);
            string englishCache = Path.Combine(cache, "dl_en_US.bin");
            string targetCache = Path.Combine(cache, "dl_uk_UA.bin");
            string stampPath = Path.Combine(cache, "dl_uk_UA.bin.ver");
            try
            {
                byte[] englishRaw = RawWithContentVersion(englishPath, "Prod_0.60.0_57");
                byte[] baseRaw = RawWithContentVersion(basePath, "Prod_0.60.0_58");
                File.WriteAllBytes(englishCache, englishRaw);
                File.WriteAllBytes(targetCache, baseRaw);
                File.WriteAllText(stampPath, "0.60.test", new UTF8Encoding(false));
                Loc1Document english = Loc1Codec.Parse(englishRaw);
                Loc1Document baseLocale = Loc1Codec.Parse(baseRaw);
                Dictionary<ulong, Loc1Entry> baseByHash = baseLocale.IndexByHash();
                Loc1Entry changedSource = english.Entries.First(entry => entry.Value != null
                    && baseByHash.TryGetValue(entry.KeyHash, out Loc1Entry? target) && target.Value != null
                    && TranslationValidator.ExtractProtectedTokens(entry.Value).Count == 0
                    && !entry.Value.Any(char.IsDigit)
                    && !TranslationValidator.RequiresContextReview(entry.Value, target.Value)
                    && !TranslationValidator.ClassifyRisks(entry.Value).Any(TranslationValidator.IsSensitiveRisk));
                Loc1Entry needsReviewSource = english.Entries.First(entry => entry.KeyHash != changedSource.KeyHash
                    && entry.Value != null && baseByHash.TryGetValue(entry.KeyHash, out Loc1Entry? target) && target.Value != null);
                baseByHash.TryGetValue(changedSource.KeyHash, out Loc1Entry? changedHint);
                baseByHash.TryGetValue(needsReviewSource.KeyHash, out Loc1Entry? needsReviewHint);
                var records = new List<TranslationRecord>
                {
                    new TranslationRecord
                    {
                        Id = changedSource.Id,
                        SourceSha256 = Hashing.Sha256Text(changedSource.Value!),
                        HintSha256 = changedHint?.Value == null ? null : Hashing.Sha256Text(changedHint.Value),
                        Translation = "Кэш-тест-другой-размер",
                        Status = "draft",
                        Model = "gpt-5.6-terra",
                        PromptVersion = "ru-runtime-v1",
                        Confidence = "high",
                        NeedsReview = false,
                        RiskFlags = TranslationValidator.DeriveJobRiskFlags(changedSource.Value!, changedHint?.Value, deduplicated: false).ToArray(),
                        ReviewStage = "terra_done",
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    new TranslationRecord
                    {
                        Id = needsReviewSource.Id,
                        SourceSha256 = Hashing.Sha256Text(needsReviewSource.Value!),
                        HintSha256 = needsReviewHint?.Value == null ? null : Hashing.Sha256Text(needsReviewHint.Value),
                        Translation = needsReviewSource.Value!,
                        Status = "draft",
                        Model = "gpt-5.6-terra",
                        PromptVersion = "ru-runtime-v1",
                        Confidence = "low",
                        NeedsReview = true,
                        RiskFlags = TranslationValidator.DeriveJobRiskFlags(needsReviewSource.Value!, needsReviewHint?.Value, deduplicated: false).ToArray(),
                        ReviewStage = "terra_done",
                        UpdatedAt = DateTimeOffset.UtcNow
                    }
                };
                TranslationCatalog.WriteJsonLines(translationsPath, records);
                TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(translationsPath);
                Loc1Document expectedDocument = Loc1Codec.Parse(baseRaw);
                CompositionSummary expectedComposition = TranslationComposer.Apply(
                    english, expectedDocument, catalog, includeDraft: true, approvedOnly: false,
                    excludeNeedsReview: true,
                    allowPerLocaleContentVersion: true,
                    eligibility: (record, sourceText, hintText) => RuntimeSafeDraftPolicy.IsPreviewEligible(record, sourceText, hintText, out _));
                byte[] expectedRaw = Loc1Codec.BuildRaw(expectedDocument);
                Require(expectedRaw.Length != baseRaw.Length,
                    "Runtime-cache smoke mutation must exercise a changed LOC1 data-length header field.");
                var profile = new RuntimeCacheCompatibility
                {
                    Id = "synthetic-runtime-cache",
                    GameVersion = "0.60.test",
                    ContentGuid = english.ContentGuid,
                    EnglishContentVersion = english.ContentVersion,
                    BaseContentVersion = baseLocale.ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(englishRaw),
                    BaseSha256 = Hashing.Sha256Bytes(baseRaw),
                    StampSha256 = Hashing.Sha256File(stampPath),
                    StampValue = "0.60.test",
                    EnglishLocaleId = english.LocaleId,
                    EnglishLocaleRevision = english.LocaleRevision,
                    EnglishReleaseRevision = english.ReleaseRevision,
                    BaseLocaleId = baseLocale.LocaleId,
                    BaseLocaleRevision = baseLocale.LocaleRevision,
                    BaseReleaseRevision = baseLocale.ReleaseRevision,
                    EntryCount = baseLocale.Entries.Count,
                    TranslationPolicy = "supervised-safe-drafts",
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = Hashing.Sha256File(translationsPath),
                    ExpectedOutputSha256 = Hashing.Sha256Bytes(expectedRaw),
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1
                };
                profile.TranslationPolicy = "unknown-policy";
                bool unknownPolicyRejected = false;
                try
                {
                    profile.Validate();
                }
                catch (InvalidDataException)
                {
                    unknownPolicyRejected = true;
                }
                Require(unknownPolicyRejected, "Unknown runtime-cache translation policy was accepted.");
                profile.TranslationPolicy = "supervised-safe-drafts";
                RuntimeCacheInspection original = RuntimeCacheService.Inspect(cache, profile, statePath);
                Require(original.Status == InstallationStatus.CompatibleOriginal, "Synthetic raw runtime cache was not recognized.");
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                File.WriteAllText(statePath, "{ corrupt runtime state", new UTF8Encoding(false));
                RuntimeCacheInspection corruptState = RuntimeCacheService.Inspect(cache, profile, statePath);
                Require(corruptState.Status == InstallationStatus.InconsistentState
                    && corruptState.Message.Contains("unreadable or has an unsupported schema", StringComparison.Ordinal),
                    "Corrupt runtime-cache state over an exact official tuple was reported as ready to apply.");
                bool corruptStateApplyRejected = false;
                try
                {
                    RuntimeCacheService.Apply(corruptState, translationsPath, statePath);
                }
                catch (InvalidOperationException)
                {
                    corruptStateApplyRejected = true;
                }
                Require(corruptStateApplyRejected && File.ReadAllBytes(targetCache).SequenceEqual(baseRaw),
                    "Apply did not reject a corrupt runtime-cache state before mutation.");
                bool postInspectionStateRaceRejected = false;
                try
                {
                    RuntimeCacheService.Apply(original, translationsPath, statePath);
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("appeared after inspection", StringComparison.Ordinal))
                {
                    postInspectionStateRaceRejected = true;
                }
                Require(postInspectionStateRaceRejected && File.ReadAllBytes(targetCache).SequenceEqual(baseRaw),
                    "Apply overwrote runtime-cache state that appeared after inspection.");
                File.Delete(statePath);
                profile.ExpectedAppliedTranslations = 2;
                bool countPinRejected = false;
                try
                {
                    RuntimeCacheService.Apply(original, translationsPath, statePath);
                }
                catch (InvalidDataException exception) when (exception.Message.Contains("exact pin", StringComparison.Ordinal))
                {
                    countPinRejected = true;
                }
                Require(countPinRejected && !File.Exists(statePath) && File.ReadAllBytes(targetCache).SequenceEqual(baseRaw),
                    "Runtime-cache exact applied-count mismatch was not refused before mutation.");
                profile.ExpectedAppliedTranslations = 1;
                original = RuntimeCacheService.Inspect(cache, profile, statePath);
                PatchApplyResult applied = RuntimeCacheService.Apply(original, translationsPath, statePath);
                Require(applied.Composition.AppliedTranslations == expectedComposition.AppliedTranslations,
                    "Synthetic raw cache applied an unexpected translation count.");
                Require(applied.Composition.AppliedTranslations == 1 && applied.Composition.NeedsReviewFallbacks == 1,
                    "Supervised raw cache policy did not exclude needs_review drafts.");
                Require(File.ReadAllBytes(targetCache).Take(4).SequenceEqual(Encoding.ASCII.GetBytes("LOC1")),
                    "Runtime-cache apply wrote a compressed container instead of raw LOC1.");
                Require(Loc1Codec.ReadFile(targetCache).IndexByHash()[changedSource.KeyHash].Value == "Кэш-тест-другой-размер",
                    "Synthetic runtime-cache value was not installed.");
                Require(Loc1Codec.ReadFile(targetCache).IndexByHash()[needsReviewSource.KeyHash].Value == needsReviewSource.Value,
                    "needs_review draft leaked into supervised runtime-cache output.");
                Require(File.Exists(applied.State.BackupPath), "Runtime-cache immutable backup was not created.");
                Require(RuntimeCacheService.Inspect(cache, profile, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Patched runtime cache was not recognized from state.");

                var completedCleanup = new PatchJournal
                {
                    TransactionId = "11111111111111111111111111111111",
                    Operation = "runtime-cache-apply",
                    Phase = "Completed",
                    BuildId = applied.State.BuildId,
                    GameRoot = applied.State.GameRoot,
                    TargetPath = applied.State.TargetPath,
                    BackupPath = applied.State.BackupPath,
                    QuarantinePath = Path.Combine(cache, ".dl_uk_UA.bin.11111111111111111111111111111111.displaced"),
                    RollbackPath = Path.Combine(cache, ".dl_uk_UA.bin.11111111111111111111111111111111.rollback"),
                    DisplacedSha256 = applied.State.OriginalSha256,
                    SourceSha256 = applied.State.OriginalSha256,
                    ExpectedOutputSha256 = applied.State.PatchedSha256,
                    TranslationsSha256 = applied.State.TranslationsSha256,
                    AppliedTranslations = applied.State.AppliedTranslations,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                File.Copy(applied.State.BackupPath, completedCleanup.QuarantinePath);
                PatchJournalStore.Save(statePath, completedCleanup);
                string completedCleanupResult = RuntimeCacheService.Recover(statePath, profile);
                Require(completedCleanupResult.Contains("Completed cleanup", StringComparison.Ordinal)
                    && !File.Exists(completedCleanup.QuarantinePath)
                    && PatchJournalStore.FindActive(statePath) == null
                    && File.Exists(statePath)
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetCache), applied.State.PatchedSha256),
                    "Crash after durable Completed did not safely finish quarantine/journal cleanup.");

                byte[] validStateBytes = File.ReadAllBytes(statePath);
                string forgedRoot = Path.Combine(root, "forged-cache-root");
                Directory.CreateDirectory(forgedRoot);
                File.WriteAllBytes(Path.Combine(forgedRoot, "dl_uk_UA.bin"), Encoding.ASCII.GetBytes("forged-target"));
                PatchState forgedState = JsonSerializer.Deserialize<PatchState>(validStateBytes)
                    ?? throw new InvalidOperationException("Synthetic state could not be cloned.");
                forgedState.GameRoot = forgedRoot;
                forgedState.TargetPath = Path.Combine(forgedRoot, "dl_uk_UA.bin");
                File.WriteAllText(statePath, JsonSerializer.Serialize(forgedState));
                bool forgedStateRejected = false;
                try
                {
                    RuntimeCacheService.Restore(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    forgedStateRejected = true;
                }
                Require(forgedStateRejected && File.ReadAllText(Path.Combine(forgedRoot, "dl_uk_UA.bin")) == "forged-target"
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetCache), applied.State.PatchedSha256),
                    "Forged runtime state redirected restore outside the bound cache root.");
                forgedState = JsonSerializer.Deserialize<PatchState>(validStateBytes)!;
                forgedState.BackupPath = Path.Combine(root, "forged-backup.bin");
                File.WriteAllText(statePath, JsonSerializer.Serialize(forgedState));
                bool forgedStateBackupRejected = false;
                try
                {
                    RuntimeCacheService.Restore(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    forgedStateBackupRejected = true;
                }
                Require(forgedStateBackupRejected && Hashing.FixedEqualsHex(Hashing.Sha256File(targetCache), applied.State.PatchedSha256),
                    "Forged runtime state backup path was accepted.");
                File.WriteAllBytes(statePath, validStateBytes);

                string journalDirectory = Path.Combine(Path.GetDirectoryName(statePath)!, "journals");
                Directory.CreateDirectory(journalDirectory);
                File.Delete(statePath);
                var interrupted = new PatchJournal
                {
                    TransactionId = "abcdef0123456789abcdef0123456789",
                    Operation = "runtime-cache-apply",
                    Phase = "ReplacementCommitted",
                    BuildId = applied.State.BuildId,
                    GameRoot = applied.State.GameRoot,
                    TargetPath = applied.State.TargetPath,
                    BackupPath = applied.State.BackupPath,
                    QuarantinePath = Path.Combine(cache, ".dl_uk_UA.bin.abcdef0123456789abcdef0123456789.displaced"),
                    RollbackPath = Path.Combine(cache, ".dl_uk_UA.bin.abcdef0123456789abcdef0123456789.rollback"),
                    DisplacedSha256 = applied.State.OriginalSha256,
                    SourceSha256 = applied.State.OriginalSha256,
                    ExpectedOutputSha256 = applied.State.PatchedSha256,
                    TranslationsSha256 = applied.State.TranslationsSha256,
                    AppliedTranslations = applied.State.AppliedTranslations,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                File.Copy(applied.State.BackupPath, interrupted.QuarantinePath);
                string interruptedPath = Path.Combine(journalDirectory, interrupted.TransactionId + ".json");
                string validJournalJson = JsonSerializer.Serialize(interrupted);
                interrupted.GameRoot = forgedRoot;
                interrupted.TargetPath = Path.Combine(forgedRoot, "dl_uk_UA.bin");
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool forgedJournalRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    forgedJournalRejected = true;
                }
                Require(forgedJournalRejected && File.ReadAllText(Path.Combine(forgedRoot, "dl_uk_UA.bin")) == "forged-target",
                    "Forged runtime journal redirected recovery outside the bound cache root.");
                File.WriteAllText(interruptedPath, validJournalJson);
                interrupted = JsonSerializer.Deserialize<PatchJournal>(validJournalJson)!;
                interrupted.Schema = 2;
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool unknownSchemaRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    unknownSchemaRejected = true;
                }
                Require(unknownSchemaRejected, "Unknown active runtime journal schema was ignored.");
                interrupted.Schema = 1;
                interrupted.Operation = "runtime-cache-unknown";
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool unknownOperationRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    unknownOperationRejected = true;
                }
                Require(unknownOperationRejected, "Unknown runtime recovery operation was accepted.");
                interrupted.Operation = "runtime-cache-apply";
                interrupted.Phase = "UnknownPhase";
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool unknownPhaseRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    unknownPhaseRejected = true;
                }
                Require(unknownPhaseRejected, "Unknown runtime recovery phase was accepted.");
                interrupted.Phase = "ReplacementCommitted";
                interrupted.AppliedTranslations++;
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool forgedCountRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    forgedCountRejected = true;
                }
                Require(forgedCountRejected, "Forged runtime recovery applied count was accepted.");
                interrupted.AppliedTranslations--;
                string validQuarantinePath = interrupted.QuarantinePath;
                interrupted.QuarantinePath = Path.Combine(root, "forged-quarantine.bin");
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                bool forgedQuarantineRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    forgedQuarantineRejected = true;
                }
                Require(forgedQuarantineRejected, "Forged runtime recovery quarantine path was accepted.");
                interrupted.QuarantinePath = validQuarantinePath;
                File.WriteAllText(interruptedPath, JsonSerializer.Serialize(interrupted));
                File.WriteAllText(statePath, "{not-valid-state");
                bool corruptRecoveryStateRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    corruptRecoveryStateRejected = true;
                }
                Require(corruptRecoveryStateRejected && File.Exists(interrupted.QuarantinePath),
                    "Corrupt runtime recovery state was not rejected before quarantine processing.");
                File.Delete(statePath);
                byte[] committedOutput = File.ReadAllBytes(targetCache);
                File.Copy(applied.State.BackupPath, targetCache, overwrite: true);
                File.Delete(interrupted.QuarantinePath);
                File.WriteAllText(interruptedPath, validJournalJson);
                bool impossibleCommittedPhaseRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    impossibleCommittedPhaseRejected = true;
                }
                Require(impossibleCommittedPhaseRejected && File.Exists(interruptedPath),
                    "Post-replace runtime journal phase was incorrectly cleared without quarantine/output.");
                File.WriteAllBytes(targetCache, committedOutput);
                File.Copy(applied.State.BackupPath, interrupted.QuarantinePath);
                File.WriteAllText(interruptedPath, validJournalJson);
                string recovered = RuntimeCacheService.Recover(statePath, profile);
                Require(recovered.Contains("Recovered", StringComparison.Ordinal) && File.Exists(statePath),
                    "Runtime-cache journal recovery did not reconstruct patch state.");
                RuntimeCacheService.Restore(statePath, profile);
                Require(!File.Exists(statePath) && File.ReadAllBytes(targetCache).SequenceEqual(baseRaw),
                    "Runtime-cache restore did not return exact original raw bytes.");

                File.WriteAllText(stampPath, "0.60.changed", new UTF8Encoding(false));
                RuntimeCacheInspection changedStamp = RuntimeCacheService.Inspect(cache, profile, statePath);
                Require(changedStamp.Status == InstallationStatus.UnknownBuild,
                    "Changed runtime-cache version stamp was accepted.");
                File.WriteAllText(stampPath, "0.60.test", new UTF8Encoding(false));

                RuntimeCacheInspection restoreRaceOriginal = RuntimeCacheService.Inspect(cache, profile, statePath);
                RuntimeCacheService.Apply(restoreRaceOriginal, translationsPath, statePath);
                byte[] restoreCompetitor = Encoding.ASCII.GetBytes("competing-official-update-during-restore");
                MutationTestHooks.BeforeAtomicReplace = path => AtomicRaceSwap(path, restoreCompetitor);
                bool restoreRaceRejected = false;
                try
                {
                    RuntimeCacheService.Restore(statePath, profile);
                }
                catch (IOException exception) when (exception.Message.Contains("competing bytes", StringComparison.Ordinal))
                {
                    restoreRaceRejected = true;
                }
                finally
                {
                    MutationTestHooks.BeforeAtomicReplace = null;
                }
                PatchJournal restoreRaceJournal = PatchJournalStore.FindActive(statePath)
                    ?? throw new InvalidOperationException("Restore race did not retain its recovery journal.");
                Require(restoreRaceRejected && File.ReadAllBytes(targetCache).SequenceEqual(restoreCompetitor)
                    && File.Exists(restoreRaceJournal.RollbackPath)
                    && File.ReadAllBytes(restoreRaceJournal.RollbackPath).SequenceEqual(baseRaw),
                    "Restore race did not preserve the competing target and staged original bytes.");
                bool restoreRaceRecoveryRefused = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (IOException exception) when (exception.Message.Contains("both sides", StringComparison.Ordinal))
                {
                    restoreRaceRecoveryRefused = true;
                }
                Require(restoreRaceRecoveryRefused && File.ReadAllBytes(targetCache).SequenceEqual(restoreCompetitor),
                    "Restore-race recovery did not fail closed with both sides preserved.");
                byte[] postRaceTamper = Encoding.ASCII.GetBytes("changed-after-race-rollback");
                File.WriteAllBytes(targetCache, postRaceTamper);
                bool changedRestoredSideRejected = false;
                try
                {
                    RuntimeCacheService.Recover(statePath, profile);
                }
                catch (InvalidDataException)
                {
                    changedRestoredSideRejected = true;
                }
                Require(changedRestoredSideRejected
                    && File.ReadAllBytes(targetCache).SequenceEqual(postRaceTamper)
                    && File.ReadAllBytes(restoreRaceJournal.RollbackPath).SequenceEqual(baseRaw),
                    "Recovery did not validate both preserved race sides before reporting manual recovery.");

                string applyRaceCache = Path.Combine(root, "apply-race-cache");
                string applyRaceState = Path.Combine(root, "apply-race-state", "state.v1.json");
                Directory.CreateDirectory(applyRaceCache);
                File.WriteAllBytes(Path.Combine(applyRaceCache, "dl_en_US.bin"), englishRaw);
                File.WriteAllBytes(Path.Combine(applyRaceCache, "dl_uk_UA.bin"), baseRaw);
                File.WriteAllText(Path.Combine(applyRaceCache, "dl_uk_UA.bin.ver"), "0.60.test", new UTF8Encoding(false));
                RuntimeCacheInspection applyRaceOriginal = RuntimeCacheService.Inspect(applyRaceCache, profile, applyRaceState);
                byte[] applyCompetitor = Encoding.ASCII.GetBytes("competing-official-update-during-apply");
                MutationTestHooks.BeforeAtomicReplace = path => AtomicRaceSwap(path, applyCompetitor);
                bool applyRaceRejected = false;
                try
                {
                    RuntimeCacheService.Apply(applyRaceOriginal, translationsPath, applyRaceState);
                }
                catch (IOException exception) when (exception.Message.Contains("competing bytes", StringComparison.Ordinal))
                {
                    applyRaceRejected = true;
                }
                finally
                {
                    MutationTestHooks.BeforeAtomicReplace = null;
                }
                PatchJournal applyRaceJournal = PatchJournalStore.FindActive(applyRaceState)
                    ?? throw new InvalidOperationException("Apply race did not retain its recovery journal.");
                Require(applyRaceRejected && File.ReadAllBytes(Path.Combine(applyRaceCache, "dl_uk_UA.bin")).SequenceEqual(applyCompetitor)
                    && File.Exists(applyRaceJournal.RollbackPath)
                    && File.ReadAllBytes(applyRaceJournal.RollbackPath).SequenceEqual(expectedRaw),
                    "Apply race did not preserve the competing target and staged patched bytes.");
                bool applyRaceRecoveryRefused = false;
                try
                {
                    RuntimeCacheService.Recover(applyRaceState, profile);
                }
                catch (IOException exception) when (exception.Message.Contains("both sides", StringComparison.Ordinal))
                {
                    applyRaceRecoveryRefused = true;
                }
                Require(applyRaceRecoveryRefused
                    && File.ReadAllBytes(Path.Combine(applyRaceCache, "dl_uk_UA.bin")).SequenceEqual(applyCompetitor),
                    "Apply-race recovery did not fail closed with both sides preserved.");

            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Passed.Add("raw runtime-cache round-trip/apply/restore, recovery gates, and displaced-preimage apply/restore races");
        }

        private static void OrdinaryCoreMutationGateCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-dev-negative-{Guid.NewGuid():N}");
            string statePath = Path.Combine(root, "state", "state.json");
            int rejected = 0;
            Action[] calls =
            {
                () => PatchService.Apply(new InstallationInspection { GameRoot = root }, Path.Combine(root, "missing-catalog.jsonl"), statePath, includeDraft: false),
                () => PatchService.Restore(statePath, null!),
                () => PatchService.Recover(statePath, null!),
                () => RuntimeCacheService.Apply(new RuntimeCacheInspection { CacheRoot = root }, Path.Combine(root, "missing-catalog.jsonl"), statePath),
                () => RuntimeCacheService.Restore(statePath, null!),
                () => RuntimeCacheService.Recover(statePath, null!)
            };
            foreach (Action call in calls)
            {
                try
                {
                    call();
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("compile-time disabled", StringComparison.Ordinal))
                {
                    rejected++;
                }
            }

            Require(rejected == calls.Length && !Directory.Exists(root),
                "Ordinary Core did not reject all mutation entrypoints before filesystem access.");
            Passed.Add("ordinary Core compile-time gate rejects all six direct mutation entrypoints before I/O");
        }

        private static void AtomicRaceSwap(string targetPath, byte[] competingBytes)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(targetPath))
                ?? throw new InvalidDataException("Race-test target has no directory.");
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.race-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, competingBytes);
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static Loc1Document ParseWithContentVersion(string path, string version)
        {
            return Loc1Codec.Parse(RawWithContentVersion(path, version));
        }

        private static byte[] RawWithContentVersion(string path, string version)
        {
            byte[] raw = Loc1Codec.ReadContainer(path);
            Loc1Document original = Loc1Codec.Parse(raw);
            byte[] versionBytes = Encoding.UTF8.GetBytes(version);
            int guidLength = BitConverter.ToUInt16(raw, 0x50);
            int versionLengthOffset = 0x52 + guidLength;
            int originalLength = BitConverter.ToUInt16(raw, versionLengthOffset);
            Require(versionBytes.Length == originalLength, "Synthetic ContentVersion must preserve header length.");
            byte[] changed = (byte[])raw.Clone();
            Buffer.BlockCopy(versionBytes, 0, changed, versionLengthOffset + 2, versionBytes.Length);
            Loc1Document parsed = Loc1Codec.Parse(changed);
            Require(parsed.ContentGuid == original.ContentGuid && parsed.ContentVersion == version,
                "Synthetic ContentVersion mutation changed the wrong header field.");
            return changed;
        }

        private static Loc1Document CloneWithHeaderMutation(Loc1Document document, Action<byte[]> mutate)
        {
            byte[] raw = Loc1Codec.BuildRaw(document);
            mutate(raw);
            return Loc1Codec.Parse(raw);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
