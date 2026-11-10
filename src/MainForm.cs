using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using LibVLCSharp.WinForms;

namespace OmniPlayer;

sealed class Item
{
    public string Path = "";
    public bool IsUrl;
    public long Duration = -1;          // ms; -1 = not probed yet, -2 = unknown
    public bool Bad;
    public readonly List<string> Subs = new();
    public string Name => IsUrl ? Path : System.IO.Path.GetFileName(Path);
}

sealed class MainForm : Form, IMessageFilter
{
    internal static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
    {
        // video
        ".mp4",".m4v",".mkv",".webm",".mov",".avi",".wmv",".asf",".flv",".f4v",".mpg",".mpeg",".mpe",".m1v",".m2v",
        ".ts",".mts",".m2ts",".m2t",".tp",".trp",".vob",".ogv",".ogm",".3gp",".3g2",".divx",".xvid",".rm",".rmvb",
        ".dv",".mxf",".hevc",".h264",".h265",".264",".265",".av1",".ivf",".y4m",".mjpg",".mjpeg",".nut",".amv",".wtv",".dvr-ms",".bik",
        // audio
        ".mp3",".m4a",".m4b",".aac",".wav",".flac",".ogg",".oga",".opus",".wma",".alac",".aiff",".aif",".aifc",".ape",
        ".wv",".mka",".ac3",".eac3",".dts",".mpc",".tta",".amr",".au",".caf",".dsf",".dff",".mp2",".mpa",".spx",".weba",".mod",".xm",".it",".s3m"
    };
    static readonly HashSet<string> SubExt = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sami", ".txt" };
    static readonly HashSet<string> ListExt = new(StringComparer.OrdinalIgnoreCase) { ".m3u", ".m3u8" };
    /// <summary>Types offered to Windows for "Open with" / Default apps (skips ambiguous ones like .ts, .it).</summary>
    internal static IEnumerable<string> AssociableExtensions =>
        MediaExt.Concat(ListExt).Where(e => e is not (".ts" or ".it" or ".mod" or ".xm" or ".s3m" or ".264" or ".265" or ".y4m" or ".nut" or ".tp"));
    static readonly float[] Rates = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f };
    static readonly (string Label, string Value)[] Aspects =
    {
        ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("2.35:1", "235:100"),
        ("16:10", "16:10"), ("5:4", "5:4"), ("1:1", "1:1")
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    Settings S = Settings.Load();
    readonly LibVLC vlc;
    readonly MediaPlayer mp;

    // UI
    VideoView video;
    Panel videoHost;
    Label placeholder;
    SplitContainer split;
    ListView list;
    TextBox filter;
    Label plInfo;
    Panel controls;
    Slider seek, volume;
    Label lblCur, lblDur, status;
    ComboBox speed;
    Button bPlay, bShuffle, bRepeat, bMute, bAB, bCC, bPlaylist, bFull;
    MenuStrip menu;
    ToolStripMenuItem miShuffle, miRepeat, miMute, miPlaylist, miOnTop, miResume, miRestore, miSpeed, miAspect;
    readonly ToolTip tips = new();
    Font iconFont, iconFontBig, smallBold, boldFont;
    readonly System.Windows.Forms.Timer ui = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer clickTimer = new();
    readonly System.Windows.Forms.Timer zoomTimer = new() { Interval = 40 };

    // Zoom & pan (picture-viewer style). Implemented with libVLC crop geometry, so it costs nothing extra to decode.
    float zoom = 1f, zcx = 0.5f, zcy = 0.5f, panCx, panCy;
    bool cropDirty, mouseDown, panned;
    uint fullW, fullH;   // full-frame size, captured before any crop (libVLC reports the cropped size once zoomed)
    Point downPt;

    // State
    readonly List<Item> items = new();
    readonly Dictionary<Item, ListViewItem> rows = new();
    readonly List<Item> hist = new();
    readonly Random rng = new();
    Item cur, queued;
    List<Item> dragRows;
    long abA = -1, abB = -1;
    int errRun, lastSpu = -1;
    bool resumePending, probing, fs, cursorHidden, lastPlaying;
    DateTime lastPosSave = DateTime.MinValue, lastMove = DateTime.Now, statusUntil = DateTime.MinValue;
    Point lastMouse;
    FormWindowState prevState;
    DateTime lastExternalOpen = DateTime.MinValue;
    EqualizerForm eqForm;
    Equalizer eq;

    public MainForm(string[] args)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "OmniPlayer";
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(900, 520);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        iconFont = new Font(Theme.IconFontName, 13f);
        iconFontBig = new Font(Theme.IconFontName, 16f);
        smallBold = new Font("Segoe UI", 9f, FontStyle.Bold);
        boldFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        // --audio-visual shows a spectrum for music files; hardware decoding is on by default.
        Log.Write("Starting video engine…");
        vlc = new LibVLC(false, "--audio-visual=visual", "--effect-list=spectrum", "--no-video-title-show", "--no-snapshot-preview");
        mp = new MediaPlayer(vlc) { EnableHardwareDecoding = true, EnableMouseInput = false, EnableKeyInput = false };

        Log.Write("Video engine ready (libVLC " + vlc.Version + ")");
        BuildUi();
        BuildMenu();
        WireEvents();
        Log.Write("Interface built");

        // Window placement
        if (S.X != int.MinValue)
        {
            var r = new Rectangle(S.X, S.Y, Math.Max(900, S.W), Math.Max(520, S.H));
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) { StartPosition = FormStartPosition.Manual; Bounds = r; }
            else { StartPosition = FormStartPosition.CenterScreen; Size = r.Size; }
        }
        else { StartPosition = FormStartPosition.CenterScreen; Size = new Size(S.W, S.H); }

        ResumeLayout(true);
        ApplySettings();

        // Restore last playlist, then add anything passed on the command line ("Open with").
        if (S.RestorePlaylist)
        {
            foreach (var p in S.Playlist)
                if (IsUrl(p) || File.Exists(p)) items.Add(MakeItem(p));
            if (S.PlaylistIndex >= 0 && S.PlaylistIndex < items.Count) queued = items[S.PlaylistIndex];
        }
        var startup = args.Where(a => IsUrl(a) || File.Exists(a) || Directory.Exists(a)).ToList();
        Shown += (s, e) =>
        {
            if (S.Maximized) WindowState = FormWindowState.Maximized;
            ApplySplit();
            RenderList(true);
            if (startup.Count > 0) { lastExternalOpen = DateTime.Now; AddPaths(startup); }
            else if (queued != null) Status($"Press Space to continue: {queued.Name}", 8000);
            ProbeDurations();
        };
    }

    // ───────────────────────────── UI construction ─────────────────────────────

    Button Btn(string text, string tip, Action click, Font font, int w = 40)
    {
        var b = new Button
        {
            Text = text, Size = new Size(w, 40), Margin = new Padding(1, 0, 1, 0), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel, ForeColor = Theme.Text, TabStop = false, Font = font, Cursor = Cursors.Hand,
            UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Panel2;
        b.FlatAppearance.MouseDownBackColor = Theme.Line;
        b.Click += (s, e) => click();
        tips.SetToolTip(b, tip);
        return b;
    }

    void BuildUi()
    {
        // Video area
        videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        video = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black, MediaPlayer = mp };
        placeholder = new Label
        {
            Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 13f), Cursor = Cursors.Hand,
            Text = "Drop video or audio files (or folders) here\n\nor press Ctrl+O to open files  ·  Ctrl+Shift+O for a folder  ·  Ctrl+U for a stream URL\n\nPlays HEVC/H.265, AV1, VP9, H.264, MKV, AVI, WMV, FLAC, DTS and nearly everything else"
        };
        videoHost.Controls.Add(video);
        videoHost.Controls.Add(placeholder);
        placeholder.BringToFront();

        // Playlist
        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true,
            BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text, HideSelection = false,
            AllowDrop = true, MultiSelect = true, ShowItemToolTips = true
        };
        list.Columns.Add("#", 44, HorizontalAlignment.Right);
        list.Columns.Add("Name", 200);
        list.Columns.Add("Length", 70, HorizontalAlignment.Right);
        list.Resize += (s, e) => ResizeColumns();

        var plTitle = new Label { Text = "Playlist", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Theme.Text };
        plInfo = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Theme.Muted, Text = "Empty" };
        filter = new TextBox { Dock = DockStyle.Top, BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Filter playlist…" };
        filter.TextChanged += (s, e) => RenderList();
        var plHead = new Panel { Dock = DockStyle.Top, Height = 82, Padding = new Padding(10, 8, 10, 8), BackColor = Theme.Panel };
        plHead.Controls.Add(filter); plHead.Controls.Add(plInfo); plHead.Controls.Add(plTitle);

        split = new SplitContainer
        {
            Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, BackColor = Theme.Line, SplitterWidth = 4, TabStop = false, Size = new Size(1200, 640)
        };
        split.Panel1.BackColor = Color.Black;
        split.Panel2.BackColor = Theme.Panel;
        split.Panel1.Controls.Add(videoHost);
        split.Panel2.Controls.Add(list);
        split.Panel2.Controls.Add(plHead);

        // Bottom controls
        seek = new Slider { Dock = DockStyle.Fill, WheelStep = 0 };
        lblCur = new Label { Dock = DockStyle.Left, Width = 76, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Muted, Text = "0:00" };
        lblDur = new Label { Dock = DockStyle.Right, Width = 76, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted, Text = "0:00" };
        var seekRow = new Panel { Dock = DockStyle.Top, Height = 28 };
        seekRow.Controls.Add(seek); seekRow.Controls.Add(lblCur); seekRow.Controls.Add(lblDur);

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        bShuffle = Btn(Glyph.Shuffle, "Shuffle (S)", ToggleShuffle, iconFont);
        var bPrev = Btn(Glyph.Prev, "Previous (P)", Prev, iconFont);
        var bBack = Btn("−10", "Back 10 seconds (J)", () => Skip(-10000), smallBold, 44);
        bPlay = Btn(Glyph.Play, "Play / Pause (Space)", TogglePlay, iconFontBig, 52);
        bPlay.BackColor = Theme.AccentDark; bPlay.ForeColor = Color.White;
        bPlay.FlatAppearance.MouseOverBackColor = Theme.Accent;
        var bFwd = Btn("+10", "Forward 10 seconds (L)", () => Skip(10000), smallBold, 44);
        var bNext = Btn(Glyph.Next, "Next (N)", () => Next(true), iconFont);
        var bStop = Btn(Glyph.Stop, "Stop", StopPlayback, iconFont);
        bRepeat = Btn(Glyph.RepeatAll, "Repeat: off (R)", CycleRepeat, iconFont);
        left.Controls.AddRange(new Control[] { bShuffle, bPrev, bBack, bPlay, bFwd, bNext, bStop, bRepeat });

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        bMute = Btn(Glyph.Volume, "Mute (M)", ToggleMute, iconFont);
        volume = new Slider { Size = new Size(110, 40), Margin = new Padding(0, 0, 8, 0), WheelStep = 5f / 150f };
        speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel2, ForeColor = Theme.Text, Width = 72, Margin = new Padding(0, 8, 6, 0), TabStop = false };
        foreach (var r in Rates) speed.Items.Add(r.ToString("0.##") + "×");
        tips.SetToolTip(speed, "Playback speed  ( [  and  ] )");
        bAB = Btn("A–B", "A–B loop (B): set start, set end, clear", CycleAB, smallBold, 46);
        bCC = Btn(Glyph.CC, "Subtitles (C toggles · right side menu for tracks)", ShowSubtitleMenu, iconFont);
        var bEq = Btn(Glyph.Eq, "Equalizer (E)", ShowEq, iconFont);
        var bSnap = Btn(Glyph.Camera, "Snapshot (Shift+S) — saved to Pictures\\OmniPlayer", Snapshot, iconFont);
        bPlaylist = Btn(Glyph.List, "Show / hide playlist (Ctrl+L)", TogglePlaylist, iconFont);
        bFull = Btn(Glyph.FullScreen, "Fullscreen (F or double-click)", ToggleFullscreen, iconFont);
        right.Controls.AddRange(new Control[] { bMute, volume, speed, bAB, bCC, bEq, bSnap, bPlaylist, bFull });

        status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, AutoEllipsis = true, Padding = new Padding(8, 4, 8, 0) };

        var btnRow = new Panel { Dock = DockStyle.Fill };
        btnRow.Controls.Add(status); btnRow.Controls.Add(left); btnRow.Controls.Add(right);

        controls = new Panel { Dock = DockStyle.Bottom, Height = 90, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 6) };
        controls.Controls.Add(btnRow); controls.Controls.Add(seekRow);

        menu = new MenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text, Padding = new Padding(6, 2, 0, 2) };
        MainMenuStrip = menu;

        Controls.Add(split);
        Controls.Add(controls);
        Controls.Add(menu);
    }

    static ToolStripMenuItem MI(string text, string keys, Action a)
    {
        var m = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
        if (a != null) m.Click += (s, e) => a();
        return m;
    }

    ToolStripMenuItem TrackMenu(string text, Func<TrackDescription[]> get, Func<int> current, Action<int> set, Action<ToolStripMenuItem> extras = null)
    {
        var m = new ToolStripMenuItem(text);
        m.DropDownItems.Add(new ToolStripMenuItem("(nothing playing)") { Enabled = false });
        m.DropDownOpening += (s, e) =>
        {
            m.DropDownItems.Clear();
            TrackDescription[] tracks = null;
            try { if (cur != null) tracks = get(); } catch { }
            if (tracks == null || tracks.Length == 0)
                m.DropDownItems.Add(new ToolStripMenuItem("(no tracks)") { Enabled = false });
            else
            {
                int c = -99; try { c = current(); } catch { }
                foreach (var t in tracks)
                {
                    int id = t.Id;
                    var mi = new ToolStripMenuItem(string.IsNullOrWhiteSpace(t.Name) ? $"Track {id}" : t.Name) { Checked = id == c };
                    mi.Click += (s2, e2) => set(id);
                    m.DropDownItems.Add(mi);
                }
            }
            extras?.Invoke(m);
        };
        return m;
    }

    ToolStripMenuItem AudioTrackMenu() => TrackMenu("Audio Track", () => mp.AudioTrackDescription, () => mp.AudioTrack, id => { mp.SetAudioTrack(id); Status("Audio track changed"); });
    ToolStripMenuItem VideoTrackMenu() => TrackMenu("Video Track", () => mp.VideoTrackDescription, () => mp.VideoTrack, id => mp.SetVideoTrack(id));
    ToolStripMenuItem SubTrackMenu() => TrackMenu("Subtitle Track", () => mp.SpuDescription, () => mp.Spu,
        id => { mp.SetSpu(id); Status(id == -1 ? "Subtitles off" : "Subtitle track changed"); },
        m => { m.DropDownItems.Add(new ToolStripSeparator()); m.DropDownItems.Add(MI("Add Subtitle File…", "", AddSubtitleFile)); });

    ToolStripMenuItem AspectMenu()
    {
        var m = new ToolStripMenuItem("Aspect Ratio");
        foreach (var (label, value) in Aspects)
        {
            var v = value; var mi = MI(label, "", () => { mp.AspectRatio = v; Status("Aspect ratio: " + (v ?? "default")); });
            m.DropDownItems.Add(mi);
        }
        m.DropDownItems.Add(MI("Stretch to window", "", () => { mp.AspectRatio = $"{video.Width}:{Math.Max(1, video.Height)}"; Status("Stretched to window"); }));
        m.DropDownOpening += (s, e) =>
        {
            string ar = null; try { ar = mp.AspectRatio; } catch { }
            for (int i = 0; i < Aspects.Length; i++) ((ToolStripMenuItem)m.DropDownItems[i]).Checked = Aspects[i].Value == ar || (Aspects[i].Value == null && string.IsNullOrEmpty(ar));
        };
        return m;
    }

    void BuildMenu()
    {
        var media = new ToolStripMenuItem("&Media");
        media.DropDownItems.AddRange(new ToolStripItem[]
        {
            MI("Open Files…", "Ctrl+O", OpenFiles),
            MI("Open Folder…", "Ctrl+Shift+O", OpenFolder),
            MI("Open Stream / URL…", "Ctrl+U", OpenUrl),
            new ToolStripSeparator(),
            MI("Add Subtitle File…", "", AddSubtitleFile),
            new ToolStripSeparator(),
            MI("Save Playlist As…", "", SavePlaylist),
            MI("Clear Playlist", "", ClearPlaylist),
            new ToolStripSeparator(),
            MI("Exit", "Alt+F4", Close)
        });

        miSpeed = new ToolStripMenuItem("Speed");
        foreach (var r in Rates) { var rr = r; miSpeed.DropDownItems.Add(MI(r.ToString("0.##") + "×", r == 1f ? "=" : "", () => SetRate(rr))); }
        miSpeed.DropDownItems.Add(new ToolStripSeparator());
        miSpeed.DropDownItems.Add(MI("Slower", "[", () => StepRate(-1)));
        miSpeed.DropDownItems.Add(MI("Faster", "]", () => StepRate(1)));
        miSpeed.DropDownOpening += (s, e) => { for (int i = 0; i < Rates.Length; i++) ((ToolStripMenuItem)miSpeed.DropDownItems[i]).Checked = Math.Abs(Rates[i] - S.Rate) < 0.001f; };

        miShuffle = MI("Shuffle", "S", ToggleShuffle);
        miRepeat = new ToolStripMenuItem("Repeat");
        string[] rep = { "Off", "All", "One" };
        for (int i = 0; i < 3; i++) { int v = i; miRepeat.DropDownItems.Add(MI(rep[i], "", () => { S.Repeat = v; UpdateToggles(); Status("Repeat: " + rep[v]); })); }
        miRepeat.DropDownOpening += (s, e) => { for (int i = 0; i < 3; i++) ((ToolStripMenuItem)miRepeat.DropDownItems[i]).Checked = S.Repeat == i; };

        var playback = new ToolStripMenuItem("&Playback");
        playback.DropDownItems.AddRange(new ToolStripItem[]
        {
            MI("Play / Pause", "Space", TogglePlay),
            MI("Stop", "", StopPlayback),
            MI("Previous", "P", Prev),
            MI("Next", "N", () => Next(true)),
            new ToolStripSeparator(),
            MI("Back 10 seconds", "J", () => Skip(-10000)),
            MI("Forward 10 seconds", "L", () => Skip(10000)),
            MI("Back / Forward 5 s · 30 s · 1 min", "← → · Shift · Ctrl", null),
            MI("Next Frame", ".", FrameStep),
            MI("Jump To Time…", "Ctrl+T", JumpToTime),
            new ToolStripSeparator(),
            miSpeed,
            MI("A–B Loop", "B", CycleAB),
            new ToolStripSeparator(),
            miShuffle,
            miRepeat
        });

        miMute = MI("Mute", "M", ToggleMute);
        var audioDelay = new ToolStripMenuItem("Audio Delay");
        audioDelay.DropDownItems.AddRange(new ToolStripItem[]
        {
            MI("+50 ms", "", () => AudioDelay(50)), MI("−50 ms", "", () => AudioDelay(-50)), MI("Reset", "", () => AudioDelay(0, true))
        });
        var audio = new ToolStripMenuItem("&Audio");
        audio.DropDownItems.AddRange(new ToolStripItem[]
        {
            AudioTrackMenu(), MI("Next Audio Track", "A", CycleAudio), new ToolStripSeparator(),
            miMute, MI("Volume Up", "↑", () => SetVolume(S.Volume + 5, true)), MI("Volume Down", "↓", () => SetVolume(S.Volume - 5, true)),
            new ToolStripSeparator(), MI("Equalizer…", "E", ShowEq), audioDelay
        });

        miOnTop = MI("Always on Top", "", () => { S.AlwaysOnTop = !S.AlwaysOnTop; TopMost = S.AlwaysOnTop; miOnTop.Checked = S.AlwaysOnTop; });
        miAspect = AspectMenu();
        var videoMenu = new ToolStripMenuItem("&Video");
        videoMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            MI("Fullscreen", "F / F11", ToggleFullscreen), VideoTrackMenu(), miAspect, new ToolStripSeparator(),
            MI("Zoom In", "Wheel ↑ / Ctrl+=", () => ZoomCenter(1.25f)), MI("Zoom Out", "Wheel ↓ / Ctrl+−", () => ZoomCenter(0.8f)),
            MI("Reset Zoom", "Z / middle-click", ResetZoom), new ToolStripSeparator(),
            MI("Take Snapshot", "Shift+S", Snapshot), MI("Open Snapshot Folder", "", () => OpenFolderInExplorer(SnapshotDir())),
            new ToolStripSeparator(), miOnTop
        });

        var subs = new ToolStripMenuItem("&Subtitles");
        subs.DropDownItems.AddRange(new ToolStripItem[]
        {
            MI("Add Subtitle File…", "", AddSubtitleFile), SubTrackMenu(), MI("Subtitles On / Off", "C", ToggleSubs),
            new ToolStripSeparator(),
            MI("Delay +100 ms (later)", "H", () => SubDelay(100)), MI("Delay −100 ms (earlier)", "G", () => SubDelay(-100)), MI("Reset Delay", "", () => SubDelay(0, true))
        });

        miPlaylist = MI("Playlist", "Ctrl+L", TogglePlaylist);
        miResume = MI("Remember Position in Long Files", "", () => { S.Resume = !S.Resume; miResume.Checked = S.Resume; });
        miRestore = MI("Restore Playlist on Start", "", () => { S.RestorePlaylist = !S.RestorePlaylist; miRestore.Checked = S.RestorePlaylist; });
        var view = new ToolStripMenuItem("V&iew");
        view.DropDownItems.AddRange(new ToolStripItem[]
        {
            miPlaylist, new ToolStripSeparator(), miResume, miRestore,
            MI("Forget Saved Positions", "", () => { S.Positions.Clear(); Status("Saved positions cleared"); }),
            new ToolStripSeparator(), MI("Make OmniPlayer My Default Player…", "", MakeDefault),
            MI("Remove from Windows File Associations…", "", RemoveRegistration),
            new ToolStripSeparator(), MI("Reset Settings to Defaults…", "", ResetSettings)
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.AddRange(new ToolStripItem[] { MI("Keyboard Shortcuts", "F1", ShowShortcuts), MI("Media Info", "I", ShowMediaInfo), MI("About OmniPlayer", "", ShowAbout) });

        menu.Items.AddRange(new ToolStripItem[] { media, playback, audio, videoMenu, subs, view, help });

        // Right-click menu on the video
        var vmenu = new ContextMenuStrip();
        vmenu.Items.AddRange(new ToolStripItem[]
        {
            MI("Play / Pause", "Space", TogglePlay), MI("Fullscreen", "F", ToggleFullscreen), new ToolStripSeparator(),
            AudioTrackMenu(), SubTrackMenu(), AspectMenu(), new ToolStripSeparator(),
            MI("Take Snapshot", "Shift+S", Snapshot), MI("Media Info", "I", ShowMediaInfo), MI("Open Files…", "Ctrl+O", OpenFiles)
        });
        video.ContextMenuStrip = vmenu;

        // Right-click menu on the playlist
        var pmenu = new ContextMenuStrip();
        pmenu.Items.AddRange(new ToolStripItem[]
        {
            MI("Play", "Enter", PlaySelected), MI("Remove", "Del", RemoveSelected), MI("Show in Folder", "", ShowSelectedInFolder),
            new ToolStripSeparator(), MI("Sort by Name", "", SortByName), MI("Shuffle Order", "", ShuffleOrder),
            new ToolStripSeparator(), MI("Add Files…", "", OpenFiles), MI("Clear Playlist", "", ClearPlaylist)
        });
        list.ContextMenuStrip = pmenu;
    }

    void WireEvents()
    {
        // libVLC raises events on its own threads; hop to the UI thread.
        mp.Playing += (s, e) => Ui(OnPlaying);
        mp.Paused += (s, e) => Ui(() => RefreshRow(cur));
        mp.EndReached += (s, e) => Ui(OnEnded);
        mp.EncounteredError += (s, e) => Ui(OnError);
        mp.LengthChanged += (s, e) => Ui(() => { TryResume(); UpdateMarks(); });

        seek.HoverText = v => cur != null && mp.Length > 0 ? Fmt((long)(v * mp.Length)) : null;
        seek.Scrubbing += v => { if (mp.Length > 0) lblCur.Text = Fmt((long)(v * mp.Length)); };
        seek.Committed += v =>
        {
            if (cur == null) return;
            if (mp.State == VLCState.Ended || mp.State == VLCState.Stopped) { PlayItem(cur); return; }
            if (mp.IsSeekable) mp.Position = v;
        };
        volume.HoverText = v => $"{(int)Math.Round(v * 150)}%";
        volume.Scrubbing += v => SetVolume((int)Math.Round(v * 150));
        volume.Committed += v => SetVolume((int)Math.Round(v * 150), true);
        speed.SelectedIndexChanged += (s, e) => { if (speed.SelectedIndex >= 0 && Math.Abs(Rates[speed.SelectedIndex] - S.Rate) > 0.001f) SetRate(Rates[speed.SelectedIndex]); };

        // Video clicks: single = play/pause, double = fullscreen
        clickTimer.Interval = SystemInformation.DoubleClickTime;
        clickTimer.Tick += (s, e) => { clickTimer.Stop(); TogglePlay(); };
        // Left click = play/pause (unless it turned into a drag); drag while zoomed = pan; middle click = reset zoom
        video.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Middle) { ResetZoom(); return; }
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true; panned = false; downPt = e.Location; panCx = zcx; panCy = zcy;
        };
        video.MouseMove += (s, e) =>
        {
            if (!mouseDown || zoom <= 1f) return;
            int dx = e.X - downPt.X, dy = e.Y - downPt.Y;
            if (!panned && Math.Abs(dx) + Math.Abs(dy) < 5) return;
            if (!VideoSize(out var w, out var h)) return;
            panned = true;
            var r = DisplayRect(w, h);
            zcx = panCx - dx / r.Width / zoom;
            zcy = panCy - dy / r.Height / zoom;
            ClampCenter();
            cropDirty = true;
        };
        video.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false;
            if (panned) { panned = false; ApplyCrop(); return; }
            clickTimer.Stop(); clickTimer.Start();
        };
        zoomTimer.Tick += (s, e) => { if (cropDirty) { cropDirty = false; ApplyCrop(); } };
        zoomTimer.Start();
        Application.AddMessageFilter(this);
        video.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) { clickTimer.Stop(); ToggleFullscreen(); } };
        placeholder.Click += (s, e) => { if (items.Count == 0) OpenFiles(); else TogglePlay(); };

        // Drag & drop of files/folders anywhere
        foreach (var c in new Control[] { this, videoHost, video, placeholder, controls, split.Panel1 })
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (s, e) => DropFiles(e);
        }

        // Playlist interactions
        list.DoubleClick += (s, e) => PlaySelected();
        list.ItemDrag += (s, e) =>
        {
            if (filter.TextLength > 0) return;
            dragRows = list.SelectedItems.Cast<ListViewItem>().Select(x => (Item)x.Tag).ToList();
            if (dragRows.Count > 0) list.DoDragDrop("OmniPlayerRows", DragDropEffects.Move);
            dragRows = null;
        };
        list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : dragRows != null ? DragDropEffects.Move : DragDropEffects.None;
        list.DragOver += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : dragRows != null ? DragDropEffects.Move : DragDropEffects.None;
        list.DragDrop += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { DropFiles(e); return; }
            if (dragRows == null) return;
            var pt = list.PointToClient(new Point(e.X, e.Y));
            var target = list.GetItemAt(pt.X, pt.Y)?.Tag as Item;
            var moving = dragRows;
            if (target != null && moving.Contains(target)) return;
            foreach (var m in moving) items.Remove(m);
            int idx = target == null ? items.Count : items.IndexOf(target);
            items.InsertRange(idx, moving);
            RenderList();
        };

        ui.Tick += (s, e) => Tick();
        ui.Start();
    }

    void Ui(Action a)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    // ───────────────────────────── Playlist ─────────────────────────────

    public static bool IsUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && !u.IsFile &&
        u.Scheme is "http" or "https" or "rtsp" or "rtmp" or "rtp" or "udp" or "mms" or "mmsh" or "ftp" or "smb" or "srt";

    static Item MakeItem(string p) => new() { Path = p, IsUrl = IsUrl(p) };

    void DropFiles(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            Activate();
            AddPaths(paths);
        }
    }

    /// <summary>Adds files/folders/URLs to the playlist and starts playing the first new item.</summary>
    void AddPaths(IEnumerable<string> paths, bool play = true)
    {
        var media = new List<string>();
        var subs = new List<string>();
        foreach (var p in paths) Collect(p, media, subs, explicitFile: true);

        var added = media.Select(MakeItem).ToList();
        items.AddRange(added);
        foreach (var sp in subs) AttachSub(sp, added.Count == 0);

        if (added.Count == 0 && subs.Count == 0) { Status("No playable files found"); return; }
        if (added.Count > 0) Status($"Added {added.Count} file{(added.Count == 1 ? "" : "s")}");

        if (added.Count > 0 && (play || cur == null))
        {
            if (cur != null) hist.Add(cur);
            PlayItem(added[0]);
        }
        else RenderList(true);
        ProbeDurations();
    }

    void Collect(string p, List<string> media, List<string> subs, bool explicitFile)
    {
        try
        {
            if (IsUrl(p)) { media.Add(p); return; }
            if (Directory.Exists(p))
            {
                var found = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .ToList();
                found.Sort(StrCmpLogicalW);
                foreach (var f in found) Collect(f, media, subs, explicitFile: false);
                return;
            }
            if (!File.Exists(p)) return;
            var ext = Path.GetExtension(p);
            if (ListExt.Contains(ext)) { ReadM3u(p, media); return; }
            if (SubExt.Contains(ext)) { if (ext != ".txt" || explicitFile) subs.Add(p); return; }
            if (MediaExt.Contains(ext) || explicitFile) media.Add(p);
        }
        catch { }
    }

    static void ReadM3u(string file, List<string> media)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (IsUrl(line)) { media.Add(line); continue; }
            var full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
            if (File.Exists(full)) media.Add(full);
        }
    }

    void AttachSub(string subPath, bool allowCurrent)
    {
        string sb = Path.GetFileNameWithoutExtension(subPath).ToLowerInvariant();
        var match = items.FirstOrDefault(it => !it.IsUrl && Path.GetFileNameWithoutExtension(it.Path).ToLowerInvariant() == sb)
                 ?? items.FirstOrDefault(it => !it.IsUrl && sb.StartsWith(Path.GetFileNameWithoutExtension(it.Path).ToLowerInvariant() + "."));
        if (match == null && allowCurrent) match = cur;
        if (match == null) { Status("No matching video for " + Path.GetFileName(subPath)); return; }
        if (!match.Subs.Contains(subPath, StringComparer.OrdinalIgnoreCase)) match.Subs.Add(subPath);
        if (match == cur)
        {
            mp.AddSlave(MediaSlaveType.Subtitle, new Uri(subPath).AbsoluteUri, true);
            Status("Subtitles loaded: " + Path.GetFileName(subPath));
        }
        RefreshRow(match);
    }

    static string DurText(Item it) => it.IsUrl ? "stream" : it.Duration > 0 ? Fmt(it.Duration) : it.Duration == -1 ? "…" : "";

    void RenderList(bool scroll = false)
    {
        string q = filter.Text.Trim();
        list.BeginUpdate();
        list.Items.Clear();
        rows.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (q.Length > 0 && it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var lvi = new ListViewItem(new[] { "", "", "" }) { Tag = it, ToolTipText = it.Path };
            rows[it] = lvi;
            FillRow(lvi, it, i);
            list.Items.Add(lvi);
        }
        list.EndUpdate();
        UpdateInfo();
        if (scroll && cur != null && rows.TryGetValue(cur, out var r)) r.EnsureVisible();
        ResizeColumns();
    }

    void FillRow(ListViewItem lvi, Item it, int index)
    {
        bool isCur = it == cur;
        lvi.SubItems[0].Text = isCur ? (mp.IsPlaying ? "▶" : "❚❚") : (index + 1).ToString();
        lvi.SubItems[1].Text = it.Name + (it.Subs.Count > 0 ? "   [CC]" : "");
        lvi.SubItems[2].Text = DurText(it);
        lvi.ForeColor = isCur ? Theme.Accent : it.Bad ? Theme.Muted : Theme.Text;
        lvi.Font = isCur ? boldFont : list.Font;
    }

    void RefreshRow(Item it)
    {
        if (it != null && rows.TryGetValue(it, out var lvi)) FillRow(lvi, it, items.IndexOf(it));
    }

    void UpdateInfo()
    {
        long total = items.Where(x => x.Duration > 0).Sum(x => x.Duration);
        plInfo.Text = items.Count == 0 ? "Empty — drop files here"
            : $"{items.Count} item{(items.Count == 1 ? "" : "s")}{(total > 0 ? "  ·  " + Fmt(total) : "")}";
    }

    void ResizeColumns()
    {
        int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[2].Width - 4;
        if (w > 60 && list.Columns[1].Width != w) list.Columns[1].Width = w;
    }

    async void ProbeDurations()
    {
        if (probing) return;
        probing = true;
        try
        {
            while (!IsDisposed)
            {
                var it = items.FirstOrDefault(x => x.Duration == -1 && !x.IsUrl);
                if (it == null) break;
                try
                {
                    using var m = new Media(vlc, it.Path, FromType.FromPath);
                    await m.Parse(MediaParseOptions.ParseLocal, 5000);
                    it.Duration = m.Duration > 0 ? m.Duration : -2;
                }
                catch { it.Duration = -2; }
                if (IsDisposed) return;
                RefreshRow(it);
                UpdateInfo();
            }
        }
        finally { probing = false; }
    }

    Item SelectedItem() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Item : null;

    void PlaySelected() { var it = SelectedItem(); if (it != null) { if (cur != null && cur != it) hist.Add(cur); PlayItem(it); } }

    void RemoveSelected()
    {
        var sel = list.SelectedItems.Cast<ListViewItem>().Select(x => (Item)x.Tag).ToList();
        if (sel.Count == 0) return;
        if (sel.Contains(cur)) { SavePos(); mp.Stop(); cur = null; ShowIdle(); }
        foreach (var it in sel) { items.Remove(it); hist.Remove(it); if (queued == it) queued = null; }
        RenderList();
    }

    void ShowSelectedInFolder()
    {
        var it = SelectedItem();
        if (it == null || it.IsUrl || !File.Exists(it.Path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{it.Path}\""); } catch { }
    }

    void SortByName()
    {
        items.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
        RenderList(true);
    }

    void ShuffleOrder()
    {
        for (int i = items.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (items[i], items[j]) = (items[j], items[i]); }
        RenderList(true);
    }

    void ClearPlaylist()
    {
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"Remove all {items.Count} items from the playlist?", "OmniPlayer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        SavePos(); mp.Stop();
        items.Clear(); hist.Clear(); cur = null; queued = null;
        ShowIdle(); RenderList();
    }

    void SavePlaylist()
    {
        if (items.Count == 0) { Status("Playlist is empty"); return; }
        using var d = new SaveFileDialog { Filter = "M3U playlist (*.m3u8)|*.m3u8", FileName = "playlist.m3u8" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var it in items)
        {
            sb.Append($"#EXTINF:{(it.Duration > 0 ? it.Duration / 1000 : -1)},{it.Name}\n");
            sb.Append(it.Path).Append('\n');
        }
        File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(false));
        Status("Playlist saved");
    }

    // ───────────────────────────── Open dialogs ─────────────────────────────

    void OpenFiles()
    {
        using var d = new OpenFileDialog
        {
            Multiselect = true, Title = "Open media files",
            Filter = "Media files|" + string.Join(";", MediaExt.Concat(ListExt).Select(x => "*" + x)) +
                     "|Subtitles|" + string.Join(";", SubExt.Where(x => x != ".txt").Select(x => "*" + x)) + "|All files|*.*"
        };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
    }

    void OpenFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Add every media file in this folder (and its subfolders)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
    }

    void OpenUrl()
    {
        string initial = "";
        try { var c = Clipboard.GetText(); if (IsUrl(c.Trim())) initial = c.Trim(); } catch { }
        var url = Dialogs.Prompt(this, "Open Stream", "Network URL (http, https, rtsp, rtmp, udp, ftp, smb…):", initial);
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!IsUrl(url)) { Status("That doesn't look like a supported URL"); return; }
        var it = MakeItem(url);
        items.Add(it);
        PlayItem(it);
    }

    void AddSubtitleFile()
    {
        if (cur == null) { Status("Play a video first, then add its subtitles"); return; }
        using var d = new OpenFileDialog { Title = "Add subtitles", Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.smi;*.txt|All files|*.*" };
        if (!cur.IsUrl) try { d.InitialDirectory = Path.GetDirectoryName(cur.Path); } catch { }
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!cur.Subs.Contains(d.FileName)) cur.Subs.Add(d.FileName);
        mp.AddSlave(MediaSlaveType.Subtitle, new Uri(d.FileName).AbsoluteUri, true);
        Status("Subtitles loaded: " + Path.GetFileName(d.FileName));
        RefreshRow(cur);
    }

    // ───────────────────────────── Playback ─────────────────────────────

    void PlayItem(Item it)
    {
        if (it == null) return;
        if (cur != null && cur != it) SavePos();
        var prev = cur;
        cur = it; queued = null;
        abA = abB = -1; resumePending = true; lastSpu = -1;
        zoom = 1f; zcx = zcy = 0.5f; cropDirty = false; fullW = fullH = 0; video.Cursor = Cursors.Default;
        try { mp.CropGeometry = null; } catch { }
        Media media;
        try
        {
            media = it.IsUrl ? new Media(vlc, new Uri(it.Path)) : new Media(vlc, it.Path, FromType.FromPath);
        }
        catch (Exception ex) { Status("Can't open: " + ex.Message); return; }
        foreach (var s in it.Subs)
            try { media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(s).AbsoluteUri); } catch { }
        mp.Play(media);
        media.Dispose();

        placeholder.Visible = false;
        Text = it.Name + " — OmniPlayer";
        Status("Opening " + it.Name);
        UpdateMarks();
        if (prev != null) RefreshRow(prev);
        RenderList(true);
    }

    void OnPlaying()
    {
        errRun = 0;
        if (cur != null) cur.Bad = false;
        mp.SetRate(S.Rate);
        mp.Volume = S.Volume;
        mp.Mute = S.Muted;
        TryResume();
        RefreshRow(cur);
    }

    void TryResume()
    {
        if (!resumePending || cur == null) return;
        long len = mp.Length;
        if (len <= 0) return;
        resumePending = false;
        if (cur.Duration <= 0) { cur.Duration = len; RefreshRow(cur); UpdateInfo(); }
        if (S.Resume && !cur.IsUrl && S.Positions.TryGetValue(cur.Path, out var pos) && len > 120000 && pos < len - 15000)
        {
            mp.Time = pos;
            Status($"Resumed at {Fmt(pos)} — press 0 to start over", 5000);
        }
        else Status("Playing " + cur.Name);
    }

    void OnEnded()
    {
        if (cur != null) S.Positions.Remove(cur.Path);
        if (S.Repeat == 2 && cur != null) { PlayItem(cur); return; }
        Next(false);
    }

    void OnError()
    {
        if (cur == null) return;
        cur.Bad = true;
        RefreshRow(cur);
        Status($"Can't play \"{cur.Name}\" — the file may be damaged or not a media file", 5000);
        errRun++;
        if (errRun < items.Count)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Next(false); };
            t.Start();
        }
    }

    void Next(bool manual)
    {
        if (items.Count == 0) return;
        if (S.Shuffle && items.Count > 1)
        {
            Item n; do n = items[rng.Next(items.Count)]; while (n == cur);
            if (cur != null) hist.Add(cur);
            PlayItem(n); return;
        }
        int i = cur == null ? 0 : items.IndexOf(cur) + 1;
        if (i >= items.Count)
        {
            if (S.Repeat == 1 || manual) i = 0;
            else { Status("End of playlist"); RefreshRow(cur); return; }
        }
        if (cur != null) hist.Add(cur);
        PlayItem(items[i]);
    }

    void Prev()
    {
        if (items.Count == 0) return;
        var st = mp.State;
        if (cur != null && st != VLCState.Ended && st != VLCState.Stopped && mp.Time > 3000) { mp.Time = 0; return; }
        if (S.Shuffle && hist.Count > 0)
        {
            var h = hist[^1]; hist.RemoveAt(hist.Count - 1);
            if (items.Contains(h)) { PlayItem(h); return; }
        }
        int i = cur == null ? 0 : items.IndexOf(cur) - 1;
        if (i < 0) i = items.Count - 1;
        PlayItem(items[i]);
    }

    void TogglePlay()
    {
        if (cur == null)
        {
            var start = queued ?? SelectedItem() ?? items.FirstOrDefault();
            if (start != null) PlayItem(start); else OpenFiles();
            return;
        }
        switch (mp.State)
        {
            case VLCState.Playing: mp.SetPause(true); break;
            case VLCState.Paused: mp.SetPause(false); break;
            case VLCState.Opening: case VLCState.Buffering: break;
            default: PlayItem(cur); break;
        }
    }

    void StopPlayback()
    {
        if (cur == null) return;
        SavePos();
        mp.Stop();
        Status("Stopped");
        seek.Value = 0; lblCur.Text = "0:00";
        RefreshRow(cur);
    }

    void ShowIdle()
    {
        placeholder.Visible = true;
        Text = "OmniPlayer";
        seek.Value = 0; lblCur.Text = lblDur.Text = "0:00";
        abA = abB = -1; UpdateMarks();
    }

    bool Ready => cur != null && mp.Length > 0 && mp.State != VLCState.Ended && mp.State != VLCState.Stopped;

    void Skip(long ms)
    {
        if (!Ready) return;
        long t = Math.Clamp(mp.Time + ms, 0, Math.Max(0, mp.Length - 500));
        mp.Time = t;
        Status($"{(ms > 0 ? "+" : "−")}{Math.Abs(ms) / 1000} s  ·  {Fmt(t)}");
    }

    void FrameStep()
    {
        if (!Ready) return;
        mp.NextFrame();
        Status("Next frame  ·  " + Fmt(mp.Time));
    }

    void JumpPercent(int tenth)
    {
        if (!Ready) return;
        mp.Time = mp.Length * tenth / 10;
        Status($"Jumped to {tenth * 10}%  ·  {Fmt(mp.Time)}");
    }

    void JumpToTime()
    {
        if (!Ready) return;
        var s = Dialogs.Prompt(this, "Jump to Time", "Time (h:mm:ss, mm:ss or seconds):", Fmt(mp.Time));
        if (string.IsNullOrWhiteSpace(s)) return;
        double secs = 0;
        foreach (var part in s.Split(':'))
        {
            if (!double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            { Status("Couldn't read that time"); return; }
            secs = secs * 60 + v;
        }
        mp.Time = Math.Clamp((long)(secs * 1000), 0, mp.Length - 500);
    }

    void SetVolume(int v, bool announce = false)
    {
        v = Math.Clamp(v, 0, 150);
        S.Volume = v;
        mp.Volume = v;
        if (v > 0 && S.Muted) { S.Muted = false; mp.Mute = false; }
        UpdateVolumeUi();
        if (announce) Status($"Volume {v}%");
    }

    void ToggleMute()
    {
        if (S.Volume == 0) { SetVolume(50, true); return; }
        S.Muted = !S.Muted;
        mp.Mute = S.Muted;
        UpdateVolumeUi();
        Status(S.Muted ? "Muted" : $"Volume {S.Volume}%");
    }

    void UpdateVolumeUi()
    {
        bool silent = S.Muted || S.Volume == 0;
        bMute.Text = silent ? Glyph.Mute : Glyph.Volume;
        if (!volume.Dragging) volume.Value = S.Muted ? 0 : S.Volume / 150f;
        if (miMute != null) miMute.Checked = S.Muted;
    }

    void SetRate(float r)
    {
        S.Rate = r;
        mp.SetRate(r);
        int i = Array.FindIndex(Rates, x => Math.Abs(x - r) < 0.001f);
        if (i >= 0 && speed.SelectedIndex != i) speed.SelectedIndex = i;
        Status($"Speed {r:0.##}×");
    }

    void StepRate(int dir)
    {
        int i = Array.FindIndex(Rates, x => x >= S.Rate - 0.001f);
        if (i < 0) i = Rates.Length - 1;
        SetRate(Rates[Math.Clamp(i + dir, 0, Rates.Length - 1)]);
    }

    void ToggleShuffle()
    {
        S.Shuffle = !S.Shuffle;
        UpdateToggles();
        Status("Shuffle " + (S.Shuffle ? "on" : "off"));
    }

    void CycleRepeat()
    {
        S.Repeat = (S.Repeat + 1) % 3;
        UpdateToggles();
        Status("Repeat: " + new[] { "off", "all", "one track" }[S.Repeat]);
    }

    void UpdateToggles()
    {
        bShuffle.ForeColor = S.Shuffle ? Theme.Accent : Theme.Text;
        miShuffle.Checked = S.Shuffle;
        bRepeat.Text = S.Repeat == 2 ? Glyph.RepeatOne : Glyph.RepeatAll;
        bRepeat.ForeColor = S.Repeat > 0 ? Theme.Accent : Theme.Text;
        tips.SetToolTip(bRepeat, "Repeat: " + new[] { "off", "all", "one" }[S.Repeat] + " (R)");
        bPlaylist.ForeColor = S.ShowPlaylist ? Theme.Accent : Theme.Text;
        miPlaylist.Checked = S.ShowPlaylist;
        miOnTop.Checked = S.AlwaysOnTop;
        miResume.Checked = S.Resume;
        miRestore.Checked = S.RestorePlaylist;
    }

    void CycleAB()
    {
        if (!Ready) return;
        if (abA < 0) { abA = mp.Time; Status("Loop start (A) at " + Fmt(abA)); }
        else if (abB < 0)
        {
            if (mp.Time <= abA + 300) { Status("Loop end must come after " + Fmt(abA)); return; }
            abB = mp.Time; mp.Time = abA;
            Status($"Looping {Fmt(abA)} – {Fmt(abB)}  (B again to clear)");
        }
        else { abA = abB = -1; Status("A–B loop off"); }
        UpdateMarks();
    }

    void UpdateMarks()
    {
        long len = mp.Length;
        seek.SetMarks(abA >= 0 && len > 0 ? abA / (float)len : null, abB >= 0 && len > 0 ? abB / (float)len : null);
        bAB.Text = abA < 0 ? "A–B" : abB < 0 ? "A…" : "A–B";
        bAB.ForeColor = abA >= 0 ? Theme.Warn : Theme.Text;
    }

    void ToggleSubs()
    {
        if (cur == null) return;
        TrackDescription[] tracks = null;
        try { tracks = mp.SpuDescription; } catch { }
        var real = tracks?.Where(t => t.Id != -1).ToArray() ?? Array.Empty<TrackDescription>();
        if (real.Length == 0) { AddSubtitleFile(); return; }
        if (mp.Spu != -1) { lastSpu = mp.Spu; mp.SetSpu(-1); Status("Subtitles off"); }
        else
        {
            int id = real.Any(t => t.Id == lastSpu) ? lastSpu : real[0].Id;
            mp.SetSpu(id);
            Status("Subtitles on: " + real.First(t => t.Id == id).Name);
        }
        bCC.ForeColor = mp.Spu != -1 ? Theme.Accent : Theme.Text;
    }

    void ShowSubtitleMenu()
    {
        var m = new ContextMenuStrip();
        var tracks = SubTrackMenu();
        m.Items.Add(MI("Subtitles On / Off", "C", ToggleSubs));
        m.Items.Add(tracks);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(MI("Delay +100 ms", "H", () => SubDelay(100)));
        m.Items.Add(MI("Delay −100 ms", "G", () => SubDelay(-100)));
        m.Items.Add(MI("Add Subtitle File…", "", AddSubtitleFile));
        m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
        m.Show(bCC, new Point(0, -m.PreferredSize.Height));
    }

    void CycleAudio()
    {
        if (cur == null) return;
        var tracks = (mp.AudioTrackDescription ?? Array.Empty<TrackDescription>()).Where(t => t.Id != -1).ToArray();
        if (tracks.Length <= 1) { Status(tracks.Length == 1 ? "Only one audio track" : "No audio tracks"); return; }
        int i = Array.FindIndex(tracks, t => t.Id == mp.AudioTrack);
        var nt = tracks[(i + 1) % tracks.Length];
        mp.SetAudioTrack(nt.Id);
        Status("Audio: " + nt.Name);
    }

    void SubDelay(long ms, bool reset = false)
    {
        if (cur == null) return;
        long us = reset ? 0 : mp.SpuDelay + ms * 1000;
        mp.SetSpuDelay(us);
        Status($"Subtitle delay {us / 1000} ms");
    }

    void AudioDelay(long ms, bool reset = false)
    {
        if (cur == null) return;
        long us = reset ? 0 : mp.AudioDelay + ms * 1000;
        mp.SetAudioDelay(us);
        Status($"Audio delay {us / 1000} ms");
    }

    static string SnapshotDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OmniPlayer");

    void Snapshot()
    {
        if (!Ready) return;
        var dir = SnapshotDir();
        Directory.CreateDirectory(dir);
        string name = cur.IsUrl ? "stream" : Path.GetFileNameWithoutExtension(cur.Path);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var file = Path.Combine(dir, $"{name}_{Fmt(mp.Time).Replace(':', '-')}.png");
        bool ok = mp.TakeSnapshot(0, file, 0, 0);
        Status(ok ? "Snapshot saved to Pictures\\OmniPlayer" : "Snapshot failed (no video picture?)", 4000);
    }

    static void OpenFolderInExplorer(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
    }

    void ApplyEq()
    {
        if (!S.EqEnabled) { mp.UnsetEqualizer(); return; }
        eq ??= new Equalizer();
        eq.SetPreamp(S.EqPreamp);
        for (uint i = 0; i < eq.BandCount && i < S.EqBands.Length; i++) eq.SetAmp(S.EqBands[i], i);
        mp.SetEqualizer(eq);
    }

    void ShowEq()
    {
        if (eqForm == null || eqForm.IsDisposed) eqForm = new EqualizerForm(S, ApplyEq);
        if (!eqForm.Visible)
        {
            eqForm.Location = new Point(Left + (Width - eqForm.Width) / 2, Top + Math.Max(40, (Height - eqForm.Height) / 2));
            eqForm.Show(this);
        }
        eqForm.Activate();
    }

    // ───────────────────────────── Window / view ─────────────────────────────

    /// <summary>Sets playlist width/min sizes once the window has its real size (doing it earlier throws).</summary>
    void ApplySplit()
    {
        try
        {
            int w = split.Width;
            if (w < 600) return;
            split.Panel1MinSize = 300;
            split.Panel2MinSize = 220;
            split.SplitterDistance = Math.Clamp(w - S.PlaylistWidth, 300, w - 220 - split.SplitterWidth);
        }
        catch (Exception ex) { Log.Write("Split layout: " + ex.Message); }
    }

    void TogglePlaylist()
    {
        S.ShowPlaylist = !S.ShowPlaylist;
        if (!fs) split.Panel2Collapsed = !S.ShowPlaylist;
        UpdateToggles();
    }

    void ToggleFullscreen()
    {
        SuspendLayout();
        if (!fs)
        {
            fs = true;
            prevState = WindowState;
            menu.Visible = false;
            split.Panel2Collapsed = true;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
            bFull.Text = Glyph.ExitFullScreen;
            lastMove = DateTime.Now;
        }
        else
        {
            fs = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = prevState;
            menu.Visible = true;
            controls.Visible = true;
            split.Panel2Collapsed = !S.ShowPlaylist;
            bFull.Text = Glyph.FullScreen;
            ShowCursor(true);
        }
        ResumeLayout(true);
    }

    void FullscreenIdle()
    {
        var p = Cursor.Position;
        if (p != lastMouse) { lastMouse = p; lastMove = DateTime.Now; }
        var cp = PointToClient(p);
        bool overControls = controls.Visible && controls.Bounds.Contains(cp);
        bool show = (DateTime.Now - lastMove).TotalMilliseconds < 2500 || !mp.IsPlaying || overControls;
        if (controls.Visible != show) controls.Visible = show;
        ShowCursor(show);
    }

    void ShowCursor(bool v)
    {
        if (v && cursorHidden) { Cursor.Show(); cursorHidden = false; }
        else if (!v && !cursorHidden) { Cursor.Hide(); cursorHidden = true; }
    }

    void Status(string text, int ms = 2500)
    {
        status.Text = text;
        statusUntil = DateTime.Now.AddMilliseconds(ms);
    }

    void Tick()
    {
        bool playing = mp.IsPlaying;
        string g = playing ? Glyph.Pause : Glyph.Play;
        if (bPlay.Text != g) bPlay.Text = g;
        if (playing != lastPlaying) { lastPlaying = playing; RefreshRow(cur); }

        long len = mp.Length;
        if (cur != null && len > 0 && mp.State != VLCState.Stopped && mp.State != VLCState.Ended)
        {
            long t = mp.Time;
            if (!seek.Dragging) { seek.Value = (float)Math.Clamp(t / (double)len, 0, 1); lblCur.Text = Fmt(t); }
            string d = Fmt(len); if (lblDur.Text != d) lblDur.Text = d;
            if (abB >= 0 && t >= abB) mp.Time = abA;
        }

        if (playing && (DateTime.Now - lastPosSave).TotalSeconds > 5) { lastPosSave = DateTime.Now; SavePos(); }
        if (fs) FullscreenIdle();
        if (DateTime.Now > statusUntil && statusUntil != DateTime.MinValue)
        {
            statusUntil = DateTime.MinValue;
            status.Text = cur != null ? cur.Name : "";
        }
        bool subsOn = false;
        if (cur != null) try { subsOn = mp.Spu != -1; } catch { }
        var cc = subsOn ? Theme.Accent : Theme.Text;
        if (bCC.ForeColor != cc) bCC.ForeColor = cc;
    }

    void SavePos()
    {
        if (cur == null || cur.IsUrl || !S.Resume) return;
        long len = mp.Length, t = mp.Time;
        if (len <= 0) return;
        if (len > 120000 && t > 10000 && t < len - 15000) S.Positions[cur.Path] = t;
        else S.Positions.Remove(cur.Path);
        while (S.Positions.Count > 400) S.Positions.Remove(S.Positions.Keys.First());
    }

    void ApplySettings()
    {
        mp.Volume = S.Volume;
        mp.Mute = S.Muted;
        UpdateVolumeUi();
        int ri = Array.FindIndex(Rates, x => Math.Abs(x - S.Rate) < 0.001f);
        if (ri < 0) { S.Rate = 1f; ri = 3; }
        speed.SelectedIndex = ri;
        mp.SetRate(S.Rate);
        split.Panel2Collapsed = !S.ShowPlaylist;
        TopMost = S.AlwaysOnTop;
        UpdateToggles();
        ApplyEq();
    }

    void ResetSettings()
    {
        if (MessageBox.Show(this, "Reset volume, speed, equalizer, shuffle/repeat and window options to defaults?\n\nYour playlist is kept.",
            "Reset settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var keep = S.Positions;
        S = new Settings { Positions = keep };
        eqForm?.Dispose(); eqForm = null;
        ApplySettings();
        ApplySplit();
        Status("Settings reset to defaults");
    }

    void ShowMediaInfo()
    {
        if (cur == null) { Status("Nothing is playing"); return; }
        var sb = new StringBuilder();
        sb.AppendLine(cur.Name);
        sb.AppendLine(new string('─', 52));
        sb.AppendLine($"Location : {cur.Path}");
        if (!cur.IsUrl) try { sb.AppendLine($"Size     : {new FileInfo(cur.Path).Length / 1048576.0:0.0} MB"); } catch { }
        sb.AppendLine($"Length   : {Fmt(mp.Length)}");
        try
        {
            uint w = 0, h = 0;
            if (mp.Size(0, ref w, ref h) && w > 0) sb.AppendLine($"Picture  : {w} × {h}");
        }
        catch { }
        sb.AppendLine($"Hardware decoding : {(mp.EnableHardwareDecoding ? "on (when the GPU supports the codec)" : "off")}");
        sb.AppendLine();
        try
        {
            var media = mp.Media;
            if (media != null)
            {
                foreach (var t in media.Tracks)
                {
                    string codec = FourCC(t.Codec);
                    switch (t.TrackType)
                    {
                        case TrackType.Video:
                            sb.AppendLine($"Video    : {codec}  {t.Data.Video.Width}×{t.Data.Video.Height}" +
                                (t.Data.Video.FrameRateDen > 0 ? $"  {t.Data.Video.FrameRateNum / (double)t.Data.Video.FrameRateDen:0.###} fps" : ""));
                            break;
                        case TrackType.Audio:
                            sb.AppendLine($"Audio    : {codec}  {t.Data.Audio.Channels} ch  {t.Data.Audio.Rate} Hz" + (string.IsNullOrEmpty(t.Language) ? "" : $"  [{t.Language}]"));
                            break;
                        case TrackType.Text:
                            sb.AppendLine($"Subtitle : {codec}" + (string.IsNullOrEmpty(t.Language) ? "" : $"  [{t.Language}]"));
                            break;
                    }
                }
            }
        }
        catch { sb.AppendLine("(track details unavailable)"); }
        Dialogs.Info(this, "Media Info", sb.ToString());
    }

    static string FourCC(uint c)
    {
        var s = new string(new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 24) & 0xFF) }).Trim('\0', ' ');
        return s.ToLowerInvariant() switch
        {
            "hevc" or "h265" or "hev1" or "hvc1" => "HEVC / H.265",
            "h264" or "avc1" => "H.264 / AVC",
            "av01" => "AV1",
            "vp90" => "VP9",
            "vp80" => "VP8",
            "mp4a" => "AAC",
            "mpga" => "MP3",
            "a52 " or "a52" => "AC-3 (Dolby Digital)",
            "eac3" => "E-AC-3 (Dolby Digital Plus)",
            "dts " or "dts" => "DTS",
            "flac" => "FLAC",
            "opus" => "Opus",
            "vorb" => "Vorbis",
            _ => s.ToUpperInvariant()
        };
    }

    void ShowShortcuts()
    {
        Dialogs.Info(this, "Keyboard Shortcuts",
@"PLAYBACK
  Space / K          Play / pause
  ← / →              Back / forward 5 s
  Shift + ← / →      Back / forward 30 s
  Ctrl + ← / →       Back / forward 1 min
  J / L              Back / forward 10 s
  .                  Next frame
  0 – 9              Jump to 0 % – 90 %
  Ctrl + T           Jump to a time
  N / P              Next / previous
  [ / ]  /  =        Slower / faster / normal speed
  B                  A–B loop (start, end, clear)
  S / R              Shuffle / repeat (off, all, one)

AUDIO
  ↑ / ↓              Volume (up to 150 %)
  M                  Mute
  A                  Next audio track
  E                  Equalizer

VIDEO & SUBTITLES
  F / F11 / dbl-click  Fullscreen   (Esc exits)
  C                  Subtitles on / off
  G / H              Subtitle delay −/+ 100 ms
  Mouse wheel        Zoom in / out at the cursor (up to 10×)
  Drag (zoomed)      Move around the zoomed picture
  Ctrl + = / −       Zoom in / out      Z or middle-click  Reset zoom
  Shift + S          Snapshot → Pictures\OmniPlayer
  I                  Media info (codecs, resolution)

FILES
  Ctrl + O           Open files
  Ctrl + Shift + O   Open folder
  Ctrl + U           Open stream / URL
  Ctrl + L           Show / hide playlist
  Enter / Del        Play / remove selected playlist item
  F1                 This list");
    }

    void ShowAbout()
    {
        Dialogs.Info(this, "About OmniPlayer",
@"OmniPlayer 1.0

A personal media player built on libVLC — the
playback engine from VideoLAN (the makers of VLC).

It carries its own codecs, so it plays HEVC/H.265,
AV1, VP9, H.264, MPEG-2/4, DivX/Xvid, WMV, ProRes,
MKV, AVI, MOV, TS, FLAC, ALAC, DTS, AC-3, Opus and
much more — no Windows codec extensions needed.

Settings are stored in:
  %AppData%\OmniPlayer\settings.json

libVLC is licensed under the LGPL 2.1.");
    }

    /// <summary>Files handed over by a second launch (e.g. double-clicking a video while OmniPlayer is open).</summary>
    public void ReceiveArgs(string[] args)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        var paths = args.Where(a => IsUrl(a) || File.Exists(a) || Directory.Exists(a)).ToList();
        if (paths.Count == 0) return;
        // Selecting several files and pressing Enter launches one copy per file: play the first, queue the rest.
        bool play = (DateTime.Now - lastExternalOpen).TotalSeconds > 2;
        lastExternalOpen = DateTime.Now;
        AddPaths(paths, play);
    }

    void MakeDefault()
    {
        try { FileAssoc.Register(AssociableExtensions); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't register with Windows:\n\n" + ex.Message, "OmniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        MessageBox.Show(this,
            "Windows will now open Settings → Default apps on OmniPlayer's page.\n\n" +
            "Click \"Set default\" at the top to use OmniPlayer for all its file types,\n" +
            "or pick individual types (.mp4, .mkv, .mp3 …) from the list below it.\n\n" +
            "(Windows requires you to confirm this yourself — apps can't change defaults on their own.)",
            "Make OmniPlayer your default player", MessageBoxButtons.OK, MessageBoxIcon.Information);
        FileAssoc.OpenDefaultAppsSettings();
    }

    void RemoveRegistration()
    {
        if (MessageBox.Show(this, "Remove OmniPlayer from Windows \"Open with\" and Default apps?\n\n(Files currently set to open with OmniPlayer will go back to asking.)",
            "OmniPlayer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        FileAssoc.Unregister();
        Status("Removed from Windows file associations");
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (filter.Focused)
        {
            if (keyData == Keys.Escape) { filter.Clear(); list.Focus(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        if (list.Focused)
        {
            switch (keyData)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                case Keys.Shift | Keys.Up: case Keys.Shift | Keys.Down: case Keys.Control | Keys.A:
                    return base.ProcessCmdKey(ref msg, keyData);
                case Keys.Enter: PlaySelected(); return true;
                case Keys.Delete: RemoveSelected(); return true;
            }
        }
        if (HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    bool HandleKey(Keys k)
    {
        switch (k)
        {
            case Keys.Space: case Keys.K: TogglePlay(); return true;
            case Keys.Left: Skip(-5000); return true;
            case Keys.Right: Skip(5000); return true;
            case Keys.Shift | Keys.Left: Skip(-30000); return true;
            case Keys.Shift | Keys.Right: Skip(30000); return true;
            case Keys.Control | Keys.Left: Skip(-60000); return true;
            case Keys.Control | Keys.Right: Skip(60000); return true;
            case Keys.J: Skip(-10000); return true;
            case Keys.L: Skip(10000); return true;
            case Keys.Up: SetVolume(S.Volume + 5, true); return true;
            case Keys.Down: SetVolume(S.Volume - 5, true); return true;
            case Keys.M: ToggleMute(); return true;
            case Keys.F: case Keys.F11: ToggleFullscreen(); return true;
            case Keys.Escape: if (fs) { ToggleFullscreen(); return true; } return false;
            case Keys.N: Next(true); return true;
            case Keys.P: Prev(); return true;
            case Keys.MediaNextTrack: Next(true); return true;
            case Keys.MediaPreviousTrack: Prev(); return true;
            case Keys.MediaPlayPause: TogglePlay(); return true;
            case Keys.MediaStop: StopPlayback(); return true;
            case Keys.S: ToggleShuffle(); return true;
            case Keys.Shift | Keys.S: Snapshot(); return true;
            case Keys.R: CycleRepeat(); return true;
            case Keys.B: CycleAB(); return true;
            case Keys.C: ToggleSubs(); return true;
            case Keys.A: CycleAudio(); return true;
            case Keys.G: SubDelay(-100); return true;
            case Keys.H: SubDelay(100); return true;
            case Keys.E: ShowEq(); return true;
            case Keys.I: ShowMediaInfo(); return true;
            case Keys.OemOpenBrackets: StepRate(-1); return true;
            case Keys.OemCloseBrackets: StepRate(1); return true;
            case Keys.Oemplus: SetRate(1f); return true;
            case Keys.OemPeriod: FrameStep(); return true;
            case Keys.Control | Keys.O: OpenFiles(); return true;
            case Keys.Control | Keys.Shift | Keys.O: OpenFolder(); return true;
            case Keys.Control | Keys.U: OpenUrl(); return true;
            case Keys.Control | Keys.L: TogglePlaylist(); return true;
            case Keys.Control | Keys.T: JumpToTime(); return true;
            case Keys.F1: ShowShortcuts(); return true;
            case Keys.Control | Keys.Oemplus: case Keys.Add: ZoomCenter(1.25f); return true;
            case Keys.Control | Keys.OemMinus: case Keys.Subtract: ZoomCenter(0.8f); return true;
            case Keys.Z: ResetZoom(); return true;
        }
        if (k >= Keys.D0 && k <= Keys.D9) { JumpPercent(k - Keys.D0); return true; }
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) { JumpPercent(k - Keys.NumPad0); return true; }
        return false;
    }

    // ───────────────────────────── Shutdown ─────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        ui.Stop();
        ShowCursor(true);
        SavePos();
        S.Playlist = items.Select(x => x.Path).ToList();
        S.PlaylistIndex = cur != null ? items.IndexOf(cur) : -1;
        if (fs) { S.Maximized = prevState == FormWindowState.Maximized; }
        else
        {
            S.Maximized = WindowState == FormWindowState.Maximized;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            S.X = b.X; S.Y = b.Y; S.W = b.Width; S.H = b.Height;
        }
        if (!split.Panel2Collapsed) S.PlaylistWidth = split.Width - split.SplitterDistance;
        S.Save();
        try { mp.Stop(); } catch { }
    }

    // ───────────────────────────── Zoom & pan ─────────────────────────────

    bool VideoSize(out uint w, out uint h)
    {
        w = h = 0;
        if (cur == null) return false;
        if (fullW > 0 && fullH > 0) { w = fullW; h = fullH; return true; }
        if (zoom > 1.001f) return false;                       // never measure while cropped
        try
        {
            if (mp.Size(0, ref w, ref h) && w > 0 && h > 0) { fullW = w; fullH = h; Log.Write($"Zoom: full frame {w}x{h}"); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>Where the (letterboxed) picture sits inside the video panel.</summary>
    RectangleF DisplayRect(uint w, uint h)
    {
        float vw = Math.Max(1, video.Width), vh = Math.Max(1, video.Height);
        float sc = Math.Min(vw / w, vh / h);
        float dw = w * sc, dh = h * sc;
        return new RectangleF((vw - dw) / 2, (vh - dh) / 2, dw, dh);
    }

    void ClampCenter()
    {
        float half = 0.5f / zoom;
        zcx = Math.Clamp(zcx, half, 1 - half);
        zcy = Math.Clamp(zcy, half, 1 - half);
    }

    void ApplyCrop()
    {
        try
        {
            if (zoom <= 1.001f) { zoom = 1f; zcx = zcy = 0.5f; mp.CropGeometry = null; video.Cursor = Cursors.Default; return; }
            if (!VideoSize(out var w, out var h)) return;
            ClampCenter();
            int cw = Math.Max(16, (int)(w / zoom)), ch = Math.Max(16, (int)(h / zoom));
            int x = (int)Math.Clamp(zcx * w - cw / 2f, 0, w - cw);
            int y = (int)Math.Clamp(zcy * h - ch / 2f, 0, h - ch);
            string geo = $"{cw}x{ch}+{x}+{y}";
            mp.CropGeometry = geo;
            Log.Write($"Zoom {zoom:0.00}x → crop {geo}");
            video.Cursor = Cursors.SizeAll;
        }
        catch (Exception ex) { Log.Write("Zoom: " + ex.Message); }
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the point under <paramref name="at"/> fixed, like a photo viewer.</summary>
    void ZoomAt(float factor, Point at)
    {
        if (!VideoSize(out var w, out var h)) { Status("Zoom works on videos"); return; }
        ClampCenter();
        var r = DisplayRect(w, h);
        float fx = Math.Clamp((at.X - r.X) / r.Width, 0, 1), fy = Math.Clamp((at.Y - r.Y) / r.Height, 0, 1);
        float px = zcx - 0.5f / zoom + fx / zoom, py = zcy - 0.5f / zoom + fy / zoom;   // frame point under the cursor
        zoom = Math.Clamp(zoom * factor, 1f, 10f);
        zcx = px - fx / zoom + 0.5f / zoom;
        zcy = py - fy / zoom + 0.5f / zoom;
        ApplyCrop();
        Status(zoom > 1.001f ? $"Zoom {zoom * 100:0}%  ·  drag to move  ·  Z to reset" : "Zoom reset");
    }

    void ZoomCenter(float factor) => ZoomAt(factor, new Point(video.Width / 2, video.Height / 2));

    void ResetZoom()
    {
        if (zoom <= 1.001f) return;
        zoom = 1f; ApplyCrop(); Status("Zoom reset");
    }

    /// <summary>Catches the mouse wheel over the video wherever Windows delivers it (the video surface is a native window).</summary>
    public bool PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL || cur == null || !video.Visible || !ContainsFocus && Form.ActiveForm != this) return false;
        var screenPt = Cursor.Position;
        if (!video.RectangleToScreen(video.ClientRectangle).Contains(screenPt)) return false;
        if (!VideoSize(out _, out _)) return false;
        int delta = (short)((long)m.WParam >> 16);
        if (delta == 0) return false;
        ZoomAt((float)Math.Pow(1.2, delta / 120.0), video.PointToClient(screenPt));
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Application.RemoveMessageFilter(this);
        try { video.MediaPlayer = null; mp.Dispose(); vlc.Dispose(); } catch { }
    }

    static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
