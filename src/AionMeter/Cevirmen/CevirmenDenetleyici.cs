using System.Diagnostics;
using System.Drawing.Imaging;

namespace AionMeter.Cevirmen;

/// <summary>
/// Çevirmen'in arayüz tarafı.
/// 1) Ctrl+Alt+T: ekranı o anda dondur → bölge seç → OCR → paragraflar → çevir → sonuç paneli (odak almaz).
/// 2) Alt ile açıklama: oyun öndeyken, Alt basılı, imleç bizim pencerelerin dışında ve ≥ 400 ms duruyorsa
///    imleç çevresini yakalar; içerik değiştiyse açıklama kutusunu bulur, sadece onu okuyup çevirir.
///    Alt bırakılınca / imleç 24 px'ten fazla kayınca gizler ve işi iptal eder. Alt basılı değilken hiç iş yapmaz.
/// Alt yoklaması overlay'in 50 ms'lik zamanlayıcısından gelir (AltDurumu); kanca yok.
/// Model indirme penceresi sadece Ctrl+Alt+T ilk kullanımında ya da tepsi menüsünden açılır, oyun sırasında asla.
/// </summary>
internal sealed class CevirmenDenetleyici : IDisposable
{
    private const int DurmaMs = 400;          // imleç bu kadar durunca bakılır
    private const int DurmaToleransi = 3;     // px: bu kadar kıpırdama "durma" sayılır
    private const int HareketEsigi = 24;      // px: panel/iş bundan fazla kayınca kapanır
    private const int OzetEsigi = 4;          // 0..255: imleç çevresi "aynı" sayılır
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

    // Alt ile açıklama
    private CeviriPaneli? _aciklamaPaneli;
    private CancellationTokenSource? _aciklamaIptal;
    private bool _altAktif, _buDurusIslendi, _isleniyor, _sonOzetVar, _sonDurustaKutuVardi;
    private Point _durma, _tetik;
    private long _durmaBas;
    private int _sayac;
    private Rectangle _kutuEkran;
    private readonly Bitmap _kucuk = new(EkranAraclari.OzetG, EkranAraclari.OzetY, PixelFormat.Format32bppArgb);
    private readonly byte[] _tampon = new byte[EkranAraclari.OzetG * EkranAraclari.OzetY * 4];
    private readonly byte[] _ozet = new byte[EkranAraclari.OzetG * EkranAraclari.OzetY];
    private readonly byte[] _sonOzet = new byte[EkranAraclari.OzetG * EkranAraclari.OzetY];
    private readonly byte[] _kutuOzet = new byte[EkranAraclari.OzetG * EkranAraclari.OzetY];

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

