using BlastDesigner.App.Controls;
using BlastDesigner.App.Services;
using BlastDesigner.App.ViewModels;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;
using BlastDesigner.Engine;
using BlastDesigner.Rendering;
using SkiaSharp;

namespace BlastDesigner.App.Tools.Section;

/// <summary>What a point of the section snapped to.</summary>
public enum SectionSnapKind { None, Collar, Toe, Grade, Surface, End, Intersection, Perpendicular, Frame }

/// <summary>A point picked on the section: where it is, what it is held on, what it snapped to.</summary>
public sealed record SectionPick(SectionPoint Point, SKPoint Screen, SectionSnapKind Snap);

/// <summary>A straight piece the section shows, for an angle or a slope: its ends as points of a mark (held on what they are).</summary>
public sealed record SectionPiece(SectionPoint A, SectionPoint B, SKPoint ScreenA, SKPoint ScreenB);

/// <summary>
/// The Section view window as its tools see it: the line of the section and its plane, the view, the marks drawn by hand
/// on it and those selected, the style the ribbon gives the next mark, and the snaps. One for each window.
/// </summary>
public sealed class SectionSession
{
    public SectionSession(MainViewModel vm, Viewport view, Func<(Vec3 A, Vec3 B)> line, Func<double> halfWidth)
    {
        Vm = vm; View = view; _line = line; HalfWidth = halfWidth;
    }

    private readonly Func<(Vec3 A, Vec3 B)> _line;
    public MainViewModel Vm { get; }
    public Viewport View { get; }
    public Func<double> HalfWidth { get; }
    public (Vec3 A, Vec3 B) Line => _line();
    public SectionFrame Frame => new(Line.A, Line.B);
    public BlastProject? Project => Vm.Project;

    /// <summary>The marks overlay of the last frame: what it drew is what is picked.</summary>
    public SectionMarksOverlay? Marks { get; set; }
    /// <summary>The frame of the section on the screen, last frame.</summary>
    public Func<Camera, BlastProject, SKRect?>? Box { get; set; }
    public HashSet<Guid> SelectedMarks { get; } = new();
    /// <summary>The mark being drawn, shown over the section.</summary>
    public SectionMark? Preview { get; set; }
    /// <summary>While the selected marks are dragged: how far, along the line and up, m.</summary>
    public (double S, double Z)? MoveBy { get; set; }

    /// <summary>What is added to the heights of the top and the bottom of the frame now, m (up positive).</summary>
    public Func<(double Top, double Bottom)>? FrameShifts { get; set; }
    /// <summary>Shows the frame with these shifts while its top or bottom line is dragged, nothing kept yet.</summary>
    public Action<double, double>? PreviewFrameShifts { get; set; }
    /// <summary>Keeps the shifts shown, in one step, once the line of the frame is let go.</summary>
    public Action? CommitFrameShifts { get; set; }

    /// <summary>The top (true) or the bottom (false) line of the frame under the screen point, within so many px; null off them.</summary>
    public bool? FrameEdgeAt(double x, double y, double radius = 4)
    {
        if (FrameShifts == null || Project is not { } project || Box?.Invoke(View.Camera, project) is not { } box) return null;
        if (x < box.Left - radius || x > box.Right + radius) return null;
        if (Math.Abs(y - box.Top) <= radius) return true;
        if (Math.Abs(y - box.Bottom) <= radius) return false;
        return null;
    }

    /// <summary>Shows the section along this line while an end of the frame is dragged, nothing kept yet.</summary>
    public Action<Vec3, Vec3>? PreviewLine { get; set; }
    /// <summary>Keeps the line shown, in one step, once the end of the frame is let go (the line as it was: nothing).</summary>
    public Action? CommitLine { get; set; }

    /// <summary>The left (true) or the right (false) line of the frame under the screen point, within so many px; null off them.</summary>
    public bool? FrameSideAt(double x, double y, double radius = 4)
    {
        if (PreviewLine == null || Project is not { } project || Box?.Invoke(View.Camera, project) is not { } box) return null;
        if (y < box.Top - radius || y > box.Bottom + radius) return null;
        if (Math.Abs(x - box.Left) <= radius) return true;
        if (Math.Abs(x - box.Right) <= radius) return false;
        return null;
    }

