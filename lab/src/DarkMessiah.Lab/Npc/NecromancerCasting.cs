using System.Numerics;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// How the necromancer casts (#10357; docs/lab.md, "The necromancer"): its <c>.qct</c>'s <c>spelldatas</c> <c>fire_1</c>, the
/// flame arrow (<c>effect</c> 6, <c>range_min</c> 200, <c>range_max</c> 1000), and the rest measured over
/// <c>l01_a-v19-reflect-probe-2</c> and <c>l05_a</c>'s <c>necromancer-l05</c> and <c>-l05b</c> (l01_a's NPCs don't see the player, so
/// its fight there broke off into a search once he moved):
/// <list type="bullet">
/// <item>between <see cref="RangeMin"/> and <see cref="RangeMax"/> it stands and casts <c>spell_flamearrow</c>, the flame
/// arrow leaving its right hand at the clip's event 2165 (cycle 0.6, 1.02 s in), and casts again <see cref="Every"/>
/// (1.68 s) on: the clip played to <see cref="SpellEnd"/>, then <see cref="Between"/> of <c>combatidle</c> (every
/// standing cast, 455 to 844 units off);</item>
/// <item>nearer than <see cref="RangeMin"/> it walks back from the player, facing it, at <see cref="WalkUnits"/> (35 units/s:
/// from 147 to 318–327 off in 4.8 to 4.9 s, three times) to <see cref="BackOffTo"/> (318: it stood again 291 to 327 off,
/// a median 318), and past <see cref="RangeMax"/> walks in, casting on the move (<c>layer_spell_flamearrow</c>
/// over its walk, let go at the same event) every <see cref="EveryMoving"/> (1.8 s: backing off, 127.4, 129.2, 131.0);</item>
/// <item>within its <c>Yellow_zone</c> (80) it fights as any NPC, with its hook (<c>attack_r</c>: 7, its strength 4 and the
/// hook's <c>WDmg</c> 3), a blow, then backs off again (the one recorded blow, 63 units off, then it walked off);</item>
/// <item>its flame arrows fly straight at the player's eye at <see cref="SpeedUnits"/> (540 units/s: the median cruise of the 53
/// recorded flights cast from 400 units or more, from just after the hand let go to the last sample; shorter flights, where
/// the release's fast first samples weigh more, ran a median 571 to 629; the samples themselves run at 499 or 718, the Wire's
/// frames not landing on the same tick phase), no lead, off to one side
/// by a turn that narrows with each cast (<c>ThrowPrecisionNbShootToIn</c> 10: "the 1st shoot has very bad accuracy"):
/// <see cref="FirstError"/> (4.6°) on the first, then <see cref="SecondError"/> (2.2°) narrowing to <see cref="LastError"/>
/// (1°) by the tenth (at 455 units its line passed the eye 35 units aside on the first, 14 to 25 on the next five, 7 and 8 on
/// the eighth and ninth; it struck within 18 and missed from 25);</item>
/// </list>
/// </summary>
internal sealed record CastingData(float RangeMin, float RangeMax, float Effect, float Every = 1.68f, float SpellEnd = .913f, float Between = .12f,
    float BackOffTo = 318, float WalkUnits = 35, float SpeedUnits = 540, float FirstError = 4.6f, float SecondError = 2.2f, float LastError = 1, int ShotsToAim = 10, float EveryMoving = 1.8f)
{
    internal const string Spell = "fire_1", Clip = "spell_flamearrow", Layer = "layer_spell_flamearrow";
    /// <summary>The spell clip's event that lets the missile go.</summary>
    internal const int Release = 2165;

    /// <summary>The necromancer's casting: its <c>fire_1</c> from its <c>.qct</c>, the rest measured.</summary>
    internal static CastingData For(DonorContent donor, string model)
    {
        float? Fire(string key) => donor.Nested(model, ["spelldatas", Spell], key);
        return new(Fire("range_min") ?? 200, Fire("range_max") ?? 1000, Fire("effect") ?? 6);
    }

    /// <summary>The aim's turn off the line to the eye for its <paramref name="cast"/>th flame arrow (1 the first), degrees.</summary>
    internal float Error(int cast) => cast <= 1 ? FirstError : SecondError + (LastError - SecondError) * Math.Clamp((cast - 2) / (float)(ShotsToAim - 2), 0, 1);
}

/// <summary>A flame arrow the necromancer let go this step: from its hand, turned off its aim by <paramref name="ErrorDegrees"/>.</summary>
internal readonly record struct NpcCast(Vector3 From, float ErrorDegrees);

internal enum CastingPhase { Stand, BackOff, Approach }

internal sealed partial class Orc
{
    CastingData? casting;
    CastingPhase castingPhase;
    float castWait;
    int casts, blowsSeen;
    bool afterBlow;

    /// <summary>The caster's numbers, or null for an NPC that doesn't cast (#10357).</summary>
    internal CastingData? Casting => casting;
    /// <summary>Where its casting stands: standing to cast, backing off, or walking in.</summary>
    internal CastingPhase CastingPhase => castingPhase;
    /// <summary>The flame arrows it let go this step.</summary>
    internal List<NpcCast> Cast { get; } = [];
    /// <summary>Its flame arrows this life.</summary>
    internal int Casts => casts;

