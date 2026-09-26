using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

public partial class AssignHotKeyDialog : UserControl
{
    public AssignHotKeyDialog()
    {
        InitializeComponent();
    }

    private const string WaitingInputText = "Waiting input...";
    private const string CaptureHintText = "Press a non-modifier key (Esc to cancel).";
    private const string ModifierOnlyHintText = "Modifier keys cannot be used alone. Press another key.";
    private const string SaveWithoutKeyHintText = "Press a non-modifier key before saving.";

    private string _backupKey = string.Empty;
    private bool _capturing;

    private AssignHotKeyDialogViewModel? Vm => DataContext as AssignHotKeyDialogViewModel;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (vm.KeyInput == WaitingInputText)
        {
            vm.KeyInput = _backupKey;
            vm.InputHint = SaveWithoutKeyHintText;
            StopCapture();
            return;
        }
        if (IsModifier(vm.KeyInput))
        {
            vm.InputHint = ModifierOnlyHintText;
            return;
        }
        if (vm.UsedGestures.Contains(vm.KeyGesture, StringComparer.OrdinalIgnoreCase))
        {
            vm.InputHint = "This hotkey is already assigned to another action.";
            return;
        }
        vm.InputHint = string.Empty;
        StopCapture();
        HostDialog.Close(this, true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => HostDialog.Close(this, false);

    // Capture the next key pressed anywhere in the dialog.
    private void AssignKey(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top)
            return;
        StopCapture();
        top.AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        _capturing = true;
        _backupKey = vm.KeyInput;
        vm.KeyInput = WaitingInputText;
        vm.InputHint = CaptureHintText;
    }

    private void StopCapture()
    {
        if (_capturing && TopLevel.GetTopLevel(this) is { } top)
            top.RemoveHandler(KeyDownEvent, Window_KeyDown);
        _capturing = false;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            vm.KeyInput = _backupKey;
            vm.InputHint = string.Empty;
            StopCapture();
            return;
        }
        if (IsModifier(e.Key.ToString()))
        {
            vm.InputHint = ModifierOnlyHintText;
            return;
        }
        // Key names follow WPF's, which Skua stores (they match Avalonia's for
        // letters, digits, F-keys and the common named keys).
        vm.KeyInput = e.Key.ToString();
        vm.InputHint = string.Empty;
        StopCapture();
    }

    private static bool IsModifier(string key) =>
        key is "LeftCtrl" or "RightCtrl" or "LeftShift" or "RightShift" or "LeftAlt" or "RightAlt";
}
