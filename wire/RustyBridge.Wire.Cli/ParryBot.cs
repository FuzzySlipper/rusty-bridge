using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using RustyBridge.Wire;

/// <summary>
/// Plays the player's defense from the Wire: when an enemy within reach starts
/// an attack sequence, hold block (right mouse) long enough to cover the
/// impact; otherwise turn toward the nearest enemy (relative mouse motion,
/// calibrated against the player's yaw on the Wire), walk up to it (W) and
/// swing (left mouse). Input goes
/// through the den-win11 playtest agent's desktop lease, so the game must be
/// in front. Its purpose is to catch clean parries on the Wire: the player's
/// (defense result 17) and an NPC's (1).
/// </summary>
static class ParryBot
{
    // Enemies whose melee the player can parry, and the sequence names that
    // start a blow (the stepper's attack classification).
    static readonly Regex Enemy = new("^npc_(orc|goblin|ghoul)", RegexOptions.IgnoreCase);
    static readonly Regex Attack = new("attack|strike|kick|bash|swing|slash|stab|uppercut|grab", RegexOptions.IgnoreCase);
    static readonly Regex NotAttack = new("^layer_|nostrike", RegexOptions.IgnoreCase);

    const float BlockReach = 220;    // units: start blocking for attacks from this close
    const float SwingReach = 110;    // units: swing at enemies this close
    const int BlockMs = 700;         // hold after the delay; NPC state updates every 120 ms
    // Block delays after a wind-up is seen, cycled so one run tries several
    // timings; each block's delay is printed so outcomes can be matched to it.
    static readonly int[] BlockDelays = { 0, 200, 400, 600 };
    const int SwingMs = 120;
    const double SwingEvery = 1.4;   // seconds between swings
    const float Facing = 20;         // degrees: close enough to face an enemy
    const int VkW = 0x57;

