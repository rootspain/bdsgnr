using System.Globalization;
using System.Text;
using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.IO;

public enum ImportDataKind { Holes, Points, Polyline, Polygon }

/// <summary>Fields that can be mapped to text columns.</summary>
public enum ImportField
{
    Ignore,
    Label,
    CollarX,
    CollarY,
    CollarZ,
    ToeX,
    ToeY,
    ToeZ,
    Angle,
    Bearing,
    Length,
    Diameter,
    Subdrill,
    HoleType,
    MaterialType,
    WaterDepth,
    ActualLength,
    Time,
    Notes,
    /// <summary>For polylines: a change in this column starts a new string.</summary>
    StringId
}

/// <summary>Column layout of a delimited text file; can be saved and reused as an import template.</summary>
public sealed class TextImportFormat
{
    public char Delimiter { get; set; } = ',';
    public int SkipLines { get; set; } = 1;
    public ImportDataKind Kind { get; set; } = ImportDataKind.Holes;
    public List<ImportField> Columns { get; set; } = new();
    public bool SkipDuplicates { get; set; } = true;
    public double DuplicateTolerance { get; set; } = 0.2;
    public bool SwapXY { get; set; }
    public string DecimalSeparator { get; set; } = ".";

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, ProjectJson.Indented));
    public static TextImportFormat Load(string path) => JsonSerializer.Deserialize<TextImportFormat>(File.ReadAllText(path), ProjectJson.Options) ?? new();

    /// <summary>Guesses delimiter and column fields from the header line.</summary>
    public static TextImportFormat Guess(IReadOnlyList<string> lines)
    {
        var f = new TextImportFormat();
        var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        f.Delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(c => first.Count(x => x == c)).First();
        var header = first.Split(f.Delimiter).Select(s => s.Trim().Trim('"').ToLowerInvariant()).ToList();
        f.SkipLines = header.Any(h => h.Length > 0 && !double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? 1 : 0;
        foreach (var h in header)
            f.Columns.Add(h switch
            {
                "id" or "hole" or "name" or "label" or "holeid" or "hole id" => ImportField.Label,
                "x" or "east" or "easting" or "collarx" or "collar x" => ImportField.CollarX,
                "y" or "north" or "northing" or "collary" or "collar y" => ImportField.CollarY,
                "z" or "rl" or "elev" or "elevation" or "collarz" or "collar z" => ImportField.CollarZ,
                "toex" or "toe x" => ImportField.ToeX,
                "toey" or "toe y" => ImportField.ToeY,
                "toez" or "toe z" => ImportField.ToeZ,
                "angle" or "dip" or "incl" or "inclination" => ImportField.Angle,
                "bearing" or "azimuth" or "azi" => ImportField.Bearing,
                "length" or "depth" or "len" => ImportField.Length,
                "diameter" or "dia" or "diam" => ImportField.Diameter,
                "subdrill" or "sub" => ImportField.Subdrill,
                "type" or "holetype" => ImportField.HoleType,
                "water" => ImportField.WaterDepth,
                "time" or "delay" => ImportField.Time,
                _ => ImportField.Ignore
            });
        if (f.Columns.All(c => c == ImportField.Ignore) && f.SkipLines == 0)
        {
            f.Columns = new List<ImportField> { ImportField.CollarX, ImportField.CollarY, ImportField.CollarZ };
            f.Kind = ImportDataKind.Points;
        }
        return f;
    }
}

public sealed class ImportResult
{
    public List<Entity> Entities { get; } = new();
    public int Skipped { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>Delimited text import and export.</summary>
public static class TextFiles
{
    public static string[] SplitLine(string line, char delimiter)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == delimiter && !quoted) { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString().Trim());
        return parts.ToArray();
    }

    public static ImportResult Import(IReadOnlyList<string> lines, TextImportFormat fmt, DesignData design, IEnumerable<Hole>? existing = null)
    {
        var result = new ImportResult();
        var ci = CultureInfo.InvariantCulture;
        double? Num(string[] parts, ImportField field)
        {
            var idx = fmt.Columns.IndexOf(field);
            if (idx < 0 || idx >= parts.Length) return null;
            var s = parts[idx];
            if (fmt.DecimalSeparator == ",") s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, ci, out var v) ? v : null;
        }
        string? Str(string[] parts, ImportField field)
        {
            var idx = fmt.Columns.IndexOf(field);
            return idx >= 0 && idx < parts.Length ? parts[idx] : null;
        }

        var known = (existing ?? Enumerable.Empty<Hole>()).ToList();
        List<Vec3>? currentLine = null;
        string? currentString = null;
        void FlushLine()
        {
            if (currentLine is { Count: > 1 })
                result.Entities.Add(new PolylineEntity { Points = currentLine, Closed = fmt.Kind == ImportDataKind.Polygon, Label = currentString ?? string.Empty });
            currentLine = null;
        }

