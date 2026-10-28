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
    float MoveYaw90, int JumpSame, IReadOnlyDictionary<(string Donor, string Lab), int> Disagree)
{
    internal float Agreement => Ticks > 0 ? Same / (float)Ticks : 0;

    public override string ToString() => FormattableString.Invariant(
        $"{Recording}: {Same} of {Ticks} ticks the same clip ({Agreement:P0}), cycle off {Cycle:0.000} (90%: {Cycle90:0.000}), facing {Yaw:0.0}° ({Yaw90:0.0}°)")
        + (MoveTicks > 0 ? FormattableString.Invariant($", move_yaw {MoveYaw:0.0}° ({MoveYaw90:0.0}°) over {MoveTicks} moving ticks") : "")
        + FormattableString.Invariant($", jump_idle alike {JumpSame / (float)Math.Max(1, Ticks):P0}; most often instead: ")
        + string.Join(", ", Disagree.OrderByDescending(d => d.Value).Take(4).Select(d => $"{d.Key.Donor}/{d.Key.Lab} {d.Value}"));
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
        || clip.StartsWith("Player_Turn_45_", StringComparison.Ordinal) || clip.StartsWith("attack_kick_", StringComparison.Ordinal);

    internal static BodyMatch Measure(ModelInfo body, string path, (int From, int To)? trace = null)
    {
        var animator = new BodyAnimator(body);
        var cycles = new List<float>();
        var yaws = new List<float>();
        var moveYaws = new List<float>();
        var disagree = new Dictionary<(string, string), int>();
        int ticks = 0, same = 0, jumpSame = 0, elsewhere = 0;
        bool resync = false;
        float lastYaw = float.NaN;
        // The donor's player origin moves on every other tick: velocity and height are over two.
        Vector3? last = null, before = null;
        bool started = false;
        string lastDonor = "";
        int lastTick = 0;
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
            bool grounded = MathF.Abs(at.Z - earlier.Z) < .5f;
            // Whether a kick goes is the sword's (stamina, its own moves), not the body's: the donor's kick starting is the cue.
            bool kickStarted = donor.StartsWith("attack_kick_", StringComparison.Ordinal) && donor != lastDonor;
            animator.Step(new BodyInput(seconds, velocity, f.ClientView.ViewYaw, f.ClientView.ViewPitch, (f.Input.Buttons & Movement) != 0,
                f.Input.Actions.HasFlag(PlayerActions.Sprint), f.Input.Buttons.HasFlag(InputButtons.Duck), grounded, kickStarted));
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
            else if (MathF.Abs(BodyAnimator.Wrap(e.Yaw - lastYaw)) > 20 && !donor.StartsWith("Player_Turn_", StringComparison.Ordinal)) animator.Face(e.Yaw);
            lastYaw = e.Yaw;
            if (trace is { } window && f.Tick >= window.From && f.Tick <= window.To)
                Console.WriteLine(FormattableString.Invariant($"  body {f.Tick} donor {donor}@{e.Cycle:0.00} lab {animator.Clip}@{animator.Cycle:0.00} speed {velocity.Length():0} moving {(f.Input.Buttons & Movement) != 0} grounded {grounded} view {f.ClientView.ViewYaw:0} donorYaw {e.Yaw:0} labYaw {animator.Yaw:0}"));
            ticks++;
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
        return new BodyMatch(Path.GetFileNameWithoutExtension(path), ticks, same, c50, c90, y50, y90, moveYaws.Count, m50, m90, jumpSame, disagree);
    }
}
