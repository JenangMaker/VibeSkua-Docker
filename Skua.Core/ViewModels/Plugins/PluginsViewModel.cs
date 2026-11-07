using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Utils;

namespace Skua.Core.ViewModels;

public partial class PluginsViewModel : BotControlViewModelBase
{
    public PluginsViewModel(IPluginManager pluginManager, IFileDialogService fileService)
        : base("Plugins", 800, 450)
    {
        PluginManager = pluginManager;
        _fileService = fileService;
    }

    protected override void OnActivated()
    {
        StrongReferenceMessenger.Default.Register<PluginsViewModel, PluginLoadedMessage, int>(this, (int)MessageChannels.Plugins, PluginLoaded);
        StrongReferenceMessenger.Default.Register<PluginsViewModel, PluginUnloadedMessage, int>(this, (int)MessageChannels.Plugins, PluginUnLoaded);

        _plugins.Clear();
        foreach (IPluginContainer container in PluginManager.Containers)
            _plugins.Add(new(container, container.OptionContainer.Options.Count > 0));
    }

    protected override void OnDeactivated()
    {
        StrongReferenceMessenger.Default.UnregisterAll(this);
    }

    private readonly IFileDialogService _fileService;

    [ObservableProperty]
    private RangedObservableCollection<PluginItemViewModel> _plugins = new();

    public IPluginManager PluginManager { get; }

    [RelayCommand]
    private void UnloadAllPlugins()
    {
        foreach (IPluginContainer container in PluginManager.Containers)
            PluginManager.Unload(container.Plugin);
    }

    [RelayCommand]
    private void LoadPlugin()
    {
        string? file = _fileService.OpenFile("DLL files |*.dll");

        if (string.IsNullOrEmpty(file))
            return;

        if (PluginManager.Load(file) is { } e)
        {
            string why = Skua.Core.Plugins.PluginManager.Describe(e);
            Console.Error.WriteLine($"[plugins] {Path.GetFileName(file)} not loaded: {why}");
            Ioc.Default.GetService<IDialogService>()?.ShowMessageBox($"{Path.GetFileName(file)} could not be loaded:\n{why}", "Plugins");
        }
        else
            Console.Error.WriteLine($"[plugins] {Path.GetFileName(file)} loaded");
    }

    private void PluginUnLoaded(PluginsViewModel recipient, PluginUnloadedMessage message)
    {
        PluginItemViewModel? plugin = recipient.Plugins.First(p => p.Container.Plugin.Name == message.Container.Plugin.Name);
        if (plugin is null)
            return;

        recipient.Plugins.Remove(plugin);
    }

    private void PluginLoaded(PluginsViewModel recipient, PluginLoadedMessage message)
    {
        recipient.Plugins.Add(new(message.Container, message.Container.OptionContainer.Options.Count > 0));
    }
}