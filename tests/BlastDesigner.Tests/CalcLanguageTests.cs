using BlastDesigner.Calc;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.Tests;

/// <summary>Формулы и названия расчёта на казахском и французском: перевод туда и обратно, псевдонимы, единицы, цепочка названий.</summary>
public class CalcLanguageTests
{
    private static CalcModel M => CalcService.BuiltInModel;

    [Fact]
    public void Aliases_AreNotAmbiguousBetweenLanguages()
    {
        Assert.Empty(Aliases.Ambiguities());
        Assert.Equal("sum", Aliases.Function("қосынды"));
        Assert.Equal("sum", Aliases.Function("somme"));
        Assert.Equal("to", Aliases.Function("қа"));
        Assert.Equal("lookup", Aliases.Function("table"));
        Assert.Equal("where", Aliases.Word("мұнда"));
        Assert.Equal("where", Aliases.Word("où"));
        Assert.Equal("and", Aliases.Word("және"));
        Assert.Equal("not", Aliases.Word("non"));
    }

    [Theory]
    [InlineData("kk")]
    [InlineData("fr")]
    public void EveryBuiltInFormula_RoundTripsThroughTheLanguage(string language)
    {
        foreach (var f in CalcService.BuiltIn.Formulas)
        {
            var ctx = f.Property[..f.Property.IndexOf('.')];
            var text = FormulaText.ToLanguage(M, ctx, f.Expression, language);
            var back = FormulaText.ToCode(M, ctx, text);
            Assert.Equal(FormulaText.ToCode(M, ctx, f.Expression), back);
            M.CompileExpression(ctx, text);
            Assert.DoesNotContain("decks", text);
            if (f.Condition is { Length: > 0 } c)
                Assert.Equal(FormulaText.ToCode(M, ctx, c), FormulaText.ToCode(M, ctx, FormulaText.ToLanguage(M, ctx, c, language)));
        }
    }

    [Fact]
    public void ChargeMass_ReadsInKazakhAndFrench()
    {
        var chargeMass = CalcService.BuiltIn.Formulas.Single(f => f.Property == "hole.chargeMass").Expression;
        Assert.Equal("қосынды([Аралықтар] мұнда [Өнім].[ЖЗ], [Масса]) + қосынды([Оталдырғыш-патрондар], біріншісі([Аралық дүмпіткіш].[Бірлік массасы], 0))",
            FormulaText.ToLanguage(M, "hole", chargeMass, "kk"));
        Assert.Equal("somme([Charges] où [Produit].[Explosif], [Masse]) + somme([Amorces], premier_de([Booster].[Masse unitaire], 0))",
            FormulaText.ToLanguage(M, "hole", chargeMass, "fr"));
        Assert.Equal("en(m, [Diamètre])", FormulaText.ToLanguage(M, "hole", "to(m, diameter)", "fr"));
        Assert.Equal("қа(м, [Диаметр])", FormulaText.ToLanguage(M, "hole", "to(m, diameter)", "kk"));
        Assert.Equal("to(m, diameter)", FormulaText.ToCode(M, "hole", "қа(м, [Диаметр])"));
    }

    [Theory]
    [InlineData("kk")]
    [InlineData("fr")]
    public void ComputedProperties_HaveUniqueNames(string language)
    {
        foreach (var n in M.Nodes)
            Assert.True(M.AliasOf(n.Property, language) == n.Property.NameIn(language), $"{n.Id}: «{n.Property.NameIn(language)}» не однозначно ({language})");
    }

    [Fact]
    public void OtherLanguages_DoNotTakeRussianNamesAway()
    {
        foreach (var ctx in M.Contexts)
        {
            var props = M.PropertiesOf(ctx.Name).GroupBy(p => p.Id).Select(g => g.Last()).Where(p => p.NameRu.Length > 0).ToList();
            foreach (var g in props.GroupBy(p => p.NameRu.Trim().ToLowerInvariant().Replace('ё', 'е')).Where(g => g.Count() == 1))
                Assert.True(M.AliasOf(g.Single(), "ru") == g.Single().NameRu, $"{g.Single().Id}: русское имя «{g.Single().NameRu}» перестало быть однозначным");
        }
    }

    [Fact]
    public void Parameters_ByKazakhAndFrenchName()
    {
        Assert.Equal("параметр([Діріл: K])", FormulaText.ToLanguage(M, "monitor", "param(site.vib.K)", "kk"));
        Assert.Equal("paramètre([Vibrations : K])", FormulaText.ToLanguage(M, "monitor", "param(site.vib.K)", "fr"));
        Assert.Equal("param(site.vib.K)", FormulaText.ToCode(M, "monitor", "параметр([Діріл: K])"));
        Assert.Equal("param(site.vib.K)", FormulaText.ToCode(M, "monitor", "paramètre([Vibrations : K])"));
    }

    [Fact]
    public void Units_InTheLanguageOfFormulas()
    {
        Assert.Equal("кг/м³", Aliases.Unit("kg/m3", "kk"));
        Assert.Equal("кг/м³", Aliases.Unit("kg/m3", "ru"));
        Assert.Equal("kg/m³", Aliases.Unit("kg/m3", "fr"));
        Assert.Equal("kg/m3", Aliases.Unit("kg/m3", "en"));
    }

    [Fact]
    public void Names_FallBackAlongTheChain()
    {
        var own = new PropertyDef { Id = "hole.myOwn", NameRu = "Моё", NameEn = "Mine" };
        Assert.Equal("Моё", own.Caption("kk"));
        Assert.Equal("Mine", own.Caption("fr"));
        own.NameKk = "Менікі";
        Assert.Equal("Менікі", own.Caption("kk"));
        var code = new PropertyDef { Id = "hole.codeOnly" };
        Assert.Equal("codeOnly", code.Caption("fr"));
        Assert.Equal("Ұңғыма", M.Registry.Context("hole")!.Caption("kk"));
        Assert.Equal("Trou", M.Registry.Context("hole")!.Caption("fr"));
        Assert.Equal("Оқтау", CalcText.Group("Заряжание", "kk"));
        Assert.Equal("Chargement", CalcText.Group("Заряжание", "fr"));
        Assert.Equal("Loading", CalcText.Group("Заряжание", "en"));
    }

