// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Protocol/Aion2FrameDecoder.cs DecodeLocalPosition, PositionOffset / DecodePosition,
// DecodeBuff (sondaki x, y, z: etkiyi koyanın yeri), DecodeZone (18 ve 22'de oyuncunun yeri) ve DecodeNpcSpawn (canavarın yeri).
// Değiştirildi (geri eklendi): yalnız yerel oyuncunun yeri ve çevresindeki varlıkların yeri okunur (Gizli Küp yön/mesafesi için);
// etkilerin kendisi, tekrar oynatma izleri ve harita okunmaz. Pasif okuma: sadece gelen çerçeveler.
using System.Buffers.Binary;

namespace AionDPS.Aion2.Protocol;

public sealed partial class Aion2FrameDecoder
{
    /// <summary>Bir varlığa etki kondu (0x2a38) / yenilendi (0x2b38): hedef (varint) | "kondu"da 01 | tür | örnek (varint) | etki u32 | süre u32 |
    /// kalıcı u32 | 8 bayt | koyan (varint) | ... | x, y, z (f32) sonda. Referans: kayıttaki 206.692 etki çerçevesinin hepsi makul bir x, y, z ile biter;
    /// yerel oyuncu koyduğunda konum çerçevesiyle santimine kadar aynıdır (iki dakikalık dövüşte ~130 bildirim, konum çerçevesinden ise 6).</summary>
    private const int EtkiKonduOpcode = 0x2a38, EtkiYenilendiOpcode = 0x2b38;

    /// <summary>Konumla ilgili çerçeveyse okur ve true döner (başka dal bu çerçeveleri okumaz).</summary>
    private bool KonumCercevesi(ReadOnlySpan<byte> frame, int opcode)
    {
        if (opcode is EtkiKonduOpcode or EtkiYenilendiOpcode)
        {
            KonumEtki(frame, yenilendi: opcode == EtkiYenilendiOpcode);
            return true;
        }

        if ((opcode & 0xff) == 0x37 && HareketKonumu((opcode >> 8) & 0xff) is int kayma)
        {
            KonumHareket(frame, kayma);
            return true;
        }

        if (_protocol.FamilyOf(opcode) == OpcodeFamily.LocalPosition)
        {
            KonumCerceve(frame);
            return true;
        }

        return false;
    }

    /// <summary>Diğer varlıkların hareket çerçeveleri: ikinci bayt 0x37, birincisi aileyi söyler; yer, varlık kimliğinden bu kadar bayt sonra
    /// (referans: üç kayıtta bu kaymalarla bir varlığın okumaları saniyede birkaç metrelik yollar çizer).</summary>
    private static int? HareketKonumu(int ilk) => ilk switch
    {
        0x1a or 0x1b or 0x1c or 0x1e or 0x20 or 0x28 or 0x29 or 0x2a => 2,
        0x1f or 0x2f => 1,
        _ => null,
    };

    /// <summary>opcode | varlık (varint) | 1-2 bayt | x f32 | y f32 (santimetre).</summary>
    private void KonumHareket(ReadOnlySpan<byte> frame, int kayma)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long varlik) || varlik is < 1 or > 10_000_000 || frame.Length < p + kayma + 8)
        {
            return;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + kayma)..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + kayma + 4)..]);
        if (float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) is > 1 and < 200_000 && Math.Abs(y) is > 1 and < 200_000
            && !_entities.IsLocalPlayer((int)varlik))
        {
            _entities.Konum.Cevre((int)varlik, x, y);
        }
    }

    /// <summary>Konum çerçevesi (<see cref="OpcodeFamily.LocalPosition"/>): opcode | varlık (varint) | 03 | x f32 | y f32 ...</summary>
    private void KonumCerceve(ReadOnlySpan<byte> frame)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long varlik) || frame.Length < p + 9 || frame[p] != 3)
        {
            return;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 1)..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(frame[(p + 5)..]);
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return;
        }

        if (_entities.IsLocalPlayer(unchecked((int)varlik)))
        {
            _entities.Konum.Kesin(x, y);
        }
        else if (Math.Abs(x) is > 1 and < 200_000 && Math.Abs(y) is > 1 and < 200_000)
        {
            _entities.Konum.Cevre(unchecked((int)varlik), x, y); // başkasının atılması / düzeltmesi
        }
    }

    private void KonumEtki(ReadOnlySpan<byte> frame, bool yenilendi)
    {
        int p = 2;
        if (!TryReadVarint(frame, ref p, out long hedef) || hedef is < 1 or > 10_000_000 || p + 2 > frame.Length)
        {
            return;
        }

        if (!yenilendi && frame[p++] != 1)
        {
            return;
        }

        p += 1; // tür
        if (!TryReadVarint(frame, ref p, out _) || p + 12 + 8 > frame.Length)
        {
            return;
        }

        uint etki = BinaryPrimitives.ReadUInt32LittleEndian(frame[p..]);
        p += 12 + 8;
        if (!TryReadVarint(frame, ref p, out long koyan) || koyan is < 1 or > 10_000_000 || etki is < 10 or > 2_999_999_999u || frame.Length - 12 <= p)
        {
            return;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[^12..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(frame[^8..]);
        float z = BinaryPrimitives.ReadSingleLittleEndian(frame[^4..]);
        if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && Math.Abs(x) is > 100 and < 500_000 && Math.Abs(y) is > 100 and < 500_000
            && Math.Abs(z) < 50_000 && _entities.IsLocalPlayer((int)koyan))
        {
            _entities.Konum.Kesin(x, y);
        }
    }

    /// <summary>Bölge çerçevesinin koyduğu yer (ışınlanma, yükleme ekranı, dirilme): kesin bir bildirim. Yeni bölgede eski çevre geçersizdir.</summary>
    private void KonumBolge((float X, float Y)? yer, bool yeniBolge)
    {
        if (yeniBolge)
        {
            _entities.Konum.Temizle();
        }

        if (yer is { } y && float.IsFinite(y.X) && float.IsFinite(y.Y) && Math.Abs(y.X) is > 100 and < 500_000 && Math.Abs(y.Y) is > 100 and < 500_000)
        {
            _entities.Konum.Kesin(y.X, y.Y);
        }
    }

    /// <summary>Canavar doğuşu: NPC kimliğinden 6 bayt sonra x, 10 bayt sonra y.</summary>
    private void KonumDogus(ReadOnlySpan<byte> frame, int npcKonumu, long varlik)
    {
        if (frame.Length < npcKonumu + 14)
        {
            return;
        }

        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[(npcKonumu + 6)..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(frame[(npcKonumu + 10)..]);
        _entities.Konum.Cevre(unchecked((int)varlik), x, y);
    }
}
