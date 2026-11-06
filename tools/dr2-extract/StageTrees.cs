using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgoEngineLibrary.Graphics.Pssg;
using EgoEngineLibrary.Graphics.Pssg.Elements;

namespace Dr2Extract;

internal static partial class StageIr
{
    private sealed record TreesResult(object Json, int Instances, int RenderTriangles);

    [GeneratedRegex(@"^(?:axe_me|[a-z0-9_]*_tree_dist_\d+)_(?<x>-?\d+)_(?<z>-?\d+)$")]
    private static partial Regex DistantCell();

    /// <summary>XZ distance from <paramref name="p"/> to the nearest segment of any polyline.</summary>
    private static float DistanceToLines(Vector3 p, IReadOnlyList<IReadOnlyList<Vector3>> lines)
    {
        var q = new Vector2(p.X, p.Z);
        var best = float.MaxValue;
        foreach (var line in lines)
        {
            for (var i = 0; i + 1 < line.Count; i++)
            {
                var a = new Vector2(line[i].X, line[i].Z);
                var ab = new Vector2(line[i + 1].X, line[i + 1].Z) - a;
                var t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                best = MathF.Min(best, Vector2.Distance(q, a + t * ab));
            }
        }

        return best;
    }

    /// <summary>Template id without the "~a" style variant suffix objects.ens adds.</summary>
    private static string Species(string name)
    {
        var i = name.IndexOf('~');
        return i > 0 ? name[..i] : name;
    }