    public static int Run(WireReader reader, Func<string, IReadOnlyList<string>>? sequences, string agent, double seconds)
    {
        if (sequences is null) { Console.Error.WriteLine("parrybot needs sequence names (game VPKs)"); return 1; }
        if (double.IsInfinity(seconds)) seconds = 120;
        using var http = new HttpClient { BaseAddress = new Uri(agent), Timeout = TimeSpan.FromSeconds(10) };
        var lease = http.PostAsJsonAsync("/v1/lease", new { holder = "rbwire-parrybot", instance = "", ttl_ms = (int)Math.Min(600_000, seconds * 1000 + 15_000) })
            .Result.Content.ReadFromJsonAsync<Dictionary<string, object>>().Result;
        if (lease is null || !lease.TryGetValue("id", out var idObj)) { Console.Error.WriteLine($"no lease: {string.Join(", ", lease ?? new())}"); return 1; }
        string id = idObj.ToString()!;
        Console.WriteLine($"lease {id}; playing for {seconds:F0} s");

        Task busy = Task.CompletedTask;
        void Send(object step) => busy = http.PostAsJsonAsync($"/v1/lease/{id}/input", new { steps = new[] { step } });
        void Act(int button, int ms) => Send(new { kind = "click", button, ms });

        var snap = new WireSnapshot();
        float? PlayerYaw()
        {
            Thread.Sleep(250);
            if (!reader.TryReadLatest(snap)) return null;
            foreach (ref readonly var e in snap.Span)
                if (reader.ClassName(snap, e.ClassId) == "player") return e.Yaw;
            return null;
        }
        // Degrees of yaw per pixel of relative mouse motion (negative: moving right turns clockwise).
        float before = PlayerYaw() ?? 0;
        Send(new { kind = "move", dx = 200, dy = 0, ms = 100 }); busy.Wait();
        float after = PlayerYaw() ?? 0;
        float perPixel = Wrap(after - before) / 200f;
        Send(new { kind = "move", dx = -200, dy = 0, ms = 100 }); busy.Wait();
        Console.WriteLine($"mouse: {perPixel:F3} degrees per pixel (yaw {before:F1} -> {after:F1})");
        if (MathF.Abs(perPixel) < 0.001f) { Console.Error.WriteLine("the view did not turn; is the game in front?"); perPixel = -0.022f; }

        var last = new Dictionary<ushort, (short seq, float cycle)>();
        var clock = Stopwatch.StartNew();
        double lastSwing = 0;
        int blocks = 0, swings = 0;
        var outcomes = new Dictionary<DefenseResult, int>();
        ulong lastFrame = 0;
        int t0 = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (!reader.TryReadLatest(snap) || snap.Frame == lastFrame) { Thread.Sleep(3); continue; }
            // Defense outcomes since the previous frame we saw (the latest frame only; good enough to report).
            foreach (var d in snap.DefenseSpan)
                if (d.Result is not (DefenseResult.Hit or DefenseResult.None))
                {
                    outcomes[d.Result] = outcomes.GetValueOrDefault(d.Result) + 1;
                    Console.WriteLine($"  tick {d.Tick}: {d.Result} victim #{d.Victim} attacker #{d.Attacker}");
                }
            lastFrame = snap.Frame;
            if (t0 == 0) t0 = snap.Tick;

            WireEntity? player = null;
            foreach (ref readonly var e in snap.Span)
                if (reader.ClassName(snap, e.ClassId) == "player") { player = e; break; }
            if (player is not { } p) continue;

            string? threat = null;
            float nearest = float.MaxValue, nearestBearing = 0;
            foreach (ref readonly var e in snap.Span)
            {
                string cls = reader.ClassName(snap, e.ClassId);
                if (!Enemy.IsMatch(cls) || e.LifeState != 0 || e.Sequence < 0) continue;
                float dist = MathF.Sqrt((e.OriginX - p.OriginX) * (e.OriginX - p.OriginX) + (e.OriginY - p.OriginY) * (e.OriginY - p.OriginY));
                if (dist < nearest)
                {
                    nearest = dist;
                    nearestBearing = MathF.Atan2(e.OriginY - p.OriginY, e.OriginX - p.OriginX) * 180 / MathF.PI;
                }
                bool started = !last.TryGetValue(e.Index, out var was) || was.seq != e.Sequence || e.Cycle < was.cycle - 0.5f;
                last[e.Index] = (e.Sequence, e.Cycle);
                if (!started || dist > BlockReach) continue;
                var names = sequences(reader.ModelPath(snap, e.ModelIndex));
                string name = e.Sequence < names.Count ? names[e.Sequence] : "";
                if (Attack.IsMatch(name) && !NotAttack.IsMatch(name)) threat = $"{cls} #{e.Index} {name} at {dist:F0} u";
            }

            double now = clock.Elapsed.TotalSeconds;
            if (threat is not null && busy.IsCompleted)
            {
                int delay = BlockDelays[blocks % BlockDelays.Length];
                busy = http.PostAsJsonAsync($"/v1/lease/{id}/input", new
                {
                    steps = delay > 0 ? new object[] { new { kind = "wait", ms = delay }, new { kind = "click", button = 3, ms = BlockMs } }
                                      : new object[] { new { kind = "click", button = 3, ms = BlockMs } }
                });
                blocks++;
                Console.WriteLine($"{(snap.Tick - t0) * reader.Header.TickInterval,7:F2}s tick {snap.Tick} block after {delay} ms: {threat}");
            }
            else if (busy.IsCompleted && nearest < 1500)
            {
                float error = Wrap(nearestBearing - p.Yaw);
                if (MathF.Abs(error) > Facing)
                    Send(new { kind = "move", dx = (int)Math.Clamp(error / perPixel, -800, 800), dy = 0, ms = 60 });
                else if (nearest > SwingReach * 0.8f)
                    Send(new { kind = "hold", keys = new[] { VkW }, ms = 200 });
                else if (now - lastSwing > SwingEvery)
                {
                    Act(1, SwingMs);
                    swings++;
                    lastSwing = now;
                }
            }
            Thread.Sleep(3);
        }
        busy.Wait();
        http.DeleteAsync($"/v1/lease/{id}").Wait();
        Console.WriteLine($"done: {blocks} blocks, {swings} swings; outcomes seen live " +
            (outcomes.Count == 0 ? "none" : string.Join(", ", outcomes.Select(kv => $"{kv.Key} {kv.Value}"))));
        return 0;
    }

    static float Wrap(float degrees) => ((degrees % 360) + 540) % 360 - 180;
}
