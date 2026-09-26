// Skua.Host: Skua.Core without a UI, driving skua.swf in the vibeskua-web
// page through Skua.Ruffle's WebSocket bridge. The runtime (environment,
// services, bridge, API, script sync) is Skua.Linux's SkuaRuntime.
//
// Control API (JSON; local only unless SKUA_API_PREFIX says otherwise):
//   GET  /status                      bridge, login, map, script state
//   POST /script/start?path=<file>    load and start a script (.cs); a
//                                     relative path is under Skua/Scripts
//   POST /script/start   (body: C#)   start a script given as source
//   POST /script/stop
//   GET  /log?type=script|debug|flash&since=<n>
//   GET  /scripts?q=<terms>&limit=<n> search the script repository
//   POST /scripts/update              fetch missing and outdated scripts

using Skua.Linux;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var runtime = SkuaRuntime.Create();
runtime.Start(Arg("--script"));
await Task.Delay(Timeout.Infinite);
