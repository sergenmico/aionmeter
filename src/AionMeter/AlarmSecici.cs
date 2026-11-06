using System.Drawing.Drawing2D;
using AionMeter.Cevirmen;

namespace AionMeter;

/// <summary>
/// Overlay'in alt satırındaki saatten (kilitliyken Alt+tık) açılan küçük alarm seçici. Kodla çizilir; odak almaz
/// (WS_EX_NOACTIVATE + MA_NOACTIVATE): oyun önde kalır, tıklamalar yine çalışır. Dışarı tıklanınca kapanır
/// (sadece açıkken 50 ms'de bir fare düğmesi yoklanır).
/// </summary>
internal sealed class AlarmSecici : Form
{
    private static readonly int[] Hazir = { 1, 3, 5, 10, 15, 30 };

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | Temel;

    private readonly float _olcek;
    private readonly Func<string?> _kalan; // çalışan alarmın kalan süresi ("04:32"), yoksa null
    private readonly Font _yaziBaslik, _yazi, _yaziKucuk, _yaziDeger;
    private readonly SolidBrush _fircaKutu = new(Palet.PanelUst);
    private readonly SolidBrush _fircaUstunde = new(Color.FromArgb(48, 54, 66));
    private readonly SolidBrush _fircaSecili = new(Color.FromArgb(40, 66, 104));
    private readonly SolidBrush _fircaVurgu = new(Color.FromArgb(52, 112, 196));     // düğme zemini: üstündeki beyaz yazı ≥ 4,5:1
    private readonly SolidBrush _fircaVurguAcik = new(Color.FromArgb(64, 128, 214));
    private readonly Pen _kalemKenar = new(Palet.Cizgi);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly System.Windows.Forms.Timer _disTik;

    private readonly Rectangle[] _hazirKutu = new Rectangle[Hazir.Length];
    private Rectangle _eksi, _arti, _deger, _baslat, _iptal, _kapat;
    private int _dk;
    private int _ustunde = -1; // 0..5 hazır, 10 eksi, 11 artı, 12 başlat, 13 iptal, 14 kapat
    private bool _calisiyor;

    public event Action<int>? Baslatildi;
    public event Action? IptalEdildi;

    public AlarmSecici(float olcek, int dk, Func<string?> kalan)
    {
        _olcek = olcek;
        _dk = Math.Clamp(dk, 1, Ayarlar.EnFazlaAlarmDk);
        _kalan = kalan;
        _calisiyor = kalan() is not null;

        Text = "AionMeter alarm";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _yaziBaslik = Palet.YaziTipi(12.5f * olcek, yariKalin: true);
        _yazi = Palet.YaziTipi(12f * olcek);
        _yaziKucuk = Palet.YaziTipi(11f * olcek);
        _yaziDeger = Palet.YaziTipi(15f * olcek, yariKalin: true);
        _kalemKapat.Width = Math.Max(1f, 1.5f * olcek);

        _disTik = new System.Windows.Forms.Timer { Interval = 50 };
        _disTik.Tick += (_, _) => DisariYokla();
        ClientSize = Yerlesim();
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);

    /// <summary>Kalan süre değişti (overlay her saniye çağırır).</summary>
    public void Yenile()
    {
        bool c = _kalan() is not null;
        if (c != _calisiyor)
        {
            _calisiyor = c;
            ClientSize = Yerlesim();
        }
        Invalidate();
    }

    private Size Yerlesim()
    {
        int pad = P(12), w = P(232);
        int x0 = pad, x1 = w - pad;
        int y = 0;
        int k = P(16);
        _kapat = new Rectangle(w - P(10) - k, (P(36) - k) / 2, k, k);
        y += P(36);

        // 3 × 2 hazır süre
        int ara = P(6), dw = (x1 - x0 - 2 * ara) / 3, dh = P(28);
        for (int i = 0; i < Hazir.Length; i++)
        {
            int sutun = i % 3, satir = i / 3;
            _hazirKutu[i] = new Rectangle(x0 + sutun * (dw + ara), y + satir * (dh + ara), dw, dh);
        }
        y += 2 * dh + ara + P(10);

        // −1 · 05:00 dk · +1
        int kw = P(40);
        _eksi = new Rectangle(x0, y, kw, dh);
        _arti = new Rectangle(x1 - kw, y, kw, dh);
        _deger = Rectangle.FromLTRB(_eksi.Right, y, _arti.Left, y + dh);
        y += dh + P(10);

        if (_calisiyor)
        {
            int yarim = (x1 - x0 - ara) / 2;
            _iptal = new Rectangle(x0, y, yarim, P(30));
            _baslat = new Rectangle(x1 - yarim, y, yarim, P(30));
        }
        else
        {
            _iptal = Rectangle.Empty;
            _baslat = new Rectangle(x0, y, x1 - x0, P(30));
        }
        y += P(30) + pad;
        return new Size(w, y);
    }

