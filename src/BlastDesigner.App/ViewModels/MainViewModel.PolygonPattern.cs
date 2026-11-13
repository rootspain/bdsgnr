using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

/// <summary>
/// Polygons made into patterns of holes: the first row along a side of each, the holes of a row aligned to the left, the
/// middle or the right; the pattern keeps with its polygon afterwards (<see cref="PatternOutlines"/>).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The selected closed polygons.</summary>
    public List<PolylineEntity> SelectedPolygons() =>
        Doc?.SelectedEntities.OfType<PolylineEntity>().Where(p => p.Closed && p.Points.Count >= 3).ToList() ?? new();

    /// <summary>Opens the Pattern from polygon tool on the selected closed polygons, each with its first row along its longest side.</summary>
    public void PolygonPatternTool()
    {
        var polygons = SelectedPolygons();
        if (polygons.Count == 0) { _ = Dialogs.Info(Loc.T("Select a closed polygon first.")); return; }
        SetTool(new PolygonPatternTool(this, polygons.Select(p => p.Id).ToList()));
    }

    /// <summary>The pattern of the panel for a polygon with its first row along side <paramref name="edge"/>.</summary>
    private PatternParameters PolygonPatternSetup(int edge, int startNumber)
    {
        var d = Project!.Design;
        return new PatternParameters
        {
            Burden = d.Burden, Spacing = d.Spacing, Pattern = d.Pattern, Orientation = Project.Header.BlastOrientation,
            EdgeOffset = PatternEdgeOffset, LabelPrefix = PatternLabelPrefix, StartNumber = startNumber,
            RotateOrientation = PatternRotateOrientation, FaceEdge = edge, Alignment = PatternAlignment
        };
    }

    /// <summary>The holes the polygons would be made into, numbered on from one to the next; nothing is changed.</summary>
    public List<Hole> PreviewPolygonPatterns(IReadOnlyList<(Guid Polygon, int Edge)> plan)
    {
        var holes = new List<Hole>();
        if (Project == null) return holes;
        var hp = NewHoleParameters();
        int number = NextPatternNumber;
        foreach (var (id, edge) in plan)
        {
            if (Project.FindEntity(id) is not PolylineEntity { Closed: true, Points.Count: >= 3 } poly) continue;
            try
            {
                var made = HoleDesign.FillPolygon(poly.Points, PolygonPatternSetup(edge, number), hp, out _, out _);
                holes.AddRange(made);
                number += made.Count;
            }
            catch (InvalidOperationException) { /* a grid too dense for its outline shows nothing */ }
        }
        return holes;
    }

    /// <summary>
    /// Makes the polygons into patterns in one undo step: a pattern each, the holes numbered on from one to the next. The
    /// polygons stay as the outlines of their patterns. The new holes are selected and the Select tool takes over.
    /// </summary>
    public void MakePolygonPatterns(IReadOnlyList<(Guid Polygon, int Edge)> plan)
    {
        if (Project == null || plan.Count == 0) return;
        var hp = NewHoleParameters();
        var toSurface = PatternToSurface;
        var toLevel = PatternToLevel;
        int count = 0, patterns = 0, unlevelled = 0;
        var made = new List<Guid>();
        try
        {
            Change("Pattern from polygon", p =>
            {
                int number = NextPatternNumber;
                foreach (var (id, edge) in plan)
                {
                    if (p.FindEntity(id) is not PolylineEntity { Closed: true, Points.Count: >= 3 } poly) continue;
                    var holes = HoleDesign.FillPolygon(poly.Points, PolygonPatternSetup(edge, number), hp, out var rows, out var pattern);
                    if (holes.Count == 0) continue;
                    foreach (var h in holes)
                    {
                        p.Add(h, HoleLayer);
                        if (!toSurface && toLevel == null) continue;
                        // a hole whose collar is not above its level keeps the design length
                        if ((toSurface ? HoleLevel.SurfaceLevel(p, h) : toLevel) is double z && HoleLevel.Reaches(h, z)) h.ToLevel = z;
                        else unlevelled++;
                    }
                    int r0 = p.Rows.Count;
                    foreach (var r in rows) r.Name = $"Row {++r0}";
                    p.Rows.AddRange(rows);
                    pattern.Name = Loc.F("Pattern {0}", p.Patterns.Count + 1);
                    pattern.OutlineEntityId = id;
                    pattern.ToLevel = toLevel;
                    pattern.ToSurface = toSurface;
                    p.Patterns.Add(pattern);
                    number += holes.Count;
                    count += holes.Count;
                    patterns++;
                    made.AddRange(holes.Select(h => h.Id));
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            // a grid too dense for its outline
            _ = Dialogs.Info(Loc.T(ex.Message), Loc.T("Pattern from polygon"));
            return;
        }
        PatternStartNumber = null;
        SelectTool();
        if (made.Count > 0) Doc!.Select(made, record: false);
        SetStatus(Loc.F("{0} holes in {1} patterns", count, patterns) + (unlevelled == 0 ? "" : "; " + (toSurface
            ? Loc.F("{0} holes meet no surface and keep their length", unlevelled)
            : Loc.F("{0} holes have the collar not above the level and keep their length", unlevelled))));
    }
}
