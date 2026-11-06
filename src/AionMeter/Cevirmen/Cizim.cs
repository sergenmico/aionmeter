using System.Drawing.Drawing2D;

namespace AionMeter.Cevirmen;

/// <summary>Çevirmen pencerelerinin ortak renkleri (sayacın paleti) ve küçük çizim yardımcıları.</summary>
internal static class Cizim
{
    // Ortak paletten (AionMeter.Palet): sayaç, detay paneli ve ayarlar penceresiyle aynı renkler.
    public static readonly Color Arka = Palet.Arka;
    public static readonly Color Yazi = Palet.Yazi;
    public static readonly Color Soluk = Palet.Soluk;
    public static readonly Color CokSoluk = Palet.CokSoluk;
    public static readonly Color Cizgi = Palet.Cizgi;          // 1 px kenar
    public static readonly Color Terim = Palet.Terim;          // korunan terimler: hafif sıcak ton
    public static readonly Color TusKenari = Palet.TusKenari;

    /// <summary>Segoe UI Variable Text varsa onu, yoksa Segoe UI'ı döndürür (GDI+ olmayan yazı tipini sessizce değiştirir).</summary>
    public static Font GovdeYaziTipi(float pikselBoyu)
    {
        var f = new Font("Segoe UI Variable Text", pikselBoyu, FontStyle.Regular, GraphicsUnit.Pixel);
        if (string.Equals(f.Name, "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase)) return f;
        f.Dispose();
        return new Font("Segoe UI", pikselBoyu, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public static GraphicsPath YuvarlakDikdortgen(RectangleF r, float yaricap)
    {
        float c = Math.Min(yaricap * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (c <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, c, c, 180, 90);
        p.AddArc(r.Right - c, r.Y, c, c, 270, 90);
        p.AddArc(r.Right - c, r.Bottom - c, c, c, 0, 90);
        p.AddArc(r.X, r.Bottom - c, c, c, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void YuvarlakDoldur(Graphics g, Brush firca, RectangleF r, float yaricap)
    {
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var yol = YuvarlakDikdortgen(r, yaricap);
        g.FillPath(firca, yol);
        g.SmoothingMode = eski;
    }

    public static void YuvarlakCiz(Graphics g, Pen kalem, RectangleF r, float yaricap)
    {
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var yol = YuvarlakDikdortgen(r, yaricap);
        g.DrawPath(kalem, yol);
        g.SmoothingMode = eski;
    }
}
