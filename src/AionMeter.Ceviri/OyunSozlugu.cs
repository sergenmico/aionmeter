using System.Text.Json;
using System.Text.RegularExpressions;

namespace AionMeter.Ceviri;

/// <summary>
/// Oyun sözlüğü. Oyun terimleri İngilizce kalır (kullanıcı kararı; oyunun İngilizce arayüzüyle tutarlı).
/// 1) <c>koru</c>: her yerde korunan terimler (Daeva, Elyos, Magic Attack, Greatsword, yer/boss adları, HP/DPS).
/// 2) <c>koruBuyukHarf</c>: sıradan İngilizce sözcük olan terimler (Attack, Sword, Block): sadece cümle ortasında
///    Büyük Harfle yazılınca ya da satırın tamamıysa korunur ("Block the attack." çevrilir, "Increases Block by 5" korunur).
/// 3) <c>beceriDosyasi</c>: beceri adları (skill_names.json), İLK KULLANIMDA okunur. Çok kelimeli adlar aynen korunur;
///    tek kelimelik adlar 2. kuralla; <c>yaygin</c> listesindeki sıradan sözcükler (Dash, Rest...) hiç korunmaz.
/// 4) <c>terimler</c>: düz arayüz sözcükleri için sabit karşılık; sadece satırın TAMAMI terimse (ya da terim + sayı).
/// Korunan terim çeviriden önce yer tutucuya ("ZxA") çevrilir, sonra geri konur ve Türkçe eki uydurulur.
/// {0}, %d ve sayılar doğrulanır: çeviride kaybolursa o cümle İngilizce gösterilir (bozuk çeviri yerine).
/// </summary>
public sealed partial class OyunSozlugu
{
    private sealed class Dosya
    {
        public List<string>? koru { get; set; }
        public List<string>? koruBuyukHarf { get; set; }
        public string? beceriDosyasi { get; set; }
        public List<string>? yaygin { get; set; }
        public Dictionary<string, string>? terimler { get; set; }
    }

    /// <summary>Çeviriye hazırlanmış cümle (iç kullanım).</summary>
    /// <param name="Degerler">Yer tutucu sırasıyla geri konacak değerler (oyun terimleri + {0}/%d).</param>
    /// <param name="OyunTerimleri">Sadece oyun terimleri (kullanıcıya "korunan" diye gösterilir).</param>
    /// <param name="Sayilar">Çeviride aynen bulunması gereken sayılar.</param>
    internal sealed record Hazirlik(string ModelGirdisi, IReadOnlyList<string> Degerler, IReadOnlyList<string> OyunTerimleri, IReadOnlyList<string> Sayilar);

    private readonly Lazy<TerimEslestirici> _eslestirici;
    private readonly Dictionary<string, string> _terimler; // anahtar büyük/küçük harf duyarsız
    private static readonly string YerTutucuOnEki = "Zx";

    /// <summary>Korunan terim sayısı (beceri adları dahil; ilk erişimde beceri dosyasını okur).</summary>
    public int KorunanSayisi => _eslestirici.Value.Sayi;
    public int TerimSayisi => _terimler.Count;
    /// <summary>Sözlük içeriğinin özeti: değişince eski önbellek kayıtları geçersiz sayılır.</summary>
    internal string Imza { get; }

    public OyunSozlugu(IEnumerable<string> koru, IReadOnlyDictionary<string, string>? terimler = null)
        : this(koru, [], null, [], terimler, "") { }

