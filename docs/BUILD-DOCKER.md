# Building the Windows client in Docker

These containers **build** the Windows VibeSkua (WPF + Flash ActiveX) so you
do not need the .NET SDK or Visual Studio on the host; you still run
`Skua.exe` on a Windows desktop. To **run** VibeSkua in Docker instead (Linux,
in a browser), see [DOCKER.md](../DOCKER.md).

---

## Which build image do I use?

| Your Docker host | Use | Command |
| :--- | :--- | :--- |
| Docker Desktop on **Windows Home** (WSL2 backend) | `docker/Dockerfile.linux` | `docker compose run --rm build` |
| Docker on macOS / Linux | `docker/Dockerfile.linux` | `docker compose run --rm build` |
| **Windows containers** (Win Pro/Enterprise/Server, GitHub Actions `windows-*`) | `docker/Dockerfile.windows` | `docker compose run --rm build-windows` |

> Docker Desktop on Windows **Home** only supports the WSL2 (Linux) backend,
> so there **`build`** is the service that runs; `build-windows` needs
> Pro/Enterprise/Server or CI.

Both produce Windows binaries. The Windows image is the toolchain-exact one and
is what CI should use; the Linux image cross-compiles with
`-p:EnableWindowsTargeting=true`.

---

## Quick start

```bash
# Cross-compile (any Docker host). Output lands in ./Build/AnyCPU
docker compose run --rm build

# Toolchain-exact build (Windows container host only)
docker compose run --rm build-windows

# Toolchain-exact build + Velopack installer. Output in ./Build and ./Releases
docker compose run --rm package-windows
```

Without compose, using BuildKit's direct output (no image is kept at all):

```bash
docker build -f docker/Dockerfile.linux --target export \
  --output type=local,dest=./Build .
```

Pin a version for Velopack packaging instead of reading
`Directory.Build.props`:

```bash
docker build -f docker/Dockerfile.windows --target export-package \
  --build-arg VERSION=1.8.6 -t vibeskua:release .
```

---

## What the build does

Both Dockerfiles follow the same shape as `.github/workflows/release.yml`:

1. **restore stage** — copies only `Skua.sln`, `Directory.Build.props`,
   `Shared/`, the 13 `.csproj` files and `Skua.App.WPF/Assemblies/`, then runs
   `dotnet restore`. Editing source code does not invalidate the NuGet layer.
2. **build stage** — copies the tree and runs
   `dotnet build Skua.sln -c Release -p:WarningLevel=0`. The `PostBuild`
   targets in the `.csproj` files assemble `Build/AnyCPU` themselves, including
   the `FFDec/` copy and the `Assemblies/` flattening.
3. **package stage** (Windows only) — `dotnet tool install -g vpk` then
   `vpk pack -u VibeSkua -v <version> -p Build\AnyCPU -e Skua.exe -o Releases`.
4. **export stage** — a thin image that carries only the artifacts and copies
   them to the bind-mounted output directory.

### The AS3 / `skua.swf` step is skipped

`Build-VibeSkua.ps1` recompiles `Skua.AS3` with the Flex SDK (`mxmlc`). The
containers **do not**, because `Skua.AS3/skua/bin/skua.swf` is committed to the
repo and the GitHub release workflow already relies on that prebuilt file. If
you change anything under `Skua.AS3/`, recompile the SWF on the host first
(FlashDevelop / IntelliJ / `Skua.AS3/compile-as3.ps1`) and commit it before
building the image.

---

## Known caveats

**Linux cross-compile — case-sensitive MSBuild globs.** The `PostBuild` target
in `Skua.App.WPF.csproj` excludes `$(TargetDir)assemblies\**` from its `Move`,
but the directory on disk is `Assemblies`. MSBuild globs are case-insensitive
on Windows and case-sensitive on Linux, so on Linux the exclude misses and the
COM interop DLLs end up at `Build/AnyCPU/Assemblies/Assemblies/`. Confirmed on
Ubuntu 20.04; the Linux Dockerfile flattens that back after the build. Fixing the casing in
`Skua.App.WPF.csproj` would remove the need for the workaround and is harmless
on Windows.

**`$(AppData)` in `Skua.Plugin.DailyTracker.csproj`.** Its `PostBuild` target
writes the plugin DLL into `$(AppData)\Skua\plugins`. There is no `%APPDATA%`
on Linux, so the image sets `APPDATA=/tmp/appdata` to keep that write out of
the container root. The release copy into `Build/AnyCPU/plugins` is unaffected.

**Windows container base image tags.** Process isolation requires the container
base to match the host build. The Dockerfile defaults to `ltsc2022`; override
both args if your host needs something else:

```bash
docker build -f docker/Dockerfile.windows \
  --build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2025 \
  --build-arg EXPORT_IMAGE=mcr.microsoft.com/windows/servercore:ltsc2025 .
```

**The Linux build cannot use `dotnet build Skua.sln`.** Verified on Ubuntu
20.04 with SDK 10.0.401. Two SDK rules collide:

* With no `RuntimeIdentifier`, no Windows apphost is emitted. `Skua.exe` and
  `Skua.Manager.exe` are never produced, and `Skua.Manager.csproj`'s PostBuild
  copy fails with `MSB3030: Could not copy the file "Skua.Manager.exe" because
  it was not found`.
* Adding `-p:RuntimeIdentifier=win-x64` to a *solution* build fails with
  `NETSDK1134: Building a solution with a specific RuntimeIdentifier is not
  supported`.

So the two `WinExe` projects are built individually with the RID, and their
ProjectReferences pull in the rest:

```bash
FLAGS="-c Release -p:WarningLevel=0 --nologo -p:EnableWindowsTargeting=true"
RIDFLAGS="-p:RuntimeIdentifier=win-x64 -p:SelfContained=false"
dotnet build Skua.App.WPF/Skua.App.WPF.csproj $FLAGS $RIDFLAGS
dotnet build Skua.Manager/Skua.Manager.csproj  $FLAGS $RIDFLAGS
dotnet build Skua.Plugin.DailyTracker/Skua.Plugin.DailyTracker.csproj $FLAGS
```

That produces a complete `Build/AnyCPU` — `Skua.exe`, `Skua.Manager.exe`,
`skua.swf`, `Assemblies/`, `FFDec/`, `plugins/`, and the JSON data files — with
0 warnings and 0 errors.

**Verification status.** The build recipe above and the `Assemblies/Assemblies`
flattening are verified on Linux. Docker itself is not installed on the machine
where these files were written, so the **images** have not been built end to
end — what is unproven is the Dockerfile plumbing around a recipe that is
known-good, not the recipe. `Dockerfile.windows` is unverified in both senses;
it mirrors `.github/workflows/release.yml` closely.
