using System.Text.RegularExpressions;

namespace BlastDesigner.Calc;

/// <summary>
/// Русские псевдонимы языка формул: функции, ключевые слова, константы и единицы. Свойства и связи называются своими
/// русскими названиями в квадратных скобках (<c>[Масса ВВ]</c>); парсер принимает и кодовые, и русские имена.
/// </summary>
public static class Aliases
{
    /// <summary>Кодовое имя функции → русское.</summary>
    public static readonly IReadOnlyDictionary<string, string> Functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sum"] = "сумма", ["avg"] = "среднее", ["min"] = "мин", ["max"] = "макс", ["count"] = "количество", ["first"] = "первый",
        ["if"] = "если", ["case"] = "выбор", ["coalesce"] = "первое_из", ["exists"] = "есть", ["clamp"] = "ограничить",
        ["round"] = "округл", ["floor"] = "вниз", ["ceil"] = "вверх", ["abs"] = "модуль", ["sqrt"] = "корень", ["cbrt"] = "корень3",
        ["log10"] = "lg", ["ln"] = "ln", ["exp"] = "exp", ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tg", ["pi"] = "пи",
        ["param"] = "параметр", ["lookup"] = "таблица", ["to"] = "в",
        ["cellArea"] = "площадь_ячейки", ["patternBurden"] = "лнс_сетки", ["patternSpacing"] = "расстояние_сетки", ["mic"] = "мвз",
        ["firingTime"] = "время_срабатывания", ["distToLine"] = "расстояние_до_линии", ["nearestChargeDistance"] = "до_ближайшего_заряда",
        ["predictPpv"] = "прогноз_скорости", ["studentT"] = "стьюдент",
    };

    private static readonly Dictionary<string, string> FunctionsBack =
        Functions.Where(f => !string.Equals(f.Key, f.Value, StringComparison.OrdinalIgnoreCase)).ToDictionary(f => f.Value, f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Кодовое ключевое слово или константа → русское.</summary>
    public static readonly IReadOnlyDictionary<string, string> Words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["and"] = "и", ["or"] = "или", ["not"] = "не", ["where"] = "где",
        ["pi"] = "пи", ["inf"] = "бесконечность", ["true"] = "истина", ["false"] = "ложь", ["null"] = "пусто",
    };

    private static readonly Dictionary<string, string> WordsBack = Words.ToDictionary(w => w.Value, w => w.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Кодовое имя функции по любому из имён (сумма → sum); неизвестное — как есть.</summary>
    public static string Function(string name) => FunctionsBack.TryGetValue(name, out var code) ? code : name;

    /// <summary>Кодовое ключевое слово или константа по любому из имён (и → and).</summary>
    public static string Word(string name) => WordsBack.TryGetValue(name, out var code) ? code : name.ToLowerInvariant();

    public static string RussianFunction(string code) => Functions.TryGetValue(code, out var ru) ? ru : code;
    public static string RussianWord(string code) => Words.TryGetValue(code, out var ru) ? ru : code;

    /// <summary>Русские обозначения единиц для ввода: м, мм, кг/м³, МДж/т.</summary>
    internal static readonly (string Ru, string En)[] UnitAtoms =
    {
        ("мм", "mm"), ("см", "cm"), ("км", "km"), ("м", "m"), ("кг", "kg"), ("г", "g"), ("т", "t"), ("мс", "ms"), ("мин", "min"),
        ("ч", "h"), ("с", "s"), ("МДж", "MJ"), ("кДж", "kJ"), ("Дж", "J"), ("кПа", "kPa"), ("МПа", "MPa"), ("ГПа", "GPa"), ("Па", "Pa"),
        ("град", "deg"), ("°", "deg"), ("дБ", "dB"), ("шт", "ea"), ("руб", "$"), ("₽", "$"),
    };

    private static readonly Regex UnitWord = new(@"[A-Za-zА-Яа-яЁё°₽$]+", RegexOptions.Compiled);

    /// <summary>Единица по-русски для сообщений и подсказок: kg/m3 → кг/м³.</summary>
    public static string RussianUnit(string symbol)
    {
        if (symbol is "?" or "1" or "") return symbol;
        var s = UnitWord.Replace(symbol, m => UnitAtoms.FirstOrDefault(a => a.En == m.Value).Ru ?? m.Value);
        return s.Replace("3", "³").Replace("2", "²").Replace("*", "·");
    }

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
