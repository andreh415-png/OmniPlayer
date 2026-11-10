using System;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace OmniPlayer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = new DarkRenderer();

        try
        {
            Core.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not load the video engine (libVLC).\n\nMake sure the 'libvlc' folder is next to OmniPlayer.exe.\n\n" + ex.Message,
                "OmniPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm(args));
    }
}
