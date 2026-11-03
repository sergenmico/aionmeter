namespace AionMeter.Ceviri;

/// <summary>
/// Korunan (çevrilmeyen) bir adın arkasındaki Türkçe eki ünlü uyumuna göre düzeltir. Model eki yer tutucuya ("ZxA") göre
/// kurduğu için "Sanctum'ya", "Kinah'yı" gibi sonuçlar çıkar; burada "Sanctum'a", "Kinah'ı" yapılır.
/// Sadece ilk ek (belirtme, yönelme, bulunma, ayrılma, ilgi, vasıta, çoğul) tanınır; geri kalan ekin ünlüleri uyumlanır.
/// KURAL (İngilizce okunuş belirsiz olduğundan tutarlı, yazıma dayalı): adın YAZILIŞINDAKİ son ünlü harf alınır
/// (a/ı→a-ı, o/u→a-u, e/i→e-i; ö/ü yoktur). Sondaki sessiz "e" sayılmaz (Defense'i, Manastone'u, Strike'ı değil Strike'i).
/// Ünsüzden sonra gelen "y" ünlü "i" sayılır (Abyss'te); "ea/ee/ie/ei" çiftleri "i" sayılır (Heal'in, Speed'i, Shield'i).
/// Kısaltmalar harf adıyla okunur (HP'yi, DPS'yi).
/// Örnekler: Magic Attack'ı, Physical Defense'i, Greatsword'u, Kinah'ı, Sanctum'a, Daeva'yı, Elyos'u.
/// </summary>
internal static class TurkceEk
{
    private const string Unluler = "aeıioöuü";

    public static string Uydur(string ad, string ek)
    {
        if (ek.Length == 0) return ek;
        var (sonUnlu, unluBiter, sertBiter) = Ses(ad);
        string ilk, kalan;
        string Iki(char u) => On(u) ? "e" : "a";
        string Dort(char u) => On(u) ? (Yuvarlak(u) ? "ü" : "i") : (Yuvarlak(u) ? "u" : "ı");

        // Sıra önemli: uzun/ayırt edici ekler önce.
        if (Esle(ek, out kalan, "lar", "ler")) ilk = "l" + Iki(sonUnlu) + "r";
        else if (Esle(ek, out kalan, "yla", "yle", "la", "le")) ilk = (unluBiter ? "y" : "") + "l" + Iki(sonUnlu);
        else if (Esle(ek, out kalan, "dan", "den", "tan", "ten")) ilk = (sertBiter ? "t" : "d") + Iki(sonUnlu) + "n";
        else if (Esle(ek, out kalan, "da", "de", "ta", "te")) ilk = (sertBiter ? "t" : "d") + Iki(sonUnlu);
        // 2. çoğul iyelik (Attack Speed'iniz): ilgi ekinden önce bakılır ("ınız" ≠ "ın" + "ız").
        else if (Esle(ek, out kalan, "ınız", "iniz", "unuz", "ünüz", "nız", "niz", "nuz", "nüz"))
            ilk = (unluBiter ? "" : Dort(sonUnlu)) + "n" + Dort(sonUnlu) + "z";
        else if (Esle(ek, out kalan, "nın", "nin", "nun", "nün", "ın", "in", "un", "ün")) ilk = (unluBiter ? "n" : "") + Dort(sonUnlu) + "n";
        // 3. tekil iyelik (Defense'sini → Defense'ini, Cooldown'su → Cooldown'u)
        else if (Esle(ek, out kalan, "sı", "si", "su", "sü")) ilk = (unluBiter ? "s" : "") + Dort(sonUnlu);
        else if (Esle(ek, out kalan, "yı", "yi", "yu", "yü", "ı", "i", "u", "ü")) ilk = (unluBiter ? "y" : "") + Dort(sonUnlu);
        else if (Esle(ek, out kalan, "ya", "ye", "a", "e")) ilk = (unluBiter ? "y" : "") + Iki(sonUnlu);
        else return Uyumla(ek, sonUnlu);

        char ilkUnlu = ilk.LastOrDefault(c => Unluler.Contains(c));
        return ilk + Uyumla(kalan, ilkUnlu == default ? sonUnlu : ilkUnlu);
    }

    /// <summary>
    /// TÜRKÇE bir ada (stat adının Türkçesi: "Hasar Artışı", "Kritik Vuruş", "Savunma") ek ekler, kesme işaretsiz.
    /// Çok sözcüklü ve ı/i/u/ü ile biten adlar isim tamlamasıdır: ekten önce "n" gelir (Hasar Artışını, Hasar Artışına).
    /// Ünsüz yumuşaması: ç → c, çok heceli sözcükte k → ğ (Direnç → Direnci, Kritik → Kritiği). Döndürülen: ad + ek.
    /// </summary>
    public static string TurkceAdaEkle(string ad, string ek) => AdaEkle(ad, ek, iyelikli: false);

