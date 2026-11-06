// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Capture/TcpReassembler.cs. Değiştirildi.
// Değiştirildi: dolmayan boşluk süreyle de bırakılır (GapTimeout); her çerçeve, son baytını taşıyan parçanın kendi zaman damgasıyla verilir
// (bekleyen parçalar boşluk dolunca/bırakılınca hepsi o anki parçanın zamanıyla çıkıyordu: sahte süre ve DPS sıçraması).
using System.Buffers.Binary;
using AionDPS.Aion2.Protocol;

namespace AionDPS.Aion2.Capture;

/// <summary>
/// Puts captured TCP segments back into a byte stream per direction and cuts that stream into
/// game frames using the length prefix described by <see cref="FrameLayout"/>. Out-of-order
/// segments wait for the missing piece; retransmissions are dropped; a hole that never fills
/// (packet loss on the capture side, not on the wire) is given up on after a bound, the stream
/// realigned at the next segment and the event counted in <see cref="Gaps"/> - a frame boundary
/// is then found again by the length prefix, so at most the frames spanning the hole are lost.
/// </summary>
public sealed class TcpReassembler
{
    private const int MaxPendingBytes = 256 * 1024;
    private const int MaxPendingSegments = 64;

    /// <summary>Değiştirildi (eklendi): sıra dışı bir parçadan sonra eksik parça bu kadar sürede gelmezse gelmeyecek sayılır (Npcap düşürdü).</summary>
    public static readonly TimeSpan GapTimeout = TimeSpan.FromMilliseconds(300);

    private readonly Dictionary<string, StreamState> _streams = new();

    public int Gaps { get; private set; }
    public int Retransmissions { get; private set; }
    public int Frames { get; private set; }
    public int Desyncs { get; private set; }
    /// <summary>TLS records of another connection on the same stream, skipped.</summary>
    public int TlsRecords { get; private set; }

    /// <summary>Değiştirildi (eklendi): <see cref="Gaps"/> içinden süre dolduğu için bırakılan boşluklar.</summary>
    public int TimedGaps { get; private set; }

    private static readonly IReadOnlyList<(ReadOnlyMemory<byte> Frame, DateTime At)> NoFrames = Array.Empty<(ReadOnlyMemory<byte>, DateTime)>();

    public IReadOnlyList<(ReadOnlyMemory<byte> Frame, DateTime At)> Push(TcpSegment segment, FrameLayout layout, IReadOnlySet<int>? syncOpcodes = null)
    {
        if (segment.Payload.Length == 0)
        {
            return NoFrames;
        }

        PruneIdle(segment.Timestamp);
        if (!_streams.TryGetValue(segment.StreamKey, out StreamState? stream))
        {
            stream = new StreamState { NextSequence = segment.Sequence };
            _streams[segment.StreamKey] = stream;
        }

        stream.LastSeen = segment.Timestamp;
        var frames = new List<(ReadOnlyMemory<byte>, DateTime)>();
        Append(stream, segment.Sequence, segment.Payload, segment.Timestamp);
        if (stream.HoleSince is { } since && segment.Timestamp - since > GapTimeout)
        {
            // Değiştirildi (eklendi): boşluk süresinde dolmadı; önce boşluğa kadarki çerçeveler, sonra bekleyenlerden devam.
            CutFrames(stream, layout, syncOpcodes, frames);
            GiveUp(stream, timed: true);
        }

        CutFrames(stream, layout, syncOpcodes, frames);
        return frames;
    }

