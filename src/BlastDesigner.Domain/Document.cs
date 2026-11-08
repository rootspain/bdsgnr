using System.IO.Compression;
using System.Runtime.InteropServices;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Domain;

/// <summary>
/// An open project: the model, its file path, the selection and the undo history.
/// Every change goes through <see cref="Execute"/> so it is undoable and marks the document modified.
/// Selection changes are undo steps too (they restore the previous selection without touching the model or the modified flag);
/// they raise <see cref="SelectionChanged"/> alone, nothing has to be worked out again for them.
/// A snapshot keeps the meshes of the surfaces apart, as they are: comparing them takes far less time than writing them
/// out, and a mesh that did not change is shared by all the snapshots.
/// </summary>
public sealed class Document
{
    private readonly List<UndoEntry> _undo = new();
    private readonly List<UndoEntry> _redo = new();
    private readonly Dictionary<Guid, byte[]> _attachmentData = new();
    /// <summary>The mesh of each surface as last stored: what the next snapshot compares it with.</summary>
    private Dictionary<Guid, Mesh> _meshes = new();

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
    /// <summary>Memory the undo history may take for meshes; the oldest steps go beyond it (each move of a large surface stores it again).</summary>
    public long MaxMeshBytes { get; set; } = 512L * 1024 * 1024;
    /// <summary>Name of the undo step recorded for a selection change.</summary>
    public const string SelectionStepName = "Selection";

    public HashSet<Guid> Selection { get; } = new();

    /// <summary>
    /// A change of <see cref="Execute"/> is running: the model is half changed and <see cref="Changed"/> has not been
    /// raised yet, so whatever keeps values worked out from it (the calculation session) must not answer from them.
    /// </summary>
    public bool IsChanging { get; private set; }

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
        var before = TakeSnapshot();
        var selectionBefore = Selection.ToArray();
        var links = InitiationLinks.Capture(Project);
        IsChanging = true;
        try
        {
            change(Project);
            // initiation points stay on their holes, whatever the change moved or deleted
            InitiationLinks.Apply(Project, links);
        }
        catch
        {
            IsChanging = false;
            Restore(before);
            // the restored project is a new object without the solved times: the listeners work them out again
            PruneSelection();
            NotifyViewChanged();
            throw;
        }
        IsChanging = false;
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
        var back = new UndoEntry(e.Name, e.State != null ? TakeSnapshot() : null, Selection.ToArray());
        if (e.State != null) Restore(e.State);
        Selection.Clear();
        foreach (var id in e.Selection) Selection.Add(id);
        return back;
    }

    private void Finish(bool modelChanged)
    {
        PruneSelection();
        if (modelChanged) MarkModified();
    }

    private void Push(UndoEntry e)
    {
        _undo.Add(e);
        // MaxUndo counts model changes; selection steps are cheap but are capped as well so the list stays usable.
        while (_undo.Count(u => u.State != null) > MaxUndo || _undo.Count > MaxUndo * 4) _undo.RemoveAt(0);
        while (_undo.Count > 1 && MeshBytes() > MaxMeshBytes) _undo.RemoveAt(0);
    }

    /// <summary>Memory of the meshes kept for undo and redo, each array counted once.</summary>
    private long MeshBytes()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var e in _undo.Concat(_redo))
            foreach (var m in e.State?.Meshes ?? Array.Empty<Mesh>())
            {
                if (seen.Add(m.Vertices)) bytes += (long)m.Vertices.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Vec3>();
                if (seen.Add(m.Triangles)) bytes += (long)m.Triangles.Length * sizeof(int);
            }
        return bytes;
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

    private Snapshot TakeSnapshot()
    {
        var light = Project.ShallowCopy();
        light.Entities = new List<Entity>(Project.Entities.Count);
        var meshes = new List<Mesh>();
        var stored = new Dictionary<Guid, Mesh>();
        foreach (var e in Project.Entities)
        {
            if (e is not SurfaceEntity s) { light.Entities.Add(e); continue; }
            var mesh = Stored(s);
            meshes.Add(mesh);
            stored[s.Id] = mesh;
            light.Entities.Add(s.WithoutMesh());
        }
        _meshes = stored;
        var raw = ProjectJson.Serialize(light);
        using var ms = new MemoryStream();
        using (var z = new BrotliStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
        return new Snapshot(ms.ToArray(), meshes.ToArray());
    }

    /// <summary>The mesh of a surface for a snapshot: the one stored last, or the parts of it that did not change.</summary>
    private Mesh Stored(SurfaceEntity s)
    {
        _meshes.TryGetValue(s.Id, out var last);
        var vertices = CollectionsMarshal.AsSpan(s.Vertices);
        var triangles = CollectionsMarshal.AsSpan(s.Triangles);
        var sameVertices = last != null && MemoryMarshal.AsBytes(vertices).SequenceEqual(MemoryMarshal.AsBytes(last.Vertices.AsSpan()));
        var sameTriangles = last != null && triangles.SequenceEqual(last.Triangles);
        return sameVertices && sameTriangles ? last! : new Mesh(sameVertices ? last!.Vertices : vertices.ToArray(), sameTriangles ? last!.Triangles : triangles.ToArray());
    }

    private void Restore(Snapshot state)
    {
        Project = Materialize(state, _attachmentData);
        _meshes = new Dictionary<Guid, Mesh>();
        var i = 0;
        foreach (var s in Project.Entities.OfType<SurfaceEntity>()) _meshes[s.Id] = state.Meshes[i++];
    }

    /// <summary>A project of its own from a snapshot; touches nothing of the document, so it can run on another thread.</summary>
    private static BlastProject Materialize(Snapshot state, IReadOnlyDictionary<Guid, byte[]> attachments)
    {
        using var input = new BrotliStream(new MemoryStream(state.Json), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        var project = ProjectJson.Deserialize(ms.ToArray());
        var i = 0;
        foreach (var s in project.Entities.OfType<SurfaceEntity>())
        {
            var mesh = state.Meshes[i++];
            s.Vertices = new List<Vec3>(mesh.Vertices);
            s.Triangles = new List<int>(mesh.Triangles);
        }
        foreach (var a in project.Attachments)
            if (attachments.TryGetValue(a.Id, out var d)) a.Data = d;
        return project;
    }

    /// <summary>
    /// The project as it is now, quickly and on this thread; the result makes a copy of it that goes on any thread, for
    /// what takes long with the document open (the recovery file).
    /// </summary>
    public Func<BlastProject> Capture()
    {
        var state = TakeSnapshot();
        var attachments = new Dictionary<Guid, byte[]>(_attachmentData);
        return () => Materialize(state, attachments);
    }

    private void RememberAttachmentData()
    {
        foreach (var a in Project.Attachments) _attachmentData[a.Id] = a.Data;
    }

    /// <summary>One undo step: a model snapshot (null for a selection-only step) and the selection to restore.</summary>
    private sealed record UndoEntry(string Name, Snapshot? State, Guid[] Selection);

    /// <summary>The project without the meshes of its surfaces, and the meshes in the order of the surfaces.</summary>
    private sealed record Snapshot(byte[] Json, Mesh[] Meshes);

    /// <summary>The mesh of a surface at some moment: never changed once stored, shared between snapshots.</summary>
    private sealed record Mesh(Vec3[] Vertices, int[] Triangles);
}

public enum SelectionMode { Replace, Add, Toggle, Remove }
