using System;
using System.Linq;
using System.Security.Cryptography;

namespace InvokersRu.Core.Loc1
{
    public static class Loc1Compatibility
    {
        public static string ComputeOrderedKeysetSha256(Loc1Document document)
        {
            ArgumentNullException.ThrowIfNull(document);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> line = stackalloc byte[17];
            line[16] = (byte)'\n';

            ulong previous = 0;
            for (int index = 0; index < document.Entries.Count; index++)
            {
                Loc1Entry entry = document.Entries[index]
                    ?? throw new Loc1FormatException("LOC1 document contains a null entry.");
                if (entry.Index != index || (index > 0 && entry.KeyHash <= previous))
                {
                    throw new Loc1FormatException("LOC1 key order is not canonical and strictly increasing.");
                }

                previous = entry.KeyHash;
                ulong value = entry.KeyHash;
                for (int digit = 15; digit >= 0; digit--)
                {
                    int nibble = (int)(value & 0xFUL);
                    line[digit] = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
                    value >>= 4;
                }

                hash.AppendData(line);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public static void RequireComposableCorpus(
            Loc1Document english,
            Loc1Document baseLocale,
            bool allowPerLocaleContentVersion = false)
        {
            bool sameGuid = string.Equals(english.ContentGuid, baseLocale.ContentGuid, StringComparison.Ordinal);
            bool sameSchema = english.FormatVersion == baseLocale.FormatVersion;
            bool sameKeys = english.Entries.Count == baseLocale.Entries.Count
                && english.Entries.Select(entry => entry.KeyHash)
                    .SequenceEqual(baseLocale.Entries.Select(entry => entry.KeyHash));
            bool sameVersion = string.Equals(english.ContentVersion, baseLocale.ContentVersion, StringComparison.Ordinal);

            if (!sameGuid || !sameSchema || !sameKeys || (!allowPerLocaleContentVersion && !sameVersion))
            {
                string versionRule = allowPerLocaleContentVersion
                    ? "ContentVersion may differ only when GUID, schema, and the complete ordered key set match"
                    : "ContentVersion must also match";
                throw new InvalidOperationException($"English and base locale packages are not composable. {versionRule}.");
            }
        }
    }
}
