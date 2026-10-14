using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>
/// The script queues (Skua.Linux/ScriptQueue.cs) and the Manager's script
/// library (favourites, recent), on the host API:
///
///   GET    /tabs/N/queue                     the queue: items, index, playing, loop, note
///   PUT    /tabs/N/queue                     {items: [{path, minutes, options}], loop}: replace the entries
///   POST   /tabs/N/queue/add?path=&amp;minutes=   add an entry at the end; body {options: {storage, values}}
///                                            optional: its own options (ScriptQueue.ItemOptions)
///   POST   /tabs/N/queue/play[?index=K]      play from the current entry, or from entry K (0-based)
///   POST   /tabs/N/queue/pause               stop moving on (the running script goes on)
///   POST   /tabs/N/queue/skip                stop the running entry and go on
///   POST   /tabs/N/queue/clear
///   GET    /tabs/N/queue/options?index=K   entry K's script options, as GET /script/options
///                                            gives them, for the tab's account (ScriptOptionsProbe)
///   GET    /scripts/library                  favourite and recent scripts
///   POST   /scripts/favorite?path=&amp;on=1|0
/// </summary>
public partial class TabHostWindow
{
    // From each status poll: does what the tab's queue asks, off the poll
    // (a start waits for the script to compile).
    private void QueueTick(SkuaTab tab, bool loggedIn, string? loaded, bool running)
    {
        // Nothing starts while the startup sync waits for its answer or downloads.
        if (!ScriptSync.IsQuiet)
            return;
        int n = tab.Number + 1;
        var (step, item) = ScriptQueue.Tick(n, loggedIn, loaded, running);
        if (step == ScriptQueue.Step.Start && item is not null)
            _ = Task.Run(async () =>
            {
                string path = item.Path;
                // Its own options first, for the account the tab has logged in.
                string? error = item.Options is { } options
                    ? await TabPost(tab, "/script/options/apply", JsonSerializer.Serialize(options, ApiJson))
                    : null;
                ScriptQueue.Started(n, path, error is null
                    ? await TabPost(tab, $"/script/start?path={Uri.EscapeDataString(path)}" + (item.Options is null ? "" : "&queue=1"))
                    : $"its options were not written: {error}");
            });
        else if (step == ScriptQueue.Step.Stop)
            _ = Task.Run(() => TabPost(tab, "/script/stop"));
    }

