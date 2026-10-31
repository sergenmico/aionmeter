// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2Zones.cs. Değiştirildi.
using System.IO;
using System.Text.Json;

namespace AionDPS.Aion2.Protocol;

/// <summary>What a zone id of the game's zone frame (0x2136) is: an open map or an instance, and its name as the game client shows it
/// (assets/aion2/zones/zones.json). Ids the file does not list are told apart by their range alone (6xxxxx are instances) and have no name.</summary>
public static class Aion2Zones
{
    // Değiştirildi: yalnız "instance mı" bilgisi tutulur (adlar ve arena sınamaları kaldırıldı).
    private static string FilePath => AssetPaths.Of("zones", "zones.json");
    private static Dictionary<int, bool>? _zones;

    /// <summary>Whether the zone is an instance (a dungeon you enter and leave) and not an open map.</summary>
    public static bool IsInstance(int zoneId) =>
        Table().TryGetValue(zoneId, out bool instance) ? instance : zoneId is >= 600000 and < 700000;

    private static Dictionary<int, bool> Table() => _zones ??= Load();

    private static Dictionary<int, bool> Load()
    {
        var table = new Dictionary<int, bool>();
        try
        {
            if (File.Exists(FilePath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                foreach (JsonElement zone in doc.RootElement.GetProperty("zones").EnumerateArray())
                {
                    table[zone.GetProperty("id").GetInt32()] = zone.GetProperty("kind").GetString() == "instance";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No zone file: the id range decides.
        }

        return table;
    }
}
