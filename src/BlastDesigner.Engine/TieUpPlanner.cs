using BlastDesigner.Domain.Geometry;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>Layouts of a non-electric surface tie-up the planner builds and compares.</summary>
public enum TieScheme
{
    /// <summary>Row by row: a trunk across the rows under the initiation point, each row tied both ways from it.</summary>
    Rows,
    /// <summary>V from the front: the front row both ways from the point, every hole behind from the hole in front of it.</summary>
    VFront,
    /// <summary>Echelon from the corner: the trunk along the side of the block nearest the point, the rows tied from it one way.</summary>
    Echelon,
    /// <summary>Diagonal: the front row one way from the corner, every hole behind from the hole in front of it one step back.</summary>
    Diagonal,
    /// <summary>V on diagonals: the front row both ways from the point, the holes behind from the diagonal towards the middle.</summary>
    VDiagonal,
    /// <summary>Radial fan: every hole from a nearer one, the delay picked so the time grows with the distance from the point.</summary>
    Fan
}

/// <summary>What a tie does in a planned tie-up.</summary>
public enum TieRole { Lead, InRow, BetweenRows, Fan, Fill }

/// <summary>One tie of a plan; ties of one connector block (several tubes from one block) share <see cref="Block"/>.</summary>
public sealed record PlannedTie(Guid From, Guid To, Product? Product, TieRole Role, double LengthM, int Block = -1);

/// <summary>A warning of a plan: an English format and its values (the window translates it).</summary>
public sealed record TieUpWarning(string Format, params object[] Args);

/// <summary>What the planner is asked for.</summary>
public sealed class TieUpOptions
{
    public required InitiationPoint Start { get; init; }
    /// <summary>Connector between holes of a row.</summary>
    public Product? InRow { get; set; }
    /// <summary>Connector between rows.</summary>
    public Product? BetweenRows { get; set; }
    /// <summary>The surface connectors that may be used: the fan picks delays from them, the lead lengths come from them.</summary>
    public List<Product> Connectors { get; set; } = new();
    /// <summary>The layouts to build; null: all of them.</summary>
    public IReadOnlyCollection<TieScheme>? Schemes { get; set; }
    /// <summary>Take for each tie the shortest lead of the same delay that reaches it (connectors with a known lead length).</summary>
    public bool PickLeadLength { get; set; } = true;
    /// <summary>
    /// Holes one connector block feeds along a row (1: one block a hole). With more the holes of a block get the surface
    /// signal together and their order comes from in-hole delays stepped by the block delay over the count.
    /// </summary>
    public int HolesPerBlock { get; set; } = 1;
    /// <summary>The window of the 8 ms rule, ms.</summary>
    public double WindowMs { get; set; } = 8;
    /// <summary>Neighbours closer than this firing within the window count as overlaps; 0: 1.5 spacings.</summary>
    public double NeighbourRadius { get; set; }
    /// <summary>A hole firing this close to a tie before the signal passed it may cut it off; 0: 2 burdens.</summary>
    public double CutOffRadius { get; set; }
    /// <summary>Fan delay per metre from the point, ms/m; 0: the in-row delay over the spacing.</summary>
    public double FanMsPerM { get; set; }
    /// <summary>Charge of a hole, kg, for the instantaneous charge; null: holes are counted.</summary>
    public Func<Hole, double>? Charge { get; set; }
    public double WeightEconomy { get; set; } = 1;
    public double WeightSeismic { get; set; } = 1;
    public double WeightReliability { get; set; } = 1;
}

/// <summary>A planned tie-up with what it costs and how it fires.</summary>
public sealed class TieUpPlan
{
    public TieScheme Scheme { get; init; }
    public List<PlannedTie> Ties { get; } = new();
    /// <summary>In-hole delay added to holes of a block fed with others (primer delay overrides), ms.</summary>
    public Dictionary<Guid, double> ExtraInHoleMs { get; } = new();
    public Dictionary<Guid, double> Surface { get; } = new();
    public Dictionary<Guid, double> Firing { get; } = new();
    public int Connectors { get; set; }
    public Dictionary<Product, int> ByProduct { get; } = new();
    public double Cost { get; set; }
    public double LengthM { get; set; }
    public int MaxHolesInWindow { get; set; }
    public double MaxCharge { get; set; }
    public int Overlaps { get; set; }
    /// <summary>Time the surface signal takes to reach the last hole, ms.</summary>
    public double NetworkMs { get; set; }
    public double FirstFiringMs { get; set; }
    public double LastFiringMs { get; set; }
    /// <summary>Holes the surface signal reaches after the first hole has fired.</summary>
    public int LateHoles { get; set; }
    public int CutOffRisk { get; set; }
    /// <summary>
    /// The least distance, m, from a tie to a hole that has fired before the signal passed the tie (how far the signal
    /// runs ahead of the blasting); null when the whole network is laid out before the first hole fires.
    /// </summary>
    public double? FrontGapM { get; set; }
    public int ShortLeads { get; set; }
    public int Nominals { get; set; }
    public int Unreached { get; set; }
    public double Score { get; set; }
    public bool Best { get; set; }
    /// <summary>
    /// Some holes may not fire: not reached, or fed by a tie near a hole that fires before the signal passes it (cut-off).
    /// Holes reached after the first hole has fired are not a risk while the signal runs far enough ahead of the
    /// blasting (<see cref="FrontGapM"/> over the cut-off radius): the tie-up of a large blast commonly works so.
    /// </summary>
    public bool Risky => Unreached > 0 || CutOffRisk > 0;
    public List<TieUpWarning> Warnings { get; } = new();
}

