using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlastDesigner.Calc;

/// <summary>
/// Профиль расчёта: пользовательские свойства, формулы, параметры, правила и контрольные примеры. Наследует базовый
/// профиль по имени (<see cref="Base"/>): свойство, формула или параметр с тем же именем заменяют базовые.
/// Хранится в файле <c>*.bdcalc</c> (JSON).
/// </summary>
public sealed class CalcProfile
{
    public const string FileExtension = ".bdcalc";

    public string Name { get; set; } = "";
    public string Version { get; set; } = "1";
    public string? Base { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Только для чтения (встроенный профиль): правят его наследника.</summary>
    public bool ReadOnly { get; set; }
    public List<PropertyDef> Properties { get; set; } = new();
    public List<FormulaDef> Formulas { get; set; } = new();
    public List<ParameterDef> Parameters { get; set; } = new();
    public List<RuleDef> Rules { get; set; } = new();
    public List<TestCaseDef> Tests { get; set; } = new();
    /// <summary>Формулы, убранные из базового профиля (свойство без формулы считается ошибкой — так видно, что её нет).</summary>
    public List<string> Removed { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CalcProfile FromJson(string json) => JsonSerializer.Deserialize<CalcProfile>(json, Json) ?? throw new InvalidDataException("Пустой профиль");
    public void Save(string path) => File.WriteAllText(path, ToJson(), Encoding.UTF8);
    public static CalcProfile Load(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));

    public CalcProfile Clone() => FromJson(ToJson());

    /// <summary>SHA-256 содержимого (для записи в проект рядом с копией профиля).</summary>
    public string Hash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())))[..16].ToLowerInvariant();

    /// <summary>
    /// Профиль со всеми унаследованными определениями: базовые по цепочке <see cref="Base"/>, поверх них свои.
    /// <paramref name="resolveBase"/> находит профиль по имени; цикл наследования — исключение.
    /// </summary>
    public CalcProfile Flatten(Func<string, CalcProfile?> resolveBase)
    {
        var chain = new List<CalcProfile> { this };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Name };
        for (var p = this; p.Base is { Length: > 0 } b;)
        {
            if (!seen.Add(b)) throw new InvalidDataException($"Цикл наследования профилей: {string.Join(" → ", chain.Select(c => c.Name))} → {b}");
            p = resolveBase(b) ?? throw new InvalidDataException($"Не найден базовый профиль «{b}»");
            chain.Add(p);
        }
        chain.Reverse();
        var flat = new CalcProfile { Name = Name, Version = Version, Base = Base, Description = Description, ReadOnly = ReadOnly };
        var props = new Dictionary<string, PropertyDef>(StringComparer.Ordinal);
        var formulas = new Dictionary<string, List<FormulaDef>>(StringComparer.Ordinal);
        var parameters = new Dictionary<string, ParameterDef>(StringComparer.Ordinal);
        var tests = new Dictionary<string, TestCaseDef>(StringComparer.Ordinal);
        foreach (var p in chain)
        {
            foreach (var r in p.Removed) formulas.Remove(r);
            foreach (var d in p.Properties) props[d.Id] = d.Clone();
            // формулы свойства заменяются целиком набором вариантов наследника
            foreach (var g in p.Formulas.GroupBy(f => f.Property)) formulas[g.Key] = g.Select(f => f.Clone()).ToList();
            foreach (var d in p.Parameters) parameters[d.Id] = d.Clone();
            foreach (var t in p.Tests) tests[t.Name] = t;
            flat.Rules.AddRange(p.Rules);
        }
        flat.Properties = props.Values.ToList();
        flat.Formulas = formulas.Values.SelectMany(f => f).ToList();
        flat.Parameters = parameters.Values.ToList();
        flat.Tests = tests.Values.ToList();
        return flat;
    }

    /// <summary>Профиль-наследник для правки встроенного: пустой, с базой <paramref name="baseProfile"/>.</summary>
    public static CalcProfile DeriveFrom(CalcProfile baseProfile, string name) =>
        new() { Name = name, Base = baseProfile.Name, Version = "1", Description = $"На основе «{baseProfile.Name}»" };
}

/// <summary>Профили, найденные по имени: встроенные и загруженные из папки настроек.</summary>
public sealed class CalcProfileLibrary
{
    private readonly Dictionary<string, CalcProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<CalcProfile> Profiles => _profiles.Values;
    public void Add(CalcProfile p) => _profiles[p.Name] = p;
    public CalcProfile? Find(string name) => _profiles.GetValueOrDefault(name);

    /// <summary>Загружает все <c>*.bdcalc</c> папки; испорченные файлы пропускаются (их имена — в результате).</summary>
    public List<string> LoadFolder(string folder)
    {
        var bad = new List<string>();
        if (!Directory.Exists(folder)) return bad;
        foreach (var f in Directory.EnumerateFiles(folder, "*" + CalcProfile.FileExtension))
        {
            try
            {
                var p = CalcProfile.Load(f);
                if (!string.IsNullOrWhiteSpace(p.Name) && Find(p.Name)?.ReadOnly != true) Add(p);
            }
            catch (Exception) { bad.Add(f); }
        }
        return bad;
    }
}
