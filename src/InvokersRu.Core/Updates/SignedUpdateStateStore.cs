using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Updates
{
    public sealed class SignedUpdateState
    {
        internal SignedUpdateState(
            ulong highestAcceptedSequence,
            string highestAcceptedPayloadSha256,
            SignedUpdateLastKnownGood? lastKnownGood)
        {
            HighestAcceptedSequence = highestAcceptedSequence;
            HighestAcceptedPayloadSha256 = highestAcceptedPayloadSha256;
            LastKnownGood = lastKnownGood;
        }

        public ulong HighestAcceptedSequence { get; }
        public string HighestAcceptedPayloadSha256 { get; }
        public SignedUpdateLastKnownGood? LastKnownGood { get; }
    }

    public sealed class SignedUpdateLastKnownGood
    {
        internal SignedUpdateLastKnownGood(
            ulong sequence,
            string releaseId,
            string artifactId,
            string manifestPayloadSha256,
            string catalogCompressedSha256,
            long catalogCompressedBytes,
            string catalogUncompressedSha256,
            long catalogUncompressedBytes,
            DateTimeOffset acceptedUtc)
        {
            Sequence = sequence;
            ReleaseId = releaseId;
            ArtifactId = artifactId;
            ManifestPayloadSha256 = manifestPayloadSha256;
            CatalogCompressedSha256 = catalogCompressedSha256;
            CatalogCompressedBytes = catalogCompressedBytes;
            CatalogUncompressedSha256 = catalogUncompressedSha256;
            CatalogUncompressedBytes = catalogUncompressedBytes;
            DateTimeOffset utc = acceptedUtc.ToUniversalTime();
            AcceptedUtc = new DateTimeOffset(
                utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond,
                TimeSpan.Zero);
        }

        public ulong Sequence { get; }
        public string ReleaseId { get; }
        public string ArtifactId { get; }
        public string ManifestPayloadSha256 { get; }
        public string CatalogCompressedSha256 { get; }
        public long CatalogCompressedBytes { get; }
        public string CatalogUncompressedSha256 { get; }
        public long CatalogUncompressedBytes { get; }
        public DateTimeOffset AcceptedUtc { get; }
    }

    /// <summary>
    /// Persists anti-rollback and last-known-good identities below one fixed LocalAppData directory.
    /// The public API never accepts a caller-selected path; the internal constructor exists only for tests.
    /// </summary>
    public sealed class SignedUpdateStateStore
    {
        public const int StateSchema = 1;
        public const string StateKind = "invokers-ru-signed-update-state";
        public const string StateFileName = "signed-update-state.v1.json";
        private const string LockFileName = "signed-update-state.lock";
        private const int MaxStateBytes = 64 * 1024;

        private static readonly JsonSerializerOptions StrictJson = new JsonSerializerOptions
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 16,
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };

        private readonly string _anchorDirectory;
        private readonly string _stateDirectory;
        private readonly string _lockPath;
        private readonly Func<DateTimeOffset> _utcNow;

        public SignedUpdateStateStore()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            {
                throw new InvalidOperationException("LocalApplicationData is unavailable; signed-update state cannot be persisted safely.");
            }

            _anchorDirectory = Path.GetFullPath(localAppData);
            _stateDirectory = Path.Combine(_anchorDirectory, "InvokersRu", "updates");
            StatePath = Path.Combine(_stateDirectory, StateFileName);
            _lockPath = Path.Combine(_stateDirectory, LockFileName);
            _utcNow = static () => DateTimeOffset.UtcNow;
        }

        private SignedUpdateStateStore(string isolatedTestDirectory, Func<DateTimeOffset> utcNow)
        {
            if (string.IsNullOrWhiteSpace(isolatedTestDirectory)) throw new ArgumentException("Test directory is required.", nameof(isolatedTestDirectory));
            ArgumentNullException.ThrowIfNull(utcNow);
            _stateDirectory = Path.GetFullPath(isolatedTestDirectory);
            _anchorDirectory = Directory.GetParent(_stateDirectory)?.FullName
                ?? throw new InvalidDataException("Test state directory must have a parent.");
            StatePath = Path.Combine(_stateDirectory, StateFileName);
            _lockPath = Path.Combine(_stateDirectory, LockFileName);
            _utcNow = utcNow;
            EnsureWithinAnchor(StatePath);
            EnsureWithinAnchor(_lockPath);
        }

        public string StatePath { get; }

        public static string DefaultStatePath()
        {
            return new SignedUpdateStateStore().StatePath;
        }

        public SignedUpdateState? Load()
        {
            if (!Directory.Exists(_stateDirectory)) return null;
            AssertSafeExistingDirectoryChain();
            return LoadCore();
        }

        public SignedUpdateVerificationContext CreateVerificationContext(
            DateTimeOffset nowUtc,
            string runningPatcherVersion,
            bool allowExpiredCachedManifest = false)
        {
            SignedUpdateState? state = Load();
            return new SignedUpdateVerificationContext(
                nowUtc,
                runningPatcherVersion,
                state?.HighestAcceptedSequence ?? 0,
                state?.HighestAcceptedPayloadSha256,
                allowExpiredCachedManifest);
        }

        public SignedUpdateState RecordAcceptedManifest(VerifiedSignedUpdate verified)
        {
            ArgumentNullException.ThrowIfNull(verified);
            if (verified.IsExpiredAt(_utcNow()))
            {
                throw new InvalidOperationException("An expired offline manifest cannot advance anti-rollback state.");
            }

            return Mutate(current => new SignedUpdateState(
                verified.Manifest.Sequence,
                verified.PayloadSha256,
                current?.LastKnownGood));
        }

        /// <summary>
        /// Marks catalog identity as last-known-good only after the caller has verified and installed it.
        /// This method records metadata; it never downloads, executes, or installs an artifact.
        /// </summary>
        public SignedUpdateState RecordLastKnownGood(VerifiedSignedUpdate verified, DateTimeOffset acceptedUtc)
        {
            ArgumentNullException.ThrowIfNull(verified);
            if (verified.PatcherDisposition == SignedUpdatePatcherDisposition.TooOld)
            {
                throw new InvalidOperationException("A below-minimum patcher cannot establish last-known-good update metadata.");
            }

            bool expiredNow = verified.IsExpiredAt(_utcNow());
            var lastKnownGood = new SignedUpdateLastKnownGood(
                verified.Manifest.Sequence,
                verified.Manifest.ReleaseId,
                verified.Manifest.Catalog.ArtifactId,
                verified.PayloadSha256,
                verified.Manifest.Catalog.CompressedSha256,
                verified.Manifest.Catalog.CompressedBytes,
                verified.Manifest.Catalog.UncompressedSha256,
                verified.Manifest.Catalog.UncompressedBytes,
                acceptedUtc);

            return Mutate(current =>
            {
                if (expiredNow)
                {
                    if (current?.LastKnownGood == null
                        || !LastKnownGoodMatchesVerified(current.LastKnownGood, verified))
                    {
                        throw new InvalidOperationException("Expired metadata may use only an already-recorded identical last-known-good artifact.");
                    }

                    return current;
                }

                ulong highestSequence = current == null
                    ? verified.Manifest.Sequence
                    : Math.Max(current.HighestAcceptedSequence, verified.Manifest.Sequence);
                string highestHash = current == null || verified.Manifest.Sequence > current.HighestAcceptedSequence
                    ? verified.PayloadSha256
                    : current.HighestAcceptedPayloadSha256;
                if (current != null
                    && verified.Manifest.Sequence == current.HighestAcceptedSequence
                    && !FixedHashEquals(verified.PayloadSha256, current.HighestAcceptedPayloadSha256))
                {
                    throw new InvalidDataException("Last-known-good manifest conflicts with the accepted payload at the same sequence.");
                }

                return new SignedUpdateState(highestSequence, highestHash, lastKnownGood);
            });
        }

        internal void AssertCurrentForRemoteArtifact(VerifiedSignedUpdate verified)
        {
            ArgumentNullException.ThrowIfNull(verified);
            if (!verified.CanDownloadRemoteArtifactAt(_utcNow()))
            {
                throw new InvalidOperationException("The verified manifest is expired or requires a newer patcher.");
            }

            SignedUpdateState? state = Load();
            if (state == null
                || state.HighestAcceptedSequence != verified.Manifest.Sequence
                || !FixedHashEquals(state.HighestAcceptedPayloadSha256, verified.PayloadSha256))
            {
                throw new InvalidOperationException("The verified manifest is not the current persisted anti-rollback identity.");
            }
        }

        private SignedUpdateState Mutate(Func<SignedUpdateState?, SignedUpdateState> mutation)
        {
            EnsureSafeStateDirectory();
            using FileStream stateLock = OpenLockFile();
            SignedUpdateState? current = LoadCore();
            SignedUpdateState next = mutation(current);
            ValidateState(next);
            ValidateMonotonicTransition(current, next);
            WriteAtomic(next);
            return next;
        }

        private SignedUpdateState? LoadCore()
        {
            if (!File.Exists(StatePath)) return null;
            AssertRegularFile(StatePath, "Signed-update state");
            try
            {
                using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is < 2 or > MaxStateBytes)
                {
                    throw new InvalidDataException("Signed-update state is empty or exceeds its fixed size cap.");
                }

                byte[] bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    throw new InvalidDataException("Signed-update state must be UTF-8 without a BOM.");
                }

                SignedUpdateStateFile? model = JsonSerializer.Deserialize<SignedUpdateStateFile>(bytes, StrictJson);
                if (model == null) throw new InvalidDataException("Signed-update state is JSON null.");
                SignedUpdateState state = FromFileModel(model);
                ValidateState(state);
                return state;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Signed-update state is not strict schema-valid JSON.", exception);
            }
        }

        private void WriteAtomic(SignedUpdateState state)
        {
            SignedUpdateStateFile model = ToFileModel(state);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(model, StrictJson);
            if (json.Length + 1 > MaxStateBytes) throw new InvalidDataException("Signed-update state exceeds its fixed size cap.");
            byte[] bytes = new byte[json.Length + 1];
            Buffer.BlockCopy(json, 0, bytes, 0, json.Length);
            bytes[^1] = (byte)'\n';

            string tempPath = Path.Combine(_stateDirectory, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
            EnsureWithinAnchor(tempPath);
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                AssertRegularFile(tempPath, "Staged signed-update state");
                if (File.Exists(StatePath))
                {
                    AssertRegularFile(StatePath, "Existing signed-update state");
                    File.Replace(tempPath, StatePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, StatePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private FileStream OpenLockFile()
        {
            if (File.Exists(_lockPath)) AssertRegularFile(_lockPath, "Signed-update state lock");
            try
            {
                FileStream stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.WriteThrough);
                AssertRegularFile(_lockPath, "Signed-update state lock");
                return stream;
            }
            catch (IOException exception)
            {
                throw new IOException("Signed-update state is busy in another patcher process.", exception);
            }
        }

        private void EnsureSafeStateDirectory()
        {
            if (!Directory.Exists(_anchorDirectory))
            {
                throw new DirectoryNotFoundException("The signed-update state anchor directory is missing.");
            }

            EnsureWithinAnchor(_stateDirectory);
            string relative = Path.GetRelativePath(_anchorDirectory, _stateDirectory);
            string current = _anchorDirectory;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current)) Directory.CreateDirectory(current);
                AssertDirectoryNotReparse(current, "Signed-update state directory");
            }
        }

        private void AssertSafeExistingDirectoryChain()
        {
            EnsureWithinAnchor(_stateDirectory);
            string relative = Path.GetRelativePath(_anchorDirectory, _stateDirectory);
            string current = _anchorDirectory;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current)) throw new DirectoryNotFoundException("Signed-update state directory disappeared during validation.");
                AssertDirectoryNotReparse(current, "Signed-update state directory");
            }
        }

        private void EnsureWithinAnchor(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(_anchorDirectory, fullPath);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed-update state path escaped its fixed root.");
            }
        }

        private static void AssertDirectoryNotReparse(string path, string label)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{label} is not a regular directory: {path}");
            }
        }

        private static void AssertRegularFile(string path, string label)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException($"{label} is not a regular file: {path}");
            }
        }

        private static SignedUpdateState FromFileModel(SignedUpdateStateFile model)
        {
            if (model.Schema != StateSchema || !string.Equals(model.Kind, StateKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed-update state identity or schema is unsupported.");
            }

            SignedUpdateLastKnownGood? lastKnownGood = null;
            if (model.LastKnownGood != null)
            {
                lastKnownGood = new SignedUpdateLastKnownGood(
                    model.LastKnownGood.Sequence,
                    model.LastKnownGood.ReleaseId,
                    model.LastKnownGood.ArtifactId,
                    model.LastKnownGood.ManifestPayloadSha256,
                    model.LastKnownGood.CatalogCompressedSha256,
                    model.LastKnownGood.CatalogCompressedBytes,
                    model.LastKnownGood.CatalogUncompressedSha256,
                    model.LastKnownGood.CatalogUncompressedBytes,
                    ParseUtcSecond(model.LastKnownGood.AcceptedUtc));
            }

            return new SignedUpdateState(
                model.HighestAcceptedSequence,
                model.HighestAcceptedPayloadSha256,
                lastKnownGood);
        }

        private static SignedUpdateStateFile ToFileModel(SignedUpdateState state)
        {
            SignedUpdateLastKnownGood? lastKnownGood = state.LastKnownGood;
            return new SignedUpdateStateFile
            {
                Schema = StateSchema,
                Kind = StateKind,
                HighestAcceptedSequence = state.HighestAcceptedSequence,
                HighestAcceptedPayloadSha256 = state.HighestAcceptedPayloadSha256,
                LastKnownGood = lastKnownGood == null ? null : new SignedUpdateLastKnownGoodFile
                {
                    Sequence = lastKnownGood.Sequence,
                    ReleaseId = lastKnownGood.ReleaseId,
                    ArtifactId = lastKnownGood.ArtifactId,
                    ManifestPayloadSha256 = lastKnownGood.ManifestPayloadSha256,
                    CatalogCompressedSha256 = lastKnownGood.CatalogCompressedSha256,
                    CatalogCompressedBytes = lastKnownGood.CatalogCompressedBytes,
                    CatalogUncompressedSha256 = lastKnownGood.CatalogUncompressedSha256,
                    CatalogUncompressedBytes = lastKnownGood.CatalogUncompressedBytes,
                    AcceptedUtc = FormatUtcSecond(lastKnownGood.AcceptedUtc)
                }
            };
        }

        private static void ValidateState(SignedUpdateState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.HighestAcceptedSequence == 0 || !IsUpperSha256(state.HighestAcceptedPayloadSha256))
            {
                throw new InvalidDataException("Signed-update state must contain a non-zero sequence and uppercase payload SHA-256.");
            }

            SignedUpdateLastKnownGood? lastKnownGood = state.LastKnownGood;
            if (lastKnownGood == null) return;
            if (lastKnownGood.Sequence == 0
                || lastKnownGood.Sequence > state.HighestAcceptedSequence
                || !IsSafeId(lastKnownGood.ReleaseId)
                || !IsSafeId(lastKnownGood.ArtifactId)
                || !IsUpperSha256(lastKnownGood.ManifestPayloadSha256)
                || !IsUpperSha256(lastKnownGood.CatalogCompressedSha256)
                || !IsUpperSha256(lastKnownGood.CatalogUncompressedSha256)
                || lastKnownGood.CatalogCompressedBytes is < 1 or > SignedUpdateLimits.MaxCompressedCatalogBytes
                || lastKnownGood.CatalogUncompressedBytes is < 1 or > SignedUpdateLimits.MaxUncompressedCatalogBytes
                || lastKnownGood.CatalogUncompressedBytes > lastKnownGood.CatalogCompressedBytes * SignedUpdateLimits.MaxCompressionRatio
                || lastKnownGood.AcceptedUtc == default)
            {
                throw new InvalidDataException("Last-known-good signed-update metadata is invalid or exceeds fixed limits.");
            }

            if (lastKnownGood.Sequence == state.HighestAcceptedSequence
                && !FixedHashEquals(lastKnownGood.ManifestPayloadSha256, state.HighestAcceptedPayloadSha256))
            {
                throw new InvalidDataException("Last-known-good payload conflicts with the accepted payload at the same sequence.");
            }
        }

        private static void ValidateMonotonicTransition(SignedUpdateState? current, SignedUpdateState next)
        {
            if (current == null) return;
            if (next.HighestAcceptedSequence < current.HighestAcceptedSequence)
            {
                throw new InvalidDataException("Signed-update anti-rollback sequence cannot decrease.");
            }

            if (next.HighestAcceptedSequence == current.HighestAcceptedSequence
                && !FixedHashEquals(next.HighestAcceptedPayloadSha256, current.HighestAcceptedPayloadSha256))
            {
                throw new InvalidDataException("An accepted signed-update sequence cannot be reused with a different payload.");
            }

            SignedUpdateLastKnownGood? oldLkg = current.LastKnownGood;
            SignedUpdateLastKnownGood? newLkg = next.LastKnownGood;
            if (oldLkg != null && newLkg == null)
            {
                throw new InvalidDataException("A monotonic state update cannot discard last-known-good metadata.");
            }

            if (oldLkg != null && newLkg != null && newLkg.Sequence < oldLkg.Sequence)
            {
                throw new InvalidDataException("Last-known-good sequence cannot decrease.");
            }

            if (oldLkg != null && newLkg != null && newLkg.Sequence == oldLkg.Sequence)
            {
                if (!LastKnownGoodIdentityEquals(oldLkg, newLkg))
                {
                    throw new InvalidDataException("Last-known-good identity cannot change at an accepted sequence.");
                }

                if (newLkg.AcceptedUtc < oldLkg.AcceptedUtc)
                {
                    throw new InvalidDataException("Last-known-good acceptance time cannot move backwards.");
                }
            }
        }

        private static bool LastKnownGoodMatchesVerified(
            SignedUpdateLastKnownGood lastKnownGood,
            VerifiedSignedUpdate verified)
        {
            VerifiedSignedUpdateCatalog catalog = verified.Manifest.Catalog;
            return lastKnownGood.Sequence == verified.Manifest.Sequence
                && string.Equals(lastKnownGood.ReleaseId, verified.Manifest.ReleaseId, StringComparison.Ordinal)
                && string.Equals(lastKnownGood.ArtifactId, catalog.ArtifactId, StringComparison.Ordinal)
                && FixedHashEquals(lastKnownGood.ManifestPayloadSha256, verified.PayloadSha256)
                && FixedHashEquals(lastKnownGood.CatalogCompressedSha256, catalog.CompressedSha256)
                && lastKnownGood.CatalogCompressedBytes == catalog.CompressedBytes
                && FixedHashEquals(lastKnownGood.CatalogUncompressedSha256, catalog.UncompressedSha256)
                && lastKnownGood.CatalogUncompressedBytes == catalog.UncompressedBytes;
        }

        private static bool LastKnownGoodIdentityEquals(
            SignedUpdateLastKnownGood left,
            SignedUpdateLastKnownGood right)
        {
            return left.Sequence == right.Sequence
                && string.Equals(left.ReleaseId, right.ReleaseId, StringComparison.Ordinal)
                && string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal)
                && FixedHashEquals(left.ManifestPayloadSha256, right.ManifestPayloadSha256)
                && FixedHashEquals(left.CatalogCompressedSha256, right.CatalogCompressedSha256)
                && left.CatalogCompressedBytes == right.CatalogCompressedBytes
                && FixedHashEquals(left.CatalogUncompressedSha256, right.CatalogUncompressedSha256)
                && left.CatalogUncompressedBytes == right.CatalogUncompressedBytes;
        }

        private static bool FixedHashEquals(string left, string right)
        {
            if (!IsUpperSha256(left) || !IsUpperSha256(right)) return false;
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }

        private static bool IsUpperSha256(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'A' && character <= 'F'))) return false;
            }

            return true;
        }

        private static bool IsSafeId(string? value)
        {
            if (value == null || value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0])) return false;
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_' && character != '.') return false;
            }

            return true;
        }

        private static DateTimeOffset ParseUtcSecond(string value)
        {
            if (value.Length != 20
                || !DateTimeOffset.TryParseExact(
                    value,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset result))
            {
                throw new InvalidDataException("last_known_good.accepted_utc must be canonical UTC with whole-second precision.");
            }

            return result;
        }

        private static string FormatUtcSecond(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private sealed class SignedUpdateStateFile
        {
            [JsonRequired]
            [JsonPropertyName("schema")]
            public int Schema { get; init; }

            [JsonRequired]
            [JsonPropertyName("kind")]
            public string Kind { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("highest_accepted_sequence")]
            public ulong HighestAcceptedSequence { get; init; }

            [JsonRequired]
            [JsonPropertyName("highest_accepted_payload_sha256")]
            public string HighestAcceptedPayloadSha256 { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("last_known_good")]
            public SignedUpdateLastKnownGoodFile? LastKnownGood { get; init; }
        }

        private sealed class SignedUpdateLastKnownGoodFile
        {
            [JsonRequired]
            [JsonPropertyName("sequence")]
            public ulong Sequence { get; init; }

            [JsonRequired]
            [JsonPropertyName("release_id")]
            public string ReleaseId { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("artifact_id")]
            public string ArtifactId { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("manifest_payload_sha256")]
            public string ManifestPayloadSha256 { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("catalog_compressed_sha256")]
            public string CatalogCompressedSha256 { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("catalog_compressed_bytes")]
            public long CatalogCompressedBytes { get; init; }

            [JsonRequired]
            [JsonPropertyName("catalog_uncompressed_sha256")]
            public string CatalogUncompressedSha256 { get; init; } = string.Empty;

            [JsonRequired]
            [JsonPropertyName("catalog_uncompressed_bytes")]
            public long CatalogUncompressedBytes { get; init; }

            [JsonRequired]
            [JsonPropertyName("accepted_utc")]
            public string AcceptedUtc { get; init; } = string.Empty;
        }
    }
}
