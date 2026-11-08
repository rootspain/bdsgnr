using BlastDesigner.Calc;

namespace BlastDesigner.Engine;

/// <summary>Место формулы библиотеки, куда подставляется свойство: <c>$q</c> в тексте.</summary>
public sealed record FormulaSlot(string Key, string Name, string Default);

/// <summary>Типовая формула: кодовый текст с местами <c>$ключ</c>, описание, источник, единица итога и новые параметры с диапазонами.</summary>
public sealed record FormulaTemplate(string Name, string Category, string Context, string Unit, string Expression, string Description, string Source,
    IReadOnlyList<FormulaSlot> Slots, IReadOnlyList<ParameterDef> Parameters);

/// <summary>Каталог типовых формул взрывного дела для «Вставить из библиотеки».</summary>
public static class FormulaLibrary
{
    private static FormulaSlot S(string key, string name, string def) => new(key, name, def);

    private static ParameterDef P(string id, string name, double value, string range) =>
        new() { Id = id, NameRu = name, Unit = "?", Value = value, Description = range };

    private static readonly FormulaSlot[] None = Array.Empty<FormulaSlot>();
    private static readonly ParameterDef[] NoParams = Array.Empty<ParameterDef>();

    public static readonly IReadOnlyList<FormulaTemplate> Templates = new[]
    {
        new FormulaTemplate("Масса колонки ВВ", "Заряжание", "deck", "кг",
            "pi * (to(m, $d) / 2) ^ 2 * length * to(kg/m3, $rho)",
            "Объём интервала по диаметру скважины, умноженный на плотность продукта.", "Геометрия колонки",
            new[] { S("d", "Диаметр скважины", "hole.diameter"), S("rho", "Плотность ВВ", "product.density") }, NoParams),
        new FormulaTemplate("Удельный расход скважины", "Заряжание", "hole", "кг/м³",
            "if($v > 0, $q / $v, 0)", "Масса ВВ на объём породы, приходящийся на скважину.", "Определение",
            new[] { S("q", "Масса ВВ", "chargeMass"), S("v", "Объём породы", "volume") }, NoParams),
        new FormulaTemplate("Удельный расход взрыва", "Заряжание", "blast", "кг/м³",
            "if($v > 0, $q / $v, 0)", "Масса ВВ взрыва на объём взорванной породы.", "Определение",
            new[] { S("q", "Масса ВВ", "explosiveKg"), S("v", "Объём породы", "volume") }, NoParams),
        new FormulaTemplate("Л.С.П. по Лангефорсу", "Сетка", "hole", "м",
            "$d / 33 * sqrt($rho * param(lib.lang.s) / (param(lib.lang.c) * param(lib.lang.f) * param(lib.lang.sb)))",
            "Наибольшая линия сопротивления: Bmax = (d/33)·√(ρ·s / (c·f·S/B)), d в мм, итог в м.", "Langefors U., Kihlström B. The Modern Technique of Rock Blasting, 1963",
            new[] { S("d", "Диаметр скважины, мм", "diameter"), S("rho", "Плотность заряжания, кг/дм³", "avg(decks where product.isExplosive, product.density)") },
            new[]
            {
                P("lib.lang.s", "Лангефорс: сила ВВ s", 1, "ANFO 0,9–1,0; эмульсии 1,0–1,3"),
                P("lib.lang.c", "Лангефорс: константа породы c", 0.4, "обычно 0,3–0,5"),
                P("lib.lang.f", "Лангефорс: степень зажима f", 1, "вертикальные скважины 1,0; наклон 3:1 — 0,9"),
                P("lib.lang.sb", "Лангефорс: отношение S/B", 1.25, "обычно 1,0–1,5")
            }),
        new FormulaTemplate("Сейсмика: приведённое расстояние по √Q", "Сейсмика", "monitor", "мм/с",
            "if($q <= 0 or $r <= 0, 0, param(site.vib.K) * ($r / sqrt($q)) ^ (-param(site.vib.B)))",
            "PPV = K·(R/√Q)^(−B): цилиндрические заряды, средние и дальние расстояния.", "Duvall W., Petkof B., USBM RI 5968, 1959; Siskind et al., USBM RI 8507, 1980",
            new[] { S("r", "Расстояние до заряда, м", "distance"), S("q", "Заряд на замедление, кг", "blast.mic") }, NoParams),
        new FormulaTemplate("Сейсмика: приведённое расстояние по ∛Q", "Сейсмика", "monitor", "мм/с",
            "if($q <= 0 or $r <= 0, 0, param(lib.vib.K3) * ($r / cbrt($q)) ^ (-param(lib.vib.B3)))",
            "PPV = K·(R/∛Q)^(−B): сосредоточенные заряды, ближняя зона.", "Ambraseys N., Hendron A., 1968",
            new[] { S("r", "Расстояние до заряда, м", "distance"), S("q", "Заряд на замедление, кг", "blast.mic") },
            new[] { P("lib.vib.K3", "Сейсмика ∛Q: K", 1000, "обычно 500–5000, уточняется по замерам"), P("lib.vib.B3", "Сейсмика ∛Q: B", 1.6, "обычно 1,2–2,0") }),
        new FormulaTemplate("Кузнецов–Рамлер: средний кусок X50", "Фрагментация", "blast", "см",
            "if($pf <= 0 or $q <= 0, 0, $a * $pf ^ -0.8 * $q ^ (1 / 6) * (115 / $rws) ^ (19 / 30))",
            "X50 = A·PF^(−0,8)·Q^(1/6)·(115/RWS)^(19/30). Фактор породы A обычно 4–14.", "Кузнецов В.М., 1973; Cunningham C., 1983, 1987",
            new[] { S("a", "Фактор породы A", "rockFactor"), S("pf", "Удельный расход, кг/м³", "powderFactor"), S("q", "Заряд скважины, кг", "avgCharge"), S("rws", "Относительная сила ВВ, %", "avgRws") }, NoParams),
        new FormulaTemplate("Swebrec: доля прохода через сито", "Фрагментация", "blast", "доля",
            "1 / (1 + (ln(param(lib.swebrec.xmax) / param(lib.swebrec.x)) / ln(param(lib.swebrec.xmax) / $x50)) ^ (2 * ln(2) * ln(param(lib.swebrec.xmax) / $x50) * $n))",
            "Функция Swebrec: доля материала мельче заданного размера.", "Ouchterlony F., 2005",
            new[] { S("x50", "Средний кусок X50, см", "x50"), S("n", "Показатель однородности n", "uniformity") },
            new[] { P("lib.swebrec.xmax", "Swebrec: наибольший кусок, см", 200, "по блочности массива, обычно 100–400"), P("lib.swebrec.x", "Swebrec: размер сита, см", 50, "размер, для которого нужна доля") }),
        new FormulaTemplate("Разлёт с откоса (Richards & Moore)", "Разлёт", "hole", "м",
            "if($b <= 0, 0, param(site.flyrock.K) ^ 2 / 9.81 * (sqrt($m) / $b) ^ 2.6)",
            "L = k²/g·(√m/B)^2,6. Константа k: 13,5 обычно, 27 с запасом.", "Richards A., Moore A., 2004",
            new[] { S("m", "Масса на метр, кг/м", "kgPerMetre"), S("b", "Л.С.П., м", "flyrockBurden") }, NoParams),
        new FormulaTemplate("Разлёт из устья (Richards & Moore)", "Разлёт", "hole", "м",
            "if($t <= 0, inf, param(site.flyrock.K) ^ 2 / 9.81 * (sqrt($m) / $t) ^ 2.6)",
            "L = k²/g·(√m/T)^2,6, T — длина забойки над зарядом.", "Richards A., Moore A., 2004",
            new[] { S("m", "Масса на метр, кг/м", "kgPerMetre"), S("t", "Забойка над зарядом, м", "explosiveTop") }, NoParams),
        new FormulaTemplate("Опасная зона по разлёту", "Опасная зона", "blast", "м",
            "max(param(site.flyrock.minClearance), $f * param(site.flyrock.safetyFactor))",
            "Наибольший разлёт с запасом, но не меньше установленного минимума.", "Richards A., Moore A., 2004",
            new[] { S("f", "Наибольший разлёт, м", "flyrockMax") }, NoParams),
        new FormulaTemplate("Радиус по сейсмике", "Опасная зона", "blast", "м",
            "sqrt(max(0, $q)) * (param(site.vib.K) / param(site.vib.ppvLimit)) ^ (1 / param(site.vib.B))",
            "Расстояние, на котором скорость колебаний падает до допустимой.", "Обращение закона PPV = K·(R/√Q)^(−B)",
            new[] { S("q", "Заряд на замедление, кг", "micOrMaxCharge") }, NoParams),
    };

