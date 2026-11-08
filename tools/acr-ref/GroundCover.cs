using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AcrRef.Splines;

namespace AcrRef;

/// Render-only plants. All distances here are UE centimetres except config and sampled areas.
internal static class GroundCover
{
    static bool Plant(string name) => Regex.IsMatch(name, @"(^|[_\W])(grass|fern|flower|weed|plant|groundcover)([_\W]|\d|$)", RegexOptions.IgnoreCase);
    static string TexName(string reference) => Path.GetFileNameWithoutExtension(reference[(reference.LastIndexOf('#') + 1)..]);

    public static (Dictionary<string, ProcMesh.Section> sections, JsonObject report) Build(
        StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Func<V3, bool> inBounds, Dr2Materials? dr2, Visuals.TopSurface ground)
    {
        var sections = new Dictionary<string, ProcMesh.Section>();
        var settings = cfg["ground_cover"];
        var mode = settings.TryGetValue("mode", out var m) ? m as string : "off";
        if (mode is not ("off" or "scatter" or "ornaments")) throw new InvalidDataException("ground_cover.mode must be off, scatter or ornaments");
        var report = new JsonObject { ["mode"] = mode, ["triangles"] = 0, ["instances"] = 0, ["prototypes_used"] = 0 };
        if (mode == "off") { Console.WriteLine("ground cover: off, 0 triangles"); return (sections, report); }
        double Num(string key, double fallback) => settings.TryGetValue(key, out var v) && v is double n ? n : fallback;
        double density = Num("density", 1.5), maxDistance = Num("max_distance_m", 6);
        double budgetValue = Num("triangle_budget", 1200000);
        if (!double.IsFinite(density) || density < 0 || density > 100 || !double.IsFinite(maxDistance) || maxDistance < 2 || maxDistance > 40
            || !double.IsFinite(budgetValue) || budgetValue < 0 || budgetValue > 2000000 || budgetValue != Math.Floor(budgetValue))
            throw new InvalidDataException("ground_cover: density must be 0..100, max_distance_m 2..40, triangle_budget an integer 0..2000000");
        int budget = (int)budgetValue;
        if (dr2 is null) throw new InvalidDataException("ground_cover requires --visuals-dr2 1 and --visuals-textured 1");

        var allowed = settings.TryGetValue("textures", out var ts) && ts is object[] { Length: > 0 } list
            ? list.Cast<string>().ToHashSet(StringComparer.Ordinal) : null;
        var prototypes = LoadPrototypes(ir.Dir, dr2, allowed);
        var textures = prototypes.SelectMany(p => p.Parts).Select(p => p.Texture).Distinct().Order().ToArray();
        report["prototypes_available"] = prototypes.Count;
        report["textures"] = new JsonArray(textures.Select(t => (JsonNode?)t).ToArray());
        report["triangle_budget"] = budget;
        if (prototypes.Count == 0)
        {
            report["skipped"] = "no usable ground_cover.glb prototypes with cutout diffuse textures";
            Console.WriteLine($"ground cover: {mode}, 0 triangles, 0 prototypes used (no usable prototypes)");
            return (sections, report);
        }
        var road = new RoadCorridor(ir.Gates.Select(g => pl.ToUe(g.crossing)).ToList(),
            ir.Gates.Select(g => pl.ToUe(g.left)).ToList(), ir.Gates.Select(g => pl.ToUe(g.right)).ToList());
        var random = new Random(19427);
        int kept = 0, rejected = 0, triangleCount = 0;
        int minTriangles = prototypes.Min(p => p.Triangles);
        var used = new HashSet<int>();
        var alpha = CheckAlpha(ir.Dir, prototypes);
        int transparentPlaced = 0, flippedTransparentPlaced = 0, missingGround = 0, patches = 0;
        void Place(Vector3 p)
        {
            var eligible = prototypes.Where(p => p.Triangles <= budget - triangleCount).ToArray();
            if (eligible.Length == 0) return;
            int pick = random.Next(eligible.Sum(p => p.Weight));
            var prototype = eligible[0];
            foreach (var candidate in eligible)
            { if (pick < candidate.Weight) { prototype = candidate; break; } pick -= candidate.Weight; }
            float scale = 0.8f + (float)random.NextDouble() * 0.45f;
            float radius = prototype.Radius * scale;
            double clearance = road.Query(p.X, p.Y).clearance;
            // Bound the whole transformed prototype, including the second billboard and hairpins.
            if (clearance - radius < 200 || clearance + radius > maxDistance * 100)
            { rejected++; return; }
            if (ground.At(new(p.X, p.Y)) is not { } height) { missingGround++; rejected++; return; }
            p.Z = (float)height + 2;
            var yaw = Matrix4x4.CreateRotationZ((float)(random.NextDouble() * Math.Tau));
            foreach (var part in prototype.Parts)
            {
                if (!sections.TryGetValue(part.Texture, out var section)) sections[part.Texture] = section = new ProcMesh.Section();
                Emit(section, part, p, scale, yaw);
            }
            triangleCount += prototype.Triangles; used.Add(prototype.Id); kept++;
            if (alpha[prototype.Id].transparent) transparentPlaced++;
            if (alpha[prototype.Id].flipped) flippedTransparentPlaced++;
        }
        if (mode == "scatter" && density > 0 && budget >= minTriangles)
        {
            // Collision selects grass-class patch centres; rendered ground supplies every plant's final Z.
            var triangles = new List<(Vector3 a, Vector3 b, Vector3 c)>();
            var cumulative = new List<double>(); double area = 0;
            foreach (var prim in new Glb(Path.Combine(ir.Dir, "collision.glb")).Primitives())
            {
                if (!Regex.IsMatch(prim.Material, @"^(GR|GS|GGL|GWT|DEFA|DBK)", RegexOptions.IgnoreCase)) continue;
                Vector3 P(uint v) { var q = pl.ToUe(new[] { (double)prim.Positions[v * 3], prim.Positions[v * 3 + 1], prim.Positions[v * 3 + 2] }); return new((float)q.X, (float)q.Y, (float)q.Z); }
                for (int t = 0; t < prim.Indices.Length; t += 3)
                {
                    // physics bounds are tested in DR2 metres (StageBuild.InPhysicsBounds), not on the UE point
                    uint i0 = prim.Indices[t], i1 = prim.Indices[t + 1], i2 = prim.Indices[t + 2];
                    if (!inBounds(new((prim.Positions[i0 * 3] + prim.Positions[i1 * 3] + prim.Positions[i2 * 3]) / 3.0, (prim.Positions[i0 * 3 + 1] + prim.Positions[i1 * 3 + 1] + prim.Positions[i2 * 3 + 1]) / 3.0, (prim.Positions[i0 * 3 + 2] + prim.Positions[i1 * 3 + 2] + prim.Positions[i2 * 3 + 2]) / 3.0))) continue;
                    var a = P(i0); var b = P(i1); var c = P(i2);
                    var cross = Vector3.Cross(b - a, c - a);
                    if (cross.LengthSquared() < 1 || Math.Abs(cross.Z) / cross.Length() < 0.65) continue;
                    var centre = (a + b + c) / 3;
                    float reach = Math.Max(Vector3.Distance(centre, a), Math.Max(Vector3.Distance(centre, b), Vector3.Distance(centre, c)));
                    double clearance = road.Query(centre.X, centre.Y).clearance;
                    if (clearance + reach < 200 || clearance - reach > maxDistance * 100) continue;
                    area += Math.Abs(cross.Z) / 20000.0; // projected square metres
                    triangles.Add((a, b, c)); cumulative.Add(area);
                }
            }
            // Uniform sampling over all eligible area keeps a small budget spread along the whole stage.
            int attempts = (int)Math.Min(Math.Floor(area * density + random.NextDouble()), budget / minTriangles);
            for (int i = 0; i < attempts; i++)
            {
                if (budget - triangleCount < minTriangles) break;
                int ix = cumulative.BinarySearch(random.NextDouble() * area); if (ix < 0) ix = ~ix;
                var (a, b, c) = triangles[ix];
                float u = (float)random.NextDouble(), v = (float)random.NextDouble();
                if (u + v > 1) { u = 1 - u; v = 1 - v; }
                var centre = a + (b - a) * u + (c - a) * v;
                int count = random.Next(4, 13); patches++;
                float patchRadius = 40 + (float)random.NextDouble() * 110;
                for (int k = 0; k < count; k++)
                {
                    float angle = (float)(random.NextDouble() * Math.Tau), distance = patchRadius * MathF.Sqrt((float)random.NextDouble());
                    Place(centre + new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, 0));
                }
            }
            report["candidate_area_m2"] = area;
            report["attempts"] = attempts;
        }
        else if (mode == "ornaments")
        {
            var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!;
            var instances = (stage["ornaments"]?["instances"]?.AsArray() ?? []).Where(i => Plant(i?["template"]?.GetValue<string>() ?? "")).ToList();
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instances));
            foreach (var instance in instances)
            {
                if (budget - triangleCount < minTriangles) break;
                var p = Vector3.Transform(Vector3.Zero, ObjectColliders.Trs(instance!["transform"]));
                if (!inBounds(new(p.X, p.Y, p.Z))) { rejected++; continue; }
                var q = pl.ToUe(new[] { (double)p.X, p.Y, p.Z });
                Place(new((float)q.X, (float)q.Y, (float)q.Z));
            }
            report["plant_ornaments"] = instances.Count;
        }
        report["instances"] = kept; report["triangles"] = triangleCount; report["rejected"] = rejected; report["prototypes_used"] = used.Count;
        report["patches"] = patches; report["missing_drawn_ground"] = missingGround;
        report["uv_centre_transparent_instances"] = transparentPlaced;
        report["uv_centre_flipped_v_transparent_instances"] = flippedTransparentPlaced;
        Console.WriteLine($"ground cover: drawn-ground +2 cm, {patches} candidate patches (4..12 plants, radius 0.4..1.5 m), {missingGround} missing-ground rejects");
        Console.WriteLine($"ground cover: UV centre alpha: {transparentPlaced}/{kept} placed prototypes on transparent texels; flipped V would be {flippedTransparentPlaced}/{kept}; leaf MIC = tree-card LeafMic");
        Console.WriteLine($"ground cover: {mode}, {kept} plants, {triangleCount} triangles (budget {budget}), {textures.Length} textures, {used.Count} prototypes used, {rejected} rejected");
        return (sections, report);
    }

    internal sealed record Part(string Texture, bool Billboard, Vector3[] Positions, Vector3[] Normals, Vector2[] Uv, uint[] Indices);
    internal sealed record Prototype(int Id, string Name, Part[] Parts, float Radius, int Triangles, int Weight);

    internal static Dictionary<int, (bool transparent, bool flipped)> CheckAlpha(string dir, IReadOnlyList<Prototype> prototypes)
    {
        var decoded = new Dictionary<string, (byte[] pixels, int w, int h)>();
        bool Transparent(Part part, bool flip)
        {
            if (!decoded.TryGetValue(part.Texture, out var image))
            {
                var d = new Dds(Path.Combine(dir, "textures", part.Texture + ".dds"));
                image = (d.Format == Dds.Fmt.BC7 ? Preview.DecodeBc7(d.Mips[0], d.Width, d.Height) : d.Decode(0)!, d.Width, d.Height);
                decoded[part.Texture] = image;
            }
            // Bounding UV rectangle centre, sampled in the DDS's top-to-bottom row convention.
            float u = (part.Uv.Min(v => v.X) + part.Uv.Max(v => v.X)) / 2;
            float v = (part.Uv.Min(v => v.Y) + part.Uv.Max(v => v.Y)) / 2;
            if (flip) v = 1 - v;
            int x = Math.Clamp((int)(u * image.w), 0, image.w - 1), y = Math.Clamp((int)(v * image.h), 0, image.h - 1);
            return image.pixels[(y * image.w + x) * 4 + 3] < 128;
        }
        var result = prototypes.ToDictionary(p => p.Id, p => (transparent: p.Parts.All(part => Transparent(part, false)), flipped: p.Parts.All(part => Transparent(part, true))));
        Console.WriteLine($"ground cover: atlas alpha inventory: {result.Values.Count(v => v.transparent)}/{prototypes.Count} prototype centres transparent; flipped V {result.Values.Count(v => v.flipped)}/{prototypes.Count}");
        foreach (var texture in prototypes.SelectMany(p => p.Parts).Select(p => p.Texture).Distinct())
        {
            var parts = prototypes.SelectMany(p => p.Parts).Where(p => p.Texture == texture).ToArray();
            Console.WriteLine($"ground cover: {texture} UV centre alpha: {parts.Count(p => Transparent(p, false))}/{parts.Length} transparent; flipped V {parts.Count(p => Transparent(p, true))}/{parts.Length}");
        }
        return result;
    }

    internal static List<Prototype> LoadPrototypes(string dir, Dr2Materials dr2, HashSet<string>? allowed = null)
    {
        var result = new List<Prototype>();
        string path = Path.Combine(dir, "render", "ground_cover.glb"), materials = Path.Combine(dir, "render", "ground_cover.materials.json");
        if (!File.Exists(path) || !File.Exists(materials)) return result;
        var diffuse = new Dictionary<string, string>();
        foreach (var mat in JsonNode.Parse(File.ReadAllText(materials))!.AsArray())
            foreach (var tex in mat?["textures"]?.AsArray() ?? [])
                if (tex?["parameter"]?.GetValue<string>().Contains("diffuse", StringComparison.OrdinalIgnoreCase) == true)
                    diffuse[mat!["name"]!.GetValue<string>()] = TexName(tex["texture"]!.GetValue<string>());
        var usable = diffuse.Values.Distinct().Where(t => (allowed is null || allowed.Contains(t))
            && dr2.HasTexture(t) && dr2.Transparency(t) > 0.05).ToHashSet();
        foreach (var group in new Glb(path).NodePrimitives().GroupBy(p => p.Node))
        {
            var prims = group.Select(p => p.Primitive).ToArray();
            // The exported asset is a palette: remove layout offsets, preserving each mesh's shape.
            var all = prims.SelectMany(p => Enumerable.Range(0, p.Positions.Length / 3).Select(i => new Vector3(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]))).ToArray();
            var min = all.Aggregate(Vector3.Min); var max = all.Aggregate(Vector3.Max);
            var origin = new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2);
            var parts = new List<Part>();
            foreach (var prim in prims)
            {
                if (!diffuse.TryGetValue(prim.Material, out var texture) || !usable.Contains(texture)) continue;
                if (prim.Uv.Length == 0 || prim.Uv[0] is not { } uv) throw new InvalidDataException($"ground cover: {prim.Mesh} has no atlas UVs");
                var pos = new Vector3[prim.Positions.Length / 3]; var normals = new Vector3[pos.Length];
                Vector3 Ue(Vector3 v) => new(v.X, v.Z, v.Y);
                for (int i = 0; i < pos.Length; i++)
                {
                    pos[i] = Ue(new Vector3(prim.Positions[i * 3], prim.Positions[i * 3 + 1], prim.Positions[i * 3 + 2]) - origin) * 100;
                    if (prim.Normal is { } n) normals[i] = Ue(new(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]));
                }
                if (prim.Normal is null)
                    for (int t = 0; t < prim.Indices.Length; t += 3)
                    {
                        uint a = prim.Indices[t], b = prim.Indices[t + 1], c = prim.Indices[t + 2];
                        var n = Vector3.Cross(pos[c] - pos[a], pos[b] - pos[a]);
                        normals[a] += n; normals[b] += n; normals[c] += n;
                    }
                for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
                parts.Add(new(texture, prim.Material.Contains("billboard", StringComparison.OrdinalIgnoreCase), pos, normals,
                    Enumerable.Range(0, pos.Length).Select(i => new Vector2(uv[i * 2], uv[i * 2 + 1])).ToArray(), prim.Indices));
            }
            if (parts.Count == 0) continue;
            float radius = parts.SelectMany(p => p.Positions).Max(p => new Vector2(p.X, p.Y).Length());
            int triangles = parts.Sum(p => p.Indices.Length / 3 * (p.Billboard ? 4 : 1));
            // Finland's upper-right atlas patches are grass tufts; names alone are opaque (t01-01 etc).
            // Modest bias keeps every flower, shrub, fern and clutter mesh eligible.
            bool grass = prims.Any(p => p.Mesh.Contains("grass", StringComparison.OrdinalIgnoreCase)) || parts.Any(p => p.Billboard
                && p.Uv.Average(v => v.X) > 0.55 && p.Uv.Average(v => v.Y) < 0.42);
            if (triangles > 0) result.Add(new(group.Key, prims[0].Mesh, parts.ToArray(), radius, triangles, grass ? 4 : 1));
        }
        return result;
    }

    static void Emit(ProcMesh.Section s, Part part, Vector3 anchor, float scale, Matrix4x4 yaw)
    {
        for (int copy = 0; copy < (part.Billboard ? 2 : 1); copy++)
        {
            var rotation = Matrix4x4.CreateRotationZ(copy * MathF.PI / 2) * yaw;
            for (int face = 0; face < (part.Billboard ? 2 : 1); face++)
            {
                uint start = (uint)s.Pos.Count;
                for (int i = 0; i < part.Positions.Length; i++)
                {
                    s.Pos.Add(anchor + Vector3.Transform(part.Positions[i] * scale, rotation));
                    var n = Vector3.TransformNormal(part.Normals[i], rotation) * (face == 0 ? 1 : -1);
                    s.Nrm.Add(Vector3.Normalize(n * 0.35f + Vector3.UnitZ));
                    s.Uv.Add(part.Uv[i]);
                }
                // X,Z,Y mirrors glTF CCW into UE clockwise; only reverse back faces.
                for (int t = 0; t < part.Indices.Length; t += 3)
                {
                    s.Idx.Add(start + part.Indices[t]);
                    s.Idx.Add(start + part.Indices[t + (face == 0 ? 1 : 2)]);
                    s.Idx.Add(start + part.Indices[t + (face == 0 ? 2 : 1)]);
                }
            }
        }
    }
}
