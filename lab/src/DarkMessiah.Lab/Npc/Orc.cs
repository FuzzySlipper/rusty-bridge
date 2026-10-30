using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>What the lab's orc is doing.</summary>
/// <summary>Its state; <see cref="Ranged"/> is the archer's on the move with the bow (#10169), running at its own speed, not its clip's.</summary>
internal enum OrcState { Approach, Ready, Attack, Parry, Hurt, Pushed, Down, Backstabbed, Stunned, KnockedDown, Dodging, Quiet, Alarmed, Ranged, Dying }

/// <summary>A ragdolled knockdown (#10216): the side its first frame took by the blow, the side it lay on at rest, and the seconds from the blow to getting up.</summary>
internal readonly record struct RagdollKnockdown(string FellAs, string LayOn, float Seconds);

/// <summary>One of the orc's attack parts landing this step: the part's index (Arkane's 2102 is 0, 2139 is 1).</summary>
/// <param name="Window">For a strike window's sweep, the cycle its window opens (the key it lands under); null for a part's blow.</param>
internal readonly record struct OrcBlow(int Part, string Attack, float Cycle, float? Window = null);

/// <summary>
/// A recorded donor orc as a replay hands it to the lab's (<see cref="Orc.Follow"/>), in lab space: where it
/// stands and faces, its health, and what it plays. <see cref="Fresh"/> is set when the recording streamed its
/// animation this tick (an NPC's state reaches the Wire every few ticks; between them the lab plays it on).
/// </summary>
/// <param name="Kit">The model paths of the items the recorded orc owns (Wire v10's owners): its cleaver, and a shield
/// if it has one. Null where the recording doesn't say (before v10): the lab's own kit.</param>
/// <param name="Model">The recorded orc's body model (its entity's model path); null for the lab's own.</param>
/// <param name="Overlays">Every overlay the recorded orc played, in its order, with its cycle and weight (#10198): the
/// pose it is drawn and swept in, as the mirror's Shadow poses it. <see cref="Layer"/> is the one that decides its state.</param>
internal sealed record OrcPose(string Id, Vector3 Feet, Vector3 Facing, float Health, string Sequence, float Cycle, float Rate,
    string? Layer, float LayerCycle, bool Fresh, IReadOnlyList<string>? Kit = null, string? Model = null,
    IReadOnlyList<(string Clip, float Cycle, float Weight)>? Overlays = null);

/// <summary>
/// The lab's orc: Arkane's <c>npc_orc</c> with its cleaver and shield, fighting by its <c>.qct</c> (#10150,
/// <see cref="OrcMind"/>): it walks in (<c>CombatWalk</c>) to its green zone, holds or strafes (<c>Combatmove_*</c>)
/// between its red and yellow zones while it waits out its cool or berserk interval, then picks an attack whose strike
/// lands from where it stands, played at the cleaver's <c>speed_attack</c>. Every clip moves it by its root motion
/// (mstudiomovement_t), as the donor's entity moves. Each attack part's event (2102, 2139) the step crosses is a blow. It parries and dodges by its <c>.qct</c> (<see cref="OrcDefence"/>),
/// reacts to each blow by the donor's rule (<see cref="OrcReactions"/>), and is stunned and knocked down by it
/// (<see cref="OrcStagger"/>). At no health it drops and comes back after a while.
/// </summary>
internal sealed partial class Orc : IDisposable
{
    internal const string Model = "models/npc/orc/npc_orc.mdl", Cleaver = "models/Items/Weapons/sword_orc/sword_orc.mdl",
        Shield = "models/items/armors/shield_orc/shield_orc.mdl";
    internal const ulong BodyId = 0x4C41_0010, CleaverId = 0x4C41_0011, ShieldId = 0x4C41_0012, BowId = 0x4C41_0013;
    static readonly int[] PartEvents = [2102, 2139];

    readonly IEngineContext engine;
    readonly DonorContent donor;
    readonly Puppet ownBody, cleaver, shield, bow;
    // The body drawn and swept: the lab's own, or in a replay the recorded orc's model (R10177-2), one puppet per model.
    Puppet body;
    readonly Dictionary<string, Puppet?> bodies = new(StringComparer.OrdinalIgnoreCase);
    const ulong OtherBodyIds = 0x4C41_0200;
    /// <summary>How far apart two orcs' Engine ids are.</summary>
    internal const ulong SlotIds = 0x1000;
    readonly ulong ids;
    readonly Random random = new(1);
    OrcTuning tuning;
    float downSeconds, pushLeft;
    readonly OrcMind mind;
    readonly OrcStagger stagger;
    readonly OrcDefence defence;
    readonly OrcStamina stamina;
    readonly OrcPerception perception;
    int seenSwing = -1, answeredSwing = -1;
    // The procedural reaction (#10382): its lean, and whether its footing is lost (Hurt with no reaction clip).
    readonly OrcFlinch flinch = new();
    bool staggered, leaning;
    // A small hit playing over whatever it does (#10153), and when the last began.
    string? hitLayer;
    float hitLayerCycle;
    double lastSmallHit = double.NegativeInfinity;
    bool wasAttacking;
    // Its own clock, seconds, for the dodge's delay; and the parry layer it played last (it never plays one twice running).
    double clock;
    string lastParry = "";
    // A stun's and a knockdown's time left, and the side a knockdown lies on (back or front).
    float stunLeft, groundLeft;
    string groundSide = "back";
    // The knockdown as an Engine ragdoll (#10216): waiting for its spawn, falling and resting, lying, blending back.
    OrcRagdoll? ragdoll;
    bool ragdollPending, ragdollSettled;
    Vector3 ragdollPush;
    float ragdollUp = OrcRagdoll.Rise;
    // The arena's hazards (#10157): a kick's push by its clip, the fire it burns in and when, its last seconds dying, a
    // fall's speed, and whether the fall it's in kills it.
    bool pushRoot, onFire, fatalFall;
    float burnLeft, dieLeft, fallSpeed, fellFromY;
    float ragdollSeconds, lieLeft, knockedFor;
    string fallSide = "back";
    // The fall into a knockdown: the pose it was struck in, blended into the lying one (R10152-2).
    string fallFrom = "combatidle";
    float fallFromCycle, fallElapsed = float.PositiveInfinity;
    Vector3 pushDirection;

    /// <param name="slot">Which of the lab's orcs it is: 0 the one the player fights, 1 its squad-mate (R10154-1); each
    /// slot's Engine ids are <see cref="SlotIds"/> apart.</param>
    internal Orc(IEngineContext engine, DonorContent donor, OrcTuning tuning, int slot = 0, NpcKind? kind = null)
    {
        this.engine = engine;
        this.donor = donor;
        this.tuning = tuning;
        Kind = kind ?? NpcKind.OrcSword;
        ids = (ulong)slot * SlotIds;
        body = ownBody = new Puppet(engine, donor.Model(Kind.Model), BodyId + ids);
        bodies[VpkIndex.Normalize(Kind.Model)] = ownBody;
        // Its weapon in the cleaver's place (the necromancer's hook), and a shield only if it carries one.
        cleaver = new Puppet(engine, donor.Model(Kind.Weapon), CleaverId + ids);
        shield = new Puppet(engine, donor.Model(Kind.Shield ?? Shield), ShieldId + ids);
        bow = new Puppet(engine, donor.Model(ArcheryData.LongBow), BowId + ids);
        AttackSpeed = donor.Number(Kind.Weapon, "entity_data", "speed_attack") ?? 1;
        mind = new OrcMind(OrcMindData.Read(donor, Kind.Model, ownBody.Model.Info, donor.Combat));
        stagger = new OrcStagger(OrcStaggerData.Read(donor, Kind.Model));
        defence = new OrcDefence(OrcDefenceData.Read(donor, Kind.Model));
        stamina = new OrcStamina(OrcStaminaData.Read(donor, Kind.Model));
        perception = new OrcPerception(OrcPerceptionData.Read(donor, Kind.Model));
        Backstable = donor.Number(Kind.Model, "entity_data", "backstable") is > 0;
        Spawn();
    }

    /// <summary>What it is (#10357): an orc swordsman, or a necromancer.</summary>
    internal NpcKind Kind { get; }

    /// <summary>
    /// Where its attacks' strikes land (Source units off the player once its travel is done): the orc's fitted windows
    /// (<c>combat.json</c> <c>orc.attackLandings</c>); another kind's (#10357, the necromancer's, of one recorded blow)
    /// anywhere within the attack's <c>.qct</c> reach, else its yellow zone.
    /// </summary>
    IReadOnlyDictionary<string, float[]> Landings => Kind == NpcKind.OrcSword ? tuning.AttackLandings
        : ownLandings ??= mind.Data.Attacks.Concat(mind.Data.Wounded ?? []).DistinctBy(a => a.Clip).ToDictionary(a => a.Clip, a => new[] { 0f, a.Range > 0 ? a.Range : mind.Data.Yellow });
    Dictionary<string, float[]>? ownLandings;

    /// <summary>It holds off: no attack starts (a developer switch, and the smoke's still orc).</summary>
    internal bool Hold { get; set; }

    /// <summary>What it fights by, from its .qct (#10150).</summary>
    internal OrcMindData Mind => mind.Data;

    /// <summary>Its wait since its last attack began is over: in a stance it may attack.</summary>
    internal bool MayAttack => mind.Ready;
    /// <summary>Its body's Engine id (its character obstacle's too).</summary>
    internal ulong Id => BodyId + ids;
    /// <summary>It saw the player this step, unaware before (R10154-1): its squad-mates within reach are called.</summary>
    internal bool Spotted { get; private set; }
    /// <summary>Whether it sees at all: not in a map the settings' <c>nostealth</c> block lists (<c>combat.json</c> <c>orc.map</c>).</summary>
    internal bool Sees => perception.Data.Sees(tuning.Map);
    /// <summary>Seconds left in its clip at the rate it plays.</summary>
    internal float ClipLeft => (1 - Cycle) * body.Model.Clip(Clip).Seconds / (State == OrcState.Attack ? AttackSpeed : 1);
    /// <summary>A fixed wait between attacks, seconds, in place of the mode's random one: for the smoke.</summary>
    internal float? FixedAttackWait { get => mind.FixedWait; set => mind.FixedWait = value; }

