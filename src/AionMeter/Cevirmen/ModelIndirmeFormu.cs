namespace AionMeter.Cevirmen;

/// <summary>
/// Çeviri modeli indirme penceresi; güncelleyici penceresiyle aynı koyu, kodla çizilen tarz.
/// Soru: "Çeviri modeli gerekli" + açıklama, birincil "İndir" (Enter) ve ikincil "Vazgeç" (Esc).
/// İndir'e basınca aynı pencere ilerlemeye geçer: çubuk + "%45 · 36 / 80 MB" + "Alta al" + "İptal". Hata: kısa mesaj + "Kapat".
/// İndirme sürerken pencere en üstte durmaz ve görev çubuğuna alınabilir (ilerleme görev çubuğu başlığında görünür).
/// SADECE açık kullanıcı eylemiyle açılır (Ctrl+Alt+T ilk kullanım / tepsi "Çeviri modelini indir");
/// oyun sırasında Alt ile açıklama çevirisi bu pencereyi asla açmaz. Odak alabilir (bir iletişim kutusu), geçiş efekti yok.
/// </summary>
internal sealed class ModelIndirmeFormu : Form
{
    private static readonly Color RenkMavi = Color.FromArgb(86, 156, 240);
    private static readonly Color RenkIkincil = Color.FromArgb(52, 57, 70);

    private const TextFormatFlags Tek = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags Paragraf = TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
                                             TextFormatFlags.NoPrefix;

    private enum Asama { Soru, Indiriliyor, Hata }

    private readonly ICeviriMotoru _motor;
    private readonly Button _birincil, _ikincil, _altaAl;
    private readonly CancellationTokenSource _iptal = new();
    private readonly SolidBrush _fircaCubukArka = new(Color.FromArgb(40, 44, 54));
    private readonly SolidBrush _fircaCubuk = new(RenkMavi);
    private readonly Pen _kalemKenar = new(Cizim.Cizgi);
    private Font? _yazi, _yaziKalin, _yaziKucuk;
    private float _olcek = 1f;

    private Asama _asama = Asama.Soru;
    private string _baslik = "Çeviri modeli gerekli", _govde = "", _ayrinti = "";
    private float _oran;
    private bool _indiriliyor;
    private bool _altaAlindi; // indirme bittiğinde pencere küçüktü ya da önde değildi (kullanıcı başka işle meşgul)

    private ModelIndirmeFormu(ICeviriMotoru motor)
    {
        _motor = motor;
        // 533.745.173 bayt ≈ 509 MiB → "~510 MB" (100 MB üstü en yakın 10'a yuvarlanır)
        long mb = Math.Max(1, (motor.ModelBoyutu + 512 * 1024) / (1024 * 1024));
        if (mb > 100) mb = (mb + 5) / 10 * 10;
        _govde = "Çevirmen çevrimdışı çalışır. Model bir kez indirilir (~" + mb.ToString("N0", Bicim.Tr) +
                 " MB) ve bu bilgisayarda kalır.";

        Text = "AionMeter Çevirmen";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Cizim.Arka;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        _birincil = Dugme("İndir", RenkMavi, Color.White);
        _ikincil = Dugme("Vazgeç", RenkIkincil, Cizim.Yazi);
        _birincil.Click += async (_, _) =>
        {
            if (_asama == Asama.Hata) DialogResult = DialogResult.Cancel; // "Kapat"
            else await IndirAsync();
        };
        _ikincil.Click += (_, _) => Vazgec();
        _altaAl = Dugme("Alta al", RenkIkincil, Cizim.Yazi);
        _altaAl.Visible = false;
        _altaAl.Click += (_, _) => AltaAl();
        Controls.Add(_birincil);
        Controls.Add(_ikincil);
        Controls.Add(_altaAl);
        AcceptButton = _birincil; // Enter
        CancelButton = _ikincil;  // Esc
    }

    private static Button Dugme(string metin, Color arka, Color yazi)
    {
        var b = new Button
        {
            Text = metin, FlatStyle = FlatStyle.Flat, BackColor = arka, ForeColor = yazi, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    /// <summary>
    /// true = model hazır (indirildi). altaAlindi = kullanıcı indirme sırasında pencereyi görev çubuğuna aldı;
    /// o zaman odak geri verilmez (kullanıcı başka bir pencerede) ve çağıran işe kendiliğinden devam etmemeli.
    /// </summary>
    public static bool Sor(ICeviriMotoru motor, IntPtr oncekiPencere, out bool altaAlindi)
    {
        altaAlindi = false;
        try
        {
            using var f = new ModelIndirmeFormu(motor);
            bool sonuc = f.ShowDialog() == DialogResult.OK;
            altaAlindi = f._altaAlindi;
            if (!altaAlindi) OdakYardimcisi.GeriVer(oncekiPencere);
            return sonuc;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Model indirme penceresi", ex);
            return false;
        }
    }

    /// <summary>Görev çubuğundan küçültülüp geri açılabilsin (çerçevesiz pencerede bu stiller kendiliğinden yok).</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_MINIMIZEBOX = 0x00020000, WS_SYSMENU = 0x00080000;
            var cp = base.CreateParams;
            cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU;
            return cp;
        }
    }

