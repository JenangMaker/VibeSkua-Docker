using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.ViewModels;
using Skua.Core.ViewModels.Manager;

namespace Skua.App.Avalonia.Views;

/// <summary>Skua.WPF/Views/AccountManager.xaml.cs: the add form, tags and groups.</summary>
public partial class AccountManagerView : UserControl
{
    public AccountManagerView()
    {
        InitializeComponent();
        ColumnsBox.ItemsSource = Enumerable.Range(1, 6).ToList();
        PasswordBox.TextChanged += (_, _) =>
        {
            if (DataContext is AccountManagerViewModel vm)
                vm.PasswordInput = PasswordBox.Text ?? "";
        };
        AttachedToVisualTree += (_, _) =>
        {
            StrongReferenceMessenger.Default.Register<AccountManagerView, ClearPasswordBoxMessage>(this, (r, _) => r.PasswordBox.Text = "");
            WeakReferenceMessenger.Default.Register<AccountManagerView, AddAccountToGroupMessage>(this, (r, m) => r.AddAccountToGroup(m.Account));
            WeakReferenceMessenger.Default.Register<AccountManagerView, AddTagsMessage>(this, (r, m) => r.EditAccountTags(m.Account));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StrongReferenceMessenger.Default.UnregisterAll(this);
            WeakReferenceMessenger.Default.UnregisterAll(this);
        };
    }

    private AccountManagerViewModel? Vm => DataContext as AccountManagerViewModel;
    private static IDialogService Dialogs => App.Service<IDialogService>();

    private void ShowAddAccount_Click(object? sender, RoutedEventArgs e)
    {
        ShowAddAccountButton.IsVisible = false;
        AddAccountForm.IsVisible = true;
    }

    private void HideAddAccount_Click(object? sender, RoutedEventArgs e)
    {
        ShowAddAccountButton.IsVisible = true;
        AddAccountForm.IsVisible = false;
    }

    // A click on an account toggles its checkbox, unless it was on a button or the checkbox.
    private void Account_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        if ((sender as Control)?.DataContext is AccountItemViewModel account)
            account.ToggleSelectionCommand.Execute(null);
    }

    private static List<string> SplitTags(string? text) =>
        (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();

    private void AddTagsToSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var selected = vm.Accounts.Where(a => a.UseCheck).ToList();
        if (selected.Count == 0)
        {
            Dialogs.ShowMessageBox("No accounts selected", "Add Tags");
            return;
        }
        InputDialogViewModel input = new("Add Tags to Selected", "Enter tags (comma-separated)", "Tags", numericInputOnly: false);
        if (Dialogs.ShowDialog(input, "Add Tags") != true)
            return;
        var tags = SplitTags(input.DialogTextInput);
        foreach (var account in selected)
            foreach (string tag in tags)
                if (!account.Tags.Contains(tag))
                    account.Tags.Add(tag);
        vm.SaveAccounts();
        vm.RefreshTagFilters();
        vm.ApplyTagFilter();
    }

    private void EditAccountTags(AccountItemViewModel account)
    {
        if (Vm is not { } vm)
            return;
        InputDialogViewModel input = new("Edit Tags", "Enter tags (comma-separated)", "Tags", numericInputOnly: false)
        {
            DialogTextInput = string.Join(", ", account.Tags),
        };
        if (Dialogs.ShowDialog(input, "Edit Tags") != true)
            return;
        account.Tags.Clear();
        foreach (string tag in SplitTags(input.DialogTextInput))
            account.Tags.Add(tag);
        vm.SaveAccounts();
        vm.RefreshTagFilters();
        vm.ApplyTagFilter();
    }

    private void AddSelectedToGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var selected = vm.Accounts.Where(a => a.UseCheck).ToList();
        if (selected.Count == 0)
        {
            Dialogs.ShowMessageBox("No accounts selected", "Add to Group");
            return;
        }
        SelectGroupDialogViewModel dialog = new(vm.Groups);
        if (Dialogs.ShowDialog(dialog, "Add Selected to Group") == true && dialog.SelectedGroup is { } group)
            foreach (var account in selected)
                vm.AddAccountToGroup(account, group);
    }

    private void AddAccountToGroup(AccountItemViewModel account)
    {
        if (Vm is not { } vm)
            return;
        SelectGroupDialogViewModel dialog = new(vm.Groups);
        if (Dialogs.ShowDialog(dialog, "Add to Group") == true && dialog.SelectedGroup is { } group)
            vm.AddAccountToGroup(account, group);
    }

    private void AddGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        InputDialogViewModel input = new("Create Group", "Enter group name", "Group Name", numericInputOnly: false);
        if (Dialogs.ShowDialog(input, "Create Group") == true && !string.IsNullOrWhiteSpace(input.DialogTextInput))
            vm.AddGroup(input.DialogTextInput.Trim());
    }

    private void RemoveFromGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountItemViewModel account } button)
            return;
        if (button.FindAncestorOfType<Expander>()?.DataContext is GroupItemViewModel group)
            WeakReferenceMessenger.Default.Send(new RemoveAccountFromGroupMessage(group, account));
    }
}
