using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>Per-user program preferences, stored as JSON in the user's application data folder.</summary>
public sealed class ProgramSettings
{
    public List<string> RecentFiles { get; set; } = new();
    /// <summary>The frame and the stamps of the reports: they go with the user, from blast to blast.</summary>
    public ReportDesign ReportDesign { get; set; } = new();
    /// <summary>Width of the panel of the Report tab of the preview window.</summary>
    public double ReportPanelWidth { get; set; } = 380;
    /// <summary>Width of the panel of the Report design tab of the preview window.</summary>
    public double ReportDesignPanelWidth { get; set; } = 600;
    /// <summary>When each of the <see cref="RecentFiles"/> was last opened or saved under its name; those from before this was kept have no time.</summary>
    public Dictionary<string, DateTime> RecentOpened { get; set; } = new();
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
    /// <summary>Font of the ribbon buttons, points; their captions keep their proportions to it.</summary>
    public double RibbonFontSize { get; set; } = DefaultRibbonFont;
    /// <summary>Room above and below the text of a ribbon button, pixels (a large button has 2 more).</summary>
    public double RibbonButtonPadding { get; set; } = DefaultRibbonPadding;
    /// <summary>Room left and right between the ribbon buttons, pixels: 1 more is drawn, so the default of 1 keeps the 2 of before.</summary>
    public double RibbonButtonGap { get; set; } = DefaultRibbonGap;
    /// <summary>Font of the names of every tab: the stages of the ribbon, the right panel, the windows; points.</summary>
    public double TabFontSize { get; set; } = DefaultTabFont;
    /// <summary>Font of the right panel (Properties, Layers, Visibility), points; the whole panel scales with it.</summary>
    public double PanelFontSize { get; set; } = DefaultPanelFont;
    /// <summary>Font of the tooltips, points.</summary>
    public double ToolTipFontSize { get; set; } = DefaultToolTipFont;
    /// <summary>Ground of the tooltips; 0 follows the theme.</summary>
    public uint ToolTipBackground { get; set; }
    /// <summary>Text of the tooltips; 0 follows the theme, or stands out from a ground of the user's.</summary>
    public uint ToolTipForeground { get; set; }
    public const double DefaultRibbonFont = 12, DefaultRibbonPadding = 4, DefaultRibbonGap = 1, DefaultTabFont = 13, DefaultPanelFont = 12, DefaultToolTipFont = 12;
    public const double MinFont = 9, MaxFont = 18, MaxTabFont = 20, MaxToolTipFont = 20, MaxRibbonSpacing = 12;

    /// <summary>The sizes inside their ranges: a settings file edited by hand cannot make the interface unusable.</summary>
    public void ClampFonts()
    {
        static double C(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        RibbonFontSize = C(RibbonFontSize, MinFont, MaxFont, DefaultRibbonFont);
        RibbonButtonPadding = C(RibbonButtonPadding, 0, MaxRibbonSpacing, DefaultRibbonPadding);
        RibbonButtonGap = C(RibbonButtonGap, 0, MaxRibbonSpacing, DefaultRibbonGap);
        TabFontSize = C(TabFontSize, 10, MaxTabFont, DefaultTabFont);
        PanelFontSize = C(PanelFontSize, MinFont, MaxFont, DefaultPanelFont);
        ToolTipFontSize = C(ToolTipFontSize, MinFont, MaxToolTipFont, DefaultToolTipFont);
    }
    /// <summary>Plan background of each theme; switching themes moves a background still at one default to the other.</summary>
    public const uint DarkBackground = 0xFF101215, LightBackground = 0xFFFFFFFF;
    /// <summary>Colour of lines, points, polygons and other drawing objects without their own colour; null keeps the layer colours.</summary>
    public uint? GeometryColor { get; set; }
    /// <summary>Colour of section lines without their own colour on the screen; at first that of the first-movement arrows on the plan.</summary>
    public uint SectionLineColor { get; set; } = DefaultSectionLine;
    public const uint DefaultSectionLine = 0xFFEA580C;

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
    /// <summary>The Section view window shows the numbers of the holes over them.</summary>
    public bool SectionNumbers { get; set; } = true;
    /// <summary>The Section view window shows the levels on the left: the top of the frame, collars, grade, toes.</summary>
    public bool SectionLevels { get; set; } = true;
    /// <summary>The Section view window shows a dimension of each: the length, the spacing along the line, the subdrill.</summary>
    public bool SectionDimensions { get; set; } = true;
    /// <summary>The Section view window shows the marks drawn by hand: dimensions, angles, levels, lines, texts.</summary>
    public bool SectionMarks { get; set; } = true;
    /// <summary>The styles offered for the marks of a section, by name.</summary>
    public List<SectionMarkPreset> SectionMarkPresets { get; set; } = SectionMarkPreset.Defaults();
    /// <summary>The style a new mark of a kind takes (Make default), by the name of the kind; a kind not here takes its built-in one.</summary>
    public Dictionary<string, SectionMarkStyle> SectionMarkDefaults { get; set; } = new();
    /// <summary>The snaps of the Section view window turned off, by name (Collar, Toe, Grade, Surface, End, Intersection, Perpendicular, Frame).</summary>
    public List<string> SectionSnapsOff { get; set; } = new();
    /// <summary>In the Section view window a point is taken straight across or up from the one before.</summary>
    public bool SectionOrtho { get; set; }

    /// <summary>The style a new mark of a kind takes: the one made default, else the built-in one.</summary>
    public SectionMarkStyle SectionMarkStyleFor(SectionMarkKind kind) =>
        (SectionMarkDefaults.TryGetValue(kind.ToString(), out var s) ? s : SectionMarkPreset.BuiltIn(kind)).Copy();
    /// <summary>Transparency of the surface fill, per cent: 0 opaque, 100 not seen.</summary>
    public int SurfaceTransparency { get; set; } = DefaultSurfaceTransparency;
    /// <summary>Transparency of the surface wireframe (the triangle edges), per cent.</summary>
    public int WireframeTransparency { get; set; } = DefaultWireframeTransparency;
    /// <summary>Transparency of the points of a point cloud, per cent.</summary>
    public int PointCloudTransparency { get; set; } = DefaultPointCloudTransparency;
    public const int DefaultSurfaceTransparency = 10, DefaultWireframeTransparency = 57, DefaultPointCloudTransparency = 22;
    /// <summary>The alpha of a colour drawn with a transparency of <paramref name="percent"/>.</summary>
    public static byte Alpha(int percent) => (byte)Math.Round(255 * (100 - Math.Clamp(percent, 0, 100)) / 100.0);
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
        ["Bench"] = 0xFF4C3A17,
        ["Grade"] = 0xFF76402D,
        ["Face"] = 0xFF33291F,
        ["Topography"] = 0xFF434D2A,
        ["Design"] = 0xFF25529C,
        ["Strata"] = 0xFF4A3A79,
        ["PointCloud"] = 0xFFB4BAC2
    };

