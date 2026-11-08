using BlastDesigner.Calc;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>Формулы для инженера: русские имена, перевод туда и обратно, понятные ошибки, автоматический перевод единиц.</summary>
public class CalcRussianTests
{
    private static CalcModel M => CalcService.BuiltInModel;

    private static double Eval(BlastProject p, string ctx, object entity, string text)
    {
        var s = CalcService.Session(p);
        var v = s.Evaluate(M.CompileExpression(ctx, text), entity);
        Assert.False(v.IsError, v.Error);
        return v.Number;
    }

    [Fact]
    public void RussianFormula_GivesTheSameAsCode()
    {
        var p = CalcParityTests.Generate(4);
        foreach (var h in p.Holes.Where(x => !x.IsDummy && x.Decks.Count > 0).Take(5))
        {
            var ru = Eval(p, "hole", h, "сумма([Интервалы] где [Продукт].[ВВ], [Масса]) + сумма([Боевики], первое_из([Промежуточный детонатор].[Масса единицы], 0))");
            Assert.Equal(Legacy.ExplosiveMass(p, h), ru, 9);
            Assert.Equal(Legacy.ExplosiveMass(p, h), Eval(p, "hole", h, "[Масса ВВ]"), 9);
            Assert.Equal(h.Diameter / 1000, Eval(p, "hole", h, "в(м, [Диаметр])"), 12);
            Assert.Equal(1, Eval(p, "hole", h, "если([Длина] > 0 и не ([Диаметр] < 0), 1, 0)"));
        }
    }

    [Fact]
    public void EveryBuiltInFormula_RoundTripsThroughRussian()
    {
        foreach (var f in CalcService.BuiltIn.Formulas)
        {
            var ctx = f.Property[..f.Property.IndexOf('.')];
            var ru = FormulaText.ToRussian(M, ctx, f.Expression);
            var back = FormulaText.ToCode(M, ctx, ru);
            Assert.Equal(FormulaText.ToCode(M, ctx, f.Expression), back);
            M.CompileExpression(ctx, ru);
            Assert.DoesNotContain("decks", ru);
        }
        var chargeMass = CalcService.BuiltIn.Formulas.Single(f => f.Property == "hole.chargeMass").Expression;
        Assert.Equal("сумма([Интервалы] где [Продукт].[ВВ], [Масса]) + сумма([Боевики], первое_из([Промежуточный детонатор].[Масса единицы], 0))",
            FormulaText.ToRussian(M, "hole", chargeMass));
    }

    [Fact]
    public void ComputedProperties_HaveUniqueRussianNames()
    {
        foreach (var n in M.Nodes)
            Assert.True(M.AliasOf(n.Property) == n.Property.NameRu, $"{n.Id}: «{n.Property.NameRu}» не однозначно");
    }

    [Fact]
    public void Errors_SuggestTyposAndRelations()
    {
        var typo = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("hole", "[Масса ВВВ] * 2"));
        Assert.Contains("возможно, вы имели в виду [Масса ВВ]", typo.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, typo.Position);
        var relation = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("deck", "[Длина интервала] * [Диаметр]"));
        Assert.Contains("[Скважина].[Диаметр]", relation.Message);
        var fn = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("hole", "сумм([Интервалы], [Масса])"));
        Assert.Contains("сумма", fn.Message);
        var units = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("deck", "[Длина интервала] + [Продукт].[Плотность]"));
        Assert.Contains("м и г/см³", units.Message);
    }

    [Fact]
    public void CompatibleUnits_ConvertedAutomatically()
    {
        var p = CalcParityTests.Generate(4);
        var h = p.Holes.First(x => !x.IsDummy);
        Assert.Equal(h.Length + h.Diameter / 1000, Eval(p, "hole", h, "[Длина] + [Диаметр]"), 12);
        Assert.Equal(h.Diameter + h.Length * 1000, Eval(p, "hole", h, "[Диаметр] + [Длина]"), 9);
        Assert.Equal(0, Eval(p, "hole", h, "если([Диаметр] > [Длина], 1, 0)"));
    }

    [Fact]
    public void Highlight_MarksPropertiesFunctionsAndNumbers()
    {
        var parts = FormulaText.Highlight("сумма([Интервалы] где [Продукт].[ВВ], [Масса]) * 2");
        Assert.Contains(parts, x => x.Text == "сумма" && x.Kind == FormulaText.Part.Function);
        Assert.Contains(parts, x => x.Text == "[Интервалы]" && x.Kind == FormulaText.Part.Property);
        Assert.Contains(parts, x => x.Text == "где" && x.Kind == FormulaText.Part.Keyword);
        Assert.Contains(parts, x => x.Text == "2" && x.Kind == FormulaText.Part.Number);
        Assert.Equal("сумма([Интервалы] где [Продукт].[ВВ], [Масса]) * 2", string.Concat(parts.Select(x => x.Text)));
    }

    [Fact]
    public void Parameters_ByRussianName()
    {
        var p = CalcParityTests.Generate(4);
        var monitor = p.Entities.OfType<PointEntity>().First();
        Assert.Equal(p.Vibration.SiteK, Eval(p, "monitor", monitor, "параметр([Сейсмика: K])"));
        Assert.Equal("параметр([Сейсмика: K])", FormulaText.ToRussian(M, "monitor", "param(site.vib.K)"));
        Assert.Equal("param(site.vib.K)", FormulaText.ToCode(M, "monitor", "параметр([Сейсмика: K])"));
    }
}

