# Stage variants: several DR2 routes in one ACR track

**Goal:** the DR2 routes that share a road become one ACR track with several variants (Forward, Reverse, later the
half-length sections), built into **one** stage container instead of one container per route.

**Status (2026-11-06):** phase 1 (pairs) implemented and on for every pair in `config/variants.toml`. Argentina 01 r3 + r4 was
checked in game (each variant starts from its own end); the other pairs are verified offline only. What is where:

- `dr2-extract stage-ir --also <route>`: ground (each route's own track.jpk outside the corridors before it), objects,
  render meshes and the ground bake cover the other route too; `stage.json` lists them in `corridor_also`.
- `acr-ref stage-build --reverse-ir <dir> --variants config/variants.toml`: one placement for both routes; the reverse
  route fills cell 7FF9 (splines, timing actors, respawn walls, the same DR2 visuals) and `DT_PacenoteHafrenSouthReverse`;
  the scenery retag keeps 7FF9 on the reverse gameplay layer only and moves every other cell to the hidden layer; the
  GameConfigurator's FullReverse entry loads the gameplay layer instead of the rally props; bounds keep 7FF9 resident.
- `acr-additive`: `Variants[].Stage` (name, sectors, elevation from that route), a check that a `DataLayers` override
  decodes to its rows.
- launcher: `[pairs]` loader (`Pairs.cs`); prepare runs the forward with `--also` and the reverse gameplay-only; the
  spec gets a FullReverse variant (`DataLayers = [HafrenSouthFullReverseGameplayActors]`) and the reverse pacenote
  clone; a selected reverse stage builds as its forward stage; the UI marks both.
- Offline result, Argentina 01 r3 + r4 (El Rodeo / La Merced, 2.8 km each): container 858 MB for both directions (the
  single Argentina 01 r2 is 931 MB); verify ALL PASS (45 checks, registry round trip byte-identical). CUE4Parse re-read of
  the container: 7FF9 holds the reverse route (328 spline points, starting 102 m from the forward finish) and points at
  `DT_PacenoteAcrXsAr01r3Reverse` (45 rows); GameConfigurator FullReverse loads only the reverse gameplay layer; only
  22VG has BE01, only 7FF9 has B42F, the other 161 streaming cells are on C2CD. The DT row's `DataLayers.Common.Value`
  is a **TSet** (int32 removed, int32 count, refs), not an array: the registry now edits the template's bytes.
- Not yet: per-variant menu map / elevation images (the reverse shows the forward's), stage-check / stage-verify of
  the reverse cell, moving the visuals into one shared cell instead of copying them (~140 MB per pair).

## 1. What DR2 has [V offline: work/layouts, dr2-extract route-layout]

- Every location has two rally areas (`<location>_rally_0N`), each with 6 routes on one road network:
  - r0 / r1: the full road, forward and reverse (8–16.5 km);
  - r2–r5: about half the road each, alternating forward / reverse. Every one of them lies 96–100% on the r0 road.
- Routes pair up as exact reverses of each other: usually (r0, r1), (r2, r5), (r3, r4).
  - Matched by start ↔ finish position: 67 pairs over the 26 areas; 45 pairs have both routes ≤ 9 km.
  - 22 routes found no partner by start/finish alone (Germany 01, Spain 01 r0/r1, ...). They may be reverses with
    moved start / finish lines; pair by road overlap + opposite direction instead (to do).
- Each route has its own start, splits, finish, pacenotes and triggers, so every variant is a full stage.

## 2. What ACR has [V offline: acr-ref export of both cells]

- A track's variants share the level and its BakedData (physics). DT_TracksVariants has one row per variant; its
  `DataLayers` (and the level's `GameConfigurator.Configurations[*].DataLayersToLoad`) choose which cells stream in.
- Hafren South: FullForward loads BE01 (forward gameplay) + E8A3 (forward props); FullReverse loads B42F + C2CD.
- Gameplay per direction lives in one generated cell:
  - forward `22VG0QIP9ATUQI633K86KWPFP`, reverse `7FF9A0XR2HIRJVFZU6AEAFHMU`;
  - same actor kinds in both (SplinesActor, 4 RaceSector, start / end triggers, CompleStart, PostRaceCamera,
    RespawnPlanes, PacenoteSetupActor, ...); only spline-mesh / TV-camera counts differ (837 vs 855, 37 vs 38);
  - `PacenoteSetupActor.PacenoteTable` → `DT_PacenoteHafrenSouthForward` / `...Reverse`.
- So two variants fit the host as it is. More than two needs new data layers in the cloned level (see 5).

## 3. Disk

Measured on Finland 01 r2 (1017 MB container): DR2 textures 359 MB, BakedData 190 MB, DR2 ground / object meshes
140 MB (written into the forward cell), the rest ~330 MB the cloned level and its cells. All of it depends on the road,
not on the direction.

| | today | grouped |
|---|---|---|
| forward + reverse pair | 2 containers, ~2 GB | 1 container, ~1.05 GB (visuals in a shared cell) or ~1.2 GB (visuals copied into both cells) |
| whole area (6 routes) | ~6 GB | ~1.5 GB (world sized for the full road) |

## 4. Phase 1: forward + reverse pairs

1. **Config / ids.** `config/stages.toml` gets a pairing (`reverse_of = "<dr2 id>"`, generated from the route
   overlap); the pair keeps the forward stage's Id. Launcher rows show one track with its variants; selecting either
   builds the pair.
2. **Placement.** One placement (rotation, offset, world bounds) for the union of both routes' gates. Ground,
   object colliders, vista and textures from the union corridor (nearly identical roads, so close to today's size).
3. **Reverse cell.** `StageBuild.BuildCell` parameterised by cell (22VG / 7FF9) and IR: splines, sectors, triggers,
   respawn, post-race camera from the reverse route; pacenotes into a relocated `DT_Pacenote<Id>Reverse`.
4. **Visuals for both directions.** Move the DR2 visuals (today in 22VG) to a cell both variants load, or copy them into
   7FF9 as a first step (+~150 MB).
5. **Layers.** The scenery retag hides Hafren cells by giving them reverse-only layers, so today the reverse variant
   would stream all of Hafren back in. New rule: hidden cells go to a layer **no** variant loads; 7FF9 keeps only its
   reverse gameplay layer; FullReverse loads only that layer (variant row + GameConfigurator).
6. **Registry / menu.** Second variant row (FullReverse) with the reverse route's name, length, elevation, map and
   elevation images; the track card keeps the road name.
7. **Checks.** stage-verify / stage-check / round trips for the reverse cell; registry verify for two variants.

**In-game checkpoint after step 5**, before polishing 6–7: the reverse variant loads, the car starts on the reverse
start line, splits and finish fire, pacenotes play, no Hafren scenery appears. Steps 3–5 are the unverified part.

## 5. Phase 2: the other routes (sections)

- **Hafren North already has six variants** (DT_DataLayers [V]): FullForward / FullReverse, Cut1Forward / Cut1Reverse,
  Cut2Forward / Cut2Reverse, each with its own GameplayActors and RallyProps layers. That is DR2's layout (full road +
  two halves, both directions). Hosting a DR2 area on a Hafren North clone would give six gameplay cells without any
  World Partition editing; the cost is porting the host-specific parts of stage-build (host splines, sectors, BakedData,
  scenery survey, bounds constants) from Hafren South to Hafren North.

- Needs one gameplay cell + data layer per variant. The clone owns its persistent level, so new layers would not touch
  vanilla, but adding a data-layer instance, a streaming cell and its runtime-cell entries is World Partition editing
  not done here yet (rejected for the *vanilla* level in additive-plan.md 2a for different reasons).
- Possible shortcut to test first: reuse the 22VG / 7FF9 cells and switch only the gameplay inside them per variant
  is **not** possible at runtime (one cooked cell = one set of actors); so new cells it is.
- The world must cover the full road, which is over 9 km in most areas: this meets the length limit (README "too long").
  Areas whose full road fits (Argentina 01 8.3 km) are the first candidates.

## 6. Open questions

- [I] Which of `DT_TracksVariants.DataLayers` and `GameConfigurator.DataLayersToLoad` the game actually uses to pick
  cells (the spec's DataLayers override is unused so far, its struct encoding a guess).
- [I] Whether a data layer that no variant activates keeps its cells unloaded (expected: yes, same as today's forward
  variant with reverse layers).
- Launcher: how selection, "installed" and size show for a pair.
