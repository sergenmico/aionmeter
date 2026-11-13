using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace AionMeter;

/// <summary>
/// Küp afişi ve çipinin ortak yazı düzeni. Mesafe sabit genişlikte bir alana sağa yaslı yazılır: "m" hep aynı yerde durur,
/// 5 m ile 388 m arasında yazı kaymaz. "~" (yer tahmini) rakamların hemen solunda, kendi ayrılmış yerinde.
/// Bütün parçalar aynı taban çizgisine oturur (her biri ayrı ortalanırsa "~" ve "m" zıplar).
/// </summary>
internal static class KupYazi
{
    /// <summary>Ölçüler mürekkep (glif çizgisi) genişliğidir; aralar yazı boyuna göre.</summary>
    internal readonly record struct Olcu(float Tilde, float Rakam, float M, float Ara, float TildeAra, float RakamOrta);

    public static GraphicsPath Yol(Font f, string metin)
    {
        var yol = new GraphicsPath();
        yol.AddString(metin, f.FontFamily, (int)f.Style, f.Size, PointF.Empty, StringFormat.GenericTypographic);
        return yol;
    }

    public static float Genislik(Font f, string metin)
    {
        using var yol = Yol(f, metin);
        return yol.GetBounds().Width;
    }

    public static Olcu Olc(Font f)
    {
        float rakam = 0f, orta;
        // En geniş üç hane: yazı tipine göre 0 ya da 8 olabilir.
        foreach (var s in new[] { "888", "000", "444" }) rakam = Math.Max(rakam, Genislik(f, s));
        using (var r = Yol(f, "8"))
        {
            var b = r.GetBounds();
            orta = b.Top + b.Height / 2f;
        }
        return new Olcu(Genislik(f, "~"), rakam, Genislik(f, "m"), f.Size * 0.30f, f.Size * 0.08f, orta);
    }

    /// <summary>"~888 m" alanının genişliği.</summary>
    public static float AlanGenisligi(Olcu o) => o.Tilde + o.TildeAra + o.Rakam + o.Ara + o.M;

    /// <summary>Mesafeyi, "m"nin sağ kenarı <paramref name="sag"/>'da olacak şekilde çizer; dikeyde rakamlar <paramref name="cy"/>'de ortalı.</summary>
    public static void MesafeCiz(Graphics g, Font f, Olcu o, float sag, float cy, KupYonu y, float cizgi)
    {
        float taban = cy - o.RakamOrta;
        float mSol = sag - o.M;
        Parca(g, f, "m", mSol, sagaYasla: false, taban, cizgi, Acik);
        float rakamSol = Parca(g, f, y.Metre.ToString(CultureInfo.InvariantCulture), mSol - o.Ara, sagaYasla: true, taban, cizgi, Acik);
        if (y.Tahmini) Parca(g, f, "~", rakamSol - o.TildeAra, sagaYasla: true, taban, cizgi, Acik);
    }

    /// <summary>Mesafe ve yön yazılarının dolgusu.</summary>
    public static readonly Color Acik = Color.FromArgb(246, 246, 246);

    /// <summary>
    /// Bir parça yazı: <paramref name="x"/> mürekkebin sol kenarı (ya da <paramref name="sagaYasla"/> ise sağ kenarı); dikeyde
    /// ortak taban kaydırması <paramref name="taban"/>. Koyu dış çizgi + dolgu. Döner: mürekkebin sol kenarı.
    /// </summary>
    public static float Parca(Graphics g, Font f, string metin, float x, bool sagaYasla, float taban, float cizgi, Color dolgu)
    {
        using var yol = Yol(f, metin);
        var b = yol.GetBounds();
        float sol = sagaYasla ? x - b.Width : x;
        using (var m = new Matrix())
        {
            m.Translate(sol - b.Left, taban);
            yol.Transform(m);
        }
        using (var golge = new Pen(Color.FromArgb(210, 0, 0, 0), cizgi) { LineJoin = LineJoin.Round })
            g.DrawPath(golge, yol);
        using (var firca = new SolidBrush(dolgu))
            g.FillPath(firca, yol);
        return sol;
    }
}

/// <summary>
/// Küp çipi: afiş bekleme süresini bitirince küp hâlâ yakındaysa afiş bu küçük çipe dönüşür (küp simgesi + pusula oku + "38 m").
/// Afişle aynı yatay ortada, afişin üst kenarının biraz üstünde durur; oyunun ortasını kapatmaz. Küp menzilden çıkınca
/// (açılınca) söner. Yüzey yalnız yön/mesafe değişince yeniden çizilir; opaklık ve konumu <see cref="KupAfisi"/> yönetir.
/// Yer bilinmiyorsa yalnız küp simgesi + "Küp"; mesafe biliniyor ama yön güvenilmezse ok çizilmez.
/// Her zaman tıklanmaz, odak çalmaz.
/// </summary>
internal sealed class KupCipi : KatmanPencere
{
    private KupYonu? _yon;
    private float _olcek = -1f;
    private Rectangle _hedef;

