using System.Text.Json;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Linux;

namespace Skua.App.Avalonia.Manager;

/// <summary>
/// The Manager's Start: opens the account as a tab of the running VibeSkua
/// (started first if need be) through the tab host's accounts API, which saves
/// it in the accounts file, opens its tab and logs it in, with the script if
/// one is given. The tab is the account's own if it has one, else an open tab
/// without an account, else the lowest free number.
/// </summary>
public sealed class HostLauncher : IClientLauncher
{
    private readonly IDialogService _dialogs;

    // One launch at a time: Start All launches in a row, and two launches
    // picking the same free tab would put both accounts in it.
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public HostLauncher(IDialogService dialogs)
    {
        _dialogs = dialogs;
    }

    public void Launch(string username, string password, string server, string? script, bool autoStart, string displayName)
    {
        _ = Task.Run(async () =>
        {
            await Lock.WaitAsync();
            try
            {
                await LaunchAsync(username, password, server, script, autoStart);
                // No account names in the log (it gets shared); the dialog below may name it.
                Console.WriteLine($"[manager] an account was launched ({server})");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[manager] launching an account failed: {e.Message}");
                Dispatcher.UIThread.Post(() => _dialogs.ShowMessageBox($"Could not launch {displayName}: {e.Message}", "Launch"));
            }
            finally
            {
                Lock.Release();
            }
        });
    }

    private static async Task LaunchAsync(string username, string password, string server, string? script, bool autoStart)
    {
        if (!await HostClient.EnsureRunning())
            throw new InvalidOperationException("VibeSkua did not start (no answer from its API in 90 s).");

        var accounts = (await HostClient.Get("/accounts")).GetProperty("accounts").EnumerateArray().ToList();
        var tabs = (await HostClient.Get("/tabs")).EnumerateArray().ToList();
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        int tab;
        if (accounts.FirstOrDefault(a => string.Equals(Str(a, "user"), username, StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } mine)
        {
            tab = mine.GetProperty("tab").GetInt32();
            if (Str(mine, "source") == "env")
            {
                // Set in VibeSkua's environment (AQW_USER_n): it logs in there by itself.
                await HostClient.Send(HttpMethod.Post, $"/tabs/{tab}/select");
                return;
            }
        }
        else
        {
            var taken = accounts.Select(a => a.GetProperty("tab").GetInt32()).ToHashSet();
            var emptyOpen = tabs.Select(t => t.GetProperty("tab").GetInt32()).Where(n => !taken.Contains(n)).Order().ToList();
            tab = emptyOpen.Count > 0
                ? emptyOpen[0]
                : Enumerable.Range(1, AccountStore.MaxTabs).FirstOrDefault(n => !taken.Contains(n));
            if (tab == 0)
                throw new InvalidOperationException($"every tab (1 to {AccountStore.MaxTabs}) already has an account.");
        }

        await HostClient.Send(HttpMethod.Put, $"/accounts/{tab}", new
        {
            user = username,
            pass = password,
            server,
            script,
            autoStart,
            open = true,
        });
        try { await HostClient.Send(HttpMethod.Post, $"/tabs/{tab}/select"); }
        catch { /* the tab may still be opening */ }
    }
}
