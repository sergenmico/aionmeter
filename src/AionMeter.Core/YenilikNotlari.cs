namespace AionMeter.Core;

/// <summary>Bir sürümün yenilikleri (assets/yenilikler.txt'deki bir bölüm).</summary>
public sealed record YenilikBolumu(Version Surum, IReadOnlyList<string> Maddeler);

/// <summary>
/// Güncellemeden sonraki "Yenilikler" kartının içeriği ve ne zaman gösterileceği. Dosya biçimi:
/// <code>
/// ; açıklama
/// ## 0.6.0
/// - Madde
///   (önceki maddenin devamı)
/// </code>
/// Kart, sürüm değişince bir kez gösterilir: son görülen sürümden sonraki (ondan yeni, şimdikinden eski ya da eşit) bölümler.
/// İlk kurulumda (ayar dosyası yok) gösterilmez. Saf mantık: dosya okumaz, pencere açmaz.
/// </summary>
public static class YenilikNotlari
{
    /// <summary>Kartta en çok bu kadar sürüm (uzun süre güncellenmemişse en yeniler).</summary>
    public const int EnFazlaBolum = 3;

    /// <summary>Metni bölümlere ayırır, en yeni sürüm önde. Bozuk başlıklı bölüm ve boş bölümler atlanır.</summary>
    public static List<YenilikBolumu> Coz(string metin)
    {
        var sonuc = new List<YenilikBolumu>();
        Version? surum = null;
        List<string>? maddeler = null;

        void Bitir()
        {
            if (surum is not null && maddeler is { Count: > 0 }) sonuc.Add(new YenilikBolumu(surum, maddeler));
            surum = null;
            maddeler = null;
        }

        foreach (string ham in metin.Split('\n'))
        {
            string satir = ham.Trim();
            if (satir.Length == 0 || satir.StartsWith(';')) continue;
            if (satir.StartsWith("##", StringComparison.Ordinal))
            {
                Bitir();
                surum = SurumOku(satir[2..]);
                if (surum is not null) maddeler = new List<string>();
                continue;
            }
            if (maddeler is null) continue; // başlıksız ya da bozuk başlıklı bölümün satırları
            if (satir.StartsWith("- ", StringComparison.Ordinal) || satir == "-")
            {
                string m = satir[1..].Trim();
                if (m.Length > 0) maddeler.Add(m);
            }
            else if (maddeler.Count > 0) maddeler[^1] += " " + satir; // önceki maddenin devamı
        }
        Bitir();
        sonuc.Sort((a, b) => b.Surum.CompareTo(a.Surum));
        return sonuc;
    }

    /// <summary>"0.6.0", "v0.6.0" → 0.6.0 (üç parçalı); okunamazsa null.</summary>
    public static Version? SurumOku(string? metin)
    {
        if (string.IsNullOrWhiteSpace(metin)) return null;
        metin = metin.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(metin, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Açılışta kart gösterilsin mi? <paramref name="ilkKurulum"/>: ayar dosyası yoktu (yeni kurulum: gösterilmez).
    /// <paramref name="sonGorulen"/> yoksa (bu özellikten önceki sürümden gelindi) ya da okunamıyorsa yalnız şimdiki sürüm gösterilir.
    /// Dönen <c>Onceki</c>: bundan yeni bölümler gösterilir (null: yalnız şimdiki sürümün bölümü).
    /// </summary>
    public static (bool Goster, Version? Onceki) AcilisKarari(string? sonGorulen, bool ilkKurulum, Version mevcut)
    {
        if (ilkKurulum) return (false, null);
        var onceki = SurumOku(sonGorulen);
        if (onceki is null) return (true, null);
        return onceki < mevcut ? (true, onceki) : (false, onceki);
    }

    /// <summary>
    /// Gösterilecek bölümler: <paramref name="onceki"/> &lt; sürüm ≤ <paramref name="mevcut"/> (en yeni önde, en çok
    /// <see cref="EnFazlaBolum"/>). <paramref name="onceki"/> null ise şimdiki sürüme eşit ya da ondan eski en yeni bölüm.
    /// </summary>
    public static List<YenilikBolumu> Secilenler(IReadOnlyList<YenilikBolumu> hepsi, Version? onceki, Version mevcut)
    {
        var sonuc = new List<YenilikBolumu>();
        foreach (var b in hepsi) // en yeni önde
        {
            if (b.Surum > mevcut) continue;
            if (onceki is null)
            {
                sonuc.Add(b);
                break;
            }
            if (b.Surum <= onceki) break;
            sonuc.Add(b);
            if (sonuc.Count == EnFazlaBolum) break;
        }
        return sonuc;
    }
}
