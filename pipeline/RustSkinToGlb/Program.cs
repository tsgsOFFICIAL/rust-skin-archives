using System.Text.Json;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SixLabors.ImageSharp;

namespace RustSkinToGlb;

// one mesh part of a skinnable item (barrel, main, maggrip, stock, ...)
// ObjPath is the WorkshopSource LOD0 obj, the textures are the skin's. Glossiness / OcclusionStrength /
// BumpScale are the manifest's values, unity multiplies them into the textures
public sealed record PartInput(
    string PartName,
    string ObjPath,
    string MainTexPath,
    string? BumpMapPath,
    string? SpecGlossMapPath,
    string? OcclusionMapPath,
    string? EmissionMapPath = null,
    float Glossiness = 1f,
    float OcclusionStrength = 1f,
    float BumpScale = 1f,
    float Cutoff = 0f,
    float StripIslandsFraction = 0f,
    float ScaleFactor = 1f,
    bool FirstObjectOnly = false,
    float StripSmallIslandsFraction = 0f,
    int IslandRank = -1,
    int ObjectIndex = -1,
    bool StripSmallestIsland = false,
    // Unity's metallic workflow, used instead of SpecGlossMap (R = metallic, A = smoothness).
    // A manifest has one or the other, never both.
    string? MetallicGlossMapPath = null,
    // rotation in degrees so the item stands upright and faces the camera
    float RotX = 0f, float RotY = 0f, float RotZ = 0f,
    // only load this OBJ `g <name>` group, null = the whole file
    string? GeometryGroup = null,
    // colour-only skins: emissive factor from the manifest's _EmissionColor (null = white)
    System.Numerics.Vector3? EmissionColor = null,
    // drop islands whose X centre is further than this from the centre line (see StripIslandsByXOffset),
    // for merged OBJs with floating props off to one side
    float? StripSideIslandsMaxAbsX = null,
    // rough group split by Z range (see ObjLoader.FilterByZRange)
    (float? minZ, float? maxZ)? ZRange = null,
    // plain transparent pane (shop front glass): solid tint at this opacity, glTF BLEND
    float? GlassAlpha = null);

public static partial class Program
{
    public static void Main(string[] args)
    {
        // batch mode: builds every skin in a map file in one process
        //   RustSkinToGlb --batch <output-dir> [map.json]
        if (args.Length >= 2 && args[0] == "--batch")
        {
            RunBatch(args[1], args.Length >= 3 ? args[2] : null);
            return;
        }

        // debug: nearest neighbour position match between two OBJs
        //   RustSkinToGlb --nn-test <srcObj> <refObj>
        if (args.Length >= 3 && args[0] == "--nn-test")
        {
            var src = ObjLoader.Load(args[1]);
            var refMesh = ObjLoader.Load(args[2]);
            if (args.Length >= 6)
            {
                float ox = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                      oy = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                      oz = float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
                refMesh = refMesh with { Positions = refMesh.Positions.Select(p => (p.x + ox, p.y + oy, p.z + oz)).ToList() };
            }
            var rng = new Random(1);
            var sample = Enumerable.Range(0, src.Positions.Count).OrderBy(_ => rng.Next()).Take(200).ToList();
            var dists = new List<float>();
            foreach (var i in sample)
            {
                var p = src.Positions[i];
                float best = float.MaxValue;
                foreach (var q in refMesh.Positions)
                {
                    float d = (p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y) + (p.z - q.z) * (p.z - q.z);
                    if (d < best) best = d;
                }
                dists.Add(MathF.Sqrt(best));
            }
            dists.Sort();
            Console.WriteLine($"n={dists.Count} min={dists[0]:F5} median={dists[dists.Count/2]:F5} p90={dists[(int)(dists.Count*0.9)]:F5} max={dists[^1]:F5}");
            return;
        }

        // weekly update (see Program.Weekly.cs)
        if (args.Length >= 1 && args[0] == "--weekly") { Environment.ExitCode = WeeklyMain(args.Skip(1).ToArray()); return; }

        // debug: skins whose _BumpMap is a plain RGB normal map and not the DXT5nm layout
        if (args.Length >= 1 && args[0] == "--scan-normals") { Environment.ExitCode = ScanNormalsMain(args.Skip(1).ToArray()); return; }

        // debug: how much of each opaque-mode main texture is fully transparent (see Program.Flags.cs)
        if (args.Length >= 1 && args[0] == "--scan-alpha") { Environment.ExitCode = ScanAlphaMain(args.Skip(1).ToArray()); return; }

        // only the site export (offline), writes docs/skins.json and docs/icons (see Program.Export.cs)
        if (args.Length >= 1 && args[0] == "--export-viewer") { Environment.ExitCode = ExportViewerMain(); return; }

        // debug: checks every ItemConfig against a real manifest and its OBJ files
        //   RustSkinToGlb --audit-configs <data.json>
        if (args.Length >= 2 && args[0] == "--audit-configs") { AuditConfigs(args[1]); return; }

        // debug: translation only ICP (trimmed) that finds the offset to lay <srcObj> on top of <refObj>
        //   RustSkinToGlb --icp-offset <srcObj> <refObj>
        if (args.Length >= 3 && args[0] == "--icp-offset")
        {
            var a = ObjLoader.Load(args[1]).Positions;
            var b = ObjLoader.Load(args[2]).Positions;
            var rng2 = new Random(7);
            var pts = a.OrderBy(_ => rng2.Next()).Take(600).ToList();
            float ox = 0, oy = 0, oz = 0;
            for (int iter = 0; iter < 400; iter++)
            {
                var deltas = new List<(float d, float dx, float dy, float dz)>();
                foreach (var p0 in pts)
                {
                    float px = p0.x + ox, py = p0.y + oy, pz = p0.z + oz, best = float.MaxValue, bx = 0, by = 0, bz = 0;
                    foreach (var q in b)
                    {
                        float d2 = (px - q.x) * (px - q.x) + (py - q.y) * (py - q.y) + (pz - q.z) * (pz - q.z);
                        if (d2 < best) { best = d2; bx = q.x - px; by = q.y - py; bz = q.z - pz; }
                    }
                    deltas.Add((MathF.Sqrt(best), bx, by, bz));
                }
                var keep = deltas.OrderBy(x => x.d).Take(deltas.Count / 2).ToList();
                ox += keep.Average(x => x.dx); oy += keep.Average(x => x.dy); oz += keep.Average(x => x.dz);
                if (iter % 50 == 49 || iter == 399)
                    Console.WriteLine(FormattableString.Invariant($"iter {iter + 1}: offset=({ox:F5},{oy:F5},{oz:F5}) median={keep[keep.Count / 2].d:F5} best10%={keep[keep.Count / 10].d:F6}"));
            }
            return;
        }
        // debug: copies face groups from a correctly grouped OBJ onto a differently exported one with the same
        // geometry (after a fixed offset), nearest vertex lookup. Writes one OBJ per group with the full v/vt/vn table.
        //   RustSkinToGlb --transfer-groups <srcObj> <refObj> <ox> <oy> <oz> <outDir>
        if (args.Length >= 7 && args[0] == "--transfer-groups")
        {
            TransferGroups(args[1], args[2],
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture),
                args[6]);
            return;
        }

        // old raw Unity workshop skins -> the modern manifest + PNG layout
        //   RustSkinToGlb --import-legacy <steamcmd content dir> <dest skins dir> <data.json> [report.tsv]
        if (args.Length >= 4 && args[0] == "--import-legacy")
        {
            LegacyImporter.Run(args[1], args[2], args[3], args.Length >= 5 ? args[4] : "legacy-import-report.tsv");
            return;
        }

        if (args.Length < 3)
        {
            Console.WriteLine("Usage: RustSkinToGlb <skin-name> <output.glb> <workshop-id>");
            Console.WriteLine("       RustSkinToGlb --batch <output-dir> [map.json]");
            return;
        }

