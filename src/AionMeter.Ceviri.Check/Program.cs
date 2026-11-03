// AionMeter.Ceviri denetimi: OCR doğruluğu, tooltip bulma, model indirme, çeviri kalitesi/hızı, RAM.
// Canlı ekran görüntüsü ALINMAZ: tüm test görüntüleri burada sentetik olarak çizilir ve ornekler\ altına kaydedilir.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using AionMeter.Ceviri;

Console.OutputEncoding = Encoding.UTF8;
string projeKlasoru = YukariBul(AppContext.BaseDirectory, "AionMeter.Ceviri.Check.csproj");
string depoKoku = YukariBul(AppContext.BaseDirectory, Path.Combine("assets", "aion2", "ceviri", "sozluk.json"));
string orneklerKlasoru = Path.Combine(projeKlasoru, "ornekler");
Directory.CreateDirectory(orneklerKlasoru);
bool sadeceCeviri = args.Contains("--ceviri");

Bolum("Ortam");
Console.WriteLine($"İşlemci çekirdeği: {Environment.ProcessorCount}, OS: {Environment.OSVersion}");
Console.WriteLine($"Kurulu OCR dilleri: {string.Join(", ", Ocr.KuruluDiller())}; kullanılan: {Ocr.Dil}");
Console.WriteLine($"Başlangıç RAM: {Ram()}");

if (!sadeceCeviri)
{
    await OcrDenetimi(orneklerKlasoru);
    await TooltipDenetimi(orneklerKlasoru);
    if (args.Contains("--goruntu")) return 0;
}

// ---------------- Model indirme ----------------
Bolum("Model indirme");
Console.WriteLine($"Klasör: {ModelIndirici.VarsayilanKlasor}");
Console.WriteLine($"Toplam boyut: {ModelIndirici.ToplamBoyut:N0} bayt ({ModelIndirici.ToplamBoyut / 1048576.0:F1} MiB)");
Console.WriteLine($"Hazır mı (önce): {ModelIndirici.HazirMi()}");
{
    var sw = Stopwatch.StartNew();
    long sonYuzde = -1;
    await ModelIndirici.IndirAsync(new AnlikIlerleme<(long indirilen, long toplam)>(p =>
    {
        long y = p.indirilen * 100 / Math.Max(1, p.toplam);
        if (y / 10 != sonYuzde / 10) { sonYuzde = y; Console.WriteLine($"  %{y,3}  {p.indirilen:N0} / {p.toplam:N0}"); }
    }), CancellationToken.None);
    Console.WriteLine($"İndirme/doğrulama süresi: {sw.Elapsed.TotalSeconds:F1} sn; hazır mı (sonra): {ModelIndirici.HazirMi()}");
}

// ---------------- Çeviri ----------------
Bolum("Çeviri");
var sozluk = OyunSozlugu.Yukle(Path.Combine(depoKoku, "assets", "aion2", "ceviri", "sozluk.json"));
{
    long m0 = GC.GetTotalMemory(true);
    var swS = Stopwatch.StartNew();
    int korunan = sozluk.KorunanSayisi; // beceri adlarını ilk erişimde yükler
    long yuklemeMs = swS.ElapsedMilliseconds;
    long m1 = GC.GetTotalMemory(true);
    Console.WriteLine($"Sözlük: {korunan} korunan terim (beceri adları dahil), {sozluk.TerimSayisi} sabit terim; " +
        $"tembel yükleme {yuklemeMs} ms, yönetilen bellek +{(m1 - m0) / 1024.0 / 1024:F2} MB");
    string[] deneme = ["Increases Magic Attack by 15% for 30 seconds.", "Ferocious Strike deals 1,200 damage and stuns the target.",
        "Block the attack with your Shield.", "Increases Block by 5 for 10 seconds.", "Fireball", "Use Fireball on the target.",
        "Dash", "Use Dash to escape.", "The Asmodians use Divine Heal often.", "Kaisinel's priests wield Maces."];
    swS.Restart();
    for (int k = 0; k < 1000; k++) foreach (var d in deneme) sozluk.Hazirla(d);
    Console.WriteLine($"Eşleştirme hızı: {swS.Elapsed.TotalMilliseconds * 1000 / (1000.0 * deneme.Length):F1} µs/cümle");
    foreach (var d in deneme)
    {
        var h = sozluk.Hazirla(d);
        Console.WriteLine($"  {d,-60} → {h.ModelGirdisi}   [{string.Join(", ", h.OyunTerimleri)}]");
    }
    Console.WriteLine("Ek uyumu (yazımdaki son ünlü harf; sessiz son e sayılmaz):");
    foreach (var (ad, ek) in new[] { ("Magic Attack", "yı"), ("Physical Defense", "yı"), ("Greatsword", "ü"), ("Manastone", "lar"),
                 ("Strike", "ya"), ("Kinah", "dan"), ("Daeva", "nın"), ("Abyss", "da"), ("HP", "ı"), ("Elyos", "ya"), ("Aethertool", "la") })
        Console.Write($"  {ad}'{TurkceEk.Uydur(ad, ek)}");
    Console.WriteLine();
}
string onbellekYolu = Path.Combine(Path.GetTempPath(), "aionmeter-ceviri-check-onbellek.json");
File.Delete(onbellekYolu);
var onbellek = new CeviriOnbellegi(onbellekYolu);

