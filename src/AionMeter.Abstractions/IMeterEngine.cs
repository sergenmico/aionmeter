namespace AionMeter;

// Motor (AionMeter.Core) ile overlay (AionMeter) arasındaki tek sözleşme.
// Overlay sadece Snapshot() okur; motor kendi iş parçacığında paketleri çözer.

/// <summary>Bir oyuncunun mevcut dövüşteki hasarı. Çağrılan yaratıkların hasarı sahibine eklenmiş olarak gelir.</summary>
/// Partideki herkes hasar atmasa da listede yer alır (Total = 0, Dps = 0).
/// <param name="ClassName">Oyunun İngilizce sınıf adı (Gladiator, Templar, Ranger, Assassin, Spiritmaster, Sorcerer, Cleric, Chanter); bilinmiyorsa null.</param>
/// <param name="CombatPower">Oyunun gösterdiği combat power (tam sayı); bilinmiyorsa null.</param>
/// <param name="BossDps">Sadece takip edilen bossa vurulan hasar ÷ aynı aktif süre; boss yoksa 0.</param>
public sealed record PlayerDamage(long Id, string Name, long Total, double Dps, bool IsLocal, string? ClassName = null, int? CombatPower = null, double BossDps = 0);

/// <summary>Takip edilen boss. Max bilinmiyorsa 0'dır.</summary>
/// <param name="TimeToKill">Kalan can ÷ son 30 sn'deki takım hasarının bossa DPS'i; tahmin edilemiyorsa null.</param>
/// <param name="TimeToRage">Öfkeye (berserk) kalan süre: boss_rage.json süresi − bossa ilk vuruştan beri geçen süre (eksiye düşebilir = öfkede); başlangıç bilinmiyorsa null.</param>
public sealed record BossHp(long Id, string Name, long Current, long Max, bool Dead, TimeSpan? TimeToKill = null, TimeSpan? TimeToRage = null);

/// <summary>Vuruş sonuçlarının sayaçları (oyundaki adlarıyla: Critical, Double Chance, Perfect Chance, arkadan, Parry).</summary>
public sealed record HitStats(long Hits, long Crits, long Doubles, long Perfects, long Backs, long Parries, long MaxHit);

/// <summary>Bir becerinin bu dövüşteki toplamı. Name oyunun İngilizce adıdır; bilinmiyorsa "Skill #id".</summary>
/// <param name="IconToken">assets/aion2/skills/icons/&lt;IconToken&gt;.png ikonunun adı (skill_icon_tokens.json, anahtar = SkillId / 10000); yoksa null.</param>
public sealed record SkillDamage(int SkillId, string Name, long Total, long Hits, long Crits, long MaxHit, string? IconToken = null);

/// <summary>Satıra tıklanınca açılan detay. Skills toplam hasara göre azalan sıralıdır.</summary>
public sealed record PlayerDetail(
    long Id,
    string Name,
    string? ClassName,
    int? CombatPower,
    long Total,
    double Dps,
    TimeSpan Active,
    HitStats Hits,
    IReadOnlyList<SkillDamage> Skills,
    // DPS grafiği: dövüş başından itibaren saniye başına hasar (en fazla 900 kova; uzun dövüşte kovalar birleştirilir, BucketSeconds büyür).
    IReadOnlyList<long>? DamagePerBucket = null,
    int BucketSeconds = 1);

public enum EngineState
{
    Starting,      // açılıyor
    NoNpcap,       // Npcap sürücüsü bulunamadı
    NoAdapter,     // ağ kartı açılamadı
    WaitingGame,   // oyun trafiği bekleniyor
    Connected,     // oyun trafiği görüldü
    Failed,        // beklenmeyen hata, Message'a bak
}

/// <summary>Overlay'in bir karede çizdiği her şey. Players toplam hasara göre azalan sıralıdır.</summary>
public sealed record MeterSnapshot(
    EngineState State,
    string? Message,
    IReadOnlyList<PlayerDamage> Players,
    BossHp? Boss,
    TimeSpan FightDuration,
    // Boss öldü ya da dövüş bitti: tablo yeni dövüşün ilk vuruşuna kadar donmuş sonuç olarak kalır.
    bool FightOver = false);

public interface IMeterEngine : IDisposable
{
    /// <summary>Yakalamayı arka planda başlatır; bloklamaz, hata fırlatmaz (hatalar State/Message'a yansır).</summary>
    void Start();

    /// <summary>İş parçacığı güvenli, ucuz; overlay saniyede birkaç kez çağırır.</summary>
    MeterSnapshot Snapshot();

    /// <summary>Mevcut dövüşte bu oyuncunun detayı; yoksa null. Sadece detay paneli açıkken çağrılır.</summary>
    PlayerDetail? Detail(long playerId);

    /// <summary>Mevcut dövüşün hasar tablosunu sıfırlar.</summary>
    void Reset();
}