    public KupCipi() : base(tiklanmaz: true, "AionMeter Küp çipi") { }

    /// <summary>Hedef alana göre yerleştirip çizer; hiçbir şey değişmediyse dokunmaz. Görünürlüğe ve opaklığa karışmaz.</summary>
    public void Hazirla(Rectangle hedef, float olcek, KupYonu? yon)
    {
        if (olcek == _olcek && hedef == _hedef && yon == _yon) return;
        _hedef = hedef;
        _olcek = olcek;
        _yon = yon;
        var boyut = Boyut(olcek, yon);
        Bas(new Rectangle(Konum(hedef, olcek, boyut), boyut), g => Ciz(g, boyut, olcek, yon));
    }

    public void Opaklik(byte alfa) => AlfaAyarla(alfa);

    public byte MevcutAlfa => Alfa;

    /// <summary>Çipin sol üst köşesi: hedefin yatay ortası; üst kenarı afişin üst kenarından biraz yukarıda.</summary>
    internal static Point Konum(Rectangle hedef, float olcek, Size boyut)
    {
        int afisUst = KupAfisi.AfisKonumu(hedef, KupAfisi.Boyut(olcek)).Y;
        return new Point(hedef.Left + (hedef.Width - boyut.Width) / 2, afisUst - P(8, olcek));
    }

    // ---------------- Ölçü ve çizim (statik: ekran dışı bitmap'e de çizilebilir) ----------------

    private static int P(float v, float olcek) => (int)MathF.Round(v * olcek);

    private static Font Yazi(float olcek) => Palet.RakamTipi(15f * olcek, yariKalin: true);

    private const string YalnizKup = "Küp";

    // Mantıksal px: kenar boşluğu 9 | küp 14 | ara 7 | kadran 16 | ara 6 | "~888 m" | kenar boşluğu 10  → ~120 × 28
    internal static Size Boyut(float olcek, KupYonu? yon)
    {
        using var f = Yazi(olcek);
        int w = P(9, olcek) + P(14, olcek) + P(7, olcek);
        if (yon is not { } y) w += (int)MathF.Ceiling(KupYazi.Genislik(f, YalnizKup)) + P(11, olcek);
        else
        {
            if (y.Derece is not null) w += P(16, olcek) + P(6, olcek);
            w += (int)MathF.Ceiling(KupYazi.AlanGenisligi(KupYazi.Olc(f))) + P(10, olcek);
        }
        return new Size(w, P(28, olcek));
    }

    internal static void Ciz(Graphics g, Size boyut, float olcek, KupYonu? yon)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // Zemin: afişin aynısı, küçük
        var zemin = new RectangleF(0.5f, 0.5f, boyut.Width - 1f, boyut.Height - 1f);
        using (var yol = KupAfisi.Yuvarlak(zemin, P(7, olcek)))
        {
            using var firca = new SolidBrush(Color.FromArgb(150, 12, 14, 19));
            g.FillPath(firca, yol);
            using var kenar = new Pen(Color.FromArgb(90, Palet.Kup), Math.Max(1f, olcek));
            g.DrawPath(kenar, yol);
        }

        float cy = boyut.Height / 2f;
        float x = P(9, olcek);
        int kup = P(14, olcek);
        using (var ust = new SolidBrush(Palet.Kup))
        using (var sol = new SolidBrush(KupSimgesi.Sol))
        using (var sag = new SolidBrush(KupSimgesi.Sag))
            KupSimgesi.Ciz(g, x, cy, kup, ust, sol, sag, new PointF[4]);
        x += kup + P(7, olcek);

        using var f = Yazi(olcek);
        float cizgi = Math.Max(2f, 2.5f * olcek);
        if (yon is not { } y)
        {
            // Yer bilinmiyor: yalnız "Küp" (afişteki gibi altın)
            using var r = KupYazi.Yol(f, "8");
            var rb = r.GetBounds();
            KupYazi.Parca(g, f, YalnizKup, x, sagaYasla: false, cy - (rb.Top + rb.Height / 2f), cizgi, Palet.Kup);
            return;
        }

        if (y.Derece is int derece) KupAfisi.CizOk(g, x + P(8, olcek), cy, P(16, olcek) / 2f, derece, olcek);
        KupYazi.MesafeCiz(g, f, KupYazi.Olc(f), boyut.Width - P(10, olcek), cy, y, cizgi);
    }
}
