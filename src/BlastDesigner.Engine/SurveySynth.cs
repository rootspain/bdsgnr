using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>What a survey line is (the recognition classes).</summary>
public enum SurveyLineClass { Crest, Toe, Contour, Boundary, Other }

/// <summary>What a survey point is.</summary>
public enum SurveyPointClass { BenchTop, Floor, Face, OnLine, Other }

/// <summary>How survey points are drawn: 3D points, blocks with number and elevation attributes, or flat points with the elevation as text.</summary>
public enum SurveyPointStyle { Point3D, Block, Text }

/// <summary>How layers are named: everything on "0", by meaning, by meaning with mistakes, numbered, or like the real sample (points on "Survey", lines on "0"/"2"/"3").</summary>
public enum SurveyLayerStyle { AllZero, Meaningful, Mixed, Numbered, SurveyLike }

/// <summary>Hard cases for the recognition, each forced into a scene on request.</summary>
public enum SurveyTrap { None, RoadAlongCrest, ContoursAlongWall, BrokenCrest, NarrowBerms, MuckAtToe, MissingToes }

/// <summary>Ranges the generator draws a scene from (metres, degrees, probabilities).</summary>
public sealed record SurveySynthOptions
{
    public (int Min, int Max) Benches { get; init; } = (1, 4);
    public (double Min, double Max) BenchHeight { get; init; } = (5, 20);
    /// <summary>Face angle of the surveyed face (real sample: median 47°, 31–56°).</summary>
    public (double Min, double Max) FaceAngle { get; init; } = (30, 75);
    public (double Min, double Max) BermWidth { get; init; } = (3, 20);
    public (double Min, double Max) Length { get; init; } = (150, 450);
    /// <summary>Spacing of survey pickets along a line (real sample: median 7 m, 2.8–12.6 m).</summary>
    public (double Min, double Max) PicketSpacing { get; init; } = (2.5, 13);
    /// <summary>Amplitude of the elevation wander along a crest (real sample: range along a line median 1.1 m, 10 % of lines over 5.7 m).</summary>
    public (double Min, double Max) CrestWander { get; init; } = (0, 4.5);
    public (double Min, double Max) Noise { get; init; } = (0.01, 0.06);
    /// <summary>Chance that a bench's toe line was surveyed at all (real sample: three crests per toe).</summary>
    public (double Min, double Max) ToeSurveyed { get; init; } = (0.2, 0.9);
    /// <summary>Chance of a gap starting at each picket.</summary>
    public (double Min, double Max) GapRate { get; init; } = (0, 0.03);
    /// <summary>Share of line pickets also drawn as separate points (real sample: 28 % of points sit on line vertices).</summary>
    public (double Min, double Max) DuplicatePoints { get; init; } = (0, 0.8);
    /// <summary>Spacing of spot heights on benches and berms.</summary>
    public (double Min, double Max) SpotSpacing { get; init; } = (5, 25);
    /// <summary>
    /// Chance that a crest or toe was surveyed a second time beside itself, 0.3–2 m off at the same level (real sample:
    /// 35 % of the line length has such a twin about a metre away).
    /// </summary>
    public (double Min, double Max) TwinLines { get; init; } = (0, 0.9);
    public double RoadChance { get; init; } = 0.4;
    public double SafetyBermChance { get; init; } = 0.3;
    public double TerrainChance { get; init; } = 0.25;
    public double MuckChance { get; init; } = 0.25;
    public double ErosionChance { get; init; } = 0.4;
    public double HolesChance { get; init; } = 0.6;
    public double BoundaryChance { get; init; } = 0.5;
    /// <summary>Plan drawing at elevation 0 (grid, design outlines) that the import moves to "(2D)" layers.</summary>
    public double PlanDrawingChance { get; init; } = 0.5;
    public IReadOnlyList<(SurveyPointStyle Style, double Weight)> PointStyles { get; init; } = new[] { (SurveyPointStyle.Point3D, 0.7), (SurveyPointStyle.Block, 0.3) };
    public IReadOnlyList<(SurveyLayerStyle Style, double Weight)> LayerStyles { get; init; } =
        new[] { (SurveyLayerStyle.AllZero, 0.2), (SurveyLayerStyle.Meaningful, 0.15), (SurveyLayerStyle.Mixed, 0.2), (SurveyLayerStyle.Numbered, 0.2), (SurveyLayerStyle.SurveyLike, 0.25) };

