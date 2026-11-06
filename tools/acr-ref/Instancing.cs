using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrRef;

/// Offline proof of instanced DR2 trees (docs/instancing-plan.md), for the one stage named by [instancing] stage in config/visuals.toml:
///   part 1  one DR2 tree template (objects.glb) as a cooked StaticMesh package, StaticMeshPackage, with the DR2-derived MICs;
///   part 2  a plain HierarchicalInstancedStaticMeshComponent (NoCollision, one leaf cluster, identity tables) added to the
///           gameplay cell with LegacyPackage.AddExport, carrying that template's real placements near the road.
/// The component is attached to the DefaultSceneRoot of BC_ReplayCamerasActor_C (the one gameplay-cell actor that already
/// serialises InstanceComponents); the root has a location and nothing else, so instance matrices are component-local =
/// DR2 world position (the same UE cm as every other position in the cell) minus that location.
internal static class Instancing
{
    /// On for the DR2 stage named by `stage` only: the proof places one template, which other locations do not have.
    public static bool Enabled(Toml cfg, string stage) => cfg["instancing"].TryGetValue("enabled", out var v) && v is double d && d != 0 && cfg.Str("instancing", "stage") == stage;

    static string Cfg(Toml cfg, string key) => cfg.Str("instancing", key);

    // 96-byte export entry of HISM_SM_LarchWales01_4 in a Hafren South PCG cell (class and template are imports of that package,
    // outer an export of it; AddExport patches them, the name and the flags)
    const string HismEntry = "DEFFFFFF00000000B1FFFFFF200100006400000005000000000000007204000000000000B6256300000000000000000000000000010000000000000000000000000000000000000000000000D303000000000000020000000200000001000000";

    // ---- part 1: the mesh -------------------------------------------------------------------------------------------------

    /// DR2 mesh-local (metres, Y up) to mesh-local UE (cm, Z up): the same swap as Placement.ToUe, so it mirrors.
    static Vector3 ToUeLocal(float x, float y, float z) => new(x * 100, z * 100, y * 100);

    internal sealed record TemplateMesh(StaticMeshPackage.Geometry Geometry, List<StaticMeshPackage.Material> Materials, int[] TrianglesPerMaterial, int Skipped);

