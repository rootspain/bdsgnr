using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>The parts of an input window: the title band, the bottom bar, groups of fields and buttons. All texts come translated.</summary>
public static class FormKit
{
    /// <summary>The band on top: title, what the window is for, and on the right what acts on the whole window.</summary>
    public static Control Header(string title, string? subtitle = null, Control? aside = null)
    {
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, TextTrimming = TextTrimming.CharacterEllipsis } }
        };
        if (subtitle != null) text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Foreground = Look.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
        var dock = new DockPanel();
        if (aside != null)
        {
            aside.VerticalAlignment = VerticalAlignment.Center;
            aside.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(aside, Dock.Right);
            dock.Children.Add(aside);
        }
        dock.Children.Add(text);
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12), Child = dock };
    }

    /// <summary>The bar at the bottom: the keys of the window on the left, its buttons on the right.</summary>
    public static Control Footer(string? hint, params Button[] buttons)
    {
        var row = Ui.Row(8, buttons.Cast<Control>().ToArray());
        DockPanel.SetDock(row, Dock.Right);
        var dock = new DockPanel { Children = { row } };
        if (hint != null) dock.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
        return new Border { Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 12), Child = dock };
    }

    public static Button Primary(string text, Action click)
    {
        var b = new Button { Content = text, Height = 32, MinWidth = 80, Padding = new Thickness(16, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primary" } };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Secondary(string text, Action click)
    {
        var b = new Button { Content = text, Height = 32, MinWidth = 80, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(6), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A small button of a list or a toolbar: a sign, with or without a text; without one the tip says what it does.</summary>
    public static Button Tool(string icon, string? text, Action click, string? tip = null)
    {
        var content = text == null ? Look.Icon(icon, 16, Look.TextSoft) : Ui.Row(6, Look.Icon(icon, 16, Look.TextSoft), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Height = 32, MinWidth = 32, Padding = new Thickness(text == null ? 8 : 12, 0), CornerRadius = new CornerRadius(6), BorderBrush = Look.LineStrong, BorderThickness = new Thickness(1), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => Ui.Safe(click);
        return b;
    }

    /// <summary>A group of fields under a small capital title.</summary>
    public static Control Section(string title, Control content) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = title.ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue },
            content
        }
    };

    public static Control Labeled(string label, Control field) => new StackPanel
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Look.Muted, TextWrapping = TextWrapping.Wrap }, field }
    };

    /// <summary>Controls side by side in equal columns, row after row.</summary>
    public static Grid Fields(int columns, params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))), ColumnSpacing = 12, RowSpacing = 10 };
        for (int i = 0; i < cells.Length; i++)
        {
            if (i % columns == 0) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(cells[i], i / columns);
            Grid.SetColumn(cells[i], i % columns);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    public static TextBox Text(string? value = null) => new()
    {
        Text = value,
        Height = 32,
        MinHeight = 32,
        Padding = new Thickness(8, 5),
        Background = Look.Field,
        BorderBrush = Look.LineStrong,
        VerticalContentAlignment = VerticalAlignment.Center
    };
}

/// <summary>How the fields of an edited object are grouped in its window, and the units of those whose name does not give one.</summary>
public static class FormLayouts
{
    private sealed record Layout(PropertySection[] Sections, Dictionary<string, string> Units);

    private static Layout Of(string units, params PropertySection[] sections)
    {
        var map = new Dictionary<string, string>();
        foreach (var group in units.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = group.Split(':');
            foreach (var name in parts[1].Split(',')) map[name.Trim()] = parts[0].Trim();
        }
        return new Layout(sections, map);
    }

