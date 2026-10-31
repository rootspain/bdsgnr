using System.Text;
using System.Text.Json;

namespace BlastDesigner.IO;

/// <summary>One recorded action: a button, tab, menu item, hot key, view flag, dialog, or the app becoming (in)active.</summary>
/// <param name="Old">Previous value of a flag (to find flags switched back).</param>
public sealed record UsageEvent(DateTime Time, string Source, string Id, string? Value = null, string? Old = null, string Session = "");

/// <summary>
/// Local record of what the user does (Settings ▸ Usage statistics): one JSON line per event in Logs/usage-yyyyMMdd.jsonl.
/// Events collect in memory and go to the file every 15 seconds or every 30 events, whichever comes first.
/// Only command ids are written — no file names, coordinates or project data. Writing never breaks the program.
/// </summary>
public static class UsageLog
{
    public const string Button = "button", Tab = "tab", Menu = "menu", Hotkey = "hotkey", Flag = "flag", Dialog = "dialog", App = "app", SessionSource = "session";
    public const int FlushCount = 30;
    public const int KeepDays = 90;

    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private static List<UsageEvent> _buffer = new();
    private static Timer? _timer;

    /// <summary>Time between writes of the buffer; settable for tests.</summary>
    public static TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(15);
    public static bool Enabled { get; private set; }
    public static string Folder { get; set; } = ProgramSettings.LogFolder;
    public static string Session { get; private set; } = string.Empty;
    public static int Pending { get { lock (Gate) return _buffer.Count; } }

    public static string FilePath(DateTime day) => Path.Combine(Folder, $"usage-{day:yyyyMMdd}.jsonl");
    public static IEnumerable<string> Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "usage-*.jsonl").OrderBy(f => f) : Enumerable.Empty<string>();

    /// <summary>Starts a session: records its start and writes the buffer every <see cref="FlushInterval"/>.</summary>
    public static void Start(string version)
    {
        if (Enabled) return;
        Enabled = true;
        Session = Guid.NewGuid().ToString("N")[..12];
        Prune();
        Track(SessionSource, "start", version);
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    /// <summary>Records the end of the session and writes what is left.</summary>
    public static void Stop()
    {
        if (!Enabled) return;
        Track(SessionSource, "end");
        Enabled = false;
        _timer?.Dispose();
        _timer = null;
        Flush();
    }

    public static void Track(string source, string id, string? value = null, string? old = null)
    {
        if (!Enabled || string.IsNullOrEmpty(id)) return;
        bool full;
        lock (Gate)
        {
            _buffer.Add(new UsageEvent(DateTime.Now, source, id, value, old, Session));
            full = _buffer.Count >= FlushCount;
        }
        if (!full) return;
        Flush();
        // the count wrote the buffer: the next timed write is a full interval away
        _timer?.Change(FlushInterval, FlushInterval);
    }

    /// <summary>Appends the buffered events to the day's file (several copies of the program may append at once).</summary>
    public static void Flush()
    {
        List<UsageEvent> batch;
        lock (Gate)
        {
            if (_buffer.Count == 0) return;
            batch = _buffer;
            _buffer = new();
        }
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (var day in batch.GroupBy(e => e.Time.Date))
                {
                    using var fs = new FileStream(FilePath(day.Key), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    var text = new StringBuilder();
                    foreach (var e in day) text.Append(ToJson(e)).Append('\n');
                    var bytes = Encoding.UTF8.GetBytes(text.ToString());
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // Statistics must never break the application.
            }
        }
    }

    /// <summary>Deletes the files older than <see cref="KeepDays"/> days, and all of them with <paramref name="all"/>.</summary>
    public static void Prune(bool all = false)
    {
        try
        {
            var limit = DateTime.Today.AddDays(-KeepDays);
            foreach (var f in Files())
                if (all || File.GetLastWriteTime(f) < limit) File.Delete(f);
        }
        catch
        {
            // a file another copy still writes stays until next time
        }
    }

    public static string ToJson(UsageEvent e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            w.WriteString("s", e.Source);
            w.WriteString("id", e.Id);
            if (e.Value != null) w.WriteString("v", e.Value);
            if (e.Old != null) w.WriteString("o", e.Old);
            if (e.Session.Length > 0) w.WriteString("sid", e.Session);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Reads one line; null for a broken one (the last line of a file cut by a crash).</summary>
    public static UsageEvent? FromJson(string line)
    {
        try
        {
            using var d = JsonDocument.Parse(line);
            var r = d.RootElement;
            string? S(string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (S("t") is not string t || !DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)) return null;
            if (S("s") is not string s || S("id") is not string id) return null;
            return new UsageEvent(time, s, id, S("v"), S("o"), S("sid") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
