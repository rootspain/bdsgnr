using System.Reflection;

namespace BlastDesigner.Calc;

/// <summary>
/// Язык формул и названий расчёта: <c>en</c> (кодовые имена и английские названия), <c>ru</c>, <c>kk</c>, <c>fr</c>.
/// Названия ищутся по цепочке: kk → ru → en, fr → en, ru → en. Названия, заданные в коде по-русски (контексты, поля
/// модели, связи, встроенные алгоритмы, группы, библиотека формул), переводятся по встроенной таблице calc-names.tsv.
/// </summary>
public static class CalcText
{
    /// <summary>Язык формул и названий программы; задаёт приложение (CalcService.FormulaLanguage).</summary>
    public static string Language { get; set; } = "ru";

    /// <summary>
    /// Язык сообщений языка формул (ошибки разбора и компиляции, диагностика модели, ошибки вычисления): язык интерфейса
    /// (en, ru, kk, fr). Задаёт приложение (CalcService.MessageLanguage); не зависит от «Формулы по-английски».
    /// </summary>
    public static string MessageLanguage { get; set; } = "ru";

    private static readonly Lazy<Dictionary<string, (string En, string Kk, string Fr)>> Messages = new(() => LoadTsv(".calc-messages.tsv"));

    /// <summary>
    /// Сообщение на языке сообщений: <paramref name="russian"/> — русская строка формата из кода (ключ таблицы
    /// calc-messages.tsv), <paramref name="args"/> — значения {0}, {1}…; нет перевода — русская строка.
    /// </summary>
    public static string M(string russian, params object?[] args)
    {
        var format = russian;
        if (MessageLanguage != "ru" && Messages.Value.TryGetValue(russian, out var t))
        {
            var tr = MessageLanguage switch { "en" => t.En, "kk" => t.Kk, "fr" => t.Fr, _ => "" };
            if (tr.Length > 0) format = tr;
        }
        return args.Length == 0 ? format : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
    }

    /// <summary>Варианты через «или» на языке сообщений: [a] или [b].</summary>
    public static string Or(IEnumerable<string> items) => string.Join(M(" или "), items);

    /// <summary>Порядок поиска названия для языка.</summary>
    public static IReadOnlyList<string> Chain(string language) => language switch
    {
        "kk" => new[] { "kk", "ru", "en" },
        "fr" => new[] { "fr", "en" },
        "ru" => new[] { "ru", "en" },
        _ => new[] { "en" }
    };

    private static readonly Lazy<Dictionary<string, (string En, string Kk, string Fr)>> Table = new(() => LoadTsv(".calc-names.tsv"));

    /// <summary>Встроенная таблица «русский TAB английский TAB казахский TAB французский».</summary>
    private static Dictionary<string, (string En, string Kk, string Fr)> LoadTsv(string suffix)
    {
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (resource == null) return map;
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length >= 4) map[c[0]] = (c[1], c[2], c[3]);
        }
        return map;
    }

    /// <summary>Перевод русского названия, заданного в коде, на язык (en, kk, fr); пусто — нет в таблице.</summary>
    public static string Translate(string russian, string language)
    {
        if (language == "ru") return russian;
        if (russian.Length == 0 || !Table.Value.TryGetValue(russian, out var t)) return "";
        return language switch { "en" => t.En, "kk" => t.Kk, "fr" => t.Fr, _ => "" };
    }

    /// <summary>Название ровно на этом языке (без цепочки); пусто — нет.</summary>
    public static string NameIn(string language, string ru, string en, string? kk, string? fr) => language switch
    {
        "ru" => ru,
        "en" => en.Length > 0 ? en : Translate(ru, "en"),
        "kk" => kk is { Length: > 0 } ? kk : Translate(ru, "kk"),
        "fr" => fr is { Length: > 0 } ? fr : Translate(ru, "fr"),
        _ => ""
    };

    /// <summary>Название на языке по цепочке; пусто — ни на одном языке цепочки.</summary>
    public static string Caption(string language, string ru, string en, string? kk, string? fr)
    {
        foreach (var l in Chain(language))
            if (NameIn(l, ru, en, kk, fr) is { Length: > 0 } n) return n;
        return "";
    }

    /// <summary>Название группы свойств (в профиле — по-русски) на языке.</summary>
    public static string Group(string group, string language) =>
        language == "ru" || group.Length == 0 ? group : Translate(group, language) is { Length: > 0 } t ? t : language == "kk" ? group : Translate(group, "en") is { Length: > 0 } e ? e : group;

    public static string Group(string group) => Group(group, Language);

    // ---- определения ----------------------------------------------------

    public static string NameIn(this PropertyDef p, string language) => NameIn(language, p.NameRu, p.NameEn, p.NameKk, p.NameFr);
    public static string NameIn(this ParameterDef p, string language) => NameIn(language, p.NameRu, p.NameEn, p.NameKk, p.NameFr);
    public static string NameIn(this ContextDef c, string language) => NameIn(language, c.NameRu, "", c.NameKk, c.NameFr);
    public static string NameIn(this BuiltinDef b, string language) => NameIn(language, b.NameRu, "", b.NameKk, b.NameFr);

    /// <summary>Название свойства на языке (по цепочке), иначе кодовое имя.</summary>
    public static string Caption(this PropertyDef p, string language) => Caption(language, p.NameRu, p.NameEn, p.NameKk, p.NameFr) is { Length: > 0 } n ? n : p.Name;
    public static string Caption(this ParameterDef p, string language) => Caption(language, p.NameRu, p.NameEn, p.NameKk, p.NameFr) is { Length: > 0 } n ? n : p.Id;
    public static string Caption(this ContextDef c, string language) => Caption(language, c.NameRu, "", c.NameKk, c.NameFr) is { Length: > 0 } n ? n : c.Name;
    public static string Caption(this BuiltinDef b, string language) => Caption(language, b.NameRu, "", b.NameKk, b.NameFr) is { Length: > 0 } n ? n : b.Name;

    /// <summary>Пишет название на языке (en — английское, ru — русское, kk/fr — свои поля).</summary>
    public static void SetName(this PropertyDef p, string language, string value)
    {
        switch (language)
        {
            case "en": p.NameEn = value; break;
            case "kk": p.NameKk = value; break;
            case "fr": p.NameFr = value; break;
            default: p.NameRu = value; break;
        }
    }
}
