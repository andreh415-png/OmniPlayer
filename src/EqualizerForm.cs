using System;
using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace OmniPlayer;

/// <summary>10-band graphic equalizer driven by libVLC's built-in equalizer and presets.</summary>
sealed class EqualizerForm : Form
{
    readonly Settings S;
    readonly Action _apply;
    readonly TrackBar[] _bars = new TrackBar[10];
    readonly Label[] _vals = new Label[10];
    readonly TrackBar _pre;
    readonly Label _preVal;
    readonly CheckBox _enable;
    readonly ComboBox _preset;
    bool _busy;

    public EqualizerForm(Settings s, Action apply)
    {
        S = s; _apply = apply;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Equalizer";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ClientSize = new Size(690, 318);
        BackColor = Theme.Panel; ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        HandleCreated += (o, e) => Theme.DarkTitleBar(Handle);

        _enable = new CheckBox { Text = "Enable equalizer", Left = 16, Top = 14, AutoSize = true, Checked = S.EqEnabled };
        _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 196, Top = 11, Width = 210, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2, ForeColor = Theme.Text };
        var flat = Dialogs.DialogButton("Reset to flat", DialogResult.None); flat.SetBounds(418, 10, 120, 30);
        Controls.AddRange(new Control[] { _enable, _preset, flat });

        var probe = new Equalizer();
        _preset.Items.Add("Custom");
        for (uint i = 0; i < probe.PresetCount; i++) _preset.Items.Add(probe.PresetName(i));

        // Preamp column
        _preVal = MakeVal(16);
        _pre = MakeBar(16);
        var preLbl = MakeFreq(16, "Preamp");
        _pre.Scroll += (o, e) => { if (_busy) return; S.EqPreamp = -_pre.Value; Changed(); };

        uint bandCount = Math.Min(10u, probe.BandCount);
        for (int i = 0; i < 10; i++)
        {
            int x = 92 + i * 58;
            _vals[i] = MakeVal(x);
            _bars[i] = MakeBar(x);
            string label = "";
            if (i < bandCount)
            {
                float f = probe.BandFrequency((uint)i);
                label = f < 1000 ? $"{f:0} Hz" : $"{f / 1000f:0.#} kHz";
            }
            MakeFreq(x, label);
            int band = i;
            _bars[i].Scroll += (o, e) => { if (_busy) return; S.EqBands[band] = -_bars[band].Value; Changed(); };
        }

        _enable.CheckedChanged += (o, e) => { if (_busy) return; S.EqEnabled = _enable.Checked; _apply(); };
        _preset.SelectedIndexChanged += (o, e) =>
        {
            if (_busy || _preset.SelectedIndex <= 0) return;
            var p = new Equalizer((uint)(_preset.SelectedIndex - 1));
            S.EqPreamp = p.Preamp;
            for (int i = 0; i < 10; i++) S.EqBands[i] = i < bandCount ? p.Amp((uint)i) : 0f;
            S.EqPreset = _preset.SelectedIndex - 1;
            S.EqEnabled = true;
            SyncUi(); _apply();
        };
        flat.Click += (o, e) =>
        {
            S.EqPreamp = 0; for (int i = 0; i < 10; i++) S.EqBands[i] = 0;
            S.EqPreset = -1; SyncUi(); _apply();
        };

        SyncUi();
    }

    Label MakeVal(int x)
    {
        var l = new Label { Left = x, Top = 50, Width = 52, Height = 18, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text, Font = new Font("Segoe UI", 8.5f) };
        Controls.Add(l); return l;
    }

    TrackBar MakeBar(int x)
    {
        // Vertical TrackBars put the minimum at the top, so values are stored negated (up = boost).
        var t = new TrackBar { Orientation = Orientation.Vertical, Minimum = -20, Maximum = 20, TickFrequency = 5, SmallChange = 1, LargeChange = 3, Left = x + 4, Top = 70, Height = 200, Width = 45, BackColor = Theme.Panel, TickStyle = TickStyle.Both };
        Controls.Add(t); return t;
    }

    Label MakeFreq(int x, string text)
    {
        var l = new Label { Left = x - 4, Top = 276, Width = 60, Height = 20, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, Text = text, Font = new Font("Segoe UI", 8.5f) };
        Controls.Add(l); return l;
    }

    static string Db(float v) => (v > 0 ? "+" : "") + v.ToString("0.#") + " dB";

    void Changed()
    {
        _busy = true;
        S.EqEnabled = true; _enable.Checked = true;
        S.EqPreset = -1; _preset.SelectedIndex = 0;
        _busy = false;
        UpdateLabels();
        _apply();
    }

    void UpdateLabels()
    {
        _preVal.Text = Db(S.EqPreamp);
        for (int i = 0; i < 10; i++) _vals[i].Text = Db(S.EqBands[i]);
    }

    void SyncUi()
    {
        _busy = true;
        _enable.Checked = S.EqEnabled;
        _preset.SelectedIndex = Math.Clamp(S.EqPreset + 1, 0, _preset.Items.Count - 1);
        _pre.Value = Math.Clamp(-(int)Math.Round(S.EqPreamp), -20, 20);
        for (int i = 0; i < 10; i++) _bars[i].Value = Math.Clamp(-(int)Math.Round(S.EqBands[i]), -20, 20);
        UpdateLabels();
        _busy = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape || keyData == Keys.E) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
