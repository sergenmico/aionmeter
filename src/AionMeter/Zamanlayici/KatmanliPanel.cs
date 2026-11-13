using System.Drawing.Drawing2D;

namespace AionMeter.Zamanlayici;

/// <summary>
/// Zamanlayıcı pencerelerinin ortak tabanı: kenarlıksız, en üstte, odak asla çalınmaz (WS_EX_NOACTIVATE + SW_SHOWNOACTIVATE,
/// WM_MOUSEACTIVATE → MA_NOACTIVATE). Tıklanmaz modda fare olayları oyuna geçer (WS_EX_TRANSPARENT + HTTRANSPARENT).
/// Saydamlık katman opaklığıyla (overlay ile aynı), çizim kodla. Sürüklenebilirken sol tuşla taşınır; bırakınca <see cref="Tasindi"/>.
/// <para>Sürükleme fare mesajlarına güvenmez: pencere hiç etkinleşmediği (oyun önde kaldığı) için imleç hızla dışarı çıkınca
/// WM_MOUSEMOVE gelmeyebilir. Basılınca 15 ms'lik bir zamanlayıcı imleci ve sol tuşu yoklar; tuş bırakılınca sürükleme biter.</para>
/// </summary>
internal abstract class KatmanliPanel : Form
{
    protected const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    protected const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    protected const TextFormatFlags Sag = TextFormatFlags.Right | Temel;

    private byte _alfa = 230;
    private bool _tiklanmaz;
    private bool _surukle, _tasindiMi, _tusGoruldu;
    private Point _fareBas, _konumBas;
    private System.Windows.Forms.Timer? _surukleYoklama; // yalnız sürüklerken çalışır

    protected float Olcek { get; private set; } = 1f;

    /// <summary>Sol tuşla taşınabilir mi (geri sayım: overlay kilitsizken; boss listesi: her zaman).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Suruklenebilir { get; set; }

    /// <summary>Şu an sürükleniyor mu (denetleyici bu sırada konumu değiştirmez).</summary>
    public bool Surukleniyor => _surukle;

    /// <summary>Sürükleme bitti ve pencere yer değiştirdi.</summary>
    public event Action? Tasindi;

