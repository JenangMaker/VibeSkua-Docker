using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia;

// Skua.WPF/TemplateSelector: pick a template from the item's value type.

/// <summary>OptionDataTemplateSelector: game/application option items, by DisplayType.</summary>
public sealed class OptionTemplateSelector : IDataTemplate
{
    public IDataTemplate? BoolTemplate { get; set; }
    public IDataTemplate? IntTemplate { get; set; }
    public IDataTemplate? StringTemplate { get; set; }
    public IDataTemplate? EnumTemplate { get; set; }
    public IDataTemplate? ActionTemplate { get; set; }

    public bool Match(object? data) => data is DisplayOptionItemViewModelBase;

    public Control? Build(object? param)
    {
        if (param is not DisplayOptionItemViewModelBase vm)
            return BoolTemplate?.Build(param);
        Type t = vm.DisplayType;
        IDataTemplate? template =
            t == typeof(bool) ? BoolTemplate :
            t == typeof(string) ? StringTemplate :
            t == typeof(int) ? IntTemplate :
            t.IsEnum ? EnumTemplate :
            t == typeof(IRelayCommand) ? ActionTemplate :
            BoolTemplate;
        return template?.Build(param);
    }
}

/// <summary>OptionContainerDataTemplateSelector: script option values, by option Type.</summary>
public sealed class OptionContainerTemplateSelector : IDataTemplate
{
    public IDataTemplate? BoolTemplate { get; set; }
    public IDataTemplate? EnumTemplate { get; set; }
    public IDataTemplate? StringTemplate { get; set; }

    public bool Match(object? data) => data is OptionContainerItemViewModel;

    public Control? Build(object? param)
    {
        Type? t = (param as OptionContainerItemViewModel)?.Type;
        IDataTemplate? template =
            t is null ? StringTemplate :
            t == typeof(bool) ? BoolTemplate :
            t.IsEnum ? EnumTemplate :
            StringTemplate;
        return template?.Build(param);
    }
}
