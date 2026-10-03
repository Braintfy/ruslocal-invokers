using System.Reflection;

internal static class Program
{
    private static readonly Type MainFormType = Assembly.Load("InvokersRu.Gui").GetType("InvokersRu.Gui.MainForm", throwOnError: true)!;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        // These forms are never shown: no initial network/CLI check and no live installation writes.
        foreach ((Size size, float scale) in new[]
        {
            (new Size(920, 780), 1f), (new Size(760, 600), 1f),
            (new Size(1000, 700), 1.5f), (new Size(1250, 880), 2f)
        })
        {
            using Form form = (Form)Activator.CreateInstance(MainFormType)!;
            form.AutoScaleMode = AutoScaleMode.None;
            if (scale != 1f)
            {
                var originalFonts = Descendants(form).Select(control => (Control: control, Font: control.Font)).ToArray();
                form.Scale(new SizeF(scale, scale));
                foreach (var entry in originalFonts)
                    entry.Control.Font = new Font(entry.Font.FontFamily, entry.Font.Size * scale, entry.Font.Style);
            }
            form.ClientSize = size;
            _ = form.Handle;
            // Set only WinForms' managed visibility bit for layout/painting. Never show the HWND,
            // activate a window, or raise Shown (which starts the production initial check).
            MethodInfo setState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Type stateType = setState.GetParameters()[0].ParameterType;
            object visibleState = Enum.Parse(stateType, "Visible");
            setState.Invoke(form, new[] { visibleState, (object)true });
            foreach (Control control in Descendants(form)) control.CreateControl();
            Layout(form);
            AssertPinned(form, "idle");
            Invoke(form, "SetBusy", true, "Устанавливаем перевод и проверяем результат");
            Application.DoEvents();
            Layout(form);
            AssertPinned(form, "busy");
            var progress = Field<ProgressBar>(form, "_operationProgress");
            Require(progress.Style == ProgressBarStyle.Marquee, "Unknown-duration operation must use Marquee.");
            Require(!Field<Button>(form, "_applyButton").Enabled && !Field<Button>(form, "_checkButton").Enabled,
                "Actions must stay disabled while busy.");
            Invoke(form, "ShowDownloadProgress", 42);
            Require(progress.Style == ProgressBarStyle.Continuous && progress.Value == 42,
                "Real download progress must be determinate.");
            Invoke(form, "ShowDownloadProgress", 100);
            Require(progress.Style == ProgressBarStyle.Marquee, "Verification after download must not show fake completion.");
            Thread.Sleep(1100);
            Application.DoEvents();
            Require(Field<System.Diagnostics.Stopwatch>(form, "_operationClock").Elapsed >= TimeSpan.FromSeconds(1)
                && System.Text.RegularExpressions.Regex.IsMatch(Field<Label>(form, "_busyLabel").Text, @" · \d{2}:\d{2}$"),
                "Busy elapsed timer did not advance or display its elapsed time.");
            if (args.Length == 2 && args[0] == "--screenshots")
            {
                MainFormType.GetField("_mutationBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
                Invoke(form, "SetBusyStage", "Устанавливаем перевод и проверяем результат");
                Layout(form);
                string directory = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(directory);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, $"layout-{size.Width}x{size.Height}-{(int)(scale * 100)}.png"));
            }
            Field<RichTextBox>(form, "_log").Visible = true;
            Layout(form);
            AssertPinned(form, "support-expanded");
            Invoke(form, "SetBusy", false, "");
            Require(!Field<System.Windows.Forms.Timer>(form, "_busyTimer").Enabled, "Busy timer must stop when idle.");
            AssertInstalledStateRendering(form);
            Layout(form);
            AssertPinned(form, "installed-with-update-warning");
            Console.WriteLine($"PASS layout {size.Width}x{size.Height}, simulated UI scale {scale:P0}: pinned actions, progress, details.");
        }
        string compact = (string)MainFormType.GetMethod("RunningProcessNotice", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { new[] { @"Invokers (1234; C:\Games\Invokers\Invokers.exe)" } })!;
        Require(compact.Contains("Invokers (1234)") && !compact.Contains(@"C:\Games"), "User-facing process explanation leaked a long path or omitted PID.");
        using (Form form = (Form)Activator.CreateInstance(MainFormType)!)
        {
            _ = form.Handle;
            Invoke(form, "FitWorkingArea", true);
            Require(Screen.FromControl(form).WorkingArea.Contains(form.Bounds), "Startup window exceeds the current working area.");
        }
        using (Form form = (Form)Activator.CreateInstance(MainFormType)!)
        {
            Type planType = MainFormType.Assembly.GetType("InvokersRu.Gui.CliPlanResult", true)!;
            object plan = Activator.CreateInstance(planType)!;
            Set(plan, "Status", "MissingFiles");
            Set(plan, "PatcherVersion", "3.1.11");
            Set(plan, "LocalProblem", "runtime-cache-input");
            Set(plan, "Message", "Файл dl_en_US.bin не читается как исходный LOC1.");
            Set(plan, "UpdateProblemBlocksApply", true);
            Set(plan, "ProcessConflicts", new[] { "Invokers (1234; C:\\Games\\Invokers\\Invokers.exe)" });
            object observed = planType.GetProperty("Observed")!.GetValue(plan)!;
            Set(observed, "GameVersion", "0.61.1506:1123186");
            Set(observed, "EnglishContentGuid", "0.61.0");
            Invoke(form, "RenderPlan", plan);
            Require(Field<Label>(form, "_stateLabel").Text.Contains("dl_en_US.bin"),
                "Specific English-file failure was replaced with generic Ukrainian download advice.");
            Require(Field<Label>(form, "_noticeLabel").Text.Contains("Invokers (1234)"),
                "Local input diagnostics hid a simultaneously running game.");
            Require(Field<Label>(form, "_versionLabel").Text.Contains("Языковые данные: 0.61.0")
                && !Field<Label>(form, "_versionLabel").Text.Contains("1506"),
                "Client version was mislabeled as the LOC1 language family.");
        }
        AssertUnavailableProfileAndRootRendering();
        Console.WriteLine("PASS working-area sizing and concise process explanation.");
        Console.WriteLine("PASS precise input-failure rendering and separate client/language identities.");
        Console.WriteLine("PASS absent signed profile, actual missing files, nonstandard root and cleared checking status.");
        Console.WriteLine("PASS installed status survives update warnings; stale state and protection remain refusals.");
    }

    private static void AssertUnavailableProfileAndRootRendering()
    {
        using Form form = (Form)Activator.CreateInstance(MainFormType)!;
        Type planType = MainFormType.Assembly.GetType("InvokersRu.Gui.CliPlanResult", true)!;
        object plan = Activator.CreateInstance(planType)!;
        Set(plan, "PatcherVersion", "3.1.13");
        object protection = planType.GetProperty("ProtectionCheck")!.GetValue(plan)!;
        Set(protection, "Status", "no-known-markers");
        object observed = planType.GetProperty("Observed")!.GetValue(plan)!;
        Set(observed, "EnglishContent", "Prod_0.61.1_3");
        Set(observed, "BaseContent", "Prod_0.61.1_5");
        Set(plan, "Status", "UnknownBuild");
        Set(plan, "LocalProblem", "signed-profile-unavailable");
        Set(plan, "PlanAction", "REFUSE_UNKNOWN_OR_INCONSISTENT");
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Подходящий перевод пока недоступен"
            && Field<Label>(form, "_stateLabel").Text.Contains("EN Prod_0.61.1_3")
            && Field<Label>(form, "_stateLabel").Text.Contains("UK Prod_0.61.1_5")
            && !Field<Label>(form, "_noticeLabel").Text.Contains("загрузите в игре"),
            "A valid but unsupported tuple must not be described as missing game files.");
        Require(!Field<Button>(form, "_applyButton").Enabled,
            "An unavailable signed profile must not enable Apply.");

        Invoke(form, "ShowCheckingStatus", "Проверяем новую версию…");
        Require(Field<Label>(form, "_statusBadge").Text == "Идёт проверка"
            && !Field<Label>(form, "_stateLabel").Text.Contains("Prod_0.61.1_5"),
            "A modal self-update dialog must not leave a stale profile verdict behind it.");

        Set(plan, "Status", "MissingFiles");
        Set(plan, "LocalProblem", null!);
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Набор файлов языка не найден",
            "Actually missing files must keep the dedicated missing-files explanation.");

        Set(plan, "Status", "CompatibleOriginal");
        Set(plan, "MutationRootAuthorized", false);
        Set(plan, "NonstandardRootVerified", true);
        Set(plan, "PlanAction", "REFUSE_NONSTANDARD_CACHE_ROOT");
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Нестандартная папка — только проверка"
            && Field<Label>(form, "_noticeLabel").Text.Contains("не будет изменять файлы")
            && !Field<Button>(form, "_applyButton").Enabled
            && !Field<Button>(form, "_restoreButton").Enabled,
            "Nonstandard root must be a visible read-only refusal, not a misleading ready state.");
        Set(plan, "Status", "InconsistentState");
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Нестандартная папка — только проверка",
            "An unrelated standard-root patch state must not hide a verified nonstandard-root refusal.");
    }

    private static void AssertInstalledStateRendering(Form form)
    {
        Type planType = MainFormType.Assembly.GetType("InvokersRu.Gui.CliPlanResult", true)!;
        object plan = Activator.CreateInstance(planType)!;
        Set(plan, "Status", "PatchedByThisTool");
        Set(plan, "PatcherVersion", "3.1.11");
        Set(plan, "CanRestore", true);
        Set(plan, "PlanAction", "NOOP_OR_RESTORE");
        object protection = planType.GetProperty("ProtectionCheck")!.GetValue(plan)!;
        Set(protection, "Status", "no-known-markers");
        object state = Activator.CreateInstance(planType.GetProperty("State")!.PropertyType)!;
        Set(state, "AppliedTranslations", 42447);
        Set(plan, "State", state);
        MainFormType.GetField("_lastPlan", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, plan);
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Русский язык установлен"
            && Field<Label>(form, "_noticeLabel").Text.Contains("Повторная установка не нужна"),
            "An authenticated installed translation must have an unambiguous success state.");
        Require(Field<Button>(form, "_applyButton").Text == "Перевод установлен"
            && !Field<Button>(form, "_applyButton").Enabled && Field<Button>(form, "_restoreButton").Enabled,
            "An installed translation must not suggest reinstalling it or lose an authorized restore action.");

        Set(plan, "UpdateProblem", "Current authenticated translation data cannot materialize a supported profile.");
        Set(plan, "UpdateProblemBlocksApply", true);
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Русский язык установлен"
            && Field<Label>(form, "_noticeLabel").Text.Contains("Обновление перевода пока недоступно")
            && Field<Label>(form, "_noticeLabel").Text.Contains("установленный перевод сохранён")
            && !Field<Button>(form, "_applyButton").Enabled,
            "A blocked update must not hide a verified installation or enable Apply.");

        Set(plan, "ProcessConflicts", new[] { "Invokers (1234)" });
        Set(plan, "CanRestore", false);
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Русский язык установлен"
            && Field<Label>(form, "_noticeLabel").Text.Contains("закройте игру и лаунчер")
            && !Field<Button>(form, "_restoreButton").Enabled,
            "Running processes must prevent mutations without implying the installed translation disappeared.");

        Set(protection, "Status", "blocked");
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Установка приостановлена",
            "Installed status must not conceal a protection refusal.");
        Set(protection, "Status", "no-known-markers");
        Set(plan, "Status", "InconsistentState");
        object diagnostic = planType.GetProperty("Diagnostic")!.GetValue(plan)!;
        Set(diagnostic, "Kind", "local-state");
        Set(diagnostic, "Component", "patch-state");
        Invoke(form, "RenderPlan", plan);
        Require(Field<Label>(form, "_statusBadge").Text == "Нужно проверить предыдущую установку"
            && !Field<Label>(form, "_stateLabel").Text.Contains("Файлы игры изменились")
            && Field<Button>(form, "_applyButton").Text == "Установить перевод",
            "Unverified historical state must not be presented as an installed translation or corrupt game files.");
        Set(plan, "Status", "PatchedByThisTool");
        Set(plan, "ProcessConflicts", Array.Empty<string>());
        Set(plan, "CanRestore", true);
        Invoke(form, "RenderPlan", plan);
    }

    private static void Set(object instance, string property, object value) =>
        instance.GetType().GetProperty(property)!.SetValue(instance, value);

    private static void AssertPinned(Form form, string phase)
    {
        Control footer = Descendants(form).Single(control => control.Name == "PinnedActions");
        Rectangle footerBounds = BoundsIn(footer, form);
        Require(form.ClientRectangle.Contains(footerBounds), $"Footer is outside client bounds ({phase}): {footerBounds}; client={form.ClientRectangle}");
        foreach (string name in new[] { "_checkButton", "_applyButton", "_restoreButton", "_busyLabel" })
        {
            Control control = Field<Control>(form, name);
            Rectangle bounds = BoundsIn(control, footer);
            Require(footer.ClientRectangle.Contains(bounds), $"{name} clipped inside footer ({phase}): {bounds}; footer={footer.ClientRectangle}");
        }
        Require(footerBounds.Top >= form.ClientSize.Height / 3, "Footer leaves too little room for the current game status.");
    }

    private static Rectangle BoundsIn(Control control, Control ancestor)
    {
        Point position = Point.Empty;
        Control? current = control;
        while (current != ancestor)
        {
            Require(current != null, "Control is not below the expected ancestor.");
            position.Offset(current!.Location);
            current = current.Parent;
        }
        return new Rectangle(position, control.Size);
    }

    private static void Layout(Control control)
    {
        for (int i = 0; i < 4; i++)
        {
            foreach (Control child in Descendants(control).Reverse()) child.PerformLayout();
            control.PerformLayout();
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(Form form, string name) => (T)MainFormType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
    private static void Invoke(Form form, string name, params object[] args) => MainFormType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, args);
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
