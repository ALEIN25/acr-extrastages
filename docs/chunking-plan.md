# Spatial PMC chunking design

Status: research/design only, at 6948b88 (`stage-visuals-and-variants`). No build or game test performed. No game directory accessed. Runtime conclusions below are hypotheses unless explicitly reported in the bug brief.

## Recommendation

Prove two independently visible, correctly bounded **existing** ProceduralMeshComponents first. Then test exactly one appended PMC under the working owner before scaling to 200–300 m chunks. Do not infer registration success from successful serialization, CUE4Parse parsing, or retoc verification: all passed for components that did not draw. Keep the single-PMC path as the default and fallback.

## Evidence

Read `docs/instancing-bug-brief.md`, `docs/instancing-plan.md`, `tools/acr-ref/Visuals.cs`, `ProcMesh.cs`, `LegacyPackage.cs`, and `Instancing.cs`. The later correction in the bug brief supersedes earlier claims of successful instancing: visible firs were merged geometry. Changing the HISM outer to the start actor still produced nothing. The two-SMC diagnostic crashed on FAsyncLoadingThread; its stack does not establish which export or dependency was responsible.

Read the actual combo2 build cell with `work/bin/acr-ref/acr-ref.exe legacy-dump .../22VG0QIP9ATUQI633K86KWPFP.umap --limit 0`, with ACR_USMAP pointing to `work/mappings/25170642.usmap`. Both required native DLLs were already beside the executable; no copy was needed. Dump indices below are **one-based FPackageIndex**, whereas C# export array indices are zero-based.

| Export | Actual cooked facts |
|---|---|
| 3 | End trigger actor; CBS 877,846,865; SBC -10,-3,-76,-15,-63,-12; CBC 861 |
| 6 | Start trigger actor; CBS 878,847,865; SBC -11,-6,-77,-16,-64,-13; CBC 861 |
| 865 | End `MeshComponent`, PMC, outer 3, flags 0x40008, 1,721 bytes; CBS 7,846; SBC -63,-12,-36; CBC 3 |
| 866 | Start `MeshComponent`, PMC, outer 6, flags 0x40008, 1,280,791,808 bytes; CBS 8,847; SBC -64,-13,-36; CBC 6 |

Imports -63/-64 are respectively the end/start template MeshComponent under the corresponding actor CDO; -36 is the PMC class. Both actors depend on 865, consistent with the current retarget path. The geometry remains outered to the start actor in this cell. The dump establishes size and dependency structure, not the supplied approximate 800-section/11.8M-triangle counts, nor actual GPU submission every frame. Those counts should be independently reported by the future chunk validator.

`Visuals.Build` around lines 805–838 rebuilds one component, retargets the host actor's MeshComponent reference and CBS edge to the other ring, and optionally changes outer/attachment/dependencies and clears RF_DefaultSubObject. Its header explicitly records that native StartEndSequenceTrigger previously made the visuals disappear during gameplay. Thus the end ring is not an inert spare.

## Component creation and loader contract

Sections share one primitive/component bounds for ordinary primitive visibility; splitting only the section array does not supply independently positioned primitives. Independent component bounds are the practical target. Engine implementation is not present in this repository, so precise section-level rendering behavior is an engine assumption to validate in game.

Reusing export 865 avoids adding exports, archetypes and new registration paths, making it the lowest serialization risk. Preserve its current class/template/outer/flags and existing actor references initially. Other existing actors/components are not automatically safe: lifecycle hiding, streaming, construction and attachment can differ. Do not convert respawn spline components or consume collision/trigger components.

For N components, cloning the working PMC is the preferred *candidate*, not a proven recipe. Use its 96-byte export entry through `LegacyPackage.AddExport` (lines 198 onward), preserving class/super and template compatibility, with a unique name and an explicitly chosen creation model. A uniquely named appended instance is not automatically another Blueprint default subobject: blindly preserving 0x40008 and the start MeshComponent archetype may conflict with native/default-subobject handling. Test the existing flags/template clone against a deliberately instance-created representation separately; do not silently mix the models.