    protected KatmanliPanel(string baslik, float olcek)
    {
        Olcek = olcek;
        Text = baslik;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected int P(float v) => (int)MathF.Round(v * Olcek);

    protected static int Olc(string metin, Font font, bool dikey = false)
    {
        if (metin.Length == 0) return 0;
        var b = TextRenderer.MeasureText(metin, font, Size.Empty, Temel);
        return dikey ? b.Height : b.Width;
    }

    /// <summary>Ölçek (DPI × boyut) değişti: alt sınıf yazı tiplerini yeniden kurar.</summary>
    public void OlcekAyarla(float olcek)
    {
        if (olcek == Olcek) return;
        Olcek = olcek;
        YaziTipleriniKur();
        Invalidate();
    }

    protected abstract void YaziTipleriniKur();

    // ---------------- Pencere stilleri / odak ----------------

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            if (_tiklanmaz) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE; // tıklama pencereyi etkinleştirmesin; oyun odakta kalsın
                return;
            case Native.WM_NCHITTEST when _tiklanmaz:
                m.Result = (IntPtr)Native.HTTRANSPARENT;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Başka DPI'lı ekrana geçişte Windows'un önerdiği boyutu alma: boyutu ve ölçeği denetleyici belirler.</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, _alfa, Native.LWA_ALPHA);
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10: köşe yuvarlama yok, sorun değil */ }
    }

    // ---------------- Dış arayüz ----------------

    public void AlfaAyarla(byte alfa)
    {
        if (alfa == _alfa) return;
        _alfa = alfa;
        if (IsHandleCreated) Native.SetLayeredWindowAttributes(Handle, 0, _alfa, Native.LWA_ALPHA);
    }

    /// <summary>Tıklanmaz (WS_EX_TRANSPARENT) yapar ya da geri alır. Odak hiçbir durumda değişmez.</summary>
    public void TiklanmazAyarla(bool tiklanmaz)
    {
        if (_tiklanmaz == tiklanmaz) return;
        _tiklanmaz = tiklanmaz;
        if (tiklanmaz) SurukleBirak();
        if (!IsHandleCreated) return; // tutamaç yaratılınca CreateParams uygular
        long ex = Native.GetExStyle(Handle);
        Native.SetExStyle(Handle, tiklanmaz ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT);
    }

    /// <summary>Etkinleştirmeden gösterir ve en üste alır.</summary>
    public void Goster()
    {
        if (Visible) return;
        Show(); // ShowWithoutActivation => SW_SHOWNOACTIVATE
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    public void Gizle()
    {
        SurukleBirak();
        if (Visible) Hide();
    }

    /// <summary>Konum ve boyutu birlikte (tek SetWindowPos); aynıysa dokunmaz.</summary>
    public void Yerles(Rectangle r)
    {
        if (Bounds != r) Bounds = r; // WinForms SetWindowPos'u SWP_NOACTIVATE ile çağırır
    }

    // ---------------- Fare: sürükleme ----------------

    /// <summary>Bu noktadan sürükleme başlayabilir mi (ör. × düğmesi değil).</summary>
    protected virtual bool SurukleBaslar(Point p) => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_tiklanmaz || !Suruklenebilir || e.Button != MouseButtons.Left || !SurukleBaslar(e.Location)) return;
        _surukle = true;
        _tasindiMi = false;
        _tusGoruldu = false;
        _fareBas = Cursor.Position;
        _konumBas = Location;
        Capture = true;
        if (_surukleYoklama is null)
        {
            _surukleYoklama = new System.Windows.Forms.Timer { Interval = 15 };
            _surukleYoklama.Tick += (_, _) => SurukleYokla();
        }
        _surukleYoklama.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_surukle) Tasi();
        else Cursor = Suruklenebilir && !_tiklanmaz && SurukleBaslar(e.Location) ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_surukle && e.Button == MouseButtons.Left) SurukleBitir();
    }

    /// <summary>
    /// Yakalama elden gitti. Tuşun durumu okunabiliyorsa yoklama bitişi kendisi görür; okunamıyorsa (oyun yönetici olarak çalışıyor,
    /// biz değiliz: Windows tuş durumunu gizler) bırakma mesajı da gelmeyecek demektir, panel imlece yapışık kalmasın diye burada biter.
    /// </summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_surukle && !_tusGoruldu) SurukleBitir();
    }

    /// <summary>15 ms'de bir: imleci izle, sol tuş bırakıldıysa bitir (ters çevrilmiş fare tuşlarında fiziksel sağ tuş).</summary>
    private void SurukleYokla()
    {
        if (!_surukle || IsDisposed)
        {
            _surukleYoklama?.Stop();
            return;
        }
        int vk = SystemInformation.MouseButtonsSwapped ? Native.VK_RBUTTON : Native.VK_LBUTTON;
        if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0) _tusGoruldu = true;
        else if (_tusGoruldu)
        {
            SurukleBitir();
            return;
        }
        Tasi();
    }

    private void Tasi()
    {
        var p = Cursor.Position;
        int dx = p.X - _fareBas.X, dy = p.Y - _fareBas.Y;
        var hedef = new Point(_konumBas.X + dx, _konumBas.Y + dy);
        if (hedef == Location) return;
        if (dx != 0 || dy != 0) _tasindiMi = true;
        Location = hedef;
    }

    private void SurukleBitir()
    {
        if (!_surukle) return;
        Tasi(); // son konum
        bool tasindi = _tasindiMi;
        SurukleBirak();
        if (tasindi) Tasindi?.Invoke();
    }

    private void SurukleBirak()
    {
        if (!_surukle) return;
        _surukle = false;
        _tasindiMi = false;
        _surukleYoklama?.Stop();
        Capture = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surukleYoklama?.Dispose();
            _surukleYoklama = null;
        }
        base.Dispose(disposing);
    }

    // ---------------- Çizim yardımcıları ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics, ClientSize); }
        catch (Exception ex) { HataLog.Yaz(Text + " çizimi", ex); }
    }

    /// <summary>Tüm içerik (pencere göstermeden bitmap'e de çizilebilir).</summary>
    public abstract void Ciz(Graphics g, Size boyut);

    /// <summary>Satır başındaki tür noktası: etkinlik mavi, boss kırmızı.</summary>
    protected static void Nokta(Graphics g, Brush firca, int x, int ortaY, int cap)
    {
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(firca, x, ortaY - cap / 2f, cap, cap);
        g.SmoothingMode = eski;
    }
}
