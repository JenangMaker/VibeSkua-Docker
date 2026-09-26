using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Services;

// Skua.WPF's services (Skua.WPF/Services) on Avalonia. Registered over
// Skua.Linux's headless stand-ins; themes, sounds and screenshots
// keep the stand-ins for now.

public static class AvaloniaServiceCollection
{
    public static IServiceCollection AddAvaloniaServices(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcherService, AvaloniaDispatcherService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IWindowService, AvaloniaWindowService>();
        services.AddSingleton<IFileDialogService, AvaloniaFileDialogService>();
        services.AddSingleton<IHotKeyService, AvaloniaHotKeyService>();
        return services;
    }

    /// <summary>The window to parent dialogs and pickers on: the active one, else the main one.</summary>
    internal static Window? CurrentWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
    }
}

public sealed class AvaloniaDispatcherService : IDispatcherService
{
    public void Invoke(Action action) => UiThread.Invoke(action);
}

public sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly Dictionary<string, object> _data = new();

    private static IClipboard? Clipboard => AvaloniaServiceCollection.CurrentWindow()?.Clipboard;

    public void SetText(string text) => UiThread.Run(async () =>
    {
        if (Clipboard is { } c)
            await c.SetTextAsync(text);
    });

    public string GetText() => UiThread.Run(async () =>
        Clipboard is { } c ? await c.GetTextAsync() ?? "" : "");

    // Only Skua's own copy/paste of structured items uses these; keep them in-process.
    public void SetData(string format, object data) => _data[format] = data;

    public object GetData(string format) => _data.TryGetValue(format, out var d) ? d : null!;
}

public sealed class AvaloniaDialogService : IDialogService
{
    private static bool? Show(object viewModel, string? title = null, Action<HostDialog>? closed = null) => UiThread.Run(async () =>
    {
        HostDialog dialog = new() { DataContext = viewModel };
        if (title is not null)
            dialog.Title = title;
        if (closed is not null)
            dialog.Closed += (_, _) => closed(dialog);
        Window? owner = AvaloniaServiceCollection.CurrentWindow();
        if (owner is null || !owner.IsVisible)
        {
            // No window to be modal over (e.g. at startup): show it on its own.
            TaskCompletionSource<bool?> result = new();
            dialog.Closed += (_, _) => result.TrySetResult(dialog.Result);
            dialog.Show();
            return await result.Task;
        }
        await dialog.ShowDialog(owner);
        return dialog.Result;
    });

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => Show(viewModel);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => Show(viewModel, title);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class =>
        Show(viewModel, closed: _ =>
        {
            try { callback(viewModel); }
            catch { }
        });

    public void ShowMessageBox(string message, string caption) => Show(new MessageBoxDialogViewModel(message, caption));

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo) =>
        Show(new MessageBoxDialogViewModel(message, caption, yesAndNo));

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        CustomDialogViewModel viewModel = new(message, caption, buttons);
        Show(viewModel);
        return viewModel.Result ?? DialogResult.Cancelled;
    }
}

public sealed class AvaloniaWindowService(IServiceProvider services) : IWindowService
{
    private readonly Dictionary<string, HostWindow> _managed = new();
    private readonly Dictionary<string, IManagedWindow> _managedViewModels = new();

    public void ShowWindow<TViewModel>() where TViewModel : class => UiThread.Invoke(() =>
    {
        object? vm = services.GetService<TViewModel>();
        Window window = vm is BotWindowViewModel ? new BotWindow { DataContext = vm } : new HostWindow { DataContext = vm };
        window.Show();
    });

    public void ShowWindow<TViewModel>(int width, int height) where TViewModel : class => UiThread.Invoke(() =>
        new HostWindow
        {
            DataContext = services.GetService<TViewModel>(),
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        }.Show());

    public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class => UiThread.Invoke(() =>
        new HostWindow { DataContext = viewModel }.Show());

    public void RegisterManagedWindow<TViewModel>(string key, TViewModel viewModel) where TViewModel : class, IManagedWindow =>
        _managedViewModels.TryAdd(key, viewModel);

    public void ShowManagedWindow(string key) => UiThread.Invoke(() =>
    {
        if (!_managedViewModels.TryGetValue(key, out var vm))
            return;
        if (!_managed.TryGetValue(key, out var window))
        {
            window = new HostWindow
            {
                DataContext = vm,
                Width = vm.Width,
                Height = vm.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                // Like WPF's HideWindow: closing hides, so the view keeps its state.
                HideOnClose = true,
            };
            _managed[key] = window;
        }
        window.Show();
        window.Activate();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (vm is ObservableRecipient recipient)
            recipient.IsActive = true;
    });
}

public sealed class AvaloniaFileDialogService : IFileDialogService
{
    private const string DefaultFilter = "Text Files (*.txt)|*.txt";

    private static IStorageProvider? Storage => AvaloniaServiceCollection.CurrentWindow()?.StorageProvider;

    /// <summary>WPF's "Name|*.a;*.b|Name2|*.c" as Avalonia file types.</summary>
    private static List<FilePickerFileType>? Types(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return null;
        string[] parts = filter.Split('|');
        List<FilePickerFileType> types = new();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries) });
        return types;
    }

    private static async Task<IStorageFolder?> Folder(IStorageProvider storage, string? path) =>
        path is not null && Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : null;

    private static string? Open(string? initialDirectory, string? filter) => UiThread.Run(async () =>
    {
        if (Storage is not { } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = Types(filter),
            SuggestedStartLocation = await Folder(storage, initialDirectory),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    private static string? SaveAs(string? initialDirectory, string? filter) => UiThread.Run(async () =>
    {
        if (Storage is not { } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            FileTypeChoices = Types(filter),
            SuggestedStartLocation = await Folder(storage, initialDirectory),
        });
        return file?.TryGetLocalPath();
    });

    private static string? PickFolder(string? initialDirectory) => UiThread.Run(async () =>
    {
        if (Storage is not { } storage)
            return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the download folder.",
            AllowMultiple = false,
            SuggestedStartLocation = await Folder(storage, initialDirectory),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    });

    public string? OpenFile() => Open(ClientFileSources.SkuaDIR, null);
    public string? OpenFile(string filters) => Open(ClientFileSources.SkuaDIR, filters);
    public string? OpenFile(string initialDirectory, string filters) => Open(initialDirectory, filters);
    public string? OpenFolder() => PickFolder(null);
    public string? OpenFolder(string initialDirectory) => PickFolder(initialDirectory);

    public IEnumerable<string>? OpenText() =>
        Open(ClientFileSources.SkuaDIR, DefaultFilter) is { } file ? File.ReadAllLines(file) : null;

    public string? Save() => SaveAs(ClientFileSources.SkuaDIR, DefaultFilter);
    public string? Save(string filters) => SaveAs(ClientFileSources.SkuaDIR, filters);
    public string? Save(string initialDirectory, string filters) => SaveAs(initialDirectory, filters);

    public void SaveText(string contents)
    {
        if (Save() is { } file)
            File.WriteAllText(file, contents);
    }

    public void SaveText(IEnumerable<string> contents)
    {
        if (Save() is { } file)
            File.WriteAllLines(file, contents);
    }
}
