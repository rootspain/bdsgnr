using System.Text.Json.Serialization;

namespace BlastDesigner.Domain.Model;

/// <summary>The stage of the work a stage button shows: drilling or loading.</summary>
public enum WorkStage { Drilling, Loading }

/// <summary>What of a stage a stage button shows: the plan of it or the fact.</summary>
public enum StageMode { Plan, Fact }

/// <summary>
/// The data the holes are drawn from: the design (collar, design toe, straight track) or what was drilled (the surveyed
/// collar, the dipped depth, the surveyed bore track).
/// </summary>
public enum HoleBasis { Design, Actual }

/// <summary>
/// A stage button over the plan (Drilling · Plan, Drilling · Fact, Loading · Plan, Loading · Fact): a set of layers, of
/// hole states and the hole data the view shows while it is on.
/// </summary>
public sealed class StageView
{
    public WorkStage Stage { get; set; }
    public StageMode Mode { get; set; }
    /// <summary>The layers of the button: shown when it is switched on, hidden when it is switched off (unless another button on has them).</summary>
    public HashSet<Guid> Layers { get; set; } = new();
    /// <summary>The hole states shown while the button is on.</summary>
    public HashSet<HoleState> States { get; set; } = new();
    public HoleBasis HoleBasis { get; set; }
    public bool On { get; set; }

    /// <summary>English name, "Drilling · Plan".</summary>
    [JsonIgnore] public string Name => StageViews.Name(Stage, Mode);
}

/// <summary>
/// The stage buttons of a project: switching them, the layers bound to them, and the first bindings of a new blast.
/// The buttons change the view only (layer visibility, hidden hole states, hole data): switching one is no undo step.
/// </summary>
public static class StageViews
{
    public static readonly WorkStage[] Stages = { WorkStage.Drilling, WorkStage.Loading };
    public static readonly StageMode[] Modes = { StageMode.Plan, StageMode.Fact };

    public static string Name(WorkStage stage, StageMode mode) => $"{stage} · {mode}";

    /// <summary>The four buttons, all off, no layers or states: Drilling · Plan, Drilling · Fact, Loading · Plan, Loading · Fact.</summary>
    public static List<StageView> Empty()
    {
        var list = new List<StageView>();
        foreach (var s in Stages)
            foreach (var m in Modes)
                list.Add(new StageView { Stage = s, Mode = m, HoleBasis = m == StageMode.Plan && s == WorkStage.Drilling ? HoleBasis.Design : HoleBasis.Actual });
        return list;
    }

    /// <summary>
    /// The four buttons of the project in their order, made when missing (a file from before them: all layers shared, the
    /// buttons off); a button twice is kept once.
    /// </summary>
    public static List<StageView> Of(BlastProject p)
    {
        var list = p.StageViews ??= new List<StageView>();
        if (list.Count == 4 && Stages.SelectMany(s => Modes.Select(m => (s, m))).Select((k, i) => list[i].Stage == k.s && list[i].Mode == k.m).All(x => x))
            return list;
        var ordered = new List<StageView>();
        foreach (var blank in Empty())
            ordered.Add(list.FirstOrDefault(v => v.Stage == blank.Stage && v.Mode == blank.Mode) ?? blank);
        p.StageViews = ordered;
        return ordered;
    }

    public static StageView Get(BlastProject p, WorkStage stage, StageMode mode) => Of(p).First(v => v.Stage == stage && v.Mode == mode);

    /// <summary>The buttons switched on.</summary>
    public static IEnumerable<StageView> Enabled(BlastProject p) => Of(p).Where(v => v.On);

    /// <summary>The other button of the same stage (plan for fact, fact for plan).</summary>
    public static StageView Sibling(BlastProject p, StageView view) => Get(p, view.Stage, view.Mode == StageMode.Plan ? StageMode.Fact : StageMode.Plan);

