using InvokersRu.Gui;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvokersRu.SmokeTests
{
    internal static class GuiContractSmokeTests
    {
        private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        private const string HashC = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        private const string ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";

        internal static void Run(Action<string> passed)
        {
            StrictPlanContractCheck();
            passed("GUI plan JSON parser accepts only a complete, internally consistent CLI contract");

            if (OperatingSystem.IsWindows())
            {
                RunnerTimeoutOwnChildOnlyCheck();
                passed("GUI companion timeout terminates only its own child process");
            }
        }

        private static void StrictPlanContractCheck()
        {
            JsonObject ready = ReadyResponse();
            CliPlanResult parsed = Parse(ready, 0);
            Require(parsed.CanApply && parsed.Catalog.ExactMatch
                && parsed.Observed.GameVersion == "0.60.1247",
                "A valid exact ready-to-apply response was rejected or lost its observed/catalog identity.");

            JsonObject compatible = Clone(ready);
            Profile(compatible)["mode"] = "compatible-revision";
            compatible["message"] = "Compatible revision with exact per-row source and hint matching.";
            compatible["diagnostic"] = new JsonObject
            {
                ["kind"] = "compatible-coverage",
                ["component"] = "source-hint-coverage",
                ["current"] = "41037/41038",
                ["expected"] = "41038/41038"
            };
            CliPlanResult compatibleParsed = Parse(compatible, 0);
            Require(compatibleParsed.CanApply
                && compatibleParsed.Profile.Mode == "compatible-revision"
                && compatibleParsed.Observed.GameVersion == "0.60.1247"
                && compatibleParsed.Observed.EnglishContent == "Prod_0.60.0_68",
                "A valid compatible-revision plan was rejected or lost its observed identity.");

            JsonObject zeroCoverage = Clone(compatible);
            Profile(zeroCoverage)["certified"] = false;
            Profile(zeroCoverage)["readiness"] = "blocked";
            Profile(zeroCoverage)["expected_output_sha256"] = null;
            Profile(zeroCoverage)["applied_translations"] = 0;
            Profile(zeroCoverage)["english_fallbacks"] = 41_038;
            zeroCoverage["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "source-hint-coverage",
                ["current"] = "0/41038",
                ["expected"] = "41038/41038"
            };
            zeroCoverage["plan"] = "REFUSE_NO_TRUSTED_CACHE_RELEASE_PROFILE";
            zeroCoverage["can_apply"] = false;
            Require(Parse(zeroCoverage, 0).Diagnostic.Kind == "translation-data",
                "Zero current source+hint coverage was not represented as a wait-for-fresh-translation diagnostic.");

            JsonObject forgedCertifiedZeroCoverage = Clone(compatible);
            Profile(forgedCertifiedZeroCoverage)["applied_translations"] = 0;
            Profile(forgedCertifiedZeroCoverage)["english_fallbacks"] = 41_038;
            forgedCertifiedZeroCoverage["diagnostic"] = Clone(zeroCoverage["diagnostic"]!.AsObject());
            ExpectInvalid(forgedCertifiedZeroCoverage, 0,
                "certified compatible-revision profile with zero exact source+hint coverage");

            JsonObject compatibleCatalogMismatch = Clone(compatible);
            Catalog(compatibleCatalogMismatch)["sha256"] = HashB;
            Catalog(compatibleCatalogMismatch)["exact_match"] = false;
            compatibleCatalogMismatch["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "catalog-sha256",
                ["current"] = HashB,
                ["expected"] = HashA
            };
            compatibleCatalogMismatch["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            compatibleCatalogMismatch["can_apply"] = false;
            Require(Parse(compatibleCatalogMismatch, 0).Diagnostic.Component == "catalog-sha256",
                "Catalog mismatch did not outrank compatible partial-coverage information.");

            JsonObject compatibleOfficialUpdate = Clone(compatible);
            compatibleOfficialUpdate["status"] = "PatchSupersededByOfficialUpdate";
            compatibleOfficialUpdate["state"] = StateObject();
            compatibleOfficialUpdate["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "official-base-refresh",
                ["current"] = HashC,
                ["expected"] = HashB
            };
            compatibleOfficialUpdate["plan"] = "READY_TO_REAPPLY_AFTER_GAME_UPDATE";
            compatibleOfficialUpdate["can_restore"] = false;
            Require(Parse(compatibleOfficialUpdate, 0).Diagnostic.Component == "official-base-refresh",
                "Official base refresh did not outrank compatible partial-coverage information.");

            JsonObject translationTupleMismatch = Clone(ready);
            translationTupleMismatch["status"] = "UnknownBuild";
            translationTupleMismatch["message"] = "Official UK translation data differs from the selected exact profile.";
            translationTupleMismatch["observed"]!["base_sha256"] = HashB;
            translationTupleMismatch["observed"]!["base_content"] = "Prod_0.60.0_71";
            translationTupleMismatch["observed"]!["base_locale_revision"] = 0x87654322;
            translationTupleMismatch["observed"]!["base_release_revision"] = 71;
            translationTupleMismatch["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "ukrainian-base",
                ["current"] = $"content=Prod_0.60.0_71;release=71;locale_revision=87654322;sha256={HashB}",
                ["expected"] = $"content=Prod_0.60.0_68;release=68;locale_revision=87654321;sha256={HashC}"
            };
            translationTupleMismatch["plan"] = "REFUSE_UNKNOWN_OR_INCONSISTENT";
            translationTupleMismatch["can_apply"] = false;
            Require(Parse(translationTupleMismatch, 5).Diagnostic.Component == "ukrainian-base",
                "A translation-data revision mismatch was mislabeled as an unsupported client version.");

            JsonObject staleRecordedState = Clone(translationTupleMismatch);
            staleRecordedState["status"] = "InconsistentState";
            staleRecordedState["state"] = StateObject();
            Require(Parse(staleRecordedState, 5).Diagnostic.Kind == "translation-data",
                "A readable recorded state plus official data update hid the exact mismatched component.");

            JsonObject structuralBoundary = Clone(translationTupleMismatch);
            structuralBoundary["observed"]!["english_schema"] = 5;
            structuralBoundary["diagnostic"] = new JsonObject
            {
                ["kind"] = "structural-boundary",
                ["component"] = "loc1-schema",
                ["current"] = "EN=5;UK=4",
                ["expected"] = "EN=4;UK=4"
            };
            Require(Parse(structuralBoundary, 5).Diagnostic.Kind == "structural-boundary",
                "A true LOC1 structural boundary was not kept distinct from stale translation data.");

            JsonObject forgedStructuralAsTranslation = Clone(translationTupleMismatch);
            forgedStructuralAsTranslation["observed"]!["english_schema"] = 5;
            ExpectInvalid(forgedStructuralAsTranslation, 5,
                "structural LOC1 boundary hidden behind a valid lower-priority translation mismatch");

            JsonObject forgedEarlierEnglishAsUkrainian = Clone(translationTupleMismatch);
            forgedEarlierEnglishAsUkrainian["observed"]!["english_sha256"] = HashB;
            ExpectInvalid(forgedEarlierEnglishAsUkrainian, 5,
                "English source mismatch hidden behind a valid lower-priority Ukrainian mismatch");

            JsonObject forgedInconsistentStructuralAsTranslation = Clone(forgedStructuralAsTranslation);
            forgedInconsistentStructuralAsTranslation["status"] = "InconsistentState";
            forgedInconsistentStructuralAsTranslation["state"] = StateObject();
            ExpectInvalid(forgedInconsistentStructuralAsTranslation, 5,
                "inconsistent readable state hid a structural boundary behind translation data");

            JsonObject unreadableSchemaWithReadableState = Clone(staleRecordedState);
            unreadableSchemaWithReadableState["observed"]!["english_schema"] = null;
            unreadableSchemaWithReadableState["diagnostic"] = new JsonObject
            {
                ["kind"] = "structural-boundary",
                ["component"] = "loc1-schema",
                ["current"] = "EN=unreadable;UK=4",
                ["expected"] = "EN=4;UK=4"
            };
            Require(Parse(unreadableSchemaWithReadableState, 5).Diagnostic.Component == "loc1-schema",
                "A readable state hid an unreadable LOC1 schema behind a generic local-state diagnostic.");

            JsonObject catalogMismatch = Clone(ready);
            Catalog(catalogMismatch)["sha256"] = HashB;
            Catalog(catalogMismatch)["exact_match"] = false;
            catalogMismatch["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "catalog-sha256",
                ["current"] = HashB,
                ["expected"] = HashA
            };
            catalogMismatch["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            catalogMismatch["can_apply"] = false;
            Require(Parse(catalogMismatch, 0).Diagnostic.Component == "catalog-sha256",
                "Catalog mismatch did not identify exact current/expected translation data hashes.");

            JsonObject forgedDiagnostic = Clone(translationTupleMismatch);
            forgedDiagnostic["diagnostic"]!["expected"] = HashA;
            ExpectInvalid(forgedDiagnostic, 5, "typed mismatch diagnostic contradicting canonical profile fields");
            JsonObject missingDiagnostic = Clone(ready);
            missingDiagnostic.Remove("diagnostic");
            ExpectInvalid(missingDiagnostic, 0, "plan response without required typed diagnostic");

            JsonObject missingMode = Clone(compatible);
            Profile(missingMode).Remove("mode");
            ExpectInvalid(missingMode, 0, "compatible profile without required mode");

            JsonObject unknownMode = Clone(compatible);
            Profile(unknownMode)["mode"] = "blind-force";
            ExpectInvalid(unknownMode, 0, "unknown compatible profile mode");

            JsonObject compatibleRemote = Clone(compatible);
            Catalog(compatibleRemote)["source"] = "CachedCurrent";
            JsonObject selectedCached = CurrentRemoteUpdateObject();
            selectedCached["source"] = "CachedCurrent";
            compatibleRemote["update"] = selectedCached;
            compatibleRemote["channel_authority"] = ChannelAuthority(CurrentRemoteUpdateObject());
            Require(Parse(compatibleRemote, 0).CanApply,
                "A signed compatible catalog was incorrectly required to share its provenance with channel authority.");
            JsonObject forgedCatalogProvenance = Clone(compatibleRemote);
            Catalog(forgedCatalogProvenance)["source"] = "Remote";
            forgedCatalogProvenance["update"]!["source"] = "CachedCurrent";
            ExpectInvalid(forgedCatalogProvenance, 0, "apply-capable catalog provenance differing from selected bundle");
            JsonObject forgedCatalogHash = Clone(compatibleRemote);
            forgedCatalogHash["update"]!["catalog_sha256"] = HashB;
            ExpectInvalid(forgedCatalogHash, 0,
                "selected signed metadata paired with a different catalog hash");
            JsonObject forgedCatalogPolicy = Clone(compatibleRemote);
            forgedCatalogPolicy["update"]!["catalog_policy"] = "release-approved-v1";
            ExpectInvalid(forgedCatalogPolicy, 0,
                "selected signed catalog paired with a profile built under a different policy");
            JsonObject forgedEmbeddedBundle = Clone(compatibleRemote);
            Catalog(forgedEmbeddedBundle)["source"] = "embedded";
            ExpectInvalid(forgedEmbeddedBundle, 0,
                "embedded catalog presented as apply-authorized by an unrelated signed bundle/head");
            JsonObject olderChannelAuthority = Clone(compatibleRemote);
            olderChannelAuthority["channel_authority"]!["sequence"] = 44;
            ExpectInvalid(olderChannelAuthority, 0,
                "channel authority sequence older than the selected signed catalog");
            JsonObject equalSequenceDifferentPayload = Clone(compatibleRemote);
            equalSequenceDifferentPayload["channel_authority"]!["payload_sha256"] = HashB;
            ExpectInvalid(equalSequenceDifferentPayload, 0,
                "same signed sequence with a different authority payload identity");
            JsonObject cachedOlderThanAuthority = Clone(compatibleRemote);
            cachedOlderThanAuthority["channel_authority"]!["sequence"] = 46;
            cachedOlderThanAuthority["channel_authority"]!["release_id"] = "invokersru-data-00000046";
            cachedOlderThanAuthority["channel_authority"]!["artifact_id"] = "ru-RU-00000046";
            cachedOlderThanAuthority["channel_authority"]!["payload_sha256"] = HashB;
            ExpectInvalid(cachedOlderThanAuthority, 0,
                "cached-current catalog older than its separate channel authority");

            JsonObject compatibleTranslationUpdate = Clone(compatibleRemote);
            compatibleTranslationUpdate["status"] = "PatchedByThisTool";
            compatibleTranslationUpdate["message"] = "A newer compatible catalog is selected.";
            compatibleTranslationUpdate["observed"]!["base_sha256"] = HashB;
            compatibleTranslationUpdate["translation_update_available"] = true;
            compatibleTranslationUpdate["translation_update_kind"] = "content";
            compatibleTranslationUpdate["state"] = StateObject();
            Profile(compatibleTranslationUpdate)["expected_output_sha256"] = new string('E', 64);
            compatibleTranslationUpdate["plan"] = "READY_TO_UPDATE_TRANSLATION";
            compatibleTranslationUpdate["can_apply"] = true;
            compatibleTranslationUpdate["can_restore"] = true;
            compatibleTranslationUpdate["restore_recovery_authorized"] = true;
            Require(Parse(compatibleTranslationUpdate, 0).TranslationUpdateAvailable,
                "A compatible-revision catalog update was rejected by the GUI contract.");

            JsonObject metadataOnlyUpdate = Clone(compatibleTranslationUpdate);
            metadataOnlyUpdate["translation_update_kind"] = "metadata-only";
            metadataOnlyUpdate["observed"]!["base_sha256"] = new string('E', 64);
            metadataOnlyUpdate["state"]!["patched_sha256"] = new string('E', 64);
            metadataOnlyUpdate["state"]!["translations_sha256"] = new string('D', 64);
            metadataOnlyUpdate["state"]!["applied_translations"] = 41_036;
            Require(Parse(metadataOnlyUpdate, 0).TranslationUpdateKind == "metadata-only",
                "An equivalent-output state metadata update with differing historical counts was not represented distinctly.");

            JsonObject contentWithoutChangedBytes = Clone(compatibleTranslationUpdate);
            contentWithoutChangedBytes["observed"]!["base_sha256"] = new string('E', 64);
            contentWithoutChangedBytes["state"]!["patched_sha256"] = new string('E', 64);
            ExpectInvalid(contentWithoutChangedBytes, 0,
                "content update whose selected output is already installed");

            JsonObject metadataWithoutChangedMetadata = Clone(metadataOnlyUpdate);
            metadataWithoutChangedMetadata["state"]!["translations_sha256"] = HashA;
            metadataWithoutChangedMetadata["state"]!["applied_translations"] = 41_037;
            ExpectInvalid(metadataWithoutChangedMetadata, 0,
                "metadata-only update whose recorded metadata already matches the profile");

            JsonObject badMetadataParity = Clone(metadataOnlyUpdate);
            badMetadataParity["translation_update_available"] = false;
            ExpectInvalid(badMetadataParity, 0, "metadata-only update without translation update flag");

            JsonObject blockedCompatibleUpdate = Clone(compatibleTranslationUpdate);
            blockedCompatibleUpdate["update_problem"] = "The selected signed data cannot authorize mutation.";
            blockedCompatibleUpdate["update_problem_blocks_apply"] = true;
            blockedCompatibleUpdate["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            blockedCompatibleUpdate["can_apply"] = false;
            Require(!Parse(blockedCompatibleUpdate, 0).CanApply
                && Parse(blockedCompatibleUpdate, 0).CanRestore,
                "A blocked compatible catalog update did not remain restore-only.");

            JsonObject nonBlockingWarning = Clone(ready);
            nonBlockingWarning["update_problem"] = "Network is unavailable; trusted embedded bootstrap remains usable.";
            Require(Parse(nonBlockingWarning, 0).CanApply,
                "A typed non-blocking network warning disabled trusted embedded bootstrap.");
            string refreshJson = new JsonObject
            {
                ["schema"] = 1,
                ["configured"] = true,
                ["network_status"] = "failed",
                ["error"] = "Synthetic network failure",
                ["source"] = "CachedCurrent",
                ["update"] = null
            }.ToJsonString();
            Require(CliPlanResult.ExtractUpdateRefreshWarning(new CliCommandResult(0, refreshJson, string.Empty))
                    == "Synthetic network failure",
                "A real update-refresh warning was not propagated into GUI presentation state.");

            JsonObject blockingProblem = Clone(ready);
            blockingProblem["update_problem"] = "Accepted channel state no longer authenticates.";
            blockingProblem["update_problem_blocks_apply"] = true;
            blockingProblem["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            blockingProblem["can_apply"] = false;
            Require(!Parse(blockingProblem, 0).CanApply,
                "A typed blocking channel problem did not disable apply.");

            JsonObject blockingWithoutMessage = Clone(blockingProblem);
            blockingWithoutMessage["update_problem"] = null;
            ExpectInvalid(blockingWithoutMessage, 0, "blocking update problem without a message");

            JsonObject blockingStillReady = Clone(blockingProblem);
            blockingStillReady["plan"] = "READY_TO_APPLY";
            blockingStillReady["can_apply"] = true;
            ExpectInvalid(blockingStillReady, 0, "blocking update problem with ready permissions");

            JsonObject nonBlockingRefusal = Clone(nonBlockingWarning);
            nonBlockingRefusal["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            nonBlockingRefusal["can_apply"] = false;
            ExpectInvalid(nonBlockingRefusal, 0, "non-blocking warning presented as authority refusal");

            JsonObject catalogMissing = Clone(ready);
            catalogMissing["catalog"] = new JsonObject
            {
                ["source"] = "embedded",
                ["present"] = false,
                ["regular_file"] = false,
                ["sha256"] = null,
                ["exact_match"] = false
            };
            catalogMissing["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            catalogMissing["can_apply"] = false;
            catalogMissing["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "catalog-sha256",
                ["current"] = "missing",
                ["expected"] = HashA
            };
            Require(!Parse(catalogMissing, 0).CanApply,
                "A coherent missing-catalog refusal was not accepted for display.");

            JsonObject conflict = Clone(ready);
            conflict["process_conflicts"] = new JsonArray("Invokers (1234; C:\\Game\\Invokers.exe)");
            conflict["plan"] = "REFUSE_CLOSE_GAME_AND_LAUNCHER";
            conflict["can_apply"] = false;
            Require(!Parse(conflict, 0).CanApply,
                "A coherent running-game refusal was not accepted for display.");

            JsonObject patched = Clone(ready);
            patched["status"] = "PatchedByThisTool";
            patched["message"] = "Already patched.";
            patched["observed"]!["base_sha256"] = HashB;
            patched["plan"] = "NOOP_OR_RESTORE";
            patched["can_apply"] = false;
            patched["can_restore"] = true;
            patched["restore_recovery_authorized"] = true;
            patched["state"] = StateObject();
            Require(Parse(patched, 0).CanRestore,
                "A valid patched state was not accepted for restore.");

            JsonObject freshLkg = Clone(ready);
            freshLkg["schema"] = 3;
            Catalog(freshLkg)["source"] = "LastKnownGood";
            freshLkg["update"] = CurrentLkgUpdateObject();
            freshLkg["channel_authority"] = ChannelAuthority(CurrentLkgUpdateObject());
            freshLkg["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            freshLkg["can_apply"] = false;
            Require(!Parse(freshLkg, 0).CanApply,
                "A current LKG was allowed to establish a fresh installation without its exact recorded state.");

            JsonObject exactLkgReapply = Clone(freshLkg);
            exactLkgReapply["status"] = "PatchSupersededByOfficialUpdate";
            exactLkgReapply["message"] = "Official game cache replaced the exact recorded patch.";
            exactLkgReapply["observed"]!["base_sha256"] = HashC;
            exactLkgReapply["state"] = StateObject();
            exactLkgReapply["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "official-base-refresh",
                ["current"] = HashC,
                ["expected"] = HashB
            };
            exactLkgReapply["plan"] = "READY_TO_REAPPLY_AFTER_GAME_UPDATE";
            exactLkgReapply["can_apply"] = true;
            Require(Parse(exactLkgReapply, 0).CanApply,
                "A current exact LKG could not maintain its recorded installation after an official update.");

            JsonObject compatibleLkgReapply = Clone(exactLkgReapply);
            Profile(compatibleLkgReapply)["mode"] = "compatible-revision";
            Require(Parse(compatibleLkgReapply, 0).CanApply,
                "A compatible-revision LKG could not maintain its exact recorded artifact.");

            JsonObject sameIdDifferentArtifact = Clone(exactLkgReapply);
            string differentOutput = new string('D', 64);
            string differentCatalog = new string('E', 64);
            sameIdDifferentArtifact["status"] = "PatchSupersededByCatalogUpdate";
            sameIdDifferentArtifact["message"] = "Allowlisted older translation artifact.";
            sameIdDifferentArtifact["translation_update_available"] = true;
            sameIdDifferentArtifact["translation_update_kind"] = "content";
            sameIdDifferentArtifact["observed"]!["base_sha256"] = differentOutput;
            sameIdDifferentArtifact["state"]!["patched_sha256"] = differentOutput;
            sameIdDifferentArtifact["state"]!["translations_sha256"] = differentCatalog;
            sameIdDifferentArtifact["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "catalog-sha256",
                ["current"] = differentCatalog,
                ["expected"] = HashA
            };
            sameIdDifferentArtifact["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            sameIdDifferentArtifact["can_apply"] = false;
            sameIdDifferentArtifact["can_restore"] = true;
            sameIdDifferentArtifact["restore_recovery_authorized"] = true;
            CliPlanResult blockedLkgDowngrade = Parse(sameIdDifferentArtifact, 0);
            Require(!blockedLkgDowngrade.CanApply && blockedLkgDowngrade.CanRestore,
                "A current LKG was allowed to downgrade a same-id different artifact or hid safe restore.");
            JsonObject catalogSupersededWithoutKind = Clone(sameIdDifferentArtifact);
            catalogSupersededWithoutKind["translation_update_available"] = false;
            catalogSupersededWithoutKind["translation_update_kind"] = "none";
            ExpectInvalid(catalogSupersededWithoutKind, 0,
                "catalog-superseded state without canonical content-update kind");

            JsonObject tooOld = Clone(ready);
            tooOld["schema"] = 3;
            tooOld["patcher_version"] = "3.1.0";
            Catalog(tooOld)["source"] = "LastKnownGood";
            tooOld["update"] = TooOldUpdateObject();
            tooOld["channel_authority"] = ChannelAuthority(TooOldUpdateObject());
            tooOld["update_problem"] = "The signed channel requires a newer patcher.";
            tooOld["update_problem_blocks_apply"] = true;
            tooOld["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            tooOld["can_apply"] = false;
            CliPlanResult blockedByAuthority = Parse(tooOld, 0);
            Require(!blockedByAuthority.CanApply
                && blockedByAuthority.ChannelAuthority?.PatcherDisposition == "TooOld",
                "A newest too-old channel head did not block an older selected LKG catalog.");

            JsonObject freshTooOld = Clone(ready);
            freshTooOld["schema"] = 3;
            freshTooOld["patcher_version"] = "3.1.0";
            freshTooOld["catalog"] = new JsonObject
            {
                ["source"] = "ChannelHead",
                ["present"] = false,
                ["regular_file"] = false,
                ["sha256"] = null,
                ["exact_match"] = false
            };
            freshTooOld["update"] = null;
            freshTooOld["channel_authority"] = ChannelAuthority(TooOldUpdateObject());
            freshTooOld["update_problem"] = "The signed channel requires a newer patcher.";
            freshTooOld["update_problem_blocks_apply"] = true;
            freshTooOld["diagnostic"] = new JsonObject
            {
                ["kind"] = "translation-data",
                ["component"] = "catalog-sha256",
                ["current"] = "missing",
                ["expected"] = HashA
            };
            freshTooOld["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            freshTooOld["can_apply"] = false;
            Require(Parse(freshTooOld, 0).ChannelAuthority?.PatcherDisposition == "TooOld",
                "A fresh too-old metadata-only channel head disappeared from the GUI contract.");
            JsonObject forgedTooOldProfileHash = Clone(freshTooOld);
            forgedTooOldProfileHash["profile"]!["catalog_sha256"] = HashC;
            ExpectInvalid(forgedTooOldProfileHash, 0,
                "GUI accepted ChannelHead metadata whose profile catalog hash was not signed by that authority.");
            JsonObject forgedTooOldProfilePolicy = Clone(freshTooOld);
            forgedTooOldProfilePolicy["profile"]!["translation_policy"] = "release-approved";
            ExpectInvalid(forgedTooOldProfilePolicy, 0,
                "GUI accepted ChannelHead metadata whose profile policy was not signed by that authority.");

            JsonObject corruptAcceptedHead = Clone(ready);
            corruptAcceptedHead["schema"] = 3;
            Catalog(corruptAcceptedHead)["source"] = "embedded";
            corruptAcceptedHead["update_problem"] = "Newest accepted metadata no longer authenticates.";
            corruptAcceptedHead["update_problem_blocks_apply"] = true;
            corruptAcceptedHead["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            corruptAcceptedHead["can_apply"] = false;
            Require(!Parse(corruptAcceptedHead, 0).CanApply,
                "A corrupt accepted channel head allowed the embedded bootstrap catalog to be applied.");

            JsonObject corruptHeadPatched = Clone(patched);
            corruptHeadPatched["schema"] = 3;
            Catalog(corruptHeadPatched)["source"] = "embedded";
            corruptHeadPatched["update_problem"] = "Newest accepted metadata no longer authenticates.";
            corruptHeadPatched["update_problem_blocks_apply"] = true;
            corruptHeadPatched["plan"] = "NOOP_OR_RESTORE";
            corruptHeadPatched["can_restore"] = true;
            corruptHeadPatched["restore_recovery_authorized"] = true;
            Require(Parse(corruptHeadPatched, 0).CanRestore,
                "A corrupt accepted channel head hid restoration of an independently authenticated embedded patch.");

            JsonObject tooOldPatched = Clone(tooOld);
            tooOldPatched["status"] = "PatchedByThisTool";
            tooOldPatched["message"] = "Already patched from authenticated history.";
            tooOldPatched["observed"]!["base_sha256"] = HashB;
            tooOldPatched["update_problem"] = "A newer patcher is required for future installation data.";
            tooOldPatched["update_problem_blocks_apply"] = true;
            tooOldPatched["plan"] = "NOOP_OR_RESTORE";
            tooOldPatched["can_restore"] = true;
            tooOldPatched["restore_recovery_authorized"] = true;
            tooOldPatched["state"] = StateObject();
            Require(Parse(tooOldPatched, 0).CanRestore,
                "A too-old channel head incorrectly hid restoration of an authenticated installed patch.");

            JsonObject recovery = Clone(ready);
            recovery["status"] = "RecoveryRequired";
            recovery["message"] = "Recovery required.";
            recovery["plan"] = "RECOVERY_REQUIRED";
            recovery["can_apply"] = false;
            recovery["can_recover"] = true;
            recovery["restore_recovery_authorized"] = true;
            recovery["journal"] = new JsonObject
            {
                ["operation"] = "runtime-cache-apply",
                ["phase"] = "ReplacementCommitted",
                ["transaction_id"] = "0123456789abcdef0123456789abcdef"
            };
            recovery["diagnostic"] = new JsonObject
            {
                ["kind"] = "local-state",
                ["component"] = "journal",
                ["current"] = "ReplacementCommitted",
                ["expected"] = "authenticated recovery journal"
            };
            Require(Parse(recovery, 5).CanRecover,
                "RecoveryRequired with the CLI's intentional exit code 5 was rejected.");

            JsonObject historicalRecovery = Clone(recovery);
            historicalRecovery["schema"] = 3;
            historicalRecovery["profile"]!["id"] = "runtime-cache-win64-historical-profile";
            Catalog(historicalRecovery)["source"] = "embedded";
            Catalog(historicalRecovery)["sha256"] = HashC;
            Catalog(historicalRecovery)["exact_match"] = false;
            historicalRecovery["update"] = null;
            historicalRecovery["channel_authority"] = null;
            historicalRecovery["update_problem"] = null;
            Require(Parse(historicalRecovery, 5).CanRecover,
                "A signed historical recovery profile was confused with the current catalog source.");

            JsonObject unauthenticatedJournal = Clone(ready);
            unauthenticatedJournal["status"] = "InconsistentState";
            unauthenticatedJournal["message"] = "Active transaction journal has no unique authenticated recovery profile.";
            unauthenticatedJournal["local_problem"] = "journal-authentication";
            unauthenticatedJournal["update_problem"] = null;
            unauthenticatedJournal["update_problem_blocks_apply"] = false;
            unauthenticatedJournal["diagnostic"] = new JsonObject
            {
                ["kind"] = "local-state",
                ["component"] = "journal-authentication",
                ["current"] = "untrusted",
                ["expected"] = "uniquely authenticated recovery journal"
            };
            unauthenticatedJournal["plan"] = "REFUSE_UNKNOWN_OR_INCONSISTENT";
            unauthenticatedJournal["can_apply"] = false;
            CliPlanResult refusedRecovery = Parse(unauthenticatedJournal, 5);
            Require(!refusedRecovery.CanRecover && refusedRecovery.PlanAction == "REFUSE_UNKNOWN_OR_INCONSISTENT",
                "An unauthenticated journal was exposed as recoverable or mislabeled as a network/catalog refusal.");

            JsonObject forgedJournalCause = Clone(ready);
            forgedJournalCause["local_problem"] = "journal-authentication";
            ExpectInvalid(forgedJournalCause, 0,
                "journal-authentication cause on a compatible original installation");

            JsonObject unknownMember = Clone(ready);
            unknownMember["unexpected"] = true;
            ExpectInvalid(unknownMember, 0, "unknown top-level member");

            string duplicate = ready.ToJsonString();
            duplicate = duplicate.Insert(duplicate.LastIndexOf('}'), ",\"can_apply\":false");
            ExpectInvalid(duplicate, 0, "duplicate member");

            JsonObject nullProfile = Clone(ready);
            nullProfile["profile"] = null;
            ExpectInvalid(nullProfile, 0, "null profile");

            JsonObject nullObserved = Clone(ready);
            nullObserved["observed"] = null;
            ExpectInvalid(nullObserved, 0, "null observed identity");

            JsonObject nullCatalog = Clone(ready);
            nullCatalog["catalog"] = null;
            ExpectInvalid(nullCatalog, 0, "null catalog identity");

            JsonObject nullProcesses = Clone(ready);
            nullProcesses["process_conflicts"] = null;
            ExpectInvalid(nullProcesses, 0, "null process list");

            JsonObject nullMessage = Clone(ready);
            nullMessage["message"] = null;
            ExpectInvalid(nullMessage, 0, "null required scalar");

            JsonObject missingWriteFlag = Clone(ready);
            missingWriteFlag.Remove("installation_writes_enabled");
            ExpectInvalid(missingWriteFlag, 0, "missing write-enabled flag");

            foreach (string requiredNullable in new[] { "update", "channel_authority", "update_problem", "local_problem" })
            {
                JsonObject missing = Clone(ready);
                missing.Remove(requiredNullable);
                ExpectInvalid(missing, 0, $"missing required nullable {requiredNullable} member");
            }
            foreach (string requiredFlag in new[]
                { "translation_update_available", "translation_update_kind", "update_problem_blocks_apply", "restore_recovery_authorized" })
            {
                JsonObject missing = Clone(ready);
                missing.Remove(requiredFlag);
                ExpectInvalid(missing, 0, $"missing required {requiredFlag} flag");
            }

            JsonObject missingCatalogSource = Clone(ready);
            Catalog(missingCatalogSource).Remove("source");
            ExpectInvalid(missingCatalogSource, 0, "missing selected catalog provenance");

            JsonObject missingProcesses = Clone(ready);
            missingProcesses.Remove("process_conflicts");
            ExpectInvalid(missingProcesses, 0, "missing process list");

            JsonObject missingObservedVersion = Clone(ready);
            missingObservedVersion["observed"]!.AsObject().Remove("game_version");
            ExpectInvalid(missingObservedVersion, 0, "missing observed version member");

            JsonObject missingObservedBaseContent = Clone(ready);
            missingObservedBaseContent["observed"]!["base_content"] = null;
            ExpectInvalid(missingObservedBaseContent, 0, "recognized tuple without observed UK content version");

            JsonObject missingCatalogPresence = Clone(ready);
            Catalog(missingCatalogPresence).Remove("present");
            ExpectInvalid(missingCatalogPresence, 0, "missing catalog presence member");

            JsonObject missingCertifiedFlag = Clone(ready);
            Profile(missingCertifiedFlag).Remove("certified");
            ExpectInvalid(missingCertifiedFlag, 0, "missing profile certification member");

            JsonObject wrongExit = Clone(ready);
            ExpectInvalid(wrongExit, 5, "contradictory exit code");

            JsonObject falsePermission = Clone(ready);
            falsePermission["can_apply"] = false;
            ExpectInvalid(falsePermission, 0, "contradictory apply permission");

            JsonObject originalWithPatchedHash = Clone(ready);
            originalWithPatchedHash["observed"]!["base_sha256"] = HashB;
            ExpectInvalid(originalWithPatchedHash, 0,
                "compatible original whose observed target equals output instead of official base");

            JsonObject writesDisabled = Clone(ready);
            writesDisabled["installation_writes_enabled"] = false;
            ExpectInvalid(writesDisabled, 0, "write-disabled ready action");

            JsonObject badCount = Clone(ready);
            Profile(badCount)["english_fallbacks"] = 2;
            ExpectInvalid(badCount, 0, "composition sum mismatch");

            JsonObject negativeCount = Clone(ready);
            Profile(negativeCount)["base_fallbacks"] = -1;
            ExpectInvalid(negativeCount, 0, "negative certified fallback");

            JsonObject tooManyReviewFallbacks = Clone(ready);
            Profile(tooManyReviewFallbacks)["needs_review_fallbacks"] = 2;
            ExpectInvalid(tooManyReviewFallbacks, 0, "review fallback outside English fallback");

            JsonObject catalogMismatchClaimedExact = Clone(ready);
            Catalog(catalogMismatchClaimedExact)["sha256"] = HashB;
            ExpectInvalid(catalogMismatchClaimedExact, 0, "false exact catalog claim");

            JsonObject exactCatalogClaimedMismatch = Clone(ready);
            Catalog(exactCatalogClaimedMismatch)["exact_match"] = false;
            exactCatalogClaimedMismatch["plan"] = "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
            exactCatalogClaimedMismatch["can_apply"] = false;
            ExpectInvalid(exactCatalogClaimedMismatch, 0, "exact catalog hash claimed as mismatch");

            JsonObject missingCatalogStillReady = Clone(catalogMissing);
            missingCatalogStillReady["plan"] = "READY_TO_APPLY";
            missingCatalogStillReady["can_apply"] = true;
            ExpectInvalid(missingCatalogStillReady, 0, "ready action without catalog");

            JsonObject conflictStillWritable = Clone(conflict);
            conflictStillWritable["can_apply"] = true;
            ExpectInvalid(conflictStillWritable, 0, "apply permission with running process");

            JsonObject patchedWithoutState = Clone(patched);
            patchedWithoutState["state"] = null;
            ExpectInvalid(patchedWithoutState, 0, "restore permission without state");

            JsonObject patchedWithoutAuthority = Clone(patched);
            patchedWithoutAuthority["restore_recovery_authorized"] = false;
            patchedWithoutAuthority["can_restore"] = false;
            patchedWithoutAuthority["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            ExpectInvalid(patchedWithoutAuthority, 0, "authenticated patched status without restoration authority");

            JsonObject incompletePatchedState = Clone(patched);
            incompletePatchedState["state"]!.AsObject().Remove("build_id");
            ExpectInvalid(incompletePatchedState, 0, "incomplete installed state");

            JsonObject mismatchedPatchedState = Clone(patched);
            mismatchedPatchedState["state"]!["build_id"] = "different-runtime-cache-profile";
            ExpectInvalid(mismatchedPatchedState, 0, "installed state from another profile");

            JsonObject patchedWrongOriginal = Clone(patched);
            patchedWrongOriginal["state"]!["original_sha256"] = HashA;
            ExpectInvalid(patchedWrongOriginal, 0, "installed state with original hash differing from profile base");

            JsonObject patchedWrongCatalog = Clone(patched);
            patchedWrongCatalog["state"]!["translations_sha256"] = HashC;
            ExpectInvalid(patchedWrongCatalog, 0, "installed state with catalog hash differing from current profile");

            JsonObject recoveryWithoutJournal = Clone(recovery);
            recoveryWithoutJournal["journal"] = null;
            ExpectInvalid(recoveryWithoutJournal, 5, "recovery permission without journal");

            JsonObject incompleteRecoveryJournal = Clone(recovery);
            incompleteRecoveryJournal["journal"]!.AsObject().Remove("phase");
            ExpectInvalid(incompleteRecoveryJournal, 5, "incomplete recovery journal");

            JsonObject unknownRecoveryPhase = Clone(recovery);
            unknownRecoveryPhase["journal"]!["phase"] = "FutureUnsafePhase";
            ExpectInvalid(unknownRecoveryPhase, 5, "unknown recovery phase");

            JsonObject unknownStatus = Clone(ready);
            unknownStatus["status"] = "FutureStatus";
            ExpectInvalid(unknownStatus, 0, "unknown status");

            JsonObject unknownAction = Clone(ready);
            unknownAction["plan"] = "FUTURE_ACTION";
            ExpectInvalid(unknownAction, 0, "unknown action");

            JsonObject contradictoryPatcherDisposition = Clone(ready);
            contradictoryPatcherDisposition["patcher_version"] = "3.1.0";
            contradictoryPatcherDisposition["update"] = CurrentRemoteUpdateObject();
            contradictoryPatcherDisposition["channel_authority"] = ChannelAuthority(TooOldUpdateObject());
            contradictoryPatcherDisposition["channel_authority"]!["patcher_disposition"] = "Current";
            contradictoryPatcherDisposition["update_problem"] = "A newer patcher is required.";
            contradictoryPatcherDisposition["update_problem_blocks_apply"] = true;
            contradictoryPatcherDisposition["plan"] = "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
            contradictoryPatcherDisposition["can_apply"] = false;
            ExpectInvalid(contradictoryPatcherDisposition, 0, "patcher disposition contradicting signed version bounds");
        }

        private static void RunnerTimeoutOwnChildOnlyCheck()
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ping = Path.Combine(systemDirectory, "PING.EXE");
            Require(File.Exists(ping), $"Windows ping executable is missing: {ping}");

            string root = Path.Combine(Path.GetTempPath(), $"invokersru-gui-runner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string companion = Path.Combine(root, CliRunner.CliFileName);
            File.Copy(ping, companion, overwrite: false);
            Process? unrelated = null;
            try
            {
                unrelated = Process.Start(new ProcessStartInfo
                {
                    FileName = ping,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "127.0.0.1", "-n", "30", "-w", "1000" }
                }) ?? throw new InvalidOperationException("Could not start the unrelated timeout sentinel process.");

                var runner = new CliRunner(root, TimeSpan.FromMilliseconds(500));
                bool timedOut = false;
                try
                {
                    _ = runner.RunAsync("127.0.0.1", new[] { "-n", "30", "-w", "1000" })
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("не завершил операцию", StringComparison.Ordinal))
                {
                    timedOut = true;
                }

                Require(timedOut, "A non-terminating companion did not return the bounded Russian timeout error.");
                Require(!unrelated.HasExited,
                    "Timing out the companion terminated an unrelated process with the same executable image.");
            }
            finally
            {
                if (unrelated != null)
                {
                    try
                    {
                        if (!unrelated.HasExited)
                        {
                            unrelated.Kill(entireProcessTree: true);
                            unrelated.WaitForExit(5_000);
                        }
                    }
                    finally
                    {
                        unrelated.Dispose();
                    }
                }

                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static JsonObject ReadyResponse()
        {
            return new JsonObject
            {
                ["schema"] = 3,
                ["patcher_version"] = "3.0.0.0",
                ["installation_writes_enabled"] = true,
                ["status"] = "CompatibleOriginal",
                ["message"] = "Exact compatible original.",
                ["cache_root"] = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invokersru-contract-cache")),
                ["observed"] = new JsonObject
                {
                    ["english_sha256"] = HashA,
                    ["base_sha256"] = HashC,
                    ["stamp_sha256"] = HashC,
                    ["game_version"] = "0.60.1247",
                    ["english_content"] = "Prod_0.60.0_68",
                    ["base_content"] = "Prod_0.60.0_68",
                    ["english_schema"] = 4,
                    ["base_schema"] = 4,
                    ["english_content_guid"] = ContentGuid,
                    ["base_content_guid"] = ContentGuid,
                    ["english_locale_id"] = 1,
                    ["english_locale_revision"] = 0x12345678,
                    ["english_release_revision"] = 68,
                    ["base_locale_id"] = 8,
                    ["base_locale_revision"] = 0x87654321,
                    ["base_release_revision"] = 68,
                    ["entry_count"] = 41_292,
                    ["ordered_keyset_sha256"] = HashC
                },
                ["catalog"] = new JsonObject
                {
                    ["source"] = "embedded",
                    ["present"] = true,
                    ["regular_file"] = true,
                    ["sha256"] = HashA,
                    ["exact_match"] = true
                },
                ["profile"] = new JsonObject
                {
                    ["id"] = "runtime-cache-win64-0.60.1247-prod68",
                    ["mode"] = "exact",
                    ["game_version"] = "0.60.1247",
                    ["readiness"] = "ready",
                    ["certified"] = true,
                    ["translation_policy"] = "community-preview-all-drafts",
                    ["content_guid"] = ContentGuid,
                    ["loc1_schema"] = 4,
                    ["english_content"] = "Prod_0.60.0_68",
                    ["base_content"] = "Prod_0.60.0_68",
                    ["english_sha256"] = HashA,
                    ["stamp_sha256"] = HashC,
                    ["english_locale_id"] = 1,
                    ["english_locale_revision"] = 0x12345678,
                    ["english_release_revision"] = 68,
                    ["base_locale_id"] = 8,
                    ["base_locale_revision"] = 0x87654321,
                    ["base_release_revision"] = 68,
                    ["ordered_keyset_sha256"] = HashC,
                    ["base_sha256"] = HashC,
                    ["catalog_sha256"] = HashA,
                    ["expected_output_sha256"] = HashB,
                    ["entry_count"] = 41_292,
                    ["applied_translations"] = 41_037,
                    ["english_fallbacks"] = 1,
                    ["base_fallbacks"] = 254,
                    ["needs_review_fallbacks"] = 0
                },
                ["diagnostic"] = new JsonObject
                {
                    ["kind"] = "none",
                    ["component"] = "none",
                    ["current"] = null,
                    ["expected"] = null
                },
                ["local_problem"] = null,
                ["update"] = null,
                ["channel_authority"] = null,
                ["translation_update_available"] = false,
                ["translation_update_kind"] = "none",
                ["update_problem"] = null,
                ["update_problem_blocks_apply"] = false,
                ["restore_recovery_authorized"] = false,
                ["state"] = null,
                ["journal"] = null,
                ["process_conflicts"] = new JsonArray(),
                ["plan"] = "READY_TO_APPLY",
                ["can_apply"] = true,
                ["can_restore"] = false,
                ["can_recover"] = false
            };
        }

        private static JsonObject StateObject()
        {
            return new JsonObject
            {
                ["build_id"] = "runtime-cache-win64-0.60.1247-prod68",
                ["applied_translations"] = 41_037,
                ["applied_at"] = "2026-08-20T18:26:44Z",
                ["patched_sha256"] = HashB,
                ["original_sha256"] = HashC,
                ["translations_sha256"] = HashA
            };
        }

        private static JsonObject TooOldUpdateObject(string source = "LastKnownGood")
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new JsonObject
            {
                ["source"] = source,
                ["sequence"] = 45,
                ["payload_sha256"] = HashC,
                ["release_id"] = "invokersru-data-00000045",
                ["artifact_id"] = "ru-RU-00000045",
                ["catalog_sha256"] = HashA,
                ["catalog_policy"] = "validated-preview-v1",
                ["issued_utc"] = now.AddDays(-1),
                ["expires_utc"] = now.AddDays(20),
                ["expired"] = false,
                ["patcher_disposition"] = "TooOld",
                ["minimum_patcher_version"] = "4.0.0",
                ["latest_patcher_version"] = "4.1.0",
                ["download_page"] = "https://github.com/Braintfy/ruslocal-invokers/releases/latest",
                ["notes_ru"] = "Требуется новая версия патчера."
            };
        }

        private static JsonObject CurrentLkgUpdateObject()
        {
            JsonObject update = TooOldUpdateObject("LastKnownGood");
            update["patcher_disposition"] = "Current";
            update["minimum_patcher_version"] = "3.0.0.0";
            update["latest_patcher_version"] = "3.0.0.0";
            return update;
        }

        private static JsonObject CurrentRemoteUpdateObject()
        {
            JsonObject update = CurrentLkgUpdateObject();
            update["source"] = "Remote";
            return update;
        }

        private static JsonObject ChannelAuthority(JsonObject update)
        {
            JsonObject authority = Clone(update);
            authority["source"] = "ChannelHead";
            return authority;
        }

        private static CliPlanResult Parse(JsonObject response, int exitCode)
        {
            return Parse(response.ToJsonString(), exitCode);
        }

        private static CliPlanResult Parse(string response, int exitCode)
        {
            return CliPlanResult.Parse(new CliCommandResult(exitCode, response, string.Empty));
        }

        private static void ExpectInvalid(JsonObject response, int exitCode, string label)
        {
            ExpectInvalid(response.ToJsonString(), exitCode, label);
        }

        private static void ExpectInvalid(string response, int exitCode, string label)
        {
            try
            {
                _ = Parse(response, exitCode);
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException($"GUI plan parser accepted {label}.");
        }

        private static JsonObject Clone(JsonObject value)
        {
            return value.DeepClone().AsObject();
        }

        private static JsonObject Profile(JsonObject response)
        {
            return response["profile"]?.AsObject()
                ?? throw new InvalidOperationException("Test response has no profile object.");
        }

        private static JsonObject Catalog(JsonObject response)
        {
            return response["catalog"]?.AsObject()
                ?? throw new InvalidOperationException("Test response has no catalog object.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
