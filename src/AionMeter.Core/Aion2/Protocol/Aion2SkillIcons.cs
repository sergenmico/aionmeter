// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2SkillIcons.cs. Değiştirildi.
// Değiştirildi: yalnız belirteç (token) tablosu kaldı; dosya yolu, varlık denetimi ve ada göre arama kaldırıldı (simgeleri arayüz yükler).
using System.IO;
using System.Text.Json;

namespace AionDPS.Aion2.Protocol;

public static class Aion2SkillIcons
{
    private static string TokensPath => AssetPaths.Of("skills", "skill_icon_tokens.json");
    private static IReadOnlyDictionary<int, string>? _tokens;
    private static readonly object Gate = new();

    /// <summary>The icon token of a skill (skills/icons/&lt;token&gt;.png), keyed by skill id / 10000; null when the table has none.</summary>
    public static string? TokenFor(int skillId) =>
        skillId > 0 && Tokens().TryGetValue(skillId / 10000, out string? token) ? token : null;

    private static IReadOnlyDictionary<int, string> Tokens()
    {
        lock (Gate)
        {
            if (_tokens is { } cached)
            {
                return cached;
            }

            var table = new Dictionary<int, string>();
            try
            {
                if (File.Exists(TokensPath))
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TokensPath)) ?? new();
                    foreach ((string key, string token) in raw)
                    {
                        // An item-skill (a Theostone) names its icon with the item folder in front: "Item/ETC/Icon_Item_...".
                        if (int.TryParse(key, out int id))
                        {
                            table[id] = token.Contains('/') ? token[(token.LastIndexOf('/') + 1)..] : token;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // No table: no icons.
            }

            _tokens = table;
            return table;
        }
    }
}