    /// <summary>
    /// Değiştirildi (eklendi): yeni parça gelmese de (trafik azaldı) <see cref="GapTimeout"/>'tan eski boşluklar bırakılır ve bekleyen
    /// parçaların çerçeveleri kendi zamanlarıyla verilir. Yakalama işçisi boşta kaldıkça çağırır.
    /// </summary>
    public IReadOnlyList<(ReadOnlyMemory<byte> Frame, DateTime At)> Flush(DateTime now, FrameLayout layout, IReadOnlySet<int>? syncOpcodes = null)
    {
        List<(ReadOnlyMemory<byte>, DateTime)>? frames = null;
        foreach (StreamState stream in _streams.Values)
        {
            if (stream.HoleSince is { } since && now - since > GapTimeout)
            {
                frames ??= new List<(ReadOnlyMemory<byte>, DateTime)>();
                GiveUp(stream, timed: true);
                CutFrames(stream, layout, syncOpcodes, frames);
            }
        }

        return frames ?? NoFrames;
    }

    // Değiştirildi (eklendi): boşluk dolmayacak; tampondaki yarım çerçeve atılır, en eski bekleyen parçadan devam edilir.
    private void GiveUp(StreamState stream, bool timed)
    {
        if (stream.Pending.Count == 0)
        {
            stream.HoleSince = null;
            return;
        }

        Gaps++;
        if (timed)
        {
            TimedGaps++;
        }

        stream.Buffer.Clear();
        stream.Marks.Clear();
        stream.Synced = false;
        stream.NextSequence = stream.Pending.Keys.First();
        DrainPending(stream);
        if (stream.Pending.Count > 0)
        {
            // arkasında başka bir boşluk var: onun süresi kalan en eski parçadan başlar
            DateTime oldest = DateTime.MaxValue;
            foreach ((ReadOnlyMemory<byte> _, DateTime at) in stream.Pending.Values)
            {
                oldest = at < oldest ? at : oldest;
            }

            stream.HoleSince = oldest;
        }
    }

    // Değiştirildi (eklendi): 60 sn'den uzun süredir parça gelmeyen akışlar (kapanmış bağlantılar) atılır.
    private static readonly TimeSpan StreamIdle = TimeSpan.FromSeconds(60);
    private DateTime _lastPrune = DateTime.MinValue;

