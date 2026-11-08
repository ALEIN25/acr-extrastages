using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

internal static partial class StageIr
{
    /// <summary>The <c>ornaments</c> block of stage.json and the render triangles it adds.</summary>
    private sealed record OrnamentsResult(object Json, int Instances, int PlacedTriangles, int RenderTriangles);

    /// <summary>
    /// <c>route_N/ornaments.bin</c> (212-byte records, see docs/dr2-formats.md): baked scenery props, mainly rocks. Instances
    /// within <c>--ornament-corridor</c> of any route centreline whose template name matches <c>--ornament-filter</c> and that
    /// are not already objects.ens templates are written as render/ornaments.glb prototypes plus stage.json instances.
    /// </summary>
    private static async Task<OrnamentsResult?> BuildOrnaments(Dr2Install install, StageRef st, Options options, string outDir,
        IEnumerable<IReadOnlyList<Vector3>> lines, IEnumerable<StageRef> routes, HashSet<string> objectsEnsTemplates)
    {
        var objectsPath = $"{st.LocDir}/objects.pssg";
        if (!install.Exists(objectsPath))
        {
            return null;
        }

        var corridor = new RouteCorridor(lines, options.OrnamentCorridor);
        var filter = new Regex(options.OrnamentFilter, RegexOptions.CultureInvariant);
        var pssg = PssgCommands.Open(await install.FetchAsync(objectsPath));
        var library = pssg.Elements<PssgLibrary>().FirstOrDefault(x => x.Type == "NODE")
                      ?? throw new InvalidDataException("no NODE library in objects.pssg");
        var byHash = new Dictionary<uint, PssgNode>();
        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            byHash[Fnv1a(TemplateName(root))] = root;
        }

        var instances = new List<(PssgNode Root, Matrix4x4 World, uint Id, string Route)>();
        var seen = new HashSet<(uint, long, long, long)>();
        var records = 0;
        var unresolved = 0;
        var inEns = 0;
        var excluded = new Dictionary<string, int>();
        var sources = new List<string>();
        foreach (var route in routes)
        {
            var path = $"{route.RouteDir}/ornaments.bin";
            if (!install.Exists(path))
            {
                continue;
            }

            sources.Add(path);
            var d = File.ReadAllBytes(await install.FetchAsync(path));
            var offset = BitConverter.ToInt32(d, 0x50);
            var count = BitConverter.ToInt32(d, 0x58);
            if (offset < 0 || count < 0 || offset + 212L * count > d.Length)
            {
                throw new InvalidDataException($"{path}: ornament table (offset {offset}, count {count}) outside the {d.Length} byte file");
            }

            for (var i = 0; i < count; i++)
            {
                var o = offset + 212 * i;
                records++;
                var f = new float[12];
                Buffer.BlockCopy(d, o + 16, f, 0, 48);
                var pos = new Vector3(f[9], f[10], f[11]);
                if (!corridor.Contains(pos))
                {
                    continue;
                }

                var hash = BitConverter.ToUInt32(d, o);
                if (!byHash.TryGetValue(hash, out var node))
                {
                    unresolved++;
                    continue;
                }

                if (objectsEnsTemplates.Contains(TemplateName(node)))
                {
                    inEns++;
                    continue;
                }

                if (!filter.IsMatch(TemplateName(node)))
                {
                    excluded[TemplateName(node)] = excluded.GetValueOrDefault(TemplateName(node)) + 1;
                    continue;
                }

                // the same prop is baked into every route of the location: keep one per hash + position
                if (!seen.Add((hash, (long)MathF.Round(pos.X * 100), (long)MathF.Round(pos.Y * 100), (long)MathF.Round(pos.Z * 100))))
                {
                    continue;
                }

                var world = new Matrix4x4(f[0], f[1], f[2], 0, f[3], f[4], f[5], 0, f[6], f[7], f[8], 0, pos.X, pos.Y, pos.Z, 1);
                instances.Add((node, world, BitConverter.ToUInt32(d, o + 4), route.Id));
            }
        }

