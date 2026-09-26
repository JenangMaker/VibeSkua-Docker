using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.App.Avalonia;

/// <summary>
/// View model -> view, as Skua.WPF/XAML/DataTemplates.xaml maps them. A view
/// model without an entry of its own takes its nearest base class's view
/// (every BotControlViewModelBase falls back to AboutView there). Skua view
/// models with no view at all get a placeholder that names them, so a gap in
/// the port is visible instead of a bare type name.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new();

    public static void Register<TViewModel, TView>() where TView : Control, new() =>
        Views[typeof(TViewModel)] = () => new TView();

    public static IReadOnlyCollection<Type> RegisteredViewModels => Views.Keys;

    public bool Match(object? data) =>
        data is not null && data.GetType().Namespace?.StartsWith("Skua.Core.ViewModels", StringComparison.Ordinal) == true;

    public Control Build(object? data)
    {
        for (Type? t = data!.GetType(); t is not null; t = t.BaseType)
        {
            if (Views.TryGetValue(t, out var create))
                return create();
        }
        return new TextBlock
        {
            Text = $"Not ported yet: {data.GetType().Name}",
            Margin = new(12),
            Foreground = Brushes.OrangeRed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
