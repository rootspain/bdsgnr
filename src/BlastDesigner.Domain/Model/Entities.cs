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
[JsonDerivedType(typeof(EbsOpeningPoint), "ebsopening")]
[JsonDerivedType(typeof(BlockEntity), "block")]
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

    /// <summary>
    /// Whether the Transform tool changes this object with <paramref name="t"/>: holes and ties never (holes have their pattern
    /// and their properties), a block not when tilted about X or Y (its walls stay vertical).
    /// </summary>
    public virtual bool Takes(SpatialTransform t) => true;

    /// <summary>
    /// The Transform tool: scale, tilt, mirror and move. Every vertex by the map; the objects that keep sizes and angles
    /// (circle, text, block, dimension) and those with a side (chevron, surface) override it.
    /// </summary>
    public virtual void Transform(SpatialTransform t)
    {
        var m = t.Matrix;
        Transform(m.Apply);
    }

    /// <summary>Deep copy with a fresh id.</summary>
    public Entity CloneWithNewId()
    {
        var c = ProjectJson.DeepClone<Entity>(this);
        c.Id = Guid.NewGuid();
        // a copy of a surface is not built from the objects of the original
        if (c is SurfaceEntity s) s.SourceIds = new();
        return c;
    }

    /// <summary>A copy whose plain fields a property editor changes; the original keeps its values until they are applied.</summary>
    public virtual Entity EditCopy() => ProjectJson.DeepClone<Entity>(this);

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
    Intersection,
    /// <summary>The exclusion zone drawn by the Exclusion zone calculator: one in a project, drawn again in place, taken off by Clear analysis.</summary>
    ExclusionZone
}

/// <summary>3D polyline; when <see cref="Closed"/> is set it is a polygon.</summary>
public sealed class PolylineEntity : Entity, ILineStyled
{
    public List<Vec3> Points { get; set; } = new();
    /// <summary>Middle of the line's extent; set, it moves the whole line there, its shape and the heights of its vertices kept.</summary>
    [JsonIgnore] public Vec3 Position
    {
        get => Points.Count == 0 ? Vec3.Zero : GetBounds().Center;
        set { if (Points.Count > 0) Translate(value - GetBounds().Center); }
    }
    public bool Closed { get; set; }
    public LineKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <inheritdoc cref="ILineStyled.LineWeight"/>
    public int? LineWeight { get; set; }
    /// <inheritdoc cref="ILineStyled.LineType"/>
    public string? LineType { get; set; }
    /// <inheritdoc cref="ILineStyled.LineTypeScale"/>
    public double LineTypeScale { get; set; } = 1;

    /// <summary>
    /// Arrows of an exclusion zone, drawn with it and gone with it: the radius of a zone of one source, the length and the
    /// width of one that several sources make. Null on every other line and in files from before them.
    /// </summary>
    public List<ZoneArrow>? ZoneArrows { get; set; }

    public override string KindName => Closed ? "Polygon" : "Polyline";
    public override IEnumerable<Vec3> GetVertices() => Points;
    public override void Translate(Vec3 d)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += d;
        if (ZoneArrows != null) foreach (var a in ZoneArrows) { a.From += d; a.To += d; }
    }
    public override void Transform(Func<Vec3, Vec3> f)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = f(Points[i]);
        if (ZoneArrows != null) foreach (var a in ZoneArrows) { a.From = f(a.From); a.To = f(a.To); }
    }
    public override double PickDistance(Vec3 p) => GeoMath.DistanceToPolyline2D(Points, p, Closed);

    /// <summary>A part of the line to show (cut by a block): the same object, same id, with these points, open.</summary>
    public PolylineEntity Piece(List<Vec3> points)
    {
        var c = (PolylineEntity)MemberwiseClone();
        c.Points = points;
        c.Closed = false;
        return c;
    }

    [JsonIgnore] public double Length => GeoMath.Length(Points, Closed);
    [JsonIgnore] public double Area => Closed ? GeoMath.Area(Points) : 0;
}

/// <summary>What an arrow of an exclusion zone measures.</summary>
public enum ZoneArrowKind
{
    /// <summary>The radius of a source: from its outermost charged hole toward the azimuth set for it, the head on the edge.</summary>
    Radius,
    /// <summary>The greatest length of a zone that several sources make: across it between its two farthest points.</summary>
    Length,
    /// <summary>The width of such a zone across one of its sources, square to its length: arrow heads at both ends.</summary>
    Width
}

/// <summary>An arrow of an exclusion zone with its length written on it (the radius, or the zone's length or width).</summary>
public sealed class ZoneArrow
{
    public Vec3 From { get; set; }
    public Vec3 To { get; set; }
    public ZoneArrowKind Kind { get; set; }

    [JsonIgnore] public double Length => Vec3.Distance2D(From, To);
    /// <summary>Heads at both ends: a length or a width across the zone, not a radius out of it.</summary>
    [JsonIgnore] public bool BothEnds => Kind != ZoneArrowKind.Radius;
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

    /// <summary>The position by the map, the height by the scale in plan, the direction of the line turned (and mirrored) with
    /// the plan; the letters themselves are never mirrored, the text stays readable.</summary>
    public override void Transform(SpatialTransform t)
    {
        var m = t.Matrix;
        Position = m.Apply(Position);
        Height *= t.PlanScale;
        var a = Rotation * Math.PI / 180;
        var d = m.ApplyVector(new Vec3(Math.Cos(a), Math.Sin(a), 0));
        if (d.Length2D > 1e-9) Rotation = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
    }
}

