using System.Collections.Concurrent;
using System.Dynamic;
using System.Globalization;
using System.Xml.Linq;
using Skua.Core.Flash;
using Skua.Core.Interfaces;

namespace Skua.Ruffle;

/// <summary>
/// <see cref="IFlashUtil"/> over a <see cref="RuffleBridge"/>: Skua.Core's view
/// of the game when skua.swf runs in Ruffle instead of the Flash ActiveX
/// control. Mirrors Skua.WPF's FlashUtil: results come back as strings and are
/// converted to the requested type, failures give the type's default, and
/// getGameObject/getGameObjectS results are reused for 15 ms.
/// </summary>
public sealed class RuffleFlashUtil : IFlashUtil
{
    private readonly ConcurrentDictionary<string, (long Ticks, string? Value)> _cache = new();
    private static readonly long CacheTicks = TimeSpan.FromMilliseconds(15).Ticks;
    private readonly Lazy<IScriptManager>? _manager;

    public RuffleFlashUtil(RuffleBridge bridge, Lazy<IScriptManager>? manager = null)
    {
        Bridge = bridge;
        _manager = manager;
        Bridge.FlashCall += (name, args) => FlashCall?.Invoke(name, AsFlashCallArgs(args));
    }

    /// <summary>
    /// The SWF's calls as Skua.WPF's FlashUtil hands them to Skua.Core.
    /// skua.swf's Externalizer passes its arguments as one array
    /// (<c>ExternalInterface.call(name, rest)</c>), and WPF reads the request's
    /// whole &lt;arguments&gt; element with <c>FromFlashXml</c>, which falls
    /// through to the element's text. So Skua.Core always gets exactly one
    /// argument: the text of every string and number in it, concatenated (for
    /// the usual single string, that string).
    /// </summary>
    public static object[] AsFlashCallArgs(object?[] args)
    {
        var text = new System.Text.StringBuilder();
        void Append(object? v)
        {
            switch (v)
            {
                case string str: text.Append(FlashValue.AsFlashJson(str)); break;
                case bool or null: break;   // <true/>, <false/>, <null/> have no text
                case IDictionary<string, object?> obj: foreach (var x in obj.Values) Append(x); break;
                case object?[] arr: foreach (var x in arr) Append(x); break;
                case IFormattable f: text.Append(f.ToString(null, CultureInfo.InvariantCulture)); break;
                default: text.Append(v); break;
            }
        }
        foreach (var a in args)
            Append(a);
        return [text.ToString()];
    }

    public RuffleBridge Bridge { get; }

    public event FlashCallHandler? FlashCall;

    /// <summary>
    /// The page owns the Ruffle player and loads skua.swf itself, so there is
    /// nothing to create here.
    /// </summary>
    public void InitializeFlash()
    {
    }

    public string? Call(string function, params object[] args) => Call<string>(function, args);

    public T? Call<T>(string function, params object[] args)
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

    public object? Call(string function, Type type, params object[] args)
    {
        // As in FlashUtil: a script being stopped should not keep calling in.
        if (_manager?.Value.ShouldExit == true && Thread.CurrentThread.Name == "Script Thread")
            _manager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

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

    /// <summary>Same conversion as FlashUtil, for callers that still hold Flash XML.</summary>
    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, CultureInfo.InvariantCulture, out float f) ? f : 0;
            case "true":
                return true;
            case "false":
                return false;
            case "null":
                return null!;
            case "array":
                return el.Elements().Select(FromFlashXml).ToArray();
            case "object":
                var obj = new ExpandoObject();
                var dict = (IDictionary<string, object?>)obj;
                foreach (var e in el.Elements())
                    dict[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First());
                return obj;
            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
    }
}
