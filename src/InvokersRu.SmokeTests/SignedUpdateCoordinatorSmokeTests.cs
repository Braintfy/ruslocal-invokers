using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Updates;
using InvokersRu.Cli;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    /// Fixture-free end-to-end checks for the embedded trust anchor, content-addressed cache,
    /// and signed-update coordinator. Run with --signed-update-coordinator-only.
    /// </summary>
    internal static class SignedUpdateCoordinatorSmokeTests
    {
        private const string OnlyArgument = "--signed-update-coordinator-only";
        private const string KeyId = "coordinator-test-key";
        private const string EnvelopeUrl =
            "https://github.com/Braintfy/ruslocal-invokers/releases/download/invokersru-update-channel-v1/update-envelope.v1.json";
        private static readonly DateTimeOffset InitialNow = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        private static int _passed;

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!HasArgument(OnlyArgument)) return;

            try
            {
                Run();
                Console.WriteLine($"PASS: {_passed} fixture-free signed-update coordinator smoke checks");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: signed-update coordinator smoke checks: {exception}");
                Environment.Exit(1);
            }
        }

        private static void Run()
        {
            using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] publicKey = signingKey.ExportSubjectPublicKeyInfo();

            ChannelConfigIsStrict(publicKey);
            CacheAcceptsExactBrotli(signingKey, publicKey);
            CacheRejectsInvalidBrotliAndPins(signingKey, publicKey);
            RefreshPromotesOnlyVerifiedContent(signingKey, publicKey);
            FailedDownloadsDoNotBecomeLastKnownGood(signingKey, publicKey);
            ExpiredCurrentFallsBackToLastKnownGood(signingKey, publicKey);
            TooOldHeadRemainsAuthoritativeOverLastKnownGood(signingKey, publicKey);
            TooOldHeadWithoutLastKnownGoodRemainsVisible(signingKey, publicKey);
            RollbackEquivocationAndExpiryAreNotPromoted(signingKey, publicKey);
            HistoricalCatalogUpgradeRecoveryResolvesFutureSignedProfile(signingKey, publicKey);
        }

        private static void ChannelConfigIsStrict(byte[] publicKey)
        {
            byte[] validBytes = CreateChannelConfig(publicKey);
            SignedUpdateChannelConfig valid = SignedUpdateChannelConfig.Parse(validBytes);
            Require(valid.Schema == SignedUpdateChannelConfig.CurrentSchema
                && valid.Kind == SignedUpdateChannelConfig.ExpectedKind
                && valid.EnvelopeUrl == EnvelopeUrl
                && valid.KeyId == KeyId
                && valid.PublicKeySubjectPublicKeyInfo.SequenceEqual(publicKey),
                "Valid signed-update channel config did not preserve its pinned identity.");

            string validJson = Encoding.UTF8.GetString(validBytes);
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(Encoding.UTF8.GetBytes(
                validJson.Insert(validJson.Length - 1, ",\"unexpected\":true"))));
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(Encoding.UTF8.GetBytes(
                validJson.Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal))));
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(
                publicKey,
                envelopeUrl: "https://github.com.attacker.example/Braintfy/ruslocal-invokers/releases/latest/download/update-envelope.v1.json")));
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(
                publicKey,
                envelopeUrl: EnvelopeUrl + "?mutable=1")));
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(
                publicKey,
                keyId: "../unsafe")));
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(
                publicKey,
                publicKeyBase64: "not base64")));

            using ECDsa wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(
                wrongCurve.ExportSubjectPublicKeyInfo())));

            byte[] concatenatedSpki = publicKey.Concat(publicKey).ToArray();
            Expect<InvalidDataException>(() => SignedUpdateChannelConfig.Parse(CreateChannelConfig(concatenatedSpki)));
            Pass();
        }

        private static void CacheAcceptsExactBrotli(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("cache-valid", root =>
            {
                byte[] catalog = CreateCatalog("Кэш", recordCount: 2);
                byte[] compressed = CompressBrotli(catalog);
                SignedFixture fixture = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(1, "invokersru-data-cache-valid", compressed, catalog, 2),
                    InitialNow);
                var cache = new SignedUpdateCacheStore(Path.Combine(root, "cache"));

                string envelopePath = cache.StoreEnvelope(fixture.EnvelopeUtf8, fixture.Update);
                string catalogPath = cache.StoreCatalog(compressed, fixture.Update);
                Require(File.Exists(envelopePath)
                    && File.ReadAllBytes(envelopePath).SequenceEqual(fixture.EnvelopeUtf8),
                    "Verified envelope was not cached exactly.");
                Require(File.Exists(catalogPath)
                    && File.ReadAllBytes(catalogPath).SequenceEqual(catalog),
                    "Verified Brotli catalog was not expanded and cached exactly.");
                Require(cache.RequireCatalog(fixture.Update) == catalogPath
                    && cache.HasCatalog(fixture.Update)
                    && cache.LoadEnvelope(fixture.Update.PayloadSha256).SequenceEqual(fixture.EnvelopeUtf8)
                    && cache.EnumerateEnvelopePayloadHashes().SequenceEqual(new[] { fixture.Update.PayloadSha256 }),
                    "Content-addressed cache could not revalidate its stored envelope/catalog.");
            });
            Pass();
        }

        private static void CacheRejectsInvalidBrotliAndPins(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("cache-negative", root =>
            {
                byte[] catalog = CreateCatalog("Проверка", recordCount: 2);
                byte[] compressed = CompressBrotli(catalog);
                var cache = new SignedUpdateCacheStore(Path.Combine(root, "cache"));

                SignedFixture exact = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(10, "invokersru-data-cache-exact", compressed, catalog, 2),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(compressed[..^1], exact.Update));

                byte[] wrongHash = (byte[])compressed.Clone();
                wrongHash[wrongHash.Length / 2] ^= 0x40;
                Expect<InvalidDataException>(() => cache.StoreCatalog(wrongHash, exact.Update));

                byte[] trailing = compressed.Concat(new byte[] { 0x00 }).ToArray();
                SignedFixture trailingFixture = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(11, "invokersru-data-cache-trailing", trailing, catalog, 2),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(trailing, trailingFixture.Update));

                byte[] invalidBrotli = Enumerable.Range(0, 64).Select(index => (byte)(index * 37 + 11)).ToArray();
                SignedFixture invalidFixture = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(12, "invokersru-data-cache-invalid", invalidBrotli, catalog, 2),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(invalidBrotli, invalidFixture.Update));

                SignedFixture wrongExpandedSize = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        13,
                        "invokersru-data-cache-expanded-size",
                        compressed,
                        catalog,
                        2,
                        signedUncompressedBytes: catalog.LongLength + 1),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(compressed, wrongExpandedSize.Update));

                SignedFixture wrongExpandedHash = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        14,
                        "invokersru-data-cache-expanded-hash",
                        compressed,
                        catalog,
                        2,
                        signedUncompressedSha256: new string('F', 64)),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(compressed, wrongExpandedHash.Update));

                SignedFixture wrongRecordCount = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(15, "invokersru-data-cache-record-count", compressed, catalog, 1),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(compressed, wrongRecordCount.Update));

                byte[] invalidJsonl = Encoding.UTF8.GetBytes("{not-json}\n");
                byte[] invalidJsonlBrotli = CompressBrotli(invalidJsonl);
                SignedFixture invalidJsonFixture = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(16, "invokersru-data-cache-jsonl", invalidJsonlBrotli, invalidJsonl, 1),
                    InitialNow);
                Expect<InvalidDataException>(() => cache.StoreCatalog(invalidJsonlBrotli, invalidJsonFixture.Update));

                Require(!Directory.Exists(Path.Combine(root, "cache", "catalogs"))
                    || Directory.GetFiles(Path.Combine(root, "cache", "catalogs"), "*.jsonl").Length == 0,
                    "Rejected catalog bytes were promoted into the content-addressed cache.");
            });
            Pass();
        }

        private static void RefreshPromotesOnlyVerifiedContent(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-refresh", root =>
            {
                DateTimeOffset clock = InitialNow;
                byte[] catalog = CreateCatalog("Обновление", recordCount: 2);
                byte[] compressed = CompressBrotli(catalog);
                SignedFixture fixture = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(20, "invokersru-data-refresh", compressed, catalog, 2),
                    clock);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, fixture.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, compressed));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                SignedUpdateBundle refreshed = coordinator.RefreshAsync().GetAwaiter().GetResult();
                SignedUpdateState accepted = stateStore.Load()
                    ?? throw new InvalidOperationException("Refresh did not persist signed-update state.");
                Require(refreshed.Source == SignedUpdateBundleSource.Remote
                    && refreshed.Update.PayloadSha256 == fixture.Update.PayloadSha256
                    && File.ReadAllBytes(refreshed.CatalogPath).SequenceEqual(catalog)
                    && accepted.HighestAcceptedSequence == 20
                    && accepted.HighestAcceptedPayloadSha256 == fixture.Update.PayloadSha256
                    && accepted.LastKnownGood == null,
                    "Refresh did not atomically retain the verified envelope/catalog identity.");
                Require(handler.Requests.SequenceEqual(new[]
                {
                    new Uri(EnvelopeUrl),
                    new Uri(fixture.Update.Manifest.Catalog.Url)
                }), "Coordinator requested an unexpected endpoint.");

                SignedUpdateBundle cached = coordinator.LoadBestAvailable()
                    ?? throw new InvalidOperationException("Fresh verified cache was not loadable.");
                Require(cached.Source == SignedUpdateBundleSource.CachedCurrent
                    && cached.Update.PayloadSha256 == fixture.Update.PayloadSha256,
                    "Current verified update was not selected from cache.");

                int installs = 0;
                coordinator.ExecuteInstall(cached, () => installs++);
                SignedUpdateState installed = stateStore.Load()
                    ?? throw new InvalidOperationException("Installed state disappeared.");
                Require(installs == 1
                    && installed.LastKnownGood?.Sequence == 20
                    && installed.LastKnownGood.ManifestPayloadSha256 == fixture.Update.PayloadSha256,
                    "Successful install callback did not promote the exact current artifact to LKG.");
            });
            Pass();
        }

        private static void FailedDownloadsDoNotBecomeLastKnownGood(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-failure", root =>
            {
                DateTimeOffset clock = InitialNow;
                byte[] catalog1 = CreateCatalog("Рабочий", recordCount: 2);
                byte[] compressed1 = CompressBrotli(catalog1);
                byte[] catalog2 = CreateCatalog("Не загружен", recordCount: 2);
                byte[] compressed2 = CompressBrotli(catalog2);
                SignedFixture first = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(30, "invokersru-data-good", compressed1, catalog1, 2),
                    clock);
                SignedFixture second = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(31, "invokersru-data-download-fails", compressed2, catalog2, 2),
                    clock);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, first.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, compressed1),
                    _ => Content(HttpStatusCode.OK, second.EnvelopeUtf8),
                    _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                    _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                SignedUpdateBundle installed = coordinator.RefreshAsync().GetAwaiter().GetResult();
                coordinator.ExecuteInstall(installed, static () => { });
                Expect<HttpRequestException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());

                SignedUpdateState afterCatalogFailure = stateStore.Load()
                    ?? throw new InvalidOperationException("State disappeared after catalog failure.");
                Require(afterCatalogFailure.HighestAcceptedSequence == 31
                    && afterCatalogFailure.HighestAcceptedPayloadSha256 == second.Update.PayloadSha256
                    && afterCatalogFailure.LastKnownGood?.Sequence == 30
                    && afterCatalogFailure.LastKnownGood.ManifestPayloadSha256 == first.Update.PayloadSha256
                    && !cacheStore.HasCatalog(second.Update),
                    "Failed catalog download was installed as LKG or lost the accepted envelope sequence.");

                SignedUpdateBundle fallback = coordinator.LoadBestAvailable()
                    ?? throw new InvalidOperationException("Known-good catalog was lost after a newer download failure.");
                Require(fallback.Source == SignedUpdateBundleSource.LastKnownGood
                    && fallback.Update.PayloadSha256 == first.Update.PayloadSha256,
                    "Failed current catalog did not fall back to the prior exact LKG.");

                Expect<HttpRequestException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());
                SignedUpdateState afterEnvelopeFailure = stateStore.Load()
                    ?? throw new InvalidOperationException("State disappeared after envelope failure.");
                Require(afterEnvelopeFailure.HighestAcceptedSequence == 31
                    && afterEnvelopeFailure.HighestAcceptedPayloadSha256 == second.Update.PayloadSha256
                    && afterEnvelopeFailure.LastKnownGood?.Sequence == 30,
                    "Failed envelope download changed signed-update state.");
            });
            Pass();
        }

        private static void ExpiredCurrentFallsBackToLastKnownGood(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-offline", root =>
            {
                DateTimeOffset clock = InitialNow;
                const string issued = "2026-08-20T00:00:00Z";
                const string expires = "2026-08-22T00:00:00Z";
                byte[] catalog1 = CreateCatalog("Офлайн LKG", recordCount: 2);
                byte[] compressed1 = CompressBrotli(catalog1);
                byte[] catalog2 = CreateCatalog("Офлайн current", recordCount: 2);
                byte[] compressed2 = CompressBrotli(catalog2);
                SignedFixture first = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(40, "invokersru-data-offline-lkg", compressed1, catalog1, 2, issuedUtc: issued, expiresUtc: expires),
                    clock);
                SignedFixture second = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(41, "invokersru-data-offline-current", compressed2, catalog2, 2, issuedUtc: issued, expiresUtc: expires),
                    clock);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, first.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, compressed1),
                    _ => Content(HttpStatusCode.OK, second.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, compressed2));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                SignedUpdateBundle firstBundle = coordinator.RefreshAsync().GetAwaiter().GetResult();
                coordinator.ExecuteInstall(firstBundle, static () => { });
                SignedUpdateBundle secondBundle = coordinator.RefreshAsync().GetAwaiter().GetResult();
                Require(secondBundle.Source == SignedUpdateBundleSource.Remote
                    && coordinator.LoadBestAvailable()?.Source == SignedUpdateBundleSource.CachedCurrent,
                    "Newest unexpired signed bundle was not current before offline expiry.");

                clock = DateTimeOffset.Parse("2026-08-23T00:00:01Z");
                SignedUpdateBundle offline = coordinator.LoadBestAvailable()
                    ?? throw new InvalidOperationException("Expired exact LKG was unavailable for offline fallback.");
                Require(offline.Source == SignedUpdateBundleSource.LastKnownGood
                    && offline.Update.PayloadSha256 == first.Update.PayloadSha256
                    && offline.Update.IsExpired,
                    "Expired current manifest was applied instead of the explicitly recorded offline LKG.");
            });
            Pass();
        }

        private static void TooOldHeadRemainsAuthoritativeOverLastKnownGood(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-too-old-authority", root =>
            {
                DateTimeOffset clock = InitialNow;
                byte[] installedCatalog = CreateCatalog("Установленный LKG", recordCount: 2);
                byte[] installedCompressed = CompressBrotli(installedCatalog);
                byte[] futureCatalog = CreateCatalog("Требует новый патчер", recordCount: 2);
                byte[] futureCompressed = CompressBrotli(futureCatalog);
                SignedFixture installed = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(45, "invokersru-data-installed-lkg", installedCompressed, installedCatalog, 2),
                    clock);
                SignedFixture future = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        46,
                        "invokersru-data-future-patcher",
                        futureCompressed,
                        futureCatalog,
                        2,
                        minimumPatcherVersion: "4.0.0",
                        latestPatcherVersion: "4.1.0"),
                    clock);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, installed.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, installedCompressed),
                    _ => Content(HttpStatusCode.OK, future.EnvelopeUtf8));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                SignedUpdateBundle installedBundle = coordinator.RefreshAsync().GetAwaiter().GetResult();
                coordinator.ExecuteInstall(installedBundle, static () => { });
                Expect<InvalidOperationException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());

                SignedUpdateBundle fallback = coordinator.LoadBestAvailable()
                    ?? throw new InvalidOperationException("Too-old channel head hid the authenticated LKG metadata.");
                Require(fallback.Source == SignedUpdateBundleSource.LastKnownGood
                    && fallback.Update.PayloadSha256 == installed.Update.PayloadSha256
                    && fallback.Authority.PayloadSha256 == future.Update.PayloadSha256
                    && fallback.Authority.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld
                    && fallback.Authority.Manifest.Patcher.MinimumVersion == "4.0.0",
                    "LKG selection lost the newest accepted patcher-version authority.");

                int callbacks = 0;
                Expect<InvalidOperationException>(() => coordinator.ExecuteInstall(fallback, () => callbacks++));
                Require(callbacks == 0, "Too-old channel authority allowed an LKG install callback to run.");
            });
            Pass();
        }

        private static void TooOldHeadWithoutLastKnownGoodRemainsVisible(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-too-old-fresh", root =>
            {
                DateTimeOffset clock = InitialNow;
                byte[] catalog = CreateCatalog("Недоступен старому патчеру", recordCount: 2);
                byte[] compressed = CompressBrotli(catalog);
                SignedFixture future = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        47,
                        "invokersru-data-future-fresh",
                        compressed,
                        catalog,
                        2,
                        minimumPatcherVersion: "4.0.0",
                        latestPatcherVersion: "4.1.0"),
                    clock);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, future.EnvelopeUtf8));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                Expect<InvalidOperationException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());
                Require(coordinator.LoadBestAvailable() == null,
                    "A metadata-only too-old head was exposed as an installable catalog bundle.");
                VerifiedSignedUpdate authority = coordinator.LoadNewestAccepted()
                    ?? throw new InvalidOperationException("Fresh too-old channel authority was lost without an LKG.");
                SignedUpdateState state = stateStore.Load()
                    ?? throw new InvalidOperationException("Fresh too-old channel state was not persisted.");
                Require(authority.PayloadSha256 == future.Update.PayloadSha256
                    && authority.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld
                    && state.HighestAcceptedSequence == 47
                    && state.LastKnownGood == null
                    && !cacheStore.HasCatalog(authority),
                    "Fresh too-old metadata did not remain authenticated and non-installable.");

                string envelopePath = Path.Combine(
                    cacheStore.RootDirectory,
                    "envelopes",
                    future.Update.PayloadSha256 + ".envelope.json");
                File.Delete(envelopePath);
                Expect<InvalidDataException>(() => coordinator.LoadNewestAccepted());
            });
            Pass();
        }

        private static void RollbackEquivocationAndExpiryAreNotPromoted(ECDsa key, byte[] publicKey)
        {
            WithTempRoot("coordinator-antireplay", root =>
            {
                DateTimeOffset clock = InitialNow;
                byte[] catalog = CreateCatalog("Антиоткат", recordCount: 2);
                byte[] compressed = CompressBrotli(catalog);
                SignedFixture current = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(50, "invokersru-data-current", compressed, catalog, 2),
                    clock);
                SignedFixture rollback = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(49, "invokersru-data-rollback", compressed, catalog, 2),
                    clock);
                SignedFixture equivocation = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(50, "invokersru-data-equivocation", compressed, catalog, 2, notesRu: "Другой payload"),
                    clock);
                SignedFixture expired = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        51,
                        "invokersru-data-expired",
                        compressed,
                        catalog,
                        2,
                        issuedUtc: "2026-07-01T00:00:00Z",
                        expiresUtc: "2026-07-31T00:00:00Z"),
                    DateTimeOffset.Parse("2026-07-15T00:00:00Z"));
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(Path.Combine(root, "state"), () => clock);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "cache"));
                var handler = new QueueHttpMessageHandler(
                    _ => Content(HttpStatusCode.OK, current.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, compressed),
                    _ => Content(HttpStatusCode.OK, rollback.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, equivocation.EnvelopeUtf8),
                    _ => Content(HttpStatusCode.OK, expired.EnvelopeUtf8));
                using var httpClient = new SignedUpdateHttpClient(handler);
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => clock);

                coordinator.RefreshAsync().GetAwaiter().GetResult();
                Expect<InvalidDataException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());
                Expect<InvalidDataException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());
                Expect<InvalidDataException>(() => coordinator.RefreshAsync().GetAwaiter().GetResult());

                SignedUpdateState state = stateStore.Load()
                    ?? throw new InvalidOperationException("Anti-rollback state disappeared.");
                Require(state.HighestAcceptedSequence == 50
                    && state.HighestAcceptedPayloadSha256 == current.Update.PayloadSha256
                    && state.LastKnownGood == null,
                    "Rollback, equivocation, or expired metadata changed accepted state.");
                Require(cacheStore.EnumerateEnvelopePayloadHashes().SequenceEqual(new[] { current.Update.PayloadSha256 }),
                    "Rejected rollback/equivocation/expired envelope was cached as accepted metadata.");
            });
            Pass();
        }

        private static void HistoricalCatalogUpgradeRecoveryResolvesFutureSignedProfile(
            ECDsa key,
            byte[] publicKey)
        {
            WithTempRoot("coordinator-catalog-upgrade-recovery", root =>
            {
                const string profileId = "runtime-cache-stable-synthetic";
                const string gameVersion = "0.60.synthetic";
                byte[] englishRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    1,
                    68,
                    0x1234ABCD,
                    new[] { "Open", "Exit" });
                byte[] officialRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8,
                    68,
                    0xD7A0FEFB,
                    new[] { "Відкрити", "Вийти" });
                byte[] legacyRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8,
                    68,
                    0xD7A0FEFB,
                    new[] { "Старое", "Вийти" });
                byte[] embeddedCurrentRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8,
                    68,
                    0xD7A0FEFB,
                    new[] { "Текущее", "Вийти" });
                byte[] futureRaw = FixtureFreeRuntimeCacheSmokeTests.CreateLoc1(
                    8,
                    68,
                    0xD7A0FEFB,
                    new[] { "Будущее", "Вийти" });
                byte[] stampRaw = Encoding.UTF8.GetBytes(gameVersion);
                Loc1Document english = Loc1Codec.Parse(englishRaw);
                Loc1Document official = Loc1Codec.Parse(officialRaw);
                string orderedKeysetSha256 = SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(official);
                string legacyOutputSha256 = Hash(legacyRaw);
                string legacyCatalogSha256 = new string('6', 64);
                string futureOutputSha256 = Hash(futureRaw);
                byte[] futureCatalog = CreateCatalog("Будущее", recordCount: 1);
                byte[] futureCompressed = CompressBrotli(futureCatalog);

                var futureSignedProfile = new SignedUpdateCompatibilityProfile
                {
                    ProfileId = profileId,
                    Mode = "exact",
                    GameVersion = gameVersion,
                    StampSha256 = Hash(stampRaw),
                    StampValue = gameVersion,
                    ContentGuid = english.ContentGuid,
                    Loc1Schema = 4,
                    OrderedKeysetSha256 = orderedKeysetSha256,
                    English = new SignedUpdateCorpusIdentity
                    {
                        Sha256 = Hash(englishRaw),
                        ContentVersion = english.ContentVersion,
                        LocaleId = english.LocaleId,
                        LocaleRevisionHex = english.LocaleRevision.ToString("X8"),
                        ReleaseRevision = english.ReleaseRevision,
                        EntryCount = english.Entries.Count
                    },
                    Base = new SignedUpdateCorpusIdentity
                    {
                        Sha256 = Hash(officialRaw),
                        ContentVersion = official.ContentVersion,
                        LocaleId = official.LocaleId,
                        LocaleRevisionHex = official.LocaleRevision.ToString("X8"),
                        ReleaseRevision = official.ReleaseRevision,
                        EntryCount = official.Entries.Count
                    },
                    Composition = new SignedUpdateComposition
                    {
                        AppliedRu = 1,
                        EnglishFallback = 1,
                        BaseFallback = 0,
                        MissingCatalog = 1,
                        StaleCatalog = 0,
                        RejectedCatalog = 0,
                        NeedsReviewFallback = 0,
                        PolicyFallback = 0,
                        ValidationErrors = 0,
                        ValidationWarnings = 0,
                        OutputRawSha256 = futureOutputSha256
                    }
                };
                SignedFixture future = SignFixture(
                    key,
                    publicKey,
                    CreateManifest(
                        60,
                        "invokersru-data-future-recovery",
                        futureCompressed,
                        futureCatalog,
                        1,
                        compatibility: new[] { futureSignedProfile }),
                    InitialNow);
                SignedUpdateChannelConfig config = SignedUpdateChannelConfig.Parse(CreateChannelConfig(publicKey));
                SignedUpdateStateStore stateStore = CreateStateStore(
                    Path.Combine(root, "update-state"),
                    () => InitialNow);
                var cacheStore = new SignedUpdateCacheStore(Path.Combine(root, "update-cache"));
                cacheStore.StoreEnvelope(future.EnvelopeUtf8, future.Update);
                stateStore.RecordAcceptedManifest(future.Update);
                string futureCatalogPath = cacheStore.StoreCatalog(futureCompressed, future.Update);
                using var httpClient = new SignedUpdateHttpClient(new QueueHttpMessageHandler());
                using var coordinator = new SignedUpdateCoordinator(
                    config,
                    "3.1.0",
                    stateStore,
                    cacheStore,
                    httpClient,
                    () => InitialNow);

                var embedded = new RuntimeCacheCompatibility
                {
                    Id = profileId,
                    GameVersion = gameVersion,
                    ContentGuid = english.ContentGuid,
                    EnglishContentVersion = english.ContentVersion,
                    BaseContentVersion = official.ContentVersion,
                    EnglishSha256 = Hash(englishRaw),
                    BaseSha256 = Hash(officialRaw),
                    StampSha256 = Hash(stampRaw),
                    StampValue = gameVersion,
                    EnglishLocaleId = english.LocaleId,
                    EnglishLocaleRevision = english.LocaleRevision,
                    EnglishReleaseRevision = english.ReleaseRevision,
                    BaseLocaleId = official.LocaleId,
                    BaseLocaleRevision = official.LocaleRevision,
                    BaseReleaseRevision = official.ReleaseRevision,
                    EntryCount = official.Entries.Count,
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = new string('7', 64),
                    ExpectedOutputSha256 = Hash(embeddedCurrentRaw),
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1,
                    ExpectedEnglishFallbacks = 1,
                    ExpectedBaseFallbacks = 0,
                    ExpectedNeedsReviewFallbacks = 0,
                    TranslationPolicy = "supervised-safe-drafts",
                    SupersededArtifacts = new[]
                    {
                        new RuntimeCacheSupersededArtifact
                        {
                            OutputSha256 = legacyOutputSha256,
                            TranslationCatalogSha256 = legacyCatalogSha256,
                            AppliedTranslations = 1,
                            EnglishFallbacks = 1,
                            BaseFallbacks = 0,
                            NeedsReviewFallbacks = 0
                        }
                    }
                };
                embedded.Validate();

                string readyRoot = Path.Combine(root, "ready-to-update");
                string readyCacheRoot = Path.Combine(readyRoot, "cache");
                string readyStatePath = Path.Combine(readyRoot, "state", "state.v1.json");
                Directory.CreateDirectory(readyCacheRoot);
                (string readyEnglishPath, string readyTargetPath, string readyStampPath) =
                    RuntimeCacheService.ResolveTuplePaths(readyCacheRoot);
                File.WriteAllBytes(readyEnglishPath, englishRaw);
                File.WriteAllBytes(readyTargetPath, legacyRaw);
                File.WriteAllBytes(readyStampPath, stampRaw);
                string readyBackupPath = Path.Combine(
                    Path.GetDirectoryName(readyStatePath)!,
                    "backups",
                    $"{profileId}-{Hashing.Sha256Text(profileId).Substring(0, 12)}",
                    $"{embedded.BaseSha256}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(readyBackupPath)!);
                File.WriteAllBytes(readyBackupPath, officialRaw);
                Directory.CreateDirectory(Path.GetDirectoryName(readyStatePath)!);
                File.WriteAllText(readyStatePath, JsonSerializer.Serialize(new PatchState
                {
                    BuildId = profileId,
                    GameRoot = Path.GetFullPath(readyCacheRoot),
                    TargetPath = Path.GetFullPath(readyTargetPath),
                    BackupPath = Path.GetFullPath(readyBackupPath),
                    OriginalSha256 = embedded.BaseSha256,
                    PatchedSha256 = legacyOutputSha256,
                    TranslationsSha256 = legacyCatalogSha256,
                    AppliedTranslations = 1,
                    AppliedAt = InitialNow
                }));
                RuntimeUpdateResolution ready = RuntimeUpdateResolver.Resolve(
                    readyCacheRoot,
                    readyStatePath,
                    embedded,
                    Path.Combine(root, "embedded-catalog.jsonl"),
                    coordinator);
                Require(ready.Bundle?.Source == SignedUpdateBundleSource.CachedCurrent
                    && ready.Profile.ExpectedOutputSha256 == futureOutputSha256
                    && ready.Profile.SupersededArtifacts.Length == 1
                    && ready.Inspection.Status == InstallationStatus.PatchSupersededByCatalogUpdate
                    && ready.TranslationUpdateAvailable
                    && RuntimeUpdateAuthorization.CanApply(ready, InitialNow),
                    "A configured signed future bundle did not produce an authorized ready-to-update plan for the exact embedded predecessor.");

                // The accepted signed envelope remains independently available as history/authority, but
                // removing its catalog simulates a later offline launch that may recover, never re-apply.
                File.Delete(futureCatalogPath);

                foreach ((string Phase, bool ReplacementCommitted, bool DisplacedVerified, string Suffix) scenario in new[]
                {
                    ("Prepared", false, false, "prepared"),
                    ("ReplacementCommitted", true, false, "replacement"),
                    ("PostCommitVerified", true, true, "postcommit")
                })
                {
                    string scenarioRoot = Path.Combine(root, scenario.Suffix);
                    string cacheRoot = Path.Combine(scenarioRoot, "cache");
                    string statePath = Path.Combine(scenarioRoot, "state", "state.v1.json");
                    Directory.CreateDirectory(cacheRoot);
                    (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
                    File.WriteAllBytes(englishPath, englishRaw);
                    File.WriteAllBytes(targetPath, scenario.ReplacementCommitted ? futureRaw : legacyRaw);
                    File.WriteAllBytes(stampPath, stampRaw);
                    string backupPath = Path.Combine(
                        Path.GetDirectoryName(statePath)!,
                        "backups",
                        $"{profileId}-{Hashing.Sha256Text(profileId).Substring(0, 12)}",
                        $"{embedded.BaseSha256}.dl_uk_UA.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.WriteAllBytes(backupPath, officialRaw);
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    File.WriteAllText(statePath, JsonSerializer.Serialize(new PatchState
                    {
                        BuildId = profileId,
                        GameRoot = Path.GetFullPath(cacheRoot),
                        TargetPath = Path.GetFullPath(targetPath),
                        BackupPath = Path.GetFullPath(backupPath),
                        OriginalSha256 = embedded.BaseSha256,
                        PatchedSha256 = legacyOutputSha256,
                        TranslationsSha256 = legacyCatalogSha256,
                        AppliedTranslations = 1,
                        AppliedAt = InitialNow
                    }));
                    string transactionId = scenario.Suffix switch
                    {
                        "prepared" => "11111111111111111111111111111111",
                        "replacement" => "22222222222222222222222222222222",
                        _ => "33333333333333333333333333333333"
                    };
                    var journal = new PatchJournal
                    {
                        TransactionId = transactionId,
                        Operation = "runtime-cache-upgrade",
                        Phase = scenario.Phase,
                        BuildId = profileId,
                        GameRoot = Path.GetFullPath(cacheRoot),
                        TargetPath = Path.GetFullPath(targetPath),
                        BackupPath = Path.GetFullPath(backupPath),
                        QuarantinePath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.displaced"),
                        RollbackPath = Path.Combine(cacheRoot, $".dl_uk_UA.bin.{transactionId}.rollback"),
                        DisplacedSha256 = scenario.DisplacedVerified ? legacyOutputSha256 : null,
                        SourceSha256 = legacyOutputSha256,
                        ExpectedOutputSha256 = futureOutputSha256,
                        TranslationsSha256 = Hash(futureCatalog),
                        AppliedTranslations = 1,
                        CreatedAt = InitialNow
                    };
                    if (scenario.ReplacementCommitted)
                    {
                        File.WriteAllBytes(journal.QuarantinePath, legacyRaw);
                    }
                    PatchJournalStore.Save(statePath, journal);

                    RuntimeUpdateResolution resolution = RuntimeUpdateResolver.Resolve(
                        cacheRoot,
                        statePath,
                        embedded,
                        Path.Combine(root, "embedded-catalog.jsonl"),
                        coordinator,
                        remoteProblem: "Network unavailable in recovery smoke.");
                    Require(resolution.InstalledProfile != null
                        && resolution.InstalledProfile.ExpectedOutputSha256 == futureOutputSha256
                        && resolution.InstalledProfile.SupersededArtifacts.Length == 1
                        && resolution.InstalledProfile.SupersededArtifacts[0].OutputSha256 == legacyOutputSha256
                        && resolution.InstalledInspection?.Status == InstallationStatus.RecoveryRequired
                        && RuntimeUpdateAuthorization.CanRestoreOrRecover(resolution),
                        $"History-only signed future profile did not authorize exact {scenario.Phase} catalog-upgrade recovery.");
                }
            });
            Pass();
        }

        private static SignedFixture SignFixture(
            ECDsa key,
            byte[] publicKey,
            SignedUpdateManifest manifest,
            DateTimeOffset verificationTime)
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
            VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                envelope,
                publicKey,
                KeyId,
                new SignedUpdateVerificationContext(verificationTime, "3.1.0"));
            return new SignedFixture(envelope, verified);
        }

        private static SignedUpdateManifest CreateManifest(
            ulong sequence,
            string releaseId,
            byte[] compressed,
            byte[] uncompressed,
            int signedRecordCount,
            long? signedCompressedBytes = null,
            string? signedCompressedSha256 = null,
            long? signedUncompressedBytes = null,
            string? signedUncompressedSha256 = null,
            string issuedUtc = "2026-08-20T00:00:00Z",
            string expiresUtc = "2026-09-10T00:00:00Z",
            string notesRu = "Fixture-free smoke metadata.",
            string minimumPatcherVersion = "3.1.0",
            string latestPatcherVersion = "3.1.0",
            SignedUpdateCompatibilityProfile[]? compatibility = null)
        {
            const int entryCount = 4;
            int applied = Math.Min(2, signedRecordCount);
            return new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = sequence,
                ReleaseId = releaseId,
                IssuedUtc = issuedUtc,
                ExpiresUtc = expiresUtc,
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = minimumPatcherVersion,
                    LatestVersion = latestPatcherVersion,
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = $"catalog-{sequence}",
                    Url = $"https://github.com/Braintfy/ruslocal-invokers/releases/download/{releaseId}/ru_RU.jsonl.br",
                    Compression = "brotli",
                    CompressedBytes = signedCompressedBytes ?? compressed.LongLength,
                    CompressedSha256 = signedCompressedSha256 ?? Hash(compressed),
                    UncompressedBytes = signedUncompressedBytes ?? uncompressed.LongLength,
                    UncompressedSha256 = signedUncompressedSha256 ?? Hash(uncompressed),
                    RecordCount = signedRecordCount,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = "validated-preview-v1"
                },
                Compatibility = compatibility ?? new[]
                {
                    new SignedUpdateCompatibilityProfile
                    {
                        ProfileId = $"profile-{sequence}",
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
                            AppliedRu = applied,
                            EnglishFallback = 1,
                            BaseFallback = entryCount - applied - 1,
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
                RevokedReleaseIds = Array.Empty<string>(),
                NotesRu = notesRu
            };
        }

        private static byte[] CreateCatalog(string translationPrefix, int recordCount)
        {
            var lines = new List<string>(recordCount);
            for (int index = 1; index <= recordCount; index++)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    id = index.ToString("X16"),
                    source_sha256 = Hash(Encoding.UTF8.GetBytes($"source-{index}")),
                    hint_sha256 = (string?)null,
                    translation = $"{translationPrefix} {index}",
                    status = "approved",
                    model = "fixture-free",
                    prompt_version = "smoke-v1",
                    confidence = "high",
                    needs_review = false,
                    issue_codes = Array.Empty<string>(),
                    risk_flags = Array.Empty<string>(),
                    review_stage = "smoke",
                    reviewer_ids = new[] { "fixture" },
                    reviewed_at = "2026-08-21T00:00:00Z",
                    review_revision = "smoke",
                    screenshot_qa = true,
                    legal_approved = true,
                    updated_at = "2026-08-21T00:00:00Z",
                    notes = (string?)null
                }));
            }

            return Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        }

        private static byte[] CompressBrotli(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                compressor.Write(bytes);
            }

            return output.ToArray();
        }

        private static byte[] CreateChannelConfig(
            byte[] publicKey,
            string envelopeUrl = EnvelopeUrl,
            string keyId = KeyId,
            string? publicKeyBase64 = null)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = SignedUpdateChannelConfig.CurrentSchema,
                kind = SignedUpdateChannelConfig.ExpectedKind,
                envelope_url = envelopeUrl,
                key_id = keyId,
                public_key_spki_base64 = publicKeyBase64 ?? Convert.ToBase64String(publicKey)
            });
        }

        private static SignedUpdateStateStore CreateStateStore(
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

        private static void WithTempRoot(string label, Action<string> action)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                action(root);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static HttpResponseMessage Content(HttpStatusCode statusCode, byte[] bytes)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(bytes)
            };
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static bool HasArgument(string expected)
        {
            return Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, expected, StringComparison.Ordinal));
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

        private sealed record SignedFixture(byte[] EnvelopeUtf8, VerifiedSignedUpdate Update);

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
    }
}
