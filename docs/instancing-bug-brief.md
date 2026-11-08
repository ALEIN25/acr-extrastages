# Bug brief: instanced trees do not render in Assetto Corsa Rally

Self-contained summary for a second opinion. Branch `stage-visuals-and-variants` of github.com/ALEIN25/acr-extrastages.
Background and format details: docs/instancing-plan.md.

## Setup

- Assetto Corsa Rally (ACR): Unreal Engine 5.6, cooked, shipping build, no logs available. Mods are loaded as zen containers
  (`.utoc/.ucas/.pak`) from `acr/Content/Paks/~mods`.
- We build stages without the editor. A stage is a cloned host level (Hafren South) whose World Partition gameplay cell
  (`22VG...`, forward; `7FF9...`, reverse variant) we edit at byte level (tools/acr-ref/LegacyPackage.cs), then
  `acr-additive relocate` renames the level package, and `retoc to-zen` packs it.
- Trees (tools/acr-ref/Instancing.cs, StaticMeshPackage.cs):
  - One cooked StaticMesh package per DR2 tree species, made by cloning ACR's `/Game/Foliage/Trees/SpruceWales/SM_SpruceWales06`
    and replacing its render data (LOD0 = DR2 mesh, LOD1 = 2-card billboard). Not Nanite. No collision.
  - Materials: our own MaterialInstanceConstant packages under `/Game/AcrXs/Stages/<id>/Mat/`, made by cloning a cooked MIC and
    swapping textures (tools/acr-ref/DropIn.cs ObjectMic/MaskedMic). Parents: `M_Simple` (trunks), `M_MasterMaskedSurfaces`
    (leaves, cards). Each MIC carries an inline static-permutation material resource (cloned), and retoc writes
    `shader_map_hashes: []` for them.
  - Placement: HierarchicalInstancedStaticMeshComponents added with `LegacyPackage.AddExport` to the cell, owned by
    `BC_ReplayCamerasActor_C` (listed in its InstanceComponents, attached to its DefaultSceneRoot), one per species per 200 m
    chunk, one-leaf cluster tree with identity SortedInstances / InstanceReorderTable, CachedBounds dropped, NoCollision.
  - The cell also has one merged ProceduralMeshComponent with all other visuals (works in game).

## What happens in game

Finland 01 r2, forward and reverse variants. "Renders" = real 3D trees visible.

| Build | Instanced species | Merged mesh still uses some tree MICs | Result |
|---|---|---|---|
| Proof (commit 9fdfd63) | 1 (fir), 300 trees, 1 component | yes | renders |
| A | 1 (fir), 679 trees, 60 components | yes | renders |
| C | 7 (fir, 2 bare, bare variant, 2 birch, full fir) | yes (other firs) | only the fir species render; birch/bare do not |
| D, E | 4 each | yes | renders (firs) |
| B, F, G, H, I, J | 12 or 13 (also capped to 1,551 trees; split per cell; meshes split over two containers) | no | nothing renders |
| K (placeholder fix) | 13, cell imports all 25 tree MICs via zero-area merged-mesh sections | yes, as placeholders only | nothing renders |

Also: a user `Engine.ini` with `r.PSOPrecache.ProxyCreationWhenPSOReady=0` and `r.PSOPrecache.Components=0` changed nothing,
and waiting 10 minutes changed nothing. The rest of the level always renders.

## Verified offline

- Per-package content is byte-identical across builds for shared packages (meshes, MICs).
- Build C vs build I: the forward cell is byte-identical in every export except the merged ProceduralMeshComponent (same 329
  HISM exports, same owner actor, same export map, dependencies and preload arrays). C renders the fir trees, I renders nothing.
