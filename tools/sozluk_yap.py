import json, re, sys
kok = sys.argv[1]; vocab = json.load(open(sys.argv[2], encoding='utf-8'))
z = json.load(open(kok + '/assets/aion2/zones/zones.json', encoding='utf-8'))['zones']
b = json.load(open(kok + '/assets/aion2/npcs/boss_npcs.json', encoding='utf-8'))['bosses']
def temiz(n):
    n = re.sub(r'\s*\([^)]*\)\s*$', '', n).strip()
    return n
adlar = set()
for x in z:
    n = temiz(x['names'].get('en', ''))
    if n: adlar.add(n)
for v in b.values():
    adlar.add(temiz(v['name'])); adlar.add(temiz(v.get('instance', '')))
kucuk_izinli = {'of', 'the', 'in', 'and', 'on', 'at', 'to', 'for'}
secilen, atilan = [], []
for n in sorted(a for a in adlar if a):
    kel = n.split()
    if '?' in n or n.startswith('Unused') or any(w[0].islower() and w not in kucuk_izinli for w in kel):
        atilan.append(n); continue
    if len(kel) == 1 and (('\u2581' + n.lower()) in vocab or n in ('Crypt', 'Culvert', 'Gallows', 'Pantheon')):
        atilan.append(n); continue   # tek, sıradan İngilizce sözcük (Crypt, Nightmare...)
    secilen.append(n)
cekirdek = ["Daeva", "Elyos", "Asmodian", "Asmodae", "Elysea", "Atreia", "Balaur", "Abyss", "Aion", "Kinah",
    "Shugo", "Krall", "Mau", "Lepharist", "Drakan", "Reian", "Stigma", "Aether", "Odian", "Fafnite", "Odylium",
    "Kaisinel", "Marchutan", "Ariel", "Azphel", "Lumiel", "Zikel", "Vaizel", "Triniel", "Nezekan", "Yustiel", "Israphel", "Siel",
    "Beritra", "Ereshkigal", "Tiamat", "Fregion",
    "Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Spiritmaster", "Elementalist", "Cleric", "Chanter",
    "Poeta", "Ishalgen", "Sanctum", "Pandaemonium", "Eltnen", "Morheim", "Verteron", "Altgard", "Reshanta",
    "HP", "MP", "DP", "CP", "AP", "DPS", "PvP", "PvE", "AoE", "EXP", "XP", "NPC"]
# Oyun istatistik/eşya terimleri: kullanıcı kararı, İngilizce kalır (oyunun İngilizce arayüzüyle aynı).
istatistik = ["Magic Attack", "Physical Attack", "Physical Defense", "Magic Defense", "Critical Strike", "Critical Hit",
    "Critical Damage", "Crit", "Accuracy", "Magic Accuracy", "Evasion", "Attack Speed", "Casting Speed", "Movement Speed",
    "Healing Boost", "Magic Boost", "Magic Resist", "Magic Resistance", "Max HP", "Max MP", "HP Regen", "MP Regen",
    "Cooldown", "Cast Time", "Daevanion", "Manastone", "Godstone", "Enchantment", "Enchantment Level",
    "Abyss Points", "Medal", "Concentration", "Stigma Shard", "Aethertool", "Greatsword", "Polearm", "Spellbook", "Dagger",
    "Aethercannon", "Keyblade",
    # AION 2 stat / sistem adları (oyun içi denemede çevrildiği görüldü: "Perfect Chance" -> "Mükemmel Şans" vb.)
    "Perfect Chance", "Perfect Resist", "Weapon Damage Boost", "Weapon Damage", "Damage Boost", "Damage Bonus",
    "Damage Reduction", "Damage Amplification", "Power Shard", "Power Shard Damage Bonus", "Critical Hit Resist",
    "Critical Strike Resist", "Critical Damage Resist", "Critical Hit Chance", "Critical Damage Boost", "Block Penetration",
    "Parry Penetration", "Defense Penetration", "Armor Penetration", "Multi-Hit", "Multi-Hit Chance", "Multi-Hit Resist",
    "Backstab Damage", "Boss Damage", "PvE Damage", "PvP Damage", "PvE Defense", "PvP Defense", "PvP Attack", "PvE Attack",
    "Healing Received", "Healing Power", "Stun Resist", "Status Resist", "Status Effect Resist", "Accuracy Bonus",
    "Evasion Bonus", "Cooldown Reduction", "Additional Damage", "Additional Defense", "Ignore Defense", "Final Damage",
    "Monolith Resonance", "Enhance Scroll", "Amulet Enhance Scroll", "Amulet Enhance Scrolls", "Combat Power"]
