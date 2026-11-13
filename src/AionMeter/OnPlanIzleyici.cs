using System.ComponentModel;
using System.Diagnostics;

namespace AionMeter;

/// <summary>
/// Ön plandaki pencere oyun mu? (AION2.exe; ayrıca kendi pencerelerimiz ve Windows ekran alıntısı araçları da "oyun önde" sayılır.)
/// Süreç adı sadece ön plandaki pid değiştiğinde çözülür; her tikte Process açılmaz.
/// </summary>
internal sealed class OnPlanIzleyici
{
    private const string OyunOneki = "AION2"; // AION2.exe, AION2-Win64-Shipping.exe vb.
    private static readonly string[] AlintiAraclari = { "ScreenClippingHost", "SnippingTool", "ScreenSketch" };
    private readonly uint _kendiPid = (uint)Environment.ProcessId;
    private uint _sonPid;
    private bool _sonSonuc, _sonOyun, _sonTamEkran;
    private bool _ilk = true;

    public bool OyunOnde()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return _sonSonuc; // pencere geçişi sırasında kısa süre 0 olabilir
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return _sonSonuc;
        if (pid == _kendiPid) return true;     // overlay, detay paneli ya da tepsi menüsü
        if (_ilk || pid != _sonPid)
        {
            _ilk = false;
            _sonPid = pid;
            (_sonSonuc, _sonOyun) = SurecIzinli(pid);
        }
        if (_sonSonuc) SonOyunPenceresi = hwnd; // ayarlar penceresi kapanınca odak buraya döner
        if (_sonOyun) OyunPenceresi = hwnd;
        return _sonSonuc;
    }

    /// <summary>En son önde görülen oyun penceresi (bizim pencerelerimiz hariç; ekran alıntısı araçları dahil); bilinmiyorsa 0.</summary>
    public IntPtr SonOyunPenceresi { get; private set; }

    /// <summary>En son önde görülen, gerçekten oyunun (AION2) penceresi; alıntı aracı sayılmaz. Konumlandırma için.</summary>
    public IntPtr OyunPenceresi { get; private set; }

    /// <summary>(izinli mi, oyunun kendisi mi)</summary>
    private static (bool Izinli, bool Oyun) SurecIzinli(uint pid)
    {
        try
        {
            using var surec = Process.GetProcessById((int)pid);
            string ad = surec.ProcessName;
            if (ad.StartsWith(OyunOneki, StringComparison.OrdinalIgnoreCase)) return (true, true);
            foreach (string arac in AlintiAraclari)
                if (ad.Equals(arac, StringComparison.OrdinalIgnoreCase)) return (true, false);
            return (false, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return (false, false); // süreç kapandı ya da erişim yok
        }
    }

    // ---------------- Özel (exclusive) tam ekran ----------------
    // Oyun özel tam ekrandayken üstünde en üstte duran bir pencere görünüp kaybolunca ya da en üste alınınca DXGI
    // tam ekran zincirini bozar; DLSS Kare Oluşturma (Streamline) bunu kaldıramayıp oyunu çökertir:
    // "Streamline/DLSSG present failed ... DXGI_ERROR_INVALID_CALL". Bu modda overlay ve çevirmen pencereleri hiç açılmaz.
    // Pencereli tam ekranda (FullscreenMode=1) Windows oyunu masaüstüyle birleştirir; overlay güvenle çalışır.

    private static readonly TimeSpan IniAraligi = TimeSpan.FromSeconds(2);
    private DateTime _iniBakildi = DateTime.MinValue, _iniYazim;
    private bool _iniTamEkran;

    /// <summary>
    /// Oyun önde ve özel tam ekranda mı? Windows'un bildirimi (QUNS_RUNNING_D3D_FULL_SCREEN) ya da oyunun kendi ayarı
    /// (GameUserSettings.ini FullscreenMode=0). Ayar dosyası sadece okunur. Ekranı kaplama şartı aranmaz: Alt+Tab ile oyuna dönülürken
    /// pencere henüz ekranı kaplamadan da (ön plan olayı anında) tam ekran sayılmalı ki pencerelerimiz bir an bile üstte kalmasın.
    /// </summary>
    public bool OzelTamEkran()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return _sonTamEkran; // pencere geçişi sırasında kısa süre 0 olabilir
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _kendiPid) return _sonTamEkran; // bizim pencerede karar değişmez
        if (!OyunOnde() || !_sonOyun) return _sonTamEkran = false; // alıntı aracı ya da başka program
        bool d3d = Native.SHQueryUserNotificationState(out int durum) == 0 && durum == Native.QUNS_RUNNING_D3D_FULL_SCREEN;
        return _sonTamEkran = d3d || IniTamEkran();
    }

    private bool IniTamEkran()
    {
        DateTime simdi = DateTime.UtcNow;
        if (simdi - _iniBakildi < IniAraligi) return _iniTamEkran;
        _iniBakildi = simdi;
        try
        {
            string kok = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2");
            FileInfo? en = null;
            foreach (string alt in new[] { "Saved_Steam", "Saved" })
            {
                var f = new FileInfo(Path.Combine(kok, alt, "Config", "Windows", "GameUserSettings.ini"));
                if (f.Exists && (en is null || f.LastWriteTimeUtc > en.LastWriteTimeUtc)) en = f;
            }
            if (en is null) return _iniTamEkran = false;
            if (en.LastWriteTimeUtc == _iniYazim) return _iniTamEkran;
            using var akis = new FileStream(en.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var okur = new StreamReader(akis);
            bool tam = false;
            while (okur.ReadLine() is { } satir)
                if (satir.StartsWith("FullscreenMode=", StringComparison.OrdinalIgnoreCase))
                    tam = satir.AsSpan("FullscreenMode=".Length).Trim().SequenceEqual("0");
            _iniYazim = en.LastWriteTimeUtc;
            return _iniTamEkran = tam;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _iniTamEkran; // oyun o an yazıyor olabilir: önceki sonuç, 2 sn sonra yeniden
        }
    }
}
