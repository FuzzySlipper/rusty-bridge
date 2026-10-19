using System.Numerics;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Scene;
using Rusty.Engine;
using Rusty.Engine.Entities;
using Rusty.Engine.Interaction;

namespace DarkMessiah.Lab.Interaction;

internal sealed class CubeInteraction : IWorldInteractionScene, IDisposable
{
    private const ulong GripId = 1;
    private readonly IEngineContext engine;
    private readonly LabScene scene;
    private readonly PlayerController player;
    private readonly InteractionTuning tuning;
    private ulong revision = 1;
    private Vector3 pendingImpulse;
    private LabCube? thrown;

    internal CubeInteraction(IEngineContext engine, LabScene scene, PlayerController player)
    {
        this.engine = engine;
        this.scene = scene;
        this.player = player;
        tuning = scene.Definition.Interaction;
        Interaction = new WorldInteraction(this);
    }

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
            Vector3 target = player.Eye + player.Forward * tuning.HoldDistance;
            engine.Dynamics.SetFixedTether(new DynamicsFixedTetherRequest(scene.World, held.Body, Vector3.Zero, target,
                new DynamicsTetherConfig(GripId, tuning.Reach, tuning.HoldSlack, tuning.HoldReelSpeed, true)));
        }
        if (thrown is not { } cube) return [];
        DynamicsEntityAction action = new(cube.Entity, Vector3.Zero, Vector3.Zero, pendingImpulse, Vector3.Zero, true);
        thrown = null;
        pendingImpulse = Vector3.Zero;
        return [action];
    }

    public InteractionSceneSnapshot ReadInteraction()
    {
        ReadOnlyMemory<SpatialEntityCollider> colliders = scene.Colliders();
        InteractionCandidate[] candidates = scene.Cubes.Select(cube => new InteractionCandidate(
            new InteractionTarget(cube.Entity.Value, revision), cube.Definition.Label, scene.Pose(cube).Translation, tuning.Reach,
            InteractionVisibilityQuery.Cast(engine.Spatial, scene.Session, player.Eye, scene.Pose(cube).Translation,
                new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), colliders, new[] { cube.Entity.Value }, .02f),
            Held is null ? InteractionAvailability.Available : InteractionAvailability.Unavailable)).ToArray();
        return new InteractionSceneSnapshot(new InteractionQuery(player.Eye, player.Forward, .2f, .26f, tuning.Reach, tuning.Reach),
            candidates, $"lab:{revision}", "pick up");
    }

    public InteractionActionResult UseInteraction(InteractionTarget target)
    {
        LabCube? cube = scene.Cubes.Find(cube => cube.Entity.Value == target.Id);
        if (Held is not null || cube is null || target.Revision != revision)
            return new InteractionActionResult(false, "Cube is unavailable");
        Held = cube;
        Pickups++;
        Message = $"Holding {cube.Definition.Label} — E drop · mouse throw";
        return new InteractionActionResult(true, Message);
    }

    internal void Reset()
    {
        if (Held is not null) Release(false);
        thrown = null;
        pendingImpulse = Vector3.Zero;
        Interaction.Focus.Clear();
        revision++;
        Pickups = Throws = Drops = 0;
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
            // An authored launch impulse; Engine owns the subsequent body motion and contacts.
            thrown = cube;
            pendingImpulse = player.Forward * (cube.Definition.Mass * tuning.ThrowSpeed);
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
