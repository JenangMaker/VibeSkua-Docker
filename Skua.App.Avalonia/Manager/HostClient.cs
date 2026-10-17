using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.Linux;

namespace Skua.App.Avalonia.Manager;

/// <summary>
/// The Manager's way into VibeSkua: the tab host's API (TabHostWindow.Api.cs) on
/// this machine, with SKUA_API_TOKEN when one is set. Starts VibeSkua when it is
/// not running.
/// </summary>
public static class HostClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(Regex.Replace(SkuaRuntime.Env("SKUA_HOST_API_PREFIX", "http://127.0.0.1:8789/"), @"://[+*]:", "://127.0.0.1:")),
            Timeout = TimeSpan.FromSeconds(30),
        };
        ApiAuth.AddTo(http);
        return http;
    }

    public static async Task<bool> IsRunning()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var reply = await Http.GetAsync("status", cts.Token);
            return reply.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static readonly SemaphoreSlim Starting = new(1, 1);

    /// <summary>Starts VibeSkua if it is not running and waits until its API answers (up to 90 s).</summary>
    public static async Task<bool> EnsureRunning()
    {
        if (await IsRunning())
            return true;
        await Starting.WaitAsync();
        try
        {
            if (await IsRunning())
                return true;
            if (ClientPath() is not { } exe)
                throw new InvalidOperationException("VibeSkua's client was not found next to the Manager.");
            Console.WriteLine($"[manager] starting {exe}");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            for (int i = 0; i < 90; i++)
            {
                await Task.Delay(1000);
                if (await IsRunning())
                    return true;
            }
            return false;
        }
        finally
        {
            Starting.Release();
        }
    }

    /// <summary>The client beside the Manager: VibeSkua.exe, else this app's own executable.</summary>
    private static string? ClientPath()
    {
        string dir = AppContext.BaseDirectory;
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (string name in new[] { "VibeSkua" + ext, "Skua.App.Avalonia" + ext })
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
                return path;
        }
        // Started as "Skua.App.Avalonia --manager": the same executable, without the switch.
        return Environment.ProcessPath is { } self && !Path.GetFileNameWithoutExtension(self).Contains("Manager", StringComparison.OrdinalIgnoreCase)
            ? self : null;
    }

    public static async Task<JsonElement> Get(string path)
    {
        using var reply = await Http.GetAsync(path.TrimStart('/'));
        return await Read(reply);
    }

    public static async Task<JsonElement> Send(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'))
        {
            Content = body is null ? new StringContent("") : JsonContent.Create(body),
        };
        using var reply = await Http.SendAsync(request);
        return await Read(reply);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage reply)
    {
        string text = await reply.Content.ReadAsStringAsync();
        JsonElement json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        if (!reply.IsSuccessStatusCode)
        {
            string error = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString() ?? "" : text;
            throw new InvalidOperationException($"VibeSkua answered {(int)reply.StatusCode}: {error}");
        }
        return json;
    }
}