/// <summary>
/// Automatic non-electric tie-up: builds the tie-ups of several layouts over the rows of the holes on a light model
/// (positions and in-hole delays, no copy of the project), measures each (connectors, cost, holes and charge in the
/// 8 ms window, neighbour overlaps, network time against the in-hole delay, cut-off risk, leads too short) and scores
/// them by the weights of the goals; <see cref="Apply"/> writes the chosen one to the project.
/// </summary>
public static class TieUpPlanner
{
    public static readonly TieScheme[] AllSchemes = Enum.GetValues<TieScheme>();

    /// <summary>The connector of the delay nearest the given one (the shorter of two alike).</summary>
    public static Product? Nearest(IEnumerable<Product> connectors, double targetMs) =>
        connectors.Where(c => c.DelayMs > 0).OrderBy(c => Math.Abs(c.DelayMs - targetMs)).ThenBy(c => c.DelayMs).FirstOrDefault();

    /// <summary>The surface connectors of the active resources.</summary>
    public static List<Product> SurfaceConnectors(BlastProject project) =>
        project.ActiveProducts.Where(p => p.Kind == ProductKind.SurfaceConnector && !p.Deleted).OrderBy(p => p.DelayMs).ThenBy(p => p.LeadLengthM).ToList();

    /// <summary>Plans every asked layout, scores them and marks the best.</summary>
    public static List<TieUpPlan> Plan(BlastProject project, IReadOnlyList<Hole> holes, TieUpOptions options)
    {
        var model = new Model(project, holes, options);
        var plans = (options.Schemes ?? AllSchemes).Distinct().Select(model.Build).ToList();
        Score(plans, options);
        return plans;
    }

    /// <summary>Plans one layout (scored alone).</summary>
    public static TieUpPlan Build(BlastProject project, IReadOnlyList<Hole> holes, TieUpOptions options, TieScheme scheme)
    {
        var plan = new Model(project, holes, options).Build(scheme);
        Score(new List<TieUpPlan> { plan }, options);
        return plan;
    }

    /// <summary>
    /// Replaces the ties of the holes with the plan and sets the in-hole delays of the holes of shared blocks on their
    /// primers. Returns the ties made.
    /// </summary>
    public static int Apply(BlastProject project, IReadOnlyList<Hole> holes, TieUpPlan plan)
    {
        Timing.DeleteTieUp(project, holes.Select(h => h.Id));
        foreach (var t in plan.Ties) Timing.Connect(project, t.From, t.To, t.Product, t.Role == TieRole.Lead ? 0 : null);
        foreach (var (id, extra) in plan.ExtraInHoleMs)
        {
            if (extra == 0 || project.FindEntity(id) is not Hole h) continue;
            foreach (var (primer, _) in Charging.Primers(h).ToList())
                if (!Charging.IsElectronic(project, primer)) primer.DelayMs = Charging.PrimerDelay(project, primer) + extra;
        }
        return plan.Ties.Count;
    }

