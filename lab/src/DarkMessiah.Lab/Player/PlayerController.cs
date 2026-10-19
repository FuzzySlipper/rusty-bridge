using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;
using Rusty.Engine.Input;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// The reference first-person body and camera: Engine <see cref="FpsInput"/> and <see cref="Look"/> for controls, one
/// Engine character proposal per admitted step for movement, and an interpolated Engine camera sample per update.
/// The accepted transform and motion are kept in the product's <see cref="EntityStore"/>; nothing else is owned here.
/// <list type="bullet">
/// <item>Look integrates on unscaled host seconds, so it keeps its speed while gameplay time holds or slows the world
/// (rusty-riders <c>Walker.ReadInput</c>; Engine csharp-lifecycle "Looking and controls while held").</item>
/// <item>Walking and sprinting configurations are built and validated once, not per step (rusty-hotel, rusty-riders).</item>
/// <item>Jump buffering and coyote time are the Engine controller's (<see cref="CharacterJumpConfig"/>), set from
/// authored seconds; the product only keeps a press until the next admitted step delivers it.</item>
/// <item>Movement scale and an external impulse let actions slow, stop or knock the body without a second controller
/// (rusty-riders <c>Walker.Step</c>).</item>
/// <item>A step the Engine refuses for deep penetration stands the body clear (rusty-craftsurvive).</item>
/// <item>Head bob is camera presentation only; aim uses the unbobbed eye (rusty-dungeon <c>HeldAnimations.HeadBob</c>,
/// re-expressed in metres walked).</item>
/// </list>
/// Copied as: rusty-hotel <c>HotelPlayer</c>, rusty-riders <c>Walker</c> (both from an earlier version of this file).
/// </summary>
internal sealed class PlayerController : IDisposable
{
    private const double NearPlane = .05;
    // How quickly head bob follows walking speed, per second.
    private const float BobResponse = 10;
    // How far below the feet a placement looks for a floor.
    private const float FloorSearch = .5f;

    private readonly IEngineContext engine;
    private readonly SpatialSession session;
    private readonly EntityStore entities;
    private readonly EntityId entity;
    private readonly Camera camera;
    private CharacterControllerConfig walking, sprinting;
    private ulong commandSequence;
    private bool cut = true;
    private Vector3 lastClear;
    private float bobPhase, bobScale;

    internal PlayerController(IEngineContext engine, SpatialSession session, EntityStore entities, EntityId entity, PlayerTuning tuning)
    {
        this.engine = engine;
        this.session = session;
        this.entities = entities;
        this.entity = entity;
        Tuning = tuning;
        (walking, sprinting, Input) = Configure(tuning);
        Place(Vector3.UnitY * (tuning.Height / 2), 0);
        camera = engine.CameraView.CreateCamera(Descriptor());
        try { engine.CameraView.SetActiveCamera(camera); }
        catch { camera.Dispose(); throw; }
    }

    internal PlayerTuning Tuning { get; private set; }
    internal FpsInput Input { get; private set; }
    internal LookState LookState { get; private set; }
    internal CharacterControllerConfig Config => walking;
    internal Vector3 Position => entities.Get(entity, EngineComponentTypes.Transform).Translation;
    internal CharacterMotion Motion => entities.Get(entity, EngineComponentTypes.CharacterMotion);
    internal float Height => Motion.Stance == CharacterStance.Crouched ? Tuning.CrouchedHeight : Tuning.Height;
    internal Vector3 Feet => Position - Vector3.UnitY * (Height / 2);
    /// <summary>The eye actions aim from: no head bob.</summary>
    internal Vector3 Eye => Position + Vector3.UnitY * (Height / 2 - Tuning.EyeBelowTop);
    /// <summary>Where the player looks, as a unit vector (pitch included): the aim of interaction and actions.</summary>
    internal Vector3 Forward => Look.IntegrateClamped(new LookRequest(LookState, Vector2.Zero, Input.Config.PointerLookConfig)).Forward;
    internal CharacterStepReceipt? LastStep { get; private set; }
    internal uint Recoveries { get; private set; }
    internal string LastRecovery { get; private set; } = "none";
    internal BodyEvents Body { get; } = new();

    /// <summary>Consumes this update's input and turns the view; call every update, admitted steps or not.</summary>
    /// <param name="hostSeconds">Unscaled host seconds since the last update (<c>ProductUpdateFacts.HostElapsedSeconds</c>).</param>
    internal FpsInputFrame ReadInput(ReadOnlySpan<ProductInputEvent> events, float hostSeconds)
    {
        FpsInputFrame frame = Input.Consume(events, hostSeconds);
        LookState = Input.IntegrateLook(LookState, frame).After;
        return frame;
    }

