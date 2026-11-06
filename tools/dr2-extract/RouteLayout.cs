using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dr2Extract;

/// <summary>
/// <c>route-layout</c>: only the route of each stage (gate crossings + timing points, from progress_track.xml), for the
/// launcher's stage map before a stage is prepared. Same keys as stage.json (<c>centreline</c>, <c>timing[].type/position</c>,
/// <c>length_m</c>), one small file per stage: <c>&lt;out&gt;/&lt;stage_id&gt;.json</c>. Skips stages whose file exists.
/// </summary>
internal static class RouteLayout
{
    public static async Task<int> Run(string[] args)
    {
        var root = Program.Option(args, "--dr2") ?? Dr2Install.DefaultRoot;
        var outRoot = Program.Option(args, "--out") ?? Path.Combine("work", "layouts");
        var cache = Program.Option(args, "--cache") ?? Path.Combine("work", "dr2", "cache");
        var filter = new Regex(Program.Option(args, "--filter") ?? "_rally_");
        var install = await Dr2Install.OpenAsync(root, cache);
        Directory.CreateDirectory(outRoot);
        int written = 0, failed = 0;
        await foreach (var st in StageRef.Enumerate(install, filter))
        {
            var file = Path.Combine(outRoot, st.Id + ".json");
            if (File.Exists(file)) continue;
            try
            {
                var progress = ProgressTrack.Load(await install.FetchAsync($"{st.RouteDir}/progress_track.xml"));
                var start = progress.GateOf("start");
                var finish = progress.GateOf("finish");
                var layout = new Dictionary<string, object?>
                {
                    ["schema"] = "ACR-XS/route-layout",
                    ["id"] = st.Id,
                    ["length_m"] = StageIr.Round(finish.Distance - start.Distance, 2),
                    ["centreline"] = progress.Gates.Select(g => StageIr.V(g.Crossing, 1)).ToList(),
                    ["timing"] = progress.Splits.Select(s => new { type = s.Type, position = StageIr.V(progress.Gates[s.Gate].Crossing, 1) }).ToList(),
                };
                // write then rename, so a reader never sees half a file
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(layout, Json.Options));
                File.Move(file + ".tmp", file, true);
                written++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"{st.Id}: {e.Message}");
                failed++;
            }
        }

        Console.WriteLine($"route layouts: {written} written, {failed} failed -> {outRoot}");
        return failed == 0 ? 0 : 1;
    }
}
