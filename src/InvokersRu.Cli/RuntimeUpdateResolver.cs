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
        public string Source { get; init; } = "embedded";
        public string? RemoteProblem { get; init; }
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
                state?.AppliedTranslations);
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
            int? installedStateAppliedTranslations)
        {
            if (hasRemoteProblem) return false;
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
            string? remoteProblem = null)
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
                RemoteProblem = remoteProblem
            };
            if (coordinator == null) return embedded;

            SignedUpdateBundle? bundle = coordinator.LoadBestAvailable();
            VerifiedSignedUpdate? channelAuthority = bundle?.Authority ?? coordinator.LoadNewestAccepted();
            VerifiedSignedUpdate? selectedUpdate = bundle?.Update ?? channelAuthority;
            if (selectedUpdate == null) return embedded;
            if (!TryReadObserved(cacheRoot, out RuntimeCacheCompatibility? directObserved, out Loc1Document? target))
                return WithUnavailableSignedData(
                    embedded,
                    bundle,
                    channelAuthority!,
                    "The fixed runtime-cache tuple is missing or unreadable.");

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

            RuntimeCacheCompatibility? installedProfile = embedded.InstalledProfile;
            RuntimeCacheInspection? installedInspection = embedded.InstalledInspection;
            PatchState? state = PatchPlanner.TryLoadState(statePath);
            PatchJournal? journal = embeddedInspection.Journal;
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

            if (installedInspection?.Status == InstallationStatus.RecoveryRequired)
            {
                return new RuntimeUpdateResolution
                {
                    Profile = installedProfile!,
                    Inspection = installedInspection,
                    CatalogPath = embeddedCatalogPath,
                    Bundle = bundle,
                    ChannelAuthority = channelAuthority,
                    InstalledProfile = installedProfile,
                    InstalledInspection = installedInspection,
                    TranslationUpdateAvailable = false,
                    // Recovery authenticates the historical profile through state/journal history, but the
                    // catalog path in this diagnostic plan is still the embedded bootstrap catalog. Do not
                    // mislabel that file as a current remote exact-profile selection.
                    Source = "embedded",
                    RemoteProblem = remoteProblem
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
                    "No signed exact translation profile has been published for the observed game build.",
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
                RemoteProblem = remoteProblem
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
                Bundle = bundle,
                ChannelAuthority = channelAuthority,
                InstalledProfile = installedProfile ?? embedded.InstalledProfile,
                InstalledInspection = installedInspection ?? embedded.InstalledInspection,
                TranslationUpdateAvailable = false,
                Source = embedded.Source,
                RemoteProblem = problem
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
            observed = RuntimeCacheService.DescribeTuple(english, basePath, stamp, "observed-runtime-cache");
            target = Loc1Codec.ReadFile(basePath);
            return true;
        }

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
    }
}
