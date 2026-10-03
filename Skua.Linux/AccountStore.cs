using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Linux;

/// <summary>
/// Accounts added at run time (the web manager), next to the ones in the
/// environment (AQW_USER_N / AQW_PASS_N). One per tab, in
/// ~/.config/vibeskua/accounts.json (/config/.config/vibeskua/ in the
/// container; VIBESKUA_ACCOUNTS_FILE to move it):
///
///   { "accounts": [ { "tab": 2, "user": "...", "pass": "...", "server": "Twilly",
///                     "script": "Farm/GoldFarm", "autoStart": true } ] }
///
/// The environment wins: a tab with AQW_USER_N set is that account, and this
/// file cannot change it. web/main.js reads the same file for the pages'
/// auto-login (credsFor); this program reads it for the tabs to open and each
/// tab's script. The file holds passwords: it is written readable by its
/// owner only, and nothing here ever returns them.
/// </summary>
public static class AccountStore
{
    public sealed class Account
    {
        public int Tab { get; set; }
        public string User { get; set; } = "";
        public string? Pass { get; set; }
        public string? Server { get; set; }
        public string? Script { get; set; }
        public bool? AutoStart { get; set; }
    }

    private sealed class FileModel
    {
        public List<Account> Accounts { get; set; } = new();
    }

    public const int MaxTabs = 50;

    public static string FilePath { get; } = SkuaRuntime.EnvRaw("VIBESKUA_ACCOUNTS_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibeskua", "accounts.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly object Lock = new();

    /// <summary>The file's accounts, by tab; empty if there is no file or it cannot be read.</summary>
    public static List<Account> Load()
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new();
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), Json);
                return (model?.Accounts ?? new())
                    .Where(a => a.Tab is >= 1 and <= MaxTabs && !string.IsNullOrWhiteSpace(a.User))
                    .GroupBy(a => a.Tab).Select(g => g.Last())
                    .OrderBy(a => a.Tab).ToList();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[accounts] {FilePath}: {e.Message}");
                return new();
            }
        }
    }

    public static Account? Get(int tab) => Load().FirstOrDefault(a => a.Tab == tab);

    /// <summary>
    /// Adds or replaces tab's account; a null or empty Pass keeps the stored
    /// one. Returns the account as saved.
    /// </summary>
    public static Account Put(Account account)
    {
        lock (Lock)
        {
            var all = Load();
            var old = all.FirstOrDefault(a => a.Tab == account.Tab);
            if (string.IsNullOrEmpty(account.Pass))
                account.Pass = old?.Pass;
            all.RemoveAll(a => a.Tab == account.Tab);
            all.Add(account);
            Save(all);
            return account;
        }
    }

    public static bool Remove(int tab)
    {
        lock (Lock)
        {
            var all = Load();
            if (all.RemoveAll(a => a.Tab == tab) == 0)
                return false;
            Save(all);
            return true;
        }
    }

    // Written whole to a temporary file, then renamed over: a reader (main.js)
    // never sees half a file.
    private static void Save(List<Account> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel { Accounts = accounts.OrderBy(a => a.Tab).ToList() }, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Tab's account comes from the environment (AQW_USER_N; tab 1 also AQW_USER).</summary>
    public static bool InEnvironment(int tab) =>
        SkuaRuntime.EnvRaw($"AQW_USER_{tab}") is not null || (tab == 1 && SkuaRuntime.EnvRaw("AQW_USER") is not null);

    /// <summary>Tab's account name, from the environment or this file.</summary>
    public static string? UserFor(int tab) =>
        SkuaRuntime.EnvRaw($"AQW_USER_{tab}") ?? (tab == 1 ? SkuaRuntime.EnvRaw("AQW_USER") : null) ?? Get(tab)?.User;

    /// <summary>
    /// Tab's login, as web/main.js's credsFor has it: AQW_USER_N / AQW_PASS_N /
    /// AQW_SERVER_N (tab 1 also AQW_USER / AQW_PASS), else this file's account;
    /// AQW_SERVER is every tab's default server. The password is taken as given,
    /// quotes and all. Null without a user and password.
    /// </summary>
    public static (string User, string Pass, string? Server)? CredentialsFor(int tab)
    {
        string? server = SkuaRuntime.EnvRaw($"AQW_SERVER_{tab}");
        string? user = SkuaRuntime.EnvRaw($"AQW_USER_{tab}") ?? (tab == 1 ? SkuaRuntime.EnvRaw("AQW_USER") : null);
        string? pass;
        if (user is not null)
        {
            pass = Environment.GetEnvironmentVariable($"AQW_PASS_{tab}") ?? (tab == 1 ? Environment.GetEnvironmentVariable("AQW_PASS") : null);
        }
        else if (Get(tab) is { } account)
        {
            user = account.User;
            pass = account.Pass;
            server ??= account.Server;
        }
        else
        {
            return null;
        }
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            return null;
        return (user, pass, server ?? SkuaRuntime.EnvRaw("AQW_SERVER"));
    }
}
