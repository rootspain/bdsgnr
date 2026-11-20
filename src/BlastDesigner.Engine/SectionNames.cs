using BlastDesigner.Domain;
using BlastDesigner.Domain.Model;

namespace BlastDesigner.Engine;

/// <summary>
/// Names of the section lines (<see cref="LineKind.CrossSection"/>), kept in their <see cref="PolylineEntity.Label"/>: a
/// Latin letter in turn, A, B … Z, then A1 … Z1, A2 and so on, the first one no other section line has. The letter stands
/// at each end of the line on the plan and the section is called A–A. Two section lines never share a name.
/// </summary>
public static class SectionNames
{
    /// <summary>Longest name typed in.</summary>
    public const int MaxLength = 12;

    /// <summary>The section lines of the project, in the order of the project.</summary>
    public static List<PolylineEntity> Lines(BlastProject project) =>
        project.Entities.OfType<PolylineEntity>().Where(l => l.Kind == LineKind.CrossSection).ToList();

    /// <summary>The name in its place: A … Z, A1 … Z1, A2 …</summary>
    public static string Name(int index) => index < 26 ? ((char)('A' + index)).ToString() : $"{(char)('A' + index % 26)}{index / 26}";

    /// <summary>The section as it is called: A–A. Empty for a line without a name.</summary>
    public static string Caption(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $"{name.Trim()}–{name.Trim()}";

    /// <summary>The first name in turn no section line of the project has.</summary>
    public static string Next(BlastProject project)
    {
        var used = Lines(project).Select(l => l.Label.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; ; i++)
            if (!used.Contains(Name(i))) return Name(i);
    }

    /// <summary>Another section line than <paramref name="line"/> has the name (letters in any case).</summary>
    public static bool Taken(BlastProject project, Guid line, string name) =>
        name.Trim().Length > 0 && Lines(project).Any(l => l.Id != line && string.Equals(l.Label.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The section lines without a name take one, in their order (a project from before the names). A name of the user's
    /// stays, the same one on two lines too.
    /// </summary>
    public static void NameAll(BlastProject project)
    {
        foreach (var l in Lines(project))
            if (l.Label.Trim().Length == 0) l.Label = Next(project);
    }

    /// <summary>
    /// Before a change of the project: the names of its section lines. After it, a section line without a name (one just
    /// drawn, a line made a section line, a name cleared) takes the next one; a line new or renamed whose name another
    /// section line has (a copy pasted) takes the next one too. The lines as they were keep their names.
    /// </summary>
    public static Action? Follow(BlastProject project)
    {
        var before = Lines(project).ToDictionary(l => l.Id, l => l.Label);
        return () =>
        {
            foreach (var l in Lines(project))
            {
                if (l.Label.Trim().Length == 0) { l.Label = Next(project); continue; }
                var changed = !before.TryGetValue(l.Id, out var was) || was != l.Label;
                if (changed && Taken(project, l.Id, l.Label)) l.Label = Next(project);
            }
        };
    }
}
