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
    public bool Kaydet(int id, Keys tus) => Kaydet(id, Native.MOD_CONTROL | Native.MOD_ALT, tus);

    /// <summary>Değiştiriciler (MOD_*) + tuş kaydeder. Başka bir program aynı kombinasyonu almışsa false döner.</summary>
    public bool Kaydet(int id, uint degistirici, Keys tus)
    {
        bool ok = Native.RegisterHotKey(Handle, id, degistirici | Native.MOD_NOREPEAT, (uint)tus);
        if (ok && !_kayitli.Contains(id)) _kayitli.Add(id);
        return ok;
    }

    /// <summary>Kaydı kaldırır (kullanıcı kısayolu değiştirirken / kapatırken).</summary>
    public void Kaldir(int id)
    {
        if (!_kayitli.Remove(id)) return;
        Native.UnregisterHotKey(Handle, id);
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

/// <summary>
/// Kullanıcının atadığı kısayolun metni: "Ctrl+Alt+K" (sıra: Ctrl, Alt, Shift, Win, tuş). ayarlar.ini'de ve arayüzde aynı metin.
/// </summary>
internal static class Kisayol
{
    public static string Metin(uint degistirici, Keys tus)
    {
        var parca = new List<string>(5);
        if ((degistirici & Native.MOD_CONTROL) != 0) parca.Add("Ctrl");
        if ((degistirici & Native.MOD_ALT) != 0) parca.Add("Alt");
        if ((degistirici & Native.MOD_SHIFT) != 0) parca.Add("Shift");
        if ((degistirici & Native.MOD_WIN) != 0) parca.Add("Win");
        parca.Add(TusAdi(tus));
        return string.Join("+", parca);
    }

    /// <summary>"Ctrl+Alt+K" → değiştiriciler + tuş. En az bir değiştirici ve bir tuş yoksa false.</summary>
    public static bool Coz(string? metin, out uint degistirici, out Keys tus)
    {
        degistirici = 0;
        tus = Keys.None;
        if (string.IsNullOrWhiteSpace(metin)) return false;
        foreach (string ham in metin.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (ham.ToLowerInvariant())
            {
                case "ctrl": degistirici |= Native.MOD_CONTROL; break;
                case "alt": degistirici |= Native.MOD_ALT; break;
                case "shift": degistirici |= Native.MOD_SHIFT; break;
                case "win": degistirici |= Native.MOD_WIN; break;
                default:
                    if (tus != Keys.None) return false;
                    if (ham.Length == 1 && ham[0] is >= '0' and <= '9') tus = Keys.D0 + (ham[0] - '0');
                    else if (ham.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && ham.Length == 4 && char.IsDigit(ham[3]))
                        tus = Keys.NumPad0 + (ham[3] - '0');
                    else if (!Enum.TryParse(ham, true, out tus) || tus == Keys.None) return false;
                    break;
            }
        }
        return degistirici != 0 && tus != Keys.None && !DegistiriciMi(tus);
    }

    /// <summary>Metni tek biçime getirir ("ctrl + alt + k" → "Ctrl+Alt+K"); geçersizse null.</summary>
    public static string? Duzgun(string? metin) => Coz(metin, out uint d, out Keys t) ? Metin(d, t) : null;

    public static bool DegistiriciMi(Keys tus) => tus is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private static string TusAdi(Keys tus) => tus switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (tus - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (tus - Keys.NumPad0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => tus.ToString(),
    };
}
