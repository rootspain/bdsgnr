using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>
/// Key to how the plan draws a hole (PlanRenderer.DrawHole): outline colour by hole type, fill by state, and the marks
/// for dummy holes, exceptions, locked holes, loading warnings and fired holes. Shown on the Visibility tab.
/// </summary>
public static class HoleSymbolLegend
{
    private const double R = 6;

    public static Control Build(BlastProject p, uint background)
    {
        var used = p.Holes.Select(h => h.HoleType).Distinct().ToHashSet();
        var types = p.Design.HoleTypes.Where(t => used.Contains(t.Index)).DefaultIfEmpty(p.Design.HoleTypes.First()).ToList();
        var bg = Color.FromUInt32(background);
        // Same lightening of dark colours on a dark plan as PlanRenderer.Adapt, so the key matches what the plan shows.
        var dark = Look.Luminance(bg) < 0.4;
        Color Adapt(uint argb)
        {
            var c = Color.FromUInt32(argb);
            if (!dark || Look.Luminance(c) >= 0.3) return c;
            const double t = 0.55;
            return Color.FromArgb(c.A, (byte)(c.R + (230 - c.R) * t), (byte)(c.G + (233 - c.G) * t), (byte)(c.B + (237 - c.B) * t));
        }
        var typeColor = Adapt(types[0].Color);
        var panel = new StackPanel { Spacing = 4 };

        void Head(string text) => panel.Children.Add(new TextBlock { Text = Loc.Ui(text), FontSize = 11, Foreground = Look.Faint, Margin = new Thickness(0, 8, 0, 2) });
        void Item(Control glyph, string text)
        {
            panel.Children.Add(new DockPanel
            {
                Children =
                {
                    Dock(glyph),
                    new TextBlock { Text = text, FontSize = 12.5, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
                }
            });
        }

        Head("Outline colour: hole type");
        foreach (var t in types) Item(Collar(Adapt(t.Color), bg), Loc.Name(t.Name));

        Head("Fill: hole state");
        Item(Collar(typeColor, bg), Loc.Enum(HoleState.Designed));
        Item(Collar(typeColor, WithAlpha(typeColor, 90)), Loc.Enum(HoleState.Drilled) + " / " + Loc.Enum(HoleState.Dipped));
        Item(Collar(typeColor, Color.FromUInt32(0xFFFDE68A)), Loc.Enum(HoleState.Released));
        Item(Collar(typeColor, typeColor), Loc.Enum(HoleState.Loaded));
        Item(Collar(typeColor, Color.FromUInt32(0xFF6B7280)), Loc.Enum(HoleState.Fired));
        var explosive = p.Products.FirstOrDefault(x => x.IsExplosive);
        Item(Collar(typeColor, Color.FromUInt32(explosive?.Color ?? 0xFFFFC107)), Loc.Ui("With “Loading” on: colour of the explosive in the column"));

        Head("Marks");
        Item(Glyph(c =>
        {
            Add(c, new Ellipse { Width = 2 * R, Height = 2 * R, Stroke = new SolidColorBrush(typeColor), StrokeThickness = 1.5 }, 0);
            Add(c, new Line { StartPoint = new Point(-R, -R), EndPoint = new Point(R, R), Stroke = new SolidColorBrush(typeColor), StrokeThickness = 1.5 }, null);
            Add(c, new Line { StartPoint = new Point(-R, R), EndPoint = new Point(R, -R), Stroke = new SolidColorBrush(typeColor), StrokeThickness = 1.5 }, null);
        }), Loc.T("Dummy hole"));
        Item(Glyph(c =>
        {
            AddCollar(c, typeColor, bg);
            Add(c, new Ellipse { Width = 2 * R + 6, Height = 2 * R + 6, Stroke = new SolidColorBrush(Color.FromUInt32(0xFFDC2626)), StrokeThickness = 1.5 }, 0);
        }), Loc.Ui("Red ring: the hole has exceptions"));
        Item(Glyph(c =>
        {
            AddCollar(c, typeColor, bg);
            Add(c, new Rectangle { Width = 3, Height = 3, Fill = new SolidColorBrush(Color.FromUInt32(0xFF6B7280)) }, null, R + 1.5, R + 1.5);
        }), Loc.Ui("Grey square: the hole is locked"));
        Item(Glyph(c =>
        {
            AddCollar(c, typeColor, bg, dy: 4);
            var tri = new Polygon
            {
                Points = { new Point(0, -11), new Point(5, -3), new Point(-5, -3) },
                Fill = new SolidColorBrush(Color.FromUInt32(0xFFF59E0B)),
                Stroke = new SolidColorBrush(Color.FromUInt32(0xFF7C4A03)),
                StrokeThickness = 1
            };
            Add(c, tri, null);
        }), Loc.Ui("Amber triangle: the charge has warnings"));
        Item(Glyph(c =>
        {
            Add(c, new Line { StartPoint = new Point(-4, -5), EndPoint = new Point(7, 6), Stroke = new SolidColorBrush(WithAlpha(typeColor, 160)), StrokeThickness = 1 }, null);
            Add(c, new Ellipse { Width = 3, Height = 3, Fill = new SolidColorBrush(WithAlpha(typeColor, 160)) }, null, 7, 6);
            AddCollar(c, typeColor, bg, -4, -5, 4.5);
        }), Loc.Ui("Line from the collar to the toe (angled holes, 3D)"));
        Item(Glyph(c =>
        {
            Add(c, new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(typeColor), Stroke = Look.Text, StrokeThickness = 1 }, 0);
            AddCollar(c, typeColor, bg);
        }), Loc.Ui("Large circle: fired at this moment of the timing animation"));
        Item(Glyph(c => AddCollar(c, Color.FromUInt32(0xFF4C9BFF), bg, stroke: 2.5)), Loc.Ui("Thick outline in the selection colour: selected"));
        return panel;
    }

    private static Control Dock(Control glyph)
    {
        glyph.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(glyph, Avalonia.Controls.Dock.Left);
        return glyph;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Control Glyph(Action<Canvas> draw)
    {
        var c = new Canvas { Width = 28, Height = 26, ClipToBounds = false };
        draw(c);
        return c;
    }

    private static Control Collar(Color outline, Color fill) => Glyph(c => AddCollar(c, outline, fill));

    private static void AddCollar(Canvas c, Color outline, Color fill, double dx = 0, double dy = 0, double r = R, double stroke = 1.5) =>
        Add(c, new Ellipse { Width = 2 * r, Height = 2 * r, Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(outline), StrokeThickness = stroke }, 0, dx, dy);

    /// <summary>Places a shape on the 28×26 glyph: centred on (dx, dy) from the middle, or by its own coordinates when <paramref name="centre"/> is null.</summary>
    private static void Add(Canvas c, Control shape, double? centre, double dx = 0, double dy = 0)
    {
        const double cx = 14, cy = 13;
        if (centre != null || shape is Ellipse or Rectangle)
        {
            Canvas.SetLeft(shape, cx + dx - shape.Width / 2);
            Canvas.SetTop(shape, cy + dy - shape.Height / 2);
        }
        else
        {
            Canvas.SetLeft(shape, cx);
            Canvas.SetTop(shape, cy);
        }
        c.Children.Add(shape);
    }
}
