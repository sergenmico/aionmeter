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
    bool YeniRekor);

/// <summary>
/// Kukla DPS testi: Bekliyor (motor sıfırlandı, ilk yerel vuruş bekleniyor) → Sayiyor (FightDuration ilerledikçe geri sayım)
/// → Sonuc (yerel satır ve detay anlık alınır, kart gösterilir). Overlay test sürerken ~100 ms'de bir yoklar.
/// Süre motorun dövüş süresinden okunur: vuruş 3 sn kesilirse motor süreyi son vuruşta durdurur, geri sayım da durur.
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
    public KuklaSonucu? Sonuc { get; private set; }

    private TimeSpan _ofset;         // yerel vuruştan önce başka biri dövüşü başlattıysa o kadar düşülür
    private TimeSpan _sonSure;
    private DateTime _sonDegisim;
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

        // Not: motor, son vuruştan 3 sn sonra süreyi son vuruşa geri çeker (birkaç sn azalabilir); bu sıfırlama sayılmaz.
        // Yeni dövüş / sıfırlama, yerel toplamın düşmesinden (ya da listeden çıkmasından) anlaşılır.
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

        if (Durum == Hal.Bekliyor)
        {
            if (yerel is null || yerel.Total <= 0) return false;
            Durum = Hal.Sayiyor;
            _ofset = sure > TimeSpan.FromSeconds(1) ? sure : TimeSpan.Zero;
            _sonSure = sure;
            _sonDegisim = DateTime.UtcNow;
        }

        // Sayıyor
        if ((yerel?.Total ?? 0) < Toplam)
        {
            // Dövüş sıfırlandı (yeni dövüş): baştan bekle.
            Baslat(Sure);
            return true;
        }
        bool degisti = false;
        if (sure != _sonSure)
        {
            _sonSure = sure;
            _sonDegisim = DateTime.UtcNow;
        }
        bool durdu = DateTime.UtcNow - _sonDegisim > TimeSpan.FromSeconds(1.5);
        if (durdu != Durdu) { Durdu = durdu; degisti = true; }

        var gecen = sure - _ofset;
        if (gecen < TimeSpan.Zero) gecen = TimeSpan.Zero;
        long toplam = Math.Max(0, yerel?.Total ?? 0);
        if ((long)gecen.TotalSeconds != (long)Gecen.TotalSeconds || toplam != Toplam) degisti = true;
        Gecen = gecen;
        Toplam = toplam;
        Dps = gecen.TotalSeconds >= 1 ? toplam / gecen.TotalSeconds : 0;

        if (gecen.TotalSeconds >= Sure) Bitir(yerel, detay, tamam: true);
        else if (s.FightOver) Bitir(yerel, detay, tamam: false);
        else return degisti;
        return true;
    }

    private static PlayerDamage? Yerel(MeterSnapshot s)
    {
        var l = s.Players;
        if (l is null) return null;
        for (int i = 0; i < l.Count; i++) if (l[i].IsLocal) return l[i];
        return null;
    }

    private void Bitir(PlayerDamage? yerel, Func<long, PlayerDetail?> detay, bool tamam)
    {
        PlayerDetail? d = null;
        if (yerel is not null)
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

        Sonuc = new KuklaSonucu(Sure, Gecen, tamam, sinif, Toplam, Dps, beceriler, onceki, yeni);
        Durum = Hal.Sonuc;
        _sonucToplam = Toplam;
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
        Oku().TryGetValue(Anahtar(sinif, sure), out long v) && v > 0 ? v : null;

    public static void Yaz(string? sinif, int sure, long dps)
    {
        try
        {
            var d = Oku();
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

    private static Dictionary<string, long> Oku()
    {
        try
        {
            if (File.Exists(Dosya))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(Dosya)) ?? new();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Kukla rekorları okunamadı", ex);
        }
        return new();
    }
}
