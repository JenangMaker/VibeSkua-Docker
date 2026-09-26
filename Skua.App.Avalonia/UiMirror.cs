using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;

namespace Skua.App.Avalonia;

/// <summary>
/// A UI-thread copy of a view model collection that may change on other
/// threads (WPF allowed that for the script list through
/// EnableCollectionSynchronization; Avalonia does not), optionally filtered
/// (WPF's ICollectionView.Filter). Changes are coalesced into one resync.
/// </summary>
public sealed class UiMirror<T> : IDisposable
{
    private readonly IEnumerable _source;
    private bool _queued;

    public UiMirror(IEnumerable source)
    {
        _source = source;
        if (_source is INotifyCollectionChanged changes)
            changes.CollectionChanged += OnSourceChanged;
        Resync();
    }

    public ObservableCollection<T> Items { get; } = new();

    public Func<T, bool>? Filter { get; set; }

    public void Refresh() => Queue();

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Queue();

    private void Queue()
    {
        if (_queued)
            return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Resync();
        }, DispatcherPriority.Background);
    }

    private void Resync()
    {
        List<T> snapshot;
        // The source may be mid-change on another thread; retry a copy that raced.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                snapshot = _source.OfType<T>().Where(i => Filter?.Invoke(i) ?? true).ToList();
                break;
            }
            catch (InvalidOperationException) when (attempt < 5)
            {
                Thread.Sleep(10);
            }
        }
        if (snapshot.SequenceEqual(Items))
            return;
        Items.Clear();
        foreach (T item in snapshot)
            Items.Add(item);
    }

    public void Dispose()
    {
        if (_source is INotifyCollectionChanged changes)
            changes.CollectionChanged -= OnSourceChanged;
    }
}
