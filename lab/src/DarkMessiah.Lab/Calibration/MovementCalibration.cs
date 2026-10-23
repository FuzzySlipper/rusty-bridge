using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A player command as the movement calibration sorts them (#10159): its direction, size and modifiers.</summary>
/// <param name="Direction">forward, back, strafe, diagonal (forward and side), back-diagonal.</param>
/// <param name="Size">The command's magnitude, Source units a second, rounded to 50 (1000 a run, 450 a walk).</param>
/// <param name="Modifier">none, crouched (duck held), sprint (Arkane's +0x95), charge (attack held), guard (attack2 held).</param>
internal readonly record struct MoveCommand(string Direction, int Size, string Modifier)
{
    public override string ToString() => $"{Direction} {Size}{(Modifier == "none" ? "" : " " + Modifier)}";
}

/// <summary>
/// The donor's movement against its commands (#10159), from Wire v14 recordings (the user command and the player's
/// origin every tick): steady speed by command, how long it took to get there from standing and to stop, and jumps.
/// Speeds are planar, Source units a second; ticks moving more than 30 units a second up or down (jumps, falls, stairs)
/// are left out of the speeds.
/// </summary>
/// <summary>
/// One tick of a player's movement, Source units (Z up): where it stood, its command, whether jump went down, and
/// whether it attacked or kicked (an attack or kick in the air cuts the donor's jump short: those jumps aren't measured).
/// </summary>
/// <param name="Yaw">Where the player looked, degrees (NaN if not known): a steady speed is taken only running straight.</param>
internal readonly record struct MovementSample(double Time, float X, float Y, float Z, MoveCommand? Command, bool JumpPressed, bool Acting = false,
    float Yaw = float.NaN);

internal sealed record MovementMeasure(Dictionary<MoveCommand, List<float>> Steady, Dictionary<MoveCommand, List<float>> Accelerations,
    List<(float Seconds, float Slide, float From)> Stops, List<(float Rise, float Air, float Speed, string Held)> Jumps, Dictionary<MoveCommand, List<float>> Gaps)
{
    internal static float Median(IReadOnlyList<float> values) => values.Count == 0 ? float.NaN : values.Order().ElementAt(values.Count / 2);

    internal static MovementMeasure Empty() => new([], [], [], [], []);
}

internal static class MovementCalibration
{
    /// <summary>A command held this long is steady, seconds.</summary>
    const float SteadyAfter = .6f;
    /// <summary>Faster than this a step is a teleport (setpos) or a recording gap, units a second.</summary>
    const float Teleport = 2000;
    /// <summary>Speeds are taken over this long, seconds.</summary>
    internal const float Window = .2f;

    internal static MoveCommand? Command(in WirePlayerInput input)
    {
        float f = input.ForwardMove, s = input.SideMove;
        float size = MathF.Sqrt(f * f + s * s);
        if (size < 1) return null;
        string direction = MathF.Abs(s) < 1 ? (f > 0 ? "forward" : "back") : MathF.Abs(f) < 1 ? "strafe" : f > 0 ? "diagonal" : "back-diagonal";
        var held = input.CommandButtons | input.Buttons;
        string modifier = (input.Actions & PlayerActions.Sprint) != 0 ? "sprint" : held.HasFlag(InputButtons.Duck) ? "crouched"
            : held.HasFlag(InputButtons.Attack) ? "charge" : held.HasFlag(InputButtons.Attack2) ? "guard" : "none";
        return new MoveCommand(direction, (int)MathF.Round(size / 50) * 50, modifier);
    }

    internal static MovementMeasure Measure(IEnumerable<string> recordings)
    {
        var into = MovementMeasure.Empty();
        foreach (var path in recordings)
        {
            var ticks = new List<MovementSample>();
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var f in RecordingReader.Frames(stream))
                {
                    if (f.Version < 14) break;
                    var player = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "player");
                    if (f.Classes.GetValueOrDefault(player.ClassId) != "player" || f.Input.Valid == 0 || f.Input.Commands == 0) continue;
                    double time = f.Tick * (double)f.TickInterval;
                    if (ticks.Count > 0 && time <= ticks[^1].Time) continue;
                    var held = f.Input.CommandButtons | f.Input.Buttons;
                    ticks.Add(new(time, player.OriginX, player.OriginY, player.OriginZ, Command(f.Input), f.Input.Pressed.HasFlag(InputButtons.Jump),
                        held.HasFlag(InputButtons.Attack) || held.HasFlag(InputButtons.Attack2) || (f.Input.Actions & PlayerActions.Kick) != 0, player.Yaw));
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            Analyze(ticks, into);
        }
        return into;
    }

    /// <summary>One run of samples (a recording, or a replay's lab body) measured into <paramref name="into"/>.</summary>
    internal static void Analyze(IReadOnlyList<MovementSample> ticks, MovementMeasure into)
    {
        var (steady, accelerations, stops, jumps) = (into.Steady, into.Accelerations, into.Stops, into.Jumps);
        {
            if (ticks.Count < 10) return;
            // Each tick's planar and vertical speed over the last Window seconds: the player's origin reaches the Wire
            // every few ticks, not every one.
            var speed = new float[ticks.Count];
            var climb = new float[ticks.Count];
            int back = 0;
            for (int i = 0; i < ticks.Count; i++)
            {
                while (back < i && ticks[i].Time - ticks[back + 1].Time >= Window) back++;
                double dt = ticks[i].Time - ticks[back].Time;
                if (back == i || dt < Window * .8 || dt > Window * 2) { speed[i] = climb[i] = float.NaN; continue; }
                float dx = ticks[i].X - ticks[back].X, dy = ticks[i].Y - ticks[back].Y;
                speed[i] = MathF.Sqrt(dx * dx + dy * dy) / (float)dt;
                climb[i] = (ticks[i].Z - ticks[back].Z) / (float)dt;
                if (speed[i] > Teleport) speed[i] = climb[i] = float.NaN;
            }
            bool Level(int i) => !float.IsNaN(speed[i]) && MathF.Abs(climb[i]) <= 30;
            // No turn of more than 30° in the last half second (#10160: the sprint scenario turns every second).
            bool Straight(int i)
            {
                if (float.IsNaN(ticks[i].Yaw)) return true;
                for (int k = i; k >= 0 && ticks[i].Time - ticks[k].Time <= .5; k--)
                    if (MathF.Abs(((ticks[i].Yaw - ticks[k].Yaw) % 360 + 540) % 360 - 180) > 30) return false;
                return true;
            }
            int start = 0;
            for (int i = 1; i <= ticks.Count; i++)
            {
                if (i < ticks.Count && ticks[i].Command == ticks[start].Command) continue;
                // A run of one command, ticks [start, i).
                var command = ticks[start].Command;
                double held = ticks[i - 1].Time - ticks[start].Time;
                if (command is { } c)
                {
                    for (int k = start; k < i; k++)
                        if (ticks[k].Time - ticks[start].Time >= SteadyAfter && Level(k) && Straight(k)) Add(steady, c, speed[k]);
                    // From standing: how long to 90% of the speed it settled at.
                    if (start > 0 && Level(start - 1) && speed[start - 1] < 10 && held >= 1.2)
                    {
                        var settled = Enumerable.Range(start, i - start).Where(k => ticks[k].Time - ticks[start].Time >= .8 && Level(k)).Select(k => speed[k]).ToList();
                        if (settled.Count > 5)
                        {
                            float target = MovementMeasure.Median(settled);
                            int reach = Enumerable.Range(start, i - start).FirstOrDefault(k => Level(k) && speed[k] >= .9f * target, -1);
                            if (reach >= 0 && target > 20) Add(accelerations, c, (float)(ticks[reach].Time - ticks[start - 1].Time));
                        }
                    }
                }
                else if (start > 0 && ticks[start - 1].Command is not null && Level(start - 1) && speed[start - 1] > 100 && held >= .8)
                {
                    // Released from a run: how long and how far to below 10% of the speed it had.
                    float from = speed[start - 1];
                    int stop = Enumerable.Range(start, i - start).FirstOrDefault(k => Level(k) && speed[k] < .1f * from, -1);
                    if (stop >= 0)
                    {
                        float slide = MathF.Sqrt(MathF.Pow(ticks[stop].X - ticks[start - 1].X, 2) + MathF.Pow(ticks[stop].Y - ticks[start - 1].Y, 2));
                        stops.Add(((float)(ticks[stop].Time - ticks[start - 1].Time), slide, from));
                    }
                }
                start = i;
            }
            // Jumps from level ground that land within 4 units of the take-off height.
            for (int i = 1; i < ticks.Count; i++)
            {
                if (!ticks[i].JumpPressed || !Level(i - 1)) continue;
                float ground = ticks[i - 1].Z, peak = ground;
                int k = i;
                for (; k < ticks.Count && ticks[k].Time - ticks[i].Time < 3; k++)
                {
                    peak = MathF.Max(peak, ticks[k].Z);
                    if (k > i + 3 && ticks[k].Z <= ground + 4 && peak > ground + 10 && float.IsFinite(climb[k]) && climb[k] > -1) break;
                }
                if (k < ticks.Count && peak > ground + 10 && MathF.Abs(ticks[k].Z - ground) <= 4 && !Enumerable.Range(i, k - i).Any(n => ticks[n].Acting))
                    jumps.Add((peak - ground, (float)(ticks[k].Time - ticks[i - 1].Time), speed[i - 1], string.Join("+", Enumerable.Range(i, Math.Max(0, k - i)).Select(n => ticks[n].Command?.Modifier ?? "none").Distinct())));
            }
        }

        static void Add(Dictionary<MoveCommand, List<float>> into, MoveCommand c, float value)
        {
            if (!into.TryGetValue(c, out var list)) into[c] = list = [];
            list.Add(value);
        }
    }
}
