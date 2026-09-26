using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Material.Icons;

namespace Skua.App.Avalonia.Views;

public partial class StatItemUserControl : UserControl
{
    public StatItemUserControl()
    {
        InitializeComponent();
    }

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StatItemUserControl, string>(nameof(Label), string.Empty);
    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<StatItemUserControl, MaterialIconKind>(nameof(Icon));
    public static readonly StyledProperty<bool> HasIconProperty =
        AvaloniaProperty.Register<StatItemUserControl, bool>(nameof(HasIcon));
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<StatItemUserControl, string?>(nameof(Value), "0");

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Leave unset for no icon (WPF's PackIconKind.None).</summary>
    public MaterialIconKind Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public bool HasIcon { get => GetValue(HasIconProperty); private set => SetValue(HasIconProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
            HasIcon = true;
    }

    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}
