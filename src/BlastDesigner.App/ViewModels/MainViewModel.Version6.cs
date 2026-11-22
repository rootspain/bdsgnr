using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using static BlastDesigner.App.Tools.MenuEntry;

namespace BlastDesigner.App.ViewModels;

/// <summary>Commands for the new features, and where they appear in the menus.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Adds the new commands to the menus built by <see cref="BuildMenu"/>.</summary>
    private List<MenuEntry> AddNewFeaturesMenus(List<MenuEntry> menu)
    {
        List<MenuEntry> SubOf(params string[] path)
        {
            var list = menu;
            foreach (var name in path) list = list.First(e => e.Header == name).Children!;
            return list;
        }
        SubOf("File", "Import").AddRange(new[]
        {
            Separator,
            Item("LIDAR files (las)…", Run(ImportLas)),
            Item("IREDES Plan file…", Run(() => ImportIredes(false))),
            Item("IREDES Quality file update holes…", Run(() => ImportIredes(true)), P),
            Item("MWD data, single file…", Run(ImportMwd), P)
        });
        SubOf("File", "Export").AddRange(new[]
        {
            Separator,
            Item("IREDES Plan file…", Run(ExportIredes), P),
            Item("Davey Tronic file (.BPD)…", Run(ExportBpd), P),
            Item("Strata intercepts…", Run(ExportStrataIntercepts), P),
            Item("Profile logs…", Run(ExportProfileLogs), P)
        });
        SubOf("Edit").AddRange(new[]
        {
            Separator,
            Item("Initiation rules…", Run(EditInitiationRules), P),
            Item("Design tolerances…", Run(EditTolerances), P),
            Item("Flyrock constants…", Run(EditFlyrockModel), P),
            Item("Fragmentation inputs…", Run(EditFragmentationModel), P),
            Item("Site coordinate system…", Run(EditCoordinates), P)
        });
        var view = SubOf("View");
        view.Insert(view.FindIndex(e => e.Header == "Markout") + 1, Item("Burden check view", () => AnalisysWindows.BurdenCheck(this), P));
        view.Insert(view.FindIndex(e => e.Header == "Burden check view") + 1, Item("Front row burden profile", () => AnalisysWindows.FrontRowProfile(this), P));
        view.Insert(view.FindIndex(e => e.Header == "Overview"), Item("Dashboard", () => AnalisysWindows.Dashboard(this), P));
        var calc = SubOf("Calculations");
        calc.InsertRange(calc.FindIndex(e => e.Header == "Quantities"), new[]
        {
            Item("Time envelope overview", () => AnalisysWindows.ChargeVsTime(this), P),
            Item("Timing overlap", () => AnalisysWindows.TimingOverlap(this), P),
            Item("Volume overview", () => AnalisysWindows.VolumeOverview(this), P)
        });
        calc.AddRange(new[]
        {
            Sub("Environmental prediction",
                Item("Flyrock prediction", () => AnalisysWindows.Flyrock(this), P),
                Item("Fragmentation prediction", () => AnalisysWindows.Fragmentation(this), P),
                Item("Exclusion zone calculator", () => AnalisysWindows.ExclusionZone(this), P)),
            Item("Design tolerance check", () => AnalisysWindows.Tolerances(this), P),
            Item("Strata intercepts", () => AnalisysWindows.StrataIntercepts(this), P)
        });
        var holes = SubOf("Tools", "Holes");
        holes.InsertRange(holes.FindIndex(e => e.Header == "Empty"), new[]
        {
            Item("Create holes from selected polylines", Run(HolesFromPolylines), S),
            Item("Delete holes outside polygon", Run(DeleteHolesOutsidePolygon), S),
            Item("Curved layout…", Run(CurvedLayout), S),
            Separator
        });
        holes.InsertRange(holes.FindIndex(e => e.Header == "Join like decks") + 1, new[]
        {
            Item("Apply initiation rules only", Run(ApplyInitiationRules), W),
            Item("Set material type from strata", Run(MaterialFromStrata), P),
            Item("Clear actuals…", Run(ClearActuals), P)
        });
        SubOf("Tools", "General functions").AddRange(new[]
        {
            Separator,
            Item("Polyline offset…", Run(PolylineOffset), S),
            Item("Extend line to intersection", Run(ExtendLine), S),
            Item("Create midpoints of selected segments", MidpointsOfSegments, S)
        });
        SubOf("Tools", "Surfaces").AddRange(new[]
        {
            Separator,
            Item("Minimum or maximum surface…", Run(() => SurfaceEnvelope()), S)
        });
        return menu;
    }

    // ---- settings -----------------------------------------------------------------------

    public async Task EditInitiationRules()
    {
        var grid = new PropertyGrid { Exclude = new() { nameof(InitiationRule.Id) } };
        AddProductChoices(grid);
        var copy = ProjectJson.DeepClone(Project!.InitiationRules);
        var editor = new ListEditor<InitiationRule>(copy, r => $"{r.Priority}. {r.Name}",
            () => new InitiationRule { Name = Loc.T("New rule"), Priority = copy.Count, DetonatorId = Project.Products.FirstOrDefault(p => p.IsDetonator)?.Id, BoosterId = Project.Products.FirstOrDefault(p => p.Kind == ProductKind.Booster)?.Id },
            r => { var c = ProjectJson.DeepClone(r); c.Id = Guid.NewGuid(); c.Name += " (2)"; return c; }, grid);
        if (!await Dialogs.Custom("Initiation rules", editor, width: 920, height: 640, form: true, bare: true)) return;
        Change("Edit initiation rules", p => p.InitiationRules = copy);
    }

    public Task EditTolerances() => ProjectPropertiesWindow.Show(this, "Design tolerances");
    public Task EditFlyrockModel() => ProjectPropertiesWindow.Show(this, "Flyrock constants");
    public Task EditFragmentationModel() => ProjectPropertiesWindow.Show(this, "Fragmentation inputs");
    public Task EditCoordinates() => ProjectPropertiesWindow.Show(this, "Site coordinate system");

    // ---- holes --------------------------------------------------------------------------

    public async Task ApplyInitiationRules()
    {
        if (Project!.InitiationRules.Count == 0) { await Dialogs.Info(Loc.T("There are no initiation rules. Add them in Edit ▸ Initiation rules.")); return; }
        var ids = Doc!.SelectedHoles.Any() ? SelectedHoleIds() : Project.Holes.Select(h => h.Id).ToHashSet();
        int n = 0;
        Change("Apply initiation rules", p => n = Initiation.ApplyRules(p, HolesById(p, ids)));
        SetStatus(Loc.F("{0} holes initiated", n));
    }

    public async Task HolesFromPolylines()
    {
        if (!await RequireSelection<PolylineEntity>("a polyline")) return;
        var lines = Doc!.SelectedEntities.OfType<PolylineEntity>().ToList();
        var hp = NewHoleParameters();
        var start = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        Change("Create holes from selected polylines", p =>
        {
            foreach (var h in CadOps.HolesFromPolylines(lines, hp)) { h.Label = (start++).ToString(); p.Add(h, HoleLayer); }
        });
    }

    public async Task DeleteHolesOutsidePolygon()
    {
        var poly = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(l => l.Closed);
        if (poly == null) { await Dialogs.Info(Loc.T("Select a closed polygon first.")); return; }
        var outside = CadOps.HolesOutside(Project!.Holes, poly.Points).Select(h => h.Id).ToHashSet();
        if (outside.Count == 0 || !await Dialogs.Confirm(Loc.F("Delete {0} holes outside the polygon?", outside.Count))) return;
        Change("Delete holes outside polygon", p =>
        {
            p.Entities.RemoveAll(e => outside.Contains(e.Id) || e is TieEntity t && (outside.Contains(t.FromId) || outside.Contains(t.ToId)));
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(outside.Contains);
        });
    }

    public sealed class CurvedLayoutParameters
    {
        public int Rows { get; set; } = 3;
        public double Burden { get; set; }
        public double Spacing { get; set; }
        public bool Staggered { get; set; } = true;
        /// <summary>Rows are added on the left of the line's direction when set, otherwise on the right.</summary>
        public bool RowsToTheLeft { get; set; }
    }

    public async Task CurvedLayout()
    {
        var line = Doc!.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault();
        if (line == null) { await Dialogs.Info(Loc.T("Select the curved baseline (a polyline) first.")); return; }
        var prm = await Dialogs.EditObject("Curved layout", new CurvedLayoutParameters { Burden = Project!.Design.Burden, Spacing = Project.Design.Spacing, Staggered = Project.Design.Pattern == PatternType.Staggered });
        if (prm == null || prm.Rows < 1 || prm.Burden <= 0 || prm.Spacing <= 0) return;
        var hp = NewHoleParameters();
        var start = int.TryParse(NextHoleLabel(), out var n) ? n : 1;
        var pts = line.Points.ToList();
        Change("Curved layout", p =>
        {
            var holes = CadOps.CurvedLayout(pts, prm.Rows, prm.Burden, prm.Spacing, prm.Staggered, prm.RowsToTheLeft, hp, out var rows);
            foreach (var h in holes) { h.Label = (start++).ToString(); p.Add(h, HoleLayer); }
            foreach (var r in rows) { r.Name = $"Row {p.Rows.Count + 1}"; p.Rows.Add(r); }
        });
    }

    public async Task MaterialFromStrata()
    {
        var strata = Project!.Entities.OfType<SurfaceEntity>().Where(s => s.Kind == SurfaceKind.Strata).ToList();
        if (strata.Count == 0) { await Dialogs.Info(Loc.T("Set the kind of the strata surfaces to Strata first.")); return; }
        var centre = Project.Holes.Select(h => h.Collar).DefaultIfEmpty().Aggregate((a, b) => a + b) / Math.Max(1, Project.Holes.Count());
        var ordered = strata.OrderByDescending(s => s.ElevationAt(centre.X, centre.Y) ?? s.Vertices.Average(v => v.Z)).ToList();
        var ids = Doc!.SelectedHoles.Any() ? SelectedHoleIds() : Project.Holes.Select(h => h.Id).ToHashSet();
        int n = 0;
        Change("Set material type from strata", p => n = Strata.AssignMaterialByToe(HolesById(p, ids), ordered));
        SetStatus(Loc.F("{0} holes updated", n));
    }

    public async Task ClearActuals()
    {
        if (!await Dialogs.Confirm(Loc.T("Clear dipped lengths, as-drilled collars, bore tracks and MWD logs of the selected holes (all holes when none is selected)?"))) return;
        var ids = Doc!.SelectedHoles.Any() ? SelectedHoleIds() : Project!.Holes.Select(h => h.Id).ToHashSet();
        Change("Clear actuals", p =>
        {
            foreach (var h in HolesById(p, ids)) { h.ActualLength = null; h.ActualCollar = null; h.BoreTrack = null; h.Mwd = null; }
        });
    }

    // ---- drawing ------------------------------------------------------------------------

    public async Task PolylineOffset()
    {
        if (!await RequireSelection<PolylineEntity>("a polyline")) return;
        var d = await Dialogs.PromptNumber("Polyline offset", Loc.T("Offset distance (m), positive to the left"), Project!.Design.Burden);
        if (d is not double dist || Math.Abs(dist) < 1e-9) return;
        var lines = Doc!.SelectedEntities.OfType<PolylineEntity>().ToList();
        Change("Polyline offset", p =>
        {
            foreach (var l in lines)
            {
                var pts = l.Closed ? GeoMath.OffsetPolygon(l.Points, dist) : CadOps.OffsetPolyline(l.Points, dist);
                p.Add(new PolylineEntity { Points = pts, Closed = l.Closed, Kind = l.Kind, Label = l.Label }, p.FindLayer(l.LayerId));
            }
        });
    }

    public async Task ExtendLine()
    {
        var lines = Doc!.SelectedEntities.OfType<PolylineEntity>().ToList();
        if (lines.Count < 2) { await Dialogs.Info(Loc.T("Select the line to extend first, then the line to extend it to.")); return; }
        var target = lines[^1];
        var ids = lines.Take(lines.Count - 1).Select(l => l.Id).ToHashSet();
        int n = 0;
        Change("Extend line to intersection", p =>
        {
            foreach (var l in p.Entities.OfType<PolylineEntity>().Where(l => ids.Contains(l.Id)))
            {
                var r = CadOps.ExtendToIntersection(l.Points, target.Points, target.Closed) ?? CadOps.ExtendToIntersection(l.Points, target.Points, target.Closed, atStart: true);
                if (r != null) { l.Points = r; n++; }
            }
        });
        SetStatus(Loc.F("{0} lines extended", n));
    }

    public void MidpointsOfSegments()
    {
        var lines = Doc!.SelectedEntities.OfType<PolylineEntity>().ToList();
        Change("Create midpoints of selected segments", p =>
        {
            foreach (var l in lines)
                foreach (var m in CadOps.SegmentMidpoints(l.Points, l.Closed)) p.Add(new PointEntity { Position = m }, p.FindLayer(l.LayerId));
        });
    }

    // ---- surfaces -----------------------------------------------------------------------

    /// <param name="minimum">The lower or the upper of the two surfaces; asked when null.</param>
    public async Task SurfaceEnvelope(bool? minimum = null)
    {
        var s = Doc!.SelectedEntities.OfType<SurfaceEntity>().Take(2).ToList();
        if (s.Count < 2) { await Dialogs.Info(Loc.T("Select two surfaces.")); return; }
        if (minimum == null)
        {
            var which = await Dialogs.Choose("Minimum or maximum surface", Loc.T("Result"), new[] { Loc.T("Lower of the two (minimum)"), Loc.T("Upper of the two (maximum)") });
            if (which == null) return;
            minimum = which == 0;
        }
        var result = CadOps.Envelope(s[0], s[1], minimum.Value);
        Change(minimum.Value ? "Find minimum surface" : "Find maximum surface", p => p.Add(result, SurfLayer(p)));
    }

    public async Task IntersectMultipleSurfaces()
    {
        var s = Doc!.SelectedEntities.OfType<SurfaceEntity>().ToList();
        if (s.Count < 2) { await Dialogs.Info(Loc.T("Select two or more surfaces.")); return; }
        var lines = CadOps.IntersectAll(s);
        Change("Create intersection of multiple surfaces", p => { foreach (var l in lines) { l.Kind = LineKind.Intersection; p.Add(l, SurfLayer(p)); } });
        SetStatus(Loc.F("{0} intersection lines", lines.Count));
    }

    public async Task ExpandSurfaceToPolygon()
    {
        var s = Doc!.SelectedEntities.OfType<SurfaceEntity>().FirstOrDefault();
        var poly = Doc.SelectedEntities.OfType<PolylineEntity>().FirstOrDefault(l => l.Closed);
        if (s == null || poly == null) { await Dialogs.Info(Loc.T("Select a surface and a closed polygon.")); return; }
        var result = CadOps.ExpandToPolygon(s, poly.Points);
        var id = s.Id;
        Change("Expand surface to polygon", p =>
        {
            var old = p.FindEntity(id);
            result.Id = id;
            result.LayerId = old?.LayerId ?? SurfLayer(p).Id;
            p.Entities.Remove(old!);
            p.Entities.Add(result);
        });
    }

    // ---- import / export ----------------------------------------------------------------

    public sealed class LasImportOptions
    {
        /// <summary>Points are thinned to one per grid cell of this size, m.</summary>
        public double CellSize { get; set; } = 1;
        /// <summary>Keep the lowest point of each cell (ground) instead of the highest (top of muckpile or face).</summary>
        public bool KeepLowest { get; set; }
        public bool CreateSurface { get; set; } = true;
        /// <summary>Longest triangle edge when building the surface, m (0 = no limit).</summary>
        public double MaxEdge { get; set; } = 10;
    }

    public async Task ImportLas()
    {
        EnsureProject();
        var path = await Dialogs.OpenFile("Import LIDAR file", ("LAS", new[] { "*.las" }));
        if (path == null) return;
        var opt = await Dialogs.EditObject("LIDAR import", new LasImportOptions());
        if (opt == null) return;
        var (info, pts) = await Task.Run(() => Interchange.ReadLas(path, opt.CellSize, opt.KeepLowest));
        if (pts.Count == 0) { await Dialogs.Info(Loc.T("The file contains no points.")); return; }
        var name = Path.GetFileNameWithoutExtension(path);
        List<Entity> result;
        if (opt.CreateSurface)
            result = new() { await Task.Run(() => { var s = Surfaces.Triangulate(pts, name, opt.MaxEdge); s.Kind = SurfaceKind.PointCloud; s.ShowWireframe = false; return (Entity)s; }) };
        else
        {
            if (pts.Count > 100_000 && !await Dialogs.Confirm(Loc.F("{0} points will be added as separate objects. Continue? (Increase the cell size to keep fewer.)", pts.Count))) return;
            var group = Guid.NewGuid();
            result = pts.Select(p => (Entity)new PointEntity { Position = p, Kind = PointKind.Survey, GroupId = group }).ToList();
        }
        AddImported(Loc.T("Import LIDAR file"), result.Select(e => (StandardLayers.Surfaces, e)), false);
        SetStatus(Loc.F("LAS {0}.{1}, format {2}: {3} points read, {4} kept", info.VersionMajor, info.VersionMinor, info.PointFormat, info.PointCount, pts.Count));
    }

    public async Task ImportIredes(bool updateExisting)
    {
        EnsureProject();
        var path = await Dialogs.OpenFile(updateExisting ? "Import IREDES quality file" : "Import IREDES plan", ("IREDES XML", new[] { "*.xml" }));
        if (path == null) return;
        var holes = Interchange.ImportIredes(path, Project!.Design);
        if (!updateExisting) { AddImported(Loc.T("Import IREDES plan"), holes.Select(h => (StandardLayers.Holes, (Entity)h)), true); return; }
        int n = 0;
        Change("Import IREDES quality file", p =>
        {
            foreach (var src in holes)
            {
                var h = p.Holes.FirstOrDefault(x => x.Label == src.Label) ?? p.Holes.OrderBy(x => Vec3.Distance2D(x.Collar, src.Collar)).FirstOrDefault(x => Vec3.Distance2D(x.Collar, src.Collar) < p.Design.Spacing / 2);
                if (h == null) continue;
                h.ActualCollar = src.ActualCollar ?? src.Collar;
                h.ActualLength = src.ActualLength ?? src.Length;
                if (src.ActualCollar == null && Vec3.Distance(src.Collar, h.Collar) > 1e-3) h.BoreTrack = new() { src.Collar, src.DesignToe };
                if (h.State < HoleState.Drilled) h.State = HoleState.Drilled;
                n++;
            }
        });
        SetStatus(Loc.F("{0} holes updated", n));
    }

    public async Task ImportMwd()
    {
        var path = await Dialogs.OpenFile("Import MWD data", ("CSV", new[] { "*.csv", "*.txt" }));
        if (path == null) return;
        var lines = await File.ReadAllLinesAsync(path);
        int n = 0;
        Change("Import MWD data", p => n = Interchange.ImportMwd(lines, p.Holes));
        SetStatus(Loc.F("{0} holes updated", n));
    }

    public Task ExportIredes() => ExportText("Export IREDES plan", BaseName + "_iredes", ".xml", Interchange.ExportIredes(Project!, ExportHolesSet()), "XML");
    /// <summary>
    /// Davey Tronic blast plan (PU_AUTO_BLASTPLAN.BPD): one line per chain of EBS ties, with its programming unit, and the
    /// header of the export window; written, the names of the blast stay in the project for the next export.
    /// </summary>
    public async Task ExportBpd()
    {
        var holes = ExportHolesSet().ToList();
        var choice = await BpdExportWindow.Show(Project!, holes);
        if (choice == null) return;
        var r = DeviTronic.Build(Project!, holes, choice.Paths, choice.Unit, choice.Header.SourceFile, levels: choice.Levels, header: choice.Header);
        string Text(IEnumerable<BpdMessage> list) => string.Join("\n", list.Take(12).Select(m => "• " + Loc.F(m.Format, m.Args)));
        if (r.Errors.Count > 0) { await Dialogs.Info(Loc.T("The BPD file was not written:") + "\n" + Text(r.Errors), Loc.T("DaveyTronic (.BPD)")); return; }
        if (r.Warnings.Count > 0)
        {
            // holes left out of the file: they can be selected in the plan instead of writing it
            var left = r.Warnings.SelectMany(w => w.Holes).Distinct().ToList();
            var buttons = left.Count > 0 ? new[] { "Yes", "Select the holes not written", "No" } : new[] { "Yes", "No" };
            var answer = await Dialogs.Show("DaveyTronic (.BPD)", Text(r.Warnings) + "\n\n" + Loc.T("Write the file?"), buttons);
            if (left.Count > 0 && answer == 1)
            {
                Doc!.Select(left);
                SetStatus(Loc.F("{0} holes selected", left.Count));
                return;
            }
            if (answer != 0) return;
        }
        var path = await Dialogs.SaveFile("DaveyTronic (.BPD)", DeviTronic.DefaultFileName, ".BPD", ("BPD", new[] { "*.BPD", "*.bpd" }));
        if (path == null) return;
        DeviTronic.Save(path, r.Plan);
        // the names written go with the project, for its next export
        var (full, displayed) = (choice.Header.FullName, choice.Header.DisplayedName);
        if (Project!.BpdExport is not { } kept || kept.FullName != full || kept.DisplayedName != displayed)
            Change("BPD export names", p => p.BpdExport = new BpdExportSettings { FullName = full, DisplayedName = displayed });
        SetStatus(Loc.F("Exported to {0}", path) + " · " + Loc.F("{0} lines, {1} detonators", r.Plan.Paths.Count, r.Plan.Paths.Sum(x => x.DetonatorCount)));
    }

    public Task ExportExtendedFiring() => ExportText("Export firing data", BaseName + "_firing_ext", ".csv", Interchange.ExportExtendedFiringData(Project!, ExportHolesSet()), "CSV");
    public Task ExportProfileLogs() => ExportText("Export profile logs", BaseName + "_profiles", ".csv", Interchange.ExportProfileLogs(ExportHolesSet()), "CSV");

    public async Task ExportStrataIntercepts()
    {
        var surfaces = Project!.Entities.OfType<SurfaceEntity>().Where(s => s.Kind == SurfaceKind.Strata).ToList();
        if (surfaces.Count == 0) surfaces = Project.Entities.OfType<SurfaceEntity>().ToList();
        await ExportText("Export strata intercepts", BaseName + "_intercepts", ".csv", Interchange.ExportStrataIntercepts(Strata.Intercepts(ExportHolesSet(), surfaces)), "CSV");
    }

    /// <summary>Saves text produced by an analysis window.</summary>
    public Task SaveCsv(string title, string suffix, string content) => ExportText(title, BaseName + suffix, ".csv", content, "CSV");
}
