using System.Diagnostics;
using System.Text.Json;

namespace AionMeter;

/// <summary>
/// Sahte motor: 5 kişilik parti, artan hasar, eriyen boss canı; boss ölür, kısa süre sonra yeni dövüş başlar. Arada bir Gizli Küp gelir.
/// Beceri adları ve ikon jetonları gerçek oyun verisinden (assets/aion2/skills) okunur; dosyalar yoksa yedek adlar kullanılır.
/// İş parçacığı kullanmaz: her Snapshot() çağrısında geçen süre kadar ilerler.
/// </summary>
internal sealed class DemoEngine : IMeterEngine
{
    private sealed class Oyuncu
    {
        public required long Id;
        public required string Ad;
        public required double TemelDps;
        public required bool Yerel;
        public string? Sinif;
        public int? Cp;
        public double SessizSure; // dövüşün ilk bu kadar saniyesinde hasar vermez (zindanda bekleyen üye)
        public double[] OlumAnlari = Array.Empty<double>(); // dövüşün bu saniyelerinde ölür (ölüm göstergesi görünsün)
        public double Toplam;
        public double BossToplam;                      // sadece bossa vurulan kısım
        public readonly List<long> Kovalar = new();    // saniye başına hasar (DPS grafiği)
    }

    // Dövüşler sırayla: zindan bossu (5 kişilik parti) ↔ dünya bossu (parti + ~20 yabancı).
    private const string DunyaBossuAdi = "Frostfang Colossus";
    private const long DunyaBossuCani = 42_000_000;
    private const int DunyaBossuOfkesi = 600;
    private static readonly string[] YabanciAdlari =
    {
        "Aldric", "Brynna", "Caelum", "Darvos", "Elowen", "Fenrik", "Gwyndra", "Halvar", "Isolde", "Joran",
        "Kyrra", "Lucan", "Maelis", "Norric", "Oriel", "Perrin", "Quilla", "Rhydian", "Sylva", "Tarquin",
    };
    private static readonly string[] Siniflar =
        { "Gladiator", "Templar", "Ranger", "Assassin", "Sorcerer", "Spiritmaster", "Cleric", "Chanter" };
    // Yerel oyuncu (82.000) ~8. sırada kalsın: 6 yabancı ondan güçlü, kalanlar zayıf.
    private static readonly double[] YabanciDpsleri =
    {
        140_000, 128_000, 118_000, 110_000, 101_000, 92_000, 79_000, 76_000, 72_000, 68_000,
        64_000, 60_000, 57_000, 53_000, 49_000, 45_000, 41_000, 37_000, 34_000, 30_000,
    };

    private static readonly string[] BossAdlari = { "Stone Golem King", "Crimson Drake Ragnar", "Shadow Priestess Ilyana" };
    private static readonly long[] BossCanlari = { 7_200_000, 9_600_000, 12_500_000 };
    // Öfke süresi (sn): ilk ikisi rahat yetişir; üçüncüsü kısa, "geç" ve "ÖFKEDE" durumları görünsün.
    private static readonly int[] BossOfkeleri = { 300, 300, 25 };
    private const double BossaGidenPay = 0.92; // hasarın %92'si bossa, kalanı yan yaratıklara

    private readonly object _kilit = new();
    private readonly Random _rnd = new(7);
    private readonly Stopwatch _saat = new();
    private readonly Oyuncu[] _oyuncular =
    {
        new() { Id = 1, Ad = "Kaelith", TemelDps = 82_000, Yerel = true, Sinif = "Sorcerer", Cp = 752_340 },
        new() { Id = 2, Ad = "Vorn", TemelDps = 96_000, Yerel = false, Sinif = "Gladiator", Cp = 1_184_200 },
        new() { Id = 3, Ad = "Seraphine", TemelDps = 74_000, Yerel = false, Sinif = "Spiritmaster", Cp = 968_515 },
        new() { Id = 4, Ad = "Draxis", TemelDps = 61_000, Yerel = false, Sinif = "Assassin", Cp = 611_090, SessizSure = 10, OlumAnlari = new[] { 22.0 } },
        new() { Id = 5, Ad = "Lunea", TemelDps = 55_000, Yerel = false, Sinif = "Cleric", Cp = 534_870, OlumAnlari = new[] { 30.0, 55.0 } },
    };

