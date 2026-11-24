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

/// <summary>
/// One line of a BPD plan: a chain of ties, programmed by one programming unit (PU); <paramref name="Line"/> its ID (A–Z), by
/// default the next one free in the order of the requests.
/// </summary>
public sealed record BpdPathRequest(string ChainKey, string PuSerialNumber, string? Line = null);

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
    /// <summary>The holes the message is about (those left out of the file), to be selected in the plan; none for the others.</summary>
    public IReadOnlyList<Guid> Holes { get; init; } = Array.Empty<Guid>();

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, Format, Args);
}

public sealed record BpdBuildResult(BpdPlan Plan, List<BpdMessage> Errors, List<BpdMessage> Warnings);

/// <summary>
/// The HEADER of a BPD file and the UTM zone of its lines (DETZONE), as set in the export window: written as given (the
/// displayed name padded with '_' to 8), each checked by <see cref="DeviTronic.Build"/>.
/// </summary>
/// <param name="FullName">FULL_NAME: Latin letters, digits and '_', up to <see cref="DeviTronic.FullNameLength"/>.</param>
/// <param name="DisplayedName">DISPLAYED_NAME: the same, up to <see cref="DeviTronic.NameLength"/>.</param>
/// <param name="SourceFile">SOURCE_FILE: printable ASCII, up to <see cref="DeviTronic.SourceLength"/>.</param>
/// <param name="UtmZone">UTM_ZONE_NUMBER, 1–60.</param>
/// <param name="Southern">UTM_ZONE_LETTER: S for the southern hemisphere, N for the northern.</param>
public sealed record BpdHeader(DateTime Created, string FullName, string DisplayedName, string SourceFile, int UtmZone, bool Southern);

