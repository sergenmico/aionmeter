using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace AionMeter.Ceviri;

/// <summary>
/// Yüklü OPUS-MT (Marian) modeli: SentencePiece (source.spm) + ortak sözlük (vocab.json) + iki ONNX oturumu
/// (kodlayıcı, birleşik çözücü). KV önbellekli ışın araması. Bellekte ~ birkaç yüz MB; işi bitince Dispose edilir.
/// </summary>
internal sealed class MarianModel : IDisposable
{
    // config.json / tokenizer_config.json (Helsinki-NLP/opus-mt-tc-big-en-tr)
    private const int Eos = 43741, Unk = 52508, Pad = 57059, Gizli = 1024, Bas = 16, BasBoyut = 64;

    private readonly SentencePieceTokenizer _spm;
    private readonly Dictionary<string, int> _vocab;
    private readonly string?[] _parcalar;
    private readonly InferenceSession _kodlayici, _cozucu;
    private readonly string[] _cozucuGirdiAdlari, _cozucuCiktiAdlari;
    private readonly RunOptions _ro = new();

    /// <summary>Ölçüm/deney için (Check): oturum ayarlarını değiştirir.</summary>
    internal static Action<SessionOptions>? AyarDegistir;
    /// <summary>Deney için çözücü dosya adı (varsayılan int8 birleşik çözücü).</summary>
    internal static string CozucuDosyasi = "decoder_model_merged_int8.onnx";

    /// <param name="dusukBellek">
    /// false (varsayılan): çözücüdeki int8 ortak gömme matrisi yüklemede bir kez fp32'ye açılır ("session.disable_quant_qdq").
    /// Aksi halde ONNX Runtime bu 57060×1024 matrisi HER adımda yeniden açıyor (profil: adım süresinin ~%60'ı).
    /// Ölçüm: ~5× hızlı, karşılığında bellek ~660 MB yerine ~1050 MB (model yüklüyken).
    /// </param>
    public MarianModel(string klasor, int isParcacigi, bool dusukBellek = false)
    {
        using (var fs = File.OpenRead(Path.Combine(klasor, "source.spm")))
            _spm = SentencePieceTokenizer.Create(fs, addBeginningOfSentence: false, addEndOfSentence: false);
        using (var fs = File.OpenRead(Path.Combine(klasor, "vocab.json")))
            _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(fs)!;
        _parcalar = new string?[_vocab.Values.Max() + 1];
        foreach (var (k, v) in _vocab) _parcalar[v] = k;

        SessionOptions Ayarlar(bool qdqKatla)
        {
            var so = new SessionOptions
            {
                IntraOpNumThreads = isParcacigi,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                EnableCpuMemArena = false,   // boşaltınca bellek işletim sistemine dönsün (ölçümde hız farkı yok)
                EnableMemoryPattern = false,
            };
            if (qdqKatla) so.AddSessionConfigEntry("session.disable_quant_qdq", "1");
            AyarDegistir?.Invoke(so);
            return so;
        }
        using (var so = Ayarlar(false)) _kodlayici = new InferenceSession(Path.Combine(klasor, "encoder_model_int8.onnx"), so);
        using (var so = Ayarlar(!dusukBellek)) _cozucu = new InferenceSession(Path.Combine(klasor, CozucuDosyasi), so);
        _cozucuGirdiAdlari = [.. _cozucu.InputMetadata.Keys];
        _cozucuCiktiAdlari = [.. _cozucu.OutputMetadata.Keys];
    }

    internal string ProfilBitir() => _cozucu.EndProfiling();

    public void Dispose()
    {
        _kodlayici.Dispose();
        _cozucu.Dispose();
        _ro.Dispose();
    }

