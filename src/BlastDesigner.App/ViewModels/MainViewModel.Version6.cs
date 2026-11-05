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

/// <summary>Commands for the features added in version 6, and where they appear in the menus.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Adds the version 6 commands to the menus built by <see cref="BuildMenu"/>.</summary>
    private List<MenuEntry> AddVersion6Menus(List<MenuEntry> menu)
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
            Item("KML/KMZ files…", Run(ImportKml)),
            Item("LIDAR files (las)…", Run(ImportLas)),
            Item("IREDES Plan file…", Run(() => ImportIredes(false))),
            Item("IREDES Quality file update holes…", Run(() => ImportIredes(true)), P),
            Item("MWD data, single file…", Run(ImportMwd), P)
        });
        SubOf("File", "Export").AddRange(new[]
        {
            Separator,
            Item("KML file…", Run(() => ExportKml(false)), P),
            Item("KMZ file…", Run(() => ExportKml(true)), P),
            Item("IREDES Plan file…", Run(ExportIredes), P),
            Item("Extended firing data with cosines…", Run(ExportExtendedFiring), P),
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
        view.Insert(view.FindIndex(e => e.Header == "Markout") + 1, Item("Burden check view", () => Version6Windows.BurdenCheck(this), P));
        view.Insert(view.FindIndex(e => e.Header == "Burden check view") + 1, Item("Front row burden profile", () => Version6Windows.FrontRowProfile(this), P));
        view.Insert(view.FindIndex(e => e.Header == "Overview"), Item("Dashboard", () => Version6Windows.Dashboard(this), P));
        var calc = SubOf("Calculations");
        calc.InsertRange(calc.FindIndex(e => e.Header == "Quantities"), new[]
        {
            Item("Time envelope overview", () => Version6Windows.ChargeVsTime(this), P),
            Item("Timing overlap", () => Version6Windows.TimingOverlap(this), P),
            Item("Volume overview", () => Version6Windows.VolumeOverview(this), P)
        });
        calc.AddRange(new[]
        {
            Sub("Environmental prediction",
                Item("Flyrock prediction", () => Version6Windows.Flyrock(this), P),
                Item("Fragmentation prediction", () => Version6Windows.Fragmentation(this), P),
                Item("Scale law calculator", () => Version6Windows.SiteLaw(this), P),
                Item("Exclusion zone calculator", () => Version6Windows.ExclusionZone(this), P)),
            Item("Design tolerance check", () => Version6Windows.Tolerances(this), P),
            Item("Strata intercepts", () => Version6Windows.StrataIntercepts(this), P)
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
            Item("Find minimum surface", Run(() => SurfaceEnvelope(true)), S),
            Item("Find maximum surface", Run(() => SurfaceEnvelope(false)), S),
            Item("Create intersection of multiple surfaces", Run(IntersectMultipleSurfaces), S),
            Item("Expand surface to polygon", Run(ExpandSurfaceToPolygon), S)
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
        if (!await Dialogs.Custom("Initiation rules", editor, width: 820, height: 520)) return;
        Change("Edit initiation rules", p => p.InitiationRules = copy);
    }

    public Task EditTolerances() => EditPart("Design tolerances", p => p.Tolerances, (p, v) => p.Tolerances = v);
    public Task EditFlyrockModel() => EditPart("Flyrock constants", p => p.Flyrock, (p, v) => p.Flyrock = v);
    public async Task EditFragmentationModel()
    {
        if (Doc == null) return;
        if (await FragmentationWindow.Show(this) is not { } edited) return;
        Change("Fragmentation inputs", p => p.Fragmentation = edited);
        // the unit last chosen is the one of the projects and presets that have none
        if (edited.SizeUnit is { } unit && unit != Settings.FragmentSizeUnit)
        {
            Settings.FragmentSizeUnit = SizeUnits.Default = unit;
            Settings.Save();
        }
    }
    public Task EditCoordinates() => EditPart("Site coordinate system", p => p.Coordinates, (p, v) => p.Coordinates = v);

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

    public async Task SurfaceEnvelope(bool minimum)
    {
        var s = Doc!.SelectedEntities.OfType<SurfaceEntity>().Take(2).ToList();
        if (s.Count < 2) { await Dialogs.Info(Loc.T("Select two surfaces.")); return; }
        var result = CadOps.Envelope(s[0], s[1], minimum);
        Change(minimum ? "Find minimum surface" : "Find maximum surface", p => p.Add(result, SurfLayer(p)));
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

    public async Task ImportKml()
    {
        EnsureProject();
        var path = await Dialogs.OpenFile("Import KML/KMZ", ("KML/KMZ", new[] { "*.kml", "*.kmz" }));
        if (path == null) return;
        var cs = ProjectJson.DeepClone(Project!.Coordinates);
        var items = Interchange.ImportKml(path, cs);
        Change("Import KML", p =>
        {
            p.Coordinates = cs;
            var layer = p.GetOrCreateLayer(Path.GetFileNameWithoutExtension(path));
            foreach (var e in items) p.Add(e, layer);
        });
        SetStatus(Loc.F("{0} objects imported (UTM zone {1})", items.Count, cs.UtmZone));
    }

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

    public async Task ExportKml(bool kmz)
    {
        if (Project!.Coordinates.UtmZone < 1) { await Dialogs.Info(Loc.T("Set the UTM zone in Edit ▸ Site coordinate system first.")); await EditCoordinates(); if (Project.Coordinates.UtmZone < 1) return; }
        var entities = Doc!.SelectedEntities.Any() ? Doc.SelectedEntities.ToList() : Project.Entities;
        var kml = Interchange.ExportKml(Project, entities);
        if (!kmz) { await ExportText("Export KML", BaseName, ".kml", kml, "KML"); return; }
        var path = await Dialogs.SaveFile("Export KMZ", BaseName + ".kmz", ".kmz", ("KMZ", new[] { "*.kmz" }));
        if (path == null) return;
        Interchange.WriteKmz(path, kml);
        SetStatus(Loc.F("Exported to {0}", path));
    }

    public Task ExportIredes() => ExportText("Export IREDES plan", BaseName + "_iredes", ".xml", Interchange.ExportIredes(Project!, ExportHolesSet()), "XML");
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
