using System.Numerics;
using System.Text.Json;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Developer;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Interaction;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Replay;
using DarkMessiah.Lab.Scene;
using DarkMessiah.Lab.Sword;
using DarkMessiah.Lab.Time;
using DarkMessiah.Lab.Ui;
using Rusty.Engine;
using Rusty.Engine.Debugging;
using Rusty.Engine.Entities;
using Rusty.Engine.Input;

namespace DarkMessiah.Lab;

/// <summary>
/// The combat lab's entry (docs/lab.md): lifecycle, and each update's composition of input, the player's body, the
/// fight (Dark Messiah's sword against an orc, <see cref="LabCombat"/>), loose cubes, world time and what is
/// published. Every update reads input, turns the view and publishes the camera; the world advances only per
/// Engine-admitted step. Built from rusty-fptester's <c>FpTesterProduct</c> (docs/lab.md, "Provenance").
/// </summary>
public sealed class LabProduct : IEngineProduct, IDebugCommandModuleSource, IReplayBody
{
    private readonly IEngineContext engine;
    private readonly DonorContent donor;
    private readonly LabScene scene;
    private readonly PlayerController player;
    private readonly CubeInteraction cubes;
    private readonly LabCombat combat;
    private readonly WorldTime time;
    private readonly InputDiagnostics diagnostics = new();
    private readonly LabDeveloper developer;
    private readonly UiStream hud;
    private ulong generation;
    private double worldSeconds;
    private string notice = "";
    private bool started;
    private bool paused;
    private bool disposed;
    private bool jumpPending;
    private bool usePending;
    private bool throwPending;
    private bool kickPending;
    // A press of attack or block not yet seen by a step: a tap whose press and release both fall between steps
    // (an update with no admitted step, or both edges in one batch) still reaches the sword as one held step.
    private bool attackPending, blockPending;

    public LabProduct(ProductCreateContext context)
    {
        engine = context.Engine;
        donor = DonorContent.Open(engine);
        scene = new LabScene(engine, LabDefinition.Load(engine));
        player = new PlayerController(engine, scene.Session, scene.Entities, scene.PlayerEntity, PlayerTuning.Load(engine));
        cubes = new CubeInteraction(engine, scene, player);
        combat = new LabCombat(engine, donor, CombatTuning.Load(engine));
        // A knocked-down orc falls as a ragdoll in the scene's world, over the arena's collision (#10216).
        combat.Orc.RagdollWorld = scene.World;
        if (combat.Mate is { } mate) mate.RagdollWorld = scene.World;
        combat.Rebuild(BuildFile.Load(engine), draw: false);
        time = new WorldTime(engine);
        developer = new LabDeveloper(engine, scene, player, cubes, combat, time, donor.Source, this, Observe, Publish);
        hud = engine.Ui.OpenStream(new UiStreamRequest("darkmessiah-lab", "rusty-bridge.lab.hud"));
        notice = $"Donor content: {donor.Source}";
        PlaceAtSpawn();
    }

    // For the smoke host's checks.
    internal LabCombat Combat => combat;
    internal PlayerController Player => player;
    internal DonorContent Donor => donor;

