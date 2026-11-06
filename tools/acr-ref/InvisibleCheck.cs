using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Physics against visuals, as two height maps over a grid around the route (UE cm): nothing solid may stand above what
/// is drawn. Per cell, at car height (from the drawn ground up to band_m above it):
///   physics top = the highest DR2 collider surface (ground and object mesh colliders, the DR2 boxes and capsules)
///   drawn top   = the highest drawn surface (every visuals section)
/// A cell whose physics top is more than tolerance_m above the drawn top is an invisible obstacle (the car stops at
/// something it cannot see: Argentina 01 r3 in game, 2026-11-01); a cell with physics but nothing drawn is a hole in the
/// visuals. Flagged cells are grouped into 8-connected clusters and reported with their place on the route. The host's
/// own colliders are left out: they are flattened under the DR2 ground (checked in StageBuild.Check).
internal static class InvisibleCheck
{
    internal sealed record Shape(bool Box, V3 Centre, V3 Ax, V3 Ay, V3 Az, double X, double Y, double Z, double Radius, double Length);

    internal sealed record Settings(double RadiusM, double CellM, double ToleranceM, double BandM, double DriveM, double MinAreaM2)
    {
        public static Settings From(Toml cfg)
        {
            double N(string k, double d) => cfg["invisible"].TryGetValue(k, out var v) && v is double x ? x : d;
            return new(N("radius_m", 25), N("cell_m", 0.5), N("tolerance_m", 0.3), N("band_m", 2.5), N("drive_m", 12), N("min_area_m2", 0.5));
        }
    }

    internal sealed class Grid
    {
        public readonly double Cell;
        public readonly Dictionary<(int, int), int> Index = new();
        public readonly List<(int i, int j)> Cells = new();
        public Grid(double cellCm) { Cell = cellCm; }
        public int Add(int i, int j)
        {
            if (Index.TryGetValue((i, j), out var k)) return k;
            Index[(i, j)] = k = Cells.Count; Cells.Add((i, j)); return k;
        }
        public double X(int i) => (i + 0.5) * Cell;
        public double Y(int j) => (j + 0.5) * Cell;
        public int I(double x) => (int)Math.Floor(x / Cell);
    }

