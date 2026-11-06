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
    private bool _sonSonuc;
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
            _sonSonuc = SurecIzinli(pid);
        }
        if (_sonSonuc) SonOyunPenceresi = hwnd; // ayarlar penceresi kapanınca odak buraya döner
        return _sonSonuc;
    }

    /// <summary>En son önde görülen oyun penceresi (bizim pencerelerimiz hariç); bilinmiyorsa 0.</summary>
    public IntPtr SonOyunPenceresi { get; private set; }

    private static bool SurecIzinli(uint pid)
    {
        try
        {
            using var surec = Process.GetProcessById((int)pid);
            string ad = surec.ProcessName;
            if (ad.StartsWith(OyunOneki, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string arac in AlintiAraclari)
                if (ad.Equals(arac, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // süreç kapandı ya da erişim yok
        }
    }
}
