using System.Text;
using System.Text.Json;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;

namespace AionMeter.Core;

/// <summary>
/// Bir saha bossunun bilinen durumu. <see cref="GorunenAd"/>: aynı ad başka haritada ya da takvimde de varsa sonuna kısa harita adı
/// eklenmiş hâli ("High Commander Lagta · Verteron"). <see cref="ZamanUtc"/>: canlıysa göründüğü an, ölüyse geri geleceği an;
/// bilinmiyorsa <see cref="DateTime.MinValue"/>. <see cref="GorulduUtc"/>: bu bilginin oyundan geldiği an.
/// </summary>
public readonly record struct SahaBossDurumu(int Yer, string GorunenAd, string Harita, bool Canli, DateTime ZamanUtc, DateTime GorulduUtc);

/// <summary>
/// Saha bosslarının son bilinen durumu. Çözücü iş parçacığı <see cref="Guncelle"/> ile yazar, arayüz <see cref="Oku"/> ile okur
/// (kilitli, küçük: en çok katalogdaki boss sayısı kadar kayıt). Ad kataloğu assets/aion2/bosses/saha_bosslari.json; katalogda olmayan
/// yer kimliği tutulmaz (tahmin yok).
/// <para>Kalıcılık: <see cref="KaliciYap"/> verilen dosyadan yükler; durum değişince en çok dakikada bir (okuma sırasında, arayüz
/// iş parçacığında) ve kapanışta yazılır. 24 saatten eski bilgiler ve geri gelme anının üstünden 12 saat geçmiş kayıtlar silinir.</para>
/// </summary>
public sealed class SahaBossDeposu
{
    /// <summary>Uygulamanın tek deposu (çözücü varsayılan olarak buna yazar).</summary>
    public static SahaBossDeposu Ortak { get; } = new();

    /// <summary>Geri gelme anının üstünden bu kadar geçince kayıt bayattır.</summary>
    public static readonly TimeSpan GeriGelmeSiniri = TimeSpan.FromHours(12);

    /// <summary>Oyundan bu kadar süredir yenisi gelmeyen bilgi silinir.</summary>
    public static readonly TimeSpan BilgiSiniri = TimeSpan.FromHours(24);

    /// <summary>Dosyaya iki yazım arasındaki en kısa süre.</summary>
    public static readonly TimeSpan YazmaAraligi = TimeSpan.FromMinutes(1);

    /// <summary>Durum aynıyken görülme anı ancak bu kadar ilerleyince "değişti" sayılır (dosya her liste açılışında yazılmasın).</summary>
    private static readonly TimeSpan GorulmeAdimi = TimeSpan.FromMinutes(10);

    private sealed record Katalog(string Ad, string GorunenAd, int Harita, string HaritaAdi);

    private readonly record struct Durum(bool Canli, long ZamanMs, long GorulduMs);

    private readonly object _kilit = new();
    private readonly Dictionary<int, Durum> _durum = new();
    private Dictionary<int, Katalog>? _katalog;
    private string? _dosya;
    private bool _kirli;
    private DateTime _sonYazim = DateTime.MinValue;
    private bool _cikisKancasi;

    /// <summary>Katalogdaki boss sayısı (veri dosyası yoksa 0).</summary>
    public int KatalogBoyutu
    {
        get { lock (_kilit) return KatalogAl().Count; }
    }

    /// <summary>Bilinen durum sayısı.</summary>
    public int Sayi
    {
        get { lock (_kilit) return _durum.Count; }
    }

    /// <summary>Bu yer kimliği katalogda var mı?</summary>
    public bool Biliniyor(int yer)
    {
        lock (_kilit) return KatalogAl().ContainsKey(yer);
    }

    // ---------------- Yazma (çözücü iş parçacığı) ----------------

