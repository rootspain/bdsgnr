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
    /// <summary>
    /// The units of the file: metres and millimetres, or feet and inches (the coordinates, lengths and depths in feet, the
    /// diameter in inches). Null in a template from before it: metric.
    /// </summary>
    public UnitSystem? Units { get; set; }
    /// <summary>
    /// What the angles of the file count from: from vertical (0° vertical) or from horizontal (90° vertical; a dip of −75°
    /// read as 75°). Null in a template from before it: from vertical.
    /// </summary>
    public AngleReference? Angles { get; set; }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, ProjectJson.Indented));
    public static TextImportFormat Load(string path) => JsonSerializer.Deserialize<TextImportFormat>(File.ReadAllText(path), ProjectJson.Options) ?? new();

    /// <summary>Guesses delimiter and column fields from the header line.</summary>
    public static TextImportFormat Guess(IReadOnlyList<string> lines)
    {
        var f = new TextImportFormat();
        var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        f.Delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(c => first.Count(x => x == c)).First();
        var header = first.Split(f.Delimiter).Select(s => s.Trim().Trim('"').ToLowerInvariant()).ToList();
        // the units the program shows are those a file of the user is likeliest in; a dip counts from the horizontal
        f.Units = Measure.System;
        f.Angles = header.Contains("dip") ? AngleReference.FromHorizontal : Measure.Angle;
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
        // the file in feet and inches: its lengths to metres, its diameters to millimetres
        var imperial = fmt.Units == UnitSystem.Imperial;
        double lengths = imperial ? 0.3048 : 1, diameters = imperial ? 25.4 : 1;
        double? Num(string[] parts, ImportField field)
        {
            var idx = fmt.Columns.IndexOf(field);
            if (idx < 0 || idx >= parts.Length) return null;
            var s = parts[idx];
            if (fmt.DecimalSeparator == ",") s = s.Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, ci, out var v) ? v : null;
        }
        double? Length(string[] parts, ImportField field) => Num(parts, field) * lengths;
        // an angle of the file from vertical: one from horizontal is 90 less it (its sign, down or up, dropped)
        double? Angle(string[] parts) => Num(parts, ImportField.Angle) is double a ? fmt.Angles == AngleReference.FromHorizontal ? 90 - Math.Abs(a) : a : null;
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
            var x = Length(parts, ImportField.CollarX);
            var y = Length(parts, ImportField.CollarY);
            if (fmt.SwapXY) (x, y) = (y, x);
            if (x is null || y is null) { result.Skipped++; result.Warnings.Add($"Line {lineNo + fmt.SkipLines}: missing coordinates"); continue; }
            var p = new Vec3(x.Value, y.Value, Length(parts, ImportField.CollarZ) ?? 0);

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
                        Diameter = Num(parts, ImportField.Diameter) * diameters ?? design.Diameter,
                        Subdrill = Length(parts, ImportField.Subdrill) ?? design.Subdrill,
                        HoleType = (int)(Num(parts, ImportField.HoleType) ?? 1),
                        MaterialType = (int)(Num(parts, ImportField.MaterialType) ?? 1),
                        WaterDepth = Length(parts, ImportField.WaterDepth) ?? 0,
                        ActualLength = Length(parts, ImportField.ActualLength),
                        Notes = Str(parts, ImportField.Notes) ?? string.Empty
                    };
                    var tx = Length(parts, ImportField.ToeX);
                    var ty = Length(parts, ImportField.ToeY);
                    var tz = Length(parts, ImportField.ToeZ);
                    if (tx.HasValue && ty.HasValue && tz.HasValue)
                        hole.SetToe(new Vec3(tx.Value, ty.Value, tz.Value));
                    else
                    {
                        hole.Angle = Angle(parts) ?? design.Angle;
                        hole.Bearing = Num(parts, ImportField.Bearing) ?? design.Bearing;
                        if (Length(parts, ImportField.Length) is double len) hole.Length = len;
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

    // ---- the exports in the units of the program: metric files as before; imperial ones in feet, inches, pounds, with
    // the unit after the name of a column ("CollarX ft"), and an angle from horizontal as Dip
    private static bool Imperial => Measure.System == UnitSystem.Imperial;
    /// <summary>A kept value as the file writes it, in the units of the program.</summary>
    private static string F(Quantity q, double kept) => F(Measure.Show(q, kept));
    /// <summary>The name of a column of a quantity: with its unit in the imperial system.</summary>
    private static string H(string name, Quantity q) => Imperial ? name + " " + Measure.Symbol(q) : name;
    /// <summary>The column of the angle: Angle from vertical, Dip from horizontal (as the import reads them).</summary>
    private static string AngleHeader => Measure.Angle == AngleReference.FromHorizontal ? "Dip" : "Angle";
    /// <summary>The columns of a quantity in a list of names ("CollarX,CollarY").</summary>
    private static string Hs(Quantity q, params string[] names) => string.Join(",", names.Select(n => H(n, q)));
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>A calculated value for a CSV cell: invariant number, the text of a string, empty for an error or a missing value.</summary>
    private static string CalcValue(BlastProject project, string propertyId, Hole h) =>
        CalcService.Value(project, propertyId, h) switch
        {
            { IsError: true } => "",
            { Object: bool b } => b ? "1" : "0",
            { Object: string s } => Csv(s),
            var v => F(CalcService.InUnits(v.Number, CalcService.UnitOf(project, propertyId)).Value)
        };

    /// <summary>Hole table export (File ▸ Export ▸ Text file).</summary>
    public static string ExportHoles(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder();
        // the calculated properties chosen as columns of the hole table follow the fixed columns, named by their ids
        var calc = project.View.CalcColumns ?? new List<string>();
        sb.AppendLine("Label," + Hs(Quantity.Length, "CollarX", "CollarY", "CollarZ", "ToeX", "ToeY", "ToeZ") + "," + AngleHeader + ",Bearing," + H("Length", Quantity.Length) + "," + H("Diameter", Quantity.Diameter)
            + "," + H("Subdrill", Quantity.Length) + ",HoleType,State," + (Imperial ? "Explosive lb" : "ExplosiveKg") + ",FiringTime" + string.Concat(calc.Select(c => "," + Csv(c))));
        foreach (var h in holes)
        {
            var toe = h.Toe;
            sb.AppendLine(string.Join(",", Csv(h.Label), F(Quantity.Length, h.Collar.X), F(Quantity.Length, h.Collar.Y), F(Quantity.Length, h.Collar.Z), F(Quantity.Length, toe.X), F(Quantity.Length, toe.Y), F(Quantity.Length, toe.Z), F(Quantity.HoleAngle, h.Angle), F(h.Bearing),
                F(Quantity.Length, h.EffectiveLength), F(Quantity.Diameter, h.Diameter), F(Quantity.Length, h.Subdrill), h.HoleType, h.State, F(Quantity.Mass, Charging.ExplosiveMass(project, h)), h.FiringTimeMs is double t ? F(t) : "")
                + string.Concat(calc.Select(c => "," + CalcValue(project, c, h))));
        }
        return sb.ToString();
    }

    public static string ExportPoints(IEnumerable<Entity> entities)
    {
        var sb = new StringBuilder("Id," + Hs(Quantity.Length, "X", "Y", "Z") + ",Label\n");
        int sid = 1;
        foreach (var e in entities)
        {
            var label = e switch { PointEntity p => p.Label, PolylineEntity l => l.Label, TextEntity t => t.Text, _ => e.KindName };
            foreach (var v in e.GetVertices()) sb.AppendLine($"{sid},{F(Quantity.Length, v.X)},{F(Quantity.Length, v.Y)},{F(Quantity.Length, v.Z)},{Csv(label)}");
            sid++;
        }
        return sb.ToString();
    }

    /// <summary>One row per deck (File ▸ Export ▸ Deck table).</summary>
    public static string ExportDeckTable(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder("Hole,Deck," + Hs(Quantity.Length, "Top", "Bottom", "Length") + ",Product," + (Imperial ? "Mass lb" : "MassKg") + ",Primers\n");
        foreach (var h in holes)
        {
            int i = 1;
            foreach (var d in h.Decks.OrderBy(d => d.Top))
                sb.AppendLine(string.Join(",", Csv(h.Label), i++, F(Quantity.Length, d.Top), F(Quantity.Length, d.Bottom), F(Quantity.Length, d.Length), Csv(project.FindProduct(d.ProductId)?.Name ?? ""),
                    F(Quantity.Mass, Charging.DeckMass(project, h, d)), d.Primers.Count));
        }
        return sb.ToString();
    }

    /// <summary>Firing data: hole, coordinates, times; optionally direction cosines of the hole axis.</summary>
    public static string ExportFiringData(BlastProject project, IEnumerable<Hole> holes, bool cosines)
    {
        Timing.Solve(project);
        var sb = new StringBuilder("Hole," + Hs(Quantity.Length, "X", "Y", "Z") + ",SurfaceTime,InholeDelay,FiringTime,Sequence," + (Imperial ? "Charge lb" : "ChargeKg") + (cosines ? ",CosX,CosY,CosZ," + H("Length", Quantity.Length) : "") + "\n");
        foreach (var h in holes.Where(h => h.FiringTimeMs.HasValue).OrderBy(h => h.FiringTimeMs))
        {
            var line = string.Join(",", Csv(h.Label), F(Quantity.Length, h.Collar.X), F(Quantity.Length, h.Collar.Y), F(Quantity.Length, h.Collar.Z), h.SurfaceTimeMs is double s ? F(s) : "", F(h.InholeDelayMs), F(h.FiringTimeMs!.Value), h.FiringSequence, F(Quantity.Mass, Charging.ExplosiveMass(project, h)));
            if (cosines) { var d = h.Direction; line += $",{d.X:0.00000},{d.Y:0.00000},{d.Z:0.00000},{F(Quantity.Length, h.EffectiveLength)}"; }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    public static string ExportQuantities(QuantityReport q)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Item,Value,Unit");
        sb.AppendLine($"Holes,{q.HoleCount},ea");
        sb.AppendLine($"Drilled,{F(Quantity.Length, q.DrilledMetres)},{Measure.Symbol(Quantity.Length)}");
        sb.AppendLine($"Volume,{F(Quantity.Volume, q.Volume)},{(Imperial ? "yd3" : "m3")}");
        sb.AppendLine($"{(Imperial ? "Short tons" : "Tonnes")},{F(Quantity.Tonnage, q.Tonnes)},{Measure.Symbol(Quantity.Tonnage)}");
        sb.AppendLine($"Explosive,{F(Quantity.Mass, q.ExplosiveKg)},{Measure.Symbol(Quantity.Mass)}");
        sb.AppendLine(Imperial ? $"Powder factor,{Measure.Show(Quantity.PowderFactor, q.PowderFactorKgM3).ToString("0.000", CultureInfo.InvariantCulture)},lb/yd3" : $"Powder factor,{q.PowderFactorKgM3:0.000},kg/m3");
        sb.AppendLine(Imperial ? $"Powder factor,{(q.PowderFactorKgT * 2).ToString("0.000", CultureInfo.InvariantCulture)},lb/st" : $"Powder factor,{q.PowderFactorKgT:0.000},kg/t");
        sb.AppendLine($"Max charge per delay,{F(Quantity.Mass, q.MaxInstantaneousCharge)},{Measure.Symbol(Quantity.Mass)}");
        sb.AppendLine($"Total cost,{q.TotalCost:0.00},");
        sb.AppendLine();
        sb.AppendLine("Product,Amount,Unit,Cost");
        foreach (var p in q.Products) sb.AppendLine($"{Csv(p.Product.Name)},{F(p.Amount)},{p.Unit},{p.Cost:0.00}");
        if (q.Segments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Imperial ? "Pattern,Segment,Holes,Drilled ft,Volume yd3,Explosive lb" : "Pattern,Segment,Holes,Drilled m,Volume m3,Explosive kg");
            foreach (var s in q.Segments) sb.AppendLine($"{Csv(s.Pattern.Name)},{Csv(s.Segment.Name)},{s.Holes},{F(Quantity.Length, s.DrilledMetres)},{F(Quantity.Volume, s.Volume)},{F(Quantity.Mass, s.ExplosiveKg)}");
        }
        return sb.ToString();
    }

    /// <summary>Load sheet for the charging crew: one row per hole with depths, water and products in order.</summary>
    public static string ExportLoadSheet(BlastProject project, IEnumerable<Hole> holes)
    {
        var sb = new StringBuilder("Hole," + Hs(Quantity.Length, "Length", "Dipped", "Water", "Stemming") + ",Column (bottom→top),Primers," + (Imperial ? "Explosive lb" : "ExplosiveKg") + "\n");
        var unit = Imperial ? "ft" : "m";
        foreach (var h in holes.OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase))
        {
            var column = string.Join(" | ", h.Decks.OrderByDescending(d => d.Bottom).Select(d => $"{project.FindProduct(d.ProductId)?.Name ?? "?"} {Measure.Show(Quantity.Length, d.Length).ToString("0.0", CultureInfo.InvariantCulture)}{unit}"));
            var primers = string.Join(" | ", Charging.Primers(h).Select(p => $"{Measure.Show(Quantity.Length, p.Primer.Depth).ToString("0.0", CultureInfo.InvariantCulture)}{unit} {project.FindProduct(p.Primer.BoosterId)?.Name} {project.FindProduct(p.Primer.DetonatorId)?.Name}"));
            sb.AppendLine(string.Join(",", Csv(h.Label), F(Quantity.Length, h.Length), h.ActualLength is double a ? F(Quantity.Length, a) : "", F(Quantity.Length, h.WaterDepth), F(Quantity.Length, Charging.TopStemming(project, h)), Csv(column), Csv(primers), F(Quantity.Mass, Charging.ExplosiveMass(project, h))));
        }
        return sb.ToString();
    }
}
