# Gitea Actions workflows

CI for `gitea.jenangmaker.cloud`. These run on **Linux** act_runners — Gitea
has no Windows runner — so they cross-compile rather than reproduce the
toolchain-exact Windows build in `.github/workflows/release.yml`.

| Workflow | Trigger | What it does |
| :--- | :--- | :--- |
| `build.yml` | any push/PR, manual | Cross-compiles, verifies the layout, uploads `Build/AnyCPU` |
| `release.yml` | manual | Same, then publishes a portable zip as a Gitea release |
| `ruffle-test.yml` | manual + weekly | Runs `docs/ruffle-test` against the live AQW client |
| `publish-image.yml` | manual | Builds the VibeSkua Web image and pushes it to this registry (`vibeskua-web`) |
| `publish-manager.yml` | manual | Builds the VibeSkua Manager image (`manager/`) and pushes it (`vibeskua-manager`) |

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

All three workflows are **verified on the live instance**.

| Run | Workflow | Event | Result |
| :--- | :--- | :--- | :--- |
| #695 | build | dispatch | 8/8 green, 3m30s |
| #696 | build | **push** | 8/8 green, 1m57s |
| #697 | release | dispatch | 7/7 green, `VibeSkua-1.8.6-portable.zip` (25.6 MB) |
| #698 | ruffle bridge | dispatch | 6/6 green, `=== PASS ===`, callbacks 28/28 |

`build` produces `Skua.exe` (173,568 bytes), `Skua.Manager.exe`, `skua.swf` and
a 65 MB `Build/AnyCPU`. The `Assemblies/Assemblies` flattening fires on the
runner, confirming it is required there and not only locally.

`ruffle-test` reached `RESULT: callbacks=28/28 requestLoadGame=true` against the
live AQW client from inside CI — the bridge works with no Windows, no desktop
and no Flash anywhere in the pipeline.

### Things to know

**The release is a draft.** `release.yml` defaults `draft: true`, so run #697
created the release object and uploaded the zip, but Gitea does not create the
git tag until a draft is published — `list_tags` is still empty and
`get_latest_release` returns not-found. Both are expected. Publish from the
release page when you want the tag. A read-only API token cannot enumerate
drafts at all, so they look absent over the API even though they exist.

**Version comes from the dispatch input** when you supply one (#697 used
1.8.6); blank falls back to `<Version>` in `Directory.Build.props`.

**Default shell is `sh -e`, not bash.** Keep these scripts POSIX.

**Triggers:** `build.yml` has no branch filter. The repo's default branch is
`docker-and-ruffle-test` and there is no `main`, so an earlier `branches:
[main]` filter meant push events never fired. Add a filter back once the branch
layout settles.

**Artifact listing quirk.** Uploads succeed and the logs confirm them, but the
Gitea artifacts API returns an empty list. Download from the run page instead.
