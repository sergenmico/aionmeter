using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace AionMeter;

/// <summary>
/// Gömülü sınıf ikonları. Her ikon ilk istendiğinde bir kez okunur, satır boyuna (yüksek kaliteyle) küçültülür,
/// küçük bitmap önbelleğe alınır ve 128×128 asıl görüntü hemen bırakılır. Bilinmeyen sınıf → nötr daire.
/// </summary>
internal sealed class SinifIkonlari : IDisposable
{
    private const string Onek = "sinif.";
    private readonly Dictionary<string, Bitmap?> _onbellek = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap? _yerTutucu;
    private int _boyut;

    public int Boyut => _boyut;

    /// <summary>Boyut değişirse (DPI) önbellek boşaltılır.</summary>
    public void BoyutAyarla(int boyut)
    {
        boyut = Math.Max(8, boyut);
        if (boyut == _boyut) return;
        Temizle();
        _boyut = boyut;
    }

    public Bitmap Al(string? sinif)
    {
        if (string.IsNullOrWhiteSpace(sinif)) return YerTutucu();
        if (!_onbellek.TryGetValue(sinif, out var bmp))
        {
            bmp = Yukle(sinif);
            _onbellek[sinif] = bmp; // bulunamadıysa null da önbelleğe girer, tekrar aranmaz
        }
        return bmp ?? YerTutucu();
    }

    private Bitmap? Yukle(string sinif)
    {
        try
        {
            using var akis = Assembly.GetExecutingAssembly().GetManifestResourceStream(Onek + sinif + ".png");
            if (akis is null) return null;
            using var asil = new Bitmap(akis);
            var kucuk = new Bitmap(_boyut, _boyut, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(kucuk);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            using var nitelik = new ImageAttributes();
            nitelik.SetWrapMode(WrapMode.TileFlipXY); // kenarlarda koyu hale olmasın
            g.DrawImage(asil, new Rectangle(0, 0, _boyut, _boyut), 0, 0, asil.Width, asil.Height, GraphicsUnit.Pixel, nitelik);
            return kucuk;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Sınıf ikonu yüklenemedi: " + sinif, ex);
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
            float kenar = _boyut * 0.18f;
            var r = new RectangleF(kenar, kenar, _boyut - 2 * kenar, _boyut - 2 * kenar);
            using var firca = new SolidBrush(Color.FromArgb(96, 102, 112));
            g.FillEllipse(firca, r);
        }
        return _yerTutucu = bmp;
    }

    private void Temizle()
    {
        foreach (var b in _onbellek.Values) b?.Dispose();
        _onbellek.Clear();
        _yerTutucu?.Dispose();
        _yerTutucu = null;
    }

    public void Dispose() => Temizle();
}
