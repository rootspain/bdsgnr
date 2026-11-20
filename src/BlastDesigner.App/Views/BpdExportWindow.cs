using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// Choices of the Davey Tronic export: the delays (always milliseconds), which detonator of a hole is LEVEL 1, for each line
/// its PU serial number and chain of ties, and the header of the file with the UTM zone of its lines.
/// </summary>
public sealed record BpdExportChoice(BpdDelayUnit Unit, BpdLevelOrder Levels, List<BpdPathRequest> Paths, BpdHeader Header);

/// <summary>
/// "Davey Tronic (.BPD)", top to bottom:
/// <list type="bullet">
/// <item>the header of the file (HEADER): FULL_NAME (up to 30) and DISPLAYED_NAME (up to 8, padded with '_'), taken as typed by
/// <see cref="DeviTronic.ToFileName"/> (Latin letters and digits, Cyrillic in Latin, the rest one '_'), kept in the project;
/// the date and the time (YYYY/MM/DD, HH24:MI:SS), the source, and the UTM zone of the lines (for this file only: the site
/// keeps its own);</item>
/// <item>which detonator of a hole is LEVEL 1;</item>
/// <item>one row per chain of EBS ties, by the letter of its route numbers: its PU serial number, its holes and the
/// electronic detonators it gives the file; opened, the frame of the line (DETZONE). A chain of more than
/// <see cref="DeviTronic.MaxTiesPerPath"/> ties is marked with a yellow warning sign;</item>
/// <item>the lines and the detonators the file will have.</item>
/// </list>
/// Errors show as the fields are filled; the file is written only without them.
/// </summary>
public static class BpdExportWindow
{
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

    private static readonly IBrush ErrorInk = Brushes.IndianRed;

    /// <summary>The name of a field and, after it, the tag it fills in the file.</summary>
    private static Control Caption(string text, string tag) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new TextBlock { Text = Loc.T(text), FontSize = 12, Foreground = Look.TextSoft },
            new TextBlock { Text = tag, FontSize = 11, FontFamily = Look.Mono, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 11.5, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap };

