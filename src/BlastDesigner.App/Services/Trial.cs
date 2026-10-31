using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BlastDesigner.IO;
using Microsoft.Win32;

namespace BlastDesigner.App.Services;

public enum TrialState { Full, Active, Expired, ClockBack, Tampered }

/// <summary>
/// Trial build (dotnet publish -p:Trial=true [-p:TrialDays=30] [-p:TrialValidDays=N]); no licence, no network.
/// The period runs from the first start of this program version, so the next version starts a new trial.
/// Each start is recorded with its UTC time in three places (registry, roaming and local app data), signed with a
/// key tied to this computer. The latest time ever seen only moves forward — while the program runs it also advances
/// by a monotonic timer — so turning the clock back gains nothing, and a clock behind the latest time seen stops the program.
/// Editing or copying the records ends the trial; deleting all three restarts it, up to the optional TrialValidDays
/// limit counted from the build date.
/// </summary>
public static class Trial
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromHours(2);
    private const int LogSize = 20;
    private static readonly byte[] Secret = Convert.FromBase64String("q3V0b0JsYXN0RGVzaWduZXJUcmlhbFNhbHQ9Pz8hKg==");

    private static readonly DateTime? Issued = Date(Stamp("TrialIssued"));
    private static readonly int Days = int.TryParse(Stamp("TrialDays"), out var d) ? d : 0;
    private static readonly int ValidDays = int.TryParse(Stamp("TrialValidDays"), out var v) ? v : 0;
    private static readonly string VersionKey = typeof(Trial).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Current UTC time; replaced only by tests.</summary>
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    private static readonly object Sync = new();
    private static Record? _record;
    private static DateTime _sessionStart;
    private static readonly Stopwatch Running = new();
    private static Timer? _timer;

    public static bool IsTrial => Issued != null && Days > 0;
    public static TrialState State { get; private set; } = TrialState.Full;

    /// <summary>Whole days left, counting today.</summary>
    public static int DaysLeft
    {
        get
        {
            if (_record == null) return 0;
            var used = Effective(Clock()) - _record.FirstRun;
            var left = (int)Math.Ceiling(Days - used.TotalDays);
            if (ValidDays > 0) left = Math.Min(left, (int)Math.Ceiling((Issued!.Value.AddDays(ValidDays) - Effective(Clock())).TotalDays));
            return Math.Max(0, left);
        }
    }

    /// <summary>"Trial version, days left: N" for the title bar and the startup window; empty in the full version.</summary>
    public static string Status => IsTrial ? Loc.F("Trial version, days left: {0}", DaysLeft) : "";

    /// <summary>Why the program may not run, or null when it may.</summary>
    public static string? Message => State switch
    {
        TrialState.Expired => Loc.F("The trial period of version {0} has ended.", VersionKey),
        TrialState.ClockBack => Loc.F("The date and time on this computer are earlier than the last time the program was run ({0}). Set the correct date and time and start the program again.",
            _record!.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
        TrialState.Tampered => Loc.T("The trial data on this computer has been changed. The trial period has ended."),
        _ => null
    };

    /// <summary>Checks the trial at program start and records this start. True when the program may run.</summary>
    public static bool Start()
    {
        if (!IsTrial) { State = TrialState.Full; return true; }
        lock (Sync)
        {
            var now = Clock();
            var records = Stores.Select(s => s.Read(VersionKey)).Where(t => t != null).Select(t => Record.Parse(t!)).ToList();
            if (records.Any(r => r == null)) return Stop(TrialState.Tampered);
            var rec = Record.Merge(records!) ?? new Record { FirstRun = now, LastSeen = now };
            _record = rec;
            if (now < Issued!.Value.AddDays(-1) || now < rec.LastSeen - Tolerance || rec.Log.Any(t => t > now + Tolerance))
                return Stop(TrialState.ClockBack);
            rec.LastSeen = Effective(now);
            rec.Launches++;
            rec.Log.Add(now);
            if (rec.Log.Count > LogSize) rec.Log.RemoveRange(0, rec.Log.Count - LogSize);
            Save(rec);
            if (DaysLeft <= 0) return Stop(TrialState.Expired);
            State = TrialState.Active;
            _sessionStart = rec.LastSeen;
            Running.Restart();
            _timer = new Timer(_ => Touch(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Touch();
            return true;
        }
    }

    private static bool Stop(TrialState state)
    {
        State = state;
        Log.Info("Trial: " + state);
        return false;
    }

    /// <summary>The latest time seen: never earlier than before, and at least the session start plus the time the program has run.</summary>
    private static DateTime Effective(DateTime now)
    {
        var t = _record!.LastSeen > now ? _record.LastSeen : now;
        if (Running.IsRunning && _sessionStart + Running.Elapsed > t) t = _sessionStart + Running.Elapsed;
        return t;
    }

    private static void Touch()
    {
        lock (Sync)
        {
            if (_record == null || State != TrialState.Active) return;
            _record.LastSeen = Effective(Clock());
            Save(_record);
        }
    }

    private static void Save(Record rec)
    {
        var text = rec.ToString();
        foreach (var s in Stores)
            try { s.Write(VersionKey, text); }
            catch (Exception ex) { Log.Error("Trial record not saved: " + s.GetType().Name, ex); }
    }

    private static string? Stamp(string key) =>
        typeof(Trial).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    private static DateTime? Date(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d.Date : null;

    // ---- records ----

    private sealed class Record
    {
        public DateTime FirstRun, LastSeen;
        public long Launches;
        public List<DateTime> Log = new();

        public override string ToString()
        {
            var body = string.Join("|", "1", VersionKey, FirstRun.Ticks, LastSeen.Ticks, Launches, string.Join(",", Log.Select(t => t.Ticks)));
            return body + "|" + Mac(body);
        }

        /// <summary>Null when the text is damaged, edited, or belongs to another computer or version.</summary>
        public static Record? Parse(string text)
        {
            var i = text.LastIndexOf('|');
            if (i < 0) return null;
            var body = text[..i];
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(body)), Encoding.ASCII.GetBytes(text[(i + 1)..]))) return null;
            var p = body.Split('|');
            if (p.Length != 6 || p[0] != "1" || p[1] != VersionKey) return null;
            try
            {
                return new Record
                {
                    FirstRun = new DateTime(long.Parse(p[2], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                    LastSeen = new DateTime(long.Parse(p[3], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                    Launches = long.Parse(p[4], CultureInfo.InvariantCulture),
                    Log = p[5].Length == 0 ? new() : p[5].Split(',').Select(t => new DateTime(long.Parse(t, CultureInfo.InvariantCulture), DateTimeKind.Utc)).ToList()
                };
            }
            catch { return null; }
        }

        /// <summary>The earliest first start and the latest time seen across all copies, so deleting one copy changes nothing.</summary>
        public static Record? Merge(List<Record> records) => records.Count == 0 ? null : new Record
        {
            FirstRun = records.Min(r => r.FirstRun),
            LastSeen = records.Max(r => r.LastSeen),
            Launches = records.Max(r => r.Launches),
            Log = records.SelectMany(r => r.Log).Distinct().Order().TakeLast(LogSize).ToList()
        };

        private static string Mac(string body)
        {
            var key = SHA256.HashData(Secret.Concat(Encoding.UTF8.GetBytes(MachineId())).ToArray());
            return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
        }
    }

    private static string? _machineId;

    private static string MachineId() => _machineId ??= ReadMachineId() ?? Environment.MachineName + "/" + Environment.UserName;

    private static string? ReadMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        catch { }
        return null;
    }

    // ---- storage: every copy holds the whole record ----

    private static readonly IStore[] Stores =
    {
        new RegistryStore(),
        new FileStore(Path.Combine(ProgramSettings.AppDataFolder, "trial.dat")),
        new FileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".bdcache", "state.bin"))
    };

    private interface IStore
    {
        string? Read(string version);
        void Write(string version, string text);
    }

    private sealed class RegistryStore : IStore
    {
        private const string KeyPath = @"Software\BlastDesigner\Trial";

        public string? Read(string version)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try { using var k = Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(version) as string; }
            catch { return null; }
        }

        public void Write(string version, string text)
        {
            if (!OperatingSystem.IsWindows()) return;
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            k.SetValue(version, text);
        }
    }

    /// <summary>One line per program version: "version TAB record".</summary>
    private sealed class FileStore(string path) : IStore
    {
        public string? Read(string version)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return File.ReadAllLines(path).Select(l => l.Split('\t', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == version)?[1];
            }
            catch { return null; }
        }

        public void Write(string version, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => !l.StartsWith(version + "\t", StringComparison.Ordinal)).ToList() : new List<string>();
            lines.Add(version + "\t" + text);
            File.WriteAllLines(path, lines);
        }
    }
}
