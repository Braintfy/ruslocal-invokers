using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InvokersRu.Cli
{
    internal sealed class RuntimeUpdateResolution
    {
        public required RuntimeCacheCompatibility Profile { get; init; }
        public required RuntimeCacheInspection Inspection { get; init; }
        public required string CatalogPath { get; init; }
        public SignedUpdateBundle? Bundle { get; init; }
        public VerifiedSignedUpdate? ChannelAuthority { get; init; }
        public RuntimeCacheCompatibility? InstalledProfile { get; init; }
        public RuntimeCacheInspection? InstalledInspection { get; init; }
        public bool TranslationUpdateAvailable { get; init; }
        public bool EquivalentCatalogMetadataUpdate { get; init; }
        public string Source { get; init; } = "embedded";
        public string? LocalProblem { get; init; }
        public string? RemoteProblem { get; init; }
        public bool RemoteProblemBlocksApply { get; init; }
    }

    internal static class RuntimeUpdateAuthorization
    {
        public static bool CanApply(RuntimeUpdateResolution resolution, DateTimeOffset nowUtc)
        {
            ArgumentNullException.ThrowIfNull(resolution);
            SignedUpdateBundle? bundle = resolution.Bundle;
            VerifiedSignedUpdate? authority = resolution.ChannelAuthority;
            RuntimeCacheCompatibility profile = resolution.Profile;
            PatchState? state = resolution.Inspection.State;
            return CanApply(
                !string.IsNullOrWhiteSpace(resolution.RemoteProblem),
                authority != null,
                bundle != null,
                authority?.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld,
                bundle?.Update.IsExpiredAt(nowUtc) == true,
                bundle?.Source == SignedUpdateBundleSource.LastKnownGood,
                profile.Id,
                profile.BaseSha256,
                profile.ExpectedOutputSha256,
                profile.TranslationCatalogSha256,
                profile.ExpectedAppliedTranslations,
                state?.BuildId,
                state?.OriginalSha256,
                state?.PatchedSha256,
                state?.TranslationsSha256,
                state?.AppliedTranslations,
                resolution.RemoteProblemBlocksApply);
        }

        internal static bool CanApply(
            bool hasRemoteProblem,
            bool hasChannelAuthority,
            bool hasBundle,
            bool channelRequiresNewerPatcher,
            bool selectedBundleExpired,
            bool selectedBundleIsLastKnownGood,
            string profileId,
            string profileBaseSha256,
            string? profileOutputSha256,
            string? profileCatalogSha256,
            int profileAppliedTranslations,
            string? installedStateBuildId,
            string? installedStateOriginalSha256,
            string? installedStatePatchedSha256,
            string? installedStateCatalogSha256,
            int? installedStateAppliedTranslations,
            bool remoteProblemBlocksApply = false)
        {
            if (remoteProblemBlocksApply) return false;
            if (!hasChannelAuthority) return !hasBundle;
            if (!hasBundle || channelRequiresNewerPatcher) return false;
            bool exactInstalledArtifact = !string.IsNullOrWhiteSpace(installedStateBuildId)
                && string.Equals(installedStateBuildId, profileId, StringComparison.Ordinal)
                && Hashing.FixedEqualsHex(installedStateOriginalSha256!, profileBaseSha256)
                && !string.IsNullOrWhiteSpace(profileOutputSha256)
                && Hashing.FixedEqualsHex(installedStatePatchedSha256!, profileOutputSha256)
                && !string.IsNullOrWhiteSpace(profileCatalogSha256)
                && Hashing.FixedEqualsHex(installedStateCatalogSha256!, profileCatalogSha256)
                && installedStateAppliedTranslations == profileAppliedTranslations;
            if (selectedBundleIsLastKnownGood) return exactInstalledArtifact;
            return !selectedBundleExpired;
        }

        public static bool CanRestoreOrRecover(RuntimeUpdateResolution resolution)
        {
            ArgumentNullException.ThrowIfNull(resolution);
            RuntimeCacheInspection inspection = resolution.InstalledInspection ?? resolution.Inspection;
            if (resolution.Inspection.Status == InstallationStatus.RecoveryRequired)
            {
                return resolution.InstalledInspection?.Status == InstallationStatus.RecoveryRequired;
            }
            return CanRestoreOrRecover(inspection.Status is InstallationStatus.PatchedByThisTool
                or InstallationStatus.PatchSupersededByCatalogUpdate
                or InstallationStatus.RecoveryRequired);
        }

        internal static bool CanRestoreOrRecover(bool hasExactRestorableInspection) => hasExactRestorableInspection;

        internal static bool CanUseSelectedCatalogForApply(bool selectedCatalogExact) => selectedCatalogExact;
    }

    internal static class RuntimeUpdateResolver
    {
        public static RuntimeUpdateResolution Resolve(
            string cacheRoot,
            string statePath,
            RuntimeCacheCompatibility embeddedProfile,
            string embeddedCatalogPath,
            SignedUpdateCoordinator? coordinator,
            string? remoteProblem = null,
            bool remoteProblemBlocksApply = false)
        {
            RuntimeCacheInspection embeddedInspection = RuntimeCacheService.Inspect(cacheRoot, embeddedProfile, statePath);
            bool embeddedInstalled = embeddedInspection.Status == InstallationStatus.PatchedByThisTool
                || (embeddedInspection.Status == InstallationStatus.RecoveryRequired
                    && embeddedInspection.Journal != null
                    && IsExactJournal(embeddedProfile, embeddedInspection.Journal)
                    && RuntimeCacheService.TryAuthenticateRecovery(
                        cacheRoot,
                        statePath,
                        embeddedProfile,
                        out _));
            var embedded = new RuntimeUpdateResolution
            {
                Profile = embeddedProfile,
                Inspection = embeddedInspection,
                CatalogPath = embeddedCatalogPath,
                InstalledProfile = embeddedInstalled ? embeddedProfile : null,
                InstalledInspection = embeddedInstalled ? embeddedInspection : null,
                Source = "embedded",
                RemoteProblem = remoteProblem,
                RemoteProblemBlocksApply = remoteProblemBlocksApply
            };
            RuntimeUpdateResolution? embeddedHistorical = TryResolveEmbeddedHistoricalAuthority(
                cacheRoot,
                statePath,
                embeddedProfile,
                embeddedCatalogPath,
                remoteProblem,
                remoteProblemBlocksApply);
            if (coordinator == null)
            {
                if (embeddedHistorical != null) return embeddedHistorical;
                RuntimeUpdateResolution? compatible = TryResolveCompatibleRevision(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator: null,
                    bundle: null,
                    channelAuthority: null,
                    remoteProblem,
                    remoteProblemBlocksApply);
                if (compatible != null) return compatible;
                if (embeddedInspection.Status == InstallationStatus.RecoveryRequired && !embeddedInstalled)
                {
                    return WithUnauthenticatedJournal(
                        embedded,
                        bundle: null,
                        channelAuthority: null,
                        "The active transaction journal does not match the embedded recovery profile.");
                }
                return embedded;
            }

            SignedUpdateBundle? bundle = coordinator.LoadBestAvailable();
            VerifiedSignedUpdate? channelAuthority = bundle?.Authority ?? coordinator.LoadNewestAccepted();
            VerifiedSignedUpdate? selectedUpdate = bundle?.Update ?? channelAuthority;
            if (embeddedHistorical?.Inspection.Status == InstallationStatus.RecoveryRequired)
                return WithChannelAuthority(embeddedHistorical, channelAuthority);
            if (selectedUpdate == null)
            {
                // A brand-new offline installation has no authenticated channel head yet. The embedded
                // profile and catalog remain a complete trusted bootstrap authority, including for a
                // structurally compatible future revision. Once any authenticated head exists, its
                // patcher policy and catalog availability are handled below and cannot be bypassed here.
                if (embeddedHistorical != null)
                    return WithChannelAuthority(embeddedHistorical, channelAuthority: null);
                RuntimeUpdateResolution? compatible = TryResolveCompatibleRevision(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    bundle: null,
                    channelAuthority: null,
                    remoteProblem,
                    remoteProblemBlocksApply);
                if (compatible != null) return compatible;
                if (embeddedInspection.Status == InstallationStatus.RecoveryRequired && !embeddedInstalled)
                {
                    return WithUnauthenticatedJournal(
                        embedded,
                        bundle: null,
                        channelAuthority: null,
                        "The active transaction journal does not match any authenticated recovery profile.");
                }
                return embedded;
            }

            PatchJournal? journal = PatchJournalStore.FindActive(statePath);
            if (journal != null)
            {
                // Recovery authority is pinned by the journal that already exists. A newly published
                // exact profile must never pre-empt an interrupted compatible transaction. Resolve it
                // before reading the live target because a legal replacement phase can temporarily leave
                // that one fixed file absent while the immutable backup remains authoritative.
                RuntimeUpdateResolution? compatibleRecovery = TryResolveCompatibleRevision(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    bundle,
                    channelAuthority,
                    remoteProblem,
                    remoteProblemBlocksApply);
                if (compatibleRecovery?.Inspection.Status == InstallationStatus.RecoveryRequired)
                    return compatibleRecovery;

                if (embeddedInstalled && embeddedInspection.Status == InstallationStatus.RecoveryRequired)
                    return WithChannelAuthority(embedded, channelAuthority);

                RuntimeUpdateResolution? exactHistoryRecovery = TryResolveExactJournalRecovery(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    journal,
                    channelAuthority,
                    remoteProblem,
                    remoteProblemBlocksApply);
                if (exactHistoryRecovery != null) return exactHistoryRecovery;

                // A parseable local journal is not a network/catalog selection problem. If no exact
                // embedded, signed-history, or compatible profile authenticates its pinned transaction,
                // keep it as a local inconsistent state and never expose a generic recovery button.
                return WithUnauthenticatedJournal(
                    embedded,
                    bundle: null,
                    channelAuthority,
                    "The active transaction journal has no unique authenticated recovery profile or preserved phase artifact.");
            }

            if (!TryReadObserved(cacheRoot, out RuntimeCacheCompatibility? directObserved, out Loc1Document? target))
                return WithUnavailableSignedData(
                    embedded,
                    bundle,
                    channelAuthority!,
                    "The fixed runtime-cache tuple is missing or unreadable.");

            PatchState? state = PatchPlanner.TryLoadState(statePath);

            RuntimeCacheCompatibility? remoteProfile = null;
            try
            {
                SignedUpdateRuntimeProfileAdapter.TrySelectExact(
                    selectedUpdate.Manifest,
                    directObserved!,
                    target!,
                    out remoteProfile);
            }
            catch (InvalidDataException)
            {
                throw;
            }

            if (remoteProfile == null && state != null)
            {
                string fixedTargetPath = RuntimeCacheService.ResolveTuplePaths(cacheRoot).Target;
                string targetSha256 = BoundedArtifactReader.Sha256File(
                    fixedTargetPath,
                    BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                    "installed runtime-cache target");
                if (Hashing.FixedEqualsHex(targetSha256, state.PatchedSha256)
                    && TryResolveStateBackupPath(
                        Path.GetFullPath(cacheRoot),
                        fixedTargetPath,
                        statePath,
                        state,
                        out _))
                {
                    // The live target is a recorded patch, so exact profile selection must compare the
                    // signed official-base pin rather than the patched output hash.
                    RuntimeCacheCompatibility officialObserved = CloneObservedWithOfficialBase(
                        directObserved!,
                        state.OriginalSha256);
                    SignedUpdateRuntimeProfileAdapter.TrySelectExact(
                        selectedUpdate.Manifest,
                        officialObserved,
                        target!,
                        out remoteProfile);
                }
            }

            if (remoteProfile == null)
            {
                RuntimeUpdateResolution? compatible = TryResolveCompatibleRevision(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    bundle,
                    channelAuthority,
                    remoteProblem,
                    remoteProblemBlocksApply);
                if (compatible != null) return compatible;
            }

            RuntimeUpdateResolution? compatibleInstalled = null;
            if (remoteProfile != null && state != null && journal == null)
            {
                compatibleInstalled = TryResolveCompatibleRevision(
                    cacheRoot,
                    statePath,
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    bundle,
                    channelAuthority,
                    remoteProblem,
                    remoteProblemBlocksApply);
            }
            RuntimeCacheCompatibility? installedProfile = compatibleInstalled?.InstalledProfile
                ?? embeddedHistorical?.InstalledProfile
                ?? embedded.InstalledProfile;
            RuntimeCacheInspection? installedInspection = compatibleInstalled?.InstalledInspection
                ?? embeddedHistorical?.InstalledInspection
                ?? embedded.InstalledInspection;
            if (remoteProfile == null
                && state != null
                && embeddedInspection.Status == InstallationStatus.PatchSupersededByCatalogUpdate)
            {
                RuntimeCacheCompatibility patchedObserved = CloneObservedWithOfficialBase(
                    directObserved!,
                    state.OriginalSha256);
                SignedUpdateRuntimeProfileAdapter.TrySelectExact(
                    selectedUpdate.Manifest,
                    patchedObserved,
                    target!,
                    out remoteProfile);
            }
            if (state != null && installedProfile == null)
            {
                if (IsExactInstalledState(embeddedProfile, state))
                {
                    RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(cacheRoot, embeddedProfile, statePath);
                    if (inspection.Status == InstallationStatus.PatchedByThisTool)
                    {
                        installedProfile = embeddedProfile;
                        installedInspection = inspection;
                    }
                }

            }

            if (installedProfile == null && (state != null || journal != null))
            {
                string recordedBuildId = state?.BuildId ?? journal!.BuildId;
                foreach (VerifiedSignedUpdate history in coordinator.LoadVerifiedHistory().Reverse())
                {
                    foreach (VerifiedSignedUpdateCompatibilityProfile signed in history.Manifest.Compatibility)
                    {
                        if (!string.Equals(signed.ProfileId, recordedBuildId, StringComparison.Ordinal)) continue;
                        RuntimeCacheCompatibility candidate;
                        try
                        {
                            candidate = SignedUpdateRuntimeProfileAdapter.AdaptExact(history.Manifest, signed, target!);
                            candidate = AttachEmbeddedMigrationAllowlist(embeddedProfile, candidate);
                        }
                        catch (InvalidDataException)
                        {
                            continue;
                        }

                        RuntimeCacheInspection inspection;
                        if (journal != null)
                        {
                            if (!IsExactJournal(candidate, journal)
                                || !RuntimeCacheService.TryAuthenticateRecovery(
                                    cacheRoot,
                                    statePath,
                                    candidate,
                                    out _))
                            {
                                continue;
                            }

                            inspection = RuntimeCacheService.Inspect(cacheRoot, candidate, statePath);
                            if (inspection.Status != InstallationStatus.RecoveryRequired) continue;
                        }
                        else
                        {
                            if (state == null || !IsExactInstalledState(candidate, state)) continue;
                            inspection = RuntimeCacheService.Inspect(cacheRoot, candidate, statePath);
                            if (inspection.Status != InstallationStatus.PatchedByThisTool) continue;
                        }

                        installedProfile = candidate;
                        installedInspection = inspection;
                        break;
                    }

                    if (installedProfile != null) break;
                }
            }

            if (journal != null && installedInspection?.Status != InstallationStatus.RecoveryRequired)
            {
                return WithUnauthenticatedJournal(
                    embedded,
                    bundle,
                    channelAuthority,
                    "The active transaction journal has no unique authenticated embedded, signed-history, or compatible-revision recovery profile.");
            }

            if (installedInspection?.Status == InstallationStatus.RecoveryRequired)
            {
                return new RuntimeUpdateResolution
                {
                    Profile = installedProfile!,
                    Inspection = installedInspection,
                    CatalogPath = embeddedCatalogPath,
                    Bundle = null,
                    ChannelAuthority = channelAuthority,
                    InstalledProfile = installedProfile,
                    InstalledInspection = installedInspection,
                    TranslationUpdateAvailable = false,
                    // Recovery authenticates the historical profile through state/journal history, but the
                    // catalog path in this diagnostic plan is still the embedded bootstrap catalog. Do not
                    // mislabel that file as a current remote exact-profile selection.
                    Source = "embedded",
                    RemoteProblem = remoteProblem,
                    RemoteProblemBlocksApply = remoteProblemBlocksApply
                };
            }

            if (state != null)
            {
                if (remoteProfile == null && installedProfile != null && installedInspection != null)
                {
                    RuntimeCacheCompatibility patchedObserved = CloneObservedWithOfficialBase(
                        directObserved!,
                        installedInspection.State?.OriginalSha256
                            ?? throw new InvalidDataException("Validated patch state lost its official source hash."));
                    SignedUpdateRuntimeProfileAdapter.TrySelectExact(
                        selectedUpdate.Manifest,
                        patchedObserved,
                        target!,
                        out remoteProfile);
                }
            }

            if (remoteProfile == null)
                return WithUnavailableSignedData(
                    embedded,
                    bundle,
                    channelAuthority!,
                    "Current authenticated translation data cannot materialize a supported exact or compatible-revision profile for the observed tuple.",
                    installedProfile,
                    installedInspection);
            remoteProfile = AttachEmbeddedMigrationAllowlist(embeddedProfile, remoteProfile);
            RuntimeCacheInspection remoteInspection = RuntimeCacheService.Inspect(cacheRoot, remoteProfile, statePath);
            bool historicalUpdateAvailable = installedProfile != null
                && installedInspection?.Status == InstallationStatus.PatchedByThisTool
                && !SameInstalledArtifact(installedProfile, remoteProfile);
            bool catalogSupersededUpdateAvailable = remoteInspection.Status == InstallationStatus.PatchSupersededByCatalogUpdate;
            bool updateAvailable = historicalUpdateAvailable || catalogSupersededUpdateAvailable;
            if (historicalUpdateAvailable)
            {
                // Inspect is expected to be inconsistent against the new output pins until the old exact
                // patch is restored. The write command performs restore then a fresh exact apply.
                remoteInspection = new RuntimeCacheInspection
                {
                    Status = InstallationStatus.PatchedByThisTool,
                    Message = "A newer signed translation artifact is available for this exact game build.",
                    CacheRoot = installedInspection!.CacheRoot,
                    EnglishPath = installedInspection.EnglishPath,
                    TargetPath = installedInspection.TargetPath,
                    StampPath = installedInspection.StampPath,
                    EnglishSha256 = installedInspection.EnglishSha256,
                    BaseSha256 = installedInspection.BaseSha256,
                    StampSha256 = installedInspection.StampSha256,
                    StampValue = installedInspection.StampValue,
                    EnglishContentVersion = installedInspection.EnglishContentVersion,
                    BaseContentVersion = installedInspection.BaseContentVersion,
                    Profile = remoteProfile,
                    State = installedInspection.State,
                    Journal = installedInspection.Journal
                };
            }

            return new RuntimeUpdateResolution
            {
                Profile = remoteProfile,
                Inspection = remoteInspection,
                CatalogPath = bundle?.CatalogPath ?? string.Empty,
                Bundle = bundle,
                ChannelAuthority = channelAuthority,
                InstalledProfile = installedProfile,
                InstalledInspection = installedInspection,
                TranslationUpdateAvailable = updateAvailable,
                Source = bundle?.Source.ToString() ?? "ChannelHead",
                RemoteProblem = remoteProblem,
                RemoteProblemBlocksApply = remoteProblemBlocksApply
            };
        }

        private static RuntimeUpdateResolution? TryResolveEmbeddedHistoricalAuthority(
            string cacheRoot,
            string statePath,
            RuntimeCacheCompatibility currentProfile,
            string currentCatalogPath,
            string? remoteProblem,
            bool remoteProblemBlocksApply)
        {
            PatchState? state = PatchPlanner.TryLoadState(statePath);
            PatchJournal? journal = PatchJournalStore.FindActive(statePath);
            if (state == null && journal == null) return null;

            foreach (RuntimeCacheCompatibility historical in EmbeddedRuntimeCacheHistory.CreateProfiles())
            {
                if (SameInstalledArtifact(historical, currentProfile)) continue;
                if (journal != null)
                {
                    if (!IsExactJournal(historical, journal)
                        || !RuntimeCacheService.TryAuthenticateRecovery(cacheRoot, statePath, historical, out _))
                        continue;
                    RuntimeCacheInspection recovery = RuntimeCacheService.Inspect(cacheRoot, historical, statePath);
                    if (recovery.Status != InstallationStatus.RecoveryRequired) continue;
                    return HistoricalResolution(historical, recovery, currentCatalogPath, remoteProblem,
                        remoteProblemBlocksApply);
                }

                if (state == null || !IsExactInstalledState(historical, state)) continue;
                RuntimeCacheInspection installed = RuntimeCacheService.Inspect(cacheRoot, historical, statePath);
                if (installed.Status == InstallationStatus.PatchedByThisTool)
                    return HistoricalResolution(historical, installed, currentCatalogPath, remoteProblem,
                        remoteProblemBlocksApply);

                RuntimeCacheInspection officialUpdate = RuntimeCacheService.Inspect(
                    cacheRoot,
                    currentProfile,
                    statePath,
                    historical);
                if (officialUpdate.Status == InstallationStatus.PatchSupersededByOfficialUpdate)
                {
                    return new RuntimeUpdateResolution
                    {
                        Profile = currentProfile,
                        Inspection = officialUpdate,
                        CatalogPath = currentCatalogPath,
                        Bundle = null,
                        ChannelAuthority = null,
                        InstalledProfile = historical,
                        InstalledInspection = null,
                        Source = "embedded",
                        RemoteProblem = remoteProblem,
                        RemoteProblemBlocksApply = remoteProblemBlocksApply
                    };
                }
            }

            return null;
        }

        private static RuntimeUpdateResolution HistoricalResolution(
            RuntimeCacheCompatibility historical,
            RuntimeCacheInspection inspection,
            string currentCatalogPath,
            string? remoteProblem,
            bool remoteProblemBlocksApply)
        {
            return new RuntimeUpdateResolution
            {
                Profile = historical,
                Inspection = inspection,
                CatalogPath = currentCatalogPath,
                Bundle = null,
                ChannelAuthority = null,
                InstalledProfile = historical,
                InstalledInspection = inspection,
                Source = "embedded",
                RemoteProblem = remoteProblem,
                RemoteProblemBlocksApply = remoteProblemBlocksApply
            };
        }

        private static RuntimeUpdateResolution WithChannelAuthority(
            RuntimeUpdateResolution source,
            VerifiedSignedUpdate? channelAuthority)
        {
            return new RuntimeUpdateResolution
            {
                Profile = source.Profile,
                Inspection = source.Inspection,
                CatalogPath = source.CatalogPath,
                Bundle = null,
                ChannelAuthority = channelAuthority,
                InstalledProfile = source.InstalledProfile,
                InstalledInspection = source.InstalledInspection,
                TranslationUpdateAvailable = source.TranslationUpdateAvailable,
                EquivalentCatalogMetadataUpdate = source.EquivalentCatalogMetadataUpdate,
                Source = source.Source,
                LocalProblem = source.LocalProblem,
                RemoteProblem = source.RemoteProblem,
                RemoteProblemBlocksApply = source.RemoteProblemBlocksApply
            };
        }

        private static RuntimeUpdateResolution WithUnavailableSignedData(
            RuntimeUpdateResolution embedded,
            SignedUpdateBundle? bundle,
            VerifiedSignedUpdate channelAuthority,
            string problem,
            RuntimeCacheCompatibility? installedProfile = null,
            RuntimeCacheInspection? installedInspection = null)
        {
            RuntimeCacheCompatibility selectedProfile = installedProfile ?? embedded.Profile;
            RuntimeCacheInspection selectedInspection = installedInspection ?? embedded.Inspection;
            return new RuntimeUpdateResolution
            {
                // A historical signed profile remains the only authenticated authority for restore.
                // Surface that exact profile/inspection to the JSON plan even when the newest manifest
                // has no installable profile for the currently observed game build.
                Profile = selectedProfile,
                Inspection = selectedInspection,
                CatalogPath = embedded.CatalogPath,
                // The current head did not authorize this profile/catalog. Keeping its bundle here would
                // falsely turn an embedded diagnostic/restoration profile into a selected signed write and
                // could record an unrelated LKG receipt after apply.
                Bundle = null,
                ChannelAuthority = channelAuthority,
                InstalledProfile = installedProfile ?? embedded.InstalledProfile,
                InstalledInspection = installedInspection ?? embedded.InstalledInspection,
                TranslationUpdateAvailable = false,
                Source = embedded.Source,
                RemoteProblem = problem,
                // An authenticated current head exists but did not authorize the selected embedded
                // profile/catalog.  Never attach an unrelated bundle receipt to an embedded write.
                RemoteProblemBlocksApply = true
            };
        }

        private static bool TryReadObserved(
            string cacheRoot,
            out RuntimeCacheCompatibility? observed,
            out Loc1Document? target)
        {
            observed = null;
            target = null;
            (string english, string basePath, string stamp) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
            if (!File.Exists(english) || !File.Exists(basePath) || !File.Exists(stamp)) return false;
            observed = RuntimeCacheService.DescribeTuple(
                english,
                basePath,
                stamp,
                "observed-runtime-cache",
                out Loc1Document observedTarget);
            target = observedTarget;
            return true;
        }

        private static RuntimeUpdateResolution WithUnauthenticatedJournal(
            RuntimeUpdateResolution embedded,
            SignedUpdateBundle? bundle,
            VerifiedSignedUpdate? channelAuthority,
            string problem)
        {
            RuntimeCacheInspection source = embedded.Inspection;
            var refused = new RuntimeCacheInspection
            {
                Status = InstallationStatus.InconsistentState,
                Message = problem,
                CacheRoot = source.CacheRoot,
                EnglishPath = source.EnglishPath,
                TargetPath = source.TargetPath,
                StampPath = source.StampPath,
                EnglishSha256 = source.EnglishSha256,
                BaseSha256 = source.BaseSha256,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                EnglishContentVersion = source.EnglishContentVersion,
                BaseContentVersion = source.BaseContentVersion,
                EnglishFormatVersion = source.EnglishFormatVersion,
                BaseFormatVersion = source.BaseFormatVersion,
                EnglishContentGuid = source.EnglishContentGuid,
                BaseContentGuid = source.BaseContentGuid,
                EnglishLocaleId = source.EnglishLocaleId,
                EnglishLocaleRevision = source.EnglishLocaleRevision,
                EnglishReleaseRevision = source.EnglishReleaseRevision,
                BaseLocaleId = source.BaseLocaleId,
                BaseLocaleRevision = source.BaseLocaleRevision,
                BaseReleaseRevision = source.BaseReleaseRevision,
                EntryCount = source.EntryCount,
                OrderedKeysetSha256 = source.OrderedKeysetSha256,
                Profile = embedded.Profile,
                State = source.State,
                Journal = null
            };
            return new RuntimeUpdateResolution
            {
                Profile = embedded.Profile,
                Inspection = refused,
                CatalogPath = embedded.CatalogPath,
                Bundle = null,
                ChannelAuthority = channelAuthority,
                Source = embedded.Source,
                LocalProblem = "journal-authentication",
                RemoteProblem = null,
                RemoteProblemBlocksApply = false
            };
        }

        private static RuntimeUpdateResolution? TryResolveCompatibleRevision(
            string cacheRoot,
            string statePath,
            RuntimeCacheCompatibility embeddedProfile,
            string embeddedCatalogPath,
            SignedUpdateCoordinator? coordinator,
            SignedUpdateBundle? bundle,
            VerifiedSignedUpdate? channelAuthority,
            string? remoteProblem,
            bool remoteProblemBlocksApply)
        {
            try
            {
                string root = Path.GetFullPath(cacheRoot);
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(root);
                if (!File.Exists(englishPath) || !File.Exists(stampPath)) return null;
                if (File.Exists(statePath) && PatchPlanner.TryLoadState(statePath) == null) return null;

                PatchState? state = PatchPlanner.TryLoadState(statePath);
                PatchJournal? journal = PatchJournalStore.FindActive(statePath);
                if (!File.Exists(targetPath) && journal == null) return null;
                string targetSha256 = File.Exists(targetPath)
                    ? BoundedArtifactReader.Sha256File(
                        targetPath,
                        BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                        "compatible runtime-cache target")
                    : string.Empty;
                string basePath = targetPath;
                if (journal != null)
                {
                    if (!TryResolveJournalBasePath(root, targetPath, statePath, journal, targetSha256, out basePath))
                        return null;
                }
                else if (state != null && Hashing.FixedEqualsHex(targetSha256, state.PatchedSha256))
                {
                    if (!TryResolveStateBackupPath(root, targetPath, statePath, state, out basePath))
                        return null;
                }

                string observedContentGuid = Loc1Codec.Parse(BoundedArtifactReader.ReadRuntimeLoc1(
                    basePath,
                    "compatible-revision observed Ukrainian LOC1")).ContentGuid;
                List<CompatibleCatalogCandidate> catalogs = LoadCompatibleCatalogCandidates(
                    embeddedProfile,
                    embeddedCatalogPath,
                    coordinator,
                    bundle,
                    observedContentGuid);
                var builds = new List<(CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build)>();
                foreach (CompatibleCatalogCandidate catalog in catalogs)
                {
                    try
                    {
                        byte[] bytes = BoundedArtifactReader.ReadCatalog(
                            catalog.Path,
                            catalog.Sha256,
                            "compatible-revision catalog");
                        CompatibleRevisionProfileBuild build = CompatibleRevisionProfileBuilder.BuildForDiagnostics(
                            englishPath,
                            basePath,
                            stampPath,
                            catalog.TrustedContentGuid,
                            bytes,
                            catalog.Sha256,
                            catalog.TranslationPolicy);
                        int existingIndex = builds.FindIndex(item =>
                            SameMaterializedProfileIdentity(item.Build.Profile, build.Profile));
                        if (existingIndex < 0)
                        {
                            builds.Add((catalog, build));
                        }
                        else if (CatalogAuthorityStrength(catalog)
                            > CatalogAuthorityStrength(builds[existingIndex].Catalog))
                        {
                            // Equivalent materialization keeps the strongest deterministic provenance;
                            // result must not depend on candidate insertion order.
                            builds[existingIndex] = (catalog, build);
                        }
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or Loc1FormatException)
                    {
                        // A catalog that has no exact current source+hint rows or a tuple outside the
                        // conservative family is simply ineligible. Another independently trusted catalog
                        // may still authenticate an installed transaction for restore/recovery.
                    }
                }

                if (builds.Count == 0) return null;

                if (journal != null)
                {
                    CompatibleRecoveryCandidate[] authenticated = EnumerateCompatibleRecoveryCandidates(builds, journal)
                        .Where(item => IsExactJournal(item.Profile, journal)
                            && RuntimeCacheService.TryAuthenticateRecovery(root, statePath, item.Profile, out _))
                        .ToArray();
                    CompatibleRecoveryCandidate[] matches = authenticated
                        .GroupBy(item => RecoveryAuthorityKey(item.Profile, journal), StringComparer.Ordinal)
                        .Select(group => group
                            .OrderByDescending(item => CatalogAuthorityStrength(item.Catalog))
                            .ThenBy(item => item.Catalog.Source, StringComparer.Ordinal)
                            .ThenBy(item => item.Profile.TranslationPolicy, StringComparer.Ordinal)
                            .ThenBy(item => item.Catalog.Path, StringComparer.OrdinalIgnoreCase)
                            .First())
                        .ToArray();
                    if (matches.Length != 1) return null;
                    CompatibleRecoveryCandidate match = matches[0];
                    RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(root, match.Profile, statePath);
                    if (inspection.Status != InstallationStatus.RecoveryRequired) return null;
                    return new RuntimeUpdateResolution
                    {
                        Profile = match.Profile,
                        Inspection = inspection,
                        CatalogPath = match.Catalog.Path,
                        Bundle = match.Catalog.Bundle,
                        ChannelAuthority = channelAuthority,
                        InstalledProfile = match.Profile,
                        InstalledInspection = inspection,
                        Source = match.Catalog.Source,
                        RemoteProblem = remoteProblem,
                        RemoteProblemBlocksApply = remoteProblemBlocksApply
                    };
                }

                (CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build)? installed = null;
                RuntimeCacheInspection? installedInspection = null;
                if (state != null && Hashing.FixedEqualsHex(targetSha256, state.PatchedSha256))
                {
                    var stateMatches = new List<((CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build) Item, RuntimeCacheInspection Inspection)>();
                    foreach ((CompatibleCatalogCandidate catalog, CompatibleRevisionProfileBuild build) in builds)
                    {
                        if (!IsExactInstalledState(build.Profile, state)) continue;
                        RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(root, build.Profile, statePath);
                        if (inspection.Status == InstallationStatus.PatchedByThisTool)
                            stateMatches.Add(((catalog, build), inspection));
                    }

                    var artifactGroups = stateMatches
                        .GroupBy(match => StateArtifactAuthorityKey(match.Item.Build.Profile), StringComparer.Ordinal)
                        .ToArray();
                    if (artifactGroups.Length != 1) return null;
                    var selectedInstalled = artifactGroups[0]
                        .OrderByDescending(match => CatalogAuthorityStrength(match.Item.Catalog))
                        .ThenBy(match => match.Item.Catalog.Source, StringComparer.Ordinal)
                        .ThenBy(match => match.Item.Build.Profile.TranslationPolicy, StringComparer.Ordinal)
                        .ThenBy(match => match.Item.Catalog.Path, StringComparer.OrdinalIgnoreCase)
                        .First();
                    installed = selectedInstalled.Item;
                    installedInspection = selectedInstalled.Inspection;
                }

                RuntimeCacheCompatibility? officialUpdatePredecessor = null;
                if (state != null
                    && !Hashing.FixedEqualsHex(targetSha256, state.PatchedSha256)
                    && TryResolveStateBackupPath(root, targetPath, statePath, state, out string predecessorBasePath))
                {
                    (string predecessorEnglishPath, string predecessorStampPath) =
                        RuntimeCacheService.ResolveCompatibleSourceSnapshotPaths(predecessorBasePath);
                    var predecessors = new List<RuntimeCacheCompatibility>();
                    foreach (CompatibleCatalogCandidate catalog in catalogs)
                    {
                        try
                        {
                            byte[] bytes = BoundedArtifactReader.ReadCatalog(
                                catalog.Path,
                                catalog.Sha256,
                                "compatible-revision predecessor catalog");
                            CompatibleRevisionProfileBuild predecessor = CompatibleRevisionProfileBuilder.Build(
                                predecessorEnglishPath,
                                predecessorBasePath,
                                predecessorStampPath,
                                catalog.TrustedContentGuid,
                                bytes,
                                catalog.Sha256,
                                catalog.TranslationPolicy);
                            if (IsExactInstalledState(predecessor.Profile, state)
                                && !predecessors.Any(existing => SameInstalledArtifact(existing, predecessor.Profile)))
                                predecessors.Add(predecessor.Profile);
                        }
                        catch (Exception exception) when (exception is IOException
                            or UnauthorizedAccessException
                            or InvalidDataException
                            or InvalidOperationException
                            or Loc1FormatException)
                        {
                            // A predecessor is trusted only when its exact immutable EN/base/stamp snapshots
                            // rematerialize the state artifact with one trusted catalog. Never infer from the
                            // build id or broad state hashes after a game update.
                        }
                    }

                    // Released exact profiles predate compatible-revision EN/stamp snapshots.  They are
                    // still complete authenticated predecessor descriptors: accept only an exact embedded
                    // or verified-history profile whose full artifact/state pins and immutable backup match.
                    Loc1Document predecessorTarget = Loc1Codec.Parse(BoundedArtifactReader.ReadRuntimeLoc1(
                        predecessorBasePath,
                        "compatible-revision predecessor Ukrainian LOC1"));
                    if (IsExactInstalledState(embeddedProfile, state)
                        && string.Equals(embeddedProfile.ContentGuid, observedContentGuid, StringComparison.Ordinal)
                        && !predecessors.Any(existing => SameInstalledArtifact(existing, embeddedProfile)))
                    {
                        predecessors.Add(embeddedProfile);
                    }
                    if (coordinator != null)
                    {
                        foreach (VerifiedSignedUpdate history in coordinator.LoadVerifiedHistory())
                        {
                            foreach (VerifiedSignedUpdateCompatibilityProfile signed in history.Manifest.Compatibility)
                            {
                                if (!string.Equals(signed.ProfileId, state.BuildId, StringComparison.Ordinal)) continue;
                                try
                                {
                                    RuntimeCacheCompatibility exact = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                                        history.Manifest,
                                        signed,
                                        predecessorTarget);
                                    exact = AttachEmbeddedMigrationAllowlist(embeddedProfile, exact);
                                    if (IsExactInstalledState(exact, state)
                                        && string.Equals(exact.ContentGuid, observedContentGuid, StringComparison.Ordinal)
                                        && !predecessors.Any(existing => SameInstalledArtifact(existing, exact)))
                                    {
                                        predecessors.Add(exact);
                                    }
                                }
                                catch (InvalidDataException)
                                {
                                    // A signed history entry is predecessor authority only when its exact
                                    // base identity and state artifact rematerialize against the backup.
                                }
                            }
                        }
                    }

                    if (predecessors.Count == 1)
                        officialUpdatePredecessor = predecessors[0];
                }

                (CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build)? selected = builds
                    .Where(item => item.Catalog.Selected)
                    .Select(item => ((CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build)?)item)
                    .FirstOrDefault();

                if (selected == null)
                {
                    if (installed == null || installedInspection == null) return null;
                    return new RuntimeUpdateResolution
                    {
                        Profile = installed.Value.Build.Profile,
                        Inspection = installedInspection,
                        CatalogPath = installed.Value.Catalog.Path,
                        ChannelAuthority = channelAuthority,
                        InstalledProfile = installed.Value.Build.Profile,
                        InstalledInspection = installedInspection,
                        Source = installed.Value.Catalog.Source,
                        RemoteProblem = remoteProblem ?? "Current trusted catalog cannot materialize this compatible game revision.",
                        RemoteProblemBlocksApply = remoteProblemBlocksApply
                    };
                }

                RuntimeCacheCompatibility selectedProfile = selected.Value.Build.Profile;
                bool translationUpdate = false;
                if (installed != null && installedInspection != null)
                {
                    if (SameInstalledArtifact(installed.Value.Build.Profile, selectedProfile))
                    {
                        selectedProfile = installed.Value.Build.Profile;
                    }
                    else if (Hashing.FixedEqualsHex(
                        installed.Value.Build.Profile.ExpectedOutputSha256!,
                        selectedProfile.ExpectedOutputSha256!))
                    {
                        // The authenticated catalog/policy metadata changed but materialized identical bytes.
                        // Surface a state-only atomic rebind; keeping the old state would make future restore
                        // depend forever on an obsolete cached catalog.
                        return new RuntimeUpdateResolution
                        {
                            Profile = selectedProfile,
                            Inspection = CloneInspectionWithProfile(installedInspection, selectedProfile),
                            CatalogPath = selected.Value.Catalog.Path,
                            Bundle = selected.Value.Catalog.Bundle,
                            ChannelAuthority = channelAuthority,
                            InstalledProfile = installed.Value.Build.Profile,
                            InstalledInspection = installedInspection,
                            TranslationUpdateAvailable = true,
                            EquivalentCatalogMetadataUpdate = true,
                            Source = selected.Value.Catalog.Source,
                            RemoteProblem = remoteProblem,
                            RemoteProblemBlocksApply = remoteProblemBlocksApply
                        };
                    }
                    else
                    {
                        AttachCompatibleSupersededArtifact(selectedProfile, installed.Value.Build.Profile);
                        translationUpdate = true;
                    }
                }

                RuntimeCacheInspection selectedInspection = RuntimeCacheService.Inspect(
                    root,
                    selectedProfile,
                    statePath,
                    officialUpdatePredecessor);
                if (installed != null && translationUpdate
                    && selectedInspection.Status != InstallationStatus.PatchSupersededByCatalogUpdate)
                    return null;
                if (installed == null
                    && selectedInspection.Status is not (InstallationStatus.CompatibleOriginal
                        or InstallationStatus.PatchSupersededByOfficialUpdate))
                    return null;
                if (installed != null && !translationUpdate
                    && selectedInspection.Status != InstallationStatus.PatchedByThisTool)
                    return null;

                return new RuntimeUpdateResolution
                {
                    Profile = selectedProfile,
                    Inspection = selectedInspection,
                    CatalogPath = selected.Value.Catalog.Path,
                    Bundle = selected.Value.Catalog.Bundle,
                    ChannelAuthority = channelAuthority,
                    InstalledProfile = installed?.Build.Profile,
                    InstalledInspection = installedInspection,
                    TranslationUpdateAvailable = translationUpdate,
                    Source = selected.Value.Catalog.Source,
                    RemoteProblem = remoteProblem,
                    RemoteProblemBlocksApply = remoteProblemBlocksApply
                };
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or Loc1FormatException)
            {
                return null;
            }
        }

        private static RuntimeUpdateResolution? TryResolveExactJournalRecovery(
            string cacheRoot,
            string statePath,
            RuntimeCacheCompatibility embeddedProfile,
            string embeddedCatalogPath,
            SignedUpdateCoordinator coordinator,
            PatchJournal journal,
            VerifiedSignedUpdate? channelAuthority,
            string? remoteProblem,
            bool remoteProblemBlocksApply)
        {
            try
            {
                string root = Path.GetFullPath(cacheRoot);
                (_, string targetPath, _) = RuntimeCacheService.ResolveTuplePaths(root);
                string targetSha256 = File.Exists(targetPath)
                    ? BoundedArtifactReader.Sha256File(
                        targetPath,
                        BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                        "journaled runtime-cache target")
                    : string.Empty;
                if (!TryResolveJournalBasePath(
                        root,
                        targetPath,
                        statePath,
                        journal,
                        targetSha256,
                        out string basePath))
                    return null;

                Loc1Document preservedBase = Loc1Codec.Parse(BoundedArtifactReader.ReadRuntimeLoc1(
                    basePath,
                    "signed-history preserved Ukrainian LOC1"));
                var matches = new List<(RuntimeCacheCompatibility Profile, ulong Sequence, string Payload)>();
                foreach (VerifiedSignedUpdate history in coordinator.LoadVerifiedHistory())
                {
                    foreach (VerifiedSignedUpdateCompatibilityProfile signed in history.Manifest.Compatibility)
                    {
                        if (!string.Equals(signed.ProfileId, journal.BuildId, StringComparison.Ordinal)) continue;
                        try
                        {
                            RuntimeCacheCompatibility candidate = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                                history.Manifest,
                                signed,
                                preservedBase);
                            candidate = AttachEmbeddedMigrationAllowlist(embeddedProfile, candidate);
                            if (!IsExactJournal(candidate, journal)
                                || !RuntimeCacheService.TryAuthenticateRecovery(root, statePath, candidate, out _))
                                continue;
                            matches.Add((candidate, history.Manifest.Sequence, history.PayloadSha256));
                        }
                        catch (Exception exception) when (exception is IOException
                            or InvalidDataException
                            or InvalidOperationException
                            or Loc1FormatException)
                        {
                            // Every historical profile is independently signature-verified, but only the
                            // one exact preserved source/journal tuple may authorize recovery.
                        }
                    }
                }

                var groups = matches
                    .GroupBy(match => StateArtifactAuthorityKey(match.Profile), StringComparer.Ordinal)
                    .ToArray();
                if (groups.Length != 1) return null;
                RuntimeCacheCompatibility profile = groups[0]
                    .OrderByDescending(match => match.Sequence)
                    .ThenBy(match => match.Payload, StringComparer.Ordinal)
                    .First().Profile;
                RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(root, profile, statePath);
                if (inspection.Status != InstallationStatus.RecoveryRequired) return null;
                return new RuntimeUpdateResolution
                {
                    Profile = profile,
                    Inspection = inspection,
                    CatalogPath = embeddedCatalogPath,
                    Bundle = null,
                    ChannelAuthority = channelAuthority,
                    InstalledProfile = profile,
                    InstalledInspection = inspection,
                    TranslationUpdateAvailable = false,
                    Source = "embedded",
                    RemoteProblem = remoteProblem,
                    RemoteProblemBlocksApply = remoteProblemBlocksApply
                };
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or Loc1FormatException)
            {
                return null;
            }
        }

        private static List<CompatibleCatalogCandidate> LoadCompatibleCatalogCandidates(
            RuntimeCacheCompatibility embeddedProfile,
            string embeddedCatalogPath,
            SignedUpdateCoordinator? coordinator,
            SignedUpdateBundle? bundle,
            string observedContentGuid)
        {
            var result = new List<CompatibleCatalogCandidate>();
            if (bundle != null
                && SignedUpdateRuntimeProfileAdapter.AuthorizesContentFamily(
                    bundle.Update.Manifest,
                    observedContentGuid))
            {
                AddCatalogCandidate(result, new CompatibleCatalogCandidate(
                    bundle.CatalogPath,
                    bundle.Update.Manifest.Catalog.UncompressedSha256,
                    MapCatalogPolicy(bundle.Update.Manifest.Catalog.TranslationPolicy),
                    observedContentGuid,
                    bundle.Source.ToString(),
                    Selected: true,
                    Bundle: bundle));
            }

            if (embeddedProfile.TranslationCatalogSha256 != null
                && string.Equals(embeddedProfile.ContentGuid, observedContentGuid, StringComparison.Ordinal))
            {
                AddCatalogCandidate(result, new CompatibleCatalogCandidate(
                    embeddedCatalogPath,
                    embeddedProfile.TranslationCatalogSha256,
                    embeddedProfile.TranslationPolicy,
                    embeddedProfile.ContentGuid,
                    "embedded",
                    Selected: bundle == null,
                    Bundle: null));
            }

            if (coordinator != null)
            {
                foreach (VerifiedSignedUpdate update in coordinator.LoadVerifiedHistory().Reverse())
                {
                    if (!SignedUpdateRuntimeProfileAdapter.AuthorizesContentFamily(
                            update.Manifest,
                            observedContentGuid))
                        continue;
                    if (!coordinator.TryGetVerifiedCatalogPath(update, out string path)) continue;
                    AddCatalogCandidate(result, new CompatibleCatalogCandidate(
                        path,
                        update.Manifest.Catalog.UncompressedSha256,
                        MapCatalogPolicy(update.Manifest.Catalog.TranslationPolicy),
                        observedContentGuid,
                        "CachedCurrent",
                        Selected: false,
                        Bundle: null));
                }
            }

            return result;
        }

        private static void AddCatalogCandidate(
            List<CompatibleCatalogCandidate> candidates,
            CompatibleCatalogCandidate candidate)
        {
            if (candidates.Any(existing => Hashing.FixedEqualsHex(existing.Sha256, candidate.Sha256)
                    && string.Equals(existing.TranslationPolicy, candidate.TranslationPolicy, StringComparison.Ordinal)
                    && string.Equals(existing.TrustedContentGuid, candidate.TrustedContentGuid, StringComparison.Ordinal)
                    && string.Equals(existing.Source, candidate.Source, StringComparison.Ordinal)
                    && existing.Selected == candidate.Selected
                    && Equals(existing.Bundle?.Update.PayloadSha256, candidate.Bundle?.Update.PayloadSha256)))
                return;
            candidates.Add(candidate);
        }

        private static int CatalogAuthorityStrength(CompatibleCatalogCandidate candidate)
        {
            if (candidate.Selected && candidate.Bundle != null) return 300;
            if (string.Equals(candidate.Source, "embedded", StringComparison.Ordinal)) return 200;
            return 100;
        }

        private static string StateArtifactAuthorityKey(RuntimeCacheCompatibility profile)
        {
            // Schema-1 state authenticates only this artifact identity. Policy/fallback metadata was not
            // persisted, so an authenticated catalog republished under another policy may legitimately
            // rematerialize the same recorded bytes/count. Group only on what state plus the immutable
            // source tuple can prove, then choose the strongest provenance deterministically.
            return string.Join('\n', new[]
            {
                "runtime-cache-state-artifact-v1",
                profile.Id,
                profile.Mode,
                profile.GameVersion,
                profile.ContentGuid,
                profile.EnglishContentVersion,
                profile.BaseContentVersion,
                profile.EnglishSha256,
                profile.BaseSha256,
                profile.StampSha256,
                profile.StampValue,
                profile.EnglishLocaleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.EnglishLocaleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.EnglishReleaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.BaseLocaleId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.BaseLocaleRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.BaseReleaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile.OrderedKeysetSha256 ?? string.Empty,
                profile.ExpectedOutputSha256 ?? string.Empty,
                profile.TranslationCatalogSha256 ?? string.Empty,
                profile.ExpectedAppliedTranslations.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        private static RuntimeCacheInspection CloneInspectionWithProfile(
            RuntimeCacheInspection source,
            RuntimeCacheCompatibility profile)
        {
            return new RuntimeCacheInspection
            {
                Status = source.Status,
                Message = "A newer authenticated catalog has identical materialized bytes; only exact state metadata needs rebinding.",
                CacheRoot = source.CacheRoot,
                EnglishPath = source.EnglishPath,
                TargetPath = source.TargetPath,
                StampPath = source.StampPath,
                EnglishSha256 = source.EnglishSha256,
                BaseSha256 = source.BaseSha256,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                EnglishContentVersion = source.EnglishContentVersion,
                BaseContentVersion = source.BaseContentVersion,
                Profile = profile,
                State = source.State,
                Journal = source.Journal
            };
        }

        private static IEnumerable<CompatibleRecoveryCandidate> EnumerateCompatibleRecoveryCandidates(
            IReadOnlyList<(CompatibleCatalogCandidate Catalog, CompatibleRevisionProfileBuild Build)> builds,
            PatchJournal journal)
        {
            foreach ((CompatibleCatalogCandidate catalog, CompatibleRevisionProfileBuild build) in builds)
            {
                yield return new CompatibleRecoveryCandidate(catalog, build.Profile);
                if (journal.Operation is not ("runtime-cache-upgrade" or "runtime-cache-upgrade-restore"))
                    continue;

                foreach ((_, CompatibleRevisionProfileBuild predecessor) in builds)
                {
                    if (SameInstalledArtifact(build.Profile, predecessor.Profile)
                        || !SameCompatibleSourceTuple(build.Profile, predecessor.Profile))
                        continue;
                    RuntimeCacheCompatibility candidate = CloneCompatibleProfile(build.Profile);
                    AttachCompatibleSupersededArtifact(candidate, predecessor.Profile);
                    yield return new CompatibleRecoveryCandidate(catalog, candidate);
                }
            }
        }

        private static string RecoveryAuthorityKey(
            RuntimeCacheCompatibility profile,
            PatchJournal journal)
        {
            string staticTuple = string.Join('\n', new[]
            {
                profile.Id,
                profile.Mode,
                profile.EnglishSha256,
                profile.BaseSha256,
                profile.StampSha256,
                profile.ContentGuid,
                profile.OrderedKeysetSha256 ?? string.Empty,
                profile.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            // Schema-1 journals authenticate exactly these transaction pins. Policy/fallback diagnostics
            // are deliberately absent, so a republished catalog policy that rematerializes the same bytes
            // must not create false recovery ambiguity. Full source snapshots and Core journal validation
            // still authenticate every candidate before this equivalence grouping is used.
            return string.Join('\n', new[]
            {
                "compatible-recovery-journal-v1",
                staticTuple,
                journal.Operation,
                journal.SourceSha256,
                journal.ExpectedOutputSha256,
                journal.TranslationsSha256,
                journal.AppliedTranslations.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        private static bool SameCompatibleSourceTuple(
            RuntimeCacheCompatibility left,
            RuntimeCacheCompatibility right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.Mode, CompatibleRevisionProfileBuilder.Mode, StringComparison.Ordinal)
                && string.Equals(right.Mode, CompatibleRevisionProfileBuilder.Mode, StringComparison.Ordinal)
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
                && Hashing.FixedEqualsHex(left.OrderedKeysetSha256!, right.OrderedKeysetSha256!);
        }

        private static RuntimeCacheCompatibility CloneCompatibleProfile(RuntimeCacheCompatibility source)
        {
            var clone = new RuntimeCacheCompatibility
            {
                Schema = source.Schema,
                Id = source.Id,
                Mode = source.Mode,
                GameVersion = source.GameVersion,
                ContentGuid = source.ContentGuid,
                EnglishContentVersion = source.EnglishContentVersion,
                BaseContentVersion = source.BaseContentVersion,
                EnglishSha256 = source.EnglishSha256,
                BaseSha256 = source.BaseSha256,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                EnglishLocaleId = source.EnglishLocaleId,
                EnglishLocaleRevision = source.EnglishLocaleRevision,
                EnglishReleaseRevision = source.EnglishReleaseRevision,
                BaseLocaleId = source.BaseLocaleId,
                BaseLocaleRevision = source.BaseLocaleRevision,
                BaseReleaseRevision = source.BaseReleaseRevision,
                EntryCount = source.EntryCount,
                OrderedKeysetSha256 = source.OrderedKeysetSha256,
                Readiness = source.Readiness,
                Certified = source.Certified,
                BlockedReason = source.BlockedReason,
                TranslationCatalogSha256 = source.TranslationCatalogSha256,
                ExpectedOutputSha256 = source.ExpectedOutputSha256,
                MinimumAppliedTranslations = source.MinimumAppliedTranslations,
                ExpectedAppliedTranslations = source.ExpectedAppliedTranslations,
                ExpectedEnglishFallbacks = source.ExpectedEnglishFallbacks,
                ExpectedBaseFallbacks = source.ExpectedBaseFallbacks,
                ExpectedNeedsReviewFallbacks = source.ExpectedNeedsReviewFallbacks,
                TranslationPolicy = source.TranslationPolicy,
                SupersededArtifacts = source.SupersededArtifacts.Select(artifact => new RuntimeCacheSupersededArtifact
                {
                    OutputSha256 = artifact.OutputSha256,
                    TranslationCatalogSha256 = artifact.TranslationCatalogSha256,
                    AppliedTranslations = artifact.AppliedTranslations,
                    EnglishFallbacks = artifact.EnglishFallbacks,
                    BaseFallbacks = artifact.BaseFallbacks,
                    NeedsReviewFallbacks = artifact.NeedsReviewFallbacks
                }).ToArray()
            };
            clone.Validate();
            return clone;
        }

        private static bool TryResolveStateBackupPath(
            string root,
            string targetPath,
            string statePath,
            PatchState state,
            out string backupPath)
        {
            backupPath = string.Empty;
            if (!IsSafeProfileId(state.BuildId)
                || !IsSha256(state.OriginalSha256)
                || !IsSha256(state.PatchedSha256)
                || !IsSha256(state.TranslationsSha256)
                || state.AppliedTranslations is < 1 or > 100000
                || !PathEquals(state.GameRoot, root)
                || !PathEquals(state.TargetPath, targetPath))
                return false;

            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? string.Empty;
            string expected = Path.Combine(
                stateRoot,
                "backups",
                SafeProfileId(state.BuildId),
                $"{state.OriginalSha256}.dl_uk_UA.bin");
            if (!PathEquals(state.BackupPath, expected)) return false;
            PatchService.RejectExistingReparseComponents(expected, "compatible-revision immutable backup");
            if (!File.Exists(expected)
                || (File.GetAttributes(expected) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !Hashing.FixedEqualsHex(
                    BoundedArtifactReader.Sha256File(
                        expected,
                        BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                        "compatible-revision immutable backup"),
                    state.OriginalSha256))
                return false;
            backupPath = expected;
            return true;
        }

        private static bool TryResolveJournalBasePath(
            string root,
            string targetPath,
            string statePath,
            PatchJournal journal,
            string targetSha256,
            out string basePath)
        {
            basePath = string.Empty;
            if (!IsSafeProfileId(journal.BuildId)
                || !IsSha256(journal.SourceSha256)
                || !IsSha256(journal.ExpectedOutputSha256)
                || !IsSha256(journal.TranslationsSha256)
                || journal.AppliedTranslations is < 1 or > 100000
                || !PathEquals(journal.GameRoot, root)
                || !PathEquals(journal.TargetPath, targetPath))
                return false;

            string fileName = Path.GetFileName(journal.BackupPath);
            const string suffix = ".dl_uk_UA.bin";
            if (!fileName.EndsWith(suffix, StringComparison.Ordinal)
                || !IsSha256(fileName.Substring(0, fileName.Length - suffix.Length)))
                return false;
            string baseSha256 = fileName.Substring(0, fileName.Length - suffix.Length);
            string stateRoot = Path.GetDirectoryName(Path.GetFullPath(statePath)) ?? string.Empty;
            string expected = Path.Combine(
                stateRoot,
                "backups",
                SafeProfileId(journal.BuildId),
                fileName);
            if (!PathEquals(journal.BackupPath, expected)) return false;
            if (File.Exists(expected))
            {
                PatchService.RejectExistingReparseComponents(expected, "compatible-revision recovery backup");
                if ((File.GetAttributes(expected) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !Hashing.FixedEqualsHex(
                        BoundedArtifactReader.Sha256File(
                            expected,
                            BoundedArtifactReader.MaximumRuntimeLoc1Bytes,
                            "compatible-revision recovery backup"),
                        baseSha256))
                    return false;
                basePath = expected;
                return true;
            }

            if (journal.Operation == "runtime-cache-apply"
                && Hashing.FixedEqualsHex(targetSha256, journal.SourceSha256))
            {
                basePath = targetPath;
                return true;
            }

            return false;
        }

        private static void AttachCompatibleSupersededArtifact(
            RuntimeCacheCompatibility selected,
            RuntimeCacheCompatibility installed)
        {
            if (installed.ExpectedOutputSha256 == null || installed.TranslationCatalogSha256 == null
                || installed.ExpectedEnglishFallbacks < 0 || installed.ExpectedBaseFallbacks < 0
                || installed.ExpectedNeedsReviewFallbacks < 0)
                throw new InvalidDataException("Installed compatible-revision artifact lacks exact composition pins.");
            selected.SupersededArtifacts = selected.SupersededArtifacts.Concat(new[]
            {
                new RuntimeCacheSupersededArtifact
                {
                    OutputSha256 = installed.ExpectedOutputSha256,
                    TranslationCatalogSha256 = installed.TranslationCatalogSha256,
                    AppliedTranslations = installed.ExpectedAppliedTranslations,
                    EnglishFallbacks = installed.ExpectedEnglishFallbacks,
                    BaseFallbacks = installed.ExpectedBaseFallbacks,
                    NeedsReviewFallbacks = installed.ExpectedNeedsReviewFallbacks
                }
            }).ToArray();
            selected.Validate();
        }

        private static string MapCatalogPolicy(string value)
        {
            return value switch
            {
                "release-approved-v1" => "release-approved",
                "validated-preview-v1" => "community-preview-all-drafts",
                _ => throw new InvalidDataException("Signed catalog translation policy is unsupported for compatible-revision mode.")
            };
        }

        private static bool IsSafeProfileId(string value)
        {
            return value.Length is > 0 and <= 128
                && value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.');
        }

        private static string SafeProfileId(string value) => $"{value}-{Hashing.Sha256Text(value).Substring(0, 12)}";

        private static bool PathEquals(string left, string right) => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        private sealed record CompatibleCatalogCandidate(
            string Path,
            string Sha256,
            string TranslationPolicy,
            string TrustedContentGuid,
            string Source,
            bool Selected,
            SignedUpdateBundle? Bundle);

        private sealed record CompatibleRecoveryCandidate(
            CompatibleCatalogCandidate Catalog,
            RuntimeCacheCompatibility Profile);

        private static RuntimeCacheCompatibility CloneObservedWithOfficialBase(
            RuntimeCacheCompatibility source,
            string officialBaseSha256)
        {
            var clone = new RuntimeCacheCompatibility
            {
                Schema = source.Schema,
                Id = source.Id,
                GameVersion = source.GameVersion,
                ContentGuid = source.ContentGuid,
                EnglishContentVersion = source.EnglishContentVersion,
                BaseContentVersion = source.BaseContentVersion,
                EnglishSha256 = source.EnglishSha256,
                BaseSha256 = officialBaseSha256,
                StampSha256 = source.StampSha256,
                StampValue = source.StampValue,
                EnglishLocaleId = source.EnglishLocaleId,
                EnglishLocaleRevision = source.EnglishLocaleRevision,
                EnglishReleaseRevision = source.EnglishReleaseRevision,
                BaseLocaleId = source.BaseLocaleId,
                BaseLocaleRevision = source.BaseLocaleRevision,
                BaseReleaseRevision = source.BaseReleaseRevision,
                EntryCount = source.EntryCount,
                MinimumAppliedTranslations = 1,
                TranslationPolicy = "supervised-safe-drafts",
                Readiness = "blocked",
                Certified = false,
                BlockedReason = "Observed patched tuple used only for signed profile selection."
            };
            clone.Validate();
            return clone;
        }

        private static bool IsExactInstalledState(RuntimeCacheCompatibility profile, PatchState state)
        {
            return string.Equals(state.BuildId, profile.Id, StringComparison.Ordinal)
                && Hashing.FixedEqualsHex(state.OriginalSha256, profile.BaseSha256)
                && profile.ExpectedOutputSha256 != null
                && Hashing.FixedEqualsHex(state.PatchedSha256, profile.ExpectedOutputSha256)
                && profile.TranslationCatalogSha256 != null
                && Hashing.FixedEqualsHex(state.TranslationsSha256, profile.TranslationCatalogSha256)
                && state.AppliedTranslations == profile.ExpectedAppliedTranslations;
        }

        private static bool IsExactJournal(RuntimeCacheCompatibility profile, PatchJournal journal)
        {
            if (journal.Operation is not ("runtime-cache-apply" or "runtime-cache-restore"
                    or "runtime-cache-upgrade" or "runtime-cache-upgrade-restore"))
                return false;
            if (profile.ExpectedOutputSha256 == null || profile.TranslationCatalogSha256 == null)
                return false;
            if (journal.Schema != 1 || !string.Equals(journal.BuildId, profile.Id, StringComparison.Ordinal))
                return false;
            return journal.Operation switch
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
                    IsSupersededPatchHash(journal.SourceSha256, profile)
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.ExpectedOutputSha256)
                    && Hashing.FixedEqualsHex(journal.TranslationsSha256, profile.TranslationCatalogSha256)
                    && journal.AppliedTranslations == profile.ExpectedAppliedTranslations,
                "runtime-cache-upgrade-restore" =>
                    profile.SupersededArtifacts.Count(artifact =>
                        Hashing.FixedEqualsHex(artifact.OutputSha256, journal.SourceSha256)
                        && Hashing.FixedEqualsHex(artifact.TranslationCatalogSha256, journal.TranslationsSha256)
                        && artifact.AppliedTranslations == journal.AppliedTranslations) == 1
                    && Hashing.FixedEqualsHex(journal.ExpectedOutputSha256, profile.BaseSha256),
                _ => false
            };
        }

        private static bool IsSupersededPatchHash(string value, RuntimeCacheCompatibility profile)
        {
            return IsSha256(value)
                && profile.SupersededArtifacts.Count(artifact =>
                    Hashing.FixedEqualsHex(artifact.OutputSha256, value)) == 1;
        }

        private static bool IsSha256(string? value)
        {
            return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        internal static RuntimeCacheCompatibility AttachEmbeddedMigrationAllowlist(
            RuntimeCacheCompatibility embedded,
            RuntimeCacheCompatibility selected)
        {
            bool sameSourceTuple = string.Equals(embedded.Id, selected.Id, StringComparison.Ordinal)
                && string.Equals(embedded.GameVersion, selected.GameVersion, StringComparison.Ordinal)
                && string.Equals(embedded.ContentGuid, selected.ContentGuid, StringComparison.Ordinal)
                && string.Equals(embedded.EnglishContentVersion, selected.EnglishContentVersion, StringComparison.Ordinal)
                && string.Equals(embedded.BaseContentVersion, selected.BaseContentVersion, StringComparison.Ordinal)
                && Hashing.FixedEqualsHex(embedded.EnglishSha256, selected.EnglishSha256)
                && Hashing.FixedEqualsHex(embedded.BaseSha256, selected.BaseSha256)
                && Hashing.FixedEqualsHex(embedded.StampSha256, selected.StampSha256)
                && string.Equals(embedded.StampValue, selected.StampValue, StringComparison.Ordinal)
                && embedded.EnglishLocaleId == selected.EnglishLocaleId
                && embedded.EnglishLocaleRevision == selected.EnglishLocaleRevision
                && embedded.EnglishReleaseRevision == selected.EnglishReleaseRevision
                && embedded.BaseLocaleId == selected.BaseLocaleId
                && embedded.BaseLocaleRevision == selected.BaseLocaleRevision
                && embedded.BaseReleaseRevision == selected.BaseReleaseRevision
                && embedded.EntryCount == selected.EntryCount;
            if (!sameSourceTuple || embedded.SupersededArtifacts.Length == 0)
                return selected;

            selected.SupersededArtifacts = embedded.SupersededArtifacts
                .Where(artifact => !Hashing.FixedEqualsHex(artifact.OutputSha256, selected.ExpectedOutputSha256)
                    && !Hashing.FixedEqualsHex(artifact.TranslationCatalogSha256, selected.TranslationCatalogSha256))
                .Select(artifact => new RuntimeCacheSupersededArtifact
                {
                    OutputSha256 = artifact.OutputSha256,
                    TranslationCatalogSha256 = artifact.TranslationCatalogSha256,
                    AppliedTranslations = artifact.AppliedTranslations,
                    EnglishFallbacks = artifact.EnglishFallbacks,
                    BaseFallbacks = artifact.BaseFallbacks,
                    NeedsReviewFallbacks = artifact.NeedsReviewFallbacks
                })
                .ToArray();
            selected.Validate();
            return selected;
        }

        private static bool SameInstalledArtifact(
            RuntimeCacheCompatibility left,
            RuntimeCacheCompatibility right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && left.ExpectedOutputSha256 != null
                && right.ExpectedOutputSha256 != null
                && Hashing.FixedEqualsHex(left.ExpectedOutputSha256, right.ExpectedOutputSha256)
                && left.TranslationCatalogSha256 != null
                && right.TranslationCatalogSha256 != null
                && Hashing.FixedEqualsHex(left.TranslationCatalogSha256, right.TranslationCatalogSha256)
                && left.ExpectedAppliedTranslations == right.ExpectedAppliedTranslations;
        }

        private static bool SameMaterializedProfileIdentity(
            RuntimeCacheCompatibility left,
            RuntimeCacheCompatibility right)
        {
            return SameInstalledArtifact(left, right)
                && string.Equals(left.Mode, right.Mode, StringComparison.Ordinal)
                && string.Equals(left.TranslationPolicy, right.TranslationPolicy, StringComparison.Ordinal)
                && left.ExpectedEnglishFallbacks == right.ExpectedEnglishFallbacks
                && left.ExpectedBaseFallbacks == right.ExpectedBaseFallbacks
                && left.ExpectedNeedsReviewFallbacks == right.ExpectedNeedsReviewFallbacks
                && SameCompatibleSourceTuple(left, right);
        }
    }
}
