using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using RustyBridge.Wire;

/// <summary>
/// Plays the player's defense from the Wire: when an enemy within reach starts
/// an attack sequence, hold block (right mouse) long enough to cover the
/// impact; otherwise turn toward the nearest enemy, walk up to it (W) and
/// attack, cycling a fast swing (left click), a power attack (left held,
/// the `.qct`'s critical attacks) and a kick (F). It keeps one target until
/// it dies or another enemy is much nearer. Relative mouse motion
/// doesn't turn Dark Messiah's view, so the bot turns with keys that
/// rbbot.cfg binds to `+left`/`+right` (J and L, `cl_yawspeed 90`), held for
/// the angle over a turn rate it calibrates against the player's yaw on the
/// Wire. Input goes
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
    const int VkW = 0x57, VkF = 0x46;
    const int VkTurnLeft = 0x4A, VkTurnRight = 0x4C;   // J, L: rbbot.cfg binds them to +left, +right
    const int CalibrateMs = 1000;
    const float TurnGain = 0.6f;     // turn this share of the error per hold, then look again
    const int SettleMs = 300;        // after a turn, before the next look
    const int PowerMs = 1000;        // held long enough for layer_bigslash_*_in to release
    const float Switch = 0.6f;       // change target only to an enemy this much nearer

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
        // The view's yaw is the viewmodel's: the player entity's yaw is its body, which
        // turns in place in steps of about 90 degrees (Player_Turn_45_*) once the view
        // has moved far enough, and not at all for small turns.
        float? ViewYaw(WireSnapshot s)
        {
            float? body = null;
            foreach (ref readonly var e in s.Span)
            {
                string cls = reader.ClassName(s, e.ClassId);
                if (cls == "viewmodel") return e.Yaw;
                if (cls == "player") body = e.Yaw;
            }
            return body;
        }
        float? PlayerYaw()
        {
            Thread.Sleep(250);
            return reader.TryReadLatest(snap) ? ViewYaw(snap) : null;
        }
        // Degrees of yaw per second of holding a turn key (+left turns counter-clockwise: yaw grows).
        // The first input after the console closes can be lost, so try a few times.
        float perSecond = 0;
        for (int attempt = 0; attempt < 5 && MathF.Abs(perSecond) < 10; attempt++)
        {
            float before = PlayerYaw() ?? 0;
            Send(new { kind = "hold", keys = new[] { VkTurnLeft }, ms = CalibrateMs }); busy.Wait();
            float after = PlayerYaw() ?? 0;
            perSecond = Wrap(after - before) / (CalibrateMs / 1000f);
            Console.WriteLine($"turn keys: {perSecond:F0} degrees per second (yaw {before:F1} -> {after:F1})");
        }
        if (MathF.Abs(perSecond) < 10) { Console.Error.WriteLine("the view did not turn; is the game in front, and rbbot.cfg exec'd?"); perSecond = 70; }

        var last = new Dictionary<ushort, (short seq, float cycle)>();
        var clock = Stopwatch.StartNew();
        double lastSwing = 0;
        int blocks = 0, swings = 0, powers = 0, kicks = 0, turns = 0;
        ushort target = WireLayout.NoEntity;
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
            float nearest = float.MaxValue, nearestBearing = 0, held = float.MaxValue, heldBearing = 0;
            ushort nearestIndex = WireLayout.NoEntity;
            foreach (ref readonly var e in snap.Span)
            {
                string cls = reader.ClassName(snap, e.ClassId);
                if (!Enemy.IsMatch(cls) || e.LifeState != 0 || e.Sequence < 0) continue;
                float dist = MathF.Sqrt((e.OriginX - p.OriginX) * (e.OriginX - p.OriginX) + (e.OriginY - p.OriginY) * (e.OriginY - p.OriginY));
                float bearing = MathF.Atan2(e.OriginY - p.OriginY, e.OriginX - p.OriginX) * 180 / MathF.PI;
                if (dist < nearest) { nearest = dist; nearestBearing = bearing; nearestIndex = e.Index; }
                if (e.Index == target) { held = dist; heldBearing = bearing; }
                bool started = !last.TryGetValue(e.Index, out var was) || was.seq != e.Sequence || e.Cycle < was.cycle - 0.5f;
                last[e.Index] = (e.Sequence, e.Cycle);
                if (!started || dist > BlockReach) continue;
                var names = sequences(reader.ModelPath(snap, e.ModelIndex));
                string name = e.Sequence < names.Count ? names[e.Sequence] : "";
                if (Attack.IsMatch(name) && !NotAttack.IsMatch(name)) threat = $"{cls} #{e.Index} {name} at {dist:F0} u";
            }

            // Keep the target unless it's gone or another enemy is much nearer: two enemies
            // either side of the player would otherwise flip the bot between them.
            if (held == float.MaxValue || nearest < held * Switch) target = nearestIndex;
            else { nearest = held; nearestBearing = heldBearing; }
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
                float error = Wrap(nearestBearing - (ViewYaw(snap) ?? p.Yaw));
                if (MathF.Abs(error) > Facing)
                {
                    // Hold the turn key for the angle; positive error turns counter-clockwise.
                    int ms = (int)Math.Clamp(MathF.Abs(TurnGain * error / perSecond) * 1000, 40, 2000);
                    int key = (error > 0) == (perSecond > 0) ? VkTurnLeft : VkTurnRight;
                    // Walk while turning when the enemy is roughly ahead and still far.
                    int[] keys = MathF.Abs(error) < 60 && nearest > SwingReach * 2 ? [key, VkW] : [key];
                    busy = http.PostAsJsonAsync($"/v1/lease/{id}/input", new { steps = new object[] { new { kind = "hold", keys, ms }, new { kind = "wait", ms = SettleMs } } });
                    turns++;
                }
                else if (nearest > SwingReach * 0.8f)
                    Send(new { kind = "hold", keys = new[] { VkW }, ms = nearest > SwingReach * 3 ? 600 : 200 });
                else if (now - lastSwing > SwingEvery)
                {
                    switch ((swings + powers + kicks) % 3)
                    {
                        case 0: Act(1, SwingMs); swings++; break;
                        case 1: Act(1, PowerMs); powers++; break;
                        default: Send(new { kind = "hold", keys = new[] { VkF }, ms = SwingMs }); kicks++; break;
                    }
                    lastSwing = now;
                }
            }
            Thread.Sleep(3);
        }
        busy.Wait();
        http.DeleteAsync($"/v1/lease/{id}").Wait();
        Console.WriteLine($"done: {blocks} blocks, {swings} swings, {powers} power attacks, {kicks} kicks, {turns} turns; outcomes seen live " +
            (outcomes.Count == 0 ? "none" : string.Join(", ", outcomes.Select(kv => $"{kv.Key} {kv.Value}"))));
        return 0;
    }

    static float Wrap(float degrees) => ((degrees % 360) + 540) % 360 - 180;
}
