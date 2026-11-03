using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace AionMeter.Cevirmen;

/// <summary>
/// Windows.Graphics.Capture ile ekran (monitör) yakalama. GDI'nin (CopyFromScreen) siyah ya da eski kare gördüğü
/// tam ekran DirectX oyunlarında da çalışır. Oyuna dokunmaz: Windows'un kendi ekran kaydı arayüzü (OBS/Discord'un kullandığı).
///  - Her monitör için bir oturum açılır ve kullanıldıkça açık kalır (son kare hep elde); 5 sn kullanılmazsa kapanır (D3D cihazıyla birlikte).
///  - Windows 11'de sarı kayıt çerçevesi kapatılır (IsBorderRequired = false). Kapatılamazsa (Windows 10) bu sınıf
///    sadece GDI siyah gördüğünde, kısa süreli kullanılır (bkz. EkranAraclari.Yakala).
///  - Fare imleci görüntüye girmez. Her hata null döndürür; çağıran GDI'ye döner.
/// </summary>
internal static class EkranYakalayici
{
    private const int IlkKareBeklemeMs = 600;
    private const long BostaKapatMs = 5_000; // açık oturum DWM'ye her karede iş çıkarır: kullanılmayınca çabuk kapansın
    private const int KaliciKapatHataSayisi = 3;

    private static readonly object Kilit = new();
    private static readonly Dictionary<IntPtr, MonitorOturumu> Oturumlar = new();
    private static IDirect3DDevice? _cihaz;
    private static bool? _destek;
    private static bool _kenarsizOlabilir = true; // ilk oturumda çerçeve kapatılamazsa false olur
    private static bool _bozuk; // art arda KaliciKapatHataSayisi hata: bu çalışmada bir daha deneme (GDI)
    private static int _ardisikHata;
    private static readonly object DestekKilidi = new();
    private static long _sonKullanim;
    private static System.Threading.Timer? _bostaZamanlayici;

    /// <summary>Sistem destekliyor ve daha önce kurulum hatası olmadı.</summary>
    public static bool Destekleniyor
    {
        get
        {
            if (_bozuk) return false;
            lock (DestekKilidi) _destek ??= DestekVarMi();
            return _destek.Value;
        }
    }

    /// <summary>Destek denetimini (ilk çağrıda ~birkaç yüz ms) arka planda önceden yapar; UI iş parçacığı beklemesin.</summary>
    public static void OnHazirla() => Task.Run(() => _ = Destekleniyor);

    /// <summary>Çerçevesiz (görünmez) yakalama mümkün mü: Windows 11 ve izin. true ise birincil yöntem budur.</summary>
    public static bool Kenarsiz => Destekleniyor && _kenarsizOlabilir;

