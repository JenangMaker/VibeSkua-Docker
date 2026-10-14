using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Linux;

/// <summary>
/// Script options per account. Every tab shares Skua/options, where Skua keeps
/// one file per script (options/&lt;storage&gt;.cfg): a tab saving its options
/// changed them for every tab, so a script that restarted (after a relogin,
/// or from a queue) took the options another account saved meanwhile.
///
/// Here a logged-in account reads options/&lt;storage&gt;(&lt;account&gt;).cfg when it
/// has one, else the shared file; saving writes both, so the shared file
/// holds the options saved last, which accounts without their own start with.
/// CoreBots keeps its own options per account the same way (CBO_Storage(&lt;account&gt;).txt).
/// </summary>
public static class AccountOptions
{
    public static string SharedFile(string storage) => Path.Combine(ClientFileSources.SkuaOptionsDIR, $"{storage}.cfg");

    public static string AccountFile(string storage, string account) =>
        Path.Combine(ClientFileSources.SkuaOptionsDIR, $"{storage}({Safe(account)}).cfg");

    /// <summary>
    /// The account whose options a Skua with no game reads (ScriptOptionsProbe:
    /// another tab's account); null: the logged-in one.
    /// </summary>
    public static string? AccountOverride { get; set; }

    /// <summary>The logged-in account's name, lower case; null when none is.</summary>
    public static string? Account(IServiceProvider services)
    {
        if (AccountOverride is { Length: > 0 } other)
            return other.Trim().ToLowerInvariant();
        try
        {
            var player = services.GetRequiredService<IScriptPlayer>();
            return player.LoggedIn && player.Username is { Length: > 0 } name ? name.Trim().ToLowerInvariant() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The file this account reads: its own if it has one, else the shared one.</summary>
    public static string ReadFile(string storage, string? account) =>
        account is not null && File.Exists(AccountFile(storage, account)) ? AccountFile(storage, account) : SharedFile(storage);

    /// <summary>Removes every account's own file for the script: all of them use the shared one again.</summary>
    public static int ForgetAccounts(string storage)
    {
        int removed = 0;
        string prefix = storage + "(";
        try
        {
            foreach (string file in Directory.EnumerateFiles(ClientFileSources.SkuaOptionsDIR, "*.cfg"))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(").cfg", StringComparison.Ordinal))
                {
                    File.Delete(file);
                    removed++;
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] script options of {storage}: {e.Message}");
        }
        return removed;
    }

    /// <summary>
    /// Writes the account's own file from option lines (Category:Name=Value),
    /// as a queue entry with its own options does before it starts.
    /// </summary>
    public static string Write(string storage, string account, IEnumerable<string> lines)
    {
        string file = AccountFile(storage, account);
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        File.WriteAllLines(file, lines);
        return file;
    }

    // An account name in a file name: letters, digits, spaces and _ - . only.
    private static string Safe(string account) =>
        new(account.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' ? c : '_').ToArray());
}
