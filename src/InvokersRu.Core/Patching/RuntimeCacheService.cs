using InvokersRu.Core.Loc1;
using InvokersRu.Core.Translations;
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
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document baseLocale = Loc1Codec.ReadFile(basePath);
            Loc1Compatibility.RequireComposableCorpus(english, baseLocale, allowPerLocaleContentVersion: true);
            byte[] stampBytes = File.ReadAllBytes(stampPath);
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
                EnglishSha256 = Hashing.Sha256File(englishPath),
                BaseSha256 = Hashing.Sha256File(basePath),
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
            profile.Validate();
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

            result.EnglishSha256 = Hashing.Sha256File(english);
            result.BaseSha256 = Hashing.Sha256File(target);
            result.StampSha256 = Hashing.Sha256File(stamp);
            bool staticPinsMatch = Hashing.FixedEqualsHex(result.EnglishSha256, profile.EnglishSha256)
                && StampMatches(stamp, profile);
            try
            {
                Loc1Document englishDocument = Loc1Codec.ReadFile(english);
                Loc1Document baseDocument = Loc1Codec.ReadFile(target);
                result.EnglishContentVersion = englishDocument.ContentVersion;
                result.BaseContentVersion = baseDocument.ContentVersion;
                VerifyDocuments(englishDocument, baseDocument, profile);
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is Loc1FormatException || exception is InvalidOperationException)
            {
                result.Status = state == null ? InstallationStatus.UnknownBuild : InstallationStatus.InconsistentState;
                result.Message = $"Runtime-cache LOC1 identity is not trusted: {exception.Message}";
                return result;
            }

            if (staticPinsMatch && Hashing.FixedEqualsHex(result.BaseSha256, profile.BaseSha256))
            {
                result.Status = state == null ? InstallationStatus.CompatibleOriginal : InstallationStatus.InconsistentState;
                result.Message = state == null
                    ? $"Exact official runtime cache {profile.Id}."
                    : "The official runtime-cache target has stale patch state; refusing mutation until it is resolved.";
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
            if (inspection.Status != InstallationStatus.CompatibleOriginal)
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
            if (File.Exists(statePath))
            {
                throw new InvalidOperationException("Runtime-cache patch state appeared after inspection; refusing to overwrite it.");
            }

            VerifyExactTuple(cacheRoot, profile);
            byte[] catalogBytes = File.ReadAllBytes(translationsPath);
            string catalogHash = Hashing.Sha256Bytes(catalogBytes);
            if (string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(catalogHash, profile.TranslationCatalogSha256))
            {
                throw new InvalidDataException("Translation catalog is not the exact artifact pinned for this runtime cache.");
            }

            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(catalogBytes);
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document baseLocale = Loc1Codec.ReadFile(targetPath);
            bool supervisedSafeDrafts = profile.TranslationPolicy == "supervised-safe-drafts";
            ValidationReport validation = TranslationValidator.Validate(
                english, catalog, includeDraft: supervisedSafeDrafts, baseLocale,
                supervisedSafeDrafts ? ValidationProfile.Preview : ValidationProfile.Release,
                allowPerLocaleContentVersion: true);
            if (validation.ErrorCount > 0)
            {
                throw new InvalidDataException($"Translation catalog has {validation.ErrorCount} blocking validation errors.");
            }

            CompositionSummary composition = TranslationComposer.Apply(
                english, baseLocale, catalog,
                includeDraft: supervisedSafeDrafts,
                approvedOnly: !supervisedSafeDrafts,
                excludeNeedsReview: supervisedSafeDrafts,
                allowPerLocaleContentVersion: true,
                eligibility: supervisedSafeDrafts
                    ? (record, source) => RuntimeSafeDraftPolicy.IsEligible(record, source, out _)
                    : null);
            if (composition.AppliedTranslations < profile.MinimumAppliedTranslations)
            {
                throw new InvalidDataException("Runtime-cache output is below the pinned minimum translation count.");
            }
            if (composition.AppliedTranslations != profile.ExpectedAppliedTranslations)
            {
                throw new InvalidDataException($"Runtime-cache output applies {composition.AppliedTranslations} translations; exact pin is {profile.ExpectedAppliedTranslations}.");
            }

            byte[] patchedRaw = Loc1Codec.BuildRaw(baseLocale);
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
            var journal = NewJournal("runtime-cache-apply", profile, cacheRoot, targetPath, backupPath,
                profile.BaseSha256, patchedHash, catalogHash, composition.AppliedTranslations);
            bool committed = false;
            try
            {
                PatchJournalStore.Save(statePath, journal);
                PatchService.EnsureVerifiedBackup(targetPath, backupPath, profile.BaseSha256);
                PatchService.Advance(statePath, journal, "BackupVerified");
                PatchService.WriteDurably(tempPath, patchedRaw);
                VerifyPatchedRaw(File.ReadAllBytes(tempPath), baseLocale, profile);
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(tempPath), patchedHash)) throw new IOException("Staged raw cache hash changed.");
                PatchService.Advance(statePath, journal, "StagedVerified");
                EnsureNoProcessConflicts();
                MutationPolicy.RequireRuntimeStatePath(statePath);
                MutationPolicy.RequireRuntimeRoot(cacheRoot);
                VerifyExactTuple(cacheRoot, profile);
                PatchService.EnsureSupportedMutationPaths(cacheRoot, targetPath, statePath);
                PatchService.RejectExistingReparseComponents(backupPath, "immutable runtime-cache backup");
                PatchService.Advance(statePath, journal, "PreCommitVerified");
                PatchService.AtomicReplacePreservingPreimage(tempPath, targetPath, profile.BaseSha256, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), patchedHash)) throw new IOException("Replaced raw cache hash changed.");
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
                if (!committed && File.Exists(targetPath) && Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), profile.BaseSha256))
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
            ValidateRecordedPaths(state, statePath, profile);
            using ExecutionGuard guard = ExecutionGuard.Acquire(state.GameRoot, statePath);
            MutationPolicy.RequireRuntimeStatePath(statePath);
            state = PatchPlanner.TryLoadState(statePath) ?? throw new InvalidOperationException("Runtime-cache patch state disappeared after locking.");
            MutationPolicy.RequireRuntimeBinding(state.GameRoot, statePath);
            ValidateRecordedPaths(state, statePath, profile);
            EnsureNoProcessConflicts();
            if (PatchJournalStore.FindActive(statePath) != null) throw new InvalidOperationException("Runtime-cache recovery is required before restore.");
            VerifyStaticTuple(state.GameRoot, profile);
            RestoreUnderLock(state, statePath, profile);
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
            string currentHash = File.Exists(journal.TargetPath) ? Hashing.Sha256File(journal.TargetPath) : string.Empty;
            ValidateRecoveryStateUnderLock(statePath, journal, profile, currentHash);
            PatchService.ValidateRecoveryPhaseReadiness(journal, currentHash);
            if (journal.Phase == "Aborted")
            {
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return "Validated aborted runtime-cache transaction cleanup.";
            }
            if (journal.Phase == "Completed")
            {
                string completedOriginalHash = journal.Operation == "runtime-cache-restore"
                    ? journal.ExpectedOutputSha256 : journal.SourceSha256;
                if (!File.Exists(journal.BackupPath)
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.BackupPath), completedOriginalHash))
                    throw new IOException("Completed runtime-cache transaction backup is missing or invalid.");
                VerifyStaticTuple(journal.GameRoot, profile);
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return $"Completed cleanup for interrupted {journal.Operation} transaction.";
            }
            ResolveOrRejectQuarantineUnderLock(statePath, journal, currentHash);
            currentHash = File.Exists(journal.TargetPath) ? Hashing.Sha256File(journal.TargetPath) : string.Empty;
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
            string originalHash = journal.Operation == "runtime-cache-restore" ? journal.ExpectedOutputSha256 : journal.SourceSha256;
            if (!File.Exists(journal.BackupPath) || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.BackupPath), originalHash))
                throw new IOException("Runtime-cache recovery backup is missing or invalid.");
            VerifyStaticTuple(journal.GameRoot, profile);
            if (journal.Operation == "runtime-cache-apply")
            {
                PatchService.WriteStateAtomically(statePath, new PatchState
                {
                    BuildId = profile.Id,
                    GameRoot = journal.GameRoot,
                    TargetPath = journal.TargetPath,
                    BackupPath = journal.BackupPath,
                    OriginalSha256 = journal.SourceSha256,
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

        private static void RestoreUnderLock(PatchState state, string statePath, RuntimeCacheCompatibility profile)
        {
            if (!File.Exists(state.TargetPath)) throw new FileNotFoundException("Runtime-cache target is missing.");
            string currentHash = Hashing.Sha256File(state.TargetPath);
            if (Hashing.FixedEqualsHex(currentHash, state.OriginalSha256))
            {
                EnsureNoProcessConflicts();
                PatchService.EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.OriginalSha256))
                    throw new IOException("Original runtime-cache target changed before no-op restore cleanup.");
                File.Delete(statePath);
                return;
            }
            if (!Hashing.FixedEqualsHex(currentHash, state.PatchedSha256)) throw new IOException("Runtime-cache target no longer matches recorded patch.");
            if (!File.Exists(state.BackupPath) || !Hashing.FixedEqualsHex(Hashing.Sha256File(state.BackupPath), state.OriginalSha256))
                throw new IOException("Immutable runtime-cache backup is missing or invalid.");
            string directory = Path.GetDirectoryName(state.TargetPath) ?? throw new InvalidDataException("Runtime-cache target has no directory.");
            string tempPath = Path.Combine(directory, $".{TargetFileName}.restore-{Guid.NewGuid():N}.tmp");
            PatchJournal journal = NewJournal("runtime-cache-restore", profile, state.GameRoot, state.TargetPath, state.BackupPath,
                state.PatchedSha256, state.OriginalSha256, state.TranslationsSha256, state.AppliedTranslations);
            bool committed = false;
            try
            {
                PatchJournalStore.Save(statePath, journal);
                PatchService.WriteDurably(tempPath, File.ReadAllBytes(state.BackupPath));
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(tempPath), state.OriginalSha256)) throw new IOException("Staged cache restore hash is invalid.");
                PatchService.Advance(statePath, journal, "StagedVerified");
                EnsureNoProcessConflicts();
                VerifyStaticTuple(state.GameRoot, profile);
                PatchService.EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                PatchService.RejectExistingReparseComponents(state.BackupPath, "immutable runtime-cache backup");
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.PatchedSha256)) throw new IOException("Runtime-cache target changed before restore.");
                PatchService.Advance(statePath, journal, "PreCommitVerified");
                PatchService.AtomicReplacePreservingPreimage(tempPath, state.TargetPath, state.PatchedSha256, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.OriginalSha256)) throw new IOException("Restored runtime cache hash is invalid.");
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
                if (!committed && File.Exists(state.TargetPath) && Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.PatchedSha256))
                {
                    journal.Phase = "Aborted";
                    PatchJournalStore.Save(statePath, journal);
                    PatchJournalStore.Delete(statePath, journal.TransactionId);
                }
            }
        }

        private static void VerifyExactTuple(string root, RuntimeCacheCompatibility profile)
        {
            (string englishPath, string targetPath, string stampPath) = ResolveFixedPaths(root);
            VerifyStaticTuple(root, profile);
            if (!Hashing.FixedEqualsHex(Hashing.Sha256File(targetPath), profile.BaseSha256)) throw new IOException("Pinned dl_uk_UA.bin changed.");
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document target = Loc1Codec.ReadFile(targetPath);
            VerifyDocuments(english, target, profile);
        }

        private static void VerifyStaticTuple(string root, RuntimeCacheCompatibility profile)
        {
            (string englishPath, _, string stampPath) = ResolveFixedPaths(root);
            if (!File.Exists(englishPath) || !Hashing.FixedEqualsHex(Hashing.Sha256File(englishPath), profile.EnglishSha256))
                throw new IOException("Pinned dl_en_US.bin is missing or changed.");
            if (!File.Exists(stampPath) || !StampMatches(stampPath, profile))
                throw new IOException("Pinned dl_uk_UA.bin.ver is missing or changed.");
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            if (english.FormatVersion != 4 || english.ContentGuid != profile.ContentGuid
                || english.ContentVersion != profile.EnglishContentVersion
                || english.LocaleId != profile.EnglishLocaleId
                || english.LocaleRevision != profile.EnglishLocaleRevision
                || english.ReleaseRevision != profile.EnglishReleaseRevision
                || english.Entries.Count != profile.EntryCount)
                throw new InvalidDataException("Pinned English runtime-cache LOC1 identity changed.");
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

        private static bool StampMatches(string stampPath, RuntimeCacheCompatibility profile)
        {
            byte[] expected = new UTF8Encoding(false, true).GetBytes(profile.StampValue);
            return Hashing.FixedEqualsHex(Hashing.Sha256File(stampPath), profile.StampSha256)
                && File.ReadAllBytes(stampPath).SequenceEqual(expected);
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
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(expectedBackup), profile.BaseSha256))
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

        private static void ValidateJournalPins(PatchJournal journal, RuntimeCacheCompatibility profile)
        {
            if (journal.Schema != 1
                || (journal.Operation != "runtime-cache-apply" && journal.Operation != "runtime-cache-restore")
                || !LegalJournalPhases.Contains(journal.Phase))
                throw new InvalidDataException("Runtime-cache journal schema, operation, or phase is not supported.");
            bool apply = journal.Operation == "runtime-cache-apply";
            string originalHash = apply ? journal.SourceSha256 : journal.ExpectedOutputSha256;
            string patchedHash = apply ? journal.ExpectedOutputSha256 : journal.SourceSha256;
            if (!string.Equals(journal.BuildId, profile.Id, StringComparison.Ordinal)
                || !Hashing.FixedEqualsHex(originalHash, profile.BaseSha256)
                || string.IsNullOrWhiteSpace(profile.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(patchedHash, profile.ExpectedOutputSha256)
                || string.IsNullOrWhiteSpace(profile.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(journal.TranslationsSha256, profile.TranslationCatalogSha256)
                || journal.AppliedTranslations != profile.ExpectedAppliedTranslations)
                throw new InvalidDataException("Runtime-cache journal does not match the trusted profile pins.");
        }

        private static void ValidateRecordedPaths(string root, string target, string backup, string statePath, RuntimeCacheCompatibility profile)
        {
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(root);
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
            PatchJournal journal = PatchJournalStore.FindActive(statePath)
                ?? throw new InvalidOperationException("No runtime-cache journal requires recovery.");
            MutationPolicy.RequireRuntimeStatePath(statePath);
            MutationPolicy.RequireRuntimeRoot(journal.GameRoot);
            if (!PathEquals(journal.GameRoot, lockRoot))
                throw new InvalidDataException("Reloaded runtime-cache journal root differs from the fixed lock root.");
            ValidateRecordedPaths(journal.GameRoot, journal.TargetPath, journal.BackupPath, statePath, profile);
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

            if (state != null)
            {
                ValidateRecordedPaths(state, statePath, profile);
                if (!PathEquals(state.GameRoot, journal.GameRoot)
                    || !PathEquals(state.TargetPath, journal.TargetPath)
                    || !PathEquals(state.BackupPath, journal.BackupPath)
                    || !string.Equals(state.BuildId, journal.BuildId, StringComparison.Ordinal)
                    || !Hashing.FixedEqualsHex(state.TranslationsSha256, journal.TranslationsSha256)
                    || state.AppliedTranslations != journal.AppliedTranslations)
                    throw new InvalidDataException("Runtime-cache recovery state and journal identities differ.");
            }
            bool apply = journal.Operation == "runtime-cache-apply";
            if (journal.Phase == "Aborted")
            {
                if (apply && state != null) throw new InvalidDataException("Aborted runtime-cache apply retained patch state.");
                if (!apply && state == null) throw new InvalidDataException("Aborted runtime-cache restore lost patch state.");
                return;
            }
            bool stateTransitionMayHaveRun = journal.Phase == "PostCommitVerified";
            bool stateTransitionRecorded = journal.Phase == "StateCommitted" || journal.Phase == "Completed";
            if (apply && !stateTransitionMayHaveRun && !stateTransitionRecorded && state != null)
                throw new InvalidDataException("Runtime-cache apply recovery has state before the legal state-commit boundary.");
            if (apply && stateTransitionRecorded && state == null)
                throw new InvalidDataException("Runtime-cache apply recovery lost state after StateCommitted.");
            if (!apply && !stateTransitionMayHaveRun && !stateTransitionRecorded && state == null)
                throw new InvalidDataException("Runtime-cache restore recovery lost state before the legal state-removal boundary.");
            if (!apply && stateTransitionRecorded && state != null)
                throw new InvalidDataException("Runtime-cache restore recovery retained state after StateCommitted.");
        }

        private static void ResolveOrRejectQuarantineUnderLock(string statePath, PatchJournal journal, string currentTargetHash)
        {
            if (File.Exists(journal.RollbackPath))
            {
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(journal.RollbackPath), journal.ExpectedOutputSha256))
                    throw new IOException("Preserved rollback output hash is invalid.");
                if (string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.TargetPath), journal.DisplacedSha256))
                    throw new IOException("Restored competing runtime-cache target no longer matches the journaled displaced preimage; rollback output was preserved.");
                throw new IOException("A competing target was restored after an atomic race; both sides remain preserved for manual recovery.");
            }

            if (!File.Exists(journal.QuarantinePath)) return;
            string displacedHash = Hashing.Sha256File(journal.QuarantinePath);
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
