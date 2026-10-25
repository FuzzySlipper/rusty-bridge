using System.Numerics;
using System.Text.Json;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Interaction;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Replay;
using DarkMessiah.Lab.Scene;
using DarkMessiah.Lab.Time;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace DarkMessiah.Lab.Developer;

/// <summary>
/// The developer and agent access behind the generated command catalog: a repeatable viewpoint, world time, action
/// choice, inspection targets and the Engine's spatial readouts for the player's body. Every change goes through the
/// lab's owners; the readouts query the Engine and change nothing.
/// Provenance: rusty-hotel <c>HotelDeveloper.View</c> (<c>hotel.dev.view</c>, here with height and a clear-floor
/// search shared with recovery); rusty-doom RoomStudy <c>LoadingBayPlaytestSpatial</c> (grid, probe, jump plan and
/// clearance adapters).
/// </summary>
internal sealed class LabDeveloper(IEngineContext engine, LabScene scene, PlayerController player, CubeInteraction cubes,
    LabCombat combat, WorldTime time, string donorSource, IReplayBody replayBody, Func<DebugCommandResult> observe, Action changed) : IDebugCommandModule
{
    private const float ProbeLimit = 8, ClearanceLimit = 8;
    // The most cells one grid readout answers.
    private const int GridCells = 8192;

    [DebugCommand("lab.inspect", Description = "Read the lab: player body and look, last step, input, the fight, world time, cubes and counts.")]
    public DebugCommandResult Inspect() => observe();

    [DebugCommand("lab.dev.view", Description = "Developer override: stand with feet at x y z facing yaw and pitch in degrees (0 yaw faces -Z, positive turns right), at the nearest clear, floored place within 2 m, for repeatable captures from one viewpoint.")]
    public DebugCommandResult View(float x, float y, float z, float yaw, float pitch)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(yaw) || !float.IsFinite(pitch))
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "The point and angles must be finite numbers.");
        if (player.PlaceClear(new Vector3(x, y, z), yaw, pitch, scene.Colliders()) is null)
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, FormattableString.Invariant($"No clear, floored place for the body near [{x}, {y}, {z}]."));
        cubes.Interaction.Update();
        changed();
        return observe();
    }

    [DebugCommand("lab.time", Description = "Read the lab's world time: realtime, slow, held or stepping.")]
    public DebugCommandResult Time() => DebugCommandResult.Success(FormattableString.Invariant($"{time.Describe()}; rate {time.Rate:0.###}; step {scene.SimulationStep}"));

    [DebugCommand("lab.time.rate", Description = "Run the world at RATE of realtime, above 0 and at most 1 (slow motion below 1). Look stays at full speed.")]
    public DebugCommandResult Rate(double rate) => Result(time.SetRate(rate));

    [DebugCommand("lab.time.hold", Description = "Hold the world: no simulation steps, while look, camera and HUD keep updating.")]
    public DebugCommandResult Hold() { time.Hold(); return Time(); }

    [DebugCommand("lab.time.run", Description = "Run the world again at the chosen rate.")]
    public DebugCommandResult Run() { time.Run(); return Time(); }

    [DebugCommand("lab.time.step", Description = "Run SECONDS of world time (whole fixed steps, at the chosen rate), then hold.")]
    public DebugCommandResult Step(double seconds) => Result(time.Step(seconds));

    [DebugCommand("lab.orc.spawn", Description = "Bring the orc back at its spawn with full health.")]
    public DebugCommandResult Spawn() { combat.Orc.Spawn(); changed(); return observe(); }

    [DebugCommand("lab.orc.place", Description = "Put the orc's feet at x y z (metres); it turns to the player and fights from there.")]
    public DebugCommandResult Place(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "The point must be finite numbers.");
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(new Vector3(x, y, z));
        changed();
        return observe();
    }

    [DebugCommand("lab.orc.hold", Description = "Hold the orc's attacks (on), or let it fight (off).")]
    public DebugCommandResult HoldOrc(string state)
    {
        if (state is not ("on" or "off")) return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "on or off.");
        combat.Orc.Hold = state == "on";
        changed();
        return DebugCommandResult.Success(combat.Orc.Hold ? "orc holds" : "orc fights");
    }

    [DebugCommand("lab.orc.knockdown", Description = "Knock the orc down with a felling blow from the player's side, turned DEGREES about it (0 from the player, 180 from behind it), its impact SIDEWAYS (the move's impactdirY: 1 to the attacker's left as the power attack's, 0 straight on): it falls as a ragdoll (#10216).")]
    public DebugCommandResult Knockdown(float degrees, float sideways)
    {
        if (!float.IsFinite(degrees) || !float.IsFinite(sideways)) return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "DEGREES and SIDEWAYS must be finite numbers.");
        var orc = combat.Orc;
        var toPlayer = (player.Feet - orc.Feet) with { Y = 0 };
        if (toPlayer.LengthSquared() < 1e-6f) toPlayer = -orc.Facing;
        var from = orc.Feet + Vector3.Transform(Vector3.Normalize(toPlayer) * 1.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180));
        var outcome = orc.Struck(new DarkMessiah.Combat.AttackEvent(1, false, ChanceToFall: 1, ImpactX: sideways == 0 ? 1 : 0, ImpactY: sideways), 0, from, false, 1);
        changed();
        return DebugCommandResult.Success($"{outcome}; {RagdollText()}");
    }

    [DebugCommand("lab.orc.ragdoll", Description = "Read the orc's ragdoll: falling, lying or blending back, the side it fell to and lies on, and every knockdown so far (fell as, lay on, seconds to getting up).")]
    public DebugCommandResult Ragdoll() => DebugCommandResult.Success(RagdollText());

    private string RagdollText()
    {
        var orc = combat.Orc;
        var r = orc.RagdollState;
        var all = orc.Knockdowns;
        int other = all.Count(k => k.FellAs != k.LayOn);
        return FormattableString.Invariant(
            $"state {orc.State} {orc.Clip}; {orc.RagdollBodies}; facing {orc.Facing.X:0.00} {orc.Facing.Z:0.00}; ragdolled {r.Ragdolled} settled {r.Settled} blend {r.Blend:0.00} fell as {r.FellAs} lies on {r.LiesOn}; feet {orc.Feet.X:0.00} {orc.Feet.Y:0.00} {orc.Feet.Z:0.00}; knockdowns {all.Count}, the other side {other}, seconds {string.Join(" ", all.Select(k => $"{k.FellAs[0]}{k.LayOn[0]}:{k.Seconds:0.00}"))}");
    }

    [DebugCommand("lab.god", Description = "Toggle whether the orc's blows take the player's health (they are still counted).")]
    public DebugCommandResult God() { combat.God = !combat.God; changed(); return DebugCommandResult.Success(combat.God ? "god on" : "god off"); }

    [DebugCommand("lab.spell", Description = "Put spells.txt's SPELL in hand as its belt slot would (one cast as it's put in hand, such as water_1, the wizard eye, casts at once), or back to the weapon if it's there.")]
    public DebugCommandResult Spell(string spell)
    {
        combat.ToggleSpell(spell);
        changed();
        return DebugCommandResult.Success(FormattableString.Invariant($"{combat.Message}; wizard eye {combat.WizardEye.Left:0.0} s, fade {combat.WizardEye.Fade:0.00}"));
    }

    [DebugCommand("lab.fight", Description = "Read the fight: the sword's move, the orc, the last blow and the counts.")]
    public DebugCommandResult Fight() => observe();

    [DebugCommand("lab.replay", Description = "Replay a recording's input (Wire v12 or later; a path, or a name in local/recordings) against the donor orc the player faced, and report where the lab's moves and blows agree with the donor's. The fight starts over afterwards.")]
    public DebugCommandResult Replay(string recording)
    {
        string? path = File.Exists(recording) ? recording
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(donorSource.TrimEnd('/', '\\')))!, "recordings", recording) is var local && File.Exists(local) ? local : null;
        if (path is null) return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, $"No recording {recording} (a path, or a name in local/recordings).");
        var report = CombatReplay.Run(combat, path, replayBody);
        changed();
        return report.Ticks == 0
            ? DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, $"{report.Name} has no input to replay: it needs Wire v12 or later.")
            : DebugCommandResult.Success(report.ToString());
    }

    /// <summary>
    /// The lab's named inspection points (Engine <see cref="InspectionTargetsDebugModule"/>): the eye, the sword's reach
    /// in camera space (so an inspection view frames the swing as the player turns), the orc, the last blow, the held
    /// cube and the spawn.
    /// </summary>
    internal IReadOnlyList<InspectionTarget> Targets()
    {
        float reach = combat.FirstPerson.Range;
        List<InspectionTarget> targets =
        [
            new("eye", OverlayPlace.World, player.Eye) { Radius = 1, Framing = "side" },
            new("reach", OverlayPlace.Camera, new Vector3(0, 0, -reach / 2)) { Radius = reach / 2 + .5f, Framing = "side" },
            new("orc", OverlayPlace.World, combat.Orc.Feet + Vector3.UnitY) { Radius = 1.5f, Framing = "side" },
            new("spawn", OverlayPlace.World, LabDefinition.Vector(scene.Definition.SpawnFeet)) { Radius = 2, Framing = "top" },
        ];
        if (combat.LastBlow?.Point is Vector3 blow) targets.Add(new("last-blow", OverlayPlace.World, blow) { Radius = 1, Framing = "side" });
        if (cubes.Held is { } held) targets.Add(new("held-cube", OverlayPlace.Model(held.Entity.Value), Vector3.Zero) { Radius = held.Size + .5f, Framing = "front" });
        return targets;
    }

    internal DebugCommandResult Grid(int radius, int verticalRadius, double cellSize)
    {
        if (radius is < 0 or > 15 || verticalRadius is < 0 or > 15 || !double.IsFinite(cellSize) || cellSize < .125 || cellSize > 2 ||
            (2 * radius + 1) * (2 * radius + 1) * (2 * verticalRadius + 1) > GridCells)
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, $"Grid: radii 0..15, cell size .125..2, at most {GridCells} cells.");
        Vector3 origin = player.Feet - new Vector3((float)((radius + .5) * cellSize), (float)((verticalRadius + .5) * cellSize), (float)((radius + .5) * cellSize));
        uint size = (uint)(2 * radius + 1);
        SpatialMapRequest request = new(scene.Session, origin, cellSize, size, size, origin.Y, origin.Y + cellSize, origin.Y, origin.Y + cellSize, scene.Colliders());
        return DebugCommandResult.Success(SpatialGridSnapshot.Capture(engine.Spatial, request, (uint)(2 * verticalRadius + 1),
            new SpatialMapObservation($"step:{scene.SimulationStep}", player.Position, player.Forward)));
    }

    internal DebugCommandResult Probe(double distance)
    {
        if (!double.IsFinite(distance) || distance <= 0 || distance > ProbeLimit)
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, $"Probe distance must be in (0, {ProbeLimit}].");
        using JsonDocument rays = JsonDocument.Parse(PlaytestTraversal.Probe(engine.Spatial, scene.Session, player.Feet, player.Height,
            player.Tuning.MaximumStepHeight, (float)distance, scene.Colliders()));
        return DebugCommandResult.Success(JsonSerializer.Serialize(new ProbeReadout(scene.SimulationStep, LastStep(), rays.RootElement), DeveloperJson.Default.ProbeReadout));
    }

    internal DebugCommandResult Jump(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "Target feet must be finite world XYZ.");
        PlaytestJumpPlan plan = PlaytestTraversal.JumpToward(player.Feet, player.Forward, new Vector3((float)x, (float)y, (float)z),
            player.Config, player.Motion.Grounded, "Space", "KeyW");
        return DebugCommandResult.Success(JsonSerializer.Serialize(plan, DeveloperJson.Default.PlaytestJumpPlan));
    }

    internal DebugCommandResult Clearance(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, "Target feet must be finite world XYZ.");
        Vector3 target = new((float)x, (float)y, (float)z);
        if (Vector3.Distance(player.Feet, target) > ClearanceLimit)
            return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, $"Clearance target must be within {ClearanceLimit} m of the player's feet.");
        return DebugCommandResult.Success(SpatialClearanceSnapshot.Capture(engine.Spatial, scene.Session, player.Position, target,
            player.Height, player.Config, scene.Colliders()));
    }

    private string LastStep() => player.LastStep is not { } r ? "none" : FormattableString.Invariant(
        $"attempted={r.Step.Attempted};accepted={r.Step.Accepted};wish={r.WishVelocity.X:F3},{r.WishVelocity.Y:F3},{r.WishVelocity.Z:F3};displacement={r.Displacement.X:F3},{r.Displacement.Y:F3},{r.Displacement.Z:F3};blocked={r.BlockFlags};contact={r.Contact.Kind};casts={r.CastCount}");

    private DebugCommandResult Result(string? refusal)
    {
        if (refusal is not null) return DebugCommandResult.Failure(DebugCommandStatus.InvalidArguments, refusal);
        changed();
        return Time();
    }
}

internal sealed record ProbeReadout(ulong Step, string LastStep, JsonElement Rays);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProbeReadout))]
[System.Text.Json.Serialization.JsonSerializable(typeof(PlaytestJumpPlan))]
internal sealed partial class DeveloperJson : System.Text.Json.Serialization.JsonSerializerContext;
