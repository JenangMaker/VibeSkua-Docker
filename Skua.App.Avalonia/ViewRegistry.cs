using Skua.App.Avalonia.Views;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia;

/// <summary>Skua.WPF/XAML/DataTemplates.xaml: which view shows which view model.</summary>
public static class ViewRegistry
{
    public static void RegisterAll()
    {
        // Dialogs
        ViewLocator.Register<MessageBoxDialogViewModel, MessageBoxDialog>();
        ViewLocator.Register<CustomDialogViewModel, CustomMessageBoxDialog>();
        ViewLocator.Register<InputDialogViewModel, InputDialog>();

        // Main menu
        ViewLocator.Register<JumpViewModel, JumpUserControl>();
        ViewLocator.Register<AutoViewModel, AutoUserControl>();

        // Scripts, logs, console
        ViewLocator.Register<ScriptLoaderViewModel, ScriptLoaderView>();
        ViewLocator.Register<ScriptRepoViewModel, ScriptRepoView>();
        ViewLocator.Register<ScriptSchedulerViewModel, ScriptSchedulerUserControl>();
        ViewLocator.Register<ScriptStatsViewModel, ScriptStatsUserControl>();
        ViewLocator.Register<LogsViewModel, LogsView>();
        ViewLocator.Register<LogTabViewModel, LogTabUserControl>();
        ViewLocator.Register<ConsoleViewModel, ConsoleView>();

        // Options
        ViewLocator.Register<GameOptionsViewModel, GameOptionsView>();
        ViewLocator.Register<ApplicationOptionsViewModel, ApplicationOptionsView>();
        ViewLocator.Register<OptionContainerViewModel, OptionContainerUserControl>();
        ViewLocator.Register<CoreBotsViewModel, CoreBotsOptionsView>();
        ViewLocator.Register<CBOptionsViewModel, CBOptionsUserControl>();
        ViewLocator.Register<CBOOtherOptionsViewModel, CBOOtherOptionsUserControl>();
        ViewLocator.Register<CBOClassEquipmentViewModel, CBOClassEquipmentUserControl>();
        ViewLocator.Register<CBOLoadoutViewModel, CBOLoadoutUserControl>();
    }
}
