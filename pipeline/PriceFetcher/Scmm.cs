using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

// Prices for many markets from SCMM (https://rust.scmm.app/docs), one request for the whole catalog:
//   GET https://api.scmm.app/api/item/prices?markets=A,B,C
// -> [{ nameHash, price, ..., prices: [{ marketType, price (USD cents), supply, isAvailable, url }] }]
// Free and open, limited to 10 requests/minute per IP. SCMM is a fan project, so this stays one request per run.
// Its urls carry SCMM's referral codes, so they are not stored: the site builds the links itself (docs/js/market-urls.js).
// Every run replaces all of these markets completely, so a skin that is delisted everywhere loses its old prices.
static class Scmm
{
    const int MinSkinsPriced = 1000;
    static readonly TimeSpan MaxOfferAge = TimeSpan.FromHours(24);

    // SCMM market id -> name shown on the site. Only markets listed here are stored.
    static readonly Dictionary<string, string> Markets = new()
    {
        ["SteamCommunityMarket"] = "Steam",
        ["Skinport"] = "Skinport",
        ["CSDeals"] = "CS.Deals",
        ["DMarket"] = "DMarket",
        ["RustTM"] = "Rust.tm",
        ["Waxpeer"] = "Waxpeer",
        ["LISSkins"] = "Lis-Skins",
        ["ManncoStore"] = "Mannco",
        ["AvanMarket"] = "Avan Market",
        ["CSTrade"] = "CS.Trade",
        ["ShadowPay"] = "ShadowPay",
        ["SwapGGTrade"] = "Swap.gg",
        ["TradeitGG"] = "Tradeit.gg",
        ["SkinSwap"] = "SkinSwap",
        ["RapidSkins"] = "RapidSkins",
        ["LootFarm"] = "Loot.farm",
    };

    public static async Task<int> Run(Dictionary<string, List<string>> byName, string outPath, Action<string> log)
    {
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RustSkinViewerPrices/1.0");

        string url = "https://api.scmm.app/api/item/prices?markets=" + string.Join(",", Markets.Keys);
        JsonDocument doc;
        try
        {
            using var res = await http.GetAsync(url);
            if (!res.IsSuccessStatusCode) { log($"SCMM answered HTTP {(int)res.StatusCode}; leaving the file untouched."); return 2; }
            doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log($"SCMM request failed ({ex.GetType().Name}: {ex.Message}); leaving the file untouched.");
            return 2;
        }

        // skin id -> market name -> entry, so a market listed twice for one name keeps its cheapest offer
        var found = new Dictionary<string, Dictionary<string, JsonObject>>();
        int rows = 0, unmatched = 0, ambiguous = 0, skippedOffers = 0, staleOffers = 0;
        var now = DateTimeOffset.UtcNow;
        var perMarket = Markets.Values.ToDictionary(n => n, _ => 0);

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            rows++;
            string name = row.TryGetProperty("nameHash", out var nh) ? nh.GetString() ?? "" : "";
            if (!byName.TryGetValue(name, out var ids)) { unmatched++; continue; }
            if (ids.Count > 1) { ambiguous++; continue; }
            if (!row.TryGetProperty("prices", out var offers) || offers.ValueKind != JsonValueKind.Array) continue;

            foreach (var o in offers.EnumerateArray())
            {
                string market = o.TryGetProperty("marketType", out var mt) ? mt.GetString() ?? "" : "";
                if (!Markets.TryGetValue(market, out var display)) continue;
                bool available = !o.TryGetProperty("isAvailable", out var av) || av.ValueKind != JsonValueKind.False;
                int cents = o.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? (int)Math.Round(p.GetDouble()) : 0;
                if (!available || cents <= 0) { skippedOffers++; continue; }

                // SCMM keeps serving a market's last prices after it stops updating them (Loot.farm and SkinSwap were
                // over a week old), so an offer it hasn't seen recently is dropped instead of shown as current.
                if (o.TryGetProperty("observedAt", out var oa) && DateTimeOffset.TryParse(oa.GetString(), out var seen) && now - seen > MaxOfferAge)
                {
                    staleOffers++;
                    continue;
                }

                var entry = new JsonObject
                {
                    ["name"] = display,
                    ["price"] = cents,
                    ["listings"] = o.TryGetProperty("supply", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
                };

                if (!found.TryGetValue(ids[0], out var byMarket)) found[ids[0]] = byMarket = new();
                if (byMarket.TryGetValue(display, out var existing) && existing["price"]!.GetValue<int>() <= cents) continue;
                byMarket[display] = entry;
            }
        }

        foreach (var byMarket in found.Values) foreach (var m in byMarket.Keys) perMarket[m]++;
        int total = perMarket.Values.Sum();
        // a run replaces every market, so a truncated response must not wipe the file (a normal run prices ~5000 skins)
        if (found.Count < MinSkinsPriced) { log($"SCMM only priced {found.Count} skins (expected at least {MinSkinsPriced}); leaving the file untouched."); return 2; }

        var file = PriceFile.Load(outPath);
        var items = PriceFile.Items(file);
        PriceFile.RemoveMarkets(items, Markets.Values);   // a finished run replaces these markets completely
        foreach (var (id, byMarket) in found)
            foreach (var entry in byMarket.Values) PriceFile.SetEntry(items, id, entry);

        var sources = (JsonObject)file["sources"]!;
        sources.Remove("steam");   // from when Steam had its own fetcher
        sources["scmm"] = new JsonObject { ["fetchedAt"] = PriceFile.Now(), ["complete"] = true };
        PriceFile.Save(file, outPath);

        log($"SCMM: {rows} catalog rows, {found.Count} of our skins priced, {total} offers stored ({unmatched} rows are not our skins, {ambiguous} skipped for shared names, {skippedOffers} unavailable offers, {staleOffers} not updated in {MaxOfferAge.TotalHours:0}h) -> {outPath}");
        foreach (var kv in perMarket.OrderByDescending(k => k.Value)) log($"  {kv.Key,-12} {kv.Value}");
        return 0;
    }
}
