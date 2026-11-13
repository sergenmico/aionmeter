using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using AionMeter.Cevirmen;
using AionMeter.Core;

namespace AionMeter;

/// <summary>
/// Güncellemeden sonra bir kez açılan küçük "Yenilikler" kartı: başlık, sürüm, madde madde değişiklikler, × ile kapanır (Esc de).
/// İçerik assets/yenilikler.txt'den (<see cref="YenilikNotlari"/>). Kodla çizilir (Palet). Odak çalmaz (etkinleşmeden açılır),
/// en üstte durmaz: oyun öne gelirse arkasında kalır; oyun özel tam ekrana geçerse overlay kartı hemen kapatır.
/// Overlay kartı yalnız oyun önde değilken açar; oyun öndeyse tepside balon ve menüde "Yenilikler" kalır.
/// </summary>
internal sealed class YeniliklerKarti : Form
{
    private const TextFormatFlags Temel = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Tek = Temel | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                                        TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Cok = Temel | TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top;

    /// <summary>Kartın altındaki not: kapansa da yeniden açılabildiği yer.</summary>
    internal const string AltNot = "Tekrar bakmak için: tepsi menüsü → Yenilikler";

    private readonly IReadOnlyList<YenilikBolumu> _bolumler;
    private readonly float _olcek;
    private readonly Font _yaziBaslik, _yazi, _yaziKucuk, _yaziSurum;
    private readonly SolidBrush _fircaNokta = new(Palet.Vurgu);
    private readonly Pen _kalemKenar = new(Palet.AyiriciAcik);
    private readonly Pen _kalemAyirac = new(Palet.Cizgi);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly Pen _kalemKapatUst = new(Palet.Yazi, 1.5f);
    private Rectangle _kapatKutusu;
    private bool _kapatUstunde, _surukle;
    private Point _fareBas, _konumBas;

    // Yerleşim (kurucuda bir kez): madde metinlerinin kutuları
    private readonly List<(int Bolum, Rectangle Kutu, string Metin)> _maddeler = new();
    private readonly List<(string Baslik, int Y)> _basliklar = new();
    private int _altNotY;

    public YeniliklerKarti(IReadOnlyList<YenilikBolumu> bolumler, float olcek)
    {
        _bolumler = bolumler;
        _olcek = olcek;
        Text = "AionMeter yenilikler";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _yaziBaslik = Palet.YaziTipi(15f * olcek, yariKalin: true);
        _yazi = Palet.YaziTipi(12.5f * olcek);
        _yaziKucuk = Palet.YaziTipi(11f * olcek);
        _yaziSurum = Palet.YaziTipi(11f * olcek, yariKalin: true);
        _kalemKapat.Width = _kalemKapatUst.Width = Math.Max(1f, 1.5f * olcek);
        ClientSize = Yerlesim();
    }

    protected override bool ShowWithoutActivation => true;

    private int P(float v) => (int)MathF.Round(v * _olcek);

    private int Pad => P(18);
    private int BaslikH => P(46);