    private static void Score(List<TieUpPlan> plans, TieUpOptions o)
    {
        if (plans.Count == 0) return;
        static double Econ(TieUpPlan p) => p.Cost > 0 ? p.Cost : p.Connectors;
        static double Seis(TieUpPlan p) => (p.MaxCharge > 0 ? p.MaxCharge : p.MaxHolesInWindow) * (1 + p.Overlaps / (double)Math.Max(1, p.Firing.Count));
        static double Rel(TieUpPlan p) => 1 + 10 * p.CutOffRisk + 0.1 * p.LateHoles + p.ShortLeads + 10 * p.Unreached;
        double me = Math.Max(1e-9, plans.Max(Econ)), ms = Math.Max(1e-9, plans.Max(Seis)), mr = Math.Max(1e-9, plans.Max(Rel));
        foreach (var p in plans)
            p.Score = o.WeightEconomy * Econ(p) / me + o.WeightSeismic * Seis(p) / ms + o.WeightReliability * Rel(p) / mr;
        // a variant some holes of which may not fire is never the best while another fires them all
        var best = plans.OrderBy(p => p.Risky).ThenBy(p => p.Unreached).ThenBy(p => p.CutOffRisk).ThenBy(p => p.Score).First();
        foreach (var p in plans) p.Best = p == best;
    }

    /// <summary>The holes in a frame of the blast (U along the rows, V away from the face), their rows and in-hole delays.</summary>
    private sealed class Model
    {
        private readonly BlastProject _project;
        private readonly TieUpOptions _o;
        private readonly Hole[] _h;
        private readonly double[] _u, _dIp, _inhole;
        /// <summary>The surface block of each hole's combination detonator: the signal leaves the hole that much later; 0 without one.</summary>
        private readonly double[] _block;
        private readonly List<List<int>> _rows;
        private readonly Dictionary<int, int> _rowOf = new();
        private readonly double _s, _b, _t0;

        public Model(BlastProject project, IReadOnlyList<Hole> holes, TieUpOptions options)
        {
            _project = project;
            _o = options;
            _h = holes.ToArray();
            var ip = options.Start.Position;
            _dIp = _h.Select(h => Vec3.Distance2D(h.Collar, ip)).ToArray();
            _inhole = _h.Select(h =>
            {
                var primers = Charging.Primers(h).Where(x => !Charging.IsElectronic(project, x.Primer)).ToList();
                return primers.Count == 0 ? 0 : primers.Min(x => Charging.PrimerDelay(project, x.Primer) + Charging.PropagationDelay(project, x.Primer));
            }).ToArray();
            _block = _h.Select(h => Timing.HoleSurfaceDelay(project, h) ?? 0).ToArray();
            _t0 = options.Start.StartTimeMs + options.Start.LeadInDelayMs;

            var index = new Dictionary<Guid, int>();
            for (int i = 0; i < _h.Length; i++) index[_h[i].Id] = i;
            var rows = project.Rows.Select(r => r.HoleIds.Where(index.ContainsKey).Select(id => index[id]).Distinct().ToList()).Where(r => r.Count > 0).ToList();
            var covered = rows.SelectMany(r => r).ToHashSet();
            if (covered.Count < _h.Length || rows.SelectMany(r => r).Count() != covered.Count)
            {
                var tol = (project.Design.Burden > 0 ? project.Design.Burden : 4) / 2;
                rows = HoleDesign.AutoRows(_h, project.Header.BlastOrientation, tol).Select(r => r.HoleIds.Where(index.ContainsKey).Select(id => index[id]).ToList()).Where(r => r.Count > 0).ToList();
            }

            // the direction of the rows from the rows themselves (the orientation of the blast need not follow them):
            // the long axis of each row, doubled angles so the two ways of a row agree, weighted by the length
            double c2 = 0, s2 = 0;
            foreach (var r in rows.Where(r => r.Count >= 2))
            {
                var pts = r.Select(i => _h[i].Collar).ToList();
                double mx = pts.Average(q => q.X), my = pts.Average(q => q.Y), sxx = 0, syy = 0, sxy = 0;
                foreach (var q in pts) { sxx += (q.X - mx) * (q.X - mx); syy += (q.Y - my) * (q.Y - my); sxy += (q.X - mx) * (q.Y - my); }
                // the principal axis of the row: angle 2θ = atan2(2sxy, sxx - syy), its strength the spread along it
                var len = Math.Sqrt((sxx - syy) * (sxx - syy) + 4 * sxy * sxy);
                if (len <= 1e-9) continue;
                c2 += (sxx - syy);
                s2 += 2 * sxy;
            }
            Vec3 along;
            if (c2 * c2 + s2 * s2 > 1e-12)
            {
                var th = Math.Atan2(s2, c2) / 2;
                along = new Vec3(Math.Cos(th), Math.Sin(th), 0);
            }
            else
            {
                var dir = GeoMath.HoleDirection(project.Header.BlastOrientation, 90);
                var fwd = new Vec3(dir.X, dir.Y, 0).Normalized();
                along = new Vec3(-fwd.Y, fwd.X, 0);
            }
            var across = new Vec3(-along.Y, along.X, 0);
            _u = _h.Select(h => h.Collar.X * along.X + h.Collar.Y * along.Y).ToArray();
            var v = _h.Select(h => h.Collar.X * across.X + h.Collar.Y * across.Y).ToArray();

            // front row first: the rows side by side across their direction, from the side of the point
            var ordered = rows.Select(r => r.OrderBy(i => _u[i]).ToList()).OrderBy(r => r.Average(i => v[i])).ToList();
            if (ordered.Count > 1)
            {
                var vIp = ip.X * across.X + ip.Y * across.Y;
                if (Math.Abs(ordered[^1].Average(i => v[i]) - vIp) < Math.Abs(ordered[0].Average(i => v[i]) - vIp)) ordered.Reverse();
            }
            _rows = ordered;
            for (int r = 0; r < _rows.Count; r++) foreach (var i in _rows[r]) _rowOf[i] = r;

            var gaps = _rows.SelectMany(r => r.Zip(r.Skip(1), (a, c) => Dist(a, c))).Where(g => g > 0.01).OrderBy(g => g).ToList();
            _s = gaps.Count > 0 ? gaps[gaps.Count / 2] : project.Design.Spacing > 0 ? project.Design.Spacing : 5;
            var rowGaps = _rows.Zip(_rows.Skip(1), (a, c) => Math.Abs(c.Average(i => v[i]) - a.Average(i => v[i]))).Where(g => g > 0.01).OrderBy(g => g).ToList();
            _b = rowGaps.Count > 0 ? rowGaps[rowGaps.Count / 2] : project.Design.Burden > 0 ? project.Design.Burden : _s * 0.8;
        }

