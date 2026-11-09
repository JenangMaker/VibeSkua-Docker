# Theming

The 2.x look, taken from upstream VibeSkua 2.1.2 (`NinjaXz/VibeSkua`,
`Skua.Shared.Avalonia/Theming`, commit `698a3db`); credit to NinjaXz. Used on the same
terms as the 1.x code (MIT, see LICENSE). Changes from upstream are marked in the files.

- `Colors.axaml`: the colour tokens (accent, danger, surfaces, text, tints). The accent
  keys are set at run time from the saved theme by `Services/ThemeBrushes.cs`.
- `BaseTheme.axaml` and `Styles/*.axaml`: the control styles (one file per control) and
  their class names (`skua-button`, `skua-primary`, `outline`, `card`, ...), unchanged
  except the `avares://` paths and the `HoverGlow` namespace.
- `HoverGlow.cs`: the cursor-following hover glow on buttons. Ours: `SKUA_HOVER_GLOW=0`
  turns the glow off (plain hover colours).

Our old 1.x class names (`flat`, `outlined`, `card` on HeaderedContentControl, `h6`,
`caption`, `plain`) are kept in `../Styles.axaml`, restyled to match.
