using System.Globalization;
using System.Text;
using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.IO;

/// <summary>Units of the DELAY values written to a BPD file.</summary>
public enum BpdDelayUnit
{
    /// <summary>Whole milliseconds.</summary>
    Ms,
    /// <summary>Tenths of a millisecond.</summary>
    TenthMs
}

/// <summary>Which detonator of a hole gets LEVEL 1.</summary>
public enum BpdLevelOrder
{
    /// <summary>LEVEL 1 is the deepest detonator, the levels go up the hole.</summary>
    BottomFirst,
    /// <summary>LEVEL 1 is the detonator nearest the collar, the levels go down the hole.</summary>
    TopFirst
}

/// <summary>A tie type of the plan: the connector product of the ties, or their own delay when they have no product.</summary>
public sealed record BpdTieType(string Key, string Name);

/// <summary>
/// A chain of ties: ties of one type joined end to end (through holes or an initiation point). Ties of the same type that do not
/// touch make separate chains. <see cref="Ties"/> are in the order they were drawn.
/// </summary>
public sealed record BpdChain(string Key, string TieTypeKey, string TieTypeName, string FirstHole, string LastHole, List<TieEntity> Ties)
{
    public int TieCount => Ties.Count;
}

/// <summary>One line of a BPD plan: a chain of ties, programmed by one programming unit (PU).</summary>
public sealed record BpdPathRequest(string ChainKey, string PuSerialNumber);

public sealed record BpdDetonator(int OrderNumber, int Level, long Delay);

public sealed record BpdHole(string HoleId, List<BpdDetonator> Detonators, Hole? Source = null);

public sealed class BpdPath
{
    public string Id { get; set; } = "A";
    public string PuSerialNumber { get; set; } = string.Empty;
    /// <summary>Rectangle of the detonation zone, UTM metres: west, south, east, north edges.</summary>
    public long West { get; set; }
    public long South { get; set; }
    public long East { get; set; }
    public long North { get; set; }
    public List<BpdHole> Holes { get; set; } = new();

    public int DetonatorCount => Holes.Sum(h => h.Detonators.Count);
}

/// <summary>Contents of a PU_AUTO_BLASTPLAN file.</summary>
public sealed class BpdPlan
{
    public DateTime Created { get; set; } = DateTime.Now;
    public string FullName { get; set; } = "BLAST___";
    public string DisplayedName { get; set; } = "BLAST___";
    public string SourceFile { get; set; } = "Blast Designer 6";
    /// <summary>"N" or "S".</summary>
    public string UtmZoneLetter { get; set; } = "N";
    public int UtmZoneNumber { get; set; }
    public List<BpdPath> Paths { get; set; } = new();
}

/// <summary>A message of the export check: an English format (the translation key) and its arguments.</summary>
public sealed record BpdMessage(string Format, params object[] Args)
{
    public override string ToString() => string.Format(CultureInfo.InvariantCulture, Format, Args);
}

public sealed record BpdBuildResult(BpdPlan Plan, List<BpdMessage> Errors, List<BpdMessage> Warnings);

