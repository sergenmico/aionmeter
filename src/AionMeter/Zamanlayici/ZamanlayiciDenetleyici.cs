using AionMeter.Core;

namespace AionMeter.Zamanlayici;

/// <summary>
/// Etkinlik zamanlayıcısının arayüz cephesi. Overlay ~5 Hz'de <see cref="Tick"/> çağırır; bu sınıf takvime bakar, geri sayım
/// panelini (başlamasına 10 dk kalanlar + girişi açık etkinlikler) ve istenince boss listesini gösterir/gizler.
/// <para>Pencereler yalnız gerektiğinde yaratılır (ayar kapalıysa hiç yaratılmaz). <c>gorunebilir=false</c> (oyun özel tam
/// ekranda, oyun önde değil, kullanıcı gizledi) gelince her şey hemen gizlenir. Bölge ve açık/kapalı ayarı her tikte okunur:
/// Ayarlar penceresinde değişince ayrıca bildirim gerekmez. Hepsi UI iş parçacığından çağrılır.</para>
/// </summary>
internal sealed class ZamanlayiciDenetleyici : IDisposable
{
    /// <summary>Geri sayımın başladığı süre.</summary>
    public static readonly TimeSpan Pencere = TimeSpan.FromMinutes(10);

    private readonly Ayarlar _ayar;
    private readonly Func<float> _olcek;
    private readonly Func<IntPtr> _oyunPenceresi;
    private EtkinlikTakvimi? _takvim;
    private GeriSayimPaneli? _geri;
    private BossListesiPaneli? _boss;
    private bool _bossAcik, _bossYerles, _disposed;
    private float _sonOlcek = -1f;

    // Tik başına yeniden kullanılan listeler (ayırma yok denecek kadar az)
    private readonly List<TakvimOlayi> _olaylar = new();
    private readonly List<GeriSayimSatiri> _gsSatirlar = new();
    private readonly List<(DateTime Utc, BossSatiri Satir)> _bossSira = new();
    private readonly List<BossSatiri> _bossSatirlar = new();

    /// <summary>
    /// Saha bossları (ileride paket çözücüden doldurulacak): ad ve bir sonraki doğuş (UTC). Boss listesinde takvim
    /// bosslarıyla birlikte sıralanır; doğuştan 60 sn sonrası listeden düşer. Şimdilik boş.
    /// </summary>
    public Func<IReadOnlyList<(string Ad, DateTime DogusUtc)>> SahaBosslari { get; set; } = () => Array.Empty<(string, DateTime)>();

    public bool BossListesiAcik => _bossAcik;

    /// <param name="olcek">DPI × kullanıcı boyutu (overlay'in ölçeği).</param>
    /// <param name="oyunPenceresi">Oyun penceresi (varsayılan konum için oyunun ekranı); bilinmiyorsa 0.</param>
    public ZamanlayiciDenetleyici(Ayarlar ayar, Func<float> olcek, Func<IntPtr>? oyunPenceresi = null)
    {
        _ayar = ayar;
        _olcek = olcek;
        _oyunPenceresi = oyunPenceresi ?? (() => IntPtr.Zero);
    }

    /// <summary>Görünen zamanlayıcı pencereleri (çevirmen bunları yakalamasın / "bizim pencere" saysın diye).</summary>
    public IEnumerable<Rectangle> Pencereler
    {
        get
        {
            if (_geri is { Visible: true }) yield return _geri.Bounds;
            if (_boss is { Visible: true }) yield return _boss.Bounds;
        }
    }

    /// <summary>Boss listesini açar / kapatır (tepsi menüsü ya da kısayol).</summary>
    public void BossListesiDegistir()
    {
        _bossAcik = !_bossAcik;
        if (_bossAcik) _bossYerles = true; // bir sonraki tikte geri sayımın altına yerleşir
        else _boss?.Gizle();
    }

