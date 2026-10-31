// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2SkillNames.cs. Değiştirildi.
using System.IO;
using System.Text.Json;

namespace AionDPS.Aion2.Protocol;

/// <summary>
/// Aion 2 skill id → English name, from assets/aion2/skills/skill_names.json ({"11010000": "Cleave", …}).
/// Aion 2 skill ids are 8 digits; the last four carry level/specialisation, so a lookup falls back
/// to the base id (id rounded down to a multiple of 10000) before giving up and returning the raw
/// number - a meter that shows "12040130" is more useful than one that shows nothing.
/// </summary>
public static class Aion2SkillNames
{
    // Değiştirildi: ad tablosu bellekte metin olarak tutulmuyor; her kimlik için yalnız meter'in baktığı ad türü (NameTag) saklanır.
    private static string FilePath => AssetPaths.Of("skills", "skill_names.json");
    private static IReadOnlyDictionary<int, byte>? _cache;

    /// <summary>Değiştirildi (eklendi): bir adın meter için anlamı (0 = önemsiz).</summary>
    private const byte TagNone = 0, TagSummonAttack = 1, TagSpiritAttack = 2, TagFireWallOrColdStorm = 3;

    private static byte TagOf(string name) =>
        name is "Divine Aura" or "Bittercold Wind" ? TagSummonAttack
        : name.Contains("Spirit:", StringComparison.Ordinal) && !name.StartsWith("Summon", StringComparison.Ordinal) ? TagSpiritAttack
        : name is "Fire Wall" or "Cold Storm" ? TagFireWallOrColdStorm
        : TagNone;