string[] cumleler =
[
    "Defeat 10 Kerubim near the Poeta Outpost and report to Daeva Hylas.",
    "The Balaur have breached the eastern wall. Gather the guards and hold the gate until reinforcements arrive.",
    "Thank you, Daeva. Without your help, our village would have been destroyed.",
    "Collect 5 Ice Crystals from the frozen caves and bring them to the alchemist.",
    "Your inventory is full.",
    "You have acquired 3,500 Kinah.",
    "Deals 1,250 fire damage to a target within 25m.",
    "Increases Magic Attack by 15% for 30 seconds.",
    "The Asmodians are gathering their forces in the Abyss.",
    "Speak with the Elyos commander before entering the fortress.",
    "{0} has joined the party.",
    "You cannot use this skill while moving.",
    "This item cannot be traded or sold to a merchant.",
    "Restores 500 HP and removes one harmful effect.",
    "Do you want to teleport to Sanctum?",
    "The dungeon will close in 5 minutes.",
    "Have you seen the strange lights in the forest at night? Something evil is stirring there.",
    "Requires a Greatsword to be equipped.",
    "Not enough MP.",
    "Congratulations! You have reached level 30 and can now choose your Stigma skills.",
    // +10: istatistik / beceri / eşya ağırlıklı
    "Reduces the target's Physical Defense by 200.",
    "Ferocious Strike deals 1,200 damage and stuns the target.",
    "Can be enchanted with Manastones.",
    "Increases Critical Strike and Accuracy by 50 for 10 seconds.",
    "Equip a Staff or an Orb to use this skill.",
    "Your Magic Resist has been reduced.",
    "Cooldown of Divine Heal is reduced by 3 seconds.",
    "Socket a Manastone to increase your Attack Speed.",
    "Defeat the Balaur and obtain 3 Abyss Points.",
    "Grants Daevanion skill: Keen Strike.",
];

if (args.Contains("--hiz"))
{
    // Hız/RAM deneyi: ORT ayarları ve çözücü varyantları (ham model, sözlük/önbellek yok).
    // (q4 çözücü de denendi: 533 ms/cümle, 1004 MB → int8'den kötü, elendi.)
    foreach (var (ad, dusuk) in new[] { ("int8 hızlı (varsayılan)", false), ("int8 düşük bellek", true) })
    {
        using var c = new CevrimdisiCevirmen(ModelIndirici.VarsayilanKlasor, OyunSozlugu.Bos, new CeviriOnbellegi(Path.GetTempFileName())) { DusukBellek = dusuk };
        var swy = Stopwatch.StartNew();
        c.HamCevir("Hello.", 1);
        Console.WriteLine($"{ad}: yükleme+ilk {swy.ElapsedMilliseconds} ms, iş parçacığı {c.IsParcacigi}");
        foreach (int isin in new[] { 1, 2, 4 })
        {
            var sw = Stopwatch.StartNew();
            var cikti = cumleler.Select(o => c.HamCevir(o, isin)).ToList();
            Console.WriteLine($"  ışın={isin}: ortalama {sw.ElapsedMilliseconds / cumleler.Length} ms/cümle; yüklü RAM: {Ram()}");
        }
        c.Bosalt();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"  boşaltınca RAM: {Ram()}");
    }
    MarianModel.AyarDegistir = null;
    MarianModel.CozucuDosyasi = "decoder_model_merged_int8.onnx";
    return 0;
}

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
string ramOnce = Ram();
Console.WriteLine($"RAM (model yüklenmeden): {ramOnce}");