# Sıradan İngilizce sözcük olan tek kelimelik terimler: sadece cümle ortasında Büyük Harfle ya da satırın tamamıysa korunur.
buyukHarf = ["Socket", "Enchant", "Attack", "Defense", "Critical", "Parry", "Block", "Power", "Health", "Agility", "Knowledge", "Will", "Cast",
    "Sword", "Mace", "Staff", "Orb", "Bow", "Shield", "Cloth", "Leather", "Chain", "Plate", "Pistol", "Cannon", "Harp",
    "Penetration", "Resistance", "Resist", "Perfect", "Amulet", "Regeneration", "Fortitude"]
# Stat adları: kullanıcı kararı (3 Kasım), TÜRKÇE yazılır ("bilmeyen için sorun olur"); beceri/eşya/yer adları İngilizce kalır.
# Cümlede sadece Türkçesi ("Hasar Artışını %10 artırır"), stat satırında "Hasar Artışı (Damage Boost) +120".
# Türkçe adlar ek almaya uygun seçildi: çok sözcüklü ve ı/i/u/ü ile bitenler isim tamlamasıdır (Hasar Artışı → Hasar Artışını).
statlar = {
 # temel
 "Attack": "Saldırı", "Defense": "Savunma", "Accuracy": "İsabet", "Evasion": "Kaçınma", "Parry": "Savuşturma",
 "Block": "Blok", "Penetration": "Delme", "Resistance": "Direnç", "Resist": "Direnç", "Regeneration": "Yenilenme",
 "Critical": "Kritik", "Crit": "Kritik", "Power": "Güç", "Health": "Sağlık", "Agility": "Çeviklik",
 "Knowledge": "Bilgi", "Will": "İrade", "Concentration": "Konsantrasyon",
 # saldırı / savunma
 "Magic Attack": "Büyü Saldırısı", "Physical Attack": "Fiziksel Saldırı", "Physical Defense": "Fiziksel Savunma",
 "Magic Defense": "Büyü Savunması", "Magic Accuracy": "Büyü İsabeti", "Magic Boost": "Büyü Gücü Artışı",
 "Magic Resist": "Büyü Direnci", "Magic Resistance": "Büyü Direnci", "Back Defense": "Arka Savunma",
 "Additional Damage": "Ek Hasar", "Additional Defense": "Ek Savunma", "Ignore Defense": "Savunma Yok Sayma",
 "Final Damage": "Son Hasar", "Combat Power": "Savaş Gücü",
 # kritik / mükemmel / çoklu vuruş
 "Critical Strike": "Kritik Vuruş", "Critical Hit": "Kritik Vuruş", "Critical Damage": "Kritik Hasar",
 "Critical Hit Chance": "Kritik Vuruş Şansı", "Critical Damage Boost": "Kritik Hasar Artışı",
 "Critical Hit Resist": "Kritik Vuruş Direnci", "Critical Strike Resist": "Kritik Vuruş Direnci",
 "Critical Damage Resist": "Kritik Hasar Direnci", "Perfect Chance": "Mükemmel Vuruş Şansı",
 "Perfect Resist": "Mükemmel Vuruş Direnci", "Multi-Hit": "Çoklu Vuruş", "Multi-Hit Chance": "Çoklu Vuruş Şansı",
 "Multi-Hit Resist": "Çoklu Vuruş Direnci", "Backstab Damage": "Arkadan Vuruş Hasarı",
 # hasar artış / azaltma
 "Damage Boost": "Hasar Artışı", "Damage Bonus": "Hasar Bonusu", "Damage Reduction": "Hasar Azaltma",
 "Damage Amplification": "Hasar Güçlendirme", "Weapon Damage": "Silah Hasarı", "Weapon Damage Boost": "Silah Hasarı Artışı",
 "Power Shard Damage Bonus": "Power Shard Hasar Bonusu", "Boss Damage": "Boss Hasarı",
 "PvE Damage": "PvE Hasarı", "PvP Damage": "PvP Hasarı", "PvE Defense": "PvE Savunması", "PvP Defense": "PvP Savunması",
 "PvP Attack": "PvP Saldırısı", "PvE Attack": "PvE Saldırısı",
 # delme
 "Block Penetration": "Blok Delme", "Parry Penetration": "Savuşturma Delme", "Defense Penetration": "Savunma Delme",
 "Armor Penetration": "Zırh Delme",
 # hız / süre
 "Attack Speed": "Saldırı Hızı", "Casting Speed": "Büyü Hızı", "Movement Speed": "Hareket Hızı",
 "Cooldown": "Bekleme Süresi", "Cooldown Reduction": "Bekleme Süresi Azaltma", "Cast Time": "Hazırlanma Süresi",
 # iyileştirme / direnç
 "Healing Boost": "İyileştirme Artışı", "Healing Power": "İyileştirme Gücü", "Healing Received": "Alınan İyileştirme",
 "Stun Resist": "Sersemletme Direnci", "Status Resist": "Durum Direnci", "Status Effect Resist": "Durum Etkisi Direnci",
 "Accuracy Bonus": "İsabet Bonusu", "Evasion Bonus": "Kaçınma Bonusu", "Soul Binding Rate": "Ruh Bağlama Oranı",
}
istatistik = [t for t in istatistik if t not in statlar]
buyukHarf = [t for t in buyukHarf if t not in statlar]
koru = list(dict.fromkeys(cekirdek + istatistik + secilen))
# Beceri adları çalışma anında skills/skill_names.json'dan okunur. Tek kelimelik ve sıradan İngilizce sözcük olanlar atlanır:
beceriler = json.load(open(kok + '/assets/aion2/skills/skill_names.json', encoding='utf-8'))
yaygin = sorted({x.strip() for x in beceriler.values() if x and ' ' not in x.strip() and ('▁' + x.strip().lower()) in vocab})
# Sabit karşılık: sadece düz arayüz sözcükleri, sadece satırın tamamı (ya da "terim + sayı") eşleşirse.
terimler = {
 "Account Bound": "Hesaba Bağlı", "Character Bound": "Karaktere Bağlı", "Bound": "Bağlı",
 "Tradable": "Takas edilebilir", "Untradable": "Takas edilemez", "Cannot be traded": "Takas edilemez",
 "Sell Price": "Satış Fiyatı", "Level": "Seviye", "Required Level": "Gereken Seviye", "Durability": "Dayanıklılık",
 "Duration": "Süre", "Range": "Menzil", "Target": "Hedef", "Passive": "Pasif", "Active": "Aktif",
 "Instant": "Anında", "Instant Cast": "Anında",
 "Common": "Sıradan", "Rare": "Nadir", "Unique": "Eşsiz", "Legendary": "Efsanevi", "Epic": "Destansı", "Mythic": "Mitik",
 "Quest": "Görev", "Reward": "Ödül", "Rewards": "Ödüller", "Accept": "Kabul et", "Decline": "Reddet", "Complete": "Tamamlandı",
}
cikti = {
 "_not": [
  "aionmeter çeviri sözlüğü (BAŞLANGIÇ SETİ - elle genişletilebilir). Oyun terimleri İngilizce kalır (kullanıcı kararı).",
  "koru: her zaman korunan terimler (büyük/küçük harf duyarlı, uzun olan önce). Yer/boss adları zones.json ve boss_npcs.json'dan.",
  "koruBuyukHarf: sıradan İngilizce sözcük olan terimler; sadece cümle ortasında Büyük Harfle yazılınca ya da satırın tamamıysa korunur.",
  "beceriDosyasi: beceri adları (ilk kullanımda okunur). Çok kelimeli adlar aynen, tek kelimelik adlar koruBuyukHarf kuralıyla korunur; 'yaygin' listesindekiler atlanır.",
  "terimler: SADECE satırın tamamı bu terimse ya da 'terim + sayı' ise kullanılır ('Required Level: 25' -> 'Gereken Seviye: 25').",
  "statlar: stat adları Türkçe yazılır (cümlede sadece Türkçe, stat satırında 'Türkçe (İngilizce)'); tek sözcüklüler sadece Büyük Harfle cümle ortasında."
 ],
 "koru": koru, "koruBuyukHarf": buyukHarf, "beceriDosyasi": "../skills/skill_names.json", "yaygin": yaygin, "terimler": terimler,
 "statlar": statlar}
import os
os.makedirs(kok + '/assets/aion2/ceviri', exist_ok=True)
with open(kok + '/assets/aion2/ceviri/sozluk.json', 'w', encoding='utf-8', newline='\n') as f:
    json.dump(cikti, f, ensure_ascii=False, indent=1)
print('statlar', len(statlar), 'koru', len(koru), 'buyukHarf', len(buyukHarf), 'yaygin', len(yaygin), 'terimler', len(terimler), 'atilan', len(atilan))
print('ATILAN:', atilan)