/// <summary>
/// Blast plan for the DaveyTronic electronic initiation system (PU_AUTO_BLASTPLAN, *.BPD): an XML-like text with a header,
/// one PATH per programming unit, its detonation zone in UTM and the holes with their detonators. The CRC is checked by the
/// programming unit, so the text is written byte for byte in the layout of the reference file (ASCII, CRLF, fixed indents).
/// </summary>
public static class DeviTronic
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string DefaultFileName = "PU_AUTO_BLASTPLAN.BPD";
    /// <summary>Characters of DISPLAYED_NAME: the programming unit shows 8, padded with '_'.</summary>
    public const int NameLength = 8;
    /// <summary>Characters of FULL_NAME at most, written as they are.</summary>
    public const int FullNameLength = 30;
    /// <summary>Characters of SOURCE_FILE at most.</summary>
    public const int SourceLength = 32;
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
    /// A tie of the electronic system: its product is an electronic detonator (the EBS detonators type of the initiation
    /// catalogue). Only these make the lines of a BPD plan; shock tube, cord, wire and ties without a product do not.
    /// </summary>
    public static bool IsElectronicTie(BlastProject project, TieEntity tie) => project.FindProduct(tie.ProductId)?.Kind == ProductKind.ElectronicDetonator;

    /// <summary>
    /// The chains of EBS ties of the plan (<see cref="IsElectronicTie"/>): for each tie type, the groups of its ties joined
    /// end to end. Chains come in the order their first tie was drawn; the key of a chain is the id of that tie.
    /// </summary>
    public static List<BpdChain> Chains(BlastProject project)
    {
        var all = project.Ties.Where(t => IsElectronicTie(project, t)).ToList();
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
        // the ties of no chain (not of the electronic system) give a hole none
        foreach (var t in project.Ties)
            if (holes.Contains(t.ToId) && chainOf.TryGetValue(t.Id, out var into)) map.TryAdd(t.ToId, into);
        foreach (var t in project.Ties)
            if (holes.Contains(t.FromId) && chainOf.TryGetValue(t.Id, out var from)) map.TryAdd(t.FromId, from);
        return map;
    }

    public static string ChainName(BpdChain c) => $"{c.TieTypeName}: {c.FirstHole}–{c.LastHole}, {c.TieCount}";

    /// <summary>A line ID a BPD file takes: one letter A–Z.</summary>
    static bool IsLine(string? s) => s is { Length: 1 } && s[0] is >= 'A' and <= 'Z';

    /// <summary>
    /// The line (A–Z) of each chain, in the order of <paramref name="chains"/>: the letter of the route numbers of its holes
    /// (<see cref="Hole.RouteLine"/>, the one most of them have); a chain without one, or whose letter an earlier chain
    /// took, the first letter no chain has.
    /// </summary>
    public static List<string> Lines(BlastProject project, IReadOnlyList<BpdChain> chains)
    {
        var holes = project.Holes.ToDictionary(h => h.Id);
        var own = chains.Select(c => c.Ties.SelectMany(t => new[] { t.FromId, t.ToId }).Distinct()
            .Select(id => holes.TryGetValue(id, out var h) && h.RouteNumber != null ? h.RouteLine : null)
            .Where(IsLine).GroupBy(l => l!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key).ToList();
        return Free(own);
    }

    /// <summary>Each wanted letter where it is one and not taken before; the rest the first letters not wanted nor taken.</summary>
    static List<string> Free(IReadOnlyList<string?> wanted)
    {
        var taken = new HashSet<string>();
        var lines = wanted.Select(w => IsLine(w) && taken.Add(w!) ? w : null).ToList();
        var next = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] != null) continue;
            while (next < 26 && taken.Contains(((char)('A' + next)).ToString())) next++;
            lines[i] = next < 26 ? ((char)('A' + next)).ToString() : "?";
            taken.Add(lines[i]!);
        }
        return lines!;
    }

    // ---- building the plan ------------------------------------------------------------------

    /// <summary>
    /// Builds the plan from the holes: one PATH per request (IDs A, B, C… in the order of the requests) with the holes of its
    /// chain of ties that carry electronic detonators. Detonators are numbered along the ties of the chain; LEVEL 1 is the
    /// deepest or the topmost detonator of a hole (<paramref name="levels"/>), and within a hole the order numbers follow the
    /// levels. Delays are the holes' programmed electronic times.
    /// </summary>
    /// <param name="header">The header and the zone set in the export window; without it, the name of the blast
    /// (<see cref="DefaultFullName"/>), the moment of <paramref name="now"/> and the zone of the site.</param>
    public static BpdBuildResult Build(BlastProject project, IEnumerable<Hole> holes, IReadOnlyList<BpdPathRequest> paths, BpdDelayUnit unit, string sourceFile, DateTime? now = null, BpdLevelOrder levels = BpdLevelOrder.BottomFirst, BpdHeader? header = null)
    {
        var errors = new List<BpdMessage>();
        var warnings = new List<BpdMessage>();
        var full = header?.FullName ?? DefaultFullName(project);
        var plan = new BpdPlan
        {
            Created = header?.Created ?? now ?? DateTime.Now,
            FullName = full,
            DisplayedName = PadDisplayed(header?.DisplayedName ?? DefaultDisplayedName(full)),
            SourceFile = header?.SourceFile ?? sourceFile,
            UtmZoneNumber = header?.UtmZone ?? project.Coordinates.UtmZone,
            UtmZoneLetter = (header?.Southern ?? project.Coordinates.SouthernHemisphere) ? "S" : "N"
        };
        var chains = Chains(project);
        var byKey = chains.ToDictionary(c => c.Key);
        if (header != null)
        {
            // what the window let through is checked again: a file the programming unit refuses is never written
            if (header.FullName.Length == 0 || header.FullName.Length > FullNameLength || ToFileName(header.FullName, FullNameLength) != header.FullName)
                errors.Add(new BpdMessage("FULL_NAME: Latin letters, digits and '_', 1 to {0} characters.", FullNameLength));
            if (header.DisplayedName.Length == 0 || header.DisplayedName.Length > NameLength || ToFileName(header.DisplayedName, NameLength) != header.DisplayedName)
                errors.Add(new BpdMessage("DISPLAYED_NAME: Latin letters, digits and '_', 1 to {0} characters.", NameLength));
            if (header.SourceFile.Length > SourceLength || header.SourceFile.Any(c => c < 32 || c > 126))
                errors.Add(new BpdMessage("SOURCE_FILE: printable Latin characters, up to {0}.", SourceLength));
            if (header.UtmZone is < 1 or > 60) errors.Add(new BpdMessage("The UTM zone number is 1 to 60."));
        }
        else if (project.Coordinates.UtmZone < 1) warnings.Add(new BpdMessage("The UTM zone is not set (Edit ▸ Site coordinate system): zone 0 is written."));
        if (paths.Count == 0) errors.Add(new BpdMessage("There are no ESI ties in the plan: the lines (PATH) are made by chains of ties of electronic detonators."));
        if (paths.Count > 26) errors.Add(new BpdMessage("No more than 26 lines (A–Z) can be written."));
        foreach (var dup in paths.GroupBy(p => p.ChainKey).Where(g => g.Count() > 1))
            errors.Add(new BpdMessage("The chain is chosen for more than one line: {0}", byKey.TryGetValue(dup.Key, out var dc) ? ChainName(dc) : dup.Key));

        var chainOf = HoleChains(project, chains);
        var chosen = holes.Where(h => !h.IsDummy).ToList();
        var keys = paths.Select(p => p.ChainKey).ToHashSet();
        var notTied = chosen.Where(h => !chainOf.ContainsKey(h.Id) && HasElectronic(project, h)).Select(h => h.Id).ToList();
        if (notTied.Count > 0) warnings.Add(new BpdMessage("{0} hole(s) with electronic detonators are on no chain of ESI ties and are not written.", notTied.Count) { Holes = notTied });
        var other = chosen.Where(h => chainOf.TryGetValue(h.Id, out var k) && !keys.Contains(k) && HasElectronic(project, h)).Select(h => h.Id).ToList();
        if (other.Count > 0) warnings.Add(new BpdMessage("{0} hole(s) are on a chain not chosen for any line and are not written.", other.Count) { Holes = other });

        var ids = Free(paths.Select(p => p.Line).ToList());
        for (int i = 0; i < paths.Count && i < 26; i++)
        {
            var req = paths[i];
            var id = ids[i];
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

    // ---- names --------------------------------------------------------------------------------

    /// <summary>Cyrillic letters in Latin ones, after GOST 7.79-2000 system B without diacritics (Ц as Ts); Ь and Ъ go.</summary>
    static readonly Dictionary<char, string> Translit = BuildTranslit();

    static Dictionary<char, string> BuildTranslit()
    {
        var map = new Dictionary<char, string>();
        void Add(char upper, string latin)
        {
            map[upper] = latin;
            map[char.ToLowerInvariant(upper)] = latin.ToLowerInvariant();
        }
        foreach (var (c, l) in new[]
        {
            ('А', "A"), ('Б', "B"), ('В', "V"), ('Г', "G"), ('Д', "D"), ('Е', "E"), ('Ё', "Yo"), ('Ж', "Zh"), ('З', "Z"), ('И', "I"),
            ('Й', "Y"), ('К', "K"), ('Л', "L"), ('М', "M"), ('Н', "N"), ('О', "O"), ('П', "P"), ('Р', "R"), ('С', "S"), ('Т', "T"),
            ('У', "U"), ('Ф', "F"), ('Х', "Kh"), ('Ц', "Ts"), ('Ч', "Ch"), ('Ш', "Sh"), ('Щ', "Shch"), ('Ъ', ""), ('Ы', "Y"), ('Ь', ""),
            ('Э', "E"), ('Ю', "Yu"), ('Я', "Ya")
        })
            Add(c, l);
        return map;
    }

    /// <summary>
    /// A name as FULL_NAME and DISPLAYED_NAME take it: Latin letters and digits stay, Cyrillic goes into Latin
    /// (<see cref="Translit"/>), any other character becomes '_', '_' in a row become one; cut to <paramref name="max"/>.
    /// "Карьер Север, блок 12" → "Karer_Sever_blok_12".
    /// </summary>
    public static string ToFileName(string text, int max)
    {
        var s = new StringBuilder();
        foreach (var c in text)
        {
            string piece = c < 128 && char.IsLetterOrDigit(c) ? c.ToString() : Translit.TryGetValue(c, out var latin) ? latin : "_";
            foreach (var x in piece)
                if (x != '_' || s.Length == 0 || s[^1] != '_') s.Append(x);
        }
        return s.Length > max ? s.ToString(0, max) : s.ToString();
    }

    /// <summary>FULL_NAME by default: the number of the blast, or its name without one (<see cref="ToFileName"/>); BLAST for none.</summary>
    public static string DefaultFullName(BlastProject project)
    {
        var s = ToFileName(project.Header.BlastNumber.Length > 0 ? project.Header.BlastNumber : project.Header.Name, FullNameLength);
        return s.Trim('_').Length == 0 ? "BLAST" : s;
    }

    /// <summary>DISPLAYED_NAME by default: the first 8 characters of the full name, in capitals.</summary>
    public static string DefaultDisplayedName(string fullName) => ToFileName(fullName, NameLength).ToUpperInvariant();

    /// <summary>DISPLAYED_NAME as written: padded with '_' to 8 characters (BLAST for none).</summary>
    public static string PadDisplayed(string name)
    {
        var s = ToFileName(name, NameLength);
        if (s.Trim('_').Length == 0) s = "BLAST";
        return s.PadRight(NameLength, '_');
    }

    /// <summary>
    /// What each chain gives the file, in the order of <paramref name="chains"/>: its electronic detonators and its frame
    /// (DETZONE: west, south, east, north, UTM metres); built as the file is (<see cref="Build"/>), so the window shows what
    /// is written. A chain with nothing to write gives 0 and no frame.
    /// </summary>
    public static List<(int Detonators, (long West, long South, long East, long North)? Frame)> Preview(BlastProject project, IEnumerable<Hole> holes, IReadOnlyList<BpdChain> chains, IReadOnlyList<string> lines)
    {
        var requests = chains.Select((c, i) => new BpdPathRequest(c.Key, "1", lines[i])).ToList();
        var plan = Build(project, holes, requests, BpdDelayUnit.Ms, "").Plan;
        return lines.Select(l => plan.Paths.FirstOrDefault(p => p.Id == l) is { } p
            ? (p.DetonatorCount, ((long, long, long, long)?)(p.West, p.South, p.East, p.North))
            : (0, null)).ToList();
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
