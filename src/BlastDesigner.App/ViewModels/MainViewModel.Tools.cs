using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.App.ViewModels;

public sealed partial class MainViewModel
{
    internal List<Hole> SelectedOrAllHoles() => Doc!.SelectedHoles.Any() ? Doc.SelectedHoles.ToList() : Project!.Holes.ToList();
    private HashSet<Guid> SelectedHoleIds() => Doc!.SelectedHoles.Select(h => h.Id).ToHashSet();

    /// <summary>
    /// "All / Only selected" choice used by group operations: with holes selected the user picks the scope,
    /// otherwise the operation applies to every hole. Returns null when cancelled.
    /// </summary>
    private async Task<HashSet<Guid>?> AskHoles(string title)
    {
        var selected = SelectedHoleIds();
        if (selected.Count == 0) return Project!.Holes.Select(h => h.Id).ToHashSet();
        var choice = await Dialogs.Show(title, Loc.F("Apply to the {0} selected holes or to all {1} holes?", selected.Count, Project!.Holes.Count()), new[] { "Only selected", "All", "Cancel" });
        return choice switch
        {
            0 => selected,
            1 => Project.Holes.Select(h => h.Id).ToHashSet(),
            _ => null
        };
    }
    private List<Hole> HolesById(BlastProject p, HashSet<Guid> ids) => p.Holes.Where(h => ids.Contains(h.Id)).ToList();

    /// <summary>
    /// Properties ▸ To the surface: each selected hole takes as its level the elevation where its line from the collar meets
    /// the first surface, and its length follows that level with the subdrill (<see cref="HoleLevel.Follow"/>). A hole that
    /// meets no surface keeps its level and length. One undo step.
    /// </summary>
    public async Task HolesToSurface()
    {
        if (Doc == null) return;
        var ids = Doc.SelectedEntities.OfType<Hole>().Select(h => h.Id).ToHashSet();
        if (ids.Count == 0) return;
        if (!Project!.Entities.OfType<SurfaceEntity>().Any()) { await Dialogs.Info(Loc.T("The project has no surface to drill the holes down to.")); return; }
        int done = 0, missed = 0;
        Change("Holes to the surface", p =>
        {
            foreach (var h in HolesById(p, ids))
            {
                if (HoleLevel.SurfaceLevel(p, h) is not double z || !HoleLevel.Reaches(h, z)) { missed++; continue; }
                h.ToLevel = z;
                done++;
            }
        });
        SetStatus(Loc.F("{0} holes down to the surface", done) + (missed > 0 ? "; " + Loc.F("{0} holes meet no surface and keep their length", missed) : ""));
        if (missed > 0 && done == 0) await Dialogs.Info(Loc.F("{0} holes meet no surface and keep their length", missed));
    }

    private async Task<bool> RequireSelection<T>(string what)
    {
        if (Doc?.SelectedEntities.OfType<T>().Any() == true) return true;
        await Dialogs.Info(Loc.F("Select {0} first.", Loc.T(what)));
        return false;
    }

    private static string? DistLabel(IReadOnlyList<Vec3> pts, Vec3 h)
    {
        // nothing to measure before the first point (an empty label left its bare background by the cursor)
        if (pts.Count == 0) return null;
        var last = pts[^1];
        var total = GeoMath.Length(pts) + Vec3.Distance(last, h);
        return $"{Vec3.Distance2D(last, h):0.00} m  {GeoMath.Bearing(last, h):0.0}°  Σ {total:0.00} m";
    }

    // ---- drawing tools -----------------------------------------------------------------

    public void DrawPolylineTool(bool closed, LineKind kind = LineKind.General) =>
        SetTool(new PickPointsTool(this, closed ? "Polygon" : "Polyline", "Click points; a click on the last point, a double-click or Enter finishes, Backspace removes the last point.", closed ? 3 : 2, int.MaxValue, closed, pts =>
        {
            var layer = kind == LineKind.Boundary ? Project!.GetOrCreateLayer(StandardLayers.Boundary, 0xFF16A34A, "Design") : null;
            Change(closed ? "Add polygon" : "Add polyline", p => p.Add(new PolylineEntity { Points = pts, Closed = closed, Kind = kind }, layer));
        }) { Measure = DistLabel });

    /// <summary>A section line: a polyline of one segment, so the entry ends with its second point.</summary>
    public void DrawSectionLineTool() =>
        SetTool(new PickPointsTool(this, "Section line", "Click two points of the section line.", 2, 2, false, pts =>
            Change("Add section line", p => p.Add(new PolylineEntity { Points = pts, Kind = LineKind.CrossSection }))) { Measure = DistLabel });

    /// <summary>
    /// Name of the point tool of a kind: the English name the toolbars and the rail know it by (so its button lights up
    /// while it is on), translated for the tool panel like every tool name.
    /// </summary>
    public static string PointToolName(PointKind kind) => kind switch
    {
        PointKind.General => "Point",
        PointKind.Survey => "Survey point",
        PointKind.GradePeg => "Grade peg",
        PointKind.Monitoring => "Monitoring point",
        PointKind.SurveyStation => "Survey station",
        PointKind.NorthPoint => "North point",
        _ => "Centre of mass"
    };

    public void DrawPointTool(PointKind kind) =>
        SetTool(new PickPointsTool(this, PointToolName(kind), "Click to place points.", 1, 1, false, async pts =>
        {
            var label = kind is PointKind.Monitoring or PointKind.SurveyStation ? await Dialogs.Prompt(Loc.Enum(kind), Loc.T("Label"), Loc.Enum(kind)) ?? "" : "";
            var layer = kind is PointKind.Survey or PointKind.SurveyStation or PointKind.GradePeg ? Project!.GetOrCreateLayer(StandardLayers.Survey, 0xFF7C3AED, StandardLayers.TopographyGroup) : null;
            Change("Add point", p => p.Add(new PointEntity { Position = pts[0], Kind = kind, Label = label }, layer));
        }));

    public void DrawTextTool() =>
        SetTool(new PickPointsTool(this, "Text", "Click where the text goes.", 1, 1, false, async pts =>
        {
            var text = await Dialogs.Prompt("Text", Loc.T("Text"));
            if (string.IsNullOrEmpty(text)) return;
            Change("Add text", p => p.Add(new TextEntity { Position = pts[0], Text = text, Height = Math.Max(0.5, 12 / View.Camera.Scale) }, p.GetOrCreateLayer(StandardLayers.Annotation)));
        }));

    public void DrawCircleTool() =>
        SetTool(new PickPointsTool(this, "Circle", "Click the centre, then a point on the circle.", 2, 2, false, pts =>
            Change("Add circle", p => p.Add(new CircleEntity { Center = pts[0], Radius = Vec3.Distance2D(pts[0], pts[1]) })))
        { Measure = (pts, h) => pts.Count == 1 ? $"R {Vec3.Distance2D(pts[0], h):0.00} m" : null });

    public void DrawArrowTool() =>
        SetTool(new PickPointsTool(this, "Arrow", "Click the start and the end of the arrow.", 2, 2, false, pts =>
            Change("Add arrow", p => p.Add(new ArrowEntity { Start = pts[0], End = pts[1] }, p.GetOrCreateLayer(StandardLayers.Annotation)))));

    public void DrawDimensionTool() =>
        SetTool(new PickPointsTool(this, "Dimension", "Click two points to dimension. Left to right: the brackets go down; right to left: up.", 2, 2, false, pts =>
            Change("Add dimension", p => p.Add(new DimensionEntity { Start = pts[0], End = pts[1], Offset = 12 / View.Camera.Scale }, p.GetOrCreateLayer(StandardLayers.Annotation))))
        { Measure = DistLabel });

    public void MeasureTool() =>
        SetTool(new PickPointsTool(this, "Measure", "Click points to measure distance, bearing and grade; Enter shows the area.", 2, int.MaxValue, false, async pts =>
        {
            var len = GeoMath.Length(pts);
            var dz = pts[^1].Z - pts[0].Z;
            var msg = Loc.F("Length {0:0.00} m, plan {1:0.00} m, ΔZ {2:0.00} m, bearing {3:0.0}°", len, GeoMath.Length(pts.Select(p => p.WithZ(0)).ToList()), dz, GeoMath.Bearing(pts[0], pts[^1]));
            if (pts.Count > 2) msg += "\n" + Loc.F("Enclosed area {0:0.00} m²", GeoMath.Area(pts));
            await Dialogs.Info(msg, Loc.T("Measure"));
        }) { Measure = DistLabel });

    public void HoleMeasureTool()
    {
        Hole? first = null;
        SetTool(new PickEntityTool<Hole>(this, "Hole measure", "Click two holes to measure between them.", async (h, _) =>
        {
            if (first == null) { first = h; SetStatus(Loc.F("From hole {0}", h.Label)); return; }
            var a = first;
            first = null;
            await Dialogs.Info($"{a.Label} → {h.Label}\n" +
                Loc.F("Collars: {0:0.00} m (plan {1:0.00} m), bearing {2:0.0}°", Vec3.Distance(a.Collar, h.Collar), Vec3.Distance2D(a.Collar, h.Collar), GeoMath.Bearing(a.Collar, h.Collar)) + "\n" +
                Loc.F("Toes: {0:0.00} m", Vec3.Distance(a.Toe, h.Toe)), Loc.T("Hole measure"));
        }));
    }

