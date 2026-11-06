// Gizli Küp (Hidden Cube) denetimi: sentetik env çerçeveleri gerçek opcodes.json ile çözücüden geçirilir, dizinin sayaçlarına bakılır.
// Çerçeve düzeni referans çözücüye göredir (SkeeveAN/Aion-DPS-Meter, MIT, Client/Aion2/Protocol/Aion2FrameDecoder.cs DecodeEnvSpawn/Remove/OpenStart).
using System.Buffers.Binary;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;

internal static class KupKontrol
{
    private const long KupSablon = 1200036;   // Verteron Hidden Cube
    private const long ToplamaSablon = 9999999; // küp olmayan env nesnesi

    public static bool Calistir(string assets)
    {
        Console.WriteLine("[gizli küp]");
        Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));
        var dir = new Aion2EntityDirectory();
        var decoder = new Aion2FrameDecoder(protocol, dir);
        DateTime t0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        int gecen = 0, kalan = 0;

        void Ver(byte[] frame, DateTime at) => decoder.Decode(frame, at, _ => { }, _ => { });

        void Bak(string baslik, int gelis, int yakin)
        {
            bool ok = dir.CubeArrivals == gelis && dir.CubesInRange == yakin;
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | geliş {dir.CubeArrivals} (beklenen {gelis}), yakında {dir.CubesInRange} (beklenen {yakin})");
            if (ok) { gecen++; } else { kalan++; }
        }

        Bak("opcode eşlemesi: envSpawn/envRemove/envOpenStart", protocol.FamilyOf(0x3436) == OpcodeFamily.EnvSpawn
            && protocol.FamilyOf(0x3536) == OpcodeFamily.EnvRemove && protocol.FamilyOf(0x3836) == OpcodeFamily.EnvOpenStart ? 0 : -1, 0);

        Ver(Zone(1010), t0);
        Ver(Spawn(500, KupSablon, 100f, 200f), t0.AddSeconds(1));
        Bak("küp doğuşu", 1, 1);

        Ver(Spawn(500, KupSablon, 100f, 200f), t0.AddSeconds(2));
        Bak("aynı küp tekrar duyuruldu: sayılmaz", 1, 1);

        Ver(Spawn(501, ToplamaSablon, 50f, 50f), t0.AddSeconds(3));
        Ver(Remove(501, 4), t0.AddSeconds(4));
        Bak("küp olmayan env kimliği sayılmaz", 1, 1);

        Ver(Remove(500, 4), t0.AddSeconds(5));
        Bak("neden 04 (açıldı): menzilden düşer", 1, 0);

        Ver(Spawn(600, KupSablon, 300f, 300f), t0.AddSeconds(10));
        Bak("ikinci küp", 2, 1);
        Ver(Remove(600, 1), t0.AddSeconds(20));
        Bak("neden 01 (menzil dışı): yakında değil", 2, 0);
        Ver(Spawn(600, KupSablon, 300f, 300f), t0.AddSeconds(30));
        Bak("menzil sınırında geri geldi (10 sn): yeniden sayılmaz", 2, 1);
        Ver(Remove(600, 1), t0.AddSeconds(40));
        Ver(Spawn(601, KupSablon, 301f, 299f), t0.AddSeconds(45));
        Bak("aynı yerde yeni kimlikle duyuruldu (5 sn): aynı küp", 2, 1);
        Ver(Remove(601, 1), t0.AddSeconds(50));
        Ver(Spawn(601, KupSablon, 301f, 299f), t0.AddSeconds(50) + Aion2EntityDirectory.CubeReturnCountsAfter);
        Bak("uzun süre sonra döndü: yeniden geliş", 3, 1);

        Ver(Spawn(700, KupSablon, 900f, 900f), t0.AddMinutes(5));
        Ver(Open(700, 42), t0.AddMinutes(5).AddSeconds(1));
        Bak("açılmaya başlandı: 04 gelene kadar menzilde", 4, 2);
        Ver(Remove(700, 4), t0.AddMinutes(5).AddSeconds(3));
        Bak("açıldı (04)", 4, 1);

        Ver(Remove(12345, 1), t0.AddMinutes(6));
        Bak("bilinmeyen kimliğin kaldırılması bir şey değiştirmez", 4, 1);

        Ver(Zone(600001), t0.AddMinutes(7));
        Bak("bölge değişti: menzildekiler temizlenir, sayaç kalır", 4, 0);
        Ver(Spawn(601, KupSablon, 301f, 299f), t0.AddMinutes(7).AddSeconds(1));
        Bak("yeni bölgede aynı kimlik: yeni küp", 5, 1);

        // Denetim bulgusu 5: yeni bölgenin küpü bölge çerçevesinden hemen önce duyurulursa korunur (rozet sönmez, ses ikinci kez çalmaz).
        Ver(Spawn(800, KupSablon, 50f, 60f), t0.AddMinutes(9));
        Bak("bölge çerçevesinden 2 sn önce duyurulan küp", 6, 2);
        Ver(Zone(1010), t0.AddMinutes(9).AddSeconds(2));
        Bak("bölge değişti: hemen önce duyurulan küp korunur, eskisi düşer", 6, 1);
        Ver(Spawn(800, KupSablon, 50f, 60f), t0.AddMinutes(9).AddSeconds(3));
        Bak("aynı küp yeniden duyuruldu: ses ikinci kez çalmaz", 6, 1);

        Console.WriteLine($"  [gizli küp] geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }

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

    /// <summary>0x3436: varlık (varint) | tür | 00 | env kimliği u32 | x | y | z | durum ve doğuş zamanı (referans: +28 durum, +29 zaman).</summary>
    private static byte[] Spawn(int entity, long envId, float x, float y)
    {
        var b = new List<byte> { 0x34, 0x36 };
        Varint(b, entity);
        b.Add(0x02); b.Add(0x00);
        U32(b, (uint)envId);
        F32(b, x); F32(b, y); F32(b, 12.5f);
        b.AddRange(new byte[11]);
        b.Add(0x01);
        b.AddRange(BitConverter.GetBytes(1_762_430_000_000L));
        return b.ToArray();
    }

    /// <summary>0x3536: varlık (varint) | neden | 03 00.</summary>
    private static byte[] Remove(int entity, byte reason)
    {
        var b = new List<byte> { 0x35, 0x36 };
        Varint(b, entity);
        b.Add(reason); b.Add(0x03); b.Add(0x00);
        return b.ToArray();
    }

    /// <summary>0x3836: nesne (varint) | açan (varint) | 01.</summary>
    private static byte[] Open(int entity, int actor)
    {
        var b = new List<byte> { 0x38, 0x36 };
        Varint(b, entity); Varint(b, actor); b.Add(0x01);
        return b.ToArray();
    }

    /// <summary>0x2136 bölge çerçevesi: sayaç u32 | bölge u32 | ... konum (18, 22) | tür u32 (34).</summary>
    private static byte[] Zone(int zoneId)
    {
        var b = new List<byte> { 0x21, 0x36 };
        U32(b, 1); U32(b, (uint)zoneId);
        b.AddRange(new byte[8]);
        F32(b, 0f); F32(b, 0f);
        b.AddRange(new byte[8]);
        U32(b, 0);
        return b.ToArray();
    }
}
