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

        // Tools & Helpers
        ViewLocator.Register<LoaderViewModel, LoaderView>();
        ViewLocator.Register<CurrentDropsViewModel, CurrentDropsUserControl>();
        ViewLocator.Register<JunkItemsViewModel, JunkItemsUserControl>();
        ViewLocator.Register<FastTravelViewModel, FastTravelUserControl>();
        ViewLocator.Register<FastTravelEditorDialogViewModel, FastTravelEditorDialog>();
        ViewLocator.Register<LoadoutsViewModel, LoadoutsView>();

        // Combat > Runtime
        ViewLocator.Register<RuntimeHelpersViewModel, RuntimeHelpersView>();
        ViewLocator.Register<NotifyDropViewModel, DropNotifyUserControl>();
        ViewLocator.Register<BoostsViewModel, BoostsUserControl>();
        ViewLocator.Register<ToPickupDropsViewModel, ToPickupDropsUserControl>();
        ViewLocator.Register<RegisteredQuestsViewModel, RegisteredQuestsUserControl>();

        // Combat > Skills
        ViewLocator.Register<AdvancedSkillsViewModel, AdvancedSkillsView>();
        ViewLocator.Register<AdvancedSkillEditorViewModel, AdvancedSkillEditorUserControl>();
        ViewLocator.Register<SavedAdvancedSkillsViewModel, SavedAdvancedSkillsUserControl>();
        ViewLocator.Register<SkillRulesViewModel, SkillRuleUserControl>();
        ViewLocator.Register<SkillRuleEditorDialogViewModel, SkillRuleEditorDialog>();

        // Diagnostics > packets
        ViewLocator.Register<PacketSpammerViewModel, PacketSpammerView>();
        ViewLocator.Register<PacketLoggerViewModel, PacketLoggerView>();
        ViewLocator.Register<PacketInterceptorViewModel, PacketInterceptorView>();
    }
}
