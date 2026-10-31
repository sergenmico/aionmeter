namespace AionMeter;

/// <summary>
/// Satıra tıklanınca overlay'in yanında açılan oyuncu detayı. Kenarlıksız, en üstte, odak çalmaz
/// (WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE). Kendisi çizer; sadece görünürken 2 Hz yenilenir (overlay'in zamanlayıcısıyla).
/// Kapatılınca overlay tarafından Dispose edilir.
/// </summary>
internal sealed class DetayForm : Form
{
    private const int MaxBeceri = 12;

    private static readonly Color RenkArka = Color.FromArgb(18, 20, 26);
    private static readonly Color RenkYazi = Color.FromArgb(222, 225, 230);
    private static readonly Color RenkSoluk = Color.FromArgb(138, 145, 156);
    private static readonly Color RenkCokSoluk = Color.FromArgb(110, 116, 128);
    private static readonly Color RenkDps = Color.White;

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;

    private static readonly string[] SutunBasliklari = { "Beceri", "Hasar", "Pay %", "Vuruş", "Kritik %", "En yüksek" };
    private static readonly string[] IstatEtiketleri =
        { "Vuruş", "Kritik %", "Double %", "Perfect %", "Arkadan %", "Parry %", "En yüksek", "Ortalama" };

    private readonly IMeterEngine _motor;
    private readonly SinifIkonlari _ikonlar;
    private readonly BeceriIkonlari _beceriIkonlari; // panel ömrü kadar yaşar; kapanınca Dispose
    private readonly float _olcek;
    private readonly byte _alfa;
    private readonly Font _yazi, _yaziKalin, _yaziBuyuk, _yaziKucuk;
    private readonly Pen _kalemCizgi = new(Color.FromArgb(48, 52, 62));
    private readonly Pen _kalemKenar = new(Color.FromArgb(44, 48, 58));
    private readonly Pen _kalemKapat = new(Color.FromArgb(160, 166, 176), 1.5f);
    private readonly Pen _kalemGrafik = new(Color.FromArgb(96, 166, 245), 1.5f);
    private readonly SolidBrush _fircaGrafik = new(Color.FromArgb(70, 86, 156, 240));
    private readonly SolidBrush _fircaGrafikArka = new(Color.FromArgb(26, 29, 37));

    // DPS grafiği: kovalar → saniye başına DPS, 5 sn kayan ortalama. Diziler yeniden kullanılır.
    private float[] _grafikDps = new float[64];
    private int _grafikN;
    private float _grafikMaks;
    private string _grafikMaksMetin = "", _grafikSon = "";
    private PointF[] _cizgi = Array.Empty<PointF>(), _alan = Array.Empty<PointF>();

    // Model
    private sealed class BeceriModel { public string Ad = "", Hasar = "", Pay = "", Vurus = "", Kritik = "", Maks = ""; public string? Ikon; }
    private readonly BeceriModel[] _beceriler = new BeceriModel[MaxBeceri];
    private readonly string[] _istatDeger = new string[8];
    private int _beceriSayisi;
    private bool _veriVar;
    private string _ad = "", _cp = "", _dps = "", _toplam = "", _aktif = "";
    private string? _sinif;

    // Değişiklik kontrolü
    private long _sonToplam = -1, _sonVurus = -1, _sonAktifSn = -1, _sonKovaDeger = -1;
    private int _sonBeceri = -1, _sonKovaSayisi = -1;

    // Ölçüler (fiziksel px; açık kaldığı sürece sadece büyür)
    private readonly int _hasarW, _payW, _vurusW, _kritikW, _maksW;
    private int _adSutunW, _icerikW;
    private Rectangle _kapatKutusu;
    private Rectangle _sonOverlay;

    public long OyuncuId { get; private set; }
    public event Action? KapatIstendi;

