using BlastDesigner.Calc;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Tests;

public class CalcParserTests
{
    private static double Eval(string text)
    {
        var model = new CalcModel(CalcRegistry.Default, new CalcProfile { Name = "t" });
        var p = BlastProject.CreateNew();
        var s = new CalcSession(model, p);
        var v = s.Evaluate(model.CompileExpression("blast", text), p);
        Assert.False(v.IsError, v.Error);
        return v.Number;
    }

    [Theory]
    [InlineData("1 + 2 * 3 ^ 2", 19)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("2 ^ -1", 0.5)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 % 4", 2)]
    [InlineData("1e-3 * 2000", 2)]
    [InlineData("if(1 < 2 and not (3 = 4), 5, 6)", 5)]
    [InlineData("case(1 > 2, 1, 2 > 3, 2, 3)", 3)]
    [InlineData("min(3, 1, 2) + max(1, 4)", 5)]
    [InlineData("clamp(7, 0, 5)", 5)]
    [InlineData("round(2.5) + floor(1.7) + ceil(1.2)", 6)]
    [InlineData("cos(60) + sin(30)", 1)]
    [InlineData("sqrt(16) + cbrt(27) + abs(-1)", 8)]
    [InlineData("log10(1000) + ln(exp(2))", 5)]
    [InlineData("coalesce(0/0, 3)", 3)]
    [InlineData("pi", Math.PI)]
    [InlineData("1 // комментарий", 1)]
    public void Arithmetic(string text, double expected) => Assert.Equal(expected, Eval(text), 12);

    [Theory]
    [InlineData("1 +", "оборвалась")]
    [InlineData("(1 + 2", "Ожидается «)»")]
    [InlineData("'abc", "не закрыта")]
    [InlineData("1 $ 2", "Неожиданный символ")]
    [InlineData("foo(1)", "Неизвестная функция")]
    [InlineData("noSuchField", "Нет свойства")]
    [InlineData("if(1, 2, 3)", "условие")]
    [InlineData("holes where isDummy", "where")]
    public void SyntaxErrors(string text, string message)
    {
        var model = new CalcModel(CalcRegistry.Default, new CalcProfile { Name = "t" });
        var e = Assert.Throws<CalcSyntaxException>(() => model.CompileExpression("blast", text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void SyntaxError_HasPosition()
    {
        var e = Assert.Throws<CalcSyntaxException>(() => Parser.Parse("length * (2 + "));
        Assert.Equal(14, e.Position);
        var u = Assert.Throws<CalcSyntaxException>(() => Lexer.Tokenize("1 + 'x"));
        Assert.Equal(4, u.Position);
    }

    [Fact]
    public void Where_ParsesInsideAggregate()
    {
        var ast = Assert.IsType<CallAst>(Parser.Parse("sum(decks where product.isExplosive and top > 1, mass)"));
        var w = Assert.IsType<WhereAst>(ast.Args[0]);
        Assert.Equal("decks", ((NameAst)w.Source).Text);
        Assert.IsType<BinaryAst>(w.Filter);
    }
}

public class CalcUnitTests
{
    private static readonly CalcModel Model = new(CalcRegistry.Default, new CalcProfile { Name = "t" });

    [Fact]
    public void Parse_CompositeUnits()
    {
        var d = Unit.Parse("g/cm3");
        Assert.Equal(1000, d.Scale, 9);
        Assert.True(d.SameDimension(Unit.Parse("kg/m3")));
        Assert.False(d.SameDimension(Unit.Parse("kg/m")));
        Assert.Equal("m^-3·kg", d.DimensionText);
        Assert.True(Unit.Parse("MJ/t").SameDimension(Unit.Parse("MJ/kg")));
        Assert.Equal(1e-3, Unit.Parse("MJ/t").FactorTo(Unit.Parse("MJ/kg")), 12);
        Assert.Equal(1, Unit.Parse("m^2").Divide(Unit.Parse("m2")).Scale, 12);
        Assert.Throws<FormatException>(() => Unit.Parse("parsec"));
    }

    [Fact]
    public void Addition_RequiresSameDimensionAndScale()
    {
        Assert.Contains("Несовместимые", Assert.Throws<CalcSyntaxException>(() => Model.CompileExpression("deck", "length + product.density")).Message);
        // метры и миллиметры — одна размерность, но без явного приведения складывать нельзя
        Assert.Contains("to(", Assert.Throws<CalcSyntaxException>(() => Model.CompileExpression("hole", "length + diameter")).Message);
        var ok = Model.CompileExpression("hole", "length + to(m, diameter)");
        Assert.Equal("m", ok.Unit.Symbol);
        Assert.Contains("размерность", Assert.Throws<CalcSyntaxException>(() => Model.CompileExpression("hole", "to(kg, length)")).Message);
    }

    [Fact]
    public void Constants_AdaptToOperand()
    {
        Assert.Equal("m", Model.CompileExpression("hole", "length - 0.5").Unit.Symbol);
        Assert.True(Model.CompileExpression("hole", "length > 3").Type == CalcType.Bool);
        Assert.Equal("m2", Model.CompileExpression("hole", "length * subdrill").Unit.Symbol);
        Assert.Equal("m3", Model.CompileExpression("hole", "pi * (to(m, diameter) / 2) ^ 2 * length").Unit.Symbol);
        // нецелая степень размерной величины — эмпирика, единица неизвестна
        Assert.True(Model.CompileExpression("hole", "length ^ 0.8").Unit.IsUnknown);
        Assert.Contains("градусах", Assert.Throws<CalcSyntaxException>(() => Model.CompileExpression("hole", "cos(length)")).Message);
    }

    [Fact]
    public void FormulaUnit_CheckedAgainstProperty()
    {
        var profile = new CalcProfile
        {
            Name = "t",
            Properties = { new PropertyDef { Id = "hole.bad", Unit = "kg" }, new PropertyDef { Id = "hole.good", Unit = "m3" } },
            Formulas = { new FormulaDef { Property = "hole.bad", Expression = "length * 2" }, new FormulaDef { Property = "hole.good", Expression = "length ^ 3" } }
        };
        var m = new CalcModel(CalcRegistry.Default, profile);
        Assert.Contains(m.Diagnostics, d => d.Property == "hole.bad" && d.Message.Contains("kg") && !d.IsWarning);
        Assert.DoesNotContain(m.Diagnostics, d => d.Property == "hole.good");
    }

    [Fact]
    public void To_ConvertsByScale()
    {
        var p = BlastProject.CreateNew();
        var h = p.Add(new Hole { Diameter = 115 });
        var s = new CalcSession(Model, p);
        Assert.Equal(0.115, s.Evaluate(Model.CompileExpression("hole", "to(m, diameter)"), h).Number, 15);
        Assert.Equal(11.5, s.Evaluate(Model.CompileExpression("hole", "to(cm, diameter)"), h).Number, 12);
        Assert.Equal(115 / 1000.0, s.Evaluate(Model.CompileExpression("hole", "to(m, diameter)"), h).Number);
    }
}

public class CalcEngineCoreTests
{
    internal static BlastProject Project(int holes = 3)
    {
        var p = BlastProject.CreateNew();
        var anfo = p.Products.First(x => x.Name == "ANFO");
        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        var stem = p.Products.First(x => x.Kind == ProductKind.Stemming);
        var booster = p.Products.First(x => x.Kind == ProductKind.Booster);
        for (int i = 0; i < holes; i++)
        {
            var h = p.Add(new Hole { Label = $"{i + 1}", Collar = new Vec3(i * 4, 0, 100), Length = 10 + i, Diameter = 102 });
            h.Decks.Add(new Deck { Top = 0, Bottom = 3, ProductId = stem.Id });
            var d = new Deck { Top = 3, Bottom = h.Length, ProductId = i == 0 ? emulsion.Id : anfo.Id };
            d.Primers.Add(new Primer { Depth = h.Length - 0.5, BoosterId = booster.Id });
            h.Decks.Add(d);
        }
        return p;
    }

    private static CalcProfile Simple() => new()
    {
        Name = "simple",
        Properties =
        {
            new PropertyDef { Id = "deck.mass", Unit = "kg" },
            new PropertyDef { Id = "hole.chargeMass", Unit = "kg" },
            new PropertyDef { Id = "blast.totalCharge", Unit = "kg" },
        },
        Formulas =
        {
            new FormulaDef { Property = "deck.mass", Expression = "pi * (to(m, hole.diameter) / 2) ^ 2 * length * product.density * 1000", OnError = 0 },
            new FormulaDef { Property = "hole.chargeMass", Expression = "sum(decks where product.isExplosive, mass) + sum(primers, coalesce(booster.unitMass, 0))" },
            new FormulaDef { Property = "blast.totalCharge", Expression = "sum(holes where not isDummy, chargeMass)" },
        }
    };

    [Fact]
    public void Evaluates_FormulasThroughRelations()
    {
        var p = Project();
        var model = new CalcModel(CalcRegistry.Default, Simple());
        Assert.False(model.HasErrors, string.Join("; ", model.Diagnostics));
        var s = new CalcSession(model, p);
        var h = p.Holes.First();
        var expl = h.Decks[1];
        var expected = Math.PI * 0.051 * 0.051 * 7 * 1.15 * 1000 + 0.4;
        Assert.Equal(expected, s.Number("hole.chargeMass", h), 9);
        Assert.Equal(p.Holes.Sum(x => s.Number("hole.chargeMass", x)), s.Number("blast.totalCharge", p), 9);
        Assert.Equal(Math.PI * 0.051 * 0.051 * 7 * 1.15 * 1000, s.Number("deck.mass", expl), 9);
    }

    [Fact]
    public void MissingProduct_UsesOnErrorValueOrReportsError()
    {
        var p = Project(1);
        var h = p.Holes.First();
        h.Decks[1].ProductId = "missing";
        var model = new CalcModel(CalcRegistry.Default, Simple());
        var s = new CalcSession(model, p);
        Assert.Equal(0, s.Number("deck.mass", h.Decks[1]));
        // без продукта интервал не ВВ: остаётся масса боевика
        Assert.Equal(0.4, s.Number("hole.chargeMass", h), 12);

        var profile = Simple();
        profile.Formulas[0].OnError = null;
        s = new CalcSession(new CalcModel(CalcRegistry.Default, profile), p);
        Assert.True(s.Value("deck.mass", h.Decks[1]).IsError);
    }

    [Fact]
    public void Errors_PropagateToDependents_WithoutStoppingOthers()
    {
        var profile = new CalcProfile
        {
            Name = "e",
            Formulas =
            {
                new FormulaDef { Property = "hole.broken", Expression = "if(holeTypeIndex = 2, 0 / 0, 1)" },
                new FormulaDef { Property = "hole.uses", Expression = "broken * 2" },
                new FormulaDef { Property = "blast.total", Expression = "sum(holes, uses)" },
            }
        };
        var p = Project();
        p.Holes.ElementAt(1).HoleType = 2;
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, profile), p);
        Assert.Equal(2, s.Number("hole.uses", p.Holes.First()));
        var bad = s.Value("hole.uses", p.Holes.ElementAt(1));
        Assert.True(bad.IsError);
        Assert.Contains("hole.broken", bad.Error);
        Assert.True(s.Value("blast.total", p).IsError);
        Assert.Contains(s.Issues(), i => i.Property == "hole.broken" && i.Entity == p.Holes.ElementAt(1));
    }

    [Fact]
    public void Cycle_IsReportedWithChain()
    {
        var profile = new CalcProfile
        {
            Name = "c",
            Formulas =
            {
                new FormulaDef { Property = "hole.a", Expression = "b + 1" },
                new FormulaDef { Property = "hole.b", Expression = "c * 2" },
                new FormulaDef { Property = "hole.c", Expression = "a - 1" },
                new FormulaDef { Property = "hole.ok", Expression = "length" },
            }
        };
        var m = new CalcModel(CalcRegistry.Default, profile);
        var d = Assert.Single(m.Diagnostics, x => x.Message.Contains("Цикл"));
        Assert.Contains("hole.a → hole.c → hole.b → hole.a", d.Message);
        var s = new CalcSession(m, Project(1));
        Assert.Contains("Цикл", s.Value("hole.a", s.Project.Holes.First()).Error);
        Assert.Equal(10, s.Number("hole.ok", s.Project.Holes.First()));
    }

    [Fact]
    public void Cycle_ThroughAggregateAndRelation()
    {
        var profile = new CalcProfile
        {
            Name = "c",
            Formulas =
            {
                new FormulaDef { Property = "deck.share", Expression = "length / hole.total" },
                new FormulaDef { Property = "hole.total", Expression = "sum(decks, share)" },
            }
        };
        var m = new CalcModel(CalcRegistry.Default, profile);
        Assert.Contains(m.Diagnostics, x => x.Message.Contains("deck.share") && x.Message.Contains("hole.total") && x.Message.Contains("Цикл"));
    }

    [Fact]
    public void TopologicalOrder_FollowsDependencies()
    {
        var m = new CalcModel(CalcRegistry.Default, Simple());
        var order = m.Order.Select(n => n.Id).ToList();
        Assert.True(order.IndexOf("deck.mass") < order.IndexOf("hole.chargeMass"));
        Assert.True(order.IndexOf("hole.chargeMass") < order.IndexOf("blast.totalCharge"));
        Assert.Equal(new[] { "hole.chargeMass", "blast.totalCharge" }, m.Downstream("deck.mass").Select(n => n.Id));
    }

    [Fact]
    public void Invalidate_ProductDensity_RecalculatesOnlyItsDecksAndHoles()
    {
        var p = Project();
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, Simple()), p);
        var holes = p.Holes.ToList();
        foreach (var h in holes) s.Number("hole.chargeMass", h);
        s.Number("blast.totalCharge", p);
        var before = s.Evaluations;

        var emulsion = p.Products.First(x => x.Name == "Bulk emulsion");
        emulsion.Density = 1.25;
        s.Invalidate(emulsion, nameof(Product.Density));
        Assert.False(s.IsCached("hole.chargeMass", holes[0]));
        Assert.False(s.IsCached("deck.mass", holes[0].Decks[1]));
        Assert.False(s.IsCached("blast.totalCharge", p));
        Assert.True(s.IsCached("hole.chargeMass", holes[1]));
        Assert.True(s.IsCached("deck.mass", holes[2].Decks[1]));

        var total = s.Number("blast.totalCharge", p);
        // заново: масса интервала, скважина, итог
        Assert.Equal(3, s.Evaluations - before);
        Assert.Equal(Math.PI * 0.051 * 0.051 * 7 * 1.25 * 1000 + 0.4, s.Number("hole.chargeMass", holes[0]), 9);
        Assert.Equal(holes.Sum(h => s.Number("hole.chargeMass", h)), total, 9);
    }

