namespace AionMeter.Core;

/// <summary>Sunucu bölgesi: takvim saatleri bölgenin yerel saatidir (yaz saati dahil).</summary>
public enum SunucuBolgesi { Avrupa, NaDogu, NaBati, LatinAmerika, Asya }

public enum TakvimTuru { Etkinlik, Boss }

/// <summary>
/// Takvimdeki bir kalem: ya gün içinde düzenli aralıkla (her 60/180/720 dk, ilk saat verilir) ya da haftanın belirli
/// günlerinde sabit saatte. Saatler bölgenin yerel saatidir. <see cref="AcikDk"/>: etkinliğin girişi başladıktan sonra
/// bu kadar açık kalır (0 = yok).
/// </summary>
public sealed class TakvimKalemi
{
    public string Ad { get; }
    public TakvimTuru Tur { get; }
    /// <summary>0 = haftalık kalem.</summary>
    public int PeriyotDk { get; }
    /// <summary>Gün içindeki ilk başlangıç (gece yarısından dakika).</summary>
    public int IlkDk { get; }
    /// <summary>Etkinlik girişinin açık kaldığı süre (dk); 0 = yok.</summary>
    public int AcikDk { get; }
    private readonly bool[] _gunler = new bool[7]; // DayOfWeek dizini; düzenli kalemde hepsi açık

    private TakvimKalemi(string ad, TakvimTuru tur, int periyotDk, int ilkDk, int acikDk, DayOfWeek[]? gunler)
    {
        Ad = ad;
        Tur = tur;
        PeriyotDk = periyotDk;
        IlkDk = ilkDk;
        AcikDk = acikDk;
        if (gunler is null) Array.Fill(_gunler, true);
        else foreach (var g in gunler) _gunler[(int)g] = true;
    }

    /// <summary>Her gün <paramref name="ilkDk"/>'dan başlayıp <paramref name="periyotDk"/> arayla (gün içinde).</summary>
    public static TakvimKalemi Duzenli(string ad, TakvimTuru tur, int periyotDk, int ilkDk, int acikDk = 0) =>
        new(ad, tur, periyotDk, ilkDk, acikDk, null);

    /// <summary>Verilen günlerde tek seferlik sabit saat.</summary>
    public static TakvimKalemi Haftalik(string ad, TakvimTuru tur, int saatDk, int acikDk, params DayOfWeek[] gunler) =>
        new(ad, tur, 0, saatDk, acikDk, gunler);

    public bool GununKalemi(DayOfWeek g) => _gunler[(int)g];

    /// <summary>
    /// Başladıktan sonra listede kalma süresi: etkinlikte giriş penceresi, bossta doğuştan sonraki "şimdi" süresi (60 sn).
    /// </summary>
    public TimeSpan AcikSure => AcikDk > 0 ? TimeSpan.FromMinutes(AcikDk) : Tur == TakvimTuru.Boss ? EtkinlikTakvimi.BossSimdiSuresi : TimeSpan.Zero;
}

/// <summary>Bir kalemin belirli bir başlangıcı (UTC). Etkinlikte giriş açık penceresi varsa <see cref="GirisBitisUtc"/> dolu.</summary>
public readonly record struct TakvimOlayi(TakvimKalemi Kalem, DateTime BaslangicUtc, DateTime? GirisBitisUtc)
{
    public string Ad => Kalem.Ad;
    public TakvimTuru Tur => Kalem.Tur;

    /// <summary>Başladı mı (etkinlikte giriş açık, bossta "şimdi")?</summary>
    public bool BasladiMi(DateTime utcSimdi) => utcSimdi >= BaslangicUtc;

    /// <summary>Listeden düşeceği an: etkinlikte giriş kapanışı, bossta doğuştan 60 sn sonra.</summary>
    public DateTime BitisUtc => BaslangicUtc + Kalem.AcikSure;
}

/// <summary>
/// AION 2 Global etkinlik ve boss takvimi (questlog.gg'nin herkese açık global takvimi, 2026-11-13). Saatler bölgenin yerel
/// saatidir; yaz saati geçişleri TimeZoneInfo ile hesaplanır. Yerel saatin olmadığı an (ilkbaharda ileri alınan saat) ileri
/// kayar (02:00 → 03:00); iki kez yaşanan saat (sonbahar) ilk kez yaşandığı anda sayılır.
/// <para>Ucuz: her kalemin son sorgusu önbellekte; sorgu anı bir sonraki başlangıcı geçmedikçe yeniden hesaplanmaz.
/// Saniyede birkaç kez aynı iş parçacığından çağrılmak için; iş parçacığı güvenli değildir.</para>
/// </summary>
public sealed class EtkinlikTakvimi
{
    /// <summary>Boss doğduktan sonra listede "şimdi" olarak kaldığı süre.</summary>
    public static readonly TimeSpan BossSimdiSuresi = TimeSpan.FromSeconds(60);

