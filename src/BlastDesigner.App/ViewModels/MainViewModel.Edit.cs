using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using BlastDesigner.App.Services;
using BlastDesigner.App.Views;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;

namespace BlastDesigner.App.ViewModels;

public sealed partial class MainViewModel
{
    private const string ClipboardPrefix = "BLASTDESIGNER-ENTITIES:";

    public void Undo() { Doc?.Undo(); SetStatus(Loc.T("Undo")); }
    public void UndoSteps(int n) { Doc?.Undo(n); }
    public void Redo() { Doc?.Redo(); SetStatus(Loc.T("Redo")); }

    private IClipboard? Clipboard => Window.Clipboard;

    public async void Copy()
    {
        if (Doc == null || Clipboard == null) return;
        var json = ProjectJson.SerializeEntities(Doc.SelectedEntities.Where(e => e is not TieEntity));
        await Clipboard.SetTextAsync(ClipboardPrefix + json);
        SetStatus(Loc.F("{0} objects copied", Doc.Selection.Count));
    }

    public void Cut()
    {
        Copy();
        DeleteSelected();
    }

    public async void Paste()
    {
        if (Doc == null || Clipboard == null) return;
        var text = await Clipboard.GetTextAsync();
        if (text == null || !text.StartsWith(ClipboardPrefix)) return;
        var entities = ProjectJson.DeserializeEntities(text[ClipboardPrefix.Length..]);
        // Paste with a small offset so copies are visible next to the originals.
        var offset = new Vec3(Project!.Design.Spacing, -Project.Design.Burden, 0);
        var ids = new List<Guid>();
        Change("Paste", p =>
        {
            foreach (var e in entities)
            {
                e.Id = Guid.NewGuid();
                e.Translate(offset);
                if (p.FindLayer(e.LayerId) == null) e.LayerId = p.DefaultLayer.Id;
                if (e is Hole h) { h.Label = NextHoleLabel(); h.RowId = null; }
                p.Entities.Add(e);
                ids.Add(e.Id);
            }
        });
        // Part of the paste step: undoing the paste also brings back the earlier selection.
        Doc.Select(ids, record: false);
    }

    public async void CopyImage()
    {
        if (Doc == null || Clipboard == null) return;
        var png = Reports.RenderPng(Project!, View.Camera, (int)Math.Max(200, View.Bounds.Width), (int)Math.Max(200, View.Bounds.Height), BuildSceneOptions());
        var path = Path.Combine(Path.GetTempPath(), $"blast_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            var data = new DataObject();
            data.Set("PNG", png);
            data.Set(DataFormats.Files, new[] { path });
            await Clipboard.SetDataObjectAsync(data);
        }
        catch { /* clipboard image support differs per platform; the file path below is the fallback */ }
        SetStatus(Loc.F("Image copied (also saved to {0})", path));
    }

    public async void DeleteSelected()
    {
        if (Doc == null || Doc.Selection.Count == 0) return;
        var ids = Doc.Selection.ToHashSet();
        var locked = Doc.SelectedEntities.Count(e => e is Hole { Locked: true } || Project!.FindLayer(e.LayerId) is { Locked: true });
        if (locked > 0 && !await Dialogs.Confirm(Loc.F("{0} selected objects are locked and will be kept. Delete the rest?", locked))) return;
        Change("Delete", p =>
        {
            p.Entities.RemoveAll(e => ids.Contains(e.Id) && !(e is Hole { Locked: true }) && p.FindLayer(e.LayerId) is not { Locked: true });
            var remaining = p.Entities.Select(e => e.Id).ToHashSet();
            p.Entities.RemoveAll(e => e is TieEntity t && (!remaining.Contains(t.FromId) || !remaining.Contains(t.ToId)));
            foreach (var r in p.Rows) r.HoleIds.RemoveAll(id => !remaining.Contains(id));
            p.Rows.RemoveAll(r => r.HoleIds.Count == 0);
        });
        Doc.ClearSelection(record: false);
    }

    // ---- selection ------------------------------------------------------------------

    /// <summary>Objects that can be selected: on a visible, unfrozen layer and drawn under the current display switches.</summary>
    public bool IsSelectable(Entity e) =>
        Project!.FindLayer(e.LayerId) is { Visible: true, Frozen: false } && !HiddenEntities.Contains(e.Id) && Rendering.PlanRenderer.EntityShown(Project.View, e);

    public void SelectAll() => Doc?.Select(Project!.Entities.Where(IsSelectable).Select(e => e.Id));

