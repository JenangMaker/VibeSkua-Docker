# VibeSkua in Docker

## Read this first: the bot cannot run inside a container

VibeSkua is a **WPF desktop application that hosts the Flash ActiveX control
(`ShockwaveFlashObjects`) and hooks the Flash process via CoreHook**. Three
hard dependencies make containerised *execution* impossible:

| Dependency | Why a container can't provide it |
| :--- | :--- |
| WPF / DirectX rendering (`WMode="direct"`, hardware acceleration) | Windows containers have no GPU-backed desktop compositor or window station. |
| Flash Player **ActiveX** COM control | End-of-life, requires a registered in-process COM server on an interactive desktop. Not redistributable into a container image. |
| CoreHook process hooking (`corehook64.dll`, `coreload64.dll`) + `SetWindowPos` off-screen repositioning, hotkeys, tray icon | Needs a real interactive session (Session 1), not Session 0 container isolation. |

Linux containers are even further out — none of that exists there at all. (A
Linux container *can* run the Flash bridge once Ruffle replaces the ActiveX
control; see below. It cannot run the client as written.)

**So what's here instead:** a reproducible **build** container. It compiles
`Skua.sln` and drops the exact same `Build/AnyCPU` release layout that
`BuildRelease.bat` produces, without you installing the .NET 10 SDK, Visual
Studio, or Velopack on the host. You then run `Skua.exe` on a Windows desktop
as usual.

If you want VibeSkua running somewhere other than your own desktop **as it is
today**, the answer is a **Windows VM** (Hyper-V, a cloud Windows VM, or a
dedicated box) with Flash ActiveX installed — not Docker.

> **There is a route to headless Linux, but it needs a new host layer.**
> Replacing Flash ActiveX with [Ruffle](https://ruffle.rs) has been tested
> against the live AQW client and works: see [`docs/ruffle-test`](docs/ruffle-test/).
> `Skua.Core` (191 files, essentially no Windows coupling) would survive; only
> `Skua.WPF` / `Skua.App.WPF` and the CoreHook layer get replaced.

---

## The one container that runs something

```bash
docker compose run --rm ruffle-test
```

`docker/Dockerfile.ruffle-test` runs the Skua ExternalInterface bridge against
the **live AQW client** on Linux, headless, using Ruffle in place of Flash
ActiveX. It exits 0/1 so it works as a CI check. This is the proof that a
Linux/Docker runtime is reachable — see [`docs/ruffle-test`](docs/ruffle-test/).
It does not run the bot; it runs the bot's Flash bridge.

---

## Which build image do I use?

| Your Docker host | Use | Command |
| :--- | :--- | :--- |
| Docker Desktop on **Windows Home** (WSL2 backend) | `docker/Dockerfile.linux` | `docker compose run --rm build` |
| Docker on macOS / Linux | `docker/Dockerfile.linux` | `docker compose run --rm build` |
| **Windows containers** (Win Pro/Enterprise/Server, GitHub Actions `windows-*`) | `docker/Dockerfile.windows` | `docker compose run --rm build-windows` |

> Your machine is Windows 11 Home. Docker Desktop on Home only supports the
> WSL2 (Linux) backend, so **`build` is the service that will actually run for
> you**; `build-windows` needs Pro/Enterprise/Server or CI.

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
COM interop DLLs end up at `Build/AnyCPU/Assemblies/Assemblies/`. The Linux
Dockerfile flattens that back after the build. Fixing the casing in
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

**Not verified in this repo's environment.** Docker is not installed on the
machine where these files were written, so neither image has been built end to
end here. The Windows path mirrors the CI workflow closely and should be the
lower-risk one; the Linux cross-compile is the best-effort path for hosts that
cannot run Windows containers.
