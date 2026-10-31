using System.Drawing.Drawing2D;

namespace AionMeter;

/// <summary>Sistem tepsisi simgesi ve Türkçe sağ tık menüsü. Simge kodla çizilir (resim dosyası yok).</summary>
internal sealed class Tepsi : IDisposable
{
    private readonly NotifyIcon _simge;
    private readonly ToolStripMenuItem _kilit, _goster, _sadeceOyunda;
    private readonly IntPtr _hIcon;
    private readonly Icon _icon;

    public ContextMenuStrip Menu { get; }

    public Tepsi(Action kilitDegistir, Action gosterDegistir, Action sadeceOyundaDegistir, Action sifirla, Action detayim, Action cikis)
    {
        Menu = new ContextMenuStrip { ShowImageMargin = false };
        _kilit = new ToolStripMenuItem("Kilitle", null, (_, _) => kilitDegistir()) { ShortcutKeyDisplayString = "Ctrl+Alt+L" };
        _goster = new ToolStripMenuItem("Gizle", null, (_, _) => gosterDegistir()) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
        var sifir = new ToolStripMenuItem("Sıfırla", null, (_, _) => sifirla()) { ShortcutKeyDisplayString = "Ctrl+Alt+R" };
        var detay = new ToolStripMenuItem("Detayım (Ctrl+Alt+D)", null, (_, _) => detayim());
        _sadeceOyunda = new ToolStripMenuItem("Sadece oyun öndeyken göster", null, (_, _) => sadeceOyundaDegistir());
        var cik = new ToolStripMenuItem("Çıkış", null, (_, _) => cikis());
        Menu.ShowCheckMargin = true;
        Menu.Items.AddRange(new ToolStripItem[] { _kilit, _goster, sifir, detay, new ToolStripSeparator(), _sadeceOyunda, new ToolStripSeparator(), cik });

        (_icon, _hIcon) = SimgeCiz();
        _simge = new NotifyIcon
        {
            Icon = _icon,
            Text = "AionMeter",
            ContextMenuStrip = Menu,
            Visible = true,
        };
        _simge.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) gosterDegistir(); };
    }

    public void Guncelle(bool kilitli, bool gorunur, bool sadeceOyunda)
    {
        _sadeceOyunda.Checked = sadeceOyunda;
        _kilit.Text = kilitli ? "Kilidi Aç" : "Kilitle";
        _goster.Text = gorunur ? "Gizle" : "Göster";
        _simge.Text = kilitli ? "AionMeter (kilitli)" : "AionMeter";
    }

    public void Bildir(string metin)
    {
        try { _simge.ShowBalloonTip(5000, "AionMeter", metin, ToolTipIcon.Info); }
        catch (Exception ex) { HataLog.Yaz("Tepsi bildirimi", ex); }
    }

    private static (Icon, IntPtr) SimgeCiz()
    {
        Size boyut = SystemInformation.SmallIconSize;
        int n = Math.Max(16, boyut.Width);
        using var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = n / 16f;
            using (var arka = new SolidBrush(Color.FromArgb(28, 32, 40)))
                g.FillRectangle(arka, 0, 0, n, n);
            using var kirmizi = new SolidBrush(Color.FromArgb(214, 64, 52));
            using var mavi = new SolidBrush(Color.FromArgb(86, 156, 240));
            // Üstte boss çubuğu, altta üç hasar çubuğu
            g.FillRectangle(kirmizi, 2 * s, 2 * s, 12 * s, 3 * s);
            g.FillRectangle(mavi, 2 * s, 7 * s, 10 * s, 2 * s);
            g.FillRectangle(mavi, 2 * s, 10 * s, 7 * s, 2 * s);
            g.FillRectangle(mavi, 2 * s, 13 * s, 4 * s, 2 * s);
        }
        IntPtr h = bmp.GetHicon();
        return (Icon.FromHandle(h), h);
    }

    public void Dispose()
    {
        _simge.Visible = false;
        _simge.Dispose();
        Menu.Dispose();
        _icon.Dispose();
        Native.DestroyIcon(_hIcon);
    }
}
