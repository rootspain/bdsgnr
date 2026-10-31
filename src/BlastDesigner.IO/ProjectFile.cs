using System.IO.Compression;
using System.Text.Json;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>
/// Blast Designer project file (*.bdp): a ZIP archive with
/// <c>manifest.json</c>, <c>project.json</c>, an optional <c>thumbnail.png</c> and <c>attachments/&lt;id&gt;</c> entries.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".bdp";
    public const string TemplateExtension = ".bdt";
    public const string FormatId = "blast-designer-project";

    public sealed record Manifest(string Format, int SchemaVersion, string Application, DateTime Saved, string Name);

    public static void Save(BlastProject project, string path, int backupCopies = 3, byte[]? thumbnailPng = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var fs = File.Create(tmp))
            Write(project, fs, thumbnailPng);
        if (File.Exists(path) && backupCopies > 0) RotateBackups(path, backupCopies);
        File.Move(tmp, path, true);
    }

    public static void Write(BlastProject project, Stream stream, byte[]? thumbnailPng = null)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, true);
        var manifest = new Manifest(FormatId, BlastProject.CurrentSchemaVersion, "Blast Designer", DateTime.Now, project.Header.Name);
        WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJson.Indented));
        WriteEntry(zip, "project.json", JsonSerializer.SerializeToUtf8Bytes(project, ProjectJson.Indented));
        if (thumbnailPng != null) WriteEntry(zip, "thumbnail.png", thumbnailPng);
        foreach (var a in project.Attachments)
            WriteEntry(zip, $"attachments/{a.Id:N}", a.Data);
    }

    public static BlastProject Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static BlastProject Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a Blast Designer project file (manifest missing).");
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(manifestEntry), ProjectJson.Options);
        if (manifest?.Format != FormatId) throw new InvalidDataException("Not a Blast Designer project file.");
        if (manifest.SchemaVersion > BlastProject.CurrentSchemaVersion)
            throw new InvalidDataException($"The file was saved by a newer version (schema {manifest.SchemaVersion}).");
        var projectEntry = zip.GetEntry("project.json") ?? throw new InvalidDataException("Project data missing.");
        var project = ProjectJson.Deserialize(ReadEntry(projectEntry));
        Migrate(project);
        foreach (var a in project.Attachments)
            if (zip.GetEntry($"attachments/{a.Id:N}") is { } e) a.Data = ReadEntry(e);
        return project;
    }

    public static byte[]? ReadThumbnail(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("thumbnail.png") is { } e ? ReadEntry(e) : null;
        }
        catch { return null; }
    }

    /// <summary>Upgrades older schema versions in place. Version 1 is the first released schema.</summary>
    private static void Migrate(BlastProject p)
    {
        if (p.Layers.Count == 0) p.DefaultLayerId = p.GetOrCreateLayer("Default").Id;
        foreach (var e in p.Entities)
            if (p.FindLayer(e.LayerId) == null) e.LayerId = p.DefaultLayer.Id;
        p.SchemaVersion = BlastProject.CurrentSchemaVersion;
    }

    /// <summary>Shifts existing backups: file.bdp → file.bdp.001 → .002 … up to <paramref name="copies"/>.</summary>
    public static void RotateBackups(string path, int copies)
    {
        for (int i = copies; i >= 1; i--)
        {
            var src = i == 1 ? path : $"{path}.{i - 1:000}";
            var dst = $"{path}.{i:000}";
            if (!File.Exists(src)) continue;
            if (i == copies && File.Exists(dst)) File.Delete(dst);
            if (i == 1) File.Copy(src, dst, true);
            else File.Move(src, dst, true);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(data);
    }

    private static byte[] ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Adds every entity, layer, product and row of another project (File ▸ Merge). Returns the number of entities merged.</summary>
    public static int Merge(BlastProject target, BlastProject source)
    {
        var layerMap = new Dictionary<Guid, Guid>();
        foreach (var l in source.Layers)
        {
            var existing = target.Layers.FirstOrDefault(x => string.Equals(x.Name, l.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null) { target.Layers.Add(l); existing = l; }
            layerMap[l.Id] = existing.Id;
        }
        var productMap = new Dictionary<Guid, Guid>();
        foreach (var p in source.Products)
        {
            var existing = target.Products.FirstOrDefault(x => x.Id == p.Id) ?? target.Products.FirstOrDefault(x => x.Name == p.Name && x.Kind == p.Kind);
            if (existing == null) { target.Products.Add(p); existing = p; }
            productMap[p.Id] = existing.Id;
        }
        Guid? MapP(Guid? id) => id is Guid g && productMap.TryGetValue(g, out var m) ? m : id;

        var ids = target.Entities.Select(e => e.Id).ToHashSet();
        var idMap = new Dictionary<Guid, Guid>();
        foreach (var e in source.Entities)
        {
            if (ids.Contains(e.Id)) { var n = Guid.NewGuid(); idMap[e.Id] = n; e.Id = n; }
            e.LayerId = layerMap.GetValueOrDefault(e.LayerId, target.DefaultLayer.Id);
            if (e is Hole h)
                foreach (var d in h.Decks)
                {
                    d.ProductId = MapP(d.ProductId);
                    foreach (var pr in d.Primers) { pr.BoosterId = MapP(pr.BoosterId); pr.DetonatorId = MapP(pr.DetonatorId); }
                }
            if (e is TieEntity t) t.ProductId = MapP(t.ProductId);
        }
        foreach (var t in source.Entities.OfType<TieEntity>())
        {
            if (idMap.TryGetValue(t.FromId, out var f)) t.FromId = f;
            if (idMap.TryGetValue(t.ToId, out var to)) t.ToId = to;
        }
        foreach (var r in source.Rows)
        {
            r.HoleIds = r.HoleIds.Select(id => idMap.GetValueOrDefault(id, id)).ToList();
            target.Rows.Add(r);
        }
        target.Entities.AddRange(source.Entities);
        foreach (var a in source.Attachments) target.Attachments.Add(a);
        return source.Entities.Count;
    }
}

/// <summary>Project templates kept in the user's data folder.</summary>
public sealed class TemplateStore
{
    public TemplateStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
    }

    public string Folder { get; }

    public IEnumerable<string> List() =>
        Directory.EnumerateFiles(Folder, "*" + ProjectFile.TemplateExtension).Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x);

    public void Save(BlastProject project, string name)
    {
        var copy = ProjectJson.DeepClone(project);
        // A template keeps settings, catalogue and rules but no design objects.
        copy.Entities.Clear();
        copy.Rows.Clear();
        copy.Attachments.Clear();
        ProjectFile.Save(copy, PathFor(name), 0);
    }

    public BlastProject Load(string name)
    {
        var p = ProjectFile.Load(PathFor(name));
        p.Id = Guid.NewGuid();
        p.Header.Created = DateTime.Now;
        return p;
    }

    public void Delete(string name) => File.Delete(PathFor(name));

    private string PathFor(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Folder, name + ProjectFile.TemplateExtension);
    }
}

/// <summary>Writes crash-recovery copies of the open project to a temp folder.</summary>
public sealed class RecoveryStore
{
    public RecoveryStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
    }

    public string Folder { get; }

    public string PathFor(string? projectPath, Guid projectId) =>
        Path.Combine(Folder, $"{(projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : "untitled")}_{projectId:N}{ProjectFile.Extension}.recovery");

    public void Write(BlastProject project, string? projectPath)
    {
        var path = PathFor(projectPath, project.Id);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) ProjectFile.Write(project, fs);
        File.Move(tmp, path, true);
    }

    public void Clear(string? projectPath, Guid projectId)
    {
        var p = PathFor(projectPath, projectId);
        if (File.Exists(p)) File.Delete(p);
    }

    public IEnumerable<FileInfo> Pending() =>
        new DirectoryInfo(Folder).EnumerateFiles("*.recovery").OrderByDescending(f => f.LastWriteTime);
}