    /// <summary>The training distribution, set around the real sample.</summary>
    public static SurveySynthOptions Train => new();

    /// <summary>
    /// A deliberately different distribution for checking (never for training): steeper and flatter faces, lower and
    /// higher benches, sparser and denser pickets, more noise. A model that learnt the generator, not the bench, drops here.
    /// </summary>
    public static SurveySynthOptions Shifted => new()
    {
        Benches = (1, 5),
        BenchHeight = (3, 25),
        FaceAngle = (22, 85),
        BermWidth = (2, 30),
        PicketSpacing = (1.5, 20),
        CrestWander = (0, 7),
        Noise = (0.03, 0.12),
        ToeSurveyed = (0.1, 1),
        GapRate = (0.01, 0.06),
        SpotSpacing = (3, 40),
        RoadChance = 0.6,
        SafetyBermChance = 0.5,
        TerrainChance = 0.5,
        MuckChance = 0.4,
        ErosionChance = 0.6,
        TwinLines = (0, 1)
    };
}

/// <summary>A generated survey with its true classes. <see cref="Layers"/> holds the drawing layer of every entity.</summary>
public sealed class SurveyScene
{
    public int Seed { get; init; }
    public SurveyTrap Trap { get; init; }
    public SurveyPointStyle PointStyle { get; set; }
    public SurveyLayerStyle LayerStyle { get; set; }
    public List<Entity> Entities { get; } = new();
    public Dictionary<Guid, string> Layers { get; } = new();
    public Dictionary<string, uint> LayerColors { get; } = new();
    public Dictionary<Guid, SurveyLineClass> LineClasses { get; } = new();
    public Dictionary<Guid, SurveyPointClass> PointClasses { get; } = new();
    /// <summary>Hole collars on the top bench (context for the recognition; not part of the drawing).</summary>
    public List<Vec3> Collars { get; } = new();
    /// <summary>Bench height and face angle of each bench, for reports.</summary>
    public List<(double Height, double FaceAngle, double Berm)> Benches { get; } = new();

    public IEnumerable<PolylineEntity> Lines => Entities.OfType<PolylineEntity>();
    public IEnumerable<PointEntity> Points => Entities.OfType<PointEntity>();
}

/// <summary>
/// Synthetic open-pit bench surveys for training and checking the recognition of crests and toes: a bench wall in plan
/// (straight, arc, corner or wavy), its profile (benches, faces, berms, floor), wear (eroded crests, muck at the toe),
/// surroundings (roads, safety berms, terrain contours, boundary, plan drawing) — and then the surveyor: pickets along
/// the lines with noise and gaps, toes often left out, spot heights on the benches, and one of several drawing styles.
/// The same seed gives the same scene.
/// </summary>
public static class SurveySynth
{
    private const double Step = 1.0; // sampling of the base curve, m

