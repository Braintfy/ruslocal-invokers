using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InvokersRu.Core.Updates
{
    public enum SignedUpdateBundleSource
    {
        Remote,
        CachedCurrent,
        LastKnownGood
    }

    public sealed class SignedUpdateBundle
    {
        internal SignedUpdateBundle(
            VerifiedSignedUpdate update,
            string catalogPath,
            SignedUpdateBundleSource source,
            VerifiedSignedUpdate? authority = null)
        {
            Update = update;
            CatalogPath = catalogPath;
            Source = source;
            Authority = authority ?? update;
        }

        /// <summary>
        /// The selected catalog/profile bytes. For a last-known-good fallback this can be older than the
        /// newest accepted channel head.
        /// </summary>
        public VerifiedSignedUpdate Update { get; }

        /// <summary>
        /// The newest accepted, independently authenticated channel head. Its minimum/latest patcher
        /// versions and revocations remain authoritative even when Update is an older offline LKG.
        /// </summary>
        public VerifiedSignedUpdate Authority { get; }
        public string CatalogPath { get; }
        public SignedUpdateBundleSource Source { get; }
    }

    /// <summary>
    /// Coordinates one fixed signed GitHub channel. It never accepts a catalog path, compatibility path,
    /// public key, or endpoint from a write command; those values come from the embedded channel config.
    /// </summary>
    public sealed class SignedUpdateCoordinator : IDisposable
    {
        private readonly SignedUpdateChannelConfig _config;
        private readonly string _runningPatcherVersion;
        private readonly SignedUpdateStateStore _stateStore;
        private readonly SignedUpdateCacheStore _cacheStore;
        private readonly SignedUpdateHttpClient _httpClient;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        public SignedUpdateCoordinator(SignedUpdateChannelConfig config, string runningPatcherVersion)
            : this(
                config,
                runningPatcherVersion,
                new SignedUpdateStateStore(),
                new SignedUpdateCacheStore(),
                new SignedUpdateHttpClient(),
                static () => DateTimeOffset.UtcNow,
                ownsHttpClient: true)
        {
        }

        internal SignedUpdateCoordinator(
            SignedUpdateChannelConfig config,
            string runningPatcherVersion,
            SignedUpdateStateStore stateStore,
            SignedUpdateCacheStore cacheStore,
            SignedUpdateHttpClient httpClient,
            Func<DateTimeOffset> utcNow,
            bool ownsHttpClient = false)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(stateStore);
            ArgumentNullException.ThrowIfNull(cacheStore);
            ArgumentNullException.ThrowIfNull(httpClient);
            ArgumentNullException.ThrowIfNull(utcNow);
            if (string.IsNullOrWhiteSpace(runningPatcherVersion))
                throw new ArgumentException("Running patcher version is required.", nameof(runningPatcherVersion));
            _config = config;
            _runningPatcherVersion = runningPatcherVersion;
            _stateStore = stateStore;
            _cacheStore = cacheStore;
            _httpClient = httpClient;
            _utcNow = utcNow;
            _ownsHttpClient = ownsHttpClient;
        }

        public async Task<SignedUpdateBundle> RefreshAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            byte[] envelopeBytes = await _httpClient.DownloadEnvelopeAsync(
                _config.EnvelopeUrl,
                cancellationToken).ConfigureAwait(false);
            VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                envelopeBytes,
                _config.PublicKeySubjectPublicKeyInfo,
                _config.KeyId,
                _stateStore.CreateVerificationContext(_utcNow(), _runningPatcherVersion));

            _cacheStore.StoreEnvelope(envelopeBytes, verified);
            _stateStore.RecordAcceptedManifest(verified);
            if (verified.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld)
            {
                throw new InvalidOperationException("The signed update channel requires a newer patcher before downloading translation data.");
            }

            if (_cacheStore.HasCatalog(verified))
            {
                return new SignedUpdateBundle(
                    verified,
                    _cacheStore.RequireCatalog(verified),
                    SignedUpdateBundleSource.CachedCurrent);
            }

            using var compressed = new MemoryStream(
                capacity: checked((int)verified.Manifest.Catalog.CompressedBytes));
            await _httpClient.DownloadCatalogAsync(
                verified,
                _stateStore,
                compressed,
                cancellationToken).ConfigureAwait(false);
            string catalogPath = _cacheStore.StoreCatalog(compressed.ToArray(), verified);
            _stateStore.AssertCurrentForRemoteArtifact(verified);
            return new SignedUpdateBundle(verified, catalogPath, SignedUpdateBundleSource.Remote);
        }

        public SignedUpdateBundle? LoadBestAvailable()
        {
            ThrowIfDisposed();
            SignedUpdateState? state = _stateStore.Load();
            if (state == null) return null;

            VerifiedSignedUpdate authority = LoadNewestAcceptedCore(state);

            if (!authority.IsExpiredAt(_utcNow()) && _cacheStore.HasCatalog(authority))
            {
                return new SignedUpdateBundle(
                    authority,
                    _cacheStore.RequireCatalog(authority),
                    SignedUpdateBundleSource.CachedCurrent,
                    authority);
            }

            SignedUpdateLastKnownGood? lkg = state.LastKnownGood;
            if (lkg == null) return null;
            VerifiedSignedUpdate? fallback = TryLoadVerified(
                lkg.ManifestPayloadSha256,
                lkg.Sequence,
                allowExpired: true);
            if (fallback == null
                || !LastKnownGoodMatches(lkg, fallback)
                || authority.Manifest.RevokedReleaseIds.Contains(lkg.ReleaseId, StringComparer.Ordinal)
                || !_cacheStore.HasCatalog(fallback))
            {
                return null;
            }

            return new SignedUpdateBundle(
                fallback,
                _cacheStore.RequireCatalog(fallback),
                SignedUpdateBundleSource.LastKnownGood,
                authority);
        }

        /// <summary>
        /// Returns the newest accepted, independently authenticated channel head even when its catalog is
        /// not available locally. Callers use this metadata only for patcher-version/revocation authority;
        /// it is never itself sufficient to authorize an install.
        /// </summary>
        public VerifiedSignedUpdate? LoadNewestAccepted()
        {
            ThrowIfDisposed();
            SignedUpdateState? state = _stateStore.Load();
            return state == null ? null : LoadNewestAcceptedCore(state);
        }

        /// <summary>
        /// Returns independently signature-verified cached manifests for restore/recovery profile lookup.
        /// Expiry and revocation do not erase historical metadata: they block apply, not restoration of an
        /// immutable official backup. Corrupt or unrelated orphan files are ignored.
        /// </summary>
        public IReadOnlyList<VerifiedSignedUpdate> LoadVerifiedHistory()
        {
            ThrowIfDisposed();
            var result = new List<VerifiedSignedUpdate>();
            foreach (string payloadSha256 in _cacheStore.EnumerateEnvelopePayloadHashes())
            {
                try
                {
                    byte[] envelope = _cacheStore.LoadEnvelope(payloadSha256);
                    VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                        envelope,
                        _config.PublicKeySubjectPublicKeyInfo,
                        _config.KeyId,
                        new SignedUpdateVerificationContext(
                            _utcNow(),
                            _runningPatcherVersion,
                            highestAcceptedSequence: 0,
                            highestAcceptedPayloadSha256: null,
                            allowExpiredCachedManifest: true));
                    if (!FixedHashEquals(payloadSha256, verified.PayloadSha256)) continue;
                    result.Add(verified);
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or System.Security.Cryptography.CryptographicException)
                {
                    // History is content-addressed and independently authenticated. One corrupt orphan must
                    // not hide another exact profile; state-bound lookups still fail when their file is absent.
                }
            }

            result.Sort((left, right) => left.Manifest.Sequence.CompareTo(right.Manifest.Sequence));
            return result;
        }

        /// <summary>
        /// Resolves a catalog only when its content-addressed cache entry still matches an independently
        /// verified manifest. This is used to reconstruct exact compatible-revision restore/recovery pins.
        /// </summary>
        public bool TryGetVerifiedCatalogPath(VerifiedSignedUpdate update, out string catalogPath)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(update);
            try
            {
                catalogPath = _cacheStore.RequireCatalog(update);
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                catalogPath = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Called only after the exact catalog/profile has been installed and post-commit verified.
        /// The state store rechecks the manifest identity under its own lock.
        /// </summary>
        public SignedUpdateState RecordApplied(SignedUpdateBundle bundle)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(bundle);
            _cacheStore.RequireCatalog(bundle.Update);
            return _stateStore.RecordLastKnownGood(bundle.Update, _utcNow());
        }

        public void ExecuteInstall(SignedUpdateBundle bundle, Action installAndVerify)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(bundle);
            ArgumentNullException.ThrowIfNull(installAndVerify);
            _cacheStore.RequireCatalog(bundle.Update);
            if (bundle.Source == SignedUpdateBundleSource.LastKnownGood)
            {
                SignedUpdateState state = _stateStore.Load()
                    ?? throw new InvalidOperationException("Signed-update state is missing for last-known-good apply.");
                VerifiedSignedUpdate newest = TryLoadVerified(
                    state.HighestAcceptedPayloadSha256,
                    state.HighestAcceptedSequence,
                    allowExpired: true)
                    ?? throw new InvalidOperationException("Newest signed-update identity cannot be authenticated for LKG apply.");
                _stateStore.ExecuteWhileLastKnownGoodAllowed(newest, bundle.Update, installAndVerify);
                return;
            }

            _stateStore.ExecuteWhileCurrentAndRecordLastKnownGood(
                bundle.Update,
                _utcNow(),
                installAndVerify);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttpClient) _httpClient.Dispose();
        }

        private VerifiedSignedUpdate? TryLoadVerified(string payloadSha256, ulong sequence, bool allowExpired)
        {
            try
            {
                byte[] envelope = _cacheStore.LoadEnvelope(payloadSha256);
                return SignedUpdateVerifier.Verify(
                    envelope,
                    _config.PublicKeySubjectPublicKeyInfo,
                    _config.KeyId,
                    new SignedUpdateVerificationContext(
                        _utcNow(),
                        _runningPatcherVersion,
                        sequence,
                        payloadSha256,
                        allowExpiredCachedManifest: allowExpired));
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }

        private VerifiedSignedUpdate LoadNewestAcceptedCore(SignedUpdateState state)
        {
            return TryLoadVerified(
                state.HighestAcceptedPayloadSha256,
                state.HighestAcceptedSequence,
                allowExpired: true)
                ?? throw new InvalidDataException(
                    "Newest accepted signed-update metadata is missing, corrupt, or no longer authenticates against its pinned identity.");
        }

        private static bool LastKnownGoodMatches(
            SignedUpdateLastKnownGood lkg,
            VerifiedSignedUpdate update)
        {
            VerifiedSignedUpdateCatalog catalog = update.Manifest.Catalog;
            return lkg.Sequence == update.Manifest.Sequence
                && string.Equals(lkg.ReleaseId, update.Manifest.ReleaseId, StringComparison.Ordinal)
                && string.Equals(lkg.ArtifactId, catalog.ArtifactId, StringComparison.Ordinal)
                && FixedHashEquals(lkg.ManifestPayloadSha256, update.PayloadSha256)
                && FixedHashEquals(lkg.CatalogCompressedSha256, catalog.CompressedSha256)
                && lkg.CatalogCompressedBytes == catalog.CompressedBytes
                && FixedHashEquals(lkg.CatalogUncompressedSha256, catalog.UncompressedSha256)
                && lkg.CatalogUncompressedBytes == catalog.UncompressedBytes;
        }

        private static bool FixedHashEquals(string left, string right)
        {
            if (left.Length != 64 || right.Length != 64) return false;
            try
            {
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(left),
                    Convert.FromHexString(right));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
