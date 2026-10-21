using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
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
/// <item>an orc's blow isn't traced, as the donor's on the player isn't: it lands when the donor's reach test passes
/// (<see cref="CombatModel.Reach"/>: a sweep for the attack part's range toward the player, entering the player's
/// box), the player's parry turns it unless the part breaks parries (<see cref="CombatModel.PlayerParry"/>), a raised
/// shield blocks it from in front (<see cref="CombatModel.PlayerBlock"/>) and takes its damage, and otherwise it asks
/// its damage;</item>
/// <item>the shield bash is a swing like the others, and pushes the orc it lands on (<see cref="BashPush"/>).</item>
/// </list>
/// Hit tests use a spatial session of their own, so the hulls never touch the player's body or the loose cubes.
/// </summary>
internal sealed class LabCombat : IDisposable
{
    internal const string Set = "lab.combat";
    const string PlayerModel = "models/npc/player/player_basic.mdl";
    /// <summary>Source's head hit group.</summary>
    const int HeadGroup = 1;
    /// <summary>
    /// The trace's radius, metres: the player's sweep is a box of half-size <c>collision_inc_size_ray</c>, 1.5 units (its
    /// type 0; docs/wire.md, "Melee sweeps"), swept as a sphere of that radius.
    /// </summary>
    const float Radius = 1.5f * SourceSpace.MetresPerUnit;
    const double ShowSeconds = 1.2;

    readonly IEngineContext engine;
    readonly DonorContent donor;
    readonly StaminaRules staminaRules;
    readonly SpatialSession session;
    readonly HitSweep sweep;
    readonly CombatModel model;
    readonly List<(SwingTrace Trace, double Until)> recent = [];
    double now;
    // The fight's game time, and the slow motion asked for on it: the world runs at the factor from start to end.
    double fightSeconds, slowStart = double.NaN, slowEnd = double.NaN;
    float slowFactor = 1;
    // The swing window last settled (a hit, or a kick decided: SwingKey), and the last swing that hit or missed outright.
    int settledSwing = -1, decidedSwing = -1;
    // The critical roll: seeded, so fights, replays and the smoke repeat.
    Random criticals = new(CriticalSeed);
    internal const int CriticalSeed = 10146;
    bool canFatality = true;
    double deadSeconds;

    internal LabCombat(IEngineContext engine, DonorContent donor, CombatTuning tuning)
    {
        this.engine = engine;
        Tuning = tuning;
        session = engine.Spatial.CreateSession(new SpatialSessionConfig(1, 16, VoxelSurfaceMode.GreedyCubes));
        sweep = new HitSweep(engine, session);
        model = new CombatModel(donor.Combat);
        FirstPerson = new FirstPerson(engine, donor);
        Sword = new PlayerSword(FirstPerson.Clips(FirstPerson.Weapon, tuning));
        Orc = new Orc(engine, donor, tuning.Orc);
        this.donor = donor;
        staminaRules = StaminaRules.From(donor);
        Stamina = new PlayerStamina(staminaRules);
        PlayerHealth = tuning.PlayerHealth;
    }

    internal CombatTuning Tuning { get; private set; }
    /// <summary>The combat model the fight decides with, read from the imported .qct files and settings.</summary>
    internal CombatModel Model => model;
    internal PlayerSword Sword { get; }
    /// <summary>The player's build: the skill modules, weapon and off-hand the fight plays with (<see cref="Rebuild"/>).</summary>
    internal CharacterBuild? Build { get; private set; }

    /// <summary>
    /// Plays <paramref name="build"/>: stamina takes its maximum, the hand its weapon (<see cref="Arm"/>; a weapon the lab
    /// doesn't play, or none, keeps the one in hand), and the sword its gated moves (the parry, the chained slash, the
    /// jump and rush attacks). Not <paramref name="arm"/>ing keeps the weapon in hand (a replay's build change mid-run:
    /// its weapon switches come on their own, <see cref="Arm"/>).
    /// </summary>
    internal void Rebuild(CharacterBuild build, bool draw = true, bool arm = true)
    {
        Build = build;
        Stamina.Retune(staminaRules.For(build));
        Wear(ShieldOf(build));
        if (arm && LabWeapon.ClassOf(build.Weapon) is not null) Arm(build.Weapon, draw);
        Sword.CanParry = build.Has("combat_parry");
        Sword.ChainLength = Weapon.Simple ? 2 : build.Has("combat_chained_slash") ? 5 : 2;
        Sword.CanLeap = build.Has("combat_vertical_attack");
        Sword.CanRush = build.Has("combat_rush_attack");
        canFatality = build.Has("combat_fatality");
        Sword.CanWhirl = build.Has("combat_trample");
    }

