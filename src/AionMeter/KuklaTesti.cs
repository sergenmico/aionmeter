using System.Text.Json;

namespace AionMeter;

/// <summary>Biten bir kukla testinin anlık sonucu (sonuç kartı bundan çizilir).</summary>
internal sealed record KuklaSonucu(
    int Sure,                 // seçilen süre (sn)
    TimeSpan Gercek,          // ölçülen süre (erken bittiyse kısa)
    bool Tamam,               // süre doldu mu (erken biten test rekora sayılmaz)
    string? Sinif,
    long Toplam,
    double Dps,
    IReadOnlyList<(string Ad, long Hasar)> Beceriler, // en çok vuran 3 beceri
    long? OncekiRekor,        // aynı sınıf ve süredeki kişisel en iyi (test öncesi)
    bool YeniRekor,
    bool Kesildi = false);  // deneme sıfırlandı / birleşti (wipe, yeni deneme): sayılar son tutarlı yoklamadan

/// <summary>
/// Kukla DPS testi: Bekliyor (motor sıfırlandı, ilk yerel vuruş bekleniyor) → Sayiyor (FightDuration ilerledikçe geri sayım)
/// → Sonuc (yerel satır ve detay anlık alınır, kart gösterilir). Overlay test sürerken ~100 ms'de bir yoklar.
/// Süre motorun dövüş süresinden okunur: FightDuration aktif süredir (ardışık vuruşlar arası ≤ 3 sn olan aralıklar
/// toplanır, deneme içinde azalmaz); vuruş kesilirse süre ilerlemez, geri sayım da durur.
/// DPS = (toplam − başlangıçta görülen toplam) ÷ (süre − başlangıç süresi): ilk vuruşun hasarı sıfır süreyle sayılmasın
/// diye başlangıç anındaki hasar paydan, o anki süre paydadan düşülür (n vuruş / n−1 aralık yanlılığı yok).
/// Sonuç kartındaki "Toplam" ise gerçek toplamdır.
/// </summary>
internal sealed class KuklaTesti
{
    public enum Hal { Kapali, Bekliyor, Sayiyor, Sonuc }

    public Hal Durum { get; private set; }
    public int Sure { get; private set; }
    public TimeSpan Gecen { get; private set; }     // ilk yerel vuruştan beri (motor süresiyle)
    public long Toplam { get; private set; }
    public double Dps { get; private set; }
    public bool Durdu { get; private set; }         // sayarken süre ilerlemiyor (vuruş yok)
    public bool YerelYok { get; private set; }      // beklerken hasar var ama yerel karakter henüz tanınmadı
    public KuklaSonucu? Sonuc { get; private set; }
    public DateTime SonucZamani { get; private set; } // kart ne zaman çıktı (savaş dışı kuralına dönmek için)

    /// <summary>Motor aktif süreyi ancak yeni vuruş gelince ilerletir; 3 sn'ye kadar aralar sayılır. Bundan uzun durursa "durdu".</summary>
    private static readonly TimeSpan DurduEsigi = TimeSpan.FromSeconds(3.5);

    private TimeSpan _ofset;         // başlangıç anındaki motor süresi (paydadan düşülür)
    private long _ilkToplam;         // başlangıç anında görülen yerel toplam (paydan düşülür)
    private TimeSpan _sonSure;
    private DateTime _sonDegisim, _sonYoklama;
    private long _sonucToplam;      // sonuç anındaki (ve sonra görülen en büyük) yerel toplam

    public bool Etkin => Durum is Hal.Bekliyor or Hal.Sayiyor;

    public void Baslat(int sure)
    {
        Sure = Math.Clamp(sure, Ayarlar.EnAzKukla, Ayarlar.EnFazlaKukla);
        Durum = Hal.Bekliyor;
        Sonuc = null;
        Gecen = TimeSpan.Zero;
        Toplam = 0;
        Dps = 0;
        Durdu = false;
        YerelYok = false;
    }

    public void Kapat()
    {
        Durum = Hal.Kapali;
        Sonuc = null;
    }

    /// <summary>Anlık görüntüyle ilerler. Görünen bir şey değiştiyse true.</summary>
    public bool Ilerle(MeterSnapshot s, Func<long, PlayerDetail?> detay)
    {
        if (Durum == Hal.Kapali) return false;
        var yerel = Yerel(s);
        var sure = s.FightDuration;

        // Sonuç kartı: yeni dövüş / sıfırlama yerel toplamın düşmesinden (ya da listeden çıkmasından) anlaşılır.
        if (Durum == Hal.Sonuc)
        {
            long t = yerel?.Total ?? 0;
            if (t < _sonucToplam)
            {
                Kapat(); // yeni dövüş başladı: kart kapanır
                return true;
            }
            _sonucToplam = t;
            return false;
        }

        var simdi = DateTime.UtcNow;
        if (Durum == Hal.Bekliyor)
        {
            if (yerel is null || yerel.Total <= 0)
            {
                // Hasar akıyor ama "ben" bilinmiyor: kullanıcıya kısa bilgi (karakter tanınınca kendiliğinden başlar).
                bool yok = yerel is null && HasarVar(s);
                if (yok == YerelYok) return false;
                YerelYok = yok;
                return true;
            }
            YerelYok = false;
            Durum = Hal.Sayiyor;
            _ofset = sure;
            _ilkToplam = yerel.Total;
            _sonSure = sure;
            _sonDegisim = _sonYoklama = simdi;
            Toplam = yerel.Total;
        }

        // Sayıyor. Toplam düştüyse (wipe / yeni deneme / sıfırlama), süre geri gittiyse ya da iki yoklama arasında geçen
        // gerçek zamandan fazla ileri sıçradıysa (denemeler birleşti) test sessizce sürmez: "yarıda kaldı", rekora sayılmaz.
        long yeniToplam = yerel?.Total ?? 0;
        bool geriGitti = yeniToplam < Toplam || sure < _sonSure;
        bool sicradi = sure - _sonSure > simdi - _sonYoklama + TimeSpan.FromSeconds(1.5);
        _sonYoklama = simdi;
        if (geriGitti || sicradi)
        {
            // Motorun detayı artık yeni denemeye ait: beceri listesi karta konmaz, sayılar son tutarlı yoklamadan.
            Bitir(yerel, null, tamam: false, kesildi: true);
            return true;
        }

        bool degisti = false;
        if (sure != _sonSure)
        {
            _sonSure = sure;
            _sonDegisim = simdi;
        }
        bool durdu = simdi - _sonDegisim > DurduEsigi;
        if (durdu != Durdu) { Durdu = durdu; degisti = true; }

        var gecen = sure - _ofset;
        if (gecen < TimeSpan.Zero) gecen = TimeSpan.Zero;
        long toplam = Math.Max(0, yeniToplam);
        if ((long)gecen.TotalSeconds != (long)Gecen.TotalSeconds || toplam != Toplam) degisti = true;
        Gecen = gecen;
        Toplam = toplam;
        Dps = gecen.TotalSeconds >= 1 ? Math.Max(0, toplam - _ilkToplam) / gecen.TotalSeconds : 0;

        if (gecen.TotalSeconds >= Sure) Bitir(yerel, detay, tamam: true);
        else if (s.FightOver) Bitir(yerel, detay, tamam: false);
        else return degisti;
        return true;
    }

