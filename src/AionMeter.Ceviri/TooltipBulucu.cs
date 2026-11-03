using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AionMeter.Ceviri;

/// <summary>
/// İmleç çevresinden alınmış bir kesitte açıklama kutusunu (tooltip) bulur: koyu, pürüzsüz zeminli, üstünde açık renk
/// yazı olan dikdörtgen panel.
/// Yöntem: (1) 6×6 piksel bloklar "panel zemini" mi diye sınıflanır (koyu piksel yeterli, koyu pikseller pürüzsüz) ve
/// zemin seviyesi (koyu piksellerin medyan parlaklığı) kaydedilir. (2) Seviyesi yakın komşu bloklar bölge oluşturur
/// (farklı tondaki koyu sahne parçaları ayrı kalır). (3) Her bölgede yazı deliklerini kapatıp en büyük iç dikdörtgen
/// bulunur. (4) İçinde açık renk yazı olan, yeterince büyük ve imlece en yakın dikdörtgen seçilir; kenarlar piksel
/// düzeyinde inceltilir (varsa çerçeve çizgisi dahil edilir).
/// </summary>
public static class TooltipBulucu
{
    private const int B = 6;              // blok boyu (px)
    private const int KoyuEsik = 64;      // parlaklık < bu: zemin adayı
    private const int AcikEsik = 140;     // parlaklık > bu: yazı
    private const int SeviyeFarki = 4;    // komşu blok zemin seviyeleri en çok bu kadar farklı olabilir
    public static int EnKucukGenislik { get; set; } = 90;
    public static int EnKucukYukseklik { get; set; } = 30;
    /// <summary>İmleçten bundan uzak paneller kabul edilmez (px).</summary>
    public static int AzamiUzaklik { get; set; } = 140;

    /// <summary>Tanı (Check için): null değilse ret nedenleri yazılır.</summary>
    internal static List<string>? Tani;
    internal static (bool[] Maske, int Bw, int Bh) TaniMaske;

    public static Rectangle? Bul(Bitmap imlecCevresi, Point imlecGoreli)
    {
        int w = imlecCevresi.Width, h = imlecCevresi.Height;
        if (w < EnKucukGenislik || h < EnKucukYukseklik) return null;
        var y = Parlaklik(imlecCevresi);

        int bw = w / B, bh = h / B;
        var seviye = new int[bw * bh]; // -1 = panel değil
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
                seviye[by * bw + bx] = PanelSeviyesi(y, w, bx * B, by * B);
        if (Tani != null) TaniMaske = (seviye.Select(s => s >= 0).ToArray(), bw, bh);

        var etiket = new int[bw * bh];
        var adaylar = new List<(Rectangle Kutu, double Puan)>();
        var yigin = new Stack<int>();
        var uyeler = new List<int>();
        int sonEtiket = 0;
        for (int i = 0; i < seviye.Length; i++)
        {
            if (seviye[i] < 0 || etiket[i] != 0) continue;
            sonEtiket++;
            uyeler.Clear();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            yigin.Push(i); etiket[i] = sonEtiket;
            while (yigin.Count > 0)
            {
                int j = yigin.Pop(); uyeler.Add(j);
                int jx = j % bw, jy = j / bw;
                x0 = Math.Min(x0, jx); x1 = Math.Max(x1, jx); y0 = Math.Min(y0, jy); y1 = Math.Max(y1, jy);
                if (jx > 0) Dene(j, j - 1);
                if (jx < bw - 1) Dene(j, j + 1);
                if (jy > 0) Dene(j, j - bw);
                if (jy < bh - 1) Dene(j, j + bw);
            }
            if ((x1 - x0 + 1) * B < EnKucukGenislik || (y1 - y0 + 1) * B < EnKucukYukseklik) continue;

            // Bölge maskesi (1 blok kenar payı) → yazı deliklerini kapat → en büyük iç dikdörtgen.
            int mw = x1 - x0 + 3, mh = y1 - y0 + 3;
            var maske = new bool[mw * mh];
            foreach (int j in uyeler) maske[(j / bw - y0 + 1) * mw + (j % bw - x0 + 1)] = true;
            maske = Daralt(Genislet(maske, mw, mh), mw, mh);
            var (rx, ry, rw, rh) = EnBuyukDikdortgen(maske, mw, mh);
            var kutu = new Rectangle((rx + x0 - 1) * B, (ry + y0 - 1) * B, rw * B, rh * B);
            if (kutu.Width < EnKucukGenislik || kutu.Height < EnKucukYukseklik) { Tani?.Add($"{kutu} küçük"); continue; }
            if ((double)kutu.Width * kutu.Height > 0.6 * w * h) { Tani?.Add($"{kutu} çok büyük"); continue; }
            if (!YaziVar(y, w, kutu)) { Tani?.Add($"{kutu} yazı yok"); continue; }
            double uzak = Uzaklik(kutu, imlecGoreli);
            if (uzak > AzamiUzaklik) { Tani?.Add($"{kutu} uzak {uzak:F0}"); continue; }
            // İmlecin İÇİNDE olduğu panel (ör. çanta penceresi) genelde tooltip değildir: ceza.
            double ceza = kutu.Contains(imlecGoreli) ? 60 : 0;
            adaylar.Add((kutu, -(uzak + ceza)));
            Tani?.Add($"{kutu} aday puan {-(uzak + ceza):F0}");
        }
        if (adaylar.Count == 0) return null;
        return Incelt(y, w, h, adaylar.MaxBy(a => a.Puan).Kutu);

        void Dene(int j, int k)
        {
            if (seviye[k] >= 0 && etiket[k] == 0 && Math.Abs(seviye[k] - seviye[j]) <= SeviyeFarki)
            {
                etiket[k] = sonEtiket; yigin.Push(k);
            }
        }
    }

