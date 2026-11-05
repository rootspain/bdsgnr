using System.Text.Json.Serialization;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>Any drawable object of a blast project. Every entity lives on exactly one layer.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Hole), "hole")]
[JsonDerivedType(typeof(PointEntity), "point")]
[JsonDerivedType(typeof(PolylineEntity), "polyline")]
[JsonDerivedType(typeof(TextEntity), "text")]
[JsonDerivedType(typeof(CircleEntity), "circle")]
[JsonDerivedType(typeof(ArrowEntity), "arrow")]
[JsonDerivedType(typeof(DimensionEntity), "dimension")]
[JsonDerivedType(typeof(SurfaceEntity), "surface")]
[JsonDerivedType(typeof(TieEntity), "tie")]
[JsonDerivedType(typeof(InitiationPoint), "initiation")]
[JsonDerivedType(typeof(TimingLineEntity), "timingline")]
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LayerId { get; set; }
    /// <summary>Optional group the entity belongs to (Group / Ungroup).</summary>
    public Guid? GroupId { get; set; }
    /// <summary>Optional colour override (ARGB); null means use the layer colour.</summary>
    public uint? Color { get; set; }

    [JsonIgnore] public abstract string KindName { get; }

    public abstract IEnumerable<Vec3> GetVertices();

    public virtual Bounds3 GetBounds()
    {
        var b = Bounds3.Empty;
        foreach (var v in GetVertices()) b.Add(v);
        return b;
    }

    /// <summary>Moves the entity by a vector.</summary>
    public abstract void Translate(Vec3 delta);

    /// <summary>Applies an arbitrary point transform to every vertex.</summary>
    public abstract void Transform(Func<Vec3, Vec3> f);

    /// <summary>Deep copy with a fresh id.</summary>
    public Entity CloneWithNewId()
    {
        var c = ProjectJson.DeepClone<Entity>(this);
        c.Id = Guid.NewGuid();
        return c;
    }

    /// <summary>Distance in plan from p to the entity's drawn geometry, used for picking.</summary>
    public virtual double PickDistance(Vec3 p) =>
        GetVertices().Select(v => Vec3.Distance2D(v, p)).DefaultIfEmpty(double.MaxValue).Min();
}

public enum PointKind
{
    General,
    Survey,
    GradePeg,
    Monitoring,
    SurveyStation,
    NorthPoint,
    CentreOfMass
}

public sealed class PointEntity : Entity
{
    public Vec3 Position { get; set; }
    public PointKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;

    public override string KindName => Kind switch
    {
        PointKind.Monitoring => "Monitoring point",
        PointKind.GradePeg => "Grade peg",
        PointKind.SurveyStation => "Survey station",
        PointKind.NorthPoint => "North point",
        _ => "Point"
    };

    public override IEnumerable<Vec3> GetVertices() { yield return Position; }
    public override void Translate(Vec3 d) => Position += d;
    public override void Transform(Func<Vec3, Vec3> f) => Position = f(Position);
}

public enum LineKind
{
    General,
    Boundary,
    Crest,
    Toe,
    Baseline,
    Breakline,
    CrossSection,
    Contour,
    MaterialBoundary,
    ClearanceZone,
    BenchLayout,
    BoreTrack,
    Intersection
}

/// <summary>3D polyline; when <see cref="Closed"/> is set it is a polygon.</summary>
public sealed class PolylineEntity : Entity
{
    public List<Vec3> Points { get; set; } = new();
    public bool Closed { get; set; }
    public LineKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;

    public override string KindName => Closed ? "Polygon" : "Polyline";
    public override IEnumerable<Vec3> GetVertices() => Points;
    public override void Translate(Vec3 d) { for (int i = 0; i < Points.Count; i++) Points[i] += d; }
    public override void Transform(Func<Vec3, Vec3> f) { for (int i = 0; i < Points.Count; i++) Points[i] = f(Points[i]); }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(Points, p, Closed);

    [JsonIgnore] public double Length => GeoMath.Length(Points, Closed);
    [JsonIgnore] public double Area => Closed ? GeoMath.Area(Points) : 0;
}

public sealed class TextEntity : Entity
{
    public Vec3 Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public double Height { get; set; } = 1.0;
    public double Rotation { get; set; }

