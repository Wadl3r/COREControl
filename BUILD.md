# Building and installing CORE Control

CORE Control is a BepInEx 5 plugin for Nuclear Option. It compiles against the game's own
assemblies, so the build needs to know where the game is installed.

## Prerequisites

- Nuclear Option installed via Steam, with **BepInEx 5** already installed into the game folder
  (`<game>\BepInEx\core\BepInEx.dll` must exist).
- The **.NET SDK** (8.0 or newer). The project targets `net472`; the SDK pulls in the .NET Framework
  reference assemblies from NuGet, so no separate .NET Framework developer pack is needed.
  - Install: `winget install --id Microsoft.DotNet.SDK.8 --exact`
  - If `dotnet build` fails with `NU1100: Unable to resolve 'Microsoft.NETFramework.ReferenceAssemblies'`,
    NuGet has no package source configured. Add the default one once:
    `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`

## 1. Point the build at the game

The project file reads the `NUCLEAR_OPTION_DIR` environment variable (or an MSBuild `GameDir`
property) to find `Assembly-CSharp.dll`, `BepInEx.dll`, `0Harmony.dll`, Mirage and the Unity
modules. Set it once for your user account. Quote the path: the folder name contains a space.

```powershell
[Environment]::SetEnvironmentVariable(
    "NUCLEAR_OPTION_DIR",
    "D:\Steam\steamapps\common\Nuclear Option",
    "User")
```

Open a new terminal afterwards so the variable is picked up. Alternatively pass it per build:

```powershell
dotnet build COREControl.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\Nuclear Option"
```

## 2. Build

```powershell
dotnet build COREControl.csproj -c Release
```

