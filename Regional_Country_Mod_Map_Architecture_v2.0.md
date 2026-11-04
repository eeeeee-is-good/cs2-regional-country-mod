# Regional Country Mod — Geographic Map Architecture (v2.0 addendum)

**Status:** Proposed architecture; not implemented or verified in a running CS2 build.  
**Date:** 2026-11-04  
**Scope:** How the existing independently simulated city-save system can support a visual, geographically continuous country map.  
**Supersedes:** The notion that all country-member saves must always be positioned arbitrarily. Freeform arrangement is still supported, but a second, optional mode uses a shared master terrain.

## Key design decision

Support two complementary region map modes:

1. **Freeform region (MVP):** Existing city saves may be placed on a symbolic or geographically approximate atlas. Place each city as a pin, rectangle, or footprint with a declared scale and orientation. Assign transport corridors manually. There is no guarantee terrain, river, or roads match at boundaries. A freeform adjacency does **not** mean the game can render a seamless boundary.
2. **Geographic region (future / separate prototype):** Author one **master region elevation dataset** in a single coordinate system. Cut that terrain into standard or MapExt-sized playable city tiles. Each city remains its own map/save and simulates only when open. The region UI displays the combined base map and snapshots of city activity. Geographic coordinates, elevation data, water-system design, and approved shared border connectors allow consistent roads, rails, waterways and route distances.

**Explicitly out of scope:** Making many independent saved cities into a single, simultaneously running, seamlessly streamed CS2 simulation; teleporting agents as rendered entities into another city save; generating high-fidelity skylines for inactive cities in the active game's camera without a dedicated renderer.

## Real game constraints found in published sources

