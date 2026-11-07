# Regions Sandbox

A map-based playground for the regional engine. Build a region of cities, connect them by road, rail and ship, set production, tariffs and subsidies, and watch what steel costs in each city and why. Every number comes from the real engine (`RegionalEngine`), and the live rule check runs after every change. Nothing here touches Cities: Skylines II.

## Start it

From the repository folder:

```bash
dotnet run --project RegionsPlayground
```

Then open <http://localhost:5280> in your browser. Stop it with Ctrl+C in the terminal. It only listens on your own machine.

## Play

| Tool | Key | What it does |
|---|---|---|
| Move / select | V | Click a city to see its steel price and edit it; drag it to move it. Click a link to edit it. |
| + City | C | Click empty space to place a city. |
| Road / Rail / Ship | R / T / S | Click one city, then another, to connect them. |
| Delete | X | Click a city or link to remove it. Delete or Backspace removes the current selection. |

- **On the map:** each city shows the cheapest delivered steel price, from green (as cheap as local production) to red (as expensive as the world market or worse). Producing cities show "makes steel".
- **Selecting a city** highlights the winning supply route and animates goods along it. The side panel breaks every offer down into producer price, freight, customs and subsidy, or gives the reason it was excluded.
- **Links you draw** follow the map distance as you drag cities. Type a distance to fix it instead.
- **Policies:** import tariff and per-mode freight subsidies with daily budgets. The forecast table shows when a budget is too small and the subsidy is scaled down.
- **Prices and transport costs:** the steel producer and world prices, and each mode's fixed and per-km cost.
- **Supply and demand** (on by default, in "Prices and transport costs"):
  - A producer sells its spare steel at the normal price. When the cities leaning on it need more, its price climbs (default +25% of the price per 100% overload, at most +100%), until buyers would rather import or buy elsewhere.
  - An overloaded producer's map tag shows "sells at …".
  - Selecting a producer shows **its supply and demand chart**: the supply curve, each buyer's demand step at the price where it would switch, and the price where they meet. A table lists who buys, who is on the edge and who goes elsewhere.
  - Selecting a consumer shows how much steel it needs. The orange part of a price bar is the scarcity premium.
  - **Try this:** load G03. Ironvale has 300 spare and Carford needs 500, so Ironvale's price rises to the world ceiling (Carford pays 140). Raise Ironvale's output to 1150 and the price drops to 136.78. Lower Carford's use to 300 and it falls to 134.
- **Load an example** opens the built-in scenarios (G01–G10, corridor). Your current map is kept in the browser between visits. **Export** and **Import** save it to a file.
- **The badge at the top right** is the invariant checker (log §18.4). Click it for details.

## How it works

`Playground.cs` turns the map into a real `RegionState`: one synthetic city per map city, one transport gate per mode it uses, and one snapshot each. It then calls `LandedCostEngine.Quote`, `Check` and `Forecast`. The server keeps no state: the page posts the whole map and gets the analysis back. `Tests/Scenarios.Tests/PlaygroundTests.cs` checks that the sandbox gives exactly the engine's prices for every built-in scenario.

This version models one resource (Steel). Supply follows the A4 rule: provisional or missing figures are downgraded, and unknown confidence is excluded. Scarcity pricing follows the rule in Contracts/README.md "Scarcity pricing".
