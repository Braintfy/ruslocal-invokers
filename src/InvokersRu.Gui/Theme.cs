using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace InvokersRu.Gui;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(9, 16, 29);
    public static readonly Color Card = Color.FromArgb(17, 28, 46);
    public static readonly Color CardHover = Color.FromArgb(22, 36, 58);
    public static readonly Color Border = Color.FromArgb(39, 55, 82);
    public static readonly Color Text = Color.FromArgb(238, 242, 249);
    public static readonly Color Muted = Color.FromArgb(152, 166, 191);
    public static readonly Color Gold = Color.FromArgb(218, 171, 74);
    public static readonly Color GoldHover = Color.FromArgb(235, 191, 96);
    public static readonly Color Blue = Color.FromArgb(67, 133, 238);
    public static readonly Color Green = Color.FromArgb(63, 190, 132);
    public static readonly Color Warning = Color.FromArgb(241, 178, 70);
    public static readonly Color Danger = Color.FromArgb(237, 99, 107);
}

internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        DoubleBuffered = true;
        BackColor = Theme.Card;
        Padding = new Padding(22);
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        using var pen = new Pen(Theme.Border, 1f);
        Rectangle border = ClientRectangle;
        border.Width -= 1;
        border.Height -= 1;
        eventArgs.Graphics.DrawRectangle(pen, border);
    }
}

internal sealed class LogoBadge : Control
{
    public LogoBadge()
    {
        DoubleBuffered = true;
        Size = new Size(62, 62);
        MinimumSize = Size;
        MaximumSize = Size;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-3, -3);
        using var fill = new SolidBrush(Color.FromArgb(31, 48, 76));
        using var border = new Pen(Theme.Gold, 2f);
        eventArgs.Graphics.FillEllipse(fill, bounds);
        eventArgs.Graphics.DrawEllipse(border, bounds);
        using var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Theme.Gold);
        const string text = "RU";
        SizeF textSize = eventArgs.Graphics.MeasureString(text, font);
        eventArgs.Graphics.DrawString(
            text,
            font,
            textBrush,
            (Width - textSize.Width) / 2f,
            (Height - textSize.Height) / 2f - 1f);
    }
}

internal sealed class ActionButton : Button
{
    private readonly Color _normalColor;
    private readonly Color _hoverColor;

    public ActionButton(string text, Color normalColor, Color hoverColor, Color foreground)
    {
        _normalColor = normalColor;
        _hoverColor = hoverColor;
        Text = text;
        BackColor = normalColor;
        ForeColor = foreground;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold, GraphicsUnit.Point);
        Height = 46;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        MouseEnter += (_, _) =>
        {
            if (Enabled) BackColor = _hoverColor;
        };
        MouseLeave += (_, _) => BackColor = _normalColor;
        EnabledChanged += (_, _) =>
        {
            BackColor = Enabled ? _normalColor : Color.FromArgb(45, 55, 72);
            ForeColor = Enabled ? foreground : Color.FromArgb(112, 124, 145);
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The native disabled renderer ignores ForeColor and painted black-on-grey text.
        using var brush = new SolidBrush(Enabled ? BackColor : Color.FromArgb(36, 45, 61));
        e.Graphics.FillRectangle(brush, ClientRectangle);
        Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -6, -4);
        TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
            Enabled ? ForeColor : Color.FromArgb(163, 174, 194),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}
