using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcrExtraStages;

/// `AcrExtraStages.exe ui` (and a plain double-click): the launcher as a local web page. A small HTTP server on
/// 127.0.0.1 serves ui/index.html (embedded) and a JSON API; every API call must carry the session token from the URL
/// (X-Token header), so no other site in the browser can drive it. Jobs run the same code as the console commands
/// (Program.Build / UninstallCmd), one at a time, with everything the launcher prints streamed to the page.
///   GET  /api/state            folders, stages (location, name, length, selected / built / installed), job
///   GET  /api/layout?id=       the stage's route (thinned centreline, heights, timing points) once prepared
///   POST /api/select {ids}     the stage selection (what Install builds and installs)
///   POST /api/folders {acr, dr2}
///   POST /api/job {action, jobs}   install | update | build | uninstall
///   GET  /api/log?from=n       job output lines from n on
internal static class GuiServer
{
    sealed class Job
    {
        public string Action = "";
        public DateTime Started;
        public volatile bool Running;
        public int? Exit;
        public readonly List<JsonObject> Lines = new();
    }

    static Job? _job;

    public static int Run(Ctx c, bool open)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        HttpListener? listener = null;
        string prefix = "";
        for (int port = 8790; port < 8810 && listener is null; port++)
        {
            try
            {
                var l = new HttpListener();
                prefix = $"http://127.0.0.1:{port}/";
                l.Prefixes.Add(prefix);
                l.Start();
                listener = l;
            }
            catch (HttpListenerException) { }
        }
        if (listener is null) throw new LauncherException("no free local port (8790-8809) for the UI");

