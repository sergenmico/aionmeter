using System.Drawing.Imaging;

namespace AionMeter.Cevirmen;

// Arayüz ile çeviri motoru (AionMeter.Ceviri) arasındaki ince katman. Arayüz sadece bunu bilir;
// gerçek kütüphane bağlanana kadar SahteCeviriMotoru ile derlenir/çalışır.
//
// TODO(şef): AionMeter.Ceviri bağlanınca AionMeter.csproj'a
//   <ProjectReference Include="..\AionMeter.Ceviri\AionMeter.Ceviri.csproj" />
//   <DefineConstants>$(DefineConstants);AIONMETER_CEVIRI</DefineConstants>
// ekleyin; fabrika otomatik olarak CeviriMotoruAdaptoru'nu kullanır.

internal sealed record OkunanSatir(string Metin, Rectangle Kutu);

internal sealed record CeviriCiktisi(string Ingilizce, string Turkce, IReadOnlyList<string> KorunanTerimler,
    bool OnbellektenGeldi, TimeSpan Sure);

internal interface ICeviriMotoru : IDisposable
{
    /// <summary>Çeviri modeli diskte hazır mı? (Ucuz değilse çağıran önbelleğe alır.)</summary>
    bool ModelHazir { get; }

    /// <summary>İndirilecek toplam model boyutu (bayt).</summary>
    long ModelBoyutu { get; }

    Task ModeliIndirAsync(IProgress<(long Inen, long Toplam)> ilerleme, CancellationToken ct);
    Task<IReadOnlyList<OkunanSatir>> OkuAsync(Bitmap goruntu, CancellationToken ct);
    IReadOnlyList<string> Paragraflar(IReadOnlyList<OkunanSatir> satirlar);

    /// <summary>Çevirmen ilk çağrıda tembel yüklenir; boşta kalınca kendini boşaltır.</summary>
    Task<CeviriCiktisi> CevirAsync(string ingilizce, CancellationToken ct);

    /// <summary>İmleç çevresi görüntüsünde açıklama kutusunu bulur (görüntüye göre koordinat).</summary>
    Rectangle? TooltipBul(Bitmap imlecCevresi, Point imlecGoreli);

    /// <summary>Düşük RAM modu. Model yüklüyse bırakılır; sonraki çeviride yeni modda yüklenir.</summary>
    void DusukRamAyarla(bool dusukRam);
}

internal static class CeviriMotoruFabrikasi
{
    public static ICeviriMotoru Olustur()
    {
#if AIONMETER_CEVIRI
        return new CeviriMotoruAdaptoru();
#else
        return new SahteCeviriMotoru();
#endif
    }
}

#if AIONMETER_CEVIRI
/// <summary>AionMeter.Ceviri kütüphanesine bağdaştırıcı.</summary>
internal sealed class CeviriMotoruAdaptoru : ICeviriMotoru
{
    private readonly object _kilit = new();
    private AionMeter.Ceviri.CevrimdisiCevirmen? _cevirmen;
    private bool _dusukRam;

    public bool ModelHazir => AionMeter.Ceviri.ModelIndirici.HazirMi();
    public long ModelBoyutu => AionMeter.Ceviri.ModelIndirici.ToplamBoyut;

    public Task ModeliIndirAsync(IProgress<(long Inen, long Toplam)> ilerleme, CancellationToken ct) =>
        AionMeter.Ceviri.ModelIndirici.IndirAsync(new Progress<(long, long)>(t => ilerleme.Report(t)), ct);

    public async Task<IReadOnlyList<OkunanSatir>> OkuAsync(Bitmap goruntu, CancellationToken ct)
    {
        var satirlar = await AionMeter.Ceviri.Ocr.OkuAsync(goruntu, ct).ConfigureAwait(false);
        var sonuc = new List<OkunanSatir>(satirlar.Count);
        foreach (var s in satirlar) sonuc.Add(new OkunanSatir(s.Metin, s.Kutu));
        return sonuc;
    }

