using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

// fetches the steam market prices into docs/prices.json (loaded by the site next to skins.json)
//   dotnet run --project pipeline/PriceFetcher -c Release -- --skins docs/skins.json --out docs/prices.json [--pages N]
//
// only needs skins.json (id + displayName), so it runs on a github action.
// prices.json has a prices array per skin, one entry per market, so skinport etc. can be added next to steam:
//   { "version": 2,
//     "sources": { "steam": { "fetchedAt": "...", "complete": true } },
//     "items": { "<skinId>": { "prices": [
//         { "name": "Steam", "price": 1624, "url": "https://steamcommunity.com/market/listings/252490/...", "listings": 16 } ] } } }
// "price" is USD cents (lowest listing). "sources" only tracks when each market was last fetched.
// A new market must use its own "name" (that is what a run replaces) and add its own entry to sources.
//
// the steam market search is public but only gives 10 items per request, so a full run is ~550 requests
// (~3s apart, longer wait on a 429). Skins are matched by exact name, names shared by several skins are
// skipped. A finished run replaces every Steam entry, an interrupted one merges into the old file.

const string SteamName = "Steam";
const string ListingUrl = "https://steamcommunity.com/market/listings/252490/";
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
var fetched = new Dictionary<string, (int cents, int listings, string hash)>();
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
        if (cents > 0) fetched[ids[0]] = (cents, listings, hash);
    }
    pages++; start += 10;
    if (pages % 25 == 0) Log($"{start}/{total} items read, {fetched.Count} priced so far");
    if (n == 0 || start >= total) { complete = true; break; }
    if (maxPages > 0 && pages >= maxPages) break;
    await Task.Delay(delayMs + rng.Next(0, 900));
}

if (fetched.Count == 0) { Log("No prices fetched; leaving the file untouched."); return 2; }

// an older file (version 1, prices grouped per market) is not migrated: it is rebuilt from what this run fetched
var old = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath)) as JsonObject : null;
bool oldIsCurrent = old?["version"]?.GetValue<int>() == 2;
var file = oldIsCurrent ? old! : new JsonObject();
file["version"] = 2;
var sources = file["sources"] as JsonObject ?? new JsonObject(); file["sources"] = sources;
var items = file["items"] as JsonObject ?? new JsonObject(); file["items"] = items;
bool full = complete && !aborted && maxPages == 0;

// Removes this market's entry from a skin's prices array; drops the skin when nothing is left.
static void RemoveEntry(JsonObject items, string id, string name)
{
    if (items[id]?["prices"] is not JsonArray arr) { items.Remove(id); return; }
    for (int i = arr.Count - 1; i >= 0; i--)
        if (arr[i]?["name"]?.GetValue<string>() == name) arr.RemoveAt(i);
    if (arr.Count == 0) items.Remove(id);
}

// a finished run replaces every Steam entry, so skins that were delisted lose their old price
if (full) foreach (var id in items.Select(kv => kv.Key).ToList()) RemoveEntry(items, id, SteamName);

foreach (var kv in fetched)
{
    RemoveEntry(items, kv.Key, SteamName);
    var entry = new JsonObject
    {
        ["name"] = SteamName,
        ["price"] = kv.Value.cents,
        ["url"] = ListingUrl + Uri.EscapeDataString(kv.Value.hash),
        ["listings"] = kv.Value.listings,
    };
    if (items[kv.Key]?["prices"] is JsonArray existing) existing.Add(entry);
    else items[kv.Key] = new JsonObject { ["prices"] = new JsonArray(entry) };
}

// stable order (by skin id) so the daily diff only shows real changes
var sorted = new JsonObject();
foreach (var kv in items.OrderBy(k => long.Parse(k.Key, CultureInfo.InvariantCulture)).ToList())
{
    items.Remove(kv.Key);
    sorted[kv.Key] = kv.Value;
}
file["items"] = sorted;

sources["steam"] = new JsonObject
{
    ["fetchedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    ["complete"] = full,
};

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
string tmp = outPath + ".tmp";
File.WriteAllText(tmp, file.ToJsonString());
File.Move(tmp, outPath, true);
Log($"Steam: {fetched.Count} skins priced ({unmatched} market items are not our skins, {ambiguousHits} skipped for shared names); {(full ? "complete" : "PARTIAL")} -> {outPath}");
return full ? 0 : 1;
