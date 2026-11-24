using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Model;
using BlastDesigner.IO;

namespace BlastDesigner.App.Views;

/// <summary>
/// The stage buttons over the plan, after the pinned chips: a frame "DRILLING [Plan|Fact]" and one "LOADING [Plan|Fact]".
/// A click switches a button on alone (off when it is on), Ctrl+click only it, a right click opens its settings.
/// </summary>
public static class StageButtons
{
    /// <summary>
    /// The separator and the two frames, to go into the row of chips; <paramref name="refresh"/> builds the chips again.
    /// A frame has the see-through ground of the chips over the plan; its name folds it to the name alone and back.
    /// </summary>
    public static IEnumerable<Control> Build(MainViewModel vm, Action refresh)
    {
        yield return new Border { Width = 1, Height = 18, Background = Look.LineStrong, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var stage in StageViews.Stages)
        {
            var views = StageViews.Modes.Select(m => vm.Project is { } p ? StageViews.Get(p, stage, m) : null).ToList();
            var onView = views.FirstOrDefault(v => v?.On == true);
            var folded = vm.IsStageFolded(stage);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            row.Children.Add(NameButton(vm, stage, onView, folded, refresh));
            if (!folded)
                for (int i = 0; i < StageViews.Modes.Length; i++)
                    row.Children.Add(Button(vm, views[i], stage, StageViews.Modes[i], refresh));
            yield return new Border
            {
                Name = "stage" + stage,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                // a group with a view on: its outline a little of the accent, seen folded too
                BorderBrush = onView != null ? AccentLine() : Look.Line,
                Background = Look.ChipGround,
                Padding = new Thickness(2),
                Child = row
            };
        }
    }

    /// <summary>The outline of a group with a view on: the accent at a little over half.</summary>
    private static IBrush AccentLine() => new SolidColorBrush(Look.Accent.Color, 0.55);

