using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RustSkinToGlb;

// weekly update: catalog -> diff -> steamcmd download -> ingest -> build GLBs -> data.json -> report
//   RustSkinToGlb --weekly [--dry-run] [--skip-download] [--sandbox <dir>] [--catalog-file <path>]
//                          [--steamcmd <dir>] [--limit N] [--no-commit]
// the api key only comes from the STEAM_API_KEY env var and is scrubbed from any error text.
// without a key use --catalog-file. Every stage can be repeated, a dead run can just be started again.
// exit codes: 0 fine (also nothing new), 1 some skins need a look (see report), 2 couldn't run
public static partial class Program
{
    internal static string SkinsRoot = DefaultSkinsRoot;

    private const string AppId = "252490";
    private static readonly string[] DataFieldOrder =
    {
        "id", "itemDefId", "name", "itemShortName", "category", "itemType", "hasConfig", "dataSource", "hasTextures",
        "buildable", "note", "glb", "iconUrl", "iconUrlLarge", "tags", "storeTags", "timeLimited", "tradable",
        "marketable", "inCatalog", "workshopUrl",
    };

    private sealed class WeeklyOptions
    {
        public string Repo = "";
        public string DataPath = "", CatalogPath = "", DigestPath = "", SkinsDir = "", GlbDir = "", OutRoot = "";
        public string SteamCmdDir = @"C:\Dev\SteamCMD";
        public string? CatalogFile;
        public bool DryRun, SkipDownload, NoCommit, Sandbox;
        public int Limit = int.MaxValue;
    }

    private sealed class NewSkin
    {
        public string Id = "", Name = "", ShortName = "", Status = "", Note = "";
        public JsonElement Def;
        public string? ItemType;
        public string DataSource = "";
        public bool Buildable, HasConfig, HasTextures;
        public bool Existing;          // already in data.json (a retry), so replace its entry instead of adding a new one
    }

