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
    private CharacterControllerConfig walking, sprinting, crouching, slow, backing;
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
        (walking, sprinting, crouching, slow, backing, Input) = Configure(tuning);
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
    /// <summary>The last step moved with the sprint configuration.</summary>
    internal bool Sprinted { get; private set; }
    /// <summary>The drop, units, of the fall the last step landed from (0 when it didn't land; R10159-1).</summary>
    internal float LandedDrop { get; private set; }
    /// <summary>The health that landing takes (<see cref="FallRules"/>).</summary>
    internal float FallDamage => FallRules.Damage(LandedDrop);
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
        CharacterSupport support, ReadOnlyMemory<SpatialEntityCollider> bodies, float movementScale = 1, Vector3 impulse = default, bool walkHeld = false)
    {
        CharacterMotion before = Motion;
        CharacterStepReceipt receipt;
        LandedDrop = 0;
        Sprinted = input.SprintHeld && !input.CrouchHeld;
        // Crouched, jump does nothing: the donor's body didn't leave the ground on any of l01_a-v18-movement's three
        // crouched jumps (R10159-2).
        if (input.CrouchHeld) jumpPressed = false;
        // The body leaves the ground jumpDelay after jump goes down, as the donor's did (R10159-2).
        if (jumpPressed && Tuning.JumpDelay > 0 && before.Grounded && Climbing is null && LadderMove is null)
        {
            jumpDelayLeft = Tuning.JumpDelay;
            jumpPressed = false;
        }
        else if (jumpDelayLeft > 0 && (jumpDelayLeft -= delta) <= 1e-4f)
        {
            jumpDelayLeft = 0;
            jumpPressed = before.Grounded;
        }
        // Getting on or off a ladder (R10163-1): mm_time_to_exit_enter_ladder's second onto or off the rail, eased, the
        // controls held off meanwhile.
        if (LadderMove is { } move)
        {
            fallTop = null;
            float done = Math.Min(1, (move.Elapsed + delta) / LadderRules.EnterExitSeconds);
            float eased = done * done * (3 - 2 * done);
            var at = Vector3.Lerp(move.From, move.To, eased);
            entities.Set(entity, EngineComponentTypes.Transform, entities.Get(entity, EngineComponentTypes.Transform) with { Translation = at });
            entities.Set(entity, EngineComponentTypes.CharacterMotion, AtRest(at));
            LadderMove = done >= 1 ? null : move with { Elapsed = move.Elapsed + delta };
            if (done >= 1) Climbing = move.Then;
            lastClear = at;
            return BodyEvent.None;
        }
        // A ladder (#10163): mount it moving forward within reach of its rail, looking toward it; jump lets go.
        if (Climbing is null && input.Movement.Y > 0 && !jumpPressed && LadderInReach() is { } reached)
        {
            var (bottom, top) = LadderRules.Rail(reached, Tuning.Radius, Tuning.Height / 2 + walking.Shape.ContactSkin);
            StartLadderMove(bottom with { Y = Math.Clamp(StandingY, bottom.Y, top.Y) }, reached);
            return BodyEvent.None;
        }
        // Or from its top: at the edge, facing the ladder's way, backing off steps onto the rail's top (the exit's reverse).
        if (Climbing is null && input.Movement.Y < 0 && !jumpPressed && LadderBehind() is var (fromTop, railTop))
        {
            StartLadderMove(railTop, fromTop);
            return BodyEvent.None;
        }
        if (Climbing is not null && jumpPressed) Climbing = null;
        float climbIntent = 0;
        ClimbIntent = 0;
        try
        {
            var movement = default(CharacterMovementRequest);
            if (Climbing is { } ladder)
            {
                var (bottom, top) = LadderRules.Rail(ladder, Tuning.Radius, Tuning.Height / 2 + walking.Shape.ContactSkin);
                climbIntent = LadderRules.Intent(input.Movement.Y * movementScale, LookState.PitchRadians * 180 / MathF.PI);
                ClimbIntent = climbIntent;
                movement = new CharacterMovementRequest(CharacterMovementMode.Climbing, climbIntent, LadderRules.SpeedMetres, 0, 0, bottom, top, 0, 0, LadderRules.Reach);
            }
            receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(session, Position, before, support, obstacles,
                ReadOnlyMemory<CharacterMeshInstance>.Empty, input.CrouchHeld && Climbing is null ? crouching : input.SprintHeld ? sprinting : walkHeld ? slow
                    : input.Movement.Y < 0 && input.Movement.X == 0 ? backing : walking,
                new CharacterControllerCommand(movement, Climbing is null ? input.Movement * movementScale : Vector2.Zero, LookState.YawRadians,
                    Climbing is null && jumpPressed && movementScale > 0, input.JumpHeld, input.CrouchHeld && Climbing is null, Vector3.Zero, impulse, delta, ++commandSequence)));
        }
        catch (EngineCallException refusal) when (PlayerRecovery.IsPenetration(refusal))
        {
            Recover(bodies);
            return BodyEvent.None;
        }
        entities.Set(entity, EngineComponentTypes.Transform, receipt.Transform);
        // Off the ladder: at its top climbing on, onto what it leads to (sv_autoladderdismount, mm_dist_to_exit_ladder
        // past the top); at its foot climbing down; or when the body is no longer on the rail.
        if (Climbing is { } held)
        {
            var climb = receipt.Movement;
            var facing = Vector3.Normalize(held.Facing with { Y = 0 });
            if (climb.ClimbAtTop && climbIntent > 0)
            {
                Climbing = null;
                StartLadderMove(receipt.Transform.Translation + facing * (2 * Tuning.Radius + LadderRules.OffsetMetres + LadderRules.ExitMetres), null);
            }
            else if (climb.ClimbAtBottom && climbIntent < 0)
            {
                Climbing = null;
                StartLadderMove(receipt.Transform.Translation - facing * LadderRules.ExitMetres, null);
            }
            else if (!climb.ClimbAttached) Climbing = null;
        }
        var motion = receipt.Motion;
        // Rising, gravity is jumpHeldGravity of itself while jump is held and jumpReleaseGravity times it once let go
        // (R10159-2): the donor's jump rose 26 units held 0.05 s, 33 held 0.15, 41 held 0.3 and 42 held longer, and fell
        // at 700 units/s².
        if (!motion.Grounded && motion.ControlledVelocity.Y > 0)
        {
            float extra = (input.JumpHeld ? Tuning.JumpHeldGravity - 1 : Tuning.JumpReleaseGravity - 1) * Tuning.Gravity * delta;
            motion = motion with { ControlledVelocity = motion.ControlledVelocity with { Y = MathF.Max(0, motion.ControlledVelocity.Y - extra) } };
        }
        entities.Set(entity, EngineComponentTypes.CharacterMotion, motion);
        // A fall's drop is from the highest the feet were off the ground (a ladder holds none) to where they land.
        if (motion.Grounded || Climbing is not null)
        {
            if (fallTop is float topY && motion.Grounded && !before.Grounded) LandedDrop = FallRules.DropUnits(MathF.Max(0, topY - Feet.Y));
            fallTop = null;
        }
        else fallTop = MathF.Max(fallTop ?? Feet.Y, Feet.Y);
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
        (CharacterControllerConfig walk, CharacterControllerConfig sprint, CharacterControllerConfig crouch, CharacterControllerConfig slowWalk, CharacterControllerConfig back,
            FpsInput input) = Configure(tuning);
        Tuning = tuning;
        (walking, sprinting, crouching, slow, backing, Input) = (walk, sprint, crouch, slowWalk, back, input);
    }

    /// <summary>Stands the body at <paramref name="centre"/> (the capsule's centre), at rest, facing a yaw and pitch.</summary>
    internal void Place(Vector3 centre, float yawDegrees, float pitchDegrees = 0)
    {
        fallTop = null;
        entities.Set(entity, EngineComponentTypes.Transform, new Transform(centre, Quaternion.Identity, Vector3.One));
        entities.Set(entity, EngineComponentTypes.CharacterMotion, AtRest(centre));
        Climbing = null;
        LadderMove = null;
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

    /// <summary>Looks along a yaw and pitch, radians (a replay's recorded view).</summary>
    internal void Face(float yawRadians, float pitchRadians) => LookState = new LookState(yawRadians, pitchRadians);

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

    /// <summary>
    /// The camera the first person draws from (#10161: 9 units above the eye and 2.1 ahead, turned by the head) against the
    /// eye and the look, or null to draw from the eye along the look. Composed with the current eye and look each time
    /// the camera is published, a step or not (R10161-3).
    /// </summary>
    internal Sword.CameraRig? Camera { get; set; }

    private float jumpDelayLeft;
    // The highest the feet have been since they left the ground (R10159-1).
    private float? fallTop;

    /// <summary>The arena's ladders (#10163).</summary>
    internal IReadOnlyList<Ladder> Ladders { get; set; } = [];
    /// <summary>The ladder the player is on, or null.</summary>
    internal Ladder? Climbing { get; private set; }

    /// <summary>Getting on or off a ladder (R10163-1): from, to, the time so far, and the ladder climbed after (null: off).</summary>
    internal (Vector3 From, Vector3 To, float Elapsed, Ladder? Then)? LadderMove { get; private set; }

    // The ladder stands the body (R10163-3): a crouched one rises from where its feet are, so the move starts from the
    // standing centre over them and the rail's ends are the standing body's.
    private void StartLadderMove(Vector3 to, Ladder? then)
    {
        LadderMove = (Feet + Vector3.UnitY * (Tuning.Height / 2), to, 0, then);
        lastLadder = then ?? Climbing ?? lastLadder;
    }

    private Ladder? lastLadder;

    /// <summary>
    /// Where the body is with a ladder, for its animation (#10394, R10394-1): getting on at its foot or top or off at
    /// either end (the end the move is nearer), on it, or off.
    /// </summary>
    internal LadderPhase LadderPhase => LadderMove is { } m
        ? m.Then is { } on ? (m.To.Y > (on.Foot.Y + on.Top) / 2 ? LadderPhase.OnFromTop : LadderPhase.OnFromFoot)
        : lastLadder is { } off && m.From.Y > (off.Foot.Y + off.Top) / 2 ? LadderPhase.OffAtTop : LadderPhase.OffAtFoot
        : Climbing is not null ? LadderPhase.On : LadderPhase.None;

    /// <summary>The way the last step on a ladder asked to climb: up positive, down negative (<see cref="LadderRules.Intent"/>).</summary>
    internal float ClimbIntent { get; private set; }

    /// <summary>The ladder the body is on, getting on or getting off, or null.</summary>
    internal Ladder? LadderOf => LadderMove is { } m ? m.Then ?? lastLadder : Climbing;

    // The centre's height the body would stand at over its feet: the rail is the standing body's (R10163-3).
    private float StandingY => Feet.Y + Tuning.Height / 2 + walking.Shape.ContactSkin;

    /// <summary>A ladder whose rail is within reach, its top above the body and its foot below, the look toward it.</summary>
    private Ladder? LadderInReach()
    {
        var look = Forward with { Y = 0 };
        if (look.LengthSquared() < 1e-6f) return null;
        look = Vector3.Normalize(look);
        foreach (var ladder in Ladders)
        {
            var (bottom, top) = LadderRules.Rail(ladder, Tuning.Radius, Tuning.Height / 2 + walking.Shape.ContactSkin);
            var away = (Position - bottom) with { Y = 0 };
            if (away.Length() <= LadderRules.Reach && StandingY >= bottom.Y - .1f && StandingY < top.Y
                && Vector3.Dot(look, Vector3.Normalize(ladder.Facing with { Y = 0 })) >= LadderRules.MountDot)
                return ladder;
        }
        return null;
    }

    /// <summary>
    /// A ladder whose top the body stands at, facing the ladder's way, within reach of where its exit leaves the body:
    /// the ladder and the rail's top to step onto.
    /// </summary>
    private (Ladder Ladder, Vector3 RailTop)? LadderBehind()
    {
        var look = Forward with { Y = 0 };
        if (look.LengthSquared() < 1e-6f) return null;
        look = Vector3.Normalize(look);
        foreach (var ladder in Ladders)
        {
            var (_, top) = LadderRules.Rail(ladder, Tuning.Radius, Tuning.Height / 2 + walking.Shape.ContactSkin);
            var facing = Vector3.Normalize(ladder.Facing with { Y = 0 });
            var exit = top + facing * (2 * Tuning.Radius + LadderRules.OffsetMetres + LadderRules.ExitMetres);
            if (((Position - exit) with { Y = 0 }).Length() <= LadderRules.Reach + .3f && MathF.Abs(StandingY - top.Y) < .15f
                && Vector3.Dot(look, facing) >= LadderRules.MountDot)
                return (ladder, top with { Y = top.Y - .03f });
        }
        return null;
    }

    /// <summary>The lean (Z and X, as the donor's <c>config.cfg</c> binds them): presentation, as in the donor's client.</summary>
    internal PlayerLean Lean { get; } = new();

    /// <summary>
    /// Where the leaned camera may be (R10161-2): a ray from the eye to it stops <see cref="PlayerLean.Hull"/> short of
    /// the arena, and the camera comes back along it, as the donor's against a wall. Not leaning, as composed.
    /// </summary>
    private Vector3 Clear(Vector3 eye, Vector3 camera)
    {
        if (Lean.Value == 0) return camera;
        Vector3 out_ = camera - eye;
        float length = out_.Length();
        if (length < 1e-4f) return camera;
        var hit = engine.Spatial.CastRay(new SpatialRaycastRequest(session, eye, out_ / length, length + 4 * PlayerLean.HullMetres,
            new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), ReadOnlyMemory<SpatialEntityCollider>.Empty, new[] { entity.Value }, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        if (!hit.Present) return camera;
        // The hull keeps its distance square to what it meets, as the donor's box trace does: along a slanted ray, more.
        var direction = out_ / length;
        // The surface's normal; where the cast gives none, a wall's (the arena's are upright): against the ray's level part.
        var level = direction with { Y = 0 };
        var normal = hit.Normal.LengthSquared() > .25f ? Vector3.Normalize(hit.Normal)
            : level.LengthSquared() > 1e-6f ? -Vector3.Normalize(level) : -direction;
        float facing = MathF.Abs(Vector3.Dot(direction, normal));
        float back = facing > 1e-3f ? PlayerLean.HullMetres / facing : length;
        return eye + out_ * Math.Clamp(((float)hit.Distance - back) / length, 0, 1);
    }

    /// <summary>The bow's zoom (#10168): how far, 0 to 1, the view has narrowed toward its field of view, degrees high.</summary>
    internal (float Fraction, float Degrees) Zoom { get; set; }

    // From the tuning's view toward the zoom's, on the tangent of the half angle.
    float FieldOfView => Zoom.Fraction <= 0 ? Tuning.FieldOfViewDegrees : 2 * MathF.Atan(float.Lerp(MathF.Tan(Tuning.FieldOfViewDegrees * MathF.PI / 360),
        MathF.Tan(Zoom.Degrees * MathF.PI / 360), Zoom.Fraction)) * 180 / MathF.PI;

    internal CameraDescriptor Descriptor()
    {
        Vector3 bob = Vector3.UnitY * (Tuning.HeadBobMetres * bobScale * MathF.Sin(bobPhase));
        Vector3 eye = Eye + bob;
        if (Camera is { } rig && rig.At(eye, Forward) is var (at, forward, up))
            return new(new CameraPose(Clear(eye, at), LookState.PitchRadians * 180 / Math.PI, LookState.YawRadians * 180 / Math.PI),
                CameraBasisMode.Explicit, new CameraBasis(forward, Vector3.Normalize(Vector3.Cross(forward, up)), up),
                new CameraProjection(CameraProjectionKind.Perspective, FieldOfView, 0, NearPlane, Tuning.FarPlane),
                new CameraViewport(0, 0, 1, 1));
        return new(new CameraPose(eye, LookState.PitchRadians * 180 / Math.PI, LookState.YawRadians * 180 / Math.PI),
            CameraBasisMode.Derived, default,
            new CameraProjection(CameraProjectionKind.Perspective, FieldOfView, 0, NearPlane, Tuning.FarPlane),
            new CameraViewport(0, 0, 1, 1));
    }

    private (CharacterControllerConfig Walking, CharacterControllerConfig Sprinting, CharacterControllerConfig Crouching, CharacterControllerConfig Slow,
        CharacterControllerConfig Backing, FpsInput Input) Configure(PlayerTuning t)
    {
        CharacterControllerConfig baseline = engine.Spatial.DefaultCharacterControllerConfig();
        // The donor's speeds by direction (#10159): back at backSpeed of a full back key, crouched at crouchSpeed.
        CharacterControllerConfig At(float speed, float back, float? acceleration = null) => baseline with
        {
            Shape = baseline.Shape with { StandingHeight = t.Height, CrouchedHeight = t.CrouchedHeight, Radius = t.Radius },
            Ground = baseline.Ground with { ForwardSpeed = speed, BackwardSpeed = back, StrafeSpeed = speed, Acceleration = acceleration ?? t.Acceleration,
                Braking = t.Braking, Friction = t.Friction, StopSpeed = t.StopSpeed },
            Air = baseline.Air with { MaximumSpeed = speed, WishSpeedCap = speed },
            Vertical = baseline.Vertical with { Gravity = t.Gravity, JumpSpeed = t.JumpSpeed },
            Jump = baseline.Jump with { BufferSeconds = t.JumpBufferSeconds, CoyoteSeconds = t.CoyoteSeconds },
            Surface = baseline.Surface with { MaximumStepHeight = t.MaximumStepHeight, MaximumSlopeRadians = t.MaximumSlopeDegrees * MathF.PI / 180 }
        };
        CharacterControllerConfig walk = At(t.Speed, t.BackSpeed), sprint = At(t.SprintSpeed, t.BackSpeed), crouch = At(t.CrouchSpeed, Math.Min(t.BackSpeed, t.CrouchSpeed), t.CrouchAcceleration);
        // The donor's walk key, +speed (ALT; R10159-1): 100 units a second.
        CharacterControllerConfig slowWalk = At(t.WalkSpeed, Math.Min(t.BackSpeed, t.WalkSpeed));
        // Backing off alone, at its own acceleration (R10159-2).
        CharacterControllerConfig back = At(t.Speed, t.BackSpeed, t.BackAcceleration);
        engine.Spatial.ValidateCharacterControllerConfig(back);
        engine.Spatial.ValidateCharacterControllerConfig(slowWalk);
        engine.Spatial.ValidateCharacterControllerConfig(walk);
        engine.Spatial.ValidateCharacterControllerConfig(sprint);
        engine.Spatial.ValidateCharacterControllerConfig(crouch);
        FpsInput input = new(FpsInputConfig.Standard with
        {
            PointerLookConfig = FpsInputConfig.Standard.PointerLookConfig with
            { HorizontalRadiansPerUnit = t.PointerRadiansPerUnit, VerticalRadiansPerUnit = t.PointerRadiansPerUnit }
        });
        return (walk, sprint, crouch, slowWalk, back, input);
    }
}
