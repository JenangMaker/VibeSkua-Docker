using Avalonia;
using Avalonia.Controls;
using System.Collections;

// From upstream VibeSkua 2.1.2 (Skua.Shared.Avalonia/Controls/Settings/OptionSectionControl).
namespace Skua.App.Avalonia.Theming;

public partial class OptionSectionControl : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<OptionSectionControl, string?>(nameof(Title));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<OptionSectionControl, IEnumerable?>(nameof(ItemsSource));

    public OptionSectionControl()
    {
        InitializeComponent();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
}
