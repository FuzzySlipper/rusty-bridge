using System.Numerics;
using System.Text;
using DarkMessiah.Lab;
using DarkMessiah.Lab.Calibration;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
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
var tuning = new CombatTuning(80, .5f, [.36f, .42f], 1.4f, 1.5f, phases, new OrcTuning(35, [0, 0, 0], 1.6f, 1.6f, 1, .5f, 3));
MoveClip M(string name, float seconds, bool loop = false, float open = float.NaN, float close = float.NaN) => new(name, seconds, loop, open, close);
PowerClips Charge(string at, float wind = .5f) => new(M($"{at}in", wind), M($"{at}hold", 1, true), M($"{at}power", 1, open: .125f, close: .22f));
var clips = new SwordClips(M("idle", 2, true),
    new Dictionary<PowerDirection, PowerClips> { [PowerDirection.Neutral] = Charge(""), [PowerDirection.T] = Charge("T-", .7f), [PowerDirection.B] = Charge("B-"), [PowerDirection.L] = Charge("L-") },
    [M("fast", .3f, open: .33f, close: .78f), M("fast-b", .4f, open: .3f, close: .7f), M("fast-c", .4f, open: .3f, close: .7f),
     M("fast-d", .4f, open: .3f, close: .7f), M("fast-e", .37f, open: .3f, close: .7f)], [M("fast-end", .5f), null, null, null, M("fast-end", .5f)],
    M("parry-in", .15f), M("parry-hold", 1, true), M("parry-out", .2f), M("kick", .8f, open: .36f, close: .42f), M("broken", .8f));
const float Tick = 1f / 60;
var sword = new PlayerSword(clips) { FastDelay = 0 };
for (int i = 0; i < 6; i++) sword.Step(Tick, true, false, false);
Check(sword.Move == SwordMove.Charge && sword.Clip.Clip == "in", "pressing attack winds up");
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Fast && sword.SwingStarted, "releasing within the power hold is a fast attack");
var traced = new List<float>();
for (int i = 0; i < 40 && sword.Move == SwordMove.Fast; i++) { sword.Step(Tick, false, false, false); if (sword.Traced) traced.Add(sword.TraceCycle); }
Check(traced.Count > 0 && traced.Min() >= .33f && traced.Max() <= .78f, FormattableString.Invariant($"the blade is traced only inside the fast attack's window: {traced.Count} steps"));
Check(sword.Move == SwordMove.FastEnd && sword.Clip.Clip == "fast-end", "an A nothing chains onto recovers (fastattack_end)");
for (int i = 0; i < 40 && sword.Move != SwordMove.Ready; i++) sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Ready, "then the arm is ready");
for (int i = 0; i < 40; i++) sword.Step(Tick, true, false, false);
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Power, "releasing from the held wind-up is a power attack");
for (int i = 0; i < 80; i++) sword.Step(Tick, false, false, false);
for (int i = 0; i < 12; i++) sword.Step(Tick, false, true, false);
Check(sword.Parrying && sword.Clip.Clip == "parry-hold", "holding block parries, then holds the guard");
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.ParryOut, "releasing block lowers the guard");
for (int i = 0; i < 20; i++) sword.Step(Tick, false, false, false);
sword.Step(Tick, false, false, true);
Check(sword.Move == SwordMove.Kick && sword.SwingStarted, "F kicks");
for (int i = 0; i < 60; i++) sword.Step(Tick, false, false, false);

// The fast-attack chain (docs/lab.md, "Fast and power attacks"): a press while A plays chains B when A ends; a press
// during A's recovery starts A again at once; attack held through B charges a power attack.
SwordMove Tap(PlayerSword s)
{
    s.Step(Tick, true, false, false);
    s.Step(Tick, false, false, false);
    return s.Move;
}
sword = new PlayerSword(clips) { FastDelay = 0 };
Tap(sword);
int aSwing = sword.Swing;
for (int i = 0; i < 5; i++) sword.Step(Tick, false, false, false);
Tap(sword);
var chain = new List<string>();
for (int i = 0; i < 30 && sword.Swing == aSwing; i++) { sword.Step(Tick, false, false, false); chain.Add(sword.Clip.Clip); }
Check(sword.Move == SwordMove.Fast && sword.Clip.Clip == "fast-b" && sword.SwingStarted && chain.Count(c => c == "fast") >= 10,
    $"a press while A plays chains B when A ends ({chain.Count(c => c == "fast")} steps of A)");
for (int i = 0; i < 40 && sword.Move == SwordMove.Fast; i++) sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Ready, "B unchained ends ready, with no recovery clip");

// The chained slash (#10143): without it a press during B chains nothing; with it (ChainLength 5) presses run A to
// E, one deep, and E ends in the recovery.
List<string> Mash(PlayerSword s, int steps)
{
    var started = new List<string>();
    for (int i = 0; i < steps; i++)
    {
        // A tap every 12 steps (0.2 s), as the scenario's bursts.
        bool press = i % 12 < 4;
        s.Step(Tick, press, false, false);
        if (s.SwingStarted) started.Add(s.Clip.Clip);
    }
    return started;
}
var two = Mash(new PlayerSword(clips) { FastDelay = 0 }, 160);
Check(two.SequenceEqual(["fast", "fast-b", "fast", "fast-b", "fast", "fast-b"]) || two.Take(2).SequenceEqual(["fast", "fast-b"]) && !two.Contains("fast-c"),
    $"without the chained slash, mashing chains only A and B: {string.Join(" ", two)}");
var five = new PlayerSword(clips) { FastDelay = 0,  ChainLength = 5 };
var mashed = Mash(five, 120);
Check(mashed.Take(5).SequenceEqual(["fast", "fast-b", "fast-c", "fast-d", "fast-e"]), $"with it, mashing chains A to E: {string.Join(" ", mashed)}");
var afterE = new PlayerSword(clips) { FastDelay = 0,  ChainLength = 5 };
Mash(afterE, 4);
for (int i = 0; i < 200 && !(afterE.Clip.Clip == "fast-e"); i++) afterE.Step(Tick, i % 12 < 4, false, false);
for (int i = 0; i < 30 && afterE.Move == SwordMove.Fast; i++) afterE.Step(Tick, i % 12 < 4, false, false);
Check(afterE.Move == SwordMove.FastEnd, "a press during E chains nothing: E ends in the recovery");
// A kick takes over from an attack waiting in a jump (R10144-2): in the air a step, landed with attack held (the press
// waits out AirDelay), then kick pressed while attack stays held. The kick plays on to its end.
var landedKick = new PlayerSword(clips) { FastDelay = 0 };
landedKick.Step(Tick, false, false, false, airborne: true);
landedKick.Step(Tick, true, false, false);
landedKick.Step(Tick, true, false, true);
bool kicking = landedKick.Move == SwordMove.Kick;
var kickMoves = new List<SwordMove>();
for (int i = 0; i < 40; i++) { landedKick.Step(Tick, true, false, true); kickMoves.Add(landedKick.Move); }
Check(kicking && kickMoves.Take(30).All(m => m == SwordMove.Kick),
    $"a kick after landing with attack held isn't replaced by the waiting air attack: {string.Join(" ", kickMoves.Distinct())}");
// A retune mid-chain (an orc slider, combat.json reloaded) keeps the chain's place (R10143-2): a queued C to D stays D,
// and E's cap holds. The retuned set is a new one, its clips equal but not the same objects.
SwordClips Retuned() => clips with { Chain = clips.Chain.Select(c => c with { }).ToList() };
var retunedC = new PlayerSword(clips) { FastDelay = 0, ChainLength = 5 };
for (int i = 0; i < 400 && retunedC.Clip.Clip != "fast-c"; i++) retunedC.Step(Tick, i % 12 < 4, false, false);
retunedC.Step(Tick, true, false, false);
retunedC.Step(Tick, false, false, false);
retunedC.Retune(Retuned());
for (int i = 0; i < 60 && retunedC.Clip.Clip == "fast-c"; i++) retunedC.Step(Tick, false, false, false);
var retunedE = new PlayerSword(clips) { FastDelay = 0, ChainLength = 5 };
for (int i = 0; i < 400 && retunedE.Clip.Clip != "fast-e"; i++) retunedE.Step(Tick, i % 12 < 4, false, false);
retunedE.Retune(Retuned());
for (int i = 0; i < 40 && retunedE.Move == SwordMove.Fast; i++) retunedE.Step(Tick, i % 12 < 4, false, false);
Check(retunedC.Clip.Clip == "fast-d" && retunedE.Move == SwordMove.FastEnd,
    $"a retune mid-chain keeps its place: C's queued press plays {retunedC.Clip.Clip}, and E retuned still ends in {retunedE.Clip.Clip}");
sword = new PlayerSword(clips) { FastDelay = 0 };
Tap(sword);
for (int i = 0; i < 40 && sword.Move == SwordMove.Fast; i++) sword.Step(Tick, false, false, false);
sword.Step(Tick, false, false, false);
int ended = sword.Swing;
Tap(sword);
Check(sword.Move == SwordMove.FastEnd && sword.Swing == ended, FormattableString.Invariant($"a press early in A's recovery is dropped (cycle {sword.Cycle:0.00})"));
while (sword.Cycle < PlayerSword.FastEndOpen) sword.Step(Tick, false, false, false);
Check(Tap(sword) == SwordMove.Fast && sword.Clip.Clip == "fast" && sword.Swing == ended + 1, "one in its second half starts A again at once");
sword = new PlayerSword(clips) { FastDelay = 0 };
Tap(sword);
for (int i = 0; i < 3; i++) sword.Step(Tick, false, false, false);
for (int i = 0; i < 60 && !(sword.Move == SwordMove.Charge); i++) sword.Step(Tick, true, false, false, new Vector2(-1, 0));
Check(sword.Move == SwordMove.Charge && sword.Clip.Clip == "L-hold", $"attack held through B charges the power attack the movement picks then: {sword.Clip.Clip}");
sword.Step(Tick, false, false, false);
Check(sword.Move == SwordMove.Power && sword.Clip.Clip == "L-power", "and its release is that power attack");