    private void AltaAl()
    {
        // Sahipli pencere küçülünce görev çubuğu yerine masaüstünün köşesine küçük bir başlık olur: sahip bağını kaldır.
        Native.SahibiKaldir(Handle);
        WindowState = FormWindowState.Minimized;
    }

    private int P(float v) => (int)MathF.Round(v * _olcek);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _olcek = Math.Max(1f, DeviceDpi / 96f);
        _yazi = new Font("Segoe UI", 12f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _yaziKalin = new Font("Segoe UI", 13f * _olcek, FontStyle.Bold, GraphicsUnit.Pixel);
        _yaziKucuk = new Font("Segoe UI", 11f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
        _birincil.Font = _yaziKalin;
        _ikincil.Font = _yazi;
        _altaAl.Font = _yazi;
        try
        {
            int tercih = Native.DWMWCP_ROUNDSMALL; // Windows 11: küçük yuvarlak köşe; Windows 10: kare
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref tercih, sizeof(int));
        }
        catch { /* Windows 10 */ }
        Yerlesim();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        OdakYardimcisi.OneGetir(this); // kısayol oyundan gelir: Enter/Esc çalışsın diye öne al
        _birincil.Focus();
    }

    /// <summary>Boyut sabit (440 × 160); ilk açılışta ekranın ortasında. Düğmeler aşamaya göre.</summary>
    private void Yerlesim()
    {
        int w = P(440), h = P(160), pad = P(16);
        if (!_yerlesti)
        {
            _yerlesti = true;
            var alan = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = new Rectangle(alan.Left + (alan.Width - w) / 2, alan.Top + (alan.Height - h) / 2, w, h);
        }
        int dy = h - P(48);
        var sag = new Rectangle(w - pad - P(110), dy, P(110), P(32));
        var sol = new Rectangle(sag.X - P(10) - P(110), dy, P(110), P(32));
        switch (_asama)
        {
            case Asama.Soru:
                _birincil.Text = "İndir";
                _ikincil.Text = "Vazgeç";
                _birincil.Bounds = sol;
                _ikincil.Bounds = sag;
                _birincil.Visible = _ikincil.Visible = true;
                _altaAl.Visible = false;
                AcceptButton = _birincil;
                CancelButton = _ikincil;
                break;
            case Asama.Indiriliyor:
                _ikincil.Text = "İptal";
                _ikincil.Bounds = sag;
                _altaAl.Bounds = sol;
                _birincil.Visible = false;
                _ikincil.Visible = _altaAl.Visible = true;
                AcceptButton = null;
                CancelButton = _ikincil;
                break;
            case Asama.Hata:
                _birincil.Text = "Kapat";
                _birincil.Bounds = sag;
                _birincil.Visible = true;
                _ikincil.Visible = _altaAl.Visible = false;
                AcceptButton = _birincil;
                CancelButton = _birincil;
                break;
        }
        Invalidate();
    }

    private bool _yerlesti;

    private void AsamaDegistir(Asama asama)
    {
        _asama = asama;
        Yerlesim();
        if (asama != Asama.Indiriliyor) _birincil.Focus();
        else _ikincil.Focus();
    }

    private async Task IndirAsync()
    {
        if (_indiriliyor) return;
        _indiriliyor = true;
        _baslik = "Çeviri modeli indiriliyor";
        _oran = 0f;
        _ayrinti = "";
        TopMost = false; // uzun sürebilir: diğer pencerelerin önünü kapatmasın
        AsamaDegistir(Asama.Indiriliyor);
        var ilerleme = new Progress<(long Inen, long Toplam)>(t =>
        {
            const double Mb = 1024.0 * 1024.0;
            if (t.Toplam <= 0) return;
            _oran = (float)Math.Clamp((double)t.Inen / t.Toplam, 0, 1);
            _ayrinti = "%" + ((int)(_oran * 100)).ToString(Bicim.Tr) + " · " + ((long)(t.Inen / Mb)).ToString("N0", Bicim.Tr) +
                       " / " + ((long)Math.Round(t.Toplam / Mb)).ToString("N0", Bicim.Tr) + " MB";
            string baslik = "%" + ((int)(_oran * 100)).ToString(Bicim.Tr) + " · Çeviri modeli indiriliyor";
            if (Text != baslik) Text = baslik; // görev çubuğunda görünür
            Invalidate();
        });
        try
        {
            await _motor.ModeliIndirAsync(ilerleme, _iptal.Token);
            _indiriliyor = false;
            // Karar bitiş anında: pencere küçükse ya da kullanıcı başka pencereye (oyuna) geçtiyse "başka işle meşgul".
            _altaAlindi = WindowState == FormWindowState.Minimized || Native.GetForegroundWindow() != Handle;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) when (_iptal.IsCancellationRequested)
        {
            _indiriliyor = false;
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Çeviri modeli indirilemedi", ex);
            _indiriliyor = false;
            _baslik = "Model indirilemedi";
            _govde = "İnternet bağlantısını kontrol edip daha sonra tekrar dene.";
            _ayrinti = ex.Message;
            Text = "AionMeter Çevirmen";
            AsamaDegistir(Asama.Hata);
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; // hata görünsün
        }
    }

