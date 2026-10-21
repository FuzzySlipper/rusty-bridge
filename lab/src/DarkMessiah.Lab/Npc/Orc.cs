using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>What the lab's orc is doing.</summary>
internal enum OrcState { Approach, Ready, Attack, Parry, Hurt, Pushed, Down, Backstabbed, Stunned, KnockedDown, Dodging }

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
/// (mstudiomovement_t), as the donor's entity moves. Each attack part's event (2102, 2139) the step crosses is a blow. When not attacking it may parry a swing it sees coming (<c>layer_parry_B</c>
/// over its idle, which sets its defense state's parry bit), and it reacts to hits (<c>Hit_Body_Front_small</c>) and
/// kicks (<c>Push_Body_back_small</c>, pushed back). At no health it drops and comes back after a while.
/// </summary>
internal sealed class Orc : IDisposable
{
    internal const string Model = "models/npc/orc/npc_orc.mdl", Cleaver = "models/Items/Weapons/sword_orc/sword_orc.mdl",
        Shield = "models/items/armors/shield_orc/shield_orc.mdl";
    internal const ulong BodyId = 0x4C41_0010, CleaverId = 0x4C41_0011, ShieldId = 0x4C41_0012;
    static readonly int[] PartEvents = [2102, 2139];

    readonly IEngineContext engine;
    readonly DonorContent donor;
    readonly Puppet ownBody, cleaver, shield;
    // The body drawn and swept: the lab's own, or in a replay the recorded orc's model (R10177-2), one puppet per model.
    Puppet body;
    readonly Dictionary<string, Puppet?> bodies = new(StringComparer.OrdinalIgnoreCase);
    const ulong OtherBodyIds = 0x4C41_0200;
    readonly Random random = new(1);
    OrcTuning tuning;
    float downSeconds, pushLeft;
    readonly OrcMind mind;
    readonly OrcStagger stagger;
    readonly OrcDefence defence;
    // Its own clock, seconds, for the dodge's delay; and the parry layer it played last (it never plays one twice running).
    double clock;
    string lastParry = "";
    // A stun's and a knockdown's time left, and the side a knockdown lies on (back or front).
    float stunLeft, groundLeft;
    string groundSide = "back";
    Vector3 pushDirection;

    internal Orc(IEngineContext engine, DonorContent donor, OrcTuning tuning)
    {
        this.engine = engine;
        this.donor = donor;
        this.tuning = tuning;
        body = ownBody = new Puppet(engine, donor.Model(Model), BodyId);
        bodies[Model] = ownBody;
        cleaver = new Puppet(engine, donor.Model(Cleaver), CleaverId);
        shield = new Puppet(engine, donor.Model(Shield), ShieldId);
        AttackSpeed = donor.Number(Cleaver, "entity_data", "speed_attack") ?? 1;
        mind = new OrcMind(OrcMindData.Read(donor, Model, ownBody.Model.Info, donor.Combat));
        stagger = new OrcStagger(OrcStaggerData.Read(donor, Model));
        defence = new OrcDefence(OrcDefenceData.Read(donor, Model));
        Backstable = donor.Number(Model, "entity_data", "backstable") is > 0;
        Spawn();
    }

    /// <summary>It holds off: no attack starts (a developer switch, and the smoke's still orc).</summary>
    internal bool Hold { get; set; }

    /// <summary>What it fights by, from its .qct (#10150).</summary>
    internal OrcMindData Mind => mind.Data;

    internal float AttackSpeed { get; }
    internal Vector3 Feet { get; private set; }
    internal Vector3 Facing { get; private set; } = -Vector3.UnitZ;
    internal float Health { get; private set; }
    internal OrcState State { get; private set; }
    internal string Clip { get; private set; } = "combatidle";
    internal float Cycle { get; private set; }
    internal string? Layer { get; private set; }
    internal float LayerCycle { get; private set; }
    internal bool Alive => State != OrcState.Down;
    /// <summary>Its defense-state word as Dark Messiah's verdict reads it: bit 0x4 while parrying.</summary>
    internal uint DefenseState => State == OrcState.Parry ? CombatModel.ParryState : 0;
    internal DonorModel BodyModel => body.Model;
    internal Transform Placement => new(Feet, Rig.Facing(Facing, Vector3.UnitY), Vector3.One);
    /// <summary>The attacks it has made, and the blows that reached the player.</summary>
    internal int AttacksMade { get; private set; }

