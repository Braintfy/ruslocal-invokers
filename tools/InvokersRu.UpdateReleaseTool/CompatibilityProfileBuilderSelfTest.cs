using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using InvokersRu.Core.Updates;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace InvokersRu.UpdateReleaseTool
{
    internal static class CompatibilityProfileBuilderSelfTest
    {
        private const string ContentGuid = "00000000-0000-0000-0000-000000000001";
        private const string ContentVersion = "SelfTest_1";

        public static void Run()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "InvokersRu.CompatibilityProfileBuilder-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            try
            {
                string englishPath = Path.Combine(root, "dl_en_US.bin");
                string basePath = Path.Combine(root, "dl_uk_UA.bin");
                string stampPath = Path.Combine(root, "dl_uk_UA.bin.ver");
                string catalogPath = Path.Combine(root, "ru_RU.jsonl");
                string builtPath = Path.Combine(root, "dl_uk_UA.ru.bin");
                string reportPath = Path.Combine(root, "build-report.json");
                string profilePath = Path.Combine(root, "runtime-profile.json");
                string outputPath = Path.Combine(root, "compatibility.json");

                byte[] englishRaw = CreateLoc1Raw(1, 1, 0x01020304, new[] { "Hello", "World" });
                byte[] baseRaw = CreateLoc1Raw(8, 1, 0x05060708, new[] { "Привіт", "Світ" });
                StrictIo.WriteNewFile(englishPath, englishRaw, "Self-test English LOC1");
                StrictIo.WriteNewFile(basePath, baseRaw, "Self-test base LOC1");
                StrictIo.WriteNewFile(stampPath, StrictIo.Utf8.GetBytes("0.0.1"), "Self-test stamp");

                string[] englishValues = { "Hello", "World" };
                string[] russianValues = { "Привет", "Мир" };
                var lines = new List<string>();
                for (int index = 0; index < englishValues.Length; index++)
                {
                    var record = new TranslationRecord
                    {
                        Id = checked((ulong)index + 1).ToString("X16", CultureInfo.InvariantCulture),
                        SourceSha256 = Hashing.Sha256Text(englishValues[index]),
                        Translation = russianValues[index],
                        Status = "draft",
                        Confidence = "high",
                        IssueCodes = Array.Empty<string>(),
                        RiskFlags = Array.Empty<string>(),
                        ReviewerIds = Array.Empty<string>()
                    };
                    lines.Add(JsonSerializer.Serialize(record, StrictIo.CompactJson));
                }

                byte[] catalogBytes = StrictIo.Utf8.GetBytes(string.Join("\n", lines) + "\n");
                StrictIo.WriteNewFile(catalogPath, catalogBytes, "Self-test catalog");

                TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(catalogBytes);
                Loc1Document english = Loc1Codec.Parse(englishRaw);
                Loc1Document composed = Loc1Codec.Parse((byte[])baseRaw.Clone());
                ValidationReport validation = TranslationValidator.Validate(
                    english,
                    catalog,
                    includeDraft: true,
                    composed,
                    ValidationProfile.Preview,
                    allowPerLocaleContentVersion: true);
                if (validation.ErrorCount != 0)
                {
                    throw new InvalidOperationException("Compatibility self-test fixture unexpectedly has validation errors.");
                }

                CompositionSummary composition = TranslationComposer.Apply(
                    english,
                    composed,
                    catalog,
                    includeDraft: true,
                    approvedOnly: false,
                    excludeNeedsReview: false,
                    allowPerLocaleContentVersion: true,
                    eligibility: null);
                byte[] builtRaw = Loc1Codec.BuildRaw(composed);
                StrictIo.WriteNewFile(builtPath, builtRaw, "Self-test built LOC1");

                RuntimeCacheCompatibility runtimeProfile = RuntimeCacheService.DescribeTuple(
                    englishPath,
                    basePath,
                    stampPath,
                    "runtime-cache-self-test-generated");
                StrictIo.WriteNewFile(
                    profilePath,
                    JsonSerializer.SerializeToUtf8Bytes(runtimeProfile, StrictIo.DisplayJson),
                    "Self-test runtime profile");
                byte[] report = CreateReport(
                    englishRaw,
                    baseRaw,
                    catalogBytes,
                    builtRaw,
                    validation,
                    composition);
                StrictIo.WriteNewFile(reportPath, report, "Self-test build report");

                BuildCompatibilityRequest request = Request(
                    profilePath, englishPath, basePath, stampPath, catalogPath, builtPath, reportPath, outputPath);
                SignedUpdateCompatibilityProfile result = CompatibilityProfileBuilder.Build(request);
                SignedUpdateCompatibilityProfile[] parsed = StrictIo.DeserializeStrict<SignedUpdateCompatibilityProfile[]>(
                    StrictIo.ReadRegularFile(outputPath, "Self-test compatibility output"),
                    "Self-test compatibility output");
                if (parsed.Length != 1
                    || parsed[0].ProfileId != result.ProfileId
                    || result.Composition.AppliedRu != composition.AppliedTranslations
                    || result.Composition.ValidationErrors != 0)
                {
                    throw new InvalidOperationException("Compatibility profile self-test output is not exact.");
                }

                ExpectFailure(() => CompatibilityProfileBuilder.Build(request));

                string tamperedReportPath = Path.Combine(root, "build-report.tampered.json");
                string reportText = StrictIo.DecodeStrictUtf8(report, "Self-test build report");
                string catalogHash = StrictIo.Sha256(catalogBytes);
                string wrongCatalogHash = (catalogHash[0] == 'A' ? "B" : "A") + catalogHash.Substring(1);
                StrictIo.WriteNewFile(
                    tamperedReportPath,
                    StrictIo.Utf8.GetBytes(reportText.Replace(catalogHash, wrongCatalogHash, StringComparison.Ordinal)),
                    "Tampered self-test report");
                ExpectFailure(() => CompatibilityProfileBuilder.Build(Request(
                    profilePath, englishPath, basePath, stampPath, catalogPath, builtPath, tamperedReportPath,
                    Path.Combine(root, "compatibility.tampered-report.json"))));

                string tamperedBuiltPath = Path.Combine(root, "dl_uk_UA.ru.tampered.bin");
                Loc1Document tamperedDocument = Loc1Codec.Parse((byte[])builtRaw.Clone());
                tamperedDocument.Entries[0].Value = "Подмена";
                StrictIo.WriteNewFile(tamperedBuiltPath, Loc1Codec.BuildRaw(tamperedDocument), "Tampered built LOC1");
                ExpectFailure(() => CompatibilityProfileBuilder.Build(Request(
                    profilePath, englishPath, basePath, stampPath, catalogPath, tamperedBuiltPath, reportPath,
                    Path.Combine(root, "compatibility.tampered-built.json"))));

                string tamperedProfilePath = Path.Combine(root, "runtime-profile.tampered.json");
                string profileText = JsonSerializer.Serialize(runtimeProfile, StrictIo.DisplayJson);
                string stampHash = runtimeProfile.StampSha256;
                string wrongStampHash = (stampHash[0] == 'A' ? "B" : "A") + stampHash.Substring(1);
                StrictIo.WriteNewFile(
                    tamperedProfilePath,
                    StrictIo.Utf8.GetBytes(profileText.Replace(stampHash, wrongStampHash, StringComparison.Ordinal)),
                    "Tampered runtime profile");
                ExpectFailure(() => CompatibilityProfileBuilder.Build(Request(
                    tamperedProfilePath, englishPath, basePath, stampPath, catalogPath, builtPath, reportPath,
                    Path.Combine(root, "compatibility.tampered-profile.json"))));
            }
            finally
            {
                StrictIo.TryDeleteDirectory(root);
            }
        }

        private static BuildCompatibilityRequest Request(
            string profile,
            string english,
            string baseLocale,
            string stamp,
            string catalog,
            string built,
            string report,
            string output)
        {
            return new BuildCompatibilityRequest
            {
                RuntimeProfilePath = profile,
                EnglishLoc1Path = english,
                BaseLoc1Path = baseLocale,
                StampPath = stamp,
                CatalogPath = catalog,
                BuiltLoc1Path = built,
                BuildReportPath = report,
                OutputPath = output
            };
        }

        private static byte[] CreateReport(
            byte[] englishRaw,
            byte[] baseRaw,
            byte[] catalog,
            byte[] builtRaw,
            ValidationReport validation,
            CompositionSummary composition)
        {
            Loc1Document english = Loc1Codec.Parse(englishRaw);
            Loc1Document target = Loc1Codec.Parse(baseRaw);
            object value = new
            {
                schema = 1,
                kind = "invokers-ru-preview-build",
                source = new
                {
                    english_content_guid = english.ContentGuid,
                    english_content_version = english.ContentVersion,
                    english_schema = english.FormatVersion,
                    english_container_sha256 = StrictIo.Sha256(englishRaw),
                    english_raw_sha256 = StrictIo.Sha256(englishRaw),
                    base_content_guid = target.ContentGuid,
                    base_content_version = target.ContentVersion,
                    base_schema = target.FormatVersion,
                    base_container_sha256 = StrictIo.Sha256(baseRaw),
                    base_raw_sha256 = StrictIo.Sha256(baseRaw),
                    translations_sha256 = StrictIo.Sha256(catalog)
                },
                target = new
                {
                    locale_id = target.LocaleId,
                    locale_revision = target.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                    release_revision = target.ReleaseRevision,
                    entries = target.Entries.Count
                },
                composition = new
                {
                    applied_ru = composition.AppliedTranslations,
                    english_fallback = composition.EnglishFallbacks,
                    base_fallback = composition.BaseFallbacks,
                    missing_catalog = composition.MissingCatalogRecords,
                    stale_catalog = composition.StaleCatalogRecords,
                    rejected_catalog = composition.RejectedCatalogRecords,
                    needs_review_fallback = composition.NeedsReviewFallbacks,
                    policy_fallback = composition.PolicyFallbacks
                },
                validation = new
                {
                    profile = "preview",
                    errors = validation.ErrorCount,
                    warnings = validation.WarningCount
                },
                build_options = new
                {
                    include_draft = true,
                    exclude_needs_review = false,
                    per_locale_content_version = true,
                    container = "raw",
                    release = false
                },
                output = new
                {
                    bytes = builtRaw.LongLength,
                    raw_bytes = builtRaw.LongLength,
                    container_sha256 = StrictIo.Sha256(builtRaw),
                    raw_sha256 = StrictIo.Sha256(builtRaw)
                }
            };
            return JsonSerializer.SerializeToUtf8Bytes(value, StrictIo.DisplayJson);
        }

        private static byte[] CreateLoc1Raw(uint localeId, uint releaseRevision, uint localeRevision, string[] values)
        {
            byte[] guid = StrictIo.Utf8.GetBytes(ContentGuid);
            byte[] version = StrictIo.Utf8.GetBytes(ContentVersion);
            int headerLength = checked(0x54 + guid.Length + version.Length);
            ulong dataOffset = checked((ulong)headerLength + ((ulong)values.Length * 16UL));
            var header = new byte[headerLength];
            header[0] = (byte)'L';
            header[1] = (byte)'O';
            header[2] = (byte)'C';
            header[3] = (byte)'1';
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04, 4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08, 4), localeId);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C, 4), releaseRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10, 4), localeRevision);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C, 4), checked((uint)values.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x20, 8), checked((ulong)headerLength));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x28, 8), dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x40, 8), dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x48, 8), dataOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x50, 2), checked((ushort)guid.Length));
            guid.CopyTo(header.AsSpan(0x52));
            int versionLengthOffset = 0x52 + guid.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(versionLengthOffset, 2), checked((ushort)version.Length));
            version.CopyTo(header.AsSpan(versionLengthOffset + 2));

            var entries = new List<Loc1Entry>(values.Length);
            uint offset = 0;
            for (int index = 0; index < values.Length; index++)
            {
                uint length = checked((uint)StrictIo.Utf8.GetByteCount(values[index]));
                entries.Add(new Loc1Entry(index, checked((ulong)index + 1), offset, length, values[index]));
                offset = checked(offset + length);
            }

            var document = new Loc1Document(
                header,
                4,
                localeId,
                releaseRevision,
                localeRevision,
                dataOffset,
                offset,
                ContentGuid,
                ContentVersion,
                entries);
            return Loc1Codec.BuildRaw(document);
        }

        private static void ExpectFailure(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }

            throw new InvalidOperationException("Compatibility self-test expected tampered input to be rejected.");
        }
    }
}
