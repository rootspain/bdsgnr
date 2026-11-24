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
    /// <summary>3D faces are collected into one surface per layer; each polyface mesh, polygon mesh and MESH is a surface of its own.</summary>
    public bool FacesToSurfaces { get; set; } = true;
    public bool ImportText { get; set; } = true;
    /// <summary>What to do with layers switched off or frozen in the drawing.</summary>
    public CadHiddenLayers HiddenLayers { get; set; } = CadHiddenLayers.ImportHidden;
    /// <summary>
    /// In a drawing with real elevations (a survey), lines lying at elevation 0 are annotation or plan drawing, not survey:
    /// they go to a layer of their own ("name (2D)") so they stay out of surfaces and recognition.
    /// </summary>
    public bool SeparateFlatLines { get; set; } = true;
    /// <summary>The drawing is in feet: its coordinates, radii and text heights are taken to metres (the thinning distance stays in its units).</summary>
    public bool InFeet { get; set; }
}

public enum CadHiddenLayers { ImportHidden, Skip, ImportVisible }

/// <summary>
/// Colour (null: keep the program's default), visibility, line weight (hundredths of a millimetre; null: the default) and
/// line type (null: continuous) of a drawing layer.
/// </summary>
public sealed record CadLayerInfo(uint? Color, bool Visible, int? LineWeight = null, string? LineType = null);

/// <summary>Objects read from a drawing, with the layers they came from.</summary>
public sealed class CadImport
{
    public List<(string Layer, Entity Entity)> Items { get; } = new();
    public Dictionary<string, CadLayerInfo> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Objects left out because their layer is off or frozen (<see cref="CadHiddenLayers.Skip"/>).</summary>
    public int SkippedHidden { get; set; }
    /// <summary>
    /// Objects left out because they cannot be read, by kind: AECC (a Civil 3D surface or other object), PROXY (an object of
    /// another program), ACIS (a solid or a surface of AutoCAD), INSERT (blocks nested too deep), or the name of a mesh without faces.
    /// </summary>
    public Dictionary<string, int> Skipped { get; } = new();
    internal void Skip(string kind) => Skipped[kind] = Skipped.GetValueOrDefault(kind) + 1;
    /// <summary>Lines moved to "(2D)" layers (<see cref="CadImportOptions.SeparateFlatLines"/>).</summary>
    public int FlatLines { get; set; }
    /// <summary>The line types of the drawing (its LTYPE table, without the continuous, by layer and by block ones).</summary>
    public List<LineTypeDef> LineTypes { get; } = new();
    /// <summary>LTSCALE of the drawing: the scale of the dashes of all its line types.</summary>
    public double LineTypeScale { get; set; } = 1;
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
        // the surfaces of the meshes, one for each, named after their layer when all is read
        var meshes = new List<(string Layer, SurfaceEntity Surface)>();

