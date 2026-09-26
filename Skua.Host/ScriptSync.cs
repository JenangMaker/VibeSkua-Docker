using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;

namespace Skua.Host;

/// <summary>
/// Skua's script repository (auqw/Scripts, branch Skua), as the WPF app uses
/// it at startup: fetch the index, download missing and outdated scripts into
/// Skua/Scripts, refresh the advanced skill sets, quest data and junk list.
/// Honours the same settings (CheckBotScriptsUpdates, AutoUpdateBotScripts,
/// CheckAdvanceSkillSetsUpdates, AutoUpdateAdvanceSkillSetsUpdates,
/// CheckJunkItemsUpdates, AutoUpdateJunkItems).
/// </summary>
public sealed class ScriptSync(IServiceProvider services)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _firstSync = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IGetScriptsService Repo => services.GetRequiredService<IGetScriptsService>();
    private ISettingsService Settings => services.GetRequiredService<ISettingsService>();

    public bool Syncing { get; private set; }
    public string LastResult { get; private set; } = "not run";

    /// <summary>Completes once the startup sync has finished, whatever its outcome.</summary>
    public Task FirstSync => _firstSync.Task;

    public async Task StartupAsync()
    {
        try
        {
            if (Settings.Get<bool>("CheckBotScriptsUpdates"))
                await UpdateScriptsAsync(download: Settings.Get<bool>("AutoUpdateBotScripts"));
            else
                LastResult = "script updates off (CheckBotScriptsUpdates)";

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
                && Settings.Get<bool>("AutoUpdateJunkItems")
                && await Repo.CheckJunkItemsUpdates() > 0
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

    /// <summary>Refreshes the index and, if <paramref name="download"/>, fetches missing and outdated scripts.</summary>
    public async Task<object> UpdateScriptsAsync(bool download = true)
    {
        await _gate.WaitAsync();
        Syncing = true;
        try
        {
            Console.WriteLine("[scripts] fetching the script index");
            await Repo.RefreshScriptsAsync(null, default);
            var repo = Repo;
            int missing = repo.Missing, outdated = repo.Outdated;
            int fetched = 0;
            if (download && (missing > 0 || outdated > 0))
            {
                Console.WriteLine($"[scripts] downloading {missing} missing, {outdated} outdated of {repo.Total}");
                fetched = await repo.DownloadAllWhereAsync(s => !s.Downloaded || s.Outdated);
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
