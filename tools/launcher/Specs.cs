using System.Text.Json.Nodes;

namespace AcrExtraStages;

/// scripts/gen-stage-specs.ps1: work\acr\specs\<id>.json from config/stages.toml and work\ir\<dr2>\stage.json.
internal static class Specs
{
    public sealed record Spec(string Id, string Dr2, string File, JsonObject Json)
    {
        public string Sky => Json["SkySection"]?.ToString() ?? "nz";
        public double GroundOffset => Json["GroundOffset"]?.GetValue<double>() ?? 0;
        /// "acr": the road section keeps ACR's own Hafren gravel road material instead of the DR2 road texture
        public string? Road => Json["Road"]?.ToString();
        /// DR2 id of the reverse route built into this container as its FullReverse variant (config/variants.toml [pairs]), or null.
        public string? Reverse => Json["Variants"]?.AsArray().Skip(1).FirstOrDefault()?["Stage"]?.ToString();
        public bool MenuImages => Json["Menu"]?["Images"]?.GetValue<bool>() == true;
        public string? MenuArt => Json["Menu"]?["Art"]?.ToString();
        public IEnumerable<KeyValuePair<string, string>> ClonePackages => Pairs("Packages");
        public IEnumerable<KeyValuePair<string, string>> CloneObjects => Pairs("Objects");
        public bool LocationTemplate => Json["Location"]?["Template"] is not null;
        public bool LocationArtOrLogo => Json["Location"]?["TileArt"] is not null || Json["Location"]?["GenLogo"] is not null;

        IEnumerable<KeyValuePair<string, string>> Pairs(string k) =>
            (Json["Clone"]?[k] as JsonObject ?? new JsonObject()).Select(p => new KeyValuePair<string, string>(p.Key, p.Value!.ToString()));
    }

