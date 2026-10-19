using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Combat;

/// <summary>One traced step of a swing or kick, and what came of it, for the overlay and the readouts.</summary>
/// <param name="Group">The hit group struck on the orc's body; null for its items, a kick or a miss.</param>
/// <param name="Damage">What the blow asked; 0 when parried, blocked or nothing was struck.</param>
internal sealed record SwingTrace(int Swing, string Move, float Cycle, Vector3 Start, Vector3 End, Vector3? Point, string Outcome,
    int? Group = null, float Damage = 0);

/// <summary>
/// A decided blow, as a replay compares it with the donor's: the player's on the orc, or the orc's that reached the
/// player. <see cref="Verdict"/> is the orc's (Hit, Parry, Shield) or the player's parry (PlayerParried,
/// PlayerParryBroken); a kick that lands is a Hit.
/// </summary>
internal sealed record CombatEvent(ulong Tick, bool ByPlayer, string Move, DefenseResult Verdict, float Damage, int? Group);

/// <summary>
/// Dark Messiah's melee in the lab: the player's sword (<see cref="PlayerSword"/>, drawn and posed by
/// <see cref="FirstPerson"/>) against one <see cref="Orc"/>, decided by the game's own rules (<see cref="CombatModel"/>,
/// whose numbers come from the imported .qct files and settings):
/// <list type="bullet">
/// <item>while a swing's hit window is open, the blade is traced every step from the eye toward its tip, out to the
/// sword's range, through the orc's hit boxes and its cleaver's and shield's hulls (<see cref="HitSweep"/>, as the
/// mirror's Shadow contacts measure the donor's blows);</item>
/// <item>the first contact of a swing is the blow: the orc's verdict (<see cref="CombatModel.Verdict"/>) by what it
/// struck and its parry state, then the damage the blow asks (<see cref="CombatModel.Damage"/>);</item>
/// <item>a kick reaches an orc in front within <see cref="CombatTuning.KickRange"/>, asks its damage and pushes it;</item>
/// <item>an orc's blow isn't traced, as the donor's on the player isn't: it lands when the player is within
/// <see cref="OrcTuning.Reach"/> in front of the orc, the player's parry turns it unless the part breaks parries
/// (<see cref="CombatModel.PlayerParry"/>), and otherwise it asks its damage.</item>
/// </list>
/// Hit tests use a spatial session of their own, so the hulls never touch the player's body or the loose cubes.
/// </summary>
internal sealed class LabCombat : IDisposable
{
    internal const string Set = "lab.combat";
    const string PlayerModel = "models/npc/player/player_basic.mdl";
    /// <summary>The trace's radius, metres: the donor's trace is a ray (docs/combat.md, "Contacts").</summary>
    const float Radius = .05f * SourceSpace.MetresPerUnit;
    const double ShowSeconds = 1.2;

    readonly IEngineContext engine;
    readonly SpatialSession session;
    readonly HitSweep sweep;
    readonly CombatModel model;
    readonly List<(SwingTrace Trace, double Until)> recent = [];
    double now;
    int settledSwing = -1;
    double deadSeconds;

    internal LabCombat(IEngineContext engine, DonorContent donor, CombatTuning tuning)
    {
        this.engine = engine;
        Tuning = tuning;
        session = engine.Spatial.CreateSession(new SpatialSessionConfig(1, 16, VoxelSurfaceMode.GreedyCubes));
        sweep = new HitSweep(engine, session);
        model = new CombatModel(donor.Combat);
        FirstPerson = new FirstPerson(engine, donor);
        Sword = new PlayerSword(SwordClips.From(FirstPerson.ArmsModel.Info, FirstPerson.AttackSpeed, tuning.KickWindow), tuning);
        Orc = new Orc(engine, donor, tuning.Orc);
        PlayerHealth = tuning.PlayerHealth;
    }

    internal CombatTuning Tuning { get; private set; }
    internal PlayerSword Sword { get; }
    internal FirstPerson FirstPerson { get; }
    internal Orc Orc { get; }
    internal float PlayerHealth { get; private set; }
    internal bool God { get; set; }
    internal string Message { get; private set; } = "";
    internal SwingTrace? LastBlow { get; private set; }
    /// <summary>When set, every decided blow is added to it (a replay's comparison).</summary>
    internal List<CombatEvent>? Log { get; set; }
    internal (int Swings, int Hits, int Parried, int Blocked, int Missed, float Dealt) Player { get; private set; }
    internal (int Blows, int Reached, int Parried, int Broken, float Dealt, int Deaths) OrcStats { get; private set; }
    /// <summary>How much of the movement input moves the body now.</summary>
    internal float MovementScale => Sword.Busy ? Tuning.AttackMovementScale : 1;

