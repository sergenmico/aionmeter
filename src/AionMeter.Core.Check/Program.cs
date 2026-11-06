// Oyun gerektirmeyen denetim: upstream SelfCheckAion2.cs içindeki gerçek yakalama çerçeveleri (SkeeveAN/Aion-DPS-Meter, MIT)
// motorun çözme + toplama yolundan geçirilir; dövüş kuralları (MOTOR senaryoları) olaylar doğrudan motora verilerek denenir.
// Kullanım: dotnet run -- <assets/aion2 klasörü>
using AionDPS.Aion2;
using AionDPS.Aion2.Capture;
using AionDPS.Aion2.Protocol;
using AionMeter;
using AionMeter.Core;
using AionDPS.Combat;
using AionDPS.Combat.Sources;

string assets = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/aion2"));
AssetPaths.Root = assets;
bool ok = true;
int passed = 0, failed = 0;

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

// 3) Dövüş kuralları ve MOTOR senaryoları (oyun olmadan, olaylar doğrudan motora verilir).
FightRules();
MotorScenarios();
FrameChecks();
TcpChecks();
OtherChecks();
AuditChecks();
PlayerTargetChecks();
#if YEREL_TANI
ok &= DogrulukKontrol.Calistir(assets); // isteğe bağlı yerel tanı eklentisi (-p:YerelTani=true)
#endif

using (var proc = System.Diagnostics.Process.GetCurrentProcess())
{
    Console.WriteLine($"[bellek] yönetilen yığın {GC.GetTotalMemory(true) / 1048576.0:N1} MB, özel {proc.PrivateMemorySize64 / 1048576.0:N1} MB, working set {proc.WorkingSet64 / 1048576.0:N1} MB");
}

ok &= failed == 0;
Console.WriteLine($"[özet] geçen {passed}, kalan {failed}");
Console.WriteLine(ok ? "SONUÇ: GEÇTİ" : "SONUÇ: KALDI");
return ok ? 0 : 1;

bool Check(string title, bool pass, string? detail = null)
{
    Console.WriteLine($"  {(pass ? "tamam" : "HATA ")} {title}{(detail is null ? "" : " | " + detail)}");
    if (pass) { passed++; } else { failed++; }
    return pass;
}

string Show(MeterSnapshot s) =>
    $"bitti={s.FightOver} boss={s.Boss?.Id}({s.Boss?.Current:N0}/{s.Boss?.Max:N0} ölü={s.Boss?.Dead} ?={s.Boss?.CanBilinmiyor}) süre={s.FightDuration.TotalSeconds:0.0}s | "
    + string.Join(" ; ", s.Players.Select(p => $"{p.Name}[{p.Id}] top={p.Total:N0} dps={p.Dps:N0} bossTop={p.BossTotal:N0} bossDps={p.BossDps:N0} yerel={p.IsLocal} ölüm={p.Olum}"));

Aion2Protocol Proto() => Aion2Protocol.Load(Path.Combine(assets, "protocol", "opcodes.json"));

(Aion2MeterEngine E, Aion2EntityDirectory D, Aion2PacketCombatSource S) New()
{
    var e = new Aion2MeterEngine(assets);
    var s = new Aion2PacketCombatSource(Proto());
    e.Attach(s);
    return (e, s.Entities, s);
}

static void Varint(List<byte> o, long v)
{
    while (v >= 0x80) { o.Add((byte)(v & 0x7f | 0x80)); v >>= 7; }
    o.Add((byte)v);
}

// Sentetik hasar çerçevesi (gerçek çerçevelerin düzeni): hedef, bayrak, aktör, beceri, tür/kritik, [bit, 00, yön], float, vuruş sayısı, güç, tutar, kuyruk.
static byte[] Damage(int target, int actor, int skill, long amount, byte flags = 0x06, byte bits = 0x00, byte dir = 0x02, uint floatBits = 0x41af3b43)
{
    var b = new List<byte> { 0x04, 0x38 };
    Varint(b, target); b.Add(flags); b.Add(0x00); Varint(b, actor);
    b.AddRange(BitConverter.GetBytes(skill)); b.Add(0x4f); b.Add(0x02);
    if ((flags & 0xf) == 4) { b.AddRange(BitConverter.GetBytes(floatBits)); }
    else { b.Add(bits); b.Add(0x00); b.Add(dir); b.AddRange(BitConverter.GetBytes(floatBits)); }
    b.AddRange(new byte[] { 1, 0, 0, 0 }); Varint(b, 10170); Varint(b, amount); b.Add(1); b.Add(0);
    return b.ToArray();
}

static byte[] Wire(IEnumerable<byte[]> frames)
{
    var w = new List<byte>();
    foreach (var f in frames) { Varint(w, f.Length + 4); w.AddRange(f); }
    return w.ToArray();
}

