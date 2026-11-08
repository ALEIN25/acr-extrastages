using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Offline 3D preview of the visuals mesh (stage-build --visuals-preview <dir>; launcher --preview; viewer: tools/preview):
/// <dir>/preview.glb with one mesh per section (positions in metres relative to the start crossing, glTF Y-up: UE
/// (X, Y, Z) -> (X, Z, Y), which also turns UE's clockwise front faces into glTF's counter-clockwise ones), base colour
/// textures as PNGs under <dir>/tex (DR2 materials: the texture written for the game, first mip at most 1024 px; game
/// materials: a flat stand-in colour by name), and <dir>/preview.json with camera spots along the route.
/// UV0 is rounded to 16-bit floats, which is how the game appears to draw procedural-mesh UVs (inferred from the in-game
/// streaks before LocalUv), so precision problems show in the preview too.
internal static class Preview
{
    public static JsonObject Write(string dir, IReadOnlyList<ProcMesh.Section> sections, IReadOnlyList<int> mats, JsonArray names,
                                   Func<int, string> importPath, Dr2Materials? dr2, StageBuild.Ir ir, StageBuild.Placement pl)
    {
        Directory.CreateDirectory(Path.Combine(dir, "tex"));
        foreach (var old in Directory.GetFiles(Path.Combine(dir, "tex"), "*.png")) File.Delete(old);
        var startGate = ir.Gates[ir.Timing.First(t => t.type == "start").gate];
        var o = pl.ToUe(startGate.crossing);
        Vector3 T(Vector3 ue) => new((float)((ue.X - o.X) / 100), (float)((ue.Z - o.Z) / 100), (float)((ue.Y - o.Y) / 100));
        Vector3 Td(Splines.V3 ue) => T(new Vector3((float)ue.X, (float)ue.Y, (float)ue.Z));

        var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        var bufferViews = new JsonArray(); var accessors = new JsonArray(); var meshes = new JsonArray(); var nodes = new JsonArray();
        var materials = new JsonArray(); var images = new JsonArray(); var textures = new JsonArray();
        var matIndex = new Dictionary<int, int>();
        int View(byte[] data, int? target)
        {
            while (bin.Length % 4 != 0) bw.Write((byte)0);
            var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length };
            if (target is { } tg) v["target"] = tg;
            bw.Write(data);
            bufferViews.Add(v);
            return bufferViews.Count - 1;
        }
        int Accessor(int view, int componentType, int count, string type, JsonArray? min = null, JsonArray? max = null)
        {
            var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
            if (min is not null) { a["min"] = min; a["max"] = max; }
            accessors.Add(a);
            return accessors.Count - 1;
        }
        int Material(int mat)
        {
            if (matIndex.TryGetValue(mat, out var known)) return known;
            var path = mat == 0 ? "" : importPath(mat);
            var m = new JsonObject { ["name"] = path.Length == 0 ? "engine default" : path, ["doubleSided"] = true };
            var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 0.9 };
            if (dr2 is not null && dr2.MicBase.TryGetValue(path, out var tex) && dr2.TexMip0.TryGetValue(tex, out var mip)
                && mip.fmt is not (Dds.Fmt.BC5 or Dds.Fmt.BC4))
            {
                var px = mip.fmt == Dds.Fmt.BC7 ? DecodeBc7(mip.mip, mip.w, mip.h) : Dds.DecodeBlocks(mip.fmt, mip.mip, mip.w, mip.h);
                bool masked = dr2.MaskedMics.Contains(path);
                if (masked) { m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.5; }
                else for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                var file = $"tex/{tex[(tex.LastIndexOf('/') + 1)..]}.png";
                if (!File.Exists(Path.Combine(dir, file))) Dds.WritePng(Path.Combine(dir, file), mip.w, mip.h, px);
                images.Add(new JsonObject { ["uri"] = file });
                textures.Add(new JsonObject { ["source"] = images.Count - 1 });
                pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
            }
            else pbr["baseColorFactor"] = StandIn(path);
            m["pbrMetallicRoughness"] = pbr;
            materials.Add(m);
            return matIndex[mat] = materials.Count - 1;
        }

        long tris = 0;
        for (int s = 0; s < sections.Count; s++)
        {
            var sec = sections[s];
            var name = s < names.Count ? names[s]!.GetValue<string>() : $"section{s}";
            if (sec.Idx.Count == 0 || name == "dummy" || name.StartsWith("instancing:keep:")) continue;
            int n = sec.Pos.Count;
            var pos = new byte[n * 12]; var nrm = new byte[n * 12]; var uv = new byte[n * 8];
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                var p = T(sec.Pos[i]); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                var q = sec.Nrm[i]; var nn = new Vector3(q.X, q.Z, q.Y);
                var u = i < sec.Uv.Count ? sec.Uv[i] : Vector2.Zero;
                foreach (var (arr, at, vals) in new[] { (pos, i * 12, new[] { p.X, p.Y, p.Z }), (nrm, i * 12, new[] { nn.X, nn.Y, nn.Z }),
                                                        (uv, i * 8, new[] { (float)(Half)u.X, (float)(Half)u.Y }) })
                    for (int k = 0; k < vals.Length; k++) BitConverter.TryWriteBytes(arr.AsSpan(at + k * 4), vals[k]);
            }
            var idx = new byte[sec.Idx.Count * 4];
            for (int i = 0; i < sec.Idx.Count; i++) BitConverter.TryWriteBytes(idx.AsSpan(i * 4), sec.Idx[i]);
            tris += sec.Idx.Count / 3;
            int ap = Accessor(View(pos, 34962), 5126, n, "VEC3", new JsonArray(mn.X, mn.Y, mn.Z), new JsonArray(mx.X, mx.Y, mx.Z));
            int an = Accessor(View(nrm, 34962), 5126, n, "VEC3");
            int au = Accessor(View(uv, 34962), 5126, n, "VEC2");
            int ai = Accessor(View(idx, 34963), 5125, sec.Idx.Count, "SCALAR");
            meshes.Add(new JsonObject
            {
                ["name"] = name,
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = ap, ["NORMAL"] = an, ["TEXCOORD_0"] = au },
                    ["indices"] = ai, ["material"] = Material(mats[s]),
                }),
            });
            nodes.Add(new JsonObject { ["name"] = name, ["mesh"] = meshes.Count - 1 });
        }
        while (bin.Length % 4 != 0) bw.Write((byte)0);
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "acr-ref stage-build --visuals-preview" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(Enumerable.Range(0, nodes.Count).Select(i => (JsonNode)i).ToArray()) }),
            ["nodes"] = nodes, ["meshes"] = meshes, ["materials"] = materials, ["accessors"] = accessors, ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
        };
        if (images.Count > 0) { gltf["images"] = images; gltf["textures"] = textures; }
        WriteGlb(Path.Combine(dir, "preview.glb"), gltf.ToJsonString(), bin.ToArray());

        // camera spots: behind the car at the start and at quarter points of the route, looking down the road; a top view
        var cams = new JsonArray();
        void Cam(string label, int gate)
        {
            gate = Math.Clamp(gate, 0, ir.Gates.Count - 1);
            var a = Td(pl.ToUe(ir.Gates[gate].crossing));
            var b = Td(pl.ToUe(ir.Gates[Math.Min(ir.Gates.Count - 1, gate + 4)].crossing));
            var dir2 = Vector3.Normalize(new Vector3(b.X - a.X, 0, b.Z - a.Z));
            var eye = a - dir2 * 7 + new Vector3(0, 2.6f, 0);
            var look = a + dir2 * 25 + new Vector3(0, 1, 0);
            cams.Add(new JsonObject
            {
                ["name"] = label, ["distance_m"] = Math.Round(ir.Gates[gate].distance),
                ["eye"] = new JsonArray(eye.X, eye.Y, eye.Z), ["target"] = new JsonArray(look.X, look.Y, look.Z),
            });
        }
        int startIdx = ir.Timing.First(t => t.type == "start").gate, finishIdx = ir.Timing.First(t => t.type == "finish").gate;
        Cam("start", startIdx);
        foreach (var f in new[] { 0.25, 0.5, 0.75 }) Cam($"{f:P0} of the stage", (int)(startIdx + (finishIdx - startIdx) * f));
        Cam("before the finish", finishIdx - 6);
        var route = new JsonArray((ir.PreviewRoute ?? ir.Centre.Select(pl.ToUe).ToList()).Select(g => { var p = Td(g); return (JsonNode)new JsonArray(Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1)); }).ToArray());
        var markers = new JsonArray();
        foreach (var item in ir.PreviewMarkers) { var q = Td(item.Value); markers.Add(new JsonObject { ["name"] = item.Key, ["pos"] = new JsonArray(q.X,q.Y,q.Z) }); }
        if (ir.PreviewMarkers.TryGetValue("finish", out var finish)) {
            var q = Td(finish); var path = ir.PreviewRoute!;
            int i = Enumerable.Range(0,path.Count).MinBy(i => (path[i]-finish).Length);
            var d = Vector3.Normalize(q-Td(path[Math.Max(0,i-1)]));
            foreach (var item in new[] { ("finish",q-d*25+new Vector3(0,3,0)), ("finish top",q+new Vector3(0,100,0.1f)) })
                cams.Add(new JsonObject { ["name"]=item.Item1,["eye"]=new JsonArray(item.Item2.X,item.Item2.Y,item.Item2.Z),["target"]=new JsonArray(q.X,q.Y,q.Z) });
        }
        var meta = new JsonObject
        {
            ["stage"] = ir.Dir, ["glb"] = "preview.glb", ["triangles"] = tris, ["sections"] = nodes.Count,
            ["uv0"] = "rounded to 16-bit floats like the game", ["cameras"] = cams, ["route"] = route, ["markers"] = markers,
        };
        File.WriteAllText(Path.Combine(dir, "preview.json"), meta.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"preview: {dir}\\preview.glb, {nodes.Count} sections, {tris} triangles, {images.Count} textures, {new FileInfo(Path.Combine(dir, "preview.glb")).Length / 1e6:0} MB");
        return new JsonObject { ["dir"] = dir, ["sections"] = nodes.Count, ["triangles"] = tris, ["textures"] = images.Count };
    }

    /// Flat stand-in colours for game materials the preview cannot sample (linear RGBA).
    static JsonArray StandIn(string path)
    {
        float[] c = path switch
        {
            "" => [0.55f, 0.55f, 0.55f],
            _ when path.Contains("GravelRoad") => [0.32f, 0.28f, 0.24f],
            _ when path.Contains("Terrain") => [0.22f, 0.3f, 0.12f],
            _ when path.Contains("SurfaceGrass") => [0.05f, 0.35f, 0.02f],
            _ when path.Contains("SurfaceGravel") => [0.4f, 0.6f, 0.9f],
            _ when path.Contains("MetalPole") => [0.9f, 0.05f, 0.8f],
            _ when path.Contains("HayBale") => [0.9f, 0.35f, 0.05f],
            _ when path.Contains("WallBasic") => [0.02f, 0.02f, 0.02f],
            _ when path.Contains("WallBush") => [0.03f, 0.2f, 0.03f],
            _ when path.Contains("WallCloth") => [0.3f, 0.05f, 0.6f],
            _ when path.Contains("WallWood") => [0.8f, 0.7f, 0.1f],
            _ when path.Contains("WallConcrete") => [0.1f, 0.2f, 0.8f],
            _ when path.Contains("WallRock") => [0.1f, 0.8f, 0.8f],
            _ when path.Contains("WallPlastic") => [0.2f, 0.9f, 0.2f],
            _ => [0.5f, 0.5f, 0.5f],
        };
        return new JsonArray(c[0], c[1], c[2], 1f);
    }

    /// RGBA8 of a BC7 mip (Dds has no BC7 decoder; BCnEncoder.Net does).
    internal static byte[] DecodeBc7(byte[] blocks, int w, int h)
    {
        var px = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(blocks, Math.Max(4, w), Math.Max(4, h), BCnEncoder.Shared.CompressionFormat.Bc7);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = px[y * Math.Max(4, w) + x];
                int i = (y * w + x) * 4;
                (o[i], o[i + 1], o[i + 2], o[i + 3]) = (c.r, c.g, c.b, c.a);
            }
        return o;
    }

    static void WriteGlb(string path, string json, byte[] bin)
    {
        var j = Encoding.UTF8.GetBytes(json);
        int jl = (j.Length + 3) & ~3;
        using var f = new BinaryWriter(File.Create(path));
        f.Write(0x46546C67u); f.Write(2u); f.Write((uint)(12 + 8 + jl + 8 + bin.Length));
        f.Write((uint)jl); f.Write(0x4E4F534Au); f.Write(j); for (int i = j.Length; i < jl; i++) f.Write((byte)' ');
        f.Write((uint)bin.Length); f.Write(0x004E4942u); f.Write(bin);
    }
}
