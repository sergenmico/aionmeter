using System.Drawing.Drawing2D;
using AionMeter.Cevirmen;
using AionMeter.Core;

namespace AionMeter;

/// <summary>
/// Tepsi "Ayarlar…": tek küçük koyu pencere, tamamen kodla çizilir (overlay ile aynı palet ve yazı tipleri).
/// Üç sekme: Genel · Renk ve yazı · Kukla ve alarm. Her değişiklik anında uygulanır ve ayarlar.ini'ye yazılır;
/// saydamlık, renkler ve yazı boyutları değiştirilirken overlay (ve açık detay paneli) canlı değişir.
/// Pencere odak alabilir (Esc kapatır); kapanınca overlay odağı oyuna geri verir. Tanı kaydı burada yoktur.
/// </summary>
internal sealed class AyarlarFormu : Form
{
    /// <summary>Overlay'e bağlantılar (ayar değişince overlay'in yapması gerekenler).</summary>
    public sealed class Baglanti
    {
        public Action<int> BoyutSec { get; init; } = _ => { };
        public Action OpaklikDegisti { get; init; } = () => { };
        public Action SolukDegisti { get; init; } = () => { };
        public Action SavasDisiDegisti { get; init; } = () => { };
        public Func<bool> SadeceOyunda { get; init; } = () => true;
        public Action SadeceOyundaDegistir { get; init; } = () => { };
        public Action AltCeviriDegistir { get; init; } = () => { };
        public Action DusukRamDegistir { get; init; } = () => { };
        public Func<bool> ModelHazir { get; init; } = () => false;
        public Func<bool> ModelIndiriliyor { get; init; } = () => false;
        public Action ModeliIndir { get; init; } = () => { };
        public Action GuncellemeDenetle { get; init; } = () => { };
        public Func<bool> Denetleniyor { get; init; } = () => false;
        public Action GorunumDegisti { get; init; } = () => { };   // renk, yazı, çubuk, sadece benim satırım
        public Action YaziBitti { get; init; } = () => { };        // yazı kaydırıcısı bırakıldı (detay paneli yeniden açılır)
        public Action KuklaBaslat { get; init; } = () => { };
        public Action KuklaIptal { get; init; } = () => { };
        public Func<bool> KuklaEtkin { get; init; } = () => false;
        public Action AlarmKur { get; init; } = () => { };
        public Action AlarmIptal { get; init; } = () => { };
        public Func<string?> AlarmKalan { get; init; } = () => null;
        public Func<string> KuklaKisayol { get; init; } = () => "";
        public Func<string, string?> KuklaKisayolAyarla { get; init; } = _ => null; // hata metni ya da null
        public Action<bool> KisayolYakalama { get; init; } = _ => { };
        public SinifIkonlari? Ikonlar { get; init; }               // overlay'in ikon önbelleği (paylaşılır, Dispose edilmez)
    }

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;
    private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | Temel;

    // ---- Öğeler ----
    private abstract class Oge
    {
        public int Sekme;
        public Rectangle Alan;
        public Func<bool>? Etkin;
        public bool EtkinMi => Etkin?.Invoke() ?? true;
    }
    private sealed class Bolum(string baslik) : Oge { public readonly string Baslik = baslik; }
    private sealed class Secim(string etiket, string[] secenekler, Func<int> secili, Action<int> sec) : Oge
    {
        public readonly string Etiket = etiket;
        public readonly string[] Secenekler = secenekler;
        public readonly Func<int> Secili = secili;
        public readonly Action<int> Sec = sec;
        public Rectangle[] Parcalar = Array.Empty<Rectangle>();
    }
    private sealed class Kaydirici(string etiket, int min, int max, Func<int> deger, Action<int> ayarla, Action bitti,
                                   Func<int, string>? yazi = null) : Oge
    {
        public readonly string Etiket = etiket;
        public readonly Func<int, string> Yazi = yazi ?? (v => "%" + v.ToString(Bicim.Tr));
        public readonly int Min = min, Max = max;
        public readonly Func<int> Deger = deger;
        public readonly Action<int> Ayarla = ayarla;
        public readonly Action Bitti = bitti;
        public Rectangle Iz;
    }
    private sealed class Anahtar(string etiket, string? aciklama, Func<bool> deger, Action degistir) : Oge
    {
        public readonly string Etiket = etiket;
        public readonly string? Aciklama = aciklama;
        public readonly Func<bool> Deger = deger;
        public readonly Action Degistir = degistir;
        public Rectangle Dugme;
    }
    private sealed class Dugme(Func<string> etiket, Func<string> metin, Action tikla, string? aciklama = null) : Oge
    {
        public readonly Func<string> Etiket = etiket;
        public readonly string? Aciklama = aciklama;
        public readonly Func<string> Metin = metin;
        public readonly Action Tikla = tikla;
        public Rectangle Kutu;
    }
    /// <summary>Renk seçimi: sınıf (yarım genişlik, ikonlu) ya da kendi satır rengin. Kutuya tıklayınca palet açılır.</summary>
    private sealed class RenkSecim(string etiket, string? sinif, Func<Color> acik, Func<Color> koyu, Func<int?> secili,
                                   Color varsayilan, Action<int?> sec, bool yarim) : Oge
    {
        public readonly string Etiket = etiket;
        public readonly string? Sinif = sinif;
        public readonly Func<Color> Acik = acik, Koyu = koyu;
        public readonly Func<int?> Secili = secili;   // 0xRRGGBB, null = varsayılan
        public readonly Color Varsayilan = varsayilan;
        public readonly Action<int?> Sec = sec;
        public readonly bool Yarim = yarim;
        public Rectangle Kutu;
    }

    /// <summary>Kukla kısayolu: [Ctrl+Alt+K] [Değiştir] [Kapat]; altta ipucu ya da kırmızı uyarı.</summary>
    private sealed class KisayolSatiri : Oge
    {
        public Rectangle Deger, Degistir, Kapat;
    }

    private bool _yakalaniyor;     // "Değiştir"e basıldı: bir sonraki tuş kombinasyonu bekleniyor
    private string? _kisayolHata;  // kısa kırmızı uyarı (bir sonraki eyleme kadar)

    private static readonly string[] Sekmeler = { "Genel", "Renk ve yazı", "Kukla ve alarm" };
    private readonly Rectangle[] _sekmeKutu = new Rectangle[Sekmeler.Length];
    private int _sekme;
    private int _sekmeUstunde = -1;

