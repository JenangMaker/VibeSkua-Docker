using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Linux;

/// <summary>
/// The Manager's favourite scripts, and the scripts tabs ran lately (noted by
/// the tab host when a tab starts running one), in scripts.json next to
/// accounts.json. Shared by everyone using the Manager.
/// </summary>
public static class ScriptLibrary
{
    public sealed class Recent
    {
        public string Path { get; set; } = "";
        public int Tab { get; set; }
        public DateTime At { get; set; }
    }

    private sealed class FileModel
    {
        public List<string> Favorites { get; set; } = new();
        public List<Recent> Recent { get; set; } = new();
    }

    private const int MaxRecent = 20;

    public static string FilePath { get; } = SkuaRuntime.EnvRaw("VIBESKUA_SCRIPTS_FILE")
        ?? Path.Combine(Path.GetDirectoryName(AccountStore.FilePath)!, "scripts.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly object Lock = new();
    private static FileModel? _file;
    private static readonly Dictionary<int, string> LastRunning = new();

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

    /// <summary>Each poll: a tab that started running a script adds it to the recent ones.</summary>
    public static void NoteRunning(int tab, string? loaded, bool running)
    {
        lock (Lock)
        {
            if (!running || string.IsNullOrEmpty(loaded))
            {
                LastRunning.Remove(tab);
                return;
            }
            if (LastRunning.TryGetValue(tab, out var last) && last == loaded)
                return;
            LastRunning[tab] = loaded;
            var recent = File().Recent;
            recent.RemoveAll(r => r.Path == loaded);
            recent.Insert(0, new Recent { Path = loaded, Tab = tab, At = DateTime.UtcNow });
            if (recent.Count > MaxRecent)
                recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
            Save();
        }
    }

    public static void SetFavorite(string path, bool on)
    {
        path = ScriptQueue.Full(path);
        lock (Lock)
        {
            var favorites = File().Favorites;
            favorites.RemoveAll(p => p == path);
            if (on)
                favorites.Add(path);
            Save();
        }
    }

    public static object Snapshot()
    {
        lock (Lock)
        {
            var file = File();
            return new
            {
                favorites = file.Favorites
                    .Select(p => new { path = p, name = ScriptQueue.Name(p), exists = System.IO.File.Exists(p) }).ToList(),
                recent = file.Recent
                    .Select(r => new { path = r.Path, name = ScriptQueue.Name(r.Path), r.Tab, r.At, favorite = file.Favorites.Contains(r.Path) }).ToList(),
            };
        }
    }
}
