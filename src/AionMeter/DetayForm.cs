using System.Drawing.Drawing2D;

namespace AionMeter;

/// <summary>
/// Satıra tıklanınca overlay'in yanında açılan oyuncu detayı. Kenarlıksız, en üstte, odak çalmaz
/// (WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE). Kendisi çizer; sadece görünürken 2 Hz yenilenir (overlay'in zamanlayıcısıyla).
/// Kapatılınca overlay tarafından Dispose edilir. Renkler ortak paletten; grafik ve beceri çubukları oyuncunun sınıf renginde.
/// </summary>
internal sealed class DetayForm : Form
{
    private const int MaxBeceri = 12;

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;

    private static readonly string[] SutunBasliklari = { "Beceri", "Hasar", "Pay", "Vuruş", "Kritik", "En yüksek" };
    // 3 × 3 istatistik ızgarası (sütun sütun okunur)
    private static readonly string[] IstatEtiketleri =
        { "Vuruş", "Ortalama", "En yüksek", "Kritik", "Double", "Perfect", "Arkadan", "Parry", "Ölüm" };
    private const int IstatSutun = 3, IstatSatir = 3;

    private readonly IMeterEngine _motor;
    private readonly SinifIkonlari _ikonlar;
    private readonly BeceriIkonlari _beceriIkonlari; // panel ömrü kadar yaşar; kapanınca Dispose
    private readonly Func<long, PlayerDamage?> _oyuncuAl; // ölüm sayısı ve "yerel mi" son anlık görüntüden
    private readonly float _olcek;
    private byte _alfa;
    private readonly Font _yazi, _yaziYari, _yaziBuyuk, _yaziKucuk, _yaziAd;
    // Sayılar overlay gibi Bahnschrift, sabit genişlikli rakamla (sütunlar hizalı, akan sayı yana kaymaz)
    private readonly RakamYazisi _rYazi, _rYari, _rBuyuk;
    private readonly Pen _kalemCizgi = new(Palet.Cizgi);
    private readonly Pen _kalemKenar = new(Palet.Cizgi);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly Pen _kalemReferans = new(Color.FromArgb(40, 44, 54));
    private readonly Pen _kalemOrtalama = new(Color.FromArgb(150, Palet.Soluk)) { DashStyle = DashStyle.Dash };
    private readonly Pen _kalemGrafik = new(Palet.Vurgu, 1.5f);
    private readonly SolidBrush _fircaGrafik = new(Color.FromArgb(36, Palet.Vurgu));
    private readonly SolidBrush _fircaGrafikArka = new(Palet.Panel);
    private readonly SolidBrush _fircaBeceri = new(Palet.Cizgi);
    private int _renkSinifi = -1;

    // DPS grafiği: kovalar → saniye başına DPS, 5 sn kayan ortalama. Diziler yeniden kullanılır.
    private float[] _grafikDps = new float[64];
    private int _grafikN;
    private float _grafikMaks, _grafikOrt;
    private string _grafikMaksMetin = "", _grafikOrtMetin = "", _grafikOrta = "", _grafikSon = "";
    private PointF[] _cizgi = Array.Empty<PointF>(), _alan = Array.Empty<PointF>();

    // Model
    private sealed class BeceriModel
    {
        public string Ad = "", Hasar = "", Pay = "", Vurus = "", Kritik = "", Maks = "";
        public string? Ikon;
        public float Oran; // en çok hasar veren beceriye göre (ad arkasındaki çubuk)
    }
    private readonly BeceriModel[] _beceriler = new BeceriModel[MaxBeceri];
    private readonly string[] _istatDeger = new string[IstatEtiketleri.Length];
    private int _beceriSayisi;
    private bool _veriVar, _yerel, _oldu;
    private string _ad = "", _cp = "", _dps = "", _ozet = "";
    private string? _sinif;

    // Değişiklik kontrolü
    private long _sonToplam = -1, _sonVurus = -1, _sonAktifSn = -1, _sonKovaDeger = -1, _sonDps = -1;
    private int _sonBeceri = -1, _sonKovaSayisi = -1, _sonOlum = -1;

    // Ölçüler (fiziksel px; açık kaldığı sürece sadece büyür)
    private readonly int _hasarW, _payW, _vurusW, _kritikW, _maksW;
    private int _adSutunW, _icerikW;
    private Rectangle _kapatKutusu;
    private Rectangle _sonOverlay;

