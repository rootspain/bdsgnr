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
        // new ids up front, so a pasted initiation point goes on the pasted copy of its hole; one without it is not pasted
        var newIds = entities.ToDictionary(e => e.Id, _ => Guid.NewGuid());
        entities.RemoveAll(e => e is InitiationPoint { HoleId: Guid hid } && !newIds.ContainsKey(hid));
        Change("Paste", p =>
        {
            foreach (var e in entities)
            {
                e.Id = newIds[e.Id];
                if (e is InitiationPoint { HoleId: Guid hole } ip) ip.HoleId = newIds[hole];
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

    /// <summary>More points than this in the selection and the lines show no grips: they would only cover the lines, and cost every frame.</summary>
    private const int MaxGrips = 5000;

    /// <summary>The selected lines whose vertices can be dragged.</summary>
    public List<PolylineEntity> EditableLines()
    {
        var lines = new List<PolylineEntity>();
        if (Doc is not { Selection.Count: > 0 } doc) return lines;
        var points = 0;
        foreach (var line in doc.Project.Entities.OfType<PolylineEntity>())
        {
            if (!doc.Selection.Contains(line.Id) || !IsSelectable(line) || doc.Project.FindLayer(line.LayerId) is { Locked: true }) continue;
            points += line.Points.Count;
            if (points > MaxGrips) return new List<PolylineEntity>();
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The selected two-segment fronts whose circles can be dragged (turned, reshaped).</summary>
    public List<TimingLineEntity> EditableFrontLines()
    {
        if (Doc is not { Selection.Count: > 0 } doc) return new List<TimingLineEntity>();
        return doc.Project.Entities.OfType<TimingLineEntity>()
            .Where(l => l.HasHandles && doc.Selection.Contains(l.Id) && IsSelectable(l) && doc.Project.FindLayer(l.LayerId) is not { Locked: true })
            .ToList();
    }

    public void MoveVertex(Guid line, int vertex, Vec3 to) => Change("Move vertex", p =>
    {
        if (p.FindEntity(line) is PolylineEntity l && vertex < l.Points.Count) l.Points[vertex] = to;
    });

    public void AddVertex(Guid line, int at, Vec3 point) => Change("Add vertex", p =>
    {
        if (p.FindEntity(line) is PolylineEntity l && at <= l.Points.Count) l.Points.Insert(at, point);
    });

    /// <summary>Takes a vertex out of a line; a line keeps two points and a polygon three.</summary>
    public void DeleteVertex(Guid line, int vertex)
    {
        if (Project!.FindEntity(line) is not PolylineEntity shown || vertex >= shown.Points.Count) return;
        var least = shown.Closed ? 3 : 2;
        if (shown.Points.Count <= least) { SetStatus(Loc.F("At least {0} points are needed", least)); return; }
        Change("Delete vertex", p => { if (p.FindEntity(line) is PolylineEntity l) l.Points.RemoveAt(vertex); });
    }

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
        var template = first.EditCopy();
        var grid = new PropertyGrid { Exclude = new() { "Id", "LayerId", "GroupId", "Decks", "BoreTrack", "RowId", "FromId", "ToId", "Triangles", "Vertices", "Points", "KindName" } };
        AddProductChoices(grid);
        FormLayouts.Apply(grid, first.GetType());
        grid.SetTargets(template, sel);
        var layerBox = new Avalonia.Controls.ComboBox { ItemsSource = Project!.Layers.Select(l => l.Name).ToList(), SelectedIndex = Project.Layers.FindIndex(l => l.Id == first.LayerId), MinHeight = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var panel = new Avalonia.Controls.StackPanel { Spacing = 12 };
        panel.Children.Add(FormKit.Labeled(Loc.T("Layer"), layerBox));
        panel.Children.Add(new Avalonia.Controls.ScrollViewer { Content = grid, MaxHeight = 560, Padding = new Avalonia.Thickness(0, 0, 10, 0) });
        var subtitle = sel.Count > 1 ? Loc.F("{0} objects: only the fields you change are applied to all of them.", sel.Count) : null;
        if (!await Dialogs.Custom(Loc.F("Edit {0}", Loc.T(first.KindName)), panel, width: 620, subtitle: subtitle, form: true)) return;
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
        // a deleted product is offered no more, but what uses it goes on showing it
        var used = Project.Products.Any(p => p.Deleted) ? ProductCatalogue.UsedProducts(Project) : new HashSet<string>();
        List<(string, string)> Of(Func<Product, bool> pick) => Project.Products.Where(p => pick(p) && (!p.Deleted || used.Contains(p.Id)))
            .Select(p => (p.Id, p.Deleted ? $"{p.Name} ({Loc.Ui("deleted")})" : p.Name)).ToList();
        grid.Choices["ProductId"] = Of(_ => true);
        grid.Choices["StemmingProductId"] = Of(p => p.Kind == ProductKind.Stemming);
        grid.Choices["BoosterId"] = Of(p => p.Kind == ProductKind.Booster);
        grid.Choices["DetonatorId"] = Of(p => p.IsDetonator);
        var explosives = Of(p => p.IsExplosive);
        grid.Choices["WetAlternateProductId"] = explosives;
        grid.Choices["HardAlternateProductId"] = explosives;
        grid.Choices["SoftAlternateProductId"] = explosives;
        // The list of surface kinds shows the colour each kind is drawn with (Program settings ▸ Viewport).
        grid.EnumColors[typeof(SurfaceKind)] = k => Settings.SurfaceColor(k.ToString()!);
    }

    // ---- project settings dialogs ------------------------------------------------------------

    public Task EditProjectProperties() => ProjectPropertiesWindow.Show(this, "Blast properties");
    public Task EditBlastProperties() => ProjectPropertiesWindow.Show(this, "Blast properties");
    public Task EditDesignData() => ProjectPropertiesWindow.Show(this, "Design data");
    public Task EditOutcomes() => ProjectPropertiesWindow.Show(this, "Blast outcomes");
    public Task EditSummaryBlock() => ProjectPropertiesWindow.Show(this, "Summary block");
    public Task EditCostSchema() => ProjectPropertiesWindow.Show(this, "Resource cost schema");
    public async void EditElectronicSettings() => await ProjectPropertiesWindow.Show(this, "Electronic timing preferences");
    public Task EditVibrationModel() => ProjectPropertiesWindow.Show(this, "Vibration site constants");
    public Task EditCalculations() => CalcEditorWindow.Show(this);
    public Task EditHoleTypes() => ProjectPropertiesWindow.Show(this, "Hole and material types");

    public async Task EditResources()
    {
        if (Doc == null) return;
        if (await ProductsWindow.Show(Project!) is not var (products, tables)) return;
        tables.Accept(products);
        var (tablesBefore, productsBefore) = (Project!.Catalogue, Project.Products);
        Change("Available resources", p =>
        {
            p.Products = products;
            p.Catalogue = tables;
            ProductCatalogue.Link(p);
        });
        // what was added, edited, deleted or restored goes to the user's records too, for the other projects
        var mine = UserCatalogue.Load();
        mine.Remember(tablesBefore, productsBefore, Project!.Catalogue, Project.Products);
        mine.Save();
    }

    public Task EditProgramSettings() => EditProgramSettings(0);

    private async Task EditProgramSettings(double scroll)
    {
        double? reopenAt = null;
        await SettingsWindow.Show(Settings, ApplyProgramSettings, s => PreviewBackground = s?.BackgroundColor, scroll, y => reopenAt = y);
        // every label is taken when its control is made: a new language needs the window built again, once the settings
        // are closed; on Apply they open again over the new window, where they were
        if (Settings.Language == Loc.Language) return;
        var next = Window.Rebuild();
        if (reopenAt is double y) await next.ViewModel.EditProgramSettings(y);
    }

    /// <summary>Takes the settings edited in the settings window (Apply and OK) and saves them.</summary>
    private void ApplyProgramSettings(ProgramSettings edited)
    {
        var s = Settings;
        s.Language = edited.Language;
        s.Theme = edited.Theme;
        s.AccentColor = edited.AccentColor;
        s.BackgroundColor = edited.BackgroundColor;
        s.GeometryColor = edited.GeometryColor;
        s.SectionLineColor = edited.SectionLineColor;
        s.SurfaceColoring = edited.SurfaceColoring;
        s.SurfaceWireframe = edited.SurfaceWireframe;
        // a copy: the window keeps editing its own after Apply, and Cancel must not leave those edits here
        s.SurfaceColors = new Dictionary<string, uint>(edited.SurfaceColors);
        s.SelectionColor = edited.SelectionColor;
        s.CollarSizePixels = edited.CollarSizePixels;
        s.ShowCoordinatesBox = edited.ShowCoordinatesBox;
        s.UsageLogging = edited.UsageLogging;
        if (s.UsageLogging) IO.UsageLog.Start(IO.Changelog.Current.ToString());
        else IO.UsageLog.Stop();
        s.SnapToObjects = edited.SnapToObjects;
        s.TargetRadiusPixels = edited.TargetRadiusPixels;
        s.ShowFps = edited.ShowFps;
        s.AutosaveMinutes = edited.AutosaveMinutes;
        s.BackupCopies = edited.BackupCopies;
        s.MaxRecentFiles = edited.MaxRecentFiles;
        s.ShowStartupAssistant = edited.ShowStartupAssistant;
        s.Author = edited.Author;
        s.Save();
        Look.ApplyTheme(s.IsLight, s.AccentColor);
        View.SnapEnabled = s.SnapToObjects;
        View.TargetRadius = s.TargetRadiusPixels;
        View.ShowFps = s.ShowFps;
        View.SurfaceWireframe = s.SurfaceWireframe;
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
