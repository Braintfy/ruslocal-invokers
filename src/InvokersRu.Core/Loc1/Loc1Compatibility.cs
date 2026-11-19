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
            bool allowPerLocaleContentVersion = false,
            bool allowAdditionalBaseKeys = false)
        {
            bool sameGuid = string.Equals(english.ContentGuid, baseLocale.ContentGuid, StringComparison.Ordinal);
            bool sameSchema = english.FormatVersion == baseLocale.FormatVersion;
            bool sameKeys = english.Entries.Count == baseLocale.Entries.Count
                && english.Entries.Select(entry => entry.KeyHash)
                    .SequenceEqual(baseLocale.Entries.Select(entry => entry.KeyHash));
            bool compatibleKeys = sameKeys || (allowAdditionalBaseKeys && IsSourceKeySubset(english, baseLocale));
            bool sameVersion = string.Equals(english.ContentVersion, baseLocale.ContentVersion, StringComparison.Ordinal);

            if (!sameGuid || !sameSchema || !compatibleKeys || (!allowPerLocaleContentVersion && !sameVersion))
            {
                string versionRule = allowPerLocaleContentVersion
                    ? allowAdditionalBaseKeys
                        ? "ContentVersion may differ only when GUID/schema match and every English key exists in the base locale"
                        : "ContentVersion may differ only when GUID, schema, and the complete ordered key set match"
                    : "ContentVersion must also match";
                throw new InvalidOperationException($"English and base locale packages are not composable. {versionRule}.");
            }
        }

        // Both documents have canonical sorted unique keys from the LOC1 parser. A target
        // superset is safe only in explicitly opted-in paths: no source key may disappear,
        // and target-only entries are retained by the composer rather than synthesized.
        private static bool IsSourceKeySubset(Loc1Document english, Loc1Document baseLocale)
        {
            if (english.Entries.Count < 1 || english.Entries.Count > baseLocale.Entries.Count) return false;
            int targetIndex = 0;
            foreach (Loc1Entry source in english.Entries)
            {
                while (targetIndex < baseLocale.Entries.Count
                    && baseLocale.Entries[targetIndex].KeyHash < source.KeyHash) targetIndex++;
                if (targetIndex == baseLocale.Entries.Count
                    || baseLocale.Entries[targetIndex].KeyHash != source.KeyHash) return false;
                targetIndex++;
            }
            return true;
        }
    }
}
