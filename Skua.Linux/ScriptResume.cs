using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Linux;

/// <summary>
/// SKUA_RESUME_SCRIPTS=1: each tab's script survives a restart of VibeSkua
/// (a redeploy, a container restart) or of the tab's Skua. The tab host
/// notes from its status poll which script each tab has loaded and whether
/// it runs, in resume.json next to accounts.json:
///
///   { "tabs": { "1": { "account": "...", "script": "/config/.../Farm.cs", "running": true } } }
///
/// A tab given no script of its own (SKUA_SCRIPT_N, or its account in the
/// accounts file) loads its saved one, and starts it once logged in if it
/// was running; this comes before the every-tab SKUA_SCRIPT. Only for the
/// same account: a tab given another account starts fresh.
///
/// A script counts as stopped only once it has stayed stopped, logged in, for
/// <see cref="StopGrace"/>: a recycle, a relogin's restart, or the shutdown
/// itself stop it for a moment and must not forget that it ran. A tab closed
/// on purpose keeps its script loaded but not started.
/// </summary>
public static class ScriptResume
{
    public sealed class Entry
    {
        public string? Account { get; set; }
        public string Script { get; set; } = "";
        public bool Running { get; set; }
        public DateTime At { get; set; }
    }

    private sealed class FileModel
    {
        public Dictionary<string, Entry> Tabs { get; set; } = new();
    }

    public static readonly TimeSpan StopGrace = TimeSpan.FromMinutes(2);

    public static bool Enabled { get; } =
        SkuaRuntime.EnvRaw("SKUA_RESUME_SCRIPTS")?.ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static string FilePath { get; } = SkuaRuntime.EnvRaw("VIBESKUA_RESUME_FILE")
        ?? Path.Combine(Path.GetDirectoryName(AccountStore.FilePath)!, "resume.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly object Lock = new();
    private static FileModel? _file;
    // Since when each tab (1-based) has had its script stopped, or no script.
    private static readonly Dictionary<int, DateTime> StoppedSince = new();
    // When each tab was last seen running a script.
    private static readonly Dictionary<int, DateTime> LastRunning = new();

    private static FileModel Read()
    {
        if (_file is not null)
            return _file;
        try
        {
            _file = File.Exists(FilePath) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] resume: could not read {FilePath}: {e.Message}");
        }
        return _file ??= new FileModel();
    }

    private static void Write(FileModel file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] resume: could not write {FilePath}: {e.Message}");
        }
    }

    /// <summary>The saved script for tab (1-based), if it was this account's.</summary>
    public static Entry? For(int tab, string? account)
    {
        if (!Enabled)
            return null;
        lock (Lock)
        {
            if (!Read().Tabs.TryGetValue(tab.ToString(), out var entry) || entry.Script.Length == 0)
                return null;
            if (entry.Account is { } saved && account is not null && !saved.Equals(account, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!File.Exists(entry.Script))
            {
                Console.Error.WriteLine($"[host] resume: tab {tab}'s saved script {entry.Script} is gone");
                return null;
            }
            return entry;
        }
    }

    /// <summary>
    /// How long after its Skua started a tab may still be bringing a saved
    /// running script back (logging in, waiting for the script sync) without
    /// that counting as stopped.
    /// </summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(20);

    /// <summary>
    /// What tab (1-based) has now, from its status while logged in: the
    /// loaded script (null for none) and whether it runs. Writes the file when
    /// that changes. started: when the tab's Skua started.
    /// </summary>
    public static void Note(int tab, string? account, string? script, bool running, DateTime started)
    {
        if (!Enabled)
            return;
        lock (Lock)
        {
            var file = Read();
            string key = tab.ToString();
            file.Tabs.TryGetValue(key, out var entry);
            if (running)
                LastRunning[tab] = DateTime.UtcNow;
            // A saved running script not back yet since this start.
            else if (entry is { Running: true } && LastRunning.GetValueOrDefault(tab) < started
                     && DateTime.UtcNow - started < ResumeWindow)
                return;
            if (running && script is { Length: > 0 })
            {
                StoppedSince.Remove(tab);
                if (entry is { Running: true } && entry.Script == script && entry.Account == account)
                    return;
                file.Tabs[key] = new Entry { Account = account, Script = script, Running = true, At = DateTime.UtcNow };
                Write(file);
                return;
            }
            // Stopped, or nothing loaded: only once it has stayed so a while.
            var now = DateTime.UtcNow;
            if (!StoppedSince.TryGetValue(tab, out var since))
            {
                StoppedSince[tab] = now;
                return;
            }
            if (now - since < StopGrace)
                return;
            if (script is { Length: > 0 })
            {
                if (entry is { Running: false } && entry.Script == script && entry.Account == account)
                    return;
                file.Tabs[key] = new Entry { Account = account, Script = script, Running = false, At = now };
            }
            else if (!file.Tabs.Remove(key))
                return;
            Write(file);
        }
    }

    /// <summary>Not logged in (kicked, relogging): the stop clock starts over.</summary>
    public static void NotPlaying(int tab)
    {
        lock (Lock)
            StoppedSince.Remove(tab);
    }

    /// <summary>
    /// The tab was closed on purpose: its script is kept (loaded when it is
    /// opened again) but not started.
    /// </summary>
    public static void Closed(int tab)
    {
        if (!Enabled)
            return;
        lock (Lock)
        {
            StoppedSince.Remove(tab);
            var file = Read();
            if (file.Tabs.TryGetValue(tab.ToString(), out var entry) && entry.Running)
            {
                entry.Running = false;
                entry.At = DateTime.UtcNow;
                Write(file);
            }
        }
    }
}
