using InvokersRu.Core.Loc1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace InvokersRu.Core.Translations
{
    public sealed class TranslationCatalog
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> AllowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "draft", "reviewed", "approved"
        };

        private readonly Dictionary<ulong, TranslationRecord> _records;

        private TranslationCatalog(Dictionary<ulong, TranslationRecord> records)
        {
            _records = records;
        }

        public int Count => _records.Count;
        public IEnumerable<TranslationRecord> Records => _records.Values;

        public static TranslationCatalog Empty()
        {
            return new TranslationCatalog(new Dictionary<ulong, TranslationRecord>());
        }

        public static TranslationCatalog LoadJsonLines(string path)
        {
            return LoadJsonLinesCore(File.ReadLines(path, StrictUtf8));
        }

        public static TranslationCatalog LoadJsonLinesBytes(byte[] content)
        {
            string text;
            try
            {
                text = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Translation catalog is not strict UTF-8.", exception);
            }

            return LoadJsonLinesCore(text.Split('\n'));
        }

        private static TranslationCatalog LoadJsonLinesCore(IEnumerable<string> lines)
        {
            var records = new Dictionary<ulong, TranslationRecord>();
            int lineNumber = 0;
            foreach (string line in lines)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                TranslationRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<TranslationRecord>(line, JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException($"Invalid translation JSONL at line {lineNumber}: {exception.Message}", exception);
                }

                if (record == null || !TryParseId(record.Id, out ulong id))
                {
                    throw new InvalidDataException($"Invalid 16-digit hexadecimal translation id at line {lineNumber}.");
                }

                record.Id = id.ToString("X16", CultureInfo.InvariantCulture);
                if (record.SourceSha256 == null || record.SourceSha256.Length != 64 || !record.SourceSha256.All(IsHex))
                {
                    throw new InvalidDataException($"Invalid source_sha256 at line {lineNumber}.");
                }

                if (record.Translation == null || record.Status == null)
                {
                    throw new InvalidDataException($"Translation and status cannot be null at line {lineNumber}.");
                }

                if (!string.IsNullOrWhiteSpace(record.HintSha256)
                    && (record.HintSha256.Length != 64 || !record.HintSha256.All(IsHex)))
                {
                    throw new InvalidDataException($"Invalid hint_sha256 at line {lineNumber}.");
                }

                if (!AllowedStatuses.Contains(record.Status))
                {
                    throw new InvalidDataException($"Unsupported translation status at line {lineNumber}: {record.Status}");
                }

                if (record.IssueCodes == null || record.RiskFlags == null || record.ReviewerIds == null)
                {
                    throw new InvalidDataException($"Translation arrays cannot be null at line {lineNumber}.");
                }

                if (record.Confidence != null && record.Confidence != "high" && record.Confidence != "medium" && record.Confidence != "low")
                {
                    throw new InvalidDataException($"Unsupported confidence at line {lineNumber}: {record.Confidence}");
                }

                if (!records.TryAdd(id, record))
                {
                    throw new InvalidDataException($"Duplicate translation id {record.Id} at line {lineNumber}.");
                }
            }

            return new TranslationCatalog(records);
        }

        public bool TryGetUsable(ulong id, string source, bool includeDraft, out TranslationRecord? record, out string reason, bool approvedOnly = false)
        {
            if (!_records.TryGetValue(id, out record))
            {
                reason = "missing";
                return false;
            }

            if (!Hashing.FixedEqualsHex(record.SourceSha256, Hashing.Sha256Text(source)))
            {
                reason = "stale-source";
                return false;
            }

            bool statusAccepted = record.Status.Equals("approved", StringComparison.OrdinalIgnoreCase)
                || (!approvedOnly && record.Status.Equals("reviewed", StringComparison.OrdinalIgnoreCase))
                || (!approvedOnly && includeDraft && record.Status.Equals("draft", StringComparison.OrdinalIgnoreCase));
            if (!statusAccepted)
            {
                reason = "status-not-accepted";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(record.Translation))
            {
                reason = "empty-translation";
                return false;
            }

            reason = "usable";
            return true;
        }

        public bool TryGetRecord(ulong id, out TranslationRecord? record)
        {
            return _records.TryGetValue(id, out record);
        }

        public static JobExportSummary ExportPrivateJobs(
            Loc1Document english,
            Loc1Document? ukrainian,
            TranslationCatalog existing,
            string outputPath,
            bool includeAlreadyTranslated,
            bool deduplicate,
            bool reviewQueue = false,
            bool includeSensitive = false,
            int maxSourceCharacters = 8000)
        {
            Dictionary<ulong, Loc1Entry>? ukrainianByHash = ukrainian?.IndexByHash();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Directory.GetCurrentDirectory());
            int sourceIds = 0;
            long englishCharacters = 0;
            long hintCharacters = 0;
            int skippedSensitiveIds = 0;
            int skippedOversizeIds = 0;
            var groups = new Dictionary<string, PendingJob>(StringComparer.Ordinal);
            foreach (Loc1Entry entry in english.Entries)
            {
                string? source = entry.Value;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                Loc1Entry? ukEntry = null;
                if (ukrainianByHash != null)
                {
                    ukrainianByHash.TryGetValue(entry.KeyHash, out ukEntry);
                }

                string ukrainianHint = ukEntry?.Value ?? string.Empty;
                string? hintSha256 = ukEntry?.Value == null ? null : Hashing.Sha256Text(ukEntry.Value);
                string[] sourceRisks = TranslationValidator.ClassifyRisks(source).ToArray();
                if (!includeSensitive && sourceRisks.Any(TranslationValidator.IsSensitiveRisk))
                {
                    skippedSensitiveIds++;
                    continue;
                }

                if (source.Length > maxSourceCharacters)
                {
                    skippedOversizeIds++;
                    continue;
                }

                bool freshExisting = existing._records.TryGetValue(entry.KeyHash, out TranslationRecord? prior)
                    && Hashing.FixedEqualsHex(prior.SourceSha256, Hashing.Sha256Text(source))
                    && ((prior.HintSha256 == null && hintSha256 == null)
                        || (prior.HintSha256 != null && hintSha256 != null && Hashing.FixedEqualsHex(prior.HintSha256, hintSha256)));
                if (!includeAlreadyTranslated)
                {
                    if (reviewQueue)
                    {
                        bool requiresEscalation = freshExisting
                            && prior!.Status.Equals("draft", StringComparison.OrdinalIgnoreCase)
                            && (prior.NeedsReview
                                || !string.Equals(prior.Confidence, "high", StringComparison.Ordinal)
                                || prior.RiskFlags.Contains("long_text", StringComparer.Ordinal)
                                || prior.RiskFlags.Contains("context_required", StringComparer.Ordinal));
                        if (!requiresEscalation) continue;
                    }
                    else if (freshExisting)
                    {
                        continue;
                    }
                }

                string groupKey = source + "\u001F" + ukrainianHint + (deduplicate ? string.Empty : "\u001F" + entry.Id);
                if (!groups.TryGetValue(groupKey, out PendingJob? pending))
                {
                    pending = new PendingJob(source, ukEntry?.Value);
                    groups.Add(groupKey, pending);
                    englishCharacters += source.Length;
                    hintCharacters += ukrainianHint.Length;
                }

                pending.Ids.Add(entry.Id);
                sourceIds++;
            }

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, StrictUtf8);
            foreach (PendingJob pending in groups.Values)
            {
                string sourceHash = Hashing.Sha256Text(pending.English);
                string? hintHash = pending.UkrainianHint == null ? null : Hashing.Sha256Text(pending.UkrainianHint);
                string[] riskFlags = TranslationValidator.ClassifyRisks(pending.English)
                    .Concat(pending.Ids.Count > 1 ? new[] { "context_required" } : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var job = new TranslationJob
                {
                    ContentGuid = english.ContentGuid,
                    ContentVersion = english.ContentVersion,
                    Ids = pending.Ids.ToArray(),
                    SourceSha256 = sourceHash,
                    HintSha256 = hintHash,
                    English = pending.English,
                    UkrainianHint = pending.UkrainianHint,
                    ProtectedTokens = TranslationValidator.ExtractProtectedTokens(pending.English).ToArray(),
                    RiskFlags = riskFlags,
                    Deduplicated = pending.Ids.Count > 1
                };
                job.JobId = ComputeJobId(job);
                writer.WriteLine(JsonSerializer.Serialize(job, JsonOptions));
            }

            return new JobExportSummary(groups.Count, sourceIds, englishCharacters, hintCharacters, skippedSensitiveIds, skippedOversizeIds);
        }

        public static void WriteJsonLines(string path, IEnumerable<TranslationRecord> records)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, StrictUtf8);
            foreach (TranslationRecord record in records.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            }
        }

        public static ImportSummary ImportDraftResults(
            Loc1Document english,
            string jobsPath,
            string resultsPath,
            TranslationCatalog existing,
            string outputPath,
            bool allowPartial = false)
        {
            Dictionary<ulong, Loc1Entry> sourceByHash = english.IndexByHash();
            Dictionary<string, TranslationJob> jobs = ReadUniqueJsonLines<TranslationJob>(jobsPath, job => job.JobId, "job");
            Dictionary<string, TranslationResult> results = ReadUniqueJsonLines<TranslationResult>(resultsPath, result => result.JobId, "result");
            var merged = existing._records.ToDictionary(pair => pair.Key, pair => pair.Value);
            int importedIds = 0;
            int preservedReviewed = 0;
            int needsReview = 0;
            var errors = new List<string>();
            var ownedIds = new HashSet<ulong>();

            foreach (TranslationJob job in jobs.Values)
            {
                if (job.Ids == null || job.ProtectedTokens == null || job.RiskFlags == null)
                {
                    errors.Add($"{job.JobId}: job arrays cannot be null.");
                    continue;
                }

                bool hasMvpMetadata = job.MvpCategory != null || job.MvpRank != null || job.MvpScope != null;
                if (hasMvpMetadata
                    && (string.IsNullOrWhiteSpace(job.MvpCategory)
                        || !IsSafeWorkflowIdentifier(job.MvpCategory)
                        || !job.MvpRank.HasValue
                        || job.MvpRank.Value <= 0
                        || string.IsNullOrWhiteSpace(job.MvpScope)
                        || !IsSafeWorkflowIdentifier(job.MvpScope)))
                {
                    errors.Add($"{job.JobId}: optional MVP category, rank, and scope metadata must be supplied together and use safe identifiers.");
                    continue;
                }

                if (job.Schema != 1
                    || !string.Equals(job.ContentGuid, english.ContentGuid, StringComparison.Ordinal)
                    || !string.Equals(job.ContentVersion, english.ContentVersion, StringComparison.Ordinal))
                {
                    errors.Add($"{job.JobId}: job corpus identity does not match the current English LOC1.");
                    continue;
                }

                if (job.Ids.Length == 0
                    || !Hashing.FixedEqualsHex(job.SourceSha256, Hashing.Sha256Text(job.English))
                    || (job.UkrainianHint == null && job.HintSha256 != null)
                    || (job.UkrainianHint != null && (job.HintSha256 == null || !Hashing.FixedEqualsHex(job.HintSha256, Hashing.Sha256Text(job.UkrainianHint))))
                    || !string.Equals(job.JobId, ComputeJobId(job), StringComparison.Ordinal))
                {
                    errors.Add($"{job.JobId}: job hash, hint, or canonical id is invalid.");
                    continue;
                }

                string[] actualProtectedTokens = TranslationValidator.ExtractProtectedTokens(job.English).ToArray();
                if (!actualProtectedTokens.SequenceEqual(job.ProtectedTokens, StringComparer.Ordinal))
                {
                    errors.Add($"{job.JobId}: protected_tokens does not match the English source.");
                }

                string[] expectedRisks = TranslationValidator.ClassifyRisks(job.English)
                    .Concat(job.Ids.Length > 1 ? new[] { "context_required" } : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (job.Deduplicated != (job.Ids.Length > 1)
                    || !expectedRisks.SequenceEqual(job.RiskFlags.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    errors.Add($"{job.JobId}: risk_flags or deduplication metadata is not derived from the bound source ids.");
                }

                foreach (string idText in job.Ids)
                {
                    if (!TryParseId(idText, out ulong id)
                        || !ownedIds.Add(id)
                        || !sourceByHash.TryGetValue(id, out Loc1Entry? source)
                        || source.Value == null
                        || !string.Equals(source.Value, job.English, StringComparison.Ordinal)
                        || !Hashing.FixedEqualsHex(job.SourceSha256, Hashing.Sha256Text(source.Value)))
                    {
                        errors.Add($"{job.JobId}: id {idText} is duplicate or is not exactly bound to this source.");
                    }
                }
            }

            int missingResultJobs = jobs.Keys.Count(jobId => !results.ContainsKey(jobId));
            if (!allowPartial && missingResultJobs > 0)
            {
                errors.Add($"Results are incomplete: {missingResultJobs} job(s) have no result. Import each deterministic chunk as a complete unit.");
            }

            foreach (TranslationResult result in results.Values)
            {
                if (result.IssueCodes == null)
                {
                    errors.Add($"{result.JobId}: issue_codes cannot be null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(result.Model) || string.IsNullOrWhiteSpace(result.PromptVersion))
                {
                    errors.Add($"{result.JobId}: model and prompt_version provenance are required.");
                    continue;
                }

                if (result.Confidence != "high" && result.Confidence != "medium" && result.Confidence != "low")
                {
                    errors.Add($"{result.JobId}: confidence must be high, medium, or low.");
                    continue;
                }

                if (result.IssueCodes.Any(code => string.IsNullOrWhiteSpace(code)
                    || code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_' || character == '-'))))
                {
                    errors.Add($"{result.JobId}: issue_codes may contain only non-empty ASCII identifiers.");
                    continue;
                }

                if (!jobs.TryGetValue(result.JobId, out TranslationJob? job))
                {
                    errors.Add($"Unknown job_id in results: {result.JobId}");
                    continue;
                }

                var pairIssues = new List<TranslationIssue>();
                TranslationValidator.ValidatePair(result.JobId, job.English, result.Translation, pairIssues);
                foreach (TranslationIssue issue in pairIssues.Where(issue => issue.Severity == ValidationSeverity.Error))
                {
                    errors.Add($"{result.JobId}: {issue.Code}: {issue.Message}");
                }

                if (pairIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
                {
                    continue;
                }

                foreach (string idText in job.Ids)
                {
                    if (!TryParseId(idText, out ulong id) || !sourceByHash.TryGetValue(id, out Loc1Entry? source) || source.Value == null)
                    {
                        errors.Add($"{result.JobId}: source id {idText} is absent from current English LOC1.");
                        continue;
                    }

                    if (!string.Equals(source.Value, job.English, StringComparison.Ordinal)
                        || !Hashing.FixedEqualsHex(job.SourceSha256, Hashing.Sha256Text(source.Value)))
                    {
                        errors.Add($"{result.JobId}: source changed for id {idText}; regenerate jobs.");
                        continue;
                    }

                    if (merged.TryGetValue(id, out TranslationRecord? previous)
                        && (previous.Status.Equals("reviewed", StringComparison.OrdinalIgnoreCase)
                            || previous.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                        && Hashing.FixedEqualsHex(previous.SourceSha256, job.SourceSha256)
                        && ((previous.HintSha256 == null && job.HintSha256 == null)
                            || (previous.HintSha256 != null && job.HintSha256 != null && Hashing.FixedEqualsHex(previous.HintSha256, job.HintSha256))))
                    {
                        preservedReviewed++;
                        continue;
                    }

                    bool flaggedForReview = result.NeedsReview
                        || !string.Equals(result.Confidence, "high", StringComparison.OrdinalIgnoreCase)
                        || job.RiskFlags.Contains("context_required", StringComparer.Ordinal)
                        || job.RiskFlags.Contains("long_text", StringComparer.Ordinal);
                    if (flaggedForReview)
                    {
                        needsReview++;
                    }

                    merged[id] = new TranslationRecord
                    {
                        Id = id.ToString("X16", CultureInfo.InvariantCulture),
                        SourceSha256 = Hashing.Sha256Text(source.Value),
                        HintSha256 = job.HintSha256,
                        Translation = result.Translation,
                        Status = "draft",
                        Model = result.Model,
                        PromptVersion = result.PromptVersion,
                        Confidence = result.Confidence,
                        NeedsReview = flaggedForReview,
                        IssueCodes = result.IssueCodes.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        RiskFlags = job.RiskFlags.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                        ReviewStage = result.Model.Contains("sol", StringComparison.OrdinalIgnoreCase) ? "sol_review" : "terra_done",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    importedIds++;
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidDataException("Result import refused:\n" + string.Join("\n", errors.Take(100)));
            }

            WriteJsonLinesAtomically(outputPath, merged.Values);
            return new ImportSummary(results.Count, importedIds, preservedReviewed, needsReview, missingResultJobs);
        }

        private static Dictionary<string, T> ReadUniqueJsonLines<T>(string path, Func<T, string> keySelector, string label) where T : class
        {
            var records = new Dictionary<string, T>(StringComparer.Ordinal);
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path, StrictUtf8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                T? item;
                try
                {
                    item = JsonSerializer.Deserialize<T>(line, JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException($"Invalid {label} JSONL at line {lineNumber}: {exception.Message}", exception);
                }

                if (item == null || string.IsNullOrWhiteSpace(keySelector(item)) || !records.TryAdd(keySelector(item), item))
                {
                    throw new InvalidDataException($"Missing or duplicate {label} key at line {lineNumber}.");
                }
            }

            return records;
        }

        private static void WriteJsonLinesAtomically(string path, IEnumerable<TranslationRecord> records)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new IOException($"Import output already exists; choose a new path: {fullPath}");
            }

            string directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                WriteJsonLines(tempPath, records);
                File.Move(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        public static string ComputeJobId(TranslationJob job)
        {
            string identity = string.Join("\n", new[]
            {
                job.Schema.ToString(CultureInfo.InvariantCulture),
                job.ContentGuid,
                job.ContentVersion,
                job.SourceSha256.ToUpperInvariant(),
                job.HintSha256?.ToUpperInvariant() ?? "-",
                string.Join(",", job.Ids),
                string.Join("\u001F", job.ProtectedTokens),
                string.Join(",", job.RiskFlags.OrderBy(value => value, StringComparer.Ordinal)),
                job.Deduplicated ? "1" : "0"
            });
            return "ru-" + Hashing.Sha256Text(identity).Substring(0, 24).ToLowerInvariant();
        }

        private static bool TryParseId(string? value, out ulong id)
        {
            id = 0;
            return value != null && value.Length == 16 && ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
        }

        private static bool IsHex(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        private static bool IsSafeWorkflowIdentifier(string value)
        {
            return value.Length <= 100 && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character == '_'
                || character == '-'
                || character == '.');
        }

        private sealed class PendingJob
        {
            public PendingJob(string english, string? ukrainianHint)
            {
                English = english;
                UkrainianHint = ukrainianHint;
            }

            public string English { get; }
            public string? UkrainianHint { get; }
            public List<string> Ids { get; } = new List<string>();
        }
    }

    public sealed class JobExportSummary
    {
        public JobExportSummary(
            int itemCount,
            int sourceIdCount,
            long englishCharacters,
            long hintCharacters,
            int skippedSensitiveIds,
            int skippedOversizeIds)
        {
            ItemCount = itemCount;
            SourceIdCount = sourceIdCount;
            EnglishCharacters = englishCharacters;
            HintCharacters = hintCharacters;
            SkippedSensitiveIds = skippedSensitiveIds;
            SkippedOversizeIds = skippedOversizeIds;
        }

        public int ItemCount { get; }
        public int SourceIdCount { get; }
        public long EnglishCharacters { get; }
        public long HintCharacters { get; }
        public int SkippedSensitiveIds { get; }
        public int SkippedOversizeIds { get; }
        public long ApproximateInputTokens => Math.Max(1, (EnglishCharacters + HintCharacters) / 3);
        public long ApproximateOutputTokens => Math.Max(1, EnglishCharacters / 2);
    }

    public sealed class ImportSummary
    {
        public ImportSummary(int resultJobs, int importedIds, int preservedReviewedIds, int needsReviewIds, int missingResultJobs)
        {
            ResultJobs = resultJobs;
            ImportedIds = importedIds;
            PreservedReviewedIds = preservedReviewedIds;
            NeedsReviewIds = needsReviewIds;
            MissingResultJobs = missingResultJobs;
        }

        public int ResultJobs { get; }
        public int ImportedIds { get; }
        public int PreservedReviewedIds { get; }
        public int NeedsReviewIds { get; }
        public int MissingResultJobs { get; }
    }
}
