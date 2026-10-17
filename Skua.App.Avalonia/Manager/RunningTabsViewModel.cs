using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Skua.App.Avalonia.Manager;

/// <summary>
/// The Manager's Running tab, in place of the WPF Launcher's process list: the
/// running VibeSkua's tabs (one per launched account), from its tab host API.
/// </summary>
public sealed class RunningTabsViewModel : ObservableObject
{
    public ObservableCollection<RunningTab> Tabs { get; } = new();

    private string _status = "Checking VibeSkua...";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _hostRunning;
    public bool HostRunning { get => _hostRunning; private set => SetProperty(ref _hostRunning, value); }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand OpenVibeSkuaCommand { get; }

    public RunningTabsViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(Refresh);
        OpenVibeSkuaCommand = new AsyncRelayCommand(async () =>
        {
            Status = "Starting VibeSkua...";
            if (!await HostClient.EnsureRunning())
                Status = "VibeSkua did not start.";
            await Refresh();
        });
    }

    private DispatcherTimer? _timer;

    /// <summary>Refreshes every few seconds while the tab is shown.</summary>
    public void SetVisible(bool visible)
    {
        _timer?.Stop();
        _timer = null;
        if (!visible)
            return;
        _ = Refresh();
        _timer = DispatcherTimer.Run(() => { _ = Refresh(); return true; }, TimeSpan.FromSeconds(3));
    }

    private bool _refreshing;

    public async Task Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            if (!await HostClient.IsRunning())
            {
                HostRunning = false;
                Tabs.Clear();
                Status = "VibeSkua is not running. Start an account in the Accounts tab, or open VibeSkua.";
                return;
            }
            var list = (await HostClient.Get("/tabs")).EnumerateArray().Select(t => new RunningTab(this, t)).ToList();
            HostRunning = true;
            Tabs.Clear();
            foreach (var tab in list)
                Tabs.Add(tab);
            Status = $"{list.Count} tab{(list.Count == 1 ? "" : "s")} open, {list.Count(t => t.LoggedIn)} logged in";
        }
        catch (Exception e)
        {
            Status = $"Could not read VibeSkua's tabs: {e.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    internal async Task Act(int tab, string action)
    {
        try
        {
            await HostClient.Send(HttpMethod.Post, $"/tabs/{tab}/{action}");
        }
        catch (Exception e)
        {
            Status = $"Tab {tab}: {e.Message}";
        }
        await Refresh();
    }
}

public sealed class RunningTab
{
    public RunningTab(RunningTabsViewModel owner, JsonElement t)
    {
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Number = t.GetProperty("tab").GetInt32();
        Title = Str(t, "title") ?? $"Skua {Number}";
        Account = Str(t, "account") ?? "no account";
        LoggedIn = t.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
        Selected = t.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True;
        string? queue = t.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Object
            ? $"{q.GetProperty("position").GetInt32()}/{q.GetProperty("count").GetInt32()}{(q.TryGetProperty("playing", out var p2) && p2.ValueKind == JsonValueKind.True ? " playing" : "")}"
            : null;
        double? cpu = t.TryGetProperty("skua", out var k) && k.ValueKind == JsonValueKind.Object && k.TryGetProperty("cpu", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
        State = (LoggedIn ? "logged in" : "not logged in") + (queue is null ? "" : $", queue {queue}") + (cpu is { } p ? $", Skua {p:0.#}% CPU" : "");
        SelectCommand = new AsyncRelayCommand(() => owner.Act(Number, "select"));
        RestartCommand = new AsyncRelayCommand(() => owner.Act(Number, "restart"));
        CloseCommand = new AsyncRelayCommand(() => owner.Act(Number, "close"));
    }

    public int Number { get; }
    public string Title { get; }
    public string Account { get; }
    public bool LoggedIn { get; }
    public bool Selected { get; }
    public string State { get; }
    public IAsyncRelayCommand SelectCommand { get; }
    public IAsyncRelayCommand RestartCommand { get; }
    public IAsyncRelayCommand CloseCommand { get; }
}
