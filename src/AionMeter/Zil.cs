using System.Media;

namespace AionMeter;

/// <summary>
/// Kodla üretilen kısa, yumuşak sesler (pakette ses dosyası yok):
/// - Alarm: iki notalık çan (E5 → C5, iki kez).
/// - Gizli Küp: yükselen üç nota (E5 · G#5 · B5, majör arpej), daha kısa ve daha hafif; alarmdan inişle değil çıkışla ayrılır.
/// - Favori boss: iki nota, bir beşli yukarı (A4 → E5); ikisinden de pes ve tek seferlik, aralık daha geniş.
/// Her nota yavaş açılır ve üstel söner (tık sesi yok); tepe genliği ses ayarıyla ölçeklenir.
/// Dalga %TEMP% altında bir kez dosyaya yazılır ve yoldan çalınır (eşzamansız çalarken bellek tamponunun ömrüne bağlı
/// kalınmaz); ses seviyesi değişince dosya yeniden üretilir. Windows aynı anda tek bir PlaySound çalar: yenisi eskisini keser.
/// </summary>
internal static class Zil
{
    private const int Ornek = 22050;

    private sealed class Ses
    {
        public required string Dosya;
        public required string Ad;
        public required Func<double, byte[]> Uret;
        public SoundPlayer? Oynatici;
        public int SonSes = -1;
    }

    private static readonly Ses AlarmSesi = new()
    {
        Dosya = Path.Combine(Path.GetTempPath(), "aionmeter-zil.wav"),
        Ad = "Alarm sesi",
        Uret = ses => Dalga(new (double, double)[] { (659.25, 0.00), (523.25, 0.24), (659.25, 0.70), (523.25, 0.94) },
            notaSure: 0.42, toplam: 1.40, sonum: 7.0, tepe: 0.35 * ses),
    };

    private static readonly Ses KupSesi = new()
    {
        Dosya = Path.Combine(Path.GetTempPath(), "aionmeter-kup.wav"),
        Ad = "Küp sesi",
        Uret = ses => Dalga(new (double, double)[] { (659.25, 0.00), (830.61, 0.11), (987.77, 0.22) },
            notaSure: 0.50, toplam: 0.75, sonum: 9.0, tepe: 0.24 * ses),
    };

    private static readonly Ses FavoriSesi = new()
    {
        Dosya = Path.Combine(Path.GetTempPath(), "aionmeter-favori.wav"),
        Ad = "Favori boss sesi",
        Uret = ses => Dalga(new (double, double)[] { (440.00, 0.00), (659.25, 0.20) },
            notaSure: 0.60, toplam: 0.90, sonum: 6.0, tepe: 0.32 * ses),
    };

    /// <summary>Alarm çanı.</summary>
    public static void Cal(int sesYuzde) => Oynat(AlarmSesi, sesYuzde);

    /// <summary>Gizli Küp yakında: yükselen üç nota.</summary>
    public static void Kup(int sesYuzde) => Oynat(KupSesi, sesYuzde);

    /// <summary>Favori boss doğmak üzere: iki nota yukarı.</summary>
    public static void Favori(int sesYuzde) => Oynat(FavoriSesi, sesYuzde);

    private static void Oynat(Ses s, int sesYuzde)
    {
        try
        {
            sesYuzde = Math.Clamp(sesYuzde, 0, 100);
            if (sesYuzde != s.SonSes || !File.Exists(s.Dosya))
            {
                Durdur(s); // çalan dosyanın üstüne yazmadan önce dur
                string gecici = s.Dosya + ".tmp";
                File.WriteAllBytes(gecici, s.Uret(sesYuzde / 100.0));
                File.Move(gecici, s.Dosya, overwrite: true);
                s.SonSes = sesYuzde;
            }
            s.Oynatici ??= new SoundPlayer(s.Dosya);
            s.Oynatici.Play(); // eşzamansız; arayüz beklemez
        }
        catch (Exception ex)
        {
            HataLog.Yaz(s.Ad, ex);
        }
    }

    public static void Birak()
    {
        Durdur(AlarmSesi);
        Durdur(KupSesi);
        Durdur(FavoriSesi);
    }

    private static void Durdur(Ses s)
    {
        try { s.Oynatici?.Stop(); } catch { /* önemsiz */ }
        s.Oynatici?.Dispose();
        s.Oynatici = null;
    }

    /// <summary>16 bit mono PCM WAV: verilen notalar (frekans, başlangıç sn), her biri 12 ms açılış + üstel sönüş, hafif çan rengi.</summary>
    private static byte[] Dalga((double hz, double bas)[] notalar, double notaSure, double toplam, double sonum, double tepe)
    {
        int n = (int)(Ornek * toplam);
        var ornekler = new double[n];
        foreach (var (hz, bas) in notalar)
        {
            int i0 = (int)(bas * Ornek), adet = (int)(notaSure * Ornek);
            for (int i = 0; i < adet && i0 + i < n; i++)
            {
                double t = (double)i / Ornek;
                double zarf = Math.Min(1.0, t / 0.012) * Math.Exp(-t * sonum);                     // 12 ms açılış, üstel sönüş
                double v = Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(2 * Math.PI * hz * 2 * t); // hafif çan rengi
                ornekler[i0 + i] += v * zarf;
            }
        }
        // Son 30 ms'yi sıfıra indir: dosya sonu kesik bitmesin (tık yok).
        int kuyruk = Math.Min(n, Ornek * 30 / 1000);
        for (int i = 0; i < kuyruk; i++) ornekler[n - 1 - i] *= (double)i / kuyruk;

        double olcek = tepe / 1.18;
        using var ms = new MemoryStream(44 + n * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Ornek); w.Write(Ornek * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(n * 2);
            for (int i = 0; i < n; i++)
                w.Write((short)Math.Clamp(ornekler[i] * olcek * short.MaxValue, short.MinValue, short.MaxValue));
        }
        return ms.ToArray();
    }
}
