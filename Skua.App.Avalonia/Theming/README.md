# Theming

The 2.x look, taken from upstream VibeSkua 2.1.2 (`NinjaXz/VibeSkua`,
`Skua.Shared.Avalonia/Theming`, commit `698a3db`); credit to NinjaXz. Used on the same
terms as the 1.x code (MIT, see LICENSE). Changes from upstream are marked in the files.

- `Colors.axaml`: the colour tokens (accent, danger, surfaces, text, tints). The accent
  keys are set at run time from the saved theme by `Services/ThemeBrushes.cs`.
