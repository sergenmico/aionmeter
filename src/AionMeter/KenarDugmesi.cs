using System.Drawing.Drawing2D;

namespace AionMeter;

/// <summary>
/// Overlay'in dış kenarına (ekran kenarından uzak tarafa) yapışık küçük sekme: tıklayınca tepsi menüsü yanında açılır,
/// Alt+Tab ile ayarlara gitmek gerekmez. Overlay kilitli ve tıklanmazken de her zaman tıklanır (hiçbir zaman
/// WS_EX_TRANSPARENT değil), ama odak çalmaz (WS_EX_NOACTIVATE + MA_NOACTIVATE). Görünürlüğünü ve opaklığını overlay yönetir;
/// fare üstündeyken tam görünür ve belirginleşir. Yüzey yalnız boyut/taraf/üstünde durumu değişince yeniden çizilir.
/// </summary>
internal sealed class KenarDugmesi : KatmanPencere
{
    private const float GenislikPx = 12f, YukseklikPx = 30f, UstBoslukPx = 8f;

    private float _olcek = 1f;
    private bool _sagda = true;   // sekme overlay'in sağında (overlay ekranın sol yarısında)
    private bool _ustunde;
    private byte _overlayAlfa = 255;

    /// <summary>Sol ya da sağ tıkla basıldı.</summary>
    public event Action? Tiklandi;

    /// <summary>Sekme overlay'in sağında mı (menü bu yöne açılır).</summary>
    public bool Sagda => _sagda;

    public KenarDugmesi() : base(tiklanmaz: false, "AionMeter Menü")
    {
        Cursor = Cursors.Hand;
    }

    /// <summary>
    /// Overlay'in dikdörtgenine ve ölçeğine göre yerleşir (gösterip gizlemez). Overlay ekranın sol yarısındaysa sağ
    /// kenarına, sağ yarısındaysa sol kenarına yapışır; dikeyde başlık hizasında.
    /// </summary>
    public void Yerlestir(Rectangle overlay, float olcek)
    {
        if (overlay.Width <= 0 || overlay.Height <= 0) return;
        var ekran = Screen.FromRectangle(overlay).WorkingArea;
        bool sagda = overlay.Left + overlay.Width / 2 < ekran.Left + ekran.Width / 2;
        int w = Math.Max(8, (int)MathF.Round(GenislikPx * olcek));
        int bosluk = (int)MathF.Round(UstBoslukPx * olcek);
        int h = Math.Clamp((int)MathF.Round(YukseklikPx * olcek), Math.Min(16, overlay.Height), Math.Max(16, overlay.Height - 2 * bosluk));
        int x = sagda ? overlay.Right : overlay.Left - w;
        int y = overlay.Top + Math.Min(bosluk, Math.Max(0, overlay.Height - h));
        var alan = new Rectangle(x, y, w, h);
        if (alan == Alan && sagda == _sagda && olcek == _olcek) return;
        bool yenidenCiz = alan.Size != Alan.Size || sagda != _sagda || olcek != _olcek;
        _sagda = sagda;
        _olcek = olcek;
        if (yenidenCiz) Yenile(alan);
        else Tasi(alan.Location);
    }

    /// <summary>Overlay'in o anki opaklığı (0..255); fare üstündeyken yok sayılır (tam görünür).</summary>
    public void OverlayAlfa(int alfa)
    {
        _overlayAlfa = (byte)Math.Clamp(alfa, 0, 255);
        if (!_ustunde) AlfaAyarla(_overlayAlfa);
    }

    private void Yenile(Rectangle alan)
    {
        try
        {
            AlfaAyarla(_ustunde ? (byte)255 : _overlayAlfa);
            bool sagda = _sagda, ustunde = _ustunde;
            float olcek = _olcek;
            Bas(alan, g => Ciz(g, alan.Size, olcek, sagda, ustunde));
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Kenar düğmesi", ex);
        }
    }

    // ---------------- Fare ----------------

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        UstundeAyarla(true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_ustunde) UstundeAyarla(true); // stil/görünürlük değişiminden sonra Enter gelmeyebilir
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UstundeAyarla(false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) && new Rectangle(Point.Empty, Alan.Size).Contains(e.Location))
            Tiklandi?.Invoke();
    }

    /// <summary>Overlay gizlenince ya da menü kapanınca: fare durumunu sıfırla.</summary>
    public void UstundeAyarla(bool ustunde)
    {
        if (ustunde == _ustunde) return;
        _ustunde = ustunde;
        if (!Alan.IsEmpty) Yenile(Alan);
    }

    // ---------------- Çizim (statik: ekran dışı bitmap'e de çizilebilir) ----------------

    /// <summary>Dış tarafı yuvarlak, overlay'e bakan tarafı düz sekme; ortada dikey üç nokta.</summary>
    internal static void Ciz(Graphics g, Size boyut, float olcek, bool sagda, bool ustunde)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float w = boyut.Width, h = boyut.Height;
        float r = Math.Min(w * 0.5f, Math.Max(2f, 4f * olcek));
        using var yol = new GraphicsPath();
        // Overlay'e bakan kenar düz (overlay çerçevesiyle birleşir), dış köşeler yuvarlak.
        if (sagda)
        {
            yol.AddLine(0, 0, w - r, 0);
            yol.AddArc(w - 2 * r, 0, 2 * r, 2 * r, 270, 90);
            yol.AddLine(w, r, w, h - r);
            yol.AddArc(w - 2 * r, h - 2 * r, 2 * r, 2 * r, 0, 90);
            yol.AddLine(w - r, h, 0, h);
        }
        else
        {
            yol.AddLine(w, h, r, h);
            yol.AddArc(0, h - 2 * r, 2 * r, 2 * r, 90, 90);
            yol.AddLine(0, h - r, 0, r);
            yol.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
            yol.AddLine(r, 0, w, 0);
        }
        yol.CloseFigure();

        using (var zemin = new SolidBrush(ustunde ? Palet.PanelUst : Palet.Arka))
            g.FillPath(zemin, yol);

        // Kenar: sadece dış üç kenar (overlay'e bakan kenar çizilmez)
        float k = Math.Max(1f, MathF.Round(olcek));
        using (var kalem = new Pen(ustunde ? Palet.Vurgu : Palet.Cizgi, k))
        {
            var eskiKirp = g.Clip;
            g.SetClip(sagda ? new RectangleF(k, 0, w, h) : new RectangleF(-1, 0, w - k + 1, h));
            using var ic = (GraphicsPath)yol.Clone();
            using (var m = new Matrix())
            {
                // Kalemi şeklin içine çek ki dış kenarda yarım piksel kaybolmasın
                float sx = (w - k) / w, sy = (h - k) / h;
                m.Translate(k / 2f, k / 2f);
                m.Scale(sx, sy);
                ic.Transform(m);
            }
            g.DrawPath(kalem, ic);
            g.Clip = eskiKirp;
            eskiKirp.Dispose();
        }

        // Dikey üç nokta (⋮), sekmenin ortasında
        float d = Math.Max(2f, 2.6f * olcek);
        float ara = Math.Max(d + 1.5f, 5.5f * olcek);
        float cx = w / 2f, cy = h / 2f;
        using var nokta = new SolidBrush(ustunde ? Palet.Yazi : Palet.Soluk);
        for (int i = -1; i <= 1; i++)
            g.FillEllipse(nokta, cx - d / 2f, cy + i * ara - d / 2f, d, d);
    }
}
