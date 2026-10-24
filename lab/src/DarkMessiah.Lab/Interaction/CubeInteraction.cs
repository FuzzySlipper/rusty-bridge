using System.Numerics;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Magic;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Scene;
using Rusty.Engine;
using Rusty.Engine.Entities;
using Rusty.Engine.Interaction;

namespace DarkMessiah.Lab.Interaction;

/// <summary>
/// Telekinesis on the arena's bodies (#10164; docs/lab.md, "Telekinesis"): E grips the body in view within the spell's
/// reach for its mass class's mana (a huge one refused), holds it where the donor does, and the mouse throws it at the
/// donor's speed; a thrown body meeting the orc hurts it by its class. The lab has no spell in hand yet (#10165): the
/// donor holds attack to grip, lets go (the body stays, held), and presses again to throw.
/// </summary>
internal sealed class CubeInteraction : IWorldInteractionScene, IDisposable
{
    private const ulong GripId = 1;
    private readonly IEngineContext engine;
    private readonly LabScene scene;
    private readonly PlayerController player;
    private readonly InteractionTuning tuning;
    private readonly LabCombat combat;
    private ulong revision = 1;
    private Vector3 pendingImpulse;
    private LabCube? thrown;
    // The body in flight after a throw: where it was a step ago and how long it may still hurt.
    private (LabCube Cube, Vector3 Last, float Left)? flying;

    internal CubeInteraction(IEngineContext engine, LabScene scene, PlayerController player, LabCombat combat)
    {
        this.engine = engine;
        this.scene = scene;
        this.player = player;
        this.combat = combat;
        tuning = scene.Definition.Interaction;
        Interaction = new WorldInteraction(this);
    }

    TelekinesisRules Rules => combat.Telekinesis;
    /// <summary>The thrown bodies that hit the orc.</summary>
    internal uint Hits { get; private set; }

    internal WorldInteraction Interaction { get; }
    internal LabCube? Held { get; private set; }
    internal string Message { get; private set; } = "Aim at a cube and press E";
    internal string TargetLabel => Held is { } held ? held.Definition.Label :
        scene.Cubes.Find(cube => cube.Entity.Value == Interaction.Focus.Selected?.Id)?.Definition.Label ?? "";
    internal uint Pickups { get; private set; }
    internal uint Throws { get; private set; }
    internal uint Drops { get; private set; }

    internal void Use()
    {
        if (Held is not null) { Release(false); return; }
        Interaction.Update();
        InteractionUseReceipt receipt = Interaction.UseFocused();
        Message = receipt.Message;
    }

    internal void Throw()
    {
        if (Held is not null) Release(true);
    }

    internal DynamicsEntityAction[] PrepareStep()
    {
        if (Held is { } held)
        {
            Vector3 target = player.Eye + player.Forward * Rules.HoldMetres(held.Definition.Mass);
            // The tether's longest is where the body is now (one gripped across the room reels straight in, not slack
            // from the spell's reach), its target the hold's slack.
            float now = MathF.Max(Vector3.Distance(scene.Pose(held).Translation, target), tuning.HoldSlack);
            engine.Dynamics.SetFixedTether(new DynamicsFixedTetherRequest(scene.World, held.Body, Vector3.Zero, target,
                new DynamicsTetherConfig(GripId, now, tuning.HoldSlack, tuning.HoldReelSpeed, true)));
        }
        if (thrown is not { } cube) return [];
        DynamicsEntityAction action = new(cube.Entity, Vector3.Zero, Vector3.Zero, pendingImpulse, Vector3.Zero, true);
        thrown = null;
        pendingImpulse = Vector3.Zero;
        flying = (cube, scene.Pose(cube).Translation, ThrownSeconds);
        return [action];
    }

    /// <summary>How long after a throw the body may still hurt the orc.</summary>
    internal const float ThrownSeconds = 3;

    /// <summary>After the world's step: a thrown body in flight that meets the orc hurts it, once.</summary>
    internal void AfterStep(float seconds)
    {
        if (flying is not { } f || seconds <= 0) return;
        var at = scene.Pose(f.Cube).Translation;
        var velocity = (at - f.Last) / seconds;
        if (combat.Thrown(f.Cube.Definition.Mass, at, velocity, f.Cube.Definition.Size / 2))
        {
            Hits++;
            flying = null;
            return;
        }
        flying = f.Left - seconds > 0 ? (f.Cube, at, f.Left - seconds) : null;
    }

    public InteractionSceneSnapshot ReadInteraction()
    {
        ReadOnlyMemory<SpatialEntityCollider> colliders = scene.Colliders();
        InteractionCandidate[] candidates = scene.Cubes.Select(cube => new InteractionCandidate(
            new InteractionTarget(cube.Entity.Value, revision), cube.Definition.Label, scene.Pose(cube).Translation, Rules.ReachMetres,
            InteractionVisibilityQuery.Cast(engine.Spatial, scene.Session, player.Eye, scene.Pose(cube).Translation,
                new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), colliders, new[] { cube.Entity.Value }, .02f),
            Held is null ? InteractionAvailability.Available : InteractionAvailability.Unavailable)).ToArray();
        return new InteractionSceneSnapshot(new InteractionQuery(player.Eye, player.Forward, .2f, .26f, Rules.ReachMetres, Rules.ReachMetres),
            candidates, $"lab:{revision}", "pick up");
    }

    public InteractionActionResult UseInteraction(InteractionTarget target)
    {
        LabCube? cube = scene.Cubes.Find(cube => cube.Entity.Value == target.Id);
        if (Held is not null || cube is null || target.Revision != revision)
            return new InteractionActionResult(false, "Cube is unavailable");
        // A huge body can't be gripped, nor one whose class costs more mana than the player has (none spent).
        float kg = cube.Definition.Mass, cost = Rules.Cost(kg);
        if (Rules.Class(kg) == MassClass.Huge) return Refused($"{cube.Definition.Label} is too heavy for telekinesis");
        if (!combat.Mana.Spend(cost)) return Refused(FormattableString.Invariant($"Not enough mana for {cube.Definition.Label} ({cost:0})"));
        Held = cube;
        Pickups++;
        Message = FormattableString.Invariant($"Holding {cube.Definition.Label} for {cost:0} mana — E drop · mouse throw");
        return new InteractionActionResult(true, Message);
    }

    /// <summary>Grips refused: huge bodies, or too little mana.</summary>
    internal uint Refusals { get; private set; }

    InteractionActionResult Refused(string why)
    {
        Refusals++;
        Message = why;
        return new InteractionActionResult(false, why);
    }

    internal void Reset()
    {
        if (Held is not null) Release(false);
        thrown = null;
        flying = null;
        pendingImpulse = Vector3.Zero;
        Interaction.Focus.Clear();
        revision++;
        Pickups = Throws = Drops = Hits = Refusals = 0;
        Message = "Lab reset";
    }

    public void Dispose()
    {
        if (Held is not null) Release(false);
    }

    private void Release(bool throwing)
    {
        LabCube cube = Held ?? throw new InvalidOperationException("No cube is held.");
        engine.Dynamics.RemoveTether(new DynamicsTetherRequest(scene.World, GripId));
        Held = null;
        if (throwing)
        {
            // The donor's throw speed as an impulse; Engine owns the subsequent body motion and contacts.
            thrown = cube;
            pendingImpulse = player.Forward * (cube.Definition.Mass * Rules.ThrowMetres);
            Throws++;
            Message = $"Threw {cube.Definition.Label}";
        }
        else
        {
            Drops++;
            Message = $"Dropped {cube.Definition.Label}";
        }
        Interaction.Focus.Clear();
    }
}
