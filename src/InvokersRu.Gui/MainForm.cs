using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InvokersRu.Gui;

internal sealed class MainForm : Form
{
    private const string RiskAcknowledgement = "I_ACCEPT_LOCAL_MODIFICATION";

    private readonly CliRunner _cli = new();
    private readonly string _gameRoot;
    private readonly Label _pathLabel;
    private readonly Label _versionLabel;
    private readonly Label _stateLabel;
    private readonly Label _statusBadge;
    private readonly Label _noticeLabel;
    private readonly RichTextBox _log;
    private readonly ActionButton _checkButton;
    private readonly ActionButton _applyButton;
    private readonly ActionButton _restoreButton;
    private readonly Label _busyLabel;
    private CliPlanResult? _lastPlan;
    private bool _busy;

    public MainForm()
    {
        _gameRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Hit_Zone", "Invokers", "i18n");

        Text = "InvokersRu — русификация Titan Legacy";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 700);
        MinimumSize = new Size(820, 640);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(30, 24, 30, 24)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(CreateHeader(), 0, 0);

        CardPanel gameCard = CreateGameCard(out _pathLabel, out _versionLabel, out _stateLabel, out _statusBadge);
        gameCard.Margin = new Padding(0, 0, 0, 16);
        root.Controls.Add(gameCard, 0, 1);

