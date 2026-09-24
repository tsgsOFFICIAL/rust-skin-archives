using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

// fetches the steam market prices into docs/prices.json (loaded by the site next to skins.json)
//   dotnet run --project pipeline/PriceFetcher -c Release -- --skins docs/skins.json --out docs/prices.json [--pages N]
//
// only needs skins.json (id + displayName), so it runs on a github action.
// prices.json has one block per source so skinport etc. can be added next to steam:
//   { "version": 1, "sources": { "steam": { "fetchedAt": "...", "complete": true,
//       "items": { "<skinId>": { "cents": 1624, "listings": 16 } } } } }
//
// the steam market search is public but only gives 10 items per request, so a full run is ~550 requests
// (~3s apart, longer wait on a 429). USD cents, lowest listing. Skins are matched by exact name, names shared
// by several skins are skipped. A finished run replaces the steam block, an interrupted one merges into the old file.

const string SearchUrl = "https://steamcommunity.com/market/search/render/?appid=252490&norender=1&currency=1&sort_column=name&sort_dir=asc&count=10&start=";

string skinsPath = "docs/skins.json", outPath = "docs/prices.json";
int maxPages = 0, delayMs = 2800;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--skins": skinsPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--pages": maxPages = int.Parse(args[++i]); break;
        case "--delay-ms": delayMs = int.Parse(args[++i]); break;
        default: Console.WriteLine($"Unknown option {args[i]}"); return 2;
    }
}
void Log(string s) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {s}");

// name -> skin ids
var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
using (var doc = JsonDocument.Parse(File.ReadAllText(skinsPath)))
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        string name = e.GetProperty("displayName").GetString() ?? "";
        if (!byName.TryGetValue(name, out var list)) byName[name] = list = new List<string>();
        list.Add(e.GetProperty("id").GetString()!);
    }
Log($"{byName.Values.Sum(l => l.Count)} skins, {byName.Count(kv => kv.Value.Count > 1)} names shared by several skins (skipped).");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("RustSkinViewerPrices/1.0");
var rng = new Random();
var fetched = new Dictionary<string, (int cents, int listings)>();
int total = -1, start = 0, pages = 0, unmatched = 0, ambiguousHits = 0;
bool complete = false, aborted = false;

while (true)
{
    JsonElement root = default; bool got = false;
    for (int attempt = 1; attempt <= 5 && !got; attempt++)
    {
        try
        {
            string body = await http.GetStringAsync(SearchUrl + start);
            var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True) { root = d.RootElement.Clone(); got = true; }
            else { Log($"page at {start}: success=false (attempt {attempt})"); await Task.Delay(15000 * attempt); }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500)
        {
            int wait = 60 * attempt;
            Log($"page at {start}: HTTP {(int?)ex.StatusCode}, waiting {wait}s (attempt {attempt}/5)");
            await Task.Delay(wait * 1000);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log($"page at {start}: {ex.GetType().Name} (attempt {attempt}/5)");
            await Task.Delay(10000 * attempt);
        }
    }
    if (!got) { aborted = true; Log($"Giving up at item {start}; keeping what was fetched."); break; }

    if (total < 0) { total = root.GetProperty("total_count").GetInt32(); Log($"Steam lists {total} Rust items."); }
    var results = root.GetProperty("results");
    int n = results.GetArrayLength();
    foreach (var r in results.EnumerateArray())
    {
        string hash = r.GetProperty("hash_name").GetString() ?? "";
        int cents = r.TryGetProperty("sell_price", out var sp) ? sp.GetInt32() : 0;
        int listings = r.TryGetProperty("sell_listings", out var sl) ? sl.GetInt32() : 0;
        if (!byName.TryGetValue(hash, out var ids)) { unmatched++; continue; }
        if (ids.Count > 1) { ambiguousHits++; continue; }
        if (cents > 0) fetched[ids[0]] = (cents, listings);
    }
    pages++; start += 10;
    if (pages % 25 == 0) Log($"{start}/{total} items read, {fetched.Count} priced so far");
    if (n == 0 || start >= total) { complete = true; break; }
    if (maxPages > 0 && pages >= maxPages) break;
    await Task.Delay(delayMs + rng.Next(0, 900));
}

if (fetched.Count == 0) { Log("No prices fetched; leaving the file untouched."); return 2; }

var file = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath)) as JsonObject ?? new JsonObject() : new JsonObject();
file["version"] = 1;
var sources = file["sources"] as JsonObject ?? new JsonObject(); file["sources"] = sources;
var steam = sources["steam"] as JsonObject ?? new JsonObject();
bool full = complete && !aborted && maxPages == 0;
var items = full ? new JsonObject() : (steam["items"] as JsonObject ?? new JsonObject());
foreach (var kv in fetched.OrderBy(k => long.Parse(k.Key, CultureInfo.InvariantCulture)))
    items[kv.Key] = new JsonObject { ["cents"] = kv.Value.cents, ["listings"] = kv.Value.listings };
steam["fetchedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
steam["complete"] = full;
steam["items"] = items;
sources["steam"] = steam;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
string tmp = outPath + ".tmp";
File.WriteAllText(tmp, file.ToJsonString());
File.Move(tmp, outPath, true);
Log($"Steam: {fetched.Count} skins priced ({unmatched} market items are not our skins, {ambiguousHits} skipped for shared names); {(full ? "complete" : "PARTIAL")} -> {outPath}");
return full ? 0 : 1;
