// Ping ölçümü denetimi: sentetik, zaman damgalı TCP parçaları (istemci verisi + sunucu ACK'i) PingOlcer'e verilir; gecikme uyarısının
// histerezisi PingUyarisi ile denenir. Ağ yok, paket yok.
using AionDPS.Aion2.Capture;
using AionMeter;

internal static class PingKontrol
{
    private const long Ms = TimeSpan.TicksPerMillisecond;
    private const long T0 = 1_000_000_000_000L; // keyfi başlangıç (tick)

    public static bool Calistir()
    {
        Console.WriteLine("[ping]");
        int gecen = 0, kalan = 0;

        void Bak(string baslik, bool ok, string detay)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | {detay}");
            if (ok) { gecen++; } else { kalan++; }
        }

        // Bir istemci parçası (t ms'de) ve sunucunun onayı (rtt ms sonra). pcapKayma: Npcap saatinin varış saatine göre farkı (onayda).
        static void Tur(PingOlcer o, ref uint sira, double t, double rtt, double pcapKayma = 0, double varisGecikme = 0, int baglanti = 1)
        {
            long gonder = T0 + (long)(t * Ms);
            long onay = T0 + (long)((t + rtt) * Ms);
            o.Gozle(baglanti, sunucudan: false, sira, 40, ackVar: true, 0, gonder, gonder);
            sira += 40;
            o.Gozle(baglanti, sunucudan: true, 5000, 0, ackVar: true, sira, onay + (long)(pcapKayma * Ms), onay + (long)(varisGecikme * Ms));
        }

        static string Yaz(int? ms) => ms is int m ? m + " ms" : "null";

