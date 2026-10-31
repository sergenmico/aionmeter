using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;
using AionDPS.Combat;
using AionDPS.Combat.Sources;

namespace AionMeter.Core;

/// <summary>
/// AION 2 hasar ölçer motoru: Npcap ile oyun trafiğini pasif okur, parti oyuncularının hasarını (çağrılanlar sahibine
/// eklenmiş) ve o anki boss'un canını verir. Çözme yakalama işçi iş parçacığında yapılır; vuruş başına kayıt tutulmaz,
/// kaynak başına yalnız sayaçlar tutulur.
/// <para>Kilit sırası her yerde aynıdır: motorun <c>_gate</c>'i, sonra dizinin (Aion2EntityDirectory / Aion2HitPoints) kendi kilidi.
/// Dizin olaylarını (LocalIdChanged, IdentityMoved) kendi kilidi dışında verir.</para>
/// </summary>
public sealed class Aion2MeterEngine : IMeterEngine
{
    /// <summary>Boss tutmuyorsa bu kadar parti hasarı gelmezse dövüş biter; sonraki parti vuruşu yeni dövüş başlatır.</summary>
    private static readonly TimeSpan FightGap = TimeSpan.FromSeconds(20);

    /// <summary>Son vuruştan bu kadar sonra dövüş süresi son vuruşta durur.</summary>
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(3);

    /// <summary>Wipe: dövüş boss'u tam cana döndü ve bu kadar parti hasarı yok.</summary>
    private static readonly TimeSpan WipeIdle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Boss'un son can okuması bundan eskiyse dövüşü tutmaz. Sadece yedek: ayrılan boss'u kaldırma kaydı, sıfırlananı wipe kuralı yakalar.
    /// Uzun tutuldu çünkü takım ölü/dirilirken kimse vurmaz, can okuması gelmez ve dövüş bölünmemeli (kullanıcı kuralı).
    /// </summary>
    private static readonly TimeSpan BossStale = TimeSpan.FromMinutes(5);

    /// <summary>Ölen dövüş boss'una bu süre içinde gelen geç tikler yeni dövüş başlatmaz.</summary>
    private static readonly TimeSpan DeadBossGuard = TimeSpan.FromSeconds(30);

    /// <summary>Boss bu oranın üstündeyse tam canda sayılır (sıfırlanma / wipe).</summary>
    private const double FullHpShare = 0.995;

    /// <summary>Bir dövüşte en çok bu kadar farklı kaynak tutulur (kalabalık için RAM sınırı); doluysa en az hasarlı yabancı atılır. Parti/yerel her zaman tutulur.</summary>
    private const int MaxSources = 300;

    /// <summary>Tam detay (beceriler, saniyelik kovalar, vuruş sayaçları) yalnız hasarda ilk bu kadar yabancı + yerel + parti için tutulur.</summary>
    private const int MaxDetailedStrangers = 30;

    /// <summary>Roster yokken "aynı hedefe vuranlar" sözde partisinden en çok bu kadar kişi gösterilir (en çok hasar yapanlar).</summary>
    private const int MaxPseudoParty = 12;

    /// <summary>Yerel oyuncunun vurduğu hedeflerden en çok bu kadarı hatırlanır (roster yokken "aynı hedefe vuranlar").</summary>
    private const int MaxLocalTargets = 256;

    /// <summary>Oyuncu başına en çok bu kadar beceri tutulur.</summary>
    private const int MaxSkillsPerPlayer = 200;

    /// <summary>Kalan süre tahmini: son 30 sn, saniyelik kovalar; en çok bu kadar boss hedefi izlenir (oyuncu başına da).</summary>
    private const int TtkSeconds = 30;
    private const int MaxBossTargets = 8;

    /// <summary>DPS grafiği: oyuncu başına en çok bu kadar kova; aşılınca ikişer birleştirilir, kova süresi iki katına çıkar.</summary>
    private const int MaxBuckets = 900;

    // Upstream CombatOverlay (Ui/CombatOverlay.cs) sabitleri.
    private static readonly TimeSpan CastGrace = TimeSpan.FromMilliseconds(700);
    private const long BossHitPoints = 500_000;
    private static readonly TimeSpan BossHold = TimeSpan.FromSeconds(15);

    private readonly string _assetsDir;
    private readonly object _gate = new(); // her şey: hasar toplamları, dövüş durumu, boss seçimi
    private readonly Dictionary<int, Aggregate> _bySource = new();
    private readonly HashSet<int> _localTargets = new();
    private readonly Dictionary<int, DamageRing> _bossDamage = new(); // boss adayı -> partinin ona hasarı
    private DateTime? _fightStart;
    private DateTime? _lastPartyHit;
    private int _fightEpoch;

    // Detay kümesi: yabancılardan detay tutanların sayısı ve ilk 30'a girme eşiği (saniyede bir yeniden hesaplanır).
    private int _detailedStrangers;
    private long _detailThreshold = long.MaxValue;
    private long _detailCheckedAt = -1_000_000;

    // Dövüş boss'u: partinin hasar verdiği boss adaylarından en çok hasar alanı; ölene, ayrılana ya da sıfırlanana kadar değişmez.
    private int? _fightBoss;
    private bool _fightBossWounded;