    /// <summary>Global takvim (kalem sırası listede eşit başlangıçlarda korunur).</summary>
    public static readonly IReadOnlyList<TakvimKalemi> GlobalKalemler = new[]
    {
        TakvimKalemi.Duzenli("Shugo Festival", TakvimTuru.Etkinlik, 60, 0, acikDk: 3),
        TakvimKalemi.Duzenli("Dimensional Invasion", TakvimTuru.Etkinlik, 60, 30, acikDk: 3),
        TakvimKalemi.Duzenli("Spacetime Rift", TakvimTuru.Etkinlik, 180, 2 * 60, acikDk: 10),
        TakvimKalemi.Haftalik("Artifact Siege", TakvimTuru.Etkinlik, 21 * 60, 30, DayOfWeek.Thursday, DayOfWeek.Saturday),
        TakvimKalemi.Duzenli("Watcher Kaira", TakvimTuru.Boss, 180, 1 * 60),
        TakvimKalemi.Duzenli("Argo, the Spirit King", TakvimTuru.Boss, 720, 10 * 60 + 30),
        TakvimKalemi.Haftalik("Guardian Lord Nahma", TakvimTuru.Boss, 21 * 60, 0, DayOfWeek.Sunday, DayOfWeek.Friday),
        TakvimKalemi.Haftalik("Siege Bosses", TakvimTuru.Boss, 21 * 60 + 30, 0, DayOfWeek.Thursday, DayOfWeek.Saturday),
        TakvimKalemi.Haftalik("Middle Reshanta Siege Bosses", TakvimTuru.Boss, 21 * 60 + 30, 0,
            DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday),
    };

    /// <summary>Arayüzdeki bölge adları (enum sırasıyla).</summary>
    public static readonly string[] BolgeAdlari = { "Avrupa", "NA Doğu", "NA Batı", "Latin Amerika", "Asya" };

    public static string SaatDilimiKimligi(SunucuBolgesi b) => b switch
    {
        SunucuBolgesi.NaDogu => "Eastern Standard Time",
        SunucuBolgesi.NaBati => "Pacific Standard Time",
        SunucuBolgesi.LatinAmerika => "E. South America Standard Time",
        SunucuBolgesi.Asya => "Tokyo Standard Time",
        _ => "W. Europe Standard Time",
    };

    public SunucuBolgesi Bolge { get; }
    public TimeZoneInfo SaatDilimi { get; }
    public IReadOnlyList<TakvimKalemi> Kalemler { get; }

    // Önbellek: kalem başına son sorgu anı ve ondan sonraki ilk başlangıç.
    private readonly DateTime[] _sorgu, _sonuc;

    public EtkinlikTakvimi(SunucuBolgesi bolge = SunucuBolgesi.Avrupa, IReadOnlyList<TakvimKalemi>? kalemler = null)
    {
        Bolge = bolge;
        SaatDilimi = SaatDilimiBul(SaatDilimiKimligi(bolge));
        Kalemler = kalemler ?? GlobalKalemler;
        _sorgu = new DateTime[Kalemler.Count];
        _sonuc = new DateTime[Kalemler.Count];
        Array.Fill(_sorgu, DateTime.MaxValue);
    }

