using System;
using System.IO;

namespace InvokersRu.Core.Updates
{
    /// <summary>
    /// URL policy for signed release metadata. Download code must call ValidateArtifactResponseUrl for every
    /// redirect target as well as validating the manifest URL before the first request.
    /// </summary>
    public static class SignedUpdateUrlPolicy
    {
        private const string RepositoryReleasePrefix = "/Braintfy/ruslocal-invokers/releases/";
        private const string RepositoryDownloadPrefix = RepositoryReleasePrefix + "download/";
        private const string RepositoryLatestDownloadPrefix = RepositoryReleasePrefix + "latest/download/";

        public static Uri ValidateEnvelopeUrl(string value)
        {
            Uri uri = ParseHttps(value, nameof(value), allowQuery: false);
            if (!HostEquals(uri, "github.com"))
            {
                throw new InvalidDataException("Update envelope URL must use the trusted github.com release origin.");
            }

            RejectEncodedPathSeparators(value);
            string remainder;
            if (uri.AbsolutePath.StartsWith(RepositoryLatestDownloadPrefix, StringComparison.Ordinal))
            {
                remainder = uri.AbsolutePath.Substring(RepositoryLatestDownloadPrefix.Length);
            }
            else if (uri.AbsolutePath.StartsWith(RepositoryDownloadPrefix, StringComparison.Ordinal))
            {
                remainder = uri.AbsolutePath.Substring(RepositoryDownloadPrefix.Length);
                int separator = remainder.IndexOf('/');
                if (separator <= 0 || !IsSafeToken(remainder.Substring(0, separator)))
                {
                    throw new InvalidDataException("Immutable update envelope URL has an unsafe release id.");
                }

                remainder = remainder.Substring(separator + 1);
            }
            else
            {
                throw new InvalidDataException("Update envelope URL must point to this repository's latest or immutable release asset.");
            }

            if (!IsSafeReleaseFileName(remainder))
            {
                throw new InvalidDataException("Update envelope release asset name is empty or unsafe.");
            }

            return uri;
        }

        public static Uri ValidateCatalogUrl(string value, string releaseId)
        {
            if (!IsSafeToken(releaseId))
            {
                throw new InvalidDataException("Release id is not a safe path token.");
            }

            Uri uri = ParseHttps(value, nameof(value), allowQuery: false);
            if (!HostEquals(uri, "github.com"))
            {
                throw new InvalidDataException("Catalog URL must use the trusted github.com release origin.");
            }

            RejectEncodedPathSeparators(value);
            string requiredPrefix = RepositoryDownloadPrefix + releaseId + "/";
            if (!uri.AbsolutePath.StartsWith(requiredPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Catalog URL must point to this repository's matching immutable release id.");
            }

            string fileName = uri.AbsolutePath.Substring(requiredPrefix.Length);
            if (!IsSafeReleaseFileName(fileName))
            {
                throw new InvalidDataException("Catalog release asset name is empty or unsafe.");
            }

            return uri;
        }

        public static Uri ValidatePatcherDownloadPage(string value)
        {
            Uri uri = ParseHttps(value, nameof(value), allowQuery: false);
            if (!HostEquals(uri, "github.com")
                || !uri.AbsolutePath.StartsWith(RepositoryReleasePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Patcher download page must stay under this repository's GitHub releases.");
            }

            RejectEncodedPathSeparators(value);
            return uri;
        }

        public static Uri ValidateArtifactResponseUrl(string value)
        {
            Uri uri = ParseHttps(value, nameof(value), allowQuery: true);
            bool trustedCdn = HostEquals(uri, "release-assets.githubusercontent.com")
                || HostEquals(uri, "objects.githubusercontent.com");
            bool trustedOrigin = HostEquals(uri, "github.com")
                && (uri.AbsolutePath.StartsWith(RepositoryDownloadPrefix, StringComparison.Ordinal)
                    || uri.AbsolutePath.StartsWith(RepositoryLatestDownloadPrefix, StringComparison.Ordinal));
            if (!trustedCdn && !trustedOrigin)
            {
                throw new InvalidDataException("Artifact response or redirect host is not trusted.");
            }

            // GitHub CDN query parameters legitimately contain encoded slashes (MIME
            // types and signatures). Only the path participates in path confinement.
            RejectEncodedPathSeparators(uri.GetLeftPart(UriPartial.Path));
            return uri;
        }

        private static Uri ParseHttps(string? value, string name, bool allowQuery)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > SignedUpdateLimits.MaxUrlCharacters
                || value.IndexOf('\\') >= 0
                || HasAsciiControl(value)
                || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo)
                || string.IsNullOrEmpty(uri.IdnHost)
                || uri.IdnHost.EndsWith(".", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(uri.Fragment)
                || (!allowQuery && !string.IsNullOrEmpty(uri.Query)))
            {
                throw new InvalidDataException($"{name} must be a bounded absolute HTTPS URL without credentials, a custom port, or a fragment.");
            }

            return uri;
        }

        private static bool HostEquals(Uri uri, string expected)
        {
            return string.Equals(uri.IdnHost, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectEncodedPathSeparators(string value)
        {
            if (value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
                || value.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Encoded path separators are not allowed in update URLs.");
            }
        }

        private static bool IsSafeReleaseFileName(string value)
        {
            if (value.Length is < 1 or > 180 || value.Contains('/', StringComparison.Ordinal)) return false;
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character)
                    && character != '-' && character != '_' && character != '.')
                {
                    return false;
                }
            }

            return value != "." && value != "..";
        }

        private static bool IsSafeToken(string? value)
        {
            if (value == null || value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0])) return false;
            foreach (char character in value)
            {
                if (!char.IsAsciiLetterOrDigit(character)
                    && character != '-' && character != '_' && character != '.')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasAsciiControl(string value)
        {
            foreach (char character in value)
            {
                if (character <= 0x1F || character == 0x7F) return true;
            }

            return false;
        }
    }
}