    /// <summary>
    /// <c>route_0/trees.bin</c> (96-byte records, see docs/dr2-formats.md): instanced roadside vegetation. Records within
    /// <c>--tree-corridor</c> of a route centreline that are not already objects.ens instances (same species within 1 m)
    /// become render/trees.glb prototypes (near geometry only) plus stage.json <c>trees.instances</c>. With
    /// <c>--tree-far-lod</c> the far billboard (<c>_x2</c>, lod!N) and shadow (<c>_xs</c>, shadow!N) quads of the used
    /// species are exported to render/trees_far.glb / render/trees_shadow.glb with the billboard textures.
    /// </summary>
    private static async Task<TreesResult?> BuildTrees(Dr2Install install, StageRef st, Options options, string outDir,
        IReadOnlyList<IReadOnlyList<Vector3>> lines, IEnumerable<StageRef> routes)
    {
        var treesPssg = $"{st.LocDir}/trees.pssg";
        var binPath = $"{st.LocDir}/route_0/trees.bin";
        if (!install.Exists(treesPssg) || !install.Exists(binPath))
        {
            Console.WriteLine($"trees: no {treesPssg} / {binPath}");
            return null;
        }

        var pssg = PssgCommands.Open(await install.FetchAsync(treesPssg));
        var library = pssg.Elements<PssgLibrary>().FirstOrDefault(x => x.Type == "NODE")
                      ?? throw new InvalidDataException("no NODE library in trees.pssg");
        var byHash = new Dictionary<uint, PssgNode>();
        foreach (var root in library.ChildElements.OfType<PssgNode>())
        {
            byHash[Fnv1a(TemplateName(root))] = root;
        }

        var corridor = new RouteCorridor(lines, options.TreeCorridor);
        var cellCorridor = new RouteCorridor(lines, 400);

        // objects.ens instances of every built route, for the dedupe
        var ens = new List<(string Species, Vector3 Pos)>();
        foreach (var route in routes)
        {
            var ensPath = $"{route.RouteDir}/objects.ens";
            if (install.Exists(ensPath))
            {
                ens.AddRange(StageObjects.LoadInstances(await install.FetchAsync(ensPath), out _)
                    .Select(i => (Species(i.Template), i.World.Translation)));
            }
        }

        var ensGrid = new Dictionary<(int, int), List<(string Species, Vector3 Pos)>>();
        foreach (var e in ens)
        {
            var key = ((int)MathF.Floor(e.Pos.X), (int)MathF.Floor(e.Pos.Z));
            if (!ensGrid.TryGetValue(key, out var list))
            {
                ensGrid[key] = list = [];
            }

            list.Add(e);
        }

        (bool Same, bool Any) Coincides(string species, Vector3 p)
        {
            bool same = false, any = false;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!ensGrid.TryGetValue(((int)MathF.Floor(p.X) + dx, (int)MathF.Floor(p.Z) + dz), out var list))
                    {
                        continue;
                    }

                    foreach (var e in list.Where(e => Vector3.Distance(e.Pos, p) <= 1f))
                    {
                        any = true;
                        same |= e.Species == species;
                    }
                }
            }

            return (same, any);
        }

        var d = File.ReadAllBytes(await install.FetchAsync(binPath));
        var count = BitConverter.ToInt32(d, 0x30);
        var offset = BitConverter.ToInt32(d, 0x3c);
        if (count < 0 || offset != 0x48 || offset + 96L * count > d.Length)
        {
            throw new InvalidDataException($"{binPath}: tree table (offset {offset}, count {count}) outside the {d.Length} byte file");
        }

        var instances = new List<(PssgNode Root, Matrix4x4 World, int Index, float Distance)>();
        var seen = new HashSet<(uint, long, long, long)>();
        int cells = 0, cellsNear = 0, unresolved = 0, outside = 0, duplicates = 0, coincideSame = 0, coincideAny = 0;
        var cellMin = new Vector2(float.MaxValue);
        var cellMax = new Vector2(float.MinValue);
        var ensSpecies = ens.Select(e => e.Species).ToHashSet(StringComparer.Ordinal);
        var overlapBySpecies = new Dictionary<string, int>();
        for (var i = 0; i < count; i++)
        {
            var o = offset + 96 * i;
            var hash = BitConverter.ToUInt32(d, o);
            var f = new float[12];
            Buffer.BlockCopy(d, o + 8, f, 0, 36);
            Buffer.BlockCopy(d, o + 44, f, 36, 12);
            var pos = new Vector3(f[9], f[10], f[11]);
            if (!byHash.TryGetValue(hash, out var node))
            {
                unresolved++;
                continue;
            }

            var name = TemplateName(node);
            if (DistantCell().IsMatch(name))
            {
                cells++;
                cellMin = Vector2.Min(cellMin, new Vector2(pos.X, pos.Z));
                cellMax = Vector2.Max(cellMax, new Vector2(pos.X, pos.Z));
                if (cellCorridor.Contains(new Vector3(pos.X, 0, pos.Z)))
                {
                    cellsNear++;
                }

                continue;
            }

            if (!corridor.Contains(pos))
            {
                outside++;
                continue;
            }

            if (!seen.Add((hash, (long)MathF.Round(pos.X * 100), (long)MathF.Round(pos.Y * 100), (long)MathF.Round(pos.Z * 100))))
            {
                duplicates++;
                continue;
            }

            var (same, any) = Coincides(Species(name), pos);
            coincideAny += any ? 1 : 0;
            if (same)
            {
                coincideSame++;
                overlapBySpecies[name] = overlapBySpecies.GetValueOrDefault(name) + 1;
                continue;
            }

            var world = new Matrix4x4(f[0], f[1], f[2], 0, f[3], f[4], f[5], 0, f[6], f[7], f[8], 0, pos.X, pos.Y, pos.Z, 1);
            instances.Add((node, world, i, DistanceToLines(pos, lines)));
        }

        Console.WriteLine($"trees: {count} records in {binPath}: {cells} distant cells (axe_me_*, y=0, skipped; {cellsNear} within 400 m of the route), " +
                          $"{unresolved} unresolved hashes, {outside} real instances outside the {options.TreeCorridor} m corridor, " +
                          $"{duplicates} duplicates; {coincideSame} coincide with objects.ens (same species within 1 m, dropped; " +
                          $"{coincideAny} within 1 m of any ens instance); {instances.Count} kept");
        if (instances.Count == 0)
        {
            return null;
        }

        // Near prototypes: the whole species root minus the far billboard (_x2) and shadow (_xs) quads.
        var roots = instances.Select(i => i.Root).Distinct().OrderBy(n => TemplateName(n), StringComparer.Ordinal).ToList();
        var prototypes = roots.Select(n => (TemplateName(n), (IReadOnlyList<PssgNode>)[n])).ToList();
        var glb = Path.Combine(outDir, "render", "trees.glb");
        var (result, protoStats) = PssgMeshExport.ExportPrototypes(prototypes, glb, null, "_x2|_xs");
        File.WriteAllText(Path.ChangeExtension(glb, ".materials.json"),
            JsonSerializer.Serialize(result.Materials.Where(x => x.Triangles > 0), Json.Options));
        var kept = instances.Where(i => protoStats.ContainsKey(TemplateName(i.Root))).ToList();
        var placed = kept.Sum(i => (long)protoStats[TemplateName(i.Root)].Triangles);
        foreach (var g in kept.GroupBy(i => TemplateName(i.Root)).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {g.Key}: {g.Count()} instances x {protoStats[g.Key].Triangles} triangles" +
                              (overlapBySpecies.TryGetValue(g.Key, out var ov) ? $" ({ov} more dropped as objects.ens duplicates)" : ""));
        }

        var dist = kept.Select(i => i.Distance).Order().ToList();
        object? distStats = dist.Count == 0 ? null : new
        {
            min = Round(dist[0], 2),
            p10 = Round(dist[dist.Count / 10], 2),
            median = Round(dist[dist.Count / 2], 2),
            p90 = Round(dist[dist.Count * 9 / 10], 2),
            max = Round(dist[^1], 2),
        };
        Console.WriteLine($"render/trees.glb: {protoStats.Count} prototypes, {result.Triangles} triangles; {placed} triangles placed; " +
                          $"distance to road (XZ, m): {JsonSerializer.Serialize(distStats)}");

        object? far = null;
        if (options.TreeFarLod)
        {
            far = await ExportFarLod(install, st, pssg, roots, outDir);
        }

        var json = new
        {
            sources = new[] { binPath },
            template_source = treesPssg,
            corridor_m = options.TreeCorridor,
            records_total = count,
            distant_cells_skipped = new
            {
                count = cells,
                near_route_400m = cellsNear,
                x_range = cells == 0 ? null : new[] { cellMin.X, cellMax.X },
                z_range = cells == 0 ? null : new[] { cellMin.Y, cellMax.Y },
            },
            unresolved_hashes = unresolved,
            outside_corridor = outside,
            duplicate_records = duplicates,
            objects_ens_overlap = new
            {
                same_species_within_1m_dropped = coincideSame,
                any_template_within_1m = coincideAny,
                ens_instances = ens.Count,
                ens_species_also_in_trees_pssg = ensSpecies.Count(s => byHash.ContainsKey(Fnv1a(s))),
                dropped_by_species = overlapBySpecies,
            },
            file = "render/trees.glb",
            materials = "render/trees.materials.json",
            kind = "tree_prototypes",
            notes = "one glTF node per species (trees.pssg ROOTNODE), near geometry only, in template space; place with instances[].transform " +
                    "(same convention as objects.instances; rotation*scale 3x3 with scale baked in)",
            prototypes = protoStats.Count,
            triangles = result.Triangles,
            placed_triangles = placed,
            bounds = protoStats.ToDictionary(kv => kv.Key, kv => Bounds(kv.Value.Min, kv.Value.Max)),
            distance_to_road_m = distStats,
            species_counts = kept.GroupBy(i => TemplateName(i.Root)).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            far_lod = far,
            instances = kept.Select(i => new { id = $"tree{i.Index}", template = TemplateName(i.Root), route = "route_0", transform = M(i.World) }).ToList(),
        };
        return new TreesResult(json, kept.Count, result.Triangles);
    }

    /// <summary>Far billboard (<c>_x2</c>) and shadow (<c>_xs</c>) quads of each species root, plus the billboard textures.</summary>
    private static async Task<object> ExportFarLod(Dr2Install install, StageRef st, PssgFile trees, List<PssgNode> roots, string outDir)
    {
        var prototypes = roots.Select(n => (TemplateName(n), (IReadOnlyList<PssgNode>)[n])).ToList();
        var entries = new Dictionary<string, object>();
        var textureRefs = new List<string>();
        foreach (var (suffix, file) in new[] { ("_x2", "trees_far"), ("_xs", "trees_shadow") })
        {
            var glb = Path.Combine(outDir, "render", file + ".glb");
            var (result, stats) = PssgMeshExport.ExportPrototypes(prototypes, glb, suffix, null, expandBillboards: true);
            var materials = result.Materials.Where(x => x.Triangles > 0).ToList();
            File.WriteAllText(Path.ChangeExtension(glb, ".materials.json"), JsonSerializer.Serialize(materials, Json.Options));
            textureRefs.AddRange(materials.SelectMany(m => m.Textures).Select(t => t.Texture));

            Console.WriteLine($"render/{file}.glb: {stats.Count}/{roots.Count} templates, {result.Triangles} triangles; " +
                              string.Join(", ", stats.Select(kv => $"{kv.Key}={kv.Value.Triangles}")));
            if (result.Skipped.Count > 0)
            {
                Console.WriteLine($"  skipped: {string.Join("; ", result.Skipped.Take(5))}");
            }

            entries[file] = new
            {
                file = $"render/{file}.glb",
                materials = $"render/{file}.materials.json",
                triangles = result.Triangles,
                triangles_per_template = stats.ToDictionary(kv => kv.Key, kv => kv.Value.Triangles),
                bounds = stats.ToDictionary(kv => kv.Key, kv => Bounds(kv.Value.Min, kv.Value.Max)),
                materials_used = materials.Select(m => new { m.Name, m.ShaderGroup, m.Triangles }).ToList(),
                skipped = result.Skipped.Take(20).ToList(),
            };
        }

        // Billboard textures: the file named by the reference (treestextures.pssg) in the route / location folder.
        var sources = new List<PssgFile> { trees };
        foreach (var file in textureRefs.Where(r => r.IndexOf('#') > 0).Select(r => r[..r.IndexOf('#')]).Distinct())
        {
            var path = install.Exists($"{st.RouteDir}/{file}") ? $"{st.RouteDir}/{file}"
                : install.Exists($"{st.LocDir}/{file}") ? $"{st.LocDir}/{file}" : null;
            if (path is not null)
            {
                sources.Insert(0, PssgCommands.Open(await install.FetchAsync(path)));
            }
        }

        var texDir = Path.Combine(outDir, "render", "trees_far_textures");
        var (written, missing) = PssgMeshExport.ExportTextures(textureRefs, sources, texDir);
        Console.WriteLine($"trees_far textures: {written} written to {texDir}, {missing.Count} not found");
        entries["textures"] = new { dir = "render/trees_far_textures", written, missing };
        entries["notes"] = "billboard quads are expanded to real geometry: the PSSG vertices all sit at (0, halfHeight, 0) and ST1 holds the corner offset (+-halfWidth, +-halfHeight); " +
                           "the quad lies in the XY plane (z=0, facing +-Z; DR2 turns it to the camera) with its y range as authored (bottom slightly below the origin). ST0.u covers 1/8 of the width of the lod texture (an 8-view angle atlas, 2048x256)";
        return entries;
    }
}
