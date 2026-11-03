using System.Drawing.Drawing2D;

namespace AionMeter.Cevirmen;

/// <summary>
/// Ctrl+Alt+T: tüm sanal ekranı kaplayan seçim penceresi. Kısayol anında DONDURULMUŞ ekran görüntüsü %45 karartılır;
/// seçilen dikdörtgen karartmasız (kesik) görünür, 1 px yarı saydam beyaz kenarlı, altında boyut etiketi.
/// Üst ortada sade ipucu ("Çevrilecek alanı sürükle · Esc vazgeç"); sürükleme başlayınca gizlenir.
/// Esc / sağ tık / 12×12'den küçük seçim vazgeçer. Açık eylem olduğu için kısa süre odak alır;
/// çağıran kapandıktan sonra odağı önceki pencereye (oyuna) geri verir.
/// </summary>
internal sealed class BolgeSecimFormu : Form
{
    private const int EnAzBoyut = 12;
    private const string IpucuMetni = "Çevrilecek alanı sürükle";
    private const string TusMetni = "Esc";
    private const string VazgecMetni = "vazgeç";

    private readonly Bitmap _donmus;
    private readonly Rectangle _sanal;
    private readonly float _olcek;
    private readonly SolidBrush _karartma = new(Color.FromArgb(115, 0, 0, 0));           // %45
    private readonly SolidBrush _hap = new(Color.FromArgb(235, Cizim.Arka));             // %92
    private readonly Pen _kenar = new(Color.FromArgb(179, 255, 255, 255), 1f);           // %70 beyaz
    private readonly Pen _tusKenari = new(Cizim.TusKenari, 1f);
    private readonly Font _ipucuYazi, _kucukYazi;
    private const TextFormatFlags Tek = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                                        TextFormatFlags.VerticalCenter;

    // İpucu hapı (pencere koordinatları) ve iç parçaları
    private readonly Rectangle _ipucu, _ipucuMetin, _tus, _vazgec;
    private Point _bas;
    private bool _suruklu, _ipucuGorunur = true;
    private Rectangle _secim;
    private Rectangle _etiket; // boyut etiketi hapı
    private string _etiketMetni = "";

    /// <summary>Seçim, pencere (dondurulmuş görüntü) koordinatlarında.</summary>
    public Rectangle SecimGoruntu => _secim;

    /// <summary>Seçim, ekran koordinatlarında.</summary>
    public Rectangle SecimEkran => new(_secim.X + _sanal.X, _secim.Y + _sanal.Y, _secim.Width, _secim.Height);

    public BolgeSecimFormu(Bitmap donmus, Rectangle sanalEkran, float olcek)
    {
        _donmus = donmus;
        _sanal = sanalEkran;
        _olcek = olcek;
        _ipucuYazi = new Font("Segoe UI", 12f * olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _kucukYazi = new Font("Segoe UI", 11f * olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        Text = "AionMeter bölge seçimi";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
        Bounds = sanalEkran;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // İpucu hapı: imlecin bulunduğu ekranın üst ortası, 24 px aşağıda.
        int metinW = Olc(IpucuMetni, _ipucuYazi), tusW = Olc(TusMetni, _kucukYazi) + P(10), vazW = Olc(VazgecMetni, _ipucuYazi);
        int icH = P(18), yatay = P(14), ara = P(14);
        int hapW = yatay + metinW + ara + tusW + P(6) + vazW + yatay, hapH = P(34);
        var ekran = Screen.FromPoint(Cursor.Position).Bounds;
        int hx = ekran.X + (ekran.Width - hapW) / 2 - _sanal.X, hy = ekran.Y + P(24) - _sanal.Y;
        _ipucu = new Rectangle(hx, hy, hapW, hapH);
        int iy = hy + (hapH - icH) / 2;
        _ipucuMetin = new Rectangle(hx + yatay, iy, metinW, icH);
        _tus = new Rectangle(_ipucuMetin.Right + ara, iy, tusW, icH);
        _vazgec = new Rectangle(_tus.Right + P(6), iy, vazW, icH);
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);
    private static int Olc(string m, Font f) => TextRenderer.MeasureText(m, f, Size.Empty, Tek).Width;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true; // birden çok monitörü kaplayan pencere yeniden boyutlanmasın
        base.OnDpiChanged(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _sanal;
        OdakYardimcisi.OneGetir(this); // Esc çalışsın; kapanınca çağıran odağı oyuna geri verir
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics, e.ClipRectangle); }
        catch (Exception ex) { HataLog.Yaz("Bölge seçimi çizimi", ex); }
    }

