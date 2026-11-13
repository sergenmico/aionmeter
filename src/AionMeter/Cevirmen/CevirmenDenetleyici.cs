using System.Diagnostics;
using System.Drawing.Imaging;

namespace AionMeter.Cevirmen;

/// <summary>
/// Çevirmen'in arayüz tarafı.
/// 1) Ctrl+Alt+T: ekranı o anda dondur → bölge seç → OCR → paragraflar → çevir → sonuç paneli (odak almaz).
/// 2) Alt ile açıklama: oyun öndeyken, Alt basılı, imleç bizim pencerelerin dışında ve ≥ 400 ms duruyorsa
///    imlecin monitörünü yakalar; imlece en yakın açıklama kutusunu (≤ 400 px, bkz. Incele) bulur, sadece onu okuyup çevirir.
///    Alt bırakılınca / imleç 24 px'ten fazla kayınca gizler ve işi iptal eder. Alt basılı değilken hiç iş yapmaz.
/// Alt yoklaması overlay'in 50 ms'lik zamanlayıcısından gelir (AltDurumu); kanca yok.
/// Model indirme penceresi sadece Ctrl+Alt+T ilk kullanımında ya da tepsi menüsünden açılır, oyun sırasında asla.
/// </summary>
internal sealed class CevirmenDenetleyici : IDisposable
{
    private const int DurmaMs = 400;          // imleç bu kadar durunca bakılır
    private const int DurmaToleransi = 3;     // px: bu kadar kıpırdama "durma" sayılır
    private const int HareketEsigi = 24;      // px: panel/iş bundan fazla kayınca kapanır
    private const int HucreEsigi = 16;        // 0..255: bir özet hücresi "değişti" sayılır
    private const int DegisenHucreEnAz = 6;   // 32×24 özette bu kadar hücre değiştiyse yeni bir şey açıldı (900×700'de ~6 hücre ≈ 160×90 px)
    private const int YakinKutuPx = 80;       // imleci içermeyen kutunun en yakın kenarı bu kadar yakınsa (DPI ölçekli) açıklama sayılır
    private const int AciklamaEnFazlaG = 700, AciklamaEnFazlaY = 1000; // uzak kutu bundan büyükse açıklama değil (pencere)
    private const int UzakKutuPx = 400;       // bundan uzak kutulara hiç bakılmaz (DPI ölçekli). Envanterin yanındaki eşya bilgi
                                              // paneli imleçten ~170 px uzakta açılıyor (oyun içi ekran görüntüsüyle ölçüldü)
    private const int TekrarSureMs = 10_000;  // aynı kutu bu süre içinde çevrildiyse ve paneli açıksa tekrar işlenmez
    private const int KaybolmaEsigi = 12;     // 0..255: açıklama kutusu kayboldu sayılır
    private const int KaybolmaKontrolTik = 20; // 50 ms × 20 = 1 sn'de bir

    private readonly Ayarlar _ayar;
    private readonly Func<float> _olcek;
    private readonly Func<IEnumerable<Rectangle>> _overlayPencereleri;
    private readonly Action<string> _bildir;

    private ICeviriMotoru? _motor;
    private bool? _modelHazir;
    private bool _modelYokBildirildi;

    // Bölge çevirisi
    private CeviriPaneli? _sonucPaneli;
    private CancellationTokenSource? _bolgeIptal;
    private bool _bolgeSuruyor;

    // Model indirme (Ctrl+Alt+T ve tepsi aynı anda iki pencere açmasın)
    private bool _modelIndiriliyor;

    // Alt ile açıklama
    private CeviriPaneli? _aciklamaPaneli;
    private CancellationTokenSource? _aciklamaIptal;
    private bool _altAktif, _buDurusIslendi, _isleniyor, _incelemeSuruyor, _kayipKontrolu;
    private bool _siyahBildirildi; // tam ekran uyarısı bu oturumda bir kez (Alt)
    private const string TamEkranUyarisi = "Oyun ekranı okunamadı: oyun tam ekran (exclusive) modunda. Çeviri için oyun " +
        "ayarlarından ekran modunu \"Kenarlıksız pencere\" yap.";
    private OncekiDurus? _oncekiDurus; // bir önceki duruşun küçük gri görüntüsü (uzak kutu kararı)

