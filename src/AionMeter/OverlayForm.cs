using System.Diagnostics;
using Microsoft.Win32;

namespace AionMeter;

/// <summary>
/// Kenarlıksız, en üstte, yarı saydam, kendisi çizen overlay. Satırlar için alt kontrol yoktur.
/// Kilitliyken fare olaylarını almaz (WS_EX_TRANSPARENT | WS_EX_LAYERED); hiçbir zaman odak çalmaz.
/// Satır: [sınıf ikonu · ad · CP] | [boss/sn · /sn] | [hasar · pay]; pay çubuğu sınıfın koyu tonunda.
/// Boss varken DPS, hasar ve pay aynı hedeften (boss) gelir. Kilitli değilken satıra tıklayınca detay paneli açılır.
/// Savaş dışında (~8 sn sonra) ayara göre soluklaşır / gizlenir; geçiş 50 ms adımlı ~300 ms.
/// Ek: "sadece benim satırım" modu, kullanıcı renkleri/yazı boyutları, kukla DPS testi (boss bloğunun yerinde
/// geri sayım + sonuç kartı) ve alt satırdaki saatten kurulan kişisel alarm.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int MaxSatir = 12;
    private const int UstSayisi = 5; // kalabalıkta (> 5 oyuncu) gösterilen ilk sıra sayısı
    private static readonly string[] SiraMetni = { "1", "2", "3", "4", "5" };
    private const int KisayolKilit = 1, KisayolGoster = 2, KisayolSifirla = 3, KisayolDetayim = 4, KisayolCevir = 5;
    private static readonly TimeSpan SavasSonrasi = TimeSpan.FromSeconds(8);
    private const string BossEtiketi = "boss/sn", DpsEtiketi = "/sn", CpEtiketi = "CP", HasarEtiketi = "hasar", PayEtiketi = "pay";
    private const string SonucEtiketi = "Sonuç", CpSablonu = "8.888.888";

    private readonly IMeterEngine _motor;
    private readonly Ayarlar _ayar;
    private readonly bool _demo;
    private readonly System.Windows.Forms.Timer _zamanlayici;
    private readonly SinifIkonlari _ikonlar = new();
    private KisayolPenceresi? _kisayol;
    private DetayForm? _detay;
    private Tepsi? _tepsi;
    private AyarlarFormu? _ayarlarFormu;

    // Önbelleğe alınmış çizim nesneleri (renkler ortak paletten)
    private Font? _yazi, _yaziYari, _yaziDps, _yaziKucuk, _yaziBaslik, _yaziBoss;
    private readonly SolidBrush[] _fircaKoyu = new SolidBrush[Palet.SinifSirasi.Length];
    private readonly SolidBrush[] _fircaAcik = new SolidBrush[Palet.SinifSirasi.Length];
    private readonly SolidBrush _fircaYerel = new(Palet.YerelRenk);
    private readonly SolidBrush _fircaSecili = new(Palet.Secili);
    private readonly SolidBrush _fircaSabitSatir = new(Palet.SabitSatir);
    private readonly SolidBrush _fircaBossIz = new(Palet.BossIz);
    private readonly SolidBrush _fircaBossCan = new(Palet.BossCan);
    private readonly SolidBrush _fircaBossBilinmiyor = new(Palet.BossBilinmiyor);
    private readonly SolidBrush _fircaUyari = new(Color.Transparent);
    private readonly Pen _kalemKilitsiz = new(Palet.Vurgu);
    private readonly Pen _kalemKilitli = new(Palet.Cizgi);
    private readonly Pen _kalemAyirac = new(Palet.Cizgi);
    private readonly Pen _kalemOlum = new(Palet.Kirmizi, 1.6f);
    private readonly SolidBrush _fircaKart = new(Palet.Panel);
    private readonly SolidBrush _fircaKukla = new(Palet.YerelRenk);
    private readonly SolidBrush _fircaKuklaIz = new(Palet.PanelUst);
    private readonly SolidBrush _fircaAlarmNabiz = new(Color.Transparent);
    private readonly Pen _kalemSaat = new(Palet.CokSoluk, 1.2f);
    private int _satirlarUst;
    private int _rowH, _bossSatirH; // yazı boyutuna göre (en az 22 / 19 × ölçek)

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;
    private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | Temel;

    // Çizim modeli (anlık görüntü değişince Tick içinde hazırlanır; OnPaint sadece çizer)
    private sealed class SatirModel
    {
        public string Ad = "", Cp = "", Toplam = "", Dps = "", Dps2 = "", Pay = "", Sira = "", Olum = "";
        public string? SinifAdi; // ikon için oyunun adı (palette olmayan sınıfların ikonu da çizilsin)
        public int Sinif, AdW;   // Sinif: Palet.SinifSirasi dizini (fırça önbelleği)
        public long Id, ToplamSayi;
        public float Oran;
        public bool Yerel;
    }

    private readonly SatirModel[] _satirlar = new SatirModel[MaxSatir];
    private readonly List<PlayerDamage> _sirali = new(32); // gösterilen toplama göre sıralı (yeniden kullanılır)
    private int _satirSayisi;
    private string? _durum;
    private bool _durumHata;
    private bool _bossGoster, _bossOldu, _bossBilinmiyor;
    private string _bossAd = "", _bossYuzde = "", _bossDeger = "";
    private float _bossOran;
    private string _sure = "", _partiEtiket = "", _partiDeger = "", _ipucu = "";

    // Ölçülen sütun genişlikleri (fiziksel px). Bir dövüş boyunca sadece büyür; pencere titremesin.
    private int _adW, _cpW, _toplamW, _dpsW, _dps2W, _payW, _icerikW, _siraW;
    private int _sureW, _partiEtiketW, _partiDegerW, _bossYuzdeW, _sonucW, _oyuncuSayisiW, _bossDegerW, _noktaW, _ipucuW;

    // Kalabalık düzen (> 5 oyuncu): en üstte kendi satırın (#sıra ile) + ayırıcı, sonra 1..5.
    private bool _kalabalik, _sabitUst, _sadeceBenModu;
    private string _oyuncuSayisi = "";

    // Boss modu: boss takip ediliyorsa satırdaki ana sayı Boss DPS'tir, hasar ve pay da bossa vurulandan.
    private bool _bossModu, _sonuc;

    // Boss bilgi satırı parçaları: süre · Kalan · Öfke · Yetişir/geç (sabit diziler, tahsis yok)
    private const int MaxBilgi = 4;
    private readonly string[] _bilgiMetin = new string[MaxBilgi];
    private readonly Color[] _bilgiRenk = new Color[MaxBilgi];
    private readonly int[] _bilgiW = new int[MaxBilgi];
    private int _bilgiSayisi;
    private TimeSpan _oncekiSure;

    private MeterSnapshot? _onceki;
    private long _olumBossId = long.MinValue;
    private DateTime _olumZamani;
    private int _tikSayaci;
    private bool _kilitli;

    // Öfke uyarısı: öfkeye ≤ 30 sn kala ve "geç" durumuna geçince boss bloğu 3 kez yumuşakça yanıp söner.
    private readonly System.Windows.Forms.Timer _uyariZamanlayici;
    private readonly Stopwatch _uyariSaat = new();
    private long _uyariBossId = long.MinValue;
    private bool _uyariOfkeVerildi, _uyariGecVerildi;
    private Rectangle _bossAlani;
    private const int UyariSuresiMs = 1200, UyariNabizMs = 400;

    // Kilitliyken Alt ile göz atma: Alt basılıyken overlay tıklanabilir olur (odak yine alınmaz).
    private readonly System.Windows.Forms.Timer _altZamanlayici;
    private bool _altAcik;   // Alt şu an basılı ve overlay tıklanabilir
    private bool _altUstunde; // Alt basılı ve imleç overlay'in üstünde: savaş dışında da tam görünür
    private bool _sabit;     // detay paneli sabitlendi (Alt bırakılınca kapanmaz)
    private long _sabitId;
    private long _hoverBastirId = long.MinValue;

    // Görünürlük. Sert: Ctrl+Alt+H ya da "sadece oyun öndeyken" → anında gizle, motor okunmaz.
    // Yumuşak: savaş dışında soluk / gizli (motor okunmaya devam eder ki savaş başlayınca açılsın).
    private readonly OnPlanIzleyici _onPlan = new();
    private readonly Cevirmen.CevirmenDenetleyici _cevirmen;
    private bool _kullaniciGizledi, _sadeceOyunda, _sertGorunur = true, _pencereAcik = true;
    private bool _savasta;
    private DateTime _sonSavas = DateTime.MinValue;
    private DateTime _solukOnizlemeBitis = DateTime.MinValue;
    private readonly System.Windows.Forms.Timer _gecisZamanlayici;
    private int _alfa = -1, _hedefAlfa;   // pencere opaklığı (0..255); -1: henüz uygulanmadı
    private float _olcek = 1f;    // DPI × kullanıcı boyutu (overlay ve detay paneli çizimi)
    private float _dpiOlcek = 1f; // sadece DPI (çevirmen ve ayarlar penceresi)
    private (int, int, int) _yaziImza; // son uygulanan yazı yüzdeleri (değişmediyse yazı tipleri yeniden yaratılmaz)
    private bool _denetleniyor;   // elle güncelleme denetimi sürüyor

    // Kukla testi: test sürerken boss bloğunun yerinde geri sayım bloğu, bitince sonuç kartı.
    private readonly KuklaTesti _kukla = new();
    private readonly System.Windows.Forms.Timer _kuklaZamanlayici; // sadece test sürerken, 100 ms
    private string _kuklaBaslik = "", _kuklaSag = "", _kuklaBilgi = "", _kuklaIpucu = "";
    private Color _kuklaBilgiRenk;
    private float _kuklaOran;
    private int _kuklaSagW, _kuklaBilgiW, _kuklaIpucuW;
    private Rectangle _kuklaAlani;
    // Sonuç kartı
    private string _kartBaslik = "", _kartDps = "", _kartToplam = "", _kartKiyas = "", _kartKiyasDeger = "";
    private Color _kartKiyasRenk;
    private readonly string[] _kartBeceri = new string[3], _kartBeceriHasar = new string[3], _kartBeceriPay = new string[3];
    private int _kartBeceriSayisi, _kartHasarW, _kartPayW, _kartDpsW;
    private Font? _yaziKart;

    // Alarm: alt satırdaki saat simgesi; çalışırken "Alarm 04:32". Zamanlayıcı sadece alarm kuruluyken çalışır.
    private readonly System.Windows.Forms.Timer _alarmZamanlayici;
    private readonly Stopwatch _alarmNabizSaat = new();
    private DateTime _alarmBitis = DateTime.MinValue;
    private int _alarmDk;
    private string _alarmMetin = "";
    private int _alarmMetinW, _saatW;
    private Rectangle _saatAlani, _altAlani;
    private AlarmSecici? _alarmSecici;
    private bool _saatUstunde; // imleç saatin üstünde (kilitsiz ya da Alt basılı): saat parlar
    private const int AlarmNabizMs = 3000, AlarmNabizAdim = 750; // 4 yumuşak yanıp sönme
    private bool AlarmKurulu => _alarmBitis != DateTime.MinValue;

    // Sürükleme / genişlik
    private bool _surukle, _kenardan, _tasindi;
    private Point _fareBas, _konumBas;
    private int _genislikBas;

    public OverlayForm(IMeterEngine motor, Ayarlar ayar, bool demo)
    {
        _motor = motor;
        _ayar = ayar;
        _demo = demo;
        _kilitli = ayar.Kilitli;
        _sadeceOyunda = ayar.SadeceOyunda ?? !demo; // demoda varsayılan kapalı ki görünsün
        Palet.Uygula(ayar); // kullanıcı renkleri
        for (int i = 0; i < MaxSatir; i++) _satirlar[i] = new SatirModel();
        for (int i = 0; i < Palet.SinifSirasi.Length; i++)
        {
            _fircaKoyu[i] = new SolidBrush(Palet.Arka);
            _fircaAcik[i] = new SolidBrush(Palet.Arka);
        }
        RenkleriUygula();

        Text = "AionMeter";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Bounds = BaslangicKonumu();
        IpucuGuncelle();

        _zamanlayici = new System.Windows.Forms.Timer { Interval = 500 }; // 2 Hz
        _zamanlayici.Tick += (_, _) => Tick();
        _cevirmen = new Cevirmen.CevirmenDenetleyici(ayar, () => _dpiOlcek, BizimPencereler, m => _tepsi?.Bildir(m));
        _cevirmen.DurumDegisti += () =>
        {
            TepsiGuncelle();
            AltZamanlayiciGuncelle();
            _ayarlarFormu?.Yenile();
        };
        _altZamanlayici = new System.Windows.Forms.Timer { Interval = 50 };
        _altZamanlayici.Tick += (_, _) => AltTick();
        _gecisZamanlayici = new System.Windows.Forms.Timer { Interval = 50 };
        _gecisZamanlayici.Tick += (_, _) => GecisTick();
        _uyariZamanlayici = new System.Windows.Forms.Timer { Interval = 50 };
        _uyariZamanlayici.Tick += (_, _) => UyariTick();
        _kuklaZamanlayici = new System.Windows.Forms.Timer { Interval = 100 };
        _kuklaZamanlayici.Tick += (_, _) => KuklaTick();
        _alarmZamanlayici = new System.Windows.Forms.Timer { Interval = 250 };
        _alarmZamanlayici.Tick += (_, _) => AlarmTick();

        SystemEvents.DisplaySettingsChanged += EkranDegisti;
        _sonSavas = DateTime.UtcNow; // açılışta 8 sn tam görünür (çalıştığı görülsün), sonra savaş dışı kuralı
    }

    /// <summary>Açılışta tepsi balonunda gösterilecek tek seferlik mesaj (Program ayarlar).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? AcilisBildirimi { get; init; }

    /// <summary>Programın komut satırı argümanları (elle güncellemede kurucuya aktarılır).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string[] Argumanlar { get; init; } = Array.Empty<string>();

    // ---------------- Pencere stilleri / odak ----------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            if (_kilitli && !_altAcik) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            else cp.ExStyle &= ~Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                // Tıklama pencereyi etkinleştirmesin; oyun odakta kalsın.
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            case Native.WM_NCHITTEST when _kilitli && !_altAcik:
                m.Result = (IntPtr)Native.HTTRANSPARENT;
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _alfa = -1;
        _hedefAlfa = HedefAlfa();
        AlfaUygula(_hedefAlfa);
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10: köşe yuvarlama yok, sorun değil */ }
        OlcekAyarla();
        BoyutAyarla();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _tepsi = new Tepsi(KilitDegistir, GosterDegistir, Sifirla, Detayim, _cevirmen.BolgeCevir, AyarlariAc, GuncellemeDenetle, Close,
            sn => KuklaTepsiden(sn > 0 ? sn : -1), KuklaIptal, AlarmSeciciAc);
        _tepsi.Menu.Opening += (_, _) => TepsiGuncelle(); // alarmın kalan süresi menü açılırken güncellenir
        _kisayol = new KisayolPenceresi();
        _kisayol.Basildi += KisayolBasildi;
        var alinamayan = new List<string>(3);
        if (!_kisayol.Kaydet(KisayolKilit, Keys.L)) alinamayan.Add("Ctrl+Alt+L");
        if (!_kisayol.Kaydet(KisayolGoster, Keys.H)) alinamayan.Add("Ctrl+Alt+H");
        if (!_kisayol.Kaydet(KisayolSifirla, Keys.R)) alinamayan.Add("Ctrl+Alt+R");
        if (!_kisayol.Kaydet(KisayolDetayim, Keys.D)) alinamayan.Add("Ctrl+Alt+D");
        if (!_kisayol.Kaydet(KisayolCevir, Keys.T)) alinamayan.Add("Ctrl+Alt+T");
        TepsiGuncelle();

        if (alinamayan.Count > 0)
        {
            string liste = string.Join(", ", alinamayan);
            HataLog.Yaz("Kısayol kaydedilemedi: " + liste);
            _tepsi.Bildir("Şu kısayollar başka bir program tarafından kullanılıyor: " + liste + ". Tepsi menüsünü kullanın.");
        }
        else if (AcilisBildirimi is not null)
        {
            _tepsi.Bildir(AcilisBildirimi); // ör. "AionMeter v0.2.1 sürümüne güncellendi"
        }
        else if (_ayar.IlkAcilis)
        {
            _tepsi.Bildir("Pencereyi sürükleyip yerleştirin. Ctrl+Alt+L kilitler (tıklamalar oyuna geçer), Ctrl+Alt+H gizler, Ctrl+Alt+R sıfırlar." +
                          " Savaş dışında overlay soluklaşır (Ayarlar'dan değiştirilebilir).");
        }

        try { _motor.Start(); }
        catch (Exception ex) { HataLog.Yaz("Motor başlatılamadı", ex); }

        AyarKaydet();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // İlk görünürlük kararı pencere gösterildikten sonra verilir (OnLoad'da gizlemek WinForms'un Show'u ile çakışır).
        Tick();
        _zamanlayici.Start();
    }

    // ---------------- Görünürlük ----------------

    private int TamAlfa => Math.Clamp(_ayar.Opaklik * 255 / 100, 40, 255);
    private int SolukAlfa => Math.Clamp(_ayar.SolukOpaklik * 255 / 100, 20, 255);

    /// <summary>Savaş dışı kuralından bağımsız olarak tam görünmesi gereken durumlar.</summary>
    private bool TamGorunmeli()
    {
        if (_ayarlarFormu is not null) return true;                 // ayar yaparken nasıl görüneceği görünsün
        if (!_kilitli || _altUstunde) return true;                  // konumlandırma / Alt ile göz atma
        if (_sabit && _detay is { Visible: true }) return true;     // sabitlenmiş detay paneli
        if (_kukla.Durum != KuklaTesti.Hal.Kapali) return true;    // kukla testi ve sonuç kartı
        if (_alarmSecici is not null || _alarmNabizSaat.IsRunning) return true; // alarm kurulurken / çalınca
        if (_durumHata) return true;                                // Npcap yok vb. sorunlar görünsün
        if (_savasta || DateTime.UtcNow - _sonSavas < SavasSonrasi) return true;
        return _ayar.SavasDisi == SavasDisiModu.HepGorunur;
    }

    private int HedefAlfa()
    {
        if (DateTime.UtcNow < _solukOnizlemeBitis) return SolukAlfa; // ayarlarda soluk opaklığı kaydırılırken
        if (TamGorunmeli()) return TamAlfa;
        return _ayar.SavasDisi == SavasDisiModu.Gizle ? 0 : SolukAlfa;
    }

    private void GorunurlukUygula()
    {
        if (!IsHandleCreated) return;
        bool sert = _ayarlarFormu is not null || (!_kullaniciGizledi && (!_sadeceOyunda || _onPlan.OyunOnde()));
        if (sert != _sertGorunur)
        {
            _sertGorunur = sert;
            AltZamanlayiciGuncelle();
        }
        if (!sert)
        {
            // Ctrl+Alt+H / oyun arkada: geçiş yok, hemen gizle.
            _gecisZamanlayici.Stop();
            PencereGizle();
            _alfa = 0;
            return;
        }
        _hedefAlfa = HedefAlfa();
        if (_alfa == _hedefAlfa)
        {
            if (_alfa == 0) PencereGizle(); // tamamen saydam pencere çizilmesin
            return;
        }
        if (_hedefAlfa > 0 && !_pencereAcik)
        {
            AlfaUygula(0);
            PencereGoster();
        }
        _gecisZamanlayici.Start();
    }

    /// <summary>~300 ms'de (6 × 50 ms) hedef opaklığa yürür; 0'a varınca pencere gizlenir (çizim de durur).</summary>
    private void GecisTick()
    {
        int adim = Math.Max(8, TamAlfa / 6);
        int a = _alfa < 0 ? _hedefAlfa : _alfa;
        a = a < _hedefAlfa ? Math.Min(_hedefAlfa, a + adim) : Math.Max(_hedefAlfa, a - adim);
        AlfaUygula(a);
        if (a != _hedefAlfa) return;
        _gecisZamanlayici.Stop();
        if (a == 0) PencereGizle();
    }

    private void AlfaUygula(int alfa)
    {
        if (alfa == _alfa || !IsHandleCreated) return;
        _alfa = alfa;
        Native.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Clamp(alfa, 0, 255), Native.LWA_ALPHA);
    }

    private void PencereGoster()
    {
        if (_pencereAcik) return;
        _pencereAcik = true;
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); // asla etkinleştirme
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_SHOWNOACTIVATE);
        _onceki = null; // gizliyken kaçan değişiklikler için modeli yeniden kur
    }

    private void PencereGizle()
    {
        if (!_pencereAcik) return;
        _pencereAcik = false;
        if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_HIDE);
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    /// <summary>
    /// Odak almayan pencereden açılan sağ tık menüsü, kullanıcı oyuna tıklayınca kendiliğinden kapanmayabilir:
    /// menü açıkken ön plandaki pencere bizim süreçte değilse kapat.
    /// </summary>
    private void MenuyuGerekirseKapat()
    {
        if (_tepsi?.Menu is not { Visible: true } menu) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        if (pid != (uint)Environment.ProcessId) menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
    }

    private void TepsiGuncelle()
    {
        _tepsi?.Guncelle(_kilitli, !_kullaniciGizledi, _denetleniyor);
        _tepsi?.KuklaGuncelle(_ayar.KuklaSure, _ayar.KuklaOzel, _ayar.KuklaOzelSure, _kukla.Etkin, AlarmKalan());
    }

    /// <summary>Boyut: overlay ve detay paneli bu yüzdeyle çizilir (Windows ölçeğinin üstüne).</summary>
    private void BoyutSec(int yuzde)
    {
        if (_ayar.Boyut == yuzde) return;
        _ayar.Boyut = yuzde;
        _ayar.Kaydet();
        OlcekAyarla();
        BoyutAyarla();
        _onceki = null;
        Tick();
        Invalidate();
    }

    /// <summary>Çevirmen için: bizim görünür pencerelerimiz (maskelenir, üstündeyken Alt sayaca aittir).</summary>
    private IEnumerable<Rectangle> BizimPencereler()
    {
        if (IsHandleCreated && _pencereAcik && _alfa > 0) yield return Bounds;
        if (_detay is { Visible: true }) yield return _detay.Bounds;
        if (_ayarlarFormu is { Visible: true }) yield return _ayarlarFormu.Bounds;
        if (_alarmSecici is { Visible: true }) yield return _alarmSecici.Bounds;
    }

    // ---------------- Ayarlar penceresi ----------------

    private void AyarlariAc()
    {
        if (_ayarlarFormu is not null)
        {
            Cevirmen.OdakYardimcisi.OneGetir(_ayarlarFormu);
            return;
        }
        var baglanti = new AyarlarFormu.Baglanti
        {
            BoyutSec = BoyutSec,
            OpaklikDegisti = () => { GorunurlukUygula(); AlfaUygula(_hedefAlfa); _gecisZamanlayici.Stop(); _detay?.AlfaAyarla((byte)TamAlfa); },
            SolukDegisti = () =>
            {
                _solukOnizlemeBitis = DateTime.UtcNow.AddSeconds(1.5); // kaydırırken soluk hali görünsün
                GorunurlukUygula(); AlfaUygula(_hedefAlfa); _gecisZamanlayici.Stop();
            },
            SavasDisiDegisti = GorunurlukUygula,
            SadeceOyunda = () => _sadeceOyunda,
            SadeceOyundaDegistir = SadeceOyundaDegistir,
            AltCeviriDegistir = _cevirmen.AltCeviriDegistir,
            DusukRamDegistir = _cevirmen.DusukRamDegistir,
            ModelHazir = () => _cevirmen.ModelHazir,
            ModelIndiriliyor = () => _cevirmen.ModelIndiriliyor,
            ModeliIndir = _cevirmen.ModeliIndir,
            GuncellemeDenetle = GuncellemeDenetle,
            Denetleniyor = () => _denetleniyor,
            GorunumDegisti = GorunumUygula,
            KuklaBaslat = () => { KuklaBaslat(_ayar.KuklaSuresi); _ayarlarFormu?.Close(); }, // vurmaya gitsin
            KuklaIptal = KuklaIptal,
            KuklaEtkin = () => _kukla.Etkin,
            AlarmKur = () => { _ayarlarFormu?.Close(); AlarmSeciciAc(); },
            AlarmIptal = AlarmIptal,
            AlarmKalan = AlarmKalan,
            Ikonlar = _ikonlar,
        };
        IntPtr oyun = _onPlan.SonOyunPenceresi;
        _ayarlarFormu = new AyarlarFormu(_ayar, baglanti, _dpiOlcek);
        _ayarlarFormu.FormClosed += (_, _) =>
        {
            var f = _ayarlarFormu;
            _ayarlarFormu = null;
            f?.Dispose();
            _solukOnizlemeBitis = DateTime.MinValue;
            _sonSavas = DateTime.MinValue; // pencere kapanınca hemen kurala dön
            Cevirmen.OdakYardimcisi.GeriVer(oyun); // odağı oyuna geri ver
            GorunurlukUygula();
        };
        // Ekranın ortasında, overlay'in bulunduğu monitörde
        var alan = Screen.FromRectangle(Bounds).WorkingArea;
        var boyut = _ayarlarFormu.Size;
        _ayarlarFormu.Location = new Point(alan.Left + (alan.Width - boyut.Width) / 2, alan.Top + (alan.Height - boyut.Height) / 2);
        GorunurlukUygula(); // gizliyse önizleme süresince görünür
        Tick();
        _ayarlarFormu.Show();
        Cevirmen.OdakYardimcisi.OneGetir(_ayarlarFormu);
    }

    private void SadeceOyundaDegistir()
    {
        _sadeceOyunda = !_sadeceOyunda;
        _ayar.SadeceOyunda = _sadeceOyunda;
        _ayar.Kaydet();
        GorunurlukUygula();
        TepsiGuncelle();
        if (_sertGorunur) Tick();
    }

    // ---------------- Elle güncelleme denetimi ----------------

    private async void GuncellemeDenetle()
    {
        if (_denetleniyor) return;
        _denetleniyor = true;
        TepsiGuncelle();
        _ayarlarFormu?.Yenile();
        try
        {
            var d = await Guncelleyici.DenetleAsync(); // async HTTP: arayüz bloklanmaz
            switch (d.Durum)
            {
                case Guncelleyici.DenetimDurumu.Hata:
                    _tepsi?.Bildir("Güncelleme denetlenemedi");
                    break;
                case Guncelleyici.DenetimDurumu.Guncel:
                    _tepsi?.Bildir("AionMeter güncel (" + Guncelleyici.Goster(Guncelleyici.MevcutSurum) + ")");
                    break;
                case Guncelleyici.DenetimDurumu.GelistirmeDerlemesi:
                    _tepsi?.Bildir("Yeni sürüm var (" + Guncelleyici.Goster(d.Bilgi!.Surum) + ") ama geliştirme derlemesi güncellenmez");
                    break;
                case Guncelleyici.DenetimDurumu.YeniVar:
                    var sonuc = await Guncelleyici.GuncellemeyiSorAsync(d.Bilgi!);
                    if (sonuc.KurucuExe is not null && Guncelleyici.KurucuyuBaslat(sonuc, Argumanlar))
                    {
                        // Kurucu bu sürecin bitmesini bekleyip dosyaları değiştirir ve yeni sürümü --guncellendi ile açar.
                        Close();
                        return;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Elle güncelleme", ex);
            _tepsi?.Bildir("Güncelleme denetlenemedi");
        }
        finally
        {
            _denetleniyor = false;
            if (!IsDisposed)
            {
                TepsiGuncelle();
                _ayarlarFormu?.Yenile();
            }
        }
    }

    // ---------------- Zamanlayıcı ----------------

    private void Tick()
    {
        try
        {
            _tikSayaci++;
            if (_tikSayaci == 6) GC.Collect(); // açılıştan ~3 sn sonra bir kez: başlangıç çöplerini bırak
            GorunurlukUygula();
            AltZamanlayiciGuncelle();
            if (!_sertGorunur) return; // gizliyken motor okunmaz, çizilmez

            var s = _motor.Snapshot();
            SavasDurumu(s);
            var kuklaOnce = _kukla.Durum;
            if (kuklaOnce != KuklaTesti.Hal.Kapali && _kukla.Ilerle(s, KuklaDetay))
            {
                _onceki = null; // kart kapandı / test baştan / sonuç: model yeniden
                if (!_kukla.Etkin) _kuklaZamanlayici.Stop();
                if (_kukla.Durum != kuklaOnce)
                {
                    GenislikleriSifirla();
                    TepsiGuncelle();
                    _ayarlarFormu?.Yenile();
                }
            }
            GorunurlukUygula();
            if (!_pencereAcik) return; // savaş dışında gizli: sadece savaşın başlamasını bekler

            if ((_tikSayaci & 3) == 0)
            {
                // Bazı oyunlar kendini öne alır; en üstte kalmayı ara sıra yenile (etkinleştirmeden).
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                if (_detay is { Visible: true, IsHandleCreated: true })
                    Native.SetWindowPos(_detay.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }

            MenuyuGerekirseKapat();
            if (_detay is { Visible: true }) _detay.Yenile(); // detay sadece açıkken 2 Hz

            bool bossGoster = _kukla.Durum == KuklaTesti.Hal.Kapali && BossGosterilsinMi(s); // test ve kart sürerken boss bloğu yok
            if (_onceki is not null && bossGoster == _bossGoster && Ayni(_onceki, s)) return;

            _onceki = s;
            _bossGoster = bossGoster;
            ModelKur(s);
            BoyutAyarla();
            Invalidate();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Zamanlayıcı", ex);
        }
    }

    /// <summary>Savaş durumu ve yerel ad (anlık görüntü her tikte, model değişmese de).</summary>
    private void SavasDurumu(MeterSnapshot s)
    {
        _savasta = s.Savasta;
        if (s.Savasta) _sonSavas = DateTime.UtcNow;
        _durumHata = s.State is EngineState.NoNpcap or EngineState.NoAdapter or EngineState.Failed;

        string? ad = s.YerelAd?.Trim();
        if (!string.IsNullOrEmpty(ad) && !ad.Contains('\n') && !ad.Contains('\r') && !string.Equals(ad, _ayar.YerelAd, StringComparison.Ordinal))
        {
            _ayar.YerelAd = ad;
            _ayar.Kaydet();
        }
    }

    private bool BossGosterilsinMi(MeterSnapshot s)
    {
        if (s.State != EngineState.Connected || s.Boss is not { } b) return false;
        if (!b.Dead)
        {
            _olumBossId = long.MinValue;
            return true;
        }
        if (s.FightOver) return true; // donmuş sonuç: boss bloğu "Öldü" ile kalır
        if (_olumBossId != b.Id)
        {
            _olumBossId = b.Id;
            _olumZamani = DateTime.UtcNow;
        }
        return (DateTime.UtcNow - _olumZamani).TotalSeconds < 4.0; // "Öldü" kısa süre görünür
    }

    private static bool Ayni(MeterSnapshot a, MeterSnapshot b)
    {
        if (a.State != b.State || a.FightOver != b.FightOver || a.Message != b.Message || !Equals(a.Boss, b.Boss)) return false;
        if ((long)a.FightDuration.TotalSeconds != (long)b.FightDuration.TotalSeconds) return false;
        var x = a.Players ?? Array.Empty<PlayerDamage>();
        var y = b.Players ?? Array.Empty<PlayerDamage>();
        if (x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++) // katkı % ve parti DPS herkese bağlı: hepsi karşılaştırılır
            if (!Equals(x[i], y[i])) return false;
        return true;
    }

    private static string DurumMetni(MeterSnapshot s) => s.State switch
    {
        EngineState.Starting => "Başlatılıyor…",
        EngineState.NoNpcap => "Npcap bulunamadı",
        EngineState.NoAdapter => "Ağ kartı açılamadı",
        EngineState.WaitingGame => "Oyun bekleniyor…",
        EngineState.Failed => string.IsNullOrWhiteSpace(s.Message) ? "Hata" : s.Message!,
        _ => s.Message ?? "",
    };

    /// <summary>Gösterilen toplam: boss modunda bossa vurulan, değilse tüm hasar.</summary>
    private static long Gosterilen(PlayerDamage p, bool bossModu) => Math.Max(0, bossModu ? p.BossTotal : p.Total);

    private static readonly Comparison<PlayerDamage> BossSirasi = (a, b) =>
    {
        int c = Math.Max(0, b.BossTotal).CompareTo(Math.Max(0, a.BossTotal));
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    };
    private static readonly Comparison<PlayerDamage> GenelSira = (a, b) =>
    {
        int c = Math.Max(0, b.Total).CompareTo(Math.Max(0, a.Total));
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    };

    private void ModelKur(MeterSnapshot s)
    {
        var oyuncular = s.Players ?? Array.Empty<PlayerDamage>();
        bool bossModu = _bossGoster; // başlıkla aynı koşul (Tick'te ModelKur'dan önce hesaplanır)
        if (bossModu != _bossModu) GenislikleriSifirla();
        _bossModu = bossModu;
        _sonuc = s.FightOver;

        // Yeni dövüş / bağlantı değişimi: sütun genişliklerini baştan ölç.
        if (s.State != EngineState.Connected || s.FightDuration < _oncekiSure) GenislikleriSifirla();
        _oncekiSure = s.FightDuration;

        // Sıralama gösterilen toplama göre (boss modunda bossa vurulan hasar); eşitlikte Id ile kararlı.
        _sirali.Clear();
        _sirali.AddRange(oyuncular);
        _sirali.Sort(bossModu ? BossSirasi : GenelSira);

        double partiDps = 0;
        if (s.State != EngineState.Connected)
        {
            _durum = DurumMetni(s);
            _satirSayisi = 0;
        }
        else
        {
            // ≤ 5 oyuncu: hepsi, sıra numarasız. > 5: [kendi satırın #sıra] + ayırıcı + 1..5.
            // Sadece benim satırım: yalnız kendi satırın (#sıra ile); diğerleri arka planda sayılır.
            // Katkı % ve toplamlar HER ZAMAN tüm listeden hesaplanır.
            bool kalabalik = _sirali.Count > UstSayisi;
            long enCok = 1, partiToplam = 0;
            int yerelSira = -1;
            for (int i = 0; i < _sirali.Count; i++)
            {
                long t = Gosterilen(_sirali[i], bossModu);
                partiToplam += t;
                if (t > enCok) enCok = t;
                if (yerelSira < 0 && _sirali[i].IsLocal) yerelSira = i;
            }
            bool sadeceBen = _ayar.SadeceBen && yerelSira >= 0;
            if (sadeceBen) kalabalik = false;
            if (kalabalik != _kalabalik || sadeceBen != _sadeceBenModu) GenislikleriSifirla();
            _kalabalik = kalabalik;
            _sadeceBenModu = sadeceBen;
            int ustN = sadeceBen ? 0 : kalabalik ? UstSayisi : Math.Min(_sirali.Count, MaxSatir);
            int n = 0;
            _sabitUst = kalabalik && yerelSira >= 0;
            if (_sabitUst || sadeceBen) SatirDoldur(_satirlar[n++], _sirali[yerelSira], "#" + (yerelSira + 1).ToString(Bicim.Tr));
            for (int i = 0; i < ustN; i++) SatirDoldur(_satirlar[n++], _sirali[i], kalabalik ? SiraMetni[i] : "");

            void SatirDoldur(SatirModel m, PlayerDamage p, string sira)
            {
                long t = Gosterilen(p, bossModu);
                m.Sira = sira;
                m.Id = p.Id;
                m.ToplamSayi = t;
                m.Pay = Bicim.Oran(t, partiToplam);
                m.Ad = string.IsNullOrWhiteSpace(p.Name) ? "?" : p.Name;
                m.SinifAdi = p.ClassName;
                m.Sinif = Palet.SinifIndeksi(p.ClassName);
                m.Cp = p.CombatPower is int cp ? Bicim.Tam(cp) : "—";
                m.Toplam = Bicim.Tam(t);
                m.Dps = Bicim.Sayi(bossModu ? p.BossDps : p.Dps);
                m.Dps2 = bossModu ? Bicim.Sayi(p.Dps) : "";
                m.Olum = p.Olum > 0 ? p.Olum.ToString(Bicim.Tr) : "";
                m.Oran = t > 0 ? Math.Clamp((float)((double)t / enCok), 0f, 1f) : 0f; // 0 hasar: pay çubuğu yok
                m.Yerel = p.IsLocal;
            }
            _satirSayisi = n;
            _durum = n == 0 ? "Dövüş bekleniyor…" : null;
        }

        for (int i = 0; i < oyuncular.Count; i++)
        {
            double d = bossModu ? oyuncular[i].BossDps : oyuncular[i].Dps;
            if (d > 0 && !double.IsNaN(d) && !double.IsInfinity(d)) partiDps += d;
        }
        bool cok = oyuncular.Count > UstSayisi || (_sadeceBenModu && oyuncular.Count > 1);
        bool partiVar = s.State == EngineState.Connected && _satirSayisi > 0;
        _partiEtiket = partiVar ? (oyuncular.Count > UstSayisi ? "Toplam" : "Parti") : "";
        _partiDeger = partiVar ? Bicim.Dps(partiDps) : "";
        _oyuncuSayisi = s.State == EngineState.Connected && cok ? oyuncular.Count.ToString(Bicim.Tr) + " oyuncu" : "";
        _bilgiSayisi = 0;

        if (_bossGoster && s.Boss is { } b) BossModelKur(s, b);
        if (_kukla.Etkin) KuklaModelKur();
        else if (_kukla.Sonuc is { } ks) KartModelKur(ks);
        _sure = (s.FightOver ? "Sonuç · " : "") + Bicim.Sure(s.FightDuration);
        GenislikleriOlc();
    }

    private void BossModelKur(MeterSnapshot s, BossHp b)
    {
        _bossAd = string.IsNullOrWhiteSpace(b.Name) ? "Boss" : b.Name;
        _bossOldu = b.Dead;
        _bossBilinmiyor = b.CanBilinmiyor && !b.Dead;
        if (b.Id != _uyariBossId)
        {
            _uyariBossId = b.Id;
            _uyariOfkeVerildi = _uyariGecVerildi = false;
        }

        BilgiEkle(Bicim.Sure(s.FightDuration), Palet.Soluk);
        if (!b.Dead)
        {
            TimeSpan? ttk = _bossBilinmiyor ? null : b.TimeToKill;
            if (ttk is TimeSpan k) BilgiEkle("Kalan ~" + Bicim.Kalan(k), Palet.Yazi);
            if (b.TimeToRage is TimeSpan ofke)
            {
                if (ofke >= TimeSpan.Zero)
                {
                    bool yakin = ofke.TotalSeconds <= 30;
                    BilgiEkle("Öfke " + Bicim.Kalan(ofke), yakin ? Palet.Kirmizi : Palet.Soluk);
                    bool gec = false;
                    if (ttk is TimeSpan k2)
                    {
                        gec = k2 > ofke;
                        if (gec) BilgiEkle(Bicim.Kalan(k2 - ofke) + " geç", Palet.Kirmizi);
                        else BilgiEkle("Yetişir", Palet.Yesil);
                    }
                    if (!s.FightOver)
                    {
                        if (yakin && !_uyariOfkeVerildi) { _uyariOfkeVerildi = true; UyariBaslat(); }
                        if (gec && !_uyariGecVerildi) { _uyariGecVerildi = true; UyariBaslat(); }
                    }
                }
                else
                {
                    BilgiEkle("Öfkede +" + Bicim.Kalan(-ofke), Palet.Kirmizi);
                }
            }
        }

        if (b.Dead)
        {
            _bossOran = 0f;
            _bossYuzde = "Öldü";
            _bossDeger = b.Max > 0 ? "0 / " + Bicim.Tam(b.Max) : "";
        }
        else if (b.Max > 0)
        {
            double oran = Math.Clamp((double)b.Current / b.Max, 0.0, 1.0);
            _bossOran = (float)oran;
            _bossYuzde = _bossBilinmiyor ? "?" : Bicim.CanYuzde(oran);
            _bossDeger = (_bossBilinmiyor ? "?" : Bicim.Tam(Math.Max(0, b.Current))) + " / " + Bicim.Tam(b.Max);
        }
        else
        {
            _bossOran = 1f; // en yüksek can bilinmiyor
            _bossYuzde = _bossBilinmiyor ? "?" : "";
            _bossDeger = _bossBilinmiyor ? "?" : Bicim.Tam(Math.Max(0, b.Current));
        }
    }

    private void BilgiEkle(string metin, Color renk)
    {
        if (_bilgiSayisi >= MaxBilgi) return;
        _bilgiMetin[_bilgiSayisi] = metin;
        _bilgiRenk[_bilgiSayisi] = renk;
        _bilgiSayisi++;
    }

    private void IpucuGuncelle()
    {
        // Kilitliyken ipucu yok (yer kaplamasın); sadece konumlandırma modunda nasıl kilitleneceği yazar.
        string ipucu = _kilitli ? "" : "Ctrl+Alt+L kilitle";
        _ipucu = _demo ? (ipucu.Length > 0 ? "DEMO · " + ipucu : "DEMO") : ipucu;
    }

    // ---------------- Öfke uyarısı ----------------

    private void UyariBaslat()
    {
        _uyariSaat.Restart();
        _uyariZamanlayici.Start();
        if (_ayar.OfkeSesi)
        {
            try { System.Media.SystemSounds.Exclamation.Play(); }
            catch { /* ses yoksa önemsiz */ }
        }
    }

    /// <summary>Uyarı süresince sadece boss bloğu yeniden çizilir (satırlar 2 Hz'de kalır).</summary>
    private void UyariTick()
    {
        long ms = _uyariSaat.ElapsedMilliseconds;
        if (ms >= UyariSuresiMs)
        {
            _uyariZamanlayici.Stop();
            _uyariSaat.Reset();
            _fircaUyari.Color = Color.Transparent;
        }
        else
        {
            double k = Math.Sin(Math.PI * (ms % UyariNabizMs) / UyariNabizMs); // 0 → 1 → 0, yumuşak nabız
            _fircaUyari.Color = Color.FromArgb((int)(56 * k * k), Palet.Kirmizi);
        }
        if (!_bossAlani.IsEmpty) Invalidate(_bossAlani);
    }

    // ---------------- Renk ve yazı (Ayarlar → Renk ve yazı, canlı) ----------------

    /// <summary>Palet'teki güncel renklerden önbellekli fırçaları yeniler (tahsis yok, sadece Color atanır).</summary>
    private void RenkleriUygula()
    {
        for (int i = 0; i < Palet.SinifSirasi.Length; i++)
        {
            var r = Palet.Sinif(Palet.SinifSirasi[i]);
            _fircaKoyu[i].Color = Color.FromArgb(Palet.CubukAlfa, r.Koyu);
            _fircaAcik[i].Color = r.Acik;
        }
        _fircaYerel.Color = Palet.YerelRenk;
        _fircaKukla.Color = Palet.YerelRenk;
    }

    /// <summary>Ayarlar penceresinden: renk, yazı boyutu, çubuk opaklığı ya da "sadece benim satırım" değişti.</summary>
    private void GorunumUygula()
    {
        Palet.Uygula(_ayar);
        RenkleriUygula();
        if (_yaziImza != (_ayar.YaziAd, _ayar.YaziDps, _ayar.YaziBoss))
        {
            // Yazı boyutu değişti: açık detay paneli yeni yazıyla yeniden açılır (aynı oyuncu, aynı sabitleme).
            long? acik = _detay is { Visible: true } ? _detay.OyuncuId : null;
            bool sabit = _sabit;
            OlcekAyarla();
            BoyutAyarla();
            if (acik is long id) DetayAc(id, sabit);
        }
        else _detay?.RenkYenile();
        GenislikleriSifirla();
        _onceki = null;
        Tick();
        Invalidate();
    }

    // ---------------- Kukla testi ----------------

    private PlayerDetail? KuklaDetay(long id) => _motor.Detail(id);

    /// <summary>Tepsiden: hazır süre (sn) ya da -1 = özel süre. Seçim ayara yazılır.</summary>
    private void KuklaTepsiden(int sure)
    {
        if (sure < 0) _ayar.KuklaOzel = true;
        else
        {
            _ayar.KuklaOzel = false;
            _ayar.KuklaSure = sure;
        }
        _ayar.Kaydet();
        KuklaBaslat(_ayar.KuklaSuresi);
    }

    private void KuklaBaslat(int sure)
    {
        try { _motor.Reset(); }
        catch (Exception ex) { HataLog.Yaz("Kukla sıfırlama", ex); }
        _kukla.Baslat(sure);
        _sonSavas = DateTime.UtcNow;
        GenislikleriSifirla();
        _onceki = null;
        _kuklaZamanlayici.Start();
        GorunurlukUygula();
        Tick();
        TepsiGuncelle();
        _ayarlarFormu?.Yenile();
    }

    /// <summary>Testi iptal eder ya da sonuç kartını kapatır.</summary>
    private void KuklaIptal()
    {
        if (_kukla.Durum == KuklaTesti.Hal.Kapali) return;
        _kukla.Kapat();
        _kuklaZamanlayici.Stop();
        _sonSavas = DateTime.UtcNow; // kart kapanınca hemen soluklaşmasın
        GenislikleriSifirla();
        _onceki = null;
        Tick();
        TepsiGuncelle();
        _ayarlarFormu?.Yenile();
    }

    /// <summary>Test sürerken 100 ms: geri sayım ve canlı DPS (sadece kukla bloğu yeniden çizilir).</summary>
    private void KuklaTick()
    {
        try
        {
            if (!_kukla.Etkin)
            {
                _kuklaZamanlayici.Stop();
                return;
            }
            var onceki = _kukla.Durum;
            var s = _motor.Snapshot();
            if (!_kukla.Ilerle(s, KuklaDetay)) return;
            if (_kukla.Durum != onceki)
            {
                if (!_kukla.Etkin) _kuklaZamanlayici.Stop();
                _onceki = null; // sonuç kartı: yükseklik değişir, tam model
                GenislikleriSifirla();
                Tick();
                TepsiGuncelle();
                _ayarlarFormu?.Yenile();
                return;
            }
            KuklaModelKur();
            if (_pencereAcik && !_kuklaAlani.IsEmpty) Invalidate(_kuklaAlani);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Kukla testi", ex);
        }
    }

    private string KuklaIpucuMetni(string eylem) => (_kilitli ? "Alt+tık: " : "Tık: ") + eylem;

    private void KuklaModelKur()
    {
        _kuklaBaslik = "Kukla testi · " + _kukla.Sure.ToString(Bicim.Tr) + " sn";
        _kuklaIpucu = KuklaIpucuMetni("iptal");
        if (_kukla.Durum == KuklaTesti.Hal.Bekliyor)
        {
            _kuklaSag = Bicim.Sure(TimeSpan.FromSeconds(_kukla.Sure));
            _kuklaOran = 0f;
            _kuklaBilgi = "Vurmaya başla: ilk vuruşta sayım başlar";
            _kuklaBilgiRenk = Palet.YerelYazi;
        }
        else
        {
            double kalan = Math.Max(0, _kukla.Sure - _kukla.Gecen.TotalSeconds);
            _kuklaSag = Bicim.Sure(TimeSpan.FromSeconds(Math.Ceiling(kalan)));
            _kuklaOran = (float)Math.Clamp(_kukla.Gecen.TotalSeconds / _kukla.Sure, 0, 1);
            if (_kukla.Durdu)
            {
                _kuklaBilgi = "Süre durdu: vurmaya devam et";
                _kuklaBilgiRenk = Palet.Kirmizi;
            }
            else
            {
                _kuklaBilgi = Bicim.Dps(_kukla.Dps) + " · Toplam " + Bicim.Tam(_kukla.Toplam);
                _kuklaBilgiRenk = Palet.Yazi;
            }
        }
        if (_yaziKucuk is not null) _kuklaBilgiW = Olc(_kuklaBilgi, _yaziKucuk);
    }

    private void KartModelKur(KuklaSonucu k)
    {
        _kartBaslik = k.Tamam
            ? "Kukla testi · " + Bicim.Sure(TimeSpan.FromSeconds(k.Sure))
            : "Kukla testi yarıda kaldı · " + Bicim.Sure(k.Gercek);
        _kuklaIpucu = KuklaIpucuMetni("kapat");
        _kartDps = Bicim.Dps(k.Dps);
        _kartToplam = "Toplam " + Bicim.Tam(k.Toplam);
        _kartBeceriSayisi = Math.Min(3, k.Beceriler.Count);
        for (int i = 0; i < _kartBeceriSayisi; i++)
        {
            var (ad, hasar) = k.Beceriler[i];
            _kartBeceri[i] = ad;
            _kartBeceriHasar[i] = Bicim.Tam(hasar);
            _kartBeceriPay[i] = Bicim.Oran(hasar, k.Toplam);
        }
        if (!k.Tamam)
        {
            _kartKiyas = "Süre dolmadı, rekora sayılmadı";
            _kartKiyasRenk = Palet.Soluk;
            _kartKiyasDeger = "";
        }
        else if (k.YeniRekor)
        {
            _kartKiyas = "Yeni rekor!";
            _kartKiyasRenk = Palet.Yesil;
            _kartKiyasDeger = k.OncekiRekor is long o ? "   Önceki: " + Bicim.Dps(o) : "";
        }
        else if (k.OncekiRekor is long rekor)
        {
            _kartKiyas = "Kişisel en iyi: " + Bicim.Dps(rekor) + " · bu ";
            _kartKiyasRenk = Palet.Soluk;
            _kartKiyasDeger = Bicim.Oran((long)Math.Round(k.Dps), rekor);
        }
        else
        {
            _kartKiyas = "DPS ölçülemedi";
            _kartKiyasRenk = Palet.Soluk;
            _kartKiyasDeger = "";
        }
    }

    /// <summary>Kukla bloğu (boss bloğuyla aynı ölçüler): başlık · kalan süre / ilerleme çubuğu / canlı DPS ..... ipucu.</summary>
    private void CizKukla(Graphics g, int x0, int x1, int y)
    {
        int satirH = BossSatirH;
        int sx = x1 - _kuklaSagW;
        TextRenderer.DrawText(g, _kuklaSag, _yaziBoss!, new Rectangle(sx, y, _kuklaSagW, satirH),
            _kukla.Durum == KuklaTesti.Hal.Sayiyor ? Palet.Dps : Palet.Soluk, Sag);
        TextRenderer.DrawText(g, _kuklaBaslik, _yaziBoss!, new Rectangle(x0, y, Math.Max(0, sx - P(12) - x0), satirH), Palet.Yazi, Sol);
        y += satirH + P(3);

        var cubuk = new Rectangle(x0, y, x1 - x0, BossCubukH);
        g.FillRectangle(_fircaKuklaIz, cubuk);
        int dolu = (int)(cubuk.Width * _kuklaOran);
        if (dolu > 0) g.FillRectangle(_fircaKukla, cubuk.X, cubuk.Y, dolu, cubuk.Height);
        y += BossCubukH + P(4);

        int bilgiH = BossBilgiH;
        int ipX = x1 - _kuklaIpucuW;
        TextRenderer.DrawText(g, _kuklaIpucu, _yaziKucuk!, new Rectangle(ipX, y, _kuklaIpucuW, bilgiH), Palet.CokSoluk, Sag);
        TextRenderer.DrawText(g, _kuklaBilgi, _yaziKucuk!, new Rectangle(x0, y, Math.Max(0, Math.Min(_kuklaBilgiW, ipX - P(16) - x0)), bilgiH),
            _kuklaBilgiRenk, Sol);
    }

    /// <summary>
    /// Sonuç kartı: başlık ..... ipucu / [ikon] DPS (büyük) · toplam / en çok vuran 3 beceri (hasar · pay) / kişisel en iyi kıyası.
    /// </summary>
    private void CizKart(Graphics g, int x0, int x1, int y)
    {
        var k = _kukla.Sonuc!;
        var kart = new Rectangle(x0, y, x1 - x0, KartH);
        Cevirmen.Cizim.YuvarlakDoldur(g, _fircaKart, kart, P(5));
        g.FillRectangle(_fircaKukla, kart.X, kart.Y + P(6), Math.Max(2, P(2)), kart.Height - P(12)); // sol şerit: kendi rengin
        int ic = KartIc;
        int a0 = kart.X + ic, a1 = kart.Right - ic;
        y += ic - P(2);

        int satirH = BossSatirH;
        TextRenderer.DrawText(g, _kuklaIpucu, _yaziKucuk!, new Rectangle(a1 - _kuklaIpucuW, y, _kuklaIpucuW, satirH), Palet.CokSoluk, Sag);
        TextRenderer.DrawText(g, _kartBaslik, _yaziBoss!, new Rectangle(a0, y, Math.Max(0, a1 - _kuklaIpucuW - P(12) - a0), satirH), Palet.Yazi, Sol);
        y += satirH + P(4);

        int dpsH = KartDpsH, ikon = IkonW;
        g.DrawImage(_ikonlar.Al(k.Sinif), a0, y + (dpsH - ikon) / 2, ikon, ikon);
        int dx = a0 + ikon + P(8);
        TextRenderer.DrawText(g, _kartDps, _yaziKart!, new Rectangle(dx, y, _kartDpsW, dpsH), Palet.Dps, Sol);
        dx += _kartDpsW + P(14);
        TextRenderer.DrawText(g, _kartToplam, _yaziKucuk!, new Rectangle(dx, y + P(2), Math.Max(0, a1 - dx), dpsH), Palet.Soluk, Sol);
        y += dpsH + P(6);

        // En çok vuran 3 beceri: sıra · ad ........ hasar  pay
        int payX = a1 - _kartPayW, hasarX = payX - P(10) - _kartHasarW;
        int siraW = Olc("3", _yaziKucuk!);
        int bh = KartBeceriH;
        for (int i = 0; i < _kartBeceriSayisi; i++)
        {
            TextRenderer.DrawText(g, (i + 1).ToString(Bicim.Tr), _yaziKucuk!, new Rectangle(a0, y, siraW, bh), Palet.CokSoluk, Sag);
            int adX = a0 + siraW + P(8);
            TextRenderer.DrawText(g, _kartBeceri[i], _yazi!, new Rectangle(adX, y, Math.Max(0, hasarX - P(14) - adX), bh), Palet.Yazi, Sol);
            TextRenderer.DrawText(g, _kartBeceriHasar[i], _yaziKucuk!, new Rectangle(hasarX, y, _kartHasarW, bh), Palet.Yazi, Sag);
            TextRenderer.DrawText(g, _kartBeceriPay[i], _yaziKucuk!, new Rectangle(payX, y, _kartPayW, bh), Palet.Soluk, Sag);
            y += bh;
        }
        y += P(6);
        g.DrawLine(_kalemAyirac, a0, y, a1, y);
        y += 1 + P(6);

        var kiyasYazi = k.YeniRekor ? _yaziYari! : _yaziKucuk!;
        var degerYazi = k.YeniRekor ? _yaziKucuk! : _yaziYari!;
        int kw = Olc(_kartKiyas, kiyasYazi);
        TextRenderer.DrawText(g, _kartKiyas, kiyasYazi, new Rectangle(a0, y, Math.Min(kw, a1 - a0), P(18)), _kartKiyasRenk, Sol);
        if (_kartKiyasDeger.Length > 0 && a0 + kw < a1)
            TextRenderer.DrawText(g, _kartKiyasDeger, degerYazi, new Rectangle(a0 + kw, y, a1 - a0 - kw, P(18)),
                k.YeniRekor ? Palet.Soluk : Palet.Yazi, Sol);
    }

    // ---------------- Alarm ----------------

    private TimeSpan AlarmKalanSure()
    {
        var k = _alarmBitis - DateTime.UtcNow;
        return k <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(k.TotalSeconds));
    }

    private string? AlarmKalan() => AlarmKurulu ? Bicim.Sure(AlarmKalanSure()) : null;

    /// <summary>Saat simgesi: kodla çizilmiş daire + iki ibre (emoji/ikon dosyası yok).</summary>
    private void CizSaat(Graphics g, int x, int cy, Color renk)
    {
        float d = SaatBoy - 1f;
        float ox = x + 0.5f, oy = cy - d / 2f;
        var eski = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        _kalemSaat.Color = renk;
        g.DrawEllipse(_kalemSaat, ox, oy, d, d);
        float mx = ox + d / 2f, my = oy + d / 2f;
        g.DrawLine(_kalemSaat, mx, my, mx, oy + d * 0.22f);             // yelkovan
        g.DrawLine(_kalemSaat, mx, my, mx + d * 0.24f, my + d * 0.08f); // akrep
        g.SmoothingMode = eski;
    }

    /// <summary>Alt+tık (kilitliyken) / tık (kilitsiz) saat, ya da tepsi "Alarm…": seçiciyi aç / kapat.</summary>
    private void AlarmSeciciAc()
    {
        if (_alarmSecici is not null)
        {
            _alarmSecici.Close();
            return;
        }
        IntPtr oyun = _onPlan.SonOyunPenceresi;
        var f = new AlarmSecici(_olcek, AlarmKurulu ? _alarmDk : _ayar.AlarmDk, AlarmKalan);
        f.Baslatildi += AlarmBaslat;
        f.IptalEdildi += AlarmIptal;
        f.FormClosed += (_, _) =>
        {
            if (_alarmSecici == f) _alarmSecici = null;
            f.Dispose();
            // Seçici odak almaz; yine de odak bizdeyse (tepsiden açıldıysa) oyuna geri ver.
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
            if (pid == (uint)Environment.ProcessId) Cevirmen.OdakYardimcisi.GeriVer(oyun);
            _sonSavas = DateTime.UtcNow;
            GorunurlukUygula();
        };
        _alarmSecici = f;
        GorunurlukUygula(); // savaş dışında soluksa tam görünsün
        f.Yerlestir(Bounds);
        f.Show(); // SW_SHOWNOACTIVATE
    }

    private void AlarmBaslat(int dk)
    {
        _alarmDk = Math.Clamp(dk, 1, Ayarlar.EnFazlaAlarmDk);
        _ayar.AlarmDk = _alarmDk;
        _ayar.Kaydet();
        _alarmBitis = DateTime.UtcNow.AddMinutes(_alarmDk);
        AlarmNabizDurdur();
        _alarmMetin = "Alarm " + Bicim.Sure(AlarmKalanSure());
        _alarmZamanlayici.Interval = 250;
        _alarmZamanlayici.Start();
        _onceki = null; // alt satır genişliği değişti
        Tick();
        Invalidate();
        TepsiGuncelle();
        _ayarlarFormu?.Yenile();
    }

    private void AlarmIptal()
    {
        if (!AlarmKurulu && !_alarmNabizSaat.IsRunning) return;
        _alarmBitis = DateTime.MinValue;
        _alarmMetin = "";
        AlarmNabizDurdur();
        _alarmZamanlayici.Stop();
        GenislikleriSifirla();
        _onceki = null;
        Tick();
        Invalidate();
        TepsiGuncelle();
        _alarmSecici?.Yenile();
        _ayarlarFormu?.Yenile();
    }

    /// <summary>250 ms: kalan süre (metin saniyede bir değişir, sadece alt satır çizilir); çalınca 50 ms'de yumuşak nabız.</summary>
    private void AlarmTick()
    {
        try
        {
            if (_alarmNabizSaat.IsRunning)
            {
                long ms = _alarmNabizSaat.ElapsedMilliseconds;
                if (ms >= AlarmNabizMs)
                {
                    AlarmNabizDurdur();
                    _alarmZamanlayici.Stop();
                    GorunurlukUygula();
                }
                else
                {
                    double k = Math.Sin(Math.PI * (ms % AlarmNabizAdim) / AlarmNabizAdim); // 0 → 1 → 0
                    _fircaAlarmNabiz.Color = Color.FromArgb((int)(64 * k * k), Palet.Vurgu);
                }
                if (_pencereAcik && !_altAlani.IsEmpty) Invalidate(_altAlani);
                return;
            }
            if (!AlarmKurulu)
            {
                _alarmZamanlayici.Stop();
                return;
            }
            if (_alarmBitis <= DateTime.UtcNow)
            {
                AlarmCal();
                return;
            }
            string m = "Alarm " + Bicim.Sure(AlarmKalanSure());
            if (m == _alarmMetin) return;
            _alarmMetin = m;
            if (_pencereAcik && !_altAlani.IsEmpty) Invalidate(_altAlani);
            _alarmSecici?.Yenile();
            _ayarlarFormu?.Yenile(); // "Alarm · 04:32 kaldı" 
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Alarm", ex);
        }
    }

    private void AlarmCal()
    {
        int dk = _alarmDk;
        _alarmBitis = DateTime.MinValue;
        _alarmMetin = "";
        Zil.Cal(_ayar.AlarmSes);
        _tepsi?.Bildir("Alarm: " + dk.ToString(Bicim.Tr) + " dk doldu");
        _alarmNabizSaat.Restart();
        _alarmZamanlayici.Interval = 50;
        GorunurlukUygula(); // soluksa nabız boyunca tam görünür
        GenislikleriSifirla();
        _onceki = null;
        Tick();
        TepsiGuncelle();
        _alarmSecici?.Yenile();
        _ayarlarFormu?.Yenile();
    }

    private void AlarmNabizDurdur()
    {
        _alarmNabizSaat.Reset();
        _fircaAlarmNabiz.Color = Color.Transparent;
    }

    // ---------------- Ölçüm / boyut / ölçek ----------------

    private int P(float v) => (int)MathF.Round(v * _olcek);
    private int Pad => P(8);
    private int IkonW => P(18);
    private int Ic => P(6);         // satır içi kenar boşluğu
    private int GrupIci => P(6);    // sütun grubu içinde
    private int GrupArasi => P(14); // [ikon·ad·CP] | [boss/sn·/sn] | [hasar·pay]
    private int RowH => _rowH;      // en az 22; büyük yazıda büyür
    private int Aralik => P(2);     // satırlar arası nefes
    private int Adim => RowH + Aralik;
    private int BaslikH => P(15);   // sütun başlığı satırı
    private int Ayirac => P(7);     // sabit üst satırın altındaki boşluk + çizgi

    // Boss bloğu: ad/% satırı, ince can çubuğu, bilgi satırı
    private int BossSatirH => _bossSatirH; // en az 19; büyük boss yazısında büyür
    private int BossCubukH => P(8);
    private int BossBilgiH => P(16);
    private int BossBlokH => BossSatirH + P(3) + BossCubukH + P(4) + BossBilgiH + P(6);
    private int AltH => P(17);
    private int KartIc => P(10);     // sonuç kartı iç boşluğu
    private int KartBeceriH => P(18);
    private int KartDpsH => Math.Max(P(28), Yuk(_yaziKart!) + P(2));
    private int KartH => KartIc + BossSatirH + P(4) + KartDpsH + P(6) + _kartBeceriSayisi * KartBeceriH +
                         P(6) + 1 + P(6) + P(18) + KartIc - P(2);

    private bool KuklaBlokVar => _kukla.Etkin;
    private bool KartVar => _kukla.Durum == KuklaTesti.Hal.Sonuc && _kukla.Sonuc is not null;

    private static int Yuk(Font f) => TextRenderer.MeasureText("Ağ", f, Size.Empty, Temel).Height;

    private static int Olc(string metin, Font font) =>
        metin.Length == 0 ? 0 : TextRenderer.MeasureText(metin, font, Size.Empty, Temel).Width;

    private int OlumW(SatirModel m) => m.Olum.Length == 0 ? 0 : P(5) + P(7) + P(2) + Olc(m.Olum, _yaziKucuk!);

    private void GenislikleriSifirla()
    {
        _adW = _cpW = _toplamW = _dpsW = _dps2W = _payW = _icerikW = _siraW = 0;
    }

    /// <summary>
    /// Sütun konumları sağdan sola. Hem GenislikleriOlc (x1 = 0 ile gereken genişlik) hem Ciz bunu kullanır:
    /// ölçüm ve çizim formülleri birebir aynıdır.
    /// </summary>
    private void Sutunlar(int x1, out int cpX, out int dpsX, out int dps2X, out int toplamX, out int payX)
    {
        payX = x1 - Ic - _payW;
        toplamX = payX - GrupIci - _toplamW;
        int g = toplamX - GrupArasi;
        if (_dps2W > 0)
        {
            dps2X = g - _dps2W;
            dpsX = dps2X - GrupIci - _dpsW;
        }
        else
        {
            dps2X = g;
            dpsX = g - _dpsW;
        }
        cpX = dpsX - GrupArasi - _cpW;
    }

    /// <summary>Satırın sol bloğu: iç boşluk + [sıra] + ikon + ad (+ ölüm işareti); CP'ye kadar.</summary>
    private int SolBlokW => Ic + (_siraW > 0 ? _siraW + P(4) : 0) + IkonW + GrupIci + _adW + GrupIci;

    /// <summary>Metinleri ölçer, pencerenin içeriğe göre gereken genişliğini hesaplar (Tick içinde; OnPaint'te değil).</summary>
    private void GenislikleriOlc()
    {
        if (_yazi is null || _yaziYari is null || _yaziDps is null || _yaziKucuk is null || _yaziBaslik is null || _yaziBoss is null) return;
        int pad = Pad;
        int gerek = 0;

        if (_durum is not null)
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_durum, _yazi), P(500)));

        if (_satirSayisi > 0)
        {
            // Başlık metinleri sütunun en az genişliğidir; CP sabit şablon genişliğinde (sağa hizalı, satırlar zıplamasın).
            _cpW = Math.Max(_cpW, Math.Max(Olc(CpSablonu, _yaziKucuk), Olc(CpEtiketi, _yaziBaslik)));
            _dpsW = Math.Max(_dpsW, Olc(_bossModu ? BossEtiketi : DpsEtiketi, _yaziBaslik));
            if (_bossModu) _dps2W = Math.Max(_dps2W, Olc(DpsEtiketi, _yaziBaslik));
            _toplamW = Math.Max(_toplamW, Olc(HasarEtiketi, _yaziBaslik));
            _payW = Math.Max(_payW, Olc(PayEtiketi, _yaziBaslik));
            for (int i = 0; i < _satirSayisi; i++)
            {
                var m = _satirlar[i];
                m.AdW = Math.Min(Olc(m.Ad, m.Yerel ? _yaziYari : _yazi), P(150));
                _adW = Math.Max(_adW, m.AdW + OlumW(m));
                _cpW = Math.Max(_cpW, Olc(m.Cp, _yaziKucuk));
                _toplamW = Math.Max(_toplamW, Olc(m.Toplam, _yaziKucuk));
                _dpsW = Math.Max(_dpsW, Olc(m.Dps, _yaziDps));
                if (_bossModu) _dps2W = Math.Max(_dps2W, Olc(m.Dps2, _yaziKucuk));
                if (m.Sira.Length > 0) _siraW = Math.Max(_siraW, Olc(m.Sira, m.Sira[0] == '#' ? _yaziYari : _yaziKucuk));
                _payW = Math.Max(_payW, Olc(m.Pay, _yaziKucuk));
            }
            Sutunlar(0, out int cpX, out _, out _, out _, out _);
            gerek = Math.Max(gerek, 2 * pad + SolBlokW - cpX);
        }

        _noktaW = Olc("·", _yaziKucuk);
        if (_bossGoster)
        {
            // Satır 1: boss adı [Sonuç] ... %yy,y   Satır 3: süre · Kalan · Öfke · Yetişir ........ can
            _bossYuzdeW = Olc(_bossYuzde, _yaziBoss);
            _sonucW = _sonuc ? P(8) + Olc(SonucEtiketi, _yaziKucuk) : 0;
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_bossAd, _yaziBoss), P(220)) + _sonucW + P(12) + _bossYuzdeW);
            _bossDegerW = Olc(_bossDeger, _yaziKucuk);
            gerek = Math.Max(gerek, 2 * pad + BilgiW() + P(16) + _bossDegerW);
        }
        if (KuklaBlokVar)
        {
            // Sağdaki geri sayım şablonla ölçülür: saniye değiştikçe pencere zıplamasın.
            _kuklaSagW = Math.Max(Olc("00:00", _yaziBoss), Olc(_kuklaSag, _yaziBoss));
            gerek = Math.Max(gerek, 2 * pad + Olc(_kuklaBaslik, _yaziBoss) + P(12) + _kuklaSagW);
            _kuklaIpucuW = Olc(_kuklaIpucu, _yaziKucuk);
            _kuklaBilgiW = Olc(_kuklaBilgi, _yaziKucuk);
            int sablon = Math.Max(_kuklaBilgiW, Olc("888.888 /sn · Toplam 88.888.888", _yaziKucuk));
            gerek = Math.Max(gerek, 2 * pad + sablon + P(16) + _kuklaIpucuW);
        }
        if (KartVar)
        {
            int ic = 2 * pad + 2 * KartIc;
            _kuklaIpucuW = Olc(_kuklaIpucu, _yaziKucuk);
            gerek = Math.Max(gerek, ic + Olc(_kartBaslik, _yaziBoss) + P(12) + _kuklaIpucuW);
            _kartDpsW = Olc(_kartDps, _yaziKart!);
            gerek = Math.Max(gerek, ic + IkonW + P(8) + _kartDpsW + P(14) + Olc(_kartToplam, _yaziKucuk));
            _kartPayW = Olc("%100,0", _yaziKucuk);
            _kartHasarW = 0;
            int adMaks = 0;
            for (int i = 0; i < _kartBeceriSayisi; i++)
            {
                _kartHasarW = Math.Max(_kartHasarW, Olc(_kartBeceriHasar[i], _yaziKucuk));
                adMaks = Math.Max(adMaks, Math.Min(Olc(_kartBeceri[i], _yazi), P(190)));
            }
            gerek = Math.Max(gerek, ic + Olc("3", _yaziKucuk) + P(8) + adMaks + P(14) + _kartHasarW + P(10) + _kartPayW);
            gerek = Math.Max(gerek, ic + Olc(_kartKiyas, _yaziKucuk) + Olc(_kartKiyasDeger, _yaziYari));
        }
        _sureW = _bossGoster || KuklaBlokVar || _durum != null ? 0 : Olc(_sure, _yaziKucuk); // boss varken süre boss bloğunda; durum mesajında süre yok
        _partiEtiketW = Olc(_partiEtiket, _yaziKucuk);
        _partiDegerW = Olc(_partiDeger, _yaziYari);
        _oyuncuSayisiW = Olc(_oyuncuSayisi, _yaziKucuk);
        _ipucuW = Olc(_ipucu, _yaziKucuk);
        _alarmMetinW = AlarmKurulu ? Math.Max(Olc("Alarm 00:00", _yaziKucuk), Olc(_alarmMetin, _yaziKucuk)) : 0;
        _saatW = SaatBoy + (_alarmMetinW > 0 ? P(5) + _alarmMetinW : 0);
        gerek = Math.Max(gerek, 2 * pad + AltSolW() + (_ipucuW > 0 ? P(12) + _ipucuW : 0) + P(12) + _saatW);

        _icerikW = Math.Max(_icerikW, gerek);
    }

    /// <summary>Alt satır her zaman var: en azından alarm saati orada durur.</summary>
    private static bool AltVar => true;

    private int SaatBoy => P(12);

    private int BilgiW()
    {
        int w = 0;
        for (int i = 0; i < _bilgiSayisi; i++)
        {
            _bilgiW[i] = Olc(_bilgiMetin[i], _yaziKucuk!);
            w += _bilgiW[i] + (i > 0 ? NoktaAraligi : 0);
        }
        return w;
    }

    private int NoktaAraligi => P(6) + _noktaW + P(6);

    /// <summary>Alt satırın sol kısmı: [süre ·] Parti 294.270 /sn [· 25 oyuncu].</summary>
    private int AltSolW()
    {
        int w = 0;
        if (_sureW > 0) w += _sureW + (_partiDegerW > 0 ? NoktaAraligi : 0);
        if (_partiDegerW > 0) w += _partiEtiketW + P(4) + _partiDegerW;
        if (_oyuncuSayisiW > 0) w += NoktaAraligi + _oyuncuSayisiW;
        return w;
    }

    private void OlcekAyarla()
    {
        _dpiOlcek = Math.Max(1f, DeviceDpi / 96f);
        _olcek = _dpiOlcek * _ayar.Boyut / 100f;
        _yazi?.Dispose(); _yaziYari?.Dispose(); _yaziDps?.Dispose(); _yaziKucuk?.Dispose(); _yaziBaslik?.Dispose(); _yaziBoss?.Dispose();
        _yaziKart?.Dispose();
        // Ad, DPS ve boss yazıları Ayarlar → Renk ve yazı'daki yüzdelerle (%80–%130)
        float ad = _ayar.YaziAd / 100f, dps = _ayar.YaziDps / 100f, boss = _ayar.YaziBoss / 100f;
        _yaziImza = (_ayar.YaziAd, _ayar.YaziDps, _ayar.YaziBoss);
        _yazi = Palet.YaziTipi(12f * _olcek * ad);
        _yaziYari = Palet.YaziTipi(12f * _olcek * ad, yariKalin: true);
        _yaziDps = Palet.YaziTipi(13f * _olcek * dps, yariKalin: true);
        _yaziKucuk = Palet.YaziTipi(11f * _olcek);
        _yaziBaslik = Palet.YaziTipi(10.5f * _olcek);
        _yaziBoss = Palet.YaziTipi(13f * _olcek * boss, yariKalin: true);
        _yaziKart = Palet.YaziTipi(20f * _olcek * dps, yariKalin: true);
        _rowH = Math.Max(P(22), Math.Max(Yuk(_yazi), Yuk(_yaziDps)) + P(3));
        _bossSatirH = Math.Max(P(19), Yuk(_yaziBoss) + P(1));
        _kalemSaat.Width = Math.Max(1f, 1.2f * _olcek);
        _kalemOlum.Width = Math.Max(1.2f, 1.6f * _olcek);
        DetayKapat(); // ölçek değişti: panel yeni ölçekle yeniden açılmalı
        _ikonlar.BoyutAyarla(IkonW);
        GenislikleriSifirla();
        GenislikleriOlc();
    }

    private int HesaplaYukseklik()
    {
        int h = Pad;
        if (_bossGoster || KuklaBlokVar) h += BossBlokH;
        if (KartVar) h += KartH + P(8);
        if (_durum != null) h += RowH;
        else
        {
            h += BaslikH + _satirSayisi * Adim - Aralik; // son satırın altında nefes payı yok (Ciz ile aynı)
            if (_sabitUst) h += Ayirac;
        }
        h += AltVar ? P(4) + 1 + P(4) + AltH + P(5) : P(4);
        return h;
    }

    private void BoyutAyarla()
    {
        if (!IsHandleCreated) return;
        int w = Math.Clamp(Math.Max(P(_ayar.Genislik), _icerikW), P(Ayarlar.EnAzGenislik), P(Ayarlar.EnFazlaGenislik));
        var yeni = new Size(w, HesaplaYukseklik());
        if (Size != yeni) Size = yeni; // WinForms SetWindowPos'u SWP_NOACTIVATE ile çağırır
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        OlcekAyarla();
        BoyutAyarla();
        Invalidate();
    }

    private Rectangle BaslangicKonumu()
    {
        var bolge = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int w = _ayar.Genislik, h = Math.Max(120, _ayar.Yukseklik);
        if (_ayar.X != int.MinValue && _ayar.Y != int.MinValue)
        {
            var r = new Rectangle(_ayar.X, _ayar.Y, w, h);
            if (EkrandaGorunur(r)) return r;
        }
        return new Rectangle(bolge.Right - w - 40, bolge.Top + 160, w, h);
    }

    /// <summary>Pencerenin en az 40×20 pikseli bir ekranın çalışma alanında mı?</summary>
    private static bool EkrandaGorunur(Rectangle r)
    {
        foreach (var ekran in Screen.AllScreens)
        {
            var kesisim = Rectangle.Intersect(ekran.WorkingArea, r);
            if (kesisim.Width >= 40 && kesisim.Height >= 20) return true;
        }
        return false;
    }

    private void EkranDegisti(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            try
            {
                if (EkrandaGorunur(Bounds)) return;
                var bolge = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
                Location = new Point(bolge.Right - Width - 40, bolge.Top + 160);
                AyarKaydet();
            }
            catch (Exception ex) { HataLog.Yaz("Ekran değişikliği", ex); }
        });
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Çizim", ex); }
    }

    private void Ciz(Graphics g)
    {
        if (_yazi is null || _yaziYari is null || _yaziDps is null || _yaziKucuk is null || _yaziBaslik is null || _yaziBoss is null) return;
        if (_yaziKart is null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = Pad;
        int x0 = pad, x1 = w - pad, gen = x1 - x0;
        int y = pad;

        // --- Boss ---
        if (_bossGoster)
        {
            _bossAlani = new Rectangle(0, 0, w, y + BossBlokH - P(3));
            CizBoss(g, x0, x1, y);
            y += BossBlokH;
        }
        else _bossAlani = Rectangle.Empty;

        // --- Kukla testi: geri sayım bloğu (boss bloğunun yerinde) ya da sonuç kartı ---
        if (KuklaBlokVar)
        {
            _kuklaAlani = new Rectangle(0, 0, w, y + BossBlokH - P(3));
            CizKukla(g, x0, x1, y);
            y += BossBlokH;
        }
        else if (KartVar)
        {
            _kuklaAlani = new Rectangle(0, 0, w, y + KartH + P(4));
            CizKart(g, x0, x1, y);
            y += KartH + P(8);
        }
        else _kuklaAlani = Rectangle.Empty;

        // --- Satırlar ya da durum ---
        if (_durum != null)
        {
            TextRenderer.DrawText(g, _durum, _yazi, new Rectangle(x0, y, gen, RowH), _durumHata ? Palet.Kirmizi : Palet.Soluk, Sol);
            y += RowH;
        }
        else
        {
            Sutunlar(x1, out int cpX, out int dpsX, out int dps2X, out int toplamX, out int payX);

            // Sütun başlığı: tekrar eden birimler hücrelerden kalktı, burada bir kez yazar.
            var bc = Palet.CokSoluk;
            TextRenderer.DrawText(g, CpEtiketi, _yaziBaslik, new Rectangle(cpX, y, _cpW, BaslikH), bc, Sag);
            TextRenderer.DrawText(g, _bossModu ? BossEtiketi : DpsEtiketi, _yaziBaslik, new Rectangle(dpsX, y, _dpsW, BaslikH), bc, Sag);
            if (_dps2W > 0) TextRenderer.DrawText(g, DpsEtiketi, _yaziBaslik, new Rectangle(dps2X, y, _dps2W, BaslikH), bc, Sag);
            TextRenderer.DrawText(g, HasarEtiketi, _yaziBaslik, new Rectangle(toplamX, y, _toplamW, BaslikH), bc, Sag);
            TextRenderer.DrawText(g, PayEtiketi, _yaziBaslik, new Rectangle(payX, y, _payW, BaslikH), bc, Sag);
            y += BaslikH;

            _satirlarUst = y;
            int rowH = RowH, ic = P(2);
            for (int i = 0; i < _satirSayisi; i++)
            {
                var m = _satirlar[i];
                bool sabitSatir = _sabitUst && i == 0;
                if (sabitSatir) g.FillRectangle(_fircaSabitSatir, x0, y, gen, rowH);

                // Pay çubuğu sınıfın koyu tonunda; solda 2 px şerit: sınıfın açık tonu, yerel oyuncuda altın.
                // (Şerit çubuğun ucuna değil sola konur: sayıların üstünden geçmesin.)
                int cubukY = y + ic, cubukH = rowH - 2 * ic;
                int pay = (int)(gen * m.Oran);
                if (pay > 0 && Palet.CubukAlfa > 0) g.FillRectangle(_fircaKoyu[m.Sinif], x0, cubukY, pay, cubukH);
                g.FillRectangle(m.Yerel ? _fircaYerel : _fircaAcik[m.Sinif], x0, cubukY, Math.Max(2, P(2)), cubukH);
                if (_detay is { Visible: true } && _detay.OyuncuId == m.Id) g.FillRectangle(_fircaSecili, x0, y, gen, rowH);

                int x = x0 + Ic;
                if (_siraW > 0)
                {
                    // Sıra: kendi satırında "#14" (kendi rengin), ilk 5'te küçük ve soluk "1..5"
                    bool kendiSira = m.Sira.Length > 0 && m.Sira[0] == '#';
                    TextRenderer.DrawText(g, m.Sira, kendiSira ? _yaziYari : _yaziKucuk, new Rectangle(x, y, _siraW, rowH),
                        kendiSira ? Palet.YerelYazi : Palet.CokSoluk, Sag);
                    x += _siraW + P(4);
                }
                int ikon = IkonW;
                g.DrawImage(_ikonlar.Al(m.SinifAdi), x, y + (rowH - ikon) / 2, ikon, ikon);
                x += ikon + GrupIci;
                int adSinir = Math.Max(0, cpX - GrupIci - x - OlumW(m));
                int adW = Math.Min(m.AdW, adSinir);
                TextRenderer.DrawText(g, m.Ad, m.Yerel ? _yaziYari : _yazi, new Rectangle(x, y, adW, rowH),
                    m.Yerel ? Palet.YerelYazi : Palet.Yazi, Sol);
                if (m.Olum.Length > 0) CizOlum(g, m, x + adW + P(5), y, rowH);

                TextRenderer.DrawText(g, m.Cp, _yaziKucuk, new Rectangle(cpX, y, _cpW, rowH), Palet.Soluk, Sag);
                TextRenderer.DrawText(g, m.Dps, _yaziDps, new Rectangle(dpsX, y, _dpsW, rowH), Palet.Dps, Sag);
                if (_dps2W > 0)
                    TextRenderer.DrawText(g, m.Dps2, _yaziKucuk, new Rectangle(dps2X, y, _dps2W, rowH), Palet.Soluk, Sag);
                TextRenderer.DrawText(g, m.Toplam, _yaziKucuk, new Rectangle(toplamX, y, _toplamW, rowH), Palet.Yazi, Sag);
                TextRenderer.DrawText(g, m.Pay, _yaziKucuk, new Rectangle(payX, y, _payW, rowH), Palet.Soluk, Sag);
                y += Adim;
                if (sabitSatir)
                {
                    int cy = y - Aralik + Ayirac / 2;
                    g.DrawLine(_kalemAyirac, x0, cy, x1, cy);
                    y += Ayirac;
                }
            }
            y -= Aralik;
        }

        // --- Alt satır: 1 px ayırıcı, [süre ·] Parti 294.270 /sn [· 25 oyuncu] ...... ipucu  ◷ [Alarm 04:32] ---
        y += P(4);
        g.DrawLine(_kalemAyirac, x0, y, x1, y);
        y += 1 + P(4);
        int altH = AltH;
        _altAlani = new Rectangle(1, y - P(3), w - 2, altH + P(5));
        if (_fircaAlarmNabiz.Color.A > 0) g.FillRectangle(_fircaAlarmNabiz, _altAlani);

        // Saat (ve çalışan alarmın kalan süresi) en sağda; ipucu onun soluna dayanır.
        int saatX = x1 - _saatW;
        _saatAlani = Rectangle.FromLTRB(saatX - P(5), y - P(3), Math.Min(w - 1, x1 + P(5)), y + altH + P(3));
        CizSaat(g, saatX, y + altH / 2, AlarmKurulu ? Palet.Vurgu : _saatUstunde ? Palet.Yazi : Palet.CokSoluk);
        if (_alarmMetinW > 0)
            TextRenderer.DrawText(g, _alarmMetin, _yaziKucuk, new Rectangle(saatX + SaatBoy + P(5), y, _alarmMetinW, altH), Palet.Yazi, Sol);
        x1 = saatX - P(12);

        int x2 = x0;
        if (_sureW > 0)
        {
            TextRenderer.DrawText(g, _sure, _yaziKucuk, new Rectangle(x2, y, _sureW, altH), Palet.Soluk, Sol);
            x2 += _sureW;
            if (_partiDegerW > 0) x2 = Nokta(g, x2, y, altH);
        }
        if (_partiDegerW > 0)
        {
            TextRenderer.DrawText(g, _partiEtiket, _yaziKucuk, new Rectangle(x2, y, _partiEtiketW, altH), Palet.Soluk, Sol);
            x2 += _partiEtiketW + P(4);
            TextRenderer.DrawText(g, _partiDeger, _yaziYari, new Rectangle(x2, y, Math.Max(0, Math.Min(_partiDegerW, x1 - x2)), altH), Palet.Yazi, Sol);
            x2 += _partiDegerW;
        }
        if (_oyuncuSayisiW > 0 && x2 < x1)
        {
            x2 = Nokta(g, x2, y, altH);
            TextRenderer.DrawText(g, _oyuncuSayisi, _yaziKucuk, new Rectangle(x2, y, Math.Max(0, Math.Min(_oyuncuSayisiW, x1 - x2)), altH), Palet.Soluk, Sol);
            x2 += _oyuncuSayisiW;
        }
        if (_ipucuW > 0 && x2 + P(12) < x1)
            TextRenderer.DrawText(g, _ipucu, _yaziKucuk, new Rectangle(x2 + P(12), y, x1 - x2 - P(12), altH), Palet.CokSoluk,
                Sag | TextFormatFlags.EndEllipsis);

        g.DrawRectangle(_kilitli ? _kalemKilitli : _kalemKilitsiz, 0, 0, w - 1, h - 1);
    }

    /// <summary>Boss bloğu: ad / % · ince can çubuğu (yazısız) · süre · Kalan · Öfke · Yetişir ..... can.</summary>
    private void CizBoss(Graphics g, int x0, int x1, int y)
    {
        int satirH = BossSatirH;
        int sx = x1 - _bossYuzdeW;
        var yuzdeRenk = _bossOldu || _bossBilinmiyor ? Palet.Soluk : Palet.Yazi;
        TextRenderer.DrawText(g, _bossYuzde, _yaziBoss!, new Rectangle(sx, y, _bossYuzdeW, satirH), yuzdeRenk, Sag);
        int adW = Math.Min(Olc(_bossAd, _yaziBoss!), Math.Max(0, sx - P(12) - _sonucW - x0));
        TextRenderer.DrawText(g, _bossAd, _yaziBoss!, new Rectangle(x0, y, adW, satirH), Palet.Yazi, Sol);
        if (_sonuc)
            TextRenderer.DrawText(g, SonucEtiketi, _yaziKucuk!, new Rectangle(x0 + adW + P(8), y, _sonucW - P(8), satirH), Palet.Soluk, Sol);
        y += satirH + P(3);

        // Can çubuğu: 8 px, içinde yazı yok. Can bilinmiyorsa son bilinen oran soluk ve kesikli.
        var cubuk = new Rectangle(x0, y, x1 - x0, BossCubukH);
        g.FillRectangle(_fircaBossIz, cubuk);
        int dolu = (int)(cubuk.Width * _bossOran);
        if (dolu > 0 && !_bossOldu)
        {
            if (_bossBilinmiyor)
            {
                int parca = P(6), bosluk = P(4);
                for (int px = cubuk.X; px < cubuk.X + dolu; px += parca + bosluk)
                    g.FillRectangle(_fircaBossBilinmiyor, px, cubuk.Y, Math.Min(parca, cubuk.X + dolu - px), cubuk.Height);
            }
            else g.FillRectangle(_fircaBossCan, cubuk.X, cubuk.Y, dolu, cubuk.Height);
        }
        y += BossCubukH + P(4);

        int bilgiH = BossBilgiH;
        int canX = x1 - _bossDegerW;
        if (_bossDegerW > 0)
            TextRenderer.DrawText(g, _bossDeger, _yaziKucuk!, new Rectangle(canX, y, _bossDegerW, bilgiH), Palet.Soluk, Sag);
        int bx = x0;
        int sinir = canX - P(12);
        for (int i = 0; i < _bilgiSayisi; i++)
        {
            if (i > 0) bx = Nokta(g, bx, y, bilgiH);
            if (bx >= sinir) break;
            TextRenderer.DrawText(g, _bilgiMetin[i], _yaziKucuk!, new Rectangle(bx, y, Math.Min(_bilgiW[i], sinir - bx), bilgiH), _bilgiRenk[i], Sol);
            bx += _bilgiW[i];
        }

        // Öfke uyarısı: boss bloğunun üstüne kısa, yumuşak kırmızı nabız
        if (_fircaUyari.Color.A > 0) g.FillRectangle(_fircaUyari, _bossAlani);
    }

    /// <summary>"·" ayırıcıyı çizer, sonraki metnin x'ini döndürür.</summary>
    private int Nokta(Graphics g, int x, int y, int h)
    {
        x += P(6);
        TextRenderer.DrawText(g, "·", _yaziKucuk!, new Rectangle(x, y, _noktaW, h), Palet.CokSoluk, Sol);
        return x + _noktaW + P(6);
    }

    /// <summary>Ölüm işareti: kodla çizilmiş küçük kırmızı çarpı + sayı (emoji yok).</summary>
    private void CizOlum(Graphics g, SatirModel m, int x, int y, int rowH)
    {
        int s = P(7);
        int cy = y + rowH / 2;
        var eski = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawLine(_kalemOlum, x, cy - s / 2f, x + s, cy + s / 2f);
        g.DrawLine(_kalemOlum, x + s, cy - s / 2f, x, cy + s / 2f);
        g.SmoothingMode = eski;
        int tw = Olc(m.Olum, _yaziKucuk!);
        TextRenderer.DrawText(g, m.Olum, _yaziKucuk!, new Rectangle(x + s + P(2), y, tw, rowH), Palet.Kirmizi, Sol);
    }

    // ---------------- Fare: sürükle / genişlik ----------------

    private bool KenardaMi(int x) => x >= ClientSize.Width - P(7);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_kilitli)
        {
            if (_altAcik && e.Button == MouseButtons.Left) AltTiklandi(e.Location); // Alt+tık: saat / kukla / sabitle
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            _surukle = true;
            _tasindi = false;
            _kenardan = KenardaMi(e.X);
            _fareBas = Cursor.Position;
            _konumBas = Location;
            _genislikBas = Math.Max(_ayar.Genislik, (int)MathF.Round(Width / _olcek));
            Capture = true;
        }
        else if (e.Button == MouseButtons.Right && _tepsi is not null)
        {
            _tepsi.Menu.Show(Cursor.Position);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_kilitli)
        {
            if (_altAcik) UzerineGelindi(e.Location);
            return;
        }
        if (!_surukle)
        {
            SaatUstundeAyarla(e.Location);
            Cursor = KenardaMi(e.X) ? Cursors.SizeWE : TiklanirAlan(e.Location) ? Cursors.Hand : Cursors.SizeAll;
            return;
        }
        var p = Cursor.Position;
        int dx = p.X - _fareBas.X, dy = p.Y - _fareBas.Y;
        if (dx != 0 || dy != 0) _tasindi = true;
        if (_kenardan)
        {
            // Genislik kullanıcının istediği en az genişliktir; içerik daha genişse pencere yine içeriğe uyar.
            int yeni = Math.Clamp(_genislikBas + (int)MathF.Round(dx / _olcek), Ayarlar.EnAzGenislik, Ayarlar.EnFazlaGenislik);
            if (yeni != _ayar.Genislik)
            {
                _ayar.Genislik = yeni;
                BoyutAyarla();
                Invalidate();
            }
        }
        else
        {
            Location = new Point(_konumBas.X + dx, _konumBas.Y + dy);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_surukle) return;
        _surukle = false;
        Capture = false;
        if (_tasindi) AyarKaydet();
        else if (!_kenardan && e.Button == MouseButtons.Left && !OzelTiklama(e.Location)) SatiraTiklandi(e.Y);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SaatUstundeAyarla(new Point(-1, -1));
    }

    /// <summary>Saat ve kukla bloğu/kartı tıklanabilir (kilitsizken el imleci).</summary>
    private bool TiklanirAlan(Point p) => _saatAlani.Contains(p) || _kuklaAlani.Contains(p);

    private void SaatUstundeAyarla(Point p)
    {
        bool ust = _saatAlani.Contains(p);
        if (ust == _saatUstunde) return;
        _saatUstunde = ust;
        if (!_saatAlani.IsEmpty) Invalidate(_saatAlani);
    }

    /// <summary>Saat → alarm seçici; kukla bloğu → testi iptal; sonuç kartı → kapat. İşlendiyse true.</summary>
    private bool OzelTiklama(Point p)
    {
        if (_saatAlani.Contains(p))
        {
            AlarmSeciciAc();
            return true;
        }
        if (_kuklaAlani.Contains(p))
        {
            KuklaIptal();
            return true;
        }
        return false;
    }

    // ---------------- Detay paneli ----------------

    /// <summary>Satır dizini; satırlar Adim (satır + 2 px nefes) aralıklıdır, sabit satırın altında ayırıcı vardır.</summary>
    private int SatirIndeksi(int y)
    {
        if (_durum != null || _satirSayisi == 0 || y < _satirlarUst) return -1;
        int yy = y - _satirlarUst;
        if (_sabitUst)
        {
            if (yy < Adim) return 0;           // sabit kendi satırın
            yy -= Adim + Ayirac;
            if (yy < 0) return -1;             // ayırıcı
            int k = 1 + yy / Adim;
            return k < _satirSayisi ? k : -1;
        }
        int i = yy / Adim;
        return i >= 0 && i < _satirSayisi ? i : -1;
    }

    /// <summary>Kilitsiz mod: düz tıklama paneli açar; aynı satıra ikinci tıklama kapatır.</summary>
    private void SatiraTiklandi(int y)
    {
        int i = SatirIndeksi(y);
        if (i < 0) return;
        long id = _satirlar[i].Id;
        if (_detay is { Visible: true } && _detay.OyuncuId == id)
        {
            DetayKapat(); // aynı satıra ikinci tıklama
            return;
        }
        DetayAc(id, sabit: false);
    }

    /// <summary>Son anlık görüntüdeki oyuncu kaydı (detay paneli: ölüm sayısı, yerel mi).</summary>
    private PlayerDamage? OyuncuBul(long id)
    {
        var liste = _onceki?.Players;
        if (liste is null) return null;
        foreach (var p in liste) if (p.Id == id) return p;
        return null;
    }

    /// <summary>Paneli açar ya da oyuncuyu değiştirir. sabit null ise sabitleme durumu değişmez (Alt ile üzerine gelme).</summary>
    private void DetayAc(long id, bool? sabit)
    {
        if (_detay is null)
        {
            _detay = new DetayForm(_motor, _ikonlar, _olcek, (byte)TamAlfa, OyuncuBul, _ayar.YaziAd / 100f, _ayar.YaziDps / 100f);
            _detay.KapatIstendi += DetayKapat;
        }
        if (sabit is bool b)
        {
            _sabit = b;
            if (b) _sabitId = id;
        }
        _detay.TiklanmazAyarla(_kilitli && !_altAcik);
        _detay.Goster(id, Bounds); // SW_SHOWNOACTIVATE
        GorunurlukUygula();
        Invalidate();
    }

    // ---------------- Kilitliyken Alt ile göz atma ----------------

    private void AltZamanlayiciGuncelle()
    {
        // Demoda oyun yok: oyun-önde şartı aranmaz (test edilebilsin).
        bool oyunOnde = _demo || _onPlan.OyunOnde();
        bool calismali = oyunOnde && ((_kilitli && _sertGorunur) || _cevirmen.AltCeviriCalisir);
        if (calismali == _altZamanlayici.Enabled) return;
        if (calismali) _altZamanlayici.Start();
        else
        {
            _altZamanlayici.Stop();
            AltBirakildi();
            _cevirmen.AltDurumu(false, Point.Empty); // açıklama paneli/işi kapanır
        }
    }

    private void AltTick()
    {
        try
        {
            bool basili = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
            var imlec = Cursor.Position;
            // İmleç sayacın/detayın üstündeyse Alt sayaca aittir; değilse çevirmene. İkisi birden asla.
            // Savaş dışında gizliyken de overlay'in yeri sayılır: Alt ile üstüne gelince görünür olsun.
            bool bizimUstunde = (_sertGorunur && Bounds.Contains(imlec)) || (_detay is { Visible: true } && _detay.Bounds.Contains(imlec)) ||
                                (_alarmSecici is { Visible: true } && _alarmSecici.Bounds.Contains(imlec));

            if (_kilitli)
            {
                if (basili && !_altAcik)
                {
                    _altAcik = true;
                    TiklanmazlikUygula(); // WS_EX_TRANSPARENT kalkar; NOACTIVATE kalır
                }
                else if (!basili && _altAcik)
                {
                    AltBirakildi();
                    SaatUstundeAyarla(new Point(-1, -1));
                }
                if (_altAcik && bizimUstunde && _pencereAcik)
                {
                    // Stil değişiminden hemen sonra WM_MOUSEMOVE gelmeyebilir: imleci burada da yokla.
                    var p = PointToClient(imlec);
                    if (ClientRectangle.Contains(p)) UzerineGelindi(p);
                }
            }
            else if (_altAcik)
            {
                AltBirakildi();
            }

            bool ustunde = _altAcik && bizimUstunde;
            if (ustunde != _altUstunde)
            {
                _altUstunde = ustunde;
                if (ustunde) _onceki = null;
                GorunurlukUygula();
                if (ustunde) Tick();
            }

            // Çevirmen: sadece Alt (Ctrl/Shift/Win/Tab ile birlikte değil: kısayol, Alt+Tab) ve oyun ŞU AN önde (önbellek değil).
            bool digerTus = TusBasili(Native.VK_CONTROL) || TusBasili(Native.VK_SHIFT) || TusBasili(Native.VK_LWIN) ||
                            TusBasili(Native.VK_RWIN) || TusBasili(Native.VK_TAB);
            bool cevirmenAktif = basili && !digerTus && !bizimUstunde && (_demo || _onPlan.OyunOnde());
            _cevirmen.AltDurumu(cevirmenAktif, imlec);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Alt izleme", ex);
        }
    }

    private static bool TusBasili(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void AltBirakildi()
    {
        if (!_altAcik) return;
        _altAcik = false;
        _hoverBastirId = long.MinValue;
        TiklanmazlikUygula();
        if (!_sabit) DetayKapat();
        else if (_detay is not null && _detay.OyuncuId != _sabitId) DetayAc(_sabitId, sabit: null); // sabitlenen oyuncuya dön
    }

    /// <summary>Overlay ve detay paneli: kilitli ve Alt basılı değilse tıklanmaz.</summary>
    private void TiklanmazlikUygula()
    {
        bool tiklanmaz = _kilitli && !_altAcik;
        if (IsHandleCreated)
        {
            long ex = Native.GetExStyle(Handle);
            long yeni = tiklanmaz ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT;
            if (yeni != ex) Native.SetExStyle(Handle, yeni);
        }
        _detay?.TiklanmazAyarla(tiklanmaz);
    }

    private void UzerineGelindi(Point p)
    {
        SaatUstundeAyarla(p);
        int i = SatirIndeksi(p.Y);
        if (i < 0) return;
        long id = _satirlar[i].Id;
        if (id == _hoverBastirId) return;
        _hoverBastirId = long.MinValue;
        if (_detay is { Visible: true } && _detay.OyuncuId == id) return;
        DetayAc(id, sabit: null);
    }

    private void AltTiklandi(Point p)
    {
        if (OzelTiklama(p)) return;
        int i = SatirIndeksi(p.Y);
        if (i < 0) return;
        long id = _satirlar[i].Id;
        if (_sabit && _sabitId == id && _detay is not null)
        {
            DetayKapat(); // aynı satır: sabitlemeyi kaldır ve kapat
            _hoverBastirId = id; // Alt hâlâ basılı: üzerine gelme hemen yeniden açmasın
        }
        else DetayAc(id, sabit: true);
    }

    /// <summary>Ctrl+Alt+D / tepsi: kendi satırının detayını sabitlenmiş olarak aç ya da kapat (kilitli/kilitsiz).</summary>
    private void Detayim()
    {
        if (!_sertGorunur) return;
        long? id = null;
        for (int i = 0; i < _satirSayisi; i++)
            if (_satirlar[i].Yerel) { id = _satirlar[i].Id; break; }
        if (id is null)
        {
            _tepsi?.Bildir("Kendi satırın henüz yok (dövüş verisi bekleniyor).");
            return;
        }
        if (_detay is { Visible: true } && _detay.OyuncuId == id.Value) DetayKapat();
        else DetayAc(id.Value, sabit: true);
    }

    private void DetayKapat()
    {
        _sabit = false;
        if (_detay is null) return;
        var d = _detay;
        _detay = null;
        d.KapatIstendi -= DetayKapat;
        d.Close();
        d.Dispose();
        Invalidate();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _detay?.Yerlestir(Bounds);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _detay?.Yerlestir(Bounds);
    }

    // ---------------- Komutlar ----------------

    private void KisayolBasildi(int id)
    {
        switch (id)
        {
            case KisayolKilit: KilitDegistir(); break;
            case KisayolGoster: GosterDegistir(); break;
            case KisayolSifirla: Sifirla(); break;
            case KisayolDetayim: Detayim(); break;
            case KisayolCevir: _cevirmen.BolgeCevir(); break;
        }
    }

    private void KilitDegistir()
    {
        _kilitli = !_kilitli;
        if (_kilitli && !_sabit) DetayKapat(); // kilitlenince sabitlenmemiş panel kapanır
        _surukle = false;
        Capture = false;
        _altAcik = false;
        _altUstunde = false;
        TiklanmazlikUygula(); // overlay + sabit panel: kilitliyse tıklanmaz
        if (IsHandleCreated)
        {
            long ex = Native.GetExStyle(Handle) | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetExStyle(Handle, ex);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            int a = _alfa;
            _alfa = -1;
            AlfaUygula(Math.Max(0, a)); // stil değişiminden sonra katman opaklığını yeniden uygula
        }
        _sonSavas = _kilitli ? DateTime.UtcNow : _sonSavas; // kilitleyince hemen soluklaşmasın: 8 sn tam görünür
        Cursor = Cursors.Default;
        AltZamanlayiciGuncelle(); // kilitsizken Alt yoklaması durur
        TepsiGuncelle();
        IpucuGuncelle();
        _onceki = null; // ipucu genişliği değişti: modeli yeniden kur
        GenislikleriSifirla();
        Tick();
        Invalidate();
        AyarKaydet();
    }

    private void GosterDegistir()
    {
        _kullaniciGizledi = !_kullaniciGizledi;
        if (_kullaniciGizledi) DetayKapat();
        GorunurlukUygula(); // SW_HIDE / SW_SHOWNOACTIVATE
        if (_sertGorunur) Tick();
        TepsiGuncelle();
    }

    private void Sifirla()
    {
        if (_kukla.Etkin)
        {
            KuklaBaslat(_kukla.Sure); // test sürerken sıfırlama: test baştan
            return;
        }
        if (_kukla.Durum != KuklaTesti.Hal.Kapali) KuklaIptal();
        try { _motor.Reset(); }
        catch (Exception ex) { HataLog.Yaz("Sıfırlama", ex); }
        GenislikleriSifirla();
        _onceki = null;
        Tick();
    }

    private void AyarKaydet()
    {
        if (WindowState != FormWindowState.Normal) return;
        _ayar.X = Left;
        _ayar.Y = Top;
        _ayar.Yukseklik = Height;
        _ayar.Kilitli = _kilitli;
        _ayar.Kaydet();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _zamanlayici.Stop();
        _kuklaZamanlayici.Stop();
        _alarmZamanlayici.Stop();
        _alarmSecici?.Close();
        AyarKaydet();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= EkranDegisti;
            _zamanlayici.Dispose();
            _altZamanlayici.Dispose();
            _gecisZamanlayici.Dispose();
            _uyariZamanlayici.Dispose();
            _kuklaZamanlayici.Dispose();
            _alarmZamanlayici.Dispose();
            _alarmSecici?.Dispose();
            Zil.Birak();
            _ayarlarFormu?.Dispose();
            _cevirmen.Dispose();
            DetayKapat();
            _kisayol?.Dispose();
            _tepsi?.Dispose();
            _ikonlar.Dispose();
            _yazi?.Dispose(); _yaziYari?.Dispose(); _yaziDps?.Dispose(); _yaziKucuk?.Dispose(); _yaziBaslik?.Dispose(); _yaziBoss?.Dispose();
            _yaziKart?.Dispose();
            foreach (var f in _fircaKoyu) f.Dispose();
            foreach (var f in _fircaAcik) f.Dispose();
            _fircaYerel.Dispose(); _fircaSecili.Dispose(); _fircaSabitSatir.Dispose();
            _fircaBossIz.Dispose(); _fircaBossCan.Dispose(); _fircaBossBilinmiyor.Dispose(); _fircaUyari.Dispose();
            _kalemKilitsiz.Dispose(); _kalemKilitli.Dispose(); _kalemAyirac.Dispose(); _kalemOlum.Dispose();
            _fircaKart.Dispose(); _fircaKukla.Dispose(); _fircaKuklaIz.Dispose(); _fircaAlarmNabiz.Dispose(); _kalemSaat.Dispose();
        }
        base.Dispose(disposing);
    }
}
