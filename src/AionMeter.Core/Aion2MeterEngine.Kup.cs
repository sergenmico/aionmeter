using AionDPS.Aion2;

namespace AionMeter.Core;

// Gizli Küp yön ve mesafesi: anlık görüntüye, menzilde küp varken ve oyuncunun yeri biliniyorken eklenir.
public sealed partial class Aion2MeterEngine
{
    private MeterSnapshot KupYonuEkle(MeterSnapshot s) => _source is { } source ? KupYonuEkle(source.Entities, s) : s;

    /// <summary>Menzilde küp yoksa ya da oyuncunun yeri bilinmiyorsa anlık görüntü olduğu gibi kalır (yön/mesafe null).</summary>
    internal static MeterSnapshot KupYonuEkle(Aion2EntityDirectory dir, MeterSnapshot s)
    {
        if (s.KupYakinda <= 0 || dir.Konum.EnIyi() is not { } ben || dir.NearestCubeTo(ben.X, ben.Y) is not { } kup)
        {
            return s;
        }

        (double derece, double metre) = Aion2Konum.YonVeMesafe(ben.X, ben.Y, kup.X, kup.Y);
        return s with { KupYonDerece = derece, KupMesafe = (int)Math.Round(metre), KupKonumTahmini = ben.Tahmini };
    }
}