    public override string KindName => "Text";
    public override IEnumerable<Vec3> GetVertices() { yield return Position; }
    public override void Translate(Vec3 d) => Position += d;
    public override void Transform(Func<Vec3, Vec3> f) => Position = f(Position);
}

public sealed class CircleEntity : Entity
{
    public Vec3 Center { get; set; }
    public double Radius { get; set; } = 1;

    public override string KindName => "Circle";
    public override IEnumerable<Vec3> GetVertices() { yield return Center; }
    public override Bounds3 GetBounds()
    {
        var b = Bounds3.Empty;
        b.Add(Center - new Vec3(Radius, Radius, 0));
        b.Add(Center + new Vec3(Radius, Radius, 0));
        return b;
    }
    public override void Translate(Vec3 d) => Center += d;
    public override void Transform(Func<Vec3, Vec3> f) => Center = f(Center);
    public override double PickDistance(Vec3 p) => Math.Abs(Vec3.Distance2D(Center, p) - Radius);

    public List<Vec3> ToPolygon(int segments = 36) =>
        Enumerable.Range(0, segments)
            .Select(i => Center + new Vec3(Math.Cos(2 * Math.PI * i / segments) * Radius, Math.Sin(2 * Math.PI * i / segments) * Radius, 0))
            .ToList();
}

public sealed class ArrowEntity : Entity
{
    public Vec3 Start { get; set; }
    public Vec3 End { get; set; }

    public override string KindName => "Arrow";
    public override IEnumerable<Vec3> GetVertices() { yield return Start; yield return End; }
    public override void Translate(Vec3 d) { Start += d; End += d; }
    public override void Transform(Func<Vec3, Vec3> f) { Start = f(Start); End = f(End); }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(new[] { Start, End }, p, false);
}

public sealed class DimensionEntity : Entity
{
    public Vec3 Start { get; set; }
    public Vec3 End { get; set; }
    /// <summary>Perpendicular offset of the dimension line in plan.</summary>
    public double Offset { get; set; } = 2;

    public override string KindName => "Dimension";
    [JsonIgnore] public double Distance => Vec3.Distance(Start, End);
    public override IEnumerable<Vec3> GetVertices() { yield return Start; yield return End; }
    public override void Translate(Vec3 d) { Start += d; End += d; }
    public override void Transform(Func<Vec3, Vec3> f) { Start = f(Start); End = f(End); }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(new[] { Start, End }, p, false);
}

/// <summary>Role of a surface. Stored by name, so new kinds go anywhere in the list; <see cref="Face"/>: the bench faces of a survey.</summary>
public enum SurfaceKind { General, Bench, Grade, Face, Topography, Design, Strata, PointCloud }

/// <summary>Triangulated surface (TIN). <see cref="Triangles"/> holds vertex index triples.</summary>
public sealed class SurfaceEntity : Entity
{
    public string Name { get; set; } = "Surface";
    public SurfaceKind Kind { get; set; }
    public List<Vec3> Vertices { get; set; } = new();
    public List<int> Triangles { get; set; } = new();
    public bool ShowWireframe { get; set; } = true;

    public override string KindName => "Surface";
    [JsonIgnore] public int TriangleCount => Triangles.Count / 3;
    public override IEnumerable<Vec3> GetVertices() => Vertices;
    public override void Translate(Vec3 d) { for (int i = 0; i < Vertices.Count; i++) Vertices[i] += d; InvalidateIndex(); }
    public override void Transform(Func<Vec3, Vec3> f) { for (int i = 0; i < Vertices.Count; i++) Vertices[i] = f(Vertices[i]); InvalidateIndex(); }

    public override double PickDistance(Vec3 p) => ElevationAt(p.X, p.Y).HasValue ? 0.5 : double.MaxValue;

    // Plan-grid index of triangles, built lazily for fast elevation queries.
    private Dictionary<(int, int), List<int>>? _index;
    private double _cell, _ox, _oy;
    private int _indexedTriangles = -1;

    public void InvalidateIndex() => _index = null;

