using BlastDesigner.Calc;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>Долгий сеанс расчёта рядом с документом: синхронизация после правки, отмены и смены профиля, отказ отвечать в середине правки.</summary>
public class CalcLiveTests
{
    private static (Document Doc, LiveCalc Live) Open(int seed = 2)
    {
        var doc = new Document(CalcParityTests.Generate(seed));
        return (doc, CalcService.Attach(doc));
    }

    private static Hole Loaded(BlastProject p) => p.Holes.First(h => !h.IsDummy && h.Decks.Any(d => p.FindProduct(d.ProductId)?.IsExplosive == true));

    [Fact]
    public void Attach_IsOnePerDocument_AndAnswersForTheProject()
    {
        var (doc, live) = Open();
        Assert.Same(live, CalcService.Attach(doc));
        var s = CalcService.Live(doc.Project);
        Assert.NotNull(s);
        Assert.Same(s, CalcService.Live(doc.Project));
        Assert.Null(CalcService.Live(CalcParityTests.Generate(2)));
        var h = Loaded(doc.Project);
        Assert.Equal(Legacy.ExplosiveMass(doc.Project, h), Charging.ExplosiveMass(doc.Project, h), 9);
    }

    [Fact]
    public void Execute_SyncsChangedFields_UndoAndRedoFollow()
    {
        var (doc, live) = Open();
        var h = Loaded(doc.Project);
        var before = Charging.ExplosiveMass(doc.Project, h);
        var syncs = live.Syncs;
        doc.Execute("diameter", p => ((Hole)p.FindEntity(h.Id)!).Diameter += 25);
        var after = Charging.ExplosiveMass(doc.Project, h);
        Assert.True(after > before);
        Assert.Equal(Legacy.ExplosiveMass(doc.Project, h), after, 9);
        Assert.Equal(syncs + 1, live.Syncs);
        // без правок повторное чтение не синхронизирует
        Charging.ExplosiveMass(doc.Project, h);
        Assert.Equal(syncs + 1, live.Syncs);

        doc.Undo();
        var restored = (Hole)doc.Project.FindEntity(h.Id)!;
        Assert.Equal(before, Charging.ExplosiveMass(doc.Project, restored), 9);
        doc.Redo();
        Assert.Equal(after, Charging.ExplosiveMass(doc.Project, (Hole)doc.Project.FindEntity(h.Id)!), 9);
    }

    [Fact]
    public void DuringExecute_ValuesComeFromTheModelAsItIs()
    {
        var (doc, _) = Open();
        var h = Loaded(doc.Project);
        Charging.ExplosiveMass(doc.Project, h);
        double inside = 0;
        doc.Execute("load", p =>
        {
            var hole = (Hole)p.FindEntity(h.Id)!;
            hole.LoadingFactor = 1.5;
            // кэш долгого сеанса устарел, правка ещё идёт: отвечает короткий сеанс по текущей модели
            inside = Charging.ExplosiveMass(p, hole);
        });
        Assert.Equal(Legacy.ExplosiveMass(doc.Project, h), inside, 9);
        Assert.Equal(inside, Charging.ExplosiveMass(doc.Project, h), 9);
    }

    [Fact]
    public void PreviewEntities_NotInTheProject_GetAShortSession()
    {
        var (doc, _) = Open();
        var h = Loaded(doc.Project);
        var copy = (Hole)h.EditCopy();
        copy.Diameter = 200;
        Assert.Equal(Legacy.ExplosiveMass(doc.Project, copy), Charging.ExplosiveMass(doc.Project, copy), 9);
        var deck = copy.Decks.First(d => doc.Project.FindProduct(d.ProductId)?.IsExplosive == true);
        Assert.Equal(Legacy.DeckMass(doc.Project, copy, deck), Charging.DeckMass(doc.Project, copy, deck), 9);
        // интервал своей скважины, но проверяемый с чужой скважиной — тоже короткий сеанс
        var own = h.Decks.First(d => doc.Project.FindProduct(d.ProductId)?.IsExplosive == true);
        Assert.Equal(Legacy.DeckMass(doc.Project, copy, own), Charging.DeckMass(doc.Project, copy, own), 9);
    }

