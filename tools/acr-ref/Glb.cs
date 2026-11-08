using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Numerics;

namespace AcrRef;

/// Minimal binary glTF reader: triangle primitives with POSITION and indices, by material name.
/// Primitives reads baked stage geometry; NodePrimitives applies scene transforms for prototype assets.
internal sealed class Glb
{
    public JsonNode Json { get; }
    readonly byte[] _bin;

    public sealed record Primitive(string Mesh, string Material, float[] Positions, uint[] Indices)
    {
        /// TEXCOORD_n (2 floats per vertex), null if absent; filled by Primitives(withUv: true).
        public float[]?[] Uv { get; init; } = [];
        /// COLOR_0 (4 floats per vertex, 0..1), null if absent; filled by Primitives(withUv: true).
        public float[]? Color { get; init; }
        /// NORMAL (3 floats per vertex), null if absent; filled by Primitives(withUv: true).
        public float[]? Normal { get; init; }
    }

    public Glb(string path)
    {
        var d = File.ReadAllBytes(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x46546C67) throw new InvalidDataException($"{path} is not a glb");
        int p = 12;
        JsonNode? json = null;
        byte[]? bin = null;
        while (p + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            if (type == 0x4E4F534A) json = JsonNode.Parse(Encoding.UTF8.GetString(d, p + 8, len));
            else if (type == 0x004E4942) bin = d.AsSpan(p + 8, len).ToArray();
            p += 8 + len;
        }
        Json = json ?? throw new InvalidDataException("glb has no JSON chunk");
        _bin = bin ?? [];
    }

    public IEnumerable<Primitive> Primitives(bool withUv = false, int? meshIndex = null)
    {
        var materials = Json["materials"]?.AsArray();
        foreach (var mesh in (Json["meshes"]?.AsArray() ?? []).Where((_, i) => meshIndex is null || i == meshIndex))
            foreach (var prim in mesh?["primitives"]?.AsArray() ?? [])
            {
                int mode = prim!["mode"]?.GetValue<int>() ?? 4;
                if (mode != 4) throw new NotSupportedException($"primitive mode {mode}");
                var mat = prim["material"] is { } m && materials is not null ? materials[m.GetValue<int>()]!["name"]!.GetValue<string>() : "";
                var pos = ReadFloats(prim["attributes"]!["POSITION"]!.GetValue<int>(), 3);
                uint[] idx = prim["indices"] is { } ia ? ReadIndices(ia.GetValue<int>())
                                                       : Enumerable.Range(0, pos.Length / 3).Select(i => (uint)i).ToArray();
                var uv = new float[]?[withUv ? 2 : 0];
                for (int k = 0; k < uv.Length; k++)
                    if (prim["attributes"]![$"TEXCOORD_{k}"] is { } ta) uv[k] = ReadFloats(ta.GetValue<int>(), 2);
                float[]? col = withUv && prim["attributes"]!["COLOR_0"] is { } ca && Json["accessors"]![ca.GetValue<int>()]!["type"]!.GetValue<string>() == "VEC4"
                    ? ReadFloats(ca.GetValue<int>(), 4) : null;
                float[]? nrm = withUv && prim["attributes"]!["NORMAL"] is { } na ? ReadFloats(na.GetValue<int>(), 3) : null;
                yield return new Primitive(mesh!["name"]?.GetValue<string>() ?? "", mat, pos, idx) { Uv = uv, Color = col, Normal = nrm };
            }
    }

