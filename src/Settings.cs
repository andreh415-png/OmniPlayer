using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmniPlayer;

/// <summary>Everything the player remembers between runs. Stored in %AppData%\OmniPlayer\settings.json.</summary>
sealed class Settings
{
    public int Volume = 80;              // 0–150 (%)
    public bool Muted;
    public float Rate = 1f;
    public int Repeat;                   // 0 = off, 1 = all, 2 = one
    public bool Shuffle;

    public bool EqEnabled;
    public int EqPreset = -1;            // -1 = custom
    public float EqPreamp;
    public float[] EqBands = new float[10];

    public bool Resume = true;           // remember position in long files
    public Dictionary<string, long> Positions = new(StringComparer.OrdinalIgnoreCase);

    public bool RestorePlaylist = true;
    public List<string> Playlist = new();
    public int PlaylistIndex = -1;

    public int X = int.MinValue, Y = int.MinValue, W = 1240, H = 760;
    public bool Maximized;
    public bool ShowPlaylist = true;
    public int PlaylistWidth = 340;
    public bool AlwaysOnTop;

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, IncludeFields = true };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniPlayer");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Opts);
                if (s != null)
                {
                    if (s.EqBands == null || s.EqBands.Length != 10) s.EqBands = new float[10];
                    s.Positions = new Dictionary<string, long>(s.Positions ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.Playlist ??= new();
                    return s;
                }
            }
        }
        catch { /* corrupt file → defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* best effort */ }
    }
}
