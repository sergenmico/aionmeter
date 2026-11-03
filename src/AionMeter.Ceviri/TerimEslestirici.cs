namespace AionMeter.Ceviri;

/// <summary>
/// Korunan terimleri metinde bulur. Regex yerine "ilk sözcüğe göre sözlük": metin sözcüklere bölünür, her sözcük için
/// o sözcükle başlayan terimler (uzun olan önce) denenir. 2.000+ terimde bile cümle başına mikro saniyeler.
/// Eşleşme büyük/küçük harf duyarlıdır. Sözcük: harf/rakam/tire dizisi; kesme işareti ayırıcıdır ("Kaisinel's" → Kaisinel + 's).
/// </summary>
internal sealed class TerimEslestirici
{
    public enum Kural
    {
        /// <summary>Her yerde korunur (Daeva, Magic Attack, çok kelimeli beceri/yer adları).</summary>
        Her,
        /// <summary>Sıradan İngilizce sözcük (Attack, Sword, beceri "Fireball"): sadece cümle ortasında Büyük Harfle ya da satırın tamamıysa.</summary>
        BuyukHarfOrtada,
    }

    private sealed record Ifade(string Ad, string[] Sozcukler, string[] Ayiricilar, Kural Kural);

    /// <summary>Metinde bulunan terim: [Bas, Bas+Uzunluk) aralığı, sondaki İngilizce çoğul "s" hariç.</summary>
    public readonly record struct Eslesme(int Bas, int Uzunluk, string Ad);

    private readonly Dictionary<string, List<Ifade>> _ilkSozcuk = new(StringComparer.Ordinal);
    public int Sayi { get; private set; }

    public void Ekle(string ad, Kural kural)
    {
        ad = ad.Trim();
        var parcalar = Bol(ad);
        if (parcalar.Count == 0) return;
        var sozcukler = parcalar.Select(p => ad.Substring(p.Bas, p.Uzunluk)).ToArray();
        var ayiricilar = new string[sozcukler.Length - 1];
        for (int i = 0; i < ayiricilar.Length; i++)
            ayiricilar[i] = ad.Substring(parcalar[i].Bas + parcalar[i].Uzunluk, parcalar[i + 1].Bas - parcalar[i].Bas - parcalar[i].Uzunluk);
        if (!_ilkSozcuk.TryGetValue(sozcukler[0], out var liste)) _ilkSozcuk[sozcukler[0]] = liste = [];
        if (liste.Any(x => x.Ad == ad)) return;
        // Aynı ad iki kuralla gelirse "Her" kazanır.
        liste.Add(new Ifade(ad, sozcukler, ayiricilar, kural));
        liste.Sort((a, b) => b.Sozcukler.Length.CompareTo(a.Sozcukler.Length));
        Sayi++;
    }

    public List<Eslesme> Bul(string metin)
    {
        var sonuc = new List<Eslesme>();
        var s = Bol(metin);
        // "Satırın tamamı": sondaki istatistik değeri yok sayılır ("Attack +120", "Block: 5%").
        string satir = System.Text.RegularExpressions.Regex.Replace(metin.Trim(), @"\s*:?\s*[+\-]?\d[\d.,]*\s*(%|s|sec|m|min)?\s*$", "")
            .TrimEnd('.', '!', '?', ':');
        for (int i = 0; i < s.Count; i++)
        {
            string ilk = metin.Substring(s[i].Bas, s[i].Uzunluk);
            Ifade? bulunan = null; int bitis = 0;
            foreach (var aday in Adaylar(ilk))
            {
                int n = aday.Sozcukler.Length;
                if (i + n > s.Count) continue;
                bool tamam = true;
                for (int k = 0; k < n && tamam; k++)
                {
                    string soz = metin.Substring(s[i + k].Bas, s[i + k].Uzunluk);
                    bool son = k == n - 1;
                    tamam = soz == aday.Sozcukler[k] || (son && Cogul(soz, aday.Sozcukler[k]));
                    if (tamam && k < n - 1)
                        tamam = metin.AsSpan(s[i + k].Bas + s[i + k].Uzunluk, s[i + k + 1].Bas - s[i + k].Bas - s[i + k].Uzunluk).SequenceEqual(aday.Ayiricilar[k]);
                }
                if (!tamam) continue;
                if (aday.Kural == Kural.BuyukHarfOrtada && n == 1)
                {
                    bool buyuk = char.IsUpper(ilk[0]);
                    bool cumleBasi = CumleBasi(metin, s[i].Bas);
                    bool tumSatir = satir == aday.Ad || satir == aday.Ad + "s";
                    if (!(tumSatir || (buyuk && !cumleBasi))) continue;
                }
                bulunan = aday;
                // Çoğul "s" terime dahil edilmez (yer tutucunun dışında kalır).
                bitis = s[i + n - 1].Bas + s[i + n - 1].Uzunluk - (Cogul(metin.Substring(s[i + n - 1].Bas, s[i + n - 1].Uzunluk), aday.Sozcukler[n - 1]) ? 1 : 0);
                i += n - 1;
                break;
            }
            if (bulunan != null)
            {
                int bas = s[i - bulunan.Sozcukler.Length + 1].Bas;
                sonuc.Add(new Eslesme(bas, bitis - bas, bulunan.Ad));
            }
        }
        return sonuc;
    }

    private IEnumerable<Ifade> Adaylar(string ilk)
    {
        if (_ilkSozcuk.TryGetValue(ilk, out var l)) foreach (var x in l) yield return x;
        // Tek sözcüklü terimin çoğulu: "Asmodians" → "Asmodian"
        if (ilk.Length > 3 && ilk[^1] == 's' && _ilkSozcuk.TryGetValue(ilk[..^1], out var l2))
            foreach (var x in l2) if (x.Sozcukler.Length == 1) yield return x;
    }

    private static bool Cogul(string soz, string terim) =>
        soz.Length == terim.Length + 1 && soz[^1] == 's' && terim[^1] != 's' && soz.StartsWith(terim, StringComparison.Ordinal);

    private static bool CumleBasi(string metin, int bas)
    {
        for (int i = bas - 1; i >= 0; i--)
        {
            char c = metin[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or '“' or '‘' or '(' or '[') continue;
            return c is '.' or '!' or '?' or ':' or '\n';
        }
        return true;
    }

    /// <summary>Sözcük aralıkları: harf/rakam ile başlayan, harf/rakam/tire devamlı diziler.</summary>
    private static List<(int Bas, int Uzunluk)> Bol(string m)
    {
        var l = new List<(int, int)>();
        int i = 0;
        while (i < m.Length)
        {
            if (!char.IsLetterOrDigit(m[i])) { i++; continue; }
            int b = i;
            while (i < m.Length && (char.IsLetterOrDigit(m[i]) || (m[i] == '-' && i + 1 < m.Length && char.IsLetterOrDigit(m[i + 1])))) i++;
            l.Add((b, i - b));
        }
        return l;
    }
}
