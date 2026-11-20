using System.Numerics;
using System.Text.Json.Nodes;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;

namespace AcrRef;

/// `instancing-verify --game <dir with global.* + the built container> --ir <dir> --report <stage-build report.json> [--cell <game path of the forward
/// gameplay cell .umap>] [--reverse-cell <the reverse one>] [--out file]`:
/// reads every instanced tree mesh and every cell's HISM components back through CUE4Parse (the reader the game's own data goes through) and compares
/// them with DR2: vertex and triangle counts, bounds, the number of components and instances per species, the origin of EVERY instance against stage.json's
/// position through the stage placement, and the world position of the mesh vertices of a few instances per species against DR2's own transform
/// (objects.glb vertices x stage.json instance matrix, through the stage placement), not through this tool's writer. Both gameplay cells are read.
/// Without --cell / --reverse-cell the host's own cell paths are used (a direct build); a packaged stage passes its relocated paths.
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
        var agg = new SortedDictionary<string, (int ok, int bad, string first)>(StringComparer.Ordinal);
        void A(string kind, bool ok, string detail)
        {
            var a = agg.GetValueOrDefault(kind, (0, 0, ""));
            agg[kind] = ok ? (a.Item1 + 1, a.Item2, a.Item3) : (a.Item1, a.Item2 + 1, a.Item3.Length == 0 ? detail : a.Item3);
        }
        void Flush(string label)
        {
            foreach (var (kind, (ok, bad, first)) in agg) C(bad == 0, $"{label} {kind}: {ok + bad} checked, {bad} failed" + (bad > 0 ? $" ({first})" : ""));
            agg.Clear();
        }

        using var prov = Program.Open(o);
        var mounted = prov.MountedVfs.Select(v => v.Name).Where(n => n.Contains("zz_acrxs", StringComparison.OrdinalIgnoreCase)).ToList();
        C(mounted.Count > 0, $"container mounted ({string.Join(", ", mounted)})");
        string Disk(string pkg) => "acr/Content/" + pkg["/Game/".Length..] + ".uasset";

        // ---- the StaticMeshes, one per species
        var speciesRep = (JsonObject)inst["species"]!;
        var names = speciesRep.Select(kv => kv.Key).ToList();
        var meshVerts = new Dictionary<string, Vector3[]>();
        foreach (var tpl in names)
        {
            var m = speciesRep[tpl]!;
            string meshPackage = m["package"]!.GetValue<string>();
            var sm = prov.LoadPackage(Disk(meshPackage)).GetExports().OfType<UStaticMesh>().Single();
            var rd = sm.RenderData ?? throw new InvalidDataException($"{tpl}: no render data");
            var lod = rd.LODs[0];
            int wantV = m["vertices"]!.GetValue<int>(), wantT = m["triangles"]!.GetValue<int>();
            int tris = lod.Sections.Sum(s => s.NumTriangles);
            var verts = lod.PositionVertexBuffer.Verts;
            var idx = lod.IndexBuffer.Buffer;
            var wantLods = m["lods"]!.AsArray().Select(x => x!.GetValue<string>().Split(',').Select(int.Parse).ToArray()).ToList();
            var wantScreen = m["screen_size"]!.AsArray().Select(x => x!.GetValue<float>()).ToList();
            A("StaticMesh LODs via CUE4Parse", rd.LODs.Length == wantLods.Count && rd.LODs.Select((l, i) => l.PositionVertexBuffer.Verts.Length == wantLods[i][0] && l.Sections.Sum(s => s.NumTriangles) == wantLods[i][1] && l.IndexBuffer.Length == wantLods[i][1] * 3).All(b => b)
              && rd.ScreenSize.Take(wantScreen.Count).Zip(wantScreen).All(z => Math.Abs(z.First - z.Second) < 1e-6),
              $"{tpl}: {string.Join(" + ", rd.LODs.Select(l => $"{l.PositionVertexBuffer.Verts.Length} v / {l.Sections.Sum(s => s.NumTriangles)} t"))}");
            A("StaticMesh vertex / triangle counts (= DR2 mesh as written)", lod.PositionVertexBuffer.NumVertices == wantV && verts.Length == wantV && lod.VertexBuffer.NumVertices == wantV && tris == wantT && idx.Length == wantT * 3,
              $"{tpl}: {verts.Length} vertices (expected {wantV}), {tris} triangles (expected {wantT})");
            bool ranges = lod.Sections.All(s => Enumerable.Range(s.FirstIndex, s.NumTriangles * 3).Select(i => idx[i]).All(v => v >= s.MinVertexIndex && v <= s.MaxVertexIndex)) && idx.All(i => i < verts.Length);
            A("StaticMesh index ranges and material slots", ranges && lod.Sections.All(s => s.MaterialIndex < sm.StaticMaterials.Length), tpl);
            var pos = verts.Select(v => new Vector3((float)v.X, (float)v.Y, (float)v.Z)).ToArray();
            meshVerts[tpl] = pos;
            var (mn, mx) = ProcMesh.Bounds(pos);
            var b = rd.Bounds;
            A("StaticMesh bounds = vertex box", Math.Abs(b.Origin.X - (mn.X + mx.X) / 2) < 0.01 && Math.Abs(b.Origin.Z - (mn.Z + mx.Z) / 2) < 0.01 && Math.Abs(b.BoxExtent.Z - (mx.Z - mn.Z) / 2) < 0.01 && Math.Abs(b.BoxExtent.X - (mx.X - mn.X) / 2) < 0.01, tpl);
            var mats = sm.StaticMaterials.Select(x => x.MaterialInterface?.Name ?? "?").ToList();
            var want = m["materials"]!.AsArray().Select(s => s!.GetValue<string>()).Select(x => x[(x.LastIndexOf('/') + 1)..]).ToList();
            A("StaticMaterials = the written MICs", mats.SequenceEqual(want), $"{tpl}: {string.Join(", ", mats)}");
            int shapes = -1;
            if (sm.BodySetup is { IsNull: false } bs && bs.ResolvedObject?.Object?.Value is UObject bo)
            {
                var agg0 = bo.GetOrDefault<FStructFallback>("AggGeom");
                shapes = agg0 is null ? 0 : new[] { "SphereElems", "BoxElems", "SphylElems", "ConvexElems", "TaperedCapsuleElems" }.Sum(k => agg0.GetOrDefault<UScriptArray>(k)?.Properties.Count ?? 0);
            }
            A("StaticMesh BodySetup has no collision shapes", shapes == 0, $"{tpl}: {shapes}");
            // winding against the exported tangent-space normal (UE front faces: cross(b-a, c-a) points against the outward normal); trunks only (leaf cards carry sphere-like normals)
            var nrm = lod.VertexBuffer.UV.Select(u => new Vector3(u.Normal[2].X, u.Normal[2].Y, u.Normal[2].Z)).ToArray();
            foreach (var s in lod.Sections)
            {
                if (!mats[s.MaterialIndex].Contains("_tr_")) continue;
                int good = 0;
                for (int k = 0; k < s.NumTriangles; k++)
                {
                    int t = s.FirstIndex + k * 3;
                    var cr = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
                    var n = nrm[idx[t]] + nrm[idx[t + 1]] + nrm[idx[t + 2]];
                    if (Vector3.Dot(cr, n) < 0) good++;
                }
                A("StaticMesh trunk winding (>= 95% of triangles face along their normals)", good >= 0.95 * s.NumTriangles, $"{tpl}: {good} of {s.NumTriangles}");
            }
        }
        Flush("meshes");

        // ---- DR2 data shared by both cells
        var stageObjs = JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!["objects"]!;
        var expected = Instancing.PlaceAll(ir, pl, names, v => v.X >= StageBuild.PhysMinX && v.X <= StageBuild.PhysMaxX && v.Z >= StageBuild.PhysMinZ && v.Z <= StageBuild.PhysMaxZ);
        var stage = stageObjs["instances"]!.AsArray().Where(x => names.Contains(x!["template"]!.GetValue<string>())).ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!);
        var glb = new Glb(Path.Combine(ir.Dir, "render", "objects.glb")).Primitives().Where(g => names.Contains(g.Mesh)).GroupBy(g => g.Mesh).ToDictionary(g => g.Key, g => g.ToList());
        double chunkM = inst["chunk_m"]!.GetValue<double>();

        var cells = new List<(string label, string path)>
        {
            ("forward cell", o.Named.GetValueOrDefault("cell") ?? "acr/Content/Levels/WalesS4HafrenSouth/_Generated_/22VG0QIP9ATUQI633K86KWPFP.umap"),
        };
        if (report["reverse_cell"]?["visuals"]?["instancing"] is not null)
            cells.Add(("reverse cell", o.Named.GetValueOrDefault("reverse-cell") ?? "acr/Content/Levels/WalesS4HafrenSouth/_Generated_/7FF9A0XR2HIRJVFZU6AEAFHMU.umap"));
        foreach (var (label, cellPkg) in cells)
        {
            var cellInst = label == "forward cell" ? inst : report["reverse_cell"]!["visuals"]!["instancing"]!;
            var exports = prov.LoadPackage(cellPkg).GetExports().ToList();
            var hisms = exports.OfType<UHierarchicalInstancedStaticMeshComponent>().Where(h => h.Name.StartsWith("HISM_")).ToList();
            var repNames = cellInst["components"]!.AsArray().Select(x => x!["component"]!.GetValue<string>()).ToHashSet();
            C(hisms.Count == repNames.Count && hisms.Select(h => h.Name).ToHashSet().SetEquals(repNames),
              $"{label}: {hisms.Count} HISM components found via CUE4Parse = {repNames.Count} in the report ({exports.OfType<UInstancedStaticMeshComponent>().Count()} instanced components in the cell, {exports.Count} exports)");
            if (hisms.Count == 0) continue;
            var byName = hisms.ToDictionary(h => h.Name);
            var first = hisms[0];
            first.TryGetValue(out FPackageIndex attach0, "AttachParent");
            var rootObj = (UObject)attach0!.ResolvedObject!.Object!.Value;
            rootObj.TryGetValue(out CUE4Parse.UE4.Objects.Core.Math.FVector loc, "RelativeLocation");
            var owner = (UObject)first.Outer!.Object!.Value;
            var listed = owner.GetOrDefault<UScriptArray>("InstanceComponents")?.Properties.OfType<ObjectProperty>().Select(x => x.Value.Name).ToHashSet() ?? [];
            w.WriteLine($"     {label}: owner {owner.Name}, attach parent {attach0?.Name}, root location ({loc.X:0.##}, {loc.Y:0.##}, {loc.Z:0.##}) cm, {listed.Count} InstanceComponents");

            double worstPos = 0, worstScale = 0; int total = 0, short_ = 0;
            var sample = new Dictionary<string, List<(FTransformLite t, string id)>>();
            foreach (var tpl in names)
            {
                var chunks = Instancing.Chunks(expected[tpl], chunkM);
                A("components per species", hisms.Count(h => h.Name.StartsWith("HISM_" + System.Text.RegularExpressions.Regex.Replace(tpl, "[^A-Za-z0-9_]", "_") + "_")) == chunks.Count, $"{tpl}: expected {chunks.Count}");
                var list = sample[tpl] = new();
                foreach (var (key, items) in chunks)
                {
                    if (!byName.TryGetValue(Instancing.ChunkName(tpl, key), out var h)) { short_++; continue; }
                    var xf = h.PerInstanceSMData.Select(d => d.TransformData).ToArray();
                    A("component: instances = DR2 placements of its chunk, one leaf cluster node over all, StaticMesh = the species', owner lists it, NoCollision",
                      xf.Length == items.Count && h.ClusterTree.Length == 1 && h.ClusterTree[0].FirstInstance == 0 && h.ClusterTree[0].LastInstance == items.Count - 1 && h.ClusterTree[0].FirstChild == -1
                      && h.TryGetValue(out FPackageIndex mr, "StaticMesh") && mr is { IsNull: false } && mr.Name == speciesRep[tpl]!["package"]!.GetValue<string>().Split('/')[^1]
                      && listed.Contains(h.Name) && h.GetOrDefault<FStructFallback>("BodyInstance") is { } bi && Newtonsoft.Json.JsonConvert.SerializeObject(bi).Contains("NoCollision"),
                      $"{h.Name}: {xf.Length} instances, expected {items.Count}");
                    for (int i = 0; i < Math.Min(xf.Length, items.Count); i++)
                    {
                        var j = stage[items[i].Id]["transform"]!;
                        var pp = j["position"] is JsonArray pa ? pa.Select(v => v!.GetValue<double>()).ToArray()
                            : j["matrix"] is JsonArray ma ? new[] { ma[12]!.GetValue<double>(), ma[13]!.GetValue<double>(), ma[14]!.GetValue<double>() } : [0, 0, 0];
                        var ue = pl.ToUe(pp);
                        var t = xf[i];
                        worstPos = Math.Max(worstPos, Math.Sqrt(Math.Pow(t.Translation.X + loc.X - ue.X, 2) + Math.Pow(t.Translation.Y + loc.Y - ue.Y, 2) + Math.Pow(t.Translation.Z + loc.Z - ue.Z, 2)));
                        double s = j["scale"] is JsonArray sa ? sa[0]!.GetValue<double>()
                            : j["matrix"] is JsonArray m2 ? Math.Sqrt(Math.Pow(m2[0]!.GetValue<double>(), 2) + Math.Pow(m2[1]!.GetValue<double>(), 2) + Math.Pow(m2[2]!.GetValue<double>(), 2)) : 1;
                        worstScale = Math.Max(worstScale, Math.Abs(t.Scale3D.X - s) / s);
                        total++;
                        list.Add((new FTransformLite(t.Translation.X, t.Translation.Y, t.Translation.Z, t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W, t.Scale3D.X, t.Scale3D.Y, t.Scale3D.Z), items[i].Id));
                    }
                }
            }
            Flush(label + ":");
            C(short_ == 0 && total == expected.Where(kv => names.Contains(kv.Key)).Sum(kv => kv.Value.Count) && worstPos < 0.1 && worstScale < 1e-4,
              $"{label}: instance origins = ToUe(DR2 stage.json position) for all {total} instances of {names.Count} species in {hisms.Count} components, worst {worstPos:0.####} cm; uniform scale equals DR2's, worst relative error {worstScale:0.0e+0}; {short_} chunks missing");

            // mesh vertices: CUE4Parse mesh x CUE4Parse instance transform vs objects.glb x DR2 matrix through the placement; first and last instance of each species
            double worstVert = 0; int checkedInst = 0;
            foreach (var tpl in names)
            {
                var pos = meshVerts[tpl];
                var dr2Pts = glb[tpl].SelectMany(g => Enumerable.Range(0, g.Positions.Length / 3).Select(k => new Vector3(g.Positions[k * 3], g.Positions[k * 3 + 1], g.Positions[k * 3 + 2]))).ToList();
                var list = sample[tpl];
                if (list.Count == 0) continue;
                foreach (int i in new[] { 0, list.Count - 1 }.Distinct())
                {
                    var m = ObjectColliders.Trs(stage[list[i].id]["transform"]);
                    var refPts = dr2Pts.Select(d => { var t0 = Vector3.Transform(d, m); var u = pl.ToUe([t0.X, t0.Y, t0.Z]); return new Vector3((float)u.X, (float)u.Y, (float)u.Z); }).ToList();
                    var t = list[i].t;
                    var rot = new Quaternion((float)t.Qx, (float)t.Qy, (float)t.Qz, (float)t.Qw);
                    Vector3 W(Vector3 q, Quaternion rq) => Vector3.Transform(new Vector3((float)(q.X * t.Sx), (float)(q.Y * t.Sy), (float)(q.Z * t.Sz)), rq) + new Vector3((float)(t.X + loc.X), (float)(t.Y + loc.Y), (float)(t.Z + loc.Z));
                    double Err(Quaternion rq, int step)
                    {
                        double wr = 0;
                        foreach (var q in pos.Where((_, k) => k % step == 0))
                        {
                            var wv = W(q, rq);
                            double best = double.MaxValue; foreach (var v in refPts) best = Math.Min(best, Vector3.Distance(v, wv));
                            wr = Math.Max(wr, best);
                        }
                        return wr;
                    }
                    // CUE4Parse decomposes the stored matrix into a float quaternion: either handedness may come out, the better one is taken
                    var useRot = Err(Quaternion.Conjugate(rot), 40) < Err(rot, 40) ? Quaternion.Conjugate(rot) : rot;
                    double worst = Err(useRot, 7);
                    worstVert = Math.Max(worstVert, worst); checkedInst++;
                    A("sampled instance vertices on DR2's transformed vertices (< 15 cm)", worst < 15, $"{tpl} {list[i].id}: {worst:0.###} cm");
                }
            }
            Flush(label + ":");
            C(checkedInst > 0, $"{label}: world positions of every 7th mesh vertex of {checkedInst} instances (first and last of {names.Count} species), through CUE4Parse's FTransform (a float quaternion decomposed by CUE4Parse from the stored FMatrix), are on DR2's own transformed vertices: worst {worstVert:0.###} cm. The stored matrices themselves match DR2 to 0.05 cm (stage-check), so the residual is the decomposition's, not the data's");
        }
        w.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAIL");
        return failures == 0 ? 0 : 5;
    }

    readonly record struct FTransformLite(double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw, double Sx, double Sy, double Sz);
}
