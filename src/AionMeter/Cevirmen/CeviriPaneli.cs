using System.Drawing.Drawing2D;

namespace AionMeter.Cevirmen;

/// <summary>
/// Çeviri sonucu paneli: sayaç paletinde, sade. En üstte, odak ALMAZ (WS_EX_NOACTIVATE / MA_NOACTIVATE).
/// Normal mod (Ctrl+Alt+T): başlıkta solda "Türkçe", sağda ince ✕; gövde metin sütunu en fazla 400 px; uzunsa tekerlekle kayar.
/// Küçük mod (Alt ile açıklama): başlık ve ✕ yok, metin sütunu en fazla 340 px; denetleyici gizler.
/// Korunan terimler (Daeva, Elyos, yer adları…) metnin içinde hafif sıcak tonla gösterilir; ayrı etiket satırı yok.
/// Yüklenirken üst kenarda 2 px kayan ince bir çizgi; açılışta ~120 ms opaklık geçişi, kapanış anında.
/// Kaynak alanın yanına (8 px), ekrandan taşmadan ve onu örtmeden, kaynağın üstüne hizalı yerleşir.
/// </summary>
internal sealed class CeviriPaneli : Form
{
    private const byte HedefAlfa = 242;          // ~%95
    private const int AcilisMs = 120;
    private const int YuklemeDonguMs = 1200;

    private const TextFormatFlags Tek = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                                        TextFormatFlags.PreserveGraphicsClipping;

    private readonly bool _kucuk;
    private readonly float _olcek;
    private readonly Font _govde, _kucukYazi;
    private readonly int _satirH, _boslukW, _yaziH;
    private readonly Pen _kalemKenar = new(Cizim.Cizgi);
    private readonly SolidBrush _fircaKaydirma = new(Color.FromArgb(178, 90, 96, 110));    // %70
    private readonly SolidBrush _fircaYukleme = new(Color.FromArgb(153, 90, 140, 210));    // %60
    private readonly System.Windows.Forms.Timer _animasyon = new() { Interval = 15 };

    // İçerik
    private readonly List<string> _paragraflar = new();
    private readonly List<string> _terimler = new();
    private string? _durum;
    private string _altBilgi = "";
    private bool _yukleniyor;

    // Yerleşim (içerik değişince hesaplanır; OnPaint ölçmez)
    private readonly record struct Kelime(string Metin, int X, int Y, bool Terim);
    private readonly List<Kelime> _kelimeler = new();
    private Rectangle _govdeKutu, _kapatKutusu;
    private int _icerikH, _kaydirma;
    private bool _kapatUstunde;
    private Rectangle _kaynak;

    // Animasyon
    private long _acilisBas = -1, _yuklemeBas;

    public event Action? KapatIstendi;

