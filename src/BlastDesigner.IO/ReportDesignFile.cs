using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>The design of a report saved on its own (*.bdreport, JSON): the choices of the report and the plan, the frame, the stamps and the visibility.</summary>
public static class ReportDesignFile
{
    public const string Extension = ".bdreport";
    public const string Format = "BlastDesigner.ReportDesign";

    private sealed class File
    {
        public string? Format { get; set; }
        public int Version { get; set; } = 1;
        public ReportDesign Design { get; set; } = new();
    }

    public static void Save(string path, ReportDesign design) =>
        SafeFile.WriteAllText(path, JsonSerializer.Serialize(new File { Format = Format, Design = design }, ProjectJson.Indented));

    /// <summary>The design of a file; throws <see cref="InvalidDataException"/> for a file that is not one.</summary>
    public static ReportDesign Load(string path)
    {
        File? f;
        try { f = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllBytes(path), ProjectJson.Options); }
        catch (JsonException e) { throw new InvalidDataException("Not a report design file.", e); }
        if (f?.Format != Format) throw new InvalidDataException("Not a report design file.");
        return f.Design;
    }
}