    public DetayForm(IMeterEngine motor, SinifIkonlari ikonlar, float olcek, byte alfa)
    {
        _motor = motor;
        _ikonlar = ikonlar;
        _olcek = olcek;
        _alfa = alfa;
        for (int i = 0; i < MaxBeceri; i++) _beceriler[i] = new BeceriModel();
        for (int i = 0; i < _istatDeger.Length; i++) _istatDeger[i] = "";

        Text = "AionMeter Detay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = RenkArka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _yazi = new Font("Segoe UI", 12f * olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _yaziKalin = new Font("Segoe UI", 13f * olcek, FontStyle.Bold, GraphicsUnit.Pixel);
        _yaziBuyuk = new Font("Segoe UI", 14f * olcek, FontStyle.Bold, GraphicsUnit.Pixel);
        _yaziKucuk = new Font("Segoe UI", 11f * olcek, FontStyle.Regular, GraphicsUnit.Pixel);

        // Sayı sütunları şablonla bir kez ölçülür; her yenilemede tekrar ölçülmez.
        _hasarW = Math.Max(Olc("99.999.999", _yazi), Olc("Hasar", _yaziKucuk));
        _payW = Math.Max(Olc("%100,0", _yazi), Olc("Pay %", _yaziKucuk));
        _vurusW = Math.Max(Olc("99.999", _yazi), Olc("Vuruş", _yaziKucuk));
        _kritikW = Math.Max(Olc("%100,0", _yazi), Olc("Kritik %", _yaziKucuk));
        _maksW = Math.Max(Olc("9.999.999", _yazi), Olc("En yüksek", _yaziKucuk));
        _adSutunW = P(110);
        _beceriIkonlari = new BeceriIkonlari(P(16));
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);
    private int Pad => P(8);
    private int Bosluk => P(10);
    private int SatirH => P(17);

    private static int Olc(string metin, Font font) =>
        metin.Length == 0 ? 0 : TextRenderer.MeasureText(metin, font, Size.Empty, Temel).Width;

    // ---------------- Pencere stilleri / odak ----------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
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

