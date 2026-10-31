// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Capture/NpcapCaptureService.cs. Değiştirildi.
using AionMeter;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AionDPS.Aion2.Capture;

/// <summary>
/// Reads the game's TCP traffic off every network adapter through Npcap and hands each segment's
/// payload to a callback. Filtered in the driver (BPF) to the game-server ports, so nothing else
/// on the machine is ever copied into this process; when no port is known yet (calibration
/// recordings) it falls back to all TCP. Purely passive: no packet is ever sent, and the game
/// process is never touched. Callbacks run on SharpPcap's capture threads.
/// <para>Safeguard: when no game traffic has arrived on the known ports for 30 s (another region, a Steam server or a ping
/// reducer on another port), the filter is widened to all TCP for up to two minutes and each segment's first three bytes
/// are compared with the game's keepalive (see <see cref="GameFlowDetector"/>); nothing else is read or kept, and the port found is
/// added to the known ones. Otherwise the filter returns to the known ports and the next try comes after five minutes.</para>
/// </summary>
public sealed class NpcapCaptureService : IDisposable
{
    private volatile int[] _serverPorts;
    private readonly GameFlowDetector _flowDetector = new();
    private Timer? _discoveryTimer;
    private DateTime _discoveryStarted;
    private volatile bool _discovering;
    private const int DiscoveryAfterSeconds = 30;
    private const int DiscoveryForSeconds = 120;
    private const int DiscoveryPauseSeconds = 300;
    private readonly Action<TcpSegment> _onSegment;
    private readonly Action<EngineState, string> _onStatus; // Değiştirildi: durum doğrudan motorun EngineState'i
    private readonly string? _adapterId;
    private readonly List<ILiveDevice> _devices = new();
    private long _packets;
    private volatile bool _disposed;

    // The capture threads only parse and enqueue; decoding, the ring buffer and the debug file run on
    // one worker. Before this, all of that ran inside the Npcap callback, so a burst (a world boss)
    // overflowed the driver's buffer and packets were lost.
    // Değiştirildi: düşük RAM için kuyruk 400.000 -> 20.000 parça, çekirdek tamponu 32 MB -> 4 MB.
    private const int MaxQueued = 20_000;
    private const int KernelBufferBytes = 4 * 1024 * 1024;
    private readonly System.Collections.Concurrent.BlockingCollection<TcpSegment> _queue = new(MaxQueued);
    private Thread? _worker;
    private long _dropped;

    public NpcapCaptureService(IReadOnlyList<int> serverPorts, Action<TcpSegment> onSegment, Action<EngineState, string> onStatus, string? adapterId = null)
    {
        _adapterId = adapterId;
        _serverPorts = serverPorts.ToArray();
        _onSegment = onSegment;
        _onStatus = onStatus;
    }

    /// <summary>"ip:port" of the game server, learned from the first server-side segment.</summary>
    public string? ServerEndpoint { get; private set; }

    public long Packets => Interlocked.Read(ref _packets);

    /// <summary>Packets whose handling threw. A bad packet is counted and skipped; before this, one
    /// exception in the callback ended the capture thread without a word.</summary>
    public int CallbackErrors => Volatile.Read(ref _callbackErrors);

    public string? LastCallbackError { get; private set; }

    /// <summary>Segments thrown away because the worker fell more than <see cref="MaxQueued"/> behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    private int _callbackErrors;

    /// <summary>Optional BPF network (for example <c>193.202.112.0/24</c>): everything TCP to or from it,
    /// whatever the port. Used by the diagnostic recording to find traffic - chat, say - that runs on
    /// another connection than the combat stream.</summary>
    public string? Network { get; init; }

    public string Filter => Network is { Length: > 0 }
        ? $"tcp and net {Network}"
        : _serverPorts.Length == 0 || _discovering
            ? "tcp"
            : "tcp and (" + string.Join(" or ", _serverPorts.Select(p => $"port {p}")) + ")";