    /// The template's primitives whose material is a tree material (materialKey returns its section key and MIC path; null skips
    /// the primitive, e.g. the billboard), one section per material, vertices converted to mesh-local UE cm.
    public static TemplateMesh LoadTemplate(string glbPath, string template, Func<string, string, int, (string key, string mic)?> materialOf)
    {
        var g = new StaticMeshPackage.Geometry();
        var order = new List<string>();
        var sections = new Dictionary<string, List<uint>>();
        var mics = new Dictionary<string, string>();
        var area3 = new Dictionary<string, double>(); var areaUv = new Dictionary<string, double>();
        int skipped = 0, found = 0;
        foreach (var p in new Glb(glbPath).Primitives(withUv: true).Where(p => p.Mesh == template))
        {
            found++;
            if (materialOf(template, p.Material, p.Indices.Length / 3) is not var (key, mic)) { skipped += p.Indices.Length / 3; continue; }
            int b = g.Pos.Count, nv = p.Positions.Length / 3;
            var t0 = p.Uv.Length > 0 ? p.Uv[0] : null;
            for (int i = 0; i < nv; i++)
            {
                g.Pos.Add(ToUeLocal(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]));
                g.Uv.Add(t0 is null ? Vector2.Zero : new Vector2(t0[i * 2], t0[i * 2 + 1]));
                var n = p.Normal is null ? Vector3.Zero : ToUeLocal(p.Normal[i * 3], p.Normal[i * 3 + 1], p.Normal[i * 3 + 2]) / 100;
                g.Nrm.Add(n);
            }
            if (!sections.TryGetValue(key, out var idx)) { sections[key] = idx = new(); order.Add(key); mics[key] = mic; area3[key] = 0; areaUv[key] = 0; }
            for (int t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                uint a = (uint)(b + p.Indices[t]), c = (uint)(b + p.Indices[t + 1]), d = (uint)(b + p.Indices[t + 2]);
                // glTF CCW front faces under the mirroring DR2 -> UE map are UE's clockwise front faces: the order is kept
                idx.Add(a); idx.Add(c); idx.Add(d);
                area3[key] += Vector3.Cross(g.Pos[(int)c] - g.Pos[(int)a], g.Pos[(int)d] - g.Pos[(int)a]).Length() / 2;
                var u1 = g.Uv[(int)c] - g.Uv[(int)a]; var u2 = g.Uv[(int)d] - g.Uv[(int)a];
                areaUv[key] += Math.Abs(u1.X * u2.Y - u2.X * u1.Y) / 2;
            }
        }
        if (found == 0) throw new InvalidDataException($"{glbPath}: no mesh named {template}");
        if (order.Count == 0) throw new InvalidDataException($"{template}: no tree material (every primitive was skipped)");
        // vertex normals: the glb's where it has them, else the area-weighted face normals
        var acc = new Vector3[g.Pos.Count];
        foreach (var idx in sections.Values)
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                var f = Vector3.Cross(g.Pos[(int)idx[t + 2]] - g.Pos[(int)idx[t]], g.Pos[(int)idx[t + 1]] - g.Pos[(int)idx[t]]);   // outward under UE's winding
                foreach (var k in new[] { idx[t], idx[t + 1], idx[t + 2] }) acc[k] += f;
            }
        for (int i = 0; i < g.Pos.Count; i++)
        {
            var n = g.Nrm[i];
            g.Nrm[i] = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : acc[i].LengthSquared() > 0 ? Vector3.Normalize(acc[i]) : Vector3.UnitZ;
        }
        // tangents along +U (as Visuals.UvTangents, the writer the ProceduralMesh trees already use)
        var tan = new Vector3[g.Pos.Count];
        foreach (var idx in sections.Values)
            for (int t = 0; t + 2 < idx.Count; t += 3)
            {
                int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
                Vector3 e1 = g.Pos[b] - g.Pos[a], e2 = g.Pos[c] - g.Pos[a];
                Vector2 d1 = g.Uv[b] - g.Uv[a], d2 = g.Uv[c] - g.Uv[a];
                float det = d1.X * d2.Y - d2.X * d1.Y;
                if (MathF.Abs(det) < 1e-12f) continue;
                var tg = (e1 * d2.Y - e2 * d1.Y) / det;
                tan[a] += tg; tan[b] += tg; tan[c] += tg;
            }
        for (int i = 0; i < g.Pos.Count; i++)
        {
            var n = g.Nrm[i];
            var t = tan[i] - n * Vector3.Dot(n, tan[i]);
            g.Tan.Add(t.LengthSquared() > 1e-12f ? Vector3.Normalize(t)
                : Vector3.Normalize(MathF.Abs(n.Z) < 0.9f ? Vector3.Cross(Vector3.UnitZ, n) : Vector3.Cross(Vector3.UnitX, n)));
        }
        var mats = new List<StaticMeshPackage.Material>();
        foreach (var key in order)
        {
            g.Sections.Add(new StaticMeshPackage.Geometry.Section(mats.Count, sections[key]));
            float density = areaUv[key] > 1e-12 ? (float)Math.Sqrt(area3[key] / areaUv[key]) : 100f;
            mats.Add(new StaticMeshPackage.Material(mics[key], Regex.Replace(key.Split('@')[0], "[^A-Za-z0-9_]", "_"), density));
        }
        return new TemplateMesh(g, mats, g.Sections.Select(s => s.Idx.Count / 3).ToArray(), skipped);
    }

    /// The far LOD: DR2's own billboard (the <template>_lod_d strip of views) as two vertical cards crossing at the mesh centre, a
    /// quarter turn apart, as tall as the tree and as wide (a view is a square image of the whole plant), v = 0 at the top.
    public static StaticMeshPackage.Geometry Billboard(Vector3 mn, Vector3 mx, double maxHalfCm, int frames, int material, out float density)
    {
        var g = new StaticMeshPackage.Geometry();
        var c = (mn + mx) * 0.5f;
        float z0 = mn.Z, z1 = mx.Z, r = (float)Math.Min(maxHalfCm, (z1 - z0) / 2);
        var idx = new List<uint>();
        void Card(double yaw, int view)
        {
            var d = new Vector3((float)(Math.Cos(yaw) * r), (float)(Math.Sin(yaw) * r), 0);
            var mid = new Vector3(c.X, c.Y, 0);
            float uc = (view + 0.5f) / frames, uh = 0.5f / frames;
            var n = Vector3.Normalize(new Vector3(-d.Y, d.X, 0));
            uint i0 = (uint)g.Pos.Count;
            foreach (var (p, uv) in new[] { (mid - d + new Vector3(0, 0, z0), new Vector2(uc - uh, 1)), (mid + d + new Vector3(0, 0, z0), new Vector2(uc + uh, 1)),
                                            (mid + d + new Vector3(0, 0, z1), new Vector2(uc + uh, 0)), (mid - d + new Vector3(0, 0, z1), new Vector2(uc - uh, 0)) })
            { g.Pos.Add(p); g.Nrm.Add(n); g.Tan.Add(Vector3.Normalize(d)); g.Uv.Add(uv); }
            idx.AddRange([i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2]);
        }
        Card(0, 0);
        Card(Math.PI / 2, Math.Max(1, frames / 4) % Math.Max(1, frames));
        g.Sections.Add(new StaticMeshPackage.Geometry.Section(material, idx));
        density = (float)Math.Sqrt(2 * r * (z1 - z0) / (1.0 / frames));
        return g;
    }

    // ---- part 2: the instances ------------------------------------------------------------------------------------------

    /// FMatrix (4x4 f64, row-major: three axis rows times scale, then the translation) taking mesh-local UE cm to world UE cm for
    /// one DR2 instance: world = ToUe(M * Sinv(q)); the mirror is in the mesh, so the 3x3 part is a proper rotation times scale.
    static double[] InstanceMatrix(Matrix4x4 m, StageBuild.Placement pl, out Splines.V3 origin)
    {
        // The axes come from the 3x3 part alone, in doubles: differencing two float32 world positions thousands of metres out
        // lost 2% of the scale. Mesh-local UE q -> DR2 local d = (qx, qz, qy)/100 -> d * M -> placement rotation about vertical
        // (x' = cos x + sin z, z' = -sin x + cos z) -> UE (x', z', y): row i of the matrix is the image of UE axis i.
        double r = pl.ThetaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double[] Axis(double x, double y, double z) => [c * x + s * z, -s * x + c * z, y];
        var a0 = Axis(m.M11, m.M12, m.M13);      // DR2 x row  -> UE X
        var a1 = Axis(m.M31, m.M32, m.M33);      // DR2 z row  -> UE Y
        var a2 = Axis(m.M21, m.M22, m.M23);      // DR2 y row  -> UE Z
        origin = pl.ToUe([m.M41, m.M42, m.M43]);
        return [a0[0], a0[1], a0[2], 0, a1[0], a1[1], a1[2], 0, a2[0], a2[1], a2[2], 0, origin.X, origin.Y, origin.Z, 1];
    }

    internal sealed record Placed(string Id, Splines.V3 World, double[] Matrix, double RouteM);

    /// Instances of the template in stage.json inside the physics bounds and within near_m of the route, nearest first, at most max.
    public static List<Placed> Select(StageBuild.Ir ir, StageBuild.Placement pl, string template, double nearM, int max, Func<Splines.V3, bool> inPhysicsBounds)
    {
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)), nearM + 50);
        var list = new List<Placed>();
        foreach (var inst in stage["instances"]!.AsArray())
        {
            if (inst!["template"]!.GetValue<string>() != template) continue;
            var m = ObjectColliders.Trs(inst["transform"]);
            var o = Vector3.Transform(Vector3.Zero, m);
            if (!inPhysicsBounds(pl.ToPhysics(o.X, o.Y, o.Z))) continue;
            var mat = InstanceMatrix(m, pl, out var world);
            double d = route.Metres(new Vector2((float)world.X, (float)world.Y));
            if (d <= nearM) list.Add(new Placed(inst["id"]!.GetValue<string>(), world, mat, d));
        }
        return list.OrderBy(p => p.RouteM).ThenBy(p => p.Id, StringComparer.Ordinal).Take(max).ToList();
    }

    static (Vector3 mn, Vector3 mx, Vector3 minScale, Vector3 maxScale) InstanceBounds(IReadOnlyList<double[]> mats, Vector3 meshMin, Vector3 meshMax, Vector3 origin)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        var smin = new Vector3(float.MaxValue); var smax = new Vector3(float.MinValue);
        foreach (var m in mats)
        {
            foreach (var x in new[] { meshMin.X, meshMax.X }) foreach (var y in new[] { meshMin.Y, meshMax.Y }) foreach (var z in new[] { meshMin.Z, meshMax.Z })
            {
                var p = new Vector3((float)(x * m[0] + y * m[4] + z * m[8] + m[12]), (float)(x * m[1] + y * m[5] + z * m[9] + m[13]), (float)(x * m[2] + y * m[6] + z * m[10] + m[14]));
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
            }
            var s = new Vector3((float)Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]), (float)Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]), (float)Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]));
            smin = Vector3.Min(smin, s); smax = Vector3.Max(smax, s);
        }
        return (mn, mx, smin, smax);
    }

    /// The component's property block and native tail (UE 5.6 HISM, found from the Hafren South PCG cells; docs/instancing-plan.md).
    /// mats are component-local; a single leaf cluster node covers all instances and the sort / reorder tables are the identity.
    static byte[] HismData(int staticMesh, int attachParent, int noCollisionName, IReadOnlyList<double[]> mats, Vector3 meshMin, Vector3 meshMax, int seed)
    {
        int n = mats.Count;
        var (mn, mx, smin, smax) = InstanceBounds(mats, meshMin, meshMax, Vector3.Zero);
        var centre = (meshMin + meshMax) * 0.5f; var half = (meshMax - meshMin) * 0.5f;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // 2 SortedInstances, 3 NumBuiltInstances, 4 BuiltInstanceBounds, 9 CacheMeshExtendedBounds, 10 InstanceCountToRender,
        // 15 InstancingRandomSeed, 24 bDisableCollision, 26 InstanceReorderTable, 36 StaticMesh, 174 BodyInstance, 191 AttachParent,
        // 216 Mobility (zero = Static), 231 CreationMethod (Instance)
        Unv.WriteHeader(w, [new(2, false), new(3, false), new(4, false), new(9, false), new(10, false), new(15, false), new(24, false), new(26, false),
                            new(36, false), new(174, false), new(191, false), new(216, true), new(231, false)]);
        w.Write(n); for (int i = 0; i < n; i++) w.Write(i);
        w.Write(n);
        foreach (var v in new[] { mn.X, mn.Y, mn.Z, mx.X, mx.Y, mx.Z }) w.Write((double)v);
        w.Write((byte)1);
        w.Write((byte)0x00); w.Write((byte)0x07);
        foreach (var v in new[] { centre.X, centre.Y, centre.Z, half.X, half.Y, half.Z }) w.Write((double)v);
        w.Write((double)Math.Max(half.Length(), 1));
        w.Write(n);
        w.Write(seed);
        w.Write((byte)1);
        w.Write(n); for (int i = 0; i < n; i++) w.Write(i);
        w.Write(staticMesh);
        Unv.WriteHeader(w, [new(3, true), new(4, true), new(28, false)]);
        w.Write(noCollisionName); w.Write(0);
        w.Write(attachParent);
        w.Write((byte)3);
        // native tail: the ISMC block before the matrices, the matrices, an empty block, the cluster tree (one leaf)
        foreach (var v in new[] { 0, 0, 0, 1, 0, 1, 1 }) w.Write(v);
        w.Write(128); w.Write(n);
        foreach (var m in mats) foreach (var v in m) w.Write(v);
        w.Write(4); w.Write(0); w.Write(0);
        w.Write(64); w.Write(1);
        w.Write(mn.X); w.Write(mn.Y); w.Write(mn.Z); w.Write(-1);
        w.Write(mx.X); w.Write(mx.Y); w.Write(mx.Z); w.Write(-1);
        w.Write(0); w.Write(n - 1);
        w.Write(smin.X); w.Write(smin.Y); w.Write(smin.Z);
        w.Write(smax.X); w.Write(smax.Y); w.Write(smax.Z);
        return ms.ToArray();
    }

    static Vector3 RootLocation(LegacyPackage cell, int root)
    {
        var d = cell.GetExportData(root);
        int p = 0;
        var slots = Unv.ReadHeader(d, ref p);
        if (slots.Any(s => s.Index is 6 or 7) || slots.FirstOrDefault(s => !s.Zero).Index != 5)
            throw new InvalidDataException($"{cell.ExportName(root)}: expected RelativeLocation (slot 5) and no rotation or scale");
        var v = new Vector3((float)BitConverter.ToDouble(d, p), (float)BitConverter.ToDouble(d, p + 8), (float)BitConverter.ToDouble(d, p + 16));
        if (!float.IsFinite(v.X + v.Y + v.Z)) throw new InvalidDataException($"{cell.ExportName(root)}: location {v}");
        return v;
    }

    /// Adds the HISM to the cell, owned by BC_ReplayCamerasActor_C and attached to its DefaultSceneRoot. Returns the report.
    public static JsonObject AddHism(LegacyPackage cell, string meshPackage, string name, IReadOnlyList<Placed> placed, Vector3 meshMin, Vector3 meshMax, int seed)
    {
        int actor = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == "BC_ReplayCamerasActor_C");
        var deps = cell.ExportDeps(actor);
        int root = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportOuter(e) == actor + 1 && cell.ExportName(e) == "DefaultSceneRoot");
        var loc = RootLocation(cell, root);

        // imports: the mesh package, the component class and its default object
        int enginePkg = cell.FindImport("/Script/CoreUObject", "Package", "/Script/Engine", 0);
        if (enginePkg == 0) throw new InvalidDataException("the cell has no /Script/Engine package import");
        int cls = cell.FindImport("/Script/CoreUObject", "Class", "HierarchicalInstancedStaticMeshComponent", enginePkg);
        if (cls == 0) cls = cell.AddImport("/Script/CoreUObject", "Class", enginePkg, "HierarchicalInstancedStaticMeshComponent");
        int cdo = cell.FindImport("/Script/Engine", "HierarchicalInstancedStaticMeshComponent", "Default__HierarchicalInstancedStaticMeshComponent", enginePkg);
        if (cdo == 0) cdo = cell.AddImport("/Script/Engine", "HierarchicalInstancedStaticMeshComponent", enginePkg, "Default__HierarchicalInstancedStaticMeshComponent");
        string obj = meshPackage[(meshPackage.LastIndexOf('/') + 1)..];
        int pkgImp = cell.FindImport("/Script/CoreUObject", "Package", meshPackage, 0);
        if (pkgImp == 0) pkgImp = cell.AddImport("/Script/CoreUObject", "Package", 0, meshPackage);
        int meshImp = cell.FindImport("/Script/Engine", "StaticMesh", obj, pkgImp);
        if (meshImp == 0) meshImp = cell.AddImport("/Script/Engine", "StaticMesh", pkgImp, obj);

        var local = placed.Select(p => { var m = (double[])p.Matrix.Clone(); m[12] -= loc.X; m[13] -= loc.Y; m[14] -= loc.Z; return m; }).ToList();
        var data = HismData(meshImp, root + 1, cell.AddExportName("NoCollision"), local, meshMin, meshMax, seed);
        int e = cell.AddExport(Convert.FromHexString(HismEntry), name, cls, 0, cdo, actor + 1, 0, data,
                               [], [meshImp, root + 1], [cls, cdo], [actor + 1]);

        // the owner's InstanceComponents (slot 78) and its create-before-serialization dependencies
        var ad = cell.GetExportData(actor);
        int p = 0;
        var slots = Unv.ReadHeader(ad, ref p);
        int at = -1;
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            if (s.Index == 78) { at = p; break; }
            p += s.Index switch { 0 or 58 => 4, _ => throw new NotSupportedException($"{cell.ExportName(actor)}: slot {s.Index} before InstanceComponents") };
        }
        if (at < 0) throw new InvalidDataException($"{cell.ExportName(actor)} has no InstanceComponents");
        int count = BinaryPrimitives.ReadInt32LittleEndian(ad.AsSpan(at));
        var nd = new byte[ad.Length + 4];
        ad.AsSpan(0, at + 4 + 4 * count).CopyTo(nd);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at), count + 1);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at + 4 + 4 * count), e + 1);
        ad.AsSpan(at + 4 + 4 * count).CopyTo(nd.AsSpan(at + 8 + 4 * count));
        cell.SetExportData(actor, nd);
        List<int> Of(string k) => deps.Where(x => x.kind == k).Select(x => x.index).ToList();
        cell.SetExportDeps(actor, Of("SBS"), Of("CBS").Append(e + 1), Of("SBC"), Of("CBC"));

        var (mn, mx, _, _) = InstanceBounds(local, meshMin, meshMax, Vector3.Zero);
        return new JsonObject
        {
            ["component"] = name, ["export"] = e, ["owner"] = cell.ExportName(actor), ["attach_parent"] = cell.ExportName(root),
            ["root_location_cm"] = $"{loc.X:0.###},{loc.Y:0.###},{loc.Z:0.###}", ["instances"] = placed.Count, ["static_mesh"] = meshPackage,
            ["local_bounds_cm"] = $"({mn.X:0},{mn.Y:0},{mn.Z:0})..({mx.X:0},{mx.Y:0},{mx.Z:0})", ["export_bytes"] = data.Length,
            ["instance_components"] = $"{count} -> {count + 1}",
        };
    }

    // ---- build ---------------------------------------------------------------------------------------------------------

    /// materialOf: (template, glb material, triangles) -> the tree material's key and MIC package (null: not drawn).
    /// billboardOf: template -> its DR2 billboard's masked MIC package and number of views (null: none).
    public static JsonObject Build(LegacyPackage cell, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Visuals.Options opt, Dr2Materials dr2,
                                   Func<Splines.V3, bool> inPhysicsBounds, Func<string, string, int, (string key, string mic)?> materialOf,
                                   Func<string, (string mic, int frames)?> billboardOf)
    {
        string template = Cfg(cfg, "template");
        string tplPackage = Cfg(cfg, "mesh_template");
        string legacy = opt.InstancingLegacy ?? dr2.Legacy;
        var r = new JsonObject { ["template"] = template };
        var mesh = LoadTemplate(Path.Combine(ir.Dir, "render", "objects.glb"), template, materialOf);
        string meshPackage = $"{dr2.PackageRoot}/Mesh/SM_{Regex.Replace(template, "[^A-Za-z0-9_]", "_")}";
        var (mn, mx) = mesh.Geometry.Box();
        var lods = new List<StaticMeshPackage.Geometry> { mesh.Geometry };
        var mats = mesh.Materials.ToList();
        var screen = new List<float> { 1.0f };
        string billboard = "none";
        if (cfg["instancing"].TryGetValue("billboard", out var bbv) && bbv is double bbd && bbd != 0)
        {
            if (billboardOf(template) is { } bb)
            {
                lods.Add(Billboard(mn, mx, cfg.Num("objects", "cards_max_radius_m") * 100, bb.frames, mats.Count, out var dens));
                mats.Add(new StaticMeshPackage.Material(bb.mic, "billboard", dens));
                screen.Add((float)cfg.Num("instancing", "billboard_screen_size"));
                billboard = $"{bb.mic} ({bb.frames} views), from screen size {screen[1]}";
            }
            else billboard = "template has no usable DR2 billboard texture";
        }
        var (objPath, rep) = StaticMeshPackage.Write(legacy, tplPackage, meshPackage, lods, mats, screen, dr2.OutRoot);
        r["mesh"] = new JsonObject
        {
            ["package"] = meshPackage, ["object"] = objPath, ["from"] = tplPackage, ["summary"] = rep,
            ["vertices"] = mesh.Geometry.Pos.Count, ["triangles"] = mesh.Geometry.Sections.Sum(s => s.Idx.Count / 3), ["skipped_triangles"] = mesh.Skipped,
            ["lods"] = new JsonArray(lods.Select(l => (JsonNode)$"{l.Pos.Count},{l.Sections.Sum(s => s.Idx.Count / 3)}").ToArray()),
            ["materials"] = new JsonArray(mats.Select(m => (JsonNode)m.Mic).ToArray()),
            ["billboard"] = billboard, ["screen_size"] = new JsonArray(screen.Select(s => (JsonNode)s).ToArray()),
            ["sections"] = new JsonArray(mesh.Geometry.Sections.Select((s, i) => (JsonNode)$"{mesh.Materials[s.Material].Slot}: {s.Idx.Count / 3} triangles, {mesh.Materials[s.Material].Mic}").ToArray()),
            ["bounds_cm"] = new JsonArray(mn.X, mn.Y, mn.Z, mx.X, mx.Y, mx.Z),
        };
        var placed = Select(ir, pl, template, cfg.Num("instancing", "near_m"), (int)cfg.Num("instancing", "max_instances"), inPhysicsBounds);
        if (placed.Count == 0) throw new InvalidDataException($"no instance of {template} within {cfg.Num("instancing", "near_m")} m of the route");
        r["selected"] = new JsonObject
        {
            ["instances"] = placed.Count, ["near_m"] = cfg.Num("instancing", "near_m"), ["farthest_route_m"] = Math.Round(placed.Max(p => p.RouteM), 1),
            ["first"] = $"{placed[0].Id} at UE ({placed[0].World.X:0.#}, {placed[0].World.Y:0.#}, {placed[0].World.Z:0.#})",
        };
        r["hism"] = AddHism(cell, meshPackage, "HISM_" + Regex.Replace(template, "[^A-Za-z0-9_]", "_"), placed, mn, mx, 7919);
        Console.WriteLine($"instancing: {template}: {rep}; {placed.Count} instances within {cfg.Num("instancing", "near_m")} m of the route");
        return r;
    }

    // ---- checks ---------------------------------------------------------------------------------------------------------

    /// Offline check of the mesh package and the HISM in a built (or round-tripped) cell; `root` is the legacy tree that holds both.
    public static int Check(LegacyPackage cell, string root, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, JsonNode report, Func<Splines.V3, bool> inPhysicsBounds, TextWriter w)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{Advisory.Verdict(ok, what, out var bad)} {what}"); if (bad) failures++; }
        var mesh = report["mesh"]!;
        string pkgPath = mesh["package"]!.GetValue<string>();
        string File(string pkgName) => Path.Combine(root, "acr", "Content", pkgName["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");

        // -- the StaticMesh package
        var sp = new LegacyPackage(File(pkgPath));
        C(sp.ExportCount == 2 && sp.ExportClass(1) == "StaticMesh" && sp.ExportClass(0) == "BodySetup" && sp.PackageName == pkgPath,
          $"instancing mesh: {pkgPath} has a BodySetup and a StaticMesh export, package name matches");
        var sd = sp.GetExportData(1);
        var info = StaticMeshPackage.Read(sd);
        var lod = info.Lods[0];
        int wantV = mesh["vertices"]!.GetValue<int>(), wantT = mesh["triangles"]!.GetValue<int>();
        var idx = StaticMeshPackage.Indices(sd, lod);
        var wantLods = mesh["lods"]!.AsArray().Select(x => x!.GetValue<string>().Split(',').Select(int.Parse).ToArray()).ToList();
        C(info.Lods.Count == wantLods.Count && info.NumInlinedLods == wantLods.Count && lod.Vertices == wantV && lod.Sections.Sum(s => s.Triangles) == wantT && idx.Length == wantT * 3
          && info.Lods.Select((l, i) => l.Vertices == wantLods[i][0] && l.Sections.Sum(s => s.Triangles) == wantLods[i][1] && StaticMeshPackage.Indices(sd, l).All(v => v < l.Vertices)).All(b => b),
          $"instancing mesh re-read: {info.Lods.Count} LOD(s) (expected {wantLods.Count}), LOD0 {lod.Vertices} vertices (expected {wantV}), {lod.Sections.Sum(s => s.Triangles)} triangles (expected {wantT}) in {lod.Sections.Count} sections; " +
          $"further LODs {string.Join(", ", info.Lods.Skip(1).Select(l => $"{l.Vertices} vertices / {l.Sections.Sum(s => s.Triangles)} triangles"))}; screen sizes {string.Join(", ", info.ScreenSize.Take(info.Lods.Count).Select(f => f.ToString("0.###")))}");
        var pos = StaticMeshPackage.Positions(sd, lod);
        bool sectionsOk = true;
        foreach (var s in lod.Sections)
        {
            var span = idx.Skip(s.FirstIndex).Take(s.Triangles * 3).ToList();
            sectionsOk &= span.Count == s.Triangles * 3 && span.Min() == (uint)s.MinVertex && span.Max() == (uint)s.MaxVertex && s.Material < info.Materials;
        }
        C(sectionsOk && idx.All(i => i < lod.Vertices) && lod.Sizes[1] == lod.IbBytes, "instancing mesh: section index ranges, vertex ranges and material slots are consistent");
        var (mn, mx) = ProcMesh.Bounds(pos);
        var eb = info.ExtendedBounds;
        double TolBox(double a, double b) => Math.Abs(a - b);
        C(TolBox(eb[0], (mn.X + mx.X) / 2) < 0.01 && TolBox(eb[1], (mn.Y + mx.Y) / 2) < 0.01 && TolBox(eb[2], (mn.Z + mx.Z) / 2) < 0.01
          && TolBox(eb[3], (mx.X - mn.X) / 2) < 0.01 && TolBox(eb[4], (mx.Y - mn.Y) / 2) < 0.01 && TolBox(eb[5], (mx.Z - mn.Z) / 2) < 0.01
          && TolBox(info.TailBounds[0], eb[0]) < 0.01 && TolBox(info.TailBounds[6], eb[6]) < 0.01,
          $"instancing mesh bounds: ExtendedBounds and RenderData bounds equal the vertex box ({mn.X:0},{mn.Y:0},{mn.Z:0})..({mx.X:0},{mx.Y:0},{mx.Z:0}) cm");
        // the DR2 mesh itself: the vertex box must be the template's accepted primitives' box in UE local cm
        var tplName = report["template"]!.GetValue<string>();
        var glbMin = new Vector3(float.MaxValue); var glbMax = new Vector3(float.MinValue);
        var tris = 0;
        var accepted = mesh["sections"]!.AsArray().Count;
        foreach (var p in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().Where(p => p.Mesh == tplName))
        {
            for (int i = 0; i + 2 < p.Positions.Length; i += 3) { var q = ToUeLocal(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]); glbMin = Vector3.Min(glbMin, q); glbMax = Vector3.Max(glbMax, q); }
            tris += p.Indices.Length / 3;
        }
        C(tris == wantT + mesh["skipped_triangles"]!.GetValue<int>() && mn.X >= glbMin.X - 0.01 && mn.Y >= glbMin.Y - 0.01 && mn.Z >= glbMin.Z - 0.01 && mx.X <= glbMax.X + 0.01 && mx.Y <= glbMax.Y + 0.01 && mx.Z <= glbMax.Z + 0.01,
          $"instancing mesh vs DR2 {tplName}: {tris} triangles in objects.glb = {wantT} drawn + {mesh["skipped_triangles"]} skipped (billboard); box inside the glb's ({glbMin.X:0},{glbMin.Y:0},{glbMin.Z:0})..({glbMax.X:0},{glbMax.Y:0},{glbMax.Z:0})");
        var mics = Enumerable.Range(0, info.Materials).Select(i => info.Mats[i].import).Select(imp => imp < 0 ? sp.ImportOuter(-imp - 1) : 0).Select(o => o < 0 ? sp.ImportName(-o - 1) : "").ToList();
        C(mics.Count == info.Materials && mics.All(m => m.StartsWith("/Game/AcrXs/") && System.IO.File.Exists(File(m))),
          $"instancing mesh materials: {string.Join(", ", mics.Select(m => m[(m.LastIndexOf('/') + 1)..]))} are written MIC packages");
        var bs = sp.GetExportData(0);
        C(BinaryPrimitives.ReadInt32LittleEndian(bs.AsSpan(8)) == 0 && bs.Length < 159, "instancing mesh: BodySetup has no collision shapes (AggGeom.SphylElems empty)");
        var dr = sp.DataResources();
        C(BinaryPrimitives.ReadInt32LittleEndian(dr.AsSpan(4)) == 2 && BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + 4)) + 28 == bs.Length
          && BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + LegacyPackage.DataResourceSize + 4)) == info.TailAt + 11,
          "instancing mesh: data resource table points at the BodySetup blob and the empty Nanite pages");

        // -- the component in the cell
        var hism = report["hism"]!;
        int e = Enumerable.Range(0, cell.ExportCount).Single(x => cell.ExportName(x) == hism["component"]!.GetValue<string>());
        C(cell.ExportClass(e) == "HierarchicalInstancedStaticMeshComponent", $"instancing HISM: {cell.ExportName(e)} is a HierarchicalInstancedStaticMeshComponent");
        int owner = cell.ExportOuter(e) - 1;
        var ad = cell.GetExportData(owner);
        int ap = 0; var aslots = Unv.ReadHeader(ad, ref ap);
        foreach (var s in aslots) { if (s.Index == 78) break; if (!s.Zero) ap += 4; }
        int ic = BinaryPrimitives.ReadInt32LittleEndian(ad.AsSpan(ap));
        bool listed = Enumerable.Range(0, ic).Any(k => BinaryPrimitives.ReadInt32LittleEndian(ad.AsSpan(ap + 4 + 4 * k)) == e + 1);
        C(cell.ExportName(owner).StartsWith("BC_ReplayCamerasActor_C") && listed && cell.ExportDeps(owner).Any(d => d.kind == "CBS" && d.index == e + 1),
          $"instancing HISM: owner {cell.ExportName(owner)} lists it in InstanceComponents ({ic}) and depends on it");

        var d = cell.GetExportData(e);
        int p0 = 0;
        var slots = Unv.ReadHeader(d, ref p0);
        int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p0)); p0 += 4; return v; }
        int n = -1, nBuilt = -1, toRender = -1, meshRef = 0, attach = 0;
        int[] sorted = [], reorder = [];
        double[] box = [];
        foreach (var s in slots)
        {
            if (s.Zero) continue;
            switch (s.Index)
            {
                case 2: n = I32(); sorted = Enumerable.Range(0, n).Select(_ => I32()).ToArray(); break;
                case 3: nBuilt = I32(); break;
                case 4: box = Enumerable.Range(0, 6).Select(k => BitConverter.ToDouble(d, p0 + k * 8)).ToArray(); p0 += 49; break;
                case 9: p0 += 58; break;
                case 10: toRender = I32(); break;
                case 15: p0 += 4; break;
                case 24: p0 += 1; break;
                case 26: { int c = I32(); reorder = Enumerable.Range(0, c).Select(_ => I32()).ToArray(); break; }
                case 36: meshRef = I32(); break;
                case 174: { var h = Unv.ReadHeader(d, ref p0); foreach (var t in h) if (!t.Zero) p0 += 8; break; }
                case 191: attach = I32(); break;
                case 231: p0 += 1; break;
                default: throw new NotSupportedException($"HISM slot {s.Index}");
            }
        }
        C(slots.All(s => s.Index is not (29 or 30 or 31)), "instancing HISM: CachedBounds[0..2] are dropped");
        C(!slots.Any(s => s.Index == 36 && s.Zero) && meshRef < 0 && cell.ImportName(-meshRef - 1) == pkgPath[(pkgPath.LastIndexOf('/') + 1)..] && cell.ImportName(-cell.ImportOuter(-meshRef - 1) - 1) == pkgPath,
          $"instancing HISM: StaticMesh import resolves to {pkgPath}");
        C(attach > 0 && cell.ExportName(attach - 1) == "DefaultSceneRoot" && cell.ExportOuter(attach - 1) == owner + 1,
          "instancing HISM: attached to the owner's DefaultSceneRoot");
        // native tail
        p0 += 28;
        int m128 = I32(), nm = I32();
        var mats = new List<double[]>();
        for (int i = 0; i < nm; i++) { mats.Add(Enumerable.Range(0, 16).Select(k => BitConverter.ToDouble(d, p0 + k * 8)).ToArray()); p0 += 128; }
        int t4 = I32(), t0 = I32(), t00 = I32(), t64 = I32(), nodes = I32();
        var leaf = new { first = BitConverter.ToInt32(d, p0 + 12), last = BitConverter.ToInt32(d, p0 + 28), fi = BitConverter.ToInt32(d, p0 + 32), li = BitConverter.ToInt32(d, p0 + 36) };
        p0 += 64 * nodes;
        int want = hism["instances"]!.GetValue<int>();
        C(m128 == 128 && t4 == 4 && t0 == 0 && t00 == 0 && t64 == 64 && p0 == d.Length && nodes == 1,
          $"instancing HISM: native tail parses to the end of the export ({d.Length} bytes): {nm} matrices, {nodes} cluster node");
        C(n == want && nBuilt == want && toRender == want && nm == want && reorder.Length == want
          && sorted.Select((v, i) => v == i).All(b => b) && reorder.Select((v, i) => v == i).All(b => b)
          && leaf.first == -1 && leaf.last == -1 && leaf.fi == 0 && leaf.li == want - 1,
          $"instancing HISM: {nm} instances = SortedInstances {n} = NumBuiltInstances {nBuilt} = InstanceCountToRender {toRender}; identity tables; one leaf over 0..{want - 1}");
        // world positions against DR2: component-local + root location = ToUe(instance origin)
        int sceneRoot = attach - 1;
        var rd = cell.GetExportData(sceneRoot); int rp = 0; var rs = Unv.ReadHeader(rd, ref rp);
        var loc = new Vector3((float)BitConverter.ToDouble(rd, rp), (float)BitConverter.ToDouble(rd, rp + 8), (float)BitConverter.ToDouble(rd, rp + 16));
        var expected = Select(ir, pl, tplName, cfg.Num("instancing", "near_m"), (int)cfg.Num("instancing", "max_instances"), inPhysicsBounds);
        double worst = 0; int mism = 0; double worstScale = 0;
        for (int i = 0; i < Math.Min(nm, expected.Count); i++)
        {
            var m = mats[i]; var x = expected[i];
            double dx = m[12] + loc.X - x.World.X, dy = m[13] + loc.Y - x.World.Y, dz = m[14] + loc.Z - x.World.Z;
            worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            for (int k = 0; k < 12; k++) if (k % 4 != 3) worstScale = Math.Max(worstScale, Math.Abs(m[k] - x.Matrix[k]));
            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1) mism++;
        }
        C(expected.Count == nm && worst < 0.05 && worstScale < 1e-6 && mism == 0,
          $"instancing HISM world positions: {nm} instances, component-local + root ({loc.X:0.##}, {loc.Y:0.##}, {loc.Z:0.##}) equals ToUe(DR2 origin) within {worst:0.####} cm; axes/scale equal DR2's within {worstScale:0.#e+0}");
        // independent of InstanceMatrix: the mesh's vertices through the stored matrices (row-vector convention) against objects.glb
        // vertices through DR2's own matrix and the stage placement (ToUe); every written vertex must sit on a DR2 vertex
        {
            var stageInst = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!["instances"]!.AsArray()
                .Where(x => x!["template"]!.GetValue<string>() == tplName).ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!);
            var dr2Pts = new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().Where(g => g.Mesh == tplName)
                .SelectMany(g => Enumerable.Range(0, g.Positions.Length / 3).Select(k => new Vector3(g.Positions[k * 3], g.Positions[k * 3 + 1], g.Positions[k * 3 + 2]))).ToList();
            double worstV = 0; int tested = 0;
            foreach (int i in new[] { 0, nm / 3, nm / 2, nm - 1 }.Distinct())
            {
                var mm = ObjectColliders.Trs(stageInst[expected[i].Id]["transform"]);
                var refPts = dr2Pts.Select(q => { var t0 = Vector3.Transform(q, mm); var u = pl.ToUe([t0.X, t0.Y, t0.Z]); return (u.X, u.Y, u.Z); }).ToList();
                var m = mats[i];
                foreach (var q in pos.Where((_, k) => k % 7 == 0))
                {
                    double x = q.X * m[0] + q.Y * m[4] + q.Z * m[8] + m[12] + loc.X, y = q.X * m[1] + q.Y * m[5] + q.Z * m[9] + m[13] + loc.Y, z = q.X * m[2] + q.Y * m[6] + q.Z * m[10] + m[14] + loc.Z;
                    double best = double.MaxValue;
                    foreach (var r in refPts) best = Math.Min(best, Math.Sqrt((r.Item1 - x) * (r.Item1 - x) + (r.Item2 - y) * (r.Item2 - y) + (r.Item3 - z) * (r.Item3 - z)));
                    worstV = Math.Max(worstV, best);
                }
                tested++;
            }
            C(worstV < 0.1, $"instancing HISM vs DR2 geometry: mesh vertices (every 7th) of {tested} instances through the stored matrices land on objects.glb vertices through DR2's transform and the placement, worst {worstV:0.####} cm");
        }
        // sanity of the DR2 placement itself: inside the physics box, near the route, proper rotations
        double det = mats.Min(m => m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]));
        var pts = mats.Select(m => new Splines.V3(m[12] + loc.X, m[13] + loc.Y, m[14] + loc.Z)).ToList();
        C(det > 0 && pts.All(q => inPhysicsBounds(new Splines.V3(q.X / 100, q.Z / 100, q.Y / 100))) && expected.All(q => q.RouteM <= cfg.Num("instancing", "near_m")),
          $"instancing HISM: all {nm} instances inside the physics bounds and within {cfg.Num("instancing", "near_m")} m of the route; smallest 3x3 determinant {det:0.#} (> 0: no mirror)");
        // the component bounds contain every instance
        bool boxOk = pts.All(q => q.X - loc.X >= box[0] - 1 && q.X - loc.X <= box[3] + 1 && q.Y - loc.Y >= box[1] - 1 && q.Y - loc.Y <= box[4] + 1 && q.Z - loc.Z >= box[2] - 1 && q.Z - loc.Z <= box[5] + 1);
        C(boxOk, "instancing HISM: BuiltInstanceBounds contain every instance origin");
        return failures;
    }
}
