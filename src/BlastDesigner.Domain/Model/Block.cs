using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>
/// A block: a box with vertical walls, turned only by its azimuth in plan. While its layer is visible the view shows only
/// what is inside it (<see cref="BlockClip"/>). Each block has a layer of its own (<see cref="BlockLayers"/>).
/// </summary>
public sealed class BlockEntity : Entity
{
    /// <summary>Corner of the base the length, width and height go from.</summary>
    public Vec3 Origin { get; set; }
    /// <summary>Direction of the length, degrees clockwise from north.</summary>
    public double Azimuth { get; set; }
    /// <summary>Along <see cref="Azimuth"/>, m.</summary>
    public double Length { get; set; } = 10;
    /// <summary>To the right of the length, m.</summary>
    public double Width { get; set; } = 10;
    /// <summary>Up from the base, m.</summary>
    public double Height { get; set; } = 30;

    /// <summary>The smallest length, width or height.</summary>
    public const double MinSize = 0.1;
    /// <summary>The height of a new block, m.</summary>
    public const double DefaultHeight = 30;

    /// <summary>
    /// A new block from three picked points: <paramref name="a"/> a corner, <paramref name="b"/> the end of the length
    /// (its direction is the azimuth), <paramref name="c"/> anywhere on the far side of the width. The work plane, the
    /// elevation of the first point, goes through the middle of its height. A width picked to the left turns it over.
    /// </summary>
    public static BlockEntity FromPoints(Vec3 a, Vec3 b, Vec3 c, double height = DefaultHeight)
    {
        var d = b - a;
        var block = new BlockEntity
        {
            Origin = new Vec3(a.X, a.Y, a.Z - height / 2),
            Azimuth = Math.Abs(d.X) + Math.Abs(d.Y) > 1e-12 ? Math.Atan2(d.X, d.Y) * 180 / Math.PI : 0,
            Length = Math.Sqrt(d.X * d.X + d.Y * d.Y),
            Height = height
        };
        block.Width = Vec3.Dot(c - a, block.V);
        block.Normalize();
        return block;
    }

    public override string KindName => "Block";

    /// <summary>Unit vector of the length (along the azimuth).</summary>
    [JsonIgnore] public Vec3 U { get { var a = Azimuth * Math.PI / 180; return new Vec3(Math.Sin(a), Math.Cos(a), 0); } }
    /// <summary>Unit vector of the width (to the right of the length).</summary>
    [JsonIgnore] public Vec3 V { get { var a = Azimuth * Math.PI / 180; return new Vec3(Math.Cos(a), -Math.Sin(a), 0); } }

    [JsonIgnore] public double Bottom { get => Origin.Z; set => Origin = Origin.WithZ(value); }
    [JsonIgnore] public double Top { get => Origin.Z + Height; set => Height = value - Origin.Z; }
    [JsonIgnore] public double Volume => Length * Width * Height;
    [JsonIgnore] public Vec3 Center => Origin + U * (Length / 2) + V * (Width / 2) + new Vec3(0, 0, Height / 2);

    /// <summary>The point at local coordinates: <paramref name="u"/> along the length, <paramref name="v"/> across, <paramref name="w"/> up.</summary>
    public Vec3 At(double u, double v, double w) => Origin + U * u + V * v + new Vec3(0, 0, w);

    /// <summary>Local coordinates of a point: along the length, across, up from the base.</summary>
    public (double U, double V, double W) Local(Vec3 p)
    {
        var d = p - Origin;
        return (Vec3.Dot(d, U), Vec3.Dot(d, V), d.Z);
    }

    /// <summary>Corner <paramref name="i"/> (0…7): bit 0 the far end of the length, bit 1 the far side of the width, bit 2 the top.</summary>
    public Vec3 Corner(int i) => At((i & 1) * Length, ((i >> 1) & 1) * Width, ((i >> 2) & 1) * Height);

    public IReadOnlyList<Vec3> Corners() => Enumerable.Range(0, 8).Select(Corner).ToArray();

    /// <summary>The twelve edges as pairs of corner numbers (corners differing in one bit).</summary>
    public static readonly (int A, int B)[] Edges =
        Enumerable.Range(0, 8).SelectMany(a => new[] { 1, 2, 4 }.Where(bit => (a & bit) == 0).Select(bit => (a, a | bit))).ToArray();

    public bool Contains(Vec3 p, double eps = 1e-9)
    {
        var (u, v, w) = Local(p);
        return u >= -eps && u <= Length + eps && v >= -eps && v <= Width + eps && w >= -eps && w <= Height + eps;
    }

