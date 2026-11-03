# AionMeter

AION 2 (Global) için küçük, Türkçe hasar ölçer overlay'i. Oyunun ağ trafiğini Npcap ile pasif okur; oyuna dokunmaz.

Gösterdikleri:
- **Boss:** ad, tam can, %, dövüş süresi, tahmini öldürme süresi, öfke geri sayımı (`Yetişir` / `geç`)
- **Parti:** sınıf ikonu, isim, CP, Boss DPS, DPS, toplam hasar, katkı % — en çok vuran en üstte
- **Satıra tıkla:** vuruş istatistikleri, beceri tablosu, DPS grafiği
- Sayılar yuvarlanmaz. Ölüp dirilmek dövüşü sıfırlamaz; boss ölünce sonuç ekranda kalır.

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

Kilit açıkken pencereyi sürükleyebilir, satıra tıklayıp detayı açabilirsin. Sağ alttaki tepsi simgesinden de aynı menüye ulaşılır.
Varsayılan olarak sadece oyun penceresi öndeyken görünür (tepsi menüsünden kapatılabilir).
Çevirmen çevrimdışı çalışır; çeviri modeli bir kez indirilir (ilk `Ctrl+Alt+T`'de ya da tepsi menüsünden "Çeviri modelini indir").

## Oyun yaması sonrası çalışmazsa
Genelde sadece `assets/aion2/protocol/opcodes.json` değişir. Güncelini
[SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter/tree/main/Client/assets/aion2/protocol) reposundan alıp üzerine yaz.

## Derleme
```
dotnet publish src/AionMeter -c Release -o publish/AionMeter
```

Paket çözücü SkeeveAN/Aion-DPS-Meter (MIT) projesinden alınmıştır, bkz. `THIRD-PARTY-LICENSE.txt`.