        BuildSkin(args[0], args[1], args[2], verbose: true);
    }

    // checks every ItemConfig against a real manifest and its OBJ files
    private static void AuditConfigs(string dataJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(dataJsonPath));
        string skinsRoot = DefaultSkinsRoot;
        var firstSkinPerType = new Dictionary<string, string>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string id = e.GetProperty("id").GetString()!;
            string type = e.GetProperty("itemType").GetString() ?? "";
            if (type.Length == 0 || firstSkinPerType.ContainsKey(type)) continue;
            if (File.Exists(Path.Combine(skinsRoot, id, "manifest", "manifest.txt"))) firstSkinPerType[type] = id;
        }
        Console.WriteLine("ItemType\tskin\tmanifestGroups\tgroupsUsed\tunusedGroups\tmeshes\tflags");
        foreach (var (type, id) in firstSkinPerType.OrderBy(k => k.Key))
        {
            var (_, groups) = ParseManifest(Path.Combine(skinsRoot, id, "manifest", "manifest.txt"));
            if (!ItemConfigs.TryGetValue(type, out var cfg)) { Console.WriteLine($"{type}\t{id}\t{groups.Count}\t-\t-\t-\tNO_CONFIG"); continue; }
            var used = new SortedSet<int>(cfg.Meshes.Select(m => m.GroupIndex));
            var unused = Enumerable.Range(0, groups.Count).Where(g => !used.Contains(g)).ToList();
            var flags = new List<string>();
            var desc = new List<string>();
            foreach (var m in cfg.Meshes)
            {
                string dir = m.ObjDirOverride ?? cfg.ObjDir;
                string path = Path.Combine(dir, m.ObjFileName);
                if (!File.Exists(path)) { desc.Add($"{m.ObjFileName}(MISSING)"); flags.Add("MISSING_OBJ"); continue; }
                var lines = File.ReadLines(path).Select(l => l.Trim()).ToList();
                int mtl = lines.Where(l => l.StartsWith("usemtl ")).Distinct().Count();
                int grp = lines.Where(l => l.StartsWith("g ")).Distinct().Count();
                int objs = lines.Count(l => l.StartsWith("o "));
                bool split = m.GeometryGroup != null || m.ZRange != null || m.IslandRank != -1 || cfg.FirstObjectOnly || cfg.ObjectIndex >= 0;
                desc.Add($"{m.ObjFileName}->g{m.GroupIndex}[mtl={mtl},g={grp},o={objs}{(split ? ",split" : "")}]");
                if ((mtl >= 2 || grp >= 2) && !split) flags.Add("UNSPLIT_MULTI_MATERIAL");
            }
            if (unused.Count > 0 && groups.Count > 1) flags.Add("UNUSED_GROUPS");
            if (groups.Count > 1 && used.Count == 1) flags.Add("ONE_GROUP_FOR_MULTI");
            Console.WriteLine($"{type}\t{id}\t{groups.Count}\t{string.Join(",", used)}\t{string.Join(",", unused)}\t{string.Join(" | ", desc)}\t{string.Join(",", flags.Distinct())}");
        }
    }
    private static void TransferGroups(string srcPath, string refPath, float ox, float oy, float oz, string outDir)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float F(string s) => float.Parse(s, inv);
        (int, int, int) RoundKey(float x, float y, float z) => ((int)MathF.Round(x * 2000), (int)MathF.Round(y * 2000), (int)MathF.Round(z * 2000));

        // pass 1: read the positions and the group of every face in the reference OBJ, build position -> group
        var refPositions = new List<(float x, float y, float z)>();
        var posGroup = new Dictionary<(int, int, int), string>();
        string curGroup = "";
        foreach (var raw in File.ReadLines(refPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "v")
                refPositions.Add((F(parts[1]), F(parts[2]), F(parts[3])));
            else if (parts[0] == "g")
                curGroup = parts.Length > 1 ? parts[1] : "";
            else if (parts[0] == "f")
            {
                for (int i = 1; i < parts.Length; i++)
                {
                    int vi = int.Parse(parts[i].Split('/')[0], inv);
                    vi = vi > 0 ? vi - 1 : refPositions.Count + vi;
                    var p = refPositions[vi];
                    posGroup[RoundKey(p.x + ox, p.y + oy, p.z + oz)] = curGroup;
                }
            }
        }
        Console.WriteLine($"Reference: {refPositions.Count} positions, {posGroup.Count} grouped keys.");

        // pass 2: read the source OBJ, each face gets the group most of its vertices matched
        // in the reference (a vertex without a match doesn't vote)
        var srcPositions = new List<(float x, float y, float z)>();
        var srcLines = File.ReadAllLines(srcPath);
        string? Classify(string fLine)
        {
            var parts = fLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var votes = new Dictionary<string, int>();
            for (int i = 1; i < parts.Length; i++)
            {
                int vi = int.Parse(parts[i].Split('/')[0], inv);
                vi = vi > 0 ? vi - 1 : srcPositions.Count + vi;
                var p = srcPositions[vi];
                if (posGroup.TryGetValue(RoundKey(p.x, p.y, p.z), out var g))
                    votes[g] = votes.GetValueOrDefault(g) + 1;
            }
            return votes.Count == 0 ? null : votes.OrderByDescending(kv => kv.Value).First().Key;
        }

        var byGroup = new Dictionary<string, List<string>>();
        int unmatched = 0;
        foreach (var raw in srcLines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "v")
                srcPositions.Add((F(parts[1]), F(parts[2]), F(parts[3])));
            else if (parts[0] == "f")
            {
                var g = Classify(line);
                if (g is null) { unmatched++; continue; }
                if (!byGroup.TryGetValue(g, out var list)) byGroup[g] = list = new List<string>();
                list.Add(line);
            }
        }
        Console.WriteLine($"Source: {srcPositions.Count} positions. Faces by group: " +
            string.Join(", ", byGroup.Select(kv => $"{kv.Key}={kv.Value.Count}")) + $", unmatched={unmatched}");

        Directory.CreateDirectory(outDir);
        var header = srcLines.Where(l => { var t = l.Trim(); return t.StartsWith("v ") || t.StartsWith("vt ") || t.StartsWith("vn "); }).ToList();
        foreach (var (group, faces) in byGroup)
        {
            var outPath = Path.Combine(outDir, $"m249_saw_{group}.obj");
            File.WriteAllLines(outPath, header.Concat(faces));
            Console.WriteLine($"  wrote {outPath} ({faces.Count} faces)");
        }
    }

    // builds every skin in a map file (data.json by default), GLBs that already exist are skipped
    // failures go to a log file, progress is printed as it goes
    private static void RunBatch(string outDir, string? mapPath, string? failLogPath = null)
    {
        mapPath ??= Path.Combine(PipelineRoot, "data", "data.json");
        Directory.CreateDirectory(outDir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mapPath));
        // data.json has a "buildable" flag, older map files don't, so no flag counts as buildable
        var entries = doc.RootElement.EnumerateArray()
            .Where(x => !x.TryGetProperty("buildable", out var b) || b.ValueKind != System.Text.Json.JsonValueKind.False)
            .ToList();
        int ok = 0, fail = 0, skipped = 0, n = 0;
        var failLog = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // clean up what an interrupted run left behind (temp files and empty files). GLBs are written to
        // <id>.glb.tmp and only renamed when they're done, so a .glb that looks finished never is a partial one.
        // Don't delete the newest .glb here, an earlier version did that and threw away a good file on every small rebuild.
        foreach (var tmp in Directory.GetFiles(outDir, "*.tmp")) File.Delete(tmp);
        foreach (var z in Directory.GetFiles(outDir, "*.glb").Where(f => new FileInfo(f).Length == 0)) File.Delete(z);

        int threads = Math.Clamp(Environment.ProcessorCount - 4, 2, 12);
        Console.WriteLine($"Building {entries.Count} skins on {threads} threads...");
        var items = entries.Select(e => (Id: e.GetProperty("id").GetString()!, Type: e.GetProperty("itemType").GetString()!)).ToList();

        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = threads }, item =>
        {
            string outPath = Path.Combine(outDir, item.Id + ".glb");
            if (File.Exists(outPath)) Interlocked.Increment(ref skipped);
            else
            {
                string tmpPath = outPath + ".tmp";
                try
                {
                    BuildSkin(item.Type, tmpPath, item.Id, verbose: false);
                    File.Move(tmpPath, outPath, overwrite: true);
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref fail);
                    try { File.Delete(tmpPath); } catch { }
                    lock (failLog) failLog.Add($"{item.Id}\t{item.Type}\t{ex.GetType().Name}: {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
                }
            }
            int done = Interlocked.Increment(ref n);
            if (done % 100 == 0 || done == items.Count)
                Console.WriteLine($"[{done}/{items.Count}] ok={ok} fail={fail} skip={skipped} elapsed={sw.Elapsed:hh\\:mm\\:ss}");
        });        failLogPath ??= Path.Combine(PipelineRoot, "data", "batch-failures.tsv");   // keep the log out of the output (models) folder
        File.WriteAllLines(failLogPath, failLog);
        Console.WriteLine($"BATCH DONE: ok={ok} fail={fail} skipped={skipped} total={entries.Count} in {sw.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Failures logged to {failLogPath}");
    }

    // cloth/leather item types, never metal, so they're built fully non-metal (see "dielectric" in TextureRepacker)
    private static readonly HashSet<string> NonMetalItemTypes = new() { "Small Backpack", "Large Backpack" };

    // true when between 2% and 90% of what the mesh samples is transparent, so parts are only partly hidden.
    // Close to 100% means a blank / unused texture and that's left alone.
    private static bool TransparentPartsHidden(string mainTexPath, RawMesh mesh)
    {
        double f = TextureRepacker.MeshTransparentFraction(mainTexPath, mesh.Uvs, mesh.Indices);
        return f >= 0.02 && f <= 0.90;
    }

    // builds one skin's GLB, throws if it fails (the caller decides what to do about it)
    public static void BuildSkin(string skinName, string outputPath, string workshopId, bool verbose)
    {
        var parts = BuildCommunitySkinPartList(workshopId);
        bool dielectric = NonMetalItemTypes.Contains(
            ParseManifest(Path.Combine(SkinsRoot, workshopId, "manifest", "manifest.txt")).ItemType);

        if (verbose) Console.WriteLine($"Building GLB for skin '{skinName}' with {parts.Count} part(s)...");

        var scene = new SceneBuilder(skinName);
        string workDir = Path.Combine(Path.GetTempPath(), "RustSkinToGlb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (var part in parts)
            {
                if (TextureRepacker.IsBlankMainTexture(part.MainTexPath, part.Cutoff))
                {
                    if (verbose) Console.WriteLine($"  - {part.PartName}: MainTex is blank (no skin override for this part) - skipping.");
                    continue;
                }

                if (verbose) Console.WriteLine($"  - {part.PartName}: loading mesh...");
                var rawMesh = ObjLoader.Load(part.ObjPath, part.FirstObjectOnly, part.ObjectIndex, part.GeometryGroup);
                if (part.IslandRank == 0) rawMesh = rawMesh.ExtractByIslandRank(0);
                else if (part.IslandRank == -2) rawMesh = rawMesh.ExtractByIslandRank(0, invert: true);
                if (part.StripIslandsFraction > 0f)
                    rawMesh = rawMesh.StripDistantIslands(part.StripIslandsFraction);
                if (part.StripSmallIslandsFraction > 0f)
                    rawMesh = rawMesh.StripSmallIslands(part.StripSmallIslandsFraction);
                if (part.StripSmallestIsland)
                    rawMesh = rawMesh.StripSmallestIsland();
                if (part.StripSideIslandsMaxAbsX is float maxAbsX)
                    rawMesh = rawMesh.StripIslandsByXOffset(maxAbsX);
                if (part.ZRange is { } zRange)
                    rawMesh = rawMesh.FilterByZRange(zRange.minZ, zRange.maxZ);
                if (verbose) Console.WriteLine($"    {rawMesh.Positions.Count} verts, {rawMesh.Indices.Count / 3} tris");

                if (verbose) Console.WriteLine($"    building material...");
                string ormPath = Path.Combine(workDir, $"{part.PartName}_orm.png");
                string baseColorPath = Path.Combine(workDir, $"{part.PartName}_basecolor.png");
                var result = TextureRepacker.BuildOrmAndBaseColor(
                    part.MainTexPath, part.SpecGlossMapPath, part.OcclusionMapPath,
                    glossinessScale: part.Glossiness, occlusionStrength: part.OcclusionStrength,
                    metallicGlossPath: part.MetallicGlossMapPath,
                    dielectric: dielectric);
                using (result.Orm)
                using (result.BaseColor)
                {
                    result.Orm.SaveAsPng(ormPath);
                    result.BaseColor.SaveAsPng(baseColorPath);
                }

                var material = new MaterialBuilder(part.PartName)
                    .WithMetallicRoughnessShader()
                    .WithDoubleSide(true)
                    .WithChannelImage(KnownChannel.BaseColor, baseColorPath)
                    .WithChannelImage(KnownChannel.MetallicRoughness, ormPath)
                    .WithChannelImage(KnownChannel.Occlusion, ormPath);

                // A manifest _Cutoff of exactly 0 or 1 seems to mean "no cutout" (every AK47 group has one),
                // real masking only showed up at in-between values (0.5 on a hoodie whose transparent design
                // is in the MainTex alpha, forcing opaque made it a solid white patch).
                if (part.GlassAlpha is not null)
                    material.WithAlpha(AlphaMode.BLEND);
                else if (part.Cutoff > 0f && part.Cutoff < 1f)
                    material.WithAlpha(AlphaMode.MASK, part.Cutoff);
                else if (part.Cutoff >= 1f && TransparentPartsHidden(part.MainTexPath, rawMesh))
                    // _Cutoff is 1 but the skin hides parts of the mesh with alpha 0 and junk colour underneath (the bright
                    // green ropes on the Shark Attack backpack). Only cut the near zero alpha, nothing in the middle.
                    material.WithAlpha(AlphaMode.MASK, 8f / 255f);
                else
                    material.WithAlpha(AlphaMode.OPAQUE);

                if (part.EmissionMapPath is not null)
                {
                    // WithChannelImage on its own leaves the emissive factor at (0,0,0), which turns the emissive
                    // texture black so nothing glows. WithEmissive sets the texture and a white factor.
                    material.WithEmissive(part.EmissionMapPath, part.EmissionColor ?? new System.Numerics.Vector3(1f, 1f, 1f));
                }

                if (part.BumpMapPath is not null)
                {
                    string normalPath = Path.Combine(workDir, $"{part.PartName}_normal.png");
                    using (var normal = TextureRepacker.UnpackUnityNormalMap(part.BumpMapPath, part.BumpScale))
                    {
                        normal.SaveAsPng(normalPath);
                    }
                    material.WithChannelImage(KnownChannel.Normal, normalPath);
                }

                var meshBuilder = BuildMesh(part.PartName, rawMesh, material);

                // AffineTransform is (scale, rotation, translation): ScaleFactor goes in the scale slot and
                // RotX/RotY/RotZ (degrees) in the rotation slot, that's how items get stood up and turned
                System.Numerics.Vector3? scaleV = part.ScaleFactor != 1f
                    ? new System.Numerics.Vector3(part.ScaleFactor) : null;
                const float deg = MathF.PI / 180f;
                // not CreateFromYawPitchRoll, it gimbal locks at 90 pitch and RotY/RotZ did the same thing
                // on the acoustic guitar. Single axis rotations multiplied together are fine
                System.Numerics.Quaternion? rotQ = (part.RotX != 0f || part.RotY != 0f || part.RotZ != 0f)
                    ? System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, part.RotZ * deg)
                      * System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, part.RotY * deg)
                      * System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, part.RotX * deg)
                    : null;
                var transform = (scaleV.HasValue || rotQ.HasValue)
                    ? new SharpGLTF.Transforms.AffineTransform(scaleV, rotQ, null)
                    : SharpGLTF.Transforms.AffineTransform.Identity;
                scene.AddRigidMesh(meshBuilder, transform);
                if (verbose) Console.WriteLine($"    done.");
            }

            if (verbose) Console.WriteLine("Writing GLB...");
            var model = scene.ToGltf2();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            model.SaveGLB(outputPath);
            if (verbose) Console.WriteLine($"Wrote {outputPath}");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildMesh(
        string name, RawMesh raw, MaterialBuilder material)
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(name);
        var prim = mesh.UsePrimitive(material);

        bool hasNormals = raw.HasNormals;

        // build the vertex list (flat normals get worked out below if the OBJ has none, rare for these files)
        var verts = new (System.Numerics.Vector3 Pos, System.Numerics.Vector3 Nrm, System.Numerics.Vector2 Uv)[raw.Positions.Count];
        for (int i = 0; i < raw.Positions.Count; i++)
        {
            var p = raw.Positions[i];
            var n = raw.Normals[i];
            var uv = raw.Uvs[i];
            verts[i] = (
                new System.Numerics.Vector3(p.x, p.y, p.z),
                new System.Numerics.Vector3(n.x, n.y, n.z),
                // glTF has V the other way round from OBJ (OBJ starts bottom left), so flip it
                new System.Numerics.Vector2(uv.u, 1f - uv.v));
        }

        for (int i = 0; i < raw.Indices.Count; i += 3)
        {
            int ia = raw.Indices[i];
            int ib = raw.Indices[i + 1];
            int ic = raw.Indices[i + 2];

            var a = verts[ia];
            var b = verts[ib];
            var c = verts[ic];

            if (!hasNormals)
            {
                var flat = System.Numerics.Vector3.Normalize(
                    System.Numerics.Vector3.Cross(b.Pos - a.Pos, c.Pos - a.Pos));
                a.Nrm = b.Nrm = c.Nrm = flat;
            }

            var va = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                new VertexPositionNormal(a.Pos, a.Nrm), new VertexTexture1(a.Uv));
            var vb = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                new VertexPositionNormal(b.Pos, b.Nrm), new VertexTexture1(b.Uv));
            var vc = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                new VertexPositionNormal(c.Pos, c.Nrm), new VertexTexture1(c.Uv));

            prim.AddTriangle(va, vb, vc);
        }

        return mesh;
    }

    // one mesh of an item and which manifest group gives it its textures. Weapons are 1:1, clothing isn't
    // (the hoodie has 2 meshes but 1 group, both use it).
    // IslandRank: -1 whole mesh, 0 only the biggest island, -2 everything but the biggest (riot helmet)
    private sealed record MeshGroupMapping(string PartName, string ObjFileName, int GroupIndex, int IslandRank = -1,
        // GeometryGroup: only load the faces of this OBJ `g <name>` (for meshes that come with submesh groups,
        // the prefab M249 has receiver/barrel as m249_saw_0 / m249_saw_1).
        // ObjDirOverride: full path of the folder to load ObjFileName from, if it isn't in the item's WorkshopSource.
        string? GeometryGroup = null, string? ObjDirOverride = null,
        // rough split (see ObjLoader.FilterByZRange) for a merged mesh with no usable group or island split,
        // (minZ, maxZ) where a null bound means open on that side
        (float? minZ, float? maxZ)? ZRange = null,
        // render this mesh as see-through glass with this opacity instead of a skin texture
        float? GlassAlpha = null,
        // real glass texture from the game to use instead of a plain tint, the alpha is set to GlassAlpha
        string? GlassTexture = null);

    private sealed record ItemConfig(string ObjDir, IReadOnlyList<MeshGroupMapping> Meshes, float StripIslandsFraction = 0f, float ScaleFactor = 1f, bool FirstObjectOnly = false, float StripSmallIslandsFraction = 0f, int ObjectIndex = -1, bool StripSmallestIsland = false, float? StripSideIslandsMaxAbsX = null,
        // rotation in degrees around the model origin so every item stands upright and faces the camera.
        // RotY 90 turns weapons from end-on to side view and fixes sideways double doors,
        // RotX 90 stands up items that lie flat (guitar, wheel).
        float RotX = 0f, float RotY = 0f, float RotZ = 0f);

    private const string WorkshopSourceRoot =
        @"C:\Program Files (x86)\Steam\steamapps\common\Rust\RustClient_Data\StreamingAssets\WorkshopSource";

    // Keyed by the manifest ItemType. Each one was checked against the item's real .skinnable
    // MonoBehaviour (exported with AssetStudio) instead of guessed.
    private static readonly Dictionary<string, ItemConfig> ItemConfigs = new()
    {
        ["AK47"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "rifle.ak"),
            new[]
            {
                new MeshGroupMapping("barrel", "ak47_Barrel_LOD0.obj", 0),
                new MeshGroupMapping("maggrip", "ak47_MagGrip_LOD0.obj", 1),
                new MeshGroupMapping("body", "ak47_Body_LOD0.obj", 2),
                new MeshGroupMapping("stock", "ak47_stock_LOD0.obj", 3),
            }),
        ["Hoodie"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "hoodie"),
            new[]
            {
                new MeshGroupMapping("torso", "player_urban_torso_LOD0.obj", 0),
                new MeshGroupMapping("tshirt", "Tshirt_LOD0.obj", 0),
            }),

        // single group item types, every mesh uses the manifest's one group.
        // Two oddities: the SKS .skinnable points at a "rifle.sks" folder that doesn't exist (it's "sks"),
        // and the Tactical Gloves paths use backslashes.
