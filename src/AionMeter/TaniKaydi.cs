using System.Collections.Concurrent;
using System.Text;

namespace AionMeter;

/// <summary>
/// Gizli tanı kaydı: ayarlar.ini'de "Tani=1" varsa açılır (menüde yok). Satırlar kuyruğa atılır, tek bir arka plan
/// iş parçacığı ayarlar.ini ile aynı klasördeki tani.log'a zaman damgasıyla ekler. ~5 MB'ı aşınca tani.old.log'a döner.
/// Çağıran asla bloklanmaz (kuyruk doluysa satır atılır); her hata yutulur.
/// </summary>
internal static class TaniKaydi
{
    private const long Sinir = 5L * 1024 * 1024;
    private const int KuyrukSiniri = 20_000;
    private static BlockingCollection<string>? _kuyruk;
    private static Thread? _yazici;
    private static string _yol = "", _eskiYol = "";

    public static void Baslat(string klasor)
    {
        if (_kuyruk is not null) return;
        try
        {
            _yol = Path.Combine(klasor, "tani.log");
            _eskiYol = Path.Combine(klasor, "tani.old.log");
            _kuyruk = new BlockingCollection<string>(new ConcurrentQueue<string>(), KuyrukSiniri);
            _yazici = new Thread(Yaz) { IsBackground = true, Name = "AionMeter tanı", Priority = ThreadPriority.BelowNormal };
            _yazici.Start();
            MeterTani.Yaz = Ekle;
            Ekle("--- AionMeter " + Guncelleyici.Goster(Guncelleyici.MevcutSurum) + " tanı kaydı başladı ---");
        }
        catch
        {
            MeterTani.Yaz = null;
        }
    }

    /// <summary>Her iş parçacığından çağrılabilir; bloklamaz.</summary>
    public static void Ekle(string satir)
    {
        try
        {
            _kuyruk?.TryAdd(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + satir);
        }
        catch { /* kapanıyor */ }
    }

    /// <summary>Çıkışta: kuyruktakiler en fazla ~1 sn içinde yazılır.</summary>
    public static void Kapat()
    {
        var k = _kuyruk;
        if (k is null) return;
        MeterTani.Yaz = null;
        try
        {
            k.CompleteAdding();
            _yazici?.Join(1000);
        }
        catch { /* önemsiz */ }
    }

    private static void Yaz()
    {
        var k = _kuyruk!;
        StreamWriter? w = null;
        long boyut = 0;
        try
        {
            foreach (string satir in k.GetConsumingEnumerable())
            {
                try
                {
                    if (w is null)
                    {
                        var fs = new FileStream(_yol, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        boyut = fs.Length;
                        w = new StreamWriter(fs, new UTF8Encoding(false));
                    }
                    w.WriteLine(satir);
                    boyut += Encoding.UTF8.GetByteCount(satir) + 2;
                    if (k.Count == 0) w.Flush(); // kuyruk boşaldıkça diske (patlama sırasında satır satır değil)
                    if (boyut > Sinir)
                    {
                        w.Dispose();
                        w = null;
                        File.Move(_yol, _eskiYol, overwrite: true);
                    }
                }
                catch
                {
                    try { w?.Dispose(); } catch { /* yut */ }
                    w = null;
                    Thread.Sleep(500); // dosya kilitliyse kısa bekle; satır kaybolabilir
                }
            }
        }
        catch { /* yut */ }
        finally
        {
            try { w?.Dispose(); } catch { /* yut */ }
        }
    }
}
