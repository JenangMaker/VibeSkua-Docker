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
| C2 | Compare: Options → **Static Player Art** on, wait 5 s | **wgpu:** players freeze but stay visible. **webgl (expected to fail):** other players disappear. Turn it back off after | — |
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

## Known noise

- `Error #1010 … (accessing field: params)` once at startup (the page asking before the game has loaded)
- `QuestRequirementWiki/onFrame … ModalStack`: a few, then `disabled module`
- `hideme.swf … 404` (AQW's server)
- `town_Battleon_…/frame8 … objData`, `Ragnar_NPC_Full/frame6 … strGender` (AQW's map/NPC scripts)
- `Unknown device font …`, `mpa: invalid main_data_begin …`, `Encountered stub: …`
