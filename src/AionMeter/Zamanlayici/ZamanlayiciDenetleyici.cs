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

    /// <summary>Geri sayım panelinde en çok bu kadar satır (en yakın başlangıçlar).</summary>
    internal const int EnFazlaGeriSayim = 6;

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
    private readonly List<(DateTime Utc, GeriSayimSatiri Satir)> _gsSira = new();
    private readonly List<SahaBossDurumu> _saha = new();

    /// <summary>Boss listesinde hiç saha bossu bilgisi yokken altta görünen ipucu.</summary>
    internal const string SahaIpucu = "Saha bossları için oyunda haritayı (M) açıp saha bossu listesine göz at";

    /// <summary>
    /// Saha bosslarının bilinen durumu (paket çözücüden; <see cref="SahaBossDeposu.Oku"/>): verilen listeyi temizleyip doldurur.
    /// Ölülerin geri gelişi takvim bosslarıyla birlikte sıralanır ve 10 dk kala geri sayıma girer; canlılar listenin sonunda.
    /// </summary>
    public Action<DateTime, List<SahaBossDurumu>> SahaBosslari { get; set; } = static (_, hedef) => hedef.Clear();

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
        try
        {
            SahaBosslari(utc, _saha);
            // Reshanta bossları (harita 20/22, yer kimliği 2001..2205) takvimde zaten var: aynı boss iki satır olmasın.
            _saha.RemoveAll(static b => b.Yer < 10_000);
        }
        catch (Exception ex) { HataLog.Yaz("Saha bossları", ex); _saha.Clear(); }

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
            BossSatirlariKur(_takvim, utc, _saha, _bossSira, _bossSatirlar);
            bool degisti = _boss.Guncelle(_bossSatirlar, EtkinlikTakvimi.BolgeAdlari[(int)_takvim.Bolge], _saha.Count == 0 ? SahaIpucu : null);
            if (degisti || olcekDegisti || _bossYerles || !_boss.Visible) BossYerlestir();
            _boss.Goster();
        }
    }

    // ---------------- Geri sayım satırları ----------------

    /// <summary>Gösterilecek satır varsa true. Kilitsizken boşsa sıradaki etkinlik soluk gösterilir (paneli yerleştirebilmek için).</summary>
    private bool GeriSayimSatirlari(DateTime utc, bool kilitli) =>
        GeriSayimSatirlariKur(_takvim!, utc, kilitli, _saha, _olaylar, _gsSira, _gsSatirlar);

    /// <summary>
    /// Takvim + saha bosslarının geri gelişi (10 dk kala; doğunca 60 sn "şimdi"), en yakın üstte. Pencere açmadan da çağrılabilir
    /// (ekran dışı çizim denemesi). <paramref name="tampon"/> ve <paramref name="sira"/> çalışma listeleri.
    /// </summary>
    internal static bool GeriSayimSatirlariKur(EtkinlikTakvimi takvim, DateTime utc, bool kilitli, IReadOnlyList<SahaBossDurumu> saha,
                                               List<TakvimOlayi> tampon, List<(DateTime Utc, GeriSayimSatiri Satir)> sira, List<GeriSayimSatiri> hedef)
    {
        takvim.Yaklasanlar(utc, Pencere, tampon);
        sira.Clear();
        foreach (var o in tampon) sira.Add((o.BaslangicUtc, Satir(o, utc)));
        foreach (var s in saha)
        {
            if (s.Canli || s.ZamanUtc == DateTime.MinValue || SahaBossDeposu.Belirsiz(s, utc)) continue;
            var kalan = s.ZamanUtc - utc;
            if (kalan > Pencere || -kalan >= EtkinlikTakvimi.BossSimdiSuresi) continue;
            GeriSayimSatiri satir;
            if (kalan > TimeSpan.Zero)
            {
                var k = Yukari(kalan);
                satir = new GeriSayimSatiri(s.GorunenAd, TakvimTuru.Boss, Bicim.Kalan(k), null,
                    k <= TimeSpan.FromMinutes(1) ? Palet.Yerel : Palet.Dps, Rakam: true, Soluk: false);
            }
            else satir = new GeriSayimSatiri(s.GorunenAd, TakvimTuru.Boss, "şimdi", null, Palet.Kirmizi, Rakam: false, Soluk: false);
            if (!Var(sira, satir)) sira.Add((s.ZamanUtc, satir)); // aynı haritadaki eş adlılar aynı anda gelirse tek satır
        }
        KararliSirala(sira);
        hedef.Clear();
        foreach (var (_, s) in sira)
        {
            if (hedef.Count == EnFazlaGeriSayim) break; // en yakın başlangıçlar kalır; panel ekranı kaplamasın
            hedef.Add(s);
        }
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

    /// <summary>
    /// Takvim + saha bossları, en yakın doğuş üstte. Saha bossu ölüyse geri gelişine kalan süre (doğunca 60 sn "şimdi"); canlıysa ya da
    /// geri gelme anı geçtiyse yeşil "canlı", listenin sonunda (yer kimliği sırasıyla). Bilgi belirsizleştiyse (<see cref="SahaBossDeposu.Belirsiz"/>:
    /// 60 dk'dan eski "canlı" ya da geri gelişinin üstünden 60 dk geçmiş) soluk "canlı?", en sonda. Gizlenmez: boss kaybolursa kullanıcı
    /// nereye gittiğini merak eder; soluk soru işareti "muhtemelen canlı, haritadan bak" der. Pencere açmadan da çağrılabilir.
    /// </summary>
    internal static void BossSatirlariKur(EtkinlikTakvimi takvim, DateTime utc, IReadOnlyList<SahaBossDurumu> saha,
                                          List<(DateTime Utc, BossSatiri Satir)> tampon, List<BossSatiri> hedef)
    {
        tampon.Clear();
        foreach (var k in takvim.Kalemler)
        {
            if (k.Tur != TakvimTuru.Boss) continue;
            var o = takvim.Guncel(k, utc);
            tampon.Add((o.BaslangicUtc, BossSatir(k.Ad, o.BaslangicUtc, utc)));
        }
        foreach (var s in saha)
        {
            DateTime sira;
            BossSatiri satir;
            if (SahaBossDeposu.Belirsiz(s, utc))
            {
                sira = DateTime.MaxValue; // en sonda
                satir = new BossSatiri(s.GorunenAd, "canlı?", Palet.CokSoluk, Rakam: false, Soluk: true);
            }
            else if (!s.Canli && s.ZamanUtc != DateTime.MinValue && utc - s.ZamanUtc < EtkinlikTakvimi.BossSimdiSuresi)
            {
                sira = s.ZamanUtc;
                satir = BossSatir(s.GorunenAd, s.ZamanUtc, utc);
            }
            else
            {
                sira = DateTime.MaxValue.AddTicks(-1); // takvimden sonra, belirsizlerden önce
                satir = new BossSatiri(s.GorunenAd, "canlı", Palet.Yesil, Rakam: false);
            }
            if (!Var(tampon, satir)) tampon.Add((sira, satir)); // aynı haritadaki eş adlılar aynı durumdaysa tek satır
        }
        KararliSirala(tampon);
        hedef.Clear();
        foreach (var (_, s) in tampon) hedef.Add(s);
    }

    private static bool Var<T>(List<(DateTime Utc, T Satir)> liste, T satir) where T : IEquatable<T>
    {
        foreach (var (_, s) in liste)
            if (s.Equals(satir)) return true;
        return false;
    }

    /// <summary>Eklenme sırasını koruyan sıralama (eşit saatte takvim sırası); liste küçük, ayırma yok.</summary>
    private static void KararliSirala<T>(List<(DateTime Utc, T Satir)> liste)
    {
        for (int i = 1; i < liste.Count; i++)
        {
            var x = liste[i];
            int j = i - 1;
            while (j >= 0 && liste[j].Utc > x.Utc)
            {
                liste[j + 1] = liste[j];
                j--;
            }
            liste[j + 1] = x;
        }
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
            // Eski sürümün kaydettiği kenar dışı nokta: en yakın ekrana kenetle (ekran kaldırıldıysa da makul yer)
            var y = Screen.FromPoint(p).Bounds;
            return new Point(Math.Clamp(p.X, y.Left, y.Right - 1), Math.Clamp(p.Y, y.Top, y.Bottom - 1));
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
        // Önce ekrana sığdır, sonra kaydet: üst kenardan birkaç px taşan bırakış kaydedilirse Capa onu hiçbir ekranda bulamaz
        // ve panel varsayılan yere sıçrar (denetim bulgusu 1).
        var r = EkranaSigdir(new Rectangle(_geri.Left, _geri.Top, _geri.Width, _geri.Height));
        _ayar.ZamanlayiciX = r.Left + r.Width / 2;
        _ayar.ZamanlayiciY = r.Top;
        _ayar.Kaydet();
        GeriYerlestir(); // ekran dışına bırakıldıysa içeri al
    }

    /// <summary>İlk açılışta geri sayımın altına (yoksa çapaya) ortalanır; sonra kullanıcı taşıyabilir, boyut değişince sol üst sabit.</summary>
    private void BossYerlestir()
    {
        if (_boss is null || _boss.Surukleniyor) return;
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
            // Liste bulunduğu ekranın yüksekliğini aşmasın: fazlası kırpılır, son satırda "+N daha" (en yakın doğuşlar kalır).
            _boss.EnFazlaYukseklik = Screen.FromPoint(new Point(ortaX, ust)).Bounds.Height;
            var boyut = _boss.Boyut();
            r = new Rectangle(ortaX - boyut.Width / 2, ust, boyut.Width, boyut.Height);
        }
        else
        {
            _boss.EnFazlaYukseklik = Screen.FromRectangle(_boss.Bounds).Bounds.Height;
            r = new Rectangle(_boss.Location, _boss.Boyut());
        }
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
