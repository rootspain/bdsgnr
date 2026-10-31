namespace BlastDesigner.IO;

/// <summary>
/// What the usage log says (Settings ▸ Usage statistics): the most used buttons, tabs, menu items and hot keys, the view flags
/// switched on and back off, and the commands after which the user paused for a long time. Time while no window of the
/// program is active does not count as a pause, and a pause longer than <see cref="Away"/> means the user was away.
/// </summary>
public sealed class UsageAnalysis
{
    public sealed record Count(string Source, string Id, int Times);
    public sealed record Revert(string Flag, int Changes, int Reverts, double MedianSeconds);
    public sealed record Pause(string Source, string Id, int LongPauses, double MedianSeconds, int Times);

    public static readonly string[] Commands = { UsageLog.Button, UsageLog.Tab, UsageLog.Menu, UsageLog.Hotkey };

    public TimeSpan RevertWithin { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan LongPause { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Away { get; init; } = TimeSpan.FromMinutes(30);

    public int Events { get; private set; }
    public int Sessions { get; private set; }
    public DateTime? First { get; private set; }
    public DateTime? Last { get; private set; }
    public List<Count> Counts { get; } = new();
    public List<Revert> Reverts { get; } = new();
    public List<Pause> Pauses { get; } = new();

    public IEnumerable<Count> Top(string source, int n) => Counts.Where(c => c.Source == source).Take(n);
    public IEnumerable<Pause> TopPauses(string source, int n) => Pauses.Where(p => p.Source == source && p.LongPauses > 0).Take(n);

    /// <summary>Events of the usage files from <paramref name="from"/> on, in time order; broken lines are skipped.</summary>
    public static List<UsageEvent> Read(IEnumerable<string> files, DateTime? from = null)
    {
        var events = new List<UsageEvent>();
        foreach (var f in files)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var r = new StreamReader(fs);
                while (r.ReadLine() is { } line)
                    if (UsageLog.FromJson(line) is { } e && (from == null || e.Time >= from)) events.Add(e);
            }
            catch (IOException)
            {
                // a file being deleted is skipped
            }
        }
        // stable: events of one moment keep their written order
        return events.Select((e, i) => (e, i)).OrderBy(x => x.e.Time).ThenBy(x => x.i).Select(x => x.e).ToList();
    }

    public UsageAnalysis Analyse(IReadOnlyList<UsageEvent> events)
    {
        Events = events.Count(e => e.Source != UsageLog.SessionSource && e.Source != UsageLog.App);
        Sessions = events.Select(e => e.Session).Distinct().Count();
        First = events.Count > 0 ? events[0].Time : null;
        Last = events.Count > 0 ? events[^1].Time : null;

        Counts.AddRange(events.Where(e => Commands.Contains(e.Source)).GroupBy(e => (e.Source, e.Id))
            .Select(g => new Count(g.Key.Source, g.Key.Id, g.Count())).OrderByDescending(c => c.Times).ThenBy(c => c.Id));

        foreach (var session in events.GroupBy(e => e.Session))
        {
            var list = session.ToList();
            FindReverts(list);
            FindPauses(list);
        }
        Reverts.AddRange(_flags.Select(kv => new Revert(kv.Key, kv.Value.Changes, kv.Value.Gaps.Count, Median(kv.Value.Gaps)))
            .Where(r => r.Reverts > 0).OrderByDescending(r => r.Reverts).ThenByDescending(r => (double)r.Reverts / r.Changes).ThenBy(r => r.Flag));
        Pauses.AddRange(_pauses.Select(kv => new Pause(kv.Key.Source, kv.Key.Id, kv.Value.Count, Median(kv.Value), Counts.FirstOrDefault(c => c.Source == kv.Key.Source && c.Id == kv.Key.Id)?.Times ?? 0))
            .OrderByDescending(p => p.LongPauses).ThenByDescending(p => p.MedianSeconds).ThenBy(p => p.Id));
        return this;
    }

    private readonly Dictionary<string, (int Changes, List<double> Gaps)> _flags = new();
    private readonly Dictionary<(string Source, string Id), List<double>> _pauses = new();

    /// <summary>A flag changed and changed back to what it was before within <see cref="RevertWithin"/>.</summary>
    private void FindReverts(List<UsageEvent> session)
    {
        var last = new Dictionary<string, UsageEvent>();
        foreach (var e in session.Where(e => e.Source == UsageLog.Flag))
        {
            var (changes, gaps) = _flags.TryGetValue(e.Id, out var f) ? f : (0, new List<double>());
            if (last.TryGetValue(e.Id, out var prev) && prev.Old != null && e.Value == prev.Old && e.Time - prev.Time <= RevertWithin)
            {
                gaps.Add((e.Time - prev.Time).TotalSeconds);
                // a revert ends the pair: switching it again starts a new one
                last.Remove(e.Id);
            }
            else last[e.Id] = e;
            _flags[e.Id] = (changes + 1, gaps);
        }
    }

    /// <summary>Active time from each command to the next action of the session; long ones are kept per command.</summary>
    private void FindPauses(List<UsageEvent> session)
    {
        for (int i = 0; i < session.Count; i++)
        {
            var e = session[i];
            if (!Commands.Contains(e.Source)) continue;
            double? pause = null;
            var since = e.Time;
            var active = true;
            var total = TimeSpan.Zero;
            for (int k = i + 1; k < session.Count; k++)
            {
                var n = session[k];
                if (n.Source == UsageLog.App)
                {
                    if (n.Id == "inactive" && active) { total += n.Time - since; active = false; }
                    else if (n.Id == "active" && !active) { since = n.Time; active = true; }
                    continue;
                }
                if (n.Source == UsageLog.SessionSource) break;
                if (active) total += n.Time - since;
                pause = total.TotalSeconds;
                break;
            }
            if (pause is not double p || p > Away.TotalSeconds || p < LongPause.TotalSeconds) continue;
            var key = (e.Source, e.Id);
            if (!_pauses.TryGetValue(key, out var list)) _pauses[key] = list = new();
            list.Add(p);
        }
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