    /// <summary>Select like: same kind and layer (and hole type for holes) as the current selection.</summary>
    public void SelectLike()
    {
        if (Doc == null) return;
        var sel = Doc.SelectedEntities.ToList();
        var keys = sel.Select(e => (e.GetType(), e.LayerId, (e as Hole)?.HoleType)).ToHashSet();
        Doc.Select(Project!.Entities.Where(e => IsSelectable(e) && keys.Contains((e.GetType(), e.LayerId, (e as Hole)?.HoleType))).Select(e => e.Id));
    }

    /// <summary>Select exact: holes with the same diameter, length, angle and charge as the selection.</summary>
    public void SelectExact()
    {
        if (Doc == null) return;
        var sel = Doc.SelectedHoles.ToList();
        if (sel.Count == 0) { SelectLike(); return; }
        string Key(Hole h) => $"{h.HoleType}|{h.Diameter:0.#}|{h.EffectiveLength:0.0}|{h.Angle:0}|{Charging.ExplosiveMass(Project!, h):0}";
        var keys = sel.Select(Key).ToHashSet();
        Doc.Select(Project!.Holes.Where(h => IsSelectable(h) && keys.Contains(Key(h))).Select(h => h.Id));
    }

    public void InvertSelection()
    {
        if (Doc == null) return;
        var cur = Doc.Selection.ToHashSet();
        Doc.Select(Project!.Entities.Where(e => !cur.Contains(e.Id) && IsSelectable(e)).Select(e => e.Id));
    }

    public void GroupSelected()
    {
        var gid = Guid.NewGuid();
        var ids = Doc!.Selection.ToHashSet();
        Change("Group", p => { foreach (var e in p.Entities.Where(e => ids.Contains(e.Id))) e.GroupId = gid; });
    }

    public void UngroupSelected()
    {
        var ids = Doc!.Selection.ToHashSet();
        Change("Ungroup", p => { foreach (var e in p.Entities.Where(e => ids.Contains(e.Id))) e.GroupId = null; });
    }

    // ---- edit selected ------------------------------------------------------------------

    /// <summary>Opens the property editor for the selection. Several holes are edited together; only changed fields are applied.</summary>
    public async void EditSelected()
    {
        if (Doc == null) return;
        var sel = Doc.SelectedEntities.ToList();
        if (sel.Count == 0) return;
        var first = sel[0];
        var sameType = sel.All(e => e.GetType() == first.GetType());
        if (!sameType)
        {
            await Dialogs.Info(Loc.T("Select objects of one kind to edit them together."));
            return;
        }
        if (first is Hole)
        {
            await EditHoles(sel.Cast<Hole>().ToList());
            return;
        }
        if (first is TimingLineEntity line && sel.Count == 1)
        {
            await EditTimingLine(line);
            return;
        }
        var template = ProjectJson.DeepClone(first);
        var grid = new PropertyGrid { Exclude = new() { "Id", "LayerId", "GroupId", "Decks", "BoreTrack", "RowId", "FromId", "ToId", "Triangles", "Vertices", "Points" } };
        AddProductChoices(grid);
        grid.SetTargets(template, sel);
        var layerBox = new Avalonia.Controls.ComboBox { ItemsSource = Project!.Layers.Select(l => l.Name).ToList(), SelectedIndex = Project.Layers.FindIndex(l => l.Id == first.LayerId), MinWidth = 200 };
        var panel = new Avalonia.Controls.StackPanel { Spacing = 6 };
        if (sel.Count > 1) panel.Children.Add(new Avalonia.Controls.TextBlock { Text = Loc.F("{0} objects: only the fields you change are applied to all of them.", sel.Count) });
        panel.Children.Add(new Avalonia.Controls.TextBlock { Text = Loc.T("Layer") });
        panel.Children.Add(layerBox);
        panel.Children.Add(new Avalonia.Controls.ScrollViewer { Content = grid, MaxHeight = 560 });
        if (!await Dialogs.Custom(Loc.F("Edit {0}", Loc.T(first.KindName)), panel)) return;
        var changed = grid.Changed.ToList();
        var newLayer = layerBox.SelectedIndex >= 0 ? Project.Layers[layerBox.SelectedIndex].Id : first.LayerId;
        var ids = sel.Select(e => e.Id).ToHashSet();
        Change("Edit selected", p =>
        {
            foreach (var e in p.Entities.Where(e => ids.Contains(e.Id)))
            {
                grid.ApplyTo(template, e);
                if (newLayer != first.LayerId || sel.Count == 1) e.LayerId = newLayer;
                if (e is Hole h && (changed.Contains(nameof(Hole.Length)) || changed.Contains(nameof(Hole.ActualLength))) && !h.ManualLoading && h.Decks.Count > 0)
                    Charging.ApplyRules(p, new[] { h });
                if (e is SurfaceEntity s) s.InvalidateIndex();
            }
        });
    }