    // Renk paleti (açılır kutu): 8 × 2 renk + "Varsayılan"
    private RenkSecim? _palet;
    private Rectangle _paletAlani, _paletVarsayilan;
    private readonly Rectangle[] _paletKutu = new Rectangle[Palet.Secenekler.Length];
    private int _paletUstunde = -1; // 0..15 renk, 16 varsayılan

    private readonly Ayarlar _ayar;
    private readonly Baglanti _b;
    private readonly float _olcek;
    private readonly List<Oge> _ogeler = new();
    private readonly Font _yaziBaslik, _yazi, _yaziKucuk, _yaziBolum, _yaziDeger;
    private readonly SolidBrush _fircaPanel = new(Palet.Panel);
    private readonly SolidBrush _fircaPanelUst = new(Palet.PanelUst);
    private readonly SolidBrush _fircaVurgu = new(Palet.Vurgu);
    private readonly SolidBrush _fircaSecili = new(Color.FromArgb(40, 66, 104));
    private readonly SolidBrush _fircaTopuz = new(Palet.Dps);
    private readonly SolidBrush _fircaTopuzKapali = new(Palet.Soluk);
    private readonly Pen _kalemKenar = new(Palet.Cizgi);
    private readonly Pen _kalemSeciliKenar = new(Palet.Vurgu);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly Pen _kalemHalka = new(Palet.Dps, 2f);
    private readonly Pen _kalemHalkaSoluk = new(Palet.Soluk, 1.5f);
    private readonly SolidBrush _fircaRenk = new(Color.Black);   // renk kutuları (Color atanır, tahsis yok)
    private Rectangle _kapatKutusu, _baslikAlani;
    private Oge? _ustunde;
    private Kaydirici? _suruklenen;
    private bool _pencereSurukle;
    private Point _fareBas, _konumBas;

