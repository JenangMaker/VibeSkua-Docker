using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// The loaded script's options, as the Script Loader's Options button shows
/// them (ScriptLoaderViewModel.EditScriptConfig): the script is compiled, its
/// declared options (Options, and the MultiOptions groups such as CoreBots')
/// read with their saved values from options/&lt;storage&gt;.cfg.
///
///   GET  /script/options      the options and their values; while a script
///                             runs, the running one's, read-only
///   POST /script/options      body {"values":[{"category","name","value"}]};
///                             saved to the script's options file (not while
///                             it runs, as the Options button refuses too)
///
/// Each option: category ("Options", or the group's field name), group (the
/// heading shown), name, displayName, description, type (bool, enum, int,
/// number or text), values (an enum's choices), value and default, as text.
/// </summary>
public sealed partial class HostApi
{
    private readonly SemaphoreSlim _optionsGate = new(1, 1);

    private async Task<object> ScriptOptions()
    {
        var manager = services.GetRequiredService<IScriptManager>();
        if (string.IsNullOrEmpty(manager.LoadedScript))
            return new { error = "no script loaded: load one first" };
        if (manager.ScriptRunning)
            return manager.Config is { } running
                ? Describe(manager.LoadedScript, running, editable: false)
                : new { error = "the script is running and its options are not available" };
        if (await LoadOptionsAsync(manager) is { } error)
            return new { error };
        return Describe(manager.LoadedScript, manager.Config!, editable: true);
    }

    private async Task<object> SaveScriptOptions(HttpListenerRequest request)
    {
        var manager = services.GetRequiredService<IScriptManager>();
        if (string.IsNullOrEmpty(manager.LoadedScript))
            return new { error = "no script loaded: load one first" };
        if (manager.ScriptRunning)
            return new { error = "the script is running: stop it to change its options" };

        OptionValuesInput? input;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            try { input = JsonSerializer.Deserialize<OptionValuesInput>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException e) { return new { error = $"bad JSON: {e.Message}" }; }
        }
        if (input?.Values is not { Count: > 0 } values)
            return new { error = "give {\"values\":[{\"category\",\"name\",\"value\"}]}" };

        if (await LoadOptionsAsync(manager) is { } loadError)
            return new { error = loadError };
        var config = manager.Config!;
        var problems = new List<string>();
        int saved = 0;
        foreach (var v in values)
        {
            IOption? option = (v.Category is null or "Options" ? config.Options : config.MultipleOptions.GetValueOrDefault(v.Category ?? ""))
                ?.Find(o => o.Name == v.Name);
            if (option is null)
            {
                problems.Add($"{v.Category}/{v.Name}: no such option");
                continue;
            }
            if (Normalize(option, v.Value ?? "") is not { } text)
            {
                problems.Add(option.Type.IsEnum
                    ? $"{option.DisplayName}: \"{v.Value}\" is not one of {string.Join(", ", Enum.GetNames(option.Type).Select(n => n.Replace('_', ' ')))}"
                    : $"{option.DisplayName}: \"{v.Value}\" is not a valid {(Kind(option.Type) == "int" ? "whole number" : Kind(option.Type))}");
                continue;
            }
            config.OptionValues[option] = text;
            saved++;
        }
        // One write for all of them; transient options live in memory only,
        // as with the Options window.
        if (saved > 0)
            config.Save();
        Console.WriteLine($"[host] script options: {saved} saved to {config.OptionsFile}" + (problems.Count > 0 ? $", {problems.Count} refused" : ""));
        return problems.Count == 0
            ? new { saved, file = config.OptionsFile }
            : new { saved, file = config.OptionsFile, error = string.Join("; ", problems) };
    }

    private sealed record OptionValuesInput(List<OptionValueInput>? Values);
    private sealed record OptionValueInput(string? Category, string? Name, string? Value);

    // Compiles the loaded script and reads its options into manager.Config,
    // as the Options button does; null, or why it could not.
    private async Task<string?> LoadOptionsAsync(IScriptManager manager)
    {
        await _optionsGate.WaitAsync();
        try
        {
            string file = manager.LoadedScript;
            object? compiled = await Task.Run(() => manager.Compile(File.ReadAllText(file)));
            if (compiled is null)
                return "the script did not compile";
            manager.LoadScriptConfig(compiled);
            return manager.Config is null ? "the script has no options" : null;
        }
        catch (Exception e)
        {
            return $"the script cannot be configured, it has compilation errors: {e.Message}";
        }
        finally
        {
            _optionsGate.Release();
        }
    }

    private static object Describe(string script, IScriptOptionContainer config, bool editable)
    {
        object Item(string category, IOption o) => new
        {
            category,
            group = category == "Options" ? "Options" : category.Replace('_', ' '),
            name = o.Name,
            displayName = string.IsNullOrWhiteSpace(o.DisplayName) ? o.Name : o.DisplayName,
            description = o.Description,
            type = Kind(o.Type),
            values = o.Type.IsEnum ? Enum.GetNames(o.Type).Select(n => n.Replace('_', ' ')).ToArray() : null,
            value = Shown(o, config.OptionValues.GetValueOrDefault(o) ?? ""),
            @default = Shown(o, o.DefaultValue?.ToString() ?? ""),
            transient = o.Transient,
        };
        var options = config.Options.Select(o => Item("Options", o))
            .Concat(config.MultipleOptions.SelectMany(g => g.Value.Select(o => Item(g.Key, o))))
            .ToList();
        return new { script, storage = config.Storage, file = config.OptionsFile, editable, options };
    }

    private static string Kind(Type t) =>
        t == typeof(bool) ? "bool"
        : t.IsEnum ? "enum"
        : t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? "int"
        : t == typeof(float) || t == typeof(double) || t == typeof(decimal) ? "number"
        : "text";

    // An enum value as the Options window shows it: "_" as spaces.
    private static string Shown(IOption o, string value) => o.Type.IsEnum ? value.Replace('_', ' ') : value;

    // The text stored for a value given by the manager, or null if it does not
    // fit the option's type. Stored as the Options window stores them: bools
    // as True/False, enums by name (spaces kept; Get turns them back).
    private static string? Normalize(IOption o, string value)
    {
        value = value.Trim();
        var t = o.Type;
        if (t == typeof(bool))
            return value.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => "True",
                "false" or "0" or "no" or "off" or "" => "False",
                _ => null,
            };
        if (t.IsEnum)
        {
            string? match = Enum.GetNames(t).FirstOrDefault(n =>
                n.Equals(value.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
            return match?.Replace('_', ' ');
        }
        if (Kind(t) == "int")
            return long.TryParse(value, out long n) ? n.ToString() : null;
        if (Kind(t) == "number")
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;
        return value;
    }
}
