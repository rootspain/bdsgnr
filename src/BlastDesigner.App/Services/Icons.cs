using Avalonia.Controls;
using Avalonia.Media;

namespace BlastDesigner.App.Services;

/// <summary>
/// Toolbar icons as vector geometry on a 16×16 grid, so they look the same on every platform
/// (no dependency on emoji or symbol fonts). Keyed by the toolbar tooltip (English).
/// </summary>
public static class Icons
{
    private const string Rect = "M2,2 H14 V14 H2 Z M3.5,3.5 V12.5 H12.5 V3.5 Z";

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["New"] = "M3,1 H10 L13,4 V15 H3 Z M4.5,2.5 V13.5 H11.5 V5 H9 V2.5 Z",
        ["Open"] = "M1,3 H6 L7.5,4.5 H14 V6 H4 L1,13 Z M4.5,7 H15.5 L12.5,14 H1.5 Z",
        ["Save"] = "M2,2 H12 L14,4 V14 H2 Z M4,3.5 V6.5 H11 V3.5 Z M4.5,9 V12.5 H11.5 V9 Z",
        ["Reports"] = "M3,1 H13 V15 H3 Z M4.5,2.5 V13.5 H11.5 V2.5 Z M5.5,4.5 H10.5 V5.5 H5.5 Z M5.5,7 H10.5 V8 H5.5 Z M5.5,9.5 H9 V10.5 H5.5 Z",
        ["Plan"] = "M1,3 L5,1.5 L11,3.5 L15,2 V13 L11,14.5 L5,12.5 L1,14 Z M5.5,3 V11.5 L10.5,13.2 V4.8 Z",
        ["Delete"] = "M3.5,2.4 L8,6.9 L12.5,2.4 L13.6,3.5 L9.1,8 L13.6,12.5 L12.5,13.6 L8,9.1 L3.5,13.6 L2.4,12.5 L6.9,8 L2.4,3.5 Z",
        ["Undo"] = "M2,6 L6,2 V4.5 C11,4.5 14,7 14,13 C12.5,9.5 10,8 6,8 V10.5 Z",
        ["Redo"] = "M14,6 L10,2 V4.5 C5,4.5 2,7 2,13 C3.5,9.5 6,8 10,8 V10.5 Z",
        ["Cut"] = "M5,1 L8,7.5 L11,1 H12.5 L8.8,9.2 L10.2,11 A2.5,2.5 0 1 1 9,11.8 L8,10.4 L7,11.8 A2.5,2.5 0 1 1 5.8,11 L7.2,9.2 L3.5,1 Z",
        ["Copy"] = "M5,1 H14 V11 H5 Z M6.5,2.5 V9.5 H12.5 V2.5 Z M2,5 H3.5 V13.5 H10 V15 H2 Z",
        ["Paste"] = "M5,1 H11 V3 H14 V15 H2 V3 H5 Z M3.5,4.5 V13.5 H12.5 V4.5 H11 V5.5 H5 V4.5 Z M6.5,2.5 V4 H9.5 V2.5 Z",
        ["Select"] = "M3,1 L13,9 L8.5,9.5 L11,14.5 L9.5,15.2 L7,10.2 L3,13 Z",
        ["Zoom"] = "M6.5,1 A5.5,5.5 0 1 1 6.49,1 Z M6.5,2.5 A4,4 0 1 0 6.51,2.5 Z M10,11 L11,10 L15,14 L14,15 Z",
        ["Zoom in"] = "M6.5,1 A5.5,5.5 0 1 1 6.49,1 Z M6.5,2.5 A4,4 0 1 0 6.51,2.5 Z M10,11 L11,10 L15,14 L14,15 Z M6,4 H7 V6 H9 V7 H7 V9 H6 V7 H4 V6 H6 Z",
        ["Zoom out"] = "M6.5,1 A5.5,5.5 0 1 1 6.49,1 Z M6.5,2.5 A4,4 0 1 0 6.51,2.5 Z M10,11 L11,10 L15,14 L14,15 Z M4,6 H9 V7 H4 Z",
        ["Undo zoom"] = "M6.5,1 A5.5,5.5 0 1 1 6.49,1 Z M6.5,2.5 A4,4 0 1 0 6.51,2.5 Z M10,11 L11,10 L15,14 L14,15 Z M4,6.5 L6.5,4 V5.8 H9 V7.2 H6.5 V9 Z",
        ["Rotate views"] = "M8,1 A7,7 0 0 1 15,8 H13.5 A5.5,5.5 0 1 0 8,13.5 V15 A7,7 0 0 1 8,1 Z M11.5,8 H16 L13.75,11 Z",
        ["Pan"] = "M8,0.5 L10.5,3.5 H8.7 V7.3 H12.5 V5.5 L15.5,8 L12.5,10.5 V8.7 H8.7 V12.5 H10.5 L8,15.5 L5.5,12.5 H7.3 V8.7 H3.5 V10.5 L0.5,8 L3.5,5.5 V7.3 H7.3 V3.5 H5.5 Z",
        ["Zoom all"] = "M1,1 H6 V2.5 H2.5 V6 H1 Z M10,1 H15 V6 H13.5 V2.5 H10 Z M1,10 H2.5 V13.5 H6 V15 H1 Z M13.5,10 H15 V15 H10 V13.5 H13.5 Z M5,5 H11 V11 H5 Z",
        ["North"] = "M8,1 L13,14 L8,11 L3,14 Z",
        ["Plan view"] = Rect + " M7.3,3.5 H8.7 V12.5 H7.3 Z M3.5,7.3 H12.5 V8.7 H3.5 Z",
        ["3D view"] = "M8,1 L15,4.5 V11.5 L8,15 L1,11.5 V4.5 Z M8,2.7 L3,5 L8,7.3 L13,5 Z M2.5,6.2 V10.6 L7.3,13 V8.5 Z M8.7,8.5 V13 L13.5,10.6 V6.2 Z",
        ["Layer manager"] = "M8,1 L15,4.5 L8,8 L1,4.5 Z M2.5,7.2 L8,10 L13.5,7.2 L15,8 L8,11.5 L1,8 Z M2.5,10.7 L8,13.5 L13.5,10.7 L15,11.5 L8,15 L1,11.5 Z",
        ["Lock holes"] = "M4,7 V5 A4,4 0 0 1 12,5 V7 H13 V15 H3 V7 Z M5.5,7 H10.5 V5 A2.5,2.5 0 0 0 5.5,5 Z M7.3,9.5 H8.7 V12.5 H7.3 Z",
        ["Unlock holes"] = "M3,7 H10 V5 A2.5,2.5 0 0 0 5,5 H3.5 A4,4 0 0 1 11.5,5 V7 H13 V15 H3 Z M7.3,9.5 H8.7 V12.5 H7.3 Z",
        ["Snap to objects"] = "M7.3,0.5 H8.7 V4 H7.3 Z M7.3,12 H8.7 V15.5 H7.3 Z M0.5,7.3 H4 V8.7 H0.5 Z M12,7.3 H15.5 V8.7 H12 Z M5,5 H11 V11 H5 Z M6.3,6.3 V9.7 H9.7 V6.3 Z",
        ["Laser wizard"] = "M1,14 L4,8 H6 L3.5,13 Z M5,7 L6,5 L15,2 L14,3 Z M3,10 H5 V15 H3 Z",
        ["Survey point"] = "M7.3,1 H8.7 V7.3 H15 V8.7 H8.7 V15 H7.3 V8.7 H1 V7.3 H7.3 Z",
        ["Surface"] = "M1,11 L5,4 L9,8 L12,3 L15,7 V14 H1 Z M5,6.3 L2.5,10.6 L8,10.6 Z M12,5.3 L9.8,9 L13.5,12.5 V7.6 Z",
        ["Grade peg"] = "M2,2 H14 L8,14 Z M4.4,3.5 L8,10.7 L11.6,3.5 Z",
        ["Monitoring point"] = Rect + " M4.5,3.5 L8,7 L11.5,3.5 L12.5,4.5 L9,8 L12.5,11.5 L11.5,12.5 L8,9 L4.5,12.5 L3.5,11.5 L7,8 L3.5,4.5 Z",
        ["Bore track"] = "M6,1 H10 V3 H6 Z M7.3,3 H8.7 C9,7 11,10 12.5,15 H11 C9.5,11 7.5,7.5 7.3,3 Z",
        ["Baseline"] = "M1,7.3 H15 V8.7 H1 Z M1,5 H2.5 V11 H1 Z M13.5,5 H15 V11 H13.5 Z",
        ["Hole"] = "M8,2 A6,6 0 1 1 7.99,2 Z",
        ["Row"] = "M3,6 A2,2 0 1 1 2.99,6 Z M8,6 A2,2 0 1 1 7.99,6 Z M13,6 A2,2 0 1 1 12.99,6 Z",
        ["Pattern"] = "M3.5,1.5 A2,2 0 1 1 3.49,1.5 Z M11.5,1.5 A2,2 0 1 1 11.49,1.5 Z M7.5,6 A2,2 0 1 1 7.49,6 Z M3.5,10.5 A2,2 0 1 1 3.49,10.5 Z M11.5,10.5 A2,2 0 1 1 11.49,10.5 Z",
        ["Fill polygon"] = "M1,3 L9,1 L15,6 L12,15 L2,13 Z M5,5 A1.5,1.5 0 1 1 4.99,5 Z M9.5,6 A1.5,1.5 0 1 1 9.49,6 Z M6.5,9.5 A1.5,1.5 0 1 1 6.49,9.5 Z M11,10 A1.5,1.5 0 1 1 10.99,10 Z",
        ["Boundary"] = "M1,3 L9,1 L15,6 L12,15 L2,13 Z M2.6,4.2 L3.4,11.8 L11,13.3 L13.3,6.5 L8.6,2.6 Z",
        ["Optimize rows"] = "M1,3 H15 V4.5 H1 Z M1,7.3 H15 V8.7 H1 Z M1,11.5 H15 V13 H1 Z",
        ["Hole measure"] = "M3,4 A2.5,2.5 0 1 1 2.99,4 Z M13,9 A2.5,2.5 0 1 1 12.99,9 Z M4.5,7 L11.5,10 L11,11.2 L4,8.2 Z",
        ["Volume"] = "M2,4 L8,1 L14,4 V12 L8,15 L2,12 Z M3.5,5.2 V11.1 L7.3,13 V7.1 Z M8.7,7.1 V13 L12.5,11.1 V5.2 Z",
        ["Hole label"] = "M5,1 H6.5 L6,5 H9.5 L10,1 H11.5 L11,5 H14 V6.5 H10.8 L10.4,9.5 H13.5 V11 H10.2 L9.7,15 H8.2 L8.7,11 H5.2 L4.7,15 H3.2 L3.7,11 H1 V9.5 H3.9 L4.3,6.5 H1.5 V5 H4.5 Z M5.8,6.5 L5.4,9.5 H8.9 L9.3,6.5 Z",
        ["Batter"] = "M1,4 H6 L12,13 H15 V15 H1 Z",
        ["Cross-section"] = "M1,10 L5,6 L9,9 L15,3 V15 H1 Z M1,1.5 H15 V3 H1 Z",
        ["Lead-in"] = "M8,0.5 L10,6 L15.5,6 L11,9.5 L13,15 L8,11.5 L3,15 L5,9.5 L0.5,6 L6,6 Z",
        ["Tie"] = "M2,12.9 L11.4,3.5 H8 V2 H14 V8 H12.5 V4.6 L3.1,14 Z",
        ["Dummy hole"] = "M8,2 A6,6 0 1 1 7.99,2 Z M8,3.5 A4.5,4.5 0 1 0 8.01,3.5 Z",
        ["In-hole delay"] = "M7,1 H9 V2.1 A6.5,6.5 0 1 1 7,2.1 Z M8,3.5 A5,5 0 1 0 8.01,3.5 Z M7.3,5 H8.7 V8.5 L10.5,10.3 L9.5,11.3 L7.3,9.1 Z",
        ["Loading rules"] = "M6.5,1 H9.5 L10,3 L12,2 L14,4 L13,6 L15,6.5 V9.5 L13,10 L14,12 L12,14 L10,13 L9.5,15 H6.5 L6,13 L4,14 L2,12 L3,10 L1,9.5 V6.5 L3,6 L2,4 L4,2 L6,3 Z M8,5.5 A2.5,2.5 0 1 0 8.01,5.5 Z",
        ["Loading"] = "M5,1 H11 V15 H5 Z M6.5,2.5 V5 H9.5 V2.5 Z M6.5,6.5 V13.5 H9.5 V6.5 Z",
        ["Apply loading rules"] = "M9,1 L3,9 H7.5 L6,15 L13,6 H8.5 Z",
        ["Set hole time"] = "M8,1 A7,7 0 1 1 7.99,1 Z M8,2.5 A5.5,5.5 0 1 0 8.01,2.5 Z M7.3,4 H8.7 V7.7 L11,10 L10,11 L7.3,8.3 Z",
        ["Row set"] = "M1,3 L7,8 L1,13 Z M8,3 L14,8 L8,13 Z M14,3 H15.5 V13 H14 Z",
        ["Timing line"] = "M1,14 L14,1 L15,2 L2,15 Z M3,4 A1.5,1.5 0 1 1 2.99,4 Z M7,8 A1.5,1.5 0 1 1 6.99,8 Z M12,11 A1.5,1.5 0 1 1 11.99,11 Z",
        ["Add/subtract time"] = "M7.3,1 H8.7 V4.3 H12 V5.7 H8.7 V9 H7.3 V5.7 H4 V4.3 H7.3 Z M4,12 H12 V13.5 H4 Z",
        ["Opening point"] = "M8,1 L9.5,6.5 L15,8 L9.5,9.5 L8,15 L6.5,9.5 L1,8 L6.5,6.5 Z",
        ["Point"] = "M8,5 A3,3 0 1 1 7.99,5 Z",
        ["Text"] = "M2,2 H14 V5 H12.5 V3.5 H8.8 V12.5 H10.5 V14 H5.5 V12.5 H7.2 V3.5 H3.5 V5 H2 Z",
        ["Arrow"] = "M1,7.3 H10 V4 L15,8 L10,12 V8.7 H1 Z",
        ["Polyline"] = "M1,12 L5,4 L10,10 L14,2 L15.3,2.7 L10.2,12.6 L5.2,6.6 L2.3,12.7 Z",
        ["Polygon"] = "M8,1 L15,6 L12.5,15 H3.5 L1,6 Z M8,2.8 L2.7,6.6 L4.6,13.5 H11.4 L13.3,6.6 Z",
        ["Measure"] = "M0.5,11 L11,0.5 L15.5,5 L5,15.5 Z M2.6,11 L5,13.4 L13.4,5 L11,2.6 L10,3.6 L11.2,4.8 L10.5,5.5 L9.3,4.3 L8,5.6 L9.2,6.8 L8.5,7.5 L7.3,6.3 L6,7.6 L7.2,8.8 L6.5,9.5 L5.3,8.3 L4,9.6 L5.2,10.8 L4.5,11.5 L3.3,10.3 Z",
        ["Dimension"] = "M1,4 H2.5 V12 H1 Z M13.5,4 H15 V12 H13.5 Z M2.5,8 L5.5,5.5 V7.3 H10.5 V5.5 L13.5,8 L10.5,10.5 V8.7 H5.5 V10.5 Z",
        ["Circle"] = "M8,1 A7,7 0 1 1 7.99,1 Z M8,2.5 A5.5,5.5 0 1 0 8.01,2.5 Z",
        ["North point"] = "M8,1 L12,9 H8.7 V15 H7.3 V9 H4 Z",
    };

    /// <summary>Icon control for a toolbar tooltip, or null when none is defined.</summary>
    public static Control? For(string tip)
    {
        if (!Paths.TryGetValue(tip, out var data)) return null;
        return new PathIcon { Data = Geometry.Parse(data), Width = 16, Height = 16 };
    }

    public static IEnumerable<string> Names => Paths.Keys;
}
