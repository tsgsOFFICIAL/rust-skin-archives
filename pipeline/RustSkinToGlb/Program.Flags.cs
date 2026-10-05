using System.Collections.Concurrent;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RustSkinToGlb;

// glow / cutout flags for the site, from each skin's manifest and textures
//   glow   - a group has an _EmissionMap and a non black _EmissionColor
//   cutout - a group has _Cutoff between 0 and 1 and its _MainTex has see-through pixels
//            (Cut is the biggest share of pixels under the cutoff, the flag is Cut >= CutoutMinFraction)
// cached in data/skin-flags.json (raw values, so the threshold can change without a rescan),
// only skins missing from the cache get scanned
public static partial class Program
{
    // a cutout skin counts as "cutout" when at least this much of the main texture is cut away
    internal const double CutoutMinFraction = 0.01;

    internal sealed record SkinFlags(bool Glow, double Cut);

    internal static Dictionary<string, SkinFlags> LoadFlags(string cachePath, string skinsDir, ICollection<string> ids, Action<string> log)
    {
        var cache = new Dictionary<string, SkinFlags>();
        if (File.Exists(cachePath))
        {
            try { cache = JsonSerializer.Deserialize<Dictionary<string, SkinFlags>>(File.ReadAllText(cachePath)) ?? cache; }
            catch (JsonException) { log("skin-flags cache unreadable - rescanning everything"); }
        }
        var todo = ids.Where(id => !cache.ContainsKey(id)).ToList();
        if (todo.Count > 0)
        {
            log($"Scanning {todo.Count} skin(s) for glow / cutout");
            var added = new ConcurrentDictionary<string, SkinFlags>();
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                id => added[id] = ScanSkin(skinsDir, id));
            foreach (var kv in added) cache[kv.Key] = kv.Value;
            string tmp = cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache.OrderBy(k => long.Parse(k.Key)).ToDictionary(k => k.Key, k => k.Value)));
            File.Move(tmp, cachePath, true);
        }
        return cache;
    }

    private static SkinFlags ScanSkin(string skinsDir, string id)
    {
        string manifest = Path.Combine(skinsDir, id, "manifest", "manifest.txt");
        if (!File.Exists(manifest)) return new SkinFlags(false, 0);
        bool glow = false; double cut = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("Groups", out var groups)) return new SkinFlags(false, 0);
            foreach (var g in groups.EnumerateArray())
            {
                string? Tex(string key) => g.TryGetProperty("Textures", out var t) && t.ValueKind == JsonValueKind.Object
                    && t.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s != "none" ? s : null;

                // glow
                if (Tex("_EmissionMap") != null && g.TryGetProperty("Colors", out var cols) && cols.ValueKind == JsonValueKind.Object
                    && cols.TryGetProperty("_EmissionColor", out var ec) && ec.ValueKind == JsonValueKind.Object)
                {
                    double mx = 0;
                    foreach (var ch in new[] { "r", "g", "b" }) if (ec.TryGetProperty(ch, out var cv)) mx = Math.Max(mx, cv.GetDouble());
                    if (mx > 0.05) glow = true;
                }

                // cutout
                double cutoff = g.TryGetProperty("Floats", out var fl) && fl.ValueKind == JsonValueKind.Object
                    && fl.TryGetProperty("_Cutoff", out var cf) ? cf.GetDouble() : 0;
                string? main = Tex("_MainTex");
                if (cutoff > 0 && cutoff < 1 && main != null)
                {
                    string path = Path.Combine(skinsDir, id, Path.GetFileNameWithoutExtension(main).ToLowerInvariant(), main);
                    if (File.Exists(path)) cut = Math.Max(cut, CutFraction(path, cutoff));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UnknownImageFormatException or IOException) { }
        return new SkinFlags(glow, Math.Round(cut, 4));
    }

    // debug: --scan-alpha [out.tsv]
    // how much of the _MainTex is fully transparent (alpha < 8) on groups that are built opaque.
    // that's junk colour showing up where the game hides it (the green ropes on 3783739172)
    private static int ScanAlphaMain(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : Path.Combine(PipelineRoot, "data", "alpha-scan.tsv");
        var rows = new ConcurrentBag<string>();
        var ids = Directory.EnumerateDirectories(DefaultSkinsRoot).Select(Path.GetFileName).Where(n => n != null && n.All(char.IsDigit)).ToList();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, id =>
        {
            string manifest = Path.Combine(DefaultSkinsRoot, id!, "manifest", "manifest.txt");
            if (!File.Exists(manifest)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!doc.RootElement.TryGetProperty("Groups", out var groups)) return;
                int gi = 0;
                foreach (var g in groups.EnumerateArray())
                {
                    double cutoff = g.TryGetProperty("Floats", out var fl) && fl.ValueKind == JsonValueKind.Object && fl.TryGetProperty("_Cutoff", out var cf) ? cf.GetDouble() : 0;
                    string? main = g.TryGetProperty("Textures", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_MainTex", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : null;
                    if (main != null && !(cutoff > 0 && cutoff < 1))
                    {
                        string path = Path.Combine(DefaultSkinsRoot, id!, Path.GetFileNameWithoutExtension(main).ToLowerInvariant(), main);
                        if (File.Exists(path))
                        {
                            using var img = Image.Load<Rgba32>(path);
                            long zero = 0, total = (long)img.Width * img.Height;
                            img.ProcessPixelRows(acc => { for (int y = 0; y < acc.Height; y++) foreach (var p in acc.GetRowSpan(y)) if (p.A < 8) zero++; });
                            rows.Add($"{id}\t{gi}\t{cutoff}\t{(double)zero / total:F4}");
                        }
                    }
                    gi++;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnknownImageFormatException or InvalidOperationException) { }
        });
        File.WriteAllLines(outPath, rows.OrderBy(r => long.Parse(r.Split('\t')[0])).ThenBy(r => r));
        Console.WriteLine($"alpha scan: {rows.Count} opaque-mode groups -> {outPath}");
        return 0;
    }

    // Debug tool: --scan-normals [out.tsv], lists the groups whose _BumpMap is a plain RGB normal map (see TextureRepacker)
    private static int ScanNormalsMain(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : Path.Combine(PipelineRoot, "data", "normal-scan.tsv");
        var rows = new ConcurrentBag<string>();
        var ids = Directory.EnumerateDirectories(DefaultSkinsRoot).Select(Path.GetFileName).Where(n => n != null && n.All(char.IsDigit)).ToList();
        int scanned = 0;
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, id =>
        {
            string manifest = Path.Combine(DefaultSkinsRoot, id!, "manifest", "manifest.txt");
            if (!File.Exists(manifest)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!doc.RootElement.TryGetProperty("Groups", out var groups)) return;
                string itemType = doc.RootElement.TryGetProperty("ItemType", out var it) ? it.GetString() ?? "" : "";
                int gi = 0;
                foreach (var g in groups.EnumerateArray())
                {
                    string? bump = g.TryGetProperty("Textures", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_BumpMap", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                    if (bump != null && bump != "none")
                    {
                        string path = Path.Combine(DefaultSkinsRoot, id!, "_bumpmap" + gi, bump);
                        if (File.Exists(path))
                        {
                            Interlocked.Increment(ref scanned);
                            if (TextureRepacker.IsPlainRgbNormalMap(path)) rows.Add($"{id}\t{gi}\t{itemType}");
                        }
                    }
                    gi++;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnknownImageFormatException or InvalidOperationException) { }
        });
        File.WriteAllLines(outPath, rows.OrderBy(r => long.Parse(r.Split('\t')[0])).ThenBy(r => r));
        Console.WriteLine($"normal scan: {scanned} bump maps checked, {rows.Count} plain-RGB -> {outPath}");
        return 0;
    }

    private static double CutFraction(string path, double cutoff)
    {
        using var img = Image.Load<Rgba32>(path);
        byte limit = (byte)Math.Clamp((int)Math.Round(cutoff * 255), 1, 255);
        long cutPixels = 0, total = (long)img.Width * img.Height;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
                foreach (var p in acc.GetRowSpan(y)) if (p.A < limit) cutPixels++;
        });
        return total == 0 ? 0 : (double)cutPixels / total;
    }
}
