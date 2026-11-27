using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Holes numbered one after another from a counter (the Renumber tool): each takes the prefix typed in, or keeps its own
/// (all of its label before the number it ends with, the name of its row), and the number of the counter. A number another
/// hole has is swapped with it: that hole takes the label the numbered one had.
/// </summary>
public static class HoleNumbering
{
    /// <summary>All of a label before the number it ends with: "B12-A7" → "B12-A", "57" → "", "A" → "A".</summary>
    public static string PrefixOf(string label)
    {
        var i = label.Length;
        while (i > 0 && char.IsDigit(label[i - 1])) i--;
        return label[..i];
    }

    /// <summary>The label a hole takes: <paramref name="prefix"/> when there is one, else its own, then the number.</summary>
    public static string LabelFor(string current, string? prefix, int number) =>
        (string.IsNullOrEmpty(prefix) ? PrefixOf(current) : prefix) + number;

    /// <summary>
    /// The holes numbered in the order given from <paramref name="start"/>, one each. A label another hole has goes to the
    /// numbered hole and that hole takes the one it had. The swaps made, as (the label given, the label taken in exchange).
    /// </summary>
    public static List<(string Given, string Back)> Apply(BlastProject p, IReadOnlyList<Guid> ids, string? prefix, int start)
    {
        var swaps = new List<(string, string)>();
        var n = start;
        foreach (var id in ids)
        {
            if (p.FindEntity(id) is not Hole h) continue;
            var now = LabelFor(h.Label, prefix, n++);
            if (now == h.Label) continue;
            if (p.Holes.FirstOrDefault(x => x.Id != h.Id && x.Label == now) is { } holder)
            {
                holder.Label = h.Label;
                swaps.Add((now, h.Label));
            }
            h.Label = now;
        }
        return swaps;
    }
}
