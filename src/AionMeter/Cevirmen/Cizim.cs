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

    /// <summary>
    /// Beş köşeli yıldız (favori boss işareti): (<paramref name="cx"/>, <paramref name="cy"/>) merkez, <paramref name="r"/> dış yarıçap.
    /// Dolgu ya da kalem (ya da ikisi) verilir. <paramref name="n"/>: 10 noktalık yeniden kullanılan tampon (çizimde ayırma yok).
    /// </summary>
    public static void Yildiz(Graphics g, Brush? dolgu, Pen? kalem, float cx, float cy, float r, PointF[] n)
    {
        cy += r * 0.08f; // yıldızın ağırlık merkezi biraz aşağıda: satırda dikey ortada dursun
        for (int i = 0; i < 10; i++)
        {
            double a = (-90 + i * 36) * Math.PI / 180;
            float yr = i % 2 == 0 ? r : r * 0.46f;
            n[i] = new PointF(cx + (float)Math.Cos(a) * yr, cy + (float)Math.Sin(a) * yr);
        }
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (dolgu is not null) g.FillPolygon(dolgu, n);
        if (kalem is not null) g.DrawPolygon(kalem, n);
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
