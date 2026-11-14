namespace BlastDesigner.Domain.Model;

/// <summary>
/// A line with a weight and a type of its own, as AutoCAD has them, so a drawing comes in and goes out with them: polylines
/// and polygons (lines, arcs and splines come in as polylines) and circles. Null is "by layer".
/// </summary>
public interface ILineStyled
{
    /// <summary>Weight in hundredths of a millimetre, as AutoCAD (25 = 0.25 mm); null: the layer's.</summary>
    int? LineWeight { get; set; }
    /// <summary>Name of the line type (DASHED, CENTER…); null: the layer's.</summary>
    string? LineType { get; set; }
    /// <summary>Scale of the dashes of this line, on top of the scale of the project.</summary>
    double LineTypeScale { get; set; }
}

/// <summary>A line type: dashes and gaps in drawing units, as the LTYPE table of AutoCAD (a dash positive, a gap negative, a dot 0).</summary>
public sealed class LineTypeDef
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<double> Pattern { get; set; } = new();
}

/// <summary>The line types and weights of AutoCAD, and how they are drawn on the screen.</summary>
public static class LineTypes
{
    public const string Continuous = "CONTINUOUS";

    /// <summary>The weights AutoCAD offers, hundredths of a millimetre.</summary>
    public static readonly int[] Weights = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

    /// <summary>The usual types of acad.lin, for projects that have none of their own by those names.</summary>
    public static readonly IReadOnlyList<LineTypeDef> Standard = new[]
    {
        Def("DASHED", "Dashed __ __ __ __", 0.5, -0.25),
        Def("HIDDEN", "Hidden _ _ _ _ _ _", 0.25, -0.125),
        Def("CENTER", "Center ____ _ ____ _", 1.25, -0.25, 0.25, -0.25),
        Def("PHANTOM", "Phantom ____ _ _ ____", 1.25, -0.25, 0.25, -0.25, 0.25, -0.25),
        Def("DOT", "Dot . . . . . . . .", 0, -0.25),
        Def("DASHDOT", "Dash dot __ . __ . __", 0.5, -0.25, 0, -0.25),
        Def("BORDER", "Border __ __ . __ __ .", 0.5, -0.25, 0.5, -0.25, 0, -0.25),
        Def("DIVIDE", "Divide __ . . __ . .", 0.5, -0.25, 0, -0.25, 0, -0.25)
    };

    private static LineTypeDef Def(string name, string description, params double[] pattern) => new() { Name = name, Description = description, Pattern = pattern.ToList() };

    /// <summary>Solid: no name, continuous, by layer or by block.</summary>
    public static bool IsSolid(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals(Continuous, StringComparison.OrdinalIgnoreCase)
        || name.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase) || name.Equals("BYBLOCK", StringComparison.OrdinalIgnoreCase);

    /// <summary>The type of a name: the project's own first, then the standard one; null for solid or unknown.</summary>
    public static LineTypeDef? Find(BlastProject project, string? name)
    {
        if (IsSolid(name)) return null;
        return project.LineTypes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Standard.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>All the type names a line can take in a project: its own and the standard ones, by name.</summary>
    public static IEnumerable<string> Names(BlastProject project) =>
        new[] { Continuous }.Concat(project.LineTypes.Select(t => t.Name)).Concat(Standard.Select(t => t.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>A weight on the screen as AutoCAD shows it: 0.25 mm is one pixel, thinner too, thicker in proportion.</summary>
    public static float WeightPixels(int weight) => Math.Max(1f, weight / 25f);

    /// <summary>The thinnest a weight is printed, hundredths of a millimetre: a weight of 0 is a hairline, as AutoCAD plots it.</summary>
    public const int ThinnestPrinted = 5;

    /// <summary>A weight on a printed sheet, in the units of the sheet: its millimetres, no thinner than <see cref="ThinnestPrinted"/>.</summary>
    public static float WeightOnSheet(int weight, double unitsPerMm) => (float)(Math.Max(weight, ThinnestPrinted) / 100.0 * unitsPerMm);

    /// <summary>A weight as written: "0.25 mm".</summary>
    public static string WeightText(int weight) => (weight / 100.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + " mm";

    /// <summary>
    /// The dashes and gaps of a type on the screen, in pixels, alternating from a dash, for a dash path effect; null when the
    /// line is drawn solid (no type, or the pattern too fine to see at this scale, as AutoCAD does).
    /// </summary>
    public static float[]? ScreenPattern(LineTypeDef? type, double pixelsPerUnit)
    {
        if (type == null || type.Pattern.Count == 0 || pixelsPerUnit <= 0) return null;
        // dashes and gaps in turn: two of a kind next to each other are one
        var runs = new List<(bool Dash, double Length)>();
        foreach (var v in type.Pattern)
        {
            var dash = v >= 0;
            // a dot is a short dash, still to be seen
            var len = dash ? Math.Max(v * pixelsPerUnit, 1) : -v * pixelsPerUnit;
            if (runs.Count > 0 && runs[^1].Dash == dash) runs[^1] = (dash, runs[^1].Length + len);
            else runs.Add((dash, len));
        }
        if (runs.Count > 1 && runs[0].Dash == runs[^1].Dash)
        {
            // the pattern repeats: its last run goes on into its first
            runs[0] = (runs[0].Dash, runs[0].Length + runs[^1].Length);
            runs.RemoveAt(runs.Count - 1);
        }
        if (runs.Count < 2) return null;
        if (!runs[0].Dash) { runs.Add(runs[0]); runs.RemoveAt(0); }
        var total = runs.Sum(r => r.Length);
        if (total < 4) return null;
        return runs.Select(r => (float)r.Length).ToArray();
    }
}
