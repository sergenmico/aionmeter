using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AionMeter;

/// <summary>Gizli Küp işareti: kodla çizilmiş izometrik küp (üst yüz açık, yan yüzler koyu). Overlay rozeti, afiş ve çip ortak kullanır.</summary>
internal static class KupSimgesi
{
    public static Color Sol => Color.FromArgb(Palet.Kup.R * 72 / 100, Palet.Kup.G * 72 / 100, Palet.Kup.B * 72 / 100);
    public static Color Sag => Color.FromArgb(Palet.Kup.R * 52 / 100, Palet.Kup.G * 52 / 100, Palet.Kup.B * 52 / 100);

    /// <summary><paramref name="x"/>: sol kenar, <paramref name="cy"/>: dikey orta, <paramref name="boy"/>: kenar uzunluğu.
    /// <paramref name="n"/>: 4 noktalık yeniden kullanılan tampon (karede tahsis olmasın).</summary>
    public static void Ciz(Graphics g, float x, float cy, float boy, Brush ust, Brush sol, Brush sag, PointF[] n)
    {
        float w = boy, h = boy;
        float mx = x + w / 2f, ustY = cy - h / 2f, altY = cy + h / 2f, q = h / 4f;
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        n[0] = new PointF(mx, ustY); n[1] = new PointF(x + w, ustY + q); n[2] = new PointF(mx, cy); n[3] = new PointF(x, ustY + q);
        g.FillPolygon(ust, n);
        n[0] = new PointF(x, ustY + q); n[1] = new PointF(mx, cy); n[2] = new PointF(mx, altY); n[3] = new PointF(x, altY - q);
        g.FillPolygon(sol, n);
        n[0] = new PointF(mx, cy); n[1] = new PointF(x + w, ustY + q); n[2] = new PointF(x + w, altY - q); n[3] = new PointF(mx, altY);
        g.FillPolygon(sag, n);
        g.SmoothingMode = eski;
    }
}

/// <summary>
/// En yakın küpün pusula yönü ve mesafesi (afiş, çip ve overlay rozeti). Yön harita kuzeyine göredir (karakterin ya da kameranın
/// bakışına göre değil: oyun bakış yönü göndermez); 5° adımlıdır ki küçük oynamalarda yeniden çizilmesin. Tahmini: oyuncunun yeri kesin değil.
/// <see cref="Derece"/> null: mesafe biliniyor ama yön güvenilmez (yer tahmini ve küp 30 m'den yakın); ok ve yön adı çizilmez.
/// </summary>
internal readonly record struct KupYonu(int? Derece, int Metre, bool Tahmini)
{
    internal static readonly string[] Yonler = { "K", "KD", "D", "GD", "G", "GB", "B", "KB" };

    /// <summary>Anlık görüntüde mesafe varsa (yön de varsa onunla); yoksa null (afiş ve rozet eski görünümde kalır).</summary>
    public static KupYonu? Den(MeterSnapshot s) =>
        s.KupMesafe is int m && m >= 0
            ? new KupYonu(s.KupYonDerece is double d && double.IsFinite(d) ? ((int)Math.Round(d / 5.0) * 5 % 360 + 360) % 360 : null,
                          Math.Min(m, 999), s.KupKonumTahmini)
            : null;

    /// <summary>Sekiz bölgeli pusula kısaltması: K, KD, D, GD, G, GB, B, KB; yön yoksa boş.</summary>
    public string YonAdi => Derece is int d ? Yonler[(int)Math.Round(d / 45.0) % 8] : "";

    /// <summary>"38 m"; tahminiyse "~38 m" (overlay rozeti).</summary>
    public string MesafeMetni => (Tahmini ? "~" : "") + Metre + " m";
}

