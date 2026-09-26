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

    public MainWindow()
    {
        InitializeComponent();
        // As MainMenuUserControl did: the menu has its own view model.
        MenuBar.DataContext = App.Service<MainMenuViewModel>();
        _status = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus());
        Opened += (_, _) => _status.Start();
        StrongReferenceMessenger.Default.Register<MainWindow, ShowMainWindowMessage>(this, (w, _) => { w.Show(); w.Activate(); });
    }

    // There is no game in this window (it is in the vibeskua-web page), so a
    // line of what Skua sees stands in for it.
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
        StrongReferenceMessenger.Default.UnregisterAll(this);
        base.OnClosed(e);
    }
}
