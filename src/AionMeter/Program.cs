namespace AionMeter;

internal static class Program
{
    /// <summary>
    /// Motorun tek oluşturma noktası. Parametre: assets klasörü.
    /// null iken (gerçek motor bağlanmadıysa) ya da --demo verilince DemoEngine kullanılır.
    /// </summary>
    public static Func<string, IMeterEngine>? EngineFactory = assetsDir => new AionMeter.Core.Aion2MeterEngine(assetsDir);

    [STAThread]
    private static int Main(string[] args)
    {
        bool demoIstendi = false;
        foreach (string a in args)
            if (string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase)) demoIstendi = true;

        using var tekKopya = new Mutex(initiallyOwned: true, @"Local\AionMeter.TekKopya", out bool ilkKopya);
        if (!ilkKopya)
        {
            MessageBox.Show("AionMeter zaten çalışıyor (sistem tepsisine bakın).", "AionMeter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => HataLog.Yaz("Arayüz hatası", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HataLog.Yaz("Beklenmeyen hata", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { HataLog.Yaz("Gözlenmeyen görev hatası", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();

        bool demo = demoIstendi || EngineFactory is null;
        if (!demo) NpcapSetup.GerekirseKur(); // gerçek motor varsa: yakalama sürücüsünü ilk açılışta hallet

        var ayar = Ayarlar.Yukle();
        using IMeterEngine motor = MotorOlustur(demo);
        using (var form = new OverlayForm(motor, ayar, demo))
        {
            Application.Run(form);
        }
        GC.KeepAlive(tekKopya);
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
