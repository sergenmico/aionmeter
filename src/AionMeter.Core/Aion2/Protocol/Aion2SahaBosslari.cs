// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2FieldBosses.cs (TryParse, ReadHeader). Değiştirildi.
using System.Buffers.Binary;

namespace AionDPS.Aion2.Protocol;

/// <summary>Oyunun saha bossu listesindeki bir kayıt. <see cref="Yer"/> = harita × 100 + sıra (ör. 101001, Reshanta'da 2001).
/// <see cref="ZamanMs"/>: 1970'ten ms (UTC); canlıysa göründüğü an, ölüyse geri geleceği an; 0 = bilinmiyor.</summary>
public readonly record struct SahaBossKaydi(int Yer, bool Canli, float X, float Y, long ZamanMs);

/// <summary>
/// Dünya haritasındaki saha bossu listesi açıkken sunucunun gönderdiği çerçeve (opcode 0x0191):
/// <c>opcode(2) | 0 u16 | harita u32 | adet u8 | adet × { canlı u8 (0/1), yer varint (harita × 100 + sıra), [canlıysa x y z f32],
/// [bazı yerlerde 1 bayt boşluk], zaman i64 ms }</c>, sonra sıfırlar. Boşluk baytı ancak bir sonraki kaydın (ya da sonun) başka türlü
/// tutmamasından anlaşılır; iki okuma da denenir. Böyle okunmayan her şey reddedilir (aynı opcode'un başka çerçeveleri de olabilir).
/// </summary>
public static class Aion2SahaBosslari
{
    public const int Opcode = 0x0191;

    // Değiştirildi: harita alt sınırı 1 (referans 1000'den başlatıyordu; Reshanta haritaları 20 ve 22).
    private const int EnAzHarita = 1, EnCokHarita = 99_999, EnCokKayit = 64;
    private const long EnAzZaman = 1_600_000_000_000, EnCokZaman = 2_600_000_000_000;

    /// <summary>Çerçeveyi okur; <paramref name="kayitlar"/> temizlenip doldurulur (çağıran yeniden kullanır, ayırma yok).
    /// <paramref name="frame"/> opcode'la başlar. Liste okunamazsa false ve <paramref name="kayitlar"/> boş.</summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, out int harita, List<SahaBossKaydi> kayitlar)
    {
        kayitlar.Clear();
        harita = 0;
        if (frame.Length < 9 || frame[2] != 0 || frame[3] != 0)
        {
            return false;
        }

        harita = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]), int.MaxValue);
        int adet = frame[8];
        if (harita is < EnAzHarita or > EnCokHarita || adet is 0 or > EnCokKayit)
        {
            kayitlar.Clear();
            return false;
        }

        int o = 9;
        for (int n = 0; n < adet; n++)
        {
            if (!BaslikOku(frame, o, harita, out bool canli, out int yer, out int at))
            {
                kayitlar.Clear();
                return false;
            }

            float x = 0, y = 0;
            if (canli)
            {
                if (at + 12 > frame.Length)
                {
                    kayitlar.Clear();
                    return false;
                }

                x = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(at, 4));
                y = BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(at + 4, 4));
                at += 12; // x, y, z
            }

            bool okundu = false;
            for (int bosluk = 0; bosluk <= 1 && !okundu; bosluk++)
            {
                int t = at + bosluk;
                if (t + 8 > frame.Length)
                {
                    break;
                }

                long zaman = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(t, 8));
                if (zaman != 0 && (zaman < EnAzZaman || zaman > EnCokZaman))
                {
                    continue;
                }

                int son = t + 8;
                bool tutuyor = n == adet - 1
                    ? frame[son..].IndexOfAnyExcept((byte)0) < 0 && frame.Length - son <= 8
                    : BaslikOku(frame, son, harita, out _, out _, out _);
                if (!tutuyor)
                {
                    continue;
                }

                kayitlar.Add(new SahaBossKaydi(yer, canli, x, y, zaman));
                o = son;
                okundu = true;
            }

            if (!okundu)
            {
                kayitlar.Clear();
                return false;
            }
        }

        return true;
    }

    /// <summary><c>canlı u8 (0/1), yer varint</c>; yer bu haritanın aralığında olmalı (harita × 100 + 1..99).</summary>
    private static bool BaslikOku(ReadOnlySpan<byte> frame, int o, int harita, out bool canli, out int yer, out int sonraki)
    {
        canli = false;
        yer = 0;
        sonraki = o;
        if (o >= frame.Length || frame[o] > 1)
        {
            return false;
        }

        canli = frame[o] == 1;
        int p = o + 1;
        long deger = 0;
        for (int kaydir = 0; kaydir < 35; kaydir += 7)
        {
            if (p >= frame.Length)
            {
                return false;
            }

            byte b = frame[p++];
            deger |= (long)(b & 0x7F) << kaydir;
            if ((b & 0x80) == 0)
            {
                long sira = deger - (long)harita * 100;
                if (sira is < 1 or > 99)
                {
                    return false;
                }

                yer = (int)deger; // Değiştirildi: sıra değil tam yer kimliği tutulur (questlog kimliğiyle aynı)
                sonraki = p;
                return true;
            }
        }

        return false;
    }
}
