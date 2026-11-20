using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Route numbers of the holes with an electronic detonator (<see cref="Hole.RouteNumber"/>): the place of a hole in its
/// chain of ties, in the order the ties were made. The first hole of a chain is 1, each hole tied from another the number
/// of that one plus 1. Each chain has its letter (<see cref="Hole.RouteLine"/>): A the first, B the second and so on. A tie
/// deleted in the middle of a chain makes the holes after it a chain of their own, of the next letter, numbered from 1. A tie is made to a hole with an
/// electronic detonator only where its chain starts: not into a hole a tie comes into already, and not one that would close
/// the chain into a ring.
/// </summary>
public static class RouteNumbers
{
    static bool Electronic(BlastProject project, Guid id, out Hole hole)
    {
        hole = null!;
        if (project.FindEntity(id) is not Hole h || !Charging.HasElectronicDetonator(project, h)) return false;
        hole = h;
        return true;
    }

    /// <summary>
    /// Whether a tie from <paramref name="from"/> may be made to <paramref name="to"/>. To a hole with an electronic
    /// detonator only when no tie comes into it yet (<see cref="HasTieIn"/>: it starts its chain, or has no tie at all) and
    /// the two are not tied to each other already, directly or along other ties (the tie would close a ring: back from the
    /// last hole of a chain to its first, or back between two holes tied). The end of one chain may so be tied to the
    /// start of another.
    /// </summary>
    public static bool CanTie(BlastProject project, Guid from, Guid to) => Refusal(project, from, to) == null;

    /// <summary>
    /// Why a tie from <paramref name="from"/> to <paramref name="to"/> is not made, as a format (the translation key) and the
    /// hole it names; null when it may be. Into a hole with an electronic detonator: see <see cref="CanTie"/>. Out of one:
    /// not when a tie goes out of it already, to the next hole of its chain (<see cref="HasTieOut"/>): a chain does not branch,
    /// or two holes would take the same number.
    /// </summary>
    public static (string Format, string Hole)? Refusal(BlastProject project, Guid from, Guid to)
    {
        if (Electronic(project, to, out var target) && (HasTieIn(project, target) || Joined(project, from, to))) return (Into, target.Label);
        if (Electronic(project, from, out var source) && HasTieOut(project, source)) return (Out, source.Label);
        return null;
    }

    public const string Into = "Hole {0} has an electronic detonator: a tie comes into it already, or this one would close a ring. A tie goes to the start of a chain only";
    public const string Out = "Hole {0} has an electronic detonator and a tie going out of it already: a chain does not branch";

    /// <summary>
    /// A tie goes out of the hole to the next hole of its chain: by the route numbers, a hole with an electronic detonator
    /// tied to it numbered one more; for holes without a number, by the direction of the tie.
    /// </summary>
    public static bool HasTieOut(BlastProject project, Hole hole)
    {
        foreach (var t in project.Ties.Where(t => t.FromId == hole.Id || t.ToId == hole.Id))
        {
            var otherId = t.FromId == hole.Id ? t.ToId : t.FromId;
            if (!Electronic(project, otherId, out var other)) continue;
            if (hole.RouteNumber is int n && other.RouteNumber is int m)
            {
                if (m == n + 1) return true;
            }
            else if (t.FromId == hole.Id) return true;
        }
        return false;
    }

    /// <summary>
    /// A tie comes into the hole. By the route numbers when it has one, so that a tie turned round from the initiation
    /// point does not change it: a hole tied to it numbered one less (or a tie from an initiation point or a hole without
    /// a number into it). A hole without a number (tied before the numbers): by the direction of its ties.
    /// </summary>
    public static bool HasTieIn(BlastProject project, Hole hole)
    {
        foreach (var t in project.Ties.Where(t => t.FromId == hole.Id || t.ToId == hole.Id))
        {
            var other = t.FromId == hole.Id ? t.ToId : t.FromId;
            var otherNumber = (project.FindEntity(other) as Hole)?.RouteNumber;
            if (hole.RouteNumber is int n && otherNumber is int m)
            {
                if (m == n - 1) return true;
            }
            else if (t.ToId == hole.Id) return true;
        }
        return false;
    }

