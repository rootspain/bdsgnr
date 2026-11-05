using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.IO;

/// <summary>Exchange formats added in version 6: KML/KMZ, LAS point clouds, IREDES drill plans, MWD logs and CSV reports.</summary>
public static class Interchange
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt = "0.###") => v.ToString(fmt, Inv);

    // ---- KML / KMZ -----------------------------------------------------------------------

    private static (double Lat, double Lon) ToGeo(SiteCoordinateSystem cs, Vec3 p) =>
        Utm.ToLatLon(p.X + cs.OffsetEasting, p.Y + cs.OffsetNorthing, cs.UtmZone, cs.SouthernHemisphere);

    private static string Coord(SiteCoordinateSystem cs, Vec3 p)
    {
        var (lat, lon) = ToGeo(cs, p);
        return $"{F(lon, "0.00000000")},{F(lat, "0.00000000")},{F(p.Z + cs.OffsetElevation, "0.###")}";
    }

    private static string Hex(uint argb) => $"{(argb >> 24) & 0xFF:x2}{argb & 0xFF:x2}{(argb >> 8) & 0xFF:x2}{(argb >> 16) & 0xFF:x2}";

    /// <summary>Writes holes as placemarks (collar points with a description) and polylines/polygons as line strings and polygons.</summary>
    public static string ExportKml(BlastProject project, IEnumerable<Entity> entities)
    {
        var cs = project.Coordinates;
        if (cs.UtmZone < 1 || cs.UtmZone > 60) throw new InvalidOperationException("Set the site coordinate system (UTM zone) first.");
        XNamespace k = "http://www.opengis.net/kml/2.2";
        var doc = new XElement(k + "Document", new XElement(k + "name", project.Header.Name));
        doc.Add(new XElement(k + "Style", new XAttribute("id", "hole"),
            new XElement(k + "IconStyle", new XElement(k + "scale", "0.6"), new XElement(k + "Icon", new XElement(k + "href", "http://maps.google.com/mapfiles/kml/shapes/placemark_circle.png")))));
        var holes = new XElement(k + "Folder", new XElement(k + "name", "Holes"));
        var lines = new XElement(k + "Folder", new XElement(k + "name", "Lines"));
        foreach (var e in entities)
        {
            switch (e)
            {
                case Hole h:
                    holes.Add(new XElement(k + "Placemark",
                        new XElement(k + "name", h.Label),
                        new XElement(k + "styleUrl", "#hole"),
                        new XElement(k + "description", $"Length {F(h.Length, "0.0")} m, diameter {F(h.Diameter, "0")} mm, angle {F(h.Angle, "0")}°, bearing {F(h.Bearing, "0")}°"),
                        new XElement(k + "Point", new XElement(k + "altitudeMode", "absolute"), new XElement(k + "coordinates", Coord(cs, h.Collar)))));
                    break;
                case PolylineEntity l when l.Points.Count >= 2:
                    var colour = Hex(l.Color ?? project.FindLayer(l.LayerId)?.Color ?? 0xFF000000);
                    var coords = string.Join(" ", (l.Closed ? l.Points.Append(l.Points[0]) : l.Points).Select(p => Coord(cs, p)));
                    XElement geom = l.Closed
                        ? new XElement(k + "Polygon", new XElement(k + "altitudeMode", "absolute"), new XElement(k + "outerBoundaryIs", new XElement(k + "LinearRing", new XElement(k + "coordinates", coords))))
                        : new XElement(k + "LineString", new XElement(k + "altitudeMode", "absolute"), new XElement(k + "coordinates", coords));
                    lines.Add(new XElement(k + "Placemark", new XElement(k + "name", l.Label.Length > 0 ? l.Label : l.KindName),
                        new XElement(k + "Style", new XElement(k + "LineStyle", new XElement(k + "color", colour), new XElement(k + "width", "2")),
                            new XElement(k + "PolyStyle", new XElement(k + "color", "33" + colour[2..]))), geom));
                    break;
                case PointEntity pt:
                    lines.Add(new XElement(k + "Placemark", new XElement(k + "name", pt.Label),
                        new XElement(k + "Point", new XElement(k + "altitudeMode", "absolute"), new XElement(k + "coordinates", Coord(cs, pt.Position)))));
                    break;
            }
        }
        doc.Add(holes, lines);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(k + "kml", doc)).ToString();
    }

    public static void WriteKmz(string path, string kml)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("doc.kml").Open(), new UTF8Encoding(false));
        w.Write(kml);
    }

    /// <summary>Reads points, line strings and polygons from KML or KMZ. When the project has no UTM zone it is taken from the first coordinate.</summary>
    public static List<Entity> ImportKml(string path, SiteCoordinateSystem cs)
    {
        string text;
        if (path.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("No KML document in the KMZ file.");
            using var r = new StreamReader(entry.Open());
            text = r.ReadToEnd();
        }
        else text = File.ReadAllText(path);
        var x = XDocument.Parse(text);
        var result = new List<Entity>();
        foreach (var pm in x.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            var name = pm.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? string.Empty;
            foreach (var g in pm.Descendants().Where(e => e.Name.LocalName is "Point" or "LineString" or "LinearRing"))
            {
                var coordText = g.Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value;
                if (coordText == null) continue;
                var pts = ParseKmlCoordinates(coordText, cs);
                if (pts.Count == 0) continue;
                if (g.Name.LocalName == "Point") result.Add(new PointEntity { Position = pts[0], Label = name });
                else
                {
                    var closed = g.Name.LocalName == "LinearRing";
                    if (closed && pts.Count > 1 && Vec3.Distance(pts[0], pts[^1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
                    result.Add(new PolylineEntity { Points = pts, Closed = closed, Label = name });
                }
            }
        }
        return result;
    }

    private static List<Vec3> ParseKmlCoordinates(string text, SiteCoordinateSystem cs)
    {
        var pts = new List<Vec3>();
        foreach (var tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var a = tuple.Split(',');
            if (a.Length < 2 || !double.TryParse(a[0], NumberStyles.Float, Inv, out var lon) || !double.TryParse(a[1], NumberStyles.Float, Inv, out var lat)) continue;
            var z = a.Length > 2 && double.TryParse(a[2], NumberStyles.Float, Inv, out var zz) ? zz : 0;
            if (cs.UtmZone < 1) { cs.UtmZone = Utm.ZoneOf(lon); cs.SouthernHemisphere = lat < 0; }
            var (e, n) = Utm.FromLatLon(lat, lon, cs.UtmZone);
            if (cs.SouthernHemisphere && lat >= 0) n += 10000000;
            pts.Add(new Vec3(e - cs.OffsetEasting, n - cs.OffsetNorthing, z - cs.OffsetElevation));
        }
        return pts;
    }

    // ---- LAS point clouds ----------------------------------------------------------------------

    public sealed record LasInfo(int VersionMajor, int VersionMinor, int PointFormat, long PointCount, Vec3 Min, Vec3 Max);

    /// <summary>
    /// Reads an uncompressed ASPRS LAS 1.0–1.4 file (point formats 0–10). Points are thinned to one per grid cell of
    /// <paramref name="cellSize"/> (lowest or highest point kept) so very large scans fit in memory.
    /// </summary>
    public static (LasInfo Info, List<Vec3> Points) ReadLas(string path, double cellSize = 0.5, bool keepLowest = false, int maxPoints = 2_000_000)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "LASF") throw new InvalidDataException("Not a LAS file.");
        fs.Position = 24;
        int major = br.ReadByte(), minor = br.ReadByte();
        fs.Position = 94;
        br.ReadUInt16(); // header size
        var offsetToPoints = br.ReadUInt32();
        br.ReadUInt32(); // number of variable length records
        var format = br.ReadByte();
        if ((format & 0xC0) != 0) throw new InvalidDataException("Compressed LAZ files are not supported; decompress to LAS first.");
        var recordLength = br.ReadUInt16();
        long count = br.ReadUInt32();
        fs.Position = 131;
        double sx = br.ReadDouble(), sy = br.ReadDouble(), sz = br.ReadDouble();
        double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
        double maxX = br.ReadDouble(), minX = br.ReadDouble(), maxY = br.ReadDouble(), minY = br.ReadDouble(), maxZ = br.ReadDouble(), minZ = br.ReadDouble();
        if (major == 1 && minor >= 4 && count == 0)
        {
            fs.Position = 247;
            count = (long)br.ReadUInt64();
        }
        var info = new LasInfo(major, minor, format, count, new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
        var cells = new Dictionary<(long, long), Vec3>();
        fs.Position = offsetToPoints;
        var buffer = new byte[recordLength];
        cellSize = Math.Max(1e-3, cellSize);
        for (long i = 0; i < count; i++)
        {
            if (fs.Read(buffer, 0, recordLength) < recordLength) break;
            var p = new Vec3(BitConverter.ToInt32(buffer, 0) * sx + ox, BitConverter.ToInt32(buffer, 4) * sy + oy, BitConverter.ToInt32(buffer, 8) * sz + oz);
            var key = ((long)Math.Floor(p.X / cellSize), (long)Math.Floor(p.Y / cellSize));
            if (!cells.TryGetValue(key, out var old) || (keepLowest ? p.Z < old.Z : p.Z > old.Z))
            {
                if (!cells.ContainsKey(key) && cells.Count >= maxPoints) continue;
                cells[key] = p;
            }
        }
        return (info, cells.Values.ToList());
    }

    // ---- IREDES drill plan ----------------------------------------------------------------------

    /// <summary>
    /// Writes a drill plan in the IREDES (International Rock Excavation Data Exchange Standard) XML layout:
    /// a general header followed by one Hole element per hole with start and end points, diameter and type.
    /// Element names follow the public drill-plan profile; validate against the rig vendor's schema before use.
    /// </summary>
    public static string ExportIredes(BlastProject project, IEnumerable<Hole> holes)
    {
        XNamespace ir = "http://www.iredes.org/xml";
        XNamespace dr = "http://www.iredes.org/xml/DrillRig";
        var plan = new XElement(dr + "DrillPlan",
            new XAttribute(XNamespace.Xmlns + "IR", ir), new XAttribute(XNamespace.Xmlns + "DR", dr),
            new XElement(ir + "GenHead",
                new XElement(ir + "FileCreateDate", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", Inv)),
                new XElement(ir + "IRversion", "V 1.0"),
                new XElement(ir + "IRDownloadRequest", "0")),
            new XElement(ir + "PlanId", project.Header.BlastNumber.Length > 0 ? project.Header.BlastNumber : project.Header.Name),
            new XElement(ir + "PlanName", project.Header.Name),
            new XElement(ir + "Comment", project.Header.Comment),
            new XElement(ir + "Project", project.Header.Mine),
            new XElement(ir + "WorkOrder", project.Header.Bench),
            new XElement(dr + "DrillPosPlan",
                new XElement(ir + "PositionData", new XElement(ir + "Coordsystem", new XElement(ir + "CoordSysName", project.Coordinates.UtmZone > 0 ? $"UTM{project.Coordinates.UtmZone}{(project.Coordinates.SouthernHemisphere ? "S" : "N")}" : "Local")))));
        var bitPlan = new XElement(dr + "DrillBitDia");
        var holesEl = new XElement(dr + "DrillPlanHoles");
        int n = 0;
        foreach (var h in holes.Where(h => !h.IsDummy))
        {
            n++;
            var toe = h.DesignToe;
            holesEl.Add(new XElement(dr + "Hole",
                new XElement(dr + "HoleId", n),
                new XElement(dr + "HoleName", h.Label),
                new XElement(dr + "StartPoint", new XElement(ir + "PointX", F(h.Collar.X)), new XElement(ir + "PointY", F(h.Collar.Y)), new XElement(ir + "PointZ", F(h.Collar.Z))),
                new XElement(dr + "EndPoint", new XElement(ir + "PointX", F(toe.X)), new XElement(ir + "PointY", F(toe.Y)), new XElement(ir + "PointZ", F(toe.Z))),
                new XElement(dr + "TypeOfHole", h.HoleType),
                new XElement(dr + "DrillBitDia", F(h.Diameter, "0.#")),
                new XElement(dr + "HoleLength", F(h.Length, "0.###"))));
        }
        bitPlan.Value = F(holes.Select(h => h.Diameter).DefaultIfEmpty(project.Design.Diameter).Average(), "0.#");
        plan.Add(bitPlan, holesEl);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), plan).ToString();
    }

    /// <summary>
    /// Reads holes from an IREDES drill plan or quality report. Matching is by local element names, so namespace prefixes and
    /// vendor extensions do not matter. Quality files (with as-drilled end points or lengths) set the actual collar and length.
    /// </summary>
    public static List<Hole> ImportIredes(string path, DesignData design)
    {
        var x = XDocument.Load(path);
        var result = new List<Hole>();
        static XElement? Child(XElement e, params string[] names) => e.Elements().FirstOrDefault(c => names.Contains(c.Name.LocalName));
        static double? Num(XElement? e) => e != null && double.TryParse(e.Value.Trim(), NumberStyles.Float, Inv, out var v) ? v : null;
        static Vec3? Point(XElement? e)
        {
            if (e == null) return null;
            var px = Num(e.Elements().FirstOrDefault(c => c.Name.LocalName is "PointX" or "X"));
            var py = Num(e.Elements().FirstOrDefault(c => c.Name.LocalName is "PointY" or "Y"));
            var pz = Num(e.Elements().FirstOrDefault(c => c.Name.LocalName is "PointZ" or "Z"));
            return px.HasValue && py.HasValue ? new Vec3(px.Value, py.Value, pz ?? 0) : null;
        }
        foreach (var e in x.Descendants().Where(d => d.Name.LocalName == "Hole"))
        {
            var start = Point(Child(e, "StartPoint", "StartPosition", "CollarPoint"));
            if (start == null) continue;
            var end = Point(Child(e, "EndPoint", "EndPosition", "ToePoint"));
            var h = HoleDesign.CreateHole(start.Value, HoleParameters.From(design));
            h.Label = Child(e, "HoleName")?.Value ?? Child(e, "HoleId")?.Value ?? string.Empty;
            if (end != null) h.SetToe(end.Value);
            else if (Num(Child(e, "HoleLength", "PlannedLength")) is double l) h.Length = l;
            if (Num(Child(e, "DrillBitDia", "HoleDiameter")) is double dia) h.Diameter = dia;
            if (int.TryParse(Child(e, "TypeOfHole")?.Value, out var type)) h.HoleType = Math.Clamp(type, 1, 8);
            if (Point(Child(e, "ActualStartPoint", "RealStartPoint")) is Vec3 actualStart) h.ActualCollar = actualStart;
            if (Num(Child(e, "ActualLength", "RealLength", "DrilledLength")) is double al) { h.ActualLength = al; h.State = HoleState.Drilled; }
            result.Add(h);
        }
        return result;
    }

    // ---- MWD logs ----------------------------------------------------------------------------

    /// <summary>
    /// Attaches measure-while-drilling samples from a delimited file with columns hole, depth, penetration rate and hardness
    /// (header names are matched case-insensitively; the first four columns are used otherwise). Returns the number of holes updated.
    /// </summary>
    public static int ImportMwd(IReadOnlyList<string> lines, IEnumerable<Hole> holes)
    {
        if (lines.Count == 0) return 0;
        var delim = new[] { ',', ';', '\t' }.OrderByDescending(c => lines[0].Count(x => x == c)).First();
        var header = TextFiles.SplitLine(lines[0], delim).Select(s => s.Trim().ToLowerInvariant()).ToArray();
        int Col(int fallback, params string[] names)
        {
            var i = Array.FindIndex(header, h => names.Any(n => h.Contains(n)));
            return i >= 0 ? i : fallback;
        }
        var hasHeader = header.Any(h => h.Length > 0 && !double.TryParse(h, NumberStyles.Float, Inv, out _) && (h.Contains("depth") || h.Contains("hole")));
        int ch = hasHeader ? Col(0, "hole", "name", "id") : 0;
        int cd = hasHeader ? Col(1, "depth") : 1;
        int cp = hasHeader ? Col(2, "rate", "rop", "penetration") : 2;
        int cx = hasHeader ? Col(3, "hard", "index", "energy") : 3;
        var byLabel = holes.GroupBy(h => h.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var samples = new Dictionary<Hole, List<MwdSample>>();
        foreach (var line in lines.Skip(hasHeader ? 1 : 0))
        {
            var a = TextFiles.SplitLine(line, delim);
            if (a.Length <= Math.Max(ch, cd) || !byLabel.TryGetValue(a[ch].Trim(), out var h)) continue;
            if (!double.TryParse(a[cd], NumberStyles.Float, Inv, out var depth)) continue;
            var s = new MwdSample
            {
                Depth = depth,
                PenetrationRate = a.Length > cp && double.TryParse(a[cp], NumberStyles.Float, Inv, out var rop) ? rop : 0,
                Hardness = a.Length > cx && double.TryParse(a[cx], NumberStyles.Float, Inv, out var hard) ? hard : 0
            };
            if (!samples.TryGetValue(h, out var list)) samples[h] = list = new();
            list.Add(s);
        }
        foreach (var (h, list) in samples) h.Mwd = list.OrderBy(s => s.Depth).ToList();
        return samples.Count;
    }

    // ---- CSV reports ----------------------------------------------------------------------------

    private static string Csv(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(v => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v)));
        return sb.ToString();
    }

    public static string ExportStrataIntercepts(IEnumerable<StrataIntercept> list) =>
        Csv(new[] { "Hole", "Surface", "Depth", "X", "Y", "Z" },
            list.Select(i => new[] { i.Hole.Label, i.Surface.Name, F(i.Depth, "0.00"), F(i.Point.X), F(i.Point.Y), F(i.Point.Z) }));

    public static string ExportTolerances(IEnumerable<ToleranceResult> list) =>
        Csv(new[] { "Hole", "Collar dev m", "Depth dev m", "Toe dev m", "Angle dev deg", "Bearing dev deg", "Result" },
            list.Select(t => new[] { t.Hole.Label, F(t.CollarDev, "0.00"), F(t.DepthDev, "0.00"), F(t.ToeDev, "0.00"), F(t.AngleDev, "0.0"), F(t.BearingDev, "0.0"), t.Pass ? "Pass" : string.Join("; ", t.Failures) }));

    public static string ExportFlyrock(Flyrock.Report r) =>
        Csv(new[] { "Hole", "kg/m", "Burden m", "Stemming m", "Face burst m", "Cratering m", "Rifling m", "Max m" },
            r.Holes.Select(h => new[] { h.Hole.Label, F(h.ChargePerMetre, "0.0"), F(h.Burden, "0.00"), F(h.Stemming, "0.00"), F(h.FaceBurst, "0"), F(h.Cratering, "0"), F(h.Rifling, "0"), F(h.Max, "0") }));

    /// <summary>The size distribution curves, the sizes in <paramref name="unit"/>.</summary>
    public static string ExportFragmentation(Fragmentation.Result r, SizeUnit unit = SizeUnit.Cm) =>
        Csv(new[] { "Size " + SizeUnits.Symbol(unit), "Kuz-Ram passing %", "Swebrec passing %" },
            r.Curve.Zip(r.Swebrec, (a, b) => new[] { F(SizeUnits.FromCm(a.SizeCm, unit), "0.###"), F(a.PassingPct, "0.00"), F(b.PassingPct, "0.00") }));

    /// <summary>Extended firing data: firing data plus row, horizon, detonator ID, explosive mass and collar/toe coordinates.</summary>
    public static string ExportExtendedFiringData(BlastProject project, IEnumerable<Hole> holes)
    {
        Timing.Solve(project);
        var rowOf = project.Rows.SelectMany(r => r.HoleIds.Select(id => (id, r.Name))).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().Name);
        return Csv(new[] { "Hole", "Row", "Horizon", "Detonator ID", "Firing ms", "Surface ms", "In-hole ms", "Sequence", "kg", "Collar X", "Collar Y", "Collar Z", "Toe X", "Toe Y", "Toe Z", "Cos X", "Cos Y", "Cos Z" },
            holes.OrderBy(h => h.FiringTimeMs ?? double.MaxValue).Select(h =>
            {
                var d = h.Direction;
                var toe = h.Toe;
                return new[]
                {
                    h.Label, rowOf.TryGetValue(h.Id, out var r) ? r : "", h.Horizon.ToString(Inv), h.DetonatorId,
                    h.FiringTimeMs is double ft ? F(ft, "0.#") : "", h.SurfaceTimeMs is double st ? F(st, "0.#") : "", F(h.InholeDelayMs, "0.#"),
                    h.FiringSequence?.ToString(Inv) ?? "", F(Charging.ExplosiveMass(project, h), "0.0"),
                    F(h.Collar.X), F(h.Collar.Y), F(h.Collar.Z), F(toe.X), F(toe.Y), F(toe.Z), F(d.X, "0.00000"), F(d.Y, "0.00000"), F(d.Z, "0.00000")
                };
            }));
    }

    /// <summary>Hole profile log export: one row per MWD sample.</summary>
    public static string ExportProfileLogs(IEnumerable<Hole> holes) =>
        Csv(new[] { "Hole", "Depth", "Penetration rate", "Hardness" },
            holes.Where(h => h.Mwd != null).SelectMany(h => h.Mwd!.Select(s => new[] { h.Label, F(s.Depth, "0.00"), F(s.PenetrationRate, "0.###"), F(s.Hardness, "0.###") })));
}