    /// <summary>
    /// Hole form for one or several holes. Field edits, the layer and a column edited from the form go in as one undo step;
    /// the column is adapted to each hole's length, as with Loading on several holes.
    /// </summary>
    public async Task EditHoles(List<Hole> holes)
    {
        var edit = await HoleEditWindow.Show(this, holes);
        if (edit == null) return;
        var ids = holes.Select(h => h.Id).ToHashSet();
        var templateId = holes[0].Id;
        Change(holes.Count > 1 ? "Edit selected" : "Edit hole", p =>
        {
            foreach (var h in HolesById(p, ids))
            {
                foreach (var apply in edit.Fields.Values) apply(h);
                if (edit.LayerId is Guid layer) h.LayerId = layer;
                if (edit.Decks is { } decks && !h.IsDummy)
                {
                    h.Decks = h.Id == templateId ? decks.Select(d => d.Clone()).ToList() : Charging.AdaptColumn(p, decks, edit.DecksLength, h.EffectiveLength);
                    h.ManualLoading = true;
                    if (!edit.StateChanged && h.Decks.Count > 0 && h.State < HoleState.Loaded) h.State = HoleState.Loaded;
                }
                else if (edit.LengthChanged && !h.ManualLoading && h.Decks.Count > 0)
                    Charging.ApplyRules(p, new[] { h });
            }
        });
    }

    public void AddProductChoices(PropertyGrid grid)
    {
        if (Project == null) return;
        var all = Project.Products.Select(p => (p.Id, p.Name)).ToList();
        grid.Choices["ProductId"] = all;
        grid.Choices["StemmingProductId"] = Project.Products.Where(p => p.Kind == ProductKind.Stemming).Select(p => (p.Id, p.Name)).ToList();
        grid.Choices["BoosterId"] = Project.Products.Where(p => p.Kind == ProductKind.Booster).Select(p => (p.Id, p.Name)).ToList();
        grid.Choices["DetonatorId"] = Project.Products.Where(p => p.IsDetonator).Select(p => (p.Id, p.Name)).ToList();
        var explosives = Project.Products.Where(p => p.IsExplosive).Select(p => (p.Id, p.Name)).ToList();
        grid.Choices["WetAlternateProductId"] = explosives;
        grid.Choices["HardAlternateProductId"] = explosives;
        grid.Choices["SoftAlternateProductId"] = explosives;
        // The list of surface kinds shows the colour each kind is drawn with (Program settings ▸ Viewport).
        grid.EnumColors[typeof(SurfaceKind)] = k => Settings.SurfaceColor(k.ToString()!);
    }

    // ---- project settings dialogs ------------------------------------------------------------

    private async Task EditPart<T>(string title, Func<BlastProject, T> get, Action<BlastProject, T> set, params string[] exclude) where T : class
    {
        if (Doc == null) return;
        var edited = await Dialogs.EditObject(title, get(Project!), exclude);
        if (edited != null) Change(title, p => set(p, edited));
    }

    public Task EditBlastProperties() => EditPart("Blast properties", p => p.Header, (p, v) => p.Header = v);
    public Task EditDesignData() => EditPart("Design data", p => p.Design, (p, v) => { v.HoleTypes = p.Design.HoleTypes; v.MaterialTypes = p.Design.MaterialTypes; p.Design = v; });
    public Task EditOutcomes() => EditPart("Blast outcomes", p => p.Outcomes, (p, v) => p.Outcomes = v);
    public Task EditSummaryBlock() => EditPart("Summary block", p => p.Summary, (p, v) => p.Summary = v);
    public Task EditCostSchema() => EditPart("Resource cost schema", p => p.Costs, (p, v) => p.Costs = v);
    public async void EditElectronicSettings() => await EditPart("Electronic timing preferences", p => p.Electronic, (p, v) => p.Electronic = v);
    public Task EditVibrationModel() => EditPart("Vibration site constants", p => p.Vibration, (p, v) => p.Vibration = v);

    public async Task EditHoleTypes()
    {
        if (Doc == null) return;
        var r = await TypeTableWindow.Show("Hole and material types", Project!.Design.HoleTypes, Project.Design.MaterialTypes);
        if (r != null) Change("Hole and material types", p => { p.Design.HoleTypes = r.Value.Holes; p.Design.MaterialTypes = r.Value.Materials; });
    }

