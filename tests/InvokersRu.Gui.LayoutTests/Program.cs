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
        Console.WriteLine("PASS working-area sizing and concise process explanation.");
    }

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
