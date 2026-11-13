// Saha bossu listesi (0x0191) denetimi: sentetik çerçeveler gerçek opcodes.json ve saha_bosslari.json ile çözücüden geçirilir, ayrı bir depoya bakılır.
// Çerçeve düzeni referans çözücüye göredir (SkeeveAN/Aion-DPS-Meter, MIT, Client/Aion2/Aion2FieldBosses.cs TryParse/ReadHeader).
using System.Buffers.Binary;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;
using AionMeter.Core;

internal static class SahaBossKontrol
{
    public static bool Calistir(string assets)
    {
        Console.WriteLine("[saha bossları]");
        Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));
        var depo = new SahaBossDeposu();
        var decoder = new Aion2FrameDecoder(protocol, new Aion2EntityDirectory()) { SahaBosslari = depo };
        DateTime t0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var okunan = new List<SahaBossDurumu>();
        int gecen = 0, kalan = 0;

        void Ver(byte[] frame, DateTime at) => decoder.Decode(frame, at, _ => { }, _ => { });

        void Bak(string baslik, bool ok, string ayrinti)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | {ayrinti}");
            if (ok) { gecen++; } else { kalan++; }
        }

        SahaBossDurumu? Bul(int yer)
        {
            foreach (var d in okunan)
                if (d.Yer == yer) return d;
            return null;
        }

        string Ozet() => string.Join(", ", okunan.Select(d => $"{d.Yer}:{(d.Canli ? "canlı" : "ölü")}@{(d.ZamanUtc == DateTime.MinValue ? "-" : d.ZamanUtc.ToString("HH:mm"))}"));

        Bak("katalog yüklendi (86 boss)", depo.KatalogBoyutu == 86, $"{depo.KatalogBoyutu} kayıt");

        // 1) Verteron: iki canlı + bir ölü.
        long gorundu = Ms(t0.AddMinutes(-20)), geriGelir = Ms(t0.AddMinutes(25));
        Ver(Liste(1010, Canli(101001, 100f, 200f, gorundu), Canli(101002, 300f, 400f, 0), Olu(101003, geriGelir)), t0);
        depo.Oku(t0, okunan);
        Bak("iki canlı + bir ölü", okunan.Count == 3 && Bul(101001) is { Canli: true } a && a.ZamanUtc == t0.AddMinutes(-20)
            && Bul(101002) is { Canli: true, ZamanUtc: var z2 } && z2 == DateTime.MinValue
            && Bul(101003) is { Canli: false } c && c.ZamanUtc == t0.AddMinutes(25) && c.GorulduUtc == t0, Ozet());
        Bak("ad ve harita", Bul(101001) is { GorunenAd: "Neikel of the East", Harita: "Verteron" }, $"{Bul(101001)?.GorunenAd} / {Bul(101001)?.Harita}");

        // 2) Eltnen: 1 bayt boşluklu ölü kayıt, ardından canlı kayıt; sondaki sıfırlar.
        Ver(Liste(1011, Olu(101101, Ms(t0.AddHours(2)), bosluk: true), Canli(101102, 1f, 2f, Ms(t0.AddMinutes(-5)))), t0);
        depo.Oku(t0, okunan);
        Bak("1 bayt boşluklu kayıt", Bul(101101) is { Canli: false } e && e.ZamanUtc == t0.AddHours(2) && Bul(101102) is { Canli: true }, Ozet());
        var dogrudan = new List<SahaBossKaydi>();
        bool dogrudanOk = Aion2SahaBosslari.TryParse(Liste(1011, Olu(101101, Ms(t0.AddHours(2)), bosluk: true), Olu(101103, 0, bosluk: true)), out int dh, dogrudan);
        Bak("boşluklu iki kayıt, ikincinin zamanı 0", dogrudanOk && dh == 1011 && dogrudan.Count == 2 && dogrudan[1] is { Yer: 101103, ZamanMs: 0 },
            $"ok={dogrudanOk} harita={dh} adet={dogrudan.Count}");

        // 3) Bozuk / eksik çerçeveler: hiçbiri depoya yazılmaz.
        int once = depo.Sayi;
        byte[] tam = Liste(1110, Olu(111001, Ms(t0.AddHours(1))), Canli(111002, 5f, 6f, 0));
        var bozuklar = new (string Ad, byte[] Cerceve)[]
        {
            ("yarıda kesik", tam[..(tam.Length - 10)]),
            ("canlı baytı 2", Degistir(tam, 9, 2)),
            ("adet 0", Degistir(tam, 8, 0)),
            ("adet fazla (3, 2 kayıt var)", Degistir(tam, 8, 3)),
            ("u16 sıfır değil", Degistir(tam, 2, 1)),
            ("sonda sıfır olmayan bayt", [.. tam, 0x55]),
            ("zaman aralık dışı", Liste(1110, Olu(111003, 12345))),
            ("başka haritanın yeri (101001, harita 1110)", Liste(1110, Olu(101001, Ms(t0.AddHours(1))))),
            ("çok kısa", new byte[] { 0x01, 0x91, 0x00, 0x00, 0x56 }),
        };
        foreach (var (ad, cerceve) in bozuklar)
        {
            bool okundu = Aion2SahaBosslari.TryParse(cerceve, out _, dogrudan);
            Ver(cerceve, t0);
            Bak("reddedildi: " + ad, !okundu && dogrudan.Count == 0 && depo.Sayi == once, $"okundu={okundu}, depo {depo.Sayi} (önce {once})");
        }

        // 4) Bilinmeyen yer kimliği (haritanın aralığında ama katalogda yok): çerçeve okunur, kayıt tutulmaz.
        Ver(Liste(1010, Olu(101030, Ms(t0.AddHours(1))), Olu(101004, Ms(t0.AddHours(3)))), t0);
        depo.Oku(t0, okunan);
        Bak("bilinmeyen yer tutulmaz, yanındaki bilinen tutulur", Bul(101030) is null && Bul(101004) is { Canli: false } && !depo.Biliniyor(101030), Ozet());

        // 5) Lower Reshanta (harita 20, yer 2001): referansın 1000 alt sınırı burada geçerli değil.
        Ver(Liste(20, Olu(2001, Ms(t0.AddMinutes(8))), Canli(2002, -1500f, 2500f, Ms(t0.AddMinutes(-1)))), t0);
        depo.Oku(t0, okunan);
        Bak("harita 20: 2001 ölü, 2002 canlı", Bul(2001) is { Canli: false } r && r.ZamanUtc == t0.AddMinutes(8) && Bul(2002) is { Canli: true }, Ozet());
        Bak("takvimle aynı ad harita adıyla ayrılır", Bul(2001)?.GorunenAd == "Argo, the Spirit King · Lower Reshanta"
            && Bul(2002)?.GorunenAd == "Watcher Kaira · Lower Reshanta", $"{Bul(2001)?.GorunenAd} / {Bul(2002)?.GorunenAd}");
        Ver(Liste(1110, Olu(111022, Ms(t0.AddHours(4)))), t0);
        depo.Oku(t0, okunan);
        Bak("iki haritadaki eş ad ayrılır (Lagta)", Bul(111022)?.GorunenAd == "High Commander Lagta · Altgard", $"{Bul(111022)?.GorunenAd}");

        // 6) Başka opcode'lu aynı gövde (0x0192, 0x9101) eşleşmez.
        once = depo.Sayi;
        byte[] benzer = Liste(1111, Olu(111101, Ms(t0.AddHours(1))));
        Ver(Degistir(Degistir(benzer, 0, 0x01), 1, 0x92), t0);
        Ver(Degistir(Degistir(benzer, 0, 0x91), 1, 0x01), t0);
        Bak("başka opcode'lu benzer çerçeve yazılmaz", depo.Sayi == once, $"depo {depo.Sayi} (önce {once})");
        Ver(benzer, t0);
        Bak("aynı gövde 0x0191 ile yazılır", depo.Sayi == once + 1, $"depo {depo.Sayi}");

        // 7) Güncelleme: ölü boss canlı geldi.
        Ver(Liste(1010, Canli(101003, 7f, 8f, Ms(t0.AddMinutes(26)))), t0.AddMinutes(30));
        depo.Oku(t0.AddMinutes(30), okunan);
        Bak("yeni liste eski durumu değiştirir", Bul(101003) is { Canli: true } g && g.GorulduUtc == t0.AddMinutes(30), Ozet());

        // 8) Bayat bilgiler: geri gelme anının üstünden 12 saat geçen ölü kayıt ve 24 saattir yenilenmeyen bilgi silinir.
        depo.Oku(t0.AddHours(12).AddMinutes(30), okunan);
        Bak("12 sa önce geri gelmiş ölü kayıt kalmaz (2001, geri gelme 12:08)", Bul(2001) is null && Bul(111101) is not null && Bul(101101) is not null, Ozet());
        depo.Oku(t0.AddHours(24).AddMinutes(1), okunan);
        Bak("24 saatten eski bilgi kalmaz", okunan.Count == 1 && Bul(101003) is not null, Ozet());
        depo.Oku(t0.AddHours(25), okunan);
        Bak("hepsi eskidi", okunan.Count == 0 && depo.Sayi == 0, Ozet());

        // 9) Kalıcılık (ayrı depolarla; kapanışta yazmazlar): yaz, yeni depoya yükle.
        string dosya = Path.Combine(Path.GetTempPath(), "aionmeter-saha-denetim-" + Environment.ProcessId + ".json");
        try
        {
            File.Delete(dosya);
            var k1 = new SahaBossDeposu();
            k1.KaliciYap(dosya, t0, kapanistaYaz: false);
            k1.Guncelle(1010, new[] { new SahaBossKaydi(101001, false, 0, 0, Ms(t0.AddHours(1))), new SahaBossKaydi(101002, true, 1, 2, Ms(t0.AddMinutes(-3))) }, t0);
            k1.Guncelle(20, new[] { new SahaBossKaydi(2002, true, 1, 2, 0) }, t0);
            k1.Oku(t0, okunan); // değişti + daha önce yazılmadı: hemen yazar
            var k2 = new SahaBossDeposu();
            k2.KaliciYap(dosya, t0, kapanistaYaz: false);
            var okunan2 = new List<SahaBossDurumu>();
            k2.Oku(t0, okunan2);
            Bak("dosyaya yazılır ve geri yüklenir", File.Exists(dosya) && okunan.Count == 3 && okunan2.SequenceEqual(okunan), $"{okunan.Count} → {okunan2.Count}");

            // Bir dakikadan önce ikinci yazım olmaz.
            k1.Guncelle(1010, new[] { new SahaBossKaydi(101005, false, 0, 0, Ms(t0.AddHours(5))) }, t0);
            k1.Oku(t0.AddSeconds(10), okunan);
            bool erken = File.ReadAllText(dosya).Contains("101005");
            k1.Oku(t0.AddSeconds(61), okunan);
            bool sonra = File.ReadAllText(dosya).Contains("101005");
            Bak("en çok dakikada bir yazılır", !erken && sonra, $"10 sn sonra yazıldı={erken}, 61 sn sonra yazıldı={sonra}");

            // Aynı liste yeniden gelince (10 dk dolmadan) değişiklik sayılmaz: dosya yazılmaz.
            DateTime yazim = File.GetLastWriteTimeUtc(dosya);
            k1.Guncelle(1010, new[] { new SahaBossKaydi(101005, false, 0, 0, Ms(t0.AddHours(5))) }, t0.AddMinutes(2));
            k1.Oku(t0.AddMinutes(5), okunan);
            Bak("değişmeyen liste dosyayı yazdırmaz", File.GetLastWriteTimeUtc(dosya) == yazim, $"{File.GetLastWriteTimeUtc(dosya):HH:mm:ss.fff} / {yazim:HH:mm:ss.fff}");

            // Bozuk dosya: boş başlanır, çökme yok.
            File.WriteAllText(dosya, "{ bozuk");
            var k3 = new SahaBossDeposu();
            k3.KaliciYap(dosya, t0, kapanistaYaz: false);
            Bak("bozuk dosya: boş başlar", k3.Sayi == 0, $"{k3.Sayi}");
        }
        finally
        {
            try { File.Delete(dosya); File.Delete(dosya + ".tmp"); } catch { /* geçici dosya */ }
        }

        Console.WriteLine($"  [saha bossları] geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private static void Varint(List<byte> o, long v)
    {
        while (v >= 0x80) { o.Add((byte)(v & 0x7f | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }

    private static byte[] Canli(int yer, float x, float y, long zaman)
    {
        var b = new List<byte> { 0x01 };
        Varint(b, yer);
        Span<byte> f = stackalloc byte[4];
        foreach (float v in new[] { x, y, 50f })
        {
            BinaryPrimitives.WriteSingleLittleEndian(f, v);
            b.AddRange(f.ToArray());
        }
        b.AddRange(BitConverter.GetBytes(zaman));
        return b.ToArray();
    }

    private static byte[] Olu(int yer, long zaman, bool bosluk = false)
    {
        var b = new List<byte> { 0x00 };
        Varint(b, yer);
        if (bosluk) b.Add(0x00);
        b.AddRange(BitConverter.GetBytes(zaman));
        return b.ToArray();
    }

    /// <summary>0x0191 | 00 00 | harita u32 | adet u8 | kayıtlar | 4 sıfır.</summary>
    private static byte[] Liste(int harita, params byte[][] kayitlar)
    {
        var b = new List<byte> { 0x01, 0x91, 0x00, 0x00 };
        b.AddRange(BitConverter.GetBytes((uint)harita));
        b.Add((byte)kayitlar.Length);
        foreach (var k in kayitlar) b.AddRange(k);
        b.AddRange(new byte[4]);
        return b.ToArray();
    }

    private static byte[] Degistir(byte[] kaynak, int i, byte deger)
    {
        var c = (byte[])kaynak.Clone();
        c[i] = deger;
        return c;
    }
}
