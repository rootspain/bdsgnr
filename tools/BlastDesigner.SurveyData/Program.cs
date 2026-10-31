using System.Globalization;
using System.Text;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;

// Training data for the survey recognition (doc/onnx-surface-types.md). Every scene goes through the real import:
// generated survey → DXF → CadFiles.ReadCad → features. Output stays on this computer; nothing here reads customer data
// unless a drawing is passed to "stats".
//
//   dataset <out.csv> <count> <train|shifted> [first seed] [--traps]   segment features with the true class
//   sample  <out.dxf> <seed> [train|shifted] [trap]                    one scene as a drawing, to look at in the program
//   stats   <drawing.dwg|dxf>                                         the survey "handwriting" of a drawing
//   stats-synth <count> <train|shifted> [first seed]                  the same over synthetic scenes
var ci = CultureInfo.InvariantCulture;
if (args.Length == 0) return Usage();
try
{
    switch (args[0])
    {
        case "dataset" when args.Length >= 4:
            return Dataset(args[1], int.Parse(args[2], ci), Preset(args[3]), args.Length > 4 && int.TryParse(args[4], out var s0) ? s0 : 1, args.Contains("--traps"));
        case "sample" when args.Length >= 3:
        {
            var scene = SurveySynth.Generate(int.Parse(args[2], ci), args.Length > 3 ? Preset(args[3]) : null, args.Length > 4 ? Enum.Parse<SurveyTrap>(args[4], true) : SurveyTrap.None);
            SurveyDxf.Write(scene, args[1]);
            Console.WriteLine($"{args[1]}: {scene.Lines.Count()} lines, {scene.Points.Count()} points, {scene.Benches.Count} benches, points as {scene.PointStyle}, layers {scene.LayerStyle}");
            foreach (var g in scene.LineClasses.Values.GroupBy(c => c)) Console.WriteLine($"  {g.Key,-9} {g.Count()}");
            return 0;
        }
        case "render" when args.Length >= 3:
        {
            // a scene (seed) or a drawing (path) as a plan picture; synthetic lines coloured by their true class
            var png = args[1];
            var p = BlastProject.CreateNew("render");
            var layer = p.GetOrCreateLayer("survey", 0xFF64748B);
            if (int.TryParse(args[2], out var seed))
            {
                var scene = SurveySynth.Generate(seed, args.Length > 3 ? Preset(args[3]) : null, args.Length > 4 ? Enum.Parse<SurveyTrap>(args[4], true) : SurveyTrap.None);
                foreach (var e in scene.Entities)
                {
                    e.Color = e switch
                    {
                        PolylineEntity => scene.LineClasses[e.Id] switch
                        {
                            SurveyLineClass.Crest => 0xFFDC2626, SurveyLineClass.Toe => 0xFF2563EB, SurveyLineClass.Contour => 0xFF16A34A,
                            SurveyLineClass.Boundary => 0xFF9333EA, _ => 0xFF9CA3AF
                        },
                        _ => scene.PointClasses[e.Id] switch
                        {
                            SurveyPointClass.BenchTop => 0xFFF59E0B, SurveyPointClass.Floor => 0xFF0EA5E9, SurveyPointClass.OnLine => 0xFF111827, _ => 0xFFD1D5DB
                        }
                    };
                    if (e is PolylineEntity pl && pl.Points.All(v => v.Z == 0)) continue;
                    p.Add(e, layer);
                }
                Console.WriteLine($"benches: {string.Join("; ", scene.Benches.Select(b => $"h={b.Height:0.0} a={b.FaceAngle:0} berm={b.Berm:0.0}"))}");
            }
            else
                foreach (var (l, e) in CadFiles.ReadCad(args[2], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip }).Items)
                    if (e is not PolylineEntity pl || SurveyFeatures.IsSurveyLine(pl)) p.Add(e, layer);
            var bounds = Bounds3.Empty;
            foreach (var e in p.Entities) foreach (var v in e.GetVertices()) bounds.Add(v);
            var cam = new BlastDesigner.Rendering.Camera { Width = 1600, Height = 1200 };
            cam.Fit(bounds, 0.03);
            File.WriteAllBytes(png, BlastDesigner.Rendering.Reports.RenderPng(p, cam, 1600, 1200, new BlastDesigner.Rendering.SceneOptions { DrawSummaryBlock = false, DrawHoleLegend = false, DrawTieLegend = false }));
            Console.WriteLine(png);
            return 0;
        }
        case "stats" when args.Length >= 2:
        {
            // what the drawing shows: layers switched off hold plan drawing, not survey
            var drawing = CadFiles.ReadCad(args[1], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip });
            Print(Measure(drawing.Items.Select(i => i.Entity)));
            return 0;
        }
        case "calibrate" when args.Length >= 3:
        {
            // is the drawing's handwriting inside what the generator produces (between its 5th and 95th percentile)?
            var real = Measure(CadFiles.ReadCad(args[1], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip }).Items.Select(i => i.Entity));
            var all = Enumerable.Range(1, int.Parse(args[2], ci)).Select(seed => Measure(Imported(SurveySynth.Generate(seed, args.Length > 3 ? Preset(args[3]) : null)).Items.Select(i => i.Entity))).ToList();
            var outside = 0;
            foreach (var p in typeof(SurveyStats).GetProperties().Where(p => p.Name is not ("Lines" or "Points" or "CrestLike" or "ToeLike" or "Middle" or "Lonely")))
            {
                var v = all.Select(x => Convert.ToDouble(p.GetValue(x), ci)).OrderBy(x => x).ToList();
                var (lo, hi) = (v[(int)(v.Count * 0.05)], v[Math.Min(v.Count - 1, (int)(v.Count * 0.95))]);
                var r = Convert.ToDouble(p.GetValue(real), ci);
                var ok = r >= lo && r <= hi;
                if (!ok) outside++;
                Console.WriteLine($"{p.Name,-20} real={r,8:0.00}   synthetic {lo,8:0.00} … {hi,8:0.00}   {(ok ? "ok" : "OUTSIDE")}");
            }
            return outside == 0 ? 0 : 3;
        }
        case "evaluate" when args.Length >= 3:
            return Evaluate(int.Parse(args[1], ci), Preset(args[2]), args.Length > 3 && int.TryParse(args[3], out var e0) ? e0 : 1, args.Contains("--traps"));
        case "evaluate-labels" when args.Length >= 3:
        {
            // a drawing against its hand-checked labels (saved from the bench elements window); stays in this terminal
            var drawing = CadFiles.ReadCad(args[1], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip });
            var lines = drawing.Items.Where(x => x.Entity is PolylineEntity).Select(x => ((PolylineEntity)x.Entity, x.Layer)).ToList();
            var byKey = lines.GroupBy(l => SurveyLabels.Key(l.Item1)).ToDictionary(g => g.Key, g => g.First().Item1);
            var truth = SurveyLabels.Load(args[2]).Where(l => byKey.ContainsKey(l.Line)).ToDictionary(l => (byKey[l.Line].Id, l.From, l.To), l => l.Class);
            var r = SurveyRecognizer.Recognize(lines, drawing.Items.Select(x => x.Entity).OfType<PointEntity>().ToList(), Array.Empty<Vec3>());
            var classes = Enum.GetValues<SurveyLineClass>();
            var m = new double[classes.Length, classes.Length];
            var byId = lines.ToDictionary(l => l.Item1.Id, l => l.Item1);
            foreach (var s in r.Segments)
                if (truth.TryGetValue((s.Segment.LineId, s.Segment.From, s.Segment.To), out var t))
                    m[(int)t, (int)s.Class] += SurveyRecognition.Length(byId[s.Segment.LineId].Points, s.Segment);
            var total = classes.Sum(t => classes.Sum(f => m[(int)t, (int)f]));
            Console.WriteLine($"labelled segments matched: {truth.Count}, length {total:0} m");
            foreach (var t in classes)
            {
                var row = classes.Sum(f => m[(int)t, (int)f]);
                if (row > 0) Console.WriteLine($"{t,-9} recall {m[(int)t, (int)t] / row:P1}  ({row:0} m)");
            }
            Console.WriteLine($"accuracy by length {classes.Sum(k => m[(int)k, (int)k]) / Math.Max(1, total):P1}");
            return 0;
        }
        case "features" when args.Length >= 3:
        {
            // segment features of a drawing (no true class) with the rules' answer, to run a trained model on it;
            // derived from customer data: keep the output next to the drawing (samples/, not in the repository)
            var drawing = CadFiles.ReadCad(args[1], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip });
            var lines = drawing.Items.Where(x => x.Entity is PolylineEntity).Select(x => ((PolylineEntity)x.Entity, x.Layer)).ToList();
            var all = drawing.Items.Select(x => x.Entity).OfType<PointEntity>().ToList();
            var features = SurveyFeatures.Compute(lines, all.Where(p => Math.Abs(p.Position.Z) > 1e-6).Select(p => p.Position).ToList(), Array.Empty<Vec3>());
            var rules = SurveyRecognizer.Recognize(lines, all, Array.Empty<Vec3>(), features).Segments.ToDictionary(g => g.Segment, g => g.Class);
            using var w = new StreamWriter(args[2], false, new UTF8Encoding(false));
            w.WriteLine("scene,trap,line,from,to,label,rule," + string.Join(",", SurveyFeatures.Names));
            foreach (var (seg, values) in features)
                w.WriteLine(string.Join(",", new[] { "0", "None", seg.LineId.ToString("N")[..8], seg.From.ToString(ci), seg.To.ToString(ci), "", rules[seg].ToString() }.Concat(values.Select(v => v.ToString("0.####", ci)))));
            Console.WriteLine($"{args[2]}: {features.Count} segments");
            return 0;
        }
        case "recognize" when args.Length >= 2:
        {
            // a real drawing has no true classes: print what was found, to judge by eye (stays in this terminal)
            var drawing = CadFiles.ReadCad(args[1], new CadImportOptions { HiddenLayers = CadHiddenLayers.Skip });
            var lines = drawing.Items.Where(x => x.Entity is PolylineEntity).Select(x => ((PolylineEntity)x.Entity, x.Layer)).ToList();
            var points = drawing.Items.Select(x => x.Entity).OfType<PointEntity>().ToList();
            var r = SurveyRecognizer.Recognize(lines, points, Array.Empty<Vec3>());
            var byId = lines.ToDictionary(l => l.Item1.Id, l => l.Item1);
            foreach (var g in r.Segments.GroupBy(s => s.Class))
                Console.WriteLine($"{g.Key,-9} {g.Count(),5} segments  {g.Sum(s => SurveyRecognition.Length(byId[s.Segment.LineId].Points, s.Segment)),8:0} m   to check: {g.Count(s => s.Confidence < SurveyRecognizer.CheckBelow)}");
            Console.WriteLine($"benches: {r.Benches.Count}, with toe: {r.Benches.Count(b => b.Toe != null)}");
            foreach (var b in r.Benches.OrderBy(b => byId[b.Crest].Points.Average(p => p.Z)))
                Console.WriteLine($"  crest at {byId[b.Crest].Points.Average(p => p.Z),7:0.0}  height {b.Height,5:0.0}  " + (b.Toe == null ? "no toe              " : $"face {b.FaceWidth,5:0.0} m  angle {b.FaceAngle,3:0}") + $"  length {GeoMath.Length(byId[b.Crest].Points),6:0} m");
            foreach (var g in r.Points.Values.GroupBy(p => p.Class)) Console.WriteLine($"points {g.Key,-9} {g.Count()}");
            return 0;
        }
        case "stats-synth" when args.Length >= 3:
        {
            var first = args.Length > 3 ? int.Parse(args[3], ci) : 1;
            var all = Enumerable.Range(first, int.Parse(args[1], ci)).Select(seed => Measure(Imported(SurveySynth.Generate(seed, Preset(args[2]))).Items.Select(i => i.Entity))).ToList();
            foreach (var p in typeof(SurveyStats).GetProperties())
            {
                var v = all.Select(x => Convert.ToDouble(p.GetValue(x), ci)).OrderBy(x => x).ToList();
                Console.WriteLine($"{p.Name,-20} p10={v[(int)(v.Count * 0.1)],8:0.00} med={v[v.Count / 2],8:0.00} p90={v[Math.Min(v.Count - 1, (int)(v.Count * 0.9))],8:0.00}");
            }
            return 0;
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
return Usage();

static int Usage()
{
    Console.WriteLine("dataset <out.csv> <count> <train|shifted> [first seed] [--traps]\nsample <out.dxf> <seed> [train|shifted] [trap]\nstats <drawing>\nstats-synth <count> <train|shifted> [first seed]");
    return 2;
}

static SurveySynthOptions Preset(string name) => name.ToLowerInvariant() switch
{
    "train" => SurveySynthOptions.Train,
    "shifted" => SurveySynthOptions.Shifted,
    _ => throw new ArgumentException("preset: train or shifted")
};

// A scene through the real import path: written as DXF, read back by the program's importer.
static CadImport Imported(SurveyScene scene)
{
    var path = Path.Combine(Path.GetTempPath(), $"bd_survey_{Guid.NewGuid():N}.dxf");
    try
    {
        SurveyDxf.Write(scene, path);
        return CadFiles.ReadCad(path, new CadImportOptions());
    }
    finally { File.Delete(path); }
}

static SurveyStats Measure(IEnumerable<Entity> entities)
{
    var list = entities.ToList();
    return SurveyStats.Measure(list.OfType<PolylineEntity>().ToList(), list.OfType<PointEntity>().Where(p => Math.Abs(p.Position.Z) > 1e-6).Select(p => p.Position).ToList());
}

static void Print(SurveyStats s)
{
    foreach (var p in typeof(SurveyStats).GetProperties()) Console.WriteLine($"{p.Name,-20} {Convert.ToDouble(p.GetValue(s), CultureInfo.InvariantCulture),10:0.00}");
}

static int Dataset(string outPath, int count, SurveySynthOptions options, int firstSeed, bool traps)
{
    var ci = CultureInfo.InvariantCulture;
    var trapList = Enum.GetValues<SurveyTrap>();
    using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
    w.WriteLine("scene,trap,line,from,to,label,rule," + string.Join(",", SurveyFeatures.Names));
    int rows = 0, unmatchedTotal = 0;
    for (int i = 0; i < count; i++)
    {
        var seed = firstSeed + i;
        var trap = traps ? trapList[i % trapList.Length] : SurveyTrap.None;
        var scene = SurveySynth.Generate(seed, options, trap);
        var drawing = Imported(scene);
        var (lines, _, unmatched) = SurveyDxf.Transfer(scene, drawing.Items.Select(x => x.Entity));
        unmatchedTotal += unmatched;
        var survey = drawing.Items.Where(x => x.Entity is PolylineEntity).Select(x => ((PolylineEntity)x.Entity, x.Layer)).ToList();
        var points = drawing.Items.Select(x => x.Entity).OfType<PointEntity>().Where(p => Math.Abs(p.Position.Z) > 1e-6).Select(p => p.Position).ToList();
        var features = SurveyFeatures.Compute(survey, points, scene.Collars);
        // what the rules say about the same segments, to compare a model with them row by row
        var rules = SurveyRecognizer.Recognize(survey, drawing.Items.Select(x => x.Entity).OfType<PointEntity>().ToList(), scene.Collars, features)
            .Segments.ToDictionary(g => g.Segment, g => g.Class);
        foreach (var (seg, values) in features)
        {
            if (!lines.TryGetValue(seg.LineId, out var label)) continue;
            w.WriteLine(string.Join(",", new[] { seed.ToString(ci), trap.ToString(), seg.LineId.ToString("N")[..8], seg.From.ToString(ci), seg.To.ToString(ci), label.ToString(), rules[seg].ToString() }
                .Concat(values.Select(v => v.ToString("0.####", ci)))));
            rows++;
        }
        if ((i + 1) % 50 == 0) Console.WriteLine($"{i + 1}/{count} scenes, {rows} segments");
    }
    Console.WriteLine($"{outPath}: {count} scenes, {rows} segments, {unmatchedTotal} imported objects without a true class");
    return 0;
}

// Recognition quality on synthetic scenes (through the import): confusion by segment length, per class and per trap.
static int Evaluate(int count, SurveySynthOptions options, int firstSeed, bool traps)
{
    var classes = Enum.GetValues<SurveyLineClass>();
    var confusion = new double[classes.Length, classes.Length];
    var trapList = Enum.GetValues<SurveyTrap>();
    var perTrap = new Dictionary<SurveyTrap, (double Right, double All)>();
    var benchErr = new List<double>();
    double checkLen = 0, checkWrong = 0, restLen = 0, restWrong = 0;
    for (int i = 0; i < count; i++)
    {
        var trap = traps ? trapList[i % trapList.Length] : SurveyTrap.None;
        var scene = SurveySynth.Generate(firstSeed + i, options, trap);
        var drawing = Imported(scene);
        var (truth, _, _) = SurveyDxf.Transfer(scene, drawing.Items.Select(x => x.Entity));
        var lines = drawing.Items.Where(x => x.Entity is PolylineEntity).Select(x => ((PolylineEntity)x.Entity, x.Layer)).ToList();
        var byId = lines.ToDictionary(l => l.Item1.Id, l => l.Item1);
        var r = SurveyRecognizer.Recognize(lines, drawing.Items.Select(x => x.Entity).OfType<PointEntity>().ToList(), scene.Collars);
        var (right, all) = perTrap.GetValueOrDefault(trap);
        foreach (var s in r.Segments)
        {
            if (!truth.TryGetValue(s.Segment.LineId, out var t)) continue;
            var len = SurveyRecognition.Length(byId[s.Segment.LineId].Points, s.Segment);
            confusion[(int)t, (int)s.Class] += len;
            all += len;
            if (t == s.Class) right += len;
            if (s.Confidence < SurveyRecognizer.CheckBelow) { checkLen += len; if (t != s.Class) checkWrong += len; }
            else { restLen += len; if (t != s.Class) restWrong += len; }
        }
        perTrap[trap] = (right, all);
        // bench heights against the generator's
        foreach (var b in r.Benches)
        {

            var nearest = scene.Benches.Select(x => x.Height).OrderBy(h => Math.Abs(h - b.Height)).First();
            benchErr.Add(Math.Abs(b.Height - nearest));
        }
    }
    Console.WriteLine("true | found  " + string.Join("", classes.Select(c => $"{c,10}")) + "    recall");
    for (int t = 0; t < classes.Length; t++)
    {
        var row = Enumerable.Range(0, classes.Length).Sum(f => confusion[t, f]);
        Console.WriteLine($"{classes[t],-13}" + string.Join("", Enumerable.Range(0, classes.Length).Select(f => $"{(row > 0 ? confusion[t, f] / row : 0),10:P0}")) + $"    {(row > 0 ? confusion[t, t] / row : 0):P1}");
    }
    for (int f = 0; f < classes.Length; f++)
    {
        var col = Enumerable.Range(0, classes.Length).Sum(t => confusion[t, f]);
        Console.WriteLine($"precision {classes[f],-9} {(col > 0 ? confusion[f, f] / col : 0):P1}");
    }
    var total = Enumerable.Range(0, classes.Length).Sum(t => Enumerable.Range(0, classes.Length).Sum(f => confusion[t, f]));
    Console.WriteLine($"accuracy by length {Enumerable.Range(0, classes.Length).Sum(k => confusion[k, k]) / total:P1}");
    foreach (var (trap, (right, all)) in perTrap) Console.WriteLine($"  {trap,-18} {right / Math.Max(1, all):P1}");
    Console.WriteLine($"marked to check: {checkLen / Math.Max(1, checkLen + restLen):P1} of length, wrong among them {checkWrong / Math.Max(1, checkLen):P1}, wrong among the rest {restWrong / Math.Max(1, restLen):P1}");
    if (benchErr.Count > 0) Console.WriteLine($"bench height error: median {SurveyFeatures.Median(benchErr):0.00} m over {benchErr.Count} benches");
    return 0;
}