    private readonly Oyuncu[] _herkes;   // parti + yabancılar (dünya bossu evresi)
    private Oyuncu[] _aktif;             // şu anki dövüşte listede olanlar
    private int _dovusNo = -1, _zindanSira = -1;
    private string _bossAd = "";
    private int _bossOfke;

    private bool _dovusVar;
    private double _sonZaman, _dovusBaslangic, _olumZamani;
    private bool _bossOldu;
    private long _bossId, _bossMax, _bossCan;
    private bool _kapali;

    // ---- Detay (sahte ama tutarlı: toplam hasar sabit oranlarla becerilere dağıtılır) ----

    private static readonly double[] BeceriPayi = { 0.24, 0.19, 0.16, 0.13, 0.11, 0.09, 0.08 };
    private static readonly double[] BeceriOrtVurus = { 5_200, 9_800, 3_100, 14_500, 2_400, 21_000, 7_300 };

    private readonly record struct DemoBeceri(int Id, string Ad, string? Ikon);

    // Gerçek AION 2 beceri kimlikleri (skill_names.json); ikon jetonu = skill_icon_tokens.json[id / 10000].
    // Adlar burada yedek olarak da durur: JSON bulunamazsa bunlar kullanılır, ikonlar yer tutucuya düşer.
    private static readonly Dictionary<string, (int Id, string Ad)[]> SinifBeceriKimlikleri = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sorcerer"] = new[] { (15010000, "Flame Scattershot"), (15020000, "Firebomb"), (15030000, "Burst"), (15040000, "Firestorm"),
                               (15050000, "Blaze"), (15060000, "Hellfire"), (15090000, "Ice Chain") },
        ["Gladiator"] = new[] { (11010000, "Rending Blow"), (11020000, "Keen Strike"), (11030000, "Rupture Strike"), (11040000, "Wrathful Strike"),
                                (11050000, "Crushing Wave"), (11090000, "Blade Dance"), (11100000, "Ruinous Blow") },
        ["Spiritmaster"] = new[] { (16010000, "Cold Shock"), (16020000, "Vacuum Explosion"), (16030000, "Earth Tremor"), (16040000, "Combustion"),
                                   (16050000, "Ashy Call"), (16060000, "Siphon") },
        ["Assassin"] = new[] { (13010000, "Quick Slice"), (13030000, "Breaking Slice"), (13040000, "Swift Slice"), (13050000, "Flash Slice"),
                               (13060000, "Ambush"), (13070000, "Shadowstrike"), (13020000, "Throw Shadowblade") },
        ["Templar"] = new[] { (12010000, "Vicious Strike"), (12020000, "Decisive Strike"), (12030000, "Desperate Strike"),
                              (12060000, "Punishing Strike"), (12100000, "Shield Smite"), (12040000, "Pummel") },
        ["Ranger"] = new[] { (14010000, "Deadshot"), (14020000, "Snipe"), (14030000, "Rapid Fire"), (14040000, "Spiral Arrow"),
                             (14050000, "Drill Dart"), (14080000, "Burst Arrow") },
        ["Chanter"] = new[] { (18020000, "Resonance Crush"), (18030000, "Bolt Crush"), (18040000, "Incandescent Blow"),
                              (18050000, "Bursting Blow"), (18070000, "Crushing Blow"), (18080000, "Wave Blow") },
        ["Cleric"] = new[] { (17010000, "Earth's Retribution"), (17020000, "Thunder and Lightning"), (17030000, "Discharge"),
                             (17040000, "Judgment Thunder"), (17060000, "Bolt"), (17070000, "Chain of Torment") },
    };
    private static readonly (int Id, string Ad)[] GenelBeceriKimlikleri = { (11000001, "Basic Attack") };

    private readonly Dictionary<string, DemoBeceri[]> _sinifBecerileri = new(StringComparer.OrdinalIgnoreCase);
    private readonly DemoBeceri[] _genelBeceriler;

    public DemoEngine(string? assetsDir = null)
    {
        var rnd = new Random(42);
        var yabancilar = new Oyuncu[YabanciAdlari.Length];
        for (int i = 0; i < yabancilar.Length; i++)
        {
            yabancilar[i] = new Oyuncu
            {
                Id = 100 + i, Ad = YabanciAdlari[i], TemelDps = YabanciDpsleri[i], Yerel = false,
                Sinif = Siniflar[(i * 3 + 1) % Siniflar.Length], Cp = 300_000 + rnd.Next(0, 1_000_001),
                OlumAnlari = i % 7 == 3 ? new[] { 15.0 + i } : Array.Empty<double>(),
            };
        }
        _herkes = new Oyuncu[_oyuncular.Length + yabancilar.Length];
        _oyuncular.CopyTo(_herkes, 0);
        yabancilar.CopyTo(_herkes, _oyuncular.Length);
        _aktif = _oyuncular;

        var gerekenAd = new HashSet<string>();
        var gerekenIkon = new HashSet<string>();
        foreach (var liste in SinifBeceriKimlikleri.Values)
            foreach (var (id, _) in liste) { gerekenAd.Add(id.ToString()); gerekenIkon.Add((id / 10000).ToString()); }
        foreach (var (id, _) in GenelBeceriKimlikleri) { gerekenAd.Add(id.ToString()); gerekenIkon.Add((id / 10000).ToString()); }

        string? beceriKlasoru = assetsDir is null ? null : Path.Combine(assetsDir, "skills");
        var adlar = JsonOku(beceriKlasoru, "skill_names.json", gerekenAd);
        var ikonlar = JsonOku(beceriKlasoru, "skill_icon_tokens.json", gerekenIkon);

        DemoBeceri[] Coz((int Id, string Ad)[] liste)
        {
            var sonuc = new DemoBeceri[liste.Length];
            for (int i = 0; i < liste.Length; i++)
            {
                var (id, yedek) = liste[i];
                string ad = adlar.TryGetValue(id.ToString(), out var a) ? a : yedek;
                ikonlar.TryGetValue((id / 10000).ToString(), out var ikon);
                sonuc[i] = new DemoBeceri(id, ad, ikon);
            }
            return sonuc;
        }
        foreach (var (sinif, liste) in SinifBeceriKimlikleri) _sinifBecerileri[sinif] = Coz(liste);
        _genelBeceriler = Coz(GenelBeceriKimlikleri);
    }

    /// <summary>Düz { "anahtar": "değer" } JSON'undan sadece gereken anahtarları okur; dosya yoksa/bozuksa boş döner.</summary>
    private static Dictionary<string, string> JsonOku(string? klasor, string dosya, HashSet<string> gereken)
    {
        var sonuc = new Dictionary<string, string>();
        if (klasor is null) return sonuc;
        try
        {
            string yol = Path.Combine(klasor, dosya);
            if (!File.Exists(yol)) return sonuc;
            using var akis = File.OpenRead(yol);
            using var belge = JsonDocument.Parse(akis);
            foreach (var p in belge.RootElement.EnumerateObject())
                if (gereken.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String)
                    sonuc[p.Name] = p.Value.GetString()!;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Demo: " + dosya + " okunamadı", ex);
        }
        return sonuc;
    }

    public PlayerDetail? Detail(long playerId)
    {
        lock (_kilit)
        {
            if (_kapali || !_dovusVar) return null;
            Oyuncu? o = null;
            foreach (var x in _aktif) if (x.Id == playerId) { o = x; break; }
            if (o is null) return null;

            double t = _saat.Elapsed.TotalSeconds;
            double bitis = _bossOldu ? _olumZamani : t;
            double sure = Math.Max(0, bitis - _dovusBaslangic);
            var aktif = TimeSpan.FromSeconds(Math.Max(0, sure - o.SessizSure));
            long toplam = (long)o.Toplam;
            double kritikOrani = 0.18 + 0.04 * (o.Id % 4);

            DemoBeceri[] adlar = o.Sinif is not null && _sinifBecerileri.TryGetValue(o.Sinif, out var a) ? a : _genelBeceriler;
            var beceriler = new List<SkillDamage>(adlar.Length);
            long vurusTop = 0, kritikTop = 0, enYuksek = 0;
            for (int i = 0; i < adlar.Length; i++)
            {
                long h = (long)(toplam * BeceriPayi[i]);
                long vurus = h > 0 ? Math.Max(1, (long)(h / BeceriOrtVurus[i])) : 0;
                long kritik = (long)(vurus * kritikOrani);
                long maks = vurus > 0 ? (long)(BeceriOrtVurus[i] * (2.4 + 0.1 * i)) : 0;
                var b = adlar[i];
                beceriler.Add(new SkillDamage(b.Id, b.Ad, h, vurus, kritik, maks, b.Ikon));
                vurusTop += vurus; kritikTop += kritik; enYuksek = Math.Max(enYuksek, maks);
            }
            var vuruslar = new HitStats(vurusTop, kritikTop, (long)(vurusTop * 0.11), (long)(vurusTop * 0.04),
                (long)(vurusTop * 0.27), (long)(vurusTop * 0.02), enYuksek);
            double dps = sure >= 1 ? toplam / sure : 0;
            return new PlayerDetail(o.Id, o.Ad, o.Sinif, o.Cp, toplam, dps, aktif, vuruslar, beceriler,
                o.Kovalar.ToArray(), BucketSeconds: 1); // kopya: motor kilidi dışında güvenle okunur
        }
    }

    public void Start()
    {
        lock (_kilit) _saat.Start();
    }

    /// <summary>Kayıtlı ad verildiyse demodaki kendi karakterin o adı alır (ayar → motor → Snapshot.YerelAd yolu görünsün).</summary>
    public void YerelAdAyarla(string? ad)
    {
        lock (_kilit)
        {
            if (string.IsNullOrWhiteSpace(ad)) return;
            foreach (var o in _oyuncular) if (o.Yerel) o.Ad = ad.Trim();
        }
    }

    public void Reset()
    {
        lock (_kilit)
        {
            if (_dovusVar) YeniDovus(_saat.Elapsed.TotalSeconds, sonrakiBoss: false); // aynı boss/evre baştan
        }
    }

    public MeterSnapshot Snapshot()
    {
        lock (_kilit)
        {
            if (_kapali) return new MeterSnapshot(EngineState.Failed, "Motor kapatıldı", Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);

            double t = _saat.Elapsed.TotalSeconds;
            if (t < 1.0) return new MeterSnapshot(EngineState.Starting, null, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);
            if (t < 2.5) return new MeterSnapshot(EngineState.WaitingGame, null, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);

            if (!_dovusVar) YeniDovus(t);
            Ilerle(t);

            double bitis = _bossOldu ? _olumZamani : t;
            var sure = TimeSpan.FromSeconds(Math.Max(0, bitis - _dovusBaslangic));
            double sn = Math.Max(1.0, sure.TotalSeconds);

            var liste = new List<PlayerDamage>(_aktif.Length);
            string? yerelAd = null;
            foreach (var o in _aktif)
            {
                long toplam = (long)o.Toplam;
                int olum = 0;
                foreach (double an in o.OlumAnlari) if (sure.TotalSeconds >= an) olum++;
                liste.Add(new PlayerDamage(o.Id, o.Ad, toplam, toplam / sn, o.Yerel, o.Sinif, o.Cp, o.BossToplam / sn,
                    (long)o.BossToplam, olum));
                if (o.Yerel) yerelAd = o.Ad;
            }
            liste.Sort(static (a, b) => b.Total.CompareTo(a.Total));

            // Kalan süre tahmini: kalan can ÷ partinin bossa ortalama DPS'i (ilk 3 sn tahmin yok)
            double bossaToplam = 0;
            foreach (var o in _aktif) bossaToplam += o.BossToplam;
            // Dünya bossunda 18–24. saniyeler: can okuması yok (wipe şüphesi) → overlay "?" gösterir.
            bool canBilinmiyor = !_bossOldu && _dovusNo % 2 == 1 && sure.TotalSeconds is >= 18 and < 24;
            TimeSpan? ttk = null;
            if (!_bossOldu && !canBilinmiyor && sure.TotalSeconds >= 3 && bossaToplam > 0)
                ttk = TimeSpan.FromSeconds(_bossCan / (bossaToplam / sure.TotalSeconds));
            var ofke = TimeSpan.FromSeconds(_bossOfke - sure.TotalSeconds);
            var boss = new BossHp(_bossId, _bossAd, _bossCan, _bossMax, _bossOldu, ttk, ofke, canBilinmiyor);
            // Gizli Küp: 20. saniyeden sonra her 60 sn'de bir küp gelir ve 25 sn menzilde kalır (ses ve rozet görünsün).
            int kupGelis = t < 20 ? 0 : (int)((t - 20) / 60) + 1;
            int kupYakinda = t >= 20 && (t - 20) % 60 < 25 ? 1 : 0;
            // Demo: küp kuzeydoğudan yaklaşır (yön yavaşça döner, mesafe 95 m'den azalır).
            double kupSn = (t - 20) % 60;
            double? kupYon = kupYakinda > 0 ? (40 + kupSn * 3) % 360 : null;
            int? kupMesafe = kupYakinda > 0 ? (int)Math.Max(3, 95 - kupSn * 3.6) : null;
            // Savaşta: boss ölene dek. Ölümden sonraki ~10 sn'lik donmuş sonuçta overlay ~8 sn sonra soluklaşır.
            return new MeterSnapshot(EngineState.Connected, null, liste, boss, sure, FightOver: _bossOldu,
                Savasta: !_bossOldu, YerelAd: yerelAd, KupGelis: kupGelis, KupYakinda: kupYakinda,
                KupYonDerece: kupYon, KupMesafe: kupMesafe);
        }
    }

    private void YeniDovus(double t, bool sonrakiBoss = true)
    {
        _dovusVar = true;
        _dovusBaslangic = t;
        _sonZaman = t;
        _bossOldu = false;
        if (sonrakiBoss) _dovusNo++;
        if (!sonrakiBoss && _bossAd.Length > 0) { /* evre ve boss aynı kalır */ }
        else if (_dovusNo % 2 == 1)
        {
            _aktif = _herkes;
            _bossAd = DunyaBossuAdi;
            _bossMax = DunyaBossuCani;
            _bossOfke = DunyaBossuOfkesi;
        }
        else
        {
            _aktif = _oyuncular;
            _zindanSira = (_zindanSira + 1) % BossAdlari.Length;
            _bossAd = BossAdlari[_zindanSira];
            _bossMax = BossCanlari[_zindanSira];
            _bossOfke = BossOfkeleri[_zindanSira];
        }
        _bossId = 1000 + _dovusNo;
        _bossCan = _bossMax;
        foreach (var o in _herkes)
        {
            o.Toplam = 0;
            o.BossToplam = 0;
            o.Kovalar.Clear();
        }
    }

    private void Ilerle(double t)
    {
        double dt = Math.Clamp(t - _sonZaman, 0, 1.0);
        _sonZaman = t;

        if (_bossOldu)
        {
            // Ölüm sonrası ~10 sn sonuç donmuş kalır (FightOver), sonra yeni boss.
            if (t - _olumZamani > 10.0) YeniDovus(t);
            return;
        }

        foreach (var o in _aktif)
        {
            if (_bossCan <= 0) break;
            if (t - _dovusBaslangic < o.SessizSure) continue;
            double hasar = o.TemelDps * dt * (0.55 + 0.9 * _rnd.NextDouble());
            long h = Math.Min(_bossCan, (long)(hasar * BossaGidenPay));
            long yan = (long)(hasar * (1 - BossaGidenPay));
            o.Toplam += h + yan;
            o.BossToplam += h;
            _bossCan -= h;
            int kova = (int)(t - _dovusBaslangic);
            if (kova < 900)
            {
                while (o.Kovalar.Count <= kova) o.Kovalar.Add(0);
                o.Kovalar[kova] += h + yan;
            }
        }

        if (_bossCan <= 0)
        {
            _bossCan = 0;
            _bossOldu = true;
            _olumZamani = t;
        }
    }

    public void Dispose()
    {
        lock (_kilit)
        {
            _kapali = true;
            _saat.Stop();
        }
    }
}

/// <summary>Gerçek motor oluşturulamazsa overlay'in hata göstermesi için.</summary>
internal sealed class HataliMotor(string mesaj) : IMeterEngine
{
    private readonly MeterSnapshot _anlik = new(EngineState.Failed, mesaj, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero);
    public void Start() { }
    public MeterSnapshot Snapshot() => _anlik;
    public PlayerDetail? Detail(long playerId) => null;
    public void Reset() { }
    public void YerelAdAyarla(string? ad) { }
    public void Dispose() { }
}
