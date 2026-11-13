using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Blocks made of lines (a drawing from elsewhere, a block written to DXF, lines drawn by hand): on a layer of blocks
/// (<see cref="BlockLayers.IsBlockLayer"/>) with no block yet, lines whose segments are the twelve edges of a box with vertical
/// walls meeting in eight corners are put together into one <see cref="BlockEntity"/>, which takes their place.
/// </summary>
public static class BlockAssembly
{
    /// <summary>Corners closer than this are one corner, m.</summary>
    public const double PointTolerance = 0.001;
    /// <summary>How far from a right angle, or from vertical, an edge may be, degrees.</summary>
    public const double AngleTolerance = 0.1;

    /// <summary>
    /// Puts the blocks of the project's block layers together. Returns the names of the block layers whose lines make no
    /// box (too few or too many edges, a tilted box, other objects on the layer); their lines stay as they are.
    /// </summary>
    public static List<string> Assemble(BlastProject p)
    {
        var failed = new List<string>();
        foreach (var layer in p.Layers.ToList())
        {
            if (!BlockLayers.IsBlockLayer(layer)) continue;
            var on = p.Entities.Where(e => e.LayerId == layer.Id).ToList();
            if (on.Count == 0 || on.Any(e => e is BlockEntity)) continue;
            if (on.Any(e => e is not PolylineEntity) || FromLines(on.Cast<PolylineEntity>()) is not { } block)
            {
                failed.Add(layer.Name);
                continue;
            }
            var ids = on.Select(e => e.Id).ToHashSet();
            p.Entities.RemoveAll(e => ids.Contains(e.Id));
            block.Color = on.Select(e => e.Color).FirstOrDefault(c => c != null);
            p.Add(block, layer);
            layer.Group = BlockLayers.Group;
        }
        return failed;
    }

    /// <summary>The block whose twelve edges the segments of the lines are; null when they are not one.</summary>
    public static BlockEntity? FromLines(IEnumerable<PolylineEntity> lines)
    {
        var corners = new List<Vec3>();
        int CornerOf(Vec3 v)
        {
            for (int i = 0; i < corners.Count; i++) if (Vec3.Distance(corners[i], v) <= PointTolerance) return i;
            corners.Add(v);
            return corners.Count - 1;
        }
        var edges = new HashSet<(int, int)>();
        foreach (var l in lines)
        {
            var n = l.Closed ? l.Points.Count : l.Points.Count - 1;
            for (int i = 0; i < n; i++)
            {
                int a = CornerOf(l.Points[i]), b = CornerOf(l.Points[(i + 1) % l.Points.Count]);
                if (a == b) continue;
                edges.Add((Math.Min(a, b), Math.Max(a, b)));
            }
            if (corners.Count > 8) return null;
        }
        if (corners.Count != 8 || edges.Count != 12) return null;
        // the base: the four lowest corners, all on one level, and the top the same four straight above them
        var byZ = corners.OrderBy(c => c.Z).ToList();
        var bottom = byZ.Take(4).ToList();
        var top = byZ.Skip(4).ToList();
        if (bottom.Max(c => c.Z) - bottom.Min(c => c.Z) > PointTolerance || top.Max(c => c.Z) - top.Min(c => c.Z) > PointTolerance) return null;
        var height = top.Average(c => c.Z) - bottom.Average(c => c.Z);
        if (height < BlockEntity.MinSize) return null;
        // a corner of the base and the two corners joined to it on the base
        int Index(Vec3 v) => corners.FindIndex(c => c == v);
        bool Joined(Vec3 a, Vec3 b) => edges.Contains((Math.Min(Index(a), Index(b)), Math.Max(Index(a), Index(b))));
        var o = bottom[0];
        var near = bottom.Skip(1).Where(c => Joined(o, c)).ToList();
        if (near.Count != 2) return null;
        var (b1, b2) = (near[0], near[1]);
        var u = b1 - o;
        var v = b2 - o;
        var lenU = Math.Sqrt(u.X * u.X + u.Y * u.Y);
        var lenV = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        if (lenU < BlockEntity.MinSize || lenV < BlockEntity.MinSize) return null;
        var cos = (u.X * v.X + u.Y * v.Y) / (lenU * lenV);
        if (Math.Abs(Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI - 90) > AngleTolerance) return null;
        // the width to the right of the length
        if (u.X * v.Y - u.Y * v.X > 0) (b1, b2, u, v, lenU, lenV) = (b2, b1, v, u, lenV, lenU);
        var block = new BlockEntity
        {
            Origin = new Vec3(o.X, o.Y, bottom.Average(c => c.Z)),
            Azimuth = Math.Atan2(u.X, u.Y) * 180 / Math.PI,
            Length = lenU,
            Width = lenV,
            Height = height
        };
        // every corner of the block is one of the lines' corners and every edge of it one of their segments
        var mapped = new int[8];
        for (int i = 0; i < 8; i++)
        {
            var c = block.Corner(i);
            mapped[i] = corners.FindIndex(x => Vec3.Distance(x, c) <= Math.Max(PointTolerance, (lenU + lenV + height) * Math.Sin(AngleTolerance * Math.PI / 180)));
            if (mapped[i] < 0) return null;
        }
        if (mapped.Distinct().Count() != 8) return null;
        foreach (var (a, b) in BlockEntity.Edges)
            if (!edges.Contains((Math.Min(mapped[a], mapped[b]), Math.Max(mapped[a], mapped[b])))) return null;
        return block;
    }
}