["Acoustic Guitar"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "fun.guitar"),
    new[]
    {
        new MeshGroupMapping("guitaracoustic", "GuitarAcoustic.obj", 0),
    }),
["Armored Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.hinged.toptier"),
    new[]
    {
        new MeshGroupMapping("wall_doorway_door_hatch", "wall.doorway.door.hatch.obj", 0),
        new MeshGroupMapping("wall_doorway_door", "wall.doorway.door_LOD0.obj", 0),
    }),
["Armored Double Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.double.hinged.toptier"),
    new[]
    {
        new MeshGroupMapping("doubledoor_toptier_hatch_l", "doubledoor.toptier.hatch_L.obj", 0),
        new MeshGroupMapping("doubledoor_toptier_hatch_r", "doubledoor.toptier.hatch_R.obj", 0),
        new MeshGroupMapping("doubledoor_toptier_l", "doubledoor.toptier_L_LOD0.obj", 0),
        new MeshGroupMapping("doubledoor_toptier_r", "doubledoor.toptier_R_LOD0.obj", 0),
    }),
// Item types added 2026-09-21 (new in the Steam item catalog, the meshes came with a game update).
// Each is one OBJ with one manifest texture group (checked on every local skin of the type).
["Small Backpack"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "small.backpack"),
    new[]
    {
        new MeshGroupMapping("small_backpack", "small_backpack_LOD0.obj", 0),
    }),
