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
    }
}
