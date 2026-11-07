using Avalonia;
using Avalonia.Controls;

namespace Skua.App.Avalonia.Views;

public partial class OptionsPageView : UserControl
{
    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<OptionsPageView, string?>(nameof(Watermark), "Search options");

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public OptionsPageView() => InitializeComponent();
}
