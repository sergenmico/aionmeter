using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace AionMeter;

/// <summary>
/// AION 2 yönetici olarak çalışıyor. Windows (UIPI), yönetici olmayan bir programın, ön plandaki yönetici
/// penceresine giden tuşları okumasına izin vermez: GetAsyncKeyState oyundayken Alt'ı hep "basılı değil" görür
/// (Alt ile detay / Alt ile açıklama çevirisi çalışmaz). RegisterHotKey kısayolları bu engele takılmaz.
/// Bu yüzden açılışta bir kez yönetici izni istenir; kullanıcı "Hayır" derse uygulama normal haliyle açılır.
/// </summary>
internal static class Yonetici
{
    public const string Arguman = "--yonetici"; // yükseltilmiş yeni süreç: eski süreç kapanırken mutex'i bekler

    public static bool Mi()
    {
        try
        {
            using var kimlik = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(kimlik).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Aynı exe'yi yönetici olarak başlatır (Windows "Evet/Hayır" sorar). true = yeni süreç başladı, bu süreç kapanmalı.
    /// false = kullanıcı reddetti ya da başlatılamadı (ikisinde de reddedildi = true); bu süreç normal devam eder.
    /// </summary>
    public static bool YenidenBaslat(string[] args, out bool reddedildi)
    {
        reddedildi = false;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AionMeter.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(Arguman);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: kullanıcı "Hayır" dedi
        {
            reddedildi = true;
            return false;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Yönetici olarak başlatılamadı", ex);
            reddedildi = true; // kullanıcıya da söylensin (Alt özellikleri çalışmayacak)
            return false;
        }
    }
}
