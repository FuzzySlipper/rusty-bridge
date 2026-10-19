using System.Numerics;
using System.Text;
using DarkMessiah.Lab;
using DarkMessiah.Lab.Calibration;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Replay;
using DarkMessiah.Lab.Sword;
using Rusty.Engine;
using Rusty.Engine.Testing;

// The combat lab's smoke: the sword's moves need nothing; the fight runs the real product on the pinned Engine,
// headless, on dm-import's output (local/import, never tracked); calibration reads local/recordings. Exit 0 when
// every check passes.
int failures = 0;
void Check(bool condition, string message)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {message}");
    if (!condition) failures++;
}

// The sword's moves on authored clips: a tap is a fast attack, a hold a power one, block parries, F kicks.
var phases = new SwordPhases(MovePhases.Donor, MovePhases.Donor, MovePhases.Donor);
var tuning = new CombatTuning(80, .25f, .5f, [.36f, .42f], 1.4f, 1.5f, phases, new OrcTuning(35, [0, 0, 0], 1.6f, 1.6f, 1, .5f, 3));
MoveClip M(string name, float seconds, bool loop = false, float open = float.NaN, float close = float.NaN) => new(name, seconds, loop, open, close);
var clips = new SwordClips(M("idle", 2, true), M("in", .5f), M("hold", 1, true), M("fast", .3f, open: .33f, close: .78f), M("power", 1, open: .125f, close: .22f),
    M("parry-in", .15f), M("parry-hold", 1, true), M("parry-out", .2f), M("kick", .8f, open: .36f, close: .42f), M("bounce", .4f), M("broken", .8f));
const float Tick = 1f / 60;
var sword = new PlayerSword(clips, tuning);
for (int i = 0; i < 6; i++) sword.Step(Tick, true, false, false);
Check(sword.Move == SwordMove.Charge && sword.Clip.Clip == "in", "pressing attack winds up");
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Fast && sword.SwingStarted, "releasing within the power hold is a fast attack");
var traced = new List<float>();
for (int i = 0; i < 40 && sword.Move == SwordMove.Fast; i++) { sword.Step(Tick, false, false, false); if (sword.Traced) traced.Add(sword.Cycle); }
Check(traced.Count > 0 && traced.Min() >= .33f && traced.Max() <= .78f, FormattableString.Invariant($"the blade is traced only inside the fast attack's window: {traced.Count} steps"));
Check(sword.Move == SwordMove.Ready, "the fast attack ends ready");
for (int i = 0; i < 30; i++) sword.Step(Tick, true, false, false);
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Power, "releasing after the power hold is a power attack");
for (int i = 0; i < 80; i++) sword.Step(Tick, false, false, false);
for (int i = 0; i < 12; i++) sword.Step(Tick, false, true, false);
Check(sword.Parrying && sword.Clip.Clip == "parry-hold", "holding block parries, then holds the guard");
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.ParryOut, "releasing block lowers the guard");
for (int i = 0; i < 20; i++) sword.Step(Tick, false, false, false);
sword.Step(Tick, false, false, true);
Check(sword.Move == SwordMove.Kick && sword.SwingStarted, "F kicks");

// Phase timing (review R10121-1): a fast attack's windup, active and recovery each scale its time; donor timing is ×1.
float[] FastTiming(MovePhases p)
{
    var s = new PlayerSword(clips with { Fast = clips.Fast with { Phases = p } }, tuning);
    for (int i = 0; i < 3; i++) s.Step(Tick, true, false, false);
    s.Step(Tick, false, false, false);
    float t = 0, open = float.NaN, close = float.NaN;
    while (s.Move == SwordMove.Fast && t < 5) { s.Step(Tick, false, false, false); t += Tick; if (s.Traced && float.IsNaN(open)) open = t; if (s.Traced) close = t; }
    return [open, close, t];
}
var donorFast = FastTiming(MovePhases.Donor);
var slowWindup = FastTiming(new MovePhases(2, 1, 1));
var longRecovery = FastTiming(new MovePhases(1, 1, 3));
Check(MathF.Abs(donorFast[2] - .3f) < 2 * Tick, FormattableString.Invariant($"at ×1 the fast attack plays as the donor's clip: {donorFast[2]:0.00} s"));
Check(MathF.Abs(slowWindup[0] - 2 * donorFast[0]) < 2 * Tick && MathF.Abs((slowWindup[1] - slowWindup[0]) - (donorFast[1] - donorFast[0])) < 2 * Tick,
    FormattableString.Invariant($"windup ×2 opens the window later ({donorFast[0]:0.000} → {slowWindup[0]:0.000} s), the active part unchanged"));