    /// Scene node instances with hierarchy transforms, for assets whose transforms are not baked.
    public IEnumerable<(int Node, Primitive Primitive)> NodePrimitives()
    {
        var nodes = Json["nodes"]?.AsArray();
        if (nodes is null || nodes.Count == 0)
        {
            int i = 0;
            foreach (var group in Primitives(true).GroupBy(p => p.Mesh))
            { foreach (var p in group) yield return (i, p); i++; }
            yield break;
        }
        var children = nodes.SelectMany(n => n?["children"]?.AsArray() ?? []).Select(n => n!.GetValue<int>()).ToHashSet();
        var scene = Json["scenes"]?[Json["scene"]?.GetValue<int>() ?? 0];
        var roots = scene?["nodes"]?.AsArray().Select(n => n!.GetValue<int>())
            ?? Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i));
        foreach (int root in roots)
            foreach (var p in Visit(root, Matrix4x4.Identity, new HashSet<int>())) yield return p;

        IEnumerable<(int, Primitive)> Visit(int index, Matrix4x4 parent, HashSet<int> path)
        {
            if (!path.Add(index)) throw new InvalidDataException("glb node cycle");
            var node = nodes[index]!;
            float[] Values(string key, float[] fallback) => node[key]?.AsArray().Select(v => v!.GetValue<float>()).ToArray() ?? fallback;
            Matrix4x4 local;
            if (node["matrix"] is not null)
            {
                var a = Values("matrix", []);
                local = new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
            }
            else
            {
                var s = Values("scale", [1, 1, 1]); var r = Values("rotation", [0, 0, 0, 1]); var t = Values("translation", [0, 0, 0]);
                local = Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(new(r[0], r[1], r[2], r[3])) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
            }
            var world = local * parent;
            if (node["mesh"] is { } mesh)
            {
                if (!Matrix4x4.Invert(world, out var inverse)) throw new InvalidDataException("singular glb node transform");
                var normalMatrix = Matrix4x4.Transpose(inverse);
                foreach (var p in Primitives(true, mesh.GetValue<int>()))
                {
                    var pos = (float[])p.Positions.Clone(); var normal = p.Normal is null ? null : (float[])p.Normal.Clone();
                    for (int i = 0; i < pos.Length; i += 3)
                    {
                        var v = Vector3.Transform(new(pos[i], pos[i + 1], pos[i + 2]), world);
                        pos[i] = v.X; pos[i + 1] = v.Y; pos[i + 2] = v.Z;
                        if (normal is null) continue;
                        var n = Vector3.TransformNormal(new(normal[i], normal[i + 1], normal[i + 2]), normalMatrix);
                        normal[i] = n.X; normal[i + 1] = n.Y; normal[i + 2] = n.Z;
                    }
                    var indices = (uint[])p.Indices.Clone();
                    if (world.GetDeterminant() < 0)
                        for (int i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                    yield return (index, p with { Positions = pos, Normal = normal, Indices = indices });
                }
            }
            foreach (var child in node["children"]?.AsArray() ?? [])
                foreach (var p in Visit(child!.GetValue<int>(), world, path)) yield return p;
            path.Remove(index);
        }
    }

    (int offset, int stride, int count) View(int accessor, int elemSize)
    {
        var a = Json["accessors"]![accessor]!;
        var v = Json["bufferViews"]![a["bufferView"]!.GetValue<int>()]!;
        int offset = (v["byteOffset"]?.GetValue<int>() ?? 0) + (a["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = v["byteStride"]?.GetValue<int>() ?? elemSize;
        return (offset, stride, a["count"]!.GetValue<int>());
    }

    float[] ReadFloats(int accessor, int comps)
    {
        var a = Json["accessors"]![accessor]!;
        int type = a["componentType"]!.GetValue<int>();
        bool norm = a["normalized"]?.GetValue<bool>() ?? false;
        int size = type switch { 5126 => 4, 5123 or 5122 => 2, 5121 or 5120 => 1, _ => throw new NotSupportedException($"component type {type}") };
        if (type != 5126 && !norm) throw new NotSupportedException($"non-normalized integer attribute (type {type})");
        var (off, stride, count) = View(accessor, comps * size);
        var r = new float[count * comps];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                var s = _bin.AsSpan(off + i * stride + c * size);
                r[i * comps + c] = type switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(s),
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f,
                    5122 => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1),
                    5121 => s[0] / 255f,
                    _ => Math.Max((sbyte)s[0] / 127f, -1),
                };
            }
        return r;
    }

    uint[] ReadIndices(int accessor)
    {
        int type = Json["accessors"]![accessor]!["componentType"]!.GetValue<int>();
        int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException($"index type {type}") };
        var (off, stride, count) = View(accessor, size);
        var r = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int at = off + i * stride;
            r[i] = size switch { 1 => _bin[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(_bin.AsSpan(at)), _ => BinaryPrimitives.ReadUInt32LittleEndian(_bin.AsSpan(at)) };
        }
        return r;
    }
}