    public void Start()
    {
        try
        {
            var all = CaptureDeviceList.Instance;
            if (all.Count == 0)
            {
                _onStatus(EngineState.NoAdapter, "Npcap ağ kartı bulamadı. Npcap kurulu ve hizmeti çalışıyor mu?");
                return;
            }

            IReadOnlyList<ILiveDevice> selected = CaptureAdapters.Select(all.Cast<ILiveDevice>().ToList(), _adapterId, out string? selectionNote);
            int opened = 0;
            _worker ??= StartWorker();
            lock (_devices) // Değiştirildi: Dispose ile yarışmasın
            {
            if (_disposed)
            {
                return;
            }

            foreach (ILiveDevice device in selected)
            {
                // Loopback and disconnected adapters are harmless to include; a failing one is
                // skipped rather than aborting the whole capture (VPN adapters do this).
                try
                {
                    device.Open(new DeviceConfiguration { ReadTimeout = 250, Mode = DeviceModes.None, KernelBufferSize = KernelBufferBytes });
                    device.Filter = Filter;
                    device.OnPacketArrival += OnPacketArrival;
                    device.StartCapture();
                    _devices.Add(device);
                    opened++;
                }
                catch (PcapException)
                {
                    device.Dispose();
                }
            }

            if (opened == 0)
            {
                _onStatus(EngineState.NoAdapter, $"Npcap {all.Count} ağ kartı buldu ama hiçbiri açılamadı.");
                return;
            }

            _discoveryTimer = new Timer(DiscoveryTick, null, TimeSpan.FromSeconds(DiscoveryAfterSeconds), TimeSpan.FromSeconds(DiscoveryAfterSeconds));
            }

            _onStatus(EngineState.WaitingGame, $"{opened} ağ kartı dinleniyor - oyun trafiği bekleniyor.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PcapException)
        {
            _onStatus(EngineState.Failed, $"Paket yakalama başlatılamadı: {ex.GetBaseException().Message}");
        }
    }

    // Değiştirildi: yakalama iş parçacığından hiçbir hata kaçmaz (kaçarsa SharpPcap'in iş parçacığı sessizce biterdi).
    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            OnPacketArrivalCore(e);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _callbackErrors);
            LastCallbackError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void OnPacketArrivalCore(PacketCapture e)
    {
        RawCapture raw = e.GetPacket();
        Packet packet;
        try
        {
            packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        }
        catch (Exception)
        {
            return;
        }

        TcpPacket? tcp = packet.Extract<TcpPacket>();
        IPPacket? ip = packet.Extract<IPPacket>();
        if (tcp is null || ip is null)
        {
            return;
        }

        // Değiştirildi: boş parçalar (yalnız ACK) ping tahmini için gerekmiyor, atılır.
        byte[] payload = tcp.PayloadData ?? Array.Empty<byte>();
        if (payload.Length == 0)
        {
            return;
        }

        int[] ports = _serverPorts;
        if (_discovering && ports.Length > 0 && !ports.Contains(tcp.SourcePort) && !ports.Contains(tcp.DestinationPort))
        {
            // Not a known game port: only ask whether it is the game's stream; the segment itself is dropped.
            if (payload.Length > 0 && _flowDetector.Observe(tcp.SourcePort, payload, DateTime.UtcNow))
            {
                AdoptPort(tcp.SourcePort);
            }

            return;
        }

        bool fromServer = ports.Length == 0 ? tcp.SourcePort < tcp.DestinationPort : ports.Contains(tcp.SourcePort);
        if (!fromServer)
        {
            return; // Değiştirildi: istemcinin parçaları çözülmüyor (Ingest de atıyordu), kuyruğa hiç girmez
        }

        string source = $"{ip.SourceAddress}:{tcp.SourcePort}";
        string destination = $"{ip.DestinationAddress}:{tcp.DestinationPort}";
        if (fromServer && payload.Length > 0 && ServerEndpoint is null)
        {
            ServerEndpoint = source;
            _onStatus(EngineState.Connected, "Oyun trafiği görüldü.");
        }

        if (payload.Length > 0)
        {
            Interlocked.Increment(ref _packets);
        }

        try
        {
            if (!_queue.TryAdd(new TcpSegment(raw.Timeval.Date.ToLocalTime(), source, destination, tcp.SequenceNumber, payload, fromServer, tcp.AcknowledgmentNumber, tcp.WindowSize, ip.TimeToLive)))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
        catch (InvalidOperationException)
        {
            // Queue already completed: the capture is being stopped.
        }
    }

    /// <summary>The game was found on another port: capture it from now on, and go back to the narrow filter.</summary>
    private void AdoptPort(int port)
    {
        lock (_devices)
        {
            if (!_discovering)
            {
                return;
            }

            _serverPorts = _serverPorts.Append(port).Distinct().ToArray();
            _discovering = false;
            ApplyFilter();
        }

        _onStatus(EngineState.WaitingGame, $"Oyun trafiği {port} portunda bulundu - dinleniyor.");
    }

    private void ApplyFilter()
    {
        string filter = Filter;
        foreach (ILiveDevice device in _devices)
        {
            try
            {
                device.Filter = filter;
            }
            catch (PcapException)
            {
                // the adapter went away: the others keep working
            }
        }
    }

    /// <summary>Every 30 s: no game traffic yet and no search running - widen the filter for a while; a search that ran out narrows it again.</summary>
    // Değiştirildi: zamanlayıcı iş parçacığından hata kaçmaz; kapatıldıktan sonra çalışmaz.
    private void DiscoveryTick(object? state)
    {
        try
        {
            if (!_disposed)
            {
                DiscoveryTickCore();
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _callbackErrors);
            LastCallbackError = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void DiscoveryTickCore()
    {
        if (Network is { Length: > 0 } || _serverPorts.Length == 0)
        {
            return;
        }

        lock (_devices)
        {
            if (_discovering)
            {
                if (Interlocked.Read(ref _packets) > 0 || (DateTime.UtcNow - _discoveryStarted).TotalSeconds >= DiscoveryForSeconds)
                {
                    _discovering = false;
                    ApplyFilter();
                    _discoveryTimer?.Change(TimeSpan.FromSeconds(DiscoveryPauseSeconds), TimeSpan.FromSeconds(DiscoveryAfterSeconds));
                }

                return;
            }

            if (Interlocked.Read(ref _packets) > 0)
            {
                return;
            }

            _flowDetector.Reset();
            _discoveryStarted = DateTime.UtcNow;
            _discovering = true;
            ApplyFilter();
        }

        _onStatus(EngineState.WaitingGame, "Bilinen portlarda oyun trafiği yok - iki dakika boyunca diğer TCP portlarında aranıyor.");
    }

    private Thread StartWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (TcpSegment segment in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (segment.Payload.Length > 0)
                    {
                        _onSegment(segment);
                    }
                }
                catch (Exception ex)
                {
                    // Never let a handler's exception end the worker.
                    Interlocked.Increment(ref _callbackErrors);
                    LastCallbackError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
        })
        {
            IsBackground = true,
            Name = "Aion2 segment worker",
        };
        thread.Start();
        return thread;
    }

    public void Dispose()
    {
        // Değiştirildi: önce zamanlayıcı, sonra kilit altında liste alınır; aygıtlar kilit dışında durdurulur
        // (StopCapture yakalama iş parçacığını bekler, o da AdoptPort'ta bu kilidi bekleyebilir).
        List<ILiveDevice> devices;
        lock (_devices)
        {
            _disposed = true;
            _discoveryTimer?.Dispose();
            _discoveryTimer = null;
            devices = _devices.ToList();
            _devices.Clear();
        }

        foreach (ILiveDevice device in devices)
        {
            try
            {
                device.StopCapture();
                device.Close();
            }
            catch (Exception)
            {
                // Already gone (adapter removed mid-session) - nothing left to release. (Değiştirildi: her hata yutulur)
            }
        }

        _queue.CompleteAdding();
    }
}