    public void ModeliIndir()
    {
        if (ModelHazir)
        {
            _bildir("Çeviri modeli zaten hazır.");
            return;
        }
        if (ModelIndirmeFormu.Sor(Motor, Native.GetForegroundWindow()))
        {
            _modelHazir = true;
            _bildir("Çeviri modeli hazır. Ctrl+Alt+T ile bölge çevirebilir, oyunda Alt basılıyken açıklamaların üstüne gelebilirsin.");
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
                if (!ModelIndirmeFormu.Sor(Motor, onceki)) return;
                _modelHazir = true;
                DurumDegisti?.Invoke();
                // Pencere kapandıktan hemen sonra yakalanan ekranda hâlâ görünüyordu (DWM henüz çizmedi): kısa bekle.
                await Task.Delay(250);
            }

            Rectangle kaynak;
            // Ekranı ŞİMDİ dondur: seçim penceresinin karartması görüntüye girmesin.
            var sanal = SystemInformation.VirtualScreen;
            using (var donmus = EkranAraclari.Yakala(sanal))
            {
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
    /// Overlay'in Alt zamanlayıcısından (50 ms) çağrılır. aktif = oyun önde + Alt basılı + imleç bizim pencerelerin dışında.
    /// aktif değilken hiçbir iş yapılmaz (açık panel/iş varsa kapatılır).
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
                return;
            }

            if (_buDurusIslendi)
            {
                if (!_isleniyor && _aciklamaPaneli is { Visible: true } && ++_sayac % KaybolmaKontrolTik == 0) KutuKayboldumu();
                return;
            }
            if (simdi - _durmaBas < DurmaMs) return;
            _buDurusIslendi = true; // bir duruşta en fazla bir kez
            _ = DurusuIsleAsync(imlec);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama izleme", ex);
        }
    }

    private static int Uzaklik(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private IEnumerable<Rectangle> TumBizimPencereler()
    {
        foreach (var r in _overlayPencereleri()) yield return r;
        if (_sonucPaneli is { Visible: true }) yield return _sonucPaneli.Bounds;
        if (_aciklamaPaneli is { Visible: true }) yield return _aciklamaPaneli.Bounds;
    }

    private async Task DurusuIsleAsync(Point imlec)
    {
        _aciklamaIptal?.Cancel();
        _aciklamaIptal?.Dispose();
        var iptal = new CancellationTokenSource();
        _aciklamaIptal = iptal;
        _isleniyor = true;
        _tetik = imlec;
        Bitmap? kirpim = null, parca = null;
        try
        {
            var ekran = Screen.FromPoint(imlec).Bounds;
            var alan = Rectangle.Intersect(new Rectangle(imlec.X - 450, imlec.Y - 350, 900, 700), ekran);
            if (alan.Width < 60 || alan.Height < 40) return;

            kirpim = EkranAraclari.Yakala(alan);
            EkranAraclari.Maskele(kirpim, alan, TumBizimPencereler()); // kendi pencerelerimiz kutu sanılmasın
            EkranAraclari.Ozet(kirpim, new Rectangle(0, 0, kirpim.Width, kirpim.Height), _kucuk, _tampon, _ozet);
            bool ayni = _sonOzetVar && EkranAraclari.Fark(_ozet, _sonOzet) < OzetEsigi;
            Array.Copy(_ozet, _sonOzet, _ozet.Length);
            _sonOzetVar = true;
            // Yeni bir kutu açılmadıysa (görüntü aynı) ve: zaten çevrili gösteriliyor ya da geçen sefer kutu yoktu → iş yok.
            if (ayni && (_aciklamaPaneli is { Visible: true } || !_sonDurustaKutuVardi)) return;

            var bulunan = Motor.TooltipBul(kirpim, new Point(imlec.X - alan.X, imlec.Y - alan.Y));
            _sonDurustaKutuVardi = bulunan.HasValue;
            if (bulunan is not Rectangle r) return;
            r.Intersect(new Rectangle(0, 0, kirpim.Width, kirpim.Height));
            if (r.Width < 40 || r.Height < 16) return;
            var kutuEkran = new Rectangle(r.X + alan.X, r.Y + alan.Y, r.Width, r.Height);
            foreach (var b in _overlayPencereleri())
                if (b.IntersectsWith(kutuEkran)) return;

            parca = kirpim.Clone(r, PixelFormat.Format32bppPArgb);
            _kutuEkran = kutuEkran;
            EkranAraclari.Ozet(kirpim, r, _kucuk, _tampon, _kutuOzet);
            kirpim.Dispose();
            kirpim = null;
            if (iptal.IsCancellationRequested) return;

            var panel = _aciklamaPaneli ??= new CeviriPaneli(kucuk: true, _olcek());
            panel.Durum("Çevriliyor…", kutuEkran, yukleniyor: true);
            var bmp = parca;
            parca = null; // sahiplik CevirVeGosterAsync'te
            await CevirVeGosterAsync(bmp, panel, kutuEkran, iptal.Token);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama çevirisi", ex);
        }
        finally
        {
            kirpim?.Dispose();
            parca?.Dispose();
            if (ReferenceEquals(_aciklamaIptal, iptal)) _isleniyor = false;
        }
    }

    /// <summary>Panel açıkken saniyede bir: açıklama kutusu kaybolduysa paneli kapat.</summary>
    private void KutuKayboldumu()
    {
        if (_kutuEkran.IsEmpty) return;
        try
        {
            using var bmp = EkranAraclari.Yakala(_kutuEkran);
            EkranAraclari.Maskele(bmp, _kutuEkran, TumBizimPencereler());
            EkranAraclari.Ozet(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), _kucuk, _tampon, _ozet);
            if (EkranAraclari.Fark(_ozet, _kutuOzet) > KaybolmaEsigi) AciklamayiKapat();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Açıklama kutusu kontrolü", ex);
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
        _bolgeIptal?.Cancel();
        _aciklamaIptal?.Cancel();
        _aciklamaIptal?.Dispose();
        SonucuKapat();
        _aciklamaPaneli?.Dispose();
        _aciklamaPaneli = null;
        _motor?.Dispose(); // çevirmen modeli bellekten bırakılır
        _motor = null;
        _kucuk.Dispose();
    }
}
