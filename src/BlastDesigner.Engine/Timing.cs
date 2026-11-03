using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Firing-time solver and timing edit operations (pyrotechnic tie-ups and electronic programming).</summary>
public static class Timing
{
    /// <summary>
    /// Delay of a tie: its own when set (user defined), else by its product: a connector or cord relay its nominal delay,
    /// detonating cord the time the cord takes from end to end (length over its VOD), electric wiring, harness wire and
    /// electronic connections none (the delay is in the hole or programmed). The ends are looked up when not given.
    /// </summary>
    public static double TieDelay(BlastProject project, TieEntity tie, Vec3? from = null, Vec3? to = null)
    {
        if (tie.DelayMs != 0 || tie.ProductId is not Guid pid || project.FindProduct(pid) is not { } p) return tie.DelayMs;
        switch (p.Kind)
        {
            case ProductKind.DetonatingCord:
                if (p.Vod <= 0) return 0;
                var a = from ?? PositionOf(project, tie.FromId);
                var b = to ?? PositionOf(project, tie.ToId);
                return a is Vec3 pa && b is Vec3 pb ? Vec3.Distance(pa, pb) / p.Vod * 1000 : 0;
            case ProductKind.ElectricDetonator or ProductKind.HarnessWire or ProductKind.ElectronicDetonator:
                return 0;
            default:
                return p.DelayMs;
        }
    }

    /// <summary>
    /// Computes surface arrival, in-hole delay and firing time of every hole.
    /// Surface signals start at initiation points (and the hole one is bound to) and travel along ties (earliest arrival wins);
    /// electronic holes fire at their programmed time.
    /// </summary>
    public static void Solve(BlastProject project)
    {
        var holes = project.Holes.ToDictionary(h => h.Id);
        var ips = project.Entities.OfType<InitiationPoint>().ToDictionary(i => i.Id);
        var ties = project.Ties.ToList();

        // Resolve tie end positions for drawing (through a table: a search of the whole project per tie end was slow
        // for a blast over a survey).
        var positions = new Dictionary<Guid, Vec3>();
        foreach (var h in holes.Values) positions[h.Id] = h.Collar;
        foreach (var ip in ips.Values) positions[ip.Id] = ip.Position;
        foreach (var t in ties)
        {
            if (positions.TryGetValue(t.FromId, out var f)) t.FromPos = f;
            if (positions.TryGetValue(t.ToId, out var e)) t.ToPos = e;
        }

        var adj = new Dictionary<Guid, List<(Guid To, double Delay)>>();
        foreach (var t in ties)
        {
            if (!adj.TryGetValue(t.FromId, out var list)) adj[t.FromId] = list = new();
            list.Add((t.ToId, TieDelay(project, t, t.FromPos, t.ToPos)));
        }

        var arrival = new Dictionary<Guid, double>();
        var pq = new PriorityQueue<Guid, double>();
        foreach (var ip in ips.Values)
        {
            var t0 = ip.StartTimeMs + ip.LeadInDelayMs;
            arrival[ip.Id] = t0;
            pq.Enqueue(ip.Id, t0);
            // a point standing on a hole starts it: they are one node of the tie-up, the ties go on from the hole
            ip.SetHoleLabel(null);
            if (ip.HoleId is Guid hid && holes.TryGetValue(hid, out var on))
            {
                ip.SetHoleLabel(on.Label);
                if (!arrival.TryGetValue(hid, out var cur) || t0 < cur) { arrival[hid] = t0; pq.Enqueue(hid, t0); }
            }
        }
        while (pq.TryDequeue(out var node, out var time))
        {
            if (arrival.TryGetValue(node, out var known) && known < time) continue;
            if (!adj.TryGetValue(node, out var edges)) continue;
            foreach (var (to, delay) in edges)
            {
                var t = time + delay;
                if (!arrival.TryGetValue(to, out var cur) || t < cur)
                {
                    arrival[to] = t;
                    pq.Enqueue(to, t);
                }
            }
        }

        foreach (var h in holes.Values)
        {
            h.SurfaceTimeMs = arrival.TryGetValue(h.Id, out var s) ? s : null;
            var primers = Charging.Primers(h).ToList();
            h.InholeDelayMs = primers.Count == 0 ? 0 : primers.Min(p => Charging.PrimerDelay(project, p.Primer));
            if (h.ElectronicTimeMs is double et)
                h.FiringTimeMs = project.Electronic.BaseTimeMs + et;
            else
                h.FiringTimeMs = h.SurfaceTimeMs + h.InholeDelayMs;
        }

        int seq = 1;
        foreach (var h in holes.Values.Where(h => h.FiringTimeMs.HasValue).OrderBy(h => h.FiringTimeMs))
            h.FiringSequence = seq++;
        foreach (var h in holes.Values.Where(h => !h.FiringTimeMs.HasValue)) h.FiringSequence = null;
    }

