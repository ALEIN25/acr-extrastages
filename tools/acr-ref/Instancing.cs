using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrRef;

/// Instanced DR2 trees (docs/instancing-plan.md), for the DR2 stages named by [instancing] stages in config/visuals.toml:
///   part 1  every DR2 tree template of the stage (objects.glb; solid tree colliders) as a cooked StaticMesh package, StaticMeshPackage,
///           with the DR2-derived MICs, shared by both gameplay cells;
///   part 2  all its placements as plain HierarchicalInstancedStaticMeshComponents (NoCollision, one leaf cluster each, identity tables),
///           one per template per chunk_m cell, added to each gameplay cell with LegacyPackage.AddExport.
/// The components are attached to the DefaultSceneRoot of BC_ReplayCamerasActor_C (the one gameplay-cell actor that already
/// serialises InstanceComponents); the root has a location and nothing else, so instance matrices are component-local =
/// DR2 world position (the same UE cm as every other position in the cell) minus that location.
/// VisualObjects.Build draws nothing for these templates (no card, trunk stub or merged mesh) and the invisible-obstacle loop counts the
/// instances' real triangles as drawn (AddDrawn); the DR2 colliders are untouched.
internal static class Instancing
{
    /// On for the DR2 stages listed in `stages` only; 0 = off everywhere.
    public static bool Enabled(Toml cfg, string stage) => cfg["instancing"].TryGetValue("enabled", out var v) && v is double d && d != 0
        && cfg["instancing"].TryGetValue("stages", out var s) && s is object[] list && list.Any(x => x as string == stage);

    const string ReplayActor = "BC_ReplayCamerasActor_C";
    /// [instancing] owner: the class of the gameplay-cell actor that owns the components (default: the replay-camera actor). The components
    /// stay attached to the replay actor's DefaultSceneRoot either way; only the replay actor lists them in InstanceComponents.
    internal static string OwnerClass(Toml cfg) => cfg["instancing"].TryGetValue("owner", out var o) && o is string s && s.Length > 0 ? s : ReplayActor;