    readonly Dictionary<(string, bool), SwordClips?> weaponClips = [];

    /// <summary>
    /// A weapon's clips at the tuning the lab started with, by its model, with a <paramref name="shield"/> on the arm or
    /// not; null for one the lab doesn't play (the calibrations' move sets).
    /// </summary>
    internal SwordClips? ClipsFor(string model, bool shield = false)
    {
        model = RustyBridge.Wire.Donor.VpkIndex.Normalize(model);
        if (!weaponClips.TryGetValue((model, shield), out var clips))
            weaponClips[(model, shield)] = clips = LabWeapon.ClassOf(model) is not { } weaponClass ? null
                : SwordClips.From(FirstPerson.ArmsModel.Info, weaponClass, LabWeapon.Of(donor, model).AttackSpeed, Tuning, LabWeapon.Of(donor, model).Simple, shield);
        return clips;
    }

    /// <summary>
    /// The shield a build wears: its off-hand when that is a shield and the build has <c>combat_shield_enabled</c> (the
    /// combat skill's level 2), which the game's <c>sword_shield</c> moves need; null otherwise.
    /// </summary>
    internal LabShield? ShieldOf(CharacterBuild build) =>
        build.OffHand is { } offHand && LabShield.IsShield(offHand) && build.Has("combat_shield_enabled")
            ? LabShield.Of(donor, offHand, model.Data.Settings.PlayerShieldHealthCoeff) : null;

    /// <summary>The shield on the arm, or null.</summary>
    internal LabShield? Shield => FirstPerson.Shield;
    /// <summary>What is left of the shield's health: blocked blows take their damage from it, and at none it breaks.</summary>
    internal float ShieldHealth { get; private set; }
    /// <summary>The shield is raised: blocking, with a sword in hand.</summary>
    internal bool Blocking => Sword.Parrying && Shield is not null && Weapon.Class == WeaponClass.Sword;

    /// <summary>
    /// Puts <paramref name="shield"/> on the arm (null takes it off), at its full health, and plays the sword's set for it
    /// (<c>sword_shield</c> with one, <c>sword_noshield</c> without) from the next move on.
    /// </summary>
    void Wear(LabShield? shield)
    {
        if (shield?.Model != Shield?.Model) ShieldHealth = shield?.Health ?? 0;
        bool changed = (shield is null) != (Shield is null);
        FirstPerson.Wear(shield);
        if (changed && drawing is null) Sword.Retune(FirstPerson.Clips(Weapon, Tuning));
    }

    /// <summary>
    /// How far the shield bash pushes the orc, metres: it was pushed 1.28, 1.62 and 1.32 m by the three that landed in
    /// <c>l01_a-v17-shield</c>, playing <c>Push_*_Front_small</c> for 1.2 s. The bash's <c>.qct</c> event has
    /// <c>push</c> and <c>alwayspush</c>; other moves with them (the staff's power attacks) aren't recorded pushing.
    /// </summary>
    internal const float BashPush = 1.4f;

    /// <summary>The weapon the hand holds, or is drawing: the build's.</summary>
    internal LabWeapon Weapon => drawing ?? FirstPerson.Weapon;
    // The weapon being drawn once the one in hand is put away (null when none is).
    LabWeapon? drawing;

    /// <summary>
    /// Takes <paramref name="model"/> in hand: the sword puts the one in hand away and draws it (<see cref="PlayerSword.Equip"/>),
    /// its model in the hand once the old one is away; or, not <paramref name="draw"/>ing (a replay's start), at once.
    /// </summary>
    internal void Arm(string model, bool draw = true)
    {
        var weapon = LabWeapon.Of(donor, model);
        if (weapon.Model == Weapon.Model) return;
        Sword.ChainLength = weapon.Simple ? 2 : Build?.Has("combat_chained_slash") == true ? 5 : 2;
        if (!draw)
        {
            drawing = null;
            FirstPerson.Equip(weapon);
            Sword.Retune(FirstPerson.Clips(weapon, Tuning));
            Sword.Reset();
            return;
        }
        drawing = weapon;
        Sword.Equip(FirstPerson.Clips(weapon, Tuning));
    }

    /// <summary>The authored build the lab plays (<c>build.json</c>), as last applied; replays play the recording's for their length.</summary>
    internal BuildFile? BuildFile { get; private set; }

    /// <summary>
    /// Plays the build an authored file describes, checked first: an invalid file throws and changes nothing. Its weapon
    /// is drawn (a switch), or put in hand at once when not <paramref name="draw"/>ing (the lab starting).
    /// </summary>
    internal void Rebuild(BuildFile file, bool draw = true)
    {
        var build = BuildFrom(file);
        BuildFile = file;
        Rebuild(build, draw);
    }