    /// <param name="iyelikli">Ad zaten 3. tekil iyelik ekli (Savunması): tamlama gibi "n" kaynaştırmasıyla ek alır.</param>
    private static string AdaEkle(string ad, string ek, bool iyelikli)
    {
        if (ek.Length == 0) return ad;
        string son = ad.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ad;
        string k = son.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        if (k.Length == 0) return ad + ek;
        char sonUnlu = k.LastOrDefault(c => Unluler.Contains(c));
        if (sonUnlu == default) sonUnlu = 'e';
        bool unluBiter = Unluler.Contains(k[^1]);
        bool sertBiter = "fstkçşhp".Contains(k[^1]);
        bool tamlama = iyelikli || (ad.Contains(' ') && "ıiuü".Contains(k[^1]) && !TamlamaOlmayan.Contains(k)); // Fiziksel Saldırı → Saldırıyı
        string Iki() => On(sonUnlu) ? "e" : "a";
        string Dort() => On(sonUnlu) ? (Yuvarlak(sonUnlu) ? "ü" : "i") : (Yuvarlak(sonUnlu) ? "u" : "ı");
        string Govde(bool unluyleBaslar)
        {
            if (!unluyleBaslar || unluBiter) return ad;
            int heceler = k.Count(c => Unluler.Contains(c));
            if (k[^1] == 'ç') return ad[..^1] + "c";
            if (k[^1] == 'k' && heceler >= 2) return ad[..^1] + "ğ";
            return ad;
        }

        string ilk, kalan;
        // Tamlamaya çoğul eki ("Hasar Artışları") ses değişimi ister; çoğul atlanır, gerisi işlenir.
        if (Esle(ek, out kalan, "lar", "ler"))
            return tamlama ? AdaEkle(ad, kalan, iyelikli) : ad + "l" + Iki() + "r" + Uyumla(kalan, Iki()[0]);
        // 2. çoğul iyelik (Savunmanız, Hasar Artışınız): iyelik + hâl dalından önce ("ınız" ≠ "ı" + "nız").
        if (Esle(ek, out kalan, "ınız", "iniz", "unuz", "ünüz", "nız", "niz", "nuz", "nüz"))
            return Govde(!unluBiter) + (unluBiter ? "" : Dort()) + "n" + Dort() + "z" + Uyumla(kalan, Dort()[0]);
        // 3. tekil iyelik + hâl ("hedefin Savunmasını"): model eki "ını / ına / ında / ından / ının / ıyla" diye kurar.
        // Önce iyelikli ad yapılır (Savunma → Savunması, İsabet → İsabeti, tamlama aynen), sonra hâl eki "n" ile eklenir.
        if (ek.Length >= 3 && "ıiuü".Contains(ek[0]) && (ek[1] == 'n' || (ek[1] == 'y' && ek[2] == 'l')))
        {
            string iyelik = tamlama ? ad : unluBiter ? ad + "s" + Dort() : Govde(true) + Dort();
            return AdaEkle(iyelik, ek[1..], iyelikli: true);
        }
        if (Esle(ek, out kalan, "yla", "yle", "la", "le")) ilk = (unluBiter ? "y" : "") + "l" + Iki();
        else if (Esle(ek, out kalan, "ndan", "nden", "dan", "den", "tan", "ten")) ilk = (tamlama ? "n" : "") + (sertBiter ? "t" : "d") + Iki() + "n";
        else if (Esle(ek, out kalan, "nda", "nde", "da", "de", "ta", "te")) ilk = (tamlama ? "n" : "") + (sertBiter ? "t" : "d") + Iki();
        else if (Esle(ek, out kalan, "nın", "nin", "nun", "nün", "ın", "in", "un", "ün")) ilk = (unluBiter ? "n" : "") + Dort() + "n";
        else if (Esle(ek, out kalan, "sı", "si", "su", "sü")) ilk = tamlama ? "" : (unluBiter ? "s" : "") + Dort();
        else if (Esle(ek, out kalan, "nı", "ni", "nu", "nü", "yı", "yi", "yu", "yü", "ı", "i", "u", "ü"))
            ilk = (tamlama ? "n" : unluBiter ? "y" : "") + Dort();
        else if (Esle(ek, out kalan, "na", "ne", "ya", "ye", "a", "e")) ilk = (tamlama ? "n" : unluBiter ? "y" : "") + Iki();
        else return ad + Uyumla(ek, sonUnlu);

        string govde = Govde(ilk.Length > 0 && Unluler.Contains(ilk[0]));
        char ilkUnlu = ilk.LastOrDefault(c => Unluler.Contains(c));
        return govde + ilk + Uyumla(kalan, ilkUnlu == default ? sonUnlu : ilkUnlu);
    }

