using System.Text.RegularExpressions;

namespace AionMeter.Ceviri;

/// <summary>
/// Çeviri öncesi/sonrası küçük, güvenli düzeltmeler.
/// Önce: "acquire(d)" → "obtain(ed)" (model "acquired"ı "satın aldınız" yapıyor; "obtained" → "elde ettin").
/// Sonra: tutarlı "sen" dili. Sadece güvenli kalıplar:
///  (1) ikinci çoğul kişi/iyelik "-(I)n(I)z" → "-(I)n": kullanamazsınız → kullanamazsın, ulaştınız → ulaştın,
///      envanteriniz → envanterin, becerilerinizi → becerilerini, ister misiniz → ister misin.
///      ("deniz", "yalnız" gibi sözcükler kalıba uymaz: n'den önce ı/i/u/ü gerekir.)
///  (2) emir "-(y)In" → kök (toplayın → topla, getirin → getir): SADECE İngilizce cümle emir fiiliyle başlıyorsa ve
///      sözcük yan cümlenin sonundaysa (".", ",", "!", " ve " öncesi). Türkçede ilgi eki yan cümle sonunda olmaz,
///      bu yüzden bu konumda "-In" neredeyse her zaman emirdir; yine de "yakın, uzun, için..." gibi sözcükler hariç.
/// </summary>
internal static partial class TurkceDuzelt
{
    public const string Surum = "d1"; // kurallar değişince artır (önbellek geçersiz olur)

    public static string Once(string ingilizce) =>
        AcquireRegex().Replace(ingilizce, m => m.Value[0] == 'A' ? "Obtain" + m.Groups[1].Value : "obtain" + m.Groups[1].Value);

    private static readonly HashSet<string> EmirFiilleri = new(StringComparer.OrdinalIgnoreCase)
    {
        "defeat", "kill", "slay", "hunt", "destroy", "collect", "gather", "bring", "deliver", "take", "give", "find", "search",
        "speak", "talk", "report", "return", "go", "travel", "head", "visit", "meet", "hold", "defend", "protect", "help",
        "save", "rescue", "escort", "follow", "use", "equip", "open", "check", "wait", "choose", "select", "enter", "leave",
        "move", "stay", "attack", "investigate", "ask", "tell", "show", "buy", "sell", "craft", "learn", "try", "keep",
        "put", "place", "read", "look", "listen", "run", "stop", "obtain", "acquire", "get", "craft", "purify", "activate",
        "light", "clear", "eliminate", "capture", "retrieve", "recover", "inspect", "complete", "accept",
        "deal", "inflict", "reach", "summon", "restore", "socket", "enchant", "upgrade", "combine", "trade", "join",
    };

    private static readonly HashSet<string> EmirIstisna = new(StringComparer.OrdinalIgnoreCase)
    {
        "için", "yakın", "uzun", "altın", "kadın", "bütün", "gün", "dün", "sabun", "orman", "zemin", "yemin", "dikkatin",
        "hepin", "birin", "ikin", "onun", "bunun", "şunun", "benim", "senin", "bizim", "kimin", "neyin",
    };

    public static string SenBicimi(string turkce, string ingilizce)
    {
        // (1) -(I)n(I)z(+ek) → -(I)n(+ek)
        string s = IkinciCogulRegex().Replace(turkce, m =>
        {
            string kelime = m.Value;
            if (EmirIstisna.Contains(m.Groups["kok"].Value + m.Groups["n"].Value)) return kelime;
            return m.Groups["kok"].Value + m.Groups["n"].Value + m.Groups["ek"].Value;
        });

        // (2) emir: İngilizce emir fiiliyle başlıyorsa
        var ilk = IlkKelimeRegex().Match(ingilizce);
        if (ilk.Success && EmirFiilleri.Contains(ilk.Value))
            s = EmirRegex().Replace(s, m =>
            {
                string kelime = m.Groups["k"].Value;
                if (EmirIstisna.Contains(kelime) || kelime.Length < 4) return m.Value;
                string kok = kelime.EndsWith("yın") || kelime.EndsWith("yin") || kelime.EndsWith("yun") || kelime.EndsWith("yün")
                    ? kelime[..^3] : kelime[..^2];
                kok = kok switch { "ed" => "et", "gid" => "git", _ => kok }; // edin → et, gidin → git
                return kok + m.Groups["son"].Value;
            });
        return s;
    }

    [GeneratedRegex(@"\b(acquire(d|s)?)\b", RegexOptions.IgnoreCase)] private static partial Regex AcquireRegexHam();
    [GeneratedRegex(@"\b[Aa]cquire(d|s)?\b")] private static partial Regex AcquireRegex();
    // Kesme işaretinden sonra gelmeyen sözcük; kök + (ı/i/u/ü)n + (ı/i/u/ü)z + isteğe bağlı ek, sözcük sonu.
    [GeneratedRegex(@"(?<!\p{L})(?<kok>(?<=['’])\p{L}*?|\p{L}+?)(?<n>[ıiuü]n)[ıiuü]z(?<ek>[ıiuü]|[ae]|d[ae]n?|t[ae]n?|l[ae]|[ıiuü]n|d[ıiuü]r)?(?!\p{L})")]
    private static partial Regex IkinciCogulRegex();
    [GeneratedRegex(@"\p{L}+")] private static partial Regex IlkKelimeRegex();
    // Yan cümle sonundaki "-In"/"-yIn": ardından . , ! ? ; ya da " ve " gelir; kesme işaretli (özel ad) sözcükler hariç.
    [GeneratedRegex(@"(?<!['’\p{L}])(?<k>\p{Ll}+[ıiuü]n)(?<son>\s*[.,!?;]|\s+ve\s)")]
    private static partial Regex EmirRegex();
}
