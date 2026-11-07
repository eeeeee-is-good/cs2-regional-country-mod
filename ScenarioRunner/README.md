# ScenarioRunner (`regions` CLI)

Test the regional engine without Cities: Skylines II. It reads a region folder, prints each landed-cost quote with the reason every offer won or lost, and checks the log §18.4 invariants. It never reads or writes a game save.

## Run it

From the repository root:

```bash
dotnet run --project ScenarioRunner -- scenarios
```

```bash
dotnet run --project ScenarioRunner -- quote Contracts/golden/v1-basic --dest city-b
```

```bash
dotnet run --project ScenarioRunner -- scenario corridor ../my-regions/corridor
```

```bash
dotnet run --project ScenarioRunner -- check ../my-regions/corridor
```

| Command | What it does |
|---|---|
| `scenarios` | Lists the built-in synthetic regions (`CitySimulator/src/Scenarios.cs`). |
| `scenario <name> <folder>` | Writes one as a new region folder. Refuses a non-empty folder; nothing is overwritten. |
| `quote <folder> [--dest <cityId>] [--resource <id>] [--json <outFolder>]` | Prints quotes. `--json` also writes `quote.<dest>.<resource>.json` (schema-valid, byte-identical to the engine output). |
| `check <folder>` | Quotes every city and resource and runs the invariant checker. Exit code 1 if anything fails. |
| `ingest <folder>` | Accepts new snapshot files from `<folder>/inbox/` and lists every duplicate, conflict and rejected file. Exit code 1 if there are conflicts or rejections. |
| `--resources <file>` | Use another resource table instead of the bundled `Contracts/resources/resource-ids.v1.json`. |

Exit codes: 0 OK, 1 invariant check failed or ingestion problems, 2 usage or input error.

## Region folder

The region-store layout from [Contracts/README.md](../Contracts/README.md):

```text
manifest.json                  cities, links, world gateways, model parameters
inbox/<cityId>.<seq>.json      new snapshots waiting to be ingested
snapshots/index.json           which snapshots were accepted, their regionSeq and SHA-256
snapshots/<cityId>/<seq>.json  one CitySnapshot per accepted publication
```

All reading and writing goes through [RegionStore](../RegionStore/README.md): every file is validated against `Contracts/schema/`, every snapshot against the hash in the index, and the highest `seq` per city is used. `scenario` publishes each city through the inbox and ingestion, exactly as the game exporter will.

To experiment, write a scenario, then edit `manifest.json` (distances, links, costs) and run `quote` again. Editing an accepted snapshot breaks its hash, by design: snapshots are immutable. To publish a newer one, drop `<cityId>.<seq+1>.json` with `parentSeq` set to the previous seq into `inbox/` and run `ingest`.
