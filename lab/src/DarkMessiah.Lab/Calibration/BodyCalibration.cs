using System.Numerics;
using DarkMessiah.Lab.Player;
using DarkMessiah.Shared;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// How the lab's body rules (<see cref="BodyAnimator"/>) play against the donor's player body over a recording (#10394):
/// ticks where the donor's player played a clip, how many the lab played the same clip on, and its cycle's, facing's and
/// <c>move_yaw</c>'s errors there (medians and 90th percentiles; <c>move_yaw</c> from v21 recordings only); and how often
/// both or neither had <c>jump_idle</c> over it. <see cref="Disagree"/> counts each pair of clips played instead.
/// </summary>
internal sealed record BodyMatch(string Recording, int Ticks, int Same, float Cycle, float Cycle90, float Yaw, float Yaw90, int MoveTicks, float MoveYaw,
    float MoveYaw90, int JumpSame, IReadOnlyDictionary<(string Donor, string Lab), int> Disagree, int LadderTicks = 0, int LadderSame = 0)
{
    internal float Agreement => Ticks > 0 ? Same / (float)Ticks : 0;
    /// <summary>Of the ticks on, getting on or getting off l01_a's ladder, how many played the same clip (R10394-1).</summary>
    internal float LadderAgreement => LadderTicks > 0 ? LadderSame / (float)LadderTicks : 0;

    public override string ToString() => FormattableString.Invariant(
        $"{Recording}: {Same} of {Ticks} ticks the same clip ({Agreement:P0}), cycle off {Cycle:0.000} (90%: {Cycle90:0.000}), facing {Yaw:0.0}° ({Yaw90:0.0}°)")
        + (MoveTicks > 0 ? FormattableString.Invariant($", move_yaw {MoveYaw:0.0}° ({MoveYaw90:0.0}°) over {MoveTicks} moving ticks") : "")
        + FormattableString.Invariant($", jump_idle alike {JumpSame / (float)Math.Max(1, Ticks):P0}")
        + (LadderTicks > 0 ? FormattableString.Invariant($", on the ladder {LadderSame} of {LadderTicks} ({LadderAgreement:P0})") : "")
        + "; most often instead: "
        + string.Join(", ", Disagree.OrderByDescending(d => d.Value).Take(4).Select(d => $"{d.Key.Donor}/{d.Key.Lab} {d.Value}"));
}

/// <summary>
/// What the donor's body does while the player leans (R10394-2), over a recording's ticks with the camera rolled
/// (<c>CameraRoll</c> fully over, v9; leaning is the client's alone): how many, the camera's roll and its move to the side of where it was before the
/// lean (Source units, medians), and the most the body rolled or its origin moved off where it stood when the lean began
/// (<c>body_roll</c> from v21's pose parameters, degrees; NaN before v21).
/// </summary>
internal sealed record BodyLean(string Recording, int Ticks, float CameraRoll, float Side, float BodyRoll, float Moved)
{
    public override string ToString() => FormattableString.Invariant(
        $"{Recording}: {Ticks} leaning ticks, the camera rolled {CameraRoll:0.0}° and {Side:0.0} units aside; the body rolled at most {BodyRoll:0.0}°, its origin moved {Moved:0.0} units");
}

/// <summary>
/// The donor's player body against the lab's rules (#10394): each tick of a recording (v12 and later, with the player's
/// input and view) tells a <see cref="BodyAnimator"/> what the donor's player did (its velocity from its origin, its view,
/// its movement keys, sprint and duck, a kick pressed, off the ground when its height moves) and compares what it plays
/// with the player entity's own sequence, cycle, facing, <c>move_yaw</c> (v21) and <c>jump_idle</c> layer.
/// </summary>
internal static class BodyCalibration
{
    const InputButtons Movement = InputButtons.Forward | InputButtons.Back | InputButtons.MoveLeft | InputButtons.MoveRight;

