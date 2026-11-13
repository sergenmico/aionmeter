using AionMeter.Core;

namespace AionMeter.Zamanlayici;

/// <summary>
/// Geri sayım satırı. <see cref="Etiket"/> doluysa ("başladı") süre girişin kapanmasına kalan zamandır.
/// <see cref="Rakam"/>: süre Bahnschrift rakamlarla mı (yoksa "şimdi" gibi düz yazı mı).
/// </summary>
internal readonly record struct GeriSayimSatiri(string Ad, TakvimTuru Tur, string Sure, string? Etiket, Color SureRengi, bool Rakam, bool Soluk);

/// <summary>
/// Başlamasına 10 dk kalan etkinlik/bosslar ve girişi hâlâ açık etkinlikler: solda ad, sağda süre. Kendisi karar vermez;
/// <see cref="ZamanlayiciDenetleyici"/> satırları verir, panel yalnız içerik değişince (saniyede bir) çizer.
/// </summary>
internal sealed class GeriSayimPaneli : KatmanliPanel
{
    private readonly List<GeriSayimSatiri> _satirlar = new();
    private bool _kilitsiz;
    private Font? _yazi, _yaziEtiket, _yaziSimdi;
    private RakamYazisi? _rakam;
    private readonly SolidBrush _fircaEtkinlik = new(Palet.Vurgu);
    private readonly SolidBrush _fircaBoss = new(Palet.Kirmizi);
    private readonly SolidBrush _fircaSoluk = new(Palet.CokSoluk);
    private readonly Pen _kalemKenar = new(Palet.Cizgi);
    private readonly Pen _kalemKilitsiz = new(Palet.Vurgu);

    // Yerleşim (Guncelle'de hesaplanır)
    private int _sureW, _etiketW;

    public GeriSayimPaneli(float olcek) : base("AionMeter Geri Sayım", olcek)
    {
        YaziTipleriniKur();
    }

    protected override void YaziTipleriniKur()
    {
        _yazi?.Dispose(); _yaziEtiket?.Dispose(); _yaziSimdi?.Dispose(); _rakam?.Dispose();
        _yazi = Palet.YaziTipi(12.5f * Olcek);
        _yaziEtiket = Palet.YaziTipi(11f * Olcek);
        _yaziSimdi = Palet.YaziTipi(12.5f * Olcek, yariKalin: true);
        _rakam = new RakamYazisi(Palet.RakamTipi(14f * Olcek, yariKalin: true));
    }

    private int PadX => P(10);
    private int PadY => P(5);
    private int SatirH => Math.Max(P(22), _rakam!.Yukseklik + P(4));
    private int NoktaCap => Math.Max(4, P(6));

    /// <summary>Satırları alır; içerik aynıysa false döner (çizim yok). Boyutu <see cref="Boyut"/> verir.</summary>
    public bool Guncelle(IReadOnlyList<GeriSayimSatiri> satirlar, bool kilitsiz)
    {
        if (kilitsiz == _kilitsiz && satirlar.Count == _satirlar.Count)
        {
            bool ayni = true;
            for (int i = 0; i < satirlar.Count && ayni; i++) ayni = satirlar[i] == _satirlar[i];
            if (ayni) return false;
        }
        _kilitsiz = kilitsiz;
        _satirlar.Clear();
        _satirlar.AddRange(satirlar);
        Invalidate();
        return true;
    }

    /// <summary>İçeriğe göre boyut: süre sütunu en az "00:00" genişliğinde (saniyeler akarken genişlik zıplamaz).</summary>
    public Size Boyut()
    {
        int adW = 0, sureW = _rakam!.Genislik("00:00"), etiketW = 0;
        foreach (var s in _satirlar)
        {
            adW = Math.Max(adW, Olc(s.Ad, _yazi!));
            sureW = Math.Max(sureW, s.Rakam ? _rakam.Genislik(s.Sure) : Olc(s.Sure, _yaziSimdi!));
            if (s.Etiket is not null) etiketW = Math.Max(etiketW, Olc(s.Etiket, _yaziEtiket!) + P(6));
        }
        _sureW = sureW;
        _etiketW = etiketW;
        int w = PadX + NoktaCap + P(7) + Math.Min(adW, P(260)) + P(20) + etiketW + sureW + PadX;
        int h = 2 * PadY + Math.Max(1, _satirlar.Count) * SatirH;
        return new Size(Math.Max(w, P(180)), h);
    }

    public override void Ciz(Graphics g, Size boyut)
    {
        int w = boyut.Width, h = boyut.Height;
        g.Clear(Palet.Arka);
        int x0 = PadX, x1 = w - PadX, y = PadY, satirH = SatirH, cap = NoktaCap;
        foreach (var s in _satirlar)
        {
            int orta = y + satirH / 2;
            Nokta(g, s.Soluk ? _fircaSoluk : s.Tur == TakvimTuru.Boss ? _fircaBoss : _fircaEtkinlik, x0, orta, cap);

            var sureKutu = new Rectangle(x1 - _sureW, y, _sureW, satirH);
            if (s.Rakam) _rakam!.Ciz(g, s.Sure, sureKutu, s.SureRengi, sag: true);
            else TextRenderer.DrawText(g, s.Sure, _yaziSimdi!, sureKutu, s.SureRengi, Sag);

            int adSag = sureKutu.Left - P(20);
            if (s.Etiket is not null)
            {
                var etiketKutu = new Rectangle(sureKutu.Left - _etiketW, y, _etiketW - P(6), satirH);
                TextRenderer.DrawText(g, s.Etiket, _yaziEtiket!, etiketKutu, Palet.Soluk, Sag);
                adSag = etiketKutu.Left - P(14);
            }
            else if (_etiketW > 0) adSag = sureKutu.Left - _etiketW - P(14);

            int adX = x0 + cap + P(7);
            TextRenderer.DrawText(g, s.Ad, _yazi!, new Rectangle(adX, y, Math.Max(0, adSag - adX), satirH),
                s.Soluk ? Palet.Soluk : Palet.Yazi, Sol);
            y += satirH;
        }
        g.DrawRectangle(_kilitsiz ? _kalemKilitsiz : _kalemKenar, 0, 0, w - 1, h - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _yazi?.Dispose(); _yaziEtiket?.Dispose(); _yaziSimdi?.Dispose(); _rakam?.Dispose();
            _fircaEtkinlik.Dispose(); _fircaBoss.Dispose(); _fircaSoluk.Dispose();
            _kalemKenar.Dispose(); _kalemKilitsiz.Dispose();
        }
        base.Dispose(disposing);
    }
}
