using System;
using System.Globalization;

namespace InvokersRu.Core.Loc1
{
    /// <summary>Canonical LOC1 family identifiers used by legacy and 0.61 clients.</summary>
    public static class Loc1ContentFamily
    {
        public static bool IsCanonical(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 36) return false;
            if (Guid.TryParseExact(value, "D", out Guid guid))
                return string.Equals(guid.ToString("D"), value, StringComparison.Ordinal);

            // New clients write a three-component version into the former GUID field.
            // Preserve exact identity: no trimming, normalization, prefixes or wildcards.
            return Version.TryParse(value, out Version? version)
                && version.Build >= 0 && version.Revision == -1
                && string.Equals(version.ToString(3), value, StringComparison.Ordinal);
        }

        public static bool TryParseSourceStamp(string? value, out string clientVersion, out long compressedBytes)
        {
            clientVersion = string.Empty;
            compressedBytes = 0;
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            int separator = value.IndexOf(':');
            if (separator <= 0 || separator != value.LastIndexOf(':')) return false;
            string prefix = value.Substring(0, separator);
            string size = value.Substring(separator + 1);
            if (!Version.TryParse(prefix, out Version? version)
                || version.Build < 0 || version.Revision != -1
                || !string.Equals(version.ToString(3), prefix, StringComparison.Ordinal)
                || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                || length is < 1 or > 268435456
                || !string.Equals(length.ToString(CultureInfo.InvariantCulture), size, StringComparison.Ordinal))
                return false;
            clientVersion = prefix;
            compressedBytes = length;
            return true;
        }
    }
}