    /// <summary>Önceki duruş: monitör alanı, 1/4 gri görüntü ve o an maskelenen kendi pencerelerimiz (ekran koordinatı).</summary>
    private sealed record OncekiDurus(Rectangle Alan, KucukGri Goruntu, List<Rectangle> Maske);
    private Point _durma, _tetik;
    private long _durmaBas;
    private int _sayac;
    private Rectangle _kutuEkran;
    private Task<Taban?>? _tabanGorevi;      // bu duruşun başındaki görüntü (arka planda alınır)
    // Son çevrilen kutu (tekrar önleme)
    private Rectangle _sonKutuEkran;
    private readonly byte[] _sonKutuOzet = new byte[EkranAraclari.OzetG * EkranAraclari.OzetY];
    private long _sonCeviriMs = long.MinValue;
    private const int OzetBoyu = EkranAraclari.OzetG * EkranAraclari.OzetY;
    // Açık açıklama kutusunun özeti (UI iş parçacığında yazılır; arka plan işleri kopyasını alır).
    private readonly byte[] _kutuOzet = new byte[OzetBoyu];

    /// <summary>Duruş başındaki görüntü: imlecin monitörünün tamamı (Alan, ekran koordinatı); Ozet sadece Yakin
    /// (imleç çevresi 900×700, görüntü koordinatı) için — değişim algılama hassasiyeti monitör boyutuna bağlı olmasın.</summary>
    private sealed class Taban(Bitmap goruntu, Rectangle alan, Rectangle yakin, byte[] ozet) : IDisposable
    {
        public Bitmap Goruntu { get; } = goruntu;
        public Rectangle Alan { get; } = alan;
        public Rectangle Yakin { get; } = yakin;
        public byte[] Ozet { get; } = ozet;
        public void Dispose() => Goruntu.Dispose();
    }

    /// <summary>Yeni beliren açıklama kutusu: ekran konumu, kırpılmış görüntüsü ve özeti.</summary>
    private sealed record Bulgu(Rectangle KutuEkran, Bitmap Parca, byte[] KutuOzet);

    /// <summary>Arka plan işleri için özet tamponları (her çağrıda küçük, kısa ömürlü).</summary>
    private sealed class OzetAraci : IDisposable
    {
        public readonly Bitmap Kucuk = new(EkranAraclari.OzetG, EkranAraclari.OzetY, PixelFormat.Format32bppArgb);
        public readonly byte[] Tampon = new byte[OzetBoyu * 4];
        public byte[] Ozet(Bitmap b, Rectangle r)
        {
            var o = new byte[OzetBoyu];
            EkranAraclari.Ozet(b, r, Kucuk, Tampon, o);
            return o;
        }
        public void Dispose() => Kucuk.Dispose();
    }

    /// <summary>Tepsi menüsü / Alt zamanlayıcısı yeniden değerlendirilsin.</summary>
    public event Action? DurumDegisti;

    public CevirmenDenetleyici(Ayarlar ayar, Func<float> olcek,
        Func<IEnumerable<Rectangle>> overlayPencereleri, Action<string> bildir)
    {
        _ayar = ayar;
        _olcek = olcek;
        _overlayPencereleri = overlayPencereleri;
        _bildir = bildir;
    }

    /// <summary>Motor tembel oluşturulur; çevirmenin kendisi ilk çeviride yüklenir.</summary>
    private ICeviriMotoru Motor
    {
        get
        {
            if (_motor is null)
            {
                _motor = CeviriMotoruFabrikasi.Olustur();
                _motor.DusukRamAyarla(_ayar.CevirmenDusukRam);
            }
            return _motor;
        }
    }

    public bool ModelHazir
    {
        get
        {
            if (_modelHazir is bool b) return b;
            try { _modelHazir = Motor.ModelHazir; }
            catch (Exception ex) { HataLog.Yaz("Çeviri modeli kontrolü", ex); _modelHazir = false; }
            return _modelHazir.Value;
        }
    }

    /// <summary>Alt ile açıklama çevirisi şu an çalışabilir mi (ayar açık + model hazır)?</summary>
    public bool AltCeviriCalisir => _ayar.AltCeviri && ModelHazir;

    // ---------------- Tepsi komutları ----------------