- Colossal Order's CS2 Map Editor supports importing 4096×4096, 16-bit grayscale PNG/TIFF heightmaps for the *playable area*, with a separate optional surrounding worldmap. Roads and other outside connections can be placed within the editor. (https://colossalorder.fi/news/development-diary-map-editor/)
- Standard CS2 playable terrain is approximately 14,336×14,336 metres; a 2×2 mosaic spans ~28,672×28,672 m, assuming contiguous tiles with no gaps. Do not confuse the surrounding visual **worldmap** with a playable city terrain. (https://shankscs2.github.io/cs2-maps-wiki/docs/vanilla-map-information/terrain-and-heightmap-resolution)
- MapExt2 currently documents larger playable map modes ~28.672km, ~43.008km, and ~57.344km, still using 4096×4096 terrain images with different metres per pixel. This makes exact scale, game/mod version and terrain datum mandatory metadata. Its own README warns optional surrounding worldmaps can add substantial VRAM load. (https://github.com/Noel-leoN/MapExt2/blob/master/MapExtPDX/README.md)
- Vanilla outside connections include roads, rail, ships, electricity, water/sewage and air routes. Some types have special restrictions; the vanilla map editor allows map-edge road/rail/ship connections but not freely created airway points. (https://www.paradoxinteractive.com/games/cities-skylines-ii/features/maps-themes)

## Conceptual region atlas and layers

A map view operating **outside** the currently loaded CS2 simulation should support:

- **Terrain basemap:** Geo-referenced DEM/heightmap and water/coastlines, preferably rasterized once and cached at low zoom.
- **City footprints:** Tile bounding boxes, city owner, save version, in-game age, snapshot freshness and compatible CS2/MapExt profile.
- **Infrastructure:** Intercity graph (highways, railways, canals, sea lanes, airports, power), with visible edge health and matching endpoint positions.
- **City-level overlays:** Populations, jobs, major industries, import costs, federal policy impacts, migration flow estimates, and optional road/network outlines derived from published snapshots.
- **Navigation:** Select city, show economic summary, load its authoritative save, inspect neighboring connections. Rendering full developed cities in a seamless 3D scene is *not* part of the first implementation.

The country atlas and an individual game's playable terrain are separate presentation systems. Showing a complete atlas does **not** imply the game engine has loaded all constituent cities.

## Geographic coordinate contract

The country should be the source of truth for position, not an arbitrary UI pixel offset:

- Country origin, orientation (e.g. north-up), units in metres, optional geographic CRS/projection, sea-level/elevation datum, whole-region terrain dataset hash/version.
- For every city: immutable `cityId`, `saveId` and published revision; `boundsMinX`, `boundsMinY`, `widthMetres`, `heightMetres`, rotation/transform; `mapProfile` (vanilla, MapExt28, MapExt43, MapExt57); native local-to-country transform and terrain-hash.
- For every boundary connection: unique logical ID; owning city and neighboring city; mode (road/rail/ship/power/water/other); world position, orientation, elevation, geometry/network type, paired endpoint, tolerance and verification state.
- When city terrain changes near a shared border: detect stale terrain/endpoint metadata and require a new validation. Do not silently rewrite other players' saved terrain.

Illustrative data (NOT a game API):

```json
{
  "regionId": "region-demo",
  "coordinateUnits": "metres",
  "terrainRevision": "terrain-v1",
  "tiles": [
    {"cityId":"A", "originX":0, "originY":0, "sizeX":14336, "sizeY":14336, "mapProfile":"vanilla"},
    {"cityId":"B", "originX":14336, "originY":0, "sizeX":14336, "sizeY":14336, "mapProfile":"vanilla"}
  ],
  "connections": [
    {"id":"A-B-road-01", "aCity":"A", "bCity":"B", "mode":"road", "positionX":14336, "positionY":7120, "status":"unverified"}
  ]
}
```

Terrain samples along an adjoining edge must refer to the **same underlying region coordinates**. Avoid independently resizing adjacent 4096-pixel images to fit: it can shift mountains/rivers or produce a visible seam. A region-master elevation grid sampled consistently at tile boundaries is better. The exact CS2 importer pixel-center convention still requires empirical verification; 4096 raster samples are not automatically interchangeable with 4096 terrain vertices.

## Practical map generation workflow

1. User chooses **Freeform Country** or **Shared Geography Country**. Existing saves default to Freeform unless they provide trustworthy georeferencing.
2. For Shared Geography: choose an optional real-world DEM or procedurally generated master terrain, region extent, sea level, north orientation and standard tile size. Preserve all metadata as a versioned region package.
3. Generate consistent tile heightmaps and (optionally) context worldmaps. Provide previews of all borders, coastline and waterways before export.
4. Have the CS2 map editor import each generated tile into a separate map. Set outside connections and shared water design appropriately. **Do not claim a fully automatic CS2 map package builder exists until tested.**
5. Player starts a normal save from each generated map and registers its region ID and map transform. The mod detects candidate edge connections and validates matching endpoints and modes.
6. Publish low-cost map previews (raster/tile thumbnails plus possibly simplified road/rail outlines). Inactive cities need not be simulated or rendered as full 3D scenes.
7. A city owner can make changes in their own save. Crossing-border network edits require counterpart validation; region snapshots show outdated or missing links rather than fabricating continuous roads.

**Hydrology caveat:** Matching elevation doesn't ensure matching *water simulation*: sources, flows, sea-level inputs, and dam modifications can differ across save files. Initially aim for visual river continuity, not synchronized river water physics. A bridge/road across a border requires position, tangent/direction and height agreement as well as a matching mode.

## Where MapExt fits

MapExt is a compatibility **profile**, not a prerequisite for shared geography. A vanilla tile can be 14.336 km square; a larger MapExt tile can represent much more territory with fewer city saves. A 57.344 km MapExt city tile has lower native heightmap sampling density than vanilla if both use 4096×4096 pixels. Don't assume identical resource or roadway scale across mixed tile profiles. Nonuniform tile sizes and mixed vanilla/MapExt configurations require explicit coordinate transforms and may be disallowed in early geographical-region mode while remaining usable in freeform countries.

## Recommended incremental milestones

- **M0 (1–2 day exploratory prototype):** Standalone region atlas with three hard-coded rectangular city slots, click-to-select, symbolic route edges. No CS2 integration.
- **M1:** Make arbitrary existing saves appear as city nodes/footprints, persist positions and inspect outside connections. This delivers immediate player value and supports the economy MVP.
- **M2:** Offline master-heightmap slicer: use one synthetic mountain/river landscape, create four edge-matching standard city heightmaps; unit-test that border samples agree.
- **M3:** Import two adjacent heightmaps with CS2 Map Editor; inspect same river and road endpoints on both, manually create outside connections and validate region-transform math.
- **M4:** Automatic border-connector matching and atlas-road preview from snapshots, including stale-link detection after editing or Road Builder changes.
- **M5:** Region creation wizard to generate multiple map templates, optional MapExt profiles and one-click switch to published city saves. Test mixed terrain and mod dependencies separately.

## Risks and required tests

1. **Atlas vs in-game view:** Verify users understand that inactive cities appear on the atlas, not necessarily as rendered buildings from within an active city.
2. **Terrain seams:** Identical elevation along tile borders under multiple importer orientations; test flat, river, steep mountains and coastline.
3. **Water seams:** Water source placement and sea-level agreement; ensure no unexpected flooding when a generated tile loads.
4. **Connections:** Road/rail heading, height, type, actual edge reach and georeference matching; supported special cases for shipping and airway.
5. **Terrain drift:** Region master remains immutable or versioned; warn when local player terraforming creates a border mismatch.
6. **MapExt:** Test different selected widths, 4096 resampling, absolute height values, performance and outside-connections; note version specificity.
7. **Road Builder:** Change a previously linked custom road; re-read network geometry, validate/reject corridor and do not trust a stale prefab ID.
8. **Multiplayer:** Shared-country owner cannot silently remap or overwrite another owner's city. A city publication must include map extent, profile and compatibility fingerprint.
9. **Performance:** Atlas thumbnails and vector simplification must remain lightweight; don't import full geometry of every city into a running CS2 simulation.

## Clear MVP boundary

**First public version:** Freeform atlas with city footprints and manual/validated corridors.  
**Optional larger feature:** Shared master terrain with generated adjacent map tiles.  
**Explicit non-goal:** Multiple complete cities simultaneously present in one live CS2 world.

## Sources and provenance

- Colossal Order official map editor diary: https://colossalorder.fi/news/development-diary-map-editor/
- Official outside connection overview: https://www.paradoxinteractive.com/games/cities-skylines-ii/features/maps-themes
- Vanilla playable map / heightmap dimensions: https://shankscs2.github.io/cs2-maps-wiki/docs/vanilla-map-information/terrain-and-heightmap-resolution
- MapExt2 readme: https://github.com/Noel-leoN/MapExt2/blob/master/MapExtPDX/README.md
- Independent region/map-generator research example: https://github.com/KarlCarriere/cs-map-generator/blob/main/docs/adr/0002-cs2-export-format.md

All implementation methods are proposed; none constitutes direct verification of current CS2 game binaries, save compatibility or working map-package automation.