    [Fact]
    public void Invalidate_HoleDiameter_TouchesOwnDecksOnly()
    {
        var p = Project();
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, Simple()), p);
        var holes = p.Holes.ToList();
        s.Number("blast.totalCharge", p);
        holes[1].Diameter = 115;
        s.Invalidate(holes[1], nameof(Hole.Diameter));
        Assert.False(s.IsCached("deck.mass", holes[1].Decks[1]));
        Assert.True(s.IsCached("deck.mass", holes[0].Decks[1]));
        Assert.False(s.IsCached("blast.totalCharge", p));
    }

    [Fact]
    public void Sync_DetectsChangedFieldsAndStructure()
    {
        var p = Project();
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, Simple()), p);
        Assert.Equal(-1, s.Sync());
        var holes = p.Holes.ToList();
        var first = s.Number("blast.totalCharge", p);
        Assert.Equal(0, s.Sync());
        Assert.True(s.IsCached("blast.totalCharge", p));

        holes[2].Decks[1].Bottom -= 1;
        Assert.True(s.Sync() > 0);
        Assert.True(s.IsCached("hole.chargeMass", holes[0]));
        Assert.False(s.IsCached("hole.chargeMass", holes[2]));
        Assert.True(s.Number("blast.totalCharge", p) < first);

        // новый интервал — меняется состав объектов, кэш сбрасывается
        holes[0].Decks.Add(new Deck { Top = holes[0].Length, Bottom = holes[0].Length + 1, ProductId = p.Products.First(x => x.Name == "ANFO").Id });
        Assert.Equal(-1, s.Sync());
        Assert.False(s.IsCached("blast.totalCharge", p));
    }

    [Fact]
    public void Parameters_ResolveFromHoleUpToProfile()
    {
        var profile = new CalcProfile
        {
            Name = "p",
            Parameters = { new ParameterDef { Id = "site.k", Unit = "1", Value = 1 }, new ParameterDef { Id = "site.t", Table = new() { new(0, 0), new(10, 100) } } },
            Formulas = { new FormulaDef { Property = "hole.k", Expression = "param(site.k)" }, new FormulaDef { Property = "hole.t", Expression = "lookup(site.t, length)" } }
        };
        var p = Project(3);
        var holes = p.Holes.ToList();
        holes[1].HoleType = 2;
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, profile), p);
        s.Scope.SiteParameters["site.k"] = 2;
        s.BlastParameters["site.k"] = 3;
        s.HoleTypeParameters[2] = new() { ["site.k"] = 4 };
        s.HoleParameters[holes[2].Id] = new() { ["site.k"] = 5 };
        Assert.Equal(3, s.Number("hole.k", holes[0]));
        Assert.Equal(4, s.Number("hole.k", holes[1]));
        Assert.Equal(5, s.Number("hole.k", holes[2]));
        s.BlastParameters.Clear();
        s.Reset();
        Assert.Equal(2, s.Number("hole.k", holes[0]));
        s.Scope.SiteParameters.Clear();
        s.Reset();
        Assert.Equal(1, s.Number("hole.k", holes[0]));
        Assert.Equal(100, s.Number("hole.t", holes[0]));
        holes[0].Length = 5;
        s.Invalidate(holes[0], "Length");
        Assert.Equal(50, s.Number("hole.t", holes[0]), 12);
    }

    [Fact]
    public void Overrides_ReplaceFormulaForOneEntity()
    {
        var p = Project();
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, Simple()), p);
        var h = p.Holes.First();
        s.SetOverrides(new[] { new ValueOverride { Entity = h.Id.ToString(), Property = "hole.chargeMass", Value = 42 } });
        Assert.Equal(42, s.Number("hole.chargeMass", h));
        Assert.NotEqual(42, s.Number("hole.chargeMass", p.Holes.Last()));
    }

    [Fact]
    public void Variants_PickFirstMatchingCondition()
    {
        var profile = new CalcProfile
        {
            Name = "v",
            Formulas =
            {
                new FormulaDef { Property = "hole.kind", Expression = "1", Condition = "length < 11", Priority = 0 },
                new FormulaDef { Property = "hole.kind", Expression = "2", Priority = 1 },
            }
        };
        var p = Project(3);
        var s = new CalcSession(new CalcModel(CalcRegistry.Default, profile), p);
        Assert.Equal(new double[] { 1, 2, 2 }, p.Holes.Select(h => s.Number("hole.kind", h)));
    }

    [Fact]
    public void Profile_JsonRoundTripAndInheritance()
    {
        var baseProfile = Simple();
        baseProfile.ReadOnly = true;
        baseProfile.Parameters.Add(new ParameterDef { Id = "site.x", Value = 2, Table = null });
        var json = baseProfile.ToJson();
        var back = CalcProfile.FromJson(json);
        Assert.Equal(baseProfile.Formulas.Count, back.Formulas.Count);
        Assert.Equal(baseProfile.Hash(), back.Hash());

        var child = CalcProfile.DeriveFrom(back, "mine");
        child.Formulas.Add(new FormulaDef { Property = "blast.totalCharge", Expression = "2 * sum(holes where not isDummy, chargeMass)" });
        child.Parameters.Add(new ParameterDef { Id = "site.x", Value = 3 });
        var lib = new CalcProfileLibrary();
        lib.Add(back);
        var flat = child.Flatten(lib.Find);
        Assert.Equal(3, flat.Formulas.Count);
        Assert.StartsWith("2 *", flat.Formulas.Single(f => f.Property == "blast.totalCharge").Expression);
        Assert.Equal(3, flat.Parameters.Single().Value);
        Assert.Equal(3, flat.Properties.Count);

        var p = Project();
        var s1 = new CalcSession(new CalcModel(CalcRegistry.Default, back), p);
        var s2 = new CalcSession(new CalcModel(CalcRegistry.Default, flat), p);
        Assert.Equal(2 * s1.Number("blast.totalCharge", p), s2.Number("blast.totalCharge", p), 9);

        child.Base = "mine";
        lib.Add(child);
        Assert.Throws<InvalidDataException>(() => child.Flatten(lib.Find));
    }

    [Fact]
    public void ModelFields_RegisteredAsInputs()
    {
        var r = CalcRegistry.Default;
        Assert.Equal(PropertyKind.Input, r.Property("hole.length")!.Kind);
        Assert.Equal("mm", r.Property("hole.diameter")!.Unit);
        Assert.Equal(CalcType.Enum, r.Property("product.kind")!.Type);
        Assert.Equal(CalcType.Ref, r.Property("deck.product")!.Type);
        Assert.Equal("product.decks", r.Property("deck.product")!.Inverse);
        Assert.Equal(CalcType.List, r.Property("hole.decks")!.Type);
        Assert.NotNull(r.Property("hole.waterDepth"));
        Assert.NotNull(r.Property("blast.rockDensity"));
        Assert.Contains(r.ContextsOf(BlastProject.CreateNew().Design.HoleTypes[0]), c => c.Name == "holeType");
    }

    [Fact]
    public void EnumLiteral_CheckedAtCompileTime()
    {
        var m = new CalcModel(CalcRegistry.Default, new CalcProfile { Name = "t" });
        Assert.Contains("GasBag", Assert.Throws<CalcSyntaxException>(() => m.CompileExpression("product", "kind = 'Gas'")).Message);
        var p = BlastProject.CreateNew();
        var s = new CalcSession(m, p);
        var gas = p.Products.First(x => x.Kind == ProductKind.GasBag);
        Assert.True((bool)s.Evaluate(m.CompileExpression("product", "kind = 'gasbag'"), gas).Object!);
        Assert.Equal("GasBag", s.Evaluate(m.CompileExpression("product", "if(true, kind, 'x')", CalcType.String), gas).Object);
    }
}
