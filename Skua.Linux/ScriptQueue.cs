using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Linux;

/// <summary>
/// A playlist of scripts per tab: the tab host starts the first, and each
/// next one when the one before has finished (stopped by itself, logged in,
/// for <see cref="DoneAfter"/>, so a relogin or a recycle in between does not
/// count) or has run its entry's minutes. Kept in queue.json next to
/// accounts.json, so a queue goes on after a redeploy:
///
///   { "tabs": { "1": { "items": [ { "path": "/config/.../Farm.cs", "minutes": 60, "options": {...} } ],
///                      "index": 0, "playing": true, "loop": false, "startedAt": "..." } } }
///
/// The tab host feeds it each tab's status (<see cref="Tick"/>) and does
/// what it asks for: start the current entry, or stop the script. A script
/// someone else starts in a queued tab pauses its queue. An entry with its
/// own options (taken in the Manager's Script options dialog) has them
/// written for the tab's account before it starts, so the same script can be
/// queued twice with different options.
/// </summary>
public static class ScriptQueue
{
    public sealed class Item
    {
        public string Path { get; set; } = "";
        /// <summary>Stop it after this many minutes and go on; 0 = until it ends.</summary>
        public int Minutes { get; set; }
        /// <summary>Its own options, written for the account before it starts; null: the saved ones.</summary>
        public ItemOptions? Options { get; set; }
    }

    /// <summary>A script's options as the Manager read them (GET /script/options): its storage and each value.</summary>
    public sealed class ItemOptions
    {
        public string Storage { get; set; } = "";
        public List<OptionValue> Values { get; set; } = new();
    }

    public sealed class OptionValue
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    public sealed class Queue
    {
        public List<Item> Items { get; set; } = new();
        /// <summary>The current entry; -1 when it was removed while running (the next one comes after it).</summary>
        public int Index { get; set; }
        public bool Playing { get; set; }
        public bool Loop { get; set; }
        /// <summary>It played its last entry (and does not loop); Play starts it over.</summary>
        public bool Finished { get; set; }
        /// <summary>When the current entry was started (UTC); null: not started yet.</summary>
        public DateTime? StartedAt { get; set; }
        /// <summary>The last thing the queue did, for the Manager.</summary>
        public string? Note { get; set; }
        public DateTime? NoteAt { get; set; }
    }

    private sealed class FileModel
    {
        public Dictionary<string, Queue> Tabs { get; set; } = new();
    }

    public enum Step { None, Start, Stop }

    /// <summary>A script stopped this long, logged in, has finished.</summary>
    public static readonly TimeSpan DoneAfter = TimeSpan.FromSeconds(45);

