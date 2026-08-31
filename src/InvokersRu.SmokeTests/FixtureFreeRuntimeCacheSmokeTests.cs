using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using InvokersRu.Cli;
using InvokersRu.Gui;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvokersRu.SmokeTests
{
    internal static class FixtureFreeRuntimeCacheSmokeTests
    {
        private const string CurrentProfileFileName = "runtime-cache-profile.0.60.1247.json";
        private const string ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";
        private const string ContentVersion = "Prod_synthetic_68";

        internal static void Run(Action<string> passed)
        {
            string profilePath = Path.Combine(AppContext.BaseDirectory, CurrentProfileFileName);
            CurrentProfileStrictParsingCheck(profilePath);
            passed("0.60.1247 profile strict parsing, community policy, and fallback pins");

            EmbeddedHistoricalAuthorityCheck();

            CliJsonContractCheck(profilePath);
            passed("CLI cache-plan JSON contract is consumable without a GUI reference");

            SignedRuntimeAuthorizationCheck();
            passed("CLI plan and direct mutation share the same signed-data authorization policy");

            StrictTranslationResultImportCheck();
            passed("model-result JSONL uses the exact seven-field schema before draft merge");

            CompatibleRevisionMaterializationCheck();
            passed("compatible revisions require raw schema/GUID/locale/key identity and exact source+hint rows");

            if (MutationCapability.IsTestWriteBuild)
            {
                SupersededOfficialUpdateInspectionCheck();
                passed("official update supersedes only a completely restorable old runtime-cache state");

                CatalogSupersededMigrationCheck();
                passed("allowlisted catalog upgrade applies/restores/recovers atomically and rejects tamper");

                CompatibleRevisionTransactionAndUpdaterCheck();
                passed("compatible revision persists exact snapshots, survives restart/update, and rejects stale/tampered state");
            }
        }

        private static void SignedRuntimeAuthorizationCheck()
        {
            const string profileId = "runtime-cache-profile";
            string baseSha256 = new string('A', 64);
            string outputSha256 = new string('B', 64);
            string catalogSha256 = new string('C', 64);
            const int appliedTranslations = 17;
            bool Authorized(
                bool remoteProblem,
                bool channelAuthority,
                bool bundle,
                bool patcherTooOld,
                bool expired,
                bool lkg,
                string? installedBuildId = null,
                bool exactInstalledArtifact = false,
                bool remoteProblemBlocksApply = false)
            {
                return RuntimeUpdateAuthorization.CanApply(
                    remoteProblem,
                    channelAuthority,
                    bundle,
                    patcherTooOld,
                    expired,
                    lkg,
                    profileId,
                    baseSha256,
                    outputSha256,
                    catalogSha256,
                    appliedTranslations,
                    installedBuildId,
                    exactInstalledArtifact ? baseSha256 : new string('D', 64),
                    exactInstalledArtifact ? outputSha256 : new string('E', 64),
                    exactInstalledArtifact ? catalogSha256 : new string('F', 64),
                    exactInstalledArtifact ? appliedTranslations : appliedTranslations - 1,
                    remoteProblemBlocksApply);
            }

            Require(Authorized(false, false, false, false, false, false),
                "The embedded offline bootstrap was rejected without an accepted channel authority.");
            Require(Authorized(true, false, false, false, false, false),
                "A mere first-run network failure blocked the independently trusted embedded bootstrap.");
            Require(!Authorized(true, false, false, false, false, false, remoteProblemBlocksApply: true),
                "A corrupt authenticated-channel/config fallback was mislabeled as a benign offline bootstrap.");
            Require(Authorized(true, true, true, false, false, false, remoteProblemBlocksApply: false),
                "A typed non-blocking warning disabled an otherwise current authenticated bundle.");
            Require(!Authorized(false, true, false, false, false, false),
                "An authenticated channel head without an authorized bundle was accepted.");
            Require(!Authorized(true, true, true, false, false, false, profileId, remoteProblemBlocksApply: true),
                "A resolver/channel error did not block signed-data installation.");
            Require(!Authorized(false, true, true, true, false, false, profileId),
                "A below-minimum patcher was allowed to apply signed data.");
            Require(Authorized(false, true, true, false, false, false),
                "A current authenticated bundle was rejected.");
            Require(Authorized(false, true, true, false, false, true, profileId, exactInstalledArtifact: true),
                "A current exact LKG could not maintain its already-recorded installation.");
            Require(!Authorized(false, true, true, false, false, true, profileId, exactInstalledArtifact: false),
                "A current LKG was allowed to replace a same-id but different installed artifact.");
            Require(!Authorized(false, true, true, false, true, true),
                "An expired LKG was allowed to establish a fresh installation after restore.");
            Require(!Authorized(false, true, true, false, true, false, profileId, exactInstalledArtifact: true),
                "An expired non-LKG bundle was accepted for installation.");
            Require(Authorized(false, true, true, false, true, true, profileId, exactInstalledArtifact: true),
                "An expired exact LKG could not maintain its already-recorded installation.");
            Require(!Authorized(false, true, true, false, true, true, profileId, exactInstalledArtifact: false),
                "An expired LKG was allowed to replace a same-id but different installed artifact.");
            Require(RuntimeUpdateAuthorization.CanRestoreOrRecover(hasExactRestorableInspection: true)
                && !RuntimeUpdateAuthorization.CanRestoreOrRecover(hasExactRestorableInspection: false),
                "Restore/recovery authorization is not tied to an exact restorable inspection.");
            var authorizationProfile = new RuntimeCacheCompatibility { Id = profileId };
            var unauthenticatedRecovery = new RuntimeUpdateResolution
            {
                Profile = authorizationProfile,
                Inspection = new RuntimeCacheInspection
                {
                    Status = InstallationStatus.RecoveryRequired,
                    Profile = authorizationProfile
                },
                CatalogPath = string.Empty
            };
            Require(!RuntimeUpdateAuthorization.CanRestoreOrRecover(unauthenticatedRecovery),
                "A generic parseable journal status authorized recovery without an exact installed recovery profile.");
            var embeddedRestoreWithChannelProblem = new RuntimeUpdateResolution
            {
                Profile = authorizationProfile,
                Inspection = new RuntimeCacheInspection
                {
                    Status = InstallationStatus.PatchedByThisTool,
                    Profile = authorizationProfile
                },
                CatalogPath = string.Empty,
                RemoteProblem = "Newest channel metadata is unavailable."
            };
            Require(RuntimeUpdateAuthorization.CanRestoreOrRecover(embeddedRestoreWithChannelProblem),
                "A channel/catalog problem hid restoration of an independently exact installed patch.");
            var historicalRestoreWithoutCurrentGameProfile = new RuntimeUpdateResolution
            {
                Profile = authorizationProfile,
                Inspection = new RuntimeCacheInspection
                {
                    Status = InstallationStatus.UnknownBuild,
                    Profile = authorizationProfile
                },
                CatalogPath = string.Empty,
                InstalledProfile = authorizationProfile,
                InstalledInspection = new RuntimeCacheInspection
                {
                    Status = InstallationStatus.PatchedByThisTool,
                    Profile = authorizationProfile
                },
                RemoteProblem = "No signed exact profile is published for the current official tuple."
            };
            Require(RuntimeUpdateAuthorization.CanRestoreOrRecover(historicalRestoreWithoutCurrentGameProfile),
                "A valid historical installed profile could not authorize restoration after channel rollover.");
            Require(RuntimeUpdateAuthorization.CanUseSelectedCatalogForApply(selectedCatalogExact: true)
                && !RuntimeUpdateAuthorization.CanUseSelectedCatalogForApply(selectedCatalogExact: false),
                "Apply authorization is not tied to the selected profile's exact catalog.");
        }

        private static void StrictTranslationResultImportCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-strict-results-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                Loc1Document english = Loc1Codec.Parse(CreateLoc1(1, 68, 0x1234ABCD, new[] { "Open" }));
                Loc1Entry source = english.Entries[0];
                var job = new TranslationJob
                {
                    ContentGuid = english.ContentGuid,
                    ContentVersion = english.ContentVersion,
                    Ids = new[] { source.Id },
                    SourceSha256 = Hashing.Sha256Text(source.Value!),
                    English = source.Value!,
                    ProtectedTokens = Array.Empty<string>(),
                    RiskFlags = Array.Empty<string>(),
                    Deduplicated = false
                };
                job.JobId = TranslationCatalog.ComputeJobId(job);

                string jobsPath = Path.Combine(root, "jobs.jsonl");
                string resultsPath = Path.Combine(root, "results.jsonl");
                File.WriteAllText(
                    jobsPath,
                    JsonSerializer.Serialize(job) + Environment.NewLine,
                    new UTF8Encoding(false));

                var exactResult = new JsonObject
                {
                    ["job_id"] = job.JobId,
                    ["translation"] = "Открыть",
                    ["model"] = "fixture-model",
                    ["prompt_version"] = "community-translation-v1",
                    ["confidence"] = "high",
                    ["needs_review"] = false,
                    ["issue_codes"] = new JsonArray()
                };
                string exactJson = exactResult.ToJsonString();
                File.WriteAllText(resultsPath, exactJson + Environment.NewLine, new UTF8Encoding(false));

                string positiveOutput = Path.Combine(root, "positive.jsonl");
                ImportSummary summary = TranslationCatalog.ImportDraftResults(
                    english,
                    jobsPath,
                    resultsPath,
                    TranslationCatalog.Empty(),
                    positiveOutput);
                TranslationCatalog imported = TranslationCatalog.LoadJsonLines(positiveOutput);
                Require(summary.ImportedIds == 1
                    && imported.TryGetUsable(source.KeyHash, source.Value!, includeDraft: true, out TranslationRecord? record, out _)
                    && record!.Translation == "Открыть"
                    && record.Model == "fixture-model"
                    && record.PromptVersion == "community-translation-v1"
                    && record.IssueCodes.Length == 0,
                    "An exact seven-field model result did not import as a bound draft.");

                int rejectionIndex = 0;
                void RequireRejected(string caseName, string resultJson)
                {
                    File.WriteAllText(resultsPath, resultJson + Environment.NewLine, new UTF8Encoding(false));
                    string rejectedOutput = Path.Combine(root, $"rejected-{rejectionIndex++}.jsonl");
                    bool rejected = false;
                    try
                    {
                        TranslationCatalog.ImportDraftResults(
                            english,
                            jobsPath,
                            resultsPath,
                            TranslationCatalog.Empty(),
                            rejectedOutput);
                    }
                    catch (InvalidDataException)
                    {
                        rejected = true;
                    }

                    Require(rejected, $"The strict result importer accepted {caseName}.");
                    Require(!File.Exists(rejectedOutput), $"The rejected {caseName} result created an output catalog.");
                }

                string[] requiredFields =
                {
                    "job_id", "translation", "model", "prompt_version", "confidence", "needs_review", "issue_codes"
                };
                foreach (string field in requiredFields)
                {
                    JsonObject missingField = ParseObject(exactJson);
                    missingField.Remove(field);
                    RequireRejected($"a result missing required field '{field}'", missingField.ToJsonString());
                }

                JsonObject wrongCase = ParseObject(exactJson);
                wrongCase.Remove("job_id");
                wrongCase["Job_Id"] = job.JobId;
                RequireRejected("a case-mismatched property name", wrongCase.ToJsonString());

                JsonObject unknownMember = ParseObject(exactJson);
                unknownMember["unexpected"] = true;
                RequireRejected("an unknown property", unknownMember.ToJsonString());

                RequireRejected(
                    "a duplicate JSON property",
                    exactJson.Insert(1, "\"model\":\"duplicate-model\","));

                JsonObject overlongModel = ParseObject(exactJson);
                overlongModel["model"] = new string('m', 129);
                RequireRejected("a model longer than 128 characters", overlongModel.ToJsonString());

                JsonObject whitespaceModel = ParseObject(exactJson);
                whitespaceModel["model"] = " \t ";
                RequireRejected("a whitespace-only model", whitespaceModel.ToJsonString());

                JsonObject badPrompt = ParseObject(exactJson);
                badPrompt["prompt_version"] = "bad prompt";
                RequireRejected("an unsafe prompt_version", badPrompt.ToJsonString());

                JsonObject duplicateIssueCodes = ParseObject(exactJson);
                duplicateIssueCodes["issue_codes"] = new JsonArray(
                    JsonValue.Create("terminology"),
                    JsonValue.Create("terminology"));
                RequireRejected("duplicate issue_codes", duplicateIssueCodes.ToJsonString());
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void CurrentProfileStrictParsingCheck(string profilePath)
        {
            Require(File.Exists(profilePath), $"Current runtime-cache profile was not copied to smoke output: {profilePath}");
            string json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(profilePath)).TrimStart('\uFEFF');
            RuntimeCacheCompatibility profile = RuntimeCacheCompatibility.Parse(json);

            Require(profile.Id == "runtime-cache-win64-0.60.1247-prod71"
                && profile.GameVersion == "0.60.1247"
                && profile.StampValue == profile.GameVersion
                && profile.EnglishContentVersion == "Prod_0.60.0_68"
                && profile.BaseContentVersion == "Prod_0.60.0_71"
                && profile.BaseReleaseRevision == 71
                && profile.EntryCount == 41_292
                && profile.ExpectedAppliedTranslations == 41_037
                && profile.ExpectedEnglishFallbacks == 1
                && profile.ExpectedBaseFallbacks == 254
                && profile.ExpectedNeedsReviewFallbacks == 0
                && profile.TranslationPolicy == "community-preview-all-drafts"
                && profile.SupersededArtifacts.Length == 2
                && profile.SupersededArtifacts[0].OutputSha256 == "105B6A9047E6FB0E2C34B774CAE273CC024831C06896C56C4F65F92D2F920541"
                && profile.SupersededArtifacts[0].TranslationCatalogSha256 == "C9E6281FC7918886F1A8943FD39C41B7146230F6D526AB65EA1AAFE5BF10358B"
                && profile.SupersededArtifacts[0].AppliedTranslations == 41_037
                && profile.SupersededArtifacts[0].EnglishFallbacks == 1
                && profile.SupersededArtifacts[0].BaseFallbacks == 254
                && profile.SupersededArtifacts[0].NeedsReviewFallbacks == 0
                && profile.SupersededArtifacts[1].OutputSha256 == "4E3AC2FBF663DA1D3EBF4B2EAEBFAADF5BC2A80AE7E424463582919F07B644CD"
                && profile.SupersededArtifacts[1].TranslationCatalogSha256 == "CBF5211BDCB8E940829F305154CE9A92C0D202BCC6B49617A4B92BA25166600C"
                && profile.SupersededArtifacts[1].AppliedTranslations == 41_037
                && profile.Certified
                && profile.Readiness == "ready",
                "The current runtime-cache release profile lost one or more exact compatibility/composition pins.");
            Require(profile.ExpectedAppliedTranslations + profile.ExpectedEnglishFallbacks + profile.ExpectedBaseFallbacks == profile.EntryCount,
                "The current runtime-cache profile no longer accounts for every LOC1 entry exactly once.");

            JsonObject fallbackMismatch = ParseObject(json);
            fallbackMismatch["expected_english_fallbacks"] = 2;
            Require(ProfileRejected(fallbackMismatch.ToJsonString()),
                "A certified profile whose RU/English/base composition does not sum to entry_count was accepted.");

            JsonObject invalidPolicy = ParseObject(json);
            invalidPolicy["translation_policy"] = "community-preview-all-drafts-typo";
            Require(ProfileRejected(invalidPolicy.ToJsonString()),
                "An unknown community translation policy was accepted.");

            JsonObject unknownMember = ParseObject(json);
            unknownMember["unexpected_release_override"] = true;
            Require(ProfileRejected(unknownMember.ToJsonString()),
                "An unknown property in the trusted runtime-cache profile was accepted.");

            int objectStart = json.IndexOf('{');
            Require(objectStart >= 0, "Current runtime-cache profile is not a JSON object.");
            string duplicateSchema = json.Insert(objectStart + 1, "\"schema\":1,");
            Require(ProfileRejected(duplicateSchema),
                "A duplicate property in the trusted runtime-cache profile was accepted.");
        }

        private static void SupersededOfficialUpdateInspectionCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-superseded-{Guid.NewGuid():N}");
            string cacheRoot = Path.Combine(root, "cache");
            string stateRoot = Path.Combine(root, "state");
            string statePath = Path.Combine(stateRoot, "state.v1.json");
            Directory.CreateDirectory(cacheRoot);
            try
            {
                byte[] english = CreateLoc1(1, 68, 0x1234ABCD, new[] { "Open", "Exit" });
                byte[] officialBase = CreateLoc1(8, 68, 0xD7A0FEFB, new[] { "Відкрити", "Вийти" });
                byte[] stamp = Encoding.UTF8.GetBytes("0.60.synthetic");
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
                File.WriteAllBytes(englishPath, english);
                File.WriteAllBytes(targetPath, officialBase);
                File.WriteAllBytes(stampPath, stamp);

                var profile = new RuntimeCacheCompatibility
                {
                    Id = "synthetic-runtime-cache-current",
                    GameVersion = "0.60.synthetic",
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(english),
                    BaseSha256 = Hashing.Sha256Bytes(officialBase),
                    StampSha256 = Hashing.Sha256Bytes(stamp),
                    StampValue = "0.60.synthetic",
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x1234ABCD,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0xD7A0FEFB,
                    BaseReleaseRevision = 68,
                    EntryCount = 2,
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = new string('A', 64),
                    ExpectedOutputSha256 = new string('B', 64),
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1,
                    ExpectedEnglishFallbacks = 0,
                    ExpectedBaseFallbacks = 1,
                    ExpectedNeedsReviewFallbacks = 0,
                    TranslationPolicy = "community-preview-all-drafts"
                };
                profile.Validate();

                byte[] previousOfficialBase = CreateLoc1(8, 67, 0xD7A0FEFA, new[] { "Відкрити", "Вихід" });
                string previousOriginalHash = Hashing.Sha256Bytes(previousOfficialBase);
                string retiredBuildId = "retired-runtime-cache";
                var predecessor = new RuntimeCacheCompatibility
                {
                    Id = retiredBuildId,
                    GameVersion = profile.GameVersion,
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = profile.EnglishSha256,
                    BaseSha256 = previousOriginalHash,
                    StampSha256 = profile.StampSha256,
                    StampValue = profile.StampValue,
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x1234ABCD,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0xD7A0FEFA,
                    BaseReleaseRevision = 67,
                    EntryCount = 2,
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = new string('D', 64),
                    ExpectedOutputSha256 = new string('C', 64),
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1,
                    ExpectedEnglishFallbacks = 0,
                    ExpectedBaseFallbacks = 1,
                    ExpectedNeedsReviewFallbacks = 0,
                    TranslationPolicy = "community-preview-all-drafts"
                };
                predecessor.Validate();
                string retiredBackupDirectory = $"{retiredBuildId}-{Hashing.Sha256Text(retiredBuildId).Substring(0, 12)}";
                string backupPath = Path.Combine(stateRoot, "backups", retiredBackupDirectory, $"{previousOriginalHash}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, previousOfficialBase);
                var oldState = new PatchState
                {
                    BuildId = retiredBuildId,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    OriginalSha256 = previousOriginalHash,
                    PatchedSha256 = new string('C', 64),
                    TranslationsSha256 = new string('D', 64),
                    AppliedAt = DateTimeOffset.Parse("2026-08-19T18:00:00Z", CultureInfo.InvariantCulture),
                    AppliedTranslations = 1
                };
                Directory.CreateDirectory(stateRoot);
                File.WriteAllText(statePath, JsonSerializer.Serialize(oldState), new UTF8Encoding(false));

                RuntimeCacheInspection superseded = RuntimeCacheService.Inspect(cacheRoot, profile, statePath, predecessor);
                Require(superseded.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && superseded.State != null
                    && superseded.Message.Contains("game update replaced", StringComparison.OrdinalIgnoreCase),
                    "An exact official tuple with a valid old state/backup was not classified as superseded by an official update.");

                File.WriteAllBytes(backupPath, Encoding.UTF8.GetBytes("corrupted stale backup"));
                RuntimeCacheInspection invalidBackup = RuntimeCacheService.Inspect(cacheRoot, profile, statePath, predecessor);
                Require(invalidBackup.Status == InstallationStatus.InconsistentState
                    && File.ReadAllBytes(targetPath).SequenceEqual(officialBase),
                    "An exact official tuple with an invalid stale backup was allowed to reapply.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void EmbeddedHistoricalAuthorityCheck()
        {
            RuntimeCacheCompatibility[] history = EmbeddedRuntimeCacheHistory.CreateProfiles().ToArray();
            Require(history.Length == 1, "The compiled exact-profile recovery history is ambiguous.");
            RuntimeCacheCompatibility prod68 = history[0];
            Require(prod68.Id == "runtime-cache-win64-0.60.1247-prod68"
                && prod68.Mode == "exact"
                && prod68.BaseContentVersion == "Prod_0.60.0_68"
                && prod68.BaseSha256 == "D32B038163DCA0D9830C764234AE2D5EACB5F07CD2FEBD2D2EF14B9F529E0B43"
                && prod68.TranslationCatalogSha256 == "C9E6281FC7918886F1A8943FD39C41B7146230F6D526AB65EA1AAFE5BF10358B"
                && prod68.ExpectedOutputSha256 == "105B6A9047E6FB0E2C34B774CAE273CC024831C06896C56C4F65F92D2F920541"
                && prod68.OrderedKeysetSha256 == "3B29E4AC11AC3EAF41E79B3C856E93B3DE2BEF4E6442AAC5D8F9EBA9EAAFDFB8"
                && prod68.ExpectedAppliedTranslations == 41_037
                && prod68.SupersededArtifacts.Length == 1,
                "The compiled prod68 recovery authority lost an immutable tuple/artifact pin.");
        }

        private static void CompatibleRevisionMaterializationCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-compatible-revision-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string[] englishValues =
                {
                    "Open",
                    "Exit",
                    "Gain 10%",
                    "Value {0}",
                    "<b>Open</b>",
                    "Line1\nLine2",
                    "Brand new"
                };
                string[] baseValues =
                {
                    "Відкрити",
                    "Вийти",
                    "Отримати 10%",
                    "Значення {0}",
                    "<b>Відкрити</b>",
                    "Рядок1\nРядок2",
                    "Зовсім нове"
                };
                byte[] englishRaw = CreateLoc1(1, 169, 0x1234ABCD, englishValues, "Prod_synthetic_169");
                byte[] baseRaw = CreateLoc1(8, 169, 0xD7A0FEFB, baseValues, "Prod_synthetic_169");
                byte[] stampRaw = Encoding.UTF8.GetBytes("0.61.synthetic.169");
                string englishPath = Path.Combine(root, "dl_en_US.bin");
                string basePath = Path.Combine(root, "dl_uk_UA.bin");
                string stampPath = Path.Combine(root, "dl_uk_UA.bin.ver");
                string catalogPath = Path.Combine(root, "adaptive.jsonl");
                File.WriteAllBytes(englishPath, englishRaw);
                File.WriteAllBytes(basePath, baseRaw);
                File.WriteAllBytes(stampPath, stampRaw);

                Loc1Document english = Loc1Codec.Parse(englishRaw);
                Loc1Document ukrainian = Loc1Codec.Parse(baseRaw);
                TranslationRecord Record(int index, string translation, string? hintOverride = null) => new TranslationRecord
                {
                    Id = english.Entries[index].Id,
                    SourceSha256 = Hashing.Sha256Text(english.Entries[index].Value!),
                    HintSha256 = Hashing.Sha256Text(hintOverride ?? ukrainian.Entries[index].Value!),
                    Translation = translation,
                    Status = "draft",
                    Model = "compatible-smoke",
                    PromptVersion = "compatible-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = TranslationValidator.ClassifyRisks(english.Entries[index].Value!).ToArray(),
                    ReviewStage = "synthetic",
                    UpdatedAt = DateTimeOffset.Parse("2026-08-21T00:00:00Z", CultureInfo.InvariantCulture)
                };
                var records = new[]
                {
                    Record(0, "Открыть"),
                    Record(1, "Выйти", hintOverride: "изменённая подсказка"),
                    Record(2, "Получить 20%"),
                    Record(3, "Значение"),
                    Record(4, "<b>Открыть"),
                    Record(5, "Строка1 Строка2"),
                    new TranslationRecord
                    {
                        Id = "FFFFFFFFFFFFFFFE",
                        SourceSha256 = Hashing.Sha256Text("Removed source"),
                        HintSha256 = Hashing.Sha256Text("Видалене джерело"),
                        Translation = "Удалённая строка",
                        Status = "draft",
                        Model = "compatible-smoke",
                        PromptVersion = "compatible-v1",
                        Confidence = "high",
                        RiskFlags = Array.Empty<string>(),
                        ReviewStage = "synthetic",
                        UpdatedAt = DateTimeOffset.Parse("2026-08-21T00:00:00Z", CultureInfo.InvariantCulture)
                    }
                };
                TranslationCatalog.WriteJsonLines(catalogPath, records);
                byte[] catalogBytes = File.ReadAllBytes(catalogPath);

                string oversizedCatalogPath = Path.Combine(root, "oversized-catalog.jsonl");
                using (FileStream oversized = File.Create(oversizedCatalogPath))
                    oversized.SetLength(SignedUpdateLimits.MaxUncompressedCatalogBytes + 1);
                ExpectCompatibleRejected(() => BoundedArtifactReader.ReadCatalog(
                    oversizedCatalogPath,
                    Hashing.Sha256Bytes(catalogBytes),
                    "synthetic oversized catalog"));

                string swappedCatalogPath = Path.Combine(root, "same-length-swapped.jsonl");
                byte[] expectedCatalog = Encoding.UTF8.GetBytes("same-length-A");
                File.WriteAllBytes(swappedCatalogPath, Encoding.UTF8.GetBytes("same-length-B"));
                ExpectCompatibleRejected(() => BoundedArtifactReader.ReadCatalog(
                    swappedCatalogPath,
                    Hashing.Sha256Bytes(expectedCatalog),
                    "synthetic same-length catalog swap"));

                string oversizedLoc1Path = Path.Combine(root, "oversized-runtime.loc1");
                using (FileStream oversized = File.Create(oversizedLoc1Path))
                    oversized.SetLength(BoundedArtifactReader.MaximumRuntimeLoc1Bytes + 1);
                ExpectCompatibleRejected(() => BoundedArtifactReader.ReadRuntimeLoc1(
                    oversizedLoc1Path,
                    "synthetic oversized runtime LOC1"));
                ExpectCompatibleRejected(() => RuntimeCacheService.DescribeTuple(
                    oversizedLoc1Path,
                    basePath,
                    stampPath,
                    "oversized-observed"));
                ExpectCompatibleRejected(() => BoundedArtifactReader.Sha256File(
                    oversizedLoc1Path,
                    BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                    "synthetic oversized runtime hash"));

                string oversizedBackupPath = Path.Combine(root, "oversized-backup.loc1");
                ExpectCompatibleRejected(() => PatchService.EnsureVerifiedBoundedBackup(
                    oversizedLoc1Path,
                    oversizedBackupPath,
                    Hashing.Sha256Bytes(baseRaw),
                    BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                    "synthetic oversized backup source"));
                Require(!File.Exists(oversizedBackupPath),
                    "Oversized bounded backup source created a destination file.");

                string existingOversizedBackup = Path.Combine(root, "existing-oversized-backup.loc1");
                using (FileStream oversized = File.Create(existingOversizedBackup))
                    oversized.SetLength(BoundedArtifactReader.MaximumRuntimeLoc1Bytes + 1);
                ExpectCompatibleRejected(() => PatchService.EnsureVerifiedBoundedBackup(
                    basePath,
                    existingOversizedBackup,
                    Hashing.Sha256Bytes(baseRaw),
                    BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                    "synthetic existing oversized backup"));

                string oversizedStampPath = Path.Combine(root, "oversized-runtime.ver");
                using (FileStream oversized = File.Create(oversizedStampPath))
                    oversized.SetLength(BoundedArtifactReader.MaximumRuntimeStampBytes + 1);
                ExpectCompatibleRejected(() => RuntimeCacheService.DescribeTuple(
                    englishPath,
                    basePath,
                    oversizedStampPath,
                    "oversized-stamp-observed"));

                var excessiveCatalog = new StringBuilder(capacity: 18_000_000);
                string sourcePin = new string('A', 64);
                for (int index = 0; index <= SignedUpdateLimits.MaxCatalogRecords; index++)
                {
                    excessiveCatalog.Append("{\"id\":\"")
                        .Append(index.ToString("X16", CultureInfo.InvariantCulture))
                        .Append("\",\"source_sha256\":\"")
                        .Append(sourcePin)
                        .Append("\",\"translation\":\"x\",\"status\":\"draft\"}\n");
                }
                byte[] excessiveCatalogBytes = Encoding.UTF8.GetBytes(excessiveCatalog.ToString());
                ExpectCompatibleRejected(() => TranslationCatalog.LoadJsonLinesBytes(
                    excessiveCatalogBytes,
                    SignedUpdateLimits.MaxCatalogRecords));

                var family = new RuntimeCacheCompatibility
                {
                    Id = "trusted-synthetic-family",
                    GameVersion = "0.60.synthetic",
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(CreateLoc1(1, 68, 0x11111111, new[] { "old" })),
                    BaseSha256 = Hashing.Sha256Bytes(CreateLoc1(8, 68, 0x22222222, new[] { "старе" })),
                    StampSha256 = Hashing.Sha256Bytes(Encoding.UTF8.GetBytes("0.60.synthetic")),
                    StampValue = "0.60.synthetic",
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x11111111,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0x22222222,
                    BaseReleaseRevision = 68,
                    EntryCount = 1,
                    MinimumAppliedTranslations = 1,
                    Readiness = "blocked",
                    Certified = false,
                    TranslationPolicy = "community-preview-all-drafts"
                };
                family.Validate();

                CompatibleRevisionProfileBuild built = CompatibleRevisionProfileBuilder.Build(
                    englishPath,
                    basePath,
                    stampPath,
                    family,
                    catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes),
                    "community-preview-all-drafts");
                Require(built.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                    && built.Profile.EntryCount == englishValues.Length
                    && built.Composition.AppliedTranslations == 1
                    && built.Composition.StaleHintRecords == 1
                    && built.Composition.RejectedCatalogRecords == 4
                    && built.Composition.EnglishFallbacks == 6
                    && built.Composition.BaseFallbacks == 0
                    && built.Validation.ErrorCount > 0,
                    "Compatible-revision projection did not deterministically fallback unknown/stale/invalid rows.");

                byte[] projected = Loc1Codec.BuildRaw(Loc1Codec.Parse(baseRaw));
                TranslationCatalog projectionCatalog = TranslationCatalog.LoadJsonLines(catalogPath);
                Loc1Document projectionEnglish = Loc1Codec.Parse(englishRaw);
                Loc1Document projectionBase = Loc1Codec.Parse(baseRaw);
                CompositionSummary projectedSummary = TranslationComposer.Apply(
                    projectionEnglish,
                    projectionBase,
                    projectionCatalog,
                    includeDraft: true,
                    allowPerLocaleContentVersion: true,
                    requireExactHint: true);
                projected = Loc1Codec.BuildRaw(projectionBase);
                Require(Hashing.FixedEqualsHex(Hashing.Sha256Bytes(projected), built.Profile.ExpectedOutputSha256!)
                    && projectedSummary.AppliedTranslations == built.Profile.ExpectedAppliedTranslations
                    && projectionBase.Entries[0].Value == "Открыть"
                    && projectionBase.Entries[1].Value == englishValues[1]
                    && projectionBase.Entries[2].Value == englishValues[2]
                    && projectionBase.Entries[3].Value == englishValues[3]
                    && projectionBase.Entries[4].Value == englishValues[4]
                    && projectionBase.Entries[5].Value == englishValues[5]
                    && projectionBase.Entries[6].Value == englishValues[6],
                    "Compatible-revision builder and runtime projection diverged or applied an unsafe pair.");

                byte[] exactBounded = Loc1Codec.BuildRawBounded(Loc1Codec.Parse(baseRaw), baseRaw.LongLength);
                Require(exactBounded.SequenceEqual(baseRaw),
                    "Bounded LOC1 writer rejected or changed an output exactly at its byte budget.");
                Loc1Document overBudget = Loc1Codec.Parse(baseRaw);
                overBudget.Entries[0].Value = new string('x', checked((int)baseRaw.LongLength));
                ExpectCompatibleRejected(() => Loc1Codec.BuildRawBounded(overBudget, baseRaw.LongLength));

                Loc1Document nullEnglish = Loc1Codec.Parse(englishRaw);
                Loc1Document nullBase = Loc1Codec.Parse(baseRaw);
                nullBase.Entries[0].Value = null;
                nullEnglish.Entries[1].Value = null;
                CompositionSummary nullSummary = TranslationComposer.Apply(
                    nullEnglish,
                    nullBase,
                    projectionCatalog,
                    includeDraft: true,
                    allowPerLocaleContentVersion: true,
                    requireExactHint: true);
                Require(nullBase.Entries[0].Value == null
                    && nullBase.Entries[1].Value == baseValues[1]
                    && nullSummary.BaseFallbacks >= 2,
                    "Compatible fallback filled an official null sentinel or replaced a base row whose English source is null.");

                TranslationRecord releaseRejected = Record(0, "Открыть");
                releaseRejected.Status = "approved";
                TranslationRecord releaseReady = Record(1, "Выйти");
                releaseReady.Status = "approved";
                releaseReady.ReviewerIds = new[] { "reviewer-1" };
                releaseReady.ReviewedAt = DateTimeOffset.Parse("2026-08-21T01:00:00Z", CultureInfo.InvariantCulture);
                releaseReady.ReviewRevision = "release-smoke-v1";
                TranslationRecord unresolvedRelease = Record(2, "Получить 10%");
                unresolvedRelease.Status = "approved";
                unresolvedRelease.ReviewerIds = new[] { "reviewer-1" };
                unresolvedRelease.ReviewedAt = DateTimeOffset.Parse("2026-08-21T01:00:00Z", CultureInfo.InvariantCulture);
                unresolvedRelease.ReviewRevision = "release-smoke-v1";
                unresolvedRelease.IssueCodes = new[] { "ambiguous_context" };
                Require(!TranslationValidator.IsReleaseReady(
                        unresolvedRelease,
                        english.Entries[2].Value!,
                        out string unresolvedReason)
                    && unresolvedReason == "unresolved-issue-codes",
                    "Release eligibility ignored an unresolved issue code on otherwise approved text.");
                string releaseCatalogPath = Path.Combine(root, "release.jsonl");
                TranslationCatalog.WriteJsonLines(releaseCatalogPath, new[]
                    { releaseRejected, releaseReady, unresolvedRelease });
                byte[] releaseCatalogBytes = File.ReadAllBytes(releaseCatalogPath);
                CompatibleRevisionProfileBuild releaseBuilt = CompatibleRevisionProfileBuilder.Build(
                    englishPath,
                    basePath,
                    stampPath,
                    family,
                    releaseCatalogBytes,
                    Hashing.Sha256Bytes(releaseCatalogBytes),
                    "release-approved");
                Require(releaseBuilt.Composition.AppliedTranslations == 1
                    && releaseBuilt.Composition.PolicyFallbacks == 2
                    && releaseBuilt.Composition.EnglishFallbacks == 6
                    && releaseBuilt.Validation.Issues.Any(issue =>
                        issue.Id == unresolvedRelease.Id
                        && issue.Code == "unresolved-issue-codes"
                        && issue.Severity == ValidationSeverity.Error),
                    "Release-approved compatible projection applied a row with missing review metadata or unresolved issues.");

                byte[] compressedEnglish = Loc1Codec.Compress(englishRaw);
                File.WriteAllBytes(englishPath, compressedEnglish);
                ExpectCompatibleRejected(() => CompatibleRevisionProfileBuilder.Build(
                    englishPath, basePath, stampPath, family, catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes), "community-preview-all-drafts"));
                File.WriteAllBytes(englishPath, englishRaw);

                byte[] wrongLocale = CreateLoc1(9, 169, 0xD7A0FEFB, baseValues, "Prod_synthetic_169");
                File.WriteAllBytes(basePath, wrongLocale);
                ExpectCompatibleRejected(() => CompatibleRevisionProfileBuilder.Build(
                    englishPath, basePath, stampPath, family, catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes), "community-preview-all-drafts"));
                File.WriteAllBytes(basePath, baseRaw);

                byte[] changedKeys = CreateLoc1(8, 169, 0xD7A0FEFB, baseValues.Concat(new[] { "Новий ключ" }).ToArray(), "Prod_synthetic_169");
                File.WriteAllBytes(basePath, changedKeys);
                ExpectCompatibleRejected(() => CompatibleRevisionProfileBuilder.Build(
                    englishPath, basePath, stampPath, family, catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes), "community-preview-all-drafts"));

                File.WriteAllBytes(basePath, baseRaw);
                byte[] excessiveHeader = (byte[])englishRaw.Clone();
                WriteUInt32(excessiveHeader, 0x1C, checked((uint)(Loc1Codec.MaximumEntryCount + 1)));
                File.WriteAllBytes(englishPath, excessiveHeader);
                ExpectCompatibleRejected(() => CompatibleRevisionProfileBuilder.Build(
                    englishPath, basePath, stampPath, family, catalogBytes,
                    Hashing.Sha256Bytes(catalogBytes), "community-preview-all-drafts"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void ExpectCompatibleRejected(Action action)
        {
            try
            {
                action();
                throw new InvalidOperationException("Unsafe compatible-revision tuple was accepted.");
            }
            catch (Exception exception) when (exception is InvalidDataException
                or InvalidOperationException
                or Loc1FormatException)
            {
                if (exception is InvalidOperationException invalid
                    && invalid.Message == "Unsafe compatible-revision tuple was accepted.")
                    throw;
            }
        }

        private static void CompatibleRevisionTransactionAndUpdaterCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-compatible-transaction-{Guid.NewGuid():N}");
            string cacheRoot = Path.Combine(root, "cache");
            string stateRoot = Path.Combine(root, "state");
            string statePath = Path.Combine(stateRoot, "state.v1.json");
            string catalogPath = Path.Combine(root, "ru_RU.jsonl");
            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(stateRoot);
            try
            {
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
                byte[] englishA = CreateLoc1(1, 169, 0x11112222, new[] { "Open", "Exit" }, "Prod_synthetic_169");
                byte[] baseA = CreateLoc1(8, 169, 0x33334444, new[] { "Відкрити", "Вийти" }, "Prod_synthetic_169");
                byte[] stampA = Encoding.UTF8.GetBytes("0.61.synthetic.169");
                byte[] englishB = CreateLoc1(1, 270, 0x55556666, new[] { "Open", "Exit", "New" }, "Prod_synthetic_270");
                byte[] baseB = CreateLoc1(8, 270, 0x77778888, new[] { "Відкрити", "Вийти", "Нове" }, "Prod_synthetic_270");
                byte[] stampB = Encoding.UTF8.GetBytes("0.70.synthetic.270");
                File.WriteAllBytes(englishPath, englishA);
                File.WriteAllBytes(targetPath, baseA);
                File.WriteAllBytes(stampPath, stampA);

                Loc1Document parsedEnglishA = Loc1Codec.Parse(englishA);
                Loc1Document parsedBaseA = Loc1Codec.Parse(baseA);
                TranslationRecord Record(int index, string translation) => new TranslationRecord
                {
                    Id = parsedEnglishA.Entries[index].Id,
                    SourceSha256 = Hashing.Sha256Text(parsedEnglishA.Entries[index].Value!),
                    HintSha256 = Hashing.Sha256Text(parsedBaseA.Entries[index].Value!),
                    Translation = translation,
                    Status = "draft",
                    Model = "compatible-transaction-smoke",
                    PromptVersion = "compatible-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = Array.Empty<string>(),
                    ReviewStage = "synthetic",
                    UpdatedAt = DateTimeOffset.Parse("2026-08-21T00:00:00Z", CultureInfo.InvariantCulture)
                };
                TranslationCatalog.WriteJsonLines(catalogPath, new[]
                {
                    Record(0, "Открыть"),
                    Record(1, "Выйти")
                });
                byte[] catalogBytes = File.ReadAllBytes(catalogPath);
                string catalogSha256 = Hashing.Sha256Bytes(catalogBytes);
                var family = new RuntimeCacheCompatibility
                {
                    Id = "trusted-compatible-transaction-family",
                    GameVersion = "0.60.synthetic",
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(CreateLoc1(1, 68, 0x11111111, new[] { "old" })),
                    BaseSha256 = Hashing.Sha256Bytes(CreateLoc1(8, 68, 0x22222222, new[] { "старе" })),
                    StampSha256 = Hashing.Sha256Bytes(Encoding.UTF8.GetBytes("0.60.synthetic")),
                    StampValue = "0.60.synthetic",
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x11111111,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0x22222222,
                    BaseReleaseRevision = 68,
                    EntryCount = 1,
                    MinimumAppliedTranslations = 1,
                    Readiness = "blocked",
                    Certified = false,
                    TranslationPolicy = "community-preview-all-drafts"
                };
                family.Validate();
                CompatibleRevisionProfileBuild builtA = CompatibleRevisionProfileBuilder.Build(
                    englishPath,
                    targetPath,
                    stampPath,
                    family,
                    catalogBytes,
                    catalogSha256,
                    "community-preview-all-drafts");
                Require(builtA.Composition.AppliedTranslations == 2
                    && builtA.Composition.EnglishFallbacks == 0,
                    "Compatible revision A did not materialize its exact source+hint rows.");

                string backupPath = Path.Combine(
                    stateRoot,
                    "backups",
                    $"{builtA.Profile.Id}-{Hashing.Sha256Text(builtA.Profile.Id).Substring(0, 12)}",
                    $"{builtA.Profile.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, baseA);
                (string englishSnapshot, string stampSnapshot) =
                    RuntimeCacheService.ResolveCompatibleSourceSnapshotPaths(backupPath);

                PatchJournal Prepared(string transactionId) => new PatchJournal
                {
                    TransactionId = transactionId,
                    Operation = "runtime-cache-apply",
                    Phase = "Prepared",
                    BuildId = builtA.Profile.Id,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    QuarantinePath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.displaced"),
                    RollbackPath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.rollback"),
                    SourceSha256 = builtA.Profile.BaseSha256,
                    ExpectedOutputSha256 = builtA.Profile.ExpectedOutputSha256!,
                    TranslationsSha256 = builtA.Profile.TranslationCatalogSha256!,
                    AppliedTranslations = builtA.Profile.ExpectedAppliedTranslations,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                MutationPolicy.BindTestRuntimePaths(cacheRoot, statePath);
                PatchJournal foreign = Prepared("40404040404040404040404040404040");
                foreign.BuildId = "foreign-compatible-profile";
                string foreignBackup = Path.Combine(
                    stateRoot,
                    "backups",
                    $"{foreign.BuildId}-{Hashing.Sha256Text(foreign.BuildId).Substring(0, 12)}",
                    $"{builtA.Profile.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(foreignBackup)!);
                File.WriteAllBytes(foreignBackup, baseA);
                foreign.BackupPath = foreignBackup;
                PatchJournalStore.Save(statePath, foreign);
                RuntimeUpdateResolution unauthenticatedJournal = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, builtA.Profile, catalogPath, coordinator: null);
                Require(unauthenticatedJournal.Inspection.Status == InstallationStatus.InconsistentState
                    && unauthenticatedJournal.InstalledInspection == null
                    && unauthenticatedJournal.LocalProblem == "journal-authentication"
                    && !RuntimeUpdateAuthorization.CanRestoreOrRecover(unauthenticatedJournal),
                    "A parseable foreign journal was exposed as an authenticated recovery operation.");
                PatchJournalStore.Delete(statePath, foreign.TransactionId);
                File.WriteAllBytes(englishSnapshot, englishA);
                PatchJournalStore.Save(statePath, Prepared("41414141414141414141414141414141"));
                RuntimeUpdateResolution partialEnglish = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(partialEnglish.Inspection.Status == InstallationStatus.RecoveryRequired
                    && RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, partialEnglish.Profile, out _),
                    "Prepared compatible apply with only the EN snapshot could not authenticate from the exact live tuple.");
                RuntimeCacheService.Recover(statePath, partialEnglish.Profile);
                Require(PatchJournalStore.FindActive(statePath) == null && !File.Exists(stampSnapshot),
                    "Prepared compatible apply recovery trusted or created its missing stamp snapshot.");

                File.Delete(englishSnapshot);
                File.WriteAllBytes(stampSnapshot, stampA);
                PatchJournalStore.Save(statePath, Prepared("42424242424242424242424242424242"));
                RuntimeUpdateResolution partialStamp = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(partialStamp.Inspection.Status == InstallationStatus.RecoveryRequired
                    && RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, partialStamp.Profile, out _),
                    "Prepared compatible apply with only the stamp snapshot could not authenticate from the exact live tuple.");
                RuntimeCacheService.Recover(statePath, partialStamp.Profile);

                File.Delete(stampSnapshot);
                PatchJournalStore.Save(statePath, Prepared("43434343434343434343434343434343"));
                RuntimeUpdateResolution baseOnly = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, builtA.Profile, catalogPath, coordinator: null);
                Require(baseOnly.Inspection.Status == InstallationStatus.RecoveryRequired
                    && RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, baseOnly.Profile, out _),
                    "Prepared compatible apply with only the immutable base could not authenticate from the exact live tuple.");
                RuntimeCacheService.Recover(statePath, baseOnly.Profile);

                File.WriteAllBytes(englishSnapshot, englishA);
                File.WriteAllBytes(stampSnapshot, stampA);
                PatchJournalStore.Save(statePath, Prepared("44444444444444444444444444444444"));
                RuntimeUpdateResolution completeSnapshotsPrepared = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, builtA.Profile, catalogPath, coordinator: null);
                Require(completeSnapshotsPrepared.Inspection.Status == InstallationStatus.RecoveryRequired
                    && RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, completeSnapshotsPrepared.Profile, out _),
                    "Complete compatible source snapshots before BackupVerified could not authenticate the still-Prepared transaction.");
                RuntimeCacheService.Recover(statePath, completeSnapshotsPrepared.Profile);

                foreach ((string Phase, bool Quarantine, bool DisplacedVerified, string Id) scenario in new[]
                {
                    ("PreCommitVerified", false, false, "45454545454545454545454545454545"),
                    ("ReplacementCommitted", true, false, "46464646464646464646464646464646"),
                    ("DisplacedVerified", true, true, "47474747474747474747474747474747")
                })
                {
                    PatchJournal missingTargetJournal = Prepared(scenario.Id);
                    missingTargetJournal.Phase = scenario.Phase;
                    if (scenario.Quarantine)
                        File.WriteAllBytes(missingTargetJournal.QuarantinePath, baseA);
                    if (scenario.DisplacedVerified)
                        missingTargetJournal.DisplacedSha256 = builtA.Profile.BaseSha256;
                    PatchJournalStore.Save(statePath, missingTargetJournal);
                    File.Delete(targetPath);
                    RuntimeUpdateResolution missingTarget = RuntimeUpdateResolver.Resolve(
                        cacheRoot, statePath, builtA.Profile, catalogPath, coordinator: null);
                    Require(missingTarget.Inspection.Status == InstallationStatus.InconsistentState
                        && missingTarget.InstalledInspection == null
                        && !RuntimeUpdateAuthorization.CanRestoreOrRecover(missingTarget),
                        $"A {scenario.Phase} journal with no exact live target was mislabeled as signed-data failure or recoverable.");
                    PatchJournalStore.Delete(statePath, scenario.Id);
                    if (File.Exists(missingTargetJournal.QuarantinePath))
                        File.Delete(missingTargetJournal.QuarantinePath);
                    File.WriteAllBytes(targetPath, baseA);
                }

                RuntimeCacheInspection originalA = RuntimeCacheService.Inspect(cacheRoot, builtA.Profile, statePath);
                Require(originalA.Status == InstallationStatus.CompatibleOriginal,
                    "Compatible revision A was not ready after idempotent partial-snapshot recovery.");
                PatchApplyResult appliedA = RuntimeCacheService.Apply(originalA, catalogPath, statePath);
                Require(File.Exists(englishSnapshot) && File.Exists(stampSnapshot)
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(englishSnapshot), builtA.Profile.EnglishSha256)
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(stampSnapshot), builtA.Profile.StampSha256)
                    && RuntimeCacheService.Inspect(cacheRoot, builtA.Profile, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Compatible apply did not persist and authenticate both exact immutable source snapshots.");

                RuntimeUpdateResolution restartedA = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(restartedA.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                    && restartedA.Inspection.Status == InstallationStatus.PatchedByThisTool
                    && Hashing.FixedEqualsHex(restartedA.Profile.ExpectedOutputSha256!, appliedA.State.PatchedSha256),
                    "Compatible installed state was not reconstructed exactly after restart.");

                // Same bytes may legitimately have different composition counts (for example an applied
                // translation equal to English versus an English fallback).  Rebind only state metadata,
                // then prove restore no longer depends on the predecessor catalog.
                RuntimeCacheService.Restore(statePath, builtA.Profile);
                string catalogX = Path.Combine(root, "equivalent-x.jsonl");
                string catalogY = Path.Combine(root, "equivalent-y.jsonl");
                TranslationCatalog.WriteJsonLines(catalogX, new[] { Record(0, "Открыть"), Record(1, "Exit") });
                TranslationCatalog.WriteJsonLines(catalogY, new[] { Record(0, "Открыть") });
                byte[] catalogXBytes = File.ReadAllBytes(catalogX);
                byte[] catalogYBytes = File.ReadAllBytes(catalogY);
                CompatibleRevisionProfileBuild profileX = CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, stampPath, family, catalogXBytes,
                    Hashing.Sha256Bytes(catalogXBytes), "community-preview-all-drafts");
                CompatibleRevisionProfileBuild profileY = CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, stampPath, family, catalogYBytes,
                    Hashing.Sha256Bytes(catalogYBytes), "community-preview-all-drafts");
                Require(profileX.Profile.ExpectedAppliedTranslations == 2
                    && profileY.Profile.ExpectedAppliedTranslations == 1
                    && Hashing.FixedEqualsHex(profileX.Profile.ExpectedOutputSha256!, profileY.Profile.ExpectedOutputSha256!),
                    "Equivalent-output smoke catalogs did not create the intended differing-count profiles.");
                RuntimeCacheInspection xOriginal = RuntimeCacheService.Inspect(cacheRoot, profileX.Profile, statePath);
                RuntimeCacheService.Apply(xOriginal, catalogX, statePath);
                RuntimeCacheInspection xInstalled = RuntimeCacheService.Inspect(cacheRoot, profileX.Profile, statePath);
                byte[] xStateBeforeRejectedRebind = File.ReadAllBytes(statePath);
                File.WriteAllBytes(stampPath, Encoding.UTF8.GetBytes("0.61.synthetic.changed-during-rebind"));
                ExpectCompatibleRejected(() => RuntimeCacheService.RebindEquivalentCatalogState(
                    xInstalled, profileY.Profile, catalogY, statePath));
                Require(File.ReadAllBytes(statePath).SequenceEqual(xStateBeforeRejectedRebind),
                    "Static tuple change during equivalent metadata rebind changed patch state.");
                File.WriteAllBytes(stampPath, stampA);
                MutationTestHooks.BeforeEquivalentCatalogCommit = path =>
                    File.WriteAllBytes(path, Encoding.UTF8.GetBytes("catalog changed under lock"));
                try
                {
                    ExpectCompatibleRejected(() => RuntimeCacheService.RebindEquivalentCatalogState(
                        xInstalled, profileY.Profile, catalogY, statePath));
                }
                finally
                {
                    MutationTestHooks.BeforeEquivalentCatalogCommit = null;
                    File.WriteAllBytes(catalogY, catalogYBytes);
                }
                Require(File.ReadAllBytes(statePath).SequenceEqual(xStateBeforeRejectedRebind),
                    "Catalog TOCTOU during equivalent metadata rebind changed patch state.");
                PatchApplyResult rebound = RuntimeCacheService.RebindEquivalentCatalogState(
                    xInstalled, profileY.Profile, catalogY, statePath);
                File.Delete(catalogX);
                Require(rebound.State.AppliedTranslations == 1
                    && RuntimeCacheService.Inspect(cacheRoot, profileY.Profile, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Equivalent-output catalog metadata was not rebound durably to the selected catalog/counts.");
                RuntimeCacheService.Restore(statePath, profileY.Profile);
                RuntimeCacheInspection originalAAgain = RuntimeCacheService.Inspect(cacheRoot, builtA.Profile, statePath);
                appliedA = RuntimeCacheService.Apply(originalAAgain, catalogPath, statePath);

                byte[] patchedA = File.ReadAllBytes(targetPath);
                File.WriteAllBytes(targetPath, baseA);
                RuntimeUpdateResolution repairedA = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(repairedA.Inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && string.Equals(repairedA.Profile.Id, builtA.Profile.Id, StringComparison.Ordinal),
                    "Launcher repair of only the official Ukrainian cache was not safely recognized for same-revision reapply.");
                File.WriteAllBytes(targetPath, patchedA);

                // A content-only EN/UK update can keep the client version stamp unchanged.
                // The old state must be authenticated using immutable snapshots, not live EN.
                File.WriteAllBytes(englishPath, englishB);
                File.WriteAllBytes(targetPath, baseB);
                RuntimeUpdateResolution contentOnlyUpdate = RuntimeUpdateResolver.Resolve(
                    cacheRoot, statePath, builtA.Profile, catalogPath, coordinator: null);
                Require(contentOnlyUpdate.Inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && contentOnlyUpdate.Profile.GameVersion == builtA.Profile.GameVersion
                    && contentOnlyUpdate.Profile.EnglishReleaseRevision == 270
                    && contentOnlyUpdate.Profile.ExpectedAppliedTranslations == 2
                    && contentOnlyUpdate.Profile.ExpectedEnglishFallbacks == 1,
                    "A content-only EN/UK update with an unchanged client stamp stranded the old authenticated state.");
                File.WriteAllBytes(targetPath, patchedA);

                File.WriteAllBytes(englishPath, englishB);
                File.WriteAllBytes(stampPath, stampB);
                RuntimeUpdateResolution staleTarget = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(staleTarget.Inspection.Status != InstallationStatus.PatchSupersededByOfficialUpdate
                    && staleTarget.Inspection.Status != InstallationStatus.CompatibleOriginal
                    && Hashing.Sha256File(targetPath) == Hashing.Sha256Bytes(patchedA),
                    "Changed EN/stamp with the old patched target was not kept fail-closed.");

                File.WriteAllBytes(targetPath, baseB);
                byte[] exactState = File.ReadAllBytes(statePath);
                PatchState forged = PatchPlanner.TryLoadState(statePath)
                    ?? throw new InvalidOperationException("Compatible A state disappeared before updater reconciliation.");
                forged.TranslationsSha256 = new string('F', 64);
                File.WriteAllText(statePath, JsonSerializer.Serialize(forged), new UTF8Encoding(false));
                RuntimeUpdateResolution forgedResolution = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(forgedResolution.Inspection.Status != InstallationStatus.PatchSupersededByOfficialUpdate
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), Hashing.Sha256Bytes(baseB)),
                    "Forged old compatible state authorized official-update reconciliation or changed the target.");
                File.WriteAllBytes(statePath, exactState);

                byte[] exactEnglishSnapshot = File.ReadAllBytes(englishSnapshot);
                File.WriteAllBytes(englishSnapshot, Encoding.UTF8.GetBytes("tampered snapshot"));
                RuntimeUpdateResolution tamperedSnapshot = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(tamperedSnapshot.Inspection.Status != InstallationStatus.PatchSupersededByOfficialUpdate
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), Hashing.Sha256Bytes(baseB)),
                    "Tampered compatible source snapshot authorized official-update reconciliation.");
                File.WriteAllBytes(englishSnapshot, exactEnglishSnapshot);

                RuntimeUpdateResolution updatedB = RuntimeUpdateResolver.Resolve(
                    cacheRoot,
                    statePath,
                    builtA.Profile,
                    catalogPath,
                    coordinator: null);
                Require(updatedB.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                    && updatedB.Inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && updatedB.Profile.EntryCount == 3
                    && updatedB.Profile.ExpectedAppliedTranslations == 2
                    && updatedB.Profile.ExpectedEnglishFallbacks == 1,
                    "Official future revision did not reconcile only the exact old state/snapshots and current source+hint coverage.");

                foreach (string tamper in new[] { "patched", "catalog", "applied" })
                {
                    MutationTestHooks.BeforeSupersededStateArchive = path =>
                    {
                        PatchState changed = PatchPlanner.TryLoadState(path)
                            ?? throw new InvalidOperationException("Synthetic predecessor state disappeared before archive tamper.");
                        if (tamper == "patched") changed.PatchedSha256 = new string('A', 64);
                        else if (tamper == "catalog") changed.TranslationsSha256 = new string('B', 64);
                        else changed.AppliedTranslations--;
                        File.WriteAllText(path, JsonSerializer.Serialize(changed), new UTF8Encoding(false));
                    };
                    try
                    {
                        ExpectCompatibleRejected(() => RuntimeCacheService.Apply(
                            updatedB.Inspection,
                            updatedB.CatalogPath,
                            statePath));
                    }
                    finally
                    {
                        MutationTestHooks.BeforeSupersededStateArchive = null;
                        File.WriteAllBytes(statePath, exactState);
                    }

                    string supersededHistory = Path.Combine(stateRoot, "history", "superseded");
                    Require(Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), Hashing.Sha256Bytes(baseB))
                        && PatchJournalStore.FindActive(statePath) == null
                        && (!Directory.Exists(supersededHistory)
                            || Directory.GetFiles(supersededHistory, "*.json").Length == 0),
                        $"Under-lock {tamper} predecessor-state tamper changed target/history/journal before refusal.");
                }

                PatchApplyResult appliedB = RuntimeCacheService.Apply(updatedB.Inspection, updatedB.CatalogPath, statePath);
                Require(Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), appliedB.State.PatchedSha256)
                    && !string.Equals(appliedA.State.BuildId, appliedB.State.BuildId, StringComparison.Ordinal)
                    && Directory.GetFiles(Path.Combine(stateRoot, "history", "superseded"), "*.json").Length == 1,
                    "Compatible official-update apply did not preserve the old state and commit the new exact artifact.");
                RuntimeCacheService.Restore(statePath, updatedB.Profile);
                Require(!File.Exists(statePath)
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), Hashing.Sha256Bytes(baseB)),
                    "Compatible future revision did not restore its own immutable official backup.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void CatalogSupersededMigrationCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-catalog-upgrade-{Guid.NewGuid():N}");
            string cacheRoot = Path.Combine(root, "cache");
            string stateRoot = Path.Combine(root, "state");
            string statePath = Path.Combine(stateRoot, "state.v1.json");
            string oldCatalogPath = Path.Combine(root, "old.jsonl");
            string newCatalogPath = Path.Combine(root, "new.jsonl");
            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(stateRoot);
            try
            {
                byte[] englishRaw = CreateLoc1(1, 68, 0x1234ABCD, new[] { "Open", "Exit" });
                byte[] officialRaw = CreateLoc1(8, 68, 0xD7A0FEFB, new[] { "Відкрити", "Вийти" });
                byte[] stampRaw = Encoding.UTF8.GetBytes("0.60.synthetic");
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
                File.WriteAllBytes(englishPath, englishRaw);
                File.WriteAllBytes(targetPath, officialRaw);
                File.WriteAllBytes(stampPath, stampRaw);

                Loc1Document english = Loc1Codec.Parse(englishRaw);
                Loc1Document official = Loc1Codec.Parse(officialRaw);
                Loc1Entry source = english.Entries[0];
                TranslationRecord Record(string translation) => new TranslationRecord
                {
                    Id = source.Id,
                    SourceSha256 = Hashing.Sha256Text(source.Value!),
                    HintSha256 = Hashing.Sha256Text(official.Entries[0].Value!),
                    Translation = translation,
                    Status = "draft",
                    Model = "synthetic-smoke",
                    PromptVersion = "catalog-upgrade-v1",
                    Confidence = "high",
                    NeedsReview = false,
                    RiskFlags = Array.Empty<string>(),
                    ReviewStage = "synthetic",
                    UpdatedAt = DateTimeOffset.Parse("2026-08-21T00:00:00Z", CultureInfo.InvariantCulture)
                };
                TranslationCatalog.WriteJsonLines(oldCatalogPath, new[] { Record("Старое") });
                TranslationCatalog.WriteJsonLines(newCatalogPath, new[] { Record("Новое") });

                byte[] Compose(string catalogPath)
                {
                    Loc1Document target = Loc1Codec.Parse(officialRaw);
                    CompositionSummary composition = TranslationComposer.Apply(
                        english,
                        target,
                        TranslationCatalog.LoadJsonLines(catalogPath),
                        includeDraft: true,
                        approvedOnly: false,
                        allowPerLocaleContentVersion: true);
                    Require(composition.AppliedTranslations == 1
                        && composition.EnglishFallbacks == 1
                        && composition.BaseFallbacks == 0,
                        "Synthetic catalog-upgrade composition changed unexpectedly.");
                    return Loc1Codec.BuildRaw(target);
                }

                byte[] oldPatchedRaw = Compose(oldCatalogPath);
                byte[] newPatchedRaw = Compose(newCatalogPath);
                string oldOutputSha256 = Hashing.Sha256Bytes(oldPatchedRaw);
                string newOutputSha256 = Hashing.Sha256Bytes(newPatchedRaw);
                string oldCatalogSha256 = Hashing.Sha256File(oldCatalogPath);
                string newCatalogSha256 = Hashing.Sha256File(newCatalogPath);
                var profile = new RuntimeCacheCompatibility
                {
                    Id = "synthetic-runtime-cache-catalog-upgrade",
                    GameVersion = "0.60.synthetic",
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(englishRaw),
                    BaseSha256 = Hashing.Sha256Bytes(officialRaw),
                    StampSha256 = Hashing.Sha256Bytes(stampRaw),
                    StampValue = "0.60.synthetic",
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x1234ABCD,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0xD7A0FEFB,
                    BaseReleaseRevision = 68,
                    EntryCount = 2,
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = newCatalogSha256,
                    ExpectedOutputSha256 = newOutputSha256,
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1,
                    ExpectedEnglishFallbacks = 1,
                    ExpectedBaseFallbacks = 0,
                    ExpectedNeedsReviewFallbacks = 0,
                    TranslationPolicy = "community-preview-all-drafts",
                    SupersededArtifacts = new[]
                    {
                        new RuntimeCacheSupersededArtifact
                        {
                            OutputSha256 = oldOutputSha256,
                            TranslationCatalogSha256 = oldCatalogSha256,
                            AppliedTranslations = 1,
                            EnglishFallbacks = 1,
                            BaseFallbacks = 0,
                            NeedsReviewFallbacks = 0
                        }
                    }
                };
                profile.Validate();
                string backupPath = Path.Combine(
                    stateRoot,
                    "backups",
                    $"{profile.Id}-{Hashing.Sha256Text(profile.Id).Substring(0, 12)}",
                    $"{profile.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, officialRaw);

                PatchState OldState() => new PatchState
                {
                    BuildId = profile.Id,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    OriginalSha256 = profile.BaseSha256,
                    PatchedSha256 = oldOutputSha256,
                    TranslationsSha256 = oldCatalogSha256,
                    AppliedAt = DateTimeOffset.Parse("2026-08-20T00:00:00Z", CultureInfo.InvariantCulture),
                    AppliedTranslations = 1
                };
                void InstallOldState()
                {
                    File.WriteAllBytes(targetPath, oldPatchedRaw);
                    File.WriteAllText(statePath, JsonSerializer.Serialize(OldState()), new UTF8Encoding(false));
                }

                PatchJournal UpgradeJournal(string transactionId, string phase, bool displacedVerified = false)
                {
                    return new PatchJournal
                    {
                        TransactionId = transactionId,
                        Operation = "runtime-cache-upgrade",
                        Phase = phase,
                        BuildId = profile.Id,
                        GameRoot = Path.GetFullPath(cacheRoot),
                        TargetPath = Path.GetFullPath(targetPath),
                        BackupPath = Path.GetFullPath(backupPath),
                        QuarantinePath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.displaced"),
                        RollbackPath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.rollback"),
                        DisplacedSha256 = displacedVerified ? oldOutputSha256 : null,
                        SourceSha256 = oldOutputSha256,
                        ExpectedOutputSha256 = newOutputSha256,
                        TranslationsSha256 = newCatalogSha256,
                        AppliedTranslations = 1,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                }

                MutationPolicy.BindTestRuntimePaths(cacheRoot, statePath);

                InstallOldState();
                RuntimeCacheInspection upgrade = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                Require(upgrade.Status == InstallationStatus.PatchSupersededByCatalogUpdate,
                    "An exact allowlisted older catalog was not recognized as safely upgradeable.");
                Require(!RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, profile, out _),
                    "Catalog-upgrade recovery authenticated without an active journal.");

                byte[] exactState = File.ReadAllBytes(statePath);
                PatchState tampered = OldState();
                tampered.TranslationsSha256 = new string('E', 64);
                File.WriteAllText(statePath, JsonSerializer.Serialize(tampered), new UTF8Encoding(false));
                Require(RuntimeCacheService.Inspect(cacheRoot, profile, statePath).Status == InstallationStatus.InconsistentState,
                    "A same-id state with an unallowlisted catalog hash was accepted for upgrade.");
                File.WriteAllBytes(statePath, exactState);

                tampered = OldState();
                tampered.BackupPath = Path.Combine(stateRoot, "backups", "alternate", $"{profile.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(tampered.BackupPath)!);
                File.WriteAllBytes(tampered.BackupPath, officialRaw);
                File.WriteAllText(statePath, JsonSerializer.Serialize(tampered), new UTF8Encoding(false));
                Require(RuntimeCacheService.Inspect(cacheRoot, profile, statePath).Status == InstallationStatus.InconsistentState,
                    "An official-hash backup outside the exact profile-derived path was accepted for upgrade.");
                File.WriteAllBytes(statePath, exactState);

                File.WriteAllBytes(backupPath, Encoding.UTF8.GetBytes("tampered backup"));
                Require(RuntimeCacheService.Inspect(cacheRoot, profile, statePath).Status == InstallationStatus.InconsistentState,
                    "A corrupted immutable backup was accepted for catalog upgrade.");
                File.WriteAllBytes(backupPath, officialRaw);

                RuntimeCacheSupersededArtifact[] allowlist = profile.SupersededArtifacts;
                profile.SupersededArtifacts = Array.Empty<RuntimeCacheSupersededArtifact>();
                profile.Validate();
                Require(RuntimeCacheService.Inspect(cacheRoot, profile, statePath).Status == InstallationStatus.InconsistentState,
                    "An arbitrary same-id patch was accepted without an embedded exact predecessor descriptor.");
                profile.SupersededArtifacts = allowlist;
                profile.Validate();

                PatchJournal preparedUpgrade = UpgradeJournal(
                    "01010101010101010101010101010101",
                    "Prepared");
                PatchJournalStore.Save(statePath, preparedUpgrade);
                Require(RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, profile, out string preparedProblem),
                    $"An exact Prepared catalog-upgrade journal was not authenticated: {preparedProblem}");
                string preparedRecovery = RuntimeCacheService.Recover(statePath, profile);
                Require(preparedRecovery.Contains("journal cleared", StringComparison.Ordinal)
                    && PatchJournalStore.FindActive(statePath) == null
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), oldOutputSha256)
                    && Hashing.FixedEqualsHex(
                        PatchPlanner.TryLoadState(statePath)?.PatchedSha256 ?? string.Empty,
                        oldOutputSha256),
                    "Prepared catalog-upgrade recovery did not preserve the exact old patch/state.");

                InstallOldState();
                PatchJournal replacementUpgrade = UpgradeJournal(
                    "02020202020202020202020202020202",
                    "ReplacementCommitted");
                File.WriteAllBytes(replacementUpgrade.QuarantinePath, oldPatchedRaw);
                File.WriteAllBytes(targetPath, newPatchedRaw);
                PatchJournalStore.Save(statePath, replacementUpgrade);
                Require(RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, profile, out string replacementProblem),
                    $"An exact ReplacementCommitted catalog-upgrade journal was not authenticated: {replacementProblem}");
                RuntimeCacheService.Recover(statePath, profile);
                Require(PatchJournalStore.FindActive(statePath) == null
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), newOutputSha256)
                    && Hashing.FixedEqualsHex(
                        PatchPlanner.TryLoadState(statePath)?.PatchedSha256 ?? string.Empty,
                        newOutputSha256),
                    "ReplacementCommitted catalog-upgrade recovery did not commit the exact new patch/state.");

                InstallOldState();
                PatchJournal postCommitUpgrade = UpgradeJournal(
                    "03030303030303030303030303030303",
                    "PostCommitVerified",
                    displacedVerified: true);
                File.WriteAllBytes(postCommitUpgrade.QuarantinePath, oldPatchedRaw);
                File.WriteAllBytes(targetPath, newPatchedRaw);
                PatchJournalStore.Save(statePath, postCommitUpgrade);
                Require(RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, profile, out string postCommitProblem),
                    $"An exact PostCommitVerified catalog-upgrade journal with old state was not authenticated: {postCommitProblem}");
                byte[] postCommitOldState = File.ReadAllBytes(statePath);
                File.Delete(statePath);
                Require(!RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, profile, out _),
                    "PostCommitVerified catalog upgrade authenticated after losing both old and new state.");
                File.WriteAllBytes(statePath, postCommitOldState);
                RuntimeCacheService.Recover(statePath, profile);
                Require(PatchJournalStore.FindActive(statePath) == null
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), newOutputSha256)
                    && Hashing.FixedEqualsHex(
                        PatchPlanner.TryLoadState(statePath)?.PatchedSha256 ?? string.Empty,
                        newOutputSha256),
                    "PostCommitVerified catalog-upgrade recovery did not commit the exact new patch/state.");

                InstallOldState();
                upgrade = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                PatchApplyResult applied = RuntimeCacheService.Apply(upgrade, newCatalogPath, statePath);
                Require(Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), newOutputSha256)
                    && Hashing.FixedEqualsHex(applied.State.PatchedSha256, newOutputSha256)
                    && RuntimeCacheService.Inspect(cacheRoot, profile, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Direct catalog upgrade did not atomically install the exact new output/state.");
                RuntimeCacheService.Restore(statePath, profile);
                Require(!File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), profile.BaseSha256),
                    "The current upgraded patch did not restore its exact official backup.");

                InstallOldState();
                RuntimeCacheService.Restore(statePath, profile);
                Require(!File.Exists(statePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), profile.BaseSha256),
                    "Direct restore of the allowlisted superseded catalog did not restore the exact official backup.");

                InstallOldState();
                string upgradeTransaction = "11111111111111111111111111111111";
                string upgradeQuarantine = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{upgradeTransaction}.displaced");
                File.WriteAllBytes(upgradeQuarantine, oldPatchedRaw);
                File.WriteAllBytes(targetPath, newPatchedRaw);
                var interruptedUpgrade = new PatchJournal
                {
                    TransactionId = upgradeTransaction,
                    Operation = "runtime-cache-upgrade",
                    Phase = "ReplacementCommitted",
                    BuildId = profile.Id,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    QuarantinePath = upgradeQuarantine,
                    RollbackPath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{upgradeTransaction}.rollback"),
                    SourceSha256 = oldOutputSha256,
                    ExpectedOutputSha256 = newOutputSha256,
                    TranslationsSha256 = newCatalogSha256,
                    AppliedTranslations = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                PatchJournalStore.Save(statePath, interruptedUpgrade);
                RuntimeCacheService.Recover(statePath, profile);
                PatchState recoveredUpgrade = PatchPlanner.TryLoadState(statePath)
                    ?? throw new InvalidOperationException("Recovered catalog upgrade did not commit its new state.");
                Require(Hashing.FixedEqualsHex(recoveredUpgrade.PatchedSha256, newOutputSha256)
                    && !File.Exists(upgradeQuarantine)
                    && PatchJournalStore.FindActive(statePath) == null,
                    "Interrupted catalog-upgrade apply did not recover to the exact new artifact.");

                InstallOldState();
                string restoreTransaction = "22222222222222222222222222222222";
                string restoreQuarantine = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{restoreTransaction}.displaced");
                File.WriteAllBytes(restoreQuarantine, oldPatchedRaw);
                File.WriteAllBytes(targetPath, officialRaw);
                var interruptedRestore = new PatchJournal
                {
                    TransactionId = restoreTransaction,
                    Operation = "runtime-cache-upgrade-restore",
                    Phase = "ReplacementCommitted",
                    BuildId = profile.Id,
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    QuarantinePath = restoreQuarantine,
                    RollbackPath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{restoreTransaction}.rollback"),
                    SourceSha256 = oldOutputSha256,
                    ExpectedOutputSha256 = profile.BaseSha256,
                    TranslationsSha256 = oldCatalogSha256,
                    AppliedTranslations = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                PatchJournalStore.Save(statePath, interruptedRestore);
                RuntimeCacheService.Recover(statePath, profile);
                Require(!File.Exists(statePath)
                    && !File.Exists(restoreQuarantine)
                    && PatchJournalStore.FindActive(statePath) == null
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), profile.BaseSha256),
                    "Interrupted superseded-catalog restore did not recover to the exact official base.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void CliJsonContractCheck(string profilePath)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-cli-json-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            using var standardOut = new StringWriter(CultureInfo.InvariantCulture);
            using var standardError = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode;
            try
            {
                Console.SetOut(standardOut);
                Console.SetError(standardError);
                string cliPath = Path.Combine(AppContext.BaseDirectory, "InvokersRu.Cli.dll");
                Require(File.Exists(cliPath), "The CLI project reference was not copied to the smoke output.");
                Assembly cliAssembly = Assembly.LoadFrom(cliPath);
                Type program = cliAssembly.GetType("InvokersRu.Cli.Program", throwOnError: true)!;
                MethodInfo main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("InvokersRu.Cli.Program.Main was not found.");
                object? result = main.Invoke(null, new object[]
                {
                    new[] { "cache-plan", "--cache-root", root, "--profile", profilePath, "--json" }
                });
                exitCode = result is int value ? value : throw new InvalidOperationException("CLI Main returned no integer exit code.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new InvalidOperationException("CLI cache-plan JSON contract invocation failed.", exception.InnerException);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Require(exitCode == 5, $"CLI cache-plan over a missing synthetic cache returned {exitCode}; stderr: {standardError}");
            CliPlanResult guiPlan = CliPlanResult.Parse(new CliCommandResult(
                exitCode,
                standardOut.ToString(),
                standardError.ToString()));
            Require(guiPlan.Status == "MissingFiles"
                && guiPlan.PlanAction == "REFUSE_UNKNOWN_OR_INCONSISTENT"
                && !guiPlan.CanApply && !guiPlan.CanRestore && !guiPlan.CanRecover,
                "The production CLI JSON was rejected or changed meaning in the exact GUI parser.");

            using JsonDocument document = JsonDocument.Parse(standardOut.ToString());
            JsonElement response = document.RootElement;
            Require(response.GetProperty("schema").GetInt32() == 3
                && response.GetProperty("patcher_version").ValueKind == JsonValueKind.String
                && !response.GetProperty("installation_writes_enabled").GetBoolean()
                && response.GetProperty("status").ValueKind == JsonValueKind.String
                && response.GetProperty("observed").ValueKind == JsonValueKind.Object
                && response.GetProperty("observed").TryGetProperty("game_version", out _)
                && response.GetProperty("catalog").ValueKind == JsonValueKind.Object
                && response.GetProperty("catalog").TryGetProperty("present", out _)
                && response.GetProperty("catalog").TryGetProperty("regular_file", out _)
                && response.GetProperty("catalog").TryGetProperty("sha256", out _)
                && response.GetProperty("catalog").TryGetProperty("exact_match", out _)
                && response.GetProperty("catalog").GetProperty("source").GetString() == "embedded"
                && response.GetProperty("update").ValueKind == JsonValueKind.Null
                && response.GetProperty("channel_authority").ValueKind == JsonValueKind.Null
                && !response.GetProperty("translation_update_available").GetBoolean()
                && response.GetProperty("translation_update_kind").GetString() == "none"
                && response.GetProperty("update_problem").ValueKind == JsonValueKind.Null
                && response.GetProperty("local_problem").ValueKind == JsonValueKind.Null
                && !response.GetProperty("update_problem_blocks_apply").GetBoolean()
                && !response.GetProperty("restore_recovery_authorized").GetBoolean()
                && response.GetProperty("state").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("journal").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("process_conflicts").ValueKind == JsonValueKind.Array
                && response.GetProperty("plan").ValueKind == JsonValueKind.String
                && !response.GetProperty("can_apply").GetBoolean()
                && !response.GetProperty("can_restore").GetBoolean()
                && !response.GetProperty("can_recover").GetBoolean(),
                "CLI cache-plan JSON lost a required top-level field or field type.");

            JsonElement profile = response.GetProperty("profile");
            Require(profile.GetProperty("id").GetString() == "runtime-cache-win64-0.60.1247-prod71"
                && profile.GetProperty("mode").GetString() == "exact"
                && profile.GetProperty("game_version").GetString() == "0.60.1247"
                && profile.GetProperty("translation_policy").GetString() == "community-preview-all-drafts"
                && profile.GetProperty("entry_count").GetInt32() == 41_292
                && profile.GetProperty("applied_translations").GetInt32() == 41_037
                && profile.GetProperty("english_fallbacks").GetInt32() == 1
                && profile.GetProperty("base_fallbacks").GetInt32() == 254
                && profile.GetProperty("needs_review_fallbacks").GetInt32() == 0,
                "CLI cache-plan JSON lost the exact current release profile/composition contract.");
        }

        internal static byte[] CreateLoc1(uint localeId, uint releaseRevision, uint localeRevision, string[] values)
        {
            return CreateLoc1(localeId, releaseRevision, localeRevision, values, ContentVersion);
        }

        internal static byte[] CreateLoc1(
            uint localeId,
            uint releaseRevision,
            uint localeRevision,
            string[] values,
            string contentVersion)
        {
            const int headerSize = 160;
            byte[] guid = Encoding.UTF8.GetBytes(ContentGuid);
            byte[] version = Encoding.UTF8.GetBytes(contentVersion);
            byte[][] encodedValues = new byte[values.Length][];
            int dataLength = 0;
            for (int index = 0; index < values.Length; index++)
            {
                encodedValues[index] = Encoding.UTF8.GetBytes(values[index]);
                dataLength = checked(dataLength + encodedValues[index].Length);
            }

            int dataOffset = checked(headerSize + (values.Length * 16));
            byte[] raw = new byte[checked(dataOffset + dataLength)];
            Encoding.ASCII.GetBytes("LOC1").CopyTo(raw, 0);
            WriteUInt32(raw, 0x04, 4);
            WriteUInt32(raw, 0x08, localeId);
            WriteUInt32(raw, 0x0C, releaseRevision);
            WriteUInt32(raw, 0x10, localeRevision);
            WriteUInt32(raw, 0x1C, checked((uint)values.Length));
            WriteUInt64(raw, 0x20, headerSize);
            WriteUInt64(raw, 0x28, checked((ulong)dataOffset));
            WriteUInt64(raw, 0x30, checked((ulong)dataLength));
            WriteUInt64(raw, 0x40, checked((ulong)dataOffset));
            WriteUInt64(raw, 0x48, checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)guid.Length));
            guid.CopyTo(raw, 0x52);
            int versionLengthOffset = 0x52 + guid.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(versionLengthOffset, 2), checked((ushort)version.Length));
            version.CopyTo(raw, versionLengthOffset + 2);

            int valueOffset = 0;
            for (int index = 0; index < encodedValues.Length; index++)
            {
                int recordOffset = headerSize + (index * 16);
                WriteUInt64(raw, recordOffset, checked((ulong)(index + 1)));
                WriteUInt32(raw, recordOffset + 8, checked((uint)valueOffset));
                WriteUInt32(raw, recordOffset + 12, checked((uint)encodedValues[index].Length));
                encodedValues[index].CopyTo(raw, dataOffset + valueOffset);
                valueOffset += encodedValues[index].Length;
            }

            return raw;
        }

        private static JsonObject ParseObject(string json)
        {
            return JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Runtime-cache profile did not parse as a JSON object in the smoke test.");
        }

        private static bool ProfileRejected(string json)
        {
            try
            {
                RuntimeCacheCompatibility.Parse(json);
                return false;
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is JsonException)
            {
                return true;
            }
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