    public IReadOnlyList<string> Paragraflar(IReadOnlyList<OkunanSatir> satirlar)
    {
        var liste = new List<AionMeter.Ceviri.OcrSatir>(satirlar.Count);
        foreach (var s in satirlar) liste.Add(new AionMeter.Ceviri.OcrSatir(s.Metin, s.Kutu));
        return AionMeter.Ceviri.Paragraf.Birlestir(liste);
    }

    public async Task<CeviriCiktisi> CevirAsync(string ingilizce, CancellationToken ct)
    {
        var c = await Cevirmen().CevirAsync(ingilizce, ct).ConfigureAwait(false);
        return new CeviriCiktisi(c.Ingilizce, c.Turkce, c.KorunanTerimler, c.OnbellektenGeldi, c.Sure);
    }

    public Rectangle? TooltipBul(Bitmap imlecCevresi, Point imlecGoreli) =>
        AionMeter.Ceviri.TooltipBulucu.Bul(imlecCevresi, imlecGoreli);

    private AionMeter.Ceviri.CevrimdisiCevirmen Cevirmen()
    {
        lock (_kilit)
        {
            if (_cevirmen is null)
            {
                string sozlukYolu = Path.Combine(AppContext.BaseDirectory, "assets", "aion2", "ceviri", "sozluk.json");
                var sozluk = AionMeter.Ceviri.OyunSozlugu.Yukle(sozlukYolu);
                // Önbellek ayarlar.ini ile aynı klasörde (exe yanı ya da %AppData%\aionmeter).
                _onbellek = new AionMeter.Ceviri.CeviriOnbellegi(Path.Combine(Ayarlar.Klasor, "ceviri-onbellek.json"));
                _cevirmen = new AionMeter.Ceviri.CevrimdisiCevirmen(AionMeter.Ceviri.ModelIndirici.VarsayilanKlasor, sozluk, _onbellek)
                {
                    DusukBellek = _dusukRam, // model yüklenmeden önce ayarlanmalı
                };
            }
            return _cevirmen;
        }
    }

    private AionMeter.Ceviri.CeviriOnbellegi? _onbellek;

    public void DusukRamAyarla(bool dusukRam)
    {
        AionMeter.Ceviri.CevrimdisiCevirmen? c;
        lock (_kilit)
        {
            if (_dusukRam == dusukRam) return;
            _dusukRam = dusukRam;
            c = _cevirmen;
            if (c is null) return; // henüz yüklenmedi: ilk yüklemede yeni mod kullanılır
            c.DusukBellek = dusukRam;
        }
        // Yüklü model eski modda: bırak (çeviri sürüyorsa bitmesini bekler — arayüzü bloklamasın diye arka planda).
        Task.Run(() =>
        {
            try { c.Bosalt(); }
            catch (Exception ex) { HataLog.Yaz("Çeviri modeli bırakılamadı", ex); }
        });
    }

    public void Dispose()
    {
        lock (_kilit)
        {
            _cevirmen?.Dispose(); // modeli bırakır, önbelleği kaydeder
            _cevirmen = null;
            _onbellek?.Dispose(); // önbelleği çevirmen dispose etmiyor
            _onbellek = null;
        }
    }
}
#endif

/// <summary>
/// Kütüphane bağlanana kadar: sahte OCR/çeviri (arayüzün derlenip denenebilmesi için).
/// Test kancaları (sadece sahte motorda): AIONMETER_SAHTE_GECIKME = gecikme çarpanı (varsayılan 1),
/// AIONMETER_SAHTE_TOOLTIP=1 → imlecin 16 px sağ-altındaki 300×150 alanı "açıklama kutusu" sayar,
/// AIONMETER_SAHTE_INDIRME_HATA=1 → model indirmesi %40'ta hata verir.
/// </summary>
internal sealed class SahteCeviriMotoru : ICeviriMotoru
{
    private static readonly int Carpan =
        int.TryParse(Environment.GetEnvironmentVariable("AIONMETER_SAHTE_GECIKME"), out int c) && c > 0 ? c : 1;
    private static readonly bool SahteKutu = Environment.GetEnvironmentVariable("AIONMETER_SAHTE_TOOLTIP") == "1";