Required design invariants:

- Outer owns the component; AttachParent is a valid scene component and its dependencies agree. Prefer the proven start owner for the first appended experiment; avoid the unproven replay owner.
- Preserve the donor's prerequisite groups and audit all embedded object references, including slot 2 BodySetup, slot 124 parent, material refs and any tail references. Reusing a BodySetup is only acceptable while collision remains disabled and runtime does not mutate it; otherwise clone/remap it too.
- Component CBC includes owner; class/template prerequisites retain their donor ordering. CBS includes referenced parent/body/material objects as appropriate to the donor model. Actor CBS includes new component creation before actor serialization where it serializes the reference. Do not create a serialize-before-serialize cycle.
- Actor serialized ownership/creation lists must match the selected model. Instance components belong in InstanceComponents; Blueprint-created components require the matching BlueprintCreatedComponents/creation-method representation. These are not interchangeable lists, and neither list alone proves registration. Inspect the trigger's actual schema and data before adding a list: the replay-specific slot 78 patch cannot be copied into a trigger actor.
- Keep native MeshComponent properties aimed at the dedicated control ring, never an added visuals chunk. Preserve any Blueprint construction/default-subobject references required by the original two PMCs.

`Instancing.Build` line 445 adds HISMs with CBS mesh/parent, SBC class/CDO, CBC owner; lines 461 onward append replay InstanceComponents and actor CBS. The alternate owner path omits that list update. These are examples of byte writing, not evidence that this contract registered a primitive. No repository evidence establishes the exact actor-list changes sufficient for a new PMC. An in-game one-clone test is mandatory before asserting sufficiency.

`AddExport` appends an empty legacy depends-table entry and records four preload groups. `LegacyPackage.Save` rebuilds export offsets/sizes, preload array/counts and per-export group ranges. Use SetExportDeps preserving all four old groups; do not hand-edit offsets or replace unrelated dependencies. Validate both legacy and converted Zen graphs, including absence of cycles and stale package indices. Adding a name/export alone does not establish registration.

## Bounds

Explicit bounds already exist. `ProcMesh.WriteSection` computes min/max from that section's positions and writes SectionLocalBox as six doubles plus valid byte (49 bytes). `BuildComponent` computes min/max over all section positions and writes slot 5 LocalBounds: origin=(min+max)/2, extent=(max-min)/2, sphere radius=length(extent). It also makes location/rotation/scale absolute and visible, clears HiddenInGame, and omits relative transforms. Vertices use world UE centimetres; do not subtract a chunk origin without changing this transform contract.

Therefore no reliance on an assumed UE recomputation is needed. UE may recompute bounds from section boxes during updates, but this repo cannot prove that lifecycle. Write both representations correctly. Empty sections currently have a valid zero box; even a dummy vertex at world origin can enlarge a far chunk's union. Keep the slot-zero dummy within each chunk's own box (or explicitly account for empty dummy bounds), and ensure no stage-wide section, stray vertex or parent-bound setting defeats tight bounds. Validate finite positions, boxes enclosing all vertices, union LocalBounds, radius and effective world transform after serialization. Distance culling additionally requires supported primitive distance settings; splitting alone does not enable a maximum draw distance.

## First experiment

