# Gitea Actions workflows

CI for `gitea.jenangmaker.cloud`. These run on **Linux** act_runners — Gitea
has no Windows runner — so they cross-compile rather than reproduce the
toolchain-exact Windows build in `.github/workflows/release.yml`.

| Workflow | Trigger | What it does |
| :--- | :--- | :--- |
| `build.yml` | push/PR to `main`, manual | Cross-compiles, verifies the layout, uploads `Build/AnyCPU` |
| `release.yml` | manual | Same, then publishes a portable zip as a Gitea release |
| `ruffle-test.yml` | manual + weekly | Runs `docs/ruffle-test` against the live AQW client |

## Before the first run

1. **Register an act_runner** if you have not:
   `gitea.jenangmaker.cloud` → Site Administration → Actions → Runners.
2. **Check the runner label.** These use `runs-on: ubuntu-latest`, the usual
   default. If yours registered something else, change that line in all three.
3. **Enable Actions on the repo**: Settings → Advanced → check *Actions*.

Each job sets `container: node:22-bookworm`. Node is required inside the
container because Gitea runs JS actions such as `actions/checkout` there — the
plain .NET SDK image has no node, so the SDK is installed with
`dotnet-install.sh` instead of using a .NET base image.

## The build is per-project, not per-solution

This is not a style choice. On Linux, `dotnet build Skua.sln` fails:

* No `RuntimeIdentifier` → no Windows apphost → no `Skua.exe`, and
  `Skua.Manager.csproj`'s PostBuild dies with `MSB3030`.
* Solution-wide `-p:RuntimeIdentifier=win-x64` → `NETSDK1134`.

So `Skua.App.WPF` and `Skua.Manager` are built individually with
`-p:RuntimeIdentifier=win-x64 -p:SelfContained=false`, and their
ProjectReferences pull in everything else. Verified on Ubuntu 20.04 with SDK
10.0.401: 0 warnings, 0 errors, both `.exe` files produced.

The `Normalize release layout` step flattens `Assemblies/Assemblies`, which
appears because `Skua.App.WPF.csproj`'s PostBuild excludes
`$(TargetDir)assemblies\**` while the directory is `Assemblies` — MSBuild globs
are case-sensitive on Linux. Fixing the casing in the `.csproj` is harmless on
Windows and would let that step be deleted.

## Differences from the GitHub workflow

**No Velopack.** `.github/workflows/release.yml` runs `vpk pack` to produce the
installer that feeds the Manager's auto-update tab. That is only verified on
Windows, so `release.yml` here publishes the portable `Build/AnyCPU` tree as a
zip instead. Keep using the GitHub workflow, or add a Windows runner, when you
need the installer.

**No AS3 compile.** Neither CI rebuilds `skua.swf`; the committed artifact is
used. If you change anything under `Skua.AS3/`, recompile on a machine with the
Flex SDK and commit the result first.

## Not yet run

These have not executed against a live Gitea instance. The build recipe inside
them is verified on Linux, but the surrounding Actions plumbing — runner label,
`actions/upload-artifact@v3` compatibility with your Gitea version, and the
`gitea-release-action` token — has not been. Expect to adjust on first run;
`workflow_dispatch` is enabled on all three so you can trigger them by hand.
