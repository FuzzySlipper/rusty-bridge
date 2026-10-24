using System.Numerics;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A player command as the movement calibration sorts them (#10159): its direction, size and modifiers.</summary>
/// <param name="Direction">forward, back, strafe, diagonal (forward and side), back-diagonal.</param>
/// <param name="Size">The command's magnitude, Source units a second, rounded to 50 (1000 a run, 450 a walk).</param>
/// <param name="Modifier">none, crouched (duck held), sprint (Arkane's +0x95), charge (attack held), guard (attack2 held),
/// walk (IN_WALK held), speed (IN_BULLRUSH held: Dark Messiah's +speed; R10159-1).</param>
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
/// <param name="Blocked">A wall stopped the body this tick (a replay's lab body in its own arena; R10159-2).</param>
/// <param name="Sprinting">The body sprinted this tick (a replay's lab body, as its stamina admits it).</param>
internal readonly record struct MovementSample(double Time, float X, float Y, float Z, MoveCommand? Command, bool JumpPressed, bool Acting = false,
    float Yaw = float.NaN, bool Blocked = false, bool Sprinting = false);

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
            : held.HasFlag(InputButtons.Attack) ? "charge" : held.HasFlag(InputButtons.Attack2) ? "guard"
            : held.HasFlag(InputButtons.Walk) ? "walk" : held.HasFlag(InputButtons.BullRush) ? "speed" : "none";
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

    /// <summary>
    /// What one run of samples shows, by tick index: each steady tick's speed (its run of one command starting at
    /// <c>Run</c>), each start from standing and stop from a run (by the run's first tick), and each jump (by the tick
    /// jump went down). A replay's donor and lab traces share their ticks, so their events pair by index (R10159-2).
    /// </summary>
    internal sealed record Events(List<(int Run, int Tick, MoveCommand Command, float Speed)> Steady, List<(int Run, MoveCommand Command, float Seconds)> Starts,
        List<(int Run, float Seconds, float Slide, float From)> Stops, List<(int Press, float Rise, float Air, float Speed, string Held)> Jumps, bool[] Level);

    internal static Events Find(IReadOnlyList<MovementSample> ticks)
    {
        var events = new Events([], [], [], [], new bool[ticks.Count]);
        if (ticks.Count < 10) return events;
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
        for (int i = 0; i < ticks.Count; i++) events.Level[i] = Level(i);
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
                    if (ticks[k].Time - ticks[start].Time >= SteadyAfter && Level(k) && Straight(k)) events.Steady.Add((start, k, c, speed[k]));
                // From standing: how long to 90% of the speed it settled at.
                if (start > 0 && Level(start - 1) && speed[start - 1] < 10 && held >= 1.2)
                {
                    var settled = Enumerable.Range(start, i - start).Where(k => ticks[k].Time - ticks[start].Time >= .8 && Level(k)).Select(k => speed[k]).ToList();
                    if (settled.Count > 5)
                    {
                        float target = MovementMeasure.Median(settled);
                        int reach = Enumerable.Range(start, i - start).FirstOrDefault(k => Level(k) && speed[k] >= .9f * target, -1);
                        if (reach >= 0 && target > 20) events.Starts.Add((start, c, (float)(ticks[reach].Time - ticks[start - 1].Time)));
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
                    events.Stops.Add((start, (float)(ticks[stop].Time - ticks[start - 1].Time), slide, from));
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
            // Landed: the first tick back within 4 units of the take-off height after rising over 10 (R10159-2: the
            // speed window's settling, which the donor's sparse origins and the lab's snap to the floor reach at
            // different times, isn't waited for).
            for (; k < ticks.Count && ticks[k].Time - ticks[i].Time < 3; k++)
            {
                peak = MathF.Max(peak, ticks[k].Z);
                if (k > i + 3 && ticks[k].Z <= ground + 4 && peak > ground + 10) break;
            }
            if (k < ticks.Count && peak > ground + 10 && MathF.Abs(ticks[k].Z - ground) <= 4 && !Enumerable.Range(i, k - i).Any(n => ticks[n].Acting))
                events.Jumps.Add((i, peak - ground, (float)(ticks[k].Time - ticks[i - 1].Time), speed[i - 1],
                    string.Join("+", Enumerable.Range(i, Math.Max(0, k - i)).Select(n => ticks[n].Command?.Modifier ?? "none").Distinct())));
        }
        return events;
    }

    /// <summary>One run of samples (a recording, or a replay's lab body) measured into <paramref name="into"/>.</summary>
    internal static void Analyze(IReadOnlyList<MovementSample> ticks, MovementMeasure into)
    {
        var events = Find(ticks);
        foreach (var (_, _, c, v) in events.Steady) Add(into.Steady, c, v);
        foreach (var (_, c, seconds) in events.Starts) Add(into.Accelerations, c, seconds);
        foreach (var (_, seconds, slide, from) in events.Stops) into.Stops.Add((seconds, slide, from));
        foreach (var (_, rise, air, v, held) in events.Jumps) into.Jumps.Add((rise, air, v, held));
    }

    /// <summary>A steady run takes this many paired ticks to be compared.</summary>
    const int SteadyTicks = 10;

    /// <summary>
    /// A replay's donor and lab traces case by case (R10159-2): each run of one command held steady, each start from
    /// standing, each stop from a run and each jump the donor made, against the lab's on the same ticks, with its own
    /// tolerance. A case the lab can't show is left out only on evidence: the lab's body stopped by its own arena's
    /// walls (its controller's wall contact), the donor's held by its level (it moved under a third of the lab's speed on
    /// the same command), or, for a jump, the lab's body not on level ground as jump went down.
    /// </summary>
    internal static List<MovementCase> Compare(string recording, IReadOnlyList<MovementSample> donor, IReadOnlyList<MovementSample> lab)
    {
        var cases = new List<MovementCase>();
        if (donor.Count != lab.Count) throw new ArgumentException("A replay's donor and lab traces share their ticks.");
        var d = Find(donor);
        var l = Find(lab);
        // A tick is blocked if a wall stopped the lab's body within the speed window before it.
        var blocked = new bool[lab.Count];
        for (int i = 0, b = -1; i < lab.Count; i++)
        {
            if (lab[i].Blocked) b = i;
            blocked[i] = b >= 0 && lab[i].Time - lab[b].Time <= Window + .1;
        }
        bool BlockedIn(int from, int to) => Enumerable.Range(Math.Max(0, from), Math.Max(0, to - from + 1)).Any(i => i < blocked.Length && blocked[i]);
        // A scenario's setpos moved the donor between two samples: faster than 2000 units/s across, 450 up or 800 down (a
        // sprint covers 270, a jump takes off at 270 and a 400-unit fall lands at 470; a setpos the donor's origin
        // smooths over a few ticks still rises at 600 or more).
        float Dt(int i) => MathF.Max(.015f, (float)(donor[i].Time - donor[i - 1].Time));
        bool TeleportIn(int from, int to) => Enumerable.Range(Math.Max(1, from), Math.Max(0, Math.Min(donor.Count - 1, to) - Math.Max(1, from) + 1))
            .Any(i => new Vector2(donor[i].X - donor[i - 1].X, donor[i].Y - donor[i - 1].Y).Length() / Dt(i) > 2000 || (donor[i].Z - donor[i - 1].Z) / Dt(i) is > 450 or < -800);
        // The donor's body stopped dead: from over 40 units/s to under a quarter of that in one 0.03 s span (its origin
        // moves every other tick), an obstacle or an attack's root rather than the ground's friction, which takes off
        // about 13 units/s a span.
        float SpanSpeed(int i) => new Vector2(donor[i].X - donor[i - 2].X, donor[i].Y - donor[i - 2].Y).Length() / MathF.Max(.03f, (float)(donor[i].Time - donor[i - 2].Time));
        bool StoppedDead(int from, int to)
        {
            for (int i = Math.Max(4, from); i <= Math.Min(donor.Count - 1, to); i++)
                if (SpanSpeed(i - 2) > 40 && SpanSpeed(i) < SpanSpeed(i - 2) / 4) return true;
            return false;
        }
        // The lab's body's speed before the command at i: its samples are taken after each step, so the last step before
        // it ends at i - 1 (R10159-3).
        float LabSpeedBefore(int i) => i < 2 ? 0 : new Vector2(lab[i - 1].X - lab[i - 2].X, lab[i - 1].Y - lab[i - 2].Y).Length() / MathF.Max(.015f, (float)(lab[i - 1].Time - lab[i - 2].Time));
        // Where the movement leading into the run at i began: a diagonal's second key, or sprint, goes down a tick or two
        // after the first, so the body is already moving on the run's own first tick.
        int Onset(int i)
        {
            int onset = i;
            while (onset > 0 && donor[onset - 1].Command is not null && donor[i].Time - donor[onset - 1].Time <= .1) onset--;
            return onset;
        }
        int Within(int from, double seconds) { int i = from; while (i + 1 < donor.Count && donor[i + 1].Time - donor[from].Time <= seconds) i++; return i; }
        var labSteady = l.Steady.ToDictionary(x => x.Tick, x => x.Speed);
        foreach (var run in d.Steady.GroupBy(x => (x.Run, x.Command)))
        {
            var ticks = run.ToList();
            if (ticks.Count < SteadyTicks) continue;
            double time = donor[run.Key.Run].Time;
            var pairs = ticks.Where(x => !blocked[x.Tick] && labSteady.ContainsKey(x.Tick)).Select(x => (Donor: x.Speed, Lab: labSteady[x.Tick])).ToList();
            float donorSpeed = MovementMeasure.Median(ticks.Select(x => x.Speed).ToList());
            string label = run.Key.Command.ToString();
            if (pairs.Count < SteadyTicks)
            {
                int stopped = ticks.Count(x => blocked[x.Tick]);
                cases.Add(new(recording, "speed", label, time, donorSpeed, pairs.Count > 0 ? MovementMeasure.Median(pairs.Select(p => p.Lab).ToList()) : null, .1f, true,
                    stopped > 0 ? $"the lab's body met its arena's wall on {stopped} of {ticks.Count} steady ticks" : null));
                continue;
            }
            float dMed = MovementMeasure.Median(pairs.Select(p => p.Donor).ToList()), lMed = MovementMeasure.Median(pairs.Select(p => p.Lab).ToList());
            bool sprint = run.Key.Command.Modifier == "sprint";
            int labSprinted = ticks.Count(x => lab[x.Tick].Sprinting);
            cases.Add(new(recording, "speed", label, time, dMed, lMed, .1f, true,
                dMed < lMed / 2 ? FormattableString.Invariant($"the donor's body was held (an obstacle or its level: {dMed:0} units/s against the lab's free {lMed:0})")
                : sprint && labSprinted < ticks.Count / 2 ? $"the lab's stamina, its own since the replay began, didn't admit the sprint ({labSprinted} of {ticks.Count} ticks)"
                : sprint && dMed < 200 ? FormattableString.Invariant($"the donor's sprint wasn't admitted ({dMed:0} units/s, a run; its stamina isn't on the Wire before v15)") : null));
        }
        foreach (var (run, c, seconds) in d.Starts)
        {
            double time = donor[run].Time;
            var labStart = l.Starts.FirstOrDefault(x => x.Run == run);
            bool found = l.Starts.Any(x => x.Run == run);
            cases.Add(new(recording, "start", c.ToString(), time, seconds, found ? labStart.Seconds : null, .15f, false,
                TeleportIn(run - 1, Within(run, seconds + .2)) ? "a scenario's setpos moved the donor in it"
                : BlockedIn(run, Within(run, seconds + .2)) ? "the lab's body met its arena's wall as it started"
                : LabSpeedBefore(Onset(run)) > 10 ? FormattableString.Invariant($"the lab's body wasn't at rest as it began ({LabSpeedBefore(Onset(run)):0} units/s)") : null));
        }
        foreach (var (run, seconds, slide, from) in d.Stops.Where(x => x.From is >= 160 and <= 200))
        {
            double time = donor[run].Time;
            bool found = l.Stops.Any(x => x.Run == run);
            var labStop = l.Stops.FirstOrDefault(x => x.Run == run);
            string? excluded = TeleportIn(run - 1, Within(run, seconds + .1)) ? "a scenario's setpos moved the donor before it stopped"
                : BlockedIn(run - 15, run + 60) ? "the lab's body met its arena's wall as it stopped"
                : StoppedDead(run, Within(run, seconds + .1)) ? "the donor's body stopped dead (an obstacle or an attack's root)" : null;
            cases.Add(new(recording, "stop", "from a run", time, seconds, found ? labStop.Seconds : null, .15f, false, excluded));
            cases.Add(new(recording, "slide", "from a run", time, slide, found ? labStop.Slide : null, .3f, true, excluded));
        }
        foreach (var (press, rise, air, _, held) in d.Jumps)
        {
            double time = donor[press].Time;
            bool found = l.Jumps.Any(x => x.Press == press);
            var labJump = l.Jumps.FirstOrDefault(x => x.Press == press);
            int before = press;
            while (before > 0 && donor[press].Time - donor[before].Time < Window) before--;
            string? excluded = TeleportIn(press, Within(press, air + .1)) ? "a scenario's setpos moved the donor in the jump"
                : MathF.Abs(donor[press].Z - donor[before].Z) > 4 ? FormattableString.Invariant($"the donor jumped from sloping ground ({donor[press].Z - donor[before].Z:0} units in the {Window} s before)")
                : BlockedIn(press - 10, Within(press, air + .1)) ? "the lab's body met its arena's wall in the jump"
                : found ? null
                : press > 0 && !l.Level[press - 1] ? "the lab's body wasn't on level ground as jump went down" : null;
            cases.Add(new(recording, "rise", held, time, rise, found ? labJump.Rise : null, .1f, true, excluded));
            cases.Add(new(recording, "air", held, time, air, found ? labJump.Air : null, .1f, false, excluded));
        }
        // A jump the lab made where the donor's body didn't leave the ground (rose under 10 units in the next second).
        foreach (var (press, rise, air, _, held) in l.Jumps.Where(x => !d.Jumps.Any(dj => dj.Press == x.Press)))
        {
            int end = Within(press, 1);
            float ground = press > 0 ? donor[press - 1].Z : donor[press].Z;
            bool donorRose = Enumerable.Range(press, end - press + 1).Any(i => donor[i].Z > ground + 10);
            if (donorRose) continue;
            cases.Add(new(recording, "rise", held + ", the donor's body didn't leave the ground", donor[press].Time, 0, rise, .1f, true,
                TeleportIn(press, end) ? "a scenario's setpos moved the donor" : null));
        }
        return cases;
    }

    static void Add(Dictionary<MoveCommand, List<float>> into, MoveCommand c, float value)
    {
        if (!into.TryGetValue(c, out var list)) into[c] = list = [];
        list.Add(value);
    }
}

/// <summary>
/// One case of the donor's movement against the lab's on the same ticks (R10159-2): a steady speed (units/s), a
/// start or stop's time (s), a stop's slide (units), a jump's rise (units) or time in the air (s); null for the lab
/// when it showed none. <see cref="Excluded"/> gives the evidence a case isn't compared on.
/// </summary>
internal sealed record MovementCase(string Recording, string Kind, string Label, double Time, float Donor, float? Lab, float Tolerance, bool Relative, string? Excluded)
{
    internal bool Compared => Excluded is null;
    internal bool Agrees => Excluded is null && Lab is float lab && MathF.Abs(lab - Donor) <= (Relative ? Tolerance * Donor : Tolerance);

    public override string ToString() => FormattableString.Invariant(
        $"{Recording} {Kind} {Label} at {Time:0.0} s: donor {Donor:0.##}, lab {(Lab is float l ? l.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "none")}{(Excluded is null ? (Agrees ? "" : " OFF") : " (not compared: " + Excluded + ")")}");
}
