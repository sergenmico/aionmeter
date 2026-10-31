namespace AionMeter;

/// <summary>
/// Genel kısayollar için yalnızca-mesaj penceresi (HWND_MESSAGE). RegisterHotKey kullanır; klavye kancası (hook) yok.
/// Overlay penceresinden ayrı olduğu için overlay gizlense/yeniden yaratılsa da kısayollar çalışır.
/// </summary>
internal sealed class KisayolPenceresi : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly List<int> _kayitli = new();

    public event Action<int>? Basildi;

    public KisayolPenceresi()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "AionMeter.Kisayol" });
    }

    /// <summary>Ctrl+Alt+tuş kaydeder. Başka bir program aynı tuşu almışsa false döner.</summary>
    public bool Kaydet(int id, Keys tus)
    {
        bool ok = Native.RegisterHotKey(Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)tus);
        if (ok) _kayitli.Add(id);
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            try { Basildi?.Invoke(m.WParam.ToInt32()); }
            catch (Exception ex) { HataLog.Yaz("Kısayol", ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _kayitli) Native.UnregisterHotKey(Handle, id);
        _kayitli.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
