using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace InvokersRu.Core.Loc1
{
    public static class Loc1Codec
    {
        private const int FixedHeaderMinimum = 0x52;
        private const long MaximumExpandedBytes = 256L * 1024L * 1024L;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOC1");
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static Loc1Document ReadFile(string path)
        {
            return Parse(ReadContainer(path));
        }

        public static byte[] ReadContainer(string path)
        {
            byte[] input = File.ReadAllBytes(path);
            if (HasMagic(input))
            {
                return input;
            }

            using var source = new MemoryStream(input, writable: false);
            using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: false);
            using var expanded = new MemoryStream();
            CopyWithLimit(brotli, expanded, MaximumExpandedBytes);
            byte[] result = expanded.ToArray();
            if (!HasMagic(result))
            {
                throw new Loc1FormatException("Container is neither raw LOC1 nor a Brotli-compressed LOC1 file.");
            }

            return result;
        }

        public static Loc1Document Parse(byte[] bytes)
        {
            if (!HasMagic(bytes) || bytes.Length < FixedHeaderMinimum)
            {
                throw new Loc1FormatException("Missing LOC1 magic or truncated header.");
            }

            uint formatVersion = ReadUInt32(bytes, 0x04);
            uint localeId = ReadUInt32(bytes, 0x08);
            uint releaseRevision = ReadUInt32(bytes, 0x0C);
            uint localeRevision = ReadUInt32(bytes, 0x10);
            uint declaredEntryCount = ReadUInt32(bytes, 0x1C);
            ulong headerSizeRaw = ReadUInt64(bytes, 0x20);
            ulong dataOffsetRaw = ReadUInt64(bytes, 0x28);
            ulong dataLengthRaw = ReadUInt64(bytes, 0x30);
            ulong collisionOffsetRaw = ReadUInt64(bytes, 0x40);
            ulong keyBlobOffsetRaw = ReadUInt64(bytes, 0x48);

            if (formatVersion != 4)
            {
                throw new Loc1FormatException($"Unsupported LOC1 schema {formatVersion}; expected schema 4.");
            }

            if (headerSizeRaw < FixedHeaderMinimum || headerSizeRaw > int.MaxValue || dataOffsetRaw > int.MaxValue || dataLengthRaw > int.MaxValue)
            {
                throw new Loc1FormatException("LOC1 offsets exceed supported bounds.");
            }

            int headerSize = checked((int)headerSizeRaw);
            int dataOffset = checked((int)dataOffsetRaw);
            int dataLength = checked((int)dataLengthRaw);
            if (headerSize > dataOffset || dataOffset > bytes.Length || dataLength != bytes.Length - dataOffset)
            {
                throw new Loc1FormatException("LOC1 header/index/data boundaries are inconsistent.");
            }

            if (declaredEntryCount > int.MaxValue)
            {
                throw new Loc1FormatException("LOC1 entry count exceeds supported bounds.");
            }

            int indexLength = checked((int)declaredEntryCount * 16);
            if (headerSize + indexLength != dataOffset)
            {
                throw new Loc1FormatException("LOC1 schema-4 file contains collision/key sections that this conservative writer does not support.");
            }

            if (bytes.AsSpan(0x38, 8).ToArray().Any(value => value != 0)
                || collisionOffsetRaw != dataOffsetRaw
                || keyBlobOffsetRaw != dataOffsetRaw)
            {
                throw new Loc1FormatException("LOC1 collision/key-blob layout is not the supported empty layout.");
            }

            int guidLength = ReadUInt16(bytes, 0x50);
            int versionLengthOffset = checked(0x52 + guidLength);
            if (versionLengthOffset + 2 > headerSize)
            {
                throw new Loc1FormatException("LOC1 content GUID exceeds the header.");
            }

            string contentGuid = DecodeUtf8(bytes, 0x52, guidLength, "content GUID");
            int versionLength = ReadUInt16(bytes, versionLengthOffset);
            if (versionLengthOffset + 2 + versionLength > headerSize)
            {
                throw new Loc1FormatException("LOC1 content version exceeds the header.");
            }

            string contentVersion = DecodeUtf8(bytes, versionLengthOffset + 2, versionLength, "content version");
            var entries = new List<Loc1Entry>(checked((int)declaredEntryCount));
            var hashes = new HashSet<ulong>();
            ulong previousHash = 0;
            for (int index = 0; index < declaredEntryCount; index++)
            {
                int recordOffset = checked(headerSize + (index * 16));
                ulong keyHash = ReadUInt64(bytes, recordOffset);
                uint valueOffset = ReadUInt32(bytes, recordOffset + 8);
                uint valueLength = ReadUInt32(bytes, recordOffset + 12);
                if (!hashes.Add(keyHash))
                {
                    throw new Loc1FormatException($"Duplicate LOC1 key hash {keyHash:X16}.");
                }

                if (index > 0 && keyHash <= previousHash)
                {
                    throw new Loc1FormatException($"LOC1 key hashes are not strictly increasing at record {index}.");
                }

                previousHash = keyHash;

                bool offsetNull = valueOffset == uint.MaxValue;
                if ((offsetNull && valueLength != 0) || (!offsetNull && valueLength == uint.MaxValue))
                {
                    throw new Loc1FormatException($"Invalid empty-value sentinel at record {index}.");
                }

                string? value = null;
                if (!offsetNull)
                {
                    ulong end = (ulong)valueOffset + valueLength;
                    if (end > dataLengthRaw)
                    {
                        throw new Loc1FormatException($"Out-of-range value span at record {index}.");
                    }

                    value = DecodeUtf8(bytes, checked(dataOffset + (int)valueOffset), checked((int)valueLength), $"value {keyHash:X16}");
                }

                entries.Add(new Loc1Entry(index, keyHash, valueOffset, valueLength, value));
            }

            ValidateOriginalLayout(entries, dataLengthRaw);
            byte[] header = new byte[headerSize];
            Buffer.BlockCopy(bytes, 0, header, 0, headerSize);
            return new Loc1Document(header, formatVersion, localeId, releaseRevision, localeRevision, dataOffsetRaw, dataLengthRaw, contentGuid, contentVersion, entries);
        }

        public static byte[] BuildRaw(Loc1Document document)
        {
            if ((ulong)document.Header.Length + ((ulong)document.Entries.Count * 16UL) != document.DataOffset)
            {
                throw new Loc1FormatException("Entry count would change the fixed LOC1 data offset.");
            }

            var assigned = new Dictionary<int, (uint Offset, uint Length)>();
            using var data = new MemoryStream();

            IEnumerable<Loc1Entry> originalLayout = document.Entries
                .Where(entry => !entry.WasNull)
                .OrderBy(entry => entry.OriginalOffset);
            foreach (Loc1Entry entry in originalLayout)
            {
                AssignValue(entry, data, assigned);
            }

            Loc1Entry? newlyFilledSentinel = document.Entries.FirstOrDefault(entry => entry.WasNull && entry.Value != null);
            if (newlyFilledSentinel != null)
            {
                throw new Loc1FormatException($"Conservative MVP writer refuses to fill empty sentinel {newlyFilledSentinel.Id}.");
            }

            if (data.Length > uint.MaxValue)
            {
                throw new Loc1FormatException("Generated LOC1 data section exceeds 4 GiB.");
            }

            byte[] header = (byte[])document.Header.Clone();
            WriteUInt64(header, 0x30, checked((ulong)data.Length));
            int totalLength = checked((int)document.DataOffset + (int)data.Length);
            byte[] output = new byte[totalLength];
            Buffer.BlockCopy(header, 0, output, 0, header.Length);

            foreach (Loc1Entry entry in document.Entries)
            {
                int recordOffset = checked(header.Length + (entry.Index * 16));
                WriteUInt64(output, recordOffset, entry.KeyHash);
                if (entry.Value == null)
                {
                    WriteUInt32(output, recordOffset + 8, uint.MaxValue);
                    WriteUInt32(output, recordOffset + 12, 0);
                }
                else
                {
                    (uint offset, uint length) = assigned[entry.Index];
                    WriteUInt32(output, recordOffset + 8, offset);
                    WriteUInt32(output, recordOffset + 12, length);
                }
            }

            byte[] dataBytes = data.ToArray();
            Buffer.BlockCopy(dataBytes, 0, output, checked((int)document.DataOffset), dataBytes.Length);
            return output;
        }

        public static byte[] Compress(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                brotli.Write(raw, 0, raw.Length);
            }

            return output.ToArray();
        }

        public static void WriteCompressed(string path, byte[] raw)
        {
            File.WriteAllBytes(path, Compress(raw));
        }

        private static void AssignValue(Loc1Entry entry, MemoryStream data, Dictionary<int, (uint Offset, uint Length)> assigned)
        {
            if (entry.Value == null)
            {
                return;
            }

            byte[] valueBytes = StrictUtf8.GetBytes(entry.Value);
            if (data.Position > uint.MaxValue || valueBytes.LongLength > uint.MaxValue || data.Position + valueBytes.LongLength > uint.MaxValue)
            {
                throw new Loc1FormatException("Generated LOC1 value offset exceeds 32-bit bounds.");
            }

            uint offset = checked((uint)data.Position);
            uint length = checked((uint)valueBytes.Length);
            data.Write(valueBytes, 0, valueBytes.Length);
            assigned.Add(entry.Index, (offset, length));
        }

        private static void ValidateOriginalLayout(IReadOnlyList<Loc1Entry> entries, ulong dataLength)
        {
            ulong cursor = 0;
            foreach (Loc1Entry entry in entries.Where(item => !item.WasNull).OrderBy(item => item.OriginalOffset))
            {
                if (entry.OriginalOffset != cursor)
                {
                    throw new Loc1FormatException($"Unsupported LOC1 data layout near value {entry.Id}: expected offset {cursor}, found {entry.OriginalOffset}.");
                }

                cursor += entry.OriginalLength;
            }

            if (cursor != dataLength)
            {
                throw new Loc1FormatException($"LOC1 value spans cover {cursor} bytes, but the data section contains {dataLength} bytes.");
            }
        }

        private static bool HasMagic(byte[] bytes)
        {
            return bytes.Length >= Magic.Length && bytes[0] == Magic[0] && bytes[1] == Magic[1] && bytes[2] == Magic[2] && bytes[3] == Magic[3];
        }

        private static string DecodeUtf8(byte[] bytes, int offset, int length, string field)
        {
            try
            {
                return StrictUtf8.GetString(bytes, offset, length);
            }
            catch (DecoderFallbackException exception)
            {
                throw new Loc1FormatException($"Invalid UTF-8 in LOC1 {field}: {exception.Message}");
            }
        }

        private static void CopyWithLimit(Stream input, Stream output, long limit)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new Loc1FormatException($"Expanded LOC1 container exceeds the {limit} byte safety limit.");
                }

                output.Write(buffer, 0, read);
            }
        }

        private static ushort ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        private static uint ReadUInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static ulong ReadUInt64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        private static void WriteUInt32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        private static void WriteUInt64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
    }
}
