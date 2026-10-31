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

        _capture = new Capture.NpcapCaptureService(_protocol.ServerPorts, Ingest, (state, message) => StatusChanged?.Invoke(state, message), _adapterId);
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
            // Değiştirildi: bölge değişimi segment sonunda değil, geldiği çerçeveden hemen sonra verilir (vuruşlarla aynı sırada).
            foreach (ReadOnlyMemory<byte> frame in _reassembler.Push(segment, _protocol.FrameLayout, _protocol.SyncOpcodes))
            {
                _decoder.Decode(frame.Span, segment.Timestamp, _onHit, _onZone);
            }
        }
    }
}