    private static bool HasarVar(MeterSnapshot s)
    {
        var l = s.Players;
        if (l is null) return false;
        for (int i = 0; i < l.Count; i++) if (l[i].Total > 0) return true;
        return false;
    }

    private static PlayerDamage? Yerel(MeterSnapshot s)
    {
        var l = s.Players;
        if (l is null) return null;
        for (int i = 0; i < l.Count; i++) if (l[i].IsLocal) return l[i];
        return null;
    }

    private void Bitir(PlayerDamage? yerel, Func<long, PlayerDetail?>? detay, bool tamam, bool kesildi = false)
    {
        PlayerDetail? d = null;
        if (yerel is not null && detay is not null)
        {
            try { d = detay(yerel.Id); }
            catch (Exception ex) { HataLog.Yaz("Kukla detayı", ex); }
        }
        var beceriler = new List<(string, long)>(3);
        var liste = d?.Skills;
        if (liste is not null)
            for (int i = 0; i < liste.Count && beceriler.Count < 3; i++)
            {
                var b = liste[i];
                if (b.Total <= 0) continue;
                beceriler.Add((string.IsNullOrWhiteSpace(b.Name) ? "Beceri #" + b.SkillId.ToString(Bicim.Tr) : b.Name, b.Total));
            }

        string? sinif = yerel?.ClassName ?? d?.ClassName;
        long? onceki = KuklaRekorlari.Al(sinif, Sure);
        long dps = (long)Math.Round(Dps);
        bool yeni = tamam && dps > 0 && (onceki is null || dps > onceki.Value);
        if (yeni) KuklaRekorlari.Yaz(sinif, Sure, dps);

        Sonuc = new KuklaSonucu(Sure, Gecen, tamam, sinif, Toplam, Dps, beceriler, onceki, yeni, kesildi);
        Durum = Hal.Sonuc;
        SonucZamani = DateTime.UtcNow;
        _sonucToplam = yerel?.Total ?? 0;
    }
}

/// <summary>
/// Kişisel en iyiler: sınıf + süre bazında DPS. kukla-rekor.json (ayarlar.ini ile aynı klasörde).
/// Dosya küçük; sadece test bitince okunur/yazılır.
/// </summary>
internal static class KuklaRekorlari
{
    public static string Dosya => Path.Combine(Ayarlar.Klasor, "kukla-rekor.json");

    private static string Anahtar(string? sinif, int sure) =>
        (string.IsNullOrWhiteSpace(sinif) ? "Bilinmeyen" : sinif) + "|" + sure.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static long? Al(string? sinif, int sure) =>
        Oku() is { } d && d.TryGetValue(Anahtar(sinif, sure), out long v) && v > 0 ? v : null;

    public static void Yaz(string? sinif, int sure, long dps)
    {
        try
        {
            var d = Oku();
            if (d is null) return; // okunamayan dosya kenara alınamadı: üstüne yazılmaz
            d[Anahtar(sinif, sure)] = dps;
            string gecici = Dosya + ".tmp";
            File.WriteAllText(gecici, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(gecici, Dosya, overwrite: true);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Kukla rekoru kaydedilemedi", ex);
        }
    }

    /// <summary>Rekorlar; dosya yoksa boş. Bozuksa .bozuk olarak kenara alınır ve boş döner; kenara alınamazsa null (yazılmaz).</summary>
    private static Dictionary<string, long>? Oku()
    {
        try
        {
            if (File.Exists(Dosya))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Dosya)) ?? new();
        }
        catch (Exception ex)
        {
            // Bozuk dosyanın üstüne yazılmasın: kukla-rekor.json.bozuk olarak saklanır, yeni dosya baştan başlar.
            HataLog.Yaz("Kukla rekorları okunamadı (kukla-rekor.json.bozuk olarak saklanıyor)", ex);
            try { File.Move(Dosya, Dosya + ".bozuk", overwrite: true); }
            catch (Exception ex2)
            {
                HataLog.Yaz("Bozuk rekor dosyası taşınamadı", ex2);
                return null;
            }
        }
        return new();
    }
}
