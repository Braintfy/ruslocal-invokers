using InvokersRu.Cli;
using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InvokersRu.SmokeTests
{
    internal static class SignedCompatibleObsoleteStateSmokeTests
    {
        internal static void Run(Action<string> passed)
        {
            // All writer checks use temporary copies and the isolated mutation-smoke build only.
            if (!MutationCapability.IsTestWriteBuild) return;
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-compatible-obsolete-{Guid.NewGuid():N}");
            string cache = Path.Combine(root, "cache");
            string statePath = Path.Combine(root, "state", "state.v1.json");
            Directory.CreateDirectory(cache);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            try
            {
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cache);
                // Numeric-family clients use .src, not the legacy downloaded .ver stamp. An empty
                // cache cannot yet select that layout by observing the target header.
                stampPath = Path.Combine(cache, "uk_UA.bin.src");
                byte[] english = CreateLoc1("0.61.2", 1, 10, "Open", "Exit");
                byte[] uk10 = CreateLoc1("0.61.2", 8, 10, "Відкрити", "Вийти");
                byte[] uk8 = CreateLoc1("0.61.2", 8, 8, "Відкрити", "Вийти");
                byte[] stamp = Encoding.UTF8.GetBytes("0.61.1506:1123186");
                File.WriteAllBytes(englishPath, english);
                File.WriteAllBytes(targetPath, uk10);
                File.WriteAllBytes(stampPath, stamp);
                string catalogPath = Path.Combine(root, "ru_RU.jsonl");
                TranslationCatalog.WriteJsonLines(catalogPath, new[]
                {
                    new TranslationRecord
                    {
                        Id = Loc1Codec.Parse(english).Entries[0].Id,
                        SourceSha256 = Hashing.Sha256Text("Open"),
                        HintSha256 = Hashing.Sha256Text("Відкрити"),
                        Translation = "Открыть", Status = "draft", Model = "obsolete-compatible-smoke",
                        PromptVersion = "synthetic-v1", Confidence = "high", NeedsReview = false,
                        RiskFlags = Array.Empty<string>(), ReviewStage = "synthetic", UpdatedAt = DateTimeOffset.UtcNow
                    }
                });
                byte[] catalog = File.ReadAllBytes(catalogPath);
                CompatibleRevisionProfileBuild signedBuild = CompatibleRevisionProfileBuilder.Build(
                    englishPath, targetPath, stampPath, "0.61.2", catalog,
                    Hashing.Sha256Bytes(catalog), "community-preview-all-drafts");
                RuntimeCacheCompatibility signedSource = RuntimeCacheService.DescribeTuple(
                    englishPath, targetPath, stampPath, "synthetic-signed-uk10");
                VerifiedSignedUpdate signed = MakeSignedFixture(signedSource, signedBuild.Profile, catalog);
                RuntimeCacheCompatibility embedded = SignedUpdateRuntimeProfileAdapter.SelectExact(
                    signed.Manifest, signedSource, Loc1Codec.Parse(uk10));
                File.WriteAllBytes(targetPath, uk8);
                var stale = new PatchState
                {
                    BuildId = "runtime-cache-old-0.60-prod97", GameRoot = Path.GetFullPath(cache),
                    TargetPath = Path.GetFullPath(targetPath), BackupPath = Path.Combine(root, "missing", "old.bin"),
                    OriginalSha256 = Hashing.Sha256Bytes(CreateLoc1("0.60.0", 8, 97, "Старе")),
                    PatchedSha256 = new string('B', 64), TranslationsSha256 = new string('C', 64),
                    AppliedTranslations = 1, AppliedAt = DateTimeOffset.UtcNow.AddDays(-1)
                };
                byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(stale);
                void Reset()
                {
                    File.WriteAllBytes(englishPath, english);
                    File.WriteAllBytes(targetPath, uk8);
                    File.WriteAllBytes(stampPath, stamp);
                    File.WriteAllBytes(statePath, stateBytes);
                    File.WriteAllBytes(catalogPath, catalog);
                }
                RuntimeUpdateResolution? Resolve(VerifiedSignedUpdate? update = null,
                    SignedUpdateBundleSource source = SignedUpdateBundleSource.CachedCurrent,
                    VerifiedSignedUpdate? authority = null, string? remoteProblem = null, bool blocking = false,
                    bool useBundle = true, bool useAuthority = true)
                {
                    update ??= signed;
                    return RuntimeUpdateResolver.TryResolveCompatibleRevision(cache, statePath, embedded,
                        catalogPath, coordinator: null,
                        useBundle ? new SignedUpdateBundle(update, catalogPath, source, authority ?? update) : null,
                        useAuthority ? authority ?? update : null, remoteProblem, blocking);
                }
                RuntimeUpdateResolution Permitted(string? warning = null)
                {
                    RuntimeUpdateResolution result = Resolve(remoteProblem: warning)
                        ?? throw new InvalidOperationException("The signed compatible obsolete-state route was not resolved.");
                    Require(result.Inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                        && result.Profile.Mode == CompatibleRevisionProfileBuilder.Mode
                        && result.Profile.BaseContentVersion == "Prod_0.61.2_8"
                        && result.Profile.ExpectedAppliedTranslations == 1
                        && result.Source == "CachedCurrent" && result.CatalogPath == catalogPath
                        && result.Bundle != null && result.CompatibilityFailure == null
                        && RuntimeUpdateAuthorization.CanApply(result, DateTimeOffset.UtcNow),
                        $"A current signed catalogue did not preserve compatible partial translation provenance/authority: status={result.Inspection.Status}; mode={result.Profile.Mode}; base={result.Profile.BaseContentVersion}; rows={result.Profile.ExpectedAppliedTranslations}; source={result.Source}; catalogue={result.CatalogPath}; bundle={result.Bundle != null}; local={result.LocalProblem}; failure={result.CompatibilityFailure?.Message}; inspection={result.Inspection.Message}; apply={RuntimeUpdateAuthorization.CanApply(result, DateTimeOffset.UtcNow)}.");
                    return result;
                }
                void Refused(string description, RuntimeUpdateResolution? result)
                {
                    Require(result == null || (result.Inspection.Status != InstallationStatus.PatchSupersededByOfficialUpdate
                        && !RuntimeUpdateAuthorization.CanApply(result, DateTimeOffset.UtcNow)),
                        description + " acquired obsolete-state mutation authority.");
                }

                Reset();
                Permitted();
                Permitted("Network unavailable; using the current verified cache.");
                Require(File.ReadAllBytes(statePath).SequenceEqual(stateBytes)
                    && File.ReadAllBytes(targetPath).SequenceEqual(uk8), "Read-only inspection changed the live copies.");
                passed("current signed catalogue permits UK8 with obsolete foreign-family state and remains usable from verified offline CachedCurrent");

                File.WriteAllBytes(targetPath, uk10);
                RuntimeUpdateResolution? exactCurrent = Resolve();
                Require(exactCurrent == null || (exactCurrent.Inspection.Status != InstallationStatus.PatchSupersededByOfficialUpdate
                    && exactCurrent.Inspection.SignedCompatibleStaleStateSha256 == null),
                    "Adaptive opaque-state migration pre-empted the stronger published exact current tuple.");
                Require(RuntimeCacheService.TryInspectSignedExactOfficialWithObsoleteState(
                    cache, statePath, signed, embedded, out RuntimeCacheInspection exactMigration)
                    && exactMigration.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && exactMigration.Profile?.Mode == "exact",
                    "Deferring opaque compatible migration lost the established exact current-state route.");
                Reset();
                passed("published exact current tuples retain stronger exact migration precedence over opaque compatible refresh");

                Refused("Last-known-good catalogue", Resolve(source: SignedUpdateBundleSource.LastKnownGood));
                Refused("Missing authenticated head", Resolve(useAuthority: false));
                Refused("Embedded-only/offline catalogue", Resolve(useBundle: false, useAuthority: false));
                Refused("Blocking network/catalogue failure", Resolve(blocking: true));
                Refused("Expired catalogue", Resolve(update: MakeSignedFixture(signedSource, signedBuild.Profile, catalog, expired: true)));
                Refused("Too-old patcher", Resolve(update: MakeSignedFixture(signedSource, signedBuild.Profile, catalog,
                    disposition: SignedUpdatePatcherDisposition.TooOld)));
                Refused("Another channel head", Resolve(authority: MakeSignedFixture(signedSource, signedBuild.Profile, catalog,
                    payload: new string('F', 64))));
                passed("LKG, missing/expired/mismatched authority and blocking update failures cannot archive obsolete compatible state");

                stale.PatchedSha256 = Hashing.Sha256Bytes(uk8);
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(stale));
                Refused("Unchanged old patched target", Resolve());
                stale.PatchedSha256 = new string('B', 64);
                stale.OriginalSha256 = Hashing.Sha256Bytes(uk8);
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(stale));
                Refused("Unchanged old original target", Resolve());
                stale.OriginalSha256 = new string('A', 64);
                stale.GameRoot = Path.Combine(root, "other-game");
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(stale));
                RuntimeUpdateResolution? foreign = Resolve();
                Refused("Foreign game root", foreign);
                Require(foreign?.Source == "CachedCurrent" && foreign.CatalogPath == catalogPath
                    && foreign.Profile.BaseContentVersion == "Prod_0.61.2_8"
                    && foreign.CompatibilityFailure?.Code == "runtime-state-untrusted",
                    "A rejected adaptive state misleadingly fell back to embedded profile/catalogue provenance.");
                stale.GameRoot = Path.GetFullPath(cache);
                stale.TargetPath = Path.Combine(cache, "another.bin");
                File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(stale));
                Refused("Foreign target", Resolve());
                Reset();
                File.WriteAllText(statePath, "{malformed", new UTF8Encoding(false));
                Refused("Malformed state", Resolve());
                Reset();
                var journal = new PatchJournal { TransactionId = Guid.NewGuid().ToString("N") };
                PatchJournalStore.Save(statePath, journal);
                Refused("Active journal", Resolve());
                PatchJournalStore.Delete(statePath, journal.TransactionId);
                string linkedState = Path.Combine(Path.GetDirectoryName(statePath)!, "original-state.json");
                File.Move(statePath, linkedState);
                bool linkCreated = false;
                try
                {
                    File.CreateSymbolicLink(statePath, linkedState);
                    linkCreated = true;
                    Refused("Reparse state", Resolve());
                }
                catch (Exception exception) when (!linkCreated
                    && exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // Windows without developer mode may not allow synthetic symlinks.
                }
                finally
                {
                    if (linkCreated) File.Delete(statePath);
                    File.Move(linkedState, statePath);
                }
                passed("unchanged originals/patches, malformed/foreign state and active transactions do not gain new compatible authority");

                Reset();
                File.WriteAllBytes(englishPath, CreateLoc1("0.62.0", 1, 1, "Open", "New source"));
                File.WriteAllBytes(targetPath, CreateLoc1("0.62.0", 8, 1, "Відкрити", "Нова підказка"));
                RuntimeUpdateResolution future = Resolve()
                    ?? throw new InvalidOperationException("Current signed catalogue did not materialize an unseen canonical family.");
                Require(future.Inspection.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && future.Profile.ContentGuid == "0.62.0" && future.Profile.ExpectedAppliedTranslations == 1
                    && future.Profile.ExpectedEnglishFallbacks == 1,
                    "A future family lost exact matching rows or translated unknown rows.");
                Refused("Future family with LKG catalogue", Resolve(source: SignedUpdateBundleSource.LastKnownGood));
                Refused("Future family with expired catalogue", Resolve(update: MakeSignedFixture(signedSource, signedBuild.Profile, catalog, expired: true)));
                Refused("Future family without an authenticated head", Resolve(useAuthority: false));
                Refused("Future family with another channel head", Resolve(authority: MakeSignedFixture(signedSource, signedBuild.Profile, catalog,
                    payload: new string('F', 64))));
                Refused("Future family requiring a newer patcher", Resolve(update: MakeSignedFixture(signedSource, signedBuild.Profile, catalog,
                    disposition: SignedUpdatePatcherDisposition.TooOld)));
                Refused("Future family with blocking update failure", Resolve(blocking: true));
                File.WriteAllBytes(targetPath, CreateLoc1("0.62.1", 8, 1, "Відкрити", "Нова підказка"));
                Refused("Mismatched EN/UK family", Resolve());
                File.WriteAllBytes(targetPath, CreateLoc1("0.62.0", 8, 1, "Відкрити"));
                Refused("Mismatched EN/UK keys", Resolve());
                File.WriteAllBytes(englishPath, CreateLoc1("not-a-family", 1, 1, "Open", "Exit"));
                File.WriteAllBytes(targetPath, CreateLoc1("not-a-family", 8, 1, "Відкрити", "Вийти"));
                Refused("Malformed family", Resolve());
                File.WriteAllBytes(englishPath, CreateLoc1("0.62.0", 1, 1, "Changed source", "New source"));
                File.WriteAllBytes(targetPath, CreateLoc1("0.62.0", 8, 1, "Відкрити", "Нова підказка"));
                RuntimeUpdateResolution? empty = Resolve();
                Refused("Future family with zero matching records", empty);
                Require(empty?.CompatibilityFailure?.Code == "catalog-no-current-matches"
                    && empty.Source == "CachedCurrent", "Zero matching rows lost their real catalogue provenance.");
                passed("only current signed catalogues support future canonical families, preserving unknown English rows and rejecting mixed corpora");

                Reset();
                RuntimeCacheInspection inspected = Permitted().Inspection;
                MutationPolicy.BindTestRuntimePaths(cache, statePath);
                File.WriteAllBytes(catalogPath, Encoding.UTF8.GetBytes("changed"));
                RequireApplyRefuses(inspected, catalogPath, statePath, targetPath, uk8, "Changed catalogue");
                Reset();
                inspected = Permitted().Inspection;
                File.WriteAllBytes(targetPath, uk10);
                RequireApplyRefuses(inspected, catalogPath, statePath, targetPath, uk10, "Changed target tuple");
                Reset();
                inspected = Permitted().Inspection;
                MutationTestHooks.BeforeSupersededStateArchive = _ => File.WriteAllText(statePath, "changed", new UTF8Encoding(false));
                RequireApplyRefuses(inspected, catalogPath, statePath, targetPath, uk8, "Changed old state under lock");
                MutationTestHooks.BeforeSupersededStateArchive = null;
                Reset();
                inspected = Permitted().Inspection;
                RuntimeCacheService.Apply(inspected, catalogPath, statePath);
                string[] archives = Directory.GetFiles(Path.Combine(root, "state", "history", "obsolete-official"), "*.json");
                Require(archives.Length == 1 && File.ReadAllBytes(archives[0]).SequenceEqual(stateBytes)
                    && RuntimeCacheService.Inspect(cache, inspected.Profile!, statePath).Status == InstallationStatus.PatchedByThisTool,
                    "Compatible apply did not preserve obsolete state verbatim or install a verified artifact.");
                RuntimeCacheService.Restore(statePath, inspected.Profile!);
                Require(File.ReadAllBytes(targetPath).SequenceEqual(uk8), "Compatible restore did not recover UK8 byte-for-byte.");
                passed("compatible obsolete-state apply refuses catalogue/tuple/state races, archives old bytes and restores its own exact new backup");
            }
            finally
            {
                MutationTestHooks.BeforeSupersededStateArchive = null;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void RequireApplyRefuses(RuntimeCacheInspection inspection, string catalog, string state,
            string target, byte[] expectedTarget, string description)
        {
            bool refused = false;
            try { RuntimeCacheService.Apply(inspection, catalog, state); }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or InvalidOperationException or UnauthorizedAccessException)
            { refused = true; }
            Require(refused && File.ReadAllBytes(target).SequenceEqual(expectedTarget), description + " was not refused before target mutation.");
        }

        private static VerifiedSignedUpdate MakeSignedFixture(RuntimeCacheCompatibility source,
            RuntimeCacheCompatibility composed, byte[] catalog, bool expired = false,
            SignedUpdatePatcherDisposition disposition = SignedUpdatePatcherDisposition.Current, string? payload = null)
        {
            var profile = new SignedUpdateCompatibilityProfile
            {
                ProfileId = "signed-uk10-only", Mode = "exact", GameVersion = source.GameVersion,
                StampValue = source.StampValue, StampSha256 = source.StampSha256, ContentGuid = source.ContentGuid,
                Loc1Schema = 4, OrderedKeysetSha256 = composed.OrderedKeysetSha256!,
                English = Corpus(source.EnglishSha256, source.EnglishContentVersion, 1, source.EnglishLocaleRevision, source.EnglishReleaseRevision),
                Base = Corpus(source.BaseSha256, source.BaseContentVersion, 8, source.BaseLocaleRevision, source.BaseReleaseRevision),
                Composition = new SignedUpdateComposition
                {
                    AppliedRu = composed.ExpectedAppliedTranslations, EnglishFallback = composed.ExpectedEnglishFallbacks,
                    BaseFallback = composed.ExpectedBaseFallbacks, MissingCatalog = composed.ExpectedEnglishFallbacks,
                    NeedsReviewFallback = 0, OutputRawSha256 = composed.ExpectedOutputSha256!
                }
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset expires = expired ? now.AddMinutes(-1) : now.AddDays(1);
            var manifest = new SignedUpdateManifest
            {
                Schema = 1, Kind = SignedUpdateVerifier.ManifestKind, Channel = "stable", Sequence = 1,
                ReleaseId = "compatible-obsolete-smoke", IssuedUtc = now.AddDays(-1).ToString("O"), ExpiresUtc = expires.ToString("O"),
                Patcher = new SignedUpdatePatcher(),
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = "fixture", Url = "https://example.invalid/fixture", Compression = "brotli",
                    CompressedBytes = 1, CompressedSha256 = new string('D', 64), UncompressedBytes = catalog.Length,
                    UncompressedSha256 = Hashing.Sha256Bytes(catalog), RecordCount = 1,
                    Format = "invokers-ru-jsonl-v1", TranslationPolicy = "validated-preview-v1"
                },
                Compatibility = new[] { profile }, RevokedReleaseIds = Array.Empty<string>()
            };
            return new VerifiedSignedUpdate(new SignedUpdateEnvelope(), manifest, payload ?? new string('E', 64),
                now.AddDays(-1), expires, expired, true, disposition, Array.Empty<SignedUpdateWarningCode>());

            SignedUpdateCorpusIdentity Corpus(string hash, string content, uint locale, uint revision, uint release) => new()
            {
                Sha256 = hash, ContentVersion = content, LocaleId = locale,
                LocaleRevisionHex = revision.ToString("X8", CultureInfo.InvariantCulture),
                ReleaseRevision = release, EntryCount = source.EntryCount
            };
        }

        private static byte[] CreateLoc1(string family, uint locale, uint release, params string[] values)
        {
            byte[] raw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(locale, release, locale + release,
                values, $"Prod_{family}_{release}");
            byte[] headerFamily = Encoding.UTF8.GetBytes(family);
            byte[] version = Encoding.UTF8.GetBytes($"Prod_{family}_{release}");
            Array.Clear(raw, 0x50, 160 - 0x50);
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)headerFamily.Length));
            headerFamily.CopyTo(raw, 0x52);
            int offset = 0x52 + headerFamily.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset, 2), checked((ushort)version.Length));
            version.CopyTo(raw, offset + 2);
            return raw;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
