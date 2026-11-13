using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AionMeter;

/// <summary>Gizli Küp işareti: kodla çizilmiş izometrik küp (üst yüz açık, yan yüzler koyu). Overlay rozeti ve afiş ortak kullanır.</summary>
internal static class KupSimgesi
{
    public static Color Sol => Color.FromArgb(Palet.Kup.R * 72 / 100, Palet.Kup.G * 72 / 100, Palet.Kup.B * 72 / 100);
    public static Color Sag => Color.FromArgb(Palet.Kup.R * 52 / 100, Palet.Kup.G * 52 / 100, Palet.Kup.B * 52 / 100);

    /// <summary><paramref name="x"/>: sol kenar, <paramref name="cy"/>: dikey orta, <paramref name="boy"/>: kenar uzunluğu.
    /// <paramref name="n"/>: 4 noktalık yeniden kullanılan tampon (karede tahsis olmasın).</summary>
    public static void Ciz(Graphics g, float x, float cy, float boy, Brush ust, Brush sol, Brush sag, PointF[] n)
    {
        float w = boy, h = boy;
        float mx = x + w / 2f, ustY = cy - h / 2f, altY = cy + h / 2f, q = h / 4f;
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        n[0] = new PointF(mx, ustY); n[1] = new PointF(x + w, ustY + q); n[2] = new PointF(mx, cy); n[3] = new PointF(x, ustY + q);
        g.FillPolygon(ust, n);
        n[0] = new PointF(x, ustY + q); n[1] = new PointF(mx, cy); n[2] = new PointF(mx, altY); n[3] = new PointF(x, altY - q);
        g.FillPolygon(sol, n);
        n[0] = new PointF(mx, cy); n[1] = new PointF(x + w, ustY + q); n[2] = new PointF(x + w, altY - q); n[3] = new PointF(mx, altY);
        g.FillPolygon(sag, n);
        g.SmoothingMode = eski;
    }
}

/// <summary>
/// Gizli Küp afişi: küp sesiyle aynı anda, oyunun bulunduğu ekranın yatay ortasında, üstten ~%30'da büyük
/// "Küp var" yazısı. Her zaman tıklanmaz (WS_EX_TRANSPARENT), odak çalmaz, piksel başına saydam.
/// ~150 ms belirir, ~4 sn bekler, ~600 ms'de söner; sonra gizlenir ve zamanlayıcı durur (boşta CPU/RAM yok:
/// yüzey bitmap'i çizimden hemen sonra bırakılır). Oyun özel tam ekrandayken ya da önde değilken overlay hiç göstermez,
/// gösterirken bu duruma geçilirse <see cref="Kapat"/> ile hemen kapanır.
/// </summary>
internal sealed class KupAfisi : KatmanPencere
{
    public const string Metin = "Küp var";
    private const int BelirmeMs = 150, BeklemeMs = 4000, SonmeMs = 600;

    private readonly System.Windows.Forms.Timer _zamanlayici = new() { Interval = 16 };
    private readonly Stopwatch _saat = new();
    private float _sonOlcek = -1f;
    private Rectangle _sonEkran;

    public KupAfisi() : base(tiklanmaz: true, "AionMeter Küp")
    {
        _zamanlayici.Tick += (_, _) => Ilerle();
    }

    /// <summary>
    /// Afişi <paramref name="hedef"/> alanının (oyun penceresi ya da ekranı, fiziksel px) ortasında gösterir.
    /// Zaten görünüyorsa yeniden belirmeden tam görünür hale gelir ve bekleme baştan başlar.
    /// </summary>
    public void Goster(Rectangle hedef)
    {
        try
        {
            float olcek = Olcek(hedef);
            if (!Acik || olcek != _sonOlcek || hedef != _sonEkran)
            {
                _sonOlcek = olcek;
                _sonEkran = hedef;
                var boyut = Boyut(olcek);
                var konum = new Point(hedef.Left + (hedef.Width - boyut.Width) / 2,
                                      hedef.Top + (int)(hedef.Height * 0.30f) - boyut.Height / 2);
                if (!Acik) AlfaAyarla(0);
                Bas(new Rectangle(konum, boyut), g => Ciz(g, boyut, olcek));
            }
            if (Acik)
            {
                // Sönerken / beklerken yeniden geldi: belirmeyi atla, tam görünür kal, bekleme baştan.
                _saat.Restart();
                _saatKaydir = BelirmeMs;
                AlfaAyarla(255);
            }
            else
            {
                _saat.Restart();
                _saatKaydir = 0;
                PencereGoster();
            }
            _zamanlayici.Interval = 16;
            _zamanlayici.Start();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp afişi", ex);
            Kapat();
        }
    }

    private long _saatKaydir; // yeniden tetiklenince belirme evresi atlanır

    /// <summary>Hemen gizler (tam ekran / oyun arkada).</summary>
    public void Kapat()
    {
        _zamanlayici.Stop();
        _saat.Reset();
        PencereGizle();
        AlfaAyarla(0);
    }

