using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AcrRef;

/// Cooked StaticMesh packages written without the editor (docs/instancing-plan.md, part 1): a legacy package of an ACR tree
/// mesh (SM_SpruceWales06, UE 5.6 cooked, no Nanite) is cloned and its render data replaced by our own geometry.
///
/// Layout of the StaticMesh export, found from the bytes of SM_SpruceWales06 / SM_WildGrass01 / SM_BroomCreeper01 (all three
/// agree) and re-checked by CUE4Parse on the result:
///   unversioned properties: 8-byte header (5 values: StaticMaterials, LightmapUVDensity, DistanceFieldSelfShadowBias,
///     ExtendedBounds, ShadowLodIndex), int32 material count, per material 31 bytes (struct header 80 0b + zero mask 10, imported
///     MIC index, slot FName, imported-slot FName, UVChannelData 80 0d 3a 01 + f32 density), then 74 bytes: two f32, ExtendedBounds
///     (header 00 07 + 7 f64), 8 bytes kept as they are;
///   FStripDataFlags (2), bCooked (4), BodySetup (4), NavCollision (4), LightingGuid (16), sockets (4), int32 LOD count;
///   per LOD: strip flags (2), int32 sections, 40 bytes each (material, first index, triangles, min vertex, max vertex, and five
///     flags), bounds (7 f64), 16 bytes (max deviation, cooked-out, inlined flags), position buffer (strip 2, stride, count,
///     bulk element size, bulk count, f32 x3), tangent buffer (strip 2, texcoords, count, 2 precision flags, bulk 8 bytes per
///     vertex: TangentX and TangentZ as signed-normalised bytes xyzw, bulk of half-float UV pairs), colour buffer (strip 2, stride,
///     count, [bulk]), index buffer (b32 flag, bulk of bytes), reversed / depth-only / reversed-depth-only index buffers
///     (0 0 1 count + data each), 4 + 3 x sections empty area-weighted sampler ints, buffer sizes (total, depth-only, reversed);
///   tail: NumInlinedLODs (u8), 94 bytes of empty Nanite resources, strip flags + valid flag, Lumen card bounds (2 x 3 f64 + valid
///     byte), 4 bytes, int32 card count + 61 bytes per card (15 f32 + direction), 4 zero bytes per LOD after the first,
///     05 01, bounds (7 f64), u8, 8 x (int32 1 + f32) screen sizes, 6 bytes, int32 material count + 40 bytes per material
///     (MIC import, slot FName, bInitialized, bOverrideDensities, 4 x f32 density, overlay).
/// The BodySetup keeps its properties but loses its capsule: no collision shapes, and its cooked Chaos blob is the empty one.
internal static class StaticMeshPackage
{
    public const int LodSectionSize = 40;

    /// Geometry of one LOD in the mesh's local space (UE cm): vertices are shared by the sections, which list triangles.
    internal sealed class Geometry
    {
        public List<Vector3> Pos = new(), Nrm = new(), Tan = new();
        public List<Vector2> Uv = new();
        public List<Section> Sections = new();
        public sealed record Section(int Material, List<uint> Idx);

        public (Vector3 mn, Vector3 mx) Box() => ProcMesh.Bounds(Pos);
    }

    internal sealed record Material(string Mic, string Slot, float Density);

    // ---- reading ---------------------------------------------------------------------------------------------------------

    internal sealed record SectionInfo(int Material, int FirstIndex, int Triangles, int MinVertex, int MaxVertex);
    internal sealed class LodInfo
    {
        public int Start, End, SectionsAt, BoundsAt, Hdr16At, PosAt, Vertices, PosData, TanAt, TexCoords, TanData, UvData, ColAt, ColVertices, IbAt, IbBytes, IbData, RecsAt, ZeroAt, SizesAt;
        public bool Index32;
        public List<SectionInfo> Sections = new();
        public int[] Sizes = [];
    }
    internal sealed class Info
    {
        public int Materials, MaterialsAt, PropsTailAt, PropsEnd, GuidAt, NumLodsAt, TailAt;
        public List<(int import, int name, float density)> Mats = new();
        public List<LodInfo> Lods = new();
        public int CardsAt, Cards, TailBoundsAt, ScreenAt, TailMatCountAt, TailMats;
        public double[] ExtendedBounds = [], TailBounds = [], CardBounds = [];
        public float[] ScreenSize = [];
        public int NumInlinedLods;
    }