    public CeviriPaneli(bool kucuk, float olcek)
    {
        _kucuk = kucuk;
        _olcek = olcek;
        Text = kucuk ? "AionMeter Açıklama" : "AionMeter Çeviri";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Cizim.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _govde = Cizim.GovdeYaziTipi(13.5f * olcek);
        _kucukYazi = new Font("Segoe UI", 11f * olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _yaziH = TextRenderer.MeasureText("Ağ", _govde, Size.Empty, Tek).Height;
        _satirH = (int)MathF.Round(13.5f * olcek * 1.45f);
        _boslukW = Math.Max(1, TextRenderer.MeasureText("a a", _govde, Size.Empty, Tek).Width -
                               TextRenderer.MeasureText("aa", _govde, Size.Empty, Tek).Width);
        _animasyon.Tick += (_, _) => AnimasyonTik();
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);

    // ---------------- Odak almama ----------------

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
        Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA); // açılış geçişi 0'dan başlar
        try
        {
            // Windows 11: DWM ile yuvarlak köşe; Windows 10'da sessizce kare kalır (GDI bölgesi yok, tırtıklı olmasın).
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10 */ }
    }

    // ---------------- İçerik ----------------

    public Rectangle Kaynak => _kaynak;
    public bool Kucuk => _kucuk;
    public bool Yukleniyor => _yukleniyor;

    /// <summary>Tek satırlık durum. "Çevriliyor…" ise üst kenarda yükleme çizgisi döner.</summary>
    public void Durum(string metin, Rectangle kaynak, bool yukleniyor = false)
    {
        _durum = metin;
        _paragraflar.Clear();
        _terimler.Clear();
        _altBilgi = "";
        _kaynak = kaynak;
        _yukleniyor = yukleniyor;
        Duzenle();
    }

    /// <summary>Paragraflar (çevrildikçe artar). yukleniyor = daha paragraf var.</summary>
    public void Icerik(IReadOnlyList<string> paragraflar, IReadOnlyList<string> terimler, string altBilgi, bool yukleniyor)
    {
        _durum = null;
        _paragraflar.Clear();
        _paragraflar.AddRange(paragraflar);
        _terimler.Clear();
        _terimler.AddRange(terimler);
        _altBilgi = altBilgi;
        _yukleniyor = yukleniyor;
        Duzenle();
    }

    public new void Hide()
    {
        _animasyon.Stop();
        _acilisBas = -1;
        base.Hide(); // kapanış anında
    }

    private int Olc(string m, Font f) => m.Length == 0 ? 0 : TextRenderer.MeasureText(m, f, Size.Empty, Tek).Width;

    private int Pad => P(12);
    private int UstPad => P(10);
    private int BaslikH => _kucuk ? 0 : P(26);
    private int AltH => _altBilgi.Length > 0 ? P(6) + P(15) : 0;

    /// <summary>Kelime kelime dizer (terimler hafif sıcak tonda); boyutu ve konumu hesaplar; gösterir.</summary>
    private void Duzenle()
    {
        int sutun = _kucuk ? P(340) : P(400);
        _kelimeler.Clear();
        int enGenis = 0, y = 0;

        if (_durum is not null)
        {
            _kelimeler.Add(new Kelime(_durum, 0, 0, false));
            enGenis = Olc(_durum, _govde);
            y = _satirH;
        }
        else
        {
            for (int pi = 0; pi < _paragraflar.Count; pi++)
            {
                if (pi > 0) y += P(8);
                string p = _paragraflar[pi];
                bool[] terimMi = TerimIsaretle(p);
                int x = 0, i = 0;
                while (i < p.Length)
                {
                    while (i < p.Length && char.IsWhiteSpace(p[i])) i++;
                    if (i >= p.Length) break;
                    int bas = i;
                    while (i < p.Length && !char.IsWhiteSpace(p[i])) i++;
                    string kelime = p[bas..i];
                    int w = Olc(kelime, _govde);
                    if (x > 0 && x + w > sutun)
                    {
                        x = 0;
                        y += _satirH;
                    }
                    _kelimeler.Add(new Kelime(kelime, x, y, terimMi[bas]));
                    enGenis = Math.Max(enGenis, Math.Min(sutun, x + w));
                    x += w + _boslukW;
                }
                y += _satirH;
            }
        }
        _icerikH = Math.Max(y, _satirH);

        int metinW = Math.Max(enGenis, Olc(_altBilgi, _kucukYazi));
        if (!_kucuk) metinW = Math.Max(metinW, Olc("Türkçe", _kucukYazi) + P(40));
        int w2 = Math.Max(P(160), metinW + 2 * Pad);

        int enFazlaGovde = _kucuk ? P(260) : P(440);
        int gorunur = Math.Min(_icerikH, enFazlaGovde);
        int govdeUst = _kucuk ? UstPad : BaslikH;
        _govdeKutu = new Rectangle(Pad, govdeUst, w2 - 2 * Pad, gorunur);
        _kaydirma = Math.Clamp(_kaydirma, 0, Math.Max(0, _icerikH - gorunur));
        int k = P(14);
        _kapatKutusu = _kucuk ? Rectangle.Empty : new Rectangle(w2 - P(8) - k, (BaslikH - k) / 2, k, k);

        var boyut = new Size(w2, govdeUst + gorunur + AltH + UstPad);
        Konumla(boyut);

        if (_yukleniyor && !_animasyon.Enabled) _yuklemeBas = Environment.TickCount64;
        if (!Visible)
        {
            _acilisBas = Environment.TickCount64;
            if (IsHandleCreated) Native.SetLayeredWindowAttributes(Handle, 0, 0, Native.LWA_ALPHA);
            Show(); // ShowWithoutActivation → SW_SHOWNOACTIVATE
        }
        AnimasyonGuncelle();
        Invalidate();
    }

    /// <summary>Paragrafta korunan terimlerin geçtiği karakterleri işaretler (büyük/küçük harf duyarsız, kelime sınırında).</summary>
    private bool[] TerimIsaretle(string p)
    {
        var isaret = new bool[p.Length];
        foreach (string t in _terimler)
        {
            if (t.Length == 0) continue;
            int i = 0;
            while ((i = p.IndexOf(t, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool solSinir = i == 0 || !char.IsLetterOrDigit(p[i - 1]);
                int son = i + t.Length;
                // Türkçe ekler terime yapışabilir (Daeva'nın) — sağ sınır gevşek: harf gelse de kabul.
                if (solSinir)
                    for (int j = i; j < son; j++) isaret[j] = true;
                i = son;
            }
        }
        return isaret;
    }

    /// <summary>Kaynağın sağına, soluna, altına ya da üstüne (8 px) — ekrana sığan, kaynağı örtmeyen ilk yer; yanda iken üstler hizalı.</summary>
    private void Konumla(Size boyut)
    {
        var alan = Screen.FromRectangle(_kaynak.IsEmpty ? new Rectangle(Cursor.Position, new Size(1, 1)) : _kaynak).WorkingArea;
        int ara = P(8);
        int yDik = Math.Clamp(_kaynak.Top, alan.Top, Math.Max(alan.Top, alan.Bottom - boyut.Height));
        int xYatay = Math.Clamp(_kaynak.Left, alan.Left, Math.Max(alan.Left, alan.Right - boyut.Width));
        Point[] adaylar =
        {
            new(_kaynak.Right + ara, yDik),
            new(_kaynak.Left - ara - boyut.Width, yDik),
            new(xYatay, _kaynak.Bottom + ara),
            new(xYatay, _kaynak.Top - ara - boyut.Height),
        };
        Point secilen = new(Math.Clamp(adaylar[0].X, alan.Left, Math.Max(alan.Left, alan.Right - boyut.Width)), yDik);
        foreach (var a in adaylar)
        {
            var r = new Rectangle(a, boyut);
            if (alan.Contains(r) && !r.IntersectsWith(_kaynak))
            {
                secilen = a;
                break;
            }
        }
        var yeni = new Rectangle(secilen, boyut);
        if (Bounds != yeni) Bounds = yeni;
    }

    // ---------------- Animasyon (sadece açılışta ve yüklenirken çalışır) ----------------

    private void AnimasyonGuncelle()
    {
        bool gerekli = _yukleniyor || _acilisBas >= 0;
        if (gerekli && !_animasyon.Enabled) _animasyon.Start();
        else if (!gerekli && _animasyon.Enabled) _animasyon.Stop();
    }

    private void AnimasyonTik()
    {
        if (!IsHandleCreated) return;
        if (_acilisBas >= 0)
        {
            long gecen = Environment.TickCount64 - _acilisBas;
            float oran = Math.Clamp(gecen / (float)AcilisMs, 0f, 1f);
            Native.SetLayeredWindowAttributes(Handle, 0, (byte)(HedefAlfa * oran), Native.LWA_ALPHA);
            if (oran >= 1f) _acilisBas = -1;
        }
        if (_yukleniyor) Invalidate(new Rectangle(0, 0, ClientSize.Width, P(2)));
        AnimasyonGuncelle();
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Çeviri paneli çizimi", ex); }
    }

    private void Ciz(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height;

        if (!_kucuk)
        {
            TextRenderer.DrawText(g, "Türkçe", _kucukYazi, new Point(Pad, (BaslikH - _kucukYazi.Height) / 2), Cizim.Soluk, Tek);
            // İnce ✕: 8 px glif, 1,25 px çizgi; sadece üstüne gelince parlar.
            using var kalem = new Pen(_kapatUstunde ? Cizim.Yazi : Cizim.CokSoluk, 1.25f * _olcek);
            float cx = _kapatKutusu.X + _kapatKutusu.Width / 2f, cy = _kapatKutusu.Y + _kapatKutusu.Height / 2f, r = 4f * _olcek;
            var eski = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(kalem, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(kalem, cx + r, cy - r, cx - r, cy + r);
            g.SmoothingMode = eski;
        }

        g.SetClip(_govdeKutu);
        int dy = _govdeKutu.Y - _kaydirma + (_satirH - _yaziH) / 2;
        Color durumRenk = _durum is not null ? Cizim.Soluk : Cizim.Yazi;
        foreach (var k in _kelimeler)
        {
            int y = k.Y + dy;
            if (y + _satirH < _govdeKutu.Top || y > _govdeKutu.Bottom) continue;
            TextRenderer.DrawText(g, k.Metin, _govde, new Point(_govdeKutu.X + k.X, y), k.Terim ? Cizim.Terim : durumRenk, Tek);
        }
        g.ResetClip();

        if (_icerikH > _govdeKutu.Height)
        {
            int izH = Math.Max(P(18), _govdeKutu.Height * _govdeKutu.Height / _icerikH);
            int izY = _govdeKutu.Y + (_govdeKutu.Height - izH) * _kaydirma / Math.Max(1, _icerikH - _govdeKutu.Height);
            Cizim.YuvarlakDoldur(g, _fircaKaydirma, new RectangleF(w - P(6), izY, P(3), izH), P(3) / 2f);
        }

        if (_altBilgi.Length > 0)
            TextRenderer.DrawText(g, _altBilgi, _kucukYazi, new Point(Pad, h - UstPad - P(15)), Cizim.CokSoluk, Tek);

        if (_yukleniyor)
        {
            // 2 px belirsiz ilerleme: kısa bir parça üst kenarda soldan sağa kayar (1,2 sn döngü).
            float t = ((Environment.TickCount64 - _yuklemeBas) % YuklemeDonguMs) / (float)YuklemeDonguMs;
            int parca = Math.Max(P(40), w / 4);
            int x = (int)(-parca + t * (w + parca));
            g.FillRectangle(_fircaYukleme, x, 0, parca, P(2));
        }

        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare ----------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int enFazla = Math.Max(0, _icerikH - _govdeKutu.Height);
        if (enFazla == 0) return;
        _kaydirma = Math.Clamp(_kaydirma - Math.Sign(e.Delta) * _satirH * 3, 0, enFazla);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool ust = !_kapatKutusu.IsEmpty && Rectangle.Inflate(_kapatKutusu, P(4), P(4)).Contains(e.Location);
        if (ust != _kapatUstunde)
        {
            _kapatUstunde = ust;
            Cursor = ust ? Cursors.Hand : Cursors.Default;
            Invalidate(Rectangle.Inflate(_kapatKutusu, P(2), P(2)));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_kapatUstunde)
        {
            _kapatUstunde = false;
            Invalidate(Rectangle.Inflate(_kapatKutusu, P(2), P(2)));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !_kapatKutusu.IsEmpty && Rectangle.Inflate(_kapatKutusu, P(4), P(4)).Contains(e.Location))
            KapatIstendi?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animasyon.Dispose();
            _govde.Dispose(); _kucukYazi.Dispose();
            _kalemKenar.Dispose(); _fircaKaydirma.Dispose(); _fircaYukleme.Dispose();
        }
        base.Dispose(disposing);
    }
}
