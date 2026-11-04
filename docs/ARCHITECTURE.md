# Architecture — CS2 Regional/Country Mod ("Regions", placeholder name)

**Status:** Agreed design, distilled from research log v2.0 (§27 closeout, §28 handoff, §29) and the map architecture v2.0. **Nothing in this document has been verified against a running CS2 build.** Game class names are research leads, tracked in [ASSUMPTIONS.md](ASSUMPTIONS.md); they are not confirmed APIs.
**Last updated:** 2026-11-04

## 1. Product in one sentence

Separate CS2 city saves form one connected country. Roads, rail and specialization let cities influence each other through a **transport-aware soft market**. Only the loaded city runs native CS2. The other cities exist as their latest published snapshots.

First playable target (§27): **two cities, one route, one resource, one observable consequence**, with "preview" clearly separated from "applied to native companies".

## 2. Non-negotiable decisions (do not regress)

| # | Decision | Supersedes |
|---|---|---|
| N1 | Trade is a **soft price/access signal market** (D-17, D-21, D-35). No exact bilateral shipments, no escrow, and no private-company cross-city money. | Mandatory bilateral stock/money ledger, escrow and two-step delivery from v0.1–v0.3 (§4 `RegionalEconomy`, §6, §6A, §7, D-08, D-11). Those survive only as an *optional future strict mode*. |
| N2 | Market access depends on **city + resource + mode + route**. A supplier with no valid path has no influence (D-32, D-33, D-36). Map adjacency is not a path. | Universal region-wide price (§13.2 sketch) |
| N3 | **Never mutate an unloaded save.** Region state lives in its own external store (D-12). | — |
| N4 | **No catch-up replay on load.** Converge on *current* conditions through normal ticks, bounded and smoothed (D-13, §8A). | — |
| N5 | An unloaded city's snapshot is a **decaying estimate**, never accumulating stock (D-19, D-39). | — |
| N6 | Use native per-resource taxes and demand. Never overwrite RCI bars or add duplicate tax penalties (D-23, D-26–D-29, D-37). | — |
| N7 | Migration is an **observational ledger**. It never creates or deletes citizens (§26, D-10). | Cross-save citizen transfer |
| N8 | Federal money is a **separate ledger** until cash settlement is verified. Forecasts are labelled as forecasts (D-43, D-46). | — |
| N9 | Multiplayer is **asynchronous publication** of independently owned saves. It is not co-op (§22). | Live sync |
| N10 | Every game hook is a **capability** that fails closed, and the default is shadow mode (§21.4, §23.4). | — |
| N11 | Supported compatibility is an explicit list (§24.1) and is a target, not a verified result. MapExt + Realistic Trips together is a known upstream risk. | Universal compatibility |
| N12 | Region map: the **Freeform atlas is the MVP**. Shared Geography is a separate future prototype. Multiple live cities in one CS2 world is a non-goal. | "Placement is purely conceptual" (§5) |

## 3. Components and boundaries

```
                  ┌──────────────────────── pure .NET (no game refs) ───────────────────────┐
                  │                                                                        │
  CitySimulator ──┤──► Contracts ◄── RegionStore ◄──► RegionalEngine ──► Quote + Trace ───┼─► CLI / tests
  (synthetic)     │   (schemas, DTOs)  (files,         (graph, quotes,                     │
                  │                     atomic,         invariants)                        │
                  │                     idempotent)                                        │
                  └──────────────▲─────────────────────────────────────────▲───────────────┘
                                 │ CitySnapshot JSON                        │ Quote (shadow) / bounded modifier (later)
                  ┌──────────────┴──────────── CS2 process (version-sensitive) ───────────┴───┐
                  │ CS2Integration: CapabilityProbe → Readers → SnapshotExporter               │
                  │                 (later) gated EffectWriter     RegionalUI (Gameface, later)│
                  └────────────────────────────────────────────────────────────────────────────┘
```

