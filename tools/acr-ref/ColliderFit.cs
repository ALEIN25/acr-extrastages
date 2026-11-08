using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Capsule colliders of instanced trees fitted to their real trunks. DR2's capsule is only our own physics data: it is a straight cylinder wider than the tapering trunk
/// (Finland: 61-68 cm against a trunk of 50 cm at 0.3 m and 34 cm at 2 m) and as long as the DR2 tree is tall, so a car meets an invisible skin of up to 36 cm, and
/// Australian saplings of 1.5 m carry a 20 m capsule. FitAll rewrites each instanced tree's kept capsule before BakedData is written (StageBuild.Finish):
///   axis      the DR2 capsule's own direction (the instance's rotation) translated onto the centroid of the trunk section at 0.3, 1.0 and 2.0 m above the tree base
///   radius    the mean over those heights of the section's largest distance from its centroid, never above the DR2 radius (only ever smaller)
///   extent    from the DR2 capsule's lowest point (the base stays solid) to the lower of the DR2 top and the highest trunk point
/// The section is cut from the actual placed trunk triangles (the mesh's foliage_trunk sections through the instance matrix: the instance's real scale and rotation). A species
/// whose trunk sections often give no section near the axis (the trunk is partly in other materials) is fitted to all the mesh's sections instead. A tree whose fit fails keeps its
/// DR2 capsule and is listed. Measure reports the gap both ways for the check (collider beyond trunk, trunk beyond collider).
internal static class ColliderFit
{
    // Test the actual instance mesh at two car heights; roof/floor coverage cannot pass this test.
    internal static bool EnclosedBox(ObjectColliders.Shape shape, V3[] triangles)
    {
        if (shape.Box is not { } b) return false;
        var (x, y, z) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
        // a box's up axis is its third one (z: DR2 width, length, height = X, Y, Z); the footprint is spanned by x and y
        var corners = new[] { b.Center - x * (b.X / 2) - y * (b.Y / 2),
            b.Center + x * (b.X / 2) - y * (b.Y / 2),
            b.Center + x * (b.X / 2) + y * (b.Y / 2),
            b.Center - x * (b.X / 2) + y * (b.Y / 2) };
        // sample heights above the drawn mesh's foot (the ground), not the box bottom: DR2 building boxes reach 1 m+ underground
        double baseZ = Math.Max(shape.FootprintUe.Min(p => p.Z), triangles.Min(p => p.Z));
        var verts = triangles.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToArray();
        var indices = Enumerable.Range(0, verts.Length).Select(i => (uint)i).ToArray();
        // several heights above the drawn foot (houses on a slope: low samples are underground on the uphill side); enclosed when at
        // least two of them are covered
        int goodHeights = 0;
        foreach (double height in new[] { 50.0, 100.0, 150.0, 200.0, 250.0 })
        {
            var segments = Section(verts, indices, baseZ + height);
            int total = 0, covered = 0;
            for (int edge = 0; edge < 4; edge++)
            {
                var a = corners[edge]; var c = corners[(edge + 1) % 4];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Pow(c.X - a.X, 2) + Math.Pow(c.Y - a.Y, 2)) / 25));
                for (int i = 0; i < steps; i++)
                {
                    double t = (i + 0.5) / steps;
                    var p = new Vector2((float)(a.X + (c.X - a.X) * t), (float)(a.Y + (c.Y - a.Y) * t));
                    var inward = new Vector2((float)b.Center.X, (float)b.Center.Y) - p;
                    if (inward.Length() > 2) p += Vector2.Normalize(inward) * 2;
                    total++;
                    // covered: a drawn wall of the object within 30 cm, or shielding the point from outside (a ray outward meets one of
                    // the object's own drawn surfaces within 6 m: an annex or porch in front of the box side, which a car hits first)
                    var outward = inward.Length() > 0 ? -Vector2.Normalize(inward) : Vector2.Zero;
                    if (segments.Any(s => {
                        var d = s.b - s.a;
                        float f = d.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - s.a, d) / d.LengthSquared(), 0, 1) : 0;
                        if (Vector2.DistanceSquared(p, s.a + f * d) <= 30 * 30) return true;
                        float den = outward.X * d.Y - outward.Y * d.X;
                        if (Math.Abs(den) < 1e-6f) return false;
                        var w = s.a - p;
                        float t = (w.X * d.Y - w.Y * d.X) / den, u = (w.X * outward.Y - w.Y * outward.X) / den;
                        return t >= 0 && t <= 600 && u >= 0 && u <= 1;
                    })) covered++;
                }
            }
            if (covered >= total * 0.9) goodHeights++;
        }
        return goodHeights >= 2;
    }
    // Intersect the box with mesh bounds measured in the box's own orthonormal frame.
    // Mutate the kept physics shape, just as the tree fit does before Finish writes it.
    internal static bool FitBox(ObjectColliders.Shape shape, IEnumerable<V3> vertices, double maxCm)
    {
        if (shape.Box is not { } b) return false;
        var (x, y, z) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
        V3[] axes = [x, y, z]; double[] size = [b.X, b.Y, b.Z];
        double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        foreach (var p in vertices)
        {
            var d = p - b.Center;
            for (int k = 0; k < 3; k++)
            {
                var a = axes[k]; double q = d.X * a.X + d.Y * a.Y + d.Z * a.Z;
                lo[k] = Math.Min(lo[k], q); hi[k] = Math.Max(hi[k], q);
            }
        }
        for (int k = 0; k < 3; k++)
        {
            lo[k] = Math.Max(lo[k], -size[k] / 2); hi[k] = Math.Min(hi[k], size[k] / 2);
            if (!double.IsFinite(lo[k]) || !double.IsFinite(hi[k]) || hi[k] <= lo[k] ||
                lo[k] + size[k] / 2 > maxCm || size[k] / 2 - hi[k] > maxCm) return false;
        }
        b.Center += x * ((lo[0] + hi[0]) / 2) + y * ((lo[1] + hi[1]) / 2) + z * ((lo[2] + hi[2]) / 2);
        b.X = hi[0] - lo[0]; b.Y = hi[1] - lo[1]; b.Z = hi[2] - lo[2];
        shape.FootprintUe.Clear();
        foreach (double a in new[] { -1.0, 1.0 }) foreach (double c in new[] { -1.0, 1.0 }) foreach (double d in new[] { -1.0, 1.0 })
            shape.FootprintUe.Add(b.Center + x * (a * b.X / 2) + y * (c * b.Y / 2) + z * (d * b.Z / 2));
        return true;
    }
    internal static readonly double[] Heights = [30, 100, 200];
    static readonly double[] FitHeights = [30, 65, 100, 150, 200];

    /// The horizontal section of the triangles at z = zt as segments (world UE cm).
    internal static List<(Vector2 a, Vector2 b)> Section(Vector3[] wv, IReadOnlyList<uint> idx, double zt)
    {
        var segs = new List<(Vector2, Vector2)>();
        var pts = new Vector2[2];
        for (int t = 0; t + 2 < idx.Count; t += 3)
        {
            var v0 = wv[idx[t]]; var v1 = wv[idx[t + 1]]; var v2 = wv[idx[t + 2]];
            if (Math.Min(v0.Z, Math.Min(v1.Z, v2.Z)) > zt || Math.Max(v0.Z, Math.Max(v1.Z, v2.Z)) <= zt) continue;
            int n = 0;
            Vector3[] v = [v0, v1, v2];
            for (int e = 0; e < 3 && n < 2; e++)
            {
                var a = v[e]; var b = v[(e + 1) % 3];
                if ((a.Z <= zt) != (b.Z <= zt)) { float f = (float)((zt - a.Z) / (b.Z - a.Z)); pts[n++] = new Vector2(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f); }
            }
            if (n == 2) segs.Add((pts[0], pts[1]));
        }
        return segs;
    }

    /// The tree's mesh vertices in world UE cm through its instance matrix.
    internal static Vector3[] World(IReadOnlyList<Vector3> pos, double[] m)
    {
        var w = new Vector3[pos.Count];
        for (int k = 0; k < w.Length; k++)
        {
            var p = pos[k];
            w[k] = new Vector3((float)(p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12]), (float)(p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13]), (float)(p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14]));
        }
        return w;
    }

    static (V3 up, V3 ax, V3 az) Axes(ObjectColliders.Capsule c)
    {
        var (ax, ay, az) = ObjectColliders.RotationAxes(c.Rot[0], c.Rot[1], c.Rot[2]);
        return (ay.Z >= 0 ? ay : ay * -1.0, ax, az);
    }

    /// The fit of one tree: new centre, radius, length; null (with the reason) if the geometry gives no usable section.
    static (V3 centre, double radius, double length)? TryFit(Vector3[] wv, IReadOnlyList<uint> idx, ObjectColliders.Capsule c, double baseZ, out string why)
    {
        why = "";
        var (up, _, _) = Axes(c);
        double half = c.Length / 2 + c.Radius, lowZ = c.Center.Z - up.Z * half, topZ = c.Center.Z + up.Z * half;
        var found = new List<(Vector2 ctr, double z, double r)>();
        foreach (double h in FitHeights)
        {
            double z = baseZ + h;
            if (z < lowZ || z > topZ) continue;
            double t = (z - c.Center.Z) / up.Z;
            var axisPt = new Vector2((float)(c.Center.X + up.X * t), (float)(c.Center.Y + up.Y * t));
            var near = Section(wv, idx, z).Where(s => Vector2.Distance((s.a + s.b) / 2, axisPt) <= c.Radius + 60).ToList();
            if (near.Count < 3) continue;
            // two passes: the segments of limbs and forks beyond 1.5x the median distance from the centroid are left out, then centroid and mean radius (length-weighted)
            var ctr = near.Aggregate(Vector2.Zero, (s, x) => s + (x.a + x.b) / 2) / near.Count;
            var ds = near.Select(s => Vector2.Distance((s.a + s.b) / 2, ctr)).OrderBy(x => x).ToList();
            double med = ds[ds.Count / 2];
            var core = near.Where(s => Vector2.Distance((s.a + s.b) / 2, ctr) <= 1.5 * med + 1).ToList();
            if (core.Count < 3) continue;
            ctr = core.Aggregate(Vector2.Zero, (s, x) => s + (x.a + x.b) / 2) / core.Count;
            double wsum = core.Sum(s => (double)Vector2.Distance(s.a, s.b));
            double r = wsum < 1e-6 ? 0 : core.Sum(s => Vector2.Distance((s.a + s.b) / 2, ctr) * (double)Vector2.Distance(s.a, s.b)) / wsum;
            found.Add((ctr, z, r));
        }
        if (found.Count == 0) { why = "no trunk section near the capsule axis at 0.3, 1.0 or 2.0 m"; return null; }
        double zm = found.Average(f => f.z);
        double px = found.Average(f => f.ctr.X), py = found.Average(f => f.ctr.Y);
        // a straight cylinder over the tapering trunk: halfway between its thinnest and thickest section, so the gap is the same both ways
        double radius = Math.Max(5, Math.Min(c.Radius, (found.Min(f => f.r) + found.Max(f => f.r)) / 2));
        // the highest trunk point over the axis
        double zTop = double.NegativeInfinity;
        foreach (var i in idx.Distinct())
        {
            var v = wv[i];
            double t = (v.Z - zm) / up.Z;
            if (Math.Sqrt(Math.Pow(v.X - (px + up.X * t), 2) + Math.Pow(v.Y - (py + up.Y * t), 2)) <= radius + 150) zTop = Math.Max(zTop, v.Z);
        }
        double top = double.IsNegativeInfinity(zTop) ? topZ : Math.Min(topZ, zTop);
        double tLow = (lowZ - zm) / up.Z, tTop = (top - zm) / up.Z;
        if (tTop <= tLow + 1) { why = "trunk ends below the capsule's base"; return null; }
        double len = Math.Max(0, tTop - tLow - 2 * radius), tc = (tLow + tTop) / 2;
        return (new V3(px + up.X * tc, py + up.Y * tc, zm + up.Z * tc), radius, len);
    }

    static double Score(Vector3[] wv, IReadOnlyList<uint> idx, V3 centre, ObjectColliders.Capsule c, double radius, double length, double baseZ)
    {
        var (up, ax, az) = Axes(c);
        return CarWorst(Measure(wv, idx, centre, ax, up, az, radius, length, baseZ));
    }

    /// Fits the kept capsules of every instanced tree (plan.Species) to its real trunk. A fit is kept only if it is closer to the trunk than DR2's capsule (CarWorst); a tree whose
    /// fit fails or is not closer keeps its DR2 capsule. Trees still over limitCm after the fit are listed (over_limit_ids): a multi-stem clump or a fork that no single straight
    /// capsule follows. Returns the report; the capsules are changed in place.
    public static JsonObject FitAll(Instancing.Plan plan, ObjectColliders.Result objs, double limitCm)
    {
        var caps = objs.Kept.Where(s => s.Capsule is not null && plan.Species.ContainsKey(s.Template)).GroupBy(s => s.Instance).ToDictionary(g => g.Key, g => g.Select(x => x.Capsule!).ToList());
        var per = new JsonObject(); var failedAll = new JsonArray(); var overAll = new JsonArray();
        int fitted = 0, failed = 0, noCapsule = 0;
        foreach (var sp in plan.Species.Values)
        {
            var trunkIdx = sp.TrunkSections.SelectMany(k => sp.Lod0.Sections[k].Idx).ToList();
            var allIdx = sp.Lod0.Sections.SelectMany(s => s.Idx).ToList();
            var trees = sp.Placed.Where(p => caps.ContainsKey(p.Id)).ToList();
            noCapsule += sp.Placed.Count - trees.Count;
            // the trunk sections, or all sections where those fit clearly worse (the trunk is partly in other materials)
            double Probe(List<uint> g)
            {
                var sc = new List<double>();
                foreach (var p in trees.Take(20))
                {
                    var wv = World(sp.Lod0.Pos, p.Matrix);
                    foreach (var c in caps[p.Id])
                        sc.Add(TryFit(wv, g, c, p.World.Z, out _) is { } f ? Score(wv, g, f.centre, c, f.radius, f.length, p.World.Z) : 999);
                }
                return sc.Count == 0 ? 999 : sc.Average();
            }
            bool all = trees.Count > 0 && Probe(allIdx) < Probe(trunkIdx) - 3;
            var idx = all ? allIdx : trunkIdx;
            int ok = 0, bad = 0, over = 0; double r0 = 0, r1 = 0, l0 = 0, l1 = 0; var fails = new JsonArray();
            foreach (var p in trees)
            {
                var wv = World(sp.Lod0.Pos, p.Matrix);
                foreach (var c in caps[p.Id])
                {
                    if (TryFit(wv, idx, c, p.World.Z, out var why) is not { } f) { bad++; if (fails.Count < 10) fails.Add($"{p.Id}: {why}"); failedAll.Add(p.Id); continue; }
                    double s0 = Score(wv, idx, c.Center, c, c.Radius, c.Length, p.World.Z), s1 = Score(wv, idx, f.centre, c, f.radius, f.length, p.World.Z);
                    if (s1 > s0) { bad++; if (fails.Count < 10) fails.Add($"{p.Id}: the fit ({s1:0} cm) is no closer to the trunk than DR2's capsule ({s0:0} cm)"); failedAll.Add(p.Id); continue; }
                    r0 += c.Radius; l0 += c.Length; r1 += f.radius; l1 += f.length;
                    c.Center = f.centre; c.Radius = f.radius; c.Length = f.length;
                    ok++;
                    if (s1 > limitCm) { over++; overAll.Add(p.Id); }
                }
            }
            fitted += ok; failed += bad;
            per[sp.Template] = new JsonObject
            {
                ["geometry"] = all ? "all sections" : "trunk sections", ["fitted"] = ok, ["kept_dr2_capsule"] = bad, ["fitted_but_over_limit"] = over, ["without_capsule"] = sp.Placed.Count - trees.Count,
                ["mean_radius_cm"] = ok == 0 ? null : $"{r0 / ok:0.#} -> {r1 / ok:0.#}", ["mean_length_cm"] = ok == 0 ? null : $"{l0 / ok:0} -> {l1 / ok:0}", ["failures"] = fails,
            };
        }
        Console.WriteLine($"instancing: colliders fitted to the real trunks: {fitted} capsules fitted ({overAll.Count} still over {limitCm:0} cm at car height), {failed} keep their DR2 capsule ({noCapsule} instances without a capsule)");
        return new JsonObject { ["limit_cm"] = limitCm, ["fitted"] = fitted, ["kept_dr2_capsule"] = failed, ["fitted_but_over_limit"] = overAll.Count, ["without_capsule"] = noCapsule, ["failed_ids"] = failedAll, ["over_limit_ids"] = overAll, ["per_species"] = per };
    }

    /// A tree drawn with a real mesh in the merged visuals (VisualObjects.Build, full and middle tier): its template, DR2 instance, base height, mesh vertices in world UE cm and trunk capsules.
    internal sealed record MeshTree(string Template, string Id, double BaseZ, Func<Vector3[]> World, List<ObjectColliders.Capsule> Caps);

    /// The same fit for the trees of the merged visuals. The trunk triangles are the real mesh's (the middle tier keeps them whole, so one fit serves both tiers); geometry per template:
    /// trunk triangles, or all tree triangles where those fit clearly worse. Unlike FitAll a capsule is only changed when the fit is closer to the trunk than DR2's AND within limitCm;
    /// a multi-stem clump or fork keeps its DR2 capsule (and the proxy hull the build check wants for it). Capsules are changed in place.
    internal static JsonObject FitMeshTrees(IReadOnlyList<MeshTree> trees, Func<string, (IReadOnlyList<uint> trunk, IReadOnlyList<uint> all)> geometry, double limitCm)
    {
        var per = new JsonObject(); int fitted = 0, kept = 0, over = 0, total = trees.Sum(t => t.Caps.Count);
        foreach (var g in trees.GroupBy(t => t.Template).OrderBy(g => g.Key))
        {
            var (trunkIdx, allIdx) = geometry(g.Key);
            double Probe(IReadOnlyList<uint> ix)
            {
                var sc = new List<double>();
                foreach (var t in g.Take(20))
                {
                    var wv = t.World();
                    foreach (var c in t.Caps)
                        sc.Add(TryFit(wv, ix, c, t.BaseZ, out _) is { } f ? Score(wv, ix, f.centre, c, f.radius, f.length, t.BaseZ) : 999);
                }
                return sc.Count == 0 ? 999 : sc.Average();
            }
            bool all = trunkIdx.Count == 0 || Probe(allIdx) < Probe(trunkIdx) - 3;
            var idx = all ? allIdx : trunkIdx;
            int ok = 0, bad = 0, ovr = 0;
            foreach (var t in g)
            {
                var wv = t.World();
                foreach (var c in t.Caps)
                {
                    if (TryFit(wv, idx, c, t.BaseZ, out _) is not { } f) { bad++; continue; }
                    double s0 = Score(wv, idx, c.Center, c, c.Radius, c.Length, t.BaseZ), s1 = Score(wv, idx, f.centre, c, f.radius, f.length, t.BaseZ);
                    if (s1 > s0) { bad++; continue; }
                    if (s1 > limitCm) { ovr++; continue; }
                    c.Center = f.centre; c.Radius = f.radius; c.Length = f.length; ok++;
                }
            }
            fitted += ok; kept += bad; over += ovr;
            per[g.Key] = new JsonObject { ["geometry"] = all ? "all sections" : "trunk sections", ["fitted"] = ok, ["kept_dr2_capsule"] = bad, ["fit_over_limit_kept_dr2_capsule"] = ovr };
        }
        Console.WriteLine($"vegetation: colliders fitted to the real trunks (full and middle tier trees): {fitted} of {total} capsules fitted, {kept} keep their DR2 capsule (no usable section or no closer), {over} keep it because the fit is still over {limitCm:0} cm");
        return new JsonObject { ["limit_cm"] = limitCm, ["capsules"] = total, ["fitted"] = fitted, ["kept_dr2_capsule"] = kept, ["over_limit_kept_dr2_capsule"] = over, ["per_template"] = per };
    }

    // ---- measuring ----------------------------------------------------------------------------------------------------------------

    internal readonly record struct Gap(double Out, double In);

    /// The figure the build check limits: both ways at 0.3 and 1.0 m, and the collider beyond the trunk at 2.0 m. The trunk beyond the collider at 2.0 m is left out: a car is about
    /// 1.4 m high, and what is there (a fork or limbs spreading above the roof) cannot be driven into; it is still reported.
    internal static double CarWorst(Gap[] g) => Math.Max(Math.Max(g[0].Out, g[0].In), Math.Max(Math.Max(g[1].Out, g[1].In), g[2].Out));

    static bool Inside(List<(Vector2 a, Vector2 b)> segs, Vector2 q)
    {
        bool inside = false;
        foreach (var (a, b) in segs)
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        return inside;
    }

    static double Dist(List<(Vector2 a, Vector2 b)> segs, Vector2 q)
    {
        double best = double.MaxValue;
        foreach (var (a, b) in segs)
        {
            var ab = b - a; float len2 = ab.LengthSquared();
            float f = len2 < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(q - a, ab) / len2, 0, 1);
            best = Math.Min(best, Vector2.Distance(q, a + ab * f));
        }
        return best;
    }

    /// Per height (Heights above baseZ): Out = the worst horizontal distance from a point of the collider's surface in that plane to the trunk section (0 inside it; 999 if there is
    /// no trunk there; 0 if the collider does not reach that height), In = the worst distance from the section of the trunk near the axis (within radius + 60 cm) to the collider's
    /// section (0 inside it; 999 if the collider does not reach that height; 0 if no trunk near the axis). Cm. The collider is the cylinder part as the invisible-obstacle check draws it.
    internal static Gap[] Measure(Vector3[] wv, IReadOnlyList<uint> idx, V3 centre, V3 ax, V3 up, V3 az, double radius, double length, double baseZ)
    {
        var res = new Gap[Heights.Length];
        for (int hi = 0; hi < Heights.Length; hi++)
        {
            double zt = baseZ + Heights[hi];
            var segs = Section(wv, idx, zt);
            var cp = new List<Vector2>();
            for (int k = 0; k < 48; k++)
            {
                double th = 2 * Math.PI * k / 48;
                var radial = ax * Math.Cos(th) + az * Math.Sin(th);
                double tt = (zt - centre.Z - radius * radial.Z) / up.Z;
                if (Math.Abs(tt) > length / 2 + radius) continue;
                var q = centre + up * tt + radial * radius;
                cp.Add(new Vector2((float)q.X, (float)q.Y));
            }
            double at = (zt - centre.Z) / up.Z;
            var axisPt = new Vector2((float)(centre.X + up.X * at), (float)(centre.Y + up.Y * at));
            double gout = 0, gin = 0;
            if (cp.Count > 0)
            {
                if (segs.Count == 0) gout = 999;
                else foreach (var q in cp) gout = Math.Max(gout, Inside(segs, q) ? 0 : Dist(segs, q));
            }
            var near = segs.Where(s => Vector2.Distance((s.a + s.b) / 2, axisPt) <= radius + 60).ToList();
            if (near.Count > 0)
            {
                // the tip of a thin sapling or twig (a section under 6 cm across) is not a trunk to drive into
                var nc = near.Aggregate(Vector2.Zero, (u, x) => u + (x.a + x.b) / 2) / near.Count;
                if (near.Max(x => Math.Max(Vector2.Distance(x.a, nc), Vector2.Distance(x.b, nc))) < 6) gin = 0;
                else if (cp.Count < 3) gin = 999;
                else
                {
                    var ring = new List<(Vector2 a, Vector2 b)>();
                    for (int k = 0; k < cp.Count; k++) ring.Add((cp[k], cp[(k + 1) % cp.Count]));
                    foreach (var s in near)
                        foreach (var q in new[] { s.a, s.b })
                            gin = Math.Max(gin, Inside(ring, q) ? 0 : Dist(ring, q));
                }
            }
            res[hi] = new Gap(gout, gin);
        }
        return res;
    }

    /// Build check: for every instanced tree within 12 m of the centreline, the gap between its written collider(s) and its real trunk at 0.3, 1.0 and 2.0 m above the base, both ways
    /// (Measure, CarWorst), against [instancing] collider_gap_limit_cm. The colliders are read back from BakedData (shapes), paired with their trees by the instance ids the build reported;
    /// a tree's figure is the worst of its capsules (a mallee has one per stem). The trunk is read from the mesh packages under root.
    public static int CheckColliders(string root, StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg, JsonNode inst, List<InvisibleCheck.Shape> shapes, List<string> shapeInstances, Func<V3, bool> inBounds, TextWriter w)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{Advisory.Verdict(ok, what, out var bad)} {what}"); if (bad) failures++; }
        var species = (JsonObject)inst["species"]!;
        var failedIds = ((inst["collider_fit"]?["failed_ids"] as JsonArray) ?? []).Select(x => x!.GetValue<string>()).ToHashSet();
        var overIds = ((inst["collider_fit"]?["over_limit_ids"] as JsonArray) ?? []).Select(x => x!.GetValue<string>()).ToHashSet();
        double limit = cfg.Num("instancing", "collider_gap_limit_cm"), roadM = 12;
        var placed = Instancing.PlaceAll(ir, pl, species.Select(kv => kv.Key).ToList(), inBounds);
        var route = new GroundLod.RouteDistance(ir.Centre.Select(pl.ToUe).Select(q => new Vector2((float)q.X, (float)q.Y)), roadM + 50);
        // the written capsules of each instanced tree: BakedData's simple entries are in the build's order, and the report names the DR2 instance of each
        if (shapeInstances.Count != shapes.Count) throw new InvalidDataException($"{shapes.Count} written shapes but {shapeInstances.Count} instance ids in the report");
        var byId = placed.SelectMany(kv => kv.Value.Select(p => (kv.Key, p))).ToDictionary(x => x.p.Id, x => x);
        var perTree = new Dictionary<string, (string tpl, Instancing.Placed p, List<InvisibleCheck.Shape> caps)>();
        for (int i = 0; i < shapes.Count; i++)
        {
            if (shapes[i].Box || !byId.TryGetValue(shapeInstances[i], out var b)) continue;
            if (!perTree.TryGetValue(b.p.Id, out var e)) perTree[b.p.Id] = e = (b.Key, b.p, new());
            e.caps.Add(shapes[i]);
        }
        var geo = new Dictionary<string, (Vector3[] pos, uint[] idx)>();
        (Vector3[] pos, uint[] idx) Geo(string tpl)
        {
            if (geo.TryGetValue(tpl, out var g)) return g;
            var node = species[tpl]!;
            bool all = inst["collider_fit"]?["per_species"]?[tpl]?["geometry"]?.GetValue<string>() == "all sections";
            var d = new LegacyPackage(Instancing.PackageFile(root, node["package"]!.GetValue<string>())).GetExportData(1);
            var info = StaticMeshPackage.Read(d);
            var pos = StaticMeshPackage.Positions(d, info.Lods[0]); var ix = StaticMeshPackage.Indices(d, info.Lods[0]);
            var tr = node["trunk_sections"]!.AsArray().Select(x => x!.GetValue<int>()).ToHashSet();
            var idx = Enumerable.Range(0, info.Lods[0].Sections.Count).Where(k => all || tr.Contains(k)).SelectMany(k => ix.Skip(info.Lods[0].Sections[k].FirstIndex).Take(info.Lods[0].Sections[k].Triangles * 3)).ToArray();
            return geo[tpl] = (pos, idx);
        }
        var outs = new List<double>(); var ins = new List<double>(); var worst = new List<(double g, string id, double road)>();
        int trees = 0, unfit = 0, unfitOver = 0, capsules = 0;
        foreach (var (id, (tpl, p, caps)) in perTree)
        {
            double dr = route.Metres(new Vector2((float)p.World.X, (float)p.World.Y));
            if (dr > roadM) continue;
            trees++; capsules += caps.Count;
            var (pos, idx) = Geo(tpl);
            var wv = World(pos, p.Matrix);
            double wo = 0, wi = 0, wg = 0;
            foreach (var sh in caps)
            {
                var up = sh.Ay.Z >= 0 ? sh.Ay : sh.Ay * -1.0;
                var gaps = Measure(wv, idx, sh.Centre, sh.Ax, up, sh.Az, sh.Radius, sh.Length, p.World.Z);
                wo = Math.Max(wo, gaps.Max(g => g.Out)); wi = Math.Max(wi, Math.Max(gaps[0].In, gaps[1].In)); wg = Math.Max(wg, CarWorst(gaps));
            }
            if (failedIds.Contains(id) || overIds.Contains(id)) { unfit++; if (wg > limit) unfitOver++; continue; }
            outs.Add(wo); ins.Add(wi); worst.Add((wg, id, dr));
        }
        int expected = placed.Sum(kv => kv.Value.Count(p => route.Metres(new Vector2((float)p.World.X, (float)p.World.Y)) <= roadM));
        double Pct(List<double> xs, double q) { var a = xs.OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[Math.Min(a.Count - 1, (int)Math.Floor(q * (a.Count - 1) + 0.5))]; }
        string St(List<double> xs) => $"median {Pct(xs, .5):0.#}, p90 {Pct(xs, .9):0.#}, p99 {Pct(xs, .99):0.#}, max {(xs.Count == 0 ? 0 : xs.Max()):0.#} cm";
        var over = worst.Where(x => x.g > limit).OrderByDescending(x => x.g).ToList();
        C(over.Count == 0 && trees > 0 && trees == expected,
          $"instancing colliders vs real trunks: {trees} of {expected} instanced trees within {roadM:0} m of the road have a written capsule ({capsules} capsules), measured at 0.3 / 1.0 / 2.0 m above the base; collider beyond trunk: {St(outs)}; trunk beyond collider: {St(ins)}; " +
          $"{over.Count} over the {limit:0} cm limit" + (over.Count > 0 ? $" (worst {string.Join(", ", over.Take(5).Select(x => $"{x.id} {x.g:0} cm at {x.road:0.0} m"))})" : "") + $"; {unfit} more are listed by the fit as keeping a DR2 capsule or still over the limit ({unfitOver} of them over it here)");
        return failures;
    }
}