    private const string Ing1 = "The Daeva are the ancient guardians of the Elyos. In this quest you must enter the temple in Elyos lands and open the sealed gate.";
    private const string Ing2 = "Deals 1,250 damage to the target and reduces its Physical Defense by 20% for 6 seconds.";
    private const string Tr1 = "Daeva, Elyos'un kadim koruyucularıdır. Bu görevde Elyos topraklarındaki tapınağa girip mühürlü kapıyı açman gerekiyor.";
    private const string Tr2 = "Hedefe 1.250 hasar verir ve 6 saniye boyunca Physical Defense değerini %20 düşürür.";

    private volatile bool _hazir;

    public bool ModelHazir => _hazir;
    public long ModelBoyutu => 80L * 1024 * 1024;

    public async Task ModeliIndirAsync(IProgress<(long Inen, long Toplam)> ilerleme, CancellationToken ct)
    {
        bool hata = Environment.GetEnvironmentVariable("AIONMETER_SAHTE_INDIRME_HATA") == "1";
        for (int i = 1; i <= 20; i++)
        {
            await Task.Delay(100 * Carpan, ct).ConfigureAwait(false);
            ilerleme.Report((ModelBoyutu * i / 20, ModelBoyutu));
            if (hata && i == 8) throw new HttpRequestException("Sunucuya bağlanılamadı (sahte).");
        }
        _hazir = true;
    }

    public async Task<IReadOnlyList<OkunanSatir>> OkuAsync(Bitmap goruntu, CancellationToken ct)
    {
        await Task.Delay(150 * Carpan, ct).ConfigureAwait(false);
        return new[]
        {
            new OkunanSatir(Ing1, new Rectangle(0, 0, goruntu.Width, 18)),
            new OkunanSatir(Ing2, new Rectangle(0, 40, goruntu.Width, 18)),
        };
    }

    public IReadOnlyList<string> Paragraflar(IReadOnlyList<OkunanSatir> satirlar)
    {
        var p = new List<string>(satirlar.Count);
        foreach (var s in satirlar) p.Add(s.Metin);
        return p;
    }

    public async Task<CeviriCiktisi> CevirAsync(string ingilizce, CancellationToken ct)
    {
        await Task.Delay(300 * Carpan, ct).ConfigureAwait(false);
        return ingilizce.Contains("Daeva", StringComparison.Ordinal)
            ? new CeviriCiktisi(ingilizce, Tr1, new[] { "Daeva", "Elyos" }, false, TimeSpan.FromMilliseconds(300))
            : new CeviriCiktisi(ingilizce, Tr2, new[] { "Physical Defense" }, false, TimeSpan.FromMilliseconds(300));
    }

    public void DusukRamAyarla(bool dusukRam) { }

    public Rectangle? TooltipBul(Bitmap imlecCevresi, Point imlecGoreli)
    {
        if (!SahteKutu) return null;
        var r = new Rectangle(imlecGoreli.X + 16, imlecGoreli.Y + 16, 300, 150);
        r.Intersect(new Rectangle(0, 0, imlecCevresi.Width, imlecCevresi.Height));
        return r.IsEmpty ? null : r;
    }

    public void Dispose() { }
}

/// <summary>Ekran görüntüsü ve küçük özet (değişim algılama) yardımcıları.</summary>
internal static class EkranAraclari
{
    public const int OzetG = 32, OzetY = 24;

    /// <summary>
    /// Ekran alanını yakalar. Windows 11'de önce Windows.Graphics.Capture (tam ekran oyunlarda da çalışır, görünmez);
    /// Windows 10'da önce GDI, görüntü tamamen siyahsa (tam ekran oyun) Windows.Graphics.Capture. Hepsi olmazsa GDI sonucu.
    /// </summary>
    public static Bitmap Yakala(Rectangle ekranAlani)
    {
        if (EkranYakalayici.Kenarsiz && EkranYakalayici.Yakala(ekranAlani) is Bitmap wgc) return wgc;
        var gdi = GdiYakala(ekranAlani);
        if (!EkranYakalayici.Kenarsiz && EkranYakalayici.Destekleniyor && Siyah(gdi) &&
            EkranYakalayici.Yakala(ekranAlani) is Bitmap yedek)
        {
            gdi.Dispose();
            return yedek;
        }
        return gdi;
    }

