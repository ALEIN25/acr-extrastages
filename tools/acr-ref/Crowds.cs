using System.Numerics;
using System.Text.Json.Nodes;
using static AcrRef.Splines;

namespace AcrRef;

/// Candidate spectator spots. CrowdEmission clones the native spawner graph at accepted positions.
internal static class Crowds
{
    const double ClearanceCm = 400;
    const string MarkerPrefix = "crowd:spot:";

    internal static string Mode(Toml cfg)
    {
        var mode = cfg["crowds"].TryGetValue("mode", out var value) ? value as string : "off";
        return mode is "off" or "dr2_spots" ? mode : throw new ArgumentException("[crowds] mode must be off or dr2_spots");
    }

    public static JsonObject Plan(StageBuild.Ir ir, StageBuild.Placement pl, Toml cfg,
                                  RoadCorridor road, Func<V3, bool> inBounds)
    {
        var mode = Mode(cfg);
        foreach (var key in ir.PreviewMarkers.Keys.Where(k => k.StartsWith(MarkerPrefix, StringComparison.Ordinal)).ToArray())
            ir.PreviewMarkers.Remove(key);
        if (mode == "off") return new JsonObject { ["mode"] = mode, ["actors_emitted"] = 0 };
        return Plan(JsonNode.Parse(File.ReadAllText(Path.Combine(ir.Dir, "stage.json")))!, ir, pl, road, inBounds);
    }

    internal static JsonObject Plan(JsonNode stage, StageBuild.Ir ir, StageBuild.Placement pl,
                                    RoadCorridor road, Func<V3, bool> inBounds)
    {
        var spots = new JsonArray();
        var seen = new HashSet<(double, double, double)>();
        int candidates = 0, near = 0, outside = 0, duplicates = 0;
        // These are template origins, not a claim that one record represents one spectator.
        // Triggers describe volumes, so their centres are not treated as spectator positions.
        foreach (var source in new[] { "objects", "ornaments" })
        foreach (var instance in stage[source]?["instances"]?.AsArray() ?? [])
        {
            var template = instance?["template"]?.GetValue<string>() ?? "";
            if (!template.StartsWith("core_crowd_", StringComparison.OrdinalIgnoreCase)
                && !template.Contains("spectator", StringComparison.OrdinalIgnoreCase)) continue;
            candidates++;
            var transform = instance!["transform"];
            if (transform?["position"] is not JsonArray { Count: 3 } && transform?["matrix"] is not JsonArray { Count: 16 })
                throw new InvalidDataException($"crowds: {source}/{template} has no position or 4x4 matrix");
            var p = Vector3.Transform(Vector3.Zero, ObjectColliders.Trs(transform));
            var ue = pl.ToUe([p.X, p.Y, p.Z]);
            if (!double.IsFinite(ue.X) || !double.IsFinite(ue.Y) || !double.IsFinite(ue.Z))
                throw new InvalidDataException($"crowds: {source}/{template} has a non-finite position");
            if (!inBounds(ue)) { outside++; continue; }
            var clearance = road.Query(ue.X, ue.Y);
            if (clearance.seg < 0 || clearance.clearance < ClearanceCm) { near++; continue; }
            if (!seen.Add((Math.Round(ue.X), Math.Round(ue.Y), Math.Round(ue.Z)))) { duplicates++; continue; }
            var marker = $"{MarkerPrefix}{spots.Count:D5}";
            ir.PreviewMarkers[marker] = ue;
            spots.Add(new JsonObject
            {
                ["marker"] = marker, ["source"] = source, ["template"] = template,
                ["id"] = instance["id"]?.ToString(), ["ue_cm"] = new JsonArray(ue.X, ue.Y, ue.Z),
                ["road_clearance_m"] = clearance.clearance / 100,
            });
        }
        Console.WriteLine($"crowds: dr2_spots: {candidates} candidates, {spots.Count} spots, {near} rejected within 4 m of road edge, {outside} out of bounds, {duplicates} duplicates");
        if (candidates == 0) Console.WriteLine("crowds: no spectator templates in IR objects/ornaments; crowd_*.bin is not exported by stage-ir");
        return new JsonObject
        {
            ["mode"] = "dr2_spots", ["status"] = "planned", ["actors_emitted"] = 0,
            ["min_road_clearance_m"] = ClearanceCm / 100, ["candidates"] = candidates,
            ["rejected_road_clearance"] = near, ["rejected_bounds"] = outside, ["duplicates"] = duplicates,
            ["spots"] = spots,
        };
    }

    public static int SelfTest()
    {
        var ir = new StageBuild.Ir();
        var pl = new StageBuild.Placement(0, 0, 0, 0, 0, 0, 0);
        var road = new RoadCorridor([new(0, 0, 0), new(10000, 0, 0)],
            [new(0, 200, 0), new(10000, 200, 0)], [new(0, -300, 0), new(10000, -300, 0)]);
        var stage = JsonNode.Parse("""
            {"objects":{"instances":[
              {"template":"core_crowd_a","transform":{"position":[50,0,5.99]}},
              {"template":"core_crowd_a","transform":{"position":[50,0,6]}},
              {"template":"spectator_a","transform":{"matrix":[1,0,0,0,0,1,0,0,0,0,1,0,50,0,-7,1]}},
              {"template":"core_crowd_a","transform":{"position":[50,0,20]}},
              {"template":"rock","transform":{"position":[50,0,8]}}
            ]},"ornaments":{"instances":[
              {"template":"core_crowd_a","transform":{"position":[50,0,6]}}
            ]}}
            """)!;
        var report = Plan(stage, ir, pl, road, p => Math.Abs(p.Y) < 1000);
        bool ok = report["candidates"]!.GetValue<int>() == 5 && report["spots"]!.AsArray().Count == 2
            && report["rejected_road_clearance"]!.GetValue<int>() == 1 && report["rejected_bounds"]!.GetValue<int>() == 1
            && report["duplicates"]!.GetValue<int>() == 1 && ir.PreviewMarkers.Count == 2;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} crowds: asymmetric road edges, 4 m boundary, matrix/TRS positions, bounds, deduplication and preview markers");
        return ok ? 0 : 1;
    }
}
