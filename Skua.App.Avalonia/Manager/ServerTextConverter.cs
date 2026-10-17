using System.Globalization;
using Avalonia.Data.Converters;

namespace Skua.App.Avalonia.Manager;

/// <summary>
/// A login server as the WPF Manager lists it: "Twilly [M] - 42ms - 310/1000"
/// (from Name, Upgrade, Online, Ping, PlayerCount, MaxPlayers).
/// </summary>
public sealed class ServerTextConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 6)
            return null;
        string name = values[0] as string ?? "";
        bool member = values[1] is true;
        bool online = values[2] is true;
        int ping = values[3] is int p ? p : -1;
        string pingText = !online ? "Offline" : ping switch { -1 => "...", 9999 => "timeout", _ => $"{ping}ms" };
        string players = online ? $" - {values[4]}/{values[5]}" : "";
        return $"{name}{(member ? " [M]" : "")} - {pingText}{players}";
    }
}