bool Run(string title, string[] hexFrames, long expectedTotal, long expectedHits, long expectedCrits, long[] expectedDecoded)
{
    using var engine = new Aion2MeterEngine(assets);
    Aion2Protocol protocol = Proto();
    var source = new Aion2PacketCombatSource(protocol);
    engine.Attach(source);
    var decoded = new List<long>();
    source.Hit += e => decoded.Add(e.Amount);

    var wire = new List<byte>();
    foreach (string hex in hexFrames)
    {
        byte[] body = Convert.FromHexString(hex);
        int length = body.Length + 4; // kayıt = LEB128(uzunluk) + gövde, uzunluk = gövde + 4
        Varint(wire, length);
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

void FightRules()
{
    Console.WriteLine("[dövüş kuralları]");
    const int me = 500, boss = 900, add = 901, trash = 950;
    const int catalogNpc = 2300708; // Ruthilis of Pain (boss_npcs.json)
    DateTime t0 = DateTime.Now;
    DamageEvent Hit(DateTime at, int target, long amount) => new(at, me, target, amount, false, SkillId: 11010000);

    (Aion2MeterEngine Engine, Aion2EntityDirectory Dir) NewFight()
    {
        var (engine, dir, _) = New();
        dir.NoteClass(me, "Gladiator"); // tek isimsiz sınıf kullanıcısı: yerel oyuncu sayılır
        return (engine, dir);
    }

    // a) Boss dövüşünde 500k+ canlı bir yardımcı ölür: dövüş bitmez, boss gösterilmeye devam eder.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-5), 5_000_000);
            dir.HitPoints.Note(add, t0.AddSeconds(-5), 600_000);
            engine.InjectHit(Hit(t0.AddSeconds(-4), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-4), 4_700_000);
            engine.InjectHit(Hit(t0.AddSeconds(-3), add, 100_000));
            dir.HitPoints.Note(add, t0.AddSeconds(-2), 0);
            MeterSnapshot snap = engine.Snapshot();
            Check("yardımcı öldü: dövüş sürer", !snap.FightOver && snap.Boss is { Id: boss, Dead: false } && snap.Players[0].Total == 400_000, Show(snap));
        }
    }

    // b) Yaralı katalog boss'u canlı, 25 sn parti hasarı yok: dövüş sürer.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.RegisterNpc(boss, catalogNpc);
            dir.HitPoints.Note(boss, t0.AddSeconds(-26), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-25), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-25), 4_700_000);
            MeterSnapshot snap = engine.Snapshot();
            Check("yaralı katalog boss'u, 25 sn boşluk: dövüş sürer", !snap.FightOver && snap.Boss is { Id: boss, Dead: false }, Show(snap));
        }
    }

    // b2) Takım 90 sn ölü kalıp diriliyor: boss'tan can okuması gelmiyor, yine de dövüş sürer ve hasar korunur.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.RegisterNpc(boss, catalogNpc);
            dir.HitPoints.Note(boss, t0.AddSeconds(-91), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-90), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-90), 4_700_000);
            MeterSnapshot snap = engine.Snapshot();
            Check("takım 90 sn ölü (katalog boss'u): dövüş sürer", !snap.FightOver && snap.Boss is { Id: boss, Dead: false } && snap.Players.Sum(p => p.Total) == 300_000, Show(snap));
        }
    }

    // b3) Aynı durum, katalog dışı yaralı kukla: kısa tutma, 20 sn sonra dövüş biter.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-26), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-25), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-25), 4_700_000);
            MeterSnapshot snap = engine.Snapshot();
            Check("yaralı kukla, 25 sn boşluk: dövüş biter (kukla tutmaz)", snap.FightOver && snap.Players[0].Total == 300_000, Show(snap));
        }
    }

    // c) Boss ölmeden ayrıldı (kaldırma nedeni 7) ve 21+ sn parti hasarı yok: dövüş biter.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.RegisterNpc(boss, catalogNpc);
            dir.HitPoints.Note(boss, t0.AddSeconds(-26), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-25), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-25), 4_700_000);
            dir.NoteRemoved(boss, 7, t0.AddSeconds(-22));
            MeterSnapshot snap = engine.Snapshot();
            Check("boss ayrıldı (7) + 21 sn: dövüş biter", snap.FightOver, Show(snap));
        }
    }

    // d) Katalog boss'u öldü -> donmuş sonuç; bölge değişince de sonuç kalır; sonraki parti vuruşu yeni dövüş başlatır.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.RegisterNpc(boss, catalogNpc);
            dir.HitPoints.Note(boss, t0.AddSeconds(-5), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-4), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-3), 0);
            MeterSnapshot dead = engine.Snapshot();
            Check("katalog boss'u öldü: donmuş sonuç", dead.FightOver && dead.Boss is { Id: boss, Dead: true } && dead.Players[0].Total == 300_000, Show(dead));
            engine.InjectHit(Hit(t0.AddSeconds(-2), boss, 5_000)); // ölü boss'a geç tik: yok sayılır
            engine.InjectZone(new ZoneChange(t0.AddSeconds(-1), 123456));
            MeterSnapshot afterZone = engine.Snapshot();
            Check("bölge değişimi sonrası sonuç kalır", afterZone.FightOver && afterZone.Players.Count > 0 && afterZone.Players[0].Total == 300_000, Show(afterZone));
            engine.InjectHit(Hit(t0, trash, 1_234));
            MeterSnapshot fresh = engine.Snapshot();
            Check("yeni dövüş", !fresh.FightOver && fresh.Players.Count > 0 && fresh.Players[0].Total == 1_234, Show(fresh));
        }
    }

    // d2) Katalog dışı kukla 0'a indi (kaldırma kaydı yok): dövüş bitmez; sunucu "öldü" diye kaldırınca biter.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.HitPoints.Note(boss, t0.AddSeconds(-5), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-4), boss, 300_000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-3), 0);
            MeterSnapshot zero = engine.Snapshot();
            Check("kukla 0 (kaldırma yok): dövüş sürer", !zero.FightOver && zero.Boss is { Id: boss, Dead: false }, Show(zero));
            dir.NoteRemoved(boss, Aion2EntityDirectory.RemovedDead, t0.AddSeconds(-2));
            MeterSnapshot removed = engine.Snapshot();
            Check("kukla \"öldü\" kaldırması: dövüş biter", removed.FightOver && removed.Boss is { Dead: true }, Show(removed));
        }
    }

    // e) Dünya boss'u: 12 yabancı + yerel oyuncu boss'a vuruyor -> 13 oyuncu, hasara göre azalan, yerel dahil.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t0));
            dir.HitPoints.Note(boss, t0.AddSeconds(-10), 50_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-9), boss, 5_500));
            for (int i = 0; i < 12; i++)
            {
                int stranger = 2000 + i;
                dir.Register(stranger, "Yabanci" + i);
                dir.NoteClass(stranger, "Ranger");
                engine.InjectHit(new DamageEvent(t0.AddSeconds(-8), stranger, boss, 1_000L * (i + 1), false, SkillId: 13010000));
            }

            dir.HitPoints.Note(boss, t0.AddSeconds(-7), 49_000_000);
            MeterSnapshot snap = engine.Snapshot();
            bool sorted = snap.Players.Zip(snap.Players.Skip(1)).All(pair => pair.First.Total >= pair.Second.Total);
            Check($"dünya boss'u, 12 yabancı + yerel -> {snap.Players.Count} oyuncu", snap.Players.Count == 13 && sorted && snap.Players.Any(p => p.IsLocal && p.Total == 5_500));
        }
    }

    // f) 5 kişilik parti, boss'a yalnız parti vuruyor (bir yabancı başka bir canavara vuruyor) -> tam 5 oyuncu.
    {
        var (engine, dir) = NewFight();
        using (engine)
        {
            dir.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t0));
            string[] members = { "Ben", "Uye1", "Uye2", "Uye3", "Uye4" };
            for (int i = 1; i < members.Length; i++)
            {
                dir.Register(600 + i, members[i]);
                dir.NoteClass(600 + i, "Cleric");
            }

            dir.NoteParty(members, t0);
            dir.Register(777, "Yabanci");
            dir.NoteClass(777, "Sorcerer");
            dir.HitPoints.Note(boss, t0.AddSeconds(-10), 5_000_000);
            engine.InjectHit(Hit(t0.AddSeconds(-9), boss, 10_000));
            for (int i = 1; i < members.Length; i++)
            {
                engine.InjectHit(new DamageEvent(t0.AddSeconds(-8), 600 + i, boss, 2_000L * i, false, SkillId: 17010000));
            }

            engine.InjectHit(new DamageEvent(t0.AddSeconds(-8), 777, trash, 99_999, false, SkillId: 16010000));
            dir.HitPoints.Note(boss, t0.AddSeconds(-7), 4_900_000);
            MeterSnapshot snap = engine.Snapshot();
            Check($"5 kişilik parti -> {snap.Players.Count} oyuncu", snap.Players.Count == 5 && snap.Players.All(p => members.Contains(p.Name)), Show(snap));
        }
    }
}

