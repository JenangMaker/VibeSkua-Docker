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
        MenuBar.DataContext = App.Service<MainMenuViewModel>();
        _status = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
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

    /// <summary>In the tab host's Grid View only the game shows, as in WPF.</summary>
    public void SetGridView(bool on)
    {
        MenuBar.IsVisible = !on;
        StatusBar.IsVisible = !on;
    }

    // What Skua sees, under the menu.
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
        StatusText.Text = bot.Player.LoggedIn
            ? $"{bot.Player.Username} in {bot.Map.Name} ({bot.Player.Cell}) - HP {bot.Player.Health}/{bot.Player.MaxHealth} - {script}"
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