        if (instances.Count == 0)
        {
            Console.WriteLine($"ornaments: 0 instances kept ({records} records in {sources.Count} files)");
            Console.WriteLine($"ornaments: corridor {options.OrnamentCorridor} m, filter /{options.OrnamentFilter}/; {unresolved} unresolved hashes, {inEns} already in objects.ens");
            foreach (var (name, n) in excluded.OrderByDescending(kv => kv.Value).Take(40))
                Console.WriteLine($"  ornaments excluded by filter: {name}: {n}");
            return null;
        }

        // Prototypes: the render meshes under the "*_physics" nodes (single-LOD; the other nodes of a root are collision / LODs).
        var prototypes = new List<(string Name, IReadOnlyList<PssgNode> Nodes)>();
        foreach (var node in instances.Select(i => i.Root).Distinct().OrderBy(n => TemplateName(n), StringComparer.Ordinal))
        {
            var meshes = PhysicsNodes(node).ToList();
            prototypes.Add((TemplateName(node), meshes.Count > 0 ? meshes : [node]));
        }

        var glb = Path.Combine(outDir, "render", "ornaments.glb");
        var (result, protoStats) = PssgMeshExport.ExportPrototypes(prototypes, glb);
        File.WriteAllText(Path.ChangeExtension(glb, ".materials.json"),
            JsonSerializer.Serialize(result.Materials.Where(x => x.Triangles > 0), Json.Options));

        var kept = instances.Where(i => protoStats.ContainsKey(TemplateName(i.Root))).ToList();
        var placed = kept.Sum(i => (long)protoStats[TemplateName(i.Root)].Triangles);
        Console.WriteLine($"ornaments: {kept.Count} instances kept of {records} records ({unresolved} unresolved hashes), " +
                          $"corridor {options.OrnamentCorridor} m, filter /{options.OrnamentFilter}/");
        foreach (var g in kept.GroupBy(i => TemplateName(i.Root)).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {g.Key}: {g.Count()} instances x {protoStats[g.Key].Triangles} triangles");
        }

        Console.WriteLine($"render/ornaments.glb: {protoStats.Count} prototypes, {result.Triangles} triangles; {placed} triangles placed in total");
        var json = new
        {
            sources,
            template_source = objectsPath,
            corridor_m = options.OrnamentCorridor,
            filter = options.OrnamentFilter,
            records_total = records,
            unresolved_hashes = unresolved,
            excluded_by_filter = excluded.OrderByDescending(kv => kv.Value).Take(40).ToDictionary(kv => kv.Key, kv => kv.Value),
            file = "render/ornaments.glb",
            materials = "render/ornaments.materials.json",
            kind = "ornament_prototypes",
            notes = "one glTF node per template id (objects.pssg ROOTNODE), in template space; place with instances[].transform " +
                    "(same convention as objects.instances; the rotation*scale 3x3 may carry non-uniform scale)",
            prototypes = protoStats.Count,
            triangles = result.Triangles,
            placed_triangles = placed,
            bounds = protoStats.ToDictionary(kv => kv.Key, kv => Bounds(kv.Value.Min, kv.Value.Max)),
            instances = kept.Select(i => new { id = i.Id.ToString(), template = TemplateName(i.Root), route = i.Route, transform = M(i.World) }).ToList(),
        };
        return new OrnamentsResult(json, kept.Count, (int)Math.Min(placed, int.MaxValue), result.Triangles);
    }

    /// <summary>Outermost descendants of <paramref name="root"/> named *_physics (the render mesh nodes of an object).</summary>
    private static IEnumerable<PssgNode> PhysicsNodes(PssgNode root)
    {
        foreach (var child in root.ChildElements.OfType<PssgNode>())
        {
            if (child.Id.EndsWith("_physics", StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
            }
            else
            {
                foreach (var n in PhysicsNodes(child))
                {
                    yield return n;
                }
            }
        }
    }

    /// <summary>Library roots are "<name> Root"; the ornament hash and the template name are <name>.</summary>
    private static string TemplateName(PssgNode root) => root.Id.EndsWith(" Root", StringComparison.Ordinal) ? root.Id[..^5] : root.Id;

    private static uint Fnv1a(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h = (h ^ b) * 0x01000193u;
        }

        return h;
    }
}