using (var cevirmen = new CevrimdisiCevirmen(ModelIndirici.VarsayilanKlasor, sozluk, onbellek))
{
    Console.WriteLine($"ModelHazir: {cevirmen.ModelHazir}, Yüklü: {cevirmen.Yuklu}, ışın: {cevirmen.Isin}, iş parçacığı: {cevirmen.IsParcacigi}");
    var sureler = new List<double>();
    for (int i = 0; i < cumleler.Length; i++)
    {
        var s = await cevirmen.CevirAsync(cumleler[i]);
        if (i == 0)
            Console.WriteLine($"[soğuk] model yükleme {cevirmen.SonYuklemeMs} ms; ilk çeviri toplam {s.Sure.TotalMilliseconds:F0} ms; RAM yüklü: {Ram()}");
        else sureler.Add(s.Sure.TotalMilliseconds);
        Console.WriteLine($"{i + 1,2}. EN: {s.Ingilizce}");
        Console.WriteLine($"    HAM: {cevirmen.HamCevir(cumleler[i], cevirmen.Isin)}");
        Console.WriteLine($"    TR : {s.Turkce}");
        Console.WriteLine($"    {s.Sure.TotalMilliseconds:F0} ms{(s.KorunanTerimler.Count > 0 ? "; korunan: " + string.Join(", ", s.KorunanTerimler) : "")}{(s.OnbellektenGeldi ? "; (model çalışmadı)" : "")}");
    }
    Console.WriteLine($"[sıcak] ışın {cevirmen.Isin}: ortalama {sureler.Average():F0} ms, en kötü {sureler.Max():F0} ms, en iyi {sureler.Min():F0} ms ({sureler.Count} cümle)");
    Console.WriteLine($"RAM yüklü (çevirilerden sonra): {Ram()}");

    Console.WriteLine("\n'acquired' deneyi (ham model):");
    foreach (var c in new[] { "You have acquired 3,500 Kinah.", "You have obtained 3,500 Kinah." })
        Console.WriteLine($"  {c} → {cevirmen.HamCevir(c, 2)}");

    // ---- Sözlük koruması: korumalı vs ham model ----
    Bolum("Sözlük koruması (korumalı / ham model)");
    string[] terimCumleleri =
    [
        "The Daeva must protect Elysea from the Balaur.",
        "Elyos and Asmodians must unite against the Balaur.",
        "Report to Kaisinel's priest in Sanctum.",
        "An Asmodian assassin is hiding near the Abyss gate.",
        "Bring 20 Odylium shards to the Shugo merchant.",
        "Every Daeva can use Stigma stones.",
    ];
    onbellek.Temizle();
    foreach (var c in terimCumleleri)
    {
        var s = await cevirmen.CevirAsync(c);
        Console.WriteLine($"EN : {c}");
        Console.WriteLine($"TR : {s.Turkce}   [korunan: {string.Join(", ", s.KorunanTerimler)}]");
        Console.WriteLine($"HAM: {cevirmen.HamCevir(c, 2)}");
    }

    // ---- Yer tutucu biçimi deneyi (ham model): hangi biçim aynen kopyalanıyor? ----
    Bolum("Yer tutucu deneyi");
    string[] kaliplar = ["Speak with {T} before the battle.", "{T} attacked the fortress at dawn.", "The gift was sent by {T}.",
        "Defeat {T} and return to the camp.", "You must help {T} and {U}.", "Report to {T} immediately."];
    foreach (var yt in new[] { "ZxA", "XA", "QQA", "[A]", "Zorvan" })
    {
        int tutan = 0; var ornek = "";
        foreach (var k in kaliplar)
        {
            string g = k.Replace("{T}", yt).Replace("{U}", yt.Replace('A', 'B'));
            string c = cevirmen.HamCevir(g, 2);
            bool ok = c.Contains(yt) && (!k.Contains("{U}") || c.Contains(yt.Replace('A', 'B')));
            if (ok) tutan++;
            if (ornek == "") ornek = c;
        }
        Console.WriteLine($"{yt,-7}: {tutan}/{kaliplar.Length} aynen kaldı; örnek: {ornek}");
    }
    foreach (var g in new[] { "The ZxA are gathering their forces.", "The ZxAs are gathering their forces.",
                 "ZxA and ZxBs must unite.", "ZxA and ZxB must unite." })
        Console.WriteLine($"çoğul deneyi: {g} → {cevirmen.HamCevir(g, 2)}");

    // ---- Sabitler ve sabit terimler ----
    Bolum("Sabitler ({0}, %d, sayılar) ve sabit terimler");
    foreach (var c in new[] { "{0} has defeated {1}.", "You received %d Kinah.", "Deal 12,500 damage within 10 seconds.",
                 "Attack +120", "Cooldown: 30s", "Account Bound", "Critical Strike +5%", "Magic Accuracy 1,024", "Attack" })
    {
        var s = await cevirmen.CevirAsync(c);
        Console.WriteLine($"{c,-45} → {s.Turkce}{(s.OnbellektenGeldi ? "   (model çalışmadı)" : "")}");
    }

    // ---- Önbellek ----
    Bolum("Önbellek");
    {
        var s0 = await cevirmen.CevirAsync("Your inventory is full.");
        Console.WriteLine($"İlk: '{s0.Turkce}', önbellekten: {s0.OnbellektenGeldi}, {s0.Sure.TotalMilliseconds:F0} ms");
        var s1 = await cevirmen.CevirAsync("  your INVENTORY   is full. ");
        Console.WriteLine($"Normalleştirilmiş anahtar ile tekrar: '{s1.Turkce}', önbellekten: {s1.OnbellektenGeldi}, {s1.Sure.TotalMilliseconds:F2} ms");
    }

    // ---- Boşaltma / RAM ----
    Bolum("RAM");
    Console.WriteLine($"Yüklü: {cevirmen.Yuklu}; {Ram()}");
    cevirmen.Bosalt();
    await Task.Delay(500);
    Console.WriteLine($"Bosalt() sonrası: Yüklü={cevirmen.Yuklu}; {Ram()}");

    cevirmen.BostaBekleme = TimeSpan.FromSeconds(2);
    var sw2 = Stopwatch.StartNew();
    var s3 = await cevirmen.CevirAsync("The gate is closed.");
    Console.WriteLine($"Yeniden yükleme + çeviri: {sw2.ElapsedMilliseconds} ms (yükleme {cevirmen.SonYuklemeMs} ms) → {s3.Turkce}; {Ram()}");
    await Task.Delay(3500);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Console.WriteLine($"2 sn boşta bekleme sonrası kendiliğinden boşaldı mı: {!cevirmen.Yuklu}; {Ram()}");
}
onbellek.Dispose();
using (var tekrar = new CeviriOnbellegi(onbellekYolu))
    Console.WriteLine($"Önbellek dosyası: {new FileInfo(onbellekYolu).Length:N0} bayt, yeniden okunan kayıt: {tekrar.Sayi}");
