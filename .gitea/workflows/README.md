# Gitea Actions workflows

CI for `gitea.jenangmaker.cloud`. These run on **Linux** act_runners — Gitea
has no Windows runner — so they cross-compile rather than reproduce the
toolchain-exact Windows build in `.github/workflows/release.yml`.

| Workflow | Trigger | What it does |
| :--- | :--- | :--- |
| `build.yml` | any push/PR, manual | Cross-compiles, verifies the layout, uploads `Build/AnyCPU` |
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

## Status

`build.yml` is **verified on the live instance** (run #695, `workflow_dispatch`,
all 8 steps green in ~3.5 min). It produced `Skua.exe` (173,568 bytes),
`Skua.Manager.exe`, `skua.swf` and a 65 MB `Build/AnyCPU`, and uploaded a
120-file artifact via `actions/upload-artifact@v3`. The
`Assemblies/Assemblies` flattening fired, confirming it is required on the
runner as well as locally.

Note the runner's default shell is `sh -e`, not bash. Keep these scripts POSIX.

**Triggers.** `build.yml` has no branch filter. The repo's default branch is
`docker-and-ruffle-test` and there is no `main`, so an earlier `branches:
[main]` filter meant push events never fired. Add a filter back once the
branch layout settles.

**Still unverified:** `release.yml` (never dispatched — in particular the
`gitea-release-action` token and the zip step) and `ruffle-test.yml`.

**Artifact listing quirk.** The upload succeeds and the log confirms it, but
the Gitea artifacts API returns an empty list for both `list_artifacts` and
`list_run_artifacts`. Download artifacts from the run's web page instead.
