using System.Drawing.Drawing2D;

namespace AionMeter;

/// <summary>Sistem tepsisi simgesi ve Türkçe sağ tık menüsü. Simge kodla çizilir (resim dosyası yok).</summary>
internal sealed class Tepsi : IDisposable
{
    private readonly NotifyIcon _simge;
    private readonly ToolStripMenuItem _kilit, _goster, _denetle, _kukla, _kuklaKisa, _kuklaOzel, _kuklaIptal, _alarm;
    private readonly ToolStripMenuItem[] _kuklaSureleri;
    private readonly IntPtr _hIcon;
    private readonly Icon _icon;

    public ContextMenuStrip Menu { get; }

    /// <summary>Kısa menü: sık kullanılanlar burada, geri kalan her şey Ayarlar penceresinde.</summary>
    /// <param name="kuklaBaslat">Süre (sn) ile kukla testini başlatır; 0 = ayarlardaki özel süre.</param>
    /// <param name="kuklaKisayol">Kukla kısayoluyla aynı iş: ayardaki süreyle başlat ya da iptal et / kartı kapat.</param>
    public Tepsi(Action kilitDegistir, Action gosterDegistir, Action sifirla, Action detayim, Action bolgeCevir,
                 Action ayarlar, Action guncellemeDenetle, Action cikis, Action<int> kuklaBaslat, Action kuklaIptal, Action alarm,
                 Action kuklaKisayol)
    {
        Menu = new ContextMenuStrip { ShowImageMargin = false };
        _kilit = new ToolStripMenuItem("Kilitle", null, (_, _) => kilitDegistir()) { ShortcutKeyDisplayString = "Ctrl+Alt+L" };
        _goster = new ToolStripMenuItem("Gizle", null, (_, _) => gosterDegistir()) { ShortcutKeyDisplayString = "Ctrl+Alt+H" };
        var sifir = new ToolStripMenuItem("Sıfırla", null, (_, _) => sifirla()) { ShortcutKeyDisplayString = "Ctrl+Alt+R" };
        var detay = new ToolStripMenuItem("Detayım", null, (_, _) => detayim()) { ShortcutKeyDisplayString = "Ctrl+Alt+D" };
        var bolge = new ToolStripMenuItem("Bölge çevir", null, (_, _) => bolgeCevir()) { ShortcutKeyDisplayString = "Ctrl+Alt+T" };

        // Kukla testi: hazır süreler + özel süre (Ayarlar'dan) + iptal
        _kukla = new ToolStripMenuItem("Kukla testi");
        _kuklaKisa = new ToolStripMenuItem("Başlat", null, (_, _) => kuklaKisayol());
        _kukla.DropDownItems.Add(_kuklaKisa);
        _kukla.DropDownItems.Add(new ToolStripSeparator());
        _kuklaSureleri = Array.ConvertAll(Ayarlar.KuklaSureleri, sn =>
            new ToolStripMenuItem(sn.ToString(Bicim.Tr) + " sn", null, (_, _) => kuklaBaslat(sn)));
        _kuklaOzel = new ToolStripMenuItem("Özel", null, (_, _) => kuklaBaslat(0));
        _kuklaIptal = new ToolStripMenuItem("Testi iptal et", null, (_, _) => kuklaIptal()) { Enabled = false };
        _kukla.DropDownItems.AddRange(_kuklaSureleri);
        _kukla.DropDownItems.Add(_kuklaOzel);
        _kukla.DropDownItems.Add(new ToolStripSeparator());
        _kukla.DropDownItems.Add(_kuklaIptal);
        _alarm = new ToolStripMenuItem("Alarm…", null, (_, _) => alarm());

        var ayar = new ToolStripMenuItem("Ayarlar…", null, (_, _) => ayarlar());
        _denetle = new ToolStripMenuItem("Güncellemeleri denetle", null, (_, _) => guncellemeDenetle());
        var cik = new ToolStripMenuItem("Çıkış", null, (_, _) => cikis());
        Menu.Items.AddRange(new ToolStripItem[] { _kilit, _goster, sifir, detay, new ToolStripSeparator(), bolge,
            _kukla, _alarm, new ToolStripSeparator(), ayar, _denetle, new ToolStripSeparator(), cik });

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

    public void Guncelle(bool kilitli, bool gorunur, bool denetleniyor)
    {
        _kilit.Text = kilitli ? "Kilidi Aç" : "Kilitle";
        _goster.Text = gorunur ? "Gizle" : "Göster";
        _denetle.Text = denetleniyor ? "Denetleniyor…" : "Güncellemeleri denetle";
        _denetle.Enabled = !denetleniyor;
        _simge.Text = kilitli ? "AionMeter (kilitli)" : "AionMeter";
    }

    /// <summary>Kukla alt menüsü: seçili süre işaretli, test sürerken iptal etkin; alarm kalan süresi.</summary>
    public void KuklaGuncelle(int sure, bool ozel, int ozelSure, bool etkin, bool kartAcik, string kisayol, string? alarmKalan)
    {
        // İlk öğe kısayolla aynı işi yapar; kısayol metni yanında görünür (kapalıysa boş).
        _kuklaKisa.Text = etkin ? "Testi iptal et" : kartAcik ? "Sonuç kartını kapat"
            : "Başlat (" + (ozel ? ozelSure : sure).ToString(Bicim.Tr) + " sn)";
        _kuklaKisa.ShortcutKeyDisplayString = kisayol;
        _kukla.ShortcutKeyDisplayString = kisayol;
        for (int i = 0; i < _kuklaSureleri.Length; i++) _kuklaSureleri[i].Checked = !ozel && Ayarlar.KuklaSureleri[i] == sure;
        _kuklaOzel.Text = "Özel: " + ozelSure.ToString(Bicim.Tr) + " sn";
        _kuklaOzel.Checked = ozel;
        _kuklaIptal.Enabled = etkin;
        _kukla.Text = etkin ? "Kukla testi (sürüyor)" : "Kukla testi";
        _alarm.Text = alarmKalan is null ? "Alarm…" : "Alarm… (" + alarmKalan + ")";
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
            // Üç hasar çubuğu üç sınıfın açık tonunda (overlay'deki sınıf renkleriyle aynı aile)
            using var c1 = new SolidBrush(Palet.Sinif("Gladiator").Acik);
            using var c2 = new SolidBrush(Palet.Sinif("Sorcerer").Acik);
            using var c3 = new SolidBrush(Palet.Sinif("Assassin").Acik);
            // Üstte boss çubuğu, altta üç hasar çubuğu
            g.FillRectangle(kirmizi, 2 * s, 2 * s, 12 * s, 3 * s);
            g.FillRectangle(c1, 2 * s, 7 * s, 10 * s, 2 * s);
            g.FillRectangle(c2, 2 * s, 10 * s, 7 * s, 2 * s);
            g.FillRectangle(c3, 2 * s, 13 * s, 4 * s, 2 * s);
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
