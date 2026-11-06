# AionMeter

AION 2 (Global) için küçük, Türkçe hasar ölçer overlay'i. Oyunun ağ trafiğini Npcap ile pasif okur; oyuna dokunmaz.

Gösterdikleri:
- **Boss:** ad, tam can, %, dövüş süresi, tahmini öldürme süresi, öfke geri sayımı (`Yetişir` / `geç`)
- **Parti:** sınıf ikonu ve rengi, isim, CP, Boss DPS, DPS, toplam hasar, katkı %, ölüm sayısı — en çok vuran en üstte (boss varken hasar ve pay bossa vurulandan)
- **Satıra tıkla:** vuruş istatistikleri, beceri tablosu, DPS grafiği
- Sayılar yuvarlanmaz. Ölüp dirilmek dövüşü sıfırlamaz; boss ölünce sonuç ekranda kalır.
- Boss satırındaki süre **aktif süredir**: 3 sn'den uzun vurulmayan aralar sayılmaz (DPS de bu süreye bölünür).

## Kurulum
1. Zip'i bir klasöre çıkar, `AionMeter.exe`'yi çalıştır.
2. Windows yönetici izni sorar; **Evet** de. Oyun yönetici olarak çalıştığı için `Alt` tuşu ancak böyle okunabiliyor ("Hayır" dersen de açılır, sadece `Alt` özellikleri çalışmaz).
3. Npcap yoksa sorar; **Evet** de, açılan kurulumu varsayılan ayarlarla bitir.
4. Meter'ı **oyuna girmeden önce** aç (yoksa isimler ilk bölge değişimine kadar `Oyuncu #…` görünebilir).

.NET kurmaya gerek yok, exe'nin içinde.
Her açılışta güncellemeleri kontrol eder ve kendini günceller.

## Kısayollar
| Tuş | İş |
|---|---|
| `Ctrl+Alt+L` | Kilitle / kilidi aç (kilitliyken tıklamalar oyuna geçer) |
| `Ctrl+Alt+H` | Göster / gizle |
| `Ctrl+Alt+R` | Sıfırla |
| `Alt` (basılı tut) | Kilitliyken satırın üstüne gel: detay; tıkla: sabitle |
| `Ctrl+Alt+D` | Kendi detayın |
| `Ctrl+Alt+T` | Ekranda bölge seç, Türkçeye çevir (tekrar basınca kapanır) |
| `Alt` + üstüne gel | Eşya/craft/beceri açıklamasını çevirir (model indirildikten sonra) |
| `Ctrl+Alt+K` | Kukla testini başlat (Ayarlar'daki süreyle); tekrar basınca iptal / sonuç kartını kapat. Ayarlar → Kukla ve alarm → Kısayol'dan değiştirilir ya da kapatılır |
| `Alt` + saate tıkla | Alarm kur / kapat (overlay'in sağ alt köşesi) |

Kilit açıkken pencereyi sürükleyebilir, satıra tıklayıp detayı açabilirsin. Sağ alttaki tepsi simgesinden de aynı menüye ulaşılır.
Tepsi menüsünde **Ayarlar…**: boyut (%70–%125), saydamlık (kaydırırken canlı görünür), savaş dışında davranış, öfke uyarısı sesi, çevirmen ve güncelleme ayarları. **Güncellemeleri denetle** kapatıp açmadan yeni sürüme bakar.
**Savaş dışında** (Ayarlar): bir canavara vurunca overlay tam görünür; savaş bitince ~8 sn sonra `Soluk` (varsayılan), `Gizle` ya da `Hep görünür`.
Varsayılan olarak sadece oyun penceresi öndeyken görünür (Ayarlar'dan kapatılabilir).
Çevirmen çevrimdışı çalışır; çeviri modeli bir kez indirilir (ilk `Ctrl+Alt+T`'de ya da Ayarlar'dan "Çeviri modeli: İndir").

## Ek özellikler
- **Sadece benim satırım** (Ayarlar → Genel): overlay'de boss ve yalnız senin satırın (`#3` gibi sıranla) kalır; diğerleri arka planda sayılmaya devam eder.
- **Renk ve yazı** (Ayarlar → Renk ve yazı): her sınıfın rengi, kendi satır rengin, ad / DPS / boss yazı boyutu (%80–%130) ve pay çubuğu opaklığı; değiştirdikçe overlay canlı değişir, "Varsayılana dön" ile geri alınır.
- **Kukla testi** (tepsi menüsü ya da Ayarlar → Kukla ve alarm): süreyi seç (30 / 60 / 120 / 180 sn ya da özel 5–300 sn), kuklaya vurmaya başla; süre dolunca DPS, toplam, en çok vuran 3 beceri ve sınıfına göre kişisel en iyinle kıyas çıkar. Rekorlar `kukla-rekor.json`'da durur. Kart `Alt`+tıkla ya da yeni dövüşte kapanır.
- **Alarm**: `Alt` basılı tutup overlay'deki saate tıkla, süreyi seç (hazır 1 / 3 / 5 / 10 / 15 / 30 dk; ±1 dk ile 1–180 dk), Başlat. Süre dolunca kısa ve yumuşak bir ses, tepsi bildirimi; ses seviyesi Ayarlar'da.

## Oyun yaması sonrası çalışmazsa
Genelde sadece `assets/aion2/protocol/opcodes.json` değişir. Güncelini
[SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter/tree/main/Client/assets/aion2/protocol) reposundan alıp üzerine yaz.

## Derleme
```
dotnet publish src/AionMeter -c Release -o publish/AionMeter
```

Paket çözücü SkeeveAN/Aion-DPS-Meter (MIT) projesinden alınmıştır, bkz. `THIRD-PARTY-LICENSE.txt`.