void MotorScenarios()
{
    Console.WriteLine("[MOTOR senaryoları]");
    DateTime t = DateTime.Now;
    const int me = 500, friend = 600, boss = 900, add = 901, dummyB = 902;
    const int catalogNpc = 2300708;
    DamageEvent H(int src, DateTime at, int target, long amount, int skill = 11010000) => new(at, src, target, amount, false, SkillId: skill);
    PlayerDamage? P(MeterSnapshot s, int id) => s.Players.FirstOrDefault(p => p.Id == id);

    // MOTOR 1: önce 600k'lık bir yardımcıya (add) vuruluyor, sonra boss'a; add'in canı 0'a iniyor.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.HitPoints.Note(boss, t.AddSeconds(-20), 5_000_000);
        d.HitPoints.Note(add, t.AddSeconds(-20), 600_000);
        e.InjectHit(H(me, t.AddSeconds(-15), add, 50_000));
        e.Snapshot();
        e.InjectHit(H(me, t.AddSeconds(-14), boss, 300_000));
        e.InjectHit(H(me, t.AddSeconds(-13), boss, 300_000));
        d.HitPoints.Note(boss, t.AddSeconds(-13), 4_400_000);
        MeterSnapshot s1 = e.Snapshot();
        Check("MOTOR 1: parti boss'a açıkça daha çok vuruyor -> dövüş boss'u boss", s1.Boss?.Id == boss && !s1.FightOver, Show(s1));
        d.HitPoints.Note(add, t.AddSeconds(-12), 0);
        e.InjectHit(H(me, t.AddSeconds(-11), boss, 100_000));
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 1: add öldü, dövüş sürer, hasar birikir", !s2.FightOver && s2.Boss?.Id == boss && P(s2, me)?.Total == 750_000 && P(s2, me)?.BossTotal == 700_000, Show(s2));
    }

    // MOTOR 1b: dövüş boss'u katalog dışı add; add "öldü" kaldırmasıyla gidiyor, parti boss'a da vuruyor -> dövüş boss'a geçer, bitmez.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.HitPoints.Note(boss, t.AddSeconds(-20), 5_000_000);
        d.HitPoints.Note(add, t.AddSeconds(-20), 600_000);
        e.InjectHit(H(me, t.AddSeconds(-6), add, 300_000));
        e.InjectHit(H(me, t.AddSeconds(-5), boss, 200_000));
        d.HitPoints.Note(boss, t.AddSeconds(-5), 4_800_000);
        MeterSnapshot s0 = e.Snapshot();
        d.NoteRemoved(add, Aion2EntityDirectory.RemovedDead, t.AddSeconds(-4));
        MeterSnapshot s1 = e.Snapshot();
        Check("MOTOR 1b: katalog dışı aday öldü, başka canlı adaya vuruluyor -> dövüş sürer", s0.Boss?.Id == add && !s1.FightOver && s1.Boss?.Id == boss, Show(s1));
    }

    // MOTOR 1c: katalog boss'u varken parti bir add'e (katalog dışı) daha çok vursa da dövüş boss'u katalog boss'u kalır.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-20), 5_000_000);
        d.HitPoints.Note(add, t.AddSeconds(-20), 900_000);
        e.InjectHit(H(me, t.AddSeconds(-6), add, 100_000));
        e.InjectHit(H(me, t.AddSeconds(-5), boss, 50_000));
        d.HitPoints.Note(boss, t.AddSeconds(-5), 4_950_000);
        e.InjectHit(H(me, t.AddSeconds(-4), add, 500_000));
        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 1c: katalog boss'u öncelikli, add'e çok vurmak değiştirmez", s.Boss?.Id == boss && !s.FightOver, Show(s));
    }

    // MOTOR 2: partide iki kişi iki ayrı kuklaya (ikisinin de canı görülüyor), arkadaş önce vuruyor.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Arkadas"); d.NoteClass(friend, "Ranger");
        d.NoteParty(new[] { "Ben", "Arkadas" }, t);
        d.HitPoints.Note(boss, t.AddSeconds(-20), 10_000_000);
        d.HitPoints.Note(dummyB, t.AddSeconds(-20), 10_000_000);
        e.InjectHit(H(friend, t.AddSeconds(-10), dummyB, 200_000, 13010000));
        e.Snapshot();
        e.InjectHit(H(me, t.AddSeconds(-9), boss, 300_000));
        e.InjectHit(H(me, t.AddSeconds(-2), boss, 300_000));
        e.InjectHit(H(friend, t.AddSeconds(-2), dummyB, 50_000, 13010000));
        d.HitPoints.Note(boss, t.AddSeconds(-2), 9_400_000);
        d.HitPoints.Note(dummyB, t.AddSeconds(-2), 9_750_000);
        d.SetLocalTarget(boss);
        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 2: dövüş boss'u kendi kuklam (parti ona daha çok vurdu), BossTotal doğru",
            s.Boss?.Id == boss && P(s, me) is { Total: 600_000, BossTotal: 600_000, IsLocal: true } && P(s, friend) is { Total: 250_000, BossTotal: 0 }, Show(s));
    }

    // MOTOR 3: roster yok, arkadaş aynı kuklaya vurup canını 0'a indiriyor; kukla dolup tekrar vuruluyor.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Arkadas"); d.NoteClass(friend, "Ranger");
        d.HitPoints.Note(boss, t.AddSeconds(-20), 1_000_000);
        e.InjectHit(H(me, t.AddSeconds(-10), boss, 400_000));
        d.HitPoints.Note(boss, t.AddSeconds(-10), 600_000);
        e.InjectHit(H(friend, t.AddSeconds(-9), boss, 600_000, 13010000));
        d.HitPoints.Note(boss, t.AddSeconds(-9), 0);
        MeterSnapshot s1 = e.Snapshot();
        d.HitPoints.Note(boss, t.AddSeconds(-8), 1_000_000);
        e.InjectHit(H(me, t.AddSeconds(-7), boss, 50_000));
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 3: kukla 0'a inip doldu, dövüş bölünmez", !s1.FightOver && !s2.FightOver && P(s2, me)?.Total == 450_000 && P(s2, friend)?.Total == 600_000, Show(s2));
    }

    // MOTOR 4: karakter kaydı var (ben), arkadaşın adı hiç gelmedi, beceri sayısı benden çok: açık kimlik varken tahmine bakılmaz.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.NoteClass(friend, "Ranger");
        e.InjectHit(H(me, t.AddSeconds(-5), boss, 100_000));
        e.InjectHit(H(friend, t.AddSeconds(-4), dummyB, 300_000, 13010000));
        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 4: açık kimlik varken tek yerel satır benim", s.Players.Count(p => p.IsLocal) == 1 && P(s, me) is { IsLocal: true, Total: 100_000 } && P(s, friend) is null,
            Show(s) + $" | tahmin={d.InferLocalPlayer()}");
    }

    // MOTOR 4b: karakter kaydı YOK, iki isimsiz oyuncu, arkadaş 3 kat fazla beceri kullanıyor. Kanıt yok: tahmin arkadaşı seçer (bilinen sınır);
    // kayıtlı ad (YerelAdAyarla) bir kimliğe bağlıysa o kazanır.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        for (int i = 0; i < 3; i++) { d.NoteClass(friend, "Ranger"); }
        e.InjectHit(H(me, t.AddSeconds(-5), boss, 100_000));
        e.InjectHit(H(friend, t.AddSeconds(-4), boss, 300_000, 13010000));
        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 4b: kanıtsız tahmin: en çok bir yerel satır", s.Players.Count(p => p.IsLocal) <= 1, Show(s));

        var (e2, d2, _) = New();
        e2.YerelAdAyarla("Ben");
        d2.Register(me, "Ben"); // ör. roster eşleşmesiyle kendi adım bir kimliğe bağlandı
        d2.NoteClass(me, "Gladiator");
        d2.NoteClass(friend, "Ranger"); d2.NoteClass(friend, "Ranger"); d2.NoteClass(friend, "Ranger");
        e2.InjectHit(H(me, t.AddSeconds(-5), boss, 100_000));
        e2.InjectHit(H(friend, t.AddSeconds(-4), boss, 300_000, 13010000));
        MeterSnapshot s2 = e2.Snapshot();
        Check("MOTOR 4b: kayıtlı ad (YerelAdAyarla) tahmini yener", P(s2, me) is { IsLocal: true } && P(s2, friend) is not { IsLocal: true }, Show(s2));
    }

    // MOTOR 5: 6 sn vuruş, 5 sn ara, tekrar vuruş -> aktif süre (ara sayılmaz), DPS zıplamaz.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        for (int i = 10; i >= 5; i--) { e.InjectHit(H(me, t.AddSeconds(-i), 950, 10_000)); }
        MeterSnapshot s1 = e.Snapshot();
        e.InjectHit(H(me, t, 950, 10_000));
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 5: 5 sn ara aktif süreye girmez", s1.FightDuration == TimeSpan.FromSeconds(5) && s2.FightDuration == TimeSpan.FromSeconds(5) && P(s2, me)?.Dps == 14_000, Show(s2));
    }

    // MOTOR 6: yabancının çağrısı (Bittercold Wind) sahibinin adını taşıyor ama ad henüz bir kimliğe bağlı değil; ben partideki tek Sorcerer'ım.
    {
        var (e, d, s) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 25, 50, t));
        d.NoteClass(me, "Sorcerer");
        d.NoteParty(new[] { "Ben" }, t);
        d.HitPoints.Note(boss, t.AddSeconds(-20), 10_000_000);
        int wind = 7778;
        d.NoteSpawned(wind);
        d.SetSummonOwnerName(wind, "Yabanci");
        var segs = new List<byte[]> { Damage(boss, me, 15010000, 100_000), Damage(boss, wind, 15280002, 250_000), Damage(boss, wind, 15280002, 250_000) };
        s.Ingest(new TcpSegment(t.AddSeconds(-3), "1.1.1.1:13328", "2.2.2.2:5", 1, Wire(segs), true));
        int? before = d.SummonOwnerOf(wind);
        d.Register(8000, "Yabanci"); d.NoteClass(8000, "Sorcerer");
        MeterSnapshot snap = e.Snapshot();
        Check("MOTOR 6: adı taşınan yabancı çağrısı bana yazılmaz, ad gelince sahibine geçer",
            before is null && d.SummonOwnerOf(wind) == 8000 && P(snap, me)?.Total == 100_000, Show(snap));
    }

    // MOTOR 6b: solo, roster yok, açık dünya; doğuşu görülmemiş bir çağrı (Bittercold Wind), ben tek bilinen Sorcerer'ım.
    {
        var (e, d, s) = New();
        d.NoteClass(me, "Sorcerer");
        d.HitPoints.Note(boss, t.AddSeconds(-20), 10_000_000);
        var segs = new List<byte[]> { Damage(boss, me, 15010000, 100_000), Damage(boss, 7779, 15280002, 250_000) };
        s.Ingest(new TcpSegment(t.AddSeconds(-3), "1.1.1.1:13328", "2.2.2.2:5", 1, Wire(segs), true));
        MeterSnapshot snap = e.Snapshot();
        Check("MOTOR 6b: açık dünyada tek aday kuralı yok, çağrı bana yazılmaz", d.SummonOwnerOf(7779) is null && P(snap, me)?.Total == 100_000, Show(snap));
    }

    // MOTOR 7: kataloğda olmayan boss, canı ancak hedef alınınca geliyor: önceki vuruşlar da BossTotal'a girer.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        e.InjectHit(H(me, t.AddSeconds(-10), boss, 300_000));
        e.InjectHit(H(me, t.AddSeconds(-9), boss, 300_000));
        d.HitPoints.Note(boss, t.AddSeconds(-8), 9_400_000);
        e.InjectHit(H(me, t.AddSeconds(-7), boss, 300_000));
        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 7: can okunmadan önceki vuruşlar BossTotal'da, BossDps tutarlı",
            s.Boss?.Id == boss && P(s, me) is { BossTotal: 900_000 } p && Math.Abs(p.BossDps - 900_000 / Math.Max(1, s.FightDuration.TotalSeconds)) < 1, Show(s));
    }

    // MOTOR 8: roster yok; dövüşüm bitti (donmuş sonuç), bir yabancı benim vurduğum kuklaya vuruyor: yeni dövüş başlamaz, sonucum kalır.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Yabanci"); d.NoteClass(friend, "Ranger");
        e.InjectHit(H(me, t.AddSeconds(-40), 950, 500_000));
        e.InjectHit(H(me, t.AddSeconds(-30), 950, 500_000));
        MeterSnapshot s1 = e.Snapshot();
        e.InjectHit(H(friend, t.AddSeconds(-1), 950, 1_000, 13010000));
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 8: yabancı donmuş sonucu silmez", s1.FightOver && s2.FightOver && P(s2, me)?.Total == 1_000_000 && P(s2, friend) is null, Show(s2));
    }

    // MOTOR 9: yaralı bırakılan kukla (katalog dışı), 60 sn ara, yeniden vuruş: kısa tutma, yeni dövüş.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.HitPoints.Note(boss, t.AddSeconds(-71), 10_000_000);
        for (int i = 70; i >= 65; i--) { e.InjectHit(H(me, t.AddSeconds(-i), boss, 100_000)); }
        d.HitPoints.Note(boss, t.AddSeconds(-65), 9_400_000);
        MeterSnapshot s1 = e.Snapshot();
        e.InjectHit(H(me, DateTime.Now, boss, 100_000));
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 9: yaralı kukla dövüşü tutmaz; 60 sn sonra yeni dövüş", s1.FightOver && !s2.FightOver && P(s2, me)?.Total == 100_000, Show(s2));
    }

    // MOTOR 9b: aynı durum, katalog boss'u: dövüş tutulur; taze okuma gelmeden can bilinmiyor, okuma wipe göstermezse denemeler birleşir.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-71), 10_000_000);
        for (int i = 70; i >= 65; i--) { e.InjectHit(H(me, t.AddSeconds(-i), boss, 100_000)); }
        d.HitPoints.Note(boss, t.AddSeconds(-65), 9_400_000);
        MeterSnapshot s1 = e.Snapshot();
        e.InjectHit(H(me, DateTime.Now, boss, 100_000));
        MeterSnapshot s2 = e.Snapshot();
        d.HitPoints.Note(boss, DateTime.Now, 9_300_000);
        MeterSnapshot s3 = e.Snapshot();
        Check("MOTOR 9b: katalog boss'u tutar, şüphede can bilinmiyor, wipe değilse birleşir",
            !s1.FightOver && s2.Boss is { CanBilinmiyor: true, TimeToKill: null } && s3.Boss is { CanBilinmiyor: false } && P(s3, me)?.Total == 700_000
            && s3.FightDuration == TimeSpan.FromSeconds(5), Show(s2) + " >> " + Show(s3));
    }

    foreach (bool freshWithFirstHit in new[] { false, true })
    {
        // MOTOR 10a/10b: wipe — katalog boss'u 10M→2M, herkes öldü (40 sn kimse vurmadı), boss canı doldu.
        string name = freshWithFirstHit ? "MOTOR 10b" : "MOTOR 10a";
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        var t0 = DateTime.Now.AddSeconds(-100);
        d.HitPoints.Note(boss, t0, 10_000_000);
        for (int i = 0; i < 40; i++) { e.InjectHit(H(me, t0.AddSeconds(i), boss, 200_000)); d.HitPoints.Note(boss, t0.AddSeconds(i), 10_000_000 - (i + 1) * 200_000); }
        MeterSnapshot s1 = e.Snapshot();
        var t1 = t0.AddSeconds(80);
        if (freshWithFirstHit) { d.HitPoints.Note(boss, t1, 10_000_000); }
        for (int i = 0; i < 10; i++)
        {
            e.InjectHit(H(me, t1.AddSeconds(i), boss, 200_000));
            if (freshWithFirstHit) { d.HitPoints.Note(boss, t1.AddSeconds(i), 10_000_000 - (i + 1) * 200_000); }
        }

        MeterSnapshot s2 = e.Snapshot();
        double rage = Aion2BossRage.SecondsFor(catalogNpc);
        bool newAttempt = P(s2, me)?.Total == 2_000_000 && s2.FightDuration == TimeSpan.FromSeconds(9) && s2.Boss?.TimeToRage is { } ttr && ttr.TotalSeconds > rage - 30;
        Check(freshWithFirstHit
                ? $"{name}: dolu can okuması ilk vuruşla geliyor -> yeni deneme, can ve kalan süre yeni"
                : $"{name}: dolu can okuması hiç gelmiyor -> yeni deneme, can bilinmiyor (kalan süre yok)",
            P(s1, me)?.Total == 8_000_000 && newAttempt && (freshWithFirstHit
                ? s2.Boss is { CanBilinmiyor: false, Current: 8_000_000 } && s2.Boss.TimeToKill is not null
                : s2.Boss is { CanBilinmiyor: true, TimeToKill: null }),
            Show(s2) + $" öfke={s2.Boss?.TimeToRage} kalan={s2.Boss?.TimeToKill}");
        if (!freshWithFirstHit)
        {
            d.HitPoints.Note(boss, DateTime.Now, 8_100_000); // taze okuma: boss sıfırlanmıştı
            MeterSnapshot s3 = e.Snapshot();
            Check($"{name}: taze okuma yüksek -> wipe doğrulandı, yeni deneme sürer", P(s3, me)?.Total == 2_000_000 && s3.Boss is { CanBilinmiyor: false } && !s3.FightOver, Show(s3));
        }
    }

    // MOTOR 10c: wipe kural (ii) — aynı boss NPC'si yeni bir varlık kimliğiyle doğuyor / vuruluyor.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-29), boss, 1_000_000));
        d.HitPoints.Note(boss, t.AddSeconds(-29), 9_000_000);
        e.Snapshot();
        d.NoteRemoved(boss, 7, t.AddSeconds(-20)); // takım öldü, boss sıfırlanıp kayboldu
        d.RegisterNpc(905, catalogNpc); // aynı boss, yeni kimlik
        MeterSnapshot s1 = e.Snapshot();
        d.HitPoints.Note(905, t.AddSeconds(-5), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-4), 905, 300_000));
        d.HitPoints.Note(905, t.AddSeconds(-4), 9_700_000);
        MeterSnapshot s2 = e.Snapshot();
        Check("MOTOR 10c: aynı boss yeni kimlikle doğdu -> deneme kapanır, yeni deneme yeni kimlikte",
            s1.FightOver && P(s1, me)?.Total == 1_000_000 && !s2.FightOver && s2.Boss?.Id == 905 && P(s2, me) is { Total: 300_000, BossTotal: 300_000 }, Show(s2));

    }

    // Slot: boss dövüşünde normal canavarlara (mob) vurmak ve onların ölümü dövüşü bozmaz.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-29), boss, 1_000_000));
        d.HitPoints.Note(boss, t.AddSeconds(-29), 9_000_000);
        for (int mob = 3000; mob < 3005; mob++)
        {
            d.HitPoints.Note(mob, t.AddSeconds(-20), 40_000);
            e.InjectHit(H(me, t.AddSeconds(-19), mob, 40_000));
            d.HitPoints.Note(mob, t.AddSeconds(-19), 0);
            d.NoteRemoved(mob, Aion2EntityDirectory.RemovedDead, t.AddSeconds(-18));
        }

        MeterSnapshot s = e.Snapshot();
        Check("slot: mob'lara vurmak ve ölümleri boss dövüşünü bozmaz", !s.FightOver && s.Boss?.Id == boss && P(s, me) is { Total: 1_200_000, BossTotal: 1_000_000 }, Show(s));
    }

    // MOTOR 10d: wipe kural (i) — parti vuruşu kesildikten (8+ sn) sonra dövüş boss'unun okuması belirgin yükseldi (sıfırlandı).
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-21), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-20), boss, 3_000_000));
        d.HitPoints.Note(boss, t.AddSeconds(-20), 7_000_000);
        e.SnapshotAt(t.AddSeconds(-20));
        d.HitPoints.Note(boss, t.AddSeconds(-19), 7_900_000); // %9 iyileşme: wipe değil
        MeterSnapshot heal = e.SnapshotAt(t.AddSeconds(-19));
        d.HitPoints.Note(boss, t.AddSeconds(-10), 10_000_000);
        MeterSnapshot s1 = e.SnapshotAt(t.AddSeconds(-10));
        e.InjectHit(H(me, t.AddSeconds(-6), boss, 100_000));
        MeterSnapshot s2 = e.SnapshotAt(t.AddSeconds(-6));
        Check("MOTOR 10d: vuruşsuz aradan sonra can sıçraması -> deneme kapanır, sonraki vuruş yeni deneme (küçük iyileşme wipe sayılmaz)",
            !heal.FightOver && s1.FightOver && P(s1, me)?.Total == 3_000_000 && !s2.FightOver && P(s2, me)?.Total == 100_000, Show(s1) + " >> " + Show(s2));
    }

    // MOTOR 10e: wipe kural (i) — parti vururken can sıçradı ama bilinen parti üyelerinin hepsi (yerel oyuncu) son 60 sn'de öldü.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-11), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-10), boss, 3_000_000));
        d.HitPoints.Note(boss, t.AddSeconds(-10), 7_000_000);
        e.SnapshotAt(t.AddSeconds(-10));
        d.HitPoints.Note(me, t.AddSeconds(-9), 30_000);
        d.HitPoints.Note(me, t.AddSeconds(-8), 0); // öldüm
        e.InjectHit(H(me, t.AddSeconds(-6), boss, 1_000)); // ölmeden önce koyduğum bir tik (vuruş kesilmedi)
        d.HitPoints.Note(boss, t.AddSeconds(-5), 10_000_000);
        MeterSnapshot s = e.SnapshotAt(t.AddSeconds(-5));
        Check("MOTOR 10e: herkes öldü + can sıçradı -> wipe", s.FightOver && P(s, me) is { Total: 3_001_000, Olum: 1 }, Show(s));
    }

    foreach (bool record in new[] { true, false })
    {
        // MOTOR 11a/11b: PARTİSİZ, yan yana iki kişi iki ayrı kuklaya.
        string name = record ? "MOTOR 11a (karakter kaydı var)" : "MOTOR 11b (karakter kaydı yok)";
        var (e, d, _) = New();
        if (record) { d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t)); }
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Yabanci"); d.NoteClass(friend, "Ranger");
        d.HitPoints.Note(boss, t.AddSeconds(-20), 10_000_000);
        d.HitPoints.Note(dummyB, t.AddSeconds(-20), 10_000_000);
        for (int i = 10; i >= 1; i--)
        {
            e.InjectHit(H(friend, t.AddSeconds(-i), dummyB, 80_000, 13010000));
            e.InjectHit(H(me, t.AddSeconds(-i), boss, 50_000));
            d.HitPoints.Note(boss, t.AddSeconds(-i), 10_000_000 - (11 - i) * 50_000);
            d.HitPoints.Note(dummyB, t.AddSeconds(-i), 10_000_000 - (11 - i) * 80_000);
        }

        d.SetLocalTarget(boss);
        MeterSnapshot s = e.Snapshot();
        Check($"{name}: kendi hasarım bozulmaz", s.Players.Count == 1 && P(s, me) is { IsLocal: true, Total: 500_000, BossTotal: 500_000 } && s.Boss?.Id == boss, Show(s));
    }

    // MOTOR 11c: partisiz, karakter kaydı yok, yandaki isimsiz oyuncu 3 kat fazla beceri kullanıyor; ben kendi kuklamı işaretledim.
    {
        var (e, d, _) = New();
        e.LocalCacheMs = 0; // gerçekte saniyede bir yenilenir; sentetik senaryo milisaniyeler içinde
        d.HitPoints.Note(boss, t.AddSeconds(-20), 10_000_000);
        d.HitPoints.Note(dummyB, t.AddSeconds(-20), 10_000_000);
        d.SetLocalTarget(boss);
        for (int i = 10; i >= 1; i--)
        {
            for (int k = 0; k < 3; k++) { d.NoteClass(friend, "Ranger"); d.NoteMonsterHit(friend, dummyB, 13010000); }
            d.NoteClass(me, "Gladiator"); d.NoteMonsterHit(me, boss, 11010000);
            e.InjectHit(H(friend, t.AddSeconds(-i), dummyB, 80_000, 13010000));
            e.InjectHit(H(me, t.AddSeconds(-i), boss, 50_000));
            e.Snapshot(); // overlay saniyede birkaç kez okur: yerel kimlik her okumada yenilenir
        }

        MeterSnapshot s = e.Snapshot();
        Check("MOTOR 11c: işaretlediğim kuklaya vuran isimsiz oyuncu benim (tahmin)", d.InferLocalPlayer() == me && P(s, me) is { IsLocal: true, Total: 500_000 } && P(s, friend) is null, Show(s));
    }
}

