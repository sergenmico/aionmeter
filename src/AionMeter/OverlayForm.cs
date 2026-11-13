using System.Diagnostics;
using Microsoft.Win32;

namespace AionMeter;

/// <summary>
/// Kenarlıksız, en üstte, yarı saydam, kendisi çizen overlay. Satırlar için alt kontrol yoktur.
/// Kilitliyken fare olaylarını almaz (WS_EX_TRANSPARENT | WS_EX_LAYERED); hiçbir zaman odak çalmaz.
/// Satır: [sınıf ikonu · ad · CP] | [boss/sn · /sn] | [hasar · pay]; pay çubuğu sınıfın koyu tonunda.
/// Boss varken DPS, hasar ve pay aynı hedeften (boss) gelir. Kilitli değilken satıra tıklayınca detay paneli açılır.
/// Savaş dışında (~8 sn sonra) ayara göre soluklaşır / gizlenir; geçiş 50 ms adımlı ~300 ms.
/// Akıcılık: motor 5 Hz okunur; DPS, hasar, pay, pay çubuğu ve boss canı hedefe ~300 ms'de akar (ease-out), sıra değişiminde
/// satırlar ~140 ms'de kayar. Kare zamanlayıcısı (~60 fps) sadece bir geçiş sürerken çalışır; değer değişmiyorken durur.
/// Rakamlar Bahnschrift ile, sabit genişlikte (RakamYazisi) çizilir.
/// Ek: "sadece benim satırım" modu, kullanıcı renkleri/yazı boyutları, kukla DPS testi (boss bloğunun yerinde
/// geri sayım + sonuç kartı) ve alt satırdaki saatten kurulan kişisel alarm.
/// Dış kenarda her zaman tıklanan küçük sekme (KenarDugmesi) tepsi menüsünü açar; yeni Gizli Küp gelince sesle birlikte
/// ekranın ortasında afiş (KupAfisi) belirir. İkisi de özel tam ekranda hiç açılmaz.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int MaxSatir = 12;
    private const int UstSayisi = 5; // kalabalıkta (> 5 oyuncu) gösterilen ilk sıra sayısı
    private static readonly string[] SiraMetni = { "1", "2", "3", "4", "5" };
    private const int KisayolKilit = 1, KisayolGoster = 2, KisayolSifirla = 3, KisayolDetayim = 4, KisayolCevir = 5, KisayolKukla = 6;
    // Sabit kısayollar (kimlik sırasıyla); kullanıcının kukla kısayolu bunlarla çakışamaz.
    private static readonly string[] SabitKisayollar = { "", "Ctrl+Alt+L", "Ctrl+Alt+H", "Ctrl+Alt+R", "Ctrl+Alt+D", "Ctrl+Alt+T" };
    private static readonly TimeSpan SavasSonrasi = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KartTamGorunur = TimeSpan.FromSeconds(20);
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
    private Font? _yazi, _yaziYari, _yaziKucuk, _yaziBaslik, _yaziBoss;
    // Sayılar: Bahnschrift, sabit genişlikli rakam (yoksa Segoe UI)
    private RakamYazisi? _rDps, _rKucuk, _rBoss, _rYari, _rKart;
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
    private readonly SolidBrush _fircaArka = new(Palet.Arka);              // kayan satırın zemini (alttakini örter)
    private readonly SolidBrush _fircaBossHasarIzi = new(Palet.BossHasarIzi);
    private readonly Pen _kalemIsaretDolu = new(Color.FromArgb(150, Palet.Arka));       // %75/50/25 işareti, dolu kısım üstünde
    private readonly Pen _kalemIsaretBos = new(Color.FromArgb(44, 255, 255, 255));     // boş iz üstünde
    private readonly Pen _kalemOfkeKenar = new(Color.Transparent);
    private readonly SolidBrush _fircaKupUst = new(Palet.Kup);
    private readonly SolidBrush _fircaKupSol = new(KupSimgesi.Sol);
    private readonly SolidBrush _fircaKupSag = new(KupSimgesi.Sag);
    private readonly PointF[] _kupNokta = new PointF[4];
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
        public Akis? A;      // akan değerler (oyuncu kimliğine bağlı)
        public int CizY, CizPay; // son çizimde satırın y'si ve pay çubuğunun genişliği
        public bool Sirali;  // sıralı listede (kayar); sabit üst satır ve "sadece ben" satırı kaymaz
    }

    // ---- Akıcılık ----
    private const int SayiGecisMs = 300, KaymaMs = 140, BossGecisMs = 300, HasarIziBekleMs = 450;
    private const double HasarIziTau = 120;
    private readonly System.Windows.Forms.Timer _kareZamanlayici; // sadece geçiş sürerken (~60 fps)
    private readonly Stopwatch _aniSaat = Stopwatch.StartNew();
    private long _sonKareMs;
    private readonly Dictionary<long, Akis> _akis = new();
    private readonly Stack<Akis> _akisHavuz = new();
    private readonly List<long> _akisSil = new();
    private int _nesil;
    private bool _aniSifirla = true; // sıradaki model kurulumunda geçiş yok (sıfırlama, pencere yeniden görünür, yeni dövüş)
    private bool _sonucOnceki;

    // Boss can çubuğu: gösterilen oran akar; hasar izi bir süre kalır, sonra erir.
    private Gecis _bossCanAni;
    private double _bossIz;
    private long _bossIzBekle, _bossAniId = long.MinValue;
    private float _ofkeK = -1f; // öfkeye yaklaşma 0..1 (≤ 30 sn), öfkede 1; bilinmiyorsa -1
    private bool _bossMaxVar;

    // Gizli Küp: menzildeyken alt satırda küçük küp simgesi; yeni küp gelince ses + ekran ortasında afiş (KupAfisi)
    private int _kupGelisSon = -1;
    private int _kupYakinda;
    private int _kupW;
    private KupAfisi? _kupAfisi; // ilk küpte yaratılır

    // Kenar düğmesi: overlay'in dış kenarındaki sekme; tıklayınca tepsi menüsü yanında açılır (kilitliyken de tıklanır)
    private KenarDugmesi? _kenar;
    private IntPtr _menuOnceki;           // menü açılmadan önce öndeki (bizim olmayan) pencere: kapanınca odak ona döner
    private long _menuKapandiMs = long.MinValue;
    private ToolStripDropDownCloseReason _menuKapanisNedeni;
    private bool _menuBagli;

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
    private readonly bool[] _bilgiRakam = new bool[MaxBilgi]; // saf sayı (süre): rakam yazısıyla
    private int _bilgiSayisi;
    private TimeSpan _oncekiSure;

    private MeterSnapshot? _onceki;   // modelin kurulduğu görüntü (yeniden kurmak için null'a çekilebilir)
    private MeterSnapshot? _sonAnlik; // motordan alınan son görüntü (hiç null'a çekilmez: detay paneli bunu okur)
    private long _olumBossId = long.MinValue;
    private DateTime _olumZamani;
    private int _tikSayaci;
    private bool _kilitli;

    // Öfke uyarısı: öfkeye ≤ 30 sn kala ve "geç" durumuna geçince boss bloğu 3 kez yumuşakça yanıp söner.
    private readonly System.Windows.Forms.Timer _uyariZamanlayici;
    private readonly Stopwatch _uyariSaat = new();
    private long _uyariBossId = long.MinValue;
    private bool _uyariOfkeVerildi, _uyariGecVerildi;
    private TimeSpan _uyariSonSure; // dövüş süresi geri giderse (wipe / yeni deneme) uyarılar yeniden verilebilir
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
    private readonly Zamanlayici.ZamanlayiciDenetleyici _etkinlik; // etkinlik geri sayımı ve boss listesi
    private bool _kullaniciGizledi, _sadeceOyunda, _sertGorunur = true, _pencereAcik = true, _tamEkran;
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

        _zamanlayici = new System.Windows.Forms.Timer { Interval = 200 }; // 5 Hz motor okuma
        _zamanlayici.Tick += (_, _) => Tick();
        _kareZamanlayici = new System.Windows.Forms.Timer { Interval = 15 }; // ~60 fps, sadece geçiş sürerken
        _kareZamanlayici.Tick += (_, _) => KareTick();
        _cevirmen = new Cevirmen.CevirmenDenetleyici(ayar, () => _dpiOlcek, BizimPencereler, m => _tepsi?.Bildir(m));
        _etkinlik = new Zamanlayici.ZamanlayiciDenetleyici(ayar, () => _olcek, () => _onPlan.OyunPenceresi);
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
            sn => KuklaTepsiden(sn > 0 ? sn : -1), KuklaIptal, AlarmSeciciAc, KuklaKisayolBasildi);
        _tepsi.Menu.Opening += (_, _) => TepsiGuncelle(); // alarmın kalan süresi menü açılırken güncellenir
        _tepsi.BossZamanlari = () => { _etkinlik.BossListesiDegistir(); EtkinlikTick(); };
        if (!_demo) OnPlanKancasiKur();
        _kisayol = new KisayolPenceresi();
        _kisayol.Basildi += KisayolBasildi;
        var alinamayan = new List<string>(3);
        if (!_kisayol.Kaydet(KisayolKilit, Keys.L)) alinamayan.Add("Ctrl+Alt+L");
        if (!_kisayol.Kaydet(KisayolGoster, Keys.H)) alinamayan.Add("Ctrl+Alt+H");
        if (!_kisayol.Kaydet(KisayolSifirla, Keys.R)) alinamayan.Add("Ctrl+Alt+R");
        if (!_kisayol.Kaydet(KisayolDetayim, Keys.D)) alinamayan.Add("Ctrl+Alt+D");
        if (!_kisayol.Kaydet(KisayolCevir, Keys.T)) alinamayan.Add("Ctrl+Alt+T");
        if (!KuklaKisayolKaydet(_ayar.KuklaKisayol)) alinamayan.Add(_ayar.KuklaKisayol);
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
        KenarYarat();
        _zamanlayici.Start();
    }

    // ---------------- Görünürlük ----------------

    private int TamAlfa => Math.Clamp(_ayar.Opaklik * 255 / 100, 40, 255);
    private int SolukAlfa => Math.Clamp(_ayar.SolukOpaklik * 255 / 100, 20, 255);

    /// <summary>Savaş dışı kuralından bağımsız olarak tam görünmesi gereken durumlar.</summary>
    private bool TamGorunmeli()
    {
        if (_ayarlarFormu is not null) return true;                 // ayar yaparken nasıl görüneceği görünsün
        if (_altUstunde) return true;                               // kilitliyken Alt ile göz atma
        if (!_kilitli && (_fareUstunde || _surukle)) return true;   // kilitsiz: fare üstündeyken / sürüklerken
        if (_sabit && _detay is { Visible: true }) return true;     // sabitlenmiş detay paneli
        if (_kukla.Etkin) return true;                              // kukla testi sürerken
        if (_kukla.Durum == KuklaTesti.Hal.Sonuc && DateTime.UtcNow - _kukla.SonucZamani < KartTamGorunur)
            return true;                                            // sonuç kartı ilk ~20 sn; sonra normal kural
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
        // Oyun özel tam ekrandayken üstünde hiçbir pencere açılmaz: DLSS Kare Oluşturma ile oyun çöküyor (OnPlanIzleyici).
        bool tamEkran = !_demo && _onPlan.OzelTamEkran();
        bool sert = !tamEkran && (_ayarlarFormu is not null || (!_kullaniciGizledi && (!_sadeceOyunda || _onPlan.OyunOnde())));
        bool tamEkranDegisti = tamEkran != _tamEkran;
        _tamEkran = tamEkran;
        if (tamEkranDegisti && tamEkran)
        {
            _cevirmen.PencereleriKapat();
            TamEkranUyar();
        }
        // Küp afişi: tam ekrana geçildi ya da oyun arkaya düştüyse hemen kapanır.
        if (_kupAfisi is { Acik: true } && (tamEkran || !(_demo || _onPlan.OyunOnde()))) _kupAfisi.Kapat();
        if (sert != _sertGorunur || tamEkranDegisti)
        {
            _sertGorunur = sert;
            AltZamanlayiciGuncelle(); // tam ekranda Alt ile çeviri de durur
        }
        if (!sert)
        {
            // Ctrl+Alt+H / oyun arkada: geçiş yok, hemen gizle.
            _gecisZamanlayici.Stop();
            _alfa = -1;
            AlfaUygula(0); // katman opaklığı gerçekten 0: geri gösterilince eski opaklıkla bir an parlamaz
            PencereGizle();
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
            _alfa = -1;
            AlfaUygula(0); // önbellekteki değer ne olursa olsun 0'dan başla
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
        _kenar?.OverlayAlfa(alfa); // kenar düğmesi overlay'in opaklığını izler (fare üstündeyken tam görünür)
    }

    private static readonly TimeSpan TamEkranUyariAraligi = TimeSpan.FromMinutes(10);
    private DateTime _tamEkranUyarildi = DateTime.MinValue;

    /// <summary>Tepside bir kez (10 dk'da en fazla bir) söyler; oyun tam ekrandayken Windows bildirimi oyun bırakılınca gösterebilir.</summary>
    private void TamEkranUyar(bool zorla = false)
    {
        DateTime simdi = DateTime.UtcNow;
        if (!zorla && simdi - _tamEkranUyarildi < TamEkranUyariAraligi) return;
        _tamEkranUyarildi = simdi;
        _tepsi?.Bildir("Oyun tam ekran modunda. Oyunun çökmemesi için sayaç gizlendi. " +
            "Oyunun grafik ayarlarından ekran modunu \"Pencereli tam ekran\" yapın; sayaç yeniden görünür.");
    }

    private void PencereGoster()
    {
        if (_pencereAcik) return;
        _pencereAcik = true;
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); // asla etkinleştirme
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_SHOWNOACTIVATE);
        if (_kenar is not null)
        {
            _kenar.Yerlestir(Bounds, _olcek);
            _kenar.PencereGoster(); // overlay ile birlikte (GorunurlukUygula yalnız tam ekran değilken buraya gelir)
        }
        _onceki = null; // gizliyken kaçan değişiklikler için modeli yeniden kur
        _aniSifirla = true; // gizliyken eskiyen değerlerden akmasın: doğrudan güncel değer
    }

    private void PencereGizle()
    {
        if (!_pencereAcik) return;
        _pencereAcik = false;
        _kareZamanlayici.Stop(); // görünmeyen pencere için kare çizilmez
        if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_HIDE);
        if (_kenar is not null)
        {
            _kenar.PencereGizle();
            _kenar.UstundeAyarla(false); // gizliyken MouseLeave gelmez
        }
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    /// <summary>Kenar düğmesi: overlay gösterildikten sonra bir kez yaratılır; overlay şu an görünürse o da görünür.</summary>
    private void KenarYarat()
    {
        if (_kenar is not null || !IsHandleCreated) return;
        try
        {
            _kenar = new KenarDugmesi();
            _kenar.Tiklandi += KenarTiklandi;
            _kenar.OverlayAlfa(Math.Max(0, _alfa));
            _kenar.Yerlestir(Bounds, _olcek);
            if (_pencereAcik && !_tamEkran) _kenar.PencereGoster();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Kenar düğmesi", ex);
        }
    }

    /// <summary>Kenar düğmesine tıklandı: tepsi menüsü düğmenin yanında, overlay'den dışarı doğru açılır (açıksa kapanır).</summary>
    private void KenarTiklandi()
    {
        if (_kenar is null || _tepsi is null) return;
        // Menü açıkken düğmeye basmak menüyü (fare basılınca) kapatır; bırakılınca yeniden açılmasın.
        if (_menuKapanisNedeni == ToolStripDropDownCloseReason.AppClicked && Environment.TickCount64 - _menuKapandiMs < 400) return;
        if (_tepsi.Menu.Visible)
        {
            _tepsi.Menu.Close(ToolStripDropDownCloseReason.CloseCalled);
            return;
        }
        var a = _kenar.Alan;
        if (_kenar.Sagda) MenuAc(_kenar, new Point(a.Right, a.Top), ToolStripDropDownDirection.BelowRight);
        else MenuAc(_kenar, new Point(a.Left, a.Top), ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>
    /// Tepsi menüsünü overlay'den (sağ tık) ya da kenar düğmesinden açar. Odak almayan pencereden açılan menü klavyeyi almaz
    /// ve oyuna tıklanınca kapanmaz; tepsi simgesinin yaptığı gibi menüden önce kendi penceremiz öne alınır. Menü kapanınca
    /// (bir öğe başka pencere açmadıysa) odak önceki pencereye, yani oyuna geri verilir. Tam ekranda bu pencereler zaten yoktur.
    /// </summary>
    private void MenuAc(Form sahip, Point konum, ToolStripDropDownDirection yon)
    {
        if (_tepsi is null) return;
        var menu = _tepsi.Menu;
        if (!_menuBagli)
        {
            _menuBagli = true;
            menu.Closed += MenuKapandi;
        }
        IntPtr on = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(on, out uint pid);
        _menuOnceki = on != IntPtr.Zero && pid != (uint)Environment.ProcessId ? on : IntPtr.Zero;
        if (_menuOnceki != IntPtr.Zero) Cevirmen.OdakYardimcisi.OneGetir(sahip);
        menu.Show(konum, yon);
    }

    private void MenuKapandi(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        _menuKapandiMs = Environment.TickCount64;
        _menuKapanisNedeni = e.CloseReason;
        IntPtr onceki = _menuOnceki;
        _menuOnceki = IntPtr.Zero;
        if (onceki == IntPtr.Zero || IsDisposed) return;
        // Öğenin işi (ör. Ayarlar) bu iletide bir pencere öne aldıysa ona dokunma; almadıysa odağı oyuna geri ver.
        BeginInvoke(() =>
        {
            IntPtr fg = Native.GetForegroundWindow();
            bool bizimKucuk = fg == IntPtr.Zero || fg == Handle ||
                              (_kenar is { IsHandleCreated: true } && fg == _kenar.Handle) ||
                              (_tepsi?.Menu is { IsHandleCreated: true } m && fg == m.Handle);
            if (bizimKucuk) Cevirmen.OdakYardimcisi.GeriVer(onceki);
        });
    }

    /// <summary>
    /// Odak almayan pencereden açılan sağ tık menüsü, kullanıcı oyuna tıklayınca kendiliğinden kapanmayabilir:
    /// menü açıkken ön plandaki pencere bizim süreçte değilse kapat.
    /// </summary>
    private void MenuyuGerekirseKapat()
    {
        if (_tepsi?.Menu is not { Visible: true } menu) return;
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        // Menüyü açarken öne geçilemediyse (Windows ön plan kilidi) oyun hâlâ önde: yalnız menü dışına tıklanınca kapat.
        if (_menuOnceki != IntPtr.Zero && fg == _menuOnceki && !MenuDisinaTiklandi(menu)) return;
        menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
    }

    /// <summary>Fare düğmesi şu an basılı ve imleç menünün, açık alt menülerin ve kenar düğmesinin dışında mı?</summary>
    private bool MenuDisinaTiklandi(ContextMenuStrip menu)
    {
        // 0x8000 şu an basılı, 0x0001 son yoklamadan beri basıldı: 200 ms arası kısa tık da kaçmasın (denetim bulgusu 6)
        if (!TusBasildi(Native.VK_LBUTTON) && !TusBasildi(Native.VK_RBUTTON)) return false;
        var imlec = Cursor.Position;
        if (menu.Bounds.Contains(imlec)) return false;
        if (_kenar is { Acik: true } && _kenar.Alan.Contains(imlec)) return false;
        foreach (ToolStripItem o in menu.Items)
            if (o is ToolStripMenuItem { HasDropDownItems: true } mi && mi.DropDown.Visible && mi.DropDown.Bounds.Contains(imlec))
                return false;
        return true;
    }

    /// <summary>Detay paneli overlay'in yanına yerleşirken kenar düğmesini de kapsasın (üstüne binmesin).</summary>
    private Rectangle DetayCapasi =>
        _kenar is { Acik: true } k && !k.Alan.IsEmpty ? Rectangle.Union(Bounds, k.Alan) : Bounds;

    private void TepsiGuncelle()
    {
        _tepsi?.Guncelle(_kilitli, !_kullaniciGizledi, _denetleniyor);
        _tepsi?.KuklaGuncelle(_ayar.KuklaSure, _ayar.KuklaOzel, _ayar.KuklaOzelSure, _kukla.Etkin,
            _kukla.Durum == KuklaTesti.Hal.Sonuc, _ayar.KuklaKisayol, AlarmKalan());
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
        foreach (var r in _etkinlik.Pencereler) yield return r;
        if (_kupAfisi is { Acik: true }) yield return _kupAfisi.Alan; // OCR "Küp var" yazısını çevirmeye çalışmasın
        if (_kenar is { Acik: true }) yield return _kenar.Alan;
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
            YaziBitti = YaziBitti,
            KuklaBaslat = () => { KuklaBaslat(_ayar.KuklaSuresi); _ayarlarFormu?.Close(); }, // vurmaya gitsin
            KuklaIptal = KuklaIptal,
            KuklaEtkin = () => _kukla.Etkin,
            AlarmKur = () => { _ayarlarFormu?.Close(); AlarmSeciciAc(); },
            AlarmIptal = AlarmIptal,
            AlarmKalan = AlarmKalan,
            KuklaKisayol = () => _ayar.KuklaKisayol,
            KuklaKisayolAyarla = KuklaKisayolAyarla,
            KisayolYakalama = KisayolYakalama,
            Ikonlar = _ikonlar,
        };
        IntPtr oyun = _onPlan.OyunOnde() ? _onPlan.SonOyunPenceresi : IntPtr.Zero; // oyun önde değilse odağa dokunma
        _ayarlarFormu = new AyarlarFormu(_ayar, baglanti, _dpiOlcek);
        _ayarlarFormu.FormClosed += (_, _) =>
        {
            var f = _ayarlarFormu;
            _ayarlarFormu = null;
            f?.Dispose();
            _solukOnizlemeBitis = DateTime.MinValue;
            _sonSavas = DateTime.MinValue; // pencere kapanınca hemen kurala dön
            Cevirmen.OdakYardimcisi.GeriVer(oyun); // açılırken oyun öndeyse odağı ona geri ver
            GorunurlukUygula();
            TepsiGuncelle(); // ayarlar açıkken Ctrl+Alt+H basıldıysa tepsi metni güncel kalsın
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
            if (_tikSayaci == 15) GC.Collect(); // açılıştan ~3 sn sonra bir kez: başlangıç çöplerini bırak
            FareYokla();
            GorunurlukUygula();
            EtkinlikTick(); // overlay savaş dışında gizli/soluk olsa da geri sayım çalışır; tam ekranda/gizliyken kapalı
            MenuyuGerekirseKapat(); // overlay gizlense de menü oyunun üstünde asılı kalmasın (denetim bulgusu 6)
            AltZamanlayiciGuncelle();
            if (!_sertGorunur)
            {
                // Gizliyken (Ctrl+Alt+H / oyun arkada) motor çizim için okunmaz; küp sesi açıksa saniyede bir sadece küp sayacına bakılır.
                if (_ayar.KupSesi && _tikSayaci % 5 == 0) KupDenetle(_motor.Snapshot());
                return;
            }

            var s = _motor.Snapshot();
            _sonAnlik = s;
            KupDenetle(s); // savaş dışında gizli/soluk olsa da ses çalar
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

            if (_tikSayaci % 10 == 0)
            {
                // Bazı oyunlar kendini öne alır; en üstte kalmayı ara sıra (~2 sn) yenile (etkinleştirmeden).
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                if (_detay is { Visible: true, IsHandleCreated: true })
                    Native.SetWindowPos(_detay.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                _kenar?.EnUsteAl();
            }

            if (_detay is { Visible: true } && (_tikSayaci & 1) == 0) _detay.Yenile(); // detay sadece açıkken ~2,5 Hz

            bool bossGoster = _kukla.Durum == KuklaTesti.Hal.Kapali && BossGosterilsinMi(s); // test ve kart sürerken boss bloğu yok
            if (_onceki is not null && bossGoster == _bossGoster && Ayni(_onceki, s)) return;

            _onceki = s;
            _bossGoster = bossGoster;
            ModelKur(s);
            BoyutAyarla();
            AniBaslat();
            Invalidate();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Zamanlayıcı", ex);
        }
    }

    /// <summary>Gizli Küp: açılıştan beri gelen küp sayısı arttıysa (ayar açıksa) kısa küp sesi; menzildeki küp sayısı rozet içindir.</summary>
    private void KupDenetle(MeterSnapshot s)
    {
        // Ses kapalıyken sayaç izlenmez (gizliyken motor okunmaz): ses açılınca ilk okuma yalnız eşitler, birikmiş küpler için çalmaz.
        if (!_ayar.KupSesi) { _kupGelisSon = -1; return; }
        int gelis = s.KupGelis;
        if (_kupGelisSon >= 0 && gelis > _kupGelisSon)
        {
            Zil.Kup(_ayar.KupSes);
            KupAfisiGoster(); // ses ve yazı aynı anda
        }
        _kupGelisSon = gelis;
    }

    /// <summary>
    /// Ekranın ortasında (oyun penceresinin, yoksa overlay'in ekranının) "Küp var" afişi.
    /// Oyun özel tam ekrandayken ya da önde değilken gösterilmez (tam ekranda pencere açmak oyunu çökertebilir).
    /// Overlay gizli/soluk olsa da gösterilir: sesle birlikte haber verir.
    /// </summary>
    private void KupAfisiGoster()
    {
        try
        {
            if (!IsHandleCreated || _tamEkran) return;
            if (!_demo && (_onPlan.OzelTamEkran() || !_onPlan.OyunOnde())) return;
            Rectangle hedef = Rectangle.Empty;
            IntPtr oyun = _demo ? IntPtr.Zero : _onPlan.OyunPenceresi; // alıntı aracının penceresi değil (denetim bulgusu 10)
            if (oyun != IntPtr.Zero && Native.IsWindow(oyun) && Native.GetWindowRect(oyun, out Native.RECT r))
            {
                // Pencereli oyunda pencerenin ortası; ekran dışına taşan kısımlar sayılmaz.
                var ekran = Screen.FromHandle(oyun).Bounds;
                hedef = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), ekran);
                if (hedef.Width < 200 || hedef.Height < 120) hedef = ekran;
            }
            if (hedef.IsEmpty) hedef = Screen.FromRectangle(Bounds).Bounds;
            _kupAfisi ??= new KupAfisi();
            _kupAfisi.Goster(hedef);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp afişi", ex);
        }
    }

    // ---------------- Akıcılık: kare zamanlayıcısı ----------------

    /// <summary>Model kurulduktan sonra: ara kare metinlerini hazırla, bir geçiş sürüyorsa kare zamanlayıcısını başlat.</summary>
    private void AniBaslat()
    {
        long simdi = _aniSaat.ElapsedMilliseconds;
        bool surer = AniIlerle(simdi);
        if (surer && _pencereAcik)
        {
            if (!_kareZamanlayici.Enabled) _kareZamanlayici.Start();
        }
        else if (!surer) _kareZamanlayici.Stop();
    }

    /// <summary>~60 fps: geçişleri ilerletir ve yeniden çizer; hepsi hedefe varınca durur (boşta CPU ~0).</summary>
    private void KareTick()
    {
        try
        {
            if (!_pencereAcik || !IsHandleCreated)
            {
                _kareZamanlayici.Stop();
                return;
            }
            bool surer = AniIlerle(_aniSaat.ElapsedMilliseconds);
            var kirli = KirliAlan();
            if (!kirli.IsEmpty) Invalidate(kirli); // sadece değişen alan: Ciz kırpma dışındaki metinleri atlar
            if (!surer) _kareZamanlayici.Stop();
        }
        catch (Exception ex)
        {
            _kareZamanlayici.Stop();
            HataLog.Yaz("Kare", ex);
        }
    }

    /// <summary>Tüm geçişleri ilerletir (satırlar + boss can çubuğu ve hasar izi). Bir şey sürüyorsa true.</summary>
    private bool AniIlerle(long simdi)
    {
        bool surer = false;
        foreach (var a in _akis.Values)
        {
            a.Ilerle(simdi);
            surer |= a.Surer;
        }

        double dt = Math.Clamp(simdi - _sonKareMs, 0, 100);
        _sonKareMs = simdi;
        double izOnce = _bossIz;
        bool bossDegisti = _bossCanAni.Ilerle(simdi);
        double gosterilen = _bossCanAni.Deger;
        if (_bossIz > gosterilen)
        {
            if (simdi >= _bossIzBekle)
            {
                _bossIz = gosterilen + (_bossIz - gosterilen) * Math.Exp(-dt / HasarIziTau);
                if (_bossIz - gosterilen < 0.002) _bossIz = gosterilen; // ~yarım pikselden az kaldı: bitti
            }
        }
        else _bossIz = gosterilen;
        _bossDegisti = bossDegisti || _bossIz != izOnce;
        return surer || _bossCanAni.Aktif || _bossIz > gosterilen;
    }

    private bool _bossDegisti;

    // Son çizimin satır geometrisi (kare zamanlayıcısının kirli alanı için)
    private int _cizX0, _cizX1, _cizDpsX, _cizSatirAlt;
    private Rectangle _bossCubukAlani, _klip;

    /// <summary>Son adımda değişenlerin kapsadığı alan: akan sayı sütunları, çubuk ucunun gittiği aralık, kayan satırlar, boss çubuğu.</summary>
    private Rectangle KirliAlan()
    {
        if (_cizX1 <= _cizX0) return ClientRectangle; // henüz çizilmedi
        Rectangle r = Rectangle.Empty;
        void Ekle(Rectangle x) => r = r.IsEmpty ? x : Rectangle.Union(r, x);
        int rowH = RowH, gen = _cizX1 - _cizX0;
        bool kayma = false;
        for (int i = 0; i < _satirSayisi; i++)
        {
            var m = _satirlar[i];
            if (m.A is not { } a) continue;
            if (m.Sirali && a.YDegisti) kayma = true;
            if (a.SayiDegisti) Ekle(new Rectangle(_cizDpsX - P(2), m.CizY, _cizX1 - _cizDpsX + P(2), rowH));
            if (a.OranDegisti)
            {
                int yeni = (int)(gen * Math.Clamp(a.Oran.Deger, 0, 1));
                int x = _cizX0 + Math.Min(yeni, m.CizPay), sag = _cizX0 + Math.Max(yeni, m.CizPay);
                Ekle(Rectangle.FromLTRB(x - 1, m.CizY, sag + 1, m.CizY + rowH));
            }
        }
        if (kayma) Ekle(Rectangle.FromLTRB(0, _satirlarUst, ClientSize.Width, _cizSatirAlt));
        if (_bossDegisti && !_bossCubukAlani.IsEmpty) Ekle(Rectangle.Inflate(_bossCubukAlani, P(3), P(3)));
        return r;
    }

    /// <summary>Bu dikdörtgen kırpma alanına giriyor mu (kare zamanlayıcısının kısmi çizimlerinde pahalı metinler atlanır).</summary>
    private bool Gor(Rectangle r) => _klip.IntersectsWith(r);

    private Akis AkisAl(long id)
    {
        if (_akis.TryGetValue(id, out var a)) return a;
        a = _akisHavuz.Count > 0 ? _akisHavuz.Pop() : new Akis();
        a.Id = id;
        a.Nesil = -1; // yeni: ilk değerler doğrudan
        a.YuvaVar = false;
        _akis[id] = a;
        return a;
    }

    /// <summary>Satırın hedef değerlerini akışa verir. ani: geçiş yok, doğrudan hedef (yeni oyuncu, sıfırlama, yeni dövüş).
    /// yuva: sıralı listedeki yer (kayma animasyonu için; sabit satırlarda kullanılmaz).</summary>
    private void AkisHedefle(SatirModel m, double dps, double dps2, long toplam, double pay, bool payVar, int yuva, bool ani, long simdi)
    {
        var a = AkisAl(m.Id);
        m.A = a;
        if (a.Nesil == _nesil)
        {
            // Aynı oyuncu bu kurulumda ikinci kez (kalabalıkta sabit üst satır + sıradaki satırı): değerler aynı, sadece yuva.
            if (m.Sirali) YuvaHedefle(a, yuva, ani, simdi);
            return;
        }
        bool yeni = a.Nesil < 0;
        a.Nesil = _nesil;
        a.PayVar = payVar;
        // Yeni oyuncu, sıfırlama ya da toplam geri gitti (yeni deneme): eski değerden geri sayım yerine doğrudan.
        if (yeni || ani || toplam < a.Toplam.Hedef - 0.5)
        {
            a.Hepsini(dps, dps2, toplam, pay, m.Oran, yuva);
            a.YuvaVar = m.Sirali;
            if (m.Sirali) a.SiraliNesil = _nesil;
            return;
        }
        a.Dps.Hedefle(dps, simdi, SayiGecisMs);
        a.Dps2.Hedefle(dps2, simdi, SayiGecisMs);
        a.Toplam.Hedefle(toplam, simdi, SayiGecisMs);
        if (payVar) a.Pay.Hedefle(pay, simdi, SayiGecisMs); else a.Pay.Ani(pay);
        a.Oran.Hedefle(m.Oran, simdi, SayiGecisMs);
        if (m.Sirali) YuvaHedefle(a, yuva, false, simdi);
    }

    /// <summary>Sıralı listedeki yer: önceden sıralı listede değilse (listeye yeni girdi) kaymadan yerine konur.</summary>
    private void YuvaHedefle(Akis a, int yuva, bool ani, long simdi)
    {
        if (ani || !a.YuvaVar) a.Y.Ani(yuva);
        else a.Y.Hedefle(yuva, simdi, KaymaMs);
        a.YuvaVar = true;
        a.SiraliNesil = _nesil;
    }

    /// <summary>Bu kurulumda görünmeyen oyuncuların akışı bırakılır (geri gelirse doğrudan değerle başlar).</summary>
    private void AkisTemizle()
    {
        _akisSil.Clear();
        foreach (var (id, a) in _akis)
        {
            if (a.Nesil != _nesil) _akisSil.Add(id);
            else if (a.SiraliNesil != _nesil) a.YuvaVar = false; // sadece sabit satırda kaldı: listeye dönünce kaymadan girer
        }
        foreach (long id in _akisSil)
        {
            _akisHavuz.Push(_akis[id]);
            _akis.Remove(id);
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
        if (a.KupYakinda != b.KupYakinda) return false;
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
        // Geçişsiz (doğrudan doğru değer): sıfırlama / pencere yeniden göründü, boss modu değişti, dövüş bitti ya da yeni dövüş.
        bool ani = _aniSifirla || bossModu != _bossModu || s.FightOver != _sonucOnceki || s.State != EngineState.Connected
                   || s.FightDuration < _oncekiSure;
        _aniSifirla = false;
        _sonucOnceki = s.FightOver;
        long simdi = _aniSaat.ElapsedMilliseconds;
        _nesil++;
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
            if (kalabalik != _kalabalik || sadeceBen != _sadeceBenModu)
            {
                GenislikleriSifirla();
                ani = true; // düzen değişti: satırlar kaymadan yerine
            }
            _kalabalik = kalabalik;
            _sadeceBenModu = sadeceBen;
            int ustN = sadeceBen ? 0 : kalabalik ? UstSayisi : Math.Min(_sirali.Count, MaxSatir);
            int n = 0;
            _sabitUst = kalabalik && yerelSira >= 0;
            if (_sabitUst || sadeceBen) SatirDoldur(_satirlar[n++], _sirali[yerelSira], "#" + (yerelSira + 1).ToString(Bicim.Tr), false, 0);
            for (int i = 0; i < ustN; i++) SatirDoldur(_satirlar[n++], _sirali[i], kalabalik ? SiraMetni[i] : "", true, i);

            void SatirDoldur(SatirModel m, PlayerDamage p, string sira, bool sirali, int yuva)
            {
                long t = Gosterilen(p, bossModu);
                m.Sirali = sirali;
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
                // Akan değerler: kesin metinle aynı sayılar (Bicim.Sayi yuvarlar, Bicim.Oran yüzde × 100)
                AkisHedefle(m, DpsHedef(bossModu ? p.BossDps : p.Dps), bossModu ? DpsHedef(p.Dps) : 0, t,
                    partiToplam > 0 ? (double)t / partiToplam * 100.0 : 0, partiToplam > 0, yuva, ani, simdi);
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

        AkisTemizle();

        if (_bossGoster && s.Boss is { } b) BossModelKur(s, b, ani, simdi);
        else
        {
            _ofkeK = -1f;
            _bossAniId = long.MinValue;
        }
        if (_kukla.Etkin) KuklaModelKur();
        else if (_kukla.Sonuc is { } ks) KartModelKur(ks);
        _sure = (s.FightOver ? "Sonuç · " : "") + Bicim.Sure(s.FightDuration);
        _sureRakam = !s.FightOver;
        _kupYakinda = Math.Max(0, s.KupYakinda); // menzildeyken alt satırda yalnız küp simgesi (yazı ekran ortasındaki afişte)
        GenislikleriOlc();
    }

    private bool _sureRakam;

    /// <summary>DPS'in gösterilen tam sayısı (Bicim.Sayi ile aynı): NaN/sonsuz/eksi 0, sonra yuvarlanır.</summary>
    private static double DpsHedef(double d) => double.IsNaN(d) || double.IsInfinity(d) || d < 0 ? 0 : Math.Round(d);

    private void BossModelKur(MeterSnapshot s, BossHp b, bool ani, long simdi)
    {
        _bossAd = string.IsNullOrWhiteSpace(b.Name) ? "Boss" : b.Name;
        _bossOldu = b.Dead;
        _bossBilinmiyor = b.CanBilinmiyor && !b.Dead;
        if (b.Id != _uyariBossId || s.FightDuration < _uyariSonSure)
        {
            _uyariBossId = b.Id;
            _uyariOfkeVerildi = _uyariGecVerildi = false;
        }
        _uyariSonSure = s.FightDuration;

        BilgiEkle(Bicim.Sure(s.FightDuration), Palet.Soluk, rakam: true);
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
                        // Histerezis: öfke yeniden 40 sn'nin üstüne çıkarsa / ekip 10 sn pay ile yetişir hale gelirse
                        // uyarı yeniden kurulur (sınırda gidip gelirken her tikte yanıp sönmez).
                        if (ofke.TotalSeconds > 40) _uyariOfkeVerildi = false;
                        if (ttk is TimeSpan k3 && ofke - k3 > TimeSpan.FromSeconds(10)) _uyariGecVerildi = false;
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
        _bossMaxVar = b.Max > 0;

        // Can çubuğu akışı: hasar alınınca gösterilen oran ~300 ms'de iner; inen kısım hasar izi olarak kısa süre açık renkte kalır,
        // sonra erir. Yeni boss / sıfırlama / ölüm / can bilinmiyor / can arttı (wipe, yeni deneme): doğrudan.
        double hedef = _bossOldu ? 0 : _bossOran;
        if (ani || b.Id != _bossAniId || _bossOldu || _bossBilinmiyor || !_bossMaxVar || hedef > _bossCanAni.Hedef + 1e-9)
        {
            _bossCanAni.Ani(hedef);
            _bossIz = hedef;
        }
        else if (hedef < _bossCanAni.Hedef)
        {
            if (_bossIz <= _bossCanAni.Deger + 1e-9)
            {
                _bossIz = _bossCanAni.Deger;            // yeni iz: bir süre bekler, sonra erir
                _bossIzBekle = simdi + HasarIziBekleMs;
            }
            _bossCanAni.Hedefle(hedef, simdi, BossGecisMs);
        }
        _bossAniId = b.Id;

        // Öfkeye yaklaşma: son 30 sn'de çubuğun kenarı yavaşça kırmızıya döner, öfkedeyken kırmızı kalır (öfke darbesini tamamlar).
        _ofkeK = -1f;
        if (!b.Dead && b.TimeToRage is TimeSpan ofkeye)
        {
            double sn = ofkeye.TotalSeconds;
            _ofkeK = sn <= 0 ? 1f : sn < 30 ? (float)(1.0 - sn / 30.0) : 0f;
        }
        if (_ofkeK > 0) _kalemOfkeKenar.Color = Color.FromArgb((int)Math.Round(40 + 200 * _ofkeK), Palet.Kirmizi);
    }

    private void BilgiEkle(string metin, Color renk, bool rakam = false)
    {
        if (_bilgiSayisi >= MaxBilgi) return;
        _bilgiMetin[_bilgiSayisi] = metin;
        _bilgiRenk[_bilgiSayisi] = renk;
        _bilgiRakam[_bilgiSayisi] = rakam;
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

    /// <summary>Uyarı süresince sadece boss bloğu yeniden çizilir.</summary>
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
            // Yazı boyutu değişti: overlay canlı değişir; açık detay paneli kapanır ve kaydırıcı bırakılınca
            // (YaziBitti) aynı oyuncu ve aynı sabitlemeyle yeni yazıyla bir kez yeniden açılır.
            if (_detay is { Visible: true } d)
            {
                _bekleyenDetay = d.OyuncuId;
                _bekleyenSabit = _sabit;
            }
            OlcekAyarla();
            BoyutAyarla();
        }
        else _detay?.RenkYenile();
        GenislikleriSifirla();
        _aniSifirla = true;
        _onceki = null;
        Tick();
        Invalidate();
    }

    private long? _bekleyenDetay; // yazı kaydırılırken kapanan detay paneli (bırakınca yeniden açılır)
    private bool _bekleyenSabit;

    /// <summary>Yazı kaydırıcısı bırakıldı ya da renk/yazı sıfırlandı: bekleyen detay paneli yeni yazıyla açılır.</summary>
    private void YaziBitti()
    {
        if (_bekleyenDetay is not long id) return;
        _bekleyenDetay = null;
        DetayAc(id, _bekleyenSabit);
    }

    // ---------------- Kukla testi ----------------

    // Kukla kısayolu (varsayılan Ctrl+Alt+K; Ayarlar → Kukla ve alarm'dan değiştirilir ya da kapatılır)
    private bool _kuklaKisayolKayitli, _kisayolYakalaniyor;

    /// <summary>Kısayol: ayardaki süreyle testi başlat; test sürerken ya da kart açıkken iptal et / kartı kapat.</summary>
    private void KuklaKisayolBasildi()
    {
        if (_kukla.Durum != KuklaTesti.Hal.Kapali) KuklaIptal();
        else KuklaBaslat(_ayar.KuklaSuresi);
    }

    /// <summary>Kombinasyonu kaydeder ("" = kısayol yok, başarılı sayılır). Çakışmada false.</summary>
    private bool KuklaKisayolKaydet(string metin)
    {
        if (_kisayol is null || metin.Length == 0) return true;
        if (!Kisayol.Coz(metin, out uint d, out Keys t)) return false;
        _kuklaKisayolKayitli = _kisayol.Kaydet(KisayolKukla, d, t);
        return _kuklaKisayolKayitli;
    }

    private void KuklaKisayolKaldir()
    {
        if (!_kuklaKisayolKayitli) return;
        _kisayol?.Kaldir(KisayolKukla);
        _kuklaKisayolKayitli = false;
    }

    /// <summary>
    /// Ayarlardan: yeni kısayol ("" = kapat). Sabit kısayollarla çakışırsa ya da başka program almışsa reddedilir,
    /// eski kombinasyon geri kaydedilir. Hata metni döner; başarılıysa null (ayar kaydedilir).
    /// </summary>
    private string? KuklaKisayolAyarla(string yeni)
    {
        if (yeni.Length > 0)
        {
            if (Kisayol.Duzgun(yeni) is not string d) return "Geçersiz kısayol";
            yeni = d;
            if (Array.IndexOf(SabitKisayollar, yeni) > 0) return yeni + " AionMeter'da başka işe atanmış";
        }
        string eski = _ayar.KuklaKisayol;
        KuklaKisayolKaldir();
        if (!KuklaKisayolKaydet(yeni))
        {
            if (!_kisayolYakalaniyor) KuklaKisayolKaydet(eski); // yakalarken eski, yakalama bitince geri kaydedilir
            return "Bu kısayol kullanımda";
        }
        _ayar.KuklaKisayol = yeni;
        _ayar.Kaydet();
        TepsiGuncelle();
        return null;
    }

    /// <summary>Ayarlar yeni tuşu yakalarken kendi kısayolu WM_HOTKEY'e gitmesin diye geçici olarak bırakılır.</summary>
    private void KisayolYakalama(bool basladi)
    {
        _kisayolYakalaniyor = basladi;
        if (basladi) KuklaKisayolKaldir();
        else if (!_kuklaKisayolKayitli) KuklaKisayolKaydet(_ayar.KuklaKisayol);
    }

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
        _aniSifirla = true;
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
            if (_kukla.YerelYok)
            {
                _kuklaBilgi = "Karakterin tanınmadı: bölge değiştir ya da vur";
                _kuklaBilgiRenk = Palet.Soluk;
            }
            else
            {
                _kuklaBilgi = "Vurmaya başla: ilk vuruşta sayım başlar";
                _kuklaBilgiRenk = Palet.YerelYazi;
            }
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
            _kartKiyas = k.Kesildi ? "Deneme sıfırlandı, rekora sayılmadı" : "Süre dolmadı, rekora sayılmadı";
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
        _rBoss!.Ciz(g, _kuklaSag, new Rectangle(sx, y, _kuklaSagW, satirH),
            _kukla.Durum == KuklaTesti.Hal.Sayiyor ? Palet.Dps : Palet.Soluk, sag: true);
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
        _rKart!.Ciz(g, _kartDps, new Rectangle(dx, y, _kartDpsW, dpsH), Palet.Dps, sag: false);
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
            _rKucuk!.Ciz(g, _kartBeceriHasar[i], new Rectangle(hasarX, y, _kartHasarW, bh), Palet.Yazi, sag: true);
            _rKucuk.Ciz(g, _kartBeceriPay[i], new Rectangle(payX, y, _kartPayW, bh), Palet.Soluk, sag: true);
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
    private int KartDpsH => Math.Max(P(28), Math.Max(Yuk(_yaziKart!), _rKart?.Yukseklik ?? 0) + P(2));
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
        if (_yazi is null || _yaziYari is null || _yaziKucuk is null || _yaziBaslik is null || _yaziBoss is null) return;
        if (_rDps is null || _rKucuk is null || _rBoss is null || _rYari is null || _rKart is null) return;
        int pad = Pad;
        int gerek = 0;

        if (_durum is not null)
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_durum, _yazi), P(500)));

        if (_satirSayisi > 0)
        {
            // Başlık metinleri sütunun en az genişliğidir; CP sabit şablon genişliğinde (sağa hizalı, satırlar zıplamasın).
            _cpW = Math.Max(_cpW, Math.Max(_rKucuk.Genislik(CpSablonu), Olc(CpEtiketi, _yaziBaslik)));
            _dpsW = Math.Max(_dpsW, Olc(_bossModu ? BossEtiketi : DpsEtiketi, _yaziBaslik));
            if (_bossModu) _dps2W = Math.Max(_dps2W, Olc(DpsEtiketi, _yaziBaslik));
            _toplamW = Math.Max(_toplamW, Olc(HasarEtiketi, _yaziBaslik));
            _payW = Math.Max(_payW, Olc(PayEtiketi, _yaziBaslik));
            for (int i = 0; i < _satirSayisi; i++)
            {
                var m = _satirlar[i];
                m.AdW = Math.Min(Olc(m.Ad, m.Yerel ? _yaziYari : _yazi), P(150));
                _adW = Math.Max(_adW, m.AdW + OlumW(m));
                _cpW = Math.Max(_cpW, _rKucuk.Genislik(m.Cp));
                _toplamW = Math.Max(_toplamW, _rKucuk.Genislik(m.Toplam));
                _dpsW = Math.Max(_dpsW, _rDps.Genislik(m.Dps));
                if (_bossModu) _dps2W = Math.Max(_dps2W, _rKucuk.Genislik(m.Dps2));
                if (m.Sira.Length > 0) _siraW = Math.Max(_siraW, Olc(m.Sira, m.Sira[0] == '#' ? _yaziYari : _yaziKucuk));
                _payW = Math.Max(_payW, _rKucuk.Genislik(m.Pay));
            }
            Sutunlar(0, out int cpX, out _, out _, out _, out _);
            gerek = Math.Max(gerek, 2 * pad + SolBlokW - cpX);
        }

        _noktaW = Olc("·", _yaziKucuk);
        if (_bossGoster)
        {
            // Satır 1: boss adı [Sonuç] ... %yy,y   Satır 3: süre · Kalan · Öfke · Yetişir ........ can
            _bossYuzdeW = _bossOldu ? Olc(_bossYuzde, _yaziBoss) : _rBoss.Genislik(_bossYuzde);
            _sonucW = _sonuc ? P(8) + Olc(SonucEtiketi, _yaziKucuk) : 0;
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_bossAd, _yaziBoss), P(220)) + _sonucW + P(12) + _bossYuzdeW);
            _bossDegerW = _rKucuk.Genislik(_bossDeger);
            gerek = Math.Max(gerek, 2 * pad + BilgiW() + P(16) + _bossDegerW);
        }
        if (KuklaBlokVar)
        {
            // Sağdaki geri sayım şablonla ölçülür: saniye değiştikçe pencere zıplamasın.
            _kuklaSagW = Math.Max(_rBoss.Genislik("00:00"), _rBoss.Genislik(_kuklaSag));
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
            _kartDpsW = _rKart.Genislik(_kartDps);
            gerek = Math.Max(gerek, ic + IkonW + P(8) + _kartDpsW + P(14) + Olc(_kartToplam, _yaziKucuk));
            _kartPayW = _rKucuk.Genislik("%100,0");
            _kartHasarW = 0;
            int adMaks = 0;
            for (int i = 0; i < _kartBeceriSayisi; i++)
            {
                _kartHasarW = Math.Max(_kartHasarW, _rKucuk.Genislik(_kartBeceriHasar[i]));
                adMaks = Math.Max(adMaks, Math.Min(Olc(_kartBeceri[i], _yazi), P(190)));
            }
            gerek = Math.Max(gerek, ic + Olc("3", _yaziKucuk) + P(8) + adMaks + P(14) + _kartHasarW + P(10) + _kartPayW);
            bool rekor = _kukla.Sonuc?.YeniRekor == true; // CizKart ile aynı yazı tipleri
            gerek = Math.Max(gerek, ic + Olc(_kartKiyas, rekor ? _yaziYari : _yaziKucuk) + Olc(_kartKiyasDeger, rekor ? _yaziKucuk : _yaziYari));
        }
        _sureW = _bossGoster || KuklaBlokVar || _durum != null ? 0 // boss varken süre boss bloğunda; durum mesajında süre yok
            : _sureRakam ? _rKucuk.Genislik(_sure) : Olc(_sure, _yaziKucuk);
        _partiEtiketW = Olc(_partiEtiket, _yaziKucuk);
        _partiDegerW = _rYari.Genislik(_partiDeger);
        _oyuncuSayisiW = Olc(_oyuncuSayisi, _yaziKucuk);
        _ipucuW = Olc(_ipucu, _yaziKucuk);
        _alarmMetinW = AlarmKurulu ? Math.Max(Olc("Alarm 00:00", _yaziKucuk), Olc(_alarmMetin, _yaziKucuk)) : 0;
        _saatW = SaatBoy + (_alarmMetinW > 0 ? P(5) + _alarmMetinW : 0);
        _kupW = _kupYakinda > 0 ? KupBoy : 0;
        gerek = Math.Max(gerek, 2 * pad + AltSolW() + (_ipucuW > 0 ? P(12) + _ipucuW : 0) + P(12) + _saatW + (_kupW > 0 ? _kupW + P(12) : 0));

        _icerikW = Math.Max(_icerikW, gerek);
    }

    /// <summary>Alt satır her zaman var: en azından alarm saati orada durur.</summary>
    private static bool AltVar => true;

    private int SaatBoy => P(12);
    private int KupBoy => P(11);

    private int BilgiW()
    {
        int w = 0;
        for (int i = 0; i < _bilgiSayisi; i++)
        {
            _bilgiW[i] = _bilgiRakam[i] ? _rKucuk!.Genislik(_bilgiMetin[i]) : Olc(_bilgiMetin[i], _yaziKucuk!);
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
        _yazi?.Dispose(); _yaziYari?.Dispose(); _yaziKucuk?.Dispose(); _yaziBaslik?.Dispose(); _yaziBoss?.Dispose();
        _yaziKart?.Dispose();
        RakamlariBirak();
        // Ad, DPS ve boss yazıları Ayarlar → Renk ve yazı'daki yüzdelerle (%80–%130)
        float ad = _ayar.YaziAd / 100f, dps = _ayar.YaziDps / 100f, boss = _ayar.YaziBoss / 100f;
        _yaziImza = (_ayar.YaziAd, _ayar.YaziDps, _ayar.YaziBoss);
        _yazi = Palet.YaziTipi(12f * _olcek * ad);
        _yaziYari = Palet.YaziTipi(12f * _olcek * ad, yariKalin: true);
        _yaziKucuk = Palet.YaziTipi(11f * _olcek);
        _yaziBaslik = Palet.YaziTipi(10.5f * _olcek);
        _yaziBoss = Palet.YaziTipi(13f * _olcek * boss, yariKalin: true);
        _yaziKart = Palet.YaziTipi(20f * _olcek * dps, yariKalin: true);
        // Sayılar: aynı boyutlarda Bahnschrift (ana sayılar SemiBold, küçükler normal)
        _rDps = new RakamYazisi(Palet.RakamTipi(13f * _olcek * dps, yariKalin: true));
        _rKucuk = new RakamYazisi(Palet.RakamTipi(11f * _olcek));
        _rBoss = new RakamYazisi(Palet.RakamTipi(13f * _olcek * boss, yariKalin: true));
        _rYari = new RakamYazisi(Palet.RakamTipi(12f * _olcek * ad, yariKalin: true));
        _rKart = new RakamYazisi(Palet.RakamTipi(20f * _olcek * dps, yariKalin: true));
        _kalemOfkeKenar.Width = Math.Max(1f, MathF.Round(_olcek));
        _kalemIsaretDolu.Width = _kalemIsaretBos.Width = Math.Max(1f, MathF.Round(_olcek));
        _rowH = Math.Max(P(22), Math.Max(Yuk(_yazi), _rDps.Yukseklik) + P(3));
        _bossSatirH = Math.Max(P(19), Math.Max(Yuk(_yaziBoss), _rBoss.Yukseklik) + P(1));
        _kalemSaat.Width = Math.Max(1f, 1.2f * _olcek);
        _kalemOlum.Width = Math.Max(1.2f, 1.6f * _olcek);
        DetayKapat(); // ölçek değişti: panel yeni ölçekle yeniden açılmalı
        _ikonlar.BoyutAyarla(IkonW);
        GenislikleriSifirla();
        GenislikleriOlc();
        _kenar?.Yerlestir(Bounds, _olcek); // ölçek değişti: sekme yeni boyutta
    }

    private void RakamlariBirak()
    {
        _rDps?.Dispose(); _rKucuk?.Dispose(); _rBoss?.Dispose(); _rYari?.Dispose(); _rKart?.Dispose();
        _rDps = _rKucuk = _rBoss = _rYari = _rKart = null;
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
        if (_yazi is null || _yaziYari is null || _yaziKucuk is null || _yaziBaslik is null || _yaziBoss is null) return;
        if (_yaziKart is null || _rDps is null || _rKucuk is null || _rBoss is null || _rYari is null || _rKart is null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = Pad;
        int x0 = pad, x1 = w - pad, gen = x1 - x0;
        int y = pad;
        var kb = g.ClipBounds;
        _klip = Rectangle.FromLTRB((int)Math.Max(-1e6, Math.Floor(kb.Left)), (int)Math.Max(-1e6, Math.Floor(kb.Top)),
            (int)Math.Min(1e6, Math.Ceiling(kb.Right)), (int)Math.Min(1e6, Math.Ceiling(kb.Bottom)));

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
            if (Gor(new Rectangle(x0, y, gen, BaslikH)))
            {
                TextRenderer.DrawText(g, CpEtiketi, _yaziBaslik, new Rectangle(cpX, y, _cpW, BaslikH), bc, Sag);
                TextRenderer.DrawText(g, _bossModu ? BossEtiketi : DpsEtiketi, _yaziBaslik, new Rectangle(dpsX, y, _dpsW, BaslikH), bc, Sag);
                if (_dps2W > 0) TextRenderer.DrawText(g, DpsEtiketi, _yaziBaslik, new Rectangle(dps2X, y, _dps2W, BaslikH), bc, Sag);
                TextRenderer.DrawText(g, HasarEtiketi, _yaziBaslik, new Rectangle(toplamX, y, _toplamW, BaslikH), bc, Sag);
                TextRenderer.DrawText(g, PayEtiketi, _yaziBaslik, new Rectangle(payX, y, _payW, BaslikH), bc, Sag);
            }
            y += BaslikH;
            _cizX0 = x0; _cizX1 = x1; _cizDpsX = dpsX;

            _satirlarUst = y;
            // Sıralı satırlar yuvalarına göre (kayma animasyonu oyuncunun akışında); sabit üst satır yerinde.
            // Çizim sırası: duranlar, aşağı kayanlar, yukarı kayanlar: yukarı çıkan satır en üstte görünür.
            int siraliUst = y + (_sabitUst ? Adim + Ayirac : 0);
            for (int tur = 0; tur < 3; tur++)
            {
                for (int i = 0; i < _satirSayisi; i++)
                {
                    var m = _satirlar[i];
                    if (KaymaTuru(m) != tur) continue;
                    int ry = !m.Sirali ? y
                        : m.A is { } a ? siraliUst + (int)Math.Round(a.Y.Deger * Adim)
                        : siraliUst + (i - (_sabitUst ? 1 : 0)) * Adim;
                    CizSatir(g, m, x0, x1, ry, kayiyor: tur > 0, sabitSatir: _sabitUst && i == 0, cpX, dpsX, dps2X, toplamX, payX);
                }
            }
            if (_sabitUst)
            {
                int cy = y + Adim - Aralik + Ayirac / 2;
                g.DrawLine(_kalemAyirac, x0, cy, x1, cy);
            }
            y += _satirSayisi * Adim + (_sabitUst ? Ayirac : 0) - Aralik;
            _cizSatirAlt = y;
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
        if (!Gor(_altAlani))
        {
            g.DrawRectangle(_kilitli ? _kalemKilitli : _kalemKilitsiz, 0, 0, w - 1, h - 1);
            return;
        }
        CizSaat(g, saatX, y + altH / 2, AlarmKurulu ? Palet.Vurgu : _saatUstunde ? Palet.Yazi : Palet.CokSoluk);
        if (_alarmMetinW > 0)
            TextRenderer.DrawText(g, _alarmMetin, _yaziKucuk, new Rectangle(saatX + SaatBoy + P(5), y, _alarmMetinW, altH), Palet.Yazi, Sol);
        x1 = saatX - P(12);
        if (_kupW > 0)
        {
            // Gizli Küp menzilde: saatin solunda küçük küp simgesi
            int kx = x1 - _kupW;
            CizKup(g, kx, y + altH / 2);
            x1 = kx - P(12);
        }

        int x2 = x0;
        if (_sureW > 0)
        {
            if (_sureRakam) _rKucuk.Ciz(g, _sure, new Rectangle(x2, y, _sureW, altH), Palet.Soluk, sag: false);
            else TextRenderer.DrawText(g, _sure, _yaziKucuk, new Rectangle(x2, y, _sureW, altH), Palet.Soluk, Sol);
            x2 += _sureW;
            if (_partiDegerW > 0) x2 = Nokta(g, x2, y, altH);
        }
        if (_partiDegerW > 0)
        {
            TextRenderer.DrawText(g, _partiEtiket, _yaziKucuk, new Rectangle(x2, y, _partiEtiketW, altH), Palet.Soluk, Sol);
            x2 += _partiEtiketW + P(4);
            _rYari.Ciz(g, _partiDeger, new Rectangle(x2, y, Math.Max(0, Math.Min(_partiDegerW, x1 - x2)), altH), Palet.Yazi, sag: false);
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

    /// <summary>0: yerinde, 1: aşağı kayıyor, 2: yukarı kayıyor.</summary>
    private static int KaymaTuru(SatirModel m)
    {
        if (!m.Sirali || m.A is not { } a || !a.Y.Aktif) return 0;
        return a.Y.Hedef > a.Y.Deger ? 1 : 2;
    }

    /// <summary>Bir oyuncu satırı. Sayılar geçiş sürerken akışın ara metninden, bitince kesin metinden (tam sayı, binlik ayırıcı).</summary>
    private void CizSatir(Graphics g, SatirModel m, int x0, int x1, int y, bool kayiyor, bool sabitSatir,
                          int cpX, int dpsX, int dps2X, int toplamX, int payX)
    {
        int gen = x1 - x0, rowH = RowH, ic = P(2);
        var a = m.A;
        m.CizY = y;
        if (!Gor(new Rectangle(x0, y, gen, rowH))) return;
        if (sabitSatir) g.FillRectangle(_fircaSabitSatir, x0, y, gen, rowH);
        else if (kayiyor) g.FillRectangle(_fircaArka, x0, y, gen, rowH); // kayan satır altındakini örter

        // Pay çubuğu sınıfın koyu tonunda; solda 2 px şerit: sınıfın açık tonu, yerel oyuncuda altın.
        // (Şerit çubuğun ucuna değil sola konur: sayıların üstünden geçmesin.)
        int cubukY = y + ic, cubukH = rowH - 2 * ic;
        double oran = a is not null ? Math.Clamp(a.Oran.Deger, 0, 1) : m.Oran;
        int pay = (int)(gen * oran);
        m.CizPay = pay;
        if (pay > 0 && Palet.CubukAlfa > 0) g.FillRectangle(_fircaKoyu[m.Sinif], x0, cubukY, pay, cubukH);
        g.FillRectangle(m.Yerel ? _fircaYerel : _fircaAcik[m.Sinif], x0, cubukY, Math.Max(2, P(2)), cubukH);
        if (_detay is { Visible: true } && _detay.OyuncuId == m.Id) g.FillRectangle(_fircaSecili, x0, y, gen, rowH);

        int x = x0 + Ic;
        if (Gor(Rectangle.FromLTRB(x0, y, cpX, y + rowH))) // sol blok (sıra · ikon · ad): kısmi karelerde çoğu zaman atlanır
        {
            if (_siraW > 0)
            {
                // Sıra: kendi satırında "#14" (kendi rengin), ilk 5'te küçük ve soluk "1..5"
                bool kendiSira = m.Sira.Length > 0 && m.Sira[0] == '#';
                TextRenderer.DrawText(g, m.Sira, kendiSira ? _yaziYari! : _yaziKucuk!, new Rectangle(x, y, _siraW, rowH),
                    kendiSira ? Palet.YerelYazi : Palet.CokSoluk, Sag);
                x += _siraW + P(4);
            }
            int ikon = IkonW;
            g.DrawImage(_ikonlar.Al(m.SinifAdi), x, y + (rowH - ikon) / 2, ikon, ikon);
            x += ikon + GrupIci;
            int adSinir = Math.Max(0, cpX - GrupIci - x - OlumW(m));
            int adW = Math.Min(m.AdW, adSinir);
            TextRenderer.DrawText(g, m.Ad, m.Yerel ? _yaziYari! : _yazi!, new Rectangle(x, y, adW, rowH),
                m.Yerel ? Palet.YerelYazi : Palet.Yazi, Sol);
            if (m.Olum.Length > 0) CizOlum(g, m, x + adW + P(5), y, rowH);
        }

        // Sayılar tek HDC oturumunda (satır başına bir GetHdc)
        var rk = _rKucuk!;
        var rd = _rDps!;
        IntPtr hdc = RakamYazisi.Ac(g);
        try
        {
            rk.Ciz(hdc, m.Cp, new Rectangle(cpX, y, _cpW, rowH), Palet.Soluk, sag: true);
            var r = new Rectangle(dpsX, y, _dpsW, rowH);
            if (a is { Dps.Aktif: true }) rd.Ciz(hdc, a.DpsB.AsSpan(0, a.DpsN), r, Palet.Dps, sag: true);
            else rd.Ciz(hdc, m.Dps, r, Palet.Dps, sag: true);
            if (_dps2W > 0)
            {
                r = new Rectangle(dps2X, y, _dps2W, rowH);
                if (a is { Dps2.Aktif: true }) rk.Ciz(hdc, a.Dps2B.AsSpan(0, a.Dps2N), r, Palet.Soluk, sag: true);
                else rk.Ciz(hdc, m.Dps2, r, Palet.Soluk, sag: true);
            }
            r = new Rectangle(toplamX, y, _toplamW, rowH);
            if (a is { Toplam.Aktif: true }) rk.Ciz(hdc, a.ToplamB.AsSpan(0, a.ToplamN), r, Palet.Yazi, sag: true);
            else rk.Ciz(hdc, m.Toplam, r, Palet.Yazi, sag: true);
            r = new Rectangle(payX, y, _payW, rowH);
            if (a is { PayVar: true, Pay.Aktif: true }) rk.Ciz(hdc, a.PayB.AsSpan(0, a.PayN), r, Palet.Soluk, sag: true);
            else rk.Ciz(hdc, m.Pay, r, Palet.Soluk, sag: true);
        }
        finally
        {
            RakamYazisi.Kapat(g, hdc);
        }
    }

    /// <summary>Gizli Küp işareti: kodla çizilmiş küçük izometrik küp (üst yüz açık, yan yüzler koyu).</summary>
    private void CizKup(Graphics g, int x, int cy) =>
        KupSimgesi.Ciz(g, x, cy, KupBoy, _fircaKupUst, _fircaKupSol, _fircaKupSag, _kupNokta);

    /// <summary>Boss bloğu: ad / % · ince can çubuğu (yazısız) · süre · Kalan · Öfke · Yetişir ..... can.</summary>
    private void CizBoss(Graphics g, int x0, int x1, int y)
    {
        int satirH = BossSatirH;
        int sx = x1 - _bossYuzdeW;
        var yuzdeRenk = _bossOldu || _bossBilinmiyor ? Palet.Soluk : Palet.Yazi;
        if (Gor(Rectangle.FromLTRB(x0, y, x1, y + satirH)))
        {
            if (_bossOldu) TextRenderer.DrawText(g, _bossYuzde, _yaziBoss!, new Rectangle(sx, y, _bossYuzdeW, satirH), yuzdeRenk, Sag);
            else _rBoss!.Ciz(g, _bossYuzde, new Rectangle(sx, y, _bossYuzdeW, satirH), yuzdeRenk, sag: true);
            int adW = Math.Min(Olc(_bossAd, _yaziBoss!), Math.Max(0, sx - P(12) - _sonucW - x0));
            TextRenderer.DrawText(g, _bossAd, _yaziBoss!, new Rectangle(x0, y, adW, satirH), Palet.Yazi, Sol);
            if (_sonuc)
                TextRenderer.DrawText(g, SonucEtiketi, _yaziKucuk!, new Rectangle(x0 + adW + P(8), y, _sonucW - P(8), satirH), Palet.Soluk, Sol);
        }
        y += satirH + P(3);

        // Can çubuğu: 8 px, içinde yazı yok. Can bilinmiyorsa son bilinen oran soluk ve kesikli.
        // Akış: gösterilen oran hedefe iner; yeni inen kısım hasar izi olarak kısa süre açık renkte kalır.
        var cubuk = new Rectangle(x0, y, x1 - x0, BossCubukH);
        _bossCubukAlani = cubuk;
        g.FillRectangle(_fircaBossIz, cubuk);
        int dolu = 0;
        if (!_bossOldu)
        {
            if (_bossBilinmiyor)
            {
                dolu = (int)(cubuk.Width * _bossOran);
                int parca = P(6), bosluk = P(4);
                for (int px = cubuk.X; px < cubuk.X + dolu; px += parca + bosluk)
                    g.FillRectangle(_fircaBossBilinmiyor, px, cubuk.Y, Math.Min(parca, cubuk.X + dolu - px), cubuk.Height);
            }
            else
            {
                dolu = (int)(cubuk.Width * Math.Clamp(_bossCanAni.Deger, 0, 1));
                int iz = (int)(cubuk.Width * Math.Clamp(_bossIz, 0, 1));
                if (iz > dolu) g.FillRectangle(_fircaBossHasarIzi, cubuk.X + dolu, cubuk.Y, iz - dolu, cubuk.Height);
                if (dolu > 0) g.FillRectangle(_fircaBossCan, cubuk.X, cubuk.Y, dolu, cubuk.Height);
            }
        }
        if (_bossMaxVar)
        {
            // %75 / %50 / %25: ince, göze batmayan işaretler (dolu kısımda koyu, boş izde hafif açık)
            for (int k = 3; k >= 1; k--)
            {
                int mx = cubuk.X + (int)Math.Round(cubuk.Width * k / 4.0);
                g.DrawLine(mx < cubuk.X + dolu ? _kalemIsaretDolu : _kalemIsaretBos, mx, cubuk.Y, mx, cubuk.Bottom - 1);
            }
        }
        if (_ofkeK > 0)
        {
            // Öfkeye son 30 sn: kenar yavaşça kırmızıya döner; öfkede kırmızı kalır.
            int k = (int)_kalemOfkeKenar.Width;
            g.DrawRectangle(_kalemOfkeKenar, cubuk.X - (k + 1) / 2, cubuk.Y - (k + 1) / 2, cubuk.Width + k, cubuk.Height + k);
        }
        y += BossCubukH + P(4);

        int bilgiH = BossBilgiH;
        int canX = x1 - _bossDegerW;
        if (!Gor(Rectangle.FromLTRB(x0, y, x1, y + bilgiH)))
        {
            if (_fircaUyari.Color.A > 0) g.FillRectangle(_fircaUyari, _bossAlani);
            return;
        }
        if (_bossDegerW > 0)
            _rKucuk!.Ciz(g, _bossDeger, new Rectangle(canX, y, _bossDegerW, bilgiH), Palet.Soluk, sag: true);
        int bx = x0;
        int sinir = canX - P(12);
        for (int i = 0; i < _bilgiSayisi; i++)
        {
            if (i > 0) bx = Nokta(g, bx, y, bilgiH);
            if (bx >= sinir) break;
            if (_bilgiRakam[i]) _rKucuk!.Ciz(g, _bilgiMetin[i], new Rectangle(bx, y, Math.Min(_bilgiW[i], sinir - bx), bilgiH), _bilgiRenk[i], sag: false);
            else TextRenderer.DrawText(g, _bilgiMetin[i], _yaziKucuk!, new Rectangle(bx, y, Math.Min(_bilgiW[i], sinir - bx), bilgiH), _bilgiRenk[i], Sol);
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
            MenuAc(this, Cursor.Position, ToolStripDropDownDirection.BelowRight);
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
            if (!_fareUstunde || _fareCikis != DateTime.MinValue)
            {
                // Kilitsiz ve savaş dışında soluk: fare gelince hemen tam görünür.
                _fareUstunde = true;
                _fareCikis = DateTime.MinValue;
                GorunurlukUygula();
            }
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
        if (_fareUstunde && _fareCikis == DateTime.MinValue) _fareCikis = DateTime.UtcNow; // ~1 sn sonra kurala dön
    }

    // Kilitsizken savaş dışı kuralı da geçerli; fare overlay'in (ya da açık detay panelinin) üstündeyken tam görünür.
    private bool _fareUstunde;
    private DateTime _fareCikis = DateTime.MinValue;
    private static readonly TimeSpan FareCikisGecikmesi = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ana zamanlayıcıdan (2 Hz) imleç yoklaması, sadece kilitsizken: "Gizle"de pencere gizliyken fare olayı gelmez,
    /// imleç overlay'in kayıtlı sınırlarına girince yeniden görünür olsun. Çıkınca ~1 sn sonra kurala döner.
    /// </summary>
    private void FareYokla()
    {
        if (_kilitli)
        {
            _fareUstunde = false;
            _fareCikis = DateTime.MinValue;
            return;
        }
        var p = Cursor.Position;
        bool ust = Bounds.Contains(p) || (_detay is { Visible: true } && _detay.Bounds.Contains(p));
        if (ust)
        {
            _fareUstunde = true;
            _fareCikis = DateTime.MinValue;
        }
        else if (_fareUstunde)
        {
            if (_fareCikis == DateTime.MinValue) _fareCikis = DateTime.UtcNow;
            else if (DateTime.UtcNow - _fareCikis >= FareCikisGecikmesi)
            {
                _fareUstunde = false;
                _fareCikis = DateTime.MinValue;
            }
        }
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
        var liste = (_sonAnlik ?? _onceki)?.Players;
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
        _detay.Goster(id, DetayCapasi); // SW_SHOWNOACTIVATE
        GorunurlukUygula();
        Invalidate();
    }

    // ---------------- Kilitliyken Alt ile göz atma ----------------

    private void AltZamanlayiciGuncelle()
    {
        // Demoda oyun yok: oyun-önde şartı aranmaz (test edilebilsin).
        bool oyunOnde = _demo || _onPlan.OyunOnde();
        bool calismali = oyunOnde && !_tamEkran && ((_kilitli && _sertGorunur) || _cevirmen.AltCeviriCalisir);
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
                                (_kenar is { Acik: true } && _kenar.Alan.Contains(imlec)) ||
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
    private static bool TusBasildi(int vk) => (Native.GetAsyncKeyState(vk) & 0x8001) != 0;

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
        _kenar?.Yerlestir(Bounds, _olcek);
        _detay?.Yerlestir(DetayCapasi);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _kenar?.Yerlestir(Bounds, _olcek);
        _detay?.Yerlestir(DetayCapasi);
    }

    // ---------------- Komutlar ----------------

    private void KisayolBasildi(int id)
    {
        // Ayarlarda yeni kukla kısayolu yakalanırken sabit bir kısayola basıldı: çalıştırma, yakalanan tuş say (reddedilir).
        if (_kisayolYakalaniyor && id > 0 && id < SabitKisayollar.Length && _ayarlarFormu is not null)
        {
            _ayarlarFormu.KisayolYakalandi(SabitKisayollar[id]);
            return;
        }
        switch (id)
        {
            case KisayolKilit: KilitDegistir(); break;
            case KisayolGoster: GosterDegistir(); break;
            case KisayolSifirla: Sifirla(); break;
            case KisayolDetayim: Detayim(); break;
            case KisayolCevir:
                if (!_demo && _onPlan.OzelTamEkran()) TamEkranUyar(zorla: true); // seçim ve çeviri pencereleri oyunu çökertebilir
                else _cevirmen.BolgeCevir();
                break;
            case KisayolKukla: KuklaKisayolBasildi(); break;
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
        _sonSavas = DateTime.UtcNow; // kilitleyince / kilidi açınca hemen soluklaşmasın: 8 sn tam görünür
        _fareUstunde = false;
        _fareCikis = DateTime.MinValue;
        Cursor = Cursors.Default;
        AltZamanlayiciGuncelle(); // kilitsizken Alt yoklaması durur
        EtkinlikTick(); // geri sayım paneli kilitsizken hemen taşınabilir olur
        TepsiGuncelle();
        IpucuGuncelle();
        _onceki = null; // ipucu genişliği değişti: modeli yeniden kur
        GenislikleriSifirla();
        Tick();
        Invalidate();
        AyarKaydet();
    }

    /// <summary>Geri sayım ve boss listesi: oyun önde, tam ekran değil, kullanıcı gizlemedi (= _sertGorunur) iken görünür.</summary>
    private void EtkinlikTick()
    {
        try { _etkinlik.Tick(DateTime.UtcNow, gorunebilir: _sertGorunur, kilitli: _kilitli); }
        catch (Exception ex) { HataLog.Yaz("Etkinlik zamanlayıcısı", ex); }
    }

    // Ön plan değişince beklemeden görünürlüğü yeniden hesapla: Alt+Tab ile özel tam ekran oyuna dönülürken pencerelerimiz
    // 5 Hz tiki beklerken (≤200 ms) oyunun üstünde kalmasın (denetim bulgusu 5). Temsilci alanda tutulur (GC toplamasın).
    private Native.WinEventProc? _onPlanGeriCagri;
    private IntPtr _onPlanKancasi;

    private void OnPlanKancasiKur()
    {
        _onPlanGeriCagri = (_, _, _, _, _, _, _) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                GorunurlukUygula();
                EtkinlikTick();
                MenuyuGerekirseKapat();
            }
            catch (Exception ex) { HataLog.Yaz("Ön plan değişimi", ex); }
        };
        _onPlanKancasi = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _onPlanGeriCagri, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    private void GosterDegistir()
    {
        _kullaniciGizledi = !_kullaniciGizledi;
        if (_kullaniciGizledi) DetayKapat();
        GorunurlukUygula(); // SW_HIDE / SW_SHOWNOACTIVATE
        EtkinlikTick();
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
        _aniSifirla = true;
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
        _kareZamanlayici.Stop();
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
            if (_onPlanKancasi != IntPtr.Zero) Native.UnhookWinEvent(_onPlanKancasi);
            _onPlanKancasi = IntPtr.Zero;
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
            _etkinlik.Dispose();
            DetayKapat();
            _kenar?.Dispose();
            _kupAfisi?.Dispose();
            _kisayol?.Dispose();
            if (_tepsi is not null && _menuBagli) _tepsi.Menu.Closed -= MenuKapandi;
            _tepsi?.Dispose();
            _ikonlar.Dispose();
            _yazi?.Dispose(); _yaziYari?.Dispose(); _yaziKucuk?.Dispose(); _yaziBaslik?.Dispose(); _yaziBoss?.Dispose();
            _yaziKart?.Dispose();
            RakamlariBirak();
            _kareZamanlayici.Dispose();
            _fircaArka.Dispose(); _fircaBossHasarIzi.Dispose(); _kalemIsaretDolu.Dispose(); _kalemIsaretBos.Dispose(); _kalemOfkeKenar.Dispose();
            _fircaKupUst.Dispose(); _fircaKupSol.Dispose(); _fircaKupSag.Dispose();
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