    /// <summary>Bir haritanın listesi geldi: listedeki her bossun durumu yenilenir. Listede olmayanlar olduğu gibi kalır.</summary>
    public void Guncelle(int harita, IReadOnlyList<SahaBossKaydi> kayitlar, DateTime gorulduUtc)
    {
        long goruldu = Ms(gorulduUtc);
        lock (_kilit)
        {
            var katalog = KatalogAl();
            foreach (var k in kayitlar)
            {
                if (!katalog.TryGetValue(k.Yer, out var b) || b.Harita != harita)
                {
                    continue; // bilinmeyen yer: ad tahmini yapılmaz
                }

                var yeni = new Durum(k.Canli, k.ZamanMs, goruldu);
                if (_durum.TryGetValue(k.Yer, out var eski) && eski.Canli == yeni.Canli && eski.ZamanMs == yeni.ZamanMs)
                {
                    if (goruldu - eski.GorulduMs < (long)GorulmeAdimi.TotalMilliseconds)
                    {
                        continue;
                    }
                }

                _durum[k.Yer] = yeni;
                _kirli = true;
            }
        }
    }

    // ---------------- Okuma (arayüz iş parçacığı) ----------------

    /// <summary>
    /// Gösterilebilir durumları yer kimliği sırasıyla <paramref name="hedef"/>'e yazar (önce temizler). Bayat kayıtlar burada silinir;
    /// bekleyen bir değişiklik varsa ve son yazımın üstünden bir dakika geçtiyse dosya da burada yazılır.
    /// </summary>
    public void Oku(DateTime utcSimdi, List<SahaBossDurumu> hedef)
    {
        hedef.Clear();
        string? yazilacak = null, dosya = null;
        lock (_kilit)
        {
            Temizle(utcSimdi);
            var katalog = KatalogAl();
            foreach (var (yer, d) in _durum)
            {
                if (!katalog.TryGetValue(yer, out var b))
                {
                    continue;
                }

                DateTime zaman = d.ZamanMs > 0 ? Utc(d.ZamanMs) : DateTime.MinValue;
                if (!d.Canli && d.ZamanMs <= 0)
                {
                    continue; // ölü ama ne zaman geleceği bilinmiyor: gösterecek bir şey yok
                }

                hedef.Add(new SahaBossDurumu(yer, b.GorunenAd, b.HaritaAdi, d.Canli, zaman, Utc(d.GorulduMs)));
            }

            if (_kirli && _dosya is not null && utcSimdi - _sonYazim >= YazmaAraligi)
            {
                yazilacak = Serilestir();
                dosya = _dosya;
                _kirli = false;
                _sonYazim = utcSimdi;
            }
        }

        hedef.Sort(static (a, b) => a.Yer.CompareTo(b.Yer));
        if (yazilacak is not null)
        {
            Yaz(dosya!, yazilacak);
        }
    }

    // ---------------- Kalıcılık ----------------

    /// <summary>Durum bu dosyada saklanır: önce dosyadan yüklenir, uygulama kapanırken bekleyen değişiklik yazılır.</summary>
    /// <param name="utcSimdi">Bayat kayıt temizliği için an (denetim); verilmezse şimdi.</param>
    /// <param name="kapanistaYaz">false: kapanışta yazılmaz (denetim geçici dosya bırakmasın).</param>
    public void KaliciYap(string dosya, DateTime? utcSimdi = null, bool kapanistaYaz = true)
    {
        lock (_kilit)
        {
            _dosya = dosya;
            Yukle(dosya);
            Temizle(utcSimdi ?? DateTime.UtcNow);
            if (kapanistaYaz && !_cikisKancasi)
            {
                _cikisKancasi = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Bosalt();
            }
        }
    }

    /// <summary>Bekleyen değişikliği hemen yazar (kapanış).</summary>
    public void Bosalt()
    {
        string? yazilacak, dosya;
        lock (_kilit)
        {
            if (!_kirli || _dosya is null)
            {
                return;
            }

            yazilacak = Serilestir();
            dosya = _dosya;
            _kirli = false;
            _sonYazim = DateTime.UtcNow;
        }

        Yaz(dosya, yazilacak);
    }

    private void Temizle(DateTime utc)
    {
        long simdi = Ms(utc);
        long bilgi = (long)BilgiSiniri.TotalMilliseconds, geri = (long)GeriGelmeSiniri.TotalMilliseconds;
        List<int>? sil = null;
        foreach (var (yer, d) in _durum)
        {
            bool bayat = simdi - d.GorulduMs > bilgi || (!d.Canli && d.ZamanMs > 0 && simdi - d.ZamanMs > geri);
            if (bayat)
            {
                (sil ??= new List<int>()).Add(yer);
            }
        }

        if (sil is null)
        {
            return;
        }

        foreach (int yer in sil)
        {
            _durum.Remove(yer);
        }

        _kirli = true;
    }

