using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;

namespace Skua.Linux;

/// <summary>
/// More script repositories, each synced into its own folder under
/// Skua/Scripts, next to the main one (ScriptSync). Scripts in them include
/// each other by that folder, e.g. UltrasLW's
/// <c>//cs_include Scripts/UltrasLW/CoreLoneWolf.cs</c>.
///
///   SKUA_SCRIPTS_EXTRA      one or more, separated by ";" or new lines
///   SKUA_SCRIPTS_EXTRA_N    one each (N = 1, 2, ...)
///
/// Each is <c>[folder=]url[#branch]</c>: a GitHub or Gitea repository; the
/// folder defaults to the repository's name and the branch to its default
/// one. For example
/// <c>UltrasLW=https://github.com/l0newolf12/UltrasLW</c>.
///
/// Unlike the main repository these have no script index: the repository's
/// file list (with git's hash of each file) is compared with the files on
/// disk, and the .cs files that are missing or differ are downloaded. Files
/// that are no longer in the repository are left alone.
/// </summary>
public static class ExtraScripts
{
    public sealed record Source(string Folder, string Url, string? Branch, bool IsGitHub, string Site, string Owner, string Repo)
    {
        public string Name => $"{Owner}/{Repo}";
        public string Dir => Path.Combine(ClientFileSources.SkuaScriptsDIR, Folder);
    }

