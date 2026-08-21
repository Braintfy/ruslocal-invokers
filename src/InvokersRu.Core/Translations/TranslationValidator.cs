using InvokersRu.Core.Loc1;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InvokersRu.Core.Translations
{
    public enum ValidationSeverity
    {
        Warning,
        Error
    }

    public enum ValidationProfile
    {
        Preview,
        Release
    }

    public sealed class TranslationIssue
    {
        public TranslationIssue(string id, string code, ValidationSeverity severity, string message)
        {
            Id = id;
            Code = code;
            Severity = severity;
            Message = message;
        }

        public string Id { get; }
        public string Code { get; }
        public ValidationSeverity Severity { get; }
        public string Message { get; }
    }

    public sealed class ValidationReport
    {
        public int CatalogRecords { get; set; }
        public int FreshRecords { get; set; }
        public int UsableRecords { get; set; }
        public int StaleRecords { get; set; }
        public int MissingSourceIds { get; set; }
        public int SourceRecords { get; set; }
        public int ApprovedRecords { get; set; }
        public int CoverageRequiredRecords { get; set; }
        public int CoverageMissingRecords { get; set; }
        public int SensitiveFallbackRecords { get; set; }
        public List<TranslationIssue> Issues { get; } = new List<TranslationIssue>();
        public int ErrorCount => Issues.Count(issue => issue.Severity == ValidationSeverity.Error);
        public int WarningCount => Issues.Count(issue => issue.Severity == ValidationSeverity.Warning);
    }

    public static class TranslationValidator
    {
        private static readonly Regex ProtectedTokenRegex = new Regex("https?://[^\\s<>\\\"']+|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}|\\{[^{}]+\\}|<[^<>]+>|\\\\[nrt]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex NumericTokenRegex = new Regex(@"[-+]?\d+(?:[.,]\d+)?%?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MechanicUnitRegex = new Regex(@"[-+]?\d+(?:[.,]\d+)?(?:ms|s|m|h|d|px|x|%)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RichTagRegex = new Regex(@"<\s*(/?)\s*([A-Za-z][A-Za-z0-9]*)\b[^<>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LegalPrivacyRegex = new Regex(@"\b(terms of (use|service)|privacy policy|privacy notice|personal data|data processing|consent|copyright|license agreement|legal notice)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex AccountPaymentRegex = new Regex(@"\b(payment|purchase|refund|subscription|billing|credit card|delete (your )?account|account deletion|real[ -]?money|in-app purchase)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> SelfClosingRichTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "br", "sprite" };

        public static ValidationReport Validate(
            Loc1Document english,
            TranslationCatalog catalog,
            bool includeDraft,
            Loc1Document? ukrainianHint = null,
            ValidationProfile profile = ValidationProfile.Preview,
            bool allowPerLocaleContentVersion = false)
        {
            if (ukrainianHint != null)
            {
                Loc1Compatibility.RequireComposableCorpus(english, ukrainianHint, allowPerLocaleContentVersion);
            }

            Dictionary<ulong, Loc1Entry> sourceByHash = english.IndexByHash();
            Dictionary<ulong, Loc1Entry>? hintByHash = ukrainianHint?.IndexByHash();
            var report = new ValidationReport
            {
                CatalogRecords = catalog.Count,
                SourceRecords = english.Entries.Count(entry => entry.Value != null)
            };
            var releaseReadyIds = new HashSet<ulong>();
            foreach (TranslationRecord record in catalog.Records)
            {
                if (!TryParseId(record.Id, out ulong id) || !sourceByHash.TryGetValue(id, out Loc1Entry? sourceEntry) || sourceEntry.Value == null)
                {
                    report.MissingSourceIds++;
                    report.Issues.Add(new TranslationIssue(record.Id, "unknown-id", ValidationSeverity.Error, "Translation id is absent from the current English package."));
                    continue;
                }

                string source = sourceEntry.Value;
                if (!Hashing.FixedEqualsHex(record.SourceSha256, Hashing.Sha256Text(source)))
                {
                    report.StaleRecords++;
                    // A stale record is never composed: TryGetUsable refuses it and the official English text
                    // is kept instead. That is a defect for a release, but a preview should still assemble the
                    // records that did survive the content update.
                    report.Issues.Add(new TranslationIssue(record.Id, "stale-source",
                        profile == ValidationProfile.Release ? ValidationSeverity.Error : ValidationSeverity.Warning,
                        "English source changed; this translation must be reviewed again."));
                    continue;
                }

                report.FreshRecords++;
                if (hintByHash != null && hintByHash.TryGetValue(id, out Loc1Entry? hintEntry))
                {
                    string? currentHintHash = hintEntry.Value == null ? null : Hashing.Sha256Text(hintEntry.Value);
                    if ((record.HintSha256 == null) != (currentHintHash == null)
                        || (record.HintSha256 != null && currentHintHash != null && !Hashing.FixedEqualsHex(record.HintSha256, currentHintHash)))
                    {
                        report.Issues.Add(new TranslationIssue(record.Id, "stale-hint",
                            profile == ValidationProfile.Release ? ValidationSeverity.Error : ValidationSeverity.Warning,
                            "Ukrainian context hint changed; review this translation again."));
                        continue;
                    }
                }

                int errorsBefore = report.ErrorCount;
                ValidatePair(record.Id, source, record.Translation, report.Issues);
                if (profile == ValidationProfile.Release && record.IssueCodes.Length != 0)
                {
                    report.Issues.Add(new TranslationIssue(
                        record.Id,
                        "unresolved-issue-codes",
                        ValidationSeverity.Error,
                        "Approved release text still carries unresolved issue codes."));
                }
                string[] expectedSourceRisks = ClassifyRisks(source).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                string[] recordedSourceRisks = record.RiskFlags
                    .Where(value => !string.Equals(value, "context_required", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (!expectedSourceRisks.SequenceEqual(recordedSourceRisks, StringComparer.Ordinal))
                {
                    report.Issues.Add(new TranslationIssue(record.Id, "risk-metadata-mismatch", profile == ValidationProfile.Release ? ValidationSeverity.Error : ValidationSeverity.Warning, "Risk flags are not derived from the current source."));
                }
                if (catalog.TryGetUsable(id, source, includeDraft, out _, out _))
                {
                    report.UsableRecords++;
                }

                if (record.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                {
                    report.ApprovedRecords++;
                    if (profile == ValidationProfile.Release)
                    {
                        string[] reviewers = record.ReviewerIds
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value.Trim())
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        if (record.NeedsReview
                            || reviewers.Length != record.ReviewerIds.Length
                            || reviewers.Length == 0
                            || record.ReviewedAt == null
                            || string.IsNullOrWhiteSpace(record.ReviewRevision))
                        {
                            report.Issues.Add(new TranslationIssue(record.Id, "human-review-metadata", ValidationSeverity.Error, "Approved release text requires reviewer, timestamp, revision, and no pending review flag."));
                        }

                        if (record.RiskFlags.Contains("context_required", StringComparer.Ordinal) && !record.ScreenshotQa)
                        {
                            report.Issues.Add(new TranslationIssue(record.Id, "screenshot-qa-required", ValidationSeverity.Error, "Deduplicated/context-sensitive text needs screenshot QA before release."));
                        }

                        if (expectedSourceRisks.Any(IsSensitiveRisk) && (!record.LegalApproved || reviewers.Length < 2))
                        {
                            report.Issues.Add(new TranslationIssue(record.Id, "sensitive-review-required", ValidationSeverity.Error, "Legal, privacy, account, or payment text needs explicit approval and two reviewers."));
                        }

                        if (report.ErrorCount == errorsBefore)
                        {
                            releaseReadyIds.Add(id);
                        }
                    }
                }
            }

            if (profile == ValidationProfile.Release)
            {
                foreach (Loc1Entry entry in english.Entries.Where(entry => entry.Value != null))
                {
                    if (ClassifyRisks(entry.Value!).Any(IsSensitiveRisk))
                    {
                        report.SensitiveFallbackRecords++;
                        continue;
                    }

                    report.CoverageRequiredRecords++;
                    if (!releaseReadyIds.Contains(entry.KeyHash)) report.CoverageMissingRecords++;
                }

                if (report.CoverageMissingRecords > 0)
                {
                    report.Issues.Add(new TranslationIssue("*", "release-coverage", ValidationSeverity.Error, $"Release is missing {report.CoverageMissingRecords} approved, validated non-sensitive source ids."));
                }

                if (report.SensitiveFallbackRecords > 0)
                {
                    report.Issues.Add(new TranslationIssue("*", "sensitive-english-fallback", ValidationSeverity.Warning, $"{report.SensitiveFallbackRecords} sensitive source ids remain in official English until separately approved."));
                }
            }

            return report;
        }

        public static IReadOnlyList<string> ExtractProtectedTokens(string value)
        {
            return ProtectedTokenRegex.Matches(value).Cast<Match>().Select(match => match.Value).ToArray();
        }

        public static IReadOnlyList<string> ClassifyRisks(string source)
        {
            var flags = new List<string>();
            if (LegalPrivacyRegex.IsMatch(source)) flags.Add("legal_or_privacy");
            if (AccountPaymentRegex.IsMatch(source)) flags.Add("account_or_payment");
            if (source.Length > 1000) flags.Add("long_text");
            if (ProtectedTokenRegex.IsMatch(source)) flags.Add("protected_tokens");
            if (NumericTokenRegex.IsMatch(source)) flags.Add("numeric");
            if (source.Any(character => (char.IsControl(character) && character != '\r' && character != '\n' && character != '\t') || IsBidirectionalControl(character))) flags.Add("legacy_control");
            return flags;
        }

        public static bool IsSensitiveRisk(string risk)
        {
            return string.Equals(risk, "legal_or_privacy", StringComparison.Ordinal)
                || string.Equals(risk, "account_or_payment", StringComparison.Ordinal)
                || string.Equals(risk, "legacy_control", StringComparison.Ordinal);
        }

        /// <summary>
        /// Per-record release gate used by compatible-revision projection. Aggregate release validation
        /// may report harmless coverage/unknown/stale rows that deliberately fall back to English, so the
        /// writer needs this exact local predicate for the one fresh source+hint row it is considering.
        /// </summary>
        public static bool IsReleaseReady(TranslationRecord record, string source, out string reason)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(source);
            if (!record.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
            {
                reason = "status-not-approved";
                return false;
            }

            if (record.IssueCodes.Length != 0)
            {
                reason = "unresolved-issue-codes";
                return false;
            }

            var pairIssues = new List<TranslationIssue>();
            ValidatePair(record.Id, source, record.Translation, pairIssues);
            if (pairIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
            {
                reason = "pair-validation";
                return false;
            }

            string[] expectedRisks = ClassifyRisks(source).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] recordedRisks = record.RiskFlags
                .Where(value => !string.Equals(value, "context_required", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!expectedRisks.SequenceEqual(recordedRisks, StringComparer.Ordinal))
            {
                reason = "risk-metadata";
                return false;
            }

            string[] reviewers = record.ReviewerIds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (record.NeedsReview
                || reviewers.Length != record.ReviewerIds.Length
                || reviewers.Length == 0
                || record.ReviewedAt == null
                || string.IsNullOrWhiteSpace(record.ReviewRevision))
            {
                reason = "human-review-metadata";
                return false;
            }

            if (record.RiskFlags.Contains("context_required", StringComparer.Ordinal) && !record.ScreenshotQa)
            {
                reason = "screenshot-qa";
                return false;
            }

            if (expectedRisks.Any(IsSensitiveRisk) && (!record.LegalApproved || reviewers.Length < 2))
            {
                reason = "sensitive-review";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static void ValidatePair(string id, string source, string translation, ICollection<TranslationIssue> issues)
        {
            if (!string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(translation))
            {
                issues.Add(new TranslationIssue(id, "empty-translation", ValidationSeverity.Error, "Non-empty source has an empty translation."));
            }

            bool validUnicode = true;
            try
            {
                _ = new UTF8Encoding(false, true).GetByteCount(translation);
            }
            catch (EncoderFallbackException)
            {
                validUnicode = false;
                issues.Add(new TranslationIssue(id, "invalid-unicode", ValidationSeverity.Error, "Translation cannot be encoded as strict UTF-8."));
            }

            if (validUnicode && !translation.IsNormalized(NormalizationForm.FormC))
            {
                issues.Add(new TranslationIssue(id, "unicode-not-nfc", ValidationSeverity.Error, "Translation must use Unicode NFC normalization."));
            }

            if (translation.IndexOf('\0') >= 0)
            {
                issues.Add(new TranslationIssue(id, "nul-character", ValidationSeverity.Error, "Translation contains a NUL character."));
            }

            if (translation.Any(character => (char.IsControl(character) && character != '\r' && character != '\n' && character != '\t') || IsBidirectionalControl(character)))
            {
                issues.Add(new TranslationIssue(id, "unsafe-control-character", ValidationSeverity.Error, "Translation contains a forbidden control or bidirectional formatting character."));
            }

            string[] sourceTokens = ExtractProtectedTokens(source).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] targetTokens = ExtractProtectedTokens(translation).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!sourceTokens.SequenceEqual(targetTokens, StringComparer.Ordinal))
            {
                issues.Add(new TranslationIssue(id, "protected-token-mismatch", ValidationSeverity.Error, "Placeholders, rich-text tags, or escaped line breaks do not match the source."));
            }

            // The protected-token regex deliberately refuses to look inside braces, so a doubled brace
            // yields the very same token as the source: {{0}} and {0} both extract as {0}. The multiset
            // then matches while the game prints the braces literally. Counting the characters is what
            // separates the two, and a translation has no business adding or dropping a brace.
            if (source.Count(character => character == '{') != translation.Count(character => character == '{')
                || source.Count(character => character == '}') != translation.Count(character => character == '}'))
            {
                issues.Add(new TranslationIssue(id, "brace-count-mismatch", ValidationSeverity.Error, "Curly braces do not match the source; a placeholder is doubled or lost."));
            }

            string[] sourceNumbers = NumericTokenRegex.Matches(source).Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] targetNumbers = NumericTokenRegex.Matches(translation).Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!sourceNumbers.SequenceEqual(targetNumbers, StringComparer.Ordinal))
            {
                issues.Add(new TranslationIssue(id, "numeric-token-mismatch", ValidationSeverity.Error, "Literal numbers or percentages do not match the source."));
            }

            string[] sourceUnits = MechanicUnitRegex.Matches(source).Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            string[] targetUnits = MechanicUnitRegex.Matches(translation).Cast<Match>().Select(match => match.Value).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!sourceUnits.SequenceEqual(targetUnits, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new TranslationIssue(id, "mechanic-unit-mismatch", ValidationSeverity.Error, "Compact mechanic values or unit suffixes do not match the source."));
            }

            if (HasBalancedRichTags(source) && !HasBalancedRichTags(translation))
            {
                issues.Add(new TranslationIssue(id, "rich-text-nesting", ValidationSeverity.Error, "Rich-text tags have invalid nesting or closing order."));
            }

            int sourceNewlines = source.Count(value => value == '\n');
            int targetNewlines = translation.Count(value => value == '\n');
            if (sourceNewlines != targetNewlines)
            {
                issues.Add(new TranslationIssue(id, "newline-mismatch", ValidationSeverity.Error, "Literal newline count differs from the source."));
            }

            if (source.Length >= 8 && translation.Length > source.Length * 3)
            {
                issues.Add(new TranslationIssue(id, "length-expansion", ValidationSeverity.Warning, "Translation is more than three times longer than the source."));
            }

            if (ContainsLatinWord(source) && string.Equals(source, translation, StringComparison.Ordinal) && source.Any(char.IsLetter))
            {
                issues.Add(new TranslationIssue(id, "unchanged-source", ValidationSeverity.Warning, "Translation is identical to an English source string."));
            }
        }

        private static bool TryParseId(string value, out ulong id)
        {
            return ulong.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out id);
        }

        private static bool ContainsLatinWord(string value)
        {
            return Regex.IsMatch(value, "[A-Za-z]{2,}", RegexOptions.CultureInvariant);
        }

        private static bool HasBalancedRichTags(string value)
        {
            var stack = new Stack<string>();
            foreach (Match match in RichTagRegex.Matches(value))
            {
                string name = match.Groups[2].Value;
                bool closing = match.Groups[1].Value.Length != 0;
                if (closing)
                {
                    if (stack.Count == 0 || !string.Equals(stack.Pop(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else if (!SelfClosingRichTags.Contains(name) && !match.Value.EndsWith("/>", StringComparison.Ordinal))
                {
                    stack.Push(name);
                }
            }

            return stack.Count == 0;
        }

        private static bool IsBidirectionalControl(char value)
        {
            return (value >= '\u202A' && value <= '\u202E')
                || (value >= '\u2066' && value <= '\u2069')
                || (value >= '\u200B' && value <= '\u200F')
                || value == '\u2060'
                || value == '\uFEFF';
        }
    }
}
