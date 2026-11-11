using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>What the EBS opening point window returns: the values of the point, and whether manual times take its times too.</summary>
public sealed record OpeningPointSetup(EbsOpeningPoint Point, bool KeepManualTimes);

/// <summary>
/// EBS opening point: burden and spacing with their delays, the bearing of the burden, the time of the first hole and
/// "Open the segment at this point"; the overall relief and what the point would do to its holes follow every change.
/// </summary>
public static class OpeningPointWindow
{
    private static string L(string english) => Loc.Ui(english);
    private static string N(double v, string format = "0.##") => v.ToString(format, CultureInfo.CurrentCulture);
    private static readonly IBrush Ok = new SolidColorBrush(Color.FromUInt32(0xFF5BC08A));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromUInt32(0xFFF0C13A));

    /// <summary>A new point at <paramref name="at"/> (<paramref name="existing"/> null) or a point of the project to edit; <paramref name="holes"/> are those it times.</summary>
    public static async Task<OpeningPointSetup?> Show(MainViewModel vm, Vec3 at, IReadOnlyList<Hole> holes, EbsOpeningPoint? existing = null)
    {
        var p = vm.Project!;
        var op = existing != null ? (EbsOpeningPoint)existing.CloneWithNewId() : new EbsOpeningPoint
        {
            // the pattern of the project and its electronic intervals: a row interval a burden, a hole interval a spacing
            BurdenM = p.Design.Burden > 0 ? p.Design.Burden : 4,
            SpacingM = p.Design.Spacing > 0 ? p.Design.Spacing : 5,
            BurdenBearingDeg = p.Header.BlastOrientation,
            BurdenDelayMs = p.Electronic.RowIntervalMs,
            SpacingDelayMs = p.Electronic.HoleIntervalMs
        };
        op.Position = at;
        var keepManual = true;
        var result = new StackPanel { Spacing = 5 };
        var relief = new TextBlock { FontFamily = Look.Mono, Foreground = Look.Text, Name = "totalRelief", VerticalAlignment = VerticalAlignment.Center };

        void Recompute()
        {
            relief.Text = N(op.TotalReliefMsPerM, "0.0") + " " + L("ms/m");
            var (times, r) = EbsOpening.Plan(p, holes, op, keepManual);
            result.Children.Clear();
            result.Children.Add(times.Count == 0 ? Line(Warn, L("No hole gets a time from this point"))
                : Line(Ok, Loc.F("{0} holes get times: {1} – {2} ms", times.Count, N(times.Values.Min(), "0"), N(times.Values.Max(), "0"))));
            if (r.SkippedManual > 0) result.Children.Add(Line(Look.Muted, Loc.F("{0} holes with a time set by hand keep it", r.SkippedManual)));
            if (r.NotElectronic > 0) result.Children.Add(Line(Look.Muted, Loc.F("{0} holes without an electronic detonator fire by their tie-up", r.NotElectronic)));
            if (r.MaxTimeExceeded.Count > 0) result.Children.Add(Line(Warn, Loc.F("{0} holes would pass the maximum delay {1} ms and stay as they are", r.MaxTimeExceeded.Count, N(p.Electronic.MaxTimeMs, "0"))));
        }

        TextBox Number(string name, double value, string unit, Action<double> set, double min = 0)
        {
            var box = FormKit.Text(N(value));
            box.Name = name;
            box.FontFamily = Look.Mono;
            box.InnerRightContent = new TextBlock { Text = L(unit), FontSize = 11.5, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            box.TextChanged += (_, _) =>
            {
                if (!double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min) return;
                set(v);
                Recompute();
            };
            return box;
        }

        var open = new CheckBox { Content = L("Open the segment at this point"), IsChecked = op.OpenHere, Name = "openHere" };
        open.IsCheckedChanged += (_, _) => { op.OpenHere = open.IsChecked == true; Recompute(); };
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(FormKit.Fields(2,
            FormKit.Labeled(L("Burden"), Number("burden", op.BurdenM, "m", v => op.BurdenM = v, 0.01)),
            FormKit.Labeled(L("Spacing"), Number("spacing", op.SpacingM, "m", v => op.SpacingM = v, 0.01)),
            FormKit.Labeled(L("Delay along the burden"), Number("burdenDelay", op.BurdenDelayMs, "ms", v => op.BurdenDelayMs = v)),
            FormKit.Labeled(L("Delay along the spacing"), Number("spacingDelay", op.SpacingDelayMs, "ms", v => op.SpacingDelayMs = v)),
            FormKit.Labeled(L("Burden bearing"), Number("bearing", op.BurdenBearingDeg, "°", v => op.BurdenBearingDeg = v, -360)),
            FormKit.Labeled(L("Time of the first hole"), Number("start", op.StartOffsetMs, "ms", v => op.StartOffsetMs = v))));
        body.Children.Add(open);
        body.Children.Add(Ui.Row(8, new TextBlock { Text = L("Overall relief"), Foreground = Look.Muted, VerticalAlignment = VerticalAlignment.Center }, relief));
        if (holes.Any(h => h.ElectronicTimeManual && h.ElectronicTimeMs.HasValue && Charging.HasElectronicDetonator(p, h)))
        {
            var manual = new CheckBox { Content = L("Apply to holes with a manual time"), Name = "applyToManual" };
            manual.IsCheckedChanged += (_, _) => { keepManual = manual.IsChecked != true; Recompute(); };
            body.Children.Add(new StackPanel { Spacing = 4, Children = { Line(Warn, L("There are holes with a manual time")), manual } });
        }
        body.Children.Add(new Border
        {
            Background = Look.Panel, BorderBrush = Look.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12),
            Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = L("Result").ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, Foreground = Look.Faint }, result } }
        });
        Recompute();
        var title = existing != null ? "Edit EBS opening point" : "EBS opening point";
        if (!await Dialogs.Custom(title, body, width: 560, subtitle: Loc.F("Holes: {0}", holes.Count), form: true)) return null;
        return new OpeningPointSetup(op, keepManual);
    }

    private static Control Line(IBrush colour, string text) => Ui.Row(8,
        new Ellipse { Width = 8, Height = 8, Fill = colour, VerticalAlignment = VerticalAlignment.Center },
        new TextBlock { Text = text, Foreground = Look.TextSoft, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
}
