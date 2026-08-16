using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Patching
{
    public sealed class CompatibilityManifest
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; }

        [JsonPropertyName("builds")]
        public List<CompatibleBuild> Builds { get; set; } = new List<CompatibleBuild>();

        public static CompatibilityManifest Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static CompatibilityManifest Parse(string json)
        {
            var options = new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false
            };
            CompatibilityManifest? result = JsonSerializer.Deserialize<CompatibilityManifest>(json, options);
            if (result == null || result.Schema != 1 || result.Builds == null || result.Builds.Count == 0)
            {
                throw new InvalidDataException("Compatibility manifest must use schema 1 and contain at least one build.");
            }

            if (result.Builds.GroupBy(build => build.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("Compatibility manifest contains duplicate build ids.");
            }

            foreach (CompatibleBuild build in result.Builds)
            {
                build.Validate();
            }

            return result;
        }
    }

    public sealed class CompatibleBuild
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("game_version")]
        public string GameVersion { get; set; } = string.Empty;

        [JsonPropertyName("content_guid")]
        public string ContentGuid { get; set; } = string.Empty;

        [JsonPropertyName("content_version")]
        public string ContentVersion { get; set; } = string.Empty;

        [JsonPropertyName("readiness")]
        public string Readiness { get; set; } = "blocked";

        [JsonPropertyName("certified")]
        public bool Certified { get; set; }

        [JsonPropertyName("blocked_reason")]
        public string? BlockedReason { get; set; }

        [JsonPropertyName("english_relative_path")]
        public string EnglishRelativePath { get; set; } = string.Empty;

        [JsonPropertyName("english_sha256")]
        public string EnglishSha256 { get; set; } = string.Empty;

        [JsonPropertyName("english_raw_sha256")]
        public string EnglishRawSha256 { get; set; } = string.Empty;

        [JsonPropertyName("base_relative_path")]
        public string BaseRelativePath { get; set; } = string.Empty;

        [JsonPropertyName("base_sha256")]
        public string BaseSha256 { get; set; } = string.Empty;

        [JsonPropertyName("base_raw_sha256")]
        public string BaseRawSha256 { get; set; } = string.Empty;

        [JsonPropertyName("base_locale_id")]
        public uint BaseLocaleId { get; set; }

        [JsonPropertyName("base_locale_revision")]
        public uint BaseLocaleRevision { get; set; }

        [JsonPropertyName("entry_count")]
        public int EntryCount { get; set; }

        [JsonPropertyName("translation_catalog_sha256")]
        public string? TranslationCatalogSha256 { get; set; }

        [JsonPropertyName("minimum_applied_translations")]
        public int MinimumAppliedTranslations { get; set; } = 1;

        [JsonPropertyName("patch_mode")]
        public string PatchMode { get; set; } = "release";

        [JsonPropertyName("exclude_needs_review")]
        public bool ExcludeNeedsReview { get; set; }

        [JsonPropertyName("expected_output_sha256")]
        public string? ExpectedOutputSha256 { get; set; }

        [JsonPropertyName("expected_output_raw_sha256")]
        public string? ExpectedOutputRawSha256 { get; set; }

        [JsonPropertyName("game_files")]
        public List<KnownFile> GameFiles { get; set; } = new List<KnownFile>();

        [JsonPropertyName("manifest_relative_path")]
        public string ManifestRelativePath { get; set; } = string.Empty;

        [JsonPropertyName("manifest_sha256")]
        public string ManifestSha256 { get; set; } = string.Empty;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ContentGuid) || string.IsNullOrWhiteSpace(ContentVersion))
            {
                throw new InvalidDataException("Compatibility build identity fields cannot be empty.");
            }

            if (!IsSafeBuildId(Id))
            {
                throw new InvalidDataException("Compatibility build id must be a 1-128 character ASCII path-safe token beginning with a letter or digit.");
            }

            ValidateRelativePath(EnglishRelativePath, nameof(EnglishRelativePath));
            ValidateRelativePath(BaseRelativePath, nameof(BaseRelativePath));
            ValidateRelativePath(ManifestRelativePath, nameof(ManifestRelativePath));
            ValidateHash(EnglishSha256, nameof(EnglishSha256));
            ValidateHash(EnglishRawSha256, nameof(EnglishRawSha256));
            ValidateHash(BaseSha256, nameof(BaseSha256));
            ValidateHash(BaseRawSha256, nameof(BaseRawSha256));
            ValidateHash(ManifestSha256, nameof(ManifestSha256));
            if (Readiness != "blocked" && Readiness != "ready")
            {
                throw new InvalidDataException("Readiness must be blocked or ready.");
            }

            if (Certified && Readiness != "ready")
            {
                throw new InvalidDataException("A certified build must also be ready.");
            }

            if (EntryCount <= 0)
            {
                throw new InvalidDataException("EntryCount must be positive.");
            }

            if (MinimumAppliedTranslations <= 0 || MinimumAppliedTranslations > EntryCount)
            {
                throw new InvalidDataException("MinimumAppliedTranslations must be within the LOC1 entry count.");
            }

            if (PatchMode != "release" && PatchMode != "supervised_preview")
            {
                throw new InvalidDataException("PatchMode must be release or supervised_preview.");
            }

            if (PatchMode == "release" && ExcludeNeedsReview)
            {
                throw new InvalidDataException("ExcludeNeedsReview is only valid for a supervised_preview compatibility entry.");
            }

            if (Certified)
            {
                if (string.IsNullOrWhiteSpace(TranslationCatalogSha256)
                    || string.IsNullOrWhiteSpace(ExpectedOutputSha256)
                    || string.IsNullOrWhiteSpace(ExpectedOutputRawSha256))
                {
                    throw new InvalidDataException("A certified build must pin translation catalog and exact output SHA-256 digests.");
                }

                ValidateHash(TranslationCatalogSha256, nameof(TranslationCatalogSha256));
                ValidateHash(ExpectedOutputSha256, nameof(ExpectedOutputSha256));
                ValidateHash(ExpectedOutputRawSha256, nameof(ExpectedOutputRawSha256));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(TranslationCatalogSha256)) ValidateHash(TranslationCatalogSha256, nameof(TranslationCatalogSha256));
                if (!string.IsNullOrWhiteSpace(ExpectedOutputSha256)) ValidateHash(ExpectedOutputSha256, nameof(ExpectedOutputSha256));
                if (!string.IsNullOrWhiteSpace(ExpectedOutputRawSha256)) ValidateHash(ExpectedOutputRawSha256, nameof(ExpectedOutputRawSha256));
            }

            if (GameFiles == null || GameFiles.Count == 0)
            {
                throw new InvalidDataException("At least one game executable file must be pinned.");
            }

            if (GameFiles.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("Pinned game file paths must be unique.");
            }

            foreach (KnownFile file in GameFiles)
            {
                ValidateRelativePath(file.RelativePath, nameof(file.RelativePath));
                ValidateHash(file.Sha256, nameof(file.Sha256));
                if (file.Size <= 0) throw new InvalidDataException("Known game file size must be positive.");
            }
        }

        private static void ValidateRelativePath(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{name} must be a safe relative path.");
            }
        }

        private static void ValidateHash(string? value, string name)
        {
            if (value == null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"{name} must be a SHA-256 hexadecimal digest.");
            }
        }

        private static bool IsSafeBuildId(string value)
        {
            if (value.Length == 0 || value.Length > 128 || !IsAsciiLetterOrDigit(value[0]) || value[^1] == '.')
            {
                return false;
            }

            return value.All(character => IsAsciiLetterOrDigit(character) || character == '-' || character == '_' || character == '.');
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
        }
    }

    public sealed class KnownFile
    {
        [JsonPropertyName("relative_path")]
        public string RelativePath { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
