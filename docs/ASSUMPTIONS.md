# Verification register — assumptions not yet checked against CS2

Every item comes from secondary research: community decompile analysis of **v1.6.2f1**, READMEs and dev diaries. Items stay unconfirmed until checked against the installed game. Code may rely on an item only after its status is `verified`, with the build and evidence recorded here.

Status values: `open` · `partly verified (what is confirmed; what still needs a runtime check)` · `verified (build, date, evidence)` · `false (what's actually true)` · `n/a`.
"Blocks" refers to task IDs in [PROTOTYPE_PLAN.md](PROTOTYPE_PLAN.md). Research log section in brackets.

## Installed environment (B0, checked 2026-11-06)

| Item | Value | Source |
|---|---|---|
| Game | **1.6.2f1 (767.21d1) [6300.26419]**, Steam build id 25127643 | `Player.log` "Game version", `appmanifest_949230.acf` |
| Unity engine | 2022.3.71f1 | `Player.log` |
| Modding toolchain | Installed. `Mod.props`: `TargetFramework net48`, `LangVersion 9.0`. Entities 1.3.10 source generators present. .NET Framework 4.8 reference assemblies present. | `%CSII_TOOLPATH%` |
| Toolchain Unity project | 2022.3.62f2, older than the engine. Probably harmless for code-only mods; update it from the game's Options > Modding if a build or post-process fails. | `UnityModsProject/ProjectSettings/ProjectVersion.txt` |
| Paths | Managed DLLs: `…\steamapps\common\Cities Skylines II\Cities2_Data\Managed`. User data: `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II`. Local mods: `<user data>\Mods`. | `CSII_*` user environment variables |
| Mods active in the last session | Many, including Harmony-based ones, Move It, and mods that change statistics profiles (RWH, Time2Work and others under `ModsData/`). The user's own local mods (EconomyObserver, RoadSignGenerator) build and load. | `Logs/Modding.log`, `ModsData/` |

**Consequence for G00 and L3 runs:** use a playset with only this mod enabled, or record every active mod in `testlogs/`. Profiles such as `occupancy` and `mobility` must not be reported as `Vanilla` while RWH or Time2Work are active.

## Toolchain and environment (check first)

| ID | Assumption | Blocks | Status |
|---|---|---|---|
| V-01 | Installed CS2 build matches the 1.6.2f1 research baseline, or the differences are known [R-14] | all B tasks | **verified** (1.6.2f1 (767.21d1), 2026-11-06, `Player.log`). Matches the baseline. Steam can update the game at any time: re-check this line before every L3 run (log §21.5). |
| V-02 | Mods built with the official toolchain target .NET Framework 4.8 / Unity Mono and can reference a `netstandard2.0` library DLL shipped beside the mod | B1, J1 | **partly verified** (1.6.2f1, 2026-11-06). Confirmed: the toolchain targets `net48`; the game's `Managed/` has Mono `mscorlib` 4.0.0.0 (file 4.6.57) and a `netstandard.dll` 2.1.0.0 facade, so `netstandard2.0` references should resolve. Not yet seen at runtime: no netstandard mod was loaded in the last session. **Runtime check in B1:** the skeleton mod ships `Regions.Contracts.dll` and logs a Contracts type. |
| V-03 | A JSON serializer is safely loadable in-game (Newtonsoft bundled? `Colossal.Json`?) without version conflicts with other mods | B5, J1 | **partly verified** (1.6.2f1, 2026-11-06). Confirmed: the game ships **Newtonsoft.Json 13.0.2** (assembly 13.0.0.0) in `Managed/`, and its own `Colossal.Json` (in `Colossal.Core.dll`). System.Text.Json is **not** shipped. Plan: reference the game's Newtonsoft with `Private=false` and ship no copy, so this mod adds no version conflict. Newtonsoft writes `100.0` where System.Text.Json writes `100`, and indents with CRLF on Windows; the exporter must write LF (Contracts rule 1). Number formatting is fine: files are hashed as written, and comparisons use a tolerance (rule 7). **Runtime check in B1:** round-trip a `CitySnapshot` and validate the output on the engine side. |
| V-04 | A writable per-user folder exists for external region state (e.g. `ModsData/`) and normal file IO from a mod works there | B5 | **partly verified** (1.6.2f1, 2026-11-06). Confirmed: `Colossal.PSI.Environment.EnvPath.kUserDataPath` is the user data folder, and `<user data>/ModsData/<ModName>/` is used by 22 installed mods on this machine. Proposed region root: `ModsData/Regions/<regionId>/`. **Runtime check in B1:** write, rename and read back a file there. macOS and Linux are untested. |
| V-05 | Lifecycle: `IMod.OnLoad(UpdateSystem)` / `OnDispose()`; systems can be registered in a chosen update phase; a reliable "city loaded" and "save completed" signal exists [§3.6] | B1, B5 | **verified (signatures)** (1.6.2f1 decompile, 2026-11-06). `Game.Modding.IMod` is exactly `void OnLoad(UpdateSystem)` and `void OnDispose()`. `UpdateSystem` has `UpdateAt/UpdateBefore/UpdateAfter(SystemUpdatePhase)`; phases include `GameSimulation`, `PostSimulation`, `UIUpdate`, `Serialize`, `Deserialize`. City loaded: override `GameSystemBase.OnGameLoadingComplete(Purpose, GameMode)`, or the event `GameManager.onGameLoadingComplete`. Save completed: `GameManager.Save(...)` raises `onGameSaveLoad(saveName, previewUri, start: true, success: true)` before writing, and `onGameSaveLoad(saveName, previewUri, start: false, saveSuccess)` after the save package is written. **Runtime check in B1:** whether `onGameSaveLoad` also fires on load (its name suggests so); the exporter should react only to `start == false && success` while a city is loaded. |
| V-30 | Engine results are bit-identical on Mono (in-game) and CoreCLR (tests) for the operations used | J3 | open |
| V-29 | Third-party tooling (CitiesSkylinesModding agents-plugins, unity-devtools, coherent-gameface, both CS2 MCP servers) works with the retail build and is safe to run. **Read the code before installing.** [§28.1] | optional | open. **Decision 2026-11-06:** none of the listed tools are installed for this project. Already present on the developer's machine, not installed by this project and **not code-reviewed**: an ILSpy MCP server and a "cities2" MCP server (wiki corpus and project tools). The ILSpy server was used read-only to decompile game DLLs for V-05. Rule until reviewed: read-only decompilation only; nothing that drives the game or writes into the game, `Mods/` or a save. |