    private static bool DestekVarMi()
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return false;
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired") ||
                Environment.OSVersion.Version.Build < 22000)
                _kenarsizOlabilir = false;
            else
            {
                // Paketlenmemiş uygulamada izin sorulmaz; sonuç ne olursa olsun çerçeve ayarı oturumda denenir.
                try { GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000); }
                catch { /* eski sürüm */ }
            }
            return true;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Ekran yakalama desteği", ex);
            return false;
        }
    }

    /// <summary>
    /// Ekran alanını (fiziksel piksel) yakalar. Alan birden çok monitöre yayılabilir; monitör dışı kalan yer siyahtır.
    /// Olmazsa null.
    /// </summary>
    public static Bitmap? Yakala(Rectangle alan)
    {
        if (alan.Width <= 0 || alan.Height <= 0 || !Destekleniyor) return null;
        Bitmap? bmp = null;
        try
        {
            lock (Kilit)
            {
                _sonKullanim = Environment.TickCount64;
                _bostaZamanlayici ??= new System.Threading.Timer(_ => BostaysaKapat(), null, 2000, 2000);
                _cihaz ??= CihazOlustur();

                bmp = new Bitmap(alan.Width, alan.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
                bool herhangi = false;
                foreach (var (hmon, sinir) in Monitorler())
                {
                    var kesisim = Rectangle.Intersect(sinir, alan);
                    if (kesisim.IsEmpty) continue;
                    var o = Oturum(hmon, sinir);
                    if (o is null) { bmp.Dispose(); return null; }
                    if (!o.Kopyala(kesisim, bmp, alan.Location, IlkKareBeklemeMs))
                    {
                        // Kare yok ya da boyut değişti (çözünürlük): oturumu kapat, bu sefer GDI.
                        Oturumlar.Remove(hmon);
                        o.Dispose();
                        bmp.Dispose();
                        return null;
                    }
                    herhangi = true;
                }
                if (!herhangi) { bmp.Dispose(); return null; }
                if (!_kenarsizOlabilir) TumunuKapat(); // çerçeve görünüyor: oturum açık kalmasın
                _ardisikHata = 0;
                var sonuc = bmp;
                bmp = null;
                return sonuc;
            }
        }
        catch (Exception ex)
        {
            // Geçici olabilir (sürücü sıfırlandı, GPU değişti, monitör çıkarıldı): her şeyi bırak, sonraki çağrıda baştan kur.
            HataLog.Yaz("Ekran yakalama (WGC)", ex);
            bmp?.Dispose();
            Kapat();
            if (Interlocked.Increment(ref _ardisikHata) >= KaliciKapatHataSayisi) _bozuk = true;
            return null;
        }
    }

    /// <summary>Tüm oturumları kapatır (uygulama kapanırken ya da boşta).</summary>
    public static void Kapat()
    {
        lock (Kilit) TumunuKapat();
    }

    private static void TumunuKapat()
    {
        foreach (var o in Oturumlar.Values) o.Dispose();
        Oturumlar.Clear();
        _bostaZamanlayici?.Dispose();
        _bostaZamanlayici = null;
        // D3D cihazı da bırakılır: ekran kartı sürücüsü süreçte ~70 MB tutuyor (ölçüldü); yeniden kurmak ~50 ms.
        try { _cihaz?.Dispose(); } catch { /* kapanırken */ }
        _cihaz = null;
    }

    private static void BostaysaKapat()
    {
        lock (Kilit)
        {
            if (Oturumlar.Count == 0 && _cihaz is null) return;
            if (Environment.TickCount64 - _sonKullanim < BostaKapatMs) return;
            TumunuKapat(); // aynı kilit içinde: bu arada kullanılan oturum kapanmasın
        }
    }

    private static MonitorOturumu? Oturum(IntPtr hmon, Rectangle sinir)
    {
        if (Oturumlar.TryGetValue(hmon, out var o))
        {
            if (o.Sinir == sinir) return o;
            Oturumlar.Remove(hmon); // monitör yerleşimi değişti
            o.Dispose();
        }
        var item = OgeOlustur(hmon);
        if (item.Size.Width != sinir.Width || item.Size.Height != sinir.Height) return null; // ölçek uyuşmazlığı
        o = new MonitorOturumu(item, sinir, _cihaz!, _kenarsizOlabilir, out bool cerceveKapandi);
        if (_kenarsizOlabilir && !cerceveKapandi)
        {
            // Çerçeve kapatılamadı: sarı çerçeve ekranda dursun istemeyiz. Bundan sonra sadece gerektiğinde, kısa süreli.
            _kenarsizOlabilir = false;
        }
        Oturumlar[hmon] = o;
        return o;
    }

    /// <summary>Bir monitör oturumu: serbest iş parçacıklı kare havuzu, en son kare tutulur.</summary>
    private sealed class MonitorOturumu : IDisposable
    {
        public readonly Rectangle Sinir;
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _havuz;
        private readonly GraphicsCaptureSession _oturum;
        private readonly ManualResetEventSlim _ilkKare = new(false);
        private readonly object _kareKilidi = new();
        private Direct3D11CaptureFrame? _son;
        private bool _kapandi;

        public MonitorOturumu(GraphicsCaptureItem item, Rectangle sinir, IDirect3DDevice cihaz, bool kenarsizIste, out bool cerceveKapandi)
        {
            Sinir = sinir;
            _item = item;
            _havuz = Direct3D11CaptureFramePool.CreateFreeThreaded(cihaz, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                _havuz.FrameArrived += KareGeldi;
                _oturum = _havuz.CreateCaptureSession(item);
                _oturum.IsCursorCaptureEnabled = false;
                cerceveKapandi = false;
                if (kenarsizIste)
                {
                    try { _oturum.IsBorderRequired = false; cerceveKapandi = true; }
                    catch { /* izin yok / eski Windows */ }
                }
                _oturum.StartCapture();
            }
            catch
            {
                try { _oturum?.Dispose(); } catch { /* yarım kurulum */ }
                try { _havuz.Dispose(); } catch { /* yarım kurulum */ }
                _ilkKare.Dispose();
                throw;
            }
        }

        private void KareGeldi(Direct3D11CaptureFramePool havuz, object _)
        {
            Direct3D11CaptureFrame? eski;
            var yeni = havuz.TryGetNextFrame();
            if (yeni is null) return;
            lock (_kareKilidi)
            {
                if (_kapandi) { yeni.Dispose(); return; }
                eski = _son;
                _son = yeni;
            }
            eski?.Dispose();
            _ilkKare.Set();
        }

        /// <summary>Son karenin ekrandaki "kesisim" bölümünü hedefe (hedefKoken = hedefin ekran konumu) kopyalar.</summary>
        public bool Kopyala(Rectangle kesisim, Bitmap hedef, Point hedefKoken, int beklemeMs)
        {
            if (!_ilkKare.Wait(beklemeMs)) return false;
            SoftwareBitmap sb;
            lock (_kareKilidi)
            {
                if (_son is null) return false;
                if (_son.ContentSize.Width != Sinir.Width || _son.ContentSize.Height != Sinir.Height) return false;
                // GPU → CPU kopyası; kare kilit altında tutulur ki bu sırada atılmasın.
                sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(_son.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
            }
            using (sb)
            {
                if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8) return false;
                using var tampon = sb.LockBuffer(BitmapBufferAccessMode.Read);
                using var referans = tampon.CreateReference();
                referans.As<IMemoryBufferByteAccess>().GetBuffer(out IntPtr kaynak, out uint kapasite);
                var duzlem = tampon.GetPlaneDescription(0);

                int kx = kesisim.X - Sinir.X, ky = kesisim.Y - Sinir.Y;   // kaynak karede
                int hx = kesisim.X - hedefKoken.X, hy = kesisim.Y - hedefKoken.Y; // hedef bitmap'te
                long sonBayt = duzlem.StartIndex + (long)(ky + kesisim.Height - 1) * duzlem.Stride + (long)(kx + kesisim.Width) * 4;
                if (kx < 0 || ky < 0 || kx + kesisim.Width > duzlem.Width || ky + kesisim.Height > duzlem.Height || sonBayt > kapasite)
                    return false;

                var veri = hedef.LockBits(new Rectangle(hx, hy, kesisim.Width, kesisim.Height), ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppPArgb);
                try
                {
                    int satirBayt = kesisim.Width * 4;
                    var satir = new byte[satirBayt];
                    for (int y = 0; y < kesisim.Height; y++)
                    {
                        IntPtr k = kaynak + duzlem.StartIndex + (ky + y) * duzlem.Stride + kx * 4;
                        Marshal.Copy(k, satir, 0, satirBayt);
                        Marshal.Copy(satir, 0, veri.Scan0 + y * veri.Stride, satirBayt);
                    }
                }
                finally
                {
                    hedef.UnlockBits(veri);
                }
            }
            return true;
        }

        public void Dispose()
        {
            lock (_kareKilidi)
            {
                if (_kapandi) return;
                _kapandi = true;
                _son?.Dispose();
                _son = null;
            }
            try { _havuz.FrameArrived -= KareGeldi; } catch { /* kapanırken */ }
            try { _oturum.Dispose(); } catch { /* kapanırken */ }
            try { _havuz.Dispose(); } catch { /* kapanırken */ }
            _ilkKare.Dispose();
        }
    }

    // ---------------- Yerel arayüzler ----------------

    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    private static GraphicsCaptureItem OgeOlustur(IntPtr hmon)
    {
        var fabrika = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = fabrika.AsInterface<IGraphicsCaptureItemInterop>();
        Guid iid = IidGraphicsCaptureItem;
        IntPtr p = interop.CreateForMonitor(hmon, ref iid);
        try { return GraphicsCaptureItem.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    private static IDirect3DDevice CihazOlustur()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7;
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION, out IntPtr cihaz, out _, out IntPtr baglam);
        Marshal.ThrowExceptionForHR(hr);
        IntPtr dxgi = IntPtr.Zero, winrt = IntPtr.Zero;
        try
        {
            Guid iid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(cihaz, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out winrt));
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrt);
        }
        finally
        {
            if (winrt != IntPtr.Zero) Marshal.Release(winrt);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (baglam != IntPtr.Zero) Marshal.Release(baglam);
            Marshal.Release(cihaz);
        }
    }

    private static List<(IntPtr Hmon, Rectangle Sinir)> Monitorler()
    {
        var l = new List<(IntPtr, Rectangle)>(2);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT r, IntPtr _) =>
        {
            l.Add((h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)));
            return true;
        }, IntPtr.Zero);
        return l;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool MonitorGeriCagir(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorGeriCagir cb, IntPtr data);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
