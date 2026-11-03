using Microsoft.Win32;

namespace AionMeter;

/// <summary>
/// Kenarlıksız, en üstte, yarı saydam, kendisi çizen overlay. Satırlar için alt kontrol yoktur.
/// Kilitliyken fare olaylarını almaz (WS_EX_TRANSPARENT | WS_EX_LAYERED); hiçbir zaman odak çalmaz.
/// Satır: sınıf ikonu · ad · CP (soluk) | DPS (birincil) · toplam · katkı %.
/// Kilitli değilken satıra tıklayınca yanda detay paneli (<see cref="DetayForm"/>) açılır.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int MaxSatir = 12;
    private const int UstSayisi = 5; // kalabalıkta (> 5 oyuncu) gösterilen ilk sıra sayısı
    private static readonly string[] SiraMetni = { "1", "2", "3", "4", "5" };
    private const int KisayolKilit = 1, KisayolGoster = 2, KisayolSifirla = 3, KisayolDetayim = 4, KisayolCevir = 5;

    // Renkler
    private static readonly Color RenkArka = Color.FromArgb(18, 20, 26);
    private static readonly Color RenkYazi = Color.FromArgb(222, 225, 230);
    private static readonly Color RenkSoluk = Color.FromArgb(138, 145, 156);
    private static readonly Color RenkCokSoluk = Color.FromArgb(110, 116, 128);
    private static readonly Color RenkDps = Color.FromArgb(255, 255, 255);
    private static readonly Color RenkYerel = Color.FromArgb(255, 210, 110);
    private static readonly Color RenkParti = Color.FromArgb(120, 200, 255);
    private static readonly Color RenkKalan = Color.FromArgb(255, 190, 120);
    private static readonly Color RenkKirmizi = Color.FromArgb(250, 96, 84);
    private static readonly Color RenkYesil = Color.FromArgb(110, 214, 124);

    private readonly IMeterEngine _motor;
    private readonly Ayarlar _ayar;
    private readonly bool _demo;
    private readonly System.Windows.Forms.Timer _zamanlayici;
    private readonly SinifIkonlari _ikonlar = new();
    private KisayolPenceresi? _kisayol;
    private DetayForm? _detay;
    private Tepsi? _tepsi;

    // Önbelleğe alınmış çizim nesneleri
    private Font? _yazi, _yaziKalin, _yaziDps, _yaziKucuk;
    private readonly SolidBrush _fircaPay = new(Color.FromArgb(40, 64, 96));
    private readonly SolidBrush _fircaPayYerel = new(Color.FromArgb(92, 74, 30));
    private readonly SolidBrush _fircaBossArka = new(Color.FromArgb(56, 22, 22));
    private readonly SolidBrush _fircaBossCan = new(Color.FromArgb(196, 54, 42));
    private readonly SolidBrush _fircaBossOldu = new(Color.FromArgb(70, 70, 74));
    private readonly Pen _kalemKilitsiz = new(Color.FromArgb(70, 140, 230));
    private readonly Pen _kalemKilitli = new(Color.FromArgb(44, 48, 58));
    private readonly Pen _kalemSecili = new(Color.FromArgb(120, 170, 240));
    private readonly SolidBrush _fircaRozet = new(Color.FromArgb(58, 64, 80));
    private readonly SolidBrush _fircaSabitSatir = new(Color.FromArgb(32, 36, 46));
    private readonly Pen _kalemAyirac = new(Color.FromArgb(64, 70, 86));
    private int _satirlarUst;

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;
    private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | Temel;

    // Çizim modeli (anlık görüntü değişince Tick içinde hazırlanır; OnPaint sadece çizer)
    private sealed class SatirModel
    {
        public string Ad = "", Cp = "", Toplam = "", Dps = "", Dps2 = "", Pay = "", Sira = "";
        public string? Sinif;
        public long Id, ToplamSayi;
        public float Oran;
        public bool Yerel;
    }

    private readonly SatirModel[] _satirlar = new SatirModel[MaxSatir];
    private int _satirSayisi;
    private string? _durum;
    private bool _bossGoster, _bossOldu;
    private string _bossAd = "", _bossYuzde = "", _bossDeger = "";
    private float _bossOran;
    private string _sure = "Süre 00:00", _partiDps = "", _ipucu = "";

    // Ölçülen sütun genişlikleri (fiziksel px). Bir dövüş boyunca sadece büyür; pencere titremesin.
    private int _adW, _cpW, _toplamW, _dpsW, _dps2W, _payW, _icerikW;
    private int _sureW, _partiW, _bossYuzdeW, _bossEtiketW, _sonucW, _siraW, _oyuncuSayisiW;

    // Kalabalık düzen (> 5 oyuncu): en üstte kendi satırın (#sıra rozetiyle) + ayırıcı, sonra 1..5.
    private bool _kalabalik, _sabitUst;
    private string _oyuncuSayisi = "";

    // Boss modu: boss takip ediliyorsa satırdaki ana sayı Boss DPS'tir, genel DPS ikincil.
    private bool _bossModu, _sonuc;
    private const string BossEtiketi = "boss", SonucEtiketi = "Sonuç";

    // Boss bilgi satırı parçaları: süre · Kalan ~ · Öfke · Yetişir/geç (sabit diziler, tahsis yok)
    private const int MaxBilgi = 4;
    private readonly string[] _bilgiMetin = new string[MaxBilgi];
    private readonly Color[] _bilgiRenk = new Color[MaxBilgi];
    private readonly bool[] _bilgiKalin = new bool[MaxBilgi];
    private readonly int[] _bilgiW = new int[MaxBilgi];
    private int _bilgiSayisi;
    private TimeSpan _oncekiSure;

    private MeterSnapshot? _onceki;
    private long _olumBossId = long.MinValue;
    private DateTime _olumZamani;
    private int _tikSayaci;
    private bool _kilitli;

    // Kilitliyken Alt ile göz atma: Alt basılıyken overlay tıklanabilir olur (odak yine alınmaz).
    // 50 ms'lik yoklama sadece kilitliyken ve oyun (ya da bizim pencerelerimiz) öndeyken çalışır. Klavye kancası yok.
    private readonly System.Windows.Forms.Timer _altZamanlayici;
    private bool _altAcik;   // Alt şu an basılı ve overlay tıklanabilir
    private bool _sabit;     // detay paneli sabitlendi (Alt bırakılınca kapanmaz)
    private long _sabitId;
    private long _hoverBastirId = long.MinValue; // Alt+tık ile kapatılan satır: Alt bırakılana/başka satıra geçilene dek yeniden açma

    // Görünürlük: kullanıcı gizlediyse (Ctrl+Alt+H) ya da "sadece oyun öndeyken" açıkken oyun önde değilse gizli.
    private readonly OnPlanIzleyici _onPlan = new();
    private readonly Cevirmen.CevirmenDenetleyici _cevirmen;
    private bool _kullaniciGizledi, _sadeceOyunda, _gorunur = true;
    private float _olcek = 1f;    // DPI × kullanıcı boyutu (overlay ve detay paneli çizimi)
    private float _dpiOlcek = 1f; // sadece DPI (çevirmen: piksel mesafeleri, panel yazıları)

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
        for (int i = 0; i < MaxSatir; i++) _satirlar[i] = new SatirModel();

        Text = "AionMeter";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = RenkArka;
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
        };
        _altZamanlayici = new System.Windows.Forms.Timer { Interval = 50 };
        _altZamanlayici.Tick += (_, _) => AltTick();

        SystemEvents.DisplaySettingsChanged += EkranDegisti;
    }

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
        SaydamlikUygula();
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10: köşe yuvarlama yok, sorun değil */ }
        OlcekAyarla();
        BoyutAyarla();
    }

    private void SaydamlikUygula()
    {
        byte alfa = (byte)Math.Clamp(_ayar.Opaklik * 255 / 100, 40, 255);
        Native.SetLayeredWindowAttributes(Handle, 0, alfa, Native.LWA_ALPHA);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _tepsi = new Tepsi(KilitDegistir, GosterDegistir, SadeceOyundaDegistir, Sifirla, Detayim, OtoGuncellemeDegistir,
            _cevirmen.BolgeCevir, _cevirmen.AltCeviriDegistir, _cevirmen.ModeliIndir, _cevirmen.DusukRamDegistir, BoyutSec, Close);
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
                          (_sadeceOyunda ? " Overlay sadece oyun öndeyken görünür (tepsi menüsünden değiştirilebilir)." : ""));
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

    // ---------------- Görünürlük (oyun önde mi?) ----------------

    private void GorunurlukUygula()
    {
        if (!IsHandleCreated) return;
        bool olmali = !_kullaniciGizledi && (!_sadeceOyunda || _onPlan.OyunOnde());
        if (olmali == _gorunur) return;
        _gorunur = olmali;
        AltZamanlayiciGuncelle();
        if (olmali)
        {
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); // asla etkinleştirme
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_SHOWNOACTIVATE);
            _onceki = null; // gizliyken kaçan değişiklikler için modeli yeniden kur
        }
        else
        {
            if (_detay is { IsHandleCreated: true }) Native.ShowWindow(_detay.Handle, Native.SW_HIDE);
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }
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

    private void TepsiGuncelle() => _tepsi?.Guncelle(_kilitli, !_kullaniciGizledi, _sadeceOyunda, _ayar.OtomatikGuncelleme,
        _ayar.AltCeviri, _cevirmen.ModelHazir, _ayar.CevirmenDusukRam, _cevirmen.ModelIndiriliyor, _ayar.Boyut);

    /// <summary>Tepsi "Boyut": overlay ve detay paneli bu yüzdeyle çizilir (Windows ölçeğinin üstüne).</summary>
    private void BoyutSec(int yuzde)
    {
        if (_ayar.Boyut == yuzde) return;
        _ayar.Boyut = yuzde;
        _ayar.Kaydet();
        OlcekAyarla();
        BoyutAyarla();
        Invalidate();
        TepsiGuncelle();
    }

    /// <summary>Çevirmen için: bizim görünür pencerelerimiz (maskelenir, üstündeyken Alt sayaca aittir).</summary>
    private IEnumerable<Rectangle> BizimPencereler()
    {
        if (IsHandleCreated && _gorunur) yield return Bounds;
        if (_detay is { Visible: true }) yield return _detay.Bounds;
    }

    /// <summary>Açılışta tepsi balonunda gösterilecek tek seferlik mesaj (Program ayarlar).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? AcilisBildirimi { get; init; }

    private void OtoGuncellemeDegistir()
    {
        _ayar.OtomatikGuncelleme = !_ayar.OtomatikGuncelleme;
        _ayar.Kaydet();
        TepsiGuncelle();
    }

    private void SadeceOyundaDegistir()
    {
        _sadeceOyunda = !_sadeceOyunda;
        _ayar.SadeceOyunda = _sadeceOyunda;
        _ayar.Kaydet();
        GorunurlukUygula();
        TepsiGuncelle();
        if (_gorunur) Tick();
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
            if (!_gorunur) return; // gizliyken motor okunmaz, çizilmez

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

            var s = _motor.Snapshot();
            bool bossGoster = BossGosterilsinMi(s);
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

    private bool BossGosterilsinMi(MeterSnapshot s)
    {
        if (s.State != EngineState.Connected || s.Boss is not { } b) return false;
        if (!b.Dead)
        {
            _olumBossId = long.MinValue;
            return true;
        }
        if (s.FightOver) return true; // donmuş sonuç: boss çubuğu "Öldü" ile kalır
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

    private void ModelKur(MeterSnapshot s)
    {
        var oyuncular = s.Players ?? Array.Empty<PlayerDamage>();
        double partiDps = 0;
        bool bossModu = _bossGoster; // başlıkla aynı koşul (Tick'te ModelKur'dan önce hesaplanır)
        if (bossModu != _bossModu) GenislikleriSifirla();
        _bossModu = bossModu;
        _sonuc = s.FightOver;

        // Yeni dövüş / bağlantı değişimi: sütun genişliklerini baştan ölç.
        if (s.State != EngineState.Connected || s.FightDuration < _oncekiSure) GenislikleriSifirla();
        _oncekiSure = s.FightDuration;

        if (s.State != EngineState.Connected)
        {
            _durum = DurumMetni(s);
            _satirSayisi = 0;
        }
        else
        {
            // ≤ 5 oyuncu: hepsi, sıra numarasız. > 5: [kendi satırın #sıra] + ayırıcı + 1..5.
            // Katkı % ve toplamlar HER ZAMAN tüm listeden hesaplanır.
            bool kalabalik = oyuncular.Count > UstSayisi;
            if (kalabalik != _kalabalik) GenislikleriSifirla();
            _kalabalik = kalabalik;
            int ustN = kalabalik ? UstSayisi : Math.Min(oyuncular.Count, MaxSatir);
            long enCok = 1, partiToplam = 0;
            int yerelSira = -1;
            for (int i = 0; i < oyuncular.Count; i++)
            {
                partiToplam += Math.Max(0, oyuncular[i].Total);
                if (oyuncular[i].Total > enCok) enCok = oyuncular[i].Total;
                if (yerelSira < 0 && oyuncular[i].IsLocal) yerelSira = i;
            }
            int n = 0;
            _sabitUst = kalabalik && yerelSira >= 0;
            if (_sabitUst) SatirDoldur(_satirlar[n++], oyuncular[yerelSira], "#" + (yerelSira + 1).ToString(Bicim.Tr));
            int ustBas = n;
            for (int i = 0; i < ustN; i++) SatirDoldur(_satirlar[n++], oyuncular[i], kalabalik ? SiraMetni[i] : "");

            void SatirDoldur(SatirModel m, PlayerDamage p, string sira)
            {
                m.Sira = sira;
                m.Id = p.Id;
                m.ToplamSayi = Math.Max(0, p.Total);
                m.Pay = Bicim.Oran(m.ToplamSayi, partiToplam);
                m.Ad = string.IsNullOrWhiteSpace(p.Name) ? "?" : p.Name;
                m.Sinif = p.ClassName;
                m.Cp = p.CombatPower is int cp ? Bicim.Tam(cp) + " CP" : "— CP";
                m.Toplam = Bicim.Tam(Math.Max(0, p.Total));
                m.Dps = Bicim.Dps(bossModu ? p.BossDps : p.Dps);
                m.Dps2 = bossModu ? Bicim.Dps(p.Dps) : "";
                m.Oran = p.Total > 0 ? Math.Clamp((float)((double)p.Total / enCok), 0f, 1f) : 0f; // 0 hasar: pay çubuğu yok
                m.Yerel = p.IsLocal;
            }
            // Motor toplam hasara göre sıralı verir; sadece eşitlikleri Id ile kararlı hale getir (satırlar zıplamasın).
            // Sabit üst satır (kendi satırın) bu sıralamaya girmez.
            for (int i = ustBas + 1; i < n; i++)
            {
                var m = _satirlar[i];
                int j = i - 1;
                while (j >= ustBas && _satirlar[j].ToplamSayi == m.ToplamSayi && _satirlar[j].Id > m.Id)
                {
                    _satirlar[j + 1] = _satirlar[j];
                    j--;
                }
                _satirlar[j + 1] = m;
            }
            _satirSayisi = n;
            _durum = n == 0 ? "Dövüş bekleniyor…" : null;
        }

        for (int i = 0; i < oyuncular.Count; i++)
        {
            double d = bossModu ? oyuncular[i].BossDps : oyuncular[i].Dps;
            if (d > 0 && !double.IsNaN(d) && !double.IsInfinity(d)) partiDps += d;
        }
        bool cok = oyuncular.Count > UstSayisi;
        _partiDps = s.State == EngineState.Connected && _satirSayisi > 0
            ? (cok ? (bossModu ? "Toplam boss " : "Toplam ") : (bossModu ? "Parti boss " : "Parti ")) + Bicim.Dps(partiDps)
            : "";
        _oyuncuSayisi = s.State == EngineState.Connected && cok ? "· " + oyuncular.Count.ToString(Bicim.Tr) + " oyuncu" : "";
        _bilgiSayisi = 0;

        if (_bossGoster && s.Boss is { } b)
        {
            _bossAd = string.IsNullOrWhiteSpace(b.Name) ? "Boss" : b.Name;
            _bossOldu = b.Dead;
            BilgiEkle(Bicim.Sure(s.FightDuration), RenkSoluk, false);
            if (!b.Dead)
            {
                if (b.TimeToKill is TimeSpan ttk) BilgiEkle("Kalan ~ " + Bicim.Kalan(ttk), RenkKalan, false);
                if (b.TimeToRage is TimeSpan ofke)
                {
                    if (ofke >= TimeSpan.Zero)
                    {
                        BilgiEkle("Öfke " + Bicim.Kalan(ofke), ofke.TotalSeconds <= 30 ? RenkKirmizi : RenkSoluk, false);
                        if (b.TimeToKill is TimeSpan ttk2)
                        {
                            if (ttk2 <= ofke) BilgiEkle("Yetişir", RenkYesil, true);
                            else BilgiEkle(Bicim.Kalan(ttk2 - ofke) + " geç", RenkKirmizi, true);
                        }
                    }
                    else
                    {
                        BilgiEkle("ÖFKEDE +" + Bicim.Kalan(-ofke), RenkKirmizi, true);
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
                _bossYuzde = Bicim.CanYuzde(oran);
                _bossDeger = Bicim.Tam(Math.Max(0, b.Current)) + " / " + Bicim.Tam(b.Max);
            }
            else
            {
                _bossOran = 1f; // en yüksek can bilinmiyor
                _bossYuzde = "";
                _bossDeger = Bicim.Tam(Math.Max(0, b.Current));
            }
        }

        _sure = (s.FightOver ? "Sonuç · Süre " : "Süre ") + Bicim.Sure(s.FightDuration);
        GenislikleriOlc();
    }

    private void BilgiEkle(string metin, Color renk, bool kalin)
    {
        if (_bilgiSayisi >= MaxBilgi) return;
        _bilgiMetin[_bilgiSayisi] = metin;
        _bilgiRenk[_bilgiSayisi] = renk;
        _bilgiKalin[_bilgiSayisi] = kalin;
        _bilgiSayisi++;
    }

    private void IpucuGuncelle()
    {
        string durum = _kilitli ? "kilitli" : "Ctrl+Alt+L";
        _ipucu = _demo ? "DEMO · " + durum : durum;
    }

    // ---------------- Ölçüm / boyut / ölçek ----------------

    private int P(float v) => (int)MathF.Round(v * _olcek);
    private int IkonW => P(18);
    private int Bosluk => P(8);
    private int RowH => P(20);
    private int Ayirac => P(7); // sabit üst satırın altındaki boşluk + çizgi

    private static int Olc(string metin, Font font) =>
        metin.Length == 0 ? 0 : TextRenderer.MeasureText(metin, font, Size.Empty, Temel).Width;

    private void GenislikleriSifirla()
    {
        _adW = _cpW = _toplamW = _dpsW = _dps2W = _payW = _icerikW = _siraW = 0;
    }

    /// <summary>Metinleri ölçer, pencerenin içeriğe göre gereken genişliğini hesaplar (Tick içinde; OnPaint'te değil).</summary>
    private void GenislikleriOlc()
    {
        if (_yazi is null || _yaziKalin is null || _yaziDps is null || _yaziKucuk is null) return;
        int pad = P(6);
        int gerek = 0;

        if (_durum is not null)
        {
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_durum, _yazi), P(500)));
        }
        for (int i = 0; i < _satirSayisi; i++)
        {
            var m = _satirlar[i];
            _adW = Math.Max(_adW, Math.Min(Olc(m.Ad, m.Yerel ? _yaziKalin : _yazi), P(150)));
            _cpW = Math.Max(_cpW, Olc(m.Cp, _yaziKucuk));
            _toplamW = Math.Max(_toplamW, Olc(m.Toplam, _yaziKucuk));
            _dpsW = Math.Max(_dpsW, Olc(m.Dps, _yaziDps));
            _dps2W = Math.Max(_dps2W, Olc(m.Dps2, _yaziKucuk));
            if (m.Sira.Length > 0) _siraW = Math.Max(_siraW, Olc(m.Sira, _yaziKucuk) + P(5));
            _payW = Math.Max(_payW, Olc(m.Pay, _yaziKucuk));
        }
        if (_satirSayisi > 0)
        {
            _bossEtiketW = _bossModu ? Olc(BossEtiketi, _yaziKucuk) : 0;
            int sag = (_bossModu ? _bossEtiketW + P(3) : 0) + _dpsW + Bosluk + (_dps2W > 0 ? _dps2W + Bosluk : 0) +
                      _toplamW + Bosluk + _payW + P(5);
            gerek = Math.Max(gerek, 2 * pad + P(2) + _siraW + IkonW + P(4) + _adW + Bosluk + _cpW + Bosluk + sag);
        }
        _sureW = Olc(_sure, _yaziKucuk);
        _sonucW = Olc(SonucEtiketi, _yaziKucuk) + P(8);
        if (_bossGoster)
        {
            // Satır 1: boss adı [Sonuç] ... %yy,y   Bilgi satırı: süre · Kalan ~ · Öfke · Yetişir/geç
            _bossYuzdeW = Olc(_bossYuzde, _yaziKalin);
            int rozet = _sonuc ? _sonucW + P(6) : 0;
            gerek = Math.Max(gerek, 2 * pad + Math.Min(Olc(_bossAd, _yaziKalin), P(220)) + rozet + P(12) + _bossYuzdeW);
            gerek = Math.Max(gerek, 2 * pad + Olc(_bossDeger, _yaziKucuk) + P(16));
            int bilgi = 0;
            for (int i = 0; i < _bilgiSayisi; i++)
            {
                _bilgiW[i] = Olc(_bilgiMetin[i], _bilgiKalin[i] ? _yaziKalin : _yaziKucuk);
                bilgi += _bilgiW[i] + (i > 0 ? P(12) : 0);
            }
            gerek = Math.Max(gerek, 2 * pad + bilgi);
        }
        _partiW = Olc(_partiDps, _yaziKalin);
        _oyuncuSayisiW = Olc(_oyuncuSayisi, _yaziKucuk);
        int altSure = _bossGoster ? 0 : _sureW + Bosluk;
        int sayi = _oyuncuSayisiW > 0 ? P(6) + _oyuncuSayisiW : 0;
        gerek = Math.Max(gerek, 2 * pad + altSure + _partiW + sayi + P(10) + Olc(_ipucu, _yaziKucuk));

        _icerikW = Math.Max(_icerikW, gerek);
    }

    private void OlcekAyarla()
    {
        _dpiOlcek = Math.Max(1f, DeviceDpi / 96f);
        _olcek = _dpiOlcek * _ayar.Boyut / 100f;
        _yazi?.Dispose(); _yaziKalin?.Dispose(); _yaziDps?.Dispose(); _yaziKucuk?.Dispose();
        _yazi = new Font("Segoe UI", 12f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _yaziKalin = new Font("Segoe UI", 12f * _olcek, FontStyle.Bold, GraphicsUnit.Pixel);
        _yaziDps = new Font("Segoe UI", 13f * _olcek, FontStyle.Bold, GraphicsUnit.Pixel);
        _yaziKucuk = new Font("Segoe UI", 11f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        DetayKapat(); // DPI değişti: panel yeni ölçekle yeniden açılmalı
        _ikonlar.BoyutAyarla(IkonW);
        GenislikleriSifirla();
        GenislikleriOlc();
    }

    private int HesaplaYukseklik()
    {
        int h = P(6);
        if (_bossGoster) h += P(17) + P(15) + P(16) + P(4);
        h += (_durum != null ? 1 : _satirSayisi) * RowH;
        if (_durum == null && _sabitUst) h += Ayirac;
        h += P(3) + P(16) + P(4);
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
        if (_yazi is null || _yaziKalin is null || _yaziDps is null || _yaziKucuk is null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = P(6);
        int x0 = pad, x1 = w - pad, gen = x1 - x0;
        int y = pad;

        // --- Boss ---
        if (_bossGoster)
        {
            int satirH = P(17);
            int sx = x1 - _bossYuzdeW;
            TextRenderer.DrawText(g, _bossYuzde, _yaziKalin, new Rectangle(sx, y, _bossYuzdeW, satirH),
                _bossOldu ? RenkYerel : RenkYazi, Sag);
            int rozet = _sonuc ? _sonucW + P(6) : 0;
            int adW = Math.Min(Olc(_bossAd, _yaziKalin), Math.Max(0, sx - P(8) - rozet - x0));
            TextRenderer.DrawText(g, _bossAd, _yaziKalin, new Rectangle(x0, y, adW, satirH), RenkYazi, Sol);
            if (_sonuc)
            {
                // Donmuş sonuç rozeti
                var rr = new Rectangle(x0 + adW + P(6), y + P(2), _sonucW, satirH - P(4));
                g.FillRectangle(_fircaRozet, rr);
                TextRenderer.DrawText(g, SonucEtiketi, _yaziKucuk, rr, RenkYazi, Orta);
            }
            y += satirH;

            var cubuk = new Rectangle(x0, y, gen, P(15));
            g.FillRectangle(_fircaBossArka, cubuk);
            int dolu = (int)(cubuk.Width * _bossOran);
            if (dolu > 0) g.FillRectangle(_bossOldu ? _fircaBossOldu : _fircaBossCan, cubuk.X, cubuk.Y, dolu, cubuk.Height);
            if (_bossDeger.Length > 0) TextRenderer.DrawText(g, _bossDeger, _yaziKucuk, cubuk, RenkYazi, Orta);
            y += cubuk.Height;

            int bx = x0;
            for (int i = 0; i < _bilgiSayisi; i++)
            {
                if (bx >= x1) break;
                TextRenderer.DrawText(g, _bilgiMetin[i], _bilgiKalin[i] ? _yaziKalin : _yaziKucuk,
                    new Rectangle(bx, y, Math.Min(_bilgiW[i], x1 - bx), P(16)), _bilgiRenk[i], Sol);
                bx += _bilgiW[i] + P(12);
            }
            y += P(16) + P(4);
        }

        // --- Satırlar ya da durum ---
        int rowH = RowH;
        if (_durum != null)
        {
            TextRenderer.DrawText(g, _durum, _yazi, new Rectangle(x0, y, gen, rowH), RenkSoluk, Sol);
            y += rowH;
        }
        else
        {
            _satirlarUst = y;
            int ikon = IkonW, bosluk = Bosluk;
            int payX = x1 - P(5) - _payW;
            int toplamX = payX - bosluk - _toplamW;
            int dps2X = _dps2W > 0 ? toplamX - bosluk - _dps2W : toplamX;
            int dpsX = dps2X - bosluk - _dpsW;
            int etiketX = _bossModu ? dpsX - P(3) - _bossEtiketW : dpsX;
            for (int i = 0; i < _satirSayisi; i++)
            {
                var m = _satirlar[i];
                bool sabitSatir = _sabitUst && i == 0;
                if (sabitSatir) g.FillRectangle(_fircaSabitSatir, x0, y, gen, rowH);
                int pay = (int)(gen * m.Oran);
                if (pay > 0) g.FillRectangle(m.Yerel ? _fircaPayYerel : _fircaPay, x0, y + 1, pay, rowH - 2);
                if (_detay is { Visible: true } && _detay.OyuncuId == m.Id)
                    g.DrawRectangle(_kalemSecili, x0, y + 1, gen - 1, rowH - 3);

                int x = x0 + P(2);
                if (_siraW > 0)
                {
                    // Sıra: sabit satırda "#14" (vurgulu), ilk 5'te küçük ve soluk "1..5"
                    TextRenderer.DrawText(g, m.Sira, sabitSatir ? _yaziKalin : _yaziKucuk, new Rectangle(x, y, _siraW - P(5), rowH),
                        sabitSatir ? RenkYerel : RenkCokSoluk, Sag);
                    x += _siraW;
                }
                g.DrawImage(_ikonlar.Al(m.Sinif), x, y + (rowH - ikon) / 2, ikon, ikon);
                x += ikon + P(4);
                TextRenderer.DrawText(g, m.Ad, m.Yerel ? _yaziKalin : _yazi, new Rectangle(x, y, _adW, rowH),
                    m.Yerel ? RenkYerel : RenkYazi, Sol);
                x += _adW + bosluk;
                TextRenderer.DrawText(g, m.Cp, _yaziKucuk, new Rectangle(x, y, Math.Max(0, etiketX - bosluk - x), rowH), RenkCokSoluk, Sol);
                // DPS birincil: kalın, büyük, parlak (boss varken Boss DPS, küçük "boss" etiketiyle).
                // Genel DPS (boss varken), toplam ve katkı % ikincil: küçük ve soluk.
                if (_bossModu)
                    TextRenderer.DrawText(g, BossEtiketi, _yaziKucuk, new Rectangle(etiketX, y, _bossEtiketW, rowH), RenkCokSoluk, Sag);
                TextRenderer.DrawText(g, m.Dps, _yaziDps, new Rectangle(dpsX, y, _dpsW, rowH), m.Yerel ? RenkYerel : RenkDps, Sag);
                if (_dps2W > 0)
                    TextRenderer.DrawText(g, m.Dps2, _yaziKucuk, new Rectangle(dps2X, y, _dps2W, rowH), RenkSoluk, Sag);
                TextRenderer.DrawText(g, m.Toplam, _yaziKucuk, new Rectangle(toplamX, y, _toplamW, rowH), RenkSoluk, Sag);
                TextRenderer.DrawText(g, m.Pay, _yaziKucuk, new Rectangle(payX, y, _payW, rowH), RenkCokSoluk, Sag);
                y += rowH;
                if (sabitSatir)
                {
                    int cy = y + Ayirac / 2;
                    g.DrawLine(_kalemAyirac, x0, cy, x1, cy);
                    y += Ayirac;
                }
            }
        }

        // --- Alt satır: süre, parti DPS, ipucu ---
        y += P(3);
        int altH = P(16);
        int x2 = x0;
        if (!_bossGoster)
        {
            TextRenderer.DrawText(g, _sure, _yaziKucuk, new Rectangle(x0, y, _sureW, altH), RenkSoluk, Sol);
            x2 += _sureW + Bosluk;
        }
        if (_partiDps.Length > 0)
            TextRenderer.DrawText(g, _partiDps, _yaziKalin, new Rectangle(x2, y, Math.Min(_partiW, x1 - x2), altH), RenkParti, Sol);
        int sayiX = x2 + _partiW + P(6);
        if (_oyuncuSayisiW > 0 && sayiX < x1)
            TextRenderer.DrawText(g, _oyuncuSayisi, _yaziKucuk, new Rectangle(sayiX, y, Math.Min(_oyuncuSayisiW, x1 - sayiX), altH), RenkSoluk, Sol);
        int ipucuX = x2 + _partiW + (_oyuncuSayisiW > 0 ? P(6) + _oyuncuSayisiW : 0) + P(10);
        if (ipucuX < x1 - P(20))
            TextRenderer.DrawText(g, _ipucu, _yaziKucuk, new Rectangle(ipucuX, y, x1 - ipucuX, altH), RenkSoluk,
                Sag | TextFormatFlags.EndEllipsis);

        g.DrawRectangle(_kilitli ? _kalemKilitli : _kalemKilitsiz, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare: sürükle / genişlik ----------------

    private bool KenardaMi(int x) => x >= ClientSize.Width - P(7);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_kilitli)
        {
            if (_altAcik && e.Button == MouseButtons.Left) AltTiklandi(e.Y); // Alt+tık: sabitle / bırak
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
            if (_altAcik) UzerineGelindi(e.Y);
            return;
        }
        if (!_surukle)
        {
            Cursor = KenardaMi(e.X) ? Cursors.SizeWE : Cursors.SizeAll;
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
        else if (!_kenardan && e.Button == MouseButtons.Left) SatiraTiklandi(e.Y);
    }

    // ---------------- Detay paneli ----------------

    private int SatirIndeksi(int y)
    {
        if (_durum != null || _satirSayisi == 0 || y < _satirlarUst) return -1;
        int yy = y - _satirlarUst;
        if (_sabitUst)
        {
            if (yy < RowH) return 0;          // sabit kendi satırın
            yy -= RowH + Ayirac;
            if (yy < 0) return -1;            // ayırıcı
            int k = 1 + yy / RowH;
            return k < _satirSayisi ? k : -1;
        }
        int i = yy / RowH;
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

    /// <summary>Paneli açar ya da oyuncuyu değiştirir. sabit null ise sabitleme durumu değişmez (Alt ile üzerine gelme).</summary>
    private void DetayAc(long id, bool? sabit)
    {
        if (_detay is null)
        {
            byte alfa = (byte)Math.Clamp(_ayar.Opaklik * 255 / 100, 40, 255);
            _detay = new DetayForm(_motor, _ikonlar, _olcek, alfa);
            _detay.KapatIstendi += DetayKapat;
        }
        if (sabit is bool b)
        {
            _sabit = b;
            if (b) _sabitId = id;
        }
        _detay.TiklanmazAyarla(_kilitli && !_altAcik);
        _detay.Goster(id, Bounds); // SW_SHOWNOACTIVATE
        Invalidate();
    }

    // ---------------- Kilitliyken Alt ile göz atma ----------------

    private void AltZamanlayiciGuncelle()
    {
        // Demoda oyun yok: oyun-önde şartı aranmaz (test edilebilsin).
        bool oyunOnde = _demo || _onPlan.OyunOnde();
        bool calismali = oyunOnde && ((_kilitli && _gorunur) || _cevirmen.AltCeviriCalisir);
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
            bool bizimUstunde = (_gorunur && Bounds.Contains(imlec)) || (_detay is { Visible: true } && _detay.Bounds.Contains(imlec));

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
                }
                if (_altAcik && bizimUstunde)
                {
                    // Stil değişiminden hemen sonra WM_MOUSEMOVE gelmeyebilir: imleci burada da yokla.
                    var p = PointToClient(imlec);
                    if (ClientRectangle.Contains(p)) UzerineGelindi(p.Y);
                }
            }
            else if (_altAcik)
            {
                AltBirakildi();
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

    private void UzerineGelindi(int y)
    {
        int i = SatirIndeksi(y);
        if (i < 0) return;
        long id = _satirlar[i].Id;
        if (id == _hoverBastirId) return;
        _hoverBastirId = long.MinValue;
        if (_detay is { Visible: true } && _detay.OyuncuId == id) return;
        DetayAc(id, sabit: null);
    }

    private void AltTiklandi(int y)
    {
        int i = SatirIndeksi(y);
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
        if (!_gorunur) return;
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
        TiklanmazlikUygula(); // overlay + sabit panel: kilitliyse tıklanmaz
        if (IsHandleCreated)
        {
            long ex = Native.GetExStyle(Handle) | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetExStyle(Handle, ex);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            SaydamlikUygula();
        }
        Cursor = Cursors.Default;
        AltZamanlayiciGuncelle(); // kilitsizken Alt yoklaması durur
        TepsiGuncelle();
        IpucuGuncelle();
        _onceki = null; // ipucu genişliği değişti: modeli yeniden kur
        Tick();
        Invalidate();
        AyarKaydet();
    }

    private void GosterDegistir()
    {
        _kullaniciGizledi = !_kullaniciGizledi;
        if (_kullaniciGizledi) DetayKapat();
        GorunurlukUygula(); // SW_HIDE / SW_SHOWNOACTIVATE
        if (_gorunur) Tick();
        TepsiGuncelle();
    }

    private void Sifirla()
    {
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
            _cevirmen.Dispose();
            DetayKapat();
            _kisayol?.Dispose();
            _tepsi?.Dispose();
            _ikonlar.Dispose();
            _yazi?.Dispose(); _yaziKalin?.Dispose(); _yaziDps?.Dispose(); _yaziKucuk?.Dispose();
            _fircaPay.Dispose(); _fircaPayYerel.Dispose(); _fircaBossArka.Dispose();
            _fircaBossCan.Dispose(); _fircaBossOldu.Dispose();
            _kalemKilitsiz.Dispose(); _kalemKilitli.Dispose(); _kalemSecili.Dispose(); _fircaRozet.Dispose();
            _fircaSabitSatir.Dispose(); _kalemAyirac.Dispose();
        }
        base.Dispose(disposing);
    }
}
