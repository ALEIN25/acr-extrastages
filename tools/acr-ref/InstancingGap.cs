using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// `instancing-gap --ir <dir> --legacy <built mod root> --report <stage-build report.json> [--near 25] [--limit 25] [--out file]`: how far the colliders of every instanced tree within `near`
/// metres of the centreline are from its real trunk, measured geometrically (not on the invisible-obstacle grid), twice: DR2's own capsules as ObjectColliders keeps them ("DR2") and the
/// capsules after ColliderFit.FitAll, run here on the same inputs as the build ("written"; the build's own check, ColliderFit.CheckColliders, reads the capsules back from BakedData).
/// A tree with several capsules (a mallee has one per stem) takes the worst of them. For each tree and each of 0.3, 1.0 and 2.0 m above its base (ColliderFit.Measure): the worst distance from
/// a collider surface point in that plane to the cross-section of the placed trunk triangles ("collider beyond trunk"; 0 inside the trunk, 999 where there is no trunk), and the worst
/// distance from the trunk section near the axis to the collider's section ("trunk beyond collider"; 999 where the collider does not reach that height).
internal static class InstancingGap
{
    public static int Run(Options o)
    {
        var ir = StageBuild.Ir.Load(o.Named.GetValueOrDefault("ir") ?? throw new ArgumentException("--ir is required"));
        var report = JsonNode.Parse(File.ReadAllText(o.Named.GetValueOrDefault("report") ?? throw new ArgumentException("--report is required")))!;
        string root = o.Named.GetValueOrDefault("legacy") ?? throw new ArgumentException("--legacy is required");
        double nearM = double.Parse(o.Named.GetValueOrDefault("near", "25"), System.Globalization.CultureInfo.InvariantCulture);
        var p = report["placement"]!;
        var pl = new StageBuild.Placement(p["theta_deg"]!.GetValue<double>(), p["cx"]!.GetValue<double>(), p["cz"]!.GetValue<double>(),
            p["tx"]!.GetValue<double>(), p["tz"]!.GetValue<double>(), p["dy"]!.GetValue<double>(), p["slack_m"]!.GetValue<double>(),
            p["world"] is { } wn ? StageBuild.World.FromJson(wn) : null);
        if (pl.World is not null) StageBuild.UseWorld(pl.World);
        using var w = o.Out is null ? Console.Out : new StreamWriter(o.Out);
        var inst = report["cell"]!["visuals"]!["instancing"]!;
        var species = (JsonObject)inst["species"]!;
        bool InBounds(V3 v) => v.X >= StageBuild.PhysMinX && v.X <= StageBuild.PhysMaxX && v.Z >= StageBuild.PhysMinZ && v.Z <= StageBuild.PhysMaxZ;

        // DR2's capsules as ObjectColliders keeps them
        var surfaces = new SurfaceGrid(Path.Combine(ir.Dir, "collision.glb"), pl);
        var objs = ObjectColliders.Build(Path.Combine(ir.Dir, "stage.json"), pl, Toml.Load(report["options"]!["objects"]!.GetValue<string>()), StageBuild.Road(ir, pl), InBounds, surfaces, ir.Centre.Select(pl.ToUe).ToList());
        var dr2Caps = objs.Kept.Where(s => s.Capsule is not null && species.ContainsKey(s.Template)).GroupBy(s => s.Instance).ToDictionary(g => g.Key, g => g.Select(x => x.Capsule!).ToList());
        var before = dr2Caps.ToDictionary(kv => kv.Key, kv => kv.Value.Select(c => (c.Center, c.Radius, c.Length)).ToList());
        var placed = Instancing.PlaceAll(ir, pl, species.Select(kv => kv.Key).ToList(), InBounds);
        var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)), nearM + 50);
        // each species' mesh: positions, and the index lists of its trunk sections and of all sections
        var meshes = new Dictionary<string, (Vector3[] pos, uint[] trunk, uint[] all, List<int> trunkSecs, StaticMeshPackage.Info info, uint[] ix)>();
        foreach (var (tpl, node) in species)
        {
            var d = new LegacyPackage(Instancing.PackageFile(root, node!["package"]!.GetValue<string>())).GetExportData(1);
            var info = StaticMeshPackage.Read(d);
            var ix = StaticMeshPackage.Indices(d, info.Lods[0]);
            var tr = node["trunk_sections"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
            uint[] Of(Func<int, bool> pick) => Enumerable.Range(0, info.Lods[0].Sections.Count).Where(pick).SelectMany(k => ix.Skip(info.Lods[0].Sections[k].FirstIndex).Take(info.Lods[0].Sections[k].Triangles * 3)).ToArray();
            meshes[tpl] = (StaticMeshPackage.Positions(d, info.Lods[0]), Of(tr.Contains), Of(_ => true), tr, info, ix);
        }
        JsonNode? fitReport;
        {
            var plan = new Instancing.Plan();
            foreach (var (tpl, m) in meshes)
            {
                var sp = new Instancing.Species { Template = tpl, Placed = placed[tpl] };
                sp.Lod0.Pos.AddRange(m.pos);
                foreach (var sec in m.info.Lods[0].Sections) sp.Lod0.Sections.Add(new StaticMeshPackage.Geometry.Section(sec.Material, m.ix.Skip(sec.FirstIndex).Take(sec.Triangles * 3).ToList()));
                sp.TrunkSections.AddRange(m.trunkSecs);
                plan.Species[tpl] = sp;
            }
            fitReport = ColliderFit.FitAll(plan, objs, double.Parse(o.Named.GetValueOrDefault("limit", "25"), System.Globalization.CultureInfo.InvariantCulture));
        }
        var rows = new List<Row>();
        foreach (var (tpl, node) in species)
        {
            bool all = fitReport?["per_species"]?[tpl]?["geometry"]?.GetValue<string>() == "all sections";
            var m = meshes[tpl]; var idx = all ? m.all : m.trunk;
            foreach (var pi in placed[tpl])
            {
                double dr = route.Metres(new Vector2((float)pi.World.X, (float)pi.World.Y));
                if (dr > nearM || !dr2Caps.TryGetValue(pi.Id, out var cs)) continue;
                var wv = ColliderFit.World(m.pos, pi.Matrix);
                var row = new Row { Id = pi.Id, Template = tpl, RoadM = dr, Geometry = all ? "all" : "trunk", Capsules = cs.Count };
                var d0 = new ColliderFit.Gap[3]; var d1 = new ColliderFit.Gap[3];
                for (int k = 0; k < cs.Count; k++)
                {
                    var c = cs[k]; var b0 = before[pi.Id][k];
                    var (ax, ay, az) = ObjectColliders.RotationAxes(c.Rot[0], c.Rot[1], c.Rot[2]);
                    var up = ay.Z >= 0 ? ay : ay * -1.0;
                    var g0 = ColliderFit.Measure(wv, idx, b0.Center, ax, up, az, b0.Radius, b0.Length, pi.World.Z);
                    var g1 = ColliderFit.Measure(wv, idx, c.Center, ax, up, az, c.Radius, c.Length, pi.World.Z);
                    for (int h = 0; h < 3; h++) { d0[h] = new ColliderFit.Gap(Math.Max(d0[h].Out, g0[h].Out), Math.Max(d0[h].In, g0[h].In)); d1[h] = new ColliderFit.Gap(Math.Max(d1[h].Out, g1[h].Out), Math.Max(d1[h].In, g1[h].In)); }
                    if (k == 0) { row.DR2Radius = b0.Radius; row.DR2Length = b0.Length; row.WrittenRadius = c.Radius; row.WrittenLength = c.Length; }
                }
                row.Dr2 = d0; row.Written = d1;
                rows.Add(row);
            }
        }
        w.WriteLine($"trees within {nearM} m of the centreline with a DR2 and a written capsule: {rows.Count} of {placed.Sum(kv => kv.Value.Count)} instanced");
        foreach (var variant in new[] { "DR2", "written" })
        {
            Func<Row, ColliderFit.Gap[]> g = variant == "DR2" ? r => r.Dr2 : r => r.Written;
            w.WriteLine($"=== {variant} capsules");
            for (int i = 0; i < ColliderFit.Heights.Length; i++)
            {
                w.WriteLine($"  {ColliderFit.Heights[i] / 100:0.0} m: collider beyond trunk {Stats(rows.Select(r => g(r)[i].Out))}");
                w.WriteLine($"       trunk beyond collider {Stats(rows.Select(r => g(r)[i].In))}");
            }
            double Worst(Row r) => ColliderFit.CarWorst(g(r));
            w.WriteLine($"  car-height worst (both ways at 0.3 and 1.0 m, collider beyond trunk at 2.0 m), all trees: {Stats(rows.Select(Worst))}; {Over(rows.Select(Worst))}");
            var close = rows.Where(r => r.RoadM <= 12).ToList();
            w.WriteLine($"  within 12 m of the road: {close.Count} trees: {Stats(close.Select(Worst))}; {Over(close.Select(Worst))}");
            foreach (var grp in rows.GroupBy(r => r.Template).OrderBy(x => x.Key))
            {
                var gc = grp.Where(r => r.RoadM <= 12).ToList();
                w.WriteLine($"    {grp.Key} ({grp.First().Geometry} sections): all {Stats(grp.Select(Worst))}; within 12 m: {gc.Count} trees, {Over(gc.Select(Worst))}");
            }
            w.WriteLine("  10 worst:");
            foreach (var r in rows.OrderByDescending(Worst).Take(10))
            {
                var gg = g(r);
                w.WriteLine($"    {r.Id}: worst {Worst(r):0} cm; {r.RoadM:0.0} m from the road; beyond-trunk {string.Join("/", gg.Select(x => x.Out.ToString("0")))} cm, trunk-beyond {string.Join("/", gg.Select(x => x.In.ToString("0")))} cm; " +
                            $"{r.Capsules} capsule(s), first: radius {(variant == "DR2" ? r.DR2Radius : r.WrittenRadius):0} cm, length {(variant == "DR2" ? r.DR2Length : r.WrittenLength):0} cm; {Cause(r, gg)}");
            }
        }
        return 0;
    }

    static double Pct(IEnumerable<double> xs, double q) { var a = xs.OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[Math.Min(a.Count - 1, (int)Math.Floor(q * (a.Count - 1) + 0.5))]; }
    static string Stats(IEnumerable<double> xs) { var l = xs.ToList(); return $"median {Pct(l, .5):0.#}, p90 {Pct(l, .9):0.#}, p99 {Pct(l, .99):0.#}, max {(l.Count == 0 ? 0 : l.Max()):0.#} cm"; }
    static string Over(IEnumerable<double> xs) { var l = xs.ToList(); return string.Join(", ", new[] { 10, 15, 20, 25, 30 }.Select(t => $">{t} cm: {l.Count(x => x > t)}")); }

    static string Cause(Row r, ColliderFit.Gap[] g)
    {
        if (g.Any(x => x.Out >= 999)) return "no trunk geometry at that height (collider reaches above the mesh)";
        if (g.Any(x => x.In >= 999)) return "collider does not reach that height but the trunk does";
        return g.Max(x => x.Out) >= g.Max(x => x.In) ? "collider wider than the trunk, or offset from it" : "trunk wider than the collider, or offset from it";
    }

    sealed class Row
    {
        public int Capsules;
        public string Id = "", Template = "", Geometry = ""; public double RoadM, DR2Radius, WrittenRadius, DR2Length, WrittenLength;
        public ColliderFit.Gap[] Dr2 = [], Written = [];
    }
}
