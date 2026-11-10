using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OmniPlayer;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(15, 17, 21);
    public static readonly Color Panel = Color.FromArgb(23, 26, 33);
    public static readonly Color Panel2 = Color.FromArgb(34, 38, 48);
    public static readonly Color Line = Color.FromArgb(48, 54, 66);
    public static readonly Color Text = Color.FromArgb(231, 233, 238);
    public static readonly Color Muted = Color.FromArgb(152, 160, 173);
    public static readonly Color Accent = Color.FromArgb(79, 156, 255);
    public static readonly Color AccentDark = Color.FromArgb(31, 111, 235);
    public static readonly Color Warn = Color.FromArgb(245, 165, 36);

    public static readonly string IconFontName = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static bool FontExists(string name)
    {
        try { using var f = new Font(name, 10f); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar on Windows 10 (20H1+) and 11.</summary>
    public static void DarkTitleBar(IntPtr handle)
    {
        try { int on = 1; if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(handle, 19, ref on, 4); }
        catch { }
    }
}

static class Glyph
{
    public const string Play = "", Pause = "", Prev = "", Next = "", Stop = "",
        Shuffle = "", RepeatAll = "", RepeatOne = "", Volume = "", Mute = "",
        FullScreen = "", ExitFullScreen = "", Camera = "", List = "", Eq = "", CC = "";
}

sealed class DarkColors : ProfessionalColorTable
{
    public override Color MenuItemSelected => Theme.Panel2;
    public override Color MenuItemBorder => Theme.Line;
    public override Color MenuBorder => Theme.Line;
    public override Color MenuItemSelectedGradientBegin => Theme.Panel2;
    public override Color MenuItemSelectedGradientEnd => Theme.Panel2;
    public override Color MenuItemPressedGradientBegin => Theme.Panel2;
    public override Color MenuItemPressedGradientMiddle => Theme.Panel2;
    public override Color MenuItemPressedGradientEnd => Theme.Panel2;
    public override Color MenuStripGradientBegin => Theme.Panel;
    public override Color MenuStripGradientEnd => Theme.Panel;
    public override Color ToolStripDropDownBackground => Theme.Panel;
    public override Color ImageMarginGradientBegin => Theme.Panel;
    public override Color ImageMarginGradientMiddle => Theme.Panel;
    public override Color ImageMarginGradientEnd => Theme.Panel;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Line;
    public override Color CheckBackground => Theme.Panel2;
    public override Color CheckSelectedBackground => Theme.Panel2;
    public override Color CheckPressedBackground => Theme.Panel2;
    public override Color ToolStripBorder => Theme.Line;
}

sealed class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, r, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Flat slider used for the seek bar and the volume control.</summary>
sealed class Slider : Control
{
    float _v;
    int _hoverX = -1;
    string _lastTip;
    readonly ToolTip _tip = new() { InitialDelay = 0, ReshowDelay = 0, UseAnimation = false, UseFading = false };

    public float? MarkA, MarkB;
    public float WheelStep = 0.05f;
    public bool Dragging { get; private set; }
    public Func<float, string> HoverText;
    public event Action<float> Scrubbing;
    public event Action<float> Committed;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        TabStop = false;
        Height = 26;
    }

    public float Value
    {
        get => _v;
        set
        {
            float nv = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(nv - _v) > 0.0002f) { _v = nv; Invalidate(); }
        }
    }

    public void SetMarks(float? a, float? b) { MarkA = a; MarkB = b; Invalidate(); }

    float FromX(int x) => Width <= 18 ? 0 : Math.Clamp((x - 9f) / (Width - 18f), 0f, 1f);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Dragging = true; Capture = true;
        Value = FromX(e.X);
        Scrubbing?.Invoke(Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.X;
        if (Dragging) { Value = FromX(e.X); Scrubbing?.Invoke(Value); }
        Invalidate();
        if (HoverText != null)
        {
            string s = HoverText(FromX(e.X));
            if (s == null) { _tip.Hide(this); _lastTip = null; }
            else if (s != _lastTip) { _lastTip = s; _tip.Show(s, this, Math.Max(0, e.X - 18), -28, 2000); }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false; Capture = false;
        Value = FromX(e.X);
        Committed?.Invoke(Value);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverX = -1; _lastTip = null;
        _tip.Hide(this);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (WheelStep <= 0) return;
        Value += Math.Sign(e.Delta) * WheelStep;
        Committed?.Invoke(Value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        float x0 = 9, w = Width - 18, cy = Height / 2f;
        bool hot = _hoverX >= 0 || Dragging;
        float th = hot ? 6f : 4f;
        using (var track = new SolidBrush(Theme.Line)) g.FillRectangle(track, x0, cy - th / 2, w, th);
        using (var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Muted))
            if (_v > 0) g.FillRectangle(fill, x0, cy - th / 2, w * _v, th);
        using (var mk = new SolidBrush(Theme.Warn))
        {
            if (MarkA is float a) g.FillRectangle(mk, x0 + w * a - 1.5f, cy - 9, 3, 18);
            if (MarkB is float b) g.FillRectangle(mk, x0 + w * b - 1.5f, cy - 9, 3, 18);
        }
        float r = hot ? 7f : 5f, tx = x0 + w * _v;
        using (var thumb = new SolidBrush(Color.White)) g.FillEllipse(thumb, tx - r, cy - r, r * 2, r * 2);
    }
}

static class Dialogs
{
    public static Button DialogButton(string text, DialogResult result, bool primary = false)
    {
        var b = new Button
        {
            Text = text, DialogResult = result, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.AccentDark : Theme.Panel2, ForeColor = Color.White, Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = primary ? Theme.AccentDark : Theme.Line;
        return b;
    }

    static Form BaseForm(string title, Size client)
    {
        var f = new Form
        {
            AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi,
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = client,
            BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 9.5f)
        };
        f.HandleCreated += (s, e) => Theme.DarkTitleBar(f.Handle);
        return f;
    }

    public static string Prompt(IWin32Window owner, string title, string label, string initial = "")
    {
        using var f = BaseForm(title, new Size(540, 130));
        var lbl = new Label { Text = label, Left = 14, Top = 14, AutoSize = true };
        var tb = new TextBox { Left = 14, Top = 42, Width = 510, BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Text = initial };
        var ok = DialogButton("Open", DialogResult.OK, true); ok.SetBounds(342, 84, 88, 32);
        var cancel = DialogButton("Cancel", DialogResult.Cancel); cancel.SetBounds(436, 84, 88, 32);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
    }

    public static void Info(IWin32Window owner, string title, string body)
    {
        using var f = BaseForm(title, new Size(560, 520));
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Consolas", 10f),
            Text = body.Replace("\n", "\r\n"), Left = 16, Top = 14, Width = 530, Height = 440, TabStop = false
        };
        var ok = DialogButton("Close", DialogResult.OK, true); ok.SetBounds(456, 474, 88, 32);
        f.Controls.AddRange(new Control[] { text, ok });
        f.AcceptButton = ok; f.CancelButton = ok;
        f.Shown += (s, e) => { ok.Focus(); text.SelectionLength = 0; };
        f.ShowDialog(owner);
    }
}