    public static SurveyScene Generate(int seed, SurveySynthOptions? options = null, SurveyTrap trap = SurveyTrap.None)
    {
        var o = options ?? SurveySynthOptions.Train;
        var r = new Random(seed);
        var scene = new SurveyScene { Seed = seed, Trap = trap, PointStyle = Pick(r, o.PointStyles), LayerStyle = Pick(r, o.LayerStyles) };
        var w = new Wall(r, o, trap);
        var noise = U(r, o.Noise);
        var spacing = o.PicketSpacing;
        var dupRate = U(r, o.DuplicatePoints);
        var gapRate = trap == SurveyTrap.BrokenCrest ? Math.Max(0.06, U(r, o.GapRate)) : U(r, o.GapRate);
        foreach (var b in w.BenchList) scene.Benches.Add((b.Height, b.Angle, b.Berm));

        void AddLine(List<Vec3> pts, SurveyLineClass c, string role, bool closed = false)
        {
            if (pts.Count < 2) return;
            var e = new PolylineEntity { Points = pts, Closed = closed };
            scene.Entities.Add(e);
            scene.LineClasses[e.Id] = c;
            scene.Layers[e.Id] = role;
        }
        void AddPoint(Vec3 p, SurveyPointClass c)
        {
            var e = new PointEntity { Position = p };
            scene.Entities.Add(e);
            scene.PointClasses[e.Id] = c;
            scene.Layers[e.Id] = "points";
        }
        // The surveyor walks a line: a picket every few metres with noise, sometimes a gap; vertices may also be points.
        void Survey(Func<double, Vec3> at, double s0, double s1, SurveyLineClass c, string role, double step0, double step1, bool duplicates = true)
        {
            // all the way round a pit without a gap: the line closes
            var round = w.IsLoop && s0 <= 0 && s1 >= w.Length;
            var piece = new List<Vec3>();
            var s = s0 + U(r, (0, step1));
            while (s < s1)
            {
                var p = at(s);
                p = new Vec3(p.X + Gauss(r) * noise, p.Y + Gauss(r) * noise, p.Z + Gauss(r) * noise);
                piece.Add(p);
                if (duplicates && r.NextDouble() < dupRate) AddPoint(p, SurveyPointClass.OnLine);
                if (r.NextDouble() < gapRate)
                {
                    round = false;
                    AddLine(piece, c, role);
                    piece = new List<Vec3>();
                    s += U(r, (10, 40));
                }
                s += U(r, (step0, step1));
            }
            AddLine(piece, c, role, round);
        }

        // ---- benches: crests always, toes when surveyed (partly or not at all) --------------------------------------
        // this surveyor's picket spacing: somewhere between a lower and an upper bound inside the option range
        var mid = (spacing.Min + spacing.Max) / 2;
        var stepLo = U(r, (spacing.Min, mid));
        var lineStep = (Lo: stepLo, Hi: Math.Max(stepLo + 0.5, U(r, (mid, spacing.Max))));
        var toeChance = trap == SurveyTrap.MissingToes ? 0 : U(r, o.ToeSurveyed);
        var (start, end) = w.IsLoop && r.NextDouble() < 0.7 ? (0.0, w.Length) : (U(r, (0, 0.15)) * w.Length, (1 - U(r, (0, 0.15))) * w.Length);
        var full = w.IsLoop && start == 0 && end == w.Length;
        var twinChance = U(r, o.TwinLines);
        // the same edge surveyed again (another survey, another date): a twin line beside it, along part of its length
        void Twin(Func<double, double> offset, Func<double, double> z, SurveyLineClass c, string role, double a, double b)
        {
            if (r.NextDouble() >= twinChance) return;
            var off = U(r, (0.3, 2.0)) * (r.NextDouble() < 0.5 ? 1 : -1);
            var dz = U(r, (-0.3, 0.3));
            var from = U(r, (a, a + (b - a) * 0.5));
            var to = Math.Min(b, from + (b - a) * U(r, (0.3, 1)));
            Survey(s => w.Ground(s, offset(s) + off).WithZ(z(s) + dz), from, to, c, role, lineStep.Lo, lineStep.Hi, duplicates: false);
        }
        for (int i = 0; i < w.BenchList.Count; i++)
        {
            var k = i;
            var (c0, c1) = (full ? 0 : Math.Max(0, start + U(r, (-10, 10))), full ? w.Length : Math.Min(w.Length, end + U(r, (-10, 10))));
            Survey(s => w.Crest(k, s), c0, c1, SurveyLineClass.Crest, "crest", lineStep.Lo, lineStep.Hi);
            Twin(s => w.CrestOffset(k, s), s => w.CrestZ(k, s), SurveyLineClass.Crest, "crest", c0, c1);
            if (r.NextDouble() < toeChance)
            {
                // a toe may be surveyed only along part of the bench
                var a = r.NextDouble() < 0.6 ? start : U(r, (start, (start + end) / 2));
                var b = r.NextDouble() < 0.6 ? end : U(r, ((start + end) / 2, end));
                Survey(s => w.Toe(k, s), a, b, SurveyLineClass.Toe, "toe", lineStep.Lo, lineStep.Hi);
                Twin(s => w.ToeOffset(k, s), s => w.ToeZ(k, s), SurveyLineClass.Toe, "toe", a, b);
            }
        }

        // ---- spot heights on the top bench, berms and floor, a few on faces -----------------------------------------
        var spot = U(r, o.SpotSpacing);
        foreach (var (d0, d1, cls) in w.Platforms())
        {
            var width = d1 - d0;
            if (width < 1) continue;
            for (var s = start + U(r, (0, spot)); s < end; s += spot * U(r, (0.6, 1.4)))
                for (var d = d0 + U(r, (0.5, Math.Min(spot, width))); d < d1 - 0.5; d += spot * U(r, (0.6, 1.4)))
                {
                    var p = w.Ground(s + U(r, (-spot / 3, spot / 3)), d);
                    AddPoint(new Vec3(p.X + Gauss(r) * noise, p.Y + Gauss(r) * noise, p.Z + Gauss(r) * noise), cls);
                }
        }
        for (int i = 0; i < w.BenchList.Count; i++)
            if (r.NextDouble() < 0.3)
                for (int n = r.Next(1, 4); n > 0; n--)
                {
                    var s = U(r, (start, end));
                    var p = w.Ground(s, U(r, (w.CrestOffset(i, s) + 0.5, w.ToeOffset(i, s) - 0.5)));
                    AddPoint(p, SurveyPointClass.Face);
                }

        // ---- surroundings -------------------------------------------------------------------------------------------
        if (trap == SurveyTrap.RoadAlongCrest || r.NextDouble() < o.RoadChance)
        {
            // a road behind the crest at the same level, or down on a berm or the floor climbing along the wall
            var onTop = trap == SurveyTrap.RoadAlongCrest || r.NextDouble() < 0.5;
            var width = U(r, (7, 15));
            var level = onTop ? -1 : r.Next(0, w.BenchList.Count);
            var grade = onTop ? 0 : U(r, (-0.08, 0.08));
            double Centre(double s) => onTop ? w.CrestOffset(0, s) - U2(3, 12, s) - width / 2 : w.ToeOffset(level, s) + Math.Min(w.Room(level) / 2, width / 2 + 1);
            var s0 = U(r, (start, (start + end) / 2));
            var s1 = U(r, ((start + end) / 2, end));
            foreach (var side in new[] { -0.5, 0.5 })
            {
                Vec3 Edge(double s)
                {
                    var g = w.Ground(s, Centre(s) + side * width);
                    return g.WithZ((onTop ? w.CrestZ(0, s) : g.Z) + grade * (s - s0));
                }
                // a road edge that runs along a face edge is that edge (a ramp's crest or toe), otherwise just a road
                var crestLike = 0;
                var toeLike = 0;
                var samples = 0;
                for (var s = s0; s < s1; s += 5, samples++)
                {
                    var d = Centre(s) + side * width;
                    for (int j = 0; j < w.BenchList.Count; j++)
                    {
                        if (Math.Abs(d - w.CrestOffset(j, s)) < 2) crestLike++;
                        if (Math.Abs(d - w.ToeOffset(j, s)) < 2) toeLike++;
                    }
                }
                var edgeClass = crestLike > samples / 2 ? SurveyLineClass.Crest : toeLike > samples / 2 ? SurveyLineClass.Toe : SurveyLineClass.Other;
                Survey(Edge, s0, s1, edgeClass, edgeClass == SurveyLineClass.Other ? "road" : edgeClass == SurveyLineClass.Crest ? "crest" : "toe", lineStep.Lo, lineStep.Hi);
            }
        }
        if (r.NextDouble() < o.SafetyBermChance)
        {
            // safety berm (windrow) along the crest of a lower bench: its top 1–2 m above, 1.5–3 m back
            var i = r.Next(0, w.BenchList.Count);
            var back = U(r, (1.5, 3));
            var height = U(r, (0.8, 2));
            Survey(s => w.Ground(s, w.CrestOffset(i, s) - back).WithZ(w.CrestZ(i, s) + height), start, end, SurveyLineClass.Other, "berm", lineStep.Lo, lineStep.Hi);
        }
        if (trap == SurveyTrap.ContoursAlongWall || r.NextDouble() < o.TerrainChance)
        {
            // natural ground rising behind the top bench, drawn as contours (interpolated: dense vertices, no pickets)
            var interval = new[] { 1.0, 2.0, 2.5, 5.0 }[r.Next(4)];
            var rise = U(r, (0.05, 0.3));
            var z0 = w.CrestZ(0, w.Length / 2);
            // at most eight contours: the real sample has none, they are here as a look-alike, not as the bulk of the data
            var first = Math.Ceiling((z0 + 0.5) / interval) * interval;
            for (var level = first; level < z0 + rise * 60 && level < first + 8 * interval; level += interval)
            {
                var lv = level;
                var pts = new List<Vec3>();
                for (var s = start; s < end; s += U(r, (1, 3)))
                {
                    var back = (lv - w.CrestZ(0, s)) / rise + w.TopWidth;
                    if (back < 0) continue;
                    pts.Add(w.Ground(s, w.CrestOffset(0, s) - back).WithZ(lv));
                }
                AddLine(pts, SurveyLineClass.Contour, "contour");
            }
            for (var n = (int)(w.Length / Math.Max(10, spot) * 2); n > 0; n--)
            {
                var s = U(r, (start, end));
                var back = U(r, (w.TopWidth, w.TopWidth + 50));
                AddPoint(w.Ground(s, w.CrestOffset(0, s) - back).WithZ(w.CrestZ(0, s) + (back - w.TopWidth) * rise), SurveyPointClass.Other);
            }
        }
        if (r.NextDouble() < o.HolesChance)
        {
            var burden = U(r, (3, 6));
            var spacingH = U(r, (3, 6.5));
            var rows = r.Next(1, 5);
            var s0 = U(r, (start, (start + end) / 2));
            var s1 = Math.Min(end, s0 + U(r, (40, 150)));
            for (int row = 0; row < rows; row++)
                for (var s = s0 + (row % 2) * spacingH / 2; s < s1; s += spacingH)
                    scene.Collars.Add(w.Ground(s, w.CrestOffset(0, s) - burden * (row + 1)));
            // drilled holes are often surveyed too: rows of pickets at the collars
            if (r.NextDouble() < 0.5)
                foreach (var c in scene.Collars) AddPoint(new Vec3(c.X + Gauss(r) * noise, c.Y + Gauss(r) * noise, c.Z + Gauss(r) * noise), SurveyPointClass.BenchTop);
            if (r.NextDouble() < o.BoundaryChance)
            {
                // blast boundary around the holes, at the bench level
                var near = new List<Vec3>();
                foreach (var s in new[] { s0 - 3, s1 + 3 })
                {
                    near.Add(w.Ground(s, w.CrestOffset(0, s) - 1));
                    near.Add(w.Ground(s, w.CrestOffset(0, s) - burden * (rows + 1)));
                }
                var poly = new List<Vec3> { near[0], near[2], near[3], near[1] };
                var steps = new List<Vec3>();
                for (int i = 0; i < 4; i++)
                    for (int k = 0; k < 6; k++) steps.Add(Vec3.Lerp(poly[i], poly[(i + 1) % 4], k / 6.0));
                AddLine(steps, SurveyLineClass.Boundary, "boundary", closed: true);
            }
        }
        if (r.NextDouble() < o.PlanDrawingChance)
        {
            // plan drawing at elevation 0: a coordinate grid and a design outline
            var a = w.Ground(start, -60);
            var b = w.Ground(end, w.TotalWidth + 30);
            for (int n = 0; n < 4; n++)
            {
                var x = Math.Round(Math.Min(a.X, b.X) / 100 + n) * 100;
                AddLine(new() { new Vec3(x, Math.Min(a.Y, b.Y), 0), new Vec3(x, Math.Max(a.Y, b.Y), 0) }, SurveyLineClass.Other, "plan");
            }
            var design = new List<Vec3>();
            for (var s = start; s < end; s += 10) design.Add(w.Ground(s, w.CrestOffset(0, s) + U(r, (-2, 2))).WithZ(0));
            AddLine(design, SurveyLineClass.Other, "plan");
        }

        Dress(scene, r);
        // place the scene somewhere in a real-looking coordinate system, turned at random
        var angle = r.NextDouble() * Math.PI * 2;
        var shift = new Vec3(U(r, (300_000, 700_000)), U(r, (4_000_000, 7_000_000)), 0);
        Vec3 Place(Vec3 p) => new Vec3(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), p.X * Math.Sin(angle) + p.Y * Math.Cos(angle), p.Z) + shift;
        foreach (var e in scene.Entities) e.Transform(Place);
        for (int i = 0; i < scene.Collars.Count; i++) scene.Collars[i] = Place(scene.Collars[i]);
        return scene;

