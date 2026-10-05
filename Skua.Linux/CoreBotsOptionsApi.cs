using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.ViewModels;

namespace Skua.Linux;

/// <summary>
/// CoreBots Options (the Options > CoreBots window) for a remote page: the
/// logged-in account's options/CBO_Storage(user).txt, which CoreBots reads
/// each time a script starts, as "Key: Value" lines.
///
///   GET  /cbo    the values, the option definitions and the account's choices
///   POST /cbo    body {"values":{"Key":"Value",...}}: merged into the file
///
/// The definitions come from Skua's own window (CoreBotsViewModel's Options
/// and Other tabs: label, key, description, type, default), so a page shows
/// what the window shows. The Loadout tab's keys are fixed (SoloClassSelect,
/// SoloModeSelect, SoloEquipCheck, Helm1Select... as CBOClassSelectViewModel
/// and CBOClassEquipmentViewModel write them); its choices are the account's:
/// enhanced classes with their modes (the Advanced Skills), and its items per
/// equipment slot.
/// </summary>
public sealed partial class HostApi
{
    // As CBOClassSelectViewModel writes "use the class I wear".
    private const string CboCurrentClass = "[Current]";

    private object CoreBotsOptions()
    {
        var player = services.GetRequiredService<IScriptPlayer>();
        if (!services.GetRequiredService<Skua.Ruffle.RuffleBridge>().IsConnected || !player.LoggedIn || string.IsNullOrEmpty(player.Username))
            return new { error = "log in first: the options are saved per account" };

        string file = CboFile(player.Username);
        var values = File.Exists(file) ? CboRead(File.ReadAllLines(file)) : new Dictionary<string, string>();

        var vm = services.GetRequiredService<CoreBotsViewModel>();
        var options = new List<object>();
        foreach (var tab in vm.CoreBotsTabs)
        {
            if (tab.Content is CBOptionsViewModel main)
            {
                var defaults = typeof(CBOptionsViewModel).GetProperty("DefaultValues", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main) as Dictionary<string, object>;
                foreach (var o in main.Options)
                    options.Add(CboOption("Options", "Options", o, defaults?.GetValueOrDefault(o.Tag)));
            }
            else if (tab.Content is CBOOtherOptionsViewModel other)
            {
                foreach (var group in other.Options)
                    foreach (var o in group.Items)
                        options.Add(CboOption("Other", group.Category, o, other.DefaultValues.GetValueOrDefault(o.Tag)));
            }
        }

        // The choices, as the Loadout tab lists them.
        var items = services.GetRequiredService<IScriptInventory>().Items ?? new();
        List<string> Names(Func<InventoryItem, bool> take) =>
            items.Where(take).Select(i => i.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var classes = Names(i => i.Category == ItemCategory.Class && i.EnhancementLevel > 0);
        var skills = services.GetRequiredService<IAdvancedSkillContainer>().LoadedSkills;
        var modes = classes.Append(player.CurrentClass?.Name ?? string.Empty).Where(c => c.Length > 0).Distinct()
            .ToDictionary(c => c, c =>
            {
                var m = skills.Where(s => s.ClassName == c).Select(s => s.ClassUseMode.ToString()).Distinct().ToList();
                return m.Count > 0 ? m : new List<string> { "Base" };
            });

        return new
        {
            user = player.Username,
            file,
            exists = File.Exists(file),
            currentClass = player.CurrentClass?.Name,
            currentClassOption = CboCurrentClass,
            values,
            options,
            choices = new
            {
                classes,
                modes,
                // A class item's quantity is its class points (rank 10 at 302,500).
                classPoints = items.Where(i => i.Category == ItemCategory.Class && i.EnhancementLevel > 0)
                    .GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.Max(i => i.Quantity)),
                helm = Names(i => i.Category == ItemCategory.Helm && i.EnhancementLevel > 0),
                armor = Names(i => i.Category == ItemCategory.Armor),
                cape = Names(i => i.Category == ItemCategory.Cape && i.EnhancementLevel > 0),
                weapon = Names(i => i.ItemGroup == "Weapon" && i.EnhancementLevel > 0),
                pet = Names(i => i.Category == ItemCategory.Pet),
                groundItem = Names(i => i.Category == ItemCategory.Misc),
            },
        };
    }

    private async Task<object> SaveCoreBotsOptions(HttpListenerRequest request)
    {
        var player = services.GetRequiredService<IScriptPlayer>();
        if (!player.LoggedIn || string.IsNullOrEmpty(player.Username))
            return new { error = "log in first: the options are saved per account" };

        CboInput? input;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            try { input = JsonSerializer.Deserialize<CboInput>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException e) { return new { error = $"bad JSON: {e.Message}" }; }
        }
        if (input?.Values is not { Count: > 0 } changes)
            return new { error = "give {\"values\":{\"Key\":\"Value\",...}}" };
        // One value per line: no line breaks or ':' in a key.
        if (changes.Keys.FirstOrDefault(k => string.IsNullOrWhiteSpace(k) || k.IndexOfAny([':', '\n', '\r']) >= 0) is { } badKey)
            return new { error = $"bad key \"{badKey}\"" };

        string file = CboFile(player.Username);
        // Keep the file's order and the lines this page does not know; change
        // or add the ones given.
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        var pending = new Dictionary<string, string>(changes);
        for (int i = 0; i < lines.Count; i++)
        {
            int sep = lines[i].IndexOf(':');
            if (sep <= 0)
                continue;
            string key = lines[i][..sep].Trim();
            if (pending.Remove(key, out string? value))
                lines[i] = $"{key}: {Clean(value)}";
        }
        lines.AddRange(pending.Select(kv => $"{kv.Key}: {Clean(kv.Value)}"));
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        await File.WriteAllLinesAsync(file, lines);

        // The window keeps what it read per account and saves it back when it
        // closes; forget it so the window reads this file next time.
        var cache = typeof(CoreBotsViewModel).GetField("_readValues", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(services.GetRequiredService<CoreBotsViewModel>()) as Dictionary<string, Dictionary<string, string>>;
        cache?.Remove(player.Username);

        return new { saved = changes.Count, file };

        static string Clean(string? v) => (v ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string CboFile(string user) => Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({user}).txt");

    // As CoreBotsViewModel.ReadValues: "Key: Value", split on the first ':'.
    private static Dictionary<string, string> CboRead(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>();
        foreach (string line in lines)
        {
            int sep = line.IndexOf(':');
            if (sep > 0)
                values[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }
        return values;
    }

    private static object CboOption(string tab, string group, DisplayOptionItemViewModelBase o, object? def) => new
    {
        tab,
        group,
        key = o.Tag,
        label = o.Content,
        description = o.Description == o.Content ? null : o.Description,
        type = o.DisplayType == typeof(bool) ? "bool" : o.DisplayType == typeof(int) ? "int" : "text",
        @default = def?.ToString(),
    };

    private sealed class CboInput
    {
        public Dictionary<string, string>? Values { get; set; }
    }
}