    internal float AttackSpeed { get; }
    internal Vector3 Feet { get; private set; }
    internal Vector3 Facing { get; private set; } = -Vector3.UnitZ;
    internal float Health { get; set; }
    internal OrcState State { get; private set; }
    internal string Clip { get; private set; } = "combatidle";
    internal float Cycle { get; private set; }
    internal string? Layer { get; private set; }
    internal float LayerCycle { get; private set; }
    internal bool Alive => State is not (OrcState.Down or OrcState.Dying);
    /// <summary>Its defense-state word as Dark Messiah's verdict reads it: bit 0x4 while parrying.</summary>
    internal uint DefenseState => State == OrcState.Parry ? CombatModel.ParryState : 0;
    internal DonorModel BodyModel => body.Model;
    /// <summary>Where it's drawn and struck: at its feet, facing its way, at its <see cref="Scale"/> (a shrunk orc's, #10348).</summary>
    internal Transform Placement => new(Feet, Rig.Facing(Facing, Vector3.UnitY), Vector3.One * Scale);
    /// <summary>The attacks it has made, and the blows that reached the player.</summary>
    internal int AttacksMade { get; private set; }

    internal void Retune(OrcTuning next) => tuning = next;

    /// <summary>Following a recorded orc (<see cref="Follow"/>): its own loop, reactions and pushes are off.</summary>
    internal bool Following { get; private set; }
    // The items it holds: its own kit, or the followed orc's. No recorded orc has had a shield (#10177).
    bool holdsCleaver = true, holdsShield = true;
    bool OwnShield => Kind.Shield is not null;
    string? followed;
    float followRate = 1;
    // The followed orc's overlays as last streamed and played on since: what it is posed in (empty for its own loop).
    (string Clip, float Cycle, float Weight)[] overlays = [];
    // The overlays as last streamed: a change is taken as it arrives, whether or not the main sequence moved.
    IReadOnlyList<(string Clip, float Cycle, float Weight)>? streamedOverlays;
    readonly HashSet<(string Clip, int Part, float At)> landed = [];
    // What the followed orc plays: its sequence, cycle and rate, the layer that decides its state, and its overlays.
    readonly record struct Played(string Clip, float Cycle, float Rate, string? Layer, float LayerCycle, (string Clip, float Cycle, float Weight)[] Overlays);
    // While the procedural reaction has it (R10382-6): what the recording plays meanwhile, taken up on the handback.
    Played recorded;
    // Given back to a recording it hadn't tracked (another orc's): that orc's next fresh pose takes up its attack.
    bool resyncOnFresh;

