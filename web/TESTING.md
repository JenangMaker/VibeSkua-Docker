# In-game test cases (vibeskua-web)

Manual checks for the Ruffle build, run in the browser at :3000. For each run,
save the container log (`docker logs vibeskua-web > docker_logsN.txt`) and a
screen recording, and note the renderer, Draw and Resolution from the control
bar.

Use a throwaway account. Where a case says "compare", run it once on
**wgpu** and once on **webgl**: wgpu can draw into bitmaps
(`BitmapData.draw`), webgl cannot, and every AQW feature that relies on that
comes out blank on webgl.

Pass = the result column matches. Log lines listed under "Known noise" at the
bottom are expected and don't fail a case.

## A. Loading

| # | Steps | Expected | Log check |
| :- | :- | :- | :- |
| A1 | Open the page | Title screen within ~20 s; panel shows `bridge up`, `disabled module: …` ×2 | `Used renderer:` matches the button that is lit |
| A2 | Log in, pick a server | Character loads into battleon; `Character load complete` in chat | No `RustError`, no `Error #1009 at Game/userTreeWrite` |
| A3 | webgl only: after A1 | Panel shows `webgl: Smooth Background on` | No `accessing field: applySmoothBG` error |
| A4 | Walk to battleontown, then `/join yulgar`, `/join greenguardwest` | Full backgrounds every time, no blank or black areas | At most one `BitmapData.draw … cannot render offscreen` stub warning (it logs once) |
| A5 | Change Resolution 100 → 50 → 100 on a map | Background stays correct after each change | none |

## B. Combat

| # | Steps | Expected | Log check |
| :- | :- | :- | :- |
| B1 | Target a monster, auto-attack | Attacks animate, damage numbers show, monster dies and respawns | none |
| B2 | Press skill 2, then spam 2 | Chat shows `Ability '…' is not ready yet` while it is cooling down | none |
| B3 | Compare: press a skill, watch its icon | **wgpu:** icon darkens and the dark part sweeps away clockwise. **webgl (known gap):** icon stays bright | — |
| B4 | Options → Game Settings → turn **Visual Skill CDs** on, then press a skill | Seconds count down over the icon (e.g. `3.2`), on **both** renderers | none |
| B5 | Compare: use skills repeatedly for 60 s | Note whether `sp_ssorc…/frameNN … at World/countDownAct` errors appear on wgpu too. If they do, they are AQW's; if only on webgl, report it | count these errors |
| B6 | Use a potion (skill 6 / item) | Potion works, global cooldown on other skills | none |
| B7 | Compare: get a buff/debuff (class skill with an aura) | Aura icons appear by the portraits and fade as they expire. webgl may show them without the fade | none |

## C. Other players and UI

| # | Steps | Expected | Log check |
| :- | :- | :- | :- |
| C1 | Stand in a busy battleon (5+ players) | All players visible with correct colours and gear | none |
| C2 | Compare: Options → **Static Player Art** on, wait 5 s | **wgpu:** players freeze but stay visible. **webgl:** players keep animating (no effect; tested 2026-09-26). Turn it back off after | — |
| C3 | Open Inventory, Bank, a Shop, the Quest list | Each opens, item previews render, closes cleanly | none |
| C4 | Open character customise (hair/colour) and the armour colour picker | Picker shows its colour gradient. webgl: the gradient/eyedropper may be blank | none |
| C5 | Open the Travel menu and travel from it | Map changes, background correct (see A4) | none |

## D. Performance

| # | Steps | Expected | Log check |
| :- | :- | :- | :- |
| D1 | Busy battleon, Draw **Max**, 60 s; note `docker stats` CPU | Record CPU per renderer; webgl should be clearly lower | — |
| D2 | Same with Draw **15** | Game keeps running at full speed (movement, combat timing); only the picture is choppier | — |
| D3 | Draw **Off** for 30 s, then back to Max | Picture resumes at the current game state; still logged in | — |
| D4 | Leave it in battleon 30 min, note container memory at start and end | Memory levels off. Steady growth = the known weak-Dictionary leak | — |

