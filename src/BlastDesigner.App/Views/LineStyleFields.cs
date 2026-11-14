using BlastDesigner.App.Services;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.App.Views;

/// <summary>The weight and type of a line (or of a layer) chosen from the lists of AutoCAD in a property grid.</summary>
public static class LineStyleFields
{
    /// <param name="layer">For a layer: no weight is its line width in pixels, no type continuous. For a line: both by layer.</param>
    public static void Apply(PropertyGrid grid, BlastProject project, bool layer = false)
    {
        grid.Choices[nameof(ILineStyled.LineWeight)] = LineTypes.Weights.Select(w => (w.ToString(System.Globalization.CultureInfo.InvariantCulture), WeightName(w))).ToList();
        grid.Choices[nameof(ILineStyled.LineType)] = LineTypes.Names(project).Where(n => !LineTypes.IsSolid(n)).Select(n => (n, n)).ToList();
        grid.NoneLabels[nameof(ILineStyled.LineWeight)] = layer ? "Line width in pixels" : "By layer";
        grid.NoneLabels[nameof(ILineStyled.LineType)] = layer ? "Continuous" : "By layer";
        grid.Tips[nameof(ILineStyled.LineWeight)] = Loc.Ui("As in AutoCAD: 0.25 mm is a pixel on the screen, thinner too, thicker in proportion");
        grid.Tips[nameof(ILineStyled.LineType)] = Loc.Ui("Dashes as in AutoCAD, in drawing units: they grow as the plan is zoomed in");
        grid.Tips[nameof(ILineStyled.LineTypeScale)] = Loc.Ui("The dashes of this line longer (above 1) or shorter (below 1)");
    }

    private static string WeightName(int w) => (w / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " " + Loc.Ui("mm");
}
