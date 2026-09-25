# CORE Control

BepInEx 5 plugin for Nuclear Option (Unity 2022.3, `net472`, Harmony). It adds an RTS command layer
on top of the game: free camera, selection and orders, production and economy, air tasking, and AI
commanders for any faction. Player-facing behaviour is in `README.md`, build and install details in
`BUILD.md`.

Fork of AMAUKDev/RTS-Commander ("Ground Control (RTS)"), git remote `upstream`. Upstream is a source
to cherry-pick from. The C# namespace is still `GroundControlRts` so cherry-picks apply cleanly.

## Identity

`Core/PluginInfo.cs` holds every name that must differ from upstream: the GUID
`com.wadl3r.corecontrol` (also the Harmony id and the config file name), the display name, the
shipped mission names, the save folder name, and the upstream GUIDs that `[BepInIncompatibility]` in
`Core/CommanderPlugin.cs` refuses to load beside. The version string appears in `COREControl.csproj`,
`Core/AssemblyInfo.cs` and `Core/PluginInfo.cs` (the one BepInEx logs).

## Build

```
dotnet build -c Release -p:GameDir="D:\Steam\steamapps\common\Nuclear Option"
```

- `GameDir` can also come from `NUCLEAR_OPTION_DIR`. Game assemblies are referenced from the install
  and never copied. The build currently has 0 warnings.
- `.claude/check.cmd` runs the same build quietly in about 2 s; its exit code is the result.
- `build-and-install.ps1` (double-click: `build-release.bat`) builds and copies `bin\Release\net472\*`
  into `BepInEx\plugins\COREControl\`. The mission JSON files must sit beside the DLL. Close the game
  first; it locks the loaded DLL.
- `build-dev.bat` hot-reloads into `BepInEx\scripts\` and needs the ScriptEngine plugin from
  BepInEx.Debug, which this install does not have.

## Layout

One folder per subsystem. Large services are split into partial classes by concern
(`CommanderEconomyService.cs`, `CommanderEconomyServiceEnemy.cs`, `CommanderEconomyServiceCatalog.cs`).

| Folder | Contents |
| --- | --- |
| `Core` | Plugin entry, service registry, scheduler, settings, feature gate, game-access wrapper, save stores |
| `Units` | Selection, groups, move/attack orders, formations, markers, alerts, radar, repair |
| `Camera`, `Map`, `UI` | Free and POV camera; tactical map; IMGUI windows including settings |
| `Depot`, `Economy`, `Naval` | Ground purchases; buildings, mines, factories; naval dock and ship purchases |
| `AirCommand` | Air missions for player and AI, driven through the game's pilot AI |
| `Supply` | Helicopter cargo runs and airdrops |
| `Ai` | Enemy and player-side AI commanders: buying, defence, capture, air roster |
| `Operations` | AI platoons, front line, offensives, forward bases, air packages (32k lines, the largest) |
| `Points` | Strategic points: discovery, holding, income |
| `SamSites`, `Terrain` | SAM site analysis and construction; height-map sampling |
| `Mission` | The two shipped duel missions |

## Architecture

- **Services.** A feature is a class implementing some of the hook interfaces in
  `Core/ICommanderService.cs`: `ICommanderActivate`/`ICommanderDeactivate` (entering and leaving RTS
  mode), `ICommanderTickActive` (every frame in RTS mode), `ICommanderTickPersistent` (every frame,
  including while the player flies), `ICommanderResetSession` (scene change), and
  `ICommanderPersistState`/`ICommanderPersistStrategic` for saves. They are registered in
  `CommanderModeController.Awake`; registration order is execution order.
- **Tiers.** `CommanderTier.Advanced` services run only when `CommanderFeatureGate` passes: the
  mission name contains Altercation, Confrontation, Domination, Escalation, Terminal Control or the
  duel name, or the player pressed UNLOCK ALL FEATURES. Everything else is `CommanderTier.Core`.
- **Time.** `CommanderScheduler.IsDue` runs on scaled time, so it stops when paused and speeds up at
  2x/4x. `IsDueRealtime` is for UI refresh. `Stagger` spreads first runs apart.
- **Settings.** BepInEx config entries in `Core/CommanderSettings.cs`, each a property over
  `Get<T>(section, key, default)` / `Set`, plus a `_ = Property;` line in `Initialize` so the entry is
  written to the config file on first run. The settings window is `UI/CommanderOverlayUiSettings.cs`.
- **Game internals.** `Core/CommanderGameAccess.cs` wraps most of them. Harmony patches live in
  `*Patches.cs` next to the service they serve; `harmony.PatchAll()` in `CommanderPlugin.Awake`
  applies them all.
- **AI commanders.** Review loops iterate `FactionRegistry.GetAllHQs()` and ask
  `CommanderPlayerCommanderService.IsCommanded(hq, localHq)`: hostile factions follow the enemy
  commander setting, the player's own faction follows the player commander switch.
- **Files on disk.** `persistentDataPath` is `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption`.
  The hot-reload snapshot and the strategic save are JSON in its `COREControlState\` folder
  (`CommanderStateStore.StatePathFor`). `CommanderMissionInstaller` copies the shipped missions into
  its `Missions\<name>\` on every normal load and overwrites them when the shipped copy changes.

## Engine facts

- **String-named game members are not compile-checked.** 26 Harmony targets are given as
  `[HarmonyPatch(typeof(X), "name")]`, and about 75 private members are reached through
  `AccessTools` or `GetField` by name. After a game update, a missing Harmony target throws out of
  `PatchAll` before the mode controller exists, which leaves the mod dead. A missing field makes its
  lookup null, or throws from the type initializer for `FieldRefAccess`. Check them against
  `NuclearOption_Data\Managed\Assembly-CSharp.dll` whenever the game updates.
- **Server authority.** `FactionHQ.AddSupplyUnit` and `Spawner.SpawnUnit` are Mirage `[Server]`
  methods and throw on a pure multiplayer client. `FactionHQ.AddFunds` and `ModifyUnitSupply` do not
  throw; on a client they write local synced state and silently desync. That is why building,
  spawning and AI commanders are host-only and the code checks `hq.IsServer`.
- **Mission files.** The game's mission JSON converters go up to `JsonVersion` 6, the version the
  shipped missions use. The game finds a mission's start objective by the name `Mission Start`
  (`MissionObjectivesFactory.MissionStartName`); `CommanderMissionInstaller` warns if it is missing.

## Testing

- There is no test project. 23 subsystem `SelfCheck()` methods and `CommanderServiceRegistryCheck.Run()`
  run from `CommanderPlugin.Awake` and log `... self-check FAILED ...` as errors.
- In-game check: install, launch Nuclear Option, host `CORE Control Duel`, then read
  `D:\Steam\steamapps\common\Nuclear Option\BepInEx\LogOutput.log`. A clean load logs
  `CORE Control 0.7.6.0 loaded`, and on first run one `Installed mission '...'` line per shipped mission.
