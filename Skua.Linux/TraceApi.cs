using System.Diagnostics.Tracing;
using System.Net;
using Microsoft.Diagnostics.Tracing.Etlx;
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
///
/// With summary=1 the reply also has the busiest methods as text: the samples taken while a
/// thread ran managed code, by the method on top (self) and by every method on the stack
/// (total), so a trace can be read without access to the server's disk.
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
            long bytes = new FileInfo(file).Length;
            if (request.QueryString["summary"] is "1" or "true")
                return new { file, secs, bytes, summary = await Task.Run(() => Summarize(file)) };
            return new { file, secs, bytes };
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>The top methods of a trace, as lines of text.</summary>
    public static List<string> Summarize(string file, int top = 30)
    {
        string etlx = Microsoft.Diagnostics.Tracing.Etlx.TraceLog.CreateFromEventPipeDataFile(file);
        try
        {
            using var log = new Microsoft.Diagnostics.Tracing.Etlx.TraceLog(etlx);
            var self = new Dictionary<string, int>();
            var total = new Dictionary<string, int>();
            var threads = new Dictionary<int, int>();
            int samples = 0;
            var kinds = new Dictionary<string, int>();
            foreach (var e in log.Events)
            {
                string kind = $"{e.ProviderName}/{e.EventName} Type={e.PayloadByName("Type")}";
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (e.ProviderName != "Microsoft-DotNETCore-SampleProfiler" || !e.EventName.Contains("Sample"))
                    continue;
                // Type 2: the thread was running managed code; 1 (external) is a wait or native code.
                if (e.PayloadByName("Type")?.ToString() is not ("Managed" or "2"))
                    continue;
                var stack = e.CallStack();
                if (stack is null)
                    continue;
                samples++;
                threads[e.ThreadID] = threads.GetValueOrDefault(e.ThreadID) + 1;
                var seen = new HashSet<string>();
                bool first = true;
                for (var frame = stack; frame is not null; frame = frame.Caller)
                {
                    string name = frame.CodeAddress.FullMethodName;
                    if (string.IsNullOrEmpty(name))
                        name = frame.CodeAddress.ModuleName is { Length: > 0 } m ? $"[{m}]" : "?";
                    if (first)
                        self[name] = self.GetValueOrDefault(name) + 1;
                    first = false;
                    if (seen.Add(name))
                        total[name] = total.GetValueOrDefault(name) + 1;
                }
            }
            var lines = new List<string> { $"{samples} samples of running managed code, on {threads.Count} threads (busiest: {string.Join(", ", threads.OrderByDescending(t => t.Value).Take(5).Select(t => $"{t.Key}: {t.Value}"))})" };
            if (samples == 0)
            {
                lines.AddRange(kinds.OrderByDescending(k => k.Value).Take(8).Select(k => $"  event {k.Key}: {k.Value}"));
                return lines;
            }
            string Line(KeyValuePair<string, int> kv) => $"{100.0 * kv.Value / samples,5:0.0}%  {kv.Key}";
            lines.Add("-- self");
            lines.AddRange(self.OrderByDescending(kv => kv.Value).Take(top).Select(Line));
            lines.Add("-- total");
            lines.AddRange(total.OrderByDescending(kv => kv.Value).Take(top * 2).Select(Line));
            return lines;
        }
        finally
        {
            try { File.Delete(etlx); } catch { }
        }
    }
}