    /// <summary>
    /// Colours the surface palette had before 0.3.5 and the ones that took their place (RGB, the alpha kept): the settings
    /// and the projects that still have them are given the new ones when they are loaded (<see cref="NewSurfaceColor"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, uint> RetiredSurfaceColors = new Dictionary<uint, uint>
    {
        [0xD9A441] = 0x4C3A17,
        [0xC46A4A] = 0x76402D,
        [0x7D644C] = 0x33291F,
        [0x8DA35A] = 0x434D2A,
        [0x3B82F6] = 0x25529C,
        [0x9B7BFF] = 0x4A3A79
    };

    /// <summary>A colour of the old surface palette as its new one, its alpha kept; any other as it is.</summary>
    public static uint NewSurfaceColor(uint argb) =>
        RetiredSurfaceColors.TryGetValue(argb & 0xFFFFFF, out var rgb) ? (argb & 0xFF000000) | rgb : argb;

    public uint SurfaceColor(string kind) =>
        SurfaceColors.TryGetValue(kind, out var c) ? c : DefaultSurfaceColors.TryGetValue(kind, out var d) ? d : 0xFF8A9BB0;
    /// <summary>Shortest top stemming accepted by the loading check, in hole diameters.</summary>
    public double MinStemmingDiameters { get; set; } = 25;
    /// <summary>Unit of the block sizes of fragmentation for projects and presets that have none of their own.</summary>
    public SizeUnit FragmentSizeUnit { get; set; } = SizeUnit.Mm;
    /// <summary>The units values are shown and typed in (<see cref="Measure.System"/>); the program keeps SI.</summary>
    public UnitSystem UnitSystem { get; set; } = UnitSystem.Metric;
    /// <summary>What the angle of a hole is counted from on the screen (<see cref="Measure.Angle"/>); it is kept from vertical.</summary>
    public AngleReference HoleAngleReference { get; set; } = AngleReference.FromVertical;