    /// <summary>
    /// The name of a group: a flat button with a down arrow (open) or a right one (folded); a click folds the frame to it
    /// or opens it again. Folded with a view on, it says which: "● DRILLING · Fact".
    /// </summary>
    private static Button NameButton(MainViewModel vm, WorkStage stage, StageView? onView, bool folded, Action refresh)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new TextBlock { Text = folded ? "▸" : "▾", FontSize = 12, Width = 9, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center });
        if (folded && onView != null)
            content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Look.Accent, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = MainViewModel.StageWord(stage.ToString()).ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Look.Faint, VerticalAlignment = VerticalAlignment.Center
        });
        if (folded && onView != null)
            content.Children.Add(new TextBlock { Text = "· " + MainViewModel.StageWord(onView.Mode.ToString()), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Look.Accent, VerticalAlignment = VerticalAlignment.Center, Name = "stageFoldedMode" });
        var b = new Button
        {
            Name = "stageName" + stage,
            Content = content,
            Height = 22,
            Padding = new Thickness(8, 0, folded ? 8 : 6, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = vm.Project != null
        };
        b.Classes.Add("flat");
        b.Classes.Add("stageName");
        ToolTip.SetTip(b, Loc.Ui(folded ? "Show the plan and fact buttons of this stage" : "Fold the group to its name (F8 and Shift+F8 still switch it)"));
        b.Click += (_, _) =>
        {
            UsageLog.Track(UsageLog.Button, (folded ? "Unfold " : "Fold ") + stage, "chip");
            Ui.Safe(() => vm.ToggleStageFold(stage));
            refresh();
        };
        return b;
    }

    /// <summary>
    /// A plan or fact button: on, as a chip on (the soft accent, its outline and text in the accent); off, bare text with
    /// no outline. Under the mouse as the chips (App.axaml: Button.chip).
    /// </summary>
    private static Button Button(MainViewModel vm, StageView? view, WorkStage stage, StageMode mode, Action refresh)
    {
        var on = view?.On == true;
        var b = new Button
        {
            Name = $"stage{stage}{mode}",
            Content = MainViewModel.StageWord(mode.ToString()),
            Height = 22,
            Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
            IsEnabled = view != null
        };
        b.Classes.Add("flat");
        Ui.SetChip(b, on);
        if (!on)
        {
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
        }
        if (view == null) return b;
        ToolTip.SetTip(b, Tip(vm, view));
        var ctrl = false;
        b.AddHandler(InputElement.PointerPressedEvent, (_, e) => ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), RoutingStrategies.Tunnel);
        b.Click += (_, _) =>
        {
            UsageLog.Track(UsageLog.Button, view.Name + (ctrl ? " (Ctrl)" : ""), "chip");
            Ui.Safe(() => vm.SwitchStage(view, ctrl));
            ctrl = false;
            refresh();
        };
        b.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            e.Handled = true;
            Settings(vm, view, refresh).ShowAt(b);
        }, RoutingStrategies.Tunnel);
        return b;
    }

    /// <summary>The tip of a button: what a click, Ctrl+click and a right click do, and its layers.</summary>
    public static string Tip(MainViewModel vm, StageView view)
    {
        var p = vm.Project!;
        var lines = new List<string>
        {
            MainViewModel.StageName(view),
            Loc.Ui("Click: show only this stage view (click again to switch it off)"),
            Loc.Ui("Ctrl+click: switch this one on or off, keeping the others (the other view of the same stage goes off)"),
            Loc.Ui("Right click: its layers, hole states and hole data")
        };
        var layers = p.Layers.Where(l => view.Layers.Contains(l.Id)).Select(l => "• " + Loc.Name(l.Name)).ToList();
        lines.Add("");
        lines.Add(layers.Count == 0 ? Loc.Ui("No layers of its own") : Loc.Ui("Layers:"));
        lines.AddRange(layers);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The settings of a button: its layers with the number of objects (a layer of another button signed with its name),
    /// the six hole states, the hole data (design or actual) and Done.
    /// </summary>
    public static Flyout Settings(MainViewModel vm, StageView view, Action refresh)
    {
        var p = vm.Project!;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var panel = new StackPanel { Spacing = 6, Width = 300 };
        panel.Children.Add(new TextBlock { Text = MainViewModel.StageName(view), FontWeight = FontWeight.SemiBold, FontSize = 13 });

        panel.Children.Add(Caption("Layers"));
        var layers = new StackPanel { Spacing = 1 };
        foreach (var layer in p.Layers)
        {
            var id = layer.Id;
            var count = p.Entities.Count(e => e.LayerId == id);
            var others = StageViews.Owners(p, id).Where(v => v != view).Select(MainViewModel.StageName).ToList();
            var text = $"{Loc.Name(layer.Name)} ({count})" + (others.Count > 0 ? "  — " + string.Join(", ", others) : "");
            var box = new CheckBox { Content = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, IsChecked = view.Layers.Contains(id), Tag = "layer" };
            box.IsCheckedChanged += (_, _) => Ui.Safe(() => vm.SetStageLayer(view, id, box.IsChecked == true));
            layers.Children.Add(box);
        }
        panel.Children.Add(new ScrollViewer { Content = layers, MaxHeight = 260 });

        panel.Children.Add(Caption("Hole states"));
        var states = new WrapPanel();
        foreach (var state in Enum.GetValues<HoleState>())
        {
            var s = state;
            var box = new CheckBox { Content = Loc.Enum(s), IsChecked = view.States.Contains(s), MinWidth = 140, Tag = "state" };
            box.IsCheckedChanged += (_, _) => Ui.Safe(() => vm.SetStageState(view, s, box.IsChecked == true));
            states.Children.Add(box);
        }
        panel.Children.Add(states);

        panel.Children.Add(Caption("Hole data"));
        var design = new RadioButton { Content = MainViewModel.StageWord("Design"), GroupName = "basis" + view.Stage + view.Mode, IsChecked = view.HoleBasis == HoleBasis.Design, Margin = new Thickness(0, 0, 12, 0) };
        var actual = new RadioButton { Content = MainViewModel.StageWord("Actual"), GroupName = "basis" + view.Stage + view.Mode, IsChecked = view.HoleBasis == HoleBasis.Actual };
        design.IsCheckedChanged += (_, _) => { if (design.IsChecked == true) Ui.Safe(() => vm.SetStageBasis(view, HoleBasis.Design)); };
        actual.IsCheckedChanged += (_, _) => { if (actual.IsChecked == true) Ui.Safe(() => vm.SetStageBasis(view, HoleBasis.Actual)); };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { design, actual } });

        var done = new Button { Content = Loc.Ui("Done"), MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "primary" } };
        done.Click += (_, _) => flyout.Hide();
        panel.Children.Add(done);
        flyout.Content = panel;
        // what the settings changed shows in the chips, the layers and on the plan once they close
        flyout.Closed += (_, _) => { vm.StagesEdited(); refresh(); };
        return flyout;
    }

    private static TextBlock Caption(string english) =>
        new() { Text = Loc.Ui(english), FontSize = 11, Foreground = Look.Faint, Margin = new Thickness(0, 4, 0, 0) };
}
