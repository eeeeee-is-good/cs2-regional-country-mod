# Claude Code handoff — CS2 Regional/Country Mod

**Project status:** Phase 1 research complete, prototype not built. Read `Cities_Skylines_II_Regional_Country_Mod_Research_Log_v1.9.md` for full rationale; this checklist is the practical starting point.

## Before coding

- Read the cumulative log's **Phase 1 final design choices**, **Go/no-go guidance**, and **§28 AI-assisted development handoff**. Treat later decisions as authoritative when older exploratory sections conflict.
- Inspect and vet https://github.com/CitiesSkylinesModding/agents-plugins (CS2 knowledge, project scaffolding, UI skills). Verify instructions and local compatibility before installing.
- Potential tooling: `unity-devtools`, `coherent-gameface`, https://github.com/LancerComet/cities-skylines-2-mcp, https://github.com/mayor-modder/Cities2-MCP. These are candidates, not proven working in this environment.
- Reference: https://code.claude.com/docs/en/skills ; consider https://github.com/Unity-Technologies/skills and https://github.com/nowsprinting/unity-coding-skills for general test/debug patterns.
- Set up shared `CLAUDE.md` and `.claude/skills/{regional-economy,economic-testing,save-synchronization,safe-development}/SKILL.md` (project-owned skills to author and keep in version control).

## Non-negotiable architecture

- CS2 simulates active cities; pure `RegionalEngine` computes relationships; `CitySimulator` supplies synthetic city test data; `CS2Integration` reads/writes through a small, version-sensitive adapter.
- Transport-aware **soft market**, not exact shipments. Unloaded cities do not process native companies or citizens; no direct edits to closed saves.
- Migration ledger is accounting until native origin/destination effects are verified.
- Prefer native industry-specific tax and demand mechanisms; avoid duplicate overrides.
- Core compatible-mod targets: vanilla, MapExt/EconomyEX individually, Realistic Trips (fallback Realistic Time and Traffic), Road Builder, plus prioritized RWH, Change Company and production/capacity mods. Unsupported full-economy overhauls are not a first-release requirement.

## Collaboration

- Two developers, AI-assisted, separately cloned private GitHub repo, short feature branches and review before merging.
- One owns `RegionalEngine`/`CitySimulator`/`Tests`; the other owns read-only `CS2Integration` and snapshots. Agree on `Contracts/CitySnapshot` **before** independent implementation.
- Never commit frequently changing full saves into Git or edit valuable saves. Record disposable test save versions, CS2 build, mod versions, and checksums.

## First implementation gate

1. Define versioned CitySnapshot schema and exact testable acceptance criteria.
2. Build two synthetic cities + one route + one resource + delivered-cost quote in standalone C# code.
3. Add tests (connectivity, tariff eligibility, subsidy cost, identical-snapshot replay, invariants, explainable price trace).
4. Independently build a read-only CS2 mod that exports matching snapshots and inspects outside connections.
5. Test an isolated, reversible economic integration only after both pieces work.

**Required AI response after every task:** files changed; why; assumptions; actual build/test results; version-specific APIs cited; possible conflicts; how to undo the change; pending unverified claims. No "fully working" claim based on compilation alone.


### New v2.0 geographical map option (NOT MVP)

The country map supports **Freeform Country** (existing saves arbitrarily arranged on an atlas) and a future **Shared Geography Country** (separate CS2 city maps generated from a single master heightmap). Neither mode renders multiple live cities inside one CS2 scene. The technical decisions, coordinates/terrain/endpoint contracts and seam-validation plan are in `Regional_Country_Mod_Map_Architecture_v2.0.md`. Read that small document **only for regional mapping work**; do not auto-load the 40,000-word archive.
