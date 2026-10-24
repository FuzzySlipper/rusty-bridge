using System.Numerics;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Ranged;

/// <summary>
/// A bow's numbers (#10168; docs/lab.md, "The bow"). From its model's <c>.qct</c>: <c>WDmg</c>, which the combat model
/// reads by the same path, and its <c>ammo</c>. Its arrow's damage is a player blow of the arms' <c>layer_bowattack_shoot</c>
/// (<c>HitPow</c> 1.5): (strength + <c>WDmg</c>) × 1.5 × the hit group, ×3 on an unaware orc, as every recorded arrow
/// asked (12 into the world with the classic bow at strength 6, 15, 16.5 and 19.5 with the long, sniper and explosive
/// bows, 10.5 with the rope bow; 9 on an unaware orc at strength 0, 36 at 6). The draw doesn't change it: arrows
/// released 2 to 6 s into the draw asked the same.
/// server.dll's ConVars and the recordings (<c>l01_a-v18-bow</c>, <c>-bow-basic</c>, <c>-bow-more</c>, <c>-bow-sway</c>):
/// <list type="bullet">
/// <item><see cref="SpeedUnits"/>: <c>arrow_speed</c>, 2100 units/s; every recorded arrow flew at it.</item>
/// <item><see cref="GravityUnits"/>: 600 units/s² (586 to 606 fitted on 15 flights).</item>
/// <item><see cref="RaiseDegrees"/>: an arrow leaves 2.8° above the view (2.7° to 3.1° at views 4.6° down to 42° up;
/// <c>mm_arrow_pitch_compensation</c> 1), along the view, not the swaying camera. From 45° up
/// (<c>mm_arrow_pitch_compensation_start</c> −45) less: 2.04°, 1.96°, 1.81° and 1.51° at 45°, 50°, 60° and 80° up, 1.34° +
/// the cosine of the view's pitch (<see cref="Raise"/>).</item>
/// <item><see cref="DrawDelay"/>: the draw starts 0.12 s after attack is pressed at rest, every press.</item>
/// <item><see cref="LeaveDelay"/>: the arrow leaves the hand 0.03 s into the shoot clip.</item>
/// <item><see cref="Settle"/>: each phase runs a server tick (15 ms) past its clip: the recorded shoot, fast idle, fast
/// out, draw and fast reload came 0.24, 0.54, 0.51, 2.01 and 1.02 s apart for clips of 0.23, 0.53, 0.50, 2.00 and 1.00
/// (frames 2 ticks apart).</item>
/// <item><see cref="RestDelay"/>: a press held while the bow lowered draws 0.03 s after it's at rest.</item>
/// <item><see cref="BounceBelowDegrees"/> and <see cref="Restitution"/>: an arrow meeting the world at a grazing angle
/// (the recorded ones met the floor at 6° to 12°) bounces once, its speed ×0.56 (0.53 to 0.59 over 40 bounces, less
/// the steeper), and its next impact stops it; steeper, it stops (R10168-1).</item>
/// </list>
/// </summary>
internal sealed record BowRules(string Model, int Ammo, float SpeedUnits = 2100, float GravityUnits = 600, float RaiseDegrees = 2.8f,
    float DrawDelay = .12f, float LeaveDelay = .03f, float RestDelay = .03f, float Settle = .015f,
    float ExplosiveDamage = 0, float ExplosiveRadius = 0, float BounceBelowDegrees = 15, float Restitution = .56f)
{
    /// <summary>
    /// The explosive bow's <c>explosive_damage</c> (15) and <c>explosive_radius</c> (30 units, R10168-1): each of its arrow's
    /// impacts casts a blast (the donor's <c>weapon_MMDummyCaster</c> and <c>spell_fire_1</c> there) that takes
    /// <see cref="ExplosiveDamage"/> from every NPC whose origin is within <see cref="ExplosiveRadius"/> of it
    /// (<c>l01_a-v18-bow-r2</c>: 15 from the orc 25.6 units from the second impact; none from impacts 51 and 72 off).
    /// </summary>
    internal bool Explosive => ExplosiveDamage > 0;
    /// <summary>The rope bow (<c>bow_ropearrow</c>): its arrow sticks where it stops, a rope's anchor (R10168-2).</summary>
    internal bool Rope => Model.Contains("/bow_ropearrow/", StringComparison.Ordinal);

    internal const string Classic = "models/items/weapons/bow_classic/bow_classic.mdl";

    /// <summary>The arms' clips (<c>first_basic.mdl</c>), each played for its own length.</summary>
    internal const string Rest = "layer_bow_idle", Draw = "layer_bowattack_in", Drawn = "layer_bowattack_idle", Shoot = "layer_bowattack_shoot",
        Out = "layer_bowattack_out", FastIdle = "layer_bowattack_fastidle", FastReload = "layer_bowattack_fastreload", FastOut = "layer_bowattack_fastout";

    /// <summary>
    /// The head's clips (<c>player_head_mvt.mdl</c>; #10161's camera): the draw's dip, <c>layer_bowattack_in</c>, which
    /// with <c>stability</c> is <c>layer_bowattack_stability_in</c> and settles by 1.4 s; without it the 1 s loop
    /// <c>layer_bow_stabilisation</c> follows the draw, its weight falling from 1 to none over <see cref="SwayFade"/> s
    /// (0.86, 0.62, 0.38 and 0.13 at the recorded peaks, 2.6 to 5.5 s into the draw).
    /// </summary>
    internal const string HeadDraw = "layer_bowattack_in", HeadSteadyDraw = "layer_bowattack_stability_in", HeadSway = "layer_bow_stabilisation";
    internal const float SwayFade = 4;

    /// <summary>
    /// The zoom (<c>bow_zoom</c>, Eagle Eye: "Press +zoom while using a bow"): held with the bow drawn, the view narrows to
    /// 45° across at 4:3 (the donor's 101.4° to 57.8° across at 16:9: <see cref="ZoomedDegrees"/> high at any aspect) in
    /// <see cref="ZoomIn"/> s, and widens in <see cref="ZoomOut"/> s once it's let go (<c>l01_a-v18-bow-zoom</c>). At rest it
    /// does nothing.
    /// </summary>
    internal const float ZoomIn = .38f, ZoomOut = .2f;
    internal static readonly float ZoomedDegrees = 2 * MathF.Atan(MathF.Tan(22.5f * MathF.PI / 180) * 3 / 4) * 180 / MathF.PI;

    internal static BowRules Of(DonorContent donor, string model) =>
        new(VpkIndex.Normalize(model), (int)(donor.Number(model, "entity_data", "ammo") ?? 30),
            ExplosiveDamage: donor.Number(model, "entity_data", "explosive_damage") ?? 0, ExplosiveRadius: donor.Number(model, "entity_data", "explosive_radius") ?? 0);

    /// <summary>Whether a model path is a bow's: its folder under <c>models/items/weapons</c> is <c>bow_*</c>.</summary>
    internal static bool IsBow(string model)
    {
        var parts = VpkIndex.Normalize(model).Split('/');
        return parts.Length >= 5 && parts[^5] == "models" && parts[^4] == "items" && parts[^3] == "weapons" && parts[^2].StartsWith("bow_", StringComparison.Ordinal);
    }

    /// <summary>The pitch up from which the compensation ends: <c>mm_arrow_pitch_compensation_start</c>'s −45.</summary>
    internal const float CompensationStart = 45;

    /// <summary>How far above a view <paramref name="pitchUp"/> degrees up an arrow leaves, degrees.</summary>
    internal float Raise(float pitchUp) => pitchUp < CompensationStart ? RaiseDegrees : 1.34f + MathF.Cos(pitchUp * MathF.PI / 180);

    internal float SpeedMetres => SpeedUnits * SourceSpace.MetresPerUnit;
    internal float GravityMetres => GravityUnits * SourceSpace.MetresPerUnit;

    /// <summary>The arrow's velocity, m/s, leaving along <paramref name="forward"/> (the view), raised by <see cref="Raise"/>.</summary>
    internal Vector3 Launch(Vector3 forward)
    {
        var view = Vector3.Normalize(forward);
        var level = view with { Y = 0 };
        if (level.LengthSquared() < 1e-6f) return view * SpeedMetres;
        level = Vector3.Normalize(level);
        float up = MathF.Asin(Math.Clamp(view.Y, -1, 1));
        float pitch = up + Raise(up * 180 / MathF.PI) * MathF.PI / 180;
        return (level * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch)) * SpeedMetres;
    }
}

