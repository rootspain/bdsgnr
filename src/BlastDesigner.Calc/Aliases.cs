using System.Text.RegularExpressions;

namespace BlastDesigner.Calc;

/// <summary>
/// Псевдонимы языка формул на языках интерфейса (ru, kk, fr): функции, ключевые слова, константы и единицы. Свойства и
/// связи называются своими названиями в квадратных скобках (<c>[Масса ВВ]</c>); парсер принимает кодовые имена и
/// псевдонимы любого языка, так что формулу, набранную по-русски, понимает и французский интерфейс. Английский — это
/// кодовая запись (<c>en</c>).
/// </summary>
public static class Aliases
{
    /// <summary>Языки формул с псевдонимами (кроме кодового английского).</summary>
    public static readonly string[] Languages = { "ru", "kk", "fr" };

    private static Dictionary<string, string> Table(params (string Code, string Name)[] items)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (c, n) in items) d[c] = n;
        return d;
    }

    /// <summary>Кодовое имя функции → имя на языке формул, по языкам.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> FunctionsIn = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["ru"] = Table(("sum", "сумма"), ("avg", "среднее"), ("min", "мин"), ("max", "макс"), ("count", "количество"), ("first", "первый"),
            ("if", "если"), ("case", "выбор"), ("coalesce", "первое_из"), ("exists", "есть"), ("clamp", "ограничить"),
            ("round", "округл"), ("floor", "вниз"), ("ceil", "вверх"), ("abs", "модуль"), ("sqrt", "корень"), ("cbrt", "корень3"),
            ("log10", "lg"), ("ln", "ln"), ("exp", "exp"), ("sin", "sin"), ("cos", "cos"), ("tan", "tg"), ("pi", "пи"),
            ("param", "параметр"), ("lookup", "таблица"), ("to", "в"),
            ("cellArea", "площадь_ячейки"), ("patternBurden", "лнс_сетки"), ("patternSpacing", "расстояние_сетки"), ("mic", "мвз"),
            ("firingTime", "время_срабатывания"), ("distToLine", "расстояние_до_линии"), ("nearestChargeDistance", "до_ближайшего_заряда"),
            ("predictPpv", "прогноз_скорости"), ("studentT", "стьюдент")),
        ["kk"] = Table(("sum", "қосынды"), ("avg", "орташа"), ("min", "мин"), ("max", "макс"), ("count", "саны"), ("first", "бірінші"),
            ("if", "егер"), ("case", "таңдау"), ("coalesce", "біріншісі"), ("exists", "бар"), ("clamp", "шектеу"),
            ("round", "дөңгелектеу"), ("floor", "төмен"), ("ceil", "жоғары"), ("abs", "модуль"), ("sqrt", "түбір"), ("cbrt", "түбір3"),
            ("log10", "lg"), ("ln", "ln"), ("exp", "exp"), ("sin", "sin"), ("cos", "cos"), ("tan", "tg"), ("pi", "пи"),
            ("param", "параметр"), ("lookup", "кесте"), ("to", "қа"),
            ("cellArea", "ұяшық_ауданы"), ("patternBurden", "тор_еқкс"), ("patternSpacing", "тор_қашықтығы"), ("mic", "ең_үлкен_заряд"),
            ("firingTime", "жару_уақыты"), ("distToLine", "сызыққа_дейін"), ("nearestChargeDistance", "жақын_зарядқа_дейін"),
            ("predictPpv", "діріл_болжамы"), ("studentT", "стьюдент")),
        ["fr"] = Table(("sum", "somme"), ("avg", "moyenne"), ("min", "min"), ("max", "max"), ("count", "nombre"), ("first", "premier"),
            ("if", "si"), ("case", "selon"), ("coalesce", "premier_de"), ("exists", "existe"), ("clamp", "borner"),
            ("round", "arrondi"), ("floor", "plancher"), ("ceil", "plafond"), ("abs", "abs"), ("sqrt", "racine"), ("cbrt", "racine3"),
            ("log10", "log10"), ("ln", "ln"), ("exp", "exp"), ("sin", "sin"), ("cos", "cos"), ("tan", "tan"), ("pi", "pi"),
            ("param", "paramètre"), ("lookup", "table"), ("to", "en"),
            ("cellArea", "aire_cellule"), ("patternBurden", "banquette_maille"), ("patternSpacing", "espacement_maille"), ("mic", "charge_unitaire_max"),
            ("firingTime", "instant_tir"), ("distToLine", "distance_ligne"), ("nearestChargeDistance", "distance_charge_proche"),
            ("predictPpv", "prévision_vitesse"), ("studentT", "student")),
    };

    /// <summary>Кодовое ключевое слово или константа → слово на языке формул, по языкам.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> WordsIn = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["ru"] = Table(("and", "и"), ("or", "или"), ("not", "не"), ("where", "где"),
            ("pi", "пи"), ("inf", "бесконечность"), ("true", "истина"), ("false", "ложь"), ("null", "пусто")),
        ["kk"] = Table(("and", "және"), ("or", "немесе"), ("not", "емес"), ("where", "мұнда"),
            ("pi", "пи"), ("inf", "шексіздік"), ("true", "ақиқат"), ("false", "жалған"), ("null", "бос")),
        ["fr"] = Table(("and", "et"), ("or", "ou"), ("not", "non"), ("where", "où"),
            ("pi", "pi"), ("inf", "infini"), ("true", "vrai"), ("false", "faux"), ("null", "nul")),
    };

    /// <summary>Кодовое имя функции → русское (как было до других языков).</summary>
    public static IReadOnlyDictionary<string, string> Functions => FunctionsIn["ru"];

    /// <summary>Кодовое ключевое слово или константа → русское.</summary>
    public static IReadOnlyDictionary<string, string> Words => WordsIn["ru"];

    private static readonly Dictionary<string, string> FunctionsBack = Back(FunctionsIn, true);
    private static readonly Dictionary<string, string> WordsBack = Back(WordsIn, false);

    /// <summary>Имя на любом языке → кодовое; при неоднозначности между языками берётся первый язык (тест следит, чтобы их не было).</summary>
    private static Dictionary<string, string> Back(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables, bool skipSame)
    {
        var back = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in Languages)
            foreach (var (code, name) in tables[lang])
                if (!(skipSame && string.Equals(code, name, StringComparison.OrdinalIgnoreCase))) back.TryAdd(name, code);
        return back;
    }

    /// <summary>Все известные имена функций: кодовые и на всех языках (для подсказки при опечатке).</summary>
    public static IEnumerable<string> AllFunctionNames => FunctionsIn.Values.SelectMany(t => t.Keys.Concat(t.Values)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Имена, которые на разных языках означают разные кодовые функции или слова, и псевдонимы, совпадающие с другим
    /// кодовым именем (для теста: таких быть не должно).
    /// </summary>
    public static IReadOnlyList<string> Ambiguities()
    {
        var list = new List<string>();
        void Check(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> tables, string what)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lang in Languages)
                foreach (var (code, name) in tables[lang])
                {
                    if (seen.TryGetValue(name, out var other) && !string.Equals(other, code, StringComparison.OrdinalIgnoreCase)) list.Add($"{what} «{name}»: {other} / {code}");
                    else seen[name] = code;
                    if (!string.Equals(code, name, StringComparison.OrdinalIgnoreCase) && tables[lang].ContainsKey(name)) list.Add($"{what} «{name}» ({lang}) — это и кодовое имя");
                }
        }
        Check(FunctionsIn, "функция");
        Check(WordsIn, "слово");
        return list;
    }

    /// <summary>Кодовое имя функции по любому из имён (сумма, қосынды, somme → sum); неизвестное — как есть.</summary>
    public static string Function(string name) => FunctionsBack.TryGetValue(name, out var code) ? code : name;

    /// <summary>Кодовое ключевое слово или константа по любому из имён (и, және, et → and).</summary>
    public static string Word(string name) => WordsBack.TryGetValue(name, out var code) ? code : name.ToLowerInvariant();

    /// <summary>Имя функции на языке формул (en — кодовое).</summary>
    public static string FunctionName(string code, string language) =>
        FunctionsIn.TryGetValue(language, out var t) && t.TryGetValue(code, out var name) ? name : code;

    /// <summary>Ключевое слово на языке формул (en — кодовое).</summary>
    public static string WordName(string code, string language) =>
        WordsIn.TryGetValue(language, out var t) && t.TryGetValue(code, out var name) ? name : code;

    public static string RussianFunction(string code) => FunctionName(code, "ru");
    public static string RussianWord(string code) => WordName(code, "ru");

    /// <summary>Кириллические обозначения единиц для ввода (ru, kk): м, мм, кг/м³, МДж/т.</summary>
    internal static readonly (string Ru, string En)[] UnitAtoms =
    {
        ("мм", "mm"), ("см", "cm"), ("км", "km"), ("м", "m"), ("кг", "kg"), ("г", "g"), ("т", "t"), ("мс", "ms"), ("мин", "min"),
        ("ч", "h"), ("с", "s"), ("МДж", "MJ"), ("кДж", "kJ"), ("Дж", "J"), ("кПа", "kPa"), ("МПа", "MPa"), ("ГПа", "GPa"), ("Па", "Pa"),
        ("град", "deg"), ("°", "deg"), ("дБ", "dB"), ("шт", "ea"), ("руб", "$"), ("₽", "$"),
    };

    /// <summary>Языки, где единицы пишутся кириллицей (ru, kk); во французском — латиницей, как в кодовой записи.</summary>
    public static bool CyrillicUnits(string language) => language is "ru" or "kk";

    private static readonly Regex UnitWord = new(@"[A-Za-zА-Яа-яЁё°₽$]+", RegexOptions.Compiled);

    /// <summary>
    /// Единица на языке формул для сообщений и подсказок: ru и kk — kg/m3 → кг/м³; fr — латиница со степенями (kg/m³);
    /// en — как записана.
    /// </summary>
    public static string Unit(string symbol, string language)
    {
        if (symbol is "?" or "1" or "") return symbol;
        if (language == "en") return symbol;
        var s = CyrillicUnits(language) ? UnitWord.Replace(symbol, m => UnitAtoms.FirstOrDefault(a => a.En == m.Value).Ru ?? m.Value) : symbol;
        return s.Replace("3", "³").Replace("2", "²").Replace("*", "·");
    }

    /// <summary>Единица на текущем языке формул (<see cref="CalcText.Language"/>).</summary>
    public static string Unit(string symbol) => Unit(symbol, CalcText.Language);

    /// <summary>Единица по-русски для сообщений и подсказок: kg/m3 → кг/м³.</summary>
    public static string RussianUnit(string symbol) => Unit(symbol, "ru");

    /// <summary>Расстояние Левенштейна без учёта регистра (для подсказки «возможно, вы имели в виду»).</summary>
    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant().Replace('ё', 'е');
        b = b.ToLowerInvariant().Replace('ё', 'е');
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    /// <summary>Самые близкие варианты к опечатке: не дальше трети длины (минимум 2 правки).</summary>
    public static IReadOnlyList<string> Closest(string typo, IEnumerable<string> candidates, int take = 3)
    {
        var limit = Math.Max(2, typo.Length / 3);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (c, d: Distance(typo, c)))
            .Where(x => x.d <= limit || x.c.StartsWith(typo, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.d).ThenBy(x => x.c.Length).Take(take).Select(x => x.c).ToList();
    }
}