1. Keep the current single-PMC build as baseline. Prepare a two-existing-PMC variant only, with the same triangles/materials and no new exports or actor-list edits. Split triangles at a spatial plane chosen to make two clearly separated boxes; retain whole triangles and compact vertices per side. Assigning whole material sections will usually leave stage-wide boxes and is unsuitable.
2. Put the far part into 865 using its own parsed donor and `BuildComponent`, keeping slot 0 reserved on both components. Preserve the native ring geometry/control slot on 865; offset its visuals material slots accordingly. The original ring near the finish may enlarge its bounds: report that explicitly. Both triggers currently reference 865 and may change visibility/materials or clear sections. Test loading, countdown, driving, finish and restart. This experiment fails as a culling proof if native code suppresses its geometry.
3. If the ring lifecycle interferes, stop this variant. A subsequent controlled experiment must separate the control ring from both visual carriers (likely one appended, minimally populated PMC); simply pointing triggers back at the first visual chunk reintroduces the known bug. Reparenting alone cannot remove a native property reference.
4. At fixed resolution/settings/weather, record baseline and variant GPU/CPU frame time, median and p95 frame time over repeated identical stationary camera views and drives. Use available game/engine counters or a frame capture to inspect primitive visibility, submitted triangles and draw calls. Compare facing toward and away from a complete chunk, with neither box intersecting the view. Check occlusion separately behind substantial terrain; do not confuse off-screen culling with occlusion. If counters/capture are unavailable, FPS changes are supporting evidence only, not proof of culling.
5. Require both halves visible when expected, no holes/duplicates, stable start/finish lifecycle and lower submitted work when a whole half is outside the frustum. Then test one appended PMC carrying a small recognizable patch under the working owner, with identical geometry removed from the original. Load success plus visibility across lifecycle is the gate for N-clone work.

## Implementation proposal (future work)

- `config/visuals.toml`: `[visuals] chunk_m = 0` preserves the current path; positive values enable spatial chunking, initially 250. Reject nonfinite/negative values. Start with world XY grid cells of chunk_m*100 cm; grid cells handle off-route scenery and folded routes better than route-distance bins. Route bins can follow after spatial diameter checks.
- New `tools/acr-ref/VisualChunks.cs`: deterministic triangle partitioner called after final tangents/UVs and all proxies/fills/vista generation, before `Visuals.Build` calls BuildComponent. Bucket each triangle once by centroid; duplicate/remap referenced vertices into each chunk/material section while preserving every attribute, winding, colour and material. A triangle crossing a border remains whole, and its actual vertices determine bounds. Report oversized triangles/boxes; later clip only if necessary with correct attribute interpolation.
- `Visuals.cs`: preserve legacy behavior at zero; choose existing/clone carriers through a separate allocator, not triangle logic. Emit compact per-chunk material arrays, slot-zero control policy and deterministic component names. Keep distant vista in independently bounded cells rather than letting it inflate route chunks.
- `ProcMesh.cs`: reuse BuildComponent and WriteSection; add independent readback bounds/count validation and an explicit dummy policy. No collision changes.
- `LegacyPackage.cs`: reuse AddExport/SetExportDeps/Save; add only general accessors needed to inspect exact donor entry/template and audit graphs. No new package writer.
- `Visuals.Check`/stage validation and report consumers: remove assumptions of one PMC and fixed section numbering. Aggregate all chunk triangles for ground/invisible-obstacle checks, while retaining source-section identity. Report export, owner, parent, materials, triangles, vertices, bounds and dependency groups for each chunk, plus total conservation and oversized boxes. Support both gameplay directions and relocation.
- Future authorized validation: independent triangle/attribute conservation, valid indices/material slots, tight boxes, component/list/dependency consistency, CUE4Parse readback and Zen conversion verification. Only then an explicitly authorized stage build and game trial. None performed for this design.

## Risks and fallback

New component registration/default-subobject semantics remain the largest uncertainty; neither HISM failure nor SMC crash identifies a definitive cause. Native trigger mutation makes reuse of the end ring conditional. More chunks can increase sections/draw calls, loading overhead and memory; chunking adds no LOD and does not reduce total resident geometry in a single gameplay cell. Occlusion depends on scene occluders and renderer behavior; distance requires a separate verified setting. Measure 200/250/300 m only after the correctness gates pass.

Fallback: chunk_m=0 restores the exact established single-PMC path, with existing geometry reduction options. If two reused PMCs work but new exports do not, retain only that measured two-component split where lifecycle is safe; otherwise keep one PMC and investigate registration with a minimal patch rather than scaling an unproven loader recipe.
