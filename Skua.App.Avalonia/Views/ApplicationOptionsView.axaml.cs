using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class ApplicationOptionsView : UserControl
{
    // Sections by settings key (the option's Tag), as upstream 2.1.2; the rest are webhook options.
    private static readonly string[] GeneralKeys = { "ShowUsernameInTitle", "GrandpaMode", "ClientAnim", "ReloginTryDelay", "ReloginWatchdog", "ClearFlashCache" };
    private static readonly string[] UpdateKeys =
    {
        "CheckBotScriptsUpdates", "AutoUpdateBotScripts", "CheckAdvanceSkillSetsUpdates", "AutoUpdateAdvanceSkillSetsUpdates",
        "CheckJunkItemsUpdates", "AutoUpdateJunkItems",
    };

    public ApplicationOptionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ApplicationOptionsViewModel vm)
                return;
            // Two action options have no Tag; key them by name.
            static string Key(DisplayOptionItemViewModelBase o) =>
                string.IsNullOrEmpty(o.Tag) ? o.Content.Replace(" ", "") : o.Tag;
            Page.DataContext = new OptionsPage(OptionsPage.Group(vm.ApplicationOptions, Key,
                ("General & Performance", "General", "ApplicationCogOutline", GeneralKeys),
                ("Auto Updates & Sync", "Updates", "CloudSyncOutline", UpdateKeys),
                ("Webhooks & Alerts", "Webhooks", "Webhook", null)));
        };
    }
}
