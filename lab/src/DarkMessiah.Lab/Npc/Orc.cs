using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Shared;
using Rusty.Engine;

namespace DarkMessiah.Lab.Npc;

/// <summary>What the lab's orc is doing.</summary>
internal enum OrcState { Approach, Ready, Attack, Parry, Hurt, Pushed, Down }

/// <summary>One of the orc's attack parts landing this step: the part's index (Arkane's 2102 is 0, 2139 is 1).</summary>
internal readonly record struct OrcBlow(int Part, string Attack, float Cycle);

/// <summary>
/// A recorded donor orc as a replay hands it to the lab's (<see cref="Orc.Follow"/>), in lab space: where it
/// stands and faces, its health, and what it plays. <see cref="Fresh"/> is set when the recording streamed its
/// animation this tick (an NPC's state reaches the Wire every few ticks; between them the lab plays it on).
/// </summary>
/// <param name="Kit">The model paths of the items the recorded orc owns (Wire v10's owners): its cleaver, and a shield
/// if it has one. Null where the recording doesn't say (before v10): the lab's own kit.</param>
internal sealed record OrcPose(string Id, Vector3 Feet, Vector3 Facing, float Health, string Sequence, float Cycle, float Rate,
    string? Layer, float LayerCycle, bool Fresh, IReadOnlyList<string>? Kit = null);

/// <summary>
/// The lab's orc: Arkane's <c>npc_orc</c> with its cleaver and shield, and a minimal fight loop, not the donor's AI
/// (which is a later task). It walks in (<c>CombatWalk</c>), then attacks in turn with <c>attack_Fast</c>,
/// <c>attack_right</c> and <c>attack_Bash</c> at the cleaver's <c>speed_attack</c>. Each attack part's event (2102,
/// 2139) the step crosses is a blow. When not attacking it may parry a swing it sees coming (<c>layer_parry_B</c>
/// over its idle, which sets its defense state's parry bit), and it reacts to hits (<c>Hit_Body_Front_small</c>) and
/// kicks (<c>Push_Body_back_small</c>, pushed back). At no health it drops and comes back after a while.
/// </summary>
internal sealed class Orc : IDisposable
{
    internal const string Model = "models/npc/orc/npc_orc.mdl", Cleaver = "models/Items/Weapons/sword_orc/sword_orc.mdl",
        Shield = "models/items/armors/shield_orc/shield_orc.mdl";
    internal const ulong BodyId = 0x4C41_0010, CleaverId = 0x4C41_0011, ShieldId = 0x4C41_0012;
    static readonly string[] Attacks = ["attack_Fast", "attack_right", "attack_Bash"];
    static readonly int[] PartEvents = [2102, 2139];

    readonly IEngineContext engine;
    readonly Puppet body, cleaver, shield;
    readonly Random random = new(1);
    OrcTuning tuning;
    float cooldown, downSeconds, pushLeft;
    int attackIndex;
    Vector3 pushDirection;

