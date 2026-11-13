// Parti bayatlığı denetimi: sentetik parti roster'ı (0x0297) ve bölge (0x2136) çerçeveleri gerçek opcodes.json ile çözücüden geçirilir.
// Roster düzeni referans çözücüye göredir (SkeeveAN/Aion-DPS-Meter, MIT, Client/Aion2/Protocol/Aion2FrameDecoder.cs DecodeRoster):
// üye başına sunucu u16 | uzunluk | ad | sınıf kodu u32 | seviye u32.
using System.Buffers.Binary;
using System.Text;
using AionDPS.Aion2;
using AionDPS.Aion2.Protocol;

internal static class PartiKontrol
{
    public static bool Calistir(string assets)
    {
        Console.WriteLine("[parti]");
        Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));
        int gecen = 0, kalan = 0;

        void Bak(string baslik, bool ok, string ayrinti)
        {
            Console.WriteLine($"  {(ok ? "tamam" : "HATA ")} {baslik} | {ayrinti}");
            if (ok) { gecen++; } else { kalan++; }
        }

        Bak("opcode eşlemesi: roster = 0x0297", protocol.FamilyOf(0x0297) == OpcodeFamily.Roster, $"{protocol.FamilyOf(0x0297)}");

        DateTime t0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan bayat = Aion2FrameDecoder.PartiBayatSuresi;
        string Yaz(Aion2EntityDirectory d) => $"parti [{string.Join(", ", d.PartyNames.OrderBy(n => n, StringComparer.Ordinal))}]";

        (Aion2EntityDirectory Dir, Action<byte[], DateTime> Ver) Yeni()
        {
            var dir = new Aion2EntityDirectory();
            var decoder = new Aion2FrameDecoder(protocol, dir);
            return (dir, (f, at) => decoder.Decode(f, at, _ => { }, _ => { }));
        }

        byte[] ikili = Roster("Ben", "Arkadas");

        // 1) Partideyken roster birkaç saniyede bir gelir: bölge değişse de parti hiç düşmez (10 dk boyunca 5 sn'de bir).
        {
            var (dir, ver) = Yeni();
            ver(Zone(1010), t0);
            ver(ikili, t0.AddSeconds(1));
            Bak("roster okundu: iki üye", dir.PartySize == 2 && dir.RosterKnown, Yaz(dir));
            ver(Zone(600001), t0.AddSeconds(60));
            bool hep = true;
            for (int s = 65; s <= 660; s += 5)
            {
                ver(ikili, t0.AddSeconds(s));
                ver(Zone(600001), t0.AddSeconds(s + 1)); // aynı bölge yeniden: değişim değil, yalnız zaman ilerler
                hep &= dir.PartySize == 2;
            }
            Bak("bölge değişti, roster gelmeye devam ediyor: parti kalır", hep && dir.PartySize == 2, Yaz(dir));
        }

        // 2) Partiden ayrıldı, ayrılış çerçevesi kaçtı: bölge değişiminden sonra süre boyunca roster yok -> parti boşalır.
        {
            var (dir, ver) = Yeni();
            ver(Zone(1010), t0);
            ver(ikili, t0.AddSeconds(1));
            DateTime bolge = t0.AddSeconds(60);
            ver(Zone(600001), bolge);
            ver(Zone(600001), bolge + bayat - TimeSpan.FromSeconds(1));
            Bak($"bölge değişiminden {bayat.TotalMinutes:0} dk eksi 1 sn sonra: parti henüz kalır", dir.PartySize == 2, Yaz(dir));
            ver(Zone(600001), bolge + bayat);
            Bak($"{bayat.TotalMinutes:0} dk roster gelmedi: parti boş, roster bilinmiyor", dir.PartySize == 0 && !dir.RosterKnown, Yaz(dir));
            ver(ikili, bolge + bayat + TimeSpan.FromSeconds(3));
            Bak("yeniden roster gelince parti geri gelir", dir.PartySize == 2, Yaz(dir));
        }

        // 3) Bölge değişmeden hiç düşmez (roster uzun süre gelmese de): 1 saat aynı bölgede.
        {
            var (dir, ver) = Yeni();
            ver(Zone(1010), t0);
            ver(ikili, t0.AddSeconds(1));
            for (int dk = 1; dk <= 60; dk++) ver(Zone(1010), t0.AddMinutes(dk));
            Bak("aynı bölgede 1 sa roster yok: parti kalır", dir.PartySize == 2, Yaz(dir));
        }

        // 4) Roster bölge çerçevesinden hemen önce (15 sn içinde) geldi: o yeni bölgenindir, denetim kurulmaz.
        {
            var (dir, ver) = Yeni();
            ver(Zone(1010), t0);
            ver(ikili, t0.AddSeconds(50));
            ver(Zone(600001), t0.AddSeconds(60));
            ver(Zone(600001), t0.AddSeconds(60) + bayat + TimeSpan.FromMinutes(1));
            Bak("bölge çerçevesinden 10 sn önceki roster: parti kalır", dir.PartySize == 2, Yaz(dir));
        }

        // 5) Ayrılınca gelen "yalnız yerel oyuncu" roster'ı partiyi hemen daraltır (mevcut davranış).
        {
            var (dir, ver) = Yeni();
            ver(Zone(1010), t0);
            ver(ikili, t0.AddSeconds(1));
            ver(Roster("Ben"), t0.AddSeconds(20));
            Bak("ayrılış roster'ı (yalnız Ben): tek kişi", dir.PartySize == 1 && dir.PartyNames.Contains("Ben"), Yaz(dir));
        }

        Console.WriteLine($"  [parti] geçen {gecen}, kalan {kalan}");
        return kalan == 0;
    }

    /// <summary>0x0297 | üye başına: sunucu u16 (18 05) | uzunluk | ad | sınıf kodu u32 (5: Gladiator) | seviye u32 (45).</summary>
    private static byte[] Roster(params string[] adlar)
    {
        var b = new List<byte> { 0x02, 0x97 };
        foreach (string ad in adlar)
        {
            byte[] baytlar = Encoding.UTF8.GetBytes(ad);
            b.Add(0x18); b.Add(0x05);
            b.Add((byte)baytlar.Length);
            b.AddRange(baytlar);
            b.AddRange(new byte[] { 5, 0, 0, 0, 45, 0, 0, 0 });
            b.AddRange(new byte[4]);
        }

        b.AddRange(new byte[8]);
        return b.ToArray();
    }

    /// <summary>0x2136 bölge çerçevesi: sayaç u32 | bölge u32 | ... | tür u32 (34) = 0.</summary>
    private static byte[] Zone(int zoneId)
    {
        var b = new byte[38];
        b[0] = 0x21; b[1] = 0x36;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), (uint)zoneId);
        return b;
    }
}