/// <summary>
/// Gizli Küp afişi: küp sesiyle aynı anda, oyunun bulunduğu ekranın yatay ortasında, üstten ~%30'da büyük "Küp var" yazısı.
/// ~150 ms belirir, ~4 sn bekler. Sonra küp hâlâ yakındaysa ~240 ms'de yukarı kayıp sönerken yerini küçük bir çipe
/// (<see cref="KupCipi"/>: küp + ok + "38 m") bırakır; çip küp menzilden çıkana (açılana) kadar kalır, sonra ~400 ms'de söner.
/// Küp gitmişse afiş eskisi gibi ~600 ms'de söner.
/// <para>Her zaman tıklanmaz (WS_EX_TRANSPARENT), odak çalmaz, piksel başına saydam. Zamanlayıcı yalnız belirme/geçiş/sönme
/// sürerken çalışır; afiş beklerken saniyede ~4 kez, çip dururken hiç uyanmaz. Yüzeyler yalnız yön/mesafe değişince çizilir;
/// geçiş karelerinde yalnız konum ve opaklık değişir (yeniden çizim ve ayırma yok).</para>
/// <para>Oyun özel tam ekrandayken ya da önde değilken overlay hiç göstermez; bu duruma geçilirse <see cref="Kapat"/> ile hemen
/// kapanır. Oyun geri gelince küp hâlâ yakındaysa (<see cref="CipBekliyor"/>) afiş değil yalnız çip döner (ses yok).</para>
/// Oyuncunun yeri biliniyorsa yazının sağında pusula oku ve "KD · 38 m"; yer bilinmiyorsa yalnız "Küp var", çipte yalnız "Küp".
/// </summary>
internal sealed class KupAfisi : KatmanPencere
{
    public const string Metin = "Küp var";
    private const int BelirmeMs = 150, BeklemeMs = 4000, SonmeMs = 600;
    internal const int GecisMs = 240, CipBelirmeMs = 150, CipSonmeMs = 400;

    private enum Evre { Kapali, Afis, Gecis, Cip, CipBelirme, CipSonme }

    private readonly System.Windows.Forms.Timer _zamanlayici = new() { Interval = 16 };
    private readonly Stopwatch _saat = new();
    private long _saatKaydir; // yeniden tetiklenince belirme evresi atlanır
    private Evre _evre;
    private float _sonOlcek = -1f;
    private Rectangle _sonEkran;
    private KupYonu? _yon;
    private KupCipi? _cip; // ilk geçişte yaratılır
    private bool _menzilde;  // küp hâlâ yakında mı (KupYakinda > 0)
    private bool _cipIstegi; // bu küp duyuruldu; yakında kaldıkça çip gösterilebilir
    private Point _gecisBas;
    private int _gecisKayma;
    private byte _sonmeBas = 255;
    private long _sonUstMs;

    public KupAfisi() : base(tiklanmaz: true, "AionMeter Küp")
    {
        _zamanlayici.Tick += (_, _) => Ilerle();
    }

    /// <summary>Afiş ya da çip ekranda mı.</summary>
    public bool Gorunur => Acik || _cip is { Acik: true };

    /// <summary>Kapatılmıştı (oyun arkadaydı / tam ekrandı) ama duyurulan küp hâlâ yakında: çip geri getirilebilir.</summary>
    public bool CipBekliyor => _evre == Evre.Kapali && _cipIstegi && _menzilde;

    /// <summary>Görünen pencerelerin ekran alanları (çevirmen bunları maskeler).</summary>
    public IEnumerable<Rectangle> Alanlar()
    {
        if (Acik) yield return Alan;
        if (_cip is { Acik: true }) yield return _cip.Alan;
    }

    /// <summary>
    /// Yeni küp: afişi <paramref name="hedef"/> alanının (oyun penceresi ya da ekranı, fiziksel px) ortasında gösterir; çip varsa
    /// hemen gizlenir. Zaten görünüyorsa yeniden belirmeden tam görünür hale gelir ve bekleme baştan başlar.
    /// </summary>
    public void Goster(Rectangle hedef, KupYonu? yon, bool menzilde)
    {
        try
        {
            _menzilde = menzilde;
            _cipIstegi = true;
            CipGizle();
            float olcek = Olcek(hedef);
            if (!Acik || _evre == Evre.Gecis || olcek != _sonOlcek || hedef != _sonEkran || yon != _yon)
            {
                _sonOlcek = olcek;
                _sonEkran = hedef;
                _yon = yon;
                if (!Acik) AlfaAyarla(0);
                Yerlestir(); // geçişte yukarı kaymışsa yerine döner
            }
            _saat.Restart();
            if (Acik)
            {
                // Sönerken / beklerken / çipe dönerken yeniden geldi: belirmeyi atla, tam görünür kal, bekleme baştan.
                _saatKaydir = BelirmeMs;
                AlfaAyarla(255);
            }
            else
            {
                _saatKaydir = 0;
                PencereGoster();
            }
            _evre = Evre.Afis;
            _zamanlayici.Interval = 16;
            _zamanlayici.Start();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp afişi", ex);
            Bitir();
        }
    }

