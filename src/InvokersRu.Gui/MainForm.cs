using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InvokersRu.Gui;

internal sealed class MainForm : Form
{
    private const string RiskAcknowledgement = "I_ACCEPT_LOCAL_MODIFICATION";

    private readonly CliRunner _cli = new();
    private readonly string _gameRoot;
    private readonly string _catalogPath;
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
        _catalogPath = ResolveCatalogPath();

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
            CliCommandResult command = await _cli.RunAsync("cache-plan", new[] { "--json" });
            CliPlanResult plan = CliPlanResult.Parse(command);
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
                "Точная версия игры или файлы перевода не подтверждены. Принудительной установки у этого патчера нет.",
                "Установка заблокирована",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            RequireCatalog();
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            AppendLog("ОШИБКА: " + exception.Message);
            MessageBox.Show(this, exception.Message, "Файл перевода не найден", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string coverage = $"{plan.Profile.AppliedTranslations:N0} из {plan.Profile.EntryCount:N0}";
        DialogResult confirmation = MessageBox.Show(
            this,
            "Перед продолжением убедитесь: в игре выбран украинский язык, а игра и лаунчер полностью закрыты.\n\n"
            + $"Будет установлена русская локализация для версии {plan.Profile.GameVersion}.\n\n"
            + $"Русский текст: {coverage}.\n"
            + $"Останется на английском: {plan.Profile.EnglishFallbacks:N0}; пустых/служебных строк: {plan.Profile.BaseFallbacks:N0}.\n\n"
            + "Это предварительный перевод сообщества: часть формулировок ещё будет редактироваться. Оригинал сохраняется в проверенной резервной копии. Продолжить?",
            string.Equals(plan.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal)
                ? "Обновить русификацию после обновления игры"
                : "Установить русификацию",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        await RunMutationAsync(
            "cache-apply",
            new[] { "--translations", _catalogPath, "--acknowledge-risk", RiskAcknowledgement },
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
        string content = plan.Observed.EnglishContent ?? "не определён";
        string observedVersion = plan.Observed.GameVersion ?? "не определена";
        string versionText = string.Equals(observedVersion, plan.Profile.GameVersion, StringComparison.Ordinal)
            ? $"Версия игры: {observedVersion}"
            : $"Версия игры: {observedVersion}   •   поддерживается: {plan.Profile.GameVersion}";
        _versionLabel.Text = $"{versionText}   •   контент: {content}   •   патчер: {plan.PatcherVersion}";

        if (plan.CanApply)
        {
            bool afterUpdate = string.Equals(plan.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal);
            SetBadge(afterUpdate ? "НУЖНО ОБНОВИТЬ ПЕРЕВОД" : "ГОТОВО К УСТАНОВКЕ", afterUpdate ? Theme.Warning : Theme.Green);
            _stateLabel.Text = $"Перевод: {plan.Profile.AppliedTranslations:N0} / {plan.Profile.EntryCount:N0} строк   •   английских: {plan.Profile.EnglishFallbacks:N0}";
            _stateLabel.ForeColor = Theme.Green;
            _noticeLabel.Text = afterUpdate
                ? "Игра обновилась и вернула официальный файл. Старую резервную копию патчер сохранит в истории и установит перевод, собранный точно для новой версии."
                : "Версия, файлы игры, каталог и ожидаемый результат совпадают. Оригинал будет сохранён перед атомарной заменой.";
            _noticeLabel.ForeColor = afterUpdate ? Theme.Warning : Theme.Text;
        }
        else if (plan.CanRestore)
        {
            SetBadge("РУСИФИКАЦИЯ АКТИВНА", Theme.Blue);
            _stateLabel.Text = $"Установлено русских строк: {plan.State?.AppliedTranslations ?? plan.Profile.AppliedTranslations:N0}   •   резервная копия проверена";
            _stateLabel.ForeColor = Theme.Blue;
            _noticeLabel.Text = "Русификация уже установлена. Оригинал можно восстановить из закреплённой резервной копии.";
            _noticeLabel.ForeColor = Theme.Text;
        }
        else if (string.Equals(plan.Status, "MissingFiles", StringComparison.Ordinal))
        {
            SetBadge("ИГРА НЕ НАЙДЕНА", Theme.Danger);
            _stateLabel.Text = "Состояние: файлы игры отсутствуют по стандартному пути";
            _stateLabel.ForeColor = Theme.Danger;
            _noticeLabel.Text = $"Клиент не найден в {_gameRoot}. Установка заблокирована; оболочка не ищет случайные каталоги и не изменяет путь вручную.";
            _noticeLabel.ForeColor = Theme.Danger;
        }
        else if (plan.IsVersionRisk)
        {
            SetBadge("ВЕРСИЯ НЕ ПОДДЕРЖИВАЕТСЯ", Theme.Danger);
            _stateLabel.Text = $"Состояние: {DisplayStatus(plan.Status)}";
            _stateLabel.ForeColor = Theme.Danger;
            _noticeLabel.Text = "Версия или контрольные суммы расходятся с проверенной сборкой. Перевод для старой версии мог бы оставить новые тексты на английском или повредить таблицу, поэтому принудительная установка отключена. Обновите патчер."
                + (plan.ProcessConflicts.Length > 0 ? " Игра или лаунчер сейчас также запущены." : string.Empty);
            _noticeLabel.ForeColor = Theme.Danger;
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
        else if (plan.ProcessConflicts.Length > 0 || string.Equals(plan.PlanAction, "REFUSE_CLOSE_GAME_AND_LAUNCHER", StringComparison.Ordinal))
        {
            SetBadge("ЗАКРОЙТЕ ИГРУ", Theme.Warning);
            _stateLabel.Text = $"Состояние: обнаружены запущенные процессы ({plan.ProcessConflicts.Length})";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = "Полностью закройте игру и лаунчер, включая значок в системном трее, затем нажмите «Проверить». Патчер сам процессы не завершает.";
            _noticeLabel.ForeColor = Theme.Warning;
        }
        else if (string.Equals(plan.PlanAction, "REFUSE_MISSING_OR_MISMATCHED_CATALOG", StringComparison.Ordinal))
        {
            SetBadge("ОБНОВИТЕ ПЕРЕВОД", Theme.Warning);
            _stateLabel.Text = "Состояние: файл перевода отсутствует или не совпадает с этой версией патчера";
            _stateLabel.ForeColor = Theme.Warning;
            _noticeLabel.Text = "Переустановите или обновите патчер целиком. Смешивать EXE, профиль и каталог из разных выпусков нельзя.";
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
            : string.Equals(_lastPlan?.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal)
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

    private static string ResolveCatalogPath()
    {
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        string[] candidates =
        {
            Path.Combine(root, "translations", "ru_RU.jsonl"),
            Path.Combine(root, "ru_RU.jsonl")
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }

    private void RequireCatalog()
    {
        if (!File.Exists(_catalogPath))
            throw new FileNotFoundException($"Файл перевода не найден рядом с патчером: {_catalogPath}", _catalogPath);
        if ((File.GetAttributes(_catalogPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Файл перевода не должен быть ссылкой или точкой повторной обработки.");
    }

    private static string DisplayStatus(string status)
    {
        return status switch
        {
            "UnknownBuild" => "неизвестная или обновлённая версия игры",
            "MissingFiles" => "файлы игры не найдены по стандартному пути",
            "InconsistentState" => "файлы изменены после предыдущей операции",
            "RecoveryRequired" => "предыдущая транзакция требует восстановления",
            "CompatibleOriginal" => "оригинальные файлы найдены, но запись не разрешена",
            "PatchSupersededByOfficialUpdate" => "игра обновилась и вернула оригинальную локализацию",
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
            return "Файл перевода не совпадает с проверенной версией патчера. Обновите или переустановите патчер.";
        if (detail.Contains("exact compatible original tuple", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("pinned", StringComparison.OrdinalIgnoreCase))
            return "Версия или файлы игры изменились. Принудительная установка отключена — дождитесь обновления патчера.";
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