    // ---- hole design tools ----------------------------------------------------------------

    public void HoleTool(bool dummy = false)
    {
        var tool = new PickPointsTool(this, dummy ? "Dummy hole" : "Hole", "Click to add holes using the design data.", 1, 1, false, pts =>
        {
            var hp = NewHoleParameters(dummy);
            var label = NextHoleLabel();
            Change(dummy ? "Add dummy hole" : "Add hole", p =>
            {
                var h = HoleDesign.CreateHole(pts[0], hp);
                h.Label = label;
                p.Add(h, HoleLayer);
            });
        })
        {
            Extra =
            {
                MenuEntry.Item("Add holes to selected row", Run(AttachHolesToRow), () => HasSelection),
                MenuEntry.Item("Hole properties..", Run(EditDesignData))
            }
        };
        SetTool(tool);
    }

    public void RowTool() => SetTool(new RowDrawTool(this, AddRow));

    private void AddRow(IReadOnlyList<Vec3> line, double spacing)
    {
        var hp = NewHoleParameters();
        var start = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        Change("Add row", p =>
        {
            var holes = HoleDesign.AlongPolyline(line, spacing, hp, true, out var row);
            row.Name = $"Row {p.Rows.Count + 1}";
            foreach (var h in holes) { h.Label = (start++).ToString(); p.Add(h, HoleLayer); }
            p.Rows.Add(row);
        });
    }

    public void PatternTool() =>
        SetTool(new PickPointsTool(this, "Pattern", "Click the pattern boundary; double-click to finish. The pattern fills the polygon.", 3, int.MaxValue, true, async pts => await FillPolygon(pts, false))
        { Extra = { MenuEntry.Item("Set pattern properties..", Run(EditDesignData)) }, Measure = DistLabel });

    public async Task FillSelectedPolygon()
    {
        var poly = Doc?.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(p => p.Closed);
        if (poly == null) { await Dialogs.Info(Loc.T("Select a closed polygon first.")); return; }
        await FillPolygon(poly.Points, true);
    }

    /// <summary>Pattern tool panel: fields kept for the session (the project has no place for them).</summary>
    public double PatternEdgeOffset { get; set; }
    public string PatternLabelPrefix { get; set; } = string.Empty;
    /// <summary>First hole number of the next pattern; null: the next free number.</summary>
    public int? PatternStartNumber { get; set; }
    public int NextPatternNumber => PatternStartNumber ?? (int.TryParse(NextHoleLabel(), out var n) ? n : 1);

    /// <summary>
    /// Fills a polygon with the pattern. The pattern tool takes its values from its panel; filling a selected polygon,
    /// which has no panel, asks for them.
    /// </summary>
    private async Task FillPolygon(IReadOnlyList<Vec3> polygon, bool ask)
    {
        var d = Project!.Design;
        var pp = new PatternParameters
        {
            Burden = d.Burden, Spacing = d.Spacing, Pattern = d.Pattern, Orientation = Project.Header.BlastOrientation,
            EdgeOffset = PatternEdgeOffset, LabelPrefix = PatternLabelPrefix, StartNumber = NextPatternNumber
        };
        if (ask && await Dialogs.EditObject("Pattern properties", pp) is not { } edited) return;
        if (ask) { PatternEdgeOffset = pp.EdgeOffset; PatternLabelPrefix = pp.LabelPrefix; }
        var hp = NewHoleParameters();
        int count = 0;
        var made = new List<Guid>();
        Change("Pattern", p =>
        {
            var holes = HoleDesign.FillPolygon(polygon, pp, hp, out var rows);
            foreach (var h in holes) p.Add(h, HoleLayer);
            int r0 = p.Rows.Count;
            foreach (var r in rows) r.Name = $"Row {++r0}";
            p.Rows.AddRange(rows);
            p.Design.Burden = pp.Burden;
            p.Design.Spacing = pp.Spacing;
            p.Design.Pattern = pp.Pattern;
            p.Header.BlastOrientation = pp.Orientation;
            if (!p.Entities.OfType<PolylineEntity>().Any(x => x.Kind == LineKind.Boundary))
                p.Add(new PolylineEntity { Points = polygon.ToList(), Closed = true, Kind = LineKind.Boundary }, p.GetOrCreateLayer(StandardLayers.Boundary, 0xFF16A34A, "Design"));
            count = holes.Count;
            made = holes.Select(h => h.Id).ToList();
        });
        // the next pattern starts after these holes again
        PatternStartNumber = null;
        // a pattern drawn with the tool is finished: its panel goes and the Select tool takes over, with the new holes selected
        if (!ask && ActiveTool?.Name == "Pattern") SelectTool();
        else ToolChanged?.Invoke();
        // not a step of its own: one undo takes the pattern away, and the selection from before it comes back
        if (made.Count > 0) Doc!.Select(made, record: false);
        SetStatus(Loc.F("{0} holes created", count));
    }

    public async Task AddHolesAtPoints()
    {
        if (!await RequireSelection<PointEntity>("points")) return;
        var pts = Doc!.SelectedEntities.OfType<PointEntity>().Select(p => p.Position).ToList();
        var hp = NewHoleParameters();
        var start = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        Change("Add holes at points", p => { foreach (var pt in pts) { var h = HoleDesign.CreateHole(pt, hp); h.Label = (start++).ToString(); p.Add(h, HoleLayer); } });
    }

    public async Task AddHolesAlongPolyline()
    {
        if (!await RequireSelection<PolylineEntity>("a polyline")) return;
        var spacing = await Dialogs.PromptNumber("Add along selected polyline", Loc.T("Spacing (m)"), Project!.Design.Spacing);
        if (spacing is not double s || s <= 0) return;
        foreach (var pl in Doc!.SelectedEntities.OfType<PolylineEntity>().ToList())
            AddRow(pl.Closed ? pl.Points.Append(pl.Points[0]).ToList() : pl.Points, s);
    }

    public async Task MoveHolesToPoints()
    {
        var holes = Doc!.SelectedHoles.Select(h => h.Id).ToHashSet();
        var pts = Doc.SelectedEntities.OfType<PointEntity>().Select(p => p.Position).ToList();
        if (holes.Count == 0 || pts.Count == 0) { await Dialogs.Info(Loc.T("Select holes and the points to move them to.")); return; }
        var max = await Dialogs.PromptNumber("Move to selected points", Loc.T("Maximum move distance (m)"), Project!.Design.Spacing);
        if (max is not double m) return;
        int moved = 0;
        Change("Move holes to points", p => moved = HoleDesign.MoveToPoints(HolesById(p, holes), pts, m));
        SetStatus(Loc.F("{0} holes moved", moved));
    }

    public void CreatePointsAt(string where)
    {
        var ids = SelectedHoleIds();
        Change("Create points", p =>
        {
            var layer = p.GetOrCreateLayer(StandardLayers.Survey, 0xFF7C3AED, StandardLayers.TopographyGroup);
            foreach (var h in HolesById(p, ids))
            {
                var pt = where switch { "grade" => h.GradePoint, "toe" => h.Toe, _ => h.Collar };
                p.Add(new PointEntity { Position = pt, Label = h.Label, Kind = where == "grade" ? PointKind.GradePeg : PointKind.General }, layer);
            }
        });
    }

    public void CreateBoreTrackPolylines()
    {
        var ids = SelectedHoleIds();
        Change("Create polyline of bore track", p =>
        {
            foreach (var h in HolesById(p, ids))
                p.Add(new PolylineEntity { Points = h.BoreTrack?.ToList() ?? new List<Vec3> { h.Collar, h.Toe }, Kind = LineKind.BoreTrack, Label = h.Label }, p.GetOrCreateLayer("Bore tracks", 0xFF0891B2, StandardLayers.TopographyGroup));
        });
    }

    public void BoreTrackTool() =>
        SetTool(new PickEntityTool<Hole>(this, "Bore track", "Click a hole, then click its surveyed toe position in plan; the depth is kept.", (h, _) =>
        {
            var id = h.Id;
            SetTool(new PickPointsTool(this, "Bore track", "Click the surveyed toe position.", 1, 1, false, pts =>
            {
                Change("Bore track", p =>
                {
                    if (p.FindEntity(id) is not Hole hole) return;
                    var toe = hole.Toe;
                    var target = new Vec3(pts[0].X, pts[0].Y, toe.Z);
                    hole.BoreTrack = new List<Vec3> { hole.Collar, Vec3.Lerp(hole.Collar, target, 0.5), target };
                    hole.ActualLength = GeoMath.Length(hole.BoreTrack);
                });
                BoreTrackTool();
            }) { Repeat = false });
        }));

    public async Task ResetDesignTracks()
    {
        if (await AskHoles("Reset design tracks") is not { } ids) return;
        Change("Reset design tracks", p => { foreach (var h in HolesById(p, ids)) { h.BoreTrack = null; } });
    }