    /// <summary>İkili maskede tamamen dolu en büyük dikdörtgen (histogram yöntemi). (x, y, genişlik, yükseklik) blok cinsinden.</summary>
    private static (int X, int Y, int W, int H) EnBuyukDikdortgen(bool[] m, int mw, int mh)
    {
        var yuk = new int[mw];
        var yigin = new Stack<int>();
        (int X, int Y, int W, int H) enIyi = (0, 0, 0, 0);
        int enIyiAlan = 0;
        for (int yy = 0; yy < mh; yy++)
        {
            for (int x = 0; x < mw; x++) yuk[x] = m[yy * mw + x] ? yuk[x] + 1 : 0;
            yigin.Clear();
            for (int x = 0; x <= mw; x++)
            {
                int hh = x == mw ? 0 : yuk[x];
                while (yigin.Count > 0 && yuk[yigin.Peek()] >= hh)
                {
                    int tepe = yuk[yigin.Pop()];
                    int sol = yigin.Count == 0 ? 0 : yigin.Peek() + 1;
                    int alan = tepe * (x - sol);
                    if (alan > enIyiAlan) { enIyiAlan = alan; enIyi = (sol, yy - tepe + 1, x - sol, tepe); }
                }
                yigin.Push(x);
            }
        }
        return enIyi;
    }