    // How long a requested stop may take before the script still running
    // counts as someone else's.
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);

    public static string FilePath { get; } = SkuaRuntime.EnvRaw("VIBESKUA_QUEUE_FILE")
        ?? Path.Combine(Path.GetDirectoryName(AccountStore.FilePath)!, "queue.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly object Lock = new();
    private static FileModel? _file;

    // Not saved: what this run of the tab host has seen of each tab.
    private static readonly Dictionary<int, DateTime> StoppedSince = new();
    private static readonly Dictionary<int, DateTime> StopRequested = new();
    private static readonly HashSet<int> Pending = new();
    private static readonly HashSet<int> SeenRunning = new();

    private static FileModel File()
    {
        if (_file is not null)
            return _file;
        try
        {
            if (System.IO.File.Exists(FilePath))
                _file = JsonSerializer.Deserialize<FileModel>(System.IO.File.ReadAllText(FilePath), Json);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] could not read {FilePath}: {e.Message}");
        }
        return _file ??= new FileModel();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(File(), Json));
            System.IO.File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tabs] could not save {FilePath}: {e.Message}");
        }
    }

    private static Queue Of(int tab)
    {
        var tabs = File().Tabs;
        if (!tabs.TryGetValue(tab.ToString(), out var q))
            tabs[tab.ToString()] = q = new Queue();
        return q;
    }

    private static Queue Copy(Queue q) => JsonSerializer.Deserialize<Queue>(JsonSerializer.Serialize(q, Json), Json)!;

    private static Item? Current(Queue q) => q.Index >= 0 && q.Index < q.Items.Count ? q.Items[q.Index] : null;

    private static void Note(int tab, Queue q, string note, bool notify = false)
    {
        q.Note = note;
        q.NoteAt = DateTime.UtcNow;
        Console.WriteLine($"[tabs] tab {tab} queue: {note}");
        // An entry done: the tab's own Skua notifies of the starts and stops.
        if (notify)
            Notifier.For(tab).Post(Notifier.QueueStep, "Queue", note);
    }

    public static string Name(string? path) => Path.GetFileNameWithoutExtension(path ?? "");

    /// <summary>
    /// A script path as the API takes it ("Farm/Gold", "Scripts/Farm/Gold.cs"
    /// or absolute), as the full path a tab reports when it runs it, as
    /// ScriptSync.ResolveAsync makes it.
    /// </summary>
    public static string Full(string path)
    {
        path = path.Trim();
        if (Path.IsPathRooted(path))
            return path;
        string relative = path.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
            relative = relative["Scripts/".Length..];
        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            relative += ".cs";
        return Path.Combine(Skua.Core.Models.ClientFileSources.SkuaScriptsDIR, relative);
    }

    // Ignoring case: a repository script may be spelt differently on disk.
    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // On to the next entry (or the end); the next Tick starts it.
    private static void Advance(int tab, Queue q, string why)
    {
        string done = Name(Current(q)?.Path);
        q.StartedAt = null;
        StoppedSince.Remove(tab);
        if (q.Index + 1 < q.Items.Count)
            q.Index++;
        else if (q.Loop && q.Items.Count > 0)
            q.Index = 0;
        else
        {
            q.Index = 0;
            q.Playing = false;
            q.Finished = true;
            Note(tab, q, done.Length > 0 ? $"{done} {why}; queue finished" : "queue finished", notify: true);
            return;
        }
        Note(tab, q, done.Length > 0 ? $"{done} {why}; next: {Name(Current(q)?.Path)}" : $"next: {Name(Current(q)?.Path)}", notify: true);
    }

    /// <summary>
    /// The tab's status, every poll; what to do now: start the current entry
    /// (the entry, with its options), stop the running script, or nothing.
    /// </summary>
    public static (Step Step, Item? Item) Tick(int tab, bool loggedIn, string? loaded, bool running)
    {
        lock (Lock)
        {
            if (running)
                SeenRunning.Add(tab);
            var q = Of(tab);
            if (!q.Playing || q.Items.Count == 0 || Pending.Contains(tab))
                return (Step.None, null);
            if (!loggedIn)
            {
                // A relogin or a recycle: not the end of the script.
                StoppedSince.Remove(tab);
                return (Step.None, null);
            }
            DateTime now = DateTime.UtcNow;
            if (StopRequested.TryGetValue(tab, out var asked))
            {
                if (running && now - asked < StopWait)
                    return (Step.None, null);
                StopRequested.Remove(tab);
            }
            var item = Current(q);

            if (running)
            {
                StoppedSince.Remove(tab);
                if (item is null || !Same(loaded, item.Path))
                {
                    if (q.StartedAt is null && item is not null)
                    {
                        // Its turn, but the tab runs something else: wait for that to end.
                        return (Step.None, null);
                    }
                    if (item is null && q.Index == -1)
                        return (Step.None, null);   // the removed entry, still running
                    q.Playing = false;
                    Note(tab, q, $"paused: {Name(loaded)} was started in this tab");
                    Save();
                    return (Step.None, null);
                }
                if (q.StartedAt is null)
                {
                    q.StartedAt = now;   // already running (resumed, or started by hand)
                    Save();
                }
                if (item.Minutes > 0 && now - q.StartedAt >= TimeSpan.FromMinutes(item.Minutes))
                {
                    StopRequested[tab] = now;
                    Advance(tab, q, $"ran its {item.Minutes} min");
                    Save();
                    return (Step.Stop, null);
                }
                return (Step.None, null);
            }

            if (item is null)
            {
                // The removed entry ended: on to the one after it.
                Advance(tab, q, "ended");
                Save();
                return (Step.None, null);
            }
            if (q.StartedAt is not null && !SeenRunning.Contains(tab))
            {
                // Started before this tab host did (a redeploy) and not
                // running now: start it again rather than skip it.
                q.StartedAt = null;
            }
            if (q.StartedAt is null)
            {
                Pending.Add(tab);
                q.StartedAt = now;
                Note(tab, q, $"starting {Name(item.Path)}");
                Save();
                return (Step.Start, JsonSerializer.Deserialize<Item>(JsonSerializer.Serialize(item, Json), Json));
            }
            if (!StoppedSince.TryGetValue(tab, out var since))
                StoppedSince[tab] = since = now;
            if (now - since >= DoneAfter && now - q.StartedAt >= DoneAfter)
            {
                Advance(tab, q, "ended");
                Save();
            }
            return (Step.None, null);
        }
    }

    /// <summary>What came of a Start: null when it started.</summary>
    public static void Started(int tab, string path, string? error)
    {
        lock (Lock)
        {
            Pending.Remove(tab);
            if (error is null)
            {
                SeenRunning.Add(tab);
                return;
            }
            var q = Of(tab);
            if (!Same(Current(q)?.Path, path))
                return;
            // A script running already (a resume that came first) is ours or will be noticed.
            if (error.Contains("is running", StringComparison.OrdinalIgnoreCase))
                return;
            Advance(tab, q, $"did not start ({error.Split('\n')[0]})");
            Save();
        }
    }

    public static Queue Get(int tab)
    {
        lock (Lock)
            return Copy(Of(tab));
    }

    /// <summary>Replaces the entries (and loop); the running entry keeps running.</summary>
    public static Queue Put(int tab, List<Item> items, bool loop)
    {
        lock (Lock)
        {
            var q = Of(tab);
            string? current = Current(q)?.Path;
            items = items.Where(i => !string.IsNullOrWhiteSpace(i.Path))
                .Select(i => new Item { Path = Full(i.Path), Minutes = Math.Max(0, i.Minutes), Options = Valid(i.Options) }).ToList();
            // The same script may be queued twice (with other options): the
            // entry at the same place first.
            int at = current is null ? -1
                : q.Index >= 0 && q.Index < items.Count && Same(items[q.Index].Path, current) ? q.Index
                : items.FindIndex(i => Same(i.Path, current));
            if (at >= 0)
                q.Index = at;
            else if (q.StartedAt is not null && q.Playing)
                q.Index = Math.Min(q.Index, items.Count) - 1;   // removed while running: next comes after it
            else
                q.Index = Math.Clamp(q.Index, 0, Math.Max(0, items.Count - 1));
            q.Items = items;
            q.Loop = loop;
            if (items.Count == 0)
            {
                q.Playing = false;
                q.Index = 0;
                q.StartedAt = null;
            }
            Save();
            return Copy(q);
        }
    }

    public static Queue Add(int tab, string path, int minutes, ItemOptions? options = null)
    {
        lock (Lock)
        {
            var q = Of(tab);
            q.Items.Add(new Item { Path = Full(path), Minutes = Math.Max(0, minutes), Options = Valid(options) });
            Save();
            return Copy(q);
        }
    }

    // Options worth keeping: a storage name usable as a file name, and values.
    private static ItemOptions? Valid(ItemOptions? o) =>
        o is not null && !string.IsNullOrWhiteSpace(o.Storage) && o.Storage.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && o.Values.Any(v => !string.IsNullOrEmpty(v.Name))
            ? new ItemOptions { Storage = o.Storage.Trim(), Values = o.Values.Where(v => !string.IsNullOrEmpty(v.Name)).ToList() }
            : null;

    /// <summary>Plays from the current entry, or from index (true: stop the running script first).</summary>
    public static (Queue Queue, bool StopFirst) Play(int tab, int? index, bool running)
    {
        lock (Lock)
        {
            var q = Of(tab);
            bool stop = false;
            if (q.Items.Count == 0)
                return (Copy(q), false);
            if (index is { } i && i >= 0 && i < q.Items.Count)
            {
                stop = running;
                q.Index = i;
                q.StartedAt = null;
                if (stop)
                    StopRequested[tab] = DateTime.UtcNow;
            }
            else if (Current(q) is null)
            {
                q.Index = 0;
                q.StartedAt = null;
            }
            q.Playing = true;
            q.Finished = false;
            Note(tab, q, $"playing from {Name(Current(q)?.Path)}");
            Save();
            return (Copy(q), stop);
        }
    }

    /// <summary>Stops moving on; the running script goes on.</summary>
    public static Queue Pause(int tab)
    {
        lock (Lock)
        {
            var q = Of(tab);
            if (q.Playing)
            {
                q.Playing = false;
                Note(tab, q, "paused");
                Save();
            }
            return Copy(q);
        }
    }

    /// <summary>On to the next entry; true: stop the running script.</summary>
    public static (Queue Queue, bool Stop) Skip(int tab, bool running)
    {
        lock (Lock)
        {
            var q = Of(tab);
            if (q.Items.Count == 0)
                return (Copy(q), false);
            bool stop = running && q.Playing;
            if (stop)
                StopRequested[tab] = DateTime.UtcNow;
            Advance(tab, q, "skipped");
            Save();
            return (Copy(q), stop);
        }
    }

    public static Queue Clear(int tab)
    {
        lock (Lock)
        {
            var q = Of(tab);
            q.Items.Clear();
            q.Index = 0;
            q.Playing = false;
            q.Finished = false;
            q.StartedAt = null;
            Note(tab, q, "cleared");
            Save();
            return Copy(q);
        }
    }

    /// <summary>For the tab list: null when the tab has no queue.</summary>
    public static object? Summary(int tab)
    {
        lock (Lock)
        {
            if (!File().Tabs.TryGetValue(tab.ToString(), out var q) || q.Items.Count == 0)
                return null;
            return new
            {
                count = q.Items.Count,
                position = Math.Max(0, q.Index) + 1,
                playing = q.Playing,
                finished = q.Finished,
                current = Name(Current(q)?.Path),
            };
        }
    }
}
