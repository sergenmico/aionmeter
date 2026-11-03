using System.Globalization;
using System.Text.RegularExpressions;

namespace AionMeter.Ceviri;

/// <summary>
/// Çeviri öncesi/sonrası küçük, güvenli düzeltmeler.
/// Önce: "acquire/acquired/acquires" → "obtain/obtained/obtains" (model "acquired"ı "satın aldınız" yapıyor).
/// Sonra: tutarlı "sen" dili — ya cümlenin TAMAMI çevrilir ya da hiç dokunulmaz (resmî/samimi karışık cümle olmaz).
///  (1) 2. çoğul kişi / iyelik: sözcük sonundaki "-n(I)z(+ek)" → "-n(+ek)" (n'den önce ünlü ya da kesme işareti):
///      alamazsınız → alamazsın, aldınız → aldın, isterseniz → istersen, çantanız → çantan, seviyeniz → seviyen,
///      becerilerinizi → becerilerini, HP'niz → HP'n. Zamirler: siz → sen, size → sana, sizin → senin...
///      "deniz", "beniz" hariç; "hepiniz/biriniz/ikiniz" çevrilemez sayılır (cümle resmî kalır).
///      İngilizcede we/us/our varsa hiç uygulanmaz ("eminiz" = biz eminiz).
///  (2) emir "-(y)In" → kök: SADECE İngilizce cümle emir fiiliyle başlıyorsa, sadece ilk N yan cümlenin SON sözcüğüne
///      (N = İngilizcede baştan art arda gelen emir parçası sayısı: "Collect X and bring Y" → 2) ve sözcük kesin emir
///      biçimindeyse: ≥ 5 harf, ünlü + "yIn" (toplayın) ya da ünsüz + "In" (getirin, konuşun); "-nIn" (ilgi), "-sIn"
///      (olsun, ölürsün), "-dIn" (aldın), "-mIn", ünsüz + "tIn" (gittin), "-lArIn" hariç. Küçük bir sözlük:
///      edin → et, gidin → git, yiyin → ye, deyin → de, olun → ol, alın → al.
///      Bu kurallara uymayan ama "-In" ile biten bir yan cümle sonu sözcüğü varsa (belki resmî emir, belki ilgi eki)
///      cümleye HİÇ dokunulmaz.
/// </summary>
internal static partial class TurkceDuzelt
{
    public const string Surum = "d3"; // kurallar değişince artır (önbellek geçersiz olur)
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Once(string ingilizce) =>
        AcquireRegex().Replace(ingilizce, m =>
        {
            string kok = m.Value[0] == 'A' ? "Obtain" : "obtain";
            return m.Groups[1].Value switch { "d" => kok + "ed", "s" => kok + "s", _ => kok };
        });

    private static readonly HashSet<string> EmirFiilleri = new(StringComparer.OrdinalIgnoreCase)
    {
        "defeat", "kill", "slay", "hunt", "destroy", "collect", "gather", "bring", "deliver", "take", "give", "find", "search",
        "speak", "talk", "report", "return", "go", "travel", "head", "visit", "meet", "hold", "defend", "protect", "help",
        "save", "rescue", "escort", "follow", "use", "equip", "open", "check", "wait", "choose", "select", "enter", "leave",
        "move", "stay", "attack", "investigate", "ask", "tell", "show", "buy", "sell", "craft", "learn", "try", "keep",
        "put", "place", "read", "look", "listen", "run", "stop", "obtain", "acquire", "get", "purify", "activate",
        "light", "clear", "eliminate", "capture", "retrieve", "recover", "inspect", "complete", "accept", "eat", "drink",
        "deal", "inflict", "reach", "summon", "restore", "socket", "enchant", "upgrade", "combine", "trade", "join", "be",
    };

