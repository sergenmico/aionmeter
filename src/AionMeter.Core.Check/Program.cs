// Oyun gerektirmeyen denetim: upstream SelfCheckAion2.cs içindeki gerçek yakalama çerçeveleri (SkeeveAN/Aion-DPS-Meter, MIT)
// motorun çözme + toplama yolundan geçirilir. Kullanım: dotnet run -- <assets/aion2 klasörü>
using AionDPS.Aion2;
using AionDPS.Aion2.Capture;
using AionDPS.Aion2.Protocol;
using AionMeter;
using AionMeter.Core;
using AionDPS.Combat;
using AionDPS.Combat.Sources;

string assets = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/aion2"));
bool ok = true;

// 1) Tekil hasar çerçeveleri (upstream RunAion2RealCaptureScenario): 381, 588 (kritik), 432 hasar + 65 iyileştirme.
ok &= Run("tekil çerçeveler", new[]
{
    "04388484030600be08fe26a8004f02000002433baf4101000000ba4ffd020100",
    "04388484032600be08fe26a8005703000002433baf4101000000ba4fcc0401020100",
    "04388484032600be080e4ea8004b02000002837dbe4101000000ba4fb00301010100",
    "0438be080400be0857fcb2004a020792ea4501000000ba4f410100",
}, expectedTotal: 381 + 588 + 432, expectedHits: 3, expectedCrits: 1, expectedDecoded: new long[] { 381, 588, 432, 65 });

// 2) LZ4 paket çerçevesi (upstream RunAion2BundleScenario): 1031, 964, 973, 947 çözülür. Bunlar Chanter iyileştirme becerisi
//    (canavar doğuşu görülmediği için hedefler canavar sayılmaz), bu yüzden hasar tablosuna girmez: toplam 0 beklenir.
ok &= Run("LZ4 paket", new[]
{
    "ffff08020000ff13200438b81c0400ec1b5e7d14011a02c3f8006c01000000c24e87080100200438f81b1d000150c40701001e1d0015003a0015c43a000c1b002700c51b004f200438ea53000212cd53001fea530007071b000c53001fe153000212b353001fe1530007071b00095300f100332a38f81b011335ade5cc0ae02e0001006065a020f4a0019e00f0090c5e7d1401004f576fc60aa7724600c021440d0e92f81b012e0090160538f81b09ec1b354a0120c00b2a0080332a38ea1b0113391f000f4d001415ea4d0010ea4d0010394d00118d77004f332a38e19a001c15e14d0010e14d00019a0011854d00b00e0036857120f4a0010000",
}, expectedTotal: 0, expectedHits: 0, expectedCrits: 0, expectedDecoded: new long[] { 1031, 964, 973, 947 });

// 3) Dövüş kuralları (oyun olmadan, olaylar doğrudan motora verilir).
ok &= FightRules();

using (var proc = System.Diagnostics.Process.GetCurrentProcess())
{
    Console.WriteLine($"[bellek] yönetilen yığın {GC.GetTotalMemory(true) / 1048576.0:N1} MB, özel {proc.PrivateMemorySize64 / 1048576.0:N1} MB, working set {proc.WorkingSet64 / 1048576.0:N1} MB");
}
Console.WriteLine(ok ? "SONUÇ: GEÇTİ" : "SONUÇ: KALDI");
return ok ? 0 : 1;

bool Run(string title, string[] hexFrames, long expectedTotal, long expectedHits, long expectedCrits, long[] expectedDecoded)
{
    using var engine = new Aion2MeterEngine(assets);
    Aion2Protocol protocol = Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));
    var source = new Aion2PacketCombatSource(protocol);
    engine.Attach(source);
    var decoded = new List<long>();
    source.Hit += e => decoded.Add(e.Amount);
    if (Environment.GetEnvironmentVariable("CHECK_VERBOSE") is not null) { source.Hit += e => Console.WriteLine($"  olay: {e.SourceObjectId}->{e.TargetObjectId} {e.Amount} heal={e.IsHeal} local={source.Entities.LocalPlayerId} infer={source.Entities.InferLocalPlayer()} class={source.Entities.ClassOf(e.SourceObjectId)}"); }

    var wire = new List<byte>();
    foreach (string hex in hexFrames)
    {
        byte[] body = Convert.FromHexString(hex);
        int length = body.Length + 4; // kayıt = LEB128(uzunluk) + gövde, uzunluk = gövde + 4
        while (length >= 0x80)
        {
            wire.Add((byte)(length & 0x7f | 0x80));
            length >>= 7;
        }

        wire.Add((byte)length);
        wire.AddRange(body);
    }

    source.Ingest(new TcpSegment(DateTime.Now, "10.0.0.1:13328", "192.168.0.2:50000", 1000, wire.ToArray(), FromServer: true));
    MeterSnapshot snap = engine.Snapshot();
    Console.WriteLine($"[{title}] kalibre: {protocol.IsCalibrated}, çözülen: {string.Join(", ", decoded)}, oyuncu sayısı: {snap.Players.Count}");
    if (!decoded.SequenceEqual(expectedDecoded))
    {
        Console.WriteLine($"  BEKLENEN çözülen {string.Join(", ", expectedDecoded)}");
        return false;
    }
    foreach (PlayerDamage p in snap.Players)
    {
        Console.WriteLine($"  {p.Name} toplam {p.Total} dps {p.Dps:0.0} yerel {p.IsLocal} sınıf {p.ClassName}");
    }

    if (snap.Players.Count != 1 || snap.Players[0].Total != expectedTotal)
    {
        Console.WriteLine($"  BEKLENEN toplam {expectedTotal}");
        return false;
    }

    PlayerDetail? detail = engine.Detail(snap.Players[0].Id);
    Console.WriteLine($"  detay: vuruş {detail?.Hits.Hits} kritik {detail?.Hits.Crits} en büyük {detail?.Hits.MaxHit} beceriler: {string.Join(", ", detail?.Skills.Select(s => $"{s.Name}={s.Total}") ?? [])}");
    long bucketSum = detail?.DamagePerBucket?.Sum() ?? 0;
    Console.WriteLine($"  grafik kovaları: {detail?.DamagePerBucket?.Count} x {detail?.BucketSeconds} sn, toplam {bucketSum}; FightOver {snap.FightOver}");
    return detail is not null && bucketSum == expectedTotal && !snap.FightOver && detail.Hits.Hits == expectedHits && detail.Hits.Crits == expectedCrits && detail.Skills.Sum(s => s.Total) == expectedTotal;
}