public class CalcExplainTests
{
    [Fact]
    public void Explain_ShowsFormulaWithValuesStepByStep()
    {
        var p = CalcParityTests.Generate(5);
        var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        var s = CalcService.Session(p);
        var steps = CalcExplain.Explain(s, "hole.tonnes", h);
        var first = steps[0];
        Assert.Equal("Масса породы", first.Caption);
        Assert.Equal("[Объём] * [Плотность породы]", first.Formula);
        var volume = s.Number("hole.volume", h);
        Assert.Contains(volume.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " м³", first.Substituted);
        Assert.Contains("т/м³", first.Substituted);
        Assert.EndsWith(" т", first.Result);
        Assert.Contains(steps, x => x.Property == "hole.volume" && x.Depth == 1);
        var area = steps.First(x => x.Property == "hole.area");
        Assert.Contains("{площадь_ячейки() = ", area.Substituted);
        var mass = CalcExplain.Explain(s, "hole.chargeMass", h)[0];
        Assert.StartsWith("{сумма([Интервалы] где [Продукт].[ВВ], [Масса]) = ", mass.Substituted);
    }
}

public class CalcPublishTests
{
    private static CalcProfile Doubled(string name = "Рудник")
    {
        var p = CalcProfile.DeriveFrom(CalcService.BuiltIn, name);
        p.Formulas.Add(new FormulaDef { Property = "hole.chargeMass", Expression = "2 * (sum(decks where product.isExplosive, mass) + sum(primers, coalesce(booster.unitMass, 0)))" });
        return p;
    }

    [Fact]
    public void Publish_KeepsHistoryAndRollsBack()
    {
        var project = CalcParityTests.Generate(4);
        var h = project.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        var before = Charging.ExplosiveMass(project, h);
        CalcService.Publish(project, Doubled(), "коэффициент 2", "инженер", DateTime.Now);
        Assert.Equal(2 * before, Charging.ExplosiveMass(project, h), 9);
        Assert.Equal(new[] { "Исходное состояние", "коэффициент 2" }, project.Calc!.History!.Select(v => v.Reason));

        CalcService.Publish(project, Doubled(), "ещё раз", "инженер", DateTime.Now);
        Assert.Equal("2", project.Calc.ProfileVersion);

        CalcService.Rollback(project, project.Calc.History![0], "инженер", DateTime.Now);
        Assert.Equal(before, Charging.ExplosiveMass(project, h), 9);
        Assert.Equal(CalcService.BuiltInName, project.Calc.ProfileName);
        Assert.StartsWith("Откат к версии", project.Calc.History![^1].Reason);
        Assert.Equal(4, project.Calc.History.Count);

        var back = BlastDesigner.Domain.ProjectJson.Deserialize(BlastDesigner.Domain.ProjectJson.Serialize(project));
        Assert.Equal(4, back.Calc!.History!.Count);
    }

    [Fact]
    public void PublishProblem_RefusesBuiltInNameErrorsAndFailedTests()
    {
        var project = CalcParityTests.Generate(4);
        Assert.Null(CalcService.PublishProblem(project, Doubled()));
        Assert.Contains("только для чтения", CalcService.PublishProblem(project, Doubled(CalcService.BuiltInName)));
        var broken = Doubled();
        broken.Formulas[0].Expression = "нетТакого + 1";
        Assert.Contains("Ошибка", CalcService.PublishProblem(project, broken));
        var failing = Doubled();
        failing.Tests.Add(new TestCaseDef { Name = "пример", Expression = "holeCount", Expected = -1, Tolerance = 0 });
        Assert.Contains("пример", CalcService.PublishProblem(project, failing));
    }

    [Fact]
    public void Range_FlagsValuesOutside()
    {
        var project = CalcParityTests.Generate(4);
        var h = project.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
        Assert.False(CalcService.IsOutOfRange(project, "hole.chargeMass", h));
        var p = Doubled();
        p.Properties.Add(new PropertyDef { Id = "hole.chargeMass", NameRu = "Масса ВВ", Unit = "kg", Min = 0, Max = 0.001 });
        CalcService.SetProfile(project, p);
        Assert.True(CalcService.IsOutOfRange(project, "hole.chargeMass", h));
    }
}