    /// <summary>
    /// Moves the selected marks by so much along the line and up, in one step. Their points go free where they are put:
    /// a point held on a hole or a surface lets go of it.
    /// </summary>
    public void MoveSelected(double ds, double dz)
    {
        if (SelectedMarks.Count == 0 || Vm.Doc == null || Project is not { } project) return;
        var frame = Frame;
        var ids = SelectedMarks.ToHashSet();
        // where the points are now, before the change: a held point is where its hole is
        var placed = project.SectionMarks.Where(m => ids.Contains(m.Id)).ToDictionary(m => m.Id, m => SectionMarking.Resolve(project, frame, m));
        Vm.Doc.Execute("Move section marks", p =>
        {
            foreach (var m in p.SectionMarks.Where(m => placed.ContainsKey(m.Id)))
                for (int i = 0; i < m.Points.Count && i < placed[m.Id].Count; i++)
                    m.Points[i] = new SectionPoint(placed[m.Id][i].S + ds, placed[m.Id][i].Z + dz);
        });
        Touch();
    }

    /// <summary>The style the ribbon set for the marks to come: its fields set override the default of each kind.</summary>
    public SectionMarkStyle? Patch { get; set; }
    /// <summary>What the point under the cursor snapped to, for the status line.</summary>
    public SectionSnapKind Snapped { get; set; }
    /// <summary>Where the cursor was last over the section, px.</summary>
    public SKPoint LastPointer { get; set; }
    /// <summary>
    /// Asks for a value in a box at a point of the view (the height of a level, the words of a text): the prompt, the value
    /// it opens with, what to do with what is typed (Enter); Esc drops it. Set by the window.
    /// </summary>
    public Action<SKPoint, string, string, Action<string>>? AskAt { get; set; }
    /// <summary>The hint of the step, under the section.</summary>
    public string Hint { get; private set; } = "";
    /// <summary>The selection of marks, the hint or the marks changed: the ribbon and the status line show it again.</summary>
    public event Action? Changed;

    public void SetHint(string hint) { Hint = hint; Changed?.Invoke(); }
    public void Touch() { Changed?.Invoke(); View.Redraw(); }

    /// <summary>The section line of the window, null for a section only picked.</summary>
    public PolylineEntity? LineEntity => Project is { } p ? SectionMarking.LineAt(p, Line.A, Line.B) : null;

    /// <summary>
    /// The section line the marks go on: the one of the window, or, for a section only picked, a section line made along it
    /// in the same step (it takes the next letter): the marks are kept in the project and printed with it.
    /// </summary>
    public Guid EnsureLine(BlastProject p)
    {
        if (SectionMarking.LineAt(p, Line.A, Line.B) is { } l) return l.Id;
        var line = new PolylineEntity { Points = new List<Vec3> { Line.A, Line.B }, Kind = LineKind.CrossSection, Label = SectionNames.Next(p) };
        p.Add(line);
        return line.Id;
    }

    /// <summary>The style a new mark of a kind takes: the default of the kind with what the ribbon set over it.</summary>
    public SectionMarkStyle StyleFor(SectionMarkKind kind)
    {
        var s = Vm.Settings.SectionMarkStyleFor(kind);
        if (Patch is { } p)
        {
            s.Color = p.Color ?? s.Color;
            s.LineType = p.LineType ?? s.LineType;
            if (p.LineWeight > 0) s.LineWeight = p.LineWeight;
        }
        return s;
    }

    /// <summary>Adds a mark in one undoable step, on the line of the window (made if it is only picked).</summary>
    public void Add(SectionMark mark)
    {
        Vm.Doc?.Execute("Add section mark", p =>
        {
            mark.SectionLineId = EnsureLine(p);
            p.SectionMarks.Add(mark);
        });
        SelectedMarks.Clear();
        Touch();
    }

    /// <summary>Changes the selected marks in one undoable step.</summary>
    public void ChangeSelected(string name, Action<SectionMark> change)
    {
        if (SelectedMarks.Count == 0 || Vm.Doc == null) return;
        var ids = SelectedMarks.ToHashSet();
        Vm.Doc.Execute(name, p => { foreach (var m in p.SectionMarks.Where(m => ids.Contains(m.Id))) change(m); });
        Touch();
    }

