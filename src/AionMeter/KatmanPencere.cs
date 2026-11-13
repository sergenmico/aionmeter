using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AionMeter;

/// <summary>
/// Piksel başına saydam (UpdateLayeredWindow) küçük pencereler için ortak taban: kenarlıksız, en üstte, görev çubuğunda yok,
/// odak çalmaz (WS_EX_NOACTIVATE + WM_MOUSEACTIVATE → MA_NOACTIVATE). İstenirse her zaman tıklanmaz (WS_EX_TRANSPARENT).
/// WM_PAINT ile çizilmez: içerik <see cref="Bas"/> ile bir kez bitmap'e çizilip pencereye verilir, bitmap hemen bırakılır
/// (pencere kendi kopyasını tutar). Opaklık değişimi yüzeyi yeniden çizmez (<see cref="AlfaAyarla"/>).
/// Gösterme/gizleme sadece ShowWindow ile (SW_SHOWNOACTIVATE / SW_HIDE); WinForms Show() kullanılmaz.
/// </summary>
internal abstract class KatmanPencere : Form
{
    private readonly bool _tiklanmaz;
    private byte _alfa = 255;
    private bool _basildi; // en az bir kez içerik verildi (opaklık tek başına güncellenebilir)

    /// <summary>Pencere şu an gösteriliyor mu (SW_SHOWNOACTIVATE sonrası, SW_HIDE öncesi).</summary>
    public bool Acik { get; private set; }

    /// <summary>Son verilen içeriğin ekran dikdörtgeni (fiziksel px).</summary>
    public Rectangle Alan { get; private set; }

    protected KatmanPencere(bool tiklanmaz, string baslik)
    {
        _tiklanmaz = tiklanmaz;
        Text = baslik;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            if (_tiklanmaz) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            else cp.ExStyle &= ~Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE; // tıklama pencereyi etkinleştirmez: oyun odakta kalır
                return;
            case Native.WM_NCHITTEST when _tiklanmaz:
                m.Result = (IntPtr)Native.HTTRANSPARENT;
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Tutamacı (gerekirse) yaratır; pencere görünmez kalır.</summary>
    protected void TutamacHazirla()
    {
        if (!IsHandleCreated) CreateHandle();
    }

    /// <summary>
    /// İçeriği <paramref name="alan"/> boyutunda saydam bir bitmap'e çizdirir ve pencereyi o konuma/boyuta getirir.
    /// Çizim önceden çarpılmış (premultiplied) 32 bit yüzeye GDI+ ile yapılır; TextRenderer (GDI) kullanılmamalı (alfa bozulur).
    /// </summary>
    protected void Bas(Rectangle alan, Action<Graphics> ciz)
    {
        if (alan.Width <= 0 || alan.Height <= 0) return;
        TutamacHazirla();
        IntPtr ekranDc = GetDC(IntPtr.Zero);
        IntPtr bellekDc = IntPtr.Zero, dib = IntPtr.Zero, eski = IntPtr.Zero;
        try
        {
            bellekDc = CreateCompatibleDC(ekranDc);
            var bi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = alan.Width,
                biHeight = -alan.Height, // yukarıdan aşağı
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = CreateDIBSection(bellekDc, ref bi, 0, out IntPtr bitler, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bitler == IntPtr.Zero) return;
            using (var bmp = new Bitmap(alan.Width, alan.Height, alan.Width * 4, PixelFormat.Format32bppPArgb, bitler))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                ciz(g);
            }
            eski = SelectObject(bellekDc, dib);
            var konum = new POINT { X = alan.X, Y = alan.Y };
            var boyut = new SIZE { cx = alan.Width, cy = alan.Height };
            var kaynak = new POINT();
            var karisim = Karisim(_alfa);
            UpdateLayeredWindow(Handle, ekranDc, ref konum, ref boyut, bellekDc, ref kaynak, 0, ref karisim, ULW_ALPHA);
            _basildi = true;
            Alan = alan;
        }
        finally
        {
            if (eski != IntPtr.Zero) SelectObject(bellekDc, eski);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (bellekDc != IntPtr.Zero) DeleteDC(bellekDc);
            ReleaseDC(IntPtr.Zero, ekranDc);
        }
    }

    /// <summary>Pencerenin genel opaklığı (0..255). Yüzey yeniden çizilmez.</summary>
    protected void AlfaAyarla(byte alfa)
    {
        if (alfa == _alfa) return;
        _alfa = alfa;
        if (!IsHandleCreated || !_basildi) return;
        var karisim = Karisim(alfa);
        UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref karisim, ULW_ALPHA);
    }

    protected byte Alfa => _alfa;

    /// <summary>Boyut aynı kalırken yalnızca taşır (yüzey yeniden çizilmez).</summary>
    protected void Tasi(Point konum)
    {
        if (!IsHandleCreated || !_basildi || Alan.Location == konum) return;
        Alan = new Rectangle(konum, Alan.Size);
        Native.SetWindowPos(Handle, IntPtr.Zero, konum.X, konum.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Etkinleştirmeden gösterir ve en üste alır. İçerik yoksa hiçbir şey yapmaz.</summary>
    public void PencereGoster()
    {
        if (!IsHandleCreated || !_basildi) return;
        if (!Acik)
        {
            Acik = true;
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        }
        EnUsteAl();
    }

    public void PencereGizle()
    {
        if (!Acik) return;
        Acik = false;
        if (IsHandleCreated) Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    /// <summary>En üstte kalmayı yeniler (etkinleştirmeden).</summary>
    public void EnUsteAl()
    {
        if (!Acik || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    // ---------------- Win32 (sadece bu taban kullanır) ----------------

    private const uint ULW_ALPHA = 0x2;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    private static BLENDFUNCTION Karisim(byte alfa) =>
        new() { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alfa, AlphaFormat = AC_SRC_ALPHA };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    /// <summary>Sadece opaklık: konum, boyut ve yüzey değişmez (hepsi NULL).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize,
        IntPtr hdcSrc, IntPtr pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits,
        IntPtr hSection, uint offset);
}