    private static int WeeklyMain(string[] args)
    {
        var o = new WeeklyOptions();
        o.Repo = FindRepoRoot();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": o.DryRun = true; break;
                case "--skip-download": o.SkipDownload = true; break;
                case "--no-commit": o.NoCommit = true; break;
                case "--sandbox": o.Sandbox = true; o.OutRoot = Path.GetFullPath(args[++i]); break;
                case "--catalog-file": o.CatalogFile = Path.GetFullPath(args[++i]); break;
                case "--steamcmd": o.SteamCmdDir = args[++i]; break;
                case "--limit": o.Limit = int.Parse(args[++i]); break;
                default: Console.WriteLine("ERROR: " + $"Unknown option {args[i]}"); return 2;
            }
        }

        string dataDir = Path.Combine(o.Repo, "data");
        if (o.Sandbox)
        {
            // everything gets written under the sandbox folder, the inputs are copied in first
            Directory.CreateDirectory(o.OutRoot);
            o.DataPath = Path.Combine(o.OutRoot, "data.json");
            o.CatalogPath = Path.Combine(o.OutRoot, "steam-itemdefs.json");
            o.DigestPath = Path.Combine(o.OutRoot, "steam-itemdefs.digest.txt");
            o.SkinsDir = Path.Combine(o.OutRoot, "skins");
            o.GlbDir = Path.Combine(o.OutRoot, "glb");
            if (!File.Exists(o.DataPath)) File.Copy(Path.Combine(dataDir, "data.json"), o.DataPath);
            if (!File.Exists(o.CatalogPath)) File.Copy(Path.Combine(dataDir, "steam-itemdefs.json"), o.CatalogPath);
            Directory.CreateDirectory(o.SkinsDir); Directory.CreateDirectory(o.GlbDir);
            o.NoCommit = true;
        }
        else
        {
            o.DataPath = Path.Combine(dataDir, "data.json");
            o.CatalogPath = Path.Combine(dataDir, "steam-itemdefs.json");
            o.DigestPath = Path.Combine(dataDir, "steam-itemdefs.digest.txt");
            o.SkinsDir = Path.Combine(dataDir, "all-skins-textures", "assets", "skins");
            o.GlbDir = DefaultGlbDir;
            o.OutRoot = dataDir;
        }
        SkinsRoot = o.SkinsDir;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        string runDir = Path.Combine(o.Sandbox ? o.OutRoot : dataDir, "weekly", stamp);
        Directory.CreateDirectory(runDir);
        string apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY") ?? "";
        var problems = new List<string>();
        void Log(string s) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {Scrub(s, apiKey)}"); }
        // rewrites docs/skins.json + icons, called on every exit that isn't a dry run so the site matches data.json
        void Export() =>
            ExportViewer(o.DataPath, o.CatalogPath, o.SkinsDir, o.GlbDir,
                         o.Sandbox ? Path.Combine(o.OutRoot, "docs") : Path.GetFullPath(Path.Combine(o.Repo, "..", "docs")),
                         Log);

        // get up to date first, the price action commits to main on its own and this way the commit at the end
        // doesn't get rejected. Stops here if the pull fails so nothing gets built on an old copy.
        if (!o.Sandbox && !o.DryRun && !o.NoCommit)
        {
            var (pullCode, pullOut) = Git(o, "pull --rebase --autostash");
            if (pullCode != 0) { Console.WriteLine("ERROR: git pull failed, fix that and run again:\n" + pullOut); return 2; }
            Log("git pull: up to date");
        }

        try
        {
            // 1. catalog
            string catalogText; string? newDigest = null;
            if (o.CatalogFile != null)
            {
                Log($"Stage 1: using catalog file {o.CatalogFile}");
                catalogText = CleanJson(File.ReadAllText(o.CatalogFile));
            }
            else
            {
                if (apiKey.Length == 0)
                {
                    Console.WriteLine("ERROR: " + "STEAM_API_KEY is not set (and no --catalog-file given). Set it once with:\n  setx STEAM_API_KEY <your key>");
                    return 2;
                }
                string? known = File.Exists(o.DigestPath) ? File.ReadAllText(o.DigestPath).Trim() : null;
                Log("Stage 1: asking Steam for the item-definition digest");
                var fetched = FetchCatalog(apiKey, known);
                if (fetched == null) { Log("Catalog digest unchanged since last run - nothing to do."); if (!o.DryRun) Export(); return 0; }
                (newDigest, catalogText) = fetched.Value;
            }
            var catalogSkins = ParseCatalogSkins(catalogText);
            Log($"Catalog lists {catalogSkins.Count} skins.");

            // 2. diff against data.json
            var (knownIds, shortToType, retryIds) = ReadDataIndex(o.DataPath);
            var newIds = catalogSkins.Keys.Where(id => !knownIds.Contains(id)).OrderBy(id => long.Parse(id)).ToList();
            var removed = knownIds.Where(id => !catalogSkins.ContainsKey(id)).ToList();
            Log($"Diff: {newIds.Count} new skins, {removed.Count} in data.json but no longer in the catalog.");
            if (newIds.Count > o.Limit) { Log($"--limit {o.Limit}: processing only the first {o.Limit}."); newIds = newIds.Take(o.Limit).ToList(); }

            var skins = newIds.Select(id => new NewSkin
            {
                Id = id, Def = catalogSkins[id],
                Name = Str(catalogSkins[id], "name"), ShortName = Str(catalogSkins[id], "itemshortname"),
            }).ToList();

            // retry queue: skins that are in data.json but still not buildable (download failed, no mesh
            // mapping yet, build failed) get another go every run, so nothing gets forgotten
            foreach (var id in retryIds.Where(catalogSkins.ContainsKey).OrderBy(id => long.Parse(id)))
                skins.Add(new NewSkin { Id = id, Def = catalogSkins[id], Existing = true, Name = Str(catalogSkins[id], "name"), ShortName = Str(catalogSkins[id], "itemshortname") });
            if (retryIds.Count > 0) Log($"Retry queue: {skins.Count(s => s.Existing)} skin(s) already in data.json are still not buildable and will be re-attempted.");

            if (o.DryRun)
            {
                WriteReport(runDir, o, skins, removed, newDigest, problems, dryRun: true);
                Log($"Dry run: report written to {runDir}. No downloads, builds or data changes were made.");
                return 0;
            }

            // save the new catalog before the slow stuff, so an interrupted run doesn't start from scratch
            if (o.CatalogFile == null)
            {
                // only rewrite if the content really changed, otherwise a new digest alone would give a
                // huge whitespace-only diff in git
                if (!(File.Exists(o.CatalogPath) && SameJson(File.ReadAllText(o.CatalogPath), catalogText)))
                {
                    if (File.Exists(o.CatalogPath)) File.Copy(o.CatalogPath, o.CatalogPath + ".prev", true);
                    File.WriteAllText(o.CatalogPath, catalogText, new UTF8Encoding(false));
                }
                if (newDigest != null) File.WriteAllText(o.DigestPath, newDigest);
            }
            if (skins.Count == 0) { WriteReport(runDir, o, skins, removed, newDigest, problems, false); Log("No new skins."); Export(); return 0; }

            // 3. download
            string content = Path.Combine(o.SteamCmdDir, "steamapps", "workshop", "content", AppId);
            if (!o.SkipDownload)
            {
                var need = skins.Select(s => s.Id).Where(id => !HasSkinData(content, o.SkinsDir, id)).ToList();
                Log($"Stage 3: {need.Count} of {skins.Count} skins need downloading via SteamCMD (anonymous).");
                DownloadWorkshop(need, o.SteamCmdDir, runDir, Log);
            }
            else Log("Stage 3: skipped (--skip-download).");

            // 4. ingest
            Log("Stage 4: ingesting downloaded skins");
            var legacyIds = new List<string>();
            foreach (var s in skins)
            {
                string src = Path.Combine(content, s.Id), dest = Path.Combine(o.SkinsDir, s.Id);
                if (File.Exists(Path.Combine(dest, "manifest", "manifest.txt"))) { s.DataSource = "steamcmd"; continue; }
                if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any()) { s.Status = "no-data"; s.Note = "SteamCMD produced no files"; continue; }
                if (File.Exists(Path.Combine(src, "manifest.txt"))) { IngestModern(src, dest); s.DataSource = "steamcmd"; }
                else if (Directory.EnumerateFiles(src, "*.asset", SearchOption.AllDirectories).Any()) { legacyIds.Add(s.Id); s.DataSource = "legacyImport"; }
                else { s.Status = "unknown-format"; s.Note = "no manifest.txt and no *.asset (Unity meta)"; }
            }
            if (legacyIds.Count > 0)
            {
                Log($"  {legacyIds.Count} old-format skin(s) -> legacy importer");
                string tmpData = Path.Combine(runDir, "legacy-data.json");
                WriteLegacyLookup(o.DataPath, tmpData, skins);
                LegacyImporter.Run(content, o.SkinsDir, tmpData, Path.Combine(runDir, "legacy-import-report.tsv"));
            }

            // classify: item type, config and textures
            foreach (var s in skins)
            {
                string manifest = Path.Combine(o.SkinsDir, s.Id, "manifest", "manifest.txt");
                if (!File.Exists(manifest))
                {
                    s.ItemType = shortToType.GetValueOrDefault(s.ShortName);
                    if (s.Status == "") { s.Status = "no-data"; s.Note = "no manifest after ingestion"; }
                    continue;
                }
                try
                {
                    var (type, groups) = ParseManifest(manifest);
                    s.ItemType = type;
                    s.HasConfig = ItemConfigs.ContainsKey(type);
                    s.HasTextures = groups.Any(g => g.Textures.TryGetValue("_MainTex", out var f) && f is not null and not "none");
                    bool colourOnly = !s.HasTextures && groups.Any(g => g.Color is not null);
                    s.Buildable = s.HasConfig && (s.HasTextures || colourOnly);
                    if (!s.HasConfig) { s.Status = "new-item-type"; s.Note = $"no ItemConfig for \"{type}\" (new item type; needs a mesh mapping)"; }
                    else if (!s.Buildable) { s.Status = "no-texture"; s.Note = "manifest has no usable main texture or colour"; }
                }
                catch (Exception ex) { s.Status = "bad-manifest"; s.Note = ex.Message; }
            }

            // 5. build
            var toBuild = skins.Where(s => s.Buildable).ToList();
            Log($"Stage 5: building {toBuild.Count} GLBs into {o.GlbDir}");
            if (toBuild.Count > 0)
            {
                string map = Path.Combine(runDir, "build-map.json");
                File.WriteAllText(map, JsonSerializer.Serialize(toBuild.Select(s => new { id = s.Id, itemType = s.ItemType })));
                string failFile = Path.Combine(runDir, "batch-failures.tsv");
                RunBatch(o.GlbDir, map, failFile);
                var failText = File.Exists(failFile) ? File.ReadAllLines(failFile) : Array.Empty<string>();
                foreach (var s in toBuild)
                {
                    string glb = Path.Combine(o.GlbDir, s.Id + ".glb");
                    if (File.Exists(glb) && new FileInfo(glb).Length > 5000) s.Status = "built";
                    else
                    {
                        s.Buildable = false; s.Status = "build-failed";
                        s.Note = failText.FirstOrDefault(l => l.StartsWith(s.Id + "\t"))?.Split('\t').Skip(2).FirstOrDefault() ?? "no GLB produced";
                    }
                }
            }

            // 6. data.json
            WriteDataEntries(o, skins);
            Log($"Stage 6: data.json updated ({skins.Count(s => !s.Existing)} appended, {skins.Count(s => s.Existing)} replaced)");

            foreach (var s in skins.Where(x => x.Status is not "built"))
                problems.Add($"{s.Id} \"{s.Name}\" [{s.ItemType ?? s.ShortName}]: {s.Status} - {s.Note}");

            // 7. viewer export, report and commit
            Export();
            WriteReport(runDir, o, skins, removed, newDigest, problems, false);
            Log($"Stage 7: report written to {runDir}");
            if (!o.Sandbox && skins.Any(s => s.Status == "built")) UpdateReadmeStamp(o);
            if (!o.NoCommit) GitCommit(o, skins.Count(s => s.Status == "built"), skins.Count, Log);
            Log(problems.Count == 0 ? "Done - everything built." : $"Done - {problems.Count} item(s) need attention (see report.md).");
            return problems.Count == 0 ? 0 : 1;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            Console.WriteLine("ERROR: " + "Steam rejected the API key (HTTP " + (int)ex.StatusCode! + "). Check the STEAM_API_KEY environment variable.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + "Weekly update failed: " + Scrub(ex.ToString(), apiKey));
            return 2;
        }
    }

    // helpers

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !(Directory.Exists(Path.Combine(d.FullName, "data")) && Directory.Exists(Path.Combine(d.FullName, "RustSkinToGlb")))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repo root (a folder containing data and RustSkinToGlb) above " + AppContext.BaseDirectory);
    }

    private static string Scrub(string s, string key) => key.Length == 0 ? s : Regex.Replace(s.Replace(key, "<KEY>"), @"key=[0-9A-Fa-f]{16,}", "key=<KEY>");

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static (string digest, string archive)? FetchCatalog(string key, string? knownDigest)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        string meta = http.GetStringAsync($"https://api.steampowered.com/IInventoryService/GetItemDefMeta/v1/?key={key}&appid={AppId}").GetAwaiter().GetResult();
        string? digest = FindProperty(JsonDocument.Parse(meta).RootElement, "digest");
        if (string.IsNullOrEmpty(digest)) throw new InvalidDataException("GetItemDefMeta returned no digest.");
        if (digest == knownDigest) return null;
        string archive = CleanJson(http.GetStringAsync($"https://api.steampowered.com/IGameInventory/GetItemDefArchive/v1/?appid={AppId}&digest={Uri.EscapeDataString(digest)}").GetAwaiter().GetResult());
        if (JsonDocument.Parse(archive).RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Item definition archive is not a JSON array.");
        return (digest, archive);
    }

    // steam pads the item archive with trailing NUL bytes, so strip those plus whitespace and any BOM
    private static string CleanJson(string s) => s.TrimStart('\uFEFF').TrimEnd('\0', ' ', '\r', '\n', '\t');

    private static bool SameJson(string a, string b)
    {
        try { return JsonSerializer.Serialize(JsonDocument.Parse(CleanJson(a)).RootElement) == JsonSerializer.Serialize(JsonDocument.Parse(CleanJson(b)).RootElement); }
        catch { return false; }
    }

    private static string? FindProperty(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == name && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                var r = FindProperty(p.Value, name); if (r != null) return r;
            }
        return null;
    }

    // workshop id -> catalog record, only for defs with a workshopid (first one wins if there are duplicates)
    private static Dictionary<string, JsonElement> ParseCatalogSkins(string catalogText)
    {
        var doc = JsonDocument.Parse(CleanJson(catalogText));
        var map = new Dictionary<string, JsonElement>();
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            if (!d.TryGetProperty("workshopid", out var w)) continue;
            string id = w.ValueKind == JsonValueKind.Number ? w.GetInt64().ToString() : (w.GetString() ?? "0");
            if (id == "0" || id.Length == 0 || map.ContainsKey(id)) continue;
            map[id] = d.Clone();
        }
        return map;
    }

    private static (HashSet<string> ids, Dictionary<string, string> shortToType, List<string> retry) ReadDataIndex(string dataPath)
    {
        var ids = new HashSet<string>(); var st = new Dictionary<string, string>(); var retry = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(dataPath));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            ids.Add(e.GetProperty("id").GetString()!);
            if (e.TryGetProperty("buildable", out var b) && b.ValueKind == JsonValueKind.False && e.TryGetProperty("inCatalog", out var ic) && ic.ValueKind == JsonValueKind.True)
                retry.Add(e.GetProperty("id").GetString()!);
            string sn = Str(e, "itemShortName"), it = Str(e, "itemType");
            if (sn.Length > 0 && it.Length > 0) st[sn] = it;
        }
        return (ids, st, retry);
    }

    private static bool HasSkinData(string steamContent, string skinsDir, string id) =>
        File.Exists(Path.Combine(skinsDir, id, "manifest", "manifest.txt")) ||
        (Directory.Exists(Path.Combine(steamContent, id)) && Directory.EnumerateFileSystemEntries(Path.Combine(steamContent, id)).Any());

    private static void DownloadWorkshop(List<string> ids, string steamCmdDir, string runDir, Action<string> log)
    {
        if (ids.Count == 0) return;
        string exe = Path.Combine(steamCmdDir, "steamcmd.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"steamcmd.exe not found at {exe} (use --steamcmd <dir>)");
        string content = Path.Combine(steamCmdDir, "steamapps", "workshop", "content", AppId);
        var pending = ids.ToList();
        for (int attempt = 1; attempt <= 2 && pending.Count > 0; attempt++)
        {
            foreach (var chunk in pending.Chunk(60))
            {
                string script = Path.Combine(runDir, $"steamcmd-{attempt}-{chunk[0]}.txt");
                File.WriteAllLines(script, new[] { "@ShutdownOnFailedCommand 0", "@NoPromptForPassword 1", "login anonymous" }
                    .Concat(chunk.Select(id => $"workshop_download_item {AppId} {id}")).Append("quit"));
                log($"  SteamCMD attempt {attempt}: {chunk.Length} items");
                var psi = new ProcessStartInfo(exe, $"+runscript \"{script}\"") { WorkingDirectory = steamCmdDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi)!;
                var outTask = p.StandardOutput.ReadToEndAsync(); var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit((int)TimeSpan.FromMinutes(90).TotalMilliseconds)) { try { p.Kill(true); } catch { } log("  SteamCMD timed out; killed."); }
                File.WriteAllText(Path.ChangeExtension(script, ".log"), outTask.Result + errTask.Result);
            }
            pending = pending.Where(id => !Directory.Exists(Path.Combine(content, id)) || !Directory.EnumerateFileSystemEntries(Path.Combine(content, id)).Any()).ToList();
            if (pending.Count > 0) log($"  {pending.Count} item(s) still missing after attempt {attempt}");
        }
    }

    // copies a flat workshop folder into the nested layout the converter wants
    private static void IngestModern(string srcDir, string destDir)
    {
        foreach (var f in Directory.GetFiles(srcDir))
        {
            string name = Path.GetFileName(f), ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".txt")) continue;
            if (name.StartsWith("workshop_icon", StringComparison.OrdinalIgnoreCase)) continue;   // not kept
            string folder = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            string dir = Path.Combine(destDir, folder);
            Directory.CreateDirectory(dir);
            File.Copy(f, Path.Combine(dir, name), true);
        }
    }

    private static void WriteLegacyLookup(string dataPath, string tmpPath, List<NewSkin> skins)
    {
        var arr = new List<object>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(dataPath)))
            foreach (var e in doc.RootElement.EnumerateArray())
                arr.Add(new { id = e.GetProperty("id").GetString(), itemShortName = Str(e, "itemShortName"), itemType = Str(e, "itemType") });
        foreach (var s in skins) arr.Add(new { id = s.Id, itemShortName = s.ShortName });   // no itemType on purpose, an empty one would overwrite the real shortname -> type mapping
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(arr));
    }

    // data.json is edited as text so it keeps the exact style it already has (BOM, CRLF, 4/8 space indent, `":  "`)

    private static string JsonStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
            sb.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                '\'' or '&' or '<' or '>' => $"\\u{(int)c:x4}",           // powershell 5.1 escaped these when it wrote the file
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }

    private static string FormatDataEntry(NewSkin s, WeeklyOptions o)
    {
        string storeTags = Str(s.Def, "store_tags");
        string glb = s.Buildable ? RelPath(o, Path.Combine(o.GlbDir, s.Id + ".glb")) : "";
        var f = new Dictionary<string, string>
        {
            ["id"] = JsonStr(s.Id), ["itemDefId"] = JsonStr(Str(s.Def, "itemdefid")), ["name"] = JsonStr(s.Name),
            ["itemShortName"] = JsonStr(s.ShortName), ["category"] = JsonStr(Str(s.Def, "type")),
            ["itemType"] = s.ItemType == null ? "null" : JsonStr(s.ItemType),
            ["hasConfig"] = s.HasConfig ? "true" : "false", ["dataSource"] = JsonStr(s.DataSource.Length > 0 ? s.DataSource : "none"),
            ["hasTextures"] = s.HasTextures ? "true" : "false", ["buildable"] = s.Buildable ? "true" : "false",
            ["note"] = s.Note.Length > 0 ? JsonStr(s.Note) : "null", ["glb"] = glb.Length > 0 ? JsonStr(glb) : "null",
            ["iconUrl"] = JsonStr(Str(s.Def, "icon_url")), ["iconUrlLarge"] = JsonStr(Str(s.Def, "icon_url_large")),
            ["tags"] = JsonStr(Str(s.Def, "tags")), ["storeTags"] = JsonStr(storeTags),
            ["timeLimited"] = storeTags.Contains("time.limited") ? "true" : "false",
            ["tradable"] = Bool(s.Def, "tradable") ? "true" : "false", ["marketable"] = Bool(s.Def, "marketable") ? "true" : "false",
            ["inCatalog"] = "true", ["workshopUrl"] = JsonStr($"https://steamcommunity.com/sharedfiles/filedetails/?id={s.Id}"),
        };
        var sb = new StringBuilder("    {\r\n");
        for (int i = 0; i < DataFieldOrder.Length; i++)
            sb.Append("        ").Append(JsonStr(DataFieldOrder[i])).Append(":  ").Append(f[DataFieldOrder[i]]).Append(i < DataFieldOrder.Length - 1 ? ",\r\n" : "\r\n");
        return sb.Append("    }").ToString();
    }

    private static string RelPath(WeeklyOptions o, string abs)
    {
        string rel = Path.GetRelativePath(Path.GetDirectoryName(o.Repo)!, abs);   // relative to the repo root (the parent of pipeline/)
        return rel.StartsWith("..") ? abs.Replace('\\', '/') : rel.Replace('\\', '/');
    }

    // adds entries for new skins and replaces the ones of retried skins in place
    private static void WriteDataEntries(WeeklyOptions o, List<NewSkin> skins)
    {
        if (skins.Count == 0) return;
        string text = File.ReadAllText(o.DataPath);                 // ReadAllText drops the BOM
        foreach (var s in skins.Where(x => x.Existing))
        {
            var rx = new Regex("    \\{\\r\\n        \"id\":  \"" + s.Id + "\".*?\\r\\n    \\}", RegexOptions.Singleline);
            if (!rx.IsMatch(text)) throw new InvalidDataException($"could not find the data.json entry for retried skin {s.Id}");
            string block = FormatDataEntry(s, o);
            text = rx.Replace(text, _ => block, 1);
        }
        var fresh = skins.Where(x => !x.Existing).ToList();
        string result = text;
        if (fresh.Count > 0)
        {
            int close = text.LastIndexOf(']');
            if (close < 0) throw new InvalidDataException("data.json has no closing bracket");
            string head = text.Substring(0, close).TrimEnd();
            result = head + ",\r\n" + string.Join(",\r\n", fresh.Select(s => FormatDataEntry(s, o))) + "\r\n]";
        }
        JsonDocument.Parse(result).Dispose();                        // still has to be valid json
        File.Copy(o.DataPath, o.DataPath + ".bak", true);
        File.WriteAllText(o.DataPath, result, new UTF8Encoding(true));
    }

    private static void WriteReport(string runDir, WeeklyOptions o, List<NewSkin> skins, List<string> removed, string? digest, List<string> problems, bool dryRun)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Weekly update {DateTime.Now:yyyy-MM-dd HH:mm}{(dryRun ? " (DRY RUN)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"- New skins in catalog: **{skins.Count}**");
        if (!dryRun) sb.AppendLine($"- Built: **{skins.Count(s => s.Status == "built")}**, need attention: **{problems.Count}**");
        sb.AppendLine($"- In data.json but no longer in the catalog: {removed.Count}");
        if (digest != null) sb.AppendLine($"- Catalog digest: `{digest}`");
        var newTypes = skins.Where(s => s.Status == "new-item-type").GroupBy(s => s.ItemType).ToList();
        if (newTypes.Count > 0)
        {
            sb.AppendLine().AppendLine("## New item types (need a mesh mapping before these can be built)");
            foreach (var g in newTypes) sb.AppendLine($"- **{g.Key}**: {g.Count()} skin(s), e.g. {g.First().Id} \"{g.First().Name}\"");
        }
        if (problems.Count > 0)
        {
            sb.AppendLine().AppendLine("## Needs attention");
            foreach (var p in problems) sb.AppendLine($"- {p}");
        }
        sb.AppendLine().AppendLine("## New skins");
        sb.AppendLine("| id | name | type | status |").AppendLine("|---|---|---|---|");
        foreach (var s in skins) sb.AppendLine($"| {s.Id} | {s.Name.Replace("|", "/")}{(s.Existing ? " (retry)" : "")} | {s.ItemType ?? s.ShortName} | {(dryRun ? "-" : s.Status)} |");
        if (removed.Count > 0) sb.AppendLine().AppendLine("## Removed from catalog").AppendLine(string.Join(", ", removed.Take(50)) + (removed.Count > 50 ? $" ... (+{removed.Count - 50})" : ""));
        File.WriteAllText(Path.Combine(runDir, "report.md"), sb.ToString());
    }

    // bumps the "Last full asset update" line in the readme
    private static void UpdateReadmeStamp(WeeklyOptions o)
    {
        string path = Path.GetFullPath(Path.Combine(o.Repo, "..", "README.md"));
        if (!File.Exists(path)) return;
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        string text = File.ReadAllText(path);
        string next = Regex.Replace(text, @"Last full asset update: [^\r\n]*", "Last full asset update: " + stamp);
        if (next != text) File.WriteAllText(path, next, new UTF8Encoding(false));
    }

    // runs git in the repo and returns the exit code and what it printed (stdout and stderr together)
    private static (int Code, string Output) Git(WeeklyOptions o, string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = o.Repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    private static void GitCommit(WeeklyOptions o, int built, int total, Action<string> log)
    {
        // add the paths one by one, if one is missing (no digest file on a --catalog-file run for example)
        // git refuses the whole add. The data files, the site files and the new models/icons (lfs) get
        // committed, not reports or logs.
        foreach (var rel in new[] { "data/data.json", "data/steam-itemdefs.json", "data/steam-itemdefs.digest.txt", "data/skin-flags.json",
                                    "../README.md", "../docs/skins.json", "../docs/icons", "../docs/models" })
            if (File.Exists(Path.Combine(o.Repo, rel)) || Directory.Exists(Path.Combine(o.Repo, rel))) Git(o, $"add {rel}");
        var (commitCode, commitOut) = Git(o, $"commit -q -m \"Weekly update {DateTime.Now:yyyy-MM-dd}: {built}/{total} new skins built\"");
        if (commitCode != 0) { log("Nothing to commit (or git commit failed): " + commitOut); return; }
        log("Committed to git");

        // push, and if the remote moved in the meantime pull once more and try again
        var (pushCode, pushOut) = Git(o, "push");
        if (pushCode != 0)
        {
            log("git push was rejected, pulling and trying once more");
            var (rebaseCode, rebaseOut) = Git(o, "pull --rebase --autostash");
            if (rebaseCode == 0) (pushCode, pushOut) = Git(o, "push");
            else pushOut = rebaseOut;
        }
        log(pushCode == 0 ? "Pushed to GitHub" : "Push failed, the commit is still local (run git push yourself): " + pushOut);
    }
}