    private static Control Title(string text, string tag) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new TextBlock { Text = Loc.T(text).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Blue },
            new TextBlock { Text = tag, FontSize = 11, FontFamily = Look.Mono, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static TextBox Box(string text, string name, int max) => new()
    {
        Text = text, Name = name, MaxLength = max, Height = 34, FontFamily = Look.Mono, VerticalContentAlignment = VerticalAlignment.Center,
        Background = Look.Field, BorderBrush = Look.LineStrong
    };

    private static StackPanel Stack(params Control[] children)
    {
        var s = new StackPanel { Spacing = 6 };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static async Task<BpdExportChoice?> Show(BlastProject project, IReadOnlyCollection<Hole> holes)
    {
        var all = DeviTronic.Chains(project);
        if (all.Count == 0)
        {
            await Dialogs.Info(Loc.T("There are no EBS ties in the plan: the lines (PATH) are made by chains of ties of electronic detonators."), Loc.T("DaveyTronic (.BPD)"));
            return null;
        }
        var holeChains = DeviTronic.HoleChains(project, all);
        // the line of a chain is the letter of its route numbers; the rows go by the letters
        var lineOf = DeviTronic.Lines(project, all);
        var order = Enumerable.Range(0, all.Count).OrderBy(i => lineOf[i], StringComparer.Ordinal).ToList();
        var types = order.Select(i => all[i]).ToList();
        var lines = order.Select(i => lineOf[i]).ToList();
        // what each line gives the file, built as the file is
        var preview = DeviTronic.Preview(project, holes, types, lines);

        // ---- the header of the file
        var saved = project.BpdExport;
        var fullName = Box(saved is { FullName.Length: > 0 } ? saved.FullName : DeviTronic.DefaultFullName(project), "bpdFullName", DeviTronic.FullNameLength);
        var displayed = Box(saved is { DisplayedName.Length: > 0 } ? saved.DisplayedName : DeviTronic.DefaultDisplayedName(fullName.Text!), "bpdDisplayedName", DeviTronic.NameLength);
        // the displayed name follows the full one while it is the one the window put there (not typed in itself)
        var auto = DeviTronic.DefaultDisplayedName(fullName.Text!);
        var fullCount = new TextBlock { FontSize = 11, FontFamily = Look.Mono, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var displayedCount = new TextBlock { FontSize = 11, FontFamily = Look.Mono, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        fullName.InnerRightContent = fullCount;
        displayed.InnerRightContent = displayedCount;
        // as the programming unit shows it: padded with '_' to 8
        var shown = new TextBlock { FontFamily = Look.Mono, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center, Name = "bpdDisplayedShown" };
        var shownBox = new Border { Height = 34, Padding = new Thickness(10, 0), Background = Look.Ground, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = shown };
        ToolTip.SetTip(shownBox, Loc.T("As the programming unit shows it"));
        var opened = DateTime.Now;
        var date = Box(opened.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), "bpdDate", 10);
        date.Watermark = "YYYY/MM/DD";
        date.Width = 150;
        var time = Box(opened.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "bpdTime", 8);
        time.Watermark = "HH24:MI:SS";
        time.Width = 110;
        var nowButton = new Button { Content = Loc.T("Now"), Height = 34, Padding = new Thickness(12, 0), VerticalContentAlignment = VerticalAlignment.Center, Name = "bpdNow" };
        nowButton.Click += (_, _) =>
        {
            var t = DateTime.Now;
            date.Text = t.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            time.Text = t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        };
        var version = typeof(BpdExportWindow).Assembly.GetName().Version;
        var source = Box("Blast Designer " + (version == null ? "6" : $"{version.Major}.{version.Minor}.{version.Build}"), "bpdSource", DeviTronic.SourceLength);
        source.FontFamily = FontFamily.Default;

        // the UTM zone of the lines: for this file only
        var zone = new NumericUpDown
        {
            Value = project.Coordinates.UtmZone is >= 1 and <= 60 ? project.Coordinates.UtmZone : null, Minimum = 1, Maximum = 60, Increment = 1, FormatString = "0",
            Width = 110, Height = 34, FontFamily = Look.Mono, VerticalContentAlignment = VerticalAlignment.Center, Name = "bpdZone"
        };
        var southern = project.Coordinates.SouthernHemisphere;
        var zoneShown = new TextBlock { FontFamily = Look.Mono, FontSize = 12, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Name = "bpdZoneShown" };
        Button north = null!, south = null!;
        Action describe = () => { };
        void Hemisphere(bool s)
        {
            southern = s;
            Ui.SetChip(north, !s);
            Ui.SetChip(south, s);
            describe();
        }
        north = Ui.Chip(Loc.T("N · northern"), () => Hemisphere(false), !southern);
        south = Ui.Chip(Loc.T("S · southern"), () => Hemisphere(true), southern);
        north.Name = "bpdNorth";
        south.Name = "bpdSouth";

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 18, RowSpacing = 14 };
        void Put(Control c, int row, int col, int span = 1) { Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); fields.Children.Add(c); }
        var displayedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { displayed } };
        Grid.SetColumn(shownBox, 1);
        displayedRow.Children.Add(shownBox);
        Put(Stack(Caption("Full name", "FULL_NAME"), fullName, Hint(Loc.F("Latin letters, digits and _, up to {0}; Cyrillic in Latin letters", DeviTronic.FullNameLength))), 0, 0);
        Put(Stack(Caption("Displayed name", "DISPLAYED_NAME"), displayedRow, Hint(Loc.T("8 characters on the programming unit, a shorter name is padded with _"))), 0, 1);
        Put(Stack(Caption("Date and time", "DATE · TIME"), Ui.Row(8, date, time, nowButton),
            Ui.Row(8, new TextBlock { Text = Loc.T("Date") + ": YYYY/MM/DD", Width = 150, FontSize = 11.5, Foreground = Look.Faint }, new TextBlock { Text = Loc.T("Time") + ": HH24:MI:SS", FontSize = 11.5, Foreground = Look.Faint })), 1, 0);
        Put(Stack(Caption("Source", "SOURCE_FILE"), source), 1, 1);
        Put(new Border
        {
            BorderBrush = Look.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0),
            Child = Stack(Caption("UTM zone of the lines", "UTM_ZONE_NUMBER · UTM_ZONE_LETTER"),
                Ui.Row(10, zone, north, south, zoneShown),
                Hint(Loc.T("For this file only: the coordinate system of the site does not change")))
        }, 2, 0, 2);

