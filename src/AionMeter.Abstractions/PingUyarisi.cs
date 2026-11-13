namespace AionMeter;

/// <summary>
/// Gecikme uyarısının görünürlük kuralı (overlay kullanır, denetim aracı dener). Histerezis: ping eşiğe ulaşınca görünür,
/// eşiğin %80'inin altına inince (250 → 200 ms) ya da ölçüm kaybolunca (null) gizlenir; sınırda gidip gelirken yanıp sönmez.
/// </summary>
public static class PingUyarisi
{
    public const int VarsayilanEsik = 250;
    public static readonly int[] Esikler = { 150, 250, 400 };

    /// <summary>Uyarının kalktığı değer: eşiğin %80'i.</summary>
    public static int Kalkis(int esik) => esik - esik / 5;

    public static bool Gorunsun(bool simdiGorunur, int? pingMs, int esik)
    {
        if (pingMs is not int ms || esik <= 0) return false;
        return simdiGorunur ? ms >= Kalkis(esik) : ms >= esik;
    }
}
