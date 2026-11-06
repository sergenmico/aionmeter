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
/// Dizin olaylarını (LocalIdChanged, IdentityMoved, Died, BossSpawned) kendi kilidi dışında verir.</para>
/// <para>Deneme: bir dövüşün hasar tablosu. Wipe (boss herkesi öldürüp sıfırlandı) önceki denemeyi kapatır, yeni deneme sıfırdan başlar.</para>
/// </summary>
public sealed class Aion2MeterEngine : IMeterEngine
{
    /// <summary>Boss tutmuyorsa bu kadar parti hasarı gelmezse dövüş biter; sonraki parti vuruşu yeni dövüş başlatır.</summary>
    private static readonly TimeSpan FightGap = TimeSpan.FromSeconds(20);

    /// <summary>Aktif süre: ardışık iki parti vuruşu arası bundan uzunsa o aralık süreye sayılmaz (DPS boşlukla düşmez).</summary>
    private static readonly TimeSpan ActiveGap = TimeSpan.FromSeconds(3);

    /// <summary>Wipe: dövüş boss'u tam cana döndü ve bu kadar parti hasarı yok.</summary>
    private static readonly TimeSpan WipeIdle = TimeSpan.FromSeconds(10);

    /// <summary>Wipe şüphesi: tutan boss dövüşünde bu kadar parti vuruşu yokken (takım ölü) vuruş yeniden başladı, taze can okuması yok.</summary>
    private static readonly TimeSpan WipeSuspectGap = TimeSpan.FromSeconds(15);

    /// <summary>Wipe: dövüş boss'unun yeni okuması öncekinden en az bu kadar (azami canın payı) yüksek ve en az <see cref="WipeHighShare"/>'de.
    /// İkinci koşul boss'un kendini iyileştirmesini (bir okuma %10 kadar çıkabiliyor, upstream 2026-10-03 kaydı) wipe saymamak için.</summary>
    private const double WipeRiseShare = 0.10;
    private const double WipeHighShare = 0.85;

    /// <summary>
    /// Boss'un son can okuması bundan eskiyse dövüşü tutmaz. Yalnız katalog boss'u (ya da zindanda boss-benzeri) dövüşü tutar: takım
    /// ölü/dirilirken kimse vurmaz, can okuması gelmez ve dövüş bölünmemeli (kullanıcı kuralı). Kukla/açık dünya canavarı tutmaz (<see cref="FightGap"/>).
    /// </summary>
    private static readonly TimeSpan BossStale = TimeSpan.FromMinutes(5);

    /// <summary>Ölen dövüş boss'una bu süre içinde gelen geç tikler yeni dövüş başlatmaz.</summary>
    private static readonly TimeSpan DeadBossGuard = TimeSpan.FromSeconds(30);

    /// <summary>Katalog dışı dövüş boss'u öldüğünde bu süre içinde vurulmuş başka canlı aday varsa dövüş ona geçer (bitmez).</summary>
    private static readonly TimeSpan CandidateRecent = TimeSpan.FromSeconds(10);

    /// <summary>Aynı sınıftaki aday, son 30 sn'de dövüş boss'una vurulanın bu katı kadar parti hasarı alırsa dövüş boss'u o olur.</summary>
    private const double SwitchFactor = 2.0;

    /// <summary>Savaşta: yerel oyuncu ya da partisi son bu kadar sürede bir canavara vurdu.</summary>
    private static readonly TimeSpan CombatWindow = TimeSpan.FromSeconds(8);

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

    /// <summary>Kalan süre tahmini: son 30 sn, saniyelik kovalar. Hedef başına hasar (canı okunmadan önceki vuruşlar dahil) en çok bu kadar
    /// hedef için tutulur: kaynak başına ve partinin halkaları için.</summary>
    private const int TtkSeconds = 30;
    private const int MaxTargets = 8;

    /// <summary>DPS grafiği: oyuncu başına en çok bu kadar kova; aşılınca ikişer birleştirilir, kova süresi iki katına çıkar.</summary>
    private const int MaxBuckets = 900;

    /// <summary>Ölüm sayacında en çok bu kadar oyuncu (parti/yerel her zaman).</summary>
    private const int MaxDeathRows = 64;

    // Upstream CombatOverlay (Ui/CombatOverlay.cs) sabitleri.
    private static readonly TimeSpan CastGrace = TimeSpan.FromMilliseconds(700);
    private const long BossHitPoints = 500_000;
    private static readonly TimeSpan BossHold = TimeSpan.FromSeconds(15);

    private readonly string _assetsDir;
    private readonly object _gate = new(); // her şey: hasar toplamları, dövüş durumu, boss seçimi
    private readonly HashSet<int> _localTargets = new();
    private Attempt _cur = new();
    private int _fightEpoch;

    // Wipe şüphesi: uzun aradan sonra taze can okuması olmadan vuruş başladı. Aradan önceki deneme bekletilir, vuruşlar yeni denemeye
    // yazılır; taze okuma yüksekse wipe (bekleyen atılır), değilse ikisi birleşir.
    private Attempt? _held;

    // Dövüş boss'u: partinin hasar verdiği boss adaylarından seçilen; ölene, ayrılana, sıfırlanana ya da parti açıkça başkasına vurana kadar değişmez.
    private int? _fightBoss;
    private bool _fightBossWounded;
    private long _bossCheckedAt = -1_000_000;

    // Dövüş bitti: anlık görüntü yeni dövüşün ilk parti vuruşuna kadar donar.
    private bool _fightOver;
    private int? _deadBoss;
    private DateTime _deadBossAt;
    private MeterSnapshot? _frozen;
    private MeterSnapshot? _lastLive; // hasar içeren son canlı görüntü (bölge değişiminde donmuş sonuç olarak kalır)

