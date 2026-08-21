using InvokersRu.Core.Loc1;
using InvokersRu.Core.Translations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InvokersRu.Core.Patching
{
    public sealed class PatchApplyResult
    {
        public PatchState State { get; set; } = new PatchState();
        public CompositionSummary Composition { get; set; } = new CompositionSummary();
        public ValidationReport Validation { get; set; } = new ValidationReport();
    }

    public static class PatchService
    {
        private static readonly JsonSerializerOptions StateJsonOptions = new JsonSerializerOptions { WriteIndented = true };
        private static readonly string TargetLocaleRelativePath = Path.Combine("Invokers_Data", "StreamingAssets", "i18n", "uk_UA.bin.br");
        private static readonly HashSet<string> LegalJournalPhases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Prepared", "BackupVerified", "StagedVerified", "PreCommitVerified",
            "ReplacementCommitted", "DisplacedVerified", "PostCommitVerified", "StateCommitted",
            "UnexpectedPreimageDetected", "UnexpectedPreimageRestored", "Completed", "Aborted"
        };

        internal static PatchApplyResult Apply(
            InstallationInspection inspection,
            string translationsPath,
            string statePath,
            bool includeDraft)
        {
            MutationPolicy.RequireEnabled();
            if (inspection.Status != InstallationStatus.CompatibleOriginal || inspection.Build == null)
            {
                throw new InvalidOperationException("Patch can only be applied to an exact compatible original build.");
            }

            CompatibleBuild build = inspection.Build;
            bool supervisedPreview = string.Equals(build.PatchMode, "supervised_preview", StringComparison.Ordinal);
            if (includeDraft != supervisedPreview)
            {
                throw new InvalidOperationException(supervisedPreview
                    ? "A supervised preview entry requires the explicit --include-draft acknowledgement."
                    : "Release installation writes never accept draft translations.");
            }

            if (!build.Certified || !string.Equals(build.Readiness, "ready", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Compatibility entry is blocked: {build.BlockedReason ?? "not certified"}");
            }

            string englishPath = PatchPlanner.ResolveInside(inspection.GameRoot, build.EnglishRelativePath);
            string basePath = PatchPlanner.ResolveInside(inspection.GameRoot, build.BaseRelativePath);
            string fixedTargetPath = PatchPlanner.ResolveInside(inspection.GameRoot, TargetLocaleRelativePath);
            if (!PathEquals(basePath, fixedTargetPath))
            {
                throw new InvalidDataException("Compatibility entry does not target the fixed uk_UA localization package.");
            }

            EnsureSupportedMutationPaths(inspection.GameRoot, basePath, statePath);
            using ExecutionGuard executionGuard = ExecutionGuard.Acquire(inspection.GameRoot, statePath);
            EnsureSupportedMutationPaths(inspection.GameRoot, basePath, statePath);
            EnsureGameStopped(inspection.GameRoot);
            if (PatchJournalStore.FindActive(statePath) != null)
            {
                throw new InvalidOperationException("An interrupted transaction requires recovery before apply.");
            }

            VerifyExactTupleUnderLock(inspection.GameRoot, build);

            byte[] translationSnapshot = File.ReadAllBytes(translationsPath);
            string translationsHash = Hashing.Sha256Bytes(translationSnapshot);
            if (string.IsNullOrWhiteSpace(build.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(translationsHash, build.TranslationCatalogSha256))
            {
                throw new InvalidDataException("Translation catalog is not the exact artifact pinned by the certified compatibility entry.");
            }

            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(translationSnapshot);
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document baseLocale = Loc1Codec.ReadFile(basePath);
            if (!string.Equals(english.ContentGuid, build.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(english.ContentVersion, build.ContentVersion, StringComparison.Ordinal)
                || !string.Equals(baseLocale.ContentGuid, build.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(baseLocale.ContentVersion, build.ContentVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException("LOC1 content identity does not match the compatibility manifest.");
            }

            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(Loc1Codec.ReadContainer(englishPath)), build.EnglishRawSha256)
                || !Hashing.FixedEqualsHex(Hashing.Sha256Bytes(Loc1Codec.ReadContainer(basePath)), build.BaseRawSha256)
                || baseLocale.LocaleId != build.BaseLocaleId
                || baseLocale.LocaleRevision != build.BaseLocaleRevision
                || baseLocale.Entries.Count != build.EntryCount)
            {
                throw new InvalidDataException("Parsed LOC1 invariants do not match the compatibility manifest.");
            }

            ValidationProfile validationProfile = supervisedPreview ? ValidationProfile.Preview : ValidationProfile.Release;
            ValidationReport validation = TranslationValidator.Validate(english, catalog, includeDraft: supervisedPreview, baseLocale, validationProfile);
            if (validation.ErrorCount > 0)
            {
                string examples = string.Join("; ", validation.Issues
                    .Where(issue => issue.Severity == ValidationSeverity.Error)
                    .Take(5)
                    .Select(issue => $"{issue.Id}/{issue.Code}: {issue.Message}"));
                throw new InvalidDataException($"Translation catalog has {validation.ErrorCount} blocking validation errors. {examples}");
            }

            CompositionSummary composition = TranslationComposer.Apply(
                english,
                baseLocale,
                catalog,
                includeDraft: supervisedPreview,
                approvedOnly: !supervisedPreview,
                excludeNeedsReview: supervisedPreview && build.ExcludeNeedsReview);
            if (composition.AppliedTranslations < build.MinimumAppliedTranslations)
            {
                throw new InvalidDataException($"Patch would apply only {composition.AppliedTranslations} translations; certified minimum is {build.MinimumAppliedTranslations}.");
            }
            byte[] patchedRaw = Loc1Codec.BuildRaw(baseLocale);
            if (string.IsNullOrWhiteSpace(build.ExpectedOutputRawSha256)
                || !Hashing.FixedEqualsHex(Hashing.Sha256Bytes(patchedRaw), build.ExpectedOutputRawSha256))
            {
                throw new InvalidDataException("Materialized raw LOC1 does not match the exact output pinned by the certified compatibility entry.");
            }

            Loc1Document verification = Loc1Codec.Parse(patchedRaw);
            if (verification.Entries.Count != baseLocale.Entries.Count || verification.LocaleId != baseLocale.LocaleId)
            {
                throw new InvalidDataException("Generated LOC1 failed structural verification.");
            }

            byte[] compressed = Loc1Codec.Compress(patchedRaw);
            if (string.IsNullOrWhiteSpace(build.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(Hashing.Sha256Bytes(compressed), build.ExpectedOutputSha256))
            {
                throw new InvalidDataException("Materialized Brotli container does not match the exact output pinned by the certified compatibility entry.");
            }

            string targetDirectory = Path.GetDirectoryName(basePath) ?? throw new InvalidDataException("Base locale path has no parent directory.");
            string tempPath = Path.Combine(targetDirectory, $".{Path.GetFileName(basePath)}.invokersru-{Guid.NewGuid():N}.tmp");
            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
            string backupDirectory = Path.Combine(stateRoot, "backups", Sanitize(build.Id));
            string backupPath = Path.Combine(backupDirectory, $"{build.BaseSha256}.uk_UA.bin.br");
            RejectExistingReparseComponents(backupPath, "immutable backup path");
            Directory.CreateDirectory(backupDirectory);
            RejectExistingReparseComponents(backupPath, "immutable backup path");
            string patchedHash = Hashing.Sha256Bytes(compressed);
            var journal = new PatchJournal
            {
                TransactionId = Guid.NewGuid().ToString("N"),
                Operation = "apply",
                Phase = "Prepared",
                BuildId = build.Id,
                GameRoot = Path.GetFullPath(inspection.GameRoot),
                TargetPath = Path.GetFullPath(basePath),
                BackupPath = Path.GetFullPath(backupPath),
                SourceSha256 = build.BaseSha256.ToUpperInvariant(),
                ExpectedOutputSha256 = patchedHash,
                TranslationsSha256 = translationsHash,
                AppliedTranslations = composition.AppliedTranslations,
                CreatedAt = DateTimeOffset.UtcNow
            };
            InitializeQuarantinePaths(journal);
            bool committed = false;

            try
            {
                PatchJournalStore.Save(statePath, journal);
                EnsureVerifiedBackup(basePath, backupPath, build.BaseSha256);
                Advance(statePath, journal, "BackupVerified");

                WriteDurably(tempPath, compressed);
                Loc1Document tempVerification = Loc1Codec.ReadFile(tempPath);
                if (tempVerification.Entries.Count != baseLocale.Entries.Count || tempVerification.LocaleId != baseLocale.LocaleId)
                {
                    throw new InvalidDataException("Temporary patched container failed decompression verification.");
                }

                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(tempPath), patchedHash))
                {
                    throw new InvalidDataException("Staged container hash differs from the materialized output hash.");
                }

                Advance(statePath, journal, "StagedVerified");
                EnsureGameStopped(inspection.GameRoot);
                VerifyExactTupleUnderLock(inspection.GameRoot, build);
                EnsureSupportedMutationPaths(inspection.GameRoot, basePath, statePath);
                RejectExistingReparseComponents(backupPath, "immutable backup path");

                Advance(statePath, journal, "PreCommitVerified");
                EnsureSupportedMutationPaths(inspection.GameRoot, basePath, statePath);
                AtomicReplacePreservingPreimage(tempPath, basePath, build.BaseSha256, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(basePath), patchedHash))
                {
                    throw new IOException("Patched file hash changed during replacement.");
                }

                Advance(statePath, journal, "PostCommitVerified");

                var state = new PatchState
                {
                    BuildId = build.Id,
                    GameRoot = Path.GetFullPath(inspection.GameRoot),
                    TargetPath = Path.GetFullPath(basePath),
                    BackupPath = Path.GetFullPath(backupPath),
                    OriginalSha256 = build.BaseSha256.ToUpperInvariant(),
                    PatchedSha256 = patchedHash,
                    TranslationsSha256 = translationsHash,
                    AppliedAt = DateTimeOffset.UtcNow,
                    AppliedTranslations = composition.AppliedTranslations
                };
                WriteStateAtomically(statePath, state);
                Advance(statePath, journal, "StateCommitted");
                Advance(statePath, journal, "Completed");
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return new PatchApplyResult { State = state, Composition = composition, Validation = validation };
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (!committed && File.Exists(basePath) && Hashing.FixedEqualsHex(Hashing.Sha256File(basePath), build.BaseSha256))
                {
                    journal.Phase = "Aborted";
                    PatchJournalStore.Save(statePath, journal);
                    PatchJournalStore.Delete(statePath, journal.TransactionId);
                }
            }
        }

        internal static void Restore(string statePath, CompatibilityManifest compatibility)
        {
            MutationPolicy.RequireEnabled();
            PatchState state = PatchPlanner.TryLoadState(statePath) ?? throw new InvalidOperationException("No valid patch state exists.");
            ValidateRecordedPaths(state.GameRoot, state.TargetPath, state.BackupPath, statePath);
            EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
            CompatibleBuild build = ResolveRecordedBuild(compatibility, state.BuildId, state.OriginalSha256);
            using ExecutionGuard executionGuard = ExecutionGuard.Acquire(state.GameRoot, statePath);
            EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
            RejectExistingReparseComponents(state.BackupPath, "immutable backup path");
            EnsureGameStopped(state.GameRoot);
            if (PatchJournalStore.FindActive(statePath) != null)
            {
                throw new InvalidOperationException("An interrupted transaction requires recovery before restore.");
            }

            if (!File.Exists(state.TargetPath))
            {
                throw new FileNotFoundException("Patched target is missing.");
            }

            string currentTargetHash = Hashing.Sha256File(state.TargetPath);
            if (Hashing.FixedEqualsHex(currentTargetHash, state.OriginalSha256))
            {
                VerifyStaticTupleUnderLock(state.GameRoot, build);
                EnsureGameStopped(state.GameRoot);
                EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.OriginalSha256))
                {
                    throw new IOException("Original target changed immediately before no-op restore cleanup.");
                }

                RejectExistingReparseComponents(statePath, "patch state path");
                File.Delete(statePath);
                return;
            }

            if (!Hashing.FixedEqualsHex(currentTargetHash, state.PatchedSha256))
            {
                throw new IOException("Installed target no longer matches the recorded patch. Refusing to overwrite unknown changes.");
            }

            if (!File.Exists(state.BackupPath))
            {
                throw new FileNotFoundException("Immutable backup is missing.");
            }

            if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.BackupPath), state.OriginalSha256))
            {
                throw new IOException("Backup no longer matches the original hash.");
            }

            VerifyStaticTupleUnderLock(state.GameRoot, build);

            string directory = Path.GetDirectoryName(state.TargetPath) ?? throw new InvalidDataException("Target has no parent directory.");
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(state.TargetPath)}.restore-{Guid.NewGuid():N}.tmp");
            var journal = new PatchJournal
            {
                TransactionId = Guid.NewGuid().ToString("N"),
                Operation = "restore",
                Phase = "Prepared",
                BuildId = state.BuildId,
                GameRoot = state.GameRoot,
                TargetPath = state.TargetPath,
                BackupPath = state.BackupPath,
                SourceSha256 = state.PatchedSha256,
                ExpectedOutputSha256 = state.OriginalSha256,
                TranslationsSha256 = state.TranslationsSha256,
                AppliedTranslations = state.AppliedTranslations,
                CreatedAt = DateTimeOffset.UtcNow
            };
            InitializeQuarantinePaths(journal);
            bool committed = false;
            try
            {
                PatchJournalStore.Save(statePath, journal);
                WriteDurably(tempPath, File.ReadAllBytes(state.BackupPath));
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(tempPath), state.OriginalSha256))
                {
                    throw new IOException("Staged restore file hash differs from the original backup.");
                }

                Advance(statePath, journal, "StagedVerified");
                EnsureGameStopped(state.GameRoot);
                VerifyStaticTupleUnderLock(state.GameRoot, build);
                EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                RejectExistingReparseComponents(state.BackupPath, "immutable backup path");
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.PatchedSha256))
                {
                    throw new IOException("Patched target changed immediately before restore commit.");
                }

                Advance(statePath, journal, "PreCommitVerified");
                EnsureSupportedMutationPaths(state.GameRoot, state.TargetPath, statePath);
                AtomicReplacePreservingPreimage(tempPath, state.TargetPath, state.PatchedSha256, statePath, journal);
                committed = true;
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.OriginalSha256))
                {
                    throw new IOException("Restored target does not match the original hash.");
                }

                Advance(statePath, journal, "PostCommitVerified");
                File.Delete(statePath);
                Advance(statePath, journal, "StateCommitted");
                Advance(statePath, journal, "Completed");
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (!committed && File.Exists(state.TargetPath) && Hashing.FixedEqualsHex(Hashing.Sha256File(state.TargetPath), state.PatchedSha256))
                {
                    journal.Phase = "Aborted";
                    PatchJournalStore.Save(statePath, journal);
                    PatchJournalStore.Delete(statePath, journal.TransactionId);
                }
            }
        }

        internal static string Recover(string statePath, CompatibilityManifest compatibility)
        {
            MutationPolicy.RequireEnabled();
            PatchJournal journal = PatchJournalStore.FindActive(statePath) ?? throw new InvalidOperationException("No active patch journal requires recovery.");
            EnsureSupportedRecoveryOperation(journal.Operation);
            ValidateRecordedPaths(journal.GameRoot, journal.TargetPath, journal.BackupPath, statePath);
            EnsureSupportedMutationPaths(journal.GameRoot, journal.TargetPath, statePath);
            RejectExistingReparseComponents(journal.BackupPath, "immutable backup path");
            using ExecutionGuard executionGuard = ExecutionGuard.Acquire(journal.GameRoot, statePath);
            journal = PatchJournalStore.FindActive(statePath) ?? throw new InvalidOperationException("The recovery journal was already resolved by another process.");
            ValidateRecordedPaths(journal.GameRoot, journal.TargetPath, journal.BackupPath, statePath);
            EnsureSupportedMutationPaths(journal.GameRoot, journal.TargetPath, statePath);
            RejectExistingReparseComponents(journal.BackupPath, "immutable backup path");
            EnsureGameStopped(journal.GameRoot);
            CompatibleBuild build = ValidateRecoveryJournalUnderLock(statePath, journal, compatibility);

            if (!File.Exists(journal.TargetPath))
            {
                throw new FileNotFoundException("Recovery target is missing.");
            }

            string currentHash = Hashing.Sha256File(journal.TargetPath);
            ValidateRecoveryStateUnderLock(statePath, journal, build, currentHash);
            ValidateRecoveryPhaseReadiness(journal, currentHash);
            if (journal.Phase == "Aborted")
            {
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return "Validated aborted patch transaction cleanup.";
            }
            if (journal.Phase == "Completed")
            {
                string completedOriginalHash = journal.Operation == "restore"
                    ? journal.ExpectedOutputSha256 : journal.SourceSha256;
                if (!File.Exists(journal.BackupPath)
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.BackupPath), completedOriginalHash))
                    throw new IOException("Completed transaction backup is missing or invalid.");
                VerifyStaticTupleUnderLock(journal.GameRoot, build);
                if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return $"Completed cleanup for interrupted {journal.Operation} transaction.";
            }
            ResolveOrRejectQuarantineUnderLock(statePath, journal, currentHash);
            currentHash = Hashing.Sha256File(journal.TargetPath);
            if (Hashing.FixedEqualsHex(currentHash, journal.SourceSha256))
            {
                if (File.Exists(journal.QuarantinePath) || File.Exists(journal.RollbackPath))
                    throw new IOException("Uncommitted recovery has unexpected preserved transaction files.");
                journal.Phase = "Aborted";
                PatchJournalStore.Save(statePath, journal);
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                return "No replacement was committed; the interrupted journal was safely cleared.";
            }

            if (!Hashing.FixedEqualsHex(currentHash, journal.ExpectedOutputSha256))
            {
                throw new IOException("Recovery target matches neither the recorded source nor expected output. Use the official launcher repair flow.");
            }

            if (!File.Exists(journal.QuarantinePath))
                throw new IOException("Committed recovery is missing its validated displaced-file quarantine.");

            if (!File.Exists(journal.BackupPath))
            {
                throw new FileNotFoundException("Committed recovery requires the immutable backup, but it is missing.");
            }

            string originalHash = journal.Operation == "restore" ? journal.ExpectedOutputSha256 : journal.SourceSha256;
            if (!Hashing.FixedEqualsHex(Hashing.Sha256File(journal.BackupPath), originalHash))
            {
                throw new IOException("Recovery backup hash is invalid.");
            }

            VerifyStaticTupleUnderLock(journal.GameRoot, build);
            EnsureSupportedMutationPaths(journal.GameRoot, journal.TargetPath, statePath);
            RejectExistingReparseComponents(journal.BackupPath, "immutable backup path");

            if (journal.Operation == "apply")
            {
                var state = new PatchState
                {
                    BuildId = journal.BuildId,
                    GameRoot = journal.GameRoot,
                    TargetPath = journal.TargetPath,
                    BackupPath = journal.BackupPath,
                    OriginalSha256 = journal.SourceSha256,
                    PatchedSha256 = journal.ExpectedOutputSha256,
                    TranslationsSha256 = journal.TranslationsSha256,
                    AppliedAt = journal.CreatedAt,
                    AppliedTranslations = journal.AppliedTranslations
                };
                WriteStateAtomically(statePath, state);
            }
            else if (journal.Operation == "restore")
            {
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            journal.Phase = "Completed";
            PatchJournalStore.Save(statePath, journal);
            if (File.Exists(journal.QuarantinePath)) File.Delete(journal.QuarantinePath);
            PatchJournalStore.Delete(statePath, journal.TransactionId);
            return $"Recovered interrupted {journal.Operation} transaction from the committed file hash.";
        }

        private static CompatibleBuild ValidateRecoveryJournalUnderLock(
            string statePath,
            PatchJournal journal,
            CompatibilityManifest compatibility)
        {
            EnsureSupportedRecoveryOperation(journal.Operation);
            if (journal.Schema != 1 || !LegalJournalPhases.Contains(journal.Phase))
                throw new InvalidDataException("Patch journal schema or phase is not supported.");
            ValidateRecordedPaths(journal.GameRoot, journal.TargetPath, journal.BackupPath, statePath);
            EnsureSupportedMutationPaths(journal.GameRoot, journal.TargetPath, statePath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(journal.TargetPath))
                ?? throw new InvalidDataException("Recovery target has no directory.");
            string fileName = Path.GetFileName(journal.TargetPath);
            string expectedQuarantine = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.displaced");
            string expectedRollback = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.rollback");
            if (!PathEquals(journal.QuarantinePath, expectedQuarantine) || !PathEquals(journal.RollbackPath, expectedRollback))
                throw new InvalidDataException("Patch journal quarantine paths are not transaction-derived.");
            RejectExistingReparseComponents(journal.QuarantinePath, "displaced-file quarantine path");
            RejectExistingReparseComponents(journal.RollbackPath, "rollback preservation path");
            if ((journal.Phase == "DisplacedVerified" || journal.Phase == "PostCommitVerified" || journal.Phase == "StateCommitted" || journal.Phase == "Completed"
                    || journal.Phase == "UnexpectedPreimageDetected" || journal.Phase == "UnexpectedPreimageRestored")
                && string.IsNullOrWhiteSpace(journal.DisplacedSha256))
                throw new InvalidDataException("Patch journal phase requires a recorded displaced-file hash.");

            string originalHash = journal.Operation == "restore" ? journal.ExpectedOutputSha256 : journal.SourceSha256;
            CompatibleBuild build = ResolveRecordedBuild(compatibility, journal.BuildId, originalHash);
            string patchedHash = journal.Operation == "restore" ? journal.SourceSha256 : journal.ExpectedOutputSha256;
            if (string.IsNullOrWhiteSpace(build.ExpectedOutputSha256)
                || !Hashing.FixedEqualsHex(patchedHash, build.ExpectedOutputSha256)
                || string.IsNullOrWhiteSpace(build.TranslationCatalogSha256)
                || !Hashing.FixedEqualsHex(journal.TranslationsSha256, build.TranslationCatalogSha256)
                || journal.AppliedTranslations < build.MinimumAppliedTranslations
                || journal.AppliedTranslations > build.EntryCount)
                throw new InvalidDataException("Patch journal does not match the trusted build output/catalog/count pins.");
            return build;
        }

        private static void ValidateRecoveryStateUnderLock(
            string statePath,
            PatchJournal journal,
            CompatibleBuild build,
            string currentTargetHash)
        {
            bool stateFileExists = File.Exists(statePath);
            PatchState? state = PatchPlanner.TryLoadState(statePath);
            if (stateFileExists && state == null)
                throw new InvalidDataException("Recovery state exists but is unreadable or has an unknown schema.");
            if (state != null)
            {
                ValidateRecordedPaths(state.GameRoot, state.TargetPath, state.BackupPath, statePath);
                if (!string.Equals(state.BuildId, journal.BuildId, StringComparison.Ordinal)
                    || !PathEquals(state.GameRoot, journal.GameRoot)
                    || !PathEquals(state.TargetPath, journal.TargetPath)
                    || !PathEquals(state.BackupPath, journal.BackupPath)
                    || !Hashing.FixedEqualsHex(state.OriginalSha256, build.BaseSha256)
                    || !Hashing.FixedEqualsHex(state.PatchedSha256, build.ExpectedOutputSha256)
                    || !Hashing.FixedEqualsHex(state.TranslationsSha256, journal.TranslationsSha256)
                    || state.AppliedTranslations != journal.AppliedTranslations)
                    throw new InvalidDataException("Recovery state and journal identities differ.");
            }
            bool apply = journal.Operation == "apply";
            if (journal.Phase == "Aborted")
            {
                if (apply && state != null) throw new InvalidDataException("Aborted apply retained patch state.");
                if (!apply && state == null) throw new InvalidDataException("Aborted restore lost patch state.");
                return;
            }
            bool stateTransitionMayHaveRun = journal.Phase == "PostCommitVerified";
            bool stateTransitionRecorded = journal.Phase == "StateCommitted" || journal.Phase == "Completed";
            if (apply && !stateTransitionMayHaveRun && !stateTransitionRecorded && state != null)
                throw new InvalidDataException("Apply recovery has state before the legal state-commit boundary.");
            if (apply && stateTransitionRecorded && state == null)
                throw new InvalidDataException("Apply recovery lost state after StateCommitted.");
            if (!apply && !stateTransitionMayHaveRun && !stateTransitionRecorded && state == null)
                throw new InvalidDataException("Restore recovery lost state before the legal state-removal boundary.");
            if (!apply && stateTransitionRecorded && state != null)
                throw new InvalidDataException("Restore recovery retained state after StateCommitted.");
        }

        internal static void ValidateRecoveryPhaseReadiness(PatchJournal journal, string currentTargetHash)
        {
            bool apply = journal.Operation == "apply"
                || journal.Operation == "runtime-cache-apply"
                || journal.Operation == "runtime-cache-upgrade";
            bool restore = journal.Operation == "restore"
                || journal.Operation == "runtime-cache-restore"
                || journal.Operation == "runtime-cache-upgrade-restore";
            if (!apply && !restore) throw new InvalidDataException("Recovery operation is not supported.");
            bool targetIsSource = Hashing.FixedEqualsHex(currentTargetHash, journal.SourceSha256);
            bool targetIsOutput = Hashing.FixedEqualsHex(currentTargetHash, journal.ExpectedOutputSha256);
            bool hasQuarantine = File.Exists(journal.QuarantinePath);
            bool hasRollback = File.Exists(journal.RollbackPath);

            if (journal.Phase == "Aborted")
            {
                if (!targetIsSource || hasQuarantine || hasRollback)
                    throw new InvalidDataException("Aborted journal has impossible target/quarantine state.");
                return;
            }
            if (journal.Phase == "Completed")
            {
                bool quarantineValid = !hasQuarantine
                    || (!string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                        && Hashing.FixedEqualsHex(Hashing.Sha256File(journal.QuarantinePath), journal.DisplacedSha256)
                        && Hashing.FixedEqualsHex(journal.DisplacedSha256, journal.SourceSha256));
                if (!targetIsOutput || hasRollback || string.IsNullOrWhiteSpace(journal.DisplacedSha256) || !quarantineValid)
                    throw new InvalidDataException("Completed journal has incomplete or invalid preserved transaction state.");
                return;
            }

            if (journal.Phase == "Prepared" || journal.Phase == "StagedVerified"
                || (journal.Phase == "BackupVerified" && apply))
            {
                if (!targetIsSource || hasQuarantine || hasRollback)
                    throw new InvalidDataException("Pre-commit recovery phase has impossible target/quarantine state.");
                return;
            }
            if (journal.Phase == "BackupVerified" && restore)
                throw new InvalidDataException("Restore journals cannot enter BackupVerified.");
            if (journal.Phase == "PreCommitVerified")
            {
                bool beforeReplace = targetIsSource && !hasQuarantine && !hasRollback;
                bool replaceCommittedBeforePhaseSave = targetIsOutput && hasQuarantine && !hasRollback;
                if (!beforeReplace && !replaceCommittedBeforePhaseSave)
                    throw new InvalidDataException("PreCommitVerified journal has impossible target/quarantine state.");
                return;
            }
            if (journal.Phase == "ReplacementCommitted")
            {
                if (!targetIsOutput || !hasQuarantine || hasRollback)
                    throw new InvalidDataException("ReplacementCommitted requires output plus displaced quarantine.");
                return;
            }
            if (journal.Phase == "DisplacedVerified" || journal.Phase == "PostCommitVerified" || journal.Phase == "StateCommitted")
            {
                if (!targetIsOutput || !hasQuarantine || hasRollback
                    || string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.QuarantinePath), journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(journal.DisplacedSha256, journal.SourceSha256))
                    throw new InvalidDataException($"{journal.Phase} requires output and the exact displaced source quarantine.");
                return;
            }
            if (journal.Phase == "UnexpectedPreimageDetected")
            {
                bool beforeRollback = targetIsOutput && hasQuarantine && !hasRollback
                    && !string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    && Hashing.FixedEqualsHex(Hashing.Sha256File(journal.QuarantinePath), journal.DisplacedSha256)
                    && !Hashing.FixedEqualsHex(journal.DisplacedSha256, journal.SourceSha256);
                bool rollbackCommittedBeforePhaseSave = !hasQuarantine && hasRollback
                    && !string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    && Hashing.FixedEqualsHex(currentTargetHash, journal.DisplacedSha256);
                if (!beforeRollback && !rollbackCommittedBeforePhaseSave)
                    throw new InvalidDataException("UnexpectedPreimageDetected has impossible preservation state.");
                return;
            }
            if (journal.Phase == "UnexpectedPreimageRestored")
            {
                if (hasQuarantine || !hasRollback || string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(currentTargetHash, journal.DisplacedSha256))
                    throw new InvalidDataException("UnexpectedPreimageRestored requires the preserved rollback output.");
                return;
            }
            throw new InvalidDataException("Recovery journal phase is not supported.");
        }

        private static void ResolveOrRejectQuarantineUnderLock(string statePath, PatchJournal journal, string currentTargetHash)
        {
            if (File.Exists(journal.RollbackPath))
            {
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(journal.RollbackPath), journal.ExpectedOutputSha256))
                    throw new IOException("Preserved rollback output hash is invalid.");
                if (string.IsNullOrWhiteSpace(journal.DisplacedSha256)
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(journal.TargetPath), journal.DisplacedSha256))
                    throw new IOException("Restored competing target no longer matches the journaled displaced preimage; rollback output was preserved.");
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
                Advance(statePath, journal, "DisplacedVerified");
                return;
            }

            Advance(statePath, journal, "UnexpectedPreimageDetected");
            if (!Hashing.FixedEqualsHex(currentTargetHash, journal.ExpectedOutputSha256))
                throw new IOException("Unexpected displaced preimage and target hashes require manual recovery; both files were preserved.");
            RejectExistingReparseComponents(journal.QuarantinePath, "displaced-file quarantine path");
            RejectExistingReparseComponents(journal.RollbackPath, "rollback preservation path");
            if (File.Exists(journal.RollbackPath))
                throw new IOException("Rollback preservation path appeared during recovery; both existing files were retained.");
            File.Replace(journal.QuarantinePath, journal.TargetPath, journal.RollbackPath, ignoreMetadataErrors: true);
            Advance(statePath, journal, "UnexpectedPreimageRestored");
            throw new IOException("A competing target was atomically restored; staged output is preserved for manual recovery.");
        }

        private static void EnsureSupportedRecoveryOperation(string operation)
        {
            if (operation != "apply" && operation != "restore")
            {
                throw new InvalidDataException($"Unsupported recovery operation: {operation}");
            }
        }

        public static IReadOnlyList<string> FindProcessConflicts(string gameRoot)
        {
            return FindProcessConflicts(gameRoot, blockKnownNamesAnywhere: false);
        }

        public static IReadOnlyList<string> FindRuntimeCacheProcessConflicts()
        {
            return FindProcessConflicts(PatchPlanner.DefaultGameRoot(), blockKnownNamesAnywhere: true);
        }

        private static IReadOnlyList<string> FindProcessConflicts(string gameRoot, bool blockKnownNamesAnywhere)
        {
            string[] knownBlockedNames = { "Invokers", "Invokers Titan Legacy", "UnityCrashHandler64" };
            bool officialRoot = PathEquals(gameRoot, PatchPlanner.DefaultGameRoot());
            string launcherRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Invokers Titan Legacy");
            string updaterRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "invokers-updater");
            var conflicts = new List<string>();
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string processName;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    string? imagePath = null;
                    try
                    {
                        imagePath = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
                    {
                        if (officialRoot && knownBlockedNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                        {
                            conflicts.Add($"{processName} ({process.Id}; image path unavailable)");
                        }

                        continue;
                    }

                    if (imagePath != null && (PathIsInside(gameRoot, imagePath)
                        || (officialRoot && (PathIsInside(launcherRoot, imagePath) || PathIsInside(updaterRoot, imagePath)))))
                    {
                        conflicts.Add($"{processName} ({process.Id}; {imagePath})");
                    }
                    else if (blockKnownNamesAnywhere && knownBlockedNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    {
                        conflicts.Add($"{processName} ({process.Id}; {imagePath ?? "image path unavailable"})");
                    }
                }
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }

            return conflicts;
        }

        private static void EnsureGameStopped(string gameRoot)
        {
            IReadOnlyList<string> conflicts = FindProcessConflicts(gameRoot);
            if (conflicts.Count > 0)
            {
                throw new InvalidOperationException($"Close these game/launcher processes before applying, restoring, or recovering: {string.Join("; ", conflicts.Take(8))}. The patcher never terminates processes itself.");
            }
        }

        private static bool PathEquals(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathIsInside(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string canonicalPath = Path.GetFullPath(path);
            return canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase);
        }

        internal static void EnsureSupportedMutationPaths(string gameRoot, string targetPath, string statePath)
        {
            string fullRoot = Path.GetFullPath(gameRoot);
            string fullStatePath = Path.GetFullPath(statePath);
            string stateRoot = Path.GetDirectoryName(fullStatePath) ?? throw new InvalidDataException("Patch state path has no parent directory.");
            EnsureFixedNtfsVolume(fullRoot, "Game root");
            EnsureFixedNtfsVolume(fullStatePath, "Patch state");

            RejectExistingReparseComponents(fullRoot, "game root");
            RejectExistingReparseComponents(targetPath, "localization target");
            RejectExistingReparseComponents(fullStatePath, "patch state path");
            RejectExistingReparseComponents(Path.Combine(stateRoot, "patcher.lock"), "execution lock path");
            RejectExistingReparseComponents(Path.Combine(stateRoot, "journals"), "transaction journal directory");
            RejectExistingReparseComponents(Path.Combine(stateRoot, "backups"), "immutable backup root");
        }

        private static void EnsureFixedNtfsVolume(string path, string label)
        {
            string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                throw new InvalidDataException($"{label} has no local volume.");
            }

            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{label} writes are supported only on a ready local fixed NTFS volume.");
            }
        }

        internal static void RejectExistingReparseComponents(string path, string label)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException($"{label} has no path root.");
            string current = root;
            string remainder = fullPath.Substring(root.Length);
            foreach (string component in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception exception) when (exception is FileNotFoundException || exception is DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Refusing installation write because {label} traverses a reparse point: {current}");
                }
            }
        }

        internal static void EnsureVerifiedBackup(string sourcePath, string backupPath, string expectedHash)
        {
            RejectExistingReparseComponents(backupPath, "immutable backup path");
            if (File.Exists(backupPath))
            {
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(backupPath), expectedHash))
                {
                    throw new IOException("Existing immutable backup hash is invalid.");
                }

                return;
            }

            string directory = Path.GetDirectoryName(backupPath) ?? throw new InvalidDataException("Backup path has no parent directory.");
            Directory.CreateDirectory(directory);
            RejectExistingReparseComponents(backupPath, "immutable backup path");
            string temp = Path.Combine(directory, $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                WriteDurably(temp, File.ReadAllBytes(sourcePath));
                if (!Hashing.FixedEqualsHex(Hashing.Sha256File(temp), expectedHash))
                {
                    throw new IOException("New backup hash does not match the verified source.");
                }

                File.Move(temp, backupPath);
            }
            catch (IOException) when (File.Exists(backupPath) && Hashing.FixedEqualsHex(Hashing.Sha256File(backupPath), expectedHash))
            {
                // Another verified process won the content-addressed backup race.
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        internal static void Advance(string statePath, PatchJournal journal, string phase)
        {
            journal.Phase = phase;
            PatchJournalStore.Save(statePath, journal);
        }

        private static void EnsurePathInside(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string canonicalPath = Path.GetFullPath(path);
            if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Journal target escapes the recorded game root.");
            }
        }

        private static void ValidateRecordedPaths(string gameRoot, string targetPath, string backupPath, string statePath)
        {
            string expectedTarget = PatchPlanner.ResolveInside(gameRoot, TargetLocaleRelativePath);
            if (!PathEquals(expectedTarget, targetPath))
            {
                throw new InvalidDataException("Recorded target is not the fixed uk_UA localization package under the game root.");
            }

            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? PatchPlanner.DefaultStateRoot();
            EnsurePathInside(Path.Combine(stateRoot, "backups"), backupPath);
        }

        private static void VerifyExactTupleUnderLock(string gameRoot, CompatibleBuild build)
        {
            VerifyStaticTupleUnderLock(gameRoot, build);
            VerifyContainer(
                PatchPlanner.ResolveInside(gameRoot, build.BaseRelativePath),
                build.BaseSha256,
                build.BaseRawSha256,
                build,
                build.BaseLocaleId,
                build.BaseLocaleRevision);
        }

        private static void VerifyStaticTupleUnderLock(string gameRoot, CompatibleBuild build)
        {
            foreach (KnownFile expected in build.GameFiles)
            {
                string path = PatchPlanner.ResolveInside(gameRoot, expected.RelativePath);
                if (!File.Exists(path) || new FileInfo(path).Length != expected.Size
                    || !Hashing.FixedEqualsHex(Hashing.Sha256File(path), expected.Sha256))
                {
                    throw new IOException($"Pinned game file changed after planning: {expected.RelativePath}");
                }
            }

            VerifyContainer(
                PatchPlanner.ResolveInside(gameRoot, build.EnglishRelativePath),
                build.EnglishSha256,
                build.EnglishRawSha256,
                build,
                expectedLocaleId: null,
                expectedLocaleRevision: null);
            string manifestPath = PatchPlanner.ResolveInside(gameRoot, build.ManifestRelativePath);
            if (!File.Exists(manifestPath) || !Hashing.FixedEqualsHex(Hashing.Sha256File(manifestPath), build.ManifestSha256))
            {
                throw new IOException("The i18n manifest changed after planning.");
            }
        }

        private static CompatibleBuild ResolveRecordedBuild(CompatibilityManifest compatibility, string buildId, string originalSha256)
        {
            CompatibleBuild[] matches = compatibility.Builds
                .Where(candidate => string.Equals(candidate.Id, buildId, StringComparison.Ordinal)
                    && Hashing.FixedEqualsHex(candidate.BaseSha256, originalSha256))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException("Patch state does not match exactly one trusted compatibility entry; refusing restore or recovery.");
            }

            return matches[0];
        }

        private static void VerifyContainer(
            string path,
            string expectedContainerSha256,
            string expectedRawSha256,
            CompatibleBuild build,
            uint? expectedLocaleId,
            uint? expectedLocaleRevision)
        {
            if (!File.Exists(path) || !Hashing.FixedEqualsHex(Hashing.Sha256File(path), expectedContainerSha256))
            {
                throw new IOException($"Localization container changed after planning: {Path.GetFileName(path)}");
            }

            byte[] raw = Loc1Codec.ReadContainer(path);
            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(raw), expectedRawSha256))
            {
                throw new InvalidDataException($"Raw LOC1 hash changed after planning: {Path.GetFileName(path)}");
            }

            Loc1Document document = Loc1Codec.Parse(raw);
            if (!string.Equals(document.ContentGuid, build.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(document.ContentVersion, build.ContentVersion, StringComparison.Ordinal)
                || document.Entries.Count != build.EntryCount
                || (expectedLocaleId.HasValue && document.LocaleId != expectedLocaleId.Value)
                || (expectedLocaleRevision.HasValue && document.LocaleRevision != expectedLocaleRevision.Value))
            {
                throw new InvalidDataException($"LOC1 identity changed after planning: {Path.GetFileName(path)}");
            }
        }

        internal static void AtomicReplacePreservingPreimage(
            string tempPath,
            string targetPath,
            string expectedPreimageSha256,
            string statePath,
            PatchJournal journal)
        {
            RejectExistingReparseComponents(tempPath, "staged localization path");
            RejectExistingReparseComponents(targetPath, "localization target");
            RejectExistingReparseComponents(journal.QuarantinePath, "displaced-file quarantine path");
            RejectExistingReparseComponents(journal.RollbackPath, "rollback preservation path");
            if (!File.Exists(targetPath)) throw new FileNotFoundException("Atomic replacement target disappeared.", targetPath);
            if (File.Exists(journal.QuarantinePath) || File.Exists(journal.RollbackPath))
                throw new IOException("Unique transaction quarantine paths already exist.");

            MutationTestHooks.InvokeBeforeAtomicReplace(targetPath);
            File.Replace(tempPath, targetPath, journal.QuarantinePath, ignoreMetadataErrors: true);
            Advance(statePath, journal, "ReplacementCommitted");
            if (!File.Exists(journal.QuarantinePath)) throw new IOException("Atomic replacement did not preserve the displaced target.");
            journal.DisplacedSha256 = Hashing.Sha256File(journal.QuarantinePath);
            if (!Hashing.FixedEqualsHex(journal.DisplacedSha256, expectedPreimageSha256))
            {
                Advance(statePath, journal, "UnexpectedPreimageDetected");
                RejectExistingReparseComponents(journal.QuarantinePath, "displaced-file quarantine path");
                RejectExistingReparseComponents(journal.RollbackPath, "rollback preservation path");
                if (File.Exists(journal.RollbackPath))
                    throw new IOException("Rollback preservation path appeared during commit; both existing files were retained.");
                File.Replace(journal.QuarantinePath, targetPath, journal.RollbackPath, ignoreMetadataErrors: true);
                Advance(statePath, journal, "UnexpectedPreimageRestored");
                throw new IOException("Target changed at atomic commit; competing bytes were restored and the staged output was preserved for recovery.");
            }

            Advance(statePath, journal, "DisplacedVerified");
        }

        private static void InitializeQuarantinePaths(PatchJournal journal)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(journal.TargetPath))
                ?? throw new InvalidDataException("Mutation target has no directory.");
            string fileName = Path.GetFileName(journal.TargetPath);
            journal.QuarantinePath = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.displaced");
            journal.RollbackPath = Path.Combine(directory, $".{fileName}.{journal.TransactionId}.rollback");
        }

        internal static void WriteDurably(string path, byte[] content)
        {
            RejectExistingReparseComponents(path, "durable output path");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
            stream.Write(content, 0, content.Length);
            stream.Flush(flushToDisk: true);
        }

        internal static void WriteStateAtomically(string statePath, PatchState state)
        {
            string fullPath = Path.GetFullPath(statePath);
            RejectExistingReparseComponents(fullPath, "patch state path");
            string directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("State path has no parent directory.");
            Directory.CreateDirectory(directory);
            RejectExistingReparseComponents(fullPath, "patch state path");
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, StateJsonOptions));
            using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }

        private static string Sanitize(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
                .TrimEnd(' ', '.');
            if (safe.Length == 0 || safe == "." || safe == "..") safe = "_";
            if (safe.Length > 64) safe = safe.Substring(0, 64).TrimEnd(' ', '.');
            if (safe.Length == 0 || safe == "." || safe == "..") safe = "_";
            return $"{safe}-{Hashing.Sha256Text(value).Substring(0, 12)}";
        }
    }
}