    /// <summary>
    /// The part of the segment <paramref name="a"/>–<paramref name="b"/> inside the block, as parameters 0…1 along it
    /// (Liang–Barsky in the block's own axes); null when it misses.
    /// </summary>
    public (double T0, double T1)? ClipSegment(Vec3 a, Vec3 b)
    {
        var (au, av, aw) = Local(a);
        var (bu, bv, bw) = Local(b);
        double t0 = 0, t1 = 1;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-15) return q >= -1e-9;
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Edge(-(bu - au), au) || !Edge(bu - au, Length - au)) return null;
        if (!Edge(-(bv - av), av) || !Edge(bv - av, Width - av)) return null;
        if (!Edge(-(bw - aw), aw) || !Edge(bw - aw, Height - aw)) return null;
        return t1 >= t0 ? (t0, t1) : null;
    }

    /// <summary>
    /// Sizes that came out negative turn the block over: the origin goes to the other side and the size is positive again;
    /// none is below <see cref="MinSize"/>.
    /// </summary>
    public void Normalize()
    {
        if (Length < 0) { Origin += U * Length; Length = -Length; }
        if (Width < 0) { Origin += V * Width; Width = -Width; }
        if (Height < 0) { Origin += new Vec3(0, 0, Height); Height = -Height; }
        Length = Math.Max(MinSize, Length);
        Width = Math.Max(MinSize, Width);
        Height = Math.Max(MinSize, Height);
    }

    /// <summary>Grips: 0…7 the corners (<see cref="Corner"/>), 8…13 the middles of the faces, 8 + 2 · axis + side (axis 0 the
    /// length, 1 the width, 2 the height; side 0 the near one, 1 the far one).</summary>
    public const int GripCount = 14;

    /// <summary>Where grip <paramref name="g"/> is.</summary>
    public Vec3 Grip(int g)
    {
        if (g < 8) return Corner(g);
        var (axis, side) = ((g - 8) / 2, (g - 8) % 2);
        return At(axis == 0 ? side * Length : Length / 2, axis == 1 ? side * Width : Width / 2, axis == 2 ? side * Height : Height / 2);
    }

    /// <summary>Whether a grip changes the height: a face of the top or the bottom, or a corner dragged up or down.</summary>
    public static bool GripIsVertical(int g, bool shift) => g < 8 ? shift : (g - 8) / 2 == 2;

    /// <summary>The shape of a block: what a grip changes.</summary>
    public readonly record struct Shape(Vec3 Origin, double Azimuth, double Length, double Width, double Height);

    [JsonIgnore] public Shape Current { get => new(Origin, Azimuth, Length, Width, Height); set => (Origin, Azimuth, Length, Width, Height) = (value.Origin, value.Azimuth, value.Length, value.Width, value.Height); }

    /// <summary>
    /// The block with grip <paramref name="g"/> taken to <paramref name="to"/>; the opposite corner or face stays where it is.
    /// A corner moves the length and the width ends on the ground, or with <paramref name="vertical"/> its top or bottom;
    /// a face moves its own side only. With <paramref name="fine"/> a changed size is a whole number of 0.1 m. Taken past
    /// the opposite side the block turns over; no size is below <see cref="MinSize"/>.
    /// </summary>
    public Shape Dragged(int g, Vec3 to, bool vertical, bool fine)
    {
        var (tu, tv, tw) = Local(to);
        double[] lo = { 0, 0, 0 }, hi = { Length, Width, Height };
        double[] t = { tu, tv, tw };
        var moved = new List<(int Axis, bool High)>();
        if (g < 8)
        {
            if (vertical) moved.Add((2, (g & 4) != 0));
            else { moved.Add((0, (g & 1) != 0)); moved.Add((1, (g & 2) != 0)); }
        }
        else moved.Add(((g - 8) / 2, (g - 8) % 2 == 1));
        foreach (var (axis, high) in moved)
        {
            var value = t[axis];
            if (fine)
            {
                // the size from the side that stays, in whole steps
                var stay = high ? lo[axis] : hi[axis];
                value = stay + Math.Round((value - stay) / 0.1) * 0.1;
            }
            if (high) hi[axis] = value; else lo[axis] = value;
        }
        var b = new BlockEntity { Origin = At(lo[0], lo[1], lo[2]), Azimuth = Azimuth, Length = hi[0] - lo[0], Width = hi[1] - lo[1], Height = hi[2] - lo[2] };
        b.Normalize();
        return b.Current;
    }

    public override IEnumerable<Vec3> GetVertices() => Corners();
    public override void Translate(Vec3 d) => Origin += d;

    /// <summary>
    /// Takes the block along with a move or a turn in plan: the origin goes where the transform puts it, the azimuth turns
    /// with the length, the sizes stay. A tilt is not taken: the walls stay vertical.
    /// </summary>
    public override void Transform(Func<Vec3, Vec3> f)
    {
        var o = f(Origin);
        var u = f(Origin + U * Length) - o;
        if (Math.Abs(u.X) + Math.Abs(u.Y) > 1e-12) Azimuth = Math.Atan2(u.X, u.Y) * 180 / Math.PI;
        Origin = o;
    }

    public override double PickDistance(Vec3 p) =>
        Edges.Min(e => GeoMath.DistanceToPolyline2D(new[] { Corner(e.A), Corner(e.B) }, p, false));
}