Console.WriteLine("BİTTİ");
return 0;

// =====================================================================================

static async Task OcrDenetimi(string klasor)
{
    Bolum("OCR doğruluğu (sentetik oyun görüntüleri)");
    var ornekler = OcrOrnekleri();
    double[] toplam = new double[4];
    double[] sure = new double[4];
    foreach (var (ad, gercek, bmp) in ornekler)
    {
        bmp.Save(Path.Combine(klasor, ad + ".png"), ImageFormat.Png);
        var satir = new StringBuilder($"{ad,-22} {bmp.Width}x{bmp.Height}");
        for (int olcek = 1; olcek <= 3; olcek++)
        {
            var sw = Stopwatch.StartNew();
            var satirlar = await Ocr.OkuAsync(bmp, olcek);
            sure[olcek] += sw.Elapsed.TotalMilliseconds;
            string okunan = string.Join(" ", satirlar.Select(s => s.Metin));
            double d = Dogruluk(okunan, gercek);
            toplam[olcek] += d;
            satir.Append($"  {olcek}x: %{d * 100:F1}");
            if (olcek == 2 && d < 0.999) satir.Append($"\n      2x okunan: {okunan}");
        }
        Console.WriteLine(satir);
    }
    for (int o = 1; o <= 3; o++)
        Console.WriteLine($"Ortalama karakter doğruluğu {o}x: %{toplam[o] / ornekler.Count * 100:F1}  (ortalama süre {sure[o] / ornekler.Count:F0} ms)");

    // Paragraf birleştirme örneği
    var (_, _, gorev) = ornekler[0];
    var p = Paragraf.Birlestir(await Ocr.OkuAsync(gorev));
    Console.WriteLine($"Paragraf.Birlestir (görev görüntüsü): {p.Count} paragraf → " + string.Join(" | ", p));
    var (_, _, tt) = ornekler[2];
    var p2 = Paragraf.Birlestir(await Ocr.OkuAsync(tt));
    Console.WriteLine($"Paragraf.Birlestir (eşya tooltip): {p2.Count} paragraf → " + string.Join(" | ", p2));
    foreach (var o in ornekler) o.Bmp.Dispose();
}