    /// <summary>~5 Hz. <paramref name="kilitli"/>: overlay kilitliyse geri sayım tıklanmaz; kilitsizse taşınabilir.</summary>
    public void Tick(DateTime utcSimdi, bool gorunebilir, bool kilitli)
    {
        if (_disposed) return;
        try { TickIc(utcSimdi.Kind == DateTimeKind.Local ? utcSimdi.ToUniversalTime() : utcSimdi, gorunebilir, kilitli); }
        catch (Exception ex) { HataLog.Yaz("Zamanlayıcı", ex); }
    }

    private void TickIc(DateTime utc, bool gorunebilir, bool kilitli)
    {
        if (!gorunebilir)
        {
            _geri?.Gizle();
            _boss?.Gizle();
            return;
        }
        if (_takvim is null || _takvim.Bolge != _ayar.Bolge) _takvim = new EtkinlikTakvimi(_ayar.Bolge);
        float olcek = _olcek();
        bool olcekDegisti = olcek != _sonOlcek;
        _sonOlcek = olcek;
        byte alfa = (byte)Math.Clamp(_ayar.Opaklik * 255 / 100, 40, 255);

        // ---- Geri sayım ----
        if (_ayar.Zamanlayici && GeriSayimSatirlari(utc, kilitli))
        {
            if (_geri is null)
            {
                _geri = new GeriSayimPaneli(olcek);
                _geri.Tasindi += GeriTasindi;
            }
            _geri.OlcekAyarla(olcek);
            _geri.AlfaAyarla(alfa);
            _geri.Suruklenebilir = !kilitli;
            _geri.TiklanmazAyarla(kilitli);
            bool degisti = _geri.Guncelle(_gsSatirlar, kilitsiz: !kilitli);
            if (degisti || olcekDegisti || !_geri.Visible) GeriYerlestir();
            _geri.Goster();
        }
        else _geri?.Gizle();

        // ---- Boss listesi ----
        if (_bossAcik)
        {
            if (_boss is null)
            {
                _boss = new BossListesiPaneli(olcek);
                _boss.KapatIstendi += () => { _bossAcik = false; _boss?.Gizle(); };
            }
            _boss.OlcekAyarla(olcek);
            _boss.AlfaAyarla(alfa);
            BossSatirlari(utc);
            bool degisti = _boss.Guncelle(_bossSatirlar, EtkinlikTakvimi.BolgeAdlari[(int)_takvim.Bolge]);
            if (degisti || olcekDegisti || _bossYerles || !_boss.Visible) BossYerlestir();
            _boss.Goster();
        }
    }

    // ---------------- Geri sayım satırları ----------------

    /// <summary>Gösterilecek satır varsa true. Kilitsizken boşsa sıradaki etkinlik soluk gösterilir (paneli yerleştirebilmek için).</summary>
    private bool GeriSayimSatirlari(DateTime utc, bool kilitli) => GeriSayimSatirlariKur(_takvim!, utc, kilitli, _olaylar, _gsSatirlar);

    /// <summary>Pencere açmadan da çağrılabilir (ekran dışı çizim denemesi). <paramref name="tampon"/> takvimin çalışma listesi.</summary>
    internal static bool GeriSayimSatirlariKur(EtkinlikTakvimi takvim, DateTime utc, bool kilitli, List<TakvimOlayi> tampon, List<GeriSayimSatiri> hedef)
    {
        takvim.Yaklasanlar(utc, Pencere, tampon);
        hedef.Clear();
        foreach (var o in tampon) hedef.Add(Satir(o, utc));
        if (hedef.Count == 0 && !kilitli)
        {
            TakvimOlayi? en = null;
            foreach (var k in takvim.Kalemler)
            {
                var o = takvim.Guncel(k, utc);
                if (en is null || o.BaslangicUtc < en.Value.BaslangicUtc) en = o;
            }
            if (en is { } s)
                hedef.Add(new GeriSayimSatiri(s.Ad, s.Tur, Bicim.Kalan(Yukari(s.BaslangicUtc - utc)), null, Palet.Soluk, Rakam: true, Soluk: true));
        }
        return hedef.Count > 0;
    }

