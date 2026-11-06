namespace AionMeter;

/// <summary>
/// Ortak renkler ve yazı tipleri: overlay, detay paneli, ayarlar penceresi ve çevirmen pencereleri aynı paleti kullanır.
/// Kontrastlar koyu zemin (Arka) ve sınıf çubuklarının koyu tonu üzerinde ölçüldü:
/// Yazi ≥ 10:1, Soluk ≥ 5:1 (çubuk üstünde), CokSoluk ≥ 5:1 (yalnızca düz zeminde kullanılır).
/// </summary>
internal static class Palet
{
    public static readonly Color Arka = Color.FromArgb(18, 20, 26);
    public static readonly Color Panel = Color.FromArgb(27, 30, 38);         // grafik zemini, ayar kutuları
    public static readonly Color PanelUst = Color.FromArgb(38, 42, 52);      // üzerine gelinen / seçili kutu
    public static readonly Color Yazi = Color.FromArgb(226, 229, 234);
    public static readonly Color Dps = Color.FromArgb(246, 247, 250);        // ana sayı, herkeste aynı
    public static readonly Color Soluk = Color.FromArgb(162, 169, 182);
    public static readonly Color CokSoluk = Color.FromArgb(128, 135, 148);
    public static readonly Color Cizgi = Color.FromArgb(44, 48, 58);         // 1 px kenar / ayırıcı
    public static readonly Color AyiriciAcik = Color.FromArgb(58, 63, 76);
    public static readonly Color Yerel = Color.FromArgb(242, 196, 107);      // kendi adın + sol şerit
    public static readonly Color Kirmizi = Color.FromArgb(250, 110, 96);
    public static readonly Color Yesil = Color.FromArgb(118, 214, 132);
    public static readonly Color Vurgu = Color.FromArgb(86, 156, 240);       // kilitsiz kenar, düğme
    public static readonly Color SabitSatir = Color.FromArgb(30, 34, 44);
    public static readonly Color Secili = Color.FromArgb(22, 255, 255, 255); // seçili satır: hafif açık zemin

    // Boss canı
    public static readonly Color BossCan = Color.FromArgb(178, 58, 48);      // #B23A30
    public static readonly Color BossIz = Color.FromArgb(42, 27, 29);        // #2A1B1D
    public static readonly Color BossBilinmiyor = Color.FromArgb(104, 62, 58);
    public static readonly Color BossOldu = Color.FromArgb(70, 72, 80);

    // Çevirmen
    public static readonly Color Terim = Color.FromArgb(232, 206, 150);
    public static readonly Color TusKenari = Color.FromArgb(70, 76, 90);

    /// <summary>Sınıf rengi: Acik = ince vurgu (çubuk ucu, grafik), Koyu = pay çubuğu (üstündeki yazı okunur kalır).</summary>
    public readonly record struct SinifRengi(Color Acik, Color Koyu);

    private static readonly SinifRengi Notr = new(Color.FromArgb(150, 158, 170), Color.FromArgb(44, 48, 58));

    // Tonlar yan yana ayırt edilsin diye renk çemberine yayıldı; oyun ikonlarının rol renkleriyle (mavi savaşçı,
    // yeşil izci, mor büyücü, sarı rahip) aynı aile korunarak her sınıf kendi tonunu aldı.
    private static readonly Dictionary<string, SinifRengi> Siniflar = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gladiator"] = new(Color.FromArgb(98, 164, 240), Color.FromArgb(26, 48, 74)),
        ["Templar"] = new(Color.FromArgb(146, 152, 242), Color.FromArgb(38, 40, 84)),
        ["Assassin"] = new(Color.FromArgb(96, 212, 150), Color.FromArgb(20, 56, 38)),
        ["Ranger"] = new(Color.FromArgb(160, 214, 98), Color.FromArgb(36, 56, 22)),
        ["Sorcerer"] = new(Color.FromArgb(186, 124, 240), Color.FromArgb(56, 32, 78)),
        ["Spiritmaster"] = new(Color.FromArgb(236, 120, 196), Color.FromArgb(74, 28, 60)),
        ["Cleric"] = new(Color.FromArgb(232, 200, 100), Color.FromArgb(56, 48, 22)),
        ["Chanter"] = new(Color.FromArgb(236, 150, 104), Color.FromArgb(70, 40, 26)),
    };

    public static SinifRengi Sinif(string? ad) =>
        ad is not null && Siniflar.TryGetValue(ad, out var r) ? r : Notr;

    /// <summary>Sınıf adlarının sabit sırası (fırça önbelleği için dizin); bilinmeyen = 0.</summary>
    public static readonly string?[] SinifSirasi = { null, "Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Spiritmaster", "Cleric", "Chanter" };

    public static int SinifIndeksi(string? ad)
    {
        if (ad is null) return 0;
        for (int i = 1; i < SinifSirasi.Length; i++)
            if (string.Equals(SinifSirasi[i], ad, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Segoe UI (yarı kalın istenirse Segoe UI Semibold; yoksa kalın Segoe UI). Boyut piksel.</summary>
    public static Font YaziTipi(float piksel, bool yariKalin = false, bool kalin = false)
    {
        piksel = Math.Max(6f, piksel);
        if (yariKalin)
        {
            var f = new Font("Segoe UI Semibold", piksel, FontStyle.Regular, GraphicsUnit.Pixel);
            if (string.Equals(f.Name, "Segoe UI Semibold", StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
            return new Font("Segoe UI", piksel, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        return new Font("Segoe UI", piksel, kalin ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }
}
