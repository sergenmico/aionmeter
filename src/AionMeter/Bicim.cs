using System.Globalization;

namespace AionMeter;

/// <summary>Türkçe tam sayı biçimi (yuvarlama/kısaltma yok): 46.562, 12.345.678, 48.213 /sn.</summary>
internal static class Bicim
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Tam(long v) => v.ToString("N0", Tr);

    public static string Dps(double dps)
    {
        if (double.IsNaN(dps) || double.IsInfinity(dps) || dps < 0) dps = 0;
        return Tam((long)Math.Round(dps)) + " /sn";
    }

    /// <summary>Birimsiz DPS (birim sütun başlığında): 48.213.</summary>
    public static string Sayi(double dps)
    {
        if (double.IsNaN(dps) || double.IsInfinity(dps) || dps < 0) dps = 0;
        return Tam((long)Math.Round(dps));
    }

    public static string Yuzde(double oran) => "%" + (oran * 100.0).ToString("0.0", Tr);

    /// <summary>Boss canı: bir ondalığa AŞAĞI yuvarlanır (99,96 → %99,9); hasar almış boss asla %100,0 görünmez.</summary>
    public static string CanYuzde(double oran)
    {
        if (double.IsNaN(oran) || oran < 0) oran = 0;
        double binde = Math.Floor(Math.Min(oran, 1.0) * 1000.0);
        return "%" + (binde / 10.0).ToString("0.0", Tr);
    }

    /// <summary>pay/payda yüzdesi, payda 0 ise "—".</summary>
    public static string Oran(long pay, long payda) => payda > 0 ? Yuzde((double)pay / payda) : "—";

    /// <summary>Kalan süre: 1:42 ya da 1:02:05.</summary>
    public static string Kalan(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return ((int)t.TotalHours).ToString(Tr) + ":" + t.Minutes.ToString("00", Tr) + ":" + t.Seconds.ToString("00", Tr);
        return ((int)t.TotalMinutes).ToString(Tr) + ":" + t.Seconds.ToString("00", Tr);
    }

    public static string Sure(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int dk = (int)t.TotalMinutes;
        return dk.ToString("00", Tr) + ":" + t.Seconds.ToString("00", Tr);
    }
}
