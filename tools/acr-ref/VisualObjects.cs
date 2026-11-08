using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Render-only geometry for the DR2 objects, appended to the visuals mesh as one section per class colour, so the
/// driver sees what is solid. Solid = the shapes ObjectColliders kept (the same list that goes into BakedData).
///   - tree trunks: the kept trunk shapes themselves (capsule -> prism, cone, box); with vegetation "crowns" also a
///     small crown octahedron high up, kept clear of the road corridor in plan
///   - other instances with kept shapes: the DR2 render mesh (render/objects.glb, one mesh per template, template-local
///     vertices placed by the instance transform) if it is at most max_mesh_triangles and does not reach into the road
///     corridor beyond what the colliders may, else the kept shapes
///   - everything not solid (soft vegetation, cloth, light breakables, objects dropped from the road corridor): not
///     drawn; their render bounds are 9-17 m wide but low, so any proxy over them hides the road
internal static class VisualObjects
{
    public static readonly string[] Classes = ["trunks", "wood", "metal", "concrete", "rock", "plastic", "other", "soft"];

    internal static string ClassOf(string wall) => wall switch
    {
        "PM_WallWoodTreeTrunks" => "trunks",
        _ when wall.StartsWith("PM_WallWood") => "wood",
        _ when wall.StartsWith("PM_WallMetal") => "metal",
        "PM_WallConcrete" => "concrete",
        "PM_WallRock" => "rock",
        _ when wall.StartsWith("PM_WallPlastic") => "plastic",
        "PM_WallBush" => "soft",
        _ => "other",
    };

    sealed record Mesh(Vector3[] Pos, uint[] Idx, Vector3 Min, Vector3 Max, Vector2[] Uv, string[] TriMaterial, Dictionary<string, int> MatTris, string?[]? TreeMat = null, bool[]? TrunkTri = null);

