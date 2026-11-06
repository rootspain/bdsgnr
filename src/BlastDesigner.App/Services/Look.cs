using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace BlastDesigner.App.Services;

/// <summary>
/// Colours, fonts and stroke icons of the work-stage interface. The brushes are shared instances: <see cref="ApplyTheme"/>
/// recolours them in place, so everything drawn with them follows a theme or accent change without being rebuilt.
/// </summary>
public static class Look
{
    public static readonly SolidColorBrush Ground = Brush(0xFF15181C);
    public static readonly SolidColorBrush Panel = Brush(0xFF1B1E23);
    public static readonly SolidColorBrush Ribbon = Brush(0xFF20242A);
    public static readonly SolidColorBrush Field = Brush(0xFF101215);
    public static readonly SolidColorBrush Line = Brush(0xFF2C3138);
    public static readonly SolidColorBrush LineStrong = Brush(0xFF3A414A);
    public static readonly SolidColorBrush Hover = Brush(0xFF2A2F36);
    public static readonly SolidColorBrush Text = Brush(0xFFE6E9ED);
    public static readonly SolidColorBrush TextSoft = Brush(0xFFC9CFD6);
    public static readonly SolidColorBrush Muted = Brush(0xFFA3ABB6);
    public static readonly SolidColorBrush Faint = Brush(0xFF8A929D);
    public static readonly SolidColorBrush Accent = Brush(0xFFF0A63A);
    public static readonly SolidColorBrush AccentSoft = Brush(0x29F0A63A);
    public static readonly SolidColorBrush AccentHover = Brush(0xFFFFB956);
    public static readonly SolidColorBrush AccentPressed = Brush(0xFFE0962A);
    public static readonly SolidColorBrush Blue = Brush(0xFF4C9BFF);
    public static readonly SolidColorBrush OnAccent = Brush(0xFF15181C);
    /// <summary>Menus lie over the ribbon and the panels: a ground and an outline of their own keep them apart from what is under them.</summary>
    public static readonly SolidColorBrush Popup = Brush(0xFF2A2F36);
    public static readonly SolidColorBrush PopupLine = Brush(0xFF5A636E);