["Wood Armor Jacket"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "jacket.woodarmor"),
    new[]
    {
        new MeshGroupMapping("woodarmor_jacket", "jacket.woodarmor.obj", 0),
    }),
["Wood Armor Pants"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pants.woodarmor"),
    new[]
    {
        new MeshGroupMapping("woodarmor_pants", "pants.woodarmor.obj", 0),
    }),
["Wood Armor Helmet"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hat.woodarmor"),
    new[]
    {
        new MeshGroupMapping("woodarmor_helmet", "hat.woodarmor.obj", 0),
    }),
["Wood Armor Gloves"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "gloves.woodarmor"),
    new[]
    {
        new MeshGroupMapping("woodarmor_gloves", "gloves.woodarmor.obj", 0),
    }),
// Item types added 2026-07-22 (skins taken from the Steam workshop content folder)
["Auto Turret"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "auto.turret"),
    new[]
    {
        new MeshGroupMapping("auto_turret", "auto_turret.obj", 0),
    }),
// Metal Shop Front: one merged OBJ but 2 texture groups, split up below by its usemtl materials.
["Metal Shop Front"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "wall.frame.shopfront"),
    new[]
    {
        // 3 usemtl materials: front (527 faces, manifest group 1), back (787 faces, group 0) and
        // glass (2 faces, no skin texture, the pane is transparent in the game)
        new MeshGroupMapping("shopfront_front", "metal_shopfront_workshop.obj", 1, GeometryGroup: "metal_shopfront_front"),
        new MeshGroupMapping("shopfront_back", "metal_shopfront_workshop.obj", 0, GeometryGroup: "metal_shopfront_back"),
        new MeshGroupMapping("shopfront_glass", "metal_shopfront_workshop.obj", 0, GeometryGroup: "metal_shopfront_glass", GlassAlpha: 0.6f,
            // game texture content/textures/generic/glass_reinforced (wire mesh glass), same as the pane in the game
            GlassTexture: Path.Combine(GameMeshRefs, "shopfront.glass", "glass_reinforced_bc.png")),
    }),
["Balaclava"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "mask.balaclava"),
    new[]
    {
        new MeshGroupMapping("balaclava", "balaclava_LOD0.obj", 0),
    }),
["Bandana"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "mask.bandana"),
    new[]
    {
        new MeshGroupMapping("bandana", "bandana_LOD0.obj", 0),
    }),
["Bearskin Rug"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rug.bear"),
    new[]
    {
        new MeshGroupMapping("rug_bear_a", "rug_bear_a_LOD0.obj", 0),
    }),
["Barbeque"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "bbq"),
    new[]
    {
        new MeshGroupMapping("bbq_workshop", "BBQ_workshop.obj", 0),
    }),
["Bed"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "bed"),
    new[]
    {
        new MeshGroupMapping("bed_workshop", "bed_workshop.obj", 0),
    }, ScaleFactor: 0.01f),  // the OBJ is in cm (bounding box about 237 x 148 x 92), scale it down to meters
["Bolt Rifle"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rifle.bolt"),
    new[]
    {
        new MeshGroupMapping("bolt_rifle_mesh", "bolt_rifle_mesh.obj", 0),
    // bolt_rifle_mesh.obj has two bullets floating next to the gun, all at X around 0.174, while the gun
    // itself stays within about 0.02 of the centre line. See StripIslandsByXOffset
    // (the RS_DEBUG_ISLANDS=1 island dump shows the numbers).
    }, StripSideIslandsMaxAbsX: 0.1f),
["Beenie Hat"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hat.beenie"),
    new[]
    {
        new MeshGroupMapping("player_urban_hat", "player_urban_hat_LOD0.obj", 0),
    }),
["Bone Club"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "bone.club"),
    new[]
    {
        new MeshGroupMapping("boneclub_mesh", "boneclub_mesh.obj", 0),
    }),
["Bone Knife"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "knife.bone"),
    new[]
    {
        new MeshGroupMapping("boneknife_mesh", "boneknife_mesh.obj", 0),
    }),
["Boonie Hat"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hat.boonie"),
    new[]
    {
        new MeshGroupMapping("hat_boonie", "hat.boonie_LOD0.obj", 0),
    }),
["Bucket Helmet"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "bucket.helmet"),
    new[]
    {
        new MeshGroupMapping("metal_improvised_helmet_02", "metal_improvised_helmet_02_LOD0.obj", 0),
    }),
