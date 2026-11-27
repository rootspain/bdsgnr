using System.Text.Json.Serialization;
using BlastDesigner.Domain.Geometry;

namespace BlastDesigner.Domain.Model;

/// <summary>Life-cycle state of a blast hole.</summary>
public enum HoleState
{
    Designed,
    Drilled,
    Dipped,
    Released,
    Loaded,
    Fired
}

/// <summary>Field logging exceptions recorded against a hole.</summary>
[Flags]
public enum HoleException
{
    None = 0,
    ExtraDetonator = 1,
    ExtraHole = 2,
    NoLog = 4,
    MissingDetonator = 8,
    Blocked = 16,
    NotDrilled = 32,
    Wet = 64,
    Misfire = 128,
    ShortHole = 256
}

/// <summary>A blast hole: collar, direction and length, plus its charge column and timing data.</summary>
public sealed class Hole : Entity
{
    public string Label { get; set; } = string.Empty;
    public Vec3 Collar { get; set; }
    /// <summary>
    /// The surface the collar sits on: when the hole is moved, copied or pasted, the collar goes down (or up) onto it again,
    /// straight down, the angle, bearing and length as they were (<c>CollarSurfaces</c> in the engine). An elevation of the
    /// collar typed in by hand takes the hole off it (null). Null: the collar stays where it is put.
    /// </summary>
    public Guid? CollarSurfaceId { get; set; }
    /// <summary>Angle from vertical, degrees (0 = vertical).</summary>
    public double Angle { get; set; }
    /// <summary>Bearing of the hole's lean, degrees clockwise from north.</summary>
    public double Bearing { get; set; }
    /// <summary>Design length along the hole, metres (includes subdrill).</summary>
    public double Length { get; set; } = 10;
    /// <summary>Diameter, millimetres.</summary>
    public double Diameter { get; set; } = 102;
    /// <summary>Subdrill below grade, metres.</summary>
    public double Subdrill { get; set; }
    /// <summary>
    /// Elevation the hole goes down to, m: when set and not 0, the length is worked out from the collar down to it
    /// (<c>HoleLevel</c> in the engine) after every change. The collar must be above it.
    /// </summary>
    public double? ToLevel { get; set; }
    /// <summary>User-defined hole type 1..8.</summary>
    public int HoleType { get; set; } = 1;
    /// <summary>Material (rock) type 1..8.</summary>
    public int MaterialType { get; set; } = 1;
    /// <summary>Dummy holes are drawn and timed but never drilled or loaded.</summary>
    public bool IsDummy { get; set; }
    public HoleState State { get; set; } = HoleState.Designed;
    public HoleException Exceptions { get; set; }
    /// <summary>Measured (dipped) length; null until dipped.</summary>
    public double? ActualLength { get; set; }
    /// <summary>Water depth measured from the toe, metres.</summary>
    public double WaterDepth { get; set; }
    /// <summary>Measured hole temperature, °C (hot or reactive ground); null until measured.</summary>
    public double? Temperature { get; set; }
    /// <summary>Factor applied to the explosive mass by charging rules (1 = nominal).</summary>
    public double LoadingFactor { get; set; } = 1;
    public List<Deck> Decks { get; set; } = new();
    /// <summary>When set, the column was edited by hand and auto-loading leaves it alone.</summary>
    public bool ManualLoading { get; set; }
    /// <summary>
    /// The length or level of a hole of a pattern with a depth from/to was edited by hand: laying the pattern out again
    /// keeps it (until the depth of the pattern is changed).
    /// </summary>
    public bool DepthManual { get; set; }
    /// <summary>Programmed time for electronic detonators, ms; null when the hole uses pyrotechnic delays.</summary>
    public double? ElectronicTimeMs { get; set; }
    public bool ElectronicTimeManual { get; set; }
    /// <summary>Identifier of the logged electronic detonator.</summary>
    public string DetonatorId { get; set; } = string.Empty;
    /// <summary>
    /// Route number of a hole with an electronic detonator: its place in its chain of ties, in the order they were made
    /// (1 the first); null when it is on no chain. Set by the ties made, see RouteNumbers of the engine.
    /// </summary>
    public int? RouteNumber { get; set; }
    /// <summary>
    /// The letter of the chain of ties the hole is on (A the first chain, B the second…), shown before its
    /// <see cref="RouteNumber"/>; null with it.
    /// </summary>
    public string? RouteLine { get; set; }
    public Guid? RowId { get; set; }
    /// <summary>
    /// The pattern grid (or segment of one) the hole is of (<see cref="HolePattern"/>); null for a hole of its own. A copy of
    /// a hole of a pattern, pasted or copied, is of the same pattern or segment, on no node (an extra hole).
    /// </summary>
    public Guid? PatternId { get; set; }
    /// <summary>
    /// Node of the hole in its pattern grid: along the row and row number. Null in a hole of a pattern on no node: an extra
    /// hole (a copy), which a layout leaves where it is, or one the seam rule of segments put in a gap.
    /// </summary>
    public int? GridI { get; set; }
    public int? GridJ { get; set; }
    /// <summary>Where the hole was moved by hand off its node, in the grid's axes: X along the row, Y against the blast.</summary>
    public Vec3? GridOffset { get; set; }
    /// <summary>Horizon (timing deck level) for multi-horizon timing.</summary>
    public int Horizon { get; set; }
    public bool Locked { get; set; }
    /// <summary>Optional surveyed bore track (collar to toe) for as-drilled holes.</summary>
    public List<Vec3>? BoreTrack { get; set; }
    /// <summary>Surveyed as-drilled collar; null when the hole was drilled at its design collar.</summary>
    public Vec3? ActualCollar { get; set; }
    /// <summary>Measure-while-drilling log, ordered by depth.</summary>
    public List<MwdSample>? Mwd { get; set; }
    public string Notes { get; set; } = string.Empty;

