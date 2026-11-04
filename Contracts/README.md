# Contracts v1

The shared data contract between **RegionalEngine/RegionStore** (Developer A) and **CS2Integration** (Developer B). **Change it only through a PR reviewed by both developers.**

The source of truth is the set of JSON schemas in `schema/` together with the golden files in `golden/`. The C# classes in `src/` mirror the schemas. If they disagree, the schema wins and the class is a bug.

## Files

| File | Written by | Read by |
|---|---|---|
| `schema/city-snapshot.v1.schema.json` | CS2Integration exporter, CitySimulator | RegionStore, RegionalEngine |
| `schema/region-manifest.v1.schema.json` | User/tools (later the UI) | Engine; the exporter (to find its `cityId`) |
| `schema/store-index.v1.schema.json` | RegionStore only | Exporter (to choose the next `seq`), engine |
| `schema/quote-result.v1.schema.json` | RegionalEngine | CLI, tests, later the UI |
| `schema/capability-report.v1.schema.json` | CS2Integration | Snapshot (by hash), diagnostics |
| `schema/common.v1.schema.json` | — | Shared enums and ids |
| `resources/resource-ids.v1.json` | Joint | Both. Game names are unverified (V-10). |

## Serialization rules (both serializers must follow these)

1. UTF-8 without BOM, **LF line endings**, camelCase property names.
2. Enums are written as their exact PascalCase **string** names, never as integers.
3. **Every property is written, including explicit `null`s.** `null` means unknown or unmeasured. It never means zero, and `0` is a measured zero. A reader treats a missing property as invalid, not as null.
4. No NaN or ±Infinity, which JSON cannot represent anyway. A writer that holds a non-finite value writes `null` and logs a warning.
5. Arrays documented as sorted must be sorted with **ordinal** string comparison (or enum order for enums), so that identical state produces identical bytes.
6. The engine never reads `exportedAtUtc` or any other wall-clock field.
7. Numbers compare with a tolerance of `1e-9` against golden files. Engine-against-engine replay must be **byte-identical**.

## Versioning

- Every document has `schemaVersion` as an int.
- **Within v1** you may add an enum member or add an optional property. Do this by updating the schema and the class in one PR. Old readers must still reject unknown properties (`additionalProperties:false`), so both sides upgrade together.
- **Breaking changes** (renaming or removing a field, changing its meaning or units) need a new `*.v2.schema.json`, a version bump and a migration note.

## Identity

- `cityId` is assigned by the region in the manifest. In Prototype 0 the exporter finds it by matching `cities[].saveRef`. **This is temporary until in-save identity (V-15) is verified.**
- Snapshot identity is `(cityId, seq)`. `seq` starts at 1 and increments per city. `parentSeq` is the latest seq the exporter saw.
- `endpointKey` is an opaque string, stable per city and owned by CS2Integration (built from a fingerprint, V-08). It is **never** an ECS `Entity` index or version. The golden keys are illustrative.

## Region store layout (external folder, never inside a save)

```text
<RegionRoot>/
  manifest.json                     RegionManifest; atomic replace; optimistic on `revision`
  inbox/<cityId>.<seq>.json          CitySnapshot written by an exporter (create-only, temp file + rename)
  snapshots/index.json              StoreIndex; written only by RegionStore
  snapshots/<cityId>/<seq>.json      accepted snapshots; immutable once written
  capability/<cityId>.<seq>.json     optional CapabilityReport referenced by producer.capabilityReportSha256
```

Exporter protocol:
1. Read `manifest.json` and find `cityId`.
2. Read `snapshots/index.json` and `inbox/` to find this city's highest `seq`.
3. Write `inbox/<cityId>.<seq+1>.json` with `parentSeq` set to that highest seq.

Never overwrite or delete anything.

Ingestion rules (RegionStore):

| Case | Result |
|---|---|
| Schema-invalid | Rejected |
| Same `(cityId, seq)` and same `fileSha256` | No-op (G06) |
| Same `(cityId, seq)` with a different hash | **Conflict** |
| `seq` ≤ the latest, or `parentSeq` ≠ the latest | **Conflict** (fork or rollback, log §22.3) |
| `gameTime.gameDays` lower than the previous snapshot's (when both are non-null) | **Conflict** (an older save was loaded) |
| Accepted | Copied into `snapshots/`; `currentRegionSeq += 1` and assigned as its `regionSeq` |

Conflicts are reported and are never resolved silently.

Hashes are lowercase hex SHA-256 of the **exact file bytes**. Hashing happens at the file level, so it needs no canonical JSON.

## Quote semantics fixed by the contract (`golden/v1-basic/expected/`)

- **One offer per supplier city** (every city in the manifest except the destination that has a snapshot with this resource), plus one offer per world gateway of the **destination**. In v1, world access goes only through the destination's own gateways.
- City offer: `producerPrice = regionalProducerPriceIndex`, `freight = Σ over links (fixedCost + costPerKm × distanceKm)` along the cheapest eligible path, `landedPrice = producerPrice + freight + tariff − subsidy`. Tariff and subsidy are 0 until A7.
- World offer: `producerPrice = worldProducerPriceIndex`, `freight = fixedCost + costPerKm × gateway.distanceKm`.
- A link mode is usable for a resource only if it is in that resource's `allowedFreightModes`. Utility connections are never freight.
- When an offer has several exclusion reasons, report the first in this order: `UnknownResource` → `NoPath` / `ModeIncompatible` → `StaleSnapshot` → `UnknownSupply` → `NoSupply`.
- `effectiveRegionalOfferPerGameDay = min(output − inputUse, capacity) × confidenceFactor`, where `confidenceFactor(Validated) = 1.0`. The factors for `Provisional`/`Unknown`, and how a null capacity is handled, are **decided in A4** and documented here when merged.
- `selectedOfferIndex` points to the lowest eligible `landedPrice`; a tie goes to the earlier offer. It is `null` when nothing is eligible.
- Offer order: City offers by `originCityId`, then World offers by gateway `endpointKey`.

## Open items to settle in review

- Whether `MaxRegionSeqAge` (staleness counted in ingestions, not in city clocks) is the right rule. Developer A may replace it in A4.
- Whether `CitySnapshot` needs `saveLineageId` before V-15 is verified. Currently it doesn't; lineage is checked through `seq`/`parentSeq`/`gameDays`.
- The coordinate convention of `positionMetres` (V-28). Golden values assume map-centred x/z with y up, and that is a guess.
