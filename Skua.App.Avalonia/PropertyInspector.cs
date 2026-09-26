using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Skua.App.Avalonia;

/// <summary>
/// Stands in for Skua.WPF's PropertyGrid where the grabber uses it: the
/// selected object's public properties, name and value, read-only and
/// selectable for copying. Collections show their items, one per line.
/// </summary>
public sealed class PropertyInspector : UserControl
{
    public static readonly StyledProperty<object?> SelectedObjectProperty =
        AvaloniaProperty.Register<PropertyInspector, object?>(nameof(SelectedObject));

    private readonly Grid _grid = new() { ColumnDefinitions = new("Auto,*"), Margin = new(4) };

    public PropertyInspector()
    {
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _grid,
        };
        BorderBrush = Brushes.Gray;
    }

    public object? SelectedObject
    {
        get => GetValue(SelectedObjectProperty);
        set => SetValue(SelectedObjectProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedObjectProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        if (SelectedObject is not { } obj)
            return;

        var props = obj.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .OrderBy(p => p.GetCustomAttribute<CategoryAttribute>()?.Category ?? "")
            .ThenBy(p => p.Name);

        int row = 0;
        foreach (PropertyInfo p in props)
        {
            string name = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name;
            string value;
            try { value = Format(p.GetValue(obj)); }
            catch (Exception e) { value = $"<{e.GetBaseException().Message}>"; }

            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Text = name,
                FontWeight = FontWeight.SemiBold,
                Margin = new(0, 2, 12, 2),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var text = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2) };
            if (p.GetCustomAttribute<DescriptionAttribute>()?.Description is { Length: > 0 } tip)
                ToolTip.SetTip(label, tip);
            Grid.SetRow(label, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            _grid.Children.Add(label);
            _grid.Children.Add(text);
            row++;
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => string.Join("\n", e.Cast<object?>().Select(i => i?.ToString() ?? "")),
        _ => value.ToString() ?? "",
    };
}