    public void AltCeviriDegistir()
    {
        _ayar.AltCeviri = !_ayar.AltCeviri;
        _ayar.Kaydet();
        if (!_ayar.AltCeviri) AciklamayiKapat();
        DurumDegisti?.Invoke();
    }

    /// <summary>Tepsi: "Düşük RAM modu (çevirmen)". Yüklü model bırakılır, sonraki kullanımda yeni modda yüklenir.</summary>
    public void DusukRamDegistir()
    {
        _ayar.CevirmenDusukRam = !_ayar.CevirmenDusukRam;
        _ayar.Kaydet();
        _motor?.DusukRamAyarla(_ayar.CevirmenDusukRam);
        DurumDegisti?.Invoke();
    }

    /// <summary>Tepsi menüsü için: model penceresi açık/indiriyor.</summary>
    public bool ModelIndiriliyor => _modelIndiriliyor;

    public void ModeliIndir()
    {
        if (_modelIndiriliyor) return; // ikinci pencere açılmasın
        if (ModelHazir)
        {
            _bildir("Çeviri modeli zaten hazır.");
            return;
        }
        if (ModeliSor(Native.GetForegroundWindow(), out _))
            _bildir("Çeviri modeli hazır. Ctrl+Alt+T ile bölge çevirebilir, oyunda Alt basılıyken açıklamaların üstüne gelebilirsin.");
    }

    /// <summary>Tek giriş noktası: aynı anda tek model penceresi. true = model hazır.</summary>
    private bool ModeliSor(IntPtr onceki, out bool altaAlindi)
    {
        altaAlindi = false;
        if (_modelIndiriliyor) return false;
        _modelIndiriliyor = true;
        DurumDegisti?.Invoke(); // tepsi: "Çeviri modeli indiriliyor…" (pasif)
        try
        {
            if (!ModelIndirmeFormu.Sor(Motor, onceki, out altaAlindi)) return false;
            _modelHazir = true;
            return true;
        }
        finally
        {
            _modelIndiriliyor = false;
            DurumDegisti?.Invoke();
        }
    }

    // ---------------- 1) Ctrl+Alt+T: bölge çevirisi ----------------

    public async void BolgeCevir()
    {
        if (_sonucPaneli is not null) // ikinci Ctrl+Alt+T: kapat (sürüyorsa iptal)
        {
            SonucuKapat();
            return;
        }
        if (_bolgeSuruyor) return;
        _bolgeSuruyor = true;
        AciklamayiKapat();
        IntPtr onceki = Native.GetForegroundWindow();
        Bitmap? secilen = null;
        try
        {
            if (!ModelHazir)
            {
                if (!ModeliSor(onceki, out bool altaAlindi)) return;
                if (altaAlindi)
                {
                    // Kullanıcı indirme sırasında başka işe geçti: ekranı aniden dondurma, sadece haber ver.
                    _bildir("Çeviri modeli hazır. Ctrl+Alt+T ile bölge çevirebilir, oyunda Alt basılıyken açıklamaların üstüne gelebilirsin.");
                    return;
                }
                // Pencere kapandıktan hemen sonra yakalanan ekranda hâlâ görünüyordu (DWM henüz çizmedi): kısa bekle.
                await Task.Delay(250);
            }

            Rectangle kaynak;
            // Ekranı ŞİMDİ dondur: seçim penceresinin karartması görüntüye girmesin.
            var sanal = SystemInformation.VirtualScreen;
            using (var donmus = EkranAraclari.Yakala(sanal))
            {
                var monitor = Screen.FromPoint(Cursor.Position).Bounds;
                monitor.Offset(-sanal.X, -sanal.Y);
                if (EkranAraclari.Siyah(donmus, monitor))
                {
                    _bildir(TamEkranUyarisi);
                    return;
                }
                using var secim = new BolgeSecimFormu(donmus, sanal, _olcek());
                var sonuc = secim.ShowDialog();
                OdakYardimcisi.GeriVer(onceki); // odağı oyuna geri ver
                if (sonuc != DialogResult.OK) return;
                kaynak = secim.SecimEkran;
                secilen = donmus.Clone(secim.SecimGoruntu, PixelFormat.Format32bppPArgb);
            } // tam ekran görüntü burada bırakılır

            var iptal = new CancellationTokenSource();
            _bolgeIptal = iptal;
            var panel = new CeviriPaneli(kucuk: false, _olcek());
            panel.KapatIstendi += SonucuKapat;
            _sonucPaneli = panel;
            panel.Durum("Çevriliyor…", kaynak, yukleniyor: true);
            var bmp = secilen;
            secilen = null; // sahiplik CevirVeGosterAsync'te
            await CevirVeGosterAsync(bmp, panel, kaynak, iptal.Token);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Bölge çevirisi", ex);
        }
        finally
        {
            secilen?.Dispose();
            _bolgeSuruyor = false;
        }
    }

