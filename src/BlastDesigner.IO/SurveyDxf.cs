using System.Globalization;
using ACadSharp;
using ACadSharp.IO;
using ACadSharp.Tables;
using Layer = ACadSharp.Tables.Layer;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using CSMath;
using Cad = ACadSharp.Entities;

namespace BlastDesigner.IO;

/// <summary>
/// Writes a synthetic survey (<see cref="SurveySynth"/>) as a DXF drawing in the scene's drawing style, so training data
/// goes through the same import as a real drawing, and carries the true classes over to the imported objects.
/// </summary>
public static class SurveyDxf
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static void Write(SurveyScene scene, string path)
    {
        var doc = new CadDocument();
        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        Layer LayerOf(string name)
        {
            if (layers.TryGetValue(name, out var l)) return l;
            if (!doc.Layers.TryGetValue(name, out l))
            {
                l = new Layer(name);
                doc.Layers.Add(l);
            }
            if (scene.LayerColors.TryGetValue(name, out var c)) l.Color = Aci(c);
            return layers[name] = l;
        }

        BlockRecord? picket = null;
        if (scene.PointStyle == SurveyPointStyle.Block)
        {
            picket = new BlockRecord("PIKET");
            picket.Entities.Add(new Cad.Circle { Radius = 0.25 });
            picket.Entities.Add(new Cad.AttributeDefinition { Tag = "НОМЕР", Height = 0.5, InsertPoint = new XYZ(0.4, 0.1, 0) });
            picket.Entities.Add(new Cad.AttributeDefinition { Tag = "ОТМ", Height = 0.5, InsertPoint = new XYZ(0.4, -0.6, 0) });
            doc.BlockRecords.Add(picket);
        }
        var comma = scene.Seed % 3 == 0;
        string Elevation(double z) { var s = z.ToString("0.00", Ci); return comma ? s.Replace('.', ',') : s; }

        int number = 1;
        foreach (var e in scene.Entities)
        {
            var layer = LayerOf(scene.Layers.TryGetValue(e.Id, out var n) ? n : "0");
            Cad.Entity? cad = null;
            switch (e)
            {
                case PolylineEntity pl when pl.Points.All(p => Math.Abs(p.Z) < 1e-9):
                    var lw = new Cad.LwPolyline(pl.Points.Select(p => new Cad.LwPolyline.Vertex(new XY(p.X, p.Y))).ToArray()) { IsClosed = pl.Closed };
                    cad = lw;
                    break;
                case PolylineEntity pl:
                    var p3 = new Cad.Polyline3D(pl.Points.Select(p => new XYZ(p.X, p.Y, p.Z)));
                    if (pl.Closed) p3.Flags |= Cad.PolylineFlags.ClosedPolylineOrClosedPolygonMeshInM;
                    cad = p3;
                    break;
                case PointEntity pt when scene.PointStyle == SurveyPointStyle.Block:
                    var ins = new Cad.Insert(picket!) { InsertPoint = new XYZ(pt.Position.X, pt.Position.Y, 0) };
                    ins.Attributes.Single(a => a.Tag == "НОМЕР").Value = (number++).ToString(Ci);
                    ins.Attributes.Single(a => a.Tag == "ОТМ").Value = Elevation(pt.Position.Z);
                    cad = ins;
                    break;
                case PointEntity pt when scene.PointStyle == SurveyPointStyle.Text:
                    // a flat point with its elevation written beside it
                    doc.Entities.Add(new Cad.TextEntity { Value = Elevation(pt.Position.Z), InsertPoint = new XYZ(pt.Position.X + 0.4, pt.Position.Y + 0.2, 0), Height = 0.6, Layer = layer });
                    cad = new Cad.Point(new XYZ(pt.Position.X, pt.Position.Y, 0));
                    break;
                case PointEntity pt:
                    cad = new Cad.Point(new XYZ(pt.Position.X, pt.Position.Y, pt.Position.Z));
                    break;
            }
            if (cad == null) continue;
            cad.Layer = layer;
            if (e.Color is uint ec) cad.Color = Aci(ec);
            doc.Entities.Add(cad);
        }
        using var w = new DxfWriter(path, doc, false);
        w.Write();
    }

    /// <summary>
    /// The true classes of imported objects, found by geometry (a line by its first and last vertex and vertex count, a
    /// point by its plan position). Objects with no match in the scene are counted in Unmatched.
    /// </summary>
    public static (Dictionary<Guid, SurveyLineClass> Lines, Dictionary<Guid, SurveyPointClass> Points, int Unmatched) Transfer(SurveyScene scene, IEnumerable<Entity> imported)
    {
        static (long, long) Key(double x, double y) => ((long)Math.Round(x * 1000), (long)Math.Round(y * 1000));
        var lineKeys = new Dictionary<((long, long), (long, long), int), SurveyLineClass>();
        foreach (var l in scene.Lines)
            lineKeys[(Key(l.Points[0].X, l.Points[0].Y), Key(l.Points[^1].X, l.Points[^1].Y), l.Points.Count)] = scene.LineClasses[l.Id];
        var pointKeys = new Dictionary<(long, long), SurveyPointClass>();
        foreach (var p in scene.Points) pointKeys[Key(p.Position.X, p.Position.Y)] = scene.PointClasses[p.Id];

        var lines = new Dictionary<Guid, SurveyLineClass>();
        var points = new Dictionary<Guid, SurveyPointClass>();
        var unmatched = 0;
        foreach (var e in imported)
        {
            switch (e)
            {
                case PolylineEntity pl when pl.Points.Count > 0:
                    if (lineKeys.TryGetValue((Key(pl.Points[0].X, pl.Points[0].Y), Key(pl.Points[^1].X, pl.Points[^1].Y), pl.Points.Count), out var lc)) lines[e.Id] = lc;
                    else unmatched++;
                    break;
                case PointEntity pt:
                    if (pointKeys.TryGetValue(Key(pt.Position.X, pt.Position.Y), out var pc)) points[e.Id] = pc;
                    else unmatched++;
                    break;
            }
        }
        return (lines, points, unmatched);
    }

    private static Color Aci(uint argb) => new(Color.ApproxIndex((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
