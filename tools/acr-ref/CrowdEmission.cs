using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Objects;
using static AcrRef.Splines;

namespace AcrRef;

// Clone the inspected native graph, never scan arbitrary bytes for reference-looking integers.
internal static class CrowdEmission
{
    public const string Template = @"acr\Content\Levels\WalesS4HafrenSouth\_Generated_\0SEKWS6594N5CV2V6D2XLJYYI";
    public const string Blueprint = @"acr\Content\Data\Crowd\Blueprints\BC_CrowdSystemSpawnerBakedActor";
    const string ActorClass = "BC_CrowdSystemSpawnerBakedActor_C";
    const string Prefix = ActorClass + "_UAID_ACRX_";

    public static void CopyBlueprint(string legacy, string output)
    {
        string src = HostPaths.Resolve(legacy, Blueprint), dst = Path.Combine(output, Blueprint + ".uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        foreach (string ext in new[] { ".uasset", ".uexp" })
            File.Copy(Path.ChangeExtension(src, ext), Path.ChangeExtension(dst, ext), true);
    }

    // Restricted schema walker for this graph and Level's property prefix. Unknown serializers fail closed.
    sealed class Layout
    {
        public Action<string, Dictionary<string, int>>? Visit;
        readonly Dictionary<string, Dictionary<int, (string name, PropertyType type)>> types = new();
        public Layout(string usmap)
        {
            var mappings = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase).MappingsForGame!;
            foreach (var (name, schema) in mappings.Types)
            {
                var props = new Dictionary<int, (string, PropertyType)>(); int offset = 0;
                for (var s = schema; s != null; s = s.Super?.Value)
                {
                    foreach (var (i, prop) in s.Properties) props.Add(offset + i, (prop.Name, prop.MappingType));
                    offset += s.PropertyCount;
                }
                types[name] = props;
            }
        }
        public Dictionary<string, int> Struct(byte[] d, ref int p, string name, Func<int, int>? refs = null, Func<int, int>? names = null)
        {
            var result = new Dictionary<string, int>();
            foreach (var slot in Unv.ReadHeader(d, ref p))
            {
                if (slot.Zero) continue;
                var prop = types[name][slot.Index]; result[prop.name] = p;
                Value(d, ref p, prop.type, refs, names);
            }
            Visit?.Invoke(name, result);
            return result;
        }
        void Value(byte[] d, ref int p, PropertyType t, Func<int, int>? refs, Func<int, int>? names)
        {
            switch (t.Type)
            {
                case "ObjectProperty": if (refs != null) Put(d, p, refs(Int(d, p))); p += 4; break;
                case "NameProperty": if (names != null) Put(d, p, names(Int(d, p))); p += 8; break;
                case "IntProperty": case "UInt32Property": case "FloatProperty": p += 4; break;
                case "BoolProperty": case "ByteProperty": p++; break;
                case "EnumProperty": Value(d, ref p, t.InnerType!, refs, names); break;
                case "ArrayProperty":
                    int count = Int(d, p); p += 4;
                    for (int i = 0; i < count; i++) Value(d, ref p, t.InnerType!, refs, names);
                    break;
                case "MapProperty":
                    int removed = Int(d, p); p += 4;
                    if (removed != 0) throw new InvalidDataException("crowds: map tombstones unsupported");
                    int n = Int(d, p); p += 4;
                    for (int i = 0; i < n; i++) { Value(d, ref p, t.InnerType!, refs, names); Value(d, ref p, t.ValueType!, refs, names); }
                    break;
                case "StructProperty":
                    int size = t.StructType switch { "Vector" or "Rotator" => 24, "Quat" => 32, "Guid" => 16, "IntVector" => 12, _ => 0 };
                    if (size > 0) p += size; else Struct(d, ref p, t.StructType!, refs, names);
                    break;
                case "SoftObjectProperty":
                    // FSoftObjectPath: package/asset FNames, subpath FString.
                    if (names != null) { Put(d, p, names(Int(d, p))); Put(d, p + 8, names(Int(d, p + 8))); }
                    p += 16; int len = Int(d, p); p += 4 + (len < 0 ? -len * 2 : len); break;
                default: throw new NotSupportedException($"crowds: serializer {t.Type}/{t.StructType}");
            }
            if (p > d.Length) throw new InvalidDataException("crowds: property exceeds export");
        }
    }
    static int Int(byte[] d, int p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
    static void Put(byte[] d, int p, int value) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(p), value);
    static void Double(byte[] d, int p, double value) => BinaryPrimitives.WriteDoubleLittleEndian(d.AsSpan(p), value);

