using System.Numerics;

namespace AcrRef;

/// Render-only lighting controls; no positions, indices or collision data are changed.
internal static class FoliageLighting
{
    public static bool Spherical(Toml cfg) =>
        (cfg["vegetation"].TryGetValue("foliage_normals", out var v) ? v as string : "original") switch
        {
            "original" => false,
            "spherical" => true,
            _ => throw new InvalidDataException("vegetation.foliage_normals must be original or spherical"),
        };

    public static Vector3? Centre(IEnumerable<Vector3> foliage)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var p in foliage) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true; }
        return any ? (min + max) * 0.5f : null;
    }

    public static Vector3 Normal(Vector3 position, Vector3 centre, Vector3 fallback)
    {
        var d = position - centre;
        return d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : fallback;
    }
}