    /// Rasterizes a triangle (UE cm) onto the grid cells it covers at their centres: f(cell, z at the centre).
    static void Raster(Grid g, Vector3 a, Vector3 b, Vector3 c, Action<int, double> f)
    {
        double den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        double lo = Math.Min(a.Z, Math.Min(b.Z, c.Z)), hi = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        // (near) vertical in plan, e.g. a billboard card or a wall face: the cells along its edges get its whole height range
        // (sampled every 25 cm), so the part at car height counts; its top alone would be overhead for a tree card
        if (Math.Abs(den) / 2 < 0.1 * g.Cell * g.Cell && hi - lo > 1)
        {
            int nz = Math.Min(64, Math.Max(1, (int)Math.Ceiling((hi - lo) / 25)));
            var hit = new HashSet<int>();
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                double len = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                int n = Math.Max(1, (int)Math.Ceiling(len / (g.Cell / 2)));
                for (int k = 0; k <= n; k++)
                {
                    double u = (double)k / n;
                    if (g.Index.TryGetValue((g.I(p.X + (q.X - p.X) * u), g.I(p.Y + (q.Y - p.Y) * u)), out var id) && hit.Add(id))
                        for (int m = 0; m <= nz; m++) f(id, lo + (hi - lo) * m / nz);
                }
            }
            return;
        }
        if (Math.Abs(den) < 1e-6) return;
        int i0 = g.I(Math.Min(a.X, Math.Min(b.X, c.X))), i1 = g.I(Math.Max(a.X, Math.Max(b.X, c.X)));
        int j0 = g.I(Math.Min(a.Y, Math.Min(b.Y, c.Y))), j1 = g.I(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
        if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 4_000_000) return;   // a degenerate sliver spanning the world
        bool any = false;
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
            {
                if (!g.Index.TryGetValue((i, j), out var id)) continue;
                double x = g.X(i), y = g.Y(j);
                double l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den;
                double l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                f(id, l1 * a.Z + l2 * b.Z + l3 * c.Z); any = true;
            }
        // smaller than a cell: its centroid's cell
        if (!any && g.Index.TryGetValue((g.I((a.X + b.X + c.X) / 3), g.I((a.Y + b.Y + c.Y) / 3)), out var cid))
            f(cid, Math.Max(a.Z, Math.Max(b.Z, c.Z)));
    }

    /// The box's 12 triangles and the capsule as a 12-sided prism with flat ends (UE cm).
    internal static IEnumerable<(Vector3, Vector3, Vector3)> Triangles(Shape s)
    {
        V3 P(double u, double v, double w) => s.Centre + s.Ax * u + s.Ay * v + s.Az * w;
        static Vector3 F(V3 p) => new((float)p.X, (float)p.Y, (float)p.Z);
        if (s.Box)
        {
            var c = new Vector3[8];
            for (int k = 0; k < 8; k++) c[k] = F(P((k & 1) == 0 ? -s.X / 2 : s.X / 2, (k & 2) == 0 ? -s.Y / 2 : s.Y / 2, (k & 4) == 0 ? -s.Z / 2 : s.Z / 2));
            int[][] faces = [[0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]];
            foreach (var f in faces) { yield return (c[f[0]], c[f[1]], c[f[2]]); yield return (c[f[0]], c[f[2]], c[f[3]]); }
            yield break;
        }
        const int n = 12;
        double h = s.Length / 2 + s.Radius;
        var top = new Vector3[n]; var bot = new Vector3[n];
        for (int k = 0; k < n; k++)
        {
            double t = 2 * Math.PI * k / n, u = Math.Cos(t) * s.Radius, v = Math.Sin(t) * s.Radius;
            top[k] = F(P(u, h, v)); bot[k] = F(P(u, -h, v));   // FKSphylElem axis = local Y (stock trunks: Ay vertical)
        }
        var ct = F(P(0, h, 0)); var cb = F(P(0, -h, 0));
        for (int k = 0; k < n; k++)
        {
            int k1 = (k + 1) % n;
            yield return (ct, top[k], top[k1]); yield return (cb, bot[k], bot[k1]);
            yield return (top[k], bot[k], bot[k1]); yield return (top[k], bot[k1], top[k1]);
        }
    }

    /// The triangles (UE cm) the check rasterises for a kept object collider: box, capsule prism or cone mesh (once per face).
    internal static List<(Vector3, Vector3, Vector3)> ColliderTriangles(ObjectColliders.Shape s)
    {
        if (s.Box is { } b)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
            return Triangles(new Shape(true, b.Center, ax, ay, az, b.X, b.Y, b.Z, 0, 0)).ToList();
        }
        if (s.Capsule is { } c)
        {
            var (ax, ay, az) = ObjectColliders.RotationAxes(c.Rot[0], c.Rot[1], c.Rot[2]);
            return Triangles(new Shape(false, c.Center, ax, ay, az, 0, 0, 0, c.Radius, c.Length)).ToList();
        }
        var res = new List<(Vector3, Vector3, Vector3)>();
        if (s.MeshPhysics is null || s.MeshTris is null) return res;
        Vector3 U(uint i) => new(s.MeshPhysics[(int)i].X * 100, s.MeshPhysics[(int)i].Z * 100, s.MeshPhysics[(int)i].Y * 100);
        var seen = new HashSet<(uint, uint, uint)>();
        for (int t = 0; t + 2 < s.MeshTris.Count; t += 3)
        {
            var k = new[] { s.MeshTris[t], s.MeshTris[t + 1], s.MeshTris[t + 2] }; Array.Sort(k);
            if (seen.Add((k[0], k[1], k[2]))) res.Add((U(s.MeshTris[t]), U(s.MeshTris[t + 1]), U(s.MeshTris[t + 2])));
        }
        return res;
    }

    /// The DR2 boxes and capsules (the last <paramref name="added"/> simple entries), decoded as StageBuild.CheckObjects does.
    internal static List<Shape> SimpleShapes(byte[] d, SimpleColliders.Info simple, int added)
    {
        var res = new List<Shape>();
        int host = simple.Entries - added;
        int p = simple.Owners[host - 1].End;
        for (int i = 0; i < added; i++)
        {
            foreach (var s in Unv.ReadHeader(d, ref p))
            {
                if (s.Zero) continue;
                switch (s.Index)
                {
                    case 0 or 2:
                    {
                        int n = BitConverter.ToInt32(d, p); p += 4;
                        for (int k = 0; k < n; k++)
                        {
                            var vals = new Dictionary<int, double[]>();
                            foreach (var e in Unv.ReadHeader(d, ref p))
                            {
                                if (e.Zero) { vals[e.Index] = [0]; continue; }
                                int size = e.Index switch { 0 or 1 => 24, 6 when s.Index == 0 => 8, 5 when s.Index == 2 => 8, 7 when s.Index == 0 => 1, 8 when s.Index == 0 => 1, 6 when s.Index == 2 => 1, 7 when s.Index == 2 => 1, _ => 4 };
                                vals[e.Index] = size == 24 ? [BitConverter.ToDouble(d, p), BitConverter.ToDouble(d, p + 8), BitConverter.ToDouble(d, p + 16)]
                                              : size == 4 ? [BitConverter.ToSingle(d, p)] : [d[p]];
                                p += size;
                            }
                            double[] c = vals.GetValueOrDefault(0) is { Length: 3 } cv ? cv : [0, 0, 0];
                            double[] rot = vals.GetValueOrDefault(1) is { Length: 3 } rv ? rv : [0, 0, 0];
                            var (ax, ay, az) = ObjectColliders.RotationAxes(rot[0], rot[1], rot[2]);
                            double V(int idx) => vals.TryGetValue(idx, out var x) ? x[0] : 0;
                            res.Add(s.Index == 0
                                ? new Shape(true, new V3(c[0], c[1], c[2]), ax, ay, az, V(2), V(3), V(4), 0, 0)
                                : new Shape(false, new V3(c[0], c[1], c[2]), ax, ay, az, 0, 0, 0, V(2), V(3)));
                        }
                        break;
                    }
                    case 1:   // spheres: none written by ObjectColliders
                    {
                        int n = BitConverter.ToInt32(d, p); p += 4;
                        if (n != 0) throw new InvalidDataException("DR2 simple entry with spheres");
                        break;
                    }
                    case 3: p += 4; break;                       // PhysMaterial (FPackageIndex)
                    case 4:                                      // Owner FSoftObjectPath {FName, FName, FString}
                    {
                        p += 16;
                        int len = BitConverter.ToInt32(d, p); p += 4;
                        p += len < 0 ? -len * 2 : len;
                        break;
                    }
                    default: throw new InvalidDataException($"simple entry property {s.Index}");
                }
            }
        }
        return res;
    }

    /// The grid around the route and the drawn surfaces on it: ground height (terrain and road sections) and the top of
    /// everything drawn at car height (up to BandM above that ground; higher geometry such as a banner or canopy is ignored).
    internal sealed class Drawn
    {
        public readonly Settings O;
        public readonly Grid G;
        public readonly List<(V3 p, double s)> Route = new();
        public readonly double[] Ground, Top;
        public readonly int[] TopSection;                      // the section that set Top (-1: none)
        public readonly IReadOnlyList<string>? Names;
        public int N => G.Cells.Count;

        public Drawn(Settings o, List<V3> centre, IReadOnlyList<ProcMesh.Section> drawn, IReadOnlyList<string>? names)
        {
            O = o;
            G = new Grid(o.CellM * 100);
            int r = (int)Math.Ceiling(o.RadiusM * 100 / G.Cell);
            double sAlong = 0;
            for (int k = 0; k + 1 < centre.Count; k++)
            {
                var a = centre[k]; var b = centre[k + 1];
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int n = Math.Max(1, (int)Math.Ceiling(len / G.Cell));
                for (int m = 0; m < n; m++)
                {
                    var q = a + (b - a) * ((double)m / n);
                    Route.Add((q, sAlong + len * m / n));
                    int ci = G.I(q.X), cj = G.I(q.Y);
                    for (int di = -r; di <= r; di++)
                        for (int dj = -r; dj <= r; dj++)
                            if (di * di + dj * dj <= r * r) G.Add(ci + di, cj + dj);
                }
                sAlong += len;
            }
            Ground = new double[N]; Array.Fill(Ground, double.NegativeInfinity);
            Top = new double[N]; Array.Fill(Top, double.NegativeInfinity);
            TopSection = new int[N]; Array.Fill(TopSection, -1);
            Names = names;
            static Vector3 At(ProcMesh.Section s, int t, int k) => s.Pos[(int)s.Idx[t + k]];
            // drawn ground: section 1 = terrain, 2 = road, and their per-tile / per-slice sections
            bool IsGround(int i) => i is 1 or 2 || names is not null && i < names.Count && (names[i].StartsWith("terrain") || names[i].StartsWith("road"));
            var ground = Ground; var top = Top;
            for (int i = 0; i < drawn.Count; i++)
            {
                if (!IsGround(i)) continue;
                var s = drawn[i];
                for (int t = 0; t + 2 < s.Idx.Count; t += 3) Raster(G, At(s, t, 0), At(s, t, 1), At(s, t, 2), (id, z) => { if (z > ground[id]) ground[id] = z; });
            }
            for (int i = 0; i < drawn.Count; i++)
            {
                if (names is not null && i < names.Count && names[i] == "dummy") continue;
                var s = drawn[i];
                int si = i;
                for (int t = 0; t + 2 < s.Idx.Count; t += 3) Raster(G, At(s, t, 0), At(s, t, 1), At(s, t, 2), (id, z) => Add(id, z, si));
            }
        }

        /// Adds a drawn ground triangle after construction (the physics fill, pass by pass): ground height first, then the
        /// drawn top. Tops added before under a lower ground keep their clamp, so a later pass may see slightly less drawn than
        /// a fresh grid would and draw a little more: conservative.
        public void AddGround(Vector3 a, Vector3 b, Vector3 c, int section = -1)
        {
            var ground = Ground;
            Raster(G, a, b, c, (id, z) => { if (z > ground[id]) ground[id] = z; });
            Raster(G, a, b, c, (id, z) => Add(id, z, section));
        }

        /// Adds a drawn surface triangle that is not ground (a proxy of a collider): drawn top only.
        public void AddSurface(Vector3 a, Vector3 b, Vector3 c, int section = -1) => Raster(G, a, b, c, (id, z) => Add(id, z, section));

        /// Rasterizes a physics triangle as the check does: per covered cell its height, clamped to car height (the drawn ground
        /// plus BandM); a triangle wholly above car height is left out.
        public void PhysicsRaster(Vector3 a, Vector3 b, Vector3 c, Action<int, double> f)
        {
            float lo = Math.Min(a.Z, Math.Min(b.Z, c.Z));
            double band = O.BandM * 100;
            Raster(G, a, b, c, (id, z) =>
            {
                if (!double.IsNegativeInfinity(Ground[id]))
                {
                    double top = Ground[id] + band;
                    if (lo > top) return;
                    z = Math.Min(z, top);
                }
                f(id, z);
            });
        }

        /// The highest drawn surface in a cell or its 8 neighbours.
        public double NearTop(int k)
        {
            var (ci, cj) = G.Cells[k];
            double best = Top[k];
            for (int di = -1; di <= 1; di++)
                for (int dj = -1; dj <= 1; dj++)
                    if (G.Index.TryGetValue((ci + di, cj + dj), out var nb) && Top[nb] > best) best = Top[nb];
            return best;
        }

        /// The check's verdict for a cell with physics up to phyZ: 1 invisible obstacle, 2 nothing drawn, 0 fine. Against the
        /// highest drawn surface within one cell: at the edge of a drawn object (a rounded camper corner inside its square box)
        /// the cell centres fall either side of it, and a collider 0.5 m from a visible surface of its height is not invisible.
        public int Flag(int k, double phyZ)
        {
            if (double.IsNegativeInfinity(Top[k])) return 2;
            double tol = O.ToleranceM * 100;
            return phyZ > Top[k] + tol && phyZ > NearTop(k) + tol ? 1 : 0;
        }

        /// Adds a drawn surface point to a cell (clamped to the band; overhead geometry ignored).
        public void Add(int id, double z, int section = -1)
        {
            if (!double.IsNegativeInfinity(Ground[id]))
            {
                double band = Ground[id] + O.BandM * 100;
                if (z > band + 50) return;
                z = Math.Min(z, band);
            }
            if (z > Top[id]) { Top[id] = z; TopSection[id] = section; }
        }

        /// Whether a physics triangle (UE cm) stands more than ToleranceM above everything drawn at car height anywhere it covers.
        public bool Above(Vector3 a, Vector3 b, Vector3 c)
        {
            bool above = false;
            float lo = Math.Min(a.Z, Math.Min(b.Z, c.Z));
            Raster(G, a, b, c, (id, z) =>
            {
                if (above) return;
                if (!double.IsNegativeInfinity(Ground[id]))
                {
                    double band = Ground[id] + O.BandM * 100;
                    if (lo > band) return;
                    z = Math.Min(z, band);
                }
                if (double.IsNegativeInfinity(Top[id]) || z > Top[id] + O.ToleranceM * 100) above = true;
            });
            return above;
        }
    }

    public static int Check(Settings o, StageBuild.Ir ir, StageBuild.Placement pl, List<BakedEdit.Collider> physicsMeshes, List<Shape> shapes,
                            List<ProcMesh.Section> drawn, List<string>? names, TextWriter w, JsonObject? report = null)
    {
        int failures = 0;
        void C(bool ok, string what) { w.WriteLine($"{Advisory.Verdict(ok, what, out var bad)} {what}"); if (bad) failures++; }
        var dg = new Drawn(o, ir.Centre.Select(pl.ToUe).ToList(), drawn, names);
        var g = dg.G; var route = dg.Route; int N = dg.N;
        var visGround = dg.Ground; var visTop = dg.Top;
        var phyTop = new double[N]; Array.Fill(phyTop, double.NegativeInfinity);
        var phySrc = new byte[N];   // 1 collision mesh, 3 box, 4 capsule
        void Phy(Vector3 a, Vector3 b, Vector3 c, byte src) =>
            dg.PhysicsRaster(a, b, c, (id, z) => { if (z > phyTop[id]) { phyTop[id] = z; phySrc[id] = src; } });
        static Vector3 Ue(float[] p, uint i) => new(p[i * 3] * 100, p[i * 3 + 2] * 100, p[i * 3 + 1] * 100);
        foreach (var (col, isObject) in physicsMeshes.Select(c => (c, false)))
            for (int t = 0; t + 2 < col.Indices.Length; t += 3)
                Phy(Ue(col.Positions, col.Indices[t]), Ue(col.Positions, col.Indices[t + 1]), Ue(col.Positions, col.Indices[t + 2]), 1);
        foreach (var sh in shapes)
            foreach (var (a, b, c) in Triangles(sh)) Phy(a, b, c, sh.Box ? (byte)3 : (byte)4);

        // compare
        var flag = new byte[N];   // 1 invisible obstacle, 2 nothing drawn over physics
        for (int k = 0; k < N; k++)
            if (!double.IsNegativeInfinity(phyTop[k])) flag[k] = (byte)dg.Flag(k, phyTop[k]);
        // clusters (8-connected, same flag)
        var seen = new bool[N];
        var clusters = new List<(int flag, int cells, double maxDiff, double cx, double cy, byte src, string drawnTop)>();
        for (int k = 0; k < N; k++)
        {
            if (flag[k] == 0 || seen[k]) continue;
            var stack = new Stack<int>(); stack.Push(k); seen[k] = true;
            int cells = 0; double md = 0, sx = 0, sy = 0; var srcCount = new int[5]; var drawnBy = new Dictionary<int, int>();
            while (stack.Count > 0)
            {
                int c = stack.Pop(); cells++;
                var (ci, cj) = g.Cells[c];
                sx += g.X(ci); sy += g.Y(cj); srcCount[phySrc[c]]++; drawnBy[dg.TopSection[c]] = drawnBy.GetValueOrDefault(dg.TopSection[c]) + 1;
                if (flag[k] == 1) md = Math.Max(md, phyTop[c] - visTop[c]);
                for (int di = -1; di <= 1; di++)
                    for (int dj = -1; dj <= 1; dj++)
                        if (g.Index.TryGetValue((ci + di, cj + dj), out var nb) && !seen[nb] && flag[nb] == flag[k]) { seen[nb] = true; stack.Push(nb); }
            }
            int top = drawnBy.MaxBy(kv => kv.Value).Key;
            clusters.Add((flag[k], cells, md, sx / cells, sy / cells, (byte)Array.IndexOf(srcCount, srcCount.Max()), top < 0 ? "nothing" : names is not null && top < names.Count ? names[top] : $"section {top}"));
        }
        double cellArea = o.CellM * o.CellM;
        string Src(byte s) => s switch { 1 => "collision mesh", 3 => "box", 4 => "capsule", _ => "?" };
        (double s, double lat) Where(double x, double y)
        {
            double best = double.MaxValue, bs = 0, lat = 0;
            for (int k = 0; k + 1 < route.Count; k += 4)
            {
                double d = Math.Sqrt((route[k].p.X - x) * (route[k].p.X - x) + (route[k].p.Y - y) * (route[k].p.Y - y));
                if (d < best)
                {
                    best = d; bs = route[k].s;
                    var nxt = route[Math.Min(k + 4, route.Count - 1)].p;
                    double hx = nxt.X - route[k].p.X, hy = nxt.Y - route[k].p.Y;
                    lat = Math.Sign(hx * (y - route[k].p.Y) - hy * (x - route[k].p.X)) * d;   // + = left of travel
                }
            }
            return (bs / 100, lat / 100);
        }
        var big = clusters.Where(c => c.cells * cellArea >= o.MinAreaM2).Select(c => (c, w: Where(c.cx, c.cy))).ToList();
        // a collider over its own drawn object (a vehicle box above the bonnet): the object is seen and hit, a little early;
        // reported, not failed. Over drawn ground (or nothing) it is an invisible obstacle.
        static bool OverObject(string top) => top.StartsWith("objects:") || top is "posts" or "gates";
        var coarse = big.Where(x => x.c.flag == 1 && OverObject(x.c.drawnTop)).ToList();
        var obstacles = big.Where(x => x.c.flag == 1 && !OverObject(x.c.drawnTop)).OrderBy(x => Math.Abs(x.w.lat)).ToList();
        var holes = big.Where(x => x.c.flag == 2).ToList();
        var near = obstacles.Where(x => Math.Abs(x.w.lat) <= o.DriveM).ToList();
        var list = new JsonArray();
        foreach (var (c, at) in obstacles.Take(60))
            list.Add($"{(c.flag == 1 ? "obstacle" : "undrawn")} at {at.s:0} m, {(at.lat >= 0 ? "left" : "right")} {Math.Abs(at.lat):0.0} m: {c.cells * cellArea:0.##} m2, up to {c.maxDiff / 100:0.00} m above the drawn surface ({Src(c.src)} over {c.drawnTop}); UE ({c.cx / 100:0.0}, {c.cy / 100:0.0}) m");
        if (report is not null)
            report["invisible"] = new JsonObject
            {
                ["cells"] = N, ["cell_m"] = o.CellM, ["radius_m"] = o.RadiusM, ["tolerance_m"] = o.ToleranceM, ["band_m"] = o.BandM,
                ["obstacles"] = obstacles.Count, ["coarse_over_drawn_objects"] = coarse.Count, ["obstacles_within_drive_m"] = near.Count, ["undrawn"] = holes.Count, ["list"] = list,
            };
        foreach (var (c, at) in near.Take(25))
            w.WriteLine($"     invisible: {at.s:0} m along the route, {(at.lat >= 0 ? "left" : "right")} {Math.Abs(at.lat):0.0} m: {c.cells * cellArea:0.##} m2 up to " +
                        $"{c.maxDiff / 100:0.00} m above the drawn surface ({Src(c.src)} over {c.drawnTop}), UE ({c.cx / 100:0.0}, {c.cy / 100:0.0}) m");
        C(near.Count == 0,
          $"invisible obstacles: {near.Count} within {o.DriveM} m of the centreline (+ {coarse.Count} colliders standing over their own drawn object) ({obstacles.Count} within {o.RadiusM} m, clusters of {o.MinAreaM2} m2+ where a collider " +
          $"stands over {o.ToleranceM} m above everything drawn at car height); {holes.Count} patches of physics with nothing drawn; {N} cells of {o.CellM} m");
        return failures;
    }
}
