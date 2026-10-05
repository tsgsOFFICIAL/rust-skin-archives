using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustSkinToGlb;

// imports the old workshop skins (raw unity project uploads: .mat + .tga/.jpg/.png/.psd + .meta, sometimes a
// meta.asset) into the same layout as the modern ones (manifest/manifest.txt plus one folder per texture slot)
//
// groups come from meta.asset (skinMaterial0..N), without one the best non default .mat is used
// textures are found by GUID through the .meta files, stale GUIDs fall back to matching file names
// raw normal maps are plain RGB so they get repacked to the DXT5nm layout the rest of the pipeline reads
// anything above MaxTextureSize is scaled down (some raw uploads are 100 MB TGAs)
public static class LegacyImporter
{
    private const int MaxTextureSize = 2048;
    private static readonly string[] TextureSlots = { "_MainTex", "_BumpMap", "_SpecGlossMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap" };
    private static readonly Dictionary<string, string[]> SlotKeywords = new()
    {
        ["_MainTex"] = new[] { "diff", "albedo", "colour", "color", "base", "main" },
        ["_BumpMap"] = new[] { "normal", "norm", "bump" },
        ["_SpecGlossMap"] = new[] { "spec" },
        ["_MetallicGlossMap"] = new[] { "metal" },
        ["_OcclusionMap"] = new[] { "occlu", "_ao", ".ao" },
        ["_EmissionMap"] = new[] { "emiss", "glow" },
    };
    private static readonly string[] IgnoreNames = { "icon", "preview", "screenshot", "thumb" };

