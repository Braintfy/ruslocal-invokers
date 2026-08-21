using System;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Translations
{
    public sealed class TranslationRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("source_sha256")]
        public string SourceSha256 { get; set; } = string.Empty;

        [JsonPropertyName("hint_sha256")]
        public string? HintSha256 { get; set; }

        [JsonPropertyName("translation")]
        public string Translation { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "draft";

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("prompt_version")]
        public string? PromptVersion { get; set; }

        [JsonPropertyName("confidence")]
        public string? Confidence { get; set; }

        [JsonPropertyName("needs_review")]
        public bool NeedsReview { get; set; }

        [JsonPropertyName("issue_codes")]
        public string[] IssueCodes { get; set; } = Array.Empty<string>();

        [JsonPropertyName("risk_flags")]
        public string[] RiskFlags { get; set; } = Array.Empty<string>();

        [JsonPropertyName("review_stage")]
        public string? ReviewStage { get; set; }

        [JsonPropertyName("reviewer_ids")]
        public string[] ReviewerIds { get; set; } = Array.Empty<string>();

        [JsonPropertyName("reviewed_at")]
        public DateTimeOffset? ReviewedAt { get; set; }

        [JsonPropertyName("review_revision")]
        public string? ReviewRevision { get; set; }

        [JsonPropertyName("screenshot_qa")]
        public bool ScreenshotQa { get; set; }

        [JsonPropertyName("legal_approved")]
        public bool LegalApproved { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    public sealed class TranslationJob
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("job_id")]
        public string JobId { get; set; } = string.Empty;

        [JsonPropertyName("content_guid")]
        public string ContentGuid { get; set; } = string.Empty;

        [JsonPropertyName("content_version")]
        public string ContentVersion { get; set; } = string.Empty;

        [JsonPropertyName("ids")]
        public string[] Ids { get; set; } = Array.Empty<string>();

        [JsonPropertyName("source_sha256")]
        public string SourceSha256 { get; set; } = string.Empty;

        [JsonPropertyName("hint_sha256")]
        public string? HintSha256 { get; set; }

        [JsonPropertyName("english")]
        public string English { get; set; } = string.Empty;

        [JsonPropertyName("ukrainian_hint")]
        public string? UkrainianHint { get; set; }

        [JsonPropertyName("protected_tokens")]
        public string[] ProtectedTokens { get; set; } = Array.Empty<string>();

        [JsonPropertyName("risk_flags")]
        public string[] RiskFlags { get; set; } = Array.Empty<string>();

        [JsonPropertyName("deduplicated")]
        public bool Deduplicated { get; set; }

        [JsonPropertyName("mvp_category")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MvpCategory { get; set; }

        [JsonPropertyName("mvp_rank")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MvpRank { get; set; }

        [JsonPropertyName("mvp_scope")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MvpScope { get; set; }
    }

    public sealed class TranslationResult
    {
        [JsonRequired]
        [JsonPropertyName("job_id")]
        public string JobId { get; set; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("translation")]
        public string Translation { get; set; } = string.Empty;

        [JsonRequired]
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonRequired]
        [JsonPropertyName("prompt_version")]
        public string? PromptVersion { get; set; }

        [JsonRequired]
        [JsonPropertyName("confidence")]
        public string? Confidence { get; set; }

        [JsonRequired]
        [JsonPropertyName("needs_review")]
        public bool NeedsReview { get; set; }

        [JsonRequired]
        [JsonPropertyName("issue_codes")]
        public string[] IssueCodes { get; set; } = Array.Empty<string>();
    }
}