    /// <summary>The clips the rules play: the idles, gaits, crouches, turns on the spot and kicks.</summary>
    internal static bool Plays(string clip) => clip.StartsWith("idle", StringComparison.Ordinal) || clip is "Walk" or "run" or "sprint" or "Crouch" or "Crouch_Idle"
        || clip.StartsWith("Player_Turn_45_", StringComparison.Ordinal) || clip.StartsWith("attack_kick_", StringComparison.Ordinal)
        || clip.StartsWith("Player_ladder_", StringComparison.Ordinal);

    /// <summary>
    /// l01_a's one ladder (<c>room03.ladder.02</c>), from its BSP entities: the <c>func_useableladder</c> from (1797, −575,
    /// 554) to (1797, −543, 822) facing 90°, so its rail (the climber's origin, <c>mm_player_ladder_offset</c> 9 units out)
    /// runs from (1797, −584, 554) leaning 32 units in 268; its top's <c>info_ladder_dismount</c> at (1797, −511, 826).
    /// </summary>
    static class Room03Ladder
    {
        internal const float X = 1797, FootY = -584, FootZ = 554, Lean = 32f / 268, TopZ = 757, ExitY = -511, ExitZ = 826, Facing = 90;
        internal static float RailY(float z) => FootY + (z - FootZ) * Lean;
    }

    /// <summary>
    /// Where the donor's player is with l01_a's ladder, from where it is and how it moves (the Wire has no move type):
    /// on the rail; between the rail's top and the top's dismount rising and moving out (off at the top) or sinking and
    /// moving in (on from the top); just off the rail's foot, moving out of it, after being on it (off at the foot).
    /// </summary>
    static LadderPhase PhaseOn(Vector3 at, Vector3 earlier, LadderPhase was)
    {
        if (MathF.Abs(at.X - Room03Ladder.X) > 8) return LadderPhase.None;
        float rail = Room03Ladder.RailY(at.Z);
        if (MathF.Abs(at.X - Room03Ladder.X) < 3 && MathF.Abs(at.Y - rail) < 3 && at.Z >= Room03Ladder.FootZ - 1 && at.Z < Room03Ladder.TopZ) return LadderPhase.On;
        if (at.Z >= Room03Ladder.TopZ - 6 && at.Y > Room03Ladder.RailY(Room03Ladder.TopZ) - 4 && at.Y < Room03Ladder.ExitY && at.Z <= Room03Ladder.ExitZ + 1)
        {
            if (at.Z > earlier.Z + .1f || at.Y > earlier.Y + .1f) return LadderPhase.OffAtTop;
            if (at.Z < earlier.Z - .1f || at.Y < earlier.Y - .1f) return LadderPhase.OnFromTop;
            return was is LadderPhase.OffAtTop or LadderPhase.OnFromTop ? was : LadderPhase.None;
        }
        if (was is LadderPhase.On or LadderPhase.OffAtFoot && MathF.Abs(at.Z - Room03Ladder.FootZ) < 2 && at.Y < rail - .5f && at.Y < earlier.Y - .1f)
            return LadderPhase.OffAtFoot;
        return LadderPhase.None;
    }

