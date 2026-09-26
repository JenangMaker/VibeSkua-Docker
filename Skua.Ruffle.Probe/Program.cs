// Phase-0 probe for the Ruffle bridge.
//
//   dotnet run --project Skua.Ruffle.Probe -- [--prefix http://127.0.0.1:8790/]
//       [--origin http://127.0.0.1:8770] [--calls 200] [--events 10] [--actions]
//
// Starts the bridge, waits for the page to connect, then reports what the
// game says through it, how long a call takes, which events arrive, and
// (with --actions) whether walking, attacking and a skill go through.

using System.Collections.Concurrent;
using System.Diagnostics;
using Skua.Ruffle;

string prefix = Opt("--prefix") ?? "http://127.0.0.1:8790/";
string[] origins = (Opt("--origin") ?? "http://127.0.0.1:8770").Split(',');
int calls = int.Parse(Opt("--calls") ?? "200");
int eventSeconds = int.Parse(Opt("--events") ?? "10");
bool actions = args.Contains("--actions");

using var bridge = new RuffleBridge(prefix, origins);
var events = new ConcurrentDictionary<string, int>();
bridge.FlashCall += (name, _) => events.AddOrUpdate(name, 1, (_, n) => n + 1);
bridge.ConnectionChanged += up => Console.WriteLine(up ? "[probe] page connected" : "[probe] page disconnected");
bridge.Start();
Console.WriteLine($"[probe] listening on {prefix}");

if (!bridge.WaitForConnection(TimeSpan.FromMinutes(3)))
{
    Console.WriteLine("[probe] FAIL: no page connected");
    return 1;
}

var flash = new RuffleFlashUtil(bridge);
string? Get(string path) => flash.Call("getGameObject", path);

Console.WriteLine("== reads");
Console.WriteLine($"isLoggedIn  = {flash.Call("isLoggedIn")}");
Console.WriteLine($"map         = {Get("world.strMapName")}");
Console.WriteLine($"cell        = {Get("world.strFrame")}");
Console.WriteLine($"player      = {Get("world.myAvatar.objData.strUsername")}");
Console.WriteLine($"hp          = {flash.Call<int>("getGameObject", "world.myAvatar.dataLeaf.intHP")}");
Console.WriteLine($"class       = {Get("world.myAvatar.objData.strClassName")}");
Console.WriteLine($"areaUsers   = {Get("world.areaUsers")}");

Console.WriteLine($"== latency: {calls} sequential uncached calls");
var times = new List<double>(calls);
for (int i = 0; i < 5; i++)
    bridge.Invoke("getGameObject", "world.myAvatar.dataLeaf.intHP");
var total = Stopwatch.StartNew();
for (int i = 0; i < calls; i++)
{
    var sw = Stopwatch.StartNew();
    bridge.Invoke("getGameObject", "world.myAvatar.dataLeaf.intHP");
    times.Add(sw.Elapsed.TotalMilliseconds);
}
total.Stop();
times.Sort();
double P(double q) => times[Math.Min(times.Count - 1, (int)(q * times.Count))];
Console.WriteLine($"min {times[0]:0.00} ms  median {P(0.5):0.00}  p95 {P(0.95):0.00}  max {times[^1]:0.00}  => {calls / total.Elapsed.TotalSeconds:0} calls/s");

Console.WriteLine($"== events for {eventSeconds}s");
events.Clear();
Thread.Sleep(TimeSpan.FromSeconds(eventSeconds));
foreach (var (name, n) in events.OrderByDescending(e => e.Value))
    Console.WriteLine($"{n,6}  {name}");
if (events.IsEmpty)
    Console.WriteLine("  (none)");

if (actions)
{
    Console.WriteLine("== actions");
    int x = flash.Call<int>("getGameObject", "world.myAvatar.pMC.x");
    int y = flash.Call<int>("getGameObject", "world.myAvatar.pMC.y");
    int toX = x < 480 ? x + 120 : x - 120;
    flash.Call("walkTo", toX, y, 8);
    Thread.Sleep(2500);
    int nx = flash.Call<int>("getGameObject", "world.myAvatar.pMC.x");
    Console.WriteLine($"walkTo      : x {x} -> {nx} (asked {toX}) {(Math.Abs(nx - toX) <= 10 ? "PASS" : "CHECK")}");

    string? attack = flash.Call("attackMonsterName", "*");
    Thread.Sleep(800);
    string? target = Get("world.myAvatar.target.objData.strMonName");
    Console.WriteLine($"attack *    : returned {attack}, target {target ?? "(none)"}");

    string? skill = flash.Call("useSkill", 1);
    Console.WriteLine($"useSkill 1  : returned {skill}");
}

Console.WriteLine("[probe] done");
return 0;

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
