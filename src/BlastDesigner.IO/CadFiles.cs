using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ACadSharp.IO;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using Cad = ACadSharp.Entities;

namespace BlastDesigner.IO;

public sealed class CadImportOptions
{
    /// <summary>Points closer than this along a line are dropped (thinning filter).</summary>
    public double MinPointDistance { get; set; }
    /// <summary>3D faces are collected into one surface per layer.</summary>
    public bool FacesToSurfaces { get; set; } = true;
    public bool ImportText { get; set; } = true;
    /// <summary>What to do with layers switched off or frozen in the drawing.</summary>
    public CadHiddenLayers HiddenLayers { get; set; } = CadHiddenLayers.ImportHidden;
    /// <summary>
    /// In a drawing with real elevations (a survey), lines lying at elevation 0 are annotation or plan drawing, not survey:
    /// they go to a layer of their own ("name (2D)") so they stay out of surfaces and recognition.
    /// </summary>
    public bool SeparateFlatLines { get; set; } = true;
}

public enum CadHiddenLayers { ImportHidden, Skip, ImportVisible }

/// <summary>Colour (null: keep the program's default) and visibility of a drawing layer.</summary>
public sealed record CadLayerInfo(uint? Color, bool Visible);

/// <summary>Objects read from a drawing, with the layers they came from.</summary>
public sealed class CadImport
{
    public List<(string Layer, Entity Entity)> Items { get; } = new();
    public Dictionary<string, CadLayerInfo> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Objects left out because their layer is off or frozen (<see cref="CadHiddenLayers.Skip"/>).</summary>
    public int SkippedHidden { get; set; }
    /// <summary>Lines moved to "(2D)" layers (<see cref="CadImportOptions.SeparateFlatLines"/>).</summary>
    public int FlatLines { get; set; }
}