    // Upstream boss tutma (_lastBoss) durumu.
    private (int Id, DateTime At, int Epoch)? _lastBoss;

    // Yerel oyuncu önbelleği (InferLocalPlayer O(n) olduğu için vuruş başına değil, saniyede bir).
    private int? _localId;
    private string _localSource = "yok";
    private long _localCheckedAt = -1_000_000;
    private string? _configuredName;

    // Savaşta: yerel oyuncunun ya da partinin bir canavara son vuruşu.
    private DateTime? _lastEngaged;

    private volatile Aion2PacketCombatSource? _source;
    private volatile EngineState _state = EngineState.Starting;
    private volatile string? _message;
    private int _started;
    private bool _disposed;

    /// <summary>Bir denemenin hasar tablosu ve süresi.</summary>
    private sealed class Attempt
    {
        public readonly Dictionary<int, Aggregate> BySource = new();
        public readonly Dictionary<int, DamageRing> Rings = new(); // hedef -> partinin ona hasarı (TTK, öfke, dövüş boss'u seçimi)
        public readonly Dictionary<int, int> Deaths = new();       // oyuncu -> bu denemede ölüm sayısı
        public DateTime? Start;
        public DateTime? LastPartyHit;
        public TimeSpan Active;
        public HpSample? BossReading; // dövüş boss'unun motorun gördüğü son okuması (wipe: sıçrama)

        // Detay kümesi: yabancılardan detay tutanların sayısı ve ilk 30'a girme eşiği (saniyede bir yeniden hesaplanır).
        public int DetailedStrangers;
        public long DetailThreshold = long.MaxValue;

        /// <summary>Wipe şüphesi boşa çıktı: <paramref name="later"/> (aradan sonraki vuruşlar) buna eklenir.</summary>
        public void Absorb(Attempt later, int? fightBoss)
        {
            long offset = Start is { } mine && later.Start is { } theirs ? (long)Math.Max(0, (theirs - mine).TotalSeconds) : 0;
            foreach ((int id, Aggregate agg) in later.BySource)
            {
                if (!BySource.TryGetValue(id, out Aggregate? into))
                {
                    BySource[id] = into = new Aggregate { First = agg.First, Last = agg.Last };
                    if (!agg.Party && agg.Detail is not null)
                    {
                        DetailedStrangers++;
                    }
                }

                into.Absorb(agg, fightBoss, offset);
            }

            foreach ((int id, DamageRing ring) in later.Rings)
            {
                if (Rings.TryGetValue(id, out DamageRing? known))
                {
                    known.Absorb(ring);
                }
                else
                {
                    Rings[id] = ring;
                }
            }

            foreach ((int id, int n) in later.Deaths)
            {
                Deaths[id] = Deaths.GetValueOrDefault(id) + n;
            }

            Active += later.Active;
            if (later.LastPartyHit is { } last && (LastPartyHit is not { } before || last > before))
            {
                LastPartyHit = last;
            }

            if (later.BossReading is { } r && (BossReading is not { } b || r.At > b.At))
            {
                BossReading = r;
            }
        }
    }

    private sealed class Aggregate
    {
        public long Total;
        public DateTime First;
        public DateTime Last;
        public bool SharesTarget; // yerel oyuncunun vurduğu bir hedefe vurdu (roster yokken parti tahmini)
        public bool Party;        // yerel oyuncu ya da parti üyesi olarak vurdu (atılmaz, detayı hep tutulur)
        public readonly Dictionary<int, (long Amount, DateTime Last)> Targets = new(); // hedef -> bu oyuncunun ona hasarı (boss sayılmadan öncesi dahil)
        public AggDetail? Detail;  // yalnız ilk 30 yabancı + yerel + parti

        public void AddTarget(int target, long amount, DateTime at, int? fightBoss)
        {
            if (!Targets.TryGetValue(target, out var known) && Targets.Count >= MaxTargets)
            {
                // en uzun süredir vurulmayan atılır; dövüş boss'u hiç atılmaz
                int? oldest = null;
                DateTime oldestAt = DateTime.MaxValue;
                foreach ((int id, (long _, DateTime last)) in Targets)
                {
                    if (id != fightBoss && last < oldestAt)
                    {
                        oldest = id;
                        oldestAt = last;
                    }
                }

                if (oldest is int drop)
                {
                    Targets.Remove(drop);
                }
            }

            Targets[target] = (known.Amount + amount, at > known.Last ? at : known.Last);
        }