    internal void Retune(OrcTuning next) => tuning = next;

    /// <summary>Following a recorded orc (<see cref="Follow"/>): its own loop, reactions and pushes are off.</summary>
    internal bool Following { get; private set; }
    // The items it holds: its own kit, or the followed orc's. No recorded orc has had a shield (#10177).
    bool holdsCleaver = true, holdsShield = true;
    string? followed;
    float followRate = 1;
    // The followed orc's overlays as last streamed and played on since: what it is posed in (empty for its own loop).
    (string Clip, float Cycle, float Weight)[] overlays = [];
    // The overlays as last streamed: a change is taken as it arrives, whether or not the main sequence moved.
    IReadOnlyList<(string Clip, float Cycle, float Weight)>? streamedOverlays;
    readonly HashSet<(string Clip, int Part, float At)> landed = [];

    /// <summary>
    /// One replayed step: the orc stands, faces, plays and has the health the recording gives it, and plays on
    /// between streamed states at the recorded rate. Its state is read from what it plays: a parry layer is a parry
    /// (the verdict's parry bit), an attack sequence an attack, no health down. Returns the attack parts whose events
    /// it crossed, once per play of a clip.
    /// </summary>
    internal List<OrcBlow> Follow(OrcPose pose, float seconds)
    {
        Following = true;
        if (pose.Id != followed) { followed = pose.Id; landed.Clear(); }
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
        if (pose.Fresh && body.Model.Info.Clips.ContainsKey(pose.Sequence))
        {
            Clip = pose.Sequence;
            Cycle = pose.Cycle;
            followRate = pose.Rate > 0 ? pose.Rate : 1;
            Layer = pose.Layer is { } l && body.Model.Info.Clips.ContainsKey(l) ? l : null;
            LayerCycle = pose.LayerCycle;
        }
        else
        {
            var clip = body.Model.Clip(Clip);
            Cycle += clip.Seconds > 0 ? seconds * followRate / clip.Seconds : 1;
            Cycle = Looping(Clip) || clip.Looping ? Cycle - MathF.Floor(Cycle) : MathF.Min(Cycle, 1);
            if (Layer is not null) LayerCycle = MathF.Min(1, LayerCycle + (body.Model.Clip(Layer).Seconds is > 0 and var s ? seconds / s : 1));
        }
        if (pose.Overlays is { } streamed && (streamedOverlays is null || !streamed.SequenceEqual(streamedOverlays)))
        {
            streamedOverlays = streamed;
            overlays = [.. streamed.Where(o => body.Model.Info.Clips.ContainsKey(o.Clip))];
        }
        else
        {
            for (int i = 0; i < overlays.Length; i++)
            {
                var (name, at, weight) = overlays[i];
                var played = body.Model.Clip(name);
                at += played.Seconds > 0 ? seconds / played.Seconds : 1;
                overlays[i] = (name, played.Looping ? at - MathF.Floor(at) : MathF.Min(at, 1), weight);
            }
        }
        State = Health <= 0 ? OrcState.Down
            : Layer?.Contains("parry", StringComparison.OrdinalIgnoreCase) == true || Clip.Contains("parry", StringComparison.OrdinalIgnoreCase) ? OrcState.Parry
            : Clip.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || Layer?.StartsWith("layer_attack", StringComparison.OrdinalIgnoreCase) == true ? OrcState.Attack
            : OrcState.Ready;
        var blows = new List<OrcBlow>();
        if (State == OrcState.Down) return blows;
        // An attack plays as the main sequence, or, on the move, as a layer_attack_* overlay.
        if (Clip != clipBefore) landed.RemoveWhere(x => x.Clip == Clip);
        if (Layer != layerBefore) landed.RemoveWhere(x => x.Clip == Layer);
        Cross(Clip, Clip == clipBefore ? before : 0, Cycle, blows);
        if (Layer is not null) Cross(Layer, Layer == layerBefore ? layerCycleBefore : 0, LayerCycle, blows);
        return blows;
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
        puppet = donor.Read(Path.ChangeExtension(model, ".glb")) is null ? null : new Puppet(engine, donor.Model(model), OtherBodyIds + (ulong)bodies.Count);
        bodies[model] = puppet;
        return puppet;
    }

