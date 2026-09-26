# Skua.Ruffle

Skua's Flash bridge for Ruffle, so Skua.Core can drive `skua.swf` running in
a browser page (the vibeskua-web container) instead of the Flash ActiveX
control. Plain `net10.0`; runs on Linux.

- `RuffleBridge` - WebSocket server the page connects to; synchronous
  `Invoke` like Flash's `CallFunction`; the SWF's `ExternalInterface.call`s
  arrive as `FlashCall` events, in order, off the receive loop.
- `RuffleFlash` - Skua's call surface over it (`Call`, `Call<T>`,
  `Call(Type)`, the 15 ms getGameObject cache), matching Skua.WPF's
  `FlashUtil` conversions. Becomes the `IFlashUtil` in phase 1.
- `FlashValue` - JSON <-> .NET values, same rules as `ToFlashXml` /
  `FromFlashXml`.
- Page side: `web/public/skua-bridge.js`.
- `Skua.Ruffle.Probe` - phase-0 check: reads, call latency, events, actions.

## Phase 0 result (2026-09-26)

Driven from Linux (.NET 10) against a logged-in session, through a test relay
over the DevTools port (the container has no .NET yet):

| Check | Result |
| :- | :- |
| Reads (logged in, map, cell, name, HP, class, player list) | correct |
| `walkTo` | moved exactly to the requested x |
| `attackMonsterName` / `useSkill` | correct results |
| SWF events (`pext`, `debug`, ...) | arrive |
| Errors | 0 in 219 calls |

Latency is set by the page, not the transport: a SWF call costs ~0.1-0.3 ms,
but a message waits for Ruffle's main thread, which runs AQW frames of
~100 ms each in a busy Battleon late in a session (one frame per task, since
frames exceed the 41 ms budget). That caps round trips at ~10/s. Early in a
session frames were far cheaper (~60 fps with drawing off), and the profile
is dominated by the per-frame display-tree walks (`construct_frame`,
`enter_frame`, `run_frame_scripts`), so the frame cost appears to grow with a
display-object count that keeps rising. Fixing that in Ruffle is the
prerequisite for a responsive bridge.

Also found: Skua's `DisableFX` module makes Ruffle stall for ~3 s per frame
(`gotoAndStop(0)` over every weapon and monster clip every 15 frames).
