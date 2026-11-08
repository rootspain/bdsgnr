using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Calc;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.Views;

/// <summary>
/// «Объяснить значение»: как получилось значение свойства скважины — формула по-русски, она же с числами и единицами,
/// итог; свойства, из которых оно сложено, — следующими шагами.
/// </summary>
public static class CalcExplainWindow
{
    public static Task Show(MainViewModel vm, Hole hole, string? propertyId = null)
    {
        var p = vm.Project!;
        var props = CalcService.Calculated(p, "hole");
        var choice = new ComboBox
        {
            ItemsSource = props.Select(d => CalcService.Caption(p, d.Id)).ToList(), MinWidth = 320, Name = "explainProperty",
            SelectedIndex = Math.Max(0, props.ToList().FindIndex(d => d.Id == (propertyId ?? p.View.CalcColumns?.FirstOrDefault() ?? "hole.chargeMass")))
        };
        var steps = new StackPanel { Spacing = 10, Name = "explainSteps" };
        void Fill()
        {
            steps.Children.Clear();
            if (choice.SelectedIndex < 0) return;
            var session = CalcService.SessionFor(p, "hole", hole);
            foreach (var s in CalcExplain.Explain(session, props[choice.SelectedIndex].Id, hole))
                steps.Children.Add(Step(s));
        }
        choice.SelectionChanged += (_, _) => Fill();
        Fill();
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                FormKit.Labeled(Loc.T("Property to explain"), choice),
                new ScrollViewer { Content = steps, MaxHeight = 520 }
            }
        };
        return Dialogs.Custom(Loc.T("Explain value") + " — " + Loc.T("Hole") + " " + hole.Label, content, width: 760);
    }

    private static Control Step(ExplainStep s)
    {
        var body = new StackPanel { Spacing = 3, Margin = new Thickness(s.Depth * 22, 0, 0, 0) };
        body.Children.Add(new TextBlock { Text = $"{s.Caption} = {s.Result}", FontWeight = FontWeight.SemiBold, Foreground = s.Depth == 0 ? Look.Accent : Look.Text, FontSize = s.Depth == 0 ? 14 : 12.5 });
        if (s.Condition != null) body.Children.Add(Line(Loc.T("Condition") + ": " + s.Condition, Look.Muted));
        if (s.Formula.Length > 0)
        {
            body.Children.Add(Line("= " + s.Formula, Look.TextSoft));
            body.Children.Add(Line("= " + s.Substituted, Look.Blue));
            body.Children.Add(Line("= " + s.Result, Look.Text));
        }
        return new Border { BorderBrush = Look.Line, BorderThickness = new Thickness(s.Depth == 0 ? 0 : 1, 0, 0, 0), Padding = new Thickness(s.Depth == 0 ? 0 : 10, 0, 0, 0), Child = body };
    }

    private static TextBlock Line(string text, IBrush brush) =>
        new() { Text = text, Foreground = brush, FontFamily = Look.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap };
}