    /// <summary>
    /// A click on a button. Off: it goes on, and every other button off (<paramref name="keepOthers"/>, Ctrl: only the
    /// other button of its stage). On: it goes off. The layers of a button switched on are shown; those of a button
    /// switched off hidden, unless a button still on has them; other layers stay as they are. The hole states shown are
    /// those of the buttons on (none on: left as they are); the hole data that of the button switched on last.
    /// Returns the layers hidden.
    /// </summary>
    public static HashSet<Guid> Switch(BlastProject p, StageView view, bool keepOthers)
    {
        var all = Of(p);
        view = all.FirstOrDefault(v => v.Stage == view.Stage && v.Mode == view.Mode) ?? view;
        var turnedOff = new List<StageView>();
        StageView? turnedOn = null;
        if (view.On)
        {
            view.On = false;
            turnedOff.Add(view);
        }
        else
        {
            foreach (var other in all)
                if (other != view && other.On && (!keepOthers || other.Stage == view.Stage))
                {
                    other.On = false;
                    turnedOff.Add(other);
                }
            view.On = true;
            turnedOn = view;
        }
        var hidden = new HashSet<Guid>();
        var stillShown = all.Where(v => v.On).SelectMany(v => v.Layers).ToHashSet();
        foreach (var off in turnedOff)
            foreach (var id in off.Layers)
                if (!stillShown.Contains(id) && p.FindLayer(id) is { } layer)
                {
                    if (layer.Visible) hidden.Add(id);
                    layer.Visible = false;
                }
        if (turnedOn != null)
            foreach (var id in turnedOn.Layers)
                if (p.FindLayer(id) is { } layer) layer.Visible = true;
        Apply(p, turnedOn);
        return hidden;
    }

    /// <summary>
    /// The hole states and hole data of the buttons on, put into the view: states of none of them hidden, the hole data
    /// that of <paramref name="last"/> (else of the last button on in their order). No button on, or none with states: states left as they are.
    /// </summary>
    public static void Apply(BlastProject p, StageView? last = null)
    {
        var on = Enabled(p).ToList();
        var shown = on.SelectMany(v => v.States).ToHashSet();
        // a button with no states (a file from before them) leaves the states as they are
        if (shown.Count > 0)
        {
            p.View.HiddenStates = Enum.GetValues<HoleState>().Where(s => !shown.Contains(s)).ToHashSet();
        }
        var basisFrom = last is { On: true } ? last : on.LastOrDefault();
        p.View.HoleBasis = basisFrom?.HoleBasis ?? HoleBasis.Design;
    }

    /// <summary>A new layer goes to every button on; with none on it is shared (shown whatever the buttons). True when bound to any.</summary>
    public static bool BindNewLayer(BlastProject p, Layer layer)
    {
        var any = false;
        foreach (var v in Enabled(p)) any |= v.Layers.Add(layer.Id);
        return any;
    }

    /// <summary>Takes the layers no longer in the project out of the buttons.</summary>
    public static void Prune(BlastProject p)
    {
        if (p.StageViews is not { Count: > 0 } list) return;
        var ids = p.Layers.Select(l => l.Id).ToHashSet();
        foreach (var v in list) v.Layers.RemoveWhere(id => !ids.Contains(id));
    }

    /// <summary>The buttons whose layers have this one.</summary>
    public static IEnumerable<StageView> Owners(BlastProject p, Guid layerId) => Of(p).Where(v => v.Layers.Contains(layerId));

    /// <summary>The default name of a new layer while buttons are on: their names and the date ("Drilling · Fact · 2026-11-24"); null with none on.</summary>
    public static string? NewLayerName(BlastProject p, Func<StageView, string> name, DateTime today)
    {
        var on = Enabled(p).ToList();
        return on.Count == 0 ? null : string.Join(" · ", on.Select(name).Append(today.ToString("yyyy-MM-dd")));
    }

    /// <summary>
    /// For <see cref="Document.Followers"/>: a layer added by a change goes to the buttons on, a layer deleted out of the
    /// buttons, in the same undo step.
    /// </summary>
    public static Action? Follow(BlastProject p)
    {
        var before = p.Layers.Select(l => l.Id).ToHashSet();
        return () =>
        {
            if (p.Layers.Count == before.Count && p.Layers.All(l => before.Contains(l.Id))) return;
            foreach (var l in p.Layers.Where(l => !before.Contains(l.Id))) BindNewLayer(p, l);
            Prune(p);
        };
    }

