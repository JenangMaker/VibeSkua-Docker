using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;

namespace Skua.Core.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = global::Skua.AppInfo.Title;

    private System.Timers.Timer? _titleUpdateTimer;
    private IScriptPlayer? _player;
    private ISettingsService? _settingsService;
    private string? _lastUsername;
    private bool _showUsernameInTitle;

    public bool ShowUsernameInTitle
    {
        get => _showUsernameInTitle;
        set
        {
            if (SetProperty(ref _showUsernameInTitle, value))
            {
                UpdateTitle();
            }
        }
    }

    public MainViewModel(IEnumerable<TabItemViewModel> tabs, IDialogService dialogService)
    {
        _title = global::Skua.AppInfo.Title;
        Ioc.Default.GetRequiredService<IDiscordWebhookService>().Initialize();

        try
        {
            _player = Ioc.Default.GetService<IScriptPlayer>();
            _settingsService = Ioc.Default.GetService<ISettingsService>();

            if (_settingsService != null)
            {
                _showUsernameInTitle = _settingsService.Get<bool>("ShowUsernameInTitle");
            }
        }
        catch { }

        StartTitleTimer();
        UpdateTitle();
    }

    private void StartTitleTimer()
    {
        if (_titleUpdateTimer == null)
        {
            // The name only changes at login: every 5 s is soon enough (it was a game read a second).
            _titleUpdateTimer = new System.Timers.Timer(5000);
            _titleUpdateTimer.Elapsed += (s, e) => CheckAndUpdateTitle();
            _titleUpdateTimer.AutoReset = true;
            _titleUpdateTimer.Start();
        }
    }

    private string? GetPlayerUsername()
    {
        if (_player == null) return null;
        try
        {
            string? u = _player.Username;
            if (string.IsNullOrWhiteSpace(u) || u == "null" || u == "undefined")
            {
                var flash = Ioc.Default.GetService<IFlashUtil>();
                if (flash != null)
                {
                    u = flash.GetGameObject<string>("world.myAvatar.objData.strUsername");
                    if (u == "null" || u == "undefined") u = null;
                }
            }
            return u;
        }
        catch
        {
            return null;
        }
    }

    private void CheckAndUpdateTitle()
    {
        if (_settingsService == null)
            return;
        // Read on the timer's thread: a game call waits for the game, and on the UI thread
        // that wait froze the window. Only a change goes to the UI thread.
        bool showUsername;
        string? currentUsername;
        try
        {
            showUsername = _settingsService.Get<bool>("ShowUsernameInTitle");
            currentUsername = GetPlayerUsername();
        }
        catch
        {
            return;
        }
        if (showUsername == _showUsernameInTitle && currentUsername == _lastUsername)
            return;

        var dispatcher = Ioc.Default.GetService<IDispatcherService>();
        if (dispatcher == null)
        {
            DoUpdate();
            return;
        }

        dispatcher.Invoke(DoUpdate);

        void DoUpdate()
        {
            try
            {
                _showUsernameInTitle = showUsername;
                UpdateTitle();
            }
            catch { }
        }
    }

    public void UpdateTitle()
    {
        string? username = GetPlayerUsername();

        _lastUsername = username;

        string title = global::Skua.AppInfo.Title;

        if (_showUsernameInTitle && !string.IsNullOrWhiteSpace(username))
            title += $" : {username}";

        Title = title;

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "SkuaTabs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{System.Diagnostics.Process.GetCurrentProcess().Id}.txt");
            File.WriteAllText(file, username ?? "");
        }
        catch { }
    }

    [RelayCommand]
    private void ShowMainWindow()
    {
        StrongReferenceMessenger.Default.Send<ShowMainWindowMessage>();
    }
}