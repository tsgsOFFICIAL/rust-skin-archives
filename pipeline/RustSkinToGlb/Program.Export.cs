using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RustSkinToGlb;

// writes docs/skins.json from data.json and copies the icons to docs/icons/<id>.png
// runs at the end of the weekly update, or alone with `--export-viewer` (offline)
// one entry per buildable skin with a GLB, no prices in here (prices.json)
public static partial class Program
{
    internal static void ExportViewer(string dataPath, string catalogPath, string skinsDir, string glbDir, string viewerDir,
                                      Action<string> log)
    {
        Directory.CreateDirectory(viewerDir);
        string iconDir = Path.Combine(viewerDir, "icons");
        Directory.CreateDirectory(iconDir);
        string outPath = Path.Combine(viewerDir, "skins.json");

        // catalog date_created looks like 20260918T205611Z, turn it into a normal ISO date
        var created = new Dictionary<string, string>();
        if (File.Exists(catalogPath))
        {
            using var cat = JsonDocument.Parse(CleanJson(File.ReadAllText(catalogPath)));
            foreach (var d in cat.RootElement.EnumerateArray())
            {
                if (!d.TryGetProperty("workshopid", out var w) || !d.TryGetProperty("date_created", out var dc)) continue;
                string wid = w.ValueKind == JsonValueKind.Number ? w.GetInt64().ToString() : w.GetString() ?? "";
                if (wid is "" or "0") continue;
                if (DateTime.TryParseExact(dc.GetString(), "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    created[wid] = dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // glow / cutout are worked out from the manifest and textures (Program.Flags.cs), cached next to data.json
        List<string> exportIds;
        using (var pre = JsonDocument.Parse(File.ReadAllText(dataPath)))
            exportIds = pre.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("buildable", out var b0) && b0.ValueKind == JsonValueKind.True)
                .Select(e => e.GetProperty("id").GetString()!)
                .Where(id => File.Exists(Path.Combine(glbDir, id + ".glb"))).ToList();
        var flags = LoadFlags(Path.Combine(Path.GetDirectoryName(dataPath)!, "skin-flags.json"), skinsDir, exportIds, log);

        // which catalog icon url was used for each skin that gets Facepunch's icon, so it's only downloaded again when it changes
        string iconCachePath = Path.Combine(Path.GetDirectoryName(dataPath)!, "icon-cache.json");
        var iconCache = new Dictionary<string, string>();
        if (File.Exists(iconCachePath))
        {
            try { iconCache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(iconCachePath)) ?? iconCache; }
            catch (JsonException) { /* unreadable, just download them again */ }
        }

        var rows = new List<(long Id, string Json)>();
        var needIcons = new List<(string Id, string Url)>();
        int missingGlb = 0, missingIcon = 0, iconsCopied = 0;
        var opts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        using (var doc = JsonDocument.Parse(File.ReadAllText(dataPath)))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string id = e.GetProperty("id").GetString()!;
                if (!(e.TryGetProperty("buildable", out var b) && b.ValueKind == JsonValueKind.True)) continue;
                if (!File.Exists(Path.Combine(glbDir, id + ".glb"))) { missingGlb++; continue; }

                string icon = Path.Combine(skinsDir, id, "icon", "icon.png");
                string iconDest = Path.Combine(iconDir, id + ".png");
                string? url = e.TryGetProperty("iconUrlLarge", out var ul) && ul.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ul.GetString()) ? ul.GetString()
                            : e.TryGetProperty("iconUrl", out var us) && us.ValueKind == JsonValueKind.String ? us.GetString() : null;
                string source = e.TryGetProperty("dataSource", out var ds) && ds.ValueKind == JsonValueKind.String ? ds.GetString() ?? "" : "";

                if (source != "bundle" && url != null)
                {
                    // workshop skins come with whatever image the author uploaded (blank, a close-up, a flat texture),
                    // Facepunch's own icon looks like the rest of the site. The skin's own icon is only a fallback.
                    if (!File.Exists(iconDest) && File.Exists(icon)) { File.Copy(icon, iconDest, true); iconsCopied++; }
                    if (!File.Exists(iconDest) || !iconCache.TryGetValue(id, out var used) || used != url) needIcons.Add((id, url));
                }
                else if (File.Exists(icon))
                {
                    if (!File.Exists(iconDest) || new FileInfo(iconDest).Length != new FileInfo(icon).Length) { File.Copy(icon, iconDest, true); iconsCopied++; }
                }
                else if (!File.Exists(iconDest))
                {
                    // no icon from the skin itself (old skins), use the one from the catalog
                    if (url != null) needIcons.Add((id, url)); else missingIcon++;
                }

                // twitch drops have a "twitchdrop" token in the store tags
                bool isTwitch = e.TryGetProperty("storeTags", out var st) && st.ValueKind == JsonValueKind.String
                                && st.GetString()!.Split(';').Contains("twitchdrop");

                rows.Add((long.Parse(id), JsonSerializer.Serialize(new
                {
                    id,
                    displayName = e.GetProperty("name").GetString(),
                    itemShortName = e.GetProperty("itemShortName").GetString(),
                    modelUrls = new[] { id + ".glb" },
                    dateCreated = created.GetValueOrDefault(id),
                    twitchDrop = isTwitch ? true : (bool?)null,
                    glow = flags.TryGetValue(id, out var fl) && fl.Glow ? true : (bool?)null,
                    cutout = fl != null && fl.Cut >= CutoutMinFraction ? true : (bool?)null,
                }, opts)));
            }
        }