    /// <summary>Oyuncuyu gösterir (ya da başka oyuncuya geçer) ve overlay'in yanına yerleşir.</summary>
    public void Goster(long oyuncuId, Rectangle overlay)
    {
        if (oyuncuId != OyuncuId)
        {
            OyuncuId = oyuncuId;
            _sonToplam = _sonVurus = _sonAktifSn = _sonKovaDeger = -1;
            _sonBeceri = _sonKovaSayisi = -1;
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
        try { d = _motor.Detail(OyuncuId); }
        catch (Exception ex) { HataLog.Yaz("Detay", ex); }

        long toplam = d?.Total ?? -2, vurus = d?.Hits?.Hits ?? -2, aktif = (long)(d?.Active.TotalSeconds ?? -2);
        int beceri = d?.Skills?.Count ?? -2;
        var kovalar = d?.DamagePerBucket;
        int kovaSayisi = kovalar?.Count ?? -2;
        long kovaDeger = kovaSayisi > 0 ? kovalar![kovaSayisi - 1] : -2;
        if (toplam == _sonToplam && vurus == _sonVurus && aktif == _sonAktifSn && beceri == _sonBeceri &&
            kovaSayisi == _sonKovaSayisi && kovaDeger == _sonKovaDeger) return;
        _sonToplam = toplam; _sonVurus = vurus; _sonAktifSn = aktif; _sonBeceri = beceri;
        _sonKovaSayisi = kovaSayisi; _sonKovaDeger = kovaDeger;

        ModelKur(d);
        Yerlestir(_sonOverlay);
        Invalidate();
    }

    /// <summary>Overlay'in sağına sığıyorsa sağa, değilse soluna yerleşir; ekran dışına taşmaz.</summary>
    public void Yerlestir(Rectangle overlay)
    {
        _sonOverlay = overlay;
        var boyut = new Size(Math.Max(_icerikW, P(300)), HesaplaYukseklik());
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

    private void ModelKur(PlayerDetail? d)
    {
        _veriVar = d is not null;
        if (d is null)
        {
            _beceriSayisi = 0;
            _icerikW = Math.Max(_icerikW, P(300));
            return;
        }

        _ad = string.IsNullOrWhiteSpace(d.Name) ? "?" : d.Name;
        _sinif = d.ClassName;
        _cp = d.CombatPower is int cp ? Bicim.Tam(cp) + " CP" : "— CP";
        _dps = Bicim.Dps(d.Dps);
        _toplam = "Toplam " + Bicim.Tam(Math.Max(0, d.Total));
        _aktif = "Aktif " + Bicim.Sure(d.Active);

        var h = d.Hits ?? new HitStats(0, 0, 0, 0, 0, 0, 0);
        _istatDeger[0] = Bicim.Tam(h.Hits);
        _istatDeger[1] = Bicim.Oran(h.Crits, h.Hits);
        _istatDeger[2] = Bicim.Oran(h.Doubles, h.Hits);
        _istatDeger[3] = Bicim.Oran(h.Perfects, h.Hits);
        _istatDeger[4] = Bicim.Oran(h.Backs, h.Hits);
        _istatDeger[5] = Bicim.Oran(h.Parries, h.Hits);
        _istatDeger[6] = Bicim.Tam(h.MaxHit);
        _istatDeger[7] = h.Hits > 0 ? Bicim.Tam((long)Math.Round((double)Math.Max(0, d.Total) / h.Hits)) : "—";

        var liste = d.Skills ?? Array.Empty<SkillDamage>();
        int n = Math.Min(liste.Count, MaxBeceri);
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
            _adSutunW = Math.Max(_adSutunW, _beceriIkonlari.Boyut + P(4) + Math.Min(Olc(m.Ad, _yazi), P(180)));
        }
        _beceriSayisi = n;
        GrafikKur(d);

        int pad = Pad, b = Bosluk;
        int tablo = 2 * pad + _adSutunW + b + _hasarW + b + _payW + b + _vurusW + b + _kritikW + b + _maksW;
        int baslik = 2 * pad + _ikonlar.Boyut + P(6) + Math.Min(Olc(_ad, _yaziKalin), P(200)) + P(8) + Olc(_cp, _yaziKucuk) + P(24);
        int ikinci = 2 * pad + Olc(_dps, _yaziBuyuk) + P(12) + Olc(_toplam, _yaziKucuk) + P(12) + Olc(_aktif, _yaziKucuk);
        _icerikW = Math.Max(_icerikW, Math.Max(tablo, Math.Max(baslik, ikinci)));
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
        double toplam = 0, maks = 1;
        for (int i = 0; i < n; i++)
        {
            toplam += Math.Max(0, k[i]);
            if (i >= pencere) toplam -= Math.Max(0, k[i - pencere]);
            int adet = Math.Min(i + 1, pencere);
            double v = toplam / adet / bs;
            _grafikDps[i] = (float)v;
            if (v > maks) maks = v;
        }
        _grafikN = n;
        _grafikMaks = (float)maks;
        _grafikMaksMetin = "maks " + Bicim.Dps(maks);
        _grafikSon = Bicim.Sure(TimeSpan.FromSeconds((double)n * bs));
    }

    private int GrafikH => P(70);

    private int HesaplaYukseklik()
    {
        int h = Pad + P(22) + P(20);           // ad satırı + DPS satırı
        if (!_veriVar) return h + SatirH + Pad;
        h += P(6) + GrafikH + P(14);           // DPS grafiği + zaman ekseni
        h += P(9) + 4 * SatirH;                // çizgi + 4 satır istatistik
        h += P(9) + SatirH + _beceriSayisi * SatirH; // çizgi + tablo başlığı + beceriler
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
        _kapatKutusu = new Rectangle(w - pad - k, pad + (P(22) - k) / 2, k, k);
        int ki = P(4);
        g.DrawLine(_kalemKapat, _kapatKutusu.Left + ki, _kapatKutusu.Top + ki, _kapatKutusu.Right - ki, _kapatKutusu.Bottom - ki);
        g.DrawLine(_kalemKapat, _kapatKutusu.Right - ki, _kapatKutusu.Top + ki, _kapatKutusu.Left + ki, _kapatKutusu.Bottom - ki);

        if (!_veriVar)
        {
            TextRenderer.DrawText(g, "Bu oyuncu için veri yok", _yazi, new Rectangle(x0, y, x1 - x0 - k, P(22)), RenkSoluk, Sol);
            g.DrawRectangle(_kalemKenar, 0, 0, w - 1, hh - 1);
            return;
        }

        // Satır 1: ikon · ad · CP
        int ikon = _ikonlar.Boyut;
        g.DrawImage(_ikonlar.Al(_sinif), x0, y + (P(22) - ikon) / 2, ikon, ikon);
        int x = x0 + ikon + P(6);
        int adW = Math.Min(Olc(_ad, _yaziKalin), _kapatKutusu.Left - x - P(80));
        TextRenderer.DrawText(g, _ad, _yaziKalin, new Rectangle(x, y, Math.Max(0, adW), P(22)), RenkYazi, Sol);
        x += Math.Max(0, adW) + P(8);
        TextRenderer.DrawText(g, _cp, _yaziKucuk, new Rectangle(x, y, Math.Max(0, _kapatKutusu.Left - P(6) - x), P(22)), RenkCokSoluk, Sol);
        y += P(22);

        // Satır 2: DPS (birincil) · toplam · aktif süre
        int dpsW = Olc(_dps, _yaziBuyuk);
        TextRenderer.DrawText(g, _dps, _yaziBuyuk, new Rectangle(x0, y, dpsW, P(20)), RenkDps, Sol);
        x = x0 + dpsW + P(12);
        int tW = Olc(_toplam, _yaziKucuk);
        TextRenderer.DrawText(g, _toplam, _yaziKucuk, new Rectangle(x, y, tW, P(20)), RenkSoluk, Sol);
        x += tW + P(12);
        TextRenderer.DrawText(g, _aktif, _yaziKucuk, new Rectangle(x, y, Math.Max(0, x1 - x), P(20)), RenkSoluk, Sol);
        y += P(20);

        // DPS grafiği
        y += P(6);
        GrafikCiz(g, new Rectangle(x0, y, x1 - x0, GrafikH));
        y += GrafikH;
        TextRenderer.DrawText(g, "00:00", _yaziKucuk, new Rectangle(x0, y, (x1 - x0) / 2, P(14)), RenkCokSoluk, Sol);
        if (_grafikN > 0)
            TextRenderer.DrawText(g, _grafikSon, _yaziKucuk, new Rectangle(x0 + (x1 - x0) / 2, y, (x1 - x0) / 2, P(14)), RenkCokSoluk, Sag);
        y += P(14);

        // İstatistikler: iki sütun × dört satır
        y += P(4);
        g.DrawLine(_kalemCizgi, x0, y, x1, y);
        y += P(5);
        int yarim = (x1 - x0 - Bosluk * 2) / 2;
        for (int i = 0; i < 8; i++)
        {
            int sutun = i / 4, satir = i % 4;
            int cx = x0 + sutun * (yarim + Bosluk * 2);
            int cy = y + satir * SatirH;
            TextRenderer.DrawText(g, IstatEtiketleri[i], _yaziKucuk, new Rectangle(cx, cy, yarim, SatirH), RenkSoluk, Sol);
            TextRenderer.DrawText(g, _istatDeger[i], _yazi, new Rectangle(cx, cy, yarim, SatirH), RenkYazi, Sag);
        }
        y += 4 * SatirH;

        // Beceri tablosu
        y += P(4);
        g.DrawLine(_kalemCizgi, x0, y, x1, y);
        y += P(5);
        int b = Bosluk;
        int maksX = x1 - _maksW;
        int kritikX = maksX - b - _kritikW;
        int vurusX = kritikX - b - _vurusW;
        int payX = vurusX - b - _payW;
        int hasarX = payX - b - _hasarW;
        int adSutun = hasarX - b - x0;

        TextRenderer.DrawText(g, SutunBasliklari[0], _yaziKucuk, new Rectangle(x0, y, adSutun, SatirH), RenkSoluk, Sol);
        TextRenderer.DrawText(g, SutunBasliklari[1], _yaziKucuk, new Rectangle(hasarX, y, _hasarW, SatirH), RenkSoluk, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[2], _yaziKucuk, new Rectangle(payX, y, _payW, SatirH), RenkSoluk, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[3], _yaziKucuk, new Rectangle(vurusX, y, _vurusW, SatirH), RenkSoluk, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[4], _yaziKucuk, new Rectangle(kritikX, y, _kritikW, SatirH), RenkSoluk, Sag);
        TextRenderer.DrawText(g, SutunBasliklari[5], _yaziKucuk, new Rectangle(maksX, y, _maksW, SatirH), RenkSoluk, Sag);
        y += SatirH;

        for (int i = 0; i < _beceriSayisi; i++)
        {
            var m = _beceriler[i];
            int bi = _beceriIkonlari.Boyut;
            g.DrawImage(_beceriIkonlari.Al(m.Ikon), x0, y + (SatirH - bi) / 2, bi, bi);
            TextRenderer.DrawText(g, m.Ad, _yazi, new Rectangle(x0 + bi + P(4), y, Math.Max(0, adSutun - bi - P(4)), SatirH), RenkYazi, Sol);
            TextRenderer.DrawText(g, m.Hasar, _yazi, new Rectangle(hasarX, y, _hasarW, SatirH), RenkDps, Sag);
            TextRenderer.DrawText(g, m.Pay, _yazi, new Rectangle(payX, y, _payW, SatirH), RenkSoluk, Sag);
            TextRenderer.DrawText(g, m.Vurus, _yazi, new Rectangle(vurusX, y, _vurusW, SatirH), RenkSoluk, Sag);
            TextRenderer.DrawText(g, m.Kritik, _yazi, new Rectangle(kritikX, y, _kritikW, SatirH), RenkSoluk, Sag);
            TextRenderer.DrawText(g, m.Maks, _yazi, new Rectangle(maksX, y, _maksW, SatirH), RenkSoluk, Sag);
            y += SatirH;
        }

        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, hh - 1);
    }

