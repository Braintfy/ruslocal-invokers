using InvokersRu.Core.Translations;
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// Content-addressed storage for already verified update envelopes and catalogs. Public construction
    /// always uses one fixed LocalAppData root; the isolated constructor exists for smoke tests only.
    /// </summary>
    public sealed class SignedUpdateCacheStore
    {
        private const string EnvelopeDirectoryName = "envelopes";
        private const string CatalogDirectoryName = "catalogs";

        private readonly string _anchorDirectory;
        private readonly string _rootDirectory;
        private readonly string _envelopeDirectory;
        private readonly string _catalogDirectory;

        public SignedUpdateCacheStore()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            {
                throw new InvalidOperationException("LocalApplicationData is unavailable; signed updates cannot be cached safely.");
            }

            _anchorDirectory = Path.GetFullPath(localAppData);
            _rootDirectory = Path.Combine(_anchorDirectory, "InvokersRu", "updates");
            _envelopeDirectory = Path.Combine(_rootDirectory, EnvelopeDirectoryName);
            _catalogDirectory = Path.Combine(_rootDirectory, CatalogDirectoryName);
        }

        internal SignedUpdateCacheStore(string isolatedDirectory)
        {
            if (string.IsNullOrWhiteSpace(isolatedDirectory))
                throw new ArgumentException("An isolated signed-update cache directory is required.", nameof(isolatedDirectory));
            _rootDirectory = Path.GetFullPath(isolatedDirectory);
            _anchorDirectory = Directory.GetParent(_rootDirectory)?.FullName
                ?? throw new InvalidDataException("The isolated cache directory must have a parent.");
            _envelopeDirectory = Path.Combine(_rootDirectory, EnvelopeDirectoryName);
            _catalogDirectory = Path.Combine(_rootDirectory, CatalogDirectoryName);
            EnsureWithinAnchor(_rootDirectory);
        }

        public string RootDirectory => _rootDirectory;

        public string StoreEnvelope(byte[] envelopeUtf8, VerifiedSignedUpdate verified)
        {
            ArgumentNullException.ThrowIfNull(envelopeUtf8);
            ArgumentNullException.ThrowIfNull(verified);
            if (envelopeUtf8.Length is < 2 or > SignedUpdateLimits.MaxEnvelopeBytes)
                throw new InvalidDataException("Verified update envelope has an invalid byte count.");
            return StoreContentAddressed(
                _envelopeDirectory,
                verified.PayloadSha256,
                ".envelope.json",
                envelopeUtf8,
                SignedUpdateLimits.MaxEnvelopeBytes,
                "signed-update envelope");
        }

        public string StoreCatalog(byte[] compressedBytes, VerifiedSignedUpdate verified)
        {
            ArgumentNullException.ThrowIfNull(compressedBytes);
            ArgumentNullException.ThrowIfNull(verified);
            VerifiedSignedUpdateCatalog expected = verified.Manifest.Catalog;
            if (compressedBytes.LongLength != expected.CompressedBytes
                || !FixedHashEquals(HashBytes(compressedBytes), expected.CompressedSha256))
            {
                throw new InvalidDataException("Compressed catalog does not match the signed size and SHA-256 pins.");
            }

            if (expected.UncompressedBytes > int.MaxValue)
                throw new InvalidDataException("Signed catalog exceeds the supported in-memory validation size.");
            byte[] uncompressed;
            if (string.Equals(expected.Compression, "none", StringComparison.Ordinal))
            {
                if (expected.CompressedBytes != expected.UncompressedBytes)
                    throw new InvalidDataException("Uncompressed catalog has contradictory signed byte counts.");
                uncompressed = (byte[])compressedBytes.Clone();
            }
            else if (string.Equals(expected.Compression, "brotli", StringComparison.Ordinal))
            {
                uncompressed = new byte[checked((int)expected.UncompressedBytes)];
                var decoder = new BrotliDecoder();
                OperationStatus status;
                int consumed;
                int written;
                try
                {
                    status = decoder.Decompress(compressedBytes, uncompressed, out consumed, out written);
                }
                finally
                {
                    decoder.Dispose();
                }

                if (status != OperationStatus.Done
                    || consumed != compressedBytes.Length
                    || written != uncompressed.Length)
                {
                    throw new InvalidDataException("Catalog Brotli stream is truncated, has trailing data, or does not match its signed expanded size.");
                }
            }
            else
            {
                throw new InvalidDataException("Signed catalog compression is unsupported by this patcher.");
            }

            if (!FixedHashEquals(HashBytes(uncompressed), expected.UncompressedSha256))
                throw new InvalidDataException("Expanded catalog SHA-256 does not match the signed manifest.");
            if (uncompressed.Length >= 3 && uncompressed[0] == 0xEF && uncompressed[1] == 0xBB && uncompressed[2] == 0xBF)
                throw new InvalidDataException("Expanded catalog must be strict UTF-8 without a BOM.");

            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(uncompressed);
            if (catalog.Count != expected.RecordCount)
                throw new InvalidDataException("Expanded catalog record count does not match the signed manifest.");

            return StoreContentAddressed(
                _catalogDirectory,
                expected.UncompressedSha256,
                ".jsonl",
                uncompressed,
                SignedUpdateLimits.MaxUncompressedCatalogBytes,
                "signed-update catalog");
        }

        public byte[] LoadEnvelope(string payloadSha256)
        {
            string path = PathFor(_envelopeDirectory, payloadSha256, ".envelope.json");
            return ReadVerifiedFile(path, SignedUpdateLimits.MaxEnvelopeBytes, expectedBytes: null, expectedSha256: null, "signed-update envelope");
        }

        public string RequireCatalog(VerifiedSignedUpdate verified)
        {
            ArgumentNullException.ThrowIfNull(verified);
            VerifiedSignedUpdateCatalog catalog = verified.Manifest.Catalog;
            string path = PathFor(_catalogDirectory, catalog.UncompressedSha256, ".jsonl");
            byte[] bytes = ReadVerifiedFile(
                path,
                SignedUpdateLimits.MaxUncompressedCatalogBytes,
                catalog.UncompressedBytes,
                catalog.UncompressedSha256,
                "signed-update catalog");
            TranslationCatalog parsed = TranslationCatalog.LoadJsonLinesBytes(bytes);
            if (parsed.Count != catalog.RecordCount)
                throw new InvalidDataException("Cached signed-update catalog record count is inconsistent.");
            return path;
        }

        public bool HasCatalog(VerifiedSignedUpdate verified)
        {
            try
            {
                RequireCatalog(verified);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public IReadOnlyList<string> EnumerateEnvelopePayloadHashes()
        {
            if (!Directory.Exists(_envelopeDirectory)) return Array.Empty<string>();
            AssertSafeExistingDirectories(_envelopeDirectory);
            string[] files = Directory.GetFiles(_envelopeDirectory, "*.envelope.json", SearchOption.TopDirectoryOnly);
            if (files.Length > 256)
                throw new InvalidDataException("Signed-update envelope history exceeds its fixed file-count cap.");
            var hashes = new List<string>(files.Length);
            foreach (string file in files)
            {
                EnsureWithinAnchor(file);
                AssertRegularFile(file, "Cached signed-update envelope");
                string name = Path.GetFileName(file);
                const string suffix = ".envelope.json";
                if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string hash = name.Substring(0, name.Length - suffix.Length);
                try
                {
                    ValidateHash(hash);
                    hashes.Add(hash);
                }
                catch (InvalidDataException)
                {
                    // Unrecognized regular files are not trust inputs and are deliberately ignored.
                }
            }

            hashes.Sort(StringComparer.Ordinal);
            return hashes;
        }

        private string StoreContentAddressed(
            string directory,
            string sha256,
            string suffix,
            byte[] bytes,
            long maximumBytes,
            string label)
        {
            ValidateHash(sha256);
            if (bytes.LongLength is < 1 || bytes.LongLength > maximumBytes)
                throw new InvalidDataException($"{label} has an invalid byte count.");
            EnsureSafeDirectories(directory);
            string finalPath = PathFor(directory, sha256, suffix);
            if (File.Exists(finalPath))
            {
                ReadVerifiedFile(finalPath, maximumBytes, bytes.LongLength, HashBytes(bytes), label);
                return finalPath;
            }

            string tempPath = Path.Combine(directory, $".{sha256}.{Guid.NewGuid():N}.tmp");
            EnsureWithinAnchor(tempPath);
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                AssertRegularFile(tempPath, $"Staged {label}");
                try
                {
                    File.Move(tempPath, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    ReadVerifiedFile(finalPath, maximumBytes, bytes.LongLength, HashBytes(bytes), label);
                }

                ReadVerifiedFile(finalPath, maximumBytes, bytes.LongLength, HashBytes(bytes), label);
                return finalPath;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private byte[] ReadVerifiedFile(
            string path,
            long maximumBytes,
            long? expectedBytes,
            string? expectedSha256,
            string label)
        {
            EnsureWithinAnchor(path);
            AssertSafeExistingDirectories(Path.GetDirectoryName(path)
                ?? throw new InvalidDataException($"{label} has no parent directory."));
            if (!File.Exists(path)) throw new FileNotFoundException($"Cached {label} is missing.", path);
            AssertRegularFile(path, $"Cached {label}");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 || stream.Length > maximumBytes
                || (expectedBytes.HasValue && stream.Length != expectedBytes.Value))
            {
                throw new InvalidDataException($"Cached {label} has an invalid byte count.");
            }

            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (expectedSha256 != null && !FixedHashEquals(HashBytes(bytes), expectedSha256))
                throw new InvalidDataException($"Cached {label} SHA-256 is inconsistent.");
            return bytes;
        }

        private string PathFor(string directory, string sha256, string suffix)
        {
            ValidateHash(sha256);
            string path = Path.Combine(directory, sha256 + suffix);
            EnsureWithinAnchor(path);
            return path;
        }

        private void EnsureSafeDirectories(string leaf)
        {
            EnsureWithinAnchor(leaf);
            if (!Directory.Exists(_anchorDirectory))
                throw new DirectoryNotFoundException("Signed-update cache anchor directory is missing.");
            string relative = Path.GetRelativePath(_anchorDirectory, leaf);
            string current = _anchorDirectory;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current)) Directory.CreateDirectory(current);
                AssertDirectoryNotReparse(current, "Signed-update cache directory");
            }
        }

        private void AssertSafeExistingDirectories(string leaf)
        {
            EnsureWithinAnchor(leaf);
            string relative = Path.GetRelativePath(_anchorDirectory, leaf);
            string current = _anchorDirectory;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current)) throw new DirectoryNotFoundException("Signed-update cache directory is missing.");
                AssertDirectoryNotReparse(current, "Signed-update cache directory");
            }
        }

        private void EnsureWithinAnchor(string path)
        {
            string fullAnchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_anchorDirectory));
            string fullPath = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(fullAnchor, fullPath);
            if (relative == ".." || Path.IsPathFullyQualified(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Signed-update cache path escaped its fixed LocalAppData anchor.");
            }
        }

        private static void AssertDirectoryNotReparse(string path, string label)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{label} is not a regular local directory.");
        }

        private static void AssertRegularFile(string path, string label)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"{label} is not a regular file.");
        }

        private static string HashBytes(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static bool FixedHashEquals(string left, string right)
        {
            if (left.Length != 64 || right.Length != 64) return false;
            try
            {
                return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void ValidateHash(string value)
        {
            if (value.Length != 64)
                throw new InvalidDataException("Signed-update content address must be one SHA-256 digest.");
            try
            {
                byte[] bytes = Convert.FromHexString(value);
                if (bytes.Length != 32 || !string.Equals(Convert.ToHexString(bytes), value, StringComparison.Ordinal))
                    throw new InvalidDataException("Signed-update content address must use canonical uppercase hexadecimal.");
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Signed-update content address is not hexadecimal.", exception);
            }
        }
    }
}