    /// <summary>Firing time computed by the timing engine (ms), null when the hole is not initiated.</summary>
    [JsonIgnore] public double? FiringTimeMs { get; set; }
    /// <summary>Time the surface signal reaches the collar (ms), computed by the timing engine.</summary>
    [JsonIgnore] public double? SurfaceTimeMs { get; set; }
    /// <summary>In-hole delay used by the timing engine (ms): of the detonator and the run of the signal down its tube.</summary>
    [JsonIgnore] public double InholeDelayMs { get; set; }
    /// <summary>Firing order (1-based) computed by the timing engine.</summary>
    [JsonIgnore] public int? FiringSequence { get; set; }
    /// <summary>
    /// The charges of the hole as the timing engine fires them: runs of explosive decks between inert ones, from the collar
    /// down, each at the time of its earliest primer (SHOTPlus <c>BaseHole.CalcInholeTiming</c>).
    /// </summary>
    [JsonIgnore] public List<HoleCharge> Charges { get; set; } = new();

    public override string KindName => IsDummy ? "Dummy hole" : "Hole";

    [JsonIgnore] public Vec3 Direction => GeoMath.HoleDirection(Bearing, Angle);
    [JsonIgnore] public double EffectiveLength => ActualLength ?? Length;
    [JsonIgnore] public Vec3 Toe => PointAtDepth(EffectiveLength);
    [JsonIgnore] public Vec3 DesignToe => Collar + Direction * Length;
    /// <summary>Point on the design grade (toe minus subdrill).</summary>
    [JsonIgnore] public Vec3 GradePoint => PointAtDepth(Math.Max(0, Length - Subdrill));
    [JsonIgnore] public double RadiusM => Diameter / 2000.0;

    public Vec3 PointAtDepth(double depth)
    {
        if (BoreTrack is { Count: > 1 })
            return GeoMath.PointAlong(BoreTrack, depth, out _);
        return Collar + Direction * depth;
    }

    // ---- the hole as it is drawn: from the design or from what was drilled (ViewState.HoleBasis) ----

    /// <summary>The collar drawn: the design collar, or the surveyed one (the design collar when not surveyed).</summary>
    public Vec3 DrawnCollar(HoleBasis basis) => basis == HoleBasis.Actual ? DrawnTrackStart() : Collar;

    /// <summary>The length drawn: the design length, or the dipped one (the design length when not dipped).</summary>
    public double DrawnLength(HoleBasis basis) => basis == HoleBasis.Actual ? EffectiveLength : Length;

    /// <summary>The toe drawn: the design toe, or the toe at the dipped depth along the surveyed track (straight from the collar without one).</summary>
    public Vec3 DrawnToe(HoleBasis basis) => basis == HoleBasis.Actual ? DrawnPointAtDepth(basis, EffectiveLength) : DesignToe;

    /// <summary>A point at a depth along the hole as drawn: straight from the design collar, or along what was drilled.</summary>
    public Vec3 DrawnPointAtDepth(HoleBasis basis, double depth)
    {
        if (basis == HoleBasis.Design) return Collar + Direction * depth;
        if (BoreTrack is { Count: > 1 }) return GeoMath.PointAlong(BoreTrack, depth, out _);
        return DrawnTrackStart() + Direction * depth;
    }