    internal void Retune(CombatTuning next)
    {
        Tuning = next;
        Sword.Retune(SwordClips.From(FirstPerson.ArmsModel.Info, FirstPerson.AttackSpeed, next.KickWindow), next);
        Orc.Retune(next.Orc);
    }

    /// <summary>One admitted step of the fight.</summary>
    /// <param name="orcPose">In a replay, the recorded orc the lab's follows this step instead of fighting on its own.</param>
    internal void Step(float seconds, ulong tick, bool attackHeld, bool blockHeld, bool kickPressed, Vector3 eye, Vector3 forward, Vector3 feet,
        OrcPose? orcPose = null)
    {
        int swingsBefore = Sword.Swing;
        Sword.Step(seconds, attackHeld, blockHeld, kickPressed);
        if (Sword.Swing != swingsBefore) Player = Player with { Swings = Player.Swings + 1 };
        FirstPerson.Pose(Sword, seconds, eye, forward);
        var blows = orcPose is null ? Orc.Step(seconds, feet, Sword.SwingStarted) : Orc.Follow(orcPose, seconds);
        if (Sword.Traced && settledSwing != Sword.Swing)
        {
            if (Sword.Move == SwordMove.Kick) Kick(tick, eye, forward);
            else Swing(tick, eye, feet);
        }
        else if (Sword.Move is SwordMove.Fast or SwordMove.Power && settledSwing != Sword.Swing && Sword.Clip.Traced && Sword.Cycle > Sword.Clip.Close)
        {
            settledSwing = Sword.Swing;
            Player = Player with { Missed = Player.Missed + 1 };
        }
        foreach (var blow in blows) OrcStrikes(tick, blow, feet);
        if (PlayerHealth <= 0 && (deadSeconds += seconds) > 2) { PlayerHealth = Tuning.PlayerHealth; deadSeconds = 0; Message = "Back on your feet"; }
    }