        int iconsDownloaded = 0;
        if (needIcons.Count > 0)
        {
            var done = DownloadIcons(needIcons, iconDir, log);
            iconsDownloaded = done.Count;
            foreach (var (id, url) in needIcons.Where(n => done.Contains(n.Id))) iconCache[id] = url;
            missingIcon += needIcons.Count(n => !done.Contains(n.Id) && !File.Exists(Path.Combine(iconDir, n.Id + ".png")));
            File.WriteAllText(iconCachePath, JsonSerializer.Serialize(iconCache.OrderBy(k => long.Parse(k.Key)).ToDictionary(k => k.Key, k => k.Value)), new UTF8Encoding(false));
        }

        // newest ids first so new skins show up on top
        var sb = new StringBuilder("[\n");
        sb.Append(string.Join(",\n", rows.OrderByDescending(r => r.Id).Select(r => "  " + r.Json)));
        sb.Append("\n]\n");
        string tmp = outPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, outPath, true);
        log($"Viewer export: {rows.Count} skins -> {outPath} ({iconsCopied} icons copied, {iconsDownloaded} downloaded from Facepunch; " +
            $"{missingGlb} without a GLB and {missingIcon} without an icon)");
    }

    // Downloads icons from Facepunch (urls come from the steam catalog).
    // Best effort: if one fails it's only logged, whatever icon was there stays and the next export tries again.
    // Only real PNGs get saved. Returns the ids that were saved.
    private static HashSet<string> DownloadIcons(List<(string Id, string Url)> items, string iconDir, Action<string> log)
    {
        var ok = new HashSet<string>();
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RustSkinViewerPipeline/1.0");
        foreach (var (id, url) in items)
        {
            string dest = Path.Combine(iconDir, id + ".png");
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    bool png = bytes.Length > 1024 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                    if (!png) { log($"  icon {id}: not a PNG from {url}"); break; }
                    File.WriteAllBytes(dest + ".tmp", bytes);
                    File.Move(dest + ".tmp", dest, true);
                    ok.Add(id);
                    break;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
                {
                    if (attempt == 2) log($"  icon {id}: download failed ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        return ok;
    }

    // --export-viewer: offline export from the real data folders
    private static int ExportViewerMain()
    {
        string viewer = Path.GetFullPath(Path.Combine(PipelineRoot, "..", "docs"));
        ExportViewer(Path.Combine(PipelineRoot, "data", "data.json"), Path.Combine(PipelineRoot, "data", "steam-itemdefs.json"),
                     DefaultSkinsRoot, DefaultGlbDir, viewer, s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"));
        return 0;
    }
}
