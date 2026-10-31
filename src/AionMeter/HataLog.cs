using System.Text;

namespace AionMeter;

/// <summary>hata.log (ayar klasöründe). Boyutu ~256 KB ile sınırlı; aşınca son yarısı tutulur.</summary>
internal static class HataLog
{
    private const long Sinir = 256 * 1024;
    private static readonly object Kilit = new();
    private static string? _sonBaslik;
    private static DateTime _sonZaman;

    /// <summary>Ayarlanırsa günlük bu klasöre yazılır (güncelleme kurucusu geçici klasörden çalışırken uygulama klasörü).</summary>
    public static string? Klasor;

    public static string Yol => Path.Combine(Klasor ?? Ayarlar.Klasor, "hata.log");

    public static void Yaz(string baslik, Exception? ex = null)
    {
        try
        {
            lock (Kilit)
            {
                var simdi = DateTime.Now;
                string ozet = baslik + "|" + ex?.GetType().Name + "|" + ex?.Message;
                // Zamanlayıcı her 500 ms'de aynı hatayı atarsa dosyayı doldurmasın.
                if (ozet == _sonBaslik && (simdi - _sonZaman).TotalSeconds < 60) return;
                _sonBaslik = ozet;
                _sonZaman = simdi;

                string satir = simdi.ToString("yyyy-MM-dd HH:mm:ss") + "  " + baslik +
                               (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine;
                string yol = Yol;
                var fi = new FileInfo(yol);
                if (fi.Exists && fi.Length + satir.Length > Sinir)
                {
                    byte[] eski = File.ReadAllBytes(yol);
                    int bas = eski.Length / 2;
                    using var fs = new FileStream(yol, FileMode.Create, FileAccess.Write, FileShare.Read);
                    fs.Write(Encoding.UTF8.GetBytes("... (eski kayıtlar kırpıldı)" + Environment.NewLine));
                    fs.Write(eski, bas, eski.Length - bas);
                }
                File.AppendAllText(yol, satir, Encoding.UTF8);
            }
        }
        catch
        {
            // Kayıt yazılamıyorsa yapacak bir şey yok; uygulama çalışmaya devam eder.
        }
    }
}
