using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AionMeter.Ceviri;

/// <summary>Bir çevirinin sonucu.</summary>
/// <param name="KorunanTerimler">Çeviriden korunan oyun terimleri (Daeva, Elyos, ...).</param>
/// <param name="OnbellektenGeldi">Model hiç çalışmadı: tüm cümleler önbellekten ya da sabit terim sözlüğünden geldi.</param>
public sealed record CeviriSonucu(string Ingilizce, string Turkce, IReadOnlyList<string> KorunanTerimler, bool OnbellektenGeldi, TimeSpan Sure);

public interface ICevirmen : IDisposable
{
    Task<CeviriSonucu> CevirAsync(string ingilizce, CancellationToken ct = default);
    /// <summary>Model dosyaları diskte tam (indirilmiş). Bellekte yüklü olması gerekmez.</summary>
    bool ModelHazir { get; }
}

/// <summary>
/// Çevrimdışı İngilizce→Türkçe çevirmen: OPUS-MT tc-big en-tr (Marian), ONNX Runtime (CPU), int8.
/// Model ilk gerekli çeviride yüklenir; <see cref="BostaBekleme"/> (120 sn) kullanılmazsa kendiliğinden boşaltılır.
/// Metin satır satır, satırlar cümle cümle çevrilir; her cümle önce önbellekte aranır.
/// </summary>
public sealed partial class CevrimdisiCevirmen : ICevirmen
{
    private readonly string _klasor;
    private readonly OyunSozlugu _sozluk;
    private readonly CeviriOnbellegi _onbellek;
    private readonly SemaphoreSlim _kilit = new(1, 1);
    private readonly Timer _bosZamanlayici;
    private MarianModel? _model;
    private volatile bool _atildi, _bosaltIstegi;
    private readonly CancellationTokenSource _kapanis = new();