    /// <summary>Kutu kenarlarını piksel düzeyinde ayarla: koyu oranı ≥ %50 olan en dış çizgi + varsa düz renkli çerçeve.</summary>
    private static Rectangle Incelt(byte[] y, int w, int h, Rectangle k)
    {
        int sol = Math.Max(0, k.Left - B), sag = Math.Min(w - 1, k.Right + B - 1);
        int ust = Math.Max(0, k.Top - B), alt = Math.Min(h - 1, k.Bottom + B - 1);
        int ortaX0 = k.Left + k.Width / 4, ortaX1 = k.Right - k.Width / 4;  // köşe yuvarlaklığından etkilenmesin
        int ortaY0 = k.Top + k.Height / 4, ortaY1 = k.Bottom - k.Height / 4;

        double SatirKoyu(int yy) { int c = 0; for (int x = ortaX0; x < ortaX1; x++) if (y[yy * w + x] < KoyuEsik) c++; return c / (double)Math.Max(1, ortaX1 - ortaX0); }
        double SutunKoyu(int xx) { int c = 0; for (int yy = ortaY0; yy < ortaY1; yy++) if (y[yy * w + xx] < KoyuEsik) c++; return c / (double)Math.Max(1, ortaY1 - ortaY0); }

        while (ust < k.Top + B && SatirKoyu(ust) < 0.5) ust++;
        while (alt > k.Bottom - B && SatirKoyu(alt) < 0.5) alt--;
        while (sol < k.Left + B && SutunKoyu(sol) < 0.5) sol++;
        while (sag > k.Right - B && SutunKoyu(sag) < 0.5) sag--;

        // Bloklardan bulunan dikdörtgen panelin sadece bir parçası olabilir (sık yazılı gövde, soldaki ikon, ayraç çizgisi
        // blokları bozar; ör. sadece başlık bandı). Bu yüzden kenarlar satır/sütun düzeyinde DIŞA doğru büyütülür:
        // çizgi panel zemini gibiyse (bkz. CizgiPanel) kenar oraya taşınır; 3 piksele kadar uymayan çizgi atlanabilir
        // (ince ayraç çizgisi, ikonun kenarı). Ton farklı ya da sahne gibi pürüzlü çizgide durur.
        int seviye = ZeminSeviyesi(y, w, Rectangle.FromLTRB(sol, ust, sag + 1, alt + 1));
        bool PanelSatiri(int yy) => CizgiPanel(Enumerable.Range(sol, sag - sol + 1).Select(x => y[yy * w + x]), seviye);
        bool PanelSutunu(int xx) => CizgiPanel(Enumerable.Range(ust, alt - ust + 1).Select(yy => y[yy * w + xx]), seviye);
        static int Ilerle(int kenar, int yon, int sinir, Func<int, bool> panel)
        {
            while (true)
            {
                int bulunan = -1;
                for (int d = 1; d <= 4; d++)
                {
                    int c = kenar + yon * d;
                    if (c < 0 || c >= sinir) break;
                    if (panel(c)) { bulunan = c; break; }
                }
                if (bulunan < 0) return kenar;
                kenar = bulunan;
            }
        }
        for (int tur = 0; tur < 4; tur++)
        {
            int u = Ilerle(ust, -1, h, PanelSatiri), a = Ilerle(alt, 1, h, PanelSatiri);
            ust = u; alt = a;
            int s0 = Ilerle(sol, -1, w, PanelSutunu), s1 = Ilerle(sag, 1, w, PanelSutunu);
            bool degisti = s0 != sol || s1 != sag;
            sol = s0; sag = s1;
            if (!degisti) break;
        }

        // Çerçeve: hemen dışındaki 1–3 piksellik çizgi düz renkliyse (düşük sapma) kutuya kat.
        bool DuzSatir(int yy) => yy >= 0 && yy < h && Sapma(Enumerable.Range(ortaX0, ortaX1 - ortaX0).Select(x => y[yy * w + x])) < 14;
        bool DuzSutun(int xx) => xx >= 0 && xx < w && Sapma(Enumerable.Range(ortaY0, ortaY1 - ortaY0).Select(yy => y[yy * w + xx])) < 14;
        for (int i = 0; i < 3 && DuzSatir(ust - 1); i++) ust--;
        for (int i = 0; i < 3 && DuzSatir(alt + 1); i++) alt++;
        for (int i = 0; i < 3 && DuzSutun(sol - 1); i++) sol--;
        for (int i = 0; i < 3 && DuzSutun(sag + 1); i++) sag++;
        return Rectangle.FromLTRB(sol, ust, sag + 1, alt + 1);
    }

    /// <summary>
    /// Piksel çizgisi panel zemini gibi mi: en az %30'u koyu (gerisi yazı/ikon olabilir), koyu piksellerin en az %75'i
    /// panel tonunun ±8 yakınında (karanlık sahnede farklı tonlu parçalar elenir) ve koyu komşular arası farkın medyanı ≤ 2.
    /// </summary>
    private static bool CizgiPanel(IEnumerable<byte> cizgi, int seviye)
    {
        Span<int> fark = stackalloc int[256];
        int n = 0, koyu = 0, yakin = 0, cift = 0, onceki = -1;
        foreach (var v in cizgi)
        {
            n++;
            if (v < KoyuEsik)
            {
                koyu++;
                if (Math.Abs(v - seviye) <= 8) yakin++;
                if (onceki >= 0) { fark[Math.Abs(v - onceki)]++; cift++; }
                onceki = v;
            }
            else onceki = -1;
        }
        return n > 0 && koyu >= n * 0.3 && yakin >= koyu * 0.75 && cift > 0 && Medyan(fark, cift) <= 2;
    }

    /// <summary>Kutudaki koyu piksellerin medyan parlaklığı.</summary>
    private static int ZeminSeviyesi(byte[] y, int w, Rectangle k)
    {
        Span<int> deger = stackalloc int[KoyuEsik];
        int koyu = 0;
        for (int yy = k.Top; yy < k.Bottom; yy += 2)
            for (int x = k.Left; x < k.Right; x += 2) { int v = y[yy * w + x]; if (v < KoyuEsik) { deger[v]++; koyu++; } }
        return koyu == 0 ? 0 : Medyan(deger, koyu);
    }