// Directional power attacks: the movement as the charge begins picks it; back wins over a strafe, a strafe over forward.
foreach (var (move, expect) in new (Vector2, string)[] { (Vector2.Zero, "power"), (new(0, 1), "T-power"), (new(0, -.15f), "B-power"), (new(-1, 1), "L-power"),
    (new(-1, -.15f), "B-power"), (new(1, 0), "power") })
{
    var d = new PlayerSword(clips) { FastDelay = 0 };
    d.Step(Tick, true, false, false, move);
    for (int i = 0; i < 50; i++) d.Step(Tick, true, false, false);
    d.Step(Tick, false, false, false);
    Check(d.Move == SwordMove.Power && d.Clip.Clip == expect,
        FormattableString.Invariant($"movement ({move.X}, {move.Y}) as the charge begins releases {d.Clip.Clip} ({expect}{(expect == "power" && move != Vector2.Zero ? ": no R clip authored, the neutral one" : "")})"));
}

// The wind-up, not a fixed hold, decides: T's is longer than the neutral one, so a release the neutral charge would
// make a power attack is still T's fast attack.
var wind = new PlayerSword(clips) { FastDelay = 0 };
wind.Step(Tick, true, false, false, new Vector2(0, 1));
for (int i = 0; i < 36; i++) wind.Step(Tick, true, false, false);
wind.Step(Tick, false, false, false);
Check(wind.Move == SwordMove.Fast && wind.Clip.Clip == "fast", "released 0.6 s into T's 0.7 s wind-up, the attack is fast");

// Kicks (docs/lab.md, "Kicks"): a press stands while held and starts once the body may kick, from any move but a kick.
var k = new PlayerSword(clips) { FastDelay = 0 };
k.Step(Tick, false, false, true, mayKick: false);
for (int i = 0; i < 5; i++) k.Step(Tick, false, false, true, mayKick: false);
Check(k.Move == SwordMove.Ready && k.KickWanted, "a kick the body may not make yet waits while F is held");
k.Step(Tick, false, false, true);
Check(k.Move == SwordMove.Kick && k.SwingStarted && !k.KickWanted, "and starts the step it may");
k = new PlayerSword(clips) { FastDelay = 0 };
k.Step(Tick, false, false, true, mayKick: false);
k.Step(Tick, false, false, false);
k.Step(Tick, false, false, false);
Check(k.Move == SwordMove.Ready && !k.KickWanted, "released before it may, the kick is dropped");
k.Step(Tick, false, false, true);
int kicks = k.Swing;
for (int i = 0; i < 6; i++) k.Step(Tick, false, false, false);
k.Step(Tick, false, false, true);
k.Step(Tick, false, false, false);
for (int i = 0; i < 40; i++) k.Step(Tick, false, false, true);
Check(k.Swing == kicks, "a tap during a kick is dropped; a held press needs to go down after the kick began");
for (int i = 0; i < 60 && k.Swing == kicks; i++) k.Step(Tick, false, false, false);
k = new PlayerSword(clips) { FastDelay = 0 };
k.Step(Tick, false, false, true);
for (int i = 0; i < 6; i++) k.Step(Tick, false, false, false);
k.Step(Tick, false, false, true);
int heldFrom = k.Swing, waited = 0;
while (k.Swing == heldFrom && waited < 80) { k.Step(Tick, false, false, true); waited++; }
Check(k.Swing == heldFrom + 1 && k.Move == SwordMove.Kick && waited > 30, $"a press held through a kick kicks again as it ends ({waited} steps later)");
k = new PlayerSword(clips) { FastDelay = 0 };
for (int i = 0; i < 5; i++) k.Step(Tick, true, false, false);
k.Step(Tick, true, false, true);
Check(k.Move == SwordMove.Kick, "a kick cuts a charge");
k = new PlayerSword(clips) { FastDelay = 0 };
k.Step(Tick, false, false, true);
while (k.Cycle < .5f) k.Step(Tick, false, false, false);
k.Step(Tick, true, false, false);
k.Step(Tick, false, false, false);
Check(k.Move == SwordMove.Kick, "a tap of attack mid-kick does nothing");
while (k.Cycle < PlayerSword.KickAttackOpen) k.Step(Tick, false, false, false);
k.Step(Tick, true, false, false);
k.Step(Tick, false, false, false);
Check(k.Move == SwordMove.Kick, "on the ground a tap late in the kick does nothing either");
var air = new PlayerSword(clips) { FastDelay = 0 };
air.Step(Tick, false, false, true);
while (air.Cycle < PlayerSword.KickAttackOpen) air.Step(Tick, false, false, false, airborne: true);
air.Step(Tick, true, false, false, airborne: true);
air.Step(Tick, false, false, false, airborne: true);
Check(air.Move == SwordMove.Power && air.Clip.Clip == "power" && air.SwingStarted, "in the air, one late in the kick releases the neutral power attack at once (the jump attack)");
// Attack in a jump (#10144): nothing until held 0.125 s, a shorter press dropped; then without the jump attack's
// skill the neutral power attack's hold, with it the jump attack.
var jump = new PlayerSword(clips) { FastDelay = 0 };
for (int i = 0; i < 4; i++) jump.Step(Tick, true, false, false, airborne: true);
Check(jump.Move == SwordMove.Ready, "attack pressed in the air does nothing at first");
for (int i = 0; i < 4; i++) jump.Step(Tick, false, false, false, airborne: true);
Check(jump.Move == SwordMove.Ready, "and let go within 0.1 s it is dropped");
for (int i = 0; i < 9; i++) jump.Step(Tick, true, false, false, airborne: true);
Check(jump.Move == SwordMove.Charge && jump.Clip.Clip == "hold", "held 0.125 s, it holds the neutral power attack, no wind-up");
jump.Step(Tick, false, false, false, airborne: true);
Check(jump.Move == SwordMove.Power && jump.Clip.Clip == "power", "and its release is the power attack, not a fast one");
jump = new PlayerSword(clips) { FastDelay = 0 };
for (int i = 0; i < 7; i++) jump.Step(Tick, true, false, false, airborne: true);
jump.Step(Tick, false, false, false, airborne: true);
Check(jump.Move == SwordMove.Ready, "held longer than 0.1 s and let go, it waits");
jump.Step(Tick, false, false, false, airborne: true);
Check(jump.Move == SwordMove.Power, "and is the power attack at 0.125 s");
var landing = new PlayerSword(clips) { FastDelay = 0 };
landing.Step(Tick, false, false, false, airborne: true);
landing.Step(Tick, true, false, false);
landing.Step(Tick, false, false, false);
Check(landing.Move == SwordMove.Ready, "a tap while the landing plays counts as one in the air: dropped");
var leapClips = clips with { Leap = new LeapClips(M("leap-in", .3f), M("leap-idle", 2f / 30, true), M("leap-out", 1, open: .03f, close: .27f), M("leap-cancel", .3f)) };
var leap = new PlayerSword(leapClips) { FastDelay = 0, CanLeap = true };
for (int i = 0; i < 9; i++) leap.Step(Tick, true, false, false, airborne: true);
Check(leap.Move == SwordMove.Leap && leap.Clip.Clip == "leap-in", "with combat_vertical_attack the press winds up the jump attack");
for (int i = 0; i < 30 && leap.Clip.Clip == "leap-in"; i++) leap.Step(Tick, true, false, false, airborne: true);
Check(leap.Clip.Clip == "leap-idle", "held, it holds the jump attack's pose");
leap.Step(Tick, false, false, false, airborne: true);
Check(leap.Move == SwordMove.Leap, "the pose plays once before the release strikes");
for (int i = 0; i < 6 && leap.Move == SwordMove.Leap; i++) leap.Step(Tick, false, false, false, airborne: true);
Check(leap.Move == SwordMove.LeapOut && leap.SwingStarted, "then it strikes, still in the air");
for (int i = 0; i < 8; i++) leap.Step(Tick, false, false, false, airborne: true);
Check(leap.Traced, "and its strike traces the blade");
leap = new PlayerSword(leapClips) { FastDelay = 0, CanLeap = true };
for (int i = 0; i < 10; i++) leap.Step(Tick, true, false, false, airborne: true);
leap.Step(Tick, false, false, false, airborne: true);
Check(leap.Move == SwordMove.Power && leap.Clip.Clip == "power", "released early in the wind-up it is the neutral power attack");
leap = new PlayerSword(leapClips) { FastDelay = 0, CanLeap = true };
for (int i = 0; i < 20; i++) leap.Step(Tick, true, false, false, airborne: true);
leap.Step(Tick, true, false, false);
Check(leap.Move == SwordMove.LeapCancel, "landing while held cuts the jump attack short");
for (int i = 0; i < 20; i++) leap.Step(Tick, true, false, false);
Check(leap.Move == SwordMove.Charge && leap.Clip.Clip == "hold", "to the neutral power attack's hold");

