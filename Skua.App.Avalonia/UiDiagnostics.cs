using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;

namespace Skua.App.Avalonia;

/// <summary>
/// Avalonia's own warnings and errors in the container log, as [ui] lines, and a check that each
/// window still renders. LogToTrace sent them to .NET's Trace, which nothing listens to: a tab
/// whose window stopped painting (black, its UI thread still answering) left no trace at all.
/// Binding warnings are left out (many, and harmless); each message template is logged 5 times,
/// then every 100th time.
/// </summary>
internal sealed class UiLogSink : ILogSink
{
    private readonly ConcurrentDictionary<string, int> _seen = new();

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Error || (level == LogEventLevel.Warning && area != LogArea.Binding);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, Array.Empty<object?>());

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
            return;
        int n = _seen.AddOrUpdate(area + "|" + messageTemplate, 1, (_, c) => c + 1);
        if (n > 5 && n % 100 != 0)
            return;
        Console.Error.WriteLine($"[ui] {level} {area}: {Format(messageTemplate, propertyValues)}" +
            (source is null ? "" : $" ({source.GetType().Name})") + (n > 5 ? $" (seen {n} times)" : ""));
    }

    // "{Name}" placeholders, filled in order.
    private static string Format(string template, object?[] values)
    {
        var sb = new StringBuilder();
        int next = 0;
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '{' && template.IndexOf('}', i) is int end and > 0)
            {
                sb.Append(next < values.Length ? values[next++]?.ToString() : template[i..(end + 1)]);
                i = end;
            }
            else
                sb.Append(template[i]);
        }
        return sb.ToString();
    }
}

internal static class RenderWatch
{
    /// <summary>
    /// Every 30 s, asks the window for an animation frame (called back by the render loop) and
    /// logs when none comes within 10 s, and when frames come again: whether a black window has a
    /// stuck renderer, and since when. The graphics in use is logged once at the first frame.
    /// </summary>
    public static void Watch(TopLevel window, string name)
    {
        bool stalled = false, first = true;
        DateTime since = default;
        DispatcherTimer.Run(() =>
        {
            DateTime asked = DateTime.UtcNow;
            bool answered = false;
            window.RequestAnimationFrame(_ =>
            {
                answered = true;
                if (first)
                {
                    first = false;
                    Console.WriteLine($"[ui] {name}: rendering with {Graphics()}");
                }
                if (stalled)
                {
                    stalled = false;
                    Console.Error.WriteLine($"[ui] {name}: rendering again after {(DateTime.UtcNow - since).TotalSeconds:0} s");
                }
            });
            DispatcherTimer.RunOnce(() =>
            {
                if (answered || stalled)
                    return;
                stalled = true;
                since = asked;
                Console.Error.WriteLine($"[ui] {name}: no frame rendered for 10 s (window {(window.IsVisible ? "visible" : "hidden")}, {window.Bounds.Width:0}x{window.Bounds.Height:0})");
            }, TimeSpan.FromSeconds(10));
            return true;
        }, TimeSpan.FromSeconds(30));
    }

    private static string Graphics()
    {
        try
        {
            object? locator = typeof(AvaloniaLocator).GetProperty("Current")?.GetValue(null);
            Type? type = Type.GetType("Avalonia.Platform.IPlatformGraphics, Avalonia.Base");
            object? graphics = type is null ? null
                : locator?.GetType().GetMethod("GetService", new[] { typeof(Type) })?.Invoke(locator, new object[] { type });
            return graphics?.GetType().Name ?? "software";
        }
        catch (Exception e)
        {
            return "unknown (" + e.Message + ")";
        }
    }
}
