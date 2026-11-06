using System.Runtime.InteropServices;

namespace AionMeter;

/// <summary>
/// Sayı yazısı: rakamlar sabit genişlikte (tabular) çizilir, akan sayı sayaç gibi değişirken metin yana kaymaz.
/// Bahnschrift'in rakamları orantılıdır ("1" dar), bu yüzden metin GDI ExtTextOut ile karakter başına verilen ilerlemeyle
/// (lpDx) tek çağrıda çizilir: her rakam en geniş rakamın ilerlemesini alır, diğer karakterler kendi genişliğini.
/// Ölçüm GDI çağrısı olmadan önbellekten yapılır (karede ölçmek bedava); çizim ayırma yapmaz.
/// </summary>
internal sealed class RakamYazisi : IDisposable
{
    public Font Font { get; }
    public int Yukseklik { get; }

    private readonly IntPtr _hfont;
    private readonly int[] _ilerleme = new int[0x7F - 0x20]; // ' '..'~'
    private readonly Dictionary<char, int> _diger = new();  // —, Türkçe harf vb. (nadir)
    private readonly int _rakam;
    private readonly int[] _dx = new int[64];

    public RakamYazisi(Font font)
    {
        Font = font;
        _hfont = font.ToHfont();
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr eski = SelectObject(dc, _hfont);
        try
        {
            GetCharWidth32W(dc, 0x20, 0x7E, _ilerleme);
            GetTextMetricsW(dc, out TEXTMETRICW tm);
            Yukseklik = tm.tmHeight;
            int r = 0;
            for (char c = '0'; c <= '9'; c++) r = Math.Max(r, _ilerleme[c - 0x20]);
            _rakam = r;
        }
        finally
        {
            SelectObject(dc, eski);
            DeleteDC(dc);
        }
    }

    private int Ilerleme(char c)
    {
        if (c is >= '0' and <= '9') return _rakam;
        if (c is >= ' ' and <= '~') return _ilerleme[c - 0x20];
        if (_diger.TryGetValue(c, out int w)) return w;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr eski = SelectObject(dc, _hfont);
        try
        {
            GetTextExtentPoint32W(dc, c, 1, out SIZE s);
            w = s.cx;
        }
        finally
        {
            SelectObject(dc, eski);
            DeleteDC(dc);
        }
        _diger[c] = w;
        return w;
    }

    /// <summary>Metnin çizim genişliği (rakamlar sabit genişlikte).</summary>
    public int Genislik(ReadOnlySpan<char> s)
    {
        int w = 0;
        foreach (char c in s) w += Ilerleme(c);
        return w;
    }

    public int Genislik(string? s) => s is null ? 0 : Genislik(s.AsSpan());

    /// <summary>Dikeyde ortalı, sağa ya da sola hizalı çizer (TextRenderer VerticalCenter + Right/Left ile aynı yerleşim).
    /// Kutudan taşan metin sola doğru taşar ama kesilmez (sütun genişlikleri en genişe göre tutulur, taşma olmaz).</summary>
    public void Ciz(Graphics g, ReadOnlySpan<char> s, Rectangle r, Color renk, bool sag)
    {
        if (s.IsEmpty || r.Width <= 0) return;
        IntPtr hdc = Ac(g);
        try { Ciz(hdc, s, r, renk, sag); }
        finally { Kapat(g, hdc); }
    }

    public void Ciz(Graphics g, string? s, Rectangle r, Color renk, bool sag)
    {
        if (s is not null) Ciz(g, s.AsSpan(), r, renk, sag);
    }

    /// <summary>Toplu çizim: bir satırın sayıları tek HDC oturumunda (GetHdc/ReleaseHdc GDI+ tamponunu boşaltır, pahalıdır).
    /// Ac ile Kapat arasında Graphics'e başka bir şey çizilmez.</summary>
    public static IntPtr Ac(Graphics g)
    {
        IntPtr hdc = g.GetHdc();
        SetBkMode(hdc, TRANSPARENT);
        return hdc;
    }

    public static void Kapat(Graphics g, IntPtr hdc) => g.ReleaseHdc(hdc);

    public void Ciz(IntPtr hdc, ReadOnlySpan<char> s, Rectangle r, Color renk, bool sag)
    {
        if (s.IsEmpty || r.Width <= 0) return;
        int n = Math.Min(s.Length, _dx.Length);
        int bas = 0;
        for (int i = 0; i < n; i++)
        {
            char c = s[i];
            if (c is >= '0' and <= '9')
            {
                // Dar rakam ("1") sabit hücrenin ortasına: artan payın yarısı önceki karakterin ilerlemesine (ya da başa) eklenir.
                int dogal = _ilerleme[c - 0x20], fazla = _rakam - dogal, sol = fazla / 2;
                if (i > 0) _dx[i - 1] += sol; else bas = sol;
                _dx[i] = dogal + fazla - sol;
            }
            else _dx[i] = Ilerleme(c);
        }
        int w = bas; // toplam = Genislik(s)
        for (int i = 0; i < n; i++) w += _dx[i];
        int x = (sag ? r.Right - w : r.Left) + bas;
        int y = r.Top + (r.Height - Yukseklik) / 2;
        IntPtr eski = SelectObject(hdc, _hfont);
        SetTextColor(hdc, renk.R | (renk.G << 8) | (renk.B << 16));
        ExtTextOutW(hdc, x, y, 0, IntPtr.Zero, ref MemoryMarshal.GetReference(s), n, _dx);
        SelectObject(hdc, eski);
    }

    public void Ciz(IntPtr hdc, string? s, Rectangle r, Color renk, bool sag)
    {
        if (s is not null) Ciz(hdc, s.AsSpan(), r, renk, sag);
    }

    public void Dispose()
    {
        DeleteObject(_hfont);
        Font.Dispose();
    }

    // ---- gdi32 ----
    private const int TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
                   tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int renk);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetCharWidth32W(IntPtr hdc, uint ilk, uint son, [Out] int[] genislik);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc, in char metin, int n, out SIZE s);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint secenek, IntPtr kutu, ref char metin, int n, int[] dx);
}
