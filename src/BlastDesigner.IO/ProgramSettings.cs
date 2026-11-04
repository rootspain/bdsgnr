using System.Text.Json;
using BlastDesigner.Domain;

namespace BlastDesigner.IO;

/// <summary>Per-user program preferences, stored as JSON in the user's application data folder.</summary>
public sealed class ProgramSettings
{
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>Files pinned at the top of the startup assistant; they stay there when they drop off <see cref="RecentFiles"/>.</summary>
    public List<string> PinnedFiles { get; set; } = new();
    public string LastFolder { get; set; } = string.Empty;
    public string LastImportFolder { get; set; } = string.Empty;
    public int AutosaveMinutes { get; set; } = 5;
    public int BackupCopies { get; set; } = 3;
    public int MaxRecentFiles { get; set; } = 10;
    public string Language { get; set; } = "ru";
    /// <summary>Interface theme: <see cref="DarkTheme"/> or <see cref="LightTheme"/>.</summary>
    public string Theme { get; set; } = DarkTheme;
    public const string DarkTheme = "Dark";
    public const string LightTheme = "Light";
    /// <summary>Accent of the interface (active tab, chips, primary buttons, group captions).</summary>
    public uint AccentColor { get; set; } = DefaultAccent;
    public const uint DefaultAccent = 0xFFF0A63A;
    /// <summary>Plan background of each theme; switching themes moves a background still at one default to the other.</summary>
    public const uint DarkBackground = 0xFF101215, LightBackground = 0xFFFFFFFF;
    /// <summary>Colour of lines, points, polygons and other drawing objects without their own colour; null keeps the layer colours.</summary>
    public uint? GeometryColor { get; set; }

    /// <summary>How surfaces are filled: <see cref="SurfaceByType"/> (palette below) or <see cref="SurfaceByElevation"/> (height ramp).</summary>
    public string SurfaceColoring { get; set; } = SurfaceByType;
    public const string SurfaceByType = "Type", SurfaceByElevation = "Elevation";
    [System.Text.Json.Serialization.JsonIgnore] public bool SurfacesByType => SurfaceColoring != SurfaceByElevation;
    /// <summary>
    /// When surface wireframes are drawn: <see cref="WireframeAlways"/>, <see cref="WireframeWhileStill"/> (left out while
    /// the camera moves, the costliest part of a frame), <see cref="WireframeSelected"/> (on selected surfaces only)
    /// or <see cref="WireframeNever"/>.
    /// </summary>
    public string SurfaceWireframe { get; set; } = WireframeWhileStill;
    public const string WireframeAlways = "Always", WireframeWhileStill = "WhileStill", WireframeSelected = "Selected", WireframeNever = "Never";
    /// <summary>Fill colour per surface kind (SurfaceKind names); kinds missing here use <see cref="DefaultSurfaceColors"/>.</summary>
    public Dictionary<string, uint> SurfaceColors { get; set; } = new();

    /// <summary>
    /// Earth tones for what exists (bench, grade, topography, dark brown faces), blue for design, violet for strata, grey for raw data —
    /// apart in hue and in lightness so the common colour-vision deficiencies still tell them apart.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint> DefaultSurfaceColors = new Dictionary<string, uint>
    {
        ["General"] = 0xFF8A9BB0,
        ["Bench"] = 0xFFD9A441,
        ["Grade"] = 0xFFC46A4A,
        ["Face"] = 0xFF7D644C,
        ["Topography"] = 0xFF8DA35A,
        ["Design"] = 0xFF3B82F6,
        ["Strata"] = 0xFF9B7BFF,
        ["PointCloud"] = 0xFFB4BAC2
    };

