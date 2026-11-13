// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2EntityDirectory.cs LocalPosition / BestPosition / GuessIsPlausible /
// NoteAround / EnvironmentEstimateLocked / Around (Trimmed) ve FixTrust. Değiştirildi: yalnız Gizli Küp yön/mesafesi için gereken kısım;
// harita, kayıt ve ölçüm modu yok; saat Environment.TickCount64 (denetimde değiştirilebilir); tahsis yapmadan orta nokta.
// Birim: oyun dünya birimi, 100 birim = 1 metre (referans Aion2EntityDirectory.cs LocalPosition özeti, PetMapController.cs AnnounceCubes özeti).

namespace AionDPS.Aion2;

/// <summary>
/// Yerel oyuncunun yeri. Sunucu oyuncunun kendi yerini yalnız ara sıra bildirir (beceri, vuruş, düzeltme: konum çerçevesi; yerel oyuncunun
/// koyduğu/yenilediği her etkinin sonundaki x, y, z; bölge çerçevesi). Kesin bildirim <see cref="KesinGuven"/> boyunca olduğu gibi alınır;
/// sonra çevredeki varlıkların (menzildeki canavar ve oyuncular, doğuş ve hareket çerçeveleriyle) orta noktası daha iyi tahmindir
/// (referansın kayıtlardaki ölçümü: medyanda ~12-15 m sapma). İş parçacığı güvenli: yazan çözücü, okuyan anlık görüntü.
/// </summary>
public sealed class Aion2Konum
{
    private readonly object _kilit = new();
    private (float X, float Y, long Tik)? _kesin;
    private readonly Dictionary<int, (float X, float Y, long Tik)> _cevre = new();
    private float[] _xs = new float[64], _ys = new float[64], _sx = new float[64], _sy = new float[64]; // yeniden kullanılan tamponlar

    /// <summary>Kesin bildirim bu kadar süre olduğu gibi alınır (referans FixTrust: 4 sn).</summary>
    internal const long KesinGuvenMs = 4000;

    /// <summary>Bundan eski bir yer hiç kullanılmaz (referans radar: 5 sn'den eski yerle mesafe hesaplanmaz, 8 sn sonra susar).</summary>
    internal const long EnFazlaYasMs = 8000;

    private const long CevreYasMs = 5000;      // referans AroundMaxAge
    private const int CevreEnAz = 3;           // referans AroundMinEntities
    private const float KirpmaBirim = 4500f;   // medyandan 45 m içindekilerin ortalaması (referans Trimmed)
    private const int CevreSinir = 1500;

    /// <summary>Milisaniye saati; denetimde sabit bir saat verilir.</summary>
    internal Func<long> Saat { get; set; } = () => Environment.TickCount64;

    /// <summary>Oyuncunun kendi yeri kesin bildirildi (dünya birimi).</summary>
    public void Kesin(float x, float y)
    {
        if (!Makul(x, y))
        {
            return;
        }

        lock (_kilit)
        {
            _kesin = (x, y, Saat());
        }
    }

    /// <summary>Çevredeki bir varlık (yerel oyuncu değil) bu yerde: doğuş ya da hareket çerçevesi.</summary>
    public void Cevre(int varlik, float x, float y)
    {
        if (!Makul(x, y))
        {
            return;
        }

        lock (_kilit)
        {
            if (_cevre.Count >= CevreSinir && !_cevre.ContainsKey(varlik))
            {
                _cevre.Clear(); // bu kadarı hiç yakında olmaz: kaldırmalar kaybolmuş
            }

            _cevre[varlik] = (x, y, Saat());
        }
    }

    /// <summary>Varlık kayboldu (öldü, menzilden çıktı).</summary>
    public void Unut(int varlik)
    {
        lock (_kilit)
        {
            _cevre.Remove(varlik);
        }
    }

    /// <summary>Yeni bölge: çevre tablosu ve eski kesin yer geçersiz.</summary>
    public void Temizle()
    {
        lock (_kilit)
        {
            _cevre.Clear();
            _kesin = null;
        }
    }

