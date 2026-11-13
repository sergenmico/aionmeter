// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Capture/NetworkStats.cs (ping tahmini: istemcinin verisi -> sunucunun onayı,
// Karn kuralı, onayın kapsadığı en yeni parça). Değiştirildi: ayrı küçük sınıf, bağlantı başına durum, iki saat (Npcap zaman damgası +
// varış saati) ve güvenilirlik denetimi, son 5 sn'nin alt çeyreği; ağ günlüğü, kopma/duraklama ölçümleri alınmadı.
using System.Diagnostics;

namespace AionDPS.Aion2.Capture;

/// <summary>
/// Oyun bağlantısının gidiş-dönüş süresi (ping), yalnız zaten yakalanan trafikten: hiçbir paket gönderilmez.
/// Yöntem bir TCP yığınınınki gibidir: istemcinin sunucuya gönderdiği bir veri parçası ile sunucunun onu onaylayan ACK'i arasındaki süre.
/// Yakalama oyuncunun kendi bilgisayarında olduğundan bu, sunucuya giden yolun tamamıdır.
/// <para>Güvenilirlik:</para>
/// <list type="bullet">
/// <item>Karn kuralı: yeniden gönderilen parça örnek vermez; yeniden gönderim görülünce bekleyen tüm örnekler atılır (onay hangisine, belirsiz).</item>
/// <item>Her örnek iki saatle ölçülür: Npcap'in zaman damgası ve paketin bu sürece varış anı (Stopwatch). Küçük olanı alınır:
/// teslim gecikmesi ya da bozuk zaman damgası süreyi yalnız uzatabilir, kısaltamaz. Zaman damgası ters gidiyor ya da varış farkından
/// belirgin uzun çıkıyorsa (bazı Npcap zaman damgası modları) örnek "bozuk" sayılır; son 5 sn'de bozuklar %20'yi aşarsa ölçüm yok (null).</item>
/// <item>Gösterilen değer son 5 sn'deki örneklerin alt çeyreğidir (25. yüzdelik): sunucunun gecikmeli ACK'i ve tek aykırı örnek alarm vermez;
/// gerçekten kötü bir bağlantıda örneklerin hepsi yükselir. En az 3 örnek yoksa null (yanlış alarm yerine sessizlik).</item>
/// </list>
/// Yakalama iş parçacıklarından beslenir, arayüz iş parçacığından okunur (kilitli; bellek sabit: halka dizileri).
/// </summary>
internal sealed class PingOlcer
{
    public const int PencereMs = 5000;
    public const int EnAzOrnek = 3;
    private const int EnFazlaBekleyen = 64;      // bağlantı başına onay bekleyen istemci parçası
    private const int EnFazlaBaglanti = 8;
    private const int HalkaBoyu = 256;           // son örnekler (5 sn'de bundan fazlası gerekmez)
    private const double EnFazlaRttMs = 5000;    // bundan uzunu örnek değildir (bağlantı kopmuş, eski parça)
    private const double ZamanDamgasiToleransMs = 100; // Npcap süresi varış farkını bu kadar aşarsa zaman damgası bozuk
    private const long TickMs = TimeSpan.TicksPerMillisecond;

    private sealed class Baglanti
    {
        public uint IstemciSon;   // istemcinin gönderdiği en ileri sıra numarası + uzunluk
        public bool IstemciSonVar;
        public long SonGorulme;   // varış saati (tick)
        public readonly Queue<(uint Son, long Pcap, long Varis)> Bekleyen = new();
    }

    private readonly object _kilit = new();
    private readonly Dictionary<int, Baglanti> _baglantilar = new();
    private readonly long[] _ornekVaris = new long[HalkaBoyu];
    private readonly float[] _ornekMs = new float[HalkaBoyu];   // bozuk örnekte -1
    private readonly float[] _sirala = new float[HalkaBoyu];    // Ms() için yeniden kullanılan dizi
    private int _yaz, _dolu;
    private long _toplamOrnek, _toplamBozuk, _toplamYeniden;

    /// <summary>Bu sürecin tekdüze saati, TimeSpan tick'i olarak (varış anı; duvar saati ayarlarından etkilenmez).</summary>
    public static long VarisSaati() => Stopwatch.GetElapsedTime(0).Ticks;