    private const string Num = @"-?[\d.]+(?:[eE][-+]?\d+)?";
    // Unity 5.x writes "data: / first: name: _X / second: ...", Unity 2017+ writes "- _X: ..."
    private static readonly Regex TexOld = new(@"name:\s*(_\w+)\s*\r?\n\s*second:\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+(?:,\s*guid:\s*([0-9a-f]{32}))?", RegexOptions.Compiled);
    private static readonly Regex TexNew = new(@"-\s*(_\w+):\s*\r?\n\s*m_Texture:\s*\{fileID:\s*-?\d+(?:,\s*guid:\s*([0-9a-f]{32}))?", RegexOptions.Compiled);
    private static readonly Regex FloatOld = new(@"name:\s*(_\w+)\s*\r?\n\s*second:\s*(" + Num + @")\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex FloatNew = new(@"-\s*(_\w+):\s*(" + Num + @")\s*\r?\n", RegexOptions.Compiled);

    public static void Run(string srcDir, string destSkinsDir, string dataJsonPath, string reportPath)
    {
        var idToShort = new Dictionary<string, string>();
        var shortToType = new Dictionary<string, string>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dataJsonPath)))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string id = e.GetProperty("id").GetString()!;
                string? sn = e.TryGetProperty("itemShortName", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                string? it = e.TryGetProperty("itemType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (sn != null) idToShort[id] = sn;
                if (sn != null && it != null) shortToType[sn] = it;
            }
        }

        var report = new List<string> { "id\tshortName\titemType\tgroups\tmainTex\tstatus\tnotes" };
        int ok = 0, skipped = 0, fail = 0, noMain = 0;
        foreach (var dir in Directory.GetDirectories(srcDir).OrderBy(d => d))
        {
            string id = Path.GetFileName(dir);
            if (!idToShort.TryGetValue(id, out var shortName)) continue;                 // not an official skin
            if (File.Exists(Path.Combine(dir, "manifest.txt"))) continue;                // modern format, handled elsewhere
            string destDir = Path.Combine(destSkinsDir, id);
            if (File.Exists(Path.Combine(destDir, "manifest", "manifest.txt"))) { skipped++; continue; }
            if (!shortToType.TryGetValue(shortName, out var itemType))
            {
                report.Add($"{id}\t{shortName}\t\t0\t0\tFAIL\tno ItemType known for shortname");
                fail++; continue;
            }
            try
            {
                var (groups, mainOk, notes) = ImportOne(dir, destDir, itemType);
                report.Add($"{id}\t{shortName}\t{itemType}\t{groups}\t{mainOk}/{groups}\tOK\t{notes}");
                ok++; if (mainOk < groups) noMain++;
            }
            catch (Exception ex)
            {
                report.Add($"{id}\t{shortName}\t{itemType}\t0\t0\tFAIL\t{ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
                fail++;
                try { if (Directory.Exists(destDir) && !File.Exists(Path.Combine(destDir, "manifest", "manifest.txt"))) Directory.Delete(destDir, true); } catch { }
            }
        }
        File.WriteAllLines(reportPath, report);
        Console.WriteLine($"Legacy import: ok={ok} (of which missing a _MainTex in some group: {noMain}) fail={fail} alreadyDone={skipped}. Report: {reportPath}");
    }

    private static (int groups, int mainOk, string notes) ImportOne(string dir, string destDir, string itemType)
    {
        var notes = new List<string>();

        // guid -> asset file
        var guidToFile = new Dictionary<string, string>();
        foreach (var meta in Directory.EnumerateFiles(dir, "*.meta", SearchOption.AllDirectories))
        {
            var m = Regex.Match(File.ReadAllText(meta), @"guid:\s*([0-9a-f]{32})");
            string asset = meta.Substring(0, meta.Length - ".meta".Length);
            if (m.Success && File.Exists(asset)) guidToFile[m.Groups[1].Value] = asset;
        }

        var candidates = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => Regex.IsMatch(p, @"\.(png|jpg|jpeg|tga|psd|gif)$", RegexOptions.IgnoreCase)
                        && !IgnoreNames.Any(n => Path.GetFileName(p).Contains(n, StringComparison.OrdinalIgnoreCase))
                        && !p.Contains(Path.DirectorySeparatorChar + "screenshots" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // materials, in group order
        var matFiles = new List<string>();
        var metaAsset = Directory.EnumerateFiles(dir, "*.asset", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(p).Equals("meta.asset", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".meta.asset", StringComparison.OrdinalIgnoreCase));
        if (metaAsset != null)
        {
            var refs = Regex.Matches(File.ReadAllText(metaAsset), @"skinMaterial(\d*):\s*\{[^}]*guid:\s*([0-9a-f]{32})")
                .Select(m => (idx: m.Groups[1].Value == "" ? 0 : int.Parse(m.Groups[1].Value), guid: m.Groups[2].Value))
                .OrderBy(x => x.idx);
            var refList = refs.ToList();
            // skinMaterial0..3 all pointing at the same material (a storage box for example) is one group, not four
            if (refList.Count > 1 && refList.Select(r => r.guid).Distinct().Count() == 1) { refList = refList.Take(1).ToList(); notes.Add("skinMaterial0..N identical -> 1 group"); }
            foreach (var r in refList) if (guidToFile.TryGetValue(r.guid, out var f) && f.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) matFiles.Add(f);
        }
        if (matFiles.Count == 0)
        {
            var all = Directory.EnumerateFiles(dir, "*.mat", SearchOption.AllDirectories).ToList();
            var cands = all.Where(p => !p.Contains(Path.DirectorySeparatorChar + "Materials" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                       && !Path.GetFileName(p).StartsWith("Default", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cands.Count == 0) cands = all;
            if (cands.Count == 0) throw new InvalidDataException("no .mat file");
            var best = cands.OrderByDescending(p => ParseMaterial(File.ReadAllText(p), guidToFile).textures.Count).First();
            matFiles.Add(best);
            notes.Add(cands.Count > 1 ? $"no meta.asset; picked best of {cands.Count} materials ({Path.GetFileName(best)})" : "no meta.asset; single material");
        }

        var groups = new List<Dictionary<string, object>>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int mainOk = 0;
        for (int gi = 0; gi < matFiles.Count; gi++)
        {
            var (textures, floats, unresolved) = ParseMaterial(File.ReadAllText(matFiles[gi]), guidToFile);
            var matTokens = Tokens(Path.GetFileNameWithoutExtension(matFiles[gi]));
            var texNames = new Dictionary<string, string>();

            foreach (var slot in TextureSlots)
            {
                var options = new List<string>();
                bool referenced = textures.ContainsKey(slot);
                if (referenced) options.Add(textures[slot]);
                if (referenced || unresolved.Contains(slot) || slot == "_MainTex")
                    options.AddRange(NameMatches(slot, candidates, used, matTokens));
                if (options.Count == 0) continue;

                string outName = $"{slot}{gi}.png";
                string outDir = Path.Combine(destDir, $"{slot.ToLowerInvariant()}{gi}");
                bool done = false;
                foreach (var src in options.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(outDir);
                        ConvertTexture(src, Path.Combine(outDir, outName), slot == "_BumpMap");
                        texNames[slot] = outName; used.Add(src); done = true;
                        if (!referenced || !string.Equals(src, textures.GetValueOrDefault(slot), StringComparison.OrdinalIgnoreCase))
                            notes.Add($"g{gi} {slot} <- {Path.GetFileName(src)} (by name)");
                        break;
                    }
                    catch (Exception ex) { notes.Add($"g{gi} {slot} {Path.GetFileName(src)} unreadable ({ex.GetType().Name})"); }
                }
                if (!done) try { if (Directory.Exists(outDir) && !Directory.EnumerateFileSystemEntries(outDir).Any()) Directory.Delete(outDir); } catch { }
            }
            if (texNames.ContainsKey("_MainTex")) mainOk++; else notes.Add($"g{gi} has no _MainTex");

            // _Cutoff only means something in cutout mode (_Mode == 1), standard materials always have a 0.5 default
            float mode = floats.GetValueOrDefault("_Mode", 0f);
            float cutoff = mode == 1f ? floats.GetValueOrDefault("_Cutoff", 0.5f) : 0f;
            var floatObj = new Dictionary<string, object>
            {
                ["_Cutoff"] = cutoff,
                ["_BumpScale"] = floats.GetValueOrDefault("_BumpScale", 1f),
                ["_Glossiness"] = floats.GetValueOrDefault("_Glossiness", 0.5f),
                ["_OcclusionStrength"] = floats.GetValueOrDefault("_OcclusionStrength", 1f),
            };
            groups.Add(new Dictionary<string, object> { ["Textures"] = texNames, ["Floats"] = floatObj });
        }

        // icon, used as the thumbnail
        var icon = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(p => Path.GetFileName(p).Contains("icon", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(p, @"\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase));
        if (icon != null)
        {
            try { Directory.CreateDirectory(Path.Combine(destDir, "icon")); ConvertTexture(icon, Path.Combine(destDir, "icon", "icon.png"), false, 512); }
            catch { notes.Add("icon unreadable"); }
        }

        var manifest = new Dictionary<string, object> { ["Version"] = 1, ["ItemType"] = itemType, ["ImportedFrom"] = "legacy raw Unity upload", ["Groups"] = groups };
        Directory.CreateDirectory(Path.Combine(destDir, "manifest"));
        File.WriteAllText(Path.Combine(destDir, "manifest", "manifest.txt"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return (groups.Count, mainOk, string.Join("; ", notes));
    }

    private static HashSet<string> Tokens(string name) =>
        Regex.Split(name.ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length >= 3).ToHashSet();

    // image files whose name looks like they belong to this slot, best match first
    private static IEnumerable<string> NameMatches(string slot, List<string> candidates, HashSet<string> used, HashSet<string> matTokens)
    {
        var pool = candidates.Where(p => !used.Contains(p)).ToList();
        bool Has(string path, string slotName) =>
            SlotKeywords[slotName].Any(k => Path.GetFileNameWithoutExtension(path).Contains(k, StringComparison.OrdinalIgnoreCase));
        var hits = pool.Where(p => Has(p, slot));
        if (slot == "_MainTex")
            hits = hits.Where(p => !TextureSlots.Where(s => s != "_MainTex").Any(s => Has(p, s)));
        var ordered = hits
            .OrderByDescending(p => Tokens(Path.GetFileNameWithoutExtension(p)).Intersect(matTokens).Count())
            .ThenBy(p => Path.GetExtension(p).Equals(".psd", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ordered.Count == 0 && slot == "_MainTex")
        {
            // no keyword hit, pick from the images that don't look like another map (identical copies count once)
            var rest = pool.Where(p => !TextureSlots.Any(s => Has(p, s)) &&
                                       !Regex.IsMatch(Path.GetFileNameWithoutExtension(p), @"highpass|mask|detail|displ|height|parallax", RegexOptions.IgnoreCase))
                           .GroupBy(p => new FileInfo(p).Length).Select(g => g.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First()).ToList();
            if (rest.Count == 1) ordered.Add(rest[0]);
            else if (rest.Count > 1)
            {
                // take the one file that shares a word with the material name (123.mat / 123.png),
                // otherwise guess the biggest image, the diffuse usually is
                var scored = rest.Select(p => (p, n: Tokens(Path.GetFileNameWithoutExtension(p)).Intersect(matTokens).Count())).OrderByDescending(x => x.n).ToList();
                if (scored[0].n > 0 && scored[1].n < scored[0].n) ordered.Add(scored[0].p);
                else ordered.Add(rest.OrderByDescending(p => new FileInfo(p).Length).First());
            }
            // last resort, some uploads call the diffuse "<name>.highpass" (hoodie.highpass.tga)
            if (ordered.Count == 0)
            {
                var hp = pool.Where(p => Regex.IsMatch(Path.GetFileNameWithoutExtension(p), @"highpass", RegexOptions.IgnoreCase) &&
                                         !TextureSlots.Where(s => s != "_MainTex").Any(s => Has(p, s)))
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (hp != null) ordered.Add(hp);
            }
        }
        return ordered;
    }

    private static (Dictionary<string, string> textures, Dictionary<string, float> floats, HashSet<string> unresolved)
        ParseMaterial(string text, Dictionary<string, string> guidToFile)
    {
        var textures = new Dictionary<string, string>();
        var unresolved = new HashSet<string>();
        foreach (var rx in new[] { TexOld, TexNew })
            foreach (Match m in rx.Matches(text))
            {
                string slot = m.Groups[1].Value;
                if (!m.Groups[2].Success) continue;                       // nothing in this slot
                if (guidToFile.TryGetValue(m.Groups[2].Value, out var f)) { if (!textures.ContainsKey(slot)) textures[slot] = f; }
                else unresolved.Add(slot);
            }
        var floats = new Dictionary<string, float>();
        foreach (var rx in new[] { FloatOld, FloatNew })
            foreach (Match m in rx.Matches(text))
                if (float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) floats.TryAdd(m.Groups[1].Value, v);
        return (textures, floats, unresolved);
    }

    private static Image<Rgba32> LoadImage(string path) =>
        path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) ? LoadPsd(path) : Image.Load<Rgba32>(path);

    private static void ConvertTexture(string src, string dest, bool isNormalMap, int maxSize = MaxTextureSize)
    {
        using var img = LoadImage(src);
        if (isNormalMap)
        {
            // plain RGB normal map -> DXT5nm layout (R=1, G=Y, B=Y, A=X)
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++) { var p = row[x]; row[x] = new Rgba32(255, p.G, p.G, p.R); }
                }
            });
        }
        if (Math.Max(img.Width, img.Height) > maxSize)
            img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(maxSize, maxSize), Mode = ResizeMode.Max }));
        img.SaveAsPng(dest);
    }

    // Small PSD reader. Only reads the flattened image of 8/16-bit RGB(A) or grayscale files, raw or RLE.
    private static ushort U16(BinaryReader br) { var b = br.ReadBytes(2); return (ushort)((b[0] << 8) | b[1]); }
    private static uint U32(BinaryReader br) { var b = br.ReadBytes(4); return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]); }

    private static Image<Rgba32> LoadPsd(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "8BPS") throw new InvalidDataException("not a PSD");
        if (U16(br) != 1) throw new InvalidDataException("PSB not supported");
        br.ReadBytes(6);
        int channels = U16(br); int height = (int)U32(br); int width = (int)U32(br); int depth = U16(br); int mode = U16(br);
        if (depth != 8 && depth != 16) throw new InvalidDataException($"PSD depth {depth} not supported");
        if (mode != 3 && mode != 1) throw new InvalidDataException($"PSD colour mode {mode} not supported");
        for (int i = 0; i < 3; i++) { uint len = U32(br); fs.Seek(len, SeekOrigin.Current); }   // skip colour mode data, image resources and layer/mask info
        int compression = U16(br);
        int bytesPer = depth / 8;
        int rowBytes = width * bytesPer;
        var planes = new byte[channels][];
        if (compression == 0)
        {
            for (int c = 0; c < channels; c++) planes[c] = br.ReadBytes(rowBytes * height);
        }
        else if (compression == 1)
        {
            var counts = new int[channels * height];
            for (int i = 0; i < counts.Length; i++) counts[i] = U16(br);
            for (int c = 0; c < channels; c++)
            {
                var plane = new byte[rowBytes * height];
                for (int y = 0; y < height; y++)
                {
                    var src = br.ReadBytes(counts[c * height + y]);
                    int si = 0, di = y * rowBytes, end = di + rowBytes;
                    while (si < src.Length && di < end)
                    {
                        int n = (sbyte)src[si++];
                        if (n >= 0) { int cnt = n + 1; for (int k = 0; k < cnt && di < end && si < src.Length; k++) plane[di++] = src[si++]; }
                        else if (n != -128) { int cnt = 1 - n; byte v = si < src.Length ? src[si++] : (byte)0; for (int k = 0; k < cnt && di < end; k++) plane[di++] = v; }
                    }
                }
                planes[c] = plane;
            }
        }
        else throw new InvalidDataException($"PSD compression {compression} not supported");

        var img = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * rowBytes + x * bytesPer;
                byte r = planes[0][i];
                byte g = channels >= 3 ? planes[1][i] : r;
                byte b = channels >= 3 ? planes[2][i] : r;
                byte a = channels >= 4 ? planes[3][i] : (channels == 2 ? planes[1][i] : (byte)255);
                img[x, y] = new Rgba32(r, g, b, a);
            }
        return img;
    }
}
