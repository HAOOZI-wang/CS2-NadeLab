using System.Text.Json;
using System.Text.Json.Nodes;

namespace NadeLab.Core;

public static class WorldIdentity
{
    // Legacy records have an outer Map field but no MapName inside WorldVersion.
    public static string? Key(JsonElement world,string map)
    {
        try
        {
            if(!MapCatalog.IsSupported(map) || world.ValueKind!=JsonValueKind.Object ||
                !world.TryGetProperty("GameVersion",out var game) || game.GetString() is not {Length:>0} version || version=="unknown" ||
                !world.TryGetProperty("MapBytes",out var bytes) || !bytes.TryGetInt64(out long length) || length<=0 ||
                !world.TryGetProperty("MapLastWriteUtc",out var time) || time.GetString() is not {Length:>0}) return null;
            if(world.TryGetProperty("MapName",out var inner) && inner.GetString()!=map) return null;
            var values=JsonNode.Parse(world.GetRawText())!.AsObject();values["MapName"]=map;
            var ordered=new JsonObject();
            foreach(var pair in values.OrderBy(pair=>pair.Key,StringComparer.Ordinal)) ordered[pair.Key]=pair.Value?.DeepClone();
            return ordered.ToJsonString();
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException) {return null;}
    }
    public static string? Key(string? world,string map)
    {
        if(string.IsNullOrWhiteSpace(world)) return null;
        try {using var doc=JsonDocument.Parse(world);return Key(doc.RootElement,map);}
        catch(JsonException) {return null;}
    }
    public static bool Matches(string? recordedMap,JsonElement recorded,string currentMap,JsonElement current)
    {
        if(recordedMap!=currentMap) return false;
        string? key=Key(current,currentMap);
        return key is not null && Key(recorded,currentMap)==key;
    }
    // The route library predates multi-map support and had no outer Map field.
    // Only that old format is assigned its original, sole supported map.
    public static string? RouteKey(string? world)
    {
        if(string.IsNullOrWhiteSpace(world)) return null;
        try
        {
            using var doc=JsonDocument.Parse(world);var root=doc.RootElement;
            string? map=root.TryGetProperty("MapName",out var name) ? name.GetString() : "de_mirage";
            return map is null ? null : Key(root,map);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException) {return null;}
    }
}