/// <summary>CAD and mining-package exchange formats: DXF/DWG, string files, OBJ and an IREDES-style drill plan.</summary>
public static class CadFiles
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    // ---- DXF / DWG import (via ACadSharp) -------------------------------------

    public static List<(string Layer, Entity Entity)> ImportCad(string path, CadImportOptions options) => ReadCad(path, options).Items;

    public static CadImport ReadCad(string path, CadImportOptions options)
    {
        var doc = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? DwgReader.Read(path) : DxfReader.Read(path);
        var result = new CadImport();
        var faces = new Dictionary<string, List<Vec3>>();

        foreach (var e in doc.Entities)
        {
            var layer = e.Layer?.Name ?? "0";
            var shown = e.Layer == null || (e.Layer.IsOn && !e.Layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen));
            if (!shown && options.HiddenLayers == CadHiddenLayers.Skip) { result.SkippedHidden++; continue; }
            if (!result.Layers.ContainsKey(layer))
                result.Layers[layer] = new CadLayerInfo(e.Layer == null ? null : Argb(e.Layer.Color), shown || options.HiddenLayers == CadHiddenLayers.ImportVisible);
            Entity? item = null;
            switch (e)
            {
                case Cad.Point p:
                    item = new PointEntity { Position = V(p.Location) };
                    break;
                case Cad.Line l:
                    item = new PolylineEntity { Points = new() { V(l.StartPoint), V(l.EndPoint) } };
                    break;
                case Cad.LwPolyline lw:
                    item = new PolylineEntity { Points = Thin(lw.Vertices.Select(v => new Vec3(v.Location.X, v.Location.Y, lw.Elevation)), options.MinPointDistance), Closed = lw.IsClosed };
                    break;
                case Cad.Polyline2D p2:
                    item = new PolylineEntity { Points = Thin(p2.Vertices.Select(v => V(v.Location)), options.MinPointDistance), Closed = p2.IsClosed };
                    break;
                case Cad.Polyline3D p3:
                    item = new PolylineEntity { Points = Thin(p3.Vertices.Select(v => V(v.Location)), options.MinPointDistance), Closed = p3.IsClosed };
                    break;
                case Cad.Arc a:
                    // a vertex every 5° of the sweep
                    item = new PolylineEntity { Points = a.PolygonalVertexes(Math.Max(8, (int)Math.Ceiling(Math.Abs(a.Sweep) / GeoMath.Deg / 5))).Select(V).ToList() };
                    break;
                case Cad.Circle c:
                    item = new CircleEntity { Center = V(c.Center), Radius = c.Radius };
                    break;
                case Cad.Spline s when s.TryPolygonalVertexes(Math.Max(16, s.ControlPoints.Count * 8), out var sp):
                    item = new PolylineEntity { Points = Thin(sp.Select(V), options.MinPointDistance), Closed = s.IsClosed };
                    break;
                case Cad.Insert ins when ins.HasAttributes:
                    item = SurveyPoint(ins);
                    break;
                case Cad.TextEntity t when options.ImportText:
                    item = new TextEntity { Position = V(t.InsertPoint), Text = t.Value, Height = t.Height, Rotation = t.Rotation / GeoMath.Deg };
                    break;
                case Cad.MText mt when options.ImportText:
                    item = new TextEntity { Position = V(mt.InsertPoint), Text = PlainMText(mt.Value), Height = mt.Height, Rotation = mt.Rotation / GeoMath.Deg };
                    break;
                case Cad.Face3D f when options.FacesToSurfaces:
                    if (!faces.TryGetValue(layer, out var list)) faces[layer] = list = new();
                    list.Add(V(f.FirstCorner)); list.Add(V(f.SecondCorner)); list.Add(V(f.ThirdCorner));
                    if (!f.FourthCorner.Equals(f.ThirdCorner)) { list.Add(V(f.FirstCorner)); list.Add(V(f.ThirdCorner)); list.Add(V(f.FourthCorner)); }
                    break;
            }
            if (item == null) continue;
            item.Color = Argb(e.Color);
            result.Items.Add((layer, item));
        }
        foreach (var (layer, tri) in faces) result.Items.Add((layer, SurfaceFromTriangleSoup(tri, layer)));
        if (options.SeparateFlatLines) SeparateFlat(result);
        return result;
    }

    /// <summary>
    /// Moves lines at elevation 0 to "layer (2D)" when the drawing also holds objects with real elevations — the
    /// 2D plan drawing (design outlines, grid, annotation) of a survey drawing. A drawing that is all 2D is left alone.
    /// </summary>
    private static void SeparateFlat(CadImport result)
    {
        static bool Flat(Entity e) => e is PolylineEntity pl && pl.Points.Count > 0 && pl.Points.All(p => Math.Abs(p.Z) < 1e-6);
        var elevated = result.Items.Any(i => i.Entity is PointEntity or PolylineEntity or SurfaceEntity && i.Entity.GetVertices().Any(p => Math.Abs(p.Z) > 1e-6));
        if (!elevated) return;
        for (int i = 0; i < result.Items.Count; i++)
        {
            var (layer, e) = result.Items[i];
            // the edges of a block at 0 stay with the others of its box
            if (!Flat(e) || BlockLayers.IsBlockLayerName(layer)) continue;
            var flat = layer + " (2D)";
            if (!result.Layers.ContainsKey(flat) && result.Layers.TryGetValue(layer, out var info)) result.Layers[flat] = info;
            result.Items[i] = (flat, e);
            result.FlatLines++;
        }
    }

    /// <summary>
    /// A survey point drawn as a block with attributes: the point at the insertion, its elevation from the attribute that
    /// holds it (by tag, or the only attribute that reads as a decimal number) when the insertion itself is at 0.
    /// </summary>
    private static PointEntity SurveyPoint(Cad.Insert ins)
    {
        var attrs = ins.Attributes.Select(a => (Tag: a.Tag?.Trim().ToUpperInvariant() ?? "", Value: a.Value?.Trim() ?? "")).Where(a => a.Value.Length > 0).ToList();
        static double? Number(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Ci, out var v) ? v : null;
        string[] elevationTags = { "Z", "H", "ELEV", "ELEVATION", "HEIGHT", "ОТМ", "ОТМЕТКА", "ВЫСОТА", "ОТМЕТКИ" };
        string[] nameTags = { "N", "NO", "NAME", "NUMBER", "POINT", "ID", "НОМЕР", "№", "ИМЯ", "ПИКЕТ", "ТОЧКА" };
        var elevation = attrs.Where(a => elevationTags.Contains(a.Tag)).Select(a => Number(a.Value)).FirstOrDefault(v => v != null);
        if (elevation == null)
        {
            // values like "612.34": a decimal point, unlike point numbers
            var decimals = attrs.Where(a => (a.Value.Contains('.') || a.Value.Contains(',')) && Number(a.Value) != null).ToList();
            if (decimals.Count == 1) elevation = Number(decimals[0].Value);
        }
        var p = V(ins.InsertPoint);
        if (elevation is double z && Math.Abs(p.Z) < 1e-6) p = p.WithZ(z);
        // the point number: a named attribute, else the first one that is not a decimal (the elevation)
        var label = attrs.FirstOrDefault(a => nameTags.Contains(a.Tag)).Value
            ?? attrs.FirstOrDefault(a => a.Value.Length > 0 && !a.Value.Contains('.') && !a.Value.Contains(',')).Value ?? "";
        return new PointEntity { Position = p, Label = label };
    }

    /// <summary>ARGB of an AutoCAD colour; null for by-layer, by-block and colour 7 (white on dark, black on light — the program's default does that).</summary>
    private static uint? Argb(ACadSharp.Color c)
    {
        if (c.IsByLayer || c.IsByBlock || (!c.IsTrueColor && c.Index is 0 or 7 or 256)) return null;
        var rgb = c.GetRgb();
        return rgb.Length < 3 ? null : 0xFF000000u | (uint)rgb[0] << 16 | (uint)rgb[1] << 8 | rgb[2];
    }

    /// <summary>
    /// Text of a multi-line text without AutoCAD's inline formatting: \P is a line break, \S a/b; a stacked fraction,
    /// codes like \A1; \fArial|b0; \H2.5x; \W1; \Q0; \C3; and the braces around formatted runs are dropped.
    /// </summary>
    public static string PlainMText(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '{' or '}') continue;
            if (c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
            var code = value[++i];
            switch (code)
            {
                case 'P': case 'n': sb.Append('\n'); break;
                case '~': sb.Append(' '); break;
                case '\\': case '{': case '}': sb.Append(code); break;
                case 'L': case 'l': case 'O': case 'o': case 'K': case 'k': break;
                case 'S':
                    // \Snumerator^denominator; (or / or #)
                    var end = value.IndexOf(';', i + 1);
                    if (end < 0) end = value.Length;
                    sb.Append(Regex.Replace(value[(i + 1)..end], @"[\^#]", "/"));
                    i = end;
                    break;
                case 'U' when i + 5 < value.Length && value[i + 1] == '+':
                    // \U+0416: a character by its code
                    if (int.TryParse(value.AsSpan(i + 2, 4), NumberStyles.HexNumber, Ci, out var u)) { sb.Append((char)u); i += 5; }
                    break;
                default:
                    // \A1; \fFont|b0|i0; \H2.5x; \W1; \Q0; \T1; \C3; \c255; \p…; — up to the semicolon
                    var semi = value.IndexOf(';', i);
                    i = semi < 0 ? value.Length : semi;
                    break;
            }
        }
        var lines = sb.ToString().Split('\n').Select(l => l.Trim());
        return string.Join("\n", lines).Trim('\n');
    }

    private static Vec3 V(CSMath.XYZ p) => new(p.X, p.Y, p.Z);

    private static List<Vec3> Thin(IEnumerable<Vec3> pts, double min)
    {
        var list = new List<Vec3>();
        foreach (var p in pts)
            if (list.Count == 0 || min <= 0 || Vec3.Distance(list[^1], p) >= min) list.Add(p);
        return list;
    }

    /// <summary>Welds a list of triangle corners into an indexed surface.</summary>
    public static SurfaceEntity SurfaceFromTriangleSoup(List<Vec3> corners, string name)
    {
        var s = new SurfaceEntity { Name = name };
        var map = new Dictionary<(long, long, long), int>();
        foreach (var c in corners)
        {
            var key = ((long)Math.Round(c.X * 1000), (long)Math.Round(c.Y * 1000), (long)Math.Round(c.Z * 1000));
            if (!map.TryGetValue(key, out var idx)) { idx = s.Vertices.Count; s.Vertices.Add(c); map[key] = idx; }
            s.Triangles.Add(idx);
        }
        return s;
    }

    // ---- DXF export (own ASCII writer) ---------------------------------------

    public static string ExportDxf(BlastProject project, IEnumerable<Entity> entities)
    {
        var sb = new StringBuilder();
        void G(int code, string value) { sb.Append(code).Append('\n').Append(value).Append('\n'); }
        void Gd(int code, double v) => G(code, v.ToString("0.######", Ci));
        void Pt(Vec3 p, int baseCode = 10) { Gd(baseCode, p.X); Gd(baseCode + 10, p.Y); Gd(baseCode + 20, p.Z); }
        string LayerName(Entity e) => Sanitize(project.FindLayer(e.LayerId)?.Name ?? "0");

        G(0, "SECTION"); G(2, "HEADER"); G(9, "$ACADVER"); G(1, "AC1009"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "TABLES"); G(0, "TABLE"); G(2, "LAYER"); G(70, project.Layers.Count.ToString(Ci));
        foreach (var l in project.Layers) { G(0, "LAYER"); G(2, Sanitize(l.Name)); G(70, "0"); G(62, "7"); G(6, "CONTINUOUS"); }
        G(0, "ENDTAB"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "ENTITIES");
        foreach (var e in entities)
        {
            var layer = LayerName(e);
            switch (e)
            {
                case Hole h:
                    G(0, "CIRCLE"); G(8, layer); Pt(h.Collar); Gd(40, Math.Max(h.RadiusM, 0.05));
                    G(0, "LINE"); G(8, layer + "_TRACE"); Pt(h.Collar); Pt(h.Toe, 11);
                    G(0, "TEXT"); G(8, layer + "_LABEL"); Pt(h.Collar + new Vec3(0.3, 0.3, 0)); Gd(40, 0.5); G(1, h.Label);
                    break;
                case PointEntity p:
                    G(0, "POINT"); G(8, layer); Pt(p.Position);
                    break;
                case PolylineEntity pl:
                    G(0, "POLYLINE"); G(8, layer); G(66, "1"); Pt(Vec3.Zero); G(70, pl.Closed ? "9" : "8");
                    foreach (var v in pl.Points) { G(0, "VERTEX"); G(8, layer); Pt(v); G(70, "32"); }
                    G(0, "SEQEND"); G(8, layer);
                    break;
                case TimingLineEntity tl:
                    G(0, "POLYLINE"); G(8, layer); G(66, "1"); Pt(Vec3.Zero); G(70, "8");
                    foreach (var v in tl.Points) { G(0, "VERTEX"); G(8, layer); Pt(v); G(70, "32"); }
                    G(0, "SEQEND"); G(8, layer);
                    break;
                case TextEntity t:
                    G(0, "TEXT"); G(8, layer); Pt(t.Position); Gd(40, t.Height); Gd(50, t.Rotation); G(1, t.Text);
                    break;
                case CircleEntity c:
                    G(0, "CIRCLE"); G(8, layer); Pt(c.Center); Gd(40, c.Radius);
                    break;
                case ArrowEntity a:
                    G(0, "LINE"); G(8, layer); Pt(a.Start); Pt(a.End, 11);
                    break;
                case DimensionEntity d:
                    G(0, "LINE"); G(8, layer); Pt(d.Start); Pt(d.End, 11);
                    G(0, "TEXT"); G(8, layer); Pt(Vec3.Lerp(d.Start, d.End, 0.5)); Gd(40, 0.5); G(1, d.Distance.ToString("0.00", Ci));
                    break;
                case TieEntity tie:
                    G(0, "LINE"); G(8, layer); Pt(tie.FromPos); Pt(tie.ToPos, 11);
                    break;
                case BlockEntity b:
                    // twelve edges on the block's own layer: opened again, they make the block (BlockAssembly)
                    var corners = b.Corners();
                    foreach (var (i, j) in BlockEntity.Edges) { G(0, "LINE"); G(8, layer); Pt(corners[i]); Pt(corners[j], 11); }
                    break;
                case SurfaceEntity s:
                    for (int i = 0; i < s.Triangles.Count; i += 3)
                    {
                        G(0, "3DFACE"); G(8, layer);
                        Pt(s.Vertices[s.Triangles[i]], 10); Pt(s.Vertices[s.Triangles[i + 1]], 11);
                        Pt(s.Vertices[s.Triangles[i + 2]], 12); Pt(s.Vertices[s.Triangles[i + 2]], 13);
                    }
                    break;
            }
        }
        G(0, "ENDSEC"); G(0, "EOF");
        return sb.ToString();
    }

    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    // ---- String (.str) files ---------------------------------------------------
    // Text format of numbered strings: "string, y, x, z, label" with "0" separator records.

    public static List<Entity> ImportStr(string path, bool ignoreDiameters)
    {
        var result = new List<Entity>();
        var current = new List<Vec3>();
        int currentString = -1;
        void Flush()
        {
            if (current.Count == 1) result.Add(new PointEntity { Position = current[0] });
            else if (current.Count > 1)
            {
                var closed = current.Count > 2 && Vec3.Distance(current[0], current[^1]) < 1e-6;
                if (closed) current.RemoveAt(current.Count - 1);
                result.Add(new PolylineEntity { Points = current.ToList(), Closed = closed, Label = currentString.ToString(Ci) });
            }
            current.Clear();
        }
        foreach (var line in File.ReadLines(path).Skip(2))
        {
            var parts = line.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 4 || !int.TryParse(parts[0], out var sn)) continue;
            if (sn == 0) { Flush(); continue; }
            if (sn != currentString) { Flush(); currentString = sn; }
            if (!double.TryParse(parts[1], NumberStyles.Float, Ci, out var y) || !double.TryParse(parts[2], NumberStyles.Float, Ci, out var x) || !double.TryParse(parts[3], NumberStyles.Float, Ci, out var z)) continue;
            current.Add(new Vec3(x, y, z));
            _ = ignoreDiameters;
        }
        Flush();
        return result;
    }

    public static string ExportStr(IEnumerable<Entity> entities, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{title},{DateTime.Now:dd-MMM-yy},,");
        sb.AppendLine("0, 0.000, 0.000, 0.000, 0.000, 0.000, 0.000");
        int sn = 1;
        foreach (var e in entities)
        {
            IEnumerable<Vec3> pts = e switch
            {
                PolylineEntity pl when pl.Closed => pl.Points.Append(pl.Points[0]),
                Hole h => new[] { h.Collar, h.Toe },
                _ => e.GetVertices()
            };
            var label = e is Hole hh ? hh.Label : string.Empty;
            foreach (var p in pts) sb.AppendLine($"{sn}, {p.Y.ToString("0.000", Ci)}, {p.X.ToString("0.000", Ci)}, {p.Z.ToString("0.000", Ci)}, {label}");
            sb.AppendLine("0, 0.000, 0.000, 0.000,");
            sn = sn % 32000 + 1;
        }
        sb.AppendLine("0, 0.000, 0.000, 0.000, END");
        return sb.ToString();
    }

    // ---- Wavefront OBJ ----------------------------------------------------------

    public static SurfaceEntity ImportObj(string path)
    {
        var s = new SurfaceEntity { Name = Path.GetFileNameWithoutExtension(path) };
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "v" && parts.Length >= 4)
                s.Vertices.Add(new Vec3(double.Parse(parts[1], Ci), double.Parse(parts[2], Ci), double.Parse(parts[3], Ci)));
            else if (parts[0] == "f" && parts.Length >= 4)
            {
                var idx = parts.Skip(1).Select(p => int.Parse(p.Split('/')[0], Ci)).Select(i => i < 0 ? s.Vertices.Count + i : i - 1).ToList();
                for (int k = 1; k < idx.Count - 1; k++) { s.Triangles.Add(idx[0]); s.Triangles.Add(idx[k]); s.Triangles.Add(idx[k + 1]); }
            }
        }
        return s;
    }

    public static string ExportObj(SurfaceEntity s)
    {
        var sb = new StringBuilder($"# {s.Name}\n");
        foreach (var v in s.Vertices) sb.AppendLine($"v {v.X.ToString(Ci)} {v.Y.ToString(Ci)} {v.Z.ToString(Ci)}");
        for (int i = 0; i < s.Triangles.Count; i += 3) sb.AppendLine($"f {s.Triangles[i] + 1} {s.Triangles[i + 1] + 1} {s.Triangles[i + 2] + 1}");
        return sb.ToString();
    }

    // ---- Drill plan XML (subset of the open IREDES drill-plan structure) --------

    public static string ExportDrillPlan(BlastProject project, IEnumerable<Hole> holes)
    {
        XNamespace ns = "http://www.iredes.org/xml/DrillRig";
        var plan = new XElement(ns + "DRPPlan",
            new XElement(ns + "PlanName", project.Header.Name),
            new XElement(ns + "PlanId", project.Id),
            new XElement(ns + "Holes",
                holes.Where(h => !h.IsDummy).Select(h => new XElement(ns + "Hole",
                    new XElement(ns + "HoleId", h.Label),
                    new XElement(ns + "HoleName", h.Label),
                    Point(ns + "StartPoint", h.Collar),
                    Point(ns + "EndPoint", h.DesignToe),
                    new XElement(ns + "TypeOfHole", project.Design.HoleTypes.FirstOrDefault(t => t.Index == h.HoleType)?.Name ?? "Production"),
                    new XElement(ns + "DrillBitDia", h.Diameter.ToString("0.#", Ci))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), plan).ToString();
    }

    private static XElement Point(XName name, Vec3 p) =>
        new(name, new XElement(name.Namespace + "PointX", p.X.ToString("0.###", Ci)), new XElement(name.Namespace + "PointY", p.Y.ToString("0.###", Ci)), new XElement(name.Namespace + "PointZ", p.Z.ToString("0.###", Ci)));

    public static List<Hole> ImportDrillPlan(string path, DesignData design)
    {
        var doc = XDocument.Load(path);
        var holes = new List<Hole>();
        foreach (var h in doc.Descendants().Where(e => e.Name.LocalName == "Hole"))
        {
            Vec3? Pt(string n)
            {
                var e = h.Elements().FirstOrDefault(x => x.Name.LocalName == n);
                if (e == null) return null;
                double Get(string c) => double.Parse(e.Elements().First(x => x.Name.LocalName == c).Value, Ci);
                return new Vec3(Get("PointX"), Get("PointY"), Get("PointZ"));
            }
            var start = Pt("StartPoint");
            var end = Pt("EndPoint");
            if (start is null) continue;
            var hole = new Hole
            {
                Label = h.Elements().FirstOrDefault(x => x.Name.LocalName == "HoleId")?.Value ?? string.Empty,
                Collar = start.Value,
                Diameter = double.TryParse(h.Elements().FirstOrDefault(x => x.Name.LocalName == "DrillBitDia")?.Value, NumberStyles.Float, Ci, out var d) ? d : design.Diameter,
                Subdrill = design.Subdrill
            };
            if (end is Vec3 toe) hole.SetToe(toe);
            holes.Add(hole);
        }
        return holes;
    }
}