static async Task TooltipDenetimi(string klasor)
{
    Bolum("Tooltip bulma (sentetik: gürültülü arka plan + koyu yuvarlak kutu + imleç)");
    var r = new Random(1234);
    int dogru = 0, pozitif = 0, negatifDogru = 0, negatif = 0, kaydedilen = 0;
    double iouToplam = 0, ocrToplam = 0; int ocrSayi = 0;
    var sureler = new List<double>();
    var hatalar = new List<string>();
    var kosul = new Dictionary<string, (int Dogru, int Toplam)>();
    for (int i = 0; i < 60; i++)
    {
        bool var_ = i < 48;
        bool dikkatDagitici = i % 3 == 0;   // imleç çanta benzeri koyu bir panelin üstünde
        bool karanlikSahne = i % 5 == 0;
        var (bmp, imlec, gercekKutu, metin) = TooltipOrnegi(r, var_, dikkatDagitici, karanlikSahne);
        var sw = Stopwatch.StartNew();
        TooltipBulucu.Tani = Environment.GetEnvironmentVariable("TANI") == "1" ? [] : null;
        var bulunan = TooltipBulucu.Bul(bmp, imlec);
        if (TooltipBulucu.Tani != null && var_ && (bulunan is not { } bb || Iou(bb, gercekKutu!.Value) < 0.8) && i < 12)
        {
            var (m, bw, bh) = TooltipBulucu.TaniMaske;
            var gk = gercekKutu!.Value;
            int icinde = 0, toplamB = 0;
            for (int by = gk.Top / 6 + 1; by < gk.Bottom / 6 - 1; by++) for (int bx = gk.Left / 6 + 1; bx < gk.Right / 6 - 1; bx++) { toplamB++; if (m[by * bw + bx]) icinde++; }
            Console.WriteLine($"TANI #{i}: gerçek kutu içi panel blok {icinde}/{toplamB}; " + string.Join(" | ", TooltipBulucu.Tani.Take(6)));
        }
        sureler.Add(sw.Elapsed.TotalMilliseconds);
        if (var_)
        {
            pozitif++;
            double iou = bulunan is { } b ? Iou(b, gercekKutu!.Value) : 0;
            iouToplam += iou;
            string ks = (karanlikSahne ? "karanlık sahne" : "aydınlık sahne") + (dikkatDagitici ? " + imleç çanta panelinde" : "");
            var (kd, kt) = kosul.GetValueOrDefault(ks);
            kosul[ks] = (kd + (iou >= 0.8 ? 1 : 0), kt + 1);
            if (iou >= 0.8) dogru++;
            else hatalar.Add($"#{i} IoU={iou:F2} gerçek={gercekKutu} bulunan={bulunan?.ToString() ?? "yok"} dikkatDağıtıcı={dikkatDagitici} karanlık={karanlikSahne}");
            if (bulunan is { } bk && iou >= 0.5)
            {
                using var kesit = bmp.Clone(Rectangle.Intersect(bk, new Rectangle(0, 0, bmp.Width, bmp.Height)), PixelFormat.Format32bppArgb);
                var satirlar = await Ocr.OkuAsync(kesit);
                ocrToplam += Dogruluk(string.Join(" ", satirlar.Select(s => s.Metin)), metin!);
                ocrSayi++;
            }
        }
        else
        {
            negatif++;
            if (bulunan == null) negatifDogru++;
            else hatalar.Add($"#{i} yanlış alarm: {bulunan} dikkatDağıtıcı={dikkatDagitici}");
        }
        if (kaydedilen < 4 && (i % 7 == 0 || i == 50))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.FillPolygon(Brushes.White, [imlec, new Point(imlec.X, imlec.Y + 16), new Point(imlec.X + 11, imlec.Y + 11)]);
                if (bulunan is { } b2) g.DrawRectangle(new Pen(Color.Lime, 1), b2);
            }
            bmp.Save(Path.Combine(klasor, $"tooltip_{i:00}.png"), ImageFormat.Png);
            kaydedilen++;
        }
        bmp.Dispose();
    }
    Console.WriteLine($"Pozitif (kutu var): {dogru}/{pozitif} doğru (IoU ≥ 0,8), ortalama IoU {iouToplam / pozitif:F3}");
    foreach (var (k, (d, t)) in kosul.OrderBy(x => x.Key)) Console.WriteLine($"   {k}: {d}/{t}");
    Console.WriteLine($"Negatif (kutu yok): {negatifDogru}/{negatif} doğru (null)");
    Console.WriteLine($"Süre: ortalama {sureler.Average():F1} ms, en kötü {sureler.Max():F1} ms (640x480 kesit)");
    if (ocrSayi > 0) Console.WriteLine($"Bulunan kutudan OCR karakter doğruluğu: %{ocrToplam / ocrSayi * 100:F1} ({ocrSayi} kutu)");
    foreach (var h in hatalar) Console.WriteLine("  " + h);
}