    /// <summary>Işın genişliği (1 = açgözlü, en hızlı). Varsayılan 2: ölçümde kalite/hız dengesi.</summary>
    public int Isin { get; set; } = 2;
    /// <summary>ONNX Runtime iş parçacığı sayısı. Ölçümde 2'den fazlası hızlandırmadı; oyunu boğmasın diye 2.</summary>
    public int IsParcacigi { get; set; } = Math.Min(2, Environment.ProcessorCount);
    /// <summary>true: model yüklüyken ~660 MB ama ~5× yavaş; false (varsayılan): ~1050 MB, cümle başına ~0,2 sn. Yüklemeden önce ayarlanır.</summary>
    public bool DusukBellek { get; set; }
    /// <summary>Çıktıyı tutarlı "sen" diline çevir (kullanamazsınız → kullanamazsın, toplayın → topla). Varsayılan açık.</summary>
    public bool SenBicimi { get; set; } = true;
    public TimeSpan BostaBekleme { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>Model şu an bellekte mi.</summary>
    public bool Yuklu => _model != null;
    public bool ModelHazir => ModelIndirici.HazirMi(_klasor);
    /// <summary>Son model yükleme süresi (ms).</summary>
    public long SonYuklemeMs { get; private set; }

    public CevrimdisiCevirmen(string modelKlasoru, OyunSozlugu sozluk, CeviriOnbellegi onbellek)
    {
        _klasor = modelKlasoru;
        _sozluk = sozluk;
        _onbellek = onbellek;
        // Sözlük ya da düzeltme kuralları değiştiyse eski önbellek kayıtları geçersiz.
        _onbellek.SurumAyarla(sozluk.Imza + "|" + TurkceDuzelt.Surum);
        _bosZamanlayici = new Timer(_ => BostaBosalt(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task<CeviriSonucu> CevirAsync(string ingilizce, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_atildi, this);
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(ingilizce)) return new(ingilizce ?? "", "", [], true, sw.Elapsed);

        using var bagli = CancellationTokenSource.CreateLinkedTokenSource(ct, _kapanis.Token);
        ct = bagli.Token;
        await _kilit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ZamanlayiciKur(Timeout.InfiniteTimeSpan);
            return await Task.Run(() =>
            {
                var korunan = new List<string>();
                bool modelCalisti = false;
                var cikti = new StringBuilder();
                foreach (var satir in ingilizce.Replace("\r", "").Split('\n'))
                {
                    if (cikti.Length > 0) cikti.Append('\n');
                    bool ilk = true;
                    foreach (var cumle in Cumleler(satir))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!ilk) cikti.Append(' ');
                        ilk = false;
                        cikti.Append(CumleCevir(cumle, korunan, ref modelCalisti, ct));
                    }
                }
                return new CeviriSonucu(ingilizce, cikti.ToString(), korunan, !modelCalisti, sw.Elapsed);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Çeviri sürerken Dispose/Bosalt istendiyse modeli şimdi bırak (onlar UI'ı bekletmedi).
                if (_atildi || _bosaltIstegi) { _bosaltIstegi = false; BellektenAt(); }
                else ZamanlayiciKur(BostaBekleme);
            }
            finally { _kilit.Release(); }
        }
    }

    private void ZamanlayiciKur(TimeSpan sure)
    {
        try { _bosZamanlayici.Change(sure, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private string CumleCevir(string cumle, List<string> korunan, ref bool modelCalisti, CancellationToken ct)
    {
        if (_sozluk.SabitCeviri(cumle) is { } sabit) return sabit;
        var h = _sozluk.Hazirla(cumle);
        foreach (var t in h.OyunTerimleri) if (!korunan.Contains(t)) korunan.Add(t);
        if (!HarfVarRegex().IsMatch(h.ModelGirdisi.Replace("Zx", ""))) return cumle; // sadece sayı/terim: çevrilecek bir şey yok
        if (_onbellek.TryGet(cumle, out var onceki)) return onceki;

        modelCalisti = true;
        _model ??= Yukle();
        string ham = _model.Cevir(TurkceDuzelt.Once(h.ModelGirdisi), Isin, ct);
        string? tr = _sozluk.Tamamla(ham, h);
        if (tr == null)
        {
            // Korunan terim/sayı kayboldu: bozuk çeviri yerine İngilizce. Bu da önbelleğe girer (her üzerine gelişte
            // model yeniden çalışmasın); önbellek sözlük/kural sürümüyle etiketli, sürüm değişince temizlenir.
            _onbellek.Ekle(cumle, cumle);
            return cumle;
        }
        if (SenBicimi) tr = TurkceDuzelt.SenBicimi(tr, cumle);
        _onbellek.Ekle(cumle, tr);
        return tr;
    }

    /// <summary>Modeli bellekten atar (ONNX oturumları kapanır). Sonraki çeviride yeniden yüklenir.</summary>
    /// <remarks>UI iş parçacığını en çok ~200 ms bekletir: o sırada çeviri/yükleme sürüyorsa boşaltma, çeviri bitince yapılır.</remarks>
    public void Bosalt()
    {
        if (!_kilit.Wait(200)) { _bosaltIstegi = true; return; }
        try { BellektenAt(); }
        finally { _kilit.Release(); }
    }

    private void BostaBosalt()
    {
        if (!_kilit.Wait(0)) return; // çeviri sürüyor; bitince zamanlayıcı yeniden kurulur
        try { BellektenAt(); }
        finally { _kilit.Release(); }
    }

    private void BellektenAt()
    {
        if (_model == null) return;
        _model.Dispose();
        _model = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <remarks>
    /// Bloklamaz (en çok ~200 ms): çeviri sürüyorsa iptal edilir; model yükleniyorsa (iptal edilemez) yükleme bitince
    /// çevirinin finally bloğu modeli bırakır.
    /// </remarks>
    public void Dispose()
    {
        if (_atildi) return;
        _atildi = true;
        try { _kapanis.Cancel(); } catch (ObjectDisposedException) { }
        _bosZamanlayici.Dispose();
        Bosalt();
        try { _onbellek.Kaydet(); } catch (ObjectDisposedException) { }
    }

    private MarianModel Yukle()
    {
        var sw = Stopwatch.StartNew();
        var m = new MarianModel(_klasor, IsParcacigi, DusukBellek);
        SonYuklemeMs = sw.ElapsedMilliseconds;
        return m;
    }

    /// <summary>Marian cümle düzeyinde eğitildi; uzun paragrafı cümlelere bölmek kaliteyi korur.</summary>
    internal static IEnumerable<string> Cumleler(string metin)
    {
        metin = BoslukRegex().Replace(metin.Trim(), " ");
        foreach (var c in CumleSonuRegex().Split(metin))
            if (!string.IsNullOrWhiteSpace(c)) yield return c.Trim();
    }

    /// <summary>Ölçüm için: önbellek/sözlük olmadan ham model çevirisi (iç kullanım).</summary>
    internal string HamCevir(string cumle, int isin)
    {
        _kilit.Wait();
        try { _model ??= Yukle(); return _model.Cevir(cumle, isin, CancellationToken.None); }
        finally { _kilit.Release(); }
    }

    internal string? ProfilBitir() => _model?.ProfilBitir();

    [GeneratedRegex(@"\s+")] private static partial Regex BoslukRegex();
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[""'(\[]?[A-Z0-9])")] private static partial Regex CumleSonuRegex();
    [GeneratedRegex(@"\p{L}{2,}")] private static partial Regex HarfVarRegex();
}