    /// <summary>Oyun özel tam ekrana geçti: açık çeviri pencereleri kapanır (üstünde pencere kalırsa oyun çökebilir).</summary>
    public void PencereleriKapat()
    {
        try
        {
            SonucuKapat();
            AltDurumu(false, Point.Empty);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Çevirmen pencerelerini kapatma", ex);
        }
    }

    private void SonucuKapat()
    {
        _bolgeIptal?.Cancel();
        _bolgeIptal = null;
        var p = _sonucPaneli;
        _sonucPaneli = null;
        if (p is null) return;
        p.KapatIstendi -= SonucuKapat;
        p.Close();
        p.Dispose();
    }

    /// <summary>OCR → paragraflar → her paragrafı çevir (panel adım adım dolar). bmp'yi her durumda bırakır.</summary>
    private async Task CevirVeGosterAsync(Bitmap bmp, CeviriPaneli panel, Rectangle kaynak, CancellationToken ct)
    {
        bool Gecerli() => !ct.IsCancellationRequested && !panel.IsDisposed;
        try
        {
            var saat = Stopwatch.StartNew();
            var motor = Motor;
            var satirlar = await Task.Run(() => motor.OkuAsync(bmp, ct), ct);
            var paragraflar = motor.Paragraflar(satirlar);
            if (!Gecerli()) return;
            if (paragraflar.Count == 0)
            {
                panel.Durum("Metin bulunamadı", kaynak);
                return;
            }

            var turkce = new List<string>(paragraflar.Count);
            var terimler = new List<string>();
            bool hepsiOnbellekten = true;
            foreach (string p in paragraflar)
            {
                var c = await Task.Run(() => motor.CevirAsync(p, ct), ct);
                if (!Gecerli()) return;
                turkce.Add(c.Turkce);
                foreach (string t in c.KorunanTerimler)
                {
                    if (terimler.Count >= 12) break;
                    if (!terimler.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) terimler.Add(t);
                }
                hepsiOnbellekten &= c.OnbellektenGeldi;
                if (turkce.Count < paragraflar.Count) panel.Icerik(turkce, terimler, "", yukleniyor: true);
            }
            // Alt bilgi tek satır: "önbellekten" ya da süre ("0,9 sn"); küçük modda sadece "önbellekten".
            string alt = hepsiOnbellekten ? "önbellekten"
                : panel.Kucuk ? "" : saat.Elapsed.TotalSeconds.ToString("0.0", Bicim.Tr) + " sn";
            if (Gecerli()) panel.Icerik(turkce, terimler, alt, yukleniyor: false);
        }
        catch (OperationCanceledException)
        {
            // kapatıldı / imleç kaydı
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Çeviri", ex);
            if (Gecerli()) panel.Durum("Çeviri başarısız", kaynak);
        }
        finally
        {
            bmp.Dispose();
        }
    }

    // ---------------- 2) Alt ile açıklama çevirisi ----------------