    static readonly byte[] PropsHeader = [0x03, 0x04, 0x05, 0x02, 0x0f, 0x02, 0x01, 0x03];

    public static Info Read(byte[] d)
    {
        var info = new Info();
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
        double F64(int at) => BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(at));
        if (!d.AsSpan(0, 8).SequenceEqual(PropsHeader)) throw new InvalidDataException("StaticMesh property header is not the expected 5 values");
        int n = I32(8), p = 12;
        info.Materials = n; info.MaterialsAt = p;
        for (int i = 0; i < n; i++, p += 31)
        {
            if (d[p] != 0x80 || d[p + 1] != 0x0b || d[p + 2] != 0x10 || d[p + 23] != 0x80 || d[p + 24] != 0x0d || d[p + 25] != 0x3a || d[p + 26] != 0x01)
                throw new InvalidDataException($"StaticMaterials[{i}] is not the expected struct layout");
            info.Mats.Add((I32(p + 3), I32(p + 7), BitConverter.ToSingle(d, p + 27)));
        }
        info.PropsTailAt = p;
        if (d[p + 8] != 0x00 || d[p + 9] != 0x07) throw new InvalidDataException("ExtendedBounds header");
        info.ExtendedBounds = Enumerable.Range(0, 7).Select(k => F64(p + 10 + k * 8)).ToArray();
        info.PropsEnd = p + 74;
        info.GuidAt = info.PropsEnd + 14;
        info.NumLodsAt = info.GuidAt + 20;
        if (I32(info.PropsEnd + 2) != 1) throw new InvalidDataException("StaticMesh is not cooked");
        if (I32(info.GuidAt + 16) != 0) throw new InvalidDataException("StaticMesh has sockets");
        int lods = I32(info.NumLodsAt);
        p = info.NumLodsAt + 4;
        for (int l = 0; l < lods; l++)
        {
            var L = new LodInfo { Start = p };
            p += 2;
            int ns = I32(p); p += 4; L.SectionsAt = p;
            for (int s = 0; s < ns; s++, p += LodSectionSize)
                L.Sections.Add(new SectionInfo(I32(p), I32(p + 4), I32(p + 8), I32(p + 12), I32(p + 16)));
            L.BoundsAt = p; p += 56; L.Hdr16At = p; p += 16;
            L.PosAt = p; p += 2;
            if (I32(p) != 12) throw new InvalidDataException("position stride");
            p += 4;
            L.Vertices = I32(p); p += 4;
            int e = I32(p), c = I32(p + 4); p += 8; L.PosData = p; p += e * c;
            if (c != L.Vertices || e != 12) throw new InvalidDataException("position bulk");
            L.TanAt = p; p += 2; L.TexCoords = I32(p); p += 4;
            if (I32(p) != L.Vertices) throw new InvalidDataException("tangent buffer vertex count");
            p += 4;
            p += 8;                                                   // precision flags
            int te = I32(p), tc = I32(p + 4); p += 8; L.TanData = p; p += te * tc;
            if (te != 8 || tc != L.Vertices) throw new InvalidDataException("tangent bulk");
            int ue = I32(p), uc = I32(p + 4); p += 8; L.UvData = p; p += ue * uc;
            if (ue != 4 || uc != L.Vertices * L.TexCoords) throw new InvalidDataException("uv bulk");
            L.ColAt = p; p += 2; p += 4; L.ColVertices = I32(p); p += 4;
            if (L.ColVertices > 0) { int ce = I32(p), cc = I32(p + 4); p += 8 + ce * cc; }
            L.IbAt = p; L.Index32 = I32(p) != 0; p += 4;
            if (I32(p) != 1) throw new InvalidDataException("index bulk element size");
            p += 4;
            L.IbBytes = I32(p); p += 4; L.IbData = p; p += L.IbBytes;
            L.RecsAt = p;
            for (int r = 0; r < 3; r++)
            {
                if (I32(p) != 0 || I32(p + 4) != 0 || I32(p + 8) != 1) throw new InvalidDataException($"index buffer record {r}");
                p += 12; int cnt = I32(p); p += 4 + cnt;
            }
            L.ZeroAt = p; p += (4 + 3 * ns) * 4;
            L.SizesAt = p; L.Sizes = [I32(p), I32(p + 4), I32(p + 8)]; p += 12;
            L.End = p;
            info.Lods.Add(L);
        }
        info.TailAt = p;
        info.NumInlinedLods = d[p];
        info.CardBounds = Enumerable.Range(0, 6).Select(k => F64(p + 101 + k * 8)).ToArray();
        info.CardsAt = p + 158; info.Cards = I32(p + 154);
        int q = info.CardsAt + 61 * info.Cards + 4 * (lods - 1);
        if (d[q] != 0x05 || d[q + 1] != 0x01) throw new InvalidDataException("tail: expected 05 01 after the cards");
        info.TailBoundsAt = q + 2;
        info.TailBounds = Enumerable.Range(0, 7).Select(k => F64(info.TailBoundsAt + k * 8)).ToArray();
        info.ScreenAt = info.TailBoundsAt + 56 + 1;
        info.ScreenSize = Enumerable.Range(0, 8).Select(k => BitConverter.ToSingle(d, info.ScreenAt + k * 8 + 4)).ToArray();
        info.TailMatCountAt = info.ScreenAt + 64 + 6;
        info.TailMats = I32(info.TailMatCountAt);
        if (info.TailMatCountAt + 4 + 40 * info.TailMats != d.Length) throw new InvalidDataException($"tail ends at {info.TailMatCountAt + 4 + 40 * info.TailMats}, export has {d.Length} bytes");
        return info;
    }

    public static Vector3[] Positions(byte[] d, LodInfo l) =>
        Enumerable.Range(0, l.Vertices).Select(i => new Vector3(BitConverter.ToSingle(d, l.PosData + i * 12), BitConverter.ToSingle(d, l.PosData + i * 12 + 4), BitConverter.ToSingle(d, l.PosData + i * 12 + 8))).ToArray();

    public static uint[] Indices(byte[] d, LodInfo l)
    {
        int n = l.Index32 ? l.IbBytes / 4 : l.IbBytes / 2;
        return Enumerable.Range(0, n).Select(i => l.Index32 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(l.IbData + i * 4)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(l.IbData + i * 2))).ToArray();
    }

    // ---- writing ---------------------------------------------------------------------------------------------------------

    static void Box7(BinaryWriter w, Vector3 mn, Vector3 mx, IEnumerable<Vector3> pts)
    {
        var c = (mn + mx) * 0.5f; var ext = (mx - mn) * 0.5f;
        double r = pts.Any() ? pts.Max(p => (double)Vector3.Distance(p, c)) : 0;
        foreach (var v in new[] { c.X, c.Y, c.Z, ext.X, ext.Y, ext.Z }) w.Write((double)v);
        w.Write(r);
    }

    static sbyte S8(float v) => (sbyte)Math.Clamp((int)MathF.Round(v * 127f), -127, 127);

    static byte[] WriteLod(byte[] tpl, LodInfo t, Geometry g, int texCoords)
    {
        int nv = g.Pos.Count;
        if (nv == 0 || nv > 65535) throw new NotSupportedException($"LOD with {nv} vertices (16-bit indices only)");
        if (g.Nrm.Count != nv || g.Tan.Count != nv || g.Uv.Count != nv) throw new ArgumentException("vertex attribute counts differ");
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(tpl, t.Start, 2);
        w.Write(g.Sections.Count);
        var idx = new List<uint>();
        foreach (var s in g.Sections)
        {
            var sec = tpl.AsSpan(t.SectionsAt, LodSectionSize).ToArray();
            uint mn = s.Idx.Min(), mx = s.Idx.Max();
            BinaryPrimitives.WriteInt32LittleEndian(sec.AsSpan(0), s.Material);
            BinaryPrimitives.WriteInt32LittleEndian(sec.AsSpan(4), idx.Count);
            BinaryPrimitives.WriteInt32LittleEndian(sec.AsSpan(8), s.Idx.Count / 3);
            BinaryPrimitives.WriteInt32LittleEndian(sec.AsSpan(12), (int)mn);
            BinaryPrimitives.WriteInt32LittleEndian(sec.AsSpan(16), (int)mx);
            w.Write(sec);
            idx.AddRange(s.Idx);
        }
        var (bmn, bmx) = g.Box();
        Box7(w, bmn, bmx, g.Pos);
        w.Write(tpl, t.Hdr16At, 16);
        // positions
        w.Write(tpl, t.PosAt, 2); w.Write(12); w.Write(nv); w.Write(12); w.Write(nv);
        foreach (var p in g.Pos) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        // tangents (TangentX, TangentZ: signed-normalised xyzw, w = binormal sign) and half-float UVs
        w.Write(tpl, t.TanAt, 2); w.Write(texCoords); w.Write(nv); w.Write(0); w.Write(0); w.Write(8); w.Write(nv);
        for (int i = 0; i < nv; i++)
        {
            var tx = g.Tan[i]; var nz = g.Nrm[i];
            w.Write(S8(tx.X)); w.Write(S8(tx.Y)); w.Write(S8(tx.Z)); w.Write((sbyte)127);
            w.Write(S8(nz.X)); w.Write(S8(nz.Y)); w.Write(S8(nz.Z)); w.Write((sbyte)127);
        }
        w.Write(4); w.Write(nv * texCoords);
        for (int i = 0; i < nv; i++)
            for (int c = 0; c < texCoords; c++)
            {
                w.Write(BitConverter.HalfToUInt16Bits((Half)g.Uv[i].X)); w.Write(BitConverter.HalfToUInt16Bits((Half)g.Uv[i].Y));
            }
        // no vertex colours (like the template's own far LODs)
        w.Write(tpl, t.ColAt, 2); w.Write(0); w.Write(0);
        // indices, then the (identical) depth-only copy between two empty buffers
        var ib = new byte[idx.Count * 2];
        for (int i = 0; i < idx.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(ib.AsSpan(i * 2), (ushort)idx[i]);
        w.Write(0); w.Write(1); w.Write(ib.Length); w.Write(ib);
        w.Write(0); w.Write(0); w.Write(1); w.Write(0);
        w.Write(0); w.Write(0); w.Write(1); w.Write(ib.Length); w.Write(ib);
        w.Write(0); w.Write(0); w.Write(1); w.Write(0);
        w.Write(new byte[(4 + 3 * g.Sections.Count) * 4]);
        int total = nv * 12 + nv * 8 + nv * texCoords * 4 + ib.Length * 2;
        w.Write(total); w.Write(ib.Length); w.Write(0);
        return ms.ToArray();
    }

    /// Lumen cards of the box with a little margin: -X +X -Y +Y -Z +Z, like the template's six.
    static byte[] Cards(Vector3 mn, Vector3 mx)
    {
        var c = (mn + mx) * 0.5f; var h = (mx - mn) * 0.5f;
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void V(float x, float y, float z) { w.Write(x); w.Write(y); w.Write(z); }
        var ax = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        float[] hc = [h.X, h.Y, h.Z], lo = [mn.X, mn.Y, mn.Z], hi = [mx.X, mx.Y, mx.Z], ctr = [c.X, c.Y, c.Z];
        for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
                float depth = Math.Max(hc[axis] * 0.9f, 1f);
                float face = sign < 0 ? lo[axis] - 10 : hi[axis] + 10;
                var o = (float[])ctr.Clone(); o[axis] = face - sign * depth;
                var au = ax[u]; var av = ax[v]; var az = ax[axis] * sign;
                V(au.X, au.Y, au.Z); V(av.X, av.Y, av.Z); V(az.X, az.Y, az.Z);
                V(o[0], o[1], o[2]);
                V(Math.Max(hc[u], 1f), Math.Max(hc[v], 1f), depth);
                w.Write((byte)(axis * 2 + (sign < 0 ? 0 : 1)));
            }
        return ms.ToArray();
    }

    /// Writes `newPackage` (a /Game path) under outRoot from the template package (found under legacyRoot).
    /// Returns the object path ("/Game/...SM_X.SM_X") and a short report.
    public static (string objectPath, string report) Write(string legacyRoot, string templatePackage, string newPackage, IReadOnlyList<Geometry> lods,
                                                           IReadOnlyList<Material> mats, IReadOnlyList<float> screenSize, string outRoot)
    {
        var g = lods[0];
        if (lods.Count != screenSize.Count || lods.Count > 4) throw new ArgumentException("one screen size per LOD, at most 4 LODs");
        string File(string root, string pkgName) => Path.Combine(root, "acr", "Content", pkgName["/Game/".Length..].Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        var pkg = new LegacyPackage(File(legacyRoot, templatePackage));
        if (pkg.ExportCount != 2 || pkg.ExportClass(0) != "BodySetup" || pkg.ExportClass(1) != "StaticMesh")
            throw new InvalidDataException($"{templatePackage}: expected exports BodySetup + StaticMesh, found {string.Join(", ", Enumerable.Range(0, pkg.ExportCount).Select(pkg.ExportClass))}");
        var tpl = pkg.GetExportData(1);
        var info = Read(tpl);
        if (info.Lods.Count < 1) throw new InvalidDataException("template has no LOD");
        var lod0 = info.Lods[0];
        string obj = newPackage[(newPackage.LastIndexOf('/') + 1)..];
        if (pkg.Names[10] != pkg.ExportName(1) || pkg.Names[11] != pkg.PackageName)
            throw new InvalidDataException($"{templatePackage}: name map is not [..., object, package] at 10, 11");
        int nMats = mats.Count;
        if (lods.Any(l => l.Sections.Any(s => s.Material < 0 || s.Material >= nMats))) throw new ArgumentException("section material out of range");

        // names used by the export data come first in the name map: slot names, then rename object and package
        var slotNames = mats.Select(m => pkg.AddExportName(m.Slot)).ToArray();
        pkg.RenameName(10, obj);
        pkg.RenameName(11, newPackage);
        pkg.SetPackageName(newPackage);

        // imports: drop the template's materials (and their packages), import ours
        var micIdx = Enumerable.Range(0, pkg.ImportCount).Where(i => pkg.ImportClassName(i) == "MaterialInstanceConstant").ToList();
        var pkgIdx = micIdx.Select(i => -pkg.ImportOuter(i) - 1).Distinct().ToList();
        if (pkgIdx.Any(i => pkg.ImportClassName(i) != "Package")) throw new InvalidDataException("a material import's outer is not a package");
        int staticMeshClass = -(Enumerable.Range(0, pkg.ImportCount).Single(i => pkg.ImportName(i) == "StaticMesh" && pkg.ImportClassName(i) == "Class") + 1);
        int defaultSm = -(Enumerable.Range(0, pkg.ImportCount).Single(i => pkg.ImportName(i) == "Default__StaticMesh") + 1);
        pkg.SetExportDeps(1, [], [1], [staticMeshClass, defaultSm], []);
        pkg.RemoveImports(micIdx.Concat(pkgIdx));
        var micImports = new Dictionary<string, int>();
        foreach (var m in mats.Select(m => m.Mic).Distinct())
        {
            int pi = pkg.AddImport("/Script/CoreUObject", "Package", 0, m);
            micImports[m] = pkg.AddImport("/Script/Engine", "MaterialInstanceConstant", pi, m[(m.LastIndexOf('/') + 1)..]);
        }
        // dependencies were remapped by RemoveImports; the new materials go before the body setup
        var sbc = pkg.ExportDeps(1).Where(x => x.kind == "SBC").Select(x => x.index).ToList();
        pkg.SetExportDeps(1, [], micImports.Values.Concat([1]).ToList(), sbc, []);

        // ---- StaticMesh export
        var bounds = g.Box();
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(tpl, 0, 8);
        w.Write(nMats);
        for (int i = 0; i < nMats; i++)
        {
            w.Write((byte)0x80); w.Write((byte)0x0b); w.Write((byte)0x10);
            w.Write(micImports[mats[i].Mic]); w.Write(slotNames[i]); w.Write(0); w.Write(slotNames[i]); w.Write(0);
            w.Write((byte)0x80); w.Write((byte)0x0d); w.Write((byte)0x3a); w.Write((byte)0x01); w.Write(Math.Max(mats[i].Density, 1f));
        }
        var centre = (bounds.mn + bounds.mx) * 0.5f; var half = (bounds.mx - bounds.mn) * 0.5f;
        double radius = g.Pos.Max(p => (double)Vector3.Distance(p, centre));
        var propsTail = tpl.AsSpan(info.PropsTailAt, 74).ToArray();
        {
            var vals = new[] { centre.X, centre.Y, centre.Z, half.X, half.Y, half.Z };
            for (int k = 0; k < 6; k++) BinaryPrimitives.WriteDoubleLittleEndian(propsTail.AsSpan(10 + k * 8), vals[k]);
            BinaryPrimitives.WriteDoubleLittleEndian(propsTail.AsSpan(10 + 48), radius);
        }
        w.Write(propsTail);
        w.Write(tpl, info.PropsEnd, 14);
        w.Write(MD5.HashData(Encoding.UTF8.GetBytes("lighting:" + newPackage)));
        w.Write(0);                                   // sockets
        w.Write(lods.Count);                          // LOD count
        foreach (var l in lods) w.Write(WriteLod(tpl, lod0, l, 1));
        long tailAt = ms.Position;
        // tail: the template's, with the parts that depend on the mesh replaced
        var tw = w;
        int T = info.TailAt;
        tw.Write((byte)lods.Count);
        tw.Write(tpl, T + 1, 100);                    // empty Nanite resources, strip flags + valid flag (up to the card bounds)
        var cardMin = bounds.mn - new Vector3(10); var cardMax = bounds.mx + new Vector3(10);
        foreach (var v in new[] { cardMin.X, cardMin.Y, cardMin.Z, cardMax.X, cardMax.Y, cardMax.Z }) tw.Write((double)v);
        tw.Write(tpl, T + 149, 5);                    // valid byte, flag
        var cards = Cards(bounds.mn, bounds.mx);
        tw.Write(cards.Length / 61); tw.Write(cards);
        tw.Write(new byte[4 * (lods.Count - 1)]);     // no cards for the further LODs
        tw.Write((byte)5); tw.Write((byte)1);
        foreach (var v in new[] { centre.X, centre.Y, centre.Z, half.X, half.Y, half.Z }) tw.Write((double)v);
        tw.Write(radius);
        tw.Write((byte)0);                            // bLODsShareStaticLighting
        for (int k = 0; k < 8; k++) { tw.Write(1); tw.Write(k < lods.Count ? screenSize[k] : 0.0f); }
        tw.Write(tpl, info.TailMatCountAt - 6, 6);
        tw.Write(nMats);
        for (int i = 0; i < nMats; i++)
        {
            tw.Write(micImports[mats[i].Mic]); tw.Write(slotNames[i]); tw.Write(0);
            tw.Write(1); tw.Write(0); tw.Write(Math.Max(mats[i].Density, 1f)); tw.Write(0f); tw.Write(0f); tw.Write(0f); tw.Write(0);
        }
        var sm = ms.ToArray();
        pkg.SetExportData(1, sm);

        // ---- BodySetup: no capsule (SphylElems count 0), unique guid
        var bs = pkg.GetExportData(0);
        if (BinaryPrimitives.ReadInt32LittleEndian(bs.AsSpan(8)) != 1 || bs[12] != 0x80 || bs[13] != 0x11)
            throw new InvalidDataException("BodySetup: expected one SphylElem right after the 8-byte header");
        const int elem = 61;
        var nb = new byte[bs.Length - elem];
        bs.AsSpan(0, 8).CopyTo(nb);
        BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(8), 0);
        bs.AsSpan(12 + elem).CopyTo(nb.AsSpan(12));
        MD5.HashData(Encoding.UTF8.GetBytes("body:" + newPackage)).CopyTo(nb, 0x5b - elem);
        pkg.SetExportData(0, nb);

        // ---- data resources: BodySetup's inline Chaos blob moved up by the removed capsule, the empty Nanite pages at the tail
        var dr = pkg.DataResources();
        if (BinaryPrimitives.ReadInt32LittleEndian(dr.AsSpan(4)) != 2) throw new InvalidDataException("expected 2 data resources");
        var ndr = (byte[])dr.Clone();
        long o0 = BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + 4)), o1 = BinaryPrimitives.ReadInt64LittleEndian(dr.AsSpan(8 + LegacyPackage.DataResourceSize + 4));
        if (o0 + 28 != bs.Length || o1 != info.TailAt + 11) throw new InvalidDataException($"data resources at {o0}, {o1}: not the expected template layout (body {bs.Length}, tail {info.TailAt})");
        BinaryPrimitives.WriteInt64LittleEndian(ndr.AsSpan(8 + 4), nb.Length - 28);
        BinaryPrimitives.WriteInt64LittleEndian(ndr.AsSpan(8 + LegacyPackage.DataResourceSize + 4), tailAt + 11);
        pkg.SetDataResources(ndr);

        pkg.SaveTo(File(outRoot, newPackage));
        return ($"{newPackage}.{obj}", $"{lods.Count} LOD(s): {string.Join(" + ", lods.Select(l => $"{l.Pos.Count} vertices / {l.Sections.Sum(s => s.Idx.Count / 3)} triangles in {l.Sections.Count} sections"))}, {nMats} materials, " +
                $"bounds ({bounds.mn.X:0},{bounds.mn.Y:0},{bounds.mn.Z:0})..({bounds.mx.X:0},{bounds.mx.Y:0},{bounds.mx.Z:0}) cm, export {sm.Length} bytes");
    }
}
