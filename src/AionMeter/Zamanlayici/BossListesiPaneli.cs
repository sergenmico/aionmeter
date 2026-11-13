using System.Drawing.Drawing2D;

namespace AionMeter.Zamanlayici;

/// <summary>Boss listesi satırı: ad ve bir sonraki doğuşa kalan süre ("1 sa 41 dk", "41:20", "şimdi"). <see cref="Soluk"/>: bilgi belirsiz
/// ("canlı?"), ad ve nokta soluk. <see cref="Canli"/>: saha bossu şu an canlı. <see cref="Grup"/>: "Abyss" (takvim) ya da harita adı.
/// <see cref="Anahtar"/>: favori anahtarı (Core.FavoriBoss); <see cref="Favori"/>: adın yanında yıldız.</summary>
internal readonly record struct BossSatiri(string Ad, string Sure, Color SureRengi, bool Rakam, bool Soluk = false, bool Canli = false,
                                           string Grup = "", string Anahtar = "", bool Favori = false);

/// <summary>
/// İstendiğinde açılan boss listesi: Ayarlar'daki Bosslar sekmesiyle aynı gruplu görünüm (<see cref="BossListesiCizimi"/>). Kullanıcı
/// kendisi açtığı için kilitliyken de tıklanır (odak yine alınmaz): sağ üstteki × kapatır, başka yerden tutup taşınır. Ekrana
/// sığmayan liste fare tekerleğiyle kayar.
/// </summary>
internal sealed class BossListesiPaneli : KatmanliPanel
{
    private readonly BossListesiCizimi _cizim;
    private string _altBaslik = "";
    private Font? _yaziBaslik, _yaziKucuk;
    private readonly Pen _kalemKenar = new(Palet.AyiriciAcik);
    private readonly Pen _kalemAyirac = new(Palet.Cizgi);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly Pen _kalemKapatUst = new(Palet.Yazi, 1.5f);
    private Rectangle _kapatKutusu;
    private bool _kapatUstunde;
    private int _listeW, _icerikH, _kaydirma;
    private bool _tasar; // liste ekrana sığmadı: kaydırılır, sağda ince çubuk

    /// <summary>× tıklandı.</summary>
    public event Action? KapatIstendi;

    /// <summary>Panelin en çok yüksekliği (fiziksel px); 0: sınırsız. Aşan liste tekerlekle kaydırılır.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int EnFazlaYukseklik { get; set; }

    public BossListesiPaneli(float olcek) : base("AionMeter Boss Listesi", olcek)
    {
        Suruklenebilir = true;
        _cizim = new BossListesiCizimi(olcek);
        YaziTipleriniKur();
    }

    protected override void YaziTipleriniKur()
    {
        _yaziBaslik?.Dispose(); _yaziKucuk?.Dispose();
        _yaziBaslik = Palet.YaziTipi(12.5f * Olcek, yariKalin: true);
        _yaziKucuk = Palet.YaziTipi(11f * Olcek);
        _cizim.OlcekAyarla(Olcek);
        _kalemKapat.Width = _kalemKapatUst.Width = Math.Max(1f, 1.5f * Olcek);
    }

    private int Pad => P(12);
    private int BaslikH => P(30);
    private int CubukW => P(4);
    private int ListeUst => P(4) + BaslikH;
    private int SabitH => ListeUst + P(10);

    /// <summary>Satırları alır; görünen içerik aynıysa false (çizim yok). <paramref name="sahaVar"/> false: altta harita ipucu.</summary>
    public bool Guncelle(IReadOnlyList<BossSatiri> satirlar, string altBaslik, bool sahaVar)
    {
        bool degisti = _cizim.Guncelle(satirlar, sahaVar);
        if (!degisti && altBaslik == _altBaslik) return false;
        _altBaslik = altBaslik;
        Invalidate();
        return true;
    }