    /// <summary>The donor's body while its player leans standing (R10394-2): see <see cref="BodyLean"/>.</summary>
    internal static BodyLean Leans(string path)
    {
        var rolls = new List<float>();
        var sides = new List<float>();
        float bodyRoll = float.NaN, moved = 0;
        Vector3? stood = null;
        Vector2 camera = default;
        using var stream = File.OpenRead(path);
        foreach (var f in RecordingReader.Frames(stream))
        {
            var v = f.ClientView;
            if (f.Version < 9 || !v.Flags.HasFlag(ClientViewFlags.ViewAngles) || !f.Entities.TryGetValue(1, out var e)) continue;
            var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
            // The camera's level place against the body's, as it was before the lean.
            var place = new Vector2(v.CameraX - e.OriginX, v.CameraY - e.OriginY);
            if (MathF.Abs(v.CameraRoll) < 1) camera = place;
            if (MathF.Abs(v.CameraRoll) < 14.5f || (f.Input.Buttons & Movement) != 0) { stood = null; continue; }
            stood ??= at;
            float yaw = v.ViewYaw * MathF.PI / 180;
            // Source's right is (sin yaw, −cos yaw).
            var aside = place - camera;
            rolls.Add(MathF.Abs(v.CameraRoll));
            sides.Add(MathF.Abs(Vector2.Dot(aside, new Vector2(MathF.Sin(yaw), -MathF.Cos(yaw)))));
            moved = MathF.Max(moved, Vector3.Distance(at, stood.Value));
            if (f.Version >= 21 && !float.IsNaN(f.Pose[3]))
                bodyRoll = MathF.Max(float.IsNaN(bodyRoll) ? 0 : bodyRoll, MathF.Abs(-35.8f + 71.6f * f.Pose[3]));
        }
        static float Median(List<float> x) { if (x.Count == 0) return float.NaN; x.Sort(); return x[x.Count / 2]; }
        return new BodyLean(Path.GetFileNameWithoutExtension(path), rolls.Count, Median(rolls), Median(sides), bodyRoll, moved);
    }