void FrameChecks()
{
    Console.WriteLine("[çerçeve: vuruş sayısı işaretçisi]");
    foreach (var (title, frame, expected) in new[]
    {
        ("normal (bit 0, yön 2)", Damage(49668, 1086, 11020030, 381), 381L),
        ("perfect bit 4, yön 0, float alt baytı 0", Damage(49668, 1086, 11020030, 381, bits: 0x04, dir: 0x00, floatBits: 0x41af3b00), 381L),
        ("yön 2, float 2.0 (00 00 00 40)", Damage(49668, 1086, 11020030, 381, dir: 0x02, floatBits: 0x40000000), 381L),
        ("double bit 8, yön 0, float 0x41af3b00", Damage(49668, 1086, 11020030, 45_000, bits: 0x08, dir: 0x00, floatBits: 0x41af3b00), 45_000L),
        ("tür 4 (yalnız float)", Damage(49668, 1086, 11020030, 777, flags: 0x04), 777L),
    })
    {
        var src = new Aion2PacketCombatSource(Proto());
        var got = new List<long>();
        src.Hit += e => got.Add(e.Amount);
        src.Ingest(new TcpSegment(DateTime.Now, "1.1.1.1:13328", "2.2.2.2:5", 1, Wire(new[] { frame }), true));
        Check($"{title}: {expected}", got.SequenceEqual(new[] { expected }), $"çözülen [{string.Join(",", got)}]");
    }
}