    [Fact]
    public void ProfileChange_GivesANewSession()
    {
        var (doc, _) = Open();
        var h = Loaded(doc.Project);
        var first = CalcService.Live(doc.Project);
        var before = Charging.ExplosiveMass(doc.Project, h);
        var mine = CalcProfile.DeriveFrom(CalcService.BuiltIn, "Live test");
        mine.Formulas.Add(new FormulaDef { Property = "hole.chargeMass", Expression = "3 * sum(decks where product.isExplosive, mass)" });
        doc.Execute("profile", p => CalcService.SetProfile(p, mine));
        Assert.NotSame(first, CalcService.Live(doc.Project));
        var expected = 3 * h.Decks.Where(d => doc.Project.FindProduct(d.ProductId)?.IsExplosive == true).Sum(d => Legacy.DeckMass(doc.Project, h, d));
        Assert.Equal(expected, Charging.ExplosiveMass(doc.Project, (Hole)doc.Project.FindEntity(h.Id)!), 9);
        doc.Undo();
        Assert.Equal(before, Charging.ExplosiveMass(doc.Project, (Hole)doc.Project.FindEntity(h.Id)!), 9);
        // параметры проекта — тоже новый сеанс
        doc.Execute("param", p => p.Calc = new ProjectCalc { ProfileName = CalcService.BuiltInName, Parameters = new() { ["site.vib.K"] = 1234 } });
        Assert.Equal(1234, CalcService.Live(doc.Project)!.ParameterValue("site.vib.K"));
    }

    [Fact]
    public void TimingSolve_TouchesTheSession()
    {
        var (doc, _) = Open(3);
        var mic = CalcService.Value(doc.Project, "blast.mic", doc.Project).Number;
        foreach (var t in doc.Project.Ties) t.DelayMs = 0;
        foreach (var h in doc.Project.Holes) h.ElectronicTimeMs = 0;
        Timing.Solve(doc.Project);
        Assert.Equal(Legacy.Mic(doc.Project, doc.Project.Vibration.WindowMs), CalcService.Value(doc.Project, "blast.mic", doc.Project).Number, 9);
        Assert.NotEqual(mic, CalcService.Value(doc.Project, "blast.mic", doc.Project).Number);
    }

    [Fact]
    public void Format_UsesFormatAndUnit()
    {
        var (doc, _) = Open();
        var h = Loaded(doc.Project);
        var text = CalcService.Format(doc.Project, "hole.volume", h);
        Assert.EndsWith("m³", text);
        Assert.Equal("Объём", CalcService.Caption(doc.Project, "hole.volume"));
        Assert.Contains(CalcService.Calculated(doc.Project, "hole"), p => p.Id == "hole.chargeMass");
    }

    /// <summary>Замер: масса ВВ скважины из кэша долгого сеанса против короткого сеанса на каждый вызов и кода до движка.</summary>
    [Fact]
    public void Benchmark_ExplosiveMass_FromTheLongSession()
    {
        var p = BlastProject.CreateNew();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming);
        var booster = p.Products.First(x => x.Kind == ProductKind.Booster);
        for (int i = 0; i < 2000; i++)
        {
            var h = p.Add(new Hole { Collar = new Vec3(i % 50 * 4, i / 50 * 4, 0), Length = 12 });
            h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem.Id });
            var d = new Deck { Top = 3, Bottom = 12, ProductId = anfo.Id };
            d.Primers.Add(new Primer { Depth = 11.5, BoosterId = booster.Id });
            h.Decks.Add(d);
        }
        var holes = p.Holes.ToList();
        double Measure(Func<Hole, double> f, int rounds)
        {
            foreach (var h in holes) f(h); // прогрев и заполнение кэша
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++) foreach (var h in holes) f(h);
            return sw.Elapsed.TotalMilliseconds * 1000 / (rounds * holes.Count);
        }
        var legacy = Measure(h => Legacy.ExplosiveMass(p, h), 5);
        var shortSession = Measure(h => Charging.ExplosiveMass(p, h), 2);
        var doc = new Document(p);
        CalcService.Attach(doc);
        var live = Measure(h => Charging.ExplosiveMass(p, h), 10);
        Console.WriteLine($"ExplosiveMass µs/call: long session {live:0.00}, short session {shortSession:0.00}, before the engine {legacy:0.00}");
        Assert.True(live < shortSession, $"long {live:0.00} µs is not faster than short {shortSession:0.00} µs");
        Assert.True(live < 3, $"ExplosiveMass from the long session {live:0.00} µs/call");
        GC.KeepAlive(doc);
    }
}

