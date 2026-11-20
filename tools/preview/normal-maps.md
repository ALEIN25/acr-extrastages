Procedural mesh normal-map verification
=======================================

In `config/visuals.toml`, `[dr2_textures] proc_tangents = "uv"` (also the
fallback when omitted) computes missing tangents from UV0. `"normal"` retains
the old normal-only tangent. Supplied tangent arrays are preserved in either
mode. `object_normal_maps = false` remains the default, including when omitted.

Build a preview with the usual stage-build arguments plus
`--visuals-preview <directory> --visuals-preview-normal-maps 1`.
The second flag is optional and defaults to 0. It exports glTF VEC4 TANGENT
attributes and paired DR2 normalTexture PNGs even with object_normal_maps=false.
It reuses the game's BC5 conversion (including DXT5 X-in-alpha detection),
reconstructs positive Z and converts DirectX green to OpenGL green. It can
therefore populate the normal texture conversion cache/packages during export.

Open that stage in the usual tools/preview viewer. The new Normal maps checkbox
starts unchecked. At close range, compare logs, trunks, rocks and rock banks
with the checkbox off/on. Detail should follow the texture, raised/recessed
features should keep consistent lighting across rotated and mirrored islands,
and surfaces should not become broadly black. Inspect both sunlit and shaded
sides and UV seams. Compare a separate proc_tangents="normal" export as the
legacy control. Missing paired maps retain flat shading. This is a basis/map
diagnostic; three.js lighting and materials do not reproduce the cooked UE
shader. Only enable object_normal_maps for shipping after an in-game check.

Implementation and handedness
----------------------------

Each triangle contributes dP/dU and dP/dV to its indexed vertices. The final U
vector is Gram-Schmidt orthogonalized against the normal and normalized.
Degenerate/nonfinite UV triangles and degenerate geometry contribute nothing;
cancelled U uses V when possible, then a stable normal-derived perpendicular.
There are two Vector3 accumulator arrays (24 bytes per vertex), followed by
stored tangents and bools. Time is O(vertices + indices). No welding occurs;
UV/normal seams and mirrored islands must have separate vertex indices. A
vertex shared across incompatible mirrored bases cannot represent both bases.

FProcMeshTangent supports a real per-vertex sign. Slot 0 is TangentX and slot 1
is bFlipTangentY. A false bool is zero-masked with no payload. A true bool has
a nonzero slot and one byte 0x01 after TangentX's three doubles. It is not the
tagged-property bool convention where the value lives in the property tag.
The reader now consumes this byte, and the writer emits it. See the
[unversioned bool reader/writer in UAssetAPI](https://github.com/atenfyr/UAssetAPI/blob/master/UAssetAPI/PropertyTypes/Objects/BoolPropertyData.cs)
and [Epic's FProcMeshTangent API](https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Plugins/ProceduralMeshComponent/FProcMeshTangent?application_version=5.6).

For generated tangents, T follows +U. UE B = cross(N,T) times the flip sign;
choose that sign so B follows -V for DirectX positive green. Mirroring changes
the sign without reversing T. Existing authored tangents retain their old
sign (false if absent). UE-to-glTF swaps Y/Z, reflecting the basis; converting
green to OpenGL negates B again. These two sign changes cancel, so glTF
tangent.w is -1 for bFlipTangentY=true and +1 otherwise.

Validation without game data:

```
dotnet build tools/acr-ref/AcrRef.csproj -c Release --no-restore
dotnet tools/acr-ref/bin/Release/net10.0/acr-ref.dll procmesh-selftest
```

The synthetic check covers ordinary/rotated/mirrored UVs, degenerate UVs and
isolated vertices, Gram-Schmidt, authored tangents, legacy mode, mixed true/false
serialization and following-section alignment, byte-exact round trips, BC5 Z
reconstruction, and UE/glTF basis parity. Full stage and UE validation require
the verifier's game data.