void TcpChecks()
{
    Console.WriteLine("[TCP: süreli boşluk ve parça zamanları]");
    const int N = 400;
    var frames = Enumerable.Range(0, N).Select(i => Damage(49668, 1086, 11020030, 1000 + i)).ToArray();
    byte[] wire = Wire(frames);
    var expected = Enumerable.Range(0, N).Select(i => 1000L + i).ToHashSet();

    List<(int Start, byte[] Data)> Cut(Random r)
    {
        var segs = new List<(int, byte[])>();
        for (int at = 0; at < wire.Length;)
        {
            int len = Math.Min(r.Next(30, 160), wire.Length - at);
            segs.Add((at, wire[at..(at + len)]));
            at += len;
        }

        return segs;
    }

    DateTime t0 = DateTime.Now;
    (List<(int Seg, long Amount, DateTime At)> Out, Aion2PacketCombatSource Src) Feed(IEnumerable<(int Start, byte[] Data)> segs, bool tickAfter = false)
    {
        var src = new Aion2PacketCombatSource(Proto());
        var outp = new List<(int, long, DateTime)>();
        int idx = 0;
        src.Hit += e => outp.Add((idx, e.Amount, e.Timestamp));
        foreach (var (start, data) in segs)
        {
            src.Ingest(new TcpSegment(t0.AddMilliseconds(idx * 20), "1.1.1.1:13328", "2.2.2.2:5", unchecked(1000u + (uint)start), data, true));
            idx++;
        }

        if (tickAfter)
        {
            src.FlushAt(t0.AddMilliseconds(idx * 20 + 400)); // paket saatinde 400 ms sonra
        }

        return (outp, src);
    }

    var segs0 = Cut(new Random(1));
    var (o0, _) = Feed(segs0);
    Check("sıralı akış: hepsi çözülür", o0.Count == N && o0.Sum(x => x.Amount) == expected.Sum());

    {
        int drop = 20;
        var (o, _) = Feed(segs0.Where((_, i) => i != drop));
        var after = o.Where(x => x.Seg >= drop).ToList();
        int firstOut = after.Count > 0 ? after.Min(x => x.Seg) : -1;
        var burst = o.Where(x => x.Seg == firstOut).ToList();
        Check("bir parça kayıp: bekleme ~300 ms ile sınırlı, çerçeveler kendi parça zamanlarıyla",
            firstOut >= 0 && (firstOut - drop) * 20 <= 360 && burst.Select(x => x.At).Distinct().Count() > 1 && o.All(x => expected.Contains(x.Amount)),
            $"ilk çıkış parçası #{firstOut} ({(firstOut - drop) * 20} ms), çözülen {o.Count}/{N}");
    }

    {
        int drop = 100;
        var segs = segs0.Take(drop + 11).Where((_, i) => i != drop).ToList();
        var (noTick, _) = Feed(segs);
        var (withTick, _) = Feed(segs, tickAfter: true);
        Check("kayıptan sonra trafik azalınca: zaman dolunca bekleyenler çözülür (Tick)", withTick.Count > noTick.Count && withTick.All(x => expected.Contains(x.Amount)),
            $"Tick yok {noTick.Count}, Tick var {withTick.Count}");
    }

    {
        int garbage = 0, dup = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            var r = new Random(1000 + trial);
            var segs = Cut(r);
            var drops = Enumerable.Range(0, r.Next(1, 4)).Select(_ => r.Next(1, segs.Count - 1)).ToHashSet();
            var (o, _) = Feed(segs.Where((_, i) => !drops.Contains(i)), tickAfter: true);
            garbage += o.Count(x => !expected.Contains(x.Amount));
            dup += o.GroupBy(x => x.Amount).Count(g => g.Count() > 1);
        }

        Check("300 rastgele kayıplı deneme: çöp sayı ve çift sayım yok", garbage == 0 && dup == 0, $"çöp {garbage}, çift {dup}");
    }

    {
        var segs = segs0.ToList();
        (segs[10], segs[11]) = (segs[11], segs[10]);
        var (o, _) = Feed(segs);
        Check("sıra dışı iki komşu parça: hepsi çözülür", o.Count == N);
    }
}