    /// <summary>Back to the lab's own loop after a replay.</summary>
    internal void StopFollowing()
    {
        Following = false;
        body = ownBody;
        holdsCleaver = holdsShield = true;
        followed = null;
        overlays = [];
        streamedOverlays = null;
        landed.Clear();
        Spawn();
    }

    /// <summary>
    /// One step: turns to the player, walks in, attacks, parries, reacts or lies down. Returns the blows whose part
    /// events this step crossed; whether they reach the player is the combat's to decide.
    /// </summary>
    /// <param name="swing">A player's swing starting this step, as the orc sees it (#10151).</param>
    internal List<OrcBlow> Step(float seconds, Vector3 playerFeet, IncomingSwing swing)
    {
        clock += seconds;
        var blows = new List<OrcBlow>();
        Vector3 toPlayer = playerFeet - Feet;
        toPlayer.Y = 0;
        float distance = toPlayer.Length();
        if (State is not (OrcState.Down or OrcState.Pushed or OrcState.Hurt or OrcState.Backstabbed or OrcState.Stunned or OrcState.KnockedDown) && distance > 1e-3f)
            Facing = toPlayer / distance;
        mind.Step(seconds, swing != IncomingSwing.None);
        float before = Cycle;
        string playing = Clip;
        Advance(seconds);
        if (State is OrcState.Approach or OrcState.Ready or OrcState.Attack or OrcState.Hurt or OrcState.Dodging) RootMotion(playing, before, Cycle);
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
        switch (State)
        {
            case OrcState.Down:
                downSeconds += seconds;
                if (downSeconds >= tuning.RespawnSeconds) Spawn();
                break;
            case OrcState.Approach:
                // It walks in to its green zone, or until an attack it may make lands from here.
                if (units <= data.Green || !Hold && mind.Ready && mind.Choose(units, tuning.AttackLandings) is not null) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Ready:
                var answer = defence.Decide(swing, clock);
                if (answer == Defence.Parry) Parry();
                else if (answer == Defence.Dodge) Dodge();
                else if (!Hold && mind.Ready && mind.Choose(units, tuning.AttackLandings) is { } attack)
                {
                    Play(OrcState.Attack, attack.Clip);
                    mind.Attacked();
                    AttacksMade++;
                }
                // Past its yellow zone, or ready to attack with nothing landing from here, it walks in.
                else if (units > data.Yellow || !Hold && mind.Ready && units > data.Green) Play(OrcState.Approach, "CombatWalk");
                // Ready and too close for any attack to land (pressed against the player at its hull's 32 units,
                // nearer than the uppercut's 34): it steps back to fight again (R10150-1), as the donor's orcs began
                // attacks out of Combatmove_backward_small.
                else if (!Hold && mind.Ready && units < mind.Nearest(tuning.AttackLandings) && Clip != "Combatmove_backward_small")
                    Play(OrcState.Ready, "Combatmove_backward_small");
                else if (done || Clip == "combatidle" && Cycle < before) Wait(units);
                break;
            case OrcState.Attack:
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
                float step = MathF.Min(pushLeft, seconds * 3);
                Feet += pushDirection * step;
                pushLeft -= step;
                if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Hurt:
                if (done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.Stunned:
                // hit_stunned_<side>, then Stunned until stunned_duration is spent, then Stunned_out.
                stunLeft -= seconds;
                if (Clip.StartsWith("hit_stunned", StringComparison.Ordinal) && done) Play(OrcState.Stunned, "Stunned");
                else if (Clip == "Stunned" && stunLeft <= 0) Play(OrcState.Stunned, "Stunned_out");
                else if (Clip == "Stunned_out" && done) Play(OrcState.Ready, "combatidle");
                break;
            case OrcState.KnockedDown:
                // Lying (StandUp_<side>_firstframe) while the donor's ragdoll would fall and rest, then getting up.
                groundLeft -= seconds;
                if (Clip.EndsWith("_firstframe", StringComparison.Ordinal) && groundLeft <= 0) Play(OrcState.KnockedDown, $"StandUp_{groundSide}");
                else if (!Clip.EndsWith("_firstframe", StringComparison.Ordinal) && done) Play(OrcState.Ready, "combatidle");
                break;
        }
        return blows;
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

    /// <summary>The verdict and blow are the combat's; this takes their outcome.</summary>
    internal void Damage(float amount)
    {
        if (!Alive || Following) return;
        Health -= amount;
        if (Health <= 0) { Health = 0; Layer = null; downSeconds = 0; Play(OrcState.Down, "combatidle"); }
        // A hit ends a stun (every recorded stun a blow cut short); one on the ground or getting up plays no reaction.
        else if (State is not (OrcState.Attack or OrcState.Backstabbed or OrcState.KnockedDown)) Play(OrcState.Hurt, "Hit_Body_Front_small");
    }

    /// <summary>
    /// A kick or a shield bash: pushed back along <paramref name="direction"/> by <paramref name="metres"/>, attack
    /// interrupted, playing <paramref name="clip"/> (a bashed orc plays <c>Push_*_Front_small</c>).
    /// </summary>
    internal void Push(Vector3 direction, float metres, string clip = "Push_Body_back_small")
    {
        if (!Alive || Following || State == OrcState.KnockedDown) return;
        direction.Y = 0;
        pushDirection = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : -Facing;
        pushLeft = metres;
        Layer = null;
        Play(OrcState.Pushed, clip);
    }

    /// <summary>Below its <c>life_tohurt</c> share of its health: wounded, which makes stuns and falls likelier.</summary>
    internal bool Wounded => Alive && Health <= stagger.Data.HurtFraction * mind.Data.Health;

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
    internal Stagger Struck(AttackEvent? blow, double now, Vector3 from, bool staff, float stunMultiplier)
    {
        if (!Alive || Following || State is OrcState.KnockedDown or OrcState.Backstabbed) return Stagger.None;
        var outcome = stagger.Decide(blow, now, Wounded, staff, stunMultiplier);
        if (outcome == Stagger.None || State == OrcState.Stunned && outcome == Stagger.Stunned) return Stagger.None;
        Vector3 toward = (from - Feet) with { Y = 0 };
        float ahead = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), Facing) : 1;
        Layer = null;
        if (outcome == Stagger.KnockedDown)
        {
            groundSide = ahead >= 0 ? "back" : "front";
            groundLeft = stagger.Data.KnockedDownSeconds + RagdollFallSeconds;
            Play(OrcState.KnockedDown, $"StandUp_{groundSide}_firstframe");
        }
        else
        {
            var left = new Vector3(Facing.Z, 0, -Facing.X);
            float side = toward.LengthSquared() > 1e-6f ? Vector3.Dot(Vector3.Normalize(toward), left) : 0;
            string from_ = ahead >= .707f ? "front" : ahead <= -.707f ? "back" : side > 0 ? "left" : "right";
            stunLeft = stagger.Data.StunSeconds;
            Play(OrcState.Stunned, $"hit_stunned_{from_}");
        }
        return outcome;
    }

