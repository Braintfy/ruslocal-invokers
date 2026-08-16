using System;
using System.Linq;

namespace InvokersRu.Core.Loc1
{
    public static class Loc1Compatibility
    {
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