    /// <summary>Шаблоны для свойства этого объекта.</summary>
    public static IReadOnlyList<FormulaTemplate> For(string context) => Templates.Where(t => t.Context == context).ToList();

    /// <summary>Кодовый текст формулы с выражениями на местах (кодом или по-русски; непростое выражение — в скобках).</summary>
    public static string Build(FormulaTemplate t, IReadOnlyDictionary<string, string>? values = null)
    {
        var text = t.Expression;
        foreach (var s in t.Slots.OrderByDescending(x => x.Key.Length))
        {
            var v = values != null && values.TryGetValue(s.Key, out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : s.Default;
            var simple = v.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '[' or ']' or ' ') && (!v.Contains(' ') || (v.StartsWith('[') && v.EndsWith(']') && v.IndexOf('[', 1) < 0));
            text = text.Replace("$" + s.Key, simple ? v : "(" + v + ")");
        }
        return text;
    }

    /// <summary>Добавляет в профиль параметры шаблона, которых в нём ещё нет.</summary>
    public static void AddParameters(CalcProfile draft, FormulaTemplate t, Func<string, bool> exists)
    {
        foreach (var p in t.Parameters)
            if (!exists(p.Id) && draft.Parameters.All(x => x.Id != p.Id)) draft.Parameters.Add(p.Clone());
    }
}

