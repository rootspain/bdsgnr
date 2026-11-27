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
        // a line front copied by an older version is the straight chevron now
        foreach (var l in entities.OfType<TimingLineEntity>()) { l.LineFrontToStraightChevron(); l.PointByPoints(); }
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
                // a surface pasted with all its objects is bound to the pasted ones, otherwise to none
                if (e is SurfaceEntity { SourceIds.Count: > 0 } ps)
                    ps.SourceIds = ps.SourceIds.All(newIds.ContainsKey) ? ps.SourceIds.Select(id => newIds[id]).ToList() : new();
                e.Translate(offset);
                if (p.FindLayer(e.LayerId) == null) e.LayerId = p.DefaultLayer.Id;
                if (e is Hole h)
                {
                    h.Label = NextHoleLabel();
                    h.RowId = null;
                    // a copy of a hole of a pattern stays in its pattern (segment), on no node; pasted into a project
                    // without that pattern, it is a hole of its own
                    if (h.PatternId != null && p.FindPattern(h.PatternId) == null) h.DetachFromPattern();
                }
                p.Entities.Add(e);
                ids.Add(e.Id);
            }
            // a hole pasted with the surface its collar sits on is bound to the pasted surface; to one of this project,
            // to it; to none otherwise (pasted from another project). Then it goes onto it at its new place.
            var pasted = entities.OfType<Hole>().ToList();
            foreach (var h in pasted)
                if (h.CollarSurfaceId is Guid sid)
                    h.CollarSurfaceId = newIds.TryGetValue(sid, out var to) ? to : CollarSurfaces.Find(p, sid) != null ? sid : null;
            CollarSurfaces.SeatAll(p, pasted);
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
        // vertices selected on the lines (a click on a grip): only they go, as with Ctrl and a click on a grip; the lines
        // stay selected
        if (DeleteSelectedVertices()) return;
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

    /// <summary>
    /// Objects that can be selected: on a visible, unfrozen layer, drawn under the current display switches, not in a group
    /// locked on the Visibility tab and, with blocks shown, inside them.
    /// </summary>
    public bool IsSelectable(Entity e) =>
        Project!.FindLayer(e.LayerId) is { Visible: true, Frozen: false } && !HiddenEntities.Contains(e.Id) && Rendering.PlanRenderer.EntityShown(Project.View, e)
        && !ViewKinds.Locked(Project.View, e)
        && (Blocks is not { } blocks || blocks.Shows(e));

    private BlockClip? _blocks;
    private bool _blocksTaken;

    /// <summary>The visible blocks (only what is inside them is shown); null without any. Taken again after every change of the blast or the view.</summary>
    public BlockClip? Blocks
    {
        get
        {
            if (!_blocksTaken && Project != null) { _blocks = BlockClip.Of(Project); _blocksTaken = true; }
            return Project == null ? null : _blocks;
        }
    }

    /// <summary>The blocks are taken again on their next use.</summary>
    private void ForgetBlocks() => _blocksTaken = false;

    /// <summary>The holes shown: with blocks shown, those with the collar inside one; the hole table and its quick filters go by them.</summary>
    public IEnumerable<Hole> ShownHoles(BlastProject p) => Blocks is { } blocks && p == Project ? p.Holes.Where(blocks.Shows) : p.Holes;

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
        // a dimension: its start (0) or its end (1), the other end and the offset kept
        else if (p.FindEntity(line) is DimensionEntity d)
        {
            if (vertex == 0) d.Start = to;
            else if (vertex == 1) d.End = to;
        }
    });

    /// <summary>
    /// The selected objects with grips (<see cref="Tools.VertexGrips"/>): the lines (<see cref="EditableLines"/>) and the
    /// dimensions, whose two ends move on their own. None on a locked layer.
    /// </summary>
    public List<Entity> EditableGripped()
    {
        var result = new List<Entity>(EditableLines());
        if (Doc is not { Selection.Count: > 0 } doc) return result;
        foreach (var d in doc.Project.Entities.OfType<DimensionEntity>())
            if (doc.Selection.Contains(d.Id) && IsSelectable(d) && doc.Project.FindLayer(d.LayerId) is not { Locked: true }) result.Add(d);
        return result;
    }

    /// <summary>
    /// The vertices selected on the selected lines (a click on a grip, with Shift added): their grips are marked, their
    /// fields are in the properties panel, the arrows move them and Delete takes them out. A vertex goes when its line is no
    /// longer selected.
    /// </summary>
    private readonly List<(Guid Line, int Vertex)> _selectedVertices = new();

    /// <summary>The selected vertices whose line is selected and has them still.</summary>
    public List<(PolylineEntity Line, int Vertex)> SelectedLineVertices
    {
        get
        {
            var result = new List<(PolylineEntity, int)>();
            if (Doc is not { } doc) return result;
            foreach (var (id, i) in _selectedVertices)
                if (doc.Selection.Contains(id) && doc.Project.FindEntity(id) is PolylineEntity line && i < line.Points.Count) result.Add((line, i));
            return result;
        }
    }

    /// <summary>The selected vertex when it is the only one.</summary>
    public (PolylineEntity Line, int Vertex)? SelectedLineVertex => SelectedLineVertices is { Count: 1 } one ? one[0] : null;

    /// <summary>The selected vertices of the selected lines that can be edited (not on a locked layer).</summary>
    private List<(PolylineEntity Line, int Vertex)> EditableSelectedVertices()
    {
        var lines = EditableLines().Select(l => l.Id).ToHashSet();
        return SelectedLineVertices.Where(v => lines.Contains(v.Line.Id)).ToList();
    }

    /// <summary>
    /// Selects a vertex of a selected line, in place of the vertices selected before or, with <paramref name="add"/>, with
    /// them: its grip marked, its fields shown in the properties panel.
    /// </summary>
    public void SelectVertex(Guid line, int vertex, bool add = false)
    {
        if (!add) _selectedVertices.Clear();
        if (!_selectedVertices.Contains((line, vertex))) _selectedVertices.Add((line, vertex));
        SelectionRefreshed?.Invoke();
        View.Redraw();
        Window.ShowPropertiesTab();
    }

    /// <summary>
    /// A box or a lasso with Shift (<see cref="SelectionMode.Add"/>) or Ctrl (<see cref="SelectionMode.Toggle"/>) while a
    /// vertex is selected: the vertices of the selected lines inside it are added to the selected ones, with Ctrl taken off
    /// them; the objects selected stay. False when no vertex is selected or the mode is neither, and the box selects objects.
    /// </summary>
    public bool SelectVerticesIn(Lasso shape, SelectionMode mode)
    {
        if (mode is not (SelectionMode.Add or SelectionMode.Toggle) || SelectedLineVertices.Count == 0) return false;
        var cam = View.Camera;
        foreach (var line in EditableLines())
            for (int i = 0; i < line.Points.Count; i++)
            {
                var (x, y) = cam.ToScreen(line.Points[i]);
                if (!shape.Contains(x, y)) continue;
                if (mode == SelectionMode.Add) { if (!_selectedVertices.Contains((line.Id, i))) _selectedVertices.Add((line.Id, i)); }
                else _selectedVertices.Remove((line.Id, i));
            }
        SelectionRefreshed?.Invoke();
        View.Redraw();
        return true;
    }

    /// <summary>No vertex is selected any more.</summary>
    public void ClearSelectedVertex()
    {
        if (_selectedVertices.Count == 0) return;
        _selectedVertices.Clear();
        SelectionRefreshed?.Invoke();
        View.Redraw();
    }

    /// <summary>
    /// The undo steps of the document take the selected vertices with them: an undo of a deletion of vertices gives them
    /// back selected, as the objects of an undone deletion are.
    /// </summary>
    private void KeepVerticesInUndo(Document doc)
    {
        doc.SaveSubSelection = () => _selectedVertices.ToArray();
        doc.RestoreSubSelection = saved =>
        {
            _selectedVertices.Clear();
            if (saved is (Guid, int)[] vertices) _selectedVertices.AddRange(vertices);
        };
    }

    /// <summary>The selected vertices whose line is no longer selected are let go.</summary>
    private void KeepSelectedVerticesOfSelection()
    {
        if (Doc is not { } doc) { _selectedVertices.Clear(); return; }
        _selectedVertices.RemoveAll(v => !doc.Selection.Contains(v.Line));
    }

    /// <summary>Vertices moved to where they go, in one undo step; the rest of their lines stays.</summary>
    public void MoveVertices(IReadOnlyList<(Guid Line, int Vertex, Vec3 To)> moves)
    {
        if (moves.Count == 0) return;
        Change("Move vertex", p =>
        {
            foreach (var (line, vertex, to) in moves)
                if (p.FindEntity(line) is PolylineEntity l && vertex < l.Points.Count) l.Points[vertex] = to;
        });
    }

    /// <summary>
    /// The selected vertices moved by an offset (the arrows): true when there were any to move, and the lines stay.
    /// </summary>
    public bool NudgeSelectedVertices(Vec3 delta)
    {
        var chosen = EditableSelectedVertices();
        if (chosen.Count == 0) return false;
        MoveVertices(chosen.Select(v => (v.Line.Id, v.Vertex, v.Line.Points[v.Vertex] + delta)).ToList());
        return true;
    }

    /// <summary>
    /// The selected vertices taken out of their lines, in one undo step: a line keeps two points and a polygon three, so
    /// a line that would keep fewer keeps its vertices, still selected. True when there were any selected.
    /// </summary>
    private bool DeleteSelectedVertices()
    {
        var chosen = EditableSelectedVertices();
        if (chosen.Count == 0) return false;
        var taken = new Dictionary<Guid, List<int>>();
        var refused = 0;
        foreach (var g in chosen.GroupBy(v => v.Line.Id))
        {
            var line = g.First().Line;
            var least = line.Closed ? 3 : 2;
            var indices = g.Select(v => v.Vertex).Distinct().OrderByDescending(i => i).ToList();
            if (line.Points.Count - indices.Count < least) { refused = least; continue; }
            taken[g.Key] = indices;
        }
        if (taken.Count > 0)
        {
            // the undo step takes them selected (an undo gives them back so), then they go
            Change("Delete vertex", p =>
            {
                foreach (var (id, indices) in taken)
                    if (p.FindEntity(id) is PolylineEntity l)
                        foreach (var i in indices) if (i < l.Points.Count) l.Points.RemoveAt(i);
            });
            _selectedVertices.RemoveAll(v => taken.ContainsKey(v.Line));
            SelectionRefreshed?.Invoke();
            View.Redraw();
        }
        if (refused > 0) SetStatus(Loc.F("At least {0} points are needed", refused));
        return true;
    }

    /// <summary>An end of an open line brought onto its other end: the end goes and the line is closed, one undo step.</summary>
    public void CloseLineAtEnd(Guid line, int end) => Change("Close line", p =>
    {
        if (p.FindEntity(line) is not PolylineEntity { Closed: false } l || l.Points.Count < 4 || (end != 0 && end != l.Points.Count - 1)) return;
        l.Points.RemoveAt(end);
        l.Closed = true;
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

    /// <summary>
    /// Select exact: holes with the same diameter, length, angle and charge as the selected ones; ties of the same product
    /// at the same delay as the selected ones.
    /// </summary>
    public void SelectExact()
    {
        if (Doc == null) return;
        var sel = Doc.SelectedHoles.ToList();
        // ties: the same product at the same delay ("Tie 1" 17 ms is not "Tie 1" 24 ms nor "Tie 2" 17 ms)
        var ties = Doc.Selection.Select(id => Project!.FindEntity(id)).OfType<TieEntity>().ToList();
        if (sel.Count == 0 && ties.Count == 0) { SelectLike(); return; }
        string Key(Hole h) => $"{h.HoleType}|{h.Diameter:0.#}|{h.EffectiveLength:0.0}|{h.Angle:0}|{Charging.ExplosiveMass(Project!, h):0}";
        string TieKey(TieEntity t) => (t.ProductId ?? "") + "|" + Math.Round(Timing.TieDelay(Project!, t)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var keys = sel.Select(Key).ToHashSet();
        var tieKeys = ties.Select(TieKey).ToHashSet();
        Doc.Select(Project!.Holes.Where(h => keys.Count > 0 && IsSelectable(h) && keys.Contains(Key(h))).Select(h => h.Id)
            .Concat(Project.Ties.Where(t => tieKeys.Count > 0 && IsSelectable(t) && tieKeys.Contains(TieKey(t))).Select(t => t.Id)));
    }

    /// <summary>
    /// Properties ▸ With the same warning: the holes marked with a loading warning (the exclamation mark over the collar)
    /// that has a kind the selected holes have. With all the selected holes in one pattern, only the holes of that pattern.
    /// </summary>
    public void SelectSameWarning()
    {
        if (Doc == null) return;
        var sel = Doc.SelectedHoles.ToList();
        if (sel.Count == 0) { SetStatus(Loc.T("Select holes with a warning first.")); return; }
        var kinds = sel.SelectMany(h => Charging.LoadingWarnings(Project!, h)).ToHashSet();
        if (kinds.Count == 0) { SetStatus(Loc.T("The selected holes have no loading warning.")); return; }
        var patterns = sel.Select(h => h.PatternId).Distinct().ToList();
        var pattern = patterns is [Guid one] ? Project!.Patterns.FirstOrDefault(x => x.Id == one) : null;
        var found = Project!.Holes.Where(h => IsSelectable(h) && (pattern == null || h.PatternId == pattern.Id)
            && Charging.LoadingWarnings(Project, h).Overlaps(kinds)).Select(h => h.Id).ToList();
        Doc.Select(found);
        SetStatus(pattern != null ? Loc.F("{0} holes with the same warning in {1}", found.Count, pattern.Name) : Loc.F("{0} holes with the same warning", found.Count));
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
        if (first is EbsOpeningPoint opening && sel.Count == 1)
        {
            await EditOpeningPoint(opening);
            return;
        }
        var template = first.EditCopy();
        var grid = new PropertyGrid { Exclude = new() { "Id", "LayerId", "GroupId", "Decks", "BoreTrack", "RowId", "FromId", "ToId", "Triangles", "Vertices", "Points", "KindName", "SourceIds", "SourceBreaklines" } };
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
                }
                else if (edit.LengthChanged && !h.ManualLoading && h.Decks.Count > 0)
                    Charging.ApplyRules(p, new[] { h });
            }
        });
    }

    /// <summary>
    /// Products offered by the lists of a property grid. With <paramref name="ties"/> (the grid of ties) the products of the
    /// types of the Initiation catalogue that fit the holes of the ties (<see cref="TieFit.Offered"/>), each with the short
    /// name of its type ("Shock tube · Surface connector 17 ms"); what a tie has now stays, marked when it does not fit.
    /// </summary>
    public void AddProductChoices(PropertyGrid grid, IEnumerable<TieEntity>? ties = null)
    {
        if (Project == null) return;
        // a deleted product is offered no more, but what uses it goes on showing it
        var used = Project.Products.Any(p => p.Deleted) ? ProductCatalogue.UsedProducts(Project) : new HashSet<string>();
        List<(string, string)> Of(Func<Product, bool> pick) => Project.Products.Where(p => pick(p) && (!p.Deleted || used.Contains(p.Id)))
            .Select(p => (p.Id, p.Deleted ? $"{p.Name} ({Loc.Ui("deleted")})" : p.Name)).ToList();
        if (ties != null)
        {
            grid.Choices["ProductId"] = TieFit.Offered(Project, ties.ToList(), used).Select(o =>
            {
                var name = o.Type is { } type ? $"{Loc.Short(ProductCatalogue.ShortNameOf(type))} · {o.Product.Name}" : o.Product.Name;
                if (o.Product.Deleted) name += $" ({Loc.Ui("deleted")})";
                if (!o.Fits) name += $" ({Loc.Ui("does not fit")})";
                return (o.Product.Id, name);
            }).ToList();
        }
        else grid.Choices["ProductId"] = Of(_ => true);
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
        await SettingsWindow.Show(Settings, ApplyProgramSettings, s => PreviewBackground = s?.BackgroundColor, scroll, y => reopenAt = y, HotKeyGroups());
        // every label is taken when its control is made: a new language needs the window built again, once the settings
        // are closed; on Apply they open again over the new window, where they were
        if (Settings.Language == Loc.Language) return;
        var next = Window.Rebuild();
        if (reopenAt is double y) await next.ViewModel.EditProgramSettings(y);
    }

    /// <summary>
    /// The language of the formulas and the names of the calculations: that of the interface (ru, kk, fr), or English
    /// (code names) when the settings ask for it or the interface is English.
    /// </summary>
    public static string FormulaLanguage(ProgramSettings s) => s.FormulasInEnglish ? "en" : Loc.Language;

    /// <summary>Takes the settings edited in the settings window (Apply and OK) and saves them.</summary>
    public void ApplyProgramSettings(ProgramSettings edited)
    {
        var s = Settings;
        s.Language = edited.Language;
        s.FormulasInEnglish = edited.FormulasInEnglish;
        CalcService.FormulaLanguage = FormulaLanguage(s);
        s.Theme = edited.Theme;
        s.AccentColor = edited.AccentColor;
        s.BackgroundColor = edited.BackgroundColor;
        s.GeometryColor = edited.GeometryColor;
        s.SectionLineColor = edited.SectionLineColor;
        s.SurfaceColoring = edited.SurfaceColoring;
        s.SurfaceWireframe = edited.SurfaceWireframe;
        s.SurfaceTransparency = edited.SurfaceTransparency;
        s.WireframeTransparency = edited.WireframeTransparency;
        s.PointCloudTransparency = edited.PointCloudTransparency;
        // a copy: the window keeps editing its own after Apply, and Cancel must not leave those edits here
        s.SurfaceColors = new Dictionary<string, uint>(edited.SurfaceColors);
        s.SelectionColor = edited.SelectionColor;
        s.SelectionThick = edited.SelectionThick;
        s.SelectionHaloPx = edited.SelectionHaloPx;
        s.PatternArrowColor = edited.PatternArrowColor;
        s.PatternCollarPolygonColor = edited.PatternCollarPolygonColor;
        s.PatternDepthPolygonColor = edited.PatternDepthPolygonColor;
        s.PatternTurnColor = edited.PatternTurnColor;
        s.PatternTurnRefusedColor = edited.PatternTurnRefusedColor;
        s.CollarSizePixels = edited.CollarSizePixels;
        // the delays switched off: the tie legend comes on, to tell the kinds of the ties; its switch takes it off again
        if (s.TieDelayPlace != ProgramSettings.TieDelayOff && edited.TieDelayPlace == ProgramSettings.TieDelayOff && Project is { View.ShowTieLegend: false })
            ToggleView(v => v.ShowTieLegend = true);
        s.TieDelayPlace = edited.TieDelayPlace;
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
        s.UnitSystem = edited.UnitSystem;
        s.HoleAngleReference = edited.HoleAngleReference;
        s.RibbonFontSize = edited.RibbonFontSize;
        s.RibbonButtonPadding = edited.RibbonButtonPadding;
        s.RibbonButtonGap = edited.RibbonButtonGap;
        s.TabFontSize = edited.TabFontSize;
        s.PanelFontSize = edited.PanelFontSize;
        s.ToolTipFontSize = edited.ToolTipFontSize;
        s.ToolTipBackground = edited.ToolTipBackground;
        s.ToolTipForeground = edited.ToolTipForeground;
        s.ClampFonts();
        s.ApplyUnits();
        s.Save();
        Look.ApplyTheme(s.IsLight, s.AccentColor);
        Look.ApplyFonts(s);
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

    /// <summary>Transform coordinates (menu, ribbon): the Transform tool, whose panel has the move, the turn and the scale of the old window.</summary>
    public void TransformCoordinates() => TransformTool();
}
