using System;
using System.Linq;

namespace InvokersRu.Core.Translations
{
    public static class RuntimeSafeDraftPolicy
    {
        public static bool IsEligible(TranslationRecord record, string source, out string reason)
        {
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

            string[] expectedRisks = TranslationValidator.ClassifyRisks(source)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] recordedRisks = record.RiskFlags
                .Where(value => !string.Equals(value, "context_required", StringComparison.Ordinal))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            bool validRiskArray = record.RiskFlags.All(value => !string.IsNullOrWhiteSpace(value))
                && record.RiskFlags.Distinct(StringComparer.Ordinal).Count() == record.RiskFlags.Length
                && expectedRisks.SequenceEqual(recordedRisks, StringComparer.Ordinal);
            if (!validRiskArray || expectedRisks.Any(TranslationValidator.IsSensitiveRisk))
            {
                reason = "risk-metadata-or-sensitive";
                return false;
            }

            if (record.RiskFlags.Contains("context_required", StringComparer.Ordinal) && !record.ScreenshotQa)
            {
                reason = "context-screenshot-qa";
                return false;
            }

            bool noIssues = record.IssueCodes.Length == 0;
            bool resolvedSourceProblem = record.IssueCodes.Length == 1
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
    }
}
