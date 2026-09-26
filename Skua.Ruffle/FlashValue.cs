using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Text.Json;

namespace Skua.Ruffle;

/// <summary>
/// Converts between .NET values and the JSON the bridge page exchanges with
/// Ruffle, following the same rules as the ActiveX bridge's
/// <c>ToFlashXml</c> / <c>FromFlashXml</c> (Skua.WPF/Flash/FlashUtil.cs), so
/// Skua.Core sees the same types either way.
/// </summary>
public static class FlashValue
{
    /// <summary>Writes an argument for a call into the SWF.</summary>
    public static void Write(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                w.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                w.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case IDictionary<string, object?> dict: // ExpandoObject
                w.WriteStartObject();
                foreach (var (key, v) in dict)
                {
                    w.WritePropertyName(key);
                    Write(w, v);
                }
                w.WriteEndObject();
                break;
            case IEnumerable list:
                w.WriteStartArray();
                foreach (var item in list)
                    Write(w, item);
                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// An event argument from the SWF: numbers become <see cref="int"/> when
    /// integral and <see cref="float"/> otherwise, objects become
    /// <see cref="ExpandoObject"/>, arrays <c>object?[]</c>, as FromFlashXml does.
    /// </summary>
    public static object? Read(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out int i) ? i : (object)e.GetSingle(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Read).ToArray(),
        JsonValueKind.Object => ReadObject(e),
        _ => null,
    };

    private static ExpandoObject ReadObject(JsonElement e)
    {
        var obj = new ExpandoObject();
        var dict = (IDictionary<string, object?>)obj;
        foreach (var p in e.EnumerateObject())
            dict[p.Name] = Read(p.Value);
        return obj;
    }

    /// <summary>
    /// A call's return value as the string Skua converts from. The SWF's
    /// callbacks mostly return strings (often JSON) already; anything else is
    /// rendered the way AS3 would print it.
    /// </summary>
    public static string? ResultString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };
}