    private void GrafikCiz(Graphics g, Rectangle r)
    {
        g.FillRectangle(_fircaGrafikArka, r);
        if (_grafikN < 2)
        {
            TextRenderer.DrawText(g, "Grafik için veri bekleniyor…", _yaziKucuk, r, RenkCokSoluk,
                TextFormatFlags.HorizontalCenter | Temel);
            return;
        }

        // Sütun başına en fazla bir nokta: kova sayısı piksel genişliğini aşarsa ortalamayla küçült.
        int m = Math.Min(_grafikN, Math.Max(2, r.Width));
        if (_cizgi.Length != m) { _cizgi = new PointF[m]; _alan = new PointF[m + 2]; }
        float ust = r.Top + P(14);                 // maks etiketi için boşluk
        float yukseklik = r.Bottom - 1 - ust;
        for (int j = 0; j < m; j++)
        {
            int bas = (int)((long)j * _grafikN / m);
            int son = Math.Max(bas + 1, (int)((long)(j + 1) * _grafikN / m));
            float t = 0;
            for (int i = bas; i < son; i++) t += _grafikDps[i];
            float v = t / (son - bas);
            float x = r.Left + (float)j * (r.Width - 1) / (m - 1);
            float y = r.Bottom - 1 - v / _grafikMaks * yukseklik;
            _cizgi[j] = new PointF(x, y);
            _alan[j + 1] = _cizgi[j];
        }
        _alan[0] = new PointF(_cizgi[0].X, r.Bottom - 1);
        _alan[m + 1] = new PointF(_cizgi[m - 1].X, r.Bottom - 1);

        var eski = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPolygon(_fircaGrafik, _alan);
        g.DrawLines(_kalemGrafik, _cizgi);
        g.SmoothingMode = eski;

        TextRenderer.DrawText(g, _grafikMaksMetin, _yaziKucuk, new Rectangle(r.Left + P(4), r.Top, r.Width - P(8), P(14)),
            RenkSoluk, Sol);
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
            _yazi.Dispose(); _yaziKalin.Dispose(); _yaziBuyuk.Dispose(); _yaziKucuk.Dispose();
            _kalemCizgi.Dispose(); _kalemKenar.Dispose(); _kalemKapat.Dispose();
            _kalemGrafik.Dispose(); _fircaGrafik.Dispose(); _fircaGrafikArka.Dispose();
            _beceriIkonlari.Dispose();
        }
        base.Dispose(disposing);
    }
}