    /// <summary>
    /// Overlay'in Alt zamanlayıcısından (50 ms) çağrılır. aktif = oyun önde (her tikte bakılır) + sadece Alt basılı
    /// (Ctrl/Shift/Win/Tab ile değil) + imleç bizim pencerelerin dışında. aktif değilken hiç iş yapılmaz.
    /// Duruşun başında (t0) imleç çevresi arka planda yakalanır; t0+400 ms'de aynı yer tekrar yakalanıp karşılaştırılır:
    /// sadece bu arada YENİ beliren (ya da içeriği değişen) bir kutu varsa OCR/çeviri yapılır. Sohbet, görev listesi,
    /// envanter gibi sabit kutular böylece hiç işlenmez.
    /// </summary>
    public void AltDurumu(bool aktif, Point imlec)
    {
        try
        {
            if (!aktif || _bolgeSuruyor || !_ayar.AltCeviri)
            {
                if (_altAktif)
                {
                    _altAktif = false;
                    TabaniBirak();
                    AciklamayiKapat();
                }
                return;
            }
            if (!ModelHazir)
            {
                // Oyun sırasında indirme penceresi ASLA açılmaz; bir kez tepsi balonu.
                if (!_modelYokBildirildi)
                {
                    _modelYokBildirildi = true;
                    _bildir("Çevirmen için modeli tepsi menüsünden indir (\"Çeviri modelini indir\").");
                }
                return;
            }

            long simdi = Environment.TickCount64;
            if (!_altAktif)
            {
                _altAktif = true;
                _durma = imlec;
                _durmaBas = simdi;
                _buDurusIslendi = false;
            }

            if ((_isleniyor || _aciklamaPaneli is { Visible: true }) && Uzaklik(imlec, _tetik) > HareketEsigi)
                AciklamayiKapat();

            if (Uzaklik(imlec, _durma) > DurmaToleransi)
            {
                // Hareket: sadece konumu not al, iş yok.
                _durma = imlec;
                _durmaBas = simdi;
                _buDurusIslendi = false;
                TabaniBirak();
                return;
            }

            if (_buDurusIslendi)
            {
                if (!_isleniyor && _aciklamaPaneli is { Visible: true } && ++_sayac % KaybolmaKontrolTik == 0) KutuKayboldumu();
                return;
            }
            if (_tabanGorevi is null)
            {
                // t0: duruşun ilk tikinde taban görüntü (arka planda).
                var alan = AlanHesapla(imlec);
                if (alan.IsEmpty)
                {
                    _buDurusIslendi = true;
                    return;
                }
                var maske = MaskeListesi();
                var yakin = YakinHesapla(imlec, alan);
                _tabanGorevi = Task.Run(() => TabanAl(alan, yakin, maske));
                return;
            }
            if (simdi - _durmaBas < DurmaMs) return;
            _buDurusIslendi = true; // bir duruşta en fazla bir kez
            var gorev = _tabanGorevi;
            _tabanGorevi = null;
            _ = DurusuIsleAsync(imlec, gorev);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama izleme", ex);
        }
    }