/// <summary>Where the bow is in its cycle.</summary>
internal enum BowState { Rest, Pressed, Drawing, Drawn, Shooting, Out, FastIdle, FastReload, FastOut }

/// <summary>
/// The bow in hand, as the donor plays it (#10168; <c>l01_a-v18-bow</c>, <c>-bow-basic</c>, <c>-bow-more</c>): from rest a
/// press draws it after <see cref="BowRules.DrawDelay"/>; the draw (<c>layer_bowattack_in</c>, 2 s) always plays out, and
/// the arrow goes once it's drawn and attack isn't held: a tap's as the draw ends, a hold's when it's let go, and one let
/// go and pressed again during the draw waits for the next release. After the shot (<c>layer_bowattack_shoot</c>), with
/// <c>bow_reload</c> the bow waits in <c>layer_bowattack_fastidle</c>, where attack held nocks the next arrow at once
/// (<c>layer_bowattack_fastreload</c>, 1 s, then drawn), else it lowers (<c>layer_bowattack_fastout</c>); without the
/// skill it lowers (<c>layer_bowattack_out</c>), and a press meanwhile draws only if still held at rest. A parry
/// (attack2) pressed during the draw puts the arrow down, back to rest at once; one held from before doesn't. Each phase
/// lasts its clip and a tick (<see cref="BowRules.Settle"/>).
/// </summary>
internal sealed class LabBow(BowRules rules, Func<string, float> clipSeconds)
{
    internal BowRules Rules { get; } = rules;
    internal BowState State { get; private set; }
    /// <summary>Seconds into the current state.</summary>
    internal float Age { get; private set; }
    /// <summary>Seconds since the draw began (the head's sway runs from it), or NaN at rest.</summary>
    internal float DrawAge { get; private set; } = float.NaN;
    internal int Arrows { get; private set; } = rules.Ammo;
    internal int Shots { get; private set; }
    bool shotLeft, wasAttack, wasParry, pressedAtRest;
    // Since attack last went down, s; NaN once that press is spent or lost.
    float pressAge = float.NaN;