        /// <summary>The hole of a row nearest a hole (in plan).</summary>
        private int NearestTo(List<int> row, int hole) => row.OrderBy(i => Dist(i, hole)).First();

        private double Dist(int a, int c) => Vec3.Distance2D(_h[a].Collar, _h[c].Collar);

        /// <summary>The hole of a row nearest a place along the rows; -1 when none is within <paramref name="maxDev"/>.</summary>
        private int NearestU(List<int> row, double u, double maxDev = double.MaxValue)
        {
            int best = -1;
            double bd = maxDev;
            foreach (var i in row)
            {
                var d = Math.Abs(_u[i] - u);
                if (d <= bd) { bd = d; best = i; }
            }
            return best;
        }

        public TieUpPlan Build(TieScheme scheme)
        {
            var plan = new TieUpPlan { Scheme = scheme };
            int n = _h.Length;
            if (n == 0) return plan;
            // the tree: the parent of each hole (-2 the point) and the role of its tie
            var parent = Enumerable.Repeat(-1, n).ToArray();
            var role = new TieRole[n];
            var product = new Product?[n];
            void Link(int from, int to, TieRole r, Product? p = null)
            {
                if (parent[to] != -1) return;
                parent[to] = from;
                role[to] = r;
                product[to] = p ?? (r == TieRole.BetweenRows ? _o.BetweenRows : r == TieRole.Lead ? null : _o.InRow);
            }
            void ChainBoth(List<int> row, int anchor)
            {
                var k = row.IndexOf(anchor);
                for (int j = k + 1; j < row.Count; j++) Link(row[j - 1], row[j], TieRole.InRow);
                for (int j = k - 1; j >= 0; j--) Link(row[j + 1], row[j], TieRole.InRow);
            }
            // the front of the V layouts: the first full row from the point (a corner of a block with rows at a slant has
            // rows of one or two holes before it, tied from the rows behind them)
            var longest = _rows.Max(r => r.Count);
            var f = _rows.FindIndex(r => r.Count >= Math.Max(2, longest / 2));
            if (f < 0) f = 0;
            var front = _rows[f];
            var outward = Enumerable.Range(f + 1, _rows.Count - f - 1).Select(r => (Row: r, From: r - 1))
                .Concat(Enumerable.Range(0, f).Reverse().Select(r => (Row: r, From: r + 1))).ToList();
            int Center() => front.OrderBy(i => _dIp[i]).First();
            int Corner() => _dIp[front[0]] <= _dIp[front[^1]] ? front[0] : front[^1];

            switch (scheme)
            {
                case TieScheme.Rows:
                case TieScheme.Echelon:
                {
                    var first = _rows[0];
                    var a = scheme == TieScheme.Rows ? first.OrderBy(i => _dIp[i]).First() : _dIp[first[0]] <= _dIp[first[^1]] ? first[0] : first[^1];
                    Link(-2, a, TieRole.Lead);
                    ChainBoth(first, a);
                    for (int r = 1; r < _rows.Count; r++)
                    {
                        var next = NearestTo(_rows[r], a);
                        Link(a, next, TieRole.BetweenRows);
                        ChainBoth(_rows[r], next);
                        a = next;
                    }
                    break;
                }
                case TieScheme.VFront:
                {
                    var a = Center();
                    Link(-2, a, TieRole.Lead);
                    ChainBoth(front, a);
                    foreach (var (r, from) in outward)
                        foreach (var i in _rows[r]) Link(NearestTo(_rows[from], i), i, TieRole.BetweenRows);
                    break;
                }
                case TieScheme.Diagonal:
                case TieScheme.VDiagonal:
                {
                    var a = scheme == TieScheme.Diagonal ? Corner() : Center();
                    Link(-2, a, TieRole.Lead);
                    ChainBoth(front, a);
                    var oneWay = a == front[0] ? 1 : -1;
                    foreach (var (r, back) in outward)
                        foreach (var i in _rows[r])
                        {
                            var du = _u[i] - _u[a];
                            var way = scheme == TieScheme.Diagonal ? oneWay : Math.Abs(du) < _s * 0.5 ? 0 : Math.Sign(du);
                            var from = way == 0 ? -1 : _rows[back].Where(j => (_u[i] - _u[j]) * way > 0.25 * _s && Dist(i, j) <= 2 * Math.Max(_s, _b)).OrderBy(j => Dist(i, j)).DefaultIfEmpty(-1).First();
                            if (from < 0) from = NearestTo(_rows[back], i);
                            Link(from, i, TieRole.BetweenRows);
                        }
                    break;
                }
                case TieScheme.Fan:
                    BuildFan(parent, role, product);
                    break;
            }

            // holes no layout reached (a lone hole, a row cut short): from the nearest reached hole
            while (true)
            {
                int bi = -1, bp = -1;
                double bd = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (parent[i] != -1) continue;
                    for (int j = 0; j < n; j++)
                        if (parent[j] != -1 && Dist(i, j) < bd) { bd = Dist(i, j); bi = i; bp = j; }
                }
                if (bi < 0) break;
                Link(bp, bi, TieRole.Fill);
            }
            if (parent.All(p => p == -1)) Link(-2, Center(), TieRole.Lead);