    // Dövüş bitti: anlık görüntü yeni dövüşün ilk parti vuruşuna kadar donar.
    private bool _fightOver;
    private int? _deadBoss;
    private DateTime _deadBossAt;
    private MeterSnapshot? _frozen;
    private MeterSnapshot? _lastLive; // hasar içeren son canlı görüntü (bölge değişiminde donmuş sonuç olarak kalır)

    // Upstream boss tutma (_lastBoss) durumu.
    private (int Id, DateTime At, int Epoch)? _lastBoss;

    // Yerel oyuncu önbelleği (InferLocalPlayer O(n) olduğu için vuruş başına değil, saniyede bir).
    private int _localId = -1;
    private int? _inferredLocal;
    private long _localCheckedAt = -1_000_000;

    private volatile Aion2PacketCombatSource? _source;
    private volatile EngineState _state = EngineState.Starting;
    private volatile string? _message;
    private int _started;
    private bool _disposed;

    private sealed class Aggregate
    {
        public long Total;
        public DateTime First;
        public DateTime Last;
        public bool SharesTarget; // yerel oyuncunun vurduğu bir hedefe vurdu (roster yokken parti tahmini)
        public bool Party;        // yerel oyuncu ya da parti üyesi olarak vurdu (atılmaz, detayı hep tutulur)
        public readonly Dictionary<int, (long Amount, DateTime Last)> BossDamage = new(); // boss hedefi -> bu oyuncunun ona hasarı
        public AggDetail? Detail;  // yalnız ilk 30 yabancı + yerel + parti

        public void AddBossDamage(int target, long amount, DateTime at, int? fightBoss)
        {
            if (!BossDamage.TryGetValue(target, out var known) && BossDamage.Count >= MaxBossTargets)
            {
                // en uzun süredir vurulmayan atılır; dövüş boss'u hiç atılmaz
                int? oldest = null;
                DateTime oldestAt = DateTime.MaxValue;
                foreach ((int id, (long _, DateTime last)) in BossDamage)
                {
                    if (id != fightBoss && last < oldestAt)
                    {
                        oldest = id;
                        oldestAt = last;
                    }
                }

                if (oldest is int drop)
                {
                    BossDamage.Remove(drop);
                }
            }

            BossDamage[target] = (known.Amount + amount, at > known.Last ? at : known.Last);
        }

        /// <summary>Kimlik birleşmesi: <paramref name="other"/>'ın her şeyi buna eklenir.</summary>
        public void Absorb(Aggregate other, int? fightBoss)
        {
            Total += other.Total;
            First = other.First < First ? other.First : First;
            Last = other.Last > Last ? other.Last : Last;
            SharesTarget |= other.SharesTarget;
            Party |= other.Party;
            foreach ((int boss, (long amount, DateTime last)) in other.BossDamage)
            {
                AddBossDamage(boss, amount, last, fightBoss);
            }

            if (other.Detail is { } theirs)
            {
                (Detail ??= new AggDetail()).Absorb(theirs);
            }
        }
    }

    private sealed class AggDetail
    {
        public long Hits, Crits, Doubles, Perfects, Backs, Parries, MaxHit;
        public readonly Dictionary<int, SkillAgg> Skills = new();
        public List<long>? Buckets; // dövüş başından itibaren kova başına hasar
        public int BucketSeconds = 1;

        public void AddToBucket(long secondFromStart, long amount)
        {
            if (secondFromStart < 0)
            {
                secondFromStart = 0;
            }

            Buckets ??= new List<long>();
            long index = secondFromStart / BucketSeconds;
            while (index >= MaxBuckets)
            {
                var merged = new List<long>((Buckets.Count + 1) / 2);
                for (int i = 0; i < Buckets.Count; i += 2)
                {
                    merged.Add(Buckets[i] + (i + 1 < Buckets.Count ? Buckets[i + 1] : 0));
                }

                Buckets = merged;
                BucketSeconds *= 2;
                index = secondFromStart / BucketSeconds;
            }

            while (Buckets.Count <= index)
            {
                Buckets.Add(0);
            }

            Buckets[(int)index] += amount;
        }

        public void Absorb(AggDetail other)
        {
            Hits += other.Hits;
            Crits += other.Crits;
            Doubles += other.Doubles;
            Perfects += other.Perfects;
            Backs += other.Backs;
            Parries += other.Parries;
            MaxHit = Math.Max(MaxHit, other.MaxHit);
            foreach ((int skillId, SkillAgg s) in other.Skills)
            {
                if (Skills.TryGetValue(skillId, out SkillAgg? mine))
                {
                    mine.Total += s.Total;
                    mine.Hits += s.Hits;
                    mine.Crits += s.Crits;
                    mine.MaxHit = Math.Max(mine.MaxHit, s.MaxHit);
                }
                else if (Skills.Count < MaxSkillsPerPlayer)
                {
                    Skills[skillId] = s;
                }
            }

            if (other.Buckets is { } theirs)
            {
                for (int i = 0; i < theirs.Count; i++)
                {
                    if (theirs[i] != 0)
                    {
                        AddToBucket((long)i * other.BucketSeconds, theirs[i]);
                    }
                }
            }
        }
    }

    private sealed class SkillAgg
    {
        public long Total, Hits, Crits, MaxHit;
    }