    /// <summary>The two are tied to each other, directly or along other ties (either way, through any object).</summary>
    static bool Joined(BlastProject project, Guid a, Guid b)
    {
        var next = new Dictionary<Guid, List<Guid>>();
        foreach (var t in project.Ties)
        {
            (next.TryGetValue(t.FromId, out var f) ? f : next[t.FromId] = new()).Add(t.ToId);
            (next.TryGetValue(t.ToId, out var g) ? g : next[t.ToId] = new()).Add(t.FromId);
        }
        var seen = new HashSet<Guid> { a };
        var queue = new Queue<Guid>();
        queue.Enqueue(a);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (id == b) return true;
            if (next.TryGetValue(id, out var list))
                foreach (var n in list)
                    if (seen.Add(n)) queue.Enqueue(n);
        }
        return false;
    }

    /// <summary>
    /// Numbers the holes of the ties just made, in the order given (the order they were drawn, from → to): the hole a tie
    /// starts from keeps its number, or is 1 when it has none (a new chain); the hole it goes to is that number plus 1. A
    /// tie from an initiation point or a hole without an electronic detonator gives the hole it goes to 1. The chain that
    /// hole started (tied to the start of another chain) goes on from it: each hole after it 1 more than the one before.
    /// </summary>
    public static void Number(BlastProject project, IEnumerable<(Guid From, Guid To)> ties)
    {
        var list = ties.ToList();
        var made = list.Select(t => (t.From, t.To)).ToHashSet();
        // a tie whose start is not numbered yet but will be by a tie later in the list waits for it
        var pending = list.ToList();
        for (var pass = 0; pending.Count > 0 && pass <= list.Count; pass++)
        {
            var waiting = new List<(Guid From, Guid To)>();
            foreach (var (from, to) in pending)
            {
                if (!Electronic(project, to, out var target)) continue;
                int start;
                string line;
                if (Electronic(project, from, out var source))
                {
                    if (source.RouteNumber is not int n)
                    {
                        // numbered by a tie still to come in this list: later
                        if (pass == 0 && pending.Any(t => t.To == from)) { waiting.Add((from, to)); continue; }
                        // tied already to a numbered hole (its number lost to a tie deleted before): one more than it, on
                        // its chain; otherwise the start of a new chain: the letter of the chain it is tied to the start
                        // of, or the next one
                        var (number, before) = Before(project, source, made);
                        source.RouteNumber = n = number + 1;
                        source.RouteLine = before ?? target.RouteLine ?? NextLine(project);
                    }
                    line = source.RouteLine ??= target.RouteLine ?? NextLine(project);
                    start = n;
                }
                else
                {
                    start = 0;
                    line = target.RouteLine ?? NextLine(project);
                }
                var was = target.RouteNumber;
                target.RouteNumber = start + 1;
                target.RouteLine = line;
                Shift(project, target, was, made);
            }
            pending = waiting;
        }
    }

    /// <summary>
    /// The number and the letter of the hole before a hole without one in its chain: the largest number of a hole with an
    /// electronic detonator tied to it (by a tie not just made); 0 and null when there is none.
    /// </summary>
    static (int Number, string? Line) Before(BlastProject project, Hole hole, HashSet<(Guid, Guid)> made)
    {
        (int, string?) best = (0, null);
        foreach (var t in project.Ties.Where(t => (t.FromId == hole.Id || t.ToId == hole.Id) && !made.Contains((t.FromId, t.ToId))))
            if (Electronic(project, t.FromId == hole.Id ? t.ToId : t.FromId, out var other) && other.RouteNumber is int m && m > best.Item1) best = (m, other.RouteLine);
        return best;
    }

    /// <summary>The letter of a chain by its place: A, B … Z, then AA, AB and so on.</summary>
    public static string LineName(int index) => index < 26 ? ((char)('A' + index)).ToString() : LineName(index / 26 - 1) + (char)('A' + index % 26);

    /// <summary>The place of a chain letter (<see cref="LineName"/> back); -1 for none.</summary>
    public static int LineIndex(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.All(c => c is >= 'A' and <= 'Z')) return -1;
        var i = 0;
        foreach (var c in line) i = i * 26 + (c - 'A' + 1);
        return i - 1;
    }

    /// <summary>The letters of the chains of the project, A first.</summary>
    public static List<string> Lines(BlastProject project) =>
        project.Holes.Where(h => h.RouteNumber != null && h.RouteLine != null).Select(h => h.RouteLine!).Distinct()
            .OrderBy(LineIndex).ThenBy(l => l, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The chain of letter <paramref name="from"/> takes the letter <paramref name="to"/>, set by hand; the chain that had
    /// <paramref name="to"/> takes <paramref name="from"/> (the two swap). The numbers stay.
    /// </summary>
    public static void SwapLine(BlastProject project, string from, string to)
    {
        if (from == to) return;
        foreach (var h in project.Holes.Where(h => h.RouteNumber != null))
            if (h.RouteLine == from) h.RouteLine = to;
            else if (h.RouteLine == to) h.RouteLine = from;
    }

    /// <summary>The letter of a new chain: the one after the last letter of the chains of the project, A for the first.</summary>
    public static string NextLine(BlastProject project) =>
        LineName(project.Holes.Where(h => h.RouteNumber != null).Select(h => LineIndex(h.RouteLine)).DefaultIfEmpty(-1).Max() + 1);

    /// <summary>
    /// The holes after <paramref name="hole"/> in its chain, given its number before (<paramref name="was"/>), take the
    /// numbers after its new one: a hole tied to it that was numbered one more than it, and so on. A hole that had no number
    /// is followed along the direction of its ties to holes without one. The ties just made are left to <see cref="Number"/>.
    /// </summary>
    static void Shift(BlastProject project, Hole hole, int? was, HashSet<(Guid, Guid)> made)
    {
        var queue = new Queue<(Hole Hole, int? Was)>();
        queue.Enqueue((hole, was));
        var seen = new HashSet<Guid> { hole.Id };
        while (queue.Count > 0)
        {
            var (h, old) = queue.Dequeue();
            if (h.RouteNumber is not int n) continue;
            foreach (var t in project.Ties.Where(t => (t.FromId == h.Id || t.ToId == h.Id) && !made.Contains((t.FromId, t.ToId))))
            {
                var otherId = t.FromId == h.Id ? t.ToId : t.FromId;
                if (seen.Contains(otherId) || !Electronic(project, otherId, out var next)) continue;
                var after = old is int o ? next.RouteNumber == o + 1 : t.FromId == h.Id && next.RouteNumber == null;
                if (!after) continue;
                seen.Add(next.Id);
                var nextWas = next.RouteNumber;
                next.RouteNumber = n + 1;
                next.RouteLine = h.RouteLine;
                queue.Enqueue((next, nextWas));
            }
        }
    }

    /// <summary>
    /// Before a change of the project: the ties and the numbers of their ends. After it, for each tie gone (its two ends tied
    /// no more) the end that came after the other in the chain (numbered one more than it, or the first hole tied from an
    /// initiation point) and every hole after it (tied to it, numbered one more, and so on) become a chain of their own: of
    /// the next letter, numbered from 1, in the order of the chains and the numbers they had. A hole of a tie gone left with
    /// no tie at all, either end, loses its number and its letter; a part left the only one of its chain so (the hole before
    /// it alone) keeps the letter, numbered from 1. The hole the tie came from otherwise keeps its number.
    /// Null without a numbered hole.
    /// </summary>
    public static Action? Follow(BlastProject project)
    {
        if (!project.Holes.Any(h => h.RouteNumber != null)) return null;
        var numbers = project.Holes.Where(h => h.RouteNumber != null).ToDictionary(h => h.Id, h => h.RouteNumber!.Value);
        var before = project.Ties.Select(t => (t.FromId, t.ToId)).ToList();
        return () =>
        {
            var now = project.Ties.Select(t => Pair(t.FromId, t.ToId)).ToHashSet();
            var starts = new Dictionary<Guid, (Hole Hole, int Was)>();
            var ends = new HashSet<Guid>();
            foreach (var (a, b) in before)
            {
                if (now.Contains(Pair(a, b))) continue;
                ends.Add(a);
                ends.Add(b);
                numbers.TryGetValue(a, out var na);
                numbers.TryGetValue(b, out var nb);
                // the end that came after the other in the chain: numbered one more than it, or the first hole of a chain
                // tied from an initiation point (or a hole without a number). The hole the tie came from keeps its number
                Guid later;
                int was;
                if (na > 0 && nb == na + 1) (later, was) = (b, nb);
                else if (nb > 0 && na == nb + 1) (later, was) = (a, na);
                else if (na == 0 && nb == 1) (later, was) = (b, 1);
                else if (nb == 0 && na == 1) (later, was) = (a, 1);
                else continue;
                // if it is still there with the number it had
                if (project.FindEntity(later) is Hole h && h.RouteNumber == was) starts.TryAdd(h.Id, (h, was));
            }
            // the parts, all found before a number changes: each ends at a tie gone
            var parts = starts.Values.Select(s => (s.Hole, s.Was, Members: After(project, s.Hole)))
                .OrderBy(s => LineIndex(s.Hole.RouteLine)).ThenBy(s => s.Was).ToList();
            // a hole of a tie gone left with no tie at all (either end: the first hole of a chain too) is in no chain: no
            // number, no letter
            foreach (var id in ends)
                if (project.FindEntity(id) is Hole lone && lone.RouteNumber != null && !project.Ties.Any(t => t.FromId == id || t.ToId == id))
                {
                    lone.RouteNumber = null;
                    lone.RouteLine = null;
                }
            foreach (var (h, was, members) in parts)
            {
                if (h.RouteNumber == null) continue;
                // the whole chain, cut from its initiation point: it stays as it is
                if (was == 1) continue;
                // the rest of the chain before it gone (a hole left alone lost its number above): it keeps the letter
                var alone = !project.Holes.Any(o => o.RouteNumber != null && o.RouteLine == h.RouteLine && !members.Contains(o));
                var line = alone && h.RouteLine != null ? h.RouteLine : NextLine(project);
                foreach (var m in members) { m.RouteNumber -= was - 1; m.RouteLine = line; }
            }
        };

        static (Guid, Guid) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    /// <summary>A hole and the holes after it in its chain: tied to it either way, numbered one more, and so on.</summary>
    static List<Hole> After(BlastProject project, Hole hole)
    {
        var list = new List<Hole>();
        var seen = new HashSet<Guid>();
        var stack = new Stack<Hole>();
        stack.Push(hole);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            if (h.RouteNumber is not int n || !seen.Add(h.Id)) continue;
            list.Add(h);
            foreach (var t in project.Ties.Where(t => t.FromId == h.Id || t.ToId == h.Id))
                if (project.FindEntity(t.FromId == h.Id ? t.ToId : t.FromId) is Hole next && next.RouteNumber == n + 1) stack.Push(next);
        }
        return list;
    }
}
