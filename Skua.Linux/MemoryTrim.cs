using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Linux;

/// <summary>
/// Gives memory back after a script starts (its compile is done) and after it
/// stops (it has been unloaded). Compiling a big script (CoreBots and the
/// story files it includes) leaves the .NET heap and glibc's heaps holding
/// what the compiler freed: a tab's Skua went from 190 to 434 MB after
/// starting three big scripts, and a compacting collection plus malloc_trim
/// took it to 347 MB. Once per start or stop, a few tens of ms.
/// SKUA_MEMORY_TRIM=0 turns it off.
/// </summary>
public sealed class MemoryTrim(IServiceProvider services)
{
    private static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AfterStop = TimeSpan.FromSeconds(10);

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    public void Start()
    {
        if (!OperatingSystem.IsLinux() || SkuaRuntime.EnvRaw("SKUA_MEMORY_TRIM") is "0")
            return;
        _ = Task.Run(Watch);
    }

    private async Task Watch()
    {
        var manager = services.GetRequiredService<IScriptManager>();
        bool running = false;
        DateTime? due = null;
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                bool now = manager.ScriptRunning;
                if (now != running)
                {
                    running = now;
                    due = DateTime.UtcNow + (now ? AfterStart : AfterStop);
                }
                if (due is { } at && DateTime.UtcNow >= at)
                {
                    due = null;
                    Trim();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[host] memory trim: {e.Message}");
            }
        }
    }

    private static void Trim()
    {
        long before = Environment.WorkingSet;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try
        {
            MallocTrim(0);
        }
        catch (Exception)
        {
            // Not glibc: nothing to trim.
        }
        long after = Environment.WorkingSet;
        Console.WriteLine($"[host] memory trim: {before / 1048576} -> {after / 1048576} MB");
    }
}
