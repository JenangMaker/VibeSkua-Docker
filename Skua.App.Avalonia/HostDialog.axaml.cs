using Avalonia.Controls;
using Avalonia.Input;

namespace Skua.App.Avalonia;

/// <summary>
/// A modal dialog around one view model (Skua.WPF's HostDialog). Result is
/// WPF's DialogResult: views close it with <see cref="Close(Control, bool?)"/>;
/// Escape (WPF's IsCancel) closes it with no result.
/// </summary>
public partial class HostDialog : Window
{
    public HostDialog()
    {
        InitializeComponent();
        WindowPlacement.Centred(this);
        TabChildWindows.Attach(this);
    }

    public bool? Result { get; private set; }

    /// <summary>Closes it with a result, as its own buttons do (a message box answered through the control API).</summary>
    public void CloseWith(bool? result)
    {
        Result = result;
        Close();
    }

    /// <summary>Closes the dialog containing <paramref name="control"/> with a result.</summary>
    public static void Close(Control control, bool? result)
    {
        if (TopLevel.GetTopLevel(control) is HostDialog dialog)
        {
            dialog.Result = result;
            dialog.Close();
        }
        else if (TopLevel.GetTopLevel(control) is Window window)
        {
            window.Close();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