static List<(string Ad, string Gercek, Bitmap Bmp)> OcrOrnekleri()
{
    var r = new Random(42);
    var l = new List<(string, string, Bitmap)>
    {
        Ornek(r, "1_gorev", 520, ["Defeat 10 Kerubim near the Poeta Outpost and report to Daeva Hylas."],
            "Segoe UI", 11, FontStyle.Regular, Color.FromArgb(235, 235, 225), stil: 0),
        Ornek(r, "2_diyalog", 560, ["The Balaur have breached the eastern wall. Gather the Elyos guards and hold the gate until reinforcements arrive."],
            "Georgia", 12, FontStyle.Regular, Color.FromArgb(240, 225, 190), stil: 1),
        Ornek(r, "3_esya_tooltip", 300, ["Ancient Staff of Wisdom", "Magic Attack +145", "Casting Speed +8%", "Required Level: 25", "Cannot be traded"],
            "Tahoma", 9, FontStyle.Regular, Color.FromArgb(220, 220, 220), stil: 0, baslikRengi: Color.FromArgb(255, 200, 80)),
        Ornek(r, "4_beceri_tooltip", 380, ["Flame Bolt", "Cast Time: 1.5s Cooldown: 8s", "Deals 1,250 fire damage to a target within 25m."],
            "Arial", 10, FontStyle.Regular, Color.White, stil: 2, baslikRengi: Color.FromArgb(120, 200, 255)),
        Ornek(r, "5_sistem_mesaji", 420, ["You have acquired 3,500 Kinah."],
            "Verdana", 9, FontStyle.Bold, Color.FromArgb(255, 230, 90), stil: 2, aydinlikZemin: true),
        Ornek(r, "6_uzun_gorev_kucuk", 470, ["Speak with Marchutan's envoy in Pandaemonium, then travel to Morheim and collect 5 Ice Crystals from the frozen caves."],
            "Segoe UI", 9, FontStyle.Regular, Color.FromArgb(225, 225, 215), stil: 1),
        Ornek(r, "7_cok_kucuk", 360, ["Asmodian forces were spotted near the Abyss gate."],
            "Segoe UI", 8, FontStyle.Regular, Color.FromArgb(210, 210, 200), stil: 0),
        Ornek(r, "8_serif_golge", 500, ["Hurry, Daeva! The Asmodian raiders will reach the village before nightfall."],
            "Times New Roman", 12, FontStyle.Italic, Color.FromArgb(250, 240, 220), stil: 1),
    };
    return l;
}

