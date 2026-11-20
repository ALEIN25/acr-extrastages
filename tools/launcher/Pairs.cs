using System.Text.RegularExpressions;

namespace AcrExtraStages;

/// config/variants.toml [pairs]: a DR2 route and its reverse built into one stage container (docs/variants-plan.md). The forward
/// stage keeps its id and gets a FullReverse variant; the reverse stage is then not built on its own. Only pairs whose two
/// stages are both enabled (fit) count, and nothing counts while `enabled = false`.
internal sealed class Pairs
{
    readonly Dictionary<string, string> _reverseOf = new(StringComparer.OrdinalIgnoreCase);   // forward dr2 -> reverse dr2
    readonly Dictionary<string, string> _forwardOf = new(StringComparer.OrdinalIgnoreCase);   // reverse dr2 -> forward dr2
    public bool Enabled { get; private set; }

    public static string File(Layout l) => Path.Combine(l.Config, "variants.toml");

    public static Pairs Load(Layout l, StagesConfig cfg)
    {
        var p = new Pairs();
        var path = File(l);
        if (!System.IO.File.Exists(path)) return p;
        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? only = null;
        bool inPairs = false;
        foreach (var raw in System.IO.File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { inPairs = line == "[pairs]"; continue; }
            if (!inPairs) continue;
            var kv = Regex.Match(line, @"^([A-Za-z0-9_]+)\s*=\s*(.+)$");
            if (!kv.Success) continue;
            var v = StagesConfig.Value(kv.Groups[2].Value.Trim());
            if (kv.Groups[1].Value == "enabled") p.Enabled = v is true;
            else if (kv.Groups[1].Value == "only" && v is string o)
                only = o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            else if (v is string rev) listed[kv.Groups[1].Value] = rev;
        }
        if (!p.Enabled) return p;
        var enabled = cfg.Entries().Where(e => e.Enabled).Select(e => e.Dr2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (fwd, rev) in listed.Where(x => only is null || only.Contains(x.Key)))
            if (enabled.Contains(fwd) && enabled.Contains(rev)) { p._reverseOf[fwd] = rev; p._forwardOf[rev] = fwd; }
        return p;
    }

    /// The reverse built into this stage's container, or null.
    public string? ReverseOf(string dr2) => _reverseOf.GetValueOrDefault(dr2);

    /// The stage whose container carries this one as its reverse variant, or null (built on its own).
    public string? ForwardOf(string dr2) => _forwardOf.GetValueOrDefault(dr2);
}
