using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AionMeter.Ceviri;

/// <summary>
/// Çeviri modelini ilk kullanımda %LOCALAPPDATA%\aionmeter\modeller\opus-mt-tc-big-en-tr\ altına indirir.
/// Kaynak: Hugging Face onnx-community/opus-mt-tc-big-en-tr (Helsinki-NLP modelinin ONNX dönüşümü, CC-BY-4.0),
/// sabit commit üzerinden (dosyalar değişmez). Her dosya SHA256 ile doğrulanır; yarım kalan indirme (.part) kaldığı yerden sürer.
/// </summary>
public static class ModelIndirici
{
    private sealed record Dosya(string DepoYolu, long Boyut, string Sha256)
    {
        public string Ad => Path.GetFileName(DepoYolu);
    }

    private const string Depo = "onnx-community/opus-mt-tc-big-en-tr";
    private const string Commit = "ce128ea834a090aca240e4e113e7756e06cc78e0";

    // Boyut + SHA256: büyük dosyalar Hugging Face API'sindeki LFS oid'den (= SHA256), küçükler indirilip hesaplandı.
    private static readonly Dosya[] Dosyalar =
    [
        new("source.spm", 796_953, "180d3f94ddfee5585d78b702290ded0f4f9bd40f299349c8383b32a579fefad4"),
        new("vocab.json", 1_501_287, "01ef522455d2bf22b0716416ee34688cc2b3f5fa257f742665d21b6acc450ac1"),
        new("onnx/encoder_model_int8.onnx", 135_730_164, "58bfc076347346049a52a8d53b1f6e4664af74e9737aded374d7d1f78805f46e"),
        new("onnx/decoder_model_merged_int8.onnx", 395_716_769, "23223bce303d925b119ec905b9422a2814e7ad5f96275407be1d387b13879147"),
    ];

    public static string VarsayilanKlasor { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aionmeter", "modeller", "opus-mt-tc-big-en-tr");

    /// <summary>Toplam indirme boyutu (bayt): 533.745.173 ≈ 509 MiB.</summary>
    public static long ToplamBoyut { get; } = Dosyalar.Sum(d => d.Boyut);

    public static bool HazirMi() => HazirMi(VarsayilanKlasor);

    /// <summary>Hızlı kontrol: dosyalar var ve boyutları doğru (özet indirmede doğrulandı; dosya ancak sonra adlandırıldı).</summary>
    public static bool HazirMi(string klasor) =>
        Dosyalar.All(d => new FileInfo(Path.Combine(klasor, d.Ad)) is { Exists: true } f && f.Length == d.Boyut);

    public static Task IndirAsync(IProgress<(long indirilen, long toplam)> ilerleme, CancellationToken ct) =>
        IndirAsync(VarsayilanKlasor, ilerleme, ct);

    /// <summary>Eksik/bozuk dosyaları indirir ve doğrular. Tamam olan dosyaya dokunmaz.</summary>
    public static async Task IndirAsync(string klasor, IProgress<(long indirilen, long toplam)>? ilerleme, CancellationToken ct)
    {
        Directory.CreateDirectory(klasor);
        long onceki = 0;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("aionmeter");

        foreach (var d in Dosyalar)
        {
            string hedef = Path.Combine(klasor, d.Ad), part = hedef + ".part";
            if (File.Exists(hedef) && new FileInfo(hedef).Length == d.Boyut)
            {
                onceki += d.Boyut; ilerleme?.Report((onceki, ToplamBoyut));
                continue;
            }
            if (File.Exists(hedef)) File.Delete(hedef); // boyutu yanlış: bozuk

            for (int deneme = 1; ; deneme++)
            {
                try
                {
                    await ParcaIndirAsync(http, $"https://huggingface.co/{Depo}/resolve/{Commit}/{d.DepoYolu}", part, d, onceki, ilerleme, ct).ConfigureAwait(false);
                    string ozet = await Sha256Async(part, ct).ConfigureAwait(false);
                    if (!ozet.Equals(d.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(part);
                        throw new InvalidDataException($"{d.Ad}: SHA256 tutmadı.");
                    }
                    File.Move(part, hedef, overwrite: true);
                    break;
                }
                catch (Exception e) when (deneme < 3 && !ct.IsCancellationRequested
                                          && e is HttpRequestException or IOException or InvalidDataException)
                {
                    await Task.Delay(2000 * deneme, ct).ConfigureAwait(false);
                }
            }
            onceki += d.Boyut;
            ilerleme?.Report((onceki, ToplamBoyut));
        }
    }

    private static async Task ParcaIndirAsync(HttpClient http, string url, string part, Dosya d, long onceki,
        IProgress<(long, long)>? ilerleme, CancellationToken ct)
    {
        long mevcut = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (mevcut > d.Boyut) { File.Delete(part); mevcut = 0; }
        if (mevcut == d.Boyut) return; // tamamı inmiş, sadece doğrulanacak

        using var istek = new HttpRequestMessage(HttpMethod.Get, url);
        if (mevcut > 0) istek.Headers.Range = new RangeHeaderValue(mevcut, null);
        using var yanit = await http.SendAsync(istek, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        yanit.EnsureSuccessStatusCode();
        if (mevcut > 0 && yanit.StatusCode != System.Net.HttpStatusCode.PartialContent) mevcut = 0; // sunucu baştan gönderiyor

        await using var giris = await yanit.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var cikis = new FileStream(part, mevcut > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var tampon = new byte[1 << 16];
        long inen = mevcut, sonRapor = 0;
        int n;
        while ((n = await giris.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
        {
            await cikis.WriteAsync(tampon.AsMemory(0, n), ct).ConfigureAwait(false);
            inen += n;
            if (inen - sonRapor >= 1 << 20) { sonRapor = inen; ilerleme?.Report((onceki + inen, ToplamBoyut)); }
        }
    }

    private static async Task<string> Sha256Async(string yol, CancellationToken ct)
    {
        await using var fs = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }
}
