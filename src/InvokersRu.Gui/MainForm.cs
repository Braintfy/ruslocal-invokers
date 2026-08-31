using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InvokersRu.Gui;

internal sealed class MainForm : Form
{
    private const string RiskAcknowledgement = "I_ACCEPT_LOCAL_MODIFICATION";
    private const string SettingsRegistryPath = @"Software\InvokersRu";
    private const string CacheRootSettingName = "RuntimeCacheRoot";

    private readonly CliRunner _cli = new();
    private string _gameRoot;
    private readonly Label _pathLabel;
    private readonly Label _versionLabel;
    private readonly Label _stateLabel;
    private readonly Label _statusBadge;
    private readonly Label _noticeLabel;
    private readonly RichTextBox _log;
    private readonly ActionButton _checkButton;
    private readonly ActionButton _applyButton;
    private readonly ActionButton _restoreButton;
    private readonly Button _browseButton;
    private readonly Label _busyLabel;
    private readonly Button _updatePatcherButton;
    private VerifiedPatcherInstaller? _pendingInstaller;
    private CliPlanResult? _lastPlan;
    private bool _busy;

    public MainForm()
    {
        _gameRoot = LoadSavedCacheRoot();
        Text = "InvokersRu — русский язык для Titan Legacy";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 720);
        MinimumSize = new Size(680, 580);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
        var root = VerticalLayout();
        root.Padding = new Padding(24);
        scroll.Controls.Add(root);
        Controls.Add(scroll);
        root.Controls.Add(CreateHeader(out _updatePatcherButton));

        var gameCard = CreateGameCard(out _pathLabel, out _versionLabel, out _stateLabel, out _statusBadge, out _browseButton);
        root.Controls.Add(gameCard);