    /// <summary>POSTs to the tab's API; null when it did it, else the error
    /// (the tab answers a refusal with 200 and {"error"}).</summary>
    private async Task<string?> TabPost(SkuaTab tab, string pathAndQuery, string? json = null)
    {
        try
        {
            using var reply = await _pass.PostAsync(tab.Api + pathAndQuery,
                json is null ? new ByteArrayContent([]) : new StringContent(json, Encoding.UTF8, "application/json"));
            string text = await reply.Content.ReadAsStringAsync();
            string? error = null;
            try
            {
                if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } root
                    && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    error = e.GetString();
            }
            catch (JsonException) { }
            if (reply.IsSuccessStatusCode)
                return error;
            return error ?? $"{(int)reply.StatusCode} {text}";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    private async Task<bool> TabRunning(SkuaTab tab)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _quick.GetStringAsync($"{tab.Api}/status"));
            return doc.RootElement.TryGetProperty("script", out var s) && s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    // The account logged in to the tab (its options are the ones read); null if none.
    private async Task<string?> TabAccount(SkuaTab tab)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _quick.GetStringAsync($"{tab.Api}/status"));
            return doc.RootElement.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.Object
                && g.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } name
                ? name : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    // A script's options from a Skua of its own (Skua.Linux/ScriptOptionsProbe.cs),
    // so the tab's running script is not touched. One at a time: each compiles
    // the script and its includes (a few seconds, a few hundred MB while it runs).
    private static async Task<object> ProbeOptions(string script, string? account)
    {
        if (!File.Exists(script))
            return new ApiError(404, $"{Path.GetFileName(script)} is not in the Scripts folder");
        await ProbeGate.WaitAsync();
        string output = Path.Combine(Path.GetTempPath(), $"skua-options-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(psi.FileName) == "dotnet")
                psi.ArgumentList.Add(typeof(TabHostWindow).Assembly.Location);
            psi.ArgumentList.Add("--script-options");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(output);
            if (account is not null)
                psi.ArgumentList.Add(account);
            psi.Environment.Remove("SKUA_INSTANCE");
            using var process = Process.Start(psi)!;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) { errors.Enqueue(line); if (errors.Count > 5) errors.TryDequeue(out string? _); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                return new ApiError(504, $"reading the options of {Path.GetFileName(script)} took over 3 minutes");
            }
            if (!File.Exists(output))
                return new ApiError(500, $"reading the options of {Path.GetFileName(script)} failed (exit {process.ExitCode}): {string.Join(" | ", errors)}");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            return doc.RootElement.Clone();
        }
        finally
        {
            try { File.Delete(output); } catch { }
            ProbeGate.Release();
        }
    }

    private static object QueueView(int tab, ScriptQueue.Queue q) => new
    {
        tab,
        items = q.Items.Select((i, k) => new { i.Path, name = ScriptQueue.Name(i.Path), i.Minutes, i.Options, current = k == q.Index }).ToList(),
        q.Index,
        q.Playing,
        q.Finished,
        q.Loop,
        q.StartedAt,
        runningMinutes = q.StartedAt is { } at ? (int)(DateTime.UtcNow - at).TotalMinutes : (int?)null,
        q.Note,
        q.NoteAt,
    };

    private sealed record QueueInput(List<ScriptQueue.Item>? Items, bool? Loop);
    private sealed record AddInput(ScriptQueue.ItemOptions? Options);

    private async Task<object?> QueueApi(string method, string number, string? action, HttpListenerRequest request, Func<string, string?> query)
    {
        var tab = await OnUi(() => TabByNumber(number));
        if (tab is null)
            return new ApiError(404, $"no tab {number}");
        int n = tab.Number + 1;
        switch (method, action)
        {
            case ("GET", null):
                return QueueView(n, ScriptQueue.Get(n));
            case ("PUT" or "POST", null):
            {
                QueueInput? input;
                try { input = JsonSerializer.Deserialize<QueueInput>(Encoding.UTF8.GetString(await ReadBody(request)), ApiJson); }
                catch (JsonException e) { return new ApiError(400, $"bad JSON: {e.Message}"); }
                if (input?.Items is null)
                    return new ApiError(400, "items is required");
                return QueueView(n, ScriptQueue.Put(n, input.Items, input.Loop ?? ScriptQueue.Get(n).Loop));
            }
            case ("POST", "add"):
            {
                if (query("path") is not { } path)
                    return new ApiError(400, "path is required");
                // An optional body {"options": {"storage", "values"}}: the entry's own options.
                ScriptQueue.ItemOptions? options = null;
                string body = Encoding.UTF8.GetString(await ReadBody(request));
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try { options = JsonSerializer.Deserialize<AddInput>(body, ApiJson)?.Options; }
                    catch (JsonException e) { return new ApiError(400, $"bad JSON: {e.Message}"); }
                }
                return QueueView(n, ScriptQueue.Add(n, path, int.TryParse(query("minutes"), out int m) ? m : 0, options));
            }
            case ("POST", "play"):
            {
                int? index = int.TryParse(query("index"), out int k) ? k : null;
                var (q, stopFirst) = ScriptQueue.Play(n, index, index is not null && await TabRunning(tab));
                if (stopFirst)
                    await TabPost(tab, "/script/stop");
                return QueueView(n, q);
            }
            case ("POST", "pause"):
                return QueueView(n, ScriptQueue.Pause(n));
            case ("POST", "skip"):
            {
                var (q, stop) = ScriptQueue.Skip(n, await TabRunning(tab));
                if (stop)
                    await TabPost(tab, "/script/stop");
                return QueueView(n, q);
            }
            case ("POST", "clear"):
                return QueueView(n, ScriptQueue.Clear(n));
            case ("GET", "options"):
            {
                var items = ScriptQueue.Get(n).Items;
                if (!int.TryParse(query("index"), out int k) || k < 0 || k >= items.Count)
                    return new ApiError(400, $"index is required (0 to {items.Count - 1})");
                return await ProbeOptions(items[k].Path, await TabAccount(tab) ?? AccountStore.UserFor(n));
            }
            default:
                return null;
        }
    }

    private static object Favorite(Func<string, string?> query)
    {
        if (query("path") is not { } path)
            return new ApiError(400, "path is required");
        ScriptLibrary.SetFavorite(path, query("on") is not ("0" or "false" or "no" or "off"));
        return ScriptLibrary.Snapshot();
    }
}