void OtherChecks()
{
    Console.WriteLine("[diğer]");
    DateTime t = DateTime.Now;
    const int me = 500, friend = 600, boss = 900;
    const int catalogNpc = 2300708;
    DamageEvent H(int src, DateTime at, int target, long amount, int skill = 11010000) => new(at, src, target, amount, false, SkillId: skill);

    // Upstream a2b1c38: doğuş değeri zaten grup değeriyse ikinci kez çarpılmaz.
    Check("boss canı: doğuş zaten 5 x 61.500 -> 307.500 (iki kez çarpılmaz)", Aion2BossHp.Compute(307_500, 61_500, true, 5, 306_000, 306_000) == 307_500);
    Check("boss canı: doğuş tek kişilik, grup 3 -> 184.500", Aion2BossHp.Compute(61_500, 61_500, true, 3, 73_800, 73_800) == 184_500);

    // Ölüm sayacı: 0'ın üstünden 0'a inen okuma bir ölüm; tekrar eden 0 sayılmaz; diriliş sonrası yeni 0 ikinci ölüm; wipe yeni denemede sıfırlar.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Arkadas"); d.NoteClass(friend, "Cleric");
        d.NoteParty(new[] { "Ben", "Arkadas" }, t);
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        e.InjectHit(H(me, t.AddSeconds(-29), boss, 1_000_000));
        d.HitPoints.Note(boss, t.AddSeconds(-29), 9_000_000);
        d.HitPoints.Note(me, t.AddSeconds(-28), 40_000);
        d.HitPoints.Note(me, t.AddSeconds(-27), 0);
        d.HitPoints.Note(me, t.AddSeconds(-26), 0);
        d.HitPoints.Note(me, t.AddSeconds(-20), 20_000);
        d.HitPoints.Note(me, t.AddSeconds(-19), 0);
        d.HitPoints.Note(friend, t.AddSeconds(-25), 30_000);
        d.HitPoints.Note(friend, t.AddSeconds(-24), 0);
        MeterSnapshot s1 = e.Snapshot();
        bool counted = s1.Players.FirstOrDefault(p => p.Id == me)?.Olum == 2 && s1.Players.FirstOrDefault(p => p.Name == "Arkadas")?.Olum == 1;
        d.HitPoints.Note(boss, t.AddSeconds(-5), 10_000_000); // wipe: boss sıfırlandı
        e.InjectHit(H(me, t.AddSeconds(-4), boss, 10_000));
        MeterSnapshot s2 = e.Snapshot();
        Check("ölüm sayacı: tek ölüm bir kez, diriliş sonrası yeni ölüm; wipe yeni denemede sıfır",
            counted && s2.Players.All(p => p.Olum == 0) && s2.Players.FirstOrDefault(p => p.Id == me)?.Total == 10_000, Show(s1) + " >> " + Show(s2));
    }

    // Savaşta: son 8 sn içinde yerel/parti vuruşu.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        e.InjectHit(H(me, t, 950, 1_000));
        Check("Savasta: 5 sn sonra true, 9 sn sonra false", e.SnapshotAt(t.AddSeconds(5)).Savasta && !e.SnapshotAt(t.AddSeconds(9)).Savasta);
    }

    // YerelAd: karakter kaydından kesin ad; kayıt yoksa null.
    {
        var (e, d, _) = New();
        bool none = e.Snapshot().YerelAd is null;
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        Check("YerelAd: karakter kaydından", none && e.Snapshot().YerelAd == "Ben");
    }

    // Tanı kaydı: dövüş başlangıç/bitiş, wipe, yerel kimlik ve dakikalık sayaçlar yazılır; kapalıyken hiçbir şey yazılmaz.
    {
        var lines = new List<string>();
        MeterTani.Yaz = lines.Add;
        try
        {
            var (e, d, s) = New();
            d.NoteClass(me, "Gladiator");
            d.RegisterNpc(boss, catalogNpc);
            d.HitPoints.Note(boss, t.AddSeconds(-10), 10_000_000);
            e.InjectHit(H(me, t.AddSeconds(-9), boss, 3_000_000));
            d.HitPoints.Note(boss, t.AddSeconds(-9), 7_000_000);
            e.Snapshot();
            d.HitPoints.Note(boss, t.AddSeconds(-0.5), 10_000_000); // 8,5 sn vuruşsuz aradan sonra sıfırlandı
            e.Snapshot();
            s.Tick(DateTime.Now.AddMinutes(2));
        }
        finally
        {
            MeterTani.Yaz = null;
        }

        foreach (string line in lines)
        {
            Console.WriteLine("    tanı> " + line);
        }

        Check("tanı: başlangıç, boss, wipe, yerel kimlik, sayaç satırları",
            lines.Any(l => l.Contains("dövüş başladı")) && lines.Any(l => l.Contains("wipe")) && lines.Any(l => l.Contains("yerel kimlik"))
            && lines.Any(l => l.Contains("dövüş boss'u")) && lines.Any(l => l.Contains("sayaç")));
    }
}