    public static Vec3? PositionOf(BlastProject project, Guid id) => project.FindEntity(id) switch
    {
        Hole h => h.Collar,
        InitiationPoint ip => ip.Position,
        _ => null
    };

    /// <summary>Adds a surface tie between two nodes, replacing an existing tie between them.</summary>
    public static TieEntity Connect(BlastProject project, Guid from, Guid to, Product? connector, double? delayOverride = null)
    {
        project.Entities.RemoveAll(e => e is TieEntity t && ((t.FromId == from && t.ToId == to) || (t.FromId == to && t.ToId == from)));
        var tie = new TieEntity
        {
            FromId = from,
            ToId = to,
            ProductId = connector?.Id,
            DelayMs = delayOverride ?? 0,
            FromPos = PositionOf(project, from) ?? Vec3.Zero,
            ToPos = PositionOf(project, to) ?? Vec3.Zero
        };
        project.Add(tie, project.GetOrCreateLayer(StandardLayers.Ties, 0xFFDC2626, "Design"));
        return tie;
    }

    public static void FlipTies(IEnumerable<TieEntity> ties)
    {
        foreach (var t in ties)
        {
            (t.FromId, t.ToId) = (t.ToId, t.FromId);
            (t.FromPos, t.ToPos) = (t.ToPos, t.FromPos);
        }
    }

    /// <summary>Removes every tie touching the given holes (or all ties when none are given).</summary>
    public static int DeleteTieUp(BlastProject project, IEnumerable<Guid>? holeIds = null)
    {
        var set = holeIds?.ToHashSet();
        return project.Entities.RemoveAll(e => e is TieEntity t && (set == null || set.Contains(t.FromId) || set.Contains(t.ToId)));
    }

    /// <summary>
    /// Automatic echelon tie-up: holes along each row are chained with the hole connector and
    /// rows are chained at the hole nearest the initiation point with the row connector.
    /// </summary>
    public static int AutoTieUp(BlastProject project, IReadOnlyList<Hole> holes, InitiationPoint ip, Product holeConnector, Product rowConnector, double rowTolerance)
    {
        var holeSet = holes.ToHashSet();
        var rows = project.Rows
            .Select(r => r.HoleIds.Select(project.FindEntity).OfType<Hole>().Where(holeSet.Contains).ToList())
            .Where(r => r.Count > 0).ToList();
        if (rows.Count == 0)
            rows = HoleDesign.AutoRows(holes, project.Header.BlastOrientation, rowTolerance)
                .Select(r => r.HoleIds.Select(id => holes.First(h => h.Id == id)).ToList()).ToList();

        // Front row first: order rows by distance to the initiation point.
        rows = rows.OrderBy(r => r.Min(h => Vec3.Distance2D(h.Collar, ip.Position))).ToList();
        DeleteTieUp(project, holes.Select(h => h.Id));
        int count = 0;
        Hole? prevAnchor = null;
        foreach (var row in rows)
        {
            var anchor = row.OrderBy(h => Vec3.Distance2D(h.Collar, ip.Position)).First();
            // a point standing on the anchor starts it without a tie
            if (prevAnchor == null) { if (ip.HoleId != anchor.Id) { Connect(project, ip.Id, anchor.Id, null, 0); count++; } }
            else { Connect(project, prevAnchor.Id, anchor.Id, rowConnector); count++; }
            var idx = row.IndexOf(anchor);
            for (int i = idx; i < row.Count - 1; i++) { Connect(project, row[i].Id, row[i + 1].Id, holeConnector); count++; }
            for (int i = idx; i > 0; i--) { Connect(project, row[i].Id, row[i - 1].Id, holeConnector); count++; }
            prevAnchor = anchor;
        }
        return count;
    }