// stil: 0 = düz, 1 = gölge, 2 = dış çizgi
static (string, string, Bitmap) Ornek(Random r, string ad, int genislik, string[] satirlar, string yazi, float punto, FontStyle fs,
    Color renk, int stil, Color? baslikRengi = null, bool aydinlikZemin = false)
{
    using var font = new Font(yazi, punto, fs, GraphicsUnit.Point);
    using var baslikFont = new Font(yazi, punto + 1, FontStyle.Bold, GraphicsUnit.Point);
    using var olcum = new Bitmap(1, 1);
    using var og = Graphics.FromImage(olcum);
    int pad = 14;
    var bloklar = new List<(string Metin, Font F, Color C, RectangleF Kutu)>();
    float y = pad;
    for (int i = 0; i < satirlar.Length; i++)
    {
        var f = baslikRengi != null && i == 0 ? baslikFont : font;
        var c = baslikRengi != null && i == 0 ? baslikRengi.Value : renk;
        var boy = og.MeasureString(satirlar[i], f, genislik - 2 * pad);
        bloklar.Add((satirlar[i], f, c, new RectangleF(pad, y, genislik - 2 * pad, boy.Height + 2)));
        y += boy.Height + 3;
    }
    int yukseklik = (int)y + pad;
    var bmp = new Bitmap(genislik, yukseklik, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    Sahne(g, r, genislik, yukseklik, aydinlikZemin ? 1.0 : 0.8);
    // yarı saydam koyu panel + doku
    using (var panel = new SolidBrush(Color.FromArgb(aydinlikZemin ? 120 : 205, 14, 16, 24))) g.FillRectangle(panel, 4, 4, genislik - 8, yukseklik - 8);
    for (int i = 0; i < 400; i++)
    {
        using var nokta = new SolidBrush(Color.FromArgb(r.Next(10, 30), r.Next(256), r.Next(256), r.Next(256)));
        g.FillRectangle(nokta, r.Next(genislik), r.Next(yukseklik), r.Next(1, 4), r.Next(1, 4));
    }
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    foreach (var (metin, f, c, kutu) in bloklar)
    {
        if (stil == 1)
            using (var golge = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                g.DrawString(metin, f, golge, new RectangleF(kutu.X + 1.5f, kutu.Y + 1.5f, kutu.Width, kutu.Height));
        if (stil == 2)
        {
            using var yol = new GraphicsPath();
            yol.AddString(metin, f.FontFamily, (int)f.Style, g.DpiY * f.SizeInPoints / 72f, kutu, StringFormat.GenericDefault);
            using var kalem = new Pen(Color.FromArgb(230, 0, 0, 0), 2.5f) { LineJoin = LineJoin.Round };
            g.DrawPath(kalem, yol);
            using var dolgu = new SolidBrush(c);
            g.FillPath(dolgu, yol);
        }
        else
            using (var b = new SolidBrush(c)) g.DrawString(metin, f, b, kutu);
    }
    return (ad, string.Join(" ", satirlar), bmp);
}

/// <summary>Gürültülü, renkli "oyun sahnesi" arka planı.</summary>
static void Sahne(Graphics g, Random r, int w, int h, double parlaklik)
{
    int P(int v) => Math.Clamp((int)(v * parlaklik), 0, 255);
    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(P(r.Next(40, 160)), P(r.Next(40, 160)), P(r.Next(40, 160))),
               Color.FromArgb(P(r.Next(20, 120)), P(r.Next(60, 200)), P(r.Next(20, 120))), (float)r.Next(360)))
        g.FillRectangle(lg, 0, 0, w, h);
    for (int i = 0; i < 70; i++)
    {
        using var b = new SolidBrush(Color.FromArgb(r.Next(80, 220), P(r.Next(256)), P(r.Next(256)), P(r.Next(256))));
        int x = r.Next(-40, w), y = r.Next(-40, h), ew = r.Next(10, 140), eh = r.Next(10, 140);
        if (r.Next(2) == 0) g.FillEllipse(b, x, y, ew, eh); else g.FillRectangle(b, x, y, ew, eh);
    }
    for (int i = 0; i < 40; i++)
    {
        using var p = new Pen(Color.FromArgb(r.Next(100, 255), P(r.Next(256)), P(r.Next(256)), P(r.Next(256))), r.Next(1, 4));
        g.DrawLine(p, r.Next(w), r.Next(h), r.Next(w), r.Next(h));
    }
    for (int i = 0; i < w * h / 12; i++)
    {
        int v = P(r.Next(256));
        using var b = new SolidBrush(Color.FromArgb(70, v, v, v));
        g.FillRectangle(b, r.Next(w), r.Next(h), 2, 2);
    }
}