    /// <summary>
    /// One replayed step: the orc stands, faces, plays and has the health the recording gives it, and plays on
    /// between streamed states at the recorded rate. Its state is read from what it plays: a parry layer is a parry
    /// (the verdict's parry bit), an attack sequence an attack, no health down. Returns the attack parts whose events
    /// it crossed, once per play of a clip.
    /// </summary>
    internal List<OrcBlow> Follow(OrcPose pose, float seconds)
    {
        Following = true;
        // Handed back by the procedural reaction this step: what the recording played meanwhile is past.
        bool handback = false;
        if (pose.Id != followed)
        {
            followed = pose.Id;
            landed.Clear();
            if (staggered) { staggered = false; resyncOnFresh = true; ReleasedAt = (Feet, Facing); ReleasedFor = "the replay followed another orc"; }
        }
        // Taken over by the procedural reaction (R10382-1): off its footing, it plays no recorded sequence, overlay or
        // blow, and slides and turns as the model has it, until it has its footing back and follows the recording again.
        if (staggered)
        {
            if (tuning.Flinch is { On: true } overriding)
            {
                var (slid, turned) = flinch.Advance(overriding, seconds);
                Feet += slid;
                Facing = Vector3.Normalize(Vector3.Transform(Facing, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turned * MathF.PI / 180)));
                Health = pose.Health;
                Advance(seconds);
                if (flinch.Busy > 0 && pose.Health > 0)
                {
                    recorded = Track(recorded, pose, seconds);
                    return [];
                }
            }
            staggered = false;
            ReleasedAt = (Feet, Facing);
            ReleasedFor = pose.Health <= 0 ? "the recorded orc died" : tuning.Flinch is { On: true } ? "its footing back" : "the procedural reaction was switched off";
            // Given back (R10382-5, R10382-6): it takes up what the recording played meanwhile, fresh this step or not, so
            // the attack events that passed while it reeled are landed below and not struck.
            (Clip, Cycle, followRate, Layer, LayerCycle, overlays) = recorded;
            handback = true;
        }
        // The recorded body model, if the import has it; else the lab's. A body that lacks the clip playing (the
        // one-clip ragdoll bodies) takes the pose's sequence, or its idle, or its only clip.
        if (pose.Model is { Length: > 0 } model && (Body(model) ?? ownBody) is var next && next != body)
        {
            body = next;
            var has = body.Model.Info.Clips;
            if (!has.ContainsKey(Clip))
            {
                Clip = has.ContainsKey(pose.Sequence) ? pose.Sequence : has.ContainsKey("combatidle") ? "combatidle" : has.Keys.First();
                Cycle = 0;
            }
            if (Layer is not null && !has.ContainsKey(Layer)) Layer = null;
        }
        Feet = pose.Feet;
        Facing = pose.Facing;
        Health = pose.Health;
        if (pose.Kit is { } kit)
        {
            holdsCleaver = kit.Any(m => m.Equals(Cleaver, StringComparison.OrdinalIgnoreCase));
            holdsShield = kit.Any(m => m.Equals(Shield, StringComparison.OrdinalIgnoreCase));
        }
        var (clipBefore, before, layerBefore, layerCycleBefore) = (Clip, Cycle, Layer, LayerCycle);
        bool adopted = pose.Fresh && body.Model.Info.Clips.ContainsKey(pose.Sequence);
        (Clip, Cycle, followRate, Layer, LayerCycle, overlays) = Track(new(Clip, Cycle, followRate, Layer, LayerCycle, overlays), pose, seconds);
        State = Health <= 0 ? OrcState.Down
            : Layer?.Contains("parry", StringComparison.OrdinalIgnoreCase) == true || Clip.Contains("parry", StringComparison.OrdinalIgnoreCase) ? OrcState.Parry
            : Clip.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || Layer?.StartsWith("layer_attack", StringComparison.OrdinalIgnoreCase) == true ? OrcState.Attack
            : OrcState.Ready;
        // The procedural reaction's lean settles over the recorded pose (R10382-1); its slide and turn are the recording's.
        if (tuning.Flinch is { On: true } flinchTuning) flinch.Advance(flinchTuning, seconds);
        var blows = new List<OrcBlow>();
        if (State == OrcState.Down) return blows;
        // An attack plays as the main sequence, or, on the move, as a layer_attack_* overlay.
        if (Clip != clipBefore) landed.RemoveWhere(x => x.Clip == Clip);
        if (Layer != layerBefore) landed.RemoveWhere(x => x.Clip == Layer);
        // On the handback the recorded attack, main or layer, takes up where the recording has it: the events it crossed
        // while the orc reeled are landed, not struck now; those still ahead strike as they're crossed (R10382-5). Given
        // back to an orc it hadn't tracked, it waits for that orc's next fresh pose to know where its attack is (R10382-6).
        if (resyncOnFresh && adopted) (handback, resyncOnFresh) = (true, false);
        if (handback || resyncOnFresh)
        {
            Elapsed(Clip, Cycle);
            if (Layer is not null) Elapsed(Layer, LayerCycle);
            return blows;
        }
        Cross(Clip, Clip == clipBefore ? before : 0, Cycle, blows);
        if (Layer is not null) Cross(Layer, Layer == layerBefore ? layerCycleBefore : 0, LayerCycle, blows);
        return blows;
    }

    /// <summary>
    /// What the followed orc plays after <paramref name="seconds"/> more of <paramref name="was"/>: the pose's sequence and
    /// layer when it streamed them fresh, else played on at the recorded rate; its overlays as streamed, else played on.
    /// </summary>
    Played Track(Played was, OrcPose pose, float seconds)
    {
        var has = body.Model.Info.Clips;
        var next = was;
        if (pose.Fresh && has.ContainsKey(pose.Sequence))
            next = was with { Clip = pose.Sequence, Cycle = pose.Cycle, Rate = pose.Rate > 0 ? pose.Rate : 1,
                Layer = pose.Layer is { } l && has.ContainsKey(l) ? l : null, LayerCycle = pose.LayerCycle };
        else
        {
            var clip = body.Model.Clip(was.Clip);
            float cycle = was.Cycle + (clip.Seconds > 0 ? seconds * was.Rate / clip.Seconds : 1);
            next = was with { Cycle = Looping(was.Clip) || clip.Looping ? cycle - MathF.Floor(cycle) : MathF.Min(cycle, 1) };
            if (was.Layer is not null) next = next with { LayerCycle = MathF.Min(1, was.LayerCycle + (body.Model.Clip(was.Layer).Seconds is > 0 and var s ? seconds / s : 1)) };
        }
        if (pose.Overlays is { } streamed && (streamedOverlays is null || !streamed.SequenceEqual(streamedOverlays)))
        {
            streamedOverlays = streamed;
            return next with { Overlays = [.. streamed.Where(o => has.ContainsKey(o.Clip))] };
        }
        var overlaid = new (string Clip, float Cycle, float Weight)[was.Overlays.Length];
        for (int i = 0; i < overlaid.Length; i++)
        {
            var (name, at, weight) = was.Overlays[i];
            var played = body.Model.Clip(name);
            at += played.Seconds > 0 ? seconds / played.Seconds : 1;
            overlaid[i] = (name, played.Looping ? at - MathF.Floor(at) : MathF.Min(at, 1), weight);
        }
        return next with { Overlays = overlaid };
    }

    /// <summary>Adds the attack parts <paramref name="clip"/> crossed from <paramref name="from"/> to <paramref name="to"/>, once per play.</summary>
    void Cross(string clip, float from, float to, List<OrcBlow> blows)
    {
        if (to < from - .05f) { landed.RemoveWhere(x => x.Clip == clip); from = 0; }
        var played = body.Model.Clip(clip);
        bool parts = false;
        for (int part = 0; part < PartEvents.Length; part++)
            foreach (float at in played.At(PartEvents[part]))
            {
                parts = true;
                if (from < at && to >= at && landed.Add((clip, part, at))) blows.Add(new OrcBlow(part, clip, at));
            }
        // A turn strike (Turn_strike_*) has no part events: it sweeps every step inside its window (2175 to 2176), as
        // the donor's does at every think, until a sweep lands (#10198; CombatModel's reach cases, Reach.cs).
        if (!parts && played.At(CombatData.WindowOpen).FirstOrDefault(float.NaN) is var open && played.At(CombatData.WindowClose).FirstOrDefault(float.NaN) is var close
            && float.IsFinite(open) && float.IsFinite(close) && to >= open && from <= close && !landed.Contains((clip, WindowPart, open)))
            blows.Add(new OrcBlow(0, clip, Math.Clamp(to, open, close), Window: open));
    }

    /// <summary>
    /// Makes this play of <paramref name="clip"/> the one landed: the attack parts it has passed by <paramref name="to"/>
    /// landed, striking none, and none of an earlier play's left to keep those ahead from striking (R10382-7).
    /// </summary>
    void Elapsed(string clip, float to)
    {
        landed.RemoveWhere(x => x.Clip == clip);
        var played = body.Model.Clip(clip);
        for (int part = 0; part < PartEvents.Length; part++)
            foreach (float at in played.At(PartEvents[part]))
                if (at <= to) landed.Add((clip, part, at));
    }

    // The landed key's part for a strike window.
    const int WindowPart = -1;

    /// <summary>A blow from a strike window landed: the window strikes no more this play.</summary>
    internal void Landed(OrcBlow blow)
    {
        if (blow.Window is float open) landed.Add((blow.Attack, WindowPart, open));
    }

    /// <summary>A body model's puppet, opened on first use; null when the import lacks the model.</summary>
    Puppet? Body(string model)
    {
        model = VpkIndex.Normalize(model);
        if (bodies.TryGetValue(model, out var puppet)) return puppet;
        puppet = donor.Read(Path.ChangeExtension(model, ".glb")) is null ? null : new Puppet(engine, donor.Model(model), OtherBodyIds + ids + (ulong)bodies.Count);
        bodies[model] = puppet;
        return puppet;
    }

    /// <summary>Back to the lab's own loop after a replay.</summary>
    internal void StopFollowing()
    {
        Following = false;
        flinch.Reset();
        FollowedFlinches.Clear();
        body = ownBody;
        (holdsCleaver, holdsShield) = (true, OwnShield);
        followed = null;
        overlays = [];
        streamedOverlays = null;
        landed.Clear();
        resyncOnFresh = false;
        Spawn();
    }

    /// <summary>
    /// One step: turns to the player, walks in, attacks, parries, reacts or lies down. Returns the blows whose part
    /// events this step crossed; whether they reach the player is the combat's to decide.
    /// </summary>
    /// <param name="swing">A player's swing coming, as the orc sees it (#10151): from its start until its hit window
    /// closes. The orc answers it once, as soon as it stands ready (R10151-1).</param>
    /// <param name="swingId">Which swing it is (the sword's count), so each is answered once.</param>
    /// <param name="gait">How the player moves, for an unaware orc's hearing (#10154).</param>
    /// <param name="clearShot">For an archer (#10169): nothing of the arena between its aim and the player's eye.</param>
    internal List<OrcBlow> Step(float seconds, Vector3 playerFeet, IncomingSwing swing, int swingId = 0, PlayerGait gait = PlayerGait.Running, bool clearShot = true)
    {
        clock += seconds;
        Spotted = false;
        Loosed.Clear();
        Cast.Clear();
        var blows = new List<OrcBlow>();
        // A spell's blow that would have killed it left it at 1: it dies once its fire or the shock has run (#10166).
        if (dyingLeft > 0 && (dyingLeft -= seconds) <= 0 && Alive) Damage(Health);
        Regenerate(seconds);
        // Shrunk (#10348), it stands small: no thought, no blow.
        if (StepShrink(seconds)) return blows;
        WeakenedFor = MathF.Max(0, WeakenedFor - seconds);
        CharmedFor = MathF.Max(0, CharmedFor - seconds);
        // Frozen (#10166), it stands as it was: no thought, no clip, no blow.
        if (FrozenFor > 0) { FrozenFor = MathF.Max(0, FrozenFor - seconds); return blows; }
        // Its wait runs from its attack's end, however it ended (R10151-1: server.dll stamps NPC +0x2980 with the time
        // while the NPC is flagged attacking, +0x2DFD59, and when its attack task completes, +0x2B06D2).
        // An attack cut short between steps (a blow, a stun, a fall) starts it here; one that ends inside a step, done
        // or dodged out of, as the step ends (R10151-4).
        AttackEnded();
        // Wounded mid-walk or mid-strafe, it goes on in its wounded clip.
        if (Wounded && Alive && Hurt(Clip) is var hurt && hurt != Clip) Clip = hurt;
        Vector3 toPlayer = playerFeet - Feet;
        toPlayer.Y = 0;
        float distance = toPlayer.Length();
        if (State is not (OrcState.Down or OrcState.Dying or OrcState.Pushed or OrcState.Hurt or OrcState.Backstabbed or OrcState.Stunned or OrcState.KnockedDown or OrcState.Quiet)
            && distance > 1e-3f)
            Facing = toPlayer / distance;
        bool fresh = swing != IncomingSwing.None && swingId != seenSwing;
        if (fresh) seenSwing = swingId;
        mind.Step(seconds, fresh);
        float before = Cycle;
        string playing = Clip;
        // Dead, it holds the pose it died in (R10157-1).
        if (State != OrcState.Down) Advance(seconds);
        if (State is OrcState.Approach or OrcState.Ready or OrcState.Attack or OrcState.Hurt or OrcState.Dodging) RootMotion(playing, before, Cycle);
        // The procedural reaction's lean goes on over whatever it plays; a slide runs to its end, and off its footing it
        // turns (back on it, it faces the player again) (#10382).
        if (tuning.Flinch is { } flinchTuning && !Following)
        {
            var (slid, turned) = flinch.Advance(flinchTuning, seconds);
            Feet += slid;
            if (Staggered) Facing = Vector3.Normalize(Vector3.Transform(Facing, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turned * MathF.PI / 180)));
        }
        // Walking in, it moves at the donor's ground speed, not its walk clip's (R10150-2).
        if (State == OrcState.Approach && distance > 1e-3f) Feet += Facing * tuning.WalkSpeed * SourceSpace.MetresPerUnit * seconds;
        // The bodies keep apart: its feet no nearer the player's than two hulls (BodyGap).
        Vector3 apart = (Feet - playerFeet) with { Y = 0 };
        if (apart.LengthSquared() > 1e-6f && apart.Length() < BodyGap * SourceSpace.MetresPerUnit)
            Feet = playerFeet with { Y = Feet.Y } + Vector3.Normalize(apart) * BodyGap * SourceSpace.MetresPerUnit;
        bool done = Cycle >= 1 && !Looping(Clip);
        // Distances in Source units, as the .qct's zones and ranges are.
        float units = distance / SourceSpace.MetresPerUnit;
        var data = mind.Data;
        // An archer with its bow fights its own way (#10169).
        if (StepArchery(seconds, units, toPlayer / SourceSpace.MetresPerUnit, clearShot)) { AttackEnded(); wasAttacking = State == OrcState.Attack; return blows; }
        if (StepCasting(seconds, units, toPlayer, clearShot)) { AttackEnded(); wasAttacking = State == OrcState.Attack; return blows; }
        switch (State)
        {
            case OrcState.Quiet:
                // Unaware (#10154): it idles where it stands, facing as it was, until it hears the player.
                // It sees in a map with stealth (R10154-2), it hears, or a squad-mate's call reaches it (R10154-1).
                var noticed = perception.Step(seconds, units, gait, Sees && perception.Data.InSight(Facing, toPlayer, units));
                Spotted = noticed == AwareBy.Seen;
                // Called, the donor's squad-mate ran in without the surprised clip (l05_a-v18-call-l05).
                if (noticed == AwareBy.Called) Play(OrcState.Ready, "combatidle");
                else if (noticed != AwareBy.None) Play(OrcState.Alarmed, ClipNamed("surprised") ?? "combatidle");
                else if (done) Play(OrcState.Quiet, IdleClips[random.Next(IdleClips.Length)]);
                break;
            case OrcState.Alarmed:
                if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Down:
                downSeconds += seconds;
                // Its ragdoll lies where it came to rest while the body is drawn (R10157-1).
                if (downSeconds > DrawnDead) ReleaseRagdoll();
                if (downSeconds >= tuning.RespawnSeconds) Spawn();
                break;
            case OrcState.Approach:
                // Walking it defends as standing (R10151-1: the parry's gate, +0x2A897C, skips attacking, not moving).
                var walkingAnswer = swing != IncomingSwing.None && swingId != answeredSwing ? defence.Decide(swing, clock, Wounded) : Defence.None;
                if (swing != IncomingSwing.None) answeredSwing = swingId;
                if (walkingAnswer == Defence.Parry) { Parry(); stamina.Spend(stamina.Data.Parry); }
                else if (walkingAnswer == Defence.Dodge) { Dodge(); stamina.Spend(stamina.Data.Dodge); }
                // It walks in to its green zone, or until an attack it may make lands from here.
                else if (units <= data.Green || !Hold && mind.Ready && mind.Choose(units, Landings, WeakenedFor > 0) is not null) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Ready:
                // A swing it saw while busy it answers now, if it hasn't yet and the swing is still coming.
                var answer = swing != IncomingSwing.None && swingId != answeredSwing ? defence.Decide(swing, clock, Wounded) : Defence.None;
                if (swing != IncomingSwing.None) answeredSwing = swingId;
                if (answer == Defence.Parry) { Parry(); stamina.Spend(stamina.Data.Parry); }
                else if (answer == Defence.Dodge) { Dodge(); stamina.Spend(stamina.Data.Dodge); }
                else if (!Hold && mind.Ready && mind.Choose(units, Landings, WeakenedFor > 0) is { } attack)
                {
                    Play(OrcState.Attack, attack.Clip);
                    AttacksMade++;
                }
                // Past its yellow zone, or ready to attack with nothing landing from here, it walks in.
                else if (units > data.Yellow || !Hold && mind.Ready && units > data.Green) Play(OrcState.Approach, "CombatWalk");
                // Ready and too close for any attack to land (pressed against the player at its hull's 32 units,
                // nearer than the uppercut's 34): it steps back to fight again (R10150-1), as the donor's orcs began
                // attacks out of Combatmove_backward_small.
                else if (!Hold && mind.Ready && units < mind.Nearest(Landings, WeakenedFor > 0) && Clip != Hurt("Combatmove_backward_small") && Clip != "Combatmove_backward_small")
                    Play(OrcState.Ready, "Combatmove_backward_small");
                else if (done || Clip == "combatidle" && Cycle < before) Wait(units);
                break;
            case OrcState.Attack:
                // Its dodge test doesn't ask whether it attacks, as its parry does (R10151-1: server.dll's defence skips
                // an NPC flagged attacking, +0x2A897C; its dodge test, +0x2ACB30, doesn't): a power attack or a kick
                // coming it may dodge out of its own attack. The donor's dodged 13% of power attacks attacking, as ready.
                if (swing is IncomingSwing.BreaksParry or IncomingSwing.Kick && swingId != answeredSwing)
                {
                    answeredSwing = swingId;
                    if (defence.Decide(swing, clock, Wounded) == Defence.Dodge) { Dodge(); stamina.Spend(stamina.Data.Dodge); break; }
                }
                var clip = body.Model.Clip(Clip);
                for (int part = 0; part < PartEvents.Length; part++)
                    foreach (float at in clip.At(PartEvents[part]))
                        if (before < at && Cycle >= at) blows.Add(new OrcBlow(part, Clip, at));
                if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Parry:
                if (LayerCycle >= 1) { Layer = null; Play(OrcState.Ready, "combatidle"); }
                break;
            case OrcState.Dodging:
                if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Pushed:
                var pushedFrom = Feet;
                if (pushRoot) RootMotion(playing, before, Cycle);
                else
                {
                    float step = MathF.Min(pushLeft, seconds * 3);
                    Feet += pushDirection * step;
                    pushLeft -= step;
                }
                // What the push meets first (#10157): spikes impale it, a wall or a fire's edge throws it down.
                if (Hazards?.Ahead(pushedFrom, Feet) is { } met)
                {
                    var moved = Feet - pushedFrom;
                    Feet = pushedFrom;
                    if (met == HazardKind.Spikes) Impale();
                    else
                    {
                        WallFall(moved.LengthSquared() > 1e-8f ? moved : pushDirection);
                        // Thrown down into a fire, it burns at once and is on fire (the donor's first burn came the tick its
                        // fall began).
                        if (met == HazardKind.FireEdge) { burnLeft = ArenaHazards.FireEvery; onFire = true; Damage(ArenaHazards.FireDamage); }
                    }
                }
                else if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Dying:
                if ((dieLeft -= seconds) <= 0) Dead();
                break;
            case OrcState.Hurt:
                if (staggered ? flinch.Busy <= 0 : done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Stunned:
                // hit_stunned_<side>, then Stunned until stunned_duration is spent, then Stunned_out.
                stunLeft -= seconds;
                if (Clip.StartsWith("hit_stunned", StringComparison.Ordinal) && done) Play(OrcState.Stunned, "Stunned");
                else if (Playing("Stunned") && stunLeft <= 0) Play(OrcState.Stunned, "Stunned_out");
                else if (Playing("Stunned_out") && done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.KnockedDown when ragdollPending || ragdoll is not null:
                knockedFor += seconds;
                StepRagdoll(seconds);
                break;
            case OrcState.KnockedDown when fatalFall:
                // The stand-in's fall to its death (#10157): it drops to the ground below and is dead there.
                if (Hazards?.Drop(Feet) is not float below) { Dead(); break; }
                fallSpeed += Gravity * seconds;
                Feet -= Vector3.UnitY * MathF.Min(fallSpeed * seconds, below);
                if (below - fallSpeed * seconds <= 0) Dead();
                break;
            case OrcState.KnockedDown:
                // Falling, then lying (StandUp_<side>_firstframe) while the donor's ragdoll would fall and rest, then getting up.
                groundLeft -= seconds;
                fallElapsed += seconds;
                if (Clip.EndsWith("_firstframe", StringComparison.Ordinal) && groundLeft <= 0) Play(OrcState.KnockedDown, $"StandUp_{groundSide}");
                else if (!Clip.EndsWith("_firstframe", StringComparison.Ordinal) && done) Play(OrcState.Ready, "combatidle");
                break;
        }
        StepHazards(seconds);
        AttackEnded();
        wasAttacking = State == OrcState.Attack;
        return blows;
    }

    /// <summary>
    /// The arena's hazards where it now stands (#10157; <see cref="ArenaHazards"/>): spikes it touches impale it, a fire
    /// burns it and sets it on fire (it dies in <c>onfire</c> as soon as it can act), and a drop under its feet it drops
    /// down, or falls to its death past <see cref="ArenaHazards.LethalDrop"/>.
    /// </summary>
    void StepHazards(float seconds)
    {
        if (Hazards is not { } hazards || Following) return;
        // Playing onfire, it burns on in the fire (R10157-2): the burn that spends its health ends it sooner.
        if (State == OrcState.Dying && onFire) { Burn(hazards, seconds); return; }
        if (!Alive) return;
        if (hazards.Touching(Feet) is not null) { Impale(); return; }
        Burn(hazards, seconds);
        if (!Alive) return;
        if (onFire && State is not (OrcState.KnockedDown or OrcState.Backstabbed)) { Die("onfire", ArenaHazards.OnFireSeconds, spent: false); return; }
        if (State == OrcState.KnockedDown) return;
        // Off a drop: a short one it drops down, as Source's NPCs fall; a deep one it falls to its death. With no ground
        // below at all it's off the arena (a check's place out of the way): nothing to fall onto.
        if (hazards.Drop(Feet) is not float drop) return;
        if (drop <= ArenaHazards.StepDown)
        {
            if (drop > 0) Feet -= Vector3.UnitY * drop;
            fallSpeed = 0;
            return;
        }
        if (fallSpeed == 0 && ArenaHazards.Lethal(drop)) { FallToDeath(State == OrcState.Pushed ? pushDirection : Facing, drop); return; }
        fallSpeed += Gravity * seconds;
        Feet -= Vector3.UnitY * MathF.Min(fallSpeed * seconds, drop);
    }

    /// <summary>
    /// A fire's burn where it stands: <see cref="ArenaHazards.FireDamage"/> every <see cref="ArenaHazards.FireEvery"/> s
    /// within it, the first as it steps in, and it's on fire. Dead of it, it's down.
    /// </summary>
    void Burn(ArenaHazards hazards, float seconds)
    {
        if (hazards.FireAt(Feet) is null) { burnLeft = 0; return; }
        if ((burnLeft -= seconds) > 0) return;
        burnLeft = ArenaHazards.FireEvery;
        onFire = true;
        if (Alive) { Damage(ArenaHazards.FireDamage); return; }
        Taken += MathF.Min(MathF.Max(Health, 0), ArenaHazards.FireDamage);
        if ((Health -= ArenaHazards.FireDamage) <= 0) Dead();
    }

    /// <summary>Source's <c>sv_gravity</c>, 800 units/s², in metres: how an NPC falls a short drop.</summary>
    const float Gravity = 800 * SourceSpace.MetresPerUnit;

    /// <summary>The arena's hazards (#10157), which it meets as the donor's NPCs do; null in a lab without them.</summary>
    internal ArenaHazards? Hazards { get; set; }

    /// <summary>Its wait until the next attack starts once for each attack that has ended, however it ended.</summary>
    void AttackEnded()
    {
        if (!wasAttacking || State == OrcState.Attack) return;
        mind.Attacked(Wounded);
        wasAttacking = false;
    }

    /// <summary>
    /// Whether an orc playing <paramref name="sequence"/> can't be backstabbed: on guard (<c>combatidle</c>, the lab's orc
    /// standing ready: 0 of 2262 recorded ticks in reach) or getting up after a knockdown (<c>StandUp_*</c>: 24 of 472).
    /// </summary>
    internal static bool Guarded(string sequence) =>
        sequence.Equals("combatidle", StringComparison.OrdinalIgnoreCase) || sequence.StartsWith("StandUp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a backstab can take it: its .qct's <c>backstable</c> (1 for the orc).</summary>
    internal bool Backstable { get; }

    /// <summary>
    /// A backstab has it (#10148): it plays <c>die_backstabbed</c>, left at 1 health, and fights no more; the backstab's
    /// blow then kills it (<see cref="Damage"/>).
    /// </summary>
    internal void Backstabbed()
    {
        if (!Alive || Following) return;
        Health = MathF.Min(Health, 1);
        Layer = null;
        Play(OrcState.Backstabbed, body.Model.Info.Clips.ContainsKey("die_backstabbed") ? "die_backstabbed" : "combatidle");
    }

    /// <summary>Seconds it stays frozen (the freeze spell's first stage, #10166); 0 when it isn't.</summary>
    internal float FrozenFor { get; private set; }
    float dyingLeft;

    /// <summary>Freezes it where it stands for <paramref name="seconds"/>.</summary>
    internal void Freeze(float seconds)
    {
        if (!Alive || Following) return;
        perception.Struck();
        FrozenFor = MathF.Max(FrozenFor, seconds);
    }

    /// <summary>Seconds it stays weakened (the weaken spell, #10167): wounded, striking with its wounded attacks only; 0 when it isn't.</summary>
    internal float WeakenedFor { get; private set; }

    /// <summary>Weakens it for <paramref name="seconds"/>: aware of the player, it fights (an unaware one stands up from its idle, R10167-2).</summary>
    internal void Weaken(float seconds)
    {
        if (!Alive || Following) return;
        Rouse();
        WeakenedFor = MathF.Max(WeakenedFor, seconds);
    }

    /// <summary>A spell cast on it makes it aware and, idling unaware, leaves its idle to fight, as a blow does (<see cref="Damage"/>).</summary>
    void Rouse()
    {
        perception.Struck();
        if (State == OrcState.Quiet) Play(OrcState.Ready, "combatidle");
    }

    /// <summary>Seconds it stays charmed (the charm spell, #10167): on the player's side, fighting its own squad; 0 when it isn't.</summary>
    internal float CharmedFor { get; private set; }

    /// <summary>Charms it for <paramref name="seconds"/>, aware, out of its idle (R10167-2).</summary>
    internal void Charm(float seconds)
    {
        if (!Alive || Following) return;
        Rouse();
        CharmedFor = MathF.Max(CharmedFor, seconds);
    }

    float regenClock;

    /// <summary>What blows have taken of its health since it spawned (its regeneration aside, #10348).</summary>
    internal float Taken { get; private set; }

    /// <summary>
    /// Its <c>.qct</c>'s <c>life_regen</c> (0.25) every <see cref="RegenSeconds"/> while it lives short of its health
    /// (#10348): a shrunk orc's climbed from 1 so, and the hurt orcs of the support-spells and attack-spells recordings.
    /// </summary>
    void Regenerate(float seconds)
    {
        if (Following || !Alive) return;
        for (regenClock += seconds; regenClock >= RegenSeconds; regenClock -= RegenSeconds)
            if (Health < mind.Data.Health) Health = MathF.Min(mind.Data.Health, Health + mind.Data.LifeRegen);
    }

    /// <summary>
    /// How often its health rises by its <c>life_regen</c>: every 2 s, as measured (the recorded rises 1.92 or 2.04 s
    /// apart, 2.00 on average over 145 of them; the settings' <c>life_time_regen</c> is 2, said to be the player's).
    /// </summary>
    internal const float RegenSeconds = 2;

    /// <summary>It dies <paramref name="seconds"/> from now (a spell's blow left it at 1; #10166).</summary>
    internal void DieIn(float seconds)
    {
        if (Alive && !Following && dyingLeft <= 0) dyingLeft = seconds;
    }

    /// <summary>
    /// Stuns it as a stunning blow from <paramref name="from"/> does, with no dice: the lightning bolt stunned every orc it
    /// struck (#10166).
    /// </summary>
    internal void Stun(Vector3 from)
    {
        if (!Alive || Following || State is OrcState.KnockedDown or OrcState.Backstabbed or OrcState.Stunned) return;
        Vector3 toward = (from - Feet) with { Y = 0 };
        float ahead = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), Facing) : 1;
        var left = new Vector3(Facing.Z, 0, -Facing.X);
        float side = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), left) : 0;
        Layer = null;
        stunLeft = stagger.Data.StunSeconds;
        Play(OrcState.Stunned, $"hit_stunned_{(ahead >= .707f ? "front" : ahead <= -.707f ? "back" : side > 0 ? "left" : "right")}");
    }

    /// <summary>The verdict and blow are the combat's; this takes their outcome.</summary>
    internal void Damage(float amount)
    {
        if (!Alive || Following) return;
        // A blow makes it fight at once (#10154), the reaction its blow plays aside.
        perception.Struck();
        if (State == OrcState.Quiet) Play(OrcState.Ready, "combatidle");
        Taken += MathF.Min(MathF.Max(Health, 0), amount);
        Health -= amount;
        if (Health <= 0) Dead();
    }

    /// <summary>
    /// Knocked down: with the scene's dynamics world it falls as a ragdoll from the pose it was drawn in (#10216), pushed
    /// along <paramref name="push"/> with <paramref name="up"/> of it upward; else as the stand-in, lying on its back
    /// (<paramref name="onBack"/>) or front.
    /// </summary>
    void KnockDown(Vector3 push, float up, bool onBack)
    {
        Layer = null;
        knockedFor = 0;
        if (RagdollWorld is not null)
        {
            ReleaseRagdoll();
            ragdollPending = true;
            ragdollSeconds = 0;
            ragdollPush = push;
            ragdollUp = up;
        }
        groundSide = onBack ? "back" : "front";
        fellFromY = Feet.Y;
        groundLeft = stagger.Data.KnockedDownSeconds + RagdollFallSeconds;
        fallFrom = Clip;
        fallFromCycle = Cycle;
        fallElapsed = 0;
        // A ragdoll spawns from the pose last drawn: it holds the pose it was struck in until then, and takes the
        // get-up's first frame (under the ragdoll) once it has.
        if (ragdollPending) State = OrcState.KnockedDown;
        else Play(OrcState.KnockedDown, $"StandUp_{groundSide}_firstframe");
    }

    /// <summary>How long a dead orc is drawn, seconds: its body, as it died, and then none.</summary>
    internal const float DrawnDead = .5f;

    /// <summary>
    /// Dead: down until it respawns, drawn no more after <see cref="DrawnDead"/>. Till then it stays as it died
    /// (R10157-1): its ragdoll where it came to rest, else its clip held where it ended (impaled, burnt, lying at the foot
    /// of a fall), never back on its feet.
    /// </summary>
    void Dead()
    {
        Health = 0;
        Layer = null;
        hitLayer = null;
        downSeconds = 0;
        (onFire, fatalFall, fallSpeed) = (false, false, 0);
        staggered = false;
        State = OrcState.Down;
    }

    /// <summary>
    /// Dies playing <paramref name="clip"/> for <paramref name="seconds"/> (#10157): impaled on spikes (<c>Die_Impaled</c>),
    /// or on fire (<c>onfire</c>). It's down when the clip has played: impaled, its health is spent at once; on fire, it
    /// keeps it, and the fire may burn it out first (R10157-2).
    /// </summary>
    void Die(string clip, float seconds, bool spent = true)
    {
        if (!Alive || Following) return;
        if (spent)
        {
            Taken += MathF.Max(Health, 0);
            Health = 0;
        }
        Layer = null;
        hitLayer = null;
        ReleaseRagdoll();
        dieLeft = seconds;
        Play(OrcState.Dying, ClipNamed(clip) ?? "combatidle");
    }

    /// <summary>Impaled on a spike rack it touched (#10157): the trap's 10000 damage, and <c>Die_Impaled</c> where it stands.</summary>
    internal void Impale() => Die("Die_Impaled", body.Model.Info.Clips.TryGetValue("Die_Impaled", out var c) ? c.Seconds : 2.7f);

    /// <summary>
    /// Thrown down by what its push met (#10157): a ragdoll pushed along <paramref name="along"/>, more up than a blow's
    /// (<see cref="ArenaHazards.WallFallUp"/>), on its back when it was pushed backwards.
    /// </summary>
    internal void WallFall(Vector3 along)
    {
        if (!Alive || Following || State is OrcState.KnockedDown or OrcState.Backstabbed) return;
        along.Y = 0;
        KnockDown(along, ArenaHazards.WallFallUp, Vector3.Dot(along, Facing) <= 0);
    }

    /// <summary>
    /// Falls off a drop of <paramref name="metres"/> (null: none below) that kills it (#10157): a ragdoll carried on the
    /// way it was going, dead where it comes to rest; as the stand-in, dropped there.
    /// </summary>
    void FallToDeath(Vector3 along, float? metres)
    {
        fatalFall = true;
        KnockDown(along, 0, Vector3.Dot(along, Facing) <= 0);
    }

    /// <summary>
    /// A blow's reaction (#10153; <see cref="OrcReactions"/>): a small hit over what it does, or a hit or push in its
    /// place, which cuts short an attack, a parry or a stun (every recorded stun a blow cut short), as the donor's did.
    /// None while it lies, gets up, dodges, is backstabbed or dies: the recorded orcs played none dodging (11 blows).
    /// A push slides it <paramref name="pushMetres"/> along <paramref name="pushDirection"/> (a kick's, a bash's);
    /// with none it moves by the clip's root motion.
    /// </summary>
    internal void React(OrcReaction reaction, Vector3 pushDirection = default, float pushMetres = 0)
    {
        if (!Alive || Following || reaction.Clip is null || State is OrcState.Down or OrcState.KnockedDown or OrcState.Backstabbed or OrcState.Dodging) return;
        string? clip = ClipNamed(reaction.Clip);
        if (clip is null) return;
        if (reaction.Overlay)
        {
            hitLayer = clip;
            hitLayerCycle = 0;
            lastSmallHit = clock;
            return;
        }
        Layer = null;
        if (reaction.Push && pushMetres > 0) Push(pushDirection, pushMetres, clip);
        else Play(OrcState.Hurt, clip);
    }

    /// <summary>
    /// A blow's reaction: the procedural one (#10382, <see cref="OrcFlinch"/>) when <c>orc.flinch.on</c>, else the
    /// authored clip the rule picked (<paramref name="authored"/>).
    /// </summary>
    internal void React(ReactionBlow blow, OrcReaction authored, Vector3 pushDirection = default, float pushMetres = 0)
    {
        if (tuning.Flinch is { On: true } f) Flinch(f, blow);
        else if (authored.Clip is not null) React(authored, pushDirection, pushMetres);
    }

    /// <summary>
    /// The procedural reaction (#10382): the blow leans its torso over whatever it plays; past its balance its action is
    /// cut short and, playing no reaction clip, it slides and turns until it has its footing back. None where the authored
    /// rule plays none (lying, getting up, dodging, backstabbed, dead).
    /// </summary>
    internal FlinchOutcome Flinch(FlinchTuning rules, ReactionBlow blow)
    {
        if (!Alive || State is OrcState.Down or OrcState.KnockedDown or OrcState.Backstabbed or OrcState.Dodging) return default;
        var outcome = flinch.Hit(rules, blow, State == OrcState.Stunned, Facing);
        // Followed in a replay (R10382-1), the recording moves and poses it: the lean is drawn over the recorded pose, and
        // what the reaction would have done is kept for the replay's report.
        if (Following)
        {
            FollowedFlinches.Add(outcome);
            // The reaction governs (R10382-1): cut short, the recorded orc's action stops until it has its footing back.
            if (outcome.Interrupted && Alive)
            {
                if (!staggered) recorded = new(Clip, Cycle, followRate, Layer, LayerCycle, overlays);
                Layer = null;
                hitLayer = null;
                overlays = [];
                Play(OrcState.Hurt, "combatidle");
                staggered = true;
                TakenAt = (Feet, Facing);
            }
            return outcome;
        }
        if (!outcome.Interrupted) return outcome;
        Layer = null;
        hitLayer = null;
        Play(OrcState.Hurt, "combatidle");
        staggered = true;
        return outcome;
    }

    /// <summary>Each procedural reaction to a blow while it was followed in a replay (R10382-1), in order.</summary>
    internal List<FlinchOutcome> FollowedFlinches { get; } = [];

    /// <summary>Where a replayed orc stood and faced as the procedural reaction last took it over, and as it gave it back (R10382-1).</summary>
    internal (Vector3 Feet, Vector3 Facing) TakenAt { get; private set; }

    /// <inheritdoc cref="TakenAt"/>
    internal (Vector3 Feet, Vector3 Facing) ReleasedAt { get; private set; }

    /// <summary>Why the procedural reaction last gave a replayed orc back: its footing back, or the recording's doing.</summary>
    internal string ReleasedFor { get; private set; } = "";

    /// <summary>The procedural reaction's state (#10382).</summary>
    internal OrcFlinch Flinching => flinch;

    /// <summary>Off its footing from a procedural reaction (#10382): its action cut short, playing no reaction clip.</summary>
    internal bool Staggered => staggered && State == OrcState.Hurt;

    /// <summary>Seconds since its last small hit began.</summary>
    internal double SinceSmallHit => clock - lastSmallHit;

    /// <summary>The small hit playing over it, if any.</summary>
    internal string? HitLayer => hitLayer;

    /// <summary>The model's clip by a name in any case (the model's own: <c>Hit_head_Front_small</c>, <c>Push_Body_back_small</c>).</summary>
    string? ClipNamed(string name) =>
        body.Model.Info.Clips.ContainsKey(name) ? name : body.Model.Info.Clips.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A kick or a shield bash: pushed back along <paramref name="direction"/> by <paramref name="metres"/>, attack
    /// interrupted, playing <paramref name="clip"/> (a bashed orc plays <c>Push_*_Front_small</c>); by the clip's own root
    /// motion for <see cref="ClipPush"/> (a kick's, #10157).
    /// </summary>
    internal const float ClipPush = float.PositiveInfinity;

    internal void Push(Vector3 direction, float metres, string clip = "Push_Body_back_small")
    {
        if (!Alive || Following || State == OrcState.KnockedDown) return;
        direction.Y = 0;
        pushDirection = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : -Facing;
        // A kick's push is its clip's own root motion (#10157): Push_Front_medium 90 units back, the sides' 60, the back's 76.
        pushRoot = float.IsPositiveInfinity(metres);
        pushLeft = pushRoot ? 0 : metres;
        Layer = null;
        Play(OrcState.Pushed, clip);
    }

    /// <summary>
    /// Wounded, which makes stuns and falls likelier: below its <c>life_tohurt</c> share of its health, or out of
    /// stamina (R10151-2: server.dll's one flag for both), or weakened (#10167: the weakened orcs walked, strafed and
    /// struck in their wounded clips).
    /// </summary>
    internal bool Wounded => Alive && (Health <= stagger.Data.HurtFraction * mind.Data.Health || stamina.Exhausted || WeakenedFor > 0);

    /// <summary>Its awareness of the player (#10154).</summary>
    internal OrcPerception Perception => perception;

    /// <summary>Its alert level and NPC state as the damage record carries them: 0 and 1 unaware, 4 and 3 fighting.</summary>
    internal (int Level, int NpcState) Alert => (perception.AlertLevel, perception.Aware ? 3 : 1);

    /// <summary>The idles an unaware orc plays (<c>idle_0</c>–<c>idle_3</c>), as the recorded ones did before they heard the player.</summary>
    static readonly string[] IdleClips = ["idle_0", "idle_1", "idle_2", "idle_3"];

    /// <summary>Its stamina (R10151-2): spent by its parries, dodges and stuns, never restored.</summary>
    internal OrcStamina Stamina => stamina;

    /// <summary>What the stun and knockdown rules work from (#10152).</summary>
    internal OrcStaggerData StaggerData => stagger.Data;

    /// <summary>
    /// How long the donor's ragdoll fell and came to rest before the knocked-down time began, seconds: over 47 recorded
    /// knockdowns the orc lay a median 3.3 s before getting up (2.9 to 4.2), of which <c>KnockedDownTime</c> is 2.
    /// The lab's stand-in lies that long at once (#10152).
    /// </summary>
    internal const float RagdollFallSeconds = 1.3f;

    /// <summary>
    /// After a player's blow has done its damage (#10152): whether it stuns or knocks the orc down
    /// (<see cref="OrcStagger"/>), and the reaction if so. <paramref name="from"/> is where the blow came from (the
    /// player's feet). Knocked down it lies on its back to a blow from in front, else on its front (the donor's lay on
    /// their backs 31 of 35 times: the stand-in doesn't roll); stunned it plays the side the blow came from.
    /// </summary>
    /// <param name="jumpDrop">For a jump attack, how far the player has dropped since leaving the ground, Source units: it fells only after 50.</param>
    internal Stagger Struck(AttackEvent? blow, double now, Vector3 from, bool staff, float stunMultiplier, float? jumpDrop = null)
    {
        if (!Alive || Following || State is OrcState.KnockedDown or OrcState.Backstabbed) return Stagger.None;
        var outcome = stagger.Decide(blow, now, Wounded, staff, stunMultiplier, jumpDrop);
        if (outcome == Stagger.None || State == OrcState.Stunned && outcome == Stagger.Stunned) return Stagger.None;
        Vector3 toward = (from - Feet) with { Y = 0 };
        float ahead = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), Facing) : 1;
        Layer = null;
        if (outcome == Stagger.KnockedDown)
        {
            // Pushed as the move's impact goes (#10153's impactdir: X ahead of the attacker, Y to its left), so a power
            // attack's sideways swipe rolls it.
            var away = (Feet - from) with { Y = 0 };
            away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : -Facing;
            var attackerLeft = new Vector3(away.Z, 0, -away.X);
            var impact = away * (blow?.ImpactX ?? 1) + attackerLeft * (blow?.ImpactY ?? 0);
            KnockDown(impact.LengthSquared() > 1e-6f ? impact : away, OrcRagdoll.Rise, ahead >= 0);
        }
        else
        {
            var left = new Vector3(Facing.Z, 0, -Facing.X);
            float side = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), left) : 0;
            string from_ = ahead >= .707f ? "front" : ahead <= -.707f ? "back" : side > 0 ? "left" : "right";
            stunLeft = stagger.Data.StunSeconds;
            stamina.Spend(stamina.Data.Stun);
            Play(OrcState.Stunned, $"hit_stunned_{from_}");
        }
        return outcome;
    }

    /// <summary>Starts its dice again (mind, stagger, defence) from their seeds.</summary>
    internal void Reseed()
    {
        mind.Reset(reseed: true);
        stagger.Reset(reseed: true);
        defence.Reset(reseed: true);
    }

    /// <summary>
    /// How long a knockdown's fall takes, seconds, before it lies: authored (R10152-2). The orc's model has no fall
    /// clip (the donor drops a <c>prop_ragdoll</c>, #10216, and its NPC waits on the get-up's first frame), so the pose it
    /// was struck in blends into <c>StandUp_&lt;side&gt;_firstframe</c>, its root tipping from upright to lying; a body
    /// falling its 1.1 m from rest takes about half a second. The lie that follows keeps the donor's timing.
    /// </summary>
    internal const float FallSeconds = .6f;

    /// <summary>Knocked down and still falling (<see cref="FallSeconds"/>).</summary>
    internal bool Falling => Alive && !Following && State == OrcState.KnockedDown && Clip.EndsWith("_firstframe", StringComparison.Ordinal)
        && (ragdoll is not null || ragdollPending ? !ragdollSettled : fallElapsed < FallSeconds);

    /// <summary>How far into its fall the orc is, 0 to 1, eased in and out.</summary>
    float FallWeight => Falling ? MathF.Min(1, fallElapsed / FallSeconds) is var t ? t * t * (3 - 2 * t) : 1 : 1;

    /// <summary>Lying on the ground: past halfway through its fall, until it begins to get up.</summary>
    internal bool Lying => Alive && !Following && State == OrcState.KnockedDown && Clip.EndsWith("_firstframe", StringComparison.Ordinal)
        && (ragdoll is not null ? ragdollSeconds >= RagdollLyingAfter : !ragdollPending && FallWeight >= .5f);

    /// <summary>
    /// The orc's body as the player's meets it (R10152-2), a world-aligned box (the Engine's character obstacles are):
    /// standing, 0.8 m across and 1.8 m tall around its feet; lying, 0.6 m high around a body 0.9 m across and 2.1 m
    /// long along its facing, from behind its head to its toes (<c>StandUp_*_firstframe</c> spans −0.9 to 1.2 m along
    /// its facing and rises 0.6 m).
    /// </summary>
    internal (Vector3 Center, Vector3 HalfExtents) Hull
    {
        get
        {
            // Shrunk (#10348), its box at the small model's scale.
            if (!Lying) return (Feet + Vector3.UnitY * .9f * Scale, new Vector3(.4f, .9f, .4f) * Scale);
            var forward = Vector3.Normalize(Facing with { Y = 0 });
            var side = new Vector3(forward.Z, 0, -forward.X);
            Vector3 Half(Vector3 axis, float extent) => new(MathF.Abs(axis.X) * extent, MathF.Abs(axis.Y) * extent, MathF.Abs(axis.Z) * extent);
            return (Feet + Vector3.UnitY * .3f + forward * .15f, Half(forward, 1.05f) + Half(side, .45f) + Vector3.UnitY * .3f);
        }
    }

    /// <summary>Turns the orc to face <paramref name="direction"/> (a developer placement: an unaware orc keeps it).</summary>
    internal void TurnTo(Vector3 direction)
    {
        direction.Y = 0;
        if (direction.LengthSquared() > 1e-6f) Facing = Vector3.Normalize(direction);
    }

    /// <summary>
    /// The scene's dynamics world, where a knocked-down orc falls as a ragdoll (#10216); null for the stand-in fall
    /// (<see cref="FallSeconds"/>), as in a lab without a scene.
    /// </summary>
    internal DynamicsWorld? RagdollWorld { get; set; }

    /// <summary>
    /// The longest a ragdoll falls before its lie is counted, seconds, if its bodies haven't all slept: the donor's
    /// lay a median 3.3 s before getting up (2.9 to 4.2), of which <c>KnockedDownTime</c> is 2, the rest its
    /// <c>prop_ragdoll</c> falling and coming to rest (<see cref="RagdollFallSeconds"/>).
    /// </summary>
    internal const float RagdollFallLimit = RagdollFallSeconds;

    /// <summary>The ragdoll's blend back into the get-up's first frame, seconds.</summary>
    internal const float RagdollBlendBack = .4f;

    /// <summary>How long a knockdown waits for the orc's pose to be drawn before it falls as the stand-in, seconds.</summary>
    const float RagdollDrawWait = .1f;

    /// <summary>From the blow until the ragdolled body counts as lying for the player's, seconds.</summary>
    const float RagdollLyingAfter = .3f;

    /// <summary>Every ragdolled knockdown since the lab started: the side its first frame took, the side it got up from, and the seconds from the blow to getting up.</summary>
    internal List<RagdollKnockdown> Knockdowns { get; } = [];

    /// <summary>Its ragdoll's state: falling, lying, or blending back; and the side it fell to.</summary>
    internal (bool Ragdolled, bool Settled, float Blend, string FellAs, string LiesOn) RagdollState =>
        (ragdoll is not null, ragdollSettled, ragdoll?.Blend ?? 0, fallSide, groundSide);

    /// <summary>Its ragdoll's bodies now, a developer readout.</summary>
    internal string RagdollBodies => ragdoll?.Describe() ?? "no ragdoll";

    /// <summary>
    /// A ragdolled knockdown's step: it spawns once the orc's pose has been drawn, falls and rests (until its bodies
    /// sleep, or <see cref="RagdollFallLimit"/>), lies for <c>KnockedDownTime</c>, then takes the get-up's first frame
    /// where and as it lies (on its back when its chest faces up), blends back into it and gets up on that side.
    /// </summary>
    void StepRagdoll(float seconds)
    {
        if (ragdollPending)
        {
            // Undrawn (stepped without the scene's draw, as the calibrations and checks do), it falls as the stand-in.
            if (RagdollWorld is null || OrcRagdoll.Spawn(engine, RagdollWorld, body, Facing, ragdollPush, ragdollUp) is not { } spawned)
            {
                if ((ragdollSeconds += seconds) <= RagdollDrawWait) return;
                ragdollPending = false;
                fallElapsed = 0;
                Play(OrcState.KnockedDown, $"StandUp_{groundSide}_firstframe");
                return;
            }
            Play(OrcState.KnockedDown, $"StandUp_{groundSide}_firstframe");
            ragdoll = spawned;
            ragdollPending = false;
            ragdollSettled = false;
            ragdollSeconds = 0;
            fallSide = groundSide;
        }
        if (ragdoll is null) return;
        ragdollSeconds += seconds;
        var (side, pelvis, facing) = ragdoll.Lie();
        if (ragdoll.Blend >= 1)
        {
            // Its feet follow its pelvis, on the ground under it (#10157: down a drop it fell off), else where it fell from.
            Feet = pelvis with { Y = Hazards?.Drop(pelvis) is float ground ? pelvis.Y - ground : Feet.Y };
            if (!ragdollSettled && (ragdoll.Read().Resting || ragdollSeconds >= RagdollFallLimit))
            {
                ragdollSettled = true;
                lieLeft = stagger.Data.KnockedDownSeconds;
                // Come to rest a lethal drop below where it fell (#10157), it's dead.
                if (fatalFall || ArenaHazards.Lethal((fellFromY - Feet.Y) is var dropped && dropped > 0 ? dropped : 0)) { Dead(); return; }
            }
            if (ragdollSettled && (lieLeft -= seconds) <= 0)
            {
                groundSide = side;
                Facing = facing;
                Play(OrcState.KnockedDown, $"StandUp_{groundSide}_firstframe");
                ragdoll.SetBlend(1 - seconds / RagdollBlendBack);
            }
            return;
        }
        ragdoll.SetBlend(ragdoll.Blend - seconds / RagdollBlendBack);
        if (ragdoll.Blend > 0) return;
        // Blended back into the first frame: it gets up in the same step, so no pose between shows it standing as it was
        // struck (R10216-1: the stand-in's fall and lie are spent).
        ReleaseRagdoll();
        groundLeft = 0;
        fallElapsed = FallSeconds;
        Play(OrcState.KnockedDown, $"StandUp_{groundSide}");
        Knockdowns.Add(new RagdollKnockdown(fallSide, groundSide, knockedFor));
    }


    void ReleaseRagdoll()
    {
        ragdoll?.Dispose();
        ragdoll = null;
        ragdollPending = false;
    }

    /// <summary>Turns it to face <paramref name="direction"/> (flat): a developer placement.</summary>
    internal void Face(Vector3 direction)
    {
        var flat = direction with { Y = 0 };
        if (flat.LengthSquared() > 1e-8f) Facing = Vector3.Normalize(flat);
    }

    /// <summary>A squad-mate saw the player (R10154-1): unaware, it comes to fight <see cref="OrcPerceptionData.CallDelay"/> later.</summary>
    internal void Call() => perception.Call();

    /// <summary>Moves the orc's feet (a developer placement).</summary>
    internal void PlaceAt(Vector3 feet) => Feet = feet;

    /// <summary>
    /// A new orc. Its dice go on from the last one's (a fresh roll sequence each life would replay its first rolls);
    /// <see cref="Reseed"/> starts them again, as a fight's reset does, so fights repeat.
    /// </summary>
    internal void Spawn()
    {
        ReleaseRagdoll();
        Feet = Shared(tuning.Spawn);
        Health = mind.Data.Health;
        FrozenFor = dyingLeft = WeakenedFor = CharmedFor = regenClock = Taken = 0;
        (onFire, fatalFall, pushRoot, burnLeft, dieLeft, fallSpeed) = (false, false, false, 0, 0, 0);
        ResetArchery();
        ResetCasting();
        holdsShield = OwnShield;
        ResetShrink();
        Layer = null;
        mind.Reset();
        stagger.Reset();
        defence.Reset();
        stamina.Reset();
        perception.Reset(!tuning.SpawnUnaware);
        seenSwing = answeredSwing = -1;
        hitLayer = null;
        lastSmallHit = double.NegativeInfinity;
        wasAttacking = false;
        flinch.Reset();
        if (perception.Aware) Play(OrcState.Approach, "CombatWalk");
        else Play(OrcState.Quiet, "idle_0");
    }

    /// <summary>The orc as Dark Messiah's combat model sees it.</summary>
    internal Party Party() => new(Kind.Class, Clip, Layer is null ? [] : [Layer], Cycle, "#2", Kind.Model);

    /// <summary>
    /// Holds it in <paramref name="clip"/> at <paramref name="cycle"/>, facing <paramref name="yawDegrees"/> (0 along -Z, the
    /// lab's view yaw), frozen until it spawns again (<c>lab.orc.pose</c>, #10395: to pose it as a donor screenshot was).
    /// </summary>
    internal void PoseAt(string clip, float cycle, float yawDegrees)
    {
        Clip = ClipNamed(clip) ?? clip;
        Cycle = Math.Clamp(cycle, 0, 1);
        Layer = null;
        hitLayer = null;
        float yaw = yawDegrees * MathF.PI / 180;
        Facing = new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        FrozenFor = float.MaxValue;
    }

    /// <summary>The landmark pairs its grip and shield are judged by (#10395, <see cref="HeldLandmarks"/>), and what they're on.</summary>
    internal (string Key, LandmarkPair[] Pairs) Landmarks()
    {
        var pairs = new List<LandmarkPair>();
        if (holdsCleaver) pairs.AddRange(HeldLandmarks.Grip("orc", HeldLandmarks.Hand(engine, body.Model, body.Id, HeldLandmarks.OrcRight), cleaver.Model, cleaver.Id, HeldLandmarks.Hold.OutOfPalm));
        if (holdsShield && HeldLandmarks.Strap(body.Model, body.Id, shield.Model, shield.Id) is { } strap) pairs.Add(strap);
        return ($"{body.Id}:{holdsCleaver}:{holdsShield}", [.. pairs]);
    }

    /// <summary>
    /// Degrees its cleaver is drawn turned in its hand, about the item's Z (across its blade's flat), off the donor's hang
    /// (<c>lab.held.turn</c>): to see the grip's landmark pairs catch a turned hold (#10395). Drawn only; it strikes as hung.
    /// </summary>
    internal float CleaverTurn { get; set; }

    /// <summary>Where its cleaver and shield hang now (each null when it doesn't hold it), as they're drawn.</summary>
    internal (Transform? Cleaver, Transform? Shield) Held()
    {
        Present();
        var pose = Rig.Pose(engine, body, Placement, controls: leaning);
        return (holdsCleaver ? Rig.Attached(body.Model, pose, "RightHand") : null, holdsShield ? Rig.Attached(body.Model, pose, "LeftShield") : null);
    }

    /// <summary>Its body and items posed now, for a sweep: the body by hit boxes, the cleaver and shield by their hulls.</summary>
    internal List<PosedTarget> Targets()
    {
        if (!Alive) return [];
        Present();
        // Ragdolled, its hit boxes are where its bodies drew it.
        var drawn = ragdoll is not null ? engine.Animation.ReadJointPose(body.Instance) : default;
        // Leaning (#10382), its hit boxes and items are where the lean drew them.
        var pose = ragdoll is not null && drawn.Reported ? drawn.Joints.ToArray() : Rig.Pose(engine, body, Placement, controls: leaning);
        var targets = new List<PosedTarget> { new(body.Model.Info, body.Model.Joints, pose) };
        foreach (var (item, attachment, held) in new[] { (cleaver, "RightHand", holdsCleaver), (shield, "LeftShield", holdsShield), (bow, "LeftHand_bow", HoldsBow) })
            if (held && Rig.Attached(body.Model, pose, attachment) is Transform at)
                targets.Add(new(item.Model.Info, item.Model.Joints, Rig.Rest(engine, item.Instance, at), Holder: 0));
        return targets;
    }

    // The body was in the last publish.
    bool published = true;

    /// <summary>The body and its items to draw; the items hang where the body's attachments put them.</summary>
    internal IEnumerable<AppearanceFact> Facts()
    {
        Present();
        if (State == OrcState.Down && downSeconds > DrawnDead) { published = false; return []; }
        // Back after a death it wasn't drawn through (a reset, a respawn): the body first, its pose sampled once it's in
        // the published scene again, and the items from the next publish.
        if (!published) { published = true; return [body.Fact(Placement)]; }
        // Ragdolled, its items hang where its bodies drew it (#10216).
        var drawn = ragdoll is not null ? engine.Animation.ReadJointPose(body.Instance) : default;
        var pose = ragdoll is not null && drawn.Reported ? drawn.Joints.ToArray() : Rig.Pose(engine, body, Placement);
        pose = Lean(pose);
        var facts = new List<AppearanceFact> { body.Fact(Placement) };
        if (holdsCleaver && Rig.Attached(body.Model, pose, "RightHand") is Transform hand)
            facts.Add(cleaver.Fact(CleaverTurn == 0 ? hand : hand with { Rotation = hand.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, CleaverTurn * MathF.PI / 180) }));
        if (holdsShield && Rig.Attached(body.Model, pose, "LeftShield") is Transform wrist) facts.Add(shield.Fact(wrist));
        if (HoldsBow && Rig.Attached(body.Model, pose, "LeftHand_bow") is Transform bowHand) facts.Add(bow.Fact(bowHand));
        return facts;
    }

    /// <summary>Its head's height over its feet as it's drawn now, alive or dead (its ragdoll's, else its clip's): a check's readout.</summary>
    internal float DrawnHead()
    {
        Present();
        var drawn = ragdoll is not null ? engine.Animation.ReadJointPose(body.Instance) : default;
        var pose = ragdoll is not null && drawn.Reported ? drawn.Joints.ToArray() : Rig.Pose(engine, body, Placement);
        return pose[body.Model.Joints["SK_Head"]].World.Translation.Y - Feet.Y;
    }

    /// <summary>Where its head is in the clip's pose and in the pose its items and hit boxes take (leaned, #10382).</summary>
    internal (Vector3 Clip, Vector3 Posed) Head()
    {
        int head = body.Model.Joints["SK_Head"];
        return (Rig.Pose(engine, body, Placement)[head].World.Translation, Targets()[0].Pose[head].World.Translation);
    }

    /// <summary>The joint turns its lean draws now (#10382), over the clip's pose.</summary>
    internal JointOverride[] LeanTurns() => flinch.Overrides(body.Model, Rig.Pose(engine, body, Placement), Facing);

    /// <summary>
    /// The procedural reaction's lean (#10382) as turns of its spine, neck and head over the clip's pose
    /// (<paramref name="pose"/>), cleared once it has stood back up or a ragdoll has it. Returns the pose leaned, which
    /// its items hang from.
    /// </summary>
    JointPose[] Lean(JointPose[] pose)
    {
        var turns = ragdoll is null && !ragdollPending ? flinch.Overrides(body.Model, pose, Facing) : [];
        if (turns.Length == 0 && !leaning) return pose;
        engine.Animation.SetPose(new(body.Instance, ReadOnlyMemory<TwoBoneIk>.Empty, turns));
        leaning = turns.Length > 0;
        return leaning ? Rig.Pose(engine, body, Placement, controls: true) : pose;
    }

    void Present()
    {
        // Followed, it is posed as the recording streamed it: every overlay at its weight.
        if (Following && overlays.Length > 0) body.Play(Clip, Cycle, overlays);
        // Waiting for its ragdoll: the pose it was struck in, held.
        else if (ragdollPending) body.Play(fallFrom, fallFromCycle);
        // Falling: the pose it was struck in gives way to the lying one.
        else if (Falling) body.Play(fallFrom, fallFromCycle, (Clip, 0, FallWeight));
        else
        {
            var over = new List<(string, float, float)>(2);
            if (Layer is not null) over.Add((Layer, LayerCycle, 1));
            if (hitLayer is not null) over.Add((hitLayer, hitLayerCycle, 1));
            body.Play(Clip, Cycle, [.. over]);
        }
    }

    /// <summary>A parry over its guard: one of <c>layer_parry_A</c>–<c>E</c> at random, never the last one again (server.dll's SetParryLayer).</summary>
    void Parry()
    {
        string[] layers = ["layer_parry_A", "layer_parry_B", "layer_parry_C", "layer_parry_D", "layer_parry_E"];
        var choices = layers.Where(l => l != lastParry && body.Model.Info.Clips.ContainsKey(l)).ToList();
        Layer = lastParry = choices.Count > 0 ? choices[random.Next(choices.Count)] : "layer_parry_B";
        LayerCycle = 0;
        Play(OrcState.Parry, "combatidle");
    }

    /// <summary>A dodge to one side (<c>Dodge_left</c> or <c>Dodge_right</c>): its root motion carries it 75 units, and it can't be touched in its window (#10198).</summary>
    void Dodge()
    {
        Layer = null;
        Play(OrcState.Dodging, random.Next(2) == 0 ? "Dodge_left" : "Dodge_right");
    }

    /// <summary>How the orc defends (#10151); its <c>Forced</c> answer sets every one (the smoke's checks).</summary>
    internal OrcDefence Defending => defence;

    void Play(OrcState state, string clip)
    {
        staggered = false;
        State = state;
        Clip = Resolve(Wounded ? Hurt(clip) : clip);
        Cycle = 0;
    }

    /// <summary>
    /// The clip of its body that <paramref name="clip"/> names (#10357): the orc's names, matched in any case
    /// (the necromancer's <c>Combatwalk</c>), its stunned loop <c>stunned_idle</c>, else its guard.
    /// </summary>
    string Resolve(string clip) =>
        ClipNamed(clip) ?? (Aliases.TryGetValue(clip, out var alias) ? ClipNamed(alias) : null) ?? ClipNamed("combatidle") ?? clip;

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase) { ["Stunned"] = "stunned_idle" };

    /// <summary>Whether it plays what <paramref name="clip"/> names on its body.</summary>
    bool Playing(string clip) => Clip == Resolve(clip);

    /// <summary>
    /// Its locomotion wounded (#10153): below <c>life_tohurt</c> (or exhausted) the recorded orcs walked in
    /// <c>Hurtwalk</c> and strafed with <c>Wounded_combatmove_*</c> (117 and 219 of their ticks there, no
    /// <c>CombatWalk</c> or <c>Combatmove_*_small</c> in the top of the list).
    /// </summary>
    internal static string Hurt(string clip) => clip switch
    {
        "CombatWalk" => "Hurtwalk",
        "Combatmove_left_small" => "Wounded_combatmove_left",
        "Combatmove_right_small" => "Wounded_combatmove_right",
        "Combatmove_backward_small" => "Wounded_combatmove_back",
        "Combatmove_forward_small" => "Wounded_combatmove_front",
        _ => clip,
    };

    void Advance(float seconds)
    {
        var clip = body.Model.Clip(Clip);
        // The cleaver's speed_attack; an archer's kick with the bow at its own (#10169).
        float rate = State == OrcState.Attack && !HoldsBow ? AttackSpeed : 1;
        Cycle += clip.Seconds > 0 ? seconds * rate / clip.Seconds : 1;
        if (Looping(Clip) && Cycle >= 1) Cycle -= MathF.Floor(Cycle);
        else Cycle = MathF.Min(Cycle, 1);
        if (Layer is not null)
        {
            var layer = body.Model.Clip(Layer);
            LayerCycle = MathF.Min(1, LayerCycle + (layer.Seconds > 0 ? seconds / layer.Seconds : 1));
        }
        if (hitLayer is not null)
        {
            var layer = body.Model.Clip(hitLayer);
            hitLayerCycle += layer.Seconds > 0 ? seconds / layer.Seconds : 1;
            if (hitLayerCycle >= 1) hitLayer = null;
        }
    }

    bool Looping(string clip) => clip.Equals("combatidle", StringComparison.OrdinalIgnoreCase) || clip.Equals("CombatWalk", StringComparison.OrdinalIgnoreCase)
        || clip.Equals("Hurtwalk", StringComparison.OrdinalIgnoreCase) || clip.Equals("Stunned", StringComparison.OrdinalIgnoreCase) || clip.Equals(Resolve("Stunned"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How near its feet come to the player's, Source units: two human hulls (16 each; the donor's player and NPC hulls
    /// stop each other). The recorded orcs began their closest attacks 33 to 37 units off (10th percentiles).
    /// </summary>
    internal const float BodyGap = 32;

    /// <summary>
    /// Waiting out its interval within its zones: backing off inside the red zone, else a strafe either way
    /// (<c>Combatmove_left_small</c>, <c>_right_small</c>) or its guard (<c>combatidle</c>), each played through. The
    /// recorded orcs between 20 and 140 units strafed (<c>Combatmove_*</c>) about four times as often as they idled.
    /// </summary>
    void Wait(float units)
    {
        if (units < mind.Data.Red) { Play(OrcState.Ready, "Combatmove_backward_small"); return; }
        float roll = mind.Roll();
        Play(OrcState.Ready, roll < .4f ? "Combatmove_left_small" : roll < .8f ? "Combatmove_right_small" : "combatidle");
    }

    /// <summary>
    /// Moves it by <paramref name="clip"/>'s root motion from cycle <paramref name="from"/> to <paramref name="to"/>
    /// (around the loop when <paramref name="to"/> wrapped), turned to its facing: forward along it, left of it.
    /// </summary>
    void RootMotion(string clip, float from, float to)
    {
        // The walk moves by the donor's ground speed instead (Step).
        if (clip is "CombatWalk" or "Hurtwalk") return;
        var played = body.Model.Clip(clip);
        if (played.Movement is not { Count: > 0 }) return;
        Vector2 moved = to >= from ? played.MovedAt(to) - played.MovedAt(from) : played.MovedAt(1) - played.MovedAt(from) + played.MovedAt(to);
        var left = new Vector3(Facing.Z, 0, -Facing.X);
        Feet += (Facing * moved.X + left * moved.Y) * SourceSpace.MetresPerUnit;
    }

    static Vector3 Shared(float[] xyz) => new(xyz[0], xyz[1], xyz[2]);

    public void Dispose()
    {
        ReleaseRagdoll();
        foreach (var b in bodies.Values) b?.Dispose();
        cleaver.Dispose();
        shield.Dispose();
        bow.Dispose();
    }
}
