using InvokersRu.Core.Loc1;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InvokersRu.Core.Patching
{
    public static class RuntimeCacheService
    {
        private const string EnglishFileName = "dl_en_US.bin";
        private const string TargetFileName = "dl_uk_UA.bin";
        private const string StampFileName = "dl_uk_UA.bin.ver";
        private const string CompatibleEnglishSnapshotFileName = "source.dl_en_US.bin";
        private const string CompatibleStampSnapshotFileName = "source.dl_uk_UA.bin.ver";
        private static readonly HashSet<string> LegalJournalPhases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Prepared", "BackupVerified", "StagedVerified", "PreCommitVerified",
            "ReplacementCommitted", "DisplacedVerified", "PostCommitVerified", "StateCommitted",
            "UnexpectedPreimageDetected", "UnexpectedPreimageRestored", "Completed", "Aborted"
        };

        // Resolved once per process: the mutation guards compare against this value repeatedly and must not
        // observe a root that changes underneath them while a transaction is in flight.
        private static readonly Lazy<(string? Root, string? Problem)> DefaultCacheRootValue =
            new Lazy<(string?, string?)>(ResolveDefaultCacheRoot);

        public static string DefaultCacheRoot()
        {
            (string? root, string? problem) = DefaultCacheRootValue.Value;
            return root ?? throw new InvalidOperationException(problem);
        }

        public static bool TryDefaultCacheRoot(out string cacheRoot, out string problem)
        {
            (string? root, string? issue) = DefaultCacheRootValue.Value;
            cacheRoot = root ?? string.Empty;
            problem = issue ?? string.Empty;
            return root != null;
        }

        private static (string? Root, string? Problem) ResolveDefaultCacheRoot()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!OperatingSystem.IsMacOS())
            {
                return (Path.Combine(home, "AppData", "LocalLow", "Hit_Zone", "Invokers", "i18n"), null);
            }

            // The iOS-on-Mac build stores the same tuple inside a per-installation container whose directory
            // name is a random UUID, so the only stable way to find it is to look for the English cache file.
            string containers = Path.Combine(home, "Library", "Containers");
            List<string> candidates;
            try
            {
                if (!Directory.Exists(containers))
                {
                    return (null, $"No application container directory exists at {containers}; pass --cache-root PATH.");
                }

                candidates = Directory.EnumerateDirectories(containers)
                    .Select(container => Path.Combine(container, "Data", "Documents", "i18n"))
                    .Where(candidate => File.Exists(Path.Combine(candidate, EnglishFileName)))
                    .Take(2)
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return (null, $"Application containers under {containers} could not be read: {exception.Message}; pass --cache-root PATH.");
            }

            return candidates.Count switch
            {
                1 => (candidates[0], null),
                0 => (null, $"No game localization cache containing {EnglishFileName} was found under {containers}; pass --cache-root PATH."),
                _ => (null, $"Several containers under {containers} hold {EnglishFileName}; refusing to guess, pass --cache-root PATH.")
            };
        }

        public static RuntimeCacheCompatibility DescribeTuple(string englishPath, string basePath, string stampPath, string? id)
        {
            return DescribeTuple(englishPath, basePath, stampPath, id, out _);
        }

        internal static RuntimeCacheCompatibility DescribeTuple(
            string englishPath,
            string basePath,
            string stampPath,
            string? id,
            out Loc1Document baseLocale)
        {
            byte[] englishBytes = BoundedArtifactReader.ReadRuntimeLoc1(englishPath, "runtime-cache English LOC1");
            byte[] baseBytes = BoundedArtifactReader.ReadRuntimeLoc1(basePath, "runtime-cache Ukrainian LOC1");
            byte[] stampBytes = BoundedArtifactReader.ReadRuntimeStamp(stampPath, "runtime-cache version stamp");
            Loc1Document english = Loc1Codec.Parse(englishBytes);
            baseLocale = Loc1Codec.Parse(baseBytes);
            Loc1Compatibility.RequireComposableCorpus(english, baseLocale, allowPerLocaleContentVersion: true);
            string stampValue = new UTF8Encoding(false, true).GetString(stampBytes);
            // The stamp is the game's own version marker and becomes this profile's identity, so a corrupted
            // or padded file must fail here rather than produce a profile that describes nothing real.
            if (stampValue.Length is 0 or > 64 || stampValue.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                throw new InvalidDataException($"Runtime-cache version stamp is not a bare version string: {stampPath}");
            }
            var profile = new RuntimeCacheCompatibility
            {
                Id = string.IsNullOrWhiteSpace(id) ? $"runtime-cache-{stampValue}" : id!,
                GameVersion = stampValue,
                ContentGuid = english.ContentGuid,
                EnglishContentVersion = english.ContentVersion,
                BaseContentVersion = baseLocale.ContentVersion,
                EnglishSha256 = Hashing.Sha256Bytes(englishBytes),
                BaseSha256 = Hashing.Sha256Bytes(baseBytes),
                StampSha256 = Hashing.Sha256Bytes(stampBytes),
                StampValue = stampValue,
                EnglishLocaleId = english.LocaleId,
                EnglishLocaleRevision = english.LocaleRevision,
                EnglishReleaseRevision = english.ReleaseRevision,
                BaseLocaleId = baseLocale.LocaleId,
                BaseLocaleRevision = baseLocale.LocaleRevision,
                BaseReleaseRevision = baseLocale.ReleaseRevision,
                EntryCount = english.Entries.Count,
                MinimumAppliedTranslations = 1,
                TranslationPolicy = "supervised-safe-drafts",
                Readiness = "blocked",
                Certified = false,
                BlockedReason = "Generated from a local cache tuple; catalog and output pins are not certified yet."
            };
            profile.Validate();
            return profile;
        }

        public static string DefaultStatePath()
        {
            return Path.Combine(PatchPlanner.DefaultStateRoot(), "runtime-cache", "state.v1.json");
        }

        public static RuntimeCacheInspection Inspect(string cacheRoot, RuntimeCacheCompatibility profile, string statePath)
        {
            return Inspect(cacheRoot, profile, statePath, authenticatedOfficialUpdatePredecessor: null);
        }

        internal static RuntimeCacheInspection Inspect(
            string cacheRoot,
            RuntimeCacheCompatibility profile,
            string statePath,
            RuntimeCacheCompatibility? authenticatedOfficialUpdatePredecessor)
        {
            profile.Validate();
            authenticatedOfficialUpdatePredecessor?.Validate();
            string root = Path.GetFullPath(cacheRoot);
            (string english, string target, string stamp) = ResolveFixedPaths(root);
            bool stateFileExists = File.Exists(statePath);
            PatchState? state = PatchPlanner.TryLoadState(statePath);
            PatchJournal? journal = PatchJournalStore.FindActive(statePath);
            var result = new RuntimeCacheInspection
            {
                CacheRoot = root,
                EnglishPath = english,
                TargetPath = target,
                StampPath = stamp,
                Profile = profile,
                OfficialUpdatePredecessor = authenticatedOfficialUpdatePredecessor,
                State = state,
                Journal = journal
            };
            if (journal != null)
            {
                result.Status = InstallationStatus.RecoveryRequired;
                result.Message = $"An interrupted runtime-cache transaction is at phase {journal.Phase}.";
                return result;
            }

            if (stateFileExists && state == null)
            {
                result.Status = InstallationStatus.InconsistentState;
                result.Message = "The runtime-cache state file exists but is unreadable or has an unsupported schema; refusing mutation.";
                return result;
            }

            if (!File.Exists(english) || !File.Exists(target) || !File.Exists(stamp))
            {
                result.Status = InstallationStatus.MissingFiles;
                result.Message = "The exact dl_en_US.bin, dl_uk_UA.bin, and dl_uk_UA.bin.ver cache tuple is missing.";
                return result;
            }

            bool staticPinsMatch = false;
            try
            {
                byte[] englishBytes = BoundedArtifactReader.ReadRuntimeLoc1(english, "runtime-cache observed English LOC1");
                byte[] baseBytes = BoundedArtifactReader.ReadRuntimeLoc1(target, "runtime-cache observed Ukrainian LOC1");
                byte[] stampBytes = BoundedArtifactReader.ReadRuntimeStamp(stamp, "runtime-cache observed version stamp");
                result.EnglishSha256 = Hashing.Sha256Bytes(englishBytes);
                result.BaseSha256 = Hashing.Sha256Bytes(baseBytes);
                result.StampSha256 = Hashing.Sha256Bytes(stampBytes);
                result.StampValue = BoundedArtifactReader.DecodeObservedStamp(stampBytes);
                staticPinsMatch = Hashing.FixedEqualsHex(result.EnglishSha256, profile.EnglishSha256)
                    && StampMatches(stampBytes, profile);
                Loc1Document englishDocument = Loc1Codec.Parse(englishBytes);
                Loc1Document baseDocument = Loc1Codec.Parse(baseBytes);
                result.EnglishContentVersion = englishDocument.ContentVersion;
                result.BaseContentVersion = baseDocument.ContentVersion;
                result.EnglishFormatVersion = englishDocument.FormatVersion;
                result.BaseFormatVersion = baseDocument.FormatVersion;
                result.EnglishContentGuid = englishDocument.ContentGuid;
                result.BaseContentGuid = baseDocument.ContentGuid;
                result.EnglishLocaleId = englishDocument.LocaleId;
                result.EnglishLocaleRevision = englishDocument.LocaleRevision;
                result.EnglishReleaseRevision = englishDocument.ReleaseRevision;
                result.BaseLocaleId = baseDocument.LocaleId;
                result.BaseLocaleRevision = baseDocument.LocaleRevision;
                result.BaseReleaseRevision = baseDocument.ReleaseRevision;
                result.EntryCount = baseDocument.Entries.Count;
                result.OrderedKeysetSha256 = Loc1Compatibility.ComputeOrderedKeysetSha256(baseDocument);
                VerifyDocuments(englishDocument, baseDocument, profile);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is InvalidDataException || exception is Loc1FormatException || exception is InvalidOperationException)
            {
                result.Status = state == null ? InstallationStatus.UnknownBuild : InstallationStatus.InconsistentState;
                result.Message = $"Runtime-cache LOC1 identity is not trusted: {exception.Message}";
                return result;
            }

            if (staticPinsMatch && Hashing.FixedEqualsHex(result.BaseSha256, profile.BaseSha256))
            {
                RuntimeCacheCompatibility? effectivePredecessor = authenticatedOfficialUpdatePredecessor
                    ?? (profile.Mode == "exact" ? profile : null);
                string supersededProblem = "no exact authenticated predecessor descriptor was reconstructed";
                if (state == null)
                {
                    result.Status = InstallationStatus.CompatibleOriginal;
                    result.Message = $"Exact official runtime cache {profile.Id}.";
                }
                else if (effectivePredecessor != null
                    && TryValidateOfficialUpdatePredecessor(
                        root,
                        target,
                        statePath,
                        state,
                        profile,
                        effectivePredecessor,
                        out supersededProblem))
                {
                    result.OfficialUpdatePredecessor = effectivePredecessor;
                    result.Status = InstallationStatus.PatchSupersededByOfficialUpdate;
                    result.Message = $"The game update replaced the previous authenticated patch with exact official cache {profile.Id}; its immutable backup will be preserved.";
                }
                else
                {
                    result.Status = InstallationStatus.InconsistentState;
                    result.Message = $"The official runtime-cache target has unsafe stale patch state: {supersededProblem}";
                }
                return result;
            }

            string stateProblem = "state file is absent";
            if (staticPinsMatch && state != null
                && TryValidatePatchedState(root, target, statePath, profile, state, result.BaseSha256!, out stateProblem))
            {
                result.Status = InstallationStatus.PatchedByThisTool;
                result.Message = $"Runtime cache {profile.Id} is already patched by this tool.";
                return result;
            }

            if (staticPinsMatch && state != null
                && TryValidateCatalogSupersededState(root, target, statePath, profile, state, result.BaseSha256!, out stateProblem))
            {
                result.Status = InstallationStatus.PatchSupersededByCatalogUpdate;
                result.Message = $"Runtime cache {profile.Id} contains an older restorable translation artifact; an exact newer catalog can replace it.";
                return result;
            }

            result.Status = state == null ? InstallationStatus.UnknownBuild : InstallationStatus.InconsistentState;
            result.Message = state == null
                ? "Runtime cache or version stamp differs from the exact pinned tuple; refusing it."
                : $"Runtime-cache state is not completely restorable: {stateProblem}";
            return result;
        }

        internal static PatchApplyResult Apply(RuntimeCacheInspection inspection, string translationsPath, string statePath)
        {
            MutationPolicy.RequireEnabled();
            MutationPolicy.RequireRuntimeStatePath(statePath);
            string cacheRoot = MutationPolicy.IsTestWriteBuild ? Path.GetFullPath(inspection.CacheRoot) : DefaultCacheRoot();
            MutationPolicy.RequireRuntimeRoot(inspection.CacheRoot);
            if (!PathEquals(inspection.CacheRoot, cacheRoot))
                throw new InvalidDataException("Runtime-cache inspection root is not the internally derived mutation root.");
            MutationPolicy.BindTestRuntimePaths(cacheRoot, statePath);
            MutationPolicy.RequireRuntimeBinding(cacheRoot, statePath);
            bool supersededByOfficialUpdate = inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate;
            bool supersededByCatalogUpdate = inspection.Status == InstallationStatus.PatchSupersededByCatalogUpdate;
            if (inspection.Status != InstallationStatus.CompatibleOriginal
                && !supersededByOfficialUpdate
                && !supersededByCatalogUpdate)
            {
                throw new InvalidOperationException("Runtime-cache patching requires the exact compatible original tuple.");
            }

            RuntimeCacheCompatibility profile = inspection.Profile;
            if (!profile.Certified || profile.Readiness != "ready")
            {
                throw new InvalidOperationException($"Runtime-cache compatibility is blocked: {profile.BlockedReason ?? "not certified"}");
            }

            (string englishPath, string targetPath, string stampPath) = ResolveFixedPaths(cacheRoot);
            RequireInspectionPaths(inspection, englishPath, targetPath, stampPath);
            PatchService.EnsureSupportedMutationPaths(cacheRoot, targetPath, statePath);
            using ExecutionGuard guard = ExecutionGuard.Acquire(cacheRoot, statePath);
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(cacheRoot);
            RequireInspectionPaths(inspection, englishPath, targetPath, stampPath);
            EnsureNoProcessConflicts();
            if (PatchJournalStore.FindActive(statePath) != null)
            {
                throw new InvalidOperationException("An interrupted transaction requires recovery before runtime-cache apply.");
            }
            PatchState? supersededCatalogState = null;
            if (supersededByOfficialUpdate)
            {
                MutationTestHooks.InvokeBeforeSupersededStateArchive(statePath);
                ArchiveSupersededStateUnderLock(
                    cacheRoot,
                    targetPath,
                    statePath,
                    profile,
                    inspection.OfficialUpdatePredecessor);
            }
            else if (supersededByCatalogUpdate)
            {
                supersededCatalogState = RequireCatalogSupersededStateUnderLock(
                    cacheRoot,
                    targetPath,
                    statePath,
                    profile);
            }
            else if (File.Exists(statePath))
            {
                throw new InvalidOperationException("Runtime-cache patch state appeared after inspection; refusing to overwrite it.");
            }

            Loc1Document english;
            Loc1Document baseLocale;
            if (supersededCatalogState == null)
            {
                (english, baseLocale) = VerifyExactTuple(cacheRoot, profile);
            }
            else
            {
                english = VerifyStaticTuple(cacheRoot, profile);
                byte[] backupBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                    supersededCatalogState.BackupPath,
                    "catalog-upgrade immutable Ukrainian backup");
                if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(backupBytes), profile.BaseSha256))
                    throw new IOException("Catalog-upgrade immutable Ukrainian backup hash changed.");
                baseLocale = Loc1Codec.Parse(backupBytes);
                VerifyDocuments(english, baseLocale, profile);
            }
            if (string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256))
                throw new InvalidDataException("Runtime-cache profile has no authenticated translation catalog pin.");
            byte[] catalogBytes = BoundedArtifactReader.ReadCatalog(
                translationsPath,
                profile.TranslationCatalogSha256,
                "runtime-cache translation catalog");
            string catalogHash = Hashing.Sha256Bytes(catalogBytes);

            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(
                catalogBytes,
                SignedUpdateLimits.MaxCatalogRecords);
            bool compatibleRevision = profile.Mode == CompatibleRevisionProfileBuilder.Mode;
            bool supervisedSafeDrafts = profile.TranslationPolicy == "supervised-safe-drafts";
            bool releaseApproved = profile.TranslationPolicy == "release-approved";
            bool communityPreview = profile.TranslationPolicy == "community-preview-all-drafts";
            bool includeDraft = supervisedSafeDrafts || communityPreview;
            ValidationReport validation = TranslationValidator.Validate(
                english, catalog, includeDraft, baseLocale,
                releaseApproved ? ValidationProfile.Release : ValidationProfile.Preview,
                allowPerLocaleContentVersion: true);
            if (validation.ErrorCount > 0 && !compatibleRevision)
            {
                throw new InvalidDataException($"Translation catalog has {validation.ErrorCount} blocking validation errors.");
            }

            CompositionSummary composition = TranslationComposer.Apply(
                english, baseLocale, catalog,
                includeDraft,
                approvedOnly: !includeDraft,
                excludeNeedsReview: supervisedSafeDrafts,
                allowPerLocaleContentVersion: true,
                eligibility: supervisedSafeDrafts
                    ? (record, source) => RuntimeSafeDraftPolicy.IsEligible(record, source, out _)
                    : releaseApproved
                        ? (record, source) => TranslationValidator.IsReleaseReady(record, source, out _)
                        : null,
                requireExactHint: compatibleRevision);
            if (composition.AppliedTranslations < profile.MinimumAppliedTranslations)
            {
                throw new InvalidDataException("Runtime-cache output is below the pinned minimum translation count.");
            }
            if (composition.AppliedTranslations != profile.ExpectedAppliedTranslations)
            {
                throw new InvalidDataException($"Runtime-cache output applies {composition.AppliedTranslations} translations; exact pin is {profile.ExpectedAppliedTranslations}.");
            }
            if (profile.ExpectedEnglishFallbacks >= 0 && composition.EnglishFallbacks != profile.ExpectedEnglishFallbacks)
            {
                throw new InvalidDataException($"Runtime-cache output has {composition.EnglishFallbacks} English fallbacks; exact pin is {profile.ExpectedEnglishFallbacks}.");
            }
            if (profile.ExpectedBaseFallbacks >= 0 && composition.BaseFallbacks != profile.ExpectedBaseFallbacks)
            {
                throw new InvalidDataException($"Runtime-cache output has {composition.BaseFallbacks} base fallbacks; exact pin is {profile.ExpectedBaseFallbacks}.");
            }
            if (profile.ExpectedNeedsReviewFallbacks >= 0 && composition.NeedsReviewFallbacks != profile.ExpectedNeedsReviewFallbacks)
            {
                throw new InvalidDataException($"Runtime-cache output has {composition.NeedsReviewFallbacks} review fallbacks; exact pin is {profile.ExpectedNeedsReviewFallbacks}.");
            }

            byte[] patchedRaw = Loc1Codec.BuildRawBounded(baseLocale, BoundedArtifactReader.MaximumRuntimeLoc1Bytes);
            string patchedHash = Hashing.Sha256Bytes(patchedRaw);
            if (string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(patchedHash, profile.ExpectedOutputSha256))
            {
                throw new InvalidDataException("Materialized raw runtime-cache LOC1 does not match the pinned output.");
            }

            VerifyPatchedRaw(patchedRaw, baseLocale, profile);
            string targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new InvalidDataException("Runtime-cache target has no directory.");
            string tempPath = Path.Combine(targetDirectory, $".{TargetFileName}.invokersru-{Guid.NewGuid():N}.tmp");
            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
            string backupPath = Path.Combine(stateRoot, "backups", SafeProfileId(profile.Id), $"{profile.BaseSha256}.{TargetFileName}");
            string sourcePreimageHash = supersededCatalogState?.PatchedSha256 ?? profile.BaseSha256;
            string operation = supersededCatalogState == null ? "runtime-cache-apply" : "runtime-cache-upgrade";
            var journal = NewJournal(operation, profile, cacheRoot, targetPath, backupPath,
                sourcePreimageHash, patchedHash, catalogHash, composition.AppliedTranslations);
            bool committed = false;
            try
            {
                PatchJournalStore.Save(statePath, journal);
                if (supersededCatalogState == null)
                {
                    PatchService.EnsureVerifiedBoundedBackup(
                        targetPath,
                        backupPath,
                        profile.BaseSha256,
                        BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                        "runtime-cache Ukrainian base");
                }
                else
                {
                    VerifyExactImmutableBackup(backupPath, profile.BaseSha256, "catalog-upgrade runtime-cache backup");
                }
                EnsureCompatibleSourceSnapshots(profile, englishPath, stampPath, backupPath);
                PatchService.Advance(statePath, journal, "BackupVerified");
                PatchService.WriteDurably(tempPath, patchedRaw);
                VerifyPatchedRaw(
                    BoundedArtifactReader.ReadRuntimeLoc1(tempPath, "staged runtime-cache LOC1"),
                    baseLocale,
                    profile);
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(tempPath, "staged runtime-cache LOC1"), patchedHash)) throw new IOException("Staged raw cache hash changed.");
                PatchService.Advance(statePath, journal, "StagedVerified");
                EnsureNoProcessConflicts();
                MutationPolicy.RequireRuntimeStatePath(statePath);
                MutationPolicy.RequireRuntimeRoot(cacheRoot);
                if (supersededCatalogState == null)
                {
                    VerifyExactTuple(cacheRoot, profile);
                }
                else
                {
                    supersededCatalogState = RequireCatalogSupersededStateUnderLock(
                        cacheRoot,
                        targetPath,
                        statePath,
                        profile);
                    if (!Hashing.FixedEqualsHex(supersededCatalogState.PatchedSha256, sourcePreimageHash))
                        throw new IOException("Recorded superseded translation preimage changed during catalog upgrade.");
                }
                PatchService.EnsureSupportedMutationPaths(cacheRoot, targetPath, statePath);
                PatchService.RejectExistingReparseComponents(backupPath, "immutable runtime-cache backup");
                PatchService.Advance(statePath, journal, "PreCommitVerified");
                PatchService.AtomicReplacePreservingPreimage(tempPath, targetPath, sourcePreimageHash, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(targetPath, "replaced runtime-cache LOC1"), patchedHash)) throw new IOException("Replaced raw cache hash changed.");
                PatchService.Advance(statePath, journal, "PostCommitVerified");
                var state = new PatchState
                {
                    BuildId = profile.Id,
                    GameRoot = cacheRoot,
                    TargetPath = targetPath,
                    BackupPath = backupPath,
                    OriginalSha256 = profile.BaseSha256.ToUpperInvariant(),
                    PatchedSha256 = patchedHash,
                    TranslationsSha256 = catalogHash,
                    AppliedAt = DateTimeOffset.UtcNow,
                    AppliedTranslations = composition.AppliedTranslations
                };
                PatchService.WriteStateAtomically(statePath, state);
                PatchService.Advance(statePath, journal, "StateCommitted");
                PatchService.Advance(statePath, journal, "Completed");
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return new PatchApplyResult { State = state, Composition = composition, Validation = validation };
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                if (!committed && File.Exists(targetPath) && Hashing.FixedEqualsHex(HashRuntimeLoc1(targetPath, "runtime-cache rollback target"), sourcePreimageHash))
                {
                    journal.Phase = "Aborted";
                    PatchJournalStore.Save(statePath, journal);
                    PatchJournalStore.Delete(statePath, journal.TransactionId);
                }
            }
        }

        internal static void Restore(string statePath, RuntimeCacheCompatibility profile)
        {
            MutationPolicy.RequireEnabled();
            MutationPolicy.RequireRuntimeStatePath(statePath);
            profile.Validate();
            PatchState state = PatchPlanner.TryLoadState(statePath) ?? throw new InvalidOperationException("No valid runtime-cache patch state exists.");
            MutationPolicy.RequireRuntimeBinding(state.GameRoot, statePath);
            RuntimeCacheInspection inspection = Inspect(state.GameRoot, profile, statePath);
            bool catalogSuperseded = inspection.Status == InstallationStatus.PatchSupersededByCatalogUpdate;
            if (inspection.Status != InstallationStatus.PatchedByThisTool && !catalogSuperseded)
                throw new InvalidOperationException("Runtime-cache restore requires an exact current or safely superseded translation state.");
            if (catalogSuperseded)
            {
                state = RequireCatalogSupersededStateUnderLock(state.GameRoot, state.TargetPath, statePath, profile);
            }
            else
            {
                ValidateRecordedPaths(state, statePath, profile);
            }
            using ExecutionGuard guard = ExecutionGuard.Acquire(state.GameRoot, statePath);
            MutationPolicy.RequireRuntimeStatePath(statePath);
            state = PatchPlanner.TryLoadState(statePath) ?? throw new InvalidOperationException("Runtime-cache patch state disappeared after locking.");
            MutationPolicy.RequireRuntimeBinding(state.GameRoot, statePath);
            EnsureNoProcessConflicts();
            if (PatchJournalStore.FindActive(statePath) != null) throw new InvalidOperationException("Runtime-cache recovery is required before restore.");
            VerifyStaticTuple(state.GameRoot, profile);
            if (catalogSuperseded)
            {
                state = RequireCatalogSupersededStateUnderLock(state.GameRoot, state.TargetPath, statePath, profile);
            }
            else
            {
                ValidateRecordedPaths(state, statePath, profile);
            }
            RestoreUnderLock(state, statePath, profile, catalogSuperseded);
        }

        internal static string Recover(string statePath, RuntimeCacheCompatibility profile)
        {
            MutationPolicy.RequireEnabled();
            MutationPolicy.RequireRuntimeStatePath(statePath);
            profile.Validate();
            string lockRoot = DefaultCacheRoot();
            if (MutationPolicy.IsTestWriteBuild)
            {
                PatchJournal preliminary = PatchJournalStore.FindActive(statePath) ?? throw new InvalidOperationException("No runtime-cache journal requires recovery.");
                lockRoot = preliminary.GameRoot;
            }
            MutationPolicy.RequireRuntimeBinding(lockRoot, statePath);
            using ExecutionGuard guard = ExecutionGuard.Acquire(lockRoot, statePath);
            EnsureNoProcessConflicts();
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeBinding(lockRoot, statePath);
            PatchJournal journal = LoadAndValidateRuntimeJournalUnderLock(statePath, lockRoot, profile);
            string currentHash = File.Exists(journal.TargetPath) ? HashRuntimeLoc1(journal.TargetPath, "runtime-cache recovery target") : string.Empty;
            ValidateRecoveryStateUnderLock(statePath, journal, profile, currentHash);
            PatchService.ValidateRecoveryPhaseReadiness(journal, currentHash);
            if (journal.Phase == "Aborted")
            {
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return "Validated aborted runtime-cache transaction cleanup.";
            }
            if (journal.Phase == "Completed")
            {
                VerifyExactImmutableBackup(
                    journal.BackupPath,
                    profile.BaseSha256,
                    "completed runtime-cache transaction backup");
                VerifyStaticTuple(journal.GameRoot, profile);
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return $"Completed cleanup for interrupted {journal.Operation} transaction.";
            }
            ResolveOrRejectQuarantineUnderLock(statePath, journal, currentHash);
            currentHash = File.Exists(journal.TargetPath) ? HashRuntimeLoc1(journal.TargetPath, "runtime-cache recovered target") : string.Empty;
            if (Hashing.FixedEqualsHex(currentHash, journal.SourceSha256))
            {
                if (File.Exists(journal.QuarantinePath) || File.Exists(journal.RollbackPath))
                    throw new IOException("Uncommitted runtime-cache recovery has unexpected preserved transaction files.");
                journal.Phase = "Aborted";
                PatchJournalStore.Save(statePath, journal);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return "No runtime-cache replacement was committed; journal cleared.";
            }
            if (!Hashing.FixedEqualsHex(currentHash, journal.ExpectedOutputSha256)) throw new IOException("Runtime-cache recovery target is neither recorded source nor output.");
            if (!File.Exists(journal.QuarantinePath))
                throw new IOException("Committed runtime-cache recovery is missing its validated displaced-file quarantine.");
            VerifyExactImmutableBackup(
                journal.BackupPath,
                profile.BaseSha256,
                "runtime-cache recovery backup");
            VerifyStaticTuple(journal.GameRoot, profile);
            if (journal.Operation is "runtime-cache-apply" or "runtime-cache-upgrade")
            {
                PatchService.WriteStateAtomically(statePath, new PatchState
                {
                    BuildId = profile.Id,
                    GameRoot = journal.GameRoot,
                    TargetPath = journal.TargetPath,
                    BackupPath = journal.BackupPath,
                    OriginalSha256 = profile.BaseSha256,
                    PatchedSha256 = journal.ExpectedOutputSha256,
                    TranslationsSha256 = journal.TranslationsSha256,
                    AppliedTranslations = journal.AppliedTranslations,
                    AppliedAt = journal.CreatedAt
                });
            }
            else if (File.Exists(statePath)) File.Delete(statePath);
            journal.Phase = "Completed";
            PatchJournalStore.Save(statePath, journal);
            if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
            PatchJournalStore.Delete(statePath, journal.TransactionId);
            return $"Recovered interrupted {journal.Operation} transaction.";
        }

        /// <summary>
        /// Atomically rebinds an already installed compatible artifact to a newer authenticated catalog
        /// when both catalogs materialize byte-for-byte identical output.  No game file is rewritten and
        /// there is no multi-file crash window: the old complete state or the new complete state survives.
        /// </summary>
        internal static PatchApplyResult RebindEquivalentCatalogState(
            RuntimeCacheInspection installedInspection,
            RuntimeCacheCompatibility selectedProfile,
            string selectedCatalogPath,
            string statePath)
        {
            MutationPolicy.RequireEnabled();
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(installedInspection.CacheRoot);
            installedInspection.Profile.Validate();
            selectedProfile.Validate();
            RuntimeCacheCompatibility installedProfile = installedInspection.Profile;
            if (installedInspection.Status != InstallationStatus.PatchedByThisTool
                || installedProfile.Mode != CompatibleRevisionProfileBuilder.Mode
                || selectedProfile.Mode != CompatibleRevisionProfileBuilder.Mode
                || !SameCompatibleSourceTuple(installedProfile, selectedProfile)
                || installedProfile.ExpectedOutputSha256 == null
                || selectedProfile.ExpectedOutputSha256 == null
                || !Hashing.FixedEqualsHex(installedProfile.ExpectedOutputSha256, selectedProfile.ExpectedOutputSha256)
                || installedProfile.TranslationCatalogSha256 == null
                || selectedProfile.TranslationCatalogSha256 == null
                || Hashing.FixedEqualsHex(installedProfile.TranslationCatalogSha256, selectedProfile.TranslationCatalogSha256))
            {
                throw new InvalidDataException("Equivalent catalog metadata rebind is not the exact same compatible artifact.");
            }

            byte[] catalogBytes = BoundedArtifactReader.ReadCatalog(
                selectedCatalogPath,
                selectedProfile.TranslationCatalogSha256,
                "equivalent catalog metadata rebind catalog");

            string root = Path.GetFullPath(installedInspection.CacheRoot);
            (_, string targetPath, _) = ResolveFixedPaths(root);
            using ExecutionGuard guard = ExecutionGuard.Acquire(root, statePath);
            EnsureNoProcessConflicts();
            if (PatchJournalStore.FindActive(statePath) != null)
                throw new InvalidOperationException("Recovery is required before equivalent catalog metadata rebind.");

            RuntimeCacheInspection reloaded = Inspect(root, installedProfile, statePath);
            if (reloaded.Status != InstallationStatus.PatchedByThisTool || reloaded.State == null)
                throw new InvalidDataException("Installed artifact changed before equivalent catalog metadata rebind.");
            PatchState oldState = reloaded.State;
            ValidateRecordedPaths(oldState, statePath, installedProfile);
            VerifyStaticTuple(root, selectedProfile);
            VerifyCompatibleSourceSnapshots(selectedProfile, oldState.BackupPath);
            if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(targetPath, "metadata-rebind runtime-cache target"), selectedProfile.ExpectedOutputSha256))
                throw new InvalidDataException("Equivalent catalog metadata rebind target changed under lock.");
            MutationTestHooks.InvokeBeforeEquivalentCatalogCommit(selectedCatalogPath);
            _ = BoundedArtifactReader.ReadCatalog(
                selectedCatalogPath,
                selectedProfile.TranslationCatalogSha256,
                "equivalent catalog metadata rebind catalog under lock");

            var rebound = new PatchState
            {
                Schema = 1,
                BuildId = selectedProfile.Id,
                GameRoot = oldState.GameRoot,
                TargetPath = oldState.TargetPath,
                BackupPath = oldState.BackupPath,
                OriginalSha256 = oldState.OriginalSha256,
                PatchedSha256 = selectedProfile.ExpectedOutputSha256,
                TranslationsSha256 = selectedProfile.TranslationCatalogSha256,
                AppliedAt = oldState.AppliedAt,
                AppliedTranslations = selectedProfile.ExpectedAppliedTranslations
            };
            try
            {
                PatchService.WriteStateAtomically(statePath, rebound);
                RuntimeCacheInspection verified = Inspect(root, selectedProfile, statePath);
                if (verified.Status != InstallationStatus.PatchedByThisTool)
                    throw new InvalidDataException("Equivalent catalog metadata rebind did not produce an exact restorable state.");
            }
            catch
            {
                PatchService.WriteStateAtomically(statePath, oldState);
                throw;
            }

            return new PatchApplyResult
            {
                State = rebound,
                Composition = new CompositionSummary
                {
                    AppliedTranslations = selectedProfile.ExpectedAppliedTranslations,
                    EnglishFallbacks = selectedProfile.ExpectedEnglishFallbacks,
                    BaseFallbacks = selectedProfile.ExpectedBaseFallbacks,
                    NeedsReviewFallbacks = selectedProfile.ExpectedNeedsReviewFallbacks
                },
                Validation = new ValidationReport()
            };
        }

        /// <summary>
        /// Authenticates an interrupted runtime-cache transaction without changing any files. The resolver
        /// uses this only to select the one exact signed profile that is allowed to recover the journal;
        /// <see cref="Recover"/> repeats every check after acquiring the execution lock before it mutates.
        /// </summary>
        internal static bool TryAuthenticateRecovery(
            string cacheRoot,
            string statePath,
            RuntimeCacheCompatibility profile,
            out string problem)
        {
            try
            {
                profile.Validate();
                string root = Path.GetFullPath(cacheRoot);
                PatchJournal journal = LoadAndValidateRuntimeJournal(statePath, root, profile);
                VerifyStaticTuple(root, profile);
                string currentHash = File.Exists(journal.TargetPath)
                    ? HashRuntimeLoc1(journal.TargetPath, "runtime-cache authenticated recovery target")
                    : string.Empty;
                ValidateRecoveryStateUnderLock(statePath, journal, profile, currentHash);
                PatchService.ValidateRecoveryPhaseReadiness(journal, currentHash);
                problem = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is UnauthorizedAccessException || exception is InvalidDataException
                || exception is ArgumentException || exception is NotSupportedException
                || exception is System.Security.SecurityException || exception is Loc1FormatException)
            {
                problem = exception.Message;
                return false;
            }
        }

        private static void RestoreUnderLock(
            PatchState state,
            string statePath,
            RuntimeCacheCompatibility profile,
            bool catalogSuperseded)
        {
            if (!File.Exists(state.TargetPath)) throw new FileNotFoundException("Runtime-cache target is missing.");
            string currentHash = HashRuntimeLoc1(state.TargetPath, "runtime-cache restore target");
            if (Hashing.FixedEqualsHex(currentHash, state.OriginalSha256))
            {
                EnsureNoProcessConflicts();
                PatchService.EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(state.TargetPath, "runtime-cache already-restored target"), state.OriginalSha256))
                    throw new IOException("Original runtime-cache target changed before no-op restore cleanup.");
                File.Delete(statePath);
                return;
            }
            if (!Hashing.FixedEqualsHex(currentHash, state.PatchedSha256)) throw new IOException("Runtime-cache target no longer matches recorded patch.");
            if (!File.Exists(state.BackupPath) || !Hashing.FixedEqualsHex(HashRuntimeLoc1(state.BackupPath, "runtime-cache immutable restore backup"), state.OriginalSha256))
                throw new IOException("Immutable runtime-cache backup is missing or invalid.");
            string directory = Path.GetDirectoryName(state.TargetPath) ?? throw new InvalidDataException("Runtime-cache target has no directory.");
            string tempPath = Path.Combine(directory, $".{TargetFileName}.restore-{Guid.NewGuid():N}.tmp");
            string operation = catalogSuperseded ? "runtime-cache-upgrade-restore" : "runtime-cache-restore";
            PatchJournal journal = NewJournal(operation, profile, state.GameRoot, state.TargetPath, state.BackupPath,
                state.PatchedSha256, state.OriginalSha256, state.TranslationsSha256, state.AppliedTranslations);
            bool committed = false;
            try
            {
                PatchJournalStore.Save(statePath, journal);
                byte[] restoreBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                    state.BackupPath,
                    "immutable runtime-cache restore backup");
                if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(restoreBytes), state.OriginalSha256))
                    throw new IOException("Immutable runtime-cache restore backup hash changed while reading.");
                PatchService.WriteDurably(tempPath, restoreBytes);
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(tempPath, "staged runtime-cache restore"), state.OriginalSha256)) throw new IOException("Staged cache restore hash is invalid.");
                PatchService.Advance(statePath, journal, "StagedVerified");
                EnsureNoProcessConflicts();
                VerifyStaticTuple(state.GameRoot, profile);
                PatchService.EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                PatchService.RejectExistingReparseComponents(state.BackupPath, "immutable runtime-cache backup");
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(state.TargetPath, "runtime-cache pre-restore target"), state.PatchedSha256)) throw new IOException("Runtime-cache target changed before restore.");
                PatchService.Advance(statePath, journal, "PreCommitVerified");
                PatchService.AtomicReplacePreservingPreimage(tempPath, state.TargetPath, state.PatchedSha256, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(state.TargetPath, "restored runtime-cache target"), state.OriginalSha256)) throw new IOException("Restored runtime cache hash is invalid.");
                PatchService.Advance(statePath, journal, "PostCommitVerified");
                File.Delete(statePath);
                PatchService.Advance(statePath, journal, "StateCommitted");
                PatchService.Advance(statePath, journal, "Completed");
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                if (!committed && File.Exists(state.TargetPath) && Hashing.FixedEqualsHex(HashRuntimeLoc1(state.TargetPath, "runtime-cache restore rollback target"), state.PatchedSha256))
                {
                    journal.Phase = "Aborted";
                    PatchJournalStore.Save(statePath, journal);
                    PatchJournalStore.Delete(statePath, journal.TransactionId);
                }
            }
        }

        private static (Loc1Document English, Loc1Document Target) VerifyExactTuple(
            string root,
            RuntimeCacheCompatibility profile)
        {
            (_, string targetPath, _) = ResolveFixedPaths(root);
            Loc1Document english = VerifyStaticTuple(root, profile);
            byte[] targetBytes = BoundedArtifactReader.ReadRuntimeLoc1(targetPath, "pinned runtime-cache Ukrainian LOC1");
            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(targetBytes), profile.BaseSha256))
                throw new IOException("Pinned dl_uk_UA.bin changed.");
            Loc1Document target = Loc1Codec.Parse(targetBytes);
            VerifyDocuments(english, target, profile);
            return (english, target);
        }

        private static bool TryValidateSupersededState(
            string cacheRoot,
            string targetPath,
            string statePath,
            PatchState state,
            out string problem)
        {
            problem = string.Empty;
            try
            {
                string fixedRoot = MutationPolicy.IsTestWriteBuild ? Path.GetFullPath(cacheRoot) : DefaultCacheRoot();
                if (!PathEquals(cacheRoot, fixedRoot) || !PathEquals(state.GameRoot, fixedRoot))
                    throw new InvalidDataException("recorded cache root differs from the fixed runtime cache");
                (string _, string fixedTarget, string _) = ResolveFixedPaths(fixedRoot);
                if (!PathEquals(targetPath, fixedTarget) || !PathEquals(state.TargetPath, fixedTarget))
                    throw new InvalidDataException("recorded target differs from the fixed Ukrainian cache file");
                if (string.IsNullOrWhiteSpace(state.BuildId) || state.BuildId.Length > 128
                    || state.BuildId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
                    throw new InvalidDataException("recorded build id is unsafe");
                ValidateRecordedHash(state.OriginalSha256, "recorded original hash");
                ValidateRecordedHash(state.PatchedSha256, "recorded patched hash");
                ValidateRecordedHash(state.TranslationsSha256, "recorded catalog hash");
                if (state.AppliedTranslations <= 0 || state.AppliedTranslations > 100000)
                    throw new InvalidDataException("recorded translation count is invalid");

                string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath))
                    ?? throw new InvalidDataException("runtime state has no parent directory");
                string backupRoot = Path.Combine(stateRoot, "backups");
                string backupPath = Path.GetFullPath(state.BackupPath);
                string expectedBackup = Path.Combine(
                    backupRoot,
                    SafeProfileId(state.BuildId),
                    $"{state.OriginalSha256}.{TargetFileName}");
                if (!PathEquals(backupPath, expectedBackup))
                    throw new InvalidDataException("recorded backup is not the exact content-addressed path");
                PatchService.RejectExistingReparseComponents(backupPath, "superseded runtime-cache backup");
                if (!File.Exists(backupPath)
                    || (File.GetAttributes(backupPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !Hashing.FixedEqualsHex(HashRuntimeLoc1(backupPath, "superseded runtime-cache backup"), state.OriginalSha256))
                    throw new InvalidDataException("recorded immutable backup is missing or invalid");
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is InvalidDataException || exception is InvalidOperationException)
            {
                problem = exception.Message;
                return false;
            }
        }

        private static bool TryValidateOfficialUpdatePredecessor(
            string cacheRoot,
            string targetPath,
            string statePath,
            PatchState state,
            RuntimeCacheCompatibility currentProfile,
            RuntimeCacheCompatibility predecessor,
            out string problem)
        {
            problem = string.Empty;
            try
            {
                if (currentProfile.Mode is not ("exact" or CompatibleRevisionProfileBuilder.Mode)
                    || predecessor.Mode is not ("exact" or CompatibleRevisionProfileBuilder.Mode)
                    || !string.Equals(currentProfile.ContentGuid, predecessor.ContentGuid, StringComparison.Ordinal)
                    || predecessor.ExpectedOutputSha256 == null
                    || Hashing.FixedEqualsHex(currentProfile.BaseSha256, predecessor.ExpectedOutputSha256))
                {
                    throw new InvalidDataException("compatible-revision predecessor/current family or identity is invalid");
                }

                ValidateRecordedPathsReadOnly(
                    state.GameRoot,
                    state.TargetPath,
                    state.BackupPath,
                    statePath,
                    predecessor);
                if (state.Schema != 1
                    || !string.Equals(state.BuildId, predecessor.Id, StringComparison.Ordinal)
                    || !Hashing.FixedEqualsHex(state.OriginalSha256, predecessor.BaseSha256)
                    || predecessor.ExpectedOutputSha256 == null
                    || !Hashing.FixedEqualsHex(state.PatchedSha256, predecessor.ExpectedOutputSha256)
                    || predecessor.TranslationCatalogSha256 == null
                    || !Hashing.FixedEqualsHex(state.TranslationsSha256, predecessor.TranslationCatalogSha256)
                    || state.AppliedTranslations != predecessor.ExpectedAppliedTranslations
                    || state.AppliedTranslations is < 1 or > 100_000
                    || !PathEquals(cacheRoot, state.GameRoot)
                    || !PathEquals(targetPath, state.TargetPath)
                    || state.AppliedAt == default)
                {
                    throw new InvalidDataException("compatible-revision predecessor paths or timestamp are invalid");
                }

                // Compatible predecessors additionally carry immutable EN/stamp snapshots because their
                // profile was derived locally.  An exact predecessor is instead a complete embedded or
                // signed descriptor; its exact state pins and immutable base backup are sufficient and
                // must remain usable for users upgrading from pre-snapshot releases.
                VerifyCompatibleSourceSnapshots(predecessor, state.BackupPath);
                VerifyExactImmutableBackup(state.BackupPath, predecessor.BaseSha256,
                    "official-update predecessor backup");
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is InvalidDataException || exception is InvalidOperationException
                || exception is Loc1FormatException)
            {
                problem = exception.Message;
                return false;
            }
        }

        private static void ArchiveSupersededStateUnderLock(
            string cacheRoot,
            string targetPath,
            string statePath,
            RuntimeCacheCompatibility profile,
            RuntimeCacheCompatibility? authenticatedPredecessor)
        {
            VerifyExactTuple(cacheRoot, profile);
            PatchState state = PatchPlanner.TryLoadState(statePath)
                ?? throw new InvalidDataException("Superseded runtime-cache state disappeared or became unreadable after locking.");
            string problem = "no authenticated compatible-revision predecessor was supplied";
            bool valid = authenticatedPredecessor != null
                ? TryValidateOfficialUpdatePredecessor(
                        cacheRoot,
                        targetPath,
                        statePath,
                        state,
                        profile,
                        authenticatedPredecessor,
                        out problem)
                : false;
            if (!valid)
                throw new InvalidDataException($"Superseded runtime-cache state is not safe to archive: {problem}");

            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath))
                ?? throw new InvalidDataException("Runtime-cache state has no parent directory.");
            string historyRoot = Path.Combine(stateRoot, "history", "superseded");
            PatchService.RejectExistingReparseComponents(historyRoot, "superseded runtime-cache state history");
            Directory.CreateDirectory(historyRoot);
            PatchService.RejectExistingReparseComponents(historyRoot, "superseded runtime-cache state history");
            string historyName = $"{state.AppliedAt.UtcDateTime:yyyyMMddTHHmmssZ}-{SafeProfileId(state.BuildId)}-{Guid.NewGuid():N}.json";
            string historyPath = Path.Combine(historyRoot, historyName);
            File.Move(statePath, historyPath);
            if (!File.Exists(historyPath))
                throw new IOException("Superseded runtime-cache state was not preserved in history.");
        }

        private static void ValidateRecordedHash(string value, string label)
        {
            if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"{label} is not SHA-256.");
        }

        private static Loc1Document VerifyStaticTuple(string root, RuntimeCacheCompatibility profile)
        {
            (string englishPath, _, string stampPath) = ResolveFixedPaths(root);
            byte[] englishBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                englishPath,
                "pinned runtime-cache English LOC1");
            byte[] stampBytes = BoundedArtifactReader.ReadRuntimeStamp(
                stampPath,
                "pinned runtime-cache version stamp");
            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(englishBytes), profile.EnglishSha256))
                throw new IOException("Pinned dl_en_US.bin is missing or changed.");
            if (!StampMatches(stampBytes, profile))
                throw new IOException("Pinned dl_uk_UA.bin.ver is missing or changed.");
            Loc1Document english = Loc1Codec.Parse(englishBytes);
            if (english.FormatVersion != 4 || english.ContentGuid != profile.ContentGuid
                || english.ContentVersion != profile.EnglishContentVersion
                || english.LocaleId != profile.EnglishLocaleId
                || english.LocaleRevision != profile.EnglishLocaleRevision
                || english.ReleaseRevision != profile.EnglishReleaseRevision
                || english.Entries.Count != profile.EntryCount)
                throw new InvalidDataException("Pinned English runtime-cache LOC1 identity changed.");
            if (profile.Mode == CompatibleRevisionProfileBuilder.Mode
                && (profile.OrderedKeysetSha256 == null
                    || !Hashing.FixedEqualsHex(
                        Loc1Compatibility.ComputeOrderedKeysetSha256(english),
                        profile.OrderedKeysetSha256)))
                throw new InvalidDataException("Pinned compatible-revision English ordered key set changed.");
            return english;
        }

        private static void VerifyDocuments(Loc1Document english, Loc1Document target, RuntimeCacheCompatibility profile)
        {
            Loc1Compatibility.RequireComposableCorpus(english, target, allowPerLocaleContentVersion: true);
            if (english.FormatVersion != 4 || english.ContentGuid != profile.ContentGuid
                || english.ContentVersion != profile.EnglishContentVersion
                || english.LocaleId != profile.EnglishLocaleId
                || english.LocaleRevision != profile.EnglishLocaleRevision
                || english.ReleaseRevision != profile.EnglishReleaseRevision
                || target.ContentGuid != profile.ContentGuid || target.ContentVersion != profile.BaseContentVersion
                || target.LocaleId != profile.BaseLocaleId || target.LocaleRevision != profile.BaseLocaleRevision
                || target.ReleaseRevision != profile.BaseReleaseRevision || target.Entries.Count != profile.EntryCount)
                throw new InvalidDataException("Runtime-cache LOC1 tuple does not match its exact content pins.");
            if (profile.Mode == CompatibleRevisionProfileBuilder.Mode
                && (profile.OrderedKeysetSha256 == null
                    || !Hashing.FixedEqualsHex(
                        Loc1Compatibility.ComputeOrderedKeysetSha256(target),
                        profile.OrderedKeysetSha256)))
                throw new InvalidDataException("Runtime-cache compatible-revision ordered key set changed.");
        }

        private static void VerifyPatchedRaw(byte[] raw, Loc1Document sourceBase, RuntimeCacheCompatibility profile)
        {
            Loc1Document parsed = Loc1Codec.Parse(raw);
            if (!HeaderIdentityMatches(parsed.Header, sourceBase.Header) || parsed.FormatVersion != 4
                || parsed.DataLength != checked((ulong)raw.LongLength - parsed.DataOffset)
                || parsed.ContentGuid != profile.ContentGuid || parsed.ContentVersion != profile.BaseContentVersion
                || parsed.LocaleId != profile.BaseLocaleId || parsed.LocaleRevision != profile.BaseLocaleRevision
                || parsed.ReleaseRevision != profile.BaseReleaseRevision || parsed.Entries.Count != profile.EntryCount
                || !parsed.Entries.Select(entry => entry.KeyHash).SequenceEqual(sourceBase.Entries.Select(entry => entry.KeyHash)))
                throw new InvalidDataException("Generated raw runtime cache did not preserve the exact base header/corpus identity.");
            if (profile.Mode == CompatibleRevisionProfileBuilder.Mode
                && (profile.OrderedKeysetSha256 == null
                    || !Hashing.FixedEqualsHex(
                        Loc1Compatibility.ComputeOrderedKeysetSha256(parsed),
                        profile.OrderedKeysetSha256)))
                throw new InvalidDataException("Generated compatible-revision raw cache changed the ordered key set.");
        }

        private static bool HeaderIdentityMatches(byte[] actual, byte[] expected)
        {
            if (actual.Length != expected.Length) return false;
            for (int index = 0; index < actual.Length; index++)
            {
                // The writer must update the declared data length when translated UTF-8 sizes differ.
                if (index >= 0x30 && index < 0x38) continue;
                if (actual[index] != expected[index]) return false;
            }
            return true;
        }

        private static bool StampMatches(byte[] stampBytes, RuntimeCacheCompatibility profile)
        {
            byte[] expected = new UTF8Encoding(false, true).GetBytes(profile.StampValue);
            return Hashing.FixedEqualsHex(Hashing.Sha256Bytes(stampBytes), profile.StampSha256)
                && stampBytes.SequenceEqual(expected);
        }

        public static (string English, string Target, string Stamp) ResolveTuplePaths(string root)
        {
            return ResolveFixedPaths(root);
        }

        private static (string English, string Target, string Stamp) ResolveFixedPaths(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            return (
                PatchPlanner.ResolveInside(fullRoot, EnglishFileName),
                PatchPlanner.ResolveInside(fullRoot, TargetFileName),
                PatchPlanner.ResolveInside(fullRoot, StampFileName));
        }

        private static void RequireInspectionPaths(RuntimeCacheInspection inspection, string english, string target, string stamp)
        {
            if (!PathEquals(inspection.EnglishPath, english) || !PathEquals(inspection.TargetPath, target) || !PathEquals(inspection.StampPath, stamp))
                throw new InvalidDataException("Runtime-cache inspection paths are not the fixed allowlisted tuple.");
        }

        private static void ValidateRecordedPaths(PatchState state, string statePath, RuntimeCacheCompatibility profile)
        {
            ValidateRecordedPaths(state.GameRoot, state.TargetPath, state.BackupPath, statePath, profile);
            if (state.Schema != 1
                || !string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                || !Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                || string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(state.PatchedSha256, profile.ExpectedOutputSha256)
                || string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(state.TranslationsSha256, profile.TranslationCatalogSha256)
                || state.AppliedTranslations != profile.ExpectedAppliedTranslations)
                throw new InvalidDataException("Runtime-cache state does not match the trusted profile.");
            VerifyCompatibleSourceSnapshots(profile, state.BackupPath);
        }

        private static bool TryValidatePatchedState(
            string root,
            string target,
            string statePath,
            RuntimeCacheCompatibility profile,
            PatchState state,
            string actualTargetHash,
            out string problem)
        {
            string expectedBackup = ExpectedBackupPath(statePath, profile);
            if (state.Schema != 1 || !string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                || !PathEquals(state.GameRoot, root) || !PathEquals(state.TargetPath, target)
                || !PathEquals(state.BackupPath, expectedBackup)
                || !Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                || string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(state.PatchedSha256, profile.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(actualTargetHash, profile.ExpectedOutputSha256)
                || string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(state.TranslationsSha256, profile.TranslationCatalogSha256)
                || state.AppliedTranslations != profile.ExpectedAppliedTranslations)
            {
                problem = "state identity/path/hash/count pins differ from the trusted profile";
                return false;
            }
            try
            {
                PatchService.RejectExistingReparseComponents(expectedBackup, "immutable runtime-cache backup");
                if (!File.Exists(expectedBackup)
                    || !Hashing.FixedEqualsHex(HashRuntimeLoc1(expectedBackup, "authenticated runtime-cache backup"), profile.BaseSha256))
                {
                    problem = "immutable original backup is missing or invalid";
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException || exception is UnauthorizedAccessException)
            {
                problem = $"immutable original backup path is unsafe: {exception.Message}";
                return false;
            }
            problem = string.Empty;
            return true;
        }

        internal static (string English, string Stamp) ResolveCompatibleSourceSnapshotPaths(string backupPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(backupPath))
                ?? throw new InvalidDataException("Compatible-revision backup has no parent directory.");
            return (
                Path.Combine(directory, CompatibleEnglishSnapshotFileName),
                Path.Combine(directory, CompatibleStampSnapshotFileName));
        }

        private static void EnsureCompatibleSourceSnapshots(
            RuntimeCacheCompatibility profile,
            string englishPath,
            string stampPath,
            string backupPath)
        {
            if (profile.Mode != CompatibleRevisionProfileBuilder.Mode) return;
            (string englishSnapshot, string stampSnapshot) = ResolveCompatibleSourceSnapshotPaths(backupPath);
            PatchService.EnsureVerifiedBoundedBackup(
                englishPath,
                englishSnapshot,
                profile.EnglishSha256,
                BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                "compatible-revision English source");
            PatchService.EnsureVerifiedBoundedBackup(
                stampPath,
                stampSnapshot,
                profile.StampSha256,
                BoundedArtifactReader.MaximumRuntimeStampBytes,
                "compatible-revision version stamp");
            VerifyCompatibleSourceSnapshots(profile, backupPath);
        }

        private static void VerifyCompatibleSourceSnapshots(
            RuntimeCacheCompatibility profile,
            string backupPath)
        {
            if (profile.Mode != CompatibleRevisionProfileBuilder.Mode) return;
            (string englishSnapshot, string stampSnapshot) = ResolveCompatibleSourceSnapshotPaths(backupPath);
            foreach (string path in new[] { englishSnapshot, backupPath, stampSnapshot })
            {
                PatchService.RejectExistingReparseComponents(path, "compatible-revision immutable source snapshot");
                if (!File.Exists(path)
                    || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Compatible-revision immutable source snapshot is missing or unsafe.");
            }

            byte[] englishBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                englishSnapshot,
                "compatible-revision immutable English snapshot");
            byte[] baseBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                backupPath,
                "compatible-revision immutable Ukrainian snapshot");
            byte[] stampBytes = BoundedArtifactReader.ReadRuntimeStamp(
                stampSnapshot,
                "compatible-revision immutable version snapshot");
            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(englishBytes), profile.EnglishSha256)
                || !Hashing.FixedEqualsHex(Hashing.Sha256Bytes(baseBytes), profile.BaseSha256)
                || !Hashing.FixedEqualsHex(Hashing.Sha256Bytes(stampBytes), profile.StampSha256)
                || !StampMatches(stampBytes, profile))
                throw new IOException("Compatible-revision immutable source snapshot hash changed.");

            Loc1Document english = Loc1Codec.Parse(englishBytes);
            Loc1Document baseLocale = Loc1Codec.Parse(baseBytes);
            VerifyDocuments(english, baseLocale, profile);
        }

        private static bool TryValidateCatalogSupersededState(
            string root,
            string target,
            string statePath,
            RuntimeCacheCompatibility profile,
            PatchState state,
            string actualTargetHash,
            out string problem)
        {
            string expectedBackup = ExpectedBackupPath(statePath, profile);
            if (state.Schema != 1
                || !string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                || !PathEquals(state.GameRoot, root)
                || !PathEquals(state.TargetPath, target)
                || !PathEquals(state.BackupPath, expectedBackup)
                || !Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                || !TryFindSupersededArtifact(
                    profile,
                    state.PatchedSha256,
                    state.TranslationsSha256,
                    state.AppliedTranslations,
                    out _)
                || !Hashing.FixedEqualsHex(actualTargetHash, state.PatchedSha256)
                || Hashing.FixedEqualsHex(actualTargetHash, profile.BaseSha256)
                || string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || Hashing.FixedEqualsHex(actualTargetHash, profile.ExpectedOutputSha256)
                || state.AppliedAt == default)
            {
                problem = "superseded catalog state identity/path/hash/count pins are invalid";
                return false;
            }

            try
            {
                VerifyExactImmutableBackup(expectedBackup, profile.BaseSha256, "catalog-upgrade runtime-cache backup");
                VerifyCompatibleSourceSnapshots(profile, expectedBackup);
                byte[] backupBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                    expectedBackup,
                    "catalog-upgrade immutable Ukrainian backup");
                byte[] currentBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                    target,
                    "catalog-upgrade installed Ukrainian LOC1");
                byte[] englishBytes = BoundedArtifactReader.ReadRuntimeLoc1(
                    ResolveFixedPaths(root).English,
                    "catalog-upgrade English LOC1");
                Loc1Document backup = Loc1Codec.Parse(backupBytes);
                Loc1Document current = Loc1Codec.Parse(currentBytes);
                Loc1Document english = Loc1Codec.Parse(englishBytes);
                VerifyDocuments(english, backup, profile);
                VerifyDocuments(english, current, profile);
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is UnauthorizedAccessException || exception is InvalidDataException || exception is Loc1FormatException)
            {
                problem = $"superseded catalog state is not safely restorable: {exception.Message}";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static PatchState RequireCatalogSupersededStateUnderLock(
            string cacheRoot,
            string targetPath,
            string statePath,
            RuntimeCacheCompatibility profile)
        {
            VerifyStaticTuple(cacheRoot, profile);
            PatchState state = PatchPlanner.TryLoadState(statePath)
                ?? throw new InvalidDataException("Superseded catalog state disappeared or became unreadable after locking.");
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Superseded catalog target disappeared after locking.", targetPath);
            string targetHash = HashRuntimeLoc1(targetPath, "superseded runtime-cache target");
            if (!TryValidateCatalogSupersededState(
                    cacheRoot,
                    targetPath,
                    statePath,
                    profile,
                    state,
                    targetHash,
                    out string problem))
            {
                throw new InvalidDataException($"Superseded catalog state is not safe to replace: {problem}");
            }

            return state;
        }

        private static void VerifyExactImmutableBackup(string backupPath, string expectedHash, string label)
        {
            PatchService.RejectExistingReparseComponents(backupPath, label);
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("Immutable runtime-cache backup is missing.", backupPath);
            FileAttributes attributes = File.GetAttributes(backupPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Immutable runtime-cache backup is not a regular file.");
            if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(backupPath, label), expectedHash))
                throw new InvalidDataException("Immutable runtime-cache backup does not match the pinned official base hash.");
        }

        private static string HashRuntimeLoc1(string path, string purpose) =>
            BoundedArtifactReader.Sha256File(
                path,
                BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                purpose);

        private static bool IsRecordedHash(string? value)
        {
            return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        private static bool SameCompatibleSourceTuple(
            RuntimeCacheCompatibility left,
            RuntimeCacheCompatibility right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal)
                && string.Equals(left.ContentGuid, right.ContentGuid, StringComparison.Ordinal)
                && string.Equals(left.EnglishContentVersion, right.EnglishContentVersion, StringComparison.Ordinal)
                && string.Equals(left.BaseContentVersion, right.BaseContentVersion, StringComparison.Ordinal)
                && Hashing.FixedEqualsHex(left.EnglishSha256, right.EnglishSha256)
                && Hashing.FixedEqualsHex(left.BaseSha256, right.BaseSha256)
                && Hashing.FixedEqualsHex(left.StampSha256, right.StampSha256)
                && left.EnglishLocaleId == right.EnglishLocaleId
                && left.EnglishLocaleRevision == right.EnglishLocaleRevision
                && left.EnglishReleaseRevision == right.EnglishReleaseRevision
                && left.BaseLocaleId == right.BaseLocaleId
                && left.BaseLocaleRevision == right.BaseLocaleRevision
                && left.BaseReleaseRevision == right.BaseReleaseRevision
                && left.EntryCount == right.EntryCount
                && left.OrderedKeysetSha256 != null
                && right.OrderedKeysetSha256 != null
                && Hashing.FixedEqualsHex(left.OrderedKeysetSha256, right.OrderedKeysetSha256);
        }

        private static bool IsSafeSupersededPatchHash(string? value, RuntimeCacheCompatibility profile)
        {
            if (!IsRecordedHash(value)) return false;
            RuntimeCacheSupersededArtifact[] matches = profile.SupersededArtifacts
                .Where(artifact => Hashing.FixedEqualsHex(artifact.OutputSha256, value!))
                .Take(2)
                .ToArray();
            return matches.Length == 1;
        }

        private static bool TryFindSupersededArtifact(
            RuntimeCacheCompatibility profile,
            string outputSha256,
            string catalogSha256,
            int appliedTranslations,
            out RuntimeCacheSupersededArtifact? artifact)
        {
            RuntimeCacheSupersededArtifact[] matches = profile.SupersededArtifacts
                .Where(candidate => Hashing.FixedEqualsHex(candidate.OutputSha256, outputSha256)
                    && Hashing.FixedEqualsHex(candidate.TranslationCatalogSha256, catalogSha256)
                    && candidate.AppliedTranslations == appliedTranslations)
                .Take(2)
                .ToArray();
            artifact = matches.Length == 1 ? matches[0] : null;
            return artifact != null;
        }

        private static bool IsExactCurrentRecordedState(
            PatchState state,
            string statePath,
            RuntimeCacheCompatibility profile)
        {
            try
            {
                string expectedBackup = ExpectedBackupPath(statePath, profile);
                (_, string expectedTarget, _) = ResolveFixedPaths(state.GameRoot);
                if (state.Schema != 1
                    || !string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                    || !PathEquals(state.TargetPath, expectedTarget)
                    || !PathEquals(state.BackupPath, expectedBackup)
                    || !Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                    || !Hashing.FixedEqualsHex(state.PatchedSha256, profile.ExpectedOutputSha256!)
                    || !Hashing.FixedEqualsHex(state.TranslationsSha256, profile.TranslationCatalogSha256!)
                    || state.AppliedTranslations != profile.ExpectedAppliedTranslations
                    || state.AppliedAt == default)
                {
                    return false;
                }
                VerifyCompatibleSourceSnapshots(profile, expectedBackup);

                VerifyExactImmutableBackup(expectedBackup, profile.BaseSha256, "current runtime-cache recovery backup");
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is UnauthorizedAccessException || exception is InvalidDataException)
            {
                return false;
            }
        }

        private static bool IsExactSupersededRecordedState(
            PatchState state,
            string statePath,
            RuntimeCacheCompatibility profile,
            string expectedPatchedHash)
        {
            try
            {
                string expectedBackup = ExpectedBackupPath(statePath, profile);
                (_, string expectedTarget, _) = ResolveFixedPaths(state.GameRoot);
                if (state.Schema != 1
                    || !string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                    || !PathEquals(state.TargetPath, expectedTarget)
                    || !PathEquals(state.BackupPath, expectedBackup)
                    || !Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                    || !Hashing.FixedEqualsHex(state.PatchedSha256, expectedPatchedHash)
                    || !TryFindSupersededArtifact(
                        profile,
                        state.PatchedSha256,
                        state.TranslationsSha256,
                        state.AppliedTranslations,
                        out _)
                    || state.AppliedAt == default)
                {
                    return false;
                }

                VerifyExactImmutableBackup(expectedBackup, profile.BaseSha256, "superseded runtime-cache recovery backup");
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidOperationException
                || exception is UnauthorizedAccessException || exception is InvalidDataException)
            {
                return false;
            }
        }

        private static void ValidateJournalPins(PatchJournal journal, RuntimeCacheCompatibility profile)
        {
            if (journal.Schema != 1
                || journal.Operation is not ("runtime-cache-apply" or "runtime-cache-restore"
                    or "runtime-cache-upgrade" or "runtime-cache-upgrade-restore")
                || !LegalJournalPhases.Contains(journal.Phase))
                throw new InvalidDataException("Runtime-cache journal schema, operation, or phase is not supported.");
            if (!string.Equals(journal.BuildId, profile.Id, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256))
                throw new InvalidDataException("Runtime-cache journal does not match the trusted profile pins.");

            bool valid = journal.Operation switch
            {
                "runtime-cache-apply" =>
                    Hashing.FixedEqualsHex(journal.SourceSha256, profile.BaseSha256)
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.ExpectedOutputSha256)
                    && Hashing.FixedEqualsHex(journal.TranslationsSha256, profile.TranslationCatalogSha256)
                    && journal.AppliedTranslations == profile.ExpectedAppliedTranslations,
                "runtime-cache-restore" =>
                    Hashing.FixedEqualsHex(journal.SourceSha256, profile.ExpectedOutputSha256)
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.BaseSha256)
                    && Hashing.FixedEqualsHex(journal.TranslationsSha256, profile.TranslationCatalogSha256)
                    && journal.AppliedTranslations == profile.ExpectedAppliedTranslations,
                "runtime-cache-upgrade" =>
                    IsSafeSupersededPatchHash(journal.SourceSha256, profile)
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.ExpectedOutputSha256)
                    && Hashing.FixedEqualsHex(journal.TranslationsSha256, profile.TranslationCatalogSha256)
                    && journal.AppliedTranslations == profile.ExpectedAppliedTranslations,
                "runtime-cache-upgrade-restore" =>
                    TryFindSupersededArtifact(
                        profile,
                        journal.SourceSha256,
                        journal.TranslationsSha256,
                        journal.AppliedTranslations,
                        out _)
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.BaseSha256),
                _ => false
            };
            if (!valid)
                throw new InvalidDataException("Runtime-cache journal does not match the trusted profile pins.");
        }

        private static void ValidateRecordedPaths(string root, string target, string backup, string statePath, RuntimeCacheCompatibility profile)
        {
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(root);
            ValidateRecordedPathsReadOnly(root, target, backup, statePath, profile);
        }

        private static void ValidateRecordedPathsReadOnly(
            string root,
            string target,
            string backup,
            string statePath,
            RuntimeCacheCompatibility profile)
        {
            (_, string expectedTarget, _) = ResolveFixedPaths(root);
            if (!PathEquals(target, expectedTarget)) throw new InvalidDataException("Recorded runtime-cache target is not dl_uk_UA.bin.");
            string expectedBackup = ExpectedBackupPath(statePath, profile);
            if (!PathEquals(expectedBackup, backup)) throw new InvalidDataException("Recorded runtime-cache backup is not the exact content-addressed path.");
            PatchService.EnsureSupportedMutationPaths(root, target, statePath);
            PatchService.RejectExistingReparseComponents(backup, "immutable runtime-cache backup");
        }

        private static PatchJournal LoadAndValidateRuntimeJournalUnderLock(
            string statePath,
            string lockRoot,
            RuntimeCacheCompatibility profile)
        {
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(lockRoot);
            return LoadAndValidateRuntimeJournal(statePath, lockRoot, profile);
        }

        private static PatchJournal LoadAndValidateRuntimeJournal(
            string statePath,
            string expectedRoot,
            RuntimeCacheCompatibility profile)
        {
            PatchJournal journal = PatchJournalStore.FindActive(statePath)
                ?? throw new InvalidOperationException("No runtime-cache journal requires recovery.");
            if (!PathEquals(journal.GameRoot, expectedRoot))
                throw new InvalidDataException("Reloaded runtime-cache journal root differs from the fixed lock root.");
            ValidateRecordedPathsReadOnly(journal.GameRoot, journal.TargetPath, journal.BackupPath, statePath, profile);
            ValidateJournalPins(journal, profile);
            string directory = Path.GetDirectoryName(journal.TargetPath) ?? throw new InvalidDataException("Journal target has no directory.");
            string fileName = Path.GetFileName(journal.TargetPath);
            string expectedQuarantine = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.displaced");
            string expectedRollback = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.rollback");
            if (!PathEquals(journal.QuarantinePath, expectedQuarantine) || !PathEquals(journal.RollbackPath, expectedRollback))
                throw new InvalidDataException("Runtime-cache journal quarantine paths are not the exact transaction-derived paths.");
            PatchService.RejectExistingReparseComponents(journal.QuarantinePath, "runtime-cache quarantine path");
            PatchService.RejectExistingReparseComponents(journal.RollbackPath, "runtime-cache rollback path");
            if ((journal.Phase == "DisplacedVerified" || journal.Phase == "PostCommitVerified" || journal.Phase == "StateCommitted" || journal.Phase == "Completed"
                    || journal.Phase == "UnexpectedPreimageDetected" || journal.Phase == "UnexpectedPreimageRestored")
                && string.IsNullOrWhiteSpace(journal.DisplacedSha256))
                throw new InvalidDataException("Runtime-cache journal phase requires a recorded displaced-file hash.");
            return journal;
        }

        private static void ValidateRecoveryStateUnderLock(
            string statePath,
            PatchJournal journal,
            RuntimeCacheCompatibility profile,
            string currentTargetHash)
        {
            bool stateFileExists = File.Exists(statePath);
            PatchState? state = PatchPlanner.TryLoadState(statePath);
            if (stateFileExists && state == null)
                throw new InvalidDataException("Runtime-cache recovery state exists but is unreadable or has an unknown schema.");

            bool normalApply = journal.Operation == "runtime-cache-apply";
            bool normalRestore = journal.Operation == "runtime-cache-restore";
            bool upgradeApply = journal.Operation == "runtime-cache-upgrade";
            bool upgradeRestore = journal.Operation == "runtime-cache-upgrade-restore";
            bool stateIsCurrent = state != null && IsExactCurrentRecordedState(state, statePath, profile);
            bool stateIsSuperseded = state != null
                && IsExactSupersededRecordedState(state, statePath, profile, journal.SourceSha256);

            if (state != null
                && (!PathEquals(state.GameRoot, journal.GameRoot)
                    || !PathEquals(state.TargetPath, journal.TargetPath)
                    || !PathEquals(state.BackupPath, journal.BackupPath)
                    || !string.Equals(state.BuildId, journal.BuildId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Runtime-cache recovery state and journal paths or identities differ.");
            }

            if ((normalApply || normalRestore) && state != null && !stateIsCurrent)
                throw new InvalidDataException("Runtime-cache recovery state does not match the current trusted profile.");
            if ((upgradeApply || upgradeRestore) && state != null && !stateIsCurrent && !stateIsSuperseded)
                throw new InvalidDataException("Runtime-cache upgrade recovery state is neither its exact source nor current trusted output.");

            if (journal.Phase == "Aborted")
            {
                if (normalApply && state != null) throw new InvalidDataException("Aborted runtime-cache apply retained patch state.");
                if (normalRestore && !stateIsCurrent) throw new InvalidDataException("Aborted runtime-cache restore lost its exact patch state.");
                if ((upgradeApply || upgradeRestore) && !stateIsSuperseded)
                    throw new InvalidDataException("Aborted runtime-cache catalog upgrade lost its exact superseded state.");
                return;
            }
            bool stateTransitionMayHaveRun = journal.Phase == "PostCommitVerified";
            bool stateTransitionRecorded = journal.Phase == "StateCommitted" || journal.Phase == "Completed";
            if (normalApply && !stateTransitionMayHaveRun && !stateTransitionRecorded && state != null)
                throw new InvalidDataException("Runtime-cache apply recovery has state before the legal state-commit boundary.");
            if (normalApply && stateTransitionRecorded && !stateIsCurrent)
                throw new InvalidDataException("Runtime-cache apply recovery lost state after StateCommitted.");
            if (normalRestore && !stateTransitionMayHaveRun && !stateTransitionRecorded && !stateIsCurrent)
                throw new InvalidDataException("Runtime-cache restore recovery lost state before the legal state-removal boundary.");
            if (normalRestore && stateTransitionRecorded && state != null)
                throw new InvalidDataException("Runtime-cache restore recovery retained state after StateCommitted.");

            if (upgradeApply && !stateTransitionMayHaveRun && !stateTransitionRecorded && !stateIsSuperseded)
                throw new InvalidDataException("Runtime-cache catalog upgrade lost its superseded state before the state-commit boundary.");
            if (upgradeApply && stateTransitionMayHaveRun && state != null && !stateIsCurrent && !stateIsSuperseded)
                throw new InvalidDataException("Runtime-cache catalog upgrade has an impossible state at the state-commit boundary.");
            if (upgradeApply && stateTransitionMayHaveRun && state == null)
                throw new InvalidDataException("Runtime-cache catalog upgrade lost both its superseded and current state at the state-commit boundary.");
            if (upgradeApply && stateTransitionRecorded && !stateIsCurrent)
                throw new InvalidDataException("Runtime-cache catalog upgrade lost its current state after StateCommitted.");

            if (upgradeRestore && !stateTransitionMayHaveRun && !stateTransitionRecorded && !stateIsSuperseded)
                throw new InvalidDataException("Runtime-cache catalog restore lost its superseded state before removal.");
            if (upgradeRestore && stateTransitionMayHaveRun && state != null && !stateIsSuperseded)
                throw new InvalidDataException("Runtime-cache catalog restore has an impossible state at the state-removal boundary.");
            if (upgradeRestore && stateTransitionRecorded && state != null)
                throw new InvalidDataException("Runtime-cache catalog restore retained state after StateCommitted.");
        }

        private static void ResolveOrRejectQuarantineUnderLock(string statePath, PatchJournal journal, string currentTargetHash)
        {
            if (File.Exists(journal.RollbackPath))
            {
                if (!Hashing.FixedEqualsHex(HashRuntimeLoc1(journal.RollbackPath, "runtime-cache rollback artifact"), journal.ExpectedOutputSha256))
                    throw new IOException("Preserved rollback output hash is invalid.");
                if (string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(HashRuntimeLoc1(journal.TargetPath, "runtime-cache displaced target"), journal.DisplacedSha256))
                    throw new IOException("Restored competing runtime-cache target no longer matches the journaled displaced preimage; rollback output was preserved.");
                throw new IOException("A competing target was restored after an atomic race; both sides remain preserved for manual recovery.");
            }

            if (!File.Exists(journal.QuarantinePath)) return;
            string displacedHash = HashRuntimeLoc1(journal.QuarantinePath, "runtime-cache quarantine artifact");
            if (!string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                && !Hashing.FixedEqualsHex(displacedHash, journal.DisplacedSha256))
                throw new IOException("Displaced-file quarantine changed after it was journaled.");
            journal.DisplacedSha256 = displacedHash;
            if (Hashing.FixedEqualsHex(displacedHash, journal.SourceSha256))
            {
                if (!Hashing.FixedEqualsHex(currentTargetHash, journal.ExpectedOutputSha256))
                    throw new IOException("Expected displaced preimage exists, but the replacement target hash is invalid.");
                PatchService.Advance(statePath, journal, "DisplacedVerified");
                return;
            }

            PatchService.Advance(statePath, journal, "UnexpectedPreimageDetected");
            if (!Hashing.FixedEqualsHex(currentTargetHash, journal.ExpectedOutputSha256))
                throw new IOException("Unexpected displaced preimage and target hashes require manual recovery; both files were preserved.");
            PatchService.RejectExistingReparseComponents(journal.QuarantinePath, "runtime-cache quarantine path");
            PatchService.RejectExistingReparseComponents(journal.RollbackPath, "runtime-cache rollback path");
            if (File.Exists(journal.RollbackPath))
                throw new IOException("Rollback preservation path appeared during recovery; both existing files were retained.");
            File.Replace(journal.QuarantinePath, journal.TargetPath, journal.RollbackPath, ignoreMetadataErrors: true);
            PatchService.Advance(statePath, journal, "UnexpectedPreimageRestored");
            throw new IOException("A competing target was atomically restored; staged output is preserved for manual recovery.");
        }

        private static string ExpectedBackupPath(string statePath, RuntimeCacheCompatibility profile)
        {
            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
            return Path.Combine(stateRoot, "backups", SafeProfileId(profile.Id), $"{profile.BaseSha256}.{TargetFileName}");
        }

        private static PatchJournal NewJournal(string operation, RuntimeCacheCompatibility profile, string root, string target,
            string backup, string sourceHash, string outputHash, string catalogHash, int applied)
        {
            string transactionId = Guid.NewGuid().ToString("N");
            string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? throw new InvalidDataException("Runtime-cache target has no directory.");
            return new PatchJournal
            {
                TransactionId = transactionId,
                Operation = operation,
                Phase = "Prepared",
                BuildId = profile.Id,
                GameRoot = Path.GetFullPath(root),
                TargetPath = Path.GetFullPath(target),
                BackupPath = Path.GetFullPath(backup),
                QuarantinePath = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{transactionId}.displaced"),
                RollbackPath = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{transactionId}.rollback"),
                SourceSha256 = sourceHash.ToUpperInvariant(),
                ExpectedOutputSha256 = outputHash.ToUpperInvariant(),
                TranslationsSha256 = catalogHash.ToUpperInvariant(),
                AppliedTranslations = applied,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        private static void EnsureNoProcessConflicts()
        {
            // Isolated mutation-smoke builds operate only on their test-bound temporary root. Production
            // mutation assemblies can never take this branch; process conflicts are covered separately by
            // the CLI/GUI plan contract while transactional tests remain independent of the user's game.
            if (MutationPolicy.IsTestWriteBuild) return;
            IReadOnlyList<string> conflicts = PatchService.FindRuntimeCacheProcessConflicts();
            if (conflicts.Count > 0)
                throw new InvalidOperationException($"Close game/launcher processes before runtime-cache mutation: {string.Join("; ", conflicts.Take(8))}.");
        }

        private static string SafeProfileId(string value) => $"{value}-{Hashing.Sha256Text(value).Substring(0, 12)}";

        private static bool PathEquals(string left, string right) => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        private static bool PathIsInside(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