    public void SetHoleState(HoleState state)
    {
        var ids = SelectedHoleIds();
        Change(state == HoleState.Released ? "Release for loading" : "Set hole state", p => { foreach (var h in HolesById(p, ids)) h.State = state; });
    }

    public void LockHoles(bool locked)
    {
        var ids = SelectedHoleIds();
        Change(locked ? "Lock holes" : "Unlock holes", p => { foreach (var h in HolesById(p, ids)) h.Locked = locked; });
    }

    public void ConvertDummy(bool toDummy)
    {
        var ids = SelectedHoleIds();
        Change(toDummy ? "Convert holes to dummy holes" : "Convert dummy holes to holes", p => { foreach (var h in HolesById(p, ids)) { h.IsDummy = toDummy; if (toDummy) h.Decks.Clear(); } });
    }

    public async Task DrillBetweenLines()
    {
        var lines = Doc?.SelectedEntities.OfType<PolylineEntity>().Take(2).ToList();
        if (lines is not { Count: 2 }) { await Dialogs.Info(Loc.T("Select two polylines.")); return; }
        var spacing = await Dialogs.PromptNumber("Drill holes between lines", Loc.T("Spacing (m)"), Project!.Design.Spacing);
        if (spacing is not double s) return;
        var rowsGuess = Math.Max(2, (int)Math.Round(GeoMath.DistanceToPolyline2D(lines[0].Points, lines[1].Points[0], false) / Project.Design.Burden) + 1);
        var rows = await Dialogs.PromptNumber("Drill holes between lines", Loc.T("Number of rows"), rowsGuess);
        if (rows is not double r) return;
        var hp = NewHoleParameters();
        var start = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        Change("Drill holes between lines", p =>
        {
            var holes = HoleDesign.BetweenLines(lines[0].Points, lines[1].Points, s, (int)r, hp, out var newRows);
            foreach (var h in holes) { h.Label = (start++).ToString(); p.Add(h, HoleLayer); }
            p.Rows.AddRange(newRows);
        });
    }

    public async Task CheckDuplicates()
    {
        var tol = await Dialogs.PromptNumber("Check for duplicates", Loc.T("Distance tolerance (m)"), Project!.Design.DuplicateTolerance);
        if (tol is not double t) return;
        var pairs = HoleDesign.FindDuplicates(Project.Holes.ToList(), t);
        SetHighlight(pairs.SelectMany(x => new[] { x.A.Id, x.B.Id }));
        if (pairs.Count == 0) { await Dialogs.Info(Loc.T("No duplicate holes found.")); return; }
        if (await Dialogs.Confirm(Loc.F("{0} duplicate pairs found (highlighted). Delete the second hole of each pair?", pairs.Count)))
        {
            var remove = pairs.Select(x => x.B.Id).ToHashSet();
            Change("Delete duplicate holes", p => p.Entities.RemoveAll(e => remove.Contains(e.Id)));
            SetHighlight(Array.Empty<Guid>());
        }
    }

    public void RelabelDuplicates()
    {
        int n = 0;
        Change("Re-label duplicate holes", p => n = HoleDesign.RelabelDuplicates(p.Holes));
        SetStatus(Loc.F("{0} holes re-labelled", n));
    }

    public async Task Renumber()
    {
        var prefix = await Dialogs.Prompt("Renumber holes", Loc.T("Label prefix"), "");
        if (prefix == null) return;
        var start = await Dialogs.PromptNumber("Renumber holes", Loc.T("First number"), 1);
        if (start is not double s) return;
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        Change("Renumber holes", p => HoleDesign.Renumber(p, HolesById(p, ids), prefix, (int)s));
    }

    public void HoleLabelTool()
    {
        int next = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        var t = new PickEntityTool<Hole>(this, "Hole label", "Click holes in order to number them.", (h, _) =>
        {
            var label = (next++).ToString();
            Change("Label hole", _ => h.Label = label);
            SetStatus(Loc.F("Next label: {0}", next));
        });
        t.ExtraMenu.Add(MenuEntry.Item("Start at…", async () => { if (await Dialogs.PromptNumber("Hole label", Loc.T("Next number"), next) is double v) next = (int)v; }));
        SetTool(t);
    }

