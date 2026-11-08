using System.Reflection;
using System.Text.Json;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Calc;

/// <summary>
/// Регистрация контекстов и полей модели: простые свойства классов (числа, логические, строки, перечисления) —
/// отражением с именем в camelCase, единицы и русские имена основных полей — явной таблицей, связи — вручную.
/// </summary>
public static class ModelContexts
{
    /// <summary>Единицы и имена известных полей: id → (единица, имя RU).</summary>
    private static readonly Dictionary<string, (string Unit, string Ru)> Known = new()
    {
        ["hole.length"] = ("m", "Длина"), ["hole.diameter"] = ("mm", "Диаметр"), ["hole.subdrill"] = ("m", "Перебур"),
        ["hole.angle"] = ("deg", "Угол от вертикали"), ["hole.bearing"] = ("deg", "Азимут"), ["hole.actualLength"] = ("m", "Фактическая длина"),
        ["hole.effectiveLength"] = ("m", "Длина (факт или проект)"), ["hole.waterDepth"] = ("m", "Столб воды"), ["hole.temperature"] = ("?", "Температура"),
        ["hole.loadingFactor"] = ("1", "Коэффициент заряжания"), ["hole.radiusM"] = ("m", "Радиус"), ["hole.electronicTimeMs"] = ("ms", "Время ЭСИ"),
        ["hole.firingTimeMs"] = ("ms", "Время срабатывания"), ["hole.surfaceTimeMs"] = ("ms", "Время на поверхности"), ["hole.inholeDelayMs"] = ("ms", "Замедление в скважине"),
        ["hole.x"] = ("m", "X устья"), ["hole.y"] = ("m", "Y устья"), ["hole.z"] = ("m", "Z устья"),
        ["hole.toeX"] = ("m", "X забоя"), ["hole.toeY"] = ("m", "Y забоя"), ["hole.toeZ"] = ("m", "Z забоя"),
        ["hole.holeTypeIndex"] = ("1", "Номер типа скважины"), ["hole.materialIndex"] = ("1", "Номер материала"), ["hole.isDummy"] = ("1", "Фиктивная"),
        ["deck.top"] = ("m", "Кровля"), ["deck.bottom"] = ("m", "Подошва"), ["deck.length"] = ("m", "Длина интервала"),
        ["primer.depth"] = ("m", "Глубина"), ["primer.delayMs"] = ("ms", "Замедление"),
        ["product.density"] = ("g/cm3", "Плотность"), ["product.relativeWeightStrength"] = ("%", "Относительная сила"), ["product.energy"] = ("MJ/kg", "Энергия"),
        ["product.vod"] = ("m/s", "Скорость детонации"), ["product.delayMs"] = ("ms", "Замедление"), ["product.unitMass"] = ("kg", "Масса единицы"),
        ["product.unitLength"] = ("m", "Длина патрона"), ["product.cost"] = ("?", "Цена"), ["product.kind"] = ("1", "Вид"),
        ["product.isExplosive"] = ("1", "ВВ"), ["product.name"] = ("1", "Название"),
        ["row.burden"] = ("m", "Л.С.П. ряда"), ["row.spacing"] = ("m", "Расстояние в ряду"),
        ["holeType.diameter"] = ("mm", "Диаметр"), ["holeType.density"] = ("t/m3", "Плотность"),
        ["material.diameter"] = ("mm", "Диаметр"), ["material.density"] = ("t/m3", "Плотность породы"),
        ["tie.delayMs"] = ("ms", "Замедление"),
        ["blast.burden"] = ("m", "Л.С.П."), ["blast.spacing"] = ("m", "Расстояние"), ["blast.benchHeight"] = ("m", "Высота уступа"),
        ["blast.subdrill"] = ("m", "Перебур"), ["blast.diameter"] = ("mm", "Диаметр"), ["blast.angle"] = ("deg", "Угол"), ["blast.bearing"] = ("deg", "Азимут"),
        ["blast.stemmingLength"] = ("m", "Забойка"), ["blast.rockDensity"] = ("t/m3", "Плотность породы"),
        ["blast.drillingPerMetre"] = ("$/m", "Бурение за метр"), ["blast.loadingPerHole"] = ("$", "Заряжание за скважину"),
        ["blast.labourPerBlast"] = ("$", "Работа за взрыв"), ["blast.otherFixed"] = ("$", "Прочие постоянные"),
        ["monitor.x"] = ("m", "X"), ["monitor.y"] = ("m", "Y"), ["monitor.z"] = ("m", "Z"),
    };

