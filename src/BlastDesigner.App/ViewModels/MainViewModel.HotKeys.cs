using System.Text.RegularExpressions;
using BlastDesigner.App.Services;
using BlastDesigner.App.Tools;

namespace BlastDesigner.App.ViewModels;

/// <summary>A key or keys and what they do, both shown as they are.</summary>
public sealed record HotKey(string Keys, string Action);

/// <summary>Where the keys work, and the keys; texts translated.</summary>
public sealed record HotKeyGroup(string Place, IReadOnlyList<HotKey> Keys);

/// <summary>
/// Every assigned key, by the place it works in, for the Hot keys page of the program settings. The menu, ribbon, rail
/// and quick filter keys are read from their declarations; the keys of the window, tools and windows are listed here
/// in English, as MainWindow.OnKeyDown, the tools and the windows handle them: a key changed there
/// is changed here too.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly (string Place, HotKey[] Keys)[] WindowKeys =
    {
        ("Main window, anywhere", new HotKey[]
        {
            new("Alt+1…7", "Switch the ribbon stage"),
            new("Alt+Enter", "Full screen and back"),
            new("Ctrl+K", "Command search"),
            new("Ctrl+F, F3", "Find hole by label"),
            new("Ctrl+E", "Find empty hole"),
            new("Ctrl+M", "Find misfire hole"),
            new("Ctrl+L", "Select holes with an actual length"),
            new("Ctrl+U", "Select holes not programmed"),
            new("Ctrl+G", "Group the selection"),
            new("Ctrl+T", "Transform tool"),
            new("Ctrl+Shift+T", "Hole table"),
            new("Ctrl+Shift+V", "Visualize timing"),
            new("Ctrl+Shift+Q", "Quantities"),
            new("Ctrl+Shift+L", "Show loading"),
            new("Ctrl+Shift+S", "Section view"),
            new("Ctrl+Shift+X", "Export IREDES plan"),
            new("Ctrl+Shift+D", "Dashboard"),
            new("Ctrl+Shift+O", "Overview"),
            new("Ctrl+Shift+Y", "Single hole view (bullseye)"),
            new("Ctrl+Shift+H", "Horizontal slice"),
            new("Ctrl+Shift+R", "Row check view")
        }),
        ("Plan and 3D view", new HotKey[]
        {
            new("V", "Select tool"),
            new("H", "Pan"),
            new("Q", "Lasso selection"),
            new("Z", "Zoom to rectangle"),
            new("Home", "View everything"),
            new("Shift+Z", "Previous view"),
            new("O", "Rotate 3D"),
            new("N", "North up / rotate plan"),
            new("M", "Measure"),
            new("W", "Drawing tool"),
            new("S", "Snap to objects on or off"),
            new("G", "Coordinate grid on or off"),
            new("D", "Hole tool"),
            new("R", "Row tool"),
            new("P", "Pattern tool"),
            new("L", "Loading tool"),
            new("T", "Ties with the initiation catalogue"),
            new("I", "Initiation point"),
            new("E", "Set time tool"),
            new("X", "Cross-section"),
            new("F7", "Layers tab of the inspector"),
            new("Shift+H", "Fade what is behind a surface"),
            new("Esc", "Leave the tool; with the Select tool, clear the selection")
        })
    };

    private static readonly (string Place, HotKey[] Keys)[] OtherKeys =
    {
        ("Select tool", new HotKey[]
        {
            new("Shift+click", "Add to the selection"),
            new("Ctrl+click", "Add or remove from the selection"),
            new("← → ↑ ↓", "Move the selection"),
            new("Shift+↑ ↓", "Move the selection up and down (Z)"),
            new("Ctrl+arrows", "Move in fine steps"),
            new("Shift+drag", "Drag up and down (Z)"),
            new("Ctrl+drag", "Drag in fine steps"),
            new("Enter", "Properties of the selection"),
            new("Esc", "Clear the selection")
        }),
        ("Drawing and placing tools", new HotKey[]
        {
            new("Enter", "Finish the line or entry"),
            new("Backspace", "Remove the last point"),
            new("Tab", "Type the coordinates of the next point"),
            new("Esc", "Drop the entry; again, leave the tool"),
            new("Shift+click", "Row: go on past two points"),
            new("Shift", "Front line: angle in steps")
        }),
        ("Ties tool", new HotKey[]
        {
            new("F", "Show or hide the capture zone"),
            new("+ / −", "Pick radius larger or smaller"),
            new("Shift+click", "Go on from the last hole")
        }),
        ("Transform tool", new HotKey[]
        {
            new("0–9 . −", "Type the exact value while dragging a handle"),
            new("Backspace", "Remove the last typed character"),
            new("Enter", "Take the typed value")
        }),
        ("Section view", new HotKey[]
        {
            new("Delete", "Delete the selected marks"),
            new("← → ↑ ↓", "Move the selected marks 0.1 m"),
            new("Ctrl+arrows", "Move the selected marks 0.01 m"),
            new("Ctrl+C, Ctrl+V", "Copy and paste the marks"),
            new("H, U", "Angle mark: level second line"),
            new("V, D", "Angle mark: upright second line"),
            new("Tab", "Angle mark: as drawn, acute or obtuse"),
            new("Enter", "Finish the polyline mark or type the level"),
            new("Shift", "Point not snapped"),
            new("Esc", "Cancel, then clear the selection")
        }),
        ("Mouse in the view", new HotKey[]
        {
            new("Wheel", "Zoom at the cursor"),
            new("Middle button", "Pan"),
            new("Shift+middle button", "Orbit in 3D"),
            new("Alt+left button", "Orbit in 3D"),
            new("Right click", "Context menu")
        }),
        ("Command search", new HotKey[]
        {
            new("↑ ↓", "Move through the results"),
            new("Enter", "Run the command"),
            new("Esc", "Close the search")
        }),
        ("Windows and dialogs", new HotKey[]
        {
            new("Enter", "OK; in a form, confirm the field"),
            new("Ctrl+Enter", "OK in a form"),
            new("Esc", "Cancel"),
            new("↑ ↓", "Step the number in a field")
        }),
        ("Startup window", new HotKey[]
        {
            new("Ctrl+N", "New blast"),
            new("Ctrl+O", "Open a file"),
            new("Ctrl+F", "Search the list"),
            new("↑ ↓, Home, End", "Choose in the list"),
            new("Enter", "Open the chosen one"),
            new("Delete", "Remove from the list"),
            new("Esc", "Clear the search, then close")
        })
    };

    /// <summary>The English places and actions of the keys listed here, which have translations (checked by the UI smoke test).</summary>
    public static IEnumerable<string> ListedHotKeyTexts =>
        WindowKeys.Concat(OtherKeys).SelectMany(g => g.Keys.Select(k => k.Action).Prepend(g.Place))
            .Concat(new[] { "Main menu", "Ribbon", "View rail", "Quick filter buttons", "Switch the ribbon stage" }).Distinct();

    /// <summary>"Ctrl⇧T", "Ctrl P", "⇧Z" as "Ctrl+Shift+T", "Ctrl+P", "Shift+Z".</summary>
    public static string KeyText(string key) => Regex.Replace(key.Trim().Replace("⇧", "Shift+").Replace(' ', '+'), "(Ctrl|Alt)(?=Shift)", "$1+").Replace("++", "+");

    public List<HotKeyGroup> HotKeyGroups()
    {
        static HotKeyGroup Listed(string place, IEnumerable<HotKey> keys) => new(Loc.Ui(place), keys.Select(k => new HotKey(Loc.Ui(k.Keys), Loc.Ui(k.Action))).ToList());
        var groups = WindowKeys.Select(g => Listed(g.Place, g.Keys)).ToList();

        var menu = new List<HotKey>();
        void Walk(IEnumerable<MenuEntry> entries, string prefix)
        {
            foreach (var e in entries)
            {
                if (e.IsSeparator) continue;
                var path = prefix.Length == 0 ? Loc.T(e.Header) : prefix + " ▸ " + Loc.T(e.Header);
                if (e.Gesture != null) menu.Add(new HotKey(KeyText(e.Gesture), path));
                if (e.Children != null) Walk(e.Children, path);
            }
        }
        Walk(BuildMenu(), "");
        groups.Add(new HotKeyGroup(Loc.Ui("Main menu"), menu));

        var ribbon = new List<HotKey> { new("Alt+1…7", Loc.Ui("Switch the ribbon stage")) };
        foreach (var s in BuildStages())
            foreach (var it in s.Groups.SelectMany(g => g.Items).Where(i => !string.IsNullOrEmpty(i.Key)))
                ribbon.Add(new HotKey(KeyText(it.Key!), Loc.Ui(s.Name) + " ▸ " + Loc.Ui(it.Label)));
        groups.Add(new HotKeyGroup(Loc.Ui("Ribbon"), ribbon));

        groups.Add(new HotKeyGroup(Loc.Ui("View rail"), BuildRail().Where(r => r.Key.Length > 0).Select(r => new HotKey(KeyText(r.Key), Loc.Ui(r.Tip))).ToList()));
        var filters = QuickFilters().Where(f => f.Key.Length > 0).Select(f => new HotKey(KeyText(f.Key), Loc.Ui(f.Label))).ToList();
        if (filters.Count > 0) groups.Add(new HotKeyGroup(Loc.Ui("Quick filter buttons"), filters));

        groups.AddRange(OtherKeys.Select(g => Listed(g.Place, g.Keys)));
        return groups;
    }
}