        double U2(double lo, double hi, double s) => lo + (hi - lo) * (0.5 + 0.5 * Math.Sin(s / 37.0 + seed));
    }

    /// <summary>Layer names and colours by the scene's layer style; meaningful names in Russian or English.</summary>
    private static void Dress(SurveyScene scene, Random r)
    {
        string[] Ru(string role) => role switch
        {
            "crest" => new[] { "Бровка верхняя", "Верхняя бровка", "бровка_верх", "Бровки", "ВБ" },
            "toe" => new[] { "Бровка нижняя", "Нижняя бровка", "бровка_низ", "Бровки", "НБ" },
            "contour" => new[] { "Горизонтали", "горизонтали", "Рельеф" },
            "road" => new[] { "Дорога", "Автодорога", "Съезд" },
            "berm" => new[] { "Вал", "Предохранительный вал" },
            "boundary" => new[] { "Контур блока", "Граница", "Блок" },
            "points" => new[] { "Пикеты", "Съемка", "Точки", "Survey" },
            _ => new[] { "Сетка координат", "Проект", "Контур проект" }
        };
        string[] En(string role) => role switch
        {
            "crest" => new[] { "CREST", "Crest", "TOP", "BENCH_TOP" },
            "toe" => new[] { "TOE", "Toe", "BOTTOM", "BENCH_TOE" },
            "contour" => new[] { "CONTOURS", "Contour", "TOPO" },
            "road" => new[] { "ROAD", "RAMP", "Haul road" },
            "berm" => new[] { "BUND", "WINDROW" },
            "boundary" => new[] { "BLAST", "BOUNDARY" },
            "points" => new[] { "POINTS", "SURVEY", "Survey" },
            _ => new[] { "GRID", "DESIGN" }
        };
        var english = r.NextDouble() < 0.4;
        var names = new Dictionary<string, string>();
        string Meaningful(string role) => names.TryGetValue(role, out var n) ? n : names[role] = (english ? En(role) : Ru(role))[r.Next((english ? En(role) : Ru(role)).Length)];
        var numbered = new Dictionary<string, string>();
        var survey = new[] { "0", "0", "0", "2", "3" };
        foreach (var id in scene.Layers.Keys.ToList())
        {
            var role = scene.Layers[id];
            scene.Layers[id] = role == "plan" ? Meaningful("plan") : scene.LayerStyle switch
            {
                SurveyLayerStyle.AllZero => role == "points" && r.NextDouble() < 0.5 ? "Survey" : "0",
                SurveyLayerStyle.Meaningful => Meaningful(role),
                SurveyLayerStyle.Mixed => r.NextDouble() < 0.25 ? new[] { "0", "1", "Layer1", Meaningful("road"), Meaningful("crest") }[r.Next(5)] : Meaningful(role),
                SurveyLayerStyle.Numbered => numbered.TryGetValue(role, out var n) ? n : numbered[role] = (numbered.Count + 1).ToString(),
                _ => role == "points" ? "Survey" : survey[r.Next(survey.Length)]
            };
        }
        foreach (var layer in scene.Layers.Values.Distinct())
            scene.LayerColors[layer] = new uint[] { 0xFFFF0000, 0xFFFFFF00, 0xFF00FF00, 0xFF00FFFF, 0xFF0000FF, 0xFFFF00FF, 0xFFFFFFFF, 0xFF808080 }[r.Next(8)];
        // a few objects coloured on their own
        foreach (var e in scene.Entities)
            if (r.NextDouble() < 0.1) e.Color = new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFF00 }[r.Next(4)];
    }

    // ---- the bench wall --------------------------------------------------------------------------------------------

    /// <summary>
    /// A bench wall in a local frame: a base curve in plan (the top crest line without wear), the bench profile across it
    /// (distance d along the curve's normal, growing downhill) and wear along it. Ground(s, d) is the surface.
    /// </summary>
    private sealed class Wall
    {
        public sealed record Bench(double Height, double Angle, double Berm, double Width);

        public readonly List<Bench> BenchList = new();
        public readonly double Length;
        /// <summary>A whole pit: the base curve is closed and lines walked all round close.</summary>
        public readonly bool IsLoop;
        public readonly double TopWidth;
        private readonly List<Vec3> _base = new();
        private readonly List<Vec3> _normal = new();
        private readonly double _z0;
        private readonly Func<double, double>[] _wander;
        private readonly Func<double, double>[] _erosion;
        private readonly Func<double, double>[] _muck;
        private readonly double _floorSlope;

        public Wall(Random r, SurveySynthOptions o, SurveyTrap trap)
        {
            Length = U(r, o.Length);
            TopWidth = U(r, (15, 60));
            _z0 = U(r, (200, 1200));
            var n = r.Next(o.Benches.Min, o.Benches.Max + 1);
            if (trap == SurveyTrap.NarrowBerms) n = Math.Max(n, 3);
            for (int i = 0; i < n; i++)
            {
                var h = U(r, o.BenchHeight);
                var a = U(r, o.FaceAngle);
                var berm = trap == SurveyTrap.NarrowBerms ? U(r, (1.5, 4)) : U(r, o.BermWidth);
                BenchList.Add(new Bench(h, a, berm, h / Math.Tan(a * Math.PI / 180)));
            }
            _floorSlope = U(r, (-0.02, 0.02));
            var total = TotalWidth;

            // Base curve, sampled every metre; downhill is to the right of the direction of travel. A pit is a closed
            // loop walked clockwise so its benches step inwards. Curvature stays gentle enough (radius at least twice the
            // wall's width) that the offset crest and toe lines never cross themselves.
            var shape = r.NextDouble() < 0.35 ? 4 : r.Next(4);
            var pts = new List<Vec3>();
            if (shape == 4)
            {
                IsLoop = true;
                var a = Math.Max(3 * total, U(r, (150, 450)));
                var bMin = Math.Max(1.7 * total, Math.Sqrt(2 * total * a));
                var b = Math.Min(a, Math.Max(bMin, U(r, (0.5, 1)) * a));
                for (int k = 0; k < 4000; k++)
                {
                    var t = 2 * Math.PI * k / 4000;
                    pts.Add(new Vec3(a * Math.Cos(t), -b * Math.Sin(t), 0));
                }
            }
            else
            {
                var radius = Math.Max(total * 2.5, U(r, (120, 500))) * (r.NextDouble() < 0.5 ? 1 : -1);
                var corner = U(r, (0.25, 0.9)) * (r.NextDouble() < 0.5 ? 1 : -1);
                var bend = Math.Abs(corner) * Math.Max(2 * total, 30);
                var wave = U(r, (60, 220));
                var amp = Math.Min(U(r, (4, 18)), Math.Pow(wave / (2 * Math.PI), 2) / (2 * total));
                var q = Vec3.Zero;
                for (double sd = 0; sd <= Length; sd += Step)
                {
                    // straight, arc, a rounded corner in the middle, or a wave y = amp·sin(2πs/wave)
                    var heading = shape switch
                    {
                        1 => sd / radius,
                        2 => Math.Clamp((sd - (Length - bend) / 2) / bend, 0, 1) * corner,
                        3 => Math.Atan(amp * 2 * Math.PI / wave * Math.Cos(2 * Math.PI * sd / wave)),
                        _ => 0
                    };
                    pts.Add(q);
                    q += new Vec3(Math.Cos(heading), Math.Sin(heading), 0) * Step;
                }
            }
            // resample by arc length and take the normals from the tangents
            var run = 0.0;
            _base.Add(pts[0]);
            for (int k = 1; k < pts.Count; k++)
            {
                run += Vec3.Distance2D(pts[k - 1], pts[k]);
                if (run >= Step) { _base.Add(pts[k]); run = 0; }
            }
            Length = (_base.Count - 1) * Step;
            for (int k = 0; k < _base.Count; k++)
            {
                var prev = _base[IsLoop ? (k - 1 + _base.Count) % _base.Count : Math.Max(0, k - 1)];
                var next = _base[IsLoop ? (k + 1) % _base.Count : Math.Min(_base.Count - 1, k + 1)];
                var t = (next - prev).WithZ(0).Normalized();
                _normal.Add(new Vec3(t.Y, -t.X, 0));
            }

            Func<double, double> Smooth(double amplitude)
            {
                var (f1, f2, f3) = (U(r, (0.005, 0.02)), U(r, (0.02, 0.06)), U(r, (0.06, 0.15)));
                var (p1, p2, p3) = (r.NextDouble() * 7, r.NextDouble() * 7, r.NextDouble() * 7);
                return s => amplitude * (0.6 * Math.Sin(f1 * s + p1) + 0.3 * Math.Sin(f2 * s + p2) + 0.1 * Math.Sin(f3 * s + p3));
            }
            _wander = BenchList.Select(_ => Smooth(U(r, o.CrestWander))).ToArray();
            var eroded = trap == SurveyTrap.BrokenCrest || r.NextDouble() < o.ErosionChance;
            // erosion pulls the crest back into the bench, by up to a few metres, in patches
            _erosion = BenchList.Select(_ => { var f = Smooth(eroded ? U(r, (1, 4)) : 0); return (Func<double, double>)(s => Math.Max(0, f(s))); }).ToArray();
            var mucky = trap == SurveyTrap.MuckAtToe || r.NextDouble() < o.MuckChance;
            _muck = BenchList.Select(_ => { var f = Smooth(mucky ? U(r, (1, 3)) : 0); return (Func<double, double>)(s => Math.Max(0, f(s) + (mucky ? 1 : 0))); }).ToArray();
        }

        public double TotalWidth => BenchList.Sum(b => b.Width + b.Berm);

        private int Index(double s) => IsLoop ? (((int)Math.Round(s / Step)) % _base.Count + _base.Count) % _base.Count : Math.Clamp((int)Math.Round(s / Step), 0, _base.Count - 1);
        private Vec3 At(double s, double d) { var i = Index(s); return _base[i] + _normal[i] * d; }

        /// <summary>Offset of crest i from the base curve at s (worn crests sit further back, negative is uphill).</summary>
        public double CrestOffset(int i, double s)
        {
            var d = 0.0;
            for (int k = 0; k < i; k++) d += BenchList[k].Width + BenchList[k].Berm;
            return d - _erosion[i](s);
        }

        public double ToeOffset(int i, double s)
        {
            var d = 0.0;
            for (int k = 0; k <= i; k++) d += BenchList[k].Width + (k < i ? BenchList[k].Berm : 0);
            return d + _muck[i](s) * 0.5;
        }

        public double CrestZ(int i, double s)
        {
            var z = _z0;
            for (int k = 0; k < i; k++) z -= BenchList[k].Height;
            return z + _wander[i](s) - _erosion[i](s) * 0.3;
        }

        public double ToeZ(int i, double s) => CrestZ(i, s) - BenchList[i].Height + _erosion[i](s) * 0.3 + _muck[i](s) * 0.5;

        /// <summary>Usable width of the berm (or floor) below bench i.</summary>
        public double Room(int i) => i < BenchList.Count - 1 ? BenchList[i].Berm : 40;

        public Vec3 Crest(int i, double s) => At(s, CrestOffset(i, s)).WithZ(CrestZ(i, s));
        public Vec3 Toe(int i, double s) => At(s, ToeOffset(i, s)).WithZ(ToeZ(i, s));

        /// <summary>The ground at (s, d): top bench, faces, berms and floor, with a little roughness.</summary>
        public Vec3 Ground(double s, double d)
        {
            double z;
            if (d <= CrestOffset(0, s)) z = CrestZ(0, s);
            else
            {
                z = ToeZ(BenchList.Count - 1, s) + (d - ToeOffset(BenchList.Count - 1, s)) * _floorSlope;
                for (int i = 0; i < BenchList.Count; i++)
                {
                    double c = CrestOffset(i, s), t = ToeOffset(i, s);
                    if (d <= c) { z = CrestZ(i, s); break; }
                    if (d <= t) { z = CrestZ(i, s) + (ToeZ(i, s) - CrestZ(i, s)) * (d - c) / Math.Max(0.1, t - c); break; }
                    if (i + 1 < BenchList.Count && d <= CrestOffset(i + 1, s))
                    {
                        var c2 = CrestOffset(i + 1, s);
                        z = ToeZ(i, s) + (CrestZ(i + 1, s) - ToeZ(i, s)) * (d - t) / Math.Max(0.1, c2 - t);
                        break;
                    }
                }
            }
            z += 0.08 * Math.Sin(0.7 * s + 1.3 * d) + 0.05 * Math.Sin(1.9 * s - 0.6 * d);
            return At(s, d).WithZ(z);
        }

        /// <summary>Bands across the wall where spot heights are taken: the top bench, each berm, the floor.</summary>
        public IEnumerable<(double From, double To, SurveyPointClass Class)> Platforms()
        {
            yield return (-TopWidth, -1, SurveyPointClass.BenchTop);
            for (int i = 0; i < BenchList.Count; i++)
            {
                var from = ToeOffset(i, Length / 2) + 0.5;
                var to = i + 1 < BenchList.Count ? CrestOffset(i + 1, Length / 2) - 0.5 : from + FloorWidth;
                yield return (from, to, SurveyPointClass.Floor);
            }
        }

        /// <summary>How far the floor below the last bench is surveyed.</summary>
        private const double FloorWidth = 30;
    }

    // ---- random helpers ----------------------------------------------------------------------------------------------

    private static double U(Random r, (double Min, double Max) range) => range.Min + r.NextDouble() * (range.Max - range.Min);

    private static double Gauss(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static T Pick<T>(Random r, IReadOnlyList<(T Item, double Weight)> items)
    {
        var total = items.Sum(i => i.Weight);
        var x = r.NextDouble() * total;
        foreach (var (item, weight) in items)
        {
            if (x < weight) return item;
            x -= weight;
        }
        return items[^1].Item;
    }
}