            var block = Enumerable.Repeat(-1, n).ToArray();
            if (_o.HolesPerBlock > 1) ShareBlocks(plan, parent, role, product, block);

            var ipId = _o.Start.Id;
            for (int i = 0; i < n; i++)
            {
                if (parent[i] == -1) continue;
                if (parent[i] == -2)
                {
                    // a point standing on the hole starts it without a tie
                    if (_o.Start.HoleId != _h[i].Id) plan.Ties.Add(new PlannedTie(ipId, _h[i].Id, null, TieRole.Lead, Vec3.Distance2D(_o.Start.Position, _h[i].Collar)));
                    continue;
                }
                var len = Dist(parent[i], i);
                var p = product[i];
                if (p != null && _o.PickLeadLength) p = ByLead(p, len);
                plan.Ties.Add(new PlannedTie(_h[parent[i]].Id, _h[i].Id, p, role[i], len, block[i]));
            }
            Measure(plan);
            return plan;
        }

        /// <summary>
        /// The fan: holes by distance from the point, each from a nearer tied hole close by, with the connector whose delay
        /// brings the hole nearest the time of its distance (the delay per metre); the nearest hole from the point.
        /// </summary>
        private void BuildFan(int[] parent, TieRole[] role, Product?[] product)
        {
            var choices = _o.Connectors.Where(c => c.DelayMs > 0).GroupBy(c => c.DelayMs).Select(g => g.First()).ToList();
            foreach (var c in new[] { _o.InRow, _o.BetweenRows })
                if (c != null && c.DelayMs > 0 && choices.All(x => x.DelayMs != c.DelayMs)) choices.Add(c);
            if (choices.Count == 0) return;
            var perM = _o.FanMsPerM > 0 ? _o.FanMsPerM : (_o.InRow?.DelayMs ?? choices.Min(c => c.DelayMs)) / Math.Max(0.1, _s);
            // the whole fan within the in-hole delay when it can be: the farthest hole reached before the first one fires,
            // but no faster than the 8 ms window over a step between holes
            var minInhole = _inhole.Where(d => d > 0).DefaultIfEmpty(0).Min();
            var far = _dIp.Max() - _dIp.Min();
            if (_o.FanMsPerM <= 0 && minInhole > 0 && far > 0)
            {
                var floor = 1.5 * (_o.WindowMs > 0 ? _o.WindowMs : 8) / Math.Max(0.1, Math.Min(_s, _b));
                perM = Math.Max(floor, Math.Min(perM, 0.85 * minInhole / far));
            }
            var order = Enumerable.Range(0, _h.Length).OrderBy(i => _dIp[i]).ToList();
            var surface = new double[_h.Length];
            var tied = new List<int>();
            var first = order[0];
            parent[first] = -2;
            role[first] = TieRole.Lead;
            surface[first] = 0;
            tied.Add(first);
            var reach = 1.6 * Math.Max(_s, _b);
            var radius = _o.NeighbourRadius > 0 ? _o.NeighbourRadius : 1.5 * _s;
            var window = _o.WindowMs > 0 ? _o.WindowMs : 8;
            foreach (var i in order.Skip(1))
            {
                var target = perM * (_dIp[i] - _dIp[first]);
                var near = tied.Where(j => Dist(i, j) <= reach && _dIp[j] < _dIp[i] - 0.1).ToList();
                if (near.Count == 0) near = tied.OrderBy(j => Dist(i, j)).Take(1).ToList();
                // the tied neighbours: a time within the window of one of them is what the fan must not give
                var around = tied.Where(j => Dist(i, j) <= radius).ToList();
                int bp = -1;
                Product? bc = null;
                double be = double.MaxValue;
                foreach (var j in near)
                    foreach (var c in choices)
                    {
                        // apart from the neighbours first, then nearest the time of the distance, the shorter tie, the shorter delay
                        var fire = surface[j] + _block[j] + c.DelayMs + _inhole[i];
                        var clashes = around.Count(q => Math.Abs(surface[q] + _inhole[q] - fire) < window);
                        var e = 3 * window * clashes + Math.Abs(surface[j] + _block[j] + c.DelayMs - target) + 0.05 * Dist(i, j) + 0.001 * c.DelayMs;
                        if (e < be) { be = e; bp = j; bc = c; }
                    }
                parent[i] = bp;
                role[i] = TieRole.Fan;
                product[i] = bc;
                surface[i] = surface[bp] + _block[bp] + bc!.DelayMs;
                tied.Add(i);
            }
        }

