using System.Text.Json;

// fetches prices into docs/prices.json (loaded by the site next to skins.json)
//   dotnet run --project pipeline/PriceFetcher -c Release -- --skins docs/skins.json --out docs/prices.json
//
// All prices come from SCMM in one request (see Scmm.cs): Steam, Skinport, CS.Deals, DMarket, Waxpeer, ...
// Only needs skins.json (id + displayName), so it runs on a github action.
//
// prices.json has a prices array per skin, one entry per market:
//   { "version": 2,
//     "sources": { "scmm": { "fetchedAt": "...", "complete": true } },
//     "items": { "<skinId>": { "prices": [
//         { "name": "Steam", "price": 1624, "listings": 16 } ] } } }
// "price" is USD cents (lowest listing). "sources" only tracks when each source was last fetched.
// There are no links in the file: the site builds them from the skin name (docs/js/market-urls.js).
// Skins are matched by exact name, names shared by several skins are skipped.
//
// exit code: 0 = prices written, 2 = nothing written (SCMM unreachable or returned no usable offers)

string skinsPath = "docs/skins.json", outPath = "docs/prices.json";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--skins": skinsPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
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

return await Scmm.Run(byName, outPath, Log);