    /// <summary>Gives the units of these settings to <see cref="Measure"/>, which the whole program shows values by.</summary>
    public void ApplyUnits()
    {
        Measure.System = UnitSystem;
        Measure.Angle = HoleAngleReference;
    }
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
    /// <summary>Tie tool panel ▸ Capture width: grey lines beside the tie being drawn show what it captures.</summary>
    public bool TieCaptureShown { get; set; } = true;
    /// <summary>Visualization window: seconds the whole blast is shown in; 0 runs on the blast's own clock.</summary>
    public double PlaybackSeconds { get; set; }
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
    /// <summary>Selected objects are drawn 2 px thicker than their line (the collar of a hole 1 px).</summary>
    public bool SelectionThick { get; set; } = true;
    /// <summary>The contrasting halo round selected objects, px: 0 (off), 1 or 2.</summary>
    public int SelectionHaloPx { get; set; } = 2;
    /// <summary>Fill of the circle that turns a pattern (Select tool) when the grid can be turned.</summary>
    public uint PatternTurnColor { get; set; } = DefaultPatternTurn;
    /// <summary>Fill of the circle of a pattern that cannot be turned (its holes drilled, locked and so on).</summary>
    public uint PatternTurnRefusedColor { get; set; } = DefaultPatternTurnRefused;
    /// <summary>The burden arrow of a selected pattern, from its centre to the circle.</summary>
    public uint PatternArrowColor { get; set; } = DefaultPatternArrow;
    public const uint DefaultPatternArrow = 0xFFFACC15;
    /// <summary>Lines and fill (90 % transparent) of the collar polygon of a pattern or segment.</summary>
    public uint PatternCollarPolygonColor { get; set; } = DefaultPatternCollarPolygon;
    /// <summary>Lines and fill (90 % transparent) of the depth polygon of a pattern or segment.</summary>
    public uint PatternDepthPolygonColor { get; set; } = DefaultPatternDepthPolygon;
    public const uint DefaultPatternCollarPolygon = 0xFFA3E635;
    public const uint DefaultPatternDepthPolygon = 0xFFA855F7;
    public const uint DefaultPatternTurn = 0xFF16A34A;
    public const uint DefaultPatternTurnRefused = 0xFFDC2626;
    /// <summary>Version of the defaults the file was written with; older files are upgraded on load.</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentVersion = 5;

    /// <summary>
    /// Version 2 moved to the dark interface: a plan background or selection colour still at the old light defaults
    /// (white, magenta) is switched to the dark defaults. Colours the user picked themselves are kept.
    /// Version 3 made the theme work: files wrote "Light" before it meant anything, and the interface was dark.
    /// Versions 4 and 5 changed six colours of the surface palette: the colours of surface types still at the old ones take the new.
    /// </summary>
    public void Upgrade()
    {
        // the style of a slope face kept as Slope read as the slope tool (Уклон): it takes its own name
        foreach (var p in SectionMarkPresets.Where(p => p.Name == "Slope")) p.Name = SectionMarkPreset.SlopeFace;
        if (SettingsVersion < 2)
        {
            if (BackgroundColor == LightBackground) BackgroundColor = DarkBackground;
            if (SelectionColor == 0xFFFF00FF) SelectionColor = 0xFF4C9BFF;
        }
        if (SettingsVersion < 3) Theme = DarkTheme;
        if (Theme != LightTheme) Theme = DarkTheme;
        if (SettingsVersion < 5)
            foreach (var kind in SurfaceColors.Keys.ToList()) SurfaceColors[kind] = NewSurfaceColor(SurfaceColors[kind]);
        ClampFonts();
        SettingsVersion = CurrentVersion;
    }

    [System.Text.Json.Serialization.JsonIgnore] public bool IsLight => Theme == LightTheme;
    public double CollarSizePixels { get; set; } = 6;
    /// <summary>Font size of the hole labels last chosen: a new blast starts with it, the project keeps its own.</summary>
    public int HoleLabelFontSize { get; set; } = BlastDesigner.Domain.Model.ViewState.DefaultHoleLabelFontSize;
    /// <summary>Colours of the hole labels last chosen: a new blast starts with them, the project keeps its own.</summary>
    public Dictionary<BlastDesigner.Domain.Model.HoleLabel, uint> HoleLabelColors { get; set; } = new();
    public bool ShowCoordinatesBox { get; set; } = true;
    public Dictionary<string, string> Window { get; set; } = new();

    /// <summary>
    /// An environment variable that puts the folder of the settings, catalogue, templates and logs elsewhere: the smoke
    /// test runs in a folder of its own and never touches the user's settings (a run broken off left its settings there).
    /// </summary>
    public const string DataFolderVariable = "BLASTDESIGNER_DATA";

    public static string AppDataFolder
    {
        get
        {
            if (Environment.GetEnvironmentVariable(DataFolderVariable) is { Length: > 0 } own) return own;
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
        SafeFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, ProjectJson.Indented));
    }

    /// <summary>A date and time as the system writes them: the regional settings on Windows and macOS, the locale on Linux.</summary>
    public static string FormatDateTime(DateTime time) => time.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// Size and time of change of a file, null when it is gone or out of reach. A path on a lost network drive can take many
    /// seconds to answer, so this is for a background thread.
    /// </summary>
    public static (long Size, DateTime Changed)? ReadFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTime) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        RecentOpened[path] = DateTime.Now;
        foreach (var gone in RecentOpened.Keys.Where(k => !RecentFiles.Contains(k)).ToList()) RecentOpened.Remove(gone);
        LastFolder = Path.GetDirectoryName(path) ?? LastFolder;
    }
}

/// <summary>Writes a file so that a failure on the way leaves the old one whole.</summary>
public static class SafeFile
{
    /// <summary>The text goes to a temporary file next to the target, reaches the disk, and only then replaces it.</summary>
    public static void WriteAllText(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(System.Text.Encoding.UTF8.GetBytes(text));
                fs.Flush(true);
            }
            File.Move(tmp, path, true);
        }
        finally
        {
            File.Delete(tmp);
        }
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