    /// <summary>The arms' clip now and its cycle.</summary>
    internal (string Clip, float Cycle) Clip => State switch
    {
        BowState.Drawing => (BowRules.Draw, Cycle(BowRules.Draw)),
        BowState.Drawn => (BowRules.Drawn, 0),
        BowState.Shooting => (BowRules.Shoot, Cycle(BowRules.Shoot)),
        BowState.Out => (BowRules.Out, Cycle(BowRules.Out)),
        BowState.FastIdle => (BowRules.FastIdle, Cycle(BowRules.FastIdle)),
        BowState.FastReload => (BowRules.FastReload, Cycle(BowRules.FastReload)),
        BowState.FastOut => (BowRules.FastOut, Cycle(BowRules.FastOut)),
        _ => (BowRules.Rest, 0),
    };

    /// <summary>An arrow is nocked: drawn in the right hand.</summary>
    internal bool Nocked => State is BowState.Drawing or BowState.Drawn or BowState.FastReload || State == BowState.Shooting && !shotLeft;

    /// <summary>Drawn and held: <c>+zoom</c> may start.</summary>
    internal bool Aiming => State == BowState.Drawn;

    float Seconds(string clip) => MathF.Max(.01f, clipSeconds(clip));
    float Cycle(string clip) => Math.Clamp(Age / Seconds(clip), 0, 1);
    /// <summary>How long a phase playing <paramref name="clip"/> lasts: the clip, and the donor's tick past it.</summary>
    float Phase(string clip) => Seconds(clip) + Rules.Settle;

