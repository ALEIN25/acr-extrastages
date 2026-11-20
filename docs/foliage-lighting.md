# Object and foliage lighting

The converter's object path does not currently multiply DR2 vertex AO or a separate lightmap into albedo.
`Glb.cs:19` can read COLOR_0, but `VisualObjects.cs:140` only copies positions, UVs and indices into its
prototype mesh. `ProcMesh.cs:24` defaults section colour to white, and its writer uses that constant for
each vertex (`ProcMesh.cs:153`). Instanced StaticMeshes have no colour buffer (`StaticMeshPackage.cs:211`).
There is consequently no vertex-colour/AO multiplier to disable on these paths; interpreting all DR2 colour
channels as AO would also be unsafe (terrain colours encode layer indices; see `stage-ir.md`).

The procedural path rebuilds normals from triangle faces (`VisualObjects.cs:1127-1137`), losing DR2's authored
leaf normals. Instanced trees instead keep glTF NORMAL, with a face-normal fallback (`Instancing.cs:85-114`).
Face normals on separate cards can produce conspicuous dark patches, independently of baked lighting.

`[vegetation] foliage_normals = "spherical"` replaces only real foliage shading normals with directions
away from the template's combined foliage-bounds centre, excluding trunk triangles. The full prototype
centre survives middle-tier thinning, so individual retained cards/materials do not become separate spheres.
Procedural instances transform that centre with the tree; instanced meshes receive local spherical normals
before tangent generation. Positions, winding, UVs, clipping, trunks and collision are unchanged. Degenerate
centre directions keep the previous normal. Far billboard cards and proxy crowns retain their old shading.

The default `"original"` preserves existing procedural/instanced behavior. Missing keys also preserve it;
unknown modes fail with a descriptive error. Keeping this opt-in is deliberate: no stage/game data is
available to establish that one crown-centre approximation improves every species and viewing direction.

`[vegetation] foliage_normal_maps = false` supplies a flat normal instead of the DR2 normal map for masked
real-tree leaf materials. Default `true` preserves existing detail. The experimental `speedtree` path already
uses a flat packed normal. This switch does not affect bark, ordinary objects or far cards.

Texture/material audit:

- `DropIn.cs:598-624` selects a TDiffuse texture, rejecting missing and tiny stand-ins. `Texture` copies its
  compressed mips; it does not combine object diffuse with *_ao, TAmbientOcclusion or a lightmap. Shading
  already painted into diffuse/billboard RGB cannot be separated reliably. Existing `[albedo_compensation]`
  applies an optional per-texture linear-light RGB gain (`DropIn.cs:66-91`), preserving alpha and data textures;
  it compensates exposure rather than reconstructing unlit albedo. Neutral gain is 1.
- `DropIn.cs:200-215` supplies constant ORM, configured as `[255, 217, 0]`: AO 1, roughness 0.85, metallic 0.
  No DR2 AO texture is used. Specular, shading model and any scalar/tint behavior remain inherited from the
  cooked MIC template. The masked path uses a props MIC, not an explicitly selected two-sided-foliage shader;
  alpha masking/two-sided visibility alone does not establish subsurface transmission. No guessed scalar-byte
  patches or cooked shading-model changes were added. Confirm those properties in game/template inspection.
- `DropIn.cs:637-660` offers the existing native Spruce SpeedTree MIC with diffuse also bound as subsurface
  texture. `foliage_material = "speedtree"` remains experimental and is the separate transmission comparison.
- `DropIn.cs:667-716` already mitigates dark billboard fringes with RGB dilation into transparent texels and
  `card_brightness` (defaults 12 px and 1.15). These remain independent controls; alpha is unchanged.
- Ground/road AO baking belongs to dr2-extract (`stage-ir.md` ground lighting notes), not these object textures.

Offline verification:

1. Export two otherwise identical stage previews with `stage-build --visuals-preview <dir>`: baseline
   `foliage_normals = "original"`, then `"spherical"`. Keep material, gain, budgets and camera fixed.
   For this comparison disable `[instancing] stages` (empty array): the current preview exports procedural
   carriers, not HISM instances. Spherical normals are also written to instanced StaticMeshes, which need
   the synthetic mesh check or an in-game comparison.
2. Load each `preview.glb` using `tools/preview`. Compare the same real trees near the road and the thinned
   middle tier from several directions. Expect smoother crown-scale illumination and fewer black card patches,
   with identical silhouettes, card coverage, bark, object boxes and terrain. Some shaded leaves remain dark;
   radial normals cannot remove painted shadows or reproduce transmission.
3. Inspect leaf NORMAL attributes: finite unit vectors pointing away from the instance crown centre; positions,
   indices, UVs and trunk normals should match baseline. `Preview.cs:92` exports final section normals directly
   with the UE-to-glTF axis swap. The preview has always omitted DR2 vertex colours, matching object output.
4. The preview uses base textures, a hemisphere light and directional sun, with generic roughness. It does not
   export normal maps, ORM or UE subsurface/shader parameters. Thus it verifies spherical mesh normals and
   existing RGB gain changes, but cannot judge `foliage_normal_maps` or masked-versus-SpeedTree transmission.
   Compare those switches in game. Do not attribute far-card differences to the spherical switch.

Validation without stage data: the Release build passes. A temporary synthetic harness under ignored
`tools/acr-ref/obj/FoliageCheck` checks procedural and instanced normal changes, unchanged positions/indices/UVs
and bark normals, retained middle-tier centre, unit normals, degenerate-centre fallback and orthogonal tangents.
No full stage build or in-game visual claim is made.
