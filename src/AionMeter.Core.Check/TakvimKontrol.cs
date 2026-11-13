// Etkinlik takvimi denetimi: bölge saatleri, yaz saati geçişleri, gece yarısı, giriş penceresi ve boss "şimdi" süresi.
using AionMeter.Core;

internal static class TakvimKontrol
{
    public static bool Calistir()
    {
        Console.WriteLine("[etkinlik takvimi]");
        int gecen = 0, kalan = 0;

        static DateTime U(int y, int a, int g, int s, int d, int sn = 0) => new(y, a, g, s, d, sn, DateTimeKind.Utc);

        void Bak(string baslik, bool ok, string ayrinti)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | {ayrinti}");
            if (ok) { gecen++; } else { kalan++; }
        }

        void Sonraki(EtkinlikTakvimi t, string ad, DateTime simdi, DateTime beklenen, string? baslik = null)
        {
            var k = Kalem(t, ad);
            DateTime bulunan = t.Sonraki(k, simdi).BaslangicUtc;
            Bak(baslik ?? $"{t.Bolge} {ad}", bulunan == beklenen && bulunan.Kind == DateTimeKind.Utc,
                $"{simdi:yyyy-MM-dd HH:mm:ss}Z → {bulunan:yyyy-MM-dd HH:mm}Z (beklenen {beklenen:yyyy-MM-dd HH:mm}Z)");
        }

        var avrupa = new EtkinlikTakvimi();
        Bak("varsayılan bölge Avrupa (W. Europe Standard Time)", avrupa.Bolge == SunucuBolgesi.Avrupa
            && avrupa.SaatDilimi.Id == "W. Europe Standard Time", avrupa.SaatDilimi.Id);

        // Referans: 2026-11-13 17:18 UTC, Cuma (Berlin kış saati, UTC+1 → 18:18).
        DateTime r = U(2026, 11, 13, 17, 18);
        Sonraki(avrupa, "Dimensional Invasion", r, U(2026, 11, 13, 17, 30));
        Sonraki(avrupa, "Shugo Festival", r, U(2026, 11, 13, 18, 0));
        Sonraki(avrupa, "Watcher Kaira", r, U(2026, 11, 13, 18, 0));
        Sonraki(avrupa, "Spacetime Rift", r, U(2026, 11, 13, 19, 0));
        Sonraki(avrupa, "Guardian Lord Nahma", r, U(2026, 11, 13, 20, 0), "Avrupa Nahma: Cuma 21:00 Berlin");
        Sonraki(avrupa, "Argo, the Spirit King", r, U(2026, 11, 13, 21, 30));
        Sonraki(avrupa, "Artifact Siege", r, U(2026, 11, 14, 20, 0), "Avrupa Artifact Siege: Cumartesi 21:00 Berlin");
        Sonraki(avrupa, "Siege Bosses", r, U(2026, 11, 14, 20, 30));
        Sonraki(avrupa, "Middle Reshanta Siege Bosses", r, U(2026, 11, 14, 20, 30));
        Sonraki(avrupa, "Middle Reshanta Siege Bosses", U(2026, 11, 14, 20, 30), U(2026, 11, 16, 20, 30), "Middle Reshanta: Cumartesiden sonra Pazartesi");
        Sonraki(avrupa, "Shugo Festival", U(2026, 11, 13, 18, 0), U(2026, 11, 13, 19, 0), "tam başlangıç anında sonraki bir sonrakidir");

        // Gece yarısı: yerel gün ve UTC günü geçişleri.
        Sonraki(avrupa, "Shugo Festival", U(2026, 11, 13, 22, 50), U(2026, 11, 13, 23, 0), "Shugo: Berlin gece yarısı (00:00 yerel = 23:00Z)");
        Sonraki(avrupa, "Dimensional Invasion", U(2026, 11, 13, 23, 45), U(2026, 11, 14, 0, 30), "Invasion: UTC gece yarısını geçer");
        Sonraki(avrupa, "Spacetime Rift", U(2026, 11, 13, 22, 10), U(2026, 11, 14, 1, 0), "Rift: 23:00'ten sonra 02:00 (ertesi gün)");
        Sonraki(avrupa, "Argo, the Spirit King", U(2026, 11, 13, 21, 40), U(2026, 11, 14, 9, 30), "Argo: 22:30'dan sonra ertesi 10:30");