    private static IReadOnlyDictionary<int, byte> Load()
    {
        if (_cache is { } cached)
        {
            return cached;
        }

        var table = new Dictionary<int, byte>();
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
                foreach ((string key, string name) in raw)
                {
                    if (int.TryParse(key, out int id))
                    {
                        table[id] = TagOf(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Missing or broken table: names degrade to numbers, the meter keeps working.
        }

        _cache = table;
        return table;
    }

    private static string WatchlistPath => AssetPaths.Of("skills", "rdps_watchlist.json");
    private static IReadOnlySet<int>? _buffBases;

    /// <summary>True for ids that show up in damage frames but are not damage: consumable uses
    /// (item ids 2,000,000-2,999,999, e.g. "Life Potion" with an amount of 1) and the passive/party
    /// buffs listed in rdps_watchlist.json (e.g. "Experienced Counterstrike" with an amount of 7).
    /// The list's target debuffs are left out: they are cast on the target and hit it, and their
    /// hits are damage. A Cleric's Chain of Torment (21,818 and 4,316) and a Templar's Taunt (3,010)
    /// are exactly what the HP check found missing on four boss captures (2026-10-02/04), each one
    /// at 100.0 % once they are counted (98.8 / 99.5 / 99.5 / 99.6 % before).</summary>
    public static bool IsNonDamageEffect(int skillId)
    {
        if (skillId is >= 2_000_000 and < 3_000_000)
        {
            return true;
        }

        // The arena's own refills at the start of a round and between rounds (full HP and MP, cooldowns reset): nameless "System" skills
        // (1900908, 1900910, 1900911) whose frames look like damage - a player "hit" himself for his whole HP, up to half of what the meter showed
        // for the arena (recording of 2026-10-22, the tables say HpHealRatio / MpHealRatio).
        if (skillId is 1_900_908 or 1_900_910 or 1_900_911)
        {
            return true;
        }

        if (_buffBases is null)
        {
            var set = new HashSet<int>();
            try
            {
                if (File.Exists(WatchlistPath))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(WatchlistPath));
                    foreach (JsonElement buff in doc.RootElement.GetProperty("buffs").EnumerateArray())
                    {
                        if (buff.TryGetProperty("kind", out JsonElement kind) && kind.GetString() == "target_debuff")
                        {
                            continue;
                        }

                        set.Add(buff.GetProperty("skillId").GetInt32() / 10000 * 10000);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // No watchlist: only the consumable range is filtered.
            }

            _buffBases = set;
        }

        return _buffBases.Contains(skillId / 10000 * 10000);
    }

    private static string HealFamiliesPath => AssetPaths.Of("skills", "heal_skill_families.json");
    private static IReadOnlySet<int>? _healBases;

    /// <summary>Whether the skill belongs to a heal family (assets/aion2/skills/heal_skill_families.json,
    /// keyed by base id = id rounded down to a multiple of 10000, like <see cref="NameOf"/>).</summary>
    public static bool IsHealFamily(int skillId)
    {
        if (_healBases is null)
        {
            var set = new HashSet<int>();
            try
            {
                if (File.Exists(HealFamiliesPath))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(HealFamiliesPath));
                    if (doc.RootElement.TryGetProperty("heal", out JsonElement heal) && heal.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement e in heal.EnumerateArray())
                        {
                            if (e.TryGetInt32(out int id))
                            {
                                set.Add(id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // No table: nothing is classified as a heal, everything stays damage.
            }

            _healBases = set;
        }

        return _healBases.Contains(skillId / 10000 * 10000);
    }

    private static readonly string[] ClassByPrefix =
    {
        "Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Spiritmaster", "Cleric", "Chanter", "Brawler",
    };

    /// <summary>The class a player skill belongs to: its first two digits (11 = Gladiator ... 19 =
    /// Brawler, the order AionFlex's skills.json lists them in and the captured skills confirm).
    /// Null for ids outside the class ranges (NPC attacks, effects).</summary>
    public static string? ClassOf(int skillId)
    {
        int prefix = skillId / 1000000;
        return skillId >= 10000000 && prefix is >= 11 and <= 19 ? ClassByPrefix[prefix - 11] : null;
    }

    private static readonly string[] ClassById =
    {
        "Gladiator", "Templar", "Ranger", "Assassin", "Spiritmaster", "Sorcerer", "Cleric", "Chanter",
    };

    /// <summary>
    /// True for a summon's own attack, which only a summoned entity casts: a Spiritmaster spirit's
    /// "Fire Spirit: Leaping Slam" / "Ancient Spirit: Destruction" (the player's own casts are
    /// "Summon: ..." or have no "Spirit:" in their name), a Cleric's Divine Aura, a Sorcerer's
    /// Bittercold Wind.
    /// </summary>
    public static bool IsSummonAttack(int skillId) => NameTag(skillId) is TagSummonAttack or TagSpiritAttack;

    /// <summary>Değiştirildi (eklendi): upstream'deki <c>NameOf(skillId) is "Fire Wall" or "Cold Storm"</c> sınaması.</summary>
    public static bool IsFireWallOrColdStorm(int skillId) => NameTag(skillId) == TagFireWallOrColdStorm;

    /// <summary>
    /// The class whose summon casts this attack, or null when it is no summon attack. A summon's
    /// class skills carry the class in their id; a spirit's basic attack does not ("Fire Spirit:
    /// Basic Attack" is 100011-100018, "Ancient Spirit: Basic Attack" 100051 and 100055), so there
    /// the spirit in its name stands for the Spiritmaster.
    /// </summary>
    public static string? SummonAttackClass(int skillId) =>
        !IsSummonAttack(skillId) ? null
        : ClassOf(skillId) ?? (NameTag(skillId) == TagSpiritAttack ? "Spiritmaster" : null);

    /// <summary>
    /// The class in a character record's class code: <c>4 * class id + faction bit</c> (Gladiator 5/6,
    /// Templar 9/10, Ranger 13/14, Assassin 17/18, Spiritmaster 21/22, Sorcerer 25/26, Cleric 29/30,
    /// Chanter 33/34). Verified on four known characters; null for anything outside that pattern.
    /// </summary>
    /// <summary>The class id of a class code (4 * id + 1..4), 0 when the code is none.</summary>
    public static int ClassIdOfCode(int classCode) => classCode >= 1 ? (classCode - 1) / 4 : 0;

    /// <summary>The last part of a class code, 1..4: 1 and 2 on Elyos characters, 3 on Asmodian ones (seen on two; 4 is expected).</summary>
    public static int ClassBitsOfCode(int classCode) => classCode >= 1 ? (classCode - 1) % 4 + 1 : 0;

    /// <summary>The faction a class code points to: 1 and 2 are Elyos, 3 and 4 Asmodian (the profiles of the database: 4,630 of 4,640 Elyos have 1 or 2, 1,122 of 1,157 Asmodians have 3 or 4).
    /// Only for a character whose server is not known; null when the code is none.</summary>
    public static string? FactionOfCode(int classCode) => ClassBitsOfCode(classCode) switch { 1 or 2 => "Elyos", 3 or 4 => "Asmodian", _ => null };

    public static string? ClassFromCode(int classCode)
    {
        // 4 * class id + 1..4: the last part is faction and sex (1, 2 Elyos; 3, 4 Asmodian), so the class id is (code - 1) / 4 for all of them.
        int id = (classCode - 1) / 4;
        return classCode >= 1 && id is >= 1 and <= 8 ? ClassById[id - 1] : null;
    }

    /// <summary>
    /// The class in a party roster member's code: <c>4 * class id + 1..4</c> (Gladiator 5-8 ...
    /// Chanter 33-36). Verified on 15 members of three Draupnir/Krao Cave rosters (2026-10-02)
    /// against the classes their skills show: Gladiator 6, Templar 10, Elementalist 21/24,
    /// Sorcerer 26/27/28, Cleric 30/32, Chanter 34.
    /// </summary>
    public static string? ClassFromRosterCode(int code)
    {
        int id = (code - 1) / 4;
        return code >= 1 && id is >= 1 and <= 8 ? ClassById[id - 1] : null;
    }

    /// <summary>Değiştirildi: upstream NameOf ile aynı arama sırası (tam kimlik, temel kimlik, etki adları), ad yerine ad türü döner.</summary>
    private static byte NameTag(int skillId)
    {
        IReadOnlyDictionary<int, byte> table = Load();
        if (table.TryGetValue(skillId, out byte exact))
        {
            return exact;
        }

        int baseId = skillId / 10000 * 10000;
        if (table.TryGetValue(baseId, out byte byBase))
        {
            return byBase;
        }

        // an effect the skill table does not name: the client's own names of its effects (key = effect id * 10 + the level digit)
        IReadOnlyDictionary<long, byte> effects = LoadEffectNames();
        for (int level = 1; level <= 9; level++)
        {
            if (effects.TryGetValue(skillId * 10L + level, out byte effect))
            {
                return effect;
            }
        }

        return TagNone; // upstream: the raw number, which is no name of interest
    }

    private static IReadOnlyDictionary<long, byte>? _effectNames;

    private static IReadOnlyDictionary<long, byte> LoadEffectNames()
    {
        if (_effectNames is { } cached)
        {
            return cached;
        }

        var table = new Dictionary<long, byte>();
        try
        {
            string path = AssetPaths.Of("skills", "abnormal_names_en.json");
            if (File.Exists(path))
            {
                foreach ((string key, string name) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new())
                {
                    if (long.TryParse(key, out long id))
                    {
                        table[id] = TagOf(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // No table: effects without a skill name stay numbers.
        }

        _effectNames = table;
        return table;
    }

    // Değiştirildi (eklendi): metin adlar yalnız detay paneli açıldığında, ilk istekte yüklenir (upstream NameOf'un arama sırası).
    private static IReadOnlyDictionary<int, string>? _names;
    private static IReadOnlyDictionary<long, string>? _effectTexts;
    private static readonly object NamesGate = new();

    /// <summary>The English name, or null when neither table names the skill (upstream NameOf then shows the raw number).</summary>
    public static string? NameOf(int skillId)
    {
        IReadOnlyDictionary<int, string> table;
        IReadOnlyDictionary<long, string> effects;
        lock (NamesGate)
        {
            table = _names ??= ReadNames<int>(FilePath, k => int.TryParse(k, out int v) ? v : null);
            effects = _effectTexts ??= ReadNames<long>(AssetPaths.Of("skills", "abnormal_names_en.json"), k => long.TryParse(k, out long v) ? v : null);
        }

        if (table.TryGetValue(skillId, out string? exact))
        {
            return exact;
        }

        if (table.TryGetValue(skillId / 10000 * 10000, out string? byBase))
        {
            return byBase;
        }

        for (int level = 1; level <= 9; level++)
        {
            if (effects.TryGetValue(skillId * 10L + level, out string? effect))
            {
                return effect;
            }
        }

        return null;
    }

    private static Dictionary<T, string> ReadNames<T>(string path, Func<string, T?> key) where T : struct
    {
        var table = new Dictionary<T, string>();
        try
        {
            if (File.Exists(path))
            {
                foreach ((string k, string name) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new())
                {
                    if (key(k) is T id)
                    {
                        table[id] = name;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // No table: the caller shows the number.
        }

        return table;
    }
}
