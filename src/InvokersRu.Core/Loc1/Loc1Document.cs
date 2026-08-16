using System;
using System.Collections.Generic;

namespace InvokersRu.Core.Loc1
{
    public sealed class Loc1Entry
    {
        public Loc1Entry(int index, ulong keyHash, uint originalOffset, uint originalLength, string? value)
        {
            Index = index;
            KeyHash = keyHash;
            OriginalOffset = originalOffset;
            OriginalLength = originalLength;
            Value = value;
        }

        public int Index { get; }
        public ulong KeyHash { get; }
        public string Id => KeyHash.ToString("X16");
        public uint OriginalOffset { get; }
        public uint OriginalLength { get; }
        public bool WasNull => OriginalOffset == uint.MaxValue && OriginalLength == 0;
        public string? Value { get; set; }
    }

    public sealed class Loc1Document
    {
        public Loc1Document(
            byte[] header,
            uint formatVersion,
            uint localeId,
            uint releaseRevision,
            uint localeRevision,
            ulong dataOffset,
            ulong dataLength,
            string contentGuid,
            string contentVersion,
            IReadOnlyList<Loc1Entry> entries)
        {
            Header = header;
            FormatVersion = formatVersion;
            LocaleId = localeId;
            ReleaseRevision = releaseRevision;
            LocaleRevision = localeRevision;
            DataOffset = dataOffset;
            DataLength = dataLength;
            ContentGuid = contentGuid;
            ContentVersion = contentVersion;
            Entries = entries;
        }

        public byte[] Header { get; }
        public uint FormatVersion { get; }
        public uint LocaleId { get; }
        public uint ReleaseRevision { get; }
        public uint LocaleRevision { get; }
        public ulong HeaderSize => (ulong)Header.Length;
        public ulong DataOffset { get; }
        public ulong DataLength { get; }
        public string ContentGuid { get; }
        public string ContentVersion { get; }
        public IReadOnlyList<Loc1Entry> Entries { get; }

        public Dictionary<ulong, Loc1Entry> IndexByHash()
        {
            var result = new Dictionary<ulong, Loc1Entry>(Entries.Count);
            foreach (Loc1Entry entry in Entries)
            {
                if (!result.TryAdd(entry.KeyHash, entry))
                {
                    throw new Loc1FormatException($"Duplicate LOC1 key hash {entry.Id}.");
                }
            }

            return result;
        }
    }
}