    private static int Uzaklik(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>
    /// İmlecin monitörünün tamamı: uzun açıklama kutuları (statların altındaki açıklama, set bonusları) imleçten
    /// çok uzağa uzanabilir; küçük bir kesit kutuyu ortadan kesiyordu (açıklamanın altı okunmuyordu).
    /// </summary>
    private static Rectangle AlanHesapla(Point imlec) => Screen.FromPoint(imlec).Bounds;

    /// <summary>Değişim algılamanın baktığı imleç çevresi (900×700), alan görüntüsü koordinatında.</summary>
    private static Rectangle YakinHesapla(Point imlec, Rectangle alan)
    {
        var y = Rectangle.Intersect(new Rectangle(imlec.X - 450, imlec.Y - 350, 900, 700), alan);
        y.Offset(-alan.X, -alan.Y);
        return y.Width < 60 || y.Height < 40 ? new Rectangle(0, 0, alan.Width, alan.Height) : y;
    }

    private IEnumerable<Rectangle> TumBizimPencereler()
    {
        foreach (var r in _overlayPencereleri()) yield return r;
        if (_sonucPaneli is { Visible: true }) yield return _sonucPaneli.Bounds;
        if (_aciklamaPaneli is { Visible: true }) yield return _aciklamaPaneli.Bounds;
    }

    /// <summary>Arka plana verilecek maske (UI iş parçacığında, anlık kopya).</summary>
    private List<Rectangle> MaskeListesi()
    {
        var l = new List<Rectangle>(4);
        foreach (var r in TumBizimPencereler()) l.Add(r);
        return l;
    }

    private void TabaniBirak()
    {
        var g = _tabanGorevi;
        _tabanGorevi = null;
        g?.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>Arka planda: imleç çevresini yakala, kendi pencerelerimizi maskele, özetle.</summary>
    private static Taban? TabanAl(Rectangle alan, Rectangle yakin, List<Rectangle> maske)
    {
        try
        {
            var bmp = EkranAraclari.Yakala(alan);
            EkranAraclari.Maskele(bmp, alan, maske);
            using var arac = new OzetAraci();
            return new Taban(bmp, alan, yakin, arac.Ozet(bmp, yakin));
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama taban görüntüsü", ex);
            return null;
        }
    }

    /// <summary>
    /// Arka planda: t0+400 ms'de aynı yeri (imlecin monitörü) yakalar ve imlece en yakın kutuyu bulur. Karar önce MEKÂNSAL:
    ///  - kutu imleci İÇERİYORSA → yok (içinde gezinilen panel: sohbet, envanter, görev listesi, harita);
    ///  - imleci içermiyor ve en yakın kenarı ~80 px (DPI ölçekli) içindeyse → açıklama: tabanda zaten olsa bile kabul
    ///    (AION 2'de açıklama kutusu imleç daha gelirken anında açılır, tabana girer);
    ///  - daha uzaktaysa (en çok ~400 px) → duruştan sonra YENİ belirdiyse ya da ÖNCEKİ duruştakinden farklıysa kabul.
    ///    Envanterin yanındaki eşya bilgi paneli böyledir: eşyanın üstüne gelir gelmez açılır (tabanda var) ama her eşyada
    ///    içeriği değişir. Sohbet / görev listesi gibi sabit paneller iki duruş arasında değişmez, atlanır.
    /// simdiKucuk: bu duruşun küçük gri görüntüsü (bir sonraki duruşta "önceki" olur).
    /// </summary>
    private static Bulgu? Incele(ICeviriMotoru motor, Taban taban, Point imlec, List<Rectangle> maske, float olcek,
        OncekiDurus? onceki, Rectangle sonKutuEkran, bool panelKapali, out KucukGri? simdiKucuk)
    {
        simdiKucuk = null;
        var alan = taban.Alan;
        using var simdi = EkranAraclari.Yakala(alan);
        EkranAraclari.Maskele(simdi, alan, maske);
        simdiKucuk = EkranAraclari.KucukGriAl(simdi);
        using var arac = new OzetAraci();
        var tam = new Rectangle(0, 0, simdi.Width, simdi.Height);

        var imlecGoreli = new Point(imlec.X - alan.X, imlec.Y - alan.Y);
        var sonKutu = sonKutuEkran.IsEmpty ? Rectangle.Empty : new Rectangle(sonKutuEkran.X - alan.X, sonKutuEkran.Y - alan.Y,
            sonKutuEkran.Width, sonKutuEkran.Height);
        // Maskelenmiş yerler (bizim pencerelerimiz, bu ve önceki duruşta) "önceki duruştan farklı" sayılmasın.
        var haric = new List<Rectangle>();
        foreach (var m in maske) haric.Add(new Rectangle(m.X - alan.X, m.Y - alan.Y, m.Width, m.Height));
        if (onceki is { } o0) foreach (var m in o0.Maske) haric.Add(new Rectangle(m.X - alan.X, m.Y - alan.Y, m.Width, m.Height));
        bool yakinDegisti = EkranAraclari.DegisenHucre(arac.Ozet(simdi, taban.Yakin), taban.Ozet, HucreEsigi) >= DegisenHucreEnAz;
        int enBuyukG = (int)(AciklamaEnFazlaG * olcek), enBuyukY = (int)(AciklamaEnFazlaY * olcek);

        // Adaylar en iyiden kötüye: ilk kabul edilen kazanır (imleci içeren çanta penceresi uzaktaki bilgi panelini gizlemesin).
        foreach (var aday in motor.TooltipAdaylari(simdi, imlecGoreli, (int)(UzakKutuPx * olcek)))
        {
            var r = Rectangle.Intersect(aday, tam);
            if (r.Width < 40 || r.Height < 16) continue;
            if ((long)r.Width * r.Height > 0.35 * tam.Width * tam.Height) continue; // ekranın büyük kısmı: açıklama kutusu değil
            if (r.Contains(imlecGoreli)) continue; // imleç kutunun içinde: açıklama değil, içinde gezinilen panel

            if (KenarUzakligi(r, imlecGoreli) > YakinKutuPx * olcek)
            {
                // Uzak kutu: açıklama boyutunda olmalı ve şunlardan biri:
                //  (c) son çevrilen kutu bu ve paneli kapandı (imleç aynı eşyada biraz kaydı) → tekrar göster (önbellekten);
                //  (a) önceki duruştan bu yana içeriği değişti / yeni açıldı (envanterde eşyadan eşyaya geçiş);
                //  (b) bu duruşta yeni belirdi.
                if (r.Width > enBuyukG || r.Height > enBuyukY) continue;
                bool kabul = panelKapali && !sonKutu.IsEmpty && KesisimOrani(sonKutu, r) > 0.8f;
                if (!kabul && onceki is { } o && o.Alan == alan) kabul = simdiKucuk.Farkli(o.Goruntu, r, haric);
                if (!kabul) kabul = yakinDegisti && EkranAraclari.Fark(arac.Ozet(taban.Goruntu, r), arac.Ozet(simdi, r)) >= KaybolmaEsigi;
                if (!kabul) continue;
            }

            var parca = simdi.Clone(r, simdi.PixelFormat);
            return new Bulgu(new Rectangle(r.X + alan.X, r.Y + alan.Y, r.Width, r.Height), parca, arac.Ozet(simdi, r));
        }
        return null;
    }

    /// <summary>Noktanın dikdörtgenin en yakın kenarına uzaklığı (içerideyse 0).</summary>
    private static float KenarUzakligi(Rectangle r, Point p)
    {
        int dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - (r.Right - 1));
        int dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - (r.Bottom - 1));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float KesisimOrani(Rectangle a, Rectangle b)
    {
        var k = Rectangle.Intersect(a, b);
        if (k.IsEmpty) return 0f;
        long ka = (long)k.Width * k.Height;
        long bi = (long)a.Width * a.Height + (long)b.Width * b.Height - ka;
        return bi <= 0 ? 0f : (float)ka / bi;
    }

    private async Task DurusuIsleAsync(Point imlec, Task<Taban?> tabanGorevi)
    {
        if (_incelemeSuruyor)
        {
            TabaniBirakGorev(tabanGorevi);
            return;
        }
        _incelemeSuruyor = true;
        Taban? taban = null;
        Bulgu? bulgu = null;
        try
        {
            taban = await tabanGorevi;
            if (taban is null) return;
            if (EkranAraclari.Siyah(taban.Goruntu, new Rectangle(0, 0, taban.Goruntu.Width, taban.Goruntu.Height)))
            {
                if (!_siyahBildirildi) { _siyahBildirildi = true; _bildir(TamEkranUyarisi); }
                return;
            }
            var motor = Motor;            // UI iş parçacığında oluşturulur
            var maske = MaskeListesi();
            var t = taban;
            float olcek = _olcek();
            var onceki = _oncekiDurus;
            var sonKutu = _sonKutuEkran;
            bool panelKapali = _aciklamaPaneli is not { Visible: true };
            KucukGri? simdiKucuk = null;
            bulgu = await Task.Run(() => Incele(motor, t, imlec, maske, olcek, onceki, sonKutu, panelKapali, out simdiKucuk)); // arka planda
            if (simdiKucuk is not null) _oncekiDurus = new OncekiDurus(t.Alan, simdiKucuk, maske);
            taban.Dispose(); // tam ekran görüntü: OCR/çeviri boyunca tutulmasın
            taban = null;
            _incelemeSuruyor = false;

            // Bu arada imleç kaydıysa ya da Alt bırakıldıysa sonuç bayat.
            if (bulgu is null || !_altAktif || Uzaklik(Cursor.Position, imlec) > HareketEsigi) return;
            foreach (var p in _overlayPencereleri())
                if (p.IntersectsWith(bulgu.KutuEkran)) return;

            // Tekrar önleme: aynı kutu (örtüşme > 0,8, içerik aynı) son 10 sn'de çevrildi ve paneli hâlâ açık → iş yok.
            if (_aciklamaPaneli is { Visible: true } && Environment.TickCount64 - _sonCeviriMs < TekrarSureMs &&
                KesisimOrani(_sonKutuEkran, bulgu.KutuEkran) > 0.8f &&
                EkranAraclari.Fark(_sonKutuOzet, bulgu.KutuOzet) < KaybolmaEsigi)
            {
                _tetik = imlec; // panel bu duruşa aittir: kayma eşiği buradan ölçülsün
                return;
            }

            // Yeni kutu KESİN: önceki işi ancak şimdi iptal et (aksi halde panel "Çevriliyor…"da kalırdı).
            _aciklamaIptal?.Cancel();
            _aciklamaIptal?.Dispose();
            var iptal = new CancellationTokenSource();
            _aciklamaIptal = iptal;
            _isleniyor = true;
            _tetik = imlec;
            _kutuEkran = bulgu.KutuEkran;
            Array.Copy(bulgu.KutuOzet, _kutuOzet, OzetBoyu);
            _sonKutuEkran = bulgu.KutuEkran;
            Array.Copy(bulgu.KutuOzet, _sonKutuOzet, OzetBoyu);
            _sonCeviriMs = Environment.TickCount64;
            var panel = _aciklamaPaneli ??= new CeviriPaneli(kucuk: true, _olcek());
            panel.Durum("Çevriliyor…", _kutuEkran, yukleniyor: true);
            var bmp = bulgu.Parca;
            bulgu = null; // sahiplik CevirVeGosterAsync'te
            try
            {
                await CevirVeGosterAsync(bmp, panel, _kutuEkran, iptal.Token);
            }
            finally
            {
                if (ReferenceEquals(_aciklamaIptal, iptal)) _isleniyor = false;
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama çevirisi", ex);
        }
        finally
        {
            _incelemeSuruyor = false;
            taban?.Dispose();
            bulgu?.Parca.Dispose();
            YetimYuklemeyiGizle();
        }
    }

    private static void TabaniBirakGorev(Task<Taban?> g) =>
        g.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
        }, TaskScheduler.Default);