    private static GeriSayimSatiri Satir(TakvimOlayi o, DateTime utc)
    {
        if (!o.BasladiMi(utc))
        {
            var kalan = Yukari(o.BaslangicUtc - utc);
            var renk = kalan <= TimeSpan.FromMinutes(1) ? Palet.Yerel : Palet.Dps; // son dakika: sıcak vurgu
            return new GeriSayimSatiri(o.Ad, o.Tur, Bicim.Kalan(kalan), null, renk, Rakam: true, Soluk: false);
        }
        if (o.GirisBitisUtc is DateTime bitis)
            return new GeriSayimSatiri(o.Ad, o.Tur, Bicim.Kalan(Yukari(bitis - utc)), "başladı", Palet.Yesil, Rakam: true, Soluk: false);
        return new GeriSayimSatiri(o.Ad, o.Tur, "şimdi", null, Palet.Kirmizi, Rakam: false, Soluk: false);
    }

    /// <summary>Saniyeye yukarı yuvarlar: 0:01'den sonra doğrudan "başladı" gelir, 0:00 görünmez.</summary>
    private static TimeSpan Yukari(TimeSpan t) =>
        t <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds((t.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);

    // ---------------- Boss satırları ----------------

    private void BossSatirlari(DateTime utc)
    {
        IReadOnlyList<(string Ad, DateTime DogusUtc)> saha;
        try { saha = SahaBosslari(); }
        catch (Exception ex) { HataLog.Yaz("Saha bossları", ex); saha = Array.Empty<(string, DateTime)>(); }
        BossSatirlariKur(_takvim!, utc, saha, _bossSira, _bossSatirlar);
    }

    /// <summary>Takvim + saha bossları, en yakın doğuş üstte. Pencere açmadan da çağrılabilir.</summary>
    internal static void BossSatirlariKur(EtkinlikTakvimi takvim, DateTime utc, IReadOnlyList<(string Ad, DateTime DogusUtc)> saha,
                                          List<(DateTime Utc, BossSatiri Satir)> tampon, List<BossSatiri> hedef)
    {
        tampon.Clear();
        foreach (var k in takvim.Kalemler)
        {
            if (k.Tur != TakvimTuru.Boss) continue;
            var o = takvim.Guncel(k, utc);
            tampon.Add((o.BaslangicUtc, BossSatir(k.Ad, o.BaslangicUtc, utc)));
        }
        foreach (var (ad, dogus) in saha)
        {
            var d = DateTime.SpecifyKind(dogus, DateTimeKind.Utc);
            if (utc - d >= EtkinlikTakvimi.BossSimdiSuresi) continue; // doğuşu geçmiş, bir sonrakini sağlayıcı verir
            tampon.Add((d, BossSatir(ad, d, utc)));
        }
        // Kararlı sıralama (eşit saatte takvim sırası)
        for (int i = 1; i < tampon.Count; i++)
        {
            var x = tampon[i];
            int j = i - 1;
            while (j >= 0 && tampon[j].Utc > x.Utc)
            {
                tampon[j + 1] = tampon[j];
                j--;
            }
            tampon[j + 1] = x;
        }
        hedef.Clear();
        foreach (var (_, s) in tampon) hedef.Add(s);
    }

    private static BossSatiri BossSatir(string ad, DateTime dogus, DateTime utc)
    {
        if (dogus <= utc) return new BossSatiri(ad, "şimdi", Palet.Kirmizi, Rakam: false);
        var kalan = Yukari(dogus - utc);
        if (kalan < TimeSpan.FromHours(1))
            return new BossSatiri(ad, Bicim.Kalan(kalan), kalan <= TimeSpan.FromMinutes(1) ? Palet.Yerel : Palet.Dps, Rakam: true);
        return new BossSatiri(ad, UzunSure(kalan), Palet.Soluk, Rakam: true);
    }

    /// <summary>1 saat ve üstü: "1 sa 41 dk", "2 gün 3 sa" (sıfır kısım yazılmaz).</summary>
    internal static string UzunSure(TimeSpan t)
    {
        var tr = Bicim.Tr;
        if (t.TotalDays >= 1)
        {
            int gun = (int)t.TotalDays;
            return gun.ToString(tr) + " gün" + (t.Hours > 0 ? " " + t.Hours.ToString(tr) + " sa" : "");
        }
        int sa = (int)t.TotalHours;
        return sa.ToString(tr) + " sa" + (t.Minutes > 0 ? " " + t.Minutes.ToString(tr) + " dk" : "");
    }

    // ---------------- Konum ----------------

    /// <summary>Geri sayımın çapası: yatay orta ve üst kenar (fiziksel px). Kayıtlı değilse oyunun ekranının üst ortası.</summary>
    private Point Capa()
    {
        if (_ayar.ZamanlayiciX != int.MinValue && _ayar.ZamanlayiciY != int.MinValue)
        {
            var p = new Point(_ayar.ZamanlayiciX, _ayar.ZamanlayiciY);
            foreach (var e in Screen.AllScreens)
                if (e.Bounds.Contains(p)) return p;
        }
        Screen ekran;
        IntPtr oyun = _oyunPenceresi();
        if (oyun != IntPtr.Zero && Native.IsWindow(oyun)) ekran = Screen.FromHandle(oyun);
        else ekran = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var b = ekran.Bounds; // oyun kenarlıksız tam ekranda görev çubuğunu da kaplar: çalışma alanı değil, ekranın kendisi
        return new Point(b.Left + b.Width / 2, b.Top + (int)MathF.Round(8 * _sonOlcek));
    }

    private void GeriYerlestir()
    {
        if (_geri is null || _geri.Surukleniyor) return;
        var boyut = _geri.Boyut();
        var c = Capa();
        _geri.Yerles(EkranaSigdir(new Rectangle(c.X - boyut.Width / 2, c.Y, boyut.Width, boyut.Height)));
    }

    private void GeriTasindi()
    {
        if (_geri is null) return;
        _ayar.ZamanlayiciX = _geri.Left + _geri.Width / 2;
        _ayar.ZamanlayiciY = _geri.Top;
        _ayar.Kaydet();
        GeriYerlestir(); // ekran dışına bırakıldıysa içeri al
    }

    /// <summary>İlk açılışta geri sayımın altına (yoksa çapaya) ortalanır; sonra kullanıcı taşıyabilir, boyut değişince sol üst sabit.</summary>
    private void BossYerlestir()
    {
        if (_boss is null || _boss.Surukleniyor) return;
        var boyut = _boss.Boyut();
        Rectangle r;
        if (_bossYerles)
        {
            _bossYerles = false;
            int ortaX, ust;
            if (_geri is { Visible: true } g)
            {
                ortaX = g.Left + g.Width / 2;
                ust = g.Bottom + (int)MathF.Round(6 * _sonOlcek);
            }
            else
            {
                var c = Capa();
                (ortaX, ust) = (c.X, c.Y);
            }
            r = new Rectangle(ortaX - boyut.Width / 2, ust, boyut.Width, boyut.Height);
        }
        else r = new Rectangle(_boss.Location, boyut);
        _boss.Yerles(EkranaSigdir(r));
    }

    /// <summary>Pencere bulunduğu ekranın dışına taşmasın.</summary>
    private static Rectangle EkranaSigdir(Rectangle r)
    {
        var e = Screen.FromRectangle(r).Bounds;
        int x = Math.Clamp(r.X, e.Left, Math.Max(e.Left, e.Right - r.Width));
        int y = Math.Clamp(r.Y, e.Top, Math.Max(e.Top, e.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _geri?.Dispose();
        _boss?.Dispose();
        _geri = null;
        _boss = null;
    }
}
