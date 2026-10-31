using System.Globalization;
using System.Text.Json;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;

namespace BlastDesigner.IO;

/// <summary>A class given to a segment by hand; the line is named by its geometry (<see cref="SurveyLabels.Key"/>), not its id.</summary>
public sealed record SurveyLabel(string Line, int From, int To, SurveyLineClass Class);

/// <summary>
/// Hand-checked segment classes of a survey drawing, saved next to the drawing: the reference the recognition is
/// measured against. Ids change with every import, so a line is named by its first and last vertex (plan, to the
/// millimetre, hashed) and its vertex count — the file holds no coordinates.
/// </summary>
public static class SurveyLabels
{
    public static string Key(PolylineEntity line)
    {
        static string R(double v) => Math.Round(v, 3).ToString("0.000", CultureInfo.InvariantCulture);
        var text = $"{R(line.Points[0].X)},{R(line.Points[0].Y)},{R(line.Points[^1].X)},{R(line.Points[^1].Y)},{line.Points.Count}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 12);
    }

    public static void Save(string path, IEnumerable<SurveyLabel> labels) =>
        File.WriteAllText(path, JsonSerializer.Serialize(labels.ToList(), new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));

    public static List<SurveyLabel> Load(string path) =>
        JsonSerializer.Deserialize<List<SurveyLabel>>(File.ReadAllText(path), new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) ?? new();

    /// <summary>Labels for the segments of a recognition, keyed by line geometry.</summary>
    public static IEnumerable<SurveyLabel> From(SurveyRecognition r, IReadOnlyDictionary<Guid, PolylineEntity> lines) =>
        r.Segments.Where(s => lines.ContainsKey(s.Segment.LineId)).Select(s => new SurveyLabel(Key(lines[s.Segment.LineId]), s.Segment.From, s.Segment.To, s.Class));
}