    public void DeleteSelected()
    {
        if (SelectedMarks.Count == 0 || Vm.Doc == null) return;
        var ids = SelectedMarks.ToHashSet();
        Vm.Doc.Execute("Delete section marks", p => p.SectionMarks.RemoveAll(m => ids.Contains(m.Id)));
        SelectedMarks.Clear();
        Touch();
    }

    /// <summary>The automatic dimensions of the section as last drawn.</summary>
    public List<AutoDimension> AutoDimensions { get; } = new();

    /// <summary>The automatic dimension under a screen point: by its lines or its figure.</summary>
    public int? AutoDimensionAt(double x, double y, double radius)
    {
        var p = new SKPoint((float)x, (float)y);
        for (int i = 0; i < AutoDimensions.Count; i++)
            if (AutoDimensions[i].Figure.Contains(p) || AutoDimensions[i].Lines.Any(l => SegmentDistance(l.A, l.B, p) <= radius)) return i;
        return null;
    }

    /// <summary>The automatic dimensions in a screen box: their figure whole or all their lines (left to right), any part (right to left).</summary>
    public List<int> AutoDimensionsIn(SKRect r, bool crossing)
    {
        var found = new List<int>();
        for (int i = 0; i < AutoDimensions.Count; i++)
        {
            var d = AutoDimensions[i];
            var ends = d.Lines.SelectMany(l => new[] { l.A, l.B }).ToList();
            var inside = r.Contains(d.Figure) || ends.All(r.Contains);
            if (crossing ? inside || ends.Any(r.Contains) || r.IntersectsWith(d.Figure) : inside) found.Add(i);
        }
        return found;
    }

    /// <summary>
    /// The automatic dimensions made marks of their own, in one step, to be selected, moved, edited and deleted: held on
    /// the same holes, so they keep up with them. The section draws them no more; those picked are selected.
    /// </summary>
    public void TakeOverAutoDimensions(IEnumerable<int> picked, string step = "Edit automatic dimensions")
    {
        if (Vm.Doc == null || AutoDimensions.Count == 0) return;
        var marks = AutoDimensions.Select(d => { var m = d.Mark.Copy(); m.Id = Guid.NewGuid(); return m; }).ToList();
        var chosen = picked.Where(i => i >= 0 && i < marks.Count).Select(i => marks[i].Id).ToList();
        Vm.Doc.Execute(step, p =>
        {
            var line = EnsureLine(p);
            if (!p.SectionOwnDimensions.Contains(line)) p.SectionOwnDimensions.Add(line);
            foreach (var m in marks) { m.SectionLineId = line; p.SectionMarks.Add(m); }
        });
        foreach (var id in chosen) SelectedMarks.Add(id);
        Touch();
    }

    /// <summary>
    /// The dimensions of the section made again from where the holes are now: the marks made of them before go, and the
    /// window makes them again as it draws.
    /// </summary>
    public void RestoreAutoDimensions()
    {
        if (Vm.Doc == null || LineEntity is not { } line || Project is not { } project || !project.SectionOwnDimensions.Contains(line.Id)) return;
        Vm.Doc.Execute("Automatic dimensions", p =>
        {
            p.SectionOwnDimensions.Remove(line.Id);
            p.SectionMarks.RemoveAll(m => m.SectionLineId == line.Id && m.Auto);
        });
        SelectedMarks.Clear();
        Touch();
    }

    /// <summary>The selected marks, as they are in the project.</summary>
    public List<SectionMark> Selected() => Project?.SectionMarks.Where(m => SelectedMarks.Contains(m.Id)).ToList() ?? new();

    // ---- points ----------------------------------------------------------------------------------------------

    public SKPoint ToScreen(SectionSpot s) { var (x, y) = View.Camera.ToScreen(Frame.ToWorld(s.S, s.Z)); return new SKPoint(x, y); }
    public SKPoint ToScreen(Vec3 w) { var (x, y) = View.Camera.ToScreen(w); return new SKPoint(x, y); }
    public SKPoint ToScreen(SectionPoint p) => Project is { } pr ? ToScreen(SectionMarking.Resolve(pr, Frame, p)) : ToScreen(new SectionSpot(p.S, p.Z));
    public SectionPoint Free(double sx, double sy) { var (s, z) = Frame.ToSection(View.Camera.ToWorld(sx, sy)); return new SectionPoint(s, z); }

    /// <summary>How near on the screen a point snaps, px.</summary>
    public const double SnapRadius = 10;