    /// <summary>The donor's skills, for the build page.</summary>
    internal IReadOnlyDictionary<string, SkillInfo> Skills => donor.Tables.Skills;

    /// <summary>The build an authored file describes, checked against the donor's tables.</summary>
    internal CharacterBuild BuildFrom(BuildFile file) =>
        CharacterBuild.From(file, donor.Tables, item => donor.Read(System.IO.Path.ChangeExtension(item, ".qct")) is not null);

    /// <summary>The player's stamina: sprinting drains it, a kick needs and costs it.</summary>
    internal PlayerStamina Stamina { get; }
    /// <summary>The player's adrenaline: landed blows fill it, a full gauge makes the next power strike the Fatality.</summary>
    internal PlayerAdrenaline Adrenaline { get; } = new();
    internal FirstPerson FirstPerson { get; }
    internal Orc Orc { get; }
    internal float PlayerHealth { get; private set; }
    internal bool God { get; set; }
    internal string Message { get; private set; } = "";
    internal SwingTrace? LastBlow { get; private set; }
    /// <summary>The reach test of the orc's last blow: where its sweep entered the player's box, and its range (Source units).</summary>
    internal ReachResult? LastOrcReach { get; private set; }
    /// <summary>When set, every decided blow is added to it (a replay's comparison).</summary>
    internal List<CombatEvent>? Log { get; set; }
    internal (int Swings, int Hits, int Parried, int Blocked, int Missed, float Dealt) Player { get; private set; }
    internal (int Blows, int Reached, int Parried, int Broken, float Dealt, int Deaths) OrcStats { get; private set; }
    /// <summary>How much of the movement input moves the body now.</summary>
    internal float MovementScale => Sword.Busy ? Tuning.AttackMovementScale : 1;

    internal void Retune(CombatTuning next)
    {
        Tuning = next;
        Sword.Retune(FirstPerson.Clips(Weapon, next));
        Orc.Retune(next.Orc);
    }

    /// <summary>One admitted step of the fight.</summary>
    /// <param name="kickHeld">The kick button is down: a press asks for a kick, which starts once the body may kick.</param>
    /// <param name="orcPose">In a replay, the recorded orc the lab's follows this step instead of fighting on its own.</param>
    /// <param name="move">The movement input (side, forward): it picks a power attack's direction.</param>
    /// <param name="grounded">The body is on the ground: Dark Messiah doesn't kick in the air.</param>
    /// <param name="sprinting">Sprint is held while the body moves: it sprints, draining stamina, once there is enough to start.</param>
    /// <param name="crouched">The body crouches (duck held).</param>
    internal void Step(float seconds, ulong tick, bool attackHeld, bool blockHeld, bool kickHeld, Vector3 eye, Vector3 forward, Vector3 feet,
        OrcPose? orcPose = null, Vector2 move = default, bool grounded = true, bool sprinting = false, bool crouched = false)
    {
        fightSeconds += seconds;
        AdrenalineGained = 0;
        FatalityLanded = false;
        int swingsBefore = Sword.Swing;
        Stamina.Step(seconds, sprinting);
        Sword.Fatal = canFatality && Adrenaline.Full;
        Sword.BackstabReady = BackstabReady(forward, feet, orcPose);
        Sword.Step(seconds, attackHeld, blockHeld, kickHeld, move, grounded && Stamina.MayKick, airborne: !grounded, sprinting: Stamina.Sprinting, crouched: crouched);
        // The old weapon is away: the new one is in the hand as its draw begins.
        if (drawing is { } next && Sword.Move != SwordMove.Undraw)
        {
            FirstPerson.Equip(next);
            drawing = null;
        }
        if (Sword.Swing != swingsBefore) Player = Player with { Swings = Player.Swings + 1 };
        FirstPerson.Pose(Sword, seconds, eye, forward);
        var blows = orcPose is null ? Orc.Step(seconds, feet, Sword.SwingStarted) : Orc.Follow(orcPose, seconds);
        if (Sword.Move == SwordMove.Whirlwind && Sword.SwingStarted) Whirl(tick, eye, feet);
        else if (Sword.Move == SwordMove.Backstab) Backstab(tick, eye, forward, feet);
        else if (Sword.Traced && settledSwing != SwingKey)
        {
            if (Sword.Move == SwordMove.Kick) Kick(tick, eye, forward);
            else Swing(tick, eye, forward, feet);
        }
        else if (Sword.Swinging && Sword.Move != SwordMove.Kick && decidedSwing != Sword.Swing && Sword.Clip.Traced && Sword.Cycle > Sword.Clip.LastClose)
        {
            decidedSwing = Sword.Swing;
            Player = Player with { Missed = Player.Missed + 1 };
        }
        foreach (var blow in blows) OrcStrikes(tick, blow, feet, forward);
        if (PlayerHealth <= 0 && (deadSeconds += seconds) > 2) { PlayerHealth = Tuning.PlayerHealth; deadSeconds = 0; Message = "Back on your feet"; }
    }