Output lands in `bin\Release\net472\`:

| File | Purpose |
| --- | --- |
| `COREControl.dll` | The plugin. |
| `COREControl.pdb` | Debug symbols; BepInEx stack traces get line numbers with it present. |
| `CORE Control Duel.json` | A mission that ships with the mod. Copied from `Mission\` to the output root on purpose. |
| `CORE Control Duel Far.json` | The far-start version of that duel, shipped the same way. |

Game assemblies are referenced with `Private=false`, so they are not copied to the output.

## 3. Install

Copy the **entire** output folder, not just the DLL, into the game's plugins folder:

```powershell
$dst = "D:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\COREControl"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "bin\Release\net472\*" $dst -Recurse -Force
```

The mission JSON files must sit beside the DLL: `CommanderMissionInstaller` reads them from the
plugin folder when the game loads and installs them into the game's user mission list. A DLL-only
copy logs a warning and the CORE Control Duel missions never appear.

CORE Control declares itself incompatible with its upstreams, Ground Control (RTS)
(`com.groundcontrol.rts`, folder `GroundControlRts`) and Nuclear Option Commander
(`com.nuclearoption.commander`, folder `NuclearOptionCommander`). They patch the same game methods,
so with both loaded every Harmony patch would run twice; instead BepInEx skips CORE Control and logs
which GUID blocked it. Move the upstream folder to `BepInEx\disabledPlugins` to play CORE Control.
Nothing else collides: the config file, the plugin folder, the shipped missions and the save folder
(`<persistentDataPath>\COREControlState`) all have names of their own.

## 4. Rebuild and reinstall in one go

`build-and-install.ps1` in the repo root does steps 2 and 3 together (`build-release.bat` double-clicks it; `build-dev.bat` runs the hot-reload variant below), validates the game folder,
and warns (without deleting anything) if an upstream `GroundControlRts` or `NuclearOptionCommander`
folder is in `BepInEx\plugins`.

```powershell
.\build-and-install.ps1                 # uses $env:NUCLEAR_OPTION_DIR
.\build-and-install.ps1 -Clean          # wipe bin\Release\net472 first
.\build-and-install.ps1 -GameDir "D:\Steam\steamapps\common\Nuclear Option"
```

If PowerShell refuses to run the script, allow local scripts for your user once:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Close the game before reinstalling; Windows will not overwrite a DLL the game has loaded.

## Hot reload during development (no game restart)

The game folder has the **ScriptEngine** plugin from
[BepInEx.Debug](https://github.com/BepInEx/BepInEx.Debug) installed
(`BepInEx\plugins\ScriptEngine.dll`). It loads any DLL placed in `BepInEx\scripts\` from bytes,
so the file is never locked, and swaps the running copy for the new one on demand. Its config
(`BepInEx\config\com.bepis.bepinex.scriptengine.cfg`) is set to load `scripts\` at startup and
to **auto-reload about 3 seconds after any DLL in `scripts\` changes**. F6 forces a reload.

Two double-click wrappers in the repo root:

| File | Does |
| --- | --- |
| `build-release.bat` | Normal layout: `plugins\COREControl\`, mission JSON included, offers to launch the game. Quit the game first. |
| `build-dev.bat` | Hot-reload layout: DLL+PDB into `scripts\`, release copy removed. Run with the game open. |

Switch to hot-reload mode once (game closed, because the release copy is locked while loaded):

```powershell
.\build-dev.bat                   # or: .\build-and-install.ps1 -Dev
```

This removes `plugins\COREControl\` and puts the DLL and PDB in `scripts\`. Launch the
game. From then on, with the game running:

```powershell
.\build-dev.bat                   # build, copy, ScriptEngine reloads by itself
```

Watch the BepInEx console for `Unloading old plugin instances` then the mod's own
`CORE Control ... loaded` line. The mission you are in stays loaded.

What a reload resets: everything the mod holds in memory — control groups, camera bookmarks, the
enemy commander's plan state. Faction funds, units and buildings are game state and survive.
Settings are in the config file and survive.

What does not need a reload at all: anything in CMD → Settings. Those are live.

### Keeping Air Command missions and economy levels across a reload

On by default. While a mission runs the mod writes a small JSON snapshot every 20 s (and once more
as the old assembly unloads); a hot reload reads it once and deletes it, a normal launch never reads
it. To turn it off, set `KeepStateAcrossHotReload = false` under `[Developer]` in
`BepInEx\config\com.wadl3r.corecontrol.cfg` with the game closed (there is no in-game toggle).

A `build-dev.bat` reload keeps:

- Every Air Command mission you launched from the AIR window, including its AUTO flag and (if
  AUTO was on and the aircraft was lost since the last save) a queued relaunch. An aircraft you
  adopted with SELECT rather than launching yourself is not kept — re-adopt it after the reload.
- Gold mine, factory and naval dock upgrade levels, with a restored mine reattached to its
  resource site so the site still reads as taken.

It does **not** keep: point (village/hilltop/base) ownership, platoon composition or the enemy
commander's plan — those still reset like everything else above. A mission that was never
launched with the toggle on has nothing to restore, so it behaves exactly as before.

Watch `BepInEx\LogOutput.log` for `Snapshot written: N missions, M levels` (every ~20 s and right
before the reload) and, after the reload, `Air Command restored N missions, M relaunches queued, K
dropped` and `Economy restored N mine levels (M reattached to a resource site), N factory levels,
N dock levels`.

Back to the normal layout (the shipped mission JSON is only installed from `plugins\`):

```powershell
.\build-release.bat               # removes scripts\ copy, restores plugins\COREControl\
```

Never have the mod in both `plugins\` and `scripts\`. ScriptEngine refuses to load a GUID that
is already loaded, and if it did load, every Harmony patch would run twice.

## Checking it loaded

Launch Nuclear Option and look in `<game>\BepInEx\LogOutput.log` for lines from
`CORE Control`. A clean load prints the plugin version, one
`Installed mission '...'` line per shipped mission on first run (`CORE Control Duel` and
`CORE Control Duel Far`), and no `self-check FAILED` lines. The self-checks run at plugin load and are the mod's
only automated tests; a failed one means a tuning constant or price ladder is wrong.

## Keeping up with upstream

This fork tracks the original repository as the `upstream` remote:

```powershell
git fetch upstream
git merge upstream/main
```
