using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AionMeter;

/// <summary>
/// Açılışta GitHub'daki son sürümü kontrol eder; yenisi varsa notlarını gösterip sorar, onaylanırsa
/// AionMeter.zip'i indirir, AionMeter.zip.sha256 ile doğrular ve geçici klasöre açar.
/// Kurulumu ÇALIŞAN süreç yapmaz (tek dosya paketi derlemeleri exe'den tembel yükler; exe değişirse çöker):
/// geçici klasördeki YENİ exe "--kur &lt;klasör&gt; --bekle &lt;pid&gt;" ile başlatılır, eski süreç hemen çıkar;
/// kurucu eski sürecin bitmesini bekleyip dosyaları kopyalar (exe en son) ve uygulamayı --guncellendi ile açar.
/// Her hata eski sürümle devam etmeyle sonuçlanır (hata.log'a yazılır). ayarlar.ini ve hata.log'a dokunulmaz.
/// </summary>
internal static class Guncelleyici
{
    private const string VarsayilanApi = "https://api.github.com/repos/sergenmico/aionmeter/releases/latest";
    private const string ApiDegiskeni = "AIONMETER_GUNCELLEME_URL"; // test için yerel sunucu
    private const string ZipAdi = "AionMeter.zip", ShaAdi = "AionMeter.zip.sha256";
    private const string EskiExeAdi = "AionMeter.old.exe", EskiAssetsAdi = "assets.eski";
    private const string GeciciOnek = "aionmeter-guncelleme-";
    private static readonly TimeSpan KontrolSuresi = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DurmaSuresi = TimeSpan.FromSeconds(30);
    private const long ZipSiniri = 300L * 1024 * 1024;
    private const long AcilmisSinir = 1024L * 1024 * 1024; // zip bombası koruması
    private const int NotSiniri = 4000;
    private static readonly string[] Korunanlar = { "ayarlar.ini", "ayarlar.ini.tmp", "hata.log", EskiExeAdi };

    /// <summary>KurucuExe != null → geçici klasördeki yeni exe kurulum için başlatılmalı (Program.Main).</summary>
    public sealed record Sonuc(string? KurucuExe, string? Gecici);
    private static readonly Sonuc Devam = new(null, null);

    private sealed record SurumBilgisi(Version Surum, string Etiket, string ZipUrl, string ShaUrl, string Notlar);

    /// <summary>AssemblyInformationalVersion ("0.2.0+abc123" → 0.2.0).</summary>
    public static Version MevcutSurum { get; } = SurumOku();

    private static Version SurumOku()
    {
        string? bilgi = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (bilgi is not null)
        {
            int arti = bilgi.IndexOf('+');
            if (arti >= 0) bilgi = bilgi[..arti];
            if (SurumCoz(bilgi) is Version v) return v;
        }
        return Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    }

