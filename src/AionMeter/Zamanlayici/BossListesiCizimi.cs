using System.Drawing.Drawing2D;
using AionMeter.Cevirmen;

namespace AionMeter.Zamanlayici;

/// <summary>
/// Gruplu boss listesinin çizimi: ekranda yüzen boss listesi de Ayarlar'daki Bosslar sekmesi de bunu kullanır (aynı görünüm).
/// Satırlar <see cref="ZamanlayiciDenetleyici.BossSatirlariniDoldur"/>'dan gelir; burada yalnız gruplanır: önce "Abyss" (takvimli
/// bosslar), sonra haritalar (<see cref="ZamanlayiciDenetleyici.GrupSirasi"/>). Grup içinde gelen sıra korunur: en yakın doğuş üstte,
/// canlılar (yeşil nokta) ve belirsizler (soluk) sonda. Liste verilen alana kırpılır, <c>kaydirma</c> kadar yukarı kayar.
/// </summary>
internal sealed class BossListesiCizimi : IDisposable
{
    // PreserveGraphicsClipping: TextRenderer kırpmayı ancak bu bayrakla uygular (kaydırılan liste alanın dışına taşmasın).
    private const TextFormatFlags Temel = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
    private const TextFormatFlags Sol = TextFormatFlags.Left | Temel | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Sag = TextFormatFlags.Right | Temel;
    private const TextFormatFlags IpucuBicimi = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                                                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;

    private readonly record struct Grup(string Ad, int Ilk, int Sayi, int Canli);

    private List<BossSatiri> _satirlar = new(), _yeniSatirlar = new();
    private List<Grup> _gruplar = new(), _yeniGruplar = new();
    private bool _sahaVar = true;
    private int _ipucuW = -1, _ipucuH;

    private float _olcek;
    private Font _ad = null!, _grup = null!, _kucuk = null!, _sure = null!;
    private int _sureW;
    private readonly SolidBrush _fircaBekliyor = new(Palet.Kirmizi);
    private readonly SolidBrush _fircaCanli = new(Palet.Yesil);
    private readonly SolidBrush _fircaSoluk = new(Palet.CokSoluk);
    private readonly SolidBrush _fircaIz = new(Palet.Panel);
    private readonly SolidBrush _fircaTopuz = new(Palet.AyiriciAcik);
    private readonly SolidBrush _fircaTopuzUst = new(Palet.Soluk);
    private readonly Pen _kalemAyirac = new(Palet.Cizgi);

    public BossListesiCizimi(float olcek) => OlcekAyarla(olcek);

    private int P(float v) => (int)MathF.Round(v * _olcek);

    public int GrupH => P(30);
    public int SatirH => P(24);

    /// <summary>Fare tekerleğinin bir çentiği: üç satır.</summary>
    public int TekerAdimi => 3 * SatirH;

    /// <summary>Yazı tiplerini yeni ölçekle kurar.</summary>
    public void OlcekAyarla(float olcek)
    {
        if (olcek == _olcek) return;
        _olcek = olcek;
        _ad?.Dispose(); _grup?.Dispose(); _kucuk?.Dispose(); _sure?.Dispose();
        _ad = Palet.YaziTipi(12.5f * olcek);
        _grup = Palet.YaziTipi(11f * olcek, yariKalin: true);
        _kucuk = Palet.YaziTipi(11f * olcek);
        _sure = Palet.RakamTipi(13f * olcek, yariKalin: true); // süreler ve "canlı" / "şimdi" aynı yazıyla: sütun tek parça okunur
        _sureW = Olc("23 sa 59 dk", _sure) + P(2);             // süre kısalınca sütun zıplamasın
        _ipucuW = -1;
    }

    private static int Olc(string metin, Font font) =>
        metin.Length == 0 ? 0 : TextRenderer.MeasureText(metin, font, Size.Empty, Temel).Width;

