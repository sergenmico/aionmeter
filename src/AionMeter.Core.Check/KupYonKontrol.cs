// Gizli Küp yön/mesafe denetimi: sentetik konum, hareket, etki, bölge ve env çerçeveleri gerçek opcodes.json ile çözücüden geçirilir;
// oyuncunun yeri ve en yakın küpe pusula yönü/mesafe anlık görüntü eki (Aion2MeterEngine.KupYonuEkle) üzerinden denetlenir.
// Çerçeve düzenleri referans çözücüye göredir (SkeeveAN/Aion-DPS-Meter, MIT, Client/Aion2/Protocol/Aion2FrameDecoder.cs
// DecodeLocalPosition, DecodePosition, DecodeBuff, DecodeZone). Birim: 100 dünya birimi = 1 m; kuzey = -y, doğu = +x.
using System.Buffers.Binary;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;
using AionMeter;
using AionMeter.Core;

internal static class KupYonKontrol
{
    private const long KupSablon = 1200036; // Verteron Hidden Cube
    private const int Ben = 77;

    public static bool Calistir(string assets)
    {
        Console.WriteLine("[küp yönü]");
        Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));
        int gecen = 0, kalan = 0;

        void Sonuc(bool ok, string baslik, string ayrinti)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | {ayrinti}");
            if (ok) { gecen++; } else { kalan++; }
        }

        Sonuc(protocol.FamilyOf(0x3C37) == OpcodeFamily.LocalPosition, "opcode eşlemesi: localPosition = 0x3C37", $"{protocol.FamilyOf(0x3C37)}");

        // Yer bilinmiyor: küp var ama yön/mesafe yok (overlay eski görünümde kalır).
        {
            var bos = new Aion2EntityDirectory();
            var cz = new Aion2FrameDecoder(protocol, bos);
            cz.Decode(Spawn(500, 10000f, 5000f), DateTime.UtcNow, _ => { }, _ => { });
            var s = Aion2MeterEngine.KupYonuEkle(bos, Anlik(bos));
            Sonuc(s.KupYakinda == 1 && s.KupYonDerece is null && s.KupMesafe is null, "yer bilinmiyor: yön/mesafe yok", Yaz(s));
        }

        var dir = new Aion2EntityDirectory { LocalPlayerId = Ben };
        long saat = 1_000_000;
        dir.Konum.Saat = () => saat;
        var decoder = new Aion2FrameDecoder(protocol, dir);
        DateTime t0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        void Ver(byte[] frame) => decoder.Decode(frame, t0.AddMilliseconds(saat - 1_000_000), _ => { }, _ => { });

        void Bak(string baslik, double? derece, int? metre, bool tahmini)
        {
            var s = Aion2MeterEngine.KupYonuEkle(dir, Anlik(dir));
            bool ok = derece is null
                ? s.KupYonDerece is null && s.KupMesafe is null
                : s.KupYonDerece is double d && Math.Abs(d - derece.Value) < 0.5 && s.KupMesafe == metre && s.KupKonumTahmini == tahmini;
            Sonuc(ok, baslik, $"{Yaz(s)} (beklenen {(derece is null ? "yok" : $"{derece:0}° {metre} m{(tahmini ? " ~" : "")}")})");
        }

        Ver(Zone(1010, 0f, 0f));
        Ver(Spawn(500, 10000f, 5000f));
        Bak("bölge çerçevesi (0, 0): yer sayılmaz", null, null, false);

        Ver(Konum(Ben, 10000f, 8800f));
        Bak("konum çerçevesi: küp tam kuzeyde 38 m", 0, 38, false);

        Ver(Konum(Ben, 7000f, 8000f));
        Bak("küp kuzeydoğuda (45°) 42 m", 45, 42, false);

        Ver(Konum(Ben, 10000f, 8800f));
        Ver(Konum(901, 30000f, 30000f)); // başkasının konum çerçevesi: oyuncunun yerini değiştirmez
        Bak("başkasının konum çerçevesi yerel yeri değiştirmez", 0, 38, false);

        Ver(Hareket(Ben, 60000f, 60000f)); // kendi hareketi çevreye girmez
        saat += 4500;
        Bak("4,5 sn sonra: eski kesin yer, tahmini", 0, 38, true);

        saat += 4500;
        Bak("9 sn sonra, çevre yok: yer bilinmiyor", null, null, false);

        Ver(Hareket(911, 11900f, 4900f));
        Ver(Hareket(912, 12000f, 5000f));
        Ver(Hareket(913, 12100f, 5100f));
        Bak("çevredeki üç varlığın ortası: küp batıda 20 m, tahmini", 270, 20, true);

        Ver(Hareket(914, 90000f, 90000f));
        Bak("uzaktaki varlık kırpılır: yine batı 20 m", 270, 20, true);

        Ver(Etki(hedef: 5000, koyan: Ben, 10000f, -5000f));
        Bak("yerel oyuncunun etkisi (son 12 bayt): küp güneyde 100 m", 180, 100, false);

        Ver(Etki(hedef: 5000, koyan: 333, 10000f, 0f + 2000f));
        Bak("başkasının etkisi yeri değiştirmez", 180, 100, false);

        Ver(Yenilendi(hedef: 5000, koyan: Ben, 4000f, 5000f));
        Bak("yenilenen etki: küp doğuda 60 m", 90, 60, false);

        Ver(Spawn(501, 4500f, 5000f));
        Bak("iki küp: en yakını (doğuda 5 m)", 90, 5, false);

        Ver(EnvRemove(501, 4));
        Bak("yakındaki küp açıldı: diğeri (doğuda 60 m)", 90, 60, false);

        // Yeni bölge: eski çevre ve küpler gider; bölge çerçevesinin yeri kesin bildirimdir.
        saat += 60_000;
        Ver(Zone(600001, 50000f, 50000f));
        Bak("bölge değişti: küp yok", null, null, false);
        Ver(Spawn(700, 46000f, 46000f));
        Bak("yeni bölgede küp: kuzeybatı (315°) 57 m", 315, 57, false);

        // Doğuş çerçevesindeki canavar yeri çevreye girer.
        saat += 9000;
        Ver(NpcSpawn(921, 46000f, 49000f));
        Ver(NpcSpawn(922, 46100f, 49000f));
        Ver(NpcSpawn(923, 45900f, 49000f));
        Bak("canavar doğuşlarının ortası: küp kuzeyde 30 m, tahmini", 0, 30, true);
        Ver(NpcRemove(921));
        Ver(NpcRemove(922));
        Bak("canavarlar kalktı (2 kaldı < 3): yer bilinmiyor", null, null, false);

        (double dd, double dm) = Aion2Konum.YonVeMesafe(0, 0, 0, 100);
        Sonuc(Math.Abs(dd - 180) < 1e-9 && Math.Abs(dm - 1) < 1e-9, "yön hesabı: +y güney, 100 birim = 1 m", $"{dd:0.#}° {dm:0.##} m");

        Console.WriteLine($"  [küp yönü] geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }

    private static MeterSnapshot Anlik(Aion2EntityDirectory dir) =>
        new(EngineState.Connected, null, Array.Empty<PlayerDamage>(), null, TimeSpan.Zero, KupGelis: dir.CubeArrivals, KupYakinda: dir.CubesInRange);

    private static string Yaz(MeterSnapshot s) =>
        $"yakında {s.KupYakinda}, yön {(s.KupYonDerece is double d ? $"{d:0.#}°" : "yok")}, mesafe {(s.KupMesafe is int m ? $"{m} m" : "yok")}{(s.KupKonumTahmini ? " ~" : "")}";

    private static void Varint(List<byte> o, long v)
    {
        while (v >= 0x80) { o.Add((byte)(v & 0x7f | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }

    private static void F32(List<byte> o, float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        o.AddRange(b.ToArray());
    }

    private static void U32(List<byte> o, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        o.AddRange(b.ToArray());
    }

    /// <summary>0x3C37 konum çerçevesi: varlık (varint) | 03 | x | y | z.</summary>
    private static byte[] Konum(int varlik, float x, float y)
    {
        var b = new List<byte> { 0x3C, 0x37 };
        Varint(b, varlik);
        b.Add(0x03);
        F32(b, x); F32(b, y); F32(b, 120f);
        return b.ToArray();
    }

    /// <summary>0x1a37 hareket çerçevesi: varlık (varint) | 2 bayt | x | y.</summary>
    private static byte[] Hareket(int varlik, float x, float y)
    {
        var b = new List<byte> { 0x1a, 0x37 };
        Varint(b, varlik);
        b.Add(0x00); b.Add(0x00);
        F32(b, x); F32(b, y);
        return b.ToArray();
    }

    /// <summary>0x2a38 etki kondu: hedef | 01 | tür | örnek | etki u32 | süre u32 | kalıcı u32 | 8 bayt | koyan | 4 bayt | x | y | z.</summary>
    private static byte[] Etki(int hedef, int koyan, float x, float y) => EtkiCercevesi(0x2a, hedef, koyan, x, y);

    /// <summary>0x2b38 etki yenilendi: "kondu"daki 01 baytı yok.</summary>
    private static byte[] Yenilendi(int hedef, int koyan, float x, float y) => EtkiCercevesi(0x2b, hedef, koyan, x, y);

    private static byte[] EtkiCercevesi(byte ilk, int hedef, int koyan, float x, float y)
    {
        var b = new List<byte> { ilk, 0x38 };
        Varint(b, hedef);
        if (ilk == 0x2a) b.Add(0x01);
        b.Add(0x00);
        Varint(b, 4242);
        U32(b, 12_345_670); U32(b, 30_000); U32(b, 0);
        b.AddRange(new byte[8]);
        Varint(b, koyan);
        b.AddRange(new byte[4]);
        F32(b, x); F32(b, y); F32(b, 150f);
        return b.ToArray();
    }

    /// <summary>0x3436 env doğuşu (küp).</summary>
    private static byte[] Spawn(int entity, float x, float y)
    {
        var b = new List<byte> { 0x34, 0x36 };
        Varint(b, entity);
        b.Add(0x02); b.Add(0x00);
        U32(b, (uint)KupSablon);
        F32(b, x); F32(b, y); F32(b, 12.5f);
        b.AddRange(new byte[11]);
        b.Add(0x01);
        b.AddRange(BitConverter.GetBytes(1_762_430_000_000L));
        return b.ToArray();
    }

    /// <summary>0x3536 env kaldırma: varlık | neden | 03 00.</summary>
    private static byte[] EnvRemove(int entity, byte reason)
    {
        var b = new List<byte> { 0x35, 0x36 };
        Varint(b, entity);
        b.Add(reason); b.Add(0x03); b.Add(0x00);
        return b.ToArray();
    }

    /// <summary>0x4136 canavar doğuşu: varlık | 2 tür baytı | 00 (ad yok) | NPC kimliği u32 | 2 bayt | x | y | ...</summary>
    private static byte[] NpcSpawn(int entity, float x, float y)
    {
        var b = new List<byte> { 0x41, 0x36 };
        Varint(b, entity);
        b.Add(0x01); b.Add(0x00); b.Add(0x00);
        U32(b, 2_100_001);
        b.Add(0x00); b.Add(0x00);
        F32(b, x); F32(b, y); F32(b, 10f);
        b.AddRange(new byte[16]);
        return b.ToArray();
    }

    /// <summary>0x4236 canavar kalktı: varlık | 00 | neden.</summary>
    private static byte[] NpcRemove(int entity)
    {
        var b = new List<byte> { 0x42, 0x36 };
        Varint(b, entity);
        b.Add(0x00); b.Add(0x03);
        return b.ToArray();
    }

    /// <summary>0x2136 bölge çerçevesi: sayaç u32 | bölge u32 | 8 bayt | x (18) | y (22) | 8 bayt | tür u32 (34).</summary>
    private static byte[] Zone(int zoneId, float x, float y)
    {
        var b = new List<byte> { 0x21, 0x36 };
        U32(b, 1); U32(b, (uint)zoneId);
        b.AddRange(new byte[8]);
        F32(b, x); F32(b, y);
        b.AddRange(new byte[8]);
        U32(b, 0);
        return b.ToArray();
    }
}