        var headerTitle = new DockPanel();
        var keep = Ui.Row(6, Look.Icon("save", 13, Look.Faint), new TextBlock { Text = Loc.T("The names are kept in the project"), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(keep, Dock.Right);
        headerTitle.Children.Add(keep);
        headerTitle.Children.Add(Title("File header", "HEADER"));
        var headerCard = new Border
        {
            Background = Look.Ribbon, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14),
            Child = new StackPanel { Spacing = 14, Children = { headerTitle, fields } }
        };

        // ---- which detonator of a hole is LEVEL 1
        Button top = null!, bottom = null!;
        void Level(BpdLevelOrder l)
        {
            _levels = l;
            Ui.SetChip(top, l == BpdLevelOrder.TopFirst);
            Ui.SetChip(bottom, l == BpdLevelOrder.BottomFirst);
        }
        top = Ui.Chip(Loc.T("Top detonator"), () => Level(BpdLevelOrder.TopFirst), _levels == BpdLevelOrder.TopFirst);
        bottom = Ui.Chip(Loc.T("Bottom detonator"), () => Level(BpdLevelOrder.BottomFirst), _levels == BpdLevelOrder.BottomFirst);
        top.Name = "bpdLevelTop";
        bottom.Name = "bpdLevelBottom";
        var ms = new TextBlock { Text = Loc.T("Delays in milliseconds"), FontSize = 12, Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center };
        var levels = new DockPanel();
        DockPanel.SetDock(ms, Dock.Right);
        levels.Children.Add(ms);
        levels.Children.Add(Ui.Row(12, Stack(new TextBlock { Text = Loc.T("Order of the detonators in a hole"), FontWeight = FontWeight.SemiBold, Foreground = Look.Text },
            new TextBlock { Text = Loc.T("Which of them is LEVEL 1"), FontSize = 11.5, Foreground = Look.Faint }), top, bottom));

        // ---- lines: an opener, the letter, PU serial number, the chain, its holes and detonators; opened, its frame
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,150,*,Auto,Auto,20"), ColumnSpacing = 12, RowSpacing = 6, Name = "bpdPaths" };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Cell(Control c, int row, int col, int span = 1) { Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); grid.Children.Add(c); }
        TextBlock Head(string text, bool right = false) => new() { Text = text, FontSize = 12, Foreground = Look.Muted, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Cell(Head(Loc.T("Line")), 0, 1);
        Cell(Head("PU Serial Number"), 0, 2);
        Cell(Head(Loc.T("Chain of ties")), 0, 3);
        Cell(Head(Loc.T("Holes"), true), 0, 4);
        Cell(Head(Loc.T("Detonators"), true), 0, 5);
        var serialBoxes = new List<TextBox>();
        var holeIds = holes.Select(h => h.Id).ToHashSet();
        for (int i = 0; i < types.Count; i++)
        {
            int row = 1 + i * 2;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var chain = types[i];
            var chainHoles = project.Holes.Where(h => holeIds.Contains(h.Id) && holeChains.TryGetValue(h.Id, out var k) && k == chain.Key).ToList();
            var (dets, frame) = preview[i];
            var details = FrameDetails(project, frame, chainHoles);
            details.IsVisible = false;
            details.Name = "bpdFrame" + i;
            // a chevron large enough to be seen and hit: right closed, down opened
            var opener = new Button
            {
                Content = Look.Icon("right", 20, Look.TextSoft, 2), Width = 34, Height = 34, Padding = new Thickness(0), CornerRadius = new CornerRadius(7),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Look.Ribbon, BorderBrush = Look.Line, BorderThickness = new Thickness(1), Name = "bpdOpen" + i, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(opener, Loc.T("The frame of the line (DETZONE)"));
            opener.Click += (_, _) =>
            {
                details.IsVisible = !details.IsVisible;
                opener.Content = Look.Icon(details.IsVisible ? "down" : "right", 20, details.IsVisible ? Look.Accent : Look.TextSoft, 2);
            };
            var id = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = Look.AccentSoft, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = lines[i], FontFamily = Look.Mono, FontWeight = FontWeight.SemiBold, Foreground = Look.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Name = "bpdLine" + i }
            };
            var serial = FormKit.Text(Serials.TryGetValue(chain.Key, out var s) ? s : "");
            serial.FontFamily = Look.Mono;
            serial.Name = "bpdSerial" + i;
            serial.Watermark = Loc.T("PU number");
            var name = Loc.F("{0}: holes {1}–{2}, {3} ties", Loc.T(chain.TieTypeName), chain.FirstHole, chain.LastHole, chain.TieCount);
            var type = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Look.TextSoft, Name = "bpdChain" + i };
            ToolTip.SetTip(type, name);
            var count = new TextBlock { Text = holeChains.Values.Count(k => k == chain.Key).ToString(CultureInfo.CurrentCulture), FontFamily = Look.Mono, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Name = "bpdHoles" + i };
            var detCount = new TextBlock { Text = dets.ToString(CultureInfo.CurrentCulture), FontFamily = Look.Mono, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Name = "bpdDets" + i };
            var warn = WarningSign();
            warn.Name = "bpdWarn" + i;
            warn.IsVisible = chain.TieCount > DeviTronic.MaxTiesPerPath;
            ToolTip.SetTip(warn, Loc.F("{0} ties in the chain: more than {1}.", chain.TieCount, DeviTronic.MaxTiesPerPath));
            Cell(opener, row, 0);
            Cell(id, row, 1);
            Cell(serial, row, 2);
            Cell(type, row, 3);
            Cell(count, row, 4);
            Cell(detCount, row, 5);
            Cell(warn, row, 6);
            Cell(details, row + 1, 1, 6);
            serialBoxes.Add(serial);
        }

