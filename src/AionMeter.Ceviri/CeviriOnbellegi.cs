using System.Text.Json;
using System.Text.RegularExpressions;

namespace AionMeter.Ceviri;

/// <summary>
/// Kalıcı İngilizce→Türkçe çeviri önbelleği (cümle düzeyinde). Anahtar normalleştirilir: kırpılır, boşluklar teke indirilir,
/// büyük/küçük harf yok sayılır. En çok <see cref="Azami"/> kayıt tutar (en eski kullanılan atılır).
/// Değişiklik varsa 60 sn'de bir ve Dispose'da diske yazar (önce .tmp, sonra yer değiştirme).
/// </summary>
public sealed partial class CeviriOnbellegi : IDisposable
{
    private readonly string _yol;
    private readonly object _kilit = new();
    private readonly Dictionary<string, LinkedListNode<(string Anahtar, string Ingilizce, string Turkce)>> _harita = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Anahtar, string Ingilizce, string Turkce)> _sira = new(); // baş = en yeni
    private readonly Timer _zamanlayici;
    private bool _kirli, _atildi;
    private string _surum = "";

    public int Azami { get; }
    public int Sayi { get { lock (_kilit) return _harita.Count; } }

    public static string VarsayilanYol { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aionmeter", "ceviri-onbellek.json");

    public CeviriOnbellegi(string dosyaYolu) : this(dosyaYolu, 5000) { }

    public CeviriOnbellegi(string dosyaYolu, int azami)
    {
        _yol = dosyaYolu;
        Azami = Math.Max(1, azami);
        Oku();
        _zamanlayici = new Timer(_ => Kaydet(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    internal static string Anahtar(string ingilizce) => BoslukRegex().Replace(ingilizce.Trim(), " ").ToLowerInvariant();

    public bool TryGet(string ingilizce, out string turkce)
    {
        lock (_kilit)
        {
            if (_harita.TryGetValue(Anahtar(ingilizce), out var dugum))
            {
                _sira.Remove(dugum); _sira.AddFirst(dugum);
                turkce = dugum.Value.Turkce;
                return true;
            }
        }
        turkce = "";
        return false;
    }

    public void Ekle(string ingilizce, string turkce)
    {
        string a = Anahtar(ingilizce);
        if (a.Length == 0) return;
        lock (_kilit)
        {
            if (_harita.TryGetValue(a, out var eski)) _sira.Remove(eski);
            _harita[a] = _sira.AddFirst((a, ingilizce.Trim(), turkce));
            while (_harita.Count > Azami)
            {
                var son = _sira.Last!;
                _sira.RemoveLast();
                _harita.Remove(son.Value.Anahtar);
            }
            _kirli = true;
        }
    }

    /// <summary>Kayıtların hangi sözlük/kural sürümüyle üretildiği. Farklıysa önbellek temizlenir (çeviren ayarlar).</summary>
    internal void SurumAyarla(string surum)
    {
        lock (_kilit)
        {
            if (_surum == surum) return;
            _harita.Clear(); _sira.Clear();
            _surum = surum; _kirli = true;
        }
    }

    public void Temizle()
    {
        lock (_kilit) { _harita.Clear(); _sira.Clear(); _kirli = true; }
    }

    public void Kaydet()
    {
        string[][] kayitlar;
        string surum;
        lock (_kilit)
        {
            if (!_kirli) return;
            // Eskiden yeniye yaz: okurken sırayla eklenince en yeni başa gelir.
            kayitlar = _sira.Reverse().Select(k => new[] { k.Ingilizce, k.Turkce }).ToArray();
            surum = _surum;
            _kirli = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_yol))!);
            string tmp = _yol + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Kayit { surum = surum, kayitlar = kayitlar }));
            File.Move(tmp, _yol, overwrite: true);
        }
        catch (IOException) { lock (_kilit) _kirli = true; }
        catch (UnauthorizedAccessException) { lock (_kilit) _kirli = true; }
    }

    private void Oku()
    {
        try
        {
            if (!File.Exists(_yol)) return;
            string json = File.ReadAllText(_yol);
            string[][]? kayitlar;
            if (json.TrimStart().StartsWith('['))
                kayitlar = JsonSerializer.Deserialize<string[][]>(json); // eski biçim (sürümsüz)
            else
            {
                var k = JsonSerializer.Deserialize<Kayit>(json);
                kayitlar = k?.kayitlar;
                _surum = k?.surum ?? "";
            }
            if (kayitlar == null) return;
            foreach (var k in kayitlar)
                if (k is [var en, var tr]) Ekle(en, tr);
            _kirli = false;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Bozuk önbellek: yok say, baştan başla.
        }
    }

    public void Dispose()
    {
        if (_atildi) return;
        _atildi = true;
        _zamanlayici.Dispose();
        Kaydet();
    }

    private sealed class Kayit
    {
        public string surum { get; set; } = "";
        public string[][]? kayitlar { get; set; }
    }

    [GeneratedRegex(@"\s+")] private static partial Regex BoslukRegex();
}