    /// <summary>Поля, которые отражение не берёт (служебные и дублирующие связи).</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    {
        "hole.holeType", "hole.materialType", "hole.kindName", "hole.detonatorId", "hole.notes", "product.id", "product.kindId",
        "product.familyId", "product.isNew", "product.color", "deck.productId", "primer.boosterId", "primer.detonatorId",
        "tie.productId", "holeType.color", "material.color", "monitor.kindName", "tie.kindName", "timingLine.kindName",
        "surface.kindName", "product.familyName", "product.family", "product.manufacturer"
    };

    public static void Register(CalcRegistry r)
    {
        IEnumerable<object> One(CalcScope s) { yield return s.Project; }
        r.AddContext(new ContextDef { Name = "blast", NameRu = "Взрыв", ClrType = typeof(BlastProject), All = One });
        r.AddContext(new ContextDef { Name = "site", NameRu = "Рудник", ClrType = typeof(SiteMarker), All = _ => new object[] { SiteMarker.Instance } });
        r.AddContext(new ContextDef { Name = "hole", NameRu = "Скважина", ClrType = typeof(Hole), All = s => s.Project.Holes });
        r.AddContext(new ContextDef { Name = "deck", NameRu = "Интервал", ClrType = typeof(Deck), All = s => s.Project.Holes.SelectMany(h => s.DecksOf(h)) });
        r.AddContext(new ContextDef { Name = "primer", NameRu = "Боевик", ClrType = typeof(Primer), All = s => s.Project.Holes.SelectMany(h => s.PrimersOf(h)) });
        r.AddContext(new ContextDef { Name = "product", NameRu = "Продукт", ClrType = typeof(Product), All = s => s.Project.Products });
        r.AddContext(new ContextDef { Name = "row", NameRu = "Ряд", ClrType = typeof(HoleRow), All = s => s.Project.Rows });
        r.AddContext(new ContextDef { Name = "holeType", NameRu = "Тип скважины", ClrType = typeof(TypeDefinition), All = s => s.Project.Design.HoleTypes, Accepts = null });
        r.AddContext(new ContextDef { Name = "material", NameRu = "Материал", ClrType = typeof(TypeDefinition), All = s => s.Project.Design.MaterialTypes });
        r.AddContext(new ContextDef { Name = "tie", NameRu = "Связь", ClrType = typeof(TieEntity), All = s => s.Project.Ties });
        r.AddContext(new ContextDef { Name = "timingLine", NameRu = "Линия замедления", ClrType = typeof(TimingLineEntity), All = s => s.Project.Entities.OfType<TimingLineEntity>() });
        r.AddContext(new ContextDef
        {
            Name = "monitor", NameRu = "Точка наблюдения", ClrType = typeof(PointEntity),
            All = s => s.Project.Entities.OfType<PointEntity>().Where(p => p.Kind == PointKind.Monitoring),
            Accepts = o => o is PointEntity { Kind: PointKind.Monitoring }
        });
        r.AddContext(new ContextDef { Name = "surface", NameRu = "Поверхность", ClrType = typeof(SurfaceEntity), All = s => s.Project.Entities.OfType<SurfaceEntity>() });

        // поля моделей отражением
        Reflect<Hole>(r, "hole");
        Reflect<Deck>(r, "deck");
        Reflect<Primer>(r, "primer");
        Reflect<Product>(r, "product");
        Reflect<HoleRow>(r, "row");
        Reflect<TypeDefinition>(r, "holeType");
        Reflect<TypeDefinition>(r, "material");
        Reflect<TieEntity>(r, "tie");
        Reflect<TimingLineEntity>(r, "timingLine");
        Reflect<PointEntity>(r, "monitor");
        Reflect<SurfaceEntity>(r, "surface");

        // поля, которые не простые свойства
        r.Input<Hole, int>("hole.holeTypeIndex", (_, h) => h.HoleType, fields: "HoleType");
        r.Input<Hole, int>("hole.materialIndex", (_, h) => h.MaterialType, fields: "MaterialType");
        r.Input<Hole, double>("hole.x", (_, h) => h.Collar.X, fields: "Collar");
        r.Input<Hole, double>("hole.y", (_, h) => h.Collar.Y, fields: "Collar");
        r.Input<Hole, double>("hole.z", (_, h) => h.Collar.Z, fields: "Collar");
        r.Input<Hole, double>("hole.toeX", (_, h) => h.Toe.X, fields: new[] { "Collar", "Angle", "Bearing", "Length", "ActualLength", "BoreTrack" });
        r.Input<Hole, double>("hole.toeY", (_, h) => h.Toe.Y, fields: new[] { "Collar", "Angle", "Bearing", "Length", "ActualLength", "BoreTrack" });
        r.Input<Hole, double>("hole.toeZ", (_, h) => h.Toe.Z, fields: new[] { "Collar", "Angle", "Bearing", "Length", "ActualLength", "BoreTrack" });
        r.Input<PointEntity, double>("monitor.x", (_, p) => p.Position.X, fields: "Position");
        r.Input<PointEntity, double>("monitor.y", (_, p) => p.Position.Y, fields: "Position");
        r.Input<PointEntity, double>("monitor.z", (_, p) => p.Position.Z, fields: "Position");
        r.Input<Deck, string?>("deck.productId", (_, d) => d.ProductId, fields: "ProductId");

        var d = typeof(DesignData);
        foreach (var name in new[] { "Burden", "Spacing", "BenchHeight", "Subdrill", "Diameter", "Angle", "Bearing", "StemmingLength", "RockDensity", "DuplicateTolerance" })
        {
            var pi = d.GetProperty(name)!;
            r.Input<BlastProject, double>("blast." + Camel(name), (_, p) => (double)pi.GetValue(p.Design)!, fields: "Design." + name);
        }
        foreach (var name in new[] { "DrillingPerMetre", "LoadingPerHole", "LabourPerBlast", "OtherFixed" })
        {
            var pi = typeof(CostSchema).GetProperty(name)!;
            r.Input<BlastProject, double>("blast." + Camel(name), (_, p) => (double)pi.GetValue(p.Costs)!, fields: "Costs." + name);
        }
        r.Input<BlastProject, string>("blast.currency", (_, p) => p.Costs.Currency, fields: "Costs.Currency");
        r.Input<BlastProject, string>("blast.name", (_, p) => p.Header.Name, fields: "Header.Name");
        r.Input<BlastProject, double>("blast.orientation", (_, p) => p.Header.BlastOrientation, "deg", fields: "Header.BlastOrientation");
        r.Input<BlastProject, PatternType>("blast.pattern", (_, p) => p.Design.Pattern, fields: "Design.Pattern");
        r.Input<SiteMarker, string>("site.name", (s, _) => s.Project.Header.Mine, fields: "Header.Mine");

        // связи
        foreach (var c in r.Contexts.Values.Where(c => c.Name != "blast"))
            r.Ref<object>(c.Name + ".blast", "blast", (s, _) => s.Project, ru: "Взрыв");
        r.Ref<BlastProject>("blast.site", "site", (_, _) => SiteMarker.Instance, ru: "Рудник");
        r.Many<BlastProject>("blast.holes", "hole", (s, _) => s.Holes, "hole.blast", "Скважины", "Entities");
        r.Many<BlastProject>("blast.decks", "deck", (s, _) => s.Holes.SelectMany(h => s.DecksOf((Hole)h)).ToList(), "deck.blast", "Интервалы", "Entities");
        r.Many<BlastProject>("blast.primers", "primer", (s, _) => s.Holes.SelectMany(h => s.PrimersOf((Hole)h)).ToList(), "primer.blast", "Боевики", "Entities");
        r.Many<BlastProject>("blast.rows", "row", (s, p) => p.Rows, "row.blast", "Ряды", "Rows");
        r.Many<BlastProject>("blast.ties", "tie", (s, p) => p.Ties.Cast<object>().ToList(), "tie.blast", "Связи", "Entities");
        r.Many<BlastProject>("blast.monitors", "monitor", (s, p) => p.Entities.OfType<PointEntity>().Where(x => x.Kind == PointKind.Monitoring).Cast<object>().ToList(), "monitor.blast", "Точки наблюдения", "Entities");
        r.Many<BlastProject>("blast.timingLines", "timingLine", (s, p) => p.Entities.OfType<TimingLineEntity>().Cast<object>().ToList(), "timingLine.blast", "Линии замедления", "Entities");
        r.Many<BlastProject>("blast.surfaces", "surface", (s, p) => p.Entities.OfType<SurfaceEntity>().Cast<object>().ToList(), "surface.blast", "Поверхности", "Entities");
        r.Many<BlastProject>("blast.products", "product", (s, p) => p.Products, "product.blast", "Продукты", "Products");
        r.Many<BlastProject>("blast.holeTypes", "holeType", (s, p) => p.Design.HoleTypes, "holeType.blast", "Типы скважин", "Design.HoleTypes");
        r.Many<BlastProject>("blast.materials", "material", (s, p) => p.Design.MaterialTypes, "material.blast", "Материалы", "Design.MaterialTypes");

        r.Ref<Hole>("hole.row", "row", (s, h) => s.RowOf(h), "row.holes", "Ряд", "RowId");
        r.Ref<Hole>("hole.holeType", "holeType", (s, h) => s.Project.Design.HoleTypes.FirstOrDefault(t => t.Index == h.HoleType), "holeType.holes", "Тип скважины", "HoleType");
        r.Ref<Hole>("hole.material", "material", (s, h) => s.Project.Design.MaterialTypes.FirstOrDefault(t => t.Index == h.MaterialType), "material.holes", "Материал", "MaterialType");
        r.Many<Hole>("hole.decks", "deck", (s, h) => s.DecksOf(h), "deck.hole", "Интервалы", "Decks");
        r.Many<Hole>("hole.primers", "primer", (s, h) => s.PrimersOf(h), "primer.hole", "Боевики", "Decks");
        r.Many<Hole>("hole.tiesIn", "tie", (s, h) => s.TiesIn(h), "tie.to", "Входящие связи", "Entities");
        r.Many<Hole>("hole.tiesOut", "tie", (s, h) => s.TiesOut(h), "tie.from", "Исходящие связи", "Entities");
        r.Ref<Hole>("hole.timingLine", "timingLine", (s, h) => s.TimingLineOf(h), "timingLine.holes", "Линия замедления", "Entities");

        r.Ref<Deck>("deck.hole", "hole", (s, d) => s.HoleOf(d), "hole.decks", "Скважина");
        r.Ref<Deck>("deck.product", "product", (s, d) => s.FindProduct(d.ProductId), "product.decks", "Продукт", "ProductId");
        r.Many<Deck>("deck.primers", "primer", (s, d) => s.PrimersOf(d), "primer.deck", "Боевики", "Primers");
        r.Ref<Deck>("deck.prev", "deck", (s, d) => Neighbour(s, d, -1), "deck.next", "Интервал выше", "Top", "Bottom");
        r.Ref<Deck>("deck.next", "deck", (s, d) => Neighbour(s, d, +1), "deck.prev", "Интервал ниже", "Top", "Bottom");

        r.Ref<Primer>("primer.deck", "deck", (s, p) => s.DeckOf(p), "deck.primers", "Интервал");
        r.Ref<Primer>("primer.hole", "hole", (s, p) => s.DeckOf(p) is { } d ? s.HoleOf(d) : null, "hole.primers", "Скважина");
        r.Ref<Primer>("primer.booster", "product", (s, p) => s.FindProduct(p.BoosterId), "product.primers", "Промежуточный детонатор", "BoosterId");
        r.Ref<Primer>("primer.detonator", "product", (s, p) => s.FindProduct(p.DetonatorId), "product.primers", "Детонатор", "DetonatorId");

        r.Many<HoleRow>("row.holes", "hole", (s, row) => row.HoleIds.Select(s.FindEntity).OfType<Hole>().Cast<object>().ToList(), "hole.row", "Скважины", "HoleIds");
        r.Ref<TieEntity>("tie.from", "hole", (s, t) => s.FindEntity(t.FromId) as Hole, "hole.tiesOut", "Откуда", "FromId");
        r.Ref<TieEntity>("tie.to", "hole", (s, t) => s.FindEntity(t.ToId) as Hole, "hole.tiesIn", "Куда", "ToId");
        r.Ref<TieEntity>("tie.product", "product", (s, t) => s.FindProduct(t.ProductId), "product.ties", "Соединитель", "ProductId");
        r.Many<TimingLineEntity>("timingLine.holes", "hole", (s, l) => l.HoleIds.Select(s.FindEntity).OfType<Hole>().Cast<object>().ToList(), "hole.timingLine", "Скважины", "HoleIds");
        r.Many<TypeDefinition>("holeType.holes", "hole", (s, t) => s.Project.Holes.Where(h => h.HoleType == t.Index).Cast<object>().ToList(), "hole.holeType", "Скважины");
        r.Many<TypeDefinition>("material.holes", "hole", (s, t) => s.Project.Holes.Where(h => h.MaterialType == t.Index).Cast<object>().ToList(), "hole.material", "Скважины");

        foreach (var (id, (unit, ru)) in Known)
            if (r.Property(id) is { } p)
            {
                p.Unit = unit;
                if (ru.Length > 0) p.NameRu = ru;
            }
    }

    private static object? Neighbour(CalcScope s, Deck d, int step)
    {
        if (s.HoleOf(d) is not { } h) return null;
        var decks = s.DecksOf(h);
        for (int i = 0; i < decks.Count; i++)
            if (ReferenceEquals(decks[i], d)) return i + step >= 0 && i + step < decks.Count ? decks[i + step] : null;
        return null;
    }

    public static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static readonly MethodInfo InputMethod = typeof(CalcRegistry).GetMethod(nameof(CalcRegistry.Input))!;

    private static void Reflect<T>(CalcRegistry r, string context)
    {
        foreach (var pi in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pi.GetIndexParameters().Length > 0 || !pi.CanRead) continue;
            var t = pi.PropertyType;
            var nt = Nullable.GetUnderlyingType(t) ?? t;
            if (!(nt == typeof(double) || nt == typeof(int) || nt == typeof(bool) || nt == typeof(string) || nt.IsEnum)) continue;
            if (t == typeof(bool?)) continue;
            var id = context + "." + Camel(pi.Name);
            if (Skip.Contains(id)) continue;
            var param = System.Linq.Expressions.Expression.Parameter(typeof(T));
            var getter = System.Linq.Expressions.Expression.Lambda(System.Linq.Expressions.Expression.Property(param, pi), param).Compile();
            // Func<T, V> → Func<CalcScope, T, V>
            var wrap = typeof(ModelContexts).GetMethod(nameof(Lift), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(T), t);
            var lifted = wrap.Invoke(null, new object[] { getter })!;
            InputMethod.MakeGenericMethod(typeof(T), t).Invoke(r, new object?[] { id, lifted, "?", "", pi.Name, new[] { pi.Name } });
        }
    }

    private static Func<CalcScope, T, V> Lift<T, V>(Func<T, V> f) => (_, o) => f(o);
}

/// <summary>Объект контекста <c>site</c> (рудник): один на сеанс, его значения — параметры уровня рудника.</summary>
public sealed class SiteMarker
{
    public static readonly SiteMarker Instance = new();
    private SiteMarker() { }
}
