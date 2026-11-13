// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2FrameDecoder.cs. Değiştirildi.
using System.Buffers.Binary;
using System.Text;
using AionDPS.Combat;
using AionDPS.Combat.Sources;

namespace AionDPS.Aion2.Protocol;

/// <summary>
/// Turns one reassembled game frame into meter events, purely by the offsets in
/// <see cref="Aion2Protocol"/>. Knows nothing the JSON does not say: an opcode outside the tables
/// is skipped, a frame shorter than a field it needs is skipped and counted, never guessed.
/// </summary>
public sealed partial class Aion2FrameDecoder
{
    // İsteğe bağlı yerel tanı eklentisi: gövdesi yoksa derleyici bu çağrıyı siler. Paket çerçevesi açıldıktan sonraki her iç çerçeve için çağrılır.
    partial void YerelCerceve(ReadOnlySpan<byte> frame, int opcode, DateTime at);

    private readonly Aion2Protocol _protocol;
    private readonly Aion2EntityDirectory _entities;

    // For telling apart the summons of two players of one class (see GuessSummonOwner): when each
    // entity appeared, and when each player last cast each skill variant (skill id / 10).
    private readonly Dictionary<int, DateTime> _spawnedAt = new();
    private readonly Dictionary<(int Caster, int Variant), DateTime> _lastCasts = new();

    // A shield's share of the blow a monster (actor) is striking a player (target) with, waiting for that blow's hit
    // frame (see DecodeVarintDot).
    private readonly Dictionary<(int Actor, int Target), (DateTime At, long Amount)> _shieldShares = new();


    public Aion2FrameDecoder(Aion2Protocol protocol, Aion2EntityDirectory entities)
    {
        _protocol = protocol;
        _entities = entities;
    }

    /// <summary>Değiştirildi (eklendi): saha bossu listesinin (0x0191) yazıldığı depo; denetimde ayrı bir depo verilir.</summary>
    public AionMeter.Core.SahaBossDeposu SahaBosslari { get; set; } = AionMeter.Core.SahaBossDeposu.Ortak;

    private readonly List<SahaBossKaydi> _sahaKayitlar = new();

    /// <summary>The zone frame: <c>opcode | counter u32 | zone id u32 | position ...</c>. Sent at login and on every map or instance change
    /// (recordings 2026-10-11: 1010 Verteron, 600001 Krao Cave as an instance).</summary>
    private const int ZoneOpcode = 0x2136;

    /// <summary>A skill that is being wound up: caster, skill, aim and the time the cast takes (see <see cref="DecodeWindup"/>).</summary>
    private const int WindupOpcode = 0x0938;

    public string CurrentZone { get; private set; } = "";

    /// <summary>The zone id of the last zone frame (0 until one arrived).</summary>
    public int CurrentZoneId { get; private set; }

    private readonly List<ZoneChange> _zoneChanges = new();

    /// <summary>The zone changes decoded since the last call, oldest first. A repeat of the zone the player is already in
    /// (a reconnect) is not a change.</summary>
    public IReadOnlyList<ZoneChange> DrainZoneChanges() => Drain(_zoneChanges);