    internal static BodyMatch Measure(ModelInfo body, string path, (int From, int To)? trace = null)
    {
        var animator = new BodyAnimator(body);
        var cycles = new List<float>();
        var yaws = new List<float>();
        var moveYaws = new List<float>();
        var disagree = new Dictionary<(string, string), int>();
        int ticks = 0, same = 0, jumpSame = 0, elsewhere = 0, ladderTicks = 0, ladderSame = 0;
        bool resync = false;
        float lastYaw = float.NaN;
        // The donor's player origin moves on every other tick: velocity and height are over two.
        Vector3? last = null, before = null;
        bool started = false;
        string lastDonor = "";
        int lastTick = 0;
        var phase = LadderPhase.None;
        using var stream = File.OpenRead(path);
        foreach (var f in RecordingReader.Frames(stream))
        {
            if (f.Version < 12 || f.Input.Valid == 0 || !f.ClientView.Flags.HasFlag(ClientViewFlags.ViewAngles)
                || !f.Entities.TryGetValue(1, out var e) || f.Classes.GetValueOrDefault(e.ClassId) != "player" || !e.Flags.HasFlag(WireEntityFlags.HasAnimation))
            {
                last = before = null;
                continue;
            }
            var at = new Vector3(e.OriginX, e.OriginY, e.OriginZ);
            float seconds = (f.Tick - lastTick) * (f.TickInterval > 0 ? f.TickInterval : 1 / 66f);
            int gap = f.Tick - lastTick;
            lastTick = f.Tick;
            var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
            string donor = e.Sequence >= 0 && e.Sequence < names.Length ? names[e.Sequence] : "";
            if (last is not Vector3 was || before is not Vector3 earlier || gap is <= 0 or > 4)
            {
                (before, last) = (last, at);
                lastDonor = donor;
                if (!started && donor.Length > 0) { animator.Reset(e.Yaw, donor, e.Cycle); started = true; if (trace is not null) Console.WriteLine($"  body reset {f.Tick} {donor}@{e.Cycle} -> {animator.Clip}@{animator.Cycle}"); }
                continue;
            }
            var velocity = new Vector2(at.X - earlier.X, at.Y - earlier.Y) / (2 * seconds);
            phase = PhaseOn(at, earlier, phase);
            // On a ladder its height is its climb, not a fall.
            bool grounded = phase != LadderPhase.None || MathF.Abs(at.Z - earlier.Z) < .5f;
            // Asked to climb as the donor's ladders take it: forward toward where the view points, up or down, back the other way.
            float keys = (f.Input.Buttons.HasFlag(InputButtons.Forward) ? 1 : 0) - (f.Input.Buttons.HasFlag(InputButtons.Back) ? 1 : 0);
            var ladder = phase == LadderPhase.None ? default
                : new BodyLadder(phase, (at.Z - earlier.Z) / (2 * seconds), Room03Ladder.Facing, -keys * MathF.Sin(f.ClientView.ViewPitch * MathF.PI / 180));
            // Whether a kick goes is the sword's (stamina, its own moves), not the body's: the donor's kick starting is the cue.
            bool kickStarted = donor.StartsWith("attack_kick_", StringComparison.Ordinal) && donor != lastDonor;
            animator.Step(new BodyInput(seconds, velocity, f.ClientView.ViewYaw, f.ClientView.ViewPitch, (f.Input.Buttons & Movement) != 0,
                f.Input.Actions.HasFlag(PlayerActions.Sprint), f.Input.Buttons.HasFlag(InputButtons.Duck), grounded, kickStarted, ladder));
            (before, last) = (last, at);
            lastDonor = donor;
            if (donor.Length == 0) continue;
            // Grabs, throws, knockdowns and scripted scenes are other things' to play: left out, and the rules take up
            // where the donor stands after them.
            if (!Plays(donor))
            {
                elsewhere++;
                resync = true;
                continue;
            }
            if (resync) { animator.Reset(e.Yaw, donor, e.Cycle); resync = false; }
            // A setang (or setpos) turns the donor's body at once, with no turn played: the lab's body is put the same way.
            else if (MathF.Abs(BodyAnimator.Wrap(e.Yaw - lastYaw)) > 20 && !donor.StartsWith("Player_Turn_", StringComparison.Ordinal) && phase == LadderPhase.None) animator.Face(e.Yaw);
            lastYaw = e.Yaw;
            if (trace is { } window && f.Tick >= window.From && f.Tick <= window.To)
                Console.WriteLine(FormattableString.Invariant($"  body {f.Tick} donor {donor}@{e.Cycle:0.00} lab {animator.Clip}@{animator.Cycle:0.00} speed {velocity.Length():0} moving {(f.Input.Buttons & Movement) != 0} grounded {grounded} view {f.ClientView.ViewYaw:0} donorYaw {e.Yaw:0} labYaw {animator.Yaw:0}"));
            ticks++;
            if (phase != LadderPhase.None)
            {
                ladderTicks++;
                if (animator.Clip == donor) ladderSame++;
            }
            if (animator.Clip == donor)
            {
                same++;
                float d = MathF.Abs(animator.Cycle - e.Cycle);
                cycles.Add(MathF.Min(d, 1 - d));
            }
            else disagree[(donor, animator.Clip)] = disagree.GetValueOrDefault((donor, animator.Clip)) + 1;
            yaws.Add(MathF.Abs(BodyAnimator.Wrap(animator.Yaw - e.Yaw)));
            float moveYaw = f.Pose[0];
            if (!float.IsNaN(moveYaw) && velocity.Length() > 20)
                moveYaws.Add(MathF.Abs(BodyAnimator.Wrap(animator.MoveYaw - (-180 + 360 * moveYaw))));
            bool donorJump = f.Layers.Any(l => l.Index == 1 && l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length && names[l.Sequence] == "jump_idle");
            if (donorJump == animator.Layers.Any(l => l.Clip == "jump_idle")) jumpSame++;
        }
        static (float, float) Spread(List<float> v)
        {
            if (v.Count == 0) return (float.NaN, float.NaN);
            v.Sort();
            return (v[v.Count / 2], v[v.Count * 9 / 10]);
        }
        var (c50, c90) = Spread(cycles);
        var (y50, y90) = Spread(yaws);
        var (m50, m90) = Spread(moveYaws);
        return new BodyMatch(Path.GetFileNameWithoutExtension(path), ticks, same, c50, c90, y50, y90, moveYaws.Count, m50, m90, jumpSame, disagree, ladderTicks, ladderSame);
    }
}