    /// <summary>
    /// Oyuncunun şu anki en iyi yeri: taze kesin bildirim (Tahmini = false); yoksa çevrenin orta noktası, son kesin yerden o arada
    /// yürünebilecek kadar uzaktaysa (10 m/sn + 30 m, en çok 12 sn) alınmaz; o da yoksa en çok <see cref="EnFazlaYasMs"/> eski kesin yer
    /// (Tahmini = true). Hiçbiri yoksa null.
    /// </summary>
    public (float X, float Y, bool Tahmini)? EnIyi()
    {
        lock (_kilit)
        {
            long simdi = Saat();
            if (_kesin is { } k && simdi - k.Tik <= KesinGuvenMs)
            {
                return (k.X, k.Y, false);
            }

            if (CevreOrtasi(simdi) is { } c && Inandirici(c.X, c.Y, simdi))
            {
                return (c.X, c.Y, true);
            }

            if (_kesin is { } eski && simdi - eski.Tik <= EnFazlaYasMs)
            {
                return (eski.X, eski.Y, true);
            }

            return null;
        }
    }

    private bool Inandirici(float x, float y, long simdi)
    {
        if (_kesin is not { } son)
        {
            return true;
        }

        double sn = Math.Min((simdi - son.Tik) / 1000.0, 12);
        double dx = x - son.X, dy = y - son.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= 3000 + 1000 * sn;
    }

    /// <summary>Son 5 sn'de (yoksa 20 sn'de) görünen ya da hareket eden en az 3 varlığın kırpılmış orta noktası.</summary>
    private (float X, float Y)? CevreOrtasi(long simdi) => Orta(simdi, CevreYasMs) ?? Orta(simdi, 4 * CevreYasMs);

    private (float X, float Y)? Orta(long simdi, long yasMs)
    {
        if (_cevre.Count < CevreEnAz)
        {
            return null;
        }

        if (_xs.Length < _cevre.Count)
        {
            int boy = Math.Min(_cevre.Count * 2, CevreSinir);
            _xs = new float[boy];
            _ys = new float[boy];
            _sx = new float[boy];
            _sy = new float[boy];
        }

        int n = 0;
        foreach ((float X, float Y, long Tik) v in _cevre.Values)
        {
            if (simdi - v.Tik <= yasMs)
            {
                _xs[n] = v.X;
                _ys[n] = v.Y;
                n++;
            }
        }

        if (n < CevreEnAz)
        {
            return null;
        }

        // Önce medyan (uzakta duyurulan varlıklar ortalamayı çeker), sonra medyana 45 m içindekilerin ortalaması (3 tur).
        Array.Copy(_xs, _sx, n);
        Array.Copy(_ys, _sy, n);
        Array.Sort(_sx, 0, n);
        Array.Sort(_sy, 0, n);
        float cx = _sx[n / 2], cy = _sy[n / 2];
        for (int tur = 0; tur < 3; tur++)
        {
            double tx = 0, ty = 0;
            int yakin = 0;
            for (int i = 0; i < n; i++)
            {
                float dx = _xs[i] - cx, dy = _ys[i] - cy;
                if (dx * dx + dy * dy <= KirpmaBirim * KirpmaBirim)
                {
                    tx += _xs[i];
                    ty += _ys[i];
                    yakin++;
                }
            }

            if (yakin < CevreEnAz)
            {
                break;
            }

            cx = (float)(tx / yakin);
            cy = (float)(ty / yakin);
        }

        return (cx, cy);
    }

    private static bool Makul(float x, float y) =>
        float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000 && (Math.Abs(x) > 1 || Math.Abs(y) > 1);

    // ---------------- Yön ve mesafe ----------------

    /// <summary>
    /// Oyuncudan hedefe pusula yönü (derece, harita kuzeyinden saat yönünde, 0..360) ve mesafe (metre).
    /// Oyunun haritasında x doğuya (sağa), y güneye (aşağı) artar: referans Aion2Maps.cs "pixel = offset + scale * world, no flip, no rotation",
    /// PetMapController.cs FollowHeading "the arrow points up at 0 degrees; the map's y runs down the screen". Kuzey = -y.
    /// </summary>
    public static (double Derece, double Metre) YonVeMesafe(float benX, float benY, float hedefX, float hedefY)
    {
        double dx = hedefX - (double)benX, dy = hedefY - (double)benY;
        double derece = Math.Atan2(dx, -dy) * 180.0 / Math.PI;
        if (derece < 0)
        {
            derece += 360.0;
        }

        return (derece, Math.Sqrt(dx * dx + dy * dy) / 100.0);
    }
}
