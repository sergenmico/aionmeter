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
/// 5) <c>statlar</c>: stat adları ("Damage Boost" → "Hasar Artışı"; kullanıcı kararı: statlar Türkçe, beceri/eşya/yer adları
///    İngilizce). Cümlede yer tutucuyla korunur ve geri konurken TÜRKÇESİ yazılır (ek kesmesiz: "Hasar Artışını").
///    Sadece stat(lar) + sayılardan oluşan satır modele gitmez: "Hasar Artışı (Damage Boost) +120".
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
        public Dictionary<string, string>? statlar { get; set; }
    }

    /// <summary>Çeviriye hazırlanmış cümle (iç kullanım).</summary>
    /// <param name="Degerler">Yer tutucu sırasıyla geri konacak değerler (oyun terimleri + {0}/%d).</param>
    /// <param name="OyunTerimleri">Sadece oyun terimleri (kullanıcıya "korunan" diye gösterilir).</param>
    /// <param name="Sayilar">Çeviride aynen bulunması gereken sayılar.</param>
    /// <param name="Turkce">Degerler ile aynı sırada: değer Türkçe bir stat adı mı (ek kesmesiz, Türkçe kurallarla eklenir).</param>
    internal sealed record Hazirlik(string ModelGirdisi, IReadOnlyList<string> Degerler, IReadOnlyList<string> OyunTerimleri,
        IReadOnlyList<string> Sayilar, IReadOnlyList<bool> Turkce);

    private readonly Lazy<TerimEslestirici> _eslestirici;
    private readonly Dictionary<string, string> _terimler; // anahtar büyük/küçük harf duyarsız
    private readonly Dictionary<string, string> _statlar = new(StringComparer.Ordinal); // İngilizce stat adı → Türkçe
    private static readonly string YerTutucuOnEki = "Zx";

    /// <summary>Korunan terim sayısı (beceri adları dahil; ilk erişimde beceri dosyasını okur).</summary>
    public int KorunanSayisi => _eslestirici.Value.Sayi;
    public int TerimSayisi => _terimler.Count;
    /// <summary>Sözlük içeriğinin özeti: değişince eski önbellek kayıtları geçersiz sayılır.</summary>
    internal string Imza { get; }

    public OyunSozlugu(IEnumerable<string> koru, IReadOnlyDictionary<string, string>? terimler = null,
        IReadOnlyDictionary<string, string>? statlar = null)
        : this(koru, [], null, [], terimler, statlar, "") { }

    private OyunSozlugu(IEnumerable<string> koru, IEnumerable<string> koruBuyukHarf, string? beceriDosyasi,
        IEnumerable<string> yaygin, IReadOnlyDictionary<string, string>? terimler, IReadOnlyDictionary<string, string>? statlar,
        string imza)
    {
        var koruL = koru.ToList(); var buyukL = koruBuyukHarf.ToList(); var yayginK = yaygin.ToHashSet(StringComparer.Ordinal);
        foreach (var (en, tr) in statlar ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(en) && !string.IsNullOrWhiteSpace(tr)) _statlar[en.Trim()] = tr.Trim();
        _eslestirici = new Lazy<TerimEslestirici>(() =>
        {
            var e = new TerimEslestirici();
            // Statlar önce: tek sözcüklüler (Attack, Evasion) sıradan İngilizce sözcük olduğundan sadece Büyük Harfle cümle ortasında.
            foreach (var t in _statlar.Keys)
                e.Ekle(t, t.Contains(' ') ? TerimEslestirici.Kural.Her : TerimEslestirici.Kural.BuyukHarfOrtada);
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
        return new OyunSozlugu(d.koru ?? [], d.koruBuyukHarf ?? [], beceri, d.yaygin ?? [], d.terimler, d.statlar, imza);
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

    /// <summary>
    /// Satır sadece stat adlarından ve sayı/işaretlerden oluşuyorsa ("Critical Hit Resist +45", "Damage Boost 3.2%")
    /// her stat "Türkçe (İngilizce)" yazılır, satırın gerisi aynen kalır; model çalışmaz. Değilse null.
    /// </summary>
    public string? StatSatiri(string satir)
    {
        if (_statlar.Count == 0) return null;
        var bulunan = _eslestirici.Value.Bul(satir);
        if (!bulunan.Any(e => _statlar.ContainsKey(e.Ad)))
        {
            // Satır başındaki tek sözcüklü stat ("Cooldown: 12s") eşleştiricide sadece satırın tamamıysa korunur: burada bakılır.
            string t = satir.TrimStart();
            string? bas = _statlar.Keys.Where(k => t.StartsWith(k, StringComparison.Ordinal) &&
                                                   (t.Length == k.Length || !char.IsLetterOrDigit(t[k.Length])))
                                       .MaxBy(k => k.Length);
            if (bas is null) return null;
            bulunan = [new TerimEslestirici.Eslesme(satir.Length - t.Length, bas.Length, bas)];
        }
        var sb = new System.Text.StringBuilder(satir.Length + 32);
        int son = 0;
        foreach (var e in bulunan)
        {
            string ara = satir[son..e.Bas];
            if (HarfKaldi(ara) || !_statlar.TryGetValue(e.Ad, out var tr)) return null; // stat dışı sözcük var: cümle
            sb.Append(ara).Append(tr).Append(" (").Append(e.Ad).Append(')');
            son = e.Bas + e.Uzunluk;
        }
        string kuyruk = satir[son..];
        if (HarfKaldi(kuyruk)) return null;
        return sb.Append(kuyruk).ToString();
    }

    /// <summary>
    /// Sayılar ("12s", "1m 30s"), kısa parantezler ("(Max 30)", "(PvP)") ve birim/etiketler (sec, HP, Max, Lv) çıkarılınca
    /// harf kalıyor mu? Kalıyorsa satır stat satırı değil, cümledir.
    /// </summary>
    private static bool HarfKaldi(string s)
    {
        s = SayiBirimRegex().Replace(s, "");
        s = KisaParantezRegex().Replace(s, "");
        s = StatEtiketRegex().Replace(s, "");
        return s.Any(char.IsLetter);
    }

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

    /// <summary>
    /// OCR iyelik kesmesini "l"/"I" okur: "Balaur's" → "Balaurls". Kök bir oyun terimiyse kesmeyi geri koyar
    /// (sıradan sözcüklere dokunmaz: "Pearls", "Girls" aynen kalır).
    /// </summary>
    internal string OcrIyelikDuzelt(string cumle) =>
        OcrIyelikRegex().Replace(cumle, m =>
        {
            string kok = m.Groups[1].Value;
            foreach (var e in _eslestirici.Value.Bul(kok))
                if (e.Bas == 0 && e.Uzunluk == kok.Length) return kok + "'s";
            return m.Value;
        });

    [GeneratedRegex(@"\b(\p{Lu}\p{L}{3,})[lI]s\b")] private static partial Regex OcrIyelikRegex();

    /// <summary>
    /// Sözlükte olmayan eşya/görev adları: cümle ORTASINDA büyük harfle başlayan, art arda en az iki sözcük
    /// ("... a Balaur's Wrathful Mind.", "... with Battle Enhance Scroll") ad sayılır ve korunur. Cümle başı sayılmaz
    /// ("Used to craft" gibi). Sözlük eşleşmeleriyle çakışanlar atlanır; liste konuma göre sıralı kalır.
    /// </summary>
    /// <summary>
    /// Tek sözcüklü stat fiil olarak kullanılmış mı ("When you Parry an attack", "You will Attack the enemy")?
    /// Öncesinde özne/kip sözcüğü ya da ardından tanımlık varsa stat sayılmaz; model fiil olarak çevirir.
    /// </summary>
    private bool FiilOlarakStat(string girdi, TerimEslestirici.Eslesme e)
    {
        if (!_statlar.ContainsKey(e.Ad) || e.Ad.Contains(' ')) return false;
        var once = OncekiSozcukRegex().Match(girdi[..e.Bas]);
        if (once.Success && FiilOncesi.Contains(once.Groups[1].Value)) return true;
        var sonra = SonrakiSozcukRegex().Match(girdi[(e.Bas + e.Uzunluk)..]);
        return sonra.Success && sonra.Groups[1].Value is "a" or "an" or "the";
    }

    private static readonly HashSet<string> FiilOncesi = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "to", "will", "can", "cannot", "may", "must", "should", "would", "could", "i", "we", "they",
    };

    [GeneratedRegex(@"\([^()]{0,15}\)")] private static partial Regex KisaParantezRegex();
    [GeneratedRegex(@"\b(?:sec|secs|seconds?|min|mins|minutes?|Max|Min|HP|MP|DP|PvP|PvE|Lv|Lvl)\b")] private static partial Regex StatEtiketRegex();
    [GeneratedRegex(@"(\p{L}+)\s+$")] private static partial Regex OncekiSozcukRegex();
    [GeneratedRegex(@"^\s+(\p{L}+)")] private static partial Regex SonrakiSozcukRegex();

    private void AdDizileriniEkle(string girdi, List<TerimEslestirici.Eslesme> bulunan)
    {
        foreach (Match m in AdDizisiRegex().Matches(girdi))
        {
            int bas = m.Index, uzunluk = m.Length;
            int once = bas - 1;
            while (once >= 0 && char.IsWhiteSpace(girdi[once])) once--;
            // Cümle başı: satır başı ya da harf/rakam/virgül/kesme dışında bir işaretten sonra (". ", "• ", "» ", "1) ").
            bool cumleBasi = once < 0 || !(char.IsLetterOrDigit(girdi[once]) || girdi[once] is ',' or '\'' or '’');
            // Cümle başındaki sözcük (büyük harfi addan değil), baştaki fiil ("Grants Wrathful Mind") ve zamir/işlev sözcüğü
            // ("If You Die, You Lose It") ada katılmaz; düşürülür, kalan en az iki sözcük yeniden denenir.
            string parca = m.Value;
            int kaydir = 0;
            bool gecerli = true;
            while (true)
            {
                string ilk = AdDizisiIlkSozcukRegex().Match(parca).Value;
                if (!(cumleBasi || TurkceDuzelt.FiilMi(ilk) || AdOlamazIlk.Contains(ilk))) break;
                cumleBasi = false;
                var kalan = AdDizisiRegex().Match(parca, ilk.Length);
                if (!kalan.Success) { gecerli = false; break; }
                kaydir += kalan.Index;
                parca = kalan.Value;
            }
            if (!gecerli) continue;
            bas = m.Index + kaydir;
            uzunluk = parca.Length;
            var cakisan = bulunan.Where(e => e.Bas < bas + uzunluk && bas < e.Bas + e.Uzunluk).ToList();
            if (cakisan.Count > 0)
            {
                // Adın içindeki stat ("Greater Attack Scroll", "Blessed Health Potion"): ad daha uzunsa ve çakışanların hepsi
                // içindeki statlarsa ad kazanır (eşya adı İngilizce, bütün kalır). Aksi halde sözlük eşleşmesi kalır.
                bool adKazanir = cakisan.All(e => _statlar.ContainsKey(e.Ad) && e.Bas >= bas && e.Bas + e.Uzunluk <= bas + uzunluk &&
                                                  e.Uzunluk < uzunluk);
                if (!adKazanir) continue;
                foreach (var e in cakisan) bulunan.Remove(e);
            }
            bulunan.Add(new TerimEslestirici.Eslesme(bas, uzunluk, girdi.Substring(bas, uzunluk)));
        }
        bulunan.Sort((a, b) => a.Bas.CompareTo(b.Bas));
    }

    [GeneratedRegex(@"\d[\d.,]*\p{L}{0,3}\b")] private static partial Regex SayiBirimRegex();

    /// <summary>Ad başlatamayan zamir/işlev sözcükleri (büyük harfle yazılmış cümle parçaları: "You Lose It").</summary>
    private static readonly HashSet<string> AdOlamazIlk = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "You", "Your", "We", "Our", "It", "Its", "They", "Their", "He", "His", "She", "Her", "This", "That", "These", "Those",
        "If", "When", "Then", "And", "Or", "But", "Not", "No", "All", "Each", "Every", "Any", "Some", "There", "Here", "Is", "Are",
        "Was", "Be", "Do", "Does", "To", "For", "With", "From", "By", "On", "In", "At", "Of", "The", "A", "An",
    };

    [GeneratedRegex(@"\b\p{Lu}\p{Ll}+(?:\s+(?:of\s+|the\s+)?\p{Lu}\p{Ll}+)+\b")] private static partial Regex AdDizisiRegex();
    [GeneratedRegex(@"^\p{L}+")] private static partial Regex AdDizisiIlkSozcukRegex();

    internal Hazirlik Hazirla(string cumle)
    {
        var degerler = new List<string>();
        var turkce = new List<bool>();
        var oyunTerimleri = new List<string>();
        int Ekle(string d, bool tr = false)
        {
            int i = degerler.IndexOf(d);
            if (i < 0) { degerler.Add(d); turkce.Add(tr); i = degerler.Count - 1; }
            return i;
        }

        // {0}, %d gibi biçim yer tutucuları: model "{" karakterini tanımıyor, yer tutucuyla korunur.
        string girdi = BicimRegex().Replace(cumle, m => YerTutucu(Ekle(m.Value)));
        var bulunan = _eslestirici.Value.Bul(girdi);
        bulunan.RemoveAll(e => FiilOlarakStat(girdi, e));
        AdDizileriniEkle(girdi, bulunan);
        if (bulunan.Count > 0)
        {
            var sb = new System.Text.StringBuilder(girdi.Length);
            int son = 0;
            foreach (var e in bulunan)
            {
                sb.Append(girdi, son, e.Bas - son);
                bool stat = _statlar.TryGetValue(e.Ad, out var statTr);
                string deger = stat ? statTr! : e.Ad;
                if (!oyunTerimleri.Contains(deger)) oyunTerimleri.Add(deger);
                sb.Append(YerTutucu(Ekle(deger, stat)));
                son = e.Bas + e.Uzunluk;
            }
            sb.Append(girdi, son, girdi.Length - son);
            girdi = sb.ToString();
        }
        var sayilar = SayiRegex().Matches(cumle).Select(m => m.Value).ToList();
        return new Hazirlik(girdi, degerler, oyunTerimleri, sayilar, turkce);
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
            bool turkceDeger = h.Turkce[i];
            ceviri = desen.Replace(ceviri, m =>
            {
                var ek = m.Groups["ek"];
                if (!ek.Success) return deger;
                if (turkceDeger) return ek.Value == "s" ? deger : TurkceEk.TurkceAdaEkle(deger, ek.Value.ToLowerInvariant());
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
