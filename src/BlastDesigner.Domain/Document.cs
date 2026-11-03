using System.IO.Compression;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Domain;

/// <summary>
/// An open project: the model, its file path, the selection and the undo history.
/// Every change goes through <see cref="Execute"/> so it is undoable and marks the document modified.
/// Selection changes are undo steps too (they restore the previous selection without touching the model or the modified flag).
/// </summary>
public sealed class Document
{
    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();
    private readonly Dictionary<Guid, byte[]> _attachmentData = new();

    public Document(BlastProject project, string? filePath = null)
    {
        Project = project;
        FilePath = filePath;
        RememberAttachmentData();
    }

    public BlastProject Project { get; private set; }
    public string? FilePath { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsModified { get; private set; }
    public int MaxUndo { get; set; } = 60;
    /// <summary>Name of the undo step recorded for a selection change.</summary>
    public const string SelectionStepName = "Selection";

    public HashSet<Guid> Selection { get; } = new();

    /// <summary>Raised after any model change, undo or redo.</summary>
    public event EventHandler? Changed;
    /// <summary>Raised when the selection set changes.</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyList<string> UndoNames => _undo.Select(u => u.Name).Reverse().ToList();
    public IReadOnlyList<string> RedoNames => _redo.Select(u => u.Name).Reverse().ToList();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : (string.IsNullOrWhiteSpace(Project.Header.Name) ? "Untitled" : Project.Header.Name);

    /// <summary>Runs a change as one undoable step.</summary>
    public void Execute(string name, Action<BlastProject> change)
    {
        var before = Snapshot();
        var selectionBefore = Selection.ToArray();
        var links = InitiationLinks.Capture(Project);
        try
        {
            change(Project);
            // initiation points stay on their holes, whatever the change moved or deleted
            InitiationLinks.Apply(Project, links);
        }
        catch
        {
            Restore(before);
            throw;
        }
        Push(new UndoEntry(name, before, selectionBefore));
        _redo.Clear();
        RememberAttachmentData();
        MarkModified();
    }

    /// <summary>Runs a change and returns a value, as one undoable step.</summary>
    public T Execute<T>(string name, Func<BlastProject, T> change)
    {
        T result = default!;
        Execute(name, p => { result = change(p); });
        return result;
    }

    public void Undo() => Undo(1);

    /// <summary>Undoes several steps at once (the undo drop-down list).</summary>
    public void Undo(int steps)
    {
        bool model = false;
        for (int i = 0; i < steps && _undo.Count > 0; i++)
        {
            var e = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(Apply(e));
            model |= e.State != null;
        }
        Finish(model);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Apply(e));
        Finish(e.State != null);
    }

    /// <summary>Restores an entry and returns the entry that reverses it.</summary>
    private UndoEntry Apply(UndoEntry e)
    {
        var back = new UndoEntry(e.Name, e.State != null ? Snapshot() : null, Selection.ToArray());
        if (e.State != null) Restore(e.State);
        Selection.Clear();
        foreach (var id in e.Selection) Selection.Add(id);
        return back;
    }

    private void Finish(bool modelChanged)
    {
        PruneSelection();
        if (modelChanged) MarkModified();
        else NotifyViewChanged();
    }

    private void Push(UndoEntry e)
    {
        _undo.Add(e);
        // MaxUndo counts model changes; selection steps are cheap but are capped as well so the list stays usable.
        while (_undo.Count(u => u.State != null) > MaxUndo || _undo.Count > MaxUndo * 4) _undo.RemoveAt(0);
    }

    public void MarkModified()
    {
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved()
    {
        IsModified = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Notifies listeners without recording an undo step (view-only changes such as visibility).</summary>
    public void NotifyViewChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- selection -------------------------------------------------------

    public IEnumerable<Entity> SelectedEntities => Project.Entities.Where(e => Selection.Contains(e.Id));
    public IEnumerable<Hole> SelectedHoles => SelectedEntities.OfType<Hole>();

    /// <summary>Changes the selection; with <paramref name="record"/> the change is an undo step (when the set actually changes).</summary>
    public void Select(IEnumerable<Guid> ids, SelectionMode mode = SelectionMode.Replace, bool record = true)
    {
        var set = ids.ToList();
        var before = Selection.ToArray();
        switch (mode)
        {
            case SelectionMode.Replace:
                Selection.Clear();
                foreach (var id in set) Selection.Add(id);
                break;
            case SelectionMode.Add:
                foreach (var id in set) Selection.Add(id);
                break;
            case SelectionMode.Toggle:
                foreach (var id in set) if (!Selection.Remove(id)) Selection.Add(id);
                break;
            case SelectionMode.Remove:
                foreach (var id in set) Selection.Remove(id);
                break;
        }
        if (record && !Selection.SetEquals(before))
        {
            Push(new UndoEntry(SelectionStepName, null, before));
            _redo.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection(bool record = true) => Select(Array.Empty<Guid>(), SelectionMode.Replace, record);

    private void PruneSelection()
    {
        var ids = Project.Entities.Select(e => e.Id).ToHashSet();
        Selection.RemoveWhere(id => !ids.Contains(id));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- snapshots -------------------------------------------------------

    private byte[] Snapshot()
    {
        var raw = ProjectJson.Serialize(Project);
        using var ms = new MemoryStream();
        using (var z = new BrotliStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        return ms.ToArray();
    }

    private void Restore(byte[] state)
    {
        using var input = new BrotliStream(new MemoryStream(state), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        Project = ProjectJson.Deserialize(ms.ToArray());
        foreach (var a in Project.Attachments)
            if (_attachmentData.TryGetValue(a.Id, out var d)) a.Data = d;
    }

    private void RememberAttachmentData()
    {
        foreach (var a in Project.Attachments) _attachmentData[a.Id] = a.Data;
    }

    /// <summary>One undo step: a model snapshot (null for a selection-only step) and the selection to restore.</summary>
    private sealed record UndoEntry(string Name, byte[]? State, Guid[] Selection);
}

public enum SelectionMode { Replace, Add, Toggle, Remove }
