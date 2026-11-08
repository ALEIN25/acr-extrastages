# Spectator crowds

Opt in with `[crowds] mode = "dr2_spots"` in `config/visuals.toml`.
The default remains `off`. `max_spawners = 40` caps groups **per gameplay cell**;
zero emits none. Each group currently preserves all four native baked spectators.
No density reduction or ambient sound actor is implemented.

## Native template and serialization

The inspected Hafren cell `0SEKWS6594N5CV2V6D2XLJYYI` contains one
`BC_CrowdSystemSpawnerBakedActor_C`. Its four owned default subobjects are
`Root` (SceneComponent), `Mass Spawner Component`, `Crowd Animation`, and
`Crowd Runtime`. The actor's BakedSpawnData contains four Elements with transforms
relative to Root, representation/customization names and animation buckets.
Crowd Runtime.BoundsRadius is retained verbatim (245.2922 cm), because the baked
elements and their relative transforms are unchanged.

`CrowdEmission.cs` clones those five export blobs. A restricted usmap-driven
walker identifies FNames and object references at actual property boundaries;
it never searches arbitrary integer patterns. Unknown serializers fail closed.
The actor uses the native CrowdSystemSpawnerBakedActor schema: the inspected
blueprint adds no serialized properties. Imports are copied recursively with
deduplication, including the BP class/CDO and default component templates.
Export class/super/template/outer indices and all four preload dependency kinds
are remapped. Actor names have unique UAID-style suffixes and fresh actor GUIDs;
component names retain their native names under distinct actor outers.
The Level native actor array and create-before-serialization dependencies grow.

Root gets the spot position in UE centimetres and yaw toward the nearest XY
centreline point, with zero pitch/roll. Group elements remain relative.
The unchanged blueprint pair is copied to output so CUE4Parse can decode the
standalone cells without a game mount. Launcher stage-template extraction includes
both the template cell and blueprint.

## Planning and limitations

`Crowds.cs` accepts `core_crowd_*` and spectator-named template origins from
stage.json objects and ornaments. Positions need at least 400 cm road-edge
clearance, must fit physics bounds including height, and are deduplicated at
1 cm resolution. Nonfinite or malformed positions fail. Source height is retained;
there is no invented terrain snap. The reverse gameplay cell uses the reverse
IR's spots; only render visuals retain the existing shared-forward-IR policy.

Clearance applies to the group origin, not every spectator footprint. Native
relative Z offsets are retained. Terrain contact, nearby props and group
footprints need in-game inspection. This implementation does not decode DR2
crowd_*.bin; stages without crowd-like object/ornament records emit no groups.
Trigger volumes are not spectator placement evidence.

Preview markers `crowd:spot:*` remain candidate locations (including spots beyond
the emission cap). The build report separately records `emitted[]` actor names,
positions/yaw, `actors_emitted` and `people_emitted`.

## Verification

Build and synthetic placement test:

```powershell
dotnet build tools/acr-ref/AcrRef.csproj -c Release --no-restore
dotnet tools/acr-ref/bin/Release/net10.0/acr-ref.dll crowds-selftest
```

Native legacy clone test, requiring extracted assets and the matching usmap:

```powershell
dotnet tools/acr-ref/bin/Release/net10.0/acr-ref.dll crowds-native-selftest --legacy <legacy-root> --usmap <usmap> --out <scratch-root>
```

By default this clones two groups into a copy of 0SEK. Pass
`--cell acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP`
or the corresponding `7FF9A0XR2HIRJVFZU6AEAFHMU` path to test a gameplay cell.
Output must differ from the input legacy root. No game directory is needed.

Stage-build checks and stage-check re-read written cells through CUE4Parse with
the blueprint and usmap. They assert the emitted actor count, Level actor-list
membership, four owned components and the actor's references to them, baked
people count, root position within 1 cm on each axis, and yaw within 0.01 degree.
Successful checks print:

`crowds: emitted N spawners (M people) in <cell>`

Validated: Release build, synthetic planning test, and native cloning/re-reading
in copies of 0SEK, forward 22VG and reverse 7FF9 (two spawners, eight people each).
No full stage builds, retoc round trips or in-game spawning/rendering were run.
