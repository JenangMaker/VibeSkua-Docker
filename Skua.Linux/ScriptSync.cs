using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;
using Skua.Core.Services;

namespace Skua.Linux;

/// <summary>
/// Skua's script repository (auqw/Scripts, branch Skua, or SKUA_SCRIPTS_REPO /
/// SKUA_SCRIPTS_BRANCH; see Skua.Core ScriptsSource), as the WPF app uses
/// it at startup: fetch the index, download missing and outdated scripts into
/// Skua/Scripts, refresh the advanced skill sets, quest data and junk list.
/// Honours the same settings (CheckBotScriptsUpdates, AutoUpdateBotScripts,
/// CheckAdvanceSkillSetsUpdates, AutoUpdateAdvanceSkillSetsUpdates,
/// CheckJunkItemsUpdates, AutoUpdateJunkItems).
///
/// Instead of downloading silently it asks (Update all / Only missing / Skip)
/// when AutoUpdateBotScripts is off, when the Scripts folder is mounted from
/// the host (updating overwrites local changes to outdated scripts), or when
/// SKUA_SCRIPT_SYNC=ask; SKUA_SCRIPT_SYNC=off skips it. Like WPF, it asks
/// before updating the junk list when AutoUpdateJunkItems is off. Without a
/// UI the dialogs answer Skip / No.
/// </summary>
public sealed class ScriptSync(IServiceProvider services)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _firstSync = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IGetScriptsService Repo => services.GetRequiredService<IGetScriptsService>();
    private ISettingsService Settings => services.GetRequiredService<ISettingsService>();
    private IDialogService Dialogs => services.GetRequiredService<IDialogService>();

    // Refresh the index, then let the user choose what to download.
    private async Task AskAndUpdateAsync(bool mounted)
    {
        await UpdateScriptsAsync(Scope.None);
        var repo = Repo;
        int missing = repo.Missing, outdated = repo.Outdated;
        if (missing == 0 && outdated == 0)
            return;

        string message =
            $"{ScriptsSource.Name} has {missing} script(s) you do not have and {outdated} newer than yours (of {repo.Total}).\r\n\r\n" +
            (mounted
                ? $"Your Scripts folder ({ClientFileSources.SkuaScriptsDIR}) is mounted from the host. \"Update all\" replaces your outdated scripts with the repository's versions, including any local changes to them; \"Only missing\" adds new scripts and leaves yours alone.\r\n\r\n"
                : "") +
            "Download them now?";
        Step($"{missing + outdated} script updates available: waiting for your choice");
        DialogResult choice = Dialogs.ShowMessageBox(message, "Script Updates", "Update all", "Only missing", "Skip");
        Scope scope = choice.Value switch { 0 => Scope.All, 1 => Scope.Missing, _ => Scope.None };
        Console.WriteLine($"[scripts] {missing} missing, {outdated} outdated; chose: {(choice.Value < 0 ? "Skip" : choice.Text)}");
        if (scope != Scope.None)
            await UpdateScriptsAsync(scope, refresh: false);
        else
            Finish($"Script updates skipped ({missing} missing, {outdated} outdated)");
    }

    public bool Syncing { get; private set; }

    /// <summary>
    /// Present (holding the syncing process's id) while a sync fetches the
    /// script list or writes script files. All tabs share the Scripts folder
    /// but only one syncs, so the others read this to know. It is not held
    /// while a question waits for an answer: nothing changes meanwhile.
    /// </summary>
    public static string BusyMarker { get; } = Path.Combine(ClientFileSources.SkuaDIR, ".scripts-syncing");

    private int _busy;

    private void Busy(bool on)
    {
        int now = on ? Interlocked.Increment(ref _busy) : Interlocked.Decrement(ref _busy);
        try
        {
            if (on && now == 1)
                File.WriteAllText(BusyMarker, ProcessStamp(Environment.ProcessId));
            else if (!on && now == 0)
                File.Delete(BusyMarker);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[scripts] sync marker: {e.Message}");
        }
    }

    // "pid start-time": a container restart reuses the same small pids, so a
    // marker left behind must not match whichever process has that pid now.
    private static string ProcessStamp(int pid)
    {
        try
        {
            // Field 22 of /proc/<pid>/stat, after the parenthesised name.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return $"{pid} {rest[19]}";
        }
        catch
        {
            return pid.ToString();
        }
    }

    /// <summary>
    /// Waits (up to <paramref name="max"/>) while some tab's sync is changing
    /// the Scripts folder, so a script is not compiled from half-updated
    /// files. A marker left by a process that is gone does not count. True if
    /// the folder is quiet, false on timeout.
    /// </summary>
    public static async Task<bool> WaitUntilQuietAsync(TimeSpan max, Action? waiting = null)
    {
        DateTime giveUp = DateTime.UtcNow + max;
        bool told = false;
        while (DateTime.UtcNow < giveUp)
        {
            string? owner = null;
            try { owner = File.ReadAllText(BusyMarker).Trim(); }
            catch { }
            if (owner is null || !int.TryParse(owner.Split(' ')[0], out int pid) || ProcessStamp(pid) != owner)
                return true;
            if (!told)
            {
                waiting?.Invoke();
                told = true;
            }
            await Task.Delay(2000);
        }
        return false;
    }
    public string LastResult { get; private set; } = "not run";

    // ---- progress, for the status bar (MainWindow) ----
    private int _done;

    /// <summary>What the sync is doing right now; null when idle.</summary>
    public string? Activity { get; private set; }

    /// <summary>Items done / to do in the current step; <see cref="Total"/> 0 when it has no count.</summary>
    public int Done => Volatile.Read(ref _done);
    public int Total { get; private set; }

    /// <summary>One line on how the last sync went, and when it finished.</summary>
    public string Summary { get; private set; } = "";
    public DateTime FinishedAt { get; private set; }

    private void Step(string? activity, int total = 0)
    {
        Interlocked.Exchange(ref _done, 0);
        Total = total;
        Activity = activity;
    }

    private void Finish(string summary)
    {
        Summary = summary;
        FinishedAt = DateTime.UtcNow;
        Step(null);
    }

    /// <summary>Completes once the startup sync has finished, whatever its outcome.</summary>
    public Task FirstSync => _firstSync.Task;

    public enum Scope { None, Missing, All }

    /// <summary>Whether Skua's Scripts folder is a mount (a host folder given to the container).</summary>
    public static bool ScriptsFolderMounted
    {
        get
        {
            try
            {
                string dir = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR).TrimEnd('/');
                // mountinfo fields: id parent major:minor root mount-point ...;
                // a space in a path is written as the octal escape "\040".
                return File.ReadLines("/proc/self/mountinfo")
                    .Select(l => l.Split(' '))
                    .Any(f => f.Length > 4 && f[4].Replace("\\040", " ").TrimEnd('/') == dir);
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task StartupAsync()
    {
        try
        {
            string mode = (SkuaRuntime.EnvRaw("SKUA_SCRIPT_SYNC") ?? "auto").ToLowerInvariant();
            bool off = mode is "off" or "0" or "false" or "no";
            if (!Settings.Get<bool>("CheckBotScriptsUpdates"))
                LastResult = "script updates off (CheckBotScriptsUpdates)";
            else if (off)
                LastResult = "script updates off (SKUA_SCRIPT_SYNC)";
            else
            {
                bool mounted = ScriptsFolderMounted;
                if (mode == "ask" || mounted || !Settings.Get<bool>("AutoUpdateBotScripts"))
                    await AskAndUpdateAsync(mounted);
                else
                    await UpdateScriptsAsync(Scope.All);
            }

            string summary = Summary;
            // The extra repositories were asked for by name (SKUA_SCRIPTS_EXTRA),
            // each into a folder of its own: kept up to date without asking.
            if (!off && await UpdateExtrasAsync() is { } extras)
                summary = summary.Length > 0 ? $"{summary}; {extras}" : extras;
            Step("Checking the advanced skill sets");
            if (Settings.Get<bool>("CheckAdvanceSkillSetsUpdates")
                && Settings.Get<bool>("AutoUpdateAdvanceSkillSetsUpdates")
                && await Repo.CheckAdvanceSkillSetsUpdates() > 0
                && await Repo.UpdateSkillSetsFile())
            {
                services.GetRequiredService<IAdvancedSkillContainer>().SyncSkills();
                Console.WriteLine("[scripts] advanced skill sets updated");
            }

            Step("Updating quest data");
            await Repo.UpdateQuestDataFile();

            Step("Checking the junk item list");

            if (Settings.Get<bool>("CheckJunkItemsUpdates")
                && await Repo.CheckJunkItemsUpdates() > 0
                && (Settings.Get<bool>("AutoUpdateJunkItems")
                    || Dialogs.ShowMessageBox("Would you like to update your Junk Items list?", "Junk Items Update", true) == true)
                && await Repo.UpdateJunkItemsFile())
            {
                services.GetRequiredService<IJunkService>().Load();
                Console.WriteLine("[scripts] junk item list updated");
            }
            // The scripts' own result is the news; the rest is housekeeping.
            Finish(summary.Length > 0 ? summary : LastResult);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[scripts] startup sync: {e.Message}");
            Finish($"Script sync failed: {e.Message}");
        }
        finally
        {
            Step(null);
            _firstSync.TrySetResult();
        }
    }

    /// <summary>
    /// Refreshes the index (unless <paramref name="refresh"/> is false) and
    /// downloads the scripts in <paramref name="scope"/>: none, only missing
    /// ones, or missing and outdated ones.
    /// </summary>
    public async Task<object> UpdateScriptsAsync(Scope scope = Scope.All, bool refresh = true)
    {
        await _gate.WaitAsync();
        Syncing = true;
        Busy(true);
        try
        {
            if (refresh || Repo.Total == 0)
            {
                Step($"Fetching the script list from {ScriptsSource.Name}");
                Console.WriteLine("[scripts] fetching the script index");
                await Repo.RefreshScriptsAsync(null, default);
            }
            var repo = Repo;
            int missing = repo.Missing, outdated = repo.Outdated;
            int fetched = 0;
            List<(ScriptInfo Script, Exception Error)> failures = new();
            if (scope == Scope.All && (missing > 0 || outdated > 0))
            {
                Console.WriteLine($"[scripts] downloading {missing} missing, {outdated} outdated of {repo.Total}");
                (fetched, failures) = await DownloadAsync(s => !s.Downloaded || s.Outdated);
            }
            else if (scope == Scope.Missing && missing > 0)
            {
                Console.WriteLine($"[scripts] downloading {missing} missing of {repo.Total} (leaving {outdated} outdated as they are)");
                (fetched, failures) = await DownloadAsync(s => !s.Downloaded);
            }
            // (A failed index fetch is reported by GetScriptsService itself.)
            if (failures.Count > 0)
                ReportFailures(failures, fetched);
            LastResult = $"{repo.Total} scripts; {fetched} downloaded" + (failures.Count > 0 ? $", {failures.Count} failed" : "") + $" at {DateTime.UtcNow:u}";
            if (repo.Total == 0)
                Finish($"Could not get the script list from {ScriptsSource.Name}");
            else if (failures.Count > 0)
                Finish($"{fetched} scripts downloaded, {failures.Count} failed");
            else if (fetched > 0)
                Finish($"{fetched} scripts downloaded ({repo.Total} in all)");
            else if (scope == Scope.None && (missing > 0 || outdated > 0))
                Step(null);   // AskAndUpdateAsync takes it from here
            else
                Finish($"Scripts up to date ({repo.Total})");
            Console.WriteLine($"[scripts] {LastResult}");
            return new { total = repo.Total, missing, outdated, downloaded = fetched, failed = failures.Count };
        }
        catch (Exception e)
        {
            LastResult = $"failed: {e.Message}";
            Console.Error.WriteLine($"[scripts] {LastResult}");
            Finish($"Script sync failed: {e.Message}");
            ReportFailure($"Syncing scripts from {ScriptsSource.Name} failed:\r\n{e.Message}", e);
            return new { error = e.Message };
        }
        finally
        {
            Busy(false);
            Syncing = false;
            _gate.Release();
        }
    }

    /// <summary>The main repository, then the extra ones (POST /scripts/update).</summary>
    public async Task<object> UpdateAllAsync()
    {
        object main = await UpdateScriptsAsync();
        string? extras = await UpdateExtrasAsync();
        return new { main, extras };
    }

    /// <summary>
    /// Syncs the extra repositories (SKUA_SCRIPTS_EXTRA, ExtraScripts.cs) one
    /// after another; returns one line on how it went, null if there are none.
    /// </summary>
    public async Task<string?> UpdateExtrasAsync()
    {
        var sources = ExtraScripts.Sources;
        if (sources.Count == 0)
            return null;
        await _gate.WaitAsync();
        Syncing = true;
        Busy(true);
        try
        {
            var parts = new List<string>();
            foreach (var source in sources)
            {
                Step($"Fetching the script list from {source.Name}");
                var r = await ExtraScripts.SyncAsync(source, (done, total) =>
                {
                    if (done == 0)
                        Step($"Downloading scripts from {source.Name}", total);
                    else
                        Interlocked.Exchange(ref _done, done);
                });
                string line = r.Error is not null
                    ? $"{source.Folder}: failed ({r.Error})"
                    : r.Failed > 0
                        ? $"{source.Folder}: {r.Downloaded} downloaded, {r.Failed} failed"
                        : r.Downloaded > 0
                            ? $"{source.Folder}: {r.Downloaded} downloaded ({r.Files} in all)"
                            : $"{source.Folder}: up to date ({r.Files})";
                Console.WriteLine($"[scripts] {source.Name} -> Scripts/{source.Folder}: {line}");
                if (r.Error is not null || r.Failed > 0)
                    ReportFailure($"Syncing scripts from {source.Name} into Scripts/{source.Folder}:\r\n{line}" +
                        (r.Failed > 0 ? "\r\n\r\nThe failed files are listed in the container log." : ""), null);
                parts.Add(line);
            }
            string summary = string.Join("; ", parts);
            LastResult = $"{LastResult}; extra: {summary}";
            Step(null);
            return summary;
        }
        finally
        {
            Busy(false);
            Syncing = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Skua Manager's "Reset Scripts": deletes everything in the Scripts
    /// folder (local edits and scripts of your own included) and downloads
    /// the repository's scripts and quest data afresh. The folder itself
    /// stays, as it may be a mount point. Refused while a script runs.
    /// </summary>
    public async Task<object> ResetScriptsAsync()
    {
        if (services.GetRequiredService<IScriptManager>().ScriptRunning)
            return new { error = "a script is running; stop it before resetting the scripts" };
        string dir = ClientFileSources.SkuaScriptsDIR;
        // Held from the first delete to the last download.
        Busy(true);
        try
        {
            var (deleted, failed) = await DeleteScriptsAsync(dir);
            if (failed > 0)
                ReportFailure($"Reset Scripts could not delete {failed} item(s) in {dir} (see the log); downloading the rest again anyway.", null);

            object result = await UpdateScriptsAsync(Scope.All);
            string mainSummary = Summary;
            // The extra repositories' folders went with the rest.
            string? extras = await UpdateExtrasAsync();
            Step("Updating quest data");
            await Repo.UpdateQuestDataFile();
            Finish(extras is null ? mainSummary : $"{mainSummary}; {extras}");
            Console.WriteLine($"[scripts] reset: {deleted} item(s) deleted, then: {LastResult}");
            return new { reset = true, deleted, notDeleted = failed, sync = result };
        }
        finally
        {
            Busy(false);
        }
    }

    // Everything in the Scripts folder, but not the folder (a mount point, maybe).
    private async Task<(int Deleted, int Failed)> DeleteScriptsAsync(string dir)
    {
        int deleted = 0, failed = 0;
        await _gate.WaitAsync();
        try
        {
            Step("Deleting the scripts in " + dir);
            Console.WriteLine($"[scripts] reset: deleting the contents of {dir}");
            Directory.CreateDirectory(dir);
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, true);
                    else
                        File.Delete(entry);
                    deleted++;
                }
                catch (Exception e)
                {
                    failed++;
                    Console.Error.WriteLine($"[scripts] reset: could not delete {entry}: {e.Message}");
                }
            }
            try { File.Delete(ClientFileSources.SkuaScriptsCommitFile); } catch { }
        }
        finally
        {
            _gate.Release();
        }
        return (deleted, failed);
    }

    // Download the matching scripts 15 at a time; one failure does not stop the rest.
    private async Task<(int Fetched, List<(ScriptInfo, Exception)> Failures)> DownloadAsync(Func<ScriptInfo, bool> pred)
    {
        var repo = Repo;
        List<ScriptInfo> targets = repo.Scripts.ToList().Where(pred).ToList();
        List<(ScriptInfo, Exception)> failures = new();
        Step("Downloading scripts", targets.Count);
        using SemaphoreSlim slots = new(15);
        await Task.WhenAll(targets.Select(async s =>
        {
            await slots.WaitAsync();
            try { await repo.DownloadScriptAsync(s); }
            catch (Exception e) { lock (failures) failures.Add((s, e)); }
            finally { Interlocked.Increment(ref _done); slots.Release(); }
        }));
        return (targets.Count - failures.Count, failures);
    }

    private void ReportFailures(List<(ScriptInfo Script, Exception Error)> failures, int fetched)
    {
        string dir = ClientFileSources.SkuaScriptsDIR;
        foreach (var (script, error) in failures.Take(5))
            Console.Error.WriteLine($"[scripts] {script.FilePath}: {error.Message}");

        string message = $"{failures.Count} script(s) could not be downloaded from {ScriptsSource.Name} ({fetched} downloaded).\r\n\r\n";
        if (failures.Any(f => f.Error is UnauthorizedAccessException or IOException { HResult: 13 }))
        {
            message += $"Skua cannot write to its Scripts folder ({dir}). " +
                $"It runs as user {Geteuid()}:{Getegid()}; " +
                (ScriptsFolderMounted
                    ? "the folder is mounted from the host, so set the container's PUID/PGID to the folder's owner (stat -c '%u:%g' <host folder>) or make the folder writable for that user."
                    : "make the folder writable for that user.") +
                "\r\n\r\n";
        }
        message += "Failed:\r\n" + string.Join("\r\n", failures
            .Take(3)
            .Select(f => $"  {f.Script.FilePath}: {f.Error.Message}"));
        if (failures.Count > 3)
            message += $"\r\n  ... and {failures.Count - 3} more (see the container log)";
        ReportFailure(message, null);
    }

    // Show the problem without holding up the sync (or an API request) until it is dismissed.
    private void ReportFailure(string message, Exception? e)
    {
        _ = Task.Run(() =>
        {
            try { Dialogs.ShowMessageBox(message, "Script Sync Failed"); }
            catch { }
        });
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GeteuidNative();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetegidNative();

    private static string Geteuid() { try { return GeteuidNative().ToString(); } catch { return "?"; } }
    private static string Getegid() { try { return GetegidNative().ToString(); } catch { return "?"; } }

    /// <summary>
    /// <see cref="Search"/>, after getting the repository index if this Skua
    /// has none. Only the tab that syncs (the first) gets it at start; the
    /// others share its Scripts folder and found nothing but the extra
    /// repositories' scripts. Fetched once and kept, as the Script Repo window
    /// does; nothing is downloaded.
    /// </summary>
    public async Task<List<ScriptInfo>> SearchAsync(string? query, int limit = 50, string? category = null)
    {
        if (Repo.Total == 0)
        {
            try { await Repo.GetScriptsAsync(null, default); }
            catch (Exception e) { Console.Error.WriteLine($"[scripts] script index: {e.Message}"); }
        }
        return Search(query, limit, category).ToList();
    }

    /// <summary>
    /// The Search Scripts window's categories (ScriptRepoViewModel.FilterOptions):
    /// a script is in one when a folder (or the file) on its path starts with
    /// that name. "Local" is the scripts that are not in the index: here, the
    /// extra repositories' (SKUA_SCRIPTS_EXTRA).
    /// </summary>
    public static IReadOnlyList<string> Categories { get; } =
        ["All", "Army", "Classes", "Dailies", "Evil", "Farm", "Good", "Legion", "Local", "Nation", "Other", "Rep", "Seasonal", "Story", "Ultras"];

    /// <summary>
    /// Searches as the Search Scripts window does (Skua.WPF ScriptRepoView,
    /// ScriptRepoViewModel), with each word of the query matched on its own:
    /// <list type="bullet">
    /// <item>index entries without a name (the "null" of the Core*.cs
    /// libraries) are left out;</item>
    /// <item>every word must be in the name, file name, path, description or
    /// a tag;</item>
    /// <item><paramref name="category"/> narrows it as the window's filter;</item>
    /// <item>sorted by name, A to Z.</item>
    /// </list>
    /// </summary>
    public IEnumerable<ScriptInfo> Search(string? query, int limit = 50, string? category = null)
    {
        string[] terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string filter = Categories.FirstOrDefault(c => c.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "All";
        static bool Named(string? v) => !string.IsNullOrWhiteSpace(v) && v != "null";

        IEnumerable<ScriptInfo> index = filter == "Local"
            ? []
            : Repo.Scripts.ToList().Where(s => Named(s.Name)
                && (filter == "All" || s.FilePath.Split('/').Any(part => part.StartsWith(filter, StringComparison.OrdinalIgnoreCase))));
        IEnumerable<ScriptInfo> local = filter is "All" or "Local" ? ExtraScripts.Local() : [];

        return index.Concat(local)
            .Where(s => terms.All(t =>
                (s.Name?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (s.FileName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (s.FilePath?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (Named(s.Description) && s.Description.Contains(t, StringComparison.OrdinalIgnoreCase))
                || (s.Tags?.Any(tag => Named(tag) && tag.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? false)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit);
    }

    /// <summary>
    /// A script path as given to the API: absolute, or relative to Skua's
    /// Scripts folder (the repository path, e.g. "Farm/Gold.cs"). A repository
    /// script not on disk yet is downloaded, with any other missing scripts,
    /// since scripts include each other.
    /// </summary>
    public async Task<string> ResolveAsync(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        string relative = path.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
            relative = relative["Scripts/".Length..];
        if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            relative += ".cs";
        string local = Path.Combine(ClientFileSources.SkuaScriptsDIR, relative);
        if (File.Exists(local))
            return local;
        await FirstSync;
        // The file system is case-sensitive here; the repository's spelling wins.
        var info = Repo.Scripts.ToList().FirstOrDefault(s => string.Equals(s.FilePath, relative, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return local;
        if (!info.Downloaded)
            await UpdateScriptsAsync();
        return info.LocalFile;
    }
}
