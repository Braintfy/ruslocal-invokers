using System;
using System.Linq;

namespace InvokersRu.Core.Translations
{
    public static class RuntimeSafeDraftPolicy
    {
        public static bool IsEligible(TranslationRecord record, string source, string? ukrainianHint, out string reason)
        {
            if (!IsHintBindingCurrent(record, ukrainianHint))
            {
                reason = "hint-binding";
                return false;
            }

            if (!string.Equals(record.Status, "draft", StringComparison.Ordinal)
                || record.NeedsReview
                || !string.Equals(record.Confidence, "high", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(record.Translation))
            {
                reason = "draft-status-confidence-or-text";
                return false;
            }

            bool terra = string.Equals(record.Model, "gpt-5.6-terra", StringComparison.Ordinal)
                && (string.Equals(record.PromptVersion, "ru-v2", StringComparison.Ordinal)
                    || string.Equals(record.PromptVersion, "ru-runtime-v1", StringComparison.Ordinal))
                && string.Equals(record.ReviewStage, "terra_done", StringComparison.Ordinal);
            bool sol = string.Equals(record.Model, "gpt-5.6-sol", StringComparison.Ordinal)
                && string.Equals(record.PromptVersion, "ru-review-v1", StringComparison.Ordinal)
                && string.Equals(record.ReviewStage, "sol_review", StringComparison.Ordinal);
            if (!terra && !sol)
            {
                reason = "codex-provenance";
                return false;
            }

            string[] expectedRisks = TranslationValidator.ClassifyRisks(source).ToArray();
            bool hintRequiresContext = TranslationValidator.RequiresContextReview(source, ukrainianHint);
            bool recordedContextRequired = record.RiskFlags.Contains("context_required", StringComparer.Ordinal);
            if (!TranslationValidator.IsRiskMetadataCurrent(record, source, ukrainianHint)
                || expectedRisks.Any(TranslationValidator.IsSensitiveRisk))
            {
                reason = "risk-metadata-or-sensitive";
                return false;
            }

            if ((recordedContextRequired || hintRequiresContext) && !record.ScreenshotQa)
            {
                reason = "context-screenshot-qa";
                return false;
            }

            bool noIssues = record.IssueCodes != null && record.IssueCodes.Length == 0;
            bool resolvedSourceProblem = record.IssueCodes != null && record.IssueCodes.Length == 1
                && string.Equals(record.IssueCodes[0], "source_problem", StringComparison.Ordinal)
                && record.ScreenshotQa;
            if (!noIssues && !resolvedSourceProblem)
            {
                reason = "unresolved-issue";
                return false;
            }

            reason = "eligible";
            return true;
        }

        public static bool IsPreviewEligible(TranslationRecord record, string source, string? ukrainianHint, out string reason)
        {
            if (!IsHintBindingCurrent(record, ukrainianHint))
            {
                reason = "hint-binding";
                return false;
            }

            if (record.Status.Equals("draft", StringComparison.OrdinalIgnoreCase))
            {
                return IsEligible(record, source, ukrainianHint, out reason);
            }

            if (record.NeedsReview || string.IsNullOrWhiteSpace(record.Translation))
            {
                reason = "review-status-or-text";
                return false;
            }

            bool noIssues = record.IssueCodes != null && record.IssueCodes.Length == 0;
            bool resolvedSourceProblem = record.IssueCodes != null && record.IssueCodes.Length == 1
                && string.Equals(record.IssueCodes[0], "source_problem", StringComparison.Ordinal)
                && record.ScreenshotQa;
            if (!noIssues && !resolvedSourceProblem)
            {
                reason = "unresolved-issue";
                return false;
            }

            if (!TranslationValidator.IsRiskMetadataCurrent(record, source, ukrainianHint))
            {
                reason = "risk-metadata";
                return false;
            }

            if (TranslationValidator.ClassifyRisks(source).Any(TranslationValidator.IsSensitiveRisk))
            {
                string[] reviewers = (record.ReviewerIds ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (!record.LegalApproved || reviewers.Length < 2)
                {
                    reason = "sensitive-review-required";
                    return false;
                }
            }

            bool contextRequired = record.RiskFlags.Contains("context_required", StringComparer.Ordinal)
                || TranslationValidator.RequiresContextReview(source, ukrainianHint);
            if (contextRequired && !record.ScreenshotQa)
            {
                reason = "context-screenshot-qa";
                return false;
            }

            reason = "eligible-non-draft";
            return true;
        }

        private static bool IsHintBindingCurrent(TranslationRecord record, string? ukrainianHint)
        {
            if ((record.HintSha256 == null) != (ukrainianHint == null))
            {
                return false;
            }

            return record.HintSha256 == null
                || Hashing.FixedEqualsHex(record.HintSha256, Hashing.Sha256Text(ukrainianHint!));
        }
    }
}
