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
    // Değiştirildi: düşük RAM için kuyruk 400.000 -> 20.000 parça, çekirdek tamponu 32 MB -> 16 MB (sürücü belleği; 4 MB'ta dünya boss'u
    // patlamalarında ps_drop görülebiliyordu).
    private const int MaxQueued = 20_000;
    private const int KernelBufferBytes = 16 * 1024 * 1024;

    /// <summary>Değiştirildi (eklendi): kart seçilemeyip hepsi dinlenirken kart başına tampon (16 MB x kart sayısı olmasın).</summary>
    private const int KernelBufferBytesEach = 4 * 1024 * 1024;

    /// <summary>Değiştirildi (eklendi): işçi en çok bu aralıkla <see cref="OnTick"/> çağırır (parça gelmese de; TCP boşluk süresi için).</summary>
    private const int TickMs = 100;

    /// <summary>Değiştirildi (eklendi): işçi iş parçacığında, parça gelmese de ~100 ms'de bir (yerel saat).</summary>
    public Action<DateTime>? OnTick { get; init; }
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

    /// <summary>Değiştirildi (eklendi): oyun bağlantısının pingi, yakalanan trafikten (hiçbir paket gönderilmez).</summary>
    internal PingOlcer Ping { get; } = new();

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
                    // Değiştirildi: Immediate + MaxResponsiveness - sürücü paketi tampon dolmasını ya da okuma süresini beklemeden verir (gecikme düşer).
                    var config = new DeviceConfiguration
                    {
                        ReadTimeout = 100,
                        Mode = DeviceModes.MaxResponsiveness,
                        Immediate = true,
                        KernelBufferSize = selected.Count > 1 ? KernelBufferBytesEach : KernelBufferBytes,
                    };
                    // Desteklenmeyen bir ayar (eski sürücü) aygıtı açılmaz bırakmasın: ayar atlanır, yakalama varsayılanla sürer.
                    config.ConfigurationFailed += (_, _) => { };
                    device.Open(config);
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

        int[] ports = _serverPorts;
        byte[] payload = tcp.PayloadData ?? Array.Empty<byte>();

        // Değiştirildi (eklendi): ping ölçümü bilinen oyun portunun iki yönünü de görür (sunucunun boş ACK'i dahil); yalnız sıra/onay
        // numarası ve iki zaman tutulur, parça kuyruğa girmez. Port bilinmiyorken (kalibrasyon, Network filtresi) ölçülmez.
        if (ports.Length > 0 && Network is not { Length: > 0 })
        {
            bool sunucudan = ports.Contains(tcp.SourcePort);
            if (sunucudan || ports.Contains(tcp.DestinationPort))
            {
                System.Net.IPAddress sunucu = sunucudan ? ip.SourceAddress : ip.DestinationAddress;
                int anahtar = HashCode.Combine(sunucu, sunucudan ? tcp.SourcePort : tcp.DestinationPort, sunucudan ? tcp.DestinationPort : tcp.SourcePort);
                long pcapTick = (long)raw.Timeval.Seconds * TimeSpan.TicksPerSecond + (long)raw.Timeval.MicroSeconds * 10;
                Ping.Gozle(anahtar, sunucudan, tcp.SequenceNumber, payload.Length, tcp.Acknowledgment, tcp.AcknowledgmentNumber, pcapTick, PingOlcer.VarisSaati());
            }
        }

        // Değiştirildi: boş parçalar (yalnız ACK) çözücüye gitmez, atılır (ping yukarıda ölçüldü).
        if (payload.Length == 0)
        {
            return;
        }

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

    /// <summary>Değiştirildi (eklendi): açık aygıtların pcap sayaçlarının toplamı (alınan, ps_drop = çekirdek tamponu taşması, ps_ifdrop).</summary>
    public (long Received, long Dropped, long InterfaceDropped) KernelStatistics()
    {
        long received = 0, dropped = 0, ifDropped = 0;
        lock (_devices)
        {
            foreach (ILiveDevice device in _devices)
            {
                try
                {
                    if (device.Statistics is { } stats)
                    {
                        received += stats.ReceivedPackets;
                        dropped += stats.DroppedPackets;
                        ifDropped += stats.InterfaceDroppedPackets;
                    }
                }
                catch (Exception)
                {
                    // aygıt gitti ya da sayaç desteklenmiyor
                }
            }
        }

        return (received, dropped, ifDropped);
    }

    private Thread StartWorker()
    {
        var thread = new Thread(() =>
        {
            // Değiştirildi: GetConsumingEnumerable yerine zaman aşımlı TryTake; boşta da OnTick çağrılır.
            long lastTick = Environment.TickCount64;
            while (!_queue.IsCompleted)
            {
                try
                {
                    if (_queue.TryTake(out TcpSegment? segment, TickMs) && segment.Payload.Length > 0)
                    {
                        _onSegment(segment);
                    }

                    long nowMs = Environment.TickCount64;
                    if (OnTick is { } tick && nowMs - lastTick >= TickMs)
                    {
                        lastTick = nowMs;
                        tick(DateTime.Now);
                    }
                }
                catch (InvalidOperationException) when (_queue.IsCompleted)
                {
                    break;
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