    // ---- electronic programming -------------------------------------------

    public static void SetTime(Hole h, double ms, bool manual = true)
    {
        h.ElectronicTimeMs = Math.Max(0, ms);
        h.ElectronicTimeManual = manual;
    }

    /// <summary>Times holes in the given order from a start time with a fixed increment (Row set tool).</summary>
    public static void RowSet(IReadOnlyList<Hole> orderedHoles, double startMs, double incrementMs)
    {
        for (int i = 0; i < orderedHoles.Count; i++) SetTime(orderedHoles[i], startMs + i * incrementMs, false);
    }

    /// <summary>Times holes by their distance from a line (Vee / timing line tool): t = start + distance × ms per metre.</summary>
    public static void TimeFromLine(IEnumerable<Hole> holes, Vec3 a, Vec3 b, double startMs, double msPerMetre, double roundToMs)
    {
        foreach (var h in holes)
        {
            var (c, _) = GeoMath.ClosestOnSegment2D(a, b, h.Collar);
            var d = Vec3.Distance2D(c, h.Collar);
            var t = startMs + d * msPerMetre;
            if (roundToMs > 0) t = Math.Round(t / roundToMs) * roundToMs;
            SetTime(h, t, false);
        }
    }

    /// <summary>Shifts electronic times of the holes by a constant (Add/Subtract time).</summary>
    public static void AddTime(IEnumerable<Hole> holes, double deltaMs)
    {
        foreach (var h in holes) if (h.ElectronicTimeMs is double t) h.ElectronicTimeMs = Math.Max(0, t + deltaMs);
    }

    /// <summary>Scales the intervals between holes relative to the earliest one (Adjust intervals).</summary>
    public static void ScaleIntervals(IReadOnlyList<Hole> holes, double factor)
    {
        var timed = holes.Where(h => h.ElectronicTimeMs.HasValue).ToList();
        if (timed.Count == 0) return;
        var min = timed.Min(h => h.ElectronicTimeMs!.Value);
        foreach (var h in timed) h.ElectronicTimeMs = Math.Round(min + (h.ElectronicTimeMs!.Value - min) * factor);
    }

    /// <summary>Keeps the current firing order but re-spaces holes with a fixed increment (Adjust increments).</summary>
    public static void Resequence(IReadOnlyList<Hole> holes, double incrementMs)
    {
        var timed = holes.Where(h => h.FiringTimeMs.HasValue || h.ElectronicTimeMs.HasValue)
            .OrderBy(h => h.ElectronicTimeMs ?? h.FiringTimeMs).ToList();
        if (timed.Count == 0) return;
        var start = timed[0].ElectronicTimeMs ?? timed[0].FiringTimeMs ?? 0;
        RowSet(timed, start, incrementMs);
    }

    /// <summary>Copies times from holes of one horizon to the closest holes of another.</summary>
    public static int CopyTimesBetweenHorizons(IReadOnlyList<Hole> holes, int fromHorizon, int toHorizon, double offsetMs)
    {
        var src = holes.Where(h => h.Horizon == fromHorizon && h.ElectronicTimeMs.HasValue).ToList();
        int n = 0;
        foreach (var h in holes.Where(h => h.Horizon == toHorizon))
        {
            var best = src.OrderBy(s => Vec3.Distance2D(s.Collar, h.Collar)).FirstOrDefault();
            if (best == null) continue;
            SetTime(h, best.ElectronicTimeMs!.Value + offsetMs, false);
            n++;
        }
        return n;
    }

    public static void Deprogram(IEnumerable<Hole> holes, bool onlyManual)
    {
        foreach (var h in holes)
            if (!onlyManual || h.ElectronicTimeManual)
            {
                h.ElectronicTimeMs = null;
                h.ElectronicTimeManual = false;
            }
    }