    /// <summary>İçeriğe göre boyut; en çok yüksekliği aşarsa orada kalır ve liste kaydırılır.</summary>
    public Size Boyut()
    {
        int baslikW = Olc("Bosslar", _yaziBaslik!) + P(10) + Olc(_altBaslik, _yaziKucuk!) + P(30);
        _listeW = Math.Max(_cizim.IstenenGenislik(P(260)), baslikW);
        _icerikH = _cizim.IcerikH(_listeW);
        int h = SabitH + _icerikH;
        _tasar = EnFazlaYukseklik > 0 && h > EnFazlaYukseklik;
        if (_tasar) h = Math.Max(SabitH + 3 * _cizim.SatirH, EnFazlaYukseklik);
        _kaydirma = Math.Clamp(_kaydirma, 0, BossListesiCizimi.KaydirmaSiniri(_icerikH, h - SabitH));
        return new Size(Pad + _listeW + (_tasar ? P(8) + CubukW : 0) + Pad, h);
    }

    private Rectangle ListeAlani(Size boyut) => new(Pad, ListeUst, _listeW, Math.Max(0, boyut.Height - SabitH));

    public override void Ciz(Graphics g, Size boyut)
    {
        int w = boyut.Width, h = boyut.Height;
        g.Clear(Palet.Arka);
        int x0 = Pad, x1 = w - Pad, y = P(4);

        // Başlık: "Bosslar  bölge" ve ×
        int k = P(16);
        _kapatKutusu = new Rectangle(x1 - k + P(2), y + (BaslikH - k) / 2, k, k);
        int ki = P(4);
        var eski = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var kalem = _kapatUstunde ? _kalemKapatUst : _kalemKapat;
        g.DrawLine(kalem, _kapatKutusu.Left + ki, _kapatKutusu.Top + ki, _kapatKutusu.Right - ki, _kapatKutusu.Bottom - ki);
        g.DrawLine(kalem, _kapatKutusu.Right - ki, _kapatKutusu.Top + ki, _kapatKutusu.Left + ki, _kapatKutusu.Bottom - ki);
        g.SmoothingMode = eski;
        int bw = Olc("Bosslar", _yaziBaslik!);
        TextRenderer.DrawText(g, "Bosslar", _yaziBaslik!, new Rectangle(x0, y, bw, BaslikH), Palet.Yazi, Sol);
        TextRenderer.DrawText(g, _altBaslik, _yaziKucuk!, new Rectangle(x0 + bw + P(8), y, Math.Max(0, _kapatKutusu.Left - P(6) - x0 - bw - P(8)), BaslikH),
            Palet.CokSoluk, Sol);
        g.DrawLine(_kalemAyirac, x0, ListeUst - 1, x1, ListeUst - 1);

        var alan = ListeAlani(boyut);
        _cizim.Ciz(g, alan, _kaydirma);
        if (_tasar) _cizim.CubukCiz(g, new Rectangle(x1 - CubukW, alan.Top + P(4), CubukW, alan.Height - P(4)), _kaydirma, _icerikH, false);
        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare ----------------

    protected override bool SurukleBaslar(Point p) => !_kapatKutusu.Contains(p);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_tasar) return;
        int sinir = BossListesiCizimi.KaydirmaSiniri(_icerikH, ListeAlani(ClientSize).Height);
        int yeni = Math.Clamp(_kaydirma - e.Delta * _cizim.TekerAdimi / 120, 0, sinir);
        if (yeni == _kaydirma) return;
        _kaydirma = yeni;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool ust = _kapatKutusu.Contains(e.Location);
        if (ust) Cursor = Cursors.Hand;
        if (ust != _kapatUstunde)
        {
            _kapatUstunde = ust;
            Invalidate(_kapatKutusu);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_kapatUstunde) return;
        _kapatUstunde = false;
        Invalidate(_kapatKutusu);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _kapatKutusu.Contains(e.Location)) KapatIstendi?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yaziBaslik?.Dispose(); _yaziKucuk?.Dispose(); _cizim.Dispose();
            _kalemKenar.Dispose(); _kalemAyirac.Dispose(); _kalemKapat.Dispose(); _kalemKapatUst.Dispose();
        }
        base.Dispose(disposing);
    }
}