    private void Ciz(Graphics g, Rectangle kirpma)
    {
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_donmus, kirpma, kirpma, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;

        // Karartma: seçimin dışı (seçim içi kesik, dondurulmuş görüntü olduğu gibi)
        if (_secim.Width > 0 && _secim.Height > 0)
        {
            using var dis = new Region(kirpma);
            dis.Exclude(_secim);
            g.FillRegion(_karartma, dis);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.DrawRectangle(_kenar, _secim.X, _secim.Y, _secim.Width - 1, _secim.Height - 1);
        }
        else
        {
            g.FillRectangle(_karartma, kirpma);
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;

        if (_etiketMetni.Length > 0 && _etiket.IntersectsWith(kirpma))
        {
            Cizim.YuvarlakDoldur(g, _hap, _etiket, _etiket.Height / 2f);
            TextRenderer.DrawText(g, _etiketMetni, _kucukYazi, _etiket, Cizim.Soluk, Tek | TextFormatFlags.HorizontalCenter);
        }

        if (_ipucuGorunur && _ipucu.IntersectsWith(kirpma))
        {
            Cizim.YuvarlakDoldur(g, _hap, _ipucu, _ipucu.Height / 2f);
            TextRenderer.DrawText(g, IpucuMetni, _ipucuYazi, _ipucuMetin, Cizim.Yazi, Tek);
            Cizim.YuvarlakCiz(g, _tusKenari, new RectangleF(_tus.X + 0.5f, _tus.Y + 0.5f, _tus.Width - 1, _tus.Height - 1), P(4));
            TextRenderer.DrawText(g, TusMetni, _kucukYazi, _tus, Cizim.Soluk, Tek | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, VazgecMetni, _ipucuYazi, _vazgec, Cizim.Soluk, Tek);
        }
    }

    /// <summary>"420 × 180" etiketi: seçimin sağ altında, dışarı taşarsa içeri alınır.</summary>
    private void EtiketGuncelle()
    {
        if (_secim.Width <= 0 || _secim.Height <= 0)
        {
            _etiket = Rectangle.Empty;
            _etiketMetni = "";
            return;
        }
        _etiketMetni = _secim.Width.ToString(Bicim.Tr) + " × " + _secim.Height.ToString(Bicim.Tr);
        int w = Olc(_etiketMetni, _kucukYazi) + P(14), h = P(20), ara = P(6);
        int x = _secim.Right - w;
        int y = _secim.Bottom + ara;
        if (y + h > ClientSize.Height) y = _secim.Bottom - ara - h; // ekran altına taştı: içeri
        x = Math.Clamp(x, 0, Math.Max(0, ClientSize.Width - w));
        _etiket = new Rectangle(x, y, w, h);
    }

    // ---------------- Fare / klavye ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _bas = e.Location;
        _suruklu = true;
        _secim = Rectangle.Empty;
        if (_ipucuGorunur)
        {
            _ipucuGorunur = false; // sürükleme başlayınca ipucu gizlenir
            Invalidate(Rectangle.Inflate(_ipucu, 2, 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_suruklu) return;
        var eskiSecim = _secim;
        var eskiEtiket = _etiket;
        _secim = Rectangle.FromLTRB(Math.Min(_bas.X, e.X), Math.Min(_bas.Y, e.Y), Math.Max(_bas.X, e.X), Math.Max(_bas.Y, e.Y));
        _secim.Intersect(new Rectangle(0, 0, _donmus.Width, _donmus.Height));
        EtiketGuncelle();
        // Sadece değişen bölgeyi yeniden çiz (4K ekranda tüm pencereyi her harekette çizmek pahalı).
        var kirli = Rectangle.Union(Rectangle.Union(eskiSecim, _secim), Rectangle.Union(eskiEtiket, _etiket));
        kirli.Inflate(3, 3);
        Invalidate(kirli);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !_suruklu) return;
        _suruklu = false;
        // 12×12'den küçük: kaza ile tıklama sayılır, vazgeç.
        DialogResult = _secim.Width >= EnAzBoyut && _secim.Height >= EnAzBoyut ? DialogResult.OK : DialogResult.Cancel;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _karartma.Dispose(); _hap.Dispose(); _kenar.Dispose(); _tusKenari.Dispose();
            _ipucuYazi.Dispose(); _kucukYazi.Dispose();
        }
        base.Dispose(disposing); // _donmus çağıranındır
    }
}
