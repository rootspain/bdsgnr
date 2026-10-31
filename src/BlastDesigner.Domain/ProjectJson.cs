using System.Text.Json;
using System.Text.Json.Serialization;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Domain;

/// <summary>Shared JSON settings for the project format, undo snapshots and clipboard.</summary>
public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = Create(false);
    public static readonly JsonSerializerOptions Indented = Create(true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            // Explicit resolver: trimmed and single-file builds switch the reflection default off, which would make every
            // save, load, settings file and undo snapshot fail. The app's assemblies are kept whole by the trimmer (App.csproj).
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static byte[] Serialize(BlastProject p) => JsonSerializer.SerializeToUtf8Bytes(p, Options);
    public static BlastProject Deserialize(ReadOnlySpan<byte> data) =>
        JsonSerializer.Deserialize<BlastProject>(data, Options) ?? throw new InvalidDataException("Empty project");

    public static T DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Options), Options)!;

    public static string SerializeEntities(IEnumerable<Entity> entities) =>
        JsonSerializer.Serialize(entities.ToList(), Options);

    public static List<Entity> DeserializeEntities(string json) =>
        JsonSerializer.Deserialize<List<Entity>>(json, Options) ?? new();
}