    /// <summary><c>{"bosslar":[{"yer":101001,"canli":false,"zaman":1763...,"goruldu":1763...}]}</c></summary>
    private string Serilestir()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("bosslar");
            foreach (var (yer, d) in _durum.OrderBy(kv => kv.Key))
            {
                w.WriteStartObject();
                w.WriteNumber("yer", yer);
                w.WriteBoolean("canli", d.Canli);
                w.WriteNumber("zaman", d.ZamanMs);
                w.WriteNumber("goruldu", d.GorulduMs);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void Yukle(string dosya)
    {
        try
        {
            if (!File.Exists(dosya))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(dosya));
            var katalog = KatalogAl();
            foreach (JsonElement e in doc.RootElement.GetProperty("bosslar").EnumerateArray())
            {
                int yer = e.GetProperty("yer").GetInt32();
                if (!katalog.ContainsKey(yer) || _durum.ContainsKey(yer))
                {
                    continue; // bu oturumda gelen bilgi daha yeni
                }

                long zaman = e.GetProperty("zaman").GetInt64(), goruldu = e.GetProperty("goruldu").GetInt64();
                if ((zaman != 0 && !MakulZaman(zaman)) || !MakulZaman(goruldu))
                {
                    continue; // elle bozulmuş değer
                }

                _durum[yer] = new Durum(e.GetProperty("canli").GetBoolean(), zaman, goruldu);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // bozuk ya da okunamayan dosya: boş başlanır, ilk değişiklikte üzerine yazılır
        }
    }

    private static void Yaz(string dosya, string icerik)
    {
        try
        {
            string gecici = dosya + ".tmp";
            File.WriteAllText(gecici, icerik, new UTF8Encoding(false));
            File.Move(gecici, dosya, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bir sonraki değişiklikte yeniden denenir
        }
    }

    // ---------------- Katalog ----------------

    private Dictionary<int, Katalog> KatalogAl() => _katalog ??= KatalogYukle();

    private static Dictionary<int, Katalog> KatalogYukle()
    {
        var sonuc = new Dictionary<int, Katalog>();
        try
        {
            string yol = AssetPaths.Of("bosses", "saha_bosslari.json");
            if (!File.Exists(yol))
            {
                return sonuc;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(yol));
            var haritalar = new Dictionary<int, string>();
            foreach (JsonProperty p in doc.RootElement.GetProperty("haritalar").EnumerateObject())
            {
                if (int.TryParse(p.Name, out int h))
                {
                    haritalar[h] = p.Value.GetString() ?? "";
                }
            }

            var ham = new List<(int Yer, int Harita, string Ad)>();
            foreach (JsonElement b in doc.RootElement.GetProperty("bosslar").EnumerateArray())
            {
                string ad = b.GetProperty("ad").GetString() ?? "";
                if (ad.Length > 0)
                {
                    ham.Add((b.GetProperty("yer").GetInt32(), b.GetProperty("harita").GetInt32(), ad));
                }
            }

            // Aynı ad başka bir haritada ya da takvimde de varsa harita adıyla ayrılır (aynı haritadaki eş adlılar ayrılmaz: listede birleşir).
            var takvim = new HashSet<string>(EtkinlikTakvimi.GlobalKalemler.Where(k => k.Tur == TakvimTuru.Boss).Select(k => k.Ad), StringComparer.Ordinal);
            var adHaritalari = ham.GroupBy(x => x.Ad, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Harita).Distinct().Count(), StringComparer.Ordinal);
            foreach (var (yer, harita, ad) in ham)
            {
                string haritaAdi = haritalar.GetValueOrDefault(harita, "");
                bool ayir = haritaAdi.Length > 0 && (adHaritalari[ad] > 1 || takvim.Contains(ad));
                sonuc[yer] = new Katalog(ad, ayir ? ad + " · " + haritaAdi : ad, harita, haritaAdi);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            sonuc.Clear(); // katalog yoksa saha bossu gösterilmez
        }

        return sonuc;
    }

    private static long Ms(DateTime utc) =>
        new DateTimeOffset(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static bool MakulZaman(long ms) => ms is >= 1_600_000_000_000 and <= 2_600_000_000_000;

    private static DateTime Utc(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
