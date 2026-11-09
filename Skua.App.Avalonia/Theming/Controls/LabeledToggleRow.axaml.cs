using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

// From upstream VibeSkua 2.1.2 (Skua.Shared.Avalonia/Controls/Settings/LabeledToggleRow).
namespace Skua.App.Avalonia.Theming;

public partial class LabeledToggleRow : UserControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<LabeledToggleRow, string?>(nameof(Label));

    public static readonly StyledProperty<bool?> IsCheckedProperty =
        AvaloniaProperty.Register<LabeledToggleRow, bool?>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public LabeledToggleRow()
    {
        InitializeComponent();
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool? IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }
}