        // Yaz saati (Berlin): 2026-03-29 Pazar 02:00 → 03:00, 2026-10-25 Pazar 03:00 → 02:00.
        Sonraki(avrupa, "Argo, the Spirit King", U(2026, 3, 28, 8, 0), U(2026, 3, 28, 9, 30), "Mart geçişi öncesi Argo 10:30 CET");
        Sonraki(avrupa, "Argo, the Spirit King", U(2026, 3, 29, 7, 0), U(2026, 3, 29, 8, 30), "Mart geçişi sonrası Argo 10:30 CEST");
        Sonraki(avrupa, "Shugo Festival", U(2026, 3, 29, 0, 30), U(2026, 3, 29, 1, 0), "olmayan 02:00 ileri kayar (03:00 CEST = 01:00Z)");
        Sonraki(avrupa, "Shugo Festival", U(2026, 3, 29, 1, 0), U(2026, 3, 29, 2, 0), "ileri kayan saat tekrarlanmaz (sonra 04:00 CEST)");
        Sonraki(avrupa, "Spacetime Rift", U(2026, 3, 28, 22, 30), U(2026, 3, 29, 1, 0), "Rift olmayan 02:00 → 03:00 CEST");
        Sonraki(avrupa, "Spacetime Rift", U(2026, 3, 29, 1, 0), U(2026, 3, 29, 3, 0), "Rift sonra 05:00 CEST");
        Sonraki(avrupa, "Argo, the Spirit King", U(2026, 10, 24, 7, 0), U(2026, 10, 24, 8, 30), "Ekim geçişi öncesi Argo 10:30 CEST");
        Sonraki(avrupa, "Argo, the Spirit King", U(2026, 10, 25, 7, 0), U(2026, 10, 25, 9, 30), "Ekim geçişi sonrası Argo 10:30 CET");
        Sonraki(avrupa, "Shugo Festival", U(2026, 10, 24, 23, 30), U(2026, 10, 25, 0, 0), "iki kez yaşanan 02:00 ilk anıyla (CEST = 00:00Z)");
        Sonraki(avrupa, "Shugo Festival", U(2026, 10, 25, 0, 0), U(2026, 10, 25, 2, 0), "sonra 03:00 CET = 02:00Z (02:00 ikinci kez yok)");
        Sonraki(avrupa, "Guardian Lord Nahma", U(2026, 10, 23, 12, 0), U(2026, 10, 23, 19, 0), "Nahma Cuma yaz saatinde 19:00Z");
        Sonraki(avrupa, "Guardian Lord Nahma", U(2026, 10, 24, 12, 0), U(2026, 10, 25, 20, 0), "Nahma Pazar kış saatinde 20:00Z");