// Bağımsız denetim bulguları (D senaryoları): her biri bir açığı yeniden üretir; beklenen = düzeltilmiş davranış.
void AuditChecks()
{
    Console.WriteLine("[denetim bulguları]");
    const int me = 500, boss = 900, twin = 905, trash = 950;
    const int catalogNpc = 2300708;
    DateTime t = DateTime.Now;
    DamageEvent H(int src, DateTime at, int target, long amount, int skill = 11010000) => new(at, src, target, amount, false, SkillId: skill);
    PlayerDamage? P(MeterSnapshot s, int id) => s.Players.FirstOrDefault(p => p.Id == id);

    // D1: aynı NPC'den iki canlı varlık (ikiz / kopya); ikincisine vurmak wipe değildir.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        d.HitPoints.Note(twin, t.AddSeconds(-30), 10_000_000);
        d.RegisterNpc(twin, catalogNpc);
        e.SnapshotAt(t.AddSeconds(-29));
        for (int i = 0; i < 10; i++) { e.InjectHit(H(me, t.AddSeconds(-29 + i), boss, 500_000)); d.HitPoints.Note(boss, t.AddSeconds(-29 + i), 10_000_000 - (i + 1) * 500_000); }
        e.SnapshotAt(t.AddSeconds(-19));
        e.InjectHit(H(me, t.AddSeconds(-18), twin, 100_000)); d.HitPoints.Note(twin, t.AddSeconds(-18), 9_900_000);
        MeterSnapshot s = e.SnapshotAt(t.AddSeconds(-18));
        Check("D1: iki canlı kopyadan ikincisine vurmak yeni deneme başlatmaz", !s.FightOver && P(s, me)?.Total == 5_100_000, Show(s));
    }

    // D1b: dövüş sırasında aynı NPC'den ikinci varlık doğuyor (eskisi canlı, yerinde): deneme kapanmaz.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        for (int i = 0; i < 10; i++) { e.InjectHit(H(me, t.AddSeconds(-29 + i), boss, 500_000)); d.HitPoints.Note(boss, t.AddSeconds(-29 + i), 10_000_000 - (i + 1) * 500_000); }
        e.SnapshotAt(t.AddSeconds(-19));
        d.RegisterNpc(twin, catalogNpc);
        MeterSnapshot s3 = e.SnapshotAt(t.AddSeconds(-19));
        e.InjectHit(H(me, t.AddSeconds(-18), boss, 100_000)); d.HitPoints.Note(boss, t.AddSeconds(-18), 4_900_000);
        MeterSnapshot s4 = e.SnapshotAt(t.AddSeconds(-18));
        Check("D1b: eskisi canlıyken kopya doğması wipe değil", !s3.FightOver && !s4.FightOver && P(s4, me)?.Total == 5_100_000, Show(s4));
    }

    // D2: boss kendini %10 iyileştiriyor (%76 -> %86), parti vurmaya devam ediyor: wipe değil.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        for (int i = 0; i < 12; i++) { e.InjectHit(H(me, t.AddSeconds(-29 + i), boss, 200_000)); d.HitPoints.Note(boss, t.AddSeconds(-29 + i), 10_000_000 - (i + 1) * 200_000); }
        e.SnapshotAt(t.AddSeconds(-17));
        d.HitPoints.Note(boss, t.AddSeconds(-16.5), 8_600_000);
        e.InjectHit(H(me, t.AddSeconds(-16), boss, 200_000));
        MeterSnapshot s = e.SnapshotAt(t.AddSeconds(-16));
        Check("D2: parti vururken kendini iyileştirme wipe sayılmaz", !s.FightOver && P(s, me)?.Total == 2_600_000, Show(s));
    }

    // D3: faz değişimi: can %50 -> %100 (aynı varlık), parti sürekli vuruyor: wipe değil.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.HitPoints.Note(boss, t.AddSeconds(-30), 10_000_000);
        for (int i = 0; i < 10; i++) { e.InjectHit(H(me, t.AddSeconds(-29 + i), boss, 500_000)); d.HitPoints.Note(boss, t.AddSeconds(-29 + i), 10_000_000 - (i + 1) * 500_000); }
        e.SnapshotAt(t.AddSeconds(-19));
        d.HitPoints.Note(boss, t.AddSeconds(-18.5), 10_000_000);
        MeterSnapshot s2 = e.SnapshotAt(t.AddSeconds(-18.5));
        e.InjectHit(H(me, t.AddSeconds(-18), boss, 500_000));
        MeterSnapshot s3 = e.SnapshotAt(t.AddSeconds(-18));
        Check("D3: parti vururken faz değişimi (%50 -> %100) wipe sayılmaz", !s2.FightOver && !s3.FightOver && P(s3, me)?.Total == 5_500_000, Show(s3));
    }

    // D4: 16 sn ara (wipe şüphesi açılır), son vuruş öldürüyor, ara sonrası ilk okuma 0: aradan önceki hasar atılmaz.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        var t0 = t.AddSeconds(-100);
        d.HitPoints.Note(boss, t0, 10_000_000);
        for (int i = 0; i < 40; i++) { e.InjectHit(H(me, t0.AddSeconds(i), boss, 240_000)); d.HitPoints.Note(boss, t0.AddSeconds(i), 10_000_000 - (i + 1) * 240_000); }
        e.SnapshotAt(t0.AddSeconds(40));
        var t1 = t0.AddSeconds(56);
        e.InjectHit(H(me, t1, boss, 400_000));
        d.HitPoints.Note(boss, t1, 0);
        MeterSnapshot s = e.SnapshotAt(t1.AddMilliseconds(100));
        Check("D4: şüphe açıkken boss öldü -> denemeler birleşir (10.000.000)", s.FightOver && s.Boss is { Dead: true } && P(s, me)?.Total == 10_000_000, Show(s));
        PlayerDetail? detail = e.Detail(me);
        Check("D4: detay satırla aynı denemeden (boss modu)", detail is { Total: 10_000_000 } && detail.Hits.Hits == 41, $"detay toplam {detail?.Total:N0}, vuruş {detail?.Hits.Hits}");
    }

    // D5: 10 sn trash, ardından 10 sn boss: BossDps paydası boss'a vurulan aktif süre.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        var t0 = t.AddSeconds(-60);
        d.HitPoints.Note(trash, t0, 100_000);
        d.HitPoints.Note(boss, t0, 10_000_000);
        for (int i = 0; i <= 10; i++) { e.InjectHit(H(me, t0.AddSeconds(i), trash, 5_000)); }
        for (int i = 11; i <= 21; i++) { e.InjectHit(H(me, t0.AddSeconds(i), boss, 100_000)); d.HitPoints.Note(boss, t0.AddSeconds(i), 10_000_000 - (i - 10) * 100_000); }
        MeterSnapshot s = e.SnapshotAt(t0.AddSeconds(21));
        Check("D5: BossDps = boss'a vurulan ÷ boss'a vurulan aktif süre (~110.000)", P(s, me) is { BossTotal: 1_100_000 } p && Math.Abs(p.BossDps - 110_000) < 1, Show(s));
    }

    // D6: iki farklı katalog boss'u aynı anda, parti hedef değiştiriyor: BossTotal kümenin toplamı, seçili boss değişince zıplamaz.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, catalogNpc);
        d.RegisterNpc(twin, 2300104); // Enhanced Harcon
        var t0 = t.AddSeconds(-90);
        d.HitPoints.Note(boss, t0, 10_000_000);
        d.HitPoints.Note(twin, t0, 10_000_000);
        long hb = 10_000_000, ht = 10_000_000;
        for (int i = 0; i < 20; i++) { e.InjectHit(H(me, t0.AddSeconds(i), boss, 100_000)); hb -= 100_000; d.HitPoints.Note(boss, t0.AddSeconds(i), hb); }
        MeterSnapshot a = e.SnapshotAt(t0.AddSeconds(20));
        for (int i = 20; i < 45; i++) { e.InjectHit(H(me, t0.AddSeconds(i), twin, 100_000)); ht -= 100_000; d.HitPoints.Note(twin, t0.AddSeconds(i), ht); }
        MeterSnapshot b = e.SnapshotAt(t0.AddSeconds(45));
        Check("D6: iki katalog boss'u: BossTotal kümenin toplamı (2.000.000 -> 4.500.000)", P(a, me)?.BossTotal == 2_000_000 && P(b, me)?.BossTotal == 4_500_000 && !b.FightOver,
            Show(a) + " >> " + Show(b));
    }

    // D8: TCP: kuyruk 400 ms gerideyken (Tick duvar saatiyle) yer değiştiren iki parça: boşluk erken bırakılmamalı.
    {
        var w = new List<byte>();
        for (int i = 0; i < 60; i++) { byte[] f = Damage(49668, 1086, 11020030, 1000 + i); Varint(w, f.Length + 4); w.AddRange(f); }
        byte[] wire = w.ToArray();
        var segs = new List<(int Start, byte[] Data)>();
        for (int at = 0; at < wire.Length; at += 100) { segs.Add((at, wire[at..Math.Min(at + 100, wire.Length)])); }
        (segs[10], segs[11]) = (segs[11], segs[10]);
        int RunFeed(bool wallTick)
        {
            var src = new Aion2PacketCombatSource(Proto());
            int n = 0;
            src.Hit += _ => n++;
            DateTime pkt0 = DateTime.Now.AddSeconds(-5);
            for (int i = 0; i < segs.Count; i++)
            {
                DateTime pktAt = pkt0.AddMilliseconds(i * 2);
                src.Ingest(new TcpSegment(pktAt, "1.1.1.1:13328", "2.2.2.2:5", unchecked(1000u + (uint)segs[i].Start), segs[i].Data, true));
                if (wallTick && i == 10) { src.Tick(pktAt.AddMilliseconds(400)); }
            }

            return n;
        }

        int plain = RunFeed(false), ticked = RunFeed(true);
        Check("D8: kuyruk gerideyken Tick boşluğu erken bırakmaz (60/60)", plain == 60 && ticked == 60, $"Tick yok {plain}/60, Tick ile {ticked}/60");
    }

    // D10: ikili katalog boss'u (Rahu + Ketu): biri ölünce dövüş bitmez, diğerine geçer; BossTotal ikisinin toplamı.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        d.RegisterNpc(boss, 2300282);
        d.RegisterNpc(twin, 2300283);
        var t0 = t.AddSeconds(-90);
        d.HitPoints.Note(boss, t0, 5_000_000);
        d.HitPoints.Note(twin, t0, 5_000_000);
        for (int i = 0; i < 10; i++)
        {
            e.InjectHit(H(me, t0.AddSeconds(i), boss, 500_000)); d.HitPoints.Note(boss, t0.AddSeconds(i), 5_000_000 - (i + 1) * 500_000);
            e.InjectHit(H(me, t0.AddSeconds(i), twin, 100_000)); d.HitPoints.Note(twin, t0.AddSeconds(i), 5_000_000 - (i + 1) * 100_000);
        }

        MeterSnapshot a = e.SnapshotAt(t0.AddSeconds(10));
        e.InjectHit(H(me, t0.AddSeconds(11), twin, 100_000)); d.HitPoints.Note(twin, t0.AddSeconds(11), 3_900_000);
        MeterSnapshot b = e.SnapshotAt(t0.AddSeconds(11));
        Check("D10: ikili boss'un biri ölünce dövüş diğeriyle sürer, BossTotal toplam",
            !a.FightOver && a.Boss?.Id == twin && !b.FightOver && P(b, me) is { Total: 6_100_000, BossTotal: 6_100_000 }, Show(a) + " >> " + Show(b));
    }

    // D11: partisiz, açık dünya; 50M'lik katalog dışı boss benim dövüş boss'um. Bir kez vurduğum 600k'lık elite yabancılar (sözde parti) çok vuruyor.
    {
        var (e, d, _) = New();
        d.NoteClass(me, "Gladiator");
        int elite = 951, world = 990;
        var t0 = t.AddSeconds(-40);
        d.HitPoints.Note(world, t0, 50_000_000);
        d.HitPoints.Note(elite, t0, 600_000);
        for (int k = 0; k < 4; k++) { d.Register(700 + k, "Yabanci" + k); d.NoteClass(700 + k, "Ranger"); }
        long hw = 50_000_000, he = 600_000;
        for (int i = 0; i < 20; i++)
        {
            e.InjectHit(H(me, t0.AddSeconds(i), world, 5_000)); hw -= 5_000; d.HitPoints.Note(world, t0.AddSeconds(i), hw);
            if (i == 5) { e.InjectHit(H(me, t0.AddSeconds(i), elite, 5_000)); he -= 5_000; d.HitPoints.Note(elite, t0.AddSeconds(i), he); }
            if (i > 5) { for (int k = 0; k < 4; k++) { e.InjectHit(H(700 + k, t0.AddSeconds(i), elite, 8_000, 13010000)); he -= 8_000; } d.HitPoints.Note(elite, t0.AddSeconds(i), he); }
            e.SnapshotAt(t0.AddSeconds(i));
        }

        MeterSnapshot s = e.SnapshotAt(t0.AddSeconds(20));
        Check("D11: sözde parti dövüş boss'unu ve BossTotal'ımı değiştirmez", s.Boss?.Id == world && P(s, me)?.BossTotal == 100_000, Show(s));
    }
}

