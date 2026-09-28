using System.Text.Json;
using System.Text.Json.Serialization;
using pViewer.Core;

namespace pViewer.Services;

public enum ViewMode
{
    /// <summary>Shrinks images larger than the window, small ones stay at 100%.</summary>
    ShrinkToFit,
    /// <summary>Fits to the window, enlarging small images too.</summary>
    Fit,
    /// <summary>Fills the window (may crop the edges).</summary>
    Fill,
    FitWidth,
    FitHeight,
    /// <summary>One image pixel = one screen pixel.</summary>
    ActualSize,
}

public enum AppTheme { Dark, Light, System }

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public ViewMode ViewMode { get; set; } = ViewMode.ShrinkToFit;
    public bool AutoRotateExif { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public bool DeleteToRecycleBin { get; set; } = true;
    public bool ConfirmOverwrite { get; set; } = true;
    public bool ConfirmDiscardEdits { get; set; } = true;
    public int JpegQuality { get; set; } = 90;
    public PageLayout ArchiveLayout { get; set; } = PageLayout.Single;
    public bool ShowToolbar { get; set; } = true;
    public bool ShowStatusBar { get; set; } = true;
    public bool PixelatedZoom { get; set; } = true;
    public string CropColor { get; set; } = "#FFFF3B30";
    public string RectangleColor { get; set; } = "#FFFF3B30";
    public int RectangleThickness { get; set; } = 5;
    public int BorderThickness { get; set; } = 10;
    public string TextFontFamily { get; set; } = "Segoe UI";
    public double TextFontSize { get; set; } = 48;
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }
    public string TextColor { get; set; } = "#FFFF3B30";
    public double SlideshowSeconds { get; set; } = 5;
    public string? LastFolder { get; set; }
    public WindowPlacement? Window { get; set; }

    /// <summary>Keyboard shortcuts changed by the user: command id → keys ("Ctrl+S"). Others use the defaults.</summary>
    public Dictionary<string, List<string>>? Hotkeys { get; set; }
}

/// <summary>
/// Settings live in settings.json next to the executable (portable mode, like the
/// old settings.ini). If that folder is not writable, %AppData%\pViewer is used.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string? _path;

    public static string FilePath => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (IsWritable(AppContext.BaseDirectory)) return portable;
        // Program folder not writable (all-users install in Program Files): settings go to the
        // user's profile. A settings.json left there (e.g. by a run as administrator) is only
        // used as the starting point, since it could never be saved again.
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pViewer");
        Directory.CreateDirectory(appData);
        string path = Path.Combine(appData, "settings.json");
        if (!File.Exists(path) && File.Exists(portable))
        {
            try { File.Copy(portable, path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".pviewer_probe_{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static AppSettings Load(string? path = null)
    {
        var settings = Read(path ?? FilePath);
        // Outside the read: a hand-edited file must not break the keys, and a problem here must not
        // send the whole file to .bad.
        settings.Hotkeys = Hotkeys.Normalize(settings.Hotkeys);
        return settings;
    }

    private static AppSettings Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Unreadable file: start over from the defaults, but keep a copy instead of silently
            // overwriting it on exit.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= FilePath;
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Read-only media: settings are simply not saved.
        }
    }
}