    /// <summary>Satırları gruplar; görünen içerik değiştiyse true (değişmediyse çizime gerek yok).</summary>
    public bool Guncelle(IReadOnlyList<BossSatiri> satirlar, bool sahaVar)
    {
        _yeniSatirlar.Clear();
        _yeniGruplar.Clear();
        var sira = ZamanlayiciDenetleyici.GrupSirasi;
        foreach (string ad in sira) GrupEkle(satirlar, ad);
        // Bilinen sırada olmayan bir harita gelirse (yeni bölge), ilk görüldüğü sırayla en sona
        for (int i = 0; i < satirlar.Count; i++)
        {
            string ad = satirlar[i].Grup;
            if (Array.IndexOf(sira, ad) >= 0) continue;
            bool goruldu = false;
            for (int j = 0; j < i && !goruldu; j++) goruldu = satirlar[j].Grup == ad;
            if (!goruldu) GrupEkle(satirlar, ad);
        }

        bool ayni = sahaVar == _sahaVar && _yeniSatirlar.Count == _satirlar.Count && _yeniGruplar.Count == _gruplar.Count;
        for (int i = 0; ayni && i < _satirlar.Count; i++) ayni = _yeniSatirlar[i] == _satirlar[i];
        for (int i = 0; ayni && i < _gruplar.Count; i++) ayni = _yeniGruplar[i] == _gruplar[i];
        if (ayni) return false;
        (_satirlar, _yeniSatirlar) = (_yeniSatirlar, _satirlar);
        (_gruplar, _yeniGruplar) = (_yeniGruplar, _gruplar);
        _sahaVar = sahaVar;
        return true;
    }

    private void GrupEkle(IReadOnlyList<BossSatiri> satirlar, string ad)
    {
        int ilk = _yeniSatirlar.Count, canli = 0;
        foreach (var s in satirlar)
        {
            if (s.Grup != ad) continue;
            _yeniSatirlar.Add(s);
            if (s.Canli) canli++;
        }
        if (_yeniSatirlar.Count > ilk) _yeniGruplar.Add(new Grup(ad, ilk, _yeniSatirlar.Count - ilk, canli));
    }

    /// <summary>İpucunun (saha bilgisi yokken) bu genişlikteki yüksekliği; ölçüm genişlik değişince yenilenir.</summary>
    private int IpucuH(int genislik)
    {
        if (_ipucuW != genislik)
        {
            _ipucuW = genislik;
            _ipucuH = TextRenderer.MeasureText(ZamanlayiciDenetleyici.SahaIpucu, _kucuk, new Size(genislik, int.MaxValue), IpucuBicimi).Height;
        }
        return _ipucuH;
    }

    /// <summary>Tüm listenin yüksekliği (kaydırmasız).</summary>
    public int IcerikH(int genislik)
    {
        int h = _gruplar.Count * GrupH + _satirlar.Count * SatirH;
        if (_satirlar.Count == 0) h += SatirH;
        if (!_sahaVar) h += P(8) + IpucuH(genislik);
        return h;
    }

    /// <summary>Hiçbir ad kesilmeden gereken genişlik (en çok <paramref name="enFazlaAd"/> px ad).</summary>
    public int IstenenGenislik(int enFazlaAd)
    {
        int adW = 0, grupW = 0;
        foreach (var s in _satirlar) adW = Math.Max(adW, Olc(s.Ad, _ad));
        foreach (var g in _gruplar) grupW = Math.Max(grupW, Olc(g.Ad, _grup) + P(72)); // grup adı + sağda "12 canlı"
        return Math.Max(P(16) + Math.Min(adW, enFazlaAd) + P(14) + _sureW, grupW);
    }