        // Yaklaşanlar penceresi (10 dk) ve giriş penceresi.
        var liste = new List<TakvimOlayi>();
        var on = TimeSpan.FromMinutes(10);
        avrupa.Yaklasanlar(r, on, liste);
        Bak("17:18Z'de 10 dk içinde bir şey yok", liste.Count == 0, Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 17, 21), on, liste);
        Bak("17:21Z: Invasion 9 dk kala listede", liste.Count == 1 && liste[0].Ad == "Dimensional Invasion" && !liste[0].BasladiMi(U(2026, 11, 13, 17, 21)), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 17, 55), on, liste);
        Bak("17:55Z: Shugo ve Kaira (eşit saatte takvim sırası)", liste.Count == 2 && liste[0].Ad == "Shugo Festival" && liste[1].Ad == "Watcher Kaira", Adlar(liste));

        DateTime iki = U(2026, 11, 13, 18, 2);
        avrupa.Yaklasanlar(iki, on, liste);
        var shugo = liste.Find(o => o.Ad == "Shugo Festival");
        Bak("Shugo başladıktan 2 dk sonra girişi açık", shugo.Kalem is not null && shugo.BasladiMi(iki)
            && shugo.GirisBitisUtc == U(2026, 11, 13, 18, 3) && shugo.GirisBitisUtc - iki == TimeSpan.FromMinutes(1), Adlar(liste));
        Bak("Kaira doğuştan 2 dk sonra listede değil", !liste.Exists(o => o.Ad == "Watcher Kaira"), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 18, 3), on, liste);
        Bak("Shugo 3 dk sonra listede yok", !liste.Exists(o => o.Ad == "Shugo Festival"), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 18, 0, 59), on, liste);
        Bak("Kaira doğuştan 59 sn sonra \"şimdi\"", liste.Exists(o => o.Ad == "Watcher Kaira" && o.BasladiMi(U(2026, 11, 13, 18, 0, 59))
            && o.GirisBitisUtc is null), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 18, 1), on, liste);
        Bak("Kaira 60 sn sonra listeden düşer", !liste.Exists(o => o.Ad == "Watcher Kaira"), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 13, 19, 9), on, liste);
        var rift = liste.Find(o => o.Ad == "Spacetime Rift");
        Bak("Rift girişi 10 dk: 9. dakikada açık", rift.Kalem is not null && rift.GirisBitisUtc == U(2026, 11, 13, 19, 10), Adlar(liste));
        avrupa.Yaklasanlar(U(2026, 11, 14, 20, 25), on, liste);
        Bak("Cumartesi 20:25Z: Siege açık, iki kuşatma bossu yaklaşıyor", liste.Exists(o => o.Ad == "Artifact Siege" && o.GirisBitisUtc == U(2026, 11, 14, 20, 30))
            && liste.Exists(o => o.Ad == "Siege Bosses") && liste.Exists(o => o.Ad == "Middle Reshanta Siege Bosses"), Adlar(liste));
        var g = avrupa.Guncel(Kalem(avrupa, "Watcher Kaira"), U(2026, 11, 13, 18, 0, 30));
        Bak("Guncel: doğmuş boss 60 sn içinde aynı olay", g.BaslangicUtc == U(2026, 11, 13, 18, 0), $"{g.BaslangicUtc:HH:mm}Z");

        // Önbellek: geri giden sorgu da doğru sonuç verir.
        Sonraki(avrupa, "Shugo Festival", U(2026, 11, 13, 10, 10), U(2026, 11, 13, 11, 0), "önbellek: geçmişe sorgu");

        // NA Batı (Pacific): Kasımda PST (UTC−8), Temmuzda PDT (UTC−7). 17:18Z = 09:18 PST.
        var naBati = new EtkinlikTakvimi(SunucuBolgesi.NaBati);
        Sonraki(naBati, "Argo, the Spirit King", r, U(2026, 11, 13, 18, 30), "NA Batı Argo 10:30 PST");
        Sonraki(naBati, "Spacetime Rift", r, U(2026, 11, 13, 19, 0), "NA Batı Rift 11:00 PST");
        Sonraki(naBati, "Guardian Lord Nahma", r, U(2026, 11, 14, 5, 0), "NA Batı Nahma Cuma 21:00 PST = Cumartesi 05:00Z");
        Sonraki(naBati, "Argo, the Spirit King", U(2026, 7, 1, 17, 0), U(2026, 7, 1, 17, 30), "NA Batı yaz saati Argo 10:30 PDT");

        var naDogu = new EtkinlikTakvimi(SunucuBolgesi.NaDogu);
        Sonraki(naDogu, "Artifact Siege", r, U(2026, 11, 15, 2, 0), "NA Doğu Siege Cumartesi 21:00 EST = Pazar 02:00Z");
        var latam = new EtkinlikTakvimi(SunucuBolgesi.LatinAmerika);
        Sonraki(latam, "Guardian Lord Nahma", r, U(2026, 11, 14, 0, 0), "Latin Amerika Nahma Cuma 21:00 BRT = 00:00Z");
        var asya = new EtkinlikTakvimi(SunucuBolgesi.Asya);
        Sonraki(asya, "Argo, the Spirit King", r, U(2026, 11, 14, 1, 30), "Asya Argo 10:30 JST = 01:30Z");

        // Maliyet: 10.000 sorgu (5 Hz'de ~33 dk) ve ayırma.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long once = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) avrupa.Yaklasanlar(r.AddMilliseconds(i * 200), on, liste);
        long ayrilan = GC.GetAllocatedBytesForCurrentThread() - once;
        sw.Stop();
        Bak("10.000 Yaklasanlar çağrısı", sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms, {ayrilan / 1024} KB ayırma");

        Console.WriteLine($"  takvim: geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }

    private static TakvimKalemi Kalem(EtkinlikTakvimi t, string ad)
    {
        foreach (var k in t.Kalemler)
            if (k.Ad == ad) return k;
        throw new InvalidOperationException("Takvimde yok: " + ad);
    }

    private static string Adlar(List<TakvimOlayi> l) =>
        l.Count == 0 ? "(boş)" : string.Join(", ", l.ConvertAll(o => $"{o.Ad} {o.BaslangicUtc:HH:mm}Z"));
}
