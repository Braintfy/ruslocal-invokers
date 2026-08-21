using System;
using System.Text.Json.Serialization;

namespace InvokersRu.Core.Patching
{
    public enum InstallationStatus
    {
        CompatibleOriginal,
        PatchSupersededByOfficialUpdate,
        PatchSupersededByCatalogUpdate,
        PatchedByThisTool,
        UnknownBuild,
        MissingFiles,
        InconsistentState,
        RecoveryRequired
    }

    public sealed class InstallationInspection
    {
        public InstallationStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public string GameRoot { get; set; } = string.Empty;
        public CompatibleBuild? Build { get; set; }
        public string? EnglishSha256 { get; set; }
        public string? BaseSha256 { get; set; }
        public string? ManifestSha256 { get; set; }
        public string? DetectedContentGuid { get; set; }
        public string? DetectedContentVersion { get; set; }
        public bool GameFilesMatch { get; set; }
        public string? GameFileProblem { get; set; }
        public PatchState? State { get; set; }
        public PatchJournal? Journal { get; set; }
    }

    public sealed class PatchState
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("build_id")]
        public string BuildId { get; set; } = string.Empty;

        [JsonPropertyName("game_root")]
        public string GameRoot { get; set; } = string.Empty;

        [JsonPropertyName("target_path")]
        public string TargetPath { get; set; } = string.Empty;

        [JsonPropertyName("backup_path")]
        public string BackupPath { get; set; } = string.Empty;

        [JsonPropertyName("original_sha256")]
        public string OriginalSha256 { get; set; } = string.Empty;

        [JsonPropertyName("patched_sha256")]
        public string PatchedSha256 { get; set; } = string.Empty;

        [JsonPropertyName("translations_sha256")]
        public string TranslationsSha256 { get; set; } = string.Empty;

        [JsonPropertyName("applied_at")]
        public DateTimeOffset AppliedAt { get; set; }

        [JsonPropertyName("applied_translations")]
        public int AppliedTranslations { get; set; }
    }

    public sealed class PatchJournal
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("transaction_id")]
        public string TransactionId { get; set; } = string.Empty;

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = "Prepared";

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = "apply";

        [JsonPropertyName("build_id")]
        public string BuildId { get; set; } = string.Empty;

        [JsonPropertyName("game_root")]
        public string GameRoot { get; set; } = string.Empty;

        [JsonPropertyName("target_path")]
        public string TargetPath { get; set; } = string.Empty;

        [JsonPropertyName("backup_path")]
        public string BackupPath { get; set; } = string.Empty;

        [JsonPropertyName("quarantine_path")]
        public string QuarantinePath { get; set; } = string.Empty;

        [JsonPropertyName("rollback_path")]
        public string RollbackPath { get; set; } = string.Empty;

        [JsonPropertyName("displaced_sha256")]
        public string? DisplacedSha256 { get; set; }

        [JsonPropertyName("source_sha256")]
        public string SourceSha256 { get; set; } = string.Empty;

        [JsonPropertyName("expected_output_sha256")]
        public string ExpectedOutputSha256 { get; set; } = string.Empty;

        [JsonPropertyName("translations_sha256")]
        public string TranslationsSha256 { get; set; } = string.Empty;

        [JsonPropertyName("applied_translations")]
        public int AppliedTranslations { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
