namespace AionMeter;

/// <summary>
/// Anlık DPS oku (overlay kullanır, denetim aracı dener): oyuncunun son ~10 sn'deki DPS'i (<see cref="PlayerDamage.SonDps"/>) kendi dövüş
/// ortalamasından (<see cref="PlayerDamage.Dps"/>) belirgin yüksekse ▲ (+1), belirgin düşükse ▼ (−1), değilse ok yok (0).
/// Histerezis: ok %15 farkla çıkar, fark %8'in altına inince kalkar; sınırda gidip gelirken titremez.
/// Oyuncunun kendi aktif süresinin ilk 15 sn'sinde (ortalama henüz oturmadı; dövüşe geç giren için de kendi ilk vuruşundan sayılır) ve
/// dövüş bitmişken ok yok. Boss modunda aynı kural bossa vurulanla (<see cref="PlayerDamage.SonBossDps"/>, <see cref="PlayerDamage.BossDps"/>,
/// <see cref="PlayerDamage.BossAktif"/>) uygulanır.
/// </summary>
public static class DpsEgilimi
{
    public const double Esik = 0.15, Kalkis = 0.08;
    public static readonly TimeSpan EnAzSure = TimeSpan.FromSeconds(15);

    /// <param name="simdi">Şu an gösterilen ok (−1, 0, +1).</param>
    /// <param name="sure">Oyuncunun kendi aktif süresi (ortalamanın paydası).</param>
    public static int Hesapla(int simdi, double? sonDps, double ortalama, TimeSpan sure, bool bitti)
    {
        if (bitti || sure < EnAzSure || sonDps is not double son) return 0;
        if (double.IsNaN(son) || double.IsInfinity(son) || son < 0) return 0;
        if (double.IsNaN(ortalama) || double.IsInfinity(ortalama) || ortalama <= 0) return 0;
        double fark = son / ortalama - 1.0;
        if (fark >= Esik) return 1;
        if (fark <= -Esik) return -1;
        if (simdi > 0 && fark >= Kalkis) return 1;
        if (simdi < 0 && fark <= -Kalkis) return -1;
        return 0;
    }
}
