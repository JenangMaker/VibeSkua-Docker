using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Skua.App.Avalonia.Services;
using Skua.Core.AppStartup;
using Skua.Core.ViewModels;
using Skua.Linux;

namespace Skua.App.Avalonia;

/// <summary>
/// --snapshot DIR: renders the main window, the bot window and every
/// registered view (with its view model from the container, or a sample for
/// dialogs) to DIR/*.png with Avalonia's headless platform, for checking
/// the port without a display. Nothing is connected: game values are empty.
/// </summary>
public static class Snapshot
{
    public static void Run(string dir, string? only = null)
    {
        App.StartRuntime = false;
        App.Runtime = SkuaRuntime.Create(services =>
        {
            services.AddAvaloniaServices();
            services.AddSkuaMainAppViewModels();
        });
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        Directory.CreateDirectory(dir);
        var provider = App.Runtime.Services;

        // SNAPSHOT_START=1: also run the app's startup sequence (runtime,
        // theme, hotkeys on the main window), as a smoke test of that path.
        MainWindow? main = null;
        if (Environment.GetEnvironmentVariable("SNAPSHOT_START") == "1")
        {
            App.Runtime.Start();
            provider.GetRequiredService<AvaloniaThemeService>().ApplyCurrent();
            main = new MainWindow { DataContext = provider.GetRequiredService<MainViewModel>() };
            main.Show();
            Dispatcher.UIThread.RunJobs();
            if (provider.GetRequiredService<Skua.Core.Interfaces.IHotKeyService>() is AvaloniaHotKeyService hotkeys)
            {
                hotkeys.Target = main;
                hotkeys.Reload();
            }
            Console.WriteLine($"[snapshot] startup ok; main window key bindings: {main.KeyBindings.Count}");
        }

        void Shot(string name, Func<Window> create)
        {
            if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                Window window = create();
                window.Show();
                for (int i = 0; i < 3; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }
                window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
                Console.WriteLine($"[snapshot] {name} {window.Bounds.Width}x{window.Bounds.Height}");
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[snapshot] {name} failed: {e.GetBaseException().Message}");
            }
        }

        Shot("MainWindow", () => new MainWindow { DataContext = provider.GetRequiredService<MainViewModel>() });
        Shot("BotWindow", () => new BotWindow { DataContext = provider.GetRequiredService<BotWindowViewModel>() });
        Shot("Dialog-MessageBox", () => new HostDialog { DataContext = new MessageBoxDialogViewModel("A message from a script.", "Caption", true) });
        Shot("Dialog-Custom", () => new HostDialog { DataContext = new CustomDialogViewModel("Pick one:", "Caption", new[] { "Full", "Partial", "None" }) });
        Shot("Dialog-ScriptUpdates", () => new HostDialog
        {
            DataContext = new CustomDialogViewModel(
                """
                auqw/Scripts has 12 script(s) you do not have and 3 newer than yours (of 1925).

                Your Scripts folder (/config/.config/Skua/Scripts) is mounted from the host. "Update all" replaces your outdated scripts with the repository's versions, including any local changes to them; "Only missing" adds new scripts and leaves yours alone.

                Download them now?
                """,
                "Script Updates", new[] { "Update all", "Only missing", "Skip" }),
        });
        Shot("Dialog-Input", () => new HostDialog { DataContext = new InputDialogViewModel("Quantity", "How many?") });

        foreach (Type type in ViewLocator.RegisteredViewModels.OrderBy(t => t.Name))
        {
            object? vm;
            try { vm = provider.GetService(type); }
            catch { vm = null; }   // needs arguments only its caller has (dialogs)
            if (vm is null)
                continue;
            Shot(type.Name, () => new HostWindow { DataContext = vm, Width = 800, Height = 500 });
        }
    }
}