    /// <summary>
    /// Yeni küp duyuruldu ama oyun önde değil / özel tam ekranda: afiş gösterilmez (ses çaldı). Küp yakında kaldıkça oyun öne dönünce
    /// <see cref="CipBekliyor"/> ile yalnız çip gelir (afiş ve ses tekrarlanmaz).
    /// </summary>
    public void Ertele(KupYonu? yon, bool menzilde)
    {
        Kapat();
        _cipIstegi = true;
        _menzilde = menzilde;
        _yon = yon;
    }

    /// <summary>
    /// Her okumada (~5 Hz): küp hâlâ yakında mı, yön/mesafe ne. Yalnız değer değişince yeniden çizer. Küp gidince çip söner;
    /// sönerken geri gelirse tam görünür kalır. <paramref name="hedef"/>: oyun penceresinin şimdiki alanı (biliniyorsa); oyun penceresi
    /// taşınınca ya da boyutu değişince afiş/çip yeni alanın ortasına geçer.
    /// </summary>
    public void Guncelle(KupYonu? yon, bool menzilde, Rectangle? hedef = null)
    {
        _menzilde = menzilde;
        try
        {
            bool yerDegisti = false;
            if (_evre != Evre.Kapali && hedef is Rectangle h && h != _sonEkran)
            {
                _sonEkran = h;
                _sonOlcek = Olcek(h);
                yerDegisti = true; // afiş: aşağıda yeniden yerleşir; çip: CipCiz (Hazirla) yeni alana taşır; geçiş: bitince çip yeni alanda
            }
            switch (_evre)
            {
                case Evre.Kapali:
                    if (!menzilde) _cipIstegi = false; // küp gitti: oyun öne dönünce çip gelmesin
                    break;
                case Evre.Afis:
                    if (yon != _yon || yerDegisti)
                    {
                        _yon = yon;
                        Yerlestir();
                    }
                    break;
                case Evre.Gecis:
                    _yon = yon; // geçiş bitince çip son değerle çizilir
                    break;
                case Evre.Cip:
                case Evre.CipBelirme:
                    if (!menzilde) { SonmeyeBasla(); break; }
                    CipCiz(yon);
                    UsteAl();
                    break;
                case Evre.CipSonme:
                    if (!menzilde)
                    {
                        if (yerDegisti) CipCiz(_yon); // sönerken de oyunla birlikte taşınır
                        break;
                    }
                    _zamanlayici.Stop();
                    _evre = Evre.Cip;
                    _cip?.Opaklik(255);
                    CipCiz(yon);
                    break;
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp çipi", ex);
            Bitir();
        }
    }

    /// <summary>Oyun öne döndü / tam ekrandan çıkıldı ve küp hâlâ yakında: afişsiz, yalnız çip kısa bir belirmeyle gelir.</summary>
    public void CipGoster(Rectangle hedef, KupYonu? yon)
    {
        if (!CipBekliyor) return;
        try
        {
            _sonOlcek = Olcek(hedef);
            _sonEkran = hedef;
            _yon = yon;
            _cip ??= new KupCipi();
            _cip.Opaklik(0);
            _cip.Hazirla(hedef, _sonOlcek, yon);
            _cip.PencereGoster();
            _evre = Evre.CipBelirme;
            _saat.Restart();
            _saatKaydir = 0;
            _zamanlayici.Interval = 16;
            _zamanlayici.Start();
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp çipi", ex);
            Bitir();
        }
    }

    /// <summary>Hemen gizler (tam ekran / oyun arkada). Küp yakında kalırsa çip sonra <see cref="CipGoster"/> ile döner.</summary>
    public void Kapat()
    {
        _zamanlayici.Stop();
        _saat.Reset();
        _evre = Evre.Kapali;
        PencereGizle();
        AlfaAyarla(0);
        CipGizle();
    }

    /// <summary>Gizler ve bu küpü unutur (Gizli Küp ayarı kapatıldı ya da çip söndü).</summary>
    public void Bitir()
    {
        _cipIstegi = false;
        Kapat();
    }

    private void CipGizle()
    {
        if (_cip is null) return;
        _cip.PencereGizle();
        _cip.Opaklik(0);
    }

    private void CipCiz(KupYonu? yon)
    {
        _yon = yon;
        _cip?.Hazirla(_sonEkran, _sonOlcek, yon); // aynıysa dokunmaz
    }

    /// <summary>Çip uzun kalabilir: bazı oyunlar kendini öne alır; en üstte kalmayı ~2 sn'de bir yenile (etkinleştirmeden).</summary>
    private void UsteAl()
    {
        long simdi = Environment.TickCount64;
        if (simdi - _sonUstMs < 2000) return;
        _sonUstMs = simdi;
        _cip?.EnUsteAl();
    }

    /// <summary>Yüzeyi çizer; boyut değişirse (yön ilk kez geldi / gitti) hedefin ortasına yeniden oturtur.</summary>
    private void Yerlestir()
    {
        float olcek = _sonOlcek;
        var yon = _yon;
        var boyut = Boyut(olcek, yon is not null);
        Bas(new Rectangle(AfisKonumu(_sonEkran, boyut), boyut), g => Ciz(g, boyut, olcek, yon));
    }

    /// <summary>Afişin sol üst köşesi: hedefin yatay ortası, dikeyde üstten %30.</summary>
    internal static Point AfisKonumu(Rectangle hedef, Size boyut) =>
        new(hedef.Left + (hedef.Width - boyut.Width) / 2, hedef.Top + (int)(hedef.Height * 0.30f) - boyut.Height / 2);

    private void Ilerle()
    {
        long t = _saat.ElapsedMilliseconds + _saatKaydir;
        switch (_evre)
        {
            case Evre.Afis: AfisIlerle(t); break;
            case Evre.Gecis: GecisIlerle(t); break;
            case Evre.CipBelirme:
                if (t >= CipBelirmeMs)
                {
                    _cip?.Opaklik(255);
                    CipDurdu();
                }
                else
                {
                    double u = 1.0 - t / (double)CipBelirmeMs;
                    _cip?.Opaklik((byte)Math.Round(255 * (1.0 - u * u * u)));
                }
                break;
            case Evre.CipSonme:
                if (t >= CipSonmeMs) Bitir();
                else
                {
                    double k = t / (double)CipSonmeMs;
                    _cip?.Opaklik((byte)Math.Round(_sonmeBas * (1.0 - k * k)));
                }
                break;
            default:
                _zamanlayici.Stop();
                break;
        }
    }

    private void AfisIlerle(long t)
    {
        if (t < BelirmeMs)
        {
            double u = 1.0 - t / (double)BelirmeMs;
            AlfaAyarla((byte)Math.Round(255 * (1.0 - u * u * u))); // ease-out
            return;
        }
        if (t < BelirmeMs + BeklemeMs)
        {
            AlfaAyarla(255);
            // Beklerken saniyede ~4 kez uyanmak yeter; sönme/geçiş başlayınca yeniden ~60 fps.
            long kalan = BelirmeMs + BeklemeMs - t;
            _zamanlayici.Interval = (int)Math.Clamp(kalan, 16, 250);
            return;
        }
        _zamanlayici.Interval = 16;
        if (_menzilde && _cipIstegi && Alfa == 255)
        {
            GeciseBasla();
            return;
        }
        long s = t - BelirmeMs - BeklemeMs;
        if (s >= SonmeMs)
        {
            Kapat(); // küp sönerken geri geldiyse çip CipBekliyor ile döner
            return;
        }
        double k = s / (double)SonmeMs;
        AlfaAyarla((byte)Math.Round(255 * (1.0 - k * k))); // ease-in: yavaş başlar, sona doğru hızlanır
    }

    /// <summary>Çip görünmez halde yerine konur; afiş kendi merkezinden çipin merkezine kayarken söner, çip belirir.</summary>
    private void GeciseBasla()
    {
        try
        {
            _cip ??= new KupCipi();
            _cip.Opaklik(0);
            _cip.Hazirla(_sonEkran, _sonOlcek, _yon);
            _cip.PencereGoster(); // afişin üstünde
            _gecisBas = Alan.Location;
            var c = _cip.Alan;
            _gecisKayma = (c.Top + c.Height / 2) - (Alan.Top + Alan.Height / 2);
            _evre = Evre.Gecis;
            _saat.Restart();
            _saatKaydir = 0;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Küp çipi", ex);
            Bitir();
        }
    }

    private void GecisIlerle(long t)
    {
        if (t >= GecisMs)
        {
            PencereGizle();
            AlfaAyarla(0);
            CipCiz(_yon); // geçişte yön değiştiyse
            _cip?.Opaklik(255);
            CipDurdu();
            return;
        }
        var (kayma, afisAlfa, cipAlfa) = GecisKaresi(t);
        Tasi(new Point(_gecisBas.X, _gecisBas.Y + (int)MathF.Round(_gecisKayma * kayma)));
        AlfaAyarla(afisAlfa);
        _cip?.Opaklik(cipAlfa);
    }

    /// <summary>Çip tam görünür ve duruyor: zamanlayıcı durur (boşta CPU yok). Bu arada küp gittiyse sönmeye geçer.</summary>
    private void CipDurdu()
    {
        _evre = Evre.Cip;
        _zamanlayici.Stop();
        _sonUstMs = Environment.TickCount64;
        if (!_menzilde) SonmeyeBasla();
    }

    private void SonmeyeBasla()
    {
        _sonmeBas = _cip?.MevcutAlfa ?? 255; // belirirken gittiyse bulunduğu opaklıktan söner (sıçrama yok)
        _evre = Evre.CipSonme;
        _saat.Restart();
        _saatKaydir = 0;
        _zamanlayici.Interval = 16;
        _zamanlayici.Start();
    }

    /// <summary>
    /// Afiş → çip geçişinin bir karesi (0..<see cref="GecisMs"/> ms): afişin kayma oranı (0 → 1, yavaşlayarak), afişin ve çipin opaklığı.
    /// Afiş hızlı söner; çip ilk %30'dan sonra belirmeye başlar ki ikisi aynı anda tam görünür olmasın.
    /// </summary>
    internal static (float Kayma, byte AfisAlfa, byte CipAlfa) GecisKaresi(long ms)
    {
        double k = Math.Clamp(ms / (double)GecisMs, 0.0, 1.0);
        double kayma = 1.0 - (1.0 - k) * (1.0 - k);
        double afis = (1.0 - k) * (1.0 - k);
        double c = Math.Clamp((k - 0.3) / 0.7, 0.0, 1.0);
        double cip = 1.0 - (1.0 - c) * (1.0 - c);
        return ((float)kayma, (byte)Math.Round(255 * afis), (byte)Math.Round(255 * cip));
    }

    // ---------------- Ölçü ve çizim (statik: ekran dışı bitmap'e de çizilebilir) ----------------

    private static int P(float v, float olcek) => (int)MathF.Round(v * olcek);

    /// <summary>Yazı tipi: Bahnschrift SemiBold (yoksa Segoe UI Semibold).</summary>
    private static Font Yazi(float olcek) => Palet.RakamTipi(30f * olcek, yariKalin: true);

    /// <summary>Yön yazısı: ana yazıdan küçük.</summary>
    private static Font YonYazisi(float olcek) => Palet.RakamTipi(22f * olcek, yariKalin: true);

    /// <summary>"KD" ile "·" arasındaki ve "·" ile mesafe arasındaki boşluk.</summary>
    private static float Ara(Font f) => f.Size * 0.45f;

    /// <summary>En geniş yön adı (sabit yer: yön değişince nokta ve mesafe kaymaz).</summary>
    private static float YonAdiYeri(Font f)
    {
        float w = 0f;
        foreach (var y in KupYonu.Yonler) w = Math.Max(w, KupYazi.Genislik(f, y));
        return w;
    }

    /// <summary>"KB · ~888 m" için ayrılan yer: yön adı sabit yerde, mesafe sabit alanda sağa yaslı. Afiş mesafe değişirken genişleyip daralmaz.</summary>
    private static float YonYeri(Font f, KupYazi.Olcu o) =>
        YonAdiYeri(f) + Ara(f) + KupYazi.Genislik(f, "·") + Ara(f) + KupYazi.AlanGenisligi(o);

    internal static Size Boyut(float olcek, bool yonVar = false)
    {
        using var f = Yazi(olcek);
        using var yol = KupYazi.Yol(f, Metin);
        var b = yol.GetBounds();
        int kup = P(26, olcek);
        int w = P(22, olcek) + kup + P(14, olcek) + (int)MathF.Ceiling(b.Width) + P(24, olcek);
        if (yonVar)
        {
            using var fy = YonYazisi(olcek);
            w += P(18, olcek) + P(26, olcek) + P(8, olcek) + (int)MathF.Ceiling(YonYeri(fy, KupYazi.Olc(fy)));
        }
        int h = Math.Max(kup, (int)MathF.Ceiling(f.GetHeight())) + 2 * P(12, olcek);
        return new Size(w, h);
    }

    internal static void Ciz(Graphics g, Size boyut, float olcek, KupYonu? yon = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // Zemin: hafif koyu, yarı saydam, yuvarlak köşeli; ince sıcak kenar
        float r = P(10, olcek);
        var zemin = new RectangleF(0.5f, 0.5f, boyut.Width - 1f, boyut.Height - 1f);
        using (var yol = Yuvarlak(zemin, r))
        {
            using var firca = new SolidBrush(Color.FromArgb(150, 12, 14, 19));
            g.FillPath(firca, yol);
            using var kenar = new Pen(Color.FromArgb(90, Palet.Kup), Math.Max(1f, olcek));
            g.DrawPath(kenar, yol);
        }

        // Küp simgesi
        float cy = boyut.Height / 2f;
        int kup = P(26, olcek);
        float kx = P(22, olcek);
        using (var ust = new SolidBrush(Palet.Kup))
        using (var sol = new SolidBrush(KupSimgesi.Sol))
        using (var sag = new SolidBrush(KupSimgesi.Sag))
            KupSimgesi.Ciz(g, kx, cy, kup, ust, sol, sag, new PointF[4]);

        // Metin: koyu dış çizgi + altın dolgu (oyunun parlak sahnesinde de okunur)
        using var f = Yazi(olcek);
        using var metin = KupYazi.Yol(f, Metin);
        var b = metin.GetBounds();
        float tx = kx + kup + P(14, olcek) - b.Left;
        float ty = cy - (b.Top + b.Height / 2f);
        using (var m = new Matrix())
        {
            m.Translate(tx, ty);
            metin.Transform(m);
        }
        using (var golge = new Pen(Color.FromArgb(210, 0, 0, 0), Math.Max(2f, 4f * olcek)) { LineJoin = LineJoin.Round })
            g.DrawPath(golge, metin);
        using (var dolgu = new SolidBrush(Palet.Kup))
            g.FillPath(dolgu, metin);

        if (yon is { } y)
        {
            float ox = tx + b.Left + b.Width + P(18, olcek); // pusula kadranının sol kenarı
            CizYon(g, ox, cy, olcek, y);
        }
    }

    /// <summary>
    /// Pusula kadranı + ok, sağında "KD · 38 m". Yön adı sabit yerde başlar, mesafe sabit alanın sağına yaslıdır ("m" hep aynı
    /// yerde, kısa mesafede sağda boşluk kalmaz). Yön güvenilmezken (<see cref="KupYonu.Derece"/> null) kadran, ok ve yön adı
    /// çizilmez; mesafe yine aynı yerde durur (afişin boyu değişmez).
    /// </summary>
    private static void CizYon(Graphics g, float x, float cy, float olcek, KupYonu y)
    {
        float cap = P(26, olcek);
        using var f = YonYazisi(olcek);
        var o = KupYazi.Olc(f);
        float cizgi = Math.Max(2f, 3.5f * olcek);
        float yx = x + cap + P(8, olcek);
        if (y.Derece is int derece)
        {
            CizOk(g, x + cap / 2f, cy, cap / 2f, derece, olcek);
            float taban = cy - o.RakamOrta;
            KupYazi.Parca(g, f, y.YonAdi, yx, sagaYasla: false, taban, cizgi, KupYazi.Acik);
            KupYazi.Parca(g, f, "·", yx + YonAdiYeri(f) + Ara(f), sagaYasla: false, taban, cizgi, KupYazi.Acik);
        }
        KupYazi.MesafeCiz(g, f, o, yx + YonYeri(f, o), cy, y, cizgi);
    }

    /// <summary>
    /// Pusula kadranı (halka + kuzey çentiği) içinde harita kuzeyine göre dönmüş ok; merkez (<paramref name="mx"/>, <paramref name="cy"/>),
    /// yarıçap <paramref name="r"/>. Ok yukarı = harita kuzeyi (karakterin baktığı yön değil). Afiş ve çip ortak kullanır.
    /// </summary>
    internal static void CizOk(Graphics g, float mx, float cy, float r, int derece, float olcek)
    {
        float ince = Math.Min(1f, r / 13f); // çipteki küçük kadranda çizgiler de incelir
        using (var halka = new Pen(Color.FromArgb(120, Palet.Kup), Math.Max(1f, 1.5f * olcek)))
            g.DrawEllipse(halka, mx - r, cy - r, 2f * r, 2f * r);
        // kuzey çentiği: halkanın tepesinde kısa açık çizgi (harita kuzeyi)
        using (var centik = new Pen(Color.FromArgb(235, 240, 240, 240), Math.Max(1.5f, 2f * olcek * ince)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(centik, mx, cy - r - r * 0.15f, mx, cy - r + r * 0.23f);

        using var ok = new GraphicsPath();
        ok.AddPolygon(new[]
        {
            new PointF(mx, cy - r * 0.80f),
            new PointF(mx + r * 0.46f, cy + r * 0.58f),
            new PointF(mx, cy + r * 0.28f),
            new PointF(mx - r * 0.46f, cy + r * 0.58f),
        });
        using (var m = new Matrix())
        {
            m.RotateAt(derece, new PointF(mx, cy)); // ekranda y aşağı: pozitif açı saat yönünde = pusula
            ok.Transform(m);
        }
        using (var golge = new Pen(Color.FromArgb(200, 0, 0, 0), Math.Max(1.5f, 2.5f * olcek * ince)) { LineJoin = LineJoin.Round })
            g.DrawPath(golge, ok);
        using var dolgu = new SolidBrush(Palet.Kup);
        g.FillPath(dolgu, ok);
    }

    internal static GraphicsPath Yuvarlak(RectangleF r, float yaricap)
    {
        float d = Math.Max(1f, yaricap * 2f);
        var yol = new GraphicsPath();
        yol.AddArc(r.Left, r.Top, d, d, 180, 90);
        yol.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        yol.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        yol.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        yol.CloseFigure();
        return yol;
    }

    /// <summary>Hedef alanın bulunduğu monitörün DPI ölçeği (en az 1).</summary>
    private static float Olcek(Rectangle hedef)
    {
        try
        {
            var orta = new POINT { X = hedef.Left + hedef.Width / 2, Y = hedef.Top + hedef.Height / 2 };
            IntPtr mon = MonitorFromPoint(orta, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0)
                return Math.Max(1f, dx / 96f);
        }
        catch { /* shcore yoksa varsayılan */ }
        return 1f;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _zamanlayici.Stop();
            _zamanlayici.Dispose();
            _cip?.Dispose();
        }
        base.Dispose(disposing);
    }
}
