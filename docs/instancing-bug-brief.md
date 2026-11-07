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

## Files and scratch builds

- Code: tools/acr-ref/Instancing.cs, StaticMeshPackage.cs, DropIn.cs (MIC writer), Visuals.cs, LegacyPackage.cs.
- Config: config/visuals.toml `[instancing]` (now `stages = []`, off).
- Scratch builds of every test (configs, logs, containers):
  `%LOCALAPPDATA%\Temp\claude\C--Code-acr-extrastages-main\e0bc1bcd-465d-48d5-9463-4cf62f816fee\scratchpad\` (bisectA..J,
  fixK, diag, diag2 with zen header readers).