    private static PropertySection S(string title, string names) => new(title, names.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly Dictionary<Type, Layout> All = new()
    {
        [typeof(ProjectHeader)] = Of("°: BlastOrientation",
            S("Blast", "Name BlastNumber ShotDate BlastOrientation"), S("Place", "Mine Bench Location Client"), S("Author and notes", "Author Created Description Comment")),
        [typeof(DesignData)] = Of("m: Burden, Spacing, DuplicateTolerance, BenchHeight, Subdrill, StemmingLength; mm: Diameter; °: Angle, Bearing; t/m³: RockDensity",
            S("Pattern", "Burden Spacing Pattern DuplicateTolerance"), S("Hole", "BenchHeight Subdrill Diameter Angle Bearing StemmingLength"), S("Rock", "RockDensity")),
        [typeof(BlastOutcomes)] = Of("",
            S("Results", "FragmentationP50 FragmentationP80 MaxFlyrock MeasuredPpv MeasuredAirblast"), S("Digging", "MuckpileHeight DiggingRate"), S("Rating", "Rating Comments")),
        [typeof(CostSchema)] = Of("", S("Costs", "Currency DrillingPerMetre LoadingPerHole LabourPerBlast OtherFixed")),
        [typeof(ElectronicSettings)] = Of("", S("Intervals", "HoleIntervalMs RowIntervalMs"), S("Limits", "MaxTimeMs TimeStepMs BaseTimeMs")),
        [typeof(VibrationModel)] = Of("", S("Vibration", "SiteK SiteB PpvLimit WindowMs"), S("Airblast", "AirK AirB AirblastLimitDb")),
        [typeof(SummaryBlock)] = Of("", S("Summary block", "Corner Visible ShowHeader ShowQuantities ShowPattern")),
        [typeof(DesignTolerances)] = Of("m: CollarXY, ToeXY, Depth", S("Position", "CollarXY ToeXY Depth"), S("Direction", "AngleDeg BearingDeg"), S("Redrilling", "RedrillFraction")),
        [typeof(FlyrockModel)] = Of("m: MinimumClearance", S("Flyrock", "SiteK SafetyFactor MinimumClearance")),
        [typeof(SiteCoordinateSystem)] = Of("m: OffsetEasting, OffsetNorthing, OffsetElevation", S("UTM", "UtmZone SouthernHemisphere"), S("Shift", "OffsetEasting OffsetNorthing OffsetElevation")),
        [typeof(TransformParameters)] = Of("m: MoveX, MoveY, MoveZ, PivotX, PivotY", S("Move", "MoveX MoveY MoveZ"), S("Rotate", "RotateDegrees PivotX PivotY PivotAtCentre"), S("Scale", "Scale")),
        [typeof(MoveCopyParameters)] = Of("m: DeltaX, DeltaY, DeltaZ", S("Shift", "DeltaX DeltaY DeltaZ Copies")),
        [typeof(BatterParameters)] = Of("m: Height; °: FaceAngle", S("Batter", "Height FaceAngle Outwards")),
        [typeof(LaserParameters)] = Of("m: StationX, StationY, StationZ, InstrumentHeight, TargetHeight", S("Station", "StationX StationY StationZ"), S("Heights", "InstrumentHeight TargetHeight")),
        [typeof(MainViewModel.CurvedLayoutParameters)] = Of("m: Burden, Spacing", S("Pattern", "Rows Burden Spacing Staggered RowsToTheLeft")),
        [typeof(MainViewModel.LasImportOptions)] = Of("m: CellSize, MaxEdge", S("Points", "CellSize KeepLowest"), S("Surface", "MaxEdge CreateSurface")),
        [typeof(PatternParameters)] = Of("m: Burden, Spacing, EdgeOffset; °: Orientation", S("Pattern", "Burden Spacing Pattern Orientation EdgeOffset"), S("Numbering", "LabelPrefix StartNumber")),
        [typeof(Layer)] = Of("", S("Layer", "Name Group Color LineWidth"), S("Display", "Visible Locked Frozen ShowText")),
        [typeof(TypeDefinition)] = Of("mm: Diameter", S("Type", "Name Color Diameter Density")),
        [typeof(LoadingRule)] = Of("m: MinLength, MaxLength, StemmingLength, SoftCollarExtraStemming; mm: MinDiameter, MaxDiameter",
            S("Rule", "Name Priority"), S("Applies to", "HoleType MinLength MaxLength MinDiameter MaxDiameter Wet"), S("Stemming", "StemmingLength StemmingProductId MinStemmingDiameters SoftCollarExtraStemming"),
            S("Alternates", "WetAlternateProductId HardAlternateProductId HardnessThreshold SoftAlternateProductId SoftnessThreshold")),
        [typeof(DeckTemplate)] = Of("m: Length, PrimerOffset", S("Deck", "ProductId Length PrimerCount PrimerOffset BoosterId DetonatorId")),
        [typeof(InitiationRule)] = Of("m: MinLength, MaxLength, PrimerOffset",
            S("Rule", "Name Priority"), S("Applies to", "HoleType RowFilter Horizon MinLength MaxLength"), S("Primers", "DetonatorId BoosterId PrimersPerDeck DelayMs PrimerOffset"))
    };

    /// <summary>Lays a grid out as a form: the groups of the type, or all its fields in one group.</summary>
    public static void Apply(PropertyGrid grid, Type type)
    {
        grid.Foldable = false;
        grid.TextInColumns = true;
        grid.Sections = All.TryGetValue(type, out var layout) ? layout.Sections : new[] { new PropertySection("Properties") };
        if (layout == null) return;
        foreach (var (name, unit) in layout.Units) grid.Units[name] = Loc.Ui(unit);
    }
}