    void Enter(BowState state)
    {
        State = state;
        Age = 0;
    }

    /// <summary>A timed phase of <paramref name="seconds"/> over: the next begins with what the step had left of it.</summary>
    void Next(BowState state, float seconds)
    {
        State = state;
        Age = MathF.Max(0, Age - seconds);
    }

    bool Over(float seconds) => Age >= seconds - 1e-4f;

    void Lowered(float seconds)
    {
        Next(BowState.Rest, seconds);
        DrawAge = float.NaN;
    }

    /// <summary>The arms' landing with the bow (<c>jump_out_bow</c>), during which no draw begins.</summary>
    internal const string LandingClip = "jump_out_bow";

    /// <summary>
    /// One step: <paramref name="attack"/> held, <paramref name="parry"/> held, <paramref name="fastReload"/> the build's
    /// <c>bow_reload</c>, <paramref name="landing"/> the body in the air or landing (a press then draws once it's down: the
    /// donor's three presses during <c>jump_out_bow</c> drew at its end, 0.12 to 0.24 s late). True on the step the arrow
    /// leaves the hand.
    /// </summary>
    internal bool Step(float seconds, bool attack, bool parry, bool fastReload, bool landing = false)
    {
        Age += seconds;
        if (!float.IsNaN(DrawAge)) DrawAge += seconds;
        if (attack && !wasAttack) { pressAge = 0; pressedAtRest = State is BowState.Rest or BowState.Pressed; }
        else if (!float.IsNaN(pressAge)) pressAge += seconds;
        // A parry pressed, or a jump or fall, puts a drawn arrow down (a fall while drawn, bow-r4, then a new draw).
        bool parried = parry && !wasParry || landing && State is BowState.Drawing or BowState.Drawn or BowState.FastReload;
        wasAttack = attack;
        wasParry = parry;
        bool left = false;
        switch (State)
        {
            case BowState.Rest:
            case BowState.Pressed:
                // A press at rest draws once its delay has passed, held or not; one made while the bow lowered draws if
                // it's still held at rest, the delay counted from the press, and no sooner than RestDelay after it.
                if (float.IsNaN(pressAge) || Arrows <= 0 || !attack && !pressedAtRest) { pressAge = float.NaN; Enter(BowState.Rest); break; }
                if (landing) { if (State == BowState.Rest) Enter(BowState.Pressed); break; }
                float wait = MathF.Max(Rules.DrawDelay - pressAge, pressedAtRest ? 0 : Rules.RestDelay - Age);
                if (wait > 1e-4f) { if (State == BowState.Rest && pressedAtRest) Enter(BowState.Pressed); break; }
                Age = MathF.Max(0, -wait);
                State = BowState.Drawing;
                pressAge = float.NaN;
                DrawAge = Age;
                break;
            case BowState.Drawing:
                // A parry pressed during the draw puts the arrow down (one held from before doesn't).
                if (parried) { Enter(BowState.Rest); DrawAge = float.NaN; break; }
                if (Over(Phase(BowRules.Draw))) Next(BowState.Drawn, Phase(BowRules.Draw));
                break;
            case BowState.Drawn:
                // Drawn, the arrow goes once attack isn't held: at once for one let go during the draw, which the donor shot
                // a frame after its draw; a press again during the draw holds it.
                if (parried) { Enter(BowState.Rest); DrawAge = float.NaN; break; }
                if (!attack) { Enter(BowState.Shooting); shotLeft = false; }
                break;
            case BowState.Shooting:
                if (Over(Phase(BowRules.Shoot))) Next(fastReload ? BowState.FastIdle : BowState.Out, Phase(BowRules.Shoot));
                break;
            case BowState.FastIdle:
                if (attack && Arrows > 0) { Enter(BowState.FastReload); pressAge = float.NaN; }
                else if (Over(Phase(BowRules.FastIdle))) Next(BowState.FastOut, Phase(BowRules.FastIdle));
                break;
            case BowState.FastReload:
                if (parried) { Enter(BowState.Rest); DrawAge = float.NaN; break; }
                if (Over(Phase(BowRules.FastReload)))
                {
                    // Nocked again: the head's sway goes on as after a draw (not recorded: the reloads were shot at once).
                    DrawAge = Seconds(BowRules.Draw);
                    Next(BowState.Drawn, Phase(BowRules.FastReload));
                }
                break;
            case BowState.Out:
                if (Over(Phase(BowRules.Out))) Lowered(Phase(BowRules.Out));
                break;
            case BowState.FastOut:
                if (Over(Phase(BowRules.FastOut))) Lowered(Phase(BowRules.FastOut));
                break;
        }
        // The arrow leaves a moment into the shot.
        if (State == BowState.Shooting && !shotLeft && Over(Rules.LeaveDelay))
        {
            shotLeft = left = true;
            Arrows--;
            Shots++;
        }
        return left;
    }

