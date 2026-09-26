using System.Collections.Concurrent;
using System.Globalization;

namespace Skua.Ruffle;

/// <summary>
/// The call half of Skua's IFlashUtil, over a <see cref="RuffleBridge"/>.
/// Mirrors Skua.WPF's FlashUtil: results come back as strings and are
/// converted to the requested type, failures give the type's default, and
/// getGameObject/getGameObjectS results are reused for 15 ms.
/// (Phase 1 makes this implement IFlashUtil once Skua.Core targets net10.0.)
/// </summary>
public sealed class RuffleFlash(RuffleBridge bridge)
{
    private readonly ConcurrentDictionary<string, (long Ticks, string? Value)> _cache = new();
    private static readonly long CacheTicks = TimeSpan.FromMilliseconds(15).Ticks;

    public RuffleBridge Bridge { get; } = bridge;

    public string? Call(string function, params object?[] args) => Call<string>(function, args);

    public T? Call<T>(string function, params object?[] args)
    {
        try
        {
            return Call(function, typeof(T), args) is { } o ? (T)o : default;
        }
        catch
        {
            return default;
        }
    }

    public object? Call(string function, Type type, params object?[] args)
    {
        string? result;
        bool cacheable = function is "getGameObject" or "getGameObjectS";
        string key = cacheable ? function + "\u0001" + string.Join('\u0001', args) : "";
        if (cacheable && _cache.TryGetValue(key, out var hit) && DateTime.UtcNow.Ticks - hit.Ticks < CacheTicks)
        {
            result = hit.Value;
        }
        else
        {
            var raw = Bridge.Invoke(function, args);
            result = raw is { } r ? FlashValue.ResultString(r) : null;
            if (cacheable)
            {
                if (_cache.Count > 500)
                    _cache.Clear();
                _cache[key] = (DateTime.UtcNow.Ticks, result);
            }
        }

        if (result is null)
            return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target == typeof(string) ? result : Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
