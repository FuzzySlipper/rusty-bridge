using System.Numerics;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>How the player moves, as an orc hears it (#10154).</summary>
internal enum PlayerGait { Still, Crouched, Walking, Running }

/// <summary>How an orc became aware of the player (#10154): calls go out only from one that saw the player.</summary>
internal enum AwareBy { None, Heard, Seen, Called, Struck }

/// <summary>
/// What an orc senses by (#10154; docs/lab.md, "Perception"):
/// <list type="bullet">
/// <item>hearing: the settings' <c>ai</c> block's sound presence distances, how far the player's steps carry by gait
/// (<c>SoundPresenceDist_Inaudible</c> 100 for a stealth walk, <c>_Average</c> 300 for a walk, <c>_Big</c> 500 "mostly
/// used for run and sprint"), and <c>delayhear</c> (1 s);</item>
/// <item>sight (R10154-2): a cone <paramref name="SightHalfAngle"/> degrees either side of its facing, out to
/// <paramref name="SightRange"/> Source units, after <paramref name="SeeDelay"/> (<c>delaysee</c> 0, and the AI's think), except in a map the settings'
/// <c>nostealth</c> block lists (<paramref name="NoStealth"/>), where the recorded orcs saw nothing;</item>
/// <item>its squad (R10154-1): an orc that sees the player calls the squad-mates within
/// <paramref name="SquadDistance"/> (<c>SquadAwarnessDist</c>, 500), who fight <paramref name="CallDelay"/> seconds later.</item>
/// </list>
/// </summary>
internal sealed record OrcPerceptionData(float Crouched, float Walking, float Running, float HearDelay, float SightRange, float SightHalfAngle,
    IReadOnlySet<string> NoStealth, float SquadDistance, float CallDelay, float SeeDelay)
{
    internal static OrcPerceptionData Read(DonorContent donor, string model)
    {
        var root = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault() : null;
        var settings = root?.Block("ai");
        float S(string key, float shipped) => settings?.Number(key) ?? shipped;
        float Q(string key, float shipped) => donor.Number(model, "entity_data", key) ?? shipped;
        // The effective sight, fitted: in l05_a an idle orc saw a player standing still 525 units ahead and not 550, 20°
        // off its facing at 300 and not 30° (l05_a-v18-sight-l05*). The .qct's maxsightdistancequiet (1500) and
        // fieldofviewquiet (90) times the settings' NPCRangeOfViewRatioWhenStealth (0.6) fit both: 1500 × 0.6 × 0.6 =
        // 540 units, 90 × 0.6 = 54° (27° either side). How server.dll combines them (and the player's light) isn't read.
        float ratio = S("NPCRangeOfViewRatioWhenStealth", .6f);
        var noStealth = root?.Block("nostealth")?.Values.Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["L01_a", "L03_a"], StringComparer.OrdinalIgnoreCase);
        return new OrcPerceptionData(S("SoundPresenceDist_Inaudible", 100), S("SoundPresenceDist_Average", 300), S("SoundPresenceDist_Big", 500),
            S("delayhear", 1) + ThinkLatency, Q("maxsightdistancequiet", 1500) * ratio * ratio, Q("fieldofviewquiet", 90) * ratio / 2,
            noStealth, S("SquadAwarnessDist", 500), CalledAfter, S("delaysee", 0) + ThinkLatency);
    }

    /// <summary>
    /// What the donor's orcs took beyond <c>delayhear</c> to react, seconds: from the player crossing 500 units at a run
    /// or a sprint to the alert level's first rise, 1.4 s in both recorded approaches (the AI thinks a few times a second).
    /// Sight too: an orc in l05_a noticed a player put 289 units in front of it 0.57 s later (<c>delaysee</c> 0).
    /// </summary>
    internal const float ThinkLatency = .4f;

    /// <summary>
    /// From the caller's alert to its squad-mate fighting, seconds: 2.4 in l05_a-v18-call-l05 (searching from 1.6), 300
    /// units off. One case: a mate 600 off stayed idle.
    /// </summary>
    internal const float CalledAfter = 2.4f;

    /// <summary>How far the player's steps carry at a gait, Source units.</summary>
    internal float Reach(PlayerGait gait) => gait switch
    {
        PlayerGait.Crouched => Crouched,
        PlayerGait.Walking => Walking,
        PlayerGait.Running => Running,
        _ => 0,
    };

    /// <summary>Whether orcs see in <paramref name="map"/>: not in one the settings' <c>nostealth</c> block lists.</summary>
    internal bool Sees(string map) => !NoStealth.Contains(map);

    /// <summary>
    /// The player in sight: within <see cref="SightRange"/> Source units and <see cref="SightHalfAngle"/> degrees of
    /// <paramref name="facing"/> (both flat, lab space).
    /// </summary>
    internal bool InSight(Vector3 facing, Vector3 toPlayer, float units)
    {
        if (units > SightRange) return false;
        var flat = toPlayer with { Y = 0 };
        if (flat.LengthSquared() < 1e-8f) return true;
        float cos = Vector3.Dot(Vector3.Normalize(facing with { Y = 0 }), Vector3.Normalize(flat));
        return cos >= MathF.Cos(SightHalfAngle * MathF.PI / 180);
    }
}

