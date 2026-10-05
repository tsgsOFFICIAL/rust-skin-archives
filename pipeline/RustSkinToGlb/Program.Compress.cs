using System.Text;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustSkinToGlb;

// Rewrites a finished GLB so its embedded PNG textures are WebP (EXT_texture_webp), nothing else changes.
// quality per texture type: 0 = lossless, 1..100 = lossy. Normal maps are the ones that suffer from lossy.
//   RustSkinToGlb --compress-glb <in.glb> <out.glb> [baseQ] [ormQ] [normalQ] [emissiveQ]
public static partial class Program
{
    internal static (long Before, long After) CompressGlb(string inPath, string outPath, int baseQ, int ormQ, int normalQ, int emissiveQ, int maxBase = 0, WebpEncodingMethod method = WebpEncodingMethod.BestQuality)
    {
        byte[] file = File.ReadAllBytes(inPath);
        if (BitConverter.ToUInt32(file, 0) != 0x46546C67) throw new InvalidDataException("not a GLB");
        int jsonLen = (int)BitConverter.ToUInt32(file, 12);
        var root = JsonNode.Parse(Encoding.UTF8.GetString(file, 20, jsonLen))!.AsObject();
        int binStart = 20 + jsonLen + 8;
        int binLen = (int)BitConverter.ToUInt32(file, 20 + jsonLen);

        var images = root["images"]?.AsArray();
        var textures = root["textures"]?.AsArray();
        var views = root["bufferViews"]!.AsArray();
        var role = new Dictionary<int, string>();   // image index -> what it is used for
        if (images != null && textures != null)
        {
            int Src(JsonNode? texInfo) => texInfo == null ? -1 : (int)(textures[(int)texInfo["index"]!]!["source"] ?? -1);
            foreach (var m in root["materials"]?.AsArray() ?? new JsonArray())
            {
                var pbr = m!["pbrMetallicRoughness"];
                if (Src(pbr?["baseColorTexture"]) is >= 0 and var b) role[b] = "base";
                if (Src(pbr?["metallicRoughnessTexture"]) is >= 0 and var o) role[o] = "orm";
                if (Src(m["normalTexture"]) is >= 0 and var n) role[n] = "normal";
                if (Src(m["emissiveTexture"]) is >= 0 and var e) role[e] = "emissive";
            }
        }

        // new bytes for the image buffer views
        var replaced = new Dictionary<int, byte[]>();
        for (int i = 0; i < (images?.Count ?? 0); i++)
        {
            var im = images![i]!;
            if (im["bufferView"] == null || (string?)im["mimeType"] != "image/png") continue;
            int bv = (int)im["bufferView"]!;
            int off = (int)(views[bv]!["byteOffset"] ?? 0), len = (int)views[bv]!["byteLength"]!;
            using var img = Image.Load<Rgba32>(new ReadOnlySpan<byte>(file, binStart + off, len));
            string kind = role.GetValueOrDefault(i, "base");
            int q = kind switch { "orm" => ormQ, "normal" => normalQ, "emissive" => emissiveQ, _ => baseQ };
            // base colour can be scaled down, it hides resolution loss much better than the data textures do
            if (kind == "base" && maxBase > 0 && Math.Max(img.Width, img.Height) > maxBase)
                img.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(maxBase, maxBase), Mode = ResizeMode.Max }));
            // q > 0 lossy, q == 0 lossless, q < 0 near-lossless at |q| (full resolution, small bounded error)
            var enc = q == 0 ? new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Quality = 100, Method = method }
                    : q < 0 ? new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Quality = 100, Method = method, NearLossless = true, NearLosslessQuality = -q }
                    : new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = q, Method = method };
            using var ms = new MemoryStream();
            img.Save(ms, enc);
            // never make a texture bigger
            if (ms.Length < len) { replaced[bv] = ms.ToArray(); im["mimeType"] = "image/webp"; }
        }

        // rebuild the bin chunk with the new image bytes, everything 4 byte aligned
        var bin = new MemoryStream();
        for (int v = 0; v < views.Count; v++)
        {
            var view = views[v]!;
            int off = (int)(view["byteOffset"] ?? 0), len = (int)view["byteLength"]!;
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            view["byteOffset"] = (int)bin.Length;
            if (replaced.TryGetValue(v, out var data)) { bin.Write(data); view["byteLength"] = data.Length; }
            else bin.Write(file, binStart + off, len);
        }
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        root["buffers"]![0]!["byteLength"] = (int)bin.Length;

        // textures that now point at a WebP image go through the extension
        if (images != null && textures != null && replaced.Count > 0)
        {
            foreach (var t in textures)
            {
                int s = (int)(t!["source"] ?? -1);
                if (s < 0 || (string?)images[s]!["mimeType"] != "image/webp") continue;
                t["extensions"] = new JsonObject { ["EXT_texture_webp"] = new JsonObject { ["source"] = s } };
                ((JsonObject)t).Remove("source");
            }
            foreach (var key in new[] { "extensionsUsed", "extensionsRequired" })
            {
                var arr = root[key]?.AsArray() ?? new JsonArray();
                if (!arr.Any(x => (string?)x == "EXT_texture_webp")) arr.Add("EXT_texture_webp");
                root[key] = arr;
            }
        }

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPad = (4 - json.Length % 4) % 4;
        using var outFile = File.Create(outPath);
        using var w = new BinaryWriter(outFile);
        w.Write(0x46546C67u); w.Write(2u);
        w.Write((uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Length));
        w.Write((uint)(json.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(json); for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
        return (file.Length, new FileInfo(outPath).Length);
    }

    // decodes every texture of two GLBs (png original, webp copy) and prints the error per texture type
    // PSNR for colour data (higher is better, above ~40 dB is hard to see), mean angle in degrees for normal maps
    internal static List<(string Role, double Psnr, double Angle)> CompareGlb(string aPath, string bPath)
    {
        (JsonObject Root, byte[] File, int Bin) Open(string p)
        {
            byte[] f = File.ReadAllBytes(p); int jl = (int)BitConverter.ToUInt32(f, 12);
            return (JsonNode.Parse(Encoding.UTF8.GetString(f, 20, jl))!.AsObject(), f, 20 + jl + 8);
        }
        var (ra, fa, ba) = Open(aPath); var (rb, fb, bb) = Open(bPath);
        var result = new List<(string, double, double)>();
        var roles = new Dictionary<int, string>();
        int Src(JsonObject r, JsonNode? ti) { if (ti == null) return -1; var t = r["textures"]![(int)ti["index"]!]!; return (int)(t["source"] ?? t["extensions"]?["EXT_texture_webp"]?["source"] ?? -1); }
        foreach (var m in ra["materials"]?.AsArray() ?? new JsonArray())
        {
            var pbr = m!["pbrMetallicRoughness"];
            if (Src(ra, pbr?["baseColorTexture"]) is >= 0 and var b) roles[b] = "base";
            if (Src(ra, pbr?["metallicRoughnessTexture"]) is >= 0 and var o) roles[o] = "orm";
            if (Src(ra, m["normalTexture"]) is >= 0 and var n) roles[n] = "normal";
            if (Src(ra, m["emissiveTexture"]) is >= 0 and var e) roles[e] = "emissive";
        }
        Image<Rgba32> Load(JsonObject r, byte[] f, int bin, int i)
        {
            var v = r["bufferViews"]![(int)r["images"]![i]!["bufferView"]!]!;
            return Image.Load<Rgba32>(new ReadOnlySpan<byte>(f, bin + (int)(v["byteOffset"] ?? 0), (int)v["byteLength"]!));
        }
        for (int i = 0; i < ra["images"]!.AsArray().Count; i++)
        {
            using var x = Load(ra, fa, ba, i); using var y0 = Load(rb, fb, bb, i);
            // a scaled down copy gets scaled back up so the two can be compared pixel by pixel
            using var y = y0.Width == x.Width && y0.Height == x.Height ? y0.Clone() : y0.Clone(c => c.Resize(x.Width, x.Height));
            double se = 0, ang = 0; long n = 0; string role = roles.GetValueOrDefault(i, "base");
            for (int py = 0; py < x.Height; py += 2)
                for (int px = 0; px < x.Width; px += 2)
                {
                    var p = x[px, py]; var q = y[px, py]; n++;
                    if (role == "normal")
                    {
                        // normal maps from the converter: R = x, G = y, B = z
                        double[] u = { p.R / 127.5 - 1, p.G / 127.5 - 1, p.B / 127.5 - 1 }, w = { q.R / 127.5 - 1, q.G / 127.5 - 1, q.B / 127.5 - 1 };
                        double dot = u[0] * w[0] + u[1] * w[1] + u[2] * w[2], lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]), lw = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]);
                        ang += Math.Acos(Math.Clamp(dot / Math.Max(lu * lw, 1e-9), -1, 1)) * 180 / Math.PI;
                    }
                    se += (Math.Pow(p.R - q.R, 2) + Math.Pow(p.G - q.G, 2) + Math.Pow(p.B - q.B, 2)) / 3.0;
                }
            double mse = se / n;
            result.Add((role, mse < 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse), ang / n));
        }
        return result;
    }

    private static int CompareGlbMain(string[] a)
    {
        foreach (var (role, psnr, angle) in CompareGlb(a[0], a[1]))
            Console.WriteLine(FormattableString.Invariant($"{role}\t{psnr:F1}\t{angle:F2}"));
        return 0;
    }

    // the settings the site uses: colour lossy 75, ORM and normals near-lossless (see JOURNEY / the size and quality tests)
    // encoder method 4 on purpose, method 6 makes near-lossless files bigger
    internal const int WebBaseQ = 75, WebOrmQ = -25, WebNormalQ = -40, WebEmissiveQ = -40;

    // compresses every raw GLB that has no compressed copy yet (or whose raw file is newer) into dstDir
    internal static (int Done, int Failed) CompressNew(string srcDir, string dstDir, Action<string> log)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var t in Directory.GetFiles(dstDir, "*.tmp")) File.Delete(t);
        var todo = Directory.GetFiles(srcDir, "*.glb").Where(f =>
        {
            string d = Path.Combine(dstDir, Path.GetFileName(f));
            return !File.Exists(d) || File.GetLastWriteTimeUtc(d) < File.GetLastWriteTimeUtc(f);
        }).ToList();
        if (todo.Count == 0) { log("Compress: all models already compressed"); return (0, 0); }
        log($"Compress: {todo.Count} model(s) to compress into {dstDir}");
        int done = 0, failed = 0;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) }, f =>
        {
            string dest = Path.Combine(dstDir, Path.GetFileName(f));
            try
            {
                CompressGlb(f, dest + ".tmp", WebBaseQ, WebOrmQ, WebNormalQ, WebEmissiveQ, 0, (WebpEncodingMethod)4);
                File.Move(dest + ".tmp", dest, true);
                Interlocked.Increment(ref done);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                log($"  compress failed for {Path.GetFileName(f)}: {ex.GetType().Name}: {ex.Message}");
                try { File.Delete(dest + ".tmp"); } catch { }
            }
        });
        return (done, failed);
    }

    // the cloudflare pages project that serves assets/ (models + icons), also the address app.js loads them from
    internal const string AssetsProject = "rust-skin-assets";

    // _headers lets any site fetch the files (the 3D viewer does), index.html is only so the root address isn't a 404
    internal static void EnsureAssetsSupportFiles(string assetsDir)
    {
        Directory.CreateDirectory(assetsDir);
        string headers = Path.Combine(assetsDir, "_headers");
        if (!File.Exists(headers))
            File.WriteAllText(headers, "/*\n  Access-Control-Allow-Origin: *\n  Cache-Control: public, max-age=86400\n", new UTF8Encoding(false));
        string index = Path.Combine(assetsDir, "index.html");
        if (!File.Exists(index))
            File.WriteAllText(index, "<!doctype html><title>rust-skin-archives assets</title><p>Models and icons for rust-skin-archives.</p>\n", new UTF8Encoding(false));
    }

    // uploads assets/ to cloudflare pages with wrangler (needs node and CLOUDFLARE_API_TOKEN, wrangler reads the token itself).
    // Only files that changed are uploaded. Returns true when the upload went through.
    internal static bool DeployAssets(string assetsDir, Action<string> log)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLOUDFLARE_API_TOKEN")))
        {
            log("CLOUDFLARE_API_TOKEN is not set, nothing uploaded. Upload by hand with: npx wrangler pages deploy \"" + assetsDir + "\" --project-name " + AssetsProject + " --branch main");
            return false;
        }
        EnsureAssetsSupportFiles(assetsDir);
        log("Uploading assets to Cloudflare Pages (only changed files are sent)");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c npx --yes wrangler pages deploy \"{assetsDir}\" --project-name {AssetsProject} --branch main")
        { WorkingDirectory = assetsDir, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (!p.WaitForExit((int)TimeSpan.FromHours(3).TotalMilliseconds)) { try { p.Kill(true); } catch { } log("Upload timed out"); return false; }
        return p.ExitCode == 0;
    }

    // --compress-batch <inDir> <outDir> <baseQ> <ormQ> <normalQ> <emissiveQ> <maxBase> <count> <method 0-6>
    // compresses <count> models (spread evenly through the folder) on all cores and prints progress
    private static int CompressBatchMain(string[] a)
    {
        string inDir = a[0], outDir = a[1];
        int bq = int.Parse(a[2]), oq = int.Parse(a[3]), nq = int.Parse(a[4]), eq = int.Parse(a[5]), maxBase = int.Parse(a[6]), count = int.Parse(a[7]);
        var method = (WebpEncodingMethod)int.Parse(a[8]);
        Directory.CreateDirectory(outDir);
        var all = Directory.GetFiles(inDir, "*.glb").OrderBy(f => f).ToList();
        var files = count <= 0 || count >= all.Count ? all : Enumerable.Range(0, count).Select(i => all[i * all.Count / count]).ToList();
        long before = 0, after = 0; int done = 0, failed = 0, skipped = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) }, f =>
        {
            string dest = Path.Combine(outDir, Path.GetFileName(f));
            try
            {
                // a finished file is skipped so an interrupted run can be started again, written via .tmp so a half file never counts
                if (File.Exists(dest)) { Interlocked.Increment(ref skipped); Interlocked.Add(ref before, new FileInfo(f).Length); Interlocked.Add(ref after, new FileInfo(dest).Length); }
                else
                {
                    var (b, af) = CompressGlb(f, dest + ".tmp", bq, oq, nq, eq, maxBase, method);
                    File.Move(dest + ".tmp", dest, true);
                    Interlocked.Add(ref before, b); Interlocked.Add(ref after, af);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                failures.Add($"{Path.GetFileName(f)}\t{ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}");
                try { File.Delete(dest + ".tmp"); } catch { }
            }
            int d = Interlocked.Increment(ref done);
            if (d % 50 == 0 || d == files.Count) Console.WriteLine($"[{d}/{files.Count}] {sw.Elapsed:hh\\:mm\\:ss}  {after / 1048576.0:F0} of {before / 1048576.0:F0} MB ({100.0 * after / Math.Max(before, 1):F0}%)  failed={failed} skipped={skipped}");
        });
        if (!failures.IsEmpty) File.WriteAllLines(Path.Combine(outDir, "compress-failures.tsv"), failures);
        Console.WriteLine($"DONE {files.Count} files: {before / 1048576.0:F0} MB -> {after / 1048576.0:F0} MB ({100.0 * after / Math.Max(before, 1):F0}%) in {sw.Elapsed:hh\\:mm\\:ss}, failed={failed}, skipped={skipped}");
        return failed == 0 ? 0 : 1;
    }

    private static int CompressGlbMain(string[] a)
    {
        int Q(int i, int def) => a.Length > i ? int.Parse(a[i]) : def;
        var (before, after) = CompressGlb(a[0], a[1], Q(2, 90), Q(3, 90), Q(4, 0), Q(5, 90));
        Console.WriteLine($"{Path.GetFileName(a[0])}: {before / 1024.0 / 1024:F1} MB -> {after / 1024.0 / 1024:F1} MB ({100.0 * after / before:F0}%)");
        return 0;
    }
}