    static string Clean(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    // 96-byte export entry of HISM_SM_LarchWales01_4 in a Hafren South PCG cell (class and template are imports of that package,
    // outer an export of it; AddExport patches them, the name and the flags)
    const string HismEntry = "DEFFFFFF00000000B1FFFFFF200100006400000005000000000000007204000000000000B6256300000000000000000000000000010000000000000000000000000000000000000000000000D303000000000000020000000200000001000000";

    // ---- which templates ----------------------------------------------------------------------------------------------------

    static bool IsTreeClass(string? c) => c is not null && (c.StartsWith("sSolidTree") || c.StartsWith("sSolidPalm"));

    /// The templates of stage.json "objects" that are trees: a solid tree collider (shape class sSolidTree*, sSolidPalm*). Templates
    /// with a wood collider that VisualObjects treats as trees (acr wall PM_WallWoodTreeTrunks) but that are logs, fences and stumps are
    /// returned as `notTrees` with the reason.
    internal static (SortedSet<string> trees, SortedDictionary<string, string> notTrees) Classify(JsonNode templates)
    {
        var trees = new SortedSet<string>(StringComparer.Ordinal); var not = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, t) in templates.AsObject())
        {
            var shapes = (t!["bodies"]?.AsArray() ?? []).SelectMany(b => b!["shapes"]!.AsArray()).ToList();
            if (shapes.Any(s => IsTreeClass(s!["shape_class"]?.GetValue<string>()))) trees.Add(name);
            else if (shapes.Any(s => s!["acr_wall"]?.GetValue<string>() == "PM_WallWoodTreeTrunks"))
                not[name] = $"wood collider class {string.Join("+", shapes.Select(s => s!["shape_class"]?.GetValue<string>()).Distinct())} (log, fence or stump), not a tree";
        }
        return (trees, not);
    }

    // ---- part 1: the mesh -------------------------------------------------------------------------------------------------

    /// DR2 mesh-local (metres, Y up) to mesh-local UE (cm, Z up): the same swap as Placement.ToUe, so it mirrors.
    static Vector3 ToUeLocal(float x, float y, float z) => new(x * 100, z * 100, y * 100);

    /// Keys: the section key of each section (the material key of materialOf), in section order.
    internal sealed record TemplateMesh(StaticMeshPackage.Geometry Geometry, List<StaticMeshPackage.Material> Materials, int[] TrianglesPerMaterial, int Skipped, List<string> Keys);

    /// The template's primitives whose material is a tree material (materialKey returns its section key and MIC path; null skips
    /// the primitive, e.g. the billboard), one section per material, vertices converted to mesh-local UE cm.
    public static TemplateMesh LoadTemplate(IEnumerable<Glb.Primitive> prims, string template, Func<string, string, int, (string key, string mic)?> materialOf,
        Func<string, string, int, bool>? isTrunk = null, bool spherical = false)
    {
        var g = new StaticMeshPackage.Geometry();
        var order = new List<string>();
        var sections = new Dictionary<string, List<uint>>();
        var mics = new Dictionary<string, string>();
        var foliage = new List<int>();
        var area3 = new Dictionary<string, double>(); var areaUv = new Dictionary<string, double>();
        int skipped = 0, found = 0;
        foreach (var p in prims)
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
            if (spherical && isTrunk is not null && !isTrunk(template, p.Material, p.Indices.Length / 3))
                foliage.AddRange(p.Indices.Distinct().Select(i => b + (int)i));
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
        if (found == 0) throw new InvalidDataException($"no mesh named {template} in objects.glb");
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
        if (FoliageLighting.Centre(foliage.Select(i => g.Pos[i])) is { } centre)
            foreach (var i in foliage) g.Nrm[i] = FoliageLighting.Normal(g.Pos[i], centre, g.Nrm[i]);
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
        return new TemplateMesh(g, mats, g.Sections.Select(s => s.Idx.Count / 3).ToArray(), skipped, order);
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

    // ---- plan: what is instanced ---------------------------------------------------------------------------------------------

    internal sealed record Placed(string Id, Splines.V3 World, double[] Matrix);

    /// One instanced template: its mesh (LOD0 geometry kept for the invisible-obstacle loop) and all its placements.
    internal sealed class Species
    {
        public string Template = "", MeshPackage = "";
        public StaticMeshPackage.Geometry Lod0 = new();
        public Vector3 Min, Max;
        public List<int> TrunkSections = new();   // indices into Lod0.Sections of the foliage_trunk material(s)
        public List<Placed> Placed = new();
        public JsonObject Report = new();
    }

    /// The instanced templates of a stage and the report of the choice (meshes, fallbacks, exclusions). Built once per stage-build:
    /// both gameplay cells get the same plan (mesh packages are shared).
    internal sealed class Plan
    {
        public readonly SortedDictionary<string, Species> Species = new(StringComparer.Ordinal);
        public JsonObject Report = new();
        public double ChunkM;
        public string Owner = ReplayActor;   // [instancing] owner: the class of the actor that owns (registers) the components
        public bool Fitted;   // ColliderFit.FitAll has run on this stage-build's colliders
        public HashSet<string> Templates => Species.Keys.ToHashSet();
        /// Instance id -> the tree's base height (UE cm), for the colliders of instanced trees.
        public Dictionary<string, double> BaseZ => Species.Values.SelectMany(s => s.Placed).ToDictionary(p => p.Id, p => p.World.Z);
    }

    /// Every instance of the wanted templates in stage.json inside the physics bounds, in file order per template.
    public static Dictionary<string, List<Placed>> PlaceAll(StageBuild.Ir ir, StageBuild.Placement pl, ICollection<string> wanted, Func<Splines.V3, bool> inPhysicsBounds)
    {
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var res = wanted.ToDictionary(t => t, _ => new List<Placed>());
        foreach (var inst in stage["instances"]!.AsArray())
        {
            if (!res.TryGetValue(inst!["template"]!.GetValue<string>(), out var list)) continue;
            var m = ObjectColliders.Trs(inst["transform"]);
            var o = Vector3.Transform(Vector3.Zero, m);
            if (!inPhysicsBounds(pl.ToPhysics(o.X, o.Y, o.Z))) continue;
            var mat = InstanceMatrix(m, pl, out var world);
            list.Add(new Placed(inst["id"]!.GetValue<string>(), world, mat));
        }
        return res;
    }

    /// The placements of one species by chunk: (cell x, cell y) of the instance origin in chunk_m squares of UE world space, ordered.
    internal static List<((int x, int y) key, List<Placed> items)> Chunks(IEnumerable<Placed> placed, double chunkM)
    {
        double c = chunkM * 100;
        return placed.GroupBy(p => ((int)Math.Floor(p.World.X / c), (int)Math.Floor(p.World.Y / c)))
            .OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2).Select(g => (g.Key, g.ToList())).ToList();
    }

    internal static string ChunkName(string template, (int x, int y) k) => $"HISM_{Clean(template)}_{(k.x < 0 ? "m" : "p")}{Math.Abs(k.x)}_{(k.y < 0 ? "m" : "p")}{Math.Abs(k.y)}";

    /// Decides which templates are instanced and writes their StaticMesh packages (once per stage-build).
    /// materialOf: (template, glb material, triangles) -> the tree material's key and MIC package (null: not drawn).
    /// isTrunk: the same arguments -> whether that material is the template's foliage_trunk one with a usable DR2 diffuse.
    /// billboardOf: template -> its DR2 billboard's masked MIC package and number of views (null: none).
    public static Plan Prepare(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, Visuals.Options opt, Dr2Materials dr2, Func<Splines.V3, bool> inPhysicsBounds,
                               Func<string, string, int, (string key, string mic)?> materialOf, Func<string, string, int, bool> isTrunk,
                               Func<string, (string mic, int frames)?> billboardOf)
    {
        var plan = new Plan { ChunkM = cfg.Num("instancing", "chunk_m"), Owner = OwnerClass(cfg) };
        string tplPackage = cfg.Str("instancing", "mesh_template");
        string legacy = opt.InstancingLegacy ?? dr2.Legacy;
        var stageObjs = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var counts = stageObjs["instances"]!.AsArray().GroupBy(i => i!["template"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Count());
        var (trees, notTrees) = Classify(stageObjs["templates"]!);
        var placed = PlaceAll(ir, pl, trees, inPhysicsBounds);
        var glb = new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives(withUv: true).Where(p => trees.Contains(p.Mesh)).GroupBy(p => p.Mesh).ToDictionary(g => g.Key, g => g.ToList());
        var fallbacks = new JsonObject(); var speciesRep = new JsonObject();
        int noPlacement = 0, fallbackPlacements = 0;
        foreach (var template in trees)
        {
            var list = placed[template];
            if (list.Count == 0) { noPlacement++; continue; }
            void Fallback(string why) { fallbacks[template] = $"{list.Count} placements keep their cards and trunk stubs: {why}"; fallbackPlacements += list.Count; }
            if (!glb.TryGetValue(template, out var prims)) { Fallback("no render mesh in objects.glb"); continue; }
            if (!prims.Any(p => isTrunk(template, p.Material, p.Indices.Length / 3))) { Fallback("no usable DR2 trunk material (foliage_trunk with an exported diffuse)"); continue; }
            TemplateMesh mesh;
            try { mesh = LoadTemplate(prims, template, materialOf, isTrunk, FoliageLighting.Spherical(cfg)); }
            catch (InvalidDataException e) { Fallback(e.Message); continue; }
            if (mesh.Geometry.Pos.Count > 65535) { Fallback($"{mesh.Geometry.Pos.Count} vertices (the mesh writer uses 16-bit indices)"); continue; }
            // the sections of the foliage_trunk material(s): the real trunk the invisible-obstacle loop credits to the template's colliders
            var trunkKeys = prims.Where(p => isTrunk(template, p.Material, p.Indices.Length / 3)).Select(p => materialOf(template, p.Material, p.Indices.Length / 3)?.key).Where(k => k is not null).ToHashSet();
            var trunkSections = Enumerable.Range(0, mesh.Keys.Count).Where(k => trunkKeys.Contains(mesh.Keys[k])).ToList();
            if (trunkSections.Count == 0) { Fallback("the trunk material has no drawable DR2 diffuse (no trunk section in the mesh)"); continue; }
            string meshPackage = $"{dr2.PackageRoot}/Mesh/SM_{Clean(template)}";
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
            var sp = new Species { Template = template, MeshPackage = meshPackage, Lod0 = mesh.Geometry, Min = mn, Max = mx, Placed = list };
            sp.TrunkSections.AddRange(trunkSections);
            sp.Report = new JsonObject
            {
                ["package"] = meshPackage, ["object"] = objPath, ["from"] = tplPackage, ["summary"] = rep,
                ["vertices"] = mesh.Geometry.Pos.Count, ["triangles"] = mesh.Geometry.Sections.Sum(s => s.Idx.Count / 3), ["skipped_triangles"] = mesh.Skipped,
                ["lods"] = new JsonArray(lods.Select(l => (JsonNode)$"{l.Pos.Count},{l.Sections.Sum(s => s.Idx.Count / 3)}").ToArray()),
                ["materials"] = new JsonArray(mats.Select(m => (JsonNode)m.Mic).ToArray()),
                ["billboard"] = billboard, ["screen_size"] = new JsonArray(screen.Select(s => (JsonNode)s).ToArray()),
                ["sections"] = new JsonArray(mesh.Geometry.Sections.Select(s => (JsonNode)$"{mesh.Materials[s.Material].Slot}: {s.Idx.Count / 3} triangles, {mesh.Materials[s.Material].Mic}").ToArray()),
                ["bounds_cm"] = new JsonArray(mn.X, mn.Y, mn.Z, mx.X, mx.Y, mx.Z),
                ["placements"] = list.Count, ["trunk_sections"] = new JsonArray(sp.TrunkSections.Select(k => (JsonNode)k).ToArray()),
            };
            plan.Species[template] = sp;
            speciesRep[template] = sp.Report;
        }
        // not instanced, with the reason: wood colliders that are not trees, bushes (soft colliders: cards, no trunk to replace)
        var excluded = new JsonObject();
        foreach (var (t, why) in notTrees) excluded[t] = $"{counts.GetValueOrDefault(t)} placements: {why}";
        int bushTemplates = 0, bushPlacements = 0;
        foreach (var (name, t) in stageObjs["templates"]!.AsObject())
        {
            var shapes = (t!["bodies"]?.AsArray() ?? []).SelectMany(b => b!["shapes"]!.AsArray()).ToList();
            if (shapes.Count > 0 && shapes.All(s => s!["acr_wall"]?.GetValue<string>() == "PM_WallBush")) { bushTemplates++; bushPlacements += counts.GetValueOrDefault(name); }
        }
        plan.Report = new JsonObject
        {
            ["chunk_m"] = plan.ChunkM, ["mesh_template"] = tplPackage,
            ["tree_templates"] = trees.Count, ["instanced_templates"] = plan.Species.Count, ["templates_without_placements"] = noPlacement,
            ["instanced_placements"] = plan.Species.Values.Sum(s => s.Placed.Count), ["fallback_placements"] = fallbackPlacements,
            ["fallbacks"] = fallbacks, ["excluded"] = excluded,
            ["bushes"] = $"{bushTemplates} bush templates ({bushPlacements} placements, soft PM_WallBush colliders) are not instanced: no trunk to replace, they keep their billboard cards",
            ["species"] = speciesRep,
        };
        Console.WriteLine($"instancing: {plan.Species.Count} tree templates as StaticMesh packages ({plan.Report["instanced_placements"]} placements), " +
                          $"{fallbacks.Count} fall back to cards and stubs, {notTrees.Count} wood templates excluded (not trees)");
        foreach (var (t, why) in fallbacks) Console.WriteLine($"instancing: fallback {t}: {why}");
        return plan;
    }

    // ---- part 2: the components ---------------------------------------------------------------------------------------------

    /// Adds the HISMs (one per species per chunk) to the cell, owned by BC_ReplayCamerasActor_C and attached to its DefaultSceneRoot.
    /// Returns the report (the plan's, plus the components).
    public static JsonObject AddComponents(LegacyPackage cell, Plan plan, int seed = 7919)
    {
        int actor = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == ReplayActor);
        int owner = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == plan.Owner);
        var deps = cell.ExportDeps(actor);
        int root = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportOuter(e) == actor + 1 && cell.ExportName(e) == "DefaultSceneRoot");
        var loc = RootLocation(cell, root);
        int exportsBefore = cell.ExportCount;

        // imports: the component class and its default object (once), the mesh packages
        int enginePkg = cell.FindImport("/Script/CoreUObject", "Package", "/Script/Engine", 0);
        if (enginePkg == 0) throw new InvalidDataException("the cell has no /Script/Engine package import");
        int cls = cell.FindImport("/Script/CoreUObject", "Class", "HierarchicalInstancedStaticMeshComponent", enginePkg);
        if (cls == 0) cls = cell.AddImport("/Script/CoreUObject", "Class", enginePkg, "HierarchicalInstancedStaticMeshComponent");
        int cdo = cell.FindImport("/Script/Engine", "HierarchicalInstancedStaticMeshComponent", "Default__HierarchicalInstancedStaticMeshComponent", enginePkg);
        if (cdo == 0) cdo = cell.AddImport("/Script/Engine", "HierarchicalInstancedStaticMeshComponent", enginePkg, "Default__HierarchicalInstancedStaticMeshComponent");
        int noCollision = cell.AddExportName("NoCollision");

        var added = new List<int>();
        var comps = new JsonArray(); var perSpecies = new JsonObject();
        int instances = 0;
        foreach (var sp in plan.Species.Values)
        {
            string obj = sp.MeshPackage[(sp.MeshPackage.LastIndexOf('/') + 1)..];
            int pkgImp = cell.FindImport("/Script/CoreUObject", "Package", sp.MeshPackage, 0);
            if (pkgImp == 0) pkgImp = cell.AddImport("/Script/CoreUObject", "Package", 0, sp.MeshPackage);
            int meshImp = cell.FindImport("/Script/Engine", "StaticMesh", obj, pkgImp);
            if (meshImp == 0) meshImp = cell.AddImport("/Script/Engine", "StaticMesh", pkgImp, obj);
            var chunks = Chunks(sp.Placed, plan.ChunkM);
            foreach (var (key, items) in chunks)
            {
                var local = items.Select(p => { var m = (double[])p.Matrix.Clone(); m[12] -= loc.X; m[13] -= loc.Y; m[14] -= loc.Z; return m; }).ToList();
                var data = HismData(meshImp, root + 1, noCollision, local, sp.Min, sp.Max, seed);
                string name = ChunkName(sp.Template, key);
                int e = cell.AddExport(Convert.FromHexString(HismEntry), name, cls, 0, cdo, owner + 1, 0, data, [], [meshImp, root + 1], [cls, cdo], [owner + 1]);
                added.Add(e);
                comps.Add(new JsonObject { ["component"] = name, ["export"] = e, ["template"] = sp.Template, ["instances"] = items.Count, ["export_bytes"] = data.Length });
                instances += items.Count;
            }
            perSpecies[sp.Template] = $"{sp.Placed.Count} instances in {chunks.Count} components (largest {chunks.Max(c => c.items.Count)})";
        }

        var r = (JsonObject)plan.Report.DeepClone();
        r["components"] = comps; r["component_count"] = comps.Count; r["instances"] = instances; r["per_species"] = perSpecies;
        r["owner"] = cell.ExportName(owner); r["attach_parent"] = cell.ExportName(root);
        r["root_location_cm"] = $"{loc.X:0.###},{loc.Y:0.###},{loc.Z:0.###}";
        r["exports"] = $"{exportsBefore} -> {cell.ExportCount}";
        Console.WriteLine($"instancing: {comps.Count} components, {instances} instances in {cell.ExportName(owner)} ({exportsBefore} -> {cell.ExportCount} exports)");
        if (owner != actor) return r;

        // the replay actor's InstanceComponents (slot 78) and its create-before-serialization dependencies
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
        var nd = new byte[ad.Length + 4 * added.Count];
        ad.AsSpan(0, at + 4 + 4 * count).CopyTo(nd);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at), count + added.Count);
        for (int k = 0; k < added.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at + 4 + 4 * (count + k)), added[k] + 1);
        ad.AsSpan(at + 4 + 4 * count).CopyTo(nd.AsSpan(at + 4 + 4 * (count + added.Count)));
        cell.SetExportData(actor, nd);
        List<int> Of(string k) => deps.Where(x => x.kind == k).Select(x => x.index).ToList();
        cell.SetExportDeps(actor, Of("SBS"), Of("CBS").Concat(added.Select(e => e + 1)).ToList(), Of("SBC"), Of("CBC"));
        r["instance_components"] = $"{count} -> {count + added.Count}";
        return r;
    }

    // ---- drawn surfaces for the invisible-obstacle loop ------------------------------------------------------------------------

    /// Adds the real triangles of instances (world matrices: mesh-local UE cm -> UE cm) as drawn surface to the grid: only those
    /// whose box meets a grid cell are rasterised. This is what makes an instanced trunk count as drawn: its real triangles, not a
    /// stand-in. section: the index the cells report as the drawn top. Returns the number of instances on the grid.
    public static int AddDrawn(InvisibleCheck.Drawn dg, IReadOnlyList<Vector3> pos, IReadOnlyList<uint> trunk, IReadOnlyList<uint> rest, IEnumerable<double[]> world, int section)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in pos) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        var w = new Vector3[pos.Count];
        int used = 0;
        foreach (var m in world)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var x in new[] { mn.X, mx.X }) foreach (var y in new[] { mn.Y, mx.Y }) foreach (var z in new[] { mn.Z, mx.Z })
            {
                double wx = x * m[0] + y * m[4] + z * m[8] + m[12], wy = x * m[1] + y * m[5] + z * m[9] + m[13];
                x0 = Math.Min(x0, wx); x1 = Math.Max(x1, wx); y0 = Math.Min(y0, wy); y1 = Math.Max(y1, wy);
            }
            bool near = false;
            for (int i = dg.G.I(x0); i <= dg.G.I(x1) && !near; i++)
                for (int j = dg.G.I(y0); j <= dg.G.I(y1); j++)
                    if (dg.G.Index.ContainsKey((i, j))) { near = true; break; }
            if (!near) continue;
            used++;
            for (int k = 0; k < w.Length; k++)
            {
                var p = pos[k];
                w[k] = new Vector3((float)(p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12]), (float)(p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13]), (float)(p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14]));
            }
            for (int t = 0; t + 2 < trunk.Count; t += 3) dg.AddTrunk(w[trunk[t]], w[trunk[t + 1]], w[trunk[t + 2]], section);
            for (int t = 0; t + 2 < rest.Count; t += 3) dg.AddSurfaceCover(w[rest[t]], w[rest[t + 1]], w[rest[t + 2]], section);
        }
        return used;
    }

    /// Build time: every species' placements as drawn surface (the matrices are the plan's, world space).
    public static void AddDrawn(InvisibleCheck.Drawn dg, Plan plan, int section)
    {
        int used = 0, total = 0;
        foreach (var sp in plan.Species.Values)
        {
            var trunk = sp.TrunkSections.SelectMany(k => sp.Lod0.Sections[k].Idx).ToList();
            var rest = Enumerable.Range(0, sp.Lod0.Sections.Count).Except(sp.TrunkSections).SelectMany(k => sp.Lod0.Sections[k].Idx).ToList();
            used += AddDrawn(dg, sp.Lod0.Pos, trunk, rest, sp.Placed.Select(p => p.Matrix), section);
            total += sp.Placed.Count;
        }
        Console.WriteLine($"instancing: {used} of {total} instanced trees stand on the invisible-obstacle grid; their real triangles count as drawn");
    }

    /// stage-check: the drawn surface of the instances written to a built cell, read back from the cell's HISM exports and the mesh packages under
    /// `root` (independent of the plan in memory).
    public static (Action<InvisibleCheck.Drawn, int> draw, Func<InvisibleCheck.Shape, double?> baseOf) FromCell(LegacyPackage cell, string root, JsonNode inst)
    {
        // instance origins (UE cm) in 2 m buckets: the tree each tall capsule collider belongs to, for BaseOf
        var loc0 = SceneRootLocation(cell, out _);
        var origins = new Dictionary<(int, int), List<(double x, double y, double z)>>();
        foreach (var h in ReadHisms(cell))
            foreach (var m in h.Mats)
            {
                double x = m[12] + loc0.X, y = m[13] + loc0.Y, z = m[14] + loc0.Z;
                var k = ((int)Math.Floor(x / 200), (int)Math.Floor(y / 200));
                if (!origins.TryGetValue(k, out var l)) origins[k] = l = new();
                l.Add((x, y, z));
            }
        double? BaseOf(InvisibleCheck.Shape sh)
        {
            // a tall near-vertical capsule (a trunk) whose axis, at the height of an instance origin, passes within its radius + 50 cm of that origin and whose lowest point is
            // within 3 m of it: the collider of that tree. Anything else (no real trunk placed there) is not matched and keeps its full shadow in the check.
            if (sh.Box || sh.Length < 400) return null;
            var up = sh.Ay.Z >= 0 ? sh.Ay : sh.Ay * -1.0;
            if (up.Z < 0.85) return null;
            double low = sh.Centre.Z - up.Z * (sh.Length / 2 + sh.Radius), high = sh.Centre.Z + up.Z * (sh.Length / 2 + sh.Radius);
            double? best = null; double bestD = double.MaxValue;
            int ci = (int)Math.Floor(sh.Centre.X / 200), cj = (int)Math.Floor(sh.Centre.Y / 200);
            for (int i = ci - 3; i <= ci + 3; i++)
                for (int j = cj - 3; j <= cj + 3; j++)
                    if (origins.TryGetValue((i, j), out var l))
                        foreach (var o in l)
                        {
                            // DR2 capsules start at (or a little above or below) the tree's base height: the axis is taken at the capsule's own lowest point if that is above the origin
                            if (o.z < low - 150 || o.z > high || low > o.z + 300) continue;
                            double t = (Math.Max(o.z, low) - sh.Centre.Z) / up.Z;
                            double px = sh.Centre.X + up.X * t, py = sh.Centre.Y + up.Y * t;
                            double d = Math.Sqrt((px - o.x) * (px - o.x) + (py - o.y) * (py - o.y));
                            if (d <= sh.Radius + 50 && d < bestD) { bestD = d; best = o.z; }
                        }
            return best;
        }
        return (Draw, BaseOf);

        void Draw(InvisibleCheck.Drawn dg, int section)
        {
            var loc = loc0;
            var geo = new Dictionary<string, (Vector3[] pos, uint[] trunk, uint[] rest)>();
            var trunkOf = ((JsonObject)inst["species"]!).ToDictionary(kv => kv.Value!["package"]!.GetValue<string>(), kv => kv.Value!["trunk_sections"]!.AsArray().Select(x => x!.GetValue<int>()).ToHashSet());
            int used = 0, total = 0;
            foreach (var h in ReadHisms(cell))
            {
                if (!geo.TryGetValue(h.MeshPackage, out var g))
                {
                    var d = new LegacyPackage(PackageFile(root, h.MeshPackage)).GetExportData(1);
                    var info = StaticMeshPackage.Read(d);
                    var all = StaticMeshPackage.Indices(d, info.Lods[0]);
                    var secs = info.Lods[0].Sections;
                    IEnumerable<uint> Of(bool trunk) => Enumerable.Range(0, secs.Count).Where(k => trunkOf[h.MeshPackage].Contains(k) == trunk).SelectMany(k => all.Skip(secs[k].FirstIndex).Take(secs[k].Triangles * 3));
                    g = geo[h.MeshPackage] = (StaticMeshPackage.Positions(d, info.Lods[0]), Of(true).ToArray(), Of(false).ToArray());
                }
                used += AddDrawn(dg, g.pos, g.trunk, g.rest, h.Mats.Select(m => { var q = (double[])m.Clone(); q[12] += loc.X; q[13] += loc.Y; q[14] += loc.Z; return q; }), section);
                total += h.Mats.Count;
            }
            Console.WriteLine($"instancing: {used} of {total} instanced trees stand on the invisible-obstacle grid; their real triangles count as drawn");
        }
    }

    internal static string PackageFile(string root, string pkgName) => Path.Combine(root, "acr", "Content", pkgName["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");

    // ---- reading the components back ---------------------------------------------------------------------------------------------

    /// MeshPackage: the /Game package of the StaticMesh import ("" if unresolved). Problems: invariants of the written bytes that do not hold.
    internal sealed record Hism(string Name, int Export, string MeshPackage, int Owner, int Attach, List<double[]> Mats, List<string> Problems, int Nodes, double[] Box);

    static Vector3 SceneRootLocation(LegacyPackage cell, out int root)
    {
        int actor = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == "BC_ReplayCamerasActor_C");
        root = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportOuter(e) == actor + 1 && cell.ExportName(e) == "DefaultSceneRoot");
        return RootLocation(cell, root);
    }

    /// The instanced components this module added: HierarchicalInstancedStaticMeshComponent exports named HISM_*, parsed from their bytes.
    internal static List<Hism> ReadHisms(LegacyPackage cell)
    {
        var res = new List<Hism>();
        for (int e = 0; e < cell.ExportCount; e++)
            if (cell.ExportClass(e) == "HierarchicalInstancedStaticMeshComponent" && cell.ExportName(e).StartsWith("HISM_")) res.Add(ParseHism(cell, e));
        return res;
    }

    static Hism ParseHism(LegacyPackage cell, int e)
    {
        var problems = new List<string>();
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
        if (slots.Any(s => s.Index is 29 or 30 or 31)) problems.Add("CachedBounds present");
        string pkg = "";
        if (slots.Any(s => s.Index == 36 && s.Zero) || meshRef >= 0) problems.Add("no StaticMesh import");
        else pkg = cell.ImportName(-cell.ImportOuter(-meshRef - 1) - 1);
        p0 += 28;
        int m128 = I32(), nm = I32();
        var mats = new List<double[]>();
        for (int i = 0; i < nm; i++) { mats.Add(Enumerable.Range(0, 16).Select(k => BitConverter.ToDouble(d, p0 + k * 8)).ToArray()); p0 += 128; }
        int t4 = I32(), t0 = I32(), t00 = I32(), t64 = I32(), nodes = I32();
        int first = BitConverter.ToInt32(d, p0 + 12), last = BitConverter.ToInt32(d, p0 + 28), fi = BitConverter.ToInt32(d, p0 + 32), li = BitConverter.ToInt32(d, p0 + 36);
        p0 += 64 * nodes;
        if (!(m128 == 128 && t4 == 4 && t0 == 0 && t00 == 0 && t64 == 64 && p0 == d.Length && nodes == 1)) problems.Add($"native tail does not parse to the end ({p0} of {d.Length})");
        if (!(n == nm && nBuilt == nm && toRender == nm && reorder.Length == nm && sorted.Select((v, i) => v == i).All(b => b) && reorder.Select((v, i) => v == i).All(b => b)
              && first == -1 && last == -1 && fi == 0 && li == nm - 1)) problems.Add($"counts or tables: {n}/{nBuilt}/{toRender}/{nm}, leaf {fi}..{li}");
        return new Hism(cell.ExportName(e), e, pkg, cell.ExportOuter(e) - 1, attach - 1, mats, problems, nodes, box);
    }

    // ---- checks ---------------------------------------------------------------------------------------------------------

    /// Offline check of one gameplay cell's instanced trees (and, with `meshes`, of the shared mesh packages); `root` is the legacy tree that
    /// holds the packages, `inst` the cell's visuals.instancing report, `visuals` its visuals report. Aggregated: one verdict per kind of check.
    public static int Check(LegacyPackage cell, string root, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, JsonNode inst, JsonNode visuals,
                            Func<Splines.V3, bool> inPhysicsBounds, TextWriter w, string label, bool meshes)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{Advisory.Verdict(ok, what, out var bad)} {what}"); if (bad) failures++; }
        var agg = new SortedDictionary<string, (int ok, int bad, string first)>(StringComparer.Ordinal);
        void A(string kind, bool ok, string detail)
        {
            var a = agg.GetValueOrDefault(kind, (0, 0, ""));
            agg[kind] = ok ? (a.Item1 + 1, a.Item2, a.Item3) : (a.Item1, a.Item2 + 1, a.Item3.Length == 0 ? detail : a.Item3);
        }
        void Flush(string what)
        {
            foreach (var (kind, (ok, bad, first)) in agg) C(bad == 0, $"{label} {kind}: {ok + bad} checked, {bad} failed" + (bad > 0 ? $" ({first})" : ""));
            agg.Clear();
        }
        var speciesRep = (JsonObject)inst["species"]!;
        var names = speciesRep.Select(kv => kv.Key).ToList();

        // the templates: tree classification reproduced from stage.json, every tree template either instanced, a reported fallback or without placements
        var stageObjs = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var (trees, notTrees) = Classify(stageObjs["templates"]!);
        var fallbacks = ((JsonObject)inst["fallbacks"]!).Select(kv => kv.Key).ToHashSet();
        var expected = PlaceAll(ir, pl, trees, inPhysicsBounds);
        var accounted = names.Concat(fallbacks).ToHashSet();
        var unaccounted = trees.Where(t => expected[t].Count > 0 && !accounted.Contains(t)).ToList();
        var bad2 = names.Where(t => notTrees.ContainsKey(t) || !trees.Contains(t)).ToList();
        C(unaccounted.Count == 0 && bad2.Count == 0 && fallbacks.All(trees.Contains) && ((JsonObject)inst["excluded"]!).Select(kv => kv.Key).ToHashSet().SetEquals(notTrees.Keys),
          $"{label} templates: {names.Count} tree templates instanced, {fallbacks.Count} fall back to cards and stubs ({string.Join(", ", fallbacks)}), {notTrees.Count} wood templates excluded ({string.Join(", ", notTrees.Keys)}); " +
          $"{trees.Count - names.Count - fallbacks.Count} without placements; {unaccounted.Count} unaccounted for ({string.Join(", ", unaccounted)})");

        string File(string pkgName) => PackageFile(root, pkgName);
        var glbPrims = new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().Where(p => names.Contains(p.Mesh)).GroupBy(p => p.Mesh).ToDictionary(g => g.Key, g => g.ToList());
        var meshPos = new Dictionary<string, Vector3[]>();
        foreach (var tpl in names)
        {
            var m = speciesRep[tpl]!;
            string pkgPath = m["package"]!.GetValue<string>();
            var sp = new LegacyPackage(File(pkgPath));
            var sd = sp.GetExportData(1);
            var info = StaticMeshPackage.Read(sd);
            var lod = info.Lods[0];
            var pos = StaticMeshPackage.Positions(sd, lod);
            meshPos[tpl] = pos;
            if (!meshes) continue;
            A("mesh packages", sp.ExportCount == 2 && sp.ExportClass(1) == "StaticMesh" && sp.ExportClass(0) == "BodySetup" && sp.PackageName == pkgPath, $"{tpl}: exports or package name");
            int wantV = m["vertices"]!.GetValue<int>(), wantT = m["triangles"]!.GetValue<int>();
            var idx = StaticMeshPackage.Indices(sd, lod);
            var wantLods = m["lods"]!.AsArray().Select(x => x!.GetValue<string>().Split(',').Select(int.Parse).ToArray()).ToList();
            A("mesh LODs and counts", info.Lods.Count == wantLods.Count && info.NumInlinedLods == wantLods.Count && lod.Vertices == wantV && lod.Sections.Sum(s => s.Triangles) == wantT && idx.Length == wantT * 3
              && info.Lods.Select((l, i) => l.Vertices == wantLods[i][0] && l.Sections.Sum(s => s.Triangles) == wantLods[i][1] && StaticMeshPackage.Indices(sd, l).All(v => v < l.Vertices)).All(b => b),
              $"{tpl}: {info.Lods.Count} LODs, LOD0 {lod.Vertices} v / {lod.Sections.Sum(s => s.Triangles)} t, expected {wantV} / {wantT}");
            bool sectionsOk = true;
            foreach (var s in lod.Sections)
            {
                var span = idx.Skip(s.FirstIndex).Take(s.Triangles * 3).ToList();
                sectionsOk &= span.Count == s.Triangles * 3 && span.Min() == (uint)s.MinVertex && span.Max() == (uint)s.MaxVertex && s.Material < info.Materials;
            }
            A("mesh section ranges", sectionsOk && idx.All(i => i < lod.Vertices) && lod.Sizes[1] == lod.IbBytes, tpl);
            var (mn, mx) = ProcMesh.Bounds(pos);
            var eb = info.ExtendedBounds;
            A("mesh bounds (ExtendedBounds and RenderData = vertex box)", Math.Abs(eb[0] - (mn.X + mx.X) / 2) < 0.01 && Math.Abs(eb[1] - (mn.Y + mx.Y) / 2) < 0.01 && Math.Abs(eb[2] - (mn.Z + mx.Z) / 2) < 0.01
              && Math.Abs(eb[3] - (mx.X - mn.X) / 2) < 0.01 && Math.Abs(eb[4] - (mx.Y - mn.Y) / 2) < 0.01 && Math.Abs(eb[5] - (mx.Z - mn.Z) / 2) < 0.01
              && Math.Abs(info.TailBounds[0] - eb[0]) < 0.01 && Math.Abs(info.TailBounds[6] - eb[6]) < 0.01, tpl);
            var glbMin = new Vector3(float.MaxValue); var glbMax = new Vector3(float.MinValue);
            int tris = 0;
            foreach (var p in glbPrims[tpl])
            {
                for (int i = 0; i + 2 < p.Positions.Length; i += 3) { var q = ToUeLocal(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]); glbMin = Vector3.Min(glbMin, q); glbMax = Vector3.Max(glbMax, q); }
                tris += p.Indices.Length / 3;
            }
            A("mesh vs DR2 geometry (triangles drawn + skipped = objects.glb; box inside the glb's)", tris == wantT + m["skipped_triangles"]!.GetValue<int>() && mn.X >= glbMin.X - 0.01 && mn.Y >= glbMin.Y - 0.01 && mn.Z >= glbMin.Z - 0.01
              && mx.X <= glbMax.X + 0.01 && mx.Y <= glbMax.Y + 0.01 && mx.Z <= glbMax.Z + 0.01, $"{tpl}: {tris} glb triangles vs {wantT} + {m["skipped_triangles"]}");
            var mics = Enumerable.Range(0, info.Materials).Select(i => info.Mats[i].import).Select(imp => imp < 0 ? sp.ImportOuter(-imp - 1) : 0).Select(o => o < 0 ? sp.ImportName(-o - 1) : "").ToList();
            A("mesh materials are written MIC packages", mics.Count == info.Materials && mics.All(x => x.StartsWith("/Game/AcrXs/") && System.IO.File.Exists(File(x))), tpl);
            var bs = sp.GetExportData(0);
            A("mesh BodySetup has no collision shapes", BinaryPrimitives.ReadInt32LittleEndian(bs.AsSpan(8)) == 0 && bs.Length < 159, tpl);
            var dr = sp.DataResources();
            A("mesh data resource table (BodySetup blob, empty Nanite pages)", BinaryPrimitives.ReadInt32LittleEndian(dr.AsSpan(4)) == 2 && BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + 4)) + 28 == bs.Length
              && BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + LegacyPackage.DataResourceSize + 4)) == info.TailAt + 11, tpl);
        }
        if (meshes) Flush("meshes");

        // ---- the components in the cell
        var hisms = ReadHisms(cell);
        var rep = inst["components"]!.AsArray();
        int actor = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == ReplayActor);
        string ownerClass = OwnerClass(cfg);
        var ad = cell.GetExportData(actor);
        int ap = 0; var aslots = Unv.ReadHeader(ad, ref ap);
        foreach (var s in aslots) { if (s.Index == 78) break; if (!s.Zero) ap += 4; }
        int ic = BinaryPrimitives.ReadInt32LittleEndian(ad.AsSpan(ap));
        var listed = Enumerable.Range(0, ic).Select(k => BinaryPrimitives.ReadInt32LittleEndian(ad.AsSpan(ap + 4 + 4 * k))).ToHashSet();
        var cbs = cell.ExportDeps(actor).Where(d => d.kind == "CBS").Select(d => d.index).ToHashSet();
        var loc = SceneRootLocation(cell, out int sceneRoot);
        C(hisms.Count == rep.Count && hisms.Select(h => h.Name).ToHashSet().SetEquals(rep.Select(x => x!["component"]!.GetValue<string>())),
          $"{label} components: {hisms.Count} HierarchicalInstancedStaticMeshComponents named HISM_* in the cell = {rep.Count} in the report ({cell.ExportCount} exports, {cell.Names.Count} names)");
        foreach (var h in hisms)
        {
            if (ownerClass == ReplayActor)
                A("HISM owner lists it in InstanceComponents and depends on it", h.Owner == actor && listed.Contains(h.Export + 1) && cbs.Contains(h.Export + 1), h.Name);
            else
                A($"HISM owned by {ownerClass}, not listed by the replay actor", h.Owner >= 0 && cell.ExportClass(h.Owner) == ownerClass && !listed.Contains(h.Export + 1) && !cbs.Contains(h.Export + 1), h.Name);
            A("HISM attached to the replay actor's DefaultSceneRoot", h.Attach == sceneRoot && cell.ExportOuter(h.Attach) == actor + 1, h.Name);
            A("HISM bytes (no CachedBounds, native tail parses to the end, counts = SortedInstances = reorder table, identity tables, one leaf over 0..N-1)", h.Problems.Count == 0, $"{h.Name}: {string.Join("; ", h.Problems)}");
        }
        // ImportName gives the package's /Game path: it must be the one of the species the component is named after
        foreach (var h in hisms)
            A("HISM mesh = the species it is named after", speciesRep.Any(kv => h.Name.StartsWith("HISM_" + Clean(kv.Key) + "_") && kv.Value!["package"]!.GetValue<string>() == h.MeshPackage), $"{h.Name}: {h.MeshPackage}");
        Flush("components");

        // ---- instances against DR2: every placement of every species, chunk by chunk, in order
        double worst = 0, worstScale = 0; int mism = 0, total = 0, missing = 0;
        var stored = new Dictionary<string, List<(double[] m, Placed p)>>();
        foreach (var tpl in names)
        {
            var chunks = Chunks(expected[tpl], cfg.Num("instancing", "chunk_m"));
            var list = stored[tpl] = new();
            foreach (var (key, items) in chunks)
            {
                var h = hisms.FirstOrDefault(x => x.Name == ChunkName(tpl, key));
                if (h is null || h.Mats.Count != items.Count) { missing++; continue; }
                for (int i = 0; i < items.Count; i++)
                {
                    var m = h.Mats[i]; var x = items[i];
                    double dx = m[12] + loc.X - x.World.X, dy = m[13] + loc.Y - x.World.Y, dz = m[14] + loc.Z - x.World.Z;
                    worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    for (int k = 0; k < 12; k++) if (k % 4 != 3) worstScale = Math.Max(worstScale, Math.Abs(m[k] - x.Matrix[k]));
                    if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1) mism++;
                    list.Add((m, x));
                    total++;
                }
            }
            A("components per species = chunking of the DR2 placements", hisms.Count(h => h.Name.StartsWith("HISM_" + Clean(tpl) + "_")) == chunks.Count, tpl);
        }
        Flush("chunks");
        C(missing == 0 && total == expected.Where(kv => names.Contains(kv.Key)).Sum(kv => kv.Value.Count) && worst < 0.05 && worstScale < 1e-6 && mism == 0,
          $"{label} world positions: {total} instances of {names.Count} species in {hisms.Count} components; component-local + root ({loc.X:0.##}, {loc.Y:0.##}, {loc.Z:0.##}) equals ToUe(DR2 origin) within {worst:0.####} cm; " +
          $"axes/scale equal DR2's within {worstScale:0.#e+0}; {missing} chunks missing or short");
        double det = hisms.SelectMany(h => h.Mats).Min(m => m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8]));
        bool inside = true; bool boxOk = true;
        foreach (var h in hisms)
            foreach (var m in h.Mats)
            {
                inside &= inPhysicsBounds(new Splines.V3((m[12] + loc.X) / 100, (m[14] + loc.Z) / 100, (m[13] + loc.Y) / 100));
                boxOk &= m[12] >= h.Box[0] - 1 && m[12] <= h.Box[3] + 1 && m[13] >= h.Box[1] - 1 && m[13] <= h.Box[4] + 1 && m[14] >= h.Box[2] - 1 && m[14] <= h.Box[5] + 1;
            }
        C(det > 0 && inside, $"{label} all {total} instances inside the physics bounds; smallest 3x3 determinant {det:0.#} (> 0: no mirror)");
        C(boxOk, $"{label} BuiltInstanceBounds of every component contain its instance origins");

        // independent of InstanceMatrix: the mesh's vertices through the stored matrices (row-vector convention) against objects.glb vertices through DR2's own
        // matrix and the stage placement (ToUe); every sampled written vertex must sit on a DR2 vertex. Three instances per species (first, middle, last)
        var stageInst = stageObjs["instances"]!.AsArray().Where(x => names.Contains(x!["template"]!.GetValue<string>())).ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!);
        double worstV = 0; int tested = 0;
        foreach (var tpl in names)
        {
            var list = stored[tpl];
            if (list.Count == 0) continue;
            var dr2Pts = glbPrims[tpl].SelectMany(g => Enumerable.Range(0, g.Positions.Length / 3).Select(k => new Vector3(g.Positions[k * 3], g.Positions[k * 3 + 1], g.Positions[k * 3 + 2]))).ToList();
            foreach (int i in new[] { 0, list.Count / 2, list.Count - 1 }.Distinct())
            {
                var mm = ObjectColliders.Trs(stageInst[list[i].p.Id]["transform"]);
                var refPts = dr2Pts.Select(q => { var t0 = Vector3.Transform(q, mm); var u = pl.ToUe([t0.X, t0.Y, t0.Z]); return (u.X, u.Y, u.Z); }).ToList();
                var m = list[i].m;
                double worstOne = 0;
                foreach (var q in meshPos[tpl].Where((_, k) => k % 7 == 0))
                {
                    double x = q.X * m[0] + q.Y * m[4] + q.Z * m[8] + m[12] + loc.X, y = q.X * m[1] + q.Y * m[5] + q.Z * m[9] + m[13] + loc.Y, z = q.X * m[2] + q.Y * m[6] + q.Z * m[10] + m[14] + loc.Z;
                    double best = double.MaxValue;
                    foreach (var r in refPts) best = Math.Min(best, Math.Sqrt((r.Item1 - x) * (r.Item1 - x) + (r.Item2 - y) * (r.Item2 - y) + (r.Item3 - z) * (r.Item3 - z)));
                    worstOne = Math.Max(worstOne, best);
                }
                worstV = Math.Max(worstV, worstOne); tested++;
                A("sampled instance vertices on DR2's transformed vertices (< 0.1 cm)", worstOne < 0.1, $"{tpl} #{i} {list[i].p.Id}: {worstOne:0.###} cm");
            }
        }
        Flush("vertices");
        C(tested > 0, $"{label} instance vs DR2 geometry: mesh vertices (every 7th) of {tested} instances ({names.Count} species, first / middle / last) through the stored matrices land on objects.glb vertices through DR2's transform and the placement, worst {worstV:0.####} cm");

        // one tree, one drawing: nothing of an instanced template in the merged visuals, no collider of one drawn as a proxy
        var o = visuals["objects"];
        var perTemplate = (o?["templates"] as JsonObject)?.Select(kv => kv.Key).ToHashSet() ?? [];
        var real = (o?["real_trees"]?["per_template_real"] as JsonObject)?.Select(kv => kv.Key).ToHashSet() ?? [];
        var secNames = (visuals["sections"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
        // cards, stubs and merged meshes: none. (proxy: sections are the invisible-obstacle loop's hulls, counted separately below.)
        var leak = names.Where(t => perTemplate.Contains(t) || real.Contains(t) || secNames.Any(s => !s.StartsWith("proxy:") && !s.StartsWith("instancing:keep:") && (s.Contains(t + "_lod_d") || s.Contains(t + "_tr_d")))).ToList();
        int skipped = o?["instanced_trees"]?["placements"]?.GetValue<int>() ?? -1;
        int proxied = visuals["proxy"]?["of_instanced_trees"]?.GetValue<int>() ?? -1;
        // A proxy hull is drawn for an instanced tree only where the invisible-obstacle loop finds that its real trunk does not cover the collider at car height (the trunk
        // leans or curves away from DR2's straight capsule by over a cell): the check is not loosened for them, they are listed. Allowed: at most 0.2% of the placements.
        var ids = (visuals["proxy"]?["of_instanced_trees_ids"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
        C(leak.Count == 0 && skipped == total && proxied >= 0 && proxied <= Math.Max(1, total / 500),
          $"{label} one tree, one drawing: {skipped} placements left out of the merged visuals (instanced {total}); {leak.Count} instanced templates with cards, stubs or merged meshes ({string.Join(", ", leak)}); " +
          $"{proxied} of their colliders need a proxy hull because the real trunk is over a cell off the collider at car height ({string.Join(", ", ids)})");
        return failures;
    }
}
