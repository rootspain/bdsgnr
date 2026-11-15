using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BlastDesigner.App.Services;

namespace BlastDesigner.App.Views;

/// <summary>
/// The full colour picker (spectrum, hue, hex) in a flyout under a swatch, with Cancel and OK: OK gives the colour picked,
/// Cancel, Esc and a click outside leave the colour as it was.
/// </summary>
public static class ColourFlyout
{
    /// <param name="current">The colour the picker opens with (ARGB).</param>
    /// <param name="ok">Gets the colour picked (opaque ARGB) when OK is pressed, after the flyout has closed.</param>
    public static Flyout Make(Func<uint> current, Action<uint> ok)
    {
        var view = new ColorView
        {
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            IsAccentColorsVisible = false,
            IsColorPaletteVisible = false,
            Width = 300
        };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var cancel = new Button { Content = Loc.T("Cancel"), Tag = "cancel", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var accept = new Button { Content = Loc.T("OK"), Tag = "ok", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "primary" } };
        cancel.Click += (_, _) => flyout.Hide();
        accept.Click += (_, _) =>
        {
            var c = view.Color.ToUInt32() | 0xFF000000;
            // closed first: what OK does may build the page anew
            flyout.Hide();
            ok(c);
        };
        flyout.Opening += (_, _) => view.Color = Color.FromUInt32(current() | 0xFF000000);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, accept } };
        flyout.Content = new StackPanel { Spacing = 10, Children = { view, buttons } };
        return flyout;
    }

    /// <summary>The colour view of a flyout made by <see cref="Make"/>.</summary>
    public static ColorView? ViewOf(Flyout flyout) => (flyout.Content as StackPanel)?.Children.OfType<ColorView>().FirstOrDefault();
}