    public static Dictionary<string, Spec> Generate(Ctx c, StagesConfig cfg, TextWriter log, Pairs? pairs = null)
    {
        Directory.CreateDirectory(c.L.Specs);
        foreach (var pat in new[] { "acrxs*.json", "acrdirt*.json" }) Files.DeleteMatching(c.L.Specs, pat);
        var art = Directory.Exists(c.L.MenuArtLoading)
            ? Directory.EnumerateFiles(c.L.MenuArtLoading, "*.tpk").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var result = new Dictionary<string, Spec>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in cfg.Entries().Where(x => x.Enabled))
        {
            if (pairs?.ForwardOf(e.Dr2) is { } fwd) { log.WriteLine($"{e.Id}: built as the reverse variant of {fwd}"); continue; }
            var st = e.Raw;
            var loc = cfg.Locations[e.LocationKey];
            var irPath = Path.Combine(c.L.IrRoot, e.Dr2, "stage.json");
            if (!File.Exists(irPath)) { log.WriteLine($"missing IR {irPath} - no spec for {e.Id} (needs prepare)"); continue; }
            var ir = JsonNode.Parse(File.ReadAllText(irPath))!;
            var km = Math.Round((double)ir["length_m"]! / 1000, 3);

            var row = StagesConfig.Str(loc, "row");
            var lb = new JsonObject
            {
                ["Row"] = row ?? string.Concat(e.LocationKey.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..])),
            };
            if (row is null)
            {
                lb["Template"] = StagesConfig.Str(loc, "template");
                lb["Name"] = StagesConfig.Str(loc, "name");
                if (StagesConfig.Str(loc, "description") is { } d) lb["Description"] = d;
                if (StagesConfig.Str(loc, "tile") is { } tile) lb["Tile"] = tile;
                else
                {
                    var tileArt = StagesConfig.Str(loc, "tile_art") ?? art.FirstOrDefault(a => Like(a, $"{e.LocationKey}_rally_", "_route_0"));
                    if (tileArt is not null) lb["TileArt"] = tileArt;
                    else log.WriteLine($"WARN {e.LocationKey}: no DR2 loading art for the location tile; keeping the template's");
                }
                if (StagesConfig.Str(loc, "logo") is { } logo) lb["Logo"] = logo;
                else if (StagesConfig.Str(loc, "logo_colors") is { } lc) { lb["GenLogo"] = true; lb["LogoColors"] = lc; }
                if (StagesConfig.Str(loc, "country") is { } country) lb["Country"] = country;
            }
            if (StagesConfig.Str(loc, "surface") is { } surface) lb["Surface"] = surface;

            var id = e.Id;
            var spec = new JsonObject
            {
                ["Id"] = id,
                ["Stage"] = e.Dr2,
                ["SkySection"] = StagesConfig.Str(st, "sky") ?? StagesConfig.Str(loc, "sky") ?? "nz",
                ["Level"] = $"/Game/AcrXs/Stages/{id}/{id}",
                ["Location"] = lb,
                ["Track"] = new JsonObject { ["Template"] = "WalesS4HafrenSouth" },
                ["Variants"] = new JsonArray(new JsonObject
                {
                    ["Row"] = $"{id}FullForward", ["Template"] = "WelesS4HafrenSouthFullForward", ["Length"] = km,
                }),
                ["Clone"] = new JsonObject
                {
                    ["Packages"] = new JsonObject
                    {
                        ["/Game/Levels/WalesS4HafrenSouth"] = $"/Game/AcrXs/Stages/{id}/{id}",
                        ["/Game/Environments/WalesS4HafrenSouth/DataAssets/DA_WalesS4HafrenSouthBakedData"] = $"/Game/AcrXs/Stages/{id}/DA_{id}BakedData",
                        ["/Game/Data/Pacenote/Tables/DT_PacenoteHafrenSouthForward"] = $"/Game/AcrXs/Stages/{id}/DT_Pacenote{id}Forward",
                    },
                    ["Objects"] = new JsonObject
                    {
                        ["WalesS4HafrenSouth"] = id,
                        ["DA_WalesS4HafrenSouthBakedData"] = $"DA_{id}BakedData",
                        ["DT_PacenoteHafrenSouthForward"] = $"DT_Pacenote{id}Forward",
                    },
                },
            };
            if (pairs?.ReverseOf(e.Dr2) is { } rev)
            {
                var revPath = Path.Combine(c.L.IrRoot, rev, "stage.json");
                if (!File.Exists(revPath)) { log.WriteLine($"missing IR {revPath} - no spec for {e.Id} (its reverse needs prepare)"); continue; }
                var revKm = Math.Round((double)JsonNode.Parse(File.ReadAllText(revPath))!["length_m"]! / 1000, 3);
                // the reverse variant streams only the reverse gameplay layer: Hafren's scenery sits in its rally-props layer (Scenery.cs)
                spec["Variants"]!.AsArray().Add(new JsonObject
                {
                    ["Row"] = $"{id}FullReverse", ["Template"] = "WelesS4HafrenSouthFullReverse", ["Length"] = revKm, ["Stage"] = rev,
                    ["DataLayers"] = new JsonArray("HafrenSouthFullReverseGameplayActors"),
                });
                spec["Clone"]!["Packages"]!["/Game/Data/Pacenote/Tables/DT_PacenoteHafrenSouthReverse"] = $"/Game/AcrXs/Stages/{id}/DT_Pacenote{id}Reverse";
                spec["Clone"]!["Objects"]!["DT_PacenoteHafrenSouthReverse"] = $"DT_Pacenote{id}Reverse";
            }
            if (StagesConfig.Num(st, "ground_offset") is { } go) spec["GroundOffset"] = go;
            if ((StagesConfig.Str(st, "road") ?? StagesConfig.Str(loc, "road")) is { } road)
                spec["Road"] = road is "acr" or "dr2" ? road : throw new LauncherException($"{e.Id}: road must be acr or dr2, not {road}");
            if (StagesConfig.Bool(st, "menu_images"))
            {
                var menu = new JsonObject { ["Images"] = true };
                var a = MenuArtFor(e.Dr2, StagesConfig.Str(st, "menu_art"), art);
                if (a is not null) menu["Art"] = a;
                else log.WriteLine($"WARN {id}: no DR2 loading art; backdrop / banner / lobby images stay the template's");
                if (StagesConfig.Num(st, "menu_elevation_offset") is { } eo) menu["ElevationOffset"] = eo;
                spec["Menu"] = menu;
            }
            if (StagesConfig.Str(st, "track_name") is { } tn) spec["Track"]!["Name"] = tn;
            var file = Path.Combine(c.L.Specs, id.ToLowerInvariant() + ".json");
            File.WriteAllText(file, spec.ToJsonString(Files.JsonOpts));
            log.WriteLine($"wrote {file} ({e.Dr2}, {km} km)");
            result[id] = new Spec(id, e.Dr2, file, spec);
        }
        return result;
    }

    static bool Like(string s, string prefix, string suffix) =>
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && s.Length >= prefix.Length + suffix.Length;

    /// Menu.Art: the stage's own route when DR2 has art for it, else route 0 of the rally, else any art of the location;
    /// menu_art = "<tpk>" overrides, "none" disables.
    static string? MenuArtFor(string dr2, string? over, List<string> art)
    {
        if (over == "none") return null;
        if (over is not null)
        {
            if (!art.Contains(over)) throw new LauncherException($"{dr2}: menu_art {over} not in work\\dr2\\menu-art");
            return over;
        }
        var m = System.Text.RegularExpressions.Regex.Match(dr2, @"^(?<rally>(?<loc>.+)_rally_\d+)_route_\d+$");
        if (!m.Success) return null;
        foreach (var cand in new[] { dr2, m.Groups["rally"].Value + "_route_0" }) if (art.Contains(cand)) return cand;
        return art.FirstOrDefault(a => a.StartsWith(m.Groups["loc"].Value + "_rally_", StringComparison.OrdinalIgnoreCase));
    }
}
