using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace OmniPlayer;

/// <summary>
/// Registers OmniPlayer with Windows (per-user, no admin) so it appears in "Open with" and in
/// Settings → Apps → Default apps, where the user can make it the default player.
/// Windows does not allow apps to silently set themselves as default — the user confirms in Settings.
/// </summary>
static class FileAssoc
{
    const string ProgId = "OmniPlayer.Media";
    const string CapKey = @"Software\OmniPlayer\Capabilities";

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OmniPlayer.exe");
    static string Command => $"\"{Exe}\" \"%1\"";

    /// <summary>True when the registration already points at this copy of the exe.</summary>
    public static bool IsCurrent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            using var ra = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            return k?.GetValue("") as string == Command && ra?.GetValue("OmniPlayer") as string == CapKey;
        }
        catch { return false; }
    }

    public static void Register(IEnumerable<string> extensions)
    {
        var exts = extensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).Distinct().ToList();
        string icon = $"\"{Exe}\",0";

        using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            k.SetValue("", "Media file (OmniPlayer)");
            using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", icon);
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", Command);
            using (var p = k.CreateSubKey(@"shell\play")) p.SetValue("", "Play with OmniPlayer");
            using (var pc = k.CreateSubKey(@"shell\play\command")) pc.SetValue("", Command);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\OmniPlayer.exe"))
        {
            k.SetValue("FriendlyAppName", "OmniPlayer");
            using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", Command);
            using (var st = k.CreateSubKey("SupportedTypes")) foreach (var e in exts) st.SetValue(e, "");
        }

        using (var cap = Registry.CurrentUser.CreateSubKey(CapKey))
        {
            cap.SetValue("ApplicationName", "OmniPlayer");
            cap.SetValue("ApplicationDescription", "Plays nearly every video and audio format — HEVC/H.265, AV1, MKV, AVI, WMV, FLAC, DTS and more.");
            cap.SetValue("ApplicationIcon", icon);
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var e in exts) fa.SetValue(e, ProgId);
        }

        using (var ra = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            ra.SetValue("OmniPlayer", CapKey);

        foreach (var e in exts)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{e}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
        Log.Write($"Registered with Windows for {exts.Count} file types");
    }

    public static void Unregister()
    {
        try
        {
            using (var cap = Registry.CurrentUser.OpenSubKey(CapKey + @"\FileAssociations"))
                if (cap != null)
                    foreach (var e in cap.GetValueNames())
                        using (var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{e}\OpenWithProgids", true))
                            k?.DeleteValue(ProgId, false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\OmniPlayer.exe", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\OmniPlayer", false);
            using (var ra = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) ra?.DeleteValue("OmniPlayer", false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            Log.Write("Removed Windows registration");
        }
        catch (Exception ex) { Log.Write("Unregister: " + ex.Message); }
    }

    /// <summary>Opens Settings → Default apps directly on OmniPlayer's page (falls back to the main page).</summary>
    public static void OpenDefaultAppsSettings()
    {
        foreach (var uri in new[] { "ms-settings:defaultapps?registeredAppUser=OmniPlayer", "ms-settings:defaultapps" })
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return; }
            catch { }
        }
    }
}

/// <summary>Keeps one OmniPlayer window: a second launch (double-clicking a file) hands its files to the running copy.</summary>
static class SingleInstance
{
    static readonly string Name = "OmniPlayer-" + Environment.UserName;
    static Mutex _mutex;

    public static bool IsFirst()
    {
        _mutex = new Mutex(true, Name + "-mutex", out bool created);
        return created;
    }

    public static bool SendToRunning(string[] args)
    {
        try
        {
            var full = args.Select(a => MainForm.IsUrl(a) ? a : Path.GetFullPath(a));
            using var c = new NamedPipeClientStream(".", Name + "-pipe", PipeDirection.Out);
            c.Connect(8000);
            using var w = new StreamWriter(c, new UTF8Encoding(false));
            w.Write(string.Join("\n", full));
            w.Flush();
            return true;
        }
        catch (Exception ex) { Log.Write("Hand-off to running copy failed: " + ex.Message); return false; }
    }

    public static void Listen(MainForm form)
    {
        var t = new Thread(() =>
        {
            while (!form.IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Name + "-pipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var r = new StreamReader(server, Encoding.UTF8);
                    var args = r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (form.IsDisposed) return;
                    form.BeginInvoke(new Action(() => form.ReceiveArgs(args)));
                }
                catch (Exception ex) { Log.Write("Pipe: " + ex.Message); Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "OmniPlayer pipe" };
        t.Start();
    }

    public static void KeepAlive() => GC.KeepAlive(_mutex);
}
