using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AionMeter;

/// <summary>
/// Detay panelindeki beceri ikonları. assets\aion2\skills\icons\&lt;jeton&gt;.png dosyaları sadece panel açıkken,
/// ilk istendiğinde okunur, satır boyuna (HighQualityBicubic) bir kez küçültülür; 48×48 asıl görüntü hemen bırakılır.
/// En fazla <see cref="Sinir"/> ikon tutulur (en eskisi atılır). Panel kapanınca hepsi Dispose edilir.
/// Jeton yoksa/dosya yoksa nötr, köşeleri yuvarlatılmış kare yer tutucu döner.
/// </summary>
internal sealed class BeceriIkonlari : IDisposable
{
    public const int Sinir = 48;

    private readonly string _klasor = Path.Combine(AppContext.BaseDirectory, "assets", "aion2", "skills", "icons");
    private readonly int _boyut;
    private readonly Dictionary<string, Bitmap?> _onbellek = new(StringComparer.Ordinal);
    private readonly Queue<string> _sira = new(); // ekleme sırası: taşınca en eski atılır
    private Bitmap? _yerTutucu;

    public BeceriIkonlari(int boyut) => _boyut = Math.Max(8, boyut);

    public int Boyut => _boyut;

    public Bitmap Al(string? jeton)
    {
        if (string.IsNullOrEmpty(jeton) || !JetonGecerli(jeton)) return YerTutucu();
        if (_onbellek.TryGetValue(jeton, out var bmp)) return bmp ?? YerTutucu();

        bmp = Yukle(jeton);
        if (_onbellek.Count >= Sinir && _sira.TryDequeue(out var eski))
        {
            if (_onbellek.Remove(eski, out var atilan)) atilan?.Dispose();
        }
        _onbellek[jeton] = bmp; // bulunamayan da (null) önbelleğe girer, diske tekrar bakılmaz
        _sira.Enqueue(jeton);
        return bmp ?? YerTutucu();
    }

    /// <summary>Jeton dosya adına dönüşür: sadece harf, rakam, '_' ve '-' kabul edilir (klasör dışına çıkılamaz).</summary>
    private static bool JetonGecerli(string jeton)
    {
        if (jeton.Length > 96) return false;
        foreach (char c in jeton)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
        return true;
    }

    private Bitmap? Yukle(string jeton)
    {
        try
        {
            string yol = Path.Combine(_klasor, jeton + ".png");
            if (!File.Exists(yol)) return null;
            using var akis = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var asil = new Bitmap(akis);
            var kucuk = new Bitmap(_boyut, _boyut, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(kucuk);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            using var nitelik = new ImageAttributes();
            nitelik.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(asil, new Rectangle(0, 0, _boyut, _boyut), 0, 0, asil.Width, asil.Height, GraphicsUnit.Pixel, nitelik);
            return kucuk;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Beceri ikonu yüklenemedi: " + jeton, ex);
            return null;
        }
    }

    private Bitmap YerTutucu()
    {
        if (_yerTutucu is not null) return _yerTutucu;
        var bmp = new Bitmap(_boyut, _boyut, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = _boyut * 0.1f, r = _boyut * 0.25f, w = _boyut - 2 * k;
            using var yol = new GraphicsPath();
            yol.AddArc(k, k, r, r, 180, 90);
            yol.AddArc(k + w - r, k, r, r, 270, 90);
            yol.AddArc(k + w - r, k + w - r, r, r, 0, 90);
            yol.AddArc(k, k + w - r, r, r, 90, 90);
            yol.CloseFigure();
            using var firca = new SolidBrush(Color.FromArgb(70, 76, 88));
            g.FillPath(firca, yol);
        }
        return _yerTutucu = bmp;
    }

    public void Dispose()
    {
        foreach (var b in _onbellek.Values) b?.Dispose();
        _onbellek.Clear();
        _sira.Clear();
        _yerTutucu?.Dispose();
        _yerTutucu = null;
    }
}
