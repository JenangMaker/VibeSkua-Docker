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

    // Only view models (and types with a view registered for them or a base). Plain
    // items that live in the same namespace, like RegisteredQuestInfo in the
    // Runtime window's quest list, must fall through to Avalonia's default
    // template, which shows ToString() ("1234 (Reward: 5678)").
    public bool Match(object? data)
    {
        if (data is null || data.GetType().Namespace?.StartsWith("Skua.Core.ViewModels", StringComparison.Ordinal) != true)
            return false;
        for (Type? t = data.GetType(); t is not null; t = t.BaseType)
        {
            if (Views.ContainsKey(t))
                return true;
        }
        return data.GetType().Name.EndsWith("ViewModel", StringComparison.Ordinal);
    }

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
