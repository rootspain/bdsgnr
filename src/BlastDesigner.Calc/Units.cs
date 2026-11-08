using System.Globalization;

namespace BlastDesigner.Calc;

/// <summary>
/// Единица измерения формулы: размерность (показатели степени длины, массы, времени и денег) и множитель к базовой
/// единице (м, кг, с, валюта). Неизвестная единица (<see cref="Unknown"/>) совместима с любой: так ведут себя
/// эмпирические зависимости, у которых показатель степени не целое число или не константа.
/// </summary>
public sealed class Unit : IEquatable<Unit>
{
    private const int N = 4;
    private static readonly string[] BaseSymbols = { "m", "kg", "s", "$" };
    private readonly double[] _dims;

    private Unit(string symbol, double scale, double[] dims, bool unknown = false)
    {
        Symbol = symbol;
        Scale = scale;
        _dims = dims;
        IsUnknown = unknown;
    }

    /// <summary>Как записана единица ("kg/m3"); для производных — собранная из размерности.</summary>
    public string Symbol { get; }
    /// <summary>Множитель к базовой единице той же размерности: мм — 0.001.</summary>
    public double Scale { get; }
    public bool IsUnknown { get; }
    public bool IsDimensionless => !IsUnknown && _dims.All(d => Math.Abs(d) < 1e-9);

    public static readonly Unit One = new("1", 1, new double[N]);
    public static readonly Unit Unknown = new("?", 1, new double[N], true);

    public bool SameDimension(Unit other) =>
        IsUnknown || other.IsUnknown || _dims.Zip(other._dims).All(p => Math.Abs(p.First - p.Second) < 1e-9);

    public Unit Multiply(Unit o) => IsUnknown || o.IsUnknown ? Unknown : Make(Scale * o.Scale, _dims.Zip(o._dims, (a, b) => a + b).ToArray(), Join(Symbol, "*", o.Symbol));
    public Unit Divide(Unit o) => IsUnknown || o.IsUnknown ? Unknown : Make(Scale / o.Scale, _dims.Zip(o._dims, (a, b) => a - b).ToArray(), Join(Symbol, "/", o.Symbol));
    public Unit Pow(double n) => IsUnknown ? Unknown : Make(Math.Pow(Scale, n), _dims.Select(d => d * n).ToArray(), IsDimensionless ? Symbol : $"({Symbol})^{n.ToString(CultureInfo.InvariantCulture)}");

    private static string Join(string a, string op, string b) => a == "1" ? (op == "*" ? b : "1/" + b) : b == "1" ? a : a + op + b;

    private static Unit Make(double scale, double[] dims, string symbol)
    {
        var u = new Unit(symbol, scale, dims);
        // знакомое имя для частых сочетаний, иначе собранное из операндов
        if (Known != null)
            foreach (var (s, k) in Known)
                if (k.SameDimension(u) && Math.Abs(k.Scale - scale) <= 1e-12 * Math.Abs(scale)) return k;
        return u;
    }

    /// <summary>Множитель перевода значения из этой единицы в <paramref name="to"/>.</summary>
    public double FactorTo(Unit to) => Scale / to.Scale;

    public override string ToString() => Symbol;
    public bool Equals(Unit? other) => other != null && IsUnknown == other.IsUnknown && SameDimension(other) && Math.Abs(Scale - other.Scale) <= 1e-12 * Math.Abs(Scale);
    public override bool Equals(object? obj) => Equals(obj as Unit);
    public override int GetHashCode() => Symbol.GetHashCode();

    /// <summary>Размерность словами в базовых единицах: "kg·m^-3".</summary>
    public string DimensionText => IsUnknown ? "?" : IsDimensionless ? "1" :
        string.Join("·", _dims.Select((d, i) => (d, i)).Where(x => Math.Abs(x.d) > 1e-9)
            .Select(x => Math.Abs(x.d - 1) < 1e-9 ? BaseSymbols[x.i] : BaseSymbols[x.i] + "^" + x.d.ToString("0.###", CultureInfo.InvariantCulture)));

    private static Unit B(string s, double scale, double l = 0, double m = 0, double t = 0, double c = 0) => new(s, scale, new[] { l, m, t, c });