    /// <summary>ı/i/u/ü ile biten ama iyelik eki taşımayan sözcükler (son sözcük bunlardan biriyse tamlama sayılmaz).</summary>
    private static readonly HashSet<string> TamlamaOlmayan = new(StringComparer.Ordinal) { "saldırı", "bilgi" };

    private static bool Esle(string ek, out string kalan, params string[] bicimler)
    {
        foreach (var b in bicimler)
            if (ek.StartsWith(b, StringComparison.Ordinal)) { kalan = ek[b.Length..]; return true; }
        kalan = "";
        return false;
    }

    /// <summary>Ekin ünlülerini sırayla bir önceki ünlüye uydurur (a/e ikili, ı/i/u/ü dörtlü).</summary>
    private static string Uyumla(string ek, char onceki)
    {
        if (ek.StartsWith("ki", StringComparison.Ordinal)) return "ki" + Uyumla(ek[2..], 'i'); // "-ki" uyuma girmez (Sanctum'daki)
        var c = ek.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if ("ae".Contains(c[i])) { c[i] = On(onceki) ? 'e' : 'a'; onceki = c[i]; }
            else if ("ıiuü".Contains(c[i])) { c[i] = On(onceki) ? (Yuvarlak(onceki) ? 'ü' : 'i') : (Yuvarlak(onceki) ? 'u' : 'ı'); onceki = c[i]; }
            else if ("oö".Contains(c[i])) onceki = c[i];
        }
        return new string(c);
    }

    private static bool On(char u) => "eiöü".Contains(u);
    private static bool Yuvarlak(char u) => "oöuü".Contains(u);

    /// <summary>Adın okunuşuna göre son ünlü, ünlüyle bitiyor mu, sert ünsüzle (f s t k ç ş h p) bitiyor mu.</summary>
    private static (char SonUnlu, bool UnluBiter, bool SertBiter) Ses(string ad)
    {
        string kelime = ad.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? ad;
        kelime = new string(kelime.Where(char.IsLetter).ToArray());
        if (kelime.Length == 0) return ('e', true, false);

        // Kısaltma (HP, DPS): harf adlarıyla okunur. Ünsüzler Türkçede "-e" ile okunur (he, pe, se); X "iks".
        if (kelime.Length <= 4 && kelime.All(char.IsUpper))
        {
            char son = char.ToLowerInvariant(kelime[^1]);
            if (son == 'x') return ('i', false, true);
            if (Unluler.Contains(son)) return (son, true, false);
            return ('e', true, false);
        }

        string k = kelime.ToLowerInvariant();
        // İngilizce sessiz son "e" okunmaz (Defense → "defens", Manastone → "manaston", Strike → "straik").
        if (k.Length > 3 && k[^1] == 'e' && !Unluler.Contains(k[^2]) && k[..^1].Any(c => Unluler.Contains(c))) k = k[..^1];
        // "-tion/-sion" "-şın" okunur: Penetration → Penetration'ı, Evasion → Evasion'ı.
        if (k.Length > 4 && (k.EndsWith("tion") || k.EndsWith("sion"))) return ('ı', false, false);
        char sonUnlu = 'a';
        for (int i = k.Length - 1; i >= 0; i--)
        {
            char c = k[i];
            // İngilizce ünlü çiftleri "i" okunur: Heal, Speed, Shield, Seize → Heal'i, Speed'i, Shield'i.
            if (i > 0 && i < k.Length - 1 && (k.Substring(i - 1, 2) is "ea" or "ee" or "ie" or "ei")) { sonUnlu = 'i'; break; } // sonda değil (Elysea'yı)
            if (Unluler.Contains(c)) { sonUnlu = c; break; }
            // İngilizce "y" ünsüzden sonra ünlü gibi okunur (Abyss → "abis").
            if (c == 'y' && (i == k.Length - 1 || !Unluler.Contains(k[i + 1])) && i > 0 && !Unluler.Contains(k[i - 1])) { sonUnlu = 'i'; break; }
        }
        char sonHarf = k[^1];
        bool unluBiter = Unluler.Contains(sonHarf) || (sonHarf == 'y' && k.Length > 1 && !Unluler.Contains(k[^2]));
        bool sert = "fstkçşhp".Contains(sonHarf);
        return (sonUnlu, unluBiter, sert);
    }
}