    /// <summary>Kartın boyutu: sabit genişlik, maddeler sarılır; yükseklik içeriğe göre.</summary>
    private Size Yerlesim()
    {
        int w = P(400), x0 = Pad, x1 = w - Pad;
        int metinX = x0 + P(14), metinW = x1 - metinX;
        int y = BaslikH + P(12);
        bool cokBolum = _bolumler.Count > 1;
        _maddeler.Clear();
        _basliklar.Clear();
        for (int b = 0; b < _bolumler.Count; b++)
        {
            if (cokBolum)
            {
                if (b > 0) y += P(6);
                _basliklar.Add((Guncelleyici.Goster(_bolumler[b].Surum), y));
                y += P(22);
            }
            foreach (string ham in _bolumler[b].Maddeler)
            {
                // "5 dk", "10 sn" satır sonunda bölünmesin: sayıdan sonraki boşluk bölünmez boşluk olur
                string m = Regex.Replace(ham, @"(\d) (?=\p{L})", "$1\u00A0");
                int h = TextRenderer.MeasureText(m, _yazi, new Size(metinW, int.MaxValue), Cok).Height;
                _maddeler.Add((b, new Rectangle(metinX, y, metinW, h), m));
                y += h + P(8);
            }
        }
        y += P(6);
        _altNotY = y;
        y += P(20) + P(14);
        return new Size(w, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Yenilikler kartı", ex); }
    }

    internal void Ciz(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height, x0 = Pad, x1 = w - Pad;
        g.Clear(Palet.Arka);

        // Başlık: "Yenilikler  v0.6.0" ve ×
        int bw = TextRenderer.MeasureText("Yenilikler", _yaziBaslik, Size.Empty, Temel | TextFormatFlags.SingleLine).Width;
        TextRenderer.DrawText(g, "Yenilikler", _yaziBaslik, new Rectangle(x0, 0, bw + P(2), BaslikH), Palet.Yazi, Tek);
        if (_bolumler.Count > 0)
            TextRenderer.DrawText(g, Guncelleyici.Goster(_bolumler[0].Surum), _yaziKucuk,
                new Rectangle(x0 + bw + P(10), P(2), P(120), BaslikH), Palet.CokSoluk, Tek);
        int k = P(26);
        _kapatKutusu = new Rectangle(x1 - k + P(6), (BaslikH - k) / 2, k, k);
        int ki = P(8);
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var kalem = _kapatUstunde ? _kalemKapatUst : _kalemKapat;
        g.DrawLine(kalem, _kapatKutusu.Left + ki, _kapatKutusu.Top + ki, _kapatKutusu.Right - ki, _kapatKutusu.Bottom - ki);
        g.DrawLine(kalem, _kapatKutusu.Right - ki, _kapatKutusu.Top + ki, _kapatKutusu.Left + ki, _kapatKutusu.Bottom - ki);
        g.SmoothingMode = eski;
        g.DrawLine(_kalemAyirac, 0, BaslikH - 1, w, BaslikH - 1);

        foreach (var (baslik, y) in _basliklar)
            TextRenderer.DrawText(g, baslik, _yaziSurum, new Rectangle(x0, y, x1 - x0, P(20)), Palet.Soluk, Tek);

        // Maddeler: solda küçük mavi nokta (ilk satırın ortasında), metin sarılır
        int cap = Math.Max(4, P(5));
        int satirH = TextRenderer.MeasureText("Ay", _yazi, new Size(P(300), int.MaxValue), Cok).Height + P(1); // sarılan metnin ilk satırı
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (_, kutu, metin) in _maddeler)
            g.FillEllipse(_fircaNokta, x0 + P(1), kutu.Top + satirH / 2f - cap / 2f, cap, cap);
        g.SmoothingMode = eski;
        foreach (var (_, kutu, metin) in _maddeler)
            TextRenderer.DrawText(g, metin, _yazi, kutu, Palet.Yazi, Cok);

        g.DrawLine(_kalemAyirac, x0, _altNotY, x1, _altNotY);
        TextRenderer.DrawText(g, AltNot, _yaziKucuk, new Rectangle(x0, _altNotY + P(6), x1 - x0, P(20)), Palet.CokSoluk, Tek);
        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare / klavye ----------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _kapatKutusu.Contains(e.Location) || e.Y >= BaslikH) return;
        _surukle = true; // başlıktan tutup taşınır
        _fareBas = Cursor.Position;
        _konumBas = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_surukle)
        {
            var p = Cursor.Position;
            Location = new Point(_konumBas.X + p.X - _fareBas.X, _konumBas.Y + p.Y - _fareBas.Y);
            return;
        }
        bool ust = _kapatKutusu.Contains(e.Location);
        Cursor = ust ? Cursors.Hand : Cursors.Default;
        if (ust == _kapatUstunde) return;
        _kapatUstunde = ust;
        Invalidate(_kapatKutusu);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_kapatUstunde) return;
        _kapatUstunde = false;
        Invalidate(_kapatKutusu);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_surukle)
        {
            _surukle = false;
            Capture = false;
            return;
        }
        if (e.Button == MouseButtons.Left && _kapatKutusu.Contains(e.Location)) Close();
    }

    // ---------------- İçerik ----------------

    /// <summary>assets/yenilikler.txt'deki tüm bölümler (en yeni önde); dosya yoksa ya da okunamazsa boş.</summary>
    public static List<YenilikBolumu> DosyadanOku()
    {
        try
        {
            string yol = Path.Combine(AppContext.BaseDirectory, "assets", "yenilikler.txt");
            return File.Exists(yol) ? YenilikNotlari.Coz(File.ReadAllText(yol)) : new List<YenilikBolumu>();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Yenilikler okunamadı", ex);
            return new List<YenilikBolumu>();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yaziBaslik.Dispose(); _yazi.Dispose(); _yaziKucuk.Dispose(); _yaziSurum.Dispose();
            _fircaNokta.Dispose(); _kalemKenar.Dispose(); _kalemAyirac.Dispose(); _kalemKapat.Dispose(); _kalemKapatUst.Dispose();
        }
        base.Dispose(disposing);
    }
}
