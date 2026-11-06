# Instanced trees: plan and findings

Why: stage visuals are one ProceduralMeshComponent (no instancing, no LODs), so each DR2 tree copy costs its full ~3,000
triangles and only ~50 real trees per stage fit. Writing cooked StaticMesh assets without the editor is not practical, so
the plan is to place ACR's own tree meshes through cloned instanced components, at DR2's tree positions. [V] verified
offline from exports of all 163 Hafren South cells, [I] inferred. Nothing here is tested in game yet.

## What exists in Hafren South [V]

| Component | Exports | Owner | Instances |
|---|---|---|---|
| HierarchicalInstancedStaticMeshComponent | 653 | PCGPartitionActor | 65,496 |
| FoliageInstancedStaticMeshComponent | 211 | InstancedFoliageActor | 6,635 |
| ReplaceableFoliageISMC | 197 | PCGPartitionActor, InstancedFoliageActor | 2,382 |
| CustomGrassRegistererComponent | 649 | PCGGrassContainer, InstancedFoliageActor | 415,505 |
| InstancedStaticMeshComponent | 49 | BC_InstancedMeshContainer_C | 2,629 |

The gameplay cell 22VG has no instanced component: 6 BoxComponent, 837 SplineMeshComponent (RespawnPlanes), 2
ProceduralMeshComponent, 37 TvCamera. BC_TrackAvatar lists ReplaceableFoliageISMC under IgnoredComponentClasses.

## Serialized format [V by bytes, cell 3QNL7M8YURUHOOLI89WK0Y6SR]

Native tail after the unversioned properties (a plain ISMC has no cluster block):

- `int32 128 | int32 N | N x FMatrix`: 4x4 float64 row-major, rows 0-2 axes times scale, row 3 translation, component-local
- `int32 4 | int32 0 | int32 0`
- `int32 64 | int32 nodes | nodes x FClusterNode`, 64 bytes: BoundMin f32x3, FirstChild i32, BoundMax f32x3, LastChild i32,
  FirstInstance i32, LastInstance i32, MinInstanceScale f32x3, MaxInstanceScale f32x3

To change count or transforms, rewrite: the instance and tree blocks, SortedInstances[N], InstanceReorderTable[N] (its
inverse), NumBuiltInstances, InstanceCountToRender, BuiltInstanceBounds, PerInstanceSMCustomData; drop CachedBounds[0..2].
Slots come from `acr-ref schema HierarchicalInstancedStaticMeshComponent`; Replaceable and FISMC shift them by +6.

Cluster tree: the simplest valid tree is one leaf over [0, N-1] (FirstChild = -1) with identity tables. It culls all or
nothing per component, so split per species per 100-200 m chunk. An empty tree with NumBuiltInstances = 0 may rebuild at
runtime [I], unproven.

## Meshes [V]

Shared packages `/Game/Foliage/Trees/<Species>/SM_*`, usable from any stage. Not Nanite (moderate to high confidence): 4
LODs, e.g. SpruceWales06 4867 / 4875 / 8 / 10 triangles, so far trees drop to small billboards.

| DR2 species | Closest ACR meshes |
|---|---|
| fir, pine | SpruceWales, Spruce, Blackpine, AleppoPine |
| birch | Silverbirch01-14 |
| deciduous | Blackmaple, BeechBare, Fig |
| bushes | Hedge, Boxthorn, Smoketree |
| poplar | no match: Cypress or tall Silverbirch |
| eucalyptus | no match: Baretree, Olivetree, Blackpine |

## Imports and collision [V]

Cross-package imports work (LegacyPackage.AddImport, as the material imports in Visuals.cs). Set BodyInstance to NoCollision,
as the game does on 434 HISM and 177 Replaceable components; car physics is the BakedData we write.

## Decision: DR2's trees, not ACR's

The goal is DR2's own look, so the species table above is only a fallback. ACR tree meshes are classic LOD meshes (sections,
position, vertex, colour and index buffers; SM_SpruceWales06 LOD0 has 2+ sections, 5487 vertices, 4 UV channels), so a
stage-owned StaticMesh package can be cloned from one and filled with DR2's geometry, as UiTexture.Write does for textures.
LOD0 is the DR2 mesh; the far LOD is DR2's own billboard. DR2's materials stay as the MICs the build already writes. The same
writer can later carry rocks, bushes and grass. Open points: CardRepresentationData, BodySetup, the number of LODs.

## Offline proof (round 2; flag [instancing] enabled = 0 by default)

Part 1: `StaticMeshPackage` clones SM_SpruceWales06 and writes DR2's fin_dougfir_tall_02_a (2,816 vertices, 2,823 triangles, trunk + 3 leaf
materials as the build's MICs) plus a 2-view crossed-card billboard LOD; BodySetup without its capsule; byte layout in the class comment.
Part 2: `LegacyPackage.AddExport` (+ RemoveImports, rebuilt preload dependencies, depends table) adds a plain HISM (NoCollision, one leaf
cluster, identity tables) owned by BC_ReplayCamerasActor_C (it already serialises InstanceComponents) with 300 real placements.
`Instancing.Check` runs inside stage-check; `acr-ref instancing-verify` re-reads both through CUE4Parse. Unproven until tested in game.

## The gap and the plan

LegacyPackage has no way to add an export. Add one (96-byte export entry, preload dependencies, ExportCount, the owning
actor's InstanceComponents, AttachParent); crowd spawners need the same. Fallback: turn spare RespawnPlanes exports into HISM
by swapping class and template. Reusing HISM exports in the Hafren cells does not work: they stream by Hafren's bounds.

1. One HISM clone, one species, a few hundred trees, one stage; check it survives retoc to-zen; test in game.
2. Species map, chunking, a real cluster tree, DR2 cards and stubs removed for instanced trees.
3. InvisibleCheck must count instanced trunks as drawn; ACR trunks can be thinner than DR2's colliders, so size colliders from
   the ACR mesh's own capsule.

Risks: cluster-tree validity, stale CachedBounds, retoc accepting a new export, the level offset in component-local space.

## In-game result (2026-11-06)

[V in game] Finland 01 r2 with 300 instanced `fin_dougfir_tall_02_a`: ACR loads it, the trees render with their real trunks, branches
and leaves (no merged real tree stands on that stretch, so these are the instances), about 117-120 FPS. The added component on
BC_ReplayCamerasActor_C stays loaded and the cloned StaticMesh, its MICs and tangents work. Not looked at yet: the billboard LOD
switch, and how one leaf per component performs at full scale.

Next: remove the card and stub of instanced trees, then every Finland species (per-species meshes, chunked components, a real cluster
tree), InvisibleCheck counting instanced trunks as drawn, then trees.bin plants, rocks at full detail and the other locations.