    private static Version? SurumCoz(string? metin)
    {
        if (string.IsNullOrWhiteSpace(metin)) return null;
        metin = metin.Trim().TrimStart('v', 'V');
        int tire = metin.IndexOf('-'); // "1.2.3-beta" → 1.2.3
        if (tire >= 0) metin = metin[..tire];
        return Version.TryParse(metin, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static string Goster(Version v) => "v" + v.ToString(3);

    private static string ExeYolu => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AionMeter.exe");
    private static string UygulamaKlasoru => Path.GetDirectoryName(ExeYolu) ?? AppContext.BaseDirectory;

    /// <summary>Tek dosya yayını mı? Geliştirme derlemesinde (yanında AionMeter.dll) güncelleme yapılmaz.</summary>
    private static bool YayinDerlemesi => !File.Exists(Path.Combine(UygulamaKlasoru, "AionMeter.dll"));

    // ---------------- Açılış temizliği ----------------

    /// <summary>Kalan AionMeter.old.exe, assets.eski ve %TEMP%\aionmeter-guncelleme-* klasörlerini arka planda siler.</summary>
    public static void Temizle(bool guncellendi)
    {
        string klasor = UygulamaKlasoru;
        Task.Run(() =>
        {
            // Eski süreç kapanırken exe kısa süre kilitli kalabilir: birkaç kez dene.
            int deneme = guncellendi ? 40 : 3;
            string eskiExe = Path.Combine(klasor, EskiExeAdi);
            for (int i = 0; i < deneme && File.Exists(eskiExe); i++)
            {
                try { File.Delete(eskiExe); }
                catch { Thread.Sleep(250); }
            }
            try
            {
                string eskiAssets = Path.Combine(klasor, EskiAssetsAdi);
                if (Directory.Exists(eskiAssets)) Directory.Delete(eskiAssets, recursive: true);
            }
            catch (Exception ex) { HataLog.Yaz("assets.eski silinemedi", ex); }
            try
            {
                foreach (string d in Directory.EnumerateDirectories(Path.GetTempPath(), GeciciOnek + "*"))
                {
                    // Kurucu exe bu klasörden çalıştı; kapanması birkaç yüz ms sürebilir.
                    for (int i = 0; i < deneme && Directory.Exists(d); i++)
                    {
                        try { Directory.Delete(d, recursive: true); }
                        catch { Thread.Sleep(250); }
                    }
                }
            }
            catch { /* önemsiz */ }
        });
    }

    // ---------------- Giriş noktası ----------------

    /// <summary>Program.Main'den çağrılır (mutex alındıktan sonra, Npcap'ten önce). Asla istisna fırlatmaz.</summary>
    public static Sonuc Calistir()
    {
        try
        {
            if (!YayinDerlemesi) return Devam;
            using var form = new GuncellemeFormu();
            form.ShowDialog();
            return form.Sonuc;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleyici", ex);
            return Devam;
        }
    }

    private static readonly string[] KontrolArgumanlari = { "--guncellendi", "--guncelleme-yok", "--kur", "--bekle", Yonetici.Arguman };

    /// <summary>Özgün argümanlar, güncelleyicinin kendi bayrakları (ve --kur/--bekle değerleri) çıkarılarak.</summary>
    private static IEnumerable<string> KullaniciArgumanlari(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--kur", StringComparison.OrdinalIgnoreCase) || a.Equals("--bekle", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            bool kontrol = false;
            foreach (string k in KontrolArgumanlari) if (a.Equals(k, StringComparison.OrdinalIgnoreCase)) kontrol = true;
            if (!kontrol) yield return a;
        }
    }

    /// <summary>
    /// ESKİ süreçte: geçici klasördeki yeni exe'yi "--kur &lt;uygulama klasörü&gt; --bekle &lt;pid&gt;" ile başlatır.
    /// Uygulama klasörüne hiç dokunulmadan çağrılır; başarılıysa çağıran hemen çıkmalıdır.
    /// </summary>
    public static bool KurucuyuBaslat(Sonuc sonuc, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(sonuc.KurucuExe!)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(sonuc.KurucuExe!)!,
            };
            psi.ArgumentList.Add("--kur");
            psi.ArgumentList.Add(UygulamaKlasoru); // boşluklu / Türkçe karakterli yollar ArgumentList ile güvenle aktarılır
            psi.ArgumentList.Add("--bekle");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string a in KullaniciArgumanlari(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleme kurucusu başlatılamadı", ex);
            try { if (sonuc.Gecici is not null) Directory.Delete(sonuc.Gecici, recursive: true); } catch { /* açılışta temizlenir */ }
            return false;
        }
    }

