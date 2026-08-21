using System;
using System.Collections.Generic;
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

    [JsonPropertyName("update")]
    public SignedPlanUpdateInfo? Update { get; set; }

    [JsonPropertyName("update_problem")]
    public string? UpdateProblem { get; set; }

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

    [JsonIgnore]
    public bool TranslationUpdateAvailable => Update?.TranslationUpdateAvailable == true;

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

    private void ValidateContract()
    {
        Require(Schema is 1 or 2, "неподдерживаемая версия JSON-контракта");
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
        string[] processConflicts = ProcessConflicts
            ?? throw Contradiction("отсутствует список запущенных процессов");
        string planAction = PlanAction
            ?? throw Contradiction("отсутствует решение плана");
        Require(!string.IsNullOrWhiteSpace(planAction) && KnownPlanActions.Contains(planAction), "неизвестное решение плана");

        ValidateObserved(observed, profile);
        ValidateProfile(profile);
        ValidateCatalog(catalog, profile);
        ValidateUpdate();
        ValidateStateAndJournal();
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
            && !hasProcessConflicts && InstallationWritesEnabled && IsRestorationAuthorized();
        bool expectedCanRecover = Status == "RecoveryRequired"
            && !hasProcessConflicts && InstallationWritesEnabled && IsRestorationAuthorized();
        Require(CanApply == expectedCanApply && CanRestore == expectedCanRestore && CanRecover == expectedCanRecover,
            "разрешения кнопок противоречат проверенному плану");
    }

    private void ValidateObserved(ObservedCacheIdentity observed, EmbeddedProfileInfo profile)
    {
        ValidateOptionalHash(observed.EnglishSha256, "SHA-256 английского файла");
        ValidateOptionalHash(observed.BaseSha256, "SHA-256 целевого файла");
        ValidateOptionalHash(observed.StampSha256, "SHA-256 файла версии");
        ValidateOptionalText(observed.GameVersion, 64, "наблюдаемая версия игры");
        ValidateOptionalText(observed.EnglishContent, 256, "версия английского контента");
        ValidateOptionalText(observed.BaseContent, 256, "версия базового контента");

        if (Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate"
            or "PatchSupersededByCatalogUpdate" or "PatchedByThisTool")
        {
            Require(observed.EnglishSha256 != null && observed.BaseSha256 != null && observed.StampSha256 != null,
                "распознанное состояние не содержит контрольные суммы файлов");
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

    private void ValidateProfile(EmbeddedProfileInfo profile)
    {
        Require(IsSafeIdentifier(profile.Id), "некорректный идентификатор профиля");
        Require(!string.IsNullOrWhiteSpace(profile.GameVersion) && profile.GameVersion.Length <= 64,
            "некорректная версия игры в профиле");
        Require(profile.Readiness is "ready" or "blocked", "неизвестная готовность профиля");
        Require(KnownTranslationPolicies.Contains(profile.TranslationPolicy), "неизвестная политика перевода");
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
            Require(IsSha256(profile.CatalogSha256) && IsSha256(profile.ExpectedOutputSha256)
                && IsSha256(profile.BaseSha256),
                "сертифицированный профиль не закрепляет каталог и результат");
        }

        if (InstallationWritesEnabled)
        {
            Require(profile.Certified && profile.Readiness == "ready",
                "модуль с правом записи не использует готовый сертифицированный профиль");
        }
    }

    private void ValidateUpdate()
    {
        if (Schema == 1)
        {
            Require(Update == null && UpdateProblem == null && Catalog.Source == null,
                "контракт v1 неожиданно содержит данные канала обновлений");
            return;
        }

        Require(Catalog.Source != null, "контракт v2 не указывает источник каталога");
        if (UpdateProblem != null)
        {
            Require(!string.IsNullOrWhiteSpace(UpdateProblem) && UpdateProblem.Length <= 4096,
                "некорректное сообщение канала обновлений");
        }

        if (Update == null)
        {
            Require(Catalog.Source == "embedded" && !TranslationUpdateAvailable,
                "удалённый источник заявлен без подписанного обновления");
            return;
        }

        SignedPlanUpdateInfo update = Update;
        Require(update.Source is "Remote" or "CachedCurrent" or "LastKnownGood" or "ChannelHead",
            "неизвестный источник подписанного обновления");
        Require(update.Sequence > 0, "некорректный sequence подписанного обновления");
        Require(IsSha256(update.PayloadSha256), "некорректный SHA-256 payload обновления");
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
        Require(Version.TryParse(update.MinimumPatcherVersion, out Version? minimum)
            && Version.TryParse(update.LatestPatcherVersion, out Version? latest)
            && minimum <= latest,
            "некорректные границы версии патчера");
        Require(Uri.TryCreate(update.DownloadPage, UriKind.Absolute, out Uri? page)
            && page.Scheme == Uri.UriSchemeHttps
            && string.Equals(page.Host, "github.com", StringComparison.OrdinalIgnoreCase),
            "некорректная страница обновления патчера");
        Require(update.NotesRu.Length <= 4096, "слишком длинное примечание обновления");
        Require(update.TranslationUpdateAvailable ? update.ExactGameProfileFound : true,
            "обновление перевода заявлено без точного профиля игры");
        Require(update.ExactGameProfileFound == !string.Equals(Catalog.Source, "embedded", StringComparison.Ordinal),
            "источник каталога противоречит выбору точного профиля");
        Require(string.Equals(update.Source, Catalog.Source, StringComparison.Ordinal)
                || Catalog.Source == "embedded" && !update.ExactGameProfileFound,
            "источники обновления и выбранного каталога противоречат друг другу");
    }

    private bool IsRemoteApplyAuthorized()
    {
        if (UpdateProblem != null) return false;
        if (Update == null) return UpdateProblem == null;
        if (Update.PatcherDisposition == "TooOld") return false;
        if (Update.Source == "ChannelHead") return false;
        if (Update.Source == "LastKnownGood")
        {
            return Update.ExactGameProfileFound
                && State != null
                && string.Equals(State.BuildId, Profile.Id, StringComparison.Ordinal)
                && string.Equals(State.OriginalSha256, Profile.BaseSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(State.TranslationsSha256, Profile.CatalogSha256, StringComparison.OrdinalIgnoreCase)
                && State.AppliedTranslations == Profile.AppliedTranslations;
        }
        if (!Update.ExactGameProfileFound) return true;
        if (!Update.Expired) return true;
        return false;
    }

    private bool IsRestorationAuthorized() => Status is "PatchedByThisTool"
        or "PatchSupersededByCatalogUpdate" or "RecoveryRequired";

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
        if (Status is "PatchedByThisTool" or "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate")
        {
            Require(State != null, "состояние установленного или заменённого патча отсутствует");
        }
        if (Status == "PatchedByThisTool")
        {
            Require(string.Equals(State!.PatchedSha256, Observed.BaseSha256, StringComparison.OrdinalIgnoreCase),
                "контрольная сумма установленного файла противоречит наблюдению");
            if (TranslationUpdateAvailable)
            {
                Require(Update?.ExactGameProfileFound == true,
                    "обновление установленного перевода не закреплено точным профилем");
            }
            else
            {
                Require(string.Equals(State.BuildId, Profile.Id, StringComparison.Ordinal)
                    && State.AppliedTranslations == Profile.AppliedTranslations,
                    "установленное состояние относится к другому профилю или числу строк");
                Require(string.Equals(State.PatchedSha256, Profile.ExpectedOutputSha256, StringComparison.OrdinalIgnoreCase),
                    "контрольная сумма установленного файла противоречит профилю");
            }
        }
        if (Status == "PatchSupersededByCatalogUpdate")
        {
            Require(string.Equals(State!.BuildId, Profile.Id, StringComparison.Ordinal),
                "устаревший перевод относится к другому профилю игры");
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
        bool installable = TranslationUpdateAvailable
            || Status is "CompatibleOriginal" or "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate";
        bool restorationOnly = Status is "PatchedByThisTool" or "RecoveryRequired";
        if ((!IsRemoteApplyAuthorized() && !restorationOnly)
            || (!IsRestorationAuthorized() && restorationOnly))
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

    [JsonPropertyName("exact_game_profile_found")]
    [JsonRequired]
    public bool ExactGameProfileFound { get; set; }

    [JsonPropertyName("translation_update_available")]
    [JsonRequired]
    public bool TranslationUpdateAvailable { get; set; }

    [JsonPropertyName("notes_ru")]
    [JsonRequired]
    public string NotesRu { get; set; } = string.Empty;
}

internal sealed class EmbeddedProfileInfo
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

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
