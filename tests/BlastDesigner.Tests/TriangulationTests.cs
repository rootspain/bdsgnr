using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using Xunit;

namespace BlastDesigner.Tests;

/// <summary>
/// The triangulation against the plain algorithm it replaced, which tested the new point against every triangle:
/// the triangles, their order and their corners must stay exactly the same.
/// </summary>
public class TriangulationTests
{
    /// <summary>The plain algorithm: Bowyer–Watson, every triangle tested for every point.</summary>
    private static List<int> Plain(IEnumerable<Vec3> input)
    {
        var pts = new List<Vec3>();
        var index = new Dictionary<(long, long), int>();
        static (long, long) Key(Vec3 p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
        foreach (var p in input)
            if (index.TryAdd(Key(p), pts.Count)) pts.Add(p);
        var found = new List<int>();
        if (pts.Count < 3) return found;

        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        var d = Math.Max(maxX - minX, maxY - minY) * 20 + 1;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var extent = Math.Max(maxX - minX, maxY - minY) + 1;
        var rng = new Random(12345);
        var all = new List<Vec3>(pts.Select(p => new Vec3(p.X - cx + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Y - cy + (rng.NextDouble() - 0.5) * 1e-6 * extent, p.Z)))
        {
            new(-d, -d, 0),
            new(d, -d, 0),
            new(0, d, 0)
        };
        int s0 = pts.Count, s1 = pts.Count + 1, s2 = pts.Count + 2;
        var tris = new List<Tri> { MakeTri(all, s0, s1, s2) };

        var order = Enumerable.Range(0, pts.Count).OrderBy(i => Math.Floor((pts[i].X - minX) / Math.Max(1e-9, (maxX - minX) / 64))).ThenBy(i => pts[i].Y).ToList();
        foreach (var i in order)
        {
            var p = all[i];
            var bad = new List<int>();
            for (int t = 0; t < tris.Count; t++)
                if (tris[t].InCircle(p.X, p.Y)) bad.Add(t);
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                var tr = tris[t];
                AddEdge(edges, tr.A, tr.B);
                AddEdge(edges, tr.B, tr.C);
                AddEdge(edges, tr.C, tr.A);
            }
            for (int k = bad.Count - 1; k >= 0; k--)
            {
                tris[bad[k]] = tris[^1];
                tris.RemoveAt(tris.Count - 1);
            }
            foreach (var (e, count) in edges)
                if (count == 1) tris.Add(MakeTri(all, e.Item1, e.Item2, i));
        }
        foreach (var t in tris)
            if (t.A < s0 && t.B < s0 && t.C < s0) { found.Add(t.A); found.Add(t.B); found.Add(t.C); }
        return found;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static Tri MakeTri(List<Vec3> pts, int a, int b, int c)
    {
        var pa = pts[a];
        var pb = pts[b];
        var pc = pts[c];
        if ((pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X) < 0) (b, c, pb, pc) = (c, b, pc, pb);
        var dd = 2 * (pa.X * (pb.Y - pc.Y) + pb.X * (pc.Y - pa.Y) + pc.X * (pa.Y - pb.Y));
        double ux, uy;
        if (Math.Abs(dd) < 1e-18) { ux = pa.X; uy = pa.Y; }
        else
        {
            var a2 = pa.X * pa.X + pa.Y * pa.Y;
            var b2 = pb.X * pb.X + pb.Y * pb.Y;
            var c2 = pc.X * pc.X + pc.Y * pc.Y;
            ux = (a2 * (pb.Y - pc.Y) + b2 * (pc.Y - pa.Y) + c2 * (pa.Y - pb.Y)) / dd;
            uy = (a2 * (pc.X - pb.X) + b2 * (pa.X - pc.X) + c2 * (pb.X - pa.X)) / dd;
        }
        var r2 = (pa.X - ux) * (pa.X - ux) + (pa.Y - uy) * (pa.Y - uy);
        return new Tri(a, b, c, ux, uy, r2);
    }

    private readonly record struct Tri(int A, int B, int C, double Cx, double Cy, double R2)
    {
        public bool InCircle(double x, double y) => (x - Cx) * (x - Cx) + (y - Cy) * (y - Cy) < R2 * (1 - 1e-12);
    }

    private static List<Vec3> Scatter(int n, int seed, double size = 500, double x0 = 0, double y0 = 0)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => new Vec3(x0 + r.NextDouble() * size, y0 + r.NextDouble() * size, r.NextDouble() * 30)).ToList();
    }

    private static List<Vec3> Lattice(int nx, int ny, double step, double x0 = 0, double y0 = 0, double turn = 0)
    {
        var list = new List<Vec3>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                list.Add(new Vec3(x0 + (i * Math.Cos(turn) - j * Math.Sin(turn)) * step, y0 + (i * Math.Sin(turn) + j * Math.Cos(turn)) * step, i + j));
        return list;
    }

    /// <summary>Sets of points that are hard on a triangulation; the name says why.</summary>
    public static TheoryData<string> Shapes => new()
    {
        "scatter", "scatter small", "scatter far from the origin", "grid", "turned grid", "grid at mine coordinates", "drill pattern", "one line", "two lines", "cross",
        "circle", "circle with its centre", "clusters", "duplicates", "contours", "thin strip", "fan", "three points", "two points"
    };

    private static List<Vec3> Shape(string name)
    {
        var r = new Random(name.Length * 31 + 7);
        switch (name)
        {
            case "scatter": return Scatter(3000, 1);
            case "scatter small": return Scatter(40, 2, 3);
            case "scatter far from the origin": return Scatter(2000, 3, 800, 6_500_000, 5_400_000);
            case "grid": return Lattice(50, 40, 5);
            case "turned grid": return Lattice(40, 40, 3.5, 100, -200, 0.4);
            case "grid at mine coordinates": return Lattice(50, 30, 4.5, 512_000, 7_250_000, 0.2);
            case "drill pattern": return Lattice(30, 12, 6).Select((p, i) => p with { X = p.X + (i / 30 % 2) * 3 }).ToList();
            case "one line": return Enumerable.Range(0, 300).Select(i => new Vec3(i * 2.0, i * 1.0, 5)).ToList();
            case "two lines": return Enumerable.Range(0, 400).Select(i => new Vec3(i / 2 * 2.0, i % 2 * 30.0, 5)).ToList();
            case "cross": return Enumerable.Range(0, 200).Select(i => new Vec3(i, 100, 0)).Concat(Enumerable.Range(0, 200).Select(i => new Vec3(100.5, i, 0))).ToList();
            case "circle": return Enumerable.Range(0, 500).Select(i => new Vec3(100 * Math.Cos(i * Math.Tau / 500), 100 * Math.Sin(i * Math.Tau / 500), 0)).ToList();
            case "circle with its centre": return Enumerable.Range(0, 500).Select(i => new Vec3(100 * Math.Cos(i * Math.Tau / 500), 100 * Math.Sin(i * Math.Tau / 500), 0)).Append(new Vec3(0, 0, 9)).ToList();
            case "clusters": return Enumerable.Range(0, 12).SelectMany(c => Scatter(150, c, 2, r.NextDouble() * 5000, r.NextDouble() * 5000)).ToList();
            case "duplicates": { var s = Scatter(800, 4); return s.Concat(s.Take(300).Select(p => p with { X = p.X + 0.0001, Z = p.Z + 1 })).ToList(); }
            case "contours": return Enumerable.Range(1, 12).SelectMany(k => Enumerable.Range(0, 150).Select(i => new Vec3(k * 20 * Math.Cos(i * Math.Tau / 150), k * 14 * Math.Sin(i * Math.Tau / 150), k * 5))).ToList();
            case "thin strip": return Scatter(1000, 5).Select(p => p with { Y = p.Y / 1000 }).ToList();
            case "fan": return Enumerable.Range(1, 300).Select(i => new Vec3(i * i * 0.01, i * 0.5, 0)).Append(new Vec3(0, 0, 0)).ToList();
            case "three points": return new() { new(0, 0, 1), new(10, 0, 2), new(0, 10, 3) };
            case "two points": return new() { new(0, 0, 1), new(10, 0, 2) };
        }
        throw new ArgumentException(name);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheSameTrianglesAsThePlainAlgorithm(string shape)
    {
        var pts = Shape(shape);
        Assert.Equal(Plain(pts), Surfaces.Triangulate(pts).Triangles);
    }

    [Fact]
    public void TheSameTrianglesOnManyRandomSets()
    {
        for (var seed = 0; seed < 300; seed++)
        {
            var r = new Random(seed);
            var pts = Scatter(r.Next(3, 400), seed, Math.Pow(10, r.NextDouble() * 4), r.NextDouble() * 1e6, r.NextDouble() * 1e7);
            // some on a grid, as survey data often is
            if (seed % 3 == 0) pts = pts.Select(p => new Vec3(Math.Round(p.X), Math.Round(p.Y), p.Z)).ToList();
            Assert.Equal(Plain(pts), Surfaces.Triangulate(pts).Triangles);
        }
    }

    [Fact]
    public void ALargeSurfaceIsBuiltInAMomentAndCoversThePoints()
    {
        var pts = Scatter(200_000, 11, 5000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var s = Surfaces.Triangulate(pts);
        Assert.True(clock.Elapsed.TotalSeconds < 20, $"{clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(pts.Count, s.Triangles.Distinct().Count());
        // the plane formula of a triangulation: triangles = 2 × points − 2 − points on the hull
        Assert.InRange(s.Triangles.Count / 3, 2 * pts.Count - 2 - 2000, 2 * pts.Count - 2);
    }
}
