using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvokersRu.Gui;

internal sealed class CliPlanResult
{
    private static readonly HashSet<string> KnownStatuses = new(StringComparer.Ordinal)
    {
        "CompatibleOriginal",
        "PatchSupersededByOfficialUpdate",
        "PatchSupersededByCatalogUpdate",
        "PatchedByThisTool",
        "UnknownBuild",
        "MissingFiles",
        "InconsistentState",
        "RecoveryRequired"
    };

    private static readonly HashSet<string> KnownPlanActions = new(StringComparer.Ordinal)
    {
        "READY_TO_APPLY",
        "READY_TO_REAPPLY_AFTER_GAME_UPDATE",
        "NOOP_OR_RESTORE",
        "RECOVERY_REQUIRED",
        "REFUSE_CLOSE_GAME_AND_LAUNCHER",
        "REFUSE_DEV_WRITES_DISABLED",
        "REFUSE_NO_TRUSTED_CACHE_RELEASE_PROFILE",
        "REFUSE_MISSING_OR_MISMATCHED_CATALOG",
        "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT",
        "READY_TO_UPDATE_TRANSLATION",
        "REFUSE_UNKNOWN_OR_INCONSISTENT"
    };

    private static readonly HashSet<string> KnownTranslationPolicies = new(StringComparer.Ordinal)
    {
        "release-approved",
        "supervised-safe-drafts",
        "community-preview-all-drafts"
    };

    private static readonly HashSet<string> KnownProfileModes = new(StringComparer.Ordinal)
    {
        "exact",
        "compatible-revision"
    };

    private static readonly HashSet<string> KnownTranslationUpdateKinds = new(StringComparer.Ordinal)
    {
        "none", "content", "metadata-only"
    };

    private static readonly HashSet<string> KnownDiagnosticKinds = new(StringComparer.Ordinal)
    {
        "none", "compatible-coverage", "translation-data", "structural-boundary", "local-state"
    };

    private static readonly HashSet<string> KnownDiagnosticComponents = new(StringComparer.Ordinal)
    {
        "none", "source-hint-coverage", "catalog-sha256", "english-source", "ukrainian-base",
        "version-stamp", "official-base-refresh", "loc1-schema", "content-guid", "locale-slot",
        "ordered-keyset", "missing-files", "patch-state", "journal", "journal-authentication"
    };

    private static readonly HashSet<string> KnownLocalProblems = new(StringComparer.Ordinal)
    {
        "journal-authentication"
    };

    private static readonly HashSet<string> KnownJournalPhases = new(StringComparer.Ordinal)
    {
        "Prepared",
        "BackupVerified",
        "StagedVerified",
        "PreCommitVerified",
        "ReplacementCommitted",
        "DisplacedVerified",
        "PostCommitVerified",
        "StateCommitted",
        "UnexpectedPreimageDetected",
        "UnexpectedPreimageRestored",
        "Completed",
        "Aborted"
    };

    [JsonPropertyName("schema")]
    [JsonRequired]
    public int Schema { get; set; }

    [JsonPropertyName("patcher_version")]
    [JsonRequired]
    public string PatcherVersion { get; set; } = string.Empty;

    [JsonPropertyName("installation_writes_enabled")]
    [JsonRequired]
    public bool InstallationWritesEnabled { get; set; }

