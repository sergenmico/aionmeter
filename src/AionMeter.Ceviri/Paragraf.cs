using System.Drawing;

namespace AionMeter.Ceviri;

/// <summary>OCR satırlarını paragraflara birleştirir (görev/diyalog metni birden çok satıra kırılır).</summary>
public static class Paragraf
{
    /// <summary>
    /// Birleştirme kuralı: satırlar üst üste, aradaki boşluk satır yüksekliğinin ~0,9 katından az, sol kenarlar hizalı,
    /// yükseklikler benzer VE metin düzyazı gibi (önceki satır ≥ 4 kelime ve sayı/yüzde ile bitmiyor) ya da
    /// yeni satır küçük harfle başlıyor. Kısa istatistik satırları ("Attack +120") ayrı kalır.
    /// </summary>
    public static IReadOnlyList<string> Birlestir(IReadOnlyList<OcrSatir> satirlar)
    {
        var sonuc = new List<string>();
        string? metin = null;
        Rectangle kutu = default, son = default;
        string sonSatir = "";
        foreach (var s in satirlar.Where(s => !string.IsNullOrWhiteSpace(s.Metin)).OrderBy(s => s.Kutu.Y).ThenBy(s => s.Kutu.X))
        {
            string m = s.Metin.Trim();
            if (metin != null && AyniParagraf(son, sonSatir, s.Kutu, m))
            {
                metin = metin.EndsWith('-') && char.IsLower(m[0]) ? metin[..^1] + m : metin + " " + m;
                kutu = Rectangle.Union(kutu, s.Kutu);
            }
            else
            {
                if (metin != null) sonuc.Add(metin);
                metin = m; kutu = s.Kutu;
            }
            son = s.Kutu; sonSatir = m;
        }
        if (metin != null) sonuc.Add(metin);
        return sonuc;
    }

    private static bool AyniParagraf(Rectangle onceki, string oncekiMetin, Rectangle yeni, string yeniMetin)
    {
        float h = Math.Max(1, onceki.Height);
        float bosluk = yeni.Y - onceki.Bottom;
        bool yakin = bosluk < h * 0.9f && bosluk > -h * 0.5f;
        bool hizali = Math.Abs(yeni.X - onceki.X) < Math.Max(h * 2.5f, 20);
        bool benzerBoy = yeni.Height < h * 1.6f && yeni.Height > h * 0.6f;
        if (!(yakin && hizali && benzerBoy)) return false;
        bool kucukHarf = char.IsLower(yeniMetin[0]);
        char sonKarakter = oncekiMetin[^1];
        static int Kelime(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool duzyazi = Kelime(oncekiMetin) >= 4 && !char.IsDigit(sonKarakter) && sonKarakter != '%';
        // Cümle bittiyse yeni satır ancak kendisi de düzyazıysa aynı paragraftır ("Cooldown: 30s" ayrı kalır).
        if (duzyazi && ".!?:".Contains(sonKarakter)) duzyazi = Kelime(yeniMetin) >= 4;
        return kucukHarf || duzyazi;
    }
}
