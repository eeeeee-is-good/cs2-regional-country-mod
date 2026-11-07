# Prototype 0 — "Shadow quote from two real cities"

**Goal:** prove the end-to-end data path with **zero writes to the game.** Two disposable CS2 cities each export a `CitySnapshot`. A hand-written manifest links them by road. The standalone engine then produces an explainable landed-cost quote for one resource in City B.

This covers handoff gate steps 1–4 and research gates A and B. It deliberately stops before Gate C, which is the first native economic effect.

**Out of scope:** any ECS, money or stock write; Harmony patches; in-game UI panels; in-save mod state; multiple resources; migration; fiscal; multiplayer; the atlas.

## Status (2026-11-06)

**The engine side is done and testable without the game. Game-side work is paused by choice;** the current focus is engine-only. Everything below is merged into `main` unless it says otherwise.

### Done
| Task | What exists | How to try it |
|---|---|---|
| J1 Contracts v1 | Schemas, C# DTOs, golden fixture `v1-basic`, 29 tests. Provisional until the second developer reviews it. | `dotnet test Regions.sln` |
| A1 Solution skeleton | `Regions.sln` builds and tests from a clean clone (.NET 8.0.427). **No CI script yet** (see To do). | `dotnet build Regions.sln` |
| A3 Engine | Route graph, landed-cost quotes with a per-offer explanation, input hash for replay. | `regions quote Contracts/golden/v1-basic` |
| A5 Simulator + invariant checker | Synthetic cities, built-in regions G01, G02, G03, G10 and `corridor`, checks for log §18.4 #1, #2, #3, #7, #14. | `regions scenarios`, `regions check <folder>` |
| A6 `regions` CLI | `quote`, `check`, `scenarios`, `scenario`, `ingest`. See [ScenarioRunner/README.md](../ScenarioRunner/README.md). | `dotnet run --project ScenarioRunner -- help` |
| A2 Region store | Inbox ingestion with duplicate, conflict and corrupt-file handling, atomic writes, crash recovery, hash-verified reads. See [RegionStore/README.md](../RegionStore/README.md). | `regions ingest <folder>` |
| B0 (static part) | Game 1.6.2f1 matches the research baseline; mod lifecycle and load/save signals confirmed in the game code; framework, JSON and `ModsData/` confirmed on disk. See [ASSUMPTIONS.md](ASSUMPTIONS.md). | — |

Tests: 583 pass (Contracts 29, Engine 358, Scenarios 154, RegionStore 42).

### Acceptance criteria
| # | Criterion | Status |
|---|---|---|
| 1 | `dotnet test` incl. G01, G02, G03, G06, G10 | **Met** |
| 2 | Replay byte-identical | **Met across runs and input orders.** Not yet compared across machines (macOS vs Windows), or in-game Mono vs CoreCLR (V-30). |
| 3 | No link means `NoPath`; world fallback only with a gateway | **Met** |
| 4 | Every quote has a trace; components sum to `landedPrice` | **Met** |
| 5–8 | Exporter JSON, G00 safety, stable `endpointKey`s, real two-city quote | **Need the game** |

### Parked (resume when doing game work)
- **B1 skeleton mod** on branch `feature/b1-mod-skeleton`, not merged. It builds against the installed game with 0 warnings, but has **never run in the game**. It was removed from the local Mods folder on 2026-11-06. To resume: rebuild it, then run the in-game test in its [README](../CS2Integration/README.md), which also completes the B0 runtime spikes (V-02 to V-05).

### To do: engine only (no game needed)
1. **A4 supply rule.** Decide the confidence factors for `Provisional` and `Unknown`, and how to treat a null capacity. The engine currently uses a temporary fail-closed rule: anything not `Validated`, or with a missing number, is `UnknownSupply`.
2. **A7 tariffs and subsidies** (G04, G05, G09). Pure math, labelled as forecasts.
3. **A1 leftover: CI script**, for example a GitHub Actions workflow running `dotnet build` and `dotnet test` on every push.
4. **Open decisions to review:**
   - RegionStore: the net8.0 target, its extra rejection rules, and the exporter/inbox protocol question.
   - Contracts README "Open items": `MaxRegionSeqAge`, `saveLineageId`, and the `positionMetres` convention.
