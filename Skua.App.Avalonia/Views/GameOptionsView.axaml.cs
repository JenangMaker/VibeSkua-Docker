using Avalonia.Controls;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class GameOptionsView : UserControl
{
    // Sections by script option name, as upstream 2.1.2's GameOptionsViewModel; the rest is Gameplay.
    private static readonly string[] Visuals = { "LagKiller", "HidePlayers", "OptimizePlayers", "DisableFX", "DisableCollisions", "ShowFPS", "SetFPS", "Upgrade", "Staff", "ReloadMap" };
    private static readonly string[] Combat = { "AggroMonsters", "AggroAllMonsters", "AttackWithoutTarget", "InfiniteRange", "Magnetise", "HuntDelay", "HuntBuffer", "RestPackets", "SafeTimings" };
    private static readonly string[] Drops = { "AcceptACDrops", "AcceptAllDrops", "RejectAllDrops" };
    private static readonly string[] Relogin = { "AutoRelogin", "AutoReloginAny", "ReloginFallback", "RetryRelogin", "SafeRelogin", "ReloginTries", "LoginTimeout" };

    public GameOptionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not GameOptionsViewModel vm)
                return;
            // The option's name is its script option's, decamelized ("Lag Killer" -> LagKiller).
            static string Key(DisplayOptionItemViewModelBase o) => o.Content.Replace(" ", "");
            var groups = OptionsPage.Group(vm.GameOptions, Key,
                ("Performance & Visuals", "Visuals", "Speedometer", Visuals),
                ("Combat & Targeting", "Combat", "SwordCross", Combat),
                ("Drops & Looting", "Drops", "PackageVariantClosed", Drops),
                ("Auto-Relogin & Network", "Relogin", "ServerNetwork", Relogin),
                ("Gameplay & Character", "Gameplay", "AccountCogOutline", null));
            groups[^1].Items.Sort((a, b) => string.Compare(a.Content, b.Content, StringComparison.OrdinalIgnoreCase));
            Page.DataContext = new OptionsPage(groups);
        };
    }
}
