using System.Drawing.Drawing2D;
using AionMeter.Cevirmen;

namespace AionMeter;

/// <summary>
/// Tepsi "Ayarlar…": tek küçük koyu pencere, tamamen kodla çizilir (overlay ile aynı palet ve yazı tipleri).
/// Her değişiklik anında uygulanır ve ayarlar.ini'ye yazılır; saydamlık kaydırılırken overlay canlı değişir.
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
    }

    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;
    private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | Temel;

    // ---- Öğeler ----
    private abstract class Oge
    {
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
    private sealed class Kaydirici(string etiket, int min, int max, Func<int> deger, Action<int> ayarla, Action bitti) : Oge
    {
        public readonly string Etiket = etiket;
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
    private sealed class Dugme(Func<string> etiket, Func<string> metin, Action tikla) : Oge
    {
        public readonly Func<string> Etiket = etiket;
        public readonly Func<string> Metin = metin;
        public readonly Action Tikla = tikla;
        public Rectangle Kutu;
    }

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

        string[] boyutlar = Array.ConvertAll(Ayarlar.BoyutSecenekleri, v => "%" + v.ToString(Bicim.Tr));
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

    // ---------------- Yerleşim ----------------

    private Size Yerlesim()
    {
        int w = P(440), pad = P(20);
        int x0 = pad, x1 = w - pad;
        int y = 0;
        _baslikAlani = new Rectangle(0, 0, w, P(50));
        int k = P(18);
        _kapatKutusu = new Rectangle(w - P(14) - k, (P(50) - k) / 2, k, k);
        y += P(50);

        foreach (var o in _ogeler)
        {
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
                    int sx = x1 - toplam, sy = y + (P(38) - P(26)) / 2;
                    s.Parcalar = new Rectangle[n];
                    for (int i = 0; i < n; i++) s.Parcalar[i] = new Rectangle(sx + i * parcaW, sy, parcaW, P(26));
                    break;
                }
                case Kaydirici kd:
                    o.Alan = new Rectangle(x0, y, x1 - x0, P(38));
                    kd.Iz = new Rectangle(x1 - P(48) - P(180), y + P(38) / 2 - P(2), P(180), P(4));
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
                    o.Alan = new Rectangle(x0, y, x1 - x0, P(42));
                    int dw = Math.Max(Olc("Güncellemeleri denetle", _yaziKucuk), Olc("Denetleniyor…", _yaziKucuk)) + P(28);
                    d.Kutu = new Rectangle(x1 - dw, y + (P(42) - P(28)) / 2, dw, P(28));
                    break;
                }
            }
            y += o.Alan.Height;
        }
        y += P(10);
        return new Size(w, y + P(30) + P(10));
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
        if (e.KeyCode == Keys.Escape) Close();
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

        foreach (var o in _ogeler)
        {
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
                        var renk = i == secili ? Palet.Dps : (_ustunde == o ? Palet.Yazi : Palet.Soluk);
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
                    TextRenderer.DrawText(g, "%" + v.ToString(Bicim.Tr), _yaziDeger,
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
                    Etiket(g, d.Etiket(), null, r, true);
                    Cizim.YuvarlakDoldur(g, etkin && _ustunde == o ? _fircaSecili : _fircaPanelUst, d.Kutu, P(5));
                    TextRenderer.DrawText(g, d.Metin(), _yaziKucuk, d.Kutu, etkin ? Palet.Yazi : Palet.CokSoluk, Orta);
                    break;
                }
            }
        }

        TextRenderer.DrawText(g, "Değişiklikler hemen uygulanır ve kaydedilir.", _yaziKucuk,
            new Rectangle(pad, h - P(40), w - 2 * pad, P(30)), Palet.CokSoluk, Sol);
        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    private void Etiket(Graphics g, string etiket, string? aciklama, Rectangle r, bool etkin)
    {
        var renk = etkin ? Palet.Yazi : Palet.CokSoluk;
        if (aciklama is null)
        {
            TextRenderer.DrawText(g, etiket, _yazi, new Rectangle(r.X, r.Y, r.Width / 2, r.Height), renk, Sol);
            return;
        }
        int ust = r.Y + (r.Height - P(18) - P(15)) / 2;
        TextRenderer.DrawText(g, etiket, _yazi, new Rectangle(r.X, ust, r.Width - P(60), P(18)), renk, Sol);
        TextRenderer.DrawText(g, aciklama, _yaziKucuk, new Rectangle(r.X, ust + P(18), r.Width - P(60), P(15)), Palet.Soluk, Sol);
    }

    // ---------------- Fare ----------------

    private Oge? OgeBul(Point p)
    {
        foreach (var o in _ogeler)
        {
            switch (o)
            {
                case Secim s when Array.Exists(s.Parcalar, r => r.Contains(p)): return o;
                case Kaydirici k when Rectangle.Inflate(k.Iz, P(8), P(12)).Contains(p): return o;
                case Anahtar a when o.Alan.Contains(p): return o;   // satırın her yeri anahtarı değiştirir
                case Dugme d when d.Kutu.Contains(p): return o;
            }
        }
        return null;
    }

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
        if (e.Button != MouseButtons.Left) return;
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
        var o = OgeBul(e.Location);
        bool el = (o is not null && o.EtkinMi) || _kapatKutusu.Contains(e.Location);
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
        if (_ustunde is null) return;
        _ustunde = null;
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
        if (_kapatKutusu.Contains(e.Location))
        {
            Close();
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
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yaziBaslik.Dispose(); _yazi.Dispose(); _yaziKucuk.Dispose(); _yaziBolum.Dispose(); _yaziDeger.Dispose();
            _fircaPanel.Dispose(); _fircaPanelUst.Dispose(); _fircaVurgu.Dispose(); _fircaSecili.Dispose();
            _fircaTopuz.Dispose(); _fircaTopuzKapali.Dispose();
            _kalemKenar.Dispose(); _kalemSeciliKenar.Dispose(); _kalemKapat.Dispose();
        }
        base.Dispose(disposing);
    }
}
