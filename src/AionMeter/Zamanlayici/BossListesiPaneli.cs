using System.Drawing.Drawing2D;

namespace AionMeter.Zamanlayici;

/// <summary>Boss listesi satırı: ad ve bir sonraki doğuşa kalan süre ("1 sa 41 dk", "41:20", "şimdi").</summary>
internal readonly record struct BossSatiri(string Ad, string Sure, Color SureRengi, bool Rakam);

/// <summary>
/// İstendiğinde açılan boss listesi: takvim bossları (ve ileride saha bossları), en yakın doğuş üstte. Kullanıcı kendisi
/// açtığı için kilitliyken de tıklanır (odak yine alınmaz): sağ üstteki × kapatır, başka yerden tutup taşınır.
/// </summary>
internal sealed class BossListesiPaneli : KatmanliPanel
{
    private readonly List<BossSatiri> _satirlar = new();
    private string _altBaslik = "";
    private Font? _yazi, _yaziBaslik, _yaziKucuk, _yaziSimdi;
    private RakamYazisi? _rakam;
    private readonly SolidBrush _fircaBoss = new(Palet.Kirmizi);
    private readonly Pen _kalemKenar = new(Palet.Cizgi);
    private readonly Pen _kalemAyirac = new(Palet.Cizgi);
    private readonly Pen _kalemKapat = new(Palet.Soluk, 1.5f);
    private readonly Pen _kalemKapatUst = new(Palet.Yazi, 1.5f);
    private Rectangle _kapatKutusu;
    private bool _kapatUstunde;
    private int _sureW;

    /// <summary>× tıklandı.</summary>
    public event Action? KapatIstendi;

    public BossListesiPaneli(float olcek) : base("AionMeter Boss Listesi", olcek)
    {
        Suruklenebilir = true;
        YaziTipleriniKur();
    }

    protected override void YaziTipleriniKur()
    {
        _yazi?.Dispose(); _yaziBaslik?.Dispose(); _yaziKucuk?.Dispose(); _yaziSimdi?.Dispose(); _rakam?.Dispose();
        _yazi = Palet.YaziTipi(12.5f * Olcek);
        _yaziBaslik = Palet.YaziTipi(12.5f * Olcek, yariKalin: true);
        _yaziKucuk = Palet.YaziTipi(11f * Olcek);
        _yaziSimdi = Palet.YaziTipi(12.5f * Olcek, yariKalin: true);
        _rakam = new RakamYazisi(Palet.RakamTipi(13f * Olcek, yariKalin: true));
        _kalemKapat.Width = _kalemKapatUst.Width = Math.Max(1f, 1.5f * Olcek);
    }

    private int Pad => P(10);
    private int BaslikH => P(28);
    private int SatirH => Math.Max(P(22), _rakam!.Yukseklik + P(4));

    /// <summary>Satırları alır; içerik aynıysa false (çizim yok).</summary>
    public bool Guncelle(IReadOnlyList<BossSatiri> satirlar, string altBaslik)
    {
        if (altBaslik == _altBaslik && satirlar.Count == _satirlar.Count)
        {
            bool ayni = true;
            for (int i = 0; i < satirlar.Count && ayni; i++) ayni = satirlar[i] == _satirlar[i];
            if (ayni) return false;
        }
        _altBaslik = altBaslik;
        _satirlar.Clear();
        _satirlar.AddRange(satirlar);
        Invalidate();
        return true;
    }

    /// <summary>İçeriğe göre boyut; süre sütunu en az "23 sa 59 dk" genişliğinde (süre kısalınca genişlik zıplamaz).</summary>
    public Size Boyut()
    {
        int adW = Olc("Bosslar", _yaziBaslik!) + P(10) + Olc(_altBaslik, _yaziKucuk!) + P(24);
        int sureW = _rakam!.Genislik("23 sa 59 dk");
        foreach (var s in _satirlar)
        {
            adW = Math.Max(adW, Olc(s.Ad, _yazi!));
            sureW = Math.Max(sureW, s.Rakam ? _rakam.Genislik(s.Sure) : Olc(s.Sure, _yaziSimdi!));
        }
        _sureW = sureW;
        int w = Pad + P(13) + Math.Min(adW, P(260)) + P(20) + sureW + Pad;
        int h = Pad / 2 + BaslikH + P(4) + Math.Max(1, _satirlar.Count) * SatirH + Pad;
        return new Size(w, h);
    }

    public override void Ciz(Graphics g, Size boyut)
    {
        int w = boyut.Width, h = boyut.Height;
        g.Clear(Palet.Arka);
        int x0 = Pad, x1 = w - Pad, y = Pad / 2;

        // Başlık: "Bosslar · bölge" ve ×
        int k = P(16);
        _kapatKutusu = new Rectangle(x1 - k, y + (BaslikH - k) / 2, k, k);
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
        y += BaslikH;
        g.DrawLine(_kalemAyirac, x0, y, x1, y);
        y += P(4);

        int cap = Math.Max(4, P(6)), satirH = SatirH;
        if (_satirlar.Count == 0)
            TextRenderer.DrawText(g, "Takvimde boss yok", _yazi!, new Rectangle(x0, y, x1 - x0, satirH), Palet.Soluk, Sol);
        foreach (var s in _satirlar)
        {
            Nokta(g, _fircaBoss, x0, y + satirH / 2, cap);
            var sureKutu = new Rectangle(x1 - _sureW, y, _sureW, satirH);
            if (s.Rakam) _rakam!.Ciz(g, s.Sure, sureKutu, s.SureRengi, sag: true);
            else TextRenderer.DrawText(g, s.Sure, _yaziSimdi!, sureKutu, s.SureRengi, Sag);
            int adX = x0 + cap + P(7);
            TextRenderer.DrawText(g, s.Ad, _yazi!, new Rectangle(adX, y, Math.Max(0, sureKutu.Left - P(16) - adX), satirH), Palet.Yazi, Sol);
            y += satirH;
        }
        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    // ---------------- Fare ----------------

    protected override bool SurukleBaslar(Point p) => !_kapatKutusu.Contains(p);

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
            _yazi?.Dispose(); _yaziBaslik?.Dispose(); _yaziKucuk?.Dispose(); _yaziSimdi?.Dispose(); _rakam?.Dispose();
            _fircaBoss.Dispose(); _kalemKenar.Dispose(); _kalemAyirac.Dispose(); _kalemKapat.Dispose(); _kalemKapatUst.Dispose();
        }
        base.Dispose(disposing);
    }
}
