using System.Numerics;

namespace AcrRef;

/// Synthetic checks without stage or game data: acr-ref procmesh-selftest.
internal static class ProcMeshSelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string label) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}"); if (!ok) failures++; }
        bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
        ProcMesh.Section Triangle(Vector2 a, Vector2 b, Vector2 c) => new()
        {
            Pos = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Nrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], Uv = [a, b, c], Idx = [0, 1, 2]
        };
        var ordinary = Triangle(Vector2.Zero, Vector2.UnitX, Vector2.UnitY);
        var mirrored = Triangle(Vector2.Zero, -Vector2.UnitX, Vector2.UnitY);
        var rotated = Triangle(Vector2.Zero, Vector2.UnitY, -Vector2.UnitX);
        foreach (var (s, expected, v, label) in new[] {
            (ordinary, Vector3.UnitX, Vector3.UnitY, "ordinary"),
            (mirrored, -Vector3.UnitX, Vector3.UnitY, "mirrored"),
            (rotated, -Vector3.UnitY, Vector3.UnitX, "rotated") })
        {
            ProcMesh.EnsureUvTangents(s);
            Check(s.Tan.All(t => Near(t, expected)), label + " U tangent");
            var b = Vector3.Cross(Vector3.UnitZ, s.Tan[0]) * (s.FlipTanY[0] ? -1 : 1);
            Check(Near(b, -v), label + " DirectX green follows -V");
            var gt = Preview.GltfTangent(s.Tan[0], s.FlipTanY[0]);
            Vector3 Reflect(Vector3 p) => new(p.X, p.Z, p.Y);
            var gb = Vector3.Cross(Reflect(Vector3.UnitZ), new(gt.X, gt.Y, gt.Z)) * gt.W;
            Check(Near(-gb, Reflect(b)), label + " glTF reflection + green parity");
        }
        foreach (var uv in new[] { Vector2.Zero, new Vector2(float.NaN, 0) })
        {
            var degenerate = Triangle(uv, uv, uv);
            degenerate.Pos.Add(Vector3.One); degenerate.Nrm.Add(Vector3.Zero); degenerate.Uv.Add(uv);
            ProcMesh.EnsureUvTangents(degenerate);
            Check(degenerate.Tan.All(t => float.IsFinite(t.Length()) && Math.Abs(t.Length() - 1) < 1e-5f),
                "degenerate/nonfinite UV and isolated invalid normal: finite unit fallback");
        }
        var skew = Triangle(Vector2.Zero, Vector2.UnitX, Vector2.UnitY);
        skew.Nrm = Enumerable.Repeat(Vector3.Normalize(new Vector3(1, 2, 3)), 3).ToList();
        ProcMesh.EnsureUvTangents(skew);
        Check(skew.Tan.All(t => Math.Abs(Vector3.Dot(t, skew.Nrm[0])) < 1e-5f), "Gram-Schmidt orthogonality");
        var accumulated = Triangle(Vector2.Zero, Vector2.UnitX, Vector2.UnitY);
        accumulated.Pos.Add(-Vector3.UnitY); accumulated.Pos.Add(Vector3.UnitX);
        accumulated.Nrm.Add(Vector3.UnitZ); accumulated.Nrm.Add(Vector3.UnitZ);
        accumulated.Uv.Add(Vector2.UnitX); accumulated.Uv.Add(Vector2.UnitY);
        accumulated.Idx.AddRange([0, 3, 4]);
        ProcMesh.EnsureUvTangents(accumulated);
        Check(Near(accumulated.Tan[0], Vector3.Normalize(Vector3.UnitX - Vector3.UnitY)),
            "shared vertex accumulates derivatives from both triangles");
        var authored = Triangle(Vector2.Zero, Vector2.UnitX, Vector2.UnitY);
        authored.Tan = Enumerable.Repeat(Vector3.UnitY, 3).ToList();
        ProcMesh.EnsureUvTangents(authored);
        Check(authored.Tan.All(t => t == Vector3.UnitY) && authored.FlipTanY.Count == 0, "authored tangents preserved");
        using var ms = new MemoryStream(); using var writer = new BinaryWriter(ms);
        ordinary.FlipTanY = [false, true, false]; ordinary.Collision = true;
        ProcMesh.WriteSection(writer, ordinary); ProcMesh.WriteSection(writer, mirrored);
        var bytes = ms.ToArray(); int cursor = 0;
        var read = ProcMesh.ReadSection(bytes, ref cursor, out _);
        var read2 = ProcMesh.ReadSection(bytes, ref cursor, out _);
        Check(cursor == bytes.Length && read.Collision && read.FlipTanY.SequenceEqual(ordinary.FlipTanY)
            && read2.Pos.SequenceEqual(mirrored.Pos), "mixed bool byte/zero-mask payloads and following section alignment");
        using var round = new MemoryStream(); using var rw = new BinaryWriter(round);
        ProcMesh.WriteSection(rw, read); ProcMesh.WriteSection(rw, read2);
        Check(round.ToArray().SequenceEqual(bytes), "section byte-exact round trip with true bools");
        var legacy = Triangle(Vector2.Zero, Vector2.UnitX, Vector2.UnitY);
        using var old = new MemoryStream(); using var ow = new BinaryWriter(old);
        ProcMesh.WriteSection(ow, legacy, false); cursor = 0;
        var oldRead = ProcMesh.ReadSection(old.ToArray(), ref cursor, out _);
        Check(legacy.Tan.Count == 0 && oldRead.Tan.All(t => t == ProcMesh.NormalTangent(Vector3.UnitZ))
            && oldRead.FlipTanY.Count == 0, "normal mode keeps legacy basis and false handedness");
        byte[] normal = [128, 192, 0, 0]; Preview.ConvertNormalForGltf(normal);
        Check(normal[1] == 63 && normal[2] > 128 && normal[3] == 255, "BC5 Z reconstruction and green conversion");
        return failures == 0 ? 0 : 1;
    }
}
