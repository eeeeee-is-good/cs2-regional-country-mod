# Verification register — assumptions not yet checked against CS2

Every item comes from secondary research: community decompile analysis of **v1.6.2f1**, READMEs and dev diaries. **None of it has been confirmed against the installed game.** Code may rely on an item only after its status is `verified`, with the build and evidence recorded here.

Status values: `open` · `verified (build, date, evidence link)` · `false (what's actually true)` · `n/a`.
"Blocks" refers to task IDs in [PROTOTYPE_PLAN.md](PROTOTYPE_PLAN.md). Research log section in brackets.

## Toolchain and environment (check first)

| ID | Assumption | Blocks | Status |
|---|---|---|---|
| V-01 | Installed CS2 build matches the 1.6.2f1 research baseline, or the differences are known [R-14] | all B tasks | open |
| V-02 | Mods built with the official toolchain target .NET Framework 4.8 / Unity Mono and can reference a `netstandard2.0` library DLL shipped beside the mod | B1, J1 | open |
| V-03 | A JSON serializer is safely loadable in-game (Newtonsoft bundled? `Colossal.Json`?) without version conflicts with other mods | B5, J1 | open |
| V-04 | A writable per-user folder exists for external region state (e.g. `ModsData/`) and normal file IO from a mod works there | B5 | open |
| V-05 | Lifecycle: `IMod.OnLoad(UpdateSystem)` / `OnDispose()`; systems can be registered in a chosen update phase; a reliable "city loaded" and "save completed" signal exists [§3.6] | B1, B5 | open |
| V-30 | Engine results are bit-identical on Mono (in-game) and CoreCLR (tests) for the operations used | J3 | open |
| V-29 | Third-party tooling (CitiesSkylinesModding agents-plugins, unity-devtools, coherent-gameface, both CS2 MCP servers) works with the retail build and is safe to run. **Read the code before installing.** [§28.1] | optional | open |

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