    public sealed record Result(Source Source, int Files, int Downloaded, int Failed, string? Error);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vibeskua-script-sync");
        return client;
    }

    /// <summary>The configured repositories; bad entries are reported and skipped.</summary>
    public static List<Source> Sources { get; } = Parse();

    private static List<Source> Parse()
    {
        var entries = new List<string>();
        if (SkuaRuntime.EnvRaw("SKUA_SCRIPTS_EXTRA") is { } list)
            entries.AddRange(list.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        for (int n = 1; n <= 50; n++)
            if (SkuaRuntime.EnvRaw($"SKUA_SCRIPTS_EXTRA_{n}") is { } one)
                entries.Add(one);

        var sources = new List<Source>();
        foreach (string entry in entries)
        {
            if (TryParse(entry, out var source, out string? why))
            {
                if (sources.Any(s => s.Folder.Equals(source!.Folder, StringComparison.OrdinalIgnoreCase)))
                    Console.Error.WriteLine($"[scripts] SKUA_SCRIPTS_EXTRA: folder {source!.Folder} is used twice; skipping {entry}");
                else
                    sources.Add(source!);
            }
            else
            {
                Console.Error.WriteLine($"[scripts] SKUA_SCRIPTS_EXTRA: ignoring \"{entry}\": {why}");
            }
        }
        return sources;
    }

    private static bool TryParse(string entry, out Source? source, out string? why)
    {
        source = null;
        why = null;
        string? folder = null, branch = null;
        string url = entry.Trim().Trim('"', '\'').Trim();
        // "folder=url": the first "=" before "://".
        int eq = url.IndexOf('=');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (eq > 0 && (scheme < 0 || eq < scheme))
        {
            folder = url[..eq].Trim();
            url = url[(eq + 1)..].Trim();
        }
        int hash = url.IndexOf('#');
        if (hash >= 0)
        {
            branch = url[(hash + 1)..].Trim();
            url = url[..hash].Trim();
            if (branch.Length == 0)
                branch = null;
        }
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            why = "not a repository URL (https://host/owner/repo)";
            return false;
        }
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            why = "the URL needs the owner and the repository (https://host/owner/repo)";
            return false;
        }
        string owner = parts[^2];
        string repo = parts[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[^1][..^4] : parts[^1];
        folder ??= repo;
        // One plain folder name: it becomes Scripts/<folder>.
        if (!Regex.IsMatch(folder, @"^[A-Za-z0-9._ -]+$") || folder is "." or "..")
        {
            why = $"\"{folder}\" is not a usable folder name (letters, digits, . _ - and spaces)";
            return false;
        }
        bool gitHub = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        // Gitea may live under a sub-path (https://host/gitea/owner/repo).
        string site = (uri.GetLeftPart(UriPartial.Authority) + "/" + string.Join('/', parts[..^2])).TrimEnd('/');
        source = new Source(folder, url, branch, gitHub, site, owner, repo);
        return true;
    }

    private static string Api(Source s) => s.IsGitHub
        ? $"https://api.github.com/repos/{s.Owner}/{s.Repo}"
        : $"{s.Site}/api/v1/repos/{s.Owner}/{s.Repo}";

    private static string Raw(Source s, string branch, string path)
    {
        string p = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        string b = Uri.EscapeDataString(branch);
        return s.IsGitHub
            ? $"https://raw.githubusercontent.com/{s.Owner}/{s.Repo}/refs/heads/{b}/{p}"
            : $"{s.Site}/{s.Owner}/{s.Repo}/raw/branch/{b}/{p}";
    }

    private static async Task<JsonElement> GetJson(string url, CancellationToken ct)
    {
        using var reply = await Http.GetAsync(url, ct);
        if (!reply.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)reply.StatusCode} {reply.ReasonPhrase} from {url}");
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    /// <summary>The repository's .cs files (path, git blob hash) on its branch, and the branch.</summary>
    private static async Task<(string Branch, List<(string Path, string Sha)> Files)> ListAsync(Source s, CancellationToken ct)
    {
        string branch = s.Branch ?? (await GetJson(Api(s), ct)).GetProperty("default_branch").GetString()!;
        var files = new List<(string, string)>();
        string tree = $"{Api(s)}/git/trees/{Uri.EscapeDataString(branch)}";
        void Add(JsonElement root)
        {
            foreach (var e in root.GetProperty("tree").EnumerateArray())
                if (e.GetProperty("type").GetString() == "blob"
                    && e.GetProperty("path").GetString() is { } p
                    && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    files.Add((p, e.GetProperty("sha").GetString()!));
        }
        if (s.IsGitHub)
        {
            var root = await GetJson($"{tree}?recursive=1", ct);
            if (root.TryGetProperty("truncated", out var t) && t.GetBoolean())
                Console.Error.WriteLine($"[scripts] {s.Name}: GitHub cut the file list short; some scripts may be missing");
            Add(root);
        }
        else
        {
            // Gitea pages the recursive tree.
            for (int page = 1; page <= 100; page++)
            {
                var root = await GetJson($"{tree}?recursive=true&per_page=1000&page={page}", ct);
                Add(root);
                if (!root.TryGetProperty("truncated", out var t) || !t.GetBoolean())
                    break;
            }
        }
        return (branch, files);
    }

    // git's hash of a file's contents ("blob <length>\0<bytes>"), as the tree lists it.
    private static string GitBlobSha(string file)
    {
        byte[] content = File.ReadAllBytes(file);
        byte[] header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = SHA1.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Brings one repository's folder up to date: downloads the .cs files that
    /// are missing or differ (local edits included), 8 at a time.
    /// </summary>
    public static async Task<Result> SyncAsync(Source s, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var (branch, files) = await ListAsync(s, ct);
            var stale = files.Where(f =>
            {
                string local = Path.Combine(s.Dir, f.Path);
                return !File.Exists(local) || GitBlobSha(local) != f.Sha;
            }).ToList();
            int done = 0, failed = 0;
            progress?.Invoke(0, stale.Count);
            using var slots = new SemaphoreSlim(8);
            await Task.WhenAll(stale.Select(async f =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    byte[] bytes = await Http.GetByteArrayAsync(Raw(s, branch, f.Path), ct);
                    string local = Path.Combine(s.Dir, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    string temp = local + ".download";
                    await File.WriteAllBytesAsync(temp, bytes, ct);
                    File.Move(temp, local, overwrite: true);
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failed);
                    Console.Error.WriteLine($"[scripts] {s.Name}: {f.Path}: {e.Message}");
                }
                finally
                {
                    progress?.Invoke(Interlocked.Increment(ref done), stale.Count);
                    slots.Release();
                }
            }));
            return new Result(s, files.Count, stale.Count - failed, failed, null);
        }
        catch (Exception e)
        {
            return new Result(s, 0, 0, 0, e.Message);
        }
    }

    /// <summary>
    /// The extra repositories' scripts on disk, for searching beside the main
    /// index: name, description and tags from each script's header comment.
    /// </summary>
    public static IEnumerable<ScriptInfo> Local()
    {
        foreach (var s in Sources)
        {
            if (!Directory.Exists(s.Dir))
                continue;
            foreach (string file in Directory.EnumerateFiles(s.Dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(ClientFileSources.SkuaScriptsDIR, file).Replace('\\', '/');
                var (name, description, tags) = Header(file);
                int size = (int)new FileInfo(file).Length;
                yield return new ScriptInfo
                {
                    Name = name ?? Path.GetFileNameWithoutExtension(file),
                    Description = description ?? $"{s.Folder} ({s.Name})",
                    // As the Search Scripts window tags scripts outside the index.
                    Tags = [.. tags, "Local"],
                    FilePath = relative,
                    FileName = Path.GetFileName(file),
                    Size = size,   // what is on disk is this repository's latest
                };
            }
        }
    }

    // "/* name: ... description: ... tags: a, b */" at the top, as auqw/Scripts writes it.
    private static (string? Name, string? Description, string[] Tags) Header(string file)
    {
        try
        {
            string head = string.Join('\n', File.ReadLines(file).Take(15));
            string? Field(string key) =>
                Regex.Match(head, $@"^\s*{key}\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            string[] tags = Field("tags")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            return (Field("name"), Field("description"), tags);
        }
        catch
        {
            return (null, null, []);
        }
    }
}
