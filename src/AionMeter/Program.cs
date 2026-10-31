namespace AionMeter;

internal static class Program
{
    /// <summary>
    /// Motorun tek oluşturma noktası. Parametre: assets klasörü.
    /// null iken (gerçek motor bağlanmadıysa) ya da --demo verilince DemoEngine kullanılır.
    /// </summary>
    public static Func<string, IMeterEngine>? EngineFactory = assetsDir => new AionMeter.Core.Aion2MeterEngine(assetsDir);

    private const string MutexAdi = @"Local\AionMeter.TekKopya";

    [STAThread]
    private static int Main(string[] args)
    {
        bool demoIstendi = Arg(args, "--demo");
        bool guncellemeYok = Arg(args, "--guncelleme-yok");
        bool guncellendi = Arg(args, "--guncellendi"); // güncelleyicinin yeniden başlattığı süreç

        // Güncellemeden sonra eski süreç kapanırken mutex kısa süre daha tutulabilir: 10 sn'ye kadar bekle.
        Mutex? tekKopya = TekKopyaAl(guncellendi ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        if (tekKopya is null)
        {
            MessageBox.Show("AionMeter zaten çalışıyor (sistem tepsisine bakın).", "AionMeter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        try
        {
            return Calis(args, demoIstendi, guncellemeYok, guncellendi, ref tekKopya);
        }
        finally
        {
            if (tekKopya is not null)
            {
                try { tekKopya.ReleaseMutex(); } catch { /* sahip değilse */ }
                tekKopya.Dispose();
            }
        }
    }

    private static bool Arg(string[] args, string ad)
    {
        foreach (string a in args)
            if (string.Equals(a, ad, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Mutex? TekKopyaAl(TimeSpan bekle)
    {
        var saat = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var m = new Mutex(initiallyOwned: true, MutexAdi, out bool ilk);
            if (ilk) return m;
            m.Dispose();
            if (saat.Elapsed >= bekle) return null;
            Thread.Sleep(250);
        }
    }

    private static int Calis(string[] args, bool demoIstendi, bool guncellemeYok, bool guncellendi, ref Mutex? tekKopya)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HataLog.Yaz("Arayüz hatası", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HataLog.Yaz("Beklenmeyen hata", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { HataLog.Yaz("Gözlenmeyen görev hatası", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        var ayar = Ayarlar.Yukle();

        // Güncelleme: kalan AionMeter.old.exe / geçici klasörler arka planda silinir; sonra (gerekirse) kontrol.
        Guncelleyici.Temizle(guncellendi);
        string? bildirim = null;
        if (guncellendi)
        {
            // Döngü olmasın: yeni açılan süreç bu oturumda tekrar güncelleme denemez.
            bildirim = "AionMeter " + Guncelleyici.Goster(Guncelleyici.MevcutSurum) + " sürümüne güncellendi";
        }
        else if (!demoIstendi && !guncellemeYok && ayar.OtomatikGuncelleme)
        {
            var sonuc = Guncelleyici.Calistir();
            if (sonuc.YeniExe is string yeniExe)
            {
                // Mutex'i bırak ki yeni süreç alabilsin; başlatılamazsa geri al ve eski sürümle devam et.
                try { tekKopya!.ReleaseMutex(); } catch { /* yok say */ }
                tekKopya!.Dispose();
                tekKopya = null;
                if (Guncelleyici.YenidenBaslat(yeniExe, args)) return 0;
                tekKopya = TekKopyaAl(TimeSpan.FromSeconds(2));
                if (tekKopya is null) return 1;
            }
        }

        bool demo = demoIstendi || EngineFactory is null;
        if (!demo) NpcapSetup.GerekirseKur(); // gerçek motor varsa: yakalama sürücüsünü ilk açılışta hallet

        using IMeterEngine motor = MotorOlustur(demo);
        using (var form = new OverlayForm(motor, ayar, demo) { AcilisBildirimi = bildirim })
        {
            Application.Run(form);
        }
        return 0;
    }

    private static IMeterEngine MotorOlustur(bool demo)
    {
        string assetsDir = Path.Combine(AppContext.BaseDirectory, "assets", "aion2");
        if (demo || EngineFactory is null) return new DemoEngine(assetsDir); // beceri adları/ikonları için assets okunur
        try
        {
            return EngineFactory(assetsDir);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Motor oluşturulamadı", ex);
            return new HataliMotor("Motor başlatılamadı: " + ex.Message);
        }
    }
}
