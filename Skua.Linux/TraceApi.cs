using System.Diagnostics.Tracing;
using System.Net;
using Microsoft.Diagnostics.NETCore.Client;

namespace Skua.Linux;

/// <summary>
/// POST /debug/trace?secs=N (5-300, default 30): CPU-samples this Skua process
/// for N seconds, as dotnet-trace's cpu-sampling profile does, into
/// ~/.config/Skua/traces (in the container's /config folder). There is no shell
/// in the container to run dotnet-trace from, and a trace started from the
/// environment at process start never got its method names (no rundown) when
/// it was cut off. Open the file with PerfView, or convert it with
/// `dotnet-trace convert --format Speedscope`.
/// </summary>
internal static class TraceApi
{
    private static int _running;

    public static async Task<object> Collect(HttpListenerRequest request)
    {
        int secs = int.TryParse(request.QueryString["secs"], out int s) ? Math.Clamp(s, 5, 300) : 30;
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return new { error = "a trace is already running in this tab" };
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "traces");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"tab{SkuaRuntime.Instance + 1}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.nettrace");
            EventPipeProvider[] providers =
            [
                new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x14C14FCCBD),
            ];
            using var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(providers, requestRundown: true);
            await using (var output = File.Create(file))
            {
                Task copy = session.EventStream.CopyToAsync(output);
                await Task.Delay(TimeSpan.FromSeconds(secs));
                // Stopping writes the rundown (method names), then ends the stream.
                await Task.Run(session.Stop);
                await copy;
            }
            return new { file, secs, bytes = new FileInfo(file).Length };
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
