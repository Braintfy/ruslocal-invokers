using InvokersRu.Core.Loc1;
using System;
using System.Collections.Generic;

namespace InvokersRu.Core.Translations
{
    public sealed class CompositionSummary
    {
        public int AppliedTranslations { get; set; }
        public int EnglishFallbacks { get; set; }
        public int BaseFallbacks { get; set; }
        public int MissingCatalogRecords { get; set; }
        public int StaleCatalogRecords { get; set; }
        public int RejectedCatalogRecords { get; set; }
        public int NeedsReviewFallbacks { get; set; }
        public int PolicyFallbacks { get; set; }
    }

    public static class TranslationComposer
    {
        public static CompositionSummary Apply(
            Loc1Document english,
            Loc1Document baseLocale,
            TranslationCatalog catalog,
            bool includeDraft,
            bool approvedOnly = false,
            bool excludeNeedsReview = false,
            bool allowPerLocaleContentVersion = false,
            Func<TranslationRecord, string, string?, bool>? eligibility = null)
        {
            Loc1Compatibility.RequireComposableCorpus(english, baseLocale, allowPerLocaleContentVersion);

            Dictionary<ulong, Loc1Entry> englishByHash = english.IndexByHash();
            var summary = new CompositionSummary();
            foreach (Loc1Entry target in baseLocale.Entries)
            {
                string? contextHint = target.Value;
                if (!englishByHash.TryGetValue(target.KeyHash, out Loc1Entry? source) || source.Value == null)
                {
                    summary.BaseFallbacks++;
                    continue;
                }

                if (catalog.TryGetUsable(target.KeyHash, source.Value, includeDraft, out TranslationRecord? record, out string reason, approvedOnly))
                {
                    if (excludeNeedsReview && record!.NeedsReview)
                    {
                        summary.NeedsReviewFallbacks++;
                        target.Value = source.Value;
                        summary.EnglishFallbacks++;
                        continue;
                    }

                    if (eligibility != null && !eligibility(record!, source.Value, contextHint))
                    {
                        summary.PolicyFallbacks++;
                        target.Value = source.Value;
                        summary.EnglishFallbacks++;
                        continue;
                    }

                    var pairIssues = new List<TranslationIssue>();
                    TranslationValidator.ValidatePair(target.Id, source.Value, record!.Translation, pairIssues);
                    if (pairIssues.Exists(issue => issue.Severity == ValidationSeverity.Error))
                    {
                        summary.RejectedCatalogRecords++;
                        target.Value = source.Value;
                        summary.EnglishFallbacks++;
                    }
                    else
                    {
                        target.Value = record.Translation;
                        summary.AppliedTranslations++;
                    }
                }
                else
                {
                    if (reason == "missing")
                    {
                        summary.MissingCatalogRecords++;
                    }
                    else if (reason == "stale-source")
                    {
                        summary.StaleCatalogRecords++;
                    }
                    else
                    {
                        summary.RejectedCatalogRecords++;
                    }

                    target.Value = source.Value;
                    summary.EnglishFallbacks++;
                }
            }

            return summary;
        }
    }
}
