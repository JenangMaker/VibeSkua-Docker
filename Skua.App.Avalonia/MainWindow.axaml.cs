using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _status;
    private GameEmbed? _embed;

    public MainWindow()
    {
        InitializeComponent();
        // As MainMenuUserControl did: the menu has its own view model.
        var menu = App.Service<MainMenuViewModel>();
        AddResetScripts(menu);
        MenuBar.DataContext = menu;
        _status = new DispatcherTimer(TimeSpan.FromSeconds(0.5), DispatcherPriority.Background, (_, _) => { UpdateSync(); UpdateStatus(); });
        Opened += (_, _) =>
        {
            _status.Start();
            // The game goes under the menu, as in the WPF app; without one to
            // embed, this becomes a bar above the game window instead.
            if (Skua.Linux.SkuaRuntime.EnvRaw("SKUA_EMBED_GAME") is "0" or "false" or "no")
            {
                WindowPlacement.ToTopBar(this, GameArea);
                return;
            }
            _embed = new GameEmbed(this, GameArea);
            _embed.Embedded += () => GameAreaText.IsVisible = false;
            _embed.Failed += () => WindowPlacement.ToTopBar(this, GameArea);
            _embed.Start();
            // The state when the post runs, not the event's: two throttle
            // updates on different threads can post in the other order.
            Skua.Linux.HostApi.Shrunk += _ => Dispatcher.UIThread.Post(() => _embed?.SetShrunk(Skua.Linux.HostApi.IsShrunk));
            _embed.SetShrunk(Skua.Linux.HostApi.IsShrunk);
        };
        Skua.Linux.HostApi.HeadlessChanged += _ => Dispatcher.UIThread.Post(() => HeadlessOverlay.IsVisible = Skua.Linux.HostApi.IsHeadless);
        HeadlessOverlay.IsVisible = Skua.Linux.HostApi.IsHeadless;
        Dashboard.DataContext = App.Service<ScriptStatsViewModel>();
        GameRow.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateDashboard();
        };
        // Before the window (and anything inside it) is destroyed.
        Closing += (_, _) => _embed?.Release();
        if (App.Mode == AppMode.TabChild && App.Runtime is { } runtime)
        {
            // The tab host finds this window, and switches Grid View, through
            // this tab's control API.
            runtime.Api.Routes["GET /ui/window"] = _ => Dispatcher.UIThread.InvokeAsync<object>(() =>
                new { xid = TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero ? ((ulong)h).ToString() : null }).GetTask();
            runtime.Api.Routes["POST /ui/grid"] = request =>
            {
                bool on = request.QueryString["on"] is "1" or "true";
                return Dispatcher.UIThread.InvokeAsync<object>(() =>
                {
                    SetGridView(on);
                    return new { grid = on };
                }).GetTask();
            };
        }
        StrongReferenceMessenger.Default.Register<MainWindow, ShowMainWindowMessage>(this, (w, _) => { w.Show(); w.Activate(); });
    }

    /// <summary>
    /// Scripts > Reset Scripts..., as Skua Manager has it (the Manager is not
    /// part of this port): empty the Scripts folder and download it all again.
    /// </summary>
    private static void AddResetScripts(MainMenuViewModel menu)
    {
        var scripts = menu.MainMenuItems.FirstOrDefault(i => i.Header == "Scripts");
        if (scripts?.SubItems is not { } items || items.Any(i => i.Header == "Reset Scripts..."))
            return;
        items.Add(new MainMenuItemViewModel("Reset Scripts...", new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            if (App.Runtime?.Scripts is not { } sync)
                return;
            string dir = Skua.Core.Models.ClientFileSources.SkuaScriptsDIR;
            bool mounted = Skua.Linux.ScriptSync.ScriptsFolderMounted;
            var answer = App.Service<IDialogService>().ShowMessageBox(
                $"This deletes EVERYTHING in {dir}, including scripts of your own and any changes you made, " +
                $"then downloads all scripts from {Skua.Core.Services.ScriptsSource.Name} again (a few minutes)." +
                (mounted ? "\r\n\r\nThat folder is mounted from the host: the files are deleted there too." : "") +
                "\r\n\r\nOnly do this if you were told to, or the scripts are broken. Reset the scripts?",
                "Reset Scripts", true);
            if (answer != true)
                return;
            _ = Task.Run(async () =>
            {
                var result = await sync.ResetScriptsAsync();
                string? error = result.GetType().GetProperty("error")?.GetValue(result) as string;
                if (error is not null)
                    App.Service<IDialogService>().ShowMessageBox($"Scripts were not reset: {error}.", "Reset Scripts");
            });
        })));
    }

    /// <summary>In the tab host's Grid View only the game shows, as in WPF.</summary>
    public void SetGridView(bool on)
    {
        _grid = on;
        MenuBar.IsVisible = !on;
        StatusBar.IsVisible = !on;
        UpdateDashboard();
    }

    private bool _grid;

    // AQW's stage; the game page letterboxes it, so wider than this is room to spare.
    private const double GameAspect = 960.0 / 550.0;
    // SKUA_DASHBOARD: 0 never, 1 always (outside Grid View), else when it fits.
    private static readonly string DashboardMode = Skua.Linux.SkuaRuntime.EnvRaw("SKUA_DASHBOARD") switch
    {
        "0" or "false" or "no" or "off" => "off",
        "1" or "true" or "yes" or "on" => "on",
        _ => "auto",
    };

    /// <summary>
    /// The dashboard beside the game, as GameContainerUserControl has it: never
    /// in Grid View, and (on auto) only while it costs the game at most 15% of
    /// its size. The WPF app only used spare width, which a 16:9 screen barely
    /// has. Measured on GameRow, which the dashboard does not change, so
    /// showing it cannot flip the decision.
    /// </summary>
    private void UpdateDashboard()
    {
        var size = GameRow.Bounds.Size;
        double game = Math.Min(size.Width, size.Height * GameAspect);
        double beside = Math.Min(size.Width - Dashboard.Width, size.Height * GameAspect);
        bool fits = game > 0 && beside / game >= 0.85;
        Dashboard.IsVisible = !_grid && DashboardMode switch { "on" => true, "off" => false, _ => fits };
    }

    // What Skua sees, under the menu.
    // Script sync, on the right of the status line: the current step (with a
    // count and progress bar while downloading), then the outcome for 15 s.
    private void UpdateSync()
    {
        if (App.Runtime?.Scripts is not { } sync)
            return;
        if (sync.Activity is { } activity)
        {
            int total = sync.Total, done = Math.Min(sync.Done, total);
            SyncText.Text = total > 0 ? $"{activity} {done:N0} / {total:N0}" : activity + "...";
            SyncProgress.IsVisible = true;
            SyncProgress.IsIndeterminate = total == 0;
            SyncProgress.Maximum = Math.Max(total, 1);
            SyncProgress.Value = done;
            SyncIcon.Kind = Material.Icons.MaterialIconKind.CloudSync;
            SyncPanel.IsVisible = true;
            ToolTip.SetTip(SyncPanel, $"Syncing scripts from {Skua.Core.Services.ScriptsSource.Name}");
        }
        else if (sync.FinishedAt != default && DateTime.UtcNow - sync.FinishedAt < TimeSpan.FromSeconds(15))
        {
            SyncText.Text = sync.Summary;
            SyncProgress.IsVisible = false;
            bool bad = sync.Summary.Contains("fail", StringComparison.OrdinalIgnoreCase) || sync.Summary.StartsWith("Could not");
            SyncIcon.Kind = bad ? Material.Icons.MaterialIconKind.CloudAlert : Material.Icons.MaterialIconKind.CloudCheck;
            SyncPanel.IsVisible = true;
            ToolTip.SetTip(SyncPanel, sync.LastResult);
        }
        else
        {
            SyncPanel.IsVisible = false;
        }
    }

    private void UpdateStatus()
    {
        if (App.Runtime is not { } runtime)
            return;
        var bridge = runtime.Bridge;
        if (!bridge.IsConnected)
        {
            StatusText.Text = "Waiting for the game page to connect...";
            return;
        }
        var bot = App.Service<IScriptInterface>();
        var scripts = App.Service<IScriptManager>();
        string script = scripts.ScriptRunning ? $"running {Path.GetFileNameWithoutExtension(scripts.LoadedScript)}" : "no script running";
        string name = bot.Options.StreamerMode
            ? $"Player {Skua.Linux.SkuaRuntime.Instance + 1}"   // matches the tab header
            : bot.Player.Username;
        StatusText.Text = bot.Player.LoggedIn
            ? $"{name} in {bot.Map.Name} ({bot.Player.Cell}) - HP {bot.Player.Health}/{bot.Player.MaxHealth} - {script}"
            : $"Connected, not logged in - {script}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _status.Stop();
        _embed?.Dispose();
        StrongReferenceMessenger.Default.UnregisterAll(this);
        base.OnClosed(e);
    }
}
