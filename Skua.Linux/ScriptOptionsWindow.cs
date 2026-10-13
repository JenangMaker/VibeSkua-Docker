using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Options;

namespace Skua.Linux;

/// <summary>
/// Scripts whose options window is not to open when they start. Some open
/// it themselves on every run (UltrasLW's call <c>Bot.Config?.Configure()</c>
/// at the top of ScriptMain), which DontPreconfigure and SilentConfig do not
/// cover; on an unattended bot it waits on a desktop nobody watches. Listed by
/// their options storage name in Skua/options/skip-options-window.txt, set
/// from the options window's checkbox or the web manager. SKUA_SKIP_SCRIPT_OPTIONS=1
/// skips it for every script.
///
/// The saved options still apply: the script reads them as it would after
/// the window was closed with Save.
/// </summary>
public static class ScriptOptionsWindow
{
    public static bool SkipAll { get; } =
        SkuaRuntime.EnvRaw("SKUA_SKIP_SCRIPT_OPTIONS")?.ToLowerInvariant() is "1" or "true" or "yes" or "on" or "all";

    public static string FilePath => Path.Combine(ClientFileSources.SkuaOptionsDIR, "skip-options-window.txt");

    private static readonly object Lock = new();

    private static HashSet<string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? File.ReadAllLines(FilePath).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>The script's options storage is on the list.</summary>
    public static bool IsListed(string storage)
    {
        lock (Lock)
            return Load().Contains(storage);
    }

    public static bool IsSkipped(string storage) => SkipAll || IsListed(storage) || storage == QueueStorage;

    /// <summary>
    /// The script a queue entry with its own options started (its storage):
    /// those options were written for it, so its window does not open, also
    /// when it restarts after a relogin. Cleared by the next start through the API.
    /// </summary>
    public static string? QueueStorage { get; set; }

    public static void Set(string storage, bool skip)
    {
        if (string.IsNullOrWhiteSpace(storage))
            return;
        lock (Lock)
        {
            var list = Load();
            if (!(skip ? list.Add(storage) : list.Remove(storage)))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, list.Order(StringComparer.Ordinal));
        }
        Console.WriteLine($"[host] options window for {storage}: {(skip ? "skipped from now on" : "shown again")}");
    }
}

/// <summary>
/// Skua's script options, with the window skipped for the scripts listed in
/// <see cref="ScriptOptionsWindow"/>. Registered in place of
/// ScriptOptionContainer (SkuaRuntime.Create). Re-implements the interfaces
/// so that Configure, which is not virtual, is replaced for every caller:
/// scripts (Bot.Config) and ScriptManager reach it through them.
/// </summary>
public sealed class SkippableScriptOptionContainer(IDialogService dialogs, IServiceProvider services)
    : ScriptOptionContainer(dialogs), IScriptOptionContainer, IOptionContainer
{
    // Where Save writes now; null: where Load reads (AccountOptions).
    private string? _writing;

    public override string OptionsFile => _writing ?? AccountOptions.ReadFile(Storage, AccountOptions.Account(services));

    /// <summary>The logged-in account's own file and the shared one (AccountOptions).</summary>
    public override void Save()
    {
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        try
        {
            if (AccountOptions.Account(services) is { } account)
            {
                _writing = AccountOptions.AccountFile(Storage, account);
                base.Save();
            }
            _writing = AccountOptions.SharedFile(Storage);
            base.Save();
        }
        finally
        {
            _writing = null;
        }
    }

    void IOptionContainer.Configure()
    {
        // Only a running script's (its own call, or ScriptManager's before
        // the first run). The Script Loader's Options button works only while
        // no script runs, so it always opens the window, where the choice can
        // be undone.
        bool running = (services.GetService(typeof(IScriptManager)) as IScriptManager)?.ScriptRunning == true;
        if (!running || !ScriptOptionsWindow.IsSkipped(Storage))
        {
            Configure();
            return;
        }
        // What Configure does, without the window: the saved values.
        SetDefaults();
        Load();
        Console.WriteLine($"[host] options window for {Storage} skipped ({(ScriptOptionsWindow.SkipAll ? "SKUA_SKIP_SCRIPT_OPTIONS" : Storage == ScriptOptionsWindow.QueueStorage ? "the queue entry's own options" : "skip list")}); using the saved options");
    }
}