    public AyarlarFormu(Ayarlar ayar, Baglanti baglanti, float olcek)
    {
        _ayar = ayar;
        _b = baglanti;
        _olcek = olcek;

        Text = "AionMeter ayarları";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palet.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _yaziBaslik = Palet.YaziTipi(15f * olcek, yariKalin: true);
        _yazi = Palet.YaziTipi(12.5f * olcek);
        _yaziKucuk = Palet.YaziTipi(11f * olcek);
        _yaziBolum = Palet.YaziTipi(11f * olcek, yariKalin: true);
        _yaziDeger = Palet.YaziTipi(12f * olcek, yariKalin: true);
        _kalemKapat.Width = Math.Max(1f, 1.5f * olcek);

        _kalemHalka.Width = Math.Max(1.5f, 2f * olcek);
        _kalemHalkaSoluk.Width = Math.Max(1f, 1.5f * olcek);

        string[] boyutlar = Array.ConvertAll(Ayarlar.BoyutSecenekleri, v => "%" + v.ToString(Bicim.Tr));
        // ---- Sekme 0: Genel ----
        _ogeler.Add(new Bolum("Görünüm"));
        _ogeler.Add(new Secim("Boyut", boyutlar, () => Array.IndexOf(Ayarlar.BoyutSecenekleri, _ayar.Boyut),
            i => _b.BoyutSec(Ayarlar.BoyutSecenekleri[i])));
        _ogeler.Add(new Kaydirici("Saydamlık", 30, 100, () => _ayar.Opaklik,
            v => { _ayar.Opaklik = v; _b.OpaklikDegisti(); }, _ayar.Kaydet));
        _ogeler.Add(new Secim("Savaş dışında", new[] { "Soluk", "Gizle", "Hep görünür" }, () => (int)_ayar.SavasDisi,
            i => { _ayar.SavasDisi = (SavasDisiModu)i; _ayar.Kaydet(); _b.SavasDisiDegisti(); }));
        _ogeler.Add(new Kaydirici("Soluk opaklığı", Ayarlar.EnAzSolukOpaklik, Ayarlar.EnFazlaSolukOpaklik, () => _ayar.SolukOpaklik,
            v => { _ayar.SolukOpaklik = v; _b.SolukDegisti(); }, _ayar.Kaydet)
        { Etkin = () => _ayar.SavasDisi == SavasDisiModu.Soluk });
        _ogeler.Add(new Anahtar("Sadece oyun öndeyken göster", null, _b.SadeceOyunda, _b.SadeceOyundaDegistir));
        // Zamanlayıcı ayarları her tikte okur: burada yalnız kaydetmek yeter (geri sayım paneli kendini günceller).
        _ogeler.Add(new Anahtar("Etkinlik geri sayımı", null, () => _ayar.Zamanlayici, () =>
        {
            _ayar.Zamanlayici = !_ayar.Zamanlayici;
            _ayar.Kaydet();
        }));
        _ogeler.Add(new Secim("Bölge", EtkinlikTakvimi.BolgeAdlari, () => (int)_ayar.Bolge,
            i => { _ayar.Bolge = (SunucuBolgesi)i; _ayar.Kaydet(); }));
        _ogeler.Add(new Anahtar("Sadece benim satırım", "Diğerleri arka planda sayılır; sıran #3 gibi görünür", () => _ayar.SadeceBen, () =>
        {
            _ayar.SadeceBen = !_ayar.SadeceBen;
            _ayar.Kaydet();
            _b.GorunumDegisti();
        }));

        _ogeler.Add(new Bolum("Çevirmen"));
        _ogeler.Add(new Anahtar("Alt ile açıklama çevirisi", null, () => _ayar.AltCeviri, _b.AltCeviriDegistir));
        _ogeler.Add(new Anahtar("Düşük RAM modu", "~660 MB, çeviri daha yavaş", () => _ayar.CevirmenDusukRam, _b.DusukRamDegistir));
        _ogeler.Add(new Dugme(() => "Çeviri modeli",
            () => _b.ModelIndiriliyor() ? "İndiriliyor…" : _b.ModelHazir() ? "Hazır" : "İndir", _b.ModeliIndir)
        { Etkin = () => !_b.ModelHazir() && !_b.ModelIndiriliyor() });

        _ogeler.Add(new Bolum("Güncelleme"));
        _ogeler.Add(new Anahtar("Otomatik güncelleme", "Açılışta yeni sürümü kontrol eder", () => _ayar.OtomatikGuncelleme, () =>
        {
            _ayar.OtomatikGuncelleme = !_ayar.OtomatikGuncelleme;
            _ayar.Kaydet();
        }));
        _ogeler.Add(new Dugme(() => "Sürüm " + Guncelleyici.Goster(Guncelleyici.MevcutSurum),
            () => _b.Denetleniyor() ? "Denetleniyor…" : "Güncellemeleri denetle", _b.GuncellemeDenetle)
        { Etkin = () => !_b.Denetleniyor() });

        // ---- Sekme 1: Renk ve yazı ----
        int ilk = _ogeler.Count;
        _ogeler.Add(new Bolum("Sınıf renkleri"));
        for (int i = 1; i < Palet.SinifSirasi.Length; i++)
        {
            int si = i;
            string sinif = Palet.SinifSirasi[i]!;
            _ogeler.Add(new RenkSecim(sinif, sinif, () => Palet.Sinif(sinif).Acik, () => Palet.Sinif(sinif).Koyu,
                () => _ayar.SinifRenk[si], Palet.VarsayilanSinif(sinif).Acik, r => RenkDegisti(() => _ayar.SinifRenk[si] = r), yarim: true));
        }
        _ogeler.Add(new RenkSecim("Kendi satır rengim", null, () => Palet.YerelRenk, () => Palet.SabitSatir,
            () => _ayar.YerelRenk, Palet.Yerel, r => RenkDegisti(() => _ayar.YerelRenk = r), yarim: false));
        _ogeler.Add(new Bolum("Yazı ve çubuk"));
        Action yaziBitti = () => { _ayar.Kaydet(); _b.YaziBitti(); };
        _ogeler.Add(new Kaydirici("Ad yazısı", Ayarlar.EnAzYazi, Ayarlar.EnFazlaYazi, () => _ayar.YaziAd,
            v => { _ayar.YaziAd = v; _b.GorunumDegisti(); }, yaziBitti));
        _ogeler.Add(new Kaydirici("DPS yazısı", Ayarlar.EnAzYazi, Ayarlar.EnFazlaYazi, () => _ayar.YaziDps,
            v => { _ayar.YaziDps = v; _b.GorunumDegisti(); }, yaziBitti));
        _ogeler.Add(new Kaydirici("Boss yazısı", Ayarlar.EnAzYazi, Ayarlar.EnFazlaYazi, () => _ayar.YaziBoss,
            v => { _ayar.YaziBoss = v; _b.GorunumDegisti(); }, yaziBitti));
        _ogeler.Add(new Kaydirici("Pay çubuğu opaklığı", 0, 100, () => _ayar.CubukOpaklik,
            v => { _ayar.CubukOpaklik = v; _b.GorunumDegisti(); }, _ayar.Kaydet));
        _ogeler.Add(new Dugme(() => "Tüm renkler ve yazılar", () => "Varsayılana dön", () => RenkDegisti(_ayar.RenkSifirla))
        { Etkin = () => !_ayar.RenkVarsayilan });
        for (int i = ilk; i < _ogeler.Count; i++) _ogeler[i].Sekme = 1;

        // ---- Sekme 2: Kukla ve alarm ----
        ilk = _ogeler.Count;
        string[] sureler = new string[Ayarlar.KuklaSureleri.Length + 1];
        for (int i = 0; i < Ayarlar.KuklaSureleri.Length; i++) sureler[i] = Ayarlar.KuklaSureleri[i].ToString(Bicim.Tr) + " sn";
        sureler[^1] = "Özel";
        _ogeler.Add(new Bolum("Kukla testi"));
        _ogeler.Add(new Secim("Süre", sureler,
            () => _ayar.KuklaOzel ? sureler.Length - 1 : Array.IndexOf(Ayarlar.KuklaSureleri, _ayar.KuklaSure),
            i =>
            {
                _ayar.KuklaOzel = i == sureler.Length - 1;
                if (!_ayar.KuklaOzel) _ayar.KuklaSure = Ayarlar.KuklaSureleri[i];
                _ayar.Kaydet();
            }) { Etkin = () => !_b.KuklaEtkin() });
        _ogeler.Add(new Kaydirici("Özel süre", Ayarlar.EnAzKukla, Ayarlar.EnFazlaKukla, () => _ayar.KuklaOzelSure,
            v => _ayar.KuklaOzelSure = v, _ayar.Kaydet, v => v.ToString(Bicim.Tr) + " sn")
        { Etkin = () => _ayar.KuklaOzel && !_b.KuklaEtkin() });
        _ogeler.Add(new Dugme(() => _b.KuklaEtkin() ? "Test sürüyor" : "Kukla dövüşü",
            () => _b.KuklaEtkin() ? "İptal et" : "Başlat",
            () => { if (_b.KuklaEtkin()) _b.KuklaIptal(); else _b.KuklaBaslat(); },
            "Sayaç sıfırlanır; ilk vuruşunla geri sayım başlar"));
        _ogeler.Add(new KisayolSatiri());

        _ogeler.Add(new Bolum("Alarm"));
        _ogeler.Add(new Kaydirici("Alarm sesi", Ayarlar.EnAzAlarmSes, Ayarlar.EnFazlaAlarmSes, () => _ayar.AlarmSes,
            v => _ayar.AlarmSes = v, () => { _ayar.Kaydet(); Zil.Cal(_ayar.AlarmSes); })); // bırakınca bir kez önizleme
        _ogeler.Add(new Dugme(() => _b.AlarmKalan() is string k ? "Alarm · " + k + " kaldı" : "Alarm",
            () => _b.AlarmKalan() is null ? "Kur…" : "Kapat",
            () => { if (_b.AlarmKalan() is null) _b.AlarmKur(); else _b.AlarmIptal(); },
            "Oyunda: Alt basılı tut, overlay'deki saate tıkla"));

        _ogeler.Add(new Bolum("Uyarı"));
        _ogeler.Add(new Anahtar("Öfke uyarısı sesi", "Öfkeye 30 sn kala ve yetişmeyecekse kısa ses", () => _ayar.OfkeSesi, () =>
        {
            _ayar.OfkeSesi = !_ayar.OfkeSesi;
            _ayar.Kaydet();
            if (_ayar.OfkeSesi)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { /* önizleme */ }
            }
        }));
        // Gecikme uyarısı: overlay her tikte ayara bakar, değişiklik hemen geçerli.
        string[] pingEsikleri = Array.ConvertAll(PingUyarisi.Esikler, v => v.ToString(Bicim.Tr) + " ms");
        _ogeler.Add(new Anahtar("Gecikme uyarısı", "Ping eşiği aşınca overlay'de kırmızı \"320 ms\"", () => _ayar.PingUyari, () =>
        {
            _ayar.PingUyari = !_ayar.PingUyari;
            _ayar.Kaydet();
        }));
        _ogeler.Add(new Secim("Gecikme eşiği", pingEsikleri, () => Array.IndexOf(PingUyarisi.Esikler, _ayar.PingEsik),
            i => { _ayar.PingEsik = PingUyarisi.Esikler[i]; _ayar.Kaydet(); }) { Etkin = () => _ayar.PingUyari });

