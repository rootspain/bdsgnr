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
        if (tie.DelayMs != 0 || tie.ProductId is not string pid || project.FindProduct(pid) is not { } p) return tie.DelayMs;
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
        // the solve sets the firing times past the document: the long calculation session looks at them again
        CalcService.Touch(project);
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
            // the in-hole delay stays for the labels; the hole fires with its first charge
            h.InholeDelayMs = primers.Count == 0 ? 0 : primers.Min(p => Charging.PrimerDelay(project, p.Primer));
            h.Charges = Charges(project, h);
            if (h.Charges.Where(c => c.TimeMs.HasValue).Select(c => c.TimeMs!.Value).DefaultIfEmpty(double.NaN).Min() is var first && !double.IsNaN(first))
                h.FiringTimeMs = first;
            else if (h.ElectronicTimeMs is double et)
                h.FiringTimeMs = project.Electronic.BaseTimeMs + et;
            else
                h.FiringTimeMs = h.SurfaceTimeMs + h.InholeDelayMs;
        }

        int seq = 1;
        foreach (var h in holes.Values.Where(h => h.FiringTimeMs.HasValue).OrderBy(h => h.FiringTimeMs))
            h.FiringSequence = seq++;
        foreach (var h in holes.Values.Where(h => !h.FiringTimeMs.HasValue)) h.FiringSequence = null;
    }

    /// <summary>
    /// The charges of a hole (<see cref="Charging.ExplosiveRuns"/>), each at the time of its earliest primer: an electronic one
    /// at the base time plus its programmed time (<see cref="Charging.PrimerTimes"/>), a pyrotechnic one when the surface signal
    /// gets to the hole plus its delay and the run of the signal down its tube.
    /// </summary>
    public static List<HoleCharge> Charges(BlastProject project, Hole h)
    {
        var runs = Charging.ExplosiveRuns(project, h);
        if (runs.Count == 0) return new();
        var electronic = h.ElectronicTimeMs is double et ? Charging.PrimerTimes(project, h, et) : new Dictionary<Primer, double>();
        double? PrimerTime(Primer p)
        {
            if (Charging.IsElectronic(project, p)) return electronic.TryGetValue(p, out var t) ? project.Electronic.BaseTimeMs + t : null;
            return h.SurfaceTimeMs is double s ? s + Charging.PrimerDelay(project, p) + Charging.PropagationDelay(project, p) : null;
        }
        return runs.Select(run =>
        {
            var times = run.SelectMany(d => d.Primers).Select(PrimerTime).Where(t => t.HasValue).Select(t => t!.Value).ToList();
            return new HoleCharge(run, run[0].Top, run[^1].Bottom, times.Count > 0 ? times.Min() : null);
        }).ToList();
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

    /// <summary>
    /// Turns the existing ties so the signal runs away from the initiation points (and the holes they stand on): the
    /// signal spreads from them along the ties in both directions, by the tie delays, and each tie is pointed from the
    /// end it reaches first to the other; of two ends reached at the same time (0 ms ties) from the one fewer ties away.
    /// Ties no initiation point reaches, and ties with both ends alike, keep their direction; no tie is pointed into an
    /// initiation point. With <paramref name="only"/> just those ties are turned (the signal still runs along all of
    /// them). Returns the number of ties turned.
    /// </summary>
    public static int OrientTiesFromInitiation(BlastProject project, ISet<Guid>? only = null)
    {
        var arrival = Arrivals(project);
        var ipIds = project.Entities.OfType<InitiationPoint>().Select(i => i.Id).ToHashSet();
        int turned = 0;
        foreach (var t in project.Ties.ToList())
        {
            if (ipIds.Contains(t.FromId) || (only != null && !only.Contains(t.Id))) continue;
            var into = ipIds.Contains(t.ToId);
            if (!into && (!arrival.TryGetValue(t.FromId, out var a) || !arrival.TryGetValue(t.ToId, out var b) || b.CompareTo(a) >= 0)) continue;
            FlipTies(new[] { t });
            turned++;
        }
        return turned;
    }

    /// <summary>The ties the signal of an initiation point reaches, whichever way they are drawn.</summary>
    public static HashSet<Guid> ReachedTies(BlastProject project)
    {
        var arrival = Arrivals(project);
        return project.Ties.Where(t => arrival.ContainsKey(t.FromId) && arrival.ContainsKey(t.ToId)).Select(t => t.Id).ToHashSet();
    }

    /// <summary>
    /// When the signal of the initiation points gets to each hole and point it reaches, spreading along the ties in both
    /// directions, and through how many ties.
    /// </summary>
    private static Dictionary<Guid, (double Time, int Ties)> Arrivals(BlastProject project)
    {
        var arrival = new Dictionary<Guid, (double Time, int Ties)>();
        var ties = project.Ties.ToList();
        var ips = project.Entities.OfType<InitiationPoint>().ToList();
        if (ties.Count == 0 || ips.Count == 0) return arrival;
        var adj = new Dictionary<Guid, List<(Guid To, double Delay)>>();
        void Edge(Guid a, Guid b, double d)
        {
            if (!adj.TryGetValue(a, out var list)) adj[a] = list = new();
            list.Add((b, d));
        }
        foreach (var t in ties)
        {
            var d = TieDelay(project, t);
            Edge(t.FromId, t.ToId, d);
            Edge(t.ToId, t.FromId, d);
        }
        var pq = new PriorityQueue<Guid, (double Time, int Ties)>();
        void Seed(Guid id, (double Time, int Ties) at)
        {
            if (arrival.TryGetValue(id, out var cur) && cur.CompareTo(at) <= 0) return;
            arrival[id] = at;
            pq.Enqueue(id, at);
        }
        foreach (var ip in ips)
        {
            var t0 = ip.StartTimeMs + ip.LeadInDelayMs;
            Seed(ip.Id, (t0, 0));
            if (ip.HoleId is Guid hid) Seed(hid, (t0, 0));
        }
        while (pq.TryDequeue(out var node, out var at))
        {
            if (arrival[node].CompareTo(at) < 0 || !adj.TryGetValue(node, out var edges)) continue;
            foreach (var (to, delay) in edges) Seed(to, (at.Time + delay, at.Ties + 1));
        }
        return arrival;
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

    /// <summary>
    /// A programmed time as the detonators take it: whole milliseconds, or tenths of a millisecond when allowed; negative times are clamped to 0.
    /// </summary>
    public static double RoundTime(double ms, bool allowSubMs) => Math.Round(ms, allowSubMs ? 1 : 0, MidpointRounding.AwayFromZero);

    /// <summary>Programs the time of a hole, rounded (<see cref="RoundTime"/>); the one place a programmed time is rounded.</summary>
    public static void SetTime(Hole h, double ms, bool manual = true, bool allowSubMs = false)
    {
        h.ElectronicTimeMs = Math.Max(0, RoundTime(ms, allowSubMs));
        h.ElectronicTimeManual = manual;
    }

    /// <summary>Times holes in the given order from a start time with a fixed increment (Row set tool).</summary>
    public static void RowSet(IReadOnlyList<Hole> orderedHoles, double startMs, double incrementMs, bool allowSubMs = false)
    {
        for (int i = 0; i < orderedHoles.Count; i++) SetTime(orderedHoles[i], startMs + i * incrementMs, false, allowSubMs);
    }

    /// <summary>Times holes by their distance from a line (Vee / timing line tool): t = start + distance × ms per metre.</summary>
    public static void TimeFromLine(IEnumerable<Hole> holes, Vec3 a, Vec3 b, double startMs, double msPerMetre, bool allowSubMs = false)
    {
        foreach (var h in holes)
        {
            var (c, _) = GeoMath.ClosestOnSegment2D(a, b, h.Collar);
            var d = Vec3.Distance2D(c, h.Collar);
            SetTime(h, startMs + d * msPerMetre, false, allowSubMs);
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
    public static int CopyTimesBetweenHorizons(IReadOnlyList<Hole> holes, int fromHorizon, int toHorizon, double offsetMs, bool allowSubMs = false)
    {
        var src = holes.Where(h => h.Horizon == fromHorizon && h.ElectronicTimeMs.HasValue).ToList();
        int n = 0;
        foreach (var h in holes.Where(h => h.Horizon == toHorizon))
        {
            var best = src.OrderBy(s => Vec3.Distance2D(s.Collar, h.Collar)).FirstOrDefault();
            if (best == null) continue;
            SetTime(h, best.ElectronicTimeMs!.Value + offsetMs, false, allowSubMs);
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
            if (h.FiringTimeMs is double t) SetTime(h, t, false, project.Electronic.AllowSubMs);
            foreach (var (p, _) in Charging.Primers(h)) { p.DetonatorId = electronicDet.Id; p.DelayMs = null; }
            n++;
        }
        return n;
    }

    // ---- analysis --------------------------------------------------------

    /// <summary>
    /// Milliseconds of the blast shown per millisecond, to show <paramref name="durationMs"/> of it in
    /// <paramref name="seconds"/>; 0 seconds is the blast's own clock.
    /// </summary>
    public static double PlaybackRate(double durationMs, double seconds) => seconds > 0 ? durationMs / (seconds * 1000) : 1;

    public sealed record EnvelopePoint(double TimeMs, int Holes, double ChargeKg);

    /// <summary>Holes and charge firing within a sliding window starting at each firing time (Time envelope).</summary>
    public static List<EnvelopePoint> TimeEnvelope(BlastProject project, double windowMs)
    {
        var s = CalcService.Live(project) ?? CalcService.Session(project);
        return TimeEnvelope(project, windowMs, h => Charging.Calculated(s, "hole.chargeMass", h, 0));
    }

    /// <summary>
    /// Time envelope with the charge of each hole given (the calculation engine passes its cached masses). Each charge of a
    /// hole is an event of its own, at its time and with its share of the hole's mass (<see cref="Charging.ChargeEvents"/>);
    /// "holes" counts the charges.
    /// </summary>
    public static List<EnvelopePoint> TimeEnvelope(BlastProject project, double windowMs, Func<Hole, double> charge)
    {
        var events = project.Holes.Where(h => h.FiringTimeMs.HasValue && !h.IsDummy)
            .SelectMany(h => Charging.ChargeEvents(project, h, charge(h)))
            .Select(e => (T: e.TimeMs, Q: e.Kg))
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
