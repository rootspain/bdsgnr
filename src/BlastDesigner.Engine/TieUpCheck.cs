using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// The loaded holes the tie-up leaves out (the Unconnected tab of the Blast analysis window). A hole is checked when it has
/// a charge of explosive (with <c>primedOnly</c>, a primer too); dummy holes never. It is not connected:
/// <list type="bullet">
/// <item>with a shock tube (non-electronic) detonator, when the surface signal of no initiation point reaches it;</item>
/// <item>with an electronic detonator, when no tie comes to it and no initiation point stands on it (its programmed time
/// alone does not tie it to the blasting machine);</item>
/// <item>with both, when either of the two is not connected;</item>
/// <item>without a primer (<c>primedOnly</c> off), when no tie comes to it and no signal reaches it.</item>
/// </list>
/// </summary>
public static class TieUpCheck
{
    /// <param name="Holes">The holes not connected, in the order of the project.</param>
    /// <param name="Checked">How many holes were checked (loaded, primed with <c>primedOnly</c>).</param>
    /// <param name="ShockTube">Of <paramref name="Holes"/>, those whose shock tube detonator no signal reaches.</param>
    /// <param name="Electronic">Of <paramref name="Holes"/>, those whose electronic detonator has no tie.</param>
    public sealed record Result(List<Hole> Holes, int Checked, int ShockTube, int Electronic);

    /// <summary>The loaded holes not connected; solves the times first (the surface signal is part of the check).</summary>
    public static Result Unconnected(BlastProject project, bool primedOnly)
    {
        Timing.Solve(project);
        var tied = new HashSet<Guid>();
        foreach (var t in project.Ties) { tied.Add(t.FromId); tied.Add(t.ToId); }
        foreach (var ip in project.Entities.OfType<InitiationPoint>())
            if (ip.HoleId is Guid on) tied.Add(on);
        var holes = new List<Hole>();
        int count = 0, tube = 0, electronic = 0;
        foreach (var h in project.Holes)
        {
            if (h.IsDummy || Charging.ExplosiveRuns(project, h).Count == 0) continue;
            var primers = Charging.Primers(h).Select(x => x.Primer).ToList();
            if (primedOnly && primers.Count == 0) continue;
            count++;
            var reached = h.SurfaceTimeMs.HasValue;
            var hasTie = tied.Contains(h.Id);
            var hasElectronic = primers.Any(p => Charging.IsElectronic(project, p));
            var hasTube = primers.Any(p => !Charging.IsElectronic(project, p));
            var tubeOff = hasTube && !reached;
            var electronicOff = hasElectronic && !hasTie;
            var bareOff = primers.Count == 0 && !hasTie && !reached;
            if (!tubeOff && !electronicOff && !bareOff) continue;
            holes.Add(h);
            if (tubeOff) tube++;
            if (electronicOff) electronic++;
        }
        return new Result(holes, count, tube, electronic);
    }
}