## E. Switching renderer

| # | Steps | Expected | Log check |
| :- | :- | :- | :- |
| E1 | Click wgpu while on webgl | Page reloads on wgpu (panel `renderer: wgpu-webgl`) | — |
| E2 | After E1, check Options → **Smooth Background** | Still on (the webgl run saved it). Turning it off on wgpu brings the rasterised background back without gaps | none |

## Results

Run remotely over the DevTools port (see README), logged in, 2026-09-26.

| # | webgl | wgpu |
| :- | :- | :- |
| A3/A4 | Pass: Smooth Background on, battleon/battleontown backgrounds full | — |
| B1/B2 | Pass: 60 s combat, 50 skill presses, 6 kills | — |
| B3 | Known gap: icons never darken | Pass: icons darken, sweep clears; global CD dims the rest |
| B4 | Pass: `10.0 → 5.5 → 2.8`, clears at 0 | Pass: `10.0 → 5.3 → 2.5` |
| B5 | 9 `sp_ssorc*` frame-script errors / 60 s (Scarlet Sorceress skill FX) | 10 / 60 s -- same on both, not the renderer |
| C2 | No effect: 8/8 players `isRasterized`, still animating | Pass: players freeze |
| C3 | Pass: inventory list, item preview | — |
| E2 | — | Pass: Smooth Background and Visual Skill CDs carried over from webgl |
| D1 | — | Intel HD P530 via `/dev/dri` (ANGLE/Mesa GL, not SwiftShader). Battleon, 10 players: Draw Max = renderer 98% + GPU process 25% (Komodo: 125-139%); Draw Off = renderer 63% + GPU 1%. The game logic alone holds ~0.6 core |

| D4 | — | 30 min idle in battleon (wgpu, Max): page process 1017 → 1137 MB (+120 MB, ~240 MB/h), GPU process 471 → 516 MB, container 5.44 → 5.78 GiB. Growth comes in steps (flat for 5-10 min, then +15-35 MB), not a steady climb. CPU flat at 122-136% |

Frame rate (page `requestAnimationFrame`/s over 5 s; Ruffle draws on these),
battleon, 10 players, Intel HD P530, 2026-09-26:

| Renderer | Draw Off | Draw 15 | Draw Max | CPU at Max (page + GPU proc) | CPU at 15 |
| :- | :- | :- | :- | :- | :- |
| wgpu-webgl | 61.6 | 2.2 (gaps 568 ms) | 2.2 (gaps 575 ms) | 65% + 28% | — |
| webgl | 75.8 | 49.6 | 36 (worst gap 68 ms) | 92% + 96% | 51% + 36% |

wgpu's stall is CPU in wgpu-core, not the GPU: a 5 s profile at Draw Max put
~40% of the page's main thread in `UsageScope` drop / `BufferUsageScope::set_size`
(wgpu-core 30.0.1 resets per-pass tables sized to every buffer ever allocated).

`CHROMIUM_FLAGS=--use-angle=gl-egl` (tried to stop the GLX log spam) logs
`No suitable EGL configs` at start and shows the same ~2 fps as GLX on wgpu,
so it fixes nothing; the spam is filtered in `/app/launch.sh` instead.

CPU per Chromium process comes from CDP `SystemInfo.getProcessInfo`; container
totals from Komodo (`ListDockerContainers` stats, refreshed ~every 30 s).

## Known noise

- `Error #1010 … (accessing field: params)` once at startup (the page asking before the game has loaded)
- `QuestRequirementWiki/onFrame … ModalStack`: a few, then `disabled module`
- `hideme.swf … 404` (AQW's server)
- `town_Battleon_…/frame8 … objData`, `Ragnar_NPC_Full/frame6 … strGender` (AQW's map/NPC scripts)
- `Unknown device font …`, `mpa: invalid main_data_begin …`, `Encountered stub: …`
