using Avalonia.Threading;
using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;
using BlastDesigner.App.Views;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.IO;
using BlastDesigner.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace BlastDesigner.App.ViewModels;

/// <summary>Application state and every command of the main window. Split across partial files by menu.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _autosave;
    private readonly Stack<(Vec3 Target, double Scale, double Yaw, double Pitch)> _zoomHistory = new();
    private Tool? _tool;

    public MainViewModel(MainWindow window, Viewport view, ProgramSettings settings)
    {
        Window = window;
        View = view;
        Settings = settings;
        Templates = new TemplateStore(ProgramSettings.TemplatesFolder);
        Recovery = new RecoveryStore(ProgramSettings.RecoveryFolder);
        View.ProjectSource = () => Doc?.Project;
        View.ToolSource = () => _tool;
        View.OptionsSource = BuildSceneOptions;
        View.ViewMenu = ViewContextMenu;
        View.SnapEnabled = settings.SnapToObjects;
        View.TargetRadius = settings.TargetRadiusPixels;
        View.ShowFps = settings.ShowFps;
        View.SurfaceWireframe = settings.SurfaceWireframe;
        View.PointerWorldMoved += p => Coordinates = $"X {p.X:0.00}   Y {p.Y:0.00}   Z {p.Z:0.00}";
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, settings.AutosaveMinutes)) };
        _autosave.Tick += (_, _) => WriteRecovery();
        _autosave.Start();
        _tool = new SelectTool(this);
    }

    public MainWindow Window { get; }
    public Viewport View { get; }
    public ProgramSettings Settings { get; }
    public TemplateStore Templates { get; }
    public RecoveryStore Recovery { get; }
    public Document? Doc { get; private set; }
    public BlastProject? Project => Doc?.Project;
    public bool HasProject => Doc != null;
    public bool HasSelection => Doc?.Selection.Count > 0;
    public Tool? ActiveTool => _tool;

    /// <summary>Connector chosen in the tie palette; null means a 0 ms connection.</summary>
    public Product? ActiveConnector { get; set; }
    public HashSet<Guid> HiddenEntities { get; } = new();
    /// <summary>Tie tool panel ▸ Tie chain: a line ties the holes on its way in turn; off, one tie from start to end.</summary>
    public bool TieChain { get; set; } = true;
    /// <summary>Tie tool panel ▸ Capture width: grey lines beside the tie being drawn show what it captures; kept in the settings.</summary>
    public bool TieCaptureShown
    {
        get => Settings.TieCaptureShown;
        set
        {
            if (Settings.TieCaptureShown == value) return;
            Settings.TieCaptureShown = value;
            Settings.Save();
        }
    }
    /// <summary>Tie palette ▸ User defined: the delay new ties get instead of a product's; null uses the connector.</summary>
    public double? ActiveTieDelayMs { get; set; }
    /// <summary>Tie palette ▸ Initiation system chosen (a <see cref="Views.TiePaletteWindow"/> system key).</summary>
    public string? TieSystem { get; set; }
    /// <summary>Initiation catalogue: family chosen for each type (initiation system).</summary>
    public Dictionary<string, (ProductKind Kind, string Name)> TieFamilyChoice { get; } = new();
    public HighlightOverlay Highlight { get; } = new();
    public List<IOverlay> AnalysisOverlays { get; } = new();
    public double? AnimationTime { get; set; }

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _coordinates = string.Empty;
    [ObservableProperty] private string _title = "Blast Designer";

    /// <summary>Raised when panels (layers, table, properties) must re-read the model.</summary>
    public event Action? ModelRefreshed;
    /// <summary>Objects are being dragged on the plan (the preview offset changed): views drawn from it redraw.</summary>
    public event Action? DragPreview;
    public void RaiseDragPreview() => DragPreview?.Invoke();
    public event Action? SelectionRefreshed;
    public event Action? ToolChanged;

    public void SetStatus(string text)
    {
        Status = text;
        Log.Debug(text);
    }

    // ---- document lifecycle -----------------------------------------------------

    public void Attach(Document doc)
    {
        if (Doc != null)
        {
            Doc.Changed -= OnDocChanged;
            Doc.SelectionChanged -= OnSelectionChanged;
        }
        Doc = doc;
        HiddenEntities.Clear();
        Highlight.Ids.Clear();
        AnalysisOverlays.Clear();
        doc.Changed += OnDocChanged;
        doc.SelectionChanged += OnSelectionChanged;
        ActiveConnector = doc.Project.Products.FirstOrDefault(p => p.Kind == ProductKind.SurfaceConnector);
        UseMinStemming();
        Timing.Solve(doc.Project);
        if (doc.Project.View.Scale > 0 && doc.FilePath != null) View.Camera.LoadFrom(doc.Project.View);
        else View.ZoomAll();
        if (doc.Project.Entities.Count > 0 && doc.FilePath == null) View.ZoomAll();
        SelectTool();
        RefreshAll();
    }

    public void Detach()
    {
        if (Doc == null) return;
        Recovery.Clear(Doc.FilePath, Doc.Project.Id);
        Doc.Changed -= OnDocChanged;
        Doc.SelectionChanged -= OnSelectionChanged;
        Doc = null;
        _tool?.Deactivate();
        RefreshAll();
    }

    private void OnDocChanged(object? sender, EventArgs e)
    {
        if (Doc == null) return;
        // an undo can bring back another minimum stemming
        UseMinStemming();
        Timing.Solve(Doc.Project);
        RefreshAll();
    }

    /// <summary>The loading checks take the minimum stemming of the open project, or the program settings' when it has none.</summary>
    private void UseMinStemming() => Charging.MinStemmingDiameters = Doc?.Project.MinStemmingDiameters ?? Settings.MinStemmingDiameters;

    /// <summary>
    /// A new minimum stemming: the project keeps it (one undo step), and the program settings keep it for projects
    /// that have none.
    /// </summary>
    public void SetMinStemming(double diameters)
    {
        Settings.MinStemmingDiameters = diameters;
        Settings.Save();
        if (Doc == null) { Charging.MinStemmingDiameters = diameters; return; }
        if (Project!.MinStemmingDiameters == diameters) { UseMinStemming(); return; }
        Change("Minimum stemming", p => p.MinStemmingDiameters = diameters);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        SelectionRefreshed?.Invoke();
        var sel = Doc?.SelectedEntities.ToList() ?? new();
        if (sel.Count > 0)
        {
            var holes = sel.OfType<Hole>().ToList();
            SetStatus(holes.Count > 0
                ? Loc.F("{0} selected ({1} holes, {2:0.0} m drilled, {3:0.0} kg)", sel.Count, holes.Count, holes.Sum(h => h.EffectiveLength), holes.Sum(h => Charging.ExplosiveMass(Project!, h)))
                : Loc.F("{0} selected", sel.Count));
        }
        View.Redraw();
    }

    public void RefreshAll()
    {
        Title = (Doc == null ? "Blast Designer" : $"{Doc.DisplayName}{(Doc.IsModified ? " *" : "")}{(Doc.IsReadOnly ? " (" + Loc.T("Read only") + ")" : "")} — Blast Designer")
            + (Trial.IsTrial ? " — " + Loc.F("Trial ({0})", Trial.DaysLeft) : "");
        ModelRefreshed?.Invoke();
        SelectionRefreshed?.Invoke();
        View.Redraw();
    }

    /// <summary>Runs an undoable change of the project and refreshes the UI. Errors are shown to the user and rolled back.</summary>
    public bool Change(string name, Action<BlastProject> change)
    {
        if (Doc == null) return false;
        try
        {
            Doc.Execute(Loc.T(name), change);
            SetStatus(Loc.T(name));
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.ShowError(ex);
            return false;
        }
    }

    /// <summary>Name of the ribbon stage that is open ("Layout", "Loading"…); the viewport adapts what it shows to it.</summary>
    public string? ActiveStage { get; set; }

    /// <summary>Plan background shown while the settings window previews a theme; null outside the preview.</summary>
    public uint? PreviewBackground { get; set; }

    public SceneOptions BuildSceneOptions()
    {
        var overlays = new List<IOverlay>(AnalysisOverlays) { Highlight };
        return new SceneOptions
        {
            Selection = Doc?.Selection ?? new HashSet<Guid>(),
            Background = new SKColor(PreviewBackground ?? Settings.BackgroundColor),
            SelectionColor = new SKColor(Settings.SelectionColor),
            CollarPixels = (float)Settings.CollarSizePixels,
            Overlays = overlays,
            HiddenEntities = HiddenEntities,
            AnimationTimeMs = AnimationTime,
            // the summary box stays on printed plans only; on screen the inspector already shows the totals
            DrawSummaryBlock = false,
            // the hole type legend matters while laying out holes; on the other stages it only covers the plan
            DrawHoleLegend = ActiveStage == "Layout",
            // the connectors are listed in the tie palette; on screen the legend only covers the plan (printed plans keep it)
            DrawTieLegend = false,
            DrawElevationScale = !Settings.SurfacesByType,
            GeometryColor = Settings.GeometryColor is uint g ? new SKColor(g) : null,
            SurfaceColors = Settings.SurfacesByType ? Enum.GetValues<SurfaceKind>().ToDictionary(k => k, k => new SKColor(Settings.SurfaceColor(k.ToString()))) : null,
            TopInset = 56
        };
    }

    // ---- tools -------------------------------------------------------------------

    public void SetTool(Tool tool)
    {
        _tool?.Deactivate();
        _tool = tool;
        tool.Activate();
        ToolChanged?.Invoke();
        View.Redraw();
        View.Focus();
    }

    public void SelectTool() => SetTool(new SelectTool(this));

    /// <summary>Done / Enter on a tool: finish the input in progress; with nothing pending, close the tool and return to selection.</summary>
    public void CompleteTool()
    {
        var tool = ActiveTool;
        if (tool == null || tool is SelectTool) return;
        if (!tool.FinishEntry()) { tool.Cancel(); SelectTool(); }
        View.Redraw();
    }

    public void PushZoomHistory()
    {
        var c = View.Camera;
        _zoomHistory.Push((c.Target, c.Scale, c.Yaw, c.Pitch));
    }

    public void UndoZoom()
    {
        if (_zoomHistory.Count == 0) return;
        var (t, s, y, p) = _zoomHistory.Pop();
        View.Camera.Target = t;
        View.Camera.Scale = s;
        View.Camera.Yaw = y;
        View.Camera.Pitch = p;
        View.Redraw();
    }

    /// <summary>Default parameters for new holes from the project's design data and surfaces.</summary>
    public HoleParameters NewHoleParameters(bool dummy = false)
    {
        var p = HoleParameters.From(Project!.Design);
        p.Dummy = dummy;
        p.CollarSurface = Project.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Bench);
        p.GradeSurface = Project.Entities.OfType<SurfaceEntity>().FirstOrDefault(s => s.Kind == SurfaceKind.Grade);
        return p;
    }

    public Layer HoleLayer => Project!.GetOrCreateLayer(StandardLayers.Holes, 0xFF1E3A8A, "Design");

    /// <summary>Next free numeric hole label.</summary>
    public string NextHoleLabel()
    {
        var max = Project!.Holes.Select(h => int.TryParse(new string(h.Label.Where(char.IsDigit).ToArray()), out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString();
    }

    private void WriteRecovery()
    {
        if (Doc is not { IsModified: true }) return;
        try { Recovery.Write(Doc.Project, Doc.FilePath); }
        catch (Exception ex) { Log.Error("Autosave failed", ex); }
    }

    public void MoveSelection(Vec3 delta)
    {
        var ids = Doc!.Selection.ToHashSet();
        var timed = 0;
        Change("Move", p => timed = ReprogramTimingLines(p, ids, () =>
        {
            foreach (var e in p.Entities.Where(e => ids.Contains(e.Id)))
            {
                if (e is Hole { Locked: true }) continue;
                if (p.FindLayer(e.LayerId) is { Locked: true }) continue;
                e.Translate(delta);
            }
        }));
        ReportReprogrammed(timed);
    }

    /// <summary>
    /// Runs <paramref name="move"/> and reprograms the holes of every timing line among <paramref name="moved"/> from its
    /// new position (in the same undo step as the move), as a line drawn there would. A line from an older file that does
    /// not know its holes first takes them from its old position (<see cref="HolesTimedBy"/>) and keeps them.
    /// Returns the number of holes timed.
    /// </summary>
    public static int ReprogramTimingLines(BlastProject p, ISet<Guid> moved, Action move)
    {
        var lines = p.Entities.OfType<TimingLineEntity>().Where(l => moved.Contains(l.Id)).ToList();
        foreach (var line in lines.Where(l => l.HoleIds.Count == 0)) line.HoleIds = HolesTimedBy(p, line);
        move();
        var timed = 0;
        foreach (var line in lines.Where(l => l.HoleIds.Count > 0))
        {
            var holes = p.Holes.Where(h => line.HoleIds.Contains(h.Id) && !h.IsDummy).ToList();
            if (holes.Count > 0) timed += EbsTimingLine.Apply(p, holes, line).Timed;
        }
        return timed;
    }

    /// <summary>
    /// Holes a timing line programmed: those whose present time is what the line gives them, up to the one shift the line's
    /// own first-firing hole introduced (it times its holes from the earliest of them, not of all holes).
    /// </summary>
    internal static List<Guid> HolesTimedBy(BlastProject p, TimingLineEntity line)
    {
        var holes = p.Holes.Where(h => !h.IsDummy && h.ElectronicTimeMs.HasValue && !h.ElectronicTimeManual).ToList();
        if (holes.Count == 0 || line.Points.Count < 2) return new();
        var planned = EbsTimingLine.Plan(p, holes, line.Points, line.Segments, EbsTimingLine.OptionsOf(line), double.MaxValue, p.Electronic.TimeStepMs).Times;
        var shifts = holes.Where(h => planned.ContainsKey(h.Id)).Select(h => (h.Id, D: h.ElectronicTimeMs!.Value - planned[h.Id])).ToList();
        if (shifts.Count == 0) return new();
        // both times are rounded to the time step, so the shift of the line's holes may differ by one step
        var tol = Math.Max(p.Electronic.TimeStepMs, 0.01) + 1e-6;
        var best = shifts.MaxBy(s => shifts.Count(o => Math.Abs(o.D - s.D) <= tol)).D;
        return shifts.Where(s => Math.Abs(s.D - best) <= tol).Select(s => s.Id).ToList();
    }

    public void ReportReprogrammed(int timed)
    {
        if (timed > 0) SetStatus(Loc.F("Timing line moved: {0} holes reprogrammed", timed));
    }
}
