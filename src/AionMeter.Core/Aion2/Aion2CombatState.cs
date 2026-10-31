// Kaynak: SkeeveAN/Aion-DPS-Meter (MIT), Client/Aion2/Aion2CombatState.cs. Değiştirildi.
// Değiştirildi: yalnız süren büyü (windup) listesi kaldı; boss seçimi "en son büyü yapan boss" için bunu okur.
// Öğrenilen beceriler, konum izleri, buff'lar, hedef/ölüm/sersemletme kayıtları ve dosyaya kaydetme kaldırıldı.
namespace AionDPS.Aion2;

/// <summary>A monster's (or a player's) skill that has begun: the game's "windup" frame. It names the caster, the
/// skill, whom it is aimed at and how long the cast takes; the damage follows a moment after the cast has ended.</summary>
public readonly record struct CastNotice(
    int Actor, int SkillId, int Target, DateTime Start, int DurationMs, double Facing, float X, float Y, float Z, bool HasPosition)
{
    public DateTime End => Start.AddMilliseconds(DurationMs);
}

public sealed record CastView(CastNotice Notice);

public sealed class Aion2CombatState
{
    /// <summary>A cast stays known this long after it ended (its damage lands, the area is learnt).</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    private const int MaxActive = 200;

    private readonly object _gate = new();
    private readonly List<ActiveCast> _active = new();

    private sealed class ActiveCast
    {
        public ActiveCast(CastNotice notice) => Notice = notice;

        public CastNotice Notice { get; }
        public bool Stopped;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _active.Clear();
        }
    }

    /// <summary>Değiştirildi (eklendi): bölge değişiminde yalnız <paramref name="keep"/> içindeki varlıkların büyüleri kalır.</summary>
    public void RetainOnly(IReadOnlySet<int> keep)
    {
        lock (_gate)
        {
            _active.RemoveAll(a => !keep.Contains(a.Notice.Actor));
        }
    }

    public void NoteWindup(CastNotice cast)
    {
        lock (_gate)
        {
            Prune(cast.Start);
            _active.RemoveAll(a => a.Notice.Actor == cast.Actor && a.Notice.SkillId == cast.SkillId && a.Notice.Start == cast.Start);
            if (_active.Count >= MaxActive)
            {
                _active.RemoveAt(0);
            }

            _active.Add(new ActiveCast(cast));
        }
    }

    /// <summary>The entity died or left: its casts are over ("stopped"), they do not run on to the end.</summary>
    public void NoteGone(int entity, DateTime at)
    {
        lock (_gate)
        {
            foreach (ActiveCast cast in _active.Where(c => c.Notice.Actor == entity && at < c.Notice.End))
            {
                cast.Stopped = true;
            }
        }
    }

    /// <summary>The casts that are running at <paramref name="now"/> (not yet ended, or ended for less than <paramref name="grace"/>), soonest to end first.</summary>
    public IReadOnlyList<CastView> Casts(DateTime now, TimeSpan grace)
    {
        lock (_gate)
        {
            Prune(now);
            var list = new List<CastView>();
            foreach (ActiveCast cast in _active)
            {
                if (cast.Stopped || now < cast.Notice.Start || now > cast.Notice.End + grace)
                {
                    continue;
                }

                list.Add(new CastView(cast.Notice));
            }

            return list.OrderBy(c => c.Notice.End).ToList();
        }
    }

    /// <summary>Drops the casts that are long over.</summary>
    private void Prune(DateTime now)
    {
        _active.RemoveAll(c => now > c.Notice.End + Linger + TimeSpan.FromSeconds(30));
    }

    public void ForgetEntity(int entity)
    {
        lock (_gate)
        {
            _active.RemoveAll(c => c.Notice.Actor == entity);
        }
    }
}
