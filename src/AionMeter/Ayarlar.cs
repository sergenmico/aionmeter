using System.Globalization;
using System.Text;

namespace AionMeter;

/// <summary>
/// ayarlar.ini: exe klasörüne yazılır; yazılamıyorsa %AppData%\aionmeter kullanılır.
/// Konum fiziksel piksel, genişlik 96 DPI ve %100 Boyut'taki mantıksal piksel olarak saklanır.
/// </summary>
internal enum SavasDisiModu { Soluk, Gizle, HepGorunur }

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
    public bool AltCeviri = true;  // Alt ile açıklama çevirisi (model indirildiyse çalışır)
    public bool CevirmenDusukRam;  // false: hızlı (~1 GB yüklüyken), true: ~660 MB ama ~5× yavaş
    public int Boyut = 100;        // overlay + detay paneli boyutu, yüzde (Windows ölçeğinin üstüne). Küçük ekranlı dizüstüler için
    public static readonly int[] BoyutSecenekleri = { 70, 80, 90, 100, 110, 125 };
    public SavasDisiModu SavasDisi = SavasDisiModu.Soluk; // savaş bitince (~8 sn sonra) overlay ne yapsın
    public int SolukOpaklik = 25;  // "Soluk" modunda pencere opaklığı, yüzde (10..60)
    public bool OfkeSesi;          // öfke uyarısında kısa sistem sesi (varsayılan kapalı)
    public string? YerelAd;        // motorun kesin bildiği kendi karakter adın (sonraki açılışta motora verilir)
    public bool Tani;              // gizli tanı kaydı (tani.log); menüde/pencerede yok, kullanıcı elle "Tani=1" yazar

    public const int EnAzSolukOpaklik = 10, EnFazlaSolukOpaklik = 60;

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
                    case "AltCeviri": a.AltCeviri = !(deger == "0" || deger.Equals("hayir", StringComparison.OrdinalIgnoreCase)); break;
                    case "Boyut" when sayi: a.Boyut = Math.Clamp(n, 50, 150); break;
                    case "CevirmenDusukRam": a.CevirmenDusukRam = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
                    case "SavasDisi":
                        a.SavasDisi = deger.ToLowerInvariant() switch
                        {
                            "gizle" => SavasDisiModu.Gizle,
                            "hep" => SavasDisiModu.HepGorunur,
                            _ => SavasDisiModu.Soluk,
                        };
                        break;
                    case "SolukOpaklik" when sayi: a.SolukOpaklik = Math.Clamp(n, EnAzSolukOpaklik, EnFazlaSolukOpaklik); break;
                    case "OfkeSesi": a.OfkeSesi = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
                    case "YerelAd": a.YerelAd = deger.Length > 0 ? deger : null; break;
                    case "Tani": a.Tani = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
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
                "; AionMeter ayarları. Konum fiziksel piksel, Genislik 96 DPI ve %100 boyutta piksel, Opaklik ve Boyut yüzde." + Environment.NewLine +
                "X=" + X.ToString(inv) + Environment.NewLine +
                "Y=" + Y.ToString(inv) + Environment.NewLine +
                "Genislik=" + Genislik.ToString(inv) + Environment.NewLine +
                "Yukseklik=" + Yukseklik.ToString(inv) + Environment.NewLine +
                "Opaklik=" + Opaklik.ToString(inv) + Environment.NewLine +
                "Boyut=" + Boyut.ToString(inv) + Environment.NewLine +
                "Kilitli=" + (Kilitli ? "1" : "0") + Environment.NewLine +
                "OtomatikGuncelleme=" + (OtomatikGuncelleme ? "1" : "0") + Environment.NewLine +
                "AltCeviri=" + (AltCeviri ? "1" : "0") + Environment.NewLine +
                "CevirmenDusukRam=" + (CevirmenDusukRam ? "1" : "0") + Environment.NewLine +
                "SavasDisi=" + (SavasDisi switch { SavasDisiModu.Gizle => "gizle", SavasDisiModu.HepGorunur => "hep", _ => "soluk" }) + Environment.NewLine +
                "SolukOpaklik=" + SolukOpaklik.ToString(inv) + Environment.NewLine +
                "OfkeSesi=" + (OfkeSesi ? "1" : "0") + Environment.NewLine +
                (SadeceOyunda is bool so ? "SadeceOyundayken=" + (so ? "1" : "0") + Environment.NewLine : "") +
                (string.IsNullOrWhiteSpace(YerelAd) ? "" : "YerelAd=" + YerelAd.Trim() + Environment.NewLine) +
                (Tani ? "Tani=1" + Environment.NewLine : ""); // elle eklenen gizli anahtar korunur
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