    public async Task FindHole(string mode)
    {
        if (Doc == null) return;
        List<Hole> found;
        if (mode == "label")
        {
            var label = await Dialogs.Prompt("Find hole", Loc.T("Hole label or detonator ID"));
            if (string.IsNullOrWhiteSpace(label)) return;
            found = Project!.Holes.Where(h => string.Equals(h.Label, label, StringComparison.OrdinalIgnoreCase) || string.Equals(h.DetonatorId, label, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (mode == "empty") found = Project!.Holes.Where(h => !h.IsDummy && h.Decks.Count == 0).ToList();
        else found = Project!.Holes.Where(h => h.Exceptions.HasFlag(HoleException.Misfire)).ToList();
        if (found.Count == 0) { await Dialogs.Info(Loc.T("Nothing found.")); return; }
        Doc.Select(found.Select(h => h.Id));
        ZoomSelection();
        SetStatus(Loc.F("{0} holes found", found.Count));
    }

    public async Task CorrectSliceDirection()
    {
        var b = await Dialogs.PromptNumber("Correct slice direction", Loc.T("Bearing for angled holes (deg)"), Project!.Header.BlastOrientation);
        if (b is not double bearing) return;
        var ids = SelectedHoleIds();
        Change("Correct slice direction", p => { foreach (var h in HolesById(p, ids).Where(h => h.Angle > 0.1)) h.Bearing = bearing; });
    }

    public async Task DrapeCollarsOnSurface()
    {
        var s = await PickSurface("Move collars onto surface");
        if (s == null) return;
        var keepToe = await Dialogs.Confirm(Loc.T("Keep the toe positions (lengths change)?"));
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        var sid = s.Id;
        Change("Move collars onto surface", p => HoleDesign.DrapeCollars(HolesById(p, ids), (SurfaceEntity)p.FindEntity(sid)!, keepToe));
    }

    public async Task LengthsToGrade()
    {
        var s = await PickSurface("Set lengths to grade surface");
        if (s == null) return;
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        var sid = s.Id;
        Change("Set lengths to grade", p =>
        {
            var g = (SurfaceEntity)p.FindEntity(sid)!;
            foreach (var h in HolesById(p, ids)) HoleDesign.SetLengthToGrade(h, g, p.Design.BenchHeight);
        });
    }

    public async Task<SurfaceEntity?> PickSurface(string title)
    {
        var sel = Doc?.SelectedEntities.OfType<SurfaceEntity>().FirstOrDefault();
        if (sel != null) return sel;
        var all = Project?.Entities.OfType<SurfaceEntity>().ToList() ?? new();
        if (all.Count == 0) { await Dialogs.Info(Loc.T("The project has no surfaces.")); return null; }
        if (all.Count == 1) return all[0];
        var idx = await Dialogs.Choose(title, Loc.T("Surface"), all.Select(s => s.Name).ToList());
        return idx is int i ? all[i] : null;
    }

    // ---- rows -------------------------------------------------------------------------

    public void CreateRowFromHoles()
    {
        var holes = Doc!.SelectedHoles.ToList();
        if (holes.Count < 2) return;
        var ids = holes.Select(h => h.Id).ToList();
        Change("Create row from holes", p =>
        {
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(ids.Contains);
            p.Rows.RemoveAll(r => r.HoleIds.Count == 0);
            var ordered = GeneralOps.ChainPoints(HolesById(p, ids.ToHashSet()).Select(h => h.Collar).ToList());
            var map = HolesById(p, ids.ToHashSet()).ToDictionary(h => h.Collar);
            var row = new HoleRow { Name = $"Row {p.Rows.Count + 1}", HoleIds = ordered.Select(c => map[c].Id).ToList() };
            foreach (var h in HolesById(p, ids.ToHashSet())) h.RowId = row.Id;
            p.Rows.Add(row);
        });
    }

    public async Task AttachHolesToRow()
    {
        if (Project!.Rows.Count == 0) { await Dialogs.Info(Loc.T("There are no rows.")); return; }
        var idx = await Dialogs.Choose("Attach holes to row", Loc.T("Row"), Project.Rows.Select(r => $"{r.Name} ({r.HoleIds.Count})").ToList());
        if (idx is not int i) return;
        var ids = SelectedHoleIds();
        Change("Attach holes to row", p =>
        {
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(ids.Contains);
            var row = p.Rows[i];
            row.HoleIds.AddRange(ids);
            var holes = row.HoleIds.Select(p.FindEntity).OfType<Hole>().ToList();
            var chain = GeneralOps.ChainPoints(holes.Select(h => h.Collar).ToList());
            row.HoleIds = chain.Select(c => holes.First(h => h.Collar == c).Id).ToList();
            foreach (var h in holes) h.RowId = row.Id;
        });
    }

    public void DetachHolesFromRows()
    {
        var ids = SelectedHoleIds();
        Change("Detach holes from rows", p =>
        {
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(ids.Contains);
            p.Rows.RemoveAll(r => r.HoleIds.Count == 0);
            foreach (var h in HolesById(p, ids)) h.RowId = null;
        });
    }

    public void AutoRows() => Change("Create rows automatically", p =>
    {
        p.Rows = HoleDesign.AutoRows(p.Holes.Where(h => !h.IsDummy), p.Header.BlastOrientation, p.Design.Burden / 2);
        foreach (var r in p.Rows) foreach (var id in r.HoleIds) if (p.FindEntity(id) is Hole h) h.RowId = r.Id;
    });

    public void OptimizeRows()
    {
        var rows = Doc!.SelectedHoles.Select(h => h.RowId).OfType<Guid>().ToHashSet();
        Change("Optimize row spacing", p => { foreach (var r in p.Rows.Where(r => rows.Count == 0 || rows.Contains(r.Id))) HoleDesign.EvenOutRow(p, r); });
    }

    // ---- charging ---------------------------------------------------------------------

    public async Task ApplyLoadingRules()
    {
        if (await AskHoles("Apply loading rules") is not { } ids) return;
        var overwrite = Project!.Holes.Any(h => ids.Contains(h.Id) && h.ManualLoading) && await Dialogs.Confirm(Loc.T("Some holes were loaded by hand. Overwrite them too?"));
        int n = 0;
        Change("Apply loading rules", p => n = Charging.ApplyRules(p, HolesById(p, ids), overwrite));
        SetStatus(Loc.F("{0} holes loaded", n));
    }

    public async Task EditLoadingRules()
    {
        if (Doc == null) return;
        var rules = await LoadingRulesWindow.Show(this, Project!.LoadingRules);
        if (rules != null) Change("Edit loading rules", p => p.LoadingRules = rules);
    }

    public async Task ClearLoadingRules()
    {
        if (await Dialogs.Confirm(Loc.T("Remove all loading rules?"))) Change("Clear loading rules", p => p.LoadingRules.Clear());
    }

    public async Task EmptyHoles()
    {
        if (await AskHoles("Empty holes") is not { } ids) return;
        Change("Empty holes", p => Charging.Empty(HolesById(p, ids)));
    }

    public async Task JoinLikeDecks()
    {
        if (await AskHoles("Join like decks") is not { } ids) return;
        Change("Join like decks", p => Charging.JoinLikeDecks(HolesById(p, ids)));
    }

    public async Task SetLoadingFactor()
    {
        var f = await Dialogs.PromptNumber("Set loading factor", Loc.T("Factor applied to explosive mass (1 = nominal)"), 1);
        if (f is not double v) return;
        if (await AskHoles("Set loading factor") is not { } ids) return;
        Change("Set loading factor", p => { foreach (var h in HolesById(p, ids)) h.LoadingFactor = v; });
    }

    /// <summary>
    /// Edits the charge column of a hole. When several holes are selected, the column edited on the first one
    /// is applied to all of them, adapted to each hole's length.
    /// </summary>
    public async Task EditHoleLoading(Hole? hole = null)
    {
        List<Hole> targets;
        if (hole == null)
        {
            if (await AskHoles("Edit loading") is not { } ids0) return;
            targets = Project!.Holes.Where(h => ids0.Contains(h.Id) && !h.IsDummy).ToList();
        }
        else targets = Doc?.Selection.Contains(hole.Id) == true ? Doc.SelectedHoles.Where(h => !h.IsDummy).ToList() : new List<Hole>();
        hole ??= targets.FirstOrDefault();
        if (hole == null) { await Dialogs.Info(Loc.T("Select a hole first.")); return; }
        if (targets.Count == 0) targets.Add(hole);
        var decks = await HoleLoadingWindow.Show(this, hole, targets.Count);
        if (decks == null) return;
        var template = hole;
        var ids = targets.Select(h => h.Id).ToHashSet();
        Change(targets.Count > 1 ? "Edit loading of selected holes" : "Edit loading", p =>
        {
            foreach (var h in HolesById(p, ids))
            {
                h.Decks = h.Id == template.Id ? decks.Select(d => d.Clone()).ToList() : Charging.AdaptColumn(p, decks, template.EffectiveLength, h.EffectiveLength);
                h.ManualLoading = true;
                if (h.Decks.Count > 0 && h.State < HoleState.Loaded) h.State = HoleState.Loaded;
            }
        });
        if (targets.Count > 1) SetStatus(Loc.F("Loading applied to {0} holes", targets.Count));
    }

    public void LoadingTool() =>
        SetTool(new PickEntityTool<Hole>(this, "Loading", "Click a hole to edit its charge column.", async (h, _) => await EditHoleLoading(h)));

    public async void InholeDelayTool()
    {
        var dets = Project!.ActiveProducts.Where(p => p.Kind == ProductKind.DownholeDetonator).ToList();
        if (dets.Count == 0) { await Dialogs.Info(Loc.T("Add a downhole detonator to the available resources first.")); return; }
        var idx = await Dialogs.Choose("In-hole delay", Loc.T("Detonator to assign"), dets.Select(d => d.Name).ToList());
        if (idx is not int i) return;
        var det = dets[i];
        SetTool(new PickEntityTool<Hole>(this, "In-hole delay", "Click holes to change their downhole detonators.", (h, _) =>
            Change("Set in-hole delay", _ =>
            {
                foreach (var (pr, _) in Charging.Primers(h)) { pr.DetonatorId = det.Id; pr.DelayMs = null; }
                if (!Charging.Primers(h).Any() && h.Decks.FirstOrDefault(d => Project.FindProduct(d.ProductId)?.IsExplosive == true) is { } deck)
                    deck.Primers.Add(new Primer { Depth = deck.Bottom - 0.5, DetonatorId = det.Id, BoosterId = Project.Products.FirstOrDefault(p => p.Kind == ProductKind.Booster)?.Id });
            })));
    }

    // ---- timing -------------------------------------------------------------------------

    public void InitiationPointTool(bool electronic) =>
        SetTool(new PickPointsTool(this, electronic ? "Opening point" : "Lead-in", electronic ? "Click the hole the electronic blast opens from." : "Click the hole the blast is initiated at; the ties then go on from it.", 1, 1, false, pts =>
        {
            // the point is placed on a hole and bound to it
            var (sx, sy) = View.Camera.ToScreen(pts[0]);
            if (View.HitTest(sx, sy, accept: e => e is Hole) is not Hole hole) { SetStatus(Loc.T("Place the initiation point on a hole")); return; }
            if (InitiationLinks.PointOn(Project!, hole.Id) is { } taken) { SetStatus(Loc.F("Hole {0} already has initiation point {1}", hole.Label, taken.Label)); return; }
            var n = Project!.Entities.OfType<InitiationPoint>().Count() + 1;
            var holeId = hole.Id;
            // the existing ties are turned to run away from the new start, in the same undo step
            var turned = 0;
            Change("Add initiation point", p =>
            {
                p.Add(new InitiationPoint { Position = hole.Collar, HoleId = holeId, Label = $"IP{n}", IsElectronic = electronic }, p.GetOrCreateLayer(StandardLayers.Ties, 0xFFDC2626, "Design"));
                turned = Timing.OrientTiesFromInitiation(p);
            });
            // on to the ties from its hole, with the catalogue of what they are made with, as the Ties button does
            TieToolWithPalette(Project!.FindEntity(holeId));
            if (turned > 0) SetStatus(Loc.F("Ties turned to run from the initiation point: {0}", turned));
        }));

    public async void AutoTieUp()
    {
        if (Doc == null) return;
        var ip = Project!.Entities.OfType<InitiationPoint>().FirstOrDefault();
        if (ip == null) { await Dialogs.Info(Loc.T("Place an initiation point (lead-in) first.")); return; }
        var connectors = Project.ActiveProducts.Where(p => p.Kind == ProductKind.SurfaceConnector).ToList();
        if (connectors.Count == 0) { await Dialogs.Info(Loc.T("Add surface connectors to the available resources first.")); return; }
        var names = connectors.Select(c => c.Name).ToList();
        var hi = await Dialogs.Choose("Automatic tie-up", Loc.T("Connector between holes in a row"), names, 0);
        if (hi is not int h) return;
        var ri = await Dialogs.Choose("Automatic tie-up", Loc.T("Connector between rows"), names, Math.Min(names.Count - 1, 2));
        if (ri is not int r) return;
        var ids = SelectedOrAllHoles().Where(x => !x.IsDummy).Select(x => x.Id).ToHashSet();
        var ipId = ip.Id;
        int n = 0;
        Change("Automatic tie-up", p => n = Timing.AutoTieUp(p, HolesById(p, ids), (InitiationPoint)p.FindEntity(ipId)!, connectors[h], connectors[r], p.Design.Burden / 2));
        SetStatus(Loc.F("{0} ties created", n));
    }

    public void ConnectSelectedInOrder()
    {
        var holes = Doc!.SelectedHoles.ToList();
        if (holes.Count < 2) return;
        var chain = GeneralOps.ChainPoints(holes.Select(h => h.Collar).ToList()).Select(c => holes.First(h => h.Collar == c)).ToList();
        var connector = ActiveConnector;
        Change("Connect multiple holes", p => { for (int i = 1; i < chain.Count; i++) Timing.Connect(p, chain[i - 1].Id, chain[i].Id, connector); });
    }

    public void FlipTies()
    {
        var ids = Doc!.SelectedEntities.OfType<TieEntity>().Select(t => t.Id).ToHashSet();
        Change("Flip selected ties", p => Timing.FlipTies(p.Ties.Where(t => ids.Contains(t.Id))));
    }

    public async Task DeleteTieUp()
    {
        HashSet<Guid>? ids;
        if (Doc!.SelectedHoles.Any()) ids = await AskHoles("Delete tie up");
        else ids = await Dialogs.Confirm(Loc.T("Delete the whole tie-up?")) ? Project!.Holes.Select(h => h.Id).ToHashSet() : null;
        if (ids == null) return;
        var all = ids.Count == Project!.Holes.Count();
        Change("Delete tie up", p => Timing.DeleteTieUp(p, all ? null : ids));
    }

    public void ShowTiePalette() => TiePaletteWindow.Show(this);

    /// <summary>The Ties button: the tie tool together with its palette (what the ties are made with).</summary>
    public void TieToolWithPalette() => TieToolWithPalette(null);

    /// <summary>The tie tool with its palette, with a chain already started at <paramref name="start"/> when given.</summary>
    public void TieToolWithPalette(Entity? start)
    {
        SetTool(new TieTool(this, start));
        ShowTiePalette();
    }

    public void RowSetTool() => SetTool(new TimeSetTool(this, true));
    public void SetTimeTool() => SetTool(new TimeSetTool(this, false));

    /// <summary>
    /// The timing front tool: a line, or two segments meeting at a vertex (<see cref="FrontLineTool"/>). When the front is drawn the
    /// timing line window opens; applied, the front stays in the project, programs the selected holes (or all) and is
    /// selected, so that it can be turned and reshaped at once.
    /// </summary>
    public void TimingLineTool()
    {
        PreferHoleOrderLabel();
        SetTool(new FrontLineTool(this, CreateTimingLine));
    }

    private async void CreateTimingLine(List<Vec3> pts)
    {
        try
        {
            var holes = SelectedOrAllHoles().Where(h => !h.IsDummy).ToList();
            var front = pts.Count == 2 ? TimingFrontShape.Line : TimingFrontShape.Chevron;
            if (await TimingLineWindow.Show(this, pts, holes, front: front) is not { } setup) return;
            var ids = ElectronicHoleIds(holes);
            TimingLineResult? r = null;
            Guid? lineId = null;
            // The line stays in the project as an object; its points keep the elevation they were picked at.
            // It remembers its holes (those with an electronic detonator), so editing it later reprograms exactly those.
            Change("Timing line", p =>
            {
                var line = EbsTimingLine.CreateEntity(pts, setup.Segments, setup.Options);
                line.Front = front;
                line.HoleIds = ids.ToList();
                TakeHoles(p, line.Id, ids);
                p.Add(line, p.GetOrCreateLayer(StandardLayers.TimingLines, 0xFF008000, "Design"));
                lineId = line.Id;
                r = EbsTimingLine.Apply(p, HolesById(p, ids), line, setup.Options.KeepManualTimes);
            });
            if (r == null) return;
            // selected without an undo step of its own: one Undo takes the line back
            if (lineId is Guid id) Doc!.Select(new[] { id }, record: false);
            View.Redraw();
            await ReportTimingLine(r);
        }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    /// <summary>The holes with an electronic detonator among <paramref name="holes"/>: only those take the times of a timing line.</summary>
    private HashSet<Guid> ElectronicHoleIds(IEnumerable<Hole> holes) =>
        holes.Where(h => Charging.HasElectronicDetonator(Project!, h)).Select(h => h.Id).ToHashSet();

    /// <summary>A hole is timed by one object of time (a timing front or an opening point): the holes one takes leave the others.</summary>
    internal static void TakeHoles(BlastProject p, Guid timerId, ISet<Guid> ids)
    {
        foreach (var other in p.Entities.OfType<IHoleTimer>().Where(l => l.Id != timerId))
            other.HoleIds.RemoveAll(ids.Contains);
    }

    /// <summary>
    /// The EBS opening point tool: a click where the electronic blast opens, then its window; applied, the point stays in the
    /// project, programs the selected holes with an electronic detonator (or all of them) and is selected.
    /// </summary>
    public void OpeningPointTool()
    {
        PreferHoleOrderLabel();
        SetTool(new PickPointsTool(this, "EBS opening point", "Click where the electronic blast opens; the holes take their times from there.", 1, 1, false, pts => CreateOpeningPoint(pts[0])));
    }

    private async void CreateOpeningPoint(Vec3 at)
    {
        try
        {
            var holes = SelectedOrAllHoles().Where(h => !h.IsDummy).ToList();
            if (await OpeningPointWindow.Show(this, at, holes) is not { } setup) return;
            var ids = ElectronicHoleIds(holes);
            TimingLineResult? r = null;
            var point = setup.Point;
            point.HoleIds = ids.ToList();
            Change("EBS opening point", p =>
            {
                TakeHoles(p, point.Id, ids);
                p.Add(point, p.GetOrCreateLayer(StandardLayers.TimingLines, 0xFF008000, "Design"));
                r = EbsOpening.Apply(p, HolesById(p, ids), point, setup.KeepManualTimes);
            });
            if (r == null) return;
            Doc!.Select(new[] { point.Id }, record: false);
            SelectTool();
            await ReportTimingLine(r);
        }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    /// <summary>Edits an opening point of the project in its window: its values and the times of its holes, in one undo step.</summary>
    public async Task EditOpeningPoint(EbsOpeningPoint point)
    {
        var holes = Project!.Holes.Where(h => point.HoleIds.Contains(h.Id) && !h.IsDummy).ToList();
        if (holes.Count == 0) holes = SelectedOrAllHoles().Where(h => !h.IsDummy).ToList();
        if (await OpeningPointWindow.Show(this, point.Position, holes, point) is not { } setup) return;
        var ids = ElectronicHoleIds(holes);
        var id = point.Id;
        var v = setup.Point;
        TimingLineResult? r = null;
        Change("Edit EBS opening point", p =>
        {
            if (p.FindEntity(id) is not EbsOpeningPoint op) return;
            op.BurdenM = v.BurdenM; op.SpacingM = v.SpacingM; op.BurdenBearingDeg = v.BurdenBearingDeg; op.BurdenDelayMs = v.BurdenDelayMs;
            op.SpacingDelayMs = v.SpacingDelayMs; op.StartOffsetMs = v.StartOffsetMs; op.OpenHere = v.OpenHere;
            // the holes that lost their electronic detonator leave the point, with the time it gave them
            r = EbsOpening.Apply(p, HolesById(p, op.HoleIds.Union(ids).ToHashSet()), op, setup.KeepManualTimes);
            op.HoleIds = ids.ToList();
            TakeHoles(p, id, ids);
        });
        if (r != null) await ReportTimingLine(r);
    }

    /// <summary>
    /// What a timing line did, in the status bar: the holes timed, the manual ones kept and those left out for having no
    /// electronic detonator; the holes past the largest delay and those out of its reach (which lost their time) in a message.
    /// </summary>
    private async Task ReportTimingLine(TimingLineResult r)
    {
        var status = Loc.F("{0} holes timed, {1} with manual time kept", r.Timed, r.SkippedManual);
        if (r.NotElectronic > 0) status += "; " + Loc.F("{0} holes without an electronic detonator left out", r.NotElectronic);
        SetStatus(status);
        var lines = new List<string>();
        // a hole whose last primer fires after it is named with that offset: the line's time plus it passed the limit
        string Exceeded(Hole h) => Charging.MaxPrimerOffset(Project!, h) is var o && o > 0 ? Loc.F("{0} (primer offset {1} ms)", h.Label, o.ToString("0.#")) : h.Label;
        if (r.MaxTimeExceeded.Count > 0)
            lines.Add(Loc.F("Maximum delay {0} ms exceeded, holes not timed: {1}", Project!.Electronic.MaxTimeMs, string.Join(", ", r.MaxTimeExceeded.Select(Exceeded))));
        if (r.Unreached.Count > 0)
            lines.Add(Loc.F("Out of reach of the line, the time was cleared: {0}", string.Join(", ", r.Unreached.Where(h => !h.ElectronicTimeManual).Select(h => h.Label))));
        if (lines.Count > 0) await Dialogs.Info(string.Join("\n\n", lines));
    }

    /// <summary>
    /// New points of a timing line (a front turned or reshaped by its circles): the line and the times of its holes change
    /// in one undo step, as when it is moved.
    /// </summary>
    public void SetTimingLinePoints(Guid lineId, IReadOnlyList<Vec3> points, string undoName)
    {
        var timed = 0;
        var list = points.ToList();
        Change(undoName, p => timed = ReprogramTimingLines(p, new HashSet<Guid> { lineId }, () =>
        {
            if (p.FindEntity(lineId) is TimingLineEntity l) l.Points = list.ToList();
        }));
        if (timed > 0) SetStatus(Loc.F("Timing line changed: {0} holes reprogrammed", timed));
    }

    /// <summary>
    /// Edits a timing line of the project in the timing line window. The line's values, its layer and (unless switched off)
    /// the times of its holes change in one undo step; a line from an older file without its hole list times the selected
    /// holes, else all.
    /// </summary>
    public async Task EditTimingLine(TimingLineEntity line)
    {
        var holes = line.HoleIds.Count > 0
            ? Project!.Holes.Where(h => line.HoleIds.Contains(h.Id) && !h.IsDummy).ToList()
            : SelectedOrAllHoles().Where(h => !h.IsDummy).ToList();
        if (await TimingLineWindow.Show(this, line.Points, holes, line) is not { } setup) return;
        var ids = ElectronicHoleIds(holes);
        var lineId = line.Id;
        TimingLineResult? r = null;
        Change("Edit timing line", p =>
        {
            if (p.FindEntity(lineId) is not TimingLineEntity l) return;
            var o = setup.Options;
            l.Style = o.Style; l.StartOffsetMs = o.StartOffsetMs; l.UseProgressive = o.UseProgressive; l.ProgressRate = o.ProgressRate;
            l.ReverseOrder = o.ReverseOrder; l.BurdenReliefMsPerM = o.BurdenReliefMsPerM; l.Angle1Deg = o.Angle1Deg; l.Angle2Deg = o.Angle2Deg;
            l.Segments = setup.Segments.ToList();
            if (setup.LayerId is Guid layer) l.LayerId = layer;
            if (setup.Reprogram)
            {
                // the holes that lost their electronic detonator leave the line, with the time it gave them
                r = EbsTimingLine.Apply(p, HolesById(p, l.HoleIds.Union(ids).ToHashSet()), l, o.KeepManualTimes);
                l.HoleIds = ids.ToList();
                TakeHoles(p, l.Id, ids);
            }
        });
        if (r != null) await ReportTimingLine(r);
    }

    /// <summary>
    /// Properties ▸ Add holes on a timing line: the holes with an electronic detonator that no timing line programs yet
    /// join the holes of this line, all of them are programmed from it (one undo step), and they are selected. Holes of
    /// other lines stay with their lines.
    /// </summary>
    public async Task AddHolesToTimingLine(TimingLineEntity line)
    {
        var p = Project!;
        var bound = p.Entities.OfType<IHoleTimer>().SelectMany(l => l.HoleIds).ToHashSet();
        var electronic = p.Holes.Where(h => !h.IsDummy && !bound.Contains(h.Id) && Charging.HasElectronicDetonator(p, h)).Select(h => h.Id).ToList();
        if (electronic.Count == 0) { await Dialogs.Info(Loc.T("Every hole with an electronic detonator is already on a timing line.")); return; }
        var lineId = line.Id;
        var ids = line.HoleIds.Union(electronic).ToHashSet();
        var added = ids.Count - line.HoleIds.Count;
        TimingLineResult? r = null;
        Change("Add holes to timing line", pr =>
        {
            if (pr.FindEntity(lineId) is not TimingLineEntity l) return;
            l.HoleIds = ids.ToList();
            r = EbsTimingLine.Apply(pr, HolesById(pr, ids), l);
        });
        // selected without an undo step of its own: one Undo takes the whole command back
        Doc!.Select(ids, record: false);
        if (r == null) return;
        await ReportTimingLine(r);
        SetStatus(Loc.F("Timing line: {0} holes added, {1} holes timed, {2} with manual time kept", added, r.Timed, r.SkippedManual));
    }

    /// <summary>
    /// Properties ▸ Select assigned on a timing line: selects the holes the line programs. A line from an older file that
    /// does not know them finds them by their times, as when it is moved (nothing is changed).
    /// </summary>
    public void SelectTimingLineHoles(TimingLineEntity line)
    {
        var p = Project!;
        var ids = (line.HoleIds.Count > 0 ? line.HoleIds : HolesTimedBy(p, line)).Where(id => p.FindEntity(id) is Hole).ToList();
        if (ids.Count == 0) { SetStatus(Loc.T("The timing line programs no holes")); return; }
        Doc!.Select(ids);
        SetStatus(Loc.F("Timing line: {0} holes selected", ids.Count));
    }

    public async Task AddSubtractTime()
    {
        var d = await Dialogs.PromptNumber("Add/Subtract time", Loc.T("Milliseconds to add (negative subtracts)"), 0);
        if (d is not double v) return;
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        Change("Add/Subtract time", p => Timing.AddTime(HolesById(p, ids), v));
    }

    public async Task AdjustIntervals()
    {
        var f = await Dialogs.PromptNumber("Adjust intervals", Loc.T("Scale factor for intervals"), 1);
        if (f is not double v) return;
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        Change("Adjust intervals", p => Timing.ScaleIntervals(HolesById(p, ids), v));
    }

    public async Task AdjustIncrements()
    {
        var f = await Dialogs.PromptNumber("Adjust increments", Loc.T("Increment between consecutive holes (ms)"), Project!.Electronic.HoleIntervalMs);
        if (f is not double v) return;
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        Change("Adjust increments", p => Timing.Resequence(HolesById(p, ids), v));
    }

    public async Task CopyTimesBetweenHorizons()
    {
        var from = await Dialogs.PromptNumber("Copy times between horizons", Loc.T("From horizon"), 0);
        var to = from == null ? null : await Dialogs.PromptNumber("Copy times between horizons", Loc.T("To horizon"), 1);
        var off = to == null ? null : await Dialogs.PromptNumber("Copy times between horizons", Loc.T("Time offset (ms)"), 0);
        if (off is not double o) return;
        int n = 0;
        Change("Copy times between horizons", p => n = Timing.CopyTimesBetweenHorizons(p.Holes.ToList(), (int)from!.Value, (int)to!.Value, o, p.Electronic.AllowSubMs));
        SetStatus(Loc.F("{0} holes timed", n));
    }

    public async Task Deprogram(bool onlyManual)
    {
        if (await AskHoles(onlyManual ? "Deprogram manually set holes" : "Deprogram holes") is not { } ids) return;
        Change(onlyManual ? "Deprogram manually set holes" : "Deprogram holes", p => Timing.Deprogram(HolesById(p, ids), onlyManual));
    }

    public async Task ConvertToElectronic()
    {
        var det = Project!.Products.FirstOrDefault(p => p.Kind == ProductKind.ElectronicDetonator);
        if (det == null) { await Dialogs.Info(Loc.T("Add an electronic detonator to the available resources first.")); return; }
        var ids = SelectedOrAllHoles().Select(h => h.Id).ToHashSet();
        Change("Convert pyrotechnic devices to electronic", p => Timing.ConvertToElectronic(p, HolesById(p, ids), det));
    }

    public async Task ClearExceptions()
    {
        if (await AskHoles("Clear exceptions") is not { } ids) return;
        Change("Clear exceptions", p => { foreach (var h in HolesById(p, ids)) h.Exceptions = HoleException.None; });
    }

    public void ReapplyTiming()
    {
        Timing.Solve(Project!);
        RefreshAll();
        SetStatus(Loc.T("Timing recalculated"));
    }

    // ---- conversions and general functions ---------------------------------------------

    public void ConvertToPoints(bool group)
    {
        var sel = Doc!.SelectedEntities.ToList();
        var gid = group ? Guid.NewGuid() : (Guid?)null;
        Change(group ? "Convert to group of points" : "Convert to points", p =>
        {
            foreach (var pt in GeneralOps.ToPoints(sel)) { pt.GroupId = gid; p.Add(pt, p.FindLayer(sel[0].LayerId)); }
        });
    }

    public void ConvertToPolyline(bool closed)
    {
        var sel = Doc!.SelectedEntities.ToList();
        if (sel.Count == 0) return;
        Change(closed ? "Convert to polygon" : "Convert to polyline", p =>
        {
            var pts = sel.Count > 1 && sel.All(e => e is PointEntity or Hole) ? GeneralOps.ChainPoints(GeneralOps.ToPolyline(sel, closed).Points) : GeneralOps.ToPolyline(sel, closed).Points;
            p.Add(new PolylineEntity { Points = pts, Closed = closed }, p.FindLayer(sel[0].LayerId));
        });
    }

    public void CirclesToPolygons()
    {
        var ids = Doc!.SelectedEntities.OfType<CircleEntity>().Select(c => c.Id).ToHashSet();
        Change("Convert circles to polygons", p =>
        {
            foreach (var c in p.Entities.OfType<CircleEntity>().Where(c => ids.Contains(c.Id)).ToList())
            {
                p.Entities.Add(GeneralOps.CircleToPolygon(c));
                p.Entities.Remove(c);
            }
        });
    }

    public void SetClosed(bool closed)
    {
        var ids = Doc!.SelectedEntities.OfType<PolylineEntity>().Select(c => c.Id).ToHashSet();
        Change(closed ? "Convert polylines to polygons" : "Convert polygons to polylines", p => { foreach (var l in p.Entities.OfType<PolylineEntity>().Where(l => ids.Contains(l.Id))) l.Closed = closed; });
    }

    public void PointsToMonitoring()
    {
        var ids = Doc!.SelectedEntities.OfType<PointEntity>().Select(c => c.Id).ToHashSet();
        Change("Convert points to monitoring points", p => { foreach (var pt in p.Entities.OfType<PointEntity>().Where(x => ids.Contains(x.Id))) pt.Kind = PointKind.Monitoring; });
    }

    public async Task ClipPolylinesToPolygon()
    {
        var polygon = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(p => p.Closed);
        var lines = Doc.SelectedEntities.OfType<PolylineEntity>().Where(p => p != polygon).ToList();
        if (polygon == null || lines.Count == 0) { await Dialogs.Info(Loc.T("Select one polygon and the polylines to clip.")); return; }
        var ids = lines.Select(l => l.Id).ToHashSet();
        Change("Clip polylines to polygon", p =>
        {
            var clipped = GeneralOps.ClipPolylines(lines, polygon.Points);
            p.Entities.RemoveAll(e => ids.Contains(e.Id));
            p.Entities.AddRange(clipped);
        });
    }

    public async Task CentreOfMass()
    {
        var selected = Doc!.SelectedEntities.OfType<PolylineEntity>().Where(p => p.Closed).ToList();
        var all = Project!.Entities.OfType<PolylineEntity>().Where(p => p.Closed).ToList();
        var polys = selected;
        if (selected.Count > 0 && selected.Count < all.Count)
        {
            var c = await Dialogs.Show("Create centre of mass for polygons", Loc.T("Apply to the selected polygons or to all polygons?"), new[] { "Only selected", "All", "Cancel" });
            if (c > 1 || c < 0) return;
            if (c == 1) polys = all;
        }
        else if (selected.Count == 0) polys = all;
        Change("Create centre of mass", p => { foreach (var poly in polys) p.Entities.Add(GeneralOps.CentreOfMass(poly)); });
    }

    public async Task ExpandPolygon()
    {
        var polys = Doc!.SelectedEntities.OfType<PolylineEntity>().Where(p => p.Closed).ToList();
        if (polys.Count == 0) { await Dialogs.Info(Loc.T("Select a closed polygon first.")); return; }
        var d = await Dialogs.PromptNumber("Expand polygon", Loc.T("Distance (m), negative shrinks"), 5);
        if (d is not double v) return;
        Change("Expand polygon", p => { foreach (var poly in polys) p.Entities.Add(GeneralOps.Expand(poly, v)); });
    }

    public async Task MoveCopyEntities()
    {
        if (Doc?.Selection.Count is not > 0) return;
        var t = await Dialogs.EditObject("Move/copy entities", new MoveCopyParameters());
        if (t == null) return;
        var ids = Doc.Selection.ToHashSet();
        var offset = new Vec3(t.DeltaX, t.DeltaY, t.DeltaZ);
        var timed = 0;
        Change(t.Copies > 0 ? "Copy entities" : "Move entities", p =>
        {
            var sel = p.Entities.Where(e => ids.Contains(e.Id)).ToList();
            if (t.Copies > 0)
            {
                foreach (var c in GeneralOps.Copy(sel, offset, t.Copies))
                {
                    if (c is Hole h) { h.Label += "'"; h.RowId = null; }
                    p.Entities.Add(c);
                }
            }
            else timed = ReprogramTimingLines(p, ids, () => { foreach (var e in sel) e.Translate(offset); });
        });
        ReportReprogrammed(timed);
    }

    public async Task MarkClearanceZone()
    {
        var holes = SelectedOrAllHoles();
        var r = await Dialogs.PromptNumber("Mark clearance zone", Loc.T("Clearance radius (m)"), 500);
        if (r is not double v) return;
        Change("Mark clearance zone", p => p.Add(GeneralOps.ClearanceZone(holes, v), p.GetOrCreateLayer("Clearance", 0xFFDC2626, "Design")));
    }

    public async Task PolygonArea3D()
    {
        var polys = Doc!.SelectedEntities.OfType<PolylineEntity>().Where(p => p.Closed).ToList();
        if (polys.Count == 0) { await Dialogs.Info(Loc.T("Select a closed polygon first.")); return; }
        await Dialogs.Info(string.Join("\n", polys.Select(p => Loc.F("Plan area {0:0.00} m², 3D area {1:0.00} m², perimeter {2:0.00} m", GeoMath.Area(p.Points), GeoMath.Area3D(p.Points), p.Length))));
    }

    // ---- surfaces -------------------------------------------------------------------------

    public async Task CreateSurface(bool breaklines)
    {
        var sel = Doc!.SelectedEntities.Where(e => e is not TieEntity).ToList();
        if (sel.Count == 0) { await Dialogs.Info(Loc.T("Select points, lines or holes to build the surface from.")); return; }
        var kind = await Dialogs.Choose("Create surface", Loc.T("Surface kind"), Enum.GetValues<SurfaceKind>().Select(k => Loc.Enum(k)).ToList(), 1);
        if (kind is not int k) return;
        Change("Create surface", p =>
        {
            var s = Surfaces.FromEntities(sel, breaklines);
            s.Kind = (SurfaceKind)k;
            s.Name = Loc.Enum(s.Kind);
            p.Add(s, p.GetOrCreateLayer(StandardLayers.Surfaces, 0xFF64748B, StandardLayers.TopographyGroup));
        });
    }

    /// <summary>
    /// Survey ▸ Recognize bench elements: crests, toes, contours and boundaries among the survey lines (selected, or all
    /// on visible layers) — shown for checking, applied only from the window.
    /// </summary>
    /// <param name="useModel">The trained model or the rules; the setting when null.</param>
    public async Task RecognizeBenchElements(bool? useModel = null)
    {
        var p = Project!;
        var selected = Doc!.SelectedEntities.ToList();
        var source = selected.Count > 0 ? selected : p.Entities.Where(e => p.FindLayer(e.LayerId)?.Visible != false && !HiddenEntities.Contains(e.Id)).ToList();
        var lines = source.OfType<PolylineEntity>().Where(SurveyFeatures.IsSurveyLine).Select(l => (l, p.FindLayer(l.LayerId)?.Name ?? "0")).ToList();
        if (lines.Count == 0)
        {
            await Dialogs.Info(Loc.T("No survey lines with elevations. Import the survey (DWG/DXF) or select its lines."));
            return;
        }
        var points = source.OfType<PointEntity>().ToList();
        var collars = p.Holes.Select(h => h.Collar).ToList();
        SetStatus(Loc.T("Recognizing bench elements…"));
        var model = useModel ?? Settings.UseSurveyModel;
        if (model && !Recognition.SurveyModel.Available) IO.Log.Error("Survey model not available: " + Recognition.SurveyModel.Problem);
        var result = await Task.Run(() => Recognition.SurveyModel.Recognize(lines, points, collars, model));
        // the window's tool puts its hint in the status bar
        SurveyRecognitionWindow.Show(this, lines, points, collars, result);
    }

    /// <summary>Surface tool: click points, then a surface is triangulated through them.</summary>
    public void SurfaceTool() =>
        SetTool(new PickPointsTool(this, "Surface", "Click surface points (elevation is taken from the work plane); double-click to triangulate.", 3, int.MaxValue, false, pts =>
            Change("Create surface", p => p.Add(Surfaces.Triangulate(pts), p.GetOrCreateLayer(StandardLayers.Surfaces, 0xFF64748B, StandardLayers.TopographyGroup)))));

    private async Task SurfaceOp(string name, Func<SurfaceEntity, Task<Action<BlastProject>?>> op)
    {
        var s = await PickSurface(name);
        if (s == null) return;
        var act = await op(s);
        if (act != null) Change(name, act);
    }

    private Layer SurfLayer(BlastProject p) => p.GetOrCreateLayer(StandardLayers.Surfaces, 0xFF64748B, StandardLayers.TopographyGroup);

    public Task SurfacePointSet() => SurfaceOp("Create a point set from surface", async s =>
    {
        var d = await Dialogs.PromptNumber("Create a point set from surface", Loc.T("Grid spacing (m)"), 5);
        return d is double v ? p => { foreach (var pt in Surfaces.PointSet(s, v)) p.Add(new PointEntity { Position = pt }, SurfLayer(p)); } : null;
    });

    public Task SurfaceContours() => SurfaceOp("Create contours", async s =>
    {
        var d = await Dialogs.PromptNumber("Create contours", Loc.T("Contour interval (m)"), 1);
        return d is double v ? p => { var layer = p.GetOrCreateLayer("Contours", 0xFF94A3B8, StandardLayers.TopographyGroup); foreach (var c in Surfaces.Contours(s, v)) p.Add(c, layer); } : null;
    });

    public Task SurfaceSegmentPoints() => SurfaceOp("Create segment points", async s =>
    {
        var line = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault();
        if (line == null) { await Dialogs.Info(Loc.T("Select a polyline and a surface.")); return null; }
        return p => { foreach (var pt in Surfaces.SegmentPoints(s, line.Points)) p.Add(new PointEntity { Position = pt }, SurfLayer(p)); };
    });

    public Task SurfaceExpand() => SurfaceOp("Expand surface", async s =>
    {
        var d = await Dialogs.PromptNumber("Expand surface", Loc.T("Distance (m)"), 10);
        return d is double v ? p => p.Add(Surfaces.Expand(s, v), SurfLayer(p)) : null;
    });

    public Task SurfaceSimplify(bool smooth) => SurfaceOp(smooth ? "Simplify surface with smoothing" : "Simplify surface", async s =>
    {
        var d = await Dialogs.PromptNumber("Simplify surface", Loc.T("Vertical tolerance (m)"), 0.2);
        return d is double v ? p => p.Add(Surfaces.Simplify(s, v, smooth), SurfLayer(p)) : null;
    });

    public Task SurfaceDecimate() => SurfaceOp("Decimate surface", async s =>
    {
        var d = await Dialogs.PromptNumber("Decimate surface", Loc.T("Cell size (m)"), 2);
        return d is double v ? p => p.Add(Surfaces.Decimate(s, v), SurfLayer(p)) : null;
    });

    public Task SurfaceImprove() => SurfaceOp("Improve surface geometry", s => Task.FromResult<Action<BlastProject>?>(p => p.Add(Surfaces.ImproveGeometry(s), SurfLayer(p))));

    public Task SurfaceClip() => SurfaceOp("Clip surface to polygon", async s =>
    {
        var poly = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(x => x.Closed);
        if (poly == null) { await Dialogs.Info(Loc.T("Select a polygon and a surface.")); return null; }
        var inside = await Dialogs.Confirm(Loc.T("Keep the part inside the polygon?"));
        return p => p.Add(Surfaces.Clip(s, poly.Points, inside), SurfLayer(p));
    });

    public Task MoveObjectsOntoSurface() => SurfaceOp("Move objects onto surface", s =>
    {
        var ids = Doc!.Selection.Where(id => id != s.Id).ToHashSet();
        return Task.FromResult<Action<BlastProject>?>(p => GeneralOps.MoveOntoSurface(p.Entities.Where(e => ids.Contains(e.Id)), (SurfaceEntity)p.FindEntity(s.Id)!));
    });

    public Task IntersectLineSurface() => SurfaceOp("Create intersection line and surface", async s =>
    {
        var lines = Doc!.SelectedEntities.OfType<PolylineEntity>().ToList();
        if (lines.Count == 0) { await Dialogs.Info(Loc.T("Select polylines and a surface.")); return null; }
        return p => { foreach (var l in lines) p.Add(new PolylineEntity { Points = Surfaces.Drape(s, l.Points), Kind = LineKind.Intersection }, SurfLayer(p)); };
    });

    public Task IntersectGradePegs() => SurfaceOp("Create intersection grade peg and surface", async s =>
    {
        var pegs = Doc!.SelectedEntities.OfType<PointEntity>().ToList();
        if (pegs.Count == 0) { await Dialogs.Info(Loc.T("Select grade pegs and a surface.")); return null; }
        return p => { foreach (var g in pegs) if (s.ElevationAt(g.Position.X, g.Position.Y) is double z) p.Add(new PointEntity { Position = g.Position.WithZ(z), Kind = PointKind.GradePeg, Label = g.Label }, SurfLayer(p)); };
    });

    public async Task IntersectSurfaces()
    {
        var surfaces = Doc!.SelectedEntities.OfType<SurfaceEntity>().Take(2).ToList();
        if (surfaces.Count < 2) { await Dialogs.Info(Loc.T("Select two surfaces.")); return; }
        Change("Create intersection of two surfaces", p => { foreach (var l in Surfaces.Intersect(surfaces[0], surfaces[1])) p.Add(l, SurfLayer(p)); });
    }

    public Task BenchLayoutPolyline() => SurfaceOp("Create bench layout polyline", async s =>
    {
        var z = await Dialogs.PromptNumber("Create bench layout polyline", Loc.T("Bench elevation (m)"), s.Vertices.Count > 0 ? Math.Round(s.Vertices.Average(v => v.Z)) : 0);
        return z is double v ? p => { foreach (var l in Surfaces.Contours(s, 0, v)) { l.Kind = LineKind.BenchLayout; p.Add(l, p.GetOrCreateLayer(StandardLayers.Boundary, 0xFF16A34A, "Design")); } } : null;
    });

    public Task ProjectLineToSurface() => IntersectLineSurface();

    public async Task BatterTool()
    {
        var crest = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault();
        if (crest == null) { await Dialogs.Info(Loc.T("Select the crest polyline first.")); return; }
        var b = await Dialogs.EditObject("Batter", new BatterParameters { Height = Project!.Design.BenchHeight });
        if (b == null) return;
        var offset = b.Height / Math.Tan(b.FaceAngle * GeoMath.Deg);
        Change("Batter", p =>
        {
            List<Vec3> toe;
            if (crest.Closed) toe = GeoMath.OffsetPolygon(crest.Points, b.Outwards ? offset : -offset);
            else
            {
                toe = new List<Vec3>();
                for (int i = 0; i < crest.Points.Count; i++)
                {
                    var a = crest.Points[Math.Max(0, i - 1)];
                    var c = crest.Points[Math.Min(crest.Points.Count - 1, i + 1)];
                    var dir = new Vec3(c.X - a.X, c.Y - a.Y, 0).Normalized();
                    var n = new Vec3(dir.Y, -dir.X, 0) * (b.Outwards ? offset : -offset);
                    toe.Add(crest.Points[i] + n);
                }
            }
            p.Add(new PolylineEntity { Points = toe.Select(v => v.WithZ(v.Z - b.Height)).ToList(), Closed = crest.Closed, Kind = LineKind.Toe }, p.FindLayer(crest.LayerId));
        });
    }

    public async Task VolumeTool()
    {
        var poly = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(p => p.Closed) ?? Project!.Entities.OfType<PolylineEntity>().FirstOrDefault(p => p.Closed && p.Kind == LineKind.Boundary);
        var surfaces = Project!.Entities.OfType<SurfaceEntity>().ToList();
        if (surfaces.Count == 0)
        {
            var q = Quantities.Compute(Project, null, poly?.Points);
            await Dialogs.Info(Loc.F("Volume from holes: {0:0} m³, {1:0} t", q.Volume, q.Tonnes), Loc.T("Volume"));
            return;
        }
        var top = surfaces.FirstOrDefault(s => s.Kind is SurfaceKind.Bench or SurfaceKind.Topography) ?? surfaces[0];
        var bottom = surfaces.FirstOrDefault(s => s.Kind == SurfaceKind.Grade && s != top);
        var baseZ = Project.Holes.Any() ? Project.Holes.Average(h => h.GradePoint.Z) : top.Vertices.Min(v => v.Z);
        var (cut, fill, area) = Surfaces.VolumeBetween(top, bottom, baseZ, poly?.Points, Math.Max(0.5, Project.Design.Spacing / 4));
        await Dialogs.Info(Loc.F("{0} above {1}: volume {2:0} m³ ({3:0} t), below {4:0} m³, area {5:0} m²", top.Name, bottom?.Name ?? $"Z {baseZ:0.0}", cut, cut * Project.Design.RockDensity, fill, area), Loc.T("Volume"));
    }

    public async Task LaserWizard()
    {
        var p = await Dialogs.EditObject("Laser wizard", new LaserParameters());
        if (p == null) return;
        var origin = new Vec3(p.StationX, p.StationY, p.StationZ + p.InstrumentHeight);
        var shots = await Dialogs.Prompt("Laser wizard", Loc.T("Shots, one per line: bearing; vertical angle; slope distance"), "");
        if (string.IsNullOrWhiteSpace(shots)) return;
        var pts = new List<Vec3>();
        foreach (var line in shots.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(';', ',', '\t').Select(s => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (parts.Length < 3 || parts.Any(double.IsNaN)) continue;
            var (brg, va, sd) = (parts[0], parts[1], parts[2]);
            var hd = sd * Math.Cos(va * GeoMath.Deg);
            pts.Add(origin + new Vec3(hd * Math.Sin(brg * GeoMath.Deg), hd * Math.Cos(brg * GeoMath.Deg), sd * Math.Sin(va * GeoMath.Deg) - p.TargetHeight));
        }
        Change("Laser wizard", pr => { foreach (var pt in pts) pr.Add(new PointEntity { Position = pt, Kind = PointKind.Survey }, pr.GetOrCreateLayer(StandardLayers.Survey, 0xFF7C3AED, StandardLayers.TopographyGroup)); });
    }
}

public sealed class MoveCopyParameters
{
    public double DeltaX { get; set; }
    public double DeltaY { get; set; }
    public double DeltaZ { get; set; }
    /// <summary>0 moves the objects; N creates N copies at multiples of the offset.</summary>
    public int Copies { get; set; }
}

public sealed class BatterParameters
{
    public double Height { get; set; } = 10;
    public double FaceAngle { get; set; } = 75;
    public bool Outwards { get; set; } = true;
}

public sealed class LaserParameters
{
    public double StationX { get; set; }
    public double StationY { get; set; }
    public double StationZ { get; set; }
    public double InstrumentHeight { get; set; } = 1.5;
    public double TargetHeight { get; set; }
}