/// <summary>
/// An orc's awareness of the player (#10154): its alert level as server.dll keeps it (NPC +0x36EC; Wire v18 streams it),
/// 0 unaware, 4 fighting. In l01_a (a <c>nostealth</c> map) the recorded orcs noticed the player only by hearing: one
/// standing still in plain view 300 to 1000 units in front went unseen for 8 s, as did one walking up crouched to 558;
/// running or sprinting within 500 units they heard, 1.4 s after it came in reach. In l05_a they saw (R10154-2). A blow
/// makes it fight at once. An unaware orc takes the player's not-alerted ×3 (<c>mul_damages_Npc_Not_alerted</c>), and
/// doesn't defend.
/// </summary>
internal sealed class OrcPerception(OrcPerceptionData data)
{
    /// <summary>The alert level it fights at, as the recorded orcs did from their first alarm on.</summary>
    internal const int Fighting = 4;

    float heard, seen;
    float called = float.NaN;

    /// <summary>
    /// A gait by the player's ground speed (metres a second) and keys: still under 0.3, crouched, running from 2.5 or
    /// sprinting (the donor's run is 180 units a second, 3.4 m/s), else walking.
    /// </summary>
    internal static PlayerGait GaitOf(float speed, bool crouched, bool sprinting) =>
        speed < .3f ? PlayerGait.Still : crouched ? PlayerGait.Crouched : sprinting || speed >= 2.5f ? PlayerGait.Running : PlayerGait.Walking;

    internal OrcPerceptionData Data => data;
    internal int AlertLevel { get; private set; } = Fighting;
    internal bool Aware => AlertLevel > 0;
    /// <summary>How it became aware; <see cref="AwareBy.None"/> while unaware, and for one spawned aware.</summary>
    internal AwareBy By { get; private set; }
    /// <summary>A squad-mate's call is on its way.</summary>
    internal bool Calling => !float.IsNaN(called);

    /// <summary>A new orc, aware or not.</summary>
    internal void Reset(bool aware)
    {
        AlertLevel = aware ? Fighting : 0;
        By = AwareBy.None;
        heard = seen = 0;
        called = float.NaN;
    }

    /// <summary>
    /// Time passes with the player <paramref name="distance"/> units off at <paramref name="gait"/>, in its sight or not.
    /// Returns how it became aware this step, <see cref="AwareBy.None"/> if it didn't: in sight for the sight delay, its
    /// steps in reach for the hearing delay, or a squad-mate's call arriving.
    /// </summary>
    internal AwareBy Step(float seconds, float distance, PlayerGait gait, bool inSight = false)
    {
        if (Aware) return AwareBy.None;
        heard = distance <= data.Reach(gait) ? heard + seconds : 0;
        seen = inSight ? seen + seconds : 0;
        if (Calling) called -= seconds;
        var by = inSight && seen >= data.SeeDelay ? AwareBy.Seen : heard >= data.HearDelay ? AwareBy.Heard : Calling && called <= 0 ? AwareBy.Called : AwareBy.None;
        if (by != AwareBy.None) Alarm(by);
        return by;
    }

    /// <summary>A squad-mate saw the player: it fights <see cref="OrcPerceptionData.CallDelay"/> seconds later, unless it notices first.</summary>
    internal void Call()
    {
        if (!Aware && !Calling) called = data.CallDelay;
    }

    /// <summary>A blow: it fights at once (every recorded sneak attack took the orc from 0 to 4 on its tick).</summary>
    internal bool Struck()
    {
        bool was = Aware;
        if (!was) Alarm(AwareBy.Struck);
        return !was;
    }

    void Alarm(AwareBy by)
    {
        AlertLevel = Fighting;
        By = by;
        called = float.NaN;
    }
}