    private static readonly Dictionary<string, string> OzelEmir = new(StringComparer.Ordinal)
    {
        ["edin"] = "et", ["gidin"] = "git", ["yiyin"] = "ye", ["deyin"] = "de", ["olun"] = "ol", ["alın"] = "al",
        ["takın"] = "tak", ["atın"] = "at", ["açın"] = "aç", ["yenin"] = "yen", ["edinin"] = "edin", ["dinlenin"] = "dinlen",
    };

    /// <summary>"-yIn" ile biten ama emir olmayan sözcükler (ilgi eki ya da kök).</summary>
    private static readonly HashSet<string> YinIstisna = new(StringComparer.Ordinal)
    {
        "koyun", "boyun", "oyun", "köyün", "suyun", "ayın", "huyun", "beyin", "kuyun", "soyun", "toyun",
    };

    private static readonly Dictionary<string, string> Zamirler = new(StringComparer.Ordinal)
    {
        ["siz"] = "sen", ["sizin"] = "senin", ["size"] = "sana", ["sizi"] = "seni", ["sizde"] = "sende",
        ["sizden"] = "senden", ["sizinle"] = "seninle", ["sizce"] = "sence",
    };

    public static string SenBicimi(string turkce, string ingilizce)
    {
        if (BizRegex().IsMatch(ingilizce)) return turkce;
        int n = EmirParcaSayisi(ingilizce);
        bool sen = SenRegex().IsMatch(ingilizce) || n > 0;
        if (!sen) return turkce;

        // (1) 2. çoğul → 2. tekil, sözcük sözcük.
        bool cevrilemez = false;
        string s1 = SozcukRegex().Replace(turkce, m =>
        {
            string w = m.Value, k = w.ToLower(Tr);
            if (Zamirler.TryGetValue(k, out var z)) return char.IsUpper(w[0]) ? char.ToUpper(z[0], Tr) + z[1..] : z;
            var c = IkinciCogulRegex().Match(k);
            if (!c.Success) return w;
            if (k.StartsWith("deniz") || k.StartsWith("beniz")) return w;
            if (k.StartsWith("hepiniz") || k.StartsWith("biriniz") || k.StartsWith("ikiniz")) { cevrilemez = true; return w; }
            // "Iz" hecesini çıkar (uzunluk korunur: orijinal büyük/küçük harfle kes)
            int izBas = c.Groups["n"].Index + c.Groups["n"].Length;
            return w[..izBas] + w[(izBas + 2)..];
        });
        if (cevrilemez) return turkce;

        // (2) emir: yan cümleleri ve son sözcüklerini bul.
        var yanCumleler = YanCumleRegex().Split(s1);
        var degistir = new List<(int Bas, int Uzunluk, string Yeni)>();
        int konum = 0;
        int ayiriciSayac = 0;
        var ayiricilar = YanCumleRegex().Matches(s1);
        for (int i = 0; i < yanCumleler.Length; i++)
        {
            string yc = yanCumleler[i];
            var son = SonSozcukRegex().Match(yc);
            if (son.Success)
            {
                string w = son.Value, k = w.ToLower(Tr);
                bool inBiter = InSonuRegex().IsMatch(k);
                string? kok = i < n ? EmirKoku(w, k) : null;
                if (kok != null) degistir.Add((konum + son.Index, w.Length, kok));
                else if (inBiter && !w.Contains('\'') && !w.Contains('’'))
                {
                    // "-In" ile biten yan cümle sonu: emir mi ilgi eki mi bilinmiyor → cümle olduğu gibi kalsın,
                    // ama sadece İngilizcesi emirse ya da kesin emir biçimindeyse (resmî emir karışmasın).
                    if (i < n || EmirKoku(w, k) != null) return turkce;
                }
            }
            konum += yc.Length;
            if (ayiriciSayac < ayiricilar.Count) konum += ayiricilar[ayiriciSayac++].Length;
        }
        for (int i = degistir.Count - 1; i >= 0; i--)
            s1 = s1[..degistir[i].Bas] + degistir[i].Yeni + s1[(degistir[i].Bas + degistir[i].Uzunluk)..];
        return s1;
    }