/// <summary>
/// Layers of blocks: one block on a layer of its own, named Block, Block 2, Block 3… with the first number free. A layer
/// is one of blocks by its group, or, in a file from elsewhere, by its name.
/// </summary>
public static class BlockLayers
{
    public const string Group = "Blocks";

    private static readonly Regex Name = new(@"^\s*(Блок|Block)(\s+\d+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsBlockLayer(Layer l) => l.Group == Group || IsBlockLayerName(l.Name);

    /// <summary>A name of a block layer: Блок, Блок 2, Block, Block 3…</summary>
    public static bool IsBlockLayerName(string name) => Name.IsMatch(name);

    /// <summary>The name of the next block layer: <paramref name="baseName"/>, then with 2, 3… the first one not taken.</summary>
    public static string NextName(BlastProject p, string baseName)
    {
        var taken = p.Layers.Select(l => l.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int n = 2; ; n++)
            if (!taken.Contains($"{baseName} {n}")) return $"{baseName} {n}";
    }

    /// <summary>A new layer for a block.</summary>
    public static Layer NewLayer(BlastProject p, string baseName, uint color = 0xFF0EA5E9)
    {
        var l = new Layer { Name = NextName(p, baseName), Group = Group, Color = color };
        p.Layers.Add(l);
        return l;
    }

    /// <summary>The name of the first block layer, in the language of the program (the application sets it).</summary>
    public static string BaseName { get; set; } = "Block";

    /// <summary>
    /// For <see cref="Document.Followers"/>: keeps one block a layer after a change. A block moved to another layer goes
    /// back to its own; an object moved onto a block layer goes back to where it was; a second block on a layer (a copy, a
    /// paste) gets a layer of its own; a block layer whose block was deleted is deleted too.
    /// </summary>
    public static Action? Follow(BlastProject p)
    {
        if (!p.Layers.Any(l => l.Group == Group) && !p.Entities.OfType<BlockEntity>().Any()) return () => Keep(p, null);
        var before = p.Entities.ToDictionary(e => e.Id, e => e.LayerId);
        return () => Keep(p, before);
    }

    private static void Keep(BlastProject p, Dictionary<Guid, Guid>? before)
    {
        var blocks = p.Entities.OfType<BlockEntity>().ToList();
        if (blocks.Count == 0 && !p.Layers.Any(l => l.Group == Group)) return;
        // a size typed in below zero turns the block over, one below the least is the least
        foreach (var b in blocks) b.Normalize();
        var used = new HashSet<Guid>();
        foreach (var b in blocks)
        {
            var layer = p.FindLayer(b.LayerId);
            if (layer != null && IsBlockLayer(layer) && used.Add(layer.Id)) { layer.Group = Group; continue; }
            // moved off its layer: back to it while it is a block layer with no other block
            if (before != null && before.TryGetValue(b.Id, out var was) && p.FindLayer(was) is { } old && IsBlockLayer(old) && !used.Contains(old.Id)
                && !blocks.Any(o => o != b && o.LayerId == old.Id))
            {
                b.LayerId = old.Id;
                old.Group = Group;
                used.Add(old.Id);
                continue;
            }
            var fresh = NewLayer(p, BaseName, layer?.Color ?? 0xFF0EA5E9);
            b.LayerId = fresh.Id;
            used.Add(fresh.Id);
        }
        // nothing but its block on a block layer: anything else goes back where it was, or to the default layer
        foreach (var e in p.Entities)
        {
            if (e is BlockEntity || !used.Contains(e.LayerId)) continue;
            e.LayerId = before != null && before.TryGetValue(e.Id, out var was) && p.FindLayer(was) is { } old && !used.Contains(old.Id) && old.Group != Group
                ? old.Id
                : p.Layers.FirstOrDefault(l => l.Group != Group)?.Id ?? p.DefaultLayer.Id;
        }
        // a block layer whose block is gone goes with it
        p.Layers.RemoveAll(l => l.Group == Group && !used.Contains(l.Id) && p.Layers.Count > 1);
        if (p.FindLayer(p.DefaultLayerId) is null or { Group: Group }) p.DefaultLayerId = p.Layers.FirstOrDefault(l => l.Group != Group)?.Id ?? p.Layers[0].Id;
    }
}
