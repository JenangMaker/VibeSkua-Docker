namespace Skua.Linux;

/// <summary>
/// CPU and memory of this container's processes, from /proc: sampled every
/// few seconds by the tab host for its API (GET /tabs, GET /resources). CPU
/// is in percent of one core over the last sample interval.
/// </summary>
public sealed class ProcessStats
{
    public sealed record Sample(int Pid, string Name, string CommandLine, double Cpu, double MemoryMb);

    // Kernel clock ticks per second (sysconf(_SC_CLK_TCK)), 100 on Linux.
    private const double TicksPerSecond = 100;

    private readonly Dictionary<int, (long Ticks, DateTime At)> _last = new();
    private readonly Dictionary<int, string> _commandLines = new();
    private volatile IReadOnlyDictionary<int, Sample> _samples = new Dictionary<int, Sample>();

    public IReadOnlyDictionary<int, Sample> Samples => _samples;

    public static bool Supported => Directory.Exists("/proc/self");

    /// <summary>Takes a sample of every process; the first one shows no CPU yet.</summary>
    public void Update()
    {
        if (!Supported)
            return;
        var now = DateTime.UtcNow;
        var samples = new Dictionary<int, Sample>();
        var seen = new HashSet<int>();
        foreach (string dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid))
                continue;
            try
            {
                // pid (comm) state ... : comm may hold spaces and parentheses,
                // so the fields are counted from its last ')'.
                string stat = File.ReadAllText($"{dir}/stat");
                int close = stat.LastIndexOf(')');
                string name = stat[(stat.IndexOf('(') + 1)..close];
                string[] fields = stat[(close + 2)..].Split(' ');
                long ticks = long.Parse(fields[11]) + long.Parse(fields[12]);   // utime + stime
                double cpu = _last.TryGetValue(pid, out var last) && now > last.At
                    ? Math.Max(0, ticks - last.Ticks) / TicksPerSecond / (now - last.At).TotalSeconds * 100
                    : 0;
                _last[pid] = (ticks, now);
                seen.Add(pid);
                if (!_commandLines.TryGetValue(pid, out string? cmd))
                    _commandLines[pid] = cmd = File.ReadAllText($"{dir}/cmdline").Replace('\0', ' ').Trim();
                samples[pid] = new Sample(pid, name, cmd, Math.Round(cpu, 1), Math.Round(RssKb(dir) / 1024.0, 1));
            }
            catch
            {
                // Gone meanwhile, or not ours to read.
            }
        }
        foreach (int pid in _last.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _last.Remove(pid);
            _commandLines.Remove(pid);
        }
        _samples = samples;
    }

    private static long RssKb(string dir)
    {
        foreach (string line in File.ReadLines($"{dir}/status"))
            if (line.StartsWith("VmRSS:"))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return 0;   // kernel threads
    }

    /// <summary>Memory as the container sees it: its cgroup's use and limit, and the machine's.</summary>
    public static object Memory()
    {
        static double? Mb(string? text) => long.TryParse(text?.Trim(), out long b) ? Math.Round(b / 1048576.0) : null;
        static string? Read(string path) { try { return File.ReadAllText(path); } catch { return null; } }
        var info = new Dictionary<string, long>();
        foreach (string line in (Read("/proc/meminfo") ?? "").Split('\n'))
        {
            string[] parts = line.Split(':', 2);
            if (parts.Length == 2 && long.TryParse(parts[1].Trim().Split(' ')[0], out long kb))
                info[parts[0]] = kb;
        }
        return new
        {
            containerMb = Mb(Read("/sys/fs/cgroup/memory.current")),
            containerLimitMb = Mb(Read("/sys/fs/cgroup/memory.max")),   // null: no limit
            hostTotalMb = info.TryGetValue("MemTotal", out long t) ? Math.Round(t / 1024.0) : (double?)null,
            hostAvailableMb = info.TryGetValue("MemAvailable", out long a) ? Math.Round(a / 1024.0) : (double?)null,
        };
    }

    /// <summary>Load averages over 1, 5 and 15 minutes.</summary>
    public static double[] Load()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        }
        catch
        {
            return [];
        }
    }
}
