using AionDPS.Aion2;

namespace AionMeter.Core;

// Gizli Küp yön ve mesafesi: anlık görüntüye, menzilde küp varken ve oyuncunun yeri biliniyorken eklenir.
public sealed partial class Aion2MeterEngine
{
    /// <summary>Oyuncunun yeri tahminiyken (çevrenin ortası, eski kesin yer) yön bu mesafenin altında güvenilmez (±40-50°): yalnız mesafe verilir.</summary>
    internal const double TahminiYonEnAzMetre = 30;

    private MeterSnapshot KupYonuEkle(MeterSnapshot s) => _source is { } source ? KupYonuEkle(source.Entities, s) : s;

    /// <summary>Menzilde küp yoksa ya da oyuncunun yeri bilinmiyorsa anlık görüntü olduğu gibi kalır (yön/mesafe null). Yer tahminiyken
    /// ve küp <see cref="TahminiYonEnAzMetre"/>'den yakınken yön null, mesafe dolu ("~20 m").</summary>
    internal static MeterSnapshot KupYonuEkle(Aion2EntityDirectory dir, MeterSnapshot s)
    {
        if (s.KupYakinda <= 0 || dir.Konum.EnIyi() is not { } ben || dir.NearestCubeTo(ben.X, ben.Y) is not { } kup)
        {
            return s;
        }

        (double derece, double metre) = Aion2Konum.YonVeMesafe(ben.X, ben.Y, kup.X, kup.Y);
        int m = (int)Math.Round(metre); // karşılaştırma gösterilen değerle: "~30 m" hep yönlü, "~29 m" hep yönsüz
        bool yonGuvenilmez = ben.Tahmini && m < TahminiYonEnAzMetre;
        return s with { KupYonDerece = yonGuvenilmez ? null : derece, KupMesafe = m, KupKonumTahmini = ben.Tahmini };
    }
}