    /// <summary>Kesin emir biçimindeyse kökü (orijinal harf büyüklüğüyle), değilse null.</summary>
    private static string? EmirKoku(string w, string k)
    {
        if (w.Contains('\'') || w.Contains('’')) return null;
        if (OzelEmir.TryGetValue(k, out var oz)) return char.IsUpper(w[0]) ? char.ToUpper(oz[0], Tr) + oz[1..] : oz;
        if (k.Length < 5 || !InSonuRegex().IsMatch(k)) return null;
        const string Unlu = "aeıioöuü";
        if (k.Length >= 4 && k[^3] == 'y' && Unlu.Contains(k[^4]))
            return YinIstisna.Contains(k) ? null : w[..^3];                 // toplayın → topla
        char once = k[^3];                                               // "In"den önceki harf
        if (Unlu.Contains(once)) return null;                            // ünlü + In (ilgi/iyelik: "evinin")
        if (once is 'n' or 's' or 'd' or 'm') return null;               // -nIn, -sIn, -dIn, -mIn
        if (once == 't' && !Unlu.Contains(k[^4])) return null;           // ünsüz + tIn (gittin, kaçtın)
        if (LarinRegex().IsMatch(k)) return null;                        // -ların / -lerin
        return w[..^2];                                                  // getirin → getir, konuşun → konuş
    }

    /// <summary>İngilizce cümlenin başından art arda kaç emir parçası var ("Collect X and bring Y" → 2).</summary>
    private static int EmirParcaSayisi(string ingilizce)
    {
        int say = 0;
        foreach (var parca in IngParcaRegex().Split(ingilizce.Trim()))
        {
            var ilk = IlkKelimeRegex().Match(parca);
            if (!ilk.Success || !EmirFiilleri.Contains(ilk.Value)) break;
            say++;
        }
        return say;
    }

    [GeneratedRegex(@"\b[Aa]cquire(d|s)?\b")] private static partial Regex AcquireRegex();
    [GeneratedRegex(@"\b(we|us|our|ours|ourselves)\b", RegexOptions.IgnoreCase)] private static partial Regex BizRegex();
    [GeneratedRegex(@"\b(you|your|yours|yourself)\b", RegexOptions.IgnoreCase)] private static partial Regex SenRegex();
    [GeneratedRegex(@"\p{L}+(?:['’]\p{L}+)?")] private static partial Regex SozcukRegex();
    // Küçük harfli sözcükte: ... (ünlü|kesme) + n + (ı/i/u/ü) + z + isteğe bağlı ek, sözcük sonu.
    [GeneratedRegex(@"^.*?(?<n>(?<=[aeıioöuü'’])n)[ıiuü]z(?<ek>[ıiuü]|[ae]|d[ae]n?|t[ae]n?|l[ae]|[ıiuü]n|d[ıiuü]r|ki)?$")]
    private static partial Regex IkinciCogulRegex();
    [GeneratedRegex(@"\s*[,;:]\s*|\s+ve\s+|\s*[.!?]+\s*")] private static partial Regex YanCumleRegex();
    [GeneratedRegex(@"\p{L}+(?:['’]\p{L}+)?(?=\P{L}*$)")] private static partial Regex SonSozcukRegex();
    [GeneratedRegex(@"[ıiuü]n$")] private static partial Regex InSonuRegex();
    [GeneratedRegex(@"l[ae]r[ıi]n$")] private static partial Regex LarinRegex();
    [GeneratedRegex(@"\p{L}+")] private static partial Regex IlkKelimeRegex();
    [GeneratedRegex(@"\s*;\s*|\s*,\s*(?:and\s+|then\s+)?|\s+and\s+(?:then\s+)?|\s+then\s+")] private static partial Regex IngParcaRegex();
}
