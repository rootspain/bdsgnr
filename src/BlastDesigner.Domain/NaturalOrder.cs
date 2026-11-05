using System.Globalization;

namespace BlastDesigner.Domain;

/// <summary>
/// Order of text as people read it: numbers by value (2 before 10, 1,5 before 10), the numbers inside text too
/// (A2 before A10), the rest without regard to case.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        if (Number(a) is double x && Number(b) is double y && x != y) return x.CompareTo(y);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var p = Digits(a, ref i);
                var q = Digits(b, ref j);
                // without leading zeros the longer number is the greater
                var order = p.Length != q.Length ? p.Length.CompareTo(q.Length) : string.CompareOrdinal(p, q);
                if (order != 0) return order;
                continue;
            }
            var chars = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (chars != 0) return chars;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static double? Number(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Digits(string s, ref int at)
    {
        var start = at;
        while (at < s.Length && char.IsAsciiDigit(s[at])) at++;
        return s[start..at].TrimStart('0');
    }
}
