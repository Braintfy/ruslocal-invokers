using InvokersRu.Core;
using InvokersRu.Core.Patching;
using InvokersRu.Gui;
using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvokersRu.SmokeTests
{
    internal static class FixtureFreeRuntimeCacheSmokeTests
    {
        private const string CurrentProfileFileName = "runtime-cache-profile.0.60.1247.json";
        private const string ContentGuid = "ad875e27-1bf6-4f4a-8ed5-3957d0ed05fa";
        private const string ContentVersion = "Prod_synthetic_68";

        internal static void Run(Action<string> passed)
        {
            string profilePath = Path.Combine(AppContext.BaseDirectory, CurrentProfileFileName);
            CurrentProfileStrictParsingCheck(profilePath);
            passed("0.60.1247 profile strict parsing, community policy, and fallback pins");

            CliJsonContractCheck(profilePath);
            passed("CLI cache-plan JSON contract is consumable without a GUI reference");

            if (MutationCapability.IsTestWriteBuild)
            {
                SupersededOfficialUpdateInspectionCheck();
                passed("official update supersedes only a completely restorable old runtime-cache state");
            }
        }

        private static void CurrentProfileStrictParsingCheck(string profilePath)
        {
            Require(File.Exists(profilePath), $"Current runtime-cache profile was not copied to smoke output: {profilePath}");
            string json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(profilePath)).TrimStart('\uFEFF');
            RuntimeCacheCompatibility profile = RuntimeCacheCompatibility.Parse(json);

            Require(profile.Id == "runtime-cache-win64-0.60.1247-prod68"
                && profile.GameVersion == "0.60.1247"
                && profile.StampValue == profile.GameVersion
                && profile.EntryCount == 41_292
                && profile.ExpectedAppliedTranslations == 41_037
                && profile.ExpectedEnglishFallbacks == 1
                && profile.ExpectedBaseFallbacks == 254
                && profile.ExpectedNeedsReviewFallbacks == 0
                && profile.TranslationPolicy == "community-preview-all-drafts"
                && profile.Certified
                && profile.Readiness == "ready",
                "The current runtime-cache release profile lost one or more exact compatibility/composition pins.");
            Require(profile.ExpectedAppliedTranslations + profile.ExpectedEnglishFallbacks + profile.ExpectedBaseFallbacks == profile.EntryCount,
                "The current runtime-cache profile no longer accounts for every LOC1 entry exactly once.");

            JsonObject fallbackMismatch = ParseObject(json);
            fallbackMismatch["expected_english_fallbacks"] = 2;
            Require(ProfileRejected(fallbackMismatch.ToJsonString()),
                "A certified profile whose RU/English/base composition does not sum to entry_count was accepted.");

            JsonObject invalidPolicy = ParseObject(json);
            invalidPolicy["translation_policy"] = "community-preview-all-drafts-typo";
            Require(ProfileRejected(invalidPolicy.ToJsonString()),
                "An unknown community translation policy was accepted.");

            JsonObject unknownMember = ParseObject(json);
            unknownMember["unexpected_release_override"] = true;
            Require(ProfileRejected(unknownMember.ToJsonString()),
                "An unknown property in the trusted runtime-cache profile was accepted.");

            int objectStart = json.IndexOf('{');
            Require(objectStart >= 0, "Current runtime-cache profile is not a JSON object.");
            string duplicateSchema = json.Insert(objectStart + 1, "\"schema\":1,");
            Require(ProfileRejected(duplicateSchema),
                "A duplicate property in the trusted runtime-cache profile was accepted.");
        }

        private static void SupersededOfficialUpdateInspectionCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-superseded-{Guid.NewGuid():N}");
            string cacheRoot = Path.Combine(root, "cache");
            string stateRoot = Path.Combine(root, "state");
            string statePath = Path.Combine(stateRoot, "state.v1.json");
            Directory.CreateDirectory(cacheRoot);
            try
            {
                byte[] english = CreateLoc1(1, 68, 0x1234ABCD, new[] { "Open", "Exit" });
                byte[] officialBase = CreateLoc1(8, 68, 0xD7A0FEFB, new[] { "Відкрити", "Вийти" });
                byte[] stamp = Encoding.UTF8.GetBytes("0.60.synthetic");
                (string englishPath, string targetPath, string stampPath) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
                File.WriteAllBytes(englishPath, english);
                File.WriteAllBytes(targetPath, officialBase);
                File.WriteAllBytes(stampPath, stamp);

                var profile = new RuntimeCacheCompatibility
                {
                    Id = "synthetic-runtime-cache-current",
                    GameVersion = "0.60.synthetic",
                    ContentGuid = ContentGuid,
                    EnglishContentVersion = ContentVersion,
                    BaseContentVersion = ContentVersion,
                    EnglishSha256 = Hashing.Sha256Bytes(english),
                    BaseSha256 = Hashing.Sha256Bytes(officialBase),
                    StampSha256 = Hashing.Sha256Bytes(stamp),
                    StampValue = "0.60.synthetic",
                    EnglishLocaleId = 1,
                    EnglishLocaleRevision = 0x1234ABCD,
                    EnglishReleaseRevision = 68,
                    BaseLocaleId = 8,
                    BaseLocaleRevision = 0xD7A0FEFB,
                    BaseReleaseRevision = 68,
                    EntryCount = 2,
                    Readiness = "ready",
                    Certified = true,
                    TranslationCatalogSha256 = new string('A', 64),
                    ExpectedOutputSha256 = new string('B', 64),
                    MinimumAppliedTranslations = 1,
                    ExpectedAppliedTranslations = 1,
                    ExpectedEnglishFallbacks = 0,
                    ExpectedBaseFallbacks = 1,
                    ExpectedNeedsReviewFallbacks = 0,
                    TranslationPolicy = "community-preview-all-drafts"
                };
                profile.Validate();

                byte[] previousOfficialBase = Encoding.UTF8.GetBytes("immutable previous official cache");
                string previousOriginalHash = Hashing.Sha256Bytes(previousOfficialBase);
                string backupPath = Path.Combine(stateRoot, "backups", "retired-runtime-cache", $"{previousOriginalHash}.dl_uk_UA.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.WriteAllBytes(backupPath, previousOfficialBase);
                var oldState = new PatchState
                {
                    BuildId = "retired-runtime-cache",
                    GameRoot = Path.GetFullPath(cacheRoot),
                    TargetPath = Path.GetFullPath(targetPath),
                    BackupPath = Path.GetFullPath(backupPath),
                    OriginalSha256 = previousOriginalHash,
                    PatchedSha256 = new string('C', 64),
                    TranslationsSha256 = new string('D', 64),
                    AppliedAt = DateTimeOffset.Parse("2026-08-19T18:00:00Z", CultureInfo.InvariantCulture),
                    AppliedTranslations = 1
                };
                Directory.CreateDirectory(stateRoot);
                File.WriteAllText(statePath, JsonSerializer.Serialize(oldState), new UTF8Encoding(false));

                RuntimeCacheInspection superseded = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                Require(superseded.Status == InstallationStatus.PatchSupersededByOfficialUpdate
                    && superseded.State != null
                    && superseded.Message.Contains("game update replaced", StringComparison.OrdinalIgnoreCase),
                    "An exact official tuple with a valid old state/backup was not classified as superseded by an official update.");

                File.WriteAllBytes(backupPath, Encoding.UTF8.GetBytes("corrupted stale backup"));
                RuntimeCacheInspection invalidBackup = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
                Require(invalidBackup.Status == InstallationStatus.InconsistentState
                    && invalidBackup.Message.Contains("backup is missing or invalid", StringComparison.OrdinalIgnoreCase),
                    "An exact official tuple with an invalid stale backup was allowed to reapply.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void CliJsonContractCheck(string profilePath)
        {
            string root = Path.Combine(Path.GetTempPath(), $"invokersru-cli-json-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            using var standardOut = new StringWriter(CultureInfo.InvariantCulture);
            using var standardError = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode;
            try
            {
                Console.SetOut(standardOut);
                Console.SetError(standardError);
                string cliPath = Path.Combine(AppContext.BaseDirectory, "InvokersRu.Cli.dll");
                Require(File.Exists(cliPath), "The CLI project reference was not copied to the smoke output.");
                Assembly cliAssembly = Assembly.LoadFrom(cliPath);
                Type program = cliAssembly.GetType("InvokersRu.Cli.Program", throwOnError: true)!;
                MethodInfo main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("InvokersRu.Cli.Program.Main was not found.");
                object? result = main.Invoke(null, new object[]
                {
                    new[] { "cache-plan", "--cache-root", root, "--profile", profilePath, "--json" }
                });
                exitCode = result is int value ? value : throw new InvalidOperationException("CLI Main returned no integer exit code.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new InvalidOperationException("CLI cache-plan JSON contract invocation failed.", exception.InnerException);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }

            Require(exitCode == 5, $"CLI cache-plan over a missing synthetic cache returned {exitCode}; stderr: {standardError}");
            CliPlanResult guiPlan = CliPlanResult.Parse(new CliCommandResult(
                exitCode,
                standardOut.ToString(),
                standardError.ToString()));
            Require(guiPlan.Status == "MissingFiles"
                && guiPlan.PlanAction == "REFUSE_UNKNOWN_OR_INCONSISTENT"
                && !guiPlan.CanApply && !guiPlan.CanRestore && !guiPlan.CanRecover,
                "The production CLI JSON was rejected or changed meaning in the exact GUI parser.");

            using JsonDocument document = JsonDocument.Parse(standardOut.ToString());
            JsonElement response = document.RootElement;
            Require(response.GetProperty("schema").GetInt32() == 1
                && response.GetProperty("patcher_version").ValueKind == JsonValueKind.String
                && !response.GetProperty("installation_writes_enabled").GetBoolean()
                && response.GetProperty("status").ValueKind == JsonValueKind.String
                && response.GetProperty("observed").ValueKind == JsonValueKind.Object
                && response.GetProperty("observed").TryGetProperty("game_version", out _)
                && response.GetProperty("catalog").ValueKind == JsonValueKind.Object
                && response.GetProperty("catalog").TryGetProperty("present", out _)
                && response.GetProperty("catalog").TryGetProperty("regular_file", out _)
                && response.GetProperty("catalog").TryGetProperty("sha256", out _)
                && response.GetProperty("catalog").TryGetProperty("exact_match", out _)
                && response.GetProperty("state").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("journal").ValueKind is JsonValueKind.Null or JsonValueKind.Object
                && response.GetProperty("process_conflicts").ValueKind == JsonValueKind.Array
                && response.GetProperty("plan").ValueKind == JsonValueKind.String
                && !response.GetProperty("can_apply").GetBoolean()
                && !response.GetProperty("can_restore").GetBoolean()
                && !response.GetProperty("can_recover").GetBoolean(),
                "CLI cache-plan JSON lost a required top-level field or field type.");

            JsonElement profile = response.GetProperty("profile");
            Require(profile.GetProperty("id").GetString() == "runtime-cache-win64-0.60.1247-prod68"
                && profile.GetProperty("game_version").GetString() == "0.60.1247"
                && profile.GetProperty("translation_policy").GetString() == "community-preview-all-drafts"
                && profile.GetProperty("entry_count").GetInt32() == 41_292
                && profile.GetProperty("applied_translations").GetInt32() == 41_037
                && profile.GetProperty("english_fallbacks").GetInt32() == 1
                && profile.GetProperty("base_fallbacks").GetInt32() == 254
                && profile.GetProperty("needs_review_fallbacks").GetInt32() == 0,
                "CLI cache-plan JSON lost the exact current release profile/composition contract.");
        }

        private static byte[] CreateLoc1(uint localeId, uint releaseRevision, uint localeRevision, string[] values)
        {
            const int headerSize = 160;
            byte[] guid = Encoding.UTF8.GetBytes(ContentGuid);
            byte[] version = Encoding.UTF8.GetBytes(ContentVersion);
            byte[][] encodedValues = new byte[values.Length][];
            int dataLength = 0;
            for (int index = 0; index < values.Length; index++)
            {
                encodedValues[index] = Encoding.UTF8.GetBytes(values[index]);
                dataLength = checked(dataLength + encodedValues[index].Length);
            }

            int dataOffset = checked(headerSize + (values.Length * 16));
            byte[] raw = new byte[checked(dataOffset + dataLength)];
            Encoding.ASCII.GetBytes("LOC1").CopyTo(raw, 0);
            WriteUInt32(raw, 0x04, 4);
            WriteUInt32(raw, 0x08, localeId);
            WriteUInt32(raw, 0x0C, releaseRevision);
            WriteUInt32(raw, 0x10, localeRevision);
            WriteUInt32(raw, 0x1C, checked((uint)values.Length));
            WriteUInt64(raw, 0x20, headerSize);
            WriteUInt64(raw, 0x28, checked((ulong)dataOffset));
            WriteUInt64(raw, 0x30, checked((ulong)dataLength));
            WriteUInt64(raw, 0x40, checked((ulong)dataOffset));
            WriteUInt64(raw, 0x48, checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x50, 2), checked((ushort)guid.Length));
            guid.CopyTo(raw, 0x52);
            int versionLengthOffset = 0x52 + guid.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(versionLengthOffset, 2), checked((ushort)version.Length));
            version.CopyTo(raw, versionLengthOffset + 2);

            int valueOffset = 0;
            for (int index = 0; index < encodedValues.Length; index++)
            {
                int recordOffset = headerSize + (index * 16);
                WriteUInt64(raw, recordOffset, checked((ulong)(index + 1)));
                WriteUInt32(raw, recordOffset + 8, checked((uint)valueOffset));
                WriteUInt32(raw, recordOffset + 12, checked((uint)encodedValues[index].Length));
                encodedValues[index].CopyTo(raw, dataOffset + valueOffset);
                valueOffset += encodedValues[index].Length;
            }

            return raw;
        }

        private static JsonObject ParseObject(string json)
        {
            return JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException("Runtime-cache profile did not parse as a JSON object in the smoke test.");
        }

        private static bool ProfileRejected(string json)
        {
            try
            {
                RuntimeCacheCompatibility.Parse(json);
                return false;
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is JsonException)
            {
                return true;
            }
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