- Every import resolves in zen: cell → mesh (ImportedPublicExportHashes match the mesh packages' PublicExportHash), mesh →
  MICs, MIC → textures and parents (parents exist in the game paks). No missing packages; store entries list all imported
  packages.
- Packages survive `retoc to-zen` / `to-legacy` byte-exact; CUE4Parse loads every mesh, MIC and HISM with correct counts,
  bounds and instance transforms.

## Open question

What makes a species' HISMs render in C (fir) but not in K, when K's cell even imports every tree MIC? The visible difference in
C is that real merged-mesh triangles (not zero-area placeholders) were drawn with the fir MICs. Suspects: the MICs' inline
static-permutation shader maps lacking the instanced-static-mesh vertex factory (only built or found once the material is used
by a drawn primitive?), material usage flags, or cooked shader library lookups for our cloned MICs.

## Follow-up controls ready for an in-game run

The stock `MI_SisteronConcrete01` (trunk template) and `MI_FraChaBldWarehouseAProps04_OP` (leaf and billboard template) each list two shader-map hashes in their vanilla IoStore package-store entry. The corresponding tall-fir clones have byte-identical MIC exports with both hashes still embedded, but their package-store lists are empty in C and I. A valid inline material resource and a populated package-store shader list are separate checks. C's reported result also means the empty lists alone cannot explain the difference between C and I.

K has 542 HISMs and 4,696 instances in its forward cell. C and I have 329 HISMs and 2,982 instances there. Use I as the baseline for the following comparisons.

The three replacement container sets are under `work/instancing-diagnostic/`. Each passed `retoc verify`. Keep the registry mod installed and swap the full Finland stage trio (`.pak`, `.utoc`, `.ucas`) with ACR closed. Test Finland 01 r2 forward at the same location and load the unmodified I trio as the baseline. The three sets use the same filenames and must be tested separately.

| Container set | Change from build I | What to look for |
|---|---|---|
| `shader-map-metadata` | Restores the two original shader-map hashes for each of the 25 tree MICs in the container header. All asset chunks have the same hashes as I. | If trees return with DR2 textures, the package-store shader metadata is sufficient to recover rendering in this build. |
| `default-material` | Clears the material references in all slots of the 13 tree meshes to exercise the engine's default surface material. Both gameplay cells match I. | If default-material trees render, the cloned meshes and HISM placements can draw with a different material path. |
| `stock-material` | Uses vanilla `MI_SpruceWales06BarkMat` in every tree mesh slot. Both gameplay cells match I. | A positive result independently implicates the custom material path. The donor is a masked SpeedTree material, so prefer the default-material control when interpreting a negative result. |

If the metadata test fails and the default-material control renders, compare the cooked shader maps for the instanced vertex factory and test a donor with known instanced permutations.

## Stock tree comparison at the Finland start

Two in-game tests were reported with no visible 3D trees at the forward start. Build I's serialized forward HISM transforms put eight tree origins within 25 m of the start gate and 30 within 50 m. The nearest is 10.25 m away in plan view.

The replacement trio in `work/instancing-diagnostic/stock-tree-start-grid/` adds four stock spruces roughly 25 m and 53 m past the forward gate. Both pairs use the original ACR `SM_SpruceWales06` asset with its stock materials. The left pair uses one HISM with the stock mesh's bounds. The right pair uses two plain StaticMeshComponents. All three components share the replay-camera owner and attachment root. Only the forward gameplay cell and container header changed from I. The container passed `retoc verify`, and CUE4Parse resolved the stock mesh and attachment root for all three new exports and read both HISM transforms.

| In-game observation | What it narrows down |
|---|---|
| Left and right spruces appear | The added HISM can draw stock content here. Compare the generated tree mesh and its serialized bounds against the stock asset. |
| Right spruces alone appear | The stock mesh and shared owner can draw. Compare the HISM's native instance data and initialization with a vanilla HISM. |
| Neither pair appears | Check component registration, actor visibility and mounting before using this test to infer anything about the material or HISM. |

The two-sided build crashed on `FAsyncLoadingThread` with an access violation 35 seconds after launch. The stack does not identify which export caused the crash. The replacement trio in `work/instancing-diagnostic/stock-spruce-hism-only/` keeps the left HISM export byte-identical to the two-sided build and omits both right-side components. Relative to I, it adds one HISM and updates its owner's component list. The two spruces are still on the left, roughly 25 m and 53 m past the forward gate. The trio passed `retoc verify`, and CUE4Parse resolved the stock mesh and read both transforms. Its in-game result is pending.

## Files and scratch builds

- Code: tools/acr-ref/Instancing.cs, StaticMeshPackage.cs, DropIn.cs (MIC writer), Visuals.cs, LegacyPackage.cs.
- Config: config/visuals.toml `[instancing]` (now `stages = []`, off).
- Scratch builds of every test (configs, logs, containers):
  (deleted 2026-11-07 to free disk space) `%LOCALAPPDATA%\Temp\claude\C--Code-acr-extrastages-main\e0bc1bcd-465d-48d5-9463-4cf62f816fee\scratchpad\` (bisectA..J,
  fixK, diag, diag2 with zen header readers).

## Re-reading the results: the HISMs never drew (2026-11-07)

- stock-spruce-hism-only: no trees either. Stock mesh + stock materials on our HISM do not draw, so meshes and materials are not the cause.
- Every "renders" build (Proof, A, C, D, E) still had ~64k triangles of NON-instanced fir species in the merged mesh (build.log `objects:tree:*`);
  every "nothing" build (B, F-K) had 16k (logs only). In C the instanced species were fir tall_02, fir full_01, birch and bare; fir
  tall_01, tall_03 and full_02 stayed merged. The firs seen in game were most likely merged-mesh firs; the instanced birch/bare never showed.
- Suspect: the owner `BC_ReplayCamerasActor_C` (parent: native `/Script/acr.ReplayCamerasActor`) is hidden outside replays, and a primitive
  of a hidden actor does not draw. The merged ProceduralMeshComponent that does draw is owned by `BC_StartSequenceTrigger_C`.
- Test: `[instancing] owner = "BC_StartSequenceTrigger_C"` (Instancing.cs): the HISMs are outered to the start trigger, still attached to the
  replay actor's DefaultSceneRoot (same transforms), not listed in its InstanceComponents. Finland 01 r2, 13 species, both cells:
  stage-build 276 PASS / 0 FAIL, retoc verify OK. In game: NOTHING (no instanced trees at the forward start). So the owner actor
  is not the cause, or not the only one. Build (installed now): C:\Users\leine\AppData\Local\Temp\claude\d97ef472ef1d0aba\69704786-b825-47ff-8bba-a63fda8c2d27\scratchpad\own
  (run2.sh, build.js, wt\config, build\ = legacy stage-build output, out\ = relocated legacy, dist\ = zen trio).
