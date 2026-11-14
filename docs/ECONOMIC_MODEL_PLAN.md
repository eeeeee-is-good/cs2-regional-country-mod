# Economic model update: review and phased plan

**Status:** Review and proposal (2026-11-14). Nothing here is implemented yet, and nothing changes code or Contracts until each phase is agreed.
**Input:** "Regional Country Mod — Economic Model Design Update" (supply, demand and pricing; elasticity; unloaded cities; population and business feedback; inflation).
**Reviewed against:** engine 0.4.0 on `main`, ARCHITECTURE.md, Contracts/README.md, and research log §8A, §14.4–14.5, §16, §17, §19.8, §22.4–22.5 and §26.

## Progress

The developer asked for Phases 0–4 to be built, with the decisions below taken as recommended. Phase 5 (nominal) and Phase 6 (game side) are not started.

| Phase | Status |
|---|---|
| 0. Consistency fixes | **Done**, engine 0.5.0: D1 ageing on both sides (`Freshness.cs`); D2 documented (`DemandRule`); demand-aware subsidy exposure. |
| 1. Time and gradual signals | **Done:** `SignalPlanner` (target vs applied, D3 step, frozen when inactive, reference price for Phase 3), `FileRegionStore.AdvancePeriod` with `signals/state.json`, `regions advance`. A price cache was not needed: a full region quote takes milliseconds; revisit with G11. |
| 2. Simulator goods dynamics | **Done:** `CityGoods` (fixed elasticities by class: essential 0.2, discretionary 0.8, industrial 0; supply elasticity 0.6 below capacity; lag 0.3; output step at most 10% of capacity) and `RegionSimulation` (engine → applied signals → reactions → republished snapshots); `regions simulate`. Tests: quiet region settles, world price shock up and back, supply shock up to the world ceiling, recession and recovery, vertical engine demand guard, determinism, and 100 random regions × 60 periods with no growing oscillation. |
| 3. Unloaded cities | **Done:** D1 ageing (Phase 0) plus `ExposureReport` (forecast only: change since last publish, gap, periods to converge; never an input) and `regions exposure`. Tests: report values and wording, building it changes nothing, and a simulated city away for 30 periods during a world price rise stays frozen, ages out on both sides, and returns at most one step per period. Offline business closures and entries are deliberately not estimated. |
| 4. Simulator people | **Done:** `PeopleDynamics` (CitySimulator, synthetic only). Jobs by 3 skill groups (D6) from fixed jobs plus jobs per unit of output; commuting over direct links ≤ 60 km (D7: simulator only); internal migration toward lower unemployment along links, capped by free housing; international arrivals into vacancies and departures above 10% unemployment, kept apart so domestic moves cancel; business entry only on a price premium > 2% with plants ≥ 80% used, idle workers and free industrial space (no buildings appear), and exit of idle plants without cutting output; consumer spending follows employment. Frozen cities take no part. Tests: unemployment shock and migration along links only, commuting distance, investment ends a shortage, no workers means no investment, gradual closures, frozen city, ledger invariants every period, determinism, 100 random regions × 60 periods bounded. **The random-region test found two real bugs** (closing idle capacity also cut output, a death spiral; investing while plants were idle), both fixed. |
| Sandbox | **Done:** "Over time" panel in the Regions Sandbox (`/api/simulate`, `Playground.Simulate`). It runs the map through `RegionSimulation` for 1–500 periods, optionally with people, unloaded cities and one change during the run (world price, a city's capacity and use), and charts each city's prices, output, use, capacity, population, unemployment and commuters. Labelled synthetic. Tests: period counts, determinism, first-period targets equal the map's prices, frozen cities stay put, shocks step at most 2 a period, bad input rejected. |

**Decisions taken:**
- **D1:** fade supply and demand together. Full weight to half the limit, linear to 0 beyond it.
- **D2:** demand is not discounted for Provisional figures (documented).
- **D3:** applied prices step at most 2% of the regional producer price per period. There is no band in the engine; the ±band belongs to the in-game modifier at Gate C.
- **D4:** a period is an explicit region tick, never a city clock.
- **D5:** undecided (Phase 5).
- **D6:** 3 qualification groups.
- **D7:** commuting stays in the simulator first.

The guiding principle stays the same: **the regional engine simulates relationships between cities; Cities: Skylines II simulates what happens inside each active city.** Every recommendation below follows from it.

---

## 1. Summary

| # | Proposal | Today | Recommendation | Runs where (eventually) |
|---|---|---|---|---|
| 1a | Local prices from supply, demand, transport, tariffs, subsidies, access | **Implemented** (A3, A4, A7, scarcity in engine 0.4.0) | Keep. Fix four inconsistencies (§4). | Engine (outside the game) |
| 1b | Shared regional market: no unlimited access to one supplier | **Implemented** (scarcity pricing: suppliers are cleared together) | Keep. Optional later: let a city's demand be split between suppliers. | Engine |
| 1c | Compute periodically and cache | **Partly.** Quotes are pure and replayable; there is no schedule or cache. | Add a small "price period" step: recompute when the store changes, cache by `inputsSha256`. | Engine + store |
| 1d | Bounded, gradual price adjustment | **Missing** | Add a separate *applied signal* layer (target → bounded step). Never smooth inside the quote. | Engine (pure), state in store |
| 2a | Fixed demand elasticities by resource (and income group) | **Missing.** Demand is vertical (observed use). | **Simulator only.** Never in the engine's clearing for real cities: double response (§4.5). Income groups deferred. | Synthetic simulator |
| 2b | Industrial demand follows production needs | **Implemented** implicitly: demand is observed `inputUse`. | Keep. | Engine |
| 2c | Capacity-constrained supply response | **Missing** | **Simulator only.** In real cities, CS2 is the supply response. | Synthetic simulator |
| 3a | Unloaded city: gradual transition to current prices | **Partly.** The quote updates instantly; there is no transition. | Covered by 1d: the applied signal converges on load (§8A "freeze-and-converge"). | Engine signal + mod (Gate C) |
| 3b | Unloaded city: estimate offline business failures and entries | **Not implemented, and conflicts** with §8A, N4 and N5 | **Do not model as state.** At most a forecast-only "exposure report" that never feeds back. | Engine report (label: forecast) |
| 4 | Population, employment, business, commuting and migration feedback | **Missing.** Snapshots carry none of this data. | Aggregate model **in the simulator** first. In the region: commuting and migration as an *observational* ledger (N7), only once the game can supply data. | Simulator now; region ledger later |
| 5a | Nominal layer: CPI, PPI, real wages | **Missing.** All prices are indices. | Phase in **measurement only** (CPI/PPI from quotes), switchable, no feedback. Real wages in the simulator only. | Engine report; simulator |
| 5b | Autonomous inflation process, indexing CS2 costs | **Missing** | **Defer.** There is no agreed inflation source, and indexing CS2 money needs unverified write hooks (prohibited). | Not planned |

**In one line:** most of section 1 already exists. The real gaps are *time* (gradual signals, multi-period simulation) and *people* (labour, housing and migration in the simulator). Elasticity and supply response belong in the simulator, standing in for CS2. Offline business estimation and autonomous inflation should not be built as proposed.

---

## 2. What the engine is today (engine 0.4.0)

- **Inputs:** a manifest (cities, links, gateways, prices, costs, policies, scarcity rule), the latest snapshot per city (resources and outside connections only), and the store's ingestion counter.
- **Quote:** a pure function, deterministic and replayable. For each (city, resource) it lists every offer (supplier city or world), with producer price, scarcity premium, freight, customs and subsidy, or the reason it was excluded.
- **Supply (A4):** `min(output − use, capacity) × confidence × penalties`. Unverified figures are downgraded; Unknown is excluded.
- **Scarcity:** each supplier clears a small gate market. Supply is flat to its spare steel, then rises with a slope up to a cap. Demand is a falling step curve of reachable deficit cities, each leaving at its next-best price. Suppliers are re-cleared together until stable.
- **Policies (A7):** import tariffs on world offers only; freight subsidies on qualifying domestic links, scaled to a shared budget.
- **Staleness:** a cut-off after `maxRegionSeqAge` ingestions. There is no decay and no region clock.
- **No time dimension:** no history, no smoothing, nothing that evolves between quotes. The CitySimulator declares values; it does not simulate.
- **Guards:** the invariant checker (log §18.4 #1–#5, #7, #14), random-region property tests (more supply never raises a price; more demand never lowers one), and pinned replay hashes checked on three operating systems.

---

## 3. Review of each proposal

### 3.1 Regional supply, demand and pricing

Already in place: local prices by city, transport, infrastructure (links and modes), tariffs, subsidies and access, plus a shared market (scarcity) so cities can't all claim one supplier's full output.

Recommended additions:
- **Price period and cache.** The engine stays a pure function. RegionStore (or the runner) recomputes the price table only when a new snapshot is accepted, the manifest changes, or a period ticks. It caches by `inputsSha256`. No new economics.
- **Applied signal (bounded, gradual).** A new, pure step:
  `applied(t+1) = applied(t) + clamp(target(t+1) − applied(t), ±maxStep)`, with a hard band around the base price. The *quote* remains the instantaneous target and its replay stays byte-identical. The *applied signal* is what would later be passed to the game (Gate C). It is stored per city in the region store, never in a save. This is the "smooth / cap shocks" step the log asks for (§14.5, §16.3, §17.4 step 7).

Not needed: shipment simulation or company-level calculations. Correctly excluded already.

Optional later: **split demand between suppliers.** Today a city's demand leans on one supplier at a time (a documented simplification). Splitting helps only when two suppliers are close in price and both overloaded. Revisit if simulator tests show visible artefacts.

### 3.2 Price elasticity

This is the most important boundary decision.
- **In a real region,** a city's demand is *observed*: its snapshot already contains the quantities CS2 households and companies chose at the prices they faced. If the engine also applied an elasticity when clearing, CS2's own response would arrive in the next snapshot on top of the engine's assumed response. The same price change would cut demand twice, causing over-correction and oscillation (§4.5). **The engine's clearing keeps observed (vertical) demand.**
- **In the synthetic simulator,** nothing plays CS2's role, so elasticity is exactly what is needed: it generates the next period's consumption from the current prices.

Recommendations:
- **Fixed elasticities per resource class:** essential (low), discretionary (higher), industrial input (0: follows production). Simulator only.
- **Income groups (low, middle, high):** defer. They multiply parameters and test cases, and nothing in a real snapshot will carry income-split consumption until a game reader exists (unverified; it would need a new V-xx). Add only if class-level elasticities fail a test you care about.
- **Supply response:** simulator only. Output moves toward capacity when the gate price exceeds base, and away from it when the price falls, with a lag and a step limit. Never in the engine: in a loaded city, CS2 production *is* the supply response.
- **Dynamic elasticity:** not before fixed parameters are tested (agreed).
- **"Changing product-selection weights alone may not change quantities":** correct. It is a game-side concern for Gate C, not for the engine.

### 3.3 Unloaded cities and reconciliation

The architecture has already decided this (§8A "freeze-and-converge", N4 no catch-up, N5 decaying estimate):
- **While unloaded,** the region updates prices and conditions only. It "does not manufacture fresh production, silently bankrupt a company … The recession is encountered after re-opening."
- **On load,** read the *current* conditions and converge gradually through native systems.

So:
- **Gradual transition:** yes. It is the applied signal of §3.1. On load, the city's applied signal starts from its last applied value (from the store) and steps toward the current target. No backlog and no replay.
- **Estimating offline business failures and entries:** do not build as state. If estimated closures changed the unloaded city's snapshot (supply or demand), those businesses would be counted once by the region and again by CS2 when it really closes them after load. That is the double counting the proposal itself warns about. And because snapshots are immutable (A2), the estimate would need a second, shadow copy of the city.
  - **Acceptable alternative:** a **forecast-only exposure report** per unloaded city. For example: "since its last snapshot, steel delivered here is +18%; its steel users are under margin pressure." It is labelled `forecast`, stored separately, shown in the UI, and **never** used as an input to any quote, supply, demand or other city. It can be checked against what actually happens after load (a calibration signal, not a correction).
- **Staleness needs a real decision** (§4.1): today stale suppliers are cut off after `maxRegionSeqAge` ingestions, but their demand still counts. N5 says an unloaded city's influence should *decay* with age. Now that quantities matter (scarcity), decay is meaningful; before 0.4.0 it wasn't.

### 3.4 Population, employment and business feedback

Nothing exists yet, and the snapshot contract has no fields for it.
- **Inside a real city:** CS2 owns jobs, unemployment, housing, company entry and exit, and household spending. The region must not simulate them on top.
- **Between cities:** the region can legitimately own **commuting** (a flow over links, like freight) and **migration** (§26: an *observational* ledger of completed moves; no spawning or deleting citizens; requested ≠ completed).
- **In the simulator:** an aggregate model is the right tool for testing the region's feedback. Stocks per synthetic city:
  - population by qualification (3 groups);
  - jobs by qualification;
  - housing units;
  - company capacity per resource.

  Simple, lagged rules:
  - vacancies and unemployment drive migration pressure, capped by housing;
  - profitability (gate price vs cost) drives capacity entry and exit, capped per period and requiring workers and inputs;
  - population and income drive consumption.

  "Do not force equilibrium": keep frictions (lags, caps) and test that the system stays bounded rather than converging exactly.
- **Contract fields** (labour, housing, migration counters) should be added **only when the CS2 reader can supply them** (new V-xx rows, B-track work). Until then they live inside the simulator, not in Contracts. This avoids freezing a schema before we know what the game exposes.

### 3.5 Inflation and the nominal economy

The split into a **real economy** (what exists today: relative prices, quantities) and an optional **nominal layer** is sound. The safeguards in the proposal are right.

Recommended scope:
- **Phase it as measurement only.** A regional **CPI** (consumer basket) and **PPI** (producer inputs), computed *from the quotes* as weighted price indices against a base period, per city and region-wide. No feedback into any price. Switchable off entirely.
- **Real wages and purchasing power: simulator only** for now. Wages and household budgets exist in CS2 but there is no verified read of them (would need new V-xx rows).
- **No autonomous price-level process** until its *source* is decided. Without a source, an inflation process can only be (a) a fixed exogenous drift, which adds nothing to relative prices, or (b) driven by measured price changes, which double counts (§4.6).
- **No indexing of CS2 monetary variables** (construction, upkeep, loans). These need write hooks into game money, which is prohibited until verified (CLAUDE.md "unverified treasury writes"; N8).
- **No banking or central bank.** Agreed.

**Unresolved:** what generates persistent general inflation? Candidate answers for later discussion:
1. none (a real model with measured CPI only);
2. an exogenous policy target (a "national inflation rate" setting, purely nominal, applied consistently to all nominal quantities the mod controls);
3. demand-pull from aggregate excess demand in the simulator.

Option 2 is the only one that doesn't need a monetary model, and it still has to answer how CS2's fixed nominal costs relate to it. This decision blocks Phase 5.

---

## 4. Duplicated feedback and accounting inconsistencies

Found by reading the current code.

1. **Stale cities: supply excluded, demand kept.** In `ScarcityEvaluator.Solve`, a stale supplier is skipped (`!ctx.IsStale(pub)`), but the demand loop has no stale check. An old snapshot keeps pulling prices up after its supply has gone. **Fix:** treat both sides the same way: both excluded, or both decayed with the same weight (decision D1).
2. **Provisional data: supply halved, demand counted in full.** The A4 confidence factor applies to supply only. A provisional deficit counts 100%. **Fix (decision D2):** either apply the same factor to demand, or document that demand is deliberately not discounted (understating demand understates scarcity).
3. **Subsidy forecast ignores demand.** `PolicyEvaluator` exposure is `effectiveOffer × best per-unit subsidy`, which assumes every supplier sells all its spare steel. With scarcity, the engine now knows each supplier's load. **Fix:** use `min(effectiveOffer, load)` when a scarcity rule is on. Exposure and spend become realistic, and fewer subsidies are needlessly scaled down.
4. **No separation between target and applied price.** Fine today (nothing is applied), but any smoothing must live in a separate layer. Putting it inside the quote would break replay and the invariants.
5. **Elasticity in the engine would double the response** (§3.2). This is the duplicated feedback loop to avoid above all: engine assumes demand falls, then CS2 actually reduces demand, then the next snapshot falls again.
6. **A price index fed back into prices double counts.** If CPI is computed from resource prices and then applied as a general multiplier to those same prices, every relative change is counted twice. The index must stay output-only (or apply only to quantities that are *not* already priced by the engine).
7. **Offline business estimates double count with native failures** (§3.3). A forecast must never become an input.

Items 1–3 are small, contained engine fixes with tests (Phase 0).

---

## 5. Module boundaries

```
Contracts          unchanged until the game can supply new data (labour/housing/migration counters later)
RegionalEngine     pure, stateless
  ├ Quote          (existing) instantaneous target prices + trace
  ├ Markets        (existing) scarcity clearing
  ├ SignalPlanner  NEW, Phase 1: (previous applied, target, rule) -> next applied; pure, bounded
  ├ PriceIndex     NEW, Phase 4, optional: CPI/PPI from quotes; output only
  └ ExposureReport NEW, Phase 3, optional: forecast-only for unloaded cities; never an input
RegionStore        + applied-signal state per city (external, atomic), + price-table cache by inputsSha256
CitySimulator      synthetic only, never used with real cities
  ├ SyntheticCity  (existing) declared values
  └ Dynamics       NEW, Phase 2: time-stepped aggregate economy:
                   demand elasticity, supply response, labour/housing/business, migration pressure
ScenarioRunner     + `regions simulate <folder> --periods N` driving the loop below
CS2Integration     later (Gate C+): reads snapshots, applies only the bounded applied signal
```

**Simulation loop (simulator only):**

```
for each period:
  ingest the cities' new snapshots          (RegionStore, real ingestion path)
  quotes  = engine.Quote(all cities)        (target prices)
  applied = SignalPlanner(applied, quotes)  (bounded step)
  for each synthetic city: Dynamics.Step(city, applied prices) -> next snapshot
```

The engine never knows whether a snapshot came from the simulator or the game. That is the property that makes the simulator a valid test bench.

---

## 6. Phased plan

Each phase is a separate branch with tests, merged only when green. Nothing touches the game until Gate C.

| Phase | Scope | Depends on | Size | Main risk |
|---|---|---|---|---|
| **0. Consistency fixes** | §4 items 1–3: symmetric staleness (D1), provisional demand (D2), demand-aware subsidy exposure. Golden scenario for each. | Decisions D1, D2 | Small | Changes existing prices in some regions: re-pin hashes, document. |
| **1. Time and gradual signals** | `SignalPlanner` (bounded step, hard band); applied-signal state in RegionStore; price-table cache; `regions simulate` skeleton with *static* cities (no dynamics yet). Sandbox: show target vs applied. | 0 | Medium | Getting state semantics right on load and reload (no backlog; idempotent re-ingestion). |
| **2. Simulator dynamics, goods** | `Dynamics`: fixed elasticities by resource class; capacity-constrained supply response with lags and caps. Runaway-loop tests. | 1 | Medium | Oscillation between price and quantity. Mitigated by lags, step limits, and tests that fail on growing amplitude. |
| **3. Unloaded cities** | Decay rule (D1); exposure report (forecast-only); long-unloaded tests. | 0, 1 | Small–medium | Scope creep into offline business simulation. Stop at forecast-only. |
| **4. Simulator dynamics, people** | Aggregate labour by qualification, housing, company entry and exit, migration pressure between connected synthetic cities, commuting over passenger links. | 2 | Large | Too many parameters to calibrate. Start with 3 groups and few rules; add only what tests need. |
| **5. Nominal layer (optional switch)** | CPI/PPI measurement from quotes (engine, output only). Real wages and purchasing power in the simulator. *No* autonomous inflation until D5 is decided. | 1 (needs periods); D5 for anything beyond measurement | Small (measurement) | Being read as "the game has inflation" when it doesn't: label clearly. |
| **6. Game side (Gate C+)** | The mod applies only the **applied signal** for one resource in one city, with kill switch, cap and smoothing. Optional read-only observation of labour and migration counters (new V-xx). | B1–B6, Gate C hooks verified | Large | Hooks may not exist (V-17, V-18). Then stay read-only. |

**Not planned:**
- offline business closures and entries as state;
- income-group elasticities (until class-level fails a test);
- dynamic elasticity;
- an autonomous inflation process;
- indexing CS2 costs;
- banking or a central bank;
- individual shipments, citizens or companies.

---

## 7. Controlled tests

All of these run in the simulator with fixed seeds and a fixed number of periods. Each states its pass criteria in advance (log §19.6). "Bounded" means: no NaN, no negative quantities, prices within the configured band, and the amplitude of any oscillation non-increasing over the last half of the run.

| Test | Setup | Pass criteria | Phase |
|---|---|---|---|
| **Price shock** | Steady region; at period 10 the world price jumps +50% (or a key link breaks). | Target quotes move at once; applied prices move by at most `maxStep` per period and settle at the new target; no overshoot beyond the band; when the shock is removed, prices return to baseline. | 1–2 |
| **Supply shock** | A producer loses 60% capacity. | Its gate price rises (scarcity); buyers switch to the next-best option in order of switch price; consumption in elastic classes falls, industrial inputs do not; bounded. | 2 |
| **Recession** | Region-wide demand −30% over 5 periods. | Prices fall toward base, never below; supply response reduces output with a lag; company exits capped per period; no collapse to zero; recovery when demand returns. | 2, 4 |
| **Unemployment** | One city loses a major employer (capacity −50%). | Unemployment rises in that city's qualification group, then migration pressure and commuting toward connected cities with vacancies; no city's population goes negative; total population changes only by declared external flows (domestic moves cancel, §26.7). | 4 |
| **Migration** | Two connected cities with a housing imbalance. | Flows are capped by destination housing; requested ≠ completed; domestic moves cancel in the totals; a disconnected city receives none. | 4 |
| **Inflation (measurement)** | Raise all world prices +10% (an exogenous nominal shock). | CPI and PPI rise by the basket-weighted amount; relative prices and quantities are unchanged; turning the nominal layer off changes no quote byte (replay hash identical). | 5 |
| **Long-unloaded city** | City A stays unchanged while B is re-ingested 30 times under shifting conditions; then A publishes again. | While stale, A's supply and demand weight follow the agreed decay (D1) symmetrically. Its exposure report is labelled forecast and is not an input (quotes identical with and without it). On A's return, its applied signal steps gradually from its last value: no backlog, no jump beyond `maxStep`. Republishing the same snapshot is a no-op (G06). | 1, 3 |
| **No double response** | Same region run (a) with the engine's vertical demand and simulator elasticity, (b) with elasticity wrongly applied in both. | (a) converges; (b) shows the larger oscillation. Kept as a regression test so nobody adds elasticity to the engine by accident. | 2 |
| **Runaway feedback** | 500 random regions × 100 periods with dynamics on. | Bounded in every run; any seed that fails is printed for reproduction. | 2, 4 |

---

## 8. What runs where

| Mechanism | Region engine (outside the game, also used by the mod) | Mod in the game (Gate C+) | Synthetic simulator only |
|---|---|---|---|
| Landed prices, scarcity, tariffs, subsidies | ✔ | Reads the result | Uses it |
| Applied signal (bounded step) | ✔ computes | ✔ applies one bounded modifier, kill switch | Uses it |
| Price cache, periods | ✔ (store) | — | ✔ |
| Demand elasticity | ✘ (would double count) | ✘ (CS2 households do this) | ✔ |
| Supply / capacity response | ✘ | ✘ (CS2 companies do this) | ✔ |
| Jobs, unemployment, housing, company entry and exit | ✘ | ✘ (native CS2) | ✔ aggregate |
| Commuting between cities | Later: flow signal over passenger links | Later: observe; influence only via a verified hook | ✔ |
| Migration between cities | Later: observational ledger (§26) | Observe completed moves (read-only first) | ✔ aggregate |
| Exposure report for unloaded cities | ✔ forecast only | Display only | ✔ (to calibrate) |
| CPI / PPI | ✔ measurement only, optional | Display only | ✔ |
| Real wages, purchasing power | ✘ until wage data is readable | ✘ | ✔ |
| Autonomous inflation, indexing CS2 costs | ✘ (undecided, D5) | ✘ (unverified money writes) | Only if D5 chooses option 3 |

---

## 9. Decisions needed

| ID | Decision | Options | Blocks |
|---|---|---|---|
| D1 | Stale cities: cut-off or decay, and must supply and demand be treated the same? | (a) both cut off at `maxRegionSeqAge`; (b) both decay linearly to 0 by `maxRegionSeqAge` (recommended: matches N5, symmetric) | Phase 0, 3 |
| D2 | Should the confidence factor also discount demand? | (a) yes, symmetric; (b) no, documented (understates scarcity); (c) demand uses its own factor | Phase 0 |
| D3 | Applied-signal step and band | e.g. at most ±2% of base per period, band ±10% (log §14.5 suggests ±5–10% as a test input) | Phase 1 |
| D4 | What is a "period"? | (a) one region ingestion; (b) a fixed region clock independent of city clocks (§22.4 warns against comparing city clocks) | Phase 1 |
| D5 | Source of persistent inflation | none / exogenous target / demand-pull in the simulator (§3.5) | Phase 5 beyond measurement |
| D6 | Labour model granularity | 3 qualification groups (recommended) vs the game's 5 education levels | Phase 4 |
| D7 | Commuting: part of the engine (a cross-city flow) or simulator only until the game provides data? | Recommended: simulator first; engine once the data contract is clear | Phase 4 |

---

## 10. Risks

- **Over-modelling.** Each added mechanism adds parameters that cannot be calibrated until real game data exists. Mitigation: add only what a named test needs; prefer fewer, bounded rules.
- **The simulator diverging from CS2.** The simulator is a stand-in. Results show whether the *regional layer* is stable, not how CS2 will react. Mitigation: label simulator outputs as synthetic; calibrate against L3 runs when the mod exists (§19).
- **Silent double counting.** Mitigation: the "no double response" regression test, an invariant that forecasts are never inputs, and the module rule that the engine never applies elasticity or supply response.
- **Replay determinism.** Stateful layers (applied signal, dynamics) must be pure functions of explicit state and seeded RNG, stored externally. Mitigation: extend replay tests to multi-period runs (the same seed gives an identical run hash).
- **Game hooks may not exist.** Gate C (V-17, V-18) is unverified. Everything above except Phase 6 is useful even if the mod stays read-only (§27 go/no-go: "read-only region builder").