/// <summary>
/// Blast plan for the DaveyTronic electronic initiation system (PU_AUTO_BLASTPLAN, *.BPD): an XML-like text with a header,
/// one PATH per programming unit, its detonation zone in UTM and the holes with their detonators. The CRC is checked by the
/// programming unit, so the text is written byte for byte in the layout of the reference file (ASCII, CRLF, fixed indents).
/// </summary>
public static class DeviTronic
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string DefaultFileName = "PU_AUTO_BLASTPLAN.BPD";
    public const int NameLength = 8;
    /// <summary>Ties of one chain above which the export warns (a line of the programming unit takes no more).</summary>
    public const int MaxTiesPerPath = 800;

    // ---- tie types -----------------------------------------------------------------------

    public static string TieTypeKey(TieEntity t) => t.ProductId ?? "delay:" + t.DelayMs.ToString("0.###", Inv);

    /// <summary>The tie types used in the plan, in the order of their first tie.</summary>
    public static List<BpdTieType> TieTypes(BlastProject project)
    {
        var list = new List<BpdTieType>();
        foreach (var t in project.Ties)
        {
            var key = TieTypeKey(t);
            if (list.Any(x => x.Key == key)) continue;
            var name = t.ProductId != null
                ? project.FindProduct(t.ProductId)?.Name ?? t.ProductId
                : t.DelayMs == 0 ? "0 ms connection" : t.DelayMs.ToString("0.###", Inv) + " ms";
            list.Add(new BpdTieType(key, name));
        }
        return list;
    }

    /// <summary>
    /// The chains of ties of the plan: for each tie type, the groups of its ties joined end to end. Chains come in the order
    /// their first tie was drawn; the key of a chain is the id of that tie.
    /// </summary>
    public static List<BpdChain> Chains(BlastProject project)
    {
        var all = project.Ties.ToList();
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid x)
        {
            if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
            if (p == x) return x;
            var r = Find(p);
            parent[x] = r;
            return r;
        }
        // an object is a node of its tie type: ties of different types through one hole stay apart
        Guid Node(TieEntity t, Guid end) => GuidOf(TieTypeKey(t) + "|" + end);
        foreach (var t in all) parent[Find(Node(t, t.FromId))] = Find(Node(t, t.ToId));
        var names = TieTypes(project).ToDictionary(x => x.Key, x => x.Name);
        var holes = project.Holes.ToDictionary(h => h.Id);
        var chains = new List<BpdChain>();
        foreach (var g in all.GroupBy(t => Find(Node(t, t.FromId))))
        {
            var ties = g.ToList();
            var first = ties[0];
            var order = OrderAlongTies(ties, holes.Values.Where(h => ties.Any(t => t.FromId == h.Id || t.ToId == h.Id)).ToList());
            var type = TieTypeKey(first);
            chains.Add(new BpdChain("chain:" + first.Id, type, names[type], order.FirstOrDefault()?.Label ?? "", order.LastOrDefault()?.Label ?? "", ties));
        }
        return chains.OrderBy(c => all.IndexOf(c.Ties[0])).ToList();
    }

    private static Guid GuidOf(string s) => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>
    /// The chain of each hole: the chain of the tie coming into the hole, or for the first hole of a chain (nothing comes in)
    /// the chain of the first tie going out of it. Holes without ties have none.
    /// </summary>
    public static Dictionary<Guid, string> HoleChains(BlastProject project, IReadOnlyList<BpdChain> chains)
    {
        var chainOf = new Dictionary<Guid, string>();
        foreach (var c in chains) foreach (var t in c.Ties) chainOf[t.Id] = c.Key;
        var map = new Dictionary<Guid, string>();
        var holes = project.Holes.Select(h => h.Id).ToHashSet();
        foreach (var t in project.Ties)
            if (holes.Contains(t.ToId)) map.TryAdd(t.ToId, chainOf[t.Id]);
        foreach (var t in project.Ties)
            if (holes.Contains(t.FromId)) map.TryAdd(t.FromId, chainOf[t.Id]);
        return map;
    }

    public static string ChainName(BpdChain c) => $"{c.TieTypeName}: {c.FirstHole}–{c.LastHole}, {c.TieCount}";

    // ---- building the plan ------------------------------------------------------------------

    /// <summary>
    /// Builds the plan from the holes: one PATH per request (IDs A, B, C… in the order of the requests) with the holes of its
    /// chain of ties that carry electronic detonators. Detonators are numbered along the ties of the chain; LEVEL 1 is the
    /// deepest or the topmost detonator of a hole (<paramref name="levels"/>), and within a hole the order numbers follow the
    /// levels. Delays are the holes' programmed electronic times.
    /// </summary>
    public static BpdBuildResult Build(BlastProject project, IEnumerable<Hole> holes, IReadOnlyList<BpdPathRequest> paths, BpdDelayUnit unit, string sourceFile, DateTime? now = null, BpdLevelOrder levels = BpdLevelOrder.BottomFirst)
    {
        var errors = new List<BpdMessage>();
        var warnings = new List<BpdMessage>();
        var name = SanitizeName(project.Header.BlastNumber.Length > 0 ? project.Header.BlastNumber : project.Header.Name);
        var plan = new BpdPlan
        {
            Created = now ?? DateTime.Now,
            FullName = name,
            DisplayedName = name.ToUpperInvariant(),
            SourceFile = sourceFile,
            UtmZoneNumber = project.Coordinates.UtmZone,
            UtmZoneLetter = project.Coordinates.SouthernHemisphere ? "S" : "N"
        };
        var chains = Chains(project);
        var byKey = chains.ToDictionary(c => c.Key);
        if (project.Coordinates.UtmZone < 1) warnings.Add(new BpdMessage("The UTM zone is not set (Edit ▸ Site coordinate system): zone 0 is written."));
        if (paths.Count == 0) errors.Add(new BpdMessage("There are no ties in the plan: the lines (PATH) are made by chains of ties."));
        if (paths.Count > 26) errors.Add(new BpdMessage("No more than 26 lines (A–Z) can be written."));
        foreach (var dup in paths.GroupBy(p => p.ChainKey).Where(g => g.Count() > 1))
            errors.Add(new BpdMessage("The chain is chosen for more than one line: {0}", byKey.TryGetValue(dup.Key, out var dc) ? ChainName(dc) : dup.Key));

        var chainOf = HoleChains(project, chains);
        var chosen = holes.Where(h => !h.IsDummy).ToList();
        var keys = paths.Select(p => p.ChainKey).ToHashSet();
        var notTied = chosen.Count(h => !chainOf.ContainsKey(h.Id) && HasElectronic(project, h));
        if (notTied > 0) warnings.Add(new BpdMessage("{0} hole(s) with electronic detonators have no ties and are not written.", notTied));
        var other = chosen.Count(h => chainOf.TryGetValue(h.Id, out var k) && !keys.Contains(k) && HasElectronic(project, h));
        if (other > 0) warnings.Add(new BpdMessage("{0} hole(s) are on a chain not chosen for any line and are not written.", other));

        for (int i = 0; i < paths.Count && i < 26; i++)
        {
            var req = paths[i];
            var id = ((char)('A' + i)).ToString();
            if (!long.TryParse(req.PuSerialNumber.Trim(), NumberStyles.None, Inv, out _))
                errors.Add(new BpdMessage("Line {0}: PU Serial Number must be a whole number.", id));
            if (!byKey.TryGetValue(req.ChainKey, out var chain)) { errors.Add(new BpdMessage("Line {0}: the chain of ties is not in the plan.", id)); continue; }
            if (chain.TieCount > MaxTiesPerPath) warnings.Add(new BpdMessage("Line {0} ({1}): {2} ties, more than {3}.", id, ChainName(chain), chain.TieCount, MaxTiesPerPath));
            var lineHoles = chosen.Where(h => chainOf.TryGetValue(h.Id, out var k) && k == req.ChainKey).ToList();
            var noDet = lineHoles.Where(h => !HasElectronic(project, h)).ToList();
            if (noDet.Count > 0) warnings.Add(new BpdMessage("Line {0}: {1} hole(s) without electronic detonators are not written.", id, noDet.Count));
            lineHoles = OrderAlongTies(chain.Ties, lineHoles.Except(noDet).ToList());
            if (lineHoles.Count == 0) { errors.Add(new BpdMessage("Line {0} ({1}): no holes with electronic detonators.", id, ChainName(chain))); continue; }

            var path = new BpdPath { Id = id, PuSerialNumber = req.PuSerialNumber.Trim() };
            int order = 0, n = 0;
            foreach (var h in lineHoles)
            {
                var ms = h.ElectronicTimeMs ?? h.FiringTimeMs;
                if (ms == null) { errors.Add(new BpdMessage("Hole {0}: the electronic time is not set.", h.Label)); continue; }
                long Delay(double t) => unit == BpdDelayUnit.Ms ? (long)Math.Round(t, MidpointRounding.AwayFromZero) : (long)Math.Round(t * 10, MidpointRounding.AwayFromZero);
                // every detonator its own time: the hole's, by the deck delays or the primer's offset
                var times = Engine.Charging.PrimerTimes(project, h, ms.Value);
                var primers = Engine.Charging.Primers(h)
                    .Where(p => project.FindProduct(p.Primer.DetonatorId)?.Kind == ProductKind.ElectronicDetonator)
                    .ToList();
                primers = levels == BpdLevelOrder.BottomFirst ? primers.OrderByDescending(p => p.Primer.Depth).ToList() : primers.OrderBy(p => p.Primer.Depth).ToList();
                // a primer whose offset puts it before 0 is not programmed (SHOTPlus SetEBSTimes): the hole cannot go to the blaster
                if (primers.Any(p => !times.ContainsKey(p.Primer))) { errors.Add(new BpdMessage("Hole {0}: a primer fires before 0 with its offset and is not programmed.", h.Label)); continue; }
                var dets = primers.Select((p, level) => new BpdDetonator(++order, level + 1, Delay(times.TryGetValue(p.Primer, out var t) ? t : ms.Value))).ToList();
                path.Holes.Add(new BpdHole($"{id}{++n:0000}", dets, h));
            }
            var e = lineHoles.Select(h => h.Collar.X + project.Coordinates.OffsetEasting).ToList();
            var nn = lineHoles.Select(h => h.Collar.Y + project.Coordinates.OffsetNorthing).ToList();
            path.West = (long)Math.Floor(e.Min());
            path.East = (long)Math.Ceiling(e.Max());
            path.South = (long)Math.Floor(nn.Min());
            path.North = (long)Math.Ceiling(nn.Max());
            plan.Paths.Add(path);
        }
        return new BpdBuildResult(plan, errors, warnings);
    }

    private static bool HasElectronic(BlastProject project, Hole h) => Engine.Charging.HasElectronicDetonator(project, h);

    /// <summary>
    /// The holes of a chain in the order of its ties on the plan. The wire is followed from the start of the chain (an
    /// object no tie of the chain comes into: a hole or an initiation point); where a hole has several ties going out, they are
    /// followed in the order they were drawn, each branch to its end. The whole chain is walked, also through holes that are
    /// not written (no electronic detonator, not selected), so their neighbours keep their places. Holes of a closed loop
    /// follow in the order of their first tie; holes with no tie of the chain come last by label.
    /// </summary>
    /// <param name="ties">The ties of the chain in the order they were drawn (the order of the project's objects).</param>
    public static List<Hole> OrderAlongTies(IReadOnlyList<TieEntity> ties, List<Hole> holes)
    {
        var wanted = holes.Select(h => h.Id).ToHashSet();
        var byId = holes.ToDictionary(h => h.Id);
        var next = ties.GroupBy(t => t.FromId).ToDictionary(g => g.Key, g => g.Select(t => t.ToId).ToList());
        var hasIncoming = ties.Select(t => t.ToId).ToHashSet();
        var result = new List<Hole>();
        var seen = new HashSet<Guid>();
        void Walk(Guid start)
        {
            var stack = new Stack<Guid>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                if (wanted.Contains(id)) result.Add(byId[id]);
                if (next.TryGetValue(id, out var list))
                    for (int i = list.Count - 1; i >= 0; i--) stack.Push(list[i]);
            }
        }
        foreach (var t in ties.Where(t => !hasIncoming.Contains(t.FromId))) Walk(t.FromId);
        foreach (var t in ties) Walk(t.FromId);
        foreach (var h in holes.OrderBy(h => h.Label, NaturalOrder.Instance)) if (seen.Add(h.Id)) result.Add(h);
        return result;
    }

    /// <summary>The blast name as the programming unit shows it: letters, digits and '_', cut or padded with '_' to 8 characters.</summary>
    public static string SanitizeName(string name)
    {
        var s = new string(name.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')).ToArray());
        if (s.Length == 0) s = "BLAST";
        return s.Length >= NameLength ? s[..NameLength] : s.PadRight(NameLength, '_');
    }

    // ---- writing --------------------------------------------------------------------------

    /// <summary>The file text: CRLF line ends, a bare LF after the last tag (as in the reference file). Write it with <see cref="Encoding.ASCII"/>.</summary>
    public static string Write(BpdPlan plan)
    {
        var d = new StringBuilder();
        void L(int indent, string text) => d.Append(' ', indent).Append(text).Append("\r\n");
        void T(int indent, string tag, object value) => L(indent, $"<{tag}>{Convert.ToString(value, Inv)}</{tag}>");

        d.Append("<DATA>\r\n");
        L(4, "<HEADER>");
        T(6, "DATE", plan.Created.ToString("yyyy/MM/dd", Inv));
        T(6, "TIME", plan.Created.ToString("HH:mm:ss", Inv));
        T(6, "FULL_NAME", plan.FullName);
        T(6, "DISPLAYED_NAME", plan.DisplayedName);
        T(6, "TOTAL_DETS_QUANTITY", plan.Paths.Sum(p => p.DetonatorCount));
        T(6, "PATH_QUANTITY", plan.Paths.Count);
        T(6, "SOURCE_FILE", plan.SourceFile);
        L(4, "</HEADER>");
        foreach (var p in plan.Paths)
        {
            var delays = p.Holes.SelectMany(h => h.Detonators).Select(x => x.Delay).DefaultIfEmpty(0).ToList();
            L(4, "<PATH>");
            T(6, "ID", p.Id);
            T(6, "PU_SERIAL_NUMBER", p.PuSerialNumber);
            T(6, "PATH_DETS_QUANTITY", p.DetonatorCount);
            T(6, "PATH_MIN_DELAY", delays.Min());
            T(6, "PATH_MAX_DELAY", delays.Max());
            L(6, "<DETZONE>");
            T(8, "UTM_ZONE_LETTER", plan.UtmZoneLetter);
            T(8, "UTM_ZONE_NUMBER", plan.UtmZoneNumber);
            T(8, "UPPER_LEFT_CORNER_EASTING", p.West);
            T(8, "UPPER_LEFT_CORNER_NORTHING", p.North);
            T(8, "UPPER_RIGHT_CORNER_EASTING", p.East);
            T(8, "UPPER_RIGHT_CORNER_NORTHING", p.North);
            T(8, "LOWER_RIGHT_CORNER_EASTING", p.East);
            T(8, "LOWER_RIGHT_CORNER_NORTHING", p.South);
            T(8, "LOWER_LEFT_CORNER_EASTING", p.West);
            T(8, "LOWER_LEFT_CORNER_NORTHING", p.South);
            L(6, "</DETZONE>");
            foreach (var h in p.Holes)
            {
                L(6, "<HOLE>");
                T(8, "HOLE_ID", h.HoleId);
                foreach (var x in h.Detonators)
                {
                    L(8, "<DETONATOR>");
                    T(10, "ORDER_NUMBER", x.OrderNumber);
                    T(10, "LEVEL", x.Level);
                    T(10, "DELAY", x.Delay);
                    L(8, "</DETONATOR>");
                }
                L(6, "</HOLE>");
            }
            L(4, "</PATH>");
        }
        d.Append("</DATA>");
        var data = d.ToString();
        return $"<PU_AUTO_BLASTPLAN>\r\n<CRC>{FileCrc(data)}</CRC>\r\n{data}\r\n</PU_AUTO_BLASTPLAN>\n";
    }

    /// <summary>CRC-16/X-25 (poly 0x1021 reflected, init and xorout 0xFFFF) of the bytes.</summary>
    public static ushort Crc16X25(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
        }
        return (ushort)~crc;
    }

    /// <summary>The value of the CRC tag: X-25 of the DATA element (from "&lt;DATA&gt;" to "&lt;/DATA&gt;"), bytes swapped.</summary>
    public static int FileCrc(string dataElement)
    {
        var x = Crc16X25(Encoding.ASCII.GetBytes(dataElement));
        return ((x & 0xFF) << 8) | (x >> 8);
    }

    /// <summary>Checks the CRC of a BPD text: true when the CRC tag matches its DATA element.</summary>
    public static bool CheckCrc(string text)
    {
        int a = text.IndexOf("<DATA>", StringComparison.Ordinal), b = text.IndexOf("</DATA>", StringComparison.Ordinal);
        int c = text.IndexOf("<CRC>", StringComparison.Ordinal), e = text.IndexOf("</CRC>", StringComparison.Ordinal);
        if (a < 0 || b < a || c < 0 || e < c) return false;
        return int.TryParse(text[(c + 5)..e], NumberStyles.None, Inv, out var crc) && crc == FileCrc(text[a..(b + 7)]);
    }

    public static void Save(string path, BpdPlan plan) => File.WriteAllBytes(path, Encoding.ASCII.GetBytes(Write(plan)));
}
