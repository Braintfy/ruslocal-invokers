using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class CompatibilityProfileBuilder
    {
        private const int MaximumLoc1ContainerBytes = 256 * 1024 * 1024;

        public static SignedUpdateCompatibilityProfile Build(BuildCompatibilityRequest request)
        {
            ValidateDistinctOutput(request);

            byte[] profileBytes = StrictIo.ReadRegularFile(request.RuntimeProfilePath, "Runtime-cache compatibility profile", 1024 * 1024);
            RuntimeCacheCompatibility sourceProfile = RuntimeCacheCompatibility.Parse(
                StrictIo.DecodeStrictUtf8(profileBytes, "Runtime-cache compatibility profile"));
            byte[] englishContainer = StrictIo.ReadRegularFile(request.EnglishLoc1Path, "English LOC1", MaximumLoc1ContainerBytes);
            byte[] baseContainer = StrictIo.ReadRegularFile(request.BaseLoc1Path, "Base/target LOC1", MaximumLoc1ContainerBytes);
            byte[] stampBytes = StrictIo.ReadRegularFile(request.StampPath, "Game version stamp", 256);
            byte[] catalogBytes = StrictIo.ReadRegularFile(
                request.CatalogPath,
                "Translation catalog",
                checked((int)SignedUpdateLimits.MaxUncompressedCatalogBytes));
            byte[] builtContainer = StrictIo.ReadRegularFile(request.BuiltLoc1Path, "CLI-built LOC1", MaximumLoc1ContainerBytes);
            byte[] reportBytes = StrictIo.ReadRegularFile(request.BuildReportPath, "CLI build report", 1024 * 1024);

            StrictIo.DecodeStrictUtf8(catalogBytes, "Translation catalog");
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(catalogBytes);
            if (catalog.Count is < 1 or > SignedUpdateLimits.MaxCatalogRecords)
            {
                throw new InvalidDataException("Translation catalog record count is outside the signed-update limits.");
            }

            Loc1Snapshot english = ReadLoc1Snapshot(englishContainer, "English LOC1");
            Loc1Snapshot baseLocale = ReadLoc1Snapshot(baseContainer, "Base/target LOC1");
            Loc1Snapshot built = ReadLoc1Snapshot(builtContainer, "CLI-built LOC1");
            Loc1Compatibility.RequireComposableCorpus(english.Document, baseLocale.Document, allowPerLocaleContentVersion: true);

            string stampValue = StrictIo.DecodeStrictUtf8(stampBytes, "Game version stamp");
            if (stampValue.Length is < 1 or > 64 || stampValue.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                throw new InvalidDataException("Game version stamp must be one bounded version token without whitespace.");
            }

            ValidateRuntimeProfile(sourceProfile, english, baseLocale, stampBytes, stampValue);
            ValidateBuiltIdentity(baseLocale.Document, built.Document);

            CliBuildReport report = StrictIo.DeserializeStrict<CliBuildReport>(reportBytes, "CLI build report");
            string translationPolicy = ValidateReport(
                report,
                sourceProfile,
                english,
                baseLocale,
                built,
                catalog,
                catalogBytes,
                catalog.Count);

            string orderedKeysetSha256 = SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(baseLocale.Document);
            var result = new SignedUpdateCompatibilityProfile
            {
                ProfileId = sourceProfile.Id,
                Mode = "exact",
                GameVersion = sourceProfile.GameVersion,
                StampSha256 = StrictIo.Sha256(stampBytes),
                StampValue = stampValue,
                ContentGuid = CanonicalGuid(sourceProfile.ContentGuid),
                Loc1Schema = checked((int)baseLocale.Document.FormatVersion),
                OrderedKeysetSha256 = orderedKeysetSha256,
                English = new SignedUpdateCorpusIdentity
                {
                    Sha256 = StrictIo.Sha256(english.Container),
                    ContentVersion = english.Document.ContentVersion,
                    LocaleId = english.Document.LocaleId,
                    LocaleRevisionHex = english.Document.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                    ReleaseRevision = english.Document.ReleaseRevision,
                    EntryCount = english.Document.Entries.Count
                },
                Base = new SignedUpdateCorpusIdentity
                {
                    Sha256 = StrictIo.Sha256(baseLocale.Container),
                    ContentVersion = baseLocale.Document.ContentVersion,
                    LocaleId = baseLocale.Document.LocaleId,
                    LocaleRevisionHex = baseLocale.Document.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                    ReleaseRevision = baseLocale.Document.ReleaseRevision,
                    EntryCount = baseLocale.Document.Entries.Count
                },
                Composition = new SignedUpdateComposition
                {
                    AppliedRu = report.Composition.AppliedRu,
                    EnglishFallback = report.Composition.EnglishFallback,
                    BaseFallback = report.Composition.BaseFallback,
                    MissingCatalog = report.Composition.MissingCatalog,
                    StaleCatalog = report.Composition.StaleCatalog,
                    RejectedCatalog = report.Composition.RejectedCatalog,
                    NeedsReviewFallback = report.Composition.NeedsReviewFallback,
                    PolicyFallback = report.Composition.PolicyFallback,
                    ValidationErrors = report.Validation.Errors,
                    ValidationWarnings = report.Validation.Warnings,
                    OutputRawSha256 = StrictIo.Sha256(built.Raw)
                }
            };

            VerifyWithProductionBoundary(result, baseLocale.Document, catalogBytes, catalog.Count, translationPolicy);
            VerifyOptionalRuntimePins(sourceProfile, result, StrictIo.Sha256(catalogBytes), translationPolicy);

            byte[] output = JsonSerializer.SerializeToUtf8Bytes(new[] { result }, StrictIo.DisplayJson);
            byte[] terminated = new byte[output.Length + 1];
            Buffer.BlockCopy(output, 0, terminated, 0, output.Length);
            terminated[^1] = (byte)'\n';
            StrictIo.WriteNewFileAtomically(request.OutputPath, terminated, "Compatibility profile array output");
            return result;
        }

        private static void ValidateDistinctOutput(BuildCompatibilityRequest request)
        {
            string output = StrictIo.FullPath(request.OutputPath, "Compatibility profile array output");
            string[] inputs =
            {
                request.RuntimeProfilePath,
                request.EnglishLoc1Path,
                request.BaseLoc1Path,
                request.StampPath,
                request.CatalogPath,
                request.BuiltLoc1Path,
                request.BuildReportPath
            };
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (inputs.Any(input => string.Equals(StrictIo.FullPath(input, "Compatibility input"), output, comparison)))
            {
                throw new InvalidOperationException("Compatibility output must be a new path distinct from every input.");
            }
        }

        private static Loc1Snapshot ReadLoc1Snapshot(byte[] container, string label)
        {
            bool rawContainer = container.Length >= 4
                && container[0] == (byte)'L' && container[1] == (byte)'O'
                && container[2] == (byte)'C' && container[3] == (byte)'1';
            byte[] raw;
            if (rawContainer)
            {
                raw = (byte[])container.Clone();
            }
            else
            {
                using var input = new MemoryStream(container, writable: false);
                using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);
                using var output = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = brotli.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    if (output.Length + read > MaximumLoc1ContainerBytes)
                    {
                        throw new InvalidDataException($"{label} expands beyond the fixed LOC1 size limit.");
                    }

                    output.Write(buffer, 0, read);
                }

                if (input.Position != input.Length)
                {
                    throw new InvalidDataException($"{label} has trailing bytes after its Brotli stream.");
                }

                raw = output.ToArray();
            }

            return new Loc1Snapshot(container, raw, Loc1Codec.Parse(raw), rawContainer ? "raw" : "brotli");
        }

        private static void ValidateRuntimeProfile(
            RuntimeCacheCompatibility profile,
            Loc1Snapshot english,
            Loc1Snapshot baseLocale,
            byte[] stampBytes,
            string stampValue)
        {
            Loc1Document en = english.Document;
            Loc1Document target = baseLocale.Document;
            if (!string.Equals(profile.GameVersion, stampValue, StringComparison.Ordinal)
                || !string.Equals(profile.StampValue, stampValue, StringComparison.Ordinal)
                || !Hashing.FixedEqualsHex(profile.StampSha256, StrictIo.Sha256(stampBytes))
                || !string.Equals(profile.ContentGuid, en.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(profile.ContentGuid, target.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(profile.EnglishContentVersion, en.ContentVersion, StringComparison.Ordinal)
                || !string.Equals(profile.BaseContentVersion, target.ContentVersion, StringComparison.Ordinal)
                || !Hashing.FixedEqualsHex(profile.EnglishSha256, StrictIo.Sha256(english.Container))
                || !Hashing.FixedEqualsHex(profile.BaseSha256, StrictIo.Sha256(baseLocale.Container))
                || profile.EnglishLocaleId != en.LocaleId
                || profile.EnglishLocaleRevision != en.LocaleRevision
                || profile.EnglishReleaseRevision != en.ReleaseRevision
                || profile.BaseLocaleId != target.LocaleId
                || profile.BaseLocaleRevision != target.LocaleRevision
                || profile.BaseReleaseRevision != target.ReleaseRevision
                || profile.EntryCount != en.Entries.Count
                || profile.EntryCount != target.Entries.Count)
            {
                throw new InvalidDataException("Runtime-cache profile does not exactly match the supplied EN/base/stamp tuple.");
            }

            _ = CanonicalGuid(profile.ContentGuid);
        }

        private static void ValidateBuiltIdentity(Loc1Document source, Loc1Document built)
        {
            if (source.FormatVersion != built.FormatVersion
                || source.LocaleId != built.LocaleId
                || source.LocaleRevision != built.LocaleRevision
                || source.ReleaseRevision != built.ReleaseRevision
                || source.Entries.Count != built.Entries.Count
                || !string.Equals(source.ContentGuid, built.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(source.ContentVersion, built.ContentVersion, StringComparison.Ordinal)
                || !string.Equals(
                    SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(source),
                    SignedUpdateRuntimeProfileAdapter.ComputeOrderedKeysetSha256(built),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("CLI-built LOC1 does not preserve the exact base/target identity and ordered keys.");
            }
        }

        private static string ValidateReport(
            CliBuildReport report,
            RuntimeCacheCompatibility profile,
            Loc1Snapshot english,
            Loc1Snapshot baseLocale,
            Loc1Snapshot built,
            TranslationCatalog catalog,
            byte[] catalogBytes,
            int catalogCount)
        {
            bool release = string.Equals(report.Kind, "invokers-ru-release-build", StringComparison.Ordinal);
            bool preview = string.Equals(report.Kind, "invokers-ru-preview-build", StringComparison.Ordinal);
            if (report.Schema != 1 || (!release && !preview))
            {
                throw new InvalidDataException("CLI build report identity is unsupported.");
            }

            string expectedValidation = release ? "release" : "preview";
            string translationPolicy = release ? "release-approved-v1" : "validated-preview-v1";
            if (release && catalog.Records.Any(record => !string.Equals(record.Status, "approved", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Release compatibility requires the same all-approved catalog enforced by release-approved-v1.");
            }

            if (report.Validation.Errors != 0
                || report.Validation.Warnings is < 0 or > SignedUpdateLimits.MaxValidationWarnings
                || !string.Equals(report.Validation.Profile, expectedValidation, StringComparison.Ordinal)
                || report.BuildOptions.Release != release
                || report.BuildOptions.ExcludeNeedsReview
                || report.BuildOptions.IncludeDraft != preview
                || (report.BuildOptions.Container != "raw" && report.BuildOptions.Container != "brotli")
                || !string.Equals(report.BuildOptions.Container, built.ContainerKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CLI build report policy/options are not reproducible by the signed runtime policy, or validation has errors.");
            }

            CliBuildReportSource source = report.Source;
            Loc1Document en = english.Document;
            Loc1Document target = baseLocale.Document;
            if (!string.Equals(source.EnglishContentGuid, en.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(source.BaseContentGuid, target.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(source.EnglishContentVersion, en.ContentVersion, StringComparison.Ordinal)
                || !string.Equals(source.BaseContentVersion, target.ContentVersion, StringComparison.Ordinal)
                || source.EnglishSchema != en.FormatVersion
                || source.BaseSchema != target.FormatVersion
                || !Hashing.FixedEqualsHex(source.EnglishContainerSha256, StrictIo.Sha256(english.Container))
                || !Hashing.FixedEqualsHex(source.EnglishRawSha256, StrictIo.Sha256(english.Raw))
                || !Hashing.FixedEqualsHex(source.BaseContainerSha256, StrictIo.Sha256(baseLocale.Container))
                || !Hashing.FixedEqualsHex(source.BaseRawSha256, StrictIo.Sha256(baseLocale.Raw))
                || !Hashing.FixedEqualsHex(source.TranslationsSha256, StrictIo.Sha256(catalogBytes)))
            {
                throw new InvalidDataException("CLI build report source tuple or catalog hash does not match the supplied artifacts.");
            }

            if (report.Target.LocaleId != target.LocaleId
                || !string.Equals(report.Target.LocaleRevision, target.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || report.Target.ReleaseRevision != target.ReleaseRevision
                || report.Target.Entries != target.Entries.Count
                || report.Target.Entries != profile.EntryCount)
            {
                throw new InvalidDataException("CLI build report target identity/revisions do not match the exact runtime tuple.");
            }

            if (report.Output.Bytes != built.Container.LongLength
                || report.Output.RawBytes != built.Raw.LongLength
                || !Hashing.FixedEqualsHex(report.Output.ContainerSha256, StrictIo.Sha256(built.Container))
                || !Hashing.FixedEqualsHex(report.Output.RawSha256, StrictIo.Sha256(built.Raw)))
            {
                throw new InvalidDataException("CLI build report output pins do not match the supplied built LOC1.");
            }

            ValidateComposition(report.Composition, report.Target.Entries, catalogCount);
            Loc1Document recomposedTarget = Loc1Codec.Parse((byte[])baseLocale.Raw.Clone());
            ValidationReport reproducedValidation = TranslationValidator.Validate(
                english.Document,
                catalog,
                includeDraft: preview,
                recomposedTarget,
                release ? ValidationProfile.Release : ValidationProfile.Preview,
                report.BuildOptions.PerLocaleContentVersion);
            CompositionSummary reproduced = TranslationComposer.Apply(
                english.Document,
                recomposedTarget,
                catalog,
                includeDraft: preview,
                approvedOnly: release,
                excludeNeedsReview: false,
                allowPerLocaleContentVersion: report.BuildOptions.PerLocaleContentVersion,
                eligibility: null);
            byte[] reproducedRaw = Loc1Codec.BuildRaw(recomposedTarget);
            if (reproducedValidation.ErrorCount != report.Validation.Errors
                || reproducedValidation.WarningCount != report.Validation.Warnings
                || reproduced.AppliedTranslations != report.Composition.AppliedRu
                || reproduced.EnglishFallbacks != report.Composition.EnglishFallback
                || reproduced.BaseFallbacks != report.Composition.BaseFallback
                || reproduced.MissingCatalogRecords != report.Composition.MissingCatalog
                || reproduced.StaleCatalogRecords != report.Composition.StaleCatalog
                || reproduced.RejectedCatalogRecords != report.Composition.RejectedCatalog
                || reproduced.NeedsReviewFallbacks != report.Composition.NeedsReviewFallback
                || reproduced.PolicyFallbacks != report.Composition.PolicyFallback
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(reproducedRaw), SHA256.HashData(built.Raw))
                || reproducedRaw.LongLength != built.Raw.LongLength)
            {
                throw new InvalidDataException("CLI composition/validation report or built LOC1 cannot be reproduced from the exact EN/base/catalog inputs.");
            }

            return translationPolicy;
        }

        private static void ValidateComposition(CliBuildReportComposition value, int entryCount, int catalogCount)
        {
            int[] counts =
            {
                value.AppliedRu,
                value.EnglishFallback,
                value.BaseFallback,
                value.MissingCatalog,
                value.StaleCatalog,
                value.RejectedCatalog,
                value.NeedsReviewFallback,
                value.PolicyFallback
            };
            if (counts.Any(count => count < 0 || count > entryCount)
                || value.AppliedRu < 1
                || value.AppliedRu > catalogCount
                || (long)value.AppliedRu + value.EnglishFallback + value.BaseFallback != entryCount
                || (long)value.MissingCatalog + value.StaleCatalog + value.RejectedCatalog
                    + value.NeedsReviewFallback + value.PolicyFallback != value.EnglishFallback
                || (long)value.AppliedRu + value.StaleCatalog + value.RejectedCatalog
                    + value.NeedsReviewFallback + value.PolicyFallback > catalogCount)
            {
                throw new InvalidDataException("CLI build report composition does not exactly partition the target and catalog.");
            }
        }

        private static void VerifyOptionalRuntimePins(
            RuntimeCacheCompatibility source,
            SignedUpdateCompatibilityProfile result,
            string catalogSha256,
            string signedTranslationPolicy)
        {
            string runtimeTranslationPolicy = signedTranslationPolicy == "release-approved-v1"
                ? "release-approved"
                : "community-preview-all-drafts";
            if ((!string.IsNullOrWhiteSpace(source.TranslationCatalogSha256)
                    && !Hashing.FixedEqualsHex(source.TranslationCatalogSha256, catalogSha256))
                || (!string.IsNullOrWhiteSpace(source.ExpectedOutputSha256)
                    && !Hashing.FixedEqualsHex(source.ExpectedOutputSha256, result.Composition.OutputRawSha256))
                || (source.ExpectedAppliedTranslations > 0
                    && source.ExpectedAppliedTranslations != result.Composition.AppliedRu)
                || (source.ExpectedEnglishFallbacks >= 0
                    && source.ExpectedEnglishFallbacks != result.Composition.EnglishFallback)
                || (source.ExpectedBaseFallbacks >= 0
                    && source.ExpectedBaseFallbacks != result.Composition.BaseFallback)
                || (source.ExpectedNeedsReviewFallbacks >= 0
                    && source.ExpectedNeedsReviewFallbacks != result.Composition.NeedsReviewFallback)
                || (source.Certified
                    && !string.Equals(source.TranslationPolicy, runtimeTranslationPolicy, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Existing certified pins in the runtime-cache profile contradict the verified build artifacts.");
            }
        }

        private static void VerifyWithProductionBoundary(
            SignedUpdateCompatibilityProfile profile,
            Loc1Document target,
            byte[] catalogBytes,
            int catalogCount,
            string translationPolicy)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            now = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
            const string releaseId = "compatibility-profile-verification";
            string catalogSha256 = StrictIo.Sha256(catalogBytes);
            var manifest = new SignedUpdateManifest
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.ManifestKind,
                Channel = "stable",
                Sequence = 1,
                ReleaseId = releaseId,
                IssuedUtc = StrictIo.CanonicalUtcSecond(now),
                ExpiresUtc = StrictIo.CanonicalUtcSecond(now.AddHours(1)),
                Patcher = new SignedUpdatePatcher
                {
                    MinimumVersion = "3.1.0",
                    LatestVersion = "3.1.0",
                    DownloadPage = "https://github.com/Braintfy/ruslocal-invokers/releases/latest"
                },
                Catalog = new SignedUpdateCatalog
                {
                    ArtifactId = "compatibility-profile-verification",
                    Url = "https://github.com/Braintfy/ruslocal-invokers/releases/download/"
                        + releaseId + "/ru_RU.jsonl.br",
                    Compression = "none",
                    CompressedBytes = catalogBytes.LongLength,
                    CompressedSha256 = catalogSha256,
                    UncompressedBytes = catalogBytes.LongLength,
                    UncompressedSha256 = catalogSha256,
                    RecordCount = catalogCount,
                    Format = "invokers-ru-jsonl-v1",
                    TranslationPolicy = translationPolicy
                },
                Compatibility = new[] { profile },
                RevokedReleaseIds = Array.Empty<string>(),
                NotesRu = string.Empty
            };

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, StrictIo.CompactJson);
            byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            byte[] publicKey = key.ExportSubjectPublicKeyInfo();
            string keyId = SigningKeyService.DeriveKeyId(publicKey);
            var envelope = new SignedUpdateEnvelope
            {
                Schema = 1,
                Kind = SignedUpdateVerifier.EnvelopeKind,
                KeyId = keyId,
                Algorithm = SignedUpdateVerifier.SignatureAlgorithm,
                PayloadBase64 = Convert.ToBase64String(payload),
                SignatureBase64 = Convert.ToBase64String(signature)
            };
            byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, StrictIo.CompactJson);
            VerifiedSignedUpdate verified = SignedUpdateVerifier.Verify(
                envelopeBytes,
                publicKey,
                keyId,
                new SignedUpdateVerificationContext(now, "3.1.0"));
            RuntimeCacheCompatibility adapted = SignedUpdateRuntimeProfileAdapter.AdaptExact(
                verified.Manifest,
                verified.Manifest.Compatibility[0],
                target);
            if (!Hashing.FixedEqualsHex(adapted.TranslationCatalogSha256, catalogSha256)
                || !Hashing.FixedEqualsHex(adapted.ExpectedOutputSha256, profile.Composition.OutputRawSha256))
            {
                throw new InvalidDataException("Production runtime adapter did not reproduce the generated exact pins.");
            }
        }

        private static string CanonicalGuid(string value)
        {
            if (!Guid.TryParseExact(value, "D", out Guid parsed)
                || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Runtime-cache content_guid is not canonical lowercase GUID text.");
            }

            return value;
        }

        private sealed record Loc1Snapshot(byte[] Container, byte[] Raw, Loc1Document Document, string ContainerKind);
    }
}