    public static readonly FontFamily Mono = new("JetBrains Mono, Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private static SolidColorBrush Brush(uint argb) => new(Color.FromUInt32(argb));

    public static bool IsLight { get; private set; }

    /// <summary>Raised after <see cref="ApplyTheme"/>: what draws the brushes itself (the plan) redraws.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Ground, Panel, Ribbon, Field, Line, LineStrong, Hover, Text, TextSoft, Muted, Faint, Blue.</summary>
    private static readonly uint[] DarkColors = { 0xFF15181C, 0xFF1B1E23, 0xFF20242A, 0xFF101215, 0xFF2C3138, 0xFF3A414A, 0xFF2A2F36, 0xFFE6E9ED, 0xFFC9CFD6, 0xFFA3ABB6, 0xFF8A929D, 0xFF4C9BFF };
    private static readonly uint[] LightColors = { 0xFFEEF0F3, 0xFFFFFFFF, 0xFFF6F7F9, 0xFFFFFFFF, 0xFFDDE1E6, 0xFFC4CBD3, 0xFFE8EBEF, 0xFF1B1F24, 0xFF353B43, 0xFF5B6470, 0xFF78818C, 0xFF2563EB };

    /// <summary>Recolours the shared brushes, the Fluent palette of the standard controls and the theme variant.</summary>
    public static void ApplyTheme(bool light, uint accent)
    {
        IsLight = light;
        var c = light ? LightColors : DarkColors;
        var brushes = new[] { Ground, Panel, Ribbon, Field, Line, LineStrong, Hover, Text, TextSoft, Muted, Faint, Blue };
        for (int i = 0; i < brushes.Length; i++) brushes[i].Color = Color.FromUInt32(c[i]);
        var a = Color.FromUInt32(accent | 0xFF000000);
        Accent.Color = a;
        AccentSoft.Color = Color.FromArgb(light ? (byte)0x22 : (byte)0x29, a.R, a.G, a.B);
        AccentHover.Color = Mix(a, Colors.White, 0.18);
        AccentPressed.Color = Mix(a, Colors.Black, 0.12);
        OnAccent.Color = Luminance(a) > 0.5 ? Color.FromUInt32(0xFF15181C) : Colors.White;
        Popup.Color = Color.FromUInt32(light ? 0xFFFFFFFF : 0xFF2A2F36);
        PopupLine.Color = Color.FromUInt32(light ? 0xFFB4BCC6 : 0xFF5A636E);

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            // what the menus and their submenus take from the Fluent theme
            app.Resources["MenuFlyoutPresenterBackground"] = Popup;
            app.Resources["MenuFlyoutPresenterBorderBrush"] = PopupLine;
            app.Resources["MenuFlyoutPresenterBorderThemeThickness"] = new Thickness(1);
            app.Resources["OverlayCornerRadius"] = new CornerRadius(8);
            app.Resources["MenuFlyoutItemBackgroundPointerOver"] = AccentSoft;
            app.Resources["MenuFlyoutItemBackgroundPressed"] = AccentSoft;
            foreach (var fluent in app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>())
                foreach (var palette in fluent.Palettes.Values)
                    palette.Accent = a;
        }
        ThemeChanged?.Invoke();
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(a.A, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>24×24 stroke icons (SVG path data).</summary>
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["new"] = "M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z M14 3v5h5 M12 12v6 M9 15h6",
        ["open"] = "M3 7a1 1 0 0 1 1-1h5l2 2h8a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z",
        ["save"] = "M5 4h11l3 3v13H5z M8 4v5h7V4 M8 20v-6h8v6",
        ["undo"] = "M9 14L4 9l5-5 M4 9h10a6 6 0 0 1 0 12h-3",
        ["redo"] = "M15 14l5-5-5-5 M20 9H10a6 6 0 0 0 0 12h3",
        ["print"] = "M7 9V3h10v6 M7 18H4v-8h16v8h-3 M7 14h10v7H7z",
        ["recent"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 2",
        ["close"] = "M6 6l12 12 M18 6L6 18",
        ["template"] = "M4 4h16v6H4z M4 14h7v6H4z M14 14h6v6h-6z",
        ["import"] = "M12 3v12 M8 11l4 4 4-4 M4 15v5h16v-5",
        ["export"] = "M12 15V3 M8 7l4-4 4 4 M4 15v5h16v-5",
        ["merge"] = "M6 3v6a6 6 0 0 0 6 6a6 6 0 0 1 6 6 M18 3v6a6 6 0 0 1-6 6",
        ["image"] = "M4 5h16v14H4z M4 16l5-5 4 4 3-3 4 4 M15 9h.01",
        ["report"] = "M6 3h9l4 4v14H6z M9 12h7 M9 16h7 M9 8h3",
        ["select"] = "M5 3l14 7-6 2-2 6z",
        ["pan"] = "M12 3v18 M3 12h18 M9 6l3-3 3 3 M9 18l3 3 3-3 M6 9l-3 3 3 3 M18 9l3 3-3 3",
        ["zoombox"] = "M4 9V4h5 M20 9V4h-5 M4 15v5h5 M20 15v5h-5 M12 9v6 M9 12h6",
        ["fit"] = "M4 9V4h5 M20 9V4h-5 M4 15v5h5 M20 15v5h-5 M9 9h6v6H9z",
        ["prev"] = "M3 12a9 9 0 1 0 3-6.7 M3 4v4h4",
        ["orbit"] = "M12 3l8 4.5v9L12 21l-8-4.5v-9z M12 12l8-4.5 M12 12v9 M12 12L4 7.5",
        ["north"] = "M12 3l5 18-5-4-5 4z",
        ["snap"] = "M6 4v8a6 6 0 0 0 12 0V4 M6 4h4v8a2 2 0 0 0 4 0V4h4 M6 8h4 M14 8h4",
        ["gridi"] = "M4 4h16v16H4z M4 12h16 M12 4v16",
        ["layers"] = "M12 3l9 5-9 5-9-5z M3 12l9 5 9-5 M3 16l9 5 9-5",
        ["measure"] = "M3 17L17 3l4 4L7 21z M7 13l2 2 M10 10l2 2 M13 7l2 2",
        ["polyline"] = "M4 18l5-9 5 5 6-9",
        ["polygon"] = "M5 8l7-5 7 5-3 11H8z",
        ["sectionline"] = "M5 19L19 5 M2 16l6 6 M16 2l6 6",
        ["text"] = "M5 6V4h14v2 M12 4v16 M9 20h6",
        ["circle"] = "M12 4a8 8 0 1 0 0.01 0 M12 11.2a0.8 0.8 0 1 0 0.01 0",
        ["dimension"] = "M4 7v10 M20 7v10 M4 12h16 M7 9.5L4 12l3 2.5 M17 9.5l3 2.5-3 2.5",
        ["northmark"] = "M12 2l4 8h-8z M12 10v4 M9 22v-6l6 6v-6",
        ["point"] = "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6 M12 3v3 M12 18v3 M3 12h3 M18 12h3",
        ["hole"] = "M12 6a6 6 0 1 0 0 12a6 6 0 1 0 0-12 M12 11a1 1 0 1 0 0 2a1 1 0 1 0 0-2",
        ["row"] = "M5 10a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M12 10a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M19 10a2 2 0 1 0 0 4a2 2 0 1 0 0-4",
        ["pattern"] = "M6 4a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M12 4a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M18 4a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M9 10a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M15 10a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M6 16a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M12 16a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M18 16a2 2 0 1 0 0 4a2 2 0 1 0 0-4",
        ["baseline"] = "M3 19h18 M7 11a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M17 11a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M7 5v3 M17 5v3",
        ["fill"] = "M4 7l8-4 8 4-2 13H6z M10 10h.01 M14 10h.01 M10 15h.01 M14 15h.01",
        ["curve"] = "M3 18c5-10 13-10 18 0 M7 12h.01 M12 9h.01 M17 12h.01",
        ["profile"] = "M3 6h6l6 12h6",
        ["batter"] = "M3 7h7l5 11h6 M10 7v4 M15 18v-4",
        ["optimize"] = "M4 12h16 M7 9l-3 3 3 3 M17 9l3 3-3 3 M12 6v12",
        ["pack"] = "M4 6h16 M4 12h16 M4 18h16 M12 2v3 M12 22v-3",
        ["markout"] = "M12 3v18 M3 12h18 M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10",
        ["label"] = "M9 4L7 20 M17 4l-2 16 M4 9h16 M3 15h16",
        ["section"] = "M3 17l6-6 4 4 8-8 M3 21h18",
        ["cube"] = "M12 3l8 4.5v9L12 21l-8-4.5v-9z M4 7.5l8 4.5 8-4.5 M12 12v9",
        ["exclusion"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10 M12 11a1 1 0 1 0 0 2a1 1 0 1 0 0-2",
        ["charge"] = "M9 3h6v18H9z M9 9h6 M9 15h6",
        ["rules"] = "M9 6h11 M9 12h11 M9 18h11 M4 6h.01 M4 12h.01 M4 18h.01",
        ["det"] = "M13 3L5 14h6l-1 7 8-11h-6z",
        ["tie"] = "M5 17a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M19 3a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M19 17a2 2 0 1 0 0 4a2 2 0 1 0 0-4 M6.5 17.5L17.5 6.5 M7 19h10",
        ["pin"] = "M9 4h6 M10 4v5l-3 4h10l-3-4V4 M12 13v7",
        ["star"] = "M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z",
        ["leadin"] = "M3 12h10 M10 8l4 4-4 4 M18 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6",
        ["dummy"] = "M12 6a6 6 0 1 0 0 12a6 6 0 1 0 0-12 M8 8l8 8",
        ["clock"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 2",
        ["vee"] = "M4 6l8 12 8-12",
        ["iso"] = "M3 11c5-8 13-8 18 0 M3 16c4-6 14-6 18 0 M6 20c3-4 9-4 12 0",
        ["play"] = "M4 5h16a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z M10 9v6l5-3z",
        ["chart"] = "M4 20V4 M4 20h16 M8 16v-5 M12 16V8 M16 16v-3",
        ["wave"] = "M3 12h3l2-6 4 12 3-8 2 2h4",
        ["air"] = "M3 9h12a3 3 0 1 0-3-3 M3 15h15a3 3 0 1 1-3 3",
        ["frag"] = "M4 15l4-6 5 2 3-5 4 6-3 6H7z",
        ["eye"] = "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6",
        ["send"] = "M4 12l16-8-6 16-3-7z",
        ["table"] = "M3 5h18v14H3z M3 10h18 M3 15h18 M9 5v14",
        ["surface"] = "M3 19l6-9 4 5 3-4 5 8z M9 10l4 9 M16 11l-3 8",
        ["laser"] = "M3 21h6 M6 21v-5 M6 16l14-10 M17 4l3 2",
        ["station"] = "M12 3v4 M12 7l-6 14 M12 7l6 14 M12 7v14",
        ["peg"] = "M12 3v18 M8 21h8 M12 3l6 3-6 3",
        ["bore"] = "M8 3c0 6 8 8 8 18 M5 3h6",
        ["cloud"] = "M7 18h10a4 4 0 0 0 .5-8a6 6 0 0 0-11.5 1.5A3.5 3.5 0 0 0 7 18z",
        ["convert"] = "M4 7h13l-3-3 M20 17H7l3 3",
        ["edit"] = "M4 20l4-1 11-11-3-3L5 16z",
        ["empty"] = "M9 3h6v18H9z M6 6l12 12",
        ["flip"] = "M7 7h10l-3-3 M17 17H7l3 3",
        ["palette"] = "M4 4h7v7H4z M13 4h7v7h-7z M4 13h7v7H4z M13 13h7v7h-7z",
        ["auto"] = "M5 19L15 9 M14 3v3 M19 5l-2 2 M21 10h-3 M12.5 7.5l4 4",
        ["ebs"] = "M7 7h10v10H7z M10 3v4 M14 3v4 M10 17v4 M14 17v4 M3 10h4 M3 14h4 M17 10h4 M17 14h4",
        ["dup"] = "M8 8h12v12H8z M4 16V4h12",
        ["money"] = "M12 3v18 M16 7h-6a3 3 0 0 0 0 6h4a3 3 0 0 1 0 6H8",
        ["dash"] = "M4 4h7v9H4z M13 4h7v5h-7z M13 11h7v9h-7z M4 15h7v5H4z",
        ["slice"] = "M3 12h18 M6 8l6-4 6 4 M6 16l6 4 6-4",
        ["check"] = "M5 12l5 5 9-10",
        ["arrow"] = "M5 19L19 5 M10 5h9v9",
        // Cog with teeth, not rays: the rayed version read as a sun (theme) rather than settings.
        ["gear"] = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6",
        ["search"] = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14 M20 20l-4-4",
        ["lock"] = "M6 11h12v9H6z M8 11V8a4 4 0 0 1 8 0v3",
        ["unlock"] = "M6 11h12v9H6z M8 11V8a4 4 0 0 1 7.5-2",
        ["help"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14 M12 17v.5",
        ["menu"] = "M4 6h16 M4 12h16 M4 18h16",
        ["down"] = "M6 9l6 6 6-6",
        ["up"] = "M6 15l6-6 6 6",
        ["right"] = "M9 6l6 6-6 6",
        ["left"] = "M15 6l-6 6 6 6",
        ["plus"] = "M12 5v14 M5 12h14",
        ["minus"] = "M5 12h14",
        ["alignLeft"] = "M4 6h16 M4 12h10 M4 18h13",
        ["alignCenter"] = "M4 6h16 M7 12h10 M5.5 18h13",
        ["alignRight"] = "M4 6h16 M10 12h10 M7 18h13",
        ["alignTop"] = "M4 4h16 M7 9h10 M7 13h10",
        ["alignMiddle"] = "M4 4h16 M4 20h16 M7 10h10 M7 14h10",
        ["alignBottom"] = "M4 20h16 M7 11h10 M7 15h10",
        ["split"] = "M4 12h16 M8 8l4-4 4 4 M8 16l4 4 4-4",
        ["trash"] = "M4 7h16 M10 11v6 M14 11v6 M6 7l1 13h10l1-13 M9 7V4h6v3",
        ["wand"] = "M4 20L16 8 M14 6l4 4 M18 3v3 M21 6h-3 M8 3v2 M9 4H7",
        ["erase"] = "M7 21h10 M5 15l9-9 5 5-9 9H8z",
        ["info"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 11v5 M12 8v.5",
        ["okc"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M8 12l3 3 5-6",
        ["warn"] = "M12 3l9 16H3z M12 10v4 M12 17v.5",
        ["arrowup"] = "M12 19V5 M6 11l6-6 6 6",
        ["arrowdown"] = "M12 5v14 M6 13l6 6 6-6"
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static bool HasIcon(string key) => Paths.ContainsKey(key);

    /// <summary>The outline of an icon in its 24×24 box, for what draws it itself.</summary>
    public static Geometry Shape(string key)
    {
        if (!Cache.TryGetValue(key, out var geo))
            Cache[key] = geo = Geometry.Parse(Paths.TryGetValue(key, out var d) ? d : Paths["point"]);
        return geo;
    }

    /// <summary>Stroke icon scaled from its 24×24 box to <paramref name="size"/> pixels.</summary>
    public static Control Icon(string key, double size = 18, IBrush? brush = null, double stroke = 1.7)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Shape(key),
            Stroke = brush ?? Text,
            StrokeThickness = stroke,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 24,
            Height = 24
        };
        return new Viewbox { Width = size, Height = size, Child = path, IsHitTestVisible = false };
    }

    /// <summary>A small keyboard-key badge.</summary>
    public static Control Kbd(string text, double fontSize = 10) => new Border
    {
        BorderBrush = LineStrong,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Background = Panel,
        Padding = new Thickness(4, 0),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontFamily = Mono, FontSize = fontSize, Foreground = Muted }
    };
}