public sealed class CircleEntity : Entity, ILineStyled
{
    public Vec3 Center { get; set; }
    public double Radius { get; set; } = 1;
    /// <inheritdoc cref="ILineStyled.LineWeight"/>
    public int? LineWeight { get; set; }
    /// <inheritdoc cref="ILineStyled.LineType"/>
    public string? LineType { get; set; }
    /// <inheritdoc cref="ILineStyled.LineTypeScale"/>
    public double LineTypeScale { get; set; } = 1;

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

    /// <summary>A circle stays a circle: the centre by the map, the radius by the scale in plan (the tool keeps it the same
    /// along every axis while a circle is selected); a tilt moves the centre only.</summary>
    public override void Transform(SpatialTransform t)
    {
        Center = t.Apply(Center);
        Radius *= t.PlanScale;
    }

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

public sealed class DimensionEntity : Entity, ILineStyled
{
    public Vec3 Start { get; set; }
    public Vec3 End { get; set; }
    /// <summary>Perpendicular offset of the dimension line in plan.</summary>
    public double Offset { get; set; } = 2;
    /// <inheritdoc cref="ILineStyled.LineWeight"/>
    public int? LineWeight { get; set; }
    /// <inheritdoc cref="ILineStyled.LineType"/>
    public string? LineType { get; set; }
    /// <inheritdoc cref="ILineStyled.LineTypeScale"/>
    public double LineTypeScale { get; set; } = 1;

    public override string KindName => "Dimension";
    [JsonIgnore] public double Distance => Vec3.Distance(Start, End);
    public override IEnumerable<Vec3> GetVertices() { yield return Start; yield return End; }
    public override void Translate(Vec3 d) { Start += d; End += d; }
    public override void Transform(Func<Vec3, Vec3> f) { Start = f(Start); End = f(End); }
    public override double PickDistance(Vec3 p) => Drawn().Min(s => GeoMath.DistanceToPolyline2D(s, p, false));

    /// <summary>The measured points by the map; the dimension line stays where the map takes it, so a mirror puts it on the
    /// mirrored side and a scale across moves it out with the plan.</summary>
    public override void Transform(SpatialTransform t)
    {
        var m = t.Matrix;
        var (a, _) = Line();
        var a2 = m.Apply(a);
        Start = m.Apply(Start);
        End = m.Apply(End);
        var dir = new Vec3(End.X - Start.X, End.Y - Start.Y, 0);
        if (dir.Length2D < 1e-12) return;
        dir = dir.Normalized();
        Offset = Vec3.Dot(a2 - Start, new Vec3(-dir.Y, dir.X, 0));
    }

    /// <summary>The ends of the dimension line: the measured points moved square to them by <see cref="Offset"/> in plan.</summary>
    public (Vec3 A, Vec3 B) Line()
    {
        var dir = new Vec3(End.X - Start.X, End.Y - Start.Y, 0).Normalized();
        var n = new Vec3(-dir.Y, dir.X, 0) * Offset;
        return (Start + n, End + n);
    }

    /// <summary>What is drawn of it, as it is picked: the two extension lines and the dimension line between them.</summary>
    public Vec3[][] Drawn()
    {
        var (a, b) = Line();
        return new[] { new[] { Start, a }, new[] { a, b }, new[] { End, b } };
    }
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
    /// <summary>
    /// The points, lines and polygons the surface was built from (Create surface): it is built again from them when they
    /// change, and they move with it when it moves (the engine's SurfaceSources). Empty for a surface not built from
    /// objects (imported, drawn, cut). The list is replaced, never changed in place: copies of the surface share it.
    /// </summary>
    public List<Guid> SourceIds { get; set; } = new();
    /// <summary>Built from its <see cref="SourceIds"/> with the lines as breaklines.</summary>
    public bool SourceBreaklines { get; set; }

    public override string KindName => "Surface";
    [JsonIgnore] public int TriangleCount => Triangles.Count / 3;
    public override IEnumerable<Vec3> GetVertices() => Vertices;
    public override void Translate(Vec3 d) { for (int i = 0; i < Vertices.Count; i++) Vertices[i] += d; InvalidateIndex(); }
    public override void Transform(Func<Vec3, Vec3> f) { for (int i = 0; i < Vertices.Count; i++) Vertices[i] = f(Vertices[i]); InvalidateIndex(); }

    /// <summary>The vertices by the map; a mirror turns every triangle round, so the surface still faces up.</summary>
    public override void Transform(SpatialTransform t)
    {
        var m = t.Matrix;
        Transform(m.Apply);
        if (m.IsMirror)
            for (int i = 0; i + 2 < Triangles.Count; i += 3) (Triangles[i + 1], Triangles[i + 2]) = (Triangles[i + 2], Triangles[i + 1]);
    }

    public override double PickDistance(Vec3 p) => ElevationAt(p.X, p.Y).HasValue ? 0.5 : double.MaxValue;

    /// <summary>Shares the mesh, which property editors leave out: copied through JSON it took longer than all the rest of a click.</summary>
    public override Entity EditCopy() => (Entity)MemberwiseClone();

    /// <summary>The same surface, same id, with another mesh (a part cut by a block, to show); the original stays as it is.</summary>
    public SurfaceEntity WithMesh(List<Vec3> vertices, List<int> triangles)
    {
        var c = (SurfaceEntity)MemberwiseClone();
        c.Vertices = vertices;
        c.Triangles = triangles;
        c._index = null;
        c._indexedTriangles = -1;
        return c;
    }

    /// <summary>The same surface with no mesh, for storing it apart; the original stays as it is.</summary>
    public SurfaceEntity WithoutMesh()
    {
        var c = (SurfaceEntity)MemberwiseClone();
        c.Vertices = new();
        c.Triangles = new();
        return c;
    }

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
    /// <summary>A tie goes with its holes, which the Transform tool leaves.</summary>
    public override bool Takes(SpatialTransform t) => false;
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
