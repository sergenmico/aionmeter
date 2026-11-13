using System.Globalization;

namespace AionMeter.Core;

/// <summary>
/// Favori bossların anahtarları ve ayar dosyasındaki yazımı. Anahtar dile bağlı değildir: takvimli bosslarda kalemin adı
/// ("t:Watcher Kaira"; takvim kodda sabit), saha bosslarında oyunun yer kimliği ("y:101004"). Ayar satırında '|' ile ayrılır
/// (takvim adlarında virgül var: "Argo, the Spirit King").
/// </summary>
public static class FavoriBoss
{
    /// <summary>Favori boss doğmadan bu kadar önce ses çalar.</summary>
    public static readonly TimeSpan UyariSuresi = TimeSpan.FromMinutes(5);

    private const char Ayirac = '|';

    public static string TakvimAnahtari(string kalemAdi) => "t:" + kalemAdi;

    public static string SahaAnahtari(int yer) => "y:" + yer.ToString(CultureInfo.InvariantCulture);

    /// <summary>Bilinen biçimde mi ("t:ad" ya da "y:sayı")? Elle bozulmuş satırlar okunurken atlanır.</summary>
    public static bool Gecerli(string anahtar)
    {
        if (anahtar.Length < 3 || anahtar[1] != ':' || anahtar.Contains(Ayirac)) return false;
        return anahtar[0] switch
        {
            't' => anahtar.AsSpan(2).Trim().Length == anahtar.Length - 2,
            'y' => int.TryParse(anahtar.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int y) && y > 0,
            _ => false,
        };
    }

    /// <summary>Ayar satırının değeri: sıralı (dosya her kayıtta aynı kalsın), '|' ile ayrılmış.</summary>
    public static string Yaz(IEnumerable<string> favoriler)
    {
        var liste = new List<string>();
        foreach (string f in favoriler)
            if (Gecerli(f)) liste.Add(f);
        liste.Sort(StringComparer.Ordinal);
        return string.Join(Ayirac, liste);
    }

    /// <summary>Ayar satırından okur; geçersiz parçalar atlanır, tekrarlar teke iner.</summary>
    public static void Oku(string deger, ISet<string> hedef)
    {
        hedef.Clear();
        foreach (string p in deger.Split(Ayirac, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Gecerli(p)) hedef.Add(p);
    }
}

/// <summary>
/// "Favori boss doğmadan 5 dk önce bir kez ses" kuralı. Her denetimde favorinin sıradaki doğuşu verilir; doğuşa 5 dk ya da daha az
/// kaldıysa ve bu doğuş için daha önce çalınmadıysa true döner. Saha bossunun doğuş anı yeni bilgiyle birkaç saniye/dakika
/// kayabilir: <see cref="AyniDogus"/> içindeki kayma aynı doğuş sayılır (ikinci kez çalmaz). Sayaç doğuşa 5 dk'dan az kala
/// açıldıysa ya da boss o sırada favorilendiyse bir kez çalar. Arayüz iş parçacığından kullanılır; iş parçacığı güvenli değildir.
/// </summary>
public sealed class FavoriZili
{
    /// <summary>Doğuş anı bu kadar kaydıysa yine aynı doğuştur (takvim bossları en sık 3 saatte bir doğar).</summary>
    public static readonly TimeSpan AyniDogus = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _calinan = new(StringComparer.Ordinal);

    /// <summary>Bu doğuş için şimdi ses çalınmalı mı? true dönerse doğuş "çalındı" diye işaretlenir.</summary>
    public bool Denetle(string anahtar, DateTime dogusUtc, DateTime utcSimdi)
    {
        var kalan = dogusUtc - utcSimdi;
        if (kalan <= TimeSpan.Zero || kalan > FavoriBoss.UyariSuresi) return false;
        if (_calinan.TryGetValue(anahtar, out var onceki) && (dogusUtc - onceki).Duration() < AyniDogus) return false;
        _calinan[anahtar] = dogusUtc;
        return true;
    }

    /// <summary>Çoktan geçmiş doğuşların kaydını bırakır (sözlük favori sayısını geçmesin).</summary>
    public void Temizle(DateTime utcSimdi)
    {
        if (_calinan.Count == 0) return;
        List<string>? sil = null;
        foreach (var (k, v) in _calinan)
            if (utcSimdi - v > AyniDogus) (sil ??= new()).Add(k);
        if (sil is not null)
            foreach (string k in sil) _calinan.Remove(k);
    }

    /// <summary>Kayıtlı doğuş sayısı (denetim için).</summary>
    public int Sayi => _calinan.Count;
}
