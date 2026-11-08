using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace AcrRef;

/// Synthetic geometry/alpha fixture; no game files or cooked packages required.
internal static class GroundCoverSelfTest
{
    public static int Run()
    {
        string dir = Path.Combine("work", "ground-cover-selftest");
        Directory.CreateDirectory(Path.Combine(dir, "render")); Directory.CreateDirectory(Path.Combine(dir, "textures"));
        var ir = new StageBuild.Ir { Dir = dir };
        ir.Gates.Add((0, [0, 0, 0], [-3, 0, 0], [3, 0, 0]));
        ir.Gates.Add((100, [0, 0, 100], [-3, 0, 100], [3, 0, 100]));
        ir.Centre.AddRange(ir.Gates.Select(g => g.crossing));
        ir.Timing.Add(("start", 0, 0));
        ir.Timing.Add(("finish", 1, 100));
        var pl = new StageBuild.Placement(0, 0, 0, 0, 0, 0, 0);
        var road = new RoadCorridor(ir.Gates.Select(g => pl.ToUe(g.crossing)).ToList(), ir.Gates.Select(g => pl.ToUe(g.left)).ToList(), ir.Gates.Select(g => pl.ToUe(g.right)).ToList());
        // Broad tilted grass strip spans the road; placement must reject its middle and far side.
        var positions = new float[] { -50, -5, 0, 50, 5, 0, 50, 5, 100, -50, -5, 100 };
        var bytes = positions.SelectMany(BitConverter.GetBytes).Concat(new uint[] { 0, 1, 2, 0, 2, 3 }.SelectMany(BitConverter.GetBytes)).ToArray();
        var json = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":72}],
            "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":24}],
            "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3"},{"bufferView":1,"componentType":5125,"count":6,"type":"SCALAR"}],
            "materials":[{"name":"GRS+"}],"meshes":[{"name":"grass","primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}]}
            """;
        var j = Encoding.UTF8.GetBytes(json); int jl = (j.Length + 3) & ~3;
        using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "collision.glb"))))
        {
            w.Write(0x46546C67); w.Write(2); w.Write(28 + jl + bytes.Length);
            w.Write(jl); w.Write(0x4E4F534A); w.Write(j); for (int i = j.Length; i < jl; i++) w.Write((byte)' ');
            w.Write(bytes.Length); w.Write(0x004E4942); w.Write(bytes);
        }
        byte[] dds = new byte[144];
        void Int(int at, int value) => BitConverter.GetBytes(value).CopyTo(dds, at);
        Int(0, 0x20534444); Int(4, 124); Int(12, 4); Int(16, 4); Int(28, 1); Int(76, 32); Int(80, 4);
        Encoding.ASCII.GetBytes("DXT5").CopyTo(dds, 84);
        // BC3: transparent alpha index 0 everywhere. Colour is irrelevant to this geometry test.
        File.WriteAllBytes(Path.Combine(dir, "textures", "test_grass_d.dds"), dds);
        File.WriteAllText(Path.Combine(dir, "render", "ground_cover.materials.json"), """
            [{"name":"finland_gc_billboard","textures":[{"parameter":"TDiffuseAlphaMap","texture":"#test_grass_d.tga"}]},
             {"name":"clutter_01_mesh","textures":[{"parameter":"TDiffuseAlphaMap","texture":"#test_clutter_d.tga"}]}]
            """);
        File.WriteAllBytes(Path.Combine(dir, "textures", "test_clutter_d.dds"), dds);
        // Offset palette, nested node transforms, nonuniform scale, atlas subrectangle, both materials.
        var prototypeBytes = new float[] {
            10,2,20, 10.4f,2,20, 10.4f,2.6f,20, 10,2.6f,20,
            0,0,-1, 0,0,-1, 0,0,-1, 0,0,-1,
            .6f,.1f, .8f,.1f, .8f,.3f, .6f,.3f
        }.SelectMany(BitConverter.GetBytes).Concat(new uint[] { 0,1,2,0,2,3 }.SelectMany(BitConverter.GetBytes)).ToArray();
        string prototypeJson = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":152}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":48},
               {"buffer":0,"byteOffset":96,"byteLength":32},{"buffer":0,"byteOffset":128,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3"},
               {"bufferView":1,"componentType":5126,"count":4,"type":"VEC3"},{"bufferView":2,"componentType":5126,"count":4,"type":"VEC2"},
               {"bufferView":3,"componentType":5125,"count":6,"type":"SCALAR"},
               {"bufferView":3,"componentType":5125,"count":3,"type":"SCALAR"}],
             "materials":[{"name":"finland_gc_billboard"},{"name":"clutter_01_mesh"}],
             "meshes":[{"name":"grass","primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"material":0}]},
               {"name":"clutter","primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":4,"material":1}]}],
             "nodes":[{"translation":[3,4,5],"children":[1,2]},
               {"mesh":0,"translation":[7,8,9],"scale":[2,1,1]},{"mesh":1}],
             "scenes":[{"nodes":[0]}],"scene":0}
            """;
        void WritePrototype()
        {
            var encoded = Encoding.UTF8.GetBytes(prototypeJson); int padded = (encoded.Length + 3) & ~3;
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, "render", "ground_cover.glb")));
            w.Write(0x46546C67); w.Write(2); w.Write(28 + padded + prototypeBytes.Length);
            w.Write(padded); w.Write(0x4E4F534A); w.Write(encoded);
            for (int i = encoded.Length; i < padded; i++) w.Write((byte)' ');
            w.Write(prototypeBytes.Length); w.Write(0x004E4942); w.Write(prototypeBytes);
        }
        WritePrototype();
        Toml Config(string mode, int budget = 800, double density = 1)
        {
            string path = Path.Combine(dir, "test.toml");
            File.WriteAllText(path, $"[ground_cover]\nmode = \"{mode}\"\ndensity = {density.ToString(System.Globalization.CultureInfo.InvariantCulture)}\nmax_distance_m = 40\ntriangle_budget = {budget}\n[dr2_textures]\npackage_root = \"/Game/Test\"\nmax_size = 128\n");
            return Toml.Load(path);
        }
        int failures = 0;
        void Check(bool ok, string label) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} ground cover: {label}"); if (!ok) failures++; }
        var cfg = Config("scatter"); var dr2 = new Dr2Materials(cfg, dir, dir, dir);
        var (sections, report) = GroundCover.Build(ir, pl, cfg, _ => true, dr2);
        var plants = sections.Values.SelectMany(s => s.Pos).ToArray();
        var prototypes = GroundCover.LoadPrototypes(dir, dr2);
        Check(prototypes.Count == 2 && prototypes[0].Triangles == 8 && prototypes[1].Triangles == 1
            && Math.Abs(prototypes[0].Radius - 40) < .01f, "node hierarchy, scale, palette rebasing and real triangle costs");
        Check(sections.Count == 2 && report["prototypes_used"]!.GetValue<int>() == 2
            && sections["test_grass_d"].Idx.Count % 24 == 0
            && sections["test_clutter_d"].Idx.Count % 3 == 0
            && sections.Values.Sum(s => s.Idx.Count / 3) == report["triangles"]!.GetValue<int>(), "both prototypes/materials; crossed double-sided billboard and original clutter");
        Check(sections.Values.All(s => s.Uv.All(uv => uv.X >= .6f && uv.X <= .8f && uv.Y >= .1f && uv.Y <= .3f)), "atlas UV rectangle preserved");
        Check(plants.Length > 0 && report["triangles"]!.GetValue<int>() <= 800, "nonempty and within budget");
        Check(plants.All(p => road.Query(p.X, p.Y).clearance >= 199.99 && road.Query(p.X, p.Y).clearance <= 4000.01), "entire footprint outside road, within 2..40 m band");
        Check(sections.All(pair => {
            int stride = pair.Key == "test_grass_d" ? 16 : 4;
            return Enumerable.Range(0, pair.Value.Pos.Count / stride).All(i => {
                var a = pair.Value.Pos[i * stride]; var b = pair.Value.Pos[i * stride + 1];
                float width = Vector3.Distance(a, b), original = stride == 16 ? 80 : 40;
                return Math.Abs(a.Z - (a.X + b.X) * .05f) < .01f && width / original >= .7999f && width / original <= 1.2501f;
            });
        }), "bases follow sloping terrain; scale 0.8..1.25");
        Check(sections.Values.All(s => !s.Collision && s.Nrm.All(n => n.Z > 0.9f && Math.Abs(n.Length() - 1) < 1e-5f)), "no collision; unit sky-tilted normals on both faces");
        var again = GroundCover.Build(ir, pl, cfg, _ => true, dr2);
        Check(plants.SequenceEqual(again.sections.Values.SelectMany(s => s.Pos)), "deterministic scatter");
        Check(GroundCover.Build(ir, pl, Config("off"), _ => true, null).sections.Count == 0, "off needs no materials");
        Check(GroundCover.Build(ir, pl, Config("scatter", 0), _ => true, dr2).sections.Count == 0, "zero budget");
        var tiny = GroundCover.Build(ir, pl, Config("scatter", 7), _ => true, dr2);
        Check(tiny.report["triangles"]!.GetValue<int>() <= 7 && !tiny.sections.ContainsKey("test_grass_d"), "small budget excludes costly prototypes");
        Check(GroundCover.Build(ir, pl, Config("scatter", 800, 0), _ => true, dr2).sections.Count == 0, "zero density");
        Check(GroundCover.Build(ir, pl, cfg, _ => false, dr2).sections.Count == 0, "physics bounds rejection");
        File.WriteAllText(Path.Combine(dir, "stage.json"), """
            {"ornaments":{"instances":[
              {"template":"test_grass_01","transform":{"position":[10,1,50]}},
              {"template":"test_fern_01","transform":{"position":[0,0,50]}},
              {"template":"rock_01","transform":{"position":[10,1,50]}}
            ]}}
            """);
        var ornaments = GroundCover.Build(ir, pl, Config("ornaments"), _ => true, dr2);
        Check(ornaments.report["instances"]!.GetValue<int>() == 1 && ornaments.report["triangles"]!.GetValue<int>() is 1 or 8,
            "ornament plant anchors only; road plant rejected");
        dds[128] = 255; File.WriteAllBytes(Path.Combine(dir, "textures", "test_grass_d.dds"), dds);
        File.WriteAllBytes(Path.Combine(dir, "textures", "test_clutter_d.dds"), dds);
        Check(GroundCover.Build(ir, pl, cfg, _ => true, dr2).sections.Count == 0, "opaque terrain grass is never a plant card");
        dds[128] = 0;
        // Preview exports the same named sections, including cutout alpha and sky normals.
        dr2.MicBase["/Game/Test/Leaf"] = "/Game/Test/Grass";
        dr2.TexMip0["/Game/Test/Grass"] = (Dds.Fmt.BC3, 4, 4, dds[128..]); dr2.MaskedMics.Add("/Game/Test/Leaf");
        string preview = Path.Combine(dir, "preview");
        Preview.Write(preview, [new Preview.Carrier("test", sections.Values.ToList(), sections.Select(_ => 1).ToList(), new JsonArray(sections.Keys.Select(k => (JsonNode?)("ground_cover:" + k)).ToArray()))], _ => "/Game/Test/Leaf", dr2, ir, pl);
        var exported = new Glb(Path.Combine(preview, "preview.glb"));
        Check(exported.Primitives().Sum(p => p.Indices.Length / 3) == report["triangles"]!.GetValue<int>()
            && exported.Json["materials"]![0]!["alphaMode"]!.GetValue<string>() == "MASK", "preview keeps all triangles and cutout material");
        // Optional read-only integration check when the supplied Finland fixture is available.
        string finland = Path.Combine("work", "ir", "finland_rally_01_route_2");
        if (File.Exists(Path.Combine(finland, "render", "ground_cover.glb"))
            && File.Exists(Path.Combine(finland, "textures", "gc_billboard_d.dds"))
            && File.Exists(Path.Combine(finland, "textures", "gc_clutter_d.dds")))
        {
            var real = GroundCover.LoadPrototypes(finland, new Dr2Materials(cfg, finland, dir, dir));
            Console.WriteLine($"Finland prototype inventory: {real.Count}, {real.Sum(p => p.Triangles)} triangles, {real.Count(p => p.Weight > 1)} grass-weighted, max radius {real.Select(p => p.Radius).DefaultIfEmpty().Max():0.##} cm");
            Check(real.Count == 78 && real.Sum(p => p.Triangles) == 1828
                && real.SelectMany(p => p.Parts).Select(p => p.Texture).Distinct().Count() == 2
                && real.Any(p => p.Weight > 1) && real.All(p => p.Weight > 0 && p.Radius < 200),
                "Finland: all 78 prototypes, 1828 emitted triangles, both atlases, grass bias and local footprints");
        }
        return failures == 0 ? 0 : 1;
    }
}
