// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2EntityDirectory.cs. Değiştirildi.
using AionDPS.Combat.Sources;

using AionDPS.Aion2.Protocol;

namespace AionDPS.Aion2;

/// <summary>The local player's character record (opcode 0x3336), as of when the server last sent it
/// - at login and on every zone change.</summary>
/// Değiştirildi: ekipman listesi çıkarıldı.
public sealed record Aion2CharacterInfo(int CombatId, string Name, int ClassCode, int Level, DateTime ReceivedAt, bool Restored = false, int ServerId = 0);

/// <summary>Aion 2 frames carry the game's own object ids, so this maps those to names as nickname
/// frames reveal them. The local player is whichever id the session frame names. Names that never
/// came with a game id (hand-entered characters) get synthetic negative ids so they can never
/// collide with a real object id.</summary>
public sealed class Aion2EntityDirectory : IEntityDirectory
{
    private readonly Dictionary<int, string> _names = new();
    private readonly Dictionary<string, int> _ids = new();
    // Per object: how often each class's skills were seen with it as the actor. A received buff
    // (a Chanter's Mantra on a Gladiator) arrives with the recipient as actor, so the LAST class
    // seen is wrong - the most frequent one is right.
    private readonly Dictionary<int, Dictionary<string, int>> _classVotes = new();
    private readonly object _gate = new();

    // Name -> how many roster frames listed it. The party's own members are listed in every update;
    // a stray look-alike (a member who left, a byte run that happens to fit) is listed once.
    private readonly Dictionary<string, int> _roster = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notPlayers = new(StringComparer.Ordinal);
    private int _explicitLocalId = -1;
    private Aion2CharacterInfo? _character;
    private string? _configuredLocalName;

    /// <summary>
    /// The local player: a session frame's id when one is decoded, else the object whose name is the
    /// configured character name (<see cref="SetConfiguredLocalName"/>), else the one inferred from
    /// the stream (see <see cref="InferLocalPlayer"/>). -1 while none is known.
    /// </summary>
    public int LocalPlayerId
    {
        get
        {
            if (_explicitLocalId >= 0)
            {
                return _explicitLocalId;
            }

            lock (_gate)
            {
                if (_configuredLocalName is not null && _ids.TryGetValue(_configuredLocalName, out int byName))
                {
                    return byName;
                }
            }

            return InferLocalPlayer() ?? -1;
        }

        internal set => _explicitLocalId = value;
    }

    /// <summary>Değiştirildi (eklendi): sunucunun kesin bildirdiği yerel kimlik (oturum çerçevesi ya da karakter kaydı); yoksa -1. Tahmin içermez.</summary>
    public int ExplicitLocalPlayerId => _explicitLocalId;

    /// <summary>Değiştirildi (eklendi): kayıtlı yerel adın (<see cref="SetConfiguredLocalName"/>) şu anki kimliği; ad yoksa ya da henüz bir kimliğe bağlı değilse null.</summary>
    public int? ConfiguredLocalPlayerId
    {
        get
        {
            lock (_gate)
            {
                return _configuredLocalName is not null && _ids.TryGetValue(_configuredLocalName, out int byName) ? byName : null;
            }
        }
    }

