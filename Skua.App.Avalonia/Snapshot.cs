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
        Shot("Dialog-Input", () => new HostDialog { DataContext = new InputDialogViewModel("Quantity", "How many?") });

        foreach (Type type in ViewLocator.RegisteredViewModels.OrderBy(t => t.Name))
        {
            if (provider.GetService(type) is not { } vm)
                continue;
            Shot(type.Name, () => new HostWindow { DataContext = vm, Width = 800, Height = 500 });
        }
    }
}