        var noticeCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 10, 18, 8), Margin = new Padding(0, 0, 0, 16) };
        var noticeLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Card,
            Margin = new Padding(0)
        };
        noticeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        noticeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        noticeLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Gold,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
            Text = "ПЕРЕД РУСИФИКАЦИЕЙ: выберите украинский язык в игре, дождитесь загрузки, затем полностью закройте игру и лаунчер."
        }, 0, 0);
        _noticeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            Text = "Проверяем файлы и версию игры…"
        };
        _noticeLabel.Cursor = Cursors.Hand;
        _noticeLabel.Click += (_, _) => OpenVerifiedPatcherPage();
        noticeLayout.Controls.Add(_noticeLabel, 0, 1);
        noticeCard.Controls.Add(noticeLayout);
        root.Controls.Add(noticeCard, 0, 2);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Background,
            Margin = new Padding(0, 0, 0, 16)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13f));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _checkButton = new ActionButton("Проверить", Theme.Blue, Color.FromArgb(86, 151, 247), Color.White) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        _applyButton = new ActionButton("Установить русификацию", Theme.Gold, Theme.GoldHover, Color.FromArgb(20, 24, 32)) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Enabled = false };
        _restoreButton = new ActionButton("Восстановить оригинал", Color.FromArgb(36, 52, 78), Color.FromArgb(47, 66, 98), Theme.Text) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Enabled = false };
        _busyLabel = new Label { Dock = DockStyle.Fill, Text = string.Empty, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted };
        actions.Controls.Add(_checkButton, 0, 0);
        actions.Controls.Add(_applyButton, 1, 0);
        actions.Controls.Add(_restoreButton, 2, 0);
        actions.Controls.Add(_busyLabel, 3, 0);
        root.Controls.Add(actions, 0, 3);

        var logCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(18), Margin = new Padding(0) };
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Card };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        logLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "ЖУРНАЛ ПРОВЕРКИ",
            ForeColor = Theme.Gold,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(12, 21, 35),
            ForeColor = Color.FromArgb(189, 202, 222),
            Font = new Font("Cascadia Mono", 8.8f, FontStyle.Regular, GraphicsUnit.Point),
            DetectUrls = false,
            TabStop = false
        };
        logLayout.Controls.Add(_log, 0, 1);
        logCard.Controls.Add(logLayout);
        root.Controls.Add(logCard, 0, 4);

        _pathLabel.Text = _gameRoot;
        _checkButton.Click += async (_, _) => await CheckAsync(showFailureDialog: true);
        _applyButton.Click += async (_, _) => await ApplyOrRecoverAsync();
        _restoreButton.Click += async (_, _) => await RestoreAsync();
        Shown += async (_, _) => await CheckAsync(showFailureDialog: false);
        FormClosing += OnFormClosing;
    }

    private static Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Background,
            Margin = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.Controls.Add(new LogoBadge { Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);

        var titles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Margin = new Padding(0) };
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        titles.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "INVOKERS RU",
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 21f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        titles.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Русификация Invokers: Titan Legacy",
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        header.Controls.Add(titles, 1, 0);
        return header;
    }

    private static CardPanel CreateGameCard(
        out Label pathLabel,
        out Label versionLabel,
        out Label stateLabel,
        out Label statusBadge)
    {
        var card = new CardPanel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Theme.Card };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "УСТАНОВКА ИГРЫ",
            ForeColor = Theme.Gold,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        statusBadge = new Label
        {
            Dock = DockStyle.Fill,
            Text = "ПРОВЕРКА",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Text,
            BackColor = Color.FromArgb(45, 61, 86),
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(8, 0, 0, 2)
        };
        layout.Controls.Add(statusBadge, 1, 0);

        pathLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(pathLabel, 0, 1);
        layout.SetColumnSpan(pathLabel, 2);

        versionLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Версия: определяется…",
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(versionLabel, 0, 2);
        layout.SetColumnSpan(versionLabel, 2);

        stateLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Состояние: ожидается проверка",
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(stateLabel, 0, 3);
        layout.SetColumnSpan(stateLabel, 2);
        card.Controls.Add(layout);
        return card;
    }

    private async Task<bool> CheckAsync(bool showFailureDialog)
    {
        if (_busy) return false;
        SetBusy(true, "Проверка…");
        AppendLog("Проверяем стандартный путь, контрольные суммы, версию и запущенные процессы.");
        try
        {
            AppendLog("Проверяем подписанный канал переводов GitHub. При недоступной сети используется последний проверенный пакет.");
            CliCommandResult update = await _cli.RunAsync("update-refresh", new[] { "--json" });
            AppendLog(update.CombinedOutput.Length == 0
                ? $"Проверка обновлений завершилась с кодом {update.ExitCode} без вывода."
                : update.CombinedOutput);
            CliCommandResult command = await _cli.RunAsync("cache-plan", new[] { "--json" });
            CliPlanResult plan = CliPlanResult.Parse(command);
            string? refreshWarning = CliPlanResult.ExtractUpdateRefreshWarning(update);
            if (plan.UpdateProblem == null && !plan.UpdateProblemBlocksApply && refreshWarning != null)
                plan.UpdateProblem = refreshWarning;
            _lastPlan = plan;
            RenderPlan(plan);
            AppendLog(plan.RawOutput.Length == 0 ? $"CLI завершился с кодом {plan.ExitCode} без вывода." : plan.RawOutput);
            return plan.CanApply || plan.CanRestore || plan.CanRecover;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            _lastPlan = null;
            RenderFailure(exception.Message);
            AppendLog("ОШИБКА: " + exception.Message);
            if (showFailureDialog)
            {
                MessageBox.Show(this, exception.Message, "Проверка не выполнена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return false;
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private async Task RestoreAsync()
    {
        bool actionable = await CheckAsync(showFailureDialog: true);
        CliPlanResult? plan = _lastPlan;
        if (!actionable || plan?.CanRestore != true)
        {
            MessageBox.Show(
                this,
                "Восстановление заблокировано: не найдено точное состояние, созданное этим патчером, либо игра/лаунчер всё ещё запущены.",
                "Операция остановлена",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        DialogResult confirmation = MessageBox.Show(
            this,
            "Будет восстановлена сохранённая оригинальная локализация. Игра и лаунчер должны оставаться закрытыми. Продолжить?",
            "Восстановить оригинал",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        await RunMutationAsync(
            "cache-restore",
            new[] { "--acknowledge-risk", RiskAcknowledgement },
            "Оригинальная локализация восстановлена и состояние патчера очищено.");
    }

    private async Task ApplyOrRecoverAsync()
    {
        bool actionable = await CheckAsync(showFailureDialog: true);
        CliPlanResult? plan = _lastPlan;
        if (!actionable || plan == null)
        {
            MessageBox.Show(
                this,
                "Операция заблокирована. Закройте игру и лаунчер, затем повторите проверку.",
                "Операция остановлена",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (plan.CanRecover)
        {
            DialogResult recovery = MessageBox.Show(
                this,
                "Патчер обнаружил незавершённую транзакцию. Он сверит журнал и контрольные суммы и восстановит только однозначное состояние. Продолжить?",
                "Восстановление после сбоя",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (recovery == DialogResult.Yes)
            {
                await RunMutationAsync(
                    "cache-recover",
                    new[] { "--acknowledge-risk", RiskAcknowledgement },
                    "Состояние патчера восстановлено. Выполните проверку ещё раз.");
            }
            return;
        }

        if (!plan.CanApply)
        {
            MessageBox.Show(
                this,
                "Проверка не разрешила установку. Прочитайте предупреждение в окне, закройте игру и лаунчер и снова нажмите «Проверить».",
                "Установка заблокирована",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        bool compatibleRevision = string.Equals(plan.Profile.Mode, "compatible-revision", StringComparison.Ordinal);
        bool metadataOnly = string.Equals(plan.TranslationUpdateKind, "metadata-only", StringComparison.Ordinal);
        int sourceRows = plan.Profile.AppliedTranslations + plan.Profile.EnglishFallbacks;
        string coverage = $"{plan.Profile.AppliedTranslations:N0} из {sourceRows:N0} переводимых";
        string selectionSummary = metadataOnly
            ? "Текстовый файл уже совпадает с новым проверенным результатом. Файл игры переписываться не будет; патчер атомарно обновит только служебные данные состояния и каталога."
            : compatibleRevision
            ? "Точного опубликованного профиля этой ревизии нет. Патчер проверил неизменный формат и порядок ключей; "
                + $"Все проверки, включая точное совпадение английского исходника и украинской подсказки, прошли {coverage} строк.\n"
                + $"Останется на английском: {plan.Profile.EnglishFallbacks:N0}; пустых/служебных строк: {plan.Profile.BaseFallbacks:N0}."
            : $"Будет установлена русская локализация для версии {plan.Profile.GameVersion}.\n\n"
                + $"Русский текст: {coverage}.\n"
                + $"Останется на английском: {plan.Profile.EnglishFallbacks:N0}; пустых/служебных строк: {plan.Profile.BaseFallbacks:N0}.";
        DialogResult confirmation = MessageBox.Show(
            this,
            "Перед продолжением убедитесь: в игре выбран украинский язык, а игра и лаунчер полностью закрыты.\n\n"
            + selectionSummary + "\n\n"
            + "Это предварительный перевод сообщества: часть формулировок ещё будет редактироваться. Оригинал сохраняется в проверенной резервной копии. Продолжить?",
            metadataOnly
                ? "Обновить служебные данные перевода"
                : string.Equals(plan.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal)
                    ? "Обновить русификацию после обновления игры"
                    : plan.TranslationUpdateAvailable
                        || string.Equals(plan.Status, "PatchSupersededByCatalogUpdate", StringComparison.Ordinal)
                        ? "Обновить русификацию"
                        : "Установить русификацию",
            MessageBoxButtons.YesNo,
            compatibleRevision ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        await RunMutationAsync(
            "cache-apply",
            new[] { "--acknowledge-risk", RiskAcknowledgement },
            "Русская локализация установлена. Запускайте игру с выбранным украинским языком — этот слот используется для русского перевода.");
    }

    private async Task RunMutationAsync(string command, string[] arguments, string successMessage)
    {
        SetBusy(true, "Выполнение…");
        AppendLog($"Запускаем транзакцию: {command}.");
        try
        {
            CliCommandResult result = await _cli.RunAsync(command, arguments);
            AppendLog(result.CombinedOutput);
            if (result.ExitCode != 0)
            {
                string detail = result.CombinedOutput.Length == 0 ? $"Код завершения: {result.ExitCode}" : result.CombinedOutput;
                MessageBox.Show(this, "Операция не выполнена.\n\n" + FriendlyMutationError(detail), "Патчер остановлен", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show(this, successMessage, "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            AppendLog("ОШИБКА: " + exception.Message);
            MessageBox.Show(this, exception.Message, "Операция не выполнена", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
            await CheckAsync(showFailureDialog: false);
        }
    }

    private void RenderPlan(CliPlanResult plan)
    {
        _pathLabel.Text = string.IsNullOrWhiteSpace(plan.CacheRoot) ? _gameRoot : plan.CacheRoot;
        string englishContent = plan.Observed.EnglishContent ?? "не определён";
        string baseContent = plan.Observed.BaseContent ?? "не определён";
        string englishRevision = plan.Observed.EnglishReleaseRevision?.ToString() ?? "?";
        string baseRevision = plan.Observed.BaseReleaseRevision?.ToString() ?? "?";
        string content = $"EN: {englishContent} (rev {englishRevision})   •   UK: {baseContent} (rev {baseRevision})";
        string observedVersion = plan.Observed.GameVersion ?? "не определена";
        bool compatibleRevision = string.Equals(plan.Profile.Mode, "compatible-revision", StringComparison.Ordinal);
        string versionText = compatibleRevision
            ? $"Наблюдаемая версия игры: {observedVersion}"
            : string.Equals(observedVersion, plan.Profile.GameVersion, StringComparison.Ordinal)
                ? $"Версия игры: {observedVersion}"
                : $"Наблюдаемая версия игры: {observedVersion}   •   точный профиль: {plan.Profile.GameVersion}";
        _versionLabel.Text = $"{versionText}   •   контент: {content}   •   патчер: {plan.PatcherVersion}";

        if (plan.CanApply)
        {
            bool translationUpdate = plan.TranslationUpdateAvailable;
            bool metadataOnly = string.Equals(plan.TranslationUpdateKind, "metadata-only", StringComparison.Ordinal);
            bool catalogUpdate = string.Equals(plan.Status, "PatchSupersededByCatalogUpdate", StringComparison.Ordinal);
            bool afterUpdate = translationUpdate
                || catalogUpdate
                || string.Equals(plan.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal);
            SetBadge(compatibleRevision ? "СОВМЕСТИМАЯ ВЕРСИЯ" : afterUpdate ? "НУЖНО ОБНОВИТЬ ПЕРЕВОД" : "ГОТОВО К УСТАНОВКЕ",
                compatibleRevision ? Theme.Warning : afterUpdate ? Theme.Warning : Theme.Green);
            int sourceRows = plan.Profile.AppliedTranslations + plan.Profile.EnglishFallbacks;
            _stateLabel.Text = $"Перевод: {plan.Profile.AppliedTranslations:N0} / {sourceRows:N0} переводимых строк   •   английских: {plan.Profile.EnglishFallbacks:N0}";
            _stateLabel.ForeColor = Theme.Green;
            _noticeLabel.Text = metadataOnly
                ? "Новый проверенный каталог даёт тот же текстовый файл. Патчер не будет переписывать файл игры: обновятся только атомарные служебные данные, чтобы дальнейшее восстановление не зависело от старого каталога."
                : compatibleRevision
                ? $"Точного опубликованного профиля этой ревизии нет. Формат, локали и порядок ключей совместимы; {plan.Profile.AppliedTranslations:N0} из {sourceRows:N0} переводимых строк прошли все проверки, включая точное совпадение английского исходника и украинской подсказки. Остальные {plan.Profile.EnglishFallbacks:N0} останутся на английском; пустых/служебных: {plan.Profile.BaseFallbacks:N0}."
                : translationUpdate || catalogUpdate
                    ? "Найден более свежий проверенный перевод для этой версии игры. Новый файл будет собран из закреплённой копии оригинала и атомарно заменит старый перевод одной операцией."
                    : afterUpdate
                        ? "Игра обновилась и вернула официальный файл. Старую резервную копию патчер сохранит в истории и установит перевод, собранный точно для новой версии."
                        : "Версия, файлы игры, каталог и ожидаемый результат совпадают. Оригинал будет сохранён перед атомарной заменой.";
            if (compatibleRevision && plan.Profile.EnglishFallbacks > 0)
                _noticeLabel.Text += " Когда в GitHub появятся свежие строки для этой ревизии, патчер сможет перевести оставшийся английский текст без обновления EXE.";
            _noticeLabel.Text += CatalogSourceNotice(plan);
            _noticeLabel.ForeColor = afterUpdate ? Theme.Warning : Theme.Text;
        }
        else if (plan.CanRestore)
        {
            bool blockedTranslationUpdate = (plan.TranslationUpdateAvailable && !plan.CanApply)
                || plan.UpdateProblemBlocksApply;
            SetBadge(blockedTranslationUpdate ? "ОБНОВЛЕНИЕ ЗАБЛОКИРОВАНО" : "РУСИФИКАЦИЯ АКТИВНА",
                blockedTranslationUpdate ? Theme.Warning : Theme.Blue);
            _stateLabel.Text = $"Установлено русских строк: {plan.State?.AppliedTranslations ?? plan.Profile.AppliedTranslations:N0}   •   резервная копия проверена";
            _stateLabel.ForeColor = blockedTranslationUpdate ? Theme.Warning : Theme.Blue;
            _noticeLabel.Text = (blockedTranslationUpdate
                    ? "Установленный перевод можно восстановить, но предложенное обновление сейчас нельзя применить: подписанные данные, версия патчера или каталог не прошли текущую проверку. "
                    : "Русификация уже установлена. ")
                + "Оригинал можно восстановить из закреплённой резервной копии; перед восстановлением патчер ещё раз проверит состояние и контрольные суммы."
                + CatalogSourceNotice(plan);
            _noticeLabel.ForeColor = blockedTranslationUpdate ? Theme.Warning : Theme.Text;
        }
        else if (string.Equals(plan.Status, "MissingFiles", StringComparison.Ordinal))
        {
            SetBadge("ИГРА НЕ НАЙДЕНА", Theme.Danger);
            _stateLabel.Text = "Состояние: файлы игры отсутствуют по стандартному пути";
            _stateLabel.ForeColor = Theme.Danger;
            _noticeLabel.Text = $"Клиент не найден в {_gameRoot}. Установка заблокирована; оболочка не ищет случайные каталоги и не изменяет путь вручную.";
            _noticeLabel.ForeColor = Theme.Danger;
        }
        else if (string.Equals(plan.PlanAction, "REFUSE_PATCHER_OR_SIGNED_DATA_NOT_CURRENT", StringComparison.Ordinal))
        {
            bool patcherTooOld = plan.ChannelAuthority?.PatcherDisposition == "TooOld";
            bool expired = plan.Update?.Expired == true;
            bool lkgOnly = string.Equals(plan.Catalog.Source, "LastKnownGood", StringComparison.Ordinal);
            SetBadge(patcherTooOld ? "НУЖЕН НОВЫЙ ПАТЧЕР" : "ДАННЫЕ НЕ ГОТОВЫ", Theme.Warning);
            _stateLabel.Text = patcherTooOld
                ? $"Установлен патчер {plan.PatcherVersion}; канал требует {plan.ChannelAuthority?.MinimumPatcherVersion ?? "новую версию"}"
                : expired ? "Срок действия выбранных подписанных данных истёк"
                : lkgOnly ? "Доступна только предыдущая проверенная копия данных"
                : "Подписанный канал не предоставил пригодный каталог для новой установки";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = patcherTooOld
                ? "Формат данных изменился, и эта версия патчера больше не может установить обновление. Нажмите этот текст, чтобы открыть проверенную страницу Releases. Если русификация уже установлена, восстановление оригинала остаётся доступным."
                : expired ? "Установка заблокирована до получения актуальных подписанных данных. Уже установленный перевод и его резервная копия не изменяются."
                : lkgOnly ? "Предыдущая проверенная копия разрешена только для обслуживания точно записанного установленного артефакта. Для новой установки снова нажмите «Проверить» при доступной сети."
                : "Установка заблокирована до получения пригодного каталога с подтверждённой контрольной суммой. Проверьте сеть и снова нажмите «Проверить».";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else if (!plan.IsVersionRisk
            && (plan.ProcessConflicts.Length > 0
                || string.Equals(plan.PlanAction, "REFUSE_CLOSE_GAME_AND_LAUNCHER", StringComparison.Ordinal)))
        {
            SetBadge("ЗАКРОЙТЕ ИГРУ", Theme.Warning);
            _stateLabel.Text = $"Состояние: обнаружены запущенные процессы ({plan.ProcessConflicts.Length})";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = string.Equals(plan.Status, "RecoveryRequired", StringComparison.Ordinal)
                ? "Есть незавершённая транзакция, но восстановление нельзя запускать, пока игра или лаунчер открыты. Полностью закройте их и нажмите «Проверить»."
                : "Полностью закройте игру и лаунчер, включая значок в системном трее, затем нажмите «Проверить». Патчер сам процессы не завершает.";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else if (string.Equals(plan.Status, "InconsistentState", StringComparison.Ordinal))
        {
            bool tupleMismatch = plan.Diagnostic.Kind is "translation-data" or "structural-boundary";
            SetBadge(tupleMismatch ? "ДАННЫЕ И СОСТОЯНИЕ РАСХОДЯТСЯ" : "СОСТОЯНИЕ НЕ СОГЛАСОВАНО", Theme.Danger);
            _stateLabel.Text = tupleMismatch
                ? $"После записанной установки изменился компонент: {DiagnosticComponentName(plan.Diagnostic.Component)}"
                : "Состояние патчера, журнал или файлы не прошли взаимную проверку";
            _stateLabel.ForeColor = Theme.Danger;
            _noticeLabel.Text = "Это несогласованность локального состояния после обновления игры, ручного изменения или оборванной операции, а не вывод о версии клиента. "
                + DiagnosticComparison(plan)
                + " Принудительная запись и восстановление отключены: не запускайте игру и не меняйте файлы вручную; сохраните журнал проверки и обратитесь в поддержку проекта.";
            _noticeLabel.ForeColor = Theme.Danger;
        }
        else if (plan.IsVersionRisk)
        {
            bool translationData = string.Equals(plan.Diagnostic.Kind, "translation-data", StringComparison.Ordinal);
            SetBadge(translationData ? "НУЖНЫ СВЕЖИЕ ДАННЫЕ ПЕРЕВОДА" : "НУЖНА ПОДДЕРЖКА ФОРМАТА",
                translationData ? Theme.Warning : Theme.Danger);
            _stateLabel.Text = translationData
                ? $"Не совпадают данные перевода: {DiagnosticComponentName(plan.Diagnostic.Component)}"
                : $"Структурная граница: {DiagnosticComponentName(plan.Diagnostic.Component)}";
            _stateLabel.ForeColor = Theme.Danger;
            _noticeLabel.Text = (translationData
                    ? "Версия клиента сама по себе не признана несовместимой. Текущий каталог не может подтвердить перевод для этих данных; дождитесь свежего каталога GitHub и снова нажмите «Проверить». "
                    : "Формат LOC1, фиксированный путь, locale slot, семейство GUID или порядок ключей вышли за поддерживаемые границы. Нужна новая поддержка патчера/данных; принудительная установка отключена. ")
                + DiagnosticComparison(plan)
                + (plan.ProcessConflicts.Length > 0 ? " Игра или лаунчер сейчас также запущены." : string.Empty);
            _noticeLabel.ForeColor = translationData ? Theme.Warning : Theme.Danger;
        }
        else if (string.Equals(plan.Status, "RecoveryRequired", StringComparison.Ordinal))
        {
            SetBadge("НУЖНО ВОССТАНОВЛЕНИЕ", Theme.Warning);
            _stateLabel.Text = "Состояние: обнаружена незавершённая транзакция";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = plan.InstallationWritesEnabled
                ? "Не запускайте игру и не изменяйте её файлы вручную. Нажмите «Восстановить после сбоя»: патчер продолжит только при однозначных контрольных суммах."
                : "Не запускайте игру и не изменяйте её файлы вручную. Эта диагностическая сборка не имеет права записи.";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else if (string.Equals(plan.PlanAction, "REFUSE_MISSING_OR_MISMATCHED_CATALOG", StringComparison.Ordinal))
        {
            SetBadge("ОБНОВИТЕ ПЕРЕВОД", Theme.Warning);
            _stateLabel.Text = "Состояние: файл перевода отсутствует или не совпадает с выбранными проверенными данными";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = "Не совпадают именно данные перевода, а не версия клиента. "
                + DiagnosticComparison(plan)
                + " Нажмите «Проверить» при доступной сети; патчер повторно скачает только каталог перевода.";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else if (string.Equals(plan.PlanAction, "REFUSE_DEV_WRITES_DISABLED", StringComparison.Ordinal))
        {
            SetBadge("ДИАГНОСТИЧЕСКАЯ СБОРКА", Theme.Warning);
            _stateLabel.Text = $"Версия {plan.Profile.GameVersion} распознана, но эта сборка не имеет права записи";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = "Проверка успешна. Для установки нужен официальный пакет патчера со встроенным подписанным профилем совместимости.";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else
        {
            SetBadge("УСТАНОВКА ЗАБЛОКИРОВАНА", Theme.Warning);
            _stateLabel.Text = $"Состояние: {DisplayStatus(plan.Status)}";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = "Проверка не разрешила запись. Подробности сохранены в журнале.";
            _noticeLabel.ForeColor = Theme.Warning;
        }

        // A failed freshness check and a patcher-version warning remain relevant in every state,
        // including the exact refusal states where the player is told to wait for newer data.
        _noticeLabel.Text += NonBlockingRefreshNotice(plan) + PatcherVersionNotice(plan);
        UpdateButtons();
    }

    private void RenderFailure(string message)
    {
        SetBadge("ОШИБКА ПРОВЕРКИ", Theme.Danger);
        _versionLabel.Text = "Версия: не подтверждена";
        _stateLabel.Text = "Состояние: проверка не завершена";
        _stateLabel.ForeColor = Theme.Danger;
        _noticeLabel.Text = message;
        _noticeLabel.ForeColor = Theme.Danger;
        UpdateButtons();
    }

    private void SetBadge(string text, Color color)
    {
        _statusBadge.Text = text;
        _statusBadge.BackColor = Color.FromArgb(
            Math.Max(0, color.R - 80),
            Math.Max(0, color.G - 80),
            Math.Max(0, color.B - 80));
        _statusBadge.ForeColor = color;
    }

    private void SetBusy(bool busy, string label)
    {
        _busy = busy;
        _busyLabel.Text = label;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _checkButton.Enabled = !_busy;
        _applyButton.Text = _lastPlan?.CanRecover == true
            ? "Восстановить после сбоя"
            : string.Equals(_lastPlan?.TranslationUpdateKind, "metadata-only", StringComparison.Ordinal)
                ? "Обновить служебные данные"
            : _lastPlan?.TranslationUpdateAvailable == true
                || string.Equals(_lastPlan?.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal)
                || string.Equals(_lastPlan?.Status, "PatchSupersededByCatalogUpdate", StringComparison.Ordinal)
                ? "Обновить русификацию"
                : "Установить русификацию";
        _applyButton.Enabled = !_busy && (_lastPlan?.CanApply == true || _lastPlan?.CanRecover == true);
        _restoreButton.Enabled = !_busy && _lastPlan?.CanRestore == true;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string timestamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        if (_log.TextLength > 0) _log.AppendText(Environment.NewLine + Environment.NewLine);
        _log.AppendText($"[{timestamp}] {text.Trim()}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private static string PatcherVersionNotice(CliPlanResult plan)
    {
        return plan.ChannelAuthority?.PatcherDisposition switch
        {
            "UpdateAvailable" => $" Доступен патчер {plan.ChannelAuthority.LatestPatcherVersion}; текущая версия пока совместима.",
            "TooOld" when plan.CanRestore || plan.CanRecover => $" Для новых данных нужен патчер {plan.ChannelAuthority.MinimumPatcherVersion}; восстановление оригинала по-прежнему доступно.",
            "TooOld" => $" Для новых данных нужен патчер {plan.ChannelAuthority.MinimumPatcherVersion}.",
            _ => string.Empty
        };
    }

    private static string NonBlockingRefreshNotice(CliPlanResult plan)
    {
        if (plan.UpdateProblem == null || plan.UpdateProblemBlocksApply) return string.Empty;
        return plan.Catalog.ExactMatch
            ? " Свежесть данных GitHub сейчас не удалось подтвердить; используется уже проверенный локальный или встроенный каталог."
            : " Свежесть данных GitHub сейчас не удалось подтвердить.";
    }

    private static string CatalogSourceNotice(CliPlanResult plan)
    {
        if (!plan.Catalog.ExactMatch) return string.Empty;
        return plan.Catalog.Source switch
        {
            "Remote" => " Выбранный каталог: актуальный пакет GitHub.",
            "CachedCurrent" => " Выбранный каталог: ранее проверенный локальный кэш GitHub.",
            "LastKnownGood" => " Выбранный каталог: предыдущая проверенная копия.",
            "embedded" => " Выбранный каталог: встроенная проверенная копия.",
            _ => string.Empty
        };
    }

    private static string DiagnosticComponentName(string component)
    {
        return component switch
        {
            "english-source" => "английский источник (EN)",
            "ukrainian-base" => "украинская база (UK)",
            "version-stamp" => "маркер версии кэша",
            "catalog-sha256" => "каталог перевода",
            "source-hint-coverage" => "совпадение EN-исходников и UK-подсказок",
            "loc1-schema" => "схема LOC1",
            "content-guid" => "семейство контента GUID",
            "locale-slot" => "locale slot EN/UK",
            "ordered-keyset" => "порядок ключей LOC1",
            "missing-files" => "фиксированный набор EN/UK/stamp",
            "official-base-refresh" => "официальный UK-файл после обновления",
            "journal" => "журнал незавершённой операции",
            "journal-authentication" => "проверка журнала незавершённой операции",
            "patch-state" => "записанное состояние патчера",
            _ => component
        };
    }

    private static string DiagnosticComparison(CliPlanResult plan)
    {
        if (plan.Diagnostic.Current == null && plan.Diagnostic.Expected == null) return string.Empty;
        return plan.Diagnostic.Component switch
        {
            "english-source" => $"EN сейчас {FormatCorpus(plan.Observed.EnglishContent, plan.Observed.EnglishReleaseRevision, plan.Observed.EnglishLocaleRevision)}; "
                + $"перевод подготовлен для {FormatCorpus(plan.Profile.EnglishContent, plan.Profile.EnglishReleaseRevision, plan.Profile.EnglishLocaleRevision)}.",
            "ukrainian-base" => $"UK сейчас {FormatCorpus(plan.Observed.BaseContent, plan.Observed.BaseReleaseRevision, plan.Observed.BaseLocaleRevision)}; "
                + $"перевод подготовлен для {FormatCorpus(plan.Profile.BaseContent, plan.Profile.BaseReleaseRevision, plan.Profile.BaseLocaleRevision)}.",
            "version-stamp" => $"Маркер игры сейчас {plan.Observed.GameVersion ?? "не читается"}; профиль перевода ожидает {plan.Profile.GameVersion}.",
            "catalog-sha256" => $"Выбранный каталог: {FormatCatalogSource(plan.Catalog.Source)}, отпечаток {ShortDigest(plan.Catalog.Sha256)}; "
                + $"профиль перевода ожидает {ShortDigest(plan.Profile.CatalogSha256)}.",
            "source-hint-coverage" => CoverageComparison(plan),
            "loc1-schema" => $"Схема LOC1 сейчас EN {plan.Observed.EnglishSchema?.ToString() ?? "не читается"}, UK {plan.Observed.BaseSchema?.ToString() ?? "не читается"}; поддерживается схема {plan.Profile.Loc1Schema}.",
            "content-guid" => $"Семейство контента сейчас EN {ShortGuid(plan.Observed.EnglishContentGuid)}, UK {ShortGuid(plan.Observed.BaseContentGuid)}; ожидается {ShortGuid(plan.Profile.ContentGuid)}.",
            "locale-slot" => $"Locale slot сейчас EN {plan.Observed.EnglishLocaleId?.ToString() ?? "?"}, UK {plan.Observed.BaseLocaleId?.ToString() ?? "?"}; ожидается EN {plan.Profile.EnglishLocaleId}, UK {plan.Profile.BaseLocaleId}.",
            "ordered-keyset" => $"Порядок ключей LOC1 сейчас {ShortDigest(plan.Observed.OrderedKeysetSha256)}; ожидается {ShortDigest(plan.Profile.OrderedKeysetSha256)}.",
            "missing-files" => "Не удалось прочитать полный фиксированный набор EN/UK/stamp по пути установки игры.",
            "official-base-refresh" => $"Официальный UK-файл сейчас {FormatCorpus(plan.Observed.BaseContent, plan.Observed.BaseReleaseRevision, plan.Observed.BaseLocaleRevision)}; "
                + "состояние установленного перевода относится к предыдущему официальному UK-файлу.",
            "journal" => "Журнал незавершённой операции не прошёл аутентификацию состояния.",
            "journal-authentication" => "Найден журнал незавершённой операции, но его нельзя однозначно связать с проверенным профилем, резервной копией и текущими файлами.",
            "patch-state" => "Записанное состояние патчера не совпадает с текущими файлами и проверенным профилем.",
            _ => $"Компонент: {DiagnosticComponentName(plan.Diagnostic.Component)}; текущее и ожидаемое значения не совпали."
        };
    }

    private static string FormatCorpus(string? content, uint? releaseRevision, uint? localeRevision)
    {
        if (content == null) return "не читается";
        string release = releaseRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        string revision = localeRevision.HasValue ? localeRevision.Value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "????????";
        return $"{content} (release {release}, revision {revision})";
    }

    private static string CoverageComparison(CliPlanResult plan)
    {
        int sourceRows = checked(plan.Profile.AppliedTranslations + plan.Profile.EnglishFallbacks);
        if (plan.Profile.AppliedTranslations == 0)
            return $"Ни одна из {sourceRows:N0} переводимых строк не прошла одновременную проверку EN-исходника и UK-подсказки; нужен свежий каталог перевода.";
        return $"Все проверки прошли {plan.Profile.AppliedTranslations:N0} из {sourceRows:N0} переводимых строк; {plan.Profile.EnglishFallbacks:N0} останутся на английском до обновления каталога.";
    }

    private static string FormatCatalogSource(string? source) => source switch
    {
        "Remote" => "GitHub",
        "CachedCurrent" => "локальный кэш GitHub",
        "LastKnownGood" => "предыдущая проверенная копия",
        "embedded" => "встроенная копия",
        "ChannelHead" => "метаданные канала GitHub",
        _ => "не найден"
    };

    private static string ShortDigest(string? digest)
    {
        return string.IsNullOrWhiteSpace(digest) ? "не читается" : digest[..Math.Min(12, digest.Length)];
    }

    private static string ShortGuid(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "не читается" : value[..Math.Min(8, value.Length)];
    }

    private void OpenVerifiedPatcherPage()
    {
        string? url = _lastPlan?.ChannelAuthority?.DownloadPage;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            AppendLog("Не удалось открыть страницу Releases: " + exception.Message);
        }
    }

    private static string DisplayStatus(string status)
    {
        return status switch
        {
            "UnknownBuild" => "наблюдаемые файлы не прошли проверку совместимости",
            "MissingFiles" => "файлы игры не найдены по стандартному пути",
            "InconsistentState" => "файлы изменены после предыдущей операции",
            "RecoveryRequired" => "предыдущая транзакция требует восстановления",
            "CompatibleOriginal" => "оригинальные файлы найдены, но запись не разрешена",
            "PatchSupersededByOfficialUpdate" => "игра обновилась и вернула оригинальную локализацию",
            "PatchSupersededByCatalogUpdate" => "для установленной русификации доступен более свежий перевод",
            "PatchedByThisTool" => "русская локализация установлена",
            _ => string.IsNullOrWhiteSpace(status) ? "не удалось распознать ответ патчера" : status
        };
    }

    private static string FriendlyMutationError(string detail)
    {
        if (detail.Contains("Another patcher process is already working", StringComparison.OrdinalIgnoreCase))
            return "Другой экземпляр патчера уже выполняет операцию. Дождитесь её завершения и нажмите «Проверить».";
        if (detail.Contains("Patcher lock file is held", StringComparison.OrdinalIgnoreCase))
            return "Другой экземпляр патчера уже выполняет операцию. Дождитесь её завершения и нажмите «Проверить».";
        if (detail.Contains("Close the game and launcher", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("running game/launcher", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Close game/launcher processes", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Close these game/launcher processes", StringComparison.OrdinalIgnoreCase))
            return "Полностью закройте игру и лаунчер, включая значок в системном трее, затем повторите проверку.";
        if (detail.Contains("Translation catalog", StringComparison.OrdinalIgnoreCase))
            return "Файл перевода отсутствует или повреждён. Нажмите «Проверить» при доступной сети — патчер повторно скачает данные перевода.";
        if (detail.Contains("exact compatible original tuple", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("pinned", StringComparison.OrdinalIgnoreCase))
            return "Проверенные исходные файлы или состояние игры изменились. Нажмите «Проверить»: патчер продолжит только для точного профиля или строго совместимой ревизии с английским резервом.";
        return detail.StartsWith("ERROR: ", StringComparison.OrdinalIgnoreCase) ? detail[7..].Trim() : detail;
    }

    private static bool IsExpectedOperationException(Exception exception)
    {
        return exception is IOException
            || exception is InvalidOperationException
            || exception is UnauthorizedAccessException
            || exception is Win32Exception;
    }


    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (!_busy) return;
        eventArgs.Cancel = true;
        MessageBox.Show(
            this,
            "Дождитесь завершения текущей проверки или транзакции. Игра и её процессы оболочка не запускает и не завершает.",
            "Операция выполняется",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
