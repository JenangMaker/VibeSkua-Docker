using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Runtime is not { } runtime)
            return;
        // Start the runtime off the UI thread: services it creates may need the
        // UI thread (the dispatcher service) while it waits on them, which
        // deadlocked with the start running on it. The windows follow.
        Task.Run(() =>
        {
            try
            {
                if (StartRuntime)
                    runtime.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[host] start failed: {e}");
            }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            Service<Skua.App.Avalonia.Services.AvaloniaThemeService>().ApplyCurrent();
            var main = new MainWindow { DataContext = Service<MainViewModel>() };
            desktop.MainWindow = main;
            main.Show();
            // As Skua.App.WPF at startup: hotkeys bind to the main window.
            Service<Skua.Core.Interfaces.IHotKeyService>().Reload();
            Console.Error.WriteLine("[host] start: windows up");
        }));
    }
}
