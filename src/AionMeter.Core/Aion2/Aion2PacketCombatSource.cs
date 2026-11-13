// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2PacketCombatSource.cs. Değiştirildi.
// Değiştirildi: ICombatSource/Poll kuyruğu, karakter kaydı, bilet/ganimet/hata ayıklama kayıtları ve geri bildirim halkası kaldırıldı.
// Çözülen olaylar bekletilmeden, yakalama işçi iş parçacığında Hit/ZoneChanged ile verilir (olay başına bellek tutulmaz).
using AionDPS.Combat;
using AionDPS.Combat.Sources;
using AionMeter;

namespace AionDPS.Aion2;

/// <summary>
/// Aion 2 input: the UE5 client writes no Chat.log, so combat is read from the game's own network
/// traffic instead (Npcap capture of the TCP stream to the game server, then the game's frame and
/// opcode layout). Nothing is injected into or read from the game process.
/// </summary>
public sealed class Aion2PacketCombatSource : IDisposable
{
    private readonly Capture.NpcapAvailability _npcap = Capture.NpcapAvailability.Detect();
    private readonly Protocol.Aion2Protocol _protocol;
    private readonly Capture.TcpReassembler _reassembler = new();
    private readonly Protocol.Aion2FrameDecoder _decoder;
    private readonly Aion2EntityDirectory _entities = new();
    private readonly string? _adapterId;
    private Capture.NpcapCaptureService? _capture;

    public Aion2PacketCombatSource(Protocol.Aion2Protocol protocol, string? adapterId = null)
    {
        _adapterId = adapterId;
        _protocol = protocol;
        _decoder = new Protocol.Aion2FrameDecoder(protocol, _entities);
        _onHit = ev => Hit?.Invoke(ev);
        _onZone = zone =>
        {
            _entities.ForgetFight();
            ZoneChanged?.Invoke(zone);
        };
    }

    private readonly Action<DamageEvent> _onHit;
    private readonly Action<ZoneChange> _onZone;

    public Aion2EntityDirectory Entities => _entities;

    /// <summary>The id of the zone the local player is in (0 until the game has sent one).</summary>
    public int CurrentZoneId => _decoder.CurrentZoneId;

    /// <summary>Değiştirildi (eklendi): oyun bağlantısının son 5 sn'lik pingi (ms); yakalama yoksa ya da ölçüm güvenilir değilse null.</summary>
    public int? PingMs => _capture?.Ping.Ms();

    /// <summary>Değiştirildi (eklendi): çözülen her hasar/iyileştirme olayı; yakalama işçi iş parçacığında çağrılır.</summary>
    public event Action<DamageEvent>? Hit;

    /// <summary>Değiştirildi (eklendi): bölge değişti; yakalama işçi iş parçacığında çağrılır.</summary>
    public event Action<ZoneChange>? ZoneChanged;

    /// <summary>Değiştirildi: durum doğrudan EngineState olarak bildirilir.</summary>
    public event Action<EngineState, string>? StatusChanged;

    public void Start()
    {
        if (!_npcap.IsInstalled)
        {
            StatusChanged?.Invoke(EngineState.NoNpcap, "Npcap kurulu değil. Ölçer oyunun ağ trafiğini okur ve Npcap sürücüsüne ihtiyaç duyar (npcap.com).");
            return;
        }

        if (!_protocol.IsCalibrated)
        {
            StatusChanged?.Invoke(EngineState.Failed, "Paket düzeni (assets/aion2/protocol/opcodes.json) okunamadı ya da bu oyun sürümü için ayarlı değil.");
            return;
        }

        _capture = new Capture.NpcapCaptureService(_protocol.ServerPorts, Ingest, (state, message) => StatusChanged?.Invoke(state, message), _adapterId) { OnTick = Tick };
        _capture.Start();
    }