        /// <summary>Kimlik birleşmesi ya da deneme birleşmesi: <paramref name="other"/>'ın her şeyi buna eklenir (kovaları <paramref name="bucketOffset"/> sn kaydırılır).</summary>
        public void Absorb(Aggregate other, int? fightBoss, long bucketOffset = 0)
        {
            Total += other.Total;
            First = other.First < First ? other.First : First;
            Last = other.Last > Last ? other.Last : Last;
            SharesTarget |= other.SharesTarget;
            Party |= other.Party;
            foreach ((int target, (long amount, DateTime last)) in other.Targets)
            {
                AddTarget(target, amount, last, fightBoss);
            }

            if (other.Detail is { } theirs)
            {
                (Detail ??= new AggDetail()).Absorb(theirs, bucketOffset);
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

        public void Absorb(AggDetail other, long bucketOffset = 0)
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
                        AddToBucket(bucketOffset + (long)i * other.BucketSeconds, theirs[i]);
                    }
                }
            }
        }
    }

    private sealed class SkillAgg
    {
        public long Total, Hits, Crits, MaxHit;
    }

    /// <summary>Bir hedefe partinin hasarı: deneme toplamı ve son <see cref="TtkSeconds"/> saniyenin saniyelik kovaları (halka).</summary>
    private sealed class DamageRing
    {
        public readonly long[] Amount = new long[TtkSeconds];
        public readonly long[] Second = new long[TtkSeconds];
        public long Total;
        public DateTime FirstAt = DateTime.MaxValue; // partinin bu hedefe ilk / son vuruşu
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

        public void Absorb(DamageRing other)
        {
            Total += other.Total;
            FirstAt = other.FirstAt < FirstAt ? other.FirstAt : FirstAt;
            LastAt = other.LastAt > LastAt ? other.LastAt : LastAt;
            for (int i = 0; i < TtkSeconds; i++)
            {
                if (other.Amount[i] <= 0)
                {
                    continue;
                }

                if (Second[i] == other.Second[i])
                {
                    Amount[i] += other.Amount[i];
                }
                else if (other.Second[i] > Second[i])
                {
                    Second[i] = other.Second[i];
                    Amount[i] = other.Amount[i];
                }
            }
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
        source.Entities.HitPoints.Died += OnDied;
        source.Entities.BossSpawned += OnBossSpawned;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            source.Entities.SetConfiguredLocalName(_configuredName);
            _source = source;
            return true;
        }
    }

    /// <summary>Denetim aracı için: çözülmüş bir olayı yakalama yolu gibi verir.</summary>
    internal void InjectHit(DamageEvent ev) => OnHit(ev);

    /// <summary>Denetim aracı için: bir bölge değişimini yakalama yolu gibi verir.</summary>
    internal void InjectZone(ZoneChange zone) => OnZoneChanged(zone);

    /// <summary>Denetim aracı için: <see cref="Snapshot"/>, saat yerine verilen anla (sentetik zamanlı senaryolar).</summary>
    internal MeterSnapshot SnapshotAt(DateTime now) => SnapshotCore(now);

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

    /// <summary>Kayıtlı yerel karakter adı (ayardan): yerel kimlik kesin bilinmezken bu ada sahip oyuncu yerel sayılır (upstream SetConfiguredLocalName).</summary>
    public void YerelAdAyarla(string? ad)
    {
        lock (_gate)
        {
            _configuredName = string.IsNullOrWhiteSpace(ad) ? null : ad.Trim();
            _source?.Entities.SetConfiguredLocalName(_configuredName);
            _localCheckedAt = -1_000_000;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (MeterTani.Acik && _cur.Start is not null)
            {
                Tani("dövüş bitti: kullanıcı sıfırladı");
            }

            ClearFight();
        }
    }

    // Called under _gate.
    private void ClearFight()
    {
        _cur = new Attempt();
        _held = null;
        _localTargets.Clear();
        _fightOver = false;
        _deadBoss = null;
        _frozen = null;
        _lastLive = null;
        SetFightBoss(null, null);
        _fightEpoch++;
    }

    // Called under _gate.
    private void SetFightBoss(int? id, string? why)
    {
        if (_fightBoss != id)
        {
            if (MeterTani.Acik && why is not null)
            {
                Tani($"dövüş boss'u: {Describe(_fightBoss)} -> {Describe(id)} ({why})");
            }

            _fightBoss = id;
            _fightBossWounded = false;
            _cur.BossReading = null;
            if (_held is { } held)
            {
                held.BossReading = null;
            }

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
            if (MeterTani.Acik && _cur.Start is not null && !_fightOver)
            {
                Tani($"dövüş bitti: bölge değişti ({zone.ZoneId})");
            }

            ClearFight();
            if (keep is not null)
            {
                _frozen = keep with { FightOver = true };
                _fightOver = true;
            }
        }
    }

    // Called under _gate. Yerel oyuncu: açık kimlik (oturum çerçevesi / karakter kaydı) varken yalnız o; yoksa kayıtlı adın kimliği; o da
    // yoksa akıştan tahmin (InferLocalPlayer). Saniyede bir yenilenir.
    private bool IsLocal(Aion2EntityDirectory dir, int id, bool refresh = false)
    {
        long nowMs = Environment.TickCount64;
        if (refresh || nowMs - _localCheckedAt >= 1000 || _localId is null)
        {
            _localCheckedAt = nowMs;
            int explicitId = dir.ExplicitLocalPlayerId;
            int? byName = explicitId >= 0 ? null : dir.ConfiguredLocalPlayerId;
            int? local = explicitId >= 0 ? explicitId : byName ?? dir.InferLocalPlayer();
            string from = explicitId >= 0 ? "açık" : byName is not null ? "ad" : local is not null ? "tahmin" : "yok";
            if (local != _localId || from != _localSource)
            {
                if (MeterTani.Acik)
                {
                    Tani($"yerel kimlik: {_localId?.ToString() ?? "-"} -> {local?.ToString() ?? "-"} ({from})");
                }

                if (_localId is not null)
                {
                    _localTargets.Clear(); // önceki "yerel"in hedefleriydi: sözde parti onlara göre kurulmasın
                    foreach (Aggregate agg in _cur.BySource.Values)
                    {
                        agg.SharesTarget = false;
                    }
                }

                _localId = local;
                _localSource = from;
            }
        }

        return id == _localId;
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
            bool onPlayer = dir.IsKnownPlayer(target);
            if (party && !onPlayer && (_lastEngaged is not { } engaged || ev.Timestamp > engaged))
            {
                _lastEngaged = ev.Timestamp;
            }

            if (local)
            {
                if (_localTargets.Count >= MaxLocalTargets)
                {
                    _localTargets.Clear();
                }

                _localTargets.Add(target);
            }

            bool sharesTarget = _localTargets.Contains(target);
            // Roster yokken yerel oyuncunun hedefine vuran bilinen oyuncular sözde partidir: dövüşe hasarları yazılır ama dövüş başlatmaz,
            // donmuş sonucu silmez, dövüşü canlı tutmaz (yan yana kuklada başkası benim kuklama vurunca sonucum gitmesin).
            bool pseudo = !party && sharesTarget && !dir.RosterKnown && dir.IsKnownPlayer(id);
            if (_fightOver)
            {
                // Donmuş sonuç: parti dışı vuruşlar ve ölen boss'a geç gelen tikler yeni dövüş başlatmaz.
                bool lateOnDeadBoss = target == _deadBoss && ev.Timestamp - _deadBossAt < DeadBossGuard && DeadNow(dir, target);
                if (!party || lateOnDeadBoss)
                {
                    return;
                }
            }

            if (party)
            {
                PartyHit(dir, ev.Timestamp, target, local);
            }

            Attempt a = _cur;
            if (a.Start is not { } fightStart)
            {
                return; // dövüş partinin ilk vuruşuyla başlar
            }

            if (!a.BySource.TryGetValue(id, out Aggregate? agg))
            {
                if (a.BySource.Count >= MaxSources && !party && !EvictWeakestStranger(a, ev.Amount))
                {
                    return; // kalabalık: bu vuruş en zayıf yabancıdan da küçük
                }

                a.BySource[id] = agg = new Aggregate { First = ev.Timestamp, Last = ev.Timestamp };
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
            RefreshDetailSet(a);
            if (agg.Detail is null && (agg.Party || a.DetailedStrangers < MaxDetailedStrangers || agg.Total >= a.DetailThreshold))
            {
                agg.Detail = new AggDetail(); // ilk 30'a sonradan giren o andan itibaren detay toplar
                if (!agg.Party)
                {
                    a.DetailedStrangers++;
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

            if (onPlayer)
            {
                return; // oyuncuya vuruş (PvP): boss hasarına ve halkalara girmez
            }

            // Her hedef için (canı henüz okunmamış olsa da): hedef sonradan boss sayılınca önceki vuruşlar da BossTotal'a girer.
            agg.AddTarget(target, ev.Amount, ev.Timestamp, _fightBoss);
            if (!party && !pseudo)
            {
                return;
            }

            if (!a.Rings.TryGetValue(target, out DamageRing? ring))
            {
                if (a.Rings.Count >= MaxTargets)
                {
                    int? drop = null;
                    DateTime dropAt = DateTime.MaxValue;
                    foreach ((int candidate, DamageRing r) in a.Rings)
                    {
                        if (candidate != _fightBoss && r.LastAt < dropAt)
                        {
                            drop = candidate;
                            dropAt = r.LastAt;
                        }
                    }

                    if (drop is int d)
                    {
                        a.Rings.Remove(d);
                    }
                }

                a.Rings[target] = ring = new DamageRing();
            }

            ring.Add(ev.Timestamp, ev.Amount);
            long nowMs = Environment.TickCount64;
            if (_fightBoss is null || nowMs - _bossCheckedAt >= 250)
            {
                UpdateFightBoss(dir, ev.Timestamp);
            }
        }
    }

    // Called under _gate. Yerel oyuncu ya da roster'daki parti üyesi vurdu: yeni dövüş / wipe / şüphe kararları ve aktif süre.
    private void PartyHit(Aion2EntityDirectory dir, DateTime at, int target, bool local)
    {
        string? why = null;
        if (!_fightOver && _cur.Start is not null)
        {
            if (_fightBoss is int fb && target != fb && dir.BossNpcIdOf(fb) is int npc && dir.BossNpcIdOf(target) == npc)
            {
                why = $"wipe: aynı boss yeni kimlikle vuruldu ({fb} -> {target})";
            }
            else if (CheckWipeReading(dir) is { } rule)
            {
                why = rule;
            }
        }

        Attempt a = _cur;
        bool gap = a.LastPartyHit is { } last && at - last >= FightGap && !BossHoldsFight(dir, _fightBoss, at);
        if (why is not null || _fightOver || a.LastPartyHit is null || gap)
        {
            if (MeterTani.Acik)
            {
                if (why is not null || (gap && !_fightOver))
                {
                    Tani($"dövüş bitti: {why ?? $"{FightGap.TotalSeconds:0} sn parti vuruşu yok"}");
                }

                Tani($"dövüş başladı: {why ?? (_fightOver ? "donmuş sonuçtan sonra ilk parti vuruşu" : gap ? "uzun aradan sonra" : "ilk parti vuruşu")} (hedef {target})");
            }

            ClearFight();
            a = _cur;
            a.Start = at;
            if (local)
            {
                _localTargets.Add(target);
            }
        }
        else if (_held is null && a.LastPartyHit is { } before && at - before >= WipeSuspectGap && _fightBoss is int boss
            && BossHoldsFight(dir, boss, at) && !(dir.HitPoints.Latest(boss) is { } s && s.At > before + TimeSpan.FromSeconds(1)))
        {
            // (iii) Uzun aradan sonra vuruş, taze can okuması yok: boss sıfırlanmış olabilir (sunucu canı yalnız hedef alana gönderir).
            _held = a;
            _cur = a = new Attempt { Start = at, BossReading = _held.BossReading };
            if (MeterTani.Acik)
            {
                Tani($"wipe şüphesi: {(at - before).TotalSeconds:0} sn aradan sonra vuruş, boss {boss} için taze can okuması yok (can bilinmiyor)");
            }
        }

        if (a.LastPartyHit is { } prev)
        {
            if (at > prev)
            {
                TimeSpan step = at - prev;
                if (step <= ActiveGap)
                {
                    a.Active += step;
                }

                a.LastPartyHit = at;
            }
        }
        else
        {
            a.LastPartyHit = at;
        }
    }

    /// <summary>
    /// Wipe kural (i): dövüş boss'unun (katalogda ya da zindanda) yeni okuması öncekinden belirgin yüksek. Şüphe varken taze okuma şüpheyi
    /// çözer (yüksekse bekleyen deneme atılır, değilse birleşir) ve null döner. Wipe ise sebep metni, değilse null. Called under _gate.
    /// </summary>
    private string? CheckWipeReading(Aion2EntityDirectory dir)
    {
        if (_fightBoss is not int fb || dir.HitPoints.Latest(fb) is not { } sample)
        {
            return null;
        }

        Attempt reference = _held ?? _cur;
        if (reference.BossReading is not { } prev)
        {
            reference.BossReading = _cur.BossReading = sample;
            return null;
        }

        if (sample.At <= prev.At)
        {
            return null;
        }

        long max = Aion2BossHp.RealMax(dir, fb) ?? 0;
        // Şüphede aradan sonraki vuruşlar okumadan önce boss'u zaten indirmiş olabilir: o hasar okumaya geri eklenir (wipe yoksa boss
        // "önceki - bu hasar" civarında olmalı, wipe varsa "tam can - bu hasar").
        long since = _held is not null && _cur.Rings.TryGetValue(fb, out DamageRing? side) ? side.Total : 0;
        bool high = HoldsKind(dir, fb) && max > 0 && sample.Hp + since - prev.Hp >= max * WipeRiseShare && sample.Hp + since >= max * WipeHighShare;
        if (_held is { } held)
        {
            if (high)
            {
                // Wipe doğrulandı: aradan önceki deneme kapanır; aradan sonraki vuruşlar zaten yeni denemede.
                if (MeterTani.Acik)
                {
                    Tani($"wipe doğrulandı (şüphe + taze okuma): boss {fb} {prev.Hp:N0} -> {sample.Hp:N0} / {max:N0}; önceki deneme kapandı");
                }

                _held = null;
            }
            else
            {
                if (MeterTani.Acik)
                {
                    Tani($"wipe değil: boss {fb} taze okuma {sample.Hp:N0} (önce {prev.Hp:N0}); denemeler birleşti");
                }

                held.Absorb(_cur, _fightBoss);
                _cur = held;
                _held = null;
            }

            _cur.BossReading = sample;
            return null;
        }

        _cur.BossReading = sample;
        return high ? $"wipe: boss {fb} canı {prev.Hp:N0} -> {sample.Hp:N0} / {max:N0} (sıfırlandı)" : null;
    }

    // Called under _gate. Dolu tabloda yeni bir yabancı için en az hasarlı yabancı atılır; yalnız yeni vuruş ondan büyükse (sürekli giriş-çıkış olmasın).
    private static bool EvictWeakestStranger(Attempt a, long incoming)
    {
        int? weakest = null;
        long weakestTotal = long.MaxValue;
        foreach ((int id, Aggregate agg) in a.BySource)
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

        if (a.BySource[drop].Detail is not null)
        {
            a.DetailedStrangers--;
        }

        a.BySource.Remove(drop);
        return true;
    }

    private long _detailCheckedAt = -1_000_000;
    private readonly List<long> _strangerTotals = new();

    // Called under _gate. Saniyede bir: yabancılardan hasarda ilk 30'un eşiği bulunur, ilk 30 dışındakilerin detayı atılır.
    private void RefreshDetailSet(Attempt a)
    {
        long nowMs = Environment.TickCount64;
        if (nowMs - _detailCheckedAt < 1000)
        {
            return;
        }

        _detailCheckedAt = nowMs;
        _strangerTotals.Clear();
        foreach (Aggregate agg in a.BySource.Values)
        {
            if (!agg.Party)
            {
                _strangerTotals.Add(agg.Total);
            }
        }

        if (_strangerTotals.Count <= MaxDetailedStrangers)
        {
            a.DetailThreshold = long.MaxValue; // herkes zaten sığıyor (sayaç sınırı yeter)
            return;
        }

        _strangerTotals.Sort();
        a.DetailThreshold = _strangerTotals[^MaxDetailedStrangers];
        foreach (Aggregate agg in a.BySource.Values)
        {
            if (!agg.Party && agg.Detail is not null && agg.Total < a.DetailThreshold)
            {
                agg.Detail = null;
                a.DetailedStrangers--;
            }
        }
    }

    /// <summary>
    /// Dövüş boss'u seçimi. Aday: partinin (ya da sözde partinin) vurduğu, boss-benzeri (katalogda ya da canı 500.000+), gitmemiş ve ölmemiş hedef.
    /// Öncelik: katalog boss'u, sonra yerel oyuncunun hedefi, sonra son 30 sn'deki parti hasarı. Seçilen boss kalır; değişir: öldüyse (katalog dışıysa
    /// yakında vurulan başka canlı aday varsa ona), gittiyse, katalogdaki bir aday katalog dışı olanın yerine geçerse ya da aynı sınıftaki bir
    /// adaya parti açıkça daha çok vuruyorsa (son 30 sn'de <see cref="SwitchFactor"/> katı; yerel hedefse eşitlik yeter). Called under _gate.
    /// </summary>
    private void UpdateFightBoss(Aion2EntityDirectory dir, DateTime at)
    {
        _bossCheckedAt = Environment.TickCount64;
        long second = at.Ticks / TimeSpan.TicksPerSecond;
        int? marked = dir.LocalTarget?.EntityId;
        int? best = null;
        int bestRank = -1;
        long bestWindow = 0, bestTotal = 0;
        int? current = _fightBoss;
        foreach ((int id, DamageRing ring) in _cur.Rings)
        {
            if (id == current || !IsBoss(dir, id) || BossGone(dir, id, at) || FightBossDead(dir, id))
            {
                continue;
            }

            int rank = (IsCatalog(dir, id) ? 2 : 0) + (id == marked ? 1 : 0);
            long window = ring.Window(second).Sum;
            if (rank > bestRank || (rank == bestRank && (window > bestWindow || (window == bestWindow && ring.Total > bestTotal))))
            {
                best = id;
                bestRank = rank;
                bestWindow = window;
                bestTotal = ring.Total;
            }
        }

        if (current is not int cur)
        {
            SetFightBoss(best, "ilk aday");
            if (_fightBoss is int chosen)
            {
                NoteWound(dir, chosen);
            }

            return;
        }

        bool curCatalog = IsCatalog(dir, cur);
        bool curDead = FightBossDead(dir, cur);
        if (BossGone(dir, cur, at) && !curDead)
        {
            SetFightBoss(best, "önceki boss ayrıldı");
        }
        else if (curDead)
        {
            // Katalog boss'u öldü: dövüş biter (Snapshot). Katalog dışı (mob, kukla, add) öldü: yakında vurulan başka canlı aday varsa ona geçilir.
            if (!curCatalog && best is int other && _cur.Rings.TryGetValue(other, out DamageRing? o) && at - o.LastAt <= CandidateRecent)
            {
                SetFightBoss(other, "katalog dışı aday öldü, parti başka canlı adaya vuruyor");
            }
        }
        else if (best is int other)
        {
            bool otherCatalog = IsCatalog(dir, other);
            long curWindow = _cur.Rings.TryGetValue(cur, out DamageRing? cr) ? cr.Window(second).Sum : 0;
            if (otherCatalog && !curCatalog)
            {
                SetFightBoss(other, "katalog boss'u öncelikli");
            }
            else if (otherCatalog == curCatalog && bestWindow > 0
                && (bestWindow >= SwitchFactor * curWindow || (other == marked && bestWindow >= curWindow)))
            {
                SetFightBoss(other, other == marked && bestWindow < SwitchFactor * curWindow
                    ? "yerel hedef, parti en az o kadar vuruyor" : $"parti açıkça daha çok vuruyor ({bestWindow:N0} / {curWindow:N0})");
            }
        }

        if (_fightBoss is int chosenBoss)
        {
            NoteWound(dir, chosenBoss);
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

    private static bool IsCatalog(Aion2EntityDirectory dir, int id) => dir.BossNpcIdOf(id) is not null;

    /// <summary>Dövüşü tutabilen boss türü: katalogda, ya da zindanda boss-benzeri (kataloğa henüz girmemiş zindan boss'u).</summary>
    private bool HoldsKind(Aion2EntityDirectory dir, int id) =>
        IsCatalog(dir, id) || (_source is { } s && Aion2Zones.IsInstance(s.CurrentZoneId) && IsBoss(dir, id));

    /// <summary>Dövüş boss'u (tutan türden) canlı, yerinde ve yaralıysa (tam cana dönmediyse) dövüş boşta kalmakla bitmez.</summary>
    private bool BossHoldsFight(Aion2EntityDirectory dir, int? boss, DateTime now)
    {
        if (boss is not int id || !HoldsKind(dir, id) || dir.RemovalOf(id) is not null || dir.HitPoints.Latest(id) is not { } sample
            || now - sample.At > BossStale || IsDead(dir, id, sample.Hp))
        {
            return false;
        }

        long max = Aion2BossHp.RealMax(dir, id) ?? 0;
        return max > 0 && sample.Hp < max * FullHpShare;
    }

    /// <summary>Dövüş boss'u öldü mü: katalog boss'u can okumasıyla (0) ya da kaldırma kaydıyla; katalog dışı hedef (kukla 0'a inip dolabiliyor)
    /// yalnız sunucunun "öldü" kaldırma kaydıyla.</summary>
    private static bool FightBossDead(Aion2EntityDirectory dir, int id) =>
        IsCatalog(dir, id) ? DeadNow(dir, id) : dir.RemovalOf(id) is { Reason: Aion2EntityDirectory.RemovedDead };

    /// <summary>Kalan can ÷ son 30 sn'deki parti DPS'i (bossa); 3 sn'den az veri ya da hasar yoksa null. Called under _gate.</summary>
    private TimeSpan? TimeToKill(int boss, long hp, DateTime now)
    {
        if (!_cur.Rings.TryGetValue(boss, out DamageRing? ring))
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

    /// <summary>Öfkeye kalan: boss_rage.json süresi − partinin bu denemede bossa ilk vuruşundan beri geçen (ölüyse son vuruşta donar); ilk vuruş
    /// bilinmiyorsa null. Called under _gate.</summary>
    private TimeSpan? TimeToRage(Aion2EntityDirectory dir, int boss, bool dead, DateTime now)
    {
        if (!_cur.Rings.TryGetValue(boss, out DamageRing? ring) || ring.FirstAt == DateTime.MaxValue)
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
            if (!_cur.BySource.TryGetValue(id, out Aggregate? agg))
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
        Reattribute(_cur, from, to);
        if (_held is { } held)
        {
            Reattribute(held, from, to);
        }
    }

    private void Reattribute(Attempt a, int from, int to)
    {
        if (from != to && a.Deaths.Remove(from, out int deaths))
        {
            a.Deaths[to] = a.Deaths.GetValueOrDefault(to) + deaths;
        }

        if (from == to || !a.BySource.Remove(from, out Aggregate? moved))
        {
            return;
        }

        if (!a.BySource.TryGetValue(to, out Aggregate? into))
        {
            a.BySource[to] = moved;
            return;
        }

        if (moved.Detail is not null && into.Detail is not null && !moved.Party)
        {
            a.DetailedStrangers--; // iki detay tek satırda birleşti
        }

        into.Absorb(moved, _fightBoss);
    }

    /// <summary>Oyuncu öldü (canı 0'ın üstünden 0'a indi; upstream Combat/Deaths.cs kuralı). Bu denemedeki ölüm sayısına eklenir.</summary>
    private void OnDied(int id, DateTime at)
    {
        if (_source is not { } source)
        {
            return;
        }

        Aion2EntityDirectory dir = source.Entities;
        lock (_gate)
        {
            if (_fightOver || _cur.Start is null)
            {
                return;
            }

            bool own = IsLocal(dir, id) || dir.IsPartyMemberId(id);
            bool shown = !own && dir.IsKnownPlayer(id) && _cur.BySource.ContainsKey(id) && _cur.Deaths.Count < MaxDeathRows;
            if (!own && !shown)
            {
                return;
            }

            int n = _cur.Deaths.GetValueOrDefault(id) + 1;
            _cur.Deaths[id] = n;
            if (MeterTani.Acik)
            {
                Tani($"ölüm: {DisplayName(dir.NameFor(id), id)} [{id}] {n}. kez ({at:HH:mm:ss.fff})");
            }
        }
    }

    /// <summary>Wipe kural (ii): dövüş boss'unun NPC'si yeni bir varlık kimliğiyle doğdu. Deneme kapanır (donmuş sonuç); sonraki parti vuruşu yenisini başlatır.</summary>
    private void OnBossSpawned(int entityId, int npcId)
    {
        if (_source is not { } source)
        {
            return;
        }

        Aion2EntityDirectory dir = source.Entities;
        lock (_gate)
        {
            if (_fightOver || _cur.Start is null || _fightBoss is not int fb || fb == entityId || dir.BossNpcIdOf(fb) != npcId)
            {
                return;
            }

            if (MeterTani.Acik)
            {
                Tani($"dövüş bitti: wipe: aynı boss yeni kimlikle doğdu ({fb} -> {entityId})");
            }

            Freeze(dir, DateTime.Now, Compose(dir, DateTime.Now, over: true));
        }
    }

    private readonly record struct Row(int Id, long Total, bool SharesTarget, long BossDamage, bool HitFightBoss);

    public MeterSnapshot Snapshot() => SnapshotCore(DateTime.Now); // olay zamanları yerel saat (NpcapCaptureService: Timeval.ToLocalTime)

    private MeterSnapshot SnapshotCore(DateTime now)
    {
        EngineState state = _state;
        string? message = _message;
        if (_source is not { } source)
        {
            return new MeterSnapshot(state, message, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);
        }

        Aion2EntityDirectory dir = source.Entities;
        lock (_gate)
        {
            // Sahibi sonradan bulunan çağrıların hasarı sahibine geçer (upstream MainWindow DrainResolvedOwners).
            foreach ((int summon, int owner) in dir.DrainResolvedOwners())
            {
                Reattribute(summon, owner);
            }

            bool fighting = _lastEngaged is { } engaged && now - engaged <= CombatWindow;
            string? ownName = dir.LearnedLocalName;
            if (_frozen is { } frozen)
            {
                return frozen with { State = state, Message = message, Savasta = fighting, YerelAd = ownName };
            }

            if (_fightBoss is not null)
            {
                UpdateFightBoss(dir, now);
            }

            string? why = null;
            if (_cur.Start is not null)
            {
                TimeSpan sinceHit = _cur.LastPartyHit is { } lastHit ? now - lastHit : TimeSpan.Zero;
                bool fightBossDead = _fightBoss is int fb && FightBossDead(dir, fb);
                bool fullWipe = !fightBossDead && _fightBossWounded && _fightBoss is int wb && HoldsKind(dir, wb) && dir.HitPoints.Latest(wb)?.Hp is long whp
                    && Aion2BossHp.RealMax(dir, wb) is long wmax && wmax > 0 && whp >= wmax * FullHpShare && sinceHit >= WipeIdle;
                why = fightBossDead ? $"boss öldü ({_fightBoss})"
                    : CheckWipeReading(dir) is { } rule ? rule
                    : fullWipe ? $"wipe: boss tam cana döndü, {WipeIdle.TotalSeconds:0} sn parti vuruşu yok"
                    : !BossHoldsFight(dir, _fightBoss, now) && sinceHit >= FightGap ? $"{FightGap.TotalSeconds:0} sn parti vuruşu yok"
                    : null;
            }

            MeterSnapshot snapshot = Compose(dir, now, over: why is not null) with { State = state, Message = message, Savasta = fighting, YerelAd = ownName };
            if (why is not null)
            {
                if (MeterTani.Acik)
                {
                    Tani($"dövüş bitti: {why}");
                }

                Freeze(dir, now, snapshot);
            }
            else if (snapshot.Players.Count > 0)
            {
                _lastLive = snapshot;
            }

            return snapshot;
        }
    }

    // Called under _gate. Deneme kapanır: görüntü yeni dövüşün ilk parti vuruşuna kadar donar.
    private void Freeze(Aion2EntityDirectory dir, DateTime now, MeterSnapshot snapshot)
    {
        bool fightBossDead = _fightBoss is int fb && FightBossDead(dir, fb);
        _fightOver = true;
        _held = null;
        _deadBoss = fightBossDead ? _fightBoss : null;
        _deadBossAt = _cur.LastPartyHit ?? now;
        _frozen = snapshot;
    }

    // Called under _gate. Canlı denemenin görüntüsü (State/Message/Savasta/YerelAd çağıran tarafından doldurulur).
    private MeterSnapshot Compose(Aion2EntityDirectory dir, DateTime now, bool over)
    {
        BossHp? boss = BuildBoss(dir, now);
        int? bossId = boss is null ? null : (int)boss.Id;
        var rows = new List<Row>(_cur.BySource.Count);
        foreach ((int id, Aggregate agg) in _cur.BySource)
        {
            rows.Add(new Row(id, agg.Total, agg.SharesTarget, bossId is int b ? agg.Targets.GetValueOrDefault(b).Amount : 0,
                _fightBoss is int f && agg.Targets.TryGetValue(f, out var onFightBoss) && onFightBoss.Amount > 0));
        }

        // Aktif süre: uzun boşluklar (ölüm/diriliş, koşma) sayılmaz; DPS = hasar ÷ aktif süre.
        TimeSpan duration = _cur.Start is null ? TimeSpan.Zero : _cur.Active;
        return new MeterSnapshot(_state, _message, BuildPlayers(dir, rows, duration), boss, duration, over);
    }

    // Called under _gate.
    private IReadOnlyList<PlayerDamage> BuildPlayers(Aion2EntityDirectory dir, List<Row> rows, TimeSpan fightDuration)
    {
        bool rosterKnown = dir.RosterKnown;
        double seconds = Math.Max(1.0, fightDuration.TotalSeconds); // herkes için aynı aktif süre (ölüyken DPS düşer, oyun içi ölçer gibi)
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
            byId[rowId] = new PlayerDamage(rowId, name, 0, 0, local, (known is int c ? dir.ClassOf(c) : null) ?? dir.PartyClassOf(name), dir.CombatPowerOf(name),
                Olum: known is int dead ? _cur.Deaths.GetValueOrDefault(dead) : 0);
        }

        // Solo, karakter kaydı henüz gelmemişken: çıkarılan yerel oyuncu.
        if (byId.Count == 0 && _localId is int localId && dir.NameFor(localId) is string localName)
        {
            byId[localId] = new PlayerDamage(localId, DisplayName(localName, localId), 0, 0, true, dir.ClassOf(localId), dir.CombatPowerOf(localName),
                Olum: _cur.Deaths.GetValueOrDefault(localId));
        }

        var list = new List<PlayerDamage>(byId.Values);
        list.Sort(static (a, b) =>
            a.Total != b.Total ? b.Total.CompareTo(a.Total)
            : (a.CombatPower ?? -1) != (b.CombatPower ?? -1) ? (b.CombatPower ?? -1).CompareTo(a.CombatPower ?? -1)
            : string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private PlayerDamage Player(Aion2EntityDirectory dir, Row row, bool local, double seconds)
    {
        string name = DisplayName(dir.NameFor(row.Id), row.Id);
        return new PlayerDamage(row.Id, name, row.Total, row.Total / seconds, local, dir.ClassOf(row.Id) ?? dir.PartyClassOf(name), dir.CombatPowerOf(name),
            row.BossDamage / seconds, row.BossDamage, _cur.Deaths.GetValueOrDefault(row.Id));
    }

    /// <summary>
    /// Gösterilen boss: dövüş boss'u varsa ve gitmediyse o (ölüyse Dead = true). Yoksa upstream CombatOverlayData.BossHp (Ui/CombatOverlay.cs):
    /// kendi hedefi boss ise o; yoksa 15 sn içinde vurulan son hedef boss ise o; yoksa en son büyü başlatan boss; hiçbiri yoksa son boss 15 sn
    /// tutulur (canı > 0 ve aynı bölgeyse). Wipe şüphesinde can bilinmiyor (son bilinen can, kalan süre yok). Called under _gate.
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
        if (id == _fightBoss ? FightBossDead(dir, id) : DeadNow(dir, id))
        {
            _lastBoss = null; // upstream: ölü boss tutulmaz
            return new BossHp(id, BossName(dir, id), current, max, true, null, TimeToRage(dir, id, true, now));
        }

        if (_held is not null && id == _fightBoss)
        {
            return new BossHp(id, BossName(dir, id), current, max, false, null, TimeToRage(dir, id, false, now), CanBilinmiyor: true);
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

    private string Describe(int? id) =>
        id is not int e ? "-" : _source?.Entities.BossNpcIdOf(e) is int npc ? $"{e} ({Aion2BossCatalog.Find(npc)?.Name ?? npc.ToString()})" : e.ToString();

    /// <summary>Tanı satırı: zaman damgalı, tek satır. Çağıran önce MeterTani.Acik'a bakar. Kayıt hatası motoru durdurmaz.</summary>
    private static void Tani(string line)
    {
        try
        {
            MeterTani.Yaz?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
        catch (Exception)
        {
            // kayıt yazılamadı: ölçer çalışmaya devam eder
        }
    }

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