bool FightRules()
{
    const int me = 500, boss = 900, add = 901, trash = 950;
    DateTime t0 = DateTime.Now;
    bool all = true;
    DamageEvent Hit(DateTime at, int target, long amount) => new(at, me, target, amount, false, SkillId: 11010000);

    (Aion2MeterEngine Engine, Aion2EntityDirectory Dir) New()
    {
        var engine = new Aion2MeterEngine(assets);
        var source = new Aion2PacketCombatSource(Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json")));
        engine.Attach(source);
        source.Entities.NoteClass(me, "Gladiator"); // tek isimsiz sınıf kullanıcısı: yerel oyuncu sayılır
        return (engine, source.Entities);
    }

    bool Check(string title, bool pass, MeterSnapshot snap)
    {
        Console.WriteLine($"[dövüş] {title}: {(pass ? "tamam" : "HATA")} (FightOver {snap.FightOver}, boss {snap.Boss?.Id} ölü {snap.Boss?.Dead}, toplam {snap.Players.FirstOrDefault()?.Total}, süre {snap.FightDuration.TotalSeconds:0}s)");
        return pass;
    }

    // a) Boss dövüşünde 500k+ canlı bir yardımcı ölür: dövüş bitmez, boss gösterilmeye devam eder.
    {
        var (engine, dir) = New();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-5), 5_000_000);
            dir.HitPoints.Note(add, t0.AddSeconds(-5), 600_000);
            engine.InjectHit(Hit(t0.AddSeconds(-4), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-4), 4_700_000);
            engine.InjectHit(Hit(t0.AddSeconds(-3), add, 100_000));
            dir.HitPoints.Note(add, t0.AddSeconds(-2), 0);
            MeterSnapshot snap = engine.Snapshot();
            all &= Check("yardımcı öldü", !snap.FightOver && snap.Boss is { Id: boss, Dead: false } && snap.Players[0].Total == 400_000, snap);
        }
    }

    // b) Yaralı boss canlı, 25 sn parti hasarı yok: dövüş sürer.
    {
        var (engine, dir) = New();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-26), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-25), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-25), 4_700_000);
            MeterSnapshot snap = engine.Snapshot();
            all &= Check("yaralı boss, 25 sn boşluk", !snap.FightOver && snap.Boss is { Id: boss, Dead: false }, snap);
        }
    }

    // b2) Takım 90 sn ölü kalıp diriliyor: boss'tan can okuması gelmiyor, yine de dövüş sürer ve hasar korunur.
    {
        var (engine, dir) = New();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-91), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-90), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-90), 4_700_000);
            MeterSnapshot snap = engine.Snapshot();
            all &= Check("takım 90 sn ölü", !snap.FightOver && snap.Boss is { Id: boss, Dead: false } && snap.Players.Sum(p => p.Total) == 300_000, snap);
        }
    }

    // c) Boss ölmeden ayrıldı (kaldırma nedeni 7) ve 21+ sn parti hasarı yok: dövüş biter.
    {
        var (engine, dir) = New();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-26), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-25), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-25), 4_700_000);
            dir.NoteRemoved(boss, 7, t0.AddSeconds(-22));
            MeterSnapshot snap = engine.Snapshot();
            all &= Check("boss ayrıldı (7) + 21 sn", snap.FightOver, snap);
        }
    }

    // d) Boss öldü -> donmuş sonuç; bölge değişince de sonuç kalır; sonraki parti vuruşu yeni dövüş başlatır.
    {
        var (engine, dir) = New();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-5), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-4), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-3), 0);
            MeterSnapshot dead = engine.Snapshot();
            all &= Check("boss öldü", dead.FightOver && dead.Boss is { Id: boss, Dead: true } && dead.Players[0].Total == 300_000, dead);
            engine.InjectHit(Hit(t0.AddSeconds(-2), boss, 5_000)); // ölü boss'a geç tik: yok sayılır
            engine.InjectZone(new ZoneChange(t0.AddSeconds(-1), 123456));
            MeterSnapshot afterZone = engine.Snapshot();
            all &= Check("bölge değişimi sonrası", afterZone.FightOver && afterZone.Players.Count > 0 && afterZone.Players[0].Total == 300_000, afterZone);
            engine.InjectHit(Hit(t0, trash, 1_234));
            MeterSnapshot fresh = engine.Snapshot();
            all &= Check("yeni dövüş", !fresh.FightOver && fresh.Players.Count > 0 && fresh.Players[0].Total == 1_234, fresh);
        }
    }

    return all;
}
