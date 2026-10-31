using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Conversions and general geometry functions from the Tools menu.</summary>
public static class GeneralOps
{
    public static List<PointEntity> ToPoints(IEnumerable<Entity> entities) =>
        entities.SelectMany(e => e is Hole h ? new[] { h.Collar } : e.GetVertices())
            .Select(v => new PointEntity { Position = v }).ToList();

    public static PolylineEntity ToPolyline(IEnumerable<Entity> entities, bool closed)
    {
        var pts = entities.SelectMany(e => e is Hole h ? new[] { h.Collar } : e.GetVertices()).ToList();
        return new PolylineEntity { Points = pts, Closed = closed };
    }

    /// <summary>Orders points along their nearest-neighbour chain to build a sensible polyline.</summary>
    public static List<Vec3> ChainPoints(IReadOnlyList<Vec3> pts)
    {
        if (pts.Count < 3) return pts.ToList();
        var left = pts.ToList();
        // Start from the point farthest from the centroid.
        var c = GeoMath.Centroid(pts);
        var cur = left.OrderByDescending(p => Vec3.Distance2D(p, c)).First();
        var chain = new List<Vec3> { cur };
        left.Remove(cur);
        while (left.Count > 0)
        {
            cur = left.OrderBy(p => Vec3.Distance2D(p, cur)).First();
            chain.Add(cur);
            left.Remove(cur);
        }
        return chain;
    }

    public static PolylineEntity CircleToPolygon(CircleEntity c, int segments = 36) =>
        new() { Points = c.ToPolygon(segments), Closed = true, LayerId = c.LayerId };

    public static List<PolylineEntity> ClipPolylines(IEnumerable<PolylineEntity> lines, IReadOnlyList<Vec3> polygon) =>
        lines.SelectMany(l => GeoMath.ClipPolylineToPolygon(l.Closed ? l.Points.Append(l.Points[0]).ToList() : l.Points, polygon)
            .Select(piece => new PolylineEntity { Points = piece, Kind = l.Kind, LayerId = l.LayerId })).ToList();

    public static PointEntity CentreOfMass(PolylineEntity polygon) =>
        new() { Position = GeoMath.Centroid(polygon.Points), Kind = PointKind.CentreOfMass, Label = "CM", LayerId = polygon.LayerId };

    public static PolylineEntity Expand(PolylineEntity polygon, double distance) =>
        new() { Points = GeoMath.OffsetPolygon(polygon.Points, distance), Closed = true, Kind = polygon.Kind, LayerId = polygon.LayerId };

    /// <summary>Clearance zone: the hull of the selected holes expanded by a radius.</summary>
    public static PolylineEntity ClearanceZone(IEnumerable<Hole> holes, double radius)
    {
        var pts = new List<Vec3>();
        foreach (var h in holes)
            for (int i = 0; i < 16; i++)
                pts.Add(h.Collar + new Vec3(Math.Cos(i * Math.PI / 8) * radius, Math.Sin(i * Math.PI / 8) * radius, 0));
        return new PolylineEntity { Points = GeoMath.ConvexHull(pts), Closed = true, Kind = LineKind.ClearanceZone, Label = $"Clearance {radius:0} m" };
    }

    /// <summary>Similarity transform: translate, rotate about a pivot in plan, and scale.</summary>
    public static Func<Vec3, Vec3> MakeTransform(Vec3 translate, double rotateDeg, Vec3 pivot, double scale)
    {
        var c = Math.Cos(-rotateDeg * GeoMath.Deg);
        var s = Math.Sin(-rotateDeg * GeoMath.Deg);
        return p =>
        {
            var d = p - pivot;
            var r = new Vec3(d.X * c - d.Y * s, d.X * s + d.Y * c, d.Z) * scale;
            return pivot + r + translate;
        };
    }

    /// <summary>Two-point coordinate transformation: maps a1→b1 and a2→b2 (translation, rotation, scale in plan).</summary>
    public static Func<Vec3, Vec3> TwoPointTransform(Vec3 a1, Vec3 a2, Vec3 b1, Vec3 b2, bool allowScale)
    {
        var da = a2 - a1;
        var db = b2 - b1;
        var rot = Math.Atan2(db.Y, db.X) - Math.Atan2(da.Y, da.X);
        var scale = allowScale && da.Length2D > 1e-9 ? db.Length2D / da.Length2D : 1;
        var c = Math.Cos(rot);
        var s = Math.Sin(rot);
        var dz = b1.Z - a1.Z;
        return p =>
        {
            var d = p - a1;
            return new Vec3(b1.X + (d.X * c - d.Y * s) * scale, b1.Y + (d.X * s + d.Y * c) * scale, p.Z + dz);
        };
    }

    /// <summary>Copies entities with an offset, returning the copies (Move/copy entities).</summary>
    public static List<Entity> Copy(IEnumerable<Entity> entities, Vec3 offset, int copies)
    {
        var result = new List<Entity>();
        for (int k = 1; k <= copies; k++)
            foreach (var e in entities)
            {
                if (e is TieEntity) continue;
                var c = e.CloneWithNewId();
                c.Translate(offset * k);
                result.Add(c);
            }
        return result;
    }

    /// <summary>Moves points, polylines and texts vertically onto a surface.</summary>
    public static int MoveOntoSurface(IEnumerable<Entity> entities, SurfaceEntity surface)
    {
        int n = 0;
        foreach (var e in entities)
        {
            if (e is Hole h)
            {
                if (surface.ElevationAt(h.Collar.X, h.Collar.Y) is double z) { h.Collar = h.Collar.WithZ(z); n++; }
                continue;
            }
            if (e is SurfaceEntity) continue;
            e.Transform(p => surface.ElevationAt(p.X, p.Y) is double z ? p.WithZ(z) : p);
            n++;
        }
        return n;
    }
}