["Burlap Headwrap"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "burlap.headwrap"),
    new[]
    {
        new MeshGroupMapping("headwraps", "headwraps_LOD0.obj", 0),
    }),
["Burlap Pants"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "burlap.trousers"),
    new[]
    {
        new MeshGroupMapping("burlap_trousers", "burlap_trousers_LOD0.obj", 0),
    }),
["Burlap Shirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "burlap.shirt"),
    new[]
    {
        new MeshGroupMapping("burlap_shirt", "burlap_shirt_LOD0.obj", 0),
    }),
["Burlap Shoes"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "burlap.shoes"),
    new[]
    {
        new MeshGroupMapping("burlap_footwrap", "burlap_footwrap_LOD0.obj", 0),
    }),
["Cap"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hat.cap"),
    new[]
    {
        new MeshGroupMapping("hat_cap", "hat_cap_LOD0.obj", 0),
    }),
["Chair"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "chair"),
    new[]
    {
        new MeshGroupMapping("chair", "chair_LOD0.obj", 0),
    }),
["Collared Shirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "shirt.collared"),
    new[]
    {
        new MeshGroupMapping("shirt", "shirt_LOD0.obj", 0),
        new MeshGroupMapping("torso_00", "Torso.00_LOD0.obj", 0),
    }),
["Coffee Can Helmet"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "coffeecan.helmet"),
    new[]
    {
        new MeshGroupMapping("metal_improvised_helmet_02", "metal_improvised_helmet_02_LOD0.obj", 0),
    }),
["Combat Knife"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "knife.combat"),
    new[]
    {
        new MeshGroupMapping("knife_v", "Knife_v.obj", 0),
    }),
["Concrete Barricade"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "barricade.concrete"),
    new[]
    {
        new MeshGroupMapping("barricade_concrete", "barricade_concrete_LOD0.obj", 0),
    }),
["Crossbow"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "crossbow"),
    new[]
    {
        // arrow_mesh.obj is left out, the bolt shows up floating away from the bow
        new MeshGroupMapping("crossbow_mesh", "crossbow_mesh.obj", 0),
    }),
["Deer Skull Mask"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "deer.skull.mask"),
    new[]
    {
        new MeshGroupMapping("bonearmour_deerskullhelmet", "BoneArmour_DeerSkullHelmet_LOD0.obj", 0),
    }),
["Electric Furnace"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "electric.furnace"),
    new[]
    {
        new MeshGroupMapping("electricfurnace_workshop", "ElectricFurnace_Workshop.obj", 0),
    }),
["F1 Grenade"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "grenade.f1"),
    new[]
    {
        new MeshGroupMapping("grenade_mesh", "grenade_MESH.obj", 0),
        new MeshGroupMapping("grenade_pin", "grenade_pin.obj", 0),
    }),
["Fridge"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "fridge"),
    new[]
    {
        new MeshGroupMapping("fridge_workshop_guide", "fridge_workshop_guide.obj", 0),
    },
    ObjectIndex: 1), // the OBJ has two objects, [0] is open and [1] is closed, takes the closed one
["Furnace"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "furnace"),
    new[]
    {
        new MeshGroupMapping("furnace_v3", "furnace_v3_LOD0.obj", 0),
    }),
["Garage Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "wall.frame.garagedoor"),
    new[]
    {
        new MeshGroupMapping("garage_door_block", "garage_door_block_LOD0.obj", 0),
    }),
["Hammer"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hammer"),
    new[]
    {
        new MeshGroupMapping("v_hammer", "v_hammer.obj", 0),
    }),
["Hide Halterneck"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.helterneck"),
    new[]
    {
        new MeshGroupMapping("halterneck", "HalterNeck_LOD0.obj", 0),
    }),
["Hatchet"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hatchet"),
    new[]
    {
        new MeshGroupMapping("axe_mesh", "axe_mesh.obj", 0),
    }),
["Hide Pants"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.pants"),
    new[]
    {
        new MeshGroupMapping("pants", "pants_LOD0.obj", 0),
    }),
["Hide Poncho"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.poncho"),
    new[]
    {
        new MeshGroupMapping("fur", "fur_LOD0.obj", 0),
        new MeshGroupMapping("poncho", "poncho_LOD0.obj", 0),
    }),
["Hide Shirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.vest"),
    new[]
    {
        new MeshGroupMapping("shirt", "shirt_LOD0.obj", 0),
    }),
["Hide Shoes"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.boots"),
    new[]
    {
        new MeshGroupMapping("boots", "boots_LOD0.obj", 0),
    }),
["Hide Skirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "attire.hide.skirt"),
    new[]
    {
        new MeshGroupMapping("skirt_lod00", "Skirt_LOD00.obj", 0),
    }),
["Hunting Bow"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "bow.hunting"),
    new[]
    {
        new MeshGroupMapping("v_bow", "v_Bow.obj", 0),
    }),
["L96"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rifle.l96"),
    new[]
    {
        new MeshGroupMapping("l96_viewmodel", "L96_viewmodel.obj", 0),
    }),
["Jackhammer"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "jackhammer"),
    new[]
    {
        new MeshGroupMapping("pneujackhammer", "PneuJackHammer_LOD0.obj", 0),
    }),
["Large Backpack"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "largebackpack"),
    new[]
    {
        new MeshGroupMapping("large_backpack", "Large_Backpack_LOD0.obj", 0),
    }),
["Large Wood Box"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "box.wooden.large"),
    new[]
    {
        new MeshGroupMapping("box_wooden_large_model", "box.wooden.large.model_LOD0.obj", 0),
    }),
["Locker"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "locker"),
    new[]
    {
        new MeshGroupMapping("locker_workshop_guide", "locker_workshop_guide.obj", 0),
    }, ScaleFactor: 0.01f),
["Leather Gloves"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "burlap.gloves"),
    new[]
    {
        new MeshGroupMapping("burlap_gloves", "burlap_gloves_LOD0.obj", 0),
    }),
["Long TShirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "tshirt.long"),
    new[]
    {
        new MeshGroupMapping("tshirt", "tshirt_LOD0.obj", 0),
    }),
["Longsword"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "longsword"),
    new[]
    {
        new MeshGroupMapping("two_hand_sword_mesh", "two_hand_sword_mesh.obj", 0),
    }),
["M39"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rifle.m39"),
    new[]
    {
        new MeshGroupMapping("v_m39emr", "v_M39EMR.obj", 0),
        new MeshGroupMapping("v_m39emr_sights", "v_M39EMR_sights.obj", 0),
    }),
["Metal Chest Plate"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "metal.plate.torso"),
    new[]
    {
        new MeshGroupMapping("frontplate", "frontplate_LOD0.obj", 0),
    }),
["Metal Facemask"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "metal.facemask"),
    new[]
    {
        new MeshGroupMapping("leather", "leather_LOD0.obj", 0),
        new MeshGroupMapping("metal", "metal_LOD0.obj", 0),
    }),
["Mp5"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "smg.mp5"),
    new[]
    {
        new MeshGroupMapping("mp5_mesh", "mp5_mesh.obj", 0),
    }),
["Miner Hat"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "hat.miner"),
    new[]
    {
        new MeshGroupMapping("minerhat", "minerhat_LOD0.obj", 0),
    }),
["Pants"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pants"),
    new[]
    {
        new MeshGroupMapping("player_urban_legs", "player_urban_legs_LOD0.obj", 0),
    }),
["Pick Axe"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pickaxe"),
    new[]
    {
        new MeshGroupMapping("v_pickaxe_mesh", "v_pickaxe_mesh.obj", 0),
    }),
["Pump Shotgun"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "shotgun.pump"),
    new[]
    {
        // the workshop obj has no groups and the shell can't be found by island size or distance.
        // the prefab export has them: _0 is the body (9031 faces), _1 the shell (116 faces). Uses group 0 only
        new MeshGroupMapping("sawnoffshotgun_mesh", "sawnoffshotgun_mesh.obj", 0, GeometryGroup: "sawnoffshotgun_mesh_0",
            ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.pump")),
    }),
["Python"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pistol.python"),
    new[]
    {
        new MeshGroupMapping("python", "python.obj", 0),
    },
    StripIslandsFraction: 3.0f), // the speed loader crane floats away from the body
["Reactive Target"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "target.reactive"),
    new[]
    {
        new MeshGroupMapping("reactivetarget", "reactiveTarget_LOD0.obj", 0),
    }),
["Revolver"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pistol.revolver"),
    new[]
    {
        new MeshGroupMapping("v_revolver_mesh", "v_revolver_mesh.obj", 0),
    }),
["Roadsign Gloves"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "roadsign.gloves"),
    new[]
    {
        new MeshGroupMapping("roadsigngloves", "RoadsignGloves_LOD0.obj", 0),
    }),