    private void EnsureIndex()
    {
        if (_index != null && _indexedTriangles == Triangles.Count) return;
        _index = new Dictionary<(int, int), List<int>>();
        _indexedTriangles = Triangles.Count;
        if (Vertices.Count == 0) return;
        var b = GetBounds();
        _ox = b.Min.X;
        _oy = b.Min.Y;
        var area = Math.Max(1e-6, (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y));
        _cell = Math.Max(1e-3, Math.Sqrt(area / Math.Max(1, TriangleCount)) * 2);
        for (int t = 0; t < Triangles.Count; t += 3)
        {
            var a = Vertices[Triangles[t]];
            var bb = Vertices[Triangles[t + 1]];
            var c = Vertices[Triangles[t + 2]];
            int x0 = (int)Math.Floor((Math.Min(a.X, Math.Min(bb.X, c.X)) - _ox) / _cell);
            int x1 = (int)Math.Floor((Math.Max(a.X, Math.Max(bb.X, c.X)) - _ox) / _cell);
            int y0 = (int)Math.Floor((Math.Min(a.Y, Math.Min(bb.Y, c.Y)) - _oy) / _cell);
            int y1 = (int)Math.Floor((Math.Max(a.Y, Math.Max(bb.Y, c.Y)) - _oy) / _cell);
            for (int i = x0; i <= x1; i++)
                for (int j = y0; j <= y1; j++)
                {
                    if (!_index.TryGetValue((i, j), out var l)) _index[(i, j)] = l = new List<int>();
                    l.Add(t);
                }
        }
    }

    /// <summary>Elevation of the surface under (x, y), or null when outside.</summary>
    public double? ElevationAt(double x, double y)
    {
        EnsureIndex();
        if (_index == null || !_index.TryGetValue(((int)Math.Floor((x - _ox) / _cell), (int)Math.Floor((y - _oy) / _cell)), out var cands)) return null;
        foreach (var t in cands)
        {
            var a = Vertices[Triangles[t]];
            var b = Vertices[Triangles[t + 1]];
            var c = Vertices[Triangles[t + 2]];
            var d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(d) < 1e-15) continue;
            var l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            var l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            var l3 = 1 - l1 - l2;
            const double eps = -1e-9;
            if (l1 >= eps && l2 >= eps && l3 >= eps) return l1 * a.Z + l2 * b.Z + l3 * c.Z;
        }
        return null;
    }
}

/// <summary>Surface connection between two holes (or from an initiation point to a hole).</summary>
public sealed class TieEntity : Entity
{
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    /// <summary>Surface connector product; its delay is used unless <see cref="DelayMs"/> overrides it.</summary>
    public string? ProductId { get; set; }
    public double DelayMs { get; set; }

    [JsonIgnore] public Vec3 FromPos { get; set; }
    [JsonIgnore] public Vec3 ToPos { get; set; }

    public override string KindName => "Tie";
    public override IEnumerable<Vec3> GetVertices() { yield return FromPos; yield return ToPos; }
    public override void Translate(Vec3 d) { }
    public override void Transform(Func<Vec3, Vec3> f) { }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(new[] { FromPos, ToPos }, p, false);
}

/// <summary>
/// Point where the blast is initiated (lead-in / opening point). Its start time and lead-in delay seed the timing solve.
/// Placed on a hole it is bound to it (<see cref="HoleId"/>): it starts that hole and the ties go on from the hole;
/// the two move together and the point goes with a deleted hole (<see cref="InitiationLinks"/>).
/// </summary>
public sealed class InitiationPoint : Entity
{
    public Vec3 Position { get; set; }
    /// <summary>Hole the point stands on, null for a point of its own (tied to its first hole).</summary>
    public Guid? HoleId { get; set; }
    private string? _holeLabel;
    /// <summary>Label of the hole the point stands on, for the properties panel (set by the timing solve).</summary>
    [JsonIgnore] public string? Hole => _holeLabel;
    public void SetHoleLabel(string? label) => _holeLabel = label;
    public string Label { get; set; } = "IP1";
    /// <summary>Time the lead-in fires, ms.</summary>
    public double StartTimeMs { get; set; }
    /// <summary>Delay of the lead-in line itself, ms.</summary>
    public double LeadInDelayMs { get; set; }
    public bool IsElectronic { get; set; }

    public override string KindName => "Initiation point";
    public override IEnumerable<Vec3> GetVertices() { yield return Position; }
    public override void Translate(Vec3 d) => Position += d;
    public override void Transform(Func<Vec3, Vec3> f) => Position = f(Position);
}