    private void Ilerle()
    {
        long t = _saat.ElapsedMilliseconds + _saatKaydir;
        if (t < BelirmeMs)
        {
            double u = 1.0 - t / (double)BelirmeMs;
            AlfaAyarla((byte)Math.Round(255 * (1.0 - u * u * u))); // ease-out
            return;
        }
        if (t < BelirmeMs + BeklemeMs)
        {
            AlfaAyarla(255);
            // Beklerken saniyede ~4 kez uyanmak yeter; sönme başlayınca yeniden ~60 fps.
            long kalan = BelirmeMs + BeklemeMs - t;
            _zamanlayici.Interval = (int)Math.Clamp(kalan, 16, 250);
            return;
        }
        _zamanlayici.Interval = 16;
        long s = t - BelirmeMs - BeklemeMs;
        if (s >= SonmeMs)
        {
            Kapat();
            return;
        }
        double k = s / (double)SonmeMs;
        AlfaAyarla((byte)Math.Round(255 * (1.0 - k * k))); // ease-in: yavaş başlar, sona doğru hızlanır
    }

    // ---------------- Ölçü ve çizim (statik: ekran dışı bitmap'e de çizilebilir) ----------------

    private static int P(float v, float olcek) => (int)MathF.Round(v * olcek);

    /// <summary>Yazı tipi: Bahnschrift SemiBold (yoksa Segoe UI Semibold).</summary>
    private static Font Yazi(float olcek) => Palet.RakamTipi(30f * olcek, yariKalin: true);

    private static GraphicsPath MetinYolu(Font f)
    {
        var yol = new GraphicsPath();
        yol.AddString(Metin, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic);
        return yol;
    }

    internal static Size Boyut(float olcek)
    {
        using var f = Yazi(olcek);
        using var yol = MetinYolu(f);
        var b = yol.GetBounds();
        int kup = P(26, olcek);
        int w = P(22, olcek) + kup + P(14, olcek) + (int)MathF.Ceiling(b.Width) + P(24, olcek);
        int h = Math.Max(kup, (int)MathF.Ceiling(f.GetHeight())) + 2 * P(12, olcek);
        return new Size(w, h);
    }

    internal static void Ciz(Graphics g, Size boyut, float olcek)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // Zemin: hafif koyu, yarı saydam, yuvarlak köşeli; ince sıcak kenar
        float r = P(10, olcek);
        var zemin = new RectangleF(0.5f, 0.5f, boyut.Width - 1f, boyut.Height - 1f);
        using (var yol = Yuvarlak(zemin, r))
        {
            using var firca = new SolidBrush(Color.FromArgb(150, 12, 14, 19));
            g.FillPath(firca, yol);
            using var kenar = new Pen(Color.FromArgb(90, Palet.Kup), Math.Max(1f, olcek));
            g.DrawPath(kenar, yol);
        }

        // Küp simgesi
        float cy = boyut.Height / 2f;
        int kup = P(26, olcek);
        float kx = P(22, olcek);
        using (var ust = new SolidBrush(Palet.Kup))
        using (var sol = new SolidBrush(KupSimgesi.Sol))
        using (var sag = new SolidBrush(KupSimgesi.Sag))
            KupSimgesi.Ciz(g, kx, cy, kup, ust, sol, sag, new PointF[4]);

        // Metin: koyu dış çizgi + altın dolgu (oyunun parlak sahnesinde de okunur)
        using var f = Yazi(olcek);
        using var metin = MetinYolu(f);
        var b = metin.GetBounds();
        float tx = kx + kup + P(14, olcek) - b.Left;
        float ty = cy - (b.Top + b.Height / 2f);
        using (var m = new Matrix())
        {
            m.Translate(tx, ty);
            metin.Transform(m);
        }
        using (var golge = new Pen(Color.FromArgb(210, 0, 0, 0), Math.Max(2f, 4f * olcek)) { LineJoin = LineJoin.Round })
            g.DrawPath(golge, metin);
        using (var dolgu = new SolidBrush(Palet.Kup))
            g.FillPath(dolgu, metin);
    }

    private static GraphicsPath Yuvarlak(RectangleF r, float yaricap)
    {
        float d = Math.Max(1f, yaricap * 2f);
        var yol = new GraphicsPath();
        yol.AddArc(r.Left, r.Top, d, d, 180, 90);
        yol.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        yol.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        yol.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        yol.CloseFigure();
        return yol;
    }

    /// <summary>Hedef alanın bulunduğu monitörün DPI ölçeği (en az 1).</summary>
    private static float Olcek(Rectangle hedef)
    {
        try
        {
            var orta = new POINT { X = hedef.Left + hedef.Width / 2, Y = hedef.Top + hedef.Height / 2 };
            IntPtr mon = MonitorFromPoint(orta, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0)
                return Math.Max(1f, dx / 96f);
        }
        catch { /* shcore yoksa varsayılan */ }
        return 1f;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _zamanlayici.Stop();
            _zamanlayici.Dispose();
        }
        base.Dispose(disposing);
    }
}