    private OyunSozlugu(IEnumerable<string> koru, IEnumerable<string> koruBuyukHarf, string? beceriDosyasi,
        IEnumerable<string> yaygin, IReadOnlyDictionary<string, string>? terimler, string imza)
    {
        var koruL = koru.ToList(); var buyukL = koruBuyukHarf.ToList(); var yayginK = yaygin.ToHashSet(StringComparer.Ordinal);
        _eslestirici = new Lazy<TerimEslestirici>(() =>
        {
            var e = new TerimEslestirici();
            foreach (var t in koruL) if (!string.IsNullOrWhiteSpace(t)) e.Ekle(t, TerimEslestirici.Kural.Her);
            foreach (var t in buyukL) if (!string.IsNullOrWhiteSpace(t)) e.Ekle(t, TerimEslestirici.Kural.BuyukHarfOrtada);
            if (beceriDosyasi != null && File.Exists(beceriDosyasi)) BecerileriEkle(e, beceriDosyasi, yayginK);
            return e;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _terimler = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (en, tr) in terimler ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(en)) _terimler[Normal(en)] = tr;
        Imza = imza;
    }

    public static OyunSozlugu Yukle(string jsonYolu)
    {
        string json = File.ReadAllText(jsonYolu);
        var d = JsonSerializer.Deserialize<Dosya>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new Dosya();
        string? beceri = d.beceriDosyasi == null ? null
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonYolu))!, d.beceriDosyasi));
        string imza = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16];
        return new OyunSozlugu(d.koru ?? [], d.koruBuyukHarf ?? [], beceri, d.yaygin ?? [], d.terimler, imza);
    }

    /// <summary>
    /// skill_names.json ({ "id": "Ad" }): tekilleştirilir; " [Rampage]" gibi ekler atılır; iç/teknik adlar
    /// (alt çizgi, parantez, %, =, CamelCase, 6+ kelime, küçük harfli açıklama cümleleri) alınmaz.
    /// </summary>
    private static void BecerileriEkle(TerimEslestirici e, string yol, HashSet<string> yaygin)
    {
        using var fs = File.OpenRead(yol);
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(fs);
        if (d == null) return;
        var kucukIzinli = new HashSet<string>(["of", "the", "and", "a", "an", "in", "on", "to", "for", "with", "from", "at", "by"]);
        foreach (var ham in d.Values.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(ham)) continue;
            string ad = KoseliEkRegex().Replace(ham.Trim(), "");
            if (ad.Length < 3 || TeknikRegex().IsMatch(ad) || CamelRegex().IsMatch(ad) || char.IsDigit(ad[0])) continue;
            var kelimeler = ad.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (kelimeler.Length > 5) continue;
            if (kelimeler.Length == 1)
            {
                if (!yaygin.Contains(ad)) e.Ekle(ad, TerimEslestirici.Kural.BuyukHarfOrtada);
                continue;
            }
            if (kelimeler.Any(k => char.IsLower(k[0]) && !kucukIzinli.Contains(k))) continue;
            e.Ekle(ad, TerimEslestirici.Kural.Her);
        }
    }

    public static OyunSozlugu Bos { get; } = new([]);

    private static string Normal(string s) => BoslukRegex().Replace(s.Trim(), " ");

    /// <summary>Satırın tamamı sabit terimse (ya da "terim: +12%" biçimindeyse) Türkçesini döner, yoksa null.</summary>
    public string? SabitCeviri(string satir)
    {
        string s = Normal(satir).TrimEnd('.');
        if (s.Length == 0) return null;
        if (_terimler.TryGetValue(s, out var tr)) return tr;
        var m = StatRegex().Match(s);
        if (m.Success && _terimler.TryGetValue(m.Groups[1].Value.Trim(), out tr)) {
            string deger = m.Groups[2].Value.Trim();
            return deger.StartsWith(':') ? tr + ": " + deger[1..].Trim() : tr + " " + deger;
        }
        return null;
    }

    internal Hazirlik Hazirla(string cumle)
    {
        var degerler = new List<string>();
        var oyunTerimleri = new List<string>();
        int Ekle(string d) { int i = degerler.IndexOf(d); if (i < 0) { degerler.Add(d); i = degerler.Count - 1; } return i; }

        // {0}, %d gibi biçim yer tutucuları: model "{" karakterini tanımıyor, yer tutucuyla korunur.
        string girdi = BicimRegex().Replace(cumle, m => YerTutucu(Ekle(m.Value)));
        var bulunan = _eslestirici.Value.Bul(girdi);
        if (bulunan.Count > 0)
        {
            var sb = new System.Text.StringBuilder(girdi.Length);
            int son = 0;
            foreach (var e in bulunan)
            {
                sb.Append(girdi, son, e.Bas - son);
                if (!oyunTerimleri.Contains(e.Ad)) oyunTerimleri.Add(e.Ad);
                sb.Append(YerTutucu(Ekle(e.Ad)));
                son = e.Bas + e.Uzunluk;
            }
            sb.Append(girdi, son, girdi.Length - son);
            girdi = sb.ToString();
        }
        var sayilar = SayiRegex().Matches(cumle).Select(m => m.Value).ToList();
        return new Hazirlik(girdi, degerler, oyunTerimleri, sayilar);
    }

    /// <summary>
    /// Yer tutucuları geri koyar ve Türkçe eki terime uydurur ("Sanctum'ya" → "Sanctum'a").
    /// Bir terim, biçim yer tutucusu ya da sayı kaybolduysa null döner (bozuk çeviri yerine İngilizce gösterilsin).
    /// </summary>
    internal string? Tamamla(string ceviri, Hazirlik h)
    {
        for (int i = h.Degerler.Count - 1; i >= 0; i--)
        {
            // Model araya boşluk koyabilir ya da harf büyüklüğünü değiştirebilir: esnek eşleştir. Ardından gelen ek yakalanır.
            var yt = YerTutucu(i);
            var desen = new Regex(string.Join(@"\s?", yt.Select(c => Regex.Escape(c.ToString()))) + @"(?-i:(?:['’]?(?<ek>\p{Ll}+))?(?![\p{Ll}\p{N}]))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!desen.IsMatch(ceviri)) return null;
            string deger = h.Degerler[i];
            ceviri = desen.Replace(ceviri, m =>
            {
                var ek = m.Groups["ek"];
                if (!ek.Success) return deger;
                if (ek.Value == "s") return deger + "s"; // model çoğulu çevirmeden bıraktı
                string yeniEk = char.IsLetter(deger[^1]) ? TurkceEk.Uydur(deger, ek.Value.ToLowerInvariant()) : ek.Value;
                return deger + "'" + yeniEk;
            });
        }
        if (ceviri.Contains(YerTutucuOnEki, StringComparison.OrdinalIgnoreCase)) return null; // fazladan uydurulmuş yer tutucu
        var cikanSayilar = RakamGruplari(ceviri);
        foreach (var s in h.Sayilar)
            // Türkçede ayırıcı değişebilir (10,000 → 10.000): rakamları karşılaştır.
            if (!cikanSayilar.Contains(new string(s.Where(char.IsDigit).ToArray()))) return null;
        // Model binlik ayırıcıyı bazen düşürüyor ("3,500" → "3500"): Türkçe biçimle geri koy ("3.500").
        foreach (var s in h.Sayilar)
        {
            if (!BinlikRegex().IsMatch(s)) continue;
            string duz = s.Replace(",", ""), tr = s.Replace(",", ".");
            ceviri = Regex.Replace(ceviri, @"(?<![\d.,])" + duz + @"(?![\d])", tr);
        }
        return ceviri;
    }

    private static HashSet<string> RakamGruplari(string s) =>
        SayiRegex().Matches(s).Select(m => new string(m.Value.Where(char.IsDigit).ToArray())).ToHashSet();

    /// <summary>A, B, C ... harfli yer tutucu (rakam sayı gibi çevrilebilir). Ölçümle seçildi: bkz. Check çıktısı.</summary>
    private static string YerTutucu(int i) =>
        YerTutucuOnEki + (char)('A' + i % 26) + (i >= 26 ? ((char)('A' + i / 26 - 1)).ToString() : "");

    [GeneratedRegex(@"\s+")] private static partial Regex BoslukRegex();
    [GeneratedRegex(@"^([A-Za-z][A-Za-z '\-]*?)\s*(:?\s*[+\-]?\d[\d.,]*\s*(?:%|s|sec|m|min|h)?)$")] private static partial Regex StatRegex();
    [GeneratedRegex(@"\{\d+\}|%\d*\$?[sdf]")] private static partial Regex BicimRegex();
    [GeneratedRegex(@"\d[\d.,]*\d|\d")] private static partial Regex SayiRegex();
    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")] private static partial Regex KoseliEkRegex();
    [GeneratedRegex(@"[_()=%\[\]{}<>/\#!?.,;]")] private static partial Regex TeknikRegex();
    [GeneratedRegex(@"[a-z][A-Z]")] private static partial Regex CamelRegex();
    [GeneratedRegex(@"^\d{1,3}(,\d{3})+$")] private static partial Regex BinlikRegex();
}
