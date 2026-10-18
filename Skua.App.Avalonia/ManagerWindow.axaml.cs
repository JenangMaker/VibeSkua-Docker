using Avalonia.Controls;
using Skua.App.Avalonia.Manager;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.App.Avalonia;

/// <summary>
/// VibeSkua Manager (App.Mode Manager): Skua.Manager's account list and its
/// tabs on Skua.Core's manager view models. Start opens accounts as tabs of
/// VibeSkua (HostLauncher) instead of separate Skua.exe windows; Running lists
/// those tabs in place of the Launcher's processes. Not here: the client
/// Updater (Skua's installs auqw/Skua releases over VibeSkua; Program.cs takes
/// its services out), the Options (the launched tabs share the Manager's
/// settings file, theme included) and Goals (the original author's donation links).
/// </summary>
public partial class ManagerWindow : Window
{
    public ManagerWindow()
    {
        InitializeComponent();
        List<TabItemViewModel> tabs = new()
        {
            new("Accounts", App.Service<AccountManagerViewModel>()),
            new("Running", App.Service<RunningTabsViewModel>()),
            new("Scripts", App.Service<ScriptUpdaterViewModel>()),
            new("Themes", App.Service<ApplicationThemesViewModel>()),
            new("About", App.Service<AboutViewModel>()),
        };
        DataContext = new ManagerMainViewModel(tabs, App.Service<IDialogService>(), App.Service<ISettingsService>());
    }
}