5. **Optional:** G11 (50-city performance), G08 (the same city registered in two regions).

### To do: needs the game
- **B0 runtime spikes + B1:** the branch is ready; it needs one in-game run.
- **B2–B6:** capability report, outside-connection reader, one-resource reader, snapshot exporter, G00 safety run.
- **J2–J4:** two disposable test cities, real two-city quote, reload and conflict test.

## Acceptance criteria (all must be demonstrated, not just compiled)

1. `dotnet test` passes, including golden scenarios **G01** (no link means no benefit), **G02** (road link means an eligible offer), **G03** (road vs rail cost), **G06** (a repeated snapshot changes nothing) and **G10** (zero or huge distance, missing mode, cycles: no NaN, negative cost or infinite loop).
2. **Replay:** identical inputs produce a byte-identical `QuoteResult` and the same `provenance.inputsSha256`, across runs and machines.
3. Deleting the link makes A's offer `eligible:false, excludedReason:"NoPath"`. B falls back to World only if B has a world gateway.
4. Every quote has a trace listing all offers considered, and the price components sum to `landedPrice`.
5. The in-game exporter's JSON validates against `Contracts/schema/city-snapshot.v1.schema.json` and round-trips through the engine-side deserializer.
6. **G00 read-only safety:** on a disposable save, running with the mod enabled over N in-game days shows no differences from mod-disabled runs beyond the noise band. The noise band is measured from two vanilla runs. The save file changes only when the user saves. The log shows no exceptions. Timings are recorded.
7. Outside-connection `endpointKey`s are identical after save → quit → reload.
8. Using real snapshots from both cities, the ScenarioRunner prints a quote and trace marked `appliedToGame:false`.

## Task split

> **Staffing (2026-11-06):** one developer currently does both tracks; a second developer is expected to join later (see CLAUDE.md "Current staffing"). The A/B split stays as the plan for when they do. "Awaiting review by both developers" below means awaiting self-review now, and review by the second developer once they join.

### Joint, first (½–1 day, blocking): **J1 Contracts v1**: drafted 2026-11-04 in `Contracts/`; builds, 29 contract tests pass (.NET 8.0.427, macOS arm64); awaiting review by both developers
Agree on and merge `Contracts/`: DTOs, JSON schema, the resource-id table, and golden files covering 2 cities, 1 road link, 1 rail link, 1 world gateway and 1 resource. Field shapes are in ARCHITECTURE.md §5. **After merge, either developer changes Contracts only through a PR the other reviews.**
Dev B runs the V-02/V-03 spikes first, because they decide whether the mod can reference `Contracts.dll` directly. The fallback is to compile the Contracts sources into the mod and write the JSON by hand there.