    /// <summary>One admitted step of the body.</summary>
    /// <param name="jumpPressed">A press held since the last step; the Engine buffers it for <see cref="PlayerTuning.JumpBufferSeconds"/>.</param>
    /// <param name="obstacles">Dynamic bodies the capsule collides with this step, at their current poses and velocities.</param>
    /// <param name="support">The body the player stands on, as the product reads it now, so the Engine carries the player with it.</param>
    /// <param name="bodies">Colliders a recovery search must keep clear of.</param>
    /// <param name="movementScale">How much of the movement input moves the body (an action's commitment, a stun).</param>
    /// <param name="impulse">A velocity change applied once this step (a knockback, a lunge).</param>
    internal BodyEvent Step(FpsInputFrame input, bool jumpPressed, float delta, ReadOnlyMemory<CharacterObstacle> obstacles,
        CharacterSupport support, ReadOnlyMemory<SpatialEntityCollider> bodies, float movementScale = 1, Vector3 impulse = default)
    {
        CharacterMotion before = Motion;
        CharacterStepReceipt receipt;
        try
        {
            receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(session, Position, before, support, obstacles,
                ReadOnlyMemory<CharacterMeshInstance>.Empty, input.SprintHeld && !input.CrouchHeld ? sprinting : walking,
                new CharacterControllerCommand(input.Movement * movementScale, LookState.YawRadians, jumpPressed && movementScale > 0,
                    input.JumpHeld, input.CrouchHeld, Vector3.Zero, impulse, delta, ++commandSequence)));
        }
        catch (EngineCallException refusal) when (PlayerRecovery.IsPenetration(refusal))
        {
            Recover(bodies);
            return BodyEvent.None;
        }
        entities.Set(entity, EngineComponentTypes.Transform, receipt.Transform);
        entities.Set(entity, EngineComponentTypes.CharacterMotion, receipt.Motion);
        LastStep = receipt;
        lastClear = receipt.Transform.Translation;
        Vector2 planar = new(receipt.Displacement.X, receipt.Displacement.Z);
        float speed = delta > 0 ? planar.Length() / delta : 0;
        bobPhase = (bobPhase + planar.Length() / Tuning.HeadBobStrideMetres * 2 * MathF.PI) % (2 * MathF.PI);
        float bobTarget = receipt.Motion.Grounded ? Math.Clamp(speed / Tuning.Speed, 0, 1) : 0;
        bobScale += (bobTarget - bobScale) * Math.Min(1, delta * BobResponse);
        return Body.Hear(before.Grounded, receipt.Motion.Grounded, -before.ControlledVelocity.Y, receipt.Motion.ControlledVelocity.Y, planar.Length());
    }

    /// <summary>Takes new tuning: rebuilds and validates both configurations and the look sensitivity. Held input is cleared.</summary>
    internal void Retune(PlayerTuning tuning)
    {
        (CharacterControllerConfig walk, CharacterControllerConfig sprint, FpsInput input) = Configure(tuning);
        Tuning = tuning;
        (walking, sprinting, Input) = (walk, sprint, input);
    }

    /// <summary>Stands the body at <paramref name="centre"/> (the capsule's centre), at rest, facing a yaw and pitch.</summary>
    internal void Place(Vector3 centre, float yawDegrees, float pitchDegrees = 0)
    {
        entities.Set(entity, EngineComponentTypes.Transform, new Transform(centre, Quaternion.Identity, Vector3.One));
        entities.Set(entity, EngineComponentTypes.CharacterMotion, AtRest(centre));
        LookState = new LookState(yawDegrees * MathF.PI / 180, 0);
        LookBy(0, pitchDegrees);
        Input.Physical.Clear();
        LastStep = null;
        lastClear = centre;
        bobScale = 0;
        cut = true;
    }

    /// <summary>
    /// Stands the standing body with its feet at <paramref name="feet"/>, or at the nearest place within two metres that
    /// it fits (an Engine capsule overlap) with a floor under it (an Engine ray). Returns where its feet stand, or null
    /// when nowhere near is clear and floored.
    /// </summary>
    internal Vector3? PlaceClear(Vector3 feet, float yawDegrees, float pitchDegrees, ReadOnlyMemory<SpatialEntityCollider> bodies)
    {
        float half = Tuning.Height / 2;
        bool Floored(Vector3 centre) => engine.Spatial.CastRay(new SpatialRaycastRequest(session, centre, -Vector3.UnitY, half + FloorSearch,
            new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), bodies, new[] { entity.Value }, ReadOnlyMemory<SpatialEntityCollider>.Empty)).Present;
        Vector3 start = feet + Vector3.UnitY * (half + walking.Shape.ContactSkin);
        if (PlayerRecovery.FirstClear(engine.Spatial, session, PlayerRecovery.Around(start).Where(Floored), Tuning.Height, walking.Shape, bodies, entity.Value)
            is not { } clear) return null;
        Place(clear, yawDegrees, pitchDegrees);
        return clear - Vector3.UnitY * half;
    }

    internal void LookBy(double yawDegrees, double pitchDegrees)
    {
        LookState = Look.IntegrateClamped(new LookRequest(LookState,
            new Vector2((float)(yawDegrees * Math.PI / 180), (float)(pitchDegrees * Math.PI / 180)),
            Input.Config.PointerLookConfig with { HorizontalRadiansPerUnit = 1, VerticalRadiansPerUnit = 1, InvertVertical = false })).After;
    }

    /// <summary>The yaw, in degrees, that faces from one point toward another (0 faces -Z; positive turns right).</summary>
    internal static float YawDegreesToward(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, -(to.Z - from.Z)) * 180 / MathF.PI;

    /// <summary>Publishes the camera at world time; call every update so look shows while the world holds.</summary>
    internal void Publish(double worldSeconds)
    {
        engine.CameraView.UpdateCameraSample(new CameraSampleRequest(camera, Descriptor(), worldSeconds, 1d / 60,
            CameraInterpolation.Position, cut ? (byte)1 : (byte)0));
        cut = false;
    }

    internal void ResetCounters()
    {
        Body.Reset();
        Recoveries = 0;
        LastRecovery = "none";
    }

    public void Dispose()
    {
        engine.CameraView.ClearActiveCamera(new ClearActiveCameraRequest(0));
        camera.Dispose();
    }

    private void Recover(ReadOnlyMemory<SpatialEntityCollider> bodies)
    {
        Vector3 from = Position;
        Recoveries++;
        if (PlayerRecovery.FirstClear(engine.Spatial, session, PlayerRecovery.Above(from, lastClear), Height, walking.Shape, bodies, entity.Value)
            is not { } to)
        {
            LastRecovery = FormattableString.Invariant($"stuck at {from.X:0.00}, {from.Y:0.00}, {from.Z:0.00}");
            return;
        }
        entities.Set(entity, EngineComponentTypes.Transform, new Transform(to, Quaternion.Identity, Vector3.One));
        entities.Set(entity, EngineComponentTypes.CharacterMotion, AtRest(to));
        lastClear = to;
        cut = true;
        LastRecovery = FormattableString.Invariant($"moved clear from {from.X:0.00}, {from.Y:0.00}, {from.Z:0.00} to {to.X:0.00}, {to.Y:0.00}, {to.Z:0.00}");
    }

    private static CharacterMotion AtRest(Vector3 centre) => new(Vector3.Zero, Vector3.Zero, false, CharacterStance.Standing,
        0, 0, 0, false, 0, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Vector3.Zero, centre.Y, centre.Y, 0, 0);

    private CameraDescriptor Descriptor()
    {
        Vector3 eye = Eye + Vector3.UnitY * (Tuning.HeadBobMetres * bobScale * MathF.Sin(bobPhase));
        return new(new CameraPose(eye, LookState.PitchRadians * 180 / Math.PI, LookState.YawRadians * 180 / Math.PI),
            CameraBasisMode.Derived, default,
            new CameraProjection(CameraProjectionKind.Perspective, Tuning.FieldOfViewDegrees, 0, NearPlane, Tuning.FarPlane),
            new CameraViewport(0, 0, 1, 1));
    }

    private (CharacterControllerConfig Walking, CharacterControllerConfig Sprinting, FpsInput Input) Configure(PlayerTuning t)
    {
        CharacterControllerConfig baseline = engine.Spatial.DefaultCharacterControllerConfig();
        CharacterControllerConfig At(float speed) => baseline with
        {
            Shape = baseline.Shape with { StandingHeight = t.Height, CrouchedHeight = t.CrouchedHeight, Radius = t.Radius },
            Ground = baseline.Ground with { ForwardSpeed = speed, BackwardSpeed = speed, StrafeSpeed = speed },
            Air = baseline.Air with { MaximumSpeed = speed, WishSpeedCap = speed },
            Vertical = baseline.Vertical with { Gravity = t.Gravity, JumpSpeed = t.JumpSpeed },
            Jump = baseline.Jump with { BufferSeconds = t.JumpBufferSeconds, CoyoteSeconds = t.CoyoteSeconds },
            Surface = baseline.Surface with { MaximumStepHeight = t.MaximumStepHeight, MaximumSlopeRadians = t.MaximumSlopeDegrees * MathF.PI / 180 }
        };
        CharacterControllerConfig walk = At(t.Speed), sprint = At(t.SprintSpeed);
        engine.Spatial.ValidateCharacterControllerConfig(walk);
        engine.Spatial.ValidateCharacterControllerConfig(sprint);
        FpsInput input = new(FpsInputConfig.Standard with
        {
            PointerLookConfig = FpsInputConfig.Standard.PointerLookConfig with
            { HorizontalRadiansPerUnit = t.PointerRadiansPerUnit, VerticalRadiansPerUnit = t.PointerRadiansPerUnit }
        });
        return (walk, sprint, input);
    }
}
