using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>A turn of a pattern grid with the mouse: the angle the cursor gives, in steps, and what the turn would do.</summary>
public static class PatternTurn
{
    /// <summary>The shortest turn from one bearing to another, −180..180.</summary>
    public static double Turn(double from, double to)
    {
        var d = GeoMath.NormalizeBearing(to - from);
        return d > 180 ? d - 360 : d;
    }

    /// <summary>Step of a turn with the mouse: 1°, with Shift 5°, with Ctrl 0.1°.</summary>
    public static double Step(bool shift, bool control) => control ? 0.1 : shift ? 5 : 1;

    /// <summary>The turn from the bearing of the press to the bearing of the cursor, rounded to <paramref name="step"/>.</summary>
    public static double Snapped(double startBearing, double bearing, double step) => Math.Round(Turn(startBearing, bearing) / step) * step;

    /// <summary>What turning the grid by <paramref name="delta"/> degrees would do to its holes, the pattern left as it is.</summary>
    public static PatternPlan Plan(BlastProject p, HolePattern pat, double delta)
    {
        var copy = ProjectJson.DeepClone(pat);
        copy.Azimuth = GeoMath.NormalizeBearing(copy.Azimuth + delta);
        return HoleDesign.PlanPattern(p, copy);
    }
}
