using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>
/// Fragmentation inputs saved on their own (*.bdfrag, JSON): the rock factor, rock mass, drilling accuracy and size
/// thresholds of a site, to load into another blast. Version 2 also keeps the target fragmentation
/// (<see cref="FragmentationModel.Target"/>); a file of version 1 reads as before, without a target.
/// </summary>
public static class FragmentationPreset
{
    public const string Extension = ".bdfrag";
    public const string Format = "BlastDesigner.FragmentationPreset";
    /// <summary>The version written: 2 since the target fragmentation.</summary>
    public const int CurrentVersion = 2;

    private sealed class File
    {
        public string? Format { get; set; }
        public int Version { get; set; } = 1;
        public FragmentationModel Inputs { get; set; } = new();
    }

    public static void Save(string path, FragmentationModel inputs) =>
        System.IO.File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new File { Format = Format, Version = CurrentVersion, Inputs = inputs }, ProjectJson.Options));

    /// <summary>The inputs of a preset file; throws <see cref="InvalidDataException"/> for a file that is not one.</summary>
    public static FragmentationModel Load(string path)
    {
        File? f;
        try { f = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllBytes(path), ProjectJson.Options); }
        catch (JsonException e) { throw new InvalidDataException("Not a fragmentation preset file.", e); }
        if (f?.Format != Format) throw new InvalidDataException("Not a fragmentation preset file.");
        return f.Inputs;
    }
}
