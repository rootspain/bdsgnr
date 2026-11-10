using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>Choices of the DaveyTronic export: units of the delays and, for each line, the PU serial number and the chain of ties it takes.</summary>
public sealed record BpdExportChoice(BpdDelayUnit Unit, BpdLevelOrder Levels, List<BpdPathRequest> Paths);

/// <summary>
/// "DaveyTronic (.BPD)": the delay units and one row per chain of ties of the plan (ties of one type joined end to end; one
/// type may make several chains); each row gives a line (PATH) its programming unit serial number and its chain. The first
/// row is line A, the second B and so on. A chain of more than <see cref="DeviTronic.MaxTiesPerPath"/> ties is marked with a
/// yellow warning sign.
/// </summary>
public static class BpdExportWindow
{
    private static BpdDelayUnit _unit = BpdDelayUnit.Ms;
    private static BpdLevelOrder _levels = BpdLevelOrder.BottomFirst;
    // the serial numbers typed last, by chain (its first tie), for the next export in this session
    private static readonly Dictionary<string, string> Serials = new();

    /// <summary>Yellow triangle with an exclamation mark: the chain of the row has more ties than a line may take.</summary>
    private static Control WarningSign() => new Panel
    {
        Width = 18, Height = 16, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        Children =
        {
            new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M9,0.5 L17.5,15.5 L0.5,15.5 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x00)),
                Stroke = new SolidColorBrush(Color.FromRgb(0xB7, 0x86, 0x00)), StrokeThickness = 1, StrokeJoin = PenLineJoin.Round
            },
            new TextBlock { Text = "!", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2.5, 0, 0) }
        }
    };

    public static async Task<BpdExportChoice?> Show(BlastProject project)
    {
        var types = DeviTronic.Chains(project);
        if (types.Count == 0)
        {
            await Dialogs.Info(Loc.T("There are no ties in the plan: the lines (PATH) are made by chains of ties."), Loc.T("DaveyTronic (.BPD)"));
            return null;
        }
        var holeChains = DeviTronic.HoleChains(project, types);
        var typeNames = types.Select(c => Loc.F("{0}: holes {1}–{2}, {3} ties ({4} holes)", Loc.T(c.TieTypeName), c.FirstHole, c.LastHole, c.TieCount,
            holeChains.Values.Count(k => k == c.Key))).ToList();

        // ---- delay units
        var ms = new RadioButton { Content = Loc.T("ms"), GroupName = "bpdUnit", IsChecked = _unit == BpdDelayUnit.Ms, Name = "bpdUnitMs" };
        var tenth = new RadioButton { Content = Loc.T("0.1 ms"), GroupName = "bpdUnit", IsChecked = _unit == BpdDelayUnit.TenthMs, Name = "bpdUnitTenth" };
        var units = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children = { new TextBlock { Text = Loc.T("Delays in:"), VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Muted, Width = 110 }, ms, tenth }
        };

        // ---- which detonator of a hole is LEVEL 1
        var top = new RadioButton { Content = Loc.T("Top detonator"), GroupName = "bpdLevel", IsChecked = _levels == BpdLevelOrder.TopFirst, Name = "bpdLevelTop" };
        var bottom = new RadioButton { Content = Loc.T("Bottom detonator"), GroupName = "bpdLevel", IsChecked = _levels == BpdLevelOrder.BottomFirst, Name = "bpdLevelBottom" };
        var levels = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16,
            Children = { new TextBlock { Text = "LEVEL:1", VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Muted, Width = 110 }, top, bottom }
        };

        // ---- lines: ID, PU serial number, the chain of ties (fixed: one row per chain)
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,160,*,20"), ColumnSpacing = 12, RowSpacing = 8, Name = "bpdPaths" };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Head(string text, int col)
        {
            var t = new TextBlock { Text = text, FontSize = 12, Foreground = Look.Muted };
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Head(Loc.T("Line"), 0);
        Head("PU Serial Number", 1);
        Head(Loc.T("Chain of ties"), 2);
        var serialBoxes = new List<TextBox>();
        for (int i = 0; i < types.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var id = new TextBlock { Text = ((char)('A' + i)).ToString(), FontFamily = Look.Mono, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var serial = FormKit.Text(Serials.TryGetValue(types[i].Key, out var s) ? s : "");
            serial.FontFamily = Look.Mono;
            serial.Name = "bpdSerial" + i;
            var type = new TextBlock { Text = typeNames[i], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.Text, Name = "bpdChain" + i };
            ToolTip.SetTip(type, typeNames[i]);
            Grid.SetRow(id, i + 1);
            Grid.SetRow(serial, i + 1); Grid.SetColumn(serial, 1);
            Grid.SetRow(type, i + 1); Grid.SetColumn(type, 2);
            grid.Children.Add(id);
            grid.Children.Add(serial);
            grid.Children.Add(type);
            var warn = WarningSign();
            warn.Name = "bpdWarn" + i;
            Grid.SetRow(warn, i + 1); Grid.SetColumn(warn, 3);
            grid.Children.Add(warn);
            var count = types[i].TieCount;
            warn.IsVisible = count > DeviTronic.MaxTiesPerPath;
            ToolTip.SetTip(warn, Loc.F("{0} ties in the chain: more than {1}.", count, DeviTronic.MaxTiesPerPath));
            serialBoxes.Add(serial);
        }

        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "bpdError" };
        var content = new StackPanel
        {
            Spacing = 14, MinWidth = 520,
            Children =
            {
                units,
                levels,
                FormKit.Section(Loc.T("Lines (PATH)"), new ScrollViewer { MaxHeight = 360, Content = grid }),
                new TextBlock { Text = Loc.T("Each line takes the holes of its chain of ties. Holes are numbered along the ties in the order they were drawn."), FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap },
                error
            }
        };

        bool Validate()
        {
            string? msg = null;
            for (int i = 0; i < types.Count && msg == null; i++)
            {
                var text = serialBoxes[i].Text?.Trim() ?? "";
                if (text.Length == 0 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    msg = Loc.F("Line {0}: PU Serial Number must be a whole number.", (char)('A' + i));
            }
            error.Text = msg;
            error.IsVisible = msg != null;
            return msg == null;
        }

        if (!await Dialogs.Custom("DaveyTronic (.BPD)", content, Validate, serialBoxes.FirstOrDefault(), width: 780)) return null;

        _unit = tenth.IsChecked == true ? BpdDelayUnit.TenthMs : BpdDelayUnit.Ms;
        _levels = top.IsChecked == true ? BpdLevelOrder.TopFirst : BpdLevelOrder.BottomFirst;
        var paths = new List<BpdPathRequest>();
        for (int i = 0; i < types.Count; i++)
        {
            var key = types[i].Key;
            var serial = serialBoxes[i].Text!.Trim();
            Serials[key] = serial;
            paths.Add(new BpdPathRequest(key, serial));
        }
        return new BpdExportChoice(_unit, _levels, paths);
    }
}