        int lineNo = 0;
        foreach (var raw in lines.Skip(fmt.SkipLines))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = SplitLine(raw, fmt.Delimiter);
            var x = Num(parts, ImportField.CollarX);
            var y = Num(parts, ImportField.CollarY);
            if (fmt.SwapXY) (x, y) = (y, x);
            if (x is null || y is null) { result.Skipped++; result.Warnings.Add($"Line {lineNo + fmt.SkipLines}: missing coordinates"); continue; }
            var p = new Vec3(x.Value, y.Value, Num(parts, ImportField.CollarZ) ?? 0);

            switch (fmt.Kind)
            {
                case ImportDataKind.Points:
                    result.Entities.Add(new PointEntity { Position = p, Label = Str(parts, ImportField.Label) ?? string.Empty });
                    break;
                case ImportDataKind.Polyline:
                case ImportDataKind.Polygon:
                    var sid = Str(parts, ImportField.StringId);
                    if (currentLine == null || sid != currentString) { FlushLine(); currentLine = new List<Vec3>(); currentString = sid; }
                    currentLine.Add(p);
                    break;
                case ImportDataKind.Holes:
                    if (fmt.SkipDuplicates && known.Any(h => Vec3.Distance2D(h.Collar, p) <= fmt.DuplicateTolerance)) { result.Skipped++; continue; }
                    var hole = new Hole
                    {
                        Collar = p,
                        Label = Str(parts, ImportField.Label) ?? (known.Count + 1).ToString(ci),
                        Diameter = Num(parts, ImportField.Diameter) ?? design.Diameter,
                        Subdrill = Num(parts, ImportField.Subdrill) ?? design.Subdrill,
                        HoleType = (int)(Num(parts, ImportField.HoleType) ?? 1),
                        MaterialType = (int)(Num(parts, ImportField.MaterialType) ?? 1),
                        WaterDepth = Num(parts, ImportField.WaterDepth) ?? 0,
                        ActualLength = Num(parts, ImportField.ActualLength),
                        Notes = Str(parts, ImportField.Notes) ?? string.Empty
                    };
                    var tx = Num(parts, ImportField.ToeX);
                    var ty = Num(parts, ImportField.ToeY);
                    var tz = Num(parts, ImportField.ToeZ);
                    if (tx.HasValue && ty.HasValue && tz.HasValue)
                        hole.SetToe(new Vec3(tx.Value, ty.Value, tz.Value));
                    else
                    {
                        hole.Angle = Num(parts, ImportField.Angle) ?? design.Angle;
                        hole.Bearing = Num(parts, ImportField.Bearing) ?? design.Bearing;
                        if (Num(parts, ImportField.Length) is double len) hole.Length = len;
                        else if (tz.HasValue) hole.Length = (p.Z - tz.Value) / Math.Max(0.05, Math.Cos(hole.Angle * GeoMath.Deg));
                        else HoleDesign.SetLengthToGrade(hole, null, design.BenchHeight);
                    }
                    if (Num(parts, ImportField.Time) is double t) hole.ElectronicTimeMs = t;
                    known.Add(hole);
                    result.Entities.Add(hole);
                    break;
            }
        }
        FlushLine();
        return result;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>A calculated value for a CSV cell: invariant number, the text of a string, empty for an error or a missing value.</summary>
    private static string CalcValue(BlastProject project, string propertyId, Hole h) =>
        CalcService.Value(project, propertyId, h) switch
        {
            { IsError: true } => "",
            { Object: bool b } => b ? "1" : "0",
            { Object: string s } => Csv(s),
            var v => F(v.Number)
        };

    /// <summary>Hole table export (File ▸ Export ▸ Text file).</summary>
    public static string ExportHoles(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder();
        // the calculated properties chosen as columns of the hole table follow the fixed columns, named by their ids
        var calc = project.View.CalcColumns ?? new List<string>();
        sb.AppendLine("Label,CollarX,CollarY,CollarZ,ToeX,ToeY,ToeZ,Angle,Bearing,Length,Diameter,Subdrill,HoleType,State,ExplosiveKg,FiringTime" + string.Concat(calc.Select(c => "," + Csv(c))));
        foreach (var h in holes)
        {
            var toe = h.Toe;
            sb.AppendLine(string.Join(",", Csv(h.Label), F(h.Collar.X), F(h.Collar.Y), F(h.Collar.Z), F(toe.X), F(toe.Y), F(toe.Z), F(h.Angle), F(h.Bearing),
                F(h.EffectiveLength), F(h.Diameter), F(h.Subdrill), h.HoleType, h.State, F(Charging.ExplosiveMass(project, h)), h.FiringTimeMs is double t ? F(t) : "")
                + string.Concat(calc.Select(c => "," + CalcValue(project, c, h))));
        }
        return sb.ToString();
    }

    public static string ExportPoints(IEnumerable<Entity> entities)
    {
        var sb = new StringBuilder("Id,X,Y,Z,Label\n");
        int sid = 1;
        foreach (var e in entities)
        {
            var label = e switch { PointEntity p => p.Label, PolylineEntity l => l.Label, TextEntity t => t.Text, _ => e.KindName };
            foreach (var v in e.GetVertices()) sb.AppendLine($"{sid},{F(v.X)},{F(v.Y)},{F(v.Z)},{Csv(label)}");
            sid++;
        }
        return sb.ToString();
    }

    /// <summary>One row per deck (File ▸ Export ▸ Deck table).</summary>
    public static string ExportDeckTable(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder("Hole,Deck,Top,Bottom,Length,Product,MassKg,Primers\n");
        foreach (var h in holes)
        {
            int i = 1;
            foreach (var d in h.Decks.OrderBy(d => d.Top))
                sb.AppendLine(string.Join(",", Csv(h.Label), i++, F(d.Top), F(d.Bottom), F(d.Length), Csv(project.FindProduct(d.ProductId)?.Name ?? ""),
                    F(Charging.DeckMass(project, h, d)), d.Primers.Count));
        }
        return sb.ToString();
    }

    /// <summary>Firing data: hole, coordinates, times; optionally direction cosines of the hole axis.</summary>
    public static string ExportFiringData(BlastProject project, IEnumerable<Hole> holes, bool cosines)
    {
        Timing.Solve(project);
        var sb = new StringBuilder("Hole,X,Y,Z,SurfaceTime,InholeDelay,FiringTime,Sequence,ChargeKg" + (cosines ? ",CosX,CosY,CosZ,Length" : "") + "\n");
        foreach (var h in holes.Where(h => h.FiringTimeMs.HasValue).OrderBy(h => h.FiringTimeMs))
        {
            var line = string.Join(",", Csv(h.Label), F(h.Collar.X), F(h.Collar.Y), F(h.Collar.Z), h.SurfaceTimeMs is double s ? F(s) : "", F(h.InholeDelayMs), F(h.FiringTimeMs!.Value), h.FiringSequence, F(Charging.ExplosiveMass(project, h)));
            if (cosines) { var d = h.Direction; line += $",{d.X:0.00000},{d.Y:0.00000},{d.Z:0.00000},{F(h.EffectiveLength)}"; }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    public static string ExportQuantities(QuantityReport q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Item,Value,Unit");
        sb.AppendLine($"Holes,{q.HoleCount},ea");
        sb.AppendLine($"Drilled,{F(q.DrilledMetres)},m");
        sb.AppendLine($"Volume,{F(q.Volume)},m3");
        sb.AppendLine($"Tonnes,{F(q.Tonnes)},t");
        sb.AppendLine($"Explosive,{F(q.ExplosiveKg)},kg");
        sb.AppendLine($"Powder factor,{q.PowderFactorKgM3:0.000},kg/m3");
        sb.AppendLine($"Powder factor,{q.PowderFactorKgT:0.000},kg/t");
        sb.AppendLine($"Max charge per delay,{F(q.MaxInstantaneousCharge)},kg");
        sb.AppendLine($"Total cost,{q.TotalCost:0.00},");
        sb.AppendLine();
        sb.AppendLine("Product,Amount,Unit,Cost");
        foreach (var p in q.Products) sb.AppendLine($"{Csv(p.Product.Name)},{F(p.Amount)},{p.Unit},{p.Cost:0.00}");
        return sb.ToString();
    }

    /// <summary>Load sheet for the charging crew: one row per hole with depths, water and products in order.</summary>
    public static string ExportLoadSheet(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder("Hole,Length,Dipped,Water,Stemming,Column (bottom→top),Primers,ExplosiveKg\n");
        foreach (var h in holes.OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase))
        {
            var column = string.Join(" | ", h.Decks.OrderByDescending(d => d.Bottom).Select(d => $"{project.FindProduct(d.ProductId)?.Name ?? "?"} {d.Length:0.0}m"));
            var primers = string.Join(" | ", Charging.Primers(h).Select(p => $"{p.Primer.Depth:0.0}m {project.FindProduct(p.Primer.BoosterId)?.Name} {project.FindProduct(p.Primer.DetonatorId)?.Name}"));
            sb.AppendLine(string.Join(",", Csv(h.Label), F(h.Length), h.ActualLength is double a ? F(a) : "", F(h.WaterDepth), F(Charging.TopStemming(project, h)), Csv(column), Csv(primers), F(Charging.ExplosiveMass(project, h))));
        }
        return sb.ToString();
    }
}