    /// <summary>Bir boss adayına partinin hasarı: dövüş toplamı ve son <see cref="TtkSeconds"/> saniyenin saniyelik kovaları (halka).</summary>
    private sealed class DamageRing
    {
        public readonly long[] Amount = new long[TtkSeconds];
        public readonly long[] Second = new long[TtkSeconds];
        public long Total;
        public DateTime FirstAt = DateTime.MaxValue; // partinin bu bossa ilk / son vuruşu
        public DateTime LastAt = DateTime.MinValue;

        public void Add(DateTime at, long amount)
        {
            Total += amount;
            FirstAt = at < FirstAt ? at : FirstAt;
            LastAt = at > LastAt ? at : LastAt;
            long second = at.Ticks / TimeSpan.TicksPerSecond;
            int slot = (int)(second % TtkSeconds);
            if (Second[slot] != second)
            {
                Second[slot] = second;
                Amount[slot] = 0;
            }

            Amount[slot] += amount;
        }

        /// <summary>Son 30 sn'deki toplam ve verinin kaç saniyeyi kapsadığı.</summary>
        public (long Sum, long Span) Window(long now)
        {
            long sum = 0, first = long.MaxValue;
            for (int i = 0; i < TtkSeconds; i++)
            {
                if (Amount[i] > 0 && Second[i] > now - TtkSeconds && Second[i] <= now)
                {
                    sum += Amount[i];
                    first = Math.Min(first, Second[i]);
                }
            }

            return sum > 0 ? (sum, now - first + 1) : (0, 0);
        }
    }

    public Aion2MeterEngine(string assetsDir)
    {
        _assetsDir = assetsDir;
        AssetPaths.Root = assetsDir;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // Aygıt listesi ve tablolar birkaç yüz ms sürebilir: çağıranı bekletmez.
        ThreadPool.QueueUserWorkItem(_ => StartCore());
    }

