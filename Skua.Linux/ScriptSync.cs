using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;

namespace Skua.Linux;

/// <summary>
/// Skua's script repository (auqw/Scripts, branch Skua), as the WPF app uses
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
            $"auqw/Scripts has {missing} script(s) you do not have and {outdated} newer than yours (of {repo.Total}).\r\n\r\n" +
            (mounted
                ? $"Your Scripts folder ({ClientFileSources.SkuaScriptsDIR}) is mounted from the host. \"Update all\" replaces your outdated scripts with the repository's versions, including any local changes to them; \"Only missing\" adds new scripts and leaves yours alone.\r\n\r\n"
                : "") +
            "Download them now?";
        DialogResult choice = Dialogs.ShowMessageBox(message, "Script Updates", "Update all", "Only missing", "Skip");
        Scope scope = choice.Value switch { 0 => Scope.All, 1 => Scope.Missing, _ => Scope.None };
        Console.WriteLine($"[scripts] {missing} missing, {outdated} outdated; chose: {(choice.Value < 0 ? "Skip" : choice.Text)}");
        if (scope != Scope.None)
            await UpdateScriptsAsync(scope, refresh: false);
    }

    public bool Syncing { get; private set; }
    public string LastResult { get; private set; } = "not run";

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
            if (!Settings.Get<bool>("CheckBotScriptsUpdates"))
                LastResult = "script updates off (CheckBotScriptsUpdates)";
            else if (mode is "off" or "0" or "false" or "no")
                LastResult = "script updates off (SKUA_SCRIPT_SYNC)";
            else
            {
                bool mounted = ScriptsFolderMounted;
                if (mode == "ask" || mounted || !Settings.Get<bool>("AutoUpdateBotScripts"))
                    await AskAndUpdateAsync(mounted);
                else
                    await UpdateScriptsAsync(Scope.All);
            }

            if (Settings.Get<bool>("CheckAdvanceSkillSetsUpdates")
                && Settings.Get<bool>("AutoUpdateAdvanceSkillSetsUpdates")
                && await Repo.CheckAdvanceSkillSetsUpdates() > 0
                && await Repo.UpdateSkillSetsFile())
            {
                services.GetRequiredService<IAdvancedSkillContainer>().SyncSkills();
                Console.WriteLine("[scripts] advanced skill sets updated");
            }

            await Repo.UpdateQuestDataFile();

            if (Settings.Get<bool>("CheckJunkItemsUpdates")
                && await Repo.CheckJunkItemsUpdates() > 0
                && (Settings.Get<bool>("AutoUpdateJunkItems")
                    || Dialogs.ShowMessageBox("Would you like to update your Junk Items list?", "Junk Items Update", true) == true)
                && await Repo.UpdateJunkItemsFile())
            {
                services.GetRequiredService<IJunkService>().Load();
                Console.WriteLine("[scripts] junk item list updated");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[scripts] startup sync: {e.Message}");
        }
        finally
        {
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
        try
        {
            if (refresh || Repo.Total == 0)
            {
                Console.WriteLine("[scripts] fetching the script index");
                await Repo.RefreshScriptsAsync(null, default);
            }
            var repo = Repo;
            int missing = repo.Missing, outdated = repo.Outdated;
            int fetched = 0;
            if (scope == Scope.All && (missing > 0 || outdated > 0))
            {
                Console.WriteLine($"[scripts] downloading {missing} missing, {outdated} outdated of {repo.Total}");
                fetched = await repo.DownloadAllWhereAsync(s => !s.Downloaded || s.Outdated);
            }
            else if (scope == Scope.Missing && missing > 0)
            {
                Console.WriteLine($"[scripts] downloading {missing} missing of {repo.Total} (leaving {outdated} outdated as they are)");
                fetched = await repo.DownloadAllWhereAsync(s => !s.Downloaded);
            }
            LastResult = $"{repo.Total} scripts; {fetched} downloaded at {DateTime.UtcNow:u}";
            Console.WriteLine($"[scripts] {LastResult}");
            return new { total = repo.Total, missing, outdated, downloaded = fetched };
        }
        catch (Exception e)
        {
            LastResult = $"failed: {e.Message}";
            Console.Error.WriteLine($"[scripts] {LastResult}");
            return new { error = e.Message };
        }
        finally
        {
            Syncing = false;
            _gate.Release();
        }
    }

    /// <summary>Scripts in the repository index whose path, name or tags contain every term.</summary>
    public IEnumerable<ScriptInfo> Search(string? query, int limit = 50)
    {
        string[] terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Repo.Scripts.ToList()
            .Where(s => terms.All(t =>
                (s.FilePath?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (s.Name?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (s.Tags?.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? false)))
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