    private void Vazgec()
    {
        if (_indiriliyor) _iptal.Cancel(); // indirme görevi bitince pencere kapanır
        else DialogResult = DialogResult.Cancel;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_indiriliyor)
        {
            // İndirme sürerken kapanmasın: önce iptal, görev bitince kapanır (yarım dosya bırakmasın).
            e.Cancel = true;
            _iptal.Cancel();
            return;
        }
        base.OnFormClosing(e);
    }

    // ---------------- Çizim ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Ciz(e.Graphics); }
        catch (Exception ex) { HataLog.Yaz("Model penceresi çizimi", ex); }
    }

    private void Ciz(Graphics g)
    {
        if (_yazi is null || _yaziKalin is null || _yaziKucuk is null) return;
        int w = ClientSize.Width, h = ClientSize.Height, pad = P(16), gen = w - 2 * pad;
        TextRenderer.DrawText(g, _baslik, _yaziKalin, new Rectangle(pad, P(14), gen, P(22)), Cizim.Yazi, Tek);

        if (_asama == Asama.Indiriliyor)
        {
            var cubuk = new Rectangle(pad, P(56), gen, P(8));
            g.FillRectangle(_fircaCubukArka, cubuk);
            g.FillRectangle(_fircaCubuk, cubuk.X, cubuk.Y, (int)(cubuk.Width * _oran), cubuk.Height);
            TextRenderer.DrawText(g, _ayrinti, _yaziKucuk, new Rectangle(pad, P(72), gen, P(18)), Cizim.Soluk, Tek);
        }
        else
        {
            TextRenderer.DrawText(g, _govde, _yazi, new Rectangle(pad, P(44), gen, P(40)), Cizim.Soluk, Paragraf);
            if (_asama == Asama.Hata && _ayrinti.Length > 0)
                TextRenderer.DrawText(g, _ayrinti, _yaziKucuk, new Rectangle(pad, P(84), gen, P(18)), Cizim.CokSoluk, Tek);
        }

        g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); // önce düğmeler (yazı tiplerini kullanıyorlar)
        if (disposing)
        {
            _iptal.Dispose();
            _fircaCubukArka.Dispose(); _fircaCubuk.Dispose(); _kalemKenar.Dispose();
            _yazi?.Dispose(); _yaziKalin?.Dispose(); _yaziKucuk?.Dispose();
        }
    }
}

internal static class OdakYardimcisi
{
    /// <summary>
    /// Açık kullanıcı eylemiyle (kısayol) açılan pencerelerimizi öne getirir. Windows ön plan kilidi yüzünden
    /// SetForegroundWindow tek başına çoğu zaman reddedilir; ön plandaki iş parçacığına kısa süre bağlanılır.
    /// Sadece Ctrl+Alt+T seçim ekranı ve model indirme penceresi için kullanılır (overlay ve paneller odak almaz).
    /// </summary>
    public static void OneGetir(Form f)
    {
        try
        {
            IntPtr on = Native.GetForegroundWindow();
            if (on == f.Handle) return;
            uint onIs = Native.GetWindowThreadProcessId(on, out _);
            uint benim = Native.GetCurrentThreadId();
            bool bagli = onIs != 0 && onIs != benim && Native.AttachThreadInput(benim, onIs, true);
            try
            {
                Native.BringWindowToTop(f.Handle);
                Native.SetForegroundWindow(f.Handle);
                f.Activate();
            }
            finally
            {
                if (bagli) Native.AttachThreadInput(benim, onIs, false);
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Pencere öne getirilemedi", ex);
        }
    }

    /// <summary>Kısa süre odak alan pencerelerimiz kapandıktan sonra odağı önceki pencereye (oyuna) geri verir.</summary>
    public static void GeriVer(IntPtr onceki)
    {
        try
        {
            if (onceki != IntPtr.Zero && Native.IsWindow(onceki)) Native.SetForegroundWindow(onceki);
        }
        catch { /* önemsiz */ }
    }
}
