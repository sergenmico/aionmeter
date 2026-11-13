namespace AionMeter;

/// <summary>
/// Bir değerin hedefe geçişi: ease-out cubic (hızlı başlar, hedefte yumuşak ve net durur, aşma/sekme yok).
/// Yeni hedef gelince geçiş o anki gösterilen değerden başlar (kesintisiz). Süre dolunca değer TAM hedeftir.
/// </summary>
internal struct Gecis
{
    public double Deger, Hedef;
    private double _bas;
    private long _basMs;
    private int _sureMs;
    public bool Aktif;

    public void Ani(double v)
    {
        Deger = Hedef = _bas = v;
        Aktif = false;
    }

    public void Hedefle(double hedef, long simdiMs, int sureMs)
    {
        if (hedef == Hedef && (Aktif || Deger == hedef)) return; // aynı hedef: süren geçiş bozulmaz
        _bas = Deger;
        Hedef = hedef;
        _basMs = simdiMs;
        _sureMs = Math.Max(1, sureMs);
        Aktif = _bas != hedef;
        if (!Aktif) Deger = hedef;
    }

    /// <summary>Geçişi ilerletir. Değer bu adımda değiştiyse (son adım, hedefe oturma dahil) true; sürüp sürmediği <see cref="Aktif"/>.</summary>
    public bool Ilerle(long simdiMs)
    {
        if (!Aktif) return false;
        double t = (simdiMs - _basMs) / (double)_sureMs;
        if (t >= 1.0)
        {
            Deger = Hedef;
            Aktif = false;
            return true;
        }
        if (t < 0) t = 0;
        double u = 1.0 - t;
        Deger = _bas + (Hedef - _bas) * (1.0 - u * u * u);
        return true;
    }
}

/// <summary>
/// Bir oyuncunun satırdaki akan değerleri (oyuncu kimliğine bağlı: sıra değişince değerler satırla birlikte gider, sıfırdan akmaz).
/// Ara kare metinleri önceden ayrılmış tamponlara yazılır (karede tahsis yok); geçiş bitince satır kesin metni (SatirModel) çizer.
/// </summary>
internal sealed class Akis
{
    public long Id;
    public int Nesil;     // son model kurulumunda görüldü mü
    public Gecis Dps, Dps2, Toplam, Pay, Oran, Y;
    public bool PayVar;   // pay "—" değilse
    public bool YuvaVar;  // sıralı listede bir yeri var (yoksa listeye girerken kaymadan yerine konur)
    public int SiraliNesil;
    public int Ok;        // anlık DPS oku: +1 ▲, −1 ▼, 0 yok (histerezis için oyuncuya bağlı tutulur, DpsEgilimi)
    // Son İlerle adımında ne değişti (kare zamanlayıcısı sadece değişen alanı yeniden çizer)
    public bool SayiDegisti, OranDegisti, YDegisti;
    public readonly char[] DpsB = new char[32], Dps2B = new char[32], ToplamB = new char[32], PayB = new char[16];
    public int DpsN, Dps2N, ToplamN, PayN;

    public bool Surer => Dps.Aktif || Dps2.Aktif || Toplam.Aktif || Pay.Aktif || Oran.Aktif || Y.Aktif;

    public void Hepsini(double dps, double dps2, double toplam, double pay, double oran, double y)
    {
        Dps.Ani(dps); Dps2.Ani(dps2); Toplam.Ani(toplam); Pay.Ani(pay); Oran.Ani(oran); Y.Ani(y);
    }

    /// <summary>İlerletir ve süren sayıların ara metnini tamponlara yazar.</summary>
    public void Ilerle(long simdiMs)
    {
        bool d = Dps.Ilerle(simdiMs), d2 = Dps2.Ilerle(simdiMs), t = Toplam.Ilerle(simdiMs), p = Pay.Ilerle(simdiMs);
        OranDegisti = Oran.Ilerle(simdiMs);
        YDegisti = Y.Ilerle(simdiMs);
        SayiDegisti = d || d2 || t || (p && PayVar);
        if (d) DpsN = Tam(DpsB, Dps.Deger);
        if (d2) Dps2N = Tam(Dps2B, Dps2.Deger);
        if (t) ToplamN = Tam(ToplamB, Toplam.Deger);
        if (p && PayVar) PayN = Yuzde(PayB, Pay.Deger);
    }

    /// <summary>Tam sayı + binlik ayırıcı (Bicim.Tam ile aynı; v önce yuvarlanır, eksi/NaN 0). Tampona yazar, uzunluğu döner.</summary>
    public static int Tam(Span<char> b, double v)
    {
        long n = v <= 0 || double.IsNaN(v) ? 0 : (long)Math.Round(v);
        return n.TryFormat(b, out int k, "N0", Bicim.Tr) ? k : 0;
    }

    /// <summary>"%12,3" (Bicim.Yuzde ile aynı biçim; v yüzde cinsinden).</summary>
    private static int Yuzde(char[] b, double v)
    {
        b[0] = '%';
        return v.TryFormat(b.AsSpan(1), out int k, "0.0", Bicim.Tr) ? k + 1 : 0;
    }

    /// <summary>"%66,8" (Bicim.CanYuzde ile aynı: bir ondalığa aşağı yuvarlanır; oran 0..1).</summary>
    public static int CanYuzde(Span<char> b, double oran)
    {
        if (double.IsNaN(oran) || oran < 0) oran = 0;
        double binde = Math.Floor(Math.Min(oran, 1.0) * 1000.0);
        b[0] = '%';
        return (binde / 10.0).TryFormat(b[1..], out int k, "0.0", Bicim.Tr) ? k + 1 : 0;
    }

    /// <summary>Tam sayı + sabit son ek (" /sn", " / 7.200.000"): Bicim.Dps / boss can metniyle aynı.</summary>
    public static int TamEkli(Span<char> b, double v, ReadOnlySpan<char> ek)
    {
        int n = Tam(b, v);
        if (n + ek.Length > b.Length) return n;
        ek.CopyTo(b[n..]);
        return n + ek.Length;
    }
}
