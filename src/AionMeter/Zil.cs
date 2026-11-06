using System.Media;

namespace AionMeter;

/// <summary>
/// Alarm sesi: bellekte üretilen kısa, yumuşak iki notalık çan (E5 → C5, iki kez). Ses dosyası yok.
/// Her nota yavaş açılır ve üstel söner (tık sesi yok); tepe genliği ses ayarıyla ölçeklenir, en fazla tam ölçeğin ~%35'i.
/// Dalga çalınırken tek bir SoundPlayer yaşar; bir sonraki çalışta eskisi bırakılır.
/// </summary>
internal static class Zil
{
    private const int Ornek = 22050;
    private static SoundPlayer? _oynatici;
    private static MemoryStream? _akis;

    public static void Cal(int sesYuzde)
    {
        try
        {
            Birak();
            _akis = new MemoryStream(Dalga(Math.Clamp(sesYuzde, 0, 100) / 100.0));
            _oynatici = new SoundPlayer(_akis);
            _oynatici.Play(); // eşzamansız; arayüz beklemez
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Alarm sesi", ex);
        }
    }

    public static void Birak()
    {
        try { _oynatici?.Stop(); } catch { /* önemsiz */ }
        _oynatici?.Dispose();
        _oynatici = null;
        _akis?.Dispose();
        _akis = null;
    }

    /// <summary>16 bit mono PCM WAV: [E5 · C5] · ara · [E5 · C5].</summary>
    private static byte[] Dalga(double ses)
    {
        (double hz, double bas)[] notalar = { (659.25, 0.00), (523.25, 0.24), (659.25, 0.70), (523.25, 0.94) };
        const double notaSure = 0.42, toplam = 1.40;
        int n = (int)(Ornek * toplam);
        var ornekler = new double[n];
        foreach (var (hz, bas) in notalar)
        {
            int i0 = (int)(bas * Ornek), adet = (int)(notaSure * Ornek);
            for (int i = 0; i < adet && i0 + i < n; i++)
            {
                double t = (double)i / Ornek;
                double zarf = Math.Min(1.0, t / 0.012) * Math.Exp(-t * 7.0);                    // 12 ms açılış, üstel sönüş
                double v = Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(2 * Math.PI * hz * 2 * t); // hafif çan rengi
                ornekler[i0 + i] += v * zarf;
            }
        }
        double tepe = 0.35 * ses / 1.18;
        using var ms = new MemoryStream(44 + n * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Ornek); w.Write(Ornek * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(n * 2);
            for (int i = 0; i < n; i++)
                w.Write((short)Math.Clamp(ornekler[i] * tepe * short.MaxValue, short.MinValue, short.MaxValue));
        }
        return ms.ToArray();
    }
}
