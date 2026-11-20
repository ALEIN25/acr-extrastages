# Procedural ground cover

Ground cover is opt-in in `config/visuals.toml`:

```toml
[ground_cover]
mode = "scatter"       # default "off"; also "ornaments"
density = 0.4          # candidates per projected square metre, scatter only
max_distance_m = 40    # entire card footprint 2..40 m outside authored road edge
triangle_budget = 80000 # per gameplay cell, including reverse faces
textures = []          # optional exact diffuse DDS stems, without extension
```

Enabled modes require `--visuals-dr2 1 --visuals-textured 1`. Missing usable cutout
textures produce a logged zero-triangle skip, rather than an opaque grass-coloured
rectangle. Density must be 0..100; distance 2..40; budget an integer 0..2000000.

## Sources and findings

`StageOrnaments.cs` already decodes `ornaments.bin`: table offset at 0x50, count at
0x58, 212-byte records; FNV-1a template hash at record +0, 12-float transform at
+16, translation in the last three floats. Templates resolve against the NODE
library roots in location `objects.pssg`. Extraction defaults to `--ornament-filter
^rock`, excludes templates already in objects.ens, and crops to its own route
corridor. Thus `0 instances kept (1842 records in 2 files)` is not evidence of an
undecoded table or of absent vegetation. The zero-result log now lists unresolved
hashes, existing ENS templates, and filter exclusions with template names.

The vegetation source inventory in dr2-formats.md lists `ground_cover.pssg` and
`grass.grs`. The latter's density/placement format remains undecoded. With `--render`,
stage-ir now exports location `ground_cover.pssg`, when present, to
`render/ground_cover.glb` and `ground_cover.materials.json`. These are prototype
assets, not world terrain. `stage-textures` already scans every materials sidecar,
so their texture references enter normal extraction, including textures embedded
in that PSSG. No speculative terrain vertex-layer indices are treated as densities.

Read-only inspection of the existing main-checkout Finland route-2 IR found:

- Terrain shaders: `terrain_road.fx`, `terrain_rockbank.fx`,
  `terrain_wsm_blend_nm.fx`, `terrain_wsm_edge_nm.fx`. No explicit grass/scatter
  density bindings in the exported terrain materials.
- `textures/fin_ral_grass_d.dds` exists, but a name alone does not establish that it
  is a plant cutout; opaque maps are rejected by this implementation.
- Existing cutout billboard sources include `fin_bush_fl01_lod_d`,
  `fin_bush_fl02_lod_d`, `fin_bush_01_a_lod_d` and many full-size tree LOD strips.
  Tree/bush LOD strips are not automatically selected as small ground plants.
- No named fern, flower, weed, or plant DDS assets, no ground-cover material
  sidecar, and no ornaments sidecar in that IR. The cached Finland location also
  has no extracted `ground_cover.pssg` or `grass.grs`. An archive inventory/re-extract
  on the game-data machine is needed to establish the actual small-plant names.

## Geometry and limits

Scatter samples barycentrically on collision terrain triangles whose decoded DR2
surface is grass (`GR*`, `GS*`, `GGL*`, `GWT*`), rejecting slopes steeper than about
49 degrees. This avoids rock, water, road, and snow. Sampling uses projected area,
a fixed seed, and a cumulative area distribution over the whole eligible stage;
a small budget therefore does not stop at the first terrain tile. The attempt
count is bounded by budget / 8; rejected candidates are not replaced, so the
result can be below the requested density/budget, especially on large triangles
crossing the band boundary. Terrain triangle count bounds preprocessing memory.

Each plant is two vertical crossed cards, 0.5..0.9 m wide and 0.25..0.65 m high,
with a random yaw and its base sunk 2 cm into the sampled terrain. Each face has
its own vertices and sky-tilted normals, matching `VisualObjects.AddCard`'s
`CardUp` convention: eight triangles per plant. The leaf MIC is used directly,
independent of the tree-material setting. Sections have no collision.

`RoadCorridor.Query` checks against every gate segment, requiring centre clearance
minus card radius >= 2 m and clearance plus radius <= max_distance_m. Bounds are
checked before emitting. Sections are named `ground_cover:<texture>` and added
before carrier splitting and preview export. `Visuals.Check` treats them as soft
vegetation, with zero corridor penetration allowed at any height, and checks the
reported triangle count against the budget. The global visuals budget still applies.

`ornaments` mode uses world-space anchors from plant-named `stage.json.ornaments`
instances, with the same cards, safety band and budget. It does not reconstruct
original plant meshes, scale, yaw or atlas UVs. Re-extract with a plant-inclusive
`--ornament-filter` first; the default rock-only IR supplies no plant anchors.
Plant diffuse maps are discovered in all material sidecars or named `*_d.dds`
files (`grass`, `fern`, `flower`, `weed`, `plant`, `groundcover`); ground-cover
sidecars also admit diffuse maps with other names. `textures` overrides discovery.
Every selected map must have >5% transparent pixels. Mixed atlases or billboard
strips need an appropriate single-plant texture; atlas frame decoding is not implemented.

## Verification

No full stage builds were run for implementation validation.

```powershell
dotnet build tools/acr-ref/AcrRef.csproj -c Release --no-restore
dotnet build tools/dr2-extract/Dr2Extract.csproj -c Release -p:NuGetAudit=false
dotnet tools/acr-ref/bin/Release/net10.0/acr-ref.dll ground-cover-selftest
```

The self-test uses synthetic sloping grass terrain crossing a road. It checks
clearance across every emitted vertex, terrain-following bases, triangle cap,
determinism, sky normals, no collision, off/zero density/sub-plant budget, physics
bounds rejection, and actual Preview GLB triangle/alpha export. Generated fixture
files live under gitignored `work/ground-cover-selftest`.

On the game-data machine:

1. Inventory the location archive for `ground_cover.pssg`, `grass.grs`, grass,
   fern and detail assets. Run `stage-ir <stage> --render`, inspect the new
   ornament exclusion diagnostics, and run `stage-textures <stage>`. Inspect
   `ground_cover.materials.json` and its diffuse DDS alpha before selecting a map.
2. Enable scatter with a small budget; run the normal stage-build with DR2
   textures and `--visuals-preview work/acr/preview/<name>`. For ornaments mode,
   use a plant-inclusive extraction filter and verify nonzero plant instances.
3. Require the ground-cover budget and soft-vegetation road-corridor checks to
   pass. Inspect `ground_cover` in the visuals report. Expected build lines:
   `ground cover: off, 0 triangles`, or
   `ground cover: scatter, N plants, T triangles (budget B), K textures, R rejected`.
   Missing assets log `ground cover: scatter, 0 triangles (no usable plant diffuse textures)`.
4. Open the exported preview in `tools/preview` using its existing `serve.mjs`
   workflow. Inspect near-road shoulders, hairpins, steep banks, multiple route
   positions, cutout silhouettes and lighting from both sides. Confirm
   `ground_cover:*` meshes exist in preview.glb, with MASK/doubleSided materials.
5. Compare off and scatter, then raise density/budget within the total visuals
   budget. In-game material appearance and stage-specific asset selection remain
   to be verified with the actual game assets.
