using System.Globalization;
using System.Text;
using AionMeter.Core;

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

    // Overlay'in yeri (OverlayYeri): kullanıcının bıraktığı dikdörtgen (X, Y, Sag, Y + Yukseklik) ve sabit kenar. Pencerenin o anki
    // yeri değil: içeriğe göre büyüyüp küçülürken kayması kaydedilmez. Eski dosyada Sag/Sabit yoksa eski davranış (X/Y sol üst).
    public int X = int.MinValue;
    public int Y = int.MinValue;
    public int Sag = int.MinValue;
    public bool? SagaSabit;        // null = eski dosya (Sabit anahtarı yok)
    public int Genislik = VarsayilanGenislik;
    public int Yukseklik;          // bırakıldığı andaki yükseklik
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

    // Sadece benim satırım: overlay'de boss + yalnız kendi satırın (sıranla); diğerleri arka planda sayılır.
    public bool SadeceBen;

    // Renk ve yazı. SinifRenk: Palet.SinifSirasi dizinine göre 0xRRGGBB, null = varsayılan (0. eleman kullanılmaz).
    public readonly int?[] SinifRenk = new int?[Palet.SinifSirasi.Length];
    public int? YerelRenk;         // kendi satır rengin; null = varsayılan altın
    public int YaziAd = 100, YaziDps = 100, YaziBoss = 100; // yüzde, 80..130
    public int CubukOpaklik = 100; // pay çubuğu opaklığı, yüzde 0..100

    // Kukla testi: hazır süreler (30/60/120/180) ya da özel süre (5..300 sn).
    public static readonly int[] KuklaSureleri = { 30, 60, 120, 180 };
    public int KuklaSure = 60;
    public bool KuklaOzel;
    public int KuklaOzelSure = 90;
    public const string VarsayilanKuklaKisayol = "Ctrl+Alt+K";
    public string KuklaKisayol = VarsayilanKuklaKisayol; // "" = kısayol yok
    public int KuklaSuresi => KuklaOzel ? KuklaOzelSure : KuklaSure;

    // Alarm
    public int AlarmSes = 40;      // yüzde, 10..100
    public int AlarmDk = 5;        // son kurulan süre (seçicide ilk görünen)

    // Gizli Küp: yakına bir küp gelince kısa, yumuşak bir ses (varsayılan açık).
    public bool KupSesi = true;
    public int KupSes = 40;        // yüzde, 10..100

    // Etkinlik geri sayımı: başlamasına 10 dk kalan etkinlik/bosslar ekranın üstünde (varsayılan açık).
    public bool Zamanlayici = true;
    public SunucuBolgesi Bolge = SunucuBolgesi.Avrupa; // takvim saatleri bu bölgenin yerel saati
    // Geri sayım panelinin yatay ortası ve üst kenarı (fiziksel px; satır sayısı/genişlik değişince ortada kalır).
    // Yoksa (int.MinValue) oyunun bulunduğu ekranın üst ortası.
    public int ZamanlayiciX = int.MinValue, ZamanlayiciY = int.MinValue;
    // Gecikme uyarısı: ping eşiği aşınca overlay'de kırmızı "320 ms" (normalken hiçbir şey görünmez).
    public bool PingUyari = true;
    public int PingEsik = PingUyarisi.VarsayilanEsik; // ms, PingUyarisi.Esikler'den biri

    public const int EnAzSolukOpaklik = 10, EnFazlaSolukOpaklik = 60;
    public const int EnAzYazi = 80, EnFazlaYazi = 130;
    public const int EnAzKukla = 5, EnFazlaKukla = 300;
    public const int EnAzAlarmSes = 10, EnFazlaAlarmSes = 100;
    public const int EnFazlaAlarmDk = 180;

    /// <summary>Renk ve yazı ayarları varsayılanda mı ("Varsayılana dön" düğmesi için).</summary>
    public bool RenkVarsayilan =>
        Array.TrueForAll(SinifRenk, r => r is null) && YerelRenk is null &&
        YaziAd == 100 && YaziDps == 100 && YaziBoss == 100 && CubukOpaklik == 100;

    public void RenkSifirla()
    {
        Array.Clear(SinifRenk);
        YerelRenk = null;
        YaziAd = YaziDps = YaziBoss = 100;
        CubukOpaklik = 100;
    }

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

    // Kaydet'in yazdığı anahtarlar. Dosyada bunların dışında kalan satırlar (ör. başka sürümün ayarı) aynen korunur.
    private static readonly HashSet<string> Bilinen = new(StringComparer.Ordinal)
    {
        "X", "Y", "Sag", "Sabit", "Genislik", "Yukseklik", "Opaklik", "Kilitli", "SadeceOyundayken", "OtomatikGuncelleme", "AltCeviri",
        "Boyut", "CevirmenDusukRam", "SavasDisi", "SolukOpaklik", "OfkeSesi", "YerelAd", "Tani", "SadeceBen", "YerelRenk",
        "YaziAd", "YaziDps", "YaziBoss", "CubukOpaklik", "KuklaSure", "KuklaOzel", "KuklaOzelSure", "AlarmSes", "AlarmDk",
        "KuklaKisayol", "KupSesi", "KupSes", "Zamanlayici", "Bolge", "ZamanlayiciX", "ZamanlayiciY", "PingUyari", "PingEsik",
    };

    private readonly List<string> _bilinmeyen = new(); // tanınmayan satırlar, Kaydet'te sona aynen yazılır

    public static Ayarlar Yukle()
    {
        var a = new Ayarlar();
        try
        {
            if (!File.Exists(Dosya)) return a;
            a.IlkAcilis = false;
            foreach (string ham in File.ReadAllLines(Dosya, Encoding.UTF8))
            {
                if (ham.TrimStart().StartsWith(';')) continue; // açıklama satırı (Kaydet yeniden yazar)
                int esit = ham.IndexOf('=');
                if (esit <= 0)
                {
                    if (ham.Trim().Length > 0) a._bilinmeyen.Add(ham);
                    continue;
                }
                string anahtar = ham[..esit].Trim();
                string deger = ham[(esit + 1)..].Trim();
                if (!Bilinen.Contains(anahtar) && !anahtar.StartsWith("Renk.", StringComparison.Ordinal))
                {
                    a._bilinmeyen.Add(ham);
                    continue;
                }
                bool sayi = int.TryParse(deger, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                switch (anahtar)
                {
                    case "X" when sayi: a.X = n; break;
                    case "Y" when sayi: a.Y = n; break;
                    case "Sag" when sayi: a.Sag = n; break;
                    case "Sabit": a.SagaSabit = deger.Equals("sag", StringComparison.OrdinalIgnoreCase) ? true
                        : deger.Equals("sol", StringComparison.OrdinalIgnoreCase) ? false : null; break;
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
                    case "SadeceBen": a.SadeceBen = deger == "1" || deger.Equals("evet", StringComparison.OrdinalIgnoreCase); break;
                    case "YerelRenk": a.YerelRenk = RenkOku(deger); break;
                    case "YaziAd" when sayi: a.YaziAd = Math.Clamp(n, EnAzYazi, EnFazlaYazi); break;
                    case "YaziDps" when sayi: a.YaziDps = Math.Clamp(n, EnAzYazi, EnFazlaYazi); break;
                    case "YaziBoss" when sayi: a.YaziBoss = Math.Clamp(n, EnAzYazi, EnFazlaYazi); break;
                    case "CubukOpaklik" when sayi: a.CubukOpaklik = Math.Clamp(n, 0, 100); break;
                    case "KuklaSure" when sayi && Array.IndexOf(KuklaSureleri, n) >= 0: a.KuklaSure = n; break;
                    case "KuklaOzel": a.KuklaOzel = deger == "1"; break;
                    case "KuklaOzelSure" when sayi: a.KuklaOzelSure = Math.Clamp(n, EnAzKukla, EnFazlaKukla); break;
                    case "AlarmSes" when sayi: a.AlarmSes = Math.Clamp(n, EnAzAlarmSes, EnFazlaAlarmSes); break;
                    case "AlarmDk" when sayi: a.AlarmDk = Math.Clamp(n, 1, EnFazlaAlarmDk); break;
                    case "KupSesi": a.KupSesi = !(deger == "0" || deger.Equals("hayir", StringComparison.OrdinalIgnoreCase)); break;
                    case "KupSes" when sayi: a.KupSes = Math.Clamp(n, EnAzAlarmSes, EnFazlaAlarmSes); break;
                    case "Zamanlayici": a.Zamanlayici = !(deger == "0" || deger.Equals("hayir", StringComparison.OrdinalIgnoreCase)); break;
                    case "Bolge": a.Bolge = BolgeOku(deger); break;
                    case "ZamanlayiciX" when sayi: a.ZamanlayiciX = n; break;
                    case "ZamanlayiciY" when sayi: a.ZamanlayiciY = n; break;
                    case "PingUyari": a.PingUyari = !(deger == "0" || deger.Equals("hayir", StringComparison.OrdinalIgnoreCase)); break;
                    case "PingEsik" when sayi && Array.IndexOf(PingUyarisi.Esikler, n) >= 0: a.PingEsik = n; break;
                    case "KuklaKisayol":
                        // "yok" = kısayol kapalı; geçersiz metin varsayılana döner
                        a.KuklaKisayol = deger.Equals("yok", StringComparison.OrdinalIgnoreCase) ? ""
                            : Kisayol.Duzgun(deger) ?? VarsayilanKuklaKisayol;
                        break;
                    default:
                        // Renk.Gladiator=62A4F0
                        if (anahtar.StartsWith("Renk.", StringComparison.Ordinal))
                        {
                            int i = Palet.SinifIndeksi(anahtar[5..]);
                            if (i > 0) a.SinifRenk[i] = RenkOku(deger);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // Okunamayan dosya kaybolmasın: ayarlar.ini.bozuk olarak saklanır, sonra varsayılanlarla devam edilir.
            HataLog.Yaz("Ayarlar okunamadı (ayarlar.ini.bozuk olarak saklanıyor)", ex);
            try { File.Copy(Dosya, Dosya + ".bozuk", overwrite: true); }
            catch (Exception ex2)
            {
                HataLog.Yaz("Bozuk ayar dosyası saklanamadı; üstüne yazılmayacak", ex2);
                a._kaydetme = true;
            }
        }
        return a;
    }

    private bool _kaydetme; // okunamayan dosya saklanamadıysa üstüne yazma

    public void Kaydet()
    {
        if (_kaydetme) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            string metin =
                "; AionMeter ayarları. Konum fiziksel piksel, Genislik 96 DPI ve %100 boyutta piksel, Opaklik ve Boyut yüzde." + Environment.NewLine +
                "X=" + X.ToString(inv) + Environment.NewLine +
                "Y=" + Y.ToString(inv) + Environment.NewLine +
                (Sag != int.MinValue ? "Sag=" + Sag.ToString(inv) + Environment.NewLine : "") +
                (SagaSabit is bool sabit ? "Sabit=" + (sabit ? "sag" : "sol") + Environment.NewLine : "") +
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
                (Tani ? "Tani=1" + Environment.NewLine : "") + // elle eklenen gizli anahtar korunur
                "SadeceBen=" + (SadeceBen ? "1" : "0") + Environment.NewLine +
                "YaziAd=" + YaziAd.ToString(inv) + Environment.NewLine +
                "YaziDps=" + YaziDps.ToString(inv) + Environment.NewLine +
                "YaziBoss=" + YaziBoss.ToString(inv) + Environment.NewLine +
                "CubukOpaklik=" + CubukOpaklik.ToString(inv) + Environment.NewLine +
                (YerelRenk is int yr ? "YerelRenk=" + yr.ToString("X6", inv) + Environment.NewLine : "") +
                RenkSatirlari() +
                "KuklaSure=" + KuklaSure.ToString(inv) + Environment.NewLine +
                "KuklaOzel=" + (KuklaOzel ? "1" : "0") + Environment.NewLine +
                "KuklaOzelSure=" + KuklaOzelSure.ToString(inv) + Environment.NewLine +
                "AlarmSes=" + AlarmSes.ToString(inv) + Environment.NewLine +
                "AlarmDk=" + AlarmDk.ToString(inv) + Environment.NewLine +
                "KuklaKisayol=" + (KuklaKisayol.Length > 0 ? KuklaKisayol : "yok") + Environment.NewLine +
                "KupSesi=" + (KupSesi ? "1" : "0") + Environment.NewLine +
                "KupSes=" + KupSes.ToString(inv) + Environment.NewLine +
                "Zamanlayici=" + (Zamanlayici ? "1" : "0") + Environment.NewLine +
                "Bolge=" + BolgeAnahtarlari[(int)Bolge] + Environment.NewLine +
                (ZamanlayiciX != int.MinValue && ZamanlayiciY != int.MinValue
                    ? "ZamanlayiciX=" + ZamanlayiciX.ToString(inv) + Environment.NewLine +
                      "ZamanlayiciY=" + ZamanlayiciY.ToString(inv) + Environment.NewLine
                    : "") +
                "PingUyari=" + (PingUyari ? "1" : "0") + Environment.NewLine +
                "PingEsik=" + PingEsik.ToString(inv) + Environment.NewLine +
                (_bilinmeyen.Count > 0 ? string.Join(Environment.NewLine, _bilinmeyen) + Environment.NewLine : "");
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

    private string RenkSatirlari()
    {
        string s = "";
        for (int i = 1; i < SinifRenk.Length; i++)
            if (SinifRenk[i] is int r)
                s += "Renk." + Palet.SinifSirasi[i] + "=" + r.ToString("X6", CultureInfo.InvariantCulture) + Environment.NewLine;
        return s;
    }

    // ayarlar.ini'deki bölge adları (SunucuBolgesi sırasıyla).
    private static readonly string[] BolgeAnahtarlari = { "avrupa", "na-dogu", "na-bati", "latin-amerika", "asya" };

    /// <summary>"na-bati" → NaBati; bilinmeyen metin varsayılana (Avrupa) döner.</summary>
    private static SunucuBolgesi BolgeOku(string deger)
    {
        int i = Array.FindIndex(BolgeAnahtarlari, b => b.Equals(deger, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? (SunucuBolgesi)i : SunucuBolgesi.Avrupa;
    }

    /// <summary>"62A4F0" ya da "#62A4F0" → 0xRRGGBB; geçersizse null (varsayılan).</summary>
    private static int? RenkOku(string deger)
    {
        string d = deger.TrimStart('#');
        return d.Length == 6 && int.TryParse(d, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) ? r : null;
    }
}
