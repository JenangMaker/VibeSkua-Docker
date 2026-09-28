using Skua.Core.Models;

namespace Skua.Linux;

/// <summary>
/// SKUA_ROOM_NUMBER (SKUA_ROOM_NUMBER_N for one tab): the private room
/// CoreBots scripts join, for every account, instead of setting it in each
/// account's CoreBots Options. CoreBots reads it from
/// options/CBO_Storage(user).txt ("PrivateRooms" and "PrivateRoomNr"), the
/// file those options save to, so this writes those two lines there when the
/// account logs in and again just before an auto-started script. The other
/// options in the file are kept; changing the room in CoreBots Options holds
/// until the account next logs in.
/// </summary>
public static class RoomNumber
{
    /// <summary>The room number to use, or null if SKUA_ROOM_NUMBER is unset or invalid.</summary>
    public static int? Value { get; } = Parse(SkuaRuntime.EnvRaw("SKUA_ROOM_NUMBER"));

    private static int? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // CoreBots keeps at most 6 digits.
        if (!int.TryParse(raw.Trim(), out int room) || room < 1 || room > 999_999)
        {
            Console.Error.WriteLine($"[host] SKUA_ROOM_NUMBER={raw} is not a room number (1-999999); ignored");
            return null;
        }
        if (room < 1000)
            Console.Error.WriteLine($"[host] SKUA_ROOM_NUMBER={room}: CoreBots treats rooms under 1000 like public rooms for some maps");
        return room;
    }

    /// <summary>Writes the room into <paramref name="username"/>'s CoreBots options.</summary>
    public static void Apply(string username)
    {
        if (Value is not { } room || string.IsNullOrWhiteSpace(username))
            return;
        string file = Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({username}).txt");
        try
        {
            List<string> lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new();
            bool changed = Set(lines, "PrivateRooms", "True") | Set(lines, "PrivateRoomNr", room.ToString());
            if (!changed)
                return;
            Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
            File.WriteAllLines(file, lines);
            Console.WriteLine($"[host] CoreBots room for {username}: {room} (SKUA_ROOM_NUMBER)");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[host] could not set the CoreBots room for {username}: {e.Message}");
        }
    }

    // "Key: value" lines, as CoreBots Options writes them.
    private static bool Set(List<string> lines, string key, string value)
    {
        string line = $"{key}: {value}";
        int i = lines.FindIndex(l => l.Split(':')[0].Trim() == key);
        if (i < 0)
        {
            lines.Add(line);
            return true;
        }
        if (lines[i] == line)
            return false;
        lines[i] = line;
        return true;
    }
}
