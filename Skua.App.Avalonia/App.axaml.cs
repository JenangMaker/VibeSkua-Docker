using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Avalonia.Views;
using Skua.Core.ViewModels;
using Skua.Linux;

namespace Skua.App.Avalonia;

public partial class App : Application
{
    /// <summary>Skua.Core and the bridge; set by Program before the app starts.</summary>
    public static SkuaRuntime? Runtime { get; set; }

    /// <summary>Starts the runtime once the UI exists (off in snapshot mode).</summary>
    public static bool StartRuntime { get; set; } = true;

    public static T Service<T>() where T : notnull => Runtime!.Services.GetRequiredService<T>();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ViewRegistry.RegisterAll();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Runtime is not null)
        {
            if (StartRuntime)
                Runtime.Start();
            Service<Skua.App.Avalonia.Services.AvaloniaThemeService>().ApplyCurrent();
            desktop.MainWindow = new MainWindow { DataContext = Service<MainViewModel>() };
            // As Skua.App.WPF at startup: hotkeys bind to the main window.
            Service<Skua.Core.Interfaces.IHotKeyService>().Reload();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