        _ogeler.Add(new Bolum("Gizli Küp"));
        _ogeler.Add(new Anahtar("Gizli Küp sesi", "Yakına bir küp gelince kısa, yumuşak ses", () => _ayar.KupSesi, () =>
        {
            _ayar.KupSesi = !_ayar.KupSesi;
            _ayar.Kaydet();
            if (_ayar.KupSesi) Zil.Kup(_ayar.KupSes); // açınca bir kez önizleme
        }));
        _ogeler.Add(new Kaydirici("Küp sesi düzeyi", Ayarlar.EnAzAlarmSes, Ayarlar.EnFazlaAlarmSes, () => _ayar.KupSes,
            v => _ayar.KupSes = v, () => { _ayar.Kaydet(); Zil.Kup(_ayar.KupSes); }) // bırakınca bir kez önizleme
        { Etkin = () => _ayar.KupSesi });
        _ogeler.Add(new Dugme(() => "Küp sesi", () => "Dene", () => Zil.Kup(_ayar.KupSes)) { Etkin = () => _ayar.KupSesi });
        for (int i = ilk; i < _ogeler.Count; i++) _ogeler[i].Sekme = 2;

        ClientSize = Yerlesim();
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);

    private static int Olc(string metin, Font font) =>
        metin.Length == 0 ? 0 : TextRenderer.MeasureText(metin, font, Size.Empty, Temel).Width;

    /// <summary>Overlay'den: durum değişti (model indirildi, denetim bitti) → yeniden çiz.</summary>
    public void Yenile()
    {
        if (!IsDisposed) Invalidate();
    }

    /// <summary>Renk/yazı değişti: kaydet, overlay'e (ve detay paneline) canlı uygula, yeniden çiz.</summary>
    private void RenkDegisti(Action degistir)
    {
        degistir();
        _ayar.Kaydet();
        _b.GorunumDegisti();
        _b.YaziBitti(); // "Varsayılana dön" yazıyı da değiştirmiş olabilir
        Invalidate();
    }

    // ---------------- Yerleşim ----------------

    private Size Yerlesim()
    {
        int w = P(440), pad = P(20);
        int x0 = pad, x1 = w - pad;
        _baslikAlani = new Rectangle(0, 0, w, P(50));
        int k = P(18);
        _kapatKutusu = new Rectangle(w - P(14) - k, (P(50) - k) / 2, k, k);

        // Sekme şeridi: başlığın altında tam genişlik
        int sy = P(50) + P(12), sh = P(30);
        int sw = (x1 - x0) / Sekmeler.Length;
        for (int i = 0; i < Sekmeler.Length; i++)
            _sekmeKutu[i] = new Rectangle(x0 + i * sw, sy, i == Sekmeler.Length - 1 ? x1 - x0 - i * sw : sw, sh);
        int ust = sy + sh + P(4);

        // Her sekme aynı noktadan başlar; pencere en uzun sekmenin boyunda (sekme değişince boyut zıplamaz).
        int enAlt = ust;
        for (int t = 0; t < Sekmeler.Length; t++)
        {
            int y = ust;
            bool solDolu = false; // yarım genişlikli renk hücresi: önce sol, sonra sağ
            int yarimH = P(36), yarimW = (x1 - x0 - P(16)) / 2;
            foreach (var o in _ogeler)
            {
                if (o.Sekme != t) continue;
                if (o is RenkSecim { Yarim: true } yr)
                {
                    o.Alan = solDolu ? new Rectangle(x1 - yarimW, y, yarimW, yarimH) : new Rectangle(x0, y, yarimW, yarimH);
                    yr.Kutu = new Rectangle(o.Alan.Right - P(44), y + (yarimH - P(20)) / 2, P(44), P(20));
                    if (solDolu) y += yarimH;
                    solDolu = !solDolu;
                    continue;
                }
                if (solDolu)
                {
                    y += yarimH;
                    solDolu = false;
                }
                switch (o)
                {
                    case Bolum:
                        o.Alan = new Rectangle(x0, y, x1 - x0, P(34));
                        break;
                    case Secim s:
                    {
                        o.Alan = new Rectangle(x0, y, x1 - x0, P(38));
                        int n = s.Secenekler.Length;
                        int parcaW = 0;
                        foreach (string m in s.Secenekler) parcaW = Math.Max(parcaW, Olc(m, _yaziKucuk) + P(16));
                        int toplam = parcaW * n;
                        // Eşit parçalar etiketin yanına sığmıyorsa (ör. bölge seçimi) her parça kendi metni kadar geniş.
                        bool dar = toplam > x1 - x0 - Olc(s.Etiket, _yazi) - P(12);
                        var genislik = new int[n];
                        if (dar)
                        {
                            toplam = 0;
                            for (int i = 0; i < n; i++) toplam += genislik[i] = Olc(s.Secenekler[i], _yaziKucuk) + P(12);
                        }
                        else Array.Fill(genislik, parcaW);
                        int sx = x1 - toplam, syy = y + (P(38) - P(26)) / 2;
                        s.Parcalar = new Rectangle[n];
                        for (int i = 0; i < n; i++)
                        {
                            s.Parcalar[i] = new Rectangle(sx, syy, genislik[i], P(26));
                            sx += genislik[i];
                        }
                        break;
                    }
                    case Kaydirici kd:
                        o.Alan = new Rectangle(x0, y, x1 - x0, P(38));
                        kd.Iz = new Rectangle(x1 - P(56) - P(170), y + P(38) / 2 - P(2), P(170), P(4));
                        break;
                    case Anahtar a:
                    {
                        int h = a.Aciklama is null ? P(38) : P(48);
                        o.Alan = new Rectangle(x0, y, x1 - x0, h);
                        a.Dugme = new Rectangle(x1 - P(38), y + (h - P(20)) / 2, P(38), P(20));
                        break;
                    }
                    case Dugme d:
                    {
                        int h = d.Aciklama is null ? P(42) : P(50);
                        o.Alan = new Rectangle(x0, y, x1 - x0, h);
                        int dw = Math.Max(Olc(d.Metin(), _yaziKucuk), P(96)) + P(28);
                        d.Kutu = new Rectangle(x1 - dw, y + (h - P(28)) / 2, dw, P(28));
                        break;
                    }
                    case KisayolSatiri ks:
                    {
                        o.Alan = new Rectangle(x0, y, x1 - x0, P(58));
                        int by = y + P(6), bh = P(28);
                        ks.Kapat = new Rectangle(x1 - P(64), by, P(64), bh);
                        ks.Degistir = new Rectangle(ks.Kapat.Left - P(6) - P(84), by, P(84), bh);
                        ks.Deger = new Rectangle(ks.Degistir.Left - P(8) - P(124), by, P(124), bh);
                        break;
                    }
                    case RenkSecim r:
                        o.Alan = new Rectangle(x0, y, x1 - x0, P(40));
                        r.Kutu = new Rectangle(x1 - P(44), y + (P(40) - P(20)) / 2, P(44), P(20));
                        break;
                }
                y += o.Alan.Height;
            }
            if (solDolu) y += yarimH;
            enAlt = Math.Max(enAlt, y);
        }
        return new Size(w, enAlt + P(10) + P(30) + P(10));
    }

    // ---------------- Pencere ----------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10 */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        if (_palet is not null) PaletKapat(); // önce açık palet kapanır
        else Close();
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Ayarlar çizimi", ex); }
    }

    private void Ciz(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        int pad = P(20);

        // Başlık
        TextRenderer.DrawText(g, "Ayarlar", _yaziBaslik, new Rectangle(pad, 0, w / 2, P(50)), Palet.Yazi, Sol);
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int ki = P(5);
        g.DrawLine(_kalemKapat, _kapatKutusu.Left + ki, _kapatKutusu.Top + ki, _kapatKutusu.Right - ki, _kapatKutusu.Bottom - ki);
        g.DrawLine(_kalemKapat, _kapatKutusu.Right - ki, _kapatKutusu.Top + ki, _kapatKutusu.Left + ki, _kapatKutusu.Bottom - ki);
        g.SmoothingMode = eski;
        g.DrawLine(_kalemKenar, 0, P(50) - 1, w, P(50) - 1);

        // Sekmeler
        var serit = Rectangle.FromLTRB(_sekmeKutu[0].Left, _sekmeKutu[0].Top, _sekmeKutu[^1].Right, _sekmeKutu[0].Bottom);
        Cizim.YuvarlakDoldur(g, _fircaPanel, serit, P(6));
        for (int i = 0; i < Sekmeler.Length; i++)
        {
            var r = _sekmeKutu[i];
            if (i == _sekme) Cizim.YuvarlakDoldur(g, _fircaSecili, Rectangle.Inflate(r, -P(3), -P(3)), P(4));
            var renk = i == _sekme ? Palet.Dps : i == _sekmeUstunde ? Palet.Yazi : Palet.Soluk;
            TextRenderer.DrawText(g, Sekmeler[i], i == _sekme ? _yaziBolum : _yaziKucuk, r, renk, Orta);
        }

        foreach (var o in _ogeler)
        {
            if (o.Sekme != _sekme) continue;
            bool etkin = o.EtkinMi;
            var r = o.Alan;
            switch (o)
            {
                case Bolum b:
                    TextRenderer.DrawText(g, b.Baslik, _yaziBolum, new Rectangle(r.X, r.Y + P(12), r.Width, r.Height - P(12)), Palet.Soluk, Sol);
                    break;
                case Secim s:
                {
                    Etiket(g, s.Etiket, null, r, etkin);
                    int secili = s.Secili();
                    var dis = Rectangle.FromLTRB(s.Parcalar[0].Left, s.Parcalar[0].Top, s.Parcalar[^1].Right, s.Parcalar[0].Bottom);
                    Cizim.YuvarlakDoldur(g, _fircaPanel, dis, P(5));
                    for (int i = 0; i < s.Parcalar.Length; i++)
                    {
                        var p = s.Parcalar[i];
                        if (i == secili)
                        {
                            var ic = Rectangle.Inflate(p, -P(2), -P(2));
                            Cizim.YuvarlakDoldur(g, _fircaSecili, ic, P(4));
                        }
                        var renk = !etkin ? (i == secili ? Palet.Soluk : Palet.CokSoluk)
                            : i == secili ? Palet.Dps : (_ustunde == o ? Palet.Yazi : Palet.Soluk);
                        TextRenderer.DrawText(g, s.Secenekler[i], _yaziKucuk, p, renk, Orta);
                    }
                    break;
                }
                case Kaydirici k:
                {
                    Etiket(g, k.Etiket, null, r, etkin);
                    int v = k.Deger();
                    float oran = (float)(v - k.Min) / Math.Max(1, k.Max - k.Min);
                    Cizim.YuvarlakDoldur(g, _fircaPanelUst, k.Iz, P(2));
                    int dolu = (int)(k.Iz.Width * oran);
                    if (dolu > 0) Cizim.YuvarlakDoldur(g, etkin ? _fircaVurgu : _fircaPanelUst, new Rectangle(k.Iz.X, k.Iz.Y, dolu, k.Iz.Height), P(2));
                    int t = P(14);
                    var topuz = new RectangleF(k.Iz.X + dolu - t / 2f, k.Iz.Y + k.Iz.Height / 2f - t / 2f, t, t);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(etkin ? _fircaTopuz : _fircaTopuzKapali, topuz);
                    g.SmoothingMode = eski;
                    TextRenderer.DrawText(g, k.Yazi(v), _yaziDeger,
                        new Rectangle(k.Iz.Right + P(8), r.Y, r.Right - k.Iz.Right - P(8), r.Height), etkin ? Palet.Yazi : Palet.CokSoluk, Sag);
                    break;
                }
                case Anahtar a:
                {
                    Etiket(g, a.Etiket, a.Aciklama, r, etkin);
                    bool acik = a.Deger();
                    Cizim.YuvarlakDoldur(g, acik ? _fircaVurgu : _fircaPanelUst, a.Dugme, a.Dugme.Height / 2f);
                    int t = a.Dugme.Height - P(6);
                    int tx = acik ? a.Dugme.Right - P(3) - t : a.Dugme.Left + P(3);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(acik ? _fircaTopuz : _fircaTopuzKapali, tx, a.Dugme.Top + P(3), t, t);
                    g.SmoothingMode = eski;
                    break;
                }
                case Dugme d:
                {
                    Etiket(g, d.Etiket(), d.Aciklama, r, true, d.Kutu.Left - P(10));
                    Cizim.YuvarlakDoldur(g, etkin && _ustunde == o ? _fircaSecili : _fircaPanelUst, d.Kutu, P(5));
                    TextRenderer.DrawText(g, d.Metin(), _yaziKucuk, d.Kutu, etkin ? Palet.Yazi : Palet.CokSoluk, Orta);
                    break;
                }
                case RenkSecim rs:
                    CizRenkSecim(g, rs);
                    break;
                case KisayolSatiri ks:
                    CizKisayol(g, ks);
                    break;
            }
        }

        TextRenderer.DrawText(g, "Değişiklikler hemen uygulanır ve kaydedilir.", _yaziKucuk,
            new Rectangle(pad, h - P(40), w - 2 * pad, P(30)), Palet.CokSoluk, Sol);

        if (_palet is not null) CizPalet(g);
        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    /// <summary>Renk kutusu overlay satırının küçük bir örneği: koyu çubuk + solda açık şerit + ortada açık nokta.</summary>
    private void RenkKutusu(Graphics g, Rectangle kutu, Color acik, Color koyu, bool vurgulu)
    {
        _fircaRenk.Color = koyu;
        Cizim.YuvarlakDoldur(g, _fircaRenk, kutu, P(4));
        _fircaRenk.Color = acik;
        g.FillRectangle(_fircaRenk, kutu.X + P(1), kutu.Y + P(4), Math.Max(2, P(3)), kutu.Height - P(8));
        int d = P(10);
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(_fircaRenk, kutu.X + (kutu.Width - d) / 2f + P(2), kutu.Y + (kutu.Height - d) / 2f, d, d);
        if (vurgulu) Cizim.YuvarlakCiz(g, _kalemSeciliKenar, kutu, P(4));
        g.SmoothingMode = eski;
    }

    private void CizRenkSecim(Graphics g, RenkSecim rs)
    {
        var r = rs.Alan;
        bool vurgulu = _ustunde == rs || _palet == rs;
        if (rs.Yarim)
        {
            // [ikon] Sınıf ......... [kutu]
            int ikon = P(18);
            int x = r.X;
            if (_b.Ikonlar is { } ik) g.DrawImage(ik.Al(rs.Sinif), x, r.Y + (r.Height - ikon) / 2, ikon, ikon);
            x += ikon + P(8);
            TextRenderer.DrawText(g, rs.Etiket, _yazi, new Rectangle(x, r.Y, Math.Max(0, rs.Kutu.Left - P(6) - x), r.Height),
                vurgulu ? Palet.Dps : Palet.Yazi, Sol);
        }
        else
        {
            // Kendi satır rengim ...... Adın (o renkte) [kutu]
            Etiket(g, rs.Etiket, null, r, true);
            string ornek = string.IsNullOrWhiteSpace(_ayar.YerelAd) ? "Adın" : _ayar.YerelAd!;
            int ow = Math.Min(Olc(ornek, _yaziDeger), P(150));
            TextRenderer.DrawText(g, ornek, _yaziDeger, new Rectangle(rs.Kutu.Left - P(10) - ow, r.Y, ow, r.Height), Palet.YerelYazi, Sol);
        }
        RenkKutusu(g, rs.Kutu, rs.Acik(), rs.Koyu(), vurgulu);
    }

    private void CizKisayol(Graphics g, KisayolSatiri ks)
    {
        var r = ks.Alan;
        TextRenderer.DrawText(g, "Kısayol", _yazi, new Rectangle(r.X, ks.Deger.Y, ks.Deger.Left - r.X - P(8), ks.Deger.Height), Palet.Yazi, Sol);

        // Mevcut kombinasyon (yakalarken çerçeveli "Tuşlara bas…")
        string k = _b.KuklaKisayol();
        Cizim.YuvarlakDoldur(g, _fircaPanel, ks.Deger, P(5));
        if (_yakalaniyor)
        {
            Cizim.YuvarlakCiz(g, _kalemSeciliKenar, ks.Deger, P(5));
            TextRenderer.DrawText(g, "Tuşlara bas…", _yaziKucuk, ks.Deger, Palet.Vurgu, Orta);
        }
        else TextRenderer.DrawText(g, k.Length > 0 ? k : "Kapalı", k.Length > 0 ? _yaziDeger : _yaziKucuk, ks.Deger,
            k.Length > 0 ? Palet.Dps : Palet.CokSoluk, Orta);

        Cizim.YuvarlakDoldur(g, _ustunde == ks && _kisayolUstunde == 1 ? _fircaSecili : _fircaPanelUst, ks.Degistir, P(5));
        TextRenderer.DrawText(g, _yakalaniyor ? "Vazgeç" : "Değiştir", _yaziKucuk, ks.Degistir, Palet.Yazi, Orta);
        bool kapatEtkin = k.Length > 0 && !_yakalaniyor;
        Cizim.YuvarlakDoldur(g, kapatEtkin && _ustunde == ks && _kisayolUstunde == 2 ? _fircaSecili : _fircaPanelUst, ks.Kapat, P(5));
        TextRenderer.DrawText(g, "Kapat", _yaziKucuk, ks.Kapat, kapatEtkin ? Palet.Yazi : Palet.CokSoluk, Orta);

        string alt = _kisayolHata ?? (_yakalaniyor ? "En az bir Ctrl / Alt / Shift / Win + tuş · Esc: vazgeç"
            : k.Length == 0 ? "Kısayol kapalı; Değiştir ile yenisini ata"
            : "Basınca test başlar; tekrar basınca iptal eder ya da kartı kapatır");
        TextRenderer.DrawText(g, alt, _yaziKucuk, new Rectangle(r.X, ks.Deger.Bottom + P(4), r.Width, P(16)),
            _kisayolHata is not null ? Palet.Kirmizi : Palet.Soluk, Sol);
    }

    private int _kisayolUstunde; // 1 Değiştir, 2 Kapat

    // ---------------- Kısayol yakalama ----------------

    private void YakalamaBaslat()
    {
        _yakalaniyor = true;
        _kisayolHata = null;
        _b.KisayolYakalama(true);
        Invalidate();
    }

    private void YakalamaBitir()
    {
        if (!_yakalaniyor) return;
        _yakalaniyor = false;
        _b.KisayolYakalama(false);
        if (!IsDisposed) Invalidate();
    }

    private void KisayolDene(string metin)
    {
        _kisayolHata = _b.KuklaKisayolAyarla(metin);
        YakalamaBitir();
    }

    /// <summary>Overlay'den: yakalarken sabit bir genel kısayola basıldı (WM_HOTKEY'e gitti); onu da dene (reddedilir).</summary>
    public void KisayolYakalandi(string metin)
    {
        if (_yakalaniyor) KisayolDene(metin);
    }

    /// <summary>Yakalarken her tuş buraya gelir (Alt'lı kombinasyonlar dahil, WM_SYSKEYDOWN da işlenir).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_yakalaniyor) return base.ProcessCmdKey(ref msg, keyData);
        Keys tus = keyData & Keys.KeyCode;
        if (tus == Keys.Escape && (keyData & Keys.Modifiers) == Keys.None)
        {
            YakalamaBitir();
            return true;
        }
        if (Kisayol.DegistiriciMi(tus) || tus == Keys.None) return true; // değiştiricinin kendisi: tuşu bekle
        uint d = 0;
        if ((keyData & Keys.Control) != 0) d |= Native.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) d |= Native.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) d |= Native.MOD_SHIFT;
        if ((Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 || (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0)
            d |= Native.MOD_WIN;
        if (d == 0)
        {
            _kisayolHata = "En az bir Ctrl / Alt / Shift / Win gerekli";
            Invalidate();
            return true;
        }
        KisayolDene(Kisayol.Metin(d, tus));
        return true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        YakalamaBitir(); // başka pencereye geçildi (ör. Win tuşu): yakalama biter, eski kısayol geri gelir
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        YakalamaBitir();
        base.OnFormClosed(e);
    }

    private void CizPalet(Graphics g)
    {
        var rs = _palet!;
        var eski = g.SmoothingMode;
        Cizim.YuvarlakDoldur(g, _fircaPanel, _paletAlani, P(6));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Cizim.YuvarlakCiz(g, _kalemSeciliKenar, _paletAlani, P(6));
        int? secili = rs.Secili();
        for (int i = 0; i < _paletKutu.Length; i++)
        {
            var c = Palet.Secenekler[i].Renk;
            var k = _paletKutu[i];
            _fircaRenk.Color = c;
            g.FillEllipse(_fircaRenk, Rectangle.Inflate(k, -P(3), -P(3)));
            bool bu = secili == Palet.RgbAl(c);
            if (bu) g.DrawEllipse(_kalemHalka, Rectangle.Inflate(k, -P(1), -P(1)));
            else if (_paletUstunde == i) g.DrawEllipse(_kalemHalkaSoluk, Rectangle.Inflate(k, -P(1), -P(1)));
        }
        g.SmoothingMode = eski;

        // Varsayılan: küçük renk noktası + yazı (seçiliyse vurgulu)
        var v = _paletVarsayilan;
        Cizim.YuvarlakDoldur(g, _paletUstunde == Palet.Secenekler.Length ? _fircaSecili : _fircaPanelUst, v, P(5));
        int d = P(10);
        _fircaRenk.Color = rs.Varsayilan;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(_fircaRenk, v.X + P(10), v.Y + (v.Height - d) / 2f, d, d);
        g.SmoothingMode = eski;
        TextRenderer.DrawText(g, "Varsayılan", _yaziKucuk, new Rectangle(v.X + P(10) + d + P(8), v.Y, v.Width - P(28) - d, v.Height),
            secili is null ? Palet.Dps : Palet.Yazi, Sol);
        // Üzerine gelinen rengin adı (sağda)
        string ad = _paletUstunde >= 0 && _paletUstunde < Palet.Secenekler.Length ? Palet.Secenekler[_paletUstunde].Ad : "";
        if (ad.Length > 0)
            TextRenderer.DrawText(g, ad, _yaziKucuk, new Rectangle(v.X, v.Y, v.Width - P(10), v.Height), Palet.Soluk, Sag);
    }

    private void Etiket(Graphics g, string etiket, string? aciklama, Rectangle r, bool etkin, int sag = 0)
    {
        var renk = etkin ? Palet.Yazi : Palet.CokSoluk;
        int genislik = sag > 0 ? sag - r.X : r.Width - P(60);
        if (aciklama is null)
        {
            TextRenderer.DrawText(g, etiket, _yazi, new Rectangle(r.X, r.Y, sag > 0 ? genislik : r.Width / 2, r.Height), renk, Sol);
            return;
        }
        int ust = r.Y + (r.Height - P(18) - P(15)) / 2;
        TextRenderer.DrawText(g, etiket, _yazi, new Rectangle(r.X, ust, genislik, P(18)), renk, Sol);
        TextRenderer.DrawText(g, aciklama, _yaziKucuk, new Rectangle(r.X, ust + P(18), genislik, P(15)), Palet.Soluk, Sol);
    }

    // ---------------- Renk paleti ----------------

    private void PaletAc(RenkSecim rs)
    {
        int hucre = P(26), ara = P(4), ic = P(10);
        int pw = 8 * hucre + 7 * ara + 2 * ic;
        int ph = ic + 2 * hucre + ara + P(10) + P(28) + ic;
        int px = Math.Clamp(rs.Kutu.Right - pw, P(8), ClientSize.Width - pw - P(8));
        int py = rs.Kutu.Bottom + P(6);
        if (py + ph > ClientSize.Height - P(8)) py = rs.Kutu.Top - P(6) - ph;
        _paletAlani = new Rectangle(px, py, pw, ph);
        for (int i = 0; i < _paletKutu.Length; i++)
            _paletKutu[i] = new Rectangle(px + ic + (i % 8) * (hucre + ara), py + ic + (i / 8) * (hucre + ara), hucre, hucre);
        _paletVarsayilan = new Rectangle(px + ic, py + ic + 2 * hucre + ara + P(10), pw - 2 * ic, P(28));
        _palet = rs;
        _paletUstunde = -1;
        Invalidate();
    }

    private void PaletKapat()
    {
        _palet = null;
        _paletUstunde = -1;
        Invalidate();
    }

    private int PaletBul(Point p)
    {
        for (int i = 0; i < _paletKutu.Length; i++) if (_paletKutu[i].Contains(p)) return i;
        return _paletVarsayilan.Contains(p) ? Palet.Secenekler.Length : -1;
    }

    // ---------------- Fare ----------------

    private Oge? OgeBul(Point p)
    {
        foreach (var o in _ogeler)
        {
            if (o.Sekme != _sekme) continue;
            switch (o)
            {
                case Secim s when Array.Exists(s.Parcalar, r => r.Contains(p)): return o;
                case Kaydirici k when Rectangle.Inflate(k.Iz, P(8), P(12)).Contains(p): return o;
                case Anahtar a when o.Alan.Contains(p): return o;   // satırın her yeri anahtarı değiştirir
                case Dugme d when d.Kutu.Contains(p): return o;
                case RenkSecim when o.Alan.Contains(p): return o;   // hücrenin her yeri paleti açar
                case KisayolSatiri ks when ks.Degistir.Contains(p) || ks.Kapat.Contains(p): return o;
            }
        }
        return null;
    }

    private int SekmeBul(Point p) => Array.FindIndex(_sekmeKutu, r => r.Contains(p));

    private void KaydiriciAyarla(Kaydirici k, int x)
    {
        float oran = Math.Clamp((float)(x - k.Iz.X) / Math.Max(1, k.Iz.Width), 0f, 1f);
        int v = k.Min + (int)MathF.Round(oran * (k.Max - k.Min));
        if (v == k.Deger()) return;
        k.Ayarla(v);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _palet is not null) return; // palet açıkken tıklama bırakınca işlenir
        var o = OgeBul(e.Location);
        if (o is Kaydirici k && k.EtkinMi)
        {
            _suruklenen = k;
            Capture = true;
            KaydiriciAyarla(k, e.X);
        }
        else if (o is null && _baslikAlani.Contains(e.Location) && !_kapatKutusu.Contains(e.Location))
        {
            _pencereSurukle = true;
            _fareBas = Cursor.Position;
            _konumBas = Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_suruklenen is not null)
        {
            KaydiriciAyarla(_suruklenen, e.X);
            return;
        }
        if (_pencereSurukle)
        {
            var p = Cursor.Position;
            Location = new Point(_konumBas.X + p.X - _fareBas.X, _konumBas.Y + p.Y - _fareBas.Y);
            return;
        }
        if (_palet is not null)
        {
            int pi = PaletBul(e.Location);
            Cursor = pi >= 0 ? Cursors.Hand : Cursors.Default;
            if (pi != _paletUstunde)
            {
                _paletUstunde = pi;
                Invalidate(_paletAlani);
            }
            return;
        }
        int si = SekmeBul(e.Location);
        if (si != _sekmeUstunde)
        {
            _sekmeUstunde = si;
            Invalidate();
        }
        var o = OgeBul(e.Location);
        int ku = o is KisayolSatiri kss ? (kss.Degistir.Contains(e.Location) ? 1 : 2) : 0;
        if (ku != _kisayolUstunde)
        {
            _kisayolUstunde = ku;
            Invalidate();
        }
        bool el = (o is not null && o.EtkinMi) || _kapatKutusu.Contains(e.Location) || (si >= 0 && si != _sekme);
        Cursor = el ? Cursors.Hand : Cursors.Default;
        if (o != _ustunde)
        {
            _ustunde = o;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_ustunde is null && _sekmeUstunde < 0) return;
        _ustunde = null;
        _sekmeUstunde = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_suruklenen is not null)
        {
            var k = _suruklenen;
            _suruklenen = null;
            Capture = false;
            k.Bitti(); // kaydırma bitince bir kez kaydet
            return;
        }
        if (_pencereSurukle)
        {
            _pencereSurukle = false;
            Capture = false;
            return;
        }
        if (_palet is { } rs)
        {
            // Palet açık: renk / varsayılan seçilir; dışarı tıklama sadece kapatır.
            int pi = PaletBul(e.Location);
            if (pi >= 0)
            {
                int? yeni = pi < Palet.Secenekler.Length ? Palet.RgbAl(Palet.Secenekler[pi].Renk) : null;
                if (yeni is int y && y == Palet.RgbAl(rs.Varsayilan)) yeni = null; // varsayılanla aynı renk = varsayılan
                rs.Sec(yeni);
            }
            if (pi >= 0 || !_paletAlani.Contains(e.Location)) PaletKapat();
            return;
        }
        if (_kapatKutusu.Contains(e.Location))
        {
            Close();
            return;
        }
        int si = SekmeBul(e.Location);
        if (si >= 0)
        {
            if (si != _sekme)
            {
                _sekme = si;
                _ustunde = null;
                Invalidate();
            }
            return;
        }
        var o = OgeBul(e.Location);
        if (o is null || !o.EtkinMi) return;
        switch (o)
        {
            case Secim s:
                int i = Array.FindIndex(s.Parcalar, r => r.Contains(e.Location));
                if (i >= 0 && i != s.Secili()) s.Sec(i);
                break;
            case Anahtar a:
                a.Degistir();
                break;
            case Dugme d:
                d.Tikla();
                break;
            case RenkSecim r:
                PaletAc(r);
                return;
            case KisayolSatiri ks:
                if (ks.Degistir.Contains(e.Location))
                {
                    if (_yakalaniyor) YakalamaBitir();
                    else YakalamaBaslat();
                }
                else if (!_yakalaniyor && _b.KuklaKisayol().Length > 0)
                {
                    _kisayolHata = _b.KuklaKisayolAyarla(""); // kısayol yok
                }
                break;
        }
        if (!IsDisposed) Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yaziBaslik.Dispose(); _yazi.Dispose(); _yaziKucuk.Dispose(); _yaziBolum.Dispose(); _yaziDeger.Dispose();
            _fircaPanel.Dispose(); _fircaPanelUst.Dispose(); _fircaVurgu.Dispose(); _fircaSecili.Dispose();
            _fircaTopuz.Dispose(); _fircaTopuzKapali.Dispose(); _fircaRenk.Dispose();
            _kalemKenar.Dispose(); _kalemSeciliKenar.Dispose(); _kalemKapat.Dispose(); _kalemHalka.Dispose(); _kalemHalkaSoluk.Dispose();
        }
        base.Dispose(disposing);
    }
}
