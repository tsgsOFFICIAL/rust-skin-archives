using System.Globalization;
using System.Text.Json.Nodes;

// docs/prices.json (version 2): a prices array per skin, one entry per market, plus when each source was last fetched.
// Shared by the Steam and SCMM fetchers so both merge into the same file without touching each other's entries.
static class PriceFile
{
    public const int Version = 2;

    // An older file (version 1, prices grouped per market) is not migrated: the file is rebuilt from what the run fetched.
    public static JsonObject Load(string path)
    {
        var old = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        var file = old?["version"]?.GetValue<int>() == Version ? old : new JsonObject();
        file["version"] = Version;
        file["sources"] ??= new JsonObject();
        file["items"] ??= new JsonObject();
        return file;
    }

    public static JsonObject Items(JsonObject file) => (JsonObject)file["items"]!;

    // Removes one market's entry from a skin's prices array; drops the skin when nothing is left.
    public static void RemoveEntry(JsonObject items, string id, string name)
    {
        if (items[id]?["prices"] is not JsonArray arr) { items.Remove(id); return; }
        for (int i = arr.Count - 1; i >= 0; i--)
            if (arr[i]?["name"]?.GetValue<string>() == name) arr.RemoveAt(i);
        if (arr.Count == 0) items.Remove(id);
    }

    // Removes every entry whose market is in names, from every skin (a finished run replaces a market completely).
    public static void RemoveMarkets(JsonObject items, ICollection<string> names)
    {
        foreach (var id in items.Select(kv => kv.Key).ToList())
            foreach (var name in names) RemoveEntry(items, id, name);
    }

    // Adds an entry, replacing an existing entry of the same market for that skin.
    public static void SetEntry(JsonObject items, string id, JsonObject entry)
    {
        string name = entry["name"]!.GetValue<string>();
        if (items[id] is not null) RemoveEntry(items, id, name);
        if (items[id]?["prices"] is JsonArray existing) existing.Add(entry);
        else items[id] = new JsonObject { ["prices"] = new JsonArray(entry) };
    }

    // Stable order (by skin id, and by market name within a skin) so the diff only shows real changes.
    public static void Save(JsonObject file, string path)
    {
        var items = Items(file);
        var sorted = new JsonObject();
        foreach (var kv in items.OrderBy(k => long.Parse(k.Key, CultureInfo.InvariantCulture)).ToList())
        {
            items.Remove(kv.Key);
            if (kv.Value?["prices"] is JsonArray arr)
            {
                var ordered = arr.Select(n => n!).OrderBy(n => n["name"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
                arr.Clear();
                foreach (var n in ordered) arr.Add(n);
            }
            sorted[kv.Key] = kv.Value;
        }
        file["items"] = sorted;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, file.ToJsonString());
        File.Move(tmp, path, true);
    }

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