    /// <summary>The track drawn, collar to toe: two points for a straight hole, the surveyed track (down to the dipped depth) for the actual one.</summary>
    public IReadOnlyList<Vec3> DrawnTrack(HoleBasis basis)
    {
        if (basis == HoleBasis.Actual && BoreTrack is { Count: > 1 } track)
        {
            var points = new List<Vec3>();
            var length = EffectiveLength;
            var along = 0.0;
            points.Add(track[0]);
            for (int i = 1; i < track.Count && along < length; i++)
            {
                var step = Vec3.Distance(track[i - 1], track[i]);
                if (along + step >= length) { points.Add(GeoMath.PointAlong(track, length, out _)); break; }
                points.Add(track[i]);
                along += step;
            }
            return points;
        }
        return new[] { DrawnCollar(basis), DrawnToe(basis) };
    }

    /// <summary>The collar of what was drilled: the surveyed collar, else the design collar.</summary>
    private Vec3 DrawnTrackStart() => ActualCollar ?? Collar;

    public override IEnumerable<Vec3> GetVertices()
    {
        yield return Collar;
        yield return Toe;
    }

    public override void Translate(Vec3 d)
    {
        Collar += d;
        if (BoreTrack != null) for (int i = 0; i < BoreTrack.Count; i++) BoreTrack[i] += d;
    }

    public override void Transform(Func<Vec3, Vec3> f)
    {
        var toe = f(DesignToe);
        Collar = f(Collar);
        SetToe(toe);
        if (BoreTrack != null) for (int i = 0; i < BoreTrack.Count; i++) BoreTrack[i] = f(BoreTrack[i]);
    }

    public override double PickDistance(Vec3 p) => Math.Max(0, Vec3.Distance2D(Collar, p) - RadiusM);

    /// <summary>The Transform tool leaves holes: a pattern has its own tool, a single hole its properties.</summary>
    public override bool Takes(SpatialTransform t) => false;

    /// <summary>Makes the hole one of its own: no longer on a pattern grid.</summary>
    public void DetachFromPattern()
    {
        PatternId = null;
        GridI = GridJ = null;
        GridOffset = null;
        DepthManual = false;
    }

    /// <summary>Re-derives angle, bearing and length from a target toe point.</summary>
    public void SetToe(Vec3 toe)
    {
        var d = toe - Collar;
        Length = d.Length;
        if (Length < 1e-9) return;
        Angle = Math.Acos(Math.Clamp(-d.Z / Length, -1, 1)) / GeoMath.Deg;
        Bearing = d.Length2D < 1e-9 ? Bearing : GeoMath.Bearing(Collar, toe);
    }

    /// <summary>Volume of the hole between two depths (m³).</summary>
    public double VolumeBetween(double top, double bottom) =>
        Math.PI * RadiusM * RadiusM * Math.Max(0, bottom - top);
}

/// <summary>
/// One charge of a hole: explosive decks with no inert deck between them, from <see cref="Top"/> to <see cref="Bottom"/> (depths),
/// and the time its earliest primer fires, ms (null: no primer in it, or the primer is not reached).
/// </summary>
public sealed record HoleCharge(IReadOnlyList<Deck> Decks, double Top, double Bottom, double? TimeMs)
{
    public double MidDepth => (Top + Bottom) / 2;
}

/// <summary>Interval of the charge column measured as depth from the collar.</summary>
public sealed class Deck
{
    public double Top { get; set; }
    public double Bottom { get; set; }
    public string? ProductId { get; set; }
    public List<Primer> Primers { get; set; } = new();
    /// <summary>
    /// The deck lengthens or shortens when the hole's length changes (the others keep theirs). Null in columns that have no
    /// marks yet (files before it, a column just built): there the longest explosive deck takes the change
    /// (<c>Charging.FitDecks</c>); once one deck of a column has a mark, a deck with none is fixed.
    /// </summary>
    public bool? Fits { get; set; }

    [JsonIgnore] public double Length => Math.Max(0, Bottom - Top);

    public Deck Clone() => new()
    {
        Top = Top,
        Bottom = Bottom,
        ProductId = ProductId,
        Fits = Fits,
        Primers = Primers.Select(p => p.Clone()).ToList()
    };
}

/// <summary>Primer: a booster with a downhole detonator at a given depth.</summary>
public sealed class Primer
{
    public double Depth { get; set; }
    public string? BoosterId { get; set; }
    public string? DetonatorId { get; set; }
    /// <summary>Overrides the detonator's nominal delay when not null.</summary>
    public double? DelayMs { get; set; }
    /// <summary>
    /// Electronic detonator: its time after the hole's, ms, used when the electronic settings fire by offsets
    /// (<see cref="ElectronicSettings.UseOffsets"/>).
    /// </summary>
    public double OffsetMs { get; set; }

    public Primer Clone() => new() { Depth = Depth, BoosterId = BoosterId, DetonatorId = DetonatorId, DelayMs = DelayMs, OffsetMs = OffsetMs };
}