        // an object of the drawing, or of a block taken apart: layer "0" and by-block colour inside a block are those of
        // the insertion, as AutoCAD draws them
        void Take(Cad.Entity e, string? blockLayer, ACadSharp.Tables.Layer? blockLayerRow, uint? blockColor, int depth)
        {
            var row = e.Layer;
            var layer = row?.Name ?? "0";
            if (blockLayer != null && layer == "0") { layer = blockLayer; row = blockLayerRow; }
            var shown = row == null || (row.IsOn && !row.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen));
            if (!shown && options.HiddenLayers == CadHiddenLayers.Skip) { result.SkippedHidden++; return; }
            if (!result.Layers.ContainsKey(layer))
                result.Layers[layer] = new CadLayerInfo(row == null ? null : Argb(row.Color), shown || options.HiddenLayers == CadHiddenLayers.ImportVisible,
                    row == null ? null : LayerWeight(row.LineWeight), TypeName(row?.LineType));
            var color = e.Color.IsByBlock && blockLayer != null ? blockColor : Argb(e.Color);
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
                case Cad.Spline sp when sp.TryPolygonalVertexes(Math.Max(16, sp.ControlPoints.Count * 8), out var pts):
                    item = new PolylineEntity { Points = Thin(pts.Select(V), options.MinPointDistance), Closed = sp.IsClosed };
                    break;
                case Cad.Insert ins when ins.HasAttributes:
                    item = SurveyPoint(ins);
                    break;
                case Cad.Insert ins:
                    // a block without attributes is taken apart, as AutoCAD's Explode does, blocks in it too
                    if (depth >= MaxBlockDepth || ins.Block == null) { result.Skip("INSERT"); return; }
                    foreach (var part in ExplodeAll(ins))
                        if (part is not Cad.AttributeDefinition)
                            Take(part, layer, row, color, depth + 1);
                    return;
                case Cad.AttributeDefinition:
                    return;
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
                    return;
                case Cad.PolyfaceMesh or Cad.PolygonMesh or Cad.Mesh when options.FacesToSurfaces:
                    if (MeshSurface(e) is { } mesh) { mesh.Color = color; meshes.Add((layer, mesh)); }
                    else result.Skip(e.ObjectName);
                    return;
                default:
                    // what cannot be read at all: Civil 3D surfaces (proxies), ACIS solids and surfaces
                    if (Unreadable(e) is { } kind) result.Skip(kind);
                    return;
            }
            if (item == null) return;
            item.Color = color;
            if (item is ILineStyled styled)
            {
                styled.LineWeight = Weight(e.LineWeight);
                styled.LineType = TypeName(e.LineType);
                styled.LineTypeScale = e.LineTypeScale > 0 ? e.LineTypeScale : 1;
            }
            result.Items.Add((layer, item));
        }

        foreach (var e in doc.Entities) Take(e, null, null, null, 0);
        foreach (var (layer, tri) in faces) result.Items.Add((layer, SurfaceFromTriangleSoup(tri, layer)));
        // one surface for each mesh: the name of its layer, numbered when the layer has several surfaces
        foreach (var g in meshes.GroupBy(m => m.Layer, StringComparer.OrdinalIgnoreCase))
        {
            int n = faces.ContainsKey(g.Key) ? 1 : 0;
            foreach (var (layer, surface) in g)
            {
                n++;
                surface.Name = n == 1 ? layer : layer + " " + n;
                result.Items.Add((layer, surface));
            }
        }
        foreach (var lt in doc.LineTypes)
            if (TypeName(lt) is { } name && lt.Segments.Any())
                result.LineTypes.Add(new LineTypeDef { Name = name, Description = lt.Description ?? "", Pattern = lt.Segments.Select(x => x.Length).ToList() });
        if (doc.Header.LineTypeScale > 0) result.LineTypeScale = doc.Header.LineTypeScale;
        if (options.InFeet) ToMetres(result.Items, 0.3048);
        if (options.SeparateFlatLines) SeparateFlat(result);
        return result;
    }

    /// <summary>Blocks in blocks are taken apart this deep at most (a block that holds itself would never end).</summary>
    private const int MaxBlockDepth = 8;

    /// <summary>
    /// The objects of a block insertion where the insertion puts them; an array insertion (rows and columns) gives each copy,
    /// moved by its row and column along the turned axes of the insertion.
    /// </summary>
    private static IEnumerable<Cad.Entity> ExplodeAll(Cad.Insert ins)
    {
        var parts = ins.Explode().ToList();
        int rows = Math.Max(1, (int)ins.RowCount), cols = Math.Max(1, (int)ins.ColumnCount);
        if (rows == 1 && cols == 1) return parts;
        var list = new List<Cad.Entity>();
        double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                double dx = c * ins.ColumnSpacing, dy = r * ins.RowSpacing;
                var shift = new CSMath.XYZ(dx * cos - dy * sin, dx * sin + dy * cos, 0);
                foreach (var part in parts)
                {
                    var copy = (Cad.Entity)part.Clone();
                    copy.Layer = part.Layer;
                    if (shift.X != 0 || shift.Y != 0) copy.ApplyTransform(new CSMath.Transform(shift, new CSMath.XYZ(1, 1, 1), CSMath.XYZ.Zero));
                    list.Add(copy);
                }
            }
        return list;
    }

    /// <summary>
    /// A polyface mesh, a polygon mesh (M×N) or a MESH as a surface of triangles; null when it has no face. A face of four
    /// corners is two triangles, of more a fan from its first corner; a face with a corner repeated or missing is left out.
    /// </summary>
    public static SurfaceEntity? MeshSurface(Cad.Entity e)
    {
        var s = new SurfaceEntity();
        void Face(IReadOnlyList<int> corners)
        {
            var distinct = new List<int>();
            foreach (var c in corners) if (c >= 0 && c < s.Vertices.Count && (distinct.Count == 0 || distinct[^1] != c)) distinct.Add(c);
            if (distinct.Count > 1 && distinct[0] == distinct[^1]) distinct.RemoveAt(distinct.Count - 1);
            if (distinct.Count < 3 || distinct.Distinct().Count() != distinct.Count) return;
            for (int k = 1; k + 1 < distinct.Count; k++) { s.Triangles.Add(distinct[0]); s.Triangles.Add(distinct[k]); s.Triangles.Add(distinct[k + 1]); }
        }
        switch (e)
        {
            case Cad.PolyfaceMesh pf:
                foreach (var v in pf.Vertices) s.Vertices.Add(V(v.Location));
                // the indexes start at 1; a negative one is an invisible edge, the corner is the same
                foreach (var f in pf.Faces)
                    Face(new[] { f.Index1, f.Index2, f.Index3, f.Index4 }.Where(x => x != 0).Select(x => Math.Abs((int)x) - 1).ToList());
                break;
            case Cad.PolygonMesh pm:
            {
                var pts = pm.Vertices.Select(v => V(v.Location)).ToList();
                int m = pm.MVertexCount, n = pm.NVertexCount;
                // a smoothed mesh keeps its smoothed vertices after the control ones
                if (m * n != pts.Count && pm.MSmoothSurfaceDensity * pm.NSmoothSurfaceDensity == pts.Count) { m = pm.MSmoothSurfaceDensity; n = pm.NSmoothSurfaceDensity; }
                if (m < 2 || n < 2 || m * n > pts.Count) return null;
                s.Vertices.AddRange(pts.Take(m * n));
                bool closedM = pm.Flags.HasFlag(Cad.PolylineFlags.ClosedPolylineOrClosedPolygonMeshInM), closedN = pm.Flags.HasFlag(Cad.PolylineFlags.ClosedPolygonMeshInN);
                int At(int i, int j) => (i % m) * n + j % n;
                for (int i = 0; i < (closedM ? m : m - 1); i++)
                    for (int j = 0; j < (closedN ? n : n - 1); j++)
                    {
                        Face(new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1) });
                        Face(new[] { At(i, j), At(i + 1, j + 1), At(i, j + 1) });
                    }
                break;
            }
            case Cad.Mesh mesh:
                foreach (var v in mesh.Vertices) s.Vertices.Add(V(v));
                foreach (var f in mesh.Faces) Face(f);
                break;
            default:
                return null;
        }
        return s.Triangles.Count == 0 ? null : s;
    }

    /// <summary>What of the drawing holds a surface or a solid that cannot be read: its kind for the count of the left out, else null.</summary>
    public static string? Unreadable(Cad.Entity e)
    {
        var name = (e.ObjectName ?? "").ToUpperInvariant();
        if (name.StartsWith("AECC_", StringComparison.Ordinal)) return "AECC";
        if (e is Cad.ProxyEntity || name == "ACAD_PROXY_ENTITY") return "PROXY";
        return name switch
        {
            "3DSOLID" or "BODY" or "REGION" or "SURFACE" or "PLANESURFACE" or "EXTRUDEDSURFACE" or "LOFTEDSURFACE" or "REVOLVEDSURFACE"
                or "SWEPTSURFACE" or "NURBSURFACE" => "ACIS",
            _ => null
        };
    }

    /// <summary>Objects of a drawing in feet to metres: every point, the radius of a circle, the height of a text.</summary>
    public static void ToMetres(IEnumerable<(string Layer, Entity Entity)> items, double metresPerUnit)
    {
        foreach (var (_, e) in items)
        {
            e.Transform(v => v * metresPerUnit);
            if (e is CircleEntity c) c.Radius *= metresPerUnit;
            if (e is TextEntity t) t.Height *= metresPerUnit;
        }
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

    /// <summary>
    /// The objects as a DXF drawing of AutoCAD 2000 (AC1015): one layer each of the project's, with its colour, line weight and
    /// line type; polylines and circles with their own weight, type and type scale; the line types used, and the scale of the
    /// project as LTSCALE. Holes are a circle, a line down the hole and the number; surfaces 3D faces.
    /// </summary>
    public static string ExportDxf(BlastProject project, IEnumerable<Entity> entities)
    {
        var doc = new ACadSharp.CadDocument(ACadSharp.ACadVersion.AC1015);
        doc.Header.LineTypeScale = project.LineTypeScale > 0 ? project.LineTypeScale : 1;
        ACadSharp.Tables.LineType? Type(string? name)
        {
            if (LineTypes.IsSolid(name)) return null;
            if (doc.LineTypes.TryGetValue(name!, out var have)) return have;
            if (LineTypes.Find(project, name) is not { } def) return null;
            var lt = new ACadSharp.Tables.LineType(def.Name) { Description = def.Description };
            foreach (var v in def.Pattern) lt.AddSegment(new ACadSharp.Tables.LineType.Segment { Length = v });
            doc.LineTypes.Add(lt);
            return lt;
        }
        ACadSharp.Tables.Layer LayerOf(string name, Layer? from = null)
        {
            name = Sanitize(name);
            if (doc.Layers.TryGetValue(name, out var have)) return have;
            var l = new ACadSharp.Tables.Layer(name);
            if (from != null)
            {
                l.Color = new ACadSharp.Color((byte)(from.Color >> 16), (byte)(from.Color >> 8), (byte)from.Color);
                if (from.LineWeight is int w) l.LineWeight = (ACadSharp.LineWeightType)w;
                if (Type(from.LineType) is { } lt) l.LineType = lt;
            }
            doc.Layers.Add(l);
            return l;
        }
        foreach (var l in project.Layers) LayerOf(l.Name, l);
        CSMath.XYZ P(Vec3 v) => new(v.X, v.Y, v.Z);
        void Add(Cad.Entity e, string layer, Entity? from = null)
        {
            e.Layer = LayerOf(layer);
            if (from?.Color is uint c) e.Color = new ACadSharp.Color((byte)(c >> 16), (byte)(c >> 8), (byte)c);
            if (from is ILineStyled ls)
            {
                if (ls.LineWeight is int w) e.LineWeight = (ACadSharp.LineWeightType)w;
                if (Type(ls.LineType) is { } lt) e.LineType = lt;
                if (ls.LineTypeScale > 0 && Math.Abs(ls.LineTypeScale - 1) > 1e-12) e.LineTypeScale = ls.LineTypeScale;
            }
            doc.Entities.Add(e);
        }
        void Line(Vec3 a, Vec3 b, string layer) => Add(new Cad.Line { StartPoint = P(a), EndPoint = P(b) }, layer);
        void Text(Vec3 at, double height, string text, string layer, double rotationDeg = 0) =>
            Add(new Cad.TextEntity { InsertPoint = P(at), Height = height, Value = text, Rotation = rotationDeg * GeoMath.Deg }, layer);
        void Poly(IEnumerable<Vec3> points, bool closed, string layer, Entity? from)
        {
            var pl = new Cad.Polyline3D();
            foreach (var v in points) pl.Vertices.Add(new Cad.Vertex3D(P(v)));
            if (closed) pl.IsClosed = true;
            Add(pl, layer, from);
        }

        foreach (var e in entities)
        {
            var layer = project.FindLayer(e.LayerId)?.Name ?? "0";
            switch (e)
            {
                case Hole h:
                    Add(new Cad.Circle { Center = P(h.Collar), Radius = Math.Max(h.RadiusM, 0.05) }, layer);
                    Line(h.Collar, h.Toe, layer + "_TRACE");
                    Text(h.Collar + new Vec3(0.3, 0.3, 0), 0.5, h.Label, layer + "_LABEL");
                    break;
                case PointEntity p:
                    Add(new Cad.Point { Location = P(p.Position) }, layer, p);
                    break;
                case PolylineEntity pl:
                    Poly(pl.Points, pl.Closed, layer, pl);
                    break;
                // a point front is a point: a polyline of one vertex is no figure
                case TimingLineEntity { Points.Count: 1 } tp:
                    Add(new Cad.Point { Location = P(tp.Points[0]) }, layer, null);
                    break;
                case TimingLineEntity tl:
                    Poly(tl.Points, false, layer, null);
                    break;
                case TextEntity t:
                    Text(t.Position, t.Height, t.Text, layer, t.Rotation);
                    break;
                case CircleEntity c:
                    Add(new Cad.Circle { Center = P(c.Center), Radius = c.Radius }, layer, c);
                    break;
                case ArrowEntity a:
                    Line(a.Start, a.End, layer);
                    break;
                case DimensionEntity d:
                    Add(new Cad.Line { StartPoint = P(d.Start), EndPoint = P(d.End) }, layer, d);
                    Text(Vec3.Lerp(d.Start, d.End, 0.5), 0.5, d.Distance.ToString("0.00", Ci), layer);
                    break;
                case TieEntity tie:
                    Line(tie.FromPos, tie.ToPos, layer);
                    break;
                case BlockEntity b:
                    // twelve edges on the block's own layer: opened again, they make the block (BlockAssembly)
                    var corners = b.Corners();
                    foreach (var (i, j) in BlockEntity.Edges) Line(corners[i], corners[j], layer);
                    break;
                case SurfaceEntity s:
                    for (int i = 0; i + 2 < s.Triangles.Count; i += 3)
                    {
                        var (p0, p1, p2) = (s.Vertices[s.Triangles[i]], s.Vertices[s.Triangles[i + 1]], s.Vertices[s.Triangles[i + 2]]);
                        Add(new Cad.Face3D { FirstCorner = P(p0), SecondCorner = P(p1), ThirdCorner = P(p2), FourthCorner = P(p2) }, layer);
                    }
                    break;
            }
        }
        using var stream = new MemoryStream();
        using (var writer = new DxfWriter(stream, doc, false)) writer.Write();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The weight of an object: its own in hundredths of a millimetre, the default as 0.25 mm; null by layer or block.</summary>
    private static int? Weight(ACadSharp.LineWeightType w) => (short)w >= 0 ? (short)w : w == ACadSharp.LineWeightType.Default ? 25 : null;

    /// <summary>The weight of a layer: its own; null for the default.</summary>
    private static int? LayerWeight(ACadSharp.LineWeightType w) => (short)w >= 0 ? (short)w : null;

    /// <summary>The name of a line type; null for continuous, by layer and by block.</summary>
    private static string? TypeName(ACadSharp.Tables.LineType? lt) => lt == null || LineTypes.IsSolid(lt.Name) ? null : lt.Name;

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
