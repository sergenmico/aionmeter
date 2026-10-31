// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2Npcs.cs. Değiştirildi.
using System.IO;
using System.Text.Json;

namespace AionDPS.Aion2.Protocol;

/// <summary>
/// Every monster's name by the NPC id the game sends when it appears, read from the client's NpcData table and
/// its texts (assets/aion2/npcs/npc_names.json: {"2300709": {"en": "Talisra of the Void", "de": "..."}}, English,
/// German, French, Spanish and Russian). The bosses are in <see cref="Aion2BossCatalog"/> too; this covers the rest.
/// </summary>
public static class Aion2Npcs
{
    // Değiştirildi: npc_names.json'dan yalnız NPC kimlikleri okunur (adlar bellekte tutulmaz).
    private static string FilePath => AssetPaths.Of("npcs", "npc_names.json");
    private static string SummonFilePath => AssetPaths.Of("npcs", "player_summon_npcs.json");
    private static HashSet<int>? _table;
    private static HashSet<int>? _playerSummons;

    /// <summary>True for a monster the NPC table knows that is not one of the player summons (pet, trap, aura, spirit):
    /// a spawn of it that names a player names its target (a Blazing Totem naming the player it burns), not an owner.
    /// False for the player summons, and for ids neither list knows.</summary>
    public static bool IsMonsterNotPlayerSummon(int npcId)
    {
        var table = _table ??= Load();
        var summons = _playerSummons ??= LoadSummons();
        return summons.Count > 0 && table.Contains(npcId) && !summons.Contains(npcId);
    }

    /// <summary>True for a player summon (pet, trap, aura, spirit) of the game's NpcData; false for everything else and when the list is missing.</summary>
    public static bool IsPlayerSummon(int npcId) => (_playerSummons ??= LoadSummons()).Contains(npcId);

    private static HashSet<int> LoadSummons()
    {
        try
        {
            if (File.Exists(SummonFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SummonFilePath));
                return doc.RootElement.GetProperty("npcs").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No list: every named spawn stays an owner claim, as before.
        }

        return new HashSet<int>();
    }

    private static HashSet<int> Load()
    {
        var table = new HashSet<int>();
        try
        {
            if (File.Exists(FilePath))
            {
                // Only the top-level keys: the file is {"id": {"en": ..., "de": ...}, ...}; reading it as a stream keeps the names out of memory.
                byte[] bytes = File.ReadAllBytes(FilePath);
                var reader = new Utf8JsonReader(bytes);
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (int.TryParse(reader.GetString(), out int id))
                        {
                            table.Add(id);
                        }

                        reader.Skip();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // No table: monsters stay unnamed.
        }

        return table;
    }
}