    /// <summary>Moves the orc's feet (a developer placement).</summary>
    internal void PlaceAt(Vector3 feet) => Feet = feet;

    internal void Spawn()
    {
        Feet = Shared(tuning.Spawn);
        Health = mind.Data.Health;
        Layer = null;
        mind.Reset();
        stagger.Reset();
        defence.Reset();
        Play(OrcState.Approach, "CombatWalk");
    }

    /// <summary>The orc as Dark Messiah's combat model sees it.</summary>
    internal Party Party() => new("npc_orc_sword", Clip, Layer is null ? [] : [Layer], Cycle, "#2", Model);

    /// <summary>Its body and items posed now, for a sweep: the body by hit boxes, the cleaver and shield by their hulls.</summary>
    internal List<PosedTarget> Targets()
    {
        if (!Alive) return [];
        Present();
        var pose = Rig.Pose(engine, body, Placement);
        var targets = new List<PosedTarget> { new(body.Model.Info, body.Model.Joints, pose) };
        foreach (var (item, attachment, held) in new[] { (cleaver, "RightHand", holdsCleaver), (shield, "LeftShield", holdsShield) })
            if (held && Rig.Attached(body.Model, pose, attachment) is Transform at)
                targets.Add(new(item.Model.Info, item.Model.Joints, Rig.Rest(engine, item.Instance, at), Holder: 0));
        return targets;
    }