        Ui.Yes = true;   // the page asks; nothing may wait for console input behind it
        Ui.Echo = (kind, text) =>
        {
            var j = _job;
            if (j is null || !j.Running) return;
            lock (j.Lines) j.Lines.Add(new JsonObject { ["k"] = kind, ["t"] = text });
        };
        Preflight.Detect(c, false);
        // no saved selection yet but stages in the game: start from what is installed, so Install keeps it
        if (c.S.Selected is null && c.S.Installed is { Count: > 0 }) { c.S.Selected = c.S.Installed.ToList(); c.SaveSettings(); }
        StartRouteLayouts(c);
        var url = $"{prefix}?t={token}";
        Console.Title = "ACR Extra Stages";
        Console.WriteLine($"ACR Extra Stages {Program.Version}: the launcher is open in your browser at");
        Console.WriteLine($"  {url}");
        Console.WriteLine("Keep this window open while you use it; close it to quit.");
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.WriteLine($"(open it by hand: {e.Message})"); }
        }

        while (true)
        {
            var ctx = listener.GetContext();
            try { Handle(c, ctx, token); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                try { Send(ctx, 500, new JsonObject { ["error"] = e.Message }); } catch (Exception) { }
            }
        }
    }

    static void Handle(Ctx c, HttpListenerContext ctx, string token)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path is "/" or "/index.html")
        {
            using var s = typeof(GuiServer).Assembly.GetManifestResourceStream("ui.index.html")!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Send(ctx, 200, ms.ToArray(), "text/html; charset=utf-8");
            return;
        }
        if (!path.StartsWith("/api/")) { Send(ctx, 404, new JsonObject { ["error"] = "not found" }); return; }
        if (ctx.Request.Headers["X-Token"] != token) { Send(ctx, 403, new JsonObject { ["error"] = "bad token" }); return; }

        var body = ctx.Request.HttpMethod == "POST" ? JsonNode.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd()) : null;
        switch (path)
        {
            case "/api/state": Send(ctx, 200, State(c)); break;
            case "/api/layout":
            {
                var cfg = StagesConfig.Load(c.L.StagesToml);
                var e = cfg.Entries().FirstOrDefault(x => string.Equals(x.Id, ctx.Request.QueryString["id"], StringComparison.OrdinalIgnoreCase));
                if (e is null) { Send(ctx, 404, new JsonObject { ["error"] = "unknown stage" }); break; }
                var json = Layout(Path.Combine(c.L.IrRoot, e.Dr2, "stage.json"));
                // not prepared: the route alone, read from DiRT Rally 2.0 in the background (dr2-extract route-layout)
                if (json is null && (json = Layout(Path.Combine(c.L.Layouts, e.Dr2 + ".json"))) is not null) json["prepared"] = false;
                if (json is null) StartRouteLayouts(c);
                Send(ctx, 200, json ?? new JsonObject { ["prepared"] = false, ["pending"] = _routes == RouteState.Running });
                break;
            }
            case "/api/select":
            {
                var cfg = StagesConfig.Load(c.L.StagesToml);
                var want = (body?["ids"]?.AsArray() ?? new JsonArray()).Select(x => x!.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                c.S.Selected = cfg.Entries().Where(e => e.Enabled && want.Contains(e.Id)).Select(e => e.Id).ToList();
                c.SaveSettings();
                Send(ctx, 200, new JsonObject { ["selected"] = c.S.Selected.Count });
                break;
            }
            case "/api/folders":
            {
                var acr = body?["acr"]?.ToString(); var dr2 = body?["dr2"]?.ToString();
                var errors = new JsonArray();
                if (!string.IsNullOrWhiteSpace(acr)) { if (Steam.IsAcr(acr)) c.AcrDir = Path.GetFullPath(acr); else errors.Add($"not an Assetto Corsa Rally folder: {acr}"); }
                if (!string.IsNullOrWhiteSpace(dr2)) { if (Steam.IsDr2(dr2)) c.Dr2Dir = Path.GetFullPath(dr2); else errors.Add($"not a DiRT Rally 2.0 folder: {dr2}"); }
                c.SaveSettings();
                Send(ctx, errors.Count == 0 ? 200 : 400, new JsonObject { ["errors"] = errors });
                break;
            }
            case "/api/job":
            {
                var action = body?["action"]?.ToString() ?? "";
                if (action is not ("install" or "update" or "build" or "uninstall")) { Send(ctx, 400, new JsonObject { ["error"] = $"unknown action {action}" }); break; }
                if (_job is { Running: true }) { Send(ctx, 409, new JsonObject { ["error"] = $"{_job.Action} is still running" }); break; }
                if (body?["jobs"] is JsonValue jv && jv.TryGetValue<int>(out var jobs) && jobs is >= 0 and <= 8) c.Jobs = jobs;
                var job = new Job { Action = action, Started = DateTime.Now, Running = true };
                _job = job;
                new Thread(() =>
                {
                    int code;
                    try
                    {
                        code = action switch
                        {
                            "install" => Program.Build(c, true, false, false),
                            "update" => Program.Build(c, true, true, false),
                            "build" => Program.Build(c, false, false, false),
                            _ => Program.UninstallCmd(c) >= 0 ? 0 : 1,
                        };
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Ui.Err("error: " + e.Message);
                        c.Log?.WriteLine(e.ToString());
                        code = 1;
                    }
                    job.Exit = code;
                    job.Running = false;
                }) { IsBackground = true, Name = "job " + action }.Start();
                Send(ctx, 200, new JsonObject { ["started"] = action });
                break;
            }
            case "/api/log":
            {
                int from = int.TryParse(ctx.Request.QueryString["from"], out var f) ? f : 0;
                var j = _job;
                var lines = new JsonArray();
                int next = from;
                if (j is not null)
                    lock (j.Lines)
                    {
                        for (int i = Math.Max(0, from); i < j.Lines.Count; i++) lines.Add(j.Lines[i].DeepClone());
                        next = j.Lines.Count;
                    }
                Send(ctx, 200, new JsonObject
                {
                    ["lines"] = lines, ["next"] = next, ["action"] = j?.Action, ["running"] = j?.Running ?? false, ["exit"] = j?.Exit,
                    ["seconds"] = j is null ? 0 : (int)(DateTime.Now - j.Started).TotalSeconds,
                });
                break;
            }
            default: Send(ctx, 404, new JsonObject { ["error"] = "not found" }); break;
        }
    }

    static JsonObject State(Ctx c)
    {
        var cfg = StagesConfig.Load(c.L.StagesToml);
        var sel = c.Selection(cfg).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = c.S.Installed?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stages = new JsonArray();
        var listed = ListedNames(c.L.StagesToml);
        var pairs = Pairs.Load(c.L, cfg);
        var idOf = cfg.Entries().ToDictionary(e => e.Dr2, e => e.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<(StageEntry E, string Name, double? Km, double? Mb)>();
        foreach (var e in cfg.Entries())
        {
            var (name, km) = Program.StageInfo(c, e);
            // not prepared yet: the name and length from the comment under the stage in config/stages.toml
            if ((name == e.Dr2 || km is null) && listed.TryGetValue(e.Dr2, out var l)) { if (name == e.Dr2) name = l.name; km ??= l.km; }
            rows.Add((e, name, km, ContainerMb(c, e.Id)));
        }
        var estimate = SizeModel(rows.Where(r => r.Mb is not null).Select(r => (r.E.LocationKey, r.Km, r.Mb!.Value)).ToList());
        foreach (var (e, name, km, mb) in rows)
        {
            stages.Add(new JsonObject
            {
                ["id"] = e.Id, ["dr2"] = e.Dr2, ["location"] = e.LocationName, ["locationKey"] = e.LocationKey,
                ["name"] = name == e.Dr2 ? null : name, ["km"] = km is { } k ? Math.Round(k, 2) : null,
                ["enabled"] = e.Enabled, ["recommended"] = e.Default, ["selected"] = sel.Contains(e.Id),
                ["built"] = mb is not null,
                // MB in the game's ~mods once installed: the built container, else predicted from the built ones
                // a pair's container also carries the reverse cell, which repeats the ~140 MB of DR2 ground / object meshes (variants-plan.md)
                ["sizeMb"] = mb is { } m ? Math.Round(m) : null,
                ["estMb"] = mb is not null || pairs.ForwardOf(e.Dr2) is not null ? null : Math.Round(estimate(e.LocationKey, km) + (pairs.ReverseOf(e.Dr2) is null ? 0 : 140)),
                // pairs: the reverse stage lives in its forward stage's container (no size or container of its own)
                ["reverseOf"] = pairs.ForwardOf(e.Dr2) is { } fwd ? idOf[fwd] : null,
                ["reverseId"] = pairs.ReverseOf(e.Dr2) is { } rv ? idOf[rv] : null,
                ["installed"] = installed.Contains(e.Id) || pairs.ForwardOf(e.Dr2) is { } f2 && installed.Contains(idOf[f2]),
                ["prepared"] = File.Exists(Path.Combine(c.L.IrRoot, e.Dr2, "stage.json")),
                ["preview"] = File.Exists(Path.Combine(c.L.Preview, e.Id, "preview.glb")),
            });
        }
        double? freeGb = null;
        try { freeGb = Math.Round(new DriveInfo(Path.GetPathRoot(c.L.Work)!).AvailableFreeSpace / 1073741824.0, 1); } catch (Exception) { }
        var j = _job;
        return new JsonObject
        {
            ["version"] = Program.Version, ["acr"] = c.AcrDir, ["dr2"] = c.Dr2Dir, ["mods"] = c.AcrDir is null ? null : c.ModsDir,
            ["acrRunning"] = Preflight.AcrRunning(), ["freeGb"] = freeGb, ["jobs"] = c.EffectiveJobs,
            ["installedAt"] = c.S.InstalledAt, ["stages"] = stages,
            ["job"] = j is null ? null : new JsonObject { ["action"] = j.Action, ["running"] = j.Running, ["exit"] = j.Exit },
        };
    }

    /// A built stage's container (.ucas + .utoc + .pak in dist, the files Install puts in ~mods), MB; null when not built.
    static double? ContainerMb(Ctx c, string id)
    {
        var stem = Path.Combine(c.L.Dist, $"zz_acrxs_s_{id.ToLowerInvariant()}_P");
        if (!File.Exists(stem + ".ucas")) return null;
        return new[] { ".ucas", ".utoc", ".pak" }.Select(x => stem + x).Where(File.Exists).Sum(f => new FileInfo(f).Length) / 1048576.0;
    }

    /// Predicted container MB of an unbuilt stage from the built ones. Size follows the location (shared art, terrain
    /// density) far more than the length: measured on 15 containers, 642-1241 MB, a fit on km alone is off by 85 MB on
    /// average, the stages of one location agree within ~70 MB. So: a least-squares fit on km over all built stages (3+),
    /// shifted onto the built stages of the same location when there are any; else their mean, else 1000 MB.
    static Func<string, double?, double> SizeModel(List<(string Loc, double? Km, double Mb)> built)
    {
        var byLoc = built.Where(b => b.Km is not null).GroupBy(b => b.Loc, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Mb: g.Average(b => b.Mb), Km: g.Average(b => b.Km!.Value)), StringComparer.OrdinalIgnoreCase);
        double mean = built.Count > 0 ? built.Average(b => b.Mb) : 1000, a = mean, k = 0;
        var pts = built.Where(b => b.Km is not null).Select(b => (X: b.Km!.Value, Y: b.Mb)).ToList();
        if (pts.Count >= 3)
        {
            double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y), sxx = pts.Sum(p => (p.X - mx) * (p.X - mx));
            if (sxx > 0) { k = pts.Sum(p => (p.X - mx) * (p.Y - my)) / sxx; a = my - k * mx; }
        }
        return (loc, km) => Math.Max(mean / 2, (byLoc.TryGetValue(loc, out var m), km) switch
        {
            (true, { } x) => m.Mb + k * (x - m.Km),
            (true, null) => m.Mb,
            (false, { } x) => a + k * x,
            _ => mean,
        });
    }

    static readonly Dictionary<string, (DateTime At, JsonObject Json)> _layouts = new(StringComparer.OrdinalIgnoreCase);

    enum RouteState { Idle, Running, Done }
    static volatile RouteState _routes;

    /// Routes of every stage, for the map before Build / Install: one dr2-extract run in the background (a few seconds per
    /// location archive), once per launcher session; files already written are skipped.
    static void StartRouteLayouts(Ctx c)
    {
        if (_routes != RouteState.Idle || c.Dr2Dir is null) return;
        _routes = RouteState.Running;
        new Thread(() =>
        {
            try { c.P.Run(c.L.Tool("dr2-extract"), ["route-layout", "--out", c.L.Layouts, "--dr2", c.Dr2Dir], null); }
            catch (Exception e) when (e is not OutOfMemoryException) { c.Log?.WriteLine("route layouts: " + e.Message); }
            _routes = RouteState.Done;
        }) { IsBackground = true, Name = "route layouts" }.Start();
    }

    /// The stage layout from the prepared IR (stage.json): the centreline thinned to about 400 points as
    /// [x, z, height, distance m] in DR2's frame (plan = x, z), and the start / split / finish distances.
    static JsonObject? Layout(string stageJson)
    {
        if (!File.Exists(stageJson)) return null;
        var at = File.GetLastWriteTimeUtc(stageJson);
        lock (_layouts)
            if (_layouts.TryGetValue(stageJson, out var hit) && hit.At == at) return (JsonObject)hit.Json.DeepClone();

        using var doc = JsonDocument.Parse(File.ReadAllBytes(stageJson));
        var root = doc.RootElement;
        var pts = new List<(float X, float Y, float Z)>();
        foreach (var p in root.GetProperty("centreline").EnumerateArray())
            pts.Add((p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
        var dist = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++)
            dist[i] = dist[i - 1] + MathF.Sqrt(MathF.Pow(pts[i].X - pts[i - 1].X, 2) + MathF.Pow(pts[i].Z - pts[i - 1].Z, 2));
        int step = Math.Max(1, pts.Count / 400);
        var line = new JsonArray();
        for (int i = 0; i < pts.Count; i += step)
            line.Add(new JsonArray(MathF.Round(pts[i].X, 1), MathF.Round(pts[i].Z, 1), MathF.Round(pts[i].Y, 1), MathF.Round(dist[i])));
        if ((pts.Count - 1) % step != 0)
            line.Add(new JsonArray(MathF.Round(pts[^1].X, 1), MathF.Round(pts[^1].Z, 1), MathF.Round(pts[^1].Y, 1), MathF.Round(dist[^1])));

        // timing points sit on the route: the distance of the nearest centreline point
        var timing = new JsonArray();
        if (root.TryGetProperty("timing", out var tm))
            foreach (var t in tm.EnumerateArray())
            {
                if (!t.TryGetProperty("position", out var pos)) continue;
                float px = pos[0].GetSingle(), pz = pos[2].GetSingle();
                int best = 0; float bd = float.MaxValue;
                for (int i = 0; i < pts.Count; i++)
                {
                    var d = (pts[i].X - px) * (pts[i].X - px) + (pts[i].Z - pz) * (pts[i].Z - pz);
                    if (d < bd) { bd = d; best = i; }
                }
                timing.Add(new JsonObject { ["type"] = t.GetProperty("type").GetString(), ["d"] = MathF.Round(dist[best]) });
            }

        var json = new JsonObject
        {
            ["prepared"] = true, ["name"] = root.TryGetProperty("name", out var n) ? n.GetString() : null,
            ["lengthM"] = root.TryGetProperty("length_m", out var len) ? Math.Round(len.GetDouble()) : null,
            ["line"] = line, ["timing"] = timing,
        };
        lock (_layouts) _layouts[stageJson] = (at, json);
        return (JsonObject)json.DeepClone();
    }

    /// config/stages.toml lists each stage as `dr2 = "<id>"` followed (within the entry) by a comment "# <Name>, <n> km".
    static Dictionary<string, (string name, double? km)> ListedNames(string toml)
    {
        var map = new Dictionary<string, (string, double?)>(StringComparer.OrdinalIgnoreCase);
        string? cur = null;
        foreach (var raw in File.ReadLines(toml))
        {
            var line = raw.Trim();
            var d = System.Text.RegularExpressions.Regex.Match(line, "^dr2\\s*=\\s*\"([^\"]+)\"");
            if (d.Success) { cur = d.Groups[1].Value; continue; }
            if (line.StartsWith("[")) { cur = null; continue; }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^#\s*(.+?),\s*([0-9.]+)\s*km");
            if (cur is not null && m.Success)
            {
                map[cur] = (m.Groups[1].Value.Trim(), double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k) ? k : null);
                cur = null;
            }
        }
        return map;
    }

    static void Send(HttpListenerContext ctx, int code, JsonNode json) =>
        Send(ctx, code, Encoding.UTF8.GetBytes(json.ToJsonString()), "application/json; charset=utf-8");

    static void Send(HttpListenerContext ctx, int code, byte[] data, string type)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = type;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = data.Length;
        ctx.Response.OutputStream.Write(data);
        ctx.Response.OutputStream.Close();
    }
}