    /// <summary>args içinde "--kur &lt;klasör&gt;" varsa kurulum modudur (Program.Main bunu her şeyden önce kontrol eder).</summary>
    public static bool KurulumModuMu(string[] args, out string klasor, out int beklePid)
    {
        klasor = "";
        beklePid = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--kur", StringComparison.OrdinalIgnoreCase)) klasor = args[i + 1];
            else if (args[i].Equals("--bekle", StringComparison.OrdinalIgnoreCase))
                int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out beklePid);
        }
        return klasor.Length > 0;
    }

    /// <summary>
    /// KURUCU süreç (geçici klasördeki yeni exe): eski süreç bitince dosyaları uygulama klasörüne kopyalar,
    /// sonra uygulamayı açar (başarılıysa --guncellendi, değilse eski sürüm --guncelleme-yok ile). Hiç UI yok.
    /// </summary>
    public static int KurulumModu(string uygKlasor, int beklePid, string[] args)
    {
        uygKlasor = Path.GetFullPath(uygKlasor);
        HataLog.Klasor = uygKlasor; // hata.log uygulama klasöründe kalsın
        bool basarili = false;
        try
        {
            if (beklePid > 0)
            {
                try
                {
                    using var eski = Process.GetProcessById(beklePid);
                    if (!eski.WaitForExit(15000)) HataLog.Yaz("Güncelleme: eski süreç 15 sn içinde kapanmadı, yine de deneniyor");
                }
                catch (ArgumentException) { /* zaten kapanmış */ }
            }
            string kaynak = Path.GetFullPath(AppContext.BaseDirectory);
            string? hata = DosyalariKopyala(kaynak, uygKlasor);
            if (hata is null) basarili = true;
            else HataLog.Yaz("Güncelleme kurulamadı: " + hata);
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleme kurulumu", ex);
        }

        try
        {
            string exe = Path.Combine(uygKlasor, "AionMeter.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = uygKlasor };
            psi.ArgumentList.Add(basarili ? "--guncellendi" : "--guncelleme-yok"); // başarısızsa aynı oturumda tekrar sorma
            foreach (string a in KullaniciArgumanlari(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleme sonrası uygulama açılamadı", ex);
            return 1;
        }
    }

    /// <summary>
    /// Kaynak (açılmış yeni sürüm) → uygulama klasörü. assets bütünüyle değişir (eskisi assets.eski'ye alınır),
    /// exe EN SON kopyalanır (kilitliyse birkaç sn tekrar denenir). Hata olursa assets geri alınır; exe kopyalanamadıysa
    /// eski exe yerinde kalır. ayarlar.ini / hata.log'a dokunulmaz. null = başarılı.
    /// </summary>
    private static string? DosyalariKopyala(string kaynakKok, string uygKlasor)
    {
        string kaynakExe = Path.Combine(kaynakKok, "AionMeter.exe");
        if (!File.Exists(kaynakExe)) return "kaynakta AionMeter.exe yok: " + kaynakKok;
        string assets = Path.Combine(uygKlasor, "assets");
        string eskiAssets = Path.Combine(uygKlasor, EskiAssetsAdi);
        bool assetsTasindi = false;
        try
        {
            if (Directory.Exists(eskiAssets)) Directory.Delete(eskiAssets, recursive: true);
            if (Directory.Exists(assets))
            {
                Directory.Move(assets, eskiAssets);
                assetsTasindi = true;
            }

            foreach (string dosya in Directory.EnumerateFiles(kaynakKok, "*", SearchOption.AllDirectories))
            {
                string goreli = Path.GetRelativePath(kaynakKok, dosya);
                if (Korunan(goreli) || goreli.Equals("AionMeter.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string hedef = Path.Combine(uygKlasor, goreli);
                string? hedefKlasor = Path.GetDirectoryName(hedef);
                if (hedefKlasor is not null) Directory.CreateDirectory(hedefKlasor);
                File.Copy(dosya, hedef, overwrite: true);
            }

            // exe en son: eski süreç yeni kapandıysa dosya kısa süre kilitli olabilir.
            string hedefExe = Path.Combine(uygKlasor, "AionMeter.exe");
            for (int deneme = 1; ; deneme++)
            {
                try
                {
                    File.Copy(kaynakExe, hedefExe, overwrite: true);
                    break;
                }
                catch (IOException) when (deneme < 20)
                {
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException) when (deneme < 20)
                {
                    Thread.Sleep(250);
                }
            }

            if (assetsTasindi)
            {
                try { Directory.Delete(eskiAssets, recursive: true); } catch { /* --guncellendi açılışında silinir */ }
            }
            return null;
        }
        catch (Exception ex)
        {
            if (assetsTasindi)
            {
                try
                {
                    if (Directory.Exists(assets)) Directory.Delete(assets, recursive: true);
                    Directory.Move(eskiAssets, assets);
                }
                catch (Exception ex2) { HataLog.Yaz("Güncelleme geri alma (assets)", ex2); }
            }
            return ex.GetType().Name + ": " + ex.Message;
        }
    }


    // ---------------- Akış (UI iş parçacığında, async) ----------------

    private static async Task<Sonuc> AkisAsync(GuncellemeFormu f, CancellationToken iptal)
    {
        var mevcut = MevcutSurum;
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = KontrolSuresi,
        })
        { Timeout = Timeout.InfiniteTimeSpan }; // süreler CancellationToken ile yönetilir
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AionMeter/" + mevcut.ToString(3));

        // 1) Kontrol (en fazla 6 sn)
        f.Durum("Güncelleme kontrol ediliyor…");
        SurumBilgisi? bilgi;
        try
        {
            using var kontrol = CancellationTokenSource.CreateLinkedTokenSource(iptal);
            kontrol.CancelAfter(KontrolSuresi);
            bilgi = await KontrolAsync(http, kontrol.Token);
        }
        catch (OperationCanceledException) when (iptal.IsCancellationRequested)
        {
            return Devam;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleme kontrolü başarısız", ex);
            return Devam;
        }
        if (bilgi is null || bilgi.Surum <= mevcut) return Devam; // güncel: sessizce kapan

        // 2) Sor
        string surumMetni = Goster(mevcut) + " → " + Goster(bilgi.Surum);
        if (!await f.SorAsync(surumMetni, bilgi.Notlar)) return Devam; // "Şimdi değil"

        f.Baslik("AionMeter güncelleniyor  " + surumMetni);
        string klasor = UygulamaKlasoru;
        if (!KlasorYazilabilir(klasor))
        {
            HataLog.Yaz("Güncelleme: klasöre yazılamıyor: " + klasor);
            await f.MesajAsync("Güncelleme var ama klasöre yazılamıyor (" + klasor + ")", 3000);
            return Devam;
        }

        // 3) İndir, doğrula, kur
        string gecici = Path.Combine(Path.GetTempPath(), GeciciOnek + Guid.NewGuid().ToString("N"));
        bool devredildi = false;
        try
        {
            Directory.CreateDirectory(gecici);
            string zipYol = Path.Combine(gecici, ZipAdi);
            f.IptalGoster(true);
            f.Durum("İndiriliyor…");
            var sonGuncelleme = Stopwatch.StartNew();
            await IndirAsync(http, bilgi.ZipUrl, zipYol, ZipSiniri, iptal, (okunan, toplam) =>
            {
                if (sonGuncelleme.ElapsedMilliseconds < 100 && okunan != toplam) return; // ekranı ~10 Hz güncelle
                sonGuncelleme.Restart();
                f.Ilerleme(okunan, toplam);
            });

            f.IptalGoster(false);
            f.Durum("Doğrulanıyor…");
            string shaYol = Path.Combine(gecici, ShaAdi);
            await IndirAsync(http, bilgi.ShaUrl, shaYol, 4096, iptal, null);
            string? beklenen = ShaCoz(await File.ReadAllTextAsync(shaYol, iptal));
            string gercek = await Task.Run(() => DosyaSha256(zipYol), iptal);
            if (beklenen is null || !string.Equals(beklenen, gercek, StringComparison.Ordinal))
            {
                HataLog.Yaz("Güncelleme doğrulanamadı: beklenen=" + (beklenen ?? "(geçersiz)") + " gerçek=" + gercek);
                await f.MesajAsync("Güncelleme doğrulanamadı, eski sürümle devam ediliyor", 2000);
                return Devam;
            }

            f.Durum("Kuruluyor…");
            string acilan = Path.Combine(gecici, "acilan");
            string? hata = await Task.Run(() =>
            {
                try
                {
                    ZipAc(zipYol, acilan);
                    File.Delete(zipYol); // yer açılsın
                    return File.Exists(Path.Combine(acilan, "AionMeter", "AionMeter.exe")) ? null : "zip içinde AionMeter/AionMeter.exe yok";
                }
                catch (Exception ex)
                {
                    return "zip açılamadı: " + ex.Message;
                }
            });
            if (hata is not null)
            {
                HataLog.Yaz("Güncelleme kurulamadı: " + hata);
                await f.MesajAsync("Güncelleme kurulamadı, eski sürümle devam ediliyor", 3000);
                return Devam;
            }

            await f.MesajAsync("Güncellendi, yeniden başlatılıyor…", 700);
            devredildi = true; // geçici klasör kurucu exe'yi içeriyor: burada silinmez
            return new Sonuc(Path.Combine(acilan, "AionMeter", "AionMeter.exe"), gecici);
        }
        catch (OperationCanceledException) when (iptal.IsCancellationRequested)
        {
            HataLog.Yaz("Güncelleme kullanıcı tarafından iptal edildi");
            return Devam;
        }
        catch (Exception ex)
        {
            HataLog.Yaz("Güncelleme indirilemedi", ex);
            await f.MesajAsync("Güncelleme indirilemedi, eski sürümle devam ediliyor", 2000);
            return Devam;
        }
        finally
        {
            if (!devredildi)
            {
                try { Directory.Delete(gecici, recursive: true); } catch { /* açılışta temizlenir */ }
            }
        }
    }

    private static async Task<SurumBilgisi?> KontrolAsync(HttpClient http, CancellationToken ct)
    {
        string? ozel = Environment.GetEnvironmentVariable(ApiDegiskeni);
        bool yerelTest = !string.IsNullOrWhiteSpace(ozel);
        string url = yerelTest ? ozel!.Trim() : VarsayilanApi;

        using var istek = new HttpRequestMessage(HttpMethod.Get, url);
        istek.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var yanit = await http.SendAsync(istek, HttpCompletionOption.ResponseHeadersRead, ct);
        yanit.EnsureSuccessStatusCode(); // 403 (oran sınırı), 404 (release yok) → hata.log
        await using var akis = await yanit.Content.ReadAsStreamAsync(ct);
        using var belge = await JsonDocument.ParseAsync(akis, cancellationToken: ct);
        var kok = belge.RootElement;

        string etiket = kok.GetProperty("tag_name").GetString() ?? "";
        var surum = SurumCoz(etiket) ?? throw new InvalidDataException("Geçersiz tag_name: " + etiket);
        string? zip = null, sha = null;
        foreach (var a in kok.GetProperty("assets").EnumerateArray())
        {
            string? ad = a.GetProperty("name").GetString();
            string? adres = a.GetProperty("browser_download_url").GetString();
            if (string.Equals(ad, ZipAdi, StringComparison.OrdinalIgnoreCase)) zip = adres;
            else if (string.Equals(ad, ShaAdi, StringComparison.OrdinalIgnoreCase)) sha = adres;
        }
        if (!AdresGecerli(zip, yerelTest) || !AdresGecerli(sha, yerelTest))
            throw new InvalidDataException("Release'te " + ZipAdi + " / " + ShaAdi + " yok ya da adres geçersiz");
        string notlar = kok.TryGetProperty("body", out var govde) && govde.ValueKind == JsonValueKind.String
            ? govde.GetString() ?? "" : "";
        return new SurumBilgisi(surum, etiket, zip!, sha!, NotlariSadelestir(notlar));
    }

    /// <summary>Normalde sadece https; yerel test sunucusu (ortam değişkeni) için http de kabul edilir.</summary>
    private static bool AdresGecerli(string? adres, bool yerelTest) =>
        Uri.TryCreate(adres, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || (yerelTest && u.Scheme == Uri.UriSchemeHttp));

    /// <summary>GitHub markdown'ını düz metne çevirir: başlık #'leri, **, ` kalkar; - / * maddeleri "• " olur.</summary>
    internal static string NotlariSadelestir(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var sb = new StringBuilder(Math.Min(md.Length, NotSiniri + 16));
        foreach (string hamSatir in md.Replace("\r\n", "\n").Split('\n'))
        {
            string satir = hamSatir.TrimEnd();
            string bas = satir.TrimStart();
            string girinti = satir[..(satir.Length - bas.Length)];
            if (bas.StartsWith('#')) bas = bas.TrimStart('#').TrimStart();
            else if (bas.StartsWith("- ") || bas.StartsWith("* ")) bas = "• " + bas[2..];
            bas = bas.Replace("**", "").Replace("`", "");
            sb.Append(girinti).Append(bas).Append("\r\n");
            if (sb.Length > NotSiniri) break;
        }
        string sonuc = sb.ToString().Trim();
        return sonuc.Length > NotSiniri ? sonuc[..NotSiniri] + "…" : sonuc;
    }

    // ---------------- İndirme / doğrulama ----------------

    /// <summary>Her adım için ayrı 30 sn'lik durma süresi (veri geldikçe yenilenir) ve boyut sınırı.</summary>
    private static async Task IndirAsync(HttpClient http, string url, string hedef, long sinir, CancellationToken iptal,
        Action<long, long?>? ilerleme)
    {
        try
        {
            HttpResponseMessage yanitHam;
            using (var adim = Adim(iptal))
                yanitHam = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, adim.Token);
            using var yanit = yanitHam;
            yanit.EnsureSuccessStatusCode();
            long? toplam = yanit.Content.Headers.ContentLength;
            if (toplam > sinir) throw new InvalidDataException("Dosya sınırdan büyük: " + toplam);

            await using var kaynak = await yanit.Content.ReadAsStreamAsync(iptal);
            await using var cikis = new FileStream(hedef, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var tampon = new byte[81920];
            long okunan = 0;
            while (true)
            {
                int n;
                using (var adim = Adim(iptal))
                    n = await kaynak.ReadAsync(tampon, adim.Token);
                if (n <= 0) break;
                okunan += n;
                if (okunan > sinir) throw new InvalidDataException("Dosya sınırdan büyük");
                await cikis.WriteAsync(tampon.AsMemory(0, n), iptal);
                ilerleme?.Invoke(okunan, toplam);
            }
            if (okunan == 0) throw new InvalidDataException("Boş dosya: " + url);
        }
        catch (OperationCanceledException) when (!iptal.IsCancellationRequested)
        {
            throw new TimeoutException("İndirme " + DurmaSuresi.TotalSeconds + " sn boyunca ilerlemedi: " + url);
        }
    }

    private static CancellationTokenSource Adim(CancellationToken iptal)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(iptal);
        cts.CancelAfter(DurmaSuresi);
        return cts;
    }

    /// <summary>"abc…  AionMeter.zip" → "abc…" (64 küçük harf hex); geçersizse null.</summary>
    private static string? ShaCoz(string metin)
    {
        string ilk = metin.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        ilk = ilk.ToLowerInvariant();
        if (ilk.Length != 64) return null;
        foreach (char c in ilk) if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return null;
        return ilk;
    }

    private static string DosyaSha256(string yol)
    {
        using var fs = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static bool KlasorYazilabilir(string klasor)
    {
        try
        {
            string deneme = Path.Combine(klasor, ".guncelleme-denemesi." + Environment.ProcessId);
            using (new FileStream(deneme, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Kurulum (iş parçacığı havuzunda) ----------------

    private static bool Korunan(string goreli)
    {
        foreach (string k in Korunanlar)
            if (string.Equals(goreli, k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Zip-slip korumalı açma: "..", mutlak yol, sürücü harfi reddedilir; toplam boyut sınırlı.</summary>
    private static void ZipAc(string zipYol, string hedef)
    {
        string kok = Path.GetFullPath(hedef);
        string kokOnek = kok.EndsWith(Path.DirectorySeparatorChar) ? kok : kok + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(kok);
        using var zip = ZipFile.OpenRead(zipYol);
        long toplam = 0;
        foreach (var giris in zip.Entries)
        {
            string ad = giris.FullName.Replace('\\', '/'); // PowerShell 5.1 Compress-Archive '\' kullanır
            if (ad.Length == 0) continue;
            if (ad.StartsWith('/') || ad.Contains(':')) throw new InvalidDataException("Geçersiz zip girdisi: " + ad);
            foreach (string parca in ad.Split('/'))
                if (parca == "..") throw new InvalidDataException("Geçersiz zip girdisi: " + ad);

            string tam = Path.GetFullPath(Path.Combine(kok, ad.Replace('/', Path.DirectorySeparatorChar)));
            if (!tam.StartsWith(kokOnek, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Zip girdisi klasör dışına çıkıyor: " + ad);

            if (ad.EndsWith('/'))
            {
                Directory.CreateDirectory(tam);
                continue;
            }
            toplam += giris.Length;
            if (toplam > AcilmisSinir) throw new InvalidDataException("Zip açılmış boyutu sınırı aşıyor");
            Directory.CreateDirectory(Path.GetDirectoryName(tam)!);
            giris.ExtractToFile(tam, overwrite: true);
        }
    }

    // ---------------- Pencere ----------------

    /// <summary>
    /// Ortada, en üstte, overlay temasında küçük pencere. Oyun öncesi açıldığı için odak alabilir.
    /// Durum/ilerleme kodla çizilir; sürüm notları (kaydırma) ve iki düğme gerçek kontrollerdir (Enter/Esc çalışsın).
    /// </summary>
    private sealed class GuncellemeFormu : Form
    {
        private static readonly Color RenkArka = Color.FromArgb(18, 20, 26);
        private static readonly Color RenkPanel = Color.FromArgb(26, 29, 37);
        private static readonly Color RenkYazi = Color.FromArgb(222, 225, 230);
        private static readonly Color RenkSoluk = Color.FromArgb(138, 145, 156);
        private static readonly Color RenkMavi = Color.FromArgb(86, 156, 240);

        private const TextFormatFlags Sol = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        private const TextFormatFlags Orta = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                             TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        private readonly CancellationTokenSource _iptal = new();
        private readonly TextBox _notlar;
        private readonly Button _guncelle, _simdiDegil;
        private readonly SolidBrush _fircaCubukArka = new(Color.FromArgb(40, 44, 54));
        private readonly SolidBrush _fircaCubuk = new(RenkMavi);
        private readonly SolidBrush _fircaIptal = new(Color.FromArgb(52, 57, 70));
        private readonly Pen _kalemKenar = new(Color.FromArgb(70, 140, 230));
        private Font? _yazi, _yaziKalin, _yaziKucuk;
        private float _olcek = 1f;

        private string _baslik = "AionMeter", _durum = "", _ayrinti = "";
        private float _oran = -1f; // < 0: ilerleme çubuğu yok
        private bool _iptalGorunur, _soruModu, _bitti;
        private Rectangle _iptalKutusu;
        private TaskCompletionSource<bool>? _karar;

        public Sonuc Sonuc { get; private set; } = Devam;

        public GuncellemeFormu()
        {
            Text = "AionMeter güncelleme";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = RenkArka;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _notlar = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = RenkPanel, ForeColor = RenkYazi, TabStop = false, Visible = false, WordWrap = true,
            };
            _guncelle = Dugme("Güncelle", RenkMavi, Color.White);
            _simdiDegil = Dugme("Şimdi değil", Color.FromArgb(52, 57, 70), RenkYazi);
            _guncelle.Click += (_, _) => _karar?.TrySetResult(true);
            _simdiDegil.Click += (_, _) => _karar?.TrySetResult(false);
            Controls.Add(_notlar);
            Controls.Add(_guncelle);
            Controls.Add(_simdiDegil);
            AcceptButton = _guncelle;   // Enter
            CancelButton = _simdiDegil; // Esc
        }

        private static Button Dugme(string metin, Color arka, Color yazi)
        {
            var b = new Button
            {
                Text = metin, FlatStyle = FlatStyle.Flat, BackColor = arka, ForeColor = yazi, Visible = false,
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private int P(float v) => (int)MathF.Round(v * _olcek);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _olcek = Math.Max(1f, DeviceDpi / 96f);
            _yazi = new Font("Segoe UI", 12f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
            _yaziKalin = new Font("Segoe UI", 13f * _olcek, FontStyle.Bold, GraphicsUnit.Pixel);
            _yaziKucuk = new Font("Segoe UI", 11f * _olcek, FontStyle.Regular, GraphicsUnit.Pixel);
            _notlar.Font = _yazi;
            _guncelle.Font = _yaziKalin;
            _simdiDegil.Font = _yazi;
            Yerlesim();
        }

        /// <summary>Durum modunda küçük; soru modunda notlar ve düğmelerle büyür. Ekranın ortasında kalır.</summary>
        private void Yerlesim()
        {
            int w = P(440);
            int h = _soruModu ? P(380) : P(124);
            var alan = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = new Rectangle(alan.Left + (alan.Width - w) / 2, alan.Top + (alan.Height - h) / 2, w, h);
            if (_soruModu)
            {
                int pad = P(16);
                _notlar.Bounds = new Rectangle(pad, P(86), w - 2 * pad, h - P(86) - P(96));
                int dy = h - P(48);
                _simdiDegil.Bounds = new Rectangle(w - pad - P(110), dy, P(110), P(32));
                _guncelle.Bounds = new Rectangle(w - pad - P(110) - P(10) - P(110), dy, P(110), P(32));
            }
            Invalidate();
        }

        // --- akıştan çağrılan güncellemeler (UI iş parçacığı) ---

        public void Durum(string durum)
        {
            _durum = durum;
            _ayrinti = "";
            _oran = -1f;
            Invalidate();
        }

        public void Baslik(string baslik)
        {
            _baslik = baslik;
            Invalidate();
        }

        public void IptalGoster(bool goster)
        {
            _iptalGorunur = goster;
            Invalidate();
        }

        public void Ilerleme(long okunan, long? toplam)
        {
            const double Mb = 1024.0 * 1024.0;
            if (toplam is long t && t > 0)
            {
                double oran = Math.Clamp((double)okunan / t, 0, 1);
                _oran = (float)oran;
                _ayrinti = "%" + ((int)(oran * 100)).ToString(Bicim.Tr) + " · " + (okunan / Mb).ToString("0.0", Bicim.Tr) +
                           " / " + (t / Mb).ToString("0.0", Bicim.Tr) + " MB";
            }
            else
            {
                _oran = -1f;
                _ayrinti = (okunan / Mb).ToString("0.0", Bicim.Tr) + " MB";
            }
            Invalidate();
        }

        public async Task MesajAsync(string mesaj, int ms)
        {
            _iptalGorunur = false;
            Durum(mesaj);
            await Task.Delay(ms);
        }

        /// <summary>Sürüm notlarını gösterip sorar. true = Güncelle.</summary>
        public Task<bool> SorAsync(string surumMetni, string notlar)
        {
            _baslik = "Güncelleme var";
            _durum = surumMetni;
            _soruModu = true;
            _notlar.Text = "Yenilikler:\r\n\r\n" + (string.IsNullOrWhiteSpace(notlar) ? "Bu sürüm için not yok." : notlar);
            _notlar.Visible = _guncelle.Visible = _simdiDegil.Visible = true;
            Yerlesim();
            Activate(); // Enter/Esc çalışsın (oyun öncesi; odak almak serbest)
            _guncelle.Focus();
            _karar = new TaskCompletionSource<bool>();
            return _karar.Task.ContinueWith(t =>
            {
                _soruModu = false;
                _notlar.Visible = _guncelle.Visible = _simdiDegil.Visible = false;
                Yerlesim();
                return t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Sonuc = await AkisAsync(this, _iptal.Token);
            }
            catch (Exception ex)
            {
                HataLog.Yaz("Güncelleyici akışı", ex);
                Sonuc = Devam;
            }
            _bitti = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_bitti)
            {
                // Alt+F4 vb.: soruysa "Şimdi değil", indirmedeyse iptal; akış bitince pencere kapanır.
                e.Cancel = true;
                if (_karar is { Task.IsCompleted: false }) _karar.TrySetResult(false);
                else _iptal.Cancel();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_iptalGorunur && e.Button == MouseButtons.Left && _iptalKutusu.Contains(e.Location))
            {
                _iptalGorunur = false;
                _durum = "İptal ediliyor…";
                _iptal.Cancel();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Ciz(e.Graphics); }
            catch (Exception ex) { HataLog.Yaz("Güncelleme çizimi", ex); }
        }

        private void Ciz(Graphics g)
        {
            if (_yazi is null || _yaziKalin is null || _yaziKucuk is null) return;
            int w = ClientSize.Width, h = ClientSize.Height, pad = P(16);
            int gen = w - 2 * pad;
            TextRenderer.DrawText(g, _baslik, _yaziKalin, new Rectangle(pad, P(14), gen, P(22)), RenkYazi, Sol);
            TextRenderer.DrawText(g, _durum, _yazi, new Rectangle(pad, P(40), gen, P(20)), _soruModu ? RenkMavi : RenkSoluk, Sol);

            if (_soruModu)
            {
                TextRenderer.DrawText(g, "Güncellensin mi?", _yazi, new Rectangle(pad, h - P(48), gen, P(32)), RenkYazi, Sol);
            }
            else
            {
                var cubuk = new Rectangle(pad, P(70), gen, P(8));
                if (_oran >= 0)
                {
                    g.FillRectangle(_fircaCubukArka, cubuk);
                    g.FillRectangle(_fircaCubuk, cubuk.X, cubuk.Y, (int)(cubuk.Width * _oran), cubuk.Height);
                }
                if (_ayrinti.Length > 0)
                    TextRenderer.DrawText(g, _ayrinti, _yaziKucuk, new Rectangle(pad, P(84), gen - P(90), P(24)), RenkSoluk, Sol);
                if (_iptalGorunur)
                {
                    _iptalKutusu = new Rectangle(w - pad - P(80), P(84), P(80), P(26));
                    g.FillRectangle(_fircaIptal, _iptalKutusu);
                    TextRenderer.DrawText(g, "İptal", _yazi, _iptalKutusu, RenkYazi, Orta);
                }
            }
            g.DrawRectangle(_kalemKenar, 0, 0, w - 1, h - 1);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing); // önce alt kontroller (yazı tiplerini kullanıyorlar)
            if (disposing)
            {
                _iptal.Dispose();
                _fircaCubukArka.Dispose(); _fircaCubuk.Dispose(); _fircaIptal.Dispose(); _kalemKenar.Dispose();
                _yazi?.Dispose(); _yaziKalin?.Dispose(); _yaziKucuk?.Dispose();
            }
        }
    }
}