    void ResetCasting()
    {
        casting = Kind.Caster ? CastingData.For(donor, Kind.Model) : null;
        castingPhase = CastingPhase.Stand;
        castWait = 0;
        casts = 0;
        (blowsSeen, afterBlow) = (AttacksMade, false);
    }

    /// <summary>
    /// The caster's step while the player is beyond its melee (true when it took the step): standing and casting in range,
    /// backing off when too near, walking in when too far, its flame arrows let go at the spell's event. Its reactions
    /// (a hit, a stun, a fall) play as any NPC's, and it goes on after; within its yellow zone it fights as one.
    /// </summary>
    bool StepCasting(float seconds, float units, Vector3 toPlayer, bool clearShot)
    {
        if (casting is not { } c || !perception.Aware || !Alive || Following) return false;
        bool free = State is OrcState.Ready or OrcState.Ranged or OrcState.Approach;
        // Its melee: within the yellow zone it fights with the hook, one blow, played out; then it backs off again (the donor's
        // struck once and walked off).
        if (AttacksMade > blowsSeen) (blowsSeen, afterBlow) = (AttacksMade, true);
        if (units <= mind.Data.Yellow && !afterBlow || !free)
        {
            if (Layer == Resolve(CastingData.Layer)) Layer = null;
            // Come within reach while it cast or walked (R10357-1), it takes up the fight as any NPC, ready to strike.
            if (free && State != OrcState.Ready) Play(OrcState.Ready, "combatidle");
            return false;
        }
        var toward = toPlayer.LengthSquared() > 1e-6f ? Vector3.Normalize(toPlayer) : Facing;
        float metres = SourceSpace.MetresPerUnit * seconds;
        castingPhase = afterBlow || units < c.RangeMin || castingPhase == CastingPhase.BackOff && units < c.BackOffTo ? CastingPhase.BackOff
            : units > c.RangeMax ? CastingPhase.Approach : CastingPhase.Stand;
        if (afterBlow && units >= c.BackOffTo) afterBlow = false;
        castWait -= seconds;
        bool canCast = !Hold && clearShot;
        switch (castingPhase)
        {
            case CastingPhase.Stand:
                Layer = null;
                if (Playing(CastingData.Clip))
                {
                    var clip = body.Model.Clip(Clip);
                    if (clip.At(CastingData.Release).FirstOrDefault(float.NaN) is var at && !float.IsNaN(at) && Cycle - seconds / MathF.Max(clip.Seconds, 1e-3f) < at && Cycle >= at)
                        Loose(c);
                    if (Cycle >= c.SpellEnd) { Play(OrcState.Ranged, "combatidle"); castWait = c.Between; }
                }
                else if (castWait <= 0 && canCast) Play(OrcState.Ranged, CastingData.Clip);
                else if (!Playing("combatidle")) Play(OrcState.Ranged, "combatidle");
                return true;
            case CastingPhase.BackOff:
                Walk(-toward, "Combatwalk@0");
                break;
            case CastingPhase.Approach:
                Walk(toward, "CombatWalk");
                break;
        }
        // On the move: the cast plays over its walk, the arrow let go at the same event, the next a cast's time on.
        if (Layer == Resolve(CastingData.Layer))
        {
            float before = LayerCycle;
            var layer = body.Model.Clip(Resolve(CastingData.Layer));
            if (layer.At(CastingData.Release).FirstOrDefault(float.NaN) is var at && !float.IsNaN(at) && before - seconds / MathF.Max(layer.Seconds, 1e-3f) < at && before >= at) Loose(c);
            if (LayerCycle >= 1) { Layer = null; castWait = c.EveryMoving - layer.Seconds; }
        }
        else if (castWait <= 0 && canCast) { Layer = Resolve(CastingData.Layer); LayerCycle = 0; }
        return true;

        void Walk(Vector3 way, string clip)
        {
            if (!Playing(clip) || State != OrcState.Ranged) { var layer = (Layer, LayerCycle); Play(OrcState.Ranged, clip); (Layer, LayerCycle) = layer; }
            Feet += way * c.WalkUnits * metres;
        }
    }

    /// <summary>A flame arrow let go from its right hand, off its aim by its cast's error, to one side or the other.</summary>
    void Loose(CastingData c)
    {
        casts++;
        float side = random.Next(2) == 0 ? -1 : 1;
        Cast.Add(new NpcCast(Hand(), side * c.Error(casts)));
    }

    /// <summary>Its right hand now (the <c>RightHand</c> attachment), where its flame arrows leave.</summary>
    internal Vector3 Hand()
    {
        Present();
        var pose = Rig.Pose(engine, body, Placement);
        return Rig.Attached(body.Model, pose, "RightHand") is Transform at ? at.Translation : Feet + Vector3.UnitY * 45 * SourceSpace.MetresPerUnit;
    }
}