    private void StartCore()
    {
        try
        {
            Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(_assetsDir, "protocol", "opcodes.json"));
            var source = new Aion2PacketCombatSource(protocol);
            if (!Attach(source))
            {
                return;
            }

            source.Start();
            bool disposedMeanwhile;
            lock (_gate)
            {
                disposedMeanwhile = _disposed;
            }

            if (disposedMeanwhile)
            {
                source.Dispose(); // Dispose, Start sürerken geldi: yakalama kapatılır
            }
        }
        catch (Exception ex)
        {
            OnStatus(EngineState.Failed, $"Beklenmeyen hata: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>Kaynağın olaylarını motora bağlar; yakalamayı başlatmaz (denetim aracı da bunu kullanır).</summary>
    internal bool Attach(Aion2PacketCombatSource source)
    {
        source.StatusChanged += OnStatus;
        source.Hit += OnHit;
        source.ZoneChanged += OnZoneChanged;
        source.Entities.LocalIdChanged += MergeIds;
        source.Entities.IdentityMoved += MergeIds;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _source = source;
            return true;
        }
    }

    /// <summary>Denetim aracı için: çözülmüş bir olayı yakalama yolu gibi verir.</summary>
    internal void InjectHit(DamageEvent ev) => OnHit(ev);

    /// <summary>Denetim aracı için: bir bölge değişimini yakalama yolu gibi verir.</summary>
    internal void InjectZone(ZoneChange zone) => OnZoneChanged(zone);

    private void OnStatus(EngineState state, string message)
    {
        // Bağlandıktan sonra gelen "bekleniyor" iletileri (port arama) durumu geri almaz.
        if (_state == EngineState.Connected && state == EngineState.WaitingGame)
        {
            return;
        }

        _message = message;
        _state = state;
    }

    public void Reset()
    {
        lock (_gate)
        {
            ClearFight();
        }
    }

    // Called under _gate.
    private void ClearFight()
    {
        _bySource.Clear();
        _localTargets.Clear();
        _bossDamage.Clear();
        _detailedStrangers = 0;
        _detailThreshold = long.MaxValue;
        _fightStart = null;
        _lastPartyHit = null;
        _fightOver = false;
        _deadBoss = null;
        _frozen = null;
        _lastLive = null;
        SetFightBoss(null);
        _fightEpoch++;
    }

    // Called under _gate.
    private void SetFightBoss(int? id)
    {
        if (_fightBoss != id)
        {
            _fightBoss = id;
            _fightBossWounded = false;
            if (_source is { } source)
            {
                source.Entities.Pinned = id; // tablo sınırları dövüş boss'unu atmaz
            }
        }
    }

    private void OnZoneChanged(ZoneChange zone)
    {
        if (zone.Restart)
        {
            return; // aynı bölge kimliği tekrar geldi (diriliş, ışınlanma, aynı zindanın yeni turu): hiçbir şey sıfırlanmaz
        }

        lock (_gate)
        {
            // Sonuç bir sonraki parti vuruşuna kadar görünür kalır; iç dövüş durumu temizlenir (bölgenin kimlikleri geçersiz).
            MeterSnapshot? keep = _frozen ?? _lastLive;
            ClearFight();
            if (keep is not null)
            {
                _frozen = keep with { FightOver = true };
                _fightOver = true;
            }
        }
    }

    // Called under _gate. Upstream IsLocal: IsLocalPlayer(id) || InferLocalPlayer() == id, saniyede bir yenilenir.
    private bool IsLocal(Aion2EntityDirectory dir, int id, bool refresh = false)
    {
        long nowMs = Environment.TickCount64;
        if (refresh || nowMs - _localCheckedAt >= 1000 || (_localId < 0 && _inferredLocal is null))
        {
            _localCheckedAt = nowMs;
            _localId = dir.LocalPlayerId;
            _inferredLocal = dir.InferLocalPlayer();
        }

        return id == _localId || id == _inferredLocal;
    }

    /// <summary>Yakalama işçi iş parçacığında, her çözülen olay için. Sıcak yol: bellek ayırmaz (yeni kaynak/beceri dışında).</summary>
    private void OnHit(DamageEvent ev)
    {
        if (ev.IsHeal || ev.Estimated || ev.Amount <= 0 || _source is not { } source)
        {
            return;
        }

        Aion2EntityDirectory dir = source.Entities;
        int id = ev.SourceObjectId;
        int target = ev.TargetObjectId;
        lock (_gate)
        {
            bool local = IsLocal(dir, id);
            bool party = local || dir.IsPartyMemberId(id);
            if (local)
            {
                if (_localTargets.Count >= MaxLocalTargets)
                {
                    _localTargets.Clear();
                }

                _localTargets.Add(target);
            }

            bool sharesTarget = _localTargets.Contains(target);
            bool partyHit = party || (sharesTarget && !dir.RosterKnown && dir.IsKnownPlayer(id));
            if (_fightOver)
            {
                // Donmuş sonuç: parti dışı vuruşlar ve ölen boss'a geç gelen tikler yeni dövüş başlatmaz.
                bool lateOnDeadBoss = target == _deadBoss && ev.Timestamp - _deadBossAt < DeadBossGuard && DeadNow(dir, target);
                if (!partyHit || lateOnDeadBoss)
                {
                    return;
                }
            }

            if (partyHit)
            {
                // Ölüm/diriliş dövüşü sıfırlamaz; dövüş boss'u canlı ve yaralıyken boşluk ne kadar uzun olursa olsun dövüş sürer.
                if (_fightOver || _lastPartyHit is not { } last || (ev.Timestamp - last >= FightGap && !BossHoldsFight(dir, _fightBoss, ev.Timestamp)))
                {
                    ClearFight();
                    _fightStart = ev.Timestamp;
                    if (local)
                    {
                        _localTargets.Add(target);
                    }
                }

                if (_lastPartyHit is not { } before || ev.Timestamp > before)
                {
                    _lastPartyHit = ev.Timestamp;
                }
            }

            if (_fightStart is not { } fightStart)
            {
                return; // dövüş partinin ilk vuruşuyla başlar
            }

            if (!_bySource.TryGetValue(id, out Aggregate? agg))
            {
                if (_bySource.Count >= MaxSources && !party && !EvictWeakestStranger(ev.Amount))
                {
                    return; // kalabalık: bu vuruş en zayıf yabancıdan da küçük
                }

                _bySource[id] = agg = new Aggregate { First = ev.Timestamp, Last = ev.Timestamp };
            }

            agg.Party |= party;

            agg.Total += ev.Amount;
            if (ev.Timestamp < agg.First)
            {
                agg.First = ev.Timestamp;
            }

            if (ev.Timestamp > agg.Last)
            {
                agg.Last = ev.Timestamp;
            }

            agg.SharesTarget |= sharesTarget;
            RefreshDetailSet();
            if (agg.Detail is null && (agg.Party || _detailedStrangers < MaxDetailedStrangers || agg.Total >= _detailThreshold))
            {
                agg.Detail = new AggDetail(); // ilk 30'a sonradan giren o andan itibaren detay toplar
                if (!agg.Party)
                {
                    _detailedStrangers++;
                }
            }

            if (agg.Detail is { } det)
            {
                // Vuruş sayaçları: DoT tikleri hasara eklenir ama vuruş/kritik sayılmaz (upstream DamageEvent.IsTick).
                SkillAgg? skill = null;
                if (det.Skills.TryGetValue(ev.SkillId, out SkillAgg? known) || det.Skills.Count < MaxSkillsPerPlayer)
                {
                    skill = known ?? (det.Skills[ev.SkillId] = new SkillAgg());
                    skill.Total += ev.Amount;
                }

                if (!ev.IsTick)
                {
                    det.Hits++;
                    det.MaxHit = Math.Max(det.MaxHit, ev.Amount);
                    if (ev.IsCritical) { det.Crits++; }
                    if (ev.Kinds is { } kinds)
                    {
                        if ((kinds & HitKinds.Double) != 0) { det.Doubles++; }
                        if ((kinds & HitKinds.Perfect) != 0) { det.Perfects++; }
                        if ((kinds & HitKinds.Back) != 0) { det.Backs++; }
                        if ((kinds & HitKinds.Parry) != 0) { det.Parries++; }
                    }

                    if (skill is not null)
                    {
                        skill.Hits++;
                        skill.MaxHit = Math.Max(skill.MaxHit, ev.Amount);
                        if (ev.IsCritical) { skill.Crits++; }
                    }
                }

                det.AddToBucket((long)(ev.Timestamp - fightStart).TotalSeconds, ev.Amount);
            }

            if (!IsBoss(dir, target))
            {
                return;
            }

            agg.AddBossDamage(target, ev.Amount, ev.Timestamp, _fightBoss);
            if (!partyHit)
            {
                return;
            }

            if (!_bossDamage.TryGetValue(target, out DamageRing? ring))
            {
                if (_bossDamage.Count >= MaxBossTargets)
                {
                    int drop = _bossDamage.Where(kv => kv.Key != _fightBoss).MinBy(kv => kv.Value.LastAt).Key;
                    _bossDamage.Remove(drop);
                }

                _bossDamage[target] = ring = new DamageRing();
            }

            ring.Add(ev.Timestamp, ev.Amount);
            UpdateFightBoss(dir, ev.Timestamp);
        }
    }

    // Called under _gate. Dolu tabloda yeni bir yabancı için en az hasarlı yabancı atılır; yalnız yeni vuruş ondan büyükse (sürekli giriş-çıkış olmasın).
    private bool EvictWeakestStranger(long incoming)
    {
        int? weakest = null;
        long weakestTotal = long.MaxValue;
        foreach ((int id, Aggregate agg) in _bySource)
        {
            if (!agg.Party && agg.Total < weakestTotal)
            {
                weakest = id;
                weakestTotal = agg.Total;
            }
        }

        if (weakest is not int drop || weakestTotal >= incoming)
        {
            return false;
        }

        if (_bySource[drop].Detail is not null)
        {
            _detailedStrangers--;
        }

        _bySource.Remove(drop);
        return true;
    }

    // Called under _gate. Saniyede bir: yabancılardan hasarda ilk 30'un eşiği bulunur, ilk 30 dışındakilerin detayı atılır.
    private void RefreshDetailSet()
    {
        long nowMs = Environment.TickCount64;
        if (nowMs - _detailCheckedAt < 1000)
        {
            return;
        }

        _detailCheckedAt = nowMs;
        _strangerTotals.Clear();
        foreach (Aggregate agg in _bySource.Values)
        {
            if (!agg.Party)
            {
                _strangerTotals.Add(agg.Total);
            }
        }

        if (_strangerTotals.Count <= MaxDetailedStrangers)
        {
            _detailThreshold = long.MaxValue; // herkes zaten sığıyor (sayaç sınırı yeter)
            return;
        }

        _strangerTotals.Sort();
        _detailThreshold = _strangerTotals[^MaxDetailedStrangers];
        foreach (Aggregate agg in _bySource.Values)
        {
            if (!agg.Party && agg.Detail is not null && agg.Total < _detailThreshold)
            {
                agg.Detail = null;
                _detailedStrangers--;
            }
        }
    }

    private readonly List<long> _strangerTotals = new();

    // Called under _gate. Dövüş boss'u ölene, ayrılana ya da sıfırlanana kadar kalır; yoksa / gittiyse partinin en çok hasar verdiği aday olur.
    private void UpdateFightBoss(Aion2EntityDirectory dir, DateTime at)
    {
        if (_fightBoss is int current && !BossGone(dir, current, at))
        {
            NoteWound(dir, current);
            return;
        }

        int? best = null;
        long bestTotal = 0;
        foreach ((int id, DamageRing ring) in _bossDamage)
        {
            if (ring.Total > bestTotal && !BossGone(dir, id, at) && !DeadNow(dir, id))
            {
                best = id;
                bestTotal = ring.Total;
            }
        }

        SetFightBoss(best);

        if (_fightBoss is int chosen)
        {
            NoteWound(dir, chosen);
        }
    }

    // Called under _gate.
    private void NoteWound(Aion2EntityDirectory dir, int id)
    {
        if (id == _fightBoss && dir.HitPoints.Latest(id)?.Hp is long hp && Aion2BossHp.RealMax(dir, id) is long max && max > 0 && hp < max * FullHpShare)
        {
            _fightBossWounded = true;
        }
    }

    /// <summary>Boss ayrıldı (sunucu kaldırdı, ölüm dışı bir nedenle), can okuması yok ya da son okuması <see cref="BossStale"/>'den eski.</summary>
    private static bool BossGone(Aion2EntityDirectory dir, int id, DateTime now) =>
        dir.RemovalOf(id) is { Reason: not Aion2EntityDirectory.RemovedDead }
        || dir.HitPoints.Latest(id) is not { } sample
        || now - sample.At > BossStale;

    /// <summary>Dövüş boss'u canlı, yerinde ve yaralıysa (tam cana dönmediyse) dövüş boşta kalmakla bitmez.</summary>
    private static bool BossHoldsFight(Aion2EntityDirectory dir, int? boss, DateTime now)
    {
        if (boss is not int id || dir.RemovalOf(id) is not null || dir.HitPoints.Latest(id) is not { } sample
            || now - sample.At > BossStale || IsDead(dir, id, sample.Hp))
        {
            return false;
        }

        long max = Aion2BossHp.RealMax(dir, id) ?? 0;
        return max > 0 && sample.Hp < max * FullHpShare;
    }

    /// <summary>Kalan can ÷ son 30 sn'deki parti DPS'i (bossa); 3 sn'den az veri ya da hasar yoksa null. Called under _gate.</summary>
    private TimeSpan? TimeToKill(int boss, long hp, DateTime now)
    {
        if (!_bossDamage.TryGetValue(boss, out DamageRing? ring))
        {
            return null;
        }

        (long sum, long span) = ring.Window(now.Ticks / TimeSpan.TicksPerSecond);
        if (sum <= 0 || span < 3)
        {
            return null;
        }

        double dps = (double)sum / span;
        return TimeSpan.FromSeconds(hp / dps);
    }

    /// <summary>Öfkeye kalan: boss_rage.json süresi − partinin bossa ilk vuruşundan beri geçen (ölüyse son vuruşta donar); ilk vuruş bilinmiyorsa null. Called under _gate.</summary>
    private TimeSpan? TimeToRage(Aion2EntityDirectory dir, int boss, bool dead, DateTime now)
    {
        if (!_bossDamage.TryGetValue(boss, out DamageRing? ring) || ring.FirstAt == DateTime.MaxValue)
        {
            return null;
        }

        TimeSpan elapsed = (dead ? ring.LastAt : now) - ring.FirstAt;
        return TimeSpan.FromSeconds(Aion2BossRage.SecondsFor(dir.BossNpcIdOf(boss))) - elapsed;
    }

    public PlayerDetail? Detail(long playerId)
    {
        if (_source is null || playerId is < int.MinValue or > int.MaxValue)
        {
            return null;
        }

        int id = (int)playerId;
        // Satır (ad, sınıf, CP, toplam, DPS) anlık görüntüden: donmuşsa donmuş olandan, DPS dövüş süresine göre.
        PlayerDamage? row = Snapshot().Players.FirstOrDefault(p => p.Id == playerId);
        if (row is null)
        {
            return null;
        }

        HitStats stats;
        List<(int SkillId, long Total, long Hits, long Crits, long MaxHit)> skills;
        TimeSpan active;
        long[]? buckets;
        int bucketSeconds;
        lock (_gate)
        {
            if (!_bySource.TryGetValue(id, out Aggregate? agg))
            {
                return new PlayerDetail(row.Id, row.Name, row.ClassName, row.CombatPower, 0, 0, TimeSpan.Zero, new HitStats(0, 0, 0, 0, 0, 0, 0), Array.Empty<SkillDamage>());
            }

            active = agg.Last - agg.First;
            AggDetail? det = agg.Detail; // kalabalıkta ilk 30 dışındaki yabancının detayı yok: yalnız toplam
            stats = det is null ? new HitStats(0, 0, 0, 0, 0, 0, 0) : new HitStats(det.Hits, det.Crits, det.Doubles, det.Perfects, det.Backs, det.Parries, det.MaxHit);
            skills = det is null ? new() : det.Skills.Select(kv => (kv.Key, kv.Value.Total, kv.Value.Hits, kv.Value.Crits, kv.Value.MaxHit)).ToList();
            buckets = det?.Buckets?.ToArray();
            bucketSeconds = det?.BucketSeconds ?? 1;
        }

        var list = skills
            .OrderByDescending(s => s.Total)
            .Select(s => new SkillDamage(s.SkillId, Aion2SkillNames.NameOf(s.SkillId) ?? "Skill #" + s.SkillId, s.Total, s.Hits, s.Crits, s.MaxHit, Aion2SkillIcons.TokenFor(s.SkillId)))
            .ToList();
        return new PlayerDetail(row.Id, row.Name, row.ClassName, row.CombatPower, row.Total, row.Dps, active, stats, list, buckets ?? Array.Empty<long>(), bucketSeconds);
    }

    /// <summary>Upstream: LocalIdChanged / IdentityMoved - eski kimlik ile yeni kimlik aynı oyuncu, satırlar birleşir.</summary>
    private void MergeIds(int oldId, int newId)
    {
        lock (_gate)
        {
            Reattribute(oldId, newId);
        }
    }

    // Called under _gate.
    private void Reattribute(int from, int to)
    {
        if (from == to || !_bySource.Remove(from, out Aggregate? moved))
        {
            return;
        }

        if (!_bySource.TryGetValue(to, out Aggregate? into))
        {
            _bySource[to] = moved;
            return;
        }

        into.Absorb(moved, _fightBoss);
    }

    private readonly record struct Row(int Id, long Total, bool SharesTarget, long BossDamage, bool HitFightBoss);

    public MeterSnapshot Snapshot()
    {
        EngineState state = _state;
        string? message = _message;
        if (_source is not { } source)
        {
            return new MeterSnapshot(state, message, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);
        }

        Aion2EntityDirectory dir = source.Entities;
        DateTime now = DateTime.Now; // olay zamanları yerel saat (NpcapCaptureService: Timeval.ToLocalTime)
        lock (_gate)
        {
            // Sahibi sonradan bulunan çağrıların hasarı sahibine geçer (upstream MainWindow DrainResolvedOwners).
            foreach ((int summon, int owner) in dir.DrainResolvedOwners())
            {
                Reattribute(summon, owner);
            }

            if (_frozen is { } frozen)
            {
                return frozen with { State = state, Message = message };
            }

            if (_fightBoss is not null)
            {
                UpdateFightBoss(dir, now);
            }

            BossHp? boss = BuildBoss(dir, now);
            int? bossId = boss is null ? null : (int)boss.Id;
            var rows = new List<Row>(_bySource.Count);
            foreach ((int id, Aggregate agg) in _bySource)
            {
                rows.Add(new Row(id, agg.Total, agg.SharesTarget, bossId is int b ? agg.BossDamage.GetValueOrDefault(b).Amount : 0,
                    _fightBoss is int f && agg.BossDamage.TryGetValue(f, out var onFightBoss) && onFightBoss.Amount > 0));
            }

            TimeSpan sinceHit = _lastPartyHit is { } lastHit ? now - lastHit : TimeSpan.Zero;
            bool fightBossDead = _fightBoss is int fb && DeadNow(dir, fb);
            bool wipe = !fightBossDead && _fightBossWounded && _fightBoss is int wb && dir.HitPoints.Latest(wb)?.Hp is long whp
                && Aion2BossHp.RealMax(dir, wb) is long wmax && wmax > 0 && whp >= wmax * FullHpShare && sinceHit >= WipeIdle;
            bool idle = !BossHoldsFight(dir, _fightBoss, now) && sinceHit >= FightGap;
            bool over = _fightStart is not null && (fightBossDead || wipe || idle);
            TimeSpan duration = _fightStart is not { } start || _lastPartyHit is not { } last ? TimeSpan.Zero
                : !over && now - last <= IdleAfter ? now - start : last - start;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            var snapshot = new MeterSnapshot(state, message, BuildPlayers(dir, rows, duration), boss, duration, over);
            if (over)
            {
                _fightOver = true;
                _deadBoss = fightBossDead ? _fightBoss : null;
                _deadBossAt = _lastPartyHit ?? now;
                _frozen = snapshot;
            }
            else if (rows.Count > 0)
            {
                _lastLive = snapshot;
            }

            return snapshot;
        }
    }

    // Called under _gate.
    private IReadOnlyList<PlayerDamage> BuildPlayers(Aion2EntityDirectory dir, List<Row> rows, TimeSpan fightDuration)
    {
        bool rosterKnown = dir.RosterKnown;
        double seconds = Math.Max(1.0, fightDuration.TotalSeconds); // herkes için dövüş süresi (ölüyken DPS düşer, oyun içi ölçer gibi)
        var byId = new Dictionary<int, PlayerDamage>();
        var pseudo = new List<Row>();
        IsLocal(dir, 0, refresh: true);

        // Hasar yapan bilinen oyuncular: yerel oyuncu, roster'daki parti; roster hiç gelmediyse aynı hedeflere vuranlar (upstream Corps kapsamı gibi).
        foreach (Row row in rows)
        {
            if (!dir.IsKnownPlayer(row.Id))
            {
                continue; // upstream: bir satır yalnız sınıf becerisi görülmüş bir oyuncu içindir
            }

            bool local = IsLocal(dir, row.Id);
            // Dövüş boss'una hasar veren her bilinen oyuncu listede (dünya boss'u, 20 kişilik etkinlik); 5 kişilik zindanda bunlar zaten partidir.
            if (!local && !dir.IsPartyMemberId(row.Id) && !row.HitFightBoss)
            {
                if (!rosterKnown && row.SharesTarget)
                {
                    pseudo.Add(row);
                }

                continue;
            }

            byId[row.Id] = Player(dir, row, local, seconds);
        }

        foreach (Row row in pseudo.OrderByDescending(r => r.Total).Take(MaxPseudoParty))
        {
            byId[row.Id] = Player(dir, row, false, seconds);
        }

        // Henüz vurmamış parti üyeleri ve yerel oyuncu da görünür (Total = 0).
        var names = new HashSet<string>(dir.PartyNames, StringComparer.Ordinal);
        if (dir.LocalCharacter?.Name is { Length: > 0 } own)
        {
            names.Add(own);
        }

        var shownNames = new HashSet<string>(byId.Values.Select(p => p.Name), StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (shownNames.Contains(name))
            {
                continue;
            }

            int? known = dir.IdOfName(name);
            if (known is int id && byId.ContainsKey(id))
            {
                continue;
            }

            int rowId = known ?? SyntheticId(name);
            bool local = known is int k ? IsLocal(dir, k) : name == dir.LocalCharacter?.Name;
            byId[rowId] = new PlayerDamage(rowId, name, 0, 0, local, (known is int c ? dir.ClassOf(c) : null) ?? dir.PartyClassOf(name), dir.CombatPowerOf(name));
        }

        // Solo, karakter kaydı henüz gelmemişken: çıkarılan yerel oyuncu.
        if (byId.Count == 0 && dir.LocalPlayerId is >= 0 and var localId && dir.NameFor(localId) is string localName)
        {
            byId[localId] = new PlayerDamage(localId, DisplayName(localName, localId), 0, 0, true, dir.ClassOf(localId), dir.CombatPowerOf(localName));
        }

        var list = new List<PlayerDamage>(byId.Values);
        list.Sort(static (a, b) =>
            a.Total != b.Total ? b.Total.CompareTo(a.Total)
            : (a.CombatPower ?? -1) != (b.CombatPower ?? -1) ? (b.CombatPower ?? -1).CompareTo(a.CombatPower ?? -1)
            : string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static PlayerDamage Player(Aion2EntityDirectory dir, Row row, bool local, double seconds)
    {
        string name = DisplayName(dir.NameFor(row.Id), row.Id);
        return new PlayerDamage(row.Id, name, row.Total, row.Total / seconds, local, dir.ClassOf(row.Id) ?? dir.PartyClassOf(name), dir.CombatPowerOf(name), row.BossDamage / seconds);
    }

    /// <summary>
    /// Gösterilen boss: dövüş boss'u varsa ve gitmediyse o (ölüyse Dead = true). Yoksa upstream CombatOverlayData.BossHp (Ui/CombatOverlay.cs):
    /// kendi hedefi boss ise o; yoksa 15 sn içinde vurulan son hedef boss ise o; yoksa en son büyü başlatan boss; hiçbiri yoksa son boss 15 sn
    /// tutulur (canı > 0 ve aynı bölgeyse). Called under _gate.
    /// </summary>
    private BossHp? BuildBoss(Aion2EntityDirectory dir, DateTime now)
    {
        int? boss = _fightBoss is int fightBoss && !BossGone(dir, fightBoss, now) ? fightBoss : SelectBoss(dir, now);
        if (boss is not int id || dir.HitPoints.Latest(id)?.Hp is not long hp)
        {
            return null;
        }

        long max = Aion2BossHp.RealMax(dir, id) ?? 0;
        long current = max > 0 ? Math.Min(hp, max) : hp;
        if (DeadNow(dir, id))
        {
            _lastBoss = null; // upstream: ölü boss tutulmaz
            return new BossHp(id, BossName(dir, id), current, max, true, null, TimeToRage(dir, id, true, now));
        }

        return new BossHp(id, BossName(dir, id), current, max, false, TimeToKill(id, current, now), TimeToRage(dir, id, false, now));
    }

    // Called under _gate. Upstream CombatOverlayData.BossHp seçim kuralları.
    private int? SelectBoss(Aion2EntityDirectory dir, DateTime now)
    {
        int? boss = dir.LocalTarget is { } clicked && !dir.IsKnownPlayer(clicked.EntityId) && IsBoss(dir, clicked.EntityId) ? clicked.EntityId : null;
        if (boss is null && dir.LocalHitTarget is { } hit && DateTime.UtcNow - hit.At < BossHold && !dir.IsKnownPlayer(hit.EntityId) && IsBoss(dir, hit.EntityId))
        {
            boss = hit.EntityId;
        }

        if (boss is null)
        {
            foreach (CastView view in dir.Combat.Casts(now, CastGrace).OrderByDescending(v => v.Notice.Start))
            {
                if (!dir.IsKnownPlayer(view.Notice.Actor) && IsBoss(dir, view.Notice.Actor))
                {
                    boss = view.Notice.Actor;
                    break;
                }
            }
        }

        if (boss is int found)
        {
            _lastBoss = (found, DateTime.UtcNow, dir.ZoneEpoch);
        }
        else if (_lastBoss is { } last && last.Epoch == dir.ZoneEpoch && DateTime.UtcNow - last.At < BossHold && dir.HitPoints.Latest(last.Id)?.Hp > 0)
        {
            boss = last.Id;
        }

        return boss;
    }

    private static bool IsBoss(Aion2EntityDirectory dir, int entity) =>
        dir.BossNpcIdOf(entity) is not null || (dir.HitPoints.MaxOf(entity) ?? 0) >= BossHitPoints;

    /// <summary>Upstream MainWindow.BossDied: son okuma 0 (ya da en yüksek okumanın %0,5'i içinde) veya sunucu ölü olarak kaldırdı (neden 3).</summary>
    private static bool IsDead(Aion2EntityDirectory dir, int id, long hp) =>
        hp <= (dir.HitPoints.HighestSeen(id) ?? 0) * 0.005 || dir.RemovalOf(id) is { Reason: Aion2EntityDirectory.RemovedDead };

    private static bool DeadNow(Aion2EntityDirectory dir, int id) =>
        dir.RemovalOf(id) is { Reason: Aion2EntityDirectory.RemovedDead } || dir.HitPoints.Latest(id) is { } s && IsDead(dir, id, s.Hp);

    private static string BossName(Aion2EntityDirectory dir, int id) =>
        (dir.BossNpcIdOf(id) is int npc ? Aion2BossCatalog.Find(npc)?.Name : null) ?? "Boss";

    private static string DisplayName(string? name, int id) =>
        name is null ? $"Oyuncu #{id}"
        : name.StartsWith("Player #", StringComparison.Ordinal) ? "Oyuncu #" + name["Player #".Length..]
        : name;

    /// <summary>Kimliği henüz bilinmeyen parti üyesi için isimden türetilen kararlı negatif kimlik (FNV-1a).</summary>
    private static int SyntheticId(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619;
        }

        return -(int)(hash & 0x3FFF_FFFF) - 1_000_000;
    }

    public void Dispose()
    {
        Aion2PacketCombatSource? source;
        lock (_gate)
        {
            _disposed = true;
            source = _source;
            _source = null;
        }

        source?.Dispose();
    }
}