    /// <summary>
    /// Oyun portundaki bir TCP parçası (boş ACK dahil). <paramref name="baglanti"/> iki yönde aynı olan bağlantı anahtarı,
    /// <paramref name="pcapTick"/> Npcap zaman damgası, <paramref name="varisTick"/> <see cref="VarisSaati"/> (ikisi de 100 ns tick).
    /// </summary>
    public void Gozle(int baglanti, bool sunucudan, uint sira, int uzunluk, bool ackVar, uint ack, long pcapTick, long varisTick)
    {
        lock (_kilit)
        {
            if (!_baglantilar.TryGetValue(baglanti, out Baglanti? b))
            {
                if (!sunucudan && uzunluk == 0)
                {
                    return; // istemcinin boş ACK'i: yeni bağlantı açmaya değmez
                }

                if (_baglantilar.Count >= EnFazlaBaglanti)
                {
                    EnEskiyiAt();
                }

                b = new Baglanti();
                _baglantilar[baglanti] = b;
            }

            b.SonGorulme = varisTick;
            if (!sunucudan)
            {
                if (uzunluk <= 0)
                {
                    return;
                }

                uint son = unchecked(sira + (uint)uzunluk);
                bool yeniden = b.IstemciSonVar && (int)(sira - b.IstemciSon) < 0; // daha önce gönderilmiş baytları taşıyor
                if (yeniden)
                {
                    b.Bekleyen.Clear(); // Karn: bundan sonraki onay hangi gönderime, belirsiz
                    _toplamYeniden++;
                }

                if (!b.IstemciSonVar || (int)(son - b.IstemciSon) > 0)
                {
                    b.IstemciSon = son;
                    b.IstemciSonVar = true;
                }

                if (!yeniden)
                {
                    b.Bekleyen.Enqueue((son, pcapTick, varisTick));
                    while (b.Bekleyen.Count > EnFazlaBekleyen)
                    {
                        b.Bekleyen.Dequeue();
                    }
                }

                return;
            }

            if (!ackVar)
            {
                return;
            }

            // Sunucunun onayı: kapsadığı en yeni istemci parçası örneği verir (eskiler en kısa süreyi vermez).
            (uint Son, long Pcap, long Varis)? kapsanan = null;
            while (b.Bekleyen.Count > 0 && (int)(ack - b.Bekleyen.Peek().Son) >= 0)
            {
                kapsanan = b.Bekleyen.Dequeue();
            }

            if (kapsanan is not { } k)
            {
                return;
            }

            double varisMs = (double)(varisTick - k.Varis) / TickMs;
            if (varisMs < 0 || varisMs >= EnFazlaRttMs)
            {
                return;
            }

            double pcapMs = (double)(pcapTick - k.Pcap) / TickMs;
            bool bozuk = pcapMs < 0 || pcapMs > varisMs + ZamanDamgasiToleransMs;
            Ekle(varisTick, bozuk ? -1f : (float)Math.Min(pcapMs, varisMs));
        }
    }

    private void Ekle(long varis, float ms)
    {
        _ornekVaris[_yaz] = varis;
        _ornekMs[_yaz] = ms;
        _yaz = (_yaz + 1) % HalkaBoyu;
        if (_dolu < HalkaBoyu)
        {
            _dolu++;
        }

        _toplamOrnek++;
        if (ms < 0)
        {
            _toplamBozuk++;
        }
    }

    private void EnEskiyiAt()
    {
        int eski = 0;
        long en = long.MaxValue;
        foreach ((int anahtar, Baglanti b) in _baglantilar)
        {
            if (b.SonGorulme < en)
            {
                en = b.SonGorulme;
                eski = anahtar;
            }
        }

        _baglantilar.Remove(eski);
    }

    /// <summary>Son 5 sn'nin pingi (ms, alt çeyrek); yeterli ya da güvenilir örnek yoksa null.</summary>
    public int? Ms() => Ms(VarisSaati());

    /// <summary>Denetim için: verilen varış anına göre.</summary>
    public int? Ms(long simdiTick)
    {
        lock (_kilit)
        {
            long sinir = simdiTick - PencereMs * TickMs;
            int iyi = 0, bozuk = 0;
            for (int i = 0; i < _dolu; i++)
            {
                if (_ornekVaris[i] < sinir || _ornekVaris[i] > simdiTick)
                {
                    continue;
                }

                if (_ornekMs[i] < 0)
                {
                    bozuk++;
                }
                else
                {
                    _sirala[iyi++] = _ornekMs[i];
                }
            }

            if (iyi < EnAzOrnek || bozuk * 5 > iyi + bozuk)
            {
                return null;
            }

            Array.Sort(_sirala, 0, iyi);
            return (int)Math.Round(_sirala[(iyi - 1) / 4]);
        }
    }

    /// <summary>Tanı kaydı için: açılıştan beri örnek, bozuk örnek ve yeniden gönderim sayıları.</summary>
    public (long Ornek, long Bozuk, long Yeniden) Sayaclar()
    {
        lock (_kilit)
        {
            return (_toplamOrnek, _toplamBozuk, _toplamYeniden);
        }
    }
}
