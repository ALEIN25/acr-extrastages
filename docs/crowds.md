# Spectator crowds: investigation and spot planning

Current implementation is **spot planning only**, opt-in via `[crowds] mode = "dr2_spots"`
in `config/visuals.toml`; the default is `"off"`. No crowd actors are emitted yet.
No game data or `work/` directory was available in this checkout. No full stage builds or
native package inspections were performed. Statements below distinguish repo evidence
from work that still needs extracted assets.

## Native ACR evidence and remaining inspection

- `decisions.md:489-494` names `BC_CrowdSystemSpawnerBakedActor`, `BC_Marshal`,
  `Characters/Crowd*`, and `/Marshal`. These are prior investigation notes, not verified
  export paths in this checkout. The exact blueprint package, generated class suffix,
  property names, density and animation references remain unknown.
- `acr-stage-anatomy.md:51-52` places crowd managers and crowd/mechanic splines in the
  persistent level `/Game/Levels/WalesS4HafrenSouth`. Its gameplay cells are forward
  `22VG0QIP9ATUQI633K86KWPFP` and reverse `7FF9A0XR2HIRJVFZU6AEAFHMU` (line 58).
- `decisions.md:376` records 15 shared BE01+B42F cells containing crowds. Lines 397
  and following describe removal of host crowds when unloading Hafren scenery.
  Thus keeping a host crowd elsewhere in the level does not place it along the DR2 route.
- `instancing-plan.md:74-79` identifies added exports and component wiring as needed
  for crowds too. `Instancing.AddComponents` in `tools/acr-ref/Instancing.cs` (line 417)
  appends components, updates the owning actor's `InstanceComponents` and preload
  dependencies (line 467), and sets the attachment root. These HISM-specific slots
  must not be reused as crowd schema slots without inspecting the actual types.
- `DropIn.MicFrom` in `tools/acr-ref/DropIn.cs:254` demonstrates cloning a template
  package and rewriting references. An actor clone additionally needs all owned exports,
  the level actor list, outer/class/template indices, dependencies, attachment references,
  and appropriate per-direction data layers. Appending a class import alone is insufficient.

With the game available, use the repo's `acr-ref` inspector and `tools/retoc/retoc.exe`
(UE5_6; launcher extraction flags are in `tools/launcher/Pipeline.cs:365-368`):

```powershell
# Set ACR_GAME_DIR and ACR_USMAP to the local game and matching mappings first.
acr-ref list --filter 'Crowd|Spectator|Marshal' --out work/acr/crowd-assets.txt
acr-ref export /Game/Levels/WalesS4HafrenSouth --out work/acr/crowd-persistent.json
acr-ref export-many --filter 'WalesS4HafrenSouth' --out work/acr/crowd-cells
acr-ref schema BC_CrowdSystemSpawnerBakedActor BC_CrowdSystemSpawnerBakedActor_C
```

Check the asset listing for actual class paths, then export those blueprint/CDO packages
and the particular shared crowd cells. The stage-name filter is a discovery filter;
confirm it matches the cell paths reported by `acr-ref list` before relying on the dump.
Collect actor properties and every owned component: root, spline/area or baked position
arrays, local/world transform frame, spawn count/density, animation/data assets, seed,
crowd manager references and data layers. Exact names for these fields are **not known**.

Extract the persistent level, the selected crowd cell, the spawner blueprint and its
referenced data/animation/character packages, using the exact package substrings found
above as repeated `--filter` arguments:

```powershell
tools/retoc/retoc.exe to-legacy --version UE5_6 --no-shaders --filter '<actual crowd cell/package substring>' '<ACR>/acr/Content/Paks' work/acr/crowd-legacy
```

Do not treat `Characters/Crowd*` as a confirmed full asset path or hard-code an animation
name from inference. Native dependencies can stay as imports into the installed game;
the legacy template assets required for cloning must be added to the launcher's extraction
needs once their paths are established. After cloning, verify CUE4Parse references and
a retoc `to-zen` / `to-legacy` round trip, then test loading and spawning in both directions.

## DR2 evidence