    void Swing(ulong tick, Vector3 eye, Vector3 feet)
    {
        var (start, end) = FirstPerson.Trace(eye);
        var targets = Orc.Targets();
        var contacts = targets.Count == 0 ? [] : sweep.Sweep(start, end, Radius, targets);
        if (contacts.Count == 0) { Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, start, end, null, "none")); return; }
        settledSwing = Sword.Swing;
        var c = contacts[0];
        var orc = Orc.Party();
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        Struck struck = c.Target == 0 ? Struck.Body : c.Target == 1 ? Struck.Weapon : Struck.OffHand;
        Vector3 toPlayer = feet - Orc.Feet;
        toPlayer.Y = 0;
        float front = toPlayer.LengthSquared() > 1e-6f ? Vector3.Dot(Orc.Facing, Vector3.Normalize(toPlayer)) : 0;
        var verdict = CombatModel.Verdict(new DefenseCase((int)tick, orc, player, "weapon_arx_short_sword", struck, front, DefenseResult.Hit, Orc.DefenseState));
        string what = struck == Struck.Body ? $"hit group {targets[0].Model.Hitboxes[c.Hitbox].Group}" : struck == Struck.Weapon ? "its cleaver" : "its shield";
        if (verdict is DefenseResult.Parry or DefenseResult.Shield)
        {
            Sword.Bounce();
            Player = verdict == DefenseResult.Parry ? Player with { Parried = Player.Parried + 1 } : Player with { Blocked = Player.Blocked + 1 };
            Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, $"{verdict} ({what})"), blow: true);
            Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, 0, null));
            Message = verdict == DefenseResult.Parry ? "The orc parries" : "The orc blocks";
            return;
        }
        int? group = struck == Struck.Body ? targets[0].Model.Hitboxes[c.Hitbox].Group : null;
        float asked = model.Damage(new DamageCase((int)tick, orc, player, "weapon_arx_short_sword", group, 0x4, 0, 0, Orc.Health, Orc.Health, false,
            WeaponModel: FirstPerson.SwordModel, VictimAlert: 3)) ?? 0;
        Orc.Damage(asked);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, FormattableString.Invariant($"{asked:0.##} ({what})"), group, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, asked, group));
        Message = Orc.Alive ? FormattableString.Invariant($"Hit for {asked:0.##}") : "The orc falls";
    }

    void Kick(ulong tick, Vector3 eye, Vector3 forward)
    {
        settledSwing = Sword.Swing;
        Vector3 toOrc = Orc.Feet + Vector3.UnitY * 1.0f - eye;
        Vector3 flat = toOrc with { Y = 0 };
        bool reaches = Orc.Alive && flat.Length() <= Tuning.KickRange && Vector3.Dot(Vector3.Normalize(forward with { Y = 0 }), Vector3.Normalize(flat)) > .5f;
        if (!reaches)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, eye + forward * Tuning.KickRange, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        float asked = model.Damage(new DamageCase((int)tick, Orc.Party(), player, "-", null, 0x40000, 0x20, 0, Orc.Health, Orc.Health, false, VictimAlert: 3)) ?? 0;
        Orc.Damage(asked);
        Orc.Push(flat, Tuning.KickPush);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, Orc.Feet + Vector3.UnitY, Orc.Feet + Vector3.UnitY, FormattableString.Invariant($"kick {asked:0.##}"), Damage: asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = "Kick";
    }

    void OrcStrikes(ulong tick, OrcBlow blow, Vector3 feet)
    {
        OrcStats = OrcStats with { Blows = OrcStats.Blows + 1 };
        Vector3 toPlayer = (feet - Orc.Feet) with { Y = 0 };
        if (toPlayer.Length() > Tuning.Orc.Reach || toPlayer.LengthSquared() > 1e-6f && Vector3.Dot(Orc.Facing, Vector3.Normalize(toPlayer)) < Tuning.Orc.FrontCosine) return;
        OrcStats = OrcStats with { Reached = OrcStats.Reached + 1 };
        var orc = Orc.Party() with { Cycle = blow.Cycle };
        bool broken = false;
        if (Sword.Parrying)
        {
            var outcome = model.PlayerParry(new ParryCase((int)tick, orc, DefenseResult.PlayerParried, blow.Part));
            if (outcome == DefenseResult.PlayerParried)
            {
                OrcStats = OrcStats with { Parried = OrcStats.Parried + 1 };
                Message = "Parried";
                Log?.Add(new CombatEvent(tick, false, blow.Attack, DefenseResult.PlayerParried, 0, null));
                return;
            }
            Sword.Break();
            broken = true;
            OrcStats = OrcStats with { Broken = OrcStats.Broken + 1 };
        }
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        float asked = model.Damage(new DamageCase((int)tick, player, orc, "weapon_arxorccleaver", null, 0x4, 0, 0, PlayerHealth, PlayerHealth, false,
            blow.Part, Npc.Orc.Cleaver)) ?? 0;
        OrcStats = OrcStats with { Dealt = OrcStats.Dealt + asked };
        Log?.Add(new CombatEvent(tick, false, blow.Attack, broken ? DefenseResult.PlayerParryBroken : DefenseResult.Hit, asked, null));
        if (!God)
        {
            PlayerHealth -= asked;
            if (PlayerHealth <= 0) { PlayerHealth = 0; deadSeconds = 0; OrcStats = OrcStats with { Deaths = OrcStats.Deaths + 1 }; Message = "You fall"; return; }
        }
        Message = FormattableString.Invariant($"The orc's {blow.Attack} hits for {asked:0.##}");
    }

    void Record(SwingTrace trace, bool blow = false)
    {
        recent.Add((trace, now + ShowSeconds));
        if (blow) LastBlow = trace;
    }

    /// <summary>What to draw: the arms, the sword and the orc.</summary>
    internal IEnumerable<AppearanceFact> Facts() => FirstPerson.Facts().Concat(Orc.Facts());

    /// <summary>The overlay set <c>lab.combat</c>: each traced step of the recent swings (green struck, blue parried or blocked, grey nothing) and the orc's reach.</summary>
    internal void Present(double hostSeconds)
    {
        now += hostSeconds;
        recent.RemoveAll(r => r.Until <= now);
        var items = new List<OverlayItem>();
        foreach (var (t, _) in recent)
        {
            Color color = t.Point is null ? new Color(.6f, .65f, .72f, 1) : t.Outcome.StartsWith("Parry", StringComparison.Ordinal) || t.Outcome.StartsWith("Shield", StringComparison.Ordinal)
                ? new Color(.35f, .6f, 1, 1) : new Color(.25f, .95f, .45f, 1);
            items.Add(OverlayItem.Segment(OverlayPlace.World, t.Start, t.Point ?? t.End, color, onTop: true));
            if (t.Point is Vector3 p)
            {
                items.Add(OverlayItem.Marker(OverlayPlace.World, p, .08f, color, onTop: true));
                items.Add(OverlayItem.Label(OverlayPlace.World, p + Vector3.UnitY * .15f, t.Outcome, color, onTop: true));
            }
        }
        if (Orc.Alive) items.Add(OverlayItem.WireSphere(OverlayPlace.World, Orc.Feet + Vector3.UnitY, Tuning.Orc.Reach, new Color(1, .55f, .3f, .5f), onTop: false));
        engine.Graphics.PublishOverlaySet(new OverlaySetRequest(Set, items.ToArray(), []));
    }

    internal void Reset()
    {
        Sword.Reset();
        Orc.Spawn();
        PlayerHealth = Tuning.PlayerHealth;
        recent.Clear();
        Player = default;
        OrcStats = default;
        LastBlow = null;
        Message = "";
        settledSwing = -1;
    }

    public void Dispose()
    {
        FirstPerson.Dispose();
        Orc.Dispose();
        session.Dispose();
    }
}