    [JsonPropertyName("status")]
    [JsonRequired]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    [JsonRequired]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("cache_root")]
    [JsonRequired]
    public string CacheRoot { get; set; } = string.Empty;

    [JsonPropertyName("observed")]
    [JsonRequired]
    public ObservedCacheIdentity Observed { get; set; } = new();

    [JsonPropertyName("catalog")]
    [JsonRequired]
    public BundledCatalogInfo Catalog { get; set; } = new();

    [JsonPropertyName("profile")]
    [JsonRequired]
    public EmbeddedProfileInfo Profile { get; set; } = new();

    [JsonPropertyName("diagnostic")]
    [JsonRequired]
    public RuntimePlanDiagnostic Diagnostic { get; set; } = new();

    [JsonPropertyName("local_problem")]
    [JsonRequired]
    public string? LocalProblem { get; set; }

    [JsonPropertyName("update")]
    [JsonRequired]
    public SignedPlanUpdateInfo? Update { get; set; }

    [JsonPropertyName("channel_authority")]
    [JsonRequired]
    public SignedPlanUpdateInfo? ChannelAuthority { get; set; }

    [JsonPropertyName("update_problem")]
    [JsonRequired]
    public string? UpdateProblem { get; set; }

    [JsonPropertyName("translation_update_available")]
    [JsonRequired]
    public bool TranslationUpdateAvailable { get; set; }

    [JsonPropertyName("translation_update_kind")]
    [JsonRequired]
    public string TranslationUpdateKind { get; set; } = string.Empty;

    [JsonPropertyName("update_problem_blocks_apply")]
    [JsonRequired]
    public bool UpdateProblemBlocksApply { get; set; }

    [JsonPropertyName("restore_recovery_authorized")]
    [JsonRequired]
    public bool RestoreRecoveryAuthorized { get; set; }

    [JsonPropertyName("state")]
    [JsonRequired]
    public InstalledPatchState? State { get; set; }

    [JsonPropertyName("journal")]
    [JsonRequired]
    public InterruptedPatchJournal? Journal { get; set; }

    [JsonPropertyName("process_conflicts")]
    [JsonRequired]
    public string[] ProcessConflicts { get; set; } = Array.Empty<string>();

    [JsonPropertyName("plan")]
    [JsonRequired]
    public string? PlanAction { get; set; }

    [JsonPropertyName("can_apply")]
    [JsonRequired]
    public bool CanApply { get; set; }

    [JsonPropertyName("can_restore")]
    [JsonRequired]
    public bool CanRestore { get; set; }

    [JsonPropertyName("can_recover")]
    [JsonRequired]
    public bool CanRecover { get; set; }

    [JsonIgnore]
    public int ExitCode { get; private set; }

    [JsonIgnore]
    public string RawOutput { get; private set; } = string.Empty;

    [JsonIgnore]
    public bool IsPatched => string.Equals(Status, "PatchedByThisTool", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsVersionRisk => string.Equals(Status, "UnknownBuild", StringComparison.Ordinal)
        || string.Equals(Status, "InconsistentState", StringComparison.Ordinal)
        || string.Equals(Status, "MissingFiles", StringComparison.Ordinal)
        || string.Equals(PlanAction, "REFUSE_UNKNOWN_OR_INCONSISTENT", StringComparison.Ordinal)
        || string.Equals(PlanAction, "REFUSE_NO_TRUSTED_CACHE_RELEASE_PROFILE", StringComparison.Ordinal);

    public static CliPlanResult Parse(CliCommandResult command)
    {
        string json = command.StandardOutput.Trim();
        if (json.Length == 0)
        {
            throw new InvalidDataException(command.StandardError.Trim().Length == 0
                ? $"Проверяющий модуль завершился с кодом {command.ExitCode} без ответа."
                : command.StandardError.Trim());
        }

        CliPlanResult? result;
        try
        {
            result = JsonSerializer.Deserialize<CliPlanResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false,
                RespectNullableAnnotations = true
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Проверяющий модуль вернул ответ неизвестного формата.", exception);
        }

        if (result is null)
        {
            throw new InvalidDataException("Проверяющий модуль вернул пустой JSON-ответ.");
        }

        result.ExitCode = command.ExitCode;
        result.RawOutput = command.CombinedOutput;
        result.ValidateContract();
        return result;
    }

    internal static string? ExtractUpdateRefreshWarning(CliCommandResult command)
    {
        if (string.IsNullOrWhiteSpace(command.StandardOutput))
            return command.ExitCode == 0 ? null : "Проверка канала GitHub не вернула диагностический ответ.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(command.StandardOutput);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                return NormalizeRefreshWarning(error.GetString()!);
            if (root.TryGetProperty("network_status", out JsonElement status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "failed", StringComparison.Ordinal))
                return "Не удалось подтвердить свежесть данных GitHub; используется уже проверенная локальная или встроенная копия.";
            return null;
        }
        catch (JsonException)
        {
            return command.ExitCode == 0 ? null : "Проверка канала GitHub вернула ответ неизвестного формата.";
        }
    }

    private static string NormalizeRefreshWarning(string value)
    {
        string normalized = value.Trim();
        return normalized.Length <= 4096
            ? normalized
            : "Проверка канала GitHub вернула слишком длинное диагностическое сообщение; используется уже проверенная локальная или встроенная копия.";
    }

    private void ValidateContract()
    {
        Require(Schema == 3, "неподдерживаемая версия JSON-контракта");
        Require(!string.IsNullOrWhiteSpace(PatcherVersion) && PatcherVersion.Length <= 64
            && Version.TryParse(PatcherVersion, out _), "некорректная версия патчера");
        Require(KnownStatuses.Contains(Status), "неизвестное состояние установки");
        Require(!string.IsNullOrWhiteSpace(Message) && Message.Length <= 4096, "отсутствует сообщение проверки");
        Require(!string.IsNullOrWhiteSpace(CacheRoot) && CacheRoot.Length <= 1024
            && Path.IsPathFullyQualified(CacheRoot), "некорректный путь к кэшу игры");
        ObservedCacheIdentity observed = Observed
            ?? throw Contradiction("отсутствует наблюдаемая версия файлов");
        BundledCatalogInfo catalog = Catalog
            ?? throw Contradiction("отсутствует состояние каталога перевода");
        EmbeddedProfileInfo profile = Profile
            ?? throw Contradiction("отсутствует профиль совместимости");
        RuntimePlanDiagnostic diagnostic = Diagnostic
            ?? throw Contradiction("отсутствует типизированная диагностика совместимости");
        string[] processConflicts = ProcessConflicts
            ?? throw Contradiction("отсутствует список запущенных процессов");
        string planAction = PlanAction
            ?? throw Contradiction("отсутствует решение плана");
        Require(!string.IsNullOrWhiteSpace(planAction) && KnownPlanActions.Contains(planAction), "неизвестное решение плана");

        ValidateObserved(observed, profile);
        Require(KnownTranslationUpdateKinds.Contains(TranslationUpdateKind),
            "неизвестный вид обновления перевода");
        Require(TranslationUpdateAvailable == (TranslationUpdateKind != "none"),
            "вид обновления перевода противоречит признаку доступности");
        ValidateProfile(profile);
        ValidateCatalog(catalog, profile);
        Require(LocalProblem == null || KnownLocalProblems.Contains(LocalProblem),
            "неизвестная локальная причина отказа");
        Require(LocalProblem != "journal-authentication"
                || Status == "InconsistentState" && Journal == null && !RestoreRecoveryAuthorized,
            "причина аутентификации журнала противоречит состоянию установки");
        ValidateUpdate();
        ValidateStateAndJournal();
        ValidateDiagnostic(diagnostic, observed, catalog, profile);
        if (Status is "UnknownBuild" or "MissingFiles" or "InconsistentState")
            Require(diagnostic.Kind != "none", "блокирующее состояние не содержит типизированной причины");
        if (TranslationUpdateKind == "metadata-only")
        {
            Require(Profile.Mode == "compatible-revision"
                && Status == "PatchedByThisTool"
                && State != null
                && string.Equals(State.BuildId, Profile.Id, StringComparison.Ordinal)
                && string.Equals(State.OriginalSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(State.TranslationsSha256, Profile.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                    || State.AppliedTranslations != Profile.AppliedTranslations),
                "обновление только служебных данных не закрепляет эквивалентный установленный артефакт");
        }
        if (TranslationUpdateKind == "content")
        {
            Require(Status is "PatchedByThisTool" or "PatchSupersededByCatalogUpdate",
                "обновление содержимого перевода заявлено для неподходящего состояния");
            Require(State != null
                && !string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase),
                "обновление содержимого не меняет установленный файл перевода");
        }
        Require(Status == "PatchSupersededByCatalogUpdate"
                ? TranslationUpdateKind == "content"
                : Status == "PatchedByThisTool" || TranslationUpdateKind == "none",
            "вид обновления перевода противоречит состоянию установки");
        ValidateProcessConflicts(processConflicts);

        bool successfulInspection = Status is "CompatibleOriginal"
            or "PatchSupersededByOfficialUpdate"
            or "PatchSupersededByCatalogUpdate"
            or "PatchedByThisTool";
        Require(ExitCode == (successfulInspection ? 0 : 5), "код завершения противоречит состоянию установки");

        bool hasProcessConflicts = processConflicts.Length > 0;
        string expectedAction = ExpectedPlanAction(hasProcessConflicts);
        Require(string.Equals(planAction, expectedAction, StringComparison.Ordinal),
            "решение плана противоречит состоянию, процессам или праву записи");

        bool remoteApplyAuthorized = IsRemoteApplyAuthorized();
        bool expectedCanApply = (TranslationUpdateAvailable
                || Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate")
            && !hasProcessConflicts && InstallationWritesEnabled && profile.Certified && catalog.ExactMatch
            && remoteApplyAuthorized;
        bool expectedCanRestore = Status is "PatchedByThisTool" or "PatchSupersededByCatalogUpdate"
            && !hasProcessConflicts && InstallationWritesEnabled && RestoreRecoveryAuthorized;
        bool expectedCanRecover = Status == "RecoveryRequired"
            && !hasProcessConflicts && InstallationWritesEnabled && RestoreRecoveryAuthorized;
        Require(CanApply == expectedCanApply && CanRestore == expectedCanRestore && CanRecover == expectedCanRecover,
            "разрешения кнопок противоречат проверенному плану");
        if (CanApply && !string.Equals(Catalog.Source, "embedded", StringComparison.Ordinal))
        {
            Require(Update != null && string.Equals(Catalog.Source, Update.Source, StringComparison.Ordinal),
                "источник выбранного подписанного каталога расходится с выбранным пакетом");
        }
    }

    private void ValidateObserved(ObservedCacheIdentity observed, EmbeddedProfileInfo profile)
    {
        ValidateOptionalHash(observed.EnglishSha256, "SHA-256 английского файла");
        ValidateOptionalHash(observed.BaseSha256, "SHA-256 целевого файла");
        ValidateOptionalHash(observed.StampSha256, "SHA-256 файла версии");
        ValidateOptionalText(observed.GameVersion, 64, "наблюдаемая версия игры");
        ValidateOptionalText(observed.EnglishContent, 256, "версия английского контента");
        ValidateOptionalText(observed.BaseContent, 256, "версия базового контента");
        ValidateOptionalText(observed.EnglishContentGuid, 64, "GUID английского контента");
        ValidateOptionalText(observed.BaseContentGuid, 64, "GUID украинского контента");
        ValidateOptionalHash(observed.OrderedKeysetSha256, "SHA-256 порядка ключей");

        if (Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate"
            or "PatchSupersededByCatalogUpdate" or "PatchedByThisTool")
        {
            Require(observed.EnglishSha256 != null && observed.BaseSha256 != null && observed.StampSha256 != null,
                "распознанное состояние не содержит контрольные суммы файлов");
            Require(observed.EnglishContent != null && observed.BaseContent != null,
                "распознанное состояние не содержит версии EN/UK контента");
            Require(observed.EnglishSchema == 4 && observed.BaseSchema == 4
                && observed.EnglishLocaleId == profile.EnglishLocaleId
                && observed.BaseLocaleId == profile.BaseLocaleId
                && observed.EnglishLocaleRevision == profile.EnglishLocaleRevision
                && observed.BaseLocaleRevision == profile.BaseLocaleRevision
                && observed.EnglishReleaseRevision == profile.EnglishReleaseRevision
                && observed.BaseReleaseRevision == profile.BaseReleaseRevision
                && observed.EntryCount == profile.EntryCount
                && string.Equals(observed.EnglishContentGuid, profile.ContentGuid, StringComparison.Ordinal)
                && string.Equals(observed.BaseContentGuid, profile.ContentGuid, StringComparison.Ordinal)
                && (profile.OrderedKeysetSha256 == null
                    || string.Equals(observed.OrderedKeysetSha256, profile.OrderedKeysetSha256, StringComparison.OrdinalIgnoreCase)),
                "распознанное состояние не закрепляет структуру и ревизии EN/UK профиля");
            Require(string.Equals(observed.GameVersion, profile.GameVersion, StringComparison.Ordinal),
                "наблюдаемая версия игры расходится с распознанным профилем");
        }
    }

    private static void ValidateCatalog(BundledCatalogInfo catalog, EmbeddedProfileInfo profile)
    {
        if (catalog.Source != null)
        {
            Require(catalog.Source is "embedded" or "Remote" or "CachedCurrent" or "LastKnownGood" or "ChannelHead",
                "неизвестный источник каталога");
        }
        ValidateOptionalHash(catalog.Sha256, "SHA-256 установленного каталога");
        Require(!catalog.RegularFile || catalog.Present,
            "каталог отмечен обычным файлом, хотя он отсутствует");
        Require(!catalog.ExactMatch || catalog.Present && catalog.RegularFile,
            "совпадение каталога заявлено без обычного файла");
        Require(catalog.RegularFile ? IsSha256(catalog.Sha256) : catalog.Sha256 == null,
            "контрольная сумма каталога противоречит типу файла");
        bool expectedExactMatch = catalog.Present && catalog.RegularFile
            && IsSha256(profile.CatalogSha256)
            && string.Equals(catalog.Sha256, profile.CatalogSha256, StringComparison.OrdinalIgnoreCase);
        Require(catalog.ExactMatch == expectedExactMatch,
            "признак точного каталога противоречит SHA-256 профиля");
    }

    private void ValidateDiagnostic(
        RuntimePlanDiagnostic diagnostic,
        ObservedCacheIdentity observed,
        BundledCatalogInfo catalog,
        EmbeddedProfileInfo profile)
    {
        Require(KnownDiagnosticKinds.Contains(diagnostic.Kind)
            && KnownDiagnosticComponents.Contains(diagnostic.Component),
            "неизвестный тип или компонент диагностики");
        Require(diagnostic.Current == null || diagnostic.Current.Length is > 0 and <= 1024,
            "некорректное текущее значение диагностики");
        Require(diagnostic.Expected == null || diagnostic.Expected.Length is > 0 and <= 1024,
            "некорректное ожидаемое значение диагностики");
        RuntimePlanDiagnostic canonical = DeriveCanonicalDiagnostic(observed, catalog, profile);
        Require(string.Equals(diagnostic.Kind, canonical.Kind, StringComparison.Ordinal)
            && string.Equals(diagnostic.Component, canonical.Component, StringComparison.Ordinal)
            && string.Equals(diagnostic.Current, canonical.Current, StringComparison.Ordinal)
            && string.Equals(diagnostic.Expected, canonical.Expected, StringComparison.Ordinal),
            "типизированная диагностика не совпадает с канонической причиной и её приоритетом");
    }

    private RuntimePlanDiagnostic DeriveCanonicalDiagnostic(
        ObservedCacheIdentity observed,
        BundledCatalogInfo catalog,
        EmbeddedProfileInfo profile)
    {
        static RuntimePlanDiagnostic Value(string kind, string component, string? current, string? expected) =>
            new() { Kind = kind, Component = component, Current = current, Expected = expected };

        if (Status == "MissingFiles")
            return Value("structural-boundary", "missing-files", "missing", "fixed EN/UK/stamp tuple");
        if (Status == "InconsistentState"
            && string.Equals(LocalProblem, "journal-authentication", StringComparison.Ordinal))
        {
            return Value(
                "local-state",
                "journal-authentication",
                "untrusted",
                "uniquely authenticated recovery journal");
        }
        if (Status == "InconsistentState")
        {
            if (Journal != null)
                return Value("local-state", "journal", Journal.Phase, "authenticated recovery journal");
            if (State == null)
                return Value("local-state", "patch-state", observed.BaseSha256 ?? "unreadable",
                    profile.ExpectedOutputSha256);
        }
        if (Status == "RecoveryRequired")
            return Value("local-state", "journal", Journal?.Phase, "authenticated recovery journal");

        if (Status is "UnknownBuild" or "InconsistentState")
        {
            if (observed.EnglishSchema != 4 || observed.BaseSchema != 4)
                return Value("structural-boundary", "loc1-schema",
                    $"EN={observed.EnglishSchema?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"};UK={observed.BaseSchema?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}",
                    "EN=4;UK=4");
            if (!string.Equals(observed.EnglishContentGuid, profile.ContentGuid, StringComparison.Ordinal)
                || !string.Equals(observed.BaseContentGuid, profile.ContentGuid, StringComparison.Ordinal))
                return Value("structural-boundary", "content-guid",
                    $"EN={observed.EnglishContentGuid ?? "unreadable"};UK={observed.BaseContentGuid ?? "unreadable"}",
                    $"EN={profile.ContentGuid};UK={profile.ContentGuid}");
            if (observed.EnglishLocaleId != profile.EnglishLocaleId
                || observed.BaseLocaleId != profile.BaseLocaleId)
                return Value("structural-boundary", "locale-slot",
                    $"EN={observed.EnglishLocaleId?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"};UK={observed.BaseLocaleId?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}",
                    $"EN={profile.EnglishLocaleId.ToString(CultureInfo.InvariantCulture)};UK={profile.BaseLocaleId.ToString(CultureInfo.InvariantCulture)}");
            if (observed.EntryCount != profile.EntryCount
                || (profile.OrderedKeysetSha256 != null
                    && !string.Equals(observed.OrderedKeysetSha256, profile.OrderedKeysetSha256, StringComparison.OrdinalIgnoreCase)))
                return Value("structural-boundary", "ordered-keyset",
                    $"entries={observed.EntryCount?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"};sha256={observed.OrderedKeysetSha256 ?? "unreadable"}",
                    $"entries={profile.EntryCount.ToString(CultureInfo.InvariantCulture)};sha256={profile.OrderedKeysetSha256 ?? "not-pinned"}");
            if (!string.Equals(observed.EnglishSha256, profile.EnglishSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observed.EnglishContent, profile.EnglishContent, StringComparison.Ordinal)
                || observed.EnglishLocaleRevision != profile.EnglishLocaleRevision
                || observed.EnglishReleaseRevision != profile.EnglishReleaseRevision)
                return Value("translation-data", "english-source",
                    FormatObservedCorpus(observed.EnglishContent, observed.EnglishReleaseRevision,
                        observed.EnglishLocaleRevision, observed.EnglishSha256),
                    FormatExpectedCorpus(profile.EnglishContent, profile.EnglishReleaseRevision,
                        profile.EnglishLocaleRevision, profile.EnglishSha256));
            if (!string.Equals(observed.BaseSha256, profile.BaseSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observed.BaseContent, profile.BaseContent, StringComparison.Ordinal)
                || observed.BaseLocaleRevision != profile.BaseLocaleRevision
                || observed.BaseReleaseRevision != profile.BaseReleaseRevision)
                return Value("translation-data", "ukrainian-base",
                    FormatObservedCorpus(observed.BaseContent, observed.BaseReleaseRevision,
                        observed.BaseLocaleRevision, observed.BaseSha256),
                    FormatExpectedCorpus(profile.BaseContent, profile.BaseReleaseRevision,
                        profile.BaseLocaleRevision, profile.BaseSha256!));
            if (!string.Equals(observed.StampSha256, profile.StampSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(observed.GameVersion, profile.GameVersion, StringComparison.Ordinal))
                return Value("translation-data", "version-stamp",
                    $"value={observed.GameVersion ?? "unreadable"};sha256={observed.StampSha256 ?? "unreadable"}",
                    $"value={profile.GameVersion};sha256={profile.StampSha256}");
        }

        if (Status == "InconsistentState")
            return Value("local-state", "patch-state", observed.BaseSha256 ?? "unreadable",
                State?.PatchedSha256 ?? profile.ExpectedOutputSha256);
        if (!catalog.ExactMatch)
            return Value("translation-data", "catalog-sha256", catalog.Sha256 ?? "missing", profile.CatalogSha256);
        if (Status == "PatchSupersededByCatalogUpdate")
            return Value("translation-data", "catalog-sha256", State?.TranslationsSha256, profile.CatalogSha256);
        if (Status == "PatchSupersededByOfficialUpdate")
            return Value("translation-data", "official-base-refresh", observed.BaseSha256, State?.PatchedSha256);
        if (profile.Mode == "compatible-revision" && profile.EnglishFallbacks > 0)
        {
            int sourceRows = checked(profile.AppliedTranslations + profile.EnglishFallbacks);
            return Value(profile.AppliedTranslations > 0 ? "compatible-coverage" : "translation-data",
                "source-hint-coverage",
                $"{profile.AppliedTranslations.ToString(CultureInfo.InvariantCulture)}/{sourceRows.ToString(CultureInfo.InvariantCulture)}",
                $"{sourceRows.ToString(CultureInfo.InvariantCulture)}/{sourceRows.ToString(CultureInfo.InvariantCulture)}");
        }

        return Value("none", "none", null, null);
    }

    private static string FormatObservedCorpus(string? content, uint? release, uint? localeRevision, string? sha256)
    {
        return $"content={content ?? "unreadable"};release={release?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"};locale_revision={localeRevision?.ToString("X8", CultureInfo.InvariantCulture) ?? "unreadable"};sha256={sha256 ?? "unreadable"}";
    }

    private static string FormatExpectedCorpus(string content, uint release, uint localeRevision, string sha256)
    {
        return $"content={content};release={release.ToString(CultureInfo.InvariantCulture)};locale_revision={localeRevision.ToString("X8", CultureInfo.InvariantCulture)};sha256={sha256}";
    }

    private void ValidateProfile(EmbeddedProfileInfo profile)
    {
        Require(IsSafeIdentifier(profile.Id), "некорректный идентификатор профиля");
        Require(KnownProfileModes.Contains(profile.Mode), "неизвестный режим профиля");
        Require(!string.IsNullOrWhiteSpace(profile.GameVersion) && profile.GameVersion.Length <= 64,
            "некорректная версия игры в профиле");
        Require(profile.Readiness is "ready" or "blocked", "неизвестная готовность профиля");
        Require(KnownTranslationPolicies.Contains(profile.TranslationPolicy), "неизвестная политика перевода");
        Require(Guid.TryParseExact(profile.ContentGuid, "D", out Guid contentGuid)
            && string.Equals(contentGuid.ToString("D"), profile.ContentGuid, StringComparison.Ordinal)
            && profile.Loc1Schema == 4,
            "профиль не закрепляет поддерживаемое семейство LOC1");
        Require(!string.IsNullOrWhiteSpace(profile.EnglishContent)
            && !string.IsNullOrWhiteSpace(profile.BaseContent)
            && profile.EnglishContent.Length <= 256 && profile.BaseContent.Length <= 256,
            "профиль не закрепляет версии EN/UK контента");
        Require(IsSha256(profile.EnglishSha256) && IsSha256(profile.StampSha256)
            && (profile.Mode == "compatible-revision"
                ? IsSha256(profile.OrderedKeysetSha256)
                : profile.OrderedKeysetSha256 == null || IsSha256(profile.OrderedKeysetSha256)),
            "профиль не закрепляет исходный EN/stamp/keyset");
        Require(profile.EnglishLocaleId == 1 && profile.BaseLocaleId == 8
            && profile.EnglishLocaleRevision > 0 && profile.BaseLocaleRevision > 0
            && profile.EnglishReleaseRevision > 0 && profile.BaseReleaseRevision > 0,
            "профиль не закрепляет ожидаемые локали и ревизии EN/UK");
        ValidateOptionalHash(profile.CatalogSha256, "SHA-256 каталога");
        ValidateOptionalHash(profile.ExpectedOutputSha256, "SHA-256 ожидаемого результата");
        ValidateOptionalHash(profile.BaseSha256, "SHA-256 официальной базы");

        Require(profile.EntryCount > 0 && profile.EntryCount <= 100_000,
            "некорректное число строк профиля");
        Require(profile.AppliedTranslations >= 0 && profile.AppliedTranslations <= profile.EntryCount,
            "некорректное число русских строк");

        bool fallbacksPinned = profile.EnglishFallbacks >= 0
            && profile.BaseFallbacks >= 0
            && profile.NeedsReviewFallbacks >= 0;
        bool fallbacksOmitted = profile.EnglishFallbacks == -1
            && profile.BaseFallbacks == -1
            && profile.NeedsReviewFallbacks == -1;
        Require(fallbacksPinned || (!profile.Certified && !InstallationWritesEnabled && fallbacksOmitted),
            "некорректные значения резервных строк");
        if (fallbacksPinned)
        {
            Require(profile.AppliedTranslations + profile.EnglishFallbacks + profile.BaseFallbacks == profile.EntryCount,
                "русские, английские и базовые строки не покрывают профиль целиком");
            Require(profile.NeedsReviewFallbacks <= profile.EnglishFallbacks,
                "число строк на проверке превышает английский резерв");
        }

        if (profile.Certified)
        {
            Require(profile.Readiness == "ready", "сертифицированный профиль не готов");
            Require(profile.Mode != "compatible-revision" || profile.AppliedTranslations > 0,
                "совместимый профиль не содержит ни одной подтверждённой строки перевода");
            Require(IsSha256(profile.CatalogSha256) && IsSha256(profile.ExpectedOutputSha256)
                && IsSha256(profile.BaseSha256),
                "сертифицированный профиль не закрепляет каталог и результат");
        }

    }

    private void ValidateUpdate()
    {
        Require(Catalog.Source != null, "контракт v3 не указывает источник выбранного каталога");
        if (UpdateProblem != null)
        {
            Require(!string.IsNullOrWhiteSpace(UpdateProblem) && UpdateProblem.Length <= 4096,
                "некорректное сообщение канала обновлений");
        }
        Require(!UpdateProblemBlocksApply || UpdateProblem != null,
            "блокирующая проблема обновления не содержит сообщения");

        if (Update != null)
        {
            ValidateSignedUpdate(Update, allowChannelHead: false);
            Require(string.Equals(Catalog.Source, Update.Source, StringComparison.Ordinal)
                && string.Equals(Catalog.Sha256, Update.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Profile.CatalogSha256, Update.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Profile.TranslationPolicy, MapSignedCatalogPolicy(Update.CatalogPolicy), StringComparison.Ordinal),
                "выбранный каталог/профиль не закреплён выбранным подписанным пакетом");
        }
        if (string.Equals(Catalog.Source, "embedded", StringComparison.Ordinal))
            Require(Update == null, "встроенный каталог ошибочно связан с посторонним подписанным пакетом");
        if (ChannelAuthority != null)
            ValidateSignedUpdate(ChannelAuthority, allowChannelHead: true);
        if (string.Equals(Catalog.Source, "ChannelHead", StringComparison.Ordinal))
        {
            Require(Update == null && ChannelAuthority != null
                && !Catalog.Present && !Catalog.RegularFile && Catalog.Sha256 == null && !Catalog.ExactMatch
                && string.Equals(Profile.CatalogSha256, ChannelAuthority.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Profile.TranslationPolicy, MapSignedCatalogPolicy(ChannelAuthority.CatalogPolicy), StringComparison.Ordinal),
                "диагностический профиль не закреплён авторитетом канала");
        }
        Require(Update == null || ChannelAuthority != null,
            "выбранный подписанный пакет не имеет отдельного авторитета канала");
        if (Update != null && ChannelAuthority != null)
        {
            Require(ChannelAuthority.Sequence >= Update.Sequence,
                "авторитет канала старше выбранного подписанного пакета");
            bool sameIdentity = ChannelAuthority.Sequence == Update.Sequence
                && string.Equals(ChannelAuthority.PayloadSha256, Update.PayloadSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ChannelAuthority.ReleaseId, Update.ReleaseId, StringComparison.Ordinal)
                && string.Equals(ChannelAuthority.ArtifactId, Update.ArtifactId, StringComparison.Ordinal)
                && string.Equals(ChannelAuthority.CatalogSha256, Update.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ChannelAuthority.CatalogPolicy, Update.CatalogPolicy, StringComparison.Ordinal);
            if (ChannelAuthority.Sequence == Update.Sequence)
            {
                Require(sameIdentity,
                    "одинаковый sequence подписанных данных имеет разную identity");
            }
            if (Update.Source is "Remote" or "CachedCurrent")
            {
                Require(sameIdentity,
                    "текущий выбранный каталог не совпадает с авторитетом канала");
            }
        }
    }

    private void ValidateSignedUpdate(SignedPlanUpdateInfo update, bool allowChannelHead)
    {
        Require(allowChannelHead
                ? update.Source == "ChannelHead"
                : update.Source is "Remote" or "CachedCurrent" or "LastKnownGood",
            "неизвестный источник подписанных данных");
        Require(update.Sequence > 0, "некорректный sequence подписанного обновления");
        Require(IsSha256(update.PayloadSha256), "некорректный SHA-256 payload обновления");
        Require(IsSha256(update.CatalogSha256), "некорректный SHA-256 каталога подписанного обновления");
        Require(update.CatalogPolicy is "release-approved-v1" or "validated-preview-v1",
            "неизвестная политика каталога подписанного обновления");
        Require(IsSafeIdentifier(update.ReleaseId) && IsSafeIdentifier(update.ArtifactId),
            "некорректный release/artifact id обновления");
        Require(update.IssuedUtc != default && update.ExpiresUtc > update.IssuedUtc,
            "некорректный срок подписанного обновления");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (update.ExpiresUtc < now - TimeSpan.FromMinutes(1))
            Require(update.Expired, "истёкшее подписанное обновление не отмечено как истёкшее");
        if (update.ExpiresUtc > now + TimeSpan.FromMinutes(1))
            Require(!update.Expired, "актуальное подписанное обновление ошибочно отмечено как истёкшее");
        Require(update.PatcherDisposition is "Current" or "UpdateAvailable" or "TooOld",
            "неизвестное требование версии патчера");
        bool validMinimum = Version.TryParse(update.MinimumPatcherVersion, out Version? minimum);
        bool validLatest = Version.TryParse(update.LatestPatcherVersion, out Version? latest);
        Require(validMinimum && validLatest && minimum! <= latest!,
            "некорректные границы версии патчера");
        Version current = Version.Parse(PatcherVersion);
        string expectedDisposition = current < minimum!
            ? "TooOld"
            : current < latest! ? "UpdateAvailable" : "Current";
        Require(string.Equals(update.PatcherDisposition, expectedDisposition, StringComparison.Ordinal),
            "требование версии патчера противоречит подписанным границам");
        Require(Uri.TryCreate(update.DownloadPage, UriKind.Absolute, out Uri? page)
            && page.Scheme == Uri.UriSchemeHttps
            && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase),
            "некорректная страница обновления патчера");
        Require(update.NotesRu.Length <= 4096, "слишком длинное примечание обновления");
    }

    private bool IsRemoteApplyAuthorized()
    {
        if (UpdateProblemBlocksApply) return false;
        if (string.Equals(Catalog.Source, "embedded", StringComparison.Ordinal))
            return ChannelAuthority == null && Update == null;
        if (ChannelAuthority == null)
            return false;
        if (ChannelAuthority.PatcherDisposition == "TooOld") return false;
        if (Update == null) return false;
        if (Catalog.Source == "ChannelHead") return false;
        if (!string.Equals(Catalog.Source, Update.Source, StringComparison.Ordinal)
            || !Catalog.ExactMatch
            || !string.Equals(Catalog.Sha256, Update.CatalogSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Profile.CatalogSha256, Update.CatalogSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Profile.TranslationPolicy, MapSignedCatalogPolicy(Update.CatalogPolicy), StringComparison.Ordinal))
        {
            return false;
        }
        if (Catalog.Source == "LastKnownGood")
        {
            return State != null
                && string.Equals(State.BuildId, Profile.Id, StringComparison.Ordinal)
                && string.Equals(State.OriginalSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(State.TranslationsSha256, Profile.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && State.AppliedTranslations == Profile.AppliedTranslations;
        }
        return !Update.Expired;
    }

    private static string MapSignedCatalogPolicy(string policy)
    {
        return policy switch
        {
            "release-approved-v1" => "release-approved",
            "validated-preview-v1" => "community-preview-all-drafts",
            _ => string.Empty
        };
    }

    private void ValidateStateAndJournal()
    {
        if (State != null)
        {
            Require(IsSafeIdentifier(State.BuildId), "некорректный идентификатор записанного состояния");
            Require(State.AppliedTranslations > 0 && State.AppliedTranslations <= 100_000,
                "некорректное число строк записанного состояния");
            Require(State.AppliedAt != default, "отсутствует время установки");
            Require(IsSha256(State.PatchedSha256) && IsSha256(State.OriginalSha256)
                && IsSha256(State.TranslationsSha256),
                "некорректные контрольные суммы записанного состояния");
        }

        if (Journal != null)
        {
            Require(Journal.Operation is "runtime-cache-apply" or "runtime-cache-restore"
                or "runtime-cache-upgrade" or "runtime-cache-upgrade-restore",
                "неизвестная операция журнала");
            Require(KnownJournalPhases.Contains(Journal.Phase), "неизвестная фаза журнала");
            Require(IsSafeIdentifier(Journal.TransactionId),
                "некорректный идентификатор транзакции");
        }

        Require(Status == "RecoveryRequired" ? Journal != null : Journal == null,
            "журнал транзакции противоречит состоянию установки");
        bool restorableStatus = Status is "PatchedByThisTool"
            or "PatchSupersededByCatalogUpdate" or "RecoveryRequired";
        Require(RestoreRecoveryAuthorized == restorableStatus,
            "право восстановления противоречит аутентифицированному состоянию");
        if (Status is "PatchedByThisTool" or "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate")
        {
            Require(State != null, "состояние установленного или заменённого патча отсутствует");
        }
        if (Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate")
        {
            Require(string.Equals(Observed.BaseSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "наблюдаемый официальный файл не совпадает с базой распознанного профиля");
        }
        if (Status == "PatchedByThisTool")
        {
            Require(string.Equals(State!.PatchedSha256, Observed.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "контрольная сумма установленного файла противоречит наблюдению");
            Require(string.Equals(State.OriginalSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "исходная контрольная сумма установленного состояния противоречит профилю");
            if (TranslationUpdateAvailable)
            {
                Require(Profile.Mode is "exact" or "compatible-revision",
                    "обновление установленного перевода не закреплено распознанным режимом");
            }
            else
            {
                Require(string.Equals(State.BuildId, Profile.Id, StringComparison.Ordinal)
                    && State.AppliedTranslations == Profile.AppliedTranslations,
                    "установленное состояние относится к другому профилю или числу строк");
                Require(string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase),
                    "контрольная сумма установленного файла противоречит профилю");
                Require(string.Equals(State.TranslationsSha256, Profile.CatalogSha256, StringComparison.OrdinalIgnoreCase),
                    "каталог установленного состояния противоречит профилю");
            }
        }
        if (Status == "PatchSupersededByCatalogUpdate")
        {
            Require(string.Equals(State!.BuildId, Profile.Id, StringComparison.Ordinal),
                "устаревший перевод относится к другому профилю игры");
            Require(string.Equals(State.OriginalSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "исходная контрольная сумма устаревшего перевода противоречит профилю");
            Require(string.Equals(State.PatchedSha256, Observed.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "контрольная сумма устаревшего перевода противоречит наблюдению");
            Require(!string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(State.OriginalSha256, State.PatchedSha256, StringComparison.OrdinalIgnoreCase),
                "устаревший перевод совпадает с исходным или новым закреплённым результатом");
        }
        if (Status is "CompatibleOriginal" or "UnknownBuild")
        {
            Require(State == null, "записанное состояние противоречит исходному или неизвестному файлу");
        }
    }

    private static void ValidateProcessConflicts(string[] processConflicts)
    {
        Require(processConflicts.Length <= 256, "слишком много конфликтующих процессов");
        Require(processConflicts.All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= 2048),
            "список конфликтующих процессов содержит пустую или слишком длинную запись");
    }

    private string ExpectedPlanAction(bool hasProcessConflicts)
    {
        if (Status is "UnknownBuild" or "InconsistentState" or "MissingFiles")
            return "REFUSE_UNKNOWN_OR_INCONSISTENT";
        bool installable = TranslationUpdateAvailable
            || Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate";
        bool restorationOnly = !TranslationUpdateAvailable
            && Status is "PatchedByThisTool" or "RecoveryRequired";
        if ((!IsRemoteApplyAuthorized() && !restorationOnly)
            || (!RestoreRecoveryAuthorized && restorationOnly))
            return "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT";
        if (installable && hasProcessConflicts) return "REFUSE_CLOSE_GAME_AND_LAUNCHER";
        if (installable && !InstallationWritesEnabled) return "REFUSE_DEV_WRITES_DISABLED";
        if (installable && !Profile.Certified) return "REFUSE_NO_TRUSTED_CACHE_RELEASE_PROFILE";
        if (installable && !Catalog.ExactMatch) return "REFUSE_MISSING_OR_MISMATCHED_CATALOG";
        if (TranslationUpdateAvailable) return "READY_TO_UPDATE_TRANSLATION";
        if (Status == "PatchSupersededByCatalogUpdate") return "READY_TO_UPDATE_TRANSLATION";
        if (Status == "PatchSupersededByOfficialUpdate") return "READY_TO_REAPPLY_AFTER_GAME_UPDATE";
        if (Status == "CompatibleOriginal") return "READY_TO_APPLY";
        if (Status == "PatchedByThisTool" && hasProcessConflicts) return "REFUSE_CLOSE_GAME_AND_LAUNCHER";
        if (Status == "PatchedByThisTool") return InstallationWritesEnabled ? "NOOP_OR_RESTORE" : "REFUSE_DEV_WRITES_DISABLED";
        if (Status == "RecoveryRequired" && hasProcessConflicts) return "REFUSE_CLOSE_GAME_AND_LAUNCHER";
        if (Status == "RecoveryRequired") return InstallationWritesEnabled ? "RECOVERY_REQUIRED" : "REFUSE_DEV_WRITES_DISABLED";
        return "REFUSE_UNKNOWN_OR_INCONSISTENT";
    }

    private static void ValidateOptionalHash(string? value, string label)
    {
        if (value != null) Require(IsSha256(value), $"некорректный {label}");
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string label)
    {
        if (value != null) Require(!string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength, $"некорректная {label}");
    }

    private static bool IsSha256(string? value)
    {
        return value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool IsSafeIdentifier(string? value)
    {
        return value is { Length: > 0 and <= 128 }
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw Contradiction(problem);
        }
    }

    private static InvalidDataException Contradiction(string problem)
    {
        return new InvalidDataException($"Проверяющий модуль вернул противоречивый ответ: {problem}.");
    }
}

internal sealed class ObservedCacheIdentity
{
    [JsonPropertyName("english_sha256")]
    [JsonRequired]
    public string? EnglishSha256 { get; set; }

    [JsonPropertyName("base_sha256")]
    [JsonRequired]
    public string? BaseSha256 { get; set; }

    [JsonPropertyName("stamp_sha256")]
    [JsonRequired]
    public string? StampSha256 { get; set; }

    [JsonPropertyName("game_version")]
    [JsonRequired]
    public string? GameVersion { get; set; }

    [JsonPropertyName("english_content")]
    [JsonRequired]
    public string? EnglishContent { get; set; }

    [JsonPropertyName("base_content")]
    [JsonRequired]
    public string? BaseContent { get; set; }

    [JsonPropertyName("english_schema")]
    [JsonRequired]
    public uint? EnglishSchema { get; set; }

    [JsonPropertyName("base_schema")]
    [JsonRequired]
    public uint? BaseSchema { get; set; }

    [JsonPropertyName("english_content_guid")]
    [JsonRequired]
    public string? EnglishContentGuid { get; set; }

    [JsonPropertyName("base_content_guid")]
    [JsonRequired]
    public string? BaseContentGuid { get; set; }

    [JsonPropertyName("english_locale_id")]
    [JsonRequired]
    public uint? EnglishLocaleId { get; set; }

    [JsonPropertyName("english_locale_revision")]
    [JsonRequired]
    public uint? EnglishLocaleRevision { get; set; }

    [JsonPropertyName("english_release_revision")]
    [JsonRequired]
    public uint? EnglishReleaseRevision { get; set; }

    [JsonPropertyName("base_locale_id")]
    [JsonRequired]
    public uint? BaseLocaleId { get; set; }

    [JsonPropertyName("base_locale_revision")]
    [JsonRequired]
    public uint? BaseLocaleRevision { get; set; }

    [JsonPropertyName("base_release_revision")]
    [JsonRequired]
    public uint? BaseReleaseRevision { get; set; }

    [JsonPropertyName("entry_count")]
    [JsonRequired]
    public int? EntryCount { get; set; }

    [JsonPropertyName("ordered_keyset_sha256")]
    [JsonRequired]
    public string? OrderedKeysetSha256 { get; set; }
}

internal sealed class RuntimePlanDiagnostic
{
    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("component")]
    [JsonRequired]
    public string Component { get; set; } = string.Empty;

    [JsonPropertyName("current")]
    [JsonRequired]
    public string? Current { get; set; }

    [JsonPropertyName("expected")]
    [JsonRequired]
    public string? Expected { get; set; }
}

internal sealed class BundledCatalogInfo
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("present")]
    [JsonRequired]
    public bool Present { get; set; }

    [JsonPropertyName("regular_file")]
    [JsonRequired]
    public bool RegularFile { get; set; }

    [JsonPropertyName("sha256")]
    [JsonRequired]
    public string? Sha256 { get; set; }

    [JsonPropertyName("exact_match")]
    [JsonRequired]
    public bool ExactMatch { get; set; }
}

internal sealed class SignedPlanUpdateInfo
{
    [JsonPropertyName("source")]
    [JsonRequired]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("sequence")]
    [JsonRequired]
    public ulong Sequence { get; set; }

    [JsonPropertyName("payload_sha256")]
    [JsonRequired]
    public string PayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("release_id")]
    [JsonRequired]
    public string ReleaseId { get; set; } = string.Empty;

    [JsonPropertyName("artifact_id")]
    [JsonRequired]
    public string ArtifactId { get; set; } = string.Empty;

    [JsonPropertyName("catalog_sha256")]
    [JsonRequired]
    public string CatalogSha256 { get; set; } = string.Empty;

    [JsonPropertyName("catalog_policy")]
    [JsonRequired]
    public string CatalogPolicy { get; set; } = string.Empty;

    [JsonPropertyName("issued_utc")]
    [JsonRequired]
    public DateTimeOffset IssuedUtc { get; set; }

    [JsonPropertyName("expires_utc")]
    [JsonRequired]
    public DateTimeOffset ExpiresUtc { get; set; }

    [JsonPropertyName("expired")]
    [JsonRequired]
    public bool Expired { get; set; }

    [JsonPropertyName("patcher_disposition")]
    [JsonRequired]
    public string PatcherDisposition { get; set; } = string.Empty;

    [JsonPropertyName("minimum_patcher_version")]
    [JsonRequired]
    public string MinimumPatcherVersion { get; set; } = string.Empty;

    [JsonPropertyName("latest_patcher_version")]
    [JsonRequired]
    public string LatestPatcherVersion { get; set; } = string.Empty;

    [JsonPropertyName("download_page")]
    [JsonRequired]
    public string DownloadPage { get; set; } = string.Empty;

    [JsonPropertyName("notes_ru")]
    [JsonRequired]
    public string NotesRu { get; set; } = string.Empty;
}

