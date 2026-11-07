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
| `--resources <file>` | Use another resource table instead of the bundled `Contracts/resources/resource-ids.v1.json`. |

Exit codes: 0 OK, 1 invariant check failed, 2 usage or input error.

## Region folder

The region-store layout from [Contracts/README.md](../Contracts/README.md):

```text
manifest.json                  cities, links, world gateways, model parameters
snapshots/index.json           which snapshots were accepted, their regionSeq and SHA-256
snapshots/<cityId>/<seq>.json  one CitySnapshot per accepted publication
```

The runner validates every file against `Contracts/schema/` and every snapshot against the hash in the index, and uses the highest `seq` per city. To experiment, write a scenario, then edit `manifest.json` (distances, links, costs) and run `quote` again. Editing a snapshot breaks its hash, by design: snapshots are immutable once accepted. Write a new scenario or change the simulator instead.

The inbox and ingestion (`inbox/`, conflicts, duplicate handling) are RegionStore's job (A2) and are not handled here yet.
