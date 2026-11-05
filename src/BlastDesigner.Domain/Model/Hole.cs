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
    /// <summary>Programmed time for electronic detonators, ms; null when the hole uses pyrotechnic delays.</summary>
    public double? ElectronicTimeMs { get; set; }
    public bool ElectronicTimeManual { get; set; }
    /// <summary>Identifier of the logged electronic detonator.</summary>
    public string DetonatorId { get; set; } = string.Empty;
    public Guid? RowId { get; set; }
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
    /// <summary>In-hole delay used by the timing engine (ms).</summary>
    [JsonIgnore] public double InholeDelayMs { get; set; }
    /// <summary>Firing order (1-based) computed by the timing engine.</summary>
    [JsonIgnore] public int? FiringSequence { get; set; }

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

/// <summary>Interval of the charge column measured as depth from the collar.</summary>
public sealed class Deck
{
    public double Top { get; set; }
    public double Bottom { get; set; }
    public string? ProductId { get; set; }
    public List<Primer> Primers { get; set; } = new();

    [JsonIgnore] public double Length => Math.Max(0, Bottom - Top);

    public Deck Clone() => new()
    {
        Top = Top,
        Bottom = Bottom,
        ProductId = ProductId,
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

    public Primer Clone() => new() { Depth = Depth, BoosterId = BoosterId, DetonatorId = DetonatorId, DelayMs = DelayMs };
}