    void Swing(ulong tick, Vector3 eye, Vector3 forward, Vector3 feet)
    {
        // The donor sweeps several rays a tick, at most about 10° apart, across the arc the swing covered (#10179):
        // from where the last step left it to where it is now, the first contact ending the swing for its target.
        var targets = Orc.Targets();
        float from = Sword.TraceFromCycle, to = Sword.TraceCycle;
        var (fromSide, _) = ArcAt(from);
        var (toSide, _) = ArcAt(to);
        int rays = Math.Clamp((int)MathF.Ceiling(MathF.Abs(toSide - fromSide) / RayStepDegrees), 1, 12);
        Vector3 start = default, end = default;
        List<HitContact> contacts = [];
        for (int r = 1; r <= rays && contacts.Count == 0; r++)
        {
            (start, end) = Aim(eye, forward, from + (to - from) * r / rays);
            if (targets.Count > 0) contacts = sweep.Sweep(start, end, Radius, targets);
        }
        if (contacts.Count == 0) { Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, start, end, null, "none")); return; }
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        var c = contacts[0];
        var orc = Orc.Party();
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        Struck struck = c.Target == 0 ? Struck.Body : c.Target == 1 ? Struck.Weapon : Struck.OffHand;
        Vector3 toPlayer = feet - Orc.Feet;
        toPlayer.Y = 0;
        float front = toPlayer.LengthSquared() > 1e-6f ? Vector3.Dot(Orc.Facing, Vector3.Normalize(toPlayer)) : 0;
        var verdict = CombatModel.Verdict(new DefenseCase((int)tick, orc, player, "weapon_arx_short_sword", struck, front, DefenseResult.Hit, Orc.DefenseState));
        bool bash = Sword.Move == SwordMove.Bash;
        // The bash's event has breakparade: it goes through a parry. Not recorded (no orc parried one).
        if (bash && verdict == DefenseResult.Parry && model.EventOf(player)?.BreaksParry == true) verdict = DefenseResult.Hit;
        string what = struck == Struck.Body ? $"hit group {targets[0].Model.Hitboxes[c.Hitbox].Group}" : struck == Struck.Weapon ? "its cleaver" : "its shield";
        // The swing plays on: the donor's sword doesn't bounce off an orc's parry or shield (none of 23 recorded did).
        if (verdict is DefenseResult.Parry or DefenseResult.Shield)
        {
            Player = verdict == DefenseResult.Parry ? Player with { Parried = Player.Parried + 1 } : Player with { Blocked = Player.Blocked + 1 };
            Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, $"{verdict} ({what})"), blow: true);
            Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, 0, null));
            Message = verdict == DefenseResult.Parry ? "The orc parries" : "The orc blocks";
            return;
        }
        int? group = struck == Struck.Body ? targets[0].Model.Hitboxes[c.Hitbox].Group : null;
        float asked = Asked(tick, player, orc, group, bash ? SwordClips.BashWeapon : null);
        // The blow's .qct event gives adrenaline; the Fatality spends it, and a head blow of a cutting move kills.
        var hit = model.EventOf(player);
        Adrenaline.Gain(hit?.Adrenaline ?? 0);
        AdrenalineGained += hit?.Adrenaline ?? 0;
        if (Sword.Move == SwordMove.Fatality) { Adrenaline.Spend(); FatalityLanded = true; }
        // Only a Fatality's head blow cuts the head off: the power attack has cutheadflag too, and its head blows
        // never killed (#10177). The donor's beheaded orc becomes a ragdoll whatever its health.
        if (hit?.CutsHead == true && group == HeadGroup && Sword.Move == SwordMove.Fatality) asked = MathF.Max(asked, Orc.Health);
        bool wasAlive = Orc.Alive;
        Orc.Damage(asked);
        // A Fatality that kills slows the world, as the donor's every Fatality kill did (#10147).
        if (Sword.Move == SwordMove.Fatality && wasAlive && !Orc.Alive) AskSlowTime(SlowTime.FatalityKill);
        if (bash) Orc.Push((Orc.Feet - feet) with { Y = 0 }, BashPush, "Push_Body_Front_small");
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, FormattableString.Invariant($"{asked:0.##} ({what})"), group, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, asked, group));
        Message = !Orc.Alive ? "The orc falls" : bash ? "Bash" : FormattableString.Invariant($"Hit for {asked:0.##}");
    }

    /// <summary>
    /// Where this step traces the blade (docs/lab.md, "The blade's trace"): a ray as the donor's are, from just above the
    /// eye (<see cref="TraceRise"/>) along the view turned by the move's <see cref="SwingArc"/> at this point of its hit
    /// window, as far as the move's <c>.qct</c> range (120 units when it gives none).
    /// </summary>
    internal (Vector3 Start, Vector3 End) Aim(Vector3 eye, Vector3 forward, float cycle)
    {
        var clip = Sword.Clip;
        var (side, up) = ArcAt(cycle);
        var view = Vector3.Normalize(forward);
        var level = Vector3.Normalize(view with { Y = 0 });
        var left = Vector3.Cross(Vector3.UnitY, level);
        float pitch = MathF.Asin(Math.Clamp(view.Y, -1, 1)) + up * MathF.PI / 180, yaw = side * MathF.PI / 180;
        var direction = MathF.Cos(pitch) * (MathF.Cos(yaw) * level + MathF.Sin(yaw) * left) + MathF.Sin(pitch) * Vector3.UnitY;
        float range = (model.EventOf(new Party("player", "", [clip.Clip], cycle, "#1"))?.Range ?? AttackEvent.DefaultRange) * SourceSpace.MetresPerUnit;
        var start = eye + Vector3.UnitY * TraceRise;
        return (start, start + direction * range);
    }

    /// <summary>The move's arc at <paramref name="cycle"/> of its current hit window: side and up degrees against the view.</summary>
    (float Side, float Up) ArcAt(float cycle)
    {
        var clip = Sword.Clip;
        var window = clip.Windows[Math.Max(0, Sword.Window)];
        float fraction = window.Close > window.Open ? Math.Clamp((cycle - window.Open) / (window.Close - window.Open), 0, 1) : 0;
        return Tuning.ArcFor(clip.Clip)?.At(fraction) ?? (0, 0);
    }

    /// <summary>The widest gap between a step's rays: the donor's came 7° to 11° apart (Wire v17's sweeps).</summary>
    internal const float RayStepDegrees = 10;

    /// <summary>
    /// How far above the recorded eye the donor's traces begin: 9 units (7 to 10 in the fatality run's), metres. The
    /// client view's eye and the server's differ by about that.
    /// </summary>
    internal const float TraceRise = 9 * SourceSpace.MetresPerUnit;

    /// <summary>
    /// The Whirlwind's blow: its clip has no hit events, and the recorded one landed the tick it began (240 on the orc
    /// 55 units in front: HitPow 30). The lab strikes the orc within <see cref="WhirlwindReach"/> as it begins, on its body.
    /// </summary>
    void Whirl(ulong tick, Vector3 eye, Vector3 feet)
    {
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        // The Whirlwind asks its .qct slow motion as it begins (×0.5 for 2.5 s; recorded at its first tick).
        if (model.EventOf(new Party("player", "", [Sword.Clip.Clip], 0, "#1"))?.SlowTime is { } whirlSlow) AskSlowTime(whirlSlow);
        Vector3 flat = (Orc.Feet - feet) with { Y = 0 };
        if (!Orc.Alive || flat.Length() > WhirlwindReach)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, 0, eye, eye, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], 0, "#1", PlayerModel);
        // Its .qct event has critical_enable: it rolls (or, replayed, takes the donor's) as any blow does (R10146-1).
        float asked = Asked(tick, player, Orc.Party(), 0);
        Orc.Damage(asked);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, 0, eye, Orc.Feet + Vector3.UnitY, Orc.Feet + Vector3.UnitY, FormattableString.Invariant($"whirlwind {asked:0.##}"), 0, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, 0));
        Message = Orc.Alive ? FormattableString.Invariant($"Whirlwind for {asked:0.##}") : "The orc falls";
    }

    /// <summary>
    /// What a swing's blow asks, its critical decided: a move whose .qct event has <c>critical_enable</c> doubles on the
    /// lab's seeded roll, or in a replay on the donor's recorded critical (<see cref="RecordedCritical"/>), and counts it.
    /// </summary>
    float Asked(ulong tick, Party player, Party victim, int? group, string? weapon = null)
    {
        if (model.EventOf(player)?.CanCritical == true && (RecordedCritical?.Invoke(tick) ?? RollCritical()))
        {
            Criticals++;
            return PlayerAsks(player, victim, group, kick: false, Orc.Health, critical: true, weapon: weapon);
        }
        return PlayerAsks(player, victim, group, kick: false, Orc.Health, weapon: weapon);
    }

    /// <summary>
    /// The daggers' backstab is in reach (#10148): daggers in hand, the orc alive and <c>backstable</c>, its back turned
    /// to the player (its facing within 45° of the player's view: server.dll's <c>mm_player_backstab_cos</c>, 0.707; one
    /// at 44° didn't ready, one at 0° did, and three readied ones let go as it passed 51 to 111°) and in front of it,
    /// within the settings' <c>BackStabDist_IN</c> (100 units) to ready and <c>BackStabDist_OUT</c> (200) to stay ready,
    /// and neither on guard nor getting up. Over the daggers recordings, of the ticks with an orc in that reach, the donor
    /// readied on 463 of 1147 with it idling unaware, 76 of 80 turning, 58 of 170 attacking, 4 of 5 walking, 24 of 472
    /// getting up (<c>StandUp_*</c>), and none of 2262 with it on guard (<c>combatidle</c>).
    /// </summary>
    /// <param name="pose">In a replay, the followed orc's pose this step (the orc takes it after the sword steps).</param>
    bool BackstabReady(Vector3 forward, Vector3 feet, OrcPose? pose = null)
    {
        bool alive = pose is null ? Orc.Alive : pose.Health > 0;
        if (Sword.Clips.Backstab is null || !alive || !Orc.Backstable || Orc.State == OrcState.Backstabbed || Orc.Guarded(pose?.Sequence ?? Orc.Clip)) return false;
        Vector3 toOrc = ((pose?.Feet ?? Orc.Feet) - feet) with { Y = 0 }, look = forward with { Y = 0 }, facing = (pose?.Facing ?? Orc.Facing) with { Y = 0 };
        if (look.LengthSquared() < 1e-6f || facing.LengthSquared() < 1e-6f) return false;
        look = Vector3.Normalize(look);
        float units = toOrc.Length() / SourceSpace.MetresPerUnit;
        bool held = Sword.Move is SwordMove.BackstabIn or SwordMove.BackstabIdle or SwordMove.Backstab;
        var settings = model.Data.Settings;
        return units <= (held ? settings.BackStabDistOut : settings.BackStabDistIn) && Vector3.Dot(toOrc, look) > 0
            && Vector3.Dot(look, Vector3.Normalize(facing)) >= BackstabCos;
    }

    /// <summary>server.dll's <c>mm_player_backstab_cos</c> default: how square behind the victim the player must be.</summary>
    internal const float BackstabCos = .707f;

    /// <summary>
    /// The backstab's strike: at its 2102 (<see cref="BackstabClips.Kill"/>) the orc is left at 1 health and plays
    /// <c>die_backstabbed</c>; at <see cref="BackstabClips.Blow"/> the blow lands and kills it, as the recorded ones did.
    /// </summary>
    void Backstab(ulong tick, Vector3 eye, Vector3 forward, Vector3 feet)
    {
        float from = Sword.TraceFromCycle, to = Sword.Cycle;
        // The back lost by the time it strikes (the orc turned this very step): it strikes nothing (R10148-4).
        if (Sword.SwingStarted && !Orc.Following && !BackstabReady(forward, feet))
        {
            settledSwing = SwingKey;
            decidedSwing = Sword.Swing;
            Player = Player with { Missed = Player.Missed + 1 };
            return;
        }
        if (decidedSwing == Sword.Swing && Orc.State != OrcState.Backstabbed) return;
        // Its first contact asks nothing and leaves the victim at 1 health (the donor's, at the 2102, 4 of 4).
        if (from <= BackstabClips.Kill && to > BackstabClips.Kill || Sword.SwingStarted && to > BackstabClips.Kill)
        {
            Orc.Backstabbed();
            Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, 0, null));
        }
        if (!(from <= BackstabClips.Blow && to > BackstabClips.Blow) || decidedSwing == Sword.Swing) return;
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        if (Orc.State != OrcState.Backstabbed && !Orc.Following) { Player = Player with { Missed = Player.Missed + 1 }; return; }
        var player = new Party("player", "", [Sword.Clip.Clip], BackstabClips.Blow, "#1", PlayerModel);
        float asked = Asked(tick, player, Orc.Party(), null);
        Orc.Damage(asked);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, BackstabClips.Blow, eye, Orc.Feet + Vector3.UnitY, Orc.Feet + Vector3.UnitY, FormattableString.Invariant($"backstab {asked:0.##}"), 0, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = Orc.Alive ? FormattableString.Invariant($"Backstab for {asked:0.##}") : "Backstabbed";
    }

    /// <summary>How far the Whirlwind reaches, metres: authored (the one recorded blow struck at 1.4 m).</summary>
    internal const float WhirlwindReach = 2f;

    /// <summary>The swing and its hit window now: each window of a move that strikes in parts hits once.</summary>
    int SwingKey => Sword.Swing * 4 + Math.Max(0, Sword.Window);

    void Kick(ulong tick, Vector3 eye, Vector3 forward)
    {
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        Vector3 toOrc = Orc.Feet + Vector3.UnitY * 1.0f - eye;
        Vector3 flat = toOrc with { Y = 0 };
        bool reaches = Orc.Alive && flat.Length() <= Tuning.KickRange && Vector3.Dot(Vector3.Normalize(forward with { Y = 0 }), Vector3.Normalize(flat)) > .5f;
        Stamina.Kicked(reaches);
        if (!reaches)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, eye + forward * Tuning.KickRange, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        float asked = PlayerAsks(player, Orc.Party(), null, kick: true, Orc.Health);
        Orc.Damage(asked);
        Orc.Push(flat, Tuning.KickPush);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, Orc.Feet + Vector3.UnitY, Orc.Feet + Vector3.UnitY, FormattableString.Invariant($"kick {asked:0.##}"), Damage: asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = "Kick";
    }

    /// <summary>
    /// What the player's blow asks, as the lab asks it: the weapon's swing by the move's clip, or a kick, on a
    /// victim it takes as alert (its orc always is) and without a critical (the lab rolls none). The damage
    /// calibration asks the same of recorded blows.
    /// </summary>
    /// <param name="strength">The player's strength; null for the build's (<see cref="PlayerStrength"/>).</param>
    /// <param name="critical">The blow is a critical (×2): the lab's roll, or a recorded one.</param>
    /// <param name="weapon">The weapon's model; null for the one in hand.</param>
    internal float PlayerAsks(Party player, Party victim, int? group, bool kick, float victimHealth, float? strength = null, bool critical = false,
        string? weapon = null) => (kick
        ? model.Damage(new DamageCase(0, victim, player, "-", null, CombatModel.KickDamage, 0x20, 0, victimHealth, victimHealth, false, VictimAlert: 3))
        : model.Damage(new DamageCase(0, victim, player, weapon ?? FirstPerson.SwordModel, group, 0x4, 0, 0, victimHealth, victimHealth, false,
            WeaponModel: weapon ?? FirstPerson.SwordModel, VictimAlert: 3, AttackerCritical: critical, PlayerStrength: strength ?? PlayerStrength))) ?? 0;

    /// <summary>
    /// The player's critical chance, percent, on a blow whose move may critical: from the build's <c>critical_hit</c>
    /// level and the weapon's <c>WCritHit</c> (<see cref="CombatData.PlayerCriticalChance"/>; 2 for a new game, 12 at level 2).
    /// </summary>
    internal float CriticalChance => model.Data.PlayerCriticalChance(Build?.Level("critical_hit") ?? 0,
        model.Data.Weapon(FirstPerson.SwordModel)?.CriticalChance ?? 0);

    /// <summary>Slow motion asked for: from <see cref="SlowTime.Delay"/> on, the world runs at its factor for its game seconds.</summary>
    void AskSlowTime(SlowTime slow)
    {
        slowStart = fightSeconds + slow.Delay;
        slowEnd = slowStart + slow.GameSeconds;
        slowFactor = slow.Factor;
        SlowTimeRequests.Add((fightSeconds, slow));
    }

    /// <summary>What the lab's blows gave the gauge this step, and whether a Fatality landed (which empties it).</summary>
    internal float AdrenalineGained { get; private set; }
    internal bool FatalityLanded { get; private set; }

    /// <summary>The world's rate the fight asks for now: a slow motion's factor while it runs, else null.</summary>
    internal double? TimeScale => fightSeconds >= slowStart && fightSeconds < slowEnd ? slowFactor : null;

    /// <summary>Every slow motion the fight asked for, at its game time.</summary>
    internal List<(double At, SlowTime Slow)> SlowTimeRequests { get; } = [];

    /// <summary>One critical roll at <see cref="CriticalChance"/>, as the donor's <c>RandomFloat(0, 100) &lt; chance</c>.</summary>
    internal bool RollCritical() => criticals.NextSingle() * 100 < CriticalChance;

    /// <summary>Criticals the lab's blows have landed since the fight began.</summary>
    internal int Criticals { get; private set; }

    /// <summary>In a replay, whether the donor's blow near a tick was a critical: taken instead of the roll.</summary>
    internal Func<ulong, bool>? RecordedCritical { get; set; }

    /// <summary>The player's strength, from the build's <c>extra_damage</c> level (<see cref="CombatData.PlayerStrength"/>).</summary>
    internal float PlayerStrength => model.Data.PlayerStrength(Build?.Level("extra_damage") ?? 0);

    void OrcStrikes(ulong tick, OrcBlow blow, Vector3 feet, Vector3 forward)
    {
        OrcStats = OrcStats with { Blows = OrcStats.Blows + 1 };
        Vector3 toPlayer = (feet - Orc.Feet) with { Y = 0 };
        // The donor's reach test, in Source units on its axes (lab X is Source Y, lab Z Source X).
        static Vector2 Source(Vector3 v) => new Vector2(v.Z, v.X) / SourceSpace.MetresPerUnit;
        var reach = model.Reach(new ReachCase((int)tick, "npc_orc_sword", Npc.Orc.Model, blow.Attack, blow.Part, "player",
            Source(toPlayer), Vector2.Normalize(Source(Orc.Facing)), Reaches.PlayerHalfWidth, false));
        LastOrcReach = reach;
        if (!reach.Lands) return;
        OrcStats = OrcStats with { Reached = OrcStats.Reached + 1 };
        var orc = Orc.Party() with { Cycle = blow.Cycle };
        bool broken = false;
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        float asked = model.Damage(new DamageCase((int)tick, player, orc, "weapon_arxorccleaver", null, 0x4, 0, 0, PlayerHealth, PlayerHealth, false,
            blow.Part, Npc.Orc.Cleaver)) ?? 0;
        if (Blocking)
        {
            Vector3 look = forward with { Y = 0 }, toOrc = (Orc.Feet - feet) with { Y = 0 };
            float front = look.LengthSquared() > 1e-6f && toOrc.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(look), Vector3.Normalize(toOrc)) : 1;
            var outcome = model.PlayerBlock(new ParryCase((int)tick, orc, DefenseResult.PlayerParried, blow.Part), front, Shield!.ParryCos);
            if (outcome == DefenseResult.PlayerParried)
            {
                // The shield takes the blow's damage (l01_a-v17-shield: the player none).
                ShieldHealth -= asked;
                OrcStats = OrcStats with { Parried = OrcStats.Parried + 1 };
                Message = "Blocked";
                Log?.Add(new CombatEvent(tick, false, blow.Attack, DefenseResult.PlayerParried, asked, null));
                if (ShieldHealth <= 0)
                {
                    // Not recorded: no shield was worn through (the recorded one lost 136 of its 240).
                    Wear(null);
                    Message = "The shield breaks";
                }
                return;
            }
            if (outcome == DefenseResult.PlayerParryBroken)
            {
                Sword.Break();
                broken = true;
                OrcStats = OrcStats with { Broken = OrcStats.Broken + 1 };
            }
        }
        else if (Sword.Parrying)
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
    /// <summary>What to draw: the arms and weapon (none while the hands are taken), and the orc.</summary>
    internal IEnumerable<AppearanceFact> Facts() => Sword.Move == SwordMove.Away ? Orc.Facts() : FirstPerson.Facts().Concat(Orc.Facts());

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
        // The orc's reach: its attacks' range plus the player's box and the sweep's half-size, here its first attack's.
        if (Orc.Alive && model.Data.Character(Npc.Orc.Model)?.Attack("attack_Right")?.Events[0].Range is float range)
            items.Add(OverlayItem.WireSphere(OverlayPlace.World, Orc.Feet + Vector3.UnitY,
                (range + Reaches.PlayerHalfWidth + CombatModel.SweepHalfSize) * SourceSpace.MetresPerUnit, new Color(1, .55f, .3f, .5f), onTop: false));
        engine.Graphics.PublishOverlaySet(new OverlaySetRequest(Set, items.ToArray(), []));
    }

    internal void Reset()
    {
        Sword.Reset();
        Stamina.Reset();
        Adrenaline.Reset();
        criticals = new Random(CriticalSeed);
        Criticals = 0;
        fightSeconds = 0;
        slowStart = slowEnd = double.NaN;
        SlowTimeRequests.Clear();
        Orc.Spawn();
        PlayerHealth = Tuning.PlayerHealth;
        recent.Clear();
        Player = default;
        OrcStats = default;
        LastBlow = null;
        Message = "";
        settledSwing = decidedSwing = -1;
    }

    public void Dispose()
    {
        FirstPerson.Dispose();
        Orc.Dispose();
        session.Dispose();
    }
}