// Gerçek tanı kaydı (06:16:45): hedefi oyuncu olan vuruş (yerel oyuncunun kendisine) donmuş sonuçtan sonra yeni dövüş başlatmıştı. Ölçer PvE.
void PlayerTargetChecks()
{
    Console.WriteLine("[oyuncuya vuruşlar (PvE)]");
    const int me = 4409, friend = 600, stranger = 777, boss = 900, mySummon = 7000;
    DateTime t = DateTime.Now;
    DamageEvent H(int src, DateTime at, int target, long amount, int skill = 11010000) => new(at, src, target, amount, false, SkillId: skill);
    PlayerDamage? P(MeterSnapshot s, int id) => s.Players.FirstOrDefault(p => p.Id == id);

    // a) Boss öldü, donmuş sonuç; sonra yerel oyuncuya (kendine), parti üyesine ve bir yabancı oyuncuya vuruşlar, çağrımın arkadaşa vuruşu.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.Register(friend, "Arkadas"); d.NoteClass(friend, "Cleric");
        d.NoteParty(new[] { "Ben", "Arkadas" }, t);
        d.Register(stranger, "Yabanci"); d.NoteClass(stranger, "Ranger");
        d.NoteSpawned(mySummon); d.SetSummonOwner(mySummon, me);
        d.RegisterNpc(boss, 2300708);
        d.HitPoints.Note(boss, t.AddSeconds(-60), 5_000_000);
        e.InjectHit(H(me, t.AddSeconds(-59), boss, 300_000));
        d.HitPoints.Note(boss, t.AddSeconds(-58), 0);
        MeterSnapshot dead = e.SnapshotAt(t.AddSeconds(-58));
        e.InjectHit(H(me, t.AddSeconds(-30), me, 5_000));        // kendine gelen hasar
        e.InjectHit(H(friend, t.AddSeconds(-30), me, 5_000, 17010000));
        e.InjectHit(H(me, t.AddSeconds(-30), friend, 5_000));
        e.InjectHit(H(me, t.AddSeconds(-30), stranger, 5_000));  // PvP
        e.InjectHit(H(me, t.AddSeconds(-30), mySummon, 5_000));  // bir oyuncunun çağrısı
        MeterSnapshot after = e.SnapshotAt(t.AddSeconds(-29));
        Check("oyuncuya vuruşlar donmuş sonucu silmez, Savasta'yı tetiklemez", dead.FightOver && after.FightOver && P(after, me)?.Total == 300_000 && !after.Savasta,
            Show(after));
    }

    // b) Dövüş yokken PvP / kendine hasar dövüş başlatmaz; dövüş varken tabloya ve BossTotal'a girmez, dövüşü uzatmaz.
    {
        var (e, d, _) = New();
        d.SetLocalCharacter(new Aion2CharacterInfo(me, "Ben", 6, 50, t));
        d.NoteClass(me, "Gladiator");
        d.Register(stranger, "Yabanci"); d.NoteClass(stranger, "Ranger");
        e.InjectHit(H(me, t.AddSeconds(-50), stranger, 50_000));
        e.InjectHit(H(stranger, t.AddSeconds(-50), me, 50_000, 13010000));
        MeterSnapshot none = e.SnapshotAt(t.AddSeconds(-49));
        d.HitPoints.Note(boss, t.AddSeconds(-45), 5_000_000);
        e.InjectHit(H(me, t.AddSeconds(-44), boss, 100_000));
        d.HitPoints.Note(boss, t.AddSeconds(-44), 4_900_000);
        for (int i = 0; i < 10; i++) { e.InjectHit(H(me, t.AddSeconds(-40 + i * 2), me, 1_000)); } // kendine hasar 20 sn boyunca
        MeterSnapshot s = e.SnapshotAt(t.AddSeconds(-15));
        Check("oyuncuya vuruş dövüş başlatmaz, tabloya girmez, dövüşü uzatmaz",
            none.Players.All(p => p.Total == 0) && !none.Savasta && s.FightOver && P(s, me) is { Total: 100_000, BossTotal: 100_000 } && s.FightDuration == TimeSpan.Zero,
            Show(none) + " >> " + Show(s));
    }
}
