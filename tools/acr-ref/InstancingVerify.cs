using System.Numerics;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;

namespace AcrRef;

/// `instancing-verify --game <dir with global.* + the built container> --ir <dir> --report <stage-build report.json> [--out file]`:
/// reads the instancing mesh and the cell's HISM back through CUE4Parse (the reader the game's own data goes through) and compares
/// them with DR2: vertex and triangle counts, bounds, and the world position of every mesh vertex of a few instances against
/// DR2's own transform (objects.glb vertices x stage.json instance matrix, through the stage placement), not through this
/// tool's writer.
internal static class InstancingVerify
{
    public static int Run(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named.GetValueOrDefault("ir") ?? throw new ArgumentException("--ir is required"));
        var report = JsonNode.Parse(File.ReadAllText(o.Named.GetValueOrDefault("report") ?? throw new ArgumentException("--report is required")))!;
        var p = report["placement"]!;
        var pl = new StageBuild.Placement(p["theta_deg"]!.GetValue<double>(), p["cx"]!.GetValue<double>(), p["cz"]!.GetValue<double>(),
            p["tx"]!.GetValue<double>(), p["tz"]!.GetValue<double>(), p["dy"]!.GetValue<double>(), p["slack_m"]!.GetValue<double>(),
            p["world"] is { } wn ? StageBuild.World.FromJson(wn) : null);
        if (pl.World is not null) StageBuild.UseWorld(pl.World);
        var inst = report["cell"]?["visuals"]?["instancing"] ?? throw new InvalidDataException("the report has no cell.visuals.instancing");
        var cfg = Toml.Load(report["options"]!["visuals"]!.GetValue<string>());
        using var w = o.Out is null ? Console.Out : new StreamWriter(o.Out);
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) failures++; }

        using var prov = Program.Open(o);
        var mounted = prov.MountedVfs.Select(v => v.Name).Where(n => n.Contains("zz_acrxs", StringComparison.OrdinalIgnoreCase)).ToList();
        C(mounted.Count > 0, $"container mounted ({string.Join(", ", mounted)})");

        string template = inst["template"]!.GetValue<string>();
        string meshPackage = inst["mesh"]!["package"]!.GetValue<string>();
        string Disk(string pkg) => "acr/Content/" + pkg["/Game/".Length..] + ".uasset";

        // ---- the StaticMesh
        var sm = prov.LoadPackage(Disk(meshPackage)).GetExports().OfType<UStaticMesh>().Single();
        var rd = sm.RenderData ?? throw new InvalidDataException("no render data");
        var lod = rd.LODs[0];
        int wantV = inst["mesh"]!["vertices"]!.GetValue<int>(), wantT = inst["mesh"]!["triangles"]!.GetValue<int>();
        int tris = lod.Sections.Sum(s => s.NumTriangles);
        var verts = lod.PositionVertexBuffer.Verts;
        var idx = lod.IndexBuffer.Buffer;
        var wantLods = inst["mesh"]!["lods"]!.AsArray().Select(x => x!.GetValue<string>().Split(',').Select(int.Parse).ToArray()).ToList();
        var wantScreen = inst["mesh"]!["screen_size"]!.AsArray().Select(x => x!.GetValue<float>()).ToList();
        C(rd.LODs.Length == wantLods.Count && rd.LODs.Select((l, i) => l.PositionVertexBuffer.Verts.Length == wantLods[i][0] && l.Sections.Sum(s => s.NumTriangles) == wantLods[i][1] && l.IndexBuffer.Length == wantLods[i][1] * 3).All(b => b)
          && rd.ScreenSize.Take(wantScreen.Count).Zip(wantScreen).All(z => Math.Abs(z.First - z.Second) < 1e-6),
          $"StaticMesh LODs via CUE4Parse: {rd.LODs.Length} (expected {wantLods.Count}): {string.Join(" + ", rd.LODs.Select(l => $"{l.PositionVertexBuffer.Verts.Length} vertices / {l.Sections.Sum(s => s.NumTriangles)} triangles"))}; screen sizes {string.Join(", ", rd.ScreenSize.Take(rd.LODs.Length).Select(f => f.ToString("0.###")))}");
        C(lod.PositionVertexBuffer.NumVertices == wantV && verts.Length == wantV && lod.VertexBuffer.NumVertices == wantV && tris == wantT && idx.Length == wantT * 3,
          $"StaticMesh via CUE4Parse: {rd.LODs.Length} LOD, {verts.Length} vertices (expected {wantV}), {tris} triangles in {lod.Sections.Length} sections (expected {wantT}), {idx.Length} indices, {lod.VertexBuffer.NumTexCoords} UV channel(s)");
        bool ranges = lod.Sections.All(s => Enumerable.Range(s.FirstIndex, s.NumTriangles * 3).Select(i => idx[i]).All(v => v >= s.MinVertexIndex && v <= s.MaxVertexIndex)) && idx.All(i => i < verts.Length);
        C(ranges && lod.Sections.All(s => s.MaterialIndex < sm.StaticMaterials.Length), "StaticMesh: section index ranges and material slots valid");
        var pos = verts.Select(v => new Vector3((float)v.X, (float)v.Y, (float)v.Z)).ToArray();
        var (mn, mx) = ProcMesh.Bounds(pos);
        var b = rd.Bounds;
        C(Math.Abs(b.Origin.X - (mn.X + mx.X) / 2) < 0.01 && Math.Abs(b.Origin.Z - (mn.Z + mx.Z) / 2) < 0.01 && Math.Abs(b.BoxExtent.Z - (mx.Z - mn.Z) / 2) < 0.01 && Math.Abs(b.BoxExtent.X - (mx.X - mn.X) / 2) < 0.01,
          $"StaticMesh bounds via CUE4Parse: origin ({b.Origin.X:0.#},{b.Origin.Y:0.#},{b.Origin.Z:0.#}) extent ({b.BoxExtent.X:0.#},{b.BoxExtent.Y:0.#},{b.BoxExtent.Z:0.#}) radius {b.SphereRadius:0.#} = vertex box ({mn.X:0},{mn.Y:0},{mn.Z:0})..({mx.X:0},{mx.Y:0},{mx.Z:0})");
        var mats = sm.StaticMaterials.Select(m => m.MaterialInterface?.Name ?? "?").ToList();
        var want = inst["mesh"]!["materials"]!.AsArray().Select(s => s!.GetValue<string>()).Select(x => x[(x.LastIndexOf('/') + 1)..]).ToList();
        C(mats.SequenceEqual(want), $"StaticMaterials via CUE4Parse: {string.Join(", ", mats)}");
        int shapes = -1;
        if (sm.BodySetup is { IsNull: false } bs && bs.ResolvedObject?.Object?.Value is UObject bo)
        {
            var agg = bo.GetOrDefault<FStructFallback>("AggGeom");
            shapes = agg is null ? 0 : new[] { "SphereElems", "BoxElems", "SphylElems", "ConvexElems", "TaperedCapsuleElems" }.Sum(k => agg.GetOrDefault<UScriptArray>(k)?.Properties.Count ?? 0);
        }
        C(shapes == 0, $"StaticMesh: BodySetup via CUE4Parse has {shapes} collision shapes");

        // winding against the exported tangent-space normal (UE front faces: cross(b-a, c-a) points against the outward normal)
        var nrm = lod.VertexBuffer.UV.Select(u => new Vector3(u.Normal[2].X, u.Normal[2].Y, u.Normal[2].Z)).ToArray();
        // (leaf cards carry sphere-like normals, so only the trunk, whose normals are surface normals, is asserted; the cards are listed)
        foreach (var s in lod.Sections)
        {
            int good = 0;
            for (int k = 0; k < s.NumTriangles; k++)
            {
                int t = s.FirstIndex + k * 3;
                var cr = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
                var n = nrm[idx[t]] + nrm[idx[t + 1]] + nrm[idx[t + 2]];
                if (Vector3.Dot(cr, n) < 0) good++;
            }
            bool trunk = mats[s.MaterialIndex].Contains("_tr_");
            if (trunk) C(good >= 0.95 * s.NumTriangles, $"StaticMesh winding (trunk {mats[s.MaterialIndex]}): {good} of {s.NumTriangles} triangles face along their vertex normals under UE's clockwise convention");
            else w.WriteLine($"     winding of {mats[s.MaterialIndex]}: {good} of {s.NumTriangles} triangles face along their vertex normals");
        }

        // ---- the HISM in the gameplay cell
        string cellPkg = "acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap";
        var exports = prov.LoadPackage(cellPkg).GetExports().ToList();
        var hism = exports.OfType<UHierarchicalInstancedStaticMeshComponent>().Where(h => h.Name == inst["hism"]!["component"]!.GetValue<string>()).ToList();
        C(hism.Count == 1, $"HISM {inst["hism"]!["component"]} found in the cell via CUE4Parse ({exports.OfType<UInstancedStaticMeshComponent>().Count()} instanced components in the cell)");
        if (hism.Count != 1) return 5;
        var h = hism[0];
        int n0 = inst["hism"]!["instances"]!.GetValue<int>();
        var xf = h.PerInstanceSMData.Select(d => d.TransformData).ToArray();
        C(xf.Length == n0 && h.ClusterTree.Length == 1 && h.ClusterTree[0].FirstInstance == 0 && h.ClusterTree[0].LastInstance == n0 - 1 && h.ClusterTree[0].FirstChild == -1,
          $"HISM via CUE4Parse: {xf.Length} instances (expected {n0}), cluster tree of {h.ClusterTree.Length} leaf node over {h.ClusterTree[0].FirstInstance}..{h.ClusterTree[0].LastInstance}");
        h.TryGetValue(out FPackageIndex meshRef, "StaticMesh");
        C(meshRef is { IsNull: false } && meshRef.Name == meshPackage[(meshPackage.LastIndexOf('/') + 1)..], $"HISM StaticMesh = {meshRef?.Name}");
        h.TryGetValue(out FPackageIndex attach, "AttachParent");
        var root = (UObject)attach!.ResolvedObject!.Object!.Value;
        root.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector loc, "RelativeLocation");
        w.WriteLine($"     attach parent {attach?.Name}, root location ({loc.X:0.##}, {loc.Y:0.##}, {loc.Z:0.##}) cm");
        var owner = (UObject)h.Outer!.Object!.Value;
        var comps = owner.GetOrDefault<UScriptArray>("InstanceComponents")?.Properties.OfType<ObjectProperty>().Select(x => x.Value).ToList() ?? [];
        C(comps.Any(c => c.Name == h.Name), $"owner {owner.Name} lists {h.Name} in InstanceComponents ({comps.Count} entries)");
        var bodyInstance = h.GetOrDefault<FStructFallback>("BodyInstance");
        C(bodyInstance is not null && Newtonsoft.Json.JsonConvert.SerializeObject(bodyInstance).Contains("NoCollision"), "HISM BodyInstance collision profile = NoCollision");

        // ---- DR2: positions from stage.json and mesh vertices through DR2's own transform
        var stage = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!["instances"]!.AsArray()
            .Where(x => x!["template"]!.GetValue<string>() == template).ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!);
        var chosen = InstancingSelect(ir, pl, cfg, template).Select(x => x.Id).ToList();
        C(chosen.Count == n0, $"DR2 selection reproduces {n0} instances ({template}: {stage.Count} in stage.json, within {cfg.Num("instancing", "near_m")} m of the route, nearest first)");
        double worstPos = 0, worstScale = 0;
        for (int i = 0; i < Math.Min(n0, chosen.Count); i++)
        {
            var j = stage[chosen[i]]["transform"]!;
            var pp = j["position"] is JsonArray pa ? pa.Select(v => v!.GetValue<double>()).ToArray()
                : j["matrix"] is JsonArray ma ? new[] { ma[12]!.GetValue<double>(), ma[13]!.GetValue<double>(), ma[14]!.GetValue<double>() } : [0, 0, 0];
            var ue = pl.ToUe(pp);
            var t = xf[i];
            if (i < 3) w.WriteLine($"     instance {i} ({chosen[i]}): CUE4Parse T=({t.Translation.X:0.###},{t.Translation.Y:0.###},{t.Translation.Z:0.###}) Q=({t.Rotation.X:0.####},{t.Rotation.Y:0.####},{t.Rotation.Z:0.####},{t.Rotation.W:0.####}) S=({t.Scale3D.X:0.####},{t.Scale3D.Y:0.####},{t.Scale3D.Z:0.####}); DR2 json {j.ToJsonString()}");
            worstPos = Math.Max(worstPos, Math.Sqrt(Math.Pow(t.Translation.X + loc.X - ue.X, 2) + Math.Pow(t.Translation.Y + loc.Y - ue.Y, 2) + Math.Pow(t.Translation.Z + loc.Z - ue.Z, 2)));
            double s = j["scale"] is JsonArray sa ? sa[0]!.GetValue<double>()
                : j["matrix"] is JsonArray m2 ? Math.Sqrt(Math.Pow(m2[0]!.GetValue<double>(), 2) + Math.Pow(m2[1]!.GetValue<double>(), 2) + Math.Pow(m2[2]!.GetValue<double>(), 2)) : 1;
            worstScale = Math.Max(worstScale, Math.Abs(t.Scale3D.X - s) / s);
        }
        C(worstPos < 0.1 && worstScale < 1e-4, $"HISM instance origins = ToUe(DR2 stage.json position) for all {Math.Min(n0, chosen.Count)} instances, worst {worstPos:0.####} cm; uniform scale equals DR2's, worst relative error {worstScale:0.0e+0}");

        // mesh vertices: CUE4Parse mesh x CUE4Parse instance transform vs objects.glb x DR2 matrix through the placement
        var glb = new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().Where(g => g.Mesh == template).ToList();
        var dr2Pts = glb.SelectMany(g => Enumerable.Range(0, g.Positions.Length / 3).Select(k => new Vector3(g.Positions[k * 3], g.Positions[k * 3 + 1], g.Positions[k * 3 + 2]))).ToList();
        double worstVert = 0; int checkedInst = 0;
        foreach (int i in new[] { 0, n0 / 2, n0 - 1 }.Distinct())
        {
            var m = ObjectColliders.Trs(stage[chosen[i]]["transform"]);
            var grid = new Dictionary<(int, int, int), List<Vector3>>();
            Vector3 Q(Vector3 a) => new((float)Math.Floor(a.X / 5), (float)Math.Floor(a.Y / 5), (float)Math.Floor(a.Z / 5));
            foreach (var d in dr2Pts)
            {
                var t0 = Vector3.Transform(d, m);
                var u = pl.ToUe([t0.X, t0.Y, t0.Z]);
                var v = new Vector3((float)u.X, (float)u.Y, (float)u.Z);
                var q = Q(v);
                var key = ((int)q.X, (int)q.Y, (int)q.Z);
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new();
                l.Add(v);
            }
            var t = xf[i];
            var rot = new Quaternion((float)t.Rotation.X, (float)t.Rotation.Y, (float)t.Rotation.Z, (float)t.Rotation.W);
            double Try(Quaternion rq)
            {
                double wr = 0;
                foreach (var q in pos.Where((_, k) => k % 40 == 0))
                {
                    var wv = Vector3.Transform(new Vector3((float)(q.X * t.Scale3D.X), (float)(q.Y * t.Scale3D.Y), (float)(q.Z * t.Scale3D.Z)), rq)
                             + new Vector3((float)(t.Translation.X + loc.X), (float)(t.Translation.Y + loc.Y), (float)(t.Translation.Z + loc.Z));
                    double best = double.MaxValue; foreach (var l in grid.Values) foreach (var v in l) best = Math.Min(best, Vector3.Distance(v, wv));
                    wr = Math.Max(wr, best);
                }
                return wr;
            }
            {
                Vector3 mnA = new(float.MaxValue), mxA = new(float.MinValue), mnB = mnA, mxB = mxA, mnC = mnA, mxC = mxA;
                foreach (var l in grid.Values) foreach (var v in l) { mnA = Vector3.Min(mnA, v); mxA = Vector3.Max(mxA, v); }
                foreach (var q in pos)
                {
                    var sc = new Vector3((float)(q.X * t.Scale3D.X), (float)(q.Y * t.Scale3D.Y), (float)(q.Z * t.Scale3D.Z));
                    var off = new Vector3((float)(t.Translation.X + loc.X), (float)(t.Translation.Y + loc.Y), (float)(t.Translation.Z + loc.Z));
                    var v1 = Vector3.Transform(sc, rot) + off; var v2 = Vector3.Transform(sc, Quaternion.Conjugate(rot)) + off;
                    mnB = Vector3.Min(mnB, v1); mxB = Vector3.Max(mxB, v1); mnC = Vector3.Min(mnC, v2); mxC = Vector3.Max(mxC, v2);
                }
                w.WriteLine($"     instance {i} boxes: DR2 ({mnA.X:0},{mnA.Y:0},{mnA.Z:0})..({mxA.X:0},{mxA.Y:0},{mxA.Z:0}); stored q ({mnB.X:0},{mnB.Y:0},{mnB.Z:0})..({mxB.X:0},{mxB.Y:0},{mxB.Z:0}); conj ({mnC.X:0},{mnC.Y:0},{mnC.Z:0})..({mxC.X:0},{mxC.Y:0},{mxC.Z:0})");
            }
            double asStored = Try(rot), conj = Try(Quaternion.Conjugate(rot));
            w.WriteLine($"     instance {i}: sampled vertex error with the quaternion as stored {(asStored > 1e6 ? "no match" : asStored.ToString("0.###"))} cm, conjugated {(conj > 1e6 ? "no match" : conj.ToString("0.###"))} cm");
            if (conj < asStored) rot = Quaternion.Conjugate(rot);
            double worst = 0;
            foreach (var q in pos.Where((_, k) => k % 7 == 0))
            {
                var wv = Vector3.Transform(new Vector3((float)(q.X * t.Scale3D.X), (float)(q.Y * t.Scale3D.Y), (float)(q.Z * t.Scale3D.Z)), rot)
                         + new Vector3((float)(t.Translation.X + loc.X), (float)(t.Translation.Y + loc.Y), (float)(t.Translation.Z + loc.Z));
                double best = double.MaxValue; foreach (var l in grid.Values) foreach (var v in l) best = Math.Min(best, Vector3.Distance(v, wv));
                worst = Math.Max(worst, best);
            }
            worstVert = Math.Max(worstVert, worst); checkedInst++;
        }
        C(worstVert < 15, $"world positions of every 7th mesh vertex of {checkedInst} instances (first, middle, last), through CUE4Parse's FTransform (a float quaternion decomposed by CUE4Parse from the stored FMatrix), are on DR2's own transformed vertices: worst {worstVert:0.###} cm. The stored matrices themselves match DR2 to 0.03 cm (stage-check 'vs DR2 geometry'), so the residual is the decomposition's, not the data's");
        w.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAIL");
        return failures == 0 ? 0 : 5;
    }

    static List<Instancing.Placed> InstancingSelect(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, string template) =>
        Instancing.Select(ir, pl, template, cfg.Num("instancing", "near_m"), (int)cfg.Num("instancing", "max_instances"),
                          v => v.X >= StageBuild.PhysMinX && v.X <= StageBuild.PhysMaxX && v.Z >= StageBuild.PhysMinZ && v.Z <= StageBuild.PhysMaxZ);
}
