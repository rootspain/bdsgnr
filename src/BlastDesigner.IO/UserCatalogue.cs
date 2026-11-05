using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>
/// The catalogue records of the user, in a file next to the settings: what was created, changed or deleted in Available
/// resources, for every project on this computer. A project keeps its own copy, which goes first; a deletion here counts
/// in the projects too (<see cref="ProductCatalogue.Layered"/>).
/// </summary>
public sealed class UserCatalogue
{
    public static string FilePath => Path.Combine(ProgramSettings.AppDataFolder, "catalog.json");

    public ProductCatalogue Tables { get; set; } = new();
    public List<Product> Products { get; set; } = new();

    /// <summary>The records of the file; a missing or broken file is an empty catalogue.</summary>
    public static UserCatalogue Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<UserCatalogue>(File.ReadAllText(path), ProjectJson.Options) ?? new();
        }
        catch (JsonException)
        {
            // A broken file falls back to the catalogue of the project and the program.
        }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ProjectJson.Indented));
    }

    /// <summary>Takes the records that differ between two states of a project: new, edited, deleted or restored ones.</summary>
    public void Remember(ProductCatalogue tablesBefore, IReadOnlyList<Product> productsBefore, ProductCatalogue tables, IReadOnlyList<Product> products)
    {
        Take(Tables.Types, tablesBefore.Types, tables.Types, r => r.Id);
        Take(Tables.Kinds, tablesBefore.Kinds, tables.Kinds, r => r.Id);
        Take(Tables.Groups, tablesBefore.Groups, tables.Groups, r => r.Id);
        Take(Tables.Families, tablesBefore.Families, tables.Families, r => r.Id);
        Take(Products, productsBefore, products, r => r.Id);
    }

    private static void Take<T>(List<T> mine, IReadOnlyList<T> before, IReadOnlyList<T> now, Func<T, string> id)
    {
        var old = before.ToDictionary(id, r => JsonSerializer.Serialize(r, ProjectJson.Options));
        foreach (var r in now)
        {
            if (old.TryGetValue(id(r), out var was) && was == JsonSerializer.Serialize(r, ProjectJson.Options)) continue;
            var copy = ProjectJson.DeepClone(r);
            var at = mine.FindIndex(x => id(x) == id(r));
            if (at >= 0) mine[at] = copy;
            else mine.Add(copy);
        }
    }
}