        // ---- what the file will have
        Control Total(string text, string tag, int value, string name) => new Border
        {
            Background = Look.Ribbon, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    Caption(text, tag),
                    new TextBlock { Text = value.ToString(CultureInfo.CurrentCulture), FontFamily = Look.Mono, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Name = name }
                }
            }
        };
        var totals = Ui.Row(8, Total("Lines", "PATH_QUANTITY", preview.Count(x => x.Detonators > 0), "bpdTotalLines"),
            Total("Detonators", "TOTAL_DETS_QUANTITY", preview.Sum(x => x.Detonators), "bpdTotalDets"));

        var error = new TextBlock { Foreground = ErrorInk, TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "bpdError" };
        var content = new StackPanel
        {
            Spacing = 16, MinWidth = 820,
            Children =
            {
                headerCard,
                levels,
                FormKit.Section(Loc.T("Lines (PATH)"), new ScrollViewer { MaxHeight = 320, Content = grid }),
                Hint(Loc.T("Only the chains of EBS ties make lines; the letter of a line is that of its route numbers. Holes are numbered along the ties in the order they were drawn.")),
                totals,
                error
            }
        };

        // ---- the rules of the fields, as they are typed
        var busy = false;
        void Rule(TextBox box, Func<string, string> rule)
        {
            box.TextChanged += (_, _) =>
            {
                if (busy) return;
                var was = box.Text ?? "";
                var now = rule(was);
                if (now == was) return;
                busy = true;
                var caret = box.CaretIndex;
                box.Text = now;
                box.CaretIndex = Math.Clamp(caret + now.Length - was.Length, 0, now.Length);
                busy = false;
            };
        }
        Rule(fullName, t => DeviTronic.ToFileName(t, DeviTronic.FullNameLength));
        Rule(displayed, t => DeviTronic.ToFileName(t, DeviTronic.NameLength));
        Rule(source, t => new string(t.Where(c => c is >= ' ' and <= '~').Take(DeviTronic.SourceLength).ToArray()));
        fullName.TextChanged += (_, _) =>
        {
            if (busy || displayed.Text != auto) return;
            auto = DeviTronic.DefaultDisplayedName(fullName.Text ?? "");
            busy = true;
            displayed.Text = auto;
            busy = false;
        };

        bool Validate()
        {
            var errors = new List<string>();
            void Mark(TemplatedControl c, bool bad) => c.BorderBrush = bad ? ErrorInk : Look.LineStrong;
            var fullBad = (fullName.Text ?? "").Length == 0;
            if (fullBad) errors.Add(Loc.T("Enter the full name (FULL_NAME)."));
            Mark(fullName, fullBad);
            var displayedBad = (displayed.Text ?? "").Length == 0;
            if (displayedBad) errors.Add(Loc.T("Enter the displayed name (DISPLAYED_NAME)."));
            Mark(displayed, displayedBad);
            var dateBad = !DateTime.TryParseExact((date.Text ?? "").Trim(), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if (dateBad) errors.Add(Loc.T("The date is YYYY/MM/DD."));
            Mark(date, dateBad);
            var timeBad = !DateTime.TryParseExact((time.Text ?? "").Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if (timeBad) errors.Add(Loc.T("The time is HH24:MI:SS."));
            Mark(time, timeBad);
            var zoneBad = zone.Value is not (>= 1 and <= 60);
            if (zoneBad) errors.Add(Loc.T("The UTM zone number is 1 to 60."));
            Mark(zone, zoneBad);
            for (int i = 0; i < types.Count; i++)
            {
                var text = serialBoxes[i].Text?.Trim() ?? "";
                var bad = text.Length == 0 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                if (bad) errors.Add(Loc.F("Line {0}: PU Serial Number must be a whole number.", lines[i]));
                Mark(serialBoxes[i], bad);
            }
            error.Text = string.Join("\n", errors.Take(4)) + (errors.Count > 4 ? "\n…" : "");
            error.IsVisible = errors.Count > 0;
            return errors.Count == 0;
        }

        // the counters, the displayed name as shown, the zone; and what is wrong, at once
        describe = () =>
        {
            fullCount.Text = $"{(fullName.Text ?? "").Length}/{DeviTronic.FullNameLength}";
            displayedCount.Text = $"{(displayed.Text ?? "").Length}/{DeviTronic.NameLength}";
            shown.Text = (displayed.Text ?? "").Length == 0 ? "" : DeviTronic.PadDisplayed(displayed.Text!);
            zoneShown.Text = zone.Value is decimal z ? $"UTM {z:0}{(southern ? "S" : "N")}" : "";
            Validate();
        };
        foreach (var box in new[] { fullName, displayed, date, time, source }.Concat(serialBoxes)) box.TextChanged += (_, _) => { if (!busy) describe(); };
        zone.ValueChanged += (_, _) => describe();
        describe();

        if (!await Dialogs.Custom("DaveyTronic (.BPD)", content, Validate, serialBoxes.FirstOrDefault(), width: 900, okText: Loc.T("Write the file…"))) return null;

        var paths = new List<BpdPathRequest>();
        for (int i = 0; i < types.Count; i++)
        {
            var key = types[i].Key;
            var serial = serialBoxes[i].Text!.Trim();
            Serials[key] = serial;
            paths.Add(new BpdPathRequest(key, serial, lines[i]));
        }
        DateTime.TryParseExact((date.Text ?? "").Trim() + " " + (time.Text ?? "").Trim(), "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created);
        var header = new BpdHeader(created, fullName.Text!, displayed.Text!, source.Text ?? "", (int)zone.Value!.Value, southern);
        return new BpdExportChoice(BpdDelayUnit.Ms, _levels, paths, header);
    }

    /// <summary>
    /// The frame of a line (DETZONE) under its row: a small drawing of the frame with the holes written, and its four corners
    /// in UTM metres; computed, not edited. A line with nothing to write says so.
    /// </summary>
    private static Control FrameDetails(BlastProject project, (long West, long South, long East, long North)? frame, List<Hole> holes)
    {
        if (frame is not { } f)
            return new TextBlock { Text = Loc.T("The line gives the file no holes."), FontSize = 12, Foreground = Look.Faint, Margin = new Thickness(0, 0, 0, 8) };
        const double w = 150, h = 96, pad = 12;
        var canvas = new Canvas { Width = w, Height = h, Background = Look.Field, ClipToBounds = true };
        double spanE = Math.Max(1, f.East - f.West), spanN = Math.Max(1, f.North - f.South);
        var scale = Math.Min((w - 2 * pad) / spanE, (h - 2 * pad) / spanN);
        double fw = spanE * scale, fh = spanN * scale, x0 = (w - fw) / 2, y0 = (h - fh) / 2;
        var rect = new Rectangle { Width = fw, Height = fh, Stroke = Look.Accent, StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 } };
        Canvas.SetLeft(rect, x0);
        Canvas.SetTop(rect, y0);
        canvas.Children.Add(rect);
        foreach (var hole in holes)
        {
            var e = hole.Collar.X + project.Coordinates.OffsetEasting;
            var n = hole.Collar.Y + project.Coordinates.OffsetNorthing;
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Look.Text };
            Canvas.SetLeft(dot, x0 + (e - f.West) * scale - 2.5);
            Canvas.SetTop(dot, y0 + (f.North - n) * scale - 2.5);
            canvas.Children.Add(dot);
        }
        var border = new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = canvas };
        string Point(long e, long n) => $"E {e.ToString("N0", CultureInfo.CurrentCulture)} · N {n.ToString("N0", CultureInfo.CurrentCulture)}";
        Control Corner(string name, string tag, long e, long n) => new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Caption(name, tag),
                new TextBlock { Text = Point(e, n), FontFamily = Look.Mono, Foreground = Look.Text }
            }
        };
        var corners = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 18, RowSpacing = 8 };
        void Put(Control c, int row, int col, int span = 1) { Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); corners.Children.Add(c); }
        Put(Corner("Upper left", "UPPER_LEFT", f.West, f.North), 0, 0);
        Put(Corner("Upper right", "UPPER_RIGHT", f.East, f.North), 0, 1);
        Put(Corner("Lower left", "LOWER_LEFT", f.West, f.South), 1, 0);
        Put(Corner("Lower right", "LOWER_RIGHT", f.East, f.South), 1, 1);
        Put(Hint(Loc.T("The frame of the holes of the line, to the metre. Computed, not edited.")), 2, 0, 2);
        return new Border
        {
            Background = Look.Ribbon, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 6),
            Child = Ui.Row(18, border, corners)
        };
    }
}
