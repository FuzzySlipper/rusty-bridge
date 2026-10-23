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
    internal const string PlayerModel = "models/npc/player/player_basic.mdl";
    /// <summary>
    /// The trace's radius, metres: the player's sweep is a box of half-size <c>collision_inc_size_ray</c>, 1.5 units (its
    /// type 0; docs/wire.md, "Melee sweeps"), swept as a sphere of that radius.
    /// </summary>
    const float Radius = 1.5f * SourceSpace.MetresPerUnit;
    const double ShowSeconds = 1.2;

    readonly IEngineContext engine;
    readonly DonorContent donor;
    readonly StaminaRules staminaRules;
    readonly HealthRules healthRules;
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
        hitOrc = Orc;
        this.donor = donor;
        Mates(tuning.Orc);
        staminaRules = StaminaRules.From(donor);
        Stamina = new PlayerStamina(staminaRules);
        healthRules = HealthRules.From(donor);
        Health = new PlayerHealth(healthRules);
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
        Health.Retune(healthRules.For(build));
        if (build.Belt is { } belt) Belt.Fill(donor, belt);
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
    /// <summary>
    /// A blocked blow's damage off the shield's health; at none it breaks and comes off the arm (not recorded: no shield
    /// was worn through, the recorded one lost 136 of its 240). True when it broke. <see cref="Reset"/> puts it back whole.
    /// </summary>
    internal bool WearShield(float damage)
    {
        if (Shield is null) return false;
        ShieldHealth -= damage;
        if (ShieldHealth > 0) return false;
        ShieldHealth = 0;
        Wear(null);
        return true;
    }

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
    /// <summary>The donor content the fight reads.</summary>
    internal DonorContent Donor => donor;
    /// <summary>The player's health (#10162): its maximum and regeneration from the build.</summary>
    internal PlayerHealth Health { get; }
    /// <summary>What the belt holds, used by its keys (#10162).</summary>
    internal PlayerBelt Belt { get; } = new();
    internal float PlayerHealth => Health.Value;

    /// <summary>
    /// Uses belt slot <paramref name="slot"/> (0 to 8; the keys 1 to 9, the donor's <c>usebelt1</c>–<c>9</c>): its item's
    /// health at once. Dead, nothing is used.
    /// </summary>
    internal void UseBelt(int slot)
    {
        if (PlayerHealth <= 0) return;
        if (Belt.Use(slot, Health) is { } item) Message = FormattableString.Invariant($"{item.Name}: health {PlayerHealth:0.#} of {Health.Max:0}");
    }
    internal bool God { get; set; }
    /// <summary>The orc's blows kick the player's view (#10161); a replay kicks it with the donor's blows instead.</summary>
    internal bool BlowsPunch { get; set; } = true;

    /// <summary>
    /// How far the player is below where they last left the ground, Source units (0 on it): a jump attack knocks an
    /// orc down only after 50 (<c>PlayerVerticalAttackHeightToMakeNPCFall</c>, R10152-1).
    /// </summary>
    internal float Drop { get; private set; }

    float groundHeight = float.NaN;
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

    /// <summary>
    /// The first orc's squad-mate (R10154-1; <c>combat.json</c> <c>orc.squadMate</c>), or none: an orc that sees the
    /// player calls the other if it stands within <c>SquadAwarnessDist</c> (500 units), which comes to fight 2.4 s later.
    /// </summary>
    internal Orc? Mate { get; private set; }

    /// <summary>The orcs in the fight: the first, and its squad-mate if there is one.</summary>
    IEnumerable<Orc> Orcs => Mate is { } mate ? [Orc, mate] : [Orc];

    // The orc the player's blow being decided lands on (R10154-3): the one its sweep met first, the nearest a kick or the
    // Whirlwind reaches, the one whose back the backstab readied on. The first orc in a replay.
    Orc hitOrc;
    Orc? backstabVictim;

    /// <summary>The orc the player's last blow was decided against.</summary>
    internal Orc Victim => hitOrc;

    /// <summary>The squad's calls: whichever orc saw the player this step calls the other, within reach.</summary>
    void Squad()
    {
        if (Mate is null) return;
        float apart = Vector3.Distance(Orc.Feet with { Y = 0 }, Mate.Feet with { Y = 0 }) / SourceSpace.MetresPerUnit;
        if (apart > Orc.Perception.Data.SquadDistance) return;
        if (Orc.Spotted && Mate.Alive) Mate.Call();
        if (Mate.Spotted && Orc.Alive) Orc.Call();
    }

    internal void Retune(CombatTuning next)
    {
        Tuning = next;
        Sword.Retune(FirstPerson.Clips(Weapon, next));
        Orc.Retune(next.Orc);
        Mates(next.Orc);
    }

    /// <summary>The squad-mate as the tuning asks: made, retuned to stand at its place (from its next spawn), or gone.</summary>
    void Mates(OrcTuning orc)
    {
        if (orc.SquadMate is not { } at) { Mate?.Dispose(); Mate = null; return; }
        if (Mate is null) { Mate = new Orc(engine, donor, orc with { Spawn = at }, slot: 1); return; }
        Mate.Retune(orc with { Spawn = at });
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
        // Where the player left the ground, for a jump attack's knockdown (R10152-1).
        if (grounded) groundHeight = feet.Y;
        Drop = grounded || float.IsNaN(groundHeight) ? 0 : MathF.Max(0, (groundHeight - feet.Y) / SourceSpace.MetresPerUnit);
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
        // The body's velocity for the head's fall (#10161).
        var bodyVelocity = poseFeet is { } was && seconds > 0 ? (feet - was) / seconds : Vector3.Zero;
        poseFeet = feet;
        FirstPerson.Pose(Sword, seconds, eye, forward, !grounded, bodyVelocity);
        var gait = Gait(seconds, feet, sprinting, crouched);
        var blows = orcPose is null ? Orc.Step(seconds, feet, Incoming(), Sword.Swing, gait) : Orc.Follow(orcPose, seconds);
        // Its squad-mate fights on its own, sees the player's swings as the first does, and takes the blows that reach it.
        var mateBlows = Mate is { } mate && orcPose is null ? mate.Step(seconds, feet, Incoming(), Sword.Swing, gait) : [];
        Squad();
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
        foreach (var blow in blows) OrcStrikes(tick, blow, feet, forward, Orc);
        foreach (var blow in mateBlows) OrcStrikes(tick, blow, feet, forward, Mate!);
        Health.Step(seconds);
        if (PlayerHealth <= 0 && (deadSeconds += seconds) > 2) { Health.Fill(); deadSeconds = 0; Message = "Back on your feet"; }
    }

    void Swing(ulong tick, Vector3 eye, Vector3 forward, Vector3 feet)
    {
        // The donor sweeps several rays a tick, at most about 10° apart, across the arc the swing covered (#10179):
        // from where the last step left it to where it is now, the first contact ending the swing for its target.
        // Each orc's body and items, the nearest contact along a ray striking (R10154-3).
        var posed = Orcs.Select(o => (Orc: o, Targets: o.Targets())).Where(o => o.Targets.Count > 0).ToList();
        var targets = posed.FirstOrDefault().Targets ?? [];
        float from = Sword.TraceFromCycle, to = Sword.TraceCycle;
        var (fromSide, _) = ArcAt(from);
        var (toSide, _) = ArcAt(to);
        int rays = Math.Clamp((int)MathF.Ceiling(MathF.Abs(toSide - fromSide) / RayStepDegrees), 1, 12);
        Vector3 start = default, end = default;
        List<HitContact> contacts = [];
        for (int r = 1; r <= rays && contacts.Count == 0; r++)
        {
            (start, end) = Aim(eye, forward, from + (to - from) * r / rays);
            foreach (var (o, t) in posed)
                if (sweep.Sweep(start, end, Radius, t) is { Count: > 0 } met && (contacts.Count == 0 || met[0].Along < contacts[0].Along))
                    (contacts, targets, hitOrc) = (met, t, o);
        }
        if (contacts.Count == 0) { Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, start, end, null, "none")); return; }
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        var c = contacts[0];
        var orc = hitOrc.Party();
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        Struck struck = c.Target == 0 ? Struck.Body : c.Target == 1 ? Struck.Weapon : Struck.OffHand;
        Vector3 toPlayer = feet - hitOrc.Feet;
        toPlayer.Y = 0;
        float front = toPlayer.LengthSquared() > 1e-6f ? Vector3.Dot(hitOrc.Facing, Vector3.Normalize(toPlayer)) : 0;
        // A dodging orc can't be touched in its dodge's window (#10198): the donor's no-effect verdict.
        bool untouchable = model.Data.Untouchable(orc.Model, orc.Sequence, orc.Cycle);
        var verdict = CombatModel.Verdict(new DefenseCase((int)tick, orc, player, "weapon_arx_short_sword", struck, front, DefenseResult.Hit, hitOrc.DefenseState, untouchable));
        bool bash = Sword.Move == SwordMove.Bash;
        // The bash's event has breakparade: it goes through a parry. Not recorded (no orc parried one).
        if (bash && verdict == DefenseResult.Parry && model.EventOf(player)?.BreaksParry == true) verdict = DefenseResult.Hit;
        string what = struck == Struck.Body ? $"hit group {targets[0].Model.Hitboxes[c.Hitbox].Group}" : struck == Struck.Weapon ? "its cleaver" : "its shield";
        // The swing plays on: the donor's sword doesn't bounce off an orc's parry or shield (none of 23 recorded did).
        if (verdict is DefenseResult.Parry or DefenseResult.Shield or DefenseResult.NoEffect)
        {
            Player = verdict == DefenseResult.Parry ? Player with { Parried = Player.Parried + 1 }
                : verdict == DefenseResult.Shield ? Player with { Blocked = Player.Blocked + 1 } : Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, $"{verdict} ({what})"), blow: true);
            Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, 0, null));
            Message = verdict == DefenseResult.Parry ? "The orc parries" : verdict == DefenseResult.Shield ? "The orc blocks" : "The orc dodges";
            return;
        }
        int? group = struck == Struck.Body ? targets[0].Model.Hitboxes[c.Hitbox].Group : null;
        float asked = Asked(tick, player, orc, group, bash ? SwordClips.BashWeapon : null);
        // The blow's .qct event gives adrenaline; the Fatality spends it, and a head blow of a cutting move kills.
        var hit = model.EventOf(player);
        Adrenaline.Gain(hit?.Adrenaline ?? 0);
        AdrenalineGained += hit?.Adrenaline ?? 0;
        if (Sword.Move == SwordMove.Fatality) { Adrenaline.Spend(); FatalityLanded = true; }
        // A head blow asks what it asks, the Fatality's too (720 on the head): cutheadflag cuts the head off only a
        // blow that kills (the one recorded, an orc at 40 health, left npc_orc_noHead), and no head blow killed by
        // itself (#10198: the 100000-health orcs #10177 saw fall were knocked down and got up).
        bool wasAlive = hitOrc.Alive;
        hitOrc.Damage(asked);
        // A Fatality that kills slows the world, as the donor's every Fatality kill did (#10147).
        if (Sword.Move == SwordMove.Fatality && wasAlive && !hitOrc.Alive) AskSlowTime(SlowTime.FatalityKill);
        // Its reaction (#10153): the bash's push slides it its measured 1.4 m.
        React(hit, kick: false, asked, group, forward, (hitOrc.Feet - feet) with { Y = 0 }, bash ? BashPush : 0);
        // It may stun or knock the orc down (#10152): the shield's bash is no staff's and multiplies by 1.
        Stagger(hit, feet, bash);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, player.Layers[0], player.Cycle, start, end, c.Point, FormattableString.Invariant($"{asked:0.##} ({what})"), group, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, player.Layers[0], verdict, asked, group));
        Message = !hitOrc.Alive ? "The orc falls" : bash ? "Bash" : FormattableString.Invariant($"Hit for {asked:0.##}");
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
        hitOrc = Orcs.Where(o => o.Alive).OrderBy(o => ((o.Feet - feet) with { Y = 0 }).Length()).FirstOrDefault() ?? Orc;
        Vector3 flat = (hitOrc.Feet - feet) with { Y = 0 };
        if (!hitOrc.Alive || flat.Length() > WhirlwindReach)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, 0, eye, eye, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], 0, "#1", PlayerModel);
        // Its .qct event has critical_enable: it rolls (or, replayed, takes the donor's) as any blow does (R10146-1).
        float asked = Asked(tick, player, hitOrc.Party(), 0);
        hitOrc.Damage(asked);
        React(model.EventOf(player), kick: false, asked, 0, flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : hitOrc.Facing * -1, flat, 0);
        Stagger(model.EventOf(player), feet);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, 0, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, FormattableString.Invariant($"whirlwind {asked:0.##}"), 0, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, 0));
        Message = hitOrc.Alive ? FormattableString.Invariant($"Whirlwind for {asked:0.##}") : "The orc falls";
    }

    bool lastCritical;

    /// <summary>
    /// A player's blow's reaction on the orc (#10153; <see cref="OrcReactions"/>), from its event, its damage, whether
    /// it was a critical, its hit group and the player's facing in the orc's frame.
    /// </summary>
    void React(AttackEvent? blow, bool kick, float damage, int? group, Vector3 forward, Vector3 pushDirection, float pushMetres)
    {
        var facing = hitOrc.Facing with { Y = 0 };
        var left = new Vector3(facing.Z, 0, -facing.X);
        var look = forward with { Y = 0 };
        float angle = look.LengthSquared() > 1e-6f && facing.LengthSquared() > 1e-6f
            ? MathF.Atan2(Vector3.Dot(look, left), Vector3.Dot(look, facing)) * 180 / MathF.PI : 180;
        var reaction = OrcReactions.Pick(new ReactionBlow(blow, kick, damage, !kick && lastCritical, group, angle),
            hitOrc.State == OrcState.Stunned, hitOrc.SinceSmallHit);
        if (reaction.Clip is not null) hitOrc.React(reaction, pushDirection, pushMetres);
    }

    /// <summary>
    /// What a swing's blow asks, its critical decided: a move whose .qct event has <c>critical_enable</c> doubles on the
    /// lab's seeded roll, or in a replay on the donor's recorded critical (<see cref="RecordedCritical"/>), and counts it.
    /// </summary>
    float Asked(ulong tick, Party player, Party victim, int? group, string? weapon = null)
    {
        lastCritical = false;
        if (model.EventOf(player)?.CanCritical == true && (RecordedCritical?.Invoke(tick) ?? RollCritical()))
        {
            Criticals++;
            lastCritical = true;
            return PlayerAsks(player, victim, group, kick: false, hitOrc.Health, critical: true, weapon: weapon, alert: AlertAt(tick));
        }
        return PlayerAsks(player, victim, group, kick: false, hitOrc.Health, weapon: weapon, alert: AlertAt(tick));
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
        // A replay's followed orc, else either orc (R10154-3): the first whose back is in reach.
        backstabVictim = pose is not null || Orc.Following ? (BackstabReady(Orc, forward, feet, pose) ? Orc : null)
            : Orcs.FirstOrDefault(o => BackstabReady(o, forward, feet, null));
        return backstabVictim is not null;
    }

    bool BackstabReady(Orc orc, Vector3 forward, Vector3 feet, OrcPose? pose)
    {
        bool alive = pose is null ? orc.Alive : pose.Health > 0;
        if (Sword.Clips.Backstab is null || !alive || !orc.Backstable || orc.State == OrcState.Backstabbed || Npc.Orc.Guarded(pose?.Sequence ?? orc.Clip)) return false;
        Vector3 toOrc = ((pose?.Feet ?? orc.Feet) - feet) with { Y = 0 }, look = forward with { Y = 0 }, facing = (pose?.Facing ?? orc.Facing) with { Y = 0 };
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
        if (Sword.SwingStarted) hitOrc = backstabVictim ?? Orc;
        // The back lost by the time it strikes (the orc turned this very step): it strikes nothing (R10148-4).
        if (Sword.SwingStarted && !hitOrc.Following && !BackstabReady(forward, feet))
        {
            settledSwing = SwingKey;
            decidedSwing = Sword.Swing;
            Player = Player with { Missed = Player.Missed + 1 };
            return;
        }
        if (decidedSwing == Sword.Swing && hitOrc.State != OrcState.Backstabbed) return;
        // Its first contact asks nothing and leaves the victim at 1 health (the donor's, at the 2102, 4 of 4).
        if (from <= BackstabClips.Kill && to > BackstabClips.Kill || Sword.SwingStarted && to > BackstabClips.Kill)
        {
            hitOrc.Backstabbed();
            Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, 0, null));
        }
        if (!(from <= BackstabClips.Blow && to > BackstabClips.Blow) || decidedSwing == Sword.Swing) return;
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        if (hitOrc.State != OrcState.Backstabbed && !hitOrc.Following) { Player = Player with { Missed = Player.Missed + 1 }; return; }
        var player = new Party("player", "", [Sword.Clip.Clip], BackstabClips.Blow, "#1", PlayerModel);
        float asked = Asked(tick, player, hitOrc.Party(), null);
        hitOrc.Damage(asked);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, BackstabClips.Blow, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, FormattableString.Invariant($"backstab {asked:0.##}"), 0, asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = hitOrc.Alive ? FormattableString.Invariant($"Backstab for {asked:0.##}") : "Backstabbed";
    }

    /// <summary>
    /// The swing coming as the orc sees it (#10151): a kick, one that breaks parries (its first event's
    /// <c>breakparade</c>: the power attacks), or one it may parry, from its start until its hit window closes
    /// (R10151-1: the donor's orcs parried swings that began while they attacked, once their attack was over).
    /// </summary>
    IncomingSwing Incoming() =>
        !(Sword.Move is SwordMove.Fast or SwordMove.Power or SwordMove.Kick) || !(Sword.SwingStarted || Sword.Cycle < Sword.Clip.Close) ? IncomingSwing.None
        : Sword.Move == SwordMove.Kick ? IncomingSwing.Kick
        : model.EventOf(new Party("player", "", [Sword.Clip.Clip], 0, "#1", PlayerModel))?.BreaksParry == true ? IncomingSwing.BreaksParry
        : IncomingSwing.Parryable;

    /// <summary>A blow's stun or knockdown on the orc (#10152), told in the message.</summary>
    void Stagger(AttackEvent? blow, Vector3 from, bool shield = false)
    {
        var weapon = Weapon;
        var outcome = hitOrc.Struck(blow, fightSeconds, from, !shield && weapon.Class == WeaponClass.Staff, shield ? 1 : weapon.StunMultiplier,
            Sword.Move == SwordMove.LeapOut ? Drop : null);
        if (outcome == Npc.Stagger.KnockedDown) Message = "Knocked down";
        else if (outcome == Npc.Stagger.Stunned) Message = "Stunned";
    }

    /// <summary>How far the Whirlwind reaches, metres: authored (the one recorded blow struck at 1.4 m).</summary>
    internal const float WhirlwindReach = 2f;

    /// <summary>The swing and its hit window now: each window of a move that strikes in parts hits once.</summary>
    int SwingKey => Sword.Swing * 4 + Math.Max(0, Sword.Window);

    void Kick(ulong tick, Vector3 eye, Vector3 forward)
    {
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        bool Reaches(Orc o)
        {
            var at = (o.Feet - eye) with { Y = 0 };
            return o.Alive && at.Length() <= Tuning.KickRange && Vector3.Dot(Vector3.Normalize(forward with { Y = 0 }), Vector3.Normalize(at)) > .5f;
        }
        hitOrc = Orcs.Where(Reaches).OrderBy(o => ((o.Feet - eye) with { Y = 0 }).Length()).FirstOrDefault() ?? Orc;
        Vector3 flat = (hitOrc.Feet - eye) with { Y = 0 };
        bool reaches = Reaches(hitOrc);
        Stamina.Kicked(reaches);
        if (!reaches)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, eye + forward * Tuning.KickRange, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        // An orc in its dodge's window can't be touched, by a kick as by a swing (R10151-3): the donor's no-effect.
        var orc = hitOrc.Party();
        if (model.Data.Untouchable(orc.Model, orc.Sequence, orc.Cycle))
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, "NoEffect (kick)"), blow: true);
            Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.NoEffect, 0, null));
            Message = "The orc dodges";
            return;
        }
        float asked = PlayerAsks(player, orc, null, kick: true, hitOrc.Health, alert: AlertAt(tick));
        hitOrc.Damage(asked);
        React(null, kick: true, asked, null, forward, flat, Tuning.KickPush);
        // A kick counts toward a stun's hits but carries no chance of its own: the recorded kicks felled 1 orc of 48.
        Stagger(null, eye with { Y = hitOrc.Feet.Y });
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, FormattableString.Invariant($"kick {asked:0.##}"), Damage: asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = "Kick";
    }

    /// <summary>
    /// What the player's blow asks, as the lab asks it: the weapon's swing by the move's clip, or a kick, on a
    /// victim it takes as alert (its own orc always is; a replay's as the donor's was, <see cref="RecordedAlert"/>) and
    /// without a critical unless one is passed. The damage calibration asks the same of recorded blows.
    /// </summary>
    /// <param name="alert">The victim's alert level and NPC state, as the donor's damage record streams them (v11).</param>
    /// <param name="strength">The player's strength; null for the build's (<see cref="PlayerStrength"/>).</param>
    /// <param name="critical">The blow is a critical (×2): the lab's roll, or a recorded one.</param>
    /// <param name="weapon">The weapon's model; null for the one in hand.</param>
    internal float PlayerAsks(Party player, Party victim, int? group, bool kick, float victimHealth, float? strength = null, bool critical = false,
        string? weapon = null, (int Level, int NpcState)? alert = null) => (kick
        ? model.Damage(new DamageCase(0, victim, player, "-", null, CombatModel.KickDamage, 0x20, 0, victimHealth, victimHealth, false,
            VictimAlert: alert?.Level ?? hitOrc.Alert.Level, VictimNpcState: alert?.NpcState ?? hitOrc.Alert.NpcState))
        : model.Damage(new DamageCase(0, victim, player, weapon ?? FirstPerson.SwordModel, group, 0x4, 0, 0, victimHealth, victimHealth, false,
            WeaponModel: weapon ?? FirstPerson.SwordModel, VictimAlert: alert?.Level ?? hitOrc.Alert.Level, VictimNpcState: alert?.NpcState ?? hitOrc.Alert.NpcState,
            AttackerCritical: critical, PlayerStrength: strength ?? PlayerStrength))) ?? 0;

    Vector3? lastFeet, poseFeet;

    /// <summary>
    /// How the player moves this step, as an orc hears it (#10154): still, crouched, walking (under 2.5 m/s on the
    /// ground) or running (the donor's run, 180 units a second, is 3.4 m/s; a sprint is a run to the orc's ears).
    /// </summary>
    PlayerGait Gait(float seconds, Vector3 feet, bool sprinting, bool crouched)
    {
        float speed = lastFeet is { } was && seconds > 0 ? ((feet - was) with { Y = 0 }).Length() / seconds : 0;
        lastFeet = feet;
        return OrcPerception.GaitOf(speed, crouched, sprinting);
    }

    /// <summary>
    /// In a replay, the alert level and NPC state the donor's blow near a tick found its victim at (#10198): an orc
    /// not yet alert takes the not-alerted ×3, as the fatality run's first power strikes did. Null outside replays.
    /// </summary>
    internal Func<ulong, (int Level, int NpcState)?>? RecordedAlert { get; set; }

    (int, int)? AlertAt(ulong tick) => RecordedAlert?.Invoke(tick);

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

    void OrcStrikes(ulong tick, OrcBlow blow, Vector3 feet, Vector3 forward, Orc striker)
    {
        OrcStats = OrcStats with { Blows = OrcStats.Blows + 1 };
        Vector3 toPlayer = (feet - striker.Feet) with { Y = 0 };
        // The donor's reach test, in Source units on its axes (lab X is Source Y, lab Z Source X).
        static Vector2 Source(Vector3 v) => new Vector2(v.Z, v.X) / SourceSpace.MetresPerUnit;
        var reach = model.Reach(new ReachCase((int)tick, "npc_orc_sword", Npc.Orc.Model, blow.Attack, blow.Part, "player",
            Source(toPlayer), Vector2.Normalize(Source(striker.Facing)), Reaches.PlayerHalfWidth, false));
        LastOrcReach = reach;
        if (!reach.Lands) return;
        striker.Landed(blow);
        OrcStats = OrcStats with { Reached = OrcStats.Reached + 1 };
        var orc = striker.Party() with { Cycle = blow.Cycle };
        bool broken = false;
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        float asked = model.Damage(new DamageCase((int)tick, player, orc, "weapon_arxorccleaver", null, 0x4, 0, 0, PlayerHealth, PlayerHealth, false,
            blow.Part, Npc.Orc.Cleaver)) ?? 0;
        if (Blocking)
        {
            Vector3 look = forward with { Y = 0 }, toOrc = (striker.Feet - feet) with { Y = 0 };
            float front = look.LengthSquared() > 1e-6f && toOrc.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(look), Vector3.Normalize(toOrc)) : 1;
            var outcome = model.PlayerBlock(new ParryCase((int)tick, orc, DefenseResult.PlayerParried, blow.Part), front, Shield!.ParryCos);
            if (outcome == DefenseResult.PlayerParried)
            {
                // The shield takes the blow's damage (l01_a-v17-shield: the player none).
                OrcStats = OrcStats with { Parried = OrcStats.Parried + 1 };
                Log?.Add(new CombatEvent(tick, false, blow.Attack, DefenseResult.PlayerParried, asked, null));
                Message = WearShield(asked) ? "The shield breaks" : "Blocked";
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
        // The blow kicks the player's view (#10161).
        if (BlowsPunch) FirstPerson.Head.Punch(HeadMotion.PunchOf(blow.Attack, reach.Part?.HitPow ?? 1));
        if (!God)
        {
            if (Health.Take(asked)) { deadSeconds = 0; OrcStats = OrcStats with { Deaths = OrcStats.Deaths + 1 }; Message = "You fall"; return; }
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
    internal IEnumerable<AppearanceFact> Facts() => (Sword.Move == SwordMove.Away ? Orc.Facts() : FirstPerson.Facts().Concat(Orc.Facts())).Concat(Mate?.Facts() ?? []);

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
        // The build's shield back on the arm at its full health, worn or broken as the last fight left it (R10149-1).
        Wear(Build is { } build ? ShieldOf(build) : null);
        ShieldHealth = Shield?.Health ?? 0;
        Sword.Reset();
        Stamina.Reset();
        Adrenaline.Reset();
        criticals = new Random(CriticalSeed);
        Criticals = 0;
        fightSeconds = 0;
        slowStart = slowEnd = double.NaN;
        SlowTimeRequests.Clear();
        Orc.Reseed();
        Orc.Spawn();
        Mate?.Reseed();
        Mate?.Spawn();
        Health.Fill();
        if (Build?.Belt is { } belt) Belt.Fill(donor, belt);
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
        Mate?.Dispose();
        session.Dispose();
    }
}