// The rush attack (#10144): sprinting, a charge released from its hold rushes with combat_rush_attack; one released
// before its hold plays nothing.
var rushClips = clips with { Rush = new MoveClip("rush", 2, false, .085f, .17f, Later: [(.49f, .57f)]) };
var rush = new PlayerSword(rushClips) { FastDelay = 0, CanRush = true };
for (int i = 0; i < 40; i++) rush.Step(Tick, true, false, false, sprinting: true);
rush.Step(Tick, false, false, false, sprinting: true);
Check(rush.Move == SwordMove.Rush && rush.SwingStarted, "a charge released while sprinting is the rush attack");
var windows = new HashSet<int>();
for (int i = 0; i < 130 && rush.Move == SwordMove.Rush; i++) { rush.Step(Tick, false, false, false); if (rush.Traced) windows.Add(rush.Window); }
Check(windows.SetEquals([0, 1]), $"and strikes twice: windows {string.Join(", ", windows)}");
rush = new PlayerSword(rushClips) { FastDelay = 0, CanRush = false };
for (int i = 0; i < 40; i++) rush.Step(Tick, true, false, false, sprinting: true);
rush.Step(Tick, false, false, false, sprinting: true);
Check(rush.Move == SwordMove.Power, "without combat_rush_attack it is the power attack");
rush = new PlayerSword(rushClips) { FastDelay = 0, CanRush = true };
for (int i = 0; i < 6; i++) rush.Step(Tick, true, false, false, sprinting: true);
rush.Step(Tick, false, false, false, sprinting: true);
Check(rush.Move == SwordMove.Ready, "released before its hold while sprinting, nothing plays");

// The Fatality and the Whirlwind (#10145): with the gauge full a charge released from its hold is the huge slash, or
// crouched with combat_trample the Whirlwind; a quick release is still a fast attack.
var fatalClips = clips with { Fatality = new MoveClip("huge", 2.1f, false, .14f, .22f, Later: [(.4f, .48f)]), Whirlwind = M("whirl", 1.9f) };
var fatal = new PlayerSword(fatalClips) { FastDelay = 0, Fatal = true, CanWhirl = true };
for (int i = 0; i < 40; i++) fatal.Step(Tick, true, false, false);
fatal.Step(Tick, false, false, false);
Check(fatal.Move == SwordMove.Fatality && fatal.SwingStarted, "with the gauge full a power strike is the Fatality's huge slash");
fatal = new PlayerSword(fatalClips) { FastDelay = 0, Fatal = true, CanWhirl = true };
for (int i = 0; i < 40; i++) fatal.Step(Tick, true, false, false, crouched: true);
fatal.Step(Tick, false, false, false, crouched: true);
Check(fatal.Move == SwordMove.Whirlwind, "crouched, the Whirlwind");
fatal = new PlayerSword(fatalClips) { FastDelay = 0, Fatal = true, CanWhirl = false };
for (int i = 0; i < 40; i++) fatal.Step(Tick, true, false, false, crouched: true);
fatal.Step(Tick, false, false, false, crouched: true);
Check(fatal.Move == SwordMove.Fatality, "without combat_trample, crouched is still the huge slash");
fatal = new PlayerSword(fatalClips) { FastDelay = 0, Fatal = true };
for (int i = 0; i < 6; i++) fatal.Step(Tick, true, false, false);
fatal.Step(Tick, false, false, false);
Check(fatal.Move == SwordMove.Fast, "and a quick release is a fast attack");
var gauge = new PlayerAdrenaline();
for (int i = 0; i < 7; i++) gauge.Gain(3);
Check(gauge.Full && gauge.Value == 20, "the gauge fills by each blow's adrenalineadd, to 20");
gauge.Spend();
Check(gauge.Value == 0, "and a landed Fatality empties it");

// A build without combat_parry (#10140's gate) doesn't parry: holding block does nothing.
var noParry = new PlayerSword(clips) { FastDelay = 0,  CanParry = false };
for (int i = 0; i < 12; i++) noParry.Step(Tick, false, true, false);
Check(noParry.Move == SwordMove.Ready, "without combat_parry, holding block doesn't parry");
var airParry = new PlayerSword(clips) { FastDelay = 0 };
for (int i = 0; i < 6; i++) airParry.Step(Tick, false, true, false, airborne: true);
Check(airParry.Move == SwordMove.Ready, "nor in the air (#10144)");

// Stamina: Dark Messiah's numbers, a kick's threshold and cost, sprint's drain and the regeneration.
var stamina = new PlayerStamina(StaminaRules.Shipped);
for (int i = 0; i < 60; i++) stamina.Step(Tick, sprintAsked: true);
Check(MathF.Abs(stamina.Value - 7) < .01f, FormattableString.Invariant($"sprinting drains 2 a second: 9 → {stamina.Value:0.00}"));
stamina = new PlayerStamina(StaminaRules.Shipped);
stamina.Kicked(npc: true);
stamina.Kicked(npc: false);
stamina.Kicked(npc: false);
stamina.Kicked(npc: false);
Check(stamina.Value == 3 && stamina.MayKick, "a kick on an NPC costs 3, one that lands on none 1; 3 left may still kick");
stamina.Kicked(npc: false);
Check(!stamina.MayKick, "under 3 it may not");
for (int i = 0; i < 120; i++) stamina.Step(Tick, sprintAsked: false);
Check(MathF.Abs(stamina.Value - (2 + 16 * .06f)) < .07f, FormattableString.Invariant($"it regains 0.06 every 0.12 s: {stamina.Value:0.00} after 2 s"));
// A sprint starts only with 3 (#10144), and once going runs on below it.
stamina.Step(Tick, sprintAsked: true);
Check(!stamina.Sprinting, FormattableString.Invariant($"with {stamina.Value:0.00} a sprint doesn't start"));
for (int i = 0; i < 60 && stamina.Value < 3; i++) stamina.Step(Tick, sprintAsked: false);
stamina.Step(Tick, sprintAsked: true);
for (int i = 0; i < 20; i++) stamina.Step(Tick, sprintAsked: true);
Check(stamina.Sprinting && stamina.Value < 3, FormattableString.Invariant($"from 3 it does, and goes on below: {stamina.Value:0.00}"));

// The donor's A begins about 0.04 s after the release (#10142): the arm stays in the wind-up until then.
var late = new PlayerSword(clips);
late.Step(Tick, true, false, false);
late.Step(Tick, false, false, false);
var lateMoves = new List<SwordMove> { late.Move };
for (int i = 0; i < 3 && late.Move != SwordMove.Fast; i++) { late.Step(Tick, false, false, false); lateMoves.Add(late.Move); }
Check(lateMoves[0] == SwordMove.Charge && late.Move == SwordMove.Fast && lateMoves.Count is >= 3 and <= 4,
    $"a released wind-up becomes A {late.FastDelay:0.00} s later: {string.Join(" ", lateMoves)}");