    // ---------------- Pencere stilleri / odak ----------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
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
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10 */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _disTik.Start();
    }

    /// <summary>Overlay'in yanına yerleşir: altına sığarsa alta, değilse üstüne; ekran dışına taşmaz.</summary>
    public void Yerlestir(Rectangle overlay)
    {
        var alan = Screen.FromRectangle(overlay).WorkingArea;
        int ara = P(4);
        int x = Math.Clamp(overlay.Right - Width, alan.Left, Math.Max(alan.Left, alan.Right - Width));
        int y = overlay.Bottom + ara + Height <= alan.Bottom ? overlay.Bottom + ara : Math.Max(alan.Top, overlay.Top - ara - Height);
        Location = new Point(x, y);
    }

    private bool _basiliydi = true; // açan tık hâlâ basılı olabilir: önce bırakılması beklenir

    private void DisariYokla()
    {
        bool basili = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 ||
                      (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
        if (basili && !_basiliydi && !Bounds.Contains(Cursor.Position)) Close();
        _basiliydi = basili;
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Alarm seçici çizimi", ex); }
    }

    private void Ciz(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height, pad = P(12);
        string? kalan = _kalan();

        TextRenderer.DrawText(g, "Alarm", _yaziBaslik, new Rectangle(pad, 0, w / 2, P(36)), Palet.Yazi, Sol);
        if (kalan is not null)
        {
            int kw = TextRenderer.MeasureText(g, "Kalan " + kalan, _yaziKucuk, Size.Empty, Temel).Width;
            TextRenderer.DrawText(g, "Kalan " + kalan, _yaziKucuk, new Rectangle(_kapat.Left - P(8) - kw, 0, kw, P(36)), Palet.Vurgu, Sol);
        }
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int ki = P(4);
        if (_ustunde == 14) Cizim.YuvarlakDoldur(g, _fircaUstunde, _kapat, P(3));
        g.DrawLine(_kalemKapat, _kapat.Left + ki, _kapat.Top + ki, _kapat.Right - ki, _kapat.Bottom - ki);
        g.DrawLine(_kalemKapat, _kapat.Right - ki, _kapat.Top + ki, _kapat.Left + ki, _kapat.Bottom - ki);
        g.SmoothingMode = eski;

        for (int i = 0; i < Hazir.Length; i++)
        {
            bool secili = Hazir[i] == _dk;
            var firca = secili ? _fircaSecili : _ustunde == i ? _fircaUstunde : _fircaKutu;
            Cizim.YuvarlakDoldur(g, firca, _hazirKutu[i], P(5));
            TextRenderer.DrawText(g, Hazir[i].ToString(Bicim.Tr) + " dk", _yazi, _hazirKutu[i], secili ? Palet.Dps : Palet.Yazi, Orta);
        }

        Cizim.YuvarlakDoldur(g, _ustunde == 10 ? _fircaUstunde : _fircaKutu, _eksi, P(5));
        Cizim.YuvarlakDoldur(g, _ustunde == 11 ? _fircaUstunde : _fircaKutu, _arti, P(5));
        TextRenderer.DrawText(g, "−1", _yazi, _eksi, _dk > 1 ? Palet.Yazi : Palet.CokSoluk, Orta);
        TextRenderer.DrawText(g, "+1", _yazi, _arti, _dk < Ayarlar.EnFazlaAlarmDk ? Palet.Yazi : Palet.CokSoluk, Orta);
        TextRenderer.DrawText(g, _dk.ToString(Bicim.Tr) + " dk", _yaziDeger, _deger, Palet.Dps, Orta);

        if (!_iptal.IsEmpty)
        {
            Cizim.YuvarlakDoldur(g, _ustunde == 13 ? _fircaUstunde : _fircaKutu, _iptal, P(5));
            TextRenderer.DrawText(g, "Alarmı kapat", _yaziKucuk, _iptal, Palet.Yazi, Orta);
        }
        Cizim.YuvarlakDoldur(g, _ustunde == 12 ? _fircaVurguAcik : _fircaVurgu, _baslat, P(5));
        TextRenderer.DrawText(g, _iptal.IsEmpty ? "Başlat" : "Yeniden kur", _iptal.IsEmpty ? _yazi : _yaziKucuk, _baslat, Palet.Dps, Orta);

        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare ----------------

    private int Bul(Point p)
    {
        for (int i = 0; i < _hazirKutu.Length; i++) if (_hazirKutu[i].Contains(p)) return i;
        if (_eksi.Contains(p)) return 10;
        if (_arti.Contains(p)) return 11;
        if (_baslat.Contains(p)) return 12;
        if (_iptal.Contains(p)) return 13;
        if (_kapat.Contains(p)) return 14;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Bul(e.Location);
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i == _ustunde) return;
        _ustunde = i;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_ustunde < 0) return;
        _ustunde = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int i = Bul(e.Location);
        switch (i)
        {
            case >= 0 and < 6: _dk = Hazir[i]; break;
            case 10: _dk = Math.Max(1, _dk - 1); break;
            case 11: _dk = Math.Min(Ayarlar.EnFazlaAlarmDk, _dk + 1); break;
            case 12: Baslatildi?.Invoke(_dk); Close(); return;
            case 13: IptalEdildi?.Invoke(); Close(); return;
            case 14: Close(); return;
            default: return;
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disTik.Dispose();
            _yaziBaslik.Dispose(); _yazi.Dispose(); _yaziKucuk.Dispose(); _yaziDeger.Dispose();
            _fircaKutu.Dispose(); _fircaUstunde.Dispose(); _fircaSecili.Dispose(); _fircaVurgu.Dispose(); _fircaVurguAcik.Dispose();
            _kalemKenar.Dispose(); _kalemKapat.Dispose();
        }
        base.Dispose(disposing);
    }
}