    /// <summary>Converts pyrotechnic holes to electronic: programmed time = current firing time, detonators replaced.</summary>
    public static int ConvertToElectronic(BlastProject project, IEnumerable<Hole> holes, Product electronicDet)
    {
        Solve(project);
        int n = 0;
        foreach (var h in holes)
        {
            if (h.FiringTimeMs is double t) SetTime(h, t, false);
            foreach (var (p, _) in Charging.Primers(h)) { p.DetonatorId = electronicDet.Id; p.DelayMs = null; }
            n++;
        }
        return n;
    }

    // ---- analysis --------------------------------------------------------

    public sealed record EnvelopePoint(double TimeMs, int Holes, double ChargeKg);

    /// <summary>Holes and charge firing within a sliding window starting at each firing time (Time envelope).</summary>
    public static List<EnvelopePoint> TimeEnvelope(BlastProject project, double windowMs)
    {
        var events = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy)
            .Select(h => (T: h.FiringTimeMs!.Value, Q: Charging.ExplosiveMass(project, h)))
            .OrderBy(e => e.T).ToList();
        var result = new List<EnvelopePoint>();
        int j = 0;
        double q = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0) q -= events[i - 1].Q;
            if (j < i) { j = i; q = 0; }
            while (j < events.Count && events[j].T < events[i].T + windowMs) { q += events[j].Q; j++; }
            result.Add(new EnvelopePoint(events[i].T, j - i, q));
        }
        return result;
    }

    public static double MaxInstantaneousCharge(BlastProject project, double windowMs) =>
        TimeEnvelope(project, windowMs).Select(e => e.ChargeKg).DefaultIfEmpty(0).Max();

    public sealed record HoleTimingAnalysis(Hole Hole, double? ReliefMsPerM, double? InitiationBearing, double? MovementBearing, int SameWindowCount);

    /// <summary>
    /// Per-hole burden relief (ms/m to the earliest-firing neighbour that fired before it), direction of initiation
    /// (gradient of the firing-time field) and first movement (towards earlier-firing neighbours).
    /// </summary>
    public static List<HoleTimingAnalysis> Analyse(BlastProject project, double neighbourRadius, double windowMs)
    {
        var holes = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy).ToList();
        var result = new List<HoleTimingAnalysis>();
        foreach (var h in holes)
        {
            var t = h.FiringTimeMs!.Value;
            var nb = holes.Where(o => o != h && Vec3.Distance2D(o.Collar, h.Collar) <= neighbourRadius).ToList();
            double? relief = null;
            Vec3 move = Vec3.Zero;
            foreach (var o in nb)
            {
                var dt = t - o.FiringTimeMs!.Value;
                var d = Vec3.Distance2D(o.Collar, h.Collar);
                if (dt > 0 && d > 0)
                {
                    var r = dt / d;
                    relief = relief is null ? r : Math.Min(relief.Value, r);
                    move += (o.Collar - h.Collar).Normalized() * (1.0 / dt);
                }
            }
            double? init = null;
            if (nb.Count >= 2)
            {
                // Least-squares plane t = a·x + b·y + c around the hole.
                double sxx = 0, sxy = 0, syy = 0, sxt = 0, syt = 0;
                foreach (var o in nb)
                {
                    var dx = o.Collar.X - h.Collar.X;
                    var dy = o.Collar.Y - h.Collar.Y;
                    var dt = o.FiringTimeMs!.Value - t;
                    sxx += dx * dx; sxy += dx * dy; syy += dy * dy; sxt += dx * dt; syt += dy * dt;
                }
                var det = sxx * syy - sxy * sxy;
                if (Math.Abs(det) > 1e-9)
                {
                    var a = (sxt * syy - syt * sxy) / det;
                    var b = (syt * sxx - sxt * sxy) / det;
                    if (Math.Abs(a) + Math.Abs(b) > 1e-12) init = GeoMath.NormalizeBearing(Math.Atan2(a, b) / GeoMath.Deg);
                }
            }
            double? mv = move.Length2D > 1e-12 ? GeoMath.NormalizeBearing(Math.Atan2(move.X, move.Y) / GeoMath.Deg) : null;
            var same = holes.Count(o => Math.Abs(o.FiringTimeMs!.Value - t) < windowMs);
            result.Add(new HoleTimingAnalysis(h, relief, init, mv, same));
        }
        return result;
    }
}