Check(MathF.Abs(longRecovery[2] - longRecovery[1] - 3 * (donorFast[2] - donorFast[1])) < 3 * Tick && MathF.Abs(longRecovery[0] - donorFast[0]) < Tick,
    FormattableString.Invariant($"recovery ×3 lengthens the end ({donorFast[2]:0.00} → {longRecovery[2]:0.00} s), the windup unchanged"));

string? repo = null;
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "lab"))) { repo = dir.FullName; break; }
if (repo is null || !Directory.Exists(Path.Combine(repo, "local", "import")))
{
    Console.WriteLine("skip the fight: no local/import (dm-import level; docs/lab.md)");
    return failures == 0 ? 0 : 1;
}

string labContent = Path.Combine(repo, "lab", "content");
var content = Directory.GetFiles(labContent).ToDictionary(f => Path.GetFileName(f), f => (ReadOnlyMemory<byte>)File.ReadAllBytes(f));
using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions { Content = content });
host.Call(engine =>
{
    var files = content.Select(kv => new ProductContentFile(Encoding.UTF8.GetBytes(kv.Key), kv.Value)).ToArray();
    using var product = new LabProduct(new ProductCreateContext(engine, new ProductContent(files, engine.Content),
        new ProductInputConfiguration(default, default, default, default, InputCursorMode.PointerLock), default!));
    product.Start();
    ulong step = 0;
    void Advance(int updates)
    {
        for (int i = 0; i < updates; i++)
        {
            product.Update(new ProductUpdate(new ProductUpdateFacts(ProductLifecycleState.Running, 1, 1, 0, step, 60, 1, 0, Tick), default));
            step++;
        }
    }
    var combat = product.Combat;
    var orc = combat.Orc;

    // Input across updates (review R10121-3): a tap whose press and release both arrive on updates that admit no
    // step is still one fast attack; a hold is still a power attack.
    void Feed(uint steps, params ProductInputEvent[] input)
    {
        product.Update(new ProductUpdate(new ProductUpdateFacts(ProductLifecycleState.Running, 1, 1, 0, step, 60, steps, 0, Tick), input));
        step += steps;
    }
    static ProductInputEvent Primary(InputEdge edge) => default(ProductInputEvent) with { Kind = InputEventKind.PointerButton, PointerButton = PointerButton.Primary, Edge = edge };
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 1000 } });
    combat.Orc.PlaceAt(new System.Numerics.Vector3(0, -50, 0));
    Feed(30);
    int swingsBefore = combat.Player.Swings;
    Feed(0, Primary(InputEdge.Pressed));
    Feed(0, Primary(InputEdge.Released));
    var moves = new List<SwordMove>();
    for (int i = 0; i < 40; i++) { Feed(1); moves.Add(combat.Sword.Move); }
    Check(combat.Player.Swings == swingsBefore + 1 && moves.Contains(SwordMove.Fast) && !moves.Contains(SwordMove.Power),
        $"a tap between steps is one fast attack: {combat.Player.Swings - swingsBefore} swing, {string.Join(",", moves.Distinct())}");
    Feed(0, Primary(InputEdge.Pressed), Primary(InputEdge.Released));
    moves.Clear();
    for (int i = 0; i < 40; i++) { Feed(1); moves.Add(combat.Sword.Move); }
    Check(combat.Player.Swings == swingsBefore + 2 && moves.Contains(SwordMove.Fast), "a press and release in one batch is one fast attack too");
    Feed(1, Primary(InputEdge.Pressed));
    for (int i = 0; i < 30; i++) Feed(1);
    Feed(1, Primary(InputEdge.Released));
    moves.Clear();
    for (int i = 0; i < 5; i++) { Feed(1); moves.Add(combat.Sword.Move); }
    Check(moves.Contains(SwordMove.Power), "a hold past the power threshold is still a power attack");
    for (int i = 0; i < 90; i++) Feed(1);
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 1 } });
    combat.Orc.Spawn();
    Console.WriteLine($"donor content: {product.Donor.Source}");
    Check(combat.FirstPerson.Range > 2.4f && combat.FirstPerson.Range < 2.6f, FormattableString.Invariant($"the short sword's range is its rangeattack, 100 units: {combat.FirstPerson.Range:0.00} m"));
    Check(MathF.Abs(combat.FirstPerson.AttackSpeed - 1.1f) < 1e-3f && MathF.Abs(orc.AttackSpeed - .8f) < 1e-3f,
        "attack clips play at the weapons' speed_attack: the short sword 1.10, the cleaver 0.80");
    var fast = combat.Sword.Clip;
    var real = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning);
    Check(MathF.Abs(real.Fast.Open - .333f) < .01f && MathF.Abs(real.Fast.Close - .778f) < .01f && MathF.Abs(real.Power.Open - .125f) < .01f,
        FormattableString.Invariant($"hit windows come from Arkane's events: fast {real.Fast.Open:0.00}–{real.Fast.Close:0.00}, power {real.Power.Open:0.00}–{real.Power.Close:0.00}"));

    // The orc walks in and fights the standing player.
    float start = Vector3.Distance(orc.Feet with { Y = 0 }, product.Player.Feet with { Y = 0 });
    Advance(720);
    float near = Vector3.Distance(orc.Feet with { Y = 0 }, product.Player.Feet with { Y = 0 });
    Check(near < start && near <= combat.Tuning.Orc.EngageDistance * 1.3f, FormattableString.Invariant($"the orc walks in from {start:0.0} m to {near:0.0} m"));
    Check(combat.OrcStats.Blows > 0 && combat.OrcStats.Reached > 0, $"the orc's blows reach the player: {combat.OrcStats.Blows} blows, {combat.OrcStats.Reached} reached");
    Check(combat.PlayerHealth < combat.Tuning.PlayerHealth || combat.OrcStats.Deaths > 0, FormattableString.Invariant($"they take the player's health: {combat.PlayerHealth:0.#} left"));

    // A still orc in front: no attacks, no parries, then the player's moves against it.
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 1000, ParryChance = 0 } });
    void Face()
    {
        orc.Spawn();
        orc.PlaceAt(product.Player.Feet + Vector3.Normalize(product.Player.Forward with { Y = 0 }) * 1.3f);
        Advance(10);
    }
    (int Hits, float Health) Swing(int holdSteps, bool block = false)
    {
        int hits = combat.Player.Hits;
        for (int i = 0; i < holdSteps; i++) combat.Step(Tick, step++, true, block, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        for (int i = 0; i < 90 && combat.Sword.Move != SwordMove.Ready; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        return (combat.Player.Hits - hits, orc.Health);
    }
    combat.God = true;
    int Struck() => combat.Player.Hits + combat.Player.Parried + combat.Player.Blocked;
    Face();
    int struck = Struck();
    Swing(3);
    Console.WriteLine($"  fast attack: {combat.LastBlow}");
    Check(Struck() == struck + 1, $"a fast attack on an orc in front strikes it once (body, cleaver or shield): {combat.LastBlow?.Outcome}");
    // A power attack on the body asks (WDmg + 0 strength) × the part's HitPow × the orc's hit-group modifier, read
    // here straight from the .qct files: the lab's damage is Arkane's.
    var data = product.Donor.Combat;
    float powerPow = data.Character(DarkMessiah.Combat.CombatData.PlayerAttacksModel)!.Attack("layer_bigslash_sword_noshield_out")!.Events[0].HitPow;
    float wdmg = data.Weapon(FirstPerson.SwordModel)!.Damage;
    SwingTrace? body = null;
    for (int attempt = 0; attempt < 6 && body is null; attempt++)
    {
        Face();
        Swing(40);
        if (combat.LastBlow is { Group: int } b && b.Move.Contains("bigslash", StringComparison.Ordinal)) body = b;
        else product.Player.LookBy(8, 0);
    }
    Console.WriteLine($"  power attack: {body}");
    float expected = body?.Group is int g ? wdmg * powerPow * data.Character(Orc.Model)!.HitGroup(g) : float.NaN;
    Check(body is not null && MathF.Abs(body.Damage - expected) < 1e-3f,
        FormattableString.Invariant($"a power attack on the body asks WDmg × HitPow × the hit group's modifier: {body?.Damage:0.###} = {wdmg} × {powerPow} × group {body?.Group}"));
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { ParryChance = 1 } });
    Face();
    int parried = combat.Player.Parried + combat.Player.Blocked;
    Swing(3);
    Console.WriteLine($"  against a parrying orc: {combat.LastBlow}");
    Check(combat.Player.Parried + combat.Player.Blocked == parried + 1, "an orc that parries turns the blade: the verdict reads its parry state");
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { ParryChance = 0 } });
    Face();
    float before = orc.Health;
    combat.Step(Tick, step++, false, false, true, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    for (int i = 0; i < 60; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(MathF.Abs(before - orc.Health - data.Settings.KickDamage) < 1e-3f && orc.State == OrcState.Pushed,
        FormattableString.Invariant($"a kick asks the settings' kick_damage and pushes the orc: {before:0.##} → {orc.Health:0.##}, {orc.State}"));

    // The player's parry against the orc's attacks: parried, or broken by a part that breaks parries.
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 0 } });
    Face();
    var (p0, b0) = (combat.OrcStats.Parried, combat.OrcStats.Broken);
    for (int i = 0; i < 600; i++) combat.Step(Tick, step++, false, true, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(combat.OrcStats.Parried + combat.OrcStats.Broken > p0 + b0,
        $"the player's guard meets the orc's blows: {combat.OrcStats.Parried - p0} parried, {combat.OrcStats.Broken - b0} broken");
    Advance(30);
    Check(true, $"the fight runs: {combat.Message}");

    // Calibration: the lab's windows and clip rates against recorded player blows (Wire v9 and later).
    string recordings = Path.Combine(repo, "local", "recordings");
    var found = Directory.Exists(recordings) ? Directory.GetFiles(recordings, "*.rbrec") : [];
    if (found.Length == 0) { Console.WriteLine("skip calibration: no local/recordings"); return; }
    var calibration = SwordCalibration.Measure(found, real);
    foreach (var move in calibration) Console.WriteLine($"  {move}");
    foreach (var move in calibration.Where(m => m.Blows.Count >= 10))
        Check(move.Inside >= move.Blows.Count * 8 / 10, $"{move.Clip}: recorded blows land inside the lab's window ({move.Inside} of {move.Blows.Count})");
    foreach (var move in calibration.Where(m => float.IsFinite(m.DonorCyclesPerSecond)))
        Check(MathF.Abs(move.LabCyclesPerSecond / move.DonorCyclesPerSecond - 1) < .15f,
            FormattableString.Invariant($"{move.Clip}: the lab plays it at the donor's rate ({move.LabCyclesPerSecond:0.00} against {move.DonorCyclesPerSecond:0.00} cycles/s)"));

    // Damage (review R10121-2): every recorded player blow on an orc, asked again through the lab's own path.
    var damage = DamageCalibration.Measure(found, combat, real);
    foreach (var byMove in damage.GroupBy(d => d.Move))
        Console.WriteLine($"  damage {byMove.Key}: {byMove.Count(d => d.Agrees)} of {byMove.Count()} recorded blows ask what the lab asks");
    foreach (var d in damage.Where(d => !d.Agrees)) Console.WriteLine($"    {d}");
    Check(damage.Count >= 20 && damage.Count(d => d.Agrees) >= damage.Count * 8 / 10 && damage.All(d => d.Agrees || d.Reason != "unexplained"),
        $"the lab's damage matches recorded blows ({damage.Count(d => d.Agrees)} of {damage.Count}), and every difference has a known reason");
    // Phase timing reaches the fight through its owner: a retuned windup lengthens the fast attack's play.
    var slower = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed,
        combat.Tuning with { Phases = combat.Tuning.Phases with { Fast = new MovePhases(2, 1, 1) } });
    Check(slower.Fast.Duration > real.Fast.Duration && MathF.Abs(slower.Fast.Duration - real.Fast.Duration - real.Fast.Open * real.Fast.Seconds) < 1e-4f,
        FormattableString.Invariant($"a retuned fast windup ×2 plays {real.Fast.Duration:0.000} → {slower.Fast.Duration:0.000} s"));

    // Replay: recordings of Wire v12 and later carry the player's input; it drives the lab's sword against the
    // donor orc the player faced, and the report compares both sides' moves and blows.
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 1000 } });
    foreach (var path in found.Where(p => RecordingVersion(p) >= 12))
    {
        var report = CombatReplay.Run(combat, path, product);
        // From Wire v14 the commands drive it: kicks are requests, and movement, jump and use move the lab's own body.
        if (report.Kicks is { } kicks && report.Body is { } moved)
        {
            Check(kicks.Requests >= kicks.DonorBegan && kicks.Requests >= kicks.LabBegan && kicks.Requests > 0,
                $"{report.Name}: kicks are the recorded requests ({kicks})");
            Check(moved.MoveTicks == 0 || moved.LabSpeed > .5f * moved.DonorSpeed && moved.LabSpeed < 2f * moved.DonorSpeed,
                $"{report.Name}: the recorded movement moves the lab's body ({moved})");
            Check(moved.JumpPresses == 0 || moved.LabJumps > 0, $"{report.Name}: a recorded jump makes the lab's body jump");
        }
        // Two swings while the player's body animation runs on (review R10122-3): every blow is accounted for.
        if (Path.GetFileName(path) == "l01_a-v12-arena-bot.rbrec")
        {
            var timeline = InputTimeline.Read(path, [real.Fast.Clip, real.Power.Clip, real.Kick.Clip], real.Kick.Clip);
            bool counted = report.PlayerBlows.Matches.Any(m => m.Donor?.Tick == 16618)
                || report.NotCompared.Any(n => n.Contains("player blows on npc_orc_sword", StringComparison.Ordinal));
            Check(CombatReplay.SwingAt(timeline.Swings, 16396) != CombatReplay.SwingAt(timeline.Swings, 16618) && counted,
                "the parried fast attack at tick 16396 and the power attack at 16618 are separate swings, and the later one is counted");
        }
        foreach (var line in report.Lines()) Console.WriteLine($"  {line}");
        Check(report.Ticks > 0 && report.Moves.Values.Sum(m => m.Lab) > 0, $"{report.Name}: its input drives the lab's sword");
        int donorMoves = report.Moves.Values.Sum(m => m.Donor), together = report.Moves.Values.Sum(m => m.Together);
        Check(together >= donorMoves * 8 / 10, $"{report.Name}: the lab begins the donor's moves with them ({together} of {donorMoves})");
        Check(!combat.Orc.Following && combat.Log is null, $"{report.Name}: the lab's own fight comes back after the replay");
    }
});
return failures == 0 ? 0 : 1;

// A recording's format version, from its first frame.
static uint RecordingVersion(string path)
{
    using var stream = File.OpenRead(path);
    return RustyBridge.Wire.RecordingReader.Frames(stream).FirstOrDefault()?.Version ?? 0;
}
