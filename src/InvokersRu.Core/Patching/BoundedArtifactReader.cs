using InvokersRu.Core.Loc1;
using InvokersRu.Core.Updates;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace InvokersRu.Core.Patching
{
    /// <summary>
    /// Takes one bounded, immutable-by-hash snapshot of an authenticated catalog before any parser can
    /// allocate from its contents.  Opening without write/delete sharing closes the normal Windows update
    /// race; the exact SHA pin remains authoritative on every platform.
    /// </summary>
    internal static class BoundedArtifactReader
    {
        internal const long MaximumRuntimeLoc1Bytes = Loc1Codec.MaximumExpandedBytes;
        internal const long MaximumRuntimeStampBytes = 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static byte[] ReadCatalog(string path, string expectedSha256, string purpose)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
            byte[] snapshot = ReadFile(path, SignedUpdateLimits.MaxUncompressedCatalogBytes, purpose);
            if (!Hashing.FixedEqualsHex(Hashing.Sha256Bytes(snapshot), expectedSha256))
                throw new InvalidDataException($"{purpose} does not match its authenticated SHA-256 pin.");

            return snapshot;
        }

        internal static byte[] ReadRuntimeLoc1(string path, string purpose)
        {
            byte[] snapshot = ReadFile(path, MaximumRuntimeLoc1Bytes, purpose);
            if (snapshot.Length < 4 || snapshot[0] != (byte)'L' || snapshot[1] != (byte)'O'
                || snapshot[2] != (byte)'C' || snapshot[3] != (byte)'1')
                throw new Loc1FormatException($"{purpose} must be an uncompressed raw LOC1 file.");
            return snapshot;
        }

        internal static byte[] ReadRuntimeStamp(string path, string purpose) =>
            ReadFile(path, MaximumRuntimeStampBytes, purpose);

        internal static string? DecodeObservedStamp(byte[] snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Length is < 1 or > 64) return null;
            try
            {
                string value = StrictUtf8.GetString(snapshot);
                return value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-' or '_')
                    ? value
                    : null;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        internal static byte[] ReadFile(string path, long maximumBytes, string purpose)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
            if (maximumBytes < 1 || maximumBytes > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));

            string fullPath = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"{purpose} must be a regular non-reparse file.");

            byte[] snapshot;
            using (var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan))
            {
                long length = stream.Length;
                if (length is < 1 || length > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"{purpose} size is outside the supported limit of {maximumBytes} bytes.");
                }

                snapshot = GC.AllocateUninitializedArray<byte>(checked((int)length));
                stream.ReadExactly(snapshot);
                if (stream.Position != length || stream.Length != length || stream.ReadByte() != -1)
                    throw new IOException($"{purpose} changed while it was being snapshotted.");
            }

            attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"{purpose} changed into a non-regular file while being read.");
            return snapshot;
        }

        internal static string Sha256File(string path, long maximumBytes, string purpose)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
            if (maximumBytes < 1 || maximumBytes > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));

            string fullPath = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"{purpose} must be a regular non-reparse file.");

            byte[] digest;
            using (var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan))
            {
                long length = stream.Length;
                if (length is < 1 || length > maximumBytes)
                    throw new InvalidDataException($"{purpose} size is outside the supported limit of {maximumBytes} bytes.");

                digest = SHA256.HashData(stream);
                if (stream.Position != length || stream.Length != length || stream.ReadByte() != -1)
                    throw new IOException($"{purpose} changed while it was being hashed.");
            }

            attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException($"{purpose} changed into a non-regular file while being hashed.");
            return Convert.ToHexString(digest);
        }
    }
}