| Component | Responsibility | Must NOT | Owner |
|---|---|---|---|
| **Contracts** | Versioned DTOs and JSON schema: `CitySnapshot`, `RegionManifest`, `QuoteRequest/QuoteResult`, `CapabilityReport`, `StoreIndex`. Golden sample files. | Reference game assemblies or a serializer that has not been verified to load in-game (V-03) | **Joint.** Changes need both developers. |
| **RegionalEngine** | Deterministic pure functions covering manifest validation, the route graph, landed-cost quotes, supply eligibility and staleness, policies (later), traces and invariant checks | Do IO, use the clock or unseeded randomness, or reference `Game.*`/Unity/Harmony | Dev A |
| **RegionStore** | Reading and writing the manifest and snapshots in an external folder. Atomic write (temp + rename), immutable snapshot revisions, idempotent ingestion keyed on `(cityId, seq, fileSha256)`, and later an append-only journal. | Touch `.cok` or other save files | Dev A (interface jointly agreed) |
| **CitySimulator** | Builds synthetic `CitySnapshot`s and the golden scenarios G00–G13 (§18.6) | Become a second economy engine | Dev A |
| **ScenarioRunner (CLI)** | Loads a fixture folder, runs the engine and prints quotes and traces. This is the integration point before any UI. | — | Dev A |
| **CS2Integration** | The **only** code that touches the game. It provides the capability probe (build fingerprint, active mods, feature status), read-only readers (outside connections and one resource's statistics), and a snapshot exporter that writes Contracts JSON. Later it adds a single gated `EffectWriter`. | Write to ECS/money/stock in Prototype 0, persist anything inside the save before V-15 is verified, or run engine logic itself (it calls the engine library) | Dev B |
| **RegionalUI** | Freeform atlas, city cards, link editor and the "Why this price?" panel (Gameface/React) | Required for Prototype 0. A standalone atlas (map spec M0) is optional. | Later |

**Feature modes** (§23.4), each independently switchable: `REGISTRY_READ` (default), `TRANSPORT_GRAPH`, `MARKET_SHADOW`, `MARKET_WRITE`, `FISCAL_SHADOW/WRITE`, `POPULATION_SHADOW/WRITE`. Prototype 0 uses only `REGISTRY_READ` + `TRANSPORT_GRAPH` + `MARKET_SHADOW` (and the shadow quote is computed outside the game).

## 4. Data flow

**Loaded city (normal play):**
1. `CapabilityProbe` records the game build, the active mods and each feature's status. Any unverified feature drops to shadow or off.
2. Readers sample aggregated data periodically. They do not scan every tick.
3. On save or explicit export, `SnapshotExporter` creates `inbox/<cityId>.<seq+1>.json`. RegionStore ingests it and assigns a `regionSeq`.
4. RegionalEngine combines the manifest, the latest snapshot per city and the parameters, and produces a `QuoteResult` + trace per (city, resource).
5. In shadow mode, quotes are displayed or logged with `appliedToGame: false`. *(Later, Gate C)*: one bounded, smoothed, resource-scoped modifier is passed to `EffectWriter`.

**Unloaded city:** it has only its last snapshot. Its influence decays with age (and is excluded beyond a threshold). There is no production, there are no events, and its save is never touched.

**Reload:** RegionStore checks lineage (`seq`/`parentSeq`/`gameDays`). An older save than the latest published snapshot produces a visible conflict and read-only sync. Current conditions converge gradually; history is never replayed.

## 5. Contracts v1 (J1, drafted 2026-11-04, awaiting review by both developers)

The source of truth is [`Contracts/`](../Contracts/README.md): JSON schemas in `schema/`, golden files in `golden/`, and C# DTOs in `src/` that mirror the schemas. The decisions it fixes:
- `null` means unknown, never zero. Writers emit every property, including explicit nulls. Enums are PascalCase strings. Documented arrays are ordinal-sorted, so identical state produces identical bytes.
- Snapshot identity is `(cityId, seq)`, with lineage checked through `parentSeq` and `gameTime.gameDays`. Hashes are SHA-256 of the **exact file bytes** (no canonical JSON).
- Exporters publish only by **creating** `inbox/<cityId>.<seq>.json`. RegionStore ingests the file, assigns `regionSeq`, and reports conflicts (duplicate seq with a different hash, wrong `parentSeq`, or game-time rollback).
- Staleness is counted in region ingestions (`maxRegionSeqAge`). Independent city clocks are never compared.
- A quote has one offer per supplier city (its cheapest eligible path) plus one per destination world gateway, with a fixed exclusion-reason precedence. `reportedAs: ModeledPriceQuoteNotPhysicalTrade`, `appliedToGame: false`.
- Golden fixture `v1-basic`: A→B by rail costs 134, beating road (142) and world (140); the isolated city C is `NoPath`.

## 6. Integration interfaces (proposed C# signatures; final form decided in Contracts)

```csharp
interface ICitySnapshotSource { CitySnapshot Capture(); }             // CS2Integration and CitySimulator both implement this
// The exporter side does not reference IRegionStore: it only creates inbox files (Contracts/README.md).
interface IRegionStore {
  RegionManifest ReadManifest(); void WriteManifest(RegionManifest m, int expectedRevision); // atomic, optimistic
  IngestResult IngestInbox();                                        // idempotent on (cityId, seq, fileSha256); conflicts reported
  IReadOnlyList<CitySnapshot> LatestSnapshots(); }
interface IRegionalEngine { QuoteResult Quote(RegionState state, QuoteRequest req); InvariantReport Check(RegionState state); }
interface ICapabilityProbe { CapabilityReport Probe(); }              // game build, mods, feature -> verified|shadow|disabled + reason
// Later (Gate C): interface IMarketEffectSink { void Apply(BoundedModifier m); void ResetAll(); }
```

## 7. Repository layout

```text
CLAUDE.md
docs/        ARCHITECTURE.md, ASSUMPTIONS.md, PROTOTYPE_PLAN.md, source research docs
Contracts/   netstandard2.0 (V-02) DTOs + schema/*.json + golden/*.json
RegionalEngine/   RegionStore/   CitySimulator/   ScenarioRunner/
CS2Integration/   CS2 mod project from the official toolchain template
RegionalUI/       later
Tests/       Engine.Tests, Contracts.Tests (golden round-trip), Scenarios (G00–G13)
fixtures/    scenario inputs (text only; NO save files)
testlogs/    L3/L4 run records: build, mod versions, save checksums, results
```

## 8. Testing layers and gates (§18.3, §18.10)

| Layer | Runs CS2 | When |
|---|---|---|
| L1 engine unit and property tests | No | Every commit |
| L2 synthetic multi-city scenarios | No | Every commit |
| L3 bridge test on one disposable save | Yes | When integration code changes or the game updates |
| L4 multi-save switching | Yes | Milestones |

Gates in order: **A** library and golden scenarios pass → **B** read-only bridge is behaviourally identical to vanilla (G00) → **C** one bounded native effect with a working kill switch → **D** two-city save alternation is stable → **E** combined features.

## 9. Sequence beyond Prototype 0 (not yet scheduled)

Gate C native price pilot (one resource, one city, reversible) → in-game Freeform atlas and link editor → in-save identity (V-15) → more resources → tariffs and subsidies in game → fiscal shadow → migration observation → async publication → Shared Geography map (map spec M2+). If no safe native effect exists, ship a clearly labelled **read-only region builder** (§27 go/no-go).