    /// <summary>The body and its items to draw; the items hang where the body's attachments put them.</summary>
    internal IEnumerable<AppearanceFact> Facts()
    {
        Present();
        if (!Alive && downSeconds > .5f) return [];
        var pose = Rig.Pose(engine, body, Placement);
        var facts = new List<AppearanceFact> { body.Fact(Placement) };
        if (holdsCleaver && Rig.Attached(body.Model, pose, "RightHand") is Transform hand) facts.Add(cleaver.Fact(hand));
        if (holdsShield && Rig.Attached(body.Model, pose, "LeftShield") is Transform wrist) facts.Add(shield.Fact(wrist));
        return facts;
    }

    void Present()
    {
        // Followed, it is posed as the recording streamed it: every overlay at its weight.
        if (Following && overlays.Length > 0) body.Play(Clip, Cycle, overlays);
        else if (Layer is null) body.Play(Clip, Cycle);
        else body.Play(Clip, Cycle, (Layer, LayerCycle, 1));
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
        State = state;
        Clip = clip;
        Cycle = 0;
    }

    void Advance(float seconds)
    {
        var clip = body.Model.Clip(Clip);
        float rate = State == OrcState.Attack ? AttackSpeed : 1;
        Cycle += clip.Seconds > 0 ? seconds * rate / clip.Seconds : 1;
        if (Looping(Clip) && Cycle >= 1) Cycle -= MathF.Floor(Cycle);
        else Cycle = MathF.Min(Cycle, 1);
        if (Layer is not null)
        {
            var layer = body.Model.Clip(Layer);
            LayerCycle = MathF.Min(1, LayerCycle + (layer.Seconds > 0 ? seconds / layer.Seconds : 1));
        }
    }

    bool Looping(string clip) => clip is "combatidle" or "CombatWalk" or "Stunned";

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
        if (clip == "CombatWalk") return;
        var played = body.Model.Clip(clip);
        if (played.Movement is not { Count: > 0 }) return;
        Vector2 moved = to >= from ? played.MovedAt(to) - played.MovedAt(from) : played.MovedAt(1) - played.MovedAt(from) + played.MovedAt(to);
        var left = new Vector3(Facing.Z, 0, -Facing.X);
        Feet += (Facing * moved.X + left * moved.Y) * SourceSpace.MetresPerUnit;
    }

    static Vector3 Shared(float[] xyz) => new(xyz[0], xyz[1], xyz[2]);

    public void Dispose()
    {
        foreach (var b in bodies.Values) b?.Dispose();
        cleaver.Dispose();
        shield.Dispose();
    }
}
