
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.Views;

/// <summary>
/// Key to the surface fills on the Visibility tab: one swatch per kind of surface in the project when surfaces are
/// coloured by kind, the height ramp with the lowest and highest elevation when they are coloured by elevation.
/// </summary>
public static class SurfaceLegend
{
    /// <summary>Null when the project has no surfaces.</summary>
    public static Control? Build(BlastProject p, ProgramSettings settings)
    {
        var surfaces = p.Entities.OfType<SurfaceEntity>().ToList();
        if (surfaces.Count == 0) return null;
        var panel = new StackPanel { Spacing = 4 };
        // Both modes stretch over one range, as on the plan: from the lowest point of any surface to the highest.
        var z = surfaces.SelectMany(s => s.Vertices).Select(v => v.Z).DefaultIfEmpty(0).ToList();
        string Metres(double v) => v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " " + Loc.Ui("m");
        if (settings.SurfacesByType)
        {
            panel.Children.Add(Note(Loc.Ui("Colour: kind of surface; one height scale for all, darker at the lowest point, lighter at the highest")));
            panel.Children.Add(new TextBlock { Text = Metres(z.Min()) + "  →  " + Metres(z.Max()), FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Muted, Margin = new Thickness(3, 0, 0, 4) });
            foreach (var group in surfaces.GroupBy(s => s.Kind).OrderBy(g => (int)g.Key))
            {
                var swatch = new Border
                {
                    Width = 22,
                    Height = 14,
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(3, 0, 13, 0),
                    // Low to high, as the plan lightens the fill towards the top.
                    Background = Ramp(Color.FromUInt32(settings.SurfaceColor(group.Key.ToString()))),
                    BorderBrush = Look.LineStrong,
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var text = new TextBlock { Text = $"{Loc.Enum(group.Key)} ({group.Count()})", FontSize = 12.5, Foreground = Look.TextSoft, VerticalAlignment = VerticalAlignment.Center };
                if (group.Key == SurfaceKind.PointCloud) text.Text += " — " + Loc.Ui("points");
                panel.Children.Add(Ui.Row(0, swatch, text));
            }
            if (surfaces.Any(s => s.Color != null))
                panel.Children.Add(Note(Loc.Ui("Surfaces with their own colour keep it")));
        }
        else
        {
            // One scale for all surfaces, as on the plan: from the lowest point of any surface to the highest.
            panel.Children.Add(Note(Loc.Ui("Colour: elevation, one scale for all surfaces")));
            var ramp = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative)
            };
            for (int i = 0; i <= 5; i++)
            {
                var c = ColorRamp.Elevation(i / 5.0);
                ramp.GradientStops.Add(new GradientStop(Color.FromRgb(c.Red, c.Green, c.Blue), i / 5.0));
            }
            var bar = new Border { Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 4, 3, 2), Background = ramp };
            panel.Children.Add(bar);
            var ends = new DockPanel { Margin = new Thickness(3, 0) };
            var high = new TextBlock { Text = Metres(z.Max()), FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Muted };
            DockPanel.SetDock(high, Dock.Right);
            ends.Children.Add(high);
            ends.Children.Add(new TextBlock { Text = Metres(z.Min()), FontFamily = Look.Mono, FontSize = 11, Foreground = Look.Muted });
            panel.Children.Add(ends);
        }
        panel.Children.Add(Note(Loc.Ui("Change in Program settings ▸ Viewport")));
        return panel;
    }

    private static IBrush Ramp(Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Scale(c, PlanRenderer.KindLow), 0),
            new GradientStop(Scale(c, PlanRenderer.KindHigh), 1)
        }
    };

    private static Color Scale(Color c, double f) =>
        Color.FromRgb((byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Look.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2) };
}