    [Fact]
    public void Profile_KeepsNamesOfOtherLanguagesOptional()
    {
        var json = new CalcProfile { Name = "x", Properties = { new PropertyDef { Id = "hole.a", NameRu = "А" } } }.ToJson();
        Assert.DoesNotContain("nameKk", json);
        Assert.DoesNotContain("nameFr", json);
        var back = CalcProfile.FromJson(json.Replace("\"nameRu\": \"А\"", "\"nameRu\": \"А\", \"nameKk\": \"Ә\", \"nameFr\": \"É\""));
        Assert.Equal("Ә", back.Properties[0].NameKk);
        Assert.Equal("É", back.Properties[0].NameFr);
    }
}

[CollectionDefinition(nameof(FormulaLanguageCollection), DisableParallelization = true)]
public class FormulaLanguageCollection;

/// <summary>Тесты, которые меняют язык формул программы: идут отдельно от остальных.</summary>
[Collection(nameof(FormulaLanguageCollection))]
public class CalcFormulaLanguageSwitchTests
{
    private static CalcModel M => CalcService.BuiltInModel;

    [Fact]
    public void RussianFormula_IsUnderstoodWhenFormulasAreFrench()
    {
        var before = CalcService.FormulaLanguage;
        try
        {
            CalcService.FormulaLanguage = "fr";
            var p = CalcParityTests.Generate(4);
            var h = p.Holes.First(x => !x.IsDummy && x.Decks.Count > 0);
            var ru = "сумма([Интервалы] где [Продукт].[ВВ], [Масса]) + сумма([Боевики], первое_из([Промежуточный детонатор].[Масса единицы], 0))";
            var s = CalcService.Session(p);
            var v = s.Evaluate(M.CompileExpression("hole", ru), h);
            Assert.False(v.IsError, v.Error);
            Assert.Equal(Legacy.ExplosiveMass(p, h), v.Number, 9);
            Assert.Equal("sum(decks where product.isExplosive, mass) + sum(primers, coalesce(booster.unitMass, 0))", FormulaText.ToCode(M, "hole", ru));
            Assert.Equal("Masse d'explosif", CalcService.Caption(p, "hole.chargeMass"));
            Assert.Equal("[Masse d'explosif]", "[" + M.AliasOf(M.FindProperty("hole.chargeMass")!) + "]");
            CalcService.FormulaLanguage = "kk";
            Assert.Equal("ЖЗ массасы", CalcService.Caption(p, "hole.chargeMass"));
            CalcService.FormulaLanguage = "en";
            Assert.Equal("Explosive mass", CalcService.Caption(p, "hole.chargeMass"));
        }
        finally { CalcService.FormulaLanguage = before; }
    }

    [Theory]
    [InlineData("en", "Unknown function «сумм»", "Did you mean")]
    [InlineData("kk", "Белгісіз функция «сумм»", "Мүмкін")]
    [InlineData("fr", "Fonction inconnue « сумм »", "Vouliez-vous dire")]
    public void CompilerMessages_InTheInterfaceLanguage(string language, string start, string hint)
    {
        var before = CalcService.MessageLanguage;
        try
        {
            CalcService.MessageLanguage = language;
            var fn = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("hole", "сумм([Интервалы], [Масса])"));
            Assert.StartsWith(start.Replace("«сумм»", "\"сумм\""), fn.Message.Replace("«сумм»", "\"сумм\""));
            Assert.Contains(hint, fn.Message);
            var units = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("deck", "[Длина интервала] + [Продукт].[Плотность]"));
            Assert.DoesNotContain("Несовместимые", units.Message);
            var syntax = Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("hole", "1 +"));
            Assert.DoesNotContain("Формула оборвалась", syntax.Message);
        }
        finally { CalcService.MessageLanguage = before; }
        Assert.StartsWith("Неизвестная функция", Assert.Throws<CalcSyntaxException>(() => M.CompileExpression("hole", "сумм([Интервалы], [Масса])")).Message);
    }
}

/// <summary>Every message of the formula language written in code has its line in calc-messages.tsv, with the same values.</summary>
public class CalcMessageTableTests
{
    [Fact]
    public void EveryMessageIsInTheTableWithTheSamePlaceholders()
    {
        string? root = null;
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Directory.Build.props"))) { root = dir; break; }
        Assert.NotNull(root);
        var table = File.ReadAllLines(Path.Combine(root!, "src", "BlastDesigner.Calc", "calc-messages.tsv"))
            .Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split('\t')).ToDictionary(c => c[0], c => c);
        var used = new[] { "BlastDesigner.Calc", "BlastDesigner.Engine" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root!, "src", p), "*.cs", SearchOption.AllDirectories))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"CalcText\.M\(""([^""]*)""").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(used.Count > 60, "messages found: " + used.Count);
        var missing = used.Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "not in calc-messages.tsv: " + string.Join(" | ", missing));
        static string[] Holes(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().Order().ToArray();
        foreach (var (key, c) in table)
        {
            Assert.True(c.Length == 4 && c.Skip(1).All(x => x.Trim().Length > 0), "incomplete line: " + key);
            foreach (var tr in c.Skip(1)) Assert.True(Holes(key).SequenceEqual(Holes(tr)), $"placeholders differ: {key} → {tr}");
        }
    }
}
