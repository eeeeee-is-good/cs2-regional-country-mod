# Prototype 0 — "Shadow quote from two real cities"

**Goal:** prove the end-to-end data path with **zero writes to the game.** Two disposable CS2 cities each export a `CitySnapshot`. A hand-written manifest links them by road. The standalone engine then produces an explainable landed-cost quote for one resource in City B.

This covers handoff gate steps 1–4 and research gates A and B. It deliberately stops before Gate C, which is the first native economic effect.

**Out of scope:** any ECS, money or stock write; Harmony patches; in-game UI panels; in-save mod state; multiple resources; migration; fiscal; multiplayer; the atlas.

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

### Joint, first (½–1 day, blocking): **J1 Contracts v1**: drafted 2026-11-04 in `Contracts/`; builds, 29 contract tests pass (.NET 8.0.427, macOS arm64); awaiting review by both developers
Agree on and merge `Contracts/`: DTOs, JSON schema, the resource-id table, and golden files covering 2 cities, 1 road link, 1 rail link, 1 world gateway and 1 resource. Field shapes are in ARCHITECTURE.md §5. **After merge, either developer changes Contracts only through a PR the other reviews.**
Dev B runs the V-02/V-03 spikes first, because they decide whether the mod can reference `Contracts.dll` directly. The fallback is to compile the Contracts sources into the mod and write the JSON by hand there.

### Developer A — pure engine (no CS2 needed)
| ID | Task | Done when |
|---|---|---|
| A1 | Solution skeleton (`netstandard2.0` libs, `net8` tests/CLI), CI script, golden round-trip tests | `dotnet build && dotnet test` green from a clean clone |
| A2 | `RegionStore`: inbox ingestion per Contracts/README.md, atomic writes, index, conflict reporting; moves the engine-side JSON options out of the tests | Unit tests for duplicate, older, conflicting and corrupt-file cases |
| A3 | Route graph + landed-cost solver (road, rail, world fallback), mode compatibility, trace | G01, G02, G03, G10 pass. **Drafted 2026-11-06** on `feature/a3-regional-engine`: `RegionalEngine/` reproduces the v1-basic golden quote; G01–G03, G10, replay and 300 seeded random-graph property tests (brute-force path check) pass (.NET 8.0.427, Windows x64). Uses an interim fail-closed supply rule (non-Validated or null input = `UnknownSupply`) until A4. Awaiting review. |
| A4 | Supply eligibility: `min(surplus, capacity) × confidence`, staleness exclusion, null = unknown | Tests show stale, null and zero cases behave as specified |
| A5 | `CitySimulator` + scenario fixtures + invariant checker (§18.4 #1, 2, 3, 7, 8, 14) + replay test | G06 and replay pass; property tests on random graphs |
| A6 | `ScenarioRunner` CLI: fixture folder in, quotes and traces out (JSON + readable text) | Runs against the golden fixtures |
| A7 | *Stretch:* foreign import tariff eligibility (G04) and corridor subsidy forecast with budget cap (G05, G09). Pure math, labelled as forecast. | Tests pass |

### Developer B — read-only CS2 mod (needs the game)
| ID | Task | Verifies | Done when |
|---|---|---|---|
| B0 | Install the toolchain. Record the CS2 build. Run the spikes for framework, serializer and writable folder. Vet agents-plugins before installing. | V-01–V-05, V-29 | Findings written into ASSUMPTIONS.md |
| B1 | Mod skeleton: loads, logs its version, disposes cleanly, registers no writers | V-05 | Clean load/unload in log |
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