/// <summary>Вычисляемые свойства в выводе: экспорт CSV, поля отчётов, подписи и раскраска плана.</summary>
public class CalcOutputTests
{
    [Fact]
    public void CsvExport_AddsChosenCalculatedColumns()
    {
        var p = CalcParityTests.Generate(6);
        p.View.CalcColumns = new() { "hole.volume", "hole.chargeMass", "hole.isLoaded" };
        var lines = BlastDesigner.IO.TextFiles.ExportHoles(p, p.Holes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",hole.volume,hole.chargeMass,hole.isLoaded", lines[0].TrimEnd('\r'));
        var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        var row = lines.First(l => l.StartsWith(h.Label + ",")).TrimEnd('\r').Split(',');
        Assert.Equal(Legacy.Quantities(p).PerHole.First(x => x.H == h).Volume, double.Parse(row[^3], System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Equal("1", row[^1]);
        p.View.CalcColumns = null;
        Assert.EndsWith("FiringTime", BlastDesigner.IO.TextFiles.ExportHoles(p, p.Holes).Split('\n')[0].TrimEnd('\r'));
    }

    [Fact]
    public void ReportFields_HoldCalculatedBlastTotals()
    {
        var p = CalcParityTests.Generate(6);
        var fields = BlastDesigner.Rendering.ReportLayout.Fields(p, "t", DateTime.Now);
        Assert.Equal(CalcService.Format(p, "blast.powderFactor", p), fields["{calc:blast.powderFactor}"]);
        Assert.Contains("kg/m³", fields["{calc:blast.powderFactor}"]);
        Assert.Equal(p.Header.Name, fields["{blast}"]);
        Assert.Contains("0", BlastDesigner.Rendering.ReportLayout.Fill("PF {calc:blast.powderFactor}", fields));
    }

    [Fact]
    public void Plan_DrawsCalculatedLabelsAndColours()
    {
        var p = CalcParityTests.Generate(6);
        var cam = new BlastDesigner.Rendering.Camera { Width = 800, Height = 600 };
        var bounds = BlastDesigner.Domain.Geometry.Bounds3.Empty;
        foreach (var h in p.Holes) bounds.Add(h.Collar);
        cam.Fit(bounds, 0.2);
        byte[] Draw()
        {
            using var bitmap = new SkiaSharp.SKBitmap(800, 600);
            using var canvas = new SkiaSharp.SKCanvas(bitmap);
            BlastDesigner.Rendering.PlanRenderer.Render(canvas, cam, p, new BlastDesigner.Rendering.SceneOptions());
            return bitmap.Bytes;
        }
        var plain = Draw();
        p.View.CalcLabels = new() { "hole.volume" };
        var labelled = Draw();
        Assert.False(plain.AsSpan().SequenceEqual(labelled));
        p.View.ColorBy = "hole.chargeMass";
        p.Summary.CalcTotals = new() { "blast.powderFactor" };
        var coloured = Draw();
        Assert.False(labelled.AsSpan().SequenceEqual(coloured));
    }
}