    /// <summary>Listeyi <paramref name="alan"/> içine, <paramref name="kaydirma"/> px yukarı kaymış çizer (alan dışı kırpılır).</summary>
    public void Ciz(Graphics g, Rectangle alan, int kaydirma)
    {
        var durum = g.Save();
        g.SetClip(alan, CombineMode.Intersect);
        int x0 = alan.Left, x1 = alan.Right, y = alan.Top - kaydirma;
        int satirH = SatirH, grupH = GrupH, cap = Math.Max(4, P(6));
        var sureKutuX = x1 - _sureW;
        int adX = x0 + P(16), adW = Math.Max(0, sureKutuX - P(14) - adX);

        if (_satirlar.Count == 0)
        {
            TextRenderer.DrawText(g, "Takvimde boss yok", _ad, new Rectangle(x0, y, x1 - x0, satirH), Palet.Soluk, Sol);
            y += satirH;
        }
        for (int gi = 0; gi < _gruplar.Count; gi++)
        {
            var grup = _gruplar[gi];
            int altSinir = y + grupH + grup.Sayi * satirH;
            if (altSinir < alan.Top || y > alan.Bottom) // görünmeyen grup: atla
            {
                y = altSinir;
                continue;
            }
            // Grup başlığı: üstte ince ayırıcı (ilki hariç), solda harita adı, sağda kaç bossun canlı olduğu
            if (gi > 0) g.DrawLine(_kalemAyirac, x0, y + P(4), x1, y + P(4));
            var baslik = new Rectangle(x0, y + P(8), x1 - x0, grupH - P(8));
            TextRenderer.DrawText(g, grup.Ad, _grup, baslik, Palet.Soluk, Sol);
            if (grup.Canli > 0)
                TextRenderer.DrawText(g, grup.Canli.ToString(Bicim.Tr) + " canlı", _kucuk, baslik, Palet.Yesil, Sag);
            y += grupH;

            for (int i = grup.Ilk; i < grup.Ilk + grup.Sayi; i++, y += satirH)
            {
                if (y + satirH < alan.Top || y > alan.Bottom) continue;
                var s = _satirlar[i];
                var firca = s.Soluk ? _fircaSoluk : s.Canli ? _fircaCanli : _fircaBekliyor;
                var eski = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(firca, x0 + P(2), y + satirH / 2f - cap / 2f, cap, cap);
                g.SmoothingMode = eski;
                TextRenderer.DrawText(g, s.Ad, _ad, new Rectangle(adX, y, adW, satirH), s.Soluk ? Palet.Soluk : Palet.Yazi, Sol);
                TextRenderer.DrawText(g, s.Sure, _sure, new Rectangle(sureKutuX, y, _sureW, satirH), s.SureRengi, Sag);
            }
        }
        if (!_sahaVar)
            TextRenderer.DrawText(g, ZamanlayiciDenetleyici.SahaIpucu, _kucuk,
                new Rectangle(x0, y + P(8), x1 - x0, IpucuH(x1 - x0)), Palet.CokSoluk, IpucuBicimi);
        g.Restore(durum);
    }

    /// <summary>Kaydırma sınırı: içerik alana sığıyorsa 0.</summary>
    public static int KaydirmaSiniri(int icerikH, int alanH) => Math.Max(0, icerikH - alanH);

    /// <summary>İnce kaydırma çubuğunun tutamacı (<paramref name="iz"/>: çubuğun tüm yolu, yüksekliği görünen alanınki).</summary>
    public Rectangle Topuz(Rectangle iz, int kaydirma, int icerikH)
    {
        int sinir = KaydirmaSiniri(icerikH, iz.Height);
        if (sinir == 0) return Rectangle.Empty;
        int h = Math.Max(P(28), (int)((long)iz.Height * iz.Height / icerikH));
        int y = iz.Top + (int)((long)(iz.Height - h) * Math.Clamp(kaydirma, 0, sinir) / sinir);
        return new Rectangle(iz.Left, y, iz.Width, h);
    }

    /// <summary>Çubuğu çizer (içerik sığıyorsa hiçbir şey çizmez).</summary>
    public void CubukCiz(Graphics g, Rectangle iz, int kaydirma, int icerikH, bool vurgulu)
    {
        var t = Topuz(iz, kaydirma, icerikH);
        if (t.IsEmpty) return;
        float r = iz.Width / 2f;
        Cizim.YuvarlakDoldur(g, _fircaIz, iz, r);
        Cizim.YuvarlakDoldur(g, vurgulu ? _fircaTopuzUst : _fircaTopuz, t, r);
    }

    public void Dispose()
    {
        _ad?.Dispose(); _grup?.Dispose(); _kucuk?.Dispose(); _sure?.Dispose();
        _fircaBekliyor.Dispose(); _fircaCanli.Dispose(); _fircaSoluk.Dispose();
        _fircaIz.Dispose(); _fircaTopuz.Dispose(); _fircaTopuzUst.Dispose(); _kalemAyirac.Dispose();
    }
}