static (Bitmap Bmp, Point Imlec, Rectangle? Kutu, string? Metin) TooltipOrnegi(Random r, bool kutuVar, bool dikkatDagitici, bool karanlik)
{
    const int W = 640, H = 480;
    var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    Sahne(g, r, W, H, karanlik ? 0.35 : 1.0);
    var imlec = new Point(r.Next(120, W - 120), r.Next(100, H - 100));

    if (dikkatDagitici)
    {
        // Çanta benzeri koyu panel: renkli ikon ızgarası; imleç bir ikonun üstünde.
        var panel = new Rectangle(imlec.X - r.Next(60, 200), imlec.Y - r.Next(40, 140), r.Next(220, 300), r.Next(160, 240));
        using (var pb = new SolidBrush(Color.FromArgb(225, 18, 20, 26))) g.FillRectangle(pb, panel);
        for (int gy = panel.Top + 8; gy + 36 < panel.Bottom; gy += 42)
            for (int gx = panel.Left + 8; gx + 36 < panel.Right; gx += 42)
                using (var ib = new LinearGradientBrush(new Rectangle(gx, gy, 36, 36), Color.FromArgb(r.Next(256), r.Next(256), r.Next(256)),
                           Color.FromArgb(r.Next(256), r.Next(256), r.Next(256)), 45f))
                    g.FillRectangle(ib, gx, gy, 36, 36);
    }
    if (!kutuVar) return (bmp, imlec, null, null);

    string[][] metinler =
    [
        ["Ancient Staff of Wisdom", "Magic Attack +145", "Casting Speed +8%", "Required Level: 25", "Cannot be traded"],
        ["Flame Bolt", "Cast Time: 1.5s", "Cooldown: 8s", "Deals 1,250 fire damage to a target."],
        ["Elyos Guard Helmet", "Physical Defense +62", "Evasion +12", "Account Bound"],
        ["Healing Potion", "Restores 500 HP over 10 seconds.", "Sell Price: 25 Kinah"],
        ["Kerubim Hunt", "Defeat 10 Kerubim near the Poeta Outpost.", "Reward: 1,200 Kinah"],
    ];
    var satirlar = metinler[r.Next(metinler.Length)];
    using var font = new Font("Segoe UI", r.Next(9, 12), FontStyle.Regular, GraphicsUnit.Point);
    using var baslik = new Font("Segoe UI", font.SizeInPoints + 1, FontStyle.Bold, GraphicsUnit.Point);
    int pad = 10;
    float y = pad, enGenis = 0;
    var olculer = new List<SizeF>();
    for (int i = 0; i < satirlar.Length; i++)
    {
        var s = g.MeasureString(satirlar[i], i == 0 ? baslik : font, 300);
        olculer.Add(s); enGenis = Math.Max(enGenis, s.Width); y += s.Height + 2;
    }
    int kw = (int)enGenis + 2 * pad, kh = (int)y + pad;
    int dx = r.Next(12, 40), dy = r.Next(12, 40);
    int kx = imlec.X + dx + kw < W ? imlec.X + dx : imlec.X - dx - kw;
    int ky = imlec.Y + dy + kh < H ? imlec.Y + dy : imlec.Y - dy - kh;
    kx = Math.Clamp(kx, 0, W - kw - 1); ky = Math.Clamp(ky, 0, H - kh - 1);
    var kutu = new Rectangle(kx, ky, kw, kh);

    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var yol = Yuvarlak(kutu, 6))
    {
        using var zemin = new SolidBrush(Color.FromArgb(r.Next(215, 245), r.Next(8, 22), r.Next(10, 24), r.Next(14, 32)));
        g.FillPath(zemin, yol);
        if (r.Next(3) > 0)
        {
            using var cerceve = new Pen(r.Next(2) == 0 ? Color.FromArgb(150, 130, 90) : Color.FromArgb(110, 115, 125), r.Next(1, 3));
            g.DrawPath(cerceve, yol);
        }
    }
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    float ty = ky + pad;
    for (int i = 0; i < satirlar.Length; i++)
    {
        var renk = i == 0 ? Color.FromArgb(255, 205, 90) : Color.FromArgb(225, 225, 225);
        using var b = new SolidBrush(renk);
        g.DrawString(satirlar[i], i == 0 ? baslik : font, b, new RectangleF(kx + pad, ty, 300, olculer[i].Height + 2));
        ty += olculer[i].Height + 2;
    }
    return (bmp, imlec, kutu, string.Join(" ", satirlar));
}

static GraphicsPath Yuvarlak(Rectangle k, int r)
{
    var p = new GraphicsPath();
    int d = r * 2;
    p.AddArc(k.Left, k.Top, d, d, 180, 90);
    p.AddArc(k.Right - d, k.Top, d, d, 270, 90);
    p.AddArc(k.Right - d, k.Bottom - d, d, d, 0, 90);
    p.AddArc(k.Left, k.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static double Iou(Rectangle a, Rectangle b)
{
    var i = Rectangle.Intersect(a, b);
    if (i.IsEmpty) return 0;
    double ia = (double)i.Width * i.Height;
    return ia / ((double)a.Width * a.Height + (double)b.Width * b.Height - ia);
}

/// <summary>1 − (Levenshtein / gerçek uzunluk), boşluklar teke indirilmiş.</summary>
static double Dogruluk(string okunan, string gercek)
{
    static string N(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    string a = N(okunan), b = N(gercek);
    var d = new int[a.Length + 1, b.Length + 1];
    for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
    for (int j = 0; j <= b.Length; j++) d[0, j] = j;
    for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
    return Math.Max(0, 1 - d[a.Length, b.Length] / (double)Math.Max(1, b.Length));
}

static string Ram()
{
    var p = Process.GetCurrentProcess();
    p.Refresh();
    return $"özel bellek {p.PrivateMemorySize64 / 1048576.0:F0} MB, çalışma kümesi {p.WorkingSet64 / 1048576.0:F0} MB";
}

static void Bolum(string ad) => Console.WriteLine($"\n===== {ad} =====");

static string YukariBul(string bas, string goreli)
{
    for (var d = new DirectoryInfo(bas); d != null; d = d.Parent)
    {
        if (File.Exists(Path.Combine(d.FullName, goreli))) return d.FullName;
        foreach (var alt in new[] { "src/AionMeter.Ceviri.Check" })
            if (File.Exists(Path.Combine(d.FullName, alt, goreli))) return Path.Combine(d.FullName, alt);
    }
    throw new DirectoryNotFoundException(goreli);
}

sealed class AnlikIlerleme<T>(Action<T> f) : IProgress<T>
{
    public void Report(T value) => f(value);
}
