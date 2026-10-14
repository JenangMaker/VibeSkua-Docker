using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// Reads any script's options in a Skua of its own: Skua.App.Avalonia
/// --script-options &lt;script&gt; &lt;out.json&gt; [account]. The Manager's queue edits
/// an entry's options while the tab may be running another script; compiling
/// in that tab would replace the running script's compile state (its load
/// context, CompiledScript), so the tab host runs this instead: services only
/// (no game, no bridge, no API), the script compiled and its options read as
/// GET /script/options gives them, with the account's saved values (its own
/// file, else the shared one), written as JSON to out.json. Then the process
/// exits, and its memory goes with it.
/// </summary>
public static class ScriptOptionsProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static int Run(string script, string output, string? account)
    {
        object result;
        int code = 0;
        try
        {
            AccountOptions.AccountOverride = account;
            var runtime = SkuaRuntime.Create();
            // What a tab's start does before scripts compile: the compiler
            // references the assemblies loaded by then (Newtonsoft, the script
            // interface's), which a bare process has not loaded yet.
            runtime.Services.GetRequiredService<ISettingsService>().SetApplicationVersion();
            _ = runtime.Services.GetRequiredService<IScriptInterface>();
            _ = typeof(Newtonsoft.Json.JsonConvert).Assembly;
            var manager = runtime.Services.GetRequiredService<IScriptManager>();
            // Compile resolves the script's own includes from where it is.
            manager.SetLoadedScript(script);
            object? compiled = manager.Compile(File.ReadAllText(script));
            if (compiled is null)
                throw new Exception("the script did not compile");
            manager.LoadScriptConfig(compiled);
            result = manager.Config is { } config
                ? HostApi.DescribeOptions(script, config, editable: true, AccountOptions.Account(runtime.Services))
                : new { error = "the script has no options" };
        }
        catch (Exception e)
        {
            result = new { error = $"could not read the options of {Path.GetFileName(script)}: {e.Message}" };
            code = 1;
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result, Json));
        return code;
    }
}
