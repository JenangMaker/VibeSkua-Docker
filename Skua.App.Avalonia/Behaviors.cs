using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Skua.App.Avalonia;

/// <summary>
/// Skua.WPF/Behaviours as attached properties:
/// <c>b:Behave.OnlyNumbers="True"</c> for &lt;wpf:TextBoxOnlyNumbersBehavior /&gt;, and so on.
/// (ScrollParentBehavior has no counterpart: Avalonia already hands wheel
/// input a nested control does not use to the scroll viewer around it.)
/// </summary>
public static class Behave
{
    public static readonly AttachedProperty<bool> OnlyNumbersProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("OnlyNumbers", typeof(Behave));
    public static readonly AttachedProperty<bool> OnlyFloatingPointProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("OnlyFloatingPoint", typeof(Behave));
    public static readonly AttachedProperty<bool> SelectAllProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("SelectAll", typeof(Behave));
    public static readonly AttachedProperty<bool> CopySelectedProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("CopySelected", typeof(Behave));
    public static readonly AttachedProperty<bool> UnselectAllMenuProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("UnselectAllMenu", typeof(Behave));
    public static readonly AttachedProperty<bool> ScrollToEndProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("ScrollToEnd", typeof(Behave));
    public static readonly AttachedProperty<bool> ScrollToSelectedProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("ScrollToSelected", typeof(Behave));

    public static bool GetOnlyNumbers(TextBox t) => t.GetValue(OnlyNumbersProperty);
    public static void SetOnlyNumbers(TextBox t, bool v) => t.SetValue(OnlyNumbersProperty, v);
    public static bool GetOnlyFloatingPoint(TextBox t) => t.GetValue(OnlyFloatingPointProperty);
    public static void SetOnlyFloatingPoint(TextBox t, bool v) => t.SetValue(OnlyFloatingPointProperty, v);
    public static bool GetSelectAll(TextBox t) => t.GetValue(SelectAllProperty);
    public static void SetSelectAll(TextBox t, bool v) => t.SetValue(SelectAllProperty, v);
    public static bool GetCopySelected(ListBox l) => l.GetValue(CopySelectedProperty);
    public static void SetCopySelected(ListBox l, bool v) => l.SetValue(CopySelectedProperty, v);
    public static bool GetUnselectAllMenu(ListBox l) => l.GetValue(UnselectAllMenuProperty);
    public static void SetUnselectAllMenu(ListBox l, bool v) => l.SetValue(UnselectAllMenuProperty, v);
    public static bool GetScrollToEnd(ListBox l) => l.GetValue(ScrollToEndProperty);
    public static void SetScrollToEnd(ListBox l, bool v) => l.SetValue(ScrollToEndProperty, v);
    public static bool GetScrollToSelected(ListBox l) => l.GetValue(ScrollToSelectedProperty);
    public static void SetScrollToSelected(ListBox l, bool v) => l.SetValue(ScrollToSelectedProperty, v);

    static Behave()
    {
        OnlyNumbersProperty.Changed.AddClassHandler<TextBox>((t, e) =>
        {
            if (e.NewValue is true)
                t.AddHandler(InputElement.TextInputEvent, (_, a) => Filter(a, c => char.IsDigit(c)), RoutingStrategies.Tunnel);
        });
        OnlyFloatingPointProperty.Changed.AddClassHandler<TextBox>((t, e) =>
        {
            if (e.NewValue is true)
                t.AddHandler(InputElement.TextInputEvent, (_, a) => Filter(a, c => char.IsDigit(c) || c == '.'), RoutingStrategies.Tunnel);
        });
        SelectAllProperty.Changed.AddClassHandler<TextBox>((t, e) =>
        {
            if (e.NewValue is not true)
                return;
            t.GotFocus += (_, _) => Dispatcher.UIThread.Post(t.SelectAll);
            t.DoubleTapped += (_, _) => t.SelectAll();
        });
        CopySelectedProperty.Changed.AddClassHandler<ListBox>((l, e) =>
        {
            if (e.NewValue is true)
                l.KeyDown += (_, a) =>
                {
                    if (a.Key == Key.C && a.KeyModifiers.HasFlag(KeyModifiers.Control))
                        CopySelected(l);
                };
        });
        UnselectAllMenuProperty.Changed.AddClassHandler<ListBox>((l, e) =>
        {
            if (e.NewValue is not true)
                return;
            l.ContextMenu ??= new ContextMenu();
            l.ContextMenu.Items.Add(new MenuItem { Header = "Unselect All", Command = new RelayCommand(() => l.UnselectAll()) });
        });
        ScrollToEndProperty.Changed.AddClassHandler<ListBox>((l, e) =>
        {
            if (e.NewValue is true)
                FollowEnd(l);
        });
        ScrollToSelectedProperty.Changed.AddClassHandler<ListBox>((l, e) =>
        {
            if (e.NewValue is true)
                l.SelectionChanged += (_, _) =>
                {
                    if (l.SelectedItem is { } item)
                        Dispatcher.UIThread.Post(() => l.ScrollIntoView(item));
                };
        });
    }

    private static void Filter(TextInputEventArgs a, Func<char, bool> allowed)
    {
        if (a.Text is { } text && !text.All(allowed))
            a.Handled = true;
    }

    private static void CopySelected(ListBox l)
    {
        if (l.SelectedItems is null || TopLevel.GetTopLevel(l)?.Clipboard is not { } clipboard)
            return;
        StringBuilder text = new();
        foreach (object? item in l.SelectedItems)
            text.AppendLine(item?.ToString());
        _ = clipboard.SetTextAsync(text.ToString());
    }

    /// <summary>
    /// ListBoxScrollToCaretBehavior: keep the newest line in view while the
    /// list is scrolled to the bottom; stop following once the user scrolls up.
    /// </summary>
    private static void FollowEnd(ListBox l)
    {
        bool follow = true;
        INotifyCollectionChanged? watched = null;
        void Added(object? s, NotifyCollectionChangedEventArgs a)
        {
            if (!follow || l.ItemCount == 0)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (l.ItemCount > 0)
                    l.ScrollIntoView(l.ItemCount - 1);
            }, DispatcherPriority.Background);
        }
        void Watch()
        {
            if (watched is not null)
                watched.CollectionChanged -= Added;
            watched = l.ItemsSource as INotifyCollectionChanged;
            if (watched is not null)
                watched.CollectionChanged += Added;
            Added(null, null!);
        }
        l.PropertyChanged += (_, e) =>
        {
            if (e.Property == ItemsControl.ItemsSourceProperty)
                Watch();
        };
        l.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer sv && e.OffsetDelta.Y != 0)
                follow = sv.Offset.Y >= sv.ScrollBarMaximum.Y - 1;
        });
        Watch();
    }
}