        var helpCard = NewCard();
        var help = VerticalLayout();
        help.Controls.Add(FlowText("ПЕРЕД УСТАНОВКОЙ", Theme.Gold, bold: true));
        help.Controls.Add(FlowText(
            "1. Выберите украинский язык в игре и дождитесь загрузки.\n"
            + "2. Полностью закройте игру и лаунчер.\n"
            + "3. Нажмите «Установить перевод» ниже.", Theme.Text));
        help.Controls.Add(FlowText("ЧТО СДЕЛАТЬ СЕЙЧАС", Theme.Muted, bold: true));
        _noticeLabel = FlowText("Проверяем игру и доступные обновления…", Theme.Text);
        help.Controls.Add(_noticeLabel);
        helpCard.Controls.Add(help);
        root.Controls.Add(helpCard);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = new Padding(0, 0, 0, 10)
        };
        _checkButton = NewAction("Проверить", Theme.Blue, Theme.Blue, Color.White);
        _applyButton = NewAction("Установить перевод", Theme.Gold, Theme.GoldHover, Theme.Background);
        _restoreButton = NewAction("Вернуть оригинал", Color.FromArgb(36, 52, 78), Theme.CardHover, Theme.Text);
        _applyButton.Enabled = _restoreButton.Enabled = false;
        actions.Controls.AddRange(new Control[] { _checkButton, _applyButton, _restoreButton });
        root.Controls.Add(actions);
        _busyLabel = FlowText(string.Empty, Theme.Muted);
        root.Controls.Add(_busyLabel);

        var detailsCard = NewCard();
        var detailsLayout = VerticalLayout();
        var detailsActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var toggle = NewAction("Показать подробности", Theme.Card, Theme.CardHover, Theme.Text);
        var copy = NewAction("Скопировать для поддержки", Theme.Card, Theme.CardHover, Theme.Text);
        detailsActions.Controls.AddRange(new Control[] { toggle, copy });
        detailsLayout.Controls.Add(FlowText("ПОДРОБНОСТИ ДЛЯ ПОДДЕРЖКИ", Theme.Muted, bold: true));
        detailsLayout.Controls.Add(FlowText(
            "Если что-то не работает, скопируйте эти сведения и отправьте автору. Для обычной установки они не нужны.", Theme.Muted));
        detailsLayout.Controls.Add(detailsActions);
        _log = new RichTextBox
        {
            Dock = DockStyle.Top, Height = 240, Visible = false, ReadOnly = true,
            BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(12, 21, 35),
            ForeColor = Theme.Text, Font = new Font("Consolas", 9f),
            DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(0, 10, 0, 0)
        };
        toggle.Click += (_, _) =>
        {
            _log.Visible = !_log.Visible;
            toggle.Text = _log.Visible ? "Скрыть подробности" : "Показать подробности";
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_log.TextLength == 0 ? "Проверка ещё не выполнена." : _log.Text);
                copy.Text = "Скопировано";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(this, "Не удалось открыть буфер обмена. Попробуйте ещё раз.",
                    "Поддержка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        detailsLayout.Controls.Add(_log);
        detailsCard.Controls.Add(detailsLayout);
        root.Controls.Add(detailsCard);

        _pathLabel.Text = _gameRoot;
        _browseButton.Click += async (_, _) => await FindOrChooseCacheRootAsync();
        _checkButton.Click += async (_, _) =>
        {
            if (!await CheckPatcherUpdateAsync(showCurrent: false))
                await CheckAsync(showFailureDialog: true);
        };
        _updatePatcherButton.Click += async (_, _) => await CheckPatcherUpdateAsync(showCurrent: true);
        _applyButton.Click += async (_, _) => await ApplyOrRecoverAsync();
        _restoreButton.Click += async (_, _) => await RestoreAsync();
        Shown += async (_, _) => await InitialCheckAsync();
        FormClosing += OnFormClosing;
    }

    private static TableLayoutPanel VerticalLayout() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Margin = new Padding(0), ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) }
    };

    private static CardPanel NewCard() => new()
    {
        Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(18), Margin = new Padding(0, 0, 0, 14)
    };

    private static Label FlowText(string text, Color color, bool bold = false)
    {
        var label = new Label
        {
            Text = text, AutoSize = true, Dock = DockStyle.Top, ForeColor = color,
            UseMnemonic = false, AutoEllipsis = false, Margin = new Padding(0, 0, 0, 8),
            MaximumSize = new Size(760, 0), TextAlign = ContentAlignment.TopLeft,
            Font = new Font("Segoe UI", bold ? 9f : 10f, bold ? FontStyle.Bold : FontStyle.Regular)
        };
        label.ParentChanged += (_, _) =>
        {
            if (label.Parent is not Control parent) return;
            void ResizeLabel() => label.MaximumSize = new Size(
                Math.Max(160, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal), 0);
            parent.SizeChanged += (_, _) => ResizeLabel();
            ResizeLabel();
        };
        return label;
    }

    private static ActionButton NewAction(string text, Color normal, Color hover, Color foreground) => new(text, normal, hover, foreground)
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(140, 44), Padding = new Padding(14, 8, 14, 8),
        Margin = new Padding(0, 0, 10, 8)
    };

    private static Control CreateHeader(out Button updateButton)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Margin = new Padding(0, 0, 0, 16)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new LogoBadge { Anchor = AnchorStyles.Top | AnchorStyles.Left }, 0, 0);
        var titles = VerticalLayout();
        var title = FlowText("INVOKERS RU", Theme.Text, bold: true);
        title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
        titles.Controls.Add(title);
        titles.Controls.Add(FlowText("Русский язык для Invokers: Titan Legacy", Theme.Muted));
        header.Controls.Add(titles, 1, 0);
        updateButton = NewAction("Обновить патчер", Theme.Card, Theme.CardHover, Theme.Text);
        header.Controls.Add(updateButton, 2, 0);
        return header;
    }

    private static CardPanel CreateGameCard(out Label pathLabel, out Label versionLabel,
        out Label stateLabel, out Label statusBadge, out Button browseButton)
    {
        var card = NewCard();
        var layout = VerticalLayout();
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(FlowText("ВАША ИГРА", Theme.Gold, bold: true), 0, 0);
        browseButton = NewAction("Найти / выбрать папку", Color.FromArgb(36, 52, 78), Theme.CardHover, Theme.Text);
        heading.Controls.Add(browseButton, 1, 0);
        layout.Controls.Add(heading);
        statusBadge = FlowText("Проверяем игру…", Theme.Text, bold: true);
        statusBadge.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        statusBadge.Padding = new Padding(8);
        layout.Controls.Add(statusBadge);
        stateLabel = FlowText("Это может занять несколько секунд.", Theme.Muted);
        layout.Controls.Add(stateLabel);
        versionLabel = FlowText("Версия игры: определяется…", Theme.Muted);
        layout.Controls.Add(versionLabel);
        layout.Controls.Add(FlowText("ПАПКА ФАЙЛОВ ЯЗЫКА", Theme.Muted, bold: true));
        pathLabel = FlowText(string.Empty, Theme.Muted);
        pathLabel.Font = new Font("Segoe UI", 9f);
        layout.Controls.Add(pathLabel);
        card.Controls.Add(layout);
        return card;
    }

    private async Task InitialCheckAsync()
    {
        if (await CheckPatcherUpdateAsync(showCurrent: false)) return;
        if (!HasCacheTuple(_gameRoot))
        {
            SetBusy(true, "Поиск игры…");
            try
            {
                CacheSearchResult quickSearch = await SearchCacheRootsAsync(
                    new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
                    TimeSpan.FromSeconds(8));
                if (quickSearch.Paths.Count == 1)
                {
                    SelectCacheRoot(quickSearch.Paths[0]);
                    AppendLog($"Папка локализации найдена автоматически: {_gameRoot}");
                }
            }
            finally
            {
                SetBusy(false, string.Empty);
            }
        }

        await CheckAsync(showFailureDialog: false);
    }

    private async Task FindOrChooseCacheRootAsync()
    {
        if (_busy) return;
        SetBusy(true, "Поиск на дисках…");
        AppendLog("Ищем точный набор файлов локализации на доступных локальных дисках.");
        CacheSearchResult search;
        try
        {
            search = await SearchCacheRootsAsync(GetFixedDriveRoots(), TimeSpan.FromSeconds(60));
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        string? selectedRoot = search.Paths.Count switch
        {
            0 => null,
            1 => search.Paths[0],
            _ => ChooseFromFoundRoots(search.Paths)
        };
        if (selectedRoot != null)
        {
            SelectCacheRoot(selectedRoot);
            AppendLog($"Выбрана найденная папка локализации: {selectedRoot}");
            await CheckAsync(showFailureDialog: true);
            return;
        }

        string timeoutNotice = search.TimedOut
            ? "Поиск остановлен через 60 секунд. "
            : string.Empty;
        DialogResult manual = MessageBox.Show(
            this,
            timeoutNotice + "Автоматический поиск не нашёл подходящую папку. Выбрать папку i18n вручную?",
            "Папка локализации не найдена",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1);
        if (manual == DialogResult.Yes)
            await ChooseCacheRootManuallyAsync();
    }

    private async Task ChooseCacheRootManuallyAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку i18n, в которой находятся dl_en_US.bin, dl_uk_UA.bin и dl_uk_UA.bin.ver.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            InitialDirectory = Directory.Exists(_gameRoot)
                ? _gameRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string? selectedRoot = ResolveSelectedCacheRoot(dialog.SelectedPath);
        if (selectedRoot == null)
        {
            MessageBox.Show(
                this,
                "В выбранной папке не найден полный набор файлов локализации. Выберите папку i18n, содержащую dl_en_US.bin, dl_uk_UA.bin и dl_uk_UA.bin.ver.\n\nПеред выбором включите украинский язык в игре и дождитесь загрузки.",
                "Папка локализации не найдена",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SelectCacheRoot(selectedRoot);
        AppendLog($"Выбрана папка локализации: {selectedRoot}");
        await CheckAsync(showFailureDialog: true);
    }

    private void SelectCacheRoot(string selectedRoot)
    {
        _gameRoot = Path.GetFullPath(selectedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SaveCacheRoot(_gameRoot);
        _pathLabel.Text = _gameRoot;
        _lastPlan = null;
        UpdateButtons();
    }

    private static async Task<CacheSearchResult> SearchCacheRootsAsync(
        IReadOnlyCollection<string> roots,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await Task.Run(() => SearchCacheRoots(roots, cancellation.Token));
    }

    private static CacheSearchResult SearchCacheRoots(
        IReadOnlyCollection<string> roots,
        CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MaxRecursionDepth = 18
        };
        foreach (string root in roots)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string stampPath in Directory.EnumerateFiles(root, "dl_uk_UA.bin.ver", options))
                {
                    if (cancellationToken.IsCancellationRequested || found.Count >= 32) break;
                    string? candidate = Path.GetDirectoryName(stampPath);
                    if (candidate != null && HasCacheTuple(candidate))
                    {
                        string normalized = Path.GetFullPath(candidate)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (seen.Add(normalized)) found.Add(normalized);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Inaccessible trees are skipped; discovery never weakens the later exact file validation.
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return new CacheSearchResult(found, cancellationToken.IsCancellationRequested);
    }

    private static IReadOnlyCollection<string> GetFixedDriveRoots()
    {
        var roots = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive that cannot be inspected is not a valid automatic candidate.
            }
        }

        return roots;
    }

    private string? ChooseFromFoundRoots(IReadOnlyList<string> paths)
    {
        using var dialog = new Form
        {
            Text = "Выберите установленную игру",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(720, 330),
            MinimumSize = new Size(620, 280),
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = Font,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Найдено несколько папок локализации. Выберите используемую установку:",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            HorizontalScrollbar = true
        };
        foreach (string path in paths) list.Items.Add(path);
        list.SelectedIndex = 0;
        layout.Controls.Add(list, 0, 1);
        layout.SetColumnSpan(list, 2);
        var select = new Button { Text = "Выбрать", Dock = DockStyle.Fill, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(select, 0, 2);
        layout.Controls.Add(cancel, 1, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = select;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? list.SelectedItem as string : null;
    }

    private static string? ResolveSelectedCacheRoot(string selectedPath)
    {
        string selected;
        try
        {
            selected = Path.GetFullPath(selectedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string[] candidates =
        {
            selected,
            Path.Combine(selected, "i18n"),
            Path.Combine(selected, "Invokers", "i18n"),
            Path.Combine(selected, "Hit_Zone", "Invokers", "i18n")
        };
        foreach (string candidate in candidates)
        {
            if (HasCacheTuple(candidate))
            {
                return Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        return null;
    }

    private static bool HasCacheTuple(string candidate)
    {
        return File.Exists(Path.Combine(candidate, "dl_en_US.bin"))
            && File.Exists(Path.Combine(candidate, "dl_uk_UA.bin"))
            && File.Exists(Path.Combine(candidate, "dl_uk_UA.bin.ver"));
    }

    private sealed record CacheSearchResult(IReadOnlyList<string> Paths, bool TimedOut);

    private static string LoadSavedCacheRoot()
    {
        string defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Hit_Zone", "Invokers", "i18n");
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath, writable: false);
            string? saved = key?.GetValue(CacheRootSettingName) as string;
            if (!string.IsNullOrWhiteSpace(saved) && saved.Length <= 1024 && Path.IsPathFullyQualified(saved))
                return Path.GetFullPath(saved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A damaged optional preference must not prevent the patcher from starting.
        }

        return defaultRoot;
    }

    private static void SaveCacheRoot(string cacheRoot)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath, writable: true);
            key.SetValue(CacheRootSettingName, cacheRoot, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            // The selection still works for this session when the optional preference cannot be saved.
        }
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
            CliCommandResult command = await _cli.RunAsync(
                "cache-plan",
                new[] { "--json", "--cache-root", _gameRoot });
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
            "Вернуть оригинал",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;

        await RunMutationAsync(
            "cache-restore",
            new[] { "--acknowledge-risk", RiskAcknowledgement, "--cache-root", _gameRoot },
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
                "Предыдущая установка была прервана. Патчер проверит файлы и попытается завершить восстановление. Продолжить?",
                "Восстановление после сбоя",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (recovery == DialogResult.Yes)
            {
                await RunMutationAsync(
                    "cache-recover",
                    new[] { "--acknowledge-risk", RiskAcknowledgement, "--cache-root", _gameRoot },
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
            ? "Перевод уже актуален. Обновятся только сведения о нём в патчере; тексты игры останутся прежними."
            : compatibleRevision
            ? "Файлы языка в игре отличаются от исходной версии перевода. Будут установлены только подходящие строки; изменившиеся описания останутся на английском до обновления перевода.\n\n"
                + $"Русский текст: {coverage}. На английском: {plan.Profile.EnglishFallbacks:N0}."
            : "Будет установлен русский язык для найденной игры.\n\n"
                + $"Русский текст: {coverage}.\n"
                + $"Останется на английском: {plan.Profile.EnglishFallbacks:N0}.";
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
            new[] { "--acknowledge-risk", RiskAcknowledgement, "--cache-root", _gameRoot },
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
        _versionLabel.Text = $"Игра: {plan.Observed.GameVersion ?? "версия пока неизвестна"}   •   Патчер: {plan.PatcherVersion}";
        void Show(string title, string summary, string next, Color color)
        {
            SetBadge(title, color);
            _stateLabel.Text = summary;
            _stateLabel.ForeColor = color;
            _noticeLabel.Text = next;
            _noticeLabel.ForeColor = Theme.Text;
        }
        string coverage = $"Доступно русских строк: {plan.Profile.AppliedTranslations:N0}.";
        if (plan.Profile.EnglishFallbacks > 0)
            coverage += $" Пока на английском: {plan.Profile.EnglishFallbacks:N0}.";

        if (plan.Status == "MissingFiles")
            Show("Файлы языка не найдены", "Игра ещё не скачала украинский язык или её папка находится в другом месте.",
                "Выберите украинский язык в игре и дождитесь загрузки. Затем закройте игру и нажмите «Проверить». "
                + "Если папка не найдена, нажмите «Найти / выбрать папку».", Theme.Warning);
        else if (plan.Status == "InconsistentState")
            Show("Нужна повторная проверка", "Файлы игры изменились после установки перевода. " + FriendlyRevisionDifference(plan),
                "Нажмите «Проверить». Если сообщение осталось, скопируйте подробности для поддержки ниже. "
                + "Не удаляйте файлы и резервные копии вручную.", Theme.Danger);
        else if (plan.IsVersionRisk)
            Show("Нужно обновление перевода", FriendlyRevisionDifference(plan),
                "Нажмите «Проверить», чтобы получить свежие данные. Если обновления ещё нет, дождитесь его. "
                + "Сам номер версии игры не запрещает установку; сейчас не прошли проверки её файлов.", Theme.Warning);
        else if (plan.UpdateProblemBlocksApply)
            Show("Обновление пока недоступно", "Не удалось подтвердить подходящие данные перевода.",
                "Проверьте подключение к интернету и нажмите «Проверить». При необходимости используйте «Обновить патчер».", Theme.Warning);
        else if (plan.IsPatched && !plan.TranslationUpdateAvailable)
            Show("Русский язык установлен", $"Установлено русских строк: {plan.State?.AppliedTranslations ?? plan.Profile.AppliedTranslations:N0}.",
                plan.ProcessConflicts.Length > 0
                    ? "Можно продолжать играть. Чтобы обновить или убрать перевод, сначала закройте игру и лаунчер."
                    : "Можно запускать игру. Оставьте украинский язык в настройках — он теперь содержит русский перевод. "
                        + "Кнопка «Вернуть оригинал» убирает русификацию.", Theme.Green);
        else if (plan.ProcessConflicts.Length > 0 || plan.PlanAction == "REFUSE_CLOSE_GAME_AND_LAUNCHER")
            Show("Закройте игру и лаунчер", "Перед изменением перевода они должны быть полностью закрыты.",
                "Закройте игру и лаунчер, в том числе значок лаунчера рядом с часами Windows. Затем нажмите «Проверить». "
                + "Патчер сам их не закрывает.", Theme.Warning);
        else if (plan.CanRecover || plan.Status == "RecoveryRequired")
            Show("Установка была прервана", "Предыдущую операцию нужно завершить или отменить.",
                plan.CanRecover ? "Нажмите «Исправить установку». Не запускайте игру до завершения."
                    : "Скопируйте подробности для поддержки. Не удаляйте резервные копии и не меняйте файлы вручную.", Theme.Warning);
        else if (plan.CanApply)
        {
            bool updating = plan.TranslationUpdateAvailable || plan.Status is "PatchSupersededByOfficialUpdate" or "PatchSupersededByCatalogUpdate";
            Show(updating ? "Можно обновить перевод" : "Всё готово к установке", coverage,
                plan.TranslationUpdateKind == "metadata-only"
                    ? "Тексты уже актуальны. Нажмите «Обновить перевод», чтобы сохранить новые служебные данные."
                    : $"Нажмите «{(updating ? "Обновить перевод" : "Установить перевод")}». Оригинал сохранится в резервной копии."
                        + (plan.Profile.EnglishFallbacks > 0 ? " Непереведённые строки останутся на английском до следующего обновления." : ""),
                Theme.Green);
        }
        else if (plan.PlanAction == "REFUSE_DEV_WRITES_DISABLED")
            Show("Это сборка для проверки", "Она проверяет файлы, но не устанавливает перевод.",
                "Для установки скачайте обычный Windows-установщик со страницы релиза.", Theme.Warning);
        else
            Show("Нужна проверка", "Патчер пока не может установить перевод.",
                "Нажмите «Проверить». Если это не помогло, скопируйте подробности для поддержки ниже.", Theme.Warning);

        if (plan.UpdateProblem != null && !plan.UpdateProblemBlocksApply)
            _noticeLabel.Text += " GitHub сейчас недоступен; используется сохранённый перевод.";
        _noticeLabel.Text += PatcherVersionNotice(plan);
        UpdateButtons();
    }

    private static string FriendlyRevisionDifference(CliPlanResult plan)
    {
        return plan.Diagnostic.Component switch
        {
            "english-source" => $"Изменился английский текст игры: у вас база № {plan.Observed.EnglishReleaseRevision?.ToString() ?? "?"}, "
                + $"перевод подготовлен для № {plan.Profile.EnglishReleaseRevision}.",
            "ukrainian-base" => $"Изменилась украинская база: у вас № {plan.Observed.BaseReleaseRevision?.ToString() ?? "?"}, "
                + $"перевод подготовлен для № {plan.Profile.BaseReleaseRevision}.",
            "catalog-sha256" => "Файл перевода отсутствует или повреждён.",
            "version-stamp" => "Игра обновила сведения о файлах языка.",
            _ => "Файлы языка отличаются от ожидаемых; подробности доступны ниже."
        };
    }

    private void RenderFailure(string message)
    {
        SetBadge("ОШИБКА ПРОВЕРКИ", Theme.Danger);
        _versionLabel.Text = "Версия: не подтверждена";
        _stateLabel.Text = "Состояние: проверка не завершена";
        _stateLabel.ForeColor = Theme.Danger;
        _noticeLabel.Text = "Не удалось завершить проверку. Нажмите «Проверить» ещё раз. Если ошибка повторяется, "
            + "скопируйте подробности для поддержки ниже.";
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
        _browseButton.Enabled = !_busy;
        _updatePatcherButton.Enabled = !_busy;
        _applyButton.Text = _lastPlan?.CanRecover == true
            ? "Исправить установку"
            : string.Equals(_lastPlan?.TranslationUpdateKind, "metadata-only", StringComparison.Ordinal)
                ? "Обновить перевод"
            : _lastPlan?.TranslationUpdateAvailable == true
                || string.Equals(_lastPlan?.Status, "PatchSupersededByOfficialUpdate", StringComparison.Ordinal)
                || string.Equals(_lastPlan?.Status, "PatchSupersededByCatalogUpdate", StringComparison.Ordinal)
                ? "Обновить перевод"
                : "Установить перевод";
        _applyButton.Enabled = !_busy && (_lastPlan?.CanApply == true || _lastPlan?.CanRecover == true);
        _restoreButton.Enabled = !_busy && _lastPlan?.CanRestore == true;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                text = System.Text.Json.Nodes.JsonNode.Parse(trimmed)?.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }) ?? text;
        }
        catch (System.Text.Json.JsonException) { }
        if (_log.TextLength > 180000) _log.Text = _log.Text[^90000..];
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

    private async Task<bool> CheckPatcherUpdateAsync(bool showCurrent)
    {
        if (_busy) return false;
        SetBusy(true, "Патчер…");
        bool started = false;
        try
        {
            using var client = new PatcherUpdateClient();
            VerifiedPatcherUpdate update = await client.CheckAsync();
            Version installed = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
            if (update.Version <= installed)
            {
                AppendLog($"Обновление приложения: установлена актуальная версия {installed.Major}.{installed.Minor}.{installed.Build}.");
                if (showCurrent) MessageBox.Show(this, "Установлена актуальная версия патчера.", "Обновление приложения",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            AppendLog($"Доступен патчер {update.Version}. Подпись манифеста проверена.");
            if (MessageBox.Show(this,
                $"Доступна версия патчера {update.Version}.\n\n{update.Notes}\n\n"
                + "Скачать и установить сейчас? Патчер закроется и откроется заново после установки. "
                + "Перевод игры и резервные копии не изменятся. Windows может показать предупреждение о неизвестном издателе.",
                "Обновление патчера", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
            AppendLog("Скачиваем установщик по подписанному описанию обновления. Проверяем размер, SHA-256 и версию EXE.");
            _pendingInstaller = await client.DownloadAsync(update, installed,
                new Progress<int>(percent => _busyLabel.Text = $"Загрузка новой версии: {percent}%"));
            Process? setup = Process.Start(PatcherUpdateClient.CreateInstallerStartInfo(_pendingInstaller));
            if (setup == null) throw new InvalidOperationException("Windows не запустила установщик.");
            setup.Dispose();
            started = true;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception)
            || exception is System.Net.Http.HttpRequestException or OperationCanceledException
            or System.Security.Cryptography.CryptographicException)
        {
            _pendingInstaller?.Dispose();
            _pendingInstaller = null;
            AppendLog("Автообновление приложения недоступно: " + exception.Message);
            if (showCurrent) MessageBox.Show(this, "Обновление не установлено. " + exception.Message,
                "Обновление патчера", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false, string.Empty); }
        if (started) Close(); // Release the running mutex by exiting normally, never kill other applications.
        return started;
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
            || exception is InvalidDataException
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
