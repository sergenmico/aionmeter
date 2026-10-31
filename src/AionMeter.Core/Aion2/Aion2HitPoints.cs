// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2HitPoints.cs. Değiştirildi.
// Değiştirildi: okuma geçmişi (varlık başına 20.000 örnek), sıfırlanma anları ve öldüren beceri kayıtları kaldırıldı;
// varlık başına yalnız son okuma, en yüksek okuma, sunucunun bildirdiği azami ve doğuş değerleri tutulur.
namespace AionDPS.Aion2;

/// <summary>One hit-point reading of an entity, as the server sent it.</summary>
public readonly record struct HpSample(DateTime At, long Hp);

/// <summary>
/// Every entity's latest hit points, from the server's HP frame (see Aion2FrameDecoder.DecodeHp), and what is known
/// of its maximum (highest reading, reported maximum, spawn frame).
/// </summary>
public sealed class Aion2HitPoints
{
    /// <summary>Değiştirildi (eklendi): bir bölgede bundan fazla varlık birikirse tablo boşaltılır (kaldırma kayıpları).</summary>
    private const int MaxEntities = 20_000;

    private readonly Dictionary<int, Track> _tracks = new();
    private readonly Dictionary<int, (long Current, long Max)> _spawns = new();
    private readonly object _gate = new();

    /// <summary>Değiştirildi (eklendi): motorun izlediği dövüş boss'u; sınıra gelince bu varlığın değerleri atılmaz.</summary>
    public int? Pinned { get; set; }

    public void Note(int entityId, DateTime at, long hp)
    {
        lock (_gate)
        {
            Track track = TrackOf(entityId);
            track.HighestSeen = Math.Max(track.HighestSeen, hp);
            track.Latest = new HpSample(at, hp);
        }
    }

    /// <summary>The highest value seen - the maximum once the entity has been seen at full health
    /// (at its first reading, or after a reset); a lower bound when the meter joined mid-fight.</summary>
    public long? HighestSeen(int entityId)
    {
        lock (_gate)
        {
            return _tracks.TryGetValue(entityId, out Track? track) ? track.HighestSeen : null;
        }
    }

    /// <summary>The hit points a monster's spawn frame carried (current and maximum; the maximum of a party-scaled boss is the value for one
    /// player). Null clears them: the entity id is reused, an earlier spawn's values belong to something else.</summary>
    public void NoteSpawn(int entityId, (long Current, long Max)? hp)
    {
        lock (_gate)
        {
            _tracks.Remove(entityId); // Değiştirildi: kimlik yeniden kullanıldı, eski okumalar başka bir varlığındı
            if (hp is { } value)
            {
                if (_spawns.Count >= MaxEntities)
                {
                    EvictAllButPinned(_spawns);
                }

                _spawns[entityId] = value;
            }
            else
            {
                _spawns.Remove(entityId);
            }
        }
    }

    /// <summary>The maximum the entity's spawn frame announced, or null when no spawn frame with hit points was seen for it.</summary>
    public long? SpawnMaxOf(int entityId)
    {
        lock (_gate)
        {
            return _spawns.TryGetValue(entityId, out var spawn) ? spawn.Max : null;
        }
    }

    /// <summary>A maximum the server reported (8-byte kind 7 of the HP frame; it follows buffs that raise it).</summary>
    public void NoteMax(int entityId, long max)
    {
        if (max <= 0)
        {
            return;
        }

        lock (_gate)
        {
            TrackOf(entityId).ReportedMax = max;
        }
    }

    /// <summary>The entity's maximum hit points: the one the server reported when it is not below the latest reading,
    /// else the highest reading seen (a lower bound when the meter joined mid-fight); null when nothing is known.</summary>
    public long? MaxOf(int entityId)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(entityId, out Track? track))
            {
                return null;
            }

            long latest = track.Latest?.Hp ?? 0;
            if (track.ReportedMax >= Math.Max(latest, 1))
            {
                return track.ReportedMax;
            }

            return track.HighestSeen > 0 ? track.HighestSeen : null;
        }
    }

    /// <summary>The entity's most recent reading, or null before the first one.</summary>
    public HpSample? Latest(int entityId)
    {
        lock (_gate)
        {
            return _tracks.TryGetValue(entityId, out Track? track) ? track.Latest : null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _spawns.Clear();
        }
    }

    /// <summary>Değiştirildi (eklendi): bölge değişiminde yalnız <paramref name="keep"/> içindeki varlıklar kalır.</summary>
    public void RetainOnly(IReadOnlySet<int> keep)
    {
        lock (_gate)
        {
            foreach (int id in _tracks.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                _tracks.Remove(id);
            }

            foreach (int id in _spawns.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                _spawns.Remove(id);
            }
        }
    }

    // Called under _gate.
    private Track TrackOf(int entityId)
    {
        if (!_tracks.TryGetValue(entityId, out Track? track))
        {
            if (_tracks.Count >= MaxEntities)
            {
                EvictAllButPinned(_tracks);
            }

            track = new Track();
            _tracks[entityId] = track;
        }

        return track;
    }

    // Called under _gate.
    private void EvictAllButPinned<T>(Dictionary<int, T> table)
    {
        int? pinned = Pinned;
        if (pinned is int id && table.TryGetValue(id, out T? kept))
        {
            table.Clear();
            table[id] = kept;
        }
        else
        {
            table.Clear();
        }
    }

    private sealed class Track
    {
        public long HighestSeen;
        public long ReportedMax;
        public HpSample? Latest;
    }
}