/// <summary>Разобранная таблица параметра: точки, единица значений из заголовка, ошибки строк и замечания.</summary>
public sealed record ParameterTable(List<TablePoint> Points, string? Unit, List<string> Errors, List<string> Notes)
{
    public bool IsValid => Errors.Count == 0 && Points.Count > 0;
}

/// <summary>Разбор таблицы «ключ — значение» из буфера (Excel: табуляция) или CSV; десятичная запятая и точка.</summary>
public static class ParameterTableParser
{
    public static ParameterTable Parse(string text, string parameterUnit)
    {
        var errors = new List<string>();
        var notes = new List<string>();
        var points = new List<(double X, double Y)>();
        string? unit = null;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var data = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim('﻿', ' ');
            if (line.Trim().Length == 0) continue;
            var cells = Split(line);
            if (cells.Length >= 2 && Number(cells[0]) is { } x && Number(cells[1]) is { } y)
            {
                data = true;
                points.Add((x, y));
                if (cells.Length > 2) notes.Add($"Строка {i + 1}: лишние столбцы пропущены");
                continue;
            }
            if (!data && points.Count == 0)
            {
                // заголовок: единица берётся из второго столбца, «K, мм/с» или «K (мм/с)»
                if (cells.Length >= 2) unit = UnitIn(cells[1]);
                continue;
            }
            errors.Add($"Строка {i + 1}: нужны два числа, а здесь «{line.Trim()}»");
        }
        var factor = 1.0;
        if (unit != null && Calc.Unit.TryParse(unit, out var from) && Calc.Unit.TryParse(parameterUnit, out var to) && !from.IsUnknown && !to.IsUnknown)
        {
            if (!from.SameDimension(to)) errors.Add($"Единица столбца {unit} несовместима с единицей параметра {Aliases.RussianUnit(to.Symbol)}");
            else if (Math.Abs(from.FactorTo(to) - 1) > 1e-12)
            {
                factor = from.FactorTo(to);
                notes.Add($"Значения пересчитаны из {unit} в {Aliases.RussianUnit(to.Symbol)}");
            }
        }
        foreach (var dup in points.GroupBy(p => p.X).Where(g => g.Count() > 1)) errors.Add($"Ключ {dup.Key} встречается {dup.Count()} раза");
        if (points.Count == 0 && errors.Count == 0) errors.Add("В тексте нет строк из двух чисел");
        return new ParameterTable(points.OrderBy(p => p.X).Select(p => new TablePoint(p.X, p.Y * factor)).ToList(), unit, errors, notes);
    }

    private static string[] Split(string line)
    {
        if (line.Contains('\t')) return line.Split('\t').Select(c => c.Trim()).ToArray();
        if (line.Contains(';')) return line.Split(';').Select(c => c.Trim()).ToArray();
        var spaced = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (spaced.Length >= 2 && Number(spaced[0]) != null && Number(spaced[1]) != null) return spaced;
        return line.Split(',').Select(c => c.Trim()).ToArray();
    }

    private static double? Number(string s) =>
        double.TryParse(s.Replace(" ", "").Replace(' ', ' ').Replace(" ", "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? UnitIn(string header)
    {
        var open = header.LastIndexOf('(');
        if (open >= 0 && header.EndsWith(')')) return header[(open + 1)..^1].Trim();
        var comma = header.LastIndexOf(',');
        return comma >= 0 ? header[(comma + 1)..].Trim() : null;
    }
}
