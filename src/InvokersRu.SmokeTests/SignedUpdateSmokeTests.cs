using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.SmokeTests
{
    /// <summary>
    /// Run with: InvokersRu.SmokeTests --signed-update-only
    /// The key exists only in process memory so the repository never contains release private-key material.
    /// </summary>
    internal static class SignedUpdateSmokeTests
    {
        private const string SignedUpdateOnlyArgument = "--signed-update-only";
        private static int _passed;

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!HasArgument(SignedUpdateOnlyArgument)) return;

            try
            {
                Run();
                Console.WriteLine($"PASS: {_passed} fixture-free signed-update smoke checks");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: signed-update smoke checks: {exception}");
                Environment.Exit(1);
            }
        }

        private static void Run()
        {
            // This is an ephemeral test key. Only its public SPKI bytes leave ECDsa and nothing is written.
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] publicKey = signingKey.ExportSubjectPublicKeyInfo();
            SignedUpdateManifest manifest = CreateManifest();
            SignedEnvelopeData signed = Sign(signingKey, manifest);
            DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T18:05:00Z");

            VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0"));
            Require(verified.Manifest.Sequence == 12
                && verified.PayloadSha256 == signed.PayloadSha256
                && verified.PatcherDisposition == SignedUpdatePatcherDisposition.UpdateAvailable
                && verified.CanDownloadRemoteArtifactAt(now),
                "Valid signed update did not preserve its identity and patcher warning.");
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                publicKey,
                "different-pinned-key",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            ImmutableVerifiedSnapshotCheck(verified);
            Pass();

            var sameSequence = new SignedUpdateVerificationContext(
                now,
                "3.1.0",
                highestAcceptedSequence: 12,
                highestAcceptedPayloadSha256: signed.PayloadSha256);
            VerifiedSignedUpdate replay = SignedUpdateVerifier.Verify(signed.EnvelopeUtf8, publicKey, "release-p256-test", sameSequence);
            Require(!replay.IsNewSequence
                && replay.Warnings.Contains(SignedUpdateWarningCode.SequenceAlreadyAccepted),
                "An identical accepted sequence was not classified as an idempotent replay.");
            Pass();

            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(
                    now,
                    "3.0.0",
                    highestAcceptedSequence: 13,
                    highestAcceptedPayloadSha256: new string('A', 64))));
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(
                    now,
                    "3.0.0",
                    highestAcceptedSequence: 12,
                    highestAcceptedPayloadSha256: new string('F', 64))));
            Pass();

            byte[] tamperedPayload = (byte[])signed.PayloadUtf8.Clone();
            tamperedPayload[tamperedPayload.Length / 2] ^= 1;
            byte[] tamperedEnvelope = SerializeEnvelope(
                tamperedPayload,
                signed.Signature,
                "release-p256-test");
            Expect<CryptographicException>(() => SignedUpdateVerifier.Verify(
                tamperedEnvelope,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            string envelopeText = Encoding.UTF8.GetString(signed.EnvelopeUtf8);
            byte[] duplicateEnvelope = Encoding.UTF8.GetBytes("{\"schema\":1," + envelopeText.Substring(1));
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                duplicateEnvelope,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            string payloadText = Encoding.UTF8.GetString(signed.PayloadUtf8);
            byte[] unknownMemberPayload = Encoding.UTF8.GetBytes(
                payloadText.Replace("\"notes_ru\":", "\"unknown_member\":1,\"notes_ru\":", StringComparison.Ordinal));
            SignedEnvelopeData unknownMember = SignPayload(signingKey, unknownMemberPayload);
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                unknownMember.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            SignedEnvelopeData badUrl = Sign(signingKey, CreateManifest(
                catalogUrl: "https://github.com.attacker.example/Braintfy/ruslocal-invokers/releases/download/translations-2026.08.20.1/ru_RU.jsonl.br"));
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                badUrl.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Expect<InvalidDataException>(() => SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                "https://release-assets.githubusercontent.com.attacker.example/asset?token=x"));
            Expect<InvalidDataException>(() => SignedUpdateUrlPolicy.ValidateCatalogUrl(
                "https://github.com/Braintfy/ruslocal-invokers/releases/download/translations-2026.08.20.1/ru_RU.jsonl.br",
                "../translations-2026.08.20.1"));
            SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                "https://release-assets.githubusercontent.com/github-production-release-asset/123?token=x");
            SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                "https://release-assets.githubusercontent.com/github-production-release-asset/123?sig=a%2Fb%5Cc&response-content-type=application%2Foctet-stream");
            SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                "https://objects.githubusercontent.com/asset?response-content-type=application%2foctet-stream");
            foreach (string encodedSeparator in new[] { "%2f", "%2F", "%5c", "%5C" })
            {
                Expect<InvalidDataException>(() => SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                    $"https://release-assets.githubusercontent.com/asset{encodedSeparator}123?token=ok"));
            }
            Expect<InvalidDataException>(() => SignedUpdateUrlPolicy.ValidateArtifactResponseUrl(
                "https://attacker.example/asset?response-content-type=application%2Foctet-stream"));
            Pass();

            SignedEnvelopeData oversized = Sign(signingKey, CreateManifest(
                compressedBytes: 1,
                uncompressedBytes: SignedUpdateLimits.MaxUncompressedCatalogBytes + 1));
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                oversized.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            SignedEnvelopeData expired = Sign(signingKey, CreateManifest(
                issuedUtc: "2026-07-01T00:00:00Z",
                expiresUtc: "2026-07-31T00:00:00Z"));
            Expect<InvalidDataException>(() => SignedUpdateVerifier.Verify(
                expired.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            VerifiedSignedUpdate cached = SignedUpdateVerifier.Verify(
                expired.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0", allowExpiredCachedManifest: true));
            Require(cached.IsExpired
                && !cached.CanDownloadRemoteArtifactAt(now)
                && cached.Warnings.Contains(SignedUpdateWarningCode.ExpiredCachedManifest),
                "Expired metadata did not fail closed for remote download while remaining usable as explicit offline metadata.");
            Pass();

            VerifiedSignedUpdate stalePatcher = SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "2.9.9"));
            Require(stalePatcher.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld
                && !stalePatcher.CanDownloadRemoteArtifactAt(now)
                && stalePatcher.Warnings.Contains(SignedUpdateWarningCode.PatcherTooOld),
                "A below-minimum patcher was not blocked from remote artifact use.");
            Pass();

            using ECDsa wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            Expect<CryptographicException>(() => SignedUpdateVerifier.Verify(
                signed.EnvelopeUtf8,
                wrongCurve.ExportSubjectPublicKeyInfo(),
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0")));
            Pass();

            StateStoreChecks(signingKey, publicKey, verified, now);
            Pass();

            HttpTransportChecks(signingKey, publicKey, now);
            Pass();
        }

        private static void ImmutableVerifiedSnapshotCheck(VerifiedSignedUpdate verified)
        {
            Type[] snapshotTypes =
            {
                typeof(VerifiedSignedUpdate),
                typeof(VerifiedSignedUpdateEnvelope),
                typeof(VerifiedSignedUpdateManifest),
                typeof(VerifiedSignedUpdatePatcher),
                typeof(VerifiedSignedUpdateCatalog),
                typeof(VerifiedSignedUpdateCompatibilityProfile),
                typeof(VerifiedSignedUpdateCorpusIdentity),
                typeof(VerifiedSignedUpdateComposition)
            };
            foreach (Type type in snapshotTypes)
            {
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    Require(property.SetMethod == null, $"Verified snapshot exposes a setter: {type.Name}.{property.Name}");
                    Require(!property.PropertyType.IsArray, $"Verified snapshot exposes a mutable array: {type.Name}.{property.Name}");
                }
            }

            Require(verified.Manifest.Compatibility is IList<VerifiedSignedUpdateCompatibilityProfile> profiles
                && profiles.IsReadOnly,
                "Verified compatibility collection is not backed by a read-only collection.");
            Expect<NotSupportedException>(() =>
                ((IList<VerifiedSignedUpdateCompatibilityProfile>)verified.Manifest.Compatibility).RemoveAt(0));
            Require(verified.Warnings is IList<SignedUpdateWarningCode> warnings && warnings.IsReadOnly,
                "Verified warning collection is not backed by a read-only copy.");
            Require(verified.Manifest.Compatibility.Count == 1
                && verified.Manifest.Compatibility[0].ProfileId == "runtime-cache-win64-0.60.1247-prod68",
                "A mutation attempt changed the verified compatibility snapshot.");
        }

        private static void StateStoreChecks(
            ECDsa signingKey,
            byte[] publicKey,
            VerifiedSignedUpdate verified,
            DateTimeOffset now)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-update-state-{Guid.NewGuid():N}");
            try
            {
                DateTimeOffset clockNow = now;
                SignedUpdateStateStore store = CreateIsolatedStateStore(root, () => clockNow);
                Require(store.Load() == null, "A new signed-update state store was not empty.");
                string expectedDefault = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InvokersRu",
                    "updates",
                    SignedUpdateStateStore.StateFileName);
                Require(string.Equals(SignedUpdateStateStore.DefaultStatePath(), expectedDefault, StringComparison.OrdinalIgnoreCase),
                    "Signed-update state does not use its fixed LocalAppData path.");

                SignedUpdateState accepted = store.RecordAcceptedManifest(verified);
                Require(accepted.HighestAcceptedSequence == 12
                    && accepted.HighestAcceptedPayloadSha256 == verified.PayloadSha256
                    && accepted.LastKnownGood == null,
                    "Accepted manifest identity was not persisted.");
                SignedUpdateVerificationContext context = store.CreateVerificationContext(now, "3.0.0");
                Require(context.HighestAcceptedSequence == 12
                    && context.HighestAcceptedPayloadSha256 == verified.PayloadSha256,
                    "Persisted anti-rollback identity was not restored into a verification context.");

                DateTimeOffset acceptedUtc = DateTimeOffset.Parse("2026-08-20T18:10:00Z");
                SignedUpdateState withLkg = store.RecordLastKnownGood(verified, acceptedUtc);
                Require(withLkg.LastKnownGood?.ReleaseId == verified.Manifest.ReleaseId
                    && withLkg.LastKnownGood.ArtifactId == verified.Manifest.Catalog.ArtifactId
                    && withLkg.LastKnownGood.ManifestPayloadSha256 == verified.PayloadSha256
                    && withLkg.LastKnownGood.AcceptedUtc == acceptedUtc,
                    "Last-known-good metadata did not preserve the verified catalog identity.");

                SignedEnvelopeData belowMinimumData = Sign(signingKey, CreateManifest());
                VerifiedSignedUpdate belowMinimum = SignedUpdateVerifier.Verify(
                    belowMinimumData.EnvelopeUtf8,
                    publicKey,
                    "release-p256-test",
                    new SignedUpdateVerificationContext(now, "2.9.9"));
                Expect<InvalidOperationException>(() => store.RecordLastKnownGood(belowMinimum, acceptedUtc));

                clockNow = verified.ExpiresUtc.AddSeconds(1);
                Require(!verified.CanDownloadRemoteArtifactAt(clockNow),
                    "Verified update did not become expired when the injected clock advanced.");
                Expect<InvalidOperationException>(() => store.RecordAcceptedManifest(verified));
                SignedUpdateState cachedLkg = store.RecordLastKnownGood(verified, acceptedUtc.AddMinutes(1));
                Require(cachedLkg.LastKnownGood?.AcceptedUtc == acceptedUtc,
                    "Expired metadata changed an already-recorded last-known-good identity.");
                clockNow = now;

                byte[] stateBytes = File.ReadAllBytes(store.StatePath);
                Require(stateBytes.Length > 2
                    && !(stateBytes[0] == 0xEF && stateBytes[1] == 0xBB && stateBytes[2] == 0xBF)
                    && stateBytes[^1] == (byte)'\n',
                    "Signed-update state is not canonical BOM-less UTF-8 with a final newline.");
                Require(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0,
                    "Atomic signed-update state write left a temporary file behind.");

                string validJson = Encoding.UTF8.GetString(stateBytes);
                File.WriteAllText(
                    store.StatePath,
                    validJson.Replace("\"kind\":", "\"unknown\": true,\"kind\":", StringComparison.Ordinal),
                    new UTF8Encoding(false));
                Expect<InvalidDataException>(() => store.Load());
                File.WriteAllBytes(store.StatePath, stateBytes);

                File.WriteAllText(
                    store.StatePath,
                    "{\"schema\":1," + validJson.Substring(1),
                    new UTF8Encoding(false));
                Expect<InvalidDataException>(() => store.Load());
                File.WriteAllBytes(store.StatePath, stateBytes);

                VerifiedSignedUpdate older = VerifyManifest(signingKey, publicKey, CreateManifest(sequence: 11), now);
                Expect<InvalidDataException>(() => store.RecordAcceptedManifest(older));
                VerifiedSignedUpdate conflicting = VerifyManifest(
                    signingKey,
                    publicKey,
                    CreateManifest(sequence: 12, notesRu: "Другой payload той же последовательности."),
                    now);
                Expect<InvalidDataException>(() => store.RecordAcceptedManifest(conflicting));

                VerifiedSignedUpdate newer = VerifyManifest(signingKey, publicKey, CreateManifest(sequence: 13), now);
                SignedUpdateState advanced = store.RecordAcceptedManifest(newer);
                Require(advanced.HighestAcceptedSequence == 13
                    && advanced.HighestAcceptedPayloadSha256 == newer.PayloadSha256
                    && advanced.LastKnownGood?.Sequence == 12,
                    "A monotonic manifest advance discarded or changed last-known-good metadata.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void HttpTransportChecks(ECDsa signingKey, byte[] publicKey, DateTimeOffset now)
        {
            using (HttpClientHandler secureHandler = SignedUpdateHttpClient.CreateSecureHandler())
            {
                Require(!secureHandler.AllowAutoRedirect
                    && secureHandler.AutomaticDecompression == DecompressionMethods.None
                    && !secureHandler.UseCookies
                    && !secureHandler.UseDefaultCredentials,
                    "Signed-update HTTP handler does not fail closed on redirects, encoding, cookies, or credentials.");
            }

            const string envelopeUrl = "https://github.com/Braintfy/ruslocal-invokers/releases/latest/download/update-envelope.json";
            const string cdnUrl = "https://release-assets.githubusercontent.com/github-production-release-asset/123?sig=a%2Fb&response-content-type=application%2Foctet-stream";
            byte[] envelopeBytes = Encoding.UTF8.GetBytes("{\"signed\":true}");
            var envelopeHandler = new QueueHttpMessageHandler(
                _ => Redirect(HttpStatusCode.Found, cdnUrl),
                _ => Content(HttpStatusCode.OK, envelopeBytes));
            using (var client = new SignedUpdateHttpClient(envelopeHandler))
            {
                byte[] downloaded = client.DownloadEnvelopeAsync(envelopeUrl).GetAwaiter().GetResult();
                Require(downloaded.SequenceEqual(envelopeBytes) && envelopeHandler.Requests.Count == 2,
                    "Signed-update envelope was not streamed through an explicitly validated redirect.");
            }

            var hostileRedirectHandler = new QueueHttpMessageHandler(
                _ => Redirect(HttpStatusCode.Found, "https://attacker.example/update-envelope.json"));
            using (var client = new SignedUpdateHttpClient(hostileRedirectHandler))
            {
                Expect<InvalidDataException>(() => client.DownloadEnvelopeAsync(envelopeUrl).GetAwaiter().GetResult());
            }

            byte[] oversizedEnvelope = new byte[SignedUpdateLimits.MaxEnvelopeBytes + 1];
            var oversizedHandler = new QueueHttpMessageHandler(_ =>
            {
                HttpResponseMessage response = Content(HttpStatusCode.OK, oversizedEnvelope);
                response.Content.Headers.ContentLength = null;
                return response;
            });
            using (var client = new SignedUpdateHttpClient(oversizedHandler))
            {
                Expect<InvalidDataException>(() => client.DownloadEnvelopeAsync(envelopeUrl).GetAwaiter().GetResult());
            }

            byte[] catalogBytes = Encoding.UTF8.GetBytes("{\"id\":\"fixture-free-catalog\"}\n");
            string catalogHash = Convert.ToHexString(SHA256.HashData(catalogBytes));
            VerifiedSignedUpdate catalogUpdate = VerifyManifest(
                signingKey,
                publicKey,
                CreateManifest(
                    compressedBytes: catalogBytes.Length,
                    uncompressedBytes: catalogBytes.Length,
                    compressedSha256: catalogHash,
                    sequence: 14),
                now);
            string stateRoot = Path.Combine(Path.GetTempPath(), $"invokersru-http-state-{Guid.NewGuid():N}");
            try
            {
                DateTimeOffset httpClock = now;
                SignedUpdateStateStore stateStore = CreateIsolatedStateStore(stateRoot, () => httpClock);
                stateStore.RecordAcceptedManifest(catalogUpdate);
                var catalogHandler = new QueueHttpMessageHandler(
                    _ => Redirect(HttpStatusCode.TemporaryRedirect, cdnUrl),
                    _ => Content(HttpStatusCode.OK, catalogBytes));
                using (var client = new SignedUpdateHttpClient(catalogHandler))
                using (var destination = new MemoryStream())
                {
                    SignedUpdateArtifactDownload receipt = client.DownloadCatalogAsync(
                        catalogUpdate,
                        stateStore,
                        destination).GetAwaiter().GetResult();
                    Require(destination.ToArray().SequenceEqual(catalogBytes)
                        && receipt.BytesWritten == catalogBytes.Length
                        && receipt.Sha256 == catalogHash
                        && receipt.FinalUri.AbsoluteUri == cdnUrl,
                        "Signed catalog transport did not enforce its redirect, length, hash, or final URI pins.");
                }

                byte[] wrongBytes = (byte[])catalogBytes.Clone();
                wrongBytes[0] ^= 1;
                var wrongHashHandler = new QueueHttpMessageHandler(_ => Content(HttpStatusCode.OK, wrongBytes));
                using (var client = new SignedUpdateHttpClient(wrongHashHandler))
                using (var destination = new MemoryStream())
                {
                    Expect<InvalidDataException>(() => client.DownloadCatalogAsync(
                        catalogUpdate,
                        stateStore,
                        destination).GetAwaiter().GetResult());
                    Require(destination.Length == 0 && destination.Position == 0,
                        "Rejected catalog bytes remained in the staging destination.");
                }

                httpClock = catalogUpdate.ExpiresUtc.AddSeconds(1);
                var expiredHandler = new QueueHttpMessageHandler(_ => Content(HttpStatusCode.OK, catalogBytes));
                using (var client = new SignedUpdateHttpClient(expiredHandler))
                using (var destination = new MemoryStream())
                {
                    Expect<InvalidOperationException>(() => client.DownloadCatalogAsync(
                        catalogUpdate,
                        stateStore,
                        destination).GetAwaiter().GetResult());
                    Require(expiredHandler.Requests.Count == 0,
                        "Expired metadata reached the HTTP transport before being rejected.");
                }

                httpClock = now;
                VerifiedSignedUpdate newer = VerifyManifest(signingKey, publicKey, CreateManifest(sequence: 15), now);
                stateStore.RecordAcceptedManifest(newer);
                var staleHandler = new QueueHttpMessageHandler(_ => Content(HttpStatusCode.OK, catalogBytes));
                using (var client = new SignedUpdateHttpClient(staleHandler))
                using (var destination = new MemoryStream())
                {
                    Expect<InvalidOperationException>(() => client.DownloadCatalogAsync(
                        catalogUpdate,
                        stateStore,
                        destination).GetAwaiter().GetResult());
                    Require(staleHandler.Requests.Count == 0,
                        "A stale verified object reached the HTTP transport after anti-rollback state advanced.");
                }
            }
            finally
            {
                if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, recursive: true);
            }

            var hostileFinalHandler = new QueueHttpMessageHandler(_ =>
            {
                HttpResponseMessage response = Content(HttpStatusCode.OK, envelopeBytes);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://attacker.example/final");
                return response;
            });
            using (var client = new SignedUpdateHttpClient(hostileFinalHandler))
            {
                Expect<InvalidDataException>(() => client.DownloadEnvelopeAsync(envelopeUrl).GetAwaiter().GetResult());
            }
        }

        private static VerifiedSignedUpdate VerifyManifest(
            ECDsa signingKey,
            byte[] publicKey,
            SignedUpdateManifest manifest,
            DateTimeOffset now)
        {
            SignedEnvelopeData data = Sign(signingKey, manifest);
            return SignedUpdateVerifier.Verify(
                data.EnvelopeUtf8,
                publicKey,
                "release-p256-test",
                new SignedUpdateVerificationContext(now, "3.0.0"));
        }

        private static SignedUpdateStateStore CreateIsolatedStateStore(
            string path,
            Func<DateTimeOffset> utcNow)
        {
            return (SignedUpdateStateStore)(Activator.CreateInstance(
                typeof(SignedUpdateStateStore),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { path, utcNow },
                culture: null)
                ?? throw new InvalidOperationException("Could not create isolated signed-update state store."));
        }

        private static SignedUpdateManifest CreateManifest(
            string catalogUrl = "https://github.com/Braintfy/ruslocal-invokers/releases/download/translations-2026.08.20.1/ru_RU.jsonl.br",
            long compressedBytes = 10_000_000,
            long uncompressedBytes = 35_423_677,
            string issuedUtc = "2026-08-20T18:00:00Z",
            string expiresUtc = "2026-09-20T18:00:00Z",
            string? compressedSha256 = null,
            ulong sequence = 12,
            string notesRu = "Тестовая подписанная метаинформация.")
        {
            const int entryCount = 41_292;
            return new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = sequence,
                ReleaseId = "translations-2026.08.20.1",
                IssuedUtc = issuedUtc,
                ExpiresUtc = expiresUtc,
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = "3.0.0",
                    LatestVersion = "3.1.0",
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = "ru-RU-2026.08.20.1",
                    Url = catalogUrl,
                    Compression = "brotli",
                    CompressedBytes = compressedBytes,
                    CompressedSha256 = compressedSha256 ?? new string('A', 64),
                    UncompressedBytes = uncompressedBytes,
                    UncompressedSha256 = "CBF5211BDCB8E940829F305154CE9A92C0D202BCC6B49617A4B92BA25166600C",
                    RecordCount = 41_037,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = "validated-preview-v1"
                },
                Compatibility = new[]
                {
                    new SignedUpdateCompatibilityProfile
                    {
                        ProfileId = "runtime-cache-win64-0.60.1247-prod68",
                        Mode = "exact",
                        GameVersion = "0.60.1247",
                        StampSha256 = new string('B', 64),
                        StampValue = "0.60.1247",
                        ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa",
                        Loc1Schema = 4,
                        OrderedKeysetSha256 = new string('C', 64),
                        English = new SignedUpdateCorpusIdentity
                        {
                            Sha256 = "4FF01E3D26ECF001B74F822FF11F7115834D9A3FB20FA265186A821CEFE76B21",
                            ContentVersion = "Prod_0.60.0_68",
                            LocaleId = 1,
                            LocaleRevisionHex = "1234ABCD",
                            ReleaseRevision = 68,
                            EntryCount = entryCount
                        },
                        Base = new SignedUpdateCorpusIdentity
                        {
                            Sha256 = "D32B038163DCA0D9830C764234AE2D5EACB5F07CD2FEBD2D2EF14B9F529E0B43",
                            ContentVersion = "Prod_0.60.0_68",
                            LocaleId = 8,
                            LocaleRevisionHex = "D7A0FEFB",
                            ReleaseRevision = 68,
                            EntryCount = entryCount
                        },
                        Composition = new SignedUpdateComposition
                        {
                            AppliedRu = 41_037,
                            EnglishFallback = 1,
                            BaseFallback = 254,
                            MissingCatalog = 1,
                            StaleCatalog = 0,
                            RejectedCatalog = 0,
                            NeedsReviewFallback = 0,
                            PolicyFallback = 0,
                            ValidationErrors = 0,
                            ValidationWarnings = 5_355,
                            OutputRawSha256 = "4E3AC2FBF663DA1D3EBF4B2EAEBFAADF5BC2A80AE7E424463582919F07B644CD"
                        }
                    }
                },
                RevokedReleaseIds = Array.Empty<string>(),
                NotesRu = notesRu
            };
        }

        private static SignedEnvelopeData Sign(ECDsa signingKey, SignedUpdateManifest manifest)
        {
            return SignPayload(signingKey, JsonSerializer.SerializeToUtf8Bytes(manifest));
        }

        private static SignedEnvelopeData SignPayload(ECDsa signingKey, byte[] payload)
        {
            byte[] signature = signingKey.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new SignedEnvelopeData(
                payload,
                signature,
                SerializeEnvelope(payload, signature, "release-p256-test"),
                Convert.ToHexString(SHA256.HashData(payload)));
        }

        private static byte[] SerializeEnvelope(byte[] payload, byte[] signature, string keyId)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.EnvelopeKind,
                KeyId = keyId,
                Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                PayloadBase64 = Convert.ToBase64String(payload),
                SignatureBase64 = Convert.ToBase64String(signature)
            });
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

            throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Pass()
        {
            _passed++;
        }

        private sealed class SignedEnvelopeData
        {
            public SignedEnvelopeData(byte[] payloadUtf8, byte[] signature, byte[] envelopeUtf8, string payloadSha256)
            {
                PayloadUtf8 = payloadUtf8;
                Signature = signature;
                EnvelopeUtf8 = envelopeUtf8;
                PayloadSha256 = payloadSha256;
            }

            public byte[] PayloadUtf8 { get; }
            public byte[] Signature { get; }
            public byte[] EnvelopeUtf8 { get; }
            public string PayloadSha256 { get; }
        }

        private sealed class QueueHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

            public QueueHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
            {
                _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
            }

            public List<Uri> Requests { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.RequestUri == null) throw new InvalidOperationException("Test HTTP request has no URI.");
                if (_responses.Count == 0) throw new InvalidOperationException("Test HTTP handler received an unexpected request.");
                Requests.Add(request.RequestUri);
                HttpResponseMessage response = _responses.Dequeue()(request);
                response.RequestMessage ??= new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
                return Task.FromResult(response);
            }
        }

        private static HttpResponseMessage Redirect(HttpStatusCode statusCode, string location)
        {
            var response = new HttpResponseMessage(statusCode);
            response.Headers.Location = new Uri(location, UriKind.Absolute);
            return response;
        }

        private static HttpResponseMessage Content(HttpStatusCode statusCode, byte[] bytes)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(bytes)
            };
        }
    }
}
