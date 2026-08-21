using InvokersRu.Core.Updates;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InvokersRu.SmokeTests
{
    internal static class SignedUpdateLkgHardeningSmokeTests
    {
        private const string OnlyArgument = "--signed-update-lkg-hardening-only";
        private const string KeyId = "lkg-hardening-test-key";
        private static int _passed;

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!HasArgument(OnlyArgument)) return;

            try
            {
                Run();
                Console.WriteLine($"PASS: {_passed} signed-update LKG hardening smoke checks");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: signed-update LKG hardening smoke checks: {exception}");
                Environment.Exit(1);
            }
        }

        private static void Run()
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] publicKey = key.ExportSubjectPublicKeyInfo();
            DateTimeOffset now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
            SignedFixture oldFixture = SignAndVerify(key, publicKey, CreateManifest(12, "release-12"), now);
            SignedFixture newFixture = SignAndVerify(key, publicKey, CreateManifest(13, "release-13"), now);
            SignedFixture revokedFixture = SignAndVerify(
                key,
                publicKey,
                CreateManifest(13, "release-13-revoking", new[] { "release-12" }),
                now);

            ExactCurrentRequired(oldFixture.Update, newFixture.Update, now);
            OfflineVerificationUsesLkgIdentity(oldFixture, newFixture.Update, publicKey, now);
            RevocationBlocksApplyButRetainsMetadata(oldFixture.Update, revokedFixture.Update, now);
            UnrevokedLkgCallbackRuns(oldFixture.Update, newFixture.Update, now);
            CurrentCallbackHoldsCooperativeLock(oldFixture.Update, newFixture.Update, now);
            CurrentCallbackDetectsLockBypass(oldFixture.Update, newFixture.Update, now);
        }

        private static void ExactCurrentRequired(
            VerifiedSignedUpdate oldUpdate,
            VerifiedSignedUpdate newUpdate,
            DateTimeOffset now)
        {
            WithStore(now, (store, _) =>
            {
                Expect<InvalidOperationException>(() => store.RecordLastKnownGood(oldUpdate, now));
                store.RecordAcceptedManifest(oldUpdate);
                store.RecordAcceptedManifest(newUpdate);
                Expect<InvalidOperationException>(() => store.RecordLastKnownGood(oldUpdate, now));
                Require(store.Load()?.LastKnownGood == null,
                    "An older manifest was marked LKG after anti-rollback state advanced.");

                SignedUpdateState current = store.RecordLastKnownGood(newUpdate, now);
                Require(current.LastKnownGood?.Sequence == newUpdate.Manifest.Sequence,
                    "The exact current identity could not be recorded as LKG.");
            });
            Pass();
        }

        private static void OfflineVerificationUsesLkgIdentity(
            SignedFixture oldFixture,
            VerifiedSignedUpdate newUpdate,
            byte[] publicKey,
            DateTimeOffset now)
        {
            WithStore(now, (store, setClock) =>
            {
                store.RecordAcceptedManifest(oldFixture.Update);
                store.RecordLastKnownGood(oldFixture.Update, now);
                store.RecordAcceptedManifest(newUpdate);
                DateTimeOffset offlineNow = oldFixture.Update.ExpiresUtc.AddDays(1);
                setClock(offlineNow);

                SignedUpdateVerificationContext context = store.CreateLastKnownGoodVerificationContext(
                    offlineNow,
                    "3.1.0");
                Require(context.HighestAcceptedSequence == oldFixture.Update.Manifest.Sequence
                    && context.HighestAcceptedPayloadSha256 == oldFixture.Update.PayloadSha256
                    && context.AllowExpiredCachedManifest,
                    "Offline LKG context inherited the newer anti-rollback head.");
                VerifiedSignedUpdate offline = SignedUpdateVerifier.Verify(
                    oldFixture.EnvelopeUtf8,
                    publicKey,
                    KeyId,
                    context);
                Require(offline.Manifest.Sequence == oldFixture.Update.Manifest.Sequence && offline.IsExpired,
                    "Stored LKG could not be independently signature-verified after channel advance.");
            });
            Pass();
        }

        private static void RevocationBlocksApplyButRetainsMetadata(
            VerifiedSignedUpdate oldUpdate,
            VerifiedSignedUpdate revokingUpdate,
            DateTimeOffset now)
        {
            WithStore(now, (store, _) =>
            {
                store.RecordAcceptedManifest(oldUpdate);
                store.RecordLastKnownGood(oldUpdate, now);
                store.RecordAcceptedManifest(revokingUpdate);
                bool callbackRan = false;
                Expect<InvalidOperationException>(() => store.ExecuteWhileLastKnownGoodAllowed(
                    revokingUpdate,
                    oldUpdate,
                    () => callbackRan = true));
                Require(!callbackRan, "A revoked LKG apply callback ran.");
                SignedUpdateLastKnownGood? retained = store.Load()?.LastKnownGood;
                Require(retained?.ReleaseId == oldUpdate.Manifest.ReleaseId
                    && retained.ManifestPayloadSha256 == oldUpdate.PayloadSha256,
                    "Revocation erased historical LKG metadata required for restore.");
            });
            Pass();
        }

        private static void UnrevokedLkgCallbackRuns(
            VerifiedSignedUpdate oldUpdate,
            VerifiedSignedUpdate newUpdate,
            DateTimeOffset now)
        {
            WithStore(now, (store, _) =>
            {
                store.RecordAcceptedManifest(oldUpdate);
                store.RecordLastKnownGood(oldUpdate, now);
                store.RecordAcceptedManifest(newUpdate);
                int callbacks = 0;
                store.ExecuteWhileLastKnownGoodAllowed(newUpdate, oldUpdate, () => callbacks++);
                Require(callbacks == 1 && store.Load()?.LastKnownGood?.Sequence == oldUpdate.Manifest.Sequence,
                    "An exact unrevoked LKG callback did not run once while preserving state.");
            });
            Pass();
        }

        private static void CurrentCallbackHoldsCooperativeLock(
            VerifiedSignedUpdate oldUpdate,
            VerifiedSignedUpdate newUpdate,
            DateTimeOffset now)
        {
            WithStore(now, (store, _) =>
            {
                store.RecordAcceptedManifest(oldUpdate);
                bool competingWriterBlocked = false;
                SignedUpdateState applied = store.ExecuteWhileCurrentAndRecordLastKnownGood(
                    oldUpdate,
                    now,
                    () =>
                    {
                        try
                        {
                            store.RecordAcceptedManifest(newUpdate);
                        }
                        catch (IOException)
                        {
                            competingWriterBlocked = true;
                        }
                    });
                Require(competingWriterBlocked
                    && applied.HighestAcceptedSequence == oldUpdate.Manifest.Sequence
                    && applied.LastKnownGood?.Sequence == oldUpdate.Manifest.Sequence,
                    "Protected current callback did not hold the cooperative state lock through LKG commit.");
            });
            Pass();
        }

        private static void CurrentCallbackDetectsLockBypass(
            VerifiedSignedUpdate oldUpdate,
            VerifiedSignedUpdate newUpdate,
            DateTimeOffset now)
        {
            WithStore(now, (store, _) =>
            {
                store.RecordAcceptedManifest(oldUpdate);
                Expect<InvalidOperationException>(() => store.ExecuteWhileCurrentAndRecordLastKnownGood(
                    oldUpdate,
                    now,
                    () =>
                    {
                        string state = File.ReadAllText(store.StatePath, Encoding.UTF8);
                        state = state.Replace(
                            $"\"highest_accepted_sequence\": {oldUpdate.Manifest.Sequence}",
                            $"\"highest_accepted_sequence\": {newUpdate.Manifest.Sequence}",
                            StringComparison.Ordinal);
                        state = state.Replace(
                            oldUpdate.PayloadSha256,
                            newUpdate.PayloadSha256,
                            StringComparison.Ordinal);
                        File.WriteAllText(store.StatePath, state, new UTF8Encoding(false));
                    }));
                SignedUpdateState? bypassed = store.Load();
                Require(bypassed?.HighestAcceptedSequence == newUpdate.Manifest.Sequence
                    && bypassed.LastKnownGood == null,
                    "Final identity assertion overwrote or accepted a state file changed outside the lock protocol.");
            });
            Pass();
        }

        private static SignedFixture SignAndVerify(
            ECDsa key,
            byte[] publicKey,
            SignedUpdateManifest manifest,
            DateTimeOffset now)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
            byte[] signature = key.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.EnvelopeKind,
                KeyId = KeyId,
                Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                PayloadBase64 = Convert.ToBase64String(payload),
                SignatureBase64 = Convert.ToBase64String(signature)
            });
            VerifiedSignedUpdate update = SignedUpdateVerifier.Verify(
                envelope,
                publicKey,
                KeyId,
                new SignedUpdateVerificationContext(now, "3.1.0"));
            return new SignedFixture(envelope, update);
        }

        private static SignedUpdateManifest CreateManifest(
            ulong sequence,
            string releaseId,
            string[]? revokedReleaseIds = null)
        {
            const int entryCount = 4;
            return new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = sequence,
                ReleaseId = releaseId,
                IssuedUtc = "2026-08-21T00:00:00Z",
                ExpiresUtc = "2026-09-01T00:00:00Z",
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = "3.1.0",
                    LatestVersion = "3.1.0",
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = $"catalog-{releaseId}",
                    Url = $"https://github.com/Braintfy/ruslocal-invokers/releases/download/{releaseId}/ru_RU.jsonl.br",
                    Compression = "brotli",
                    CompressedBytes = 100,
                    CompressedSha256 = new string('C', 64),
                    UncompressedBytes = 200,
                    UncompressedSha256 = new string('D', 64),
                    RecordCount = 2,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = "validated-preview-v1"
                },
                Compatibility = new[]
                {
                    new SignedUpdateCompatibilityProfile
                    {
                        ProfileId = $"profile-{releaseId}",
                        Mode = "exact",
                        GameVersion = "0.60.1247",
                        StampSha256 = new string('B', 64),
                        StampValue = "0.60.1247",
                        ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa",
                        Loc1Schema = 4,
                        OrderedKeysetSha256 = new string('A', 64),
                        English = new SignedUpdateCorpusIdentity
                        {
                            Sha256 = new string('1', 64),
                            ContentVersion = "Prod_0.60.0_68",
                            LocaleId = 1,
                            LocaleRevisionHex = "1234ABCD",
                            ReleaseRevision = 68,
                            EntryCount = entryCount
                        },
                        Base = new SignedUpdateCorpusIdentity
                        {
                            Sha256 = new string('2', 64),
                            ContentVersion = "Prod_0.60.0_68",
                            LocaleId = 8,
                            LocaleRevisionHex = "D7A0FEFB",
                            ReleaseRevision = 68,
                            EntryCount = entryCount
                        },
                        Composition = new SignedUpdateComposition
                        {
                            AppliedRu = 2,
                            EnglishFallback = 1,
                            BaseFallback = 1,
                            MissingCatalog = 1,
                            StaleCatalog = 0,
                            RejectedCatalog = 0,
                            NeedsReviewFallback = 0,
                            PolicyFallback = 0,
                            ValidationErrors = 0,
                            ValidationWarnings = 0,
                            OutputRawSha256 = new string('E', 64)
                        }
                    }
                },
                RevokedReleaseIds = revokedReleaseIds ?? Array.Empty<string>(),
                NotesRu = string.Empty
            };
        }

        private static void WithStore(
            DateTimeOffset initialClock,
            Action<SignedUpdateStateStore, Action<DateTimeOffset>> action)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-lkg-hardening-{Guid.NewGuid():N}");
            try
            {
                DateTimeOffset clock = initialClock;
                SignedUpdateStateStore store = (SignedUpdateStateStore)(Activator.CreateInstance(
                    typeof(SignedUpdateStateStore),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args: new object[] { root, (Func<DateTimeOffset>)(() => clock) },
                    culture: null)
                    ?? throw new InvalidOperationException("Could not create isolated state store."));
                action(store, value => clock = value);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static bool HasArgument(string expected)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, expected, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static void Expect<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Pass()
        {
            _passed++;
        }

        private sealed record SignedFixture(byte[] EnvelopeUtf8, VerifiedSignedUpdate Update);
    }
}