    internal Orc(IEngineContext engine, DonorContent donor, OrcTuning tuning)
    {
        this.engine = engine;
        this.tuning = tuning;
        body = new Puppet(engine, donor.Model(Model), BodyId);
        cleaver = new Puppet(engine, donor.Model(Cleaver), CleaverId);
        shield = new Puppet(engine, donor.Model(Shield), ShieldId);
        AttackSpeed = donor.Number(Cleaver, "entity_data", "speed_attack") ?? 1;
        Spawn();
    }

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
        for (int part = 0; part < PartEvents.Length; part++)
            foreach (float at in played.At(PartEvents[part]))
                if (from < at && to >= at && landed.Add((clip, part, at))) blows.Add(new OrcBlow(part, clip, at));
    }

    /// <summary>Back to the lab's own loop after a replay.</summary>
    internal void StopFollowing()
    {
        Following = false;
        holdsCleaver = holdsShield = true;
        followed = null;
        landed.Clear();
        Spawn();
    }

    /// <summary>
    /// One step: turns to the player, walks in, attacks, parries, reacts or lies down. Returns the blows whose part
    /// events this step crossed; whether they reach the player is the combat's to decide.
    /// </summary>
    internal List<OrcBlow> Step(float seconds, Vector3 playerFeet, bool swingStarted)
    {
        var blows = new List<OrcBlow>();
        Vector3 toPlayer = playerFeet - Feet;
        toPlayer.Y = 0;
        float distance = toPlayer.Length();
        if (State is not (OrcState.Down or OrcState.Pushed or OrcState.Hurt) && distance > 1e-3f) Facing = toPlayer / distance;
        cooldown = MathF.Max(0, cooldown - seconds);
        float before = Cycle;
        Advance(seconds);
        bool done = Cycle >= 1 && !Looping(Clip);
        switch (State)
        {
            case OrcState.Down:
                downSeconds += seconds;
                if (downSeconds >= tuning.RespawnSeconds) Spawn();
                break;
            case OrcState.Approach:
                // A margin: stepping exactly the remaining gap leaves it a rounding error outside.
                if (distance <= tuning.EngageDistance + .05f) Play(OrcState.Ready, "combatidle");
                else Feet += Facing * MathF.Min(tuning.WalkSpeed * seconds, distance - tuning.EngageDistance);
                break;
            case OrcState.Ready:
                if (distance > tuning.EngageDistance * 1.25f) Play(OrcState.Approach, "CombatWalk");
                else if (swingStarted && random.NextSingle() < tuning.ParryChance) Parry();
                else if (cooldown <= 0)
                {
                    Play(OrcState.Attack, Attacks[attackIndex++ % Attacks.Length]);
                    AttacksMade++;
                }
                break;
            case OrcState.Attack:
                var clip = body.Model.Clip(Clip);
                for (int part = 0; part < PartEvents.Length; part++)
                    foreach (float at in clip.At(PartEvents[part]))
                        if (before < at && Cycle >= at) blows.Add(new OrcBlow(part, Clip, at));
                if (done) { cooldown = tuning.AttackCooldown; Play(OrcState.Ready, "combatidle"); }
                break;
            case OrcState.Parry:
                if (LayerCycle >= 1) { Layer = null; Play(OrcState.Ready, "combatidle"); }
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
        }
        return blows;
    }

    /// <summary>The verdict and blow are the combat's; this takes their outcome.</summary>
    internal void Damage(float amount)
    {
        if (!Alive || Following) return;
        Health -= amount;
        if (Health <= 0) { Health = 0; Layer = null; downSeconds = 0; Play(OrcState.Down, "combatidle"); }
        else if (State != OrcState.Attack) Play(OrcState.Hurt, "Hit_Body_Front_small");
    }

    /// <summary>A kick: pushed back along <paramref name="direction"/> by <paramref name="metres"/>, attack interrupted.</summary>
    internal void Push(Vector3 direction, float metres)
    {
        if (!Alive || Following) return;
        direction.Y = 0;
        pushDirection = direction.LengthSquared() > 1e-6f ? Vector3.Normalize(direction) : -Facing;
        pushLeft = metres;
        Layer = null;
        Play(OrcState.Pushed, "Push_Body_back_small");
    }

    /// <summary>Moves the orc's feet (a developer placement).</summary>
    internal void PlaceAt(Vector3 feet) => Feet = feet;

    internal void Spawn()
    {
        Feet = Shared(tuning.Spawn);
        Health = tuning.Health;
        Layer = null;
        cooldown = tuning.AttackCooldown;
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
        if (Layer is null) body.Play(Clip, Cycle);
        else body.Play(Clip, Cycle, (Layer, LayerCycle, 1));
    }

    void Parry()
    {
        Layer = "layer_parry_B";
        LayerCycle = 0;
        Play(OrcState.Parry, "combatidle");
    }

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

    bool Looping(string clip) => clip is "combatidle" or "CombatWalk";

    static Vector3 Shared(float[] xyz) => new(xyz[0], xyz[1], xyz[2]);

    public void Dispose()
    {
        body.Dispose();
        cleaver.Dispose();
        shield.Dispose();
    }
}
