using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>How the player moves, as an orc hears it (#10154).</summary>
internal enum PlayerGait { Still, Crouched, Walking, Running }

/// <summary>
/// What an orc hears by (#10154; docs/lab.md, "Perception"): the settings' <c>ai</c> block's sound presence distances,
/// how far the player's steps carry by gait (<c>SoundPresenceDist_Inaudible</c> 100 for a stealth walk,
/// <c>_Average</c> 300 for a walk, <c>_Big</c> 500 "mostly used for run and sprint"), and <c>delayhear</c> (1 s).
/// </summary>
internal sealed record OrcPerceptionData(float Crouched, float Walking, float Running, float HearDelay)
{
    internal static OrcPerceptionData Read(DonorContent donor)
    {
        var settings = donor.Read("scripts/mm_game_settings.txt") is byte[] bytes ? KeyValues.ParseAll(bytes).FirstOrDefault()?.Block("ai") : null;
        float S(string key, float shipped) => settings?.Number(key) ?? shipped;
        return new OrcPerceptionData(S("SoundPresenceDist_Inaudible", 100), S("SoundPresenceDist_Average", 300), S("SoundPresenceDist_Big", 500),
            S("delayhear", 1) + ThinkLatency);
    }

    /// <summary>
    /// What the donor's orcs took beyond <c>delayhear</c> to react, seconds: from the player crossing 500 units at a run
    /// or a sprint to the alert level's first rise, 1.4 s in both recorded approaches (the AI thinks a few times a second).
    /// </summary>
    internal const float ThinkLatency = .4f;

    /// <summary>How far the player's steps carry at a gait, Source units.</summary>
    internal float Reach(PlayerGait gait) => gait switch
    {
        PlayerGait.Crouched => Crouched,
        PlayerGait.Walking => Walking,
        PlayerGait.Running => Running,
        _ => 0,
    };
}

/// <summary>
/// An orc's awareness of the player (#10154): its alert level as server.dll keeps it (NPC +0x36EC; Wire v18 streams it),
/// 0 unaware, 4 fighting. The recorded orcs in l01_a noticed the player only by hearing: one standing still in plain
/// view 300 to 1000 units in front went unseen for 8 s, as did one walking up crouched to 558 (l01_a is one of the
/// maps the settings' <c>nostealth</c> block names); running or sprinting within 500 units they heard, 1.4 s after it
/// came in reach, and a blow makes it fight at once. An unaware orc takes the player's not-alerted ×3
/// (<c>mul_damages_Npc_Not_alerted</c>), and doesn't defend.
/// </summary>
internal sealed class OrcPerception(OrcPerceptionData data)
{
    /// <summary>The alert level it fights at, as the recorded orcs did from their first alarm on.</summary>
    internal const int Fighting = 4;

    float heard;

    /// <summary>
    /// A gait by the player's ground speed (metres a second) and keys: still under 0.3, crouched, running from 2.5 or
    /// sprinting (the donor's run is 180 units a second, 3.4 m/s), else walking.
    /// </summary>
    internal static PlayerGait GaitOf(float speed, bool crouched, bool sprinting) =>
        speed < .3f ? PlayerGait.Still : crouched ? PlayerGait.Crouched : sprinting || speed >= 2.5f ? PlayerGait.Running : PlayerGait.Walking;

    internal OrcPerceptionData Data => data;
    internal int AlertLevel { get; private set; } = Fighting;
    internal bool Aware => AlertLevel > 0;

    /// <summary>A new orc, aware or not.</summary>
    internal void Reset(bool aware)
    {
        AlertLevel = aware ? Fighting : 0;
        heard = 0;
    }

    /// <summary>
    /// Time passes with the player <paramref name="distance"/> units off at <paramref name="gait"/>. Returns true the step
    /// it becomes aware: the player's steps were in reach for the hearing delay.
    /// </summary>
    internal bool Step(float seconds, float distance, PlayerGait gait)
    {
        if (Aware) return false;
        heard = distance <= data.Reach(gait) ? heard + seconds : 0;
        if (heard < data.HearDelay) return false;
        AlertLevel = Fighting;
        return true;
    }

    /// <summary>A blow: it fights at once (every recorded sneak attack took the orc from 0 to 4 on its tick).</summary>
    internal bool Struck()
    {
        bool was = Aware;
        AlertLevel = Fighting;
        return !was;
    }
}