`tools/dr2-extract/StageOrnaments.cs:49-103` reads only `ornaments.bin`, using a table
offset at 0x50, count at 0x58, 212-byte records, template hash at +0, and a 3x4 matrix
at +16 (translation is floats 9..11). It resolves hashes through location `objects.pssg`,
excludes objects.ens templates and applies corridor/name filters. Defaults in
`StageIr.cs:59-61` are 60 m and `^rock`. A zero-kept log does **not** establish that DR2
has no spectators. The new zero-kept diagnostic prints unresolved hashes, objects.ens
exclusions and up to 40 templates excluded by the name filter.

`StageIr.cs:477` exports the resulting ornaments block; `objects.instances` is sourced
from objects.ens. Crowd spot planning reads `core_crowd_*` and templates containing
`spectator` from these two IR blocks, using the existing `ObjectColliders.Trs` transform
decoder. These origins are candidate spots; template geometry may represent groups or
crowd-related props. They are not assumed to be individual people.

`decisions.md:490,494` says `crowd_standing2.bin` holds spectators, marshals and photographers,
mentions 31 kinds in `crowds_references.xml`, and says the format was decoded but its
heading convention was unsettled. **The current extractor has no crowd_*.bin reader**;
that note does not supply a record layout. `stage-ir.md:285-286` still describes these
files as undecoded. A separate position-only crowd extraction pass is needed after
the binary layout and reference mapping are recovered and verified. Do not apply the
ornament 212-byte stride to crowd files on that evidence alone.

IR triggers are box volumes from `triggers_game_object.xml` (`StageIr.cs:178-179,431`;
`stage-ir.md` triggers section). Their centres are not evidence of spectator placements,
so this implementation does not spawn or mark crowds at trigger centres.

## Implemented placement and verification

`tools/acr-ref/Crowds.cs` plans unchanged DR2 template origins. It rejects positions with
less than 400 cm clearance using `RoadCorridor.Query` (`Respawn.cs:9-54`), checks the
physics bounds including height, rejects non-finite/missing positions and deduplicates
accepted positions at 1 cm resolution. It skips unsafe spots rather than moving them
onto unknown terrain. The clearance is to the gate-derived corridor, not a measured
edge of every rendered road triangle; footprint clearance for a future group spawner
still needs validation. Source height is retained; there is no invented ground snap.

`StageBuild.BuildCell` calls the planner before `Visuals.Build`. Both gameplay cells get
reports using the shared visuals IR (matching the existing reverse-visuals policy).
`Preview.Write` exports the accepted markers through its existing frame conversion:
metres relative to start crossing, Y up. Each crowd marker has `kind = "crowd_spot"`
and `runtime_actor = false`.

Verifier steps:

1. Run `dotnet build tools/acr-ref/AcrRef.csproj -c Release --no-restore`.
   On a fresh checkout restore dependencies first. Then run
   `dotnet tools/acr-ref/bin/Release/net10.0/acr-ref.dll crowds-selftest`.
   Require `PASS crowds: asymmetric road edges, 4 m boundary, matrix/TRS positions, bounds, deduplication and preview markers`.
2. With game data, set `[crowds] mode = "dr2_spots"` and run the usual stage-build
   command with `--visuals config/visuals.toml --visuals-preview <preview-dir>`.
   Require `crowds: dr2_spots: ... candidates, ... spots, ... rejected within 4 m of road edge, ... out of bounds, ... duplicates; actors emitted 0 (native template unavailable)`.
   If it also prints `crowds: no spectator templates in IR objects/ornaments; crowd_*.bin is not exported by stage-ir`,
   expect no crowd markers: source extraction remains necessary.
3. Check build report `cell.crowds` and `reverse_cell.crowds`: `status` must be
   `spots_only_native_template_required`, `actors_emitted` must be zero, and every
   `spots[].road_clearance_m` must be >= 4. Match each `marker` and absolute `ue_cm`
   to the preview coordinate conversion.
4. In `tools/preview`, load that preview and enable **route / markers**. Inspect
   `crowd:spot:00000`, etc., behind the verge at the original DR2 heights. These are
   candidate markers, not rendered people or proof of runtime actor registration.
5. Rebuild with mode `off`: no `crowd:spot:*` markers and no emitted actors.
   Both config modes leave gameplay-cell exports untouched by the crowd planner.

Validated here: Release build and synthetic spot test. Full stage builds, the changed
extractor diagnostic on real records, preview placement on real IR, crowd schema,
actor serialization, density/animation choices and in-game rendering remain unverified.