    /// <summary>
    /// English names of the layers each button of a new blast takes, when it has them; they are shown translated as
    /// "name:…" (Loc.Name).
    /// </summary>
    public static readonly IReadOnlyDictionary<(WorkStage, StageMode), string[]> InitialLayers = new Dictionary<(WorkStage, StageMode), string[]>
    {
        [(WorkStage.Drilling, StageMode.Plan)] = new[] { "Drilling design", "Block boundary" },
        [(WorkStage.Drilling, StageMode.Fact)] = new[] { "Collar survey", "Depth measurements" },
        [(WorkStage.Loading, StageMode.Plan)] = new[] { "Loading design", "Block boundary" },
        [(WorkStage.Loading, StageMode.Fact)] = new[] { "Loading actual" }
    };

    /// <summary>The names these layers had when they were Russian: blasts and templates saved with them still match.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyLayerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Drilling design"] = "Проект бурения", ["Block boundary"] = "Контур блока", ["Collar survey"] = "Съёмка устьев",
        ["Depth measurements"] = "Замеры глубин", ["Loading design"] = "Проект заряжания", ["Loading actual"] = "Факт заряжания"
    };

    /// <summary>
    /// Whether a layer is the standard layer of that English name: by its own name, its old Russian name, or the name it
    /// is shown by (<paramref name="shown"/>, the translation of both).
    /// </summary>
    public static bool IsLayer(string layerName, string english, Func<string, string>? shown = null)
    {
        var legacy = LegacyLayerNames.GetValueOrDefault(english);
        if (string.Equals(layerName, english, StringComparison.OrdinalIgnoreCase) || (legacy != null && string.Equals(layerName, legacy, StringComparison.OrdinalIgnoreCase))) return true;
        if (shown == null) return false;
        var mine = shown(layerName);
        return string.Equals(mine, shown(english), StringComparison.OrdinalIgnoreCase) || string.Equals(mine, english, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first bindings of a new blast: each button its layers by name (only those the blast has; a layer is matched by
    /// its name, its old Russian name or the name it is shown by, <paramref name="shown"/>), its hole states and hole data.
    /// All buttons off.
    /// </summary>
    public static void Initial(BlastProject p, Func<string, string>? shown = null)
    {
        var list = Empty();
        foreach (var v in list)
        {
            foreach (var name in InitialLayers[(v.Stage, v.Mode)])
                foreach (var l in p.Layers)
                    if (IsLayer(l.Name, name, shown))
                        v.Layers.Add(l.Id);
            (v.States, v.HoleBasis) = (v.Stage, v.Mode) switch
            {
                (WorkStage.Drilling, StageMode.Plan) => (new HashSet<HoleState> { HoleState.Designed }, HoleBasis.Design),
                (WorkStage.Drilling, StageMode.Fact) => (new HashSet<HoleState> { HoleState.Drilled, HoleState.Dipped }, HoleBasis.Actual),
                (WorkStage.Loading, StageMode.Plan) => (new HashSet<HoleState> { HoleState.Dipped, HoleState.Released }, HoleBasis.Actual),
                _ => (new HashSet<HoleState> { HoleState.Loaded, HoleState.Fired }, HoleBasis.Actual)
            };
        }
        p.StageViews = list;
    }

    /// <summary>
    /// The view of one button for a print, on a copy of the project: its layers shown and every other layer bound to a
    /// button hidden, its hole states and hole data. The project given is not changed.
    /// </summary>
    public static BlastProject ForPrint(BlastProject p, StageView view)
    {
        var copy = ProjectJson.DeepClone(p);
        var all = Of(copy);
        foreach (var v in all) v.On = v.Stage == view.Stage && v.Mode == view.Mode;
        var mine = all.First(v => v.On);
        var bound = all.SelectMany(v => v.Layers).ToHashSet();
        foreach (var l in copy.Layers)
        {
            if (mine.Layers.Contains(l.Id)) l.Visible = true;
            else if (bound.Contains(l.Id)) l.Visible = false;
        }
        Apply(copy, mine);
        return copy;
    }
}