        /// <summary>
        /// One block for several holes: along each chain of in-row ties the holes go in groups of
        /// <see cref="TieUpOptions.HolesPerBlock"/>; the block at the last hole of a group (the start of the chain for the
        /// first) feeds the whole next group with the connector nearest the count times the in-row delay, and the holes of a
        /// group fire one after another by in-hole delays stepped by that delay over the count.
        /// </summary>
        private void ShareBlocks(TieUpPlan plan, int[] parent, TieRole[] role, Product?[] product, int[] block)
        {
            var k = _o.HolesPerBlock;
            if (_o.InRow is not { DelayMs: > 0 } inRow) return;
            var connectors = _o.Connectors.Count > 0 ? _o.Connectors : new List<Product> { inRow };
            var big = Nearest(connectors, k * inRow.DelayMs) ?? inRow;
            var step = Math.Round(big.DelayMs / k);
            // a step the downhole detonators of the resources give (500 and 525 ms: 25 ms), with the connector nearest k steps
            var bases = _inhole.Where(d => d > 0).GroupBy(d => Math.Round(d)).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            if (bases.Count > 0)
            {
                var steps = _project.ActiveProducts.Where(p => p.Kind == ProductKind.DownholeDetonator && !p.Deleted && p.DelayMs > bases[0] && p.DelayMs - bases[0] <= 4 * inRow.DelayMs)
                    .Select(p => p.DelayMs - bases[0]).Distinct().ToList();
                var pick = steps.SelectMany(st => connectors.Where(c => c.DelayMs > 0).Select(c => (Step: st, C: c, E: Math.Abs(c.DelayMs - k * st) + 0.5 * Math.Abs(st - inRow.DelayMs))))
                    .OrderBy(x => x.E).FirstOrDefault();
                if (pick.C != null && pick.E < inRow.DelayMs) { big = pick.C; step = pick.Step; }
            }
            var maxLead = connectors.Where(c => c.DelayMs == big.DelayMs).Select(c => c.LeadLengthM).DefaultIfEmpty(0).Max();
            var children = new List<int>[_h.Length];
            for (int i = 0; i < _h.Length; i++) children[i] = new();
            for (int i = 0; i < _h.Length; i++) if (parent[i] >= 0 && role[i] == TieRole.InRow) children[parent[i]].Add(i);
            var missing = new SortedSet<double>();
            var downhole = _project.ActiveProducts.Where(p => p.Kind == ProductKind.DownholeDetonator && !p.Deleted).Select(p => p.DelayMs).ToHashSet();
            int blocks = 0;
            // the chains: from each hole along each of its in-row ties, as long as the next hole is the only one
            for (int start = 0; start < _h.Length; start++)
            {
                if (parent[start] == -1 || (parent[start] >= 0 && role[start] == TieRole.InRow)) continue;
                foreach (var firstChild in children[start].ToList())
                {
                    var chain = new List<int> { start, firstChild };
                    while (children[chain[^1]].Count == 1) chain.Add(children[chain[^1]][0]);
                    var g = 1;
                    while (g < chain.Count)
                    {
                        var feeder = chain[g - 1];
                        var id = blocks++;
                        var taken = 0;
                        for (int j = 0; j < k && g + j < chain.Count; j++)
                        {
                            var m = chain[g + j];
                            // a lead too short for the farther holes of the group: the block feeds no farther
                            if (j > 0 && maxLead > 0 && Dist(feeder, m) > maxLead) break;
                            parent[m] = feeder;
                            product[m] = big;
                            block[m] = id;
                            taken++;
                            if (j == 0) continue;
                            var extra = j * step;
                            plan.ExtraInHoleMs[_h[m].Id] = extra;
                            var need = _inhole[m] + extra;
                            if (_inhole[m] > 0 && !downhole.Contains(need)) missing.Add(need);
                        }
                        g += taken;
                    }
                }
            }
            if (missing.Count > 0)
                plan.Warnings.Add(new TieUpWarning("No downhole detonator of {0} ms among the resources: the delay is set on the primers", string.Join(", ", missing.Select(m => m.ToString("0")))));
        }