["Rock"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rock"),
    new[]
    {
        new MeshGroupMapping("w_rock", "w_rock_LOD0.obj", 0),
    }),
["Rocket Launcher"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rocket.launcher"),
    new[]
    {
        new MeshGroupMapping("rocket_launcher", "rocket_launcher.obj", 0),
    }),
["Rug"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "rug"),
    new[]
    {
        new MeshGroupMapping("rug", "rug_LOD0.obj", 0),
    }),
["Salvaged Axe"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "axe.salvaged"),
    new[]
    {
        new MeshGroupMapping("axe_salvaged", "Axe.Salvaged.obj", 0),
    }),
["Sandbag Barricade"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "barricade.sandbags"),
    new[]
    {
        new MeshGroupMapping("barricade_sandbags", "barricade_sandbags_LOD0.obj", 0),
    }),
["Salvaged Icepick"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "icepick.salvaged"),
    new[]
    {
        new MeshGroupMapping("icepick_mesh", "icepick_mesh.obj", 0),
    }),
["Semi-Automatic Pistol"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pistol.semiauto"),
    new[]
    {
        new MeshGroupMapping("semi_pistol_mesh", "semi_pistol_mesh.obj", 0),
    }),
["Sheet Metal Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.hinged.metal"),
    new[]
    {
        new MeshGroupMapping("wall_doorway_door", "wall.doorway.door_LOD0.obj", 0),
    }),
["Sheet Metal Double Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.double.hinged.metal"),
    new[]
    {
        new MeshGroupMapping("doubledoor_metal_l", "doubledoor.metal_L_LOD0.obj", 0),
        new MeshGroupMapping("doubledoor_metal_r", "doubledoor.metal_R_LOD0.obj", 0),
    }),
["Shorts"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "pants.shorts"),
    new[]
    {
        new MeshGroupMapping("shorts", "shorts_LOD0.obj", 0),
    }),
["Sleeping Bag"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "sleepingbag"),
    new[]
    {
        new MeshGroupMapping("fur", "fur_LOD0.obj", 0),
        new MeshGroupMapping("sleepingbag_leather", "sleepingbag_leather_LOD0.obj", 0),
    }),
["Snow Jacket"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "jacket.snow"),
    new[]
    {
        new MeshGroupMapping("jacket_snow", "jacket_snow_LOD0.obj", 0),
    }),
["Spinning Wheel"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "spinner.wheel"),
    new[]
    {
        new MeshGroupMapping("spinner_wheel_guide", "spinner_wheel_guide.obj", 0),
    }),
["Stone Pick Axe"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "stone.pickaxe"),
    new[]
    {
        new MeshGroupMapping("stone_pickaxe", "stone_pickaxe_LOD0.obj", 0),
    }),
["Stone Hatchet"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "stonehatchet"),
    new[]
    {
        new MeshGroupMapping("hatchet", "hatchet_LOD0.obj", 0),
    }),
["Sword"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "salvaged.sword"),
    new[]
    {
        new MeshGroupMapping("sword_mesh", "sword_mesh.obj", 0),
    }),
["TShirt"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "tshirt"),
    new[]
    {
        new MeshGroupMapping("player_basic_shirt_lod00", "player_basic_shirt_LOD00.obj", 0),
    }),
["Table"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "table"),
    new[]
    {
        new MeshGroupMapping("table", "table_LOD0.obj", 0),
    }),
["Tactical Gloves"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "tactical.gloves"),
    new[]
    {
        new MeshGroupMapping("tactical_gloves", "tactical_gloves_LOD0.obj", 0),
    }),
["Tank Top"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "shirt.tanktop"),
    new[]
    {
        new MeshGroupMapping("tanktop", "tanktop_LOD0.obj", 0),
    }),
["Thompson"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "smg.thompson"),
    new[]
    {
        new MeshGroupMapping("thompson_mesh1", "thompson_Mesh1.obj", 0),
    }),
["Vending Machine"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "vending.machine"),
    new[]
    {
        new MeshGroupMapping("vendingmachine", "vendingmachine_LOD0.obj", 0),
        // vm_screen_LOD0.obj is left out, it's a flat reference panel the artist used (the UVs cover the whole
        // 0-1 range, so the entire texture as a decal) and not real game geometry. With it in, a second copy of
        // the texture floats in front of the vending machine.
    }),
["Vagabond Jacket"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "jacket"),
    new[]
    {
        new MeshGroupMapping("jacket", "jacket_LOD0.obj", 0),
    }),
["Water Purifier"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "water.purifier"),
    new[]
    {
        new MeshGroupMapping("water_desalinator", "water_desalinator_LOD0.obj", 0),
    }),
["Wood Storage Box"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "box.wooden"),
    new[]
    {
        new MeshGroupMapping("w_storage_box_1", "w_storage_box_1_LOD0.obj", 0),
    }),
["Waterpipe Shotgun"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "shotgun.waterpipe"),
    new[]
    {
        new MeshGroupMapping("v_waterpipe_shotgun_mesh", "v_waterpipe_shotgun_mesh.obj", 0),
        // v_waterpipe_shotgun_shell.obj is left out, the shell floats away from the gun
    }),
["Wooden Double Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.double.hinged.wood"),
    new[]
    {
        new MeshGroupMapping("doubledoor_wood_l", "doubledoor.wood_L_LOD0.obj", 0),
        new MeshGroupMapping("doubledoor_wood_r", "doubledoor.wood_R_LOD0.obj", 0),
    }),
["Wooden Door"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "door.hinged.wood"),
    new[]
    {
        new MeshGroupMapping("wall_doorway_door", "wall.doorway.door_LOD0.obj", 0),
    }),
