using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Magic;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Ranged;
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
/// <item>a kick reaches an orc in front within <see cref="KickReach"/>, asks its damage and pushes it as its push clip
/// moves it (#10157);</item>
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
    readonly ManaRules manaRules;
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
        Orc = new Orc(engine, donor, tuning.Orc, FirstSlot(NpcKind.For(tuning.Orc)), NpcKind.For(tuning.Orc));
        hitOrc = Orc;
        this.donor = donor;
        Mates(tuning.Orc);
        staminaRules = StaminaRules.From(donor);
        Stamina = new PlayerStamina(staminaRules);
        healthRules = HealthRules.From(donor);
        Health = new PlayerHealth(healthRules);
        manaRules = ManaRules.From(donor);
        Mana = new PlayerMana(manaRules);
        Telekinesis = TelekinesisRules.From(donor);
        Fireball = FireballRules.From(donor);
        Lightning = LightningRules.From(donor);
        Freeze = FreezeRules.From(donor);
        Inferno = InfernoRules.From(donor);
        Cure = CureRules.From(donor);
        Weaken = WeakenRules.From(donor);
        Shrink = ShrinkRules.From(donor);
        Charm = CharmRules.From(donor);
        Reflection = ReflectionRules.From(donor);
        WizardEye = new WizardEye(WizardEyeRules.From(donor));
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
        Mana.Retune(manaRules.For(build));
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
    internal Orc Orc { get; private set; }
    // The first NPC of each kind made so far (#10357), the one in play among them.
    readonly Dictionary<NpcKind, Orc> firsts = [];
    // Each kind's first NPC in its own slot of appearance ids: the orc's 0, the necromancer's 2 (the squad-mate has 1).
    static int FirstSlot(NpcKind kind) => kind.Caster ? 2 : 0;
    /// <summary>The donor content the fight reads.</summary>
    internal DonorContent Donor => donor;
    /// <summary>The player's health (#10162): its maximum and regeneration from the build.</summary>
    internal PlayerHealth Health { get; }
    /// <summary>The player's mana (#10164): the build's maximum, its regeneration, what telekinesis spends.</summary>
    internal PlayerMana Mana { get; }
    /// <summary>The fireball's numbers (#10165).</summary>
    internal FireballRules Fireball { get; }
    /// <summary>The spell in hand (its <c>spells.txt</c> id), or null for the weapon (#10165): the belt puts it there.</summary>
    internal string? SpellInHand { get; private set; }
    bool spellAttackHeld;
    readonly List<LabFireball> fireballs = [];
    /// <summary>The fireballs in the air or forming, for drawing.</summary>
    internal IReadOnlyList<LabFireball> Fireballs => fireballs;
    /// <summary>Casts so far, and fireballs that went off.</summary>
    internal (int Casts, int Explosions, int Refused) Spells { get; private set; }
    /// <summary>
    /// The world's geometry for a fireball's flight: the first point on the segment from the first point to the second
    /// that meets the arena, or null (the product sets it to its scene's ray cast; none, and only the orc stops one).
    /// </summary>
    internal Func<Vector3, Vector3, Vector3?>? WorldRay { get; set; }

    /// <summary>
    /// The world's geometry for an arrow (#10168): <see cref="WorldRay"/>'s point and the surface's normal there, which
    /// a bounce reflects off. Unset, the arrows take <see cref="WorldRay"/>'s point facing back along their way.
    /// </summary>
    internal Func<Vector3, Vector3, (Vector3 Point, Vector3 Normal)?>? WorldSurface { get; set; }

    /// <summary>The attack spells' numbers (#10166).</summary>
    internal LightningRules Lightning { get; }
    internal FreezeRules Freeze { get; }
    internal InfernoRules Inferno { get; }
    /// <summary>The support spells' numbers (#10167).</summary>
    internal CureRules Cure { get; }
    internal WeakenRules Weaken { get; }
    /// <summary>Weaken with the gauge full (#10348).</summary>
    internal ShrinkRules Shrink { get; }
    internal CharmRules Charm { get; }
    /// <summary>Magic reflection's numbers (#10349).</summary>
    internal ReflectionRules Reflection { get; }
    /// <summary>The player's wizard eye (#10358): on, its seconds and its fade.</summary>
    internal WizardEye WizardEye { get; }

    /// <summary>The spells the lab plays: <c>spells.txt</c>'s id, and its name for the HUD.</summary>
    static readonly Dictionary<string, string> Played = new(StringComparer.OrdinalIgnoreCase)
    {
        [FireballRules.Spell] = "Fireball", [LightningRules.Spell] = "Lightning bolt", [FreezeRules.Spell] = "Freeze", [InfernoRules.Spell] = "Inferno",
        [CureRules.Spell] = "Cure wounds", [WeakenRules.Spell] = "Weaken", [CharmRules.Spell] = "Charm",
        [ReflectionRules.Spell] = "Magic reflection", [WizardEyeRules.Spell] = "Wizard eye",
    };

    /// <summary>Puts <paramref name="spell"/> in hand, or back to the weapon if it's there already (a belt key, #10165).</summary>
    internal void ToggleSpell(string spell)
    {
        if (SpellInHand is { } inHand && inHand.Equals(spell, StringComparison.OrdinalIgnoreCase)) { SpellInHand = null; EndSpell(); Message = "Weapon in hand"; return; }
        if (Build is { } build && !build.Spells.Contains(spell, StringComparer.OrdinalIgnoreCase)) { Message = $"{spell} isn't known"; return; }
        if (!Played.TryGetValue(spell, out var name)) { Message = $"{spell} isn't played yet"; return; }
        // Cure wounds is cast as it's put in hand, and the hand keeps what it held (#10167).
        if (spell.Equals(CureRules.Spell, StringComparison.OrdinalIgnoreCase)) { CastCure(); return; }
        // So is magic reflection (#10349).
        if (spell.Equals(ReflectionRules.Spell, StringComparison.OrdinalIgnoreCase)) { CastReflection(); return; }
        // And the wizard eye (#10358).
        if (spell.Equals(WizardEyeRules.Spell, StringComparison.OrdinalIgnoreCase)) { CastWizardEye(); return; }
        Bow = null;
        EndSpell();
        SpellInHand = Played.Keys.First(k => k.Equals(spell, StringComparison.OrdinalIgnoreCase));
        spellAttackHeld = false;
        Message = $"{name} in hand";
    }

    // The attack spells under way (#10166): the bolt's charge, the inferno's hold and its next blow, and the arms' clip.
    float lightningAge = float.NaN, freezeAge = float.NaN, infernoAge = float.NaN, infernoNext;
    (string Clip, float Age, bool Loop)? spellClip;
    readonly List<LabBolt> bolts = [];
    /// <summary>The freeze bolts in flight, for drawing.</summary>
    internal IReadOnlyList<LabBolt> Bolts => bolts;
    /// <summary>The last attack spell's blow on an orc: the spell and the damage it took (#10166).</summary>
    internal (string Spell, float Damage)? LastSpellHit { get; private set; }

    /// <summary>The cures under way (#10167): the points each has still to give.</summary>
    readonly List<LabCure> cures = [];
    /// <summary>The last support spell cast and what it reached: the spell, and the orc it was cast at (none for a cure).</summary>
    internal (string Spell, Orc? At)? LastSupport { get; private set; }

    /// <summary>Cure wounds (#10167): its cost, a third of its effect at once, the rest a point at a time.</summary>
    void CastCure()
    {
        if (!Mana.Spend(Cure.Cost)) { Refuse("cure wounds", Cure.Cost); return; }
        Spells = Spells with { Casts = Spells.Casts + 1 };
        float healed = Health.Heal(Cure.AtOnce);
        cures.Add(new LabCure(Cure));
        Adrenaline.Gain(Cure.AdrenalineAdd);
        LastSupport = (CureRules.Spell, null);
        Message = FormattableString.Invariant($"Cure wounds: {healed:0.#} now, {Cure.Effect - Cure.AtOnce:0} to come");
    }

    /// <summary>Seconds magic reflection's shield has left about the player (#10349); 0 when it's down.</summary>
    internal float ShieldLeft { get; private set; }

    /// <summary>Magic reflection (#10349): its cost, and the shield up for its <see cref="ReflectionRules.Seconds"/>, a new one in place of the old.</summary>
    void CastReflection()
    {
        if (!Mana.Spend(Reflection.Cost)) { Refuse("magic reflection", Reflection.Cost); return; }
        Spells = Spells with { Casts = Spells.Casts + 1 };
        ShieldLeft = Reflection.Seconds;
        Adrenaline.Gain(Reflection.AdrenalineAdd);
        LastSupport = (ReflectionRules.Spell, null);
        Message = FormattableString.Invariant($"Magic reflection: {Reflection.Seconds:0} s");
    }

    /// <summary>The wizard eye (#10358): its cost, and on for its <see cref="WizardEyeRules.Seconds"/>.</summary>
    void CastWizardEye()
    {
        if (!Mana.Spend(WizardEye.Rules.Cost)) { Refuse("wizard eye", WizardEye.Rules.Cost); return; }
        Spells = Spells with { Casts = Spells.Casts + 1 };
        WizardEye.Cast();
        LastSupport = (WizardEyeRules.Spell, null);
        Message = FormattableString.Invariant($"Wizard eye: {WizardEye.Rules.Seconds:0} s");
    }

    /// <summary>
    /// An enemy's spell missile (#10349) at <paramref name="at"/> flying at <paramref name="velocity"/> (lab space), the
    /// player's feet at <paramref name="feet"/>: its new velocity if the player's shield turns it this step, else null.
    /// The lab has no enemy caster yet; the player's own missiles are never turned.
    /// </summary>
    internal Vector3? TurnSpell(string spell, Vector3 at, Vector3 velocity, Vector3 feet)
    {
        if (ShieldLeft <= 0) return null;
        static Vector3 Source(Vector3 lab) => new Vector3(lab.Z, lab.X, lab.Y) / SourceSpace.MetresPerUnit;
        return Reflection.Turn(spell, Source(at), Source(velocity), Source(feet)) is { } turned ? SourceSpace.Position(turned) : null;
    }

    void StepCures(float seconds)
    {
        foreach (var cure in cures) if (cure.Step(seconds) is int due and > 0) Health.Heal(due);
        cures.RemoveAll(c => c.Done);
    }

    /// <summary>Weaken, shrink or charm (#10167, #10348): its cost at the press, on the nearest orc in view.</summary>
    /// <param name="name">What the HUD calls it, when not its spell's name (a shrink is a powered weaken).</param>
    void CastAt(string spell, float cost, float adrenaline, float rangeUnits, float coneDegrees, string clip, Action<Orc> effect, string? name = null)
    {
        name ??= Played[spell];
        if (!Mana.Spend(cost)) { Refuse(name.ToLowerInvariant(), cost); return; }
        Spells = Spells with { Casts = Spells.Casts + 1 };
        spellClip = (clip, 0, false);
        var at = InView(eyeAt, forwardAt, rangeUnits, coneDegrees).FirstOrDefault();
        LastSupport = (spell, at);
        if (at is null) { Message = $"{name}: nothing in view"; return; }
        effect(at);
        Adrenaline.Gain(adrenaline);
        Message = $"{name} on {(at == Orc ? "the orc" : "its mate")}";
    }
    Vector3 eyeAt, forwardAt;

    void EndSpell()
    {
        lightningAge = freezeAge = infernoAge = float.NaN;
        spellClip = null;
    }

    /// <summary>
    /// The spell in hand this step, the fireball aside (#10166): the lightning bolt charges from a press and strikes
    /// <see cref="LightningRules.FireSeconds"/> later, the freeze goes as a bolt, the inferno burns while held.
    /// </summary>
    void StepAttackSpell(float seconds, bool pressed, bool held, Vector3 eye, Vector3 forward)
    {
        if (spellClip is { } playing)
            spellClip = playing.Loop || playing.Age + seconds < FirstPerson.ClipSeconds(playing.Clip) ? playing with { Age = playing.Age + seconds } : null;
        (eyeAt, forwardAt) = (eye, forward);
        switch (SpellInHand)
        {
            case WeakenRules.Spell:
                // The gauge full, it shrinks (#10348): the gauge empties at it, and its adrenaline_add comes back.
                if (pressed && Adrenaline.Full)
                    CastAt(WeakenRules.Spell, Shrink.Cost, Shrink.AdrenalineAdd, Shrink.RangeUnits, Shrink.ConeDegrees, WeakenRules.CastClip, o => { Adrenaline.Spend(); o.Shrink(); }, "Shrink");
                else if (pressed) CastAt(WeakenRules.Spell, Weaken.Cost, Weaken.AdrenalineAdd, Weaken.RangeUnits, Weaken.ConeDegrees, WeakenRules.CastClip, o => o.Weaken(Weaken.Seconds));
                break;
            case CharmRules.Spell:
                if (pressed) CastAt(CharmRules.Spell, Charm.Cost, Charm.AdrenalineAdd, Charm.RangeUnits, Charm.ConeDegrees, CharmRules.CastClip, o => o.Charm(Charm.Seconds));
                break;
            case LightningRules.Spell:
                if (pressed && float.IsNaN(lightningAge)) { lightningAge = 0; spellClip = (LightningRules.CastClip, 0, false); }
                else if (!float.IsNaN(lightningAge) && (lightningAge += seconds) >= Lightning.FireSeconds)
                {
                    lightningAge = float.NaN;
                    spellClip = (LightningRules.OutClip, 0, false);
                    if (!Mana.Spend(Lightning.Cost)) { Refuse("the lightning bolt", Lightning.Cost); break; }
                    Spells = Spells with { Casts = Spells.Casts + 1 };
                    if (InView(eye, forward, Lightning.RangeUnits, 10).FirstOrDefault() is { } struck)
                    {
                        SpellBlow(struck, LightningRules.Spell, Lightning.Effect, Lightning.AdrenalineAdd, Lightning.DeathDelay);
                        struck.Stun(eye with { Y = struck.Feet.Y });
                    }
                }
                else if (!float.IsNaN(lightningAge) && spellClip is null) spellClip = (LightningRules.HoldClip, 0, true);
                break;
            case FreezeRules.Spell:
                // Its mana at the press, its bolt a moment into the cast.
                if (!float.IsNaN(freezeAge) && (freezeAge += seconds) >= Freeze.CastSeconds)
                {
                    freezeAge = float.NaN;
                    bolts.Add(new LabBolt(Hand(eye, forward), Vector3.Normalize(forward) * Freeze.SpeedMetres));
                }
                if (!pressed || !float.IsNaN(freezeAge)) break;
                if (!Mana.Spend(Freeze.Cost)) { Refuse("the freeze", Freeze.Cost); break; }
                Spells = Spells with { Casts = Spells.Casts + 1 };
                freezeAge = 0;
                spellClip = (FreezeRules.CastClip, 0, false);
                break;
            case InfernoRules.Spell:
                if (float.IsNaN(infernoAge))
                {
                    if (!pressed) break;
                    if (!Mana.Spend(Inferno.Cost)) { Refuse("the inferno", Inferno.Cost); break; }
                    Spells = Spells with { Casts = Spells.Casts + 1 };
                    infernoAge = 0;
                    infernoNext = Inferno.FirstSeconds;
                    spellClip = (InfernoRules.CastClip, 0, false);
                    break;
                }
                float before = infernoAge;
                infernoAge += seconds;
                // Its upkeep from 1.5 s into the hold; let go, or out of mana, it stops.
                float upkeep = Inferno.Maintain * MathF.Max(0, infernoAge - MathF.Max(before, Inferno.UpkeepFrom));
                if (!held || upkeep > 0 && !Mana.Spend(upkeep)) { infernoAge = float.NaN; spellClip = (InfernoRules.OutClip, 0, false); break; }
                if (spellClip is null) spellClip = (InfernoRules.HoldClip, 0, true);
                // An area: every orc in its path each blow (R10166-1).
                for (; infernoAge >= infernoNext; infernoNext += Inferno.EverySeconds)
                    foreach (var burnt in InView(eye, forward, Inferno.RangeUnits, Inferno.ConeDegrees).ToList())
                        SpellBlow(burnt, InfernoRules.Spell, Inferno.Effect, Inferno.AdrenalineAdd, Inferno.BurnDeath);
                break;
        }
    }

    void Refuse(string what, float cost)
    {
        Spells = Spells with { Refused = Spells.Refused + 1 };
        Message = FormattableString.Invariant($"Not enough mana for {what} ({cost:0})");
    }

    /// <summary>
    /// The live orcs within <paramref name="rangeUnits"/> and <paramref name="coneDegrees"/> of the view (their body's
    /// middle), nearest first, that the arena doesn't hide: the world's ray from the eye to the middle meets nothing first
    /// (R10166-3).
    /// </summary>
    IEnumerable<Orc> InView(Vector3 eye, Vector3 forward, float rangeUnits, float coneDegrees)
    {
        var view = Vector3.Normalize(forward);
        float cos = MathF.Cos(coneDegrees * MathF.PI / 180);
        return Orcs.Where(o => o.Alive && !o.Following).Select(o => (Orc: o, To: o.Hull.Center - eye))
            .Where(x => x.To.Length() <= rangeUnits * SourceSpace.MetresPerUnit && Vector3.Dot(Vector3.Normalize(x.To), view) >= cos
                && !(WorldRay?.Invoke(eye, eye + x.To) is Vector3 wall && Vector3.Distance(eye, wall) < x.To.Length() - .05f))
            .OrderBy(x => x.To.Length()).Select(x => x.Orc);
    }

    /// <summary>
    /// A spell's blow on <paramref name="orc"/>: its effect times the orc's vulnerability to it, and its adrenaline. A blow
    /// that would kill leaves it at 1, and it dies <paramref name="deathDelay"/> s later, as the donor's did.
    /// </summary>
    void SpellBlow(Orc orc, string spell, float effect, float adrenaline, float deathDelay)
    {
        float damage = model.SpellDamage(spell, effect, 0, 0, orc.Kind.Model);
        bool lethal = damage >= orc.Health;
        float taken = lethal ? MathF.Max(0, orc.Health - 1) : damage;
        orc.Damage(taken);
        if (lethal && orc.Alive) orc.DieIn(deathDelay);
        Adrenaline.Gain(adrenaline);
        LastSpellHit = (spell, taken);
        Message = FormattableString.Invariant($"{Played[spell]} hits the orc for {taken:0.#}");
    }

    /// <summary>The freeze bolts: each one that meets an orc's body freezes it.</summary>
    void StepBolts(float seconds)
    {
        foreach (var bolt in bolts)
        {
            Orc? met = null;
            bolt.Step(seconds, Freeze.LifeSeconds, (from, to) =>
            {
                Vector3? world = WorldRay?.Invoke(from, to);
                float nearest = world is Vector3 w ? Vector3.Distance(from, w) : float.PositiveInfinity;
                Vector3? hit = world;
                foreach (var orc in Orcs.Where(o => o.Alive && !o.Following))
                {
                    var (centre, half) = orc.Hull;
                    if (SegmentEntersBox(from, to, centre - half, centre + half) is float t && t * Vector3.Distance(from, to) <= nearest)
                    {
                        nearest = t * Vector3.Distance(from, to);
                        hit = Vector3.Lerp(from, to, t);
                        met = orc;
                    }
                }
                if (met is not null && hit is Vector3 h && world is Vector3 ww && h == ww) met = null;
                return hit;
            });
            if (met is not null)
            {
                met.Freeze(Freeze.FrozenSeconds);
                Adrenaline.Gain(Freeze.AdrenalineAdd);
                LastSpellHit = (FreezeRules.Spell, 0);
                Message = "The orc is frozen";
            }
        }
        bolts.RemoveAll(b => b.Stopped is not null);
    }

    /// <summary>The bow in hand, or null for the weapon (#10168): the belt puts it there.</summary>
    internal LabBow? Bow { get; private set; }
    readonly List<LabArrow> arrows = [];
    /// <summary>The arrows in flight, for drawing.</summary>
    internal IReadOnlyList<LabArrow> ArrowsInFlight => arrows;
    /// <summary>Arrows shot, and those that struck an orc.</summary>
    internal (int Shot, int Struck) Archery { get; private set; }
    /// <summary>The last arrow that struck an orc: the orc, its hit group and what it asked.</summary>
    internal (Orc Orc, int? Group, float Asked)? LastArrow { get; private set; }
    /// <summary>The bow's zoom, 0 (none) to 1 (narrowed to <see cref="BowRules.ZoomedDegrees"/>).</summary>
    internal float Zoom { get; private set; }

    /// <summary>Puts the bow <paramref name="model"/> in hand, or the weapon back if it's there already (a belt key, #10168).</summary>
    internal void ToggleBow(string model)
    {
        if (Bow is { } held && held.Rules.Model == VpkIndex.Normalize(model)) { Bow = null; Message = "Weapon in hand"; return; }
        SpellInHand = null;
        Bow = new LabBow(BowRules.Of(donor, model), FirstPerson.ClipSeconds);
        Zoom = 0;
        Message = $"{Path.GetFileNameWithoutExtension(model)} in hand";
    }

    /// <summary>
    /// The bow this step: its cycle, an arrow leaving along the view from the eye, the zoom held (<c>bow_zoom</c>), and
    /// the arrows in flight (#10168).
    /// </summary>
    void StepBow(ulong tick, float seconds, bool attack, bool parry, bool zoom, Vector3 eye, Vector3 forward, bool grounded)
    {
        // In the air, and for the bow's landing clip once down, no draw begins.
        bowLanding = !grounded ? float.PositiveInfinity
            : float.IsPositiveInfinity(bowLanding) ? FirstPerson.ClipSeconds(LabBow.LandingClip) : MathF.Max(0, bowLanding - seconds);
        if (Bow is { } bow)
        {
            if (bow.Step(seconds, attack, parry, Build?.Has("bow_reload") == true, landing: bowLanding > 0))
            {
                arrows.Add(new LabArrow(eye, bow.Rules.Launch(forward), bow.Rules));
                Archery = Archery with { Shot = Archery.Shot + 1 };
            }
            bool zooming = zoom && Build?.Has("bow_zoom") == true && (bow.Aiming || Zoom > 0);
            Zoom = Math.Clamp(Zoom + (zooming ? seconds / BowRules.ZoomIn : -seconds / BowRules.ZoomOut), 0, 1);
        }
        else Zoom = 0;
        foreach (var arrow in arrows)
        {
            arrowHit = null;
            int impacts = arrow.Impacts.Count;
            arrow.Step(seconds, ArrowMeets);
            if (arrowHit is { } hit)
            {
                ArrowStrikes(tick, arrow, hit.Orc, hit.Contact, hit.Targets);
                if (arrow.Rules.Explosive) Blast(arrow.Rules, hit.Contact.Point);
            }
            // The explosive bow's blast at each impact on the world, a bounce's too (R10168-1).
            if (arrow.Rules.Explosive) foreach (var at in arrow.Impacts.Skip(impacts)) Blast(arrow.Rules, at);
            // The rope bow's arrow sticks where it stops in the world: a rope's anchor (R10168-2).
            if (arrow.Rules.Rope && arrow.Stopped is Vector3 stuck && arrowHit is null && arrow.Impacts.Count > impacts) RopeAnchors.Add(stuck);
        }
        arrows.RemoveAll(a => a.Stopped is not null);
    }

    /// <summary>
    /// The head's clip for the bow (#10168; <see cref="BowRules.HeadDraw"/>): the draw's dip as it's drawn, the steadier
    /// one with <c>stability</c>; without it, the sway loop held drawn, fading; the reload's own clip while it reloads.
    /// </summary>
    (string Clip, float Cycle, float Weight)? BowHead(LabBow bow)
    {
        var head = FirstPerson.Head;
        if (bow.State == BowState.FastReload) return (BowRules.FastReload, bow.Clip.Cycle, 1);
        if (float.IsNaN(bow.DrawAge) || bow.State is not (BowState.Drawing or BowState.Drawn)) return null;
        bool steady = Build?.Has("stability") == true;
        string drawClip = steady ? BowRules.HeadSteadyDraw : BowRules.HeadDraw;
        float draw = MathF.Max(.01f, head.ClipSeconds(drawClip));
        if (bow.DrawAge < draw) return (drawClip, bow.DrawAge / draw, 1);
        float held = bow.DrawAge - draw, weight = 1 - held / BowRules.SwayFade;
        if (steady || weight <= 0) return null;
        return (BowRules.HeadSway, held / MathF.Max(.01f, head.ClipSeconds(BowRules.HeadSway)) % 1, weight);
    }

    (Orc Orc, HitContact Contact, List<PosedTarget> Targets)? arrowHit;
    float bowLanding;

    /// <summary>The arrow's radius against the orc's hit boxes, metres: 1 unit.</summary>
    const float ArrowRadius = 1 * SourceSpace.MetresPerUnit;

    /// <summary>
    /// Where an arrow's step meets the nearest live orc's hit boxes on its way, or the world. It passes the orcs' cleaver
    /// and shield hulls (no recorded arrow met one).
    /// </summary>
    (Vector3 Point, Vector3 Normal, bool World)? ArrowMeets(Vector3 from, Vector3 to)
    {
        (Vector3 Point, Vector3 Normal)? world = WorldSurface?.Invoke(from, to)
            ?? (WorldRay?.Invoke(from, to) is Vector3 p ? (p, Vector3.Normalize(from - to)) : null);
        Vector3? met = world?.Point;
        float nearest = met is Vector3 w ? Vector3.Distance(from, w) : float.PositiveInfinity;
        foreach (var orc in Orcs.Where(o => o.Alive && !o.Following))
        {
            var targets = orc.Targets();
            if (targets.Count == 0) continue;
            var contacts = sweep.Sweep(from, to, ArrowRadius, targets).Where(c => !c.Hull).OrderBy(c => c.Along).ToList();
            if (contacts.Count > 0 && contacts[0].Along <= nearest)
            {
                nearest = contacts[0].Along;
                met = contacts[0].Point;
                arrowHit = (orc, contacts[0], targets);
            }
        }
        // The world nearer than every orc: the arrow meets it there.
        if (arrowHit is { } hit && met != hit.Contact.Point) arrowHit = null;
        return arrowHit is { } struck ? (struck.Contact.Point, Vector3.Normalize(from - to), false) : world is { } surface ? (surface.Point, surface.Normal, true) : null;
    }

    /// <summary>Where the rope bow's arrows stuck in the world (R10168-2): the anchors a rope will hang from (its climbing is #10330's).</summary>
    internal List<Vector3> RopeAnchors { get; } = [];

    /// <summary>The last explosive blast's victims: each orc and what it took.</summary>
    internal List<(Orc Orc, float Taken)> LastBlast { get; } = [];

    /// <summary>
    /// An explosive arrow's blast at <paramref name="at"/> (R10168-1): the bow's <c>explosive_damage</c>, a fire spell's
    /// (<c>fire_1</c>, which the donor casts there), on every live orc whose feet are within its <c>explosive_radius</c>.
    /// </summary>
    void Blast(BowRules rules, Vector3 at)
    {
        LastBlast.Clear();
        foreach (var orc in Orcs.Where(o => o.Alive && !o.Following).ToList())
        {
            if (Vector3.Distance(at, orc.Feet) / SourceSpace.MetresPerUnit > rules.ExplosiveRadius) continue;
            float damage = model.SpellDamage("fire_1", rules.ExplosiveDamage, 0, 0, orc.Kind.Model);
            orc.Damage(damage);
            LastBlast.Add((orc, damage));
            Message = FormattableString.Invariant($"The blast takes {damage:0.#} from the orc");
        }
    }

    /// <summary>
    /// An arrow strikes an orc (#10168): on its body, a player blow of the bow's shoot clip with the <c>WDmg</c> of the bow that shot it on
    /// the hit group struck (the not-alerted ×3 and the rest as any blow), and the clip's <c>adrenalineadd</c>; on its
    /// cleaver or shield, nothing.
    /// </summary>
    void ArrowStrikes(ulong tick, LabArrow arrow, Orc orc, HitContact c, List<PosedTarget> targets)
    {
        if (c.Target != 0) { Message = "The arrow glances off"; return; }
        hitOrc = orc;
        int group = targets[0].Model.Hitboxes[c.Hitbox].Group;
        var player = new Party("player", "", [BowRules.Shoot], 0, "#1", PlayerModel);
        float asked = Asked(tick, player, orc.Party(), group, arrow.Rules.Model);
        var shot = model.EventOf(player);
        Adrenaline.Gain(shot?.Adrenaline ?? 0);
        orc.Damage(asked);
        Archery = Archery with { Struck = Archery.Struck + 1 };
        LastArrow = (orc, group, asked);
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Message = !orc.Alive ? "The orc falls" : FormattableString.Invariant($"The arrow hits for {asked:0.##} (hit group {group})");
    }

    /// <summary>Casts the spell in hand: its cost from mana, or nothing when the mana doesn't cover it.</summary>
    void Cast(Vector3 eye, Vector3 forward)
    {
        if (!Mana.Spend(Fireball.Cost))
        {
            Spells = Spells with { Refused = Spells.Refused + 1 };
            Message = FormattableString.Invariant($"Not enough mana for the fireball ({Fireball.Cost:0})");
            return;
        }
        fireballs.Add(new LabFireball(Hand(eye, forward)));
        Spells = Spells with { Casts = Spells.Casts + 1 };
    }

    /// <summary>Where a fireball forms: half a metre ahead of the eye, a little down and to the right (the casting hand).</summary>
    static Vector3 Hand(Vector3 eye, Vector3 forward)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        return eye + forward * .5f + right * .15f - Vector3.UnitY * .15f;
    }

    void StepFireballs(float seconds, Vector3 eye, Vector3 forward)
    {
        foreach (var ball in fireballs)
        {
            ball.Step(seconds, Fireball, Hand(eye, forward), forward, Meets);
            if (ball.Exploded is Vector3 at) Explode(at);
        }
        fireballs.RemoveAll(b => b.Exploded is not null);
    }

    /// <summary>A fireball's size, units: it meets the orc's body this far from it.</summary>
    const float BallUnits = 5;

    /// <summary>
    /// Where a fireball's step meets an orc's body (a sphere of its 5 units against the body's box: the nearest live orc
    /// on its way, the mate too, R10165-1) or the world.
    /// </summary>
    Vector3? Meets(Vector3 from, Vector3 to)
    {
        Vector3? world = WorldRay?.Invoke(from, to);
        float nearest = world is Vector3 w ? Vector3.Distance(from, w) : float.PositiveInfinity;
        Vector3? met = world;
        foreach (var orc in Orcs.Where(o => o.Alive && !o.Following))
        {
            var (centre, half) = orc.Hull;
            var grown = half + new Vector3(BallUnits * SourceSpace.MetresPerUnit);
            if (SegmentEntersBox(from, to, centre - grown, centre + grown) is float t && t * Vector3.Distance(from, to) <= nearest)
            {
                nearest = t * Vector3.Distance(from, to);
                met = Vector3.Lerp(from, to, t);
            }
        }
        return met;
    }

    /// <summary>The fraction along the segment where it enters the box, or null if it doesn't.</summary>
    static float? SegmentEntersBox(Vector3 from, Vector3 to, Vector3 min, Vector3 max)
    {
        float t0 = 0, t1 = 1;
        var d = to - from;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = from[axis], v = d[axis], lo = min[axis], hi = max[axis];
            if (MathF.Abs(v) < 1e-9f) { if (o < lo || o > hi) return null; continue; }
            float a = (lo - o) / v, b = (hi - o) / v;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            if (t0 > t1) return null;
        }
        return t0;
    }

    /// <summary>
    /// A fireball goes off: each live orc in its radius (the mate too, R10165-1) takes its share by its distance (to its
    /// body's nearest point), and adrenaline its gain once.
    /// </summary>
    void Explode(Vector3 at)
    {
        Spells = Spells with { Explosions = Spells.Explosions + 1 };
        var hurt = new List<string>();
        foreach (var orc in Orcs.Where(o => o.Alive && !o.Following).ToList())
        {
            var (centre, half) = orc.Hull;
            // From the ball's edge: one that struck the body is 0 off.
            float distance = MathF.Max(0, Vector3.Distance(at, Vector3.Clamp(at, centre - half, centre + half)) / SourceSpace.MetresPerUnit - BallUnits);
            float damage = model.SpellDamage(FireballRules.Spell, Fireball.Effect, Fireball.RadiusUnits, distance, orc.Kind.Model);
            if (damage <= 0) continue;
            orc.Damage(damage);
            string who = orc == Orc ? "the orc" : "its mate";
            if (orc == Orc) LastFireball = (distance, damage); else LastMateFireball = (distance, damage);
            hurt.Add(FormattableString.Invariant($"{who} for {damage:0.#} ({distance:0} units off)"));
        }
        if (hurt.Count == 0) return;
        Adrenaline.Gain(Fireball.AdrenalineAdd);
        Message = "The fireball hits " + string.Join(" and ", hurt);
    }

    /// <summary>The last fireball that hurt the mate: its distance (units) and the damage.</summary>
    internal (float Distance, float Damage)? LastMateFireball { get; private set; }
    static string ReadyClipOf(string spell) => spell switch
    {
        LightningRules.Spell => LightningRules.ReadyClip,
        FreezeRules.Spell => FreezeRules.ReadyClip,
        InfernoRules.Spell => InfernoRules.ReadyClip,
        WeakenRules.Spell => WeakenRules.ReadyClip,
        CharmRules.Spell => CharmRules.ReadyClip,
        _ => FireballRules.ReadyClip,
    };

    float SpellCycle((string Clip, float Age, bool Loop) clip)
    {
        float length = MathF.Max(.01f, FirstPerson.ClipSeconds(clip.Clip));
        return clip.Loop ? clip.Age / length % 1 : Math.Clamp(clip.Age / length, 0, 1);
    }

    /// <summary>The last fireball that hurt the orc: its distance (units) and the damage.</summary>
    internal (float Distance, float Damage)? LastFireball { get; private set; }
    /// <summary>Telekinesis' numbers (#10164): mana by mass class, reach, the hold and throw, thrown bodies' damage.</summary>
    internal TelekinesisRules Telekinesis { get; }

    /// <summary>
    /// One step of a body thrown by telekinesis (R10164-3), of <paramref name="kg"/> and <paramref name="radius"/>, from
    /// <paramref name="from"/> to <paramref name="to"/> moving <paramref name="velocity"/> (m/s), against the orc's hit
    /// boxes. An impact (it meets the body, not touching it the step before: <paramref name="touching"/>, kept by the
    /// caller) takes the class's damage when the body is fast enough (<see cref="TelekinesisRules.Damage"/>), and again
    /// for its rebound when that's still fast enough (<see cref="TelekinesisRules.Restitution"/>): the donor's jug and
    /// log each took two hits an impact, the second at 37% and 47% of the first's force. A body still touching takes none
    /// again; one that bounced off and comes back is a new impact. The hits, and whether it met the body (the caller
    /// bounces it off).
    /// </summary>
    internal (int Hits, bool Met) Thrown(float kg, Vector3 from, Vector3 to, Vector3 velocity, float radius, ref bool touching)
    {
        if (!Orc.Alive || Orc.Following) { touching = false; return (0, false); }
        var targets = Orc.Targets();
        bool met = targets.Count > 0 && sweep.Sweep(from, to, radius, targets).Any(c => c.Target == 0 && !c.Hull);
        int hits = 0;
        if (met && !touching)
        {
            float speed = velocity.Length() / SourceSpace.MetresPerUnit;
            foreach (float at in new[] { speed, speed * Telekinesis.Restitution })
                if (Telekinesis.Damage(kg, at) is float damage and > 0 && Orc.Alive)
                {
                    Orc.Damage(damage);
                    hits++;
                }
            if (hits > 0) Message = FormattableString.Invariant($"The thrown body hits the orc ×{hits}");
        }
        touching = met;
        return (hits, met);
    }

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
        // A spell's slot puts it in hand, or the weapon back (#10165).
        if (Belt.SpellAt(slot) is { } spell) { ToggleSpell(spell); return; }
        if (Belt.BowAt(slot) is { } bow) { ToggleBow(bow); return; }
        if (Belt.Use(slot, Health, Mana) is { } item)
            Message = FormattableString.Invariant($"{item.Name}: health {PlayerHealth:0.#} of {Health.Max:0}, mana {Mana.Value:0.#} of {Mana.Max:0}");
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
    // A replay strikes only the orc it follows (R10154-4): the live squad-mate stands aside, even where the recorded orc
    // is absent, so the comparison doesn't hang on the lab's squad.
    IEnumerable<Orc> Orcs => Mate is { } mate && !Orc.Following ? [Orc, mate] : [Orc];

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

    /// <summary>
    /// The orcs react procedurally (#10382, <c>orc.flinch.on</c>) or by the authored clips, until the file next reloads.
    /// False when <c>combat.json</c> has no <c>orc.flinch</c> numbers.
    /// </summary>
    internal bool UseFlinch(bool on)
    {
        if (Tuning.Orc.Flinch is not { } flinch) return false;
        Retune(Tuning with { Orc = Tuning.Orc with { Flinch = flinch with { On = on } } });
        return true;
    }

    internal void Retune(CombatTuning next)
    {
        Tuning = next;
        Sword.Retune(FirstPerson.Clips(Weapon, next));
        // Another kind asked for (#10357: the necromancer, or back to the orc): that kind's NPC in the first's place, made
        // on first use and kept, so going back gives the same one.
        if (NpcKind.For(next.Orc) is var kind && kind != Orc.Kind)
        {
            var was = Orc;
            firsts[was.Kind] = was;
            Orc = firsts.TryGetValue(kind, out var kept) ? kept : new Orc(engine, donor, next.Orc, FirstSlot(kind), kind) { RagdollWorld = was.RagdollWorld, Hazards = was.Hazards };
            Orc.Retune(next.Orc);
            Orc.Spawn();
            if (hitOrc == was) hitOrc = Orc;
            flameArrows.Clear();
        }
        else Orc.Retune(next.Orc);
        Mates(next.Orc);
    }

    /// <summary>The squad-mate as the tuning asks: made, retuned to stand at its place (from its next spawn), or gone.</summary>
    void Mates(OrcTuning orc)
    {
        if (orc.SquadMate is not { } at) { Mate?.Dispose(); Mate = null; return; }
        // The mate fights with the cleaver: only the first orc is an archer (#10169).
        // A squad-mate falls and meets the arena as the orc does.
        if (Mate is null) { Mate = new Orc(engine, donor, orc with { Spawn = at, Bow = null }, slot: 1) { RagdollWorld = Orc.RagdollWorld, Hazards = Orc.Hazards }; return; }
        Mate.Retune(orc with { Spawn = at, Bow = null });
    }

    /// <summary>One admitted step of the fight.</summary>
    /// <param name="kickHeld">The kick button is down: a press asks for a kick, which starts once the body may kick.</param>
    /// <param name="orcPose">In a replay, the recorded orc the lab's follows this step instead of fighting on its own.</param>
    /// <param name="move">The movement input (side, forward): it picks a power attack's direction.</param>
    /// <param name="grounded">The body is on the ground: Dark Messiah doesn't kick in the air.</param>
    /// <param name="sprinting">Sprint is held while the body moves: it sprints, draining stamina, once there is enough to start.</param>
    /// <param name="crouched">The body crouches (duck held).</param>
    internal void Step(float seconds, ulong tick, bool attackHeld, bool blockHeld, bool kickHeld, Vector3 eye, Vector3 forward, Vector3 feet,
        OrcPose? orcPose = null, Vector2 move = default, bool grounded = true, bool sprinting = false, bool crouched = false, float lean = 0, bool zoomHeld = false)
    {
        fightSeconds += seconds;
        AdrenalineGained = 0;
        (eyeAt, forwardAt) = (eye, forward);
        // Where the player left the ground, for a jump attack's knockdown (R10152-1).
        if (grounded) groundHeight = feet.Y;
        Drop = grounded || float.IsNaN(groundHeight) ? 0 : MathF.Max(0, (groundHeight - feet.Y) / SourceSpace.MetresPerUnit);
        FatalityLanded = false;
        int swingsBefore = Sword.Swing;
        Stamina.Step(seconds, sprinting);
        // A spell in hand takes attack: a press casts it (#10165), and the sword doesn't swing.
        if (SpellInHand is not null)
        {
            bool pressed = attackHeld && !spellAttackHeld;
            if (SpellInHand == FireballRules.Spell) { if (pressed) Cast(eye, forward); }
            else StepAttackSpell(seconds, pressed, attackHeld, eye, forward);
            spellAttackHeld = attackHeld;
            attackHeld = blockHeld = false;
        }
        StepFireballs(seconds, eye, forward);
        StepBolts(seconds);
        // The bow in hand takes attack and parry (#10168); the sword is away, and doesn't kick.
        StepBow(tick, seconds, Bow is not null && attackHeld, Bow is not null && blockHeld, zoomHeld, eye, forward, grounded);
        if (Bow is not null) attackHeld = blockHeld = kickHeld = false;
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
        // A spell in hand poses the arms its own way: the cast clip while the newest fireball forms (#10165).
        FirstPerson.Bow = Bow is { } inHand ? (inHand.Rules.Model, inHand.Clip.Clip, inHand.Clip.Cycle, inHand.Nocked, BowHead(inHand)) : null;
        FirstPerson.Spell = SpellInHand is not { } inHandSpell ? null
            : inHandSpell == FireballRules.Spell ? fireballs.LastOrDefault(b => !b.Flying) is { } forming
                ? (FireballRules.ReadyClip, FireballRules.CastClip, Math.Clamp(forming.Age / MathF.Max(.01f, FirstPerson.ClipSeconds(FireballRules.CastClip)), 0, 1))
                : (FireballRules.ReadyClip, null, 0)
            : (ReadyClipOf(inHandSpell), spellClip?.Clip, spellClip is { } c ? SpellCycle(c) : 0);
        FirstPerson.Pose(Sword, seconds, eye, forward, !grounded, bodyVelocity, lean);
        var gait = Gait(seconds, feet, sprinting, crouched);
        // Charmed (#10167), an orc fights its squad-mate and the mate it, each in the other's place of the player.
        var orcFoe = orcPose is null ? Foe(Orc) : null;
        var blows = orcPose is null ? StepOrc(Orc, orcFoe, seconds, feet, gait) : Orc.Follow(orcPose, seconds);
        // Its squad-mate fights on its own, sees the player's swings as the first does, and takes the blows that reach it.
        var mateFoe = Mate is { } m && orcPose is null ? Foe(m) : null;
        var mateBlows = Mate is { } mate && orcPose is null ? StepOrc(mate, mateFoe, seconds, feet, gait) : [];
        Squad();
        // In a replay, the player's sweep meets the followed orc as the donor's blow this tick found it (#10205).
        if (orcPose?.Arrived is { } arrived) Orc.PoseAsArrived(arrived);
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
        Orc.EndArrived();
        foreach (var blow in blows) if (orcFoe is { } foe) OrcStrikesOrc(tick, blow, Orc, foe); else OrcStrikes(tick, blow, feet, forward, Orc);
        // The archer's arrows (#10169): let go this step at its foe (the player's eye, or a squad-mate it's against,
        // R10169-2), and those in flight.
        foreach (var shot in Orc.Loosed) Loose(Orc, shot, orcFoe is { } target && target != Orc ? target : null, eye);
        StepOrcArrows(tick, seconds, feet, eye);
        // The necromancer's flame arrows (#10357): let go this step at the player's eye, and those in flight.
        foreach (var cast in Orc.Cast) LooseFlame(Orc, cast, eye);
        StepFlameArrows(tick, seconds, feet, eye);
        foreach (var blow in mateBlows) if (mateFoe is { } foe) OrcStrikesOrc(tick, blow, Mate!, foe); else OrcStrikes(tick, blow, feet, forward, Mate!);
        if (orcPose is null) Stomp(tick, feet);
        StepCures(seconds);
        ShieldLeft = MathF.Max(0, ShieldLeft - seconds);
        WizardEye.Step(seconds);
        Health.Step(seconds);
        Mana.Step(seconds);
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
        var struck = new ReactionBlow(blow, kick, damage, !kick && lastCritical, group, angle);
        hitOrc.React(struck, OrcReactions.Pick(struck, hitOrc.State == OrcState.Stunned, hitOrc.SinceSmallHit), pushDirection, pushMetres);
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

    /// <summary>
    /// How far a kick reaches, metres (#10157): server.dll's <c>mm_player_maxdist_for_kick</c>, 100 units. The recorded
    /// kicks landed from 4 to 109 units, the player's origin to the victim's (median 59, 90% within 87).
    /// </summary>
    internal const float KickReach = 100 * SourceSpace.MetresPerUnit;

    void Kick(ulong tick, Vector3 eye, Vector3 forward)
    {
        settledSwing = SwingKey;
        decidedSwing = Sword.Swing;
        bool Reaches(Orc o)
        {
            var at = (o.Feet - eye) with { Y = 0 };
            return o.Alive && at.Length() <= KickReach && Vector3.Dot(Vector3.Normalize(forward with { Y = 0 }), Vector3.Normalize(at)) > .5f;
        }
        hitOrc = Orcs.Where(Reaches).OrderBy(o => ((o.Feet - eye) with { Y = 0 }).Length()).FirstOrDefault() ?? Orc;
        Vector3 flat = (hitOrc.Feet - eye) with { Y = 0 };
        bool reaches = Reaches(hitOrc);
        if (!reaches)
        {
            Stamina.Kicked(false);
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, eye + forward * KickReach, null, "none"));
            return;
        }
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        // An orc in its dodge's window can't be touched, by a kick as by a swing (R10151-3): the donor's no-effect.
        var orc = hitOrc.Party();
        // A dodging orc isn't touched: the kick costs what one into the air does (the donor's kick on a dodging orc took
        // nothing and touched nothing, l01_a-v14-arena-hand 1728.48 s).
        bool untouched = model.Data.Untouchable(orc.Model, orc.Sequence, orc.Cycle);
        Stamina.Kicked(!untouched);
        if (untouched)
        {
            Player = Player with { Missed = Player.Missed + 1 };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, "NoEffect (kick)"), blow: true);
            Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.NoEffect, 0, null));
            Message = "The orc dodges";
            return;
        }
        float asked = PlayerAsks(player, orc, null, kick: true, hitOrc.Health, alert: AlertAt(tick));
        // A shrunk orc (#10348) dies to a kick, whatever it asked: it was at 2 for the kick's 1.
        if (hitOrc.Shrunk)
        {
            hitOrc.Crush();
            Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
            Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, hitOrc.Feet + Vector3.UnitY * .1f, hitOrc.Feet + Vector3.UnitY * .1f, FormattableString.Invariant($"kick {asked:0.##}, crushed"), Damage: asked), blow: true);
            Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
            Message = "Kick: crushed";
            return;
        }
        hitOrc.Damage(asked);
        React(null, kick: true, asked, null, forward, flat, Npc.Orc.ClipPush);
        // A kick counts toward a stun's hits but carries no chance of its own: the recorded kicks felled 1 orc of 48.
        Stagger(null, eye with { Y = hitOrc.Feet.Y });
        Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + asked };
        Record(new SwingTrace(Sword.Swing, Sword.Clip.Clip, Sword.Cycle, eye, hitOrc.Feet + Vector3.UnitY, hitOrc.Feet + Vector3.UnitY, FormattableString.Invariant($"kick {asked:0.##}"), Damage: asked), blow: true);
        Log?.Add(new CombatEvent(tick, true, Sword.Clip.Clip, DefenseResult.Hit, asked, null));
        Message = "Kick";
    }

    /// <summary>
    /// The player's body on a shrunk orc (#10348): within <see cref="ShrinkRules.StompUnits"/> of its feet, on its level,
    /// it's crushed by a kick-typed blow of <see cref="ShrinkRules.StompDamage"/>, as the donor's were walked into.
    /// </summary>
    void Stomp(ulong tick, Vector3 feet)
    {
        float reach = ShrinkRules.StompUnits * SourceSpace.MetresPerUnit;
        foreach (var orc in Orcs)
        {
            if (!orc.Alive || !orc.Shrunk || ((orc.Feet - feet) with { Y = 0 }).Length() > reach || MathF.Abs(orc.Feet.Y - feet.Y) > .5f) continue;
            orc.Crush();
            Stomped++;
            Player = Player with { Hits = Player.Hits + 1, Dealt = Player.Dealt + Shrink.StompDamage };
            Log?.Add(new CombatEvent(tick, true, "stomp", DefenseResult.Hit, Shrink.StompDamage, null));
            Message = "Stomped";
        }
    }

    /// <summary>The shrunk orcs the player's body has crushed (#10348).</summary>
    internal int Stomped { get; private set; }

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

    /// <summary>
    /// The side an orc is on (R10167-1): its faction in <c>mm_relationship.txt</c>, <c>orc</c>, or charmed (#10167) the
    /// player's, as long as the charm lasts; it's back on its own as it ends.
    /// </summary>
    static string Side(Orc orc) => orc.CharmedFor > 0 ? PlayerSide : NpcFaction;
    const string PlayerSide = "player", NpcFaction = "orc";

    /// <summary>
    /// Whether one side fights another, by the donor's relationships: a faction those it hates (<c>HT</c>; the orcs'
    /// row: the player, and every faction but its own, which it likes); the player's side, charmed NPCs with it, those
    /// that hate the player. Two on the player's side don't fight.
    /// </summary>
    bool Hostile(string side, string other) =>
        side == PlayerSide ? other != PlayerSide && donor.Tables.Disposition(other, PlayerSide) == "HT" : donor.Tables.Disposition(side, other) == "HT";

    /// <summary>
    /// Whom <paramref name="orc"/> fights (#10167, R10167-1): its squad-mate, alive and of a side it's hostile to (a
    /// charmed one and its uncharmed mate, each the other); else the player, if it's hostile to the player's side (null);
    /// else no one, and it stands (itself). Two charmed orcs stand together, and neither strikes the player.
    /// </summary>
    Orc? Foe(Orc orc)
    {
        var other = orc == Orc ? Mate : Orc;
        if (other is { Alive: true } && Hostile(Side(orc), Side(other))) return other;
        return Hostile(Side(orc), PlayerSide) ? null : orc;
    }

    /// <summary>
    /// One orc's step against its foe: the player, another orc, or (charmed, with none: itself) no one, when it keeps by the
    /// player, on its side, holding off.
    /// </summary>
    List<OrcBlow> StepOrc(Orc orc, Orc? foe, float seconds, Vector3 feet, PlayerGait gait)
    {
        if (foe is null) return orc.Step(seconds, feet, Incoming(), Sword.Swing, gait, clearShot: orc.Archery is null || ClearShot(orc, eyeAt));
        bool clear = orc.Archery is null || foe == orc || ClearShot(orc, foe.Hull.Center);
        bool held = orc.Hold;
        if (foe == orc) orc.Hold = true;
        var blows = orc.Step(seconds, foe == orc ? feet : foe.Feet, Incoming(), Sword.Swing, gait, clearShot: clear);
        orc.Hold = held;
        return foe == orc ? [] : blows;
    }

    /// <summary>Blows between orcs (#10167; a charmed one and its mate).</summary>
    internal (int Blows, int Landed, float Dealt) OrcFights { get; private set; }

    /// <summary>
    /// An orc's blow on another (#10167): the donor's reach test against the victim's hull, and the cleaver's damage as
    /// on the player (the recorded mate's on the charmed orc asked its 12 and 8), and the victim's reaction to it.
    /// </summary>
    void OrcStrikesOrc(ulong tick, OrcBlow blow, Orc striker, Orc victim)
    {
        OrcFights = OrcFights with { Blows = OrcFights.Blows + 1 };
        Vector3 toVictim = (victim.Feet - striker.Feet) with { Y = 0 };
        static Vector2 Source(Vector3 v) => new Vector2(v.Z, v.X) / SourceSpace.MetresPerUnit;
        var reach = model.Reach(new ReachCase((int)tick, striker.Kind.Class, striker.Kind.Model, blow.Attack, blow.Part, victim.Kind.Class,
            Source(toVictim), Vector2.Normalize(Source(striker.Facing)), victim.Hull.HalfExtents.X / SourceSpace.MetresPerUnit, false));
        if (!reach.Lands) return;
        striker.Landed(blow);
        var attacker = striker.Party() with { Cycle = blow.Cycle };
        float asked = model.Damage(new DamageCase((int)tick, victim.Party(), attacker, striker.Weapon.Weapon, null, 0x4, 0, 0, victim.Health, victim.Health, false,
            blow.Part, striker.Weapon.Model)) ?? 0;
        victim.Damage(asked);
        OrcFights = OrcFights with { Landed = OrcFights.Landed + 1, Dealt = OrcFights.Dealt + asked };
        // Its reaction as to the player's blow, by the striker's facing in its frame.
        var facing = victim.Facing with { Y = 0 };
        var left = new Vector3(facing.Z, 0, -facing.X);
        float angle = toVictim.LengthSquared() > 1e-6f && facing.LengthSquared() > 1e-6f
            ? MathF.Atan2(Vector3.Dot(toVictim, left), Vector3.Dot(toVictim, facing)) * 180 / MathF.PI : 180;
        var struck = new ReactionBlow(model.EventOf(attacker), false, asked, false, null, angle);
        victim.React(struck, OrcReactions.Pick(struck, victim.State == OrcState.Stunned, victim.SinceSmallHit), toVictim.LengthSquared() > 1e-6f ? Vector3.Normalize(toVictim) : -victim.Facing, 0);
        Message = FormattableString.Invariant($"{(striker == Orc ? "The orc" : "Its mate")} strikes {(victim == Orc ? "the orc" : "its mate")} for {asked:0.#}");
    }

    /// <summary>
    /// An archer's arrow in flight (#10169): where it is, its velocity, its age, the archer and its stance as it was let
    /// go, and whom it was shot at: the player (null), or a squad-mate a charmed archer fights (R10169-2).
    /// </summary>
    internal sealed record OrcArrow(Orc Archer, bool Standing, Vector3 At, Vector3 Velocity, float Age, Orc? Target = null);
    readonly List<OrcArrow> orcArrows = [];
    /// <summary>The archers' arrows in flight, for drawing.</summary>
    internal IReadOnlyList<OrcArrow> OrcArrows => orcArrows;
    /// <summary>The archers' arrows let go, those that struck the player, and what they asked.</summary>
    internal (int Loosed, int Struck, float Dealt) OrcArchery { get; private set; }
    /// <summary>The last archer's arrow on the player: where it was let go (2D distance, Source units), and what it asked.</summary>
    internal (float Distance, float Asked)? LastOrcArrow { get; private set; }

    /// <summary>The archer's aim point (its <c>Arrows_Aim</c> attachment's height, 65 units: the recorded arrows' first samples).</summary>
    static Vector3 ArcherHand(Orc archer) => archer.Feet + Vector3.UnitY * 65 * SourceSpace.MetresPerUnit + archer.Facing * 20 * SourceSpace.MetresPerUnit;

    /// <summary>Nothing of the arena between the archer's aim and its target (the player's eye, or an orc's middle).</summary>
    bool ClearShot(Orc archer, Vector3 at) =>
        WorldRay?.Invoke(ArcherHand(archer), at) is not Vector3 wall || Vector3.Distance(ArcherHand(archer), wall) >= Vector3.Distance(ArcherHand(archer), at) - .05f;

    /// <summary>
    /// An arrow let go (#10169): from the archer's hand at its speed for the distance (<see cref="ArcheryData.SpeedAt"/>), straight at
    /// where the player is (no lead): <see cref="ArcheryData.AimBelowEye"/> below the eye standing, <see cref="ArcheryData.AimAboveEye"/> above on the move;
    /// or at a squad-mate it fights, charmed (R10169-2), at its middle (no NPC target was recorded).
    /// </summary>
    void Loose(Orc archer, OrcShot shot, Orc? target, Vector3 eye)
    {
        var a = archer.Archery!;
        float mpu = SourceSpace.MetresPerUnit;
        var from = ArcherHand(archer);
        var aim = target?.Hull.Center ?? eye + Vector3.UnitY * (shot.Standing ? -a.AimBelowEye : a.AimAboveEye) * mpu;
        float units = Vector2.Distance(new Vector2(aim.X, aim.Z), new Vector2(archer.Feet.X, archer.Feet.Z)) / mpu;
        orcArrows.Add(new OrcArrow(archer, shot.Standing, from, Vector3.Normalize(aim - from) * a.SpeedAt(units) * mpu, 0, target));
        OrcArchery = OrcArchery with { Loosed = OrcArchery.Loosed + 1 };
    }

    /// <summary>
    /// The archers' arrows this step: each on until it meets the player's box (<see cref="Reaches.PlayerHalfWidth"/> each
    /// way, from the feet to 8 units over the eye) grown by the arrow's own half-size (<see cref="OrcArrowSize"/>), the
    /// arena, or 2 s.
    /// </summary>
    /// <summary>
    /// The archer's arrow's half-size against the player, Source units: 12. The donor's arrows let go on the move passed
    /// 11.5 over the eye (over the player's 72-unit hull) and struck; those at a player strafing at up to 206 units/s were
    /// 9 to 11 units beside him as they struck, where the lab's flight lets him drift 21 (the donor's arrow's first sample
    /// is 150 units out from the hand, a tick or two of flight and more: its start isn't modelled), so 12 takes every one.
    /// The player's own arrows are 1 against the orc's hit boxes.
    /// </summary>
    const float OrcArrowSize = 12;

    /// <summary>
    /// A necromancer's flame arrow in flight (#10357): where it is, its velocity, its age, its caster, and whether the
    /// player's shield turned it (#10349): turned, it flies on at whoever it meets, the caster too.
    /// </summary>
    internal sealed record FlameArrow(Orc Caster, Vector3 At, Vector3 Velocity, float Age, bool Turned = false);
    readonly List<FlameArrow> flameArrows = [];
    /// <summary>The flame arrows in flight, for drawing.</summary>
    internal IReadOnlyList<FlameArrow> FlameArrows => flameArrows;
    /// <summary>The necromancer's flame arrows: let go, struck the player, turned by the shield, struck an NPC turned, and what they asked.</summary>
    internal (int Cast, int Struck, int Turned, int StruckNpc, float Dealt) Flames { get; private set; }
    /// <summary>The last flame arrow that struck an NPC: the NPC and what it took.</summary>
    internal (Orc Victim, float Damage)? LastFlameOnNpc { get; private set; }

    /// <summary>
    /// The flame arrow's half-size against the player, Source units: 4. The recorded ones whose line passed the player 3
    /// to 18 units aside struck; 25 to 37 aside, they went by.
    /// </summary>
    const float FlameArrowSize = 4;

    /// <summary>
    /// A flame arrow let go (#10357): from the caster's hand at <see cref="CastingData.SpeedUnits"/>, straight at the player's
    /// eye (no lead), turned about the vertical by the cast's error.
    /// </summary>
    void LooseFlame(Orc caster, NpcCast cast, Vector3 eye)
    {
        var c = caster.Casting!;
        var aim = eye - cast.From;
        var turned = Vector3.Transform(aim, Quaternion.CreateFromAxisAngle(Vector3.UnitY, cast.ErrorDegrees * MathF.PI / 180));
        flameArrows.Add(new FlameArrow(caster, cast.From, Vector3.Normalize(turned) * c.SpeedUnits * SourceSpace.MetresPerUnit, 0));
        Flames = Flames with { Cast = Flames.Cast + 1 };
    }

    /// <summary>
    /// A flame arrow of <paramref name="caster"/>'s flown from <paramref name="at"/> at <paramref name="velocity"/> (lab space),
    /// turned already or not: the smoke's, since no recorded turned one came back into its caster.
    /// </summary>
    internal void FlyFlame(Orc caster, Vector3 at, Vector3 velocity, bool turned) => flameArrows.Add(new FlameArrow(caster, at, velocity, 0, turned));

    /// <summary>
    /// The flame arrows this step: the player's shield turns one it reaches (<see cref="TurnSpell"/>); untouched, one
    /// strikes the player's box grown by its <see cref="FlameArrowSize"/>; turned, any live NPC's box it meets; the arena
    /// stops it, and so do 5 s.
    /// </summary>
    void StepFlameArrows(ulong tick, float seconds, Vector3 feet, Vector3 eye)
    {
        float mpu = SourceSpace.MetresPerUnit, half = (Reaches.PlayerHalfWidth + FlameArrowSize) * mpu;
        var (min, max) = (feet - new Vector3(half, FlameArrowSize * mpu, half), new Vector3(feet.X + half, eye.Y + (8 + FlameArrowSize) * mpu, feet.Z + half));
        for (int i = 0; i < flameArrows.Count; i++)
        {
            var arrow = flameArrows[i];
            if (!arrow.Turned && TurnSpell(CastingData.Spell, arrow.At, arrow.Velocity, feet) is { } away)
            {
                arrow = arrow with { Velocity = away, Turned = true };
                Flames = Flames with { Turned = Flames.Turned + 1 };
            }
            var to = arrow.At + arrow.Velocity * seconds;
            float? world = WorldRay?.Invoke(arrow.At, to) is Vector3 w ? Vector3.Distance(arrow.At, w) / MathF.Max(1e-6f, Vector3.Distance(arrow.At, to)) : null;
            if (!arrow.Turned && SegmentEntersBox(arrow.At, to, min, max) is float t && (world is not float wt || t <= wt))
            {
                FlameStrikesPlayer(tick, arrow);
                flameArrows.RemoveAt(i--);
                continue;
            }
            if (arrow.Turned && Orcs.Where(o => o.Alive && !o.Following)
                    .Select(o => (Orc: o, At: SegmentEntersBox(arrow.At, to, o.Hull.Center - o.Hull.HalfExtents - Vector3.One * FlameArrowSize * mpu, o.Hull.Center + o.Hull.HalfExtents + Vector3.One * FlameArrowSize * mpu)))
                    .Where(x => x.At is float at && (world is not float wt2 || at <= wt2)).OrderBy(x => x.At).FirstOrDefault() is { Orc: { } victim })
            {
                FlameStrikesNpc(arrow, victim);
                flameArrows.RemoveAt(i--);
                continue;
            }
            if (world is not null || arrow.Age + seconds >= 5) { flameArrows.RemoveAt(i--); continue; }
            flameArrows[i] = arrow with { At = to, Age = arrow.Age + seconds };
        }
    }

    /// <summary>A flame arrow strikes the player: the spell's <c>effect</c> (6), as every recorded one asked.</summary>
    void FlameStrikesPlayer(ulong tick, FlameArrow arrow)
    {
        float asked = model.SpellDamage(CastingData.Spell, arrow.Caster.Casting!.Effect, 0, 0, PlayerModel);
        Flames = Flames with { Struck = Flames.Struck + 1, Dealt = Flames.Dealt + asked };
        OrcStats = OrcStats with { Dealt = OrcStats.Dealt + asked };
        Log?.Add(new CombatEvent(tick, false, CastingData.Clip, DefenseResult.Hit, asked, null));
        if (!God && Health.Take(asked)) { deadSeconds = 0; OrcStats = OrcStats with { Deaths = OrcStats.Deaths + 1 }; Message = "You fall"; return; }
        Message = FormattableString.Invariant($"The necromancer's flame arrow hits for {asked:0.##}");
    }

    /// <summary>
    /// A turned flame arrow strikes an NPC (#10357): the spell's <c>effect</c> times its fire vulnerability (the
    /// necromancer's <c>.qct</c>: 2). None was recorded striking one: the necromancer's went back over it.
    /// </summary>
    void FlameStrikesNpc(FlameArrow arrow, Orc victim)
    {
        float damage = model.SpellDamage(CastingData.Spell, arrow.Caster.Casting!.Effect, 0, 0, victim.Kind.Model);
        victim.Damage(damage);
        Flames = Flames with { StruckNpc = Flames.StruckNpc + 1 };
        LastFlameOnNpc = (victim, damage);
        Message = FormattableString.Invariant($"The turned flame arrow hits {(victim == arrow.Caster ? "the necromancer" : "an orc")} for {damage:0.#}");
    }

    void StepOrcArrows(ulong tick, float seconds, Vector3 feet, Vector3 eye)
    {
        float mpu = SourceSpace.MetresPerUnit, half = (Reaches.PlayerHalfWidth + OrcArrowSize) * mpu;
        var (min, max) = (feet - new Vector3(half, OrcArrowSize * mpu, half), new Vector3(feet.X + half, eye.Y + (8 + OrcArrowSize) * mpu, feet.Z + half));
        for (int i = 0; i < orcArrows.Count; i++)
        {
            var arrow = orcArrows[i];
            var to = arrow.At + arrow.Velocity * seconds;
            float? world = WorldRay?.Invoke(arrow.At, to) is Vector3 w ? Vector3.Distance(arrow.At, w) / MathF.Max(1e-6f, Vector3.Distance(arrow.At, to)) : null;
            // At a squad-mate (R10169-2): its hull, grown as the player's box is; at the player, the player's box.
            var (lo, hi) = arrow.Target is { } target
                ? (target.Hull.Center - target.Hull.HalfExtents - Vector3.One * OrcArrowSize * mpu, target.Hull.Center + target.Hull.HalfExtents + Vector3.One * OrcArrowSize * mpu)
                : (min, max);
            if ((arrow.Target is null || arrow.Target.Alive) && SegmentEntersBox(arrow.At, to, lo, hi) is float t && (world is not float wt || t <= wt))
            {
                if (arrow.Target is { } struck) ArrowStrikesOrc(tick, arrow, struck);
                else ArrowStrikesPlayer(tick, arrow, feet);
                orcArrows.RemoveAt(i--);
                continue;
            }
            if (world is not null || arrow.Age + seconds >= 2) { orcArrows.RemoveAt(i--); continue; }
            orcArrows[i] = arrow with { At = to, Age = arrow.Age + seconds };
        }
    }

    /// <summary>
    /// An archer's arrow strikes the player (#10169): the combat model's NPC blow of its shot (<c>bow_attack</c>, or
    /// <c>layer_Bow_Attack</c> over its run: <c>HitPow</c> 1) with the bow's <c>WDmg</c> (the long bow's 4) and the
    /// orc's strength (1): 5, as every recorded arrow asked.
    /// </summary>
    /// <summary>
    /// A charmed archer's arrow strikes the squad-mate it fights (R10169-2): the combat model's NPC blow of its shot, as on
    /// the player (5), and the victim's reaction as to any blow.
    /// </summary>
    void ArrowStrikesOrc(ulong tick, OrcArrow arrow, Orc victim)
    {
        var shooter = arrow.Standing ? new Party("npc_orc_sword", ArcheryData.Shoot, [], 0, "#2", Npc.Orc.Model) : new Party("npc_orc_sword", "Run", [ArcheryData.ShootLayer], 0, "#2", Npc.Orc.Model);
        float asked = model.Damage(new DamageCase((int)tick, victim.Party(), shooter, "weapon_mm_bow_long", null, 0x40000, 0, 0, victim.Health, victim.Health, false, 0, arrow.Archer.Archery!.Bow)) ?? 0;
        victim.Damage(asked);
        OrcFights = OrcFights with { Blows = OrcFights.Blows + 1, Landed = OrcFights.Landed + 1, Dealt = OrcFights.Dealt + asked };
        var facing = victim.Facing with { Y = 0 };
        var left = new Vector3(facing.Z, 0, -facing.X);
        var along = arrow.Velocity with { Y = 0 };
        float angle = along.LengthSquared() > 1e-6f && facing.LengthSquared() > 1e-6f ? MathF.Atan2(Vector3.Dot(along, left), Vector3.Dot(along, facing)) * 180 / MathF.PI : 180;
        var struck = new ReactionBlow(model.EventOf(shooter), false, asked, false, null, angle);
        victim.React(struck, OrcReactions.Pick(struck, victim.State == OrcState.Stunned, victim.SinceSmallHit), along.LengthSquared() > 1e-6f ? Vector3.Normalize(along) : -victim.Facing, 0);
        Message = FormattableString.Invariant($"The orc's arrow hits {(victim == Orc ? "the orc" : "its mate")} for {asked:0.#}");
    }

    void ArrowStrikesPlayer(ulong tick, OrcArrow arrow, Vector3 feet)
    {
        var archer = arrow.Archer;
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        var shooter = arrow.Standing ? new Party("npc_orc_sword", ArcheryData.Shoot, [], 0, "#2", Npc.Orc.Model) : new Party("npc_orc_sword", "Run", [ArcheryData.ShootLayer], 0, "#2", Npc.Orc.Model);
        float asked = model.Damage(new DamageCase((int)tick, player, shooter, "weapon_mm_bow_long", null, 0x40000, 0, 0, PlayerHealth, PlayerHealth, false, 0, archer.Archery!.Bow)) ?? 0;
        float distance = Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(archer.Feet.X, archer.Feet.Z)) / SourceSpace.MetresPerUnit;
        OrcArchery = OrcArchery with { Struck = OrcArchery.Struck + 1, Dealt = OrcArchery.Dealt + asked };
        LastOrcArrow = (distance, asked);
        OrcStats = OrcStats with { Dealt = OrcStats.Dealt + asked };
        Log?.Add(new CombatEvent(tick, false, arrow.Standing ? ArcheryData.Shoot : ArcheryData.ShootLayer, DefenseResult.Hit, asked, null));
        if (!God && Health.Take(asked)) { deadSeconds = 0; OrcStats = OrcStats with { Deaths = OrcStats.Deaths + 1 }; Message = "You fall"; return; }
        Message = FormattableString.Invariant($"The orc's arrow hits for {asked:0.##}");
    }

    void OrcStrikes(ulong tick, OrcBlow blow, Vector3 feet, Vector3 forward, Orc striker)
    {
        OrcStats = OrcStats with { Blows = OrcStats.Blows + 1 };
        Vector3 toPlayer = (feet - striker.Feet) with { Y = 0 };
        // The donor's reach test, in Source units on its axes (lab X is Source Y, lab Z Source X).
        static Vector2 Source(Vector3 v) => new Vector2(v.Z, v.X) / SourceSpace.MetresPerUnit;
        var reach = model.Reach(new ReachCase((int)tick, striker.Kind.Class, striker.Kind.Model, blow.Attack, blow.Part, "player",
            Source(toPlayer), Vector2.Normalize(Source(striker.Facing)), Reaches.PlayerHalfWidth, false));
        LastOrcReach = reach;
        if (!reach.Lands) return;
        striker.Landed(blow);
        OrcStats = OrcStats with { Reached = OrcStats.Reached + 1 };
        var orc = striker.Party() with { Cycle = blow.Cycle };
        bool broken = false;
        var player = new Party("player", "", [Sword.Clip.Clip], Sword.Cycle, "#1", PlayerModel);
        // The weapon in its hand: the cleaver, or an archer's bow for its kick (#10169).
        var (weapon, weaponModel) = striker.Weapon;
        float asked = model.Damage(new DamageCase((int)tick, player, orc, weapon, null, 0x4, 0, 0, PlayerHealth, PlayerHealth, false,
            blow.Part, weaponModel)) ?? 0;
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

    /// <summary>A landing's fall damage (R10159-1): taken from health as a blow's is, unless the player is a god.</summary>
    internal void Fall(float damage)
    {
        if (damage <= 0 || God) return;
        if (Health.Take(damage)) { deadSeconds = 0; OrcStats = OrcStats with { Deaths = OrcStats.Deaths + 1 }; Message = "You fall"; return; }
        Message = FormattableString.Invariant($"The fall hurts for {damage:0.#}");
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
        if (Orc.Alive && model.Data.Character(Orc.Kind.Model) is { } character && (character.Attack("attack_Right") ?? character.Attacks.Values.FirstOrDefault())?.Events[0].Range is float range)
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
        Mana.Fill();
        fireballs.Clear();
        SpellInHand = null;
        LastFireball = LastMateFireball = null;
        EndSpell();
        cures.Clear();
        LastSupport = null;
        Stomped = 0;
        ShieldLeft = 0;
        WizardEye.Reset();
        OrcFights = default;
        orcArrows.Clear();
        OrcArchery = default;
        LastOrcArrow = null;
        bolts.Clear();
        LastSpellHit = null;
        Bow = null;
        arrows.Clear();
        RopeAnchors.Clear();
        LastBlast.Clear();
        Archery = default;
        LastArrow = null;
        Zoom = 0;
        Spells = default;
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
        foreach (var kept in firsts.Values) if (kept != Orc) kept.Dispose();
        Orc.Dispose();
        Mate?.Dispose();
        session.Dispose();
    }
}