    /// <summary>The snaps turned on (<see cref="ProgramSettings.SectionSnaps"/>).</summary>
    public bool SnapOn(SectionSnapKind kind) => !Vm.Settings.SectionSnapsOff.Contains(kind.ToString());

    /// <summary>
    /// The point under the cursor: snapped to the nearest collar, toe, grade, point of a surface cut, end of a mark, crossing
    /// of two pieces, foot of the perpendicular from <paramref name="from"/> or corner of the frame, those turned on; with
    /// Ortho on, straight across or up from <paramref name="from"/>; free without them (or with <paramref name="free"/>).
    /// </summary>
    public SectionPick Pick(double sx, double sy, SectionPoint? from = null, bool free = false)
    {
        var cursor = new SKPoint((float)sx, (float)sy);
        if (Project is { } project && !free)
        {
            var best = (Pick: (SectionPick?)null, D: SnapRadius);
            void Try(SectionPoint p, SectionSnapKind kind)
            {
                if (!SnapOn(kind)) return;
                var at = ToScreen(p);
                var d = Distance(at, cursor);
                if (d < best.D) best = (new SectionPick(p, at, kind), d);
            }
            foreach (var h in SectionProfile.Build(project, Line.A, Line.B, HalfWidth()).Holes)
            {
                Try(Held(h.Hole, SectionHolePart.Collar), SectionSnapKind.Collar);
                Try(Held(h.Hole, SectionHolePart.Toe), SectionSnapKind.Toe);
                if (h.GradeZ - h.Toe.Z > 0.05) Try(Held(h.Hole, SectionHolePart.Grade), SectionSnapKind.Grade);
            }
            var pieces = Pieces();
            foreach (var piece in pieces.Where(p => p.A.SurfaceId != null))
            {
                Try(piece.A, SectionSnapKind.Surface);
                Try(piece.B, SectionSnapKind.Surface);
            }
            foreach (var m in project.SectionMarks.Where(m => LineEntity?.Id == m.SectionLineId))
                foreach (var p in m.Points)
                    Try(p.Copy(), SectionSnapKind.End);
            if (Box?.Invoke(View.Camera, project) is { } box)
                foreach (var corner in new[] { new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top), new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom) })
                    Try(Free(corner.X, corner.Y), SectionSnapKind.Frame);
            // the crossings of the pieces near the cursor
            if (best.Pick == null && SnapOn(SectionSnapKind.Intersection))
            {
                var near = pieces.Where(p => SegmentDistance(p.ScreenA, p.ScreenB, cursor) < SnapRadius * 2).ToList();
                for (int i = 0; i < near.Count; i++)
                    for (int j = i + 1; j < near.Count; j++)
                        if (Cross(near[i].ScreenA, near[i].ScreenB, near[j].ScreenA, near[j].ScreenB) is { } x)
                            Try(Free(x.X, x.Y), SectionSnapKind.Intersection);
            }
            // the foot of the perpendicular from the point before onto the piece under the cursor
            if (best.Pick == null && from != null && SnapOn(SectionSnapKind.Perpendicular))
            {
                var f = ToScreen(from);
                foreach (var p in pieces.Where(p => SegmentDistance(p.ScreenA, p.ScreenB, cursor) < SnapRadius))
                {
                    var foot = Foot(p.ScreenA, p.ScreenB, f);
                    if (Distance(foot, cursor) < SnapRadius * 1.5) Try(Free(foot.X, foot.Y), SectionSnapKind.Perpendicular);
                }
            }
            if (best.Pick is { } snapped) return snapped;
            // on a surface cut: held on the surface at that point
            if (SnapOn(SectionSnapKind.Surface))
                foreach (var p in pieces.Where(p => p.A.SurfaceId != null && SegmentDistance(p.ScreenA, p.ScreenB, cursor) < SnapRadius * 0.6))
                {
                    var foot = Foot(p.ScreenA, p.ScreenB, cursor);
                    var on = Free(foot.X, foot.Y);
                    on.SurfaceId = p.A.SurfaceId;
                    return new SectionPick(on, foot, SectionSnapKind.Surface);
                }
        }
        var point = Free(sx, sy);
        if (from != null && Vm.Settings.SectionOrtho)
        {
            var f = Project is { } pr ? SectionMarking.Resolve(pr, Frame, from) : new SectionSpot(from.S, from.Z);
            // straight across or up from the point before, whichever is nearer the cursor
            var scale = View.Camera.Scale;
            if (Math.Abs(point.S - f.S) * scale >= Math.Abs(point.Z - f.Z) * scale) point.Z = f.Z;
            else point.S = f.S;
        }
        return new SectionPick(point, ToScreen(new SectionSpot(point.S, point.Z)), SectionSnapKind.None);
    }

    private SectionPoint Held(Hole h, SectionHolePart part)
    {
        var (s, z) = Frame.ToSection(part switch { SectionHolePart.Toe => h.Toe, SectionHolePart.Grade => h.GradePoint, _ => h.Collar });
        return new SectionPoint(s, z) { HoleId = h.Id, HolePart = part };
    }

    /// <summary>
    /// The straight pieces the section shows: the holes of the band from collar to toe, the cuts of the surfaces, the lines
    /// and levels drawn by hand. An angle is measured between two of them; their crossings are snapped to.
    /// </summary>
    public List<SectionPiece> Pieces()
    {
        var list = new List<SectionPiece>();
        if (Project is not { } project) return list;
        var frame = Frame;
        SectionPiece Piece(SectionPoint a, SectionPoint b) => new(a, b, ToScreen(a), ToScreen(b));
        foreach (var h in SectionProfile.Build(project, Line.A, Line.B, HalfWidth()).Holes)
            list.Add(Piece(Held(h.Hole, SectionHolePart.Collar), Held(h.Hole, SectionHolePart.Toe)));
        var cam = View.Camera;
        var shown = project.Layers.Where(l => l.Visible).Select(l => l.Id).ToHashSet();
        foreach (var s in project.Entities.OfType<SurfaceEntity>().Where(s => shown.Contains(s.LayerId) && project.View.ShowSurfaces && ViewKinds.Shown(project.View, s)))
            foreach (var (a, b) in PlanRenderer.SurfaceCut(cam, s))
            {
                var (sa, za) = frame.ToSection(a);
                var (sb, zb) = frame.ToSection(b);
                list.Add(Piece(new SectionPoint(sa, za) { SurfaceId = s.Id }, new SectionPoint(sb, zb) { SurfaceId = s.Id }));
            }
        var line = LineEntity?.Id;
        foreach (var m in project.SectionMarks.Where(m => m.SectionLineId == line))
        {
            if (m.Kind == SectionMarkKind.Polyline)
                for (int i = 1; i < m.Points.Count; i++) list.Add(Piece(m.Points[i - 1].Copy(), m.Points[i].Copy()));
            else if (m.Kind == SectionMarkKind.Level && m.Points.Count > 0 && Box?.Invoke(cam, project) is { } box)
            {
                var z = SectionMarking.Resolve(project, frame, m.Points[0]).Z;
                var left = Free(box.Left, 0);
                var right = Free(box.Right, 0);
                list.Add(Piece(new SectionPoint(left.S, z), new SectionPoint(right.S, z)));
            }
        }
        return list;
    }

    /// <summary>The piece nearest a screen point within the snap radius.</summary>
    public SectionPiece? PieceAt(double sx, double sy)
    {
        var cursor = new SKPoint((float)sx, (float)sy);
        return Pieces().Select(p => (P: p, D: SegmentDistance(p.ScreenA, p.ScreenB, cursor))).Where(x => x.D <= SnapRadius).OrderBy(x => x.D).Select(x => x.P).FirstOrDefault();
    }

    public static double Distance(SKPoint a, SKPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double SegmentDistance(SKPoint a, SKPoint b, SKPoint p) => Distance(Foot(a, b, p), p);

    /// <summary>The point of the piece a–b nearest p.</summary>
    public static SKPoint Foot(SKPoint a, SKPoint b, SKPoint p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        return new SKPoint((float)(a.X + t * dx), (float)(a.Y + t * dy));
    }

    /// <summary>Where the pieces a–b and c–d cross, null when they do not.</summary>
    public static SKPoint? Cross(SKPoint a, SKPoint b, SKPoint c, SKPoint d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y, sx = d.X - c.X, sy = d.Y - c.Y;
        var den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-9) return null;
        var t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / den;
        var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return null;
        return new SKPoint((float)(a.X + t * rx), (float)(a.Y + t * ry));
    }
}
