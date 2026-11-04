using System.Text;
using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly (string, string[]) ProjectFilter = ("Blast Designer project", new[] { "*" + ProjectFile.Extension });

    /// <summary>Asks to save a modified project. Returns false when the user cancels.</summary>
    public async Task<bool> CheckSave()
    {
        if (Doc is not { IsModified: true }) return true;
        var r = await Dialogs.YesNoCancel(Loc.T("You have not saved your data. Do you want to save?"));
        if (r == null) return false;
        if (r == true) return await Save();
        return true;
    }

    public Task New() => New(false);

    /// <summary>File ▸ New; <paramref name="preferTemplate"/> preselects a template when none was used last (startup ▸ From template).</summary>
    public async Task New(bool preferTemplate)
    {
        if (!await CheckSave()) return;
        var templates = Templates.List().ToList();
        // a new blast is usually fired soon: its shot date starts at today
        var header = new ProjectHeader { Name = Loc.T("New blast"), Author = Settings.Author, ShotDate = DateTime.Today };
        var selected = Math.Max(preferTemplate && templates.Count > 0 ? 1 : 0, templates.IndexOf(Settings.LastTemplate) + 1);
        if (await NewBlastWindow.Show(header, templates, selected) is not { } setup) return;

        BlastProject project;
        if (setup.Template is { } name)
        {
            try { project = Templates.Load(name); }
            catch (Exception ex)
            {
                await Dialogs.Info(Loc.T("Unable to read template file - creating empty blast file") + "\n" + ex.Message);
                project = BlastProject.CreateNew();
            }
            Settings.LastTemplate = name;
        }
        else project = BlastProject.CreateNew();
        header.Created = DateTime.Now;
        project.Header = header;
        Settings.Author = header.Author;
        Detach();
        Attach(new Document(project));
        SetStatus(Loc.T("Created new file"));
        Window.Ribbon.Select(0); // a new blast starts on the File tab
    }

    /// <summary>Creates an empty project without asking (startup).</summary>
    public void NewSilent()
    {
        var p = BlastProject.CreateNew(Loc.T("New blast"));
        p.Header.Author = Settings.Author;
        p.Header.ShotDate = DateTime.Today;
        Attach(new Document(p));
        Window.Ribbon.Select(0);
    }

    public async Task Open()
    {
        if (!await CheckSave()) return;
        var path = await Dialogs.OpenFile("Open blast", ProjectFilter);
        if (path != null) await OpenPath(path);
    }

    public async Task OpenPath(string path)
    {
        try
        {
            var project = ProjectFile.Load(path);
            var doc = new Document(project, path);
            try
            {
                using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (Exception)
            {
                doc.IsReadOnly = true;
                await Dialogs.Info(Loc.T("The file is read only or locked by another user. It is opened read only."));
            }
            Detach();
            Attach(doc);
            Settings.AddRecent(path);
            Settings.Save();
            SetStatus(Loc.F("Opened file {0}", path));
            Log.Info($"Opened {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Open failed: {path}", ex);
            await Dialogs.Info(Loc.T("Unable to open file. The file is either not a Blast Designer file or is corrupt.") + "\n" + ex.Message);
        }
    }

    public async Task<bool> Save()
    {
        if (Doc == null) return false;
        if (Doc.FilePath == null || Doc.IsReadOnly || !Doc.FilePath.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) return await SaveAs();
        return SaveTo(Doc.FilePath);
    }

    public async Task<bool> SaveAs()
    {
        if (Doc == null) return false;
        var path = await Dialogs.SaveFile("Save blast as", SafeName(Doc.Project.Header.Name) + ProjectFile.Extension, ProjectFile.Extension, ProjectFilter);
        if (path == null) return false;
        path = Path.ChangeExtension(path, ProjectFile.Extension);
        if (!SaveTo(path)) return false;
        Doc.IsReadOnly = false;
        Settings.AddRecent(path);
        Settings.Save();
        RefreshAll();
        return true;
    }

    private bool SaveTo(string path)
    {
        var doc = Doc!;
        try
        {
            View.Camera.SaveTo(doc.Project.View);
            byte[]? thumb = null;
            try { thumb = Reports.RenderPng(doc.Project, View.Camera, 320, 240, new SceneOptions { DrawSummaryBlock = false }); } catch { }
            var oldPath = doc.FilePath;
            ProjectFile.Save(doc.Project, path, Settings.BackupCopies, thumb);
            Recovery.Clear(oldPath, doc.Project.Id);
            doc.FilePath = path;
            doc.MarkSaved();
            SetStatus(Loc.T("File saved"));
            Log.Info($"Saved {path}");
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(ex);
            return false;
        }
    }

    public async Task SaveAsTemplate()
    {
        if (Doc == null) return;
        var name = await Dialogs.Prompt("Save as template", Loc.T("Template name"), Doc.Project.Header.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Templates.Save(Doc.Project, name);
        SetStatus(Loc.F("Template {0} saved", name));
    }

    public async Task Close()
    {
        if (!await CheckSave()) return;
        Detach();
        SetStatus(Loc.T("File closed"));
    }

    public async Task Merge()
    {
        if (Doc == null) return;
        var path = await Dialogs.OpenFile("Merge blast", ProjectFilter);
        if (path == null) return;
        var other = ProjectFile.Load(path);
        int n = 0;
        Change("Merge file", p => n = ProjectFile.Merge(p, other));
        View.ZoomAll();
        var dups = Project!.Holes.GroupBy(h => h.Label, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        if (dups > 0 && await Dialogs.Confirm(Loc.F("{0} hole numbers are duplicated after the merge. Re-label duplicates?", dups)))
            Change("Re-label duplicate holes", p => HoleDesign.RelabelDuplicates(p.Holes));
        SetStatus(Loc.F("{0} objects merged", n));
    }

    public async Task OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            await Dialogs.Info(Loc.F("File not found: {0}", path));
            Settings.RecentFiles.Remove(path);
            return;
        }
        if (!await CheckSave()) return;
        await OpenPath(path);
    }

    public async Task Exit()
    {
        if (!await CheckSave()) return;
        if (Doc != null) Recovery.Clear(Doc.FilePath, Doc.Project.Id);
        Settings.Save();
        Window.ForceClose();
    }

    /// <summary>Offers to restore recovery files left by a crash.</summary>
    public async Task CheckRecovery()
    {
        var pending = Recovery.Pending().ToList();
        foreach (var f in pending)
        {
            if (await Dialogs.Confirm(Loc.F("A recovery file from {0:g} was found ({1}). Restore it?", f.LastWriteTime, f.Name)))
            {
                try
                {
                    using var fs = f.OpenRead();
                    var p = ProjectFile.Read(fs);
                    var doc = new Document(p);
                    Detach();
                    Attach(doc);
                    doc.MarkModified();
                }
                catch (Exception ex) { Dialogs.ShowError(ex); }
                fs_delete(f);
                return;
            }
            fs_delete(f);
        }
        static void fs_delete(FileInfo f) { try { f.Delete(); } catch { } }
    }

    public async Task DropFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ProjectFile.Extension:
                    if (await CheckSave()) await OpenPath(path);
                    break;
                case ".dxf" or ".dwg":
                    await ImportCad(path);
                    break;
                case ".csv" or ".txt":
                    await ImportText(path);
                    break;
                case ".str":
                    await ImportStr(path, false);
                    break;
                case ".obj":
                    await ImportObj(path);
                    break;
            }
        }
    }

    // ---- import ---------------------------------------------------------------------

    private bool EnsureProject()
    {
        if (Doc == null) NewSilent();
        return true;
    }

    public async Task ImportText(string? path = null)
    {
        EnsureProject();
        path ??= await Dialogs.OpenFile("Import text file", ("Text files", new[] { "*.csv", "*.txt", "*.prn" }));
        if (path == null) return;
        var lines = await File.ReadAllLinesAsync(path);
        var fmt = await TextImportWindow.Show(lines, Path.GetFileName(path));
        if (fmt == null) return;
        var result = TextFiles.Import(lines, fmt, Project!.Design, Project.Holes);
        AddImported(Loc.T("Import text file"), result.Entities.Select(e => (Path.GetFileNameWithoutExtension(path), e)), fmt.Kind == ImportDataKind.Holes);
        Settings.LastImportFolder = Path.GetDirectoryName(path) ?? "";
        var msg = Loc.F("{0} objects imported, {1} skipped", result.Entities.Count, result.Skipped);
        if (result.Warnings.Count > 0) msg += "\n" + string.Join("\n", result.Warnings.Take(10));
        await Dialogs.Info(msg);
    }

    public async Task ImportCad(string? path = null)
    {
        EnsureProject();
        path ??= await Dialogs.OpenFile("Import DXF/DWG", ("CAD files", new[] { "*.dxf", "*.dwg" }));
        if (path == null) return;
        var opt = await CadImportWindow.Show(path);
        if (opt == null) return;
        var drawing = CadFiles.ReadCad(path, opt);
        AddImported(Loc.T("Import DXF"), drawing.Items, false, drawing.Layers);
        var status = Loc.F("{0} objects imported", drawing.Items.Count);
        if (drawing.SkippedHidden > 0) status += "; " + Loc.F("{0} on hidden layers skipped", drawing.SkippedHidden);
        if (drawing.FlatLines > 0) status += "; " + Loc.F("{0} flat lines moved to 2D layers", drawing.FlatLines);
        SetStatus(status);
    }

    public async Task ImportStr(string? path, bool ignoreDiameters)
    {
        EnsureProject();
        path ??= await Dialogs.OpenFile("Import string file", ("String files", new[] { "*.str" }));
        if (path == null) return;
        var items = CadFiles.ImportStr(path, ignoreDiameters);
        AddImported(Loc.T("Import string file"), items.Select(e => (Path.GetFileNameWithoutExtension(path), e)), false);
    }

    public async Task ImportObj(string? path = null)
    {
        EnsureProject();
        path ??= await Dialogs.OpenFile("Import OBJ", ("Wavefront OBJ", new[] { "*.obj" }));
        if (path == null) return;
        var s = CadFiles.ImportObj(path);
        AddImported(Loc.T("Import OBJ"), new[] { (StandardLayers.Surfaces, (Entity)s) }, false);
    }

    public async Task ImportDrillPlan()
    {
        EnsureProject();
        var path = await Dialogs.OpenFile("Import drill plan", ("Drill plan XML", new[] { "*.xml" }));
        if (path == null) return;
        var holes = CadFiles.ImportDrillPlan(path, Project!.Design);
        AddImported(Loc.T("Import drill plan"), holes.Select(h => (StandardLayers.Holes, (Entity)h)), true);
    }

    /// <param name="layers">Colour and visibility of the source layers (drawings); applied to layers this import creates.</param>
    private void AddImported(string undoName, IEnumerable<(string Layer, Entity Entity)> items, bool holesToHoleLayer, IReadOnlyDictionary<string, CadLayerInfo>? layers = null)
    {
        var list = items.ToList();
        Change(undoName, p =>
        {
            foreach (var (layerName, e) in list)
            {
                var existed = p.Layers.Any(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                var layer = e is Hole && holesToHoleLayer ? p.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design")
                    : e is SurfaceEntity ? p.GetOrCreateLayer(layerName == StandardLayers.Surfaces ? layerName : layerName, 0xFF64748B, "Imported")
                    : p.GetOrCreateLayer(layerName, 0xFF334155, "Imported");
                if (!existed && layer.Name == layerName && layers != null && layers.TryGetValue(layerName, out var info))
                {
                    if (info.Color is uint c) layer.Color = c;
                    layer.Visible = info.Visible;
                }
                p.Add(e, layer);
            }
            if (list.Any(x => x.Entity is Hole) && p.Rows.Count == 0)
                p.Rows.AddRange(HoleDesign.AutoRows(p.Holes, p.Header.BlastOrientation, p.Design.Burden / 2));
        });
        View.ZoomAll();
    }

    // ---- export ---------------------------------------------------------------------

    private IEnumerable<Entity> ExportSet() => Doc!.Selection.Count > 0 ? Doc.SelectedEntities : Project!.Entities;
    private IEnumerable<Hole> ExportHolesSet() => Doc!.SelectedHoles.Any() ? Doc.SelectedHoles : Project!.Holes;

    private async Task ExportText(string title, string defaultName, string ext, string content, string filterName)
    {
        var path = await Dialogs.SaveFile(title, defaultName + ext, ext, (filterName, new[] { "*" + ext }));
        if (path == null) return;
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));
        SetStatus(Loc.F("Exported to {0}", path));
    }

    private string BaseName => SafeName(Project?.Header.Name ?? "blast");
    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public async Task ExportHolesText()
    {
        var kind = await Dialogs.Choose("Export text file", Loc.T("What to export"), new[] { Loc.T("Holes table"), Loc.T("Points and lines (X,Y,Z)") });
        if (kind == null) return;
        var content = kind == 0 ? TextFiles.ExportHoles(Project!, ExportHolesSet()) : TextFiles.ExportPoints(ExportSet().Where(e => e is not Hole));
        await ExportText("Export text file", BaseName, ".csv", content, "CSV");
    }

    public Task ExportDxf() => ExportText("Export DXF", BaseName, ".dxf", CadFiles.ExportDxf(Project!, ExportSet()), "DXF");
    public Task ExportStr() => ExportText("Export string file", BaseName, ".str", CadFiles.ExportStr(ExportSet(), Project!.Header.Name), "String file");
    public Task ExportQuantities() => ExportText("Export quantities", BaseName + "_quantities", ".csv", TextFiles.ExportQuantities(Quantities.Compute(Project!)), "CSV");
    public Task ExportDrillPlan() => ExportText("Export drill plan", BaseName + "_drillplan", ".xml", CadFiles.ExportDrillPlan(Project!, ExportHolesSet()), "XML");
    public Task ExportFiring(bool cosines) => ExportText("Export firing data", BaseName + "_firing", ".csv", TextFiles.ExportFiringData(Project!, ExportHolesSet(), cosines), "CSV");
    public Task ExportDeckTable() => ExportText("Export deck table", BaseName + "_decks", ".csv", TextFiles.ExportDeckTable(Project!, ExportHolesSet()), "CSV");
    public Task ExportLoadSheet() => ExportText("Export load sheet", BaseName + "_loadsheet", ".csv", TextFiles.ExportLoadSheet(Project!, ExportHolesSet()), "CSV");

    public async Task ExportObj()
    {
        var s = Doc!.SelectedEntities.OfType<SurfaceEntity>().FirstOrDefault() ?? Project!.Entities.OfType<SurfaceEntity>().FirstOrDefault();
        if (s == null) { await Dialogs.Info(Loc.T("Select a surface first.")); return; }
        await ExportText("Export OBJ", SafeName(s.Name), ".obj", CadFiles.ExportObj(s), "OBJ");
    }

    // ---- print ------------------------------------------------------------------------

    public async Task PrintReports()
    {
        if (Doc == null) return;
        var opt = await ReportDesignerWindow.Show();
        if (opt == null) return;
        var path = await Dialogs.SaveFile("Save report", BaseName + "_report.pdf", ".pdf", ("PDF", new[] { "*.pdf" }));
        if (path == null) return;
        await using (var fs = File.Create(path)) Reports.WritePdf(Project!, fs, opt);
        Timing.Solve(Project!);
        await OfferOpen(path);
    }

    public async Task PrintPlan()
    {
        if (Doc == null) return;
        var choice = await Dialogs.Choose("Print plan", Loc.T("Scale"), new[] { Loc.T("Fit to page"), "1:250", "1:500", "1:1000", "1:2000" });
        if (choice == null) return;
        double? scale = choice switch { 1 => 250, 2 => 500, 3 => 1000, 4 => 2000, _ => null };
        var landscape = await Dialogs.Confirm(Loc.T("Landscape orientation?"));
        var path = await Dialogs.SaveFile("Save plan", BaseName + "_plan.pdf", ".pdf", ("PDF", new[] { "*.pdf" }));
        if (path == null) return;
        await using (var fs = File.Create(path)) Reports.WritePlanPdf(Project!, fs, scale, landscape, Project!.Header.Name);
        await OfferOpen(path);
    }

    private async Task OfferOpen(string path)
    {
        if (await Dialogs.Confirm(Loc.F("Saved {0}. Open it now?", path))) OpenExternal(path);
    }

    public static void OpenExternal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", $"\"{path}\"");
            else System.Diagnostics.Process.Start("xdg-open", $"\"{path}\"");
        }
        catch (Exception ex) { Log.Error("Open external failed", ex); }
    }
}