["Work Boots"] = new ItemConfig(
    Path.Combine(WorkshopSourceRoot, "shoes.boots"),
    new[]
    {
        new MeshGroupMapping("player_urban_feet", "player_urban_feet_LOD0.obj", 0),
    }),

        // multi group items. Meshes with no group (bullet.obj, shotgun_shell.obj) and groups with no mesh
        // (VISOR, GRENADE, SIGHTS) are left out, IsBlankMainTexture skips empty parts

        ["Semi-Automatic Rifle"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "rifle.semiauto"),
            new[]
            {
                new MeshGroupMapping("barrel", "barrel_LOD0.obj", 2),   // Groups: SAR_main(0), SAR_gripmag(1), SAR_barrel(2), SAR_Stock(3)
                new MeshGroupMapping("grip_mag", "grip_mag_LOD0.obj", 1),
                new MeshGroupMapping("main", "main_LOD0.obj", 0),
                new MeshGroupMapping("stock", "stock_LOD0.obj", 3),
            }),

        ["SKS"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "sks"),
            new[]
            {
                new MeshGroupMapping("sksframe", "SKSFrame_LOD0.obj", 0),
                new MeshGroupMapping("sksbarrelmag", "SKSBarrelMag_LOD0.obj", 1),
                new MeshGroupMapping("sksreceiver", "SKSReceiver_LOD0.obj", 2),
            }),

        ["Custom SMG"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "smg.2"),
            new[]
            {
                // Groups: STOCK/BARREL(0), GRIP/MAG(1), BODY(2). bullet.obj excluded (no matching group).
                new MeshGroupMapping("barrel", "barrel_LOD0.obj", 0),
                new MeshGroupMapping("stock", "stock_LOD0.obj", 0),
                new MeshGroupMapping("grip", "grip_LOD0.obj", 1),
                new MeshGroupMapping("magazine", "magazine_LOD0.obj", 1),
                new MeshGroupMapping("smg", "smg_LOD0.obj", 2),
            }),

        ["Double Barrel Shotgun"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "shotgun.double"),
            new[]
            {
                // Groups: BARREL(0), GRIP(1), MAIN(2), STOCK(3), SIGHTS(4). shotgun_shell.obj is left out.
                // the workshop obj has no groups (barrels came out dark), so the group labels of the store viewer mesh
                // (same geometry, offset 0.00108 0.06825 0.01300 from --icp-offset) were copied over with
                // --transfer-groups into GameMeshRefs/shotgun.double.transferred
                // store _4/_5 = stock/grip, _0 = barrels, _1 = receiver, _2/_3/_5 = small parts
                new MeshGroupMapping("dbs_g0", "dbs_g0.obj", 0, ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.double.transferred")),
                new MeshGroupMapping("dbs_g1", "dbs_g1.obj", 2, ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.double.transferred")),
                new MeshGroupMapping("dbs_g2", "dbs_g2.obj", 1, ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.double.transferred")),
                new MeshGroupMapping("dbs_g3", "dbs_g3.obj", 4, ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.double.transferred")),
                new MeshGroupMapping("dbs_g5", "dbs_g5.obj", 3, ObjDirOverride: Path.Combine(GameMeshRefs, "shotgun.double.transferred")),
                new MeshGroupMapping("dbs_stock", "dbs_stock_LOD0.obj", 3),
                new MeshGroupMapping("grip", "grip_LOD0.obj", 1),
            }),

        ["Eoka Pistol"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "pistol.eoka"),
            new[]
            {
                new MeshGroupMapping("eoka_pistol", "v_eoka_pistol.obj", 0),
                // v_eoka_rock.obj is left out, the flint rock is a loose prop that floats
                // far away from the gun
            }),

        ["HMLMG"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "hmlmg"),
            new[]
            {
                // Groups: HMLMG(0), HMLMG Ammo(1). The gun and sights share group 0, the bullets and ammo box group 1.
                new MeshGroupMapping("hmlmg", "v_hmLMG.obj", 0),
                new MeshGroupMapping("hmlmg_sights", "v_hmLMG_sights.obj", 0),
                // v_hmLMG_bullets.obj is left out, it shows up as small floating specks above the receiver
                // (the bounding box height goes from 0.39 to 0.176 without it). Same floating prop problem
                // as the bolt rifle, crossbow and DBS.
                new MeshGroupMapping("hmlmg_ammobox", "v_hmLMG_ammobox.obj", 1),
            }, ScaleFactor: 0.01f),  // the OBJ is in cm (bounding box about 39 x 95 x 25), scale it down to meters

        ["LR300"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "lr300.item"),
            new[]
            {
                new MeshGroupMapping("lr300", "LR300.obj", 0),
                new MeshGroupMapping("lr300_sights", "LR300_sights.obj", 1),
            }),

        ["M249"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "lmg.m249"),
            new[]
            {
                // Groups: Barrel(0), beltlink(1), receiver(2).
                // the store prefab and v_m249 have the wrong UVs and m249_saw.obj is broken around the handguard.
                // The game's world (dropped weapon) model works, w_m249/m249_LOD0.obj from Bundles/shared/content.bundle,
                // it has named groups (m249_LOD0_0 = receiver, m249_LOD0_1 = barrel) and correct UVs
                new MeshGroupMapping("m249_receiver", "m249_LOD0.obj", 2, GeometryGroup: "m249_LOD0_0",
                    ObjDirOverride: Path.Combine(GameMeshRefs, "lmg.m249.world")),
                new MeshGroupMapping("m249_barrel", "m249_LOD0.obj", 0, GeometryGroup: "m249_LOD0_1",
                    ObjDirOverride: Path.Combine(GameMeshRefs, "lmg.m249.world")),
                // The world model is stored at about 29% of the normal size (0.283 x 0.061 x 0.048 against 0.973 x 0.211
                // x 0.164 for the workshop mesh, a factor of 3.43-3.46 on all three axes), so scale it back up
                // to match the other guns. The config audit found this.
            }, ScaleFactor: 3.44f),

        ["Riot Helmet"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "riot.helmet"),
            new[]
            {
                // helmet_LOD0.obj has 9 separate islands (no usemtl): island 0 is the main shell (1246 tris),
                // island 1 the visor (294 tris) and islands 2-8 are hardware (hinges, screws, ..., 44-158 tris each).
                // Split by island rank: the biggest island gets the MAIN material, the rest gets VISOR.
                new MeshGroupMapping("helmet_main", "helmet_LOD0.obj", 0, IslandRank: 0),
                new MeshGroupMapping("helmet_visor", "helmet_LOD0.obj", 1, IslandRank: -2),
            }),

        ["Roadsign Pants"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "roadsign.kilt"),
            new[]
            {
                // Groups: MAIN(0), SECONDARY(1). "NonSkinnable" in a file name means that mesh
                // isn't reskinned, IsBlankMainTexture skips it if its texture is blank.
                new MeshGroupMapping("skirt_skinnable", "Skirt_Skinnable_LOD0.obj", 0),
                new MeshGroupMapping("skirt_nonskinnable", "Skirt_NonSkinnable_LOD0.obj", 1),
            }),

        ["Roadsign Vest"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "roadsign.jacket"),
            new[]
            {
                new MeshGroupMapping("vest_skinnable", "VestSkinnable_LOD0.obj", 0),
                new MeshGroupMapping("vest_notskinnable", "Vest_NotSkinnable_LOD0.obj", 1),
            }),

        ["Satchel Charge"] = new ItemConfig(
            Path.Combine(WorkshopSourceRoot, "explosive.satchel"),
            new[]
            {
                // Groups: MAIN(0), GRENADE(1). There's only one mesh file, GRENADE has no
                // mesh of its own so only MAIN is mapped.
                new MeshGroupMapping("beancan_grenade", "beancan_Grenade.obj", 0),
            }),
    };

    // Rotation per item type (degrees) so every skin stands upright and faces the camera in a normal
    // viewer (+Y up, looking down -Z). Worked out from renders of the front view. Items that were
    // already right (most wearables and deployables, single doors, rugs, upright melee) aren't listed.
    private static readonly Dictionary<string, (float X, float Y, float Z)> OrientationOverrides = new()
    {
        // guns: the length runs along the depth (pointing at the camera), RotY 90 gives a side view
        ["AK47"] = (0, 90, 0), ["Bolt Rifle"] = (0, 90, 0), ["Custom SMG"] = (0, 90, 0),
        ["Double Barrel Shotgun"] = (0, 90, 0), ["Eoka Pistol"] = (0, 90, 0), ["Hunting Bow"] = (0, 90, 0),
        ["L96"] = (0, 90, 0), ["LR300"] = (0, 90, 0), ["M249"] = (0, 90, 0), ["M39"] = (0, 90, 0),
        ["Mp5"] = (0, 90, 0), ["Python"] = (0, 90, 0), ["Revolver"] = (0, 90, 0),
        ["Rocket Launcher"] = (0, 90, 0), ["SKS"] = (0, 90, 0), ["Semi-Automatic Pistol"] = (0, 90, 0),
        ["Semi-Automatic Rifle"] = (0, 90, 0), ["Thompson"] = (0, 90, 0),
        ["Waterpipe Shotgun"] = (0, 90, 0), ["Pump Shotgun"] = (0, 90, 0),
        // double doors and the garage door are sideways, RotY 90 turns them to the camera
        ["Armored Double Door"] = (0, 90, 0), ["Sheet Metal Double Door"] = (0, 90, 0),
        ["Wooden Double Door"] = (0, 90, 0), ["Garage Door"] = (0, 90, 0),
        // the large backpack faced backwards (the wicker back panel toward the camera, the mossy
        // front that matches the preview was 180 away), RotY 180 turns it around
        ["Large Backpack"] = (0, 180, 0),
        // floor decor: on a skin browsing site these should face the viewer like a card and not
        // lie flat like in the game, RotX 90 tips them up
        ["Rug"] = (90, 0, 0), ["Bearskin Rug"] = (90, 0, 0), ["Sleeping Bag"] = (90, 0, 0),
        // the shop front panel was edge-on (sideways), RotY 90 turns the face to the camera
        ["Metal Shop Front"] = (0, 90, 0),
        // the miner hat's lamp housing showed its underside to the camera, RotX 180 flips it
        ["Miner Hat"] = (180, 0, 0),
        // items that lie flat and should stand up
        ["Acoustic Guitar"] = (90, 0, 90), ["Spinning Wheel"] = (90, 0, 0),
        ["Hide Pants"] = (-90, 0, 0),
        // the HMLMG stands on its end (length along Y), RotZ 90 makes it a horizontal side view
        ["HMLMG"] = (0, 90, 90),
        // the bed's mattress faced sideways like a wall instead of up (the bounding box is taller, 1.486,
        // than wide, 0.928, so it was on its side), RotX 90 lays it flat
        ["Bed"] = (-90, 0, 0),
        // melee tools stand upright but edge-on to the camera, RotY 90 turns the blade/head to face
        // the viewer (they stay upright, that was the choice for melee)
        ["Salvaged Icepick"] = (0, 90, 0), ["Pick Axe"] = (0, 90, 0),
        // the stone pick axe was end-on (length along the depth) and not edge-on, so stand it up and face the camera
        ["Stone Pick Axe"] = (-90, 90, 0),
        ["Hatchet"] = (0, 90, 0), ["Stone Hatchet"] = (0, 90, 0), ["Hammer"] = (0, 90, 0),
        ["Bone Knife"] = (0, 90, 0), ["Longsword"] = (0, 90, 0), ["Sword"] = (0, 90, 0),
        // TODO: glove pairs (Leather / Roadsign / Tactical Gloves) come out flat and small. They're two mirrored
        // meshes spread along X, so one rotation around the origin can't pose them (RotX did nothing).
        // Left flat for now, would need posing per mesh.
    };

    private static List<PartInput> BuildCommunitySkinPartList(string workshopId)
    {
        string texDir = Path.Combine(SkinsRoot, workshopId);

        string manifestPath = Path.Combine(texDir, "manifest", "manifest.txt");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                $"No manifest found for skin {workshopId} at {manifestPath} - extract it with " +
                $"AssetStudioModCLI (-t textAsset --filter-by-container \"skins/{workshopId}\") first.");

        (string itemType, List<ManifestGroup> groups) = ParseManifest(manifestPath);

        if (!ItemConfigs.TryGetValue(itemType, out var config))
            throw new NotSupportedException(
                $"No part/mesh mapping configured for ItemType \"{itemType}\" (skin {workshopId}). " +
                $"Add an entry to ItemConfigs, using the item's real .skinnable MonoBehaviour " +
                $"(MeshDownloadPaths + Groups[]) to determine which meshes use which group.");

        return config.Meshes.Select<MeshGroupMapping, PartInput?>(mesh =>
        {
            if (mesh.GroupIndex >= groups.Count)
            {
                // older skins (early Roadsign Pants/Vest, Riot Helmet) don't have the extra group the config expects
                // (visor, strap, ...). Same as a "none" _MainTex, skip the part instead of failing the skin
                Console.WriteLine($"  - {mesh.PartName}: manifest has only {groups.Count} group(s), " +
                    $"needs group {mesh.GroupIndex} (older skin schema) - skipping.");
                return null;
            }

            var group = groups[mesh.GroupIndex];

            string? TexPath(string key)
            {
                if (!group.Textures.TryGetValue(key, out var fileName) || fileName is null or "none")
                    return null;
                // AssetStudio's containerFull export puts each texture in a folder named after the lowercase
                // manifest key + group index ("_maintex0"), and the PNG inside has its own name ("_MainTex0.png")
                string folder = Path.Combine(texDir, $"{key.ToLower()}{mesh.GroupIndex}");
                string path = Path.Combine(folder, fileName);
                if (File.Exists(path)) return path;
                // Some manifests (old Roadsign Pants/Vest skins) have the original extension of the texture
                // ("_MainTex0.jpg"), but AssetStudio always exports textures as PNG, so the file on disk
                // is .png. Fall back to that.
                string pngPath = Path.Combine(folder, Path.ChangeExtension(fileName, ".png"));
                return File.Exists(pngPath) ? pngPath : path;
            }

            string? mainTex = TexPath("_MainTex");
            if (mesh.GlassAlpha is float glassA)
            {
                string glass = Path.Combine(Path.GetTempPath(), $"RustSkinToGlb_glass_{workshopId}.png");
                byte glassByte = (byte)Math.Clamp((int)MathF.Round(glassA * 255f), 1, 255);
                if (mesh.GlassTexture is not null && File.Exists(mesh.GlassTexture))
                {
                    using var src = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(mesh.GlassTexture);
                    src.ProcessPixelRows(acc => { for (int y = 0; y < acc.Height; y++) foreach (ref var px in acc.GetRowSpan(y)) px.A = glassByte; });
                    src.SaveAsPng(glass);
                }
                else
                {
                    using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4,
                        new SixLabors.ImageSharp.PixelFormats.Rgba32(35, 45, 50, glassByte));
                    img.SaveAsPng(glass);
                }
                mainTex = glass;
            }
            bool colourOnlySkin = groups.All(g => !g.Textures.TryGetValue("_MainTex", out var f) || f is null or "none");
            System.Numerics.Vector3? emissionColour = null;
            if (mainTex is null && colourOnlySkin && group.Color is { } tint)
            {
                // A skin without any diffuse image is a plain colour skin, it looks like the manifest's _Color
                // (and _EmissionColor for glow). Only when every group has no _MainTex, so skins that leave
                // some parts out on purpose still skip those parts.
                string solid = Path.Combine(Path.GetTempPath(), $"RustSkinToGlb_solid_{workshopId}_{mesh.GroupIndex}.png");
                byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
                using (var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4, new SixLabors.ImageSharp.PixelFormats.Rgba32(B(tint.X), B(tint.Y), B(tint.Z), 255)))
                    img.SaveAsPng(solid);
                mainTex = solid;
                emissionColour = group.EmissionColor;
            }
            if (mainTex is null)
            {
                // the group's _MainTex is "none" or missing, so the skin doesn't reskin this part.
                // Skip it (like blank MainTex parts) instead of failing the whole skin.
                Console.WriteLine($"  - {mesh.PartName}: group {mesh.GroupIndex} has no _MainTex (part not reskinned) - skipping.");
                return null;
            }

            // glass panes are a plain tint, they don't get the group's normal / occlusion / gloss / emission maps
            bool isGlass = mesh.GlassAlpha is not null;
            string? bumpMap = isGlass ? null : TexPath("_BumpMap");
            string? specGloss = isGlass ? null : TexPath("_SpecGlossMap");
            string? occlusion = isGlass ? null : TexPath("_OcclusionMap");
            string? emission = isGlass ? null : TexPath("_EmissionMap");
            // unity's metallic workflow, a manifest has either _SpecGlossMap or _MetallicGlossMap
            string? metallicGloss = isGlass ? null : TexPath("_MetallicGlossMap");

            // rotation: the per item type table wins, otherwise the config's own
            var rot = OrientationOverrides.TryGetValue(itemType, out var ovr)
                ? ovr : (X: config.RotX, Y: config.RotY, Z: config.RotZ);

            return new PartInput(
                mesh.PartName,
                Path.Combine(mesh.ObjDirOverride ?? config.ObjDir, mesh.ObjFileName),
                mainTex,
                bumpMap is not null && File.Exists(bumpMap) ? bumpMap : null,
                specGloss is not null && File.Exists(specGloss) ? specGloss : null,
                occlusion is not null && File.Exists(occlusion) ? occlusion : null,
                emission is not null && File.Exists(emission) ? emission : null,
                group.Glossiness,
                group.OcclusionStrength,
                group.BumpScale,
                group.Cutoff,
                config.StripIslandsFraction,
                config.ScaleFactor,
                config.FirstObjectOnly,
                config.StripSmallIslandsFraction,
                mesh.IslandRank,
                config.ObjectIndex,
                config.StripSmallestIsland,
                metallicGloss is not null && File.Exists(metallicGloss) ? metallicGloss : null,
                rot.X, rot.Y, rot.Z,
                mesh.GeometryGroup,
                emissionColour,
                config.StripSideIslandsMaxAbsX,
                mesh.ZRange,
                mesh.GlassAlpha
            );
        }).Where(p => p is not null).Select(p => p!).ToList();
    }

    private sealed record ManifestGroup(
        Dictionary<string, string?> Textures, float Glossiness, float OcclusionStrength, float BumpScale, float Cutoff,
        System.Numerics.Vector3? Color = null, System.Numerics.Vector3? EmissionColor = null);

    // reads a skin's manifest (plain json): the ItemType, the texture file per group and the
    // _Glossiness / _OcclusionStrength / _BumpScale values, which default to 1 like in unity
    private static (string ItemType, List<ManifestGroup> Groups) ParseManifest(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string itemType = doc.RootElement.GetProperty("ItemType").GetString()
            ?? throw new InvalidDataException($"Manifest at {manifestPath} has no ItemType.");

        var result = new List<ManifestGroup>();

        foreach (var group in doc.RootElement.GetProperty("Groups").EnumerateArray())
        {
            var textures = new Dictionary<string, string?>();
            if (group.TryGetProperty("Textures", out var texturesEl))
            {
                foreach (var prop in texturesEl.EnumerateObject())
                    textures[prop.Name] = prop.Value.GetString();
            }

            float glossiness = 1f, occlusionStrength = 1f, bumpScale = 1f, cutoff = 0f;
            if (group.TryGetProperty("Floats", out var floats))
            {
                if (floats.TryGetProperty("_Glossiness", out var g)) glossiness = g.GetSingle();
                if (floats.TryGetProperty("_OcclusionStrength", out var o)) occlusionStrength = o.GetSingle();
                if (floats.TryGetProperty("_BumpScale", out var b)) bumpScale = b.GetSingle();
                if (floats.TryGetProperty("_Cutoff", out var c)) cutoff = c.GetSingle();
            }

            System.Numerics.Vector3? ReadColor(string name)
            {
                if (!group.TryGetProperty("Colors", out var cols) || !cols.TryGetProperty(name, out var c)) return null;
                return new System.Numerics.Vector3(
                    c.TryGetProperty("r", out var r) ? r.GetSingle() : 0f,
                    c.TryGetProperty("g", out var g2) ? g2.GetSingle() : 0f,
                    c.TryGetProperty("b", out var b2) ? b2.GetSingle() : 0f);
            }

            result.Add(new ManifestGroup(textures, glossiness, occlusionStrength, bumpScale, cutoff,
                ReadColor("_Color"), ReadColor("_EmissionColor")));
        }

        return (itemType, result);
    }
}