    public long OyuncuId { get; private set; }
    /// <summary>Overlay boss modunda mı (satırdaki ana sayı boss/sn). Başlıktaki DPS satırdakiyle aynı kaynaktan okunur.</summary>
    public Func<bool>? BossModu; // alan (özellik değil): tasarımcı serileştirmesi yok
    private bool _tiklanmaz; // kilitli overlay'de sabit panel: Alt basılı değilken tıklamalar oyuna geçer
    public event Action? KapatIstendi;

    public DetayForm(IMeterEngine motor, SinifIkonlari ikonlar, float olcek, byte alfa, Func<long, PlayerDamage?> oyuncuAl,
                     float adOlcek = 1f, float dpsOlcek = 1f)
    {
        _motor = motor;
        _ikonlar = ikonlar;
        _olcek = olcek;
        _alfa = alfa;
        _oyuncuAl = oyuncuAl;
        for (int i = 0; i < MaxBeceri; i++) _beceriler[i] = new BeceriModel();
        for (int i = 0; i < _istatDeger.Length; i++) _istatDeger[i] = "";

        Text = "AionMeter Detay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _yazi = Palet.YaziTipi(12f * olcek);
        _yaziYari = Palet.YaziTipi(12f * olcek, yariKalin: true);
        // Ad ve DPS yazıları Ayarlar → Renk ve yazı'daki yüzdelerle (overlay ile aynı)
        _yaziAd = Palet.YaziTipi(14f * olcek * adOlcek, yariKalin: true);
        _yaziBuyuk = Palet.YaziTipi(16f * olcek * dpsOlcek, yariKalin: true);
        _rYazi = new RakamYazisi(Palet.RakamTipi(12f * olcek));
        _rYari = new RakamYazisi(Palet.RakamTipi(12f * olcek, yariKalin: true));
        _rBuyuk = new RakamYazisi(Palet.RakamTipi(16f * olcek * dpsOlcek, yariKalin: true));
        _baslikH = Math.Max(P(26), Olc("Ag", _yaziAd, dikey: true) + P(4));
        _dpsH = Math.Max(P(24), Math.Max(Olc("Ag", _yaziBuyuk, dikey: true), _rBuyuk.Yukseklik) + P(2));
        _yaziKucuk = Palet.YaziTipi(11f * olcek);
        _kalemGrafik.Width = Math.Max(1f, 1.5f * olcek);

        // Sayı sütunları şablonla (rakam yazısıyla, sabit genişlikli rakam) bir kez ölçülür; her yenilemede tekrar ölçülmez.
        _hasarW = Math.Max(_rYari.Genislik("99.999.999"), Olc("Hasar", _yaziKucuk));
        _payW = Math.Max(_rYazi.Genislik("%100,0"), Olc("Pay", _yaziKucuk));
        _vurusW = Math.Max(_rYazi.Genislik("99.999"), Olc("Vuruş", _yaziKucuk));
        _kritikW = Math.Max(_rYazi.Genislik("%100,0"), Olc("Kritik", _yaziKucuk));
        _maksW = Math.Max(_rYazi.Genislik("9.999.999"), Olc("En yüksek", _yaziKucuk));
        _adSutunW = P(110);
        _beceriIkonlari = new BeceriIkonlari(P(16));
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);
    private int Pad => P(10);
    private int Bosluk => P(12);   // sütun arası
    private int SatirH => P(20);
    private int IstatH => P(18);
    private readonly int _baslikH, _dpsH;
    private int BaslikH => _baslikH;  // ad satırı (en az 26)
    private int DpsH => _dpsH;        // DPS satırı (en az 24)
    private int EtiketH => P(15);  // grafik üstü / altı etiket satırları
    private int GrafikH => P(72);