    public async Task EditResources()
    {
        if (Doc == null) return;
        var list = await ProductsWindow.Show(Project!);
        if (list != null) Change("Available resources", p => p.Products = list);
    }

    public async Task EditVisualStyle()
    {
        var edited = await Dialogs.EditObject("Visual style", new VisualStyle
        {
            BackgroundColor = Settings.BackgroundColor,
            SelectionColor = Settings.SelectionColor,
            CollarSizePixels = Settings.CollarSizePixels
        });
        if (edited == null) return;
        Settings.BackgroundColor = edited.BackgroundColor;
        Settings.SelectionColor = edited.SelectionColor;
        Settings.CollarSizePixels = edited.CollarSizePixels;
        Settings.Save();
        View.Redraw();
    }

    public async Task EditProgramSettings()
    {
        var edited = await SettingsWindow.Show(Settings);
        if (edited == null) return;
        var s = Settings;
        s.Language = edited.Language;
        s.Theme = edited.Theme;
        s.AccentColor = edited.AccentColor;
        s.BackgroundColor = edited.BackgroundColor;
        s.GeometryColor = edited.GeometryColor;
        s.SurfaceColoring = edited.SurfaceColoring;
        s.SurfaceColors = edited.SurfaceColors;
        s.SelectionColor = edited.SelectionColor;
        s.CollarSizePixels = edited.CollarSizePixels;
        s.ShowCoordinatesBox = edited.ShowCoordinatesBox;
        s.UsageLogging = edited.UsageLogging;
        if (s.UsageLogging) IO.UsageLog.Start(IO.Changelog.Current.ToString());
        else IO.UsageLog.Stop();
        s.SnapToObjects = edited.SnapToObjects;
        s.TargetRadiusPixels = edited.TargetRadiusPixels;
        s.AutosaveMinutes = edited.AutosaveMinutes;
        s.BackupCopies = edited.BackupCopies;
        s.MaxRecentFiles = edited.MaxRecentFiles;
        s.ShowStartupAssistant = edited.ShowStartupAssistant;
        s.Author = edited.Author;
        s.Save();
        Look.ApplyTheme(s.IsLight, s.AccentColor);
        View.SnapEnabled = s.SnapToObjects;
        View.TargetRadius = s.TargetRadiusPixels;
        RefreshAll();
    }

    public async Task ManageTemplates()
    {
        var names = Templates.List().ToList();
        if (names.Count == 0) { await Dialogs.Info(Loc.T("There are no templates yet. Use File ▸ Save as template.")); return; }
        var idx = await Dialogs.Choose("Templates", Loc.T("Choose a template to delete"), names);
        if (idx is int i && await Dialogs.Confirm(Loc.F("Delete template {0}?", names[i]))) Templates.Delete(names[i]);
    }

    public async void TransformCoordinates()
    {
        if (Doc == null) return;
        var t = await Dialogs.EditObject("Transform coordinates", new TransformParameters());
        if (t == null) return;
        var ids = Doc.Selection.ToHashSet();
        var sel = Doc.SelectedEntities.ToList();
        var pivot = t.PivotAtCentre ? GeoMath.Centroid(sel.SelectMany(e => e.GetVertices()).ToList()) : new Vec3(t.PivotX, t.PivotY, 0);
        var f = GeneralOps.MakeTransform(new Vec3(t.MoveX, t.MoveY, t.MoveZ), t.RotateDegrees, pivot, t.Scale);
        var timed = 0;
        Change("Transform coordinates", p => timed = ReprogramTimingLines(p, ids, () =>
        {
            foreach (var e in p.Entities.Where(e => ids.Contains(e.Id)))
            {
                e.Transform(f);
            }
        }));
        ReportReprogrammed(timed);
    }
}

/// <summary>Editable display preferences (Edit ▸ Visual style).</summary>
public sealed class VisualStyle
{
    public uint BackgroundColor { get; set; }
    public uint SelectionColor { get; set; }
    public double CollarSizePixels { get; set; }
}

public sealed class TransformParameters
{
    public double MoveX { get; set; }
    public double MoveY { get; set; }
    public double MoveZ { get; set; }
    public double RotateDegrees { get; set; }
    public bool PivotAtCentre { get; set; } = true;
    public double PivotX { get; set; }
    public double PivotY { get; set; }
    public double Scale { get; set; } = 1;
}