    public uint SurfaceColor(string kind) =>
        SurfaceColors.TryGetValue(kind, out var c) ? c : DefaultSurfaceColors.TryGetValue(kind, out var d) ? d : 0xFF8A9BB0;
    /// <summary>Shortest top stemming accepted by the loading check, in hole diameters.</summary>
    public double MinStemmingDiameters { get; set; } = 25;
    public bool SnapToObjects { get; set; } = true;
    public double TargetRadiusPixels { get; set; } = 8;
    public bool ShowStartupAssistant { get; set; } = true;
    /// <summary>Last version whose release notes were shown (or skipped on a new installation).</summary>
    public string? LastSeenVersion { get; set; }
    /// <summary>Record clicks, menus, hot keys and view flags for Settings ▸ Usage statistics (kept on this computer).</summary>
    public bool UsageLogging { get; set; } = true;
    /// <summary>Recognize bench elements with the trained model (experimental); off: by rules. The window switches it.</summary>
    public bool UseSurveyModel { get; set; } = true;
    /// <summary>Frames per second in the corner of the plan (Program settings ▸ Display).</summary>
    public bool ShowFps { get; set; }
    /// <summary>Visualization window: playback speed (0.1, 0.5 or 1 — times the blast's own clock).</summary>
    public double PlaybackSpeed { get; set; } = 1;
    /// <summary>Visualization window: isochrone interval, ms.</summary>
    public double IsochroneIntervalMs { get; set; } = 25;
    /// <summary>Visualization window: draw the isochrones.</summary>
    public bool ShowIsochrones { get; set; } = true;
    /// <summary>Visualization window: colour the holes by their firing time.</summary>
    public bool ColourHolesByTime { get; set; } = true;
    /// <summary>No settings file existed when these settings were loaded: a new installation.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsNewInstallation { get; private set; }
    public string Author { get; set; } = Environment.UserName;
    public string LastTemplate { get; set; } = string.Empty;
    public uint BackgroundColor { get; set; } = DarkBackground;
    public uint SelectionColor { get; set; } = 0xFF4C9BFF;
    /// <summary>Version of the defaults the file was written with; older files are upgraded on load.</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentVersion = 3;

    /// <summary>
    /// Version 2 moved to the dark interface: a plan background or selection colour still at the old light defaults
    /// (white, magenta) is switched to the dark defaults. Colours the user picked themselves are kept.
    /// Version 3 made the theme work: files wrote "Light" before it meant anything, and the interface was dark.
    /// </summary>
    public void Upgrade()
    {
        if (SettingsVersion < 2)
        {
            if (BackgroundColor == LightBackground) BackgroundColor = DarkBackground;
            if (SelectionColor == 0xFFFF00FF) SelectionColor = 0xFF4C9BFF;
        }
        if (SettingsVersion < 3) Theme = DarkTheme;
        if (Theme != LightTheme) Theme = DarkTheme;
        SettingsVersion = CurrentVersion;
    }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsLight => Theme == LightTheme;
    public double CollarSizePixels { get; set; } = 6;
    public bool ShowCoordinatesBox { get; set; } = true;
    public Dictionary<string, string> Window { get; set; } = new();

    public static string AppDataFolder
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "BlastDesigner");
        }
    }

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");
    public static string TemplatesFolder => Path.Combine(AppDataFolder, "Templates");
    public static string ImportFormatsFolder => Path.Combine(AppDataFolder, "ImportFormats");
    public static string ReportTemplatesFolder => Path.Combine(AppDataFolder, "Reports");
    public static string LogFolder => Path.Combine(AppDataFolder, "Logs");
    public static string RecoveryFolder => Path.Combine(Path.GetTempPath(), "BlastDesigner", "Recovery");

    public static ProgramSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<ProgramSettings>(File.ReadAllText(SettingsPath), ProjectJson.Options) ?? new();
                s.Upgrade();
                return s;
            }
        }
        catch
        {
            // A corrupt settings file falls back to defaults.
        }
        var fresh = new ProgramSettings { IsNewInstallation = !File.Exists(SettingsPath) };
        fresh.Upgrade();
        return fresh;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, ProjectJson.Indented));
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        LastFolder = Path.GetDirectoryName(path) ?? LastFolder;
    }
}

/// <summary>Simple append-only log file for diagnostics (Help ▸ Log file).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(ProgramSettings.LogFolder, $"blastdesigner-{DateTime.Now:yyyyMMdd}.log");
    public static bool Extended { get; set; }

    public static void Info(string message) => Write("INFO", message);
    public static void Debug(string message) { if (Extended) Write("DEBUG", message); }
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ProgramSettings.LogFolder);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
