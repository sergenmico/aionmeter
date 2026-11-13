// Favori boss anahtarları (ayar satırına yazılıp geri okunması) ve "doğmadan 5 dk önce, her doğuşta bir kez ses" kuralı;
// güncellemeden sonraki Yenilikler kartının içerik ve gösterim kararı.
using AionMeter.Core;

internal static class FavoriKontrol
{
    public static bool Calistir(string assets)
    {
        Console.WriteLine("[favori boss ve yenilikler]");
        int gecen = 0, kalan = 0;

        void Bak(string baslik, bool ok, string? ayrinti = null)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik}{(ayrinti is null ? "" : " | " + ayrinti)}");
            if (ok) { gecen++; } else { kalan++; }
        }

        static DateTime U(int s, int d, int sn = 0) => new(2026, 11, 14, s, d, sn, DateTimeKind.Utc);

        // ---- Anahtarlar ve kalıcılık ----
        string kaira = FavoriBoss.TakvimAnahtari("Watcher Kaira");
        string argo = FavoriBoss.TakvimAnahtari("Argo, the Spirit King"); // virgüllü ad
        string kusan = FavoriBoss.SahaAnahtari(101004);
        Bak("takvim anahtarı adla", kaira == "t:Watcher Kaira", kaira);
        Bak("saha anahtarı yer kimliğiyle (kültürden bağımsız)", kusan == "y:101004", kusan);

        var favoriler = new HashSet<string>(StringComparer.Ordinal) { kusan, argo, kaira };
        string satir = FavoriBoss.Yaz(favoriler);
        Bak("ayar satırı sıralı ve '|' ile", satir == "t:Argo, the Spirit King|t:Watcher Kaira|y:101004", satir);
        var okunan = new HashSet<string>(StringComparer.Ordinal);
        FavoriBoss.Oku(satir, okunan);
        Bak("yaz → oku aynı küme", okunan.SetEquals(favoriler), string.Join(" ; ", okunan));
        FavoriBoss.Oku(" y:101004 | t:Watcher Kaira |  | y:abc | x:1 | t: | y:-5 | y:101004 ", okunan);
        Bak("bozuk parçalar atlanır, tekrar teke iner", okunan.Count == 2 && okunan.Contains(kusan) && okunan.Contains(kaira),
            string.Join(" ; ", okunan));
        FavoriBoss.Oku("", okunan);
        Bak("boş satır: favori yok", okunan.Count == 0);
        Bak("geçersiz anahtar yazılmaz", FavoriBoss.Yaz(new[] { "y:12", "t:a|b", "z:1" }) == "y:12");

        // ---- Her doğuşta bir kez ses ----
        var zil = new FavoriZili();
        DateTime dogus = U(18, 0);
        Bak("6 dk kala çalmaz", !zil.Denetle(kaira, dogus, U(17, 54)));
        Bak("5 dk kala çalar", zil.Denetle(kaira, dogus, U(17, 55)));
        Bak("aynı doğuş için 1 sn sonra çalmaz", !zil.Denetle(kaira, dogus, U(17, 55, 1)));
        Bak("aynı doğuş için 4 dk sonra çalmaz", !zil.Denetle(kaira, dogus, U(17, 59)));
        Bak("doğuş anında / sonra çalmaz", !zil.Denetle(kaira, dogus, U(18, 0)) && !zil.Denetle(kaira, dogus, U(18, 0, 30)));
        Bak("başka favori kendi doğuşunda çalar", zil.Denetle(kusan, U(17, 58), U(17, 55)));
        Bak("saha doğuşu yeni bilgiyle 40 sn kayınca yine çalmaz", !zil.Denetle(kusan, U(17, 58, 40), U(17, 56)));
        zil.Temizle(U(18, 20));
        Bak("geçmiş doğuşların kaydı temizlenir", zil.Sayi == 0, zil.Sayi.ToString());
        Bak("sonraki doğuş (3 sa sonra) yeniden çalar", zil.Denetle(kaira, U(21, 0), U(20, 56)));
        Bak("sayaç doğuşa 2 dk kala açıldıysa bir kez çalar", new FavoriZili().Denetle(argo, U(10, 30), U(10, 28)));

        // ---- Yenilikler ----
        const string ornek = "; açıklama\n## 0.7.0\n- Yedi bir\n- Yedi iki\n  devamı\n## v0.6.0\n- Altı bir\n## bozuk\n- yok sayılır\n## 0.5.1\n\n## 0.4.0\n- Dört\n";
        var hepsi = YenilikNotlari.Coz(ornek);
        Bak("bölümler en yeni önde, boş ve bozuk başlıklılar atlanır",
            hepsi.Count == 3 && hepsi[0].Surum == new Version(0, 7, 0) && hepsi[1].Surum == new Version(0, 6, 0) && hepsi[2].Surum == new Version(0, 4, 0),
            string.Join(", ", hepsi.Select(b => b.Surum)));
        Bak("devam satırı önceki maddeye eklenir", hepsi[0].Maddeler.Count == 2 && hepsi[0].Maddeler[1] == "Yedi iki devamı", hepsi[0].Maddeler[1]);

        var v060 = new Version(0, 6, 0);
        var k1 = YenilikNotlari.AcilisKarari(null, ilkKurulum: true, v060);
        Bak("ilk kurulumda kart yok", !k1.Goster);
        var k2 = YenilikNotlari.AcilisKarari(null, ilkKurulum: false, v060);
        Bak("eski sürümden gelindi (sürüm yazılı değil): şimdiki sürümünkiler", k2.Goster && k2.Onceki is null
            && YenilikNotlari.Secilenler(hepsi, k2.Onceki, v060) is [{ Surum: var s2 }] && s2 == v060);
        var k3 = YenilikNotlari.AcilisKarari("0.6.0", ilkKurulum: false, v060);
        Bak("aynı sürüm yeniden açıldı: kart yok", !k3.Goster);
        var k4 = YenilikNotlari.AcilisKarari("0.4.0", ilkKurulum: false, new Version(0, 7, 0));
        var s4 = YenilikNotlari.Secilenler(hepsi, k4.Onceki, new Version(0, 7, 0));
        Bak("birkaç sürüm atlandı: aradakilerin hepsi, en yeni önde", k4.Goster && s4.Count == 2 && s4[0].Surum.Minor == 7 && s4[1].Surum.Minor == 6,
            string.Join(", ", s4.Select(b => b.Surum)));
        var k5 = YenilikNotlari.AcilisKarari("0.9.0", ilkKurulum: false, v060);
        Bak("eski sürüme dönüldü: kart yok", !k5.Goster);
        Bak("şimdiki sürümün notu yoksa boş (kart açılmaz)", YenilikNotlari.Secilenler(hepsi, new Version(0, 6, 0), new Version(0, 6, 5)).Count == 0);

        // Pakete giren gerçek dosya (assets/yenilikler.txt)
        string yol = Path.Combine(assets, "..", "yenilikler.txt");
        var gercek = File.Exists(yol) ? YenilikNotlari.Coz(File.ReadAllText(yol)) : new List<YenilikBolumu>();
        var v6 = gercek.FirstOrDefault(b => b.Surum == v060);
        Bak("assets/yenilikler.txt: v0.6.0 bölümü var, en çok 8 madde", v6 is { Maddeler.Count: > 0 and <= 8 }, v6 is null ? "yok" : v6.Maddeler.Count + " madde");
        Bak("ilk madde Pencereli tam ekran", v6 is not null && v6.Maddeler[0].Contains("Pencereli tam ekran", StringComparison.Ordinal),
            v6?.Maddeler[0]);

        Console.WriteLine($"  favori/yenilikler: geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }
}
