# RegionStore

The region's external store: one folder per region, outside any save (ARCHITECTURE.md N3). It implements the layout and ingestion rules in [Contracts/README.md](../Contracts/README.md), and owns the engine side's single copy of the serializer settings (`ContractJson`).

```text
<RegionRoot>/
  manifest.json                  replaced atomically; optimistic on `revision`
  inbox/<cityId>.<seq>.json      created by exporters; never modified by the store
  snapshots/index.json           written only by the store, atomically
  snapshots/<cityId>/<seq>.json  accepted snapshots, byte-for-byte copies; immutable
```

## Ingestion (`FileRegionStore.IngestInbox`)

Inbox files are processed in a fixed order: valid names by city id (ordinal), then seq. Every file gets exactly one outcome.

| Outcome | When |
|---|---|
| Accepted | Valid, and the next publication for its city. Copied to `snapshots/`, given `regionSeq = currentRegionSeq + 1`. |
| Duplicate | Same `(cityId, seq)` and same SHA-256 as an accepted snapshot. No-op (G06). |
| Conflict `SameSeqDifferentContent` | Same `(cityId, seq)`, different bytes. |
| Conflict `SeqNotNewer` | `seq` ≤ the city's latest accepted seq. |
| Conflict `ParentSeqMismatch` | `parentSeq` ≠ the latest accepted seq (`null` when the city has none yet). |
| Conflict `GameTimeRollback` | `gameTime.gameDays` went down versus the previous snapshot (both non-null): an older save was loaded. |
| Rejected `CorruptFile` / `SchemaInvalid` | Not JSON, or fails `city-snapshot.v1.schema.json`. |
| Rejected `BadFileName` / `FileNameMismatch` | Name is not `<cityId>.<seq>.json`, or disagrees with the content. |
| Rejected `UnknownCity` | The city is not in `manifest.json`. |

Files not ending in `.json` (such as an exporter's `.tmp` file) are ignored.

Conflicts are reported and never resolved. A conflict for one city does not block other cities.

## Safety rules

- **Never touches saves.** No path outside the region folder is read or written.
- **Snapshots are immutable.** If a target file already exists with the same bytes (a crash between copying it and writing the index), that is accepted. With different bytes, the store stops with `StoreCorruptException`.
- **Atomic replacement** for `manifest.json` and `index.json`: write `<file>.tmp`, then rename over the target.
- **Inbox files are left in place.** Re-ingesting is idempotent, so this is safe. Remove a conflicting file yourself once you have decided what to do.
- **Reads verify.** `LatestSnapshots` and `LoadState` check every snapshot against its index hash, schema, city and seq.
- **One writer at a time** per store is assumed. There is no lock file yet.

## Decisions to review

- **net8.0, not netstandard2.0.** The store needs System.Text.Json and a schema validator, neither verified to load in CS2 (V-03). The in-game exporter only creates inbox files, so it doesn't need this library. Revisit if the mod ever has to read the store.
- **Unknown cities and file-name checks** are rejections this store adds beyond the Contracts/README.md table.
- **Open item:** the exporter protocol picks the next seq from the index *and* the inbox. A conflicting file left in the inbox would push the exporter's next seq past it, and that file would then conflict on `parentSeq`. Settle this with B5: either the exporter reads only the index, or rejected files move to an `inbox/rejected/` folder.