    /// Round 3b, the middle tier: a tree mesh with only its tree-material triangles, the foliage simplified once to `ratio` of its triangles
    /// (GroundLod.SimplifyToCount, UV seams locked; at least minTris per material) and the trunk triangles kept whole (the drawn trunk is
    /// what the invisible-obstacle check compares with the collider). Null when the mesh has no tree material.
    static Mesh? MidMesh(Mesh mesh, double ratio, int minTris)
    {
        if (mesh.TreeMat is not { } tm) return null;
        var pos = new List<Vector3>(); var idx = new List<uint>(); var uv = new List<Vector2>(); var triMat = new List<string>(); var treeMat = new List<string?>(); var trunk = new List<bool>();
        var groups = Enumerable.Range(0, mesh.Idx.Length / 3).Where(t => tm[t] is not null).GroupBy(t => (key: tm[t]!, trunk: mesh.TrunkTri?[t] == true));
        foreach (var g in groups)
        {
            // the group as its own small mesh (cm for the simplifier), vertices remapped
            var map = new Dictionary<uint, uint>(); var gp = new List<Vector3>(); var gu = new List<Vector2>(); var gi = new List<uint>();
            foreach (var t in g)
                for (int k = 0; k < 3; k++)
                {
                    uint v = mesh.Idx[t * 3 + k];
                    if (!map.TryGetValue(v, out var nv)) { map[v] = nv = (uint)gp.Count; gp.Add(mesh.Pos[v] * 100); gu.Add(mesh.Uv[v]); }
                    gi.Add(nv);
                }
            int n = gi.Count / 3;
            List<Vector3> sp = gp; List<uint> si = gi; List<Vector2> su = gu;
            if (!g.Key.trunk && ratio < 1 && n > minTris)
            {
                // DR2 foliage is many separate leaf cards: an edge collapse cannot merge them (2645 -> 2123 at 0.25). So the cards are thinned:
                // every connected piece is kept or dropped as a whole (evenly spread, deterministic), each kept piece scaled about its centre
                // by sqrt(1/ratio) (at most 1.8) so the crown keeps about its cover. A group of few large pieces is collapsed as before
                var parent = Enumerable.Range(0, gp.Count).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                for (int t = 0; t < n; t++) { int a = Find((int)gi[t * 3]); foreach (var k in new[] { 1, 2 }) { int c = Find((int)gi[t * 3 + k]); if (c != a) parent[c] = a; } }
                var pieces = Enumerable.Range(0, n).GroupBy(t => Find((int)gi[t * 3])).ToList();
                if (pieces.Count >= 12)
                {
                    float scale = (float)Math.Min(1.8, Math.Sqrt(1 / ratio));
                    var kp = new List<Vector3>(); var ku = new List<Vector2>(); var ki = new List<uint>();
                    double acc = 0;
                    foreach (var piece in pieces.OrderBy(p => gp[(int)gi[p.First() * 3]].Z).ThenBy(p => gp[(int)gi[p.First() * 3]].X))
                    {
                        acc += ratio; if (acc < 1) continue; acc -= 1;
                        var vs = piece.SelectMany(t => new[] { gi[t * 3], gi[t * 3 + 1], gi[t * 3 + 2] }).Distinct().ToList();
                        var ctr = vs.Aggregate(Vector3.Zero, (s, v) => s + gp[(int)v]) / vs.Count;
                        var remap = new Dictionary<uint, uint>();
                        foreach (var v in vs) { remap[v] = (uint)kp.Count; kp.Add(ctr + (gp[(int)v] - ctr) * scale); ku.Add(gu[(int)v]); }
                        foreach (var t in piece) for (int k = 0; k < 3; k++) ki.Add(remap[gi[t * 3 + k]]);
                    }
                    if (ki.Count / 3 >= minTris) (sp, si, su) = (kp, ki, ku);
                }
                else (sp, si, su, _) = GroundLod.SimplifyToCount(gp, gi, gu, Math.Max(minTris, (int)Math.Ceiling(n * ratio)));
            }
            uint b = (uint)pos.Count;
            pos.AddRange(sp.Select(v => v / 100)); uv.AddRange(su); idx.AddRange(si.Select(i => i + b));
            int m = si.Count / 3;
            triMat.AddRange(Enumerable.Repeat(g.Key.key, m)); treeMat.AddRange(Enumerable.Repeat<string?>(g.Key.key, m)); trunk.AddRange(Enumerable.Repeat(g.Key.trunk, m));
        }
        if (idx.Count == 0) return null;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in pos) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        return new Mesh(pos.ToArray(), idx.ToArray(), mn, mx, uv.ToArray(), triMat.ToArray(), triMat.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()), treeMat.ToArray(), trunk.ToArray());
    }

    /// Prefix of the per-DR2-material sections (dr2Material): "dr2:" + the section key (glb material name, see Build).
    public const string Dr2Prefix = "dr2:";

    /// Prefix of the sections of objects drawn without a collider of their own whose collision is in DR2's ground mesh
    /// (walls, rock banks, armco): "static:" + "other" or + Dr2Prefix + material.
    public const string StaticPrefix = "static:";

    /// Prefix of the per-species trunk sections: "bark:" + the DR2 trunk texture (<template>_tr_d) of the tree template.
    public const string BarkPrefix = "bark:";

    /// dr2Material (v1.8): render-mesh instances put their triangles of the accepted DR2 materials into one section per
    /// material, with the glb's TEXCOORD_0; everything else stays in the class sections as before. (template, glb material
    /// name, triangles of it in that template) -> section key, or null if not accepted; the key is the material name, or
    /// name@template where several DR2 materials share the name (water_runoff on two buildings) so each keeps its textures.
    /// trunkTexture: the same arguments -> the DR2 trunk diffuse if that material is the template's foliage_trunk one.
    /// Prefix of the tree billboard sections (vegetation "cards"): "cards:" + DR2 billboard texture (the *_lod_d strip).
    public const string CardPrefix = "cards:";

    /// Prefix of the sections of the real near-road trees (round 3): "tree:" + the section key treeMaterial gives (foliage_trunk and foliage materials).
    public const string TreePrefix = "tree:";

    /// cardTexture (vegetation "cards"): the DR2 billboard texture of a tree template (its far LOD, foliage_billboard.fx, is
    /// not in objects.glb), else null; cardFrames: the number of views in that texture's strip (width / height).
    public static (Dictionary<string, ProcMesh.Section> sections, JsonObject report, Func<string, string?> bark) Build(
        StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, ObjectColliders.Result objs, Func<V3, bool> inPhysicsBounds, string vegetation, string? canopies = null,
        Func<string, string, int, string?>? dr2Material = null, Func<string, string?>? cardTexture = null, Func<string, int>? cardFrames = null,
        Func<string, string, int, string?>? trunkTexture = null, Func<string, string, int, string?>? treeMaterial = null,
        HashSet<string>? instanced = null)
    {
        int maxMeshTris = (int)cfg.Num("objects", "max_mesh_triangles");
        int sides = (int)cfg.Num("objects", "prism_sides");
        double trunkDrawCm = cfg["objects"].TryGetValue("trunk_draw_height_m", out var tdh) && tdh is double tdhd ? tdhd * 100 : 0;   // 0: whole trunk
        var sec = Classes.ToDictionary(c => c, _ => new ProcMesh.Section());
        var counts = Classes.ToDictionary(c => c, _ => 0);

        var helperKinds = cfg.Arr("dr2_textures", "object_skip_material_kinds").Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedHelpers = new Dictionary<string, int>();
        var meshes = new Dictionary<string, Mesh>();
        foreach (var g in new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives(withUv: dr2Material is not null).GroupBy(p => p.Mesh))
        {
            var pos = new List<Vector3>(); var idx = new List<uint>(); var uv = new List<Vector2>(); var triMat = new List<string>();
            var matTris = new Dictionary<string, int>();
            foreach (var p in g)
            {
                var kind = p.Material.Split('!')[0];
                if (helperKinds.Contains(kind))
                {
                    removedHelpers[kind] = removedHelpers.GetValueOrDefault(kind) + p.Indices.Length / 3;
                    continue;
                }
                uint b = (uint)pos.Count;
                for (int i = 0; i < p.Positions.Length; i += 3) pos.Add(new Vector3(p.Positions[i], p.Positions[i + 1], p.Positions[i + 2]));
                var t0 = p.Uv.Length > 0 ? p.Uv[0] : null;
                for (int i = 0; i < p.Positions.Length / 3; i++) uv.Add(t0 is null ? Vector2.Zero : new Vector2(t0[i * 2], t0[i * 2 + 1]));
                idx.AddRange(p.Indices.Select(i => i + b));
                triMat.AddRange(Enumerable.Repeat(p.Material, p.Indices.Length / 3));
                matTris[p.Material] = matTris.GetValueOrDefault(p.Material) + p.Indices.Length / 3;
            }
            if (idx.Count == 0) continue;
            // round 3: the section key of each triangle's foliage_trunk / foliage material (null: not a tree material)
            string?[]? treeMat = null; bool[]? trunkTri = null;
            if (treeMaterial is not null)
            {
                var tk = matTris.ToDictionary(kv => kv.Key, kv => treeMaterial(g.Key, kv.Key, kv.Value));
                if (tk.Values.Any(x => x is not null)) treeMat = triMat.Select(x => tk[x]).ToArray();
                if (trunkTexture is not null)
                {
                    var tr = matTris.ToDictionary(kv => kv.Key, kv => trunkTexture(g.Key, kv.Key, kv.Value) is not null);
                    trunkTri = triMat.Select(x => tr[x]).ToArray();
                }
            }
            // accepted materials: TriMaterial holds the section key (see dr2Material), the others keep the glb name
            if (dr2Material is not null)
            {
                var keys = matTris.ToDictionary(kv => kv.Key, kv => dr2Material(g.Key, kv.Key, kv.Value));
                for (int t = 0; t < triMat.Count; t++)
                    if (keys[triMat[t]] is { } key) { triMat[t] = key; sec.TryAdd(Dr2Prefix + key, new ProcMesh.Section()); }
            }
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in pos) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            meshes[g.Key] = new Mesh(pos.ToArray(), idx.ToArray(), mn, mx, uv.ToArray(), triMat.ToArray(), matTris, treeMat, trunkTri);
        }
        foreach (var kind in helperKinds.OrderBy(x => x))
            Console.WriteLine($"objects: removed helper {kind}: {removedHelpers.GetValueOrDefault(kind)} prototype triangles (before instancing)");
        // a tree template's own bark: the DR2 trunk texture of its foliage_trunk material (null: the trunks class texture)
        var barkCache = new Dictionary<string, string?>();
        string? Bark(string template)
        {
            if (barkCache.TryGetValue(template, out var known)) return known;
            string? tex = null;
            if (trunkTexture is not null && meshes.TryGetValue(template, out var bm))
                tex = bm.MatTris.Select(kv => trunkTexture(template, kv.Key, kv.Value)).FirstOrDefault(x => x is not null);
            return barkCache[template] = tex;
        }
        ProcMesh.Section BarkSec(string tex) => sec.TryGetValue(BarkPrefix + tex, out var bs) ? bs : sec[BarkPrefix + tex] = new ProcMesh.Section();
        int dr2Tris = 0, dr2Instances = 0;

        var kept = objs.Kept.GroupBy(s => s.Instance).ToDictionary(g => g.Key, g => g.ToList());
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var templates = stage["templates"]!.AsObject();
        var treeClasses = cfg.Arr("objects", "tree_shape_classes").Cast<string>().ToHashSet();
        int instances = 0, outside = 0, noMesh = 0, meshInstances = 0, proxyInstances = 0, notSolid = 0, treeInstances = 0;
        int boxesFitted = 0, boxesKept = 0;
        int preciseReplaced = 0, preciseBoxes = 0, preciseKept = 0;
        bool fitBoxes = cfg["objects"].TryGetValue("fit_colliders_to_mesh", out var fb) && fb is double fbd && fbd != 0;
        double fitMax = cfg["objects"].TryGetValue("fit_colliders_max_cm", out var boxFitMaxValue) && boxFitMaxValue is double fmd ? fmd : 100;
        int alignChecked = 0, misaligned = 0, crownsDrawn = 0, crownsDropped = 0, staticDrawn = 0;
        // objects without a collider that are still drawn from their render mesh unless the template name matches this (null: none)
        int notSolidMaxTris = cfg["objects"].TryGetValue("not_solid_max_triangles", out var nsm) && nsm is double nsmd ? (int)nsmd : maxMeshTris;
        var notSolidSkip = cfg["objects"].TryGetValue("not_solid_skip", out var nss) && nss is string nsp ? new System.Text.RegularExpressions.Regex(nsp, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
        var perTemplate = new SortedDictionary<string, string>();
        bool cards = vegetation == "cards" && cardTexture is not null && cardFrames is not null;
        bool crowns = vegetation switch { "none" => false, "crowns" or "cards" => true, _ => throw new ArgumentException($"vegetation must be none, crowns or cards, not {vegetation}") };
        int cardsDrawn = 0, cardsDropped = 0, undergrowth = 0, parallelCards = 0;
        var cardTex = new Dictionary<string, string?>();
        double overhang = cfg.Num("objects", "overhang_height_m") * 100;
        double meshPenLimit = objs.Probe.MaxPen + cfg.Num("objects", "road_overlap_tolerance_m") * 100;
        double maxOverBounds = cfg.Num("objects", "max_extent_over_bounds");
        var fallbacks = new SortedDictionary<string, (int n, double depth)>();
        // [objects] overhead_templates: start / finish arches and timing gantries that span the road by design. Their render mesh is drawn
        // even though it reaches into the road corridor; the triangles inside it at car height are left out (as for the near trees)
        var overheadRe = cfg["objects"].TryGetValue("overhead_templates", out var ohv) && ohv is string ohs && ohs.Length > 0 ? new System.Text.RegularExpressions.Regex(ohs) : null;
        int overheadInstances = 0, overheadDropped = 0; var overheadSections = new SortedSet<string>(StringComparer.Ordinal);
        bool Fallback(string t, double d) { var f = fallbacks.GetValueOrDefault(t); fallbacks[t] = (f.n + 1, Math.Max(f.depth, d)); return false; }
        var extents = new List<(string template, string id, double ext, double bounds)>();
        var canopyMode = canopies ?? (cfg["objects"].TryGetValue("canopies", out var cm) && cm is string cms ? cms : "clip");
        if (canopyMode is not ("clip" or "apex" or "full" or "none")) throw new ArgumentException($"[objects] canopies must be clip, apex, full or none, not {canopyMode}");
        double canopyClip = cfg.Num("objects", "canopy_clip_height_m") * 100, minFill = cfg.Num("objects", "shape_min_fill");
        double canopyMaxRadius = canopyMode == "apex" ? cfg.Num("objects", "canopy_max_span_m") * 100 / 2 * 0.98 : double.MaxValue;
        int canopiesClipped = 0, canopiesHidden = 0, shapesCompared = 0;
        double worstOutside = 0, worstFill = 1;
        var shapeMismatches = new List<string>();
        var canopyColliderTops = new List<double>();
        var canopySpans = new List<(double span, string who)>();
        var flareSpans = new List<(double span, string who)>();
        static string Dims(Vector3 a, Vector3 b) => $"{(b.X - a.X) / 100:0.0} x {(b.Y - a.Y) / 100:0.0} x {(b.Z - a.Z) / 100:0.0} m";

        V3 Ue(Vector3 dr2) => pl.ToUe([dr2.X, dr2.Y, dr2.Z]);

        bool IsTree(string tname, List<ObjectColliders.Shape> shapes) => shapes.Any(s => s.Material == "PM_WallWoodTreeTrunks") || templates[tname]?["bodies"]?.AsArray()
            .SelectMany(b => b!["shapes"]!.AsArray()).Any(s => treeClasses.Contains(s!["shape_class"]?.GetValue<string>() ?? "")) == true;

        // round 3: the trees drawn with their real mesh: origin within [vegetation] near_m of the centreline, nearest first until
        // near_max_triangles are used (their tree materials' triangles)
        var realTrees = new HashSet<string>();
        double nearM = cfg["vegetation"].TryGetValue("near_m", out var nmv) && nmv is double nmd ? nmd : 0;
        int nearBudget = cfg["vegetation"].TryGetValue("near_max_triangles", out var nbv) && nbv is double nbd ? (int)nbd : int.MaxValue;
        // round 3b, the middle tier: trees within mid_m that did not fit the full budget get MidMesh (foliage simplified to mid_ratio),
        // nearest first until mid_max_triangles; 0 = off. Farther trees keep the cards and trunk stub
        double midM = cfg["vegetation"].TryGetValue("mid_m", out var mmv) && mmv is double mmd ? mmd : 0;
        int midBudget = cfg["vegetation"].TryGetValue("mid_max_triangles", out var mbv) && mbv is double mbd ? (int)mbd : int.MaxValue;
        double midRatio = cfg["vegetation"].TryGetValue("mid_ratio", out var mrv) && mrv is double mrd ? mrd : 0.3;
        int midMinTris = cfg["vegetation"].TryGetValue("mid_min_triangles", out var mnv) && mnv is double mnd ? (int)mnd : 24;
        var midTrees = new HashSet<string>(); var midMeshes = new Dictionary<string, Mesh?>();
        Mesh? Mid(string tname) => midMeshes.TryGetValue(tname, out var mm) ? mm : midMeshes[tname] = meshes.TryGetValue(tname, out var full) ? MidMesh(full, midRatio, midMinTris) : null;
        int nearCandidates = 0, nearCandidateTris = 0;
        JsonObject? r0Fit = null;
        var fitIds = new HashSet<string>();   // the trees (not bushes) drawn with a real mesh, either tier
        if ((nearM > 0 || midM > 0) && treeMaterial is not null)
        {
            double reach = Math.Max(nearM, midM);
            var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)), reach + 50);
            var cand = new List<(double d, string id, int tris, string tname, bool bush)>();
            foreach (var inst in stage["instances"]!.AsArray())
            {
                var tname = inst!["template"]!.GetValue<string>();
                if (instanced?.Contains(tname) == true) continue;   // instanced trees are not drawn here
                if (!meshes.TryGetValue(tname, out var mesh) || mesh.TreeMat is not { } tm) continue;
                var origin = Vector3.Transform(Vector3.Zero, ObjectColliders.Trs(inst["transform"]));
                if (!inPhysicsBounds(pl.ToPhysics(origin.X, origin.Y, origin.Z))) continue;
                var id = inst["id"]!.GetValue<string>();
                var sh = kept.GetValueOrDefault(id) ?? [];
                if (sh.Count > 0 && !(IsTree(tname, sh) || sh.All(s => ClassOf(s.Material) == "soft"))) continue;   // trees, solid bushes, and soft vegetation without a collider
                var u = Ue(origin);
                double d = route.Metres(new Vector2((float)u.X, (float)u.Y));
                if (d <= reach) cand.Add((d, id, tm.Count(x => x is not null), tname, !IsTree(tname, sh)));
            }
            nearCandidates = cand.Count(c => c.d <= nearM); nearCandidateTris = cand.Where(c => c.d <= nearM).Sum(c => c.tris);
            int used = 0, midUsed = 0, bushUsed = 0;
            int bushBudget = cfg["vegetation"].TryGetValue("bush_max_triangles", out var bbv) && bbv is double bbd ? (int)bbd : 0;
            foreach (var c in cand.OrderBy(c => c.d))
            {
                // bushes (soft vegetation, solid bush colliders): thinned DR2 mesh within mid_m from their own budget, so the trees keep theirs
                if (c.bush) { if (c.d <= midM && Mid(c.tname) is { } bm && bushUsed + bm.Idx.Length / 3 <= bushBudget) { bushUsed += bm.Idx.Length / 3; midTrees.Add(c.id); } continue; }
                if (c.d <= nearM && used + c.tris <= nearBudget) { used += c.tris; realTrees.Add(c.id); fitIds.Add(c.id); continue; }
                if (c.d <= midM && Mid(c.tname) is { } mid && midUsed + mid.Idx.Length / 3 <= midBudget) { midUsed += mid.Idx.Length / 3; midTrees.Add(c.id); fitIds.Add(c.id); }
            }
        }
        // collider fit: every tree template with a real DR2 mesh (drawn real, middle tier or as card + stub; the stub follows the collider) has its trunk capsule fitted to the real trunk (ColliderFit.FitMeshTrees, as the instanced trees' are), so no thick stand-in is
        // drawn for the part of DR2's capsule beyond the trunk. Runs on the selection above, once per stage-build (the capsules are shared by both cells); the build check still
        // holds each collider to what is drawn
        bool fitColliders = (cfg["vegetation"].TryGetValue("fit_tree_colliders", out var frv) && frv is double frd ? frd : cfg["vegetation"].TryGetValue("fit_real_tree_colliders", out var frv2) && frv2 is double frd2 ? frd2 : 1) != 0;
        if (fitColliders)
        {
            var fitList = new List<ColliderFit.MeshTree>();
            foreach (var inst in stage["instances"]!.AsArray())
            {
                var id = inst!["id"]!.GetValue<string>(); var tname = inst["template"]!.GetValue<string>();
                if (instanced?.Contains(tname) == true || objs.RealFitted.Contains(id) || !meshes.TryGetValue(tname, out var fm) || fm.TreeMat is null) continue;
                var org = Vector3.Transform(Vector3.Zero, ObjectColliders.Trs(inst["transform"]));
                if (!inPhysicsBounds(pl.ToPhysics(org.X, org.Y, org.Z)) || !IsTree(tname, kept.GetValueOrDefault(id) ?? [])) continue;
                var caps = (kept.GetValueOrDefault(id) ?? []).Where(s => s.Material == "PM_WallWoodTreeTrunks" && s.Capsule is { } cp && Math.Abs(ObjectColliders.RotationAxes(cp.Rot[0], cp.Rot[1], cp.Rot[2]).Item2.Z) > 0.7).Select(s => s.Capsule!).ToList();
                objs.RealFitted.Add(id);
                if (caps.Count == 0) continue;
                var tm0 = ObjectColliders.Trs(inst["transform"]);
                fitList.Add(new(tname, id, Ue(Vector3.Transform(Vector3.Zero, tm0)).Z, () => fm.Pos.Select(p => { var u = Ue(Vector3.Transform(p, tm0)); return new Vector3((float)u.X, (float)u.Y, (float)u.Z); }).ToArray(), caps));
            }
            var fitGeo = new Dictionary<string, (IReadOnlyList<uint>, IReadOnlyList<uint>)>();
            r0Fit = ColliderFit.FitMeshTrees(fitList, t =>
            {
                if (fitGeo.TryGetValue(t, out var g)) return g;
                var fm = meshes[t]; var tm = fm.TreeMat!;
                IEnumerable<uint> Tris(Func<int, bool> pick) => Enumerable.Range(0, fm.Idx.Length / 3).Where(pick).SelectMany(k => new[] { fm.Idx[k * 3], fm.Idx[k * 3 + 1], fm.Idx[k * 3 + 2] });
                return fitGeo[t] = (Tris(k => tm[k] is not null && fm.TrunkTri?[k] == true).ToList(), Tris(k => tm[k] is not null).ToList());
            }, cfg.Num("instancing", "collider_gap_limit_cm"));
        }
        int midDrawn = 0, midTris = 0;
        int realDrawn = 0, realTris = 0, realClipped = 0, realClippedTris = 0, instancedPlacements = 0, instancedSolid = 0;
        var realPer = new SortedDictionary<string, int>(); var cardedPer = new SortedDictionary<string, int>(); var midPer = new SortedDictionary<string, int>();

        foreach (var inst in stage["instances"]!.AsArray())
        {
            instances++;
            var tname = inst!["template"]!.GetValue<string>();
            var m = ObjectColliders.Trs(inst["transform"]);
            var origin = Vector3.Transform(Vector3.Zero, m);
            if (!inPhysicsBounds(pl.ToPhysics(origin.X, origin.Y, origin.Z))) { outside++; continue; }
            meshes.TryGetValue(tname, out var mesh);
            var shapes = kept.GetValueOrDefault(inst["id"]!.GetValue<string>()) ?? [];
            // instanced trees (Instancing): one tree, one drawing. No card, trunk stub, proxy hull or merged real mesh from here; the collider is in BakedData as it was
            if (instanced?.Contains(tname) == true)
            {
                instancedPlacements++; if (shapes.Count > 0) instancedSolid++;
                continue;
            }
            bool fence = tname.Contains("fence", StringComparison.OrdinalIgnoreCase);
            bool tree = !fence && IsTree(tname, shapes);

            var before = sec.ToDictionary(kv => kv.Key, kv => kv.Value.Pos.Count);
            // DR2's own billboard (<template>_lod_d) as two crossed cards with its front and side views, turned by the
            // instance's yaw: null = no billboard for this template, false = dropped (no room beside the road), true = drawn
            bool? TryCard(string template, Mesh? mesh0, Matrix4x4 m0)
            {
                if (!cards || mesh0 is null) return null;
                if (!cardTex.TryGetValue(template, out var billboard)) cardTex[template] = billboard = cardTexture!(template);
                if (billboard is null) return null;
                var key = CardPrefix + billboard;
                var ax0 = Vector3.TransformNormal(Vector3.UnitX, m0);
                var o0 = Ue(Vector3.Transform(Vector3.Zero, m0)); var o1 = Ue(Vector3.Transform(Vector3.Zero, m0) + ax0);
                double yaw0 = Math.Atan2(o1.Y - o0.Y, o1.X - o0.X);
                var cb0 = CardBox(mesh0, m0, Ue, objs.Probe.Road, cfg);
                // the clearance at the plant's centre is an estimate (Finland 01 r3: a card 0.86 m into the road on a bend):
                // both crossed cards are sampled every 25 cm and shrunk until every point keeps the margin
                if (cb0 is { } b0)
                {
                    double margin = cfg.Num("objects", "crown_road_margin_m") * 100, minR = cfg.Num("objects", "crown_min_radius_m") * 100;
                    var mid = (b0.box.mn + b0.box.mx) * 0.5;
                    bool Fits(double r) => new[] { yaw0, yaw0 + Math.PI / 2 }.All(a =>
                    {
                        int steps = Math.Max(2, (int)Math.Ceiling(2 * r / 25));
                        return Enumerable.Range(0, steps + 1).All(i =>
                        {
                            double f = -1 + 2.0 * i / steps;
                            return objs.Probe.Road.QueryNear(mid.X + Math.Cos(a) * r * f, mid.Y + Math.Sin(a) * r * f).clearance >= margin;
                        });
                    });
                    double rr = b0.radius;
                    while (rr >= minR && !Fits(rr)) rr *= 0.8;
                    cb0 = rr >= minR ? ((new V3(mid.X - rr, mid.Y - rr, b0.box.mn.Z), new V3(mid.X + rr, mid.Y + rr, b0.box.mx.Z)), rr, b0.full) : null;
                }
                if (cb0 is not { } cb)
                {
                    // no room for a crossed pair: one card parallel to the road, ends kept off it
                    if (ParallelCard(mesh0, m0, Ue, objs.Probe.Road, cfg) is not { } pc) { cardsDropped++; return false; }
                    if (!sec.TryGetValue(key, out var ps)) sec[key] = ps = new ProcMesh.Section();
                    AddCard(ps, pc.box, pc.radius, pc.full, pc.yaw, 0, cardFrames!(billboard));
                    cardsDrawn++; parallelCards++;
                    return true;
                }
                if (!sec.TryGetValue(key, out var cs)) sec[key] = cs = new ProcMesh.Section();
                AddCards(cs, cb.box, cb.radius, cb.full, yaw0, cardFrames!(billboard));
                cardsDrawn++;
                return true;
            }
            double baseZ = Ue(origin).Z;
            void DrawShape(ObjectColliders.Shape s, string? into = null)
            {
                // a tree's trunk shapes: its own bark section when the template has a DR2 trunk texture
                bool trunk = s.Material == "PM_WallWoodTreeTrunks";
                var target = into is null && trunk && Bark(tname) is { } bark ? BarkSec(bark) : sec[into ?? ClassOf(s.Material)];
                int from = target.Pos.Count, fromIdx = target.Idx.Count;
                bool canopy = IsCanopy(s);
                if (canopy && canopyMode == "none") { canopiesHidden++; return; }
                if (canopy && canopyMode is "clip" or "apex")
                {
                    if (!AddClippedCone(target, s, baseZ + canopyClip, canopyMode == "apex", canopyMaxRadius)) { canopiesHidden++; return; }
                    canopiesClipped++;
                }
                else AddShape(target, s, sides, pl);
                // drawn vs collider: inside the collider's analytic bounds; unclipped at least min_fill of them per axis
                var (cmn, cmx) = ColliderBounds(s);
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int i = from; i < target.Pos.Count; i++) { lo = Vector3.Min(lo, target.Pos[i]); hi = Vector3.Max(hi, target.Pos[i]); }
                double outBy = Math.Max(Math.Max(Math.Max(cmn.X - lo.X, hi.X - cmx.X), Math.Max(cmn.Y - lo.Y, hi.Y - cmx.Y)), Math.Max(cmn.Z - lo.Z, hi.Z - cmx.Z));
                double fill = canopy && canopyMode is "clip" or "apex" ? 1 : Math.Min(Math.Min((hi.X - lo.X) / Math.Max(1, cmx.X - cmn.X), (hi.Y - lo.Y) / Math.Max(1, cmx.Y - cmn.Y)),
                                                                       (hi.Z - lo.Z) / Math.Max(1, cmx.Z - cmn.Z));
                shapesCompared++;
                if (outBy > 2 || fill < minFill)
                    shapeMismatches.Add($"{s.Template} {s.Instance} {s.Kind}: drawn {Dims(lo, hi)} vs collider {Dims(cmn, cmx)} (outside by {outBy:0} cm, fill {fill:0.00})");
                worstOutside = Math.Max(worstOutside, outBy); worstFill = Math.Min(worstFill, fill);
                if (canopy) canopyColliderTops.Add(cmx.Z - baseZ);
                if (canopy && canopyMode is "clip" or "apex") canopySpans.Add((Math.Max(hi.X - lo.X, hi.Y - lo.Y), $"{s.Template} {s.Instance}"));
                if (!canopy && s.MeshPhysics is { Count: > 1 }) flareSpans.Add((Math.Max(Math.Max(hi.X - lo.X, hi.Y - lo.Y), hi.Z - lo.Z), $"{s.Template} {s.Instance}"));
                // tree trunk capsules start above the ground in DR2 (drawn they float on a pointed tip): their lower half
                // reaches down to the tree's base (after the collider comparison above)
                if (s.Capsule is { } cap && trunk)
                    for (int i = from; i < target.Pos.Count; i++)
                        if (target.Pos[i].Z < cap.Center.Z && target.Pos[i].Z > baseZ - 30) target.Pos[i] = target.Pos[i] with { Z = (float)baseZ - 30 };
                // a near-vertical trunk prism is drawn up to trunk_draw_height_m above the tree base only: 20-26 m columns
                // stood up out of the crowns (the collider stays whole)
                if (trunk && TrunkClipZ(s, trunkDrawCm, baseZ) is { } clipZ) ClipAbove(target, from, fromIdx, clipZ);
                // a tall prism is two long rings: its side triangles reach far above car height, where the drawn-surface
                // map ignores them, so the trunk would read as nothing drawn. Rings every metre over the lowest 3 m
                if (s.Capsule is { } tall && tall.Length > 400 && Math.Abs(ObjectColliders.RotationAxes(tall.Rot[0], tall.Rot[1], tall.Rot[2]).Item2.Z) > 0.5)
                {
                    var (tax, tay, taz) = ObjectColliders.RotationAxes(tall.Rot[0], tall.Rot[1], tall.Rot[2]);
                    var up = tay.Z >= 0 ? tay : tay * -1.0;
                    var low = tall.Center - up * (tall.Length / 2);
                    if (s.Material == "PM_WallWoodTreeTrunks") low = new V3(low.X, low.Y, Math.Min(low.Z, baseZ - 30));
                    int n = Math.Max(3, sides);
                    var rings = new List<V3>();
                    for (int k = 0; k < 4; k++)
                        for (int i = 0; i < n; i++)
                        {
                            double a = 2 * Math.PI * i / n;
                            rings.Add(low + up * (k * 100.0) + tax * (Math.Cos(a) * tall.Radius) + taz * (Math.Sin(a) * tall.Radius));
                        }
                    var rf = new List<(int, int, int, int)>();
                    for (int k = 0; k < 3; k++) for (int i = 0; i < n; i++) rf.Add((k * n + i, k * n + (i + 1) % n, (k + 1) * n + (i + 1) % n, (k + 1) * n + i));
                    AddHull(target, rings, rf, low + up * 150.0);
                }
            }
            // solid bush colliders (PM_WallBush): soft vegetation is mostly not drawn (v1.4; drawn they were up to 3M triangles of bushes).
            // A collider with no card, or within reach of the road, is drawn all the same, into "other" (soft stays out of the
            // road corridor): no invisible obstacle
            string iid = inst["id"]!.GetValue<string>();
            bool bush = !tree && shapes.Count > 0 && shapes.All(s => ClassOf(s.Material) == "soft");
            bool plant = shapes.Count == 0 && !tree;   // no collider of its own (soft vegetation: DR2 bushes the car drives through)
            bool midTier = (shapes.Count > 0 && (tree || bush) || plant) && midTrees.Contains(iid);
            if (midTier && !fence) mesh = Mid(tname);
            // round 3b: a bush picked for the full or middle tier is drawn with its DR2 mesh instead of its card (below, as a tree)
            bool bushReal = (bush || plant) && (midTier || realTrees.Contains(iid)) && mesh?.TreeMat is not null;
            if (bush && !bushReal)
            {
                notSolid++;
                bool bushCard = TryCard(tname, mesh, m) == true;
                if (bushCard) undergrowth++;
                bool nearRoad = shapes.Any(s => { var (cmn, cmx) = ColliderBounds(s); return objs.Probe.Road.QueryNear((cmn.X + cmx.X) / 2, (cmn.Y + cmx.Y) / 2).clearance < 1500; });
                if (!bushCard || nearRoad) foreach (var s in shapes) DrawShape(s, "other");
                perTemplate.TryAdd(tname, bushCard ? (nearRoad ? "bush collider: billboard cards and collider shapes" : "bush collider: billboard cards") : "bush collider: shapes");
                continue;
            }
            if ((tree && shapes.Count > 0 || bushReal) && mesh?.TreeMat is { } tmat && (midTier || realTrees.Contains(iid)))
            {
                if (bushReal)
                {
                    // the bush's soft collider within reach of the road is drawn as before (with a card it was too): no invisible obstacle
                    notSolid++;
                    if (shapes.Any(s => { var (cmn, cmx) = ColliderBounds(s); return objs.Probe.Road.QueryNear((cmn.X + cmx.X) / 2, (cmn.Y + cmx.Y) / 2).clearance < 1500; }))
                        foreach (var s in shapes) DrawShape(s, "other");
                    perTemplate.TryAdd(tname, "bush collider: real DR2 mesh");
                }
                // round 3: a tree near the road is drawn with its real LOD0 mesh (opaque trunk, cut-out foliage); no card, no trunk
                // stub (the real trunk is there, the collider capsule is no wider). Triangles reaching into the road corridor beyond
                // what the colliders may are left out, as for the ornaments
                treeInstances++;
                bool[]? drop = null;
                // over any corridor surface, not only the road's (the check's own probe found foliage 2 m into the corridor on
                // Nz01 where the build's road-surface test found none)
                var w = mesh.Pos.Select(p => Ue(Vector3.Transform(p, m))).ToArray();
                var dropT = new bool[mesh.Idx.Length / 3]; int dropN = 0;
                for (int t = 0; t < dropT.Length; t++)
                    if (objs.Probe.TriangleDepth(w[mesh.Idx[t * 3]], w[mesh.Idx[t * 3 + 1]], w[mesh.Idx[t * 3 + 2]], 50, objs.Probe.HMin, overhang, surfaceOnly: false) > meshPenLimit) { dropT[t] = true; dropN++; }
                if (dropN > 0) { drop = dropT; realClipped++; realClippedTris += dropN; }
                ProcMesh.Section TreeSec(string key) => sec.TryGetValue(TreePrefix + key, out var ts) ? ts : sec[TreePrefix + key] = new ProcMesh.Section();
                int added = AddMeshSplit(t => TreeSec(tmat[t]!), _ => true, mesh, m, Ue, t => tmat[t] is not null && (drop is null || !drop[t]));
                // Fit only to triangles emitted above, including material and corridor filtering.
                if (added > 0 && cfg.Num("objects", "precise_colliders") != 0 && objs.PreciseFitted.Add(inst["id"]!.GetValue<string>()))
                {
                    var triangles = Enumerable.Range(0, mesh.Idx.Length / 3)
                        .Where(t => tmat[t] is not null && (drop is null || !drop[t]))
                        .SelectMany(t => new[] { w[mesh.Idx[t * 3]], w[mesh.Idx[t * 3 + 1]], w[mesh.Idx[t * 3 + 2]] }).ToArray();
                    foreach (var shape in shapes.ToArray().Where(s => s.Box is not null))
                    {
                        var replacements = PreciseColliders.Split(shape, triangles, fence ? 10 : cfg.Num("objects", "precise_cell_m") * 100, fence ? shape.Box!.Z + 100 : cfg.Num("objects", "precise_band_m") * 100, fence ? 4096 : (int)cfg.Num("objects", "precise_max_boxes"));
                        if (replacements is null) { preciseKept++; continue; }
                        objs.Kept.Remove(shape); objs.Kept.AddRange(replacements);
                        shapes.Remove(shape); shapes.AddRange(replacements);
                        preciseReplaced++; preciseBoxes += replacements.Count;
                    }
                }
                // Retained capsules can exceed the fitted render trunk; show their bark explicitly.
                if (tree) foreach (var shape in shapes.Where(s => s.Capsule is not null)) DrawShape(shape);
                if (midTier) { midTris += added; midDrawn++; midPer[tname] = midPer.GetValueOrDefault(tname) + 1; }
                else { realTris += added; realDrawn++; realPer[tname] = realPer.GetValueOrDefault(tname) + 1; }
            }
            else
            if (tree && shapes.Count > 0)
            {
                treeInstances++;
                int cardsBefore = cardsDrawn;
                counts["trunks"]++;
                string crown = "no crown";
                var card = TryCard(tname, mesh, m);
                if (card is not null) crown = card == true ? "billboard cards" : "billboard dropped (road)";
                else if (crowns && mesh is not null)
                {
                    if (Crown(mesh, m, Ue, objs.Probe.Road, cfg) is { } box) { AddOctahedron(sec["soft"], box); counts["soft"]++; crownsDrawn++; crown = "small crown"; }
                    else crownsDropped++;
                }
                // trunk shapes always drawn: the billboard may be shrunk or moved off the road, and the collider stays either way
                bool carded = cardsDrawn > cardsBefore;
                foreach (var s in shapes) DrawShape(s);
                perTemplate.TryAdd(tname, carded ? "tree: billboard cards and trunk shapes" : $"tree: trunk shapes, {crown}");
                cardedPer[tname] = cardedPer.GetValueOrDefault(tname) + 1;
            }
            else if (shapes.Count > 0 && mesh is not null && (fence || mesh.Idx.Length / 3 <= maxMeshTris)
                     && MeshDepth(mesh, m, Ue, objs.Probe, overhang) is var depth && (depth <= meshPenLimit || overheadRe?.IsMatch(tname) == true || Fallback(tname, depth)))
            {
                meshInstances++;
                bool[]? keepTri = null;
                if (depth > meshPenLimit)
                {
                    overheadInstances++;
                    var ow = mesh.Pos.Select(p => Ue(Vector3.Transform(p, m))).ToArray();
                    keepTri = new bool[mesh.Idx.Length / 3];
                    // a triangle on one of the object's own colliders (the legs) stays: that collision is real and must be seen
                    var own = shapes.Select(sh => ColliderBounds(sh)).ToList();
                    bool OnCollider(V3 c) => own.Any(b => c.X >= b.Item1.X - 30 && c.X <= b.Item2.X + 30 && c.Y >= b.Item1.Y - 30 && c.Y <= b.Item2.Y + 30 && c.Z >= b.Item1.Z - 30 && c.Z <= b.Item2.Z + 30);
                    for (int t = 0; t < keepTri.Length; t++)
                    {
                        V3 ta = ow[mesh.Idx[t * 3]], tb = ow[mesh.Idx[t * 3 + 1]], tc = ow[mesh.Idx[t * 3 + 2]];
                        keepTri[t] = OnCollider(new V3((ta.X + tb.X + tc.X) / 3, (ta.Y + tb.Y + tc.Y) / 3, (ta.Z + tb.Z + tc.Z) / 3))
                                     || objs.Probe.TriangleDepth(ta, tb, tc, 50, objs.Probe.HMin, overhang, surfaceOnly: false) <= meshPenLimit;
                        if (!keepTri[t]) overheadDropped++; else overheadSections.Add("objects:" + Dr2Prefix + mesh.TriMaterial[t]);
                    }
                }
                if (fitBoxes && overheadRe?.IsMatch(tname) != true)
                {
                    var vertices = mesh.Idx.Distinct().Select(i => Ue(Vector3.Transform(mesh.Pos[i], m))).ToArray();
                    foreach (var shape in shapes.Where(s => s.Box is not null))
                        if (ColliderFit.FitBox(shape, vertices, fitMax))
                        {
                            boxesFitted++;
                        }
                        else boxesKept++;
                }
                // Enclosure is independent of whether the bounded shrink was accepted.
                // Test the retained shape against this instance's actual mesh either way.
                if (cfg.Num("objects", "enclosed_interior") != 0)
                {
                    var triangles = mesh.Idx.Select(i => Ue(Vector3.Transform(mesh.Pos[i], m))).ToArray();
                    double coverage = cfg["objects"].TryGetValue("enclosed_min_coverage", out var emc) && emc is double emcd ? emcd : 0.9;
                    foreach (var shape in shapes.Where(s => s.Box is not null))
                        shape.EnclosedFittedInterior = ColliderFit.EnclosedBox(shape, triangles, coverage);
                }
                if (overheadRe?.IsMatch(tname) != true && cfg.Num("objects", "precise_colliders") != 0 && objs.PreciseFitted.Add(inst["id"]!.GetValue<string>()))
                {
                    var triangles = mesh.Idx.Select(i => Ue(Vector3.Transform(mesh.Pos[i], m))).ToArray();
                    foreach (var shape in shapes.ToArray().Where(s => s.Box is not null && !s.EnclosedFittedInterior))
                    {
                        var replacements = PreciseColliders.Split(shape, triangles, fence ? 10 : cfg.Num("objects", "precise_cell_m") * 100, fence ? shape.Box!.Z + 100 : cfg.Num("objects", "precise_band_m") * 100, fence ? 4096 : (int)cfg.Num("objects", "precise_max_boxes"));
                        if (replacements is null) { preciseKept++; continue; }
                        objs.Kept.Remove(shape); objs.Kept.AddRange(replacements);
                        shapes.Remove(shape); shapes.AddRange(replacements);
                        preciseReplaced++; preciseBoxes += replacements.Count;
                    }
                }
                var cls = shapes.GroupBy(s => ClassOf(s.Material)).OrderByDescending(g => g.Count()).First().Key;
                if (dr2Material is null) AddMesh(sec[cls], mesh, m, pl, Ue);
                else
                {
                    int n = AddMeshSplit(t => sec.GetValueOrDefault(Dr2Prefix + mesh.TriMaterial[t]) ?? sec[cls], t => sec.ContainsKey(Dr2Prefix + mesh.TriMaterial[t]), mesh, m, Ue, keepTri is null ? null : t => keepTri[t]);
                    dr2Tris += n; if (n > 0) dr2Instances++;
                }
                var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, Ue);
                foreach (var s in shapes)
                {
                    var ctr = s.Box?.Center ?? s.Capsule?.Center ?? s.FootprintUe.Aggregate((a, b) => a + b) * (1.0 / s.FootprintUe.Count);
                    alignChecked++;
                    // a collider reaching past the drawn mesh (cables, logs) would be invisible there: draw it too
                    var (smn, smx) = ColliderBounds(s);
                    if (!s.EnclosedFittedInterior && (smn.X < bmn.X - 30 || smx.X > bmx.X + 30 || smn.Y < bmn.Y - 30 || smx.Y > bmx.Y + 30 || smx.Z > bmx.Z + 30)) DrawShape(s);
                    if (ctr.X < bmn.X - 100 || ctr.X > bmx.X + 100 || ctr.Y < bmn.Y - 100 || ctr.Y > bmx.Y + 100 || ctr.Z < bmn.Z - 100 || ctr.Z > bmx.Z + 100)
                        misaligned++;
                }
                counts[cls]++;
                perTemplate.TryAdd(tname, $"render mesh ({mesh.Idx.Length / 3} triangles), {cls}");
            }
            else if (shapes.Count > 0)
            {
                proxyInstances++;
                foreach (var s in shapes) { DrawShape(s); counts[ClassOf(s.Material)]++; }
                perTemplate.TryAdd(tname, mesh is null ? "kept shapes (no render mesh)"
                    : mesh.Idx.Length / 3 > maxMeshTris ? $"kept shapes (render mesh {mesh.Idx.Length / 3} triangles > {maxMeshTris})"
                    : "kept shapes (render mesh reaches into the road corridor)");
            }
            else if (mesh is not null)
            {
                // no collider of its own: plants (soft vegetation, ferns, grass clumps) get their billboard when they have one.
                // Static structures (armco, walls, rock banks, fences, signs, houses) are often solid all the same: DR2 puts
                // their collision into the ground mesh (track.jpk, e.g. RBK+ rock banks), so they are drawn from their render
                // mesh, unless the name marks them as soft / light (not_solid_skip). No road-depth test: the corridor between DR2's
                // gate edges includes the verge, so signs, timing posts and the walls themselves measure 3-4 m "into the road"
                // (Argentina 01 r3), and the wall's collision is there whether it is drawn or not
                var plantCard = TryCard(tname, mesh, m);
                if (plantCard == true) undergrowth++;
                if (plantCard is null && notSolidSkip is not null && !notSolidSkip.IsMatch(tname) && mesh.Idx.Length / 3 <= notSolidMaxTris)
                {
                    staticDrawn++;
                    // own sections (StaticPrefix + the usual key, same material): their collision is DR2's ground, so
                    // Visuals.Check does not hold them to the road-corridor limit of geometry drawn without a collider
                    ProcMesh.Section Static(string key) => sec.TryGetValue(StaticPrefix + key, out var ss) ? ss : sec[StaticPrefix + key] = new ProcMesh.Section();
                    if (dr2Material is null) AddMesh(Static("other"), mesh, m, pl, Ue);
                    else
                    {
                        int n = AddMeshSplit(t => sec.ContainsKey(Dr2Prefix + mesh.TriMaterial[t]) ? Static(Dr2Prefix + mesh.TriMaterial[t]) : Static("other"), t => sec.ContainsKey(Dr2Prefix + mesh.TriMaterial[t]), mesh, m, Ue);
                        dr2Tris += n; if (n > 0) dr2Instances++;
                    }
                    perTemplate.TryAdd(tname, $"no collider (DR2 ground collision): render mesh ({mesh.Idx.Length / 3} triangles)");
                }
                else
                {
                    notSolid++;
                    perTemplate.TryAdd(tname, plantCard == true ? "not solid: billboard cards" : "not solid: not drawn");
                }
            }
            else noMesh++;

            // drawn plan extent of this instance, against its DR2 render bounds
            var dmn = new Vector3(float.MaxValue); var dmx = new Vector3(float.MinValue);
            foreach (var (cls, s) in sec.Where(kv => !kv.Key.StartsWith(CardPrefix)))   // cards: square images with transparent margins
                for (int i = before.GetValueOrDefault(cls); i < s.Pos.Count; i++) { dmn = Vector3.Min(dmn, s.Pos[i]); dmx = Vector3.Max(dmx, s.Pos[i]); }
            if (dmn.X <= dmx.X)
            {
                double ext = Math.Max(dmx.X - dmn.X, dmx.Y - dmn.Y) / 100;
                double bounds = double.NaN;
                if (mesh is not null) { var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, Ue); bounds = Math.Max(bmx.X - bmn.X, bmx.Y - bmn.Y) / 100; }
                extents.Add((tname, inst["id"]!.GetValue<string>(), ext, bounds));
            }
        }
        // kept colliders above car height over the road (ObjectColliders only checks the car-height band)
        var overhead = new JsonArray();
        // [objects] overhead_passable_above_m: an overhead structure's colliders over the road that start this high above the
        // centreline are left out. DR2's boxes approximate the arch tube below the drawn curve, and arches stand on crests where
        // the car leaves the ground: it was pushed down into the road at 6.2 km of Kailajärvi. Drawn and passable is not an invisible obstacle.
        double passableAbove = cfg["objects"].TryGetValue("overhead_passable_above_m", out var opa) && opa is double opad ? opad * 100 : double.PositiveInfinity;
        var passable = new List<ObjectColliders.Shape>();
        foreach (var s in objs.Kept)
        {
            double d = Math.Max(s.FootprintUe.Max(q => objs.Probe.Depth(q, s.FootprintRadius, objs.Probe.HMax, overhang)),
                                ShapeDepth(s, objs.Probe, objs.Probe.HMax, overhang));
            if (d <= meshPenLimit) continue;
            var lo = s.FootprintUe.Min(q => q.Z - objs.Probe.Road.QueryNear(q.X, q.Y).z) - s.FootprintRadius;
            if (overheadRe?.IsMatch(s.Template) == true && lo >= passableAbove) { passable.Add(s); continue; }
            var c = s.FootprintUe.Aggregate((a, b) => a + b) * (1.0 / s.FootprintUe.Count);
            double reach = s.FootprintUe.Max(q => (q - c).Length) + s.FootprintRadius;
            overhead.Add(new JsonObject
            {
                ["what"] = $"{s.Template} {s.Instance} {s.Kind} {s.Material}: {d / 100:0.0} m into the corridor, lowest {lo / 100:0.0} m above the centreline",
                ["at_cm"] = new JsonArray(Math.Round(c.X), Math.Round(c.Y), Math.Round(c.Z)), ["reach_cm"] = Math.Ceiling(reach),
            });
        }
        if (passable.Count > 0)
        {
            foreach (var s in passable) objs.Kept.Remove(s);
            Console.WriteLine($"objects: {passable.Count} overhead colliders over the road left out (lowest point at least {passableAbove / 100:0.0} m up): {string.Join(", ", passable.Select(s => s.Instance).Distinct())}");
        }
        var largest = extents.OrderByDescending(x => x.ext).Take(8).ToList();
        var overBounds = extents.Where(x => !double.IsNaN(x.bounds) && x.ext > x.bounds * maxOverBounds + 1).ToList();

        foreach (var s in sec.Values)
            for (int i = 0; i < s.Nrm.Count; i++) s.Nrm[i] = s.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(s.Nrm[i]);
        if (overheadInstances > 0) Console.WriteLine($"objects: overhead structures drawn with their DR2 mesh: {overheadInstances} instances, {overheadDropped} triangles inside the road corridor left out");
        Console.WriteLine($"objects: precise colliders: {preciseReplaced} boxes replaced by {preciseBoxes} boxes, {preciseKept} kept");
        Console.WriteLine($"objects: colliders fitted to render meshes: {boxesFitted} fitted, {boxesKept} kept (maximum shrink {fitMax:0} cm per side)");
        var r = new JsonObject
        {
            ["instances"] = instances, ["outside_physics_bounds"] = outside, ["trees"] = treeInstances,
            ["vegetation"] = vegetation,
            ["render_mesh_instances"] = meshInstances, ["shape_proxy_instances"] = proxyInstances,
            ["not_solid_not_drawn"] = notSolid,
            ["no_collider_static_drawn"] = staticDrawn,
            ["crowns_drawn"] = crownsDrawn, ["crowns_dropped"] = crownsDropped,
            ["billboard_cards_drawn"] = cardsDrawn, ["billboard_cards_dropped"] = cardsDropped, ["billboard_undergrowth"] = undergrowth, ["billboard_parallel_to_road"] = parallelCards,
            ["billboard_textures"] = new JsonArray(cardTex.Values.Where(x => x is not null).Distinct().Order().Select(x => (JsonNode)x!).ToArray()),
            ["render_mesh_road_fallback"] = new JsonObject(fallbacks.Select(kv => KeyValuePair.Create(kv.Key,
                (JsonNode?)$"{kv.Value.n} (render mesh {kv.Value.depth / 100:0.00} m into the corridor -> shapes)"))),
            ["without_render_mesh_or_shapes"] = noMesh,
            ["canopies"] = $"{canopyMode}: {canopiesClipped} drawn up to {canopyClip / 100:0.#} m above the tree base, {canopiesHidden} not drawn"
                           + (canopyColliderTops.Count > 0 ? $"; their colliders (kept, DR2 sizes) reach {canopyColliderTops.Average() / 100:0.0} m above the base on average, {canopyColliderTops.Max() / 100:0.0} m max" : ""),
            ["canopy_drawn_span"] = canopySpans.Count == 0 ? null : new JsonObject
            {
                ["max_m"] = Math.Round(canopySpans.Max(x => x.span) / 100, 2),
                ["over_1_5_m"] = canopySpans.Count(x => x.span > 150),
                ["over_3_m"] = canopySpans.Count(x => x.span > 300),
                ["widest"] = new JsonArray(canopySpans.OrderByDescending(x => x.span).Take(5).Select(x => (JsonNode)$"{x.who}: {x.span / 100:0.0} m").ToArray()),
            },
            ["other_cones_drawn_whole"] = new JsonObject
            {
                ["count"] = flareSpans.Count,
                ["max_m"] = flareSpans.Count == 0 ? 0 : Math.Round(flareSpans.Max(x => x.span) / 100, 2),
                ["over_1_5_m"] = flareSpans.Count(x => x.span > 150),
                ["widest"] = new JsonArray(flareSpans.OrderByDescending(x => x.span).Take(5).Select(x => (JsonNode)$"{x.who}: {x.span / 100:0.0} m").ToArray()),
            },
            ["shapes_vs_colliders"] = new JsonObject
            {
                ["compared"] = shapesCompared, ["mismatches"] = shapeMismatches.Count,
                ["max_outside_collider_cm"] = Math.Round(worstOutside, 1), ["min_fill"] = Math.Round(worstFill, 3),
                ["first"] = new JsonArray(shapeMismatches.Take(10).Select(x => (JsonNode)x).ToArray()),
            },
            ["colliders_overhead_in_corridor"] = overhead,
            ["largest_extent_m"] = largest.Count > 0 ? Math.Round(largest[0].ext, 2) : 0,
            ["largest"] = new JsonArray(largest.Select(x => (JsonNode)$"{x.template} {x.id}: {x.ext:0.0} m (DR2 bounds {x.bounds:0.0} m)").ToArray()),
            ["over_bounds"] = overBounds.Count,
            ["over_bounds_first"] = new JsonArray(overBounds.Take(8).Select(x => (JsonNode)$"{x.template} {x.id}: {x.ext:0.0} m vs bounds {x.bounds:0.0} m").ToArray()),
            ["collider_centres_checked_against_render_bounds"] = alignChecked,
            ["collider_centres_outside_render_bounds_plus_1m"] = misaligned,
            ["solid_instances_drawn"] = treeInstances + meshInstances + proxyInstances + instancedSolid,
            ["instanced_trees"] = new JsonObject { ["templates"] = instanced?.Count ?? 0, ["placements"] = instancedPlacements, ["with_collider"] = instancedSolid },
            ["solid_instances_total"] = objs.Kept.Select(s => s.Instance).Distinct().Count(),
            ["sections"] = new JsonObject(Classes.Where(c => sec[c].Idx.Count > 0).Select(c => KeyValuePair.Create(c,
                (JsonNode?)$"{counts[c]} objects, {sec[c].Pos.Count} vertices, {sec[c].Idx.Count / 3} triangles"))),
            ["triangles"] = sec.Values.Sum(s => s.Idx.Count / 3),
            ["dr2_material_sections"] = dr2Material is null ? null : new JsonObject
            {
                ["sections"] = sec.Count(kv => kv.Key.StartsWith(Dr2Prefix) && kv.Value.Idx.Count > 0),
                ["instances"] = dr2Instances, ["triangles"] = dr2Tris,
            },
            ["templates"] = new JsonObject(perTemplate.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        r["collider_fit"] = r0Fit;
        // sections of overhead structures (start / finish arches): kept triangles lie on their own colliders or outside the corridor
        r["overhead_sections"] = new JsonArray(overheadSections.Select(x => (JsonNode?)x).ToArray());
        r["real_trees"] = new JsonObject
        {
            ["near_m"] = nearM, ["near_max_triangles"] = nearBudget == int.MaxValue ? null : nearBudget,
            ["candidates_within_near_m"] = nearCandidates, ["candidate_triangles"] = nearCandidateTris,
            ["drawn_real"] = realDrawn, ["triangles"] = realTris, ["clipped_for_road"] = realClipped, ["triangles_left_out_for_road"] = realClippedTris,
            ["drawn_carded_or_stub"] = cardedPer.Values.Sum(),
            ["mid_m"] = midM, ["mid_max_triangles"] = midBudget == int.MaxValue ? null : midBudget, ["mid_ratio"] = midRatio,
            ["drawn_mid"] = midDrawn, ["mid_triangles"] = midTris,
            ["mid_mesh_triangles"] = new JsonObject(midMeshes.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"{meshes[kv.Key].TreeMat!.Count(x => x is not null)} -> {kv.Value!.Idx.Length / 3}"))),
            ["per_template_real"] = new JsonObject(realPer.Keys.Union(midPer.Keys).Union(cardedPer.Keys).Select(k => KeyValuePair.Create(k, (JsonNode?)$"{realPer.GetValueOrDefault(k)} real, {midPer.GetValueOrDefault(k)} mid, {cardedPer.GetValueOrDefault(k)} far"))),
        };
        Console.WriteLine($"visuals: trees: {realDrawn} full ({realTris} triangles), {midDrawn} simplified ({midTris} triangles), {cardedPer.Values.Sum()} cards/stubs");
        r["trunk_draw_height_m"] = trunkDrawCm > 0 ? trunkDrawCm / 100 : null;
        r["bark_sections"] = new JsonObject(sec.Where(kv => kv.Key.StartsWith(BarkPrefix) && kv.Value.Idx.Count > 0).OrderBy(kv => kv.Key)
            .Select(kv => KeyValuePair.Create(kv.Key[BarkPrefix.Length..], (JsonNode?)$"{kv.Value.Idx.Count / 3} triangles")));
        return (sec, r, Bark);
    }

    /// Round 2b: DR2's ornaments (the rocks stage.json "ornaments" lists, ornaments.glb prototypes placed like the objects) drawn
    /// as visuals only: DR2's collision already covers them. Instances within [ornaments] max_distance_m (plan) of the centreline
    /// are placed from prototypes decimated once to decimate_ratio of their triangles (GroundLod.SimplifyToCount, UVs kept);
    /// an instance whose mesh reaches into the road corridor beyond what the colliders may (as the objects' render meshes) is
    /// skipped. Sections are keyed by the section key dr2Material gives (null: that material's triangles are not drawn).
    /// Returns null when the IR has no ornaments.
    public static (Dictionary<string, ProcMesh.Section> sections, JsonObject report)? BuildOrnaments(
        StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, RoadProbe probe, Func<V3, bool> inPhysicsBounds,
        Func<string, string, int, string?> dr2Material)
    {
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["ornaments"];
        if (stage is null || cfg["ornaments"].Count == 0 || cfg.Num("ornaments", "enabled") == 0) return null;
        double maxDist = cfg.Num("ornaments", "max_distance_m"), ratio = cfg.Num("ornaments", "decimate_ratio");
        int minTris = (int)cfg.Num("ornaments", "min_triangles");
        var glbPath = Path.Combine(ir.Dir, stage["file"]?.GetValue<string>() ?? "render/ornaments.glb");
        var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)), maxDist + 50);
        double overhang = cfg.Num("objects", "overhang_height_m") * 100;
        double meshPenLimit = probe.MaxPen + cfg.Num("objects", "road_overlap_tolerance_m") * 100;
        V3 Ue(Vector3 dr2) => pl.ToUe([dr2.X, dr2.Y, dr2.Z]);

        var prims = new Glb(glbPath).Primitives(withUv: true).GroupBy(p => p.Mesh).ToDictionary(g => g.Key, g => g.ToList());
        var sec = new Dictionary<string, ProcMesh.Section>();
        var protos = new Dictionary<string, Mesh?>();
        int trisBefore = 0, trisAfter = 0, untextured = 0;
        var perProto = new SortedDictionary<string, string>();
        Mesh? Proto(string template)
        {
            if (protos.TryGetValue(template, out var known)) return known;
            if (!prims.TryGetValue(template, out var list)) return protos[template] = null;
            var pos = new List<Vector3>(); var idx = new List<uint>(); var uv = new List<Vector2>(); var triMat = new List<string>();
            var matTris = new Dictionary<string, int>();
            int before = 0, after = 0;
            foreach (var p in list)
            {
                int nTri = p.Indices.Length / 3;
                var key = dr2Material(template, p.Material, nTri);
                before += nTri;
                if (key is null) { untextured += nTri; continue; }
                var pp = new List<Vector3>(); var pu = new List<Vector2>(); var pi = p.Indices.ToList();
                var t0 = p.Uv.Length > 0 ? p.Uv[0] : null;
                for (int i = 0; i < p.Positions.Length / 3; i++)
                {
                    pp.Add(new Vector3(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]) * 100);   // m -> cm for the simplifier
                    pu.Add(t0 is null ? Vector2.Zero : new Vector2(t0[i * 2], t0[i * 2 + 1]));
                }
                List<Vector3> sp = pp; List<uint> si = pi; List<Vector2> su = pu;
                if (ratio < 1 && nTri > minTris)
                    (sp, si, su, _) = GroundLod.SimplifyToCount(pp, pi, pu, Math.Max(minTris, (int)Math.Ceiling(nTri * ratio)));
                uint b = (uint)pos.Count;
                pos.AddRange(sp.Select(v => v / 100)); uv.AddRange(su); idx.AddRange(si.Select(i => i + b));
                triMat.AddRange(Enumerable.Repeat(key, si.Count / 3));
                matTris[key] = matTris.GetValueOrDefault(key) + si.Count / 3;
                sec.TryAdd(Dr2Prefix + key, new ProcMesh.Section());
                after += si.Count / 3;
            }
            trisBefore += before; trisAfter += after;
            perProto[template] = $"{before} -> {after} triangles";
            if (idx.Count == 0) return protos[template] = null;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in pos) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            return protos[template] = new Mesh(pos.ToArray(), idx.ToArray(), mn, mx, uv.ToArray(), triMat.ToArray(), matTris);
        }

        int clipped = 0, clippedTris = 0; double deepestClipped = 0;
        int total = 0, far = 0, outside = 0, noMesh = 0, drawn = 0, skippedRoad = 0, tris = 0;
        double deepestKept = 0, deepestSkipped = 0;
        var skipped = new List<string>();
        foreach (var inst in stage["instances"]!.AsArray())
        {
            total++;
            var tname = inst!["template"]!.GetValue<string>();
            var m = ObjectColliders.Trs(inst["transform"]);
            var origin = Vector3.Transform(Vector3.Zero, m);
            var ue = Ue(origin);
            if (route.Metres(new Vector2((float)ue.X, (float)ue.Y)) > maxDist) { far++; continue; }
            if (!inPhysicsBounds(pl.ToPhysics(origin.X, origin.Y, origin.Z))) { outside++; continue; }
            if (Proto(tname) is not { } mesh) { noMesh++; continue; }
            double depth = MeshDepth(mesh, m, Ue, probe, overhang);
            bool[]? drop = null;
            if (depth > meshPenLimit)
            {
                // beside the road: only the triangles that would exceed the limit are left out, the rest of the rock is drawn
                var w = mesh.Pos.Select(p => Ue(Vector3.Transform(p, m))).ToArray();
                drop = new bool[mesh.Idx.Length / 3];
                int dropped = 0;
                for (int t = 0; t < drop.Length; t++)
                    if (probe.TriangleDepth(w[mesh.Idx[t * 3]], w[mesh.Idx[t * 3 + 1]], w[mesh.Idx[t * 3 + 2]], 50, probe.HMin, overhang) > meshPenLimit) { drop[t] = true; dropped++; }
                if (dropped == drop.Length)
                {
                    skippedRoad++; deepestSkipped = Math.Max(deepestSkipped, depth);
                    if (skipped.Count < 8) skipped.Add($"{tname} {inst["id"]}: {depth / 100:0.00} m into the corridor, nothing left");
                    continue;
                }
                clipped++; clippedTris += dropped; deepestClipped = Math.Max(deepestClipped, depth);
            }
            else deepestKept = Math.Max(deepestKept, depth);
            tris += AddMeshSplit(t => sec[Dr2Prefix + mesh.TriMaterial[t]], _ => true, mesh, m, Ue, drop is null ? null : t => !drop[t]);
            drawn++;
        }
        foreach (var s in sec.Values)
            for (int i = 0; i < s.Nrm.Count; i++) s.Nrm[i] = s.Nrm[i] == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(s.Nrm[i]);
        var r = new JsonObject
        {
            ["max_distance_m"] = maxDist, ["decimate_ratio"] = ratio,
            ["instances"] = total, ["beyond_max_distance"] = far, ["outside_physics_bounds"] = outside, ["without_mesh"] = noMesh,
            ["ornaments_drawn"] = drawn, ["ornament_triangles"] = tris,
            ["ornaments_clipped"] = clipped, ["clipped_triangles_left_out"] = clippedTris, ["clipped_instances_deepest_before_m"] = Math.Round(deepestClipped / 100, 2),
            ["skipped_road"] = skippedRoad,
            ["skipped_road_deepest_m"] = Math.Round(deepestSkipped / 100, 2), ["drawn_deepest_in_corridor_m"] = Math.Round(deepestKept / 100, 2),
            ["skipped_road_first"] = new JsonArray(skipped.Select(x => (JsonNode)x).ToArray()),
            ["prototypes_used"] = protos.Count(kv => kv.Value is not null), ["prototype_triangles_before"] = trisBefore, ["prototype_triangles_after"] = trisAfter,
            ["untextured_triangles_not_drawn"] = untextured,
            ["sections"] = sec.Count(kv => kv.Value.Idx.Count > 0),
            ["prototypes"] = new JsonObject(perProto.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        Console.WriteLine($"visuals: ornaments: {drawn} of {total} drawn within {maxDist:0.#} m ({tris} triangles; prototypes {trisBefore} -> {trisAfter}), {clipped} of them clipped at the road ({clippedTris} triangles left out), {skippedRoad} skipped for reaching the road, {far} too far");
        return (sec, r);
    }

    /// Clip height (UE cm) of a near-vertical, tall trunk capsule drawn up to `heightCm` above `baseZ` (null: the capsule's
    /// own lowest point); null if the shape is not one or trunk clipping is off (heightCm <= 0).
    internal static double? TrunkClipZ(ObjectColliders.Shape s, double heightCm, double? baseZ = null)
    {
        if (heightCm <= 0 || s.Capsule is not { Length: > 400 } c) return null;
        var (_, ay, _) = ObjectColliders.RotationAxes(c.Rot[0], c.Rot[1], c.Rot[2]);
        if (Math.Abs(ay.Z) < 0.85) return null;
        return (baseZ ?? c.Center.Z - Math.Abs(ay.Z) * (c.Length / 2 + c.Radius)) + heightCm;
    }

    /// Triangles cut at z = zMax (a polygon clip, the part below stays); open at the cut.
    internal static IEnumerable<(Vector3, Vector3, Vector3)> ClipTris(IEnumerable<(Vector3 a, Vector3 b, Vector3 c)> tris, float zMax)
    {
        foreach (var (a, b, c) in tris)
        {
            var poly = new List<Vector3>(4);
            Vector3[] v = [a, b, c];
            for (int i = 0; i < 3; i++)
            {
                Vector3 p = v[i], q = v[(i + 1) % 3];
                if (p.Z <= zMax) poly.Add(p);
                if ((p.Z <= zMax) != (q.Z <= zMax)) poly.Add(p + (q - p) * ((zMax - p.Z) / (q.Z - p.Z)));
            }
            for (int i = 1; i + 1 < poly.Count; i++) yield return (poly[0], poly[i], poly[i + 1]);
        }
    }

    /// Cuts the triangles added to a section since (vertex `fromV`, index `fromI`) at z = zMax (flat-shaded, three own vertices each).
    static void ClipAbove(ProcMesh.Section s, int fromV, int fromI, double zMax)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>(); var nrm = new List<Vector3>();
        for (int t = fromI; t + 2 < s.Idx.Count; t += 3)
        {
            tris.Add((s.Pos[(int)s.Idx[t]], s.Pos[(int)s.Idx[t + 1]], s.Pos[(int)s.Idx[t + 2]])); nrm.Add(s.Nrm[(int)s.Idx[t]]);
        }
        s.Pos.RemoveRange(fromV, s.Pos.Count - fromV); s.Nrm.RemoveRange(fromV, s.Nrm.Count - fromV); s.Uv.RemoveRange(fromV, s.Uv.Count - fromV);
        s.Idx.RemoveRange(fromI, s.Idx.Count - fromI);
        for (int k = 0; k < tris.Count; k++)
            foreach (var (a, b, c) in ClipTris([(tris[k].Item1, tris[k].Item2, tris[k].Item3)], (float)zMax))
            {
                int i0 = s.Pos.Count;
                foreach (var p in new[] { a, b, c }) { s.Pos.Add(p); s.Nrm.Add(nrm[k]); s.Uv.Add(Vector2.Zero); }
                s.Idx.AddRange([(uint)i0, (uint)i0 + 1, (uint)i0 + 2]);
            }
    }

    /// Deepest reach of a collider shape's hull (as drawn) into the road corridor within [hMin, hMax] over road surface.
    /// Footprint samples alone miss leaning cones and boxes whose corners are below and above the band.
    public static double ShapeDepth(ObjectColliders.Shape sh, RoadProbe probe, double hMin, double hMax)
    {
        var s = new ProcMesh.Section();
        AddShape(s, sh, 8, null!);
        double best = 0;
        for (int t = 0; t + 2 < s.Idx.Count; t += 3)
        {
            V3 P(int k) { var v = s.Pos[(int)s.Idx[t + k]]; return new(v.X, v.Y, v.Z); }
            best = Math.Max(best, probe.TriangleDepth(P(0), P(1), P(2), 50, hMin, hMax));
        }
        return best;
    }

    /// Kept collider shape in UE cm: box (full extents along the rotator axes), capsule as an n-sided prism over its full
    /// length along local Y, cone from its physics vertices.
    static void AddShape(ProcMesh.Section s, ObjectColliders.Shape sh, int sides, StageBuild.Placement pl)
    {
        if (sh.Box is { } b)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
            var c = b.Center;
            var corners = new List<V3>();
            foreach (var (i, j, k) in new[] { (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1) })
                corners.Add(c + ax * (i * b.X / 2) + ay * (j * b.Y / 2) + az * (k * b.Z / 2));
            AddHull(s, corners, [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)], c);
        }
        else if (sh.Capsule is { } cp)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(cp.Rot[0], cp.Rot[1], cp.Rot[2]);
            double half = cp.Length / 2;
            var pts = new List<V3>();
            foreach (var end in new[] { -half, half })
                for (int i = 0; i < sides; i++)
                {
                    double a = 2 * Math.PI * i / sides;
                    pts.Add(cp.Center + ay * end + ax * (Math.Cos(a) * cp.Radius) + az * (Math.Sin(a) * cp.Radius));
                }
            var faces = new List<(int, int, int, int)>();
            for (int i = 0; i < sides; i++) faces.Add((i, (i + 1) % sides, sides + (i + 1) % sides, sides + i));
            AddHull(s, pts, faces, cp.Center);
            // pointed ends inside the hemispheres, so a tilted capsule is not drawn wider than it is
            V3 tipLo = cp.Center + ay * -(half + cp.Radius), tipHi = cp.Center + ay * (half + cp.Radius);
            for (int i = 0; i < sides; i++)
            {
                int n = (i + 1) % sides;
                Tri(s, pts[i], pts[n], tipLo, cp.Center); Tri(s, pts[sides + i], pts[sides + n], tipHi, cp.Center);
            }
        }
        else if (sh.MeshPhysics is { } mp && sh.MeshTris is { } mt)
        {
            var ue = mp.Select(p => new V3(p.X * 100, p.Z * 100, p.Y * 100)).ToList();
            var ctr = new V3(ue.Average(v => v.X), ue.Average(v => v.Y), ue.Average(v => v.Z));
            for (int t = 0; t + 2 < mt.Count; t += 6) Tri(s, ue[(int)mt[t]], ue[(int)mt[t + 1]], ue[(int)mt[t + 2]], ctr);
        }
    }

    static V3 PhysToUe(Vector3 p) => new(p.X * 100, p.Z * 100, p.Y * 100);

    /// DR2 cone colliders on trees with the apex below the base are canopies (pohutukawa: ~2.8 m radius, 6.4 m tall,
    /// apex ~1.5 m above the ground); upright cones are trunk flares.
    static bool IsCanopy(ObjectColliders.Shape sh)
    {
        if (sh.MeshPhysics is not { Count: > 1 } mp) return false;
        float ringY = mp.Take(mp.Count - 1).Average(p => p.Y);
        return mp[^1].Y < ringY;
    }

    /// Part of an inverted cone below `topZ` (UE cm): apex up to the cut, capped. False if the apex is above the cut.
    /// clip: each ring point above the cut is pulled down the cone to it; ring points already below stay, so a tilted
    /// cone keeps its low side at full radius (metre-wide panels). apex: the whole cone is scaled toward its apex until
    /// every ring point is at or below the cut and within maxRadius of the apex in plan (tilted cones stay as narrow as
    /// upright ones); cones tilted past their
    /// half angle (a ring point not above the apex) are not drawn.
    static bool AddClippedCone(ProcMesh.Section s, ObjectColliders.Shape sh, double topZ, bool uniform = false, double maxRadius = double.MaxValue)
    {
        var ue = sh.MeshPhysics!.Select(PhysToUe).ToList();
        var apex = ue[^1];
        if (apex.Z >= topZ) return false;
        List<V3> ring;
        if (uniform)
        {
            var rim = ue.Take(ue.Count - 1).ToList();
            if (rim.Any(r => r.Z <= apex.Z + 1)) return false;
            double t = Math.Min(1, rim.Min(r => (topZ - apex.Z) / (r.Z - apex.Z)));
            double reach = rim.Max(r => Math.Sqrt((r.X - apex.X) * (r.X - apex.X) + (r.Y - apex.Y) * (r.Y - apex.Y)));
            if (reach * t > maxRadius) t = maxRadius / reach;
            ring = rim.Select(r => apex + (r - apex) * t).ToList();
        }
        else ring = ue.Take(ue.Count - 1).Select(r => r.Z <= topZ ? r : apex + (r - apex) * ((topZ - apex.Z) / (r.Z - apex.Z))).ToList();
        var ctr = apex * 0.25 + ring.Aggregate((a, b) => a + b) * (0.75 / ring.Count);
        for (int i = 0; i < ring.Count; i++)
        {
            var n = ring[(i + 1) % ring.Count];
            Tri(s, apex, ring[i], n, ctr);
            if (i > 0 && i + 1 < ring.Count) Tri(s, ring[0], ring[i], n, ctr);
        }
        return true;
    }

    /// UE-space bounds of the collider itself, from its definition rather than the drawn hull: box corners, capsule
    /// segment +- radius, cone vertices.
    static (Vector3 mn, Vector3 mx) ColliderBounds(ObjectColliders.Shape sh)
    {
        V3 mn, mx;
        if (sh.Box is { } b)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
            var e = Abs(ax) * (b.X / 2) + Abs(ay) * (b.Y / 2) + Abs(az) * (b.Z / 2);
            (mn, mx) = (b.Center - e, b.Center + e);
        }
        else if (sh.Capsule is { } cp)
        {
            var (_, ay, _) = ObjectColliders.RotationAxes(cp.Rot[0], cp.Rot[1], cp.Rot[2]);
            var e = Abs(ay) * (cp.Length / 2) + new V3(cp.Radius, cp.Radius, cp.Radius);
            (mn, mx) = (cp.Center - e, cp.Center + e);
        }
        else
        {
            var ue = sh.MeshPhysics!.Select(PhysToUe).ToList();
            mn = new V3(ue.Min(v => v.X), ue.Min(v => v.Y), ue.Min(v => v.Z));
            mx = new V3(ue.Max(v => v.X), ue.Max(v => v.Y), ue.Max(v => v.Z));
        }
        return (new Vector3((float)mn.X, (float)mn.Y, (float)mn.Z), new Vector3((float)mx.X, (float)mx.Y, (float)mx.Z));
        static V3 Abs(V3 v) => new(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
    }

    static void AddHull(ProcMesh.Section s, List<V3> p, List<(int a, int b, int c, int d)> quads, V3 centre)
    {
        foreach (var (a, b, c, d) in quads) { Tri(s, p[a], p[b], p[c], centre); Tri(s, p[a], p[c], p[d], centre); }
    }

    /// Triangle facing away from `inside` (UE front face: cross(b - a, c - a) points against the normal).
    static void Tri(ProcMesh.Section s, V3 a, V3 b, V3 c, V3 inside)
    {
        Vector3 A = new((float)a.X, (float)a.Y, (float)a.Z), B = new((float)b.X, (float)b.Y, (float)b.Z), C = new((float)c.X, (float)c.Y, (float)c.Z);
        var cr = Vector3.Cross(B - A, C - A);
        var outward = (A + B + C) / 3 - new Vector3((float)inside.X, (float)inside.Y, (float)inside.Z);
        if (Vector3.Dot(cr, outward) > 0) { (B, C) = (C, B); cr = -cr; }
        var n = cr.LengthSquared() > 0 ? Vector3.Normalize(-cr) : Vector3.UnitZ;
        int i0 = s.Pos.Count;
        foreach (var v in new[] { A, B, C }) { s.Pos.Add(v); s.Nrm.Add(n); s.Uv.Add(Vector2.Zero); }
        s.Idx.AddRange([(uint)i0, (uint)i0 + 1, (uint)i0 + 2]);
    }

    /// Render mesh placed by the instance transform. glTF front faces are CCW (outward cross product); the DR2 -> UE map
    /// mirrors, so each triangle's order is fixed against its outward normal mapped into UE.
    static void AddMesh(ProcMesh.Section s, Mesh mesh, Matrix4x4 m, StageBuild.Placement pl, Func<Vector3, V3> ue)
    {
        int b = s.Pos.Count;
        var w = new Vector3[mesh.Pos.Length];
        for (int i = 0; i < mesh.Pos.Length; i++)
        {
            w[i] = Vector3.Transform(mesh.Pos[i], m);
            var u = ue(w[i]);
            s.Pos.Add(new Vector3((float)u.X, (float)u.Y, (float)u.Z)); s.Nrm.Add(Vector3.Zero); s.Uv.Add(Vector2.Zero);
        }
        for (int t = 0; t + 2 < mesh.Idx.Length; t += 3)
        {
            uint i0 = mesh.Idx[t], i1 = mesh.Idx[t + 1], i2 = mesh.Idx[t + 2];
            var outDr2 = Vector3.Cross(w[i1] - w[i0], w[i2] - w[i0]);
            if (outDr2.LengthSquared() == 0) continue;
            var o0 = ue(w[i0]); var o1 = ue(w[i0] + Vector3.Normalize(outDr2));
            var outUe = new Vector3((float)(o1.X - o0.X), (float)(o1.Y - o0.Y), (float)(o1.Z - o0.Z));
            int a = b + (int)i0, bb = b + (int)i1, c = b + (int)i2;
            var cr = Vector3.Cross(s.Pos[bb] - s.Pos[a], s.Pos[c] - s.Pos[a]);
            if (Vector3.Dot(cr, outUe) > 0) (bb, c) = (c, bb);
            s.Nrm[a] += outUe; s.Nrm[bb] += outUe; s.Nrm[c] += outUe;
            s.Idx.AddRange([(uint)a, (uint)bb, (uint)c]);
        }
    }

    /// AddMesh with each triangle routed to target(t); vertices are duplicated per target section and carry the glb UV.
    /// Returns the number of triangles that went to a section where dr2(t) holds.
    static int AddMeshSplit(Func<int, ProcMesh.Section> target, Func<int, bool> dr2, Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, Func<int, bool>? keep = null)
    {
        var w = new Vector3[mesh.Pos.Length];
        var u = new Vector3[mesh.Pos.Length];
        for (int i = 0; i < mesh.Pos.Length; i++) { w[i] = Vector3.Transform(mesh.Pos[i], m); var q = ue(w[i]); u[i] = new((float)q.X, (float)q.Y, (float)q.Z); }
        var maps = new Dictionary<ProcMesh.Section, Dictionary<uint, int>>();
        int n = 0;
        for (int t = 0; t + 2 < mesh.Idx.Length; t += 3)
        {
            uint i0 = mesh.Idx[t], i1 = mesh.Idx[t + 1], i2 = mesh.Idx[t + 2];
            var outDr2 = Vector3.Cross(w[i1] - w[i0], w[i2] - w[i0]);
            if (outDr2.LengthSquared() == 0 || keep is not null && !keep(t / 3)) continue;
            var s = target(t / 3);
            if (!maps.TryGetValue(s, out var map)) maps[s] = map = new();
            int V(uint i)
            {
                if (map.TryGetValue(i, out var k)) return k;
                s.Pos.Add(u[i]); s.Nrm.Add(Vector3.Zero); s.Uv.Add(mesh.Uv[i]);
                return map[i] = s.Pos.Count - 1;
            }
            var o0 = ue(w[i0]); var o1 = ue(w[i0] + Vector3.Normalize(outDr2));
            var outUe = new Vector3((float)(o1.X - o0.X), (float)(o1.Y - o0.Y), (float)(o1.Z - o0.Z));
            int a = V(i0), bb = V(i1), c = V(i2);
            var cr = Vector3.Cross(s.Pos[bb] - s.Pos[a], s.Pos[c] - s.Pos[a]);
            if (Vector3.Dot(cr, outUe) > 0) (bb, c) = (c, bb);
            s.Nrm[a] += outUe; s.Nrm[bb] += outUe; s.Nrm[c] += outUe;
            s.Idx.AddRange([(uint)a, (uint)bb, (uint)c]);
            if (dr2(t / 3)) n++;
        }
        return n;
    }

    /// UE-space axis-aligned box of a local box under the instance transform.
    static (V3 mn, V3 mx) WorldBox(Vector3 lmn, Vector3 lmx, Matrix4x4 m, Func<Vector3, V3> ue)
    {
        var mn = new V3(double.MaxValue, double.MaxValue, double.MaxValue); var mx = new V3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var x in new[] { lmn.X, lmx.X }) foreach (var y in new[] { lmn.Y, lmx.Y }) foreach (var z in new[] { lmn.Z, lmx.Z })
        {
            var u = ue(Vector3.Transform(new Vector3(x, y, z), m));
            mn = new V3(Math.Min(mn.X, u.X), Math.Min(mn.Y, u.Y), Math.Min(mn.Z, u.Z));
            mx = new V3(Math.Max(mx.X, u.X), Math.Max(mx.Y, u.Y), Math.Max(mx.Z, u.Z));
        }
        return (mn, mx);
    }

    /// Crowns mode: a small octahedron high in the tree, centred over the render bounds; radius at most a fraction of the
    /// smaller plan extent of the scaled DR2 bounds, shrunk to keep crown_road_margin_m from the road edges (null:
    /// dropped). DR2 local Y is up.
    static (V3 mn, V3 mx)? Crown(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadCorridor road, Toml cfg)
    {
        var (wmn, wmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        double sx = Vector3.TransformNormal(Vector3.UnitX, m).Length(), sz = Vector3.TransformNormal(Vector3.UnitZ, m).Length();
        double plan = Math.Min((mesh.Max.X - mesh.Min.X) * sx, (mesh.Max.Z - mesh.Min.Z) * sz) * 100;
        double r = Math.Min(cfg.Num("objects", "crown_max_radius_m") * 100, cfg.Num("objects", "crown_radius_factor") * plan / 2);
        double bottom = wmn.Z + (wmx.Z - wmn.Z) * cfg.Num("objects", "crown_bottom_fraction");
        if (bottom - wmn.Z < cfg.Num("objects", "crown_min_bottom_m") * 100) return null;
        var c = ue(Vector3.Transform((mesh.Min + mesh.Max) / 2, m));
        double clearance = road.QueryNear(c.X, c.Y).clearance - cfg.Num("objects", "crown_road_margin_m") * 100;
        r = Math.Min(r, clearance);
        if (r < cfg.Num("objects", "crown_min_radius_m") * 100) return null;
        return (new V3(c.X - r, c.Y - r, bottom), new V3(c.X + r, c.Y + r, wmx.Z));
    }

    /// Vegetation "cards": the tree's full height (render bounds) and a half-width of half its smaller crown extent (scaled),
    /// at most cards_max_radius_m, shrunk to keep crown_road_margin_m from the road edges in plan (null: under
    /// crown_min_radius_m, dropped). DR2 local Y is up.
    static ((V3 mn, V3 mx) box, double radius, double full)? CardBox(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadCorridor road, Toml cfg)
    {
        var (wmn, wmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        // a billboard view is a square image of the whole plant: the card is as wide as the plant is tall
        double full = Math.Min(cfg.Num("objects", "cards_max_radius_m") * 100, (wmx.Z - wmn.Z) / 2);
        var c = ue(Vector3.Transform(new Vector3((mesh.Min.X + mesh.Max.X) / 2, mesh.Min.Y, (mesh.Min.Z + mesh.Max.Z) / 2), m));
        double r = Math.Min(full, road.QueryNear(c.X, c.Y).clearance - cfg.Num("objects", "crown_road_margin_m") * 100);
        if (r < cfg.Num("objects", "crown_min_radius_m") * 100) return null;
        return ((new V3(c.X - r, c.Y - r, wmn.Z), new V3(c.X + r, c.Y + r, wmx.Z)), r, full);
    }

    /// Two vertical cards crossing at the box centre, `yaw` and yaw + 90°, each with one view of the billboard strip
    /// (frames views side by side, v = 0 at the top): view 0 and the view a quarter turn on. A card narrowed to r below its
    /// full half-width (road clearance) shows the middle r / full of the view instead of squeezing all of it.
    static void AddCards(ProcMesh.Section s, (V3 mn, V3 mx) box, double r, double full, double yaw, int frames)
    {
        AddCard(s, box, r, full, yaw, 0, frames);
        AddCard(s, box, r, full, yaw + Math.PI / 2, Math.Max(1, frames / 4) % Math.Max(1, frames), frames);
    }

    /// One vertical card through the box centre at `yaw`, half-width r, showing view `view` of the strip (cropped to r / full).
    static void AddCard(ProcMesh.Section s, (V3 mn, V3 mx) box, double r, double full, double yaw, int view, int frames)
    {
        float crop = (float)Math.Clamp(r / Math.Max(1, full), 0, 1);
        var c = (box.mn + box.mx) * 0.5;
        float z0 = (float)box.mn.Z, z1 = (float)box.mx.Z;
        var d = new Vector3((float)(Math.Cos(yaw) * r), (float)(Math.Sin(yaw) * r), 0);
        var mid = new Vector3((float)c.X, (float)c.Y, 0);
        float uc = (view + 0.5f) / frames, uh = 0.5f / frames * crop;
        float u0 = uc - uh, u1 = uc + uh;
        var n = Vector3.Normalize(new Vector3(-d.Y, d.X, 0));
        int i0 = s.Pos.Count;
        foreach (var (p, uv) in new[] { (mid - d + new Vector3(0, 0, z0), new Vector2(u0, 1)), (mid + d + new Vector3(0, 0, z0), new Vector2(u1, 1)),
                                        (mid + d + new Vector3(0, 0, z1), new Vector2(u1, 0)), (mid - d + new Vector3(0, 0, z1), new Vector2(u0, 0)) })
        { s.Pos.Add(p); s.Nrm.Add(n); s.Uv.Add(uv); }
        s.Idx.AddRange([(uint)i0, (uint)i0 + 2, (uint)i0 + 1, (uint)i0, (uint)i0 + 3, (uint)i0 + 2]);
    }

    /// A single card along the road for a plant too close to it for a crossed pair: its direction is perpendicular to the
    /// road-clearance gradient at the plant, and its half-width shrinks until both ends and their midpoints keep
    /// cards_parallel_margin_m from the road in plan (null: the plant itself is closer than that, or under the minimum).
    static ((V3 mn, V3 mx) box, double radius, double full, double yaw)? ParallelCard(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadCorridor road, Toml cfg)
    {
        var (wmn, wmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        double full = Math.Min(cfg.Num("objects", "cards_max_radius_m") * 100, (wmx.Z - wmn.Z) / 2);
        var c = ue(Vector3.Transform(new Vector3((mesh.Min.X + mesh.Max.X) / 2, mesh.Min.Y, (mesh.Min.Z + mesh.Max.Z) / 2), m));
        double margin = cfg.Num("objects", "cards_parallel_margin_m") * 100, minR = cfg.Num("objects", "crown_min_radius_m") * 100;
        double Clear(double x, double y) => road.QueryNear(x, y).clearance;
        if (Clear(c.X, c.Y) < margin) return null;
        double gx = Clear(c.X + 50, c.Y) - Clear(c.X - 50, c.Y), gy = Clear(c.X, c.Y + 50) - Clear(c.X, c.Y - 50);
        double yaw = Math.Atan2(gx, -gy);   // perpendicular to the gradient: along the road
        double ca = Math.Cos(yaw), sa = Math.Sin(yaw);
        // every 25 cm along the card: on a tight bend the road can come closer between its ends than at them
        // (Finland 01 r3: a card 0.86 m into the corridor with only ends and midpoints tested)
        for (double r = full; r >= minR; r *= 0.8)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(2 * r / 25));
            if (Enumerable.Range(0, steps + 1).Select(i => -1 + 2.0 * i / steps).All(f => Clear(c.X + ca * r * f, c.Y + sa * r * f) >= margin))
                return ((new V3(c.X - r, c.Y - r, wmn.Z), new V3(c.X + r, c.Y + r, wmx.Z)), r, full, yaw);
        }
        return null;
    }

    /// Deepest reach of a placed render mesh into the road corridor (over road surface, car_height_min_m .. overhang).
    static double MeshDepth(Mesh mesh, Matrix4x4 m, Func<Vector3, V3> ue, RoadProbe probe, double overhang)
    {
        var (bmn, bmx) = WorldBox(mesh.Min, mesh.Max, m, ue);
        var bc = (bmn + bmx) * 0.5;
        if (probe.Road.QueryNear(bc.X, bc.Y).clearance > Math.Sqrt(Math.Pow(bmx.X - bmn.X, 2) + Math.Pow(bmx.Y - bmn.Y, 2)) / 2) return 0;
        var w = mesh.Pos.Select(p => ue(Vector3.Transform(p, m))).ToArray();
        double best = 0;
        for (int t = 0; t + 2 < mesh.Idx.Length; t += 3)
            best = Math.Max(best, probe.TriangleDepth(w[mesh.Idx[t]], w[mesh.Idx[t + 1]], w[mesh.Idx[t + 2]], 50, probe.HMin, overhang));
        return best;
    }

    static void AddOctahedron(ProcMesh.Section s, (V3 mn, V3 mx) box)
    {
        var (mn, mx) = box;
        var c = (mn + mx) * 0.5;
        var top = new V3(c.X, c.Y, mx.Z); var bot = new V3(c.X, c.Y, mn.Z);
        V3[] ring = [new(mn.X, c.Y, c.Z), new(c.X, mn.Y, c.Z), new(mx.X, c.Y, c.Z), new(c.X, mx.Y, c.Z)];
        for (int i = 0; i < 4; i++) { Tri(s, ring[i], ring[(i + 1) % 4], top, c); Tri(s, ring[i], ring[(i + 1) % 4], bot, c); }
    }
}