    private static TimeZoneInfo SaatDilimiBul(string kimlik)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(kimlik); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc; // sistemde yoksa (çok nadir) UTC: saatler kayar ama çökmez
        }
    }

    /// <summary>Kalemin <paramref name="utcSimdi"/>'den kesin sonraki ilk başlangıcı.</summary>
    public TakvimOlayi Sonraki(TakvimKalemi kalem, DateTime utcSimdi) => Olay(kalem, IlkBaslangic(Indeks(kalem), Utc(utcSimdi)));

    /// <summary>
    /// Kalemin şu anki olayı: başlamış ve hâlâ açıksa (etkinlikte giriş, bossta 60 sn "şimdi") o, değilse bir sonraki.
    /// </summary>
    public TakvimOlayi Guncel(TakvimKalemi kalem, DateTime utcSimdi)
    {
        int i = Indeks(kalem);
        return Olay(kalem, IlkBaslangic(i, Utc(utcSimdi) - kalem.AcikSure));
    }

    /// <summary>
    /// Başlamasına ≤ <paramref name="pencere"/> kalanlar ve başlamış ama hâlâ açık olanlar, başlangıca göre sıralı
    /// (eşitlikte takvim sırası). <paramref name="hedef"/> verilirse temizlenip doldurulur (ayırma yapılmaz).
    /// </summary>
    public List<TakvimOlayi> Yaklasanlar(DateTime utcSimdi, TimeSpan pencere, List<TakvimOlayi>? hedef = null)
    {
        hedef ??= new List<TakvimOlayi>();
        hedef.Clear();
        DateTime simdi = Utc(utcSimdi);
        for (int i = 0; i < Kalemler.Count; i++)
        {
            var o = Guncel(Kalemler[i], simdi);
            if (o.BaslangicUtc - simdi <= pencere) hedef.Add(o);
        }
        // Az eleman: kararlı ekleme sıralaması (List.Sort kararlı değil, eşit saatte takvim sırası korunsun).
        for (int i = 1; i < hedef.Count; i++)
        {
            var x = hedef[i];
            int j = i - 1;
            while (j >= 0 && hedef[j].BaslangicUtc > x.BaslangicUtc)
            {
                hedef[j + 1] = hedef[j];
                j--;
            }
            hedef[j + 1] = x;
        }
        return hedef;
    }

    // ---------------- Hesap ----------------

    private TakvimOlayi Olay(TakvimKalemi k, DateTime bas) =>
        new(k, bas, k.Tur == TakvimTuru.Etkinlik && k.AcikDk > 0 ? bas.AddMinutes(k.AcikDk) : null);

    private int Indeks(TakvimKalemi k)
    {
        for (int i = 0; i < Kalemler.Count; i++)
            if (ReferenceEquals(Kalemler[i], k)) return i;
        throw new ArgumentException("Kalem bu takvimde yok: " + k.Ad, nameof(k));
    }

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    /// <summary>Kalemin <paramref name="enAz"/>'dan kesin sonraki ilk başlangıcı (UTC). Önbellekli.</summary>
    private DateTime IlkBaslangic(int i, DateTime enAz)
    {
        if (enAz >= _sorgu[i] && enAz < _sonuc[i]) return _sonuc[i];
        DateTime sonuc = Hesapla(Kalemler[i], enAz);
        _sorgu[i] = enAz;
        _sonuc[i] = sonuc;
        return sonuc;
    }

    private DateTime Hesapla(TakvimKalemi k, DateTime enAz)
    {
        // Yerel günün bir gün öncesinden başlanır (bölge saati UTC'nin gerisinde/ilerisinde olabilir); yerel→UTC dönüşümü
        // artan sırayı korur (ileri kayan saat bir sonrakine eşit olabilir, geri gitmez), bu yüzden ilk bulunan en küçüğüdür.
        DateTime gun = TimeZoneInfo.ConvertTimeFromUtc(enAz, SaatDilimi).Date.AddDays(-1);
        for (int d = 0; d < 10; d++, gun = gun.AddDays(1))
        {
            if (!k.GununKalemi(gun.DayOfWeek)) continue;
            int adim = k.PeriyotDk > 0 ? k.PeriyotDk : 24 * 60;
            for (int dk = k.IlkDk; dk < 24 * 60; dk += adim)
            {
                DateTime utc = YereldenUtc(gun.AddMinutes(dk));
                if (utc > enAz) return utc;
            }
        }
        return DateTime.MaxValue; // olmaz: her kalem haftada en az bir kez
    }

    /// <summary>Bölge yerel saati → UTC. Olmayan saat ileri kayar, iki kez yaşanan saat ilk anıyla alınır.</summary>
    private DateTime YereldenUtc(DateTime yerel)
    {
        yerel = DateTime.SpecifyKind(yerel, DateTimeKind.Unspecified);
        TimeSpan ofset;
        if (SaatDilimi.IsInvalidTime(yerel))
            ofset = SaatDilimi.GetUtcOffset(yerel.AddHours(-6)); // geçişten önceki ofset: an, saat ileri alındıktan hemen sonrasına düşer
        else if (SaatDilimi.IsAmbiguousTime(yerel))
        {
            ofset = TimeSpan.MinValue;
            foreach (var o in SaatDilimi.GetAmbiguousTimeOffsets(yerel))
                if (o > ofset) ofset = o; // büyük ofset = daha erken UTC (ilk yaşanış)
        }
        else ofset = SaatDilimi.GetUtcOffset(yerel);
        return DateTime.SpecifyKind(yerel - ofset, DateTimeKind.Utc);
    }
}