        // 1) Kararlı 80 ms
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 40; i++) Tur(o, ref s, i * 100, 80);
            int? p = o.Ms(T0 + 4000 * Ms);
            Bak("kararlı 80 ms", p == 80, Yaz(p));
        }

        // 2) Kötü bağlantı 320 ms: uyarı eşiği (250) aşılır
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 40; i++) Tur(o, ref s, i * 100, 320);
            int? p = o.Ms(T0 + 4400 * Ms);
            Bak("kötü bağlantı 320 ms", p == 320 && PingUyarisi.Gorunsun(false, p, 250), Yaz(p));
        }

        // 3) Tek aykırı örnek (900 ms) alarm vermez
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 20; i++) Tur(o, ref s, i * 100, i == 10 ? 900 : 60);
            int? p = o.Ms(T0 + 2950 * Ms);
            Bak("tek aykırı örnek alarm vermez", p == 60 && !PingUyarisi.Gorunsun(false, p, 250), Yaz(p));
        }

        // 4) Sunucunun gecikmeli ACK'i: örneklerin yarısı +200 ms (60 / 260): alt çeyrek gerçek gecikmede kalır
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 40; i++) Tur(o, ref s, i * 100, i % 2 == 0 ? 60 : 260);
            int? p = o.Ms(T0 + 4300 * Ms);
            Bak("gecikmeli ACK yarı yarıya: alarm yok", p == 60 && !PingUyarisi.Gorunsun(false, p, 250), Yaz(p));
        }

        // 5) Karn kuralı: yeniden gönderilen parçanın onayı örnek vermez (ilk gönderimden ölçülse ~400 ms çıkardı)
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 10; i++) Tur(o, ref s, i * 100, 50);
            long t = T0 + 1000 * Ms;
            o.Gozle(1, false, s, 40, true, 0, t, t);                           // gönderildi, kayboldu
            o.Gozle(1, false, s, 40, true, 0, t + 350 * Ms, t + 350 * Ms);     // aynı baytlar yeniden
            o.Gozle(1, true, 5000, 0, true, s + 40, t + 400 * Ms, t + 400 * Ms);
            (long ornek, _, long yeniden) = o.Sayaclar();
            int? p = o.Ms(T0 + 1500 * Ms);
            Bak("Karn: yeniden gönderim örnek vermez", ornek == 10 && yeniden == 1 && p == 50, $"{Yaz(p)}, örnek {ornek}, yeniden {yeniden}");
        }

        // 6) Arayüz geç okudu (varış +300 ms) ama Npcap zamanı doğru: kısa olan (Npcap) alınır
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 20; i++) Tur(o, ref s, i * 100, 70, varisGecikme: 300);
            int? p = o.Ms(T0 + 2400 * Ms);
            Bak("teslim gecikmesi süreyi uzatmaz", p == 70, Yaz(p));
        }

        // 7) Npcap zaman damgası bozuk (ters gidiyor): ölçüm yok, yanlış alarm yok
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 20; i++) Tur(o, ref s, i * 100, 70, pcapKayma: i % 3 == 0 ? -500 : 0);
            int? p = o.Ms(T0 + 2100 * Ms);
            Bak("bozuk zaman damgası (geri): null", p is null, Yaz(p));
        }

        // 8) Npcap zaman damgası varış farkından çok uzun (ör. 15 ms çözünürlük değil, kaymış saat): bozuk, null
        {
            var o = new PingOlcer();
            uint s = 1000;
            for (int i = 0; i < 20; i++) Tur(o, ref s, i * 100, 70, pcapKayma: 400);
            int? p = o.Ms(T0 + 2100 * Ms);
            Bak("bozuk zaman damgası (ileri): null", p is null && !PingUyarisi.Gorunsun(false, p, 250), Yaz(p));
        }

        // 9) Az örnek ve eski örnek: null
        {
            var o = new PingOlcer();
            uint s = 1000;
            Tur(o, ref s, 0, 300);
            Tur(o, ref s, 100, 300);
            int? az = o.Ms(T0 + 500 * Ms);
            for (int i = 2; i < 20; i++) Tur(o, ref s, i * 100, 300);
            int? eski = o.Ms(T0 + 9000 * Ms); // son örnekten 7 sn sonra
            Bak("2 örnek / 5 sn'den eski: null", az is null && eski is null, $"{Yaz(az)}, {Yaz(eski)}");
        }

        // 10) Sıra numarası taşması (uint) ve iki bağlantı birbirine karışmaz
        {
            var o = new PingOlcer();
            uint a = uint.MaxValue - 200, b = 7;
            for (int i = 0; i < 20; i++)
            {
                Tur(o, ref a, i * 100, 90, baglanti: 1);
                Tur(o, ref b, i * 100 + 50, 90, baglanti: 2);
            }
            (long ornek, long bozuk, _) = o.Sayaclar();
            int? p = o.Ms(T0 + 2200 * Ms);
            Bak("sıra taşması + iki bağlantı", p == 90 && ornek == 40 && bozuk == 0, $"{Yaz(p)}, örnek {ornek}");
        }

        // 11) Bir onay birden çok parçayı kapsar: en yenisinden ölçülür
        {
            var o = new PingOlcer();
            for (int i = 0; i < 10; i++)
            {
                long t = T0 + i * 200 * Ms;
                uint s = (uint)(1000 + i * 120);
                o.Gozle(1, false, s, 40, true, 0, t, t);
                o.Gozle(1, false, s + 40, 40, true, 0, t + 30 * Ms, t + 30 * Ms);
                o.Gozle(1, false, s + 80, 40, true, 0, t + 60 * Ms, t + 60 * Ms);
                o.Gozle(1, true, 5000, 0, true, s + 120, t + 160 * Ms, t + 160 * Ms);
            }
            int? p = o.Ms(T0 + 2000 * Ms);
            Bak("toplu onay: en yeni parçadan", p == 100, Yaz(p));
        }

        // 12) Histerezis: 250'de görünür, 200'ün altında kaybolur, null'da kaybolur
        {
            bool g = false;
            var adimlar = new (int? Ms, bool Beklenen)[] { (240, false), (260, true), (230, true), (205, true), (199, false), (240, false), (320, true), (null, false) };
            bool hepsi = true;
            foreach ((int? ms, bool beklenen) in adimlar)
            {
                g = PingUyarisi.Gorunsun(g, ms, PingUyarisi.VarsayilanEsik);
                hepsi &= g == beklenen;
            }
            Bak("histerezis 250 / 200", hepsi && PingUyarisi.Kalkis(400) == 320 && PingUyarisi.Kalkis(150) == 120, $"kalkış {PingUyarisi.Kalkis(250)}");
        }

        Console.WriteLine($"  ping: geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }
}
