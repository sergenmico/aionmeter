using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace AionMeter;

/// <summary>
/// İlk açılışta Npcap kontrolü. Yoksa kullanıcıya sorar, resmi kurucuyu indirir, Authenticode imzasını
/// (WinVerifyTrust) ve yayıncıyı doğrular, etkileşimli kurucuyu yönetici olarak başlatır.
/// Ücretsiz Npcap lisansı sessiz kurulumu yasakladığı için /S verilmez.
/// </summary>
internal static class NpcapSetup
{
    private const string IndirmeAdresi = "https://npcap.com/dist/npcap-1.89.exe";
    private const string DosyaAdi = "npcap-1.89.exe";
    private const string BeklenenYayinci = "Nmap Software LLC"; // npcap-1.89.exe'nin imzalayanı (CN), 2026-10-31'de doğrulandı
    private const long EnFazlaBoyut = 50L * 1024 * 1024;
    private const string Baslik = "AionMeter";

    public static bool KuruluMu()
    {
        string klasor = Path.Combine(Environment.SystemDirectory, "Npcap");
        return File.Exists(Path.Combine(klasor, "wpcap.dll")) && File.Exists(Path.Combine(klasor, "Packet.dll"));
    }

    /// <summary>Hiçbir durumda istisna fırlatmaz; başarısız olursa uygulama "Npcap bulunamadı" ile çalışmaya devam eder.</summary>
    public static void GerekirseKur()
    {
        try
        {
            if (KuruluMu()) return;

            var cevap = MessageBox.Show(
                "Npcap gerekli. Şimdi indirip kurulumu başlatayım mı?\n\n" +
                "Npcap, oyun trafiğini yalnızca okumak için kullanılan ücretsiz yakalama sürücüsüdür (npcap.com).",
                Baslik, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (cevap != DialogResult.Yes) return;

            string? hata = IndirVeKur();
            if (hata != null)
            {
                MessageBox.Show(hata + "\n\nAionMeter açılacak ama \"Npcap bulunamadı\" gösterecek.",
                    Baslik, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!KuruluMu())
            {
                MessageBox.Show("Npcap kurulumu tamamlanmadı.\n\nAionMeter açılacak ama \"Npcap bulunamadı\" gösterecek. " +
                                "Npcap'i kurduktan sonra AionMeter'ı yeniden başlatın.",
                    Baslik, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Npcap kurulumu", ex);
        }
    }

    /// <summary>null = kurucu çalıştı ve kapandı; aksi halde kullanıcıya gösterilecek hata.</summary>
    private static string? IndirVeKur()
    {
        DirectoryInfo? klasor = null;
        FileStream? kilit = null;
        try
        {
            klasor = Directory.CreateTempSubdirectory("aionmeter-npcap-");
            string yol = Path.Combine(klasor.FullName, DosyaAdi);

            using (var form = new IndirmeFormu(IndirmeAdresi, yol, EnFazlaBoyut))
            {
                var sonuc = form.ShowDialog();
                if (sonuc == DialogResult.Cancel) return "İndirme iptal edildi.";
                if (form.Hata is not null)
                {
                    HataLog.Yaz("Npcap indirilemedi", form.Hata);
                    return "Npcap indirilemedi: " + form.Hata.Message;
                }
            }

            // Doğrulama ile çalıştırma arasında dosya değiştirilemesin: salt-okuma paylaşımıyla açık tut.
            kilit = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (!ImzaGecerli(yol, out int kod))
            {
                HataLog.Yaz("Npcap imzası geçersiz, WinVerifyTrust=0x" + kod.ToString("X8"));
                return "İndirilen Npcap kurucusunun dijital imzası doğrulanamadı; dosya silindi ve kurulum iptal edildi.";
            }
            string? yayinci = Yayinci(yol);
            if (!string.Equals(yayinci, BeklenenYayinci, StringComparison.Ordinal))
            {
                HataLog.Yaz("Npcap yayıncısı beklenmedik: " + (yayinci ?? "(yok)"));
                return "İndirilen dosya beklenen yayıncı (" + BeklenenYayinci + ") tarafından imzalanmamış; dosya silindi ve kurulum iptal edildi.";
            }

            MessageBox.Show(
                "Npcap kurulum penceresi açılacak (yönetici onayı istenir).\n\n" +
                "Varsayılan seçenekler yeterlidir; \"WinPcap API-compatible mode\" kutusunu işaretlemeniz gerekmez.\n" +
                "Kurulum bitince AionMeter kendiliğinden açılır.",
                Baslik, MessageBoxButtons.OK, MessageBoxIcon.Information);

            using var surec = Process.Start(new ProcessStartInfo(yol)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = klasor.FullName,
            });
            surec?.WaitForExit();
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC reddedildi
        {
            return "Yönetici onayı verilmediği için Npcap kurulmadı.";
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Npcap kurulumu", ex);
            return "Npcap kurulamadı: " + ex.Message;
        }
        finally
        {
            kilit?.Dispose();
            try { klasor?.Delete(recursive: true); } catch (Exception ex) { HataLog.Yaz("Geçici Npcap klasörü silinemedi", ex); }
        }
    }

    /// <summary>Dosyanın gömülü Authenticode imzası geçerli ve güvenilir bir köke zincirli mi? (WinVerifyTrust)</summary>
    internal static bool ImzaGecerli(string yol, out int sonucKodu)
    {
        IntPtr yolPtr = Marshal.StringToHGlobalUni(yol);
        IntPtr dosyaPtr = IntPtr.Zero;
        try
        {
            var dosya = new Native.WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_FILE_INFO>(),
                pcwszFilePath = yolPtr,
            };
            dosyaPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(dosya, dosyaPtr, false);

            var veri = new Native.WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<Native.WINTRUST_DATA>(),
                dwUIChoice = Native.WTD_UI_NONE,
                fdwRevocationChecks = Native.WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = Native.WTD_CHOICE_FILE,
                pFile = dosyaPtr,
                dwStateAction = Native.WTD_STATEACTION_VERIFY,
                dwProvFlags = Native.WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
            };
            var eylem = Native.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            sonucKodu = Native.WinVerifyTrust(IntPtr.Zero, ref eylem, ref veri);

            veri.dwStateAction = Native.WTD_STATEACTION_CLOSE;
            Native.WinVerifyTrust(IntPtr.Zero, ref eylem, ref veri);
            return sonucKodu == 0;
        }
        finally
        {
            if (dosyaPtr != IntPtr.Zero) Marshal.FreeHGlobal(dosyaPtr);
            Marshal.FreeHGlobal(yolPtr);
        }
    }

    /// <summary>İmzalayan sertifikanın ortak adı (CN). İmza yoksa null.</summary>
    internal static string? Yayinci(string yol)
    {
        try
        {
#pragma warning disable SYSLIB0057 // İmzalı dosyadan sertifika okumanın X509CertificateLoader karşılığı yok.
            using var imza = X509Certificate.CreateFromSignedFile(yol);
#pragma warning restore SYSLIB0057
            using var sertifika = new X509Certificate2(imza);
            return sertifika.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>İlerlemeyi gösteren küçük indirme penceresi.</summary>
    private sealed class IndirmeFormu : Form
    {
        private readonly string _adres, _hedef;
        private readonly long _enFazla;
        private readonly Label _etiket;
        private readonly ProgressBar _cubuk;
        private static readonly TimeSpan DurmaSuresi = TimeSpan.FromSeconds(30); // 30 sn hiç veri gelmezse vazgeç
        private readonly CancellationTokenSource _iptal = new();
        private bool _bitti; // indirme görevi bitti (dosya kapandı); pencere ancak o zaman kapanır

        public Exception? Hata { get; private set; }

        public IndirmeFormu(string adres, string hedef, long enFazla)
        {
            _adres = adres;
            _hedef = hedef;
            _enFazla = enFazla;
            Text = "Npcap indiriliyor";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(360, 84);
            _etiket = new Label { Left = 12, Top = 12, Width = 336, Height = 20, Text = "npcap.com adresine bağlanılıyor…" };
            _cubuk = new ProgressBar { Left = 12, Top = 40, Width = 336, Height = 20, Maximum = 1000 };
            Controls.Add(_etiket);
            Controls.Add(_cubuk);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            DialogResult sonuc;
            try
            {
                await IndirAsync(_iptal.Token);
                sonuc = DialogResult.OK;
            }
            catch (OperationCanceledException) when (_iptal.IsCancellationRequested)
            {
                sonuc = DialogResult.Cancel; // kullanıcı kapattı
            }
            catch (Exception ex)
            {
                Hata = ex;
                sonuc = DialogResult.Abort;
            }
            _bitti = true;
            if (!IsDisposed) DialogResult = sonuc; // ShowDialog ancak görev bittikten sonra döner
        }

        private async Task IndirAsync(CancellationToken iptal)
        {
            try
            {
                await IndirIcAsync(iptal);
            }
            catch (OperationCanceledException) when (!iptal.IsCancellationRequested)
            {
                throw new TimeoutException("İndirme " + DurmaSuresi.TotalSeconds.ToString("0", Bicim.Tr) + " saniye boyunca ilerlemedi.");
            }
        }

        /// <summary>Her adım (bağlantı, her okuma) için ayrı 30 sn'lik süre; veri geldikçe süre baştan başlar.</summary>
        private static CancellationTokenSource Adim(CancellationToken iptal)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(iptal);
            cts.CancelAfter(DurmaSuresi);
            return cts;
        }

        private async Task IndirIcAsync(CancellationToken iptal)
        {
            using var istemci = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            istemci.DefaultRequestHeaders.UserAgent.ParseAdd("AionMeter/1.0");
            HttpResponseMessage yanitHam;
            using (var adim = Adim(iptal))
                yanitHam = await istemci.GetAsync(_adres, HttpCompletionOption.ResponseHeadersRead, adim.Token);
            using var yanit = yanitHam;
            yanit.EnsureSuccessStatusCode();
            long? toplam = yanit.Content.Headers.ContentLength;
            if (toplam > _enFazla) throw new InvalidDataException("Dosya beklenenden büyük.");

            await using var kaynak = await yanit.Content.ReadAsStreamAsync(iptal);
            await using var hedef = new FileStream(_hedef, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var tampon = new byte[81920];
            long okunan = 0;
            while (true)
            {
                int n;
                using (var adim = Adim(iptal))
                    n = await kaynak.ReadAsync(tampon, adim.Token);
                if (n <= 0) break;
                okunan += n;
                if (okunan > _enFazla) throw new InvalidDataException("Dosya beklenenden büyük.");
                await hedef.WriteAsync(tampon.AsMemory(0, n), iptal);
                if (toplam > 0)
                {
                    _cubuk.Value = (int)Math.Min(1000, okunan * 1000 / toplam.Value);
                    _etiket.Text = "İndiriliyor… " + (okunan / 1024).ToString("N0", Bicim.Tr) + " / " +
                                   (toplam.Value / 1024).ToString("N0", Bicim.Tr) + " KB";
                }
                else
                {
                    _etiket.Text = "İndiriliyor… " + (okunan / 1024).ToString("N0", Bicim.Tr) + " KB";
                }
            }
            if (okunan == 0) throw new InvalidDataException("Boş dosya indirildi.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_bitti)
            {
                // Görev hâlâ dosyaya yazıyor: önce iptal et, pencere görev bitince (OnShown sonunda) kapanır.
                // Böylece geçici klasör, dosya açıkken silinmeye çalışılmaz.
                e.Cancel = true;
                if (!_iptal.IsCancellationRequested)
                {
                    _etiket.Text = "İptal ediliyor…";
                    _iptal.Cancel();
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iptal.Dispose();
            base.Dispose(disposing);
        }
    }
}