    private static double Sapma(IEnumerable<byte> d)
    {
        double t = 0, t2 = 0; int n = 0;
        foreach (var v in d) { t += v; t2 += v * v; n++; }
        if (n == 0) return 999;
        double o = t / n;
        return Math.Sqrt(Math.Max(0, t2 / n - o * o));
    }

    /// <summary>
    /// Blok panel zemini ise zemin seviyesini (koyu piksellerin medyanı), değilse -1 döner. Zemin: koyu piksel oranı
    /// yeterli ve koyu pikseller pürüzsüz (komşu koyu pikseller arası farkın medyanı küçük). Yarı saydam panelin altından
    /// sızan sahne ~%10'a iner; gürültülü sahne pürüzlüdür. Yazı pikselleri koyu olmadığından ölçüme girmez.
    /// </summary>
    internal static int PanelSeviyesi(byte[] y, int w, int x0, int y0)
    {
        Span<int> fark = stackalloc int[256];
        Span<int> deger = stackalloc int[KoyuEsik];
        int koyu = 0, cift = 0;
        for (int yy = y0; yy < y0 + B; yy++)
        {
            int satir = yy * w;
            for (int x = x0; x < x0 + B; x++)
            {
                int v = y[satir + x];
                if (v >= KoyuEsik) continue;
                koyu++; deger[v]++;
                if (x + 1 < x0 + B && y[satir + x + 1] < KoyuEsik) { fark[Math.Abs(v - y[satir + x + 1])]++; cift++; }
                if (yy + 1 < y0 + B && y[satir + w + x] < KoyuEsik) { fark[Math.Abs(v - y[satir + w + x])]++; cift++; }
            }
        }
        if (koyu < B * B * 0.35 || cift < 6) return -1;
        if (Medyan(fark, cift) > PuruzEsik) return -1;
        return Medyan(deger, koyu);
    }

    private static int Medyan(Span<int> hist, int n)
    {
        int yari = n / 2, top = 0;
        for (int i = 0; i < hist.Length; i++) { top += hist[i]; if (top > yari) return i; }
        return hist.Length - 1;
    }

    /// <summary>Komşu koyu pikseller arası farkın medyanı en çok bu kadar olabilir (parlaklık birimi).</summary>
    internal static int PuruzEsik { get; set; } = 3;

    private static bool YaziVar(byte[] y, int w, Rectangle k)
    {
        int acik = 0, n = 0;
        for (int yy = k.Top; yy < k.Bottom; yy += 2)
            for (int x = k.Left; x < k.Right; x += 2) { n++; if (y[yy * w + x] > AcikEsik) acik++; }
        double oran = acik / (double)Math.Max(1, n);
        return oran > 0.01 && oran < 0.45;
    }

    private static double Uzaklik(Rectangle r, Point p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool[] Genislet(bool[] m, int bw, int bh)
    {
        var r = new bool[m.Length];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                if (!m[y * bw + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < bw && yy < bh) r[yy * bw + xx] = true;
                    }
            }
        return r;
    }

    private static bool[] Daralt(bool[] m, int bw, int bh)
    {
        var r = new bool[m.Length];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                bool hepsi = true;
                for (int dy = -1; dy <= 1 && hepsi; dy++)
                    for (int dx = -1; dx <= 1 && hepsi; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        hepsi = xx >= 0 && yy >= 0 && xx < bw && yy < bh && m[yy * bw + xx];
                    }
                r[y * bw + x] = hepsi;
            }
        return r;
    }

    /// <summary>Gri tonlama (ITU-R BT.601) bayt dizisi.</summary>
    private static byte[] Parlaklik(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        Bitmap kaynak = bmp.PixelFormat == PixelFormat.Format32bppArgb || bmp.PixelFormat == PixelFormat.Format32bppRgb
            ? bmp : bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        try
        {
            var v = kaynak.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, kaynak.PixelFormat);
            try
            {
                var satir = new byte[w * 4];
                var y = new byte[w * h];
                for (int yy = 0; yy < h; yy++)
                {
                    Marshal.Copy(v.Scan0 + yy * v.Stride, satir, 0, satir.Length);
                    for (int x = 0; x < w; x++)
                        y[yy * w + x] = (byte)((satir[x * 4] * 29 + satir[x * 4 + 1] * 150 + satir[x * 4 + 2] * 77) >> 8);
                }
                return y;
            }
            finally { kaynak.UnlockBits(v); }
        }
        finally { if (!ReferenceEquals(kaynak, bmp)) kaynak.Dispose(); }
    }
}
