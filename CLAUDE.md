# CLAUDE.md — CS2 Regional/Country Mod

The project is designed for two developers, each working with Claude Code. These rules are mandatory for both.

> **Current staffing (2026-11-06):** only one developer is working on the project for now. A second developer (a friend) is expected to join later, but no date is set. Until then:
> - The single developer owns **both** tracks (Developer A and Developer B areas) and may edit either.
> - "PR reviewed by both developers" means self-review of a PR before merge. Keep using short feature branches and PRs, so the second developer can review the history when they join.
> - Contracts v1 and any Contracts changes made before then stay **provisional** until the second developer has reviewed them.
> - Keep the A/B ownership split in the docs. It still describes how work divides once the second developer joins.

## Read order and authority
1. This file, then [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). These are authoritative.
2. [Contracts/README.md](Contracts/README.md), schemas and golden files. These define the shared data shapes and serialization rules.
3. [docs/PROTOTYPE_PLAN.md](docs/PROTOTYPE_PLAN.md) for the current task list, and [docs/ASSUMPTIONS.md](docs/ASSUMPTIONS.md) for what is unverified.
4. Source research, **targeted reads only. Never load the whole 42k-word log.**
   - `Cities_Skylines_II_Regional_Country_Mod_Research_Log_v2.0.md`: grep the heading, then read that section.
   - `Regional_Country_Mod_Map_Architecture_v2.0.md`: read only for region map or terrain work.
   - `Claude_Code_CS2_Regional_Mod_Handoff_v2.0.md`: the original checklist. It refers to "log v1.9"; v2.0 is current.

When sources conflict, later log sections win over earlier ones. ARCHITECTURE.md §2 lists what was superseded. **Do not reintroduce** bilateral shipments, escrow, offline save edits, load-time catch-up, RCI-bar overrides, citizen transfers or unverified treasury writes.

| Topic | Log section |
|---|---|
| Outside connections, ECS leads | §3.1, §3.6 |
| Statistics reliability and demand levels | §14.3–14.5 |
| Taxes and specialization | §15 |
| Route graph and landed cost | §16 |
| Policy stacking order | §17.4, §17.10 |
| Invariants, golden scenarios, traces | §18.4–18.7 |
| A/B experiment protocol | §19 |
| Fail-closed adapter, patch-day checks | §21.4–21.5 |
| Async multiplayer, publication | §22 |
| Compatibility modes and matrix | §23.4, §24 |
| Snapshot fields for modded saves | §25.3–25.5 |
| Migration ledger | §26 |
| Product and UX scope | §27 |

## Game API rule (most important)
- Research-log class and field names (`OutsideConnectionData`, `TaxSystem`, …) are **leads from a v1.6.2f1 community decompile, not confirmed APIs.**
- Before writing code that uses a game type or method: confirm it against the **installed** build (decompile or verified source), record the build, and update its `V-xx` row in ASSUMPTIONS.md. Cite the `V-xx` in a code comment.
- If a hook cannot be verified, the feature stays in shadow mode or off. Never guess signatures. Never use global price edits as a stand-in for a local hook.

## Boundaries and ownership
- `Contracts/`, `RegionalEngine/`, `RegionStore/`, `CitySimulator/`, `ScenarioRunner/`: **pure .NET.** They must have no reference to `Game.*`, `Colossal.*`, Unity or Harmony; no clock reads; no unseeded randomness; and deterministic iteration order. Owner: Developer A.
- `CS2Integration/` is the **only** code that touches the game. Owner: Developer B. In Prototype 0 it is **read-only**, with no Harmony patches and no in-save state.
- `Contracts/` is jointly owned. Change it only through a PR reviewed by both developers, and bump `schemaVersion` on breaking changes. `null` means unknown, never zero.
- Do not edit the other developer's area without their agreement.

## Safety
- Never modify, move or overwrite a save file. Test only on disposable copies kept **outside Git**, and record their checksums in `testlogs/`.
- Never commit saves, large binaries or generated telemetry.
- Region state lives only in the external store, written atomically. It never goes into an unloaded save.
- Every game-side effect needs a capability check, a bounded magnitude, smoothing and a kill switch, and must fail closed.
- UI and logs must label estimates as estimates (`reportedAs: ModeledPriceQuoteNotPhysicalTrade`, `appliedToGame`).
- Third-party plugins and MCP servers are untrusted code. Read them before installing them, and record the decision.

## Testing requirements
- Every commit: `dotnet build Regions.sln` and `dotnet test Regions.sln` pass (.NET 8 SDK, pinned by `global.json`). This covers L1 unit and property tests and L2 synthetic scenarios.
- Engine changes need tests for the invariant they touch (ARCHITECTURE.md §8; log §18.4) and must keep the **replay test** byte-identical.
- New engine behaviour needs a golden scenario or metamorphic test. Every quote must emit a trace.
- CS2-side changes need an L3 run on a disposable save, recorded in `testlogs/` with game build, mod versions, save checksum, steps and results. Read-only features must pass G00 (no behaviour change versus vanilla).
- "It compiles" is never evidence that something works in game.

## Workflow
- Short feature branches with PR review before merge. Use minimal diffs and no unrelated rewrites. Agree on interfaces before implementing.
- **After every task, report:** files changed; why; assumptions; actual build and test output; game APIs used with `V-xx` and the build verified against; possible conflicts; how to undo; claims still unverified.
