using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AionMeter.Ceviri;

/// <summary>OCR'ın bulduğu bir satır ve kutusu (giriş görüntüsünün piksel koordinatında).</summary>
public sealed record OcrSatir(string Metin, Rectangle Kutu);

/// <summary>Windows'un yerleşik, çevrimdışı OCR'ı (Windows.Media.Ocr). Ek indirme yok; İngilizce OCR dil paketi gerekir.</summary>
public static partial class Ocr
{
    private static OcrEngine? _motor;
    private static readonly object _kilit = new();

    /// <summary>Bu boydan (piksel) kısa görüntüler OCR öncesi büyütülür. Küçük oyun yazısında doğruluğu artırır.</summary>
    public static int BuyutmeEsigi { get; set; } = 900;

    /// <summary>Kullanılan OCR dili (ör. "en-US"); motor yoksa null.</summary>
    public static string? Dil => Motor()?.RecognizerLanguage.LanguageTag;

    private static OcrEngine? Motor()
    {
        lock (_kilit)
        {
            if (_motor != null) return _motor;
            foreach (var etiket in new[] { "en-US", "en-GB", "en" })
            {
                var dil = new Language(etiket);
                if (OcrEngine.IsLanguageSupported(dil)) { _motor = OcrEngine.TryCreateFromLanguage(dil); break; }
            }
            // İngilizce OCR paketi yoksa kullanıcının dili (Latin alfabesi; İngilizceyi de okur, biraz daha kötü).
            _motor ??= OcrEngine.TryCreateFromUserProfileLanguages();
            return _motor;
        }
    }

    /// <summary>Kurulu OCR dilleri (tanı için).</summary>
    public static IReadOnlyList<string> KuruluDiller() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static Task<IReadOnlyList<OcrSatir>> OkuAsync(Bitmap goruntu, CancellationToken ct = default) =>
        OkuAsync(goruntu, olcek: 0, ct);

    /// <param name="olcek">0 = otomatik (küçük görüntüde 2×), 1 = büyütme yok, 2/3 = sabit büyütme.</param>
    internal static async Task<IReadOnlyList<OcrSatir>> OkuAsync(Bitmap goruntu, int olcek, CancellationToken ct = default)
    {
        var motor = Motor() ?? throw new InvalidOperationException("Windows OCR bulunamadı. Ayarlar > Dil bölümünden İngilizce dil paketini ekleyin.");
        if (olcek <= 0) olcek = Math.Max(goruntu.Width, goruntu.Height) < BuyutmeEsigi ? 2 : 1;
        int enBuyuk = (int)OcrEngine.MaxImageDimension;
        while (olcek > 1 && Math.Max(goruntu.Width, goruntu.Height) * olcek > enBuyuk) olcek--;

        using var sb = SoftwareBitmapYap(goruntu, olcek);
        ct.ThrowIfCancellationRequested();
        var sonuc = await motor.RecognizeAsync(sb).AsTask(ct).ConfigureAwait(false);

        var satirlar = new List<OcrSatir>();
        bool ingilizce = motor.RecognizerLanguage.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        foreach (var satir in sonuc.Lines)
        {
            if (satir.Words.Count == 0) continue;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = 0, y1 = 0;
            foreach (var k in satir.Words.Select(w => w.BoundingRect))
            {
                x0 = Math.Min(x0, (float)k.X); y0 = Math.Min(y0, (float)k.Y);
                x1 = Math.Max(x1, (float)(k.X + k.Width)); y1 = Math.Max(y1, (float)(k.Y + k.Height));
            }
            // Satır metnini kelimelerden kur: OCR noktalamayı bazen ayrı kelime verir.
            string metin = SatirMetni(satir);
            // İngilizce OCR paketi yoksa Türkçe tanıyıcı küçük "i"yi "İ" okuyor ("wİth"): İngilizce metinde düzelt.
            if (!ingilizce) metin = TurkceIDuzelt(metin);
            metin = YuzdeDuzelt(metin);
            satirlar.Add(new OcrSatir(metin,
                Rectangle.Round(new RectangleF(x0 / olcek, y0 / olcek, (x1 - x0) / olcek, (y1 - y0) / olcek))));
        }
        return satirlar;
    }

    /// <summary>
    /// OCR "%" işaretini sık sık "96" / "9/6" / "%o" / "0/0" okuyor ("by 20%" → "by 2096"). DAR düzeltme: sayı 1–100 ve
    /// (a) hemen önünde by / increases / reduces / decreases / up to VE hemen ardında for / of / to / and, noktalama ya da
    /// satır sonu varsa, ya da (b) hemen ardında "chance" varsa → "N%". Diğer durumlara dokunulmaz ("Restores 1096 HP",
    /// "Deals 296 damage", "+896" gibi gerçek sayılar korunur). Bilinen belirsizlik: gerçekten "by 196 for" yazıyorsa "1%" olur.
    /// </summary>
    internal static string YuzdeDuzelt(string metin)
    {
        metin = YuzdeOnekRegex().Replace(metin, m => m.Groups["n"].Value + "%");
        return YuzdeSansRegex().Replace(metin, m => m.Groups["n"].Value + "%");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\b(?:by|increases|reduces|decreases|increase|reduce|up to)\s+)(?<![\d.,])(?<n>100|[1-9]\d?)(?:96|9/6|%o|°/o|0/0)(?![\d%])(?=\s+(?:for|of|to|and)\b|\s*[.,;)]|\s*$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex YuzdeOnekRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\d.,])\b(?<n>100|[1-9]\d?)(?:96|9/6|%o|°/o|0/0)(?![\d%])(?=\s+chance\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex YuzdeSansRegex();

    /// <summary>Türkçe tanıyıcının "İ"si: sözcük başında büyük "I" (Israphel), içinde küçük "i" (wİth → with); "ı" → "i".</summary>
    internal static string TurkceIDuzelt(string metin)
    {
        var c = metin.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i] == 'İ') c[i] = i == 0 || !char.IsLetter(c[i - 1]) ? 'I' : 'i';
            else if (c[i] == 'ı') c[i] = 'i';
        }
        return new string(c);
    }

    private static string SatirMetni(OcrLine satir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var w in satir.Words)
        {
            var t = w.Text;
            bool yapisik = t.Length > 0 && ".,;:!?)%".Contains(t[0]) && sb.Length > 0;
            if (sb.Length > 0 && !yapisik) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static SoftwareBitmap SoftwareBitmapYap(Bitmap kaynak, int olcek)
    {
        int w = kaynak.Width * olcek, h = kaynak.Height * olcek;
        using var bgra = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bgra))
        {
            g.InterpolationMode = olcek > 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(kaynak, new Rectangle(0, 0, w, h));
        }
        var veri = bgra.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bayt = new byte[veri.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(veri.Scan0, bayt, 0, bayt.Length);
            // Stride = w*4 (32bpp hizalı), doğrudan Bgra8 olarak verilebilir.
            return SoftwareBitmap.CreateCopyFromBuffer(bayt.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        }
        finally { bgra.UnlockBits(veri); }
    }
}
