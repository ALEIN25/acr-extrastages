using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AcrExtraStages;

/// scripts/prepare-stage-data.ps1: per-stage DR2 data (render meshes, ground bake, textures) cached in work\ir\<dr2 id>,
/// skipping complete stages, bounded retries (prepare-attempts.json) and a prepare-failed.json marker after the last one.
/// scripts/prepare-menu-art.ps1: DR2 loading art for the menu images.
internal static class Prepare
{
    // sponsor packs are not in location archives; lazerstart_s uses an unsupported format. Non-fatal (stage-build DropIn skips them).
    static readonly Regex[] Optional =
        new[] { "^cloth(_[a-z])?$", "^teardrop$", "^drape$", "^board$", "^core_game_lazerstart" }.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();

    public sealed record Missing(List<string> Required, List<string> OptionalFiles);

    public static Missing Check(string dir)
    {
        var req = new List<string>();
        var opt = new List<string>();
        var render = Path.Combine(dir, "render");
        var manifest = Path.Combine(render, "ground_bake", "manifest.json");
        if (!File.Exists(Path.Combine(dir, "stage.json"))) req.Add("stage.json");
        if (!File.Exists(Path.Combine(render, "terrain.glb"))) req.Add("render/terrain.glb");
        if (!File.Exists(manifest) && !File.Exists(Path.Combine(render, "ground_bake", "skipped.json"))) req.Add("render/ground_bake/manifest.json|skipped.json");
        if (File.Exists(manifest))
        {
            try
            {
                var man = JsonNode.Parse(File.ReadAllText(manifest));
                foreach (var t in man?["tiles"]?.AsArray() ?? new JsonArray())
                    if (t?["dds"]?.GetValue<string>() is { Length: > 0 } dds && !File.Exists(Path.Combine(render, "ground_bake", dds)))
                        req.Add($"render/ground_bake/{dds}");
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { req.Add("render/ground_bake/manifest.json(unreadable)"); }
        }
        var tex = Path.Combine(dir, "textures");
        foreach (var name in MaterialTextures(render))
        {
            var b = Path.GetFileNameWithoutExtension(name);
            if (File.Exists(Path.Combine(tex, b + ".dds")) || File.Exists(Path.Combine(tex, b + "_slice0.dds"))) continue;
            (Optional.Any(r => r.IsMatch(b)) ? opt : req).Add($"textures/{b}.dds");
        }
        return new(req, opt);
    }

    static IEnumerable<string> MaterialTextures(string render)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(render)) return names;
        foreach (var f in Directory.EnumerateFiles(render, "*.materials.json"))
        {
            JsonNode? mats;
            try { mats = JsonNode.Parse(File.ReadAllText(f)); } catch (JsonException) { continue; }
            if (mats is not JsonArray arr) continue;
            foreach (var m in arr)
            {
                if (m?["textures"] is not JsonArray texs) continue;
                foreach (var t in texs)
                {
                    var r = t?["texture"]?.ToString();
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    var h = r.LastIndexOf('#');
                    var n = h >= 0 ? r[(h + 1)..] : r;
                    if (n.Length > 0) names.Add(n);
                }
            }
        }
        return names;
    }

    public sealed record Row(string Stage, bool Ok, string? Bake, string? Error, int MissingOptional, int Attempts, List<string> Steps, int Seconds);

    /// The routes a prepared stage.json's ground / render / bake also cover (stage-ir --also), from the top of the file.
    public static string[] CorridorAlso(string dir)
    {
        var p = Path.Combine(dir, "stage.json");
        if (!File.Exists(p)) return [];
        using var r = new StreamReader(p);
        var head = new char[8192];
        var text = new string(head, 0, r.ReadBlock(head, 0, head.Length));
        var m = Regex.Match(text, @"""corridor_also""\s*:\s*\[([^\]]*)\]");
        return m.Success ? Regex.Matches(m.Groups[1].Value, @"""([^""]+)""").Select(x => x.Groups[1].Value).ToArray() : [];
    }

    /// Round 2b: stage.json written by a dr2-extract that knows ornaments (stage-ir --render) has a top-level "ornaments" key, a
    /// null one for a stage without any; a file without the key predates them, so the stage is extracted again (stage-ir, then
    /// stage-textures for the rock textures the new materials file needs) to pick the rocks up.
    public static bool HasOrnamentsKey(string dir)
    {
        var p = Path.Combine(dir, "stage.json");
        if (!File.Exists(p)) return false;
        foreach (var line in File.ReadLines(p))
            if (line.StartsWith("  \"ornaments\":", StringComparison.Ordinal)) return true;
        return false;
    }

    /// Prepares one stage; never throws for stage problems (the batch continues past a failing stage). <paramref name="reverse"/>:
    /// the DR2 reverse built into the same container (config/variants.toml [pairs]): the ground, render and bake cover its road too
    /// (stage-ir --also), and the reverse itself gets a gameplay-only IR (route, timing, triggers, pacenotes; no render).
    public static Row Stage(Ctx c, StageEntry st, TextWriter log, string? reverse = null)
    {
        var id = st.Dr2;
        var dir = Path.Combine(c.L.IrRoot, id);
        var failed = Path.Combine(dir, "prepare-failed.json");
        var attemptsPath = Path.Combine(c.L.Batch, "prepare-attempts.json");
        var steps = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n;
        lock (AttemptsGate) n = ReadAttempts(attemptsPath).GetValueOrDefault(id);
        var terrain = Path.Combine(dir, "render", "terrain.glb");
        var manifest = Path.Combine(dir, "render", "ground_bake", "manifest.json");
        var skipped = Path.Combine(dir, "render", "ground_bake", "skipped.json");
        string? bake = null;
        try
        {
            if (!c.Force && File.Exists(failed))
            {
                var why = JsonNode.Parse(File.ReadAllText(failed))?["reason"]?.ToString();
                return new(id, false, null, $"failed (max attempts): {why}", 0, n, new() { "failed_marker" }, 0);
            }
            var m0 = Check(dir);
            bool corridorOk = reverse is null || CorridorAlso(dir).Contains(reverse);
            bool reverseOk = reverse is null || File.Exists(Path.Combine(c.L.IrRoot, reverse, "stage.json"));
            bool ornamentsOk = HasOrnamentsKey(dir);
            if (!c.Force && m0.Required.Count == 0 && corridorOk && reverseOk && ornamentsOk)
                return new(id, true, File.Exists(manifest) ? "ok" : "skipped", null, m0.OptionalFiles.Count, n, new() { "skip" }, 0);

            if (!c.Force && n >= c.MaxPrepareAttempts)
            {
                var reason = $"exceeded {c.MaxPrepareAttempts} prepare attempts";
                WriteMarker(failed, reason);
                throw new StepFailed(reason);
            }
            n++;
            UpdateAttempts(attemptsPath, a => a[id] = n);
            var exe = c.L.Tool("dr2-extract");
            var dr2 = new[] { "--dr2", c.Dr2Dir! };

            bool needIr = c.Force || !corridorOk || !ornamentsOk || !File.Exists(terrain) || !File.Exists(Path.Combine(dir, "stage.json")) || !File.Exists(manifest) && !File.Exists(skipped);
            if (needIr)
            {
                var also = reverse is null ? Array.Empty<string>() : new[] { "--also", reverse };
                var ir = c.P.Run(exe, new[] { "stage-ir", id, "--render", "--bake-ground", "--bake-lighting", c.Cfg.BakeLighting, "--out", c.L.IrRoot }.Concat(also).Concat(dr2), log, $"{st.Id}: extracting stage data");
                steps.Add("stage-ir");
                if (ir.Code != 0)
                {
                    // soft-skip only when the bake cannot run for shader reasons, never for disk / IO failures
                    if (File.Exists(terrain) && ir.Text.Contains("no terrain_array triangles"))
                    {
                        var hint = ir.Lines.FirstOrDefault(x => x.Contains("no terrain_array"))?.Trim() ?? "ground bake failed";
                        WriteBakeSkipped(dir, hint);
                        bake = "skipped";
                    }
                    else throw new StepFailed($"stage-ir failed ({ir.Code}): {ir.Lines.FirstOrDefault(x => x.Contains("error:"))}");
                }
            }
            var m1 = Check(dir);
            var texDir = Path.Combine(dir, "textures");
            if (c.Force || m1.Required.Any(x => x.StartsWith("textures/")) || !Directory.Exists(texDir) || !Directory.EnumerateFiles(texDir).Any())
            {
                c.P.Must(exe, new[] { "stage-textures", id, "--ir", c.L.IrRoot }.Concat(dr2), log, $"{st.Id}: extracting textures");
                steps.Add("stage-textures");
            }
            if (reverse is not null && (c.Force || !reverseOk))
            {
                c.P.Must(exe, new[] { "stage-ir", reverse, "--out", c.L.IrRoot }.Concat(dr2), log, $"{st.Id}: extracting the reverse route");
                steps.Add("stage-ir reverse");
            }
            if (!File.Exists(terrain)) throw new StepFailed("no render/terrain.glb after stage-ir");
            if (!File.Exists(Path.Combine(dir, "stage.json"))) throw new StepFailed("no stage.json after stage-ir");
            if (File.Exists(manifest)) { bake = "ok"; if (File.Exists(skipped)) File.Delete(skipped); }
            else if (!File.Exists(skipped)) WriteBakeSkipped(dir, "no ground bake manifest after stage-ir");
            bake ??= File.Exists(manifest) ? "ok" : "skipped";

            var m2 = Check(dir);
            if (m2.Required.Count > 0)
                throw new StepFailed($"still missing required files ({m2.Required.Count}): {string.Join(", ", m2.Required.Take(5))}");
            UpdateAttempts(attemptsPath, a => a.Remove(id));
            if (File.Exists(failed)) File.Delete(failed);
            return new(id, true, bake, null, m2.OptionalFiles.Count, n, steps, (int)sw.Elapsed.TotalSeconds);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (n >= c.MaxPrepareAttempts && !File.Exists(failed)) WriteMarker(failed, e.Message);
            log.WriteLine($"prepare {id}: {e.Message}");
            return new(id, false, bake, e.Message, 0, n, steps, (int)sw.Elapsed.TotalSeconds);
        }
    }

    /// prepare-attempts.json is shared by the stages being prepared at the same time: read-modify-write under one lock.
    static readonly object AttemptsGate = new();

    static void UpdateAttempts(string p, Action<Dictionary<string, int>> change)
    {
        lock (AttemptsGate)
        {
            var a = ReadAttempts(p);
            change(a);
            SaveAttempts(p, a);
        }
    }

    static Dictionary<string, int> ReadAttempts(string p)
    {
        try { return File.Exists(p) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(p)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    static void SaveAttempts(string p, Dictionary<string, int> m) =>
        Files.WriteJson(p, new SortedDictionary<string, int>(m, StringComparer.Ordinal));

    static void WriteMarker(string path, string reason) =>
        Files.WriteJson(path, new { failed = true, reason, at = DateTime.Now.ToString("o") });

    static void WriteBakeSkipped(string dir, string reason) =>
        Files.WriteJson(Path.Combine(dir, "render", "ground_bake", "skipped.json"), new { skipped = true, reason, at = DateTime.Now.ToString("o") });

    /// work\dr2\menu-art: DR2 loading-screen art (painted aerials, no logos), idempotent.
    public static void MenuArt(Ctx c, TextWriter log)
    {
        if (Directory.Exists(c.L.MenuArtLoading) && Directory.EnumerateFiles(c.L.MenuArtLoading, "*.tpk").Any()) return;
        c.P.Must(c.L.Tool("dr2-extract"), new[]
        {
            "extract", Path.Combine(c.Dr2Dir!, "game", "game.dat"), c.L.MenuArt, "--filter", @"streamed_textures/loading/.*_rally_\d+_route_\d+\.tpk$",
        }, log, "extracting DR2 loading art");
    }
}
