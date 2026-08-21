using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Cli;
using InvokersRu.Gui;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
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

            CliJsonContractCheck(profilePath);
            passed("CLI cache-plan JSON contract is consumable without a GUI reference");

            SignedRuntimeAuthorizationCheck();
            passed("CLI plan and direct mutation share the same signed-data authorization policy");

            StrictTranslationResultImportCheck();
            passed("model-result JSONL uses the exact seven-field schema before draft merge");

            if (MutationCapability.IsTestWriteBuild)
            {
                SupersededOfficialUpdateInspectionCheck();
                passed("official update supersedes only a completely restorable old runtime-cache state");

                CatalogSupersededMigrationCheck();
                passed("allowlisted catalog upgrade applies/restores/recovers atomically and rejects tamper");
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
                bool exactInstalledArtifact = false)
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
                    exactInstalledArtifact ? appliedTranslations : appliedTranslations - 1);
            }

            Require(Authorized(false, false, false, false, false, false),
                "The embedded offline bootstrap was rejected without an accepted channel authority.");
            Require(!Authorized(false, true, false, false, false, false),
                "An authenticated channel head without an authorized bundle was accepted.");
            Require(!Authorized(true, true, true, false, false, false, profileId),
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

            Require(profile.Id == "runtime-cache-win64-0.60.1247-prod68"
                && profile.GameVersion == "0.60.1247"
                && profile.StampValue == profile.GameVersion
                && profile.EntryCount == 41_292
                && profile.ExpectedAppliedTranslations == 41_037
                && profile.ExpectedEnglishFallbacks == 1
                && profile.ExpectedBaseFallbacks == 254
                && profile.ExpectedNeedsReviewFallbacks == 0
                && profile.TranslationPolicy == "community-preview-all-drafts"
                && profile.SupersededArtifacts.Length == 1
                && profile.SupersededArtifacts[0].OutputSha256 == "4E3AC2FBF663DA1D3EBF4B2EAEBFAADF5BC2A80AE7E424463582919F07B644CD"
                && profile.SupersededArtifacts[0].TranslationCatalogSha256 == "CBF5211BDCB8E940829F305154CE9A92C0D202BCC6B49617A4B92BA25166600C"
                && profile.SupersededArtifacts[0].AppliedTranslations == 41_037
                && profile.SupersededArtifacts[0].EnglishFallbacks == 1
                && profile.SupersededArtifacts[0].BaseFallbacks == 254
                && profile.SupersededArtifacts[0].NeedsReviewFallbacks == 0
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

                byte[] previousOfficialBase = Encoding.UTF8.GetBytes("immutable previous official cache");
                string previousOriginalHash = Hashing.Sha256Bytes(previousOfficialBase);
                string backupPath = Path.Combine(stateRoot, "backups", "retired-runtime-cache", $"{previousOriginalHash}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, previousOfficialBase);
                var oldState = new PatchState
                {
                    BuildId = "retired-runtime-cache",
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

                RuntimeCacheInspection superseded = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                Require(superseded.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && superseded.State != null
                    && superseded.Message.Contains("game update replaced", StringComparison.OrdinalIgnoreCase),
                    "An exact official tuple with a valid old state/backup was not classified as superseded by an official update.");

                File.WriteAllBytes(backupPath, Encoding.UTF8.GetBytes("corrupted stale backup"));
                RuntimeCacheInspection invalidBackup = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                Require(invalidBackup.Status == InstallationStatus.InconsistentState
                    && invalidBackup.Message.Contains("backup is missing or invalid", StringComparison.OrdinalIgnoreCase),
                    "An exact official tuple with an invalid stale backup was allowed to reapply.");
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
            Require(response.GetProperty("schema").GetInt32() == 2
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
                && response.GetProperty("update_problem").ValueKind == JsonValueKind.Null
                && response.GetProperty("state").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("journal").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("process_conflicts").ValueKind == JsonValueKind.Array
                && response.GetProperty("plan").ValueKind == JsonValueKind.String
                && !response.GetProperty("can_apply").GetBoolean()
                && !response.GetProperty("can_restore").GetBoolean()
                && !response.GetProperty("can_recover").GetBoolean(),
                "CLI cache-plan JSON lost a required top-level field or field type.");

            JsonElement profile = response.GetProperty("profile");
            Require(profile.GetProperty("id").GetString() == "runtime-cache-win64-0.60.1247-prod68"
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
            const int headerSize = 160;
            byte[] guid = Encoding.UTF8.GetBytes(ContentGuid);
            byte[] version = Encoding.UTF8.GetBytes(ContentVersion);
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
