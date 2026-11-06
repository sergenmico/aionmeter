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

    /// <summary>Sınıf rengi: kullanıcı Ayarlar'da seçtiyse o (koyu tonu otomatik türetilmiş), yoksa varsayılan.</summary>
    public static SinifRengi Sinif(string? ad)
    {
        int i = SinifIndeksi(ad);
        return i > 0 && _ozel[i] is { } o ? o : VarsayilanSinif(ad);
    }

    public static SinifRengi VarsayilanSinif(string? ad) =>
        ad is not null && Siniflar.TryGetValue(ad, out var r) ? r : Notr;

    /// <summary>Sınıf adlarının sabit sırası (fırça önbelleği için dizin); bilinmeyen = 0.</summary>
    public static readonly string?[] SinifSirasi = { null, "Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Spiritmaster", "Cleric", "Chanter" };

    // ---------------- Kullanıcı renkleri (Ayarlar → Renk ve yazı) ----------------

    /// <summary>
    /// Seçilebilen 16 renk: renk çemberine yayılmış, koyu zeminde canlı ama göz yormayan tonlar.
    /// 8 sınıfın varsayılanları da içindedir. Hepsi açık tondur; pay çubuğu için koyu tonları otomatik türetilir.
    /// </summary>
    public static readonly (string Ad, Color Renk)[] Secenekler =
    {
        ("Kırmızı", Color.FromArgb(240, 104, 104)), ("Turuncu", Color.FromArgb(236, 150, 104)),
        ("Altın", Color.FromArgb(232, 200, 100)), ("Limon", Color.FromArgb(222, 222, 92)),
        ("Fıstık", Color.FromArgb(160, 214, 98)), ("Nane", Color.FromArgb(150, 232, 190)),
        ("Zümrüt", Color.FromArgb(96, 212, 150)), ("Turkuaz", Color.FromArgb(78, 206, 206)),
        ("Buz", Color.FromArgb(168, 212, 250)), ("Mavi", Color.FromArgb(98, 164, 240)),
        ("Lavanta", Color.FromArgb(146, 152, 242)), ("Mor", Color.FromArgb(186, 124, 240)),
        ("Eflatun", Color.FromArgb(222, 118, 232)), ("Pembe", Color.FromArgb(236, 120, 196)),
        ("Krem", Color.FromArgb(236, 226, 186)), ("Gümüş", Color.FromArgb(184, 190, 202)),
    };

    private static readonly SinifRengi?[] _ozel = new SinifRengi?[SinifSirasi.Length];

    /// <summary>Kendi satırının rengi (ad, sıra, sol şerit). Varsayılan altın (Yerel).</summary>
    public static Color YerelRenk { get; private set; } = Yerel;

    /// <summary>YerelRenk'in yazı hali: her sınıfın pay çubuğu üstünde en az 4,5:1 okunur (gerekirse açılır).</summary>
    public static Color YerelYazi { get; private set; } = Yerel;

    /// <summary>Pay çubuğu opaklığı (0..255).</summary>
    public static int CubukAlfa { get; private set; } = 255;

    /// <summary>Ayarlardaki renkleri uygular. Overlay açılışta ve ayar değişince çağırır; sonra fırçalarını yeniler.</summary>
    public static void Uygula(Ayarlar a)
    {
        for (int i = 1; i < SinifSirasi.Length; i++)
        {
            _ozel[i] = a.SinifRenk[i] is int rgb ? new SinifRengi(Rgb(rgb), KoyuTuret(Rgb(rgb))) : null;
        }
        YerelRenk = a.YerelRenk is int y ? Rgb(y) : Yerel;
        CubukAlfa = Math.Clamp(a.CubukOpaklik * 255 / 100, 0, 255);

        // Ad en açık çubuğun üstünde de okunsun: zemin olarak sınıfların en açık koyu tonu alınır.
        Color enAcik = Arka;
        for (int i = 0; i < SinifSirasi.Length; i++)
        {
            var k = Sinif(SinifSirasi[i]).Koyu;
            if (Parlaklik(k) > Parlaklik(enAcik)) enAcik = k;
        }
        YerelYazi = Okunur(YerelRenk, enAcik, 4.5);
    }

    public static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    public static int RgbAl(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    /// <summary>
    /// Açık tondan pay çubuğu tonu: ton ve doygunluk korunur, parlaklık düşürülür; üstündeki Yazi ≥ ~10:1 ve
    /// Soluk ≥ 5:1 okunur kalana kadar (göreli parlaklık ≤ 0,030; varsayılan koyu tonlar da bu aralıkta).
    /// </summary>
    public static Color KoyuTuret(Color acik)
    {
        for (int yuzde = 34; yuzde > 8; yuzde--)
        {
            var c = Color.FromArgb(acik.R * yuzde / 100, acik.G * yuzde / 100, acik.B * yuzde / 100);
            if (Parlaklik(c) <= 0.030) return c;
        }
        return Color.FromArgb(acik.R / 12, acik.G / 12, acik.B / 12);
    }

    /// <summary>WCAG göreli parlaklık (0..1).</summary>
    public static double Parlaklik(Color c)
    {
        static double D(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * D(c.R) + 0.7152 * D(c.G) + 0.0722 * D(c.B);
    }

    public static double Kontrast(Color a, Color b)
    {
        double x = Parlaklik(a), y = Parlaklik(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Yazı rengi zeminde hedef kontrasta ulaşana kadar beyaza doğru açılır (ton korunur).</summary>
    public static Color Okunur(Color yazi, Color zemin, double hedef)
    {
        var c = yazi;
        for (int i = 1; i <= 20 && Kontrast(c, zemin) < hedef; i++)
        {
            int k = i * 5; // %5 adımlarla beyaza karıştır
            c = Color.FromArgb(yazi.R + (255 - yazi.R) * k / 100, yazi.G + (255 - yazi.G) * k / 100, yazi.B + (255 - yazi.B) * k / 100);
        }
        return c;
    }

    public static int SinifIndeksi(string? ad)
    {
        if (ad is null) return 0;
        for (int i = 1; i < SinifSirasi.Length; i++)
            if (string.Equals(SinifSirasi[i], ad, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>
    /// Sayı yazı tipi: Windows 10/11 ile gelen Bahnschrift (DIN çizgisinde, oyun arayüzlerine yakışan dar ve net rakamlar).
    /// Ana sayılar için SemiBold, küçük sayılar için normal ağırlık; ikisi de normal genişlikte (SemiCondensed 11–13 px'te
    /// 3/6/8'i kapatıyor, yarı saydam overlay'de okunurluk düşüyor). Bahnschrift yoksa Segoe UI'ye sessizce düşer.
    /// Rakamlar orantılıdır; sabit genişlik <see cref="RakamYazisi"/> ile sağlanır.
    /// </summary>
    public static Font RakamTipi(float piksel, bool yariKalin = false)
    {
        string ad = yariKalin ? "Bahnschrift SemiBold" : "Bahnschrift";
        var f = new Font(ad, Math.Max(6f, piksel), FontStyle.Regular, GraphicsUnit.Pixel);
        if (string.Equals(f.Name, ad, StringComparison.OrdinalIgnoreCase)) return f;
        f.Dispose();
        return YaziTipi(piksel, yariKalin);
    }

    /// <summary>Gizli Küp rozeti (sıcak altın: hazine; alarm mavisi ve öfke kırmızısından ayrı).</summary>
    public static readonly Color Kup = Color.FromArgb(236, 204, 122);

    /// <summary>Boss can çubuğunda yeni alınan hasarın kısa süre kalan açık izi.</summary>
    public static readonly Color BossHasarIzi = Color.FromArgb(222, 136, 112);

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
