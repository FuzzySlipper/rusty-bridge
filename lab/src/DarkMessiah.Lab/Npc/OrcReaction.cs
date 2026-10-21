using DarkMessiah.Combat;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The reaction a blow plays on the orc (#10153): a clip of its model, as an overlay over what it does (the small
/// hits, <c>Hit_head_*_small</c>, <c>Hit_Body_*_small</c>) or in its place (the medium and long hits and every push).
/// </summary>
internal readonly record struct OrcReaction(string Clip, bool Overlay, bool Push);

/// <summary>What a player's blow brings to the reaction rule.</summary>
/// <param name="Event">The move's <c>.qct</c> event; null for a kick.</param>
/// <param name="AttackerFacing">The attacker's facing in the orc's frame, degrees, positive to its left (0: facing the same way).</param>
internal readonly record struct ReactionBlow(AttackEvent? Event, bool Kick, float Damage, bool Critical, int? HitGroup, float AttackerFacing);

/// <summary>
/// Which reaction a player's blow plays on the orc, fitted to every recorded blow (#10153; docs/lab.md, "Hit
/// reactions"; <c>ReactionCalibration</c>):
/// <list type="bullet">
/// <item><b>the side</b> is where the impact comes from: the event's <c>impactdirX</c>/<c>Y</c> turned by the
/// attacker's facing into the orc's frame, reversed, in quarters (front, left, back, right);</item>
/// <item><b>a kick</b> pushes, <c>Push_&lt;side&gt;_medium</c>;</item>
/// <item><b>a push</b>: <c>alwayspush</c> with 10 damage or more, or <c>push</c> on a critical (the sword's power
/// attack, 11 of 11 criticals), <c>Push_&lt;side&gt;_long</c> (medium under 10); a light one (the shield's bash,
/// under 0.5) <c>Push_&lt;Head|Body&gt;_&lt;side&gt;_small</c>;</item>
/// <item><b>a fast attack</b> (<c>impactcoeff</c> 0.5) plays the small hit as an overlay, the head's on a head blow
/// or a swing to the left (<c>impactdirY</c> &gt; 0), else the body's, and none within a second of the last;</item>
/// <item><b>anything else</b>, and every blow on a stunned orc: <c>Hit_&lt;side&gt;_long</c> from 10 damage,
/// else <c>_medium</c>.</item>
/// </list>
/// </summary>
internal static class OrcReactions
{
    /// <summary>Damage from which a hit or push is long rather than medium: the recorded ones were medium to 9, long from 12.</summary>
    internal const float LongFrom = 10;

    /// <summary>A small hit plays no sooner than this after the last, seconds: 24 of 26 recorded 30–60 ticks after one played none, from 70 they played.</summary>
    internal const float SmallAgain = 1;

    /// <summary>The side an impact comes from, in the orc's frame.</summary>
    internal static string Side(float attackerFacing, float impactX, float impactY)
    {
        float travel = attackerFacing + MathF.Atan2(impactY, impactX) * 180 / MathF.PI;
        float from = (travel + 180 + 540) % 360 - 180;
        return from is > -45 and <= 45 ? "front" : from is > 45 and <= 135 ? "left" : from is < -45 and >= -135 ? "right" : "back";
    }

    /// <param name="stunned">The orc is stunned: it plays the full hit, which ends the stun.</param>
    /// <param name="sinceSmall">Seconds since its last small hit began.</param>
    internal static OrcReaction Pick(ReactionBlow blow, bool stunned, double sinceSmall)
    {
        var e = blow.Event;
        float x = blow.Kick ? 1 : e?.ImpactX ?? 0, y = blow.Kick ? 0 : e?.ImpactY ?? 0;
        string side = Side(blow.AttackerFacing, x, y);
        string size = blow.Damage >= LongFrom ? "long" : "medium";
        bool head = blow.HitGroup == 1 || y > 0;
        if (blow.Kick) return new($"Push_{side}_medium", false, true);
        float always = e?.AlwaysPush ?? 0;
        if (always > 0 && blow.Damage < .5f) return new($"Push_{(head ? "Head" : "Body")}_{side}_small", false, true);
        if (always > 0 && blow.Damage >= LongFrom || (e?.Push ?? 0) > 0 && blow.Critical) return new($"Push_{side}_{size}", false, true);
        if (!stunned && always <= 0 && (e?.ImpactCoeff ?? 1) <= .5f)
            return sinceSmall < SmallAgain ? default : new($"Hit_{(head ? "head" : "Body")}_{side}_small", true, false);
        return new($"Hit_{side}_{size}", false, false);
    }
}