    public void Start()
    {
        started = true;
        paused = false;
        time.Apply(); // selecting gameplay time makes every host observation an update, admitted steps or not
        Publish();
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        if (!started || paused || disposed) return ProductUpdateResult.None;
        ProductUpdateFacts facts = update.Facts;
        generation = facts.Generation;
        time.Observe(facts);
        diagnostics.Capture(update.Input, facts.SimulationStep);
        ReloadLive(update.ContentChanges);
        bool resetRequested = false;
        foreach (ProductInputEvent input in update.Input)
        {
            if (input.Kind == InputEventKind.Clear) ClearPending();
            resetRequested |= ResetClaimed(input);
        }
        // Look and controls on host time, so they keep their speed while the world holds or slows; the body per step.
        FpsInputFrame frame = player.ReadInput(update.Input, (float)facts.HostElapsedSeconds);
        PhysicalInputState physical = player.Input.Physical;
        if (resetRequested || physical.Pressed(KeyboardControl.KeyR))
        {
            ResetLab();
            frame = default;
        }
        else
        {
            jumpPending |= frame.JumpPressed;
            usePending |= frame.UsePressed;
            // Holding a cube, primary throws it; otherwise the sword reads the buttons as held, every step.
            throwPending |= cubes.Held is not null && physical.Pressed(PointerButton.Primary);
            kickPending |= physical.Pressed(KeyboardControl.KeyF);
            attackPending |= cubes.Held is null && (physical.Pressed(PointerButton.Primary) || physical.Pressed(ControllerButton.Button7));
            blockPending |= physical.Pressed(PointerButton.Secondary) || physical.Pressed(ControllerButton.Button6);
            if (physical.Pressed(KeyboardControl.KeyG)) combat.God = !combat.God;
            // The belt's slots on 1 to 9, as the donor's usebelt1–9 (#10162).
            for (int slot = 0; slot < PlayerBelt.Slots; slot++)
                if (physical.Pressed((KeyboardControl)((int)KeyboardControl.Digit1 + slot))) combat.UseBelt(slot);
            if (physical.Pressed(KeyboardControl.KeyP)) time.Toggle();
            if (physical.Pressed(KeyboardControl.KeyO)) time.Step(WorldTime.StepSeconds);
        }
        bool attackHeld = cubes.Held is null && (physical.Held(PointerButton.Primary) || physical.Held(ControllerButton.Button7));
        bool blockHeld = physical.Held(PointerButton.Secondary) || physical.Held(ControllerButton.Button6);
        bool kickHeld = physical.Held(KeyboardControl.KeyF);
        float delta = (float)facts.FixedDeltaSeconds;
        for (uint step = 0; step < facts.AdmittedStepCount; step++)
        {
            scene.SimulationStep = checked(facts.SimulationStep + step);
            ulong? held = cubes.Held?.Entity.Value;
            ReadOnlyMemory<SpatialEntityCollider> bodies = scene.Colliders();
            // The body sprints only as stamina admits it, as the sword does: not from below 3, and not once it's spent.
            bool sprintAsked = frame.SprintHeld && !frame.CrouchHeld && frame.Movement != Vector2.Zero;
            var body = frame with { SprintHeld = combat.Stamina.Admits(sprintAsked) };
            player.Step(body, jumpPending, delta, scene.Obstacles(held).Concat(OrcObstacles()).ToArray(), scene.Support(player.Motion, held), bodies, combat.MovementScale);
            jumpPending = false;
            cubes.Interaction.Update();
            if (usePending) cubes.Use();
            if (throwPending && cubes.Held is not null) cubes.Throw();
            usePending = throwPending = false;
            combat.Step(delta, scene.SimulationStep, attackHeld || attackPending, blockHeld || blockPending, kickHeld || kickPending, player.Eye, player.Forward, player.Feet,
                move: frame.Movement, grounded: player.Motion.Grounded, sprinting: sprintAsked,
                crouched: player.Motion.Stance == CharacterStance.Crouched);
            kickPending = attackPending = blockPending = false;
            // The first person draws from the arms' eyes, turned by the head (#10161).
            player.Camera = combat.FirstPerson.Camera;
            scene.Step(delta, cubes.PrepareStep());
        }
        time.SetSlow(combat.TimeScale);
        // Facts identify the first admitted step; the published state is at the end boundary.
        scene.SimulationStep = checked(facts.SimulationStep + facts.AdmittedStepCount);
        worldSeconds = scene.SimulationStep * facts.FixedDeltaSeconds;
        cubes.Interaction.Update();
        combat.Present(facts.HostElapsedSeconds);
        Publish();
        return ProductUpdateResult.None;
    }

    /// <summary>A reset claimed from a paused menu applies at once.</summary>
    public void HandlePausedIntents(ReadOnlySpan<ProductInputEvent> intents)
    {
        if (disposed) return;
        foreach (ProductInputEvent input in intents)
            if (ResetClaimed(input)) { ResetLab(); return; }
    }