    public void Stop()
    {
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();

    /// <summary>Feeds one captured segment through reassembly and decoding - the live capture's
    /// callback, and what a recorded fixture is replayed through in the self-checks.</summary>
    public void Ingest(Capture.TcpSegment segment)
    {
        // Only the server's stream carries combat; the client's small command packets are a
        // different vocabulary and would only risk a false frame.
        if (!segment.FromServer)
        {
            return;
        }

        lock (_reassembler)
        {
            // Paket saati: süreli boşluk duvar saatine değil, son paketin yakalanma zamanına göre ilerler (kuyruk gerideyken erken bırakmasın).
            if (segment.Timestamp > _lastPacketAt)
            {
                _lastPacketAt = segment.Timestamp;
            }

            _lastPacketTick = Environment.TickCount64;

            // Değiştirildi: bölge değişimi segment sonunda değil, geldiği çerçeveden hemen sonra verilir (vuruşlarla aynı sırada).
            // Değiştirildi: her çerçeve kendi parçasının zamanıyla (bekleyen parçalar boşluk dolunca kendi zamanlarıyla çıkar).
            foreach ((ReadOnlyMemory<byte> frame, DateTime at) in _reassembler.Push(segment, _protocol.FrameLayout, _protocol.SyncOpcodes))
            {
                _decoder.Decode(frame.Span, at, _onHit, _onZone);
            }
        }
    }

    private DateTime _lastPacketAt;
    private long _lastPacketTick;

    /// <summary>
    /// Değiştirildi (eklendi): yakalama işçisi boşta kaldıkça (~100 ms) çağırır: süresi dolan TCP boşlukları bırakılır, Tanı açıksa dakikada
    /// bir sayaçlar yazılır (<paramref name="wallNow"/> = duvar saati). Boşluk süresi paket saatiyle ölçülür: son paketin zamanı + ondan beri
    /// geçen süre; kuyruk gerideyken duvar saati paketlerin ilerisindedir ve gelecek parçaları beklemeden boşluğu bırakırdı.
    /// </summary>
    public void Tick(DateTime wallNow)
    {
        lock (_reassembler)
        {
            if (_lastPacketAt != default)
            {
                FlushLocked(_lastPacketAt + TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastPacketTick));
            }

            if (MeterTani.Acik && wallNow - _countersAt >= TimeSpan.FromMinutes(1))
            {
                WriteCounters(wallNow);
            }
        }
    }

    /// <summary>Denetim aracı için: paket saatinde verilen ana göre süresi dolan boşlukları bırakır.</summary>
    internal void FlushAt(DateTime packetNow)
    {
        lock (_reassembler)
        {
            FlushLocked(packetNow);
        }
    }

    // Called under _reassembler.
    private void FlushLocked(DateTime packetNow)
    {
        foreach ((ReadOnlyMemory<byte> frame, DateTime at) in _reassembler.Flush(packetNow, _protocol.FrameLayout, _protocol.SyncOpcodes))
        {
            _decoder.Decode(frame.Span, at, _onHit, _onZone);
        }
    }

    private DateTime _countersAt = DateTime.Now;
    private Counters _lastCounters;

    private readonly record struct Counters(long Gaps, long TimedGaps, long LateAfterGap, long Desyncs, long Retrans, long Tls, long QueueDrops, long KernelDrops,
        long IfDrops, long BundleFailures, long Skipped, long Unknown, long CallbackErrors);

    // Called under _reassembler. Son satırdan bu yana artışlar (Δ) tek satırda. Kayıt hatası yakalamayı durdurmaz.
    private void WriteCounters(DateTime now)
    {
        _countersAt = now;
        try
        {
            (long received, long kernelDrops, long ifDrops) = _capture?.KernelStatistics() ?? (0, 0, 0);
            var c = new Counters(_reassembler.Gaps, _reassembler.TimedGaps, _reassembler.LateAfterGap, _reassembler.Desyncs, _reassembler.Retransmissions,
                _reassembler.TlsRecords, _capture?.Dropped ?? 0, kernelDrops, ifDrops, _decoder.BundleFailures, _decoder.SkippedShortFrames, _decoder.UnknownOpcodes,
                _capture?.CallbackErrors ?? 0);
            Counters p = _lastCounters;
            _lastCounters = c;
            MeterTani.Yaz?.Invoke($"{now:HH:mm:ss.fff} sayaç (son dakika): tcp boşluk +{c.Gaps - p.Gaps} (süreyle +{c.TimedGaps - p.TimedGaps}, "
                + $"bırakılan boşluğa geç gelen parça +{c.LateAfterGap - p.LateAfterGap}), desync +{c.Desyncs - p.Desyncs}, yeniden iletim +{c.Retrans - p.Retrans}, "
                + $"tls +{c.Tls - p.Tls}, kuyruk taşması +{c.QueueDrops - p.QueueDrops}, ps_drop +{c.KernelDrops - p.KernelDrops}, if_drop +{c.IfDrops - p.IfDrops}, "
                + $"lz4/paket hatası +{c.BundleFailures - p.BundleFailures}, atlanan çerçeve +{c.Skipped - p.Skipped}, bilinmeyen opcode +{c.Unknown - p.Unknown}, "
                + $"işleyici hatası +{c.CallbackErrors - p.CallbackErrors}; alınan paket {received}");
            if (_capture is { } capture)
            {
                (long ornek, long bozuk, long yeniden) = capture.Ping.Sayaclar();
                MeterTani.Yaz?.Invoke($"{now:HH:mm:ss.fff} ping: {(capture.Ping.Ms() is int ms ? ms + " ms" : "yok")} "
                    + $"(açılıştan beri örnek {ornek}, bozuk zaman damgası {bozuk}, yeniden gönderim {yeniden})");
            }
        }
        catch (Exception)
        {
            // tanı satırı yazılamadı: yakalama sürer
        }
    }
}