    private void DecodeZone(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        if (frame.Length < 10)
        {
            return;
        }

        int zoneId = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[6..]));
        if (zoneId <= 0)
        {
            return;
        }


        // The place the frame puts the player (x, y as floats at 18 and 22), when it is long enough to hold them.
        (float X, float Y)? place = frame.Length >= 26
            ? (BinaryPrimitives.ReadSingleLittleEndian(frame[18..]), BinaryPrimitives.ReadSingleLittleEndian(frame[22..]))
            : null;
        // What the frame was for (u32 at 34): 0 for the first one of a visit, 2 for a move inside the zone (the next run's entrance, a cut scene's door),
        // 4 for a revival at an obelisk or kisk (seen in the recordings; a kisk can stand anywhere, so only a move can start a run).
        uint kind = frame.Length >= 38 ? BinaryPrimitives.ReadUInt32LittleEndian(frame[34..]) : 0;
        KonumBolge(place, yeniBolge: zoneId != CurrentZoneId); // Değiştirildi (eklendi): oyuncunun yeri (Gizli Küp yönü)

        if (zoneId == CurrentZoneId)
        {
            // The same zone again: a reconnect, a respawn or a move inside the instance - or the next run of an instance, entered straight from the run
            // before, which puts the player back at the entrance, where the first frame of the stay did (the entrances of runs 1 to 4 of one temple lay within 800 units of each other; the other frames of the stay, a stage and a boss room, 9,000 and more away).
            if (kind == ZoneKindMove && place is { } now && _entryPlace is { } entry && Aion2Zones.IsInstance(zoneId)
                && timestamp - _entryAt >= RestartMinGap && Math.Sqrt(Math.Pow(now.X - entry.X, 2) + Math.Pow(now.Y - entry.Y, 2)) < RestartRadius)
            {
                _entryAt = timestamp;
                _zoneChanges.Add(new ZoneChange(timestamp, zoneId, Restart: true));
            }

            return;
        }

        CurrentZoneId = zoneId;
        ForgetZone(timestamp); // Değiştirildi: yeni bölge, eski bölgenin varlık kimlikleri geçersiz (RAM sınırı)
        _entryPlace = place;
        _entryAt = timestamp;
        _zoneChanges.Add(new ZoneChange(timestamp, zoneId));
    }

    /// <summary>Where the first zone frame of the current stay put the player, and when.</summary>
    private (float X, float Y)? _entryPlace;
    private DateTime _entryAt;

    /// <summary>A repeat of the zone frame at the entrance counts as a new run when this long after the last start, and within this many units of the first frame's place.</summary>
    private static readonly TimeSpan RestartMinGap = TimeSpan.FromSeconds(90);
    private const double RestartRadius = 2000;
    private const uint ZoneKindMove = 2;

    /// <summary>Değiştirildi (eklendi): bölge değişince bölgeye bağlı tablolar boşaltılır; varlık kimlikleri yeni bölgede yeniden dağıtılır.</summary>
    /// Oyun bir bölgenin oyuncularını/canavarlarını bölge çerçevesinden hemen önce de duyurabildiği için son <see cref="ZoneKeepWindow"/>
    /// içinde doğanlar tutulur.
    private void ForgetZone(DateTime at)
    {
        var keep = _spawnedAt.Where(kv => at - kv.Value <= ZoneKeepWindow).Select(kv => kv.Key).ToHashSet();
        foreach (int old in _spawnedAt.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _spawnedAt.Remove(old);
        }

        foreach (var old in _lastCasts.Where(kv => at - kv.Value > ZoneKeepWindow).Select(kv => kv.Key).ToList())
        {
            _lastCasts.Remove(old);
        }

        _shieldShares.Clear();
        _pendingSpawned.Clear();
        _pendingLeftovers.Clear();
        _power.Clear();
        _effectTargets.Clear();
        _entities.ForgetZone(keep, at);
    }

    private static readonly TimeSpan ZoneKeepWindow = TimeSpan.FromSeconds(15);

    /// <summary>Değiştirildi (eklendi): bir bölgede uzun kalındığında tabloların sınırı.</summary>
    private const int MaxZoneTable = 4096;

    public int SkippedShortFrames { get; private set; }
    public int UnknownOpcodes { get; private set; }

    /// <summary>Damage-opcode frames without a damage block (see <see cref="DecodeVarintDamage"/>).</summary>
    public int NoDamageFrames { get; private set; }

    // Değiştirildi: tanı amaçlı ServerIdsSeen tablosu kaldırıldı.
    private void NoteServerId(int serverId, string name, bool inView = false)
    {
        if (inView)
        {
            _entities.NoteServerOfNameInView(name, serverId); // his own frame: no list entry of a namesake replaces it
            return;
        }

        _entities.NoteServerOfName(name, serverId); // a player's faction follows from his server
    }

    public int Bundles { get; private set; }
    public int BundleFailures { get; private set; }

    // Değiştirildi: dünya boss'u tahmini (hit notice) kaldırıldı; sadece çerçeve çözülür.
    // Değiştirildi: olaylar ve bölge değişimleri çerçeve sırasıyla (paket çerçevesinin içinde de her iç çerçeveden sonra) verilir.
    public void Decode(ReadOnlySpan<byte> frame, DateTime timestamp, Action<DamageEvent> onHit, Action<ZoneChange> onZone)
    {
        _onHit = onHit;
        _onZone = onZone;
        try
        {
            foreach (DamageEvent ev in DecodeFrame(frame, timestamp, nested: false))
            {
                onHit(ev);
            }

            FlushZones();
        }
        finally
        {
            _onHit = null;
            _onZone = null;
        }
    }

    private Action<DamageEvent>? _onHit;
    private Action<ZoneChange>? _onZone;

    private void FlushZones()
    {
        if (_onZone is { } onZone && _zoneChanges.Count > 0)
        {
            foreach (ZoneChange zone in Drain(_zoneChanges))
            {
                onZone(zone);
            }
        }
    }

    // Değiştirildi: sadece hasar ölçer ve boss canı için gereken dallar bırakıldı (bilet, ganimet, tarif, sıralama, lejyon,
    // bekleme süresi, alan bossları, zamanlayıcı, sunucu saati, ekipman, beceri listeleri, evcil hayvan, konum, buff, sersemletme,
    // hedef değişimi, öldürme/kaçınma olayları çıkarıldı). Alan bosslarının liste çerçevesi (0x0191) sonradan geri eklendi.
    private IEnumerable<DamageEvent> DecodeFrame(ReadOnlySpan<byte> frame, DateTime timestamp, bool nested)
    {
        FrameLayout layout = _protocol.FrameLayout;
        if (frame.Length < layout.OpcodeOffset + layout.OpcodeSize)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        int opcode = (int)ReadUnsigned(frame, new FieldSpec(layout.OpcodeOffset, layout.OpcodeSize), layout.LittleEndian && !layout.OpcodeBigEndian);
        if (_protocol.BundleOpcode is int bundleOpcode && opcode == bundleOpcode && !nested)
        {
            return DecodeBundle(frame, timestamp);
        }

        YerelCerceve(frame, opcode, timestamp);

        if ((opcode & 0xFF) == 0xE2)
        {
            NoteNamedServers(frame); // not final: the frame is read by whatever else knows its opcode
        }

        if (opcode == ZoneOpcode)
        {
            DecodeZone(frame, timestamp);
            return Array.Empty<DamageEvent>();
        }

        if (opcode == WindupOpcode)
        {
            DecodeWindup(frame, timestamp);
            return Array.Empty<DamageEvent>();
        }

        if (opcode == Aion2SahaBosslari.Opcode)
        {
            // Değiştirildi (geri eklendi): haritanın saha bossu listesi; bu opcode'un liste gibi okunmayan çerçeveleri atlanır.
            if (Aion2SahaBosslari.TryParse(frame, out int harita, _sahaKayitlar))
            {
                SahaBosslari.Guncelle(harita, _sahaKayitlar, timestamp);
            }

            return Array.Empty<DamageEvent>();
        }

        if (KonumCercevesi(frame, opcode))
        {
            return Array.Empty<DamageEvent>(); // Değiştirildi (geri eklendi): konum, hareket, etki çerçevelerinden yalnız yerler (Aion2FrameDecoder.Konum.cs)
        }

        OpcodeFamily family = _protocol.FamilyOf(opcode);
        IReadOnlyDictionary<string, FieldSpec> fields = _protocol.FieldsOf(family);

        switch (family)
        {
            case OpcodeFamily.Damage when string.Equals(_protocol.DamageLayout, "varint-v1", StringComparison.Ordinal):
                return DecodeVarintDamage(frame, timestamp);
            case OpcodeFamily.HpUpdate when string.Equals(_protocol.HpLayout, "varint-v1", StringComparison.Ordinal):
                DecodeHp(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Dot when string.Equals(_protocol.DotLayout, "varint-v1", StringComparison.Ordinal):
                return DecodeVarintDot(frame, timestamp);
            case OpcodeFamily.Damage:
            case OpcodeFamily.Dot:
            case OpcodeFamily.Heal:
                return DecodeAmount(frame, timestamp, fields, isHeal: family == OpcodeFamily.Heal, layout.LittleEndian);
            case OpcodeFamily.Nickname when string.Equals(_protocol.NicknameLayout, "varint-v1", StringComparison.Ordinal):
                DecodeVarintNickname(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Roster:
                DecodeRoster(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.TargetSelect:
                DecodeTargetSelect(frame);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Character:
                DecodeCharacter(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Appearance:
                DecodeAppearance(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.PlayerList:
                DecodePlayerList(frame);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.NpcSpawn:
                DecodeNpcSpawn(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.NpcRemove:
                DecodeNpcRemove(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Nickname:
                DecodeNickname(frame, fields, layout.LittleEndian);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.EnvSpawn: // Değiştirildi: env çerçevelerinden sadece Gizli Küp okunur
                DecodeEnvSpawn(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.EnvRemove:
                DecodeEnvRemove(frame, timestamp);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.EnvOpenStart:
                DecodeEnvOpenStart(frame);
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Session:
                if (TryReadInt(frame, fields, "localPlayerId", layout.LittleEndian, out long localId))
                {
                    _entities.LocalPlayerId = (int)localId;
                }
                return Array.Empty<DamageEvent>();
            case OpcodeFamily.Zone:
                if (TryReadString(frame, fields, "zoneName", layout.LittleEndian, out string? zone))
                {
                    CurrentZone = zone;
                }
                return Array.Empty<DamageEvent>();
            default:
                UnknownOpcodes++;
                return Array.Empty<DamageEvent>();
        }
    }

    private static IReadOnlyList<T> Drain<T>(List<T> list)
    {
        if (list.Count == 0)
        {
            return Array.Empty<T>();
        }

        var drained = list.ToArray();
        list.Clear();
        return drained;
    }

    private IEnumerable<DamageEvent> DecodeAmount(ReadOnlySpan<byte> frame, DateTime timestamp, IReadOnlyDictionary<string, FieldSpec> fields, bool isHeal, bool littleEndian)
    {
        if (!TryReadInt(frame, fields, "sourceId", littleEndian, out long sourceId)
            || !TryReadInt(frame, fields, "targetId", littleEndian, out long targetId)
            || !TryReadInt(frame, fields, "amount", littleEndian, out long amount))
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        string? skill = null; // Değiştirildi: beceri adı gerekmiyor
        bool critical = false;
        if (fields.TryGetValue("flags", out FieldSpec? flagsSpec) && flagsSpec.Mask is string mask
            && TryReadInt(frame, fields, "flags", littleEndian, out long flags))
        {
            critical = (flags & Convert.ToInt64(mask, 16)) != 0;
        }

        return new[] { new DamageEvent(timestamp, (int)sourceId, (int)targetId, amount, isHeal, skill, critical) };
    }

    /// <summary>
    /// A bundle frame: opcode | u32 LE uncompressed size | LZ4 block. The block is a run of ordinary
    /// frames in the same varint framing (verified: all of a real capture's bundles split cleanly),
    /// so each one is decoded exactly like a top-level frame. A bundle inside a bundle is not seen
    /// in practice and is not followed.
    /// </summary>
    private List<DamageEvent> DecodeBundle(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        var events = new List<DamageEvent>();
        FrameLayout layout = _protocol.FrameLayout;
        int headerEnd = layout.OpcodeOffset + layout.OpcodeSize + 4;
        if (!layout.IsVarint || frame.Length <= headerEnd)
        {
            BundleFailures++;
            return events;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(frame[(layout.OpcodeOffset + layout.OpcodeSize)..]);
        if (size > Aion2Lz4.MaxOutput || !Aion2Lz4.TryDecompress(frame[headerEnd..], (int)size, out byte[] raw))
        {
            BundleFailures++;
            return events;
        }

        Bundles++;
        int p = 0;
        while (p < raw.Length)
        {
            long length = 0;
            int prefix = 0;
            bool complete = false;
            while (prefix < 5 && p + prefix < raw.Length)
            {
                byte b = raw[p + prefix];
                length |= (long)(b & 0x7f) << (7 * prefix);
                prefix++;
                if ((b & 0x80) == 0)
                {
                    complete = true;
                    break;
                }
            }

            long total = length + prefix + layout.LengthBias;
            if (!complete || total - prefix < layout.OpcodeOffset + layout.OpcodeSize || p + total > raw.Length)
            {
                BundleFailures++;
                break;
            }

            IEnumerable<DamageEvent> inner = DecodeFrame(raw.AsSpan(p + prefix, (int)total - prefix), timestamp, nested: true);
            if (_onHit is { } onHit)
            {
                foreach (DamageEvent ev in inner)
                {
                    onHit(ev);
                }

                FlushZones();
            }
            else
            {
                events.AddRange(inner);
            }
            p += (int)total;
        }

        return events;
    }

    private const int DodgeSkillId = 11000100;

    /// <summary>Some frames carry a 250,000,000-style placeholder instead of a real hit (seen on an
    /// NPC skill in the reference capture); nothing a player deals comes near it.</summary>
    private const long MaxPlausibleAmount = 100_000_000;

    /// <summary>
    /// The Aion 2 damage frame, as verified against a real capture and the in-game combat log
    /// (2026-09-30): opcode(2) | target id (varint) | 2 flag bytes | actor id (varint) | skill id
    /// (u32 LE, the decimal skill number) | sequence(1) | hit type(1: 2 = normal, 3 = critical) |
    /// variable block | 4-byte hit count (1..9) | 2 bytes | damage (varint) | extra-hit counters.
    /// The variable block is skipped by looking for the hit count; every frame of the reference
    /// capture (5,779 + 931 + 11,000 frames) carries one.
    /// </summary>
    /// <summary>
    /// "A monster appears": entity id (varint), three type bytes, then the monster's NPC id as a
    /// little-endian uint32 (verified on a Krao Cave run: 2300104 = Enhanced Harcon). Only boss ids
    /// are kept - see <see cref="Aion2BossCatalog"/>.
    /// </summary>
    /// <summary>
    /// "A monster disappears": opcode | entity id (varint) | 00 | reason. Reason 3 follows a monster's
    /// death (a few seconds after its hit points read 0), 7 a monster that vanished alive - the
    /// instance was closed because the group took too long (Talisra, 2026-10-07: back to full health,
    /// gone 65 s later). Only a boss entity's reason is of use (see Aion2EntityDirectory.RemovalOf).
    /// </summary>
    private void DecodeNpcRemove(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long entityId) || frame.Length != p + 2)
        {
            return;
        }

        _entities.NoteRemoved(unchecked((int)entityId), frame[^1], timestamp);
        _entities.Konum.Unut(unchecked((int)entityId));
        _entities.Combat.NoteGone(unchecked((int)entityId), timestamp);
        _entities.Combat.ForgetEntity(unchecked((int)entityId));
    }

    private void DecodeNpcSpawn(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long entityId) || frame.Length < p + 7)
        {
            return;
        }

        if (_spawnedAt.Count >= MaxZoneTable)
        {
            foreach (int old in _spawnedAt.Where(kv => timestamp - kv.Value > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
            {
                _spawnedAt.Remove(old);
            }
        }

        _spawnedAt[unchecked((int)entityId)] = timestamp;
        _power.Remove(unchecked((int)entityId));
        _effectTargets.Remove(unchecked((int)entityId)); // the id is reused: earlier votes belong to something else

        // Two type bytes, then a flag: 1 = the entity carries a name (a summon's owner, e.g. a
        // Cleric's Divine Aura announced as "Psefon"), length-prefixed, before the NPC id.
        string? ownerName = null;
        if (frame[p + 2] == 1 && TryReadName(frame, p + 3, out string named, minLength: 2))
        {
            ownerName = named;
            p += 1 + frame[p + 3]; // the length prefix counts bytes (ë is two), not characters
            if (frame.Length < p + 7)
            {
                return;
            }
        }

        p += 3;
        int npcId = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]));
        KonumDogus(frame, p, entityId); // Değiştirildi (eklendi): canavarın yeri, oyuncunun yer tahmini için
        _entities.NoteSpawned(unchecked((int)entityId));
        _entities.RegisterNpc(unchecked((int)entityId), npcId);
        _entities.HitPoints.NoteSpawn(unchecked((int)entityId), ReadSpawnHitPoints(frame, p + 4));
        if (ownerName is not null && Aion2Npcs.IsMonsterNotPlayerSummon(npcId))
        {
            ownerName = null; // a monster naming the player it targets (Blazing Totem), not that player's summon
        }

        _entities.SetSummonOwnerName(unchecked((int)entityId), ownerName);

        // Further on: eight FF bytes, eight more bytes, then the owner's id (varint). An ordinary
        // monster names itself there; a summoned spirit names the player who summoned it (verified on
        // three Krao Cave / Urugugu captures, 2026-10-02: all 161 spirits resolved to the
        // Spiritmaster casting their "Summon:" skills, three Spiritmasters in one party kept apart).
        // A Cleric's Divine Aura names itself here, and its owner by name instead (above).
        int marker = frame[(p + 4)..].IndexOf(OwnerMarker);
        int q = marker < 0 ? -1 : p + 4 + marker + OwnerMarker.Length + 8;
        // Some monsters' spawns carry no owner block at all: a spawn is a new entity all the same, so an owner
        // from the id's previous life goes (a world boss add once took a stale owner and showed as a player).
        int? summoner = q > 0 && q < frame.Length && TryReadVarint(frame, ref q, out long owner) && owner > 0 && owner != entityId
            ? unchecked((int)owner)
            : null;
        _entities.SetSummonOwner(unchecked((int)entityId), summoner);
    }

    /// <summary>
    /// The hit points in a spawn frame: 21 bytes behind the NPC code (33 in the "extended" state) come two varints, current and maximum
    /// (current not above maximum), then exactly <c>64 00 00 00 64 00 00 00</c>. The trailer is what keeps other bytes from passing as
    /// hit points. Verified on recordings: Raur 5,220,000, Kromede's Desire 15,504,000, Ultimate Berk 123,000 (exploration, one player).
    /// </summary>
    private static (long Current, long Max)? ReadSpawnHitPoints(ReadOnlySpan<byte> frame, int afterCode)
    {
        ReadOnlySpan<byte> trailer = stackalloc byte[] { 0x64, 0, 0, 0, 0x64, 0, 0, 0 };
        foreach (int skip in stackalloc[] { 21, 33 })
        {
            int q = afterCode + skip;
            if (q >= frame.Length)
            {
                return null;
            }

            if (TryReadVarint(frame, ref q, out long current) && TryReadVarint(frame, ref q, out long max)
                && current >= 0 && max > 0 && current <= max && frame.Length >= q + 8 && frame.Slice(q, 8).SequenceEqual(trailer))
            {
                return (current, max);
            }
        }

        return null;
    }

    /// <summary>
    /// A summon whose spawn names no owner, neither by id nor by name (a Sorcerer's Bittercold Wind):
    /// an entity the server announced as a monster that casts a class's skills is somebody's
    /// summon, and when exactly one member of the party plays that class, it is theirs. Remembered
    /// once found. With two players of the class nothing is guessed.
    /// <para>Only a direct hit on a monster counts. Damage-over-time frames name a class skill next
    /// to a monster too: a Sorcerer's Steel Barrier absorbing a monster's blow reads "monster X,
    /// effect Steel Barrier, on the Sorcerer" - which once made a boss's add (Phantasmal Lakshmi)
    /// the party Sorcerer's summon, its blows on the party his damage (Draupnir capture,
    /// 2026-10-02).</para>
    /// <para>Two players of the class: the summon strikes with the variant of the skill its owner
    /// cast (skill id / 10 - talents pick the variant), and its owner cast it just before it
    /// appeared. On a Draupnir run with two Sorcerers (2026-10-02 23:00), all 23 Bittercold Winds
    /// fit both: Lumy cast 15280240 and her winds hit with 15280242/3, Aurulio cast 15280030 and
    /// his hit with 15280032/3, each cast ~50 ms before the spawn. The caster of that variant
    /// closest before the spawn is the owner - by id, so it works before the players are named.</para>
    /// </summary>
    private int? GuessSummonOwner(int actor, int skillId, int target)
    {
        // The target is no player and no summon. Not IsKnownMonster: started mid-fight, the meter
        // never saw the boss spawn.
        if (!_entities.IsSpawned(actor) || _entities.IsKnownPlayer(target) || _entities.SummonOwnerOf(target) is not null
            || Aion2SkillNames.ClassOf(skillId) is not string className)
        {
            return null;
        }

        // Değiştirildi (eklendi): doğuşu sahibini adıyla taşıyorsa tahmin yapılmaz; ad bir kimliğe bağlanınca vuruşlar sahibine geçer
        // (Register -> DrainResolvedOwners). Yabancının adı henüz gelmemiş rüzgârı "partideki tek Sorcerer" kuralıyla bize yazılıyordu.
        if (_entities.SummonOwnerNameOf(actor) is not null)
        {
            return null;
        }

        // A party member of the class who cast this variant just before (within 5 s); else any caster of
        // it within 2 s - a party member not named yet (the meter started inside a dungeon) or a player
        // outside the party, whose summon is theirs too (at a world boss the crowd is many small groups;
        // refused, each Bittercold Wind showed as a player of its own); else the party's only player of
        // the class.
        int variant = skillId / 10;
        var party = _entities.PartyMemberIdsOfClass(className).Where(id => id != actor).ToList();
        int? owner = OwnerByCast(actor, variant, TimeSpan.FromSeconds(5), id => party.Contains(id))
            ?? OwnerByCast(actor, variant, TimeSpan.FromSeconds(2), _ => true);
        if (owner is null && party.Count == 1)
        {
            // Değiştirildi: açık dünyada (zindan dışında) yabancıların çağrıları da etraftadır; tek aday yalnız güç çarpanı kanıtıyla kabul edilir.
            owner = SingleCandidateAllowed() ? party[0] : OwnersByPower(actor, party, strict: true) is [int proven] ? proven : null;
        }

        if (owner is int found)
        {
            _pendingSpawned.Remove(actor);
            _entities.SetSummonOwner(actor, found);
        }
        else if (party.Count == 0 && _pendingSpawned.Count < 64)
        {
            // nobody of the class is known in the party yet (their own casts have not been seen): try again with the next hits
            _pendingSpawned[actor] = className;
        }

        return owner;
    }

    /// <summary>Spawned summons without an owner whose class had no known party member yet (a Sorcerer's wind before the Sorcerer was read): handed over once exactly one is known.</summary>
    private readonly Dictionary<int, string> _pendingSpawned = new();

    /// <summary>The caster accepted by <paramref name="eligible"/> who cast this variant of the
    /// summon's skill closest before it spawned, within <paramref name="window"/> (a Sorcerer
    /// summons a wind every ten seconds or more).</summary>
    private int? OwnerByCast(int summon, int variant, TimeSpan window, Func<int, bool> eligible)
    {
        if (!_spawnedAt.TryGetValue(summon, out DateTime spawned))
        {
            return null;
        }

        var justBefore = _lastCasts
            .Where(kv => kv.Key.Variant == variant && kv.Key.Caster != summon && eligible(kv.Key.Caster))
            .Select(kv => (Id: kv.Key.Caster, Gap: spawned - kv.Value))
            .Where(c => c.Gap >= TimeSpan.Zero && c.Gap <= window)
            .OrderBy(c => c.Gap)
            .ToList();
        return justBefore.Count > 0 ? justBefore[0].Id : null;
    }

    /// <summary>A player's cast of a class skill, remembered for <see cref="OwnerByCast"/>.</summary>
    private void NoteCast(int actor, int skillId, DateTime timestamp)
    {
        if (!_entities.IsSpawned(actor) && Aion2SkillNames.ClassOf(skillId) is not null)
        {
            if (_lastCasts.Count >= MaxZoneTable)
            {
                foreach (var old in _lastCasts.Where(kv => timestamp - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList())
                {
                    _lastCasts.Remove(old);
                }
            }

            _lastCasts[(actor, skillId / 10)] = timestamp;
        }
    }

    /// <summary>
    /// How a hit landed, from the bytes after its damage type (<paramref name="p"/> is the byte after the skill id): the first flag byte's low nibble 4 is a plain hit with
    /// nothing more; 5-7 add three bytes - a bit set (2 parry, 4 perfect, 8 double) and, two bytes on (after the amount restored when bit 0x20 is set), the direction (1 back,
    /// 2 front). Null when the frame is too short or of another kind.
    /// </summary>
    private static HitKinds? ReadHitKinds(ReadOnlySpan<byte> frame, int p, int flags)
    {
        int kind = flags & 0xf;
        if (kind == 4)
        {
            return HitKinds.None;
        }

        if (kind is < 5 or > 7 || p + 5 > frame.Length)
        {
            return null;
        }

        byte bits = frame[p + 2];
        // Bit 0x20 (a life-steal hit) turns the byte after the bits into a varint of 1-3 bytes (the amount restored), which pushes the direction on:
        // on 1,049 such hits of four recordings (2026-10-21 to 23) the old fixed read gave directions of 3-7, the read after the varint only 0-2.
        int at = p + 3;
        if ((bits & 0x20) != 0)
        {
            if (!TryReadVarint(frame, ref at, out _))
            {
                return null;
            }
        }
        else
        {
            at++;
        }

        if (at >= frame.Length)
        {
            return null;
        }

        byte direction = frame[at];
        HitKinds kinds = HitKinds.None;
        if ((bits & 2) != 0) { kinds |= HitKinds.Parry; }
        if ((bits & 4) != 0) { kinds |= HitKinds.Perfect; }
        if ((bits & 8) != 0) { kinds |= HitKinds.Double; }
        if (direction == 1) { kinds |= HitKinds.Back; }
        else if (direction == 2) { kinds |= HitKinds.Front; }
        return kinds;
    }

    /// <summary>
    /// Değiştirildi (eklendi): vuruş sayısı işaretçisinin (1..9, 00 00 00) düzene göre yeri; orada geçerli bir işaretçi ve ardından
    /// iki varint (güç çarpanı, tutar) yoksa -1. <paramref name="p"/> beceri kimliğinden sonraki bayttır (hasar türü).
    /// </summary>
    private static int ExpectedHitCountAt(ReadOnlySpan<byte> frame, int p, int flags)
    {
        int kind = flags & 0xf;
        int at;
        if (kind == 4)
        {
            at = p + 2 + 4;
        }
        else if (kind is >= 5 and <= 7 && p + 3 < frame.Length)
        {
            at = p + 3;
            if ((frame[p + 2] & 0x20) != 0)
            {
                if (!TryReadVarint(frame, ref at, out _))
                {
                    return -1;
                }
            }
            else
            {
                at++;
            }

            at += 1 + 4; // yön + float
        }
        else
        {
            return -1;
        }

        if (at + 4 > frame.Length || frame[at] is < 1 or > 9 || frame[at + 1] != 0 || frame[at + 2] != 0 || frame[at + 3] != 0)
        {
            return -1;
        }

        int q = at + 4;
        return TryReadVarint(frame, ref q, out _) && q < frame.Length && TryReadVarint(frame, ref q, out long amount) && amount > 0 ? at : -1;
    }

    /// <summary>
    /// A summon that appeared before the meter started has no spawn frame on record, so neither its
    /// owner field nor its owner's name nor the cast before it is known. It still casts nothing but
    /// its summon attack (a spirit's "Fire Spirit: Leaping Slam", a Cleric's "Divine Aura", a
    /// Sorcerer's "Bittercold Wind"), and when the party has exactly one player of that class, known
    /// by id, it is theirs (Canyon Urugugu, recording started mid-fight, 2026-10-03: two Divine Auras
    /// and two spirits left as Player #id). A spirit's basic attack counts too, though its id names no
    /// class (see <see cref="Aion2SkillNames.SummonAttackClass"/>): the Ancient Spirit cast nothing
    /// else once its owner was known. A named entity is a player, never a summon.
    /// </summary>
    private int? LeftoverSummonOwner(int actor, int skillId)
    {
        if (_entities.IsSpawned(actor) || _entities.HasName(actor))
        {
            return null;
        }

        // A Sorcerer's Fire Wall / Cold Storm can hit from an object whose spawn the meter never saw (Deus Research Base, 2026-10-22: three of
        // them stayed Player #id). In an instance only the own party is around, so there the party's only Sorcerer is the owner.
        string? className = Aion2SkillNames.SummonAttackClass(skillId)
            ?? (Aion2Zones.IsInstance(CurrentZoneId) && Aion2SkillNames.IsFireWallOrColdStorm(skillId) ? Aion2SkillNames.ClassOf(skillId) : null);
        if (className is null)
        {
            return null;
        }

        var owners = _entities.PartyPlayerIdsOfClass(className).Where(id => id != actor).ToList();
        if (owners.Count > 1)
        {
            owners = OwnersByPower(actor, owners);
        }
        else if (owners.Count == 1 && !SingleCandidateAllowed())
        {
            // Değiştirildi (eklendi): açık dünyada tek aday kuralı yok (yabancının çağrısı bize ya da aynı sınıftan tek oyuncuya yazılıyordu);
            // yalnız güç çarpanı kanıtı sayılır. Kanıt yoksa sonraki vuruşlarda yeniden denenir.
            owners = OwnersByPower(actor, owners, strict: true);
            if (owners.Count == 0)
            {
                return null;
            }
        }

        if (owners.Count != 1)
        {
            // the party is not read yet (the meter started a moment ago): try again with the next hits, the first ones are handed over then
            if (owners.Count == 0 && _pendingLeftovers.Count < 64)
            {
                _pendingLeftovers[actor] = className;
            }

            return null;
        }

        _pendingLeftovers.Remove(actor);
        _entities.SetSummonOwner(actor, owners[0]);
        return owners[0];
    }

    /// <summary>Summons seen before the party was known (class of their attack): their owner is the party's only player of that class, once the roster is read.</summary>
    private readonly Dictionary<int, string> _pendingLeftovers = new();

    private void ResolvePendingLeftovers()
    {
        foreach ((int actor, string className) in _pendingSpawned.ToList())
        {
            if (_entities.SummonOwnerOf(actor) is not null || _entities.HasName(actor))
            {
                _pendingSpawned.Remove(actor);
                continue;
            }

            var owners = _entities.PartyPlayerIdsOfClass(className).Where(id => id != actor).ToList();
            if (owners.Count == 1 && _entities.SummonOwnerNameOf(actor) is null && (SingleCandidateAllowed() || OwnersByPower(actor, owners, strict: true).Count == 1))
            {
                _pendingSpawned.Remove(actor);
                _entities.SetSummonOwner(actor, owners[0]);
            }
        }

        foreach ((int actor, string className) in _pendingLeftovers.ToList())
        {
            if (_entities.IsSpawned(actor) || _entities.HasName(actor) || _entities.SummonOwnerOf(actor) is not null)
            {
                _pendingLeftovers.Remove(actor);
                continue;
            }

            var owners = _entities.PartyPlayerIdsOfClass(className).Where(id => id != actor).ToList();
            if (owners.Count == 1 && (SingleCandidateAllowed() || OwnersByPower(actor, owners, strict: true).Count == 1))
            {
                _pendingLeftovers.Remove(actor);
                _entities.SetSummonOwner(actor, owners[0]);
            }
        }
    }

    /// <summary>
    /// Değiştirildi (eklendi): "sınıfın tek oyuncusu sahibidir" kuralı yalnız zindanda (etrafta yalnız kendi parti) ya da bölge henüz
    /// bilinmezken bir parti listesi varken geçerli. Açık dünyada aynı sınıftan yabancılar da çağırır.
    /// </summary>
    private bool SingleCandidateAllowed() => Aion2Zones.IsInstance(CurrentZoneId) || (CurrentZoneId == 0 && _entities.RosterKnown);

    // Per actor: the damage multipliers (power scalar, 1/100 %) and the distinct skills its hits carried.
    private readonly Dictionary<int, (HashSet<int> Scalars, HashSet<int> Skills)> _power = new();

    private void NotePower(int actor, int scalar, int skillId)
    {
        if (!_power.TryGetValue(actor, out var entry))
        {
            if (_power.Count >= 2048)
            {
                _power.Clear(); // ids are reissued with every zone: old readings are worthless anyway
            }

            _power[actor] = entry = (new HashSet<int>(), new HashSet<int>());
        }

        if (entry.Scalars.Count < 16)
        {
            entry.Scalars.Add(scalar);
        }

        if (entry.Skills.Count < 64)
        {
            entry.Skills.Add(skillId);
        }
    }

    /// <summary>
    /// A summon never announced (a Cleric's Divine Aura has no spawn record) among several players of its class: every damage record
    /// carries the actor's power scalar, a summon's is its owner's. The candidates whose scalars share a value with the summon's and
    /// who use at least three times as many skills (a player runs a rotation, a summon repeats one or two skills); the owner when
    /// exactly one remains, else the list unchanged. In the recordings of 2026-10-13/14 this was right 334 times and wrong 16 times
    /// with every same-class stranger of the zone as candidate, so it is only asked of the party's players.
    /// </summary>
    // Değiştirildi: strict = kanıt yoksa aday listesi değil boş liste döner.
    private List<int> OwnersByPower(int summon, List<int> candidates, bool strict = false)
    {
        if (!_power.TryGetValue(summon, out var mine))
        {
            return strict ? new List<int>() : candidates;
        }

        var fits = candidates.Where(id => _power.TryGetValue(id, out var theirs)
            && theirs.Scalars.Overlaps(mine.Scalars) && theirs.Skills.Count >= 3 * Math.Max(mine.Skills.Count, 1)).ToList();
        return fits.Count == 1 ? fits : strict ? new List<int>() : candidates;
    }

    // Player summon -> how often each player was the target of its effects, for summons whose spawn names no owner.
    private readonly Dictionary<int, Dictionary<int, int>> _effectTargets = new();

    /// <summary>
    /// A player summon fires its friendly effects (heals, buffs) at its owner: of the recordings of 2026-10-13/14, the most-targeted
    /// player was the owner in 173 of 174 summons whose spawn names it (and filled in 90 more with no owner at all). Only NPCs of the
    /// player-summon list vote: counting every spawned entity made 445 monsters the "summon" of the player they hit. Two or more
    /// effects, a clear leader, and a target that is no monster and no spawned entity.
    /// </summary>
    private void NoteFriendlyEffect(int actor, int target)
    {
        if (actor == target || !_entities.IsSpawned(actor) || _entities.IsSpawned(target) || _entities.IsKnownMonster(target)
            || _entities.SummonOwnerOf(actor) is not null || _entities.NpcIdOf(actor) is not int npc || !Aion2Npcs.IsPlayerSummon(npc))
        {
            return;
        }

        if (!_effectTargets.TryGetValue(actor, out var votes))
        {
            if (_effectTargets.Count >= 512)
            {
                return;
            }

            _effectTargets[actor] = votes = new Dictionary<int, int>();
        }

        votes[target] = votes.GetValueOrDefault(target) + 1;
        var ranked = votes.OrderByDescending(v => v.Value).Take(2).ToList();
        if (ranked[0].Value >= 2 && (ranked.Count == 1 || ranked[0].Value > ranked[1].Value))
        {
            _entities.SetSummonOwner(actor, ranked[0].Key);
            _pendingSpawned.Remove(actor);
            _effectTargets.Remove(actor);
        }
    }

    private static ReadOnlySpan<byte> OwnerMarker => new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };

    private IEnumerable<DamageEvent> DecodeVarintDamage(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        if (_pendingLeftovers.Count > 0 || _pendingSpawned.Count > 0)
        {
            ResolvePendingLeftovers();
        }

        int p = 2;
        if (!TryReadVarint(frame, ref p, out long target) || frame.Length < p + 2)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        // Bit 0x04 of the first flag byte says the frame carries a damage block. Without it the frame
        // is a skill's companion notice (amount 1-4, often aimed at the caster itself), sent next to
        // nearly every real hit: verified on two Krao Cave captures (2026-10-02) against the in-game
        // combat analyzer, whose per-skill hit counts match only once these are left out.
        int flags = frame[p];
        p += 2;
        if ((flags & 0x04) == 0)
        {
            // Still a cast: a summoning skill is announced this way, just before its summon spawns.
            NoDamageFrames++;
            if (TryReadVarint(frame, ref p, out long caster) && frame.Length >= p + 4)
            {
                NoteCast(unchecked((int)caster), unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[p..])), timestamp);
                NoteFriendlyEffect(unchecked((int)caster), unchecked((int)target));
            }

            return Array.Empty<DamageEvent>();
        }

        if (!TryReadVarint(frame, ref p, out long actor) || frame.Length < p + 6)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        int skillId = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]));
        p += 4;

        NoteCast((int)actor, skillId, timestamp);
        NoteFriendlyEffect((int)actor, (int)target);
        if (Aion2SkillNames.IsNonDamageEffect(skillId))
        {
            return Array.Empty<DamageEvent>();
        }

        bool critical = frame[p + 1] == 3;
        HitKinds? kinds = ReadHitKinds(frame, p, flags);

        // Değiştirildi: önce işaretçinin düzene göre beklenen yeri denenir (ReadHitKinds ile aynı okuma: tür 4 = 4 baytlık float,
        // tür 5-7 = bit baytı, 1 bayt ya da can emme varint'i, yön baytı, float). Upstream'in 35 gerçek hasar çerçevesinin hepsinde
        // (SelfCheckAion2 + SelfCheckHitKinds, bit 0x10/0x20/0x40/0x80 dahil) bu yer ilk taramanın bulduğu yerle aynı. Tarama ise
        // bit baytı 4/8 (perfect/double) ve yön 0 olduğunda "04 00 00 00" dizisine, ya da float'ın alt baytları sıfırken yön baytına
        // oturup tutarı çöp okuyordu. Beklenen yerde geçerli bir işaretçi yoksa eski tarama kullanılır.
        int marker = ExpectedHitCountAt(frame, p, flags);
        if (marker < 0)
        {
            for (int i = p + 2; i + 4 <= frame.Length; i++)
            {
                if (frame[i] is >= 1 and <= 9 && frame[i + 1] == 0 && frame[i + 2] == 0 && frame[i + 3] == 0)
                {
                    marker = i;
                    break;
                }
            }
        }

        // The amount follows the marker and one more varint (a hit-kind field). Mostly that field is two
        // bytes long, but with larger values (battlefield frames: A0 9C 01, 94 A0 01) it is three, and
        // a fixed "marker + 6" then read the field's last byte as the damage: every hit read as 1.
        int hitCount = marker >= 0 ? Math.Clamp((int)frame[marker], 1, 9) : 1;
        int q = marker + 4;
        if (marker < 0 || !TryReadVarint(frame, ref q, out long powerScalar) || q >= frame.Length || !TryReadVarint(frame, ref q, out long amount) || amount <= 0 || amount > MaxPlausibleAmount)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        NotePower((int)actor, (int)powerScalar, skillId);

        if (skillId == DodgeSkillId)
        {
            return Array.Empty<DamageEvent>(); // Değiştirildi: kaçınma olayları tutulmuyor
        }

        // A summoned spirit's hits are its summoner's, as in the game's own combat analyzer. The heal
        // test below still looks at the spirit itself: its spawn "heal" targets its own id.
        int source = _entities.SummonOwnerOf((int)actor) ?? GuessSummonOwner((int)actor, skillId, (int)target)
            ?? LeftoverSummonOwner((int)actor, skillId) ?? (int)actor;
        if (Aion2SkillNames.ClassOf(skillId) is string className)
        {
            _entities.NoteClass(source, className);
        }

        // No "skill used" notification for the window: its handler refreshes the row list, which must only
        // happen on the UI thread - and this runs on the capture thread. Aion 2 knows each
        // player's class from the skill ids themselves (see Aion2EntityDirectory.NoteClass).
        string? skill = null; // Değiştirildi: beceri adı gerekmiyor

        // A heal-family skill is a heal unless it lands on a monster (Blood Absorption drains one).
        // "A monster" means one the server announced: a player is only known once seen casting, so a
        // Chanter's Recuperation on a member who had not cast yet used to read as damage between two
        // players - and one such hit made the resolver paint the whole party as enemies.
        bool isHeal = Aion2SkillNames.IsHealFamily(skillId) && !_entities.IsKnownMonster((int)target);
        // Not IsKnownMonster: started mid-fight, the meter never saw the boss spawn.
        if (!isHeal && _entities.IsKnownPlayer(source) && !_entities.IsKnownPlayer((int)target) && _entities.SummonOwnerOf((int)target) is null)
        {
            _entities.NoteMonsterHit(source, (int)target, skillId);
            if (_entities.IsLocalPlayer(source))
            {
                _entities.NoteLocalHit((int)target);
            }
        }

        // A heal on a summon is not healing the group: a Spiritmaster's spirit arrives with a heal of
        // its full health on itself (~56,000 per summon - 4.07 M over one Krao Cave run once spirits
        // are credited to their summoner), and topping up one's spirits is not party healing either.
        if (isHeal && _entities.SummonOwnerOf((int)target) is not null)
        {
            return Array.Empty<DamageEvent>();
        }


        // A blow a shield took a share of (see DecodeVarintDot): the hit frame carries the blow's FULL amount, the
        // player lost the rest - the hit points went down by exactly blow - share on the captures of 2026-10-08
        // (Protection Circle 1,535 - 638 = 897, 4,460 - 1,080 = 3,380). A share from an earlier packet belongs to a
        // blow the shield took whole. Found in StacysView's fork, checked on our own recordings.
        long taken = amount;
        if (!isHeal && _shieldShares.Remove(((int)actor, (int)target), out var share) && share.At == timestamp)
        {
            taken -= share.Amount;
            if (taken <= 0)
            {
                return Array.Empty<DamageEvent>();
            }
        }

        return new[] { new DamageEvent(timestamp, source, (int)target, taken, isHeal, skill, critical && !isHeal, SkillId: skillId, Kinds: isHeal ? null : kinds, HitCount: hitCount, ViaSummon: !isHeal && source != (int)actor) };
    }

    /// <summary>
    /// A damage- or heal-over-time tick, sent once a second per running effect: opcode | target
    /// (varint) | flags (1) | actor (varint) | stack (varint) | effect id (u32) | amount (varint, if
    /// flags &amp; 0x02) | heal (varint, if flags &amp; 0x01) | skill id (u32 LE, if flags &amp; 0x08).
    /// Every one of a capture's 556 tick frames parses to its exact length this way.
    /// <para>Damage (flags 0x0a): the amount is the tick's damage - the 23 ticks of the local player's
    /// Jointstrike: Curse on Ultimate Berk sum to 12,180, exactly what the in-game combat analyzer
    /// adds on top of the casts' direct hits (Krao Cave capture, 2026-10-02).</para>
    /// <para>Heal (flags 0x0b): the heal field is the tick's heal and the amount what is still to
    /// come - a Chanter's Recuperation announced 334 (flags 0x09, heal only), then ticked 83 four
    /// times while the amount ran 251, 168, 85, 2. So 0x0b ticks of a class's heal-family skill, or
    /// of any skill one player keeps on another, are heals of the heal field; potions and other
    /// classless effects are not counted.</para>
    /// <para>A player's own effects have ids of ten digits that start like the skill's (Drill Dart
    /// 14050340 ticks as effect 1405000022); a monster's have nine (its skill id x 100 + 11). A tick
    /// that pairs a monster's effect with a player's class skill is the monster's effect set off by
    /// that hit, not the player's damage: the boss of 2026-10-03 23:25/23:37 cast 1604660 on itself
    /// (effect 160466011), and from then on every hit of the Ranger came with such a tick of four
    /// times the hit - Deadshot 30,697, tick 122,788, hit points 665,980 -> 758,071, up exactly
    /// 122,788 - 30,697. Counted as damage, the Ranger dealt 1.4 M to a boss of 869 K (HP check
    /// 163 %). It is the target healing itself, recorded as such (source = target).</para>
    /// </summary>
    private IEnumerable<DamageEvent> DecodeVarintDot(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long target) || p >= frame.Length)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        int flags = frame[p++];
        if ((flags & 0x02) == 0 || (flags & 0x08) == 0)
        {
            return Array.Empty<DamageEvent>();
        }

        if (!TryReadVarint(frame, ref p, out long actor) || !TryReadVarint(frame, ref p, out _) || frame.Length < p + 4)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        uint effectId = BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]);
        p += 4;
        long healed = 0;
        if (!TryReadVarint(frame, ref p, out long amount) || (flags & 0x01) != 0 && !TryReadVarint(frame, ref p, out healed) || frame.Length != p + 4)
        {
            SkippedShortFrames++;
            return Array.Empty<DamageEvent>();
        }

        int skillId = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]));

        // A monster's effect (nine digits) set off by a player's class skill: the target healing
        // itself by the amount (see the remarks above), never the player's damage.
        if (effectId < 1_000_000_000 && Aion2SkillNames.ClassOf(skillId) is not null)
        {
            int effectSkill = (int)(effectId / 100);
            return amount > 0 && amount <= MaxPlausibleAmount
                ? new[] { new DamageEvent(timestamp, (int)target, (int)target, amount, IsHeal: true, null, IsTick: true, SkillId: effectSkill) }
                : Array.Empty<DamageEvent>();
        }

        // No summon guess here (see GuessSummonOwner): a tick's class skill can be the target's own
        // shield, the actor the monster striking it.
        int source = _entities.SummonOwnerOf((int)actor) ?? (int)actor;

        // A heal over time arrives in the damage tick's shape. Counting one as damage once made a
        // Chanter "hit" every party member once a second and painted the whole party as enemies, so
        // a heal-family skill, or any tick a player keeps on another player, is never damage; PvP
        // damage-over-time between players will need a capture of its own.
        if (Aion2SkillNames.IsHealFamily(skillId) || _entities.IsKnownPlayer(source) && _entities.IsKnownPlayer((int)target))
        {
            bool countedHeal = (flags & 0x01) != 0 && healed > 0 && healed <= MaxPlausibleAmount
                && Aion2SkillNames.ClassOf(skillId) is not null && _entities.SummonOwnerOf((int)target) is null;
            return countedHeal
                ? new[] { new DamageEvent(timestamp, source, (int)target, healed, IsHeal: true, null, IsTick: true, SkillId: skillId) }
                : Array.Empty<DamageEvent>();
        }

        if (amount <= 0 || amount > MaxPlausibleAmount || target == actor)
        {
            return Array.Empty<DamageEvent>();
        }

        // A shield's share of a blow: a monster's tick naming the shield a player wears. The blow itself comes in the
        // very next hit frame with its full amount, so the share is kept to be taken off it (see DecodeVarintDamage).
        // Counted as a blow of its own, it inflated the damage taken (captures of 2026-10-07 to 10-09).
        if (Aion2SkillNames.ClassOf(skillId) is not null && !_entities.IsKnownPlayer(source) && _entities.IsKnownPlayer((int)target))
        {
            var key = ((int)actor, (int)target);
            long earlier = _shieldShares.TryGetValue(key, out var share) && share.At == timestamp ? share.Amount : 0;
            if (_shieldShares.Count > 512)
            {
                // Shares of blows taken whole, whose monster never struck that player again.
                foreach (var stale in _shieldShares.Where(kv => kv.Value.At != timestamp).Select(kv => kv.Key).ToList())
                {
                    _shieldShares.Remove(stale);
                }
            }

            _shieldShares[key] = (timestamp, earlier + amount);
            return Array.Empty<DamageEvent>();
        }

        return new[] { new DamageEvent(timestamp, source, (int)target, amount, IsHeal: false, null, IsTick: true, SkillId: skillId, ViaSummon: source != (int)actor) };
    }

    /// <summary>
    /// A skill being wound up (verified on two debug recordings, 2026-10-12: 404 frames, every one reads to its end and the damage of
    /// the same skill id by the same caster follows ~0.6 s after the cast has ended): opcode | caster (varint) | 1 byte | skill id (u32 LE) |
    /// 1 byte | aim flags (1) | target (varint) | facing (f32, degrees) | if aim flags &amp; 2: x, y, z (f32) | cast time in ms (varint) | tail.
    /// </summary>
    private void DecodeWindup(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        if (!TryReadAim(frame, out int actor, out int skillId, out int target, out double facing, out float x, out float y, out float z, out bool hasPosition, out int p)
            || !TryReadVarint(frame, ref p, out long ms) || ms is < 50 or > 120_000)
        {
            return;
        }

        _entities.Combat.NoteWindup(new CastNotice(actor, skillId, target, timestamp, (int)ms, facing, x, y, z, hasPosition));
    }

    private static bool TryReadAim(ReadOnlySpan<byte> frame, out int actor, out int skillId, out int target, out double facing, out float x, out float y, out float z, out bool hasPosition, out int end)
    {
        actor = skillId = target = 0;
        facing = 0;
        x = y = z = 0;
        hasPosition = false;
        end = 0;
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long actorId) || actorId is < 1 or > 10_000_000 || p + 8 > frame.Length)
        {
            return false;
        }

        p += 1;
        uint skill = BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]);
        p += 4;
        if (skill is < 1 or > 300_000_000)
        {
            return false;
        }

        byte aimFlags = frame[p + 1];
        p += 2;
        if (!TryReadVarint(frame, ref p, out long targetId) || targetId is < 0 or > 10_000_000 || p + 4 > frame.Length)
        {
            return false;
        }

        double angle = BinaryPrimitives.ReadSingleLittleEndian(frame[p..]);
        p += 4;
        if (!double.IsFinite(angle) || Math.Abs(angle) > 720)
        {
            return false;
        }

        if ((aimFlags & 2) != 0)
        {
            if (p + 12 > frame.Length)
            {
                return false;
            }

            x = BinaryPrimitives.ReadSingleLittleEndian(frame[p..]);
            y = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 4)..]);
            z = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 8)..]);
            hasPosition = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && Math.Abs(x) < 10_000_000 && Math.Abs(y) < 10_000_000;
            p += 12;
        }

        actor = (int)actorId;
        skillId = (int)skill;
        target = (int)targetId;
        facing = angle;
        end = p;
        return true;
    }

    /// <summary>
    /// An entity's changed stats: opcode | entity (varint) | format (1) | if format &amp; 1: count (1)
    /// and that many kind (1) + u32 LE | if format &amp; 2: count (1) and that many kind (1) + i64 LE.
    /// Kind 0 of the 8-byte group is the current hit points. Verified on four captures (2026-10-02):
    /// all 4,492 frames parse to their exact length, and for every boss the hit points plus the
    /// damage decoded against it stay constant to the point (Ultimate Berk 615,000 in a party and
    /// 123,000 solo, Divine Auldor 1,125,000). The other kinds are not identified yet; 8-byte kind 7
    /// equals a boss's full health once but not a player's, so it is not taken as the maximum.
    /// </summary>
    private void DecodeHp(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long entityId) || p >= frame.Length)
        {
            SkippedShortFrames++;
            return;
        }

        int format = frame[p++];
        if ((format & 1) != 0)
        {
            // Detailed stats only ever go to the player they belong to - the local player.
            _entities.NoteDetailedStats(unchecked((int)entityId));
            if (p >= frame.Length)
            {
                SkippedShortFrames++;
                return;
            }

            p += 1 + frame[p] * 5;
        }

        long? current = null;
        long? reportedMax = null;
        if ((format & 2) != 0)
        {
            if (p >= frame.Length)
            {
                SkippedShortFrames++;
                return;
            }

            int count = frame[p++];
            for (int i = 0; i < count && p + 9 <= frame.Length; i++, p += 9)
            {
                if (frame[p] == 0)
                {
                    current = BinaryPrimitives.ReadInt64LittleEndian(frame[(p + 1)..]);
                }
                else if (frame[p] == 7)
                {
                    reportedMax = BinaryPrimitives.ReadInt64LittleEndian(frame[(p + 1)..]); // the maximum (it follows buffs that raise it)
                }
            }
        }

        if (p != frame.Length || (format & ~3) != 0)
        {
            SkippedShortFrames++;
            return;
        }

        if (reportedMax is long maxHp && maxHp > 0)
        {
            _entities.HitPoints.NoteMax(unchecked((int)entityId), maxHp);
        }

        if (current is long hp && hp >= 0)
        {
            _entities.HitPoints.Note(unchecked((int)entityId), timestamp, hp);
            if (hp == 0)
            {
                _entities.Combat.NoteGone(unchecked((int)entityId), timestamp);
                _entities.ClearLocalTarget(unchecked((int)entityId)); // a dead monster is no target any more
            }
        }
    }

    /// <summary>The local player marked a monster (see <see cref="OpcodeFamily.TargetSelect"/>): remembered as the current target.</summary>
    private void DecodeTargetSelect(ReadOnlySpan<byte> frame)
    {
        int p = 2;
        if (TryReadVarint(frame, ref p, out long entityId) && frame.Length - p <= 3)
        {
            _entities.SetLocalTarget(unchecked((int)entityId));
        }
    }

    // ---- Gizli Küp (Hidden Cube) ----
    // Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2FrameDecoder.cs IsHiddenCube / DecodeEnvSpawn / DecodeEnvRemove /
    // DecodeEnvOpenStart. Değiştirildi: toplama kaynakları (Aion2Gather), yükseklik ve doğuş zamanı okunmaz; sadece küpler dizine gider.
    // Çerçeve anlamları dr89mh/aion2-cube-radar (docs/FINDINGS.md B6) bulgularıyla aynıdır.

    /// <summary>İstemcinin "Hidden Cube" adını verdiği env nesne kimlikleri: Verteron 1200036-41, 1200667, Altgard 1301199/1301200, Abyss ve alan olayı küpleri.</summary>
    internal static bool IsHiddenCube(long envId) =>
        envId is >= 1200036 and <= 1200041 or 1200667 or 1200707 or >= 1201502 and <= 1201505 or >= 1300081 and <= 1300083 or 1300754
            or 1301199 or 1301200 or >= 1301242 and <= 1301245 or 1100056 or 1100094;

    /// <summary>"Bir env nesnesi göründü": opcode | varlık (varint) | tür baytı | 00 | env kimliği u32 | x, y, z float ...</summary>
    private void DecodeEnvSpawn(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long entityId) || frame.Length < p + 18)
        {
            return;
        }

        long envId = BinaryPrimitives.ReadUInt32LittleEndian(frame[(p + 2)..]);
        if (!IsHiddenCube(envId))
        {
            return;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 6)..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 10)..]);
        if (float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000)
        {
            _entities.NoteCube(unchecked((int)entityId), x, y, timestamp);
        }
    }

    /// <summary>"Bir env nesnesi kayboldu": varlık (varint), neden baytı (04 = toplandı/açıldı, 01 = oyuncunun menzilinden çıktı), iki bayt daha.</summary>
    private void DecodeEnvRemove(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (TryReadVarint(frame, ref p, out long entityId))
        {
            _entities.ForgetCube(unchecked((int)entityId), p < frame.Length ? frame[p] : 0, timestamp);
        }
    }

    /// <summary>"Bir oyuncu env nesnesini açmaya başladı" (0x3836): nesne varlığı (varint), açan varlık (varint), 01. Küpte ardından 0x3a36 ve neden 04'lü kaldırma gelir.</summary>
    private void DecodeEnvOpenStart(ReadOnlySpan<byte> frame)
    {
        int p = 2;
        if (TryReadVarint(frame, ref p, out long entityId) && TryReadVarint(frame, ref p, out _))
        {
            _entities.NoteCubeOpening(unchecked((int)entityId));
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int position, out long value)
    {
        value = 0;
        for (int i = 0; i < 5 && position < data.Length; i++)
        {
            byte b = data[position++];
            value |= (long)(b & 0x7f) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "Another player appeared" frame: opcode | combat id (varint) | a few bytes | length-prefixed
    /// name (UTF-8). The same id is the actor id in damage frames, so this is what turns
    /// "Assassin #3562" into "Pencilgon". The local player never gets one (verified: in a five-member
    /// party, four nickname frames, for exactly the four others).
    /// </summary>
    private void DecodeVarintNickname(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long id))
        {
            return;
        }

        for (int k = p; k < Math.Min(frame.Length - 4, p + 24); k++)
        {
            // Upstream 847db0c: minLength 2 - iki harfli bir oyuncu ("sh") 3 sınırıyla bütün oturum "Player #id" kalıyordu.
            if (TryReadName(frame, k, out string name, minLength: 2))
            {
                _entities.Register((int)id, name);
                _entities.NoteIdentity((int)id, name, 0);
                ReadSeenProfile(frame, name);

                // A player in a guild: further on, the frame carries server id (u16: 17 05 = 1303,
                // 18 05 = 1304 Kaisinel) | guild id (u32, non-zero) | 00 00 | the same server id |
                // the guild's length-prefixed name. Seen for all ten guilds of 71 nickname frames
                // (Krao Cave and Draupnir captures, 2026-10-02: HORDE, ElyosOrden, Insomnia,
                // Convèrgence, Freljord, DarkLegion...); a player without a guild has no such run,
                // and reading any "server id + name" pair there picked up garbage ("odd",
                // "jd47ddddep"). Remembered so the roster's leftover name is the player's, not the
                // guild's.
                for (int i = k + 1 + frame[k]; i + 11 < frame.Length; i++)
                {
                    int server = frame[i] | frame[i + 1] << 8;
                    if (server is >= 1000 and <= 9999 && frame[i + 8] == frame[i] && frame[i + 9] == frame[i + 1]
                        && (frame[i + 2] | frame[i + 3] | frame[i + 4] | frame[i + 5]) != 0 && frame[i + 6] == 0 && frame[i + 7] == 0
                        && TryReadName(frame, i + 10, out string other, minLength: 2) && other != name)
                    {
                        _entities.NoteNonPlayerName(other);
                        break;
                    }
                }

                return;
            }
        }
    }

    /// <summary>
    /// "Player seen" frame: opcode | target id (varint) | skill id (u32) | the acting player's combat id
    /// (varint) | server id (u16) | length-prefixed name | optional length-prefixed guild. Every player
    /// who acts near you is announced this way. It used to be found by the server id 18 05 (1304,
    /// Europe - Kaisinel) in front of the name, so players of every other server were never named by
    /// it (seen: Aera of server 1303 on a Krao Cave capture, 2026-10-02); the fields are read in
    /// order now, whatever the server.
    /// </summary>
    /// <summary>The first character id of a player the game lists; below this one a "server id | id | 00 00 | server id | name" run is a legion (their ids stayed under 1,140, players' from 8,766 on).</summary>
    private const int FirstCharacterId = 5000;

    /// <summary>
    /// A frame that lists players with their permanent character id: <c>server id (u16) | character id (u32) | 00 00 | the same server id |
    /// length-prefixed name</c>, found by scanning (a legion is listed the same way with a small id and is skipped).
    /// </summary>
    private void DecodePlayerList(ReadOnlySpan<byte> frame)
    {
        for (int i = 2; i + 11 < frame.Length; i++)
        {
            int server = frame[i] | frame[i + 1] << 8;
            if (server is < 1000 or > 9999 || frame[i + 6] != 0 || frame[i + 7] != 0 || frame[i + 8] != frame[i] || frame[i + 9] != frame[i + 1])
            {
                continue;
            }

            uint characterId = BinaryPrimitives.ReadUInt32LittleEndian(frame[(i + 2)..]);
            if (characterId >= FirstCharacterId && TryReadName(frame, i + 10, out string name, minLength: 2))
            {
                _entities.NoteCharacterId(name, server, (int)characterId);
                i += 10 + frame[i + 10];
            }
        }
    }

    /// <summary>
    /// The 0xE2 frames (a name looked at, a friend or legion entry: 76e2, 77e2, 82e2 ...) carry <c>server id (u16) | length | name</c>. A player whose character
    /// window the local player opened is otherwise only known by name, so without this his server (and his faction) stayed unknown and the website
    /// left him out of the upload. The id must look like a server of the two sides (1301, 2304 ...).
    /// </summary>
    private void NoteNamedServers(ReadOnlySpan<byte> frame)
    {
        for (int i = 2; i + 5 < frame.Length; i++)
        {
            int server = frame[i] | frame[i + 1] << 8;
            if (server is < 1000 or > 2999 || server % 100 is < 1 or > 30)
            {
                continue;
            }

            if (TryReadName(frame, i + 2, out string name, minLength: 2) && char.IsLetter(name[0]))
            {
                NoteServerId(server, name);
                i += 1 + frame[i + 2];
            }
        }
    }

    private void DecodeAppearance(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long victim) || frame.Length < p + 4)
        {
            return;
        }

        // The same frame is the death notice: the first id is who died (its hit points fell to 0 about 50 ms before: 3,364 of 3,366 frames with a skill
        // in four recordings, 2026-10-21 to 23) and the u32 the skill that did it (a hit with that skill landed on the victim in the 1.5 s before, in all
        // 1,166 checked frames); 0 means nobody took the kill.
        p += 4; // Değiştirildi: öldüren beceri (kill credit) tutulmuyor
        if (!TryReadVarint(frame, ref p, out long id) || id <= 0 || frame.Length < p + 3
            || !TryReadName(frame, p + 2, out string name, minLength: 2))
        {
            return;
        }

        _entities.Register((int)id, name);
        NoteServerId(frame[p] | frame[p + 1] << 8, name, inView: true);
        _entities.NoteIdentity((int)id, name, frame[p] | frame[p + 1] << 8);
        if (TryReadName(frame, p + 3 + frame[p + 2], out string guild, minLength: 2) && guild != name)
        {
            _entities.NoteNonPlayerName(guild);
        }
    }

    /// <summary>
    /// The local player's character record (verified against four sessions; its level climbs
    /// 10 -> 12 -> 13 -> 32 -> 33 across them, matching the character): opcode | combat id (varint) |
    /// a few bytes | length-prefixed name | <c>18 05</c> | class code (u32) | <c>01</c> | level (u32) |
    /// ... a long block ... | one entry per equipped item: item id (u32), <c>00</c>, slot index, <c>00</c>.
    /// Enchant level, stones and stats are not decoded (the enchant is not in the entry bytes).
    /// </summary>
    private void DecodeCharacter(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long id))
        {
            return;
        }

        for (int k = p; k < Math.Min(frame.Length - 12, p + 16); k++)
        {
            if (!TryReadName(frame, k, out string name, minLength: 2))
            {
                continue;
            }

            int after = k + 1 + frame[k];
            // The byte at +6 is the faction: 1 Elyos, 2 Asmodian (an Asmodian character - Templar 11, Ranger 15 on NA East, issues #17/#20 - was never read while 1 was required).
            if (after + 11 > frame.Length || frame[after + 6] is not (1 or 2))
            {
                continue;
            }

            // The two bytes after the name are the character's server id (18 05 = 1304, Europe -
            // Kaisinel; 17 05 = 1303 for a character of another EU server). They used to be required to
            // be 18 05, so the own record of anyone not on Kaisinel was never read; a plausible class
            // code (4 * class + faction bit, see Aion2SkillNames.ClassFromCode) checks the match instead.
            int classCode = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[(after + 2)..]));
            if (classCode < 1 || (classCode - 1) / 4 is < 1 or > 9)
            {
                continue;
            }

            int level = unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame[(after + 7)..]));
            if (level is < 1 or > 200)
            {
                continue;
            }

            // Değiştirildi: ekipman ve kanat okunmuyor
            _entities.SetLocalCharacter(new Aion2CharacterInfo((int)id, name, classCode, level, timestamp,
                ServerId: BinaryPrimitives.ReadUInt16LittleEndian(frame[after..])));
            return;
        }
    }

    /// <summary>
    /// Değiştirildi: "player appeared" çerçevesinden yalnız savaş gücü (combat power) okunur ve isme göre saklanır;
    /// sınıf/seviye/ekipman kısmı (Aion2ItemCatalog, ReadWornItems, ReadSeenLevels) kaldırıldı.
    /// </summary>
    private void ReadSeenProfile(ReadOnlySpan<byte> frame, string name)
    {
        if (ReadSeenCombatPower(frame) is int combatPower)
        {
            _entities.NoteCombatPower(name, combatPower);
        }
    }

    /// <summary>
    /// The combat power: a 4-byte number close behind the end of the frame (nine bytes before it, ten in one case seen). Read where the number is plausible (20,000 to
    /// 4,000,000; the neighbouring positions read as a shifted copy or as a huge number). Verified against the profile of one player (Keuno 138,016) and the
    /// numbers of 28 others; in the 10 against 10 arena it is the same fixed 397228 for everybody. Null when no position fits.
    /// </summary>
    private static int? ReadSeenCombatPower(ReadOnlySpan<byte> frame)
    {
        foreach (int back in stackalloc[] { 9, 10, 8, 11 })
        {
            int at = frame.Length - back;
            if (at < 0 || at + 4 > frame.Length)
            {
                continue;
            }

            uint value = BinaryPrimitives.ReadUInt32LittleEndian(frame[at..]);
            if (value is >= 20_000 and <= 4_000_000)
            {
                return (int)value;
            }
        }

        return null;
    }

    /// <summary>
    /// The party roster (0x0297, re-sent every few seconds while in a party; 0x0197 is a list of
    /// other parties). Each member: server id (u16) | length-prefixed name | a small u32 (not the class
    /// code of the other frames: 32 for a Cleric, 24 for an Spiritmaster) | level (u32). Found by that shape rather than by the server id 18 05 (Kaisinel) it used to require,
    /// which missed every member of another server - verified on three captures (2026-10-02):
    /// Psefon 30, Boulenbouche 45, Daidai 31, ScareNight, Destinyy 30, across servers 1303 and 2301.
    /// The party list of 0x0297 also tells which players are in the local player's group.
    /// </summary>
    private void DecodeRoster(ReadOnlySpan<byte> frame, DateTime timestamp)
    {
        var members = new List<string>();
        for (int i = 2; i + 1 < frame.Length; i++)
        {
            if (!TryReadName(frame, i, out string name, minLength: 2))
            {
                continue;
            }

            // The length prefix counts bytes: "Azaëde" is 6 characters and 7 bytes, and counting
            // characters dropped every accented member from the party (Canyon Urugugu, 2026-10-03).
            int after = i + 1 + frame[i];
            if (after + 8 > frame.Length || frame[after + 1] != 0 || frame[after + 2] != 0 || frame[after + 3] != 0
                || frame[after + 5] != 0 || frame[after + 6] != 0 || frame[after + 7] != 0)
            {
                continue;
            }

            int level = frame[after + 4];
            if (frame[after] == 0 || level is < 1 or > 60)
            {
                continue;
            }

            members.Add(name);
            if (i >= 2)
            {
                NoteServerId(frame[i - 2] | frame[i - 1] << 8, name);
            }

            if (HomeServerBehindName(frame, after) is int homeServer)
            {
                NoteServerId(homeServer, name);
                _entities.NoteHomeServerOfName(name, homeServer);
            }

            _entities.NoteRosterName(name);
            if (frame[0] == 0x02 && frame[1] == 0x97 && Aion2SkillNames.ClassFromRosterCode(frame[after]) is string className)
            {
                _entities.NotePartyClass(name, className);
            }

            i = after + 7;
        }

        if (frame[0] == 0x02 && frame[1] == 0x97 && members.Count > 0)
        {
            _entities.NoteParty(members, timestamp);
        }
    }

    /// <summary>
    /// The second server id of a roster member, 12 bytes behind the end of his name (class u32 | level u32 | a u32 | server u16 | the id again or a 0x10xx value), one byte
    /// further when a marker byte (01, 02, 03, 06, 07; it changes from frame to frame) sits in front of it. Read at the marker, "marker + low byte of the id" looks like
    /// a server for some of them (Munin 2313 as 2305, 2307 or 2311, Zemurru 2315 as 2817, 2819 or 2823), so a position counts only when the two bytes after it repeat the id
    /// or are a 0x10xx value, and the id names a server of the table. Null when neither position does - then only the id in front of the name counts (it was right in
    /// every recorded member).
    /// </summary>
    private static int? HomeServerBehindName(ReadOnlySpan<byte> frame, int afterName)
    {
        foreach (int offset in stackalloc[] { 12, 13 })
        {
            if (afterName + offset + 4 > frame.Length)
            {
                continue;
            }

            int server = frame[afterName + offset] | frame[afterName + offset + 1] << 8;
            int next = frame[afterName + offset + 2] | frame[afterName + offset + 3] << 8;
            if (server is >= 1000 and <= 2999 && server % 100 is >= 1 and <= 30 && Aion2Servers.IsKnown(server) && (next == server || next >> 8 == 0x10))
            {
                return server;
            }
        }

        return null;
    }

    /// <summary>A plausible character name at <paramref name="at"/>: a length byte (3-24) followed
    /// by that many UTF-8 bytes that are all letters or digits (no spaces, no control bytes).</summary>
    private static bool TryReadName(ReadOnlySpan<byte> frame, int at, out string name, int minLength = 3)
    {
        name = "";
        if (at >= frame.Length)
        {
            return false;
        }

        int length = frame[at];
        if (length < minLength || length > 24 || at + 1 + length > frame.Length)
        {
            return false;
        }

        // Byte scans call this at every offset and almost all of them hit bytes that are no text: checked up front, no exception per miss.
        ReadOnlySpan<byte> bytes = frame.Slice(at + 1, length);
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return false;
        }

        string candidate = Encoding.UTF8.GetString(bytes);

        foreach (char c in candidate)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        name = candidate;
        return true;
    }

    private void DecodeNickname(ReadOnlySpan<byte> frame, IReadOnlyDictionary<string, FieldSpec> fields, bool littleEndian)
    {
        if (TryReadInt(frame, fields, "objectId", littleEndian, out long objectId)
            && TryReadString(frame, fields, "name", littleEndian, out string? name) && name.Length > 0)
        {
            _entities.Register((int)objectId, name);
        }
    }

    private static bool TryReadInt(ReadOnlySpan<byte> frame, IReadOnlyDictionary<string, FieldSpec> fields, string name, bool littleEndian, out long value)
    {
        value = 0;
        if (!fields.TryGetValue(name, out FieldSpec? spec) || frame.Length < spec.Offset + spec.Size)
        {
            return false;
        }

        value = spec.Signed ? ReadSigned(frame, spec, littleEndian) : (long)ReadUnsigned(frame, spec, littleEndian);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> frame, IReadOnlyDictionary<string, FieldSpec> fields, string name, bool littleEndian, out string value)
    {
        value = "";
        if (!fields.TryGetValue(name, out FieldSpec? spec) || frame.Length < spec.Offset + 2)
        {
            return false;
        }

        // Default: 2-byte character count followed by UTF-16LE; "utf8z" = zero-terminated UTF-8.
        if (string.Equals(spec.Encoding, "utf8z", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<byte> tail = frame[spec.Offset..];
            int end = tail.IndexOf((byte)0);
            value = Encoding.UTF8.GetString(end < 0 ? tail : tail[..end]);
            return true;
        }

        int chars = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(frame[spec.Offset..]) : BinaryPrimitives.ReadUInt16BigEndian(frame[spec.Offset..]);
        int start = spec.Offset + 2;
        if (chars > 512 || frame.Length < start + chars * 2)
        {
            return false;
        }

        value = Encoding.Unicode.GetString(frame.Slice(start, chars * 2));
        return true;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> frame, FieldSpec spec, bool littleEndian)
    {
        ReadOnlySpan<byte> bytes = frame.Slice(spec.Offset, spec.Size);
        return spec.Size switch
        {
            1 => bytes[0],
            2 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
            4 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
            _ => littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes),
        };
    }

    private static long ReadSigned(ReadOnlySpan<byte> frame, FieldSpec spec, bool littleEndian)
    {
        ReadOnlySpan<byte> bytes = frame.Slice(spec.Offset, spec.Size);
        return spec.Size switch
        {
            1 => (sbyte)bytes[0],
            2 => littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes),
            4 => littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes),
            _ => littleEndian ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes),
        };
    }
}