    /// <summary>Panel "Çevriliyor…" durumunda ama ona ait canlı iş yoksa gizle (asılı kalmasın).</summary>
    private void YetimYuklemeyiGizle()
    {
        if (!_isleniyor && _aciklamaPaneli is { Visible: true, Yukleniyor: true }) _aciklamaPaneli.Hide();
    }

    /// <summary>Panel açıkken saniyede bir: açıklama kutusu kaybolduysa paneli kapat.</summary>
    /// <remarks>Yakalama arka planda (UI iş parçacığı takılmasın); karar UI'da, kutu o arada değişmediyse.</remarks>
    private async void KutuKayboldumu()
    {
        if (_kutuEkran.IsEmpty || _kayipKontrolu) return;
        _kayipKontrolu = true;
        try
        {
            var kutu = _kutuEkran;
            var maske = MaskeListesi();
            var oncekiOzet = (byte[])_kutuOzet.Clone();
            bool kayboldu = await Task.Run(() =>
            {
                using var bmp = EkranAraclari.Yakala(kutu);
                EkranAraclari.Maskele(bmp, kutu, maske);
                using var arac = new OzetAraci();
                return EkranAraclari.Fark(arac.Ozet(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)), oncekiOzet) > KaybolmaEsigi;
            });
            if (kayboldu && _kutuEkran == kutu) AciklamayiKapat();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama kutusu kontrolü", ex);
        }
        finally
        {
            _kayipKontrolu = false;
        }
    }

    private void AciklamayiKapat()
    {
        _aciklamaIptal?.Cancel();
        _isleniyor = false;
        _kutuEkran = Rectangle.Empty;
        if (_aciklamaPaneli is { Visible: true }) _aciklamaPaneli.Hide(); // tekrar kullanılır
    }

    public void Dispose()
    {
        // İki kez çağrılabilir: bırakılmış iptal nesnesine dokunulmasın.
        _bolgeIptal?.Cancel();
        _bolgeIptal = null;
        _aciklamaIptal?.Cancel();
        _aciklamaIptal?.Dispose();
        _aciklamaIptal = null;
        TabaniBirak();
        SonucuKapat();
        _aciklamaPaneli?.Dispose();
        _aciklamaPaneli = null;
        _motor?.Dispose(); // çevirmen modeli bellekten bırakılır
        _motor = null;
    }
}