internal sealed class EmbeddedProfileInfo
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    [JsonRequired]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("game_version")]
    [JsonRequired]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("readiness")]
    [JsonRequired]
    public string Readiness { get; set; } = string.Empty;

    [JsonPropertyName("certified")]
    [JsonRequired]
    public bool Certified { get; set; }

    [JsonPropertyName("translation_policy")]
    [JsonRequired]
    public string TranslationPolicy { get; set; } = string.Empty;

    [JsonPropertyName("content_guid")]
    [JsonRequired]
    public string ContentGuid { get; set; } = string.Empty;

    [JsonPropertyName("loc1_schema")]
    [JsonRequired]
    public uint Loc1Schema { get; set; }

    [JsonPropertyName("english_content")]
    [JsonRequired]
    public string EnglishContent { get; set; } = string.Empty;

    [JsonPropertyName("base_content")]
    [JsonRequired]
    public string BaseContent { get; set; } = string.Empty;

    [JsonPropertyName("english_sha256")]
    [JsonRequired]
    public string EnglishSha256 { get; set; } = string.Empty;

    [JsonPropertyName("stamp_sha256")]
    [JsonRequired]
    public string StampSha256 { get; set; } = string.Empty;

    [JsonPropertyName("english_locale_id")]
    [JsonRequired]
    public uint EnglishLocaleId { get; set; }

    [JsonPropertyName("english_locale_revision")]
    [JsonRequired]
    public uint EnglishLocaleRevision { get; set; }

    [JsonPropertyName("english_release_revision")]
    [JsonRequired]
    public uint EnglishReleaseRevision { get; set; }

    [JsonPropertyName("base_locale_id")]
    [JsonRequired]
    public uint BaseLocaleId { get; set; }

    [JsonPropertyName("base_locale_revision")]
    [JsonRequired]
    public uint BaseLocaleRevision { get; set; }

    [JsonPropertyName("base_release_revision")]
    [JsonRequired]
    public uint BaseReleaseRevision { get; set; }

    [JsonPropertyName("ordered_keyset_sha256")]
    [JsonRequired]
    public string? OrderedKeysetSha256 { get; set; }

    [JsonPropertyName("base_sha256")]
    [JsonRequired]
    public string? BaseSha256 { get; set; }

    [JsonPropertyName("catalog_sha256")]
    [JsonRequired]
    public string? CatalogSha256 { get; set; }

    [JsonPropertyName("expected_output_sha256")]
    [JsonRequired]
    public string? ExpectedOutputSha256 { get; set; }

    [JsonPropertyName("entry_count")]
    [JsonRequired]
    public int EntryCount { get; set; }

    [JsonPropertyName("applied_translations")]
    [JsonRequired]
    public int AppliedTranslations { get; set; }

    [JsonPropertyName("english_fallbacks")]
    [JsonRequired]
    public int EnglishFallbacks { get; set; }

    [JsonPropertyName("base_fallbacks")]
    [JsonRequired]
    public int BaseFallbacks { get; set; }

    [JsonPropertyName("needs_review_fallbacks")]
    [JsonRequired]
    public int NeedsReviewFallbacks { get; set; }
}

internal sealed class InstalledPatchState
{
    [JsonPropertyName("build_id")]
    [JsonRequired]
    public string BuildId { get; set; } = string.Empty;

    [JsonPropertyName("applied_translations")]
    [JsonRequired]
    public int AppliedTranslations { get; set; }

    [JsonPropertyName("applied_at")]
    [JsonRequired]
    public DateTimeOffset AppliedAt { get; set; }

    [JsonPropertyName("patched_sha256")]
    [JsonRequired]
    public string PatchedSha256 { get; set; } = string.Empty;

    [JsonPropertyName("original_sha256")]
    [JsonRequired]
    public string OriginalSha256 { get; set; } = string.Empty;

    [JsonPropertyName("translations_sha256")]
    [JsonRequired]
    public string TranslationsSha256 { get; set; } = string.Empty;
}

internal sealed class InterruptedPatchJournal
{
    [JsonPropertyName("operation")]
    [JsonRequired]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("phase")]
    [JsonRequired]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("transaction_id")]
    [JsonRequired]
    public string TransactionId { get; set; } = string.Empty;
}
