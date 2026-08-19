using InvokersRu.Core;
using InvokersRu.Core.Loc1;
using InvokersRu.Core.Patching;
using InvokersRu.Core.Translations;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InvokersRu.Cli
{
    internal static class Program
    {
        private const string RiskAcknowledgement = "I_ACCEPT_LOCAL_MODIFICATION";
        private const string TrustedCompatibilityResource = "InvokersRu.TrustedCompatibility.json";
        private const string TrustedRuntimeCacheCompatibilityResource = "InvokersRu.TrustedRuntimeCacheCompatibility.json";
#if INVOKERSRU_SUPERVISED_WRITES
        private static readonly bool InstallationWritesEnabled = true;
#else
        private static readonly bool InstallationWritesEnabled = false;
#endif

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h")
            {
                PrintHelp();
                return 0;
            }

            try
            {
                var options = new ArgumentBag(args, 1);
                string command = args[0].ToLowerInvariant();
                ValidateCommandOptions(command, options);
                switch (command)
                {
                    case "inspect": return Inspect(options);
                    case "roundtrip": return RoundTrip(options);
                    case "jobs": return ExportJobs(options);
                    case "import-results": return ImportResults(options);
                    case "validate": return Validate(options);
                    case "diff": return Diff(options);
                    case "build": return Build(options);
                    case "status": return Status(options, plan: false);
                    case "plan": return Status(options, plan: true);
                    case "trusted-manifest-info": return TrustedManifestInfo(options);
                    case "trusted-runtime-cache-info": return TrustedRuntimeCacheInfo(options);
                    case "cache-profile": return RuntimeCacheProfile(options);
                    case "cache-status": return RuntimeCacheStatus(options, plan: false);
                    case "cache-plan": return RuntimeCacheStatus(options, plan: true);
                    case "cache-apply": return RuntimeCacheApply(options);
                    case "cache-restore": return RuntimeCacheRestore(options);
                    case "cache-recover": return RuntimeCacheRecover(options);
                    case "apply": return Apply(options);
                    case "restore": return Restore(options);
                    case "recover": return Recover(options);
                    default:
                        Console.Error.WriteLine($"Unknown command: {args[0]}");
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                || exception is InvalidDataException
                || exception is IOException
                || exception is UnauthorizedAccessException
                || exception is InvalidOperationException
                || exception is Loc1FormatException)
            {
                Console.Error.WriteLine($"ERROR: {exception.Message}");
                return 1;
            }
        }

        private static int Inspect(ArgumentBag options)
        {
            string path = options.Positional(0, "LOC1 or LOC1 Brotli file");
            options.RequireNoExtraPositionals(1);
            byte[] raw = Loc1Codec.ReadContainer(path);
            Loc1Document document = Loc1Codec.Parse(raw);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = Path.GetFullPath(path),
                container_sha256 = Hashing.Sha256File(path),
                raw_sha256 = Hashing.Sha256Bytes(raw),
                raw_bytes = raw.Length,
                schema = document.FormatVersion,
                locale_id = document.LocaleId,
                locale_revision = document.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                release_revision = document.ReleaseRevision,
                header_bytes = document.HeaderSize,
                data_offset = document.DataOffset,
                data_bytes = document.DataLength,
                entries = document.Entries.Count,
                empty_entries = document.Entries.Count(entry => entry.Value == null),
                content_guid = document.ContentGuid,
                content_version = document.ContentVersion
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        private static int RoundTrip(ArgumentBag options)
        {
            string path = options.Positional(0, "LOC1 or LOC1 Brotli file");
            options.RequireNoExtraPositionals(1);
            byte[] original = Loc1Codec.ReadContainer(path);
            Loc1Document document = Loc1Codec.Parse(original);
            byte[] rebuilt = Loc1Codec.BuildRaw(document);
            bool identical = original.SequenceEqual(rebuilt);
            Console.WriteLine($"Original raw SHA-256: {Hashing.Sha256Bytes(original)}");
            Console.WriteLine($"Rebuilt  raw SHA-256: {Hashing.Sha256Bytes(rebuilt)}");
            Console.WriteLine($"Byte-identical: {identical}");
            return identical ? 0 : 3;
        }

        private static int ExportJobs(ArgumentBag options)
        {
            string englishPath = options.Required("english");
            string ukrainianPath = options.Optional("ukrainian", string.Empty);
            string translationsPath = options.Optional("translations", string.Empty);
            string outputPath = options.Required("output");
            ArgumentBag.RequireDifferentFiles(englishPath, outputPath);
            if (!string.IsNullOrWhiteSpace(ukrainianPath)) ArgumentBag.RequireDifferentFiles(ukrainianPath, outputPath);
            if (!string.IsNullOrWhiteSpace(translationsPath)) ArgumentBag.RequireDifferentFiles(translationsPath, outputPath);
            ArgumentBag.RequireSafeNewOutput(outputPath);
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document? ukrainian = string.IsNullOrWhiteSpace(ukrainianPath) ? null : Loc1Codec.ReadFile(ukrainianPath);
            TranslationCatalog existing = string.IsNullOrWhiteSpace(translationsPath) || !File.Exists(translationsPath)
                ? TranslationCatalog.Empty()
                : TranslationCatalog.LoadJsonLines(translationsPath);
            if (!int.TryParse(options.Optional("max-chars", "8000"), NumberStyles.None, CultureInfo.InvariantCulture, out int maxCharacters) || maxCharacters < 100 || maxCharacters > 100000)
            {
                throw new ArgumentException("--max-chars must be an integer from 100 through 100000.");
            }

            JobExportSummary summary = TranslationCatalog.ExportPrivateJobs(
                english,
                ukrainian,
                existing,
                outputPath,
                options.Has("include-translated"),
                deduplicate: !options.Has("per-id"),
                reviewQueue: options.Has("review-queue"),
                includeSensitive: options.Has("include-sensitive"),
                maxSourceCharacters: maxCharacters);
            Console.WriteLine($"Private work items written: {summary.ItemCount:N0} jobs for {summary.SourceIdCount:N0} string ids");
            Console.WriteLine($"Approximate model workload: input {summary.ApproximateInputTokens:N0}, output {summary.ApproximateOutputTokens:N0} tokens (rough character estimate).");
            Console.WriteLine($"Excluded by default: sensitive {summary.SkippedSensitiveIds:N0} ids; oversized {summary.SkippedOversizeIds:N0} ids.");
            Console.WriteLine("No external API request was made. Process deterministic local chunks inside Codex.");
            return 0;
        }

        private static int Validate(ArgumentBag options)
        {
            Loc1Document english = Loc1Codec.ReadFile(options.Required("english"));
            string ukrainianPath = options.Optional("ukrainian", string.Empty);
            Loc1Document? ukrainian = string.IsNullOrWhiteSpace(ukrainianPath) ? null : Loc1Codec.ReadFile(ukrainianPath);
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(options.Required("translations"));
            string profileName = options.Optional("profile", "preview");
            ValidationProfile profile = profileName switch
            {
                "preview" => ValidationProfile.Preview,
                "release" => ValidationProfile.Release,
                _ => throw new ArgumentException("--profile must be preview or release.")
            };
            if (profile == ValidationProfile.Release && options.Has("include-draft"))
            {
                throw new ArgumentException("Release validation never accepts --include-draft.");
            }
            if (profile == ValidationProfile.Release && ukrainian == null)
            {
                throw new ArgumentException("Release validation requires --ukrainian to verify context-hint freshness.");
            }

            ValidationReport report = TranslationValidator.Validate(
                english,
                catalog,
                options.Has("include-draft"),
                ukrainian,
                profile,
                options.Has("per-locale-content-version"));
            Console.WriteLine($"Catalog: {report.CatalogRecords:N0}; fresh: {report.FreshRecords:N0}; usable: {report.UsableRecords:N0}; stale: {report.StaleRecords:N0}");
            Console.WriteLine($"Release coverage: approved {report.ApprovedRecords:N0}; required {report.CoverageRequiredRecords:N0}; missing {report.CoverageMissingRecords:N0}; sensitive English fallback {report.SensitiveFallbackRecords:N0}");
            Console.WriteLine($"Errors: {report.ErrorCount:N0}; warnings: {report.WarningCount:N0}; unknown ids: {report.MissingSourceIds:N0}");
            foreach (TranslationIssue issue in report.Issues.Take(100))
            {
                Console.WriteLine($"{issue.Severity}: {issue.Id} {issue.Code} - {issue.Message}");
            }

            if (report.Issues.Count > 100)
            {
                Console.WriteLine($"... {report.Issues.Count - 100:N0} more issues omitted");
            }

            return report.ErrorCount == 0 ? 0 : 4;
        }

        private static int Diff(ArgumentBag options)
        {
            Loc1Document english = Loc1Codec.ReadFile(options.Required("english"));
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLines(options.Required("translations"));
            var records = catalog.Records.ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
            var currentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fresh = 0;
            int changed = 0;
            int missing = 0;
            int emptySource = 0;
            foreach (Loc1Entry entry in english.Entries)
            {
                currentIds.Add(entry.Id);
                if (entry.Value == null)
                {
                    emptySource++;
                    continue;
                }

                if (!records.TryGetValue(entry.Id, out TranslationRecord? record))
                {
                    missing++;
                }
                else if (Hashing.FixedEqualsHex(record.SourceSha256, Hashing.Sha256Text(entry.Value)))
                {
                    fresh++;
                }
                else
                {
                    changed++;
                }
            }

            int deleted = records.Keys.Count(id => !currentIds.Contains(id));
            Console.WriteLine($"Fresh translations: {fresh:N0}");
            Console.WriteLine($"Changed source (stale): {changed:N0}");
            Console.WriteLine($"Missing translations/new ids: {missing:N0}");
            Console.WriteLine($"Deleted ids still in overlay: {deleted:N0}");
            Console.WriteLine($"Empty source ids: {emptySource:N0}");
            return 0;
        }

        private static int ImportResults(ArgumentBag options)
        {
            string englishPath = options.Required("english");
            string jobsPath = options.Required("jobs");
            string resultsPath = options.Required("results");
            string outputPath = options.Required("output");
            ArgumentBag.RequireDifferentFiles(englishPath, outputPath);
            ArgumentBag.RequireDifferentFiles(jobsPath, outputPath);
            ArgumentBag.RequireDifferentFiles(resultsPath, outputPath);
            ArgumentBag.RequireSafeNewOutput(outputPath);
            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            string existingPath = options.Optional("translations", string.Empty);
            TranslationCatalog existing = string.IsNullOrWhiteSpace(existingPath) || !File.Exists(existingPath)
                ? TranslationCatalog.Empty()
                : TranslationCatalog.LoadJsonLines(existingPath);
            ImportSummary summary = TranslationCatalog.ImportDraftResults(
                english,
                jobsPath,
                resultsPath,
                existing,
                outputPath,
                allowPartial: options.Has("allow-partial"));
            Console.WriteLine($"Imported {summary.ImportedIds:N0} draft ids from {summary.ResultJobs:N0} result jobs.");
            Console.WriteLine($"Preserved reviewed/approved: {summary.PreservedReviewedIds:N0}; flagged for review: {summary.NeedsReviewIds:N0}; jobs without a result: {summary.MissingResultJobs:N0}");
            return 0;
        }

        private static int Build(ArgumentBag options)
        {
            string englishPath = options.Required("english");
            string basePath = options.Required("base");
            string translationsPath = options.Required("translations");
            string outputPath = options.Required("output");
            string reportPath = options.Optional("report", string.Empty);
            ArgumentBag.RequireDifferentFiles(basePath, outputPath);
            ArgumentBag.RequireDifferentFiles(englishPath, outputPath);
            ArgumentBag.RequireDifferentFiles(translationsPath, outputPath);
            ArgumentBag.RequireSafeNewOutput(outputPath);
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                ArgumentBag.RequireDifferentFiles(reportPath, outputPath);
                ArgumentBag.RequireDifferentFiles(reportPath, basePath);
                ArgumentBag.RequireDifferentFiles(reportPath, englishPath);
                ArgumentBag.RequireDifferentFiles(reportPath, translationsPath);
                ArgumentBag.RequireSafeNewOutput(reportPath);
            }

            Loc1Document english = Loc1Codec.ReadFile(englishPath);
            Loc1Document baseLocale = Loc1Codec.ReadFile(basePath);
            byte[] translationSnapshot = File.ReadAllBytes(translationsPath);
            TranslationCatalog catalog = TranslationCatalog.LoadJsonLinesBytes(translationSnapshot);
            bool includeDraft = options.Has("include-draft");
            bool release = options.Has("release");
            bool excludeNeedsReview = options.Has("exclude-needs-review");
            bool rawOutput = options.Has("raw");
            bool allowPerLocaleContentVersion = options.Has("per-locale-content-version");
            bool supervisedSafeDrafts = includeDraft && excludeNeedsReview;
            if (release && includeDraft) throw new ArgumentException("Release build never accepts --include-draft.");
            if (excludeNeedsReview && !includeDraft) throw new ArgumentException("--exclude-needs-review requires --include-draft.");
            ValidationReport report = TranslationValidator.Validate(
                english,
                catalog,
                includeDraft,
                baseLocale,
                release ? ValidationProfile.Release : ValidationProfile.Preview,
                allowPerLocaleContentVersion);
            if (report.ErrorCount > 0)
            {
                throw new InvalidDataException($"Catalog contains {report.ErrorCount} blocking errors.");
            }

            CompositionSummary composition = TranslationComposer.Apply(
                english,
                baseLocale,
                catalog,
                includeDraft,
                approvedOnly: release,
                excludeNeedsReview: excludeNeedsReview,
                allowPerLocaleContentVersion: allowPerLocaleContentVersion,
                eligibility: supervisedSafeDrafts
                    ? (record, source) => RuntimeSafeDraftPolicy.IsEligible(record, source, out _)
                    : null);
            byte[] raw = Loc1Codec.BuildRaw(baseLocale);
            byte[] compressed = Loc1Codec.Compress(raw);
            byte[] outputBytes = rawOutput ? raw : compressed;
            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDirectory);
            string tempOutputPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(tempOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    stream.Write(outputBytes, 0, outputBytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                Loc1Document verification = Loc1Codec.ReadFile(tempOutputPath);
                if (verification.Entries.Count != baseLocale.Entries.Count || verification.LocaleId != baseLocale.LocaleId)
                {
                    throw new InvalidDataException("Generated package failed verification.");
                }

                File.Move(tempOutputPath, fullOutputPath);
            }
            finally
            {
                if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);
            }

            Console.WriteLine($"Built: {fullOutputPath}");
            Console.WriteLine($"Applied RU: {composition.AppliedTranslations:N0}; English fallback: {composition.EnglishFallbacks:N0}; base fallback: {composition.BaseFallbacks:N0}; needs-review fallback: {composition.NeedsReviewFallbacks:N0}");
            string rawHash = Hashing.Sha256Bytes(raw);
            string containerHash = Hashing.Sha256File(outputPath);
            Console.WriteLine($"Raw SHA-256: {rawHash}");
            Console.WriteLine($"Container SHA-256: {containerHash}");
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                var reportObject = new
                {
                    schema = 1,
                    kind = release ? "invokers-ru-release-build" : "invokers-ru-preview-build",
                    source = new
                    {
                        english_content_guid = english.ContentGuid,
                        english_content_version = english.ContentVersion,
                        english_schema = english.FormatVersion,
                        english_container_sha256 = Hashing.Sha256File(englishPath),
                        english_raw_sha256 = Hashing.Sha256Bytes(Loc1Codec.ReadContainer(englishPath)),
                        base_content_guid = baseLocale.ContentGuid,
                        base_content_version = baseLocale.ContentVersion,
                        base_schema = baseLocale.FormatVersion,
                        base_container_sha256 = Hashing.Sha256File(basePath),
                        base_raw_sha256 = Hashing.Sha256Bytes(Loc1Codec.ReadContainer(basePath)),
                        translations_sha256 = Hashing.Sha256Bytes(translationSnapshot)
                    },
                    target = new
                    {
                        locale_id = baseLocale.LocaleId,
                        locale_revision = baseLocale.LocaleRevision.ToString("X8", CultureInfo.InvariantCulture),
                        release_revision = baseLocale.ReleaseRevision,
                        entries = baseLocale.Entries.Count
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
                        profile = release ? "release" : "preview",
                        errors = report.ErrorCount,
                        warnings = report.WarningCount
                    },
                    build_options = new
                    {
                        include_draft = includeDraft,
                        exclude_needs_review = excludeNeedsReview,
                        per_locale_content_version = allowPerLocaleContentVersion,
                        container = rawOutput ? "raw" : "brotli",
                        release
                    },
                    output = new
                    {
                        bytes = outputBytes.LongLength,
                        raw_bytes = raw.LongLength,
                        container_sha256 = containerHash,
                        raw_sha256 = rawHash
                    }
                };
                byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(reportObject, new JsonSerializerOptions { WriteIndented = true });
                WriteNewFileAtomically(reportPath, reportBytes);
                Console.WriteLine($"Build report: {Path.GetFullPath(reportPath)}");
            }
            return 0;
        }

        private static void WriteNewFileAtomically(string path, byte[] content)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static int Status(ArgumentBag options, bool plan)
        {
            CompatibilityManifest compatibility;
            if (plan && InstallationWritesEnabled)
            {
                if (options.Has("compat"))
                {
                    throw new InvalidOperationException("A write-enabled plan uses only its embedded trusted compatibility manifest; --compat is not accepted.");
                }

                compatibility = LoadTrustedMutationCompatibility();
            }
            else
            {
                string compatibilityPath = options.Optional("compat", FindProjectFile("config", "compatibility.v1.json"));
                compatibility = CompatibilityManifest.Load(compatibilityPath);
            }

            string gameRoot = options.Optional("game-root", PatchPlanner.DefaultGameRoot());
            string statePath = options.Optional("state", Path.Combine(PatchPlanner.DefaultStateRoot(), "state.v1.json"));
            InstallationInspection inspection = PatchPlanner.Inspect(gameRoot, compatibility, statePath);
            Console.WriteLine($"Status: {inspection.Status}");
            Console.WriteLine(inspection.Message);
            Console.WriteLine($"Game root: {inspection.GameRoot}");
            if (inspection.Build != null)
            {
                Console.WriteLine($"Build: {inspection.Build.Id} / game {inspection.Build.GameVersion} / {inspection.Build.ContentVersion}");
                Console.WriteLine($"Compatibility readiness: {inspection.Build.Readiness}; certified: {inspection.Build.Certified}");
                if (!string.IsNullOrWhiteSpace(inspection.Build.BlockedReason)) Console.WriteLine($"Blocked reason: {inspection.Build.BlockedReason}");
            }
            if (inspection.Journal != null)
            {
                Console.WriteLine($"Interrupted transaction: {inspection.Journal.Operation} / {inspection.Journal.Phase} / {inspection.Journal.TransactionId}");
            }

            Console.WriteLine($"English SHA-256: {inspection.EnglishSha256 ?? "n/a"}");
            Console.WriteLine($"Base SHA-256: {inspection.BaseSha256 ?? "n/a"}");
            Console.WriteLine($"Manifest SHA-256: {inspection.ManifestSha256 ?? "n/a"}");
            Console.WriteLine($"Pinned game files: {(inspection.GameFilesMatch ? "match" : inspection.GameFileProblem ?? "not checked")}");
            if (plan)
            {
                IReadOnlyList<string> processConflicts = inspection.Status == InstallationStatus.CompatibleOriginal
                    || inspection.Status == InstallationStatus.PatchedByThisTool
                    ? PatchService.FindProcessConflicts(inspection.GameRoot)
                    : Array.Empty<string>();
                Console.WriteLine($"Process conflicts: {(processConflicts.Count == 0 ? "none" : processConflicts.Count.ToString(CultureInfo.InvariantCulture))}");
                foreach (string conflict in processConflicts.Take(8)) Console.WriteLine($"  {conflict}");
                string action = inspection.Status switch
                {
                    InstallationStatus.CompatibleOriginal when processConflicts.Count > 0 => "REFUSE_CLOSE_GAME_AND_LAUNCHER",
                    InstallationStatus.CompatibleOriginal when inspection.Build?.Certified == true && InstallationWritesEnabled => "READY_TO_APPLY",
                    InstallationStatus.CompatibleOriginal when inspection.Build?.Certified == true => "REFUSE_DEV_WRITES_DISABLED",
                    InstallationStatus.CompatibleOriginal => "REFUSE_UNTIL_CERTIFIED",
                    InstallationStatus.PatchedByThisTool => "NOOP_OR_RESTORE",
                    _ => "REFUSE_UNKNOWN_OR_INCONSISTENT"
                };
                Console.WriteLine($"Plan: {action}");
            }

            return inspection.Status == InstallationStatus.UnknownBuild
                || inspection.Status == InstallationStatus.InconsistentState
                || inspection.Status == InstallationStatus.MissingFiles
                || inspection.Status == InstallationStatus.RecoveryRequired ? 5 : 0;
        }

        private static int Apply(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            RejectCustomMutationCompatibility(options);
            string gameRoot = options.Optional("game-root", PatchPlanner.DefaultGameRoot());
            RejectCustomMutationState(options);
            string statePath = Path.Combine(PatchPlanner.DefaultStateRoot(), "state.v1.json");
            CompatibilityManifest compatibility = LoadTrustedMutationCompatibility();
            InstallationInspection inspection = PatchPlanner.Inspect(gameRoot, compatibility, statePath);
            PatchApplyResult result = PatchService.Apply(inspection, options.Required("translations"), statePath, options.Has("include-draft"));
            Console.WriteLine($"Applied {result.Composition.AppliedTranslations:N0} Russian translations.");
            Console.WriteLine($"Backup: {result.State.BackupPath}");
            Console.WriteLine($"Patched SHA-256: {result.State.PatchedSha256}");
            return 0;
        }

        private static bool TryResolveCacheRoot(ArgumentBag options, out string cacheRoot)
        {
            if (options.Has("cache-root"))
            {
                cacheRoot = options.Required("cache-root");
                return true;
            }

            if (RuntimeCacheService.TryDefaultCacheRoot(out cacheRoot, out string problem))
            {
                return true;
            }

            Console.Error.WriteLine($"ERROR: {problem}");
            return false;
        }

        private static int RuntimeCacheProfile(ArgumentBag options)
        {
            options.RequireNoExtraPositionals(0);
            string outputPath = options.Required("output");
            ArgumentBag.RequireSafeNewOutput(outputPath);
            if (!TryResolveCacheRoot(options, out string cacheRoot)) return 5;
            (string defaultEnglish, string defaultBase, string defaultStamp) = RuntimeCacheService.ResolveTuplePaths(cacheRoot);
            string englishPath = options.Optional("english", defaultEnglish);
            string basePath = options.Optional("base", defaultBase);
            string stampPath = options.Optional("stamp", defaultStamp);
            foreach ((string label, string path) in new[]
            {
                ("dl_en_US.bin", englishPath), ("dl_uk_UA.bin", basePath), ("dl_uk_UA.bin.ver", stampPath)
            })
            {
                if (!File.Exists(path))
                {
                    throw new IOException($"Runtime-cache tuple member {label} is missing: {path}");
                }
            }

            RuntimeCacheCompatibility profile = RuntimeCacheService.DescribeTuple(
                englishPath, basePath, stampPath, options.Optional("id", string.Empty));
            var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(profile, serializerOptions) + Environment.NewLine);
            Console.WriteLine($"Runtime-cache profile written: {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"Game version: {profile.GameVersion}; entries: {profile.EntryCount:N0}");
            Console.WriteLine($"English: {profile.EnglishContentVersion} (release {profile.EnglishReleaseRevision}); base: {profile.BaseContentVersion} (release {profile.BaseReleaseRevision})");
            Console.WriteLine($"English SHA-256: {profile.EnglishSha256}");
            Console.WriteLine($"Base SHA-256: {profile.BaseSha256}");
            Console.WriteLine("Readiness is blocked and certified is false: pin the catalog and built output before any supervised write build.");
            return 0;
        }

        private static int RuntimeCacheStatus(ArgumentBag options, bool plan)
        {
            options.RequireNoExtraPositionals(0);
            RuntimeCacheCompatibility profile = plan && InstallationWritesEnabled
                ? LoadTrustedRuntimeCacheCompatibility()
                : RuntimeCacheCompatibility.OfficialObserved0601239();
            if (!TryResolveCacheRoot(options, out string cacheRoot)) return 5;
            string statePath = RuntimeCacheService.DefaultStatePath();
            RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(cacheRoot, profile, statePath);
            Console.WriteLine($"Status: {inspection.Status}");
            Console.WriteLine(inspection.Message);
            Console.WriteLine($"Cache root: {inspection.CacheRoot}");
            Console.WriteLine($"English SHA-256: {inspection.EnglishSha256 ?? "n/a"}");
            Console.WriteLine($"Base SHA-256: {inspection.BaseSha256 ?? "n/a"}");
            Console.WriteLine($"Stamp SHA-256: {inspection.StampSha256 ?? "n/a"}");
            Console.WriteLine($"English content: {inspection.EnglishContentVersion ?? "n/a"}");
            Console.WriteLine($"Base content: {inspection.BaseContentVersion ?? "n/a"}");
            Console.WriteLine($"Translation policy: {profile.TranslationPolicy}");
            Console.WriteLine($"Expected applied translations: {profile.ExpectedAppliedTranslations.ToString(CultureInfo.InvariantCulture)}");
            if (inspection.Journal != null)
                Console.WriteLine($"Interrupted transaction: {inspection.Journal.Operation} / {inspection.Journal.Phase} / {inspection.Journal.TransactionId}");
            if (plan)
            {
                IReadOnlyList<string> conflicts = inspection.Status == InstallationStatus.CompatibleOriginal
                    || inspection.Status == InstallationStatus.PatchedByThisTool
                    ? PatchService.FindRuntimeCacheProcessConflicts()
                    : Array.Empty<string>();
                Console.WriteLine($"Process conflicts: {(conflicts.Count == 0 ? "none" : conflicts.Count.ToString(CultureInfo.InvariantCulture))}");
                string action = inspection.Status switch
                {
                    InstallationStatus.CompatibleOriginal when conflicts.Count > 0 => "REFUSE_CLOSE_GAME_AND_LAUNCHER",
                    InstallationStatus.CompatibleOriginal when !InstallationWritesEnabled => "REFUSE_DEV_WRITES_DISABLED",
                    InstallationStatus.CompatibleOriginal when !profile.Certified => "REFUSE_NO_TRUSTED_CACHE_RELEASE_PROFILE",
                    InstallationStatus.CompatibleOriginal => "READY_TO_APPLY",
                    InstallationStatus.PatchedByThisTool => "NOOP_OR_RESTORE",
                    _ => "REFUSE_UNKNOWN_OR_INCONSISTENT"
                };
                Console.WriteLine($"Plan: {action}");
            }
            return inspection.Status is InstallationStatus.UnknownBuild or InstallationStatus.InconsistentState
                or InstallationStatus.MissingFiles or InstallationStatus.RecoveryRequired ? 5 : 0;
        }

        private static int RuntimeCacheApply(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            RuntimeCacheCompatibility profile = LoadTrustedRuntimeCacheCompatibility();
            bool includeDraft = options.Has("include-draft");
            if (profile.TranslationPolicy == "supervised-safe-drafts" && !includeDraft)
                throw new InvalidOperationException("The supervised-safe-drafts runtime policy requires explicit --include-draft acknowledgement.");
            if (profile.TranslationPolicy != "supervised-safe-drafts" && includeDraft)
                throw new InvalidOperationException("--include-draft is accepted only by the supervised-safe-drafts runtime policy.");
            string statePath = RuntimeCacheService.DefaultStatePath();
            RuntimeCacheInspection inspection = RuntimeCacheService.Inspect(RuntimeCacheService.DefaultCacheRoot(), profile, statePath);
            PatchApplyResult result = RuntimeCacheService.Apply(inspection, options.Required("translations"), statePath);
            Console.WriteLine($"Applied {result.Composition.AppliedTranslations:N0} Russian translations to the raw runtime cache.");
            Console.WriteLine($"Backup: {result.State.BackupPath}");
            Console.WriteLine($"Patched SHA-256: {result.State.PatchedSha256}");
            return 0;
        }

        private static int TrustedRuntimeCacheInfo(ArgumentBag options)
        {
            options.RequireNoExtraPositionals(0);
            byte[] bytes = LoadTrustedRuntimeCacheCompatibilityBytes();
            RuntimeCacheCompatibility profile = ParseTrustedRuntimeCacheCompatibilityBytes(bytes);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                installation_writes_enabled = InstallationWritesEnabled,
                embedded_runtime_cache_profile = true,
                profile_sha256 = Hashing.Sha256Bytes(bytes),
                profile = new
                {
                    id = profile.Id,
                    game_version = profile.GameVersion,
                    readiness = profile.Readiness,
                    certified = profile.Certified,
                    translation_policy = profile.TranslationPolicy,
                    translation_catalog_sha256 = profile.TranslationCatalogSha256,
                    expected_output_sha256 = profile.ExpectedOutputSha256,
                    minimum_applied_translations = profile.MinimumAppliedTranslations,
                    expected_applied_translations = profile.ExpectedAppliedTranslations
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        private static int RuntimeCacheRestore(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            RuntimeCacheService.Restore(RuntimeCacheService.DefaultStatePath(), LoadTrustedRuntimeCacheCompatibility());
            Console.WriteLine("Original raw runtime-cache localization restored.");
            return 0;
        }

        private static int RuntimeCacheRecover(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            Console.WriteLine(RuntimeCacheService.Recover(RuntimeCacheService.DefaultStatePath(), LoadTrustedRuntimeCacheCompatibility()));
            return 0;
        }

        private static int TrustedManifestInfo(ArgumentBag options)
        {
            options.RequireNoExtraPositionals(0);
            byte[] bytes = LoadTrustedMutationCompatibilityBytes();
            CompatibilityManifest manifest = ParseTrustedCompatibilityBytes(bytes);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = 1,
                installation_writes_enabled = InstallationWritesEnabled,
                embedded_manifest = true,
                manifest_sha256 = Hashing.Sha256Bytes(bytes),
                builds = manifest.Builds.Select(build => new
                {
                    id = build.Id,
                    patch_mode = build.PatchMode,
                    readiness = build.Readiness,
                    certified = build.Certified,
                    translation_catalog_sha256 = build.TranslationCatalogSha256,
                    expected_output_sha256 = build.ExpectedOutputSha256,
                    expected_output_raw_sha256 = build.ExpectedOutputRawSha256
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        private static int Restore(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            RejectCustomMutationState(options);
            RejectCustomMutationCompatibility(options);
            string statePath = Path.Combine(PatchPlanner.DefaultStateRoot(), "state.v1.json");
            PatchService.Restore(statePath, LoadTrustedMutationCompatibility());
            Console.WriteLine("Original localization package restored and patch state removed.");
            return 0;
        }

        private static int Recover(ArgumentBag options)
        {
            EnsureInstallationWritesEnabled();
            RequireRiskAcknowledgement(options);
            RejectCustomMutationState(options);
            RejectCustomMutationCompatibility(options);
            string statePath = Path.Combine(PatchPlanner.DefaultStateRoot(), "state.v1.json");
            Console.WriteLine(PatchService.Recover(statePath, LoadTrustedMutationCompatibility()));
            return 0;
        }

        private static void RejectCustomMutationCompatibility(ArgumentBag options)
        {
            if (options.Has("compat"))
            {
                throw new InvalidOperationException("Write commands use only the compatibility manifest embedded at build time; --compat is read-only/status tooling only.");
            }
        }

        private static void RejectCustomMutationState(ArgumentBag options)
        {
            if (options.Has("state"))
            {
                throw new InvalidOperationException("Write commands use only the dedicated LocalAppData state location; --state is read-only/status tooling only.");
            }
        }

        private static void EnsureInstallationWritesEnabled()
        {
            if (!InstallationWritesEnabled)
            {
                throw new InvalidOperationException("All installation writes are disabled in this unsigned dev build independently of compatibility.v1.json. Use read-only status/plan or build a preview; a write-enabled release requires a signed compatibility catalog and loader certification.");
            }
        }

        private static CompatibilityManifest LoadTrustedMutationCompatibility()
        {
            return ParseTrustedCompatibilityBytes(LoadTrustedMutationCompatibilityBytes());
        }

        private static RuntimeCacheCompatibility LoadTrustedRuntimeCacheCompatibility()
        {
            return ParseTrustedRuntimeCacheCompatibilityBytes(LoadTrustedRuntimeCacheCompatibilityBytes());
        }

        private static RuntimeCacheCompatibility ParseTrustedRuntimeCacheCompatibilityBytes(byte[] bytes)
        {
            string json = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            return RuntimeCacheCompatibility.Parse(json);
        }

        private static byte[] LoadTrustedRuntimeCacheCompatibilityBytes()
        {
            if (!InstallationWritesEnabled)
                throw new InvalidOperationException("This ordinary dev build has no trusted runtime-cache mutation profile.");
            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(TrustedRuntimeCacheCompatibilityResource);
            if (stream == null)
                throw new InvalidOperationException("No write-enabled runtime-cache release profile is embedded in this binary.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static CompatibilityManifest ParseTrustedCompatibilityBytes(byte[] bytes)
        {
            string json = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimStart('\uFEFF');
            return CompatibilityManifest.Parse(json);
        }

        private static byte[] LoadTrustedMutationCompatibilityBytes()
        {
            if (!InstallationWritesEnabled)
            {
                throw new InvalidOperationException("This ordinary dev build has no trusted mutation manifest.");
            }

            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(TrustedCompatibilityResource);
            if (stream == null)
            {
                throw new InvalidOperationException("The write-enabled binary has no embedded trusted compatibility manifest.");
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static void RequireRiskAcknowledgement(ArgumentBag options)
        {
            string actual = options.Optional("acknowledge-risk", string.Empty);
            if (!string.Equals(actual, RiskAcknowledgement, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"This command modifies the local installation. Re-run only after review with --acknowledge-risk {RiskAcknowledgement}");
            }
        }

        private static string FindProjectFile(params string[] pathParts)
        {
            string current = Path.Combine(new[] { Directory.GetCurrentDirectory() }.Concat(pathParts).ToArray());
            if (File.Exists(current)) return current;
            string besideExecutable = Path.Combine(new[] { AppContext.BaseDirectory }.Concat(pathParts).ToArray());
            return besideExecutable;
        }

        private static void ValidateCommandOptions(string command, ArgumentBag options)
        {
            (string[] Allowed, string[] Flags) policy = command switch
            {
                "inspect" or "roundtrip" => (Array.Empty<string>(), Array.Empty<string>()),
                "jobs" => (new[] { "english", "ukrainian", "translations", "output", "include-translated", "per-id", "review-queue", "include-sensitive", "max-chars" }, new[] { "include-translated", "per-id", "review-queue", "include-sensitive" }),
                "import-results" => (new[] { "english", "jobs", "results", "translations", "output", "allow-partial" }, new[] { "allow-partial" }),
                "validate" => (new[] { "english", "ukrainian", "translations", "include-draft", "profile", "per-locale-content-version" }, new[] { "include-draft", "per-locale-content-version" }),
                "diff" => (new[] { "english", "translations" }, Array.Empty<string>()),
                "build" => (new[] { "english", "base", "translations", "output", "report", "include-draft", "exclude-needs-review", "release", "raw", "per-locale-content-version" }, new[] { "include-draft", "exclude-needs-review", "release", "raw", "per-locale-content-version" }),
                "status" or "plan" => (new[] { "compat", "game-root", "state" }, Array.Empty<string>()),
                "trusted-manifest-info" => (Array.Empty<string>(), Array.Empty<string>()),
                "trusted-runtime-cache-info" => (Array.Empty<string>(), Array.Empty<string>()),
                "cache-status" or "cache-plan" => (new[] { "cache-root" }, Array.Empty<string>()),
                "cache-profile" => (new[] { "output", "cache-root", "english", "base", "stamp", "id" }, Array.Empty<string>()),
                "cache-apply" => (new[] { "translations", "acknowledge-risk", "include-draft" }, new[] { "include-draft" }),
                "cache-restore" or "cache-recover" => (new[] { "acknowledge-risk" }, Array.Empty<string>()),
                "apply" => (new[] { "translations", "compat", "game-root", "state", "include-draft", "acknowledge-risk" }, new[] { "include-draft" }),
                "restore" or "recover" => (new[] { "compat", "state", "acknowledge-risk" }, Array.Empty<string>()),
                _ => (Array.Empty<string>(), Array.Empty<string>())
            };
            options.RequireOnlyOptions(policy.Allowed, policy.Flags);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("InvokersRu PC localization prototype");
            Console.WriteLine();
            Console.WriteLine("Read-only:");
            Console.WriteLine("  inspect <file>             Parse LOC1/.br metadata");
            Console.WriteLine("  roundtrip <file>           Prove byte-identical raw reconstruction");
            Console.WriteLine("  status [--game-root PATH]  Inspect installed hashes");
            Console.WriteLine("  plan [--game-root PATH]    Explain apply/refuse decision");
            Console.WriteLine("  trusted-manifest-info      Print embedded manifest hash/metadata (supervised build)");
            Console.WriteLine("  trusted-runtime-cache-info Print embedded raw-cache profile pins (supervised build)");
            Console.WriteLine("  cache-status [--cache-root PATH]  Inspect the exact raw localization cache tuple");
            Console.WriteLine("  cache-plan   [--cache-root PATH]  Explain cache apply/refuse decision");
            Console.WriteLine("  validate --english FILE --translations FILE [--ukrainian FILE] [--per-locale-content-version] [--profile preview|release]");
            Console.WriteLine("  diff --english FILE --translations FILE");
            Console.WriteLine();
            Console.WriteLine("Workspace output only:");
            Console.WriteLine("  jobs --english FILE [--ukrainian FILE] --output PRIVATE.jsonl [--per-id] [--review-queue]");
            Console.WriteLine("  import-results --english FILE --jobs PRIVATE.jsonl --results FILE --output ru_RU.jsonl [--allow-partial]");
            Console.WriteLine("  cache-profile --output PROFILE.json [--cache-root PATH] [--english FILE] [--base FILE] [--stamp FILE] [--id NAME]");
            Console.WriteLine("  build --english FILE --base FILE --translations FILE --output FILE [--raw] [--per-locale-content-version] [--report FILE] [--include-draft] [--exclude-needs-review] [--release]");
            Console.WriteLine();
            Console.WriteLine("Installation changes:");
            if (InstallationWritesEnabled)
            {
                Console.WriteLine("  apply --translations FILE --include-draft --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  restore --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  recover --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  cache-apply --translations FILE --include-draft --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  cache-restore --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  cache-recover --acknowledge-risk I_ACCEPT_LOCAL_MODIFICATION");
                Console.WriteLine("  This supervised build accepts only its embedded compatibility manifest for writes.");
            }
            else
            {
                Console.WriteLine("  apply ...                 Disabled in this unsigned dev build");
                Console.WriteLine("  restore ...               Disabled in this unsigned dev build");
                Console.WriteLine("  recover ...               Disabled in this unsigned dev build");
                Console.WriteLine("  cache-apply ...           Disabled in this unsigned dev build");
                Console.WriteLine("  cache-restore ...         Disabled in this unsigned dev build");
                Console.WriteLine("  cache-recover ...         Disabled in this unsigned dev build");
            }
        }
    }
}