    /// <summary>Back at rest with a full quiver.</summary>
    internal void Reset()
    {
        Enter(BowState.Rest);
        DrawAge = pressAge = float.NaN;
        wasAttack = wasParry = false;
        Arrows = Rules.Ammo;
        Shots = 0;
    }
}

/// <summary>
/// One arrow in flight: it falls under the bow's gravity until it meets something, and strikes by the bow that shot it
/// (<see cref="Rules"/>), whatever is in hand by then (R10168-4).
/// </summary>
internal sealed class LabArrow(Vector3 at, Vector3 velocity, BowRules rules)
{
    /// <summary>The bow that shot it: its model (its <c>WDmg</c>) and its flight.</summary>
    internal BowRules Rules { get; } = rules;
    /// <summary>Its velocity as it left, m/s.</summary>
    internal Vector3 Launched { get; } = velocity;
    internal Vector3 Position { get; private set; } = at;
    internal Vector3 Velocity { get; private set; } = velocity;
    internal float Age { get; private set; }
    /// <summary>Where it stopped, once it has.</summary>
    internal Vector3? Stopped { get; private set; }
    /// <summary>It has bounced off the world once.</summary>
    internal bool Bounced { get; private set; }
    /// <summary>Its impacts on the world: where, in order (a bounce's, then where it stopped).</summary>
    internal List<Vector3> Impacts { get; } = [];

    /// <summary>The longest an arrow flies before it's dropped, s (the recorded ones met the world within 3.3 s).</summary>
    internal const float LifeSeconds = 5;

    /// <summary>
    /// One step: it moves along its velocity, which gravity bends. Where <paramref name="meets"/> finds something on the
    /// step's segment (the point, the surface's normal, and whether it's the world) it stops, unless it's the world met at
    /// a grazing angle for the first time: then it bounces (<see cref="BowRules.Restitution"/>). It stops anyway when its
    /// time is up.
    /// </summary>
    internal void Step(float seconds, Func<Vector3, Vector3, (Vector3 Point, Vector3 Normal, bool World)?> meets)
    {
        if (Stopped is not null) return;
        Age += seconds;
        var velocity = Velocity - Vector3.UnitY * Rules.GravityMetres * seconds;
        var next = Position + (Velocity + velocity) / 2 * seconds;
        Velocity = velocity;
        if (meets(Position, next) is { } hit)
        {
            Position = hit.Point;
            if (hit.World) Impacts.Add(hit.Point);
            var normal = hit.Normal.LengthSquared() > 1e-6f ? Vector3.Normalize(hit.Normal) : -Vector3.Normalize(velocity);
            float grazing = MathF.Asin(Math.Clamp(-Vector3.Dot(Vector3.Normalize(velocity), normal), -1, 1)) * 180 / MathF.PI;
            if (hit.World && !Bounced && grazing < Rules.BounceBelowDegrees)
            {
                Bounced = true;
                Velocity = (velocity - 2 * Vector3.Dot(velocity, normal) * normal) * Rules.Restitution;
                // Off the surface a hair, so the next step doesn't meet it again at once.
                Position = hit.Point + normal * .005f;
                return;
            }
            Stopped = hit.Point;
            return;
        }
        Position = next;
        if (Age >= LifeSeconds) Stopped = Position;
    }
}