    private static int Olc(string metin, Font font, bool dikey = false)
    {
        if (metin.Length == 0) return 0;
        var b = TextRenderer.MeasureText(metin, font, Size.Empty, Temel);
        return dikey ? b.Height : b.Width;
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
            if (_tiklanmaz) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, _alfa, Native.LWA_ALPHA);
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* yok sayılır */ }
    }

    // ---------------- Dış arayüz ----------------

    /// <summary>Saydamlık ayarı değişti (ayarlar penceresi): canlı uygula.</summary>
    public void AlfaAyarla(byte alfa)
    {
        if (alfa == _alfa) return;
        _alfa = alfa;
        if (IsHandleCreated) Native.SetLayeredWindowAttributes(Handle, 0, _alfa, Native.LWA_ALPHA);
    }

    /// <summary>Renk ayarı değişti (ayarlar penceresi): grafik/beceri renkleri ve kendi ad rengin yeniden.</summary>
    public void RenkYenile()
    {
        _renkSinifi = -1;
        if (_veriVar) RenkleriAyarla();
        Invalidate();
    }

    /// <summary>Paneli tıklanmaz (WS_EX_TRANSPARENT) yapar ya da geri alır. Odak hiçbir durumda değişmez.</summary>
    public void TiklanmazAyarla(bool tiklanmaz)
    {
        if (_tiklanmaz == tiklanmaz) return;
        _tiklanmaz = tiklanmaz;
        if (!IsHandleCreated) return; // tutamaç yaratılınca CreateParams uygular
        long ex = Native.GetExStyle(Handle);
        Native.SetExStyle(Handle, tiklanmaz ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT);
    }

    /// <summary>Oyuncuyu gösterir (ya da başka oyuncuya geçer) ve overlay'in yanına yerleşir.</summary>
    public void Goster(long oyuncuId, Rectangle overlay)
    {
        if (oyuncuId != OyuncuId)
        {
            OyuncuId = oyuncuId;
            _sonToplam = _sonVurus = _sonAktifSn = _sonKovaDeger = _sonDps = -1;
            _sonBeceri = _sonKovaSayisi = _sonOlum = -1;
            _adSutunW = P(110);
            _icerikW = 0;
        }
        _sonOverlay = overlay;
        Yenile();
        if (!Visible) Show(); // ShowWithoutActivation => SW_SHOWNOACTIVATE
    }

    /// <summary>Overlay zamanlayıcısından (2 Hz) çağrılır; sadece veri değiştiyse yeniden çizer.</summary>
    public void Yenile()
    {
        PlayerDetail? d = null;
        try { d = _motor.Detail(OyuncuId, BossModu?.Invoke()); } // satırla aynı mod: başlıktaki DPS, Toplam ve Aktif aynı kaynaktan
        catch (Exception ex) { HataLog.Yaz("Detay", ex); }
        var p = _oyuncuAl(OyuncuId);

        long toplam = d?.Total ?? -2, vurus = d?.Hits?.Hits ?? -2, aktif = (long)(d?.Active.TotalSeconds ?? -2);
        int beceri = d?.Skills?.Count ?? -2;
        var kovalar = d?.DamagePerBucket;
        int kovaSayisi = kovalar?.Count ?? -2;
        long kovaDeger = kovaSayisi > 0 ? kovalar![kovaSayisi - 1] : -2;
        int olum = p?.Olum ?? 0;
        long dps = (long)Math.Round(BaslikDps(d, p)); // satırdaki sayı (gösterilen tam sayı)
        if (toplam == _sonToplam && vurus == _sonVurus && aktif == _sonAktifSn && beceri == _sonBeceri &&
            kovaSayisi == _sonKovaSayisi && kovaDeger == _sonKovaDeger && olum == _sonOlum)
        {
            // Beceri modeli aynı: satırdaki DPS değiştiyse yalnız başlıktaki metin yenilenir (tam model kurulmaz).
            if (dps == _sonDps || d is null) return;
            _sonDps = dps;
            DpsMetniKur(d, p);
            Invalidate();
            return;
        }
        _sonToplam = toplam; _sonVurus = vurus; _sonAktifSn = aktif; _sonBeceri = beceri;
        _sonKovaSayisi = kovaSayisi; _sonKovaDeger = kovaDeger; _sonOlum = olum; _sonDps = dps;

        ModelKur(d, p);
        Yerlestir(_sonOverlay);
        Invalidate();
    }

    /// <summary>Başlıktaki DPS metni ve ikinci satırın genişliği (panel yalnız büyür; sığmazsa yeniden yerleşir).</summary>
    private void DpsMetniKur(PlayerDetail d, PlayerDamage? p)
    {
        _dps = Bicim.Dps(BaslikDps(d, p));
        int ikinci = IkinciSatirW();
        if (ikinci <= _icerikW) return;
        _icerikW = ikinci;
        Yerlestir(_sonOverlay);
    }

    private int IkinciSatirW() => 2 * Pad + _rBuyuk.Genislik(_dps) + P(14) + Olc(_ozet, _yaziKucuk);

    /// <summary>Overlay'in sağına sığıyorsa sağa, değilse soluna yerleşir; ekran dışına taşmaz.</summary>
    public void Yerlestir(Rectangle overlay)
    {
        _sonOverlay = overlay;
        var boyut = new Size(Math.Max(_icerikW, P(320)), HesaplaYukseklik());
        var alan = Screen.FromRectangle(overlay).WorkingArea;
        int ara = P(4);
        int x;
        if (overlay.Right + ara + boyut.Width <= alan.Right) x = overlay.Right + ara;
        else if (overlay.Left - ara - boyut.Width >= alan.Left) x = overlay.Left - ara - boyut.Width;
        else x = Math.Max(alan.Left, alan.Right - boyut.Width);
        int y = Math.Clamp(overlay.Top, alan.Top, Math.Max(alan.Top, alan.Bottom - boyut.Height));
        var yeni = new Rectangle(new Point(x, y), boyut);
        if (Bounds != yeni) Bounds = yeni;
    }

    // ---------------- Model ----------------

    /// <summary>Başlıktaki DPS: satırda gösterilen sayının aynısı (boss modunda boss/sn, değilse /sn); satır yoksa detayın DPS'i.</summary>
    private double BaslikDps(PlayerDetail? d, PlayerDamage? p)
    {
        if (p is not null) return BossModu?.Invoke() == true ? p.BossDps : p.Dps;
        return d?.Dps ?? 0;
    }

    private void ModelKur(PlayerDetail? d, PlayerDamage? p)
    {
        _veriVar = d is not null;
        if (d is null)
        {
            _beceriSayisi = 0;
            _icerikW = Math.Max(_icerikW, P(320));
            return;
        }

        _ad = string.IsNullOrWhiteSpace(d.Name) ? "?" : d.Name;
        _sinif = d.ClassName;
        _yerel = p?.IsLocal ?? false;
        _cp = d.CombatPower is int cp ? Bicim.Tam(cp) + " CP" : "— CP";
        _dps = Bicim.Dps(BaslikDps(d, p));
        _ozet = "Toplam " + Bicim.Tam(Math.Max(0, d.Total)) + "   Aktif " + Bicim.Sure(d.Active);
        RenkleriAyarla();

        var h = d.Hits ?? new HitStats(0, 0, 0, 0, 0, 0, 0);
        int olum = p?.Olum ?? 0;
        _oldu = olum > 0;
        _istatDeger[0] = Bicim.Tam(h.Hits);
        _istatDeger[1] = h.Hits > 0 ? Bicim.Tam((long)Math.Round((double)Math.Max(0, d.Total) / h.Hits)) : "—";
        _istatDeger[2] = Bicim.Tam(h.MaxHit);
        _istatDeger[3] = Bicim.Oran(h.Crits, h.Hits);
        _istatDeger[4] = Bicim.Oran(h.Doubles, h.Hits);
        _istatDeger[5] = Bicim.Oran(h.Perfects, h.Hits);
        _istatDeger[6] = Bicim.Oran(h.Backs, h.Hits);
        _istatDeger[7] = Bicim.Oran(h.Parries, h.Hits);
        _istatDeger[8] = olum.ToString(Bicim.Tr);

        var liste = d.Skills ?? Array.Empty<SkillDamage>();
        int n = Math.Min(liste.Count, MaxBeceri);
        long enCok = 1;
        for (int i = 0; i < n; i++) enCok = Math.Max(enCok, liste[i].Total);
        for (int i = 0; i < n; i++)
        {
            var s = liste[i];
            var m = _beceriler[i];
            m.Ad = string.IsNullOrWhiteSpace(s.Name) ? "Beceri #" + s.SkillId.ToString(Bicim.Tr) : s.Name;
            m.Hasar = Bicim.Tam(Math.Max(0, s.Total));
            m.Pay = Bicim.Oran(s.Total, d.Total);
            m.Vurus = Bicim.Tam(s.Hits);
            m.Kritik = Bicim.Oran(s.Crits, s.Hits);
            m.Maks = Bicim.Tam(s.MaxHit);
            m.Ikon = s.IconToken;
            m.Oran = s.Total > 0 ? Math.Clamp((float)((double)s.Total / enCok), 0f, 1f) : 0f;
            _adSutunW = Math.Max(_adSutunW, _beceriIkonlari.Boyut + P(6) + Math.Min(Olc(m.Ad, _yazi), P(200)) + P(6));
        }
        _beceriSayisi = n;
        GrafikKur(d);

        int pad = Pad, b = Bosluk;
        int tablo = 2 * pad + _adSutunW + b + _hasarW + b + _payW + b + _vurusW + b + _kritikW + b + _maksW;
        int baslik = 2 * pad + _ikonlar.Boyut + P(8) + Math.Min(Olc(_ad, _yaziAd), P(200)) + P(10) + Olc(_cp, _yaziKucuk) + P(30);
        int ikinci = IkinciSatirW();
        int istat = 2 * pad + IstatSutun * (Olc("Perfect", _yaziKucuk) + P(10) + _rYazi.Genislik("9.999.999")) + (IstatSutun - 1) * P(20);
        _icerikW = Math.Max(_icerikW, Math.Max(Math.Max(tablo, istat), Math.Max(baslik, ikinci)));
    }

    /// <summary>Grafik ve beceri çubukları oyuncunun sınıf renginde (fırçalar sadece sınıf değişince güncellenir).</summary>
    private void RenkleriAyarla()
    {
        int s = Palet.SinifIndeksi(_sinif);
        if (s == _renkSinifi) return;
        _renkSinifi = s;
        var r = Palet.Sinif(_sinif);
        var acik = s == 0 ? Palet.Vurgu : r.Acik;
        _kalemGrafik.Color = acik;
        _fircaGrafik.Color = Color.FromArgb(36, acik);
        _fircaBeceri.Color = r.Koyu;
    }

    private void GrafikKur(PlayerDetail d)
    {
        var k = d.DamagePerBucket;
        _grafikN = 0;
        if (k is null || k.Count == 0) return;
        int n = k.Count;
        int bs = Math.Max(1, d.BucketSeconds);
        if (_grafikDps.Length < n) _grafikDps = new float[Math.Max(n, _grafikDps.Length * 2)];

        // 5 sn'lik kayan ortalama (geriye dönük), kova → saniye başına DPS
        int pencere = Math.Max(1, (int)Math.Round(5.0 / bs));
        double toplam = 0, maks = 1, hepsi = 0;
        for (int i = 0; i < n; i++)
        {
            long v0 = Math.Max(0, k[i]);
            hepsi += v0;
            toplam += v0;
            if (i >= pencere) toplam -= Math.Max(0, k[i - pencere]);
            int adet = Math.Min(i + 1, pencere);
            double v = toplam / adet / bs;
            _grafikDps[i] = (float)v;
            if (v > maks) maks = v;
        }
        _grafikN = n;
        _grafikMaks = (float)maks;
        _grafikOrt = (float)(hepsi / ((double)n * bs));
        // Birim başlıkta ("DPS · 5 sn ortalama"): maks ve ort. aynı biçimde, birimsiz
        _grafikMaksMetin = "maks " + Bicim.Sayi(maks);
        _grafikOrtMetin = "ort. " + Bicim.Sayi(_grafikOrt);
        _grafikOrta = Bicim.Sure(TimeSpan.FromSeconds((double)n * bs / 2));
        _grafikSon = Bicim.Sure(TimeSpan.FromSeconds((double)n * bs));
    }

    private int HesaplaYukseklik()
    {
        int h = Pad + BaslikH + DpsH;          // ad satırı + DPS satırı
        if (!_veriVar) return h + Pad;
        h += P(6) + EtiketH + GrafikH + EtiketH;          // maks etiketi + grafik + zaman ekseni
        h += P(8) + 1 + P(6) + IstatSatir * IstatH;        // çizgi + 3 satır istatistik
        h += P(8) + 1 + P(6) + SatirH + 1 + P(2) + _beceriSayisi * SatirH; // çizgi + başlık + alt çizgi + beceriler
        return h + Pad;
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Detay çizimi", ex); }
    }

    private void Ciz(Graphics g)
    {
        int w = ClientSize.Width, hh = ClientSize.Height;
        int pad = Pad, x0 = pad, x1 = w - pad, y = pad;

        // Kapat (✕) hedefi: sağ üst köşe
        int k = P(16);
        _kapatKutusu = new Rectangle(w - pad - k, pad + (BaslikH - k) / 2, k, k);
        int ki = P(4);
        var eskiMod = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLine(_kalemKapat, _kapatKutusu.Left + ki, _kapatKutusu.Top + ki, _kapatKutusu.Right - ki, _kapatKutusu.Bottom - ki);
        g.DrawLine(_kalemKapat, _kapatKutusu.Right - ki, _kapatKutusu.Top + ki, _kapatKutusu.Left + ki, _kapatKutusu.Bottom - ki);
        g.SmoothingMode = eskiMod;

        if (!_veriVar)
        {
            TextRenderer.DrawText(g, "Bu oyuncu için veri yok", _yazi, new Rectangle(x0, y, x1 - x0 - k, BaslikH), Palet.Soluk, Sol);
            g.DrawRectangle(_kalemKenar, 0, 0, w - 1, hh - 1);
            return;
        }

        // Satır 1: ikon · ad · CP
        int ikon = _ikonlar.Boyut;
        g.DrawImage(_ikonlar.Al(_sinif), x0, y + (BaslikH - ikon) / 2, ikon, ikon);
        int x = x0 + ikon + P(8);
        int adW = Math.Min(Olc(_ad, _yaziAd), _kapatKutusu.Left - x - P(80));
        TextRenderer.DrawText(g, _ad, _yaziAd, new Rectangle(x, y, Math.Max(0, adW), BaslikH), _yerel ? Palet.YerelYazi : Palet.Yazi, Sol);
        x += Math.Max(0, adW) + P(10);
        TextRenderer.DrawText(g, _cp, _yaziKucuk, new Rectangle(x, y, Math.Max(0, _kapatKutusu.Left - P(6) - x), BaslikH), Palet.Soluk, Sol);
        y += BaslikH;

        // Satır 2: DPS (birincil) · toplam · aktif süre
        int dpsW = _rBuyuk.Genislik(_dps);
        _rBuyuk.Ciz(g, _dps, new Rectangle(x0, y, dpsW, DpsH), Palet.Dps, sag: false);
        x = x0 + dpsW + P(14);
        TextRenderer.DrawText(g, _ozet, _yaziKucuk, new Rectangle(x, y, Math.Max(0, x1 - x), DpsH), Palet.Soluk, Sol);
        y += DpsH;

        // DPS grafiği: üstte başlık ve maks (grafiğin dışında), altta zaman ekseni (başlangıç / orta / son)
        y += P(6);
        TextRenderer.DrawText(g, "DPS · 5 sn ortalama", _yaziKucuk, new Rectangle(x0, y, (x1 - x0) / 2, EtiketH), Palet.CokSoluk, Sol);
        if (_grafikN > 1)
            TextRenderer.DrawText(g, _grafikMaksMetin, _yaziKucuk, new Rectangle(x0 + (x1 - x0) / 2, y, (x1 - x0) / 2, EtiketH), Palet.Soluk, Sag);
        y += EtiketH;
        GrafikCiz(g, new Rectangle(x0, y, x1 - x0, GrafikH));
        y += GrafikH;
        int ucte = (x1 - x0) / 3;
        TextRenderer.DrawText(g, "0:00", _yaziKucuk, new Rectangle(x0, y, ucte, EtiketH), Palet.CokSoluk, Sol);
        if (_grafikN > 1)
        {
            TextRenderer.DrawText(g, _grafikOrta, _yaziKucuk, new Rectangle(x0 + ucte, y, x1 - x0 - 2 * ucte, EtiketH), Palet.CokSoluk,
                TextFormatFlags.HorizontalCenter | Temel);
            TextRenderer.DrawText(g, _grafikSon, _yaziKucuk, new Rectangle(x1 - ucte, y, ucte, EtiketH), Palet.CokSoluk, Sag);
        }
        y += EtiketH;

        // İstatistikler: 3 sütun × 3 satır
        y += P(8);
        g.DrawLine(_kalemCizgi, x0, y, x1, y);
        y += 1 + P(6);
        int araSutun = P(20);
        int sutunW = (x1 - x0 - (IstatSutun - 1) * araSutun) / IstatSutun;
        for (int i = 0; i < IstatEtiketleri.Length; i++)
        {
            int sutun = i / IstatSatir, satir = i % IstatSatir;
            TextRenderer.DrawText(g, IstatEtiketleri[i], _yaziKucuk,
                new Rectangle(x0 + sutun * (sutunW + araSutun), y + satir * IstatH, sutunW, IstatH), Palet.Soluk, Sol);
        }
        IntPtr hdc = RakamYazisi.Ac(g); // değerler tek HDC oturumunda (arada Graphics'e çizilmez)
        try
        {
            for (int i = 0; i < IstatEtiketleri.Length; i++)
            {
                int sutun = i / IstatSatir, satir = i % IstatSatir;
                var renk = i == 8 && _oldu ? Palet.Kirmizi : Palet.Yazi;
                _rYazi.Ciz(hdc, _istatDeger[i], new Rectangle(x0 + sutun * (sutunW + araSutun), y + satir * IstatH, sutunW, IstatH), renk, sag: true);
            }
        }
        finally
        {
            RakamYazisi.Kapat(g, hdc);
        }
        y += IstatSatir * IstatH;

        // Beceri tablosu
        y += P(8);
        g.DrawLine(_kalemCizgi, x0, y, x1, y);
        y += 1 + P(6);
        int b = Bosluk;
        int maksX = x1 - _maksW;
        int kritikX = maksX - b - _kritikW;
        int vurusX = kritikX - b - _vurusW;
        int payX = vurusX - b - _payW;
        int hasarX = payX - b - _hasarW;
        int adSutun = hasarX - b - x0;

        var bc = Palet.CokSoluk;
        TextRenderer.DrawText(g, SutunBasliklari[0], _yaziKucuk, new Rectangle(x0, y, adSutun, SatirH), bc, Sol);
        TextRenderer.DrawText(g, SutunBasliklari[1], _yaziKucuk, new Rectangle(hasarX, y, _hasarW, SatirH), bc, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[2], _yaziKucuk, new Rectangle(payX, y, _payW, SatirH), bc, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[3], _yaziKucuk, new Rectangle(vurusX, y, _vurusW, SatirH), bc, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[4], _yaziKucuk, new Rectangle(kritikX, y, _kritikW, SatirH), bc, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[5], _yaziKucuk, new Rectangle(maksX, y, _maksW, SatirH), bc, Sag);
        y += SatirH;
        g.DrawLine(_kalemCizgi, x0, y, x1, y); // başlık altı çizgi
        y += 1 + P(2);

        int bi = _beceriIkonlari.Boyut;
        for (int i = 0; i < _beceriSayisi; i++)
        {
            var m = _beceriler[i];
            // Ad arkasında sınıf renginde koyu pay çubuğu (en çok vuran beceriye göre)
            int cubuk = (int)(adSutun * m.Oran);
            if (cubuk > 0) g.FillRectangle(_fircaBeceri, x0, y + P(2), cubuk, SatirH - P(4));
            g.DrawImage(_beceriIkonlari.Al(m.Ikon), x0 + P(2), y + (SatirH - bi) / 2, bi, bi);
            int adX = x0 + P(2) + bi + P(6);
            TextRenderer.DrawText(g, m.Ad, _yazi, new Rectangle(adX, y, Math.Max(0, Math.Min(P(200), adSutun - (adX - x0) - P(4))), SatirH), Palet.Yazi, Sol);
            hdc = RakamYazisi.Ac(g); // satırın sayıları tek HDC oturumunda
            try
            {
                _rYari.Ciz(hdc, m.Hasar, new Rectangle(hasarX, y, _hasarW, SatirH), Palet.Dps, sag: true);
                _rYazi.Ciz(hdc, m.Pay, new Rectangle(payX, y, _payW, SatirH), Palet.Soluk, sag: true);
                _rYazi.Ciz(hdc, m.Vurus, new Rectangle(vurusX, y, _vurusW, SatirH), Palet.Soluk, sag: true);
                _rYazi.Ciz(hdc, m.Kritik, new Rectangle(kritikX, y, _kritikW, SatirH), Palet.Soluk, sag: true);
                _rYazi.Ciz(hdc, m.Maks, new Rectangle(maksX, y, _maksW, SatirH), Palet.Soluk, sag: true);
            }
            finally
            {
                RakamYazisi.Kapat(g, hdc);
            }
            y += SatirH;
        }

        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, hh - 1);
    }

    private void GrafikCiz(Graphics g, Rectangle r)
    {
        g.FillRectangle(_fircaGrafikArka, r);
        if (_grafikN < 2)
        {
            TextRenderer.DrawText(g, "Grafik için veri bekleniyor…", _yaziKucuk, r, Palet.CokSoluk,
                TextFormatFlags.HorizontalCenter | Temel);
            return;
        }

        float ust = r.Top + P(4);
        float alt = r.Bottom - 1;
        float yukseklik = alt - ust;

        // Referans çizgileri: maksimumun %50'si ve %100'ü
        g.DrawLine(_kalemReferans, r.Left, ust, r.Right - 1, ust);
        float orta = alt - 0.5f * yukseklik;
        g.DrawLine(_kalemReferans, r.Left, orta, r.Right - 1, orta);

        // Sütun başına en fazla bir nokta: kova sayısı piksel genişliğini aşarsa ortalamayla küçült.
        int m = Math.Min(_grafikN, Math.Max(2, r.Width));
        if (_cizgi.Length != m) { _cizgi = new PointF[m]; _alan = new PointF[m + 2]; }
        for (int j = 0; j < m; j++)
        {
            int bas = (int)((long)j * _grafikN / m);
            int son = Math.Max(bas + 1, (int)((long)(j + 1) * _grafikN / m));
            float t = 0;
            for (int i = bas; i < son; i++) t += _grafikDps[i];
            float v = t / (son - bas);
            float x = r.Left + (float)j * (r.Width - 1) / (m - 1);
            float y = alt - v / _grafikMaks * yukseklik;
            _cizgi[j] = new PointF(x, y);
            _alan[j + 1] = _cizgi[j];
        }
        _alan[0] = new PointF(_cizgi[0].X, alt);
        _alan[m + 1] = new PointF(_cizgi[m - 1].X, alt);

        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPolygon(_fircaGrafik, _alan);
        g.DrawLines(_kalemGrafik, _cizgi);
        g.SmoothingMode = eski;

        // Ortalama DPS: kesikli çizgi + "ort." etiketi. Etiket solda (en eski saniyeler): sağ uç en yeni saniyelerdir, örtülmesin.
        // Etiket grafik içinde kalacak şekilde sınırlanır.
        float oy = alt - Math.Clamp(_grafikOrt / _grafikMaks, 0f, 1f) * yukseklik;
        g.DrawLine(_kalemOrtalama, r.Left, oy, r.Right - 1, oy);
        int etiketH = P(14);
        int etiketW = Olc(_grafikOrtMetin, _yaziKucuk);
        int ey = Math.Clamp((int)oy - etiketH - P(1), r.Top, r.Bottom - etiketH);
        var etiket = new Rectangle(r.Left + P(4), ey, etiketW, etiketH);
        g.FillRectangle(_fircaGrafikArka, Rectangle.Inflate(etiket, P(3), 0)); // çizginin üstünde okunsun
        TextRenderer.DrawText(g, _grafikOrtMetin, _yaziKucuk, etiket, Palet.Soluk, Sol);
    }

    // ---------------- Fare ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _kapatKutusu.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _kapatKutusu.Contains(e.Location)) KapatIstendi?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yazi.Dispose(); _yaziYari.Dispose(); _yaziAd.Dispose(); _yaziBuyuk.Dispose(); _yaziKucuk.Dispose();
            _rYazi.Dispose(); _rYari.Dispose(); _rBuyuk.Dispose();
            _kalemCizgi.Dispose(); _kalemKenar.Dispose(); _kalemKapat.Dispose(); _kalemReferans.Dispose(); _kalemOrtalama.Dispose();
            _kalemGrafik.Dispose(); _fircaGrafik.Dispose(); _fircaGrafikArka.Dispose(); _fircaBeceri.Dispose();
            _beceriIkonlari.Dispose();
        }
        base.Dispose(disposing);
    }
}
