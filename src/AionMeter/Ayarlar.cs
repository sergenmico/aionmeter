using System.Globalization;
using System.Text;

namespace AionMeter;

/// <summary>
/// ayarlar.ini: exe klasörüne yazılır; yazılamıyorsa %AppData%\aionmeter kullanılır.
/// Konum fiziksel piksel, genişlik 96 DPI'deki mantıksal piksel olarak saklanır.
/// </summary>
internal sealed class Ayarlar
{
    // Genislik = kullanıcının istediği en az genişlik; içerik daha genişse pencere içeriğe göre büyür.
    public const int VarsayilanGenislik = 300;
    public const int EnAzGenislik = 300;
    public const int EnFazlaGenislik = 800;

    public int X = int.MinValue;
    public int Y = int.MinValue;
    public int Genislik = VarsayilanGenislik;
    public int Yukseklik;          // bilgi amaçlı (son çizilen yükseklik)
    public bool Kilitli;
    public int Opaklik = 90;       // yüzde, 30..100
    public bool IlkAcilis = true;  // dosya yoksa true
    public bool? SadeceOyunda;     // null = ayarlanmamış (normalde açık, --demo'da kapalı)
    public bool OtomatikGuncelleme = true;

    public static string Klasor { get; } = KlasorBul();
    public static string Dosya => Path.Combine(Klasor, "ayarlar.ini");

    private static string KlasorBul()
    {
        string exe = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exe, "ayarlar.ini")) || Yazilabilir(exe)) return exe;
        string yedek = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aionmeter");
        try { Directory.CreateDirectory(yedek); } catch { /* yine de dene */ }
        return yedek;
    }

    private static bool Yazilabilir(string klasor)
    {
        try
        {
            string deneme = Path.Combine(klasor, ".yazma-denemesi." + Environment.ProcessId);
            using (new FileStream(deneme, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Ayarlar Yukle()
    {
        var a = new Ayarlar();
        try
        {
            if (!File.Exists(Dosya)) return a;
            a.IlkAcilis = false;
            foreach (string ham in File.ReadAllLines(Dosya, Encoding.UTF8))
            {
                int esit = ham.IndexOf('=');
                if (esit <= 0) continue;
                string anahtar = ham[..esit].Trim();
                string deger = ham[(esit + 1)..].Trim();
                bool sayi = int.TryParse(deger, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                switch (anahtar)
                {
                    case "X" when sayi: a.X = n; break;
                    case "Y" when sayi: a.Y = n; break;
                    case "Genislik" when sayi: a.Genislik = Math.Clamp(n, EnAzGenislik, EnFazlaGenislik); break;
                    case "Yukseklik" when sayi: a.Yukseklik = n; break;
                    case "Opaklik" when sayi: a.Opaklik = Math.Clamp(n, 30, 100); break;
                    case "Kilitli": a.Kilitli = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
                    case "SadeceOyundayken": a.SadeceOyunda = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
                    case "OtomatikGuncelleme": a.OtomatikGuncelleme = !(deger == "0" || deger.Equals("hayir", StringComparison.OrdinalIgnoreCase)); break;
                }
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Ayarlar okunamadı", ex);
        }
        return a;
    }

    public void Kaydet()
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            string metin =
                "; AionMeter ayarları. Konum fiziksel piksel, Genislik 96 DPI'de piksel, Opaklik yüzde (30-100)." + Environment.NewLine +
                "X=" + X.ToString(inv) + Environment.NewLine +
                "Y=" + Y.ToString(inv) + Environment.NewLine +
                "Genislik=" + Genislik.ToString(inv) + Environment.NewLine +
                "Yukseklik=" + Yukseklik.ToString(inv) + Environment.NewLine +
                "Opaklik=" + Opaklik.ToString(inv) + Environment.NewLine +
                "Kilitli=" + (Kilitli ? "1" : "0") + Environment.NewLine +
                "OtomatikGuncelleme=" + (OtomatikGuncelleme ? "1" : "0") + Environment.NewLine +
                (SadeceOyunda is bool so ? "SadeceOyundayken=" + (so ? "1" : "0") + Environment.NewLine : "");
            // Önce geçici dosyaya yaz, sonra değiştir: yarıda kesilirse eski dosya bozulmaz.
            string gecici = Dosya + ".tmp";
            File.WriteAllText(gecici, metin, Encoding.UTF8);
            File.Move(gecici, Dosya, overwrite: true);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Ayarlar kaydedilemedi", ex);
        }
    }
}
