using Avalonia.Input;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using SkiaSharp;

namespace BlastDesigner.App.Tools;

/// <summary>
/// Picks the surface the collars of holes sit on (<see cref="MainViewModel.PickCollarSurface"/>): only surfaces are taken,
/// the one under the pointer shows its name and its edge; a click on one calls back, a click beside one waits for the next.
/// Esc, Cancel or another tool gives up (the second call back). The caller brings the tool used before back.
/// </summary>
public sealed class PickCollarSurfaceTool : Tool
{
    private static readonly SKColor EdgeColour = new(0xFFF59E0B);
    /// <summary>A surface with more triangles than this is not outlined while hovered: finding its edge would hold up the pointer.</summary>
    private const int MaxOutlinedTriangles = 600_000;

    private readonly Action<SurfaceEntity> _onPick;
    private readonly Action _onCancelled;
    private readonly Action _back;
    private bool _done;
    private Guid? _outlined;
    private List<IReadOnlyList<Vec3>> _edge = new();

    public PickCollarSurfaceTool(MainViewModel vm, Action<SurfaceEntity> onPick, Action onCancelled, Action back) : base(vm)
    {
        _onPick = onPick;
        _onCancelled = onCancelled;
        _back = back;
    }

    public override string Name => "Collar surface";
    public override string Hint => "Click the surface the collars sit on; Esc gives up.";

    private SurfaceEntity? Target(ToolPointer p) => View.HitTest(p.X, p.Y, accept: e => e is SurfaceEntity) as SurfaceEntity;

    public override void PointerPressed(ToolPointer p)
    {
        if (!p.Left || _done) return;
        if (Target(p) is not { } s) { Vm.SetStatus(Loc.T(Hint)); return; }
        _done = true;
        try { _onPick(s); }
        catch (Exception ex) { Dialogs.ShowError(ex); }
    }

    public override void PointerMoved(ToolPointer p)
    {
        Preview.Labels.Clear();
        Preview.Guides.Clear();
        if (Target(p) is not { } s) { _outlined = null; return; }
        Preview.Labels.Add((p.World, string.IsNullOrWhiteSpace(s.Name) ? Loc.T(s.KindName) : s.Name));
        if (_outlined != s.Id) { _outlined = s.Id; _edge = Edge(s); }
        foreach (var e in _edge) Preview.Guides.Add((e, EdgeColour));
    }

    /// <summary>The edge of a surface: the sides of its triangles that only one triangle has.</summary>
    private static List<IReadOnlyList<Vec3>> Edge(SurfaceEntity s)
    {
        var list = new List<IReadOnlyList<Vec3>>();
        if (s.Triangles.Count / 3 > MaxOutlinedTriangles) return list;
        var count = new Dictionary<(int, int), int>();
        for (int i = 0; i + 2 < s.Triangles.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = s.Triangles[i + k], b = s.Triangles[i + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                count[key] = count.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        foreach (var ((a, b), n) in count)
            if (n == 1) list.Add(new[] { s.Vertices[a], s.Vertices[b] });
        return list;
    }

    public override bool KeyDown(Key key, KeyModifiers mods)
    {
        if (key != Key.Escape) return false;
        _back();
        return true;
    }

    public override void Deactivate()
    {
        base.Deactivate();
        if (_done) return;
        _done = true;
        _onCancelled();
    }
}