    public void Pause() { paused = true; player.Input.Physical.Clear(); ClearPending(); }
    public void Resume() { paused = false; player.Input.Physical.Clear(); ClearPending(); }

    /// <summary>An Engine restart returns time to realtime; the lab resets and selects its world time again.</summary>
    public void Restart()
    {
        if (disposed) return;
        ResetLab();
        paused = false;
        time.Apply();
    }

    public void Shutdown() => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cubes.Dispose();
        player.Dispose();
        // The scene publishes an empty snapshot first, so the fight's appearances leave it before they're released.
        scene.Dispose();
        combat.Dispose();
        hud.Dispose();
        donor.Dispose();
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        registrar.Register(new PlaytestDebugModule(Observe, Action, PlaytestActions, LookBy));
        registrar.Register(new InteractionDebugModule(cubes.Interaction));
        registrar.Register(developer);
        registrar.Register(new LabTunables(engine.ContentSource, player, combat, time).Module());
        registrar.Register(new InspectionTargetsDebugModule(engine.Graphics, developer.Targets));
        registrar.Register(new SpatialInspectionDebugModule(developer.Grid, developer.Probe, developer.Jump));
        registrar.Register(new SpatialClearanceDebugModule(developer.Clearance));
        EntityStoreDebugModule entities = new();
        entities.RegisterProjection(EngineComponentTypes.CharacterMotion, static (in CharacterMotion m) => FormattableString.Invariant(
            $"grounded={m.Grounded};stance={m.Stance};velocity={m.ControlledVelocity.X:F2},{m.ControlledVelocity.Y:F2},{m.ControlledVelocity.Z:F2};jumpBuffer={m.JumpBufferRemaining:F3};coyote={m.CoyoteRemaining:F3}"));
        entities.RegisterStore("lab", scene.Entities);
        registrar.Register(entities);
    }

    private static readonly string[] PlaytestActions =
        ["forward", "back", "left", "right", "jump", "crouch", "sprint", "use", "attack", "power", "parry", "kick", "throw",
         "god", "belt1", "hold-world", "step-world", "reset"];

    private PlaytestAction Action(string id) => id switch
    {
        "forward" => new(id, "KeyW", 200, true),
        "back" => new(id, "KeyS", 200, true),
        "left" => new(id, "KeyA", 200, true),
        "right" => new(id, "KeyD", 200, true),
        "jump" => new(id, "Space", 150, false),
        "crouch" => new(id, "ControlLeft", 200, true),
        "sprint" => new(id, "ShiftLeft", 200, true, HeldKeys: new[] { "KeyW" }),
        "use" => new(id, "KeyE", 100, false),
        // A tap is a fast attack; a hold past the wind-up (its _in clip) is a power attack.
        "attack" => new(id, "Primary", 100, false, cubes.Held is null && !combat.Sword.Busy,
            cubes.Held is not null ? "Holding a cube: primary throws it" : combat.Sword.Busy ? "A swing is under way" : null),
        "power" => new(id, "Primary", 900, false, cubes.Held is null && !combat.Sword.Busy,
            cubes.Held is not null ? "Holding a cube: primary throws it" : combat.Sword.Busy ? "A swing is under way" : null),
        "parry" => new(id, "Secondary", 800, false),
        "kick" => new(id, "KeyF", 100, false),
        "god" => new(id, "KeyG", 100, false),
        "belt1" => new(id, "Digit1", 100, false),
        "throw" => new(id, "Primary", 100, false, cubes.Held is not null, cubes.Held is null ? "No cube held" : null),
        "hold-world" => new(id, "KeyP", 100, false),
        "step-world" => new(id, "KeyO", 100, false),
        "reset" => new(id, "KeyR", 100, false),
        _ => new(id, "", 0, false, false, "Unknown lab action")
    };

    private DebugCommandResult LookBy(double yaw, double pitch)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(pitch) || Math.Abs(yaw) > 360 || Math.Abs(pitch) > 180)
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "Look degrees exceed bounds.");
        player.LookBy(yaw, pitch);
        cubes.Interaction.Update();
        Publish();
        return Observe();
    }

    private DebugCommandResult Observe() => DebugCommandResult.Success(JsonSerializer.Serialize(Observation(), ObservationJson.Default.LabObservation));

    private LabObservation Observation()
    {
        static float[] Vec(Vector3 value) => [value.X, value.Y, value.Z];
        CharacterMotion motion = player.Motion;
        CharacterStepReceipt? receipt = player.LastStep;
        BodyEvents body = player.Body;
        var sword = combat.Sword;
        var orc = combat.Orc;
        LabBlowFacts? blow = combat.LastBlow is { } b
            ? new(b.Swing, b.Move, b.Cycle, Vec(b.Start), Vec(b.End), b.Point is Vector3 p ? Vec(p) : null, b.Outcome) : null;
        return new LabObservation(new LabStamp(generation, scene.SimulationStep), paused ? "paused" : "playing", time.Describe(),
            new LabPlayerFacts(Vec(player.Position), Vec(player.Feet), Vec(player.Eye), Vec(motion.ControlledVelocity + motion.ExternalVelocity),
                player.LookState.YawRadians * 180 / MathF.PI, player.LookState.PitchRadians * 180 / MathF.PI, motion.Grounded,
                motion.Stance.ToString(), motion.SupportEntityPresent ? motion.SupportEntity : null,
                receipt?.Contact.Kind.ToString() ?? "None", receipt?.Contact.Present == true && receipt.Value.Contact.SourceEntity != 0 ? receipt.Value.Contact.SourceEntity : null,
                receipt?.BlockFlags.ToString() ?? "None", motion.JumpBufferRemaining, motion.CoyoteRemaining,
                body.Jumps, body.Landings, body.HardLandings, body.LastLandingSpeed, player.Recoveries, player.LastRecovery),
            new LabFightFacts(sword.Move.ToString(), sword.Clip.Clip, sword.Cycle, sword.Traced, combat.PlayerHealth, combat.God,
                combat.Player.Swings, combat.Player.Hits, combat.Player.Parried, combat.Player.Blocked, combat.Player.Missed, combat.Player.Dealt,
                orc.State.ToString(), orc.Clip, orc.Cycle, orc.Health, Vec(orc.Feet), Vector3.Distance(orc.Feet with { Y = 0 }, player.Feet with { Y = 0 }),
                combat.OrcStats.Blows, combat.OrcStats.Reached, combat.OrcStats.Parried, combat.OrcStats.Broken, combat.OrcStats.Dealt, combat.OrcStats.Deaths,
                blow, donor.Source, combat.Health.Max, combat.Belt.Describe()),
            scene.Cubes.Select(cube => new LabCubeFacts(cube.Entity.Value, cube.Definition.Label, cube.Definition.Size, cube.Definition.Mass,
                Vec(scene.Pose(cube).Translation), Vec(scene.Motion(cube).LinearVelocity), scene.Motion(cube).Sleeping, cube == cubes.Held)).ToArray(),
            cubes.TargetLabel, cubes.Held is null && combat.Message.Length > 0 ? combat.Message : cubes.Message,
            cubes.Held?.Entity.Value, cubes.Pickups, cubes.Throws, cubes.Drops, false, diagnostics.Readout(), notice);
    }

    private void Publish()
    {
        player.Publish(worldSeconds);
        scene.Publish(combat.Facts());
        engine.Ui.PublishProjection(new UiProjection(hud, LabHud.Create(Observation())));
    }

    // Under rusty dev an edited (or tunables-saved) live file arrives here instead of a restage: read it again,
    // validated as at load, and hand it to its owner. A file that does not load is reported and the last values kept.
    private void ReloadLive(ReadOnlySpan<ContentReferenceInfo> changes)
    {
        foreach (ContentReferenceInfo change in changes)
        {
            try
            {
                if (change.Path == PlayerTuning.Path) player.Retune(PlayerTuning.Load(engine));
                else if (change.Path == CombatTuning.Path) combat.Retune(CombatTuning.Load(engine));
                else if (change.Path == BuildFile.Path) combat.Rebuild(BuildFile.Load(engine));
                else continue;
                notice = $"Reloaded {change.Path}";
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException or EngineCallException)
            {
                notice = $"Not reloaded: {error.Message}";
                Console.Error.WriteLine(notice);
            }
        }
    }

    private static bool ResetClaimed(ProductInputEvent input) =>
        input.Kind == InputEventKind.DirectDigital && input.Intent.Span.SequenceEqual("reset"u8) && input.X > .5f;

    // A replay drives the lab's own player body with the recorded movement, view, jump and use, in the arena, through
    // the same step the player takes in play (CombatReplay; docs/lab.md, "Replay").
    void IReplayBody.Begin() => ResetLab();

    /// <summary>The input frame the replay body last stepped with (the smoke checks held jump and crouch carry through).</summary>
    internal FpsInputFrame LastReplayInput { get; private set; }

    (Vector3 Feet, bool Jumped, bool Grounded) IReplayBody.Step(Vector2 move, Vector3 forward, bool jump, bool jumpHeld, bool crouchHeld, bool use, float seconds, bool sprint)
    {
        var flat = new Vector2(forward.X, forward.Z);
        if (flat.LengthSquared() > 1e-6f) player.Face(MathF.Atan2(forward.X, -forward.Z), MathF.Asin(Math.Clamp(-forward.Y, -1, 1)));
        bool grounded = player.Motion.Grounded;
        var frame = new FpsInputFrame(move, jumpHeld || jump, jump, crouchHeld, sprint, use, use, default, default);
        LastReplayInput = frame;
        player.Step(frame, jump, seconds, scene.Obstacles(null).ToArray(), scene.Support(player.Motion, null), scene.Colliders());
        if (use) cubes.Use();
        cubes.Interaction.Update();
        scene.Step(seconds, cubes.PrepareStep());
        return (player.Feet, grounded && !player.Motion.Grounded && player.Motion.ControlledVelocity.Y > 0, player.Motion.Grounded);
    }

    void IReplayBody.Return()
    {
        // lab.json's replayFeet, at rest.
        var middle = LabDefinition.Vector(scene.Definition.ReplayFeet);
        player.Place(middle + Vector3.UnitY * (player.Tuning.Height / 2 + player.Config.Shape.ContactSkin), player.LookState.YawRadians * 180 / MathF.PI);
    }

    void IReplayBody.Lift(float metres) =>
        player.Place(player.Position + Vector3.UnitY * metres, player.LookState.YawRadians * 180 / MathF.PI);

    void IReplayBody.End() => ResetLab();

    private void ResetLab()
    {
        cubes.Reset();
        scene.Reset();
        combat.Reset();
        PlaceAtSpawn();
        player.ResetCounters();
        ClearPending();
        Publish();
    }

    private void PlaceAtSpawn()
    {
        Vector3 feet = LabDefinition.Vector(scene.Definition.SpawnFeet);
        player.Place(feet + Vector3.UnitY * (player.Tuning.Height / 2 + player.Config.Shape.ContactSkin), scene.Definition.SpawnYawDegrees);
    }

    private void ClearPending() => jumpPending = usePending = throwPending = kickPending = attackPending = blockPending = false;

    /// <summary>The orcs as the player's body meets them: each one's box, standing or lying (R10152-2), so the body can't walk through it.</summary>
    private IEnumerable<CharacterObstacle> OrcObstacles()
    {
        foreach (var orc in combat.Mate is { } mate ? [combat.Orc, mate] : new[] { combat.Orc })
        {
            var (center, half) = orc.Hull;
            yield return new CharacterObstacle(orc.Id, new Transform(center, Quaternion.Identity, Vector3.One), -half, half, orc.Alive, Vector3.Zero, Vector3.Zero);
        }
    }
}