    private static readonly (string, Unit)[] Atoms =
    {
        ("m", B("m", 1, l: 1)), ("mm", B("mm", 1e-3, l: 1)), ("cm", B("cm", 1e-2, l: 1)), ("km", B("km", 1e3, l: 1)),
        ("kg", B("kg", 1, m: 1)), ("g", B("g", 1e-3, m: 1)), ("t", B("t", 1e3, m: 1)),
        ("s", B("s", 1, t: 1)), ("ms", B("ms", 1e-3, t: 1)), ("min", B("min", 60, t: 1)), ("h", B("h", 3600, t: 1)),
        ("$", B("$", 1, c: 1)),
        ("J", B("J", 1, l: 2, m: 1, t: -2)), ("kJ", B("kJ", 1e3, l: 2, m: 1, t: -2)), ("MJ", B("MJ", 1e6, l: 2, m: 1, t: -2)),
        ("Pa", B("Pa", 1, l: -1, m: 1, t: -2)), ("kPa", B("kPa", 1e3, l: -1, m: 1, t: -2)), ("MPa", B("MPa", 1e6, l: -1, m: 1, t: -2)), ("GPa", B("GPa", 1e9, l: -1, m: 1, t: -2)),
        ("deg", B("deg", 1)), ("rad", B("rad", 1)), ("%", B("%", 0.01)), ("dB", B("dB", 1)), ("ea", B("ea", 1)), ("1", One),
    };

    /// <summary>Готовые составные единицы, по которым производные получают знакомое имя.</summary>
    private static readonly (string, Unit)[] Known = new[]
    {
        "m2", "m3", "cm3", "kg/m3", "t/m3", "g/cm3", "kg/t", "kg/m", "MJ/kg", "MJ/t", "mm/s", "m/s", "ms/m", "$/kg", "$/m", "$/t", "MJ"
    }.Select(s => (s, ParseComposite(s, s))).ToArray();

    private static readonly Dictionary<string, Unit> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Разбирает запись единицы: множители через * или ·, делители через /, степень цифрой или ^ ("kg/m3", "m^2", "MJ/t").
    /// Пустая строка или "1" — безразмерная, "?" — неизвестная. Неизвестный символ — исключение <see cref="FormatException"/>.
    /// </summary>
    public static Unit Parse(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s == "1" || s == "-") return One;
        if (s == "?") return Unknown;
        lock (Cache)
        {
            if (Cache.TryGetValue(s, out var u)) return u;
            u = ParseComposite(s, s);
            Cache[s] = u;
            return u;
        }
    }

    public static bool TryParse(string? text, out Unit unit)
    {
        try { unit = Parse(text); return true; }
        catch (FormatException) { unit = Unknown; return false; }
    }

    private static Unit ParseComposite(string s, string symbol)
    {
        var parts = s.Split('/');
        var u = ParseProduct(parts[0]);
        foreach (var d in parts.Skip(1)) u = u.Divide(ParseProduct(d));
        return new Unit(symbol, u.Scale, u._dims);
    }

    private static Unit ParseProduct(string s)
    {
        var u = new Unit("1", 1, new double[N]);
        foreach (var raw in s.Split('*', '·', ' '))
        {
            var f = raw.Trim();
            if (f.Length == 0 || f == "1") continue;
            var exp = 1.0;
            var caret = f.IndexOf('^');
            if (caret > 0)
            {
                exp = double.Parse(f[(caret + 1)..], CultureInfo.InvariantCulture);
                f = f[..caret];
            }
            else
            {
                var i = f.Length;
                while (i > 0 && (char.IsDigit(f[i - 1]) || f[i - 1] == '²' || f[i - 1] == '³')) i--;
                if (i < f.Length && i > 0)
                {
                    var tail = f[i..].Replace("²", "2").Replace("³", "3");
                    exp = int.Parse(tail, CultureInfo.InvariantCulture);
                    f = f[..i];
                }
            }
            var atom = Atoms.FirstOrDefault(a => a.Item1 == f).Item2 ?? throw new FormatException($"Неизвестная единица «{f}»");
            var p = new Unit(atom.Symbol, Math.Pow(atom.Scale, exp), atom._dims.Select(d => d * exp).ToArray());
            u = new Unit("1", u.Scale * p.Scale, u._dims.Zip(p._dims, (a, b) => a + b).ToArray());
        }
        return u;
    }
}