    private void PruneIdle(DateTime now)
    {
        if (now - _lastPrune < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastPrune = now;
        foreach (string key in _streams.Where(kv => now - kv.Value.LastSeen > StreamIdle).Select(kv => kv.Key).ToList())
        {
            _streams.Remove(key);
        }
    }

    private void Append(StreamState stream, uint sequence, ReadOnlyMemory<byte> payload, DateTime at)
    {
        long delta = (int)(sequence - stream.NextSequence);
        if (delta == 0)
        {
            stream.Add(payload.Span, at);
            stream.NextSequence = unchecked(sequence + (uint)payload.Length);
            DrainPending(stream);
            return;
        }

        if (delta < 0)
        {
            // Already have (part of) this: keep only the bytes beyond what was consumed.
            long overlap = -delta;
            if (overlap >= payload.Length)
            {
                Retransmissions++;
                return;
            }

            Retransmissions++;
            stream.Add(payload.Span[(int)overlap..], at);
            stream.NextSequence = unchecked(sequence + (uint)payload.Length);
            DrainPending(stream);
            return;
        }

        // A retransmission of a waiting segment replaces it: the byte count must follow the replacement, not grow by it.
        if (stream.Pending.TryGetValue(sequence, out var replaced))
        {
            stream.PendingBytes -= replaced.Data.Length;
        }

        stream.Pending[sequence] = (payload, at);
        stream.PendingBytes += payload.Length;
        stream.HoleSince ??= at;
        if (stream.Pending.Count > MaxPendingSegments || stream.PendingBytes > MaxPendingBytes)
        {
            // The hole is not going to fill. Start over from the oldest waiting segment; whatever
            // half-frame sits in the buffer is unusable now.
            GiveUp(stream, timed: false);
        }
    }

    private static void DrainPending(StreamState stream)
    {
        while (stream.Pending.Count > 0)
        {
            uint first = stream.Pending.Keys.First();
            long delta = (int)(first - stream.NextSequence);
            if (delta > 0)
            {
                return;
            }

            (ReadOnlyMemory<byte> payload, DateTime at) = stream.Pending[first];
            stream.Pending.Remove(first);
            stream.PendingBytes -= payload.Length;
            long overlap = -delta;
            if (overlap < payload.Length)
            {
                stream.Add(payload.Span[(int)overlap..], at); // Değiştirildi: bekleyen parça kendi zamanıyla
                stream.NextSequence = unchecked(first + (uint)payload.Length);
            }
        }

        stream.HoleSince = null; // Değiştirildi (eklendi): boşluk doldu
    }

    private void CutFrames(StreamState stream, FrameLayout layout, IReadOnlySet<int>? syncOpcodes, List<(ReadOnlyMemory<byte>, DateTime)> frames)
    {
        if (layout.IsVarint)
        {
            CutVarintFrames(stream, layout, frames, syncOpcodes);
            return;
        }

        while (stream.Buffer.Count >= layout.HeaderSize)
        {
            int declared = ReadLength(stream.Buffer, layout);
            int total = layout.LengthIncludesHeader ? declared : declared + layout.HeaderSize;
            if (total < layout.HeaderSize || total > layout.MaxFrameLength)
            {
                // Not looking at a frame start - drop until the next plausible length prefix.
                Desyncs++;
                stream.Drop(1);
                continue;
            }

            if (stream.Buffer.Count < total)
            {
                break;
            }

            frames.Add((stream.Buffer.GetRange(0, total).ToArray(), stream.TimeOf(total)));
            stream.Drop(total);
            Frames++;
        }
    }

    /// <summary>Varint framing: LEB128 length L, record = L + prefixBytes + bias bytes in total; the
    /// emitted frame is the record without its prefix. An implausible length means we are not at a
    /// record start (a capture that began mid-stream) - drop one byte and look again.</summary>
    private void CutVarintFrames(StreamState stream, FrameLayout layout, List<(ReadOnlyMemory<byte>, DateTime)> frames, IReadOnlySet<int>? syncOpcodes)
    {
        while (stream.Buffer.Count > 0)
        {
            // A capture that starts in the middle of a connection begins in the middle of a frame.
            // One plausible-looking length is not proof of a frame start, and trusting it can make
            // the decoder wait for kilobytes of real traffic that then vanish into a phantom frame.
            // So while unsynced, the buffer is searched for the first offset where several COMPLETE
            // frames in a row line up; everything before it is dropped, and candidates that do not
            // fit are simply skipped rather than waited for.
            if (!stream.Synced)
            {
                int offset = FindSyncOffset(stream.Buffer, layout, syncOpcodes);
                if (offset < 0)
                {
                    // Nothing lines up yet. Keep only the tail (a real frame start can still be in
                    // it) so a stream that never syncs cannot grow without bound.
                    if (stream.Buffer.Count > SyncGiveUpBytes)
                    {
                        Desyncs++;
                        stream.Drop(stream.Buffer.Count - SyncSearchWindow);
                    }

                    return;
                }

                if (offset > 0)
                {
                    Desyncs++;
                    stream.Drop(offset);
                }

                stream.Synced = true;
            }

            // Zero bytes between frames are padding (a length of 0 is no frame), and a TLS record the game server sends from the
            // same port is no game frame either: read as one, "17" is a length of 23 and the walk lands in ciphertext,
            // swallowing real frames. Both are skipped here instead of costing a resync (a competitor meter measured 20-35 % of
            // a boss's damage lost that way).
            int zeros = 0;
            while (zeros < stream.Buffer.Count && stream.Buffer[zeros] == 0)
            {
                zeros++;
            }

            if (zeros > 0)
            {
                stream.Drop(zeros);
                continue;
            }

            if (TlsRecordSize(stream.Buffer) is int tlsSize && (syncOpcodes is null || !syncOpcodes.Contains(TlsLookalikeOpcode)))
            {
                if (stream.Buffer.Count < tlsSize)
                {
                    return; // the rest of the record is still to come
                }

                stream.Drop(tlsSize);
                TlsRecords++;
                continue;
            }

            long length = 0;
            int prefix = 0;
            bool complete = false;
            while (prefix < 5 && prefix < stream.Buffer.Count)
            {
                byte b = stream.Buffer[prefix];
                length |= (long)(b & 0x7f) << (7 * prefix);
                prefix++;
                if ((b & 0x80) == 0)
                {
                    complete = true;
                    break;
                }
            }

            if (!complete)
            {
                if (prefix >= 5)
                {
                    Desyncs++;
                    stream.Synced = false;
                    stream.Drop(1);
                    continue;
                }

                return; // the length itself is still arriving
            }

            long total = length + prefix + layout.LengthBias;
            int bodyMin = Math.Max(layout.OpcodeOffset + layout.OpcodeSize, 1);
            bool tooBig = total > layout.MaxFrameLength;
            if (tooBig && total <= MaxKnownFrameLength && syncOpcodes is { Count: > 0 } && total - prefix >= bodyMin)
            {
                // A login sends frames larger than any in a fight (the character's whole record, 40 kB): in a synced stream, a big frame
                // whose opcode is a known one is a real frame, not a lost sync. Dropping it threw away the rest of the login burst,
                // among it the account counters (the Odyle energy of the character that just logged in).
                int opcodeEnd = prefix + layout.OpcodeOffset + layout.OpcodeSize;
                if (stream.Buffer.Count < opcodeEnd)
                {
                    return; // the opcode is still arriving
                }

                int opcodeAt = prefix + layout.OpcodeOffset;
                int opcode = layout.OpcodeBigEndian
                    ? stream.Buffer[opcodeAt] << 8 | stream.Buffer[opcodeAt + 1]
                    : stream.Buffer[opcodeAt + 1] << 8 | stream.Buffer[opcodeAt];
                tooBig = !syncOpcodes.Contains(opcode);
            }

            if (total - prefix < bodyMin || tooBig)
            {
                Desyncs++;
                stream.Synced = false;
                stream.Drop(1);
                continue;
            }

            if (stream.Buffer.Count < total)
            {
                return;
            }

            frames.Add((stream.Buffer.GetRange(prefix, (int)total - prefix).ToArray(), stream.TimeOf((int)total)));
            stream.Drop((int)total);
            Frames++;
        }
    }

    /// <summary>A TLS record header read as a game frame: length 0x17 = 23, then the opcode bytes 03 03. No game frame starts so.</summary>
    private const int TlsLookalikeOpcode = 0x0303;

    /// <summary>Bytes a TLS record at the start of the buffer occupies, header included: content type 20-23, version 3.1-3.4,
    /// then a big-endian length of at most 2^14 + 256. Null when the buffer does not start with one.</summary>
    private static int? TlsRecordSize(List<byte> b)
    {
        if (b.Count < 5 || b[0] < 0x14 || b[0] > 0x17 || b[1] != 0x03 || b[2] < 0x01 || b[2] > 0x04)
        {
            return null;
        }

        int len = b[3] << 8 | b[4];
        return len > 0 && len <= 16384 + 256 ? 5 + len : null;
    }

    private const int MaxKnownFrameLength = 1 << 18;
    private const int SyncChainLength = 3;
    private const int SyncSearchWindow = 4096;
    private const int SyncGiveUpBytes = 8192;

    /// <summary>First offset (within the search window) at which <see cref="SyncChainLength"/>
    /// complete, plausible varint frames follow each other inside the buffer; -1 if none.</summary>
    private static int FindSyncOffset(List<byte> buffer, FrameLayout layout, IReadOnlySet<int>? syncOpcodes)
    {
        int last = Math.Min(buffer.Count - 1, SyncSearchWindow);
        for (int start = 0; start <= last; start++)
        {
            if (ChainFits(buffer, start, layout, syncOpcodes))
            {
                return start;
            }
        }

        return -1;
    }

    private static bool ChainFits(List<byte> buffer, int start, FrameLayout layout, IReadOnlySet<int>? syncOpcodes)
    {
        int pos = start;
        for (int n = 0; n < SyncChainLength; n++)
        {
            long length = 0;
            int prefix = 0;
            bool complete = false;
            while (prefix < 5 && pos + prefix < buffer.Count)
            {
                byte b = buffer[pos + prefix];
                length |= (long)(b & 0x7f) << (7 * prefix);
                prefix++;
                if ((b & 0x80) == 0)
                {
                    complete = true;
                    break;
                }
            }

            if (!complete)
            {
                return false;
            }

            long total = length + prefix + layout.LengthBias;
            if (total - prefix < Math.Max(layout.OpcodeOffset + layout.OpcodeSize, 1) || total > layout.MaxFrameLength || pos + total > buffer.Count)
            {
                return false;
            }

            // The frame's opcode must be one seen in real traffic (when such a list exists).
            if (syncOpcodes is { Count: > 0 })
            {
                int opcodeAt = pos + prefix + layout.OpcodeOffset;
                int opcode = layout.OpcodeBigEndian
                    ? buffer[opcodeAt] << 8 | buffer[opcodeAt + 1]
                    : buffer[opcodeAt + 1] << 8 | buffer[opcodeAt];
                if (!syncOpcodes.Contains(opcode))
                {
                    return false;
                }
            }

            pos += (int)total;

            // Frames that tile the buffer exactly - the usual case when the server sends one
            // message - are as convincing as a chain of three, even for a single frame.
            if (pos == buffer.Count)
            {
                return true;
            }
        }

        return true;
    }

    private static int ReadLength(List<byte> buffer, FrameLayout layout)
    {
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < layout.LengthSize; i++)
        {
            bytes[i] = buffer[layout.LengthOffset + i];
        }

        return layout.LengthSize switch
        {
            1 => bytes[0],
            2 => layout.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
            _ => (int)(layout.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes)),
        };
    }

    private sealed class StreamState
    {
        public uint NextSequence;
        public DateTime LastSeen;
        public bool Synced;
        public readonly List<byte> Buffer = new();
        public readonly SortedDictionary<uint, (ReadOnlyMemory<byte> Data, DateTime At)> Pending = new();
        public int PendingBytes;

        /// <summary>Değiştirildi (eklendi): ilk sıra dışı parçanın geliş zamanı (boşluk açık); boşluk yokken null.</summary>
        public DateTime? HoleSince;

        /// <summary>Değiştirildi (eklendi): tampondaki baytların zamanı: her parça için (bitiş konumu, parçanın zamanı); konumlar Buffer'a göredir.</summary>
        public readonly List<(int End, DateTime At)> Marks = new();

        public void Add(ReadOnlySpan<byte> bytes, DateTime at)
        {
            Buffer.AddRange(bytes);
            if (Marks.Count > 0 && Marks[^1].At == at)
            {
                Marks[^1] = (Buffer.Count, at);
            }
            else
            {
                Marks.Add((Buffer.Count, at));
            }
        }

        /// <summary>İlk <paramref name="length"/> baytın son baytını getiren parçanın zamanı.</summary>
        public DateTime TimeOf(int length)
        {
            foreach ((int end, DateTime at) in Marks)
            {
                if (end >= length)
                {
                    return at;
                }
            }

            return Marks.Count > 0 ? Marks[^1].At : LastSeen;
        }

        /// <summary>Tamponun başından <paramref name="count"/> bayt atılır; zaman işaretleri kaydırılır.</summary>
        public void Drop(int count)
        {
            Buffer.RemoveRange(0, count);
            int keep = 0;
            for (int i = 0; i < Marks.Count; i++)
            {
                int end = Marks[i].End - count;
                if (end > 0)
                {
                    Marks[keep++] = (end, Marks[i].At);
                }
            }

            Marks.RemoveRange(keep, Marks.Count - keep);
        }
    }
}