    internal long[] Kodla(string metin)
    {
        var parcalar = _spm.EncodeToTokens(metin, out _, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = new List<long>(parcalar.Count + 1);
        foreach (var p in parcalar) ids.Add(_vocab.TryGetValue(p.Value, out int id) ? id : Unk);
        ids.Add(Eos);
        return [.. ids];
    }

    internal string Coz(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (int id in ids)
            if (id is not (Eos or Pad or Unk) && (uint)id < (uint)_parcalar.Length) sb.Append(_parcalar[id]);
        return sb.Replace('▁', ' ').ToString().Trim();
    }

    public string Cevir(string metin, int isin, CancellationToken ct)
    {
        long[] girdi = Kodla(metin);
        int n = girdi.Length;
        int azamiUzunluk = Math.Min(512, n * 2 + 16);
        int k = Math.Max(1, isin);

        // 1) Kodlayıcı bir kez çalışır; çıktısı ışın sayısı kadar çoğaltılır.
        float[] gizli;
        using (var idsDeger = OrtValue.CreateTensorValueFromMemory(girdi, [1, n]))
        using (var maskeDeger = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, n).ToArray(), [1, n]))
        using (var kodCikti = _kodlayici.Run(_ro, ["input_ids", "attention_mask"], [idsDeger, maskeDeger], ["last_hidden_state"]))
        {
            var g1 = kodCikti[0].GetTensorDataAsSpan<float>();
            gizli = new float[k * g1.Length];
            for (int b = 0; b < k; b++) g1.CopyTo(gizli.AsSpan(b * g1.Length));
        }
        using var gizliDeger = OrtValue.CreateTensorValueFromMemory(gizli, [k, n, Gizli]);
        using var encMaskeDeger = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, k * n).ToArray(), [k, n]);

        var diziler = new List<int>[k];
        var puan = new float[k];
        for (int b = 0; b < k; b++) { diziler[b] = []; puan[b] = b == 0 ? 0 : float.NegativeInfinity; } // ilk adımda tek ışın canlı
        var bitenler = new List<(List<int> Dizi, float Puan)>();

        var gecmis = new Dictionary<string, OrtValue>();   // past_key_values.* → değer
        var adimlik = new List<OrtValue>();                // bu adım için kopyalanan öz-dikkat önbelleği
        var sabit = new List<OrtValue>();                  // çapraz-dikkat önbelleği (ilk adımda bir kez)
        foreach (var ad in _cozucuGirdiAdlari.Where(a => a.StartsWith("past_key_values", StringComparison.Ordinal)))
        {
            var bos = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [k, Bas, 0, BasBoyut]);
            gecmis[ad] = bos; adimlik.Add(bos);
        }
        var sonToken = new long[k];
        Array.Fill(sonToken, (long)Pad); // decoder_start_token_id
        IDisposableReadOnlyCollection<OrtValue>? oncekiCikti = null;

        try
        {
            for (int adim = 0; adim < azamiUzunluk; adim++)
            {
                ct.ThrowIfCancellationRequested();
                IDisposableReadOnlyCollection<OrtValue> cikti;
                using (var tokDeger = OrtValue.CreateTensorValueFromMemory(sonToken, [k, 1]))
                using (var dalDeger = OrtValue.CreateTensorValueFromMemory(new[] { adim > 0 }, [1]))
                {
                    var girdiler = new OrtValue[_cozucuGirdiAdlari.Length];
                    for (int i = 0; i < girdiler.Length; i++)
                        girdiler[i] = _cozucuGirdiAdlari[i] switch
                        {
                            "input_ids" => tokDeger,
                            "encoder_attention_mask" => encMaskeDeger,
                            "encoder_hidden_states" => gizliDeger,
                            "use_cache_branch" => dalDeger,
                            var ad => gecmis[ad],
                        };
                    cikti = _cozucu.Run(_ro, _cozucuGirdiAdlari, girdiler, _cozucuCiktiAdlari);
                }

                // log-softmax + aday seçimi (pad asla üretilmez: bad_words_ids)
                var logit = cikti[0].GetTensorDataAsSpan<float>(); // [k,1,V]
                int V = logit.Length / k;
                var adaylar = new List<(float Puan, int Isin, int Tok)>(4 * k * k);
                for (int b = 0; b < k; b++)
                {
                    if (float.IsNegativeInfinity(puan[b])) continue;
                    var satir = logit.Slice(b * V, V);
                    float enb = float.NegativeInfinity;
                    for (int v = 0; v < V; v++) if (v != Pad && satir[v] > enb) enb = satir[v];
                    double toplam = 0;
                    for (int v = 0; v < V; v++) if (v != Pad) toplam += Math.Exp(satir[v] - enb);
                    float logZ = enb + (float)Math.Log(toplam);
                    foreach (var (v, l) in EnIyiK(satir, 2 * k, Pad)) adaylar.Add((puan[b] + l - logZ, b, v));
                }
                adaylar.Sort((a, c) => c.Puan.CompareTo(a.Puan));

                var yeniDiziler = new List<int>[k];
                var yeniPuan = new float[k];
                var kaynak = new int[k];
                int dolu = 0;
                foreach (var a in adaylar)
                {
                    if (dolu == k) break;
                    if (a.Tok == Eos || adim == azamiUzunluk - 1)
                    {
                        var d = new List<int>(diziler[a.Isin]) { a.Tok };
                        bitenler.Add((d, a.Puan / d.Count)); // length_penalty = 1
                        continue;
                    }
                    yeniDiziler[dolu] = new List<int>(diziler[a.Isin]) { a.Tok };
                    yeniPuan[dolu] = a.Puan; kaynak[dolu] = a.Isin; dolu++;
                }
                for (; dolu < k; dolu++) { yeniDiziler[dolu] = []; yeniPuan[dolu] = float.NegativeInfinity; }

                bool bitti = yeniPuan.All(float.IsNegativeInfinity);
                if (!bitti && bitenler.Count >= k)
                {
                    // Erken durma: hiçbir canlı ışın, en iyi k bitenin en kötüsünü geçemiyorsa.
                    float enKotu = bitenler.Select(x => x.Puan).OrderDescending().Take(k).Min();
                    float enIyiCanli = Enumerable.Range(0, k).Where(i => !float.IsNegativeInfinity(yeniPuan[i]))
                        .Max(i => yeniPuan[i] / yeniDiziler[i].Count);
                    bitti = enIyiCanli < enKotu;
                }
                if (bitti) { cikti.Dispose(); break; }

                // KV önbelleğini ışın kaynağına göre yeniden diz.
                foreach (var o in adimlik) o.Dispose();
                adimlik.Clear();
                bool birim = true;
                for (int i = 0; i < k; i++) birim &= kaynak[i] == i;
                for (int i = 1; i < _cozucuCiktiAdlari.Length; i++)
                {
                    string cad = _cozucuCiktiAdlari[i];                      // present.0.decoder.key
                    string gad = "past_key_values" + cad["present".Length..]; // past_key_values.0.decoder.key
                    bool capraz = cad.Contains(".encoder.", StringComparison.Ordinal);
                    if (capraz && adim > 0) continue;                         // ilk adımdan sonra sabit
                    if (!capraz && birim) { gecmis[gad] = cikti[i]; continue; } // kopyasız (çıktı bir sonraki adıma kadar yaşar)
                    var veri = cikti[i].GetTensorDataAsSpan<float>();
                    var sekil = cikti[i].GetTensorTypeAndShape().Shape;
                    int parca = veri.Length / k;
                    var yeni = new float[veri.Length];
                    for (int b = 0; b < k; b++) veri.Slice((capraz ? 0 : kaynak[b]) * parca, parca).CopyTo(yeni.AsSpan(b * parca));
                    var deger = OrtValue.CreateTensorValueFromMemory(yeni, sekil);
                    gecmis[gad] = deger;
                    (capraz ? sabit : adimlik).Add(deger);
                }
                oncekiCikti?.Dispose();
                oncekiCikti = cikti;

                diziler = yeniDiziler; puan = yeniPuan;
                for (int b = 0; b < k; b++) sonToken[b] = diziler[b].Count > 0 ? diziler[b][^1] : Pad;
            }
        }
        finally
        {
            oncekiCikti?.Dispose();
            foreach (var o in adimlik) o.Dispose();
            foreach (var o in sabit) o.Dispose();
        }

        List<int> enIyi;
        if (bitenler.Count > 0) enIyi = bitenler.MaxBy(x => x.Puan).Dizi;
        else enIyi = diziler[Array.IndexOf(puan, puan.Max())];
        return Coz(enIyi);
    }

    private static (int Tok, float Logit)[] EnIyiK(ReadOnlySpan<float> satir, int k, int haric)
    {
        var en = new (int Tok, float Logit)[k];
        int say = 0;
        for (int v = 0; v < satir.Length; v++)
        {
            if (v == haric) continue;
            float l = satir[v];
            if (say < k)
            {
                int j = say++;
                while (j > 0 && en[j - 1].Logit < l) { en[j] = en[j - 1]; j--; }
                en[j] = (v, l);
                continue;
            }
            if (l <= en[k - 1].Logit) continue;
            int t = k - 1;
            while (t > 0 && en[t - 1].Logit < l) { en[t] = en[t - 1]; t--; }
            en[t] = (v, l);
        }
        return say == k ? en : en[..say];
    }
}