        /// <summary>The connector of the same delay with the shortest known lead that reaches the tie (the longest when none does).</summary>
        private Product ByLead(Product p, double length)
        {
            var same = _o.Connectors.Where(c => c.Kind == p.Kind && Math.Abs(c.DelayMs - p.DelayMs) < 0.01 && c.LeadLengthM > 0).ToList();
            if (same.Count == 0) return p;
            return same.Where(c => c.LeadLengthM >= length).OrderBy(c => c.LeadLengthM).ThenBy(c => c.Cost).FirstOrDefault()
                ?? same.OrderByDescending(c => c.LeadLengthM).First();
        }

        private void Measure(TieUpPlan plan)
        {
            var o = _o;
            var index = new Dictionary<Guid, int>();
            for (int i = 0; i < _h.Length; i++) index[_h[i].Id] = i;
            // the surface signal from the point along the planned ties (earliest arrival wins)
            var adj = new Dictionary<Guid, List<(Guid To, double Delay)>>();
            foreach (var t in plan.Ties)
            {
                if (!adj.TryGetValue(t.From, out var list)) adj[t.From] = list = new();
                list.Add((t.To, (index.TryGetValue(t.From, out var fi) ? _block[fi] : 0) + (t.Product?.DelayMs ?? 0)));
            }
            var arrival = new Dictionary<Guid, double> { [o.Start.Id] = _t0 };
            if (o.Start.HoleId is Guid on && index.ContainsKey(on)) arrival[on] = _t0;
            var pq = new PriorityQueue<Guid, double>();
            foreach (var (id, t) in arrival) pq.Enqueue(id, t);
            while (pq.TryDequeue(out var node, out var time))
            {
                if (arrival[node] < time || !adj.TryGetValue(node, out var edges)) continue;
                foreach (var (to, d) in edges)
                    if (!arrival.TryGetValue(to, out var cur) || time + d < cur) { arrival[to] = time + d; pq.Enqueue(to, time + d); }
            }
            var fire = new double[_h.Length];
            var reached = new bool[_h.Length];
            for (int i = 0; i < _h.Length; i++)
            {
                if (!arrival.TryGetValue(_h[i].Id, out var s)) { plan.Unreached++; continue; }
                reached[i] = true;
                plan.Surface[_h[i].Id] = s;
                fire[i] = s + _inhole[i] + plan.ExtraInHoleMs.GetValueOrDefault(_h[i].Id);
                plan.Firing[_h[i].Id] = fire[i];
            }
            var idx = Enumerable.Range(0, _h.Length).Where(i => reached[i]).OrderBy(i => fire[i]).ToList();

            // connectors: a block feeding several holes is one
            var units = plan.Ties.Where(t => t.Product != null).GroupBy(t => t.Block >= 0 ? (object)("b" + t.Block) : t).Select(g => g.First().Product!).ToList();
            plan.Connectors = units.Count;
            foreach (var p in units) plan.ByProduct[p] = plan.ByProduct.GetValueOrDefault(p) + 1;
            plan.Cost = units.Sum(p => p.Cost);
            plan.LengthM = plan.Ties.Where(t => t.Role != TieRole.Lead).Sum(t => t.LengthM);
            plan.Nominals = plan.ByProduct.Keys.Select(p => p.DelayMs).Distinct().Count();
            plan.ShortLeads = plan.Ties.Count(t => t.Product is { LeadLengthM: > 0 } p && t.LengthM > p.LeadLengthM + 0.05);
            if (idx.Count == 0) return;

            plan.FirstFiringMs = fire[idx[0]];
            plan.LastFiringMs = fire[idx[^1]];
            plan.NetworkMs = idx.Max(i => plan.Surface[_h[i].Id]) - _t0;
            plan.LateHoles = idx.Count(i => plan.Surface[_h[i].Id] > plan.FirstFiringMs);

            // holes and charge in the window (8 ms rule)
            var w = o.WindowMs > 0 ? o.WindowMs : 8;
            var charge = idx.Select(i => o.Charge?.Invoke(_h[i]) ?? 0).ToArray();
            double sum = 0;
            for (int a = 0, c = 0; c < idx.Count; c++)
            {
                sum += charge[c];
                while (fire[idx[c]] - fire[idx[a]] >= w) sum -= charge[a++];
                plan.MaxHolesInWindow = Math.Max(plan.MaxHolesInWindow, c - a + 1);
                plan.MaxCharge = Math.Max(plan.MaxCharge, sum);
            }

            // neighbours firing together, and holes firing near a tie before the signal has passed it
            var radius = o.NeighbourRadius > 0 ? o.NeighbourRadius : 1.5 * _s;
            var cut = o.CutOffRadius > 0 ? o.CutOffRadius : 2 * _b;
            var feed = plan.Ties.Where(t => index.ContainsKey(t.To)).GroupBy(t => t.To).ToDictionary(g => index[g.Key], g => g.First());
            foreach (var i in idx)
            {
                var pi = _h[i].Collar;
                if (idx.Any(j => j != i && Math.Abs(fire[j] - fire[i]) < w && Vec3.Distance2D(_h[j].Collar, pi) <= radius)) plan.Overlaps++;
                if (!feed.TryGetValue(i, out var tie)) continue;
                var from = tie.From == o.Start.Id ? o.Start.Position : index.TryGetValue(tie.From, out var fi) ? _h[fi].Collar : pi;
                var s = plan.Surface[_h[i].Id];
                var gap = idx.Where(j => j != i && fire[j] < s).Select(j => SegmentDistance(_h[j].Collar, from, pi)).DefaultIfEmpty(double.MaxValue).Min();
                if (gap == double.MaxValue) continue;
                plan.FrontGapM = Math.Min(plan.FrontGapM ?? double.MaxValue, gap);
                if (gap <= cut) plan.CutOffRisk++;
            }

            var minInhole = idx.Select(i => _inhole[i]).Where(d => d > 0).DefaultIfEmpty(0).Min();
            if (plan.Unreached > 0) plan.Warnings.Add(new TieUpWarning("{0} holes are not reached by the tie-up", plan.Unreached));
            if (minInhole > 0 && plan.NetworkMs > minInhole)
                plan.Warnings.Add(new TieUpWarning("The surface network takes {0} ms, longer than the in-hole delay {1} ms", Math.Round(plan.NetworkMs), Math.Round(minInhole)));
            if (plan.LateHoles > 0 && plan.CutOffRisk == 0 && plan.FrontGapM is double fg)
                plan.Warnings.Add(new TieUpWarning("{0} holes get the signal after the first hole has fired; the signal runs at least {1} m ahead of the blasting (cut-off radius {2} m)", plan.LateHoles, Math.Round(fg, 1), Math.Round(cut, 1)));
            else if (plan.LateHoles > 0) plan.Warnings.Add(new TieUpWarning("{0} holes get the signal after the first hole has fired", plan.LateHoles));
            if (plan.CutOffRisk > 0) plan.Warnings.Add(new TieUpWarning("{0} ties pass near holes that fire before the signal: risk of cut-off", plan.CutOffRisk));
            if (plan.ShortLeads > 0) plan.Warnings.Add(new TieUpWarning("{0} ties are longer than the lead of their connector", plan.ShortLeads));
            if (plan.Overlaps > 0) plan.Warnings.Add(new TieUpWarning("{0} holes fire within {1} ms of a neighbour", plan.Overlaps, w));
        }

        private static double SegmentDistance(Vec3 p, Vec3 a, Vec3 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            var t = l2 <= 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }
}
