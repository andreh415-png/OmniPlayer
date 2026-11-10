using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace OmniPlayer;

/// <summary>Startup/crash log at %AppData%\OmniPlayer\log.txt (kept small).</summary>
static class Log
{
    public static string FilePath => Path.Combine(Settings.Folder, "log.txt");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000) File.Delete(FilePath);
        }
        catch { }
        Write("──────── OmniPlayer starting ────────");
        Write($"Version {Application.ProductVersion} · {Environment.OSVersion} · 64-bit process: {Environment.Is64BitProcess}");
        Write("Folder: " + AppContext.BaseDirectory);
    }

    public static void Write(string msg)
    {
        try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); } catch { }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Log.Start();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Fatal("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal("background thread", e.ExceptionObject as Exception);

        if (!SingleInstance.IsFirst())
        {
            Log.Write("Already running — handing files to the open window");
            if (SingleInstance.SendToRunning(args)) return;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ToolStripManager.Renderer = new DarkRenderer();

            string libvlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc");
            Log.Write("libvlc folder present: " + Directory.Exists(libvlcDir));
            if (!Directory.Exists(libvlcDir))
            {
                MessageBox.Show("The 'libvlc' folder is missing.\n\nUnzip the WHOLE download into a normal folder (don't run OmniPlayer.exe from inside the zip), and keep the 'libvlc' folder next to OmniPlayer.exe.",
                    "OmniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log.Write("Loading libVLC…");
            Core.Initialize();
            Log.Write("libVLC loaded");

            Log.Write("Creating window…");
            var form = new MainForm(args);
            form.Shown += (s, e) =>
            {
                Log.Write("Window shown — startup OK");
                SingleInstance.Listen(form);
                try { if (!FileAssoc.IsCurrent()) FileAssoc.Register(MainForm.AssociableExtensions); }
                catch (Exception ex) { Log.Write("File registration: " + ex.Message); }
            };
            Application.Run(form);
            Log.Write("Exited normally");
            SingleInstance.KeepAlive();
        }
        catch (Exception ex)
        {
            Fatal("startup", ex);
        }
    }

    static int _reported;
    static void Fatal(string where, Exception ex)
    {
        Log.Write($"ERROR ({where}): {ex}");
        if (Interlocked.Exchange(ref _reported, 1) == 1) return;
        try
        {
            MessageBox.Show($"OmniPlayer hit an error ({where}):\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails were saved to:\n{Log.FilePath}",
                "OmniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