// Phase timing (review R10121-1): a fast attack's windup, active and recovery each scale its time; donor timing is ×1.
float[] FastTiming(MovePhases p)
{
    var s = new PlayerSword(clips with { Chain = [clips.Fast with { Phases = p }, .. clips.Chain.Skip(1)] }) { FastDelay = 0 };
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
    for (int i = 0; i < 60; i++) Feed(1);
    Feed(1, Primary(InputEdge.Pressed));
    for (int i = 0; i < 30; i++) Feed(1);
    Feed(1, Primary(InputEdge.Released));
    moves.Clear();
    for (int i = 0; i < 5; i++) { Feed(1); moves.Add(combat.Sword.Move); }
    Check(moves.Contains(SwordMove.Power), "a hold past the power threshold is still a power attack");
    for (int i = 0; i < 90; i++) Feed(1);
    // The body sprints only as stamina admits (R10144-3), through the product: held sprint with forward sprints from
    // full stamina, stops once it's spent, and doesn't start again below 3.
    {
        static ProductInputEvent Key(KeyboardControl key, InputEdge edge) => default(ProductInputEvent) with { Kind = InputEventKind.Key, Keyboard = key, Edge = edge };
        // Put back after: the player where it stood, the orc where it waited.
        var (stood, facing, waited) = (product.Player.Position, product.Player.LookState.YawRadians * 180 / MathF.PI, combat.Orc.Feet);
        combat.Reset();
        Feed(1, Key(KeyboardControl.KeyW, InputEdge.Pressed), Key(KeyboardControl.ShiftLeft, InputEdge.Pressed));
        Feed(1);
        bool fresh = product.Player.Sprinted && combat.Stamina.Sprinting;
        for (int i = 0; i < 600 && combat.Stamina.Value > 0; i++) Feed(1);
        Feed(1);
        bool spent = !product.Player.Sprinted && !combat.Stamina.Sprinting;
        Feed(1, Key(KeyboardControl.ShiftLeft, InputEdge.Released));
        for (int i = 0; i < 600 && combat.Stamina.Value < 1.5f; i++) Feed(1);
        Feed(1, Key(KeyboardControl.ShiftLeft, InputEdge.Pressed));
        Feed(1);
        bool low = !product.Player.Sprinted && !combat.Stamina.Sprinting;
        Feed(1, Key(KeyboardControl.ShiftLeft, InputEdge.Released), Key(KeyboardControl.KeyW, InputEdge.Released));
        Check(fresh && spent && low, FormattableString.Invariant(
            $"the body sprints as stamina admits: from full ({fresh}), not once spent ({spent}), not from {combat.Stamina.Value:0.0} ({low})"));
        Feed(1);
        product.Player.Place(stood, facing);
        combat.Orc.PlaceAt(waited);
        combat.Reset();
    }
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 1 } });
    combat.Orc.Spawn();
    Console.WriteLine($"donor content: {product.Donor.Source}");
    Check(combat.FirstPerson.Range > 2.4f && combat.FirstPerson.Range < 2.6f, FormattableString.Invariant($"the short sword's range is its rangeattack, 100 units: {combat.FirstPerson.Range:0.00} m"));
    // The gameplay tables (#10138): the scripts, item .qct and item models dm-import copies, read through the lab.
    var donor = product.Donor;
    string[] scripts = ["scripts/mm_game_settings.txt", "scripts/spells.txt", "scripts/spells_entityspellcaster.txt", "scripts/mm_skills_infos.txt",
        "scripts/mm_relationship.txt", "scripts/propdata.txt", "scripts/surfaceproperties.txt", "scripts/kb_act.lst", "scripts/plugin_animations.txt"];
    var missingScripts = scripts.Where(x => donor.Read(x) is null).ToList();
    Check(missingScripts.Count == 0, $"the pack holds the game's scripts{(missingScripts.Count > 0 ? ": missing " + string.Join(", ", missingScripts) : "")}");
    var tables = donor.Tables;
    Check(tables.Spell("air_1", "ManaCost_Use") == 2 && tables.Spell("air_1", "Big_Object_Mass") == 120 && tables.Spells.Count >= 16,
        $"spells.txt reads: telekinesis costs 2 mana to use, {tables.Spells.Count} spells");
    Check(tables.Skills.TryGetValue("combat", out var skill) && skill.MaxLevel == 3 && skill.Levels.Count == 4
        && skill.Levels[1].Cost == 1 && skill.Levels[1].Modules.Contains("combat_chained_slash"),
        $"mm_skills_infos.txt reads: combat has levels 0–3, level 1 costs 1 and unlocks the chained slash ({tables.Skills.Count} skills)");
    Check(donor.Number("models/items/weapons/sword_long/sword_long.mdl", "entity_data", "WDmg") == 3
        && donor.Number("models/items/weapons/bow_classic/bow_classic.mdl", "entity_data", "ammo") == 30
        && donor.Number("models/items/weapons/bow_classic/bow_classic.mdl", "entity_data", "rangeattack") == 80,
        "item .qct read: the long sword's WDmg 3, the classic bow's ammo 30 and rangeattack 80");
    var daggers = donor.Model("models/items/weapons/daggers/daggers.mdl");
    Check(daggers.Joints.Count > 0, $"item models open: the daggers ({daggers.Joints.Count} joints, {daggers.Info.Collision.Count} hulls)");
    // Builds (#10140): build.json's, checked against mm_skills_infos.txt and the item tables.
    var own = combat.Build!;
    Check(own.Source == BuildFile.Path && own.Modules.SetEquals(["combat_big_slash", "combat_huge_slash", "combat_parry", "combat_fatality"])
        && own.Weapon == LabWeapon.ShortSword && own.OffHand is null,
        $"the lab plays build.json: every skill at 0 gives the combat skill's level-0 modules, as a new donor game has ({own})");
    var file = combat.BuildFile!;
    string? Refused(BuildFile f) { try { combat.BuildFrom(f); return null; } catch (InvalidOperationException e) { return e.Message; } }
    BuildFile WithSkill(string name, int level) => file with { Skills = new Dictionary<string, int>(file.Skills) { [name] = level } };
    Check(Refused(WithSkill("swordplay", 1)) is string unknown && unknown.Contains("isn't a skill"), "a skill mm_skills_infos.txt lacks is refused");
    Check(Refused(WithSkill("combat", 4)) is string high && high.Contains("0 to 3"), "a level above the skill's max_level is refused");
    Check(Refused(WithSkill("adrenaline", 1)) is string needs && needs.Contains("critical_hit"), "a skill whose requirement isn't learned is refused (adrenaline needs critical_hit or extra_damage)");
    var chain = new Dictionary<string, int>(file.Skills) { ["combat"] = 1, ["extra_damage"] = 1, ["adrenaline"] = 1 };
    Check(Refused(file with { Skills = chain }) is null && Refused(file with { Skills = new Dictionary<string, int>(chain) { ["combat"] = 0 } }) is string chained && chained.Contains("extra_damage"),
        "with one of its requirements it's accepted, and that one needs its own (extra_damage needs combat or projectile)");
    Check(Refused(file with { Weapon = "models/items/weapons/sword_long/sword_long.mdl" }) is null
        && Refused(file with { Weapon = "models/items/weapons/daggers_kryss/daggers_kryss.mdl" }) is null
        && Refused(file with { Weapon = "models/items/weapons/staff_combat/staff_combat.mdl" }) is null
        && Refused(file with { Weapon = "models/items/weapons/bow_classic/bow_classic.mdl" }) is string bow && bow.Contains("isn't a sword")
        && Refused(file with { OffHand = "models/items/armors/shield_orc/shield_orc.mdl" }) is string shield && shield.Contains("#10149")
        && Refused(file with { Weapon = "models/items/weapons/nothing/nothing.mdl" }) is string missing && missing.Contains("isn't an imported item"),
        "swords, daggers and staffs are played (#10148); a bow and shields aren't yet (shields naming #10149); items the import lacks are refused");
    combat.Rebuild(WithSkill("stamina", 1));
    Check(combat.Build!.Has("double_sprint") && combat.Stamina.Rules.Max == 12, $"stamina 1 gives double_sprint, and stamina's maximum becomes 12: {combat.Stamina.Rules.Max}");
    combat.Rebuild(file);
    Check(combat.Stamina.Rules.Max == 9 && combat.Stamina.Value <= 9, "back at stamina 0 the maximum is 9 again");
    combat.Rebuild(combat.Build! with { Modules = new HashSet<string>(), Source = "no modules" });
    var parries = new List<SwordMove>();
    for (int i = 0; i < 20; i++) { combat.Step(Tick, step++, false, true, false, product.Player.Eye, product.Player.Forward, product.Player.Feet); parries.Add(combat.Sword.Move); }
    Check(!parries.Contains(SwordMove.Parry), "a build without combat_parry can't parry in the fight");
    combat.Rebuild(file);
    // The player's stat block is read, not defaulted: the first "skills" block in player_basic.qct is a starting build's.
    Check(product.Donor.Combat.PlayerSkillNumber("player_stamina_base_max") == 9 && product.Donor.Combat.PlayerSkillNumber("player_strength_4") == 6
        && product.Donor.Combat.PlayerStrength(3) == 6 && product.Donor.Combat.PlayerStrength(0) == 0,
        "player_basic.qct's stat block reads: stamina 9, and extra_damage 3 gives player_strength_4, 6");
    var rules = combat.Stamina.Rules;
    Check(rules == StaminaRules.Shipped, $"stamina comes from player_basic.qct's skills, the settings and the ConVar defaults: {rules}");
    Check(MathF.Abs(combat.FirstPerson.AttackSpeed - 1.1f) < 1e-3f && MathF.Abs(orc.AttackSpeed - .8f) < 1e-3f,
        "attack clips play at the weapons' speed_attack: the short sword 1.10, the cleaver 0.80");
    var fast = combat.Sword.Clip;
    var real = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning);
    Check(real.Charges.Count == 5 && real.Charge(PowerDirection.L).Out.Clip == "layer_bigslash_sword_noshield_L_out" && real.FastB.Traced,
        $"the first-person model has the neutral and four directional power attacks, and B's own hit window ({real.FastB.Open:0.00}–{real.FastB.Close:0.00})");
    Check(real.Moves.Where(m => m != real.Whirlwind).All(m => m.Traced) && MathF.Abs(real.Charge(PowerDirection.B).Out.Open - .105f) < .01f && MathF.Abs(real.Charge(PowerDirection.B).Out.Close - .21f) < .01f,
        FormattableString.Invariant($"every move but the Whirlwind has a hit window; B's and T's, with no 2186/2190, run 2107–2110 (B {real.Charge(PowerDirection.B).Out.Open:0.00}–{real.Charge(PowerDirection.B).Out.Close:0.00})"));
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
    var swung = new List<SwordMove>();
    (int Hits, float Health) Swing(int holdSteps, bool block = false)
    {
        int hits = combat.Player.Hits;
        swung.Clear();
        for (int i = 0; i < holdSteps; i++) combat.Step(Tick, step++, true, block, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        for (int i = 0; i < 90 && combat.Sword.Move != SwordMove.Ready; i++) { combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet); swung.Add(combat.Sword.Move); }
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
    float wdmg = data.Weapon(combat.FirstPerson.SwordModel)!.Damage;
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
    Check(swung.Contains(SwordMove.FastEnd), "the parried swing plays on to its recovery: the donor's sword doesn't bounce off a parry");
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { ParryChance = 0 } });
    Face();
    float before = orc.Health;
    combat.Step(Tick, step++, false, false, true, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    for (int i = 0; i < 60; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(MathF.Abs(before - orc.Health - data.Settings.KickDamage) < 1e-3f && orc.State == OrcState.Pushed,
        FormattableString.Invariant($"a kick asks the settings' kick_damage and pushes the orc: {before:0.##} → {orc.Health:0.##}, {orc.State}"));

    // The followed orc takes the recorded body model (R10177-2). Every recorded npc_orc_sword is npc_orc.mdl, so
    // another model with an idle stands in (the goblin's), whose hit boxes the sweep then uses; a one-clip ragdoll
    // body (npc_orc_noRightArm) still poses. After the replay the lab's own body comes back.
    {
        const string goblin = "models/npc/goblin/npc_goblin.mdl", armless = "models/npc/orc/npc_orc_noRightArm.mdl";
        orc.Follow(new OrcPose("#9.1", orc.Feet, orc.Facing, 50, "combatidle", 0, 1, null, 0, true, [Orc.Cleaver], goblin), Tick);
        string followedModel = orc.BodyModel.DonorPath;
        int followedBoxes = orc.Targets()[0].Model.Hitboxes.Count;
        int drawn = orc.Facts().Count();
        orc.Follow(new OrcPose("#9.1", orc.Feet, orc.Facing, 50, "attack_Fast", 0, 1, null, 0, true, [Orc.Cleaver], armless), Tick);
        string ragdollClip = orc.Clip;
        orc.Facts().ToList();
        orc.StopFollowing();
        Check(followedModel.Equals(goblin, StringComparison.OrdinalIgnoreCase) && followedBoxes == donor.Model(goblin).Info.Hitboxes.Count && drawn > 0
            && ragdollClip == "reference" && orc.BodyModel.DonorPath == Orc.Model,
            $"a replay's orc is drawn and swept as the recorded model ({followedModel}, {followedBoxes} hit boxes; a ragdoll body poses its {ragdollClip}), and the lab's own body comes back after");
    }

    // Slow motion (#10147): the Whirlwind asks its .qct ×0.5 for 2.5 s as it begins, which the world runs at for
    // 1.25 s of game time; the lab's world time follows TimeScale.
    {
        var ownBuild = combat.Build!;
        combat.Rebuild(ownBuild with { Modules = new HashSet<string>(ownBuild.Modules) { "combat_trample", "combat_fatality" } });
        combat.Reset();
        Face();
        combat.Adrenaline.Set(20, 20);
        for (int i = 0; i < 50; i++) combat.Step(Tick, step++, true, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, crouched: true);
        combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, crouched: true);
        bool whirled = combat.Sword.Move == SwordMove.Whirlwind;
        double? during = combat.TimeScale;
        var asked = combat.SlowTimeRequests.LastOrDefault().Slow;
        for (int i = 0; i < 80; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        Check(whirled && during == .5 && asked == new DarkMessiah.Combat.SlowTime(0, 2.5f, .5f) && combat.TimeScale is null,
            FormattableString.Invariant($"the Whirlwind asks ×0.5 for 2.5 s at once, and the world runs at it for 1.25 s of game time ({during}, then {combat.TimeScale?.ToString() ?? "realtime"})"));
        // Its blow may critical (critical_enable): a recorded critical doubles it and counts (R10146-1).
        (float Dealt, int Criticals) Whirled(bool critical)
        {
            combat.Reset();
            Face();
            combat.RecordedCritical = _ => critical;
            combat.Adrenaline.Set(20, 20);
            float dealt = combat.Player.Dealt;
            int crits = combat.Criticals;
            for (int i = 0; i < 50; i++) combat.Step(Tick, step++, true, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, crouched: true);
            for (int i = 0; i < 3; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, crouched: true);
            combat.RecordedCritical = null;
            return (combat.Player.Dealt - dealt, combat.Criticals - crits);
        }
        var plain = Whirled(false);
        var doubled = Whirled(true);
        Check(plain.Dealt > 0 && MathF.Abs(doubled.Dealt - 2 * plain.Dealt) < 1e-3f && plain.Criticals == 0 && doubled.Criticals == 1,
            FormattableString.Invariant($"a Whirlwind takes a recorded critical: {plain.Dealt:0.##}, critical {doubled.Dealt:0.##}, counted {doubled.Criticals}"));
        combat.Rebuild(ownBuild);
        combat.Reset();
    }

    // A kick tuned to a contact window shorter than a step (review R10121-4: windup ×1.4, active ×0.25 makes the
    // window 0.4200–0.4325 s, which a 60 Hz step jumps over) still contacts once, and the kick still ends.
    var untuned = combat.Tuning;
    combat.Retune(untuned with { Phases = untuned.Phases with { Kick = new MovePhases(1.4f, .25f, 1) } });
    Face();
    float beforeShort = orc.Health;
    int hitsShort = combat.Player.Hits;
    combat.Step(Tick, step++, false, false, true, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    for (int i = 0; i < 90 && combat.Sword.Move != SwordMove.Ready; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(combat.Player.Hits == hitsShort + 1 && MathF.Abs(beforeShort - orc.Health - data.Settings.KickDamage) < 1e-3f && combat.Sword.Move == SwordMove.Ready,
        FormattableString.Invariant($"a kick whose window is shorter than a step contacts once ({combat.Player.Hits - hitsShort}) and recovers"));
    combat.Retune(untuned);

    // The player's parry against the orc's attacks: parried, or broken by a part that breaks parries.
    combat.Retune(combat.Tuning with { Orc = combat.Tuning.Orc with { AttackCooldown = 0 } });
    Face();
    var (p0, b0) = (combat.OrcStats.Parried, combat.OrcStats.Broken);
    for (int i = 0; i < 600; i++) combat.Step(Tick, step++, false, true, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(combat.OrcStats.Parried + combat.OrcStats.Broken > p0 + b0,
        $"the player's guard meets the orc's blows: {combat.OrcStats.Parried - p0} parried, {combat.OrcStats.Broken - b0} broken");
    Advance(30);
    Check(true, $"the fight runs: {combat.Message}");

    // Weapons (#10148): each class's moves from first_basic, each weapon's numbers from its .qct.
    const string LongSword = "models/items/weapons/sword_long/sword_long.mdl", Club = "models/items/weapons/club/club.mdl",
        Kryss = "models/items/weapons/daggers_kryss/daggers_kryss.mdl", CombatStaff = "models/items/weapons/staff_combat/staff_combat.mdl";
    {
        var (kryss, staffWeapon, club, longSword) = (LabWeapon.Of(donor, Kryss), LabWeapon.Of(donor, CombatStaff), LabWeapon.Of(donor, Club), LabWeapon.Of(donor, LongSword));
        float Units(LabWeapon w) => w.Range / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        Check(kryss.Class == WeaponClass.Daggers && staffWeapon.Class == WeaponClass.Staff && club is { Class: WeaponClass.Sword, Simple: true } && longSword is { Class: WeaponClass.Sword, Simple: false }
            && MathF.Abs(Units(kryss) - 80) < .01f && MathF.Abs(Units(staffWeapon) - 110) < .01f && MathF.Abs(Units(longSword) - 100) < .01f
            && MathF.Abs(kryss.AttackSpeed - 1.1f) < 1e-3f && MathF.Abs(staffWeapon.AttackSpeed - 1.1f) < 1e-3f && longSword.AttackSpeed == 1 && club.AttackSpeed == 1,
            "weapons read their class and .qct: the kryss daggers reach 80 units at speed 1.10, the combat staff 110 at 1.10, the long sword 100 at 1, the club is a simple weapon");
        var daggerClips = combat.ClipsFor(Kryss)!;
        var staff = combat.ClipsFor(CombatStaff)!;
        var clubClips = combat.ClipsFor(Club)!;
        Check(daggerClips.Chain.Select(c => c.Clip).SequenceEqual("ABCDE".Select(c => $"layer_fastattack_daggers_{c}")) && daggerClips.Wraps
            && daggerClips.Ends.Select(e => e?.Clip).SequenceEqual(["layer_fastattack_daggers_end_A", "layer_fastattack_daggers_end_B", "layer_fastattack_daggers_end_C", "layer_fastattack_daggers_end_D", null])
            && staff.Chain.Select(c => c.Clip).SequenceEqual("ABCD".Select(c => $"layer_fastattack_staff_{c}")) && staff.Wraps
            && staff.Ends.Select(e => e?.Clip).SequenceEqual(["layer_fastattack_staff_A_end", "layer_fastattack_staff_B_end", "layer_fastattack_staff_C_end", null])
            && daggerClips.Power.Clip == "layer_bigslash_Daggers_out" && staff.Power.Clip == "layer_bigslash_staff_out" && daggerClips.Charges.Count == 5 && staff.Charges.Count == 5
            && daggerClips.Draw?.Clip == "layer_daggers_draw" && staff.Draw?.Clip == "layer_staff_draw" && clubClips.Draw is null && real.Draw?.Clip == "layer_sword_noshield_draw",
            "the daggers chain A to E, the staff A to D, both wrapping to A, each with its own recovery but the last; both have five power attacks; the club draws at once");
        foreach (var set in new[] { daggerClips, staff })
            Check(set.Moves.Where(m => m != set.Whirlwind).All(m => m.Traced),
                $"every {set.Idle.Clip.Replace("layer_", "").Replace("_idle", "")} move but the Whirlwind has a hit window: {string.Join(", ", set.Moves.Where(m => m != set.Whirlwind && !m.Traced).Select(m => m.Clip))}");
        // Taps: A tap, then a tap as each new fast attack begins, until the sword has swungBefore `swings` times; the fast attacks it played, then how it ended.
        (List<string> Played, SwordMove Last, string LastClip) Taps(SwordClips set, int swings, int chain = 5)
        {
            var s = new PlayerSword(set) { FastDelay = 0, ChainLength = chain };
            s.Step(Tick, true, false, false);
            s.Step(Tick, false, false, false);
            var played = new List<string>();
            for (int i = 0; i < 600; i++)
            {
                if (s.SwingStarted || i == 0 && s.Move == SwordMove.Fast) played.Add(s.Clip.Clip[^1..]);
                bool tap = s.Move == SwordMove.Fast && s.Cycle < .5f && played.Count < swings && s.SwingStarted;
                s.Step(Tick, tap, false, false);
                if (tap) s.Step(Tick, false, false, false);
                if (s.Move is SwordMove.Ready or SwordMove.FastEnd && played.Count > 0) return (played, s.Move, s.Clip.Clip);
            }
            return (played, s.Move, s.Clip.Clip);
        }
        var wrap = Taps(daggerClips, 6);
        Check(string.Concat(wrap.Played) == "ABCDEA" && wrap.Last == SwordMove.FastEnd && wrap.LastClip == "layer_fastattack_daggers_end_A",
            $"the daggers: a press during E chains A again (the recordings: 6 of 6), and that A recovers with end_A ({string.Concat(wrap.Played)}, then {wrap.LastClip})");
        var threeDaggers = Taps(daggerClips, 3);
        var oneStaff = Taps(staff, 1);
        var threeStaff = Taps(staff, 3);
        Check(threeDaggers.LastClip == "layer_fastattack_daggers_end_C" && oneStaff.LastClip == "layer_fastattack_staff_A_end" && threeStaff.LastClip == "layer_fastattack_staff_C_end"
            && string.Concat(threeStaff.Played) == "ABC",
            $"a daggers or staff chain stopped at C recovers with C's own (the recordings: end_C, C_end 9 of 9), A with A's ({threeDaggers.LastClip}, {oneStaff.LastClip}, {threeStaff.LastClip})");
        var clubChain = Taps(clubClips, 5, chain: 2);
        Check(string.Concat(clubChain.Played) == "AB", $"the club chains A and B only (the recordings: B to C 0 of 8; long sword 8 of 8): {string.Concat(clubChain.Played)}");
        // Switching: the sword puts itself away, then the daggers are drawn; taps meanwhile are dropped, attack held winds up after.
        var switching = new PlayerSword(real) { FastDelay = 0 };
        switching.Equip(daggerClips);
        bool undrawFirst = switching.Move == SwordMove.Undraw && switching.Clip.Clip == "layer_sword_noshield_undraw";
        float away = 0, drawn = 0;
        while (switching.Move == SwordMove.Undraw && away < 2) { switching.Step(Tick, false, false, false); away += Tick; }
        bool daggersDraw = switching.Move == SwordMove.Draw && switching.Clip.Clip == "layer_daggers_draw";
        int swungBefore = switching.Swing;
        for (int i = 0; i < 6; i++) { switching.Step(Tick, i % 2 == 0, false, false); drawn += Tick; }
        while (switching.Move == SwordMove.Draw && drawn < 3) { switching.Step(Tick, true, false, false); drawn += Tick; }
        switching.Step(Tick, true, false, false);
        Check(undrawFirst && daggersDraw && MathF.Abs(away - .267f) < .03f && MathF.Abs(drawn - .8f) < .03f && switching.Swing == swungBefore
            && switching.Move == SwordMove.Charge && switching.Clip.Clip == "layer_bigslash_Daggers_in",
            FormattableString.Invariant($"switching to the daggers puts the sword away ({away:0.00} s; recorded 0.27) and draws them ({drawn:0.00} s; recorded 0.84), taps meanwhile dropped, attack held winds up after: {switching.Clip.Clip}"));
        var toClub = new PlayerSword(staff) { FastDelay = 0 };
        toClub.Equip(clubClips);
        float staffAway = 0;
        while (toClub.Move == SwordMove.Undraw && staffAway < 2) { toClub.Step(Tick, false, false, false); staffAway += Tick; }
        Check(MathF.Abs(staffAway - .333f) < .03f && toClub.Move == SwordMove.Ready && toClub.Clips == clubClips,
            FormattableString.Invariant($"to the club: the staff away ({staffAway:0.00} s; recorded 0.33-0.39), the club in hand at once"));
        var grabbed = new PlayerSword(real) { FastDelay = 0 };
        grabbed.QuickDraw();
        Check(grabbed.Move == SwordMove.Draw && grabbed.Clip.Clip == "layer_sword_noshield_quickdraw",
            "the weapon coming back after the hands were taken plays the quick draw (the recordings: after each of 4 orc grabs)");
        // The build's weapon: the club caps the chain at B even with the chained slash.
        var labBuild = combat.Build!;
        combat.Rebuild(labBuild with { Weapon = Club, Modules = new HashSet<string>(labBuild.Modules) { "combat_chained_slash" } }, draw: false);
        bool clubCaps = combat.Sword.ChainLength == 2 && combat.Weapon.Model == Club && combat.Sword.Clips.Draw is null;
        combat.Rebuild(labBuild with { Weapon = Kryss, Modules = new HashSet<string>(labBuild.Modules) { "combat_chained_slash" } }, draw: false);
        bool daggersChain = combat.Sword.ChainLength == 5 && combat.Sword.Clips.Fast.Clip == "layer_fastattack_daggers_A" && combat.FirstPerson.SwordModel == Kryss;
        // Both daggers are drawn (R10148-3), one in each hand, through a chain of taps; switching away leaves one weapon.
        var blades = new List<int>();
        float apart = float.MaxValue;
        for (int i = 0; i < 90; i++)
        {
            combat.Step(Tick, step++, i % 12 < 3, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
            blades.Add(combat.FirstPerson.Facts().Count());
            if (combat.FirstPerson.OffHand is { } left) apart = MathF.Min(apart, Vector3.Distance(left.Translation, combat.FirstPerson.Sword.Translation));
        }
        bool swungDaggers = combat.Player.Swings > 0;
        combat.Rebuild(labBuild with { Weapon = LongSword }, draw: true);
        for (int i = 0; i < 200 && combat.Sword.Drawing; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        Check(swungDaggers && blades.All(c => c == 3) && apart > .05f && combat.FirstPerson.Facts().Count() == 2 && combat.FirstPerson.OffHand is null,
            FormattableString.Invariant($"the daggers are drawn as two blades, one in each hand ({apart:0.00} m apart at the closest) through a chain, and one weapon after switching to the long sword"));
        combat.Rebuild(labBuild with { Weapon = Kryss, Modules = new HashSet<string>(labBuild.Modules) { "combat_chained_slash" } }, draw: false);
        combat.Rebuild(labBuild with { Weapon = CombatStaff }, draw: true);
        bool drawing = combat.Sword.Move == SwordMove.Undraw && combat.FirstPerson.SwordModel == Kryss && combat.Weapon.Model == CombatStaff;
        combat.Rebuild(labBuild, draw: false);
        combat.Reset();
        Check(clubCaps && daggersChain && drawing && combat.FirstPerson.SwordModel == LabWeapon.ShortSword,
            "the build's weapon is the one played: the club with the chained slash still chains two, the daggers five, and a new build's weapon is drawn once the old is away");
    }

    // Calibration: the lab's windows and clip rates against recorded player blows (Wire v9 and later).
    string recordings = Path.Combine(repo, "local", "recordings");
    var found = Directory.Exists(recordings) ? Directory.GetFiles(recordings, "*.rbrec") : [];
    if (found.Length == 0) { Console.WriteLine("skip calibration: no local/recordings"); return; }
    // The chained-slash scenario's recording (#10143) is an experiment, not a fight to replay as one, but its blows
    // calibrate C, D and E.
    string chainedSlash = Path.Combine(recordings, "experiments", "l01_a-v16-chained-slash.rbrec");
    // The movement attacks (#10144): jumps with and without combat_vertical_attack, and rushes.
    string jumpAttack = Path.Combine(recordings, "experiments", "l01_a-v16-jump-attack.rbrec"),
        jumpHold = Path.Combine(recordings, "experiments", "l01_a-v16-jump-attack-hold.rbrec"),
        jumpBasic = Path.Combine(recordings, "experiments", "l01_a-v16-jump-attack-basic.rbrec"),
        rushAttack = Path.Combine(recordings, "experiments", "l01_a-v16-rush-attack.rbrec");
    // Adrenaline and its moves (#10145): the gauge filled by power strikes, the Fatality, the Whirlwind and disarm tries.
    string[] adrenalineRuns = [.. new[] { "adrenaline", "adrenaline-first", "fatality", "fatality-first", "whirlwind", "disarm" }
        .Select(n => Path.Combine(recordings, "experiments", $"l01_a-v16-{n}.rbrec")).Where(File.Exists)];
    string[] calibrated = [.. found, .. new[] { chainedSlash, jumpAttack, jumpHold, jumpBasic, rushAttack }.Where(File.Exists), .. adrenalineRuns];
    var calibration = SwordCalibration.Measure(calibrated, real);
    foreach (var move in calibration) Console.WriteLine($"  {move}");
    foreach (var move in calibration.Where(m => m.Blows.Count >= 10))
        Check(move.Inside >= move.Blows.Count * 8 / 10, $"{move.Clip}: recorded blows land inside the lab's window ({move.Inside} of {move.Blows.Count})");
    foreach (var move in calibration.Where(m => float.IsFinite(m.DonorCyclesPerSecond)))
        Check(MathF.Abs(move.LabCyclesPerSecond / move.DonorCyclesPerSecond - 1) < .15f,
            FormattableString.Invariant($"{move.Clip}: the lab plays it at the donor's rate ({move.LabCyclesPerSecond:0.00} against {move.DonorCyclesPerSecond:0.00} cycles/s)"));

    // The replay body carries held jump and crouch, not only presses (review R10122-4).
    {
        IReplayBody replayBody = product;
        replayBody.Begin();
        for (int i = 0; i < 20; i++) replayBody.Step(default, product.Player.Forward, false, false, false, false, Tick);
        var stances = new List<CharacterStance>();
        for (int i = 0; i < 40; i++) { replayBody.Step(default, product.Player.Forward, false, false, true, false, Tick); stances.Add(product.Player.Motion.Stance); }
        for (int i = 0; i < 40; i++) replayBody.Step(default, product.Player.Forward, false, false, false, false, Tick);
        Check(stances.Skip(10).All(s => s == CharacterStance.Crouched) && product.Player.Motion.Stance != CharacterStance.Crouched,
            "a recorded duck held crouches the lab's body until it's released");
        var held = new List<bool>();
        for (int i = 0; i < 12; i++) { replayBody.Step(default, product.Player.Forward, i == 0, true, false, false, Tick); held.Add(product.LastReplayInput.JumpHeld); }
        Check(held.All(h => h) && product.LastReplayInput.JumpPressed == false, "a recorded jump held stays held on every tick it is, pressed only on the first");
        replayBody.End();
    }

    // Damage (review R10121-2): every recorded player blow on an orc, asked again through the lab's own path.
    var damage = DamageCalibration.Measure(calibrated, combat, real);
    foreach (var byMove in damage.GroupBy(d => d.Move))
        Console.WriteLine($"  damage {byMove.Key}: {byMove.Count(d => d.Agrees)} of {byMove.Count()} recorded blows ask what the lab asks");
    foreach (var d in damage.Where(d => !d.Agrees)) Console.WriteLine($"    {d}");
    Check(damage.Count >= 20 && damage.Count(d => d.Agrees) >= damage.Count * 8 / 10 && damage.All(d => d.Agrees || d.Reason != "unexplained"),
        $"the lab's damage matches recorded blows ({damage.Count(d => d.Agrees)} of {damage.Count}), and every difference has a known reason");
    // Criticals (#10146): 2 × player_dexterity_{critical_hit + 1} percent, rolled on blows whose move may critical.
    {
        var labBuild = combat.Build!;
        var noviceRate = Rate(labBuild with { SkillLevels = new Dictionary<string, int> { ["critical_hit"] = 0 } });
        var expertRate = Rate(labBuild with { SkillLevels = new Dictionary<string, int> { ["critical_hit"] = 2 } });
        combat.Rebuild(labBuild);
        combat.Reset();
        Check(MathF.Abs(noviceRate.Chance - 2) < .01f && MathF.Abs(expertRate.Chance - 12) < .01f,
            FormattableString.Invariant($"the critical chance is 2 × player_dexterity at critical_hit + 1: {noviceRate.Chance:0.#}% for a new game, {expertRate.Chance:0.#}% at level 2"));
        Check(MathF.Abs(noviceRate.Rolled - 2) < .5f && MathF.Abs(expertRate.Rolled - 12) < .8f,
            FormattableString.Invariant($"and the lab's seeded roll lands at those rates over 20000 rolls: {noviceRate.Rolled:0.00}% and {expertRate.Rolled:0.00}% (the recordings: 1 of 45 and 13 of 111)"));
        var mayCritical = real.Moves.Select(m => (m.Clip, combat.Model.EventOf(new DarkMessiah.Combat.Party("player", "", [m.Clip], (m.Open + m.Close) / 2, "#1"))?.CanCritical == true)).ToList();
        Check(mayCritical.Where(m => m.Clip.Contains("fastattack")).All(m => !m.Item2) && mayCritical.Where(m => m.Clip.Contains("bigslash")).All(m => m.Item2),
            "power attacks may critical (critical_enable), fast attacks may not");
        (float Chance, float Rolled) Rate(CharacterBuild build)
        {
            combat.Rebuild(build);
            combat.Reset();
            int hits = 0;
            for (int i = 0; i < 20000; i++) hits += combat.RollCritical() ? 1 : 0;
            return (combat.CriticalChance, hits / 200f);
        }
    }
    // Slow motion (#10147): every recorded request is a Fatality kill's, the Whirlwind's or a map script's, as the lab asks.
    var slowTimes = SlowTimeCalibration.Measure(calibrated, combat.Model);
    foreach (var c in slowTimes) Console.WriteLine($"  slow motion: {c}");
    Check(slowTimes.Count(c => c.Cause == "Fatality kill") >= 3 && slowTimes.All(c => c.Agrees),
        $"the lab asks the donor's slow motion: on each Fatality kill and as the Whirlwind begins, none on other kills ({slowTimes.Count(c => c.Agrees)} of {slowTimes.Count})");
    // The blade's arcs against the donor's recorded sweeps (Wire v17, #10179).
    string[] sweepRuns = [.. new[] { "chained-slash", "fatality" }.Select(n => Path.Combine(recordings, "experiments", $"l01_a-v17-{n}.rbrec")).Where(File.Exists)];
    if (sweepRuns.Length > 0)
    {
        var sweeps = SweepCalibration.Measure(sweepRuns, combat.Tuning, real);
        foreach (var byClip in sweeps.GroupBy(s => s.Clip))
            Console.WriteLine(FormattableString.Invariant($"  sweeps {byClip.Key}: {byClip.Count(s => s.Off <= 10)} of {byClip.Count()} within 10° of the lab's arc (worst {byClip.Max(s => s.Off):0.#}°)"));
        foreach (var s in sweeps.Where(s => s.Off > 10).Take(8)) Console.WriteLine($"    {s}");
        Check(sweeps.Count >= 30 && sweeps.Count(s => s.Off <= 10) >= sweeps.Count * 8 / 10,
            $"the lab's swing arcs follow the donor's recorded sweeps within 10° ({sweeps.Count(s => s.Off <= 10)} of {sweeps.Count})");
    }
    else Console.WriteLine("skip the sweep calibration: no local/recordings/experiments/l01_a-v17-*.rbrec");
    var adrenaline = AdrenalineCalibration.Measure(calibrated, combat);
    foreach (var byMove in adrenaline.GroupBy(a => a.Move))
        Console.WriteLine($"  adrenaline {byMove.Key}: {byMove.Count(a => a.Agrees)} of {byMove.Count()} landed blows fill the gauge as the lab's does");
    foreach (var a in adrenaline.Where(a => !a.Agrees).Take(12)) Console.WriteLine($"    {a}");
    Check(adrenaline.Count >= 20 && adrenaline.Count(a => a.Agrees) >= adrenaline.Count * 9 / 10,
        $"each landed blow fills the gauge by its adrenalineadd ({adrenaline.Count(a => a.Agrees)} of {adrenaline.Count})");
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
            // Event-specific (review R10122-5): the 9-damage power attack at 16618 is compared, or excluded by name.
            bool counted = report.PlayerBlows.Matches.Any(m => m.Donor?.Tick == 16618)
                || report.Excluded?.Any(e => e.Tick == 16618 && e.ByPlayer && e.Class == "npc_orc_sword") == true;
            Check(CombatReplay.SwingAt(timeline.Swings, 16396) != CombatReplay.SwingAt(timeline.Swings, 16618) && counted,
                "the parried fast attack at tick 16396 and the power attack at 16618 are separate swings, and the later one is counted");
        }
        foreach (var line in report.Lines()) Console.WriteLine($"  {line}");
        Check(report.Ticks > 0 && report.Moves.Values.Sum(m => m.Lab) > 0, $"{report.Name}: its input drives the lab's sword");
        int donorMoves = report.Moves.Values.Sum(m => m.Donor), together = report.Moves.Values.Sum(m => m.Together);
        // A fight that used only the lab's own moves is held to them; one with the donor's variants (fast attack B
        // chained onto A, the directional power attacks) reports the gap the lab doesn't model yet.
        Check(together >= donorMoves * 8 / 10, $"{report.Name}: the lab begins the donor's moves with them ({together} of {donorMoves})");
        // #10142: the hand-played fights' fast attacks, which the lab once began far more of (21 against 13, 35 against 27).
        if (report.Name.Contains("arena-hand", StringComparison.Ordinal) && report.Moves.TryGetValue("fast attack", out var fastMoves))
            Check(fastMoves.Lab <= fastMoves.Donor + 2, $"{report.Name}: the lab begins no more fast attacks than the donor plus two ({fastMoves.Lab} against {fastMoves.Donor})");
        // From v14 each move is held to its own clip too: fast attack A or B, each power attack's direction.
        if (report.Kicks is not null && report.Clips is { } byClip)
        {
            int clipDonor = byClip.Values.Sum(c => c.Donor), clipTogether = byClip.Values.Sum(c => c.Together);
            Check(clipTogether >= clipDonor * 8 / 10, $"{report.Name}: the lab plays the donor's own clip with it ({clipTogether} of {clipDonor})");
        }
        if (report.Kicks is { } asked)
        {
            int agree = asked.Requests - asked.DonorBegan - asked.LabBegan + 2 * asked.Both;
            Check(agree >= asked.Requests * 8 / 10, $"{report.Name}: the lab kicks or refuses as the donor did on {agree} of {asked.Requests} requests");
        }
        Check(!combat.Orc.Following && combat.Log is null, $"{report.Name}: the lab's own fight comes back after the replay");
    }

    // A v15 recording replays with the donor player's build (#10140): l01_a-v15-stats starts with the combat skill's
    // level 0 and the short sword, then learns every skill, which the replay notes but doesn't follow.
    // The chained slash replayed (#10143): the recording's build has combat_chained_slash, so the lab chains A to E.
    if (File.Exists(chainedSlash))
    {
        var report = CombatReplay.Run(combat, chainedSlash, product);
        foreach (var line in report.Lines().Take(8)) Console.WriteLine($"  {line}");
        int donorMoves = report.Clips!.Values.Sum(c => c.Donor), together = report.Clips.Values.Sum(c => c.Together);
        Check(together >= donorMoves * 8 / 10 && "CDE".All(c => report.Clips.TryGetValue($"layer_fastattack_sword_noshield_{c}", out var m) && m.Lab > 0),
            $"{report.Name}: the lab chains C, D and E with the donor, each move as its own clip ({together} of {donorMoves})");
    }
    else Console.WriteLine("skip the chained-slash replay: no local/recordings/experiments/l01_a-v16-chained-slash.rbrec");

    // The jump and rush attacks replayed: the lab begins the donor's movement attacks with it.
    string Experiment(string name) => Path.Combine(recordings, "experiments", $"l01_a-v16-{name}.rbrec");
    foreach (var (path, families) in new[] { (Experiment("fatality"), new[] { "fatality", "power attack" }), (Experiment("whirlwind"), new[] { "whirlwind", "power attack" }),
        (jumpAttack, new[] { "power attack" }), (jumpHold, new[] { "jump attack", "power attack" }), (jumpBasic, new[] { "power attack" }), (rushAttack, new[] { "rush attack" }) })
    {
        if (!File.Exists(path)) { Console.WriteLine($"skip the {Path.GetFileName(path)} replay: no local/recordings/experiments/{Path.GetFileName(path)}"); continue; }
        var report = CombatReplay.Run(combat, path, product);
        // RB_ALL=1 prints each movement replay's whole report: the blows that differ, one by one.
        foreach (var line in (Environment.GetEnvironmentVariable("RB_ALL") is null ? report.Lines().Take(10).Concat(report.Lines().Where(l => l.Contains("adrenaline:"))) : report.Lines())) Console.WriteLine($"  {line}");
        // The followed orc carries the recording's kit and the blade traces the donor's arcs (#10177): in the fatality run,
        // with one orc and no shield, a third of the player's blows agree (none did before).
        if (Path.GetFileName(path) == "l01_a-v16-fatality.rbrec")
            Check(report.PlayerBlows.Count("agree") * 3 >= report.PlayerBlows.Donor,
                $"{report.Name}: the lab's blows agree with the donor's on a third or more ({report.PlayerBlows.Count("agree")} of {report.PlayerBlows.Donor})");
        foreach (var family in families)
        {
            var m = report.Moves.GetValueOrDefault(family);
            Check(m.Donor > 0 && m.Together * 10 >= m.Donor * 8 && m.Lab <= m.Donor + 2,
                $"{report.Name}: the lab begins the donor's {family}s with it ({m.Together} of {m.Donor}, lab {m.Lab})");
        }
    }

    // The other weapons' fights (#10148): their blows in each class's windows, at each weapon's speed, asking each
    // weapon's damage, and replayed with the recording's weapon; the switches replayed with their draws.
    string WeaponRun(string name) => Path.Combine(recordings, "experiments", $"l01_a-v17-weapon-{name}.rbrec");
    var weaponRuns = new[] { ("long-sword", LongSword), ("club", Club), ("daggers-1", Kryss), ("daggers", Kryss), ("staff-1", CombatStaff), ("staff", CombatStaff) }
        .Select(r => (Path: WeaponRun(r.Item1), Model: r.Item2)).Where(r => File.Exists(r.Path)).ToList();
    if (weaponRuns.Count == 0) Console.WriteLine("skip the weapons' fights: no local/recordings/experiments/l01_a-v17-weapon-*.rbrec");
    foreach (var group in weaponRuns.GroupBy(r => combat.ClipsFor(r.Model)!.Idle.Clip))
    {
        var set = combat.ClipsFor(group.First().Model)!;
        var measured = SwordCalibration.Measure(group.Select(r => r.Path), set);
        foreach (var move in measured.Where(m => m.Blows.Count > 0)) Console.WriteLine($"  {move}");
        foreach (var move in measured.Where(m => m.Blows.Count >= 10))
            Check(move.Inside >= move.Blows.Count * 8 / 10, $"{move.Clip}: recorded blows land inside the lab's window ({move.Inside} of {move.Blows.Count})");
        foreach (var move in measured.Where(m => float.IsFinite(m.DonorCyclesPerSecond) && m.Blows.Count > 0))
            Check(MathF.Abs(move.LabCyclesPerSecond / move.DonorCyclesPerSecond - 1) < .15f,
                FormattableString.Invariant($"{move.Clip}: the lab plays it at the donor's rate ({move.LabCyclesPerSecond:0.00} against {move.DonorCyclesPerSecond:0.00} cycles/s)"));
    }
    if (weaponRuns.Count > 0)
    {
        var weaponDamage = DamageCalibration.Measure(weaponRuns.Select(r => r.Path), combat, real);
        foreach (var byMove in weaponDamage.GroupBy(d => d.Move))
            Console.WriteLine($"  damage {byMove.Key}: {byMove.Count(d => d.Agrees)} of {byMove.Count()} recorded blows ask what the lab asks");
        foreach (var d in weaponDamage.Where(d => !d.Agrees).Take(20)) Console.WriteLine($"    {d}");
        Check(weaponDamage.Count >= 20 && weaponDamage.Count(d => d.Agrees) >= weaponDamage.Count * 8 / 10 && weaponDamage.All(d => d.Agrees || d.Reason != "unexplained"),
            $"each weapon's damage matches its recorded blows ({weaponDamage.Count(d => d.Agrees)} of {weaponDamage.Count}), and every difference has a known reason");
    }
    foreach (var (path, model) in weaponRuns)
    {
        var report = CombatReplay.Run(combat, path, product);
        foreach (var line in (Environment.GetEnvironmentVariable("RB_ALL") is null ? report.Lines().Take(8) : report.Lines())) Console.WriteLine($"  {line}");
        Check(report.Build?.Contains(model, StringComparison.Ordinal) == true, $"{report.Name}: the replay holds the recording's weapon ({model})");
        // The power attacks where the run held them (staff-1's 1.2 s holds were shorter than the staff's wind-up: its one
        // power attack is a jump's, pressed 0.31 s after a hop the approach made, which the replayed body doesn't make).
        foreach (var family in new[] { "fast attack", "power attack" })
        {
            var m = report.Moves.GetValueOrDefault(family);
            if (family == "power attack" && m.Donor < 5) continue;
            Check(m.Donor > 0 && m.Together * 10 >= m.Donor * 8,
                $"{report.Name}: the lab begins the donor's {family}s with it ({m.Together} of {m.Donor}, lab {m.Lab})");
        }
    }
    string switches = WeaponRun("switch");
    if (File.Exists(switches))
    {
        var report = CombatReplay.Run(combat, switches, product);
        foreach (var line in report.Lines().Take(10)) Console.WriteLine($"  {line}");
        foreach (var family in new[] { "put away", "draw" })
        {
            var m = report.Moves.GetValueOrDefault(family);
            Check(m.Donor >= 10 && m.Together * 10 >= m.Donor * 8 && m.Lab <= m.Donor + 2,
                $"{report.Name}: the lab's weapon is {(family == "draw" ? "drawn" : "put away")} with the donor's ({m.Together} of {m.Donor}, lab {m.Lab})");
        }
    }
    else Console.WriteLine("skip the switching replay: no local/recordings/experiments/l01_a-v17-weapon-switch.rbrec");

    string stats = Path.Combine(recordings, "experiments", "l01_a-v15-stats.rbrec");
    if (File.Exists(stats))
    {
        var report = CombatReplay.Run(combat, stats, product);
        Console.WriteLine($"  {report.Name}: build: {report.Build}");
        Check(report.Build is string b && b.StartsWith("l01_a-v15-stats.rbrec: 4 modules", StringComparison.Ordinal) && b.Contains("sword_short.mdl") && b.Contains("changed"),
            "a v15 recording's replay runs the recording's build, and says when it changed later");
        // Its later changes play from their ticks (R10140-1): every skill learned at once gives double_sprint, stamina 12.
        foreach (var (tick, modules, max) in report.Rebuilt ?? []) Console.WriteLine($"  rebuilt at tick {tick}: {modules} modules, stamina max {max}");
        Check(report.Rebuilt is { Count: > 0 } rebuilt && rebuilt.Any(r => r.MaxStamina == 12),
            $"the replay plays the recording's later builds from their ticks: double_sprint's stamina 12 from tick {report.Rebuilt?.FirstOrDefault(r => r.MaxStamina == 12).Tick}");
        // Gear the lab doesn't play is said so and left out (R10140-2): this recording's player took a shield.
        Check(report.Build is string held && held.Contains("not played: a shield", StringComparison.Ordinal)
            && report.NotCompared.Any(n => n.Contains("holding a shield", StringComparison.Ordinal)),
            $"a recorded shield isn't presented as played: the report names it and compares nothing while it's held ({report.NotCompared.FirstOrDefault(n => n.Contains("shield"))})");
        Check(combat.Build?.Source == BuildFile.Path, "after the replay the lab plays build.json again");
    }
    else Console.WriteLine("skip the v15 build replay: no local/recordings/experiments/l01_a-v15-stats.rbrec");
});
return failures == 0 ? 0 : 1;

// A recording's format version, from its first frame.
static uint RecordingVersion(string path)
{
    using var stream = File.OpenRead(path);
    return RustyBridge.Wire.RecordingReader.Frames(stream).FirstOrDefault()?.Version ?? 0;
}
