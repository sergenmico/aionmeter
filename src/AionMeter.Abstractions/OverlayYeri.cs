using System.Drawing;

namespace AionMeter;

/// <summary>
/// Overlay'in ekrandaki yeri (overlay kullanır, denetim aracı dener). <see cref="Referans"/>: kullanıcının pencereyi bıraktığı dikdörtgen
/// (fiziksel px). Pencere içeriğe göre büyüyüp küçülürken yeri her seferinde buna göre kurulur: içeri itilen pencere küçülünce
/// bırakıldığı yere döner, pencerenin o anki yeri hiçbir zaman yeni referans olmaz. <see cref="SagaSabit"/> bırakılırken bir kez
/// seçilir (ekranın sağ yarısındaysa sağ kenar sabit, pencere sola büyür) ve büyürken ortası ekran ortasını geçse de değişmez.
/// Ayar dosyasına referans ve sabit kenar yazılır; açılışta aynen geri gelir (açılış-kapanış konumu kaydırmaz, DPI'dan bağımsız).
/// </summary>
public readonly record struct OverlayYeri(Rectangle Referans, bool SagaSabit)
{
    /// <summary>Kullanıcı pencereyi bıraktı (taşıma ya da kenardan genişletme bitti): yeni referans ve sabit kenar.</summary>
    public static OverlayYeri Birak(Rectangle pencere, Rectangle alan) =>
        new(pencere, pencere.Left + pencere.Width / 2 > alan.Left + alan.Width / 2);

    /// <summary>
    /// <paramref name="boyut"/> boyutundaki pencerenin yeri: sabit kenar referanstaki yerinde, üst kenar referansın üstünde. Büyürken
    /// çalışma alanının içinde bırakılmış bir kenar dışarı taşmaz (pencere içeri itilir); kullanıcının kendisi kısmen dışarı koyduğu kenara
    /// dokunulmaz. Küçülünce itme kalkar, pencere referansa döner.
    /// </summary>
    public Rectangle Yerlestir(Size boyut, Rectangle alan)
    {
        var r = Referans;
        int x = SagaSabit ? r.Right - boyut.Width : r.Left, y = r.Top;
        if (x + boyut.Width > alan.Right && r.Right <= alan.Right) x = alan.Right - boyut.Width;
        if (x < alan.Left && r.Left >= alan.Left) x = alan.Left;
        if (y + boyut.Height > alan.Bottom && r.Bottom <= alan.Bottom) y = Math.Max(alan.Top, alan.Bottom - boyut.Height);
        return new Rectangle(x, y, boyut.Width, boyut.Height);
    }

    /// <summary>
    /// Ayar dosyasındaki değerlerden yer: <paramref name="sag"/> ve <paramref name="sagaSabit"/> varsa (yeni dosya) referans aynen.
    /// Eski dosya (yalnız X/Y): eski davranış, referans (x, y, <paramref name="eskiGenislik"/>, yükseklik) ve sabit kenar onun ortasına göre;
    /// ilk kayıtla yeni anahtarlar yazılır. Konum hiç yoksa null.
    /// </summary>
    public static OverlayYeri? Oku(int x, int y, int sag, int yukseklik, bool? sagaSabit, int eskiGenislik, Rectangle alan)
    {
        if (x == int.MinValue || y == int.MinValue) return null;
        int h = Math.Max(1, yukseklik);
        if (sag != int.MinValue && sag > x && sagaSabit is bool sabit)
            return new OverlayYeri(Rectangle.FromLTRB(x, y, sag, y + h), sabit);
        return Birak(new Rectangle(x, y, Math.Max(1, eskiGenislik), h), alan);
    }
}
