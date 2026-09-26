using Avalonia.Threading;

namespace Skua.App.Avalonia.Services;

/// <summary>
/// Skua.Core's services are synchronous (a script thread calls
/// ShowMessageBox and waits for the answer; a command on the UI thread
/// calls ShowDialog and reads the result), while Avalonia's dialogs are
/// async. This bridges the two: from another thread it waits on the UI
/// thread's result; on the UI thread it pumps a nested loop until the
/// operation completes, which is what WPF's ShowDialog does too.
/// </summary>
public static class UiThread
{
    public static T Run<T>(Func<Task<T>> operation)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(operation).GetAwaiter().GetResult();

        Task<T> task = operation();
        if (!task.IsCompleted)
        {
            using CancellationTokenSource done = new();
            task.ContinueWith(_ => done.Cancel(), TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.UIThread.MainLoop(done.Token);
        }
        return task.GetAwaiter().GetResult();
    }

    public static void Run(Func<Task> operation) => Run(async () =>
    {
        await operation();
        return true;
    });

    public static T Invoke<T>(Func<T> function) =>
        Dispatcher.UIThread.CheckAccess() ? function() : Dispatcher.UIThread.Invoke(function);

    public static void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Invoke(action);
    }
}