### Developer A — pure engine (no CS2 needed)
| ID | Task | Done when |
|---|---|---|
| A1 | Solution skeleton (`netstandard2.0` libs, `net8` tests/CLI), CI script, golden round-trip tests | `dotnet build && dotnet test` green from a clean clone |
| A2 | `RegionStore`: inbox ingestion per Contracts/README.md, atomic writes, index, conflict reporting; moves the engine-side JSON options out of the tests | Unit tests for duplicate, older, conflicting and corrupt-file cases. **Done 2026-11-06** (merged): `FileRegionStore` (create/open, optimistic manifest writes, inbox ingestion, verified reads, crash recovery) and the single `ContractJson`; 42 store tests incl. store-level G06 (#8). `regions ingest` added; scenario folders are now written through real ingestion. Decisions to review in [RegionStore/README.md](../RegionStore/README.md), including the net8.0 target and an open exporter-protocol item for B5. |
| A3 | Route graph + landed-cost solver (road, rail, world fallback), mode compatibility, trace | G01, G02, G03, G10 pass. **Done 2026-11-06** (merged): `RegionalEngine/` reproduces the v1-basic golden quote; G01–G03, G10, replay and 300 seeded random-graph property tests (brute-force path check) pass (.NET 8.0.427, Windows x64). Uses an interim fail-closed supply rule (non-Validated or null input = `UnknownSupply`) until A4. |
| A4 | Supply eligibility: `min(surplus, capacity) × confidence`, staleness exclusion, null = unknown | Tests show stale, null and zero cases behave as specified |
| A5 | `CitySimulator` + scenario fixtures + invariant checker (§18.4 #1, 2, 3, 7, 8, 14) + replay test | G06 and replay pass; property tests on random graphs. **Done 2026-11-06** (merged): `SyntheticCity`, `RegionScenario`, built-in scenarios G01, G02, G03, G10 and `corridor`; `InvariantChecker` covers #1, #2, #3, #7, #14 and is proven against deliberately broken engines; passes on every scenario and 100 random regions. G06 is tested at the quote layer only. **#8 and store-level G06 (identical re-ingestion is a no-op) move to A2.** |
| A6 | `ScenarioRunner` CLI: fixture folder in, quotes and traces out (JSON + readable text) | Runs against the golden fixtures. **Done 2026-11-06** (merged): `regions quote / check / scenarios / scenario`; validates inputs against the schemas and snapshot hashes against the index; JSON output byte-identical to the engine's quote. See [ScenarioRunner/README.md](../ScenarioRunner/README.md). |
| A7 | *Stretch:* foreign import tariff eligibility (G04) and corridor subsidy forecast with budget cap (G05, G09). Pure math, labelled as forecast. | Tests pass |

### Developer B — read-only CS2 mod (needs the game)
| ID | Task | Verifies | Done when |
|---|---|---|---|
| B0 | Install the toolchain. Record the CS2 build. Run the spikes for framework, serializer and writable folder. Vet agents-plugins before installing. | V-01–V-05, V-29 | Findings written into ASSUMPTIONS.md. **Static part done 2026-11-06:** game 1.6.2f1 matches the baseline (V-01); lifecycle signatures and the load/save signals confirmed by decompiling (V-05); framework, Newtonsoft and `ModsData/` confirmed on disk (V-02–V-04, partly verified). **Runtime spikes still to run**, folded into the B1 skeleton mod (it loads the Contracts DLL, round-trips JSON, writes to `ModsData/`, logs the lifecycle events). |
| B1 | Mod skeleton: loads, logs its version, disposes cleanly, registers no writers | V-05 | Clean load/unload in log. **Parked 2026-11-06** on `feature/b1-mod-skeleton`: builds, never run in game (see Status). |
| B2 | `CapabilityProbe`: game build, active mods, every feature `shadow/off` with a reason, written as JSON | V-26 | Report file produced |
| B3 | Outside-connection inspector: transport and utility OCs, mode, position, `endpointKey` fingerprint; reload test on a controlled map | V-06–V-08 | Criterion 7 met |
| B4 | One-resource reader: windowed production, producer count, confidence; `null` when unsure | V-10–V-13 | Values cross-checked against the in-game UI over several days |
| B5 | Snapshot exporter: creates `inbox/<cityId>.<seq>.json` on save or explicit trigger, following the Contracts/README.md serialization rules | V-04, V-05 | Criterion 5 met |
| B6 | G00 safety and performance run, recorded in `testlogs/` | V-14, V-16 | Criterion 6 met |

### Joint, last
- **J2** (either developer, early): build two disposable cities. City A is a producer of the chosen resource with a road edge OC. City B is a consumer with road and world access. Keep save copies **outside Git** and record their checksums.
- **J3 Integration:** export both cities, hand-write the manifest using real `endpointKey`s, run the ScenarioRunner. Meets criterion 8.
- **J4 Lifecycle:** re-export after reload (`seq` increments, publish is idempotent); load an older save and confirm a visible conflict.

**Dependencies:** J1 → (A1–A7 ∥ B0–B6) → J3 → J4. J2 can run any time before J3. The two tracks share no files except `Contracts/`.

## Next, after Prototype 0 passes
**Gate C pilot:** close V-17 and V-18 by decompiling the installed build. Then apply one ±5% resource-scoped input-cost modifier in one city, with a kill switch, and compare control against treatment (§19.4). If no safe hook exists, stay read-only and build the Freeform atlas instead.