    /// <summary>The user's own Aion 2 character name from Settings (empty = none).</summary>
    public void SetConfiguredLocalName(string? name)
    {
        lock (_gate)
        {
            _configuredLocalName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
    }

    // Gizli Küp: gerçek sayaçlar küp çözücüsüyle gelir (şimdilik yer tutucu).
    /// <summary>Açılıştan beri menzile giren Gizli Küp sayısı (aynı küpün tekrar duyurulması sayılmaz).</summary>
    public int CubeArrivals => 0;

    /// <summary>Şu an menzilde olan, açılmamış Gizli Küp sayısı.</summary>
    public int CubesInRange => 0;

    /// <summary>The local player's name as its own character record states it - what the meter then
    /// remembers in Settings so the next session knows it from the start. It used to be the party
    /// roster's leftover name (the one no visible player carries), which, once the roster was read on
    /// every server, could be a team mate not named yet: that name was then saved as one's own and the
    /// own row showed under a team mate's name.</summary>
    public string? LearnedLocalName
    {
        get
        {
            lock (_gate)
            {
                return _character is { Restored: false } own && own.Name.Length > 0 ? own.Name : null;
            }
        }
    }

    /// <summary>The local player's own record. It is only ever sent for yourself (everyone else gets
    /// the "appeared" frame), so it also settles who the local player is.</summary>
    public void SetLocalCharacter(Aion2CharacterInfo info)
    {
        int? renamedFrom = null;
        lock (_gate)
        {
            // Değiştirildi: karakter profili listeleri (beceri, ekipman, evcil hayvan...) tutulmuyor.
            int previousId = _explicitLocalId;
            string? previousName = _character?.Name;
            _character = info;
            _explicitLocalId = info.CombatId;
            if (previousId >= 0 && previousId != info.CombatId && previousName == info.Name)
            {
                renamedFrom = previousId;
            }

            _names[info.CombatId] = info.Name;
            _ids[info.Name] = info.CombatId;
        }

        if (renamedFrom is int oldId)
        {
            LocalIdChanged?.Invoke(oldId, info.CombatId);
        }
    }

    /// <summary>The own character was announced again under another combat id (a map change); the old id and the new one are the same player.</summary>
    public event Action<int, int>? LocalIdChanged;

    public Aion2CharacterInfo? LocalCharacter
    {
        get
        {
            lock (_gate)
            {
                return _character;
            }
        }
    }

    /// <summary>Değiştirildi (eklendi): bilinen oyuncu kimliği sayısı bunu aşarsa bölge değişiminde sadece parti ve yerel oyuncu tutulur.</summary>
    private const int MaxKnownPlayers = 5000;

    /// <summary>
    /// Değiştirildi (eklendi): bölge değişti. Varlık kimlikleri yeni bölgede yeniden dağıtıldığı için canavar/çağrı/can tabloları
    /// boşaltılır; <paramref name="keep"/> (bölge çerçevesinden hemen önce doğanlar) korunur. Oyuncu adı/sınıf/parti bilgisi kalır,
    /// yalnız çok büyüdüyse partide olmayanlar atılır.
    /// </summary>
    public void ForgetZone(IReadOnlySet<int> keep)
    {
        lock (_gate)
        {
            RemoveAllExcept(_npcIds, keep);
            RemoveAllExcept(_bossNpcs, keep);
            RemoveAllExcept(_removed, keep);
            RemoveAllExcept(_summonOwners, keep);
            RemoveAllExcept(_summonOwnerNames, keep);
            _spawned.RemoveWhere(id => !keep.Contains(id));
            _resolvedOwners.Clear();
            _partyTargets.Clear();
            _fightingAlongside.Clear();
            _skillsSeen.Clear();
            _markHits.Clear(); // Değiştirildi (eklendi): kimlikler bölgeyle değişir
            foreach (int faded in _detailedStats.Where(kv => kv.Value < 0.5).Select(kv => kv.Key).ToList())
            {
                _detailedStats.Remove(faded);
            }

            if (_classVotes.Count > MaxKnownPlayers || _names.Count > MaxKnownPlayers)
            {
                TrimPlayersLocked(keep);
            }

            TrimNamesLocked(force: true);
        }

        HitPoints.RetainOnly(keep);
        Combat.RetainOnly(keep);
    }

    private static readonly IReadOnlySet<int> NoIds = new HashSet<int>();

    // Called under _gate: only the party's players, the local player and `keep` stay.
    private void TrimPlayersLocked(IReadOnlySet<int> keep)
    {
        int local = _explicitLocalId;
        int? pinned = Pinned;
        bool Wanted(int id) => id == local || id == pinned || keep.Contains(id) || (_names.TryGetValue(id, out string? n) && IsPartyNameLocked(n));
        foreach (int id in _classVotes.Keys.Where(id => !Wanted(id)).ToList())
        {
            _classVotes.Remove(id);
        }

        foreach (int id in _names.Keys.Where(id => !Wanted(id)).ToList())
        {
            if (_ids.TryGetValue(_names[id], out int byName) && byName == id)
            {
                _ids.Remove(_names[id]);
            }

            _names.Remove(id);
        }

        foreach (string name in _idOfName.Keys.Where(n => !IsPartyNameLocked(n)).ToList())
        {
            _idOfName.Remove(name);
        }

    }

    /// <summary>Değiştirildi (eklendi): isim anahtarlı tablolar bundan büyükse partide olmayan isimler atılır.</summary>
    private const int MaxNames = 5000;

    // Called under _gate.
    private bool IsPartyNameLocked(string name) => _party.Contains(name) || name == _character?.Name || name == _configuredLocalName;

    // Called under _gate. Cheap unless a table passed MaxNames (or force: on a zone change).
    private void TrimNamesLocked(bool force = false)
    {
        int limit = force ? MaxNames / 2 : MaxNames;
        TrimNameKeys(_serverOfName, limit);
        TrimNameKeys(_characterOfName, limit);
        TrimNameKeys(_otherServerIds, limit);
        TrimNameKeys(_partyClasses, limit);
        TrimNameKeys(_roster, limit);
        TrimNameKeys(_idOfName, limit);
        TrimNameSet(_announcedInView, limit);
        TrimNameSet(_homeServerKnown, limit);
        TrimNameSet(_notPlayers, limit);
    }

    private void TrimNameKeys<T>(Dictionary<string, T> table, int limit)
    {
        if (table.Count > limit)
        {
            foreach (string name in table.Keys.Where(n => !IsPartyNameLocked(n)).ToList())
            {
                table.Remove(name);
            }
        }
    }

    private void TrimNameSet(HashSet<string> set, int limit)
    {
        if (set.Count > limit)
        {
            set.RemoveWhere(n => !IsPartyNameLocked(n));
        }
    }

    /// <summary>Değiştirildi (eklendi): bölge içi varlık tablolarının sınırı.</summary>
    private const int MaxZoneEntities = 5000;

    /// <summary>Değiştirildi (eklendi): motorun izlediği dövüş boss'u; tablo sınırları onu hiç atmaz.</summary>
    public int? Pinned
    {
        get => _pinnedId == NoPin ? null : _pinnedId;
        set
        {
            _pinnedId = value ?? NoPin;
            HitPoints.Pinned = value;
        }
    }

    private const int NoPin = int.MinValue;
    private volatile int _pinnedId = NoPin;

    // Called under _gate: first the entities the server removed, then (still too many) everything but the pinned boss.
    private void TrimZoneLocked()
    {
        if (_spawned.Count <= MaxZoneEntities && _npcIds.Count <= MaxZoneEntities && _removed.Count <= MaxZoneEntities
            && _summonOwners.Count <= MaxZoneEntities && _summonOwnerNames.Count <= MaxZoneEntities && _bossNpcs.Count <= MaxZoneEntities)
        {
            return;
        }

        int? pinned = Pinned;
        var gone = _removed.Keys.Where(id => id != pinned).ToHashSet();
        foreach (int id in gone)
        {
            _spawned.Remove(id);
            _npcIds.Remove(id);
            _bossNpcs.Remove(id);
            _summonOwners.Remove(id);
            _summonOwnerNames.Remove(id);
            _removed.Remove(id);
        }

        if (_spawned.Count > MaxZoneEntities || _npcIds.Count > MaxZoneEntities || _summonOwners.Count > MaxZoneEntities || _summonOwnerNames.Count > MaxZoneEntities)
        {
            var keep = pinned is int p ? new HashSet<int> { p } : new HashSet<int>();
            RemoveAllExcept(_npcIds, keep);
            RemoveAllExcept(_bossNpcs, keep);
            RemoveAllExcept(_removed, keep);
            RemoveAllExcept(_summonOwners, keep);
            RemoveAllExcept(_summonOwnerNames, keep);
            _spawned.RemoveWhere(id => !keep.Contains(id));
        }
    }

    /// <summary>Değiştirildi (eklendi): bellek ayırmadan "bu kimlik partide mi" (kayıtlı adı son roster'da ya da yerel karakter).</summary>
    public bool IsPartyMemberId(int id)
    {
        lock (_gate)
        {
            return _names.TryGetValue(id, out string? name) && (_party.Contains(name) || name == _character?.Name);
        }
    }

    /// <summary>Değiştirildi (eklendi): bellek ayırmadan upstream RosterKnown (yerel oyuncudan başka birini adlandıran roster).</summary>
    public bool RosterKnown
    {
        get
        {
            lock (_gate)
            {
                string? own = _character?.Name;
                return _party.Count > 1 || (_party.Count == 1 && (own is null || !_party.Contains(own)));
            }
        }
    }

    private static void RemoveAllExcept<T>(Dictionary<int, T> table, IReadOnlySet<int> keep)
    {
        foreach (int id in table.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            table.Remove(id);
        }
    }

    private int _zoneEpoch;

    /// <summary>Counts the zone changes: what hangs on an entity of the zone left (the boss bar) must not outlive it.</summary>
    public int ZoneEpoch
    {
        get
        {
            lock (_gate)
            {
                return _zoneEpoch;
            }
        }
    }

    /// <summary>The player entered another zone: the marked monster and the last one hit are gone with the zone.</summary>
    public void ForgetFight()
    {
        lock (_gate)
        {
            _target = null;
            _lastHit = null;
            _zoneEpoch++;
        }
    }

    private (int EntityId, DateTime At)? _target;

    /// <summary>The monster the local player marked last (tab or click), with the time it was marked; null before any or after it died.</summary>
    public (int EntityId, int? NpcId, DateTime At)? LocalTarget
    {
        get
        {
            lock (_gate)
            {
                return _target is { } t ? (t.EntityId, _npcIds.TryGetValue(t.EntityId, out int npc) ? npc : null, t.At) : null;
            }
        }
    }

    public void SetLocalTarget(int entityId)
    {
        lock (_gate)
        {
            _target = (entityId, DateTime.UtcNow);
        }
    }

    private (int EntityId, DateTime At)? _lastHit;

    /// <summary>The monster the local player hit last, with the time: stands in for the target when the player aims with the camera (no mark message is sent then).</summary>
    public (int EntityId, int? NpcId, DateTime At)? LocalHitTarget
    {
        get
        {
            lock (_gate)
            {
                return _lastHit is { } t ? (t.EntityId, _npcIds.TryGetValue(t.EntityId, out int npc) ? npc : null, t.At) : null;
            }
        }
    }

    public void NoteLocalHit(int entityId)
    {
        lock (_gate)
        {
            _lastHit = (entityId, DateTime.UtcNow);
        }
    }

    public void ClearLocalTarget(int entityId)
    {
        lock (_gate)
        {
            if (_lastHit is { } h && h.EntityId == entityId)
            {
                _lastHit = null;
            }

            if (_target is { } t && t.EntityId == entityId)
            {
                _target = null;
            }
        }
    }

    /// <summary>The NPC id a monster was announced with, when its spawn frame was seen.</summary>
    public int? NpcIdOf(int entityId)
    {
        lock (_gate)
        {
            return _npcIds.TryGetValue(entityId, out int npcId) ? npcId : null;
        }
    }

    // Entity id -> NPC id of every monster the game announced, for its name (see MonsterNameFor).
    private readonly Dictionary<int, int> _npcIds = new();

    // Entity id -> NPC id, kept only for monsters that are bosses (see Aion2BossCatalog).
    private readonly Dictionary<int, int> _bossNpcs = new();

    /// <summary>Remembers that the monster with this entity id is the boss with this NPC id. Called
    /// when the game announces a monster; ids that are no boss are ignored.</summary>
    public void RegisterNpc(int entityId, int npcId)
    {
        lock (_gate)
        {
            _npcIds[entityId] = npcId;
            TrimZoneLocked();
        }

        if (Protocol.Aion2BossCatalog.Find(npcId) is null)
        {
            return;
        }

        lock (_gate)
        {
            _bossNpcs[entityId] = npcId;
        }

        BossSpawned?.Invoke(entityId, npcId); // Değiştirildi (eklendi): kilit dışında
    }

    /// <summary>Değiştirildi (eklendi): katalogdaki bir boss bu varlık kimliğiyle doğdu (aynı boss yeni kimlikle = wipe sonrası yeni deneme).</summary>
    public event Action<int, int>? BossSpawned;

    /// <summary>Every entity's hit points as the server reports them, and when a monster was reset
    /// to full health (a wipe and retry under the same entity id).</summary>
    public Aion2HitPoints HitPoints { get; } = new();

    /// <summary>Casts, whom entities aim at and the effects on them (see <see cref="Aion2CombatState"/>).</summary>
    public Aion2CombatState Combat { get; } = new();

    // Every entity announced by the monster-appears frame: monsters and summons, never players.
    private readonly HashSet<int> _spawned = new();

    /// <summary>Notes that the server announced this entity as a monster (or a summon). An entity id is
    /// reused, so a new spawn wipes the removal of an earlier one.</summary>
    public void NoteSpawned(int entityId)
    {
        lock (_gate)
        {
            _spawned.Add(entityId);
            _removed.Remove(entityId);
            TrimZoneLocked();
        }
    }

    /// <summary>Why and when a monster disappeared; reason 3 = it died, 7 = it vanished alive.</summary>
    public readonly record struct Removal(DateTime At, byte Reason);

    public const byte RemovedDead = 3;

    private readonly Dictionary<int, Removal> _removed = new();

    public void NoteRemoved(int entityId, byte reason, DateTime at)
    {
        lock (_gate)
        {
            _removed[entityId] = new Removal(at, reason);
            TrimZoneLocked();
        }
    }

    /// <summary>The removal of the monster's current spawn, or null while it is still there.</summary>
    public Removal? RemovalOf(int entityId)
    {
        lock (_gate)
        {
            return _removed.TryGetValue(entityId, out Removal removal) ? removal : null;
        }
    }

    /// <summary>True for an entity the server announced with the monster-appears frame (monsters
    /// and summons, never players).</summary>
    public bool IsSpawned(int entityId)
    {
        lock (_gate)
        {
            return _spawned.Contains(entityId);
        }
    }

    /// <summary>The ids of the party members (see <see cref="PartyNames"/>) who play this class.</summary>
    public IReadOnlyList<int> PartyMemberIdsOfClass(string className)
    {
        var party = PartyNames;
        lock (_gate)
        {
            return party.Where(name => _ids.ContainsKey(name)).Select(name => _ids[name]).Distinct()
                .Where(id => _classVotes.TryGetValue(id, out var votes) && votes.MaxBy(v => v.Value).Key == className)
                .ToList();
        }
    }

    /// <summary>True for an entity the server announced as a monster that is nobody's summon.</summary>
    public bool IsKnownMonster(int entityId)
    {
        lock (_gate)
        {
            return _spawned.Contains(entityId) && !_summonOwners.ContainsKey(entityId) && !_summonOwnerNames.ContainsKey(entityId);
        }
    }

    // Summoned entity id -> the player who summoned it (see Aion2FrameDecoder.DecodeNpcSpawn).
    private readonly Dictionary<int, int> _summonOwners = new();

    // Owners found since the last DrainResolvedOwners: hits the summon dealt before (credited to its
    // own id while nobody knew whose it was) can be handed to its owner.
    private readonly List<(int Summon, int Owner)> _resolvedOwners = new();

    /// <summary>The summon owners found since the last call - for re-crediting the hits a summon
    /// dealt before its owner was known (a meter started mid-fight, its first seconds).</summary>
    public IReadOnlyList<(int Summon, int Owner)> DrainResolvedOwners()
    {
        lock (_gate)
        {
            var drained = _resolvedOwners.ToList();
            _resolvedOwners.Clear();
            return drained;
        }
    }

    /// <summary>Records who summoned an entity, or (null) that it is nobody's summon - entity ids
    /// are reused, so a later spawn under the same id clears an earlier owner.</summary>
    public void SetSummonOwner(int entityId, int? ownerId)
    {
        lock (_gate)
        {
            if (ownerId is int owner)
            {
                if ((!_summonOwners.TryGetValue(entityId, out int known) || known != owner) && _resolvedOwners.Count < 4096)
                {
                    _resolvedOwners.Add((entityId, owner));
                }

                _summonOwners[entityId] = owner;
                TrimZoneLocked();
            }
            else
            {
                _summonOwners.Remove(entityId);
            }
        }
    }

    /// <summary>The player who summoned this entity, or null when it is not a known summon.</summary>
    // Summoned entity id -> its owner's name, for summons announced by name (see
    // Aion2FrameDecoder.DecodeNpcSpawn); resolved through the name -> id map when asked.
    private readonly Dictionary<int, string> _summonOwnerNames = new();

    /// <summary>Records the name a spawned entity carries - a summon's owner (a Cleric's Divine
    /// Aura carries "Psefon"); null clears it, the id being reused by something unnamed.</summary>
    public void SetSummonOwnerName(int entityId, string? ownerName)
    {
        lock (_gate)
        {
            if (ownerName is null)
            {
                _summonOwnerNames.Remove(entityId);
            }
            else
            {
                _summonOwnerNames[entityId] = ownerName;
                TrimZoneLocked();
            }
        }
    }

    /// <summary>Değiştirildi (eklendi): doğuş çerçevesinin taşıdığı sahip adı (henüz bir kimliğe bağlanmamış olsa da); yoksa null.</summary>
    public string? SummonOwnerNameOf(int entityId)
    {
        lock (_gate)
        {
            return _summonOwnerNames.GetValueOrDefault(entityId);
        }
    }

    public int? SummonOwnerOf(int entityId)
    {
        lock (_gate)
        {
            if (_summonOwners.TryGetValue(entityId, out int owner))
            {
                return owner;
            }

            // Owner known by name: the player of that name, when it is a player and not the entity itself.
            if (!_summonOwnerNames.TryGetValue(entityId, out string? name))
            {
                return null;
            }

            if (_ids.TryGetValue(name, out int byName))
            {
                return byName != entityId ? byName : null;
            }

            // Değiştirildi (eklendi): kendi adı (yerel oyuncu kendine hiç duyurulmaz, adı ancak karakter kaydıyla bir kimliğe bağlanır):
            // ayardaki ad ise çağrı yerel oyuncunundur.
            return name == _configuredLocalName && LocalPlayerId is >= 0 and var local && local != entityId ? local : null;
        }
    }

    /// <summary>Every monster recognised as a boss so far: entity id and NPC id.</summary>
    public IReadOnlyList<(int EntityId, int NpcId)> KnownBosses()
    {
        lock (_gate)
        {
            return _bossNpcs.Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    /// <summary>The boss NPC id of an entity, or null when it is no known boss.</summary>
    public int? BossNpcIdOf(int entityId)
    {
        lock (_gate)
        {
            return _bossNpcs.TryGetValue(entityId, out int npcId) ? npcId : null;
        }
    }

    private readonly Dictionary<string, int> _idOfName = new(StringComparer.Ordinal);
    // name -> server -> permanent character id. A name listed on two servers belongs to two players (a matched world holds both).
    private readonly Dictionary<string, Dictionary<int, int>> _characterOfName = new(StringComparer.Ordinal);

    /// <summary>The permanent character id of a player, as the frames that list players carry it (see OpcodeFamily.PlayerList). It never changes with the
    /// combat id, so it tells two players of one name apart (two servers of a matched world) and confirms that two combat ids are one player.</summary>
    public void NoteCharacterId(string name, int serverId, int characterId)
    {
        if (serverId is < 1000 or > 9999 || characterId <= 0 || name.Length < 2)
        {
            return;
        }

        lock (_gate)
        {
            if (!_characterOfName.TryGetValue(name, out var servers))
            {
                _characterOfName[name] = servers = new Dictionary<int, int>();
            }

            servers[serverId] = characterId;
            TrimNamesLocked(); // Değiştirildi (eklendi)
            if (!_serverOfName.ContainsKey(name))
            {
                _serverOfName[name] = serverId;
                TrimNamesLocked(); // Değiştirildi (eklendi)
            }
        }
    }

    /// <summary>The permanent character id of the player of this name on this server, when a player list named him (0 = not known).</summary>
    public int CharacterIdOf(string name, int serverId)
    {
        lock (_gate)
        {
            return _characterOfName.TryGetValue(name, out var servers) && servers.TryGetValue(serverId, out int characterId) ? characterId : 0;
        }
    }

    /// <summary>A player is one name (unique per server): when the game announces him under another combat id - a map change gives everybody new ones,
    /// and so does coming back into view - the old id is the same player and <see cref="IdentityMoved"/> says so. Called for every frame that
    /// names a combat id; <paramref name="serverId"/> is 0 where the frame does not carry it (the nickname frame). Not the same player when
    /// the servers differ, when the name is listed for two servers (two permanent character ids) and the frame does not say which, or when the two ids play other classes (a namesake of another server).</summary>
    public void NoteIdentity(int id, string name, int serverId)
    {
        if (name.Length < 2)
        {
            return;
        }

        int? moved = null;
        lock (_gate)
        {
            if (serverId is >= 1000 and <= 9999 && !_serverOfName.ContainsKey(name))
            {
                _serverOfName[name] = serverId;
                TrimNamesLocked(); // Değiştirildi (eklendi)
            }

            if (_idOfName.TryGetValue(name, out int previous) && previous != id && !AreNamesakes(previous, id, name, serverId))
            {
                moved = previous;
                IdentityMovesSeen++;
            }

            _idOfName[name] = id;
            TrimNamesLocked(); // Değiştirildi (eklendi)
        }

        if (moved is int old)
        {
            IdentityMoved?.Invoke(old, id);
        }
    }

    // Called under _gate: two ids that carry the same name but cannot be one player.
    private bool AreNamesakes(int previous, int id, string name, int serverId)
    {
        if (serverId is >= 1000 and <= 9999 && _serverOfName.TryGetValue(name, out int known) && known != serverId
            && !(_otherServerIds.TryGetValue(name, out var aliases) && aliases.Contains(serverId)))
        {
            return true;
        }

        // A frame without a server (the nickname frame): a name the player lists know on two servers is two players, and which one this is stays open.
        if (serverId == 0 && _characterOfName.TryGetValue(name, out var servers) && servers.Count > 1)
        {
            return true;
        }

        string? before = ClassOf(previous), now = ClassOf(id);
        return before is not null && now is not null && before != now;
    }

    public int IdentityMovesSeen { get; private set; }

    /// <summary>A player known under one combat id was announced under another (same name, same server).</summary>
    public event Action<int, int>? IdentityMoved;

    private readonly Dictionary<string, int> _serverOfName = new(StringComparer.Ordinal);

    // Players whose server came from the party roster's second id (see NoteHomeServerOfName), and the other ids the game printed in front of their name.
    private readonly HashSet<string> _homeServerKnown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _otherServerIds = new(StringComparer.Ordinal);

    /// <summary>
    /// A player's server id as the game announced it next to his name (appearance and party frames). For a player whose home server is known from the party roster
    /// (<see cref="NoteHomeServerOfName"/>) the id is only remembered as one the game prints for him, it does not replace the home server.
    /// </summary>
    public void NoteServerOfName(string name, int serverId)
    {
        if (serverId is >= 1000 and <= 9999)
        {
            lock (_gate)
            {
                if (_announcedInView.Contains(name))
                {
                    NoteListServerOfViewedName(name, serverId);
                    return;
                }

                if (_homeServerKnown.Contains(name))
                {
                    if (_serverOfName.TryGetValue(name, out int home) && home != serverId)
                    {
                        NoteOtherServerId(name, serverId);
                    }

                    return;
                }

                _serverOfName[name] = serverId;
                TrimNamesLocked(); // Değiştirildi (eklendi)
            }
        }
    }

    /// <summary>
    /// The server id the party roster prints after a member's name: the character's server as NC lists it. The id in front of the name is the same for most players but
    /// differs for some (SaintlyOrder 2306 in front, 2303 behind; NC: Triniel 2303), and NC agreed with the one behind the name for every such player checked (2026-10-17).
    /// It wins over every other source from then on; the id in front stays known as an alias so the player's frames are not taken for a namesake.
    /// </summary>
    public void NoteHomeServerOfName(string name, int serverId)
    {
        if (serverId is < 1000 or > 9999)
        {
            return;
        }

        lock (_gate)
        {
            if (_announcedInView.Contains(name))
            {
                NoteListServerOfViewedName(name, serverId);
                return;
            }

            if (_serverOfName.TryGetValue(name, out int previous) && previous != serverId)
            {
                NoteOtherServerId(name, previous);
            }

            _serverOfName[name] = serverId;
            TrimNamesLocked(); // Değiştirildi (eklendi)
            _homeServerKnown.Add(name);
        }
    }

    // Names whose server a "player appeared" frame announced for a player in view. The lists (a friend or legion entry, a character window, the party roster) name
    // other players of the same name as well - hundreds of names of both sides and of every server in one frame - so a list entry never replaces the server of a player
    // in view; it is only remembered as an id the game prints for that name. Without this a namesake of the other side or another server was filed as the
    // server of a player of a world boss crowd (valid ids of other characters, 0.3-2 % of a crowd, the same ones for the whole session).
    private readonly HashSet<string> _announcedInView = new(StringComparer.Ordinal);

    /// <summary>The server id a "player appeared" frame names next to a player in view: his own frame, so it beats everything a list said about that name.</summary>
    public void NoteServerOfNameInView(string name, int serverId)
    {
        if (serverId is < 1000 or > 9999)
        {
            return;
        }

        lock (_gate)
        {
            if (_serverOfName.TryGetValue(name, out int before) && before != serverId)
            {
                NoteOtherServerId(name, before);
            }

            _serverOfName[name] = serverId;
            TrimNamesLocked(); // Değiştirildi (eklendi)
            _announcedInView.Add(name);
            _homeServerKnown.Remove(name);
        }
    }

    // Called under _gate.
    private void NoteListServerOfViewedName(string name, int serverId)
    {
        if (_serverOfName.TryGetValue(name, out int shown) && shown != serverId)
        {
            NoteOtherServerId(name, serverId);
        }
    }

    // Called under _gate.
    private void NoteOtherServerId(string name, int serverId)
    {
        if (!_otherServerIds.TryGetValue(name, out var others))
        {
            _otherServerIds[name] = others = new HashSet<int>();
        }

        others.Add(serverId);
        TrimNamesLocked(); // Değiştirildi (eklendi)
    }

    /// <summary>The server id the game announced next to a name (a player known only by his name, e.g. one whose character window was opened); null when none was seen.</summary>
    public int? ServerIdOfName(string name)
    {
        lock (_gate)
        {
            return _serverOfName.TryGetValue(name, out int server) ? server : null;
        }
    }

    /// <summary>The server of a player: the own one from the character record, another one's from the frames that named him.</summary>
    public int? ServerIdOf(int id)
    {
        lock (_gate)
        {
            if (IsLocalPlayer(id) && _character is { ServerId: > 0 } own)
            {
                return own.ServerId;
            }

            return _names.TryGetValue(id, out string? name) && _serverOfName.TryGetValue(name, out int server) ? server : null;
        }
    }

    /// <summary>Adds a name from the party roster frame. The roster lists the local player too, who
    /// is the one member whose own nickname frame never arrives (everyone else "appears" to you).</summary>
    public void NoteRosterName(string name)
    {
        lock (_gate)
        {
            _roster[name] = _roster.GetValueOrDefault(name) + 1;
            TrimNamesLocked(); // Değiştirildi (eklendi)
        }
    }

    // The members the latest roster frame listed, the local player included.
    private HashSet<string> _party = new(StringComparer.Ordinal);

    /// <summary>Notes a party roster frame: it lists the group as it is now, so it replaces the previous one.
    /// A frame changes only when somebody joins or leaves, and a party breaking up ends with a frame naming the
    /// local player alone. Names used to be kept for 90 s after their last mention, so whoever left within 90 s
    /// of the last frame stayed in the group for good (found in StacysView's fork, 2026-10-17).</summary>
    public void NoteParty(IReadOnlyCollection<string> names, DateTime at)
    {
        lock (_gate)
        {
            _party = new HashSet<string>(names, StringComparer.Ordinal);
            MatchPartyMembersByClass();
        }
    }

    // Party member name -> class, from the roster's class code.
    private readonly Dictionary<string, string> _partyClasses = new(StringComparer.Ordinal);

    // Monsters the local player or a named party member hit, and the unnamed players who hit one too.
    private readonly HashSet<int> _partyTargets = new();
    private readonly HashSet<int> _fightingAlongside = new();

    // Unnamed players fighting alongside the party -> the skills (base ids) they were seen using.
    private readonly Dictionary<int, HashSet<int>> _skillsSeen = new();

    /// <summary>A player uses many skills; a spirit summoned before the meter started (owner
    /// unknown) a few: 16 against 3 on a Krao Cave capture replayed from mid-fight.</summary>
    private const int MinSkillsOfAPlayer = 4;

    /// <summary>True when a name is registered for this id - a player, never a summon.</summary>
    public bool HasName(int id)
    {
        lock (_gate)
        {
            return _names.ContainsKey(id);
        }
    }

    /// <summary>The party's players of a class by id: the members a frame named, and the local
    /// player when it plays that class (its own name may be known only from Settings).</summary>
    public IReadOnlyList<int> PartyPlayerIdsOfClass(string className)
    {
        var ids = PartyMemberIdsOfClass(className).ToList();
        // Değiştirildi: açık yerel kimlik varken tahmine bakılmaz (LocalPlayerId önce açık kimliği, sonra kayıtlı adı verir).
        if (LocalPlayerId is >= 0 and var local && !ids.Contains(local) && ClassOf(local) == className)
        {
            ids.Add(local);
        }

        return ids;
    }

    /// <summary>Notes a party member's class, as the roster gives it.</summary>
    public void NotePartyClass(string name, string className)
    {
        lock (_gate)
        {
            _partyClasses[name] = className;
            TrimNamesLocked(); // Değiştirildi (eklendi)
        }
    }

    /// <summary>Değiştirildi (eklendi): parti üyesinin roster'daki sınıfı (henüz beceri görülmeden), yoksa null.</summary>
    public string? PartyClassOf(string name)
    {
        lock (_gate)
        {
            return _partyClasses.GetValueOrDefault(name);
        }
    }

    /// <summary>Değiştirildi (eklendi): ismin şu anki savaş kimliği, yoksa null.</summary>
    public int? IdOfName(string name)
    {
        lock (_gate)
        {
            return _ids.TryGetValue(name, out int id) ? id : null;
        }
    }

    // Değiştirildi (eklendi): isim -> savaş gücü ("player appeared" çerçevesinden). İsimler kalıcı olduğu için bölge değişiminde silinmez; en çok MaxCombatPowers kayıt.
    private readonly Dictionary<string, int> _combatPower = new(StringComparer.Ordinal);
    private const int MaxCombatPowers = 200;

    public void NoteCombatPower(string name, int combatPower)
    {
        lock (_gate)
        {
            if (_combatPower.Count >= MaxCombatPowers && !_combatPower.ContainsKey(name))
            {
                foreach (string other in _combatPower.Keys.Where(n => !_party.Contains(n)).ToList())
                {
                    _combatPower.Remove(other);
                }

                if (_combatPower.Count >= MaxCombatPowers)
                {
                    _combatPower.Clear();
                }
            }

            _combatPower[name] = combatPower;
        }
    }

    public int? CombatPowerOf(string name)
    {
        lock (_gate)
        {
            return _combatPower.TryGetValue(name, out int cp) ? cp : null;
        }
    }

    /// <summary>Notes a player's hit on a monster, for <see cref="MatchPartyMembersByClass"/>.</summary>
    public void NoteMonsterHit(int playerId, int monsterId, int skillId)
    {
        lock (_gate)
        {
            bool partySide = _names.TryGetValue(playerId, out string? name)
                ? CurrentPartyNames().Contains(name)
                : IsLocalPlayer(playerId); // Değiştirildi: açık yerel kimlik varken tahmin ayrıca sayılmaz
            if (!_names.ContainsKey(playerId) && _target is { } mark && mark.EntityId == monsterId)
            {
                // Değiştirildi (eklendi): yerel oyuncunun işaretlediği canavara vuran isimsiz oyuncular (yerel tahmini için, InferLocalPlayer)
                if (_markHits.Count >= 256 && !_markHits.ContainsKey(playerId))
                {
                    _markHits.Clear();
                }

                _markHits[playerId] = _markHits.GetValueOrDefault(playerId) + 1;
            }

            if (partySide)
            {
                if (_partyTargets.Count > 4096)
                {
                    _partyTargets.Clear();
                }

                _partyTargets.Add(monsterId);
            }
            else if (!_names.ContainsKey(playerId) && !_spawned.Contains(playerId) && _partyTargets.Contains(monsterId))
            {
                _fightingAlongside.Add(playerId);
                if (!_skillsSeen.TryGetValue(playerId, out var skills))
                {
                    skills = new HashSet<int>();
                    _skillsSeen[playerId] = skills;
                }

                if (skills.Add(skillId / 10000) && skills.Count == MinSkillsOfAPlayer)
                {
                    MatchPartyMembersByClass();
                }
            }
        }
    }

    /// <summary>
    /// A player's name arrives only when they "appear" (zone entry, teleport): started inside a
    /// dungeon, the meter knows the party from the roster - names and classes, no combat ids - while
    /// the members fight as unnamed ids. When the party has exactly one member of a class still
    /// without an id, and exactly one unnamed player of that class fights the party's monsters, they
    /// are the same character. Summons do not count (an unclaimed Divine Aura or Bittercold Wind
    /// casts its class's skills too). With two such members or two such players nothing is
    /// guessed; the local player is left to its own rules. (Draupnir and Krao Cave captures
    /// replayed from mid-fight, 2026-10-02.)
    /// </summary>
    private void MatchPartyMembersByClass()
    {
        var party = CurrentPartyNames();
        int? local = _explicitLocalId >= 0 ? _explicitLocalId : InferLocalPlayer();
        string? localName = _character?.Name ?? _configuredLocalName;
        var unnamedMembers = party
            .Where(n => !_ids.ContainsKey(n) && n != localName && _partyClasses.ContainsKey(n))
            .GroupBy(n => _partyClasses[n])
            .Where(g => g.Count() == 1)
            .ToList();
        foreach (var member in unnamedMembers)
        {
            // A spirit summoned before the meter started has no known owner and casts its class's
            // skills too, but few different ones (MinSkillsOfAPlayer). Then one candidate, or one
            // clearly ahead (three times the next one's casts), the same rule as InferLocalPlayer.
            var candidates = _fightingAlongside
                .Where(id => !_names.ContainsKey(id) && id != local && !_spawned.Contains(id)
                    && _skillsSeen.TryGetValue(id, out var skills) && skills.Count >= MinSkillsOfAPlayer
                    && _classVotes.TryGetValue(id, out var votes) && votes.MaxBy(v => v.Value).Key == member.Key)
                .Select(id => (Id: id, Casts: _classVotes[id].Values.Sum()))
                .OrderByDescending(c => c.Casts)
                .ToList();
            if (candidates.Count == 1 || (candidates.Count > 1 && candidates[0].Casts >= 3 * candidates[1].Casts))
            {
                Register(candidates[0].Id, member.First());
                _fightingAlongside.Remove(candidates[0].Id);
            }
        }
    }

    /// <summary>Names in the local player's party: those of the latest roster frame. Empty when no roster has
    /// arrived yet.</summary>
    public IReadOnlySet<string> PartyNames
    {
        get
        {
            lock (_gate)
            {
                return CurrentPartyNames();
            }
        }
    }

    /// <summary>How many players the latest roster frame listed (the local player included); 0 when none has arrived.</summary>
    public int PartySize
    {
        get
        {
            lock (_gate)
            {
                return _party.Count;
            }
        }
    }

    private HashSet<string> CurrentPartyNames() => new(_party, StringComparer.Ordinal);

    /// <summary>A name the roster shows that is not a party member - the guild name, which every
    /// member's nickname frame repeats after its own name.</summary>
    public void NoteNonPlayerName(string name)
    {
        lock (_gate)
        {
            _notPlayers.Add(name);
            TrimNamesLocked(); // Değiştirildi (eklendi)
        }
    }

    // Değiştirildi (eklendi): isimsiz oyuncu -> yerel oyuncunun işaretlediği (hedef seçim çerçevesi yalnız yerel oyuncuya gelir) canavara vuruş sayısı.
    private readonly Dictionary<int, int> _markHits = new();

    // Per entity: a decaying count of its detailed-stats frames (see NoteDetailedStats).
    private readonly Dictionary<int, double> _detailedStats = new();

    /// <summary>
    /// The server sends an entity's detailed stats (the 4-byte group of the stats frame) to that
    /// player alone: on four captures (2026-10-02) the local player received 651 to 1,477 of them,
    /// any other entity 0 to 5. Older counts decay, so after a zone change hands the local player a
    /// new id, the new one takes over within a few frames.
    /// </summary>
    public void NoteDetailedStats(int entityId)
    {
        lock (_gate)
        {
            foreach (int id in _detailedStats.Keys.ToList())
            {
                _detailedStats[id] *= 0.95;
            }

            _detailedStats[entityId] = _detailedStats.GetValueOrDefault(entityId) + 1;
        }
    }

    /// <summary>
    /// The local player, worked out from the stream: first the entity receiving the detailed-stats
    /// frames (see <see cref="NoteDetailedStats"/>) - reliable in a crowd; else the object seen
    /// casting class skills that never got a nickname frame. Only claimed when it is unambiguous - one
    /// such object, or one clearly dominant - otherwise null and nobody is called "you".
    /// </summary>
    public int? InferLocalPlayer()
    {
        lock (_gate)
        {
            // Two passes without sorting or allocating: this runs for every row of the list on every refresh, and
            // sorting all known players each time grew with the square of their number. The picks are the ones the
            // sorted lists gave (the first of equals comes first).
            double bestStat = double.NegativeInfinity, secondStat = double.NegativeInfinity;
            int bestStatId = 0;
            foreach (KeyValuePair<int, double> stat in _detailedStats)
            {
                if (stat.Value > bestStat)
                {
                    secondStat = bestStat;
                    bestStat = stat.Value;
                    bestStatId = stat.Key;
                }
                else if (stat.Value > secondStat)
                {
                    secondStat = stat.Value;
                }
            }

            if (_detailedStats.Count > 0 && bestStat >= 5 && (_detailedStats.Count == 1 || bestStat >= 3 * secondStat))
            {
                return bestStatId;
            }

            // Değiştirildi (eklendi): işaretlenen canavara vuruşlar. Hedef seçim çerçevesi yalnız yerel oyuncuya gelir; partisiz, yan yana iki
            // kişi iki ayrı kuklaya vurunca beceri sayısı (aşağıdaki kural) daha çok vuranı "sen" sayıyordu, işaretlediğin kuklaya vuran ise sensin.
            // Partide herkes aynı canavara vurduğundan bu sayılar beceri sayısıyla aynı sıradadır; açık önde olan yoksa aşağıdaki kurala geçilir.
            int markBest = 0, markSecond = 0, markBestId = 0;
            foreach (KeyValuePair<int, int> mark in _markHits)
            {
                if (_names.ContainsKey(mark.Key) || !_classVotes.ContainsKey(mark.Key))
                {
                    continue;
                }

                if (mark.Value > markBest)
                {
                    markSecond = markBest;
                    markBest = mark.Value;
                    markBestId = mark.Key;
                }
                else if (mark.Value > markSecond)
                {
                    markSecond = mark.Value;
                }
            }

            if (markBest >= 3 && markBest >= 3 * markSecond)
            {
                return markBestId;
            }

            int unnamed = 0, bestId = 0, bestVotes = int.MinValue, secondVotes = int.MinValue;
            foreach (KeyValuePair<int, Dictionary<string, int>> entry in _classVotes)
            {
                if (_names.ContainsKey(entry.Key))
                {
                    continue;
                }

                int votes = 0;
                foreach (int count in entry.Value.Values)
                {
                    votes += count;
                }

                unnamed++;
                if (votes > bestVotes)
                {
                    secondVotes = bestVotes;
                    bestVotes = votes;
                    bestId = entry.Key;
                }
                else if (votes > secondVotes)
                {
                    secondVotes = votes;
                }
            }

            if (unnamed == 0 || (unnamed > 1 && bestVotes < 3 * secondVotes))
            {
                return null;
            }

            return bestId;
        }
    }

    /// <summary>Diagnostic line for the replay tool.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            return $"named [{string.Join(", ", _names.Values)}] roster [{string.Join(", ", _roster.Select(kv => kv.Key + "x" + kv.Value))}] notPlayers [{string.Join(", ", _notPlayers)}] leftover [{LocalRosterName()}]";
        }
    }

    // The roster name nobody else claimed: the local player's, when exactly one is left over.
    private string? LocalRosterName()
    {
        var left = _roster.Where(kv => !_ids.ContainsKey(kv.Key) && !_notPlayers.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ToList();
        if (left.Count == 0 || (left.Count > 1 && left[0].Value < 3 * left[1].Value))
        {
            return null;
        }

        return left[0].Key;
    }

    /// <summary>The registered name; else, for an object seen casting class skills, "Player #id" -
    /// the combat frames carry only ids, and a player's name arrives separately (and sometimes
    /// late), so this keeps players apart until it does. The class is shown by the row's icon and
    /// class column, so it is deliberately not repeated in the name.</summary>
    public string? NameFor(int id)
    {
        string? registered;
        lock (_gate)
        {
            registered = _names.GetValueOrDefault(id);
            if (registered is null && _bossNpcs.TryGetValue(id, out int npcId) && Protocol.Aion2BossCatalog.Find(npcId) is { } boss)
            {
                return boss.Name;
            }

            // The local player is never announced to itself, so its id has no name of its own until
            // the character record (login, zone change) arrives. Until then: the name set in
            // Settings, else the character saved from the last login, else the party roster's
            // leftover name - solo, only the first two exist, and "Player #id" used to stay.
            // Not once the local player is known for certain (a session frame's id) or the name is
            // already registered to another object: the inference then only picked the busiest
            // unnamed caster, a team mate, who used to show up as a second row under one's own name.
            if (registered is null && InferLocalPlayer() == id && !LocalPlayerKnownElsewhere(id))
            {
                // The roster's leftover name is safe here: the local player is not named yet, so its
                // own name is still among the leftovers, and a single leftover is it.
                registered = _configuredLocalName
                    ?? (_character is { Restored: true } saved && saved.Name.Length > 0 ? saved.Name : null)
                    ?? LocalRosterName();
            }
        }

        return registered ?? (ClassOf(id) is not null ? $"Player #{id}" : null);
    }

    // Called under _gate. True when something other than `id` is the local player: the explicit id
    // names someone else, or the configured / saved character name already belongs to another object.
    private bool LocalPlayerKnownElsewhere(int id)
    {
        if (_explicitLocalId >= 0)
        {
            return _explicitLocalId != id;
        }

        string? own = _configuredLocalName ?? (_character is { Restored: true } saved ? saved.Name : null);
        return own is not null && _ids.TryGetValue(own, out int owner) && owner != id;
    }

    /// <summary>The class an object has most often been seen casting, or null.</summary>
    public string? ClassOf(int id)
    {
        lock (_gate)
        {
            return _classVotes.TryGetValue(id, out var votes) ? votes.MaxBy(v => v.Value).Key : null;
        }
    }

    /// <summary>Remembers which class an object plays, learned from the class prefix of its skills.</summary>
    public void NoteClass(int id, string className)
    {
        lock (_gate)
        {
            if (!_classVotes.TryGetValue(id, out var votes))
            {
                votes = new Dictionary<string, int>();
                _classVotes[id] = votes;
            }

            votes[className] = votes.GetValueOrDefault(className) + 1;
            if (_classVotes.Count > 2 * MaxKnownPlayers || _names.Count > 2 * MaxKnownPlayers)
            {
                TrimPlayersLocked(NoIds); // Değiştirildi (eklendi): bölge değişmeden de sınır
            }
        }
    }

    /// <summary>True once the object has been seen using a class skill - i.e. it is a player.</summary>
    public bool IsKnownPlayer(int id)
    {
        lock (_gate)
        {
            return _classVotes.ContainsKey(id);
        }
    }

    public int GetOrAssignId(string name)
    {
        if (_ids.TryGetValue(name, out int id))
        {
            return id;
        }

        id = -(_ids.Count + 2);
        Register(id, name);
        return id;
    }

    public bool IsLocalPlayer(int id) => LocalPlayerId is >= 0 and var local && id == local;

    public void Register(int id, string name)
    {
        lock (_gate)
        {
            // An id that now belongs to another player must not keep the legion of the one before (ids are handed out again).
            if (_names.TryGetValue(id, out string? before) && before != name)
            {
                // The id now belongs to another player: the old name must not point to it any more.
                if (_ids.TryGetValue(before, out int ofBefore) && ofBefore == id)
                {
                    _ids.Remove(before);
                }
            }

            _names[id] = name;
            if (_classVotes.Count > 2 * MaxKnownPlayers || _names.Count > 2 * MaxKnownPlayers)
            {
                TrimPlayersLocked(NoIds); // Değiştirildi (eklendi): bölge değişmeden de sınır
            }
            _ids[name] = id;

            // A summon announced by its owner's name (a Cleric's Divine Aura) before that name was known to belong to an id: its first hits
            // stayed on the summon's own id ("Player #id"); now the owner is found, so they are handed over (see DrainResolvedOwners).
            foreach ((int summon, string ownerName) in _summonOwnerNames)
            {
                // Değiştirildi: tahminle yazılmış bir sahip (tek aday kuralı) doğuş çerçevesindeki ada yenilir; ad kesin bilgidir.
                if (ownerName == name && summon != id && (!_summonOwners.TryGetValue(summon, out int guessed) || guessed != id) && _resolvedOwners.Count < 4096)
                {
                    _summonOwners[summon] = id;
                    _resolvedOwners.Add((summon, id));
                }
            }
        }
    }
}