    private static Bitmap GdiYakala(Rectangle ekranAlani)
    {
        var bmp = new Bitmap(Math.Max(1, ekranAlani.Width), Math.Max(1, ekranAlani.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(ekranAlani.Location, Point.Empty, ekranAlani.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Görüntü (seyrek örneklemeyle) tamamen siyah mı? GDI tam ekran DirectX oyununda böyle döner.</summary>
    private static bool Siyah(Bitmap bmp)
    {
        var veri = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int adimX = Math.Max(1, bmp.Width / 64), adimY = Math.Max(1, bmp.Height / 64);
            var satir = new byte[bmp.Width * 4];
            for (int y = 0; y < bmp.Height; y += adimY)
            {
                System.Runtime.InteropServices.Marshal.Copy(veri.Scan0 + y * veri.Stride, satir, 0, satir.Length);
                for (int x = 0; x < bmp.Width; x += adimX)
                {
                    int j = x * 4;
                    if (satir[j] > 8 || satir[j + 1] > 8 || satir[j + 2] > 8) return false;
                }
            }
            return true;
        }
        finally
        {
            bmp.UnlockBits(veri);
        }
    }

    /// <summary>Kendi pencerelerimizi görüntüde siyaha boyar (tooltip sanılmasınlar, özeti oynatmasınlar).</summary>
    public static void Maskele(Bitmap bmp, Rectangle ekranAlani, IEnumerable<Rectangle> bizimPencereler)
    {
        Graphics? g = null;
        try
        {
            foreach (var p in bizimPencereler)
            {
                var k = Rectangle.Intersect(p, ekranAlani);
                if (k.IsEmpty) continue;
                g ??= Graphics.FromImage(bmp);
                g.FillRectangle(Brushes.Black, k.X - ekranAlani.X, k.Y - ekranAlani.Y, k.Width, k.Height);
            }
        }
        finally
        {
            g?.Dispose();
        }
    }

    /// <summary>32×24 gri özet. kucuk (32×24, 32bpp) ve tampon (32*24*4 bayt) çağıranda bir kez ayrılır, yeniden kullanılır.</summary>
    public static void Ozet(Bitmap kaynak, Rectangle kaynakAlan, Bitmap kucuk, byte[] tampon, byte[] hedef)
    {
        using (var g = Graphics.FromImage(kucuk))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(kaynak, new Rectangle(0, 0, OzetG, OzetY), kaynakAlan, GraphicsUnit.Pixel);
        }
        var veri = kucuk.LockBits(new Rectangle(0, 0, OzetG, OzetY), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < OzetY; y++)
                System.Runtime.InteropServices.Marshal.Copy(veri.Scan0 + y * veri.Stride, tampon, y * OzetG * 4, OzetG * 4);
        }
        finally
        {
            kucuk.UnlockBits(veri);
        }
        for (int i = 0; i < OzetG * OzetY; i++)
        {
            int j = i * 4; // BGRA
            hedef[i] = (byte)((tampon[j] * 29 + tampon[j + 1] * 150 + tampon[j + 2] * 77) >> 8);
        }
    }

    /// <summary>
    /// Farkı esik'ten büyük hücre sayısı. Küçük bir kutu (alanın ~%7'si) ortalamayı neredeyse oynatmaz;
    /// "yeni bir şey açıldı mı" sorusu için ortalama yerine bu kullanılır.
    /// </summary>
    public static int DegisenHucre(byte[] a, byte[] b, int esik)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > esik) n++;
        return n;
    }

    /// <summary>Ortalama mutlak fark (0..255).</summary>
    public static int Fark(byte[] a, byte[] b)
    {
        long t = 0;
        for (int i = 0; i < a.Length; i++) t += Math.Abs(a[i] - b[i]);
        return (int)(t / a.Length);
    }
}