    public static void Emit(LegacyPackage cell, string legacy, string usmap, Toml cfg, JsonObject report, IReadOnlyList<V3> centre, Visuals.TopSurface ground)
    {
        if (report["mode"]?.GetValue<string>() != "dr2_spots") return;
        int cap = cfg["crowds"].TryGetValue("max_spawners", out var v) ? Convert.ToInt32(v) : 40;
        if (cap < 0) throw new ArgumentException("[crowds] max_spawners must be nonnegative");
        var spots = report["spots"]!.AsArray().ToArray();
        var emitted = new JsonArray(); report["emitted"] = emitted;
        report["actors_emitted"] = 0; report["people_emitted"] = 0; report["status"] = "emitted";
        if (spots.Length == 0) return;
        var src = new LegacyPackage(HostPaths.Resolve(legacy, Template));
        var layout = new Layout(usmap);
        int actor = Enumerable.Range(0, src.ExportCount).Single(e => src.ExportClass(e) == ActorClass);
        var templateData = src.GetExportData(actor);
        var translations = new List<int>();
        layout.Visit = (name, fields) => { if (name == "Transform") translations.Add(fields["Translation"]); };
        int scan = 0; layout.Struct(templateData, ref scan, "CrowdSystemSpawnerBakedActor");
        layout.Visit = null;
        if (translations.Count == 0) throw new InvalidDataException("crowds: no baked Transform translations found");
        double Read(int at) => BinaryPrimitives.ReadDoubleLittleEndian(templateData.AsSpan(at));
        double slopeLimit = cfg["crowds"].TryGetValue("max_slope_degrees", out var slope) ? Convert.ToDouble(slope) : 25;
        if (!double.IsFinite(slopeLimit) || slopeLimit < 0 || slopeLimit > 45) throw new ArgumentException("crowds.max_slope_degrees must be 0..45");
        int noGround = 0, steep = 0;
        var route = new StageBuild.Polyline(centre.ToList());
        bool Accept(JsonNode? spot)
        {
            var xyz = spot!["ue_cm"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            var target = route.At(route.Project(new(xyz[0], xyz[1], xyz[2])).s, 0, 0, out _);
            double yaw = Math.Atan2(target.Y - xyz[1], target.X - xyz[0]);
            var offsets = translations.Select(at => new Vector2((float)(Read(at) * Math.Cos(yaw) - Read(at + 8) * Math.Sin(yaw)),
                (float)(Read(at) * Math.Sin(yaw) + Read(at + 8) * Math.Cos(yaw)))).ToArray();
            // Sample a 50 cm grid across the group's full bounding footprint, as well as each foot.
            var points = new List<Vector2> { new((float)xyz[0], (float)xyz[1]) };
            points.AddRange(offsets.Select(o => points[0] + o));
            for (float x = offsets.Min(o => o.X) - 30; x <= offsets.Max(o => o.X) + 80; x += 50)
                for (float y = offsets.Min(o => o.Y) - 30; y <= offsets.Max(o => o.Y) + 80; y += 50)
                    points.Add(new((float)xyz[0] + x, (float)xyz[1] + y));
            var heights = points.Select(ground.At).ToArray();
            if (heights.Any(h => h is null)) { noGround++; return false; }
            double limit = Math.Tan(slopeLimit * Math.PI / 180);
            for (int i = 0; i < points.Count; i++)
                for (int k = i + 1; k < points.Count; k++)
                    if (Vector2.Distance(points[i], points[k]) > 1 && Math.Abs(heights[i]!.Value - heights[k]!.Value) > Vector2.Distance(points[i], points[k]) * limit)
                    { steep++; return false; }
            spot["ue_cm"] = new JsonArray(xyz[0], xyz[1], heights[0]!.Value);
            return true;
        }
        spots = spots.Where(Accept).Take(cap).ToArray();
        report["rejected_no_drawn_ground"] = noGround; report["rejected_slope"] = steep;
        Console.WriteLine($"crowds: drawn ground: {spots.Length} groups accepted, {noGround} missing ground, {steep} over {slopeLimit:0.#} degrees rejected");
        int[] graph = [actor, .. Enumerable.Range(0, src.ExportCount).Where(e => src.ExportOuter(e) == actor + 1)];
        if (graph.Length != 5) throw new InvalidDataException("crowds: expected four template subobjects");
        int level = Enumerable.Range(0, cell.ExportCount).Single(e => cell.ExportClass(e) == "Level");
        var importMap = new Dictionary<int, int>();
        int Import(int pi)
        {
            if (pi == 0) return 0;
            if (pi > 0) throw new InvalidDataException("crowds: unexpected export in import outer");
            if (importMap.TryGetValue(pi, out int hit)) return hit;
            int i = -pi - 1, outer = Import(src.ImportOuter(i));
            int dst = cell.FindImport(src.ImportClassPackage(i), src.ImportClassName(i), src.ImportName(i), outer);
            if (dst == 0) dst = cell.AddImport(src.ImportClassPackage(i), src.ImportClassName(i), outer, src.ImportName(i));
            return importMap[pi] = dst;
        }
        // AddExportName inserts before header-only names; do all additions before caching name indices.
        foreach (var name in src.Names.Take(src.HeaderField("NamesReferencedFromExportDataCount"))) cell.AddExportName(name);
        string[] actorNames = spots.Select(_ => Prefix + Guid.NewGuid().ToString("N").ToUpperInvariant()).ToArray();
        foreach (string name in actorNames) cell.AddExportName(name);
        int Name(int i) => cell.FindName(src.Names[i]);
        int people = 0;
        for (int j = 0; j < spots.Length; j++)
        {
            var map = graph.Select((e, i) => (e, pi: cell.ExportCount + i + 1)).ToDictionary(x => x.e + 1, x => x.pi);
            map[src.ExportOuter(actor)] = level + 1;
            int Ref(int pi) => pi <= 0 ? Import(pi) : map.TryGetValue(pi, out int dst) ? dst : throw new InvalidDataException($"crowds: external template export reference {pi}");
            var xyz = spots[j]!["ue_cm"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            var pos = new V3(xyz[0], xyz[1], xyz[2]);
            var line = new StageBuild.Polyline(centre.ToList()); var projection = line.Project(pos);
            var target = line.At(projection.s, 0, 0, out _);
            double yaw = Math.Atan2(target.Y - pos.Y, target.X - pos.X) * 180 / Math.PI;
            foreach (int e in graph)
            {
                var entry = src.ExportEntry(e); var data = (byte[])src.GetExportData(e).Clone(); int p = 0;
                string type = e == actor ? "CrowdSystemSpawnerBakedActor" : src.ExportClass(e);
                var props = layout.Struct(data, ref p, type, Ref, Name);
                if (e == actor)
                {
                    int q = props["BakedSpawnData"]; var baked = layout.Struct(data, ref q, "CrowdSystemSpawnData");
                    people += Int(data, baked["Elements"]);
                    foreach (int at in translations)
                    {
                        double angle = yaw * Math.PI / 180, x = Read(at), y = Read(at + 8);
                        var xy = new Vector2((float)(xyz[0] + x * Math.Cos(angle) - y * Math.Sin(angle)),
                            (float)(xyz[1] + x * Math.Sin(angle) + y * Math.Cos(angle)));
                        Double(data, at + 16, ground.At(xy)!.Value - xyz[2]);
                    }
                    // Native actor trailer contains a label and actor instance GUID. Give every clone a fresh GUID.
                    if (data.Length - p < 32) throw new InvalidDataException("crowds: missing actor GUID trailer");
                    Guid.NewGuid().ToByteArray().CopyTo(data, data.Length - 32);
                }
                if (type == "SceneComponent")
                {
                    int at = props["RelativeLocation"]; for (int k = 0; k < 3; k++) Double(data, at + k * 8, xyz[k]);
                    at = props["RelativeRotation"]; Double(data, at, 0); Double(data, at + 8, yaw); Double(data, at + 16, 0);
                }
                var deps = src.ExportDeps(e);
                IEnumerable<int> D(string k) => deps.Where(d => d.kind == k).Select(d => Ref(d.index));
                cell.AddExport(entry, e == actor ? actorNames[j] : src.ExportName(e), Ref(Int(entry, 0)), Ref(Int(entry, 4)), Ref(Int(entry, 8)), Ref(src.ExportOuter(e)), src.ExportFlags(e), data, D("SBS"), D("CBS"), D("SBC"), D("CBC"));
            }
            emitted.Add(new JsonObject { ["actor"] = actorNames[j], ["ue_cm"] = new JsonArray(xyz.Select(x => JsonValue.Create(x)).ToArray()), ["yaw"] = yaw });
        }
        // ULevel's native actor array follows its unversioned properties and UObject's GUID flag.
        var ld = cell.GetExportData(level); int end = 0; layout.Struct(ld, ref end, "Level");
        if (Int(ld, end) != 0) throw new InvalidDataException("crowds: Level has an object GUID");
        int atActors = end + 4, countActors = Int(ld, atActors), tail = atActors + 4 + countActors * 4;
        var grown = new byte[ld.Length + spots.Length * 4]; ld.AsSpan(0, tail).CopyTo(grown);
        Put(grown, atActors, countActors + spots.Length);
        for (int j = 0; j < actorNames.Length; j++) Put(grown, tail + j * 4, cell.FindExport(actorNames[j]) + 1);
        ld.AsSpan(tail).CopyTo(grown.AsSpan(tail + spots.Length * 4)); cell.SetExportData(level, grown);
        var levelDeps = cell.ExportDeps(level);
        IEnumerable<int> LD(string k) => levelDeps.Where(d => d.kind == k).Select(d => d.index);
        cell.SetExportDeps(level, LD("SBS"), LD("CBS").Concat(actorNames.Select(n => cell.FindExport(n) + 1)), LD("SBC"), LD("CBC"));
        report["actors_emitted"] = spots.Length; report["people_emitted"] = people;
    }

    public static int Check(string root, string cellPath, string usmap, JsonNode report, TextWriter w, Visuals.TopSurface ground)
    {
        if (report["mode"]?.GetValue<string>() != "dr2_spots") return 0;
        using var provider = new DefaultFileProvider(Path.Combine(root, "acr"), SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        string diskPath = Path.GetFullPath(Path.Combine(root, cellPath + ".umap"));
        string packageKey = provider.Files.FirstOrDefault(kv => kv.Value.Path.Replace('\\', '/').EndsWith(cellPath.Replace('\\', '/') + ".umap", StringComparison.OrdinalIgnoreCase)).Key
            ?? throw new InvalidDataException($"crowds: cell {diskPath} not indexed; files: {string.Join(", ", provider.Files.Keys.Take(5))}");
        var exports = provider.LoadPackage(packageKey).GetExports().ToList();
        var actors = exports.Where(e => e.ExportType == ActorClass && e.Name.StartsWith(Prefix)).ToList();
        var expected = report["emitted"]!.AsArray();
        if (actors.Count != expected.Count) throw new InvalidDataException("crowds: spawner count mismatch");
        var level = exports.Single(e => e.ExportType == "Level");
        // ULevel is in CUE4Parse's game-specific namespace; use its public native Actors field.
        var levelActors = (FPackageIndex[])(level.GetType().GetField("Actors")?.GetValue(level)
            ?? throw new InvalidDataException("crowds: reader has no Level.Actors field"));
        int people = 0;
        foreach (var spot in expected)
        {
            var actor = actors.Single(e => e.Name == spot!["actor"]!.GetValue<string>());
            if (!levelActors.Any(a => a.ResolvedObject?.Load() == actor)) throw new InvalidDataException("crowds: actor missing from level actor list");
            people += actor.GetOrDefault<FStructFallback>("BakedSpawnData")?.GetOrDefault<UScriptArray>("Elements")?.Properties.Count
                ?? throw new InvalidDataException("crowds: missing baked spawn elements");
            var components = exports.Where(e => e.Outer?.Name.ToString() == actor.Name).ToList();
            if (components.Count != 4) throw new InvalidDataException("crowds: component count mismatch");
            foreach (var (prop, cls) in new[] { ("RootComponent", "SceneComponent"), ("SpawnerComponent", "CrowdSystemMassSpawnerComponent"), ("CrowdAnimation", "CrowdSystemAnimation"), ("CrowdRuntime", "CrowdSystemRuntime") })
            {
                var component = actor.GetOrDefault<FPackageIndex>(prop)?.ResolvedObject?.Load();
                if (component == null || component.ExportType != cls || !components.Contains(component)) throw new InvalidDataException($"crowds: broken {prop}");
            }
            var loc = components.Single(e => e.ExportType == "SceneComponent").GetOrDefault<FVector>("RelativeLocation");
            var xyz = spot!["ue_cm"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
            if (Math.Abs(loc.X - xyz[0]) > 1 || Math.Abs(loc.Y - xyz[1]) > 1 || Math.Abs(loc.Z - xyz[2]) > 1) throw new InvalidDataException("crowds: root placement mismatch");
            if (ground.At(new((float)loc.X, (float)loc.Y)) is not { } rootHeight || Math.Abs(loc.Z - rootHeight) > 5)
                throw new InvalidDataException("crowds: root is not on drawn ground");
            var rot = components.Single(e => e.ExportType == "SceneComponent").GetOrDefault<FRotator>("RelativeRotation");
            if (Math.Abs(rot.Yaw - spot["yaw"]!.GetValue<double>()) > 0.01) throw new InvalidDataException("crowds: root yaw mismatch");
            var elements = actor.GetOrDefault<FStructFallback>("BakedSpawnData")!.GetOrDefault<UScriptArray>("Elements")!;
            foreach (var element in elements.Properties)
            {
                var transform = element.GetValue<FStructFallback>()!.GetOrDefault<FTransform>("Transform");
                double angle = rot.Yaw * Math.PI / 180;
                var foot = transform.Translation;
                var xy = new Vector2((float)(loc.X + foot.X * Math.Cos(angle) - foot.Y * Math.Sin(angle)),
                    (float)(loc.Y + foot.X * Math.Sin(angle) + foot.Y * Math.Cos(angle)));
                if (ground.At(xy) is not { } h || Math.Abs(loc.Z + foot.Z - h) > 5)
                    throw new InvalidDataException("crowds: baked person's world Z differs from drawn ground by > 5 cm");
            }
        }
        if (people != report["people_emitted"]!.GetValue<int>()) throw new InvalidDataException("crowds: baked people count mismatch");
        w.WriteLine($"crowds: emitted {actors.Count} spawners ({report["people_emitted"]} people) in {cellPath}");
        w.WriteLine($"crowds: drawn-ground contact: {people} people within 5 cm (rotated world XY)");
        return 0;
    }

    public static int SelfTest(Options o)
    {
        string legacy = o.Named["legacy"], output = o.Out ?? throw new ArgumentException("--out <scratch directory> required");
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("self-test output must differ from template root");
        string targetCell = o.Named.GetValueOrDefault("cell", Template);
        var cell = new LegacyPackage(HostPaths.Resolve(legacy, targetCell));
        var report = JsonNode.Parse("""{"mode":"dr2_spots","spots":[{"ue_cm":[1000,2000,3000]},{"ue_cm":[-4000,5000,6000]}]}""")!.AsObject();
        report["spots"]!.AsArray().Add(new JsonObject { ["ue_cm"] = new JsonArray(50000.0, 50000.0, 0.0) });
        report["spots"]!.AsArray().Add(new JsonObject { ["ue_cm"] = new JsonArray(35000.0, 0.0, 0.0) });
        var ground = new Visuals.TopSurface();
        ground.Add(new(-10000, -10000, 1000), new(20000, -10000, 4000), new(-10000, 20000, 2500));
        ground.Add(new(20000, -10000, 4000), new(20000, 20000, 5500), new(-10000, 20000, 2500));
        ground.Add(new(30000, -5000, 0), new(40000, -5000, 10000), new(30000, 5000, 0));
        ground.Add(new(40000, -5000, 10000), new(40000, 5000, 10000), new(30000, 5000, 0));
        Emit(cell, legacy, o.Usmap, Toml.Load(StageBuild.VisualsToml), report, [new(0, 0, 0), new(10000, 0, 0)], ground);
        if (report["actors_emitted"]!.GetValue<int>() != 2 || report["rejected_no_drawn_ground"]!.GetValue<int>() != 1
            || report["rejected_slope"]!.GetValue<int>() != 1) throw new InvalidDataException("crowds: slope/missing-ground rejection fixture failed");
        cell.SaveTo(Path.Combine(output, targetCell + ".umap"));
        CopyBlueprint(legacy, output);
        Check(output, targetCell, o.Usmap, report, Console.Out, ground);
        Console.WriteLine("PASS crowds native clone: two actors, eight components, remapped references and root placement");
        return 0;
    }
}