## Outside connections

| ID | Assumption | Blocks | Status |
|---|---|---|---|
| V-06 | Placed transport OCs are queryable by tag `Game.Objects.OutsideConnection`; mode comes from `OutsideConnectionData.m_Type`, a flags enum `Road=1, Train=2, Air=4, Ship=0x10` [§3.1] | B3 | open |
| V-07 | Utility OCs (`ElectricityOutsideConnection`, `WaterPipeOutsideConnection`) have a different component shape and need separate queries [§3.1] | B3 | open |
| V-08 | An OC's world position, type and network attachment give a fingerprint that is stable across save/reload and identifies the same gate after nearby road edits [R-01] | B3, J4 | open |
| V-28 | OC positions are readable in metres in a map-local frame that can be transformed into the country frame [map spec] | later (atlas) | open |
| V-09 | Road Builder roads appear as ordinary net entities to OC and connectivity queries [§24.3] | later | open |
| V-31 | `m_Remoteness` is off-map geometry, not distance or price; `m_Commuting` is unused [§3.1] | n/a (don't use them) | open |

## Statistics (the snapshot's source data)

| ID | Assumption | Blocks | Status |
|---|---|---|---|
| V-10 | Resource identifiers (36 resources) and units, including the exact name of the prototype resource (e.g. Steel) | J1, B4 | open |
| V-11 | `CityProductionStatisticSystem` production counters are usable after smoothing; per-consumer **consumption categories are incomplete**, so they are not ground truth [§14.3] | B4 | open |
| V-12 | Companies update over `UpdateFrame` (256 per game day), so any instant read is a partial observation and needs a window [§14.3] | B4 | open |
| V-13 | Company count per resource, and producer company identity, can be queried cheaply | B4 | open |
| V-14 | Periodic aggregate reads do not perturb the simulation (G00 shadow equivalence) and have acceptable cost on a large city | B6 | open |
| V-32 | `Profitability` / `CompanyEconomyStatisticSystem.m_Profit` are projections, not cash ledgers [§14.3] | n/a (don't use as truth) | open |

## Persistence and save safety

| ID | Assumption | Blocks | Status |
|---|---|---|---|
| V-15 | Mod state persists via `ISerializable`/`IDefaultSerializable`; saves still load after the mod is removed; mod-owned marker entities may linger across city loads unless cleaned [§3.6] | in-save identity (after P0) | open |
| V-16 | The save file for the current city can be located and hashed, so the "closed save is byte-identical" test can run [§8A] | B6 | open |

## Economic integration (blocks Gate C, not Prototype 0)

| ID | Assumption | Status |
|---|---|---|
| V-17 | A **resource-scoped, city-local** import/input-cost path exists that native companies actually use and that can be modified without a global price edit [R-03, §16.7]. The Adwozo price-getter postfixes are a precedent; their signatures have since changed. | open |
| V-18 | Which relevant systems are Burst-compiled, and therefore not Harmony-patchable [R-11] | open |
| V-19 | Vanilla external trade credits sellers without debiting anyone; OC stock gravitates to a target [§3.2] | open |
| V-20 | Per-resource tax range is −10%..+30%; `TaxSystem` uses a 92-slot array; demand systems read per-resource taxes [§15.1, §3.3] | open |
| V-21 | `CommercialDemandSystem`/`IndustrialDemandSystem` compute per-resource company demand; `ZoneSpawnSystem` uses it [§14.4] | open |
| V-22 | `PlayerMoney` has several writers besides `BudgetApplySystem` [§3.3] | open |
| V-23 | Completed household move-in/move-out events are observable; a `MovingAway` tag alone is not completion [§26.8] | open |
| V-24 | Commuter/tourist spawning can be modulated per gate [R-07] | open |

## Mod compatibility and map

| ID | Assumption | Status |
|---|---|---|
| V-25 | MapExt sizes 28/43/57 km; MapExt vanilla-save conversion **removes OCs**; EconomyEX discourages Realistic Trips [§24] | open |
| V-26 | Active mod IDs and versions can be enumerated at runtime for the capability report [§23.4] | open (B2 tries it) |
| V-27 | Map editor imports 4096×4096 16-bit heightmaps over ~14,336 m; the pixel-centre convention is unknown [map spec] | open |
