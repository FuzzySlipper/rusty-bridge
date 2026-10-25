using System.Numerics;
using System.Text;
using DarkMessiah.Combat;
using DarkMessiah.Lab;
using DarkMessiah.Lab.Calibration;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Replay;
using DarkMessiah.Lab.Sword;
using Rusty.Engine;
using Rusty.Engine.Testing;
using RustyBridge.Wire;

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
var tuning = new CombatTuning(.5f, [.36f, .42f], 1.4f, 1.5f, phases, new OrcTuning([0, 0, 0], 3, 61, []));
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
while (k.Cycle < .75f) k.Step(Tick, false, false, false);
k.Step(Tick, true, false, false);
k.Step(Tick, false, false, false);
Check(k.Move == SwordMove.Kick, "on the ground a tap late in the kick does nothing either");
var air = new PlayerSword(clips) { FastDelay = 0 };
air.Step(Tick, false, false, true);
while (air.Cycle < .6f) air.Step(Tick, false, false, false, airborne: true);
for (int i = 0; i < 12; i++) air.Step(Tick, true, false, false, airborne: true);
air.Step(Tick, false, false, false, airborne: true);
bool powered = air.Move == SwordMove.Power && air.Clip.Clip == "power";
var airTap = new PlayerSword(clips) { FastDelay = 0 };
airTap.Step(Tick, false, false, true);
while (airTap.Cycle < .6f) airTap.Step(Tick, false, false, false, airborne: true);
for (int i = 0; i < 4; i++) airTap.Step(Tick, i < 3, false, false, airborne: true);
for (int i = 0; i < 12; i++) airTap.Step(Tick, false, false, false, airborne: true);
Check(powered && airTap.Move == SwordMove.Kick,
    "in the air, attack held mid-kick waits as any press in a jump and releases the neutral power attack, cutting the kick; a 3-tick tap is dropped (R10142-1: 8 of 8, 4 of 4)");
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

// Stamina: Dark Messiah's numbers, a kick's threshold and cost, sprint's drain and the regeneration (#10160: it regains
// while sprinting, every 9 game ticks, and a kick into the air costs nothing).
var stamina = new PlayerStamina(StaminaRules.Shipped);
// The regeneration's interval: the first 15 ms game tick past 0.12 s.
const float RegenEvery = .135f;
for (int i = 0; i < 60; i++) stamina.Step(Tick, sprintAsked: true);
float sprinted = 60 * Tick;
Check(MathF.Abs(stamina.Value - (9 - 2 * sprinted + MathF.Floor(sprinted / RegenEvery) * .06f)) < .07f,
    FormattableString.Invariant($"sprinting drains 2 a second less the regeneration: 9 → {stamina.Value:0.00} in {sprinted:0.00} s (the donor's 1.56 a second)"));
stamina = new PlayerStamina(StaminaRules.Shipped);
stamina.Kicked(npc: true);
stamina.Kicked(npc: false);
Check(stamina.Value == 6 && stamina.MayKick, "a kick on an NPC costs 3, one into the air nothing");
stamina.Kicked(npc: true);
Check(stamina.Value == 3 && stamina.MayKick, "3 left may still kick");
stamina.Kicked(npc: true);
Check(!stamina.MayKick, "under 3 it may not");
for (int i = 0; i < 120; i++) stamina.Step(Tick, sprintAsked: false);
Check(MathF.Abs(stamina.Value - MathF.Floor(120 * Tick / RegenEvery) * .06f) < .07f,
    FormattableString.Invariant($"it regains 0.06 every 0.135 s (9 game ticks): {stamina.Value:0.00} after {120 * Tick:0.00} s"));
// A sprint starts only with 3 (#10144), and once going runs on below it.
stamina.Step(Tick, sprintAsked: true);
Check(!stamina.Sprinting, FormattableString.Invariant($"with {stamina.Value:0.00} a sprint doesn't start"));
for (int i = 0; i < 600 && stamina.Value < 3; i++) stamina.Step(Tick, sprintAsked: false);
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

// RB_ITEMS=path prints when item_* entities appear and go (#10162) and stops.
if (Environment.GetEnvironmentVariable("RB_ITEMS") is { Length: > 0 } itemsPath)
{
    var seen = new Dictionary<int, string>();
    using var stream = File.OpenRead(itemsPath);
    foreach (var f in RecordingReader.Frames(stream))
    {
        var now = new Dictionary<int, string>();
        foreach (var (index, e) in f.Entities)
            if (f.Classes.GetValueOrDefault(e.ClassId) is { } c && (c.StartsWith("item_", StringComparison.Ordinal) || c.Contains("potion", StringComparison.Ordinal)))
                now[index] = c;
        foreach (var (i, c) in now) if (!seen.ContainsKey(i)) Console.WriteLine($"ITEM {f.Tick} + {c} #{i}");
        foreach (var (i, c) in seen) if (!now.ContainsKey(i)) Console.WriteLine($"ITEM {f.Tick} - {c} #{i}");
        seen = now;
    }
    return 0;
}

// RB_LEAN=path prints the drawn camera against the eye, in the view's frame, every tick (R10161-2) and stops.
if (Environment.GetEnvironmentVariable("RB_LEAN") is { Length: > 0 } leanPath)
{
    using var stream = File.OpenRead(leanPath);
    foreach (var f in RecordingReader.Frames(stream))
    {
        var v = f.ClientView;
        if (!v.Flags.HasFlag(ClientViewFlags.Camera)) continue;
        // Source axes: the view's forward, left and up from its pitch and yaw.
        float yaw = v.ViewYaw * MathF.PI / 180, pitch = v.ViewPitch * MathF.PI / 180;
        var forward = new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        var up = Vector3.Cross(forward, left);
        var d = new Vector3(v.CameraX - v.OriginX - v.ViewOffsetX, v.CameraY - v.OriginY - v.ViewOffsetY, v.CameraZ - v.OriginZ - v.ViewOffsetZ);
        Console.WriteLine(FormattableString.Invariant($"LEAN {f.Tick} fwd {Vector3.Dot(d, forward):0.0} left {Vector3.Dot(d, left):0.0} up {Vector3.Dot(d, up):0.0} roll {v.CameraRoll - v.ViewRoll:0.0} viewroll {v.ViewRoll:0.0} vo {v.ViewOffsetX:0.0},{v.ViewOffsetY:0.0},{v.ViewOffsetZ:0.0} cam {v.CameraX - v.OriginX:0.0},{v.CameraY - v.OriginY:0.0},{v.CameraZ - v.OriginZ:0.0} pos {v.OriginX:0} {v.OriginY:0} {v.OriginZ:0} yaw {v.ViewYaw:0}"));
    }
    return 0;
}

// RB_PROPS=path prints where a recording's props stand (crates, tables, barrels: their model and origin) and stops (#10163).
if (Environment.GetEnvironmentVariable("RB_PROPS") is { Length: > 0 } propsPath)
{
    using var stream = File.OpenRead(propsPath);
    foreach (var f in RecordingReader.Frames(stream))
    {
        foreach (var (index, e) in f.Entities)
            if (f.Models.GetValueOrDefault(e.ModelIndex) is { } model && (model.Contains("crate", StringComparison.OrdinalIgnoreCase)
                || model.Contains("table", StringComparison.OrdinalIgnoreCase) || model.Contains("barrel", StringComparison.OrdinalIgnoreCase)
                || model.Contains("box", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine(FormattableString.Invariant($"PROP {f.Tick} #{index} {model} {e.OriginX:0} {e.OriginY:0} {e.OriginZ:0} yaw {e.Yaw:0}"));
        if (Environment.GetEnvironmentVariable("RB_PROPS_ALL") != "1") break;
    }
    return 0;
}

// RB_HEALTH=1 prints the donor player's health through each recording (#10162) and stops.
if (Environment.GetEnvironmentVariable("RB_HEALTH") == "1")
{
    foreach (var path in Directory.GetFiles(Path.Combine(repo, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories).Order())
    {
        float? last = null, max = null;
        int rises = 0, falls = 0, skill = -1, metabolism = -1;
        var events = new List<string>();
        try
        {
            using var stream = File.OpenRead(path);
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Version >= 16 && f.Stats.Valid != 0) { skill = f.Stats.SkillLevel("extra_health"); metabolism = f.Stats.SkillLevel("fast_metabolism"); }
                if (f.Player.Index == WireLayout.NoEntity || !f.Entities.TryGetValue(f.Player.Index, out var me) || !me.Flags.HasFlag(WireEntityFlags.HasHealth)) continue;
                if (max != me.MaxHealth) events.Add(FormattableString.Invariant($"max {me.MaxHealth:0.##}@{f.Tick}"));
                max = me.MaxHealth;
                if (last is float was && me.Health != was)
                {
                    if (me.Health > was) { rises++; if (events.Count < 60) events.Add(FormattableString.Invariant($"+{me.Health - was:0.###}@{f.Tick}")); }
                    else { falls++; if (events.Count < 60) events.Add(FormattableString.Invariant($"-{was - me.Health:0.###}@{f.Tick}")); }
                }
                last = me.Health;
            }
        }
        catch (Exception e) { events.Add("error " + e.Message); }
        if (max is null) continue;
        Console.WriteLine(FormattableString.Invariant($"HEALTH {Path.GetFileName(path)} extra_health {skill} fast_metabolism {metabolism} max {max} rises {rises} falls {falls}: {string.Join(" ", events)}"));
    }
    return 0;
}

// RB_ARCHERY=1 prints the donor's archers through the npc-archery recordings (#10169) and stops.
if (Environment.GetEnvironmentVariable("RB_ARCHERY") == "1")
{
    foreach (var path in Directory.GetFiles(Path.Combine(repo, "local", "recordings"), "*npc-archery*.rbrec", SearchOption.AllDirectories).Order())
        foreach (var fight in NpcArcheryCalibration.Measure(path))
        {
            Console.WriteLine($"{Path.GetFileName(path)} {fight}");
            foreach (var a in fight.Arrows) Console.WriteLine($"    {a}");
            foreach (var b in fight.Blows.Take(4)) Console.WriteLine(FormattableString.Invariant($"    after: {b.Clip} at {b.Cycle:0.00} asked {b.Asked:0.##} ({b.Weapon})"));
        }
    return 0;
}

// RB_MANA=1 prints the donor player's mana through each recording (#10164): its maximum by the skills, and how it rises.
if (Environment.GetEnvironmentVariable("RB_MANA") == "1")
{
    foreach (var path in Directory.GetFiles(Path.Combine(repo, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories).Order())
    {
        float? last = null, max = null;
        int extra = -1, regen = -1, recovery = -1, lastTick = 0;
        var rises = new List<(float By, int Ticks)>();
        var events = new List<string>();
        try
        {
            using var stream = File.OpenRead(path);
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Version < 15 || f.Stats.Valid == 0) continue;
                if (f.Version >= 16) { extra = f.Stats.SkillLevel("extra_mana"); regen = f.Stats.SkillLevel("mana_regeneration"); }
                recovery = f.Stats.Modules.HasFlag(SkillModules.ManaRecovery) ? 1 : 0;
                if (max != f.Stats.MaxMana) events.Add(FormattableString.Invariant($"max {f.Stats.MaxMana:0.##}@{f.Tick} (extra_mana {extra})"));
                max = f.Stats.MaxMana;
                if (last is float was && f.Stats.Mana != was)
                {
                    if (f.Stats.Mana > was) { rises.Add((f.Stats.Mana - was, f.Tick - lastTick)); lastTick = f.Tick; }
                    else if (events.Count < 40) events.Add(FormattableString.Invariant($"-{was - f.Stats.Mana:0.##}@{f.Tick}"));
                }
                last = f.Stats.Mana;
            }
        }
        catch (Exception e) { events.Add("error " + e.Message); }
        if (max is null) continue;
        string steps = string.Join(" ", rises.GroupBy(r => (MathF.Round(r.By, 2), r.Ticks)).OrderByDescending(g => g.Count()).Take(4).Select(g => FormattableString.Invariant($"+{g.Key.Item1}/{g.Key.Item2}t×{g.Count()}")));
        Console.WriteLine(FormattableString.Invariant($"MANA {Path.GetFileName(path)} extra_mana {extra} mana_regeneration {regen} mana_recovery {recovery} max {max} rises {steps}: {string.Join(" ", events.Take(12))}"));
    }
    return 0;
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
    // RB_REPLAY=path replays one recording and prints its whole report, for looking into one replay.
    // RB_REACT=1 prints the donor's hit reactions (#10153) and stops.
    if (Environment.GetEnvironmentVariable("RB_REACT") == "1")
    {
        var reactions = ReactionCalibration.Measure(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories));
        foreach (var r in reactions)
            Console.WriteLine(FormattableString.Invariant($"REACT {r.Recording} {r.Tick} {r.Move} dmg {r.Damage:0.##} grp {r.HitGroup} push {r.PushForce} flag {r.DamageFlag:x} {r.Side} {r.Angle:0} hp {r.HealthFraction:0.00} before {r.Before}@{r.BeforeCycle:0.00} -> {r.Reaction} +{r.Delay} ovl {r.Overlay} facing {r.Facing:0} crit {(r.Critical ? 1 : 0)} z {r.HitHeight:0} was {r.OverlayBefore}"));
        return;
    }
    // RB_KNOCK=1 prints the donor's stuns and knockdowns (#10152) and stops.
    if (Environment.GetEnvironmentVariable("RB_KNOCK") == "1")
    {
        var (blows, spans) = KnockdownCalibration.Measure(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories), product.Combat.Model);
        foreach (var byMove in blows.GroupBy(b => (b.Move, Stun: b.Event?.ChanceToStun ?? -1, Fall: b.Event?.ChanceToFall ?? -1)).OrderByDescending(x => x.Count()))
            Console.WriteLine(FormattableString.Invariant($"KNOCK {byMove.Key.Move} stun {byMove.Key.Stun} fall {byMove.Key.Fall}: {byMove.Count()} blows, {byMove.Count(b => b.Outcome == BlowOutcome.Fall)} falls, {byMove.Count(b => b.Outcome == BlowOutcome.Stun)} stuns"));
        foreach (var b in blows.Where(b => b.Outcome != BlowOutcome.None)) Console.WriteLine(FormattableString.Invariant($"KNOCKED {b.Recording} {b.Tick} {b.Move} {b.Outcome} dmg {b.Damage:0.#} before {b.VictimBefore} then {b.Then} hits {b.HitsBefore} ev stun {b.Event?.ChanceToStun} fall {b.Event?.ChanceToFall}"));
        foreach (var sp in spans) Console.WriteLine(FormattableString.Invariant($"SPAN {sp.Recording} {sp.Tick} {sp.Kind} {sp.First} -> {sp.Last} {sp.Seconds:0.00} s"));
        // The decoded stun rule's precondition: six hits within ten seconds (server.dll+0x2AB085).
        foreach (var byWindow in blows.Where(b => !b.Kick).GroupBy(b => (Weapon: b.Move.Contains("staff") ? "staff" : b.Move.Contains("aggers") ? "daggers" : "sword",
                     Six: blows.Count(o => o.Recording == b.Recording && o.Tick <= b.Tick && o.Tick > b.Tick - 10 * 66 && o.Victim == b.Victim) >= 6)))
            Console.WriteLine($"WINDOW {byWindow.Key.Weapon} six-in-ten {byWindow.Key.Six}: {byWindow.Count()} blows, {byWindow.Count(b => b.Outcome == BlowOutcome.Stun)} stuns, mean chance {byWindow.Average(b => b.Event?.ChanceToStun ?? 0):0.00}");
        return;
    }
    // RB_MOVE=1 prints the donor's movement by command (#10159) and stops.
    // RB_MOVE=NAME takes only the recordings whose name has NAME.
    if (Environment.GetEnvironmentVariable("RB_MOVE") is { Length: > 0 } moveFilter)
    {
        var m = MovementCalibration.Measure(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories)
            .Where(f => moveFilter == "1" || Path.GetFileName(f).Contains(moveFilter, StringComparison.Ordinal)));
        static string Q(IReadOnlyList<float> v) => v.Count == 0 ? "none" : FormattableString.Invariant($"n {v.Count} p10 {v.Order().ElementAt(v.Count / 10):0.0} median {MovementMeasure.Median(v):0.0} p90 {v.Order().ElementAt(v.Count * 9 / 10):0.0}");
        foreach (var (c, v) in m.Steady.OrderByDescending(x => x.Value.Count)) Console.WriteLine($"MOVE steady {c}: {Q(v)}");
        foreach (var (c, v) in m.Accelerations.OrderByDescending(x => x.Value.Count)) Console.WriteLine($"MOVE accel {c}: {Q(v)}");
        Console.WriteLine($"MOVE stops: seconds {Q(m.Stops.Select(x => x.Seconds).ToList())}; slide {Q(m.Stops.Select(x => x.Slide).ToList())}; from {Q(m.Stops.Select(x => x.From).ToList())}");
        Console.WriteLine($"MOVE jumps: rise {Q(m.Jumps.Select(x => x.Rise).ToList())}; air {Q(m.Jumps.Select(x => x.Air).ToList())}");
        foreach (var jumpFile in Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories))
        {
            var one = MovementCalibration.Measure([jumpFile]);
            if (one.Jumps.Count > 0) Console.WriteLine(FormattableString.Invariant($"MOVEJ {Path.GetFileName(jumpFile)}: {string.Join(" ", one.Jumps.Select(j => $"{j.Rise:0}"))}"));
        }
        foreach (var j in m.Jumps.OrderBy(j => j.Speed)) Console.WriteLine(FormattableString.Invariant($"MOVE jump rise {j.Rise:0.0} air {j.Air:0.00} speed {j.Speed:0} held {j.Held}"));
        Console.WriteLine($"MOVE lab ground {product.Player.Config.Ground}; air {product.Player.Config.Air}; vertical {product.Player.Config.Vertical}");
        return;
    }
    // RB_CAMERA=1 prints the arms' eyes attachment through the first-person clips (#10161) and stops.
    if (Environment.GetEnvironmentVariable("RB_CAMERA") == "1")
    {
        // The head's own model (#10161): its one moving node through each clip.
        var headModel = product.Donor.Model("models/npc/player/player_head_mvt.mdl");
        using (var head = new Puppet(engine, headModel, 0x4C41_9000))
        {
            Console.WriteLine($"HEAD joints {string.Join(", ", headModel.Joints.Keys)}"); int node = headModel.Joints.Values.Max();
            Transform At(string clip, float c)
            {
                head.Play(clip, c);
                return Rig.Pose(engine, head, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One))[node].World;
            }
            var restHead = At("idle", 0);
            foreach (var clip in headModel.Info.Clips.Keys.Order())
            {
                var row = new List<string>();
                for (float c = 0; c <= 1.001f; c += .1f)
                {
                    var e = At(clip, c);
                    var d = (e.Translation - restHead.Translation) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
                    var turn = Quaternion.Inverse(restHead.Rotation) * e.Rotation;
                    var f = Vector3.Transform(Vector3.UnitZ, turn);
                    var u = Vector3.Transform(Vector3.UnitY, turn);
                    row.Add(FormattableString.Invariant($"{c:0.0}:{d.X:0.0},{d.Y:0.0},{d.Z:0.0}/{MathF.Asin(Math.Clamp(-f.Y, -1, 1)) * 180 / MathF.PI:0.0},{MathF.Atan2(f.X, f.Z) * 180 / MathF.PI:0.0},{MathF.Atan2(-u.X, u.Y) * 180 / MathF.PI:0.0}"));
                }
                Console.WriteLine($"HEAD {clip} {headModel.Clip(clip).Seconds:0.00}s {string.Join(" ", row)}");
            }
        }
        var fp = product.Combat.FirstPerson;
        var look = Vector3.UnitX;
        var info = fp.ArmsModel.Info;
        var rest = fp.EyesAt("anm_reference", 0, look) ?? default;
        foreach (var clip in info.Clips.Keys.Where(n => n.Contains("jump") || n.Contains("fastattack") || n.Contains("bigslash") || n.Contains("kick") || n.Contains("combo") || n.Contains("idle") || n.Contains("land")).Order())
        {
            var row = new List<string>();
            for (float c = 0; c <= 1.001f; c += .1f)
                if (fp.EyesAt(clip, c, look) is { } e)
                {
                    var d = e.Translation - rest.Translation;
                    var turn = Quaternion.Inverse(rest.Rotation) * e.Rotation;
                    var f = Vector3.Transform(Vector3.UnitZ, turn);
                    float pitch = MathF.Asin(Math.Clamp(-f.Y, -1, 1)) * 180 / MathF.PI, yaw = MathF.Atan2(f.X, f.Z) * 180 / MathF.PI;
                    row.Add(FormattableString.Invariant($"{c:0.0}:{d.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit:0.0},{d.Y / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit:0.0},{d.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit:0.0}/{pitch:0.0},{yaw:0.0}"));
                }
            Console.WriteLine($"EYES {clip} {string.Join(" ", row)}");
        }
        return;
    }
    // RB_MOVEFIT=1 runs the lab's body through a synthetic start and stop for a grid of ground settings (#10159) and stops.
    if (Environment.GetEnvironmentVariable("RB_MOVEFIT") == "1")
    {
        IReplayBody fitBody = product;
        var baseTuning = product.Player.Tuning;
        var run = new MoveCommand("forward", 1000, "none");
        MovementMeasure Drive(PlayerTuning t)
        {
            product.Player.Retune(t);
            fitBody.Begin();
            var samples = new List<MovementSample>();
            double time = 0;
            for (int cycle = 0; cycle < 3; cycle++)
                foreach (var (seconds, move) in new[] { (1.0, Vector2.Zero), (2.0, Vector2.UnitY), (2.0, Vector2.Zero) })
                    for (double end = time + seconds; time < end; time += Tick)
                    {
                        // Turned about each cycle, so the arena's walls don't stop it.
                        var look = cycle % 2 == 0 ? Vector3.UnitX : -Vector3.UnitX;
                        var (feet, _, _, _) = fitBody.Step(move, look, false, false, false, false, Tick);
                        samples.Add(new MovementSample(time, feet.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, feet.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit,
                            feet.Y / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, move == Vector2.Zero ? null : run, false));
                    }
            fitBody.End();
            var m = MovementMeasure.Empty();
            MovementCalibration.Analyze(samples, m);
            return m;
        }
        void Show(string label, MovementMeasure m) => Console.WriteLine(FormattableString.Invariant(
            $"FIT {label}: steady {MovementMeasure.Median(m.Steady.GetValueOrDefault(run) ?? []):0.0}, accel {MovementMeasure.Median(m.Accelerations.GetValueOrDefault(run) ?? []):0.00} s, stop {MovementMeasure.Median(m.Stops.Select(x => x.Seconds).ToList()):0.00} s, slide {MovementMeasure.Median(m.Stops.Select(x => x.Slide).ToList()):0.0}"));
        Show("player.json", Drive(baseTuning));
        {
            // A straight sprint: its steady speed.
            fitBody.Begin();
            var sprintSamples = new List<MovementSample>();
            var sprintRun = new MoveCommand("forward", 1000, "sprint");
            for (double time = 0; time < 6; time += Tick)
            {
                var (feet, _, _, _) = fitBody.Step(Vector2.UnitY, Vector3.UnitX, false, false, false, false, Tick, sprint: true);
                sprintSamples.Add(new MovementSample(time, feet.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, feet.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, 0, sprintRun, false));
            }
            fitBody.End();
            var sm = MovementMeasure.Empty();
            MovementCalibration.Analyze(sprintSamples, sm);
            Console.WriteLine(FormattableString.Invariant($"FIT straight sprint: steady {MovementMeasure.Median(sm.Steady.GetValueOrDefault(sprintRun) ?? []):0}"));
            for (int k = 0; k + 13 < sprintSamples.Count; k += 26)
                Console.WriteLine(FormattableString.Invariant($"FIT sprint at {sprintSamples[k].Time:0.0} s: {MathF.Sqrt(MathF.Pow(sprintSamples[k + 13].X - sprintSamples[k].X, 2) + MathF.Pow(sprintSamples[k + 13].Y - sprintSamples[k].Y, 2)) / (13 * Tick):0}"));
        }
        foreach (float acc in new[] { 4.3f, 4.5f, 4.7f }) Show(FormattableString.Invariant($"acceleration {acc}"), Drive(baseTuning with { Acceleration = acc }));
        // RB_JUMPFIT=speeds;gravities;helds;releases fits the jump on the lab's own body (R10159-2): each combination's
        // rise and time in the air by hold, measured as the replay measures them, against l01_a-v18-jump-hold's
        // (the measure of R10159-2: to the first tick back within 4 units of the take-off).
        if (Environment.GetEnvironmentVariable("RB_JUMPFIT") is { Length: > 0 } jumpGrid)
        {
            float[] Values(string list) => list.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var axes = jumpGrid.Split(';');
            var donorJumps = new (float Hold, float Rise, float Air)[] { (.05f, 26.2f, .615f), (.1f, 28.6f, .66f), (.15f, 33.1f, .72f), (.2f, 35.7f, .755f), (.25f, 38.5f, .80f),
                (.3f, 41.1f, .84f), (.4f, 41.9f, .85f), (.7f, 41.9f, .84f) };
            foreach (float speed in Values(axes[0])) foreach (float gravity in Values(axes[1])) foreach (float held in Values(axes[2])) foreach (float release in Values(axes[3]))
            {
                product.Player.Retune(baseTuning with { JumpSpeed = speed, Gravity = gravity, JumpHeldGravity = held, JumpReleaseGravity = release });
                float worstRise = 0, worstAir = 0;
                var row = new List<string>();
                foreach (var (hold, rise, air) in donorJumps)
                {
                    fitBody.Begin();
                    var samples = new List<MovementSample>();
                    for (int i = 0; i < 30; i++)
                    {
                        var (still, _, _, _) = fitBody.Step(Vector2.Zero, Vector3.UnitX, false, false, false, false, Tick);
                        samples.Add(new MovementSample((i - 30) * Tick, still.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, still.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit,
                            still.Y / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, null, false));
                    }
                    for (float t = 0; t < 2f; t += Tick)
                    {
                        var (feet, _, _, _) = fitBody.Step(Vector2.Zero, Vector3.UnitX, t == 0, t < hold, false, false, Tick);
                        samples.Add(new MovementSample(t, feet.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, feet.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit,
                            feet.Y / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, null, t == 0));
                    }
                    fitBody.End();
                    var fitJumps = MovementCalibration.Find(samples).Jumps;
                    if (fitJumps.Count == 0) { worstRise = worstAir = 99; row.Add("none"); continue; }
                    worstRise = MathF.Max(worstRise, MathF.Abs(fitJumps[0].Rise - rise) / rise);
                    worstAir = MathF.Max(worstAir, MathF.Abs(fitJumps[0].Air - air));
                    row.Add(FormattableString.Invariant($"{fitJumps[0].Rise:0.0}/{fitJumps[0].Air:0.00}"));
                }
                Console.WriteLine(FormattableString.Invariant($"JUMPFIT speed {speed} gravity {gravity} held {held} release {release}: worst rise {worstRise * 100:0}% air {worstAir:0.00} s | {string.Join(" ", row)}"));
            }
        }
        // Crouched: the start to 90% and the steady speed by crouchAcceleration and crouchSpeed (R10159-2: the donor's
        // 0.59 s and 50 units/s).
        if (Environment.GetEnvironmentVariable("RB_MOVEFIT_CROUCH") is { Length: > 0 } crouchGrid)
            foreach (var pair in crouchGrid.Split(';'))
            {
                var parts = pair.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                bool backing = parts.Length > 2 && parts[2] > 0;
                product.Player.Retune(backing ? baseTuning with { BackAcceleration = parts[0] } : baseTuning with { CrouchAcceleration = parts[0], CrouchSpeed = parts[1] });
                fitBody.Begin();
                var crouchSamples = new List<MovementSample>();
                var crouchRun = backing ? new MoveCommand("back", 150, "none") : new MoveCommand("forward", 1000, "crouched");
                for (double time = 0; time < 3; time += Tick)
                {
                    var move = time < .5 ? Vector2.Zero : backing ? -Vector2.UnitY : Vector2.UnitY;
                    var (feet, _, _, _) = fitBody.Step(move, Vector3.UnitX, false, false, !backing, false, Tick);
                    crouchSamples.Add(new MovementSample(time, feet.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, feet.Z / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, 0,
                        move == Vector2.Zero ? null : crouchRun, false));
                }
                fitBody.End();
                var cm = MovementMeasure.Empty();
                MovementCalibration.Analyze(crouchSamples, cm);
                Console.WriteLine(FormattableString.Invariant($"FIT crouch acceleration {parts[0]} speed {parts[1]}: steady {MovementMeasure.Median(cm.Steady.GetValueOrDefault(crouchRun) ?? []):0.0}, start {MovementMeasure.Median(cm.Accelerations.GetValueOrDefault(crouchRun) ?? []):0.00} s"));
            }
        // Jumps by how long jump is held, against l01_a-v18-jump-hold (34, 36, 39, 39, 41, 41, 41, 41 units).
        foreach (float release in (Environment.GetEnvironmentVariable("RB_MOVEFIT_RELEASE") ?? "2,2.2,2.4").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)))
        {
            product.Player.Retune(baseTuning with { JumpReleaseGravity = release, JumpSpeed = 4.6f, Gravity = 10.3f });
            var rises = new List<string>();
            foreach (float hold in new[] { .05f, .1f, .15f, .2f, .25f, .3f, .4f, .5f, .7f, 1f })
            {
                fitBody.Begin();
                for (int i = 0; i < 30; i++) fitBody.Step(Vector2.Zero, Vector3.UnitX, false, false, false, false, Tick);
                float ground = product.Player.Feet.Y, peak = ground;
                for (float t = 0; t < 1.6f; t += Tick)
                {
                    var (feet, _, _, _) = fitBody.Step(Vector2.Zero, Vector3.UnitX, t == 0, t < hold, false, false, Tick);
                    peak = MathF.Max(peak, feet.Y);
                }
                fitBody.End();
                rises.Add(FormattableString.Invariant($"{(peak - ground) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit:0}"));
            }
            Console.WriteLine(FormattableString.Invariant($"FIT jumpReleaseGravity {release}: held 0.15–1 s rises {string.Join(" ", rises)}"));
        }
        product.Player.Retune(baseTuning);
        foreach (var name in new[] { "l01_a-v18-jump-hold.rbrec", "l01_a-v18-movement.rbrec", "l01_a-v18-sprint.rbrec", "l01_a-v18-sprint-double.rbrec" })
        {
            var path = Path.Combine(repo!, "local", "recordings", "experiments", name);
            if (!File.Exists(path)) continue;
            var fitReport = CombatReplay.Run(product.Combat, path, product);
            Console.WriteLine($"FIT {name} stamina {fitReport.Stamina}");
            if (fitReport.Body is not { Donor: { } d, Lab: { } l }) continue;
            var dm = MovementMeasure.Empty(); var lm = MovementMeasure.Empty();
            MovementCalibration.Analyze(d, dm); MovementCalibration.Analyze(l, lm);
            Console.WriteLine(FormattableString.Invariant($"FIT {name} donor jumps {string.Join(" ", dm.Jumps.Select(j => $"{j.Rise:0}/{j.Air:0.00}"))}"));
            Console.WriteLine(FormattableString.Invariant($"FIT {name} lab   jumps {string.Join(" ", lm.Jumps.Select(j => $"{j.Rise:0}/{j.Air:0.00}"))}"));
            foreach (var (c, v) in dm.Steady.Where(x => x.Value.Count >= 30)) Console.WriteLine(FormattableString.Invariant($"FIT {name} {c}: donor {MovementMeasure.Median(v):0}, lab {MovementMeasure.Median(lm.Steady.GetValueOrDefault(c) ?? []):0}"));
            Console.WriteLine(FormattableString.Invariant($"FIT {name} accel donor {string.Join(" ", dm.Accelerations.SelectMany(x => x.Value).Select(v => $"{v:0.00}"))}; lab {string.Join(" ", lm.Accelerations.SelectMany(x => x.Value).Select(v => $"{v:0.00}"))}"));
            Console.WriteLine(FormattableString.Invariant($"FIT {name} stops donor {string.Join(" ", dm.Stops.Select(v => $"{v.Seconds:0.00}/{v.Slide:0}"))}; lab {string.Join(" ", lm.Stops.Select(v => $"{v.Seconds:0.00}/{v.Slide:0}"))}"));
        }
        return;
    }
    // RB_MOVECASES=a,b replays the recordings whose names hold a or b and prints their movement cases (R10159-2).
    if (Environment.GetEnvironmentVariable("RB_MOVECASES") is { Length: > 0 } caseFilter)
    {
        var names = caseFilter.Split(',');
        foreach (var path in Directory.GetFiles(Path.Combine(repo, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories).Order()
                     .Where(p => names.Any(n => Path.GetFileName(p).Contains(n, StringComparison.Ordinal))))
        {
            var moved = CombatReplay.Run(product.Combat, path, product);
            if (moved.Body is not { Donor: { } d, Lab: { } l }) continue;
            foreach (var c in MovementCalibration.Compare(moved.Name, d, l)) Console.WriteLine($"CASE {c}");
            if (Environment.GetEnvironmentVariable("RB_MOVETRACE") is { Length: > 0 } window && window.Split('-') is [var fromText, var toText])
                for (int i = 0; i < d.Count; i++)
                    if (d[i].Time >= double.Parse(fromText, System.Globalization.CultureInfo.InvariantCulture) && d[i].Time <= double.Parse(toText, System.Globalization.CultureInfo.InvariantCulture))
                        Console.WriteLine(FormattableString.Invariant($"MT {d[i].Time:0.000} {d[i].Command} jump {d[i].JumpPressed} donor {d[i].X:0.0},{d[i].Y:0.0},{d[i].Z:0.0} lab {l[i].X:0.0},{l[i].Y:0.0},{l[i].Z:0.0} blocked {l[i].Blocked}"));
        }
        return;
    }
    // RB_SPELL=path:class prints each spell projectile of that class (spell_fire_3, the fireball; #10165).
    if (Environment.GetEnvironmentVariable("RB_SPELL") is { Length: > 0 } spellArg && spellArg.LastIndexOf(':') is int colon and > 0)
    {
        foreach (var shot in SpellCalibration.Measure(spellArg[..colon], spellArg[(colon + 1)..])) Console.WriteLine($"SPELL {shot}");
        return;
    }
    // RB_TK=path prints each telekinesis press: what it gripped, its mana, hold and throw (#10164).
    if (Environment.GetEnvironmentVariable("RB_TK") is { Length: > 0 } tkPath)
    {
        foreach (var grab in TelekinesisCalibration.Measure(tkPath)) Console.WriteLine($"TK {grab}");
        return;
    }
    // RB_FALL=path prints each fall's drop, landing speed and the health the landing took (R10159-1).
    if (Environment.GetEnvironmentVariable("RB_FALL") is { Length: > 0 } fallPath)
    {
        var tl = InputTimeline.Read(fallPath, [], "");
        var ft = tl.Ticks;
        float U(float metres) => metres / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        for (int i = 1; i < ft.Count; i++)
        {
            float v = U(ft[i].Feet.Y - ft[i - 1].Feet.Y) / MathF.Max(.015f, (float)(ft[i].Time - ft[i - 1].Time));
            if (v > -150) continue;
            int top = i - 1;
            while (top > 0 && ft[top].Feet.Y >= ft[top - 1].Feet.Y - 1e-4f && ft[i - 1].Time - ft[top].Time < 3 && ft[top].Feet.Y - ft[top - 1].Feet.Y < .5f) top--;
            int land = i; float fastestFall = 0;
            while (land + 1 < ft.Count && ft[land + 1].Feet.Y < ft[land].Feet.Y + 1e-4f && ft[land + 1].Time - ft[land].Time < .1)
            {
                float w = U(ft[land + 1].Feet.Y - ft[land].Feet.Y) / MathF.Max(.015f, (float)(ft[land + 1].Time - ft[land].Time));
                fastestFall = MathF.Min(fastestFall, w);
                if (ft[land + 1].Feet.Y == ft[land].Feet.Y && land + 2 < ft.Count && ft[land + 2].Feet.Y == ft[land].Feet.Y) break;
                land++;
            }
            var took = tl.Hurt.Where(h => h.Tick >= ft[i].Tick && h.Tick <= ft[land].Tick + 30).ToList();
            Console.WriteLine(FormattableString.Invariant($"FALL {ft[i].Time:0.00} s: from {U(ft[top].Feet.Y):0.0} at {ft[top].Time:0.00} to {U(ft[land].Feet.Y):0.0}, drop {U(ft[top].Feet.Y - ft[land].Feet.Y):0.0} units in {ft[land].Time - ft[top].Time:0.00} s, landing at {-fastestFall:0} units/s, health {ft[top].Health?.Value:0.#} -> {ft[Math.Min(ft.Count - 1, land + 20)].Health?.Value:0.#}, took {string.Join(" ", took.Select(h => $"{h.Taken:0.##}@{h.Tick - ft[land].Tick}"))}"));
            i = land;
        }
        return;
    }
    if (Environment.GetEnvironmentVariable("RB_REPLAY") is { Length: > 0 } only)
    {
        foreach (var line in CombatReplay.Run(product.Combat, only, product).Lines()) Console.WriteLine(line);
        return;
    }
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
    combat.Orc.Hold = true;
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
    combat.Orc.Hold = false;
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
        && Refused(file with { Weapon = "models/items/weapons/nothing/nothing.mdl" }) is string missing && missing.Contains("isn't an imported item"),
        "swords, daggers and staffs are played (#10148); a bow isn't; items the import lacks are refused");
    // The shield (#10149): with a sword and combat_shield_enabled (the combat skill's level 2).
    const string SmallShield = "models/items/armors/shield_orc/shield_orc_small.mdl";
    Check(Refused(WithSkill("combat", 2) with { OffHand = SmallShield }) is null
        && Refused(file with { OffHand = SmallShield }) is string unskilled && unskilled.Contains("combat_shield_enabled")
        && Refused(WithSkill("combat", 2) with { OffHand = SmallShield, Weapon = "models/items/weapons/daggers_kryss/daggers_kryss.mdl" }) is string twoHanded && twoHanded.Contains("goes with a sword")
        && Refused(WithSkill("combat", 2) with { OffHand = "models/items/armors/helmets/helmet_guard.mdl" }) is string notShield && notShield.Contains("isn't"),
        "a shield is played with a sword and combat_shield_enabled; without the skill, with daggers, or another off-hand item it's refused");
    combat.Rebuild(WithSkill("stamina", 1));
    Check(combat.Build!.Has("double_sprint") && combat.Stamina.Rules.Max == 12, $"stamina 1 gives double_sprint, and stamina's maximum becomes 12: {combat.Stamina.Rules.Max}");
    combat.Rebuild(file);
    Check(combat.Stamina.Rules.Max == 9 && combat.Stamina.Value <= 9, "back at stamina 0 the maximum is 9 again");
    // The drawn camera follows the look and the eye between steps (R10161-3): turned 30° with no step, the published
    // view turns 30°; placed a metre over, it moves a metre.
    {
        var p = product.Player;
        p.Camera = combat.FirstPerson.View;
        var drawnBefore = p.Descriptor();
        p.LookBy(30, 0);
        var drawnTurned = p.Descriptor();
        float degrees = MathF.Acos(Math.Clamp(Vector3.Dot(drawnBefore.Basis.Forward, drawnTurned.Basis.Forward), -1, 1)) * 180 / MathF.PI;
        p.LookBy(-30, 0);
        var at = p.Position;
        p.Place(at + Vector3.UnitX, p.LookState.YawRadians * 180 / MathF.PI, p.LookState.PitchRadians * 180 / MathF.PI);
        var drawnMoved = p.Descriptor();
        p.Place(at, p.LookState.YawRadians * 180 / MathF.PI, p.LookState.PitchRadians * 180 / MathF.PI);
        float metres = Vector3.Distance(drawnMoved.Pose.Position, drawnBefore.Pose.Position);
        Check(MathF.Abs(degrees - 30) < .5f && MathF.Abs(metres - 1) < .01f && drawnBefore.BasisMode == CameraBasisMode.Explicit,
            FormattableString.Invariant($"the first-person camera follows a look turned with no step ({degrees:0.0}°) and a placing ({metres:0.00} m)"));
    }
    // Leaning (R10161-2): the view rolls 15° and the eye moves 25 units to the side, easing at cl_leanspeed; against a
    // wall the camera stops 5 units short of it along the line from the eye, as the donor's did.
    {
        var p = product.Player;
        var leanStart = p.Position;
        float startYaw = p.LookState.YawRadians * 180 / MathF.PI, startPitch = p.LookState.PitchRadians * 180 / MathF.PI;
        (Vector3 Out, float Roll) Leaned(bool left, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                p.Lean.Step(Tick, left, false);
                combat.Step(Tick, step++, false, false, false, p.Eye, p.Forward, p.Feet, lean: p.Lean.Value);
            }
            p.Camera = combat.FirstPerson.View;
            var drawn = p.Descriptor();
            var look = Vector3.Normalize(p.Forward);
            var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
            float roll = MathF.Atan2(Vector3.Dot(drawn.Basis.Up, right), Vector3.Dot(drawn.Basis.Up, Vector3.Cross(right, look))) * -180 / MathF.PI;
            return (drawn.Pose.Position - p.Eye, roll);
        }
        float half = p.Tuning.Height / 2 + p.Config.Shape.ContactSkin, floor = p.Feet.Y;
        p.Place(new Vector3(12, floor + half, 12), 0);
        float eased = 0;
        for (int i = 0; i < 8; i++) { p.Lean.Step(PlayerStamina.GameTick, true, false); }
        eased = -p.Lean.Value;
        p.Lean.Reset();
        var open = Leaned(true, 90);
        var right0 = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(p.Forward), Vector3.UnitY));
        float openSide = Vector3.Dot(open.Out, right0) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        var back = Leaned(false, 90);
        // Against the west wall (its face at x 0.5), facing -Z so it's on the left.
        p.Place(new Vector3(.5f + p.Tuning.Radius + .01f, floor + half, 12), 0);
        var walled = Leaned(true, 90);
        float wallSide = Vector3.Dot(walled.Out, right0) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        float cameraX = (p.Eye + walled.Out).X;
        Leaned(false, 90);
        p.Lean.Reset();
        p.Place(leanStart, startYaw, startPitch);
        p.Camera = combat.FirstPerson.View;
        Check(MathF.Abs(eased - .7f) < .03f && MathF.Abs(openSide + 27.3f) < 1.5f && MathF.Abs(MathF.Abs(open.Roll) - 15) < .5f && back.Out.Length() < .3f && MathF.Abs(back.Roll) < .1f
            && wallSide > -11 && cameraX >= .5f + PlayerLean.HullMetres - .01f,
            FormattableString.Invariant($"leaning left eases {eased:0.00} of the way in 8 game ticks (the donor's 0.73), rolls the view {MathF.Abs(open.Roll):0.0}° and puts the camera {-openSide:0.0} units out (the donor's 15° and 27.3); let go it comes back ({back.Roll:0.0}°); against a wall the camera stops {-wallSide:0.0} units out, {(cameraX - .5f) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit:0.0} from it (the donor's 11 out, 5 from it)"));
    }
    // The lean against the recording: each recorded lean's start and release driving the lab's, its roll within 1° of
    // the donor's view roll on each tick.
    if (Path.Combine(repo, "local", "recordings", "experiments", "l01_a-v18-lean.rbrec") is var leanRecording && File.Exists(leanRecording))
    {
        var rolls = new List<float>();
        using (var stream = File.OpenRead(leanRecording))
            foreach (var f in RecordingReader.Frames(stream))
                if (f.ClientView.Flags.HasFlag(ClientViewFlags.ViewAngles)) rolls.Add(f.ClientView.ViewRoll);
        var lean = new PlayerLean();
        int leanNear = 0, leaning = 0, leans = 0;
        float target = 0;
        for (int i = 1; i < rolls.Count; i++)
        {
            // Pressed the tick before the roll leaves 0; let go the tick before it leaves full.
            if (target == 0 && MathF.Abs(rolls[i]) > .05f && MathF.Abs(rolls[i - 1]) <= .05f) { target = MathF.Sign(rolls[i]); leans++; }
            else if (target != 0 && MathF.Abs(rolls[i]) < MathF.Abs(rolls[i - 1]) - .05f) target = 0;
            lean.Step(PlayerStamina.GameTick, target < 0, target > 0);
            if (MathF.Abs(rolls[i]) > .05f || MathF.Abs(lean.RollDegrees) > .05f)
            {
                leaning++;
                if (MathF.Abs(lean.RollDegrees - rolls[i]) <= 1) leanNear++;
            }
        }
        Check(leans >= 6 && leanNear >= leaning * 9 / 10, $"l01_a-v18-lean: {leans} leans driven from their recorded starts and releases, the lab's roll within 1° of the donor's on {leanNear} of {leaning} leaning ticks");
    }
    // Ladders (#10163): the loft's, on the Engine's climb rail at the donor's 80 units/s; got on walking into it and off
    // at the top onto the loft, each in mm_time_to_exit_enter_ladder's 1 s with the controls held off (R10163-1); on
    // again backing off its top, down to its foot and off; a jump lets go. The drawn ladder stands where it's climbed
    // (R10163-2).
    {
        var p = product.Player;
        var climber = (IReplayBody)product;
        var ladderStart = p.Position;
        float ladderYaw = p.LookState.YawRadians * 180 / MathF.PI, ladderPitch = p.LookState.PitchRadians * 180 / MathF.PI;
        float half = p.Tuning.Height / 2 + p.Config.Shape.ContactSkin;
        var east = Vector3.UnitX;
        var loftLadder = p.Ladders[0];
        var (railBottom, _) = LadderRules.Rail(loftLadder, p.Tuning.Radius, half);
        // The drawn ladder: its rails either side of the climb rail's line, against the face, between it and the rail.
        var pieces = LadderRules.Pieces(loftLadder);
        float across = pieces.Take(2).Select(piece => MathF.Abs(piece.Centre.Z - railBottom.Z)).Max();
        float drawnFromFace = loftLadder.Foot.X - pieces[0].Centre.X, railFromFace = loftLadder.Foot.X - railBottom.X;
        bool drawnThere = pieces.Count > 6 && MathF.Abs(across - .25f) < .01f && drawnFromFace > 0 && drawnFromFace < railFromFace
            && pieces.Max(piece => piece.Centre.Y + piece.Size.Y / 2) >= loftLadder.Top - .01f;
        p.Place(new Vector3(19, half, 21), 90);
        // Getting on: from the first step the body moves onto the rail; for a second it neither climbs nor walks.
        int stepsToMount = 0;
        for (; stepsToMount < 90 && p.LadderMove is null && p.Climbing is null; stepsToMount++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        float mountY = p.Feet.Y;
        int mountSteps = 0;
        for (; mountSteps < 120 && p.Climbing is null; mountSteps++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        bool mountHeld = MathF.Abs(p.Feet.Y - mountY) < .02f;
        for (int i = 0; i < 6; i++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        float y0 = p.Feet.Y;
        for (int i = 0; i < 30; i++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        float upSpeed = (p.Feet.Y - y0) / (30 * Tick) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        for (int i = 0; i < 240 && p.LadderMove is null; i++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        // Getting off at the top: a second onto the loft, the controls held off (backing doesn't take it back).
        int exitSteps = 0;
        for (; exitSteps < 120 && p.LadderMove is not null; exitSteps++) climber.Step(-Vector2.UnitY, east, false, false, false, false, Tick);
        for (int i = 0; i < 30; i++) climber.Step(Vector2.Zero, east, false, false, false, false, Tick);
        var onLoft = p.Feet;
        bool offAtTop = p.Climbing is null && onLoft.X > 20.2f && MathF.Abs(onLoft.Y - 2.5f) < .1f && p.Motion.Grounded;
        // Back off the top, facing on: onto the rail in a second, then down at the same speed to its foot and off.
        for (int i = 0; i < 150 && p.Climbing is null; i++) climber.Step(-Vector2.UnitY, east, false, false, false, false, Tick);
        bool remounted = p.Climbing is not null;
        for (int i = 0; i < 10; i++) climber.Step(-Vector2.UnitY, east, false, false, false, false, Tick);
        float y1 = p.Feet.Y;
        for (int i = 0; i < 30; i++) climber.Step(-Vector2.UnitY, east, false, false, false, false, Tick);
        float downSpeed = (y1 - p.Feet.Y) / (30 * Tick) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        for (int i = 0; i < 300 && (p.Climbing is not null || p.LadderMove is not null); i++) climber.Step(-Vector2.UnitY, east, false, false, false, false, Tick);
        for (int i = 0; i < 20; i++) climber.Step(Vector2.Zero, east, false, false, false, false, Tick);
        bool offAtFoot = p.Climbing is null && p.Feet.Y < .05f && p.Motion.Grounded;
        // On again, a jump lets go at once.
        for (int i = 0; i < 150 && p.Climbing is null; i++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        for (int i = 0; i < 20; i++) climber.Step(Vector2.UnitY, east, false, false, false, false, Tick);
        climber.Step(Vector2.Zero, east, true, true, false, false, Tick);
        bool jumpedOff = p.Climbing is null && p.LadderMove is null;
        for (int i = 0; i < 90; i++) climber.Step(Vector2.Zero, east, false, false, false, false, Tick);
        // Crouched into it (R10163-3): the ladder stands the body over its feet, which never go below the floor, and it
        // climbs, crouch still held, with no recovery from a penetration.
        p.Place(new Vector3(19, half, 21), 90);
        for (int i = 0; i < 20; i++) climber.Step(Vector2.Zero, east, false, false, true, false, Tick);
        bool crouchedFirst = p.Motion.Stance == CharacterStance.Crouched;
        uint recoveriesBefore = p.Recoveries;
        float lowestFeet = float.MaxValue;
        void CrouchedStep() { climber.Step(Vector2.UnitY, east, false, false, true, false, Tick); lowestFeet = MathF.Min(lowestFeet, p.Feet.Y); }
        for (int i = 0; i < 300 && p.Climbing is null; i++) CrouchedStep();
        bool crouchedMounted = p.Climbing is not null && p.Motion.Stance == CharacterStance.Standing;
        float crouchedFrom = p.Feet.Y;
        for (int i = 0; i < 30; i++) CrouchedStep();
        float crouchedRise = (p.Feet.Y - crouchedFrom) / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        bool crouchedClean = p.Recoveries == recoveriesBefore;
        climber.Step(Vector2.Zero, east, true, true, false, false, Tick);
        for (int i = 0; i < 120; i++) climber.Step(Vector2.Zero, east, false, false, false, false, Tick);
        Check(crouchedFirst && crouchedMounted && lowestFeet > -.01f && crouchedRise > 30 && crouchedClean,
            FormattableString.Invariant($"crouched into the ladder (R10163-3): stood onto its rail ({crouchedMounted}), the feet never below the floor (lowest {lowestFeet:0.000} m), climbing {crouchedRise:0} units in 0.5 s with crouch held, no recovery ({crouchedClean})"));
        p.Place(ladderStart, ladderYaw, ladderPitch);
        int second = (int)MathF.Round(LadderRules.EnterExitSeconds / Tick);
        Check(drawnThere && mountSteps >= second - 1 && mountSteps <= second + 1 && mountHeld && exitSteps >= second - 2 && exitSteps <= second + 1
            && MathF.Abs(upSpeed - 80) < 4 && offAtTop && remounted && MathF.Abs(downSpeed - 80) < 4 && offAtFoot && jumpedOff,
            FormattableString.Invariant($"the loft's ladder, drawn {drawnFromFace:0.00} m off its face either side of the climb (the rail {railFromFace:0.00} m off): got on walking into it ({stepsToMount} steps) in {mountSteps * Tick:0.00} s not climbing, climbed at {upSpeed:0} units/s (the donor's 80), got off onto the loft in {exitSteps * Tick:0.00} s (feet {onLoft.X:0.00}, {onLoft.Y:0.00}); on again backing off its top ({remounted}), down at {downSpeed:0}, off at its foot ({offAtFoot}); a jump lets go ({jumpedOff})"));
    }
    // Falls (R10159-1): the donor's landings from l01_a-v18-fall-1 and -2 took none under 300 units and Source's fall
    // damage above, 10 after 316 units, 24 after 371, 51 after 517 and 61 after 575.
    {
        bool wasHeld = combat.Orc.Hold;
        combat.Orc.Hold = true;
        float Taken(float dropUnits)
        {
            combat.Health.Set(combat.Health.Max);
            float before = combat.PlayerHealth;
            var feet = new Vector3(9, dropUnits * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, 13);
            product.Player.Place(feet + Vector3.UnitY * (product.Player.Tuning.Height / 2 + product.Player.Config.Shape.ContactSkin), 0);
            for (int i = 0; i < 600 && !(i > 2 && product.Player.Motion.Grounded); i++) Advance(1);
            return before - combat.PlayerHealth;
        }
        var falls = new[] { 250f, 284, 316.4f, 370.6f, 516.5f, 574.9f }.Select(d => (Drop: d, Taken: Taken(d), Donor: FallRules.Damage(d))).ToList();
        combat.Health.Set(combat.Health.Max);
        combat.Orc.Hold = wasHeld;
        // The lab's player has 50 health (the donor's 80 in the scenario): the last drop takes all of it.
        float[] recorded = [.. new float[] { 0, 0, 10, 24, 51, 61 }.Select(x => MathF.Min(x, combat.Health.Max))];
        Check(falls.Zip(recorded).All(x => MathF.Abs(x.First.Taken - x.Second) <= 2.5f),
            FormattableString.Invariant($"a fall takes the donor's health from 300 units: {string.Join(", ", falls.Zip(recorded).Select(x => $"{x.First.Drop:0} units {x.First.Taken:0.#} (the donor's {x.Second})"))}"));
    }
    // Telekinesis (#10164): a grip costs its mass class's mana (the jug 4, a 40 kg crate 15), the 300 kg crate is refused,
    // and so is a grip the mana doesn't cover; the barrel (80 kg) only drops when thrown; held where the donor holds (54 units from the eye, small), thrown at its
    // 1447 units/s, and a thrown jug takes the donor's 16 from the orc it meets (its impact and rebound, 8 each).
    {
        var tkP = product.Player;
        var tkTk = product.Cubes;
        var tkScene = product.Scene;
        var tkRules = combat.Telekinesis;
        bool tkWasHeld = combat.Orc.Hold;
        DarkMessiah.Lab.Scene.LabCube tkCube(string label) => tkScene.Cubes.First(tkC => tkC.Definition.Label == label);
        Vector3 tkAt(DarkMessiah.Lab.Scene.LabCube tkC) => tkScene.Pose(tkC).Translation;
        float tkUnits(float metres) => metres / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        // Stands 1.5 m south of the body (its centre level with the eye's line), looking at it.
        void tkFace(DarkMessiah.Lab.Scene.LabCube tkC, float tkFrom = 1.5f)
        {
            var tkTarget = tkAt(tkC);
            var tkFeet = new Vector3(tkTarget.X, 0, tkTarget.Z - tkFrom);
            tkP.Place(tkFeet + Vector3.UnitY * (tkP.Tuning.Height / 2 + tkP.Config.Shape.ContactSkin), 0);
            Advance(2);
            var tkLook = Vector3.Normalize(tkTarget - tkP.Eye);
            tkP.Face(MathF.Atan2(tkLook.X, -tkLook.Z), MathF.Asin(Math.Clamp(tkLook.Y, -1, 1)));
            // The interaction's focus settles on what's in view over a few updates.
            Advance(10);
        }
        // The arena's bodies are l01_a's props (R10164-2): drawn as their models, their masses their .phy's, one of each
        // class: the jugs small, the crates and barrel big, the big crate huge.
        var tkProps = tkScene.Cubes.Select(c => (c.Definition.Model, c.Puppet?.Model.DonorPath, c.Mass, Class: tkRules.Class(c.Mass))).ToList();
        Check(tkProps.Count >= 5 && tkProps.All(x => x.Model is not null && x.DonorPath == x.Model && x.Mass == product.Donor.Mass(x.Model!))
                && new[] { DarkMessiah.Lab.Magic.MassClass.Small, DarkMessiah.Lab.Magic.MassClass.Big, DarkMessiah.Lab.Magic.MassClass.Huge }.All(k => tkProps.Any(x => x.Class == k)),
            FormattableString.Invariant($"the arena's bodies are l01_a's props, drawn as their models with their .phy masses: {string.Join(", ", tkProps.Select(x => $"{Path.GetFileNameWithoutExtension(x.Model)} {x.Mass:0.##} kg ({x.Class})"))}"));
        combat.Mana.Fill();
        var tkJug = tkCube("jug / 4kg");
        tkFace(tkJug);
        float tkBefore = combat.Mana.Value;
        tkTk.Use();
        if (Environment.GetEnvironmentVariable("RB_MAGICONLY") == "1") Console.WriteLine($"  jug: {tkTk.Message}; focus {tkTk.TargetLabel}; at {tkAt(tkJug)}, eye {tkP.Eye}, forward {tkP.Forward}");
        bool tkJugHeld = tkTk.Held == tkJug;
        float tkJugCost = tkBefore - combat.Mana.Value;
        // Held a second looking level, then thrown: its speed over the first steps.
        tkP.Face(tkP.LookState.YawRadians, 0);
        Advance(60);
        float tkHeld = tkUnits(Vector3.Distance(tkAt(tkJug), tkP.Eye));
        tkTk.Throw();
        Advance(2);
        var tkAfter = tkAt(tkJug);
        Advance(2);
        float tkThrown = tkUnits(Vector3.Distance(tkAt(tkJug), tkAfter)) / (2 * Tick);
        var tkCrate = tkCube("crate / 40kg");
        tkFace(tkCrate);
        tkBefore = combat.Mana.Value;
        tkTk.Use();
        float tkCrateCost = tkBefore - combat.Mana.Value;
        bool tkCrateHeld = tkTk.Held?.Mass == 40;
        tkTk.Use();
        // The barrel (80 kg): gripped for 15, held, and the throw only drops it (R10164-1).
        var tkBarrel = tkCube("barrel / 80kg");
        combat.Mana.Fill();
        tkFace(tkBarrel, 2);
        tkTk.Use();
        bool tkBarrelHeld = tkTk.Held == tkBarrel;
        tkP.Face(tkP.LookState.YawRadians, 0);
        Advance(90);
        uint tkDropsBefore = tkTk.Drops;
        tkTk.Throw();
        Advance(2);
        var tkBarrelAfter = tkAt(tkBarrel);
        Advance(2);
        var tkBarrelStep = (tkAt(tkBarrel) - tkBarrelAfter) / (2 * Tick);
        float tkBarrelSpeed = tkUnits(new Vector2(tkBarrelStep.X, tkBarrelStep.Z).Length());
        bool tkBarrelDropped = tkBarrelHeld && tkTk.Drops == tkDropsBefore + 1 && tkBarrelSpeed < 100;
        var tkBig = tkCube("big crate / 300kg");
        tkFace(tkBig, 2);
        tkBefore = combat.Mana.Value;
        tkTk.Use();
        bool tkHugeRefused = tkTk.Held is null && combat.Mana.Value == tkBefore && tkTk.Refusals == 1;
        combat.Mana.Set(10);
        var tkOther = tkScene.Cubes.Last(tkC => tkC.Definition.Label == "crate / 40kg");
        tkFace(tkOther);
        tkTk.Use();
        bool tkPoorRefused = tkTk.Held is null && combat.Mana.Value == 10 && tkTk.Refusals == 2;
        // At the orc, the donor's telekinesis-throw way (R10164-3): the arena's second jug (its open middle) gripped and
        // held level, the orc 3.5 m ahead, held still; the jug thrown straight at its body and followed for 1.5 s, every
        // part it newly touches hit (the donor's jug took two of 8 in its one impact).
        combat.Mana.Fill();
        combat.Orc.Hold = true;
        var tkThrownJug = tkScene.Cubes.Last(tkC => tkC.Definition.Label == "jug / 4kg");
        tkFace(tkThrownJug);
        tkTk.Use();
        bool tkRegripped = tkTk.Held == tkThrownJug;
        tkP.Face(tkP.LookState.YawRadians, 0);
        Advance(40);
        var tkLevel = tkP.Forward with { Y = 0 };
        var tkOrcFeet = (tkP.Eye + Vector3.Normalize(tkLevel) * 3.5f) with { Y = 0 };
        combat.Orc.PlaceAt(tkOrcFeet);
        for (int i = 0; i < 20; i++) { combat.Orc.PlaceAt(tkOrcFeet); Advance(1); }
        var tkAim = Vector3.Normalize(tkOrcFeet + Vector3.UnitY * 1.1f - tkP.Eye);
        tkP.Face(MathF.Atan2(tkAim.X, -tkAim.Z), MathF.Asin(Math.Clamp(tkAim.Y, -1, 1)));
        Advance(20);
        // What the throws took (its regeneration aside, #10348).
        float tkOrcBefore = combat.Orc.Taken;
        tkTk.Throw();
        // Frozen where it stands, as the donor's was (ai_disable): put back each step.
        for (int i = 0; i < 90; i++) { combat.Orc.PlaceAt(tkOrcFeet); Advance(1); }
        float tkOrcTaken = combat.Orc.Taken - tkOrcBefore;
        uint tkHits = tkTk.Hits;
        combat.Orc.Hold = tkWasHeld;
        ((IReplayBody)product).Begin();
        Check(tkJugHeld && tkJugCost == tkRules.SmallCost && MathF.Abs(tkHeld - tkRules.HoldSmallUnits) <= tkRules.HoldSmallUnits * .25f && MathF.Abs(tkThrown - tkRules.ThrowUnits) <= tkRules.ThrowUnits * .1f
            && tkCrateHeld && tkCrateCost == tkRules.BigCost && tkHugeRefused && tkPoorRefused && tkRegripped && tkBarrelDropped && tkHits == 2 && tkOrcTaken == 2 * tkRules.SmallDamage,
            FormattableString.Invariant($"telekinesis: the jug gripped for {tkJugCost:0} mana (the donor's {tkRules.SmallCost:0}), held {tkHeld:0} units from the eye ({tkRules.HoldSmallUnits:0}), thrown at {tkThrown:0} units/s ({tkRules.ThrowUnits:0}); a 40 kg crate for {tkCrateCost:0} ({tkRules.BigCost:0}); the 300 kg crate refused ({tkHugeRefused}), and a crate with 10 mana ({tkPoorRefused}); the barrel (80 kg) only dropped by the throw, {tkBarrelSpeed:0} units/s across ({tkBarrelDropped}); a thrown jug takes {tkOrcTaken:0.#} from the orc in {tkHits} hits (the donor's jug 16 in two of the settings' {tkRules.SmallDamage:0})"));
        // And against the donor (l01_a-v18-telekinesis-mass and -throw): every grip cost its body's class (its model's
        // .phy mass), a press that gripped nothing spent nothing, bodies under 50 kg left at the throw speed (the median
        // within 10%), and each hit of a thrown small body took the settings' 8 from the orc.
        var tkRecordings = new[] { "mass", "throw", "heavy", "barrel" }.Select(n => Path.Combine(repo!, "local", "recordings", "experiments", $"l01_a-v18-telekinesis-{n}.rbrec")).Where(File.Exists).ToList();
        if (tkRecordings.Count == 4)
        {
            var tkGrabs = tkRecordings.SelectMany(TelekinesisCalibration.Measure).ToList();
            foreach (var grab in tkGrabs) Console.WriteLine($"  telekinesis {grab}");
            var tkKnown = tkGrabs.Where(g => g.Model is { } m && product.Donor.Mass(m) is not null).Select(g => (Grab: g, Kg: product.Donor.Mass(g.Model!)!.Value)).ToList();
            int tkCostAgree = tkKnown.Count(x => x.Grab.ManaCost == tkRules.Cost(x.Kg));
            bool tkNothingFree = tkGrabs.Where(g => g.Model is null).All(g => g.ManaCost == 0);
            var tkSpeeds = tkKnown.Where(x => x.Grab.Thrown && x.Kg < 50 && x.Grab.ThrowSpeed > 100).Select(x => x.Grab.ThrowSpeed).ToList();
            // Past the lab's limit, the barrels: the throw left them falling, not flying (R10164-1).
            var tkHeavy = tkKnown.Where(x => x.Grab.Thrown && !tkRules.Throws(x.Kg)).Select(x => x.Grab.ThrowSpeed).ToList();
            float tkSpeed = tkSpeeds.Count > 0 ? MovementMeasure.Median(tkSpeeds) : 0;
            var tkSmallHits = tkKnown.Where(x => x.Grab.Thrown && tkRules.Impact(x.Kg) == DarkMessiah.Lab.Magic.ImpactClass.Small)
                .SelectMany(x => x.Grab.Hits).Where(h => h.Taken > 0).ToList();
            // And each throw's whole loss against the lab's at its speed: an impact and its rebound (R10164-3).
            var tkThrowLoss = tkKnown.Where(x => x.Grab.Thrown && x.Grab.Hits.Any(h => h.Taken > 0))
                .Select(x => (Donor: x.Grab.Hits.Sum(h => h.Taken), Lab: tkRules.Damage(x.Kg, x.Grab.ThrowSpeed) + tkRules.Damage(x.Kg, x.Grab.ThrowSpeed * tkRules.Restitution))).ToList();
            Check(tkKnown.Count >= 15 && tkCostAgree == tkKnown.Count && tkNothingFree && tkSpeeds.Count >= 6 && MathF.Abs(tkSpeed - tkRules.ThrowUnits) <= .1f * tkRules.ThrowUnits
                    && tkSmallHits.Count >= 4 && tkSmallHits.All(h => h.Taken == tkRules.SmallDamage) && tkThrowLoss.Count >= 2 && tkThrowLoss.All(t => t.Donor == t.Lab)
                    && tkHeavy.Count >= 5 && MovementMeasure.Median(tkHeavy) < 100,
                FormattableString.Invariant($"the donor's telekinesis: {tkCostAgree} of {tkKnown.Count} grips cost their class's mana, presses that gripped nothing spent none ({tkNothingFree}), bodies under 50 kg thrown at {tkSpeed:0} units/s (median of {tkSpeeds.Count}; the lab's {tkRules.ThrowUnits:0}), barrels past {tkRules.ThrownKg:0} kg left at {string.Join(", ", tkHeavy.Select(v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))} (the lab drops them), small bodies' hits on the orc took {string.Join(", ", tkSmallHits.Select(h => h.Taken.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))} (the settings' {tkRules.SmallDamage:0}), each throw {string.Join(", ", tkThrowLoss.Select(t => $"{t.Donor:0} (the lab's {t.Lab:0})"))}"));
        }
        else Console.WriteLine("skip the donor's telekinesis: no local/recordings/experiments/l01_a-v18-telekinesis-mass.rbrec and -throw.rbrec");
    }
    // The fireball (#10165): belt slot 3 puts it in hand; a press casts it for its 8 mana, the arms playing the donor's
    // cast; it forms at the hand for 0.57 s and flies at 725 units/s; struck, the orc takes its 12, and off the floor short
    // of it the share its distance leaves (Source's falloff over explosion_radius 120); with too little mana nothing is cast,
    // and a mana potion gives 50. Against l01_a-v18-fireball: each cast's cost, forming and speed, and each hit's damage.
    {
        var fb = combat.Fireball;
        bool fbWasHeld = combat.Orc.Hold;
        combat.Orc.Hold = true;
        combat.Mana.Fill();
        combat.UseBelt(2);
        bool fbInHand = combat.SpellInHand == DarkMessiah.Lab.Magic.FireballRules.Spell;
        // The orc 200 units north of the eye, in the arena's open middle.
        var fbEye = new Vector3(12, 1.6f, 6);
        var fbOrc = new Vector3(12, 0, 6 + 200 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
        combat.Orc.PlaceAt(fbOrc);
        Vector3 Toward(Vector3 at) => Vector3.Normalize(at - fbEye);
        // One cast at aim: pressed one step, held a few, then run until it's gone off or 3 s have passed.
        (float Cost, float Formed, float Speed, float Taken, string? Clip) CastAt(Vector3 aim)
        {
            float manaBefore = combat.Mana.Value, healthBefore = combat.Orc.Health, formed = float.NaN, speed = 0, cost = 0;
            string? clip = null;
            Vector3? last = null;
            for (int i = 0; i < 180; i++)
            {
                combat.Step(Tick, step++, i < 3, false, false, fbEye, Toward(aim), fbEye - Vector3.UnitY * 1.6f);
                if (i == 0) cost = manaBefore - combat.Mana.Value;
                if (i == 1) clip = combat.FirstPerson.Spell?.Cast;
                if (combat.Fireballs.FirstOrDefault() is { } ball)
                {
                    if (ball.Flying && float.IsNaN(formed)) formed = ball.Age;
                    if (ball.Flying && last is Vector3 was && ball.Exploded is null) speed = Vector3.Distance(ball.Position, was) / Tick / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
                    last = ball.Position;
                }
                else if (i > 3) break;
            }
            return (cost, formed, speed, healthBefore - combat.Orc.Health, clip);
        }
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(fbOrc);
        var fbDirect = CastAt(fbOrc + Vector3.UnitY * 1.1f);
        float fbDirectOff = combat.LastFireball?.Distance ?? float.NaN;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(fbOrc);
        var fbFloor = CastAt(new Vector3(12, 0, fbOrc.Z - 100 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit));
        float fbFloorOff = combat.LastFireball?.Distance ?? float.NaN;
        combat.Mana.Set(5);
        var fbPoor = CastAt(fbOrc + Vector3.UnitY * 1.1f);
        bool fbRefused = fbPoor.Cost == 0 && combat.Spells.Refused == 1;
        combat.UseBelt(3);
        float fbPotion = combat.Mana.Value - 5;
        combat.UseBelt(2);
        bool fbAway = combat.SpellInHand is null;
        combat.Orc.Hold = fbWasHeld;
        ((IReplayBody)product).Begin();
        Check(fbInHand && fbAway && fbDirect.Cost == fb.Cost && fbDirect.Clip == DarkMessiah.Lab.Magic.FireballRules.CastClip && MathF.Abs(fbDirect.Formed - fb.FormSeconds) <= 1.5f * Tick
            && MathF.Abs(fbDirect.Speed - fb.SpeedUnits) <= .03f * fb.SpeedUnits && MathF.Abs(fbDirect.Taken - fb.Effect) < .01f && fbDirectOff < .5f
            && fbFloorOff is > 30 and < 110 && MathF.Abs(fbFloor.Taken - fb.Damage(fbFloorOff)) < .01f && fbRefused && fbPotion == MathF.Min(50, combat.Mana.Max - 5),
            FormattableString.Invariant($"the fireball: in hand from belt slot 3 ({fbInHand}), cast for {fbDirect.Cost:0} mana ({fb.Cost:0}) with {fbDirect.Clip}, formed {fbDirect.Formed:0.00} s ({fb.FormSeconds:0.00}), flew at {fbDirect.Speed:0} units/s ({fb.SpeedUnits:0}); struck, the orc took {fbDirect.Taken:0.#} ({fb.Effect:0}, {fbDirectOff:0.##} units off); off the floor {fbFloorOff:0} units short of it, {fbFloor.Taken:0.##} ({fb.Damage(fbFloorOff):0.##}); with 5 mana none cast ({fbRefused}); a mana potion gave {fbPotion:0}; the key again, the weapon back ({fbAway})"));
        var fbRecording = Path.Combine(repo!, "local", "recordings", "experiments", "l01_a-v18-fireball.rbrec");
        if (File.Exists(fbRecording))
        {
            var (fbHalf, fbHeight) = (combat.Orc.Hull.HalfExtents.X / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, 2 * combat.Orc.Hull.HalfExtents.Y / RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
            var fbShots = SpellCalibration.Measure(fbRecording, "spell_fire_3", fbHalf, fbHeight);
            foreach (var shot in fbShots) Console.WriteLine($"  fireball {shot}");
            // Each struck orc's health, its immediate share and the trickle after it, against the lab's 12. One that went off
            // short of the body is printed, not held to the falloff: one shot doesn't settle it (docs/lab.md, "The fireball").
            var fbHits = fbShots.Where(x => x.Hits.Count > 0 && x.Hits.Min(h => h.BodyDistance) == 0).Select(x => (Taken: x.Hits.Sum(h => h.Taken) + x.Burn.Sum(b => b.Taken), Rule: fb.Damage(0))).ToList();
            var fbFast = fbShots.Where(x => x.Hits.Count > 0 || x.Life < 1).ToList();
            Check(fbShots.Count >= 6 && fbShots.All(x => x.ManaCost == fb.Cost && MathF.Abs(x.Forming - fb.FormSeconds) <= .03f)
                && fbFast.All(x => MathF.Abs(x.Speed - fb.SpeedUnits) <= .03f * fb.SpeedUnits) && fbHits.Count >= 3 && fbHits.All(h => MathF.Abs(h.Taken - h.Rule) <= MathF.Max(1, .1f * h.Rule)),
                FormattableString.Invariant($"the donor's fireballs: {fbShots.Count} cast for {fb.Cost:0} mana each, formed in {string.Join(", ", fbShots.Select(x => x.Forming.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Distinct())} s, flew at {string.Join(", ", fbFast.Select(x => x.Speed.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))} units/s; struck, the orc took {string.Join(", ", fbHits.Select(h => $"{h.Taken:0.#} (the lab's {h.Rule:0.#})"))}"));
            var fbReplayed = CombatReplay.Run(combat, fbRecording, product);
            Check(fbReplayed.Mana is { } m && m.Near >= m.Ticks * 97 / 100,
                FormattableString.Invariant($"{fbReplayed.Name}: the lab's mana follows the donor's within {CombatReplay.ManaTolerance} on {fbReplayed.Mana?.Near} of {fbReplayed.Mana?.Ticks} ticks (worst {fbReplayed.Mana?.Worst:0.##}), through 7 casts"));
        }
        else Console.WriteLine("skip the donor's fireballs: no local/recordings/experiments/l01_a-v18-fireball.rbrec");
    }
    // The attack spells (#10166; docs/lab.md, "Attack spells"): belt slots 6 to 8 put the lightning bolt, the freeze and the
    // inferno in hand. The bolt strikes 1.8 s after the press for its 8 mana, 15 ×2 on the orc, and stuns it; one that
    // would kill leaves it at 1 and it dies 1.7 s on. The freeze costs 15 as it's cast and holds the orc it meets still for
    // 7 s. The inferno costs 20 at the press and 15 a second from 1.5 s, and burns the orc in front for 13 every 0.51 s
    // from 0.17 s; a burn that would kill leaves it at 1 and it dies 5 s on. Against l01_a-v18-attack-spells and -2.
    {
        float smpu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        // The donor's mana, 100: extra_mana 3 (with focus, which it needs).
        combat.Rebuild(file with { Skills = new Dictionary<string, int>(file.Skills) { ["focus"] = 1, ["extra_mana"] = 3 } });
        bool spWasHeld = combat.Orc.Hold;
        combat.Orc.Hold = true;
        var spEye = new Vector3(12, 1.6f, 6);
        var spOrc = new Vector3(12, 0, 6 + 200 * smpu);
        var spChest = spOrc + Vector3.UnitY * 1.1f;
        void InHand(string spell, int slot) { if (combat.SpellInHand != spell) combat.UseBelt(slot); }
        // Steps with attack held for the given steps; the orc's health, the mana and its state each step.
        List<(float Health, float Mana, DarkMessiah.Lab.Npc.OrcState State, bool Alive, float Frozen)> Run(int heldSteps, int steps)
        {
            var trace = new List<(float, float, DarkMessiah.Lab.Npc.OrcState, bool, float)>();
            for (int i = 0; i < steps; i++)
            {
                // Held where it stands: the orc otherwise walks in.
                if (combat.Orc.FrozenFor <= 0) combat.Orc.PlaceAt(spOrc);
                combat.Step(Tick, step++, i < heldSteps, false, false, spEye, Vector3.Normalize(spChest - spEye), spEye - Vector3.UnitY * 1.6f);
                trace.Add((combat.Orc.Health, combat.Mana.Value, combat.Orc.State, combat.Orc.Alive, combat.Orc.FrozenFor));
            }
            return trace;
        }
        void Fresh() { combat.Orc.Spawn(); combat.Orc.PlaceAt(spOrc); combat.Mana.Fill(); }
        // The lightning bolt: tapped at a fresh orc, then again at the 5 it leaves.
        InHand(DarkMessiah.Lab.Magic.LightningRules.Spell, 5);
        bool boltInHand = combat.SpellInHand == DarkMessiah.Lab.Magic.LightningRules.Spell;
        Fresh();
        var lr = combat.Lightning;
        var bolt = Run(1, (int)(3 / Tick));
        int struckAt = bolt.FindIndex(t => t.Health < 35);
        // What its blows took, its regeneration aside (#10348).
        float boltTaken = combat.Orc.Taken;
        float boltCost = combat.Mana.Max - bolt[Math.Max(0, struckAt)].Mana;
        bool boltStunned = bolt.Skip(Math.Max(0, struckAt)).Any(t => t.State == DarkMessiah.Lab.Npc.OrcState.Stunned);
        var second = Run(1, (int)(4 / Tick));
        int leftAt = second.FindIndex(t => t.Health < bolt[^1].Health), diedAt = second.FindIndex(t => !t.Alive);
        Check(boltInHand && MathF.Abs((struckAt + 1) * Tick - lr.FireSeconds) <= .05f && MathF.Abs(boltCost - lr.Cost) < .05f && MathF.Abs(boltTaken - 2 * lr.Effect) < .01f && boltStunned
                && leftAt >= 0 && MathF.Abs(second[leftAt].Health - 1) < .01f && diedAt > leftAt && MathF.Abs((diedAt - leftAt) * Tick - lr.DeathDelay) <= 2 * Tick,
            FormattableString.Invariant($"the lightning bolt: in hand from belt slot 6 ({boltInHand}); a tap strikes {(struckAt + 1) * Tick:0.00} s after it ({lr.FireSeconds:0.0}) for {boltCost:0.#} mana, the orc taking {boltTaken:0.#} (15 ×2) and stunned ({boltStunned}); a second leaves it at {(leftAt >= 0 ? second[leftAt].Health : float.NaN):0.#} and it dies {(diedAt - leftAt) * Tick:0.00} s on ({lr.DeathDelay:0.0})"));
        // The freeze: cast at a fresh orc, which stands frozen 7 s.
        InHand(DarkMessiah.Lab.Magic.FreezeRules.Spell, 6);
        Fresh();
        var fr = combat.Freeze;
        var frozen = Run(1, (int)(9 / Tick));
        float freezeCost = combat.Mana.Max - frozen[0].Mana;
        int frozenFrom = frozen.FindIndex(t => t.Frozen > 0), thawed = frozen.FindIndex(frozenFrom + 1, t => t.Frozen <= 0);
        Check(combat.SpellInHand == DarkMessiah.Lab.Magic.FreezeRules.Spell && MathF.Abs(freezeCost - fr.Cost) < .05f && frozenFrom >= 0 && MathF.Abs(frozenFrom * Tick - fr.CastSeconds) <= .1f
                && MathF.Abs((thawed - frozenFrom) * Tick - fr.FrozenSeconds) <= 2 * Tick && frozen.All(t => MathF.Abs(t.Health - 35) < .01f),
            FormattableString.Invariant($"the freeze: cast for {freezeCost:0.#} mana ({fr.Cost:0}); its bolt froze the orc 200 units off {frozenFrom * Tick:0.00} s after the press (the bolt {fr.CastSeconds:0.00} s in) for {(thawed - frozenFrom) * Tick:0.00} s ({fr.FrozenSeconds:0}), taking nothing"));
        // The inferno: held 3 s at a fresh orc.
        InHand(DarkMessiah.Lab.Magic.InfernoRules.Spell, 7);
        Fresh();
        var ir = combat.Inferno;
        var burn = Run((int)(3 / Tick), (int)(10 / Tick));
        var burnTimes = new List<float>();
        // Each burn's blow; one after its regeneration's 0.25 (#10348) takes only that, and isn't counted.
        for (int i = 1; i < burn.Count; i++) if (burn[i].Alive && burn[i].Health < burn[i - 1].Health - .5f) burnTimes.Add((i + 1) * Tick);
        float atPress = combat.Mana.Max - burn[0].Mana, atRelease = burn[(int)(3 / Tick) - 1].Mana;
        int burnDied = burn.FindIndex(t => !t.Alive);
        float lastBurn = burnTimes.Count > 0 ? burnTimes[^1] : float.NaN;
        // Upkeep from 1.5 s to the release, and the regeneration's ticks between (the mana gains 0.7 a second while at most full).
        float upkeepSpent = burn[(int)(1.5f / Tick)].Mana - atRelease;
        Check(combat.SpellInHand == DarkMessiah.Lab.Magic.InfernoRules.Spell && MathF.Abs(atPress - ir.Cost) < .05f && MathF.Abs(upkeepSpent - ir.Maintain * 1.5f) <= 1.2f
                && burnTimes.Count == 3 && MathF.Abs(burnTimes[0] - ir.FirstSeconds) <= .04f && MathF.Abs(burnTimes[1] - burnTimes[0] - ir.EverySeconds) <= 1.5f * Tick
                && MathF.Abs(burn[(int)(3 / Tick)].Health - 1) < .01f && burnDied > 0 && MathF.Abs(burnDied * Tick - lastBurn - ir.BurnDeath) <= 2 * Tick,
            FormattableString.Invariant($"the inferno: held 3 s, {atPress:0.#} mana at the press ({ir.Cost:0}) and {upkeepSpent:0.#} from 1.5 s to the release ({ir.Maintain:0} a second, less the regeneration); it burnt the orc at {string.Join(", ", burnTimes.Select(t => t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s, leaving it at {burn[(int)(3 / Tick)].Health:0.#}, and it died {burnDied * Tick - lastBurn:0.00} s after the last ({ir.BurnDeath:0.0})"));
        // The inferno is an area (R10166-1): the orc and its squad-mate side by side, both in its path, both burn. Neither
        // spell strikes through the arena (R10166-3): a bolt at the orc behind the big crate strikes nothing, from beside it
        // the orc.
        var spTuning = combat.Tuning;
        var spMateAt = spOrc + Vector3.UnitX * .9f;
        combat.Retune(spTuning with { Orc = spTuning.Orc with { SquadMate = [spMateAt.X, spMateAt.Y, spMateAt.Z] } });
        combat.Reset();
        combat.Rebuild(file with { Skills = new Dictionary<string, int>(file.Skills) { ["focus"] = 1, ["extra_mana"] = 3 } });
        combat.Orc.Hold = combat.Mate!.Hold = true;
        InHand(DarkMessiah.Lab.Magic.InfernoRules.Spell, 7);
        combat.Mana.Fill();
        var spMid = (spOrc + spMateAt) / 2 + Vector3.UnitY * 1.1f;
        for (int i = 0; i < (int)(1.3f / Tick); i++)
        {
            combat.Orc.PlaceAt(spOrc);
            combat.Mate.PlaceAt(spMateAt);
            combat.Step(Tick, step++, true, false, false, spEye, Vector3.Normalize(spMid - spEye), spEye - Vector3.UnitY * 1.6f);
        }
        float areaFirst = combat.Orc.Taken, areaMate = combat.Mate.Taken;
        combat.Step(Tick, step++, false, false, false, spEye, Vector3.Normalize(spMid - spEye), spEye - Vector3.UnitY * 1.6f);
        combat.Retune(spTuning);
        combat.Reset();
        combat.Rebuild(file with { Skills = new Dictionary<string, int>(file.Skills) { ["focus"] = 1, ["extra_mana"] = 3 } });
        combat.Orc.Hold = true;
        InHand(DarkMessiah.Lab.Magic.LightningRules.Spell, 5);
        var crate = new Vector3(2.5f, 0, 6);
        var hidden = crate with { Z = 9 };
        float Bolted(Vector3 eye)
        {
            combat.Orc.Spawn();
            combat.Mana.Fill();
            for (int i = 0; i < (int)(2.5f / Tick); i++)
            {
                combat.Orc.PlaceAt(hidden);
                combat.Step(Tick, step++, i < 1, false, false, eye, Vector3.Normalize(hidden + Vector3.UnitY * .9f - eye), eye - Vector3.UnitY * 1.6f);
            }
            return combat.Orc.Taken;
        }
        float behindCrate = Bolted(crate with { Y = 1.6f, Z = 3 }), besideCrate = Bolted(new Vector3(5.5f, 1.6f, 3));
        // Three blows in 1.3 s (0.17, 0.68, 1.19): 39, all but the last 1 of their 35.
        Check(MathF.Abs(areaFirst - MathF.Min(3 * ir.Effect, 34)) < .01f && MathF.Abs(areaMate - MathF.Min(3 * ir.Effect, 34)) < .01f && behindCrate == 0 && MathF.Abs(besideCrate - 2 * lr.Effect) < .01f,
            FormattableString.Invariant($"the inferno is an area (R10166-1): held 1.3 s at the orc and its squad-mate side by side, they took {areaFirst:0.#} and {areaMate:0.#} (13 a blow, three blows, all but 1); a bolt at the orc behind the big crate took {behindCrate:0.#}, from beside it {besideCrate:0.#} (R10166-3)"));
        combat.UseBelt(5);
        combat.UseBelt(7);
        combat.Rebuild(file);
        combat.Orc.Hold = spWasHeld;
        ((IReplayBody)product).Begin();

        var spellRecordings = new[] { "l01_a-v18-attack-spells", "l01_a-v18-attack-spells-2" }.Select(n => Path.Combine(repo!, "local", "recordings", "experiments", n + ".rbrec")).Where(File.Exists).ToList();
        if (spellRecordings.Count > 0)
        {
            // The second run's orcs were frozen with ai_disable: they play no reaction, so its bolts' stuns aren't asked.
            var casts = spellRecordings.SelectMany(path => AttackSpellCalibration.Measure(path).Select(c => (Cast: c, Reacts: !path.EndsWith("-2.rbrec", StringComparison.Ordinal))))
                .Select(x => x.Cast with { Clips = x.Reacts ? x.Cast.Clips : [(x.Cast.Pressed, "(frozen with ai_disable)")] }).ToList();
            foreach (var c in casts) Console.WriteLine($"  cast {c}");
            // Lightning: each strike 1.8 s after its press, 8 mana as it struck, 15 asked and twice that taken but the last 1, a stun.
            var bolts = casts.Where(c => c.Spell == DarkMessiah.Lab.Magic.LightningRules.Spell && c.Hits.Count > 0).ToList();
            bool boltsRight = bolts.All(c => MathF.Abs((float)(c.Hits[0].At - c.Pressed) - lr.FireSeconds) <= .1f
                && c.Mana.Any(m => Math.Abs(m.At - c.Hits[0].At) <= .2 && MathF.Abs(m.Drop - lr.Cost) <= .5f)
                && c.Hits[0].Asked == lr.Effect && MathF.Abs(c.Hits[0].Taken - MathF.Min(2 * lr.Effect, c.Hits[0].Before - 1)) < .01f
                && c.Clips.Any(k => k.Clip.StartsWith("hit_stunned", StringComparison.OrdinalIgnoreCase) || k.Clip.StartsWith("Hit_electrocuted", StringComparison.OrdinalIgnoreCase) || k.Clip.StartsWith("(frozen", StringComparison.Ordinal)));
            // Inferno: 20 at the press, its first blow 0.17 s on and every 0.51 s after, 13 asked, none taken past 1; 15 a second from 1.5 s.
            var infernos = casts.Where(c => c.Spell == DarkMessiah.Lab.Magic.InfernoRules.Spell && c.Hits.Count > 2).ToList();
            bool infernosRight = infernos.All(c => c.Mana.Any(m => m.At - c.Pressed <= .05 && MathF.Abs(m.Drop - ir.Cost) <= .5f)
                && MathF.Abs((float)(c.Hits[0].At - c.Pressed) - ir.FirstSeconds) <= .04f
                && c.Hits.Zip(c.Hits.Skip(1)).All(p => MathF.Abs((float)(p.Second.At - p.First.At) - ir.EverySeconds) <= .045f)
                && c.Hits.All(h => h.Asked == ir.Effect && MathF.Abs(h.Taken - MathF.Max(0, MathF.Min(ir.Effect, h.Before - 1))) < .01f)
                && MathF.Abs(c.Mana.Where(m => m.At - c.Pressed > ir.UpkeepFrom - .05 && m.At <= c.Released + .05).Sum(m => m.Drop) - ir.Maintain * (float)(c.Released - c.Pressed - ir.UpkeepFrom)) <= 1.5f);
            // Freeze: 15 as it's cast; the orc it met stood (no clip begun) its 7 s from the bolt, 0.27 s after the press.
            var freezes = casts.Where(c => c.Spell == DarkMessiah.Lab.Magic.FreezeRules.Spell).ToList();
            bool freezeCosts = freezes.All(c => c.Mana.Any(m => m.At - c.Pressed <= .1 && MathF.Abs(m.Drop - fr.Cost) <= .5f));
            var stood = freezes.Select(c => c.Clips.FirstOrDefault(k => k.At > c.Pressed + .3).At - c.Pressed).Where(s => s > 0).ToList();
            // Deaths after a blow that left 1 (R10166-2): an orc with its AI on died as its reaction played out, 1.7 s after a
            // bolt and 2.4 s after the inferno's blow; one frozen with ai_disable played none (the second run) and is left out.
            var lethal = casts.Where(c => !double.IsNaN(c.Died) && c.Hits.Any(h => h.Taken > 0 && h.Taken < h.Asked * (c.Spell == DarkMessiah.Lab.Magic.LightningRules.Spell ? 2 : 1) - .01f)
                && !c.Clips.Any(k => k.Clip.StartsWith("(frozen", StringComparison.Ordinal)))
                .Select(c => (c.Spell, After: c.Died - c.Hits.Last(h => h.Taken > 0).At)).ToList();
            bool deathsRight = lethal.All(l => Math.Abs(l.After - (l.Spell == DarkMessiah.Lab.Magic.LightningRules.Spell ? lr.DeathDelay : ir.BurnDeath)) <= .1);
            Console.WriteLine($"  lethal spell blows: {string.Join(", ", lethal.Select(l => FormattableString.Invariant($"{l.Spell} {l.After:0.00} s")))}");
            Check(lethal.Count >= 2 && deathsRight,
                FormattableString.Invariant($"the donor's orcs left at 1 by a spell died {string.Join(", ", lethal.Select(l => $"{l.After:0.00} s after ({l.Spell})"))} (the lab's {lr.DeathDelay:0.0} for the bolt, {ir.BurnDeath:0.0} for the inferno; R10166-2)"));
            Check(bolts.Count >= 4 && boltsRight && infernos.Count >= 2 && infernosRight && freezes.Count >= 2 && freezeCosts && stood.Any(s => (float)s - fr.CastSeconds - fr.FrozenSeconds is >= -.1f and <= .3f),
                FormattableString.Invariant($"the donor's attack spells: {bolts.Count} lightning bolts struck {string.Join(", ", bolts.Select(c => (c.Hits[0].At - c.Pressed).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s after the press for {lr.Cost:0} mana, asking 15 and taking 30 or all but 1, the orc stunned; {infernos.Count} infernos cost 20 at the press and 15 a second from 1.5 s, burning 13 from 0.17 s every 0.51 s; {freezes.Count} freezes cost 15, and an orc it met stood {string.Join(", ", stood.Select(s => s.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} s ({fr.FrozenSeconds:0})"));
        }
        else Console.WriteLine("skip the donor's attack spells: no local/recordings/experiments/l01_a-v18-attack-spells.rbrec");
    }
    // The support spells (#10167; docs/lab.md, "Support spells"), on a belt of their own. Cure wounds is cast as its key
    // puts it in hand, for 10 mana: 5 at once, the other 10 a point at a time over 2.6 s. Weaken, pressed at the orc,
    // costs 15 and leaves it wounded 15 s, striking only with its wounded attacks (HitPow 0.5: the cleaver's 8 asks 4).
    // Charm costs 15 and turns the orc on its squad-mate, and the mate on it, for 25 s: neither strikes the player.
    // Against l01_a-v18-support-spells and -2.
    {
        float ssmpu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        var support = file with
        {
            Skills = new Dictionary<string, int>(file.Skills) { ["focus"] = 1, ["extra_mana"] = 3 },
            Belt = [new BeltSlot(Spell: DarkMessiah.Lab.Magic.CureRules.Spell), new BeltSlot(Spell: DarkMessiah.Lab.Magic.WeakenRules.Spell), new BeltSlot(Spell: DarkMessiah.Lab.Magic.CharmRules.Spell)],
        };
        bool ssWasHeld = combat.Orc.Hold, ssWasGod = combat.God;
        var ssEye = new Vector3(12, 1.6f, 6);
        var ssOrc = new Vector3(12, 0, 6 + 200 * ssmpu);
        var ssFar = new Vector3(12, 0, 6 + 4000 * ssmpu);
        var ssFeet = ssEye - Vector3.UnitY * 1.6f;
        combat.Rebuild(support);
        // Cure wounds at 10 health, the orc held far off: the heal at once, and each point after it.
        combat.Orc.Hold = true;
        combat.Orc.Spawn();
        combat.Health.Set(10);
        combat.Mana.Fill();
        float cureMana = combat.Mana.Value;
        combat.UseBelt(0);
        float cureAtOnce = combat.PlayerHealth - 10, cureCost = cureMana - combat.Mana.Value;
        bool cureLeftHand = combat.SpellInHand is null;
        var curePoints = new List<float>();
        float cureLast = combat.PlayerHealth;
        for (int i = 0; i < (int)(4 / Tick); i++)
        {
            combat.Orc.PlaceAt(ssFar);
            combat.Step(Tick, step++, false, false, false, ssEye, Vector3.UnitZ, ssFeet);
            if (combat.PlayerHealth > cureLast + .5f) curePoints.Add((i + 1) * Tick);
            cureLast = combat.PlayerHealth;
        }
        var cr = combat.Cure;
        float cureTotal = combat.PlayerHealth - 10;
        bool cureOnTime = curePoints.Count == DarkMessiah.Lab.Magic.CureRules.PointSeconds.Length
            && curePoints.Zip(DarkMessiah.Lab.Magic.CureRules.PointSeconds).All(p => p.First - p.Second is >= 0 and <= 1.01f * Tick);
        Check(cureLeftHand && MathF.Abs(cureCost - cr.Cost) < .05f && MathF.Abs(cureAtOnce - cr.AtOnce) < .01f && MathF.Abs(cureTotal - cr.Effect) < .01f && cureOnTime,
            FormattableString.Invariant($"cure wounds: cast from belt slot 1 as it's put in hand (the hand keeps its weapon: {cureLeftHand}), {cureCost:0.#} mana ({cr.Cost:0}); {cureAtOnce:0.#} at once and {cureTotal:0.#} in all ({cr.Effect:0}), a point at {string.Join(", ", curePoints.Select(p => p.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s"));
        // Weaken at the orc 200 units off, then 20 s of its fight against the player (god: the blows asked, none taken).
        combat.God = true;
        combat.Orc.Hold = false;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(ssOrc);
        combat.Mana.Fill();
        // The gauge empty: full, the cast shrinks (#10348).
        combat.Adrenaline.Set(0, DarkMessiah.Lab.Sword.PlayerAdrenaline.ShippedMax);
        combat.UseBelt(1);
        bool weakenInHand = combat.SpellInHand == DarkMessiah.Lab.Magic.WeakenRules.Spell;
        float weakenMana = combat.Mana.Value;
        combat.Log = [];
        var wr = combat.Weaken;
        var weakenedAttacks = new List<(float At, string Clip)>();
        bool walkedHurt = false;
        float weakenCost = float.NaN, weakenedFor = float.NaN;
        for (int i = 0; i < (int)(25 / Tick); i++)
        {
            var was = (combat.Orc.State, combat.Orc.Clip);
            combat.Step(Tick, step++, i < 1, false, false, ssEye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY * 1.1f - ssEye), ssFeet);
            if (i == 0) { weakenCost = weakenMana - combat.Mana.Value; weakenedFor = combat.Orc.WeakenedFor; }
            if (combat.Orc.State == DarkMessiah.Lab.Npc.OrcState.Attack && (was.State != DarkMessiah.Lab.Npc.OrcState.Attack || was.Clip != combat.Orc.Clip))
                weakenedAttacks.Add(((i + 1) * Tick, combat.Orc.Clip));
            walkedHurt |= combat.Orc.WeakenedFor > 0 && combat.Orc.Clip == "Hurtwalk";
        }
        var weakenBlows = combat.Log.Select(e => (At: (float)(e.Tick - (ulong)(step - (int)(25 / Tick))) * Tick, e.Move, e.Damage)).ToList();
        combat.Log = null;
        var duringWeaken = weakenedAttacks.Where(a => a.At < wr.Seconds).ToList();
        var afterWeaken = weakenedAttacks.Where(a => a.At > wr.Seconds + 1).ToList();
        float WoundedAsk(string clip) => combat.Model.Damage(new DarkMessiah.Combat.DamageCase(0, new DarkMessiah.Combat.Party("player", "", [], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel),
            new DarkMessiah.Combat.Party("npc_orc_sword", clip, [], .5f, "#2", DarkMessiah.Lab.Npc.Orc.Model), "weapon_arxorccleaver", null, 0x4, 0, 0, 50, 50, false, 0, DarkMessiah.Lab.Npc.Orc.Cleaver)) ?? float.NaN;
        Check(weakenInHand && MathF.Abs(weakenCost - wr.Cost) < .05f && MathF.Abs(weakenedFor - wr.Seconds) < .05f && walkedHurt && duringWeaken.Count >= 2
                && duringWeaken.All(a => DarkMessiah.Lab.Npc.OrcMindData.IsWounded(a.Clip)) && afterWeaken.All(a => !DarkMessiah.Lab.Npc.OrcMindData.IsWounded(a.Clip)) && afterWeaken.Count >= 1
                && weakenBlows.Where(b => b.At < wr.Seconds).All(b => MathF.Abs(b.Damage - 4) < .01f) && weakenBlows.Any(b => b.At < wr.Seconds),
            FormattableString.Invariant($"weaken: in hand from belt slot 2 ({weakenInHand}), {weakenCost:0.#} mana at the press ({wr.Cost:0}); the orc weakened {weakenedFor:0.#} s ({wr.Seconds:0}), walking in Hurtwalk ({walkedHurt}), its attacks {string.Join(" ", duringWeaken.Select(a => a.Clip))} then {string.Join(" ", afterWeaken.Select(a => a.Clip))}; its blows while weakened asked {string.Join(", ", weakenBlows.Where(b => b.At < wr.Seconds).Select(b => b.Damage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))} (attack_wounded_2 asks {WoundedAsk("attack_wounded_2"):0.#})"));
        // Charm at the orc with its squad-mate beside it, 30 s: they fight each other for 25 s, the player left alone.
        var ssTuning = combat.Tuning;
        var ssMateAt = ssOrc + Vector3.UnitX * 1.5f;
        // No respawn: a charmed orc that falls stays down (one back on its feet would be on its own side).
        combat.Retune(ssTuning with { Orc = ssTuning.Orc with { SquadMate = [ssMateAt.X, ssMateAt.Y, ssMateAt.Z], RespawnSeconds = 1000 } });
        combat.Reset();
        combat.Rebuild(support);
        combat.God = true;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(ssOrc);
        combat.Mate!.Spawn();
        combat.Mate.PlaceAt(ssMateAt);
        combat.Mana.Fill();
        combat.UseBelt(2);
        bool charmInHand = combat.SpellInHand == DarkMessiah.Lab.Magic.CharmRules.Spell;
        float charmMana = combat.Mana.Value, charmCost = float.NaN, charmedFor = float.NaN;
        var chr = combat.Charm;
        int playerBlowsCharmed = 0, playerBlowsAfter = 0;
        float charmEnded = float.NaN;
        for (int i = 0; i < (int)(35 / Tick); i++)
        {
            int blowsBefore = combat.OrcStats.Blows;
            combat.Step(Tick, step++, i < 1, false, false, ssEye, Vector3.Normalize(ssOrc + Vector3.UnitY * 1.1f - ssEye), ssFeet);
            if (i == 0) { charmCost = charmMana - combat.Mana.Value; charmedFor = combat.Orc.CharmedFor; }
            if (float.IsNaN(charmEnded) && i > 0 && combat.Orc.CharmedFor <= 0) charmEnded = (i + 1) * Tick;
            if (float.IsNaN(charmEnded)) playerBlowsCharmed += combat.OrcStats.Blows - blowsBefore;
            else playerBlowsAfter += combat.OrcStats.Blows - blowsBefore;
        }
        var fights = combat.OrcFights;
        Check(charmInHand && MathF.Abs(charmCost - chr.Cost) < .05f && MathF.Abs(charmedFor - chr.Seconds) < .05f && MathF.Abs(charmEnded - chr.Seconds) <= 2 * Tick
                && playerBlowsCharmed == 0 && fights.Landed >= 1 && fights.Dealt > 0 && playerBlowsAfter >= 1,
            FormattableString.Invariant($"charm: in hand from belt slot 3 ({charmInHand}), {charmCost:0.#} mana at the press ({chr.Cost:0}); the orc charmed {charmedFor:0.#} s, to {charmEnded:0.00} s ({chr.Seconds:0}); meanwhile the orcs struck each other {fights.Blows} times, {fights.Landed} landing for {fights.Dealt:0.#}, and the player {playerBlowsCharmed} times; after, the player {playerBlowsAfter}"));
        // Both charmed (R10167-1): sides by mm_relationship.txt, the charmed on the player's. The orc charmed, then its mate
        // 2 s on: from then neither strikes the other nor the player; their charms over, both fight the player.
        combat.Reset();
        combat.Rebuild(support);
        combat.God = true;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(ssOrc);
        combat.Mate!.Spawn();
        combat.Mate.PlaceAt(ssMateAt);
        combat.Orc.Hold = combat.Mate.Hold = false;
        combat.Mana.Fill();
        if (combat.SpellInHand != DarkMessiah.Lab.Magic.CharmRules.Spell) combat.UseBelt(2);
        int bothFightsFrom = 0, bothPlayerFrom = 0, bothPlayerAfter = 0, fightsDuring = 0, playerDuring = 0;
        float bothCharmedUntil = float.NaN;
        for (int i = 0; i < (int)(40 / Tick); i++)
        {
            float t = (i + 1) * Tick;
            // Its press at the orc, then at the mate (each in view: 17° apart at 200 units).
            var at = t < 1 ? combat.Orc.Feet : combat.Mate.Feet;
            bool press = i == 0 || i == (int)(2 / Tick);
            if (i == (int)(2 / Tick) + 1) (bothFightsFrom, bothPlayerFrom) = (combat.OrcFights.Blows, combat.OrcStats.Blows);
            combat.Step(Tick, step++, press, false, false, ssEye, Vector3.Normalize(at + Vector3.UnitY * 1.1f - ssEye), ssFeet);
            if (float.IsNaN(bothCharmedUntil) && i > (int)(2 / Tick) && (combat.Orc.CharmedFor <= 0 || combat.Mate.CharmedFor <= 0)) bothCharmedUntil = t;
            if (float.IsNaN(bothCharmedUntil) && i > (int)(2 / Tick)) (fightsDuring, playerDuring) = (combat.OrcFights.Blows - bothFightsFrom, combat.OrcStats.Blows - bothPlayerFrom);
            if (t > 27 + 2) bothPlayerAfter = combat.OrcStats.Blows - bothPlayerFrom - playerDuring;
            if (Environment.GetEnvironmentVariable("RB_CHARMTRACE") == "1" && i % (int)(1 / Tick) == 0)
                Console.WriteLine(FormattableString.Invariant($"  charm trace {t:0.0} s: orc {combat.Orc.State} {combat.Orc.Clip} charmed {combat.Orc.CharmedFor:0.0} hp {combat.Orc.Health:0} at {combat.Orc.Feet}; mate {combat.Mate.State} {combat.Mate.Clip} charmed {combat.Mate.CharmedFor:0.0} hp {combat.Mate.Health:0}; blows on the player {combat.OrcStats.Blows}, between {combat.OrcFights.Blows}"));
        }
        bool bothCharmed = !float.IsNaN(bothCharmedUntil) && bothCharmedUntil > 24;
        // The second orc charmed after the first: once the first's ended, the second (still charmed) and the first are of
        // other sides; both charms over, both on their own, the player's foes.
        Check(bothCharmed && fightsDuring == 0 && playerDuring == 0 && bothPlayerAfter >= 1,
            FormattableString.Invariant($"both charmed (R10167-1): from the mate's charm to the first's end ({bothCharmedUntil:0.0} s) the orcs struck each other {fightsDuring} times and the player {playerDuring}; their charms over, the player {bothPlayerAfter} times"));
        // Cast on an unaware orc (R10167-2): weakened, it leaves its idle and fights the player, as it does past its 15 s;
        // charmed, with its unaware mate beside it, it fights the mate.
        combat.Retune(ssTuning with { Orc = ssTuning.Orc with { SpawnUnaware = true } });
        combat.Reset();
        combat.Rebuild(support);
        combat.God = true;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(ssOrc);
        bool quietBefore = combat.Orc.State == DarkMessiah.Lab.Npc.OrcState.Quiet;
        combat.Mana.Fill();
        if (combat.SpellInHand != DarkMessiah.Lab.Magic.WeakenRules.Spell) combat.UseBelt(1);
        int quietBlowsBefore = combat.OrcStats.Blows, quietBlowsLate = 0;
        bool leftIdle = false;
        for (int i = 0; i < (int)(25 / Tick); i++)
        {
            combat.Step(Tick, step++, i < 1, false, false, ssEye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY * 1.1f - ssEye), ssFeet);
            if (i == 1) leftIdle = combat.Orc.State != DarkMessiah.Lab.Npc.OrcState.Quiet;
            if ((i + 1) * Tick > wr.Seconds + 1 && quietBlowsLate == 0) quietBlowsLate = -combat.OrcStats.Blows;
        }
        quietBlowsLate += combat.OrcStats.Blows;
        combat.Retune(ssTuning with { Orc = ssTuning.Orc with { SpawnUnaware = true, SquadMate = [ssMateAt.X, ssMateAt.Y, ssMateAt.Z], RespawnSeconds = 1000 } });
        combat.Reset();
        combat.Rebuild(support);
        combat.God = true;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(ssOrc);
        combat.Mate!.Spawn();
        combat.Mate.PlaceAt(ssMateAt);
        combat.Mana.Fill();
        if (combat.SpellInHand != DarkMessiah.Lab.Magic.CharmRules.Spell) combat.UseBelt(2);
        int quietFights = combat.OrcFights.Landed;
        for (int i = 0; i < (int)(20 / Tick); i++)
            combat.Step(Tick, step++, i < 1, false, false, ssEye, Vector3.Normalize(ssOrc + Vector3.UnitY * 1.1f - ssEye), ssFeet);
        quietFights = combat.OrcFights.Landed - quietFights;
        Check(quietBefore && leftIdle && quietBlowsLate >= 1 && quietFights >= 1,
            FormattableString.Invariant($"cast on an unaware orc (R10167-2): weakened, it left its idle ({leftIdle}) and struck the player {quietBlowsLate} times past its {wr.Seconds:0} s; charmed beside its unaware mate, their blows on each other landed {quietFights} times"));
        combat.Retune(ssTuning);
        combat.Reset();
        combat.Rebuild(file);
        combat.God = ssWasGod;
        combat.Orc.Hold = ssWasHeld;
        ((IReplayBody)product).Begin();

        var supportRecordings = new[] { "l01_a-v18-support-spells", "l01_a-v18-support-spells-2" }.Select(n => Path.Combine(repo!, "local", "recordings", "experiments", n + ".rbrec")).Where(File.Exists).ToList();
        if (supportRecordings.Count == 2)
        {
            var measured = supportRecordings.Select(SupportSpellCalibration.Measure).ToList();
            var cures = measured.SelectMany(m => m.Cures).ToList();
            var supportCasts = measured.SelectMany(m => m.Casts).ToList();
            foreach (var c in cures) Console.WriteLine($"  {c}");
            foreach (var c in supportCasts) Console.WriteLine($"  {c}");
            // Cures: 10 mana and 5 at once, then each point at the rules' times (within 1.5 ticks).
            bool curesRight = cures.All(c => MathF.Abs(c.Mana - cr.Cost) <= .5f && MathF.Abs(c.Heals[0].Amount - cr.AtOnce) < .01f && c.Heals.Count >= 9
                && c.Heals.Skip(1).Select((h, k) => (h, k)).All(x => MathF.Abs(x.h.Amount - 1) < .01f && Math.Abs(x.h.After - DarkMessiah.Lab.Magic.CureRules.PointSeconds[x.k]) <= .0225));
            // Weaken: 15 mana; the orc's blows on the player in its 15 s were wounded attacks asking the model's (4), after it
            // the others (8, 12); its wounded clips ran past 15 s, none begun after 16.
            float OrcAsks(SpellBlowAfter b) => combat.Model.Damage(new DarkMessiah.Combat.DamageCase(0,
                b.OnPlayer ? new DarkMessiah.Combat.Party("player", "", [], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel) : new DarkMessiah.Combat.Party("npc_orc_sword", "idle", [], 0, "#3", DarkMessiah.Lab.Npc.Orc.Model),
                new DarkMessiah.Combat.Party("npc_orc_sword", b.Clip, [], b.Cycle, "#2", DarkMessiah.Lab.Npc.Orc.Model), "weapon_arxorccleaver", null, b.Type, 0, 0, 50, 50, false, 0, DarkMessiah.Lab.Npc.Orc.Cleaver)) ?? float.NaN;
            var weakens = supportCasts.Where(c => c.Spell == DarkMessiah.Lab.Magic.WeakenRules.Spell).ToList();
            static bool WoundedClip(string clip) => clip.Contains("ounded", StringComparison.Ordinal) || clip == "Hurtwalk";
            var weakenedBlows = weakens.SelectMany(c => c.Blows.Where(b => b.OnPlayer && b.After < wr.Seconds)).ToList();
            // After it, its attacks' blows (one landed as it walked on, CombatWalk, which the model reads by its last attack: not asked here).
            var laterBlows = weakens.SelectMany(c => c.Blows.Where(b => b.OnPlayer && b.After > wr.Seconds + 1 && b.Clip.StartsWith("attack", StringComparison.Ordinal))).ToList();
            bool weakensRight = weakens.Count == 2 && weakens.All(c => MathF.Abs(c.Mana - wr.Cost) <= .5f) && weakenedBlows.Count >= 4
                && weakenedBlows.All(b => DarkMessiah.Lab.Npc.OrcMindData.IsWounded(b.Clip) && MathF.Abs(b.Asked - OrcAsks(b)) < .01f && b.Asked == 4)
                && laterBlows.Count >= 3 && laterBlows.All(b => !DarkMessiah.Lab.Npc.OrcMindData.IsWounded(b.Clip) && MathF.Abs(b.Asked - OrcAsks(b)) < .01f);
            var lastWounded = weakens.Select(c => c.Last(WoundedClip)).Where(t => !double.IsNaN(t)).ToList();
            // The player's sword on the weakened orc (-2) asked what it asks of any orc: weaken_reduction_factor 1.
            var swordOnWeakened = DamageCalibration.Measure([supportRecordings[1]], combat, SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning));
            foreach (var d in swordOnWeakened) Console.WriteLine($"  sword on the weakened orc: {d}");
            // Charm: 15 mana; within its 25 s no orc struck the player, the other orc's blows were all on the charmed one, those
            // in an attack clip asking the model's (one landed as it moved off, read by its last attack); the charmed orc
            // idled again 25 s on (within half a second).
            var charms = supportCasts.Where(c => c.Spell == DarkMessiah.Lab.Magic.CharmRules.Spell && c.Blows.Any(b => !b.OnPlayer)).ToList();
            bool charmsRight = charms.Count >= 1 && charms.All(c => MathF.Abs(c.Mana - chr.Cost) <= .5f && !c.Blows.Any(b => b.OnPlayer && b.After < chr.Seconds)
                && c.Blows.Where(b => !b.OnPlayer).All(b => b.Victim == c.Target && (!b.Clip.StartsWith("attack", StringComparison.Ordinal) || MathF.Abs(b.Asked - OrcAsks(b)) < .01f))
                && c.Blows.Any(b => !b.OnPlayer && b.Clip.StartsWith("attack", StringComparison.Ordinal))
                && Math.Abs(c.First(5, k => k.StartsWith("idle", StringComparison.Ordinal)) - chr.Seconds) <= .5);
            Check(cures.Count >= 2 && curesRight && weakensRight && lastWounded.Count >= 1 && lastWounded.All(t => t >= wr.Seconds - 1 && t <= wr.Seconds + 1) && charmsRight
                    && swordOnWeakened.Count >= 2 && swordOnWeakened.All(d => d.Agrees),
                FormattableString.Invariant($"the donor's support spells: {cures.Count} cures took {cr.Cost:0} mana and gave {cr.AtOnce:0} at once, then a point at the rules' times; {weakens.Count} weakens cost {wr.Cost:0}, the orc's {weakenedBlows.Count} blows in their {wr.Seconds:0} s wounded attacks asking 4 (the model's), {laterBlows.Count} after the others, the wounded clips last begun {string.Join(", ", lastWounded.Select(t => t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} s on, the player's {swordOnWeakened.Count} sword blows on it the lab's ({swordOnWeakened.Count(d => d.Agrees)} agree); {charms.Count} charm cost {chr.Cost:0}, no orc struck the player in its {chr.Seconds:0} s, the other orc's {charms.Sum(c => c.Blows.Count(b => !b.OnPlayer))} blows were on the charmed one, those struck in an attack clip asking the model's, and it idled again {string.Join(", ", charms.Select(c => c.First(5, k => k.StartsWith("idle", StringComparison.Ordinal)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))} s on"));
        }
        else Console.WriteLine("skip the donor's support spells: no local/recordings/experiments/l01_a-v18-support-spells.rbrec and -2");
    }
    // Shrink (#10348; docs/lab.md, "Shrink"): weaken pressed with the gauge full. 15 mana, the gauge to air_4's 2, the
    // orc's health to 1; its size falls from 0.96 s, 0.03 every two ticks to 0.2, and at 1.95 s it's small (0.3), standing
    // in combatidle, striking nothing, its health climbing 0.25 every 2 s. The player's body 26 units from it crushes it;
    // a kick kills it. Against l01_a-v19-shrink, -2 and -3.
    {
        float shmpu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        var shrinkFile = file with
        {
            Skills = new Dictionary<string, int>(file.Skills) { ["focus"] = 1, ["extra_mana"] = 3 },
            Belt = [new BeltSlot(Spell: DarkMessiah.Lab.Magic.WeakenRules.Spell)],
        };
        bool shWasHeld = combat.Orc.Hold, shWasGod = combat.God;
        var shEye = new Vector3(12, 1.6f, 6);
        var shOrc = new Vector3(12, 0, 6 + 200 * shmpu);
        var shFeet = shEye - Vector3.UnitY * 1.6f;
        var sr = combat.Shrink;
        combat.Reset();
        combat.Rebuild(shrinkFile);
        combat.God = true;
        combat.Orc.Hold = false;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(shOrc);
        float Tall(DarkMessiah.Lab.Npc.Orc o) => o.Targets() is { Count: > 0 } t ? t[0].Pose.Max(j => j.World.Translation.Y) - o.Feet.Y : float.NaN;
        float tallBefore = Tall(combat.Orc), hullBefore = combat.Orc.Hull.HalfExtents.Y;
        combat.Mana.Fill();
        combat.Adrenaline.Set(DarkMessiah.Lab.Sword.PlayerAdrenaline.ShippedMax, DarkMessiah.Lab.Sword.PlayerAdrenaline.ShippedMax);
        combat.UseBelt(0);
        float shMana = combat.Mana.Value, shCost = float.NaN, shAdrenaline = float.NaN, shHealth = float.NaN;
        var shSizes = new List<(float At, float Size)>();
        float shSmallAt = float.NaN;
        int shBlows = combat.OrcStats.Blows;
        var shClips = new HashSet<string>();
        var shRises = new List<float>();
        float shLast = float.NaN;
        // 20 s, the player 200 units off and looking at it (god: what it strikes is counted, none taken).
        for (int i = 0; i < (int)(20 / Tick); i++)
        {
            combat.Step(Tick, step++, i < 1, false, false, shEye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY * .3f - shEye), shFeet);
            float t = (i + 1) * Tick;
            if (i == 0) (shCost, shAdrenaline, shHealth) = (shMana - combat.Mana.Value, combat.Adrenaline.Value, combat.Orc.Health);
            if (!combat.Orc.Shrunk && (shSizes.Count == 0 || shSizes[^1].Size != combat.Orc.Size)) shSizes.Add((t, combat.Orc.Size));
            if (float.IsNaN(shSmallAt) && combat.Orc.Shrunk) shSmallAt = t;
            if (combat.Orc.Shrunk) shClips.Add(combat.Orc.Clip);
            if (combat.Orc.Shrunk && !float.IsNaN(shLast) && combat.Orc.Health > shLast + .01f) shRises.Add(t);
            shLast = combat.Orc.Health;
        }
        shBlows = combat.OrcStats.Blows - shBlows;
        // Its size where the donor's was (the first of each value within a tick of the rules').
        bool sizesRight = shSizes.Count >= 27 && shSizes.All(s => MathF.Abs(s.Size - DarkMessiah.Lab.Magic.ShrinkRules.SizeAt(s.At)) < .001f)
            && shSizes.Any(s => MathF.Abs(s.Size - DarkMessiah.Lab.Magic.ShrinkRules.Floor) < .001f);
        float tallAfter = Tall(combat.Orc), hullAfter = combat.Orc.Hull.HalfExtents.Y;
        float meanRise = shRises.Count > 2 ? (shRises[^1] - shRises[0]) / (shRises.Count - 1) : float.NaN;
        Check(MathF.Abs(shCost - sr.Cost) < .05f && MathF.Abs(shAdrenaline - sr.AdrenalineAdd) < .01f && shHealth == 1 && sizesRight
                && MathF.Abs(shSmallAt - DarkMessiah.Lab.Magic.ShrinkRules.SwapSeconds) <= 1.01f * Tick && shBlows == 0 && shClips.SetEquals(["combatidle"])
                && combat.Orc.Placement.Scale == Vector3.One * DarkMessiah.Lab.Magic.ShrinkRules.ShrunkScale
                && MathF.Abs(tallAfter / tallBefore - DarkMessiah.Lab.Magic.ShrinkRules.ShrunkScale) < .05f && MathF.Abs(hullAfter / hullBefore - DarkMessiah.Lab.Magic.ShrinkRules.ShrunkScale) < .01f
                && MathF.Abs(meanRise - DarkMessiah.Lab.Npc.Orc.RegenSeconds) < .05f && shRises.Count >= 8,
            FormattableString.Invariant($"shrink: weaken with the gauge full cost {shCost:0.#} mana ({sr.Cost:0}), left the gauge at {shAdrenaline:0.#} ({sr.AdrenalineAdd:0}) and the orc at {shHealth:0.##} health; its size {shSizes.Count} values from {shSizes.ElementAtOrDefault(1).At:0.00} s to {shSizes[^1].Size:0.##} at {shSizes[^1].At:0.00} s (the rules': {sizesRight}), small at {shSmallAt:0.00} s ({DarkMessiah.Lab.Magic.ShrinkRules.SwapSeconds:0.00}): drawn at {combat.Orc.Placement.Scale.X:0.##}, {tallAfter / tallBefore:0.00} as tall posed (in combatidle, from its approach), its box {hullAfter / hullBefore:0.00}; in {string.Join("/", shClips)} it struck {shBlows} times in 20 s, its health rising {shRises.Count} times, every {meanRise:0.00} s"));
        // The player walked at it from 60 units: crushed at 26 (within a step), not before; another kicked, the spell in
        // hand as the donor's was.
        var walkFrom = combat.Orc.Feet - Vector3.UnitZ * 60 * shmpu;
        float stompedAt = float.NaN;
        int stompsBefore = combat.Stomped;
        for (int i = 0; i <= 60 && float.IsNaN(stompedAt); i++)
        {
            var at = walkFrom + Vector3.UnitZ * i * shmpu;
            combat.Step(Tick, step++, false, false, false, at + Vector3.UnitY * 1.6f, Vector3.UnitZ, at);
            if (!combat.Orc.Alive) stompedAt = ((combat.Orc.Feet - at) with { Y = 0 }).Length() / shmpu;
        }
        bool crushed = combat.Stomped == stompsBefore + 1 && stompedAt <= DarkMessiah.Lab.Magic.ShrinkRules.StompUnits && stompedAt > DarkMessiah.Lab.Magic.ShrinkRules.StompUnits - 1.01f;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(shOrc);
        combat.Mana.Fill();
        combat.Adrenaline.Set(DarkMessiah.Lab.Sword.PlayerAdrenaline.ShippedMax, DarkMessiah.Lab.Sword.PlayerAdrenaline.ShippedMax);
        for (int i = 0; i < (int)(2.5f / Tick); i++)
            combat.Step(Tick, step++, i < 1, false, false, shEye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY * .3f - shEye), shFeet);
        bool smallForKick = combat.Orc.Shrunk;
        var kickFeet = combat.Orc.Feet - Vector3.UnitZ * 40 * shmpu;
        var kickEye = kickFeet + Vector3.UnitY * 1.6f;
        float kickedHealth = combat.Orc.Health;
        bool kickedDead = false;
        for (int i = 0; i < (int)(1.5f / Tick) && !kickedDead; i++)
        {
            combat.Step(Tick, step++, false, false, i < 2, kickEye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY * .2f - kickEye), kickFeet);
            kickedDead = !combat.Orc.Alive;
        }
        Check(crushed && smallForKick && kickedDead,
            FormattableString.Invariant($"stomp and kick: walked at the small orc, crushed {stompedAt:0.#} units off ({DarkMessiah.Lab.Magic.ShrinkRules.StompUnits:0}; {combat.Stomped - stompsBefore} stomps); another, small ({smallForKick}) at {kickedHealth:0.##} health, kicked from 40 units: dead ({kickedDead})"));
        combat.Reset();
        combat.Rebuild(file);
        combat.God = shWasGod;
        combat.Orc.Hold = shWasHeld;
        ((IReplayBody)product).Begin();

        var shrinkRecordings = new[] { "l01_a-v19-shrink", "l01_a-v19-shrink-2", "l01_a-v19-shrink-3" }.Select(n => Path.Combine(repo!, "local", "recordings", "experiments", n + ".rbrec")).Where(File.Exists).ToList();
        if (shrinkRecordings.Count == 3)
        {
            var shrinks = shrinkRecordings.SelectMany(ShrinkCalibration.Measure).ToList();
            foreach (var c in shrinks) Console.WriteLine($"  {c}");
            // Each: 15 mana (its regeneration aside), health 1; its size the rules' within two ticks (the cast's own tick
            // read late once), small at 1.95 s; then combatidle (a ragdoll clip first), no blow, its health rising every
            // 2 s on average. The gauge: full after a game's first, air_4's 2 after the others.
            bool SizesAgree(ShrinkCast c) => c.Sizes.Where(s => s.After >= 0).All(s =>
                Enumerable.Range(-2, 5).Any(k => MathF.Abs(DarkMessiah.Lab.Magic.ShrinkRules.SizeAt((float)s.After + k * Tick) - s.Size) < .001f));
            bool castsRight = shrinks.Count == 5 && shrinks.All(c => MathF.Abs(c.Mana - sr.Cost) <= 1 && c.HealthBefore > 1 && SizesAgree(c)
                && Math.Abs(c.Swapped - DarkMessiah.Lab.Magic.ShrinkRules.SwapSeconds) <= 2.01 * Tick && c.Blows == 0
                && c.SmallClips.All(k => k is "combatidle" or "ragdoll") && c.Rises.All(r => MathF.Abs(r.Amount - .25f) < .01f)
                && (c.Rises.Count < 3 || Math.Abs((c.Rises[^1].After - c.Rises[0].After) / (c.Rises.Count - 1) - DarkMessiah.Lab.Npc.Orc.RegenSeconds) < .05)
                && (c.AdrenalineAfter == c.AdrenalineBefore || MathF.Abs(c.AdrenalineAfter - sr.AdrenalineAdd) < .01f));
            var spent = shrinks.Where(c => c.AdrenalineAfter != c.AdrenalineBefore).ToList();
            // Its deaths: walked into, a kick-typed blow of 2 with no weapon, 26 units off; kicked, the kick's 1. Each killed it.
            var deaths = shrinks.Where(c => c.Death is not null).Select(c => c.Death!.Value).ToList();
            bool deathsRight = deaths.Count == 2 && deaths.All(d => d.Type == 0x40000)
                && deaths.Any(d => !d.Kick && MathF.Abs(d.Asked - sr.StompDamage) < .01f && MathF.Abs(d.Units - DarkMessiah.Lab.Magic.ShrinkRules.StompUnits) <= 1)
                && deaths.Any(d => d.Kick && d.Asked < d.HealthBefore);
            double longest = shrinks.Where(c => c.Death is null).Select(c => c.LastSmall).DefaultIfEmpty(0).Max();
            Check(castsRight && spent.Count >= 2 && deathsRight && longest >= 60,
                FormattableString.Invariant($"the donor's shrinks: {shrinks.Count} cost {sr.Cost:0} mana and set the orc's health to 1, its size the rules' and small at {string.Join(", ", shrinks.Select(c => c.Swapped.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s, no blow, its health rising 0.25 every 2 s; {spent.Count} left the gauge at {sr.AdrenalineAdd:0}; {deaths.Count(d => !d.Kick)} crushed by the player's body ({string.Join(", ", deaths.Where(d => !d.Kick).Select(d => d.Units.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))} units off, {sr.StompDamage:0} asked), {deaths.Count(d => d.Kick)} kicked dead; one not killed small for {longest:0} s"));
        }
        else Console.WriteLine("skip the donor's shrinks: no local/recordings/experiments/l01_a-v19-shrink.rbrec, -2 and -3");
    }
    // NPC archery (#10169; docs/lab.md, "NPC archery"): combat.json's orc.bow makes the first orc an archer (the maps'
    // npc_orc_bow, the long bow and the cleaver). Aware, it runs at the player to 471 units, nocks an arrow 1.2 s on and
    // every 2.16 s, lets it go 1.08 s after standing (1.2 s on the move) at the player's eye less 11 units, at 7311 − 6.29
    // units/s a unit off; each strikes the player's box for the model's 5. After 10 arrows standing (or 1) it moves off,
    // looses one more and rushes in, kicks with the bow (2.5), drops it, draws the cleaver and fights on as any orc. Out of
    // sight it rushes at once; nearer than 314 units it plays surprised and backs off to 350. Against l01_a-v18-npc-archery*.
    {
        float apu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        var arTuning = combat.Tuning;
        bool arWasHeld = combat.Orc.Hold, arWasGod = combat.God;
        var (arRay, arSurface) = (combat.WorldRay, combat.WorldSurface);
        // An open field: the arena's walls would stand between them at 900 units.
        (combat.WorldRay, combat.WorldSurface) = (null, null);
        combat.Retune(arTuning with { Orc = arTuning.Orc with { Bow = DarkMessiah.Lab.Npc.ArcheryData.LongBow, SquadMate = null, SpawnUnaware = false } });
        combat.Reset();
        combat.God = true;
        combat.Orc.Hold = false;
        var arOrcAt = new Vector3(0, 0, 0);
        // An archer's arrow drawn while it flies (R10169-1): the product's arrows, the player's and the archer's.
        bool archerArrowDrawn = false, archerArrowSeen = false;
        // One fight: the player's eye 900 units off (or as given), moving sideways at the given speed; the archer's arrows,
        // its phases and its blows each step, to the cleaver's third blow or the seconds given.
        (List<(float At, bool Standing, float Units)> Loosed, List<(float At, float Asked)> Struck, Dictionary<DarkMessiah.Lab.Npc.ArcheryPhase, float> Phases, float StoodAt,
            List<(float At, string Move, float Asked)> Blows, float SurprisedAt, float ClosestAfter) Fight(int? standing, float units = 900, float strafe = 0, float seconds = 45, Func<Vector3, Vector3, Vector3?>? world = null)
        {
            combat.WorldRay = world;
            combat.Orc.FixedStandingShots = standing;
            combat.Orc.Spawn();
            combat.Orc.PlaceAt(arOrcAt);
            var eye = arOrcAt + new Vector3(0, 63 * apu, units * apu);
            var loosed = new List<(float, bool, float)>();
            var struck = new List<(float, float)>();
            var phases = new Dictionary<DarkMessiah.Lab.Npc.ArcheryPhase, float>();
            var blows = new List<(float, string, float)>();
            float stoodAt = float.NaN, surprisedAt = float.NaN, closest = float.PositiveInfinity;
            combat.Log = [];
            int logged = 0, struckBefore = combat.OrcArchery.Struck;
            for (int i = 0; i < (int)(seconds / Tick); i++)
            {
                float t = (i + 1) * Tick;
                // Strafing as the donor's player did: from 4 s (npc-archery-4 waited that long first), 3 s each way.
                if (t > 4) eye += Vector3.UnitX * strafe * (MathF.Floor((t - 4) / 3) % 2 == 0 ? 1 : -1) * apu * Tick;
                combat.Step(Tick, step++, false, false, false, eye, Vector3.Normalize(combat.Orc.Feet + Vector3.UnitY - eye), eye - Vector3.UnitY * 63 * apu);
                float now = Vector2.Distance(new Vector2(eye.X, eye.Z), new Vector2(combat.Orc.Feet.X, combat.Orc.Feet.Z)) / apu;
                foreach (var shot in combat.Orc.Loosed) loosed.Add((t, shot.Standing, now));
                if (!archerArrowSeen && combat.OrcArrows.Count > 0)
                {
                    archerArrowSeen = true;
                    var drawnArrows = product.ArrowFacts().ToList();
                    archerArrowDrawn = drawnArrows.Count == combat.ArrowsInFlight.Count + combat.OrcArrows.Count
                        && combat.OrcArrows.All(a => drawnArrows.Any(f => Vector3.Distance(f.Transform.Translation, a.At) < 1e-3f));
                }
                for (; struckBefore < combat.OrcArchery.Struck; struckBefore++) struck.Add((t, combat.LastOrcArrow!.Value.Asked));
                phases.TryAdd(combat.Orc.Phase, t);
                if (float.IsNaN(stoodAt) && combat.Orc.Phase == DarkMessiah.Lab.Npc.ArcheryPhase.Stand) stoodAt = now;
                if (float.IsNaN(surprisedAt) && combat.Orc.Clip == "surprised") surprisedAt = now;
                if (!float.IsNaN(surprisedAt) && combat.Orc.Phase == DarkMessiah.Lab.Npc.ArcheryPhase.Stand) closest = MathF.Min(closest, now);
                for (; logged < combat.Log.Count; logged++) blows.Add((t, combat.Log[logged].Move, combat.Log[logged].Damage));
                if (combat.Orc.Clip == "drop_bow") phases.TryAdd(DarkMessiah.Lab.Npc.ArcheryPhase.Drop, t);
                if (blows.Count(b => b.Item2.StartsWith("attack", StringComparison.Ordinal)) >= 3) break;
            }
            combat.Log = null;
            return (loosed, struck, phases, stoodAt, blows, surprisedAt, closest);
        }
        var ad = DarkMessiah.Lab.Npc.ArcheryData.For(combat.Donor, DarkMessiah.Lab.Npc.ArcheryData.LongBow);
        float ArrowAsk(bool standing) => combat.Model.Damage(new DarkMessiah.Combat.DamageCase(0, new DarkMessiah.Combat.Party("player", "", [], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel),
            standing ? new DarkMessiah.Combat.Party("npc_orc_sword", DarkMessiah.Lab.Npc.ArcheryData.Shoot, [], 0, "#2", DarkMessiah.Lab.Npc.Orc.Model)
                : new DarkMessiah.Combat.Party("npc_orc_sword", "Run", [DarkMessiah.Lab.Npc.ArcheryData.ShootLayer], 0, "#2", DarkMessiah.Lab.Npc.Orc.Model),
            "weapon_mm_bow_long", null, 0x40000, 0, 0, 50, 50, false, 0, ad.Bow)) ?? float.NaN;
        string Times(IEnumerable<float> times) => string.Join(", ", times.Select(t => t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
        // Standing for 10, then moving off, one more and the rush.
        var full = Fight(10);
        var fullStanding = full.Loosed.Where(l => l.Standing).ToList();
        float fullKick = full.Phases.GetValueOrDefault(DarkMessiah.Lab.Npc.ArcheryPhase.Kick, float.NaN), fullDrop = full.Phases.GetValueOrDefault(DarkMessiah.Lab.Npc.ArcheryPhase.Drop, float.NaN),
            fullBlade = full.Phases.GetValueOrDefault(DarkMessiah.Lab.Npc.ArcheryPhase.Unsheathe, float.NaN);
        var kickBlow = full.Blows.FirstOrDefault(b => b.Move == DarkMessiah.Lab.Npc.ArcheryData.Kick);
        var bladeBlows = full.Blows.Where(b => b.Move.StartsWith("attack", StringComparison.Ordinal)).ToList();
        bool spacing = fullStanding.Zip(fullStanding.Skip(1)).All(p => MathF.Abs(p.Second.At - p.First.At - ad.Every) <= 1.5f * Tick);
        Check(full.Loosed.Count >= 11 && MathF.Abs(full.Loosed[0].At - ad.FirstNock - ad.LeaveMoving) <= 2.5f * Tick && !full.Loosed[0].Standing && fullStanding.Count == 10 && spacing
                && MathF.Abs(full.StoodAt - ad.StandOff) <= 3 && full.Struck.Count == full.Loosed.Count && full.Struck.All(s => MathF.Abs(s.Asked - 5) < .01f) && MathF.Abs(ArrowAsk(true) - 5) < .01f
                && MathF.Abs(fullKick - 33.2f) <= 1.5f && MathF.Abs(kickBlow.Asked - 2.5f) < .01f && fullDrop > fullKick && fullBlade > fullDrop && bladeBlows.Count >= 1 && bladeBlows.All(b => b.Asked > 0),
            FormattableString.Invariant($"the archer: its first arrow let go {(full.Loosed.Count > 0 ? full.Loosed[0].At : float.NaN):0.00} s after it woke, running ({ad.FirstNock:0.0} + {ad.LeaveMoving:0.0}); it stood {full.StoodAt:0} units off ({ad.StandOff:0}) and let go {fullStanding.Count} standing {ad.Every:0.00} s apart ({spacing}); {full.Struck.Count} of {full.Loosed.Count} struck, for {Times(full.Struck.Select(s => s.Asked).Distinct())} (the model's {ArrowAsk(true):0.#}); it kicked {fullKick:0.0} s after it woke (the donor's 33.1, 33.3) for {kickBlow.Asked:0.#}, dropped the bow {fullDrop - fullKick:0.00} s after (the donor's 0.96) and drew the cleaver {fullBlade - fullKick:0.00} s after (1.32); then {string.Join(", ", bladeBlows.Select(b => FormattableString.Invariant($"{b.Move} {b.Asked:0.#}")))}"));
        // Standing for 1 (2 of 9 recorded arrivals): the donor's two kicked 14.7 s after waking.
        var brief = Fight(1);
        float briefKick = brief.Phases.GetValueOrDefault(DarkMessiah.Lab.Npc.ArcheryPhase.Kick, float.NaN);
        Check(brief.Loosed.Count(l => l.Standing) == 1 && brief.Loosed.Count(l => l.At < briefKick) == 3 && MathF.Abs(briefKick - 14.7f) <= 1.5f && brief.Struck.Count == brief.Loosed.Count,
            FormattableString.Invariant($"the archer standing for one: {brief.Loosed.Count(l => l.At < briefKick)} arrows ({brief.Loosed.Count(l => l.Standing)} standing), all struck ({brief.Struck.Count}), and it kicked {briefKick:0.0} s after it woke (the donor's 14.7, 14.7)"));
        // The player strafing at 206 units/s (the donor's fastest) from 4 s, the archer standing 471 off: every arrow strikes, as the donor's did.
        var strafing = Fight(10, strafe: 206, seconds: 20);
        var strafedStanding = strafing.Loosed.Where(l => l.Standing).ToList();
        Console.WriteLine($"  strafed arrows: {string.Join(", ", strafing.Loosed.Select(l => FormattableString.Invariant($"{l.At:0.00} s {(l.Standing ? "standing" : "moving")} {l.Units:0}")))}; struck {string.Join(", ", strafing.Struck.Select(x => x.At.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
        Check(strafedStanding.Count >= 5 && strafing.Struck.Count == strafing.Loosed.Count,
            FormattableString.Invariant($"strafing at 206 units/s: {strafing.Struck.Count} of {strafing.Loosed.Count} arrows struck ({strafedStanding.Count} standing, at {Times(strafedStanding.Select(l => l.Units))} units)"));
        Check(archerArrowSeen && archerArrowDrawn, $"the archer's arrows are drawn as they fly (R10169-1): {archerArrowDrawn}");
        // Charmed (R10169-2), with its squad-mate 500 units the other way: it shoots the mate, not the player; the mate down,
        // with no one to fight it holds off, letting go nothing more.
        combat.Retune(arTuning with { Orc = arTuning.Orc with { Bow = DarkMessiah.Lab.Npc.ArcheryData.LongBow, SquadMate = [arOrcAt.X, arOrcAt.Y, arOrcAt.Z - 500 * apu], SpawnUnaware = false, RespawnSeconds = 1000 } });
        combat.Reset();
        combat.God = true;
        combat.Orc.FixedStandingShots = 10;
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(arOrcAt);
        combat.Mate!.Spawn();
        combat.Mate.PlaceAt(arOrcAt with { Z = arOrcAt.Z - 500 * apu });
        combat.Orc.Charm(25);
        var charmEye = arOrcAt + new Vector3(0, 63 * apu, 900 * apu);
        var (loosedBefore, struckBefore2, fightsBefore) = (combat.OrcArchery.Loosed, combat.OrcArchery.Struck, combat.OrcFights.Landed);
        float mateBefore = combat.Mate.Health;
        for (int i = 0; i < (int)(12 / Tick); i++)
            combat.Step(Tick, step++, false, false, false, charmEye, -Vector3.UnitZ, charmEye - Vector3.UnitY * 63 * apu);
        int atMate = combat.OrcArchery.Loosed - loosedBefore, onPlayer = combat.OrcArchery.Struck - struckBefore2, mateStruck = combat.OrcFights.Landed - fightsBefore;
        float mateTook = mateBefore - combat.Mate.Health;
        combat.Mate.Damage(1000);
        int afterMate = combat.OrcArchery.Loosed;
        for (int i = 0; i < (int)(6 / Tick); i++)
            combat.Step(Tick, step++, false, false, false, charmEye, -Vector3.UnitZ, charmEye - Vector3.UnitY * 63 * apu);
        afterMate = combat.OrcArchery.Loosed - afterMate;
        // The mate runs at it, and it backs off whenever the mate comes within 314: it shoots between.
        Check(atMate >= 1 && onPlayer == 0 && mateStruck >= 1 && mateTook > 0 && afterMate == 0,
            FormattableString.Invariant($"a charmed archer (R10169-2): {atMate} arrows let go at its mate 500 units off in 12 s, {mateStruck} blows on it for {mateTook:0.#}, none on the player ({onPlayer}); the mate down, it let go {afterMate} more in 6 s"));
        combat.Retune(arTuning with { Orc = arTuning.Orc with { Bow = DarkMessiah.Lab.Npc.ArcheryData.LongBow, SquadMate = null, SpawnUnaware = false } });
        combat.Reset();
        combat.God = true;
        // Killed in its kick or its drop (R10169-3): it lies until it respawns (RespawnSeconds), then an archer anew.
        (bool Down, bool Respawned) KilledIn(DarkMessiah.Lab.Npc.ArcheryPhase at)
        {
            combat.Orc.FixedStandingShots = 1;
            combat.Orc.Spawn();
            combat.Orc.PlaceAt(arOrcAt);
            var eye = arOrcAt + new Vector3(0, 63 * apu, 900 * apu);
            for (int i = 0; i < (int)(30 / Tick) && combat.Orc.Phase != at; i++)
                combat.Step(Tick, step++, false, false, false, eye, -Vector3.UnitZ, eye - Vector3.UnitY * 63 * apu);
            if (combat.Orc.Phase != at) return (false, false);
            combat.Orc.Damage(1000);
            bool down = true;
            for (int i = 0; i < (int)((combat.Tuning.Orc.RespawnSeconds - .2f) / Tick); i++)
            {
                combat.Step(Tick, step++, false, false, false, eye, -Vector3.UnitZ, eye - Vector3.UnitY * 63 * apu);
                down &= !combat.Orc.Alive && combat.Orc.State == DarkMessiah.Lab.Npc.OrcState.Down;
            }
            for (int i = 0; i < (int)(1 / Tick); i++) combat.Step(Tick, step++, false, false, false, eye, -Vector3.UnitZ, eye - Vector3.UnitY * 63 * apu);
            return (down, combat.Orc.Alive && combat.Orc.Health > 30 && combat.Orc.Archery is not null);
        }
        var inKick = KilledIn(DarkMessiah.Lab.Npc.ArcheryPhase.Kick);
        var inDrop = KilledIn(DarkMessiah.Lab.Npc.ArcheryPhase.Drop);
        Check(inKick.Down && inKick.Respawned && inDrop.Down && inDrop.Respawned,
            FormattableString.Invariant($"killed in its kick, the archer lay until it respawned ({inKick.Down}, then {inKick.Respawned}); in its drop, the same ({inDrop.Down}, {inDrop.Respawned}) (R10169-3)"));
        // Out of sight (a wall between them everywhere): it rushes at once, letting go nothing.
        var hidden = Fight(10, world: (from, to) => Vector3.Lerp(from, to, .5f), seconds: 15);
        float hiddenKick = hidden.Phases.GetValueOrDefault(DarkMessiah.Lab.Npc.ArcheryPhase.Kick, float.NaN);
        // Too near: 250 units off, it plays surprised and backs off to 350, then shoots.
        var tooNear = Fight(10, units: 250, seconds: 10);
        Check(hidden.Loosed.Count == 0 && hiddenKick > 0 && hiddenKick <= 900 / ad.RushUnits + 1 && !float.IsNaN(tooNear.SurprisedAt) && tooNear.ClosestAfter >= ad.BackOffTo - 3 && tooNear.Loosed.Count >= 1,
            FormattableString.Invariant($"out of sight it rushed in and kicked {hiddenKick:0.0} s on, letting go {hidden.Loosed.Count}; 250 units off it played surprised and stood {tooNear.ClosestAfter:0} units off to shoot ({ad.BackOffTo:0}), letting go {tooNear.Loosed.Count}"));
        combat.Orc.FixedStandingShots = null;
        (combat.WorldRay, combat.WorldSurface) = (arRay, arSurface);
        combat.Retune(arTuning);
        combat.Reset();
        combat.God = arWasGod;
        combat.Orc.Hold = arWasHeld;
        ((IReplayBody)product).Begin();

        var archeryRecordings = Directory.Exists(Path.Combine(repo!, "local", "recordings", "experiments"))
            ? Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings", "experiments"), "l01_a-v18-npc-archery*.rbrec").Order().ToList() : [];
        if (archeryRecordings.Count >= 4)
        {
            // The maps' kit (the cleaver, then the bow): a fight whose blade blows were the bow's had them the other way round (-2's second).
            var fights = archeryRecordings.SelectMany(NpcArcheryCalibration.Measure).Where(f => !f.Blows.Any(b => b.Weapon == "weapon_mm_bow_long")).ToList();
            foreach (var f in fights) Console.WriteLine($"  {f}");
            // Its arrows let go before the player left (the one at 1200 units let go as the player went into the building).
            var flown = fights.SelectMany(f => f.Arrows).Where(a => a.Distance < 1000).ToList();
            // The first nock after waking (the fights it shot in), the first after each time it stood, and those standing on.
            var firstNocks = fights.Where(f => f.Arrows.Count > 0).Select(f => f.Nocks[0] - f.Woken).ToList();
            var standNocks = fights.SelectMany(f => f.Stood.Select(st => f.Nocks.FirstOrDefault(n => n >= st && n < st + 1, double.NaN) - st)).Where(d => !double.IsNaN(d)).ToList();
            var standingGaps = fights.SelectMany(f => f.Arrows.Where(a => a.NockedStanding).Zip(f.Arrows.Where(a => a.NockedStanding).Skip(1)).Select(p => p.Second.Nocked - p.First.Nocked)).Where(g => g < 3).ToList();
            double OffCadence(double gap) => Math.Abs(gap - ad.Every);
            var speeds = flown.Where(a => !float.IsNaN(a.Speed) && a.Distance is >= 300 and <= 800).ToList();
            var kicks = fights.Where(f => !double.IsNaN(f.Kicked)).ToList();
            static double Median(IEnumerable<double> xs) { var l = xs.Order().ToList(); return l.Count == 0 ? double.NaN : l[l.Count / 2]; }
            foreach (var a in speeds.Where(a => MathF.Abs(a.Speed - ad.SpeedAt(a.Distance)) > .04f * a.Speed)) Console.WriteLine(FormattableString.Invariant($"  archer arrow off the speed rule: {a} (the rule's {ad.SpeedAt(a.Distance):0})"));
            bool struckRight = flown.Count >= 80 && flown.All(a => a.Hit && MathF.Abs(a.Asked - ArrowAsk(a.Standing)) < .01f),
                // Each let go 1.08 or 1.2 s after its nock: standing when nocked, 1.08; on the move, 1.2, but for one stopping as it went (2).
                leftRight = flown.All(a => Math.Abs(a.Left - a.Nocked - ad.LeaveStanding) <= .03 || Math.Abs(a.Left - a.Nocked - ad.LeaveMoving) <= .03)
                    && flown.Where(a => a.NockedStanding).All(a => Math.Abs(a.Left - a.Nocked - ad.LeaveStanding) <= .03)
                    && flown.Count(a => !a.NockedStanding && Math.Abs(a.Left - a.Nocked - ad.LeaveMoving) > .03) <= 2,
                cadenceRight = firstNocks.All(n => Math.Abs(n - ad.FirstNock) <= .08) && standingGaps.Count >= 50 && standingGaps.All(g => OffCadence(g) <= .02)
                    && standNocks.Count >= 10 && standNocks.Count(n => Math.Abs(n - ad.StandNock) <= .03) >= standNocks.Count - 1,
                speedRight = speeds.All(a => MathF.Abs(a.Speed - ad.SpeedAt(a.Distance)) <= .04f * a.Speed),
                aimRight = Math.Abs(Median(flown.Where(a => a.Standing).Select(a => (double)a.FromEye)) + ad.AimBelowEye) <= 1 && Math.Abs(Median(flown.Where(a => !a.Standing).Select(a => (double)a.FromEye)) - ad.AimAboveEye) <= 1.5;
            bool kicksRight = kicks.Count >= 5 && kicks.All(k => MathF.Abs(k.KickAsked - 2.5f) < .01f && Math.Abs(k.Dropped - k.Kicked - .96) <= .05 && Math.Abs(k.Unsheathed - k.Kicked - 1.32) <= .05);
            Console.WriteLine($"  donor archers: struck {struckRight}, let go {leftRight}, cadence {cadenceRight} (first {string.Join(" ", firstNocks.Select(n => n.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}; on standing {string.Join(" ", standNocks.Select(n => n.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}; off {string.Join(" ", standingGaps.Where(g => OffCadence(g) > .02).Select(g => g.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}), speed {speedRight}, aim {aimRight}, kicks {kicksRight}");
            foreach (var a in flown.Where(a => Math.Abs(a.Left - a.Nocked - (a.NockedStanding ? ad.LeaveStanding : ad.LeaveMoving)) > .03)) Console.WriteLine($"  archer arrow off its delay: {a}");
            bool arrowsRight = struckRight && leftRight && cadenceRight && speedRight && aimRight;
            Check(arrowsRight && kicksRight,
                FormattableString.Invariant($"the donor's archers: {flown.Count(a => a.Hit)} of {flown.Count} arrows struck at {flown.Min(a => a.Distance):0} to {flown.Max(a => a.Distance):0} units, the player still, strafing at up to {flown.Max(a => a.PlayerSpeed):0} units/s or backing off, each asking the model's 5; let go {ad.LeaveStanding:0.00} s after the nock standing, {ad.LeaveMoving:0.0} on the move; nocked first {Median(firstNocks):0.00} s after waking, {Median(standNocks):0.00} s after standing and every {Median(standingGaps):0.00} s on ({standingGaps.Count}); {speeds.Count} speeds within 4% of the rule; the line {Median(flown.Where(a => a.Standing).Select(a => (double)a.FromEye)):0.0} from the eye standing, {Median(flown.Where(a => !a.Standing).Select(a => (double)a.FromEye)):0.0} on the move; {kicks.Count} kicks asked 2.5, the bow dropped 0.96 s and the cleaver drawn 1.32 s after"));
        }
        else Console.WriteLine("skip the donor's archers: no local/recordings/experiments/l01_a-v18-npc-archery*.rbrec");
    }
    // The bow (#10168; docs/lab.md, "The bow"): belt slot 5 puts the classic bow in hand. A press draws it after 0.12 s, the
    // draw plays its 2 s and the arrow leaves 0.03 s into the shot, along the view raised 2.8°, at 2100 units/s falling
    // 600/s²; it strikes the held orc's hit boxes for (strength + WDmg) × the shoot clip's HitPow 1.5 × the hit group, ×3
    // while the orc is unaware. Parry puts a drawn arrow down; with bow_reload a press after the shot nocks the next at once;
    // +zoom held while drawn narrows the view in 0.38 s. Against l01_a-v18-bow*: the donor's input through the lab's bow
    // releases each arrow when the donor's did, each flight is the rules', each arrow asks the combat model's damage, and the
    // zoom's and the camera's sway are the donor's.
    {
        float bmpu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        // Archery to its level, and strength 3 with it (extra_damage needs archery or combat).
        BuildFile Archer(int projectile) => file with { Skills = new Dictionary<string, int>(file.Skills) { ["projectile"] = projectile, ["extra_damage"] = projectile > 0 ? 3 : 0 } };
        // The bow in hand: its belt key toggles it.
        void BowInHand() { if (combat.Bow is null) combat.UseBelt(4); }
        combat.Rebuild(Archer(3));
        bool bowWasHeld = combat.Orc.Hold;
        combat.Orc.Hold = true;
        BowInHand();
        var bowHeld = combat.Bow;
        var bowEye = new Vector3(12, 1.6f, 6);
        var bowOrc = new Vector3(12, 0, 6 + 200 * bmpu);
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(bowOrc);
        Vector3 BowToward(Vector3 at) => Vector3.Normalize(at - bowEye);
        // One shot: attack held for the given steps (one a tap), parry from a step on, then run until the arrow stops or 5 s.
        (float Left, float Speed, float Pitch, float Asked, int? Group, int Shots) BowShot(Vector3 aim, int held, int parryFrom = int.MaxValue, bool zoom = false)
        {
            float left = float.NaN, speed = 0, pitch = float.NaN;
            int shots = combat.Archery.Shot;
            combat.Orc.Spawn();
            combat.Orc.PlaceAt(bowOrc);
            var before = combat.LastArrow;
            for (int i = 0; i < 360; i++)
            {
                combat.Step(Tick, step++, i < held, i >= parryFrom, false, bowEye, BowToward(aim), bowEye - Vector3.UnitY * 1.6f, zoomHeld: zoom);
                if (combat.ArrowsInFlight.FirstOrDefault() is { } arrow)
                {
                    if (float.IsNaN(left))
                    {
                        left = (i + 1) * Tick;
                        pitch = MathF.Asin(Vector3.Normalize(arrow.Launched).Y) * 180 / MathF.PI;
                        speed = new Vector2(arrow.Launched.X, arrow.Launched.Z).Length() / MathF.Cos(pitch * MathF.PI / 180) / bmpu;
                    }
                }
                else if (!float.IsNaN(left) || i > held + 300) break;
            }
            for (int i = 0; i < 240 && combat.Bow?.State != DarkMessiah.Lab.Ranged.BowState.Rest; i++)
                combat.Step(Tick, step++, false, false, false, bowEye, BowToward(aim), bowEye - Vector3.UnitY * 1.6f);
            var hit = combat.LastArrow != before ? combat.LastArrow : null;
            return (left, speed, pitch, hit?.Asked ?? 0, hit?.Group, combat.Archery.Shot - shots);
        }
        var chest = bowOrc + Vector3.UnitY * 1.1f;
        float viewPitch = MathF.Asin(BowToward(chest).Y) * 180 / MathF.PI;
        var tap = BowShot(chest, 1);
        var hold = BowShot(chest, (int)(3 / Tick));
        var bowParried = BowShot(chest, (int)(3 / Tick), parryFrom: (int)(2.5f / Tick));
        var bowExpected = tap.Group is int tg ? combat.PlayerAsks(new DarkMessiah.Combat.Party("player", "", [DarkMessiah.Lab.Ranged.BowRules.Shoot], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel),
            combat.Orc.Party(), tg, false, combat.Orc.Health, weapon: DarkMessiah.Lab.Ranged.BowRules.Classic) : float.NaN;
        var bowRules = combat.Bow!.Rules;
        float drawSeconds = combat.FirstPerson.ClipSeconds(DarkMessiah.Lab.Ranged.BowRules.Draw);
        // The donor's tap (l01_a-v18-bow, 157.85 s) left 2.19 s after the press.
        Check(bowHeld is not null && MathF.Abs(tap.Left - 2.19f) <= 2 * Tick && MathF.Abs(hold.Left - 3 - bowRules.LeaveDelay) <= 2 * Tick
                && MathF.Abs(tap.Speed - bowRules.SpeedUnits) <= .01f * bowRules.SpeedUnits && MathF.Abs(tap.Pitch - viewPitch - bowRules.Raise(viewPitch)) <= .1f
                && tap.Group is not null && tap.Asked > 0 && MathF.Abs(tap.Asked - bowExpected) < .01f && bowParried.Shots == 0,
            FormattableString.Invariant($"the bow: in hand from belt slot 5 ({bowHeld is not null}); a tap's arrow left {tap.Left:0.00} s after it (the donor's 2.19: the delay {bowRules.DrawDelay:0.00}, the {drawSeconds:0.00} s draw, a tick and a step, and {bowRules.LeaveDelay:0.00}), one held 3 s at {hold.Left:0.00}; it flew at {tap.Speed:0} units/s, {tap.Pitch - viewPitch:0.0}° above the view, and struck hit group {tap.Group} for {tap.Asked:0.##} (the model's {bowExpected:0.##}); bowParried after 2.5 s, {bowParried.Shots} shot"));
        // An arrow strikes by the bow that shot it (R10168-4): the classic's at the orc 40 m off, the long bow taken out
        // while it flies (belt slot 9), and then the weapon, strikes for the classic's 12.
        var bowFar = bowOrc with { Z = bowEye.Z + 40 };
        combat.Rebuild(Archer(3) with { Belt = [.. Archer(3).Belt, new BeltSlot(Bow: "models/items/weapons/bow_long/bow_long.mdl")] });
        BowInHand();
        float switchedAsked = float.NaN, holsteredAsked = float.NaN, classicAsked = float.NaN, classicHolstered = float.NaN;
        float ClassicAt(int? group) => combat.PlayerAsks(new DarkMessiah.Combat.Party("player", "", [DarkMessiah.Lab.Ranged.BowRules.Shoot], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel),
            combat.Orc.Party(), group, false, 100, weapon: DarkMessiah.Lab.Ranged.BowRules.Classic);
        foreach (bool holster in new[] { false, true })
        {
            combat.Orc.Spawn();
            var lastBefore = combat.LastArrow;
            bool switched = false;
            for (int i = 0; i < 400 && combat.LastArrow == lastBefore; i++)
            {
                combat.Orc.PlaceAt(bowFar);
                combat.Step(Tick, step++, i < 1, false, false, bowEye, BowToward(bowFar + Vector3.UnitY * 1.1f), bowEye - Vector3.UnitY * 1.6f);
                if (!switched && combat.ArrowsInFlight.Count > 0)
                {
                    switched = true;
                    combat.UseBelt(8);
                    if (holster) combat.UseBelt(8);
                }
            }
            if (holster) (holsteredAsked, classicHolstered) = (combat.LastArrow?.Asked ?? float.NaN, ClassicAt(combat.LastArrow?.Group));
            else (switchedAsked, classicAsked) = (combat.LastArrow?.Asked ?? float.NaN, ClassicAt(combat.LastArrow?.Group));
            combat.Rebuild(Archer(3) with { Belt = [.. Archer(3).Belt, new BeltSlot(Bow: "models/items/weapons/bow_long/bow_long.mdl")] });
            BowInHand();
        }
        Check(MathF.Abs(switchedAsked - classicAsked) < .01f && MathF.Abs(holsteredAsked - classicHolstered) < .01f,
            FormattableString.Invariant($"an arrow strikes by the bow that shot it (R10168-4): the classic's, with the long bow taken out while it flew, {switchedAsked:0.##} (the classic's {classicAsked:0.##}), and with it put away again {holsteredAsked:0.##} ({classicHolstered:0.##})"));
        combat.Rebuild(Archer(3));
        BowInHand();
        // The explosive bow's blast (R10168-1): its arrow into the floor 20 units short of the held orc takes 15 from it
        // (explosive_damage, within explosive_radius 30 of its feet), 50 short none; a classic arrow meeting the floor at a
        // grazing angle bounces once at 0.6 of its speed and stops where it lands next; a rope arrow leaves an anchor (R10168-2).
        const string ExplosiveBow = "models/items/weapons/bow_explosive/bow_explosive.mdl", RopeBow = "models/items/weapons/bow_ropearrow/bow_ropearrow.mdl";
        BuildFile WithBow(string model) => Archer(3) with { Belt = [.. Archer(3).Belt, new BeltSlot(Bow: model)] };
        float BlastAt(float shortUnits)
        {
            combat.Rebuild(WithBow(ExplosiveBow));
            combat.UseBelt(8);
            var near = bowEye with { Z = bowEye.Z + 100 * bmpu };
            combat.Orc.Spawn();
            var floor = new Vector3(bowEye.X, 0, near.Z - shortUnits * bmpu);
            for (int i = 0; i < 300; i++)
            {
                combat.Orc.PlaceAt(near with { Y = 0 });
                combat.Step(Tick, step++, i < 1, false, false, bowEye, Vector3.Normalize(floor - bowEye), bowEye - Vector3.UnitY * 1.6f);
            }
            return combat.Orc.Taken;
        }
        float blast20 = BlastAt(20), blast50 = BlastAt(50);
        // The bounce: the classic bow aimed at the floor 15 m ahead, the orc out of the way.
        combat.Rebuild(Archer(3));
        BowInHand();
        combat.Orc.Spawn();
        combat.Orc.PlaceAt(bowEye with { X = bowEye.X + 30, Y = 0 });
        DarkMessiah.Lab.Ranged.LabArrow? grazing = null;
        float speedBefore = 0, speedAfter = 0;
        for (int i = 0; i < 400; i++)
        {
            combat.Orc.PlaceAt(bowEye with { X = bowEye.X + 30, Y = 0 });
            combat.Step(Tick, step++, i < 1, false, false, bowEye, Vector3.Normalize(new Vector3(bowEye.X, 0, bowEye.Z + 15) - bowEye), bowEye - Vector3.UnitY * 1.6f);
            if (grazing is null && combat.ArrowsInFlight.Count > 0) grazing = combat.ArrowsInFlight[0];
            if (grazing is { Stopped: null } flying) { if (!flying.Bounced) speedBefore = flying.Velocity.Length(); else if (speedAfter == 0) speedAfter = flying.Velocity.Length(); }
            if (grazing?.Stopped is not null) break;
        }
        // The rope arrow into the floor.
        combat.Rebuild(WithBow(RopeBow));
        combat.UseBelt(8);
        int anchors = combat.RopeAnchors.Count;
        for (int i = 0; i < 300; i++) combat.Step(Tick, step++, i < 1, false, false, bowEye, Vector3.Normalize(new Vector3(bowEye.X, 0, bowEye.Z + 3) - bowEye), bowEye - Vector3.UnitY * 1.6f);
        int anchored = combat.RopeAnchors.Count - anchors;
        Check(MathF.Abs(blast20 - 15) < .01f && blast50 == 0 && grazing is { Bounced: true, Impacts.Count: 2 } && MathF.Abs(speedAfter / speedBefore - DarkMessiah.Lab.Ranged.BowRules.Of(combat.Donor, DarkMessiah.Lab.Ranged.BowRules.Classic).Restitution) <= .03f && anchored == 1,
            FormattableString.Invariant($"the explosive bow's arrow 20 units short of the orc took {blast20:0.#} from it with its blast (explosive_damage 15, radius 30), 50 short {blast50:0.#} (R10168-1); a classic arrow meeting the floor at a grazing angle bounced ({grazing?.Bounced}) at {speedAfter / speedBefore:0.00} of its speed and stopped at its next impact ({grazing?.Impacts.Count} in all); a rope arrow left {anchored} anchor (R10168-2)"));
        combat.Rebuild(Archer(3));
        BowInHand();
        // Zoom: held from 2.2 s into the draw, until 1 s after.
        float zoomFull = float.NaN, zoomGone = float.NaN;
        combat.Orc.Spawn();
        for (int i = 0; i < 420; i++)
        {
            bool zoomKey = i >= (int)(2.3f / Tick) && i < (int)(4.5f / Tick);
            combat.Step(Tick, step++, i < (int)(4 / Tick), false, false, bowEye, BowToward(chest + Vector3.UnitY), bowEye - Vector3.UnitY * 1.6f, zoomHeld: zoomKey);
            if (float.IsNaN(zoomFull) && combat.Zoom >= 1) zoomFull = (i + 1) * Tick - 2.3f;
            if (!float.IsNaN(zoomFull) && float.IsNaN(zoomGone) && combat.Zoom <= 0) zoomGone = (i + 1) * Tick - 4.5f;
        }
        int restZoomSteps = 0;
        for (int i = 0; i < 60; i++) { combat.Step(Tick, step++, false, false, false, bowEye, Vector3.UnitZ, bowEye - Vector3.UnitY * 1.6f, zoomHeld: true); if (combat.Zoom > 0) restZoomSteps++; }
        Check(MathF.Abs(zoomFull - DarkMessiah.Lab.Ranged.BowRules.ZoomIn) <= 1.5f * Tick && MathF.Abs(zoomGone - DarkMessiah.Lab.Ranged.BowRules.ZoomOut) <= 1.5f * Tick && restZoomSteps == 0,
            FormattableString.Invariant($"the bow's zoom (bow_zoom): drawn, +zoom narrowed the view in {zoomFull:0.00} s and it widened in {zoomGone:0.00} s once let go; at rest, {restZoomSteps} steps zoomed"));
        // The reload: with bow_reload attack held after a shot nocks at once (the fast reload); without, the bow lowers and
        // draws again. Each arrow let go once drawn; recorded shot to shot: 1.29 s with the skill, past 2.9 without.
        (string? Second, float Apart) TwoShots(int projectile)
        {
            combat.Rebuild(Archer(projectile));
            BowInHand();
            var seen = new List<string>();
            var lefts = new List<float>();
            int shots = combat.Archery.Shot;
            for (int i = 0; i < 900 && lefts.Count < 2; i++)
            {
                bool drawn = combat.Bow?.State == DarkMessiah.Lab.Ranged.BowState.Drawn;
                combat.Step(Tick, step++, !drawn, false, false, bowEye, BowToward(chest + Vector3.UnitY * 3), bowEye - Vector3.UnitY * 1.6f);
                if (combat.Bow is { } b && (seen.Count == 0 || seen[^1] != b.Clip.Clip)) seen.Add(b.Clip.Clip);
                if (combat.Archery.Shot > shots + lefts.Count) lefts.Add(i * Tick);
            }
            for (int i = 0; i < 300 && combat.Bow?.State != DarkMessiah.Lab.Ranged.BowState.Rest; i++) combat.Step(Tick, step++, false, false, false, bowEye, Vector3.UnitZ, bowEye - Vector3.UnitY * 1.6f);
            return (seen.FirstOrDefault(c => c is DarkMessiah.Lab.Ranged.BowRules.FastReload or DarkMessiah.Lab.Ranged.BowRules.Out), lefts.Count == 2 ? lefts[1] - lefts[0] : float.NaN);
        }
        var bowFast = TwoShots(3);
        var bowSlow = TwoShots(0);
        Check(bowFast.Second == DarkMessiah.Lab.Ranged.BowRules.FastReload && bowSlow.Second == DarkMessiah.Lab.Ranged.BowRules.Out && MathF.Abs(bowFast.Apart - 1.29f) <= .05f && bowSlow.Apart > 2.9f,
            FormattableString.Invariant($"bow_reload: with it the bow nocks again in {bowFast.Second} and shoots {bowFast.Apart:0.00} s apart (the donor's 1.29); without, it lowers ({bowSlow.Second}) and shoots {bowSlow.Apart:0.00} s apart"));
        // The donor's bow.
        var bowRecordings = Directory.Exists(Path.Combine(repo!, "local", "recordings", "experiments"))
            ? Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings", "experiments"), "l01_a-v18-bow*.rbrec").Order().ToList() : [];
        if (bowRecordings.Count > 0)
        {
            var orcParty = combat.Orc.Party();
            int timed = 0, leftShots = 0, flown = 0, flownWell = 0, tracked = 0, asked = 0, askedRight = 0;
            float worstLeft = 0, worstTrack = 0;
            var wrong = new List<string>();
            var recorded = new Dictionary<string, BowRecording>();
            foreach (var path in bowRecordings)
            {
                var rec = BowCalibration.Measure(path);
                recorded[Path.GetFileNameWithoutExtension(path)] = rec;
                foreach (var shot in rec.Shots) Console.WriteLine($"  {Path.GetFileNameWithoutExtension(path)} arrow {shot}");
                // The donor's attack and parry through the lab's bow, at the recording's ticks: each arrow leaves when the donor's did.
                var labBow = new DarkMessiah.Lab.Ranged.LabBow(new DarkMessiah.Lab.Ranged.BowRules(DarkMessiah.Lab.Ranged.BowRules.Classic, 9999), combat.FirstPerson.ClipSeconds);
                var labLefts = new List<double>();
                bool fastReload = rec.Modules.Contains("bow_reload");
                // The donor's jumps and landings (jump_in_bow to jump_out_bow's end): no draw begins in them.
                float landingSeconds = combat.FirstPerson.ClipSeconds(DarkMessiah.Lab.Ranged.LabBow.LandingClip);
                var airborne = rec.Clips.Where(c => c.Clip == "jump_in_bow").Select(c => (From: c.Time,
                    To: rec.Clips.FirstOrDefault(o => o.Clip == DarkMessiah.Lab.Ranged.LabBow.LandingClip && o.Time >= c.Time).Time + landingSeconds)).ToList();
                for (int i = 0; i < rec.Input.Count; i++)
                {
                    double dt = i > 0 ? rec.Input[i].Time - rec.Input[i - 1].Time : rec.TickSeconds;
                    bool landing = airborne.Any(a => rec.Input[i].Time >= a.From && rec.Input[i].Time < a.To);
                    if (labBow.Step((float)dt, rec.Input[i].Attack, rec.Input[i].Parry, fastReload, landing)) labLefts.Add(rec.Input[i].Time);
                }
                leftShots += rec.Shots.Count;
                // RB_BOWTRACE=name prints the recording's donor and lab release times.
                if (Environment.GetEnvironmentVariable("RB_BOWTRACE") == Path.GetFileNameWithoutExtension(path))
                    Console.WriteLine($"  donor {string.Join(" ", rec.Shots.Select(x => x.Left.ToString("0.000")))}\n  lab   {string.Join(" ", labLefts.Select(x => x.ToString("0.000")))}\n  clips {string.Join(" ", rec.Clips.Select(c => $"{c.Time:0.000}:{c.Clip.Replace("layer_bow", "")}"))}");
                foreach (var shot in rec.Shots)
                {
                    double bowNear = labLefts.Count > 0 ? labLefts.Min(l => Math.Abs(l - shot.Left)) : double.PositiveInfinity;
                    worstLeft = MathF.Max(worstLeft, (float)bowNear);
                    if (bowNear <= 2.5 * rec.TickSeconds) timed++;
                    else wrong.Add(FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(path)} {shot.Left:0.00} s (lab {bowNear:0.00} off)"));
                }
                if (labLefts.Count != rec.Shots.Count) wrong.Add($"{Path.GetFileNameWithoutExtension(path)}: the lab shot {labLefts.Count}, the donor {rec.Shots.Count}");
                foreach (var shot in rec.Shots.Where(s => !float.IsNaN(s.Pitch)))
                {
                    flown++;
                    if (MathF.Abs(shot.Pitch - (-shot.ViewPitch + bowRules.Raise(-shot.ViewPitch))) <= .4f && MathF.Abs(shot.Speed - bowRules.SpeedUnits) <= .015f * bowRules.SpeedUnits
                        && MathF.Abs(shot.Gravity - bowRules.GravityUnits) <= .05f * bowRules.GravityUnits) flownWell++;
                    else wrong.Add($"{Path.GetFileNameWithoutExtension(path)} flight {shot}, {shot.Flight.Count} samples");
                    // The lab's arrow from the donor's start, along its heading and the view: where it is at each donor sample until its first bounce.
                    if (shot.Free < 6) continue;
                    var heading = new Vector2(shot.Flight[2].At.X - shot.Flight[0].At.X, shot.Flight[2].At.Y - shot.Flight[0].At.Y);
                    if (heading.LengthSquared() < 1) continue;
                    heading = Vector2.Normalize(heading);
                    float vp = -shot.ViewPitch * MathF.PI / 180;
                    var look = RustyBridge.Wire.Donor.SourceSpace.Direction(new Vector3(heading.X * MathF.Cos(vp), heading.Y * MathF.Cos(vp), MathF.Sin(vp)));
                    var arrow = new DarkMessiah.Lab.Ranged.LabArrow(RustyBridge.Wire.Donor.SourceSpace.Position(shot.Flight[0].At), bowRules.Launch(look), bowRules);
                    double at = 0;
                    // Within 3 units and a hundredth of the way flown, over its free flight.
                    float worst = 0;
                    for (int k = 1; k < shot.Free; k++)
                    {
                        while (at < shot.Flight[k].After - 1e-6) { arrow.Step((float)rec.TickSeconds, (_, _) => null); at += rec.TickSeconds; }
                        float off = Vector3.Distance(arrow.Position, RustyBridge.Wire.Donor.SourceSpace.Position(shot.Flight[k].At)) / bmpu;
                        worst = MathF.Max(worst, off / (3 + .01f * Vector3.Distance(shot.Flight[k].At, shot.Flight[0].At)));
                    }
                    tracked++;
                    if (worst > 1) wrong.Add(FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(path)} track {shot.Left:0.00} s: {worst:0.0} of the tolerance off"));
                    worstTrack = MathF.Max(worstTrack, worst);
                }
                foreach (var shot in rec.Shots.Where(s => s.Hit is not null && s.Bow.Length > 0))
                {
                    var h = shot.Hit!.Value;
                    asked++;
                    // Into the world, the donor's ask is the base: as on an aware orc's body untraced.
                    var alert = h.Alert is int level ? (level, h.NpcState) : (99, 3);
                    float lab = combat.PlayerAsks(new DarkMessiah.Combat.Party("player", "", [DarkMessiah.Lab.Ranged.BowRules.Shoot], 0, "#1", DarkMessiah.Combat.CombatData.PlayerModel),
                        orcParty, h.Group, false, 100, combat.Model.Data.PlayerStrength(shot.ExtraDamage), weapon: shot.Bow, alert: alert);
                    if (MathF.Abs(lab - h.Asked) < .01f) askedRight++;
                    else wrong.Add(FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(path)} {shot.Left:0.00} s: donor {h.Asked:0.##}, lab {lab:0.##}"));
                }
            }
            foreach (var w in wrong.Take(30)) Console.WriteLine($"  bow: {w}");
            // One release was later than the rules: it waited on a restart of the bow's rest clip (0.06 s; docs/lab.md, "The bow").
            Check(leftShots >= 40 && timed >= leftShots * 95 / 100 && worstLeft <= .13f && flown >= 30 && flownWell == flown && tracked >= 25 && worstTrack <= 1 && asked >= 30 && askedRight == asked,
                FormattableString.Invariant($"the donor's bow: through the lab's bow its input released {timed} of {leftShots} arrows within 2.5 ticks of the donor's (worst {worstLeft:0.000} s); {flownWell} of {flown} flights left {bowRules.RaiseDegrees:0.0}° above the view (less from {DarkMessiah.Lab.Ranged.BowRules.CompensationStart:0}° up) at {bowRules.SpeedUnits:0} units/s falling {bowRules.GravityUnits:0}/s² (within 0.4°, 1.5%, 5%), and the lab's arrow from each of {tracked} starts kept within {worstTrack:0.00} of 3 units and 1% of its way of it over its free flight; {askedRight} of {asked} arrows asked the combat model's damage (four bows, strength 0 and 6, unaware orcs and the world)"));
            // The zoom: the camera's field of view narrowed to the rules' and back, in their times.
            if (recorded.GetValueOrDefault("l01_a-v18-bow-zoom") is { } zoomRec && zoomRec.Fov.Count > 4)
            {
                var fovs = zoomRec.Fov.ToList();
                // Its field of view is across, at the 1280×720 the scenarios run: the zoomed one, high.
                float wide = fovs.Max(f => f.Degrees), narrow = fovs.Min(f => f.Degrees);
                float zoomedHigh = 2 * MathF.Atan(MathF.Tan(narrow * MathF.PI / 360) * 720 / 1280) * 180 / MathF.PI;
                int firstNarrowing = fovs.FindIndex(f => f.Degrees < wide - .5f);
                double began = fovs[firstNarrowing - 1].Time, full = fovs.First(f => f.Time > began && f.Degrees <= narrow + .5f).Time;
                int widening = fovs.FindIndex(firstNarrowing, f => f.Time > full && f.Degrees > narrow + .5f);
                // Widening began a frame (two ticks) before the first wider sample.
                double back = fovs.First(f => f.Time > fovs[widening].Time && f.Degrees >= wide - .5f).Time, unzoom = fovs[widening].Time - 2 * zoomRec.TickSeconds;
                Check(MathF.Abs(zoomedHigh - DarkMessiah.Lab.Ranged.BowRules.ZoomedDegrees) <= .3f && Math.Abs(full - began - DarkMessiah.Lab.Ranged.BowRules.ZoomIn) <= .05 && Math.Abs(back - unzoom - DarkMessiah.Lab.Ranged.BowRules.ZoomOut) <= .05,
                    FormattableString.Invariant($"the donor's zoom: {wide:0.0}° to {narrow:0.0}° across, {zoomedHigh:0.0}° high (the lab's {DarkMessiah.Lab.Ranged.BowRules.ZoomedDegrees:0.0}), in {full - began:0.00} s, back in {back - unzoom:0.00} s"));
            }
            // The sway: the camera's pitch off the view through a 6 s hold, against the lab's head with and without stability.
            float SwayOff(BowRecording rec, int projectile)
            {
                var holds = new List<List<(double Time, float Offset)>>();
                foreach (var c in rec.Camera)
                {
                    if (holds.Count == 0 || c.Time - holds[^1][^1].Time > .1) holds.Add([]);
                    holds[^1].Add(c);
                }
                var longest = holds.OrderByDescending(h => h[^1].Time - h[0].Time).First();
                combat.Rebuild(Archer(projectile));
                BowInHand();
                for (int i = 0; i < 300 && combat.Bow?.State != DarkMessiah.Lab.Ranged.BowState.Rest; i++) combat.Step(Tick, step++, false, false, false, bowEye, Vector3.UnitZ, bowEye - Vector3.UnitY * 1.6f);
                var lab = new List<float>();
                for (int i = 0; i < (int)(6 / Tick); i++)
                {
                    combat.Step(Tick, step++, true, false, false, bowEye, Vector3.UnitZ, bowEye - Vector3.UnitY * 1.6f);
                    lab.Add(combat.FirstPerson.Head.Turn.Pitch);
                }
                for (int i = 0; i < 240 && combat.Bow?.State != DarkMessiah.Lab.Ranged.BowState.Rest; i++) combat.Step(Tick, step++, false, false, false, bowEye, Vector3.UnitZ, bowEye - Vector3.UnitY * 1.6f);
                double sum = 0;
                int n = 0;
                foreach (var (time, offset) in longest)
                {
                    int k = (int)Math.Round((time - longest[0].Time) / Tick);
                    if (k < 0 || k >= lab.Count) continue;
                    sum += (offset - lab[k]) * (offset - lab[k]);
                    n++;
                }
                if (Environment.GetEnvironmentVariable("RB_SWAYTRACE") == "1")
                    Console.WriteLine($"  sway {projectile}: " + string.Join(" ", longest.Where((_, i) => i % 6 == 0).Select(c => { int k = (int)Math.Round((c.Time - longest[0].Time) / Tick); return FormattableString.Invariant($"{c.Time - longest[0].Time:0.00}:{c.Offset:0.0}/{(k < lab.Count ? lab[k] : float.NaN):0.0}"); })));
                return n > 0 ? (float)Math.Sqrt(sum / n) : float.NaN;
            }
            if (recorded.GetValueOrDefault("l01_a-v18-bow") is { } steadyRec && recorded.GetValueOrDefault("l01_a-v18-bow-basic") is { } unsteadyRec)
            {
                float steadyOff = SwayOff(steadyRec, 3), unsteadyOff = SwayOff(unsteadyRec, 0), crossed = SwayOff(unsteadyRec, 3);
                Check(steadyOff <= .6f && unsteadyOff <= .6f && crossed > 1,
                    FormattableString.Invariant($"the donor's sway through a 6 s hold: the lab's head is {steadyOff:0.00}° (RMS) off its camera with stability and {unsteadyOff:0.00}° without; without stability against the lab's steady head, {crossed:0.00}°"));
            }
            // Arrows into an orc running in (R10168-3, R10168-5): the struck orc moved over 20 units in the 0.3 s before the
            // blow. Through the lab's own bow and impact path: the shot's bow and strength in hand, drawn and let go at the
            // donor's start along its heading and view, and the lab orc (posed by its own clips, its hit boxes swept as any
            // arrow's) put at the recorded orc's origin each step, facing the archer, alert as the donor's. Its blow lands
            // when the donor's did (within 2.5 ticks), on its hit group, and asks its damage. The donor's courtyard isn't the
            // arena, so the arena's world is off for it.
            static Vector3 TargetAt(IReadOnlyList<(double Time, Vector3 At)> track, double time)
            {
                int k = 0;
                while (k + 1 < track.Count && track[k + 1].Time <= time) k++;
                if (k + 1 >= track.Count || track[k + 1].Time <= track[k].Time) return track[k].At;
                return Vector3.Lerp(track[k].At, track[k + 1].At, (float)((time - track[k].Time) / (track[k + 1].Time - track[k].Time)));
            }
            var movingShots = recorded.Values.SelectMany(r => r.Shots.Select(x => (Rec: r, Shot: x))).Where(x => x.Shot.Hit is { } h && h.Victim.StartsWith("npc_", StringComparison.Ordinal)
                && x.Shot.Target.Count > 2 && x.Shot.Flight.Count >= 3
                && Vector3.Distance(TargetAt(x.Shot.Target, x.Shot.HitAt - .3), TargetAt(x.Shot.Target, x.Shot.HitAt)) > (Environment.GetEnvironmentVariable("RB_MOVING") == "1" ? -1 : 20)).ToList();
            int movingTimed = 0, movingAsked = 0;
            var movingNotes = new List<string>();
            foreach (var (rec, shot) in movingShots)
            {
                var h = shot.Hit!.Value;
                var heading = Vector2.Normalize(new Vector2(shot.Flight[2].At.X - shot.Flight[0].At.X, shot.Flight[2].At.Y - shot.Flight[0].At.Y));
                float vp = -shot.ViewPitch * MathF.PI / 180;
                var look = RustyBridge.Wire.Donor.SourceSpace.Direction(new Vector3(heading.X * MathF.Cos(vp), heading.Y * MathF.Cos(vp), MathF.Sin(vp)));
                var from = RustyBridge.Wire.Donor.SourceSpace.Position(shot.Flight[0].At);
                var built = Archer(3) with { Skills = new Dictionary<string, int>(Archer(3).Skills) { ["extra_damage"] = shot.ExtraDamage }, Belt = [.. Archer(3).Belt, new BeltSlot(Bow: shot.Bow)] };
                combat.Rebuild(built);
                // The belt's key takes the bow out if it's in hand already: once more puts it back.
                combat.UseBelt(built.Belt.Length - 1);
                if (combat.Bow is null) combat.UseBelt(built.Belt.Length - 1);
                var (worldRay, worldSurface) = (combat.WorldRay, combat.WorldSurface);
                (combat.WorldRay, combat.WorldSurface) = (null, null);
                combat.RecordedAlert = _ => h.Alert is int level ? (level, h.NpcState) : (99, 3);
                combat.Orc.Spawn();
                void PutOrc(double time)
                {
                    var feet = RustyBridge.Wire.Donor.SourceSpace.Position(TargetAt(shot.Target, time));
                    combat.Orc.PlaceAt(feet);
                    combat.Orc.Face(from - feet);
                    combat.Mate?.PlaceAt(feet + new Vector3(0, 0, 4000 * bmpu));
                }
                int struckBefore = combat.Archery.Struck;
                int drawn = (int)(3 / Tick), left = -1;
                double met = double.NaN;
                (DarkMessiah.Lab.Npc.Orc Orc, int? Group, float Asked)? landed = null;
                for (int i = 0; i < drawn + 400 && double.IsNaN(met); i++)
                {
                    // Before the arrow leaves, the orc stands at the donor's start of flight; after, where it was at the step's end.
                    PutOrc(left < 0 ? shot.Left : shot.Left + (i - left + 1) * Tick);
                    combat.Step(Tick, step++, i < drawn, false, false, from, look, from - Vector3.UnitY * 1.6f);
                    if (left < 0 && (combat.ArrowsInFlight.Count > 0 || combat.Archery.Struck > struckBefore)) left = i;
                    if (combat.Archery.Struck > struckBefore) { landed = combat.LastArrow; met = shot.Left + (i - left + 1) * Tick; }
                    else if (left >= 0 && combat.ArrowsInFlight.Count == 0) break;
                }
                (combat.WorldRay, combat.WorldSurface) = (worldRay, worldSurface);
                combat.RecordedAlert = null;
                if (Math.Abs(met - shot.HitAt) <= 2.5 * rec.TickSeconds) movingTimed++;
                if (landed is { } s && s.Group == h.Group && MathF.Abs(s.Asked - h.Asked) < .01f) movingAsked++;
                movingNotes.Add(FormattableString.Invariant($"{shot.Left:0.00} s: the orc {Vector3.Distance(TargetAt(shot.Target, shot.HitAt - .3), TargetAt(shot.Target, shot.HitAt)):0} units in 0.3 s, struck {shot.HitAt - shot.Left:0.000} s on (the lab's {met - shot.Left:0.000}), hit group {h.Group} for {h.Asked:0.##} (the lab's {(landed?.Group is int lg ? lg : "-")} for {(landed?.Asked ?? 0):0.##})"));
            }
            foreach (var n in movingNotes) Console.WriteLine($"  moving {n}");
            Check(movingShots.Count >= 3 && movingTimed == movingShots.Count && movingAsked == movingShots.Count,
                FormattableString.Invariant($"the donor's arrows into a running orc (R10168-3): {movingShots.Count} struck an orc moving 20 units or more in 0.3 s; through the lab's bow and hit boxes the arrow struck the lab orc on its track when the donor's struck on {movingTimed} (within 2.5 ticks), and on its hit group for its damage on {movingAsked}"));
            // The donor's blasts (R10168-1): each explosive impact's caster took 15 from an orc within 30 units of it, none from
            // one farther; its bounces (0.6 of the speed, then stopped at the next impact); and the rope made (R10168-2).
            var blasts = recorded.Values.SelectMany(r => r.Blasts).ToList();
            // The bounces: arrows fitted over a free flight that ended on the floor (the view level or down; not the rope
            // bow's steep shots at the beams).
            var bounces = recorded.Values.SelectMany(r => r.Shots).Where(x => !float.IsNaN(x.Bounce.Ratio) && !float.IsNaN(x.Pitch) && x.ViewPitch >= 0).ToList();
            var ropesMade = recorded.Values.SelectMany(r => r.Ropes).ToList();
            foreach (var b in blasts) Console.WriteLine(FormattableString.Invariant($"  blast {b.Time:0.00} s at ({b.At.X:0}, {b.At.Y:0}, {b.At.Z:0}): the nearest orc {b.Nearest:0} units off, took {b.Taken:0.#}"));
            foreach (var r in ropesMade) Console.WriteLine(FormattableString.Invariant($"  rope {r.Time:0.00} s: keyframes {string.Join(" ", r.Keyframes.Select(k => $"({k.X:0}, {k.Y:0}, {k.Z:0})"))}"));
            var explosive = DarkMessiah.Lab.Ranged.BowRules.Of(combat.Donor, "models/items/weapons/bow_explosive/bow_explosive.mdl");
            bool blastsRight = blasts.All(b => b.Nearest <= explosive.ExplosiveRadius ? MathF.Abs(b.Taken - explosive.ExplosiveDamage) < .01f : b.Taken == 0);
            Check(blasts.Count(b => b.Nearest <= explosive.ExplosiveRadius) >= 1 && blasts.Count(b => b.Nearest > explosive.ExplosiveRadius) >= 3 && blastsRight
                    && bounces.Count >= 10 && bounces.All(x => MathF.Abs(x.Bounce.Ratio - bowRules.Restitution) <= .05f) && ropesMade.Count >= 1,
                FormattableString.Invariant($"the donor's explosive arrows: {blasts.Count} impacts cast a blast, {blasts.Count(b => b.Nearest <= explosive.ExplosiveRadius)} within {explosive.ExplosiveRadius:0} units of an orc took {explosive.ExplosiveDamage:0} from it and the rest nothing ({blastsRight}); {bounces.Count} arrows off the floor bounced at {(bounces.Count > 0 ? bounces.Min(x => x.Bounce.Ratio) : float.NaN):0.00} to {(bounces.Count > 0 ? bounces.Max(x => x.Bounce.Ratio) : float.NaN):0.00} of their speed (the lab's {bowRules.Restitution:0.00}); {ropesMade.Count} rope made by a rope arrow"));
        }
        else Console.WriteLine("skip the donor's bow: no local/recordings/experiments/l01_a-v18-bow*.rbrec");
        combat.Rebuild(file);
        combat.Orc.Hold = bowWasHeld;
        ((IReplayBody)product).Begin();
    }
    // RB_MAGICONLY=1 stops the smoke after the telekinesis and fireball checks.
    if (Environment.GetEnvironmentVariable("RB_MAGICONLY") == "1") Environment.Exit(0);
    // The landing's dip at player.json's comfort scale (R10161-1): the donor's 7.9° at 1, half at 0.5, none at 0.
    {
        var head = combat.FirstPerson.Head;
        float DipAt(float scale)
        {
            head.LandingScale = scale;
            head.Step(Tick, null, false, Vector3.Zero);
            head.Step(Tick, null, true, new Vector3(0, -5, 0));
            for (float t = Tick; t < .19f; t += Tick) head.Step(Tick, null, false, Vector3.Zero);
            float pitch = head.Turn.Pitch;
            for (int i = 0; i < 120; i++) head.Step(Tick, null, false, Vector3.Zero);
            return pitch;
        }
        float full = DipAt(1), half = DipAt(.5f), none = DipAt(0);
        head.LandingScale = product.Player.Tuning.LandingDip;
        Check(full > 7 && MathF.Abs(half - full / 2) < .05f && none == 0 && product.Player.Tuning.LandingDip == 1,
            FormattableString.Invariant($"the landing dips the view {full:0.0}° at landingDip 1 (player.json's), {half:0.0}° at 0.5, {none:0.0}° at 0"));
    }
    // Health (#10162): 50 and 10 an extra_health level; fast_metabolism regains 0.25 a second; the belt's potions give
    // their give_health up to the maximum, one fewer each use; keys 1 to 9 are its slots.
    {
        Check(combat.Health.Max == 50 && combat.Belt[0] is { Count: 3 } firstSlot && firstSlot.Item.GiveHealth == 10 && combat.Belt[1] is { Count: 1 } secondSlot
            && secondSlot.Item.GiveHealth == 300 && Refused(file with { Belt = [new BeltSlot("models/items/provisions/nothing.mdl", 1)] }) is string noItem && noItem.Contains("imported item"),
            $"build.json's player has 50 health and a belt of 3 life potions (give_health 10) and a full life potion (300); an item the import lacks is refused ({combat.Belt.Describe()})");
        var healthy = new Dictionary<string, int>(file.Skills) { ["stamina"] = 1, ["extra_health"] = 2, ["fast_metabolism"] = 1 };
        combat.Rebuild(file with { Skills = healthy });
        combat.Health.Set(40);
        combat.Health.Sync();
        float max = combat.Health.Max;
        combat.UseBelt(0);
        float potion = combat.PlayerHealth;
        // The HUD's health bar follows it (R10162-1): 50 of 70 fills 71.4%.
        string barFill = DarkMessiah.Lab.Ui.LabHud.Fields(product.Observation()).First(f => f.Key == "healthFill").Text;
        for (int i = 0; i < 125; i++) combat.Health.Step(Tick);
        float regained = combat.PlayerHealth - potion;
        combat.UseBelt(1);
        float full = combat.PlayerHealth;
        combat.UseBelt(1);
        Check(max == 70 && potion == 50 && MathF.Abs(regained - .5f) < 1e-4f && full == 70 && combat.Belt[0]?.Count == 2 && combat.Belt[1] is null && barFill == "71.4",
            FormattableString.Invariant($"extra_health 2 makes 70 ({max}); a life potion 40 to {potion}, the HUD's bar {barFill}% full; fast_metabolism regains {regained:0.##} in 2.08 s; the full life potion fills it ({full}) and its slot is empty after"));
        combat.Rebuild(file);
        combat.Reset();
        Check(combat.Health.Max == 50 && combat.PlayerHealth == 50 && combat.Belt[0]?.Count == 3 && (KeyboardControl)((int)KeyboardControl.Digit1 + 8) == KeyboardControl.Digit9,
            "back on build.json and reset: 50 of 50, the belt full again; the keys 1 to 9 are the belt's nine slots");
    }
    combat.Rebuild(combat.Build! with { Modules = new HashSet<string>(), Source = "no modules" });
    var parries = new List<SwordMove>();
    for (int i = 0; i < 20; i++) { combat.Step(Tick, step++, false, true, false, product.Player.Eye, product.Player.Forward, product.Player.Feet); parries.Add(combat.Sword.Move); }
    Check(!parries.Contains(SwordMove.Parry), "a build without combat_parry can't parry in the fight");
    combat.Rebuild(file);
    // The shield on the arm (#10149): sword_shield's set, block held raises it, attack pressed while blocking bashes.
    combat.Rebuild(WithSkill("combat", 2) with { OffHand = SmallShield });
    Check(combat.Shield is { } worn && MathF.Abs(worn.ParryCos - .7f) < 1e-4f && combat.ShieldHealth == 240 && combat.Sword.Clips.Idle.Clip == "layer_sword_shield_idle"
        && combat.Sword.Clips.Bash is { Traced: true } bashClip && bashClip.Clip == SwordClips.BashClip,
        $"a shield build wears the small orc shield (parry_cos 0.7, health 80 × HealthCoeffShield 3 = {combat.ShieldHealth}) and plays sword_shield's set");
    var shieldMoves = new List<(SwordMove Move, string Clip, bool Traced)>();
    for (int i = 0; i < 40; i++)
    {
        bool attack = i is >= 20 and < 23;
        combat.Step(Tick, step++, attack, i < 25, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        shieldMoves.Add((combat.Sword.Move, combat.Sword.Clip.Clip, combat.Sword.Traced));
        if (i == 19) Check(combat.Blocking && combat.Sword.Clip.Clip.StartsWith("layer_parry_sword_shield_", StringComparison.Ordinal) && combat.FirstPerson.Facts().Count() == 3,
            $"block held raises the shield ({combat.Sword.Clip.Clip}), drawn on the arms' Leftshield");
    }
    Check(shieldMoves.Skip(20).TakeWhile(m => m.Move == SwordMove.Bash).Count() == 20 && shieldMoves.Any(m => m.Move == SwordMove.Bash && m.Traced),
        "attack pressed while blocking is the shield bash, which plays on with block let go and traces in its window");
    Check(combat.Model.PlayerBlock(new ParryCase(0, combat.Orc.Party(), DefenseResult.PlayerParried), .75f, .7f) == DefenseResult.PlayerParried
        && combat.Model.PlayerBlock(new ParryCase(0, combat.Orc.Party(), DefenseResult.PlayerParried), .5f, .7f) == DefenseResult.Hit,
        "the shield blocks a blow from within its parry_cos of the facing (41°), not one from the side (60°)");
    // A reset puts the build's shield back whole, worn or broken (R10149-1).
    combat.WearShield(100);
    float wornTo = combat.ShieldHealth;
    combat.Reset();
    float afterWorn = combat.ShieldHealth;
    bool broke = combat.WearShield(500);
    bool offAfterBreak = combat.Shield is null;
    combat.Reset();
    Check(wornTo == 140 && afterWorn == 240 && broke && offAfterBreak && combat.Shield is not null && combat.ShieldHealth == 240
        && combat.Sword.Clips.Idle.Clip == "layer_sword_shield_idle",
        $"a reset puts the build's shield back whole: worn to {wornTo} then {afterWorn}; broken, off the arm, then back at {combat.ShieldHealth}");
    combat.Rebuild(file);
    Check(combat.Shield is null && combat.FirstPerson.Facts().Count() == 2, "back on build.json the shield is off the arm");
    // A recorded overlay's weight reaches the engine (R10198-2): the followed orc's parry overlay at 0.25 poses it
    // between none and full, and a change of weight alone is taken.
    {
        var orcAt = combat.Orc;
        Vector3 Hands(float weight)
        {
            orcAt.Follow(new OrcPose("#weights", orcAt.Feet, orcAt.Facing, 100, "combatidle", .3f, 1, null, 0, true,
                Overlays: weight > 0 ? [("layer_parry_B", .5f, weight)] : []), 0);
            var target = orcAt.Targets()[0];
            return target.Joints.Where(j => j.Key.Contains("Hand", StringComparison.OrdinalIgnoreCase))
                .Select(j => target.Pose[j.Value].World.Translation).Aggregate(Vector3.Zero, (a, b) => a + b);
        }
        var none = Hands(0);
        var full = Hands(1);
        var quarter = Hands(.25f);
        var half = Hands(.5f);
        orcAt.StopFollowing();
        float toFull = Vector3.Distance(none, full);
        Check(toFull > .01f && Vector3.Distance(none, quarter) > .001f && Vector3.Distance(quarter, full) > .001f && Vector3.Distance(quarter, half) > .001f,
            FormattableString.Invariant($"an overlay's weight poses the orc in between: its hands move {Vector3.Distance(none, quarter):0.000} m at 0.25, {Vector3.Distance(none, half):0.000} at 0.5, {toFull:0.000} at 1, and a weight-only change is taken"));
    }
    // The donor's blows are kept one a window in tick order, whichever pass found them (R10198-1): a parry in a swing's
    // first window and a hit in its second, 40 ticks on, are both compared; a second contact in the first window isn't.
    Check(CombatReplay.FirstPerWindow([new DonorSwing(90, 0, "layer_combo_sword_huge_noshield")], [(140, 0, "#5"), (100, 1, "#5"), (103, 0, "#5")]) is [1, 0],
        "a blocked first window and a damaging second one are both kept, whichever pass found them");
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

    // The orc's defence (#10151, R10151-1): how often it parries and dodges the swings it sees, by the swing, the orc's
    // situation and how many it has seen, against the donor's; the lab's through its combat, a scripted player fighting it.
    {
        var donorDefence = DefenceCalibration.Donor(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories));
        bool wasGod = combat.God;
        combat.God = true;
        combat.Reset();
        var labDefence = DefenceCalibration.Lab(combat, product.Player.Eye.Y - product.Player.Feet.Y, 3600);
        combat.God = wasGod;
        static double Rate(int part, int whole) => whole == 0 ? double.NaN : (double)part / whole;
        static string Show((int Swings, int Parries, int Dodges) c) => c.Swings == 0 ? "no swings"
            : FormattableString.Invariant($"{c.Swings} swings, parried {Rate(c.Parries, c.Swings):P0}, dodged {Rate(c.Dodges, c.Swings):P0}");
        // Every situation the donor has 25 swings of is compared (R10151-5), and the lab's fight must give it 30: within
        // two standard deviations of the difference between the two rates, plus 5 points. Thinner donor ones are
        // reported as uncertain; one the lab's script leaves short fails.
        var compared = new List<string>();
        var failed = new List<string>();
        var undersampled = new List<string>();
        void Compare(string name, Func<DefenceCase, bool> where, string? notModelled = null)
        {
            var d = donorDefence.Count(where);
            var l = labDefence.Count(where);
            bool supported = d.Swings >= 25;
            Console.WriteLine($"  orc defence, {name}: donor {Show(d)}; lab {Show(l)}{(notModelled is not null ? $" (not modelled: {notModelled})" : !supported ? " (too few in the donor: uncertain)" : l.Swings < 30 ? " (TOO FEW IN THE LAB)" : "")}");
            if (!supported || notModelled is not null) return;
            if (l.Swings < 30) { undersampled.Add(name); return; }
            compared.Add(name);
            foreach (var part in new Func<(int Swings, int Parries, int Dodges), int>[] { c => c.Parries, c => c.Dodges })
            {
                double p = Rate(part(d), d.Swings), q = Rate(part(l), l.Swings);
                // Two standard deviations of the difference of the two rates (both are samples), plus 5 points.
                if (Math.Abs(p - q) > 2 * Math.Sqrt(p * (1 - p) / d.Swings + q * (1 - q) / l.Swings) + .05) { failed.Add(name); break; }
            }
        }
        foreach (var kind in Enum.GetValues<SwingKind>())
            foreach (var situation in Enum.GetValues<OrcSituation>())
                Compare($"{kind} on a {situation.ToString().ToLowerInvariant()} orc", c => c.Kind == kind && c.Situation == situation,
                    // How often an attacking orc parries is how often the player swung just as its attack ended: by that,
                    // below (R10151-5).
                    kind == SwingKind.Fast && situation == OrcSituation.Attacking ? "the mix of the player's timing; compared by how soon the orc came free" : null);
        // An attacking orc answers a swing once free, while the swing comes (the donor's parried 12 of 28 fast attacks
        // within 0.2 s of their attack's end, none of 194 later). Attacks only: the lab's orc doesn't turn-strike or grab.
        static bool Attack(DefenceCase c) => c.Kind == SwingKind.Fast && c.Situation == OrcSituation.Attacking && c.Sequence.StartsWith("attack_", StringComparison.Ordinal) && c.Sequence != "attack_grab";
        Compare("fast on an attacking orc, free within 0.2 s", c => Attack(c) && c.FreeIn < .2f);
        Compare("fast on an attacking orc, free later", c => Attack(c) && !(c.FreeIn < .2f));
        foreach (var direction in new[] { "N", "T", "B", "L", "R" })
            Compare($"power {direction} on a ready orc", c => c.Kind == SwingKind.Power && c.Direction == direction && c.Situation == OrcSituation.Ready);
        // Repeated swings: the first few an orc sees against later ones, and its first kick against its later ones.
        foreach (var (name, from, to) in new[] { ("swings 1–3", 0, 2), ("swings 4–10", 3, 9), ("swings 11 on", 10, int.MaxValue) })
            Compare($"fast on a ready orc, its {name}", c => c.Kind == SwingKind.Fast && c.Situation == OrcSituation.Ready && c.Nth >= from && c.Nth <= to,
                // The donor's orcs parried their first few swings less: likely its defence's combat-state gate
                // (+0x2A8856, the NPC's state +0x1A5C at 3), which an orc reaches as it becomes aware (#10154).
                from == 0 ? "an orc not yet in its combat state, #10154" : null);
        foreach (var (name, from, to) in new[] { ("first kick", 0, 0), ("second kick", 1, 1), ("third kick on", 2, int.MaxValue) })
            Compare($"its {name}, ready", c => c.Kind == SwingKind.Kick && c.Situation == OrcSituation.Ready && c.NthKick >= from && c.NthKick <= to);
        Check(undersampled.Count == 0 && failed.Count == 0,
            $"the orc's defence matches the donor's in every situation the donor has 25 swings of ({compared.Count} compared{(failed.Count > 0 ? "; off: " + string.Join(", ", failed) : "")}{(undersampled.Count > 0 ? "; too few in the lab: " + string.Join(", ", undersampled) : "")})");
        Check(compared.Contains("Fast on a ready orc") && compared.Contains("Power on a ready orc") && compared.Contains("Kick on a ready orc"),
            "among them, the fast attacks, power attacks and kicks on a ready orc (a roll over its difficulty 0.6; base_dodge_chance 0.5 once in 15 s; dodge_on_kick_init_chance 0.2)");
        var kicks = Enumerable.Range(0, 3).Select(n => donorDefence.Count(c => c.Kind == SwingKind.Kick && c.Situation == OrcSituation.Ready && (n < 2 ? c.NthKick == n : c.NthKick >= n))).ToList();
        Console.WriteLine($"  donor kicks dodged, ready, by the orc's kick count: {string.Join("; ", kicks.Select((k, n) => $"{(n < 2 ? (n + 1).ToString() : "3+")}: {Show(k)}"))}");
    }

    // An orc's stamina (R10151-2): its .qct's 30, spent by parries (1), dodges (4) and stuns (10); spent out it is wounded.
    {
        orc.Spawn();
        var st = orc.Stamina;
        float full = st.Left;
        int stuns = 0;
        while (!st.Exhausted && stuns < 10) { st.Spend(st.Data.Stun); stuns++; }
        bool wounded = orc.Wounded;
        float left = st.Left;
        st.Spend(st.Data.Parry);
        Check(full == 30 && stuns == 3 && wounded && left == st.Data.Exhausted && st.Left == left,
            FormattableString.Invariant($"an orc starts with its .qct's {full} stamina; three stuns spend it, and it is wounded at full health ({orc.Health:0}), spending no more"));
        orc.Spawn();
        Check(!orc.Wounded && orc.Stamina.Left == full, "a new orc has its stamina back");
    }

    // Perception (#10154): the lab's hearing against every recorded approach on an unaware orc (Wire v18), and the
    // donor's sneak attacks: an unaware orc's alert level is 0 and a blow asks ×3.
    {
        // The arena's recordings and local/perception's (#10154: l05_a's sight and calls, kept apart from the fights the
        // other calibrations read; docs/recording.md).
        // Not a scenario that froze its orcs (ai_disable in its log: telekinesis-throw's, #10164): a frozen orc notices no one.
        static bool Frozen(string recording) => Path.ChangeExtension(recording, ".log") is var log && File.Exists(log)
            && File.ReadLines(log).Any(l => l.Contains("exec ai_disable", StringComparison.Ordinal));
        var perceptionRecordings = Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories)
            .Concat(Directory.Exists(Path.Combine(repo!, "local", "perception")) ? Directory.EnumerateFiles(Path.Combine(repo!, "local", "perception"), "*.rbrec") : [])
            .Where(r => !Frozen(r)).ToList();
        var approaches = PerceptionCalibration.Measure(perceptionRecordings, orc.Perception.Data);
        foreach (var a in approaches)
            Console.WriteLine(FormattableString.Invariant($"  approach {a.Recording} {a.Orc} {a.Gaits}{(a.InSight ? ", in sight" : "")}{(a.Hidden ? ", hidden" : "")}, closest {a.Closest:0}: donor {(a.Donor is double d ? $"noticed at {d:0.00} s" : "never")}{(a.Rise is { } rise ? $" ({rise.Distance:0} units, {rise.Angle:0}°)" : "")}{(float.IsNaN(a.Unseen) ? "" : $", unseen still {a.Unseen:0} units {a.UnseenAngle:0}°")}, lab {(a.Lab is double l ? $"at {l:0.00} s" : "never")}"));
        var noticed = approaches.Where(a => a.Donor is not null).ToList();
        // Within 0.6 s. The l05_a runs that hid the player (notarget) after placing it, out of the Wire's view, say only
        // whether the orc noticed: the lab's no later than the donor's.
        bool agree(ApproachCase a) => a.Donor is null ? a.Lab is null : a.Lab is double l && (a.Hidden ? l <= a.Donor.Value + .1 : Math.Abs(l - a.Donor.Value) <= .6);
        var bySight = approaches.Where(a => a.Recording.StartsWith("l05_a", StringComparison.Ordinal)).ToList();
        Check(approaches.Count >= 8 && noticed.Count >= 2 && approaches.Count(a => a.Donor is null) >= 4 && approaches.All(agree),
            FormattableString.Invariant($"an unaware orc notices the player when the donor's did, and not otherwise: {approaches.Count(agree)} of {approaches.Count} recorded approaches ({noticed.Count} noticed; in l01_a by a run or sprint within 500 units, none still or crouched, behind or in front){(approaches.Any(a => !agree(a)) ? "; off: " + string.Join(", ", approaches.Where(a => !agree(a)).Select(a => $"{a.Recording} {a.Orc}")) : "")}"));
        Check(bySight.Count(a => a.Donor is not null) >= 8 && bySight.Count(a => a.Donor is null) >= 8 && bySight.All(agree),
            FormattableString.Invariant($"in l05_a (stealth on) it sees a still player as the donor's did (R10154-2): {bySight.Count(agree)} of {bySight.Count}, seen {bySight.Count(a => a.Donor is not null)}, unseen {bySight.Count(a => a.Donor is null)} (its sight {orc.Perception.Data.SightRange:0} units, {orc.Perception.Data.SightHalfAngle:0}° either side)"));
        var sneak = new List<(DamageCase Case, string Recording)>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*sneak-attack*.rbrec", SearchOption.AllDirectories))
        {
            // The scenario gives every skill: the player's strength from its recorded extra_damage level (#10146).
            int extraDamage = 0;
            using (var statsStream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(statsStream)) if (f.Stats.Valid != 0) { extraDamage = f.Stats.SkillLevel("extra_damage"); break; }
            float strength = combat.Model.Data.PlayerStrength(extraDamage);
            using var stream = File.OpenRead(path);
            foreach (var d in DarkMessiah.Combat.Blows.Read(Path.GetFileNameWithoutExtension(path), stream).Damage)
                if (d.Attacker.IsPlayer && d.Victim.Class == "npc_orc_sword" && d.VictimAlert is not null) sneak.Add((d with { PlayerStrength = strength }, path));
        }
        // Each blow on an unaware orc as the model asks it, and the same blow had the orc been fighting (alert 4, state 3).
        var unaware = sneak.Where(x => x.Case.VictimAlert == 0)
            .Select(x => (x.Case, Unaware: combat.Model.Damage(x.Case) ?? float.NaN, Aware: combat.Model.Damage(x.Case with { VictimAlert = 4, VictimNpcState = 3 }) ?? float.NaN)).ToList();
        foreach (var u in unaware) Console.WriteLine(FormattableString.Invariant($"  sneak {u.Case}: recorded {u.Case.Requested:0.##}, model {u.Unaware:0.##}, aware {u.Aware:0.##}"));
        Check(unaware.Count >= 3 && unaware.All(u => MathF.Abs(u.Unaware - u.Case.Requested) < 1e-3f && MathF.Abs(u.Unaware - 3 * u.Aware) < 1e-3f),
            FormattableString.Invariant($"the donor's blows on an unaware orc (alert 0) asked ×3, as the model asks them: {string.Join(", ", unaware.Select(u => $"{u.Case.Requested:0.#} = 3 × {u.Aware:0.#}"))}"));

        // Calls for help (R10154-1): every recorded orc that became aware with another unaware within 1100 units. The
        // lab's rule: one that saw the player calls its squad-mates within SquadAwarnessDist, who come 2.4 s later; a blow
        // calls no one. The Wire doesn't carry squads: the recorded pairs within 500 were in one squad but for one.
        var calls = PerceptionCalibration.Calls(perceptionRecordings, orc.Perception.Data);
        foreach (var c in calls)
            Console.WriteLine(FormattableString.Invariant($"  call {c.Recording} {c.Caller} -> {c.Mate}, {c.Apart:0} apart, caller {(c.CallerStruck ? "struck" : "sensed the player")}: mate {(c.Delay is double d ? $"came {d:0.00} s later" : "never came")}{(c.OwnSense is double o ? $" (would have sensed it itself at {o:0.00} s)" : "")}"));
        var pd = orc.Perception.Data;
        // The mates that came might have sensed the player as the fight moved (the lab's rule says when): the squad's
        // came 2.4 s after, the one with no squad 5.9 s.
        var reached = calls.Where(c => !c.CallerStruck && c.Apart <= pd.SquadDistance).ToList();
        var notReached = calls.Where(c => c.CallerStruck || c.Apart > pd.SquadDistance).ToList();
        Check(reached.Count >= 1 && reached.All(c => c.Delay is double d && d >= pd.CallDelay - .3) && reached.Any(c => c.Delay is double d && Math.Abs(d - pd.CallDelay) <= .3)
            && notReached.Count >= 3 && notReached.All(c => c.Delay is null),
            FormattableString.Invariant($"recorded calls: an orc that sensed the player brought the unaware orc within {pd.SquadDistance:0} units ({string.Join(", ", reached.Select(c => c.Delay is double d ? $"{d:0.0} s" : "never"))} later; the lab's {pd.CallDelay:0.0} s for a squad-mate); one struck from behind, or farther off, brought none ({notReached.Count(c => c.Delay is null)} of {notReached.Count} never came)"));

        // The lab's two orcs through its combat (R10154-1, R10154-2): in l05_a (stealth on), unaware, both facing one way,
        // the second (its squad-mate) to the side. The player stands still ahead of the first, or strikes it from behind.
        var saved = combat.Tuning;
        float mpu = RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
        var face = -Vector3.UnitX;
        var side = Vector3.UnitZ;
        (AwareBy By, double? Noticed, double? Mate) Squad(string map, float ahead, float degrees, float apart, bool strike)
        {
            var spawnAt = new Vector3(saved.Orc.Spawn[0], saved.Orc.Spawn[1], saved.Orc.Spawn[2]);
            var mateAt = spawnAt + side * apart * mpu;
            combat.Retune(saved with { Orc = saved.Orc with { Map = map, SpawnUnaware = true, SquadMate = [mateAt.X, mateAt.Y, mateAt.Z] } });
            combat.Reset();
            combat.Orc.Face(face);
            combat.Mate!.Face(face);
            float a = degrees * MathF.PI / 180;
            var feet = strike ? spawnAt - face * 55 * mpu : spawnAt + (face * MathF.Cos(a) + side * MathF.Sin(a)) * ahead * mpu;
            var look = Vector3.Normalize((spawnAt - feet) with { Y = 0 });
            if (strike) combat.Orc.Struck(new AttackEvent(1, false), 0, feet, false, 1);
            double? noticed = combat.Orc.Perception.Aware ? 0 : null, mate = null;
            ulong t = 0;
            for (float time = Tick; time < 8; time += Tick)
            {
                combat.Step(Tick, t++, false, false, false, feet + Vector3.UnitY * 1.6f, look, feet);
                if (noticed is null && combat.Orc.Perception.Aware) noticed = time;
                if (mate is null && combat.Mate!.Perception.Aware) mate = time;
            }
            return (combat.Orc.Perception.By, noticed, mate);
        }
        var seen = Squad("l05_a", 400, 0, 300, false);
        var far = Squad("l05_a", 600, 0, 300, false);
        var wide = Squad("l05_a", 300, 35, 300, false);
        var noStealth = Squad("l01_a", 300, 0, 300, false);
        var mateFar = Squad("l05_a", 400, 0, 600, false);
        var behind = Squad("l05_a", 0, 0, 300, true);
        // The squad-mate is fought as the first orc is (R10154-3): a swing and a kick at it, the first held 20 m off.
        (float Mate, float First, string Victim, int Hits) HitMate(bool kick, bool replaying = false)
        {
            var spawnAt = new Vector3(saved.Orc.Spawn[0], saved.Orc.Spawn[1], saved.Orc.Spawn[2]);
            var mateAt = spawnAt + side * 300 * mpu;
            combat.Retune(saved with { Orc = saved.Orc with { Map = "l05_a", SquadMate = [mateAt.X, mateAt.Y, mateAt.Z] } });
            combat.Reset();
            bool wasGod = combat.God;
            combat.God = true;
            foreach (var o in new[] { combat.Orc, combat.Mate! }) { o.Hold = true; o.Defending.Forced = Defence.None; }
            combat.Orc.PlaceAt(spawnAt - side * 20);
            combat.Mate!.Face(face);
            var feet = mateAt + face * 45 * mpu;
            var look = Vector3.Normalize((mateAt - feet) with { Y = 0 });
            float mate = combat.Mate.Health, first = combat.Orc.Health;
            int hits = combat.Player.Hits;
            // Replaying, the followed orc is the recording's, here absent (as a replay's tick without one).
            var absent = new OrcPose("", new Vector3(0, -1000, 0), Vector3.UnitZ, 0, "combatidle", 0, 1, null, 0, true);
            ulong t = 0;
            for (int i = 0; i < 120; i++)
            {
                combat.Mate.PlaceAt(mateAt);
                combat.Step(Tick, t++, !kick && i < 3, false, kick && i < 3, feet + Vector3.UnitY * 1.6f, look, feet, replaying ? absent : null);
            }
            if (replaying) combat.Orc.StopFollowing();
            combat.God = wasGod;
            foreach (var o in new[] { combat.Orc, combat.Mate }) { o.Hold = false; o.Defending.Forced = null; }
            return (mate - combat.Mate.Health, first - combat.Orc.Health, combat.Victim == combat.Mate ? "the mate" : "the first orc", combat.Player.Hits - hits);
        }
        var swungAt = HitMate(false);
        var kickedAt = HitMate(true);
        var swungReplaying = HitMate(false, true);
        var kickedReplaying = HitMate(true, true);
        Check(swungReplaying.Mate == 0 && kickedReplaying.Mate == 0 && swungReplaying.Hits == 0 && kickedReplaying.Hits == 0,
            FormattableString.Invariant($"replaying, the live squad-mate in reach takes no blow (R10154-4): a swing and a kick at it with the recorded orc absent take {swungReplaying.Mate:0.##} and {kickedReplaying.Mate:0.##}, landing {swungReplaying.Hits + kickedReplaying.Hits} hits"));
        Check(swungAt.Mate > 0 && swungAt.First == 0 && kickedAt.Mate > 0 && kickedAt.First == 0 && swungAt.Victim == "the mate",
            FormattableString.Invariant($"the player's blows land on the squad-mate they reach (R10154-3): a fast attack takes {swungAt.Mate:0.##} and a kick {kickedAt.Mate:0.##} from it, none from the first orc 20 m off ({swungAt.First:0.##}, {kickedAt.First:0.##})"));
        // A fireball meets and burns the squad-mate as it does the first orc (R10165-1): cast at its torso with the first
        // 20 m off, and at the floor between the two 60 units apart; replaying, it passes the live mate by.
        (float Mate, float First) FireAtMate(bool between, bool replaying = false)
        {
            var spawnAt = new Vector3(saved.Orc.Spawn[0], saved.Orc.Spawn[1], saved.Orc.Spawn[2]);
            var mateAt = spawnAt + side * 300 * mpu;
            combat.Retune(saved with { Orc = saved.Orc with { Map = "l05_a", SquadMate = [mateAt.X, mateAt.Y, mateAt.Z] } });
            combat.Reset();
            foreach (var o in new[] { combat.Orc, combat.Mate! }) o.Hold = true;
            var firstAt = between ? mateAt + side * 60 * mpu : spawnAt - side * 20;
            combat.Orc.PlaceAt(firstAt);
            combat.Mana.Fill();
            if (combat.SpellInHand is null) combat.UseBelt(2);
            var feet = mateAt + face * 200 * mpu;
            var aim = between ? (mateAt + firstAt) / 2 : mateAt + Vector3.UnitY * 1.1f;
            var eye = feet + Vector3.UnitY * 1.6f;
            float mate = combat.Mate!.Taken, first = combat.Orc.Taken;
            var absent = new OrcPose("", new Vector3(0, -1000, 0), Vector3.UnitZ, 0, "combatidle", 0, 1, null, 0, true);
            ulong t = 0;
            for (int i = 0; i < 180; i++)
            {
                combat.Mate.PlaceAt(mateAt);
                if (!replaying) combat.Orc.PlaceAt(firstAt);
                combat.Step(Tick, t++, i < 3, false, false, eye, Vector3.Normalize(aim - eye), feet, replaying ? absent : null);
            }
            if (replaying) combat.Orc.StopFollowing();
            if (combat.SpellInHand is not null) combat.UseBelt(2);
            foreach (var o in new[] { combat.Orc, combat.Mate }) o.Hold = false;
            return (combat.Mate.Taken - mate, combat.Orc.Taken - first);
        }
        var fbMate = FireAtMate(false);
        var fbBoth = FireAtMate(true);
        var fbBothOff = (combat.LastFireball?.Distance ?? float.NaN, combat.LastMateFireball?.Distance ?? float.NaN);
        var fbMateReplaying = FireAtMate(false, true);
        var fbr = combat.Fireball;
        Check(MathF.Abs(fbMate.Mate - fbr.Effect) < .01f && fbMate.First == 0 && fbBoth.Mate > 0 && fbBoth.First > 0
                && MathF.Abs(fbBoth.Mate - fbr.Damage(fbBothOff.Item2)) < .01f && MathF.Abs(fbBoth.First - fbr.Damage(fbBothOff.Item1)) < .01f && fbMateReplaying.Mate == 0,
            FormattableString.Invariant($"a fireball meets the squad-mate (R10165-1): struck, it took {fbMate.Mate:0.##} ({fbr.Effect:0}) and the first orc 20 m off {fbMate.First:0.##}; off the floor between them, the mate took {fbBoth.Mate:0.##} ({fbBothOff.Item2:0} units off) and the first {fbBoth.First:0.##} ({fbBothOff.Item1:0} off); replaying, the mate took {fbMateReplaying.Mate:0.##}"));
        combat.Retune(saved);
        combat.Reset();
        static string When(double? t) => t is double v ? FormattableString.Invariant($"{v:0.00} s") : "never";
        Check(seen.By == AwareBy.Seen && Math.Abs(seen.Noticed!.Value - pd.SeeDelay) <= 2 * Tick && far.Noticed is null && wide.Noticed is null && noStealth.Noticed is null,
            $"the lab's orc in l05_a sees a still player 400 units ahead after its think ({When(seen.Noticed)}), not 600 ahead ({When(far.Noticed)}) or 35° off at 300 ({When(wide.Noticed)}), and in l01_a not at 300 ({When(noStealth.Noticed)})");
        Check(seen.Mate is double m && Math.Abs(m - seen.Noticed!.Value - pd.CallDelay) <= 2 * Tick && mateFar.Mate is null && behind.Mate is null,
            $"its squad-mate 300 units off answers its call {When(seen.Mate is double sm ? sm - seen.Noticed!.Value : null)} after it saw the player (the donor's 2.4), one 600 off doesn't ({When(mateFar.Mate)}), and a blow from behind calls no one ({When(behind.Mate)})");
    }

    // Hit reactions (#10153): the lab's rule against every recorded player blow on an orc.
    {
        var reactions = ReactionCalibration.Measure(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories));
        var scored = ReactionCalibration.Score(reactions, combat.Model.Data);
        static string Kind(string clip) => clip.Length == 0 ? "none"
            : System.Text.RegularExpressions.Regex.Replace(clip.ToLowerInvariant(), "_(front|back|left|right)", "_<side>");
        int agree = scored.Count(x => x.Lab.Equals(x.Donor, StringComparison.OrdinalIgnoreCase));
        foreach (var kind in scored.GroupBy(x => Kind(x.Donor)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  recorded {kind.Key}: {kind.Count()}, the lab's rule the same {kind.Count(x => x.Lab.Equals(x.Donor, StringComparison.OrdinalIgnoreCase))}, same kind {kind.Count(x => Kind(x.Lab) == kind.Key)}");
        Check(scored.Count >= 400 && agree * 10 >= scored.Count * 8,
            FormattableString.Invariant($"the orc's reaction to a blow is the donor's (clip and side) on {agree} of {scored.Count} recorded blows ({100.0 * agree / scored.Count:0}%)"));
    }

    // Stuns and knockdowns (#10152): the rules against every recorded player blow on an orc, then the lab's orc played through.
    {
        var (knockBlows, knockSpans) = KnockdownCalibration.Measure(Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories), combat.Model);
        var stagger = orc.StaggerData;
        var falls = KnockdownCalibration.Falls(knockBlows, stagger);
        // A jump attack fells only after the player's drop (R10152-1): the recorded ones by how far the player had dropped.
        var jumps = knockBlows.Where(b => OrcStagger.IsJumpAttack(b.Move) && KnockdownCalibration.Kind(b.VictimBefore) == BlowOutcome.None).ToList();
        foreach (var (name, past) in new[] { ("under 50 units", false), ("50 or more", true) })
        {
            var group = jumps.Where(b => b.Drop >= stagger.JumpDropToFall == past).ToList();
            Console.WriteLine(FormattableString.Invariant($"  recorded jump attacks on a standing orc, the player dropped {name}: {group.Count}, felled {group.Count(b => b.Outcome == BlowOutcome.Fall)} (drops {string.Join(", ", group.Select(b => b.Drop.ToString("0")).Take(12))})"));
        }
        Check(jumps.Count >= 1 && jumps.Where(b => b.Drop < stagger.JumpDropToFall).All(b => b.Outcome != BlowOutcome.Fall),
            FormattableString.Invariant($"no recorded jump attack felled an orc after a drop under 50 units ({jumps.Count(b => b.Drop < stagger.JumpDropToFall)} of {jumps.Count}); {jumps.Count(b => b.Drop >= stagger.JumpDropToFall && b.Outcome == BlowOutcome.Fall)} of {jumps.Count(b => b.Drop >= stagger.JumpDropToFall)} after more did, at chancetofall 0.6"));
        Check(falls.Blows >= 300 && Math.Abs(falls.Observed - falls.Expected) <= falls.Spread + 1,
            FormattableString.Invariant($"knockdowns come at the donor's rate: {falls.Observed} recorded falls in {falls.Blows} blows on a standing orc, the rule expects {falls.Expected:0.0} (±{falls.Spread:0.0})"));
        // The staff's blows on an alert orc that is up (the recorded idle, unaware ones stunned 5 of 12: another path).
        var staffBlows = knockBlows.Where(b => b.Move.Contains("staff", StringComparison.Ordinal) && KnockdownCalibration.Kind(b.VictimBefore) == BlowOutcome.None
            && !b.VictimBefore.StartsWith("idle", StringComparison.Ordinal)).ToList();
        int staffStuns = staffBlows.Count(b => b.Outcome != BlowOutcome.None);
        Check(staffBlows.Count >= 15 && staffStuns * 10 >= staffBlows.Count * 8,
            $"a staff's blow stuns (or fells) an alert orc that is up, as the donor's did: {staffStuns} of {staffBlows.Count}");
        var other = knockBlows.Where(b => !b.Kick && !b.Move.Contains("staff", StringComparison.Ordinal) && KnockdownCalibration.Kind(b.VictimBefore) == BlowOutcome.None).ToList();
        Check(other.Count >= 300 && other.Count(b => b.Outcome == BlowOutcome.Stun) <= 2,
            $"any other blow stuns only after six hits in ten seconds: {other.Count(b => b.Outcome == BlowOutcome.Stun)} recorded stuns in {other.Count} sword and dagger blows");
        var downs = knockSpans.Where(sp => sp.Kind == BlowOutcome.Fall).Select(sp => sp.Seconds).ToList();
        float downP10 = OrcBehaviour.Quantile(downs, .1f), downP90 = OrcBehaviour.Quantile(downs, .9f);
        // The lab's orc: knocked down by a blow that always fells, from in front.
        orc.Hold = true;
        orc.Spawn();
        orc.PlaceAt(product.Player.Feet + Vector3.Normalize(product.Player.Forward with { Y = 0 }) * 1.5f);
        for (int i = 0; i < 10; i++) orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
        // The stand-in fall first (a lab without a scene's world); the ragdoll's below (#10216).
        var ragdollWorld = orc.RagdollWorld;
        orc.RagdollWorld = null;
        var felled = orc.Struck(new AttackEvent(1, false, ChanceToFall: 1), 100, product.Player.Feet, false, 1);
        string lying = orc.Clip;
        float downFor = 0;
        string? gotUp = null;
        // Its fall (R10152-2): the head comes down from standing height to the ground over FallSeconds, and the body
        // the player meets lies down with it, its head and toes inside the lying box.
        Vector3 Joint(string name) { var t = orc.Targets()[0]; return t.Pose[t.Joints[name]].World.Translation; }
        static bool Inside(Vector3 p, (Vector3 Center, Vector3 HalfExtents) box)
        {
            var local = p - box.Center;
            return MathF.Abs(local.X) <= box.HalfExtents.X + .1f && MathF.Abs(local.Y) <= box.HalfExtents.Y + .1f && MathF.Abs(local.Z) <= box.HalfExtents.Z + .1f;
        }
        float Top() => orc.Hull.Center.Y + orc.Hull.HalfExtents.Y - orc.Feet.Y;
        float headStart = Joint("SK_Head").Y - orc.Feet.Y, headMid = float.NaN, headDown = float.NaN, fellFor = float.NaN, lyingTop = float.NaN, standingTop = float.NaN;
        bool bodyInside = false, fellFirst = orc.Falling;
        while (orc.State == OrcState.KnockedDown && downFor < 20)
        {
            orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
            downFor += Tick;
            if (float.IsNaN(headMid) && downFor >= Orc.FallSeconds / 2) headMid = Joint("SK_Head").Y - orc.Feet.Y;
            if (float.IsNaN(fellFor) && orc.Lying && !orc.Falling)
            {
                fellFor = downFor;
                headDown = Joint("SK_Head").Y - orc.Feet.Y;
                var hull = orc.Hull;
                lyingTop = Top();
                bodyInside = Inside(Joint("SK_Head"), hull) && Inside(Joint("SK_L_Toe"), hull) && Inside(Joint("SK_R_Toe"), hull) && Inside(Joint("SK_Root"), hull);
            }
            if (orc.Clip.StartsWith("StandUp_", StringComparison.Ordinal) && !orc.Clip.EndsWith("_firstframe", StringComparison.Ordinal)) gotUp ??= orc.Clip;
        }
        standingTop = Top();
        Check(fellFirst && headStart > 1.5f && headMid < headStart - .2f && headDown < .5f && MathF.Abs(fellFor - Orc.FallSeconds) <= 2 * Tick,
            FormattableString.Invariant($"a felled orc falls first: its head from {headStart:0.00} m through {headMid:0.00} to {headDown:0.00} over {fellFor:0.00} s"));
        Check(bodyInside && lyingTop <= .65f && standingTop >= 1.75f,
            FormattableString.Invariant($"lying, the body the player meets lies with it ({lyingTop:0.00} m high, its head, root and toes inside), {standingTop:0.00} m again once up"));
        Check(felled == Stagger.KnockedDown && lying == "StandUp_back_firstframe" && gotUp == "StandUp_back" && downFor >= downP10 && downFor <= downP90,
            FormattableString.Invariant($"a felled orc lies on its back ({lying}), gets up with {gotUp} and is up again after {downFor:0.00} s (the donor's {downP10:0.00}–{downP90:0.00}, {downs.Count} knockdowns)"));
        orc.RagdollWorld = ragdollWorld;
        // With the scene's world the orc falls as an Engine ragdoll (#10216), which spawns from the pose last drawn: the
        // test host draws nothing, so here it falls as the stand-in a tenth of a second later (the live check,
        // lab/tests/ragdoll-live.mjs, runs the ragdoll itself).
        {
            orc.Spawn();
            orc.PlaceAt(product.Player.Feet + Vector3.Normalize(product.Player.Forward with { Y = 0 }) * 1.5f);
            for (int i = 0; i < 10; i++) orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
            orc.Struck(new AttackEvent(1, false, ChanceToFall: 1), 300, product.Player.Feet, false, 1);
            string struckIn = orc.Clip;
            float waited = 0, down = 0;
            for (; orc.State == OrcState.KnockedDown && down < 20; down += Tick)
            {
                orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
                if (waited == 0 && orc.Clip.EndsWith("_firstframe", StringComparison.Ordinal)) waited = down + Tick;
            }
            Check(orc.RagdollWorld is not null && !orc.RagdollState.Ragdolled && struckIn == "combatidle" && waited <= .12f && Math.Abs(down - downFor - waited) <= 2 * Tick,
                FormattableString.Invariant($"undrawn, a knockdown waits {waited:0.00} s in the pose it was struck in for its ragdoll, then falls as the stand-in: up after {down:0.00} s"));
        }
        var stunned = orc.Struck(new AttackEvent(1, false), 200, product.Player.Feet, staff: true, 1);
        string stunClip = orc.Clip;
        for (int i = 0; i < 120; i++) orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
        string held = orc.Clip;
        orc.Damage(.5f);
        orc.React(OrcReactions.Pick(new ReactionBlow(new AttackEvent(1, false, ImpactCoeff: .5f), false, .5f, false, 2, 180), stunned: true, 10));
        Check(stunned == Stagger.Stunned && stunClip == "hit_stunned_front" && held == "Stunned" && orc.State == OrcState.Hurt && orc.Clip == "Hit_Front_medium",
            $"a staff's blow stuns it ({stunClip}, then {held}), and the next hit ends the stun");
        Check(CombatModel.DownMultiplier("StandUp_back") == 2 && CombatModel.DownMultiplier("Stunned") == 1.5f && CombatModel.DownMultiplier("combatidle") == 1,
            "a blow on an orc getting up asks ×2, on a stunned one ×1.5");
        orc.Hold = false;
        orc.Spawn();
    }

    // The orc fights by its .qct (#10150): its attack choice by distance, its rhythm and its walk against the donor's
    // recorded orcs, the lab's over ten minutes of a scripted fight.
    string orcRecordings = Path.Combine(repo!, "local", "recordings");
    if (Directory.Exists(orcRecordings))
    {
        var donorOrc = OrcCalibration.Donor(Directory.EnumerateFiles(orcRecordings, "*.rbrec", SearchOption.AllDirectories));
        // The landing windows the orc chooses by, refitted to the recordings: combat.json's must stay within 10 units.
        var fitted = OrcCalibration.Landings(donorOrc, orc.Mind);
        foreach (var (name, w) in fitted.OrderBy(f => f.Key, StringComparer.Ordinal))
            Console.WriteLine(FormattableString.Invariant($"  orc landing {name}: fitted [{w.Low:0}, {w.High:0}], authored {(combat.Tuning.Orc.AttackLandings.TryGetValue(name, out var a) ? $"[{a[0]:0}, {a[1]:0}]" : "none")}"));
        Check(fitted.Count >= 8 && fitted.All(f => combat.Tuning.Orc.AttackLandings.TryGetValue(f.Key, out var a) && MathF.Abs(a[0] - f.Value.Low) <= 10 && MathF.Abs(a[1] - f.Value.High) <= 10),
            $"combat.json's attack landing windows are the recordings' ({fitted.Count} attacks, each within 10 units of the refit)");
        // The scripted player swings as the recorded players did with an orc near (R10151-5).
        var swingClips = SwordClips.From(combat.FirstPerson.ArmsModel.Info, combat.FirstPerson.AttackSpeed, combat.Tuning);
        var swingGaps = OrcCalibration.PlayerSwingGaps(Directory.EnumerateFiles(orcRecordings, "*.rbrec", SearchOption.AllDirectories),
            swingClips.Moves.Select(m => m.Clip).ToArray(), swingClips.Kick.Clip);
        Console.WriteLine(FormattableString.Invariant($"  the recorded players' swings with an orc near: {swingGaps.Count} gaps, p25 {OrcBehaviour.Quantile(swingGaps, .25f):0.00} median {OrcBehaviour.Quantile(swingGaps, .5f):0.00} p75 {OrcBehaviour.Quantile(swingGaps, .75f):0.00} s"));
        var labOrc = OrcCalibration.Lab(orc, 900, swingGaps);
        static string Spread(IReadOnlyList<float> d) => d.Count == 0 ? "none" : FormattableString.Invariant(
            $"{d.Count}, p10 {OrcBehaviour.Quantile(d, .1f):0} median {OrcBehaviour.Quantile(d, .5f):0} p90 {OrcBehaviour.Quantile(d, .9f):0}");
        foreach (var name in labOrc.AttackDistances.Keys.Union(donorOrc.AttackDistances.Keys).OrderByDescending(n => donorOrc.AttackDistances.GetValueOrDefault(n)?.Count ?? 0))
            Console.WriteLine($"  orc {name}: donor {Spread(donorOrc.AttackDistances.GetValueOrDefault(name) ?? [])}; lab {Spread(labOrc.AttackDistances.GetValueOrDefault(name) ?? [])}");
        Console.WriteLine(FormattableString.Invariant($"  orc gaps start to start: donor {Spread(donorOrc.Gaps)}; lab {Spread(labOrc.Gaps)}"));
        if (donorOrc.Cut is { Count: > 0 } cutShort) Console.WriteLine($"  orc recordings cut short (read up to the cut): {string.Join(", ", cutShort)}");
        Console.WriteLine(FormattableString.Invariant($"  orc walk (CombatWalk cycles/s): donor {Spread(donorOrc.WalkRates)}; lab {Spread(labOrc.WalkRates)}"));
        var compared = labOrc.AttackDistances.Where(a => a.Value.Count >= 5 && donorOrc.AttackDistances.GetValueOrDefault(a.Key) is { Count: >= 10 }).ToList();
        var within = compared.Where(a => OrcBehaviour.Quantile(a.Value, .5f) is var m
            && m >= OrcBehaviour.Quantile(donorOrc.AttackDistances[a.Key], .1f) && m <= OrcBehaviour.Quantile(donorOrc.AttackDistances[a.Key], .9f)).ToList();
        Check(compared.Count >= 6 && within.Count == compared.Count,
            $"the orc picks each attack from the donor's distances: {within.Count} of {compared.Count} lab medians inside the donor's p10–p90 ({string.Join(", ", compared.Except(within).Select(a => a.Key))})");
        Check(MathF.Abs(labOrc.GapMedian - donorOrc.GapMedian) <= donorOrc.GapMedian * .3f,
            FormattableString.Invariant($"its attacks come at the donor's rhythm: {labOrc.GapMedian:0.00} s from one to the next against {donorOrc.GapMedian:0.00} (medians, within 30%)"));
        Console.WriteLine(FormattableString.Invariant($"  orc walking speed (units/s): donor {Spread(donorOrc.WalkSpeeds)}; lab {Spread(labOrc.WalkSpeeds)}"));
        Console.WriteLine(FormattableString.Invariant($"  orc walks end (units off): donor {Spread(donorOrc.WalkEnds)}; lab {Spread(labOrc.WalkEnds)}"));
        Check(MathF.Abs(labOrc.WalkSpeed - donorOrc.WalkSpeed) <= donorOrc.WalkSpeed * .15f && MathF.Abs(labOrc.WalkRate - donorOrc.WalkRate) <= donorOrc.WalkRate * .05f,
            FormattableString.Invariant($"it walks in at the donor's ground speed: {labOrc.WalkSpeed:0} units a second against {donorOrc.WalkSpeed:0} (medians, within 15%), the clip at the donor's {donorOrc.WalkRate:0.00} cycles a second"));
        float endP25 = OrcBehaviour.Quantile(donorOrc.WalkEnds, .25f), endP75 = OrcBehaviour.Quantile(donorOrc.WalkEnds, .75f), endLab = OrcBehaviour.Quantile(labOrc.WalkEnds, .5f);
        Check(labOrc.WalkEnds.Count >= 10 && endLab >= endP25 && endLab <= endP75,
            FormattableString.Invariant($"its walks end where the donor's did: {endLab:0} units off (median) within the donor's middle half, {endP25:0}–{endP75:0}"));
        // Pressed against a standing player at its hull's distance, it fights on (R10150-1): it steps back and attacks again.
        orc.Spawn();
        orc.PlaceAt(Vector3.UnitZ * Orc.BodyGap * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
        int attacksBefore = orc.AttacksMade;
        float pressed = 0;
        for (; pressed < 15 && orc.AttacksMade == attacksBefore; pressed += Tick) orc.Step(Tick, Vector3.Zero, IncomingSwing.None);
        Check(orc.AttacksMade > attacksBefore,
            FormattableString.Invariant($"an orc pressed against a standing player at 32 units attacks again, after {pressed:0.0} s (stepping back to its uppercut's 34)"));
        // Its wait runs from each attack's end (R10151-4, R10152-3): one that plays out, and one a stun cuts short.
        orc.FixedAttackWait = 2;
        float WaitAfter(bool interrupt)
        {
            orc.Spawn();
            orc.PlaceAt(Vector3.UnitZ * Orc.BodyGap * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
            for (float t = 0; t < 20 && orc.State != OrcState.Attack; t += Tick) orc.Step(Tick, Vector3.Zero, IncomingSwing.None);
            if (orc.State != OrcState.Attack) return -1;
            int made = orc.AttacksMade;
            float waited = 0;
            // A stun between steps starts the wait in the next step; an attack played out, in the step it ends.
            if (interrupt) orc.Struck(new AttackEvent(1, false), 500, Vector3.Zero, staff: true, 1);
            else while (orc.State == OrcState.Attack) orc.Step(Tick, Vector3.Zero, IncomingSwing.None);
            if (interrupt) { orc.Step(Tick, Vector3.Zero, IncomingSwing.None); waited = Tick; }
            // Until it may attack, or attacks (the step its wait runs out in).
            for (; waited < 5 && !orc.MayAttack && orc.AttacksMade == made; waited += Tick) orc.Step(Tick, Vector3.Zero, IncomingSwing.None);
            return waited;
        }
        float played = WaitAfter(false), cut = WaitAfter(true);
        Check(MathF.Abs(played - 2) <= 2 * Tick && MathF.Abs(cut - 2) <= 2 * Tick,
            FormattableString.Invariant($"with a 2 s wait, an attack that plays out and one a stun cuts short each keep it from attacking for 2 s after: {played:0.00} s, {cut:0.00} s"));
        orc.FixedAttackWait = null;
        orc.Spawn();
        if (Environment.GetEnvironmentVariable("RB_ORC") == "1") return;
    }
    else Console.WriteLine("skip the orc's behaviour: no local/recordings");

    // The orc walks in and fights the standing player.
    float start = Vector3.Distance(orc.Feet with { Y = 0 }, product.Player.Feet with { Y = 0 });
    Advance(720);
    float near = Vector3.Distance(orc.Feet with { Y = 0 }, product.Player.Feet with { Y = 0 });
    Check(near < start && near <= 140 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit, FormattableString.Invariant($"the orc walks in from {start:0.0} m to {near:0.0} m, inside its yellow zone"));
    Check(combat.OrcStats.Blows > 0 && combat.OrcStats.Reached > 0, $"the orc's blows reach the player: {combat.OrcStats.Blows} blows, {combat.OrcStats.Reached} reached");
    Check(combat.PlayerHealth < combat.Health.Max || combat.OrcStats.Deaths > 0, FormattableString.Invariant($"they take the player's health: {combat.PlayerHealth:0.#} left"));

    // A still orc in front: no attacks, no parries, then the player's moves against it.
    combat.Orc.Hold = true;
    combat.Orc.Defending.Forced = Defence.None;
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
    combat.Orc.Defending.Forced = Defence.Parry;
    Face();
    int parried = combat.Player.Parried + combat.Player.Blocked;
    Swing(3);
    Console.WriteLine($"  against a parrying orc: {combat.LastBlow}");
    Check(combat.Player.Parried + combat.Player.Blocked == parried + 1, "an orc that parries turns the blade: the verdict reads its parry state");
    Check(swung.Contains(SwordMove.FastEnd), "the parried swing plays on to its recovery: the donor's sword doesn't bounce off a parry");
    combat.Orc.Defending.Forced = Defence.None;
    Face();
    float before = orc.Health;
    combat.Step(Tick, step++, false, false, true, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    for (int i = 0; i < 60; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    Check(MathF.Abs(before - orc.Health - data.Settings.KickDamage) < 1e-3f && orc.State == OrcState.Pushed,
        FormattableString.Invariant($"a kick asks the settings' kick_damage and pushes the orc: {before:0.##} → {orc.Health:0.##}, {orc.State}"));
    // A kick the orc dodges finds it in its dodge's untouchable window: no damage, the dodge plays on (R10151-3).
    combat.Orc.Defending.Forced = Defence.Dodge;
    Face();
    before = orc.Health;
    int missedBefore = combat.Player.Missed;
    combat.Step(Tick, step++, false, false, true, product.Player.Eye, product.Player.Forward, product.Player.Feet);
    bool dodgedKick = false;
    string dodgeClip = "";
    for (int i = 0; i < 60 && !dodgedKick; i++)
    {
        combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
        dodgedKick = combat.Player.Missed > missedBefore;
        dodgeClip = orc.Clip;
    }
    Check(dodgedKick && orc.Health == before && orc.State == OrcState.Dodging,
        FormattableString.Invariant($"a kick reaching an orc inside its dodge's window ({dodgeClip}) does nothing: health {before:0.##} → {orc.Health:0.##}, {orc.State}"));
    combat.Orc.Defending.Forced = Defence.None;

    // Hit reactions through the combat (#10153): a fast attack plays the small hit over what the orc does, a power
    // attack a hit in its place, a kick a push; an orc attacking keeps attacking under the small hit.
    {
        (OrcState State, string Clip, string? Over, float Damage) Strike(int holdSteps, bool kick = false)
        {
            int hits = combat.Player.Hits;
            for (int i = 0; i < holdSteps; i++) combat.Step(Tick, step++, !kick, false, kick && i == 0, product.Player.Eye, product.Player.Forward, product.Player.Feet);
            for (int i = 0; i < 120 && combat.Player.Hits == hits; i++)
                combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet);
            return (orc.State, orc.Clip, orc.HitLayer, combat.LastBlow?.Damage ?? 0);
        }
        void Settle() { for (int i = 0; i < 90; i++) combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet); }
        Settle();
        Face();
        var swift = Strike(3);
        Settle();
        Face();
        var heavy = Strike(40);
        Settle();
        Face();
        var kicked = Strike(1, kick: true);
        Console.WriteLine($"  reactions: fast {swift}, power {heavy}, kick {kicked}");
        string side = "Front";
        Check(swift.Over is { } over && over.StartsWith("Hit_head_", StringComparison.Ordinal) && over.EndsWith("_small", StringComparison.Ordinal) && swift.State != OrcState.Hurt,
            $"a fast attack plays the small hit over the orc, the head's (fast A swings to the left): {swift.Over}, the orc {swift.State}");
        string size = heavy.Damage >= OrcReactions.LongFrom ? "long" : "medium";
        Check(heavy.State == OrcState.Hurt && heavy.Clip.StartsWith("Hit_", StringComparison.Ordinal) && heavy.Clip.EndsWith("_" + size, StringComparison.Ordinal),
            FormattableString.Invariant($"a power attack plays a hit in the orc's place, {size} at {heavy.Damage:0.#} damage: {heavy.Clip}"));
        Check(kicked.State == OrcState.Pushed && kicked.Clip == $"Push_{side}_medium", $"a kick from in front pushes it with Push_Front_medium: {kicked.Clip}");
        // Under an attack: the small hit plays over it, a full hit cuts it short.
        orc.Hold = false;
        Face();
        for (int i = 0; i < 600 && orc.State != OrcState.Attack; i++) orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
        string attacking = orc.Clip;
        orc.React(OrcReactions.Pick(new ReactionBlow(new AttackEvent(.7f, false, ImpactY: 1, ImpactCoeff: .5f), false, 4, false, 1, 180), false, 10));
        bool kept = orc.State == OrcState.Attack && orc.Clip == attacking && orc.HitLayer is not null;
        bool again = OrcReactions.Pick(new ReactionBlow(new AttackEvent(.7f, false, ImpactY: 1, ImpactCoeff: .5f), false, 4, false, 1, 180), false, orc.SinceSmallHit).Clip is null;
        orc.React(OrcReactions.Pick(new ReactionBlow(new AttackEvent(1.5f, false, ImpactY: 1), false, 12, false, 3, 180), false, 10));
        Check(attacking.StartsWith("attack", StringComparison.Ordinal) && kept && again && orc.State == OrcState.Hurt && orc.Clip == "Hit_left_long",
            $"an attacking orc keeps on under the small hit ({attacking}), plays no second within a second, and a full hit cuts its attack short: {orc.Clip}");
        orc.Hold = true;
        // Wounded (below life_tohurt) it walks in Hurtwalk and strafes with Wounded_combatmove_*.
        orc.Spawn();
        orc.Damage(orc.Health * .9f);
        orc.PlaceAt(product.Player.Feet + Vector3.Normalize(product.Player.Forward with { Y = 0 }) * 6);
        orc.Step(Tick, product.Player.Feet, IncomingSwing.None);
        string woundedWalk = orc.Clip;
        Check(orc.Wounded && woundedWalk == "Hurtwalk" && Orc.Hurt("Combatmove_left_small") == "Wounded_combatmove_left",
            $"a wounded orc walks in Hurtwalk ({woundedWalk}) and strafes with Wounded_combatmove_*, as the donor's did below 20% of their health");
        orc.Spawn();
    }

    // Perception through the combat (#10154): an unaware orc stays so with the player standing behind it, hears a run
    // within 500 units 1.4 s on, and a sneak attack asks ×3 and makes it fight.
    {
        var tuned = combat.Tuning;
        combat.Retune(tuned with { Orc = tuned.Orc with { SpawnUnaware = true } });
        orc.Hold = false;
        Vector3 look = Vector3.Normalize(product.Player.Forward with { Y = 0 });
        Vector3 feet = product.Player.Feet, eye = product.Player.Eye;
        void Unaware(float metres)
        {
            orc.Spawn();
            orc.PlaceAt(feet + look * metres);
            orc.TurnTo(look);
        }
        float Seconds(Func<bool> until, Func<int, Vector3> at, float limit)
        {
            float t = 0;
            for (int i = 0; t < limit && !until(); i++, t += Tick)
                combat.Step(Tick, step++, false, false, false, at(i) + (eye - feet), look, at(i));
            return t;
        }
        Unaware(150 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
        Seconds(() => false, _ => feet, 3);
        bool stillQuiet = orc.State == OrcState.Quiet && !orc.Perception.Aware;
        // A run from 800 units behind it at the donor's 180 units a second.
        Unaware(800 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit);
        float run = 180 * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit * Tick;
        float heardAfter = Seconds(() => orc.Perception.Aware, i => feet + look * run * i, 8);
        float units = (800 - 180 * heardAfter);
        float inReach = (800 - 500) / 180f;
        string alarm = orc.Clip;
        Check(stillQuiet && orc.Perception.Aware && Math.Abs(heardAfter - inReach - orc.Perception.Data.HearDelay) <= 2 * Tick && alarm == "surprised",
            FormattableString.Invariant($"an unaware orc doesn't notice a player standing 150 units behind it for 3 s, and hears one running up {heardAfter - inReach:0.00} s after they come within 500 units ({units:0} off), playing {alarm}"));
        // A sneak attack: a power attack on its back asks ×3, and it fights.
        Unaware(1.1f);
        var asleep = orc.Alert;
        int hits = combat.Player.Hits;
        for (int i = 0; i < 40; i++) combat.Step(Tick, step++, true, false, false, eye, look, feet);
        for (int i = 0; i < 60 && combat.Player.Hits == hits; i++) combat.Step(Tick, step++, false, false, false, eye, look, feet);
        var blow = combat.LastBlow;
        var party = new DarkMessiah.Combat.Party("player", "", [blow?.Move ?? ""], blow?.Cycle ?? 0, "#1", LabCombat.PlayerModel);
        float awareAsk = combat.PlayerAsks(party, orc.Party(), blow?.Group, kick: false, 1000, alert: (4, 3));
        Check(asleep == (0, 1) && blow is { Damage: > 0 } && MathF.Abs(blow.Damage - 3 * awareAsk) < 1e-3f && orc.Perception.Aware && orc.State != OrcState.Quiet,
            FormattableString.Invariant($"a sneak attack on its back asks ×3 ({blow?.Damage:0.#} against {awareAsk:0.#} aware, alert {asleep.Level}), and it fights after: alert {orc.Alert.Level}, {orc.State}"));
        combat.Retune(tuned);
        orc.Hold = true;
        orc.Spawn();
    }

    // A jump attack fells only after the player has dropped 50 units below where they left the ground (R10152-1),
    // through the combat: ten from 30 units up, ten from 60, each a press in the air held through the wind-up.
    {
        var ownBuild = combat.Build!;
        combat.Rebuild(ownBuild with { Modules = new HashSet<string>(ownBuild.Modules) { "combat_vertical_attack" } });
        (int Blows, int Falls, float Drop) Leaps(float units, int tries)
        {
            int blows = 0, falls = 0;
            float drop = 0;
            for (int n = 0; n < tries; n++)
            {
                Face();
                var up = Vector3.UnitY * units * RustyBridge.Wire.Donor.SourceSpace.MetresPerUnit;
                combat.Step(Tick, step++, false, false, false, product.Player.Eye + up, product.Player.Forward, product.Player.Feet + up, grounded: true);
                int hits = combat.Player.Hits;
                bool fell = false;
                for (int i = 0; i < 40; i++) combat.Step(Tick, step++, true, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, grounded: false);
                for (int i = 0; i < 40; i++)
                {
                    combat.Step(Tick, step++, false, false, false, product.Player.Eye, product.Player.Forward, product.Player.Feet, grounded: false);
                    fell |= orc.State == OrcState.KnockedDown;
                }
                drop = combat.Drop;
                blows += combat.Player.Hits - hits;
                falls += fell ? 1 : 0;
            }
            return (blows, falls, drop);
        }
        var low = Leaps(30, 10);
        var dropped = Leaps(60, 10);
        combat.Rebuild(ownBuild);
        Check(low.Blows >= 8 && low.Falls == 0 && dropped.Blows >= 8 && dropped.Falls >= 3,
            FormattableString.Invariant($"jump attacks after a {low.Drop:0}-unit drop fell no orc ({low.Falls} of {low.Blows} blows); after {dropped.Drop:0} units, chancetofall 0.6 fells {dropped.Falls} of {dropped.Blows}"));
    }

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
    combat.Orc.Hold = false;
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
        // The daggers' backstab (R10148-2): ready by itself, struck by a press in its idle, let go when no longer ready.
        var stab = new PlayerSword(daggerClips) { FastDelay = 0, BackstabReady = true };
        stab.Step(Tick, false, false, false);
        bool readies = stab.Move == SwordMove.BackstabIn;
        for (int i = 0; i < 60 && stab.Move == SwordMove.BackstabIn; i++) stab.Step(Tick, false, false, false);
        bool idles = stab.Move == SwordMove.BackstabIdle;
        stab.Step(Tick, true, false, false);
        bool strikes = stab.Move == SwordMove.Backstab && stab.SwingStarted && stab.Clip.Clip == "layer_backstab_out";
        var letGo = new PlayerSword(daggerClips) { FastDelay = 0, BackstabReady = true };
        for (int i = 0; i < 40; i++) letGo.Step(Tick, false, false, false);
        letGo.BackstabReady = false;
        letGo.Step(Tick, false, false, false);
        Check(readies && idles && strikes && letGo.Move == SwordMove.BackstabCancel,
            "with daggers behind a victim the backstab readies (backstab_in, then its idle), a press strikes (backstab_out), and losing the back lets it go (backstab_cancel)");
        // Losing the back on the step of the press lets go and doesn't strike (R10148-4).
        var lostAndPressed = new PlayerSword(daggerClips) { FastDelay = 0, BackstabReady = true };
        for (int i = 0; i < 60 && lostAndPressed.Move != SwordMove.BackstabIdle; i++) lostAndPressed.Step(Tick, false, false, false);
        int swingsAt = lostAndPressed.Swing;
        lostAndPressed.BackstabReady = false;
        lostAndPressed.Step(Tick, true, false, false);
        Check(lostAndPressed.Move == SwordMove.BackstabCancel && lostAndPressed.Swing == swingsAt && !lostAndPressed.SwingStarted,
            $"a press on the step the back is lost lets the backstab go and strikes nothing ({lostAndPressed.Move})");
        // Held to A's end, the daggers go through its transition into the power attack's hold (the goblin fight, 6 of 6).
        var held = new PlayerSword(daggerClips) { FastDelay = 0, ChainLength = 5 };
        held.Step(Tick, true, false, false);
        held.Step(Tick, false, false, false);
        for (int i = 0; i < 200 && !(held.Move == SwordMove.FastEnd && held.Cycle >= PlayerSword.FastEndOpen); i++) held.Step(Tick, false, false, false);
        var path = new List<string>();
        for (int i = 0; i < 120; i++) { held.Step(Tick, true, false, false); if (path.LastOrDefault() != held.Clip.Clip) path.Add(held.Clip.Clip); }
        Check(path.SequenceEqual(["layer_fastattack_daggers_A", "layer_transition_daggers_A", "layer_bigslash_Daggers_idle"]),
            $"the daggers' A, held to its end, goes through transition_daggers_A into the power attack's hold: {string.Join(" > ", path.Select(c => c.Replace("layer_", "")))}");
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
        // The backstab's readiness through LabCombat (R10148-2): an orc 1.2 m ahead with its back turned readies it; on
        // guard (combatidle) it doesn't; turned towards the player it doesn't.
        {
            Vector3 eye = product.Player.Eye, look = product.Player.Forward with { Y = 0 }, at = product.Player.Feet;
            look = Vector3.Normalize(look);
            SwordMove ReadiesOn(string sequence, Vector3 facing)
            {
                combat.Reset();
                for (int i = 0; i < 3; i++)
                    combat.Step(Tick, step++, false, false, false, eye, look, at, new OrcPose("#7.1", at + look * 1.2f, facing, 100, sequence, 0, 1, null, 0, true, [Orc.Cleaver]));
                var move = combat.Sword.Move;
                combat.Orc.StopFollowing();
                return move;
            }
            var (back, guarded, facingPlayer) = (ReadiesOn("idle_0", look), ReadiesOn("combatidle", look), ReadiesOn("idle_0", -look));
            // Through the combat (R10148-4): readied to its idle, then the orc turns to face the player as attack goes
            // down. The backstab lets go, and nothing is struck.
            combat.Reset();
            var turned = new OrcPose("#7.1", at + look * 1.2f, -look, 100, "idle_0", 0, 1, null, 0, true, [Orc.Cleaver]);
            for (int i = 0; i < 60 && combat.Sword.Move != SwordMove.BackstabIdle; i++)
                combat.Step(Tick, step++, false, false, false, eye, look, at, turned with { Facing = look });
            bool idled = combat.Sword.Move == SwordMove.BackstabIdle;
            var (hitsBefore, logBefore) = (combat.Player.Hits, combat.Criticals);
            combat.Step(Tick, step++, true, false, false, eye, look, at, turned);
            var afterTurn = combat.Sword.Move;
            for (int i = 0; i < 60; i++) combat.Step(Tick, step++, false, false, false, eye, look, at, turned);
            combat.Orc.StopFollowing();
            Check(idled && afterTurn == SwordMove.BackstabCancel && combat.Player.Hits == hitsBefore,
                $"through the combat, the orc turning as attack goes down lets the readied backstab go, with no blow ({afterTurn}, hits {combat.Player.Hits - hitsBefore})");
            Check(back == SwordMove.BackstabIn && guarded == SwordMove.Ready && facingPlayer == SwordMove.Ready,
                $"the backstab readies on an orc's turned back ({back}), not on one on guard ({guarded}) or facing the player ({facingPlayer})");
        }
        // The lab's own orc, unaware (#10154): idling with its back turned it is no guard, the backstab readies, and the
        // blow kills it (die_backstabbed).
        {
            var tuned = combat.Tuning;
            combat.Retune(tuned with { Orc = tuned.Orc with { SpawnUnaware = true } });
            combat.Reset();
            Vector3 eye = product.Player.Eye, look = Vector3.Normalize(product.Player.Forward with { Y = 0 }), at = product.Player.Feet;
            combat.Orc.PlaceAt(at + look * 1.2f);
            combat.Orc.TurnTo(look);
            for (int i = 0; i < 60 && combat.Sword.Move != SwordMove.BackstabIdle; i++) combat.Step(Tick, step++, false, false, false, eye, look, at);
            bool readied = combat.Sword.Move == SwordMove.BackstabIdle && combat.Orc.State == OrcState.Quiet;
            combat.Step(Tick, step++, true, false, false, eye, look, at);
            string stabbed = "";
            for (int i = 0; i < 120 && combat.Orc.Alive; i++)
            {
                combat.Step(Tick, step++, false, false, false, eye, look, at);
                if (combat.Orc.State == OrcState.Backstabbed) stabbed = combat.Orc.Clip;
            }
            Check(readied && stabbed == "die_backstabbed" && !combat.Orc.Alive,
                $"the daggers' backstab readies on the lab's own unaware orc, its back turned in its idle, and kills it ({stabbed})");
            combat.Retune(tuned);
            combat.Reset();
        }
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
    combat.Orc.Hold = true;
    // The body's movement, case by case (#10159, R10159-2): each steady speed, start, stop, slide and jump of the donor's
    // against the lab's body on the same ticks, compared unless the recording shows why the lab's body couldn't follow
    // (its arena's wall, a setpos, the donor held by its level, a slope; MovementCalibration.Compare).
    void MovementCases(ReplayReport report, IReadOnlyList<MovementCase> cases)
    {
        foreach (var c in cases.Where(c => !c.Agrees)) Console.WriteLine($"  movement {c}");
        var compared = cases.Where(c => c.Compared).ToList();
        Check(compared.Count > 0 && compared.All(c => c.Agrees),
            $"{report.Name}: each movement case the lab's body could follow agrees with the donor's ({compared.Count(c => c.Agrees)} of {compared.Count}; {cases.Count - compared.Count} not compared, each for its reason)");
    }
    foreach (var path in found.Where(p => RecordingVersion(p) >= 12))
    {
        var report = CombatReplay.Run(combat, path, product);
        // From Wire v14 the commands drive it: kicks are requests, and movement, jump and use move the lab's own body.
        if (report.Kicks is { } kicks && report.Body is { } moved)
        {
            Check(kicks.Requests >= kicks.DonorBegan && kicks.Requests >= kicks.LabBegan && kicks.Requests > 0,
                $"{report.Name}: kicks are the recorded requests ({kicks})");
            Console.WriteLine($"  {moved}");
            if (moved.Donor is { } d && moved.Lab is { } l) MovementCases(report, MovementCalibration.Compare(report.Name, d, l));
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
        // #10161: the lab's camera against the donor's drawn one where the donor's head, a blow, a grab or strafing turned
        // it. The hand-played fights within 1.5° on 40% of those ticks and 2° at the median; the other arena fights 3° at
        // the median. What it doesn't follow: the power attack's blend in from the charge, the view's kicks of attacks the
        // lab hasn't measured (the ghouls'), and the daggers' and staff's (3.4° to 5.6° in the weapon experiments, which
        // only report: the staff's camera sits 5° down at rest, unexplained).
        if (report.Camera is { } cam && !report.Name.Contains("daggers", StringComparison.Ordinal) && !report.Name.Contains("staff", StringComparison.Ordinal))
        {
            bool hand = report.Name.Contains("arena-hand", StringComparison.Ordinal);
            Check(hand ? cam.Near >= cam.Ticks * 4 / 10 && cam.Median <= 2 : cam.Median <= 3,
                FormattableString.Invariant($"{report.Name}: the lab's camera turns as the donor's within {CombatReplay.CameraTolerance}° on {cam.Near} of {cam.Ticks} ticks (median {cam.Median:0.0}°)"));
            // And in place, every tick (R10161-4): 9 units above the eye and 2.1 ahead, turned with the view.
            Check(report.CameraPlace is { } place && place.Near >= place.Ticks * 8 / 10 && place.Median <= .5f,
                FormattableString.Invariant($"{report.Name}: the lab's camera stands within {CombatReplay.CameraPlaceTolerance} unit of the donor's on {report.CameraPlace?.Near} of {report.CameraPlace?.Ticks} ticks (median {report.CameraPlace?.Median:0.00})"));
        }
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
    {
        // The calibration's own: the movement, jump-hold and sprint scenarios (#10159), replayed in the open.
        var scenarioCases = new List<MovementCase>();
        foreach (var path in Directory.Exists(Path.Combine(recordings, "experiments")) ? Directory.GetFiles(Path.Combine(recordings, "experiments"), "*-v18-*.rbrec")
                     .Where(p => new[] { "-movement", "-jump-hold", "-sprint" }.Any(n => Path.GetFileName(p).Contains(n, StringComparison.Ordinal))).Order().ToArray() : [])
        {
            var moved = CombatReplay.Run(combat, path, product);
            if (moved.Body is { Donor: { } d, Lab: { } l })
            {
                var cases = MovementCalibration.Compare(moved.Name, d, l);
                MovementCases(moved, cases);
                scenarioCases.AddRange(cases);
            }
            // The lab's stamina tick by tick against the donor's streamed one, through sprints, an empty gauge, kicks and
            // regeneration (#10160).
            if (Path.GetFileName(path).Contains("-sprint", StringComparison.Ordinal))
                Check(moved.Stamina is { } st && st.Ticks > 1000 && st.Near == st.Ticks,
                    FormattableString.Invariant($"{moved.Name}: the lab's stamina follows the donor's within 0.2 on {moved.Stamina?.Near} of {moved.Stamina?.Ticks} ticks (worst {moved.Stamina?.Worst:0.00})"));
        }
        // The donor's health through blows, potions, extra_health learned and fast_metabolism's regeneration (#10162).
        if (Path.Combine(recordings, "experiments", "l01_a-v18-health.rbrec") is var healthPath && File.Exists(healthPath))
        {
            var healthReplay = CombatReplay.Run(combat, healthPath, product);
            foreach (var line in healthReplay.Lines().Where(l => l.Contains("health:", StringComparison.Ordinal))) Console.WriteLine(line);
            Check(healthReplay.Health is { } hr && hr.Ticks > 5000 && hr.Near >= hr.Ticks * 99 / 100 && hr.MaxAgree == hr.Ticks,
                FormattableString.Invariant($"{healthReplay.Name}: the lab's health follows the donor's within {CombatReplay.HealthTolerance} on {healthReplay.Health?.Near} of {healthReplay.Health?.Ticks} ticks, its maximum on {healthReplay.Health?.MaxAgree}"));
        }
        // And they cover each: the run, walk (+speed), crouch, backing, strafe, diagonal and sprint's speeds, a start from
        // rest each of those ways (R10159-3), stops and slides from a run, and jumps' rise and time in the air, two compared cases at least.
        if (scenarioCases.Count > 0)
        {
            var covered = new[] { ("speed", "forward 1000"), ("speed", "forward 1000 speed"), ("speed", "forward 1000 walk"), ("speed", "forward 1000 crouched"), ("speed", "back 150"),
                    ("speed", "strafe 1000"), ("speed", "diagonal 1400"), ("speed", "forward 1000 sprint"), ("start", "forward 1000"), ("start", "strafe 1000"), ("start", "diagonal 1400"),
                    ("start", "back 150"), ("start", "forward 1000 crouched"), ("start", "forward 1000 speed"), ("start", "forward 1000 walk"), ("start", "forward 1000 sprint"), ("stop", "from a run"), ("slide", "from a run"), ("rise", "none"), ("air", "none") }
                .Select(k => (Case: $"{k.Item1} {k.Item2}", Count: scenarioCases.Count(c => c.Compared && c.Kind == k.Item1 && c.Label == k.Item2))).ToList();
            Check(covered.All(k => k.Count >= 2),
                $"the movement scenarios compare every kind of movement twice or more ({string.Join(", ", covered.Select(k => $"{k.Case} {k.Count}"))})");
        }
        else Console.WriteLine("skip the movement scenarios: no local/recordings/experiments/l01_a-v18-movement.rbrec");
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
        (jumpAttack, new[] { "jump attack", "power attack" }), (jumpHold, new[] { "jump attack", "power attack" }), (jumpBasic, new[] { "power attack" }), (rushAttack, new[] { "rush attack" }) })
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
        // The donor's hands taken by an orc's grab and given back (R10148-1): the lab's quick draws begin with its.
        if (report.Moves.TryGetValue("quick draw", out var quick))
            Check(quick.Donor > 0 && quick.Together == quick.Donor && quick.Lab == quick.Donor,
                $"{report.Name}: the lab's hands are taken and its quick draw begins with the donor's ({quick.Together} of {quick.Donor}, lab {quick.Lab})");
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
    // The backstab replayed (#10148, R10148-2): every donor backstab begun with it, and the goblin fight's moves, its
    // power attacks through the daggers' transition.
    {
        var stabbed = new[] { "backstab-1", "backstab" }.Select(n => Path.Combine(recordings, "experiments", $"l01_a-v17-{n}.rbrec")).Where(File.Exists).ToList();
        int donorStabs = 0, together = 0, labStabs = 0;
        foreach (var path in stabbed)
        {
            var m = CombatReplay.Run(combat, path, product).Moves.GetValueOrDefault("backstab");
            (donorStabs, together, labStabs) = (donorStabs + m.Donor, together + m.Together, labStabs + m.Lab);
        }
        if (stabbed.Count > 0)
            Check(donorStabs >= 3 && together * 10 >= donorStabs * 8,
                $"the lab begins the donor's backstabs with it: {together} of {donorStabs} (lab {labStabs})");
        string goblin = Path.Combine(recordings, "experiments", "l01_a-v17-daggers-goblin.rbrec");
        if (File.Exists(goblin))
        {
            var report = CombatReplay.Run(combat, goblin, product);
            foreach (var family in new[] { "fast attack", "power attack" })
            {
                var m = report.Moves.GetValueOrDefault(family);
                Check(m.Donor > 0 && m.Together * 10 >= m.Donor * 8, $"{report.Name}: the lab begins the donor's {family}s with it ({m.Together} of {m.Donor}, lab {m.Lab})");
            }
        }
    }
    // The shield replayed (#10149): its blocks and bashes as the donor's.
    string shielded = Path.Combine(recordings, "experiments", "l01_a-v17-shield.rbrec");
    if (File.Exists(shielded))
    {
        var report = CombatReplay.Run(combat, shielded, product);
        var bashes = report.Moves.GetValueOrDefault("bash");
        Check(bashes.Donor >= 5 && bashes.Together * 10 >= bashes.Donor * 8, $"{report.Name}: the lab bashes with the donor ({bashes.Together} of {bashes.Donor}, lab {bashes.Lab})");
        var bashBlows = report.PlayerBlows.Matches.Where(m => m.Donor?.Move == SwordClips.BashClip).ToList();
        Check(bashBlows.Count >= 3 && bashBlows.Count(m => m.Lab?.Verdict == m.Donor!.Verdict) * 10 >= bashBlows.Count * 8,
            $"{report.Name}: the bashes that landed land in the lab too ({bashBlows.Count(m => m.Lab?.Verdict == m.Donor!.Verdict)} of {bashBlows.Count}; hit groups {bashBlows.Count(m => m.Result == "agree")} alike)");
        var blocks = report.OrcBlows.Matches.Where(m => m.Donor?.Verdict == DefenseResult.PlayerParried).ToList();
        Check(blocks.Count >= 15 && blocks.Count(m => m.Lab?.Verdict == DefenseResult.PlayerParried) * 10 >= blocks.Count * 8
            && report.OrcBlows.Count("agree") * 10 >= report.OrcBlows.Donor * 8,
            $"{report.Name}: the shield blocks the donor's blocked blows ({blocks.Count(m => m.Lab?.Verdict == DefenseResult.PlayerParried)} of {blocks.Count}) and lets the side blows through (the orc's blows agree {report.OrcBlows.Count("agree")} of {report.OrcBlows.Donor})");
        Check(report.Shield is { } wear && wear.Alike * 10 >= wear.Donor * 8, $"{report.Name}: blocks wear the shield as the donor's did: {report.Shield}");
        // Replayed again with the recording's shield already on the arm and worn, it wears and blocks the same (R10149-1).
        combat.Rebuild(WithSkill("combat", 2) with { OffHand = SmallShield });
        combat.WearShield(100);
        var again = CombatReplay.Run(combat, shielded, product);
        combat.Rebuild(file);
        Check(again.Shield == report.Shield && again.OrcBlows.Count("agree") == report.OrcBlows.Count("agree") && again.PlayerBlows.Count("agree") == report.PlayerBlows.Count("agree"),
            $"{report.Name}: replayed again with a worn shield of the same model on the arm, the same blocks and wear ({again.Shield})");
    }
    else Console.WriteLine("skip the shield replay: no local/recordings/experiments/l01_a-v17-shield.rbrec");
    // The followed orc's rules (#10198), on the v17 fatality run: the donor's dodges are untouchable in the lab too, the
    // turn strikes land, and more than half the player's blows agree.
    string fatality17 = Path.Combine(recordings, "experiments", "l01_a-v17-fatality.rbrec");
    if (File.Exists(fatality17))
    {
        var report = CombatReplay.Run(combat, fatality17, product);
        var dodged = report.PlayerBlows.Matches.Where(m => m.Donor?.Verdict == DefenseResult.NoEffect).ToList();
        Check(dodged.Count >= 5 && dodged.Count(m => m.Lab?.Verdict == DefenseResult.NoEffect) * 10 >= dodged.Count * 8,
            $"{report.Name}: a blow on a dodging orc does nothing in the lab as in the donor ({dodged.Count(m => m.Lab?.Verdict == DefenseResult.NoEffect)} of {dodged.Count})");
        var turns = report.OrcBlows.Matches.Where(m => m.Donor?.Move.StartsWith("Turn_strike", StringComparison.Ordinal) == true).ToList();
        Check(turns.Count >= 5 && turns.All(m => m.Result == "agree") && report.OrcBlows.Count("agree") * 10 >= report.OrcBlows.Donor * 9,
            $"{report.Name}: the followed orc's turn strikes land with the donor's ({turns.Count(m => m.Result == "agree")} of {turns.Count}; its blows {report.OrcBlows.Count("agree")} of {report.OrcBlows.Donor})");
        Check(report.PlayerBlows.Count("agree") * 2 > report.PlayerBlows.Donor,
            $"{report.Name}: most of the player's blows agree with the donor's ({report.PlayerBlows.Count("agree")} of {report.PlayerBlows.Donor}; #10205 for the rest)");
    }
    else Console.WriteLine("skip the v17 fatality replay: no local/recordings/experiments/l01_a-v17-fatality.rbrec");
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

    // The chained slash at combat levels 1 and 2 (#10143, R10143-1): learned in the character sheet mid-recording, the
    // replay rebuilding at each purchase; every clip of the chain begun with the donor's.
    string levels = Path.Combine(recordings, "experiments", "l01_a-v17-chained-slash-levels.rbrec");
    if (File.Exists(levels))
    {
        var report = CombatReplay.Run(combat, levels, product);
        var clips = "ABCDE".Select(c => report.Clips!.GetValueOrDefault($"layer_fastattack_sword_noshield_{c}")).ToList();
        Check(report.Rebuilt is { Count: >= 2 } && clips.All(c => c.Donor >= 10 && c.Together * 10 >= c.Donor * 8),
            $"{report.Name}: at combat levels 1 and 2 the lab chains A to E with the donor ({string.Join(", ", clips.Select((c, i) => $"{"ABCDE"[i]} {c.Together} of {c.Donor}"))})");
    }
    else Console.WriteLine("skip the chained-slash levels replay: no local/recordings/experiments/l01_a-v17-chained-slash-levels.rbrec");

    // The sword's timing sweeps (#10142, R10142-1): presses after kicks, directions after B, short taps, presses through A's
    // recovery and held in the air through kicks, each move begun with the donor's and each power attack as its own clip.
    foreach (var name in new[] { "sword-timing", "sword-timing2-fastend", "sword-timing2" })
    {
        string path = Path.Combine(recordings, "experiments", $"l01_a-v17-{name}.rbrec");
        if (!File.Exists(path)) { Console.WriteLine($"skip the {name} replay: no {path}"); continue; }
        var report = CombatReplay.Run(combat, path, product);
        foreach (var family in new[] { "fast attack", "power attack", "kick" })
        {
            var m = report.Moves.GetValueOrDefault(family);
            if (m.Donor == 0) continue;
            Check(m.Together * 10 >= m.Donor * 9, $"{report.Name}: the lab begins the donor's {family}s with it ({m.Together} of {m.Donor}, lab {m.Lab})");
        }
        var directions = report.Clips!.Where(c => c.Key.Contains("bigslash") && c.Key.EndsWith("_out")).ToList();
        if (directions.Count > 0)
            Check(directions.All(c => c.Value.Together == c.Value.Donor),
                $"{report.Name}: each power attack's direction as the donor's: {string.Join(", ", directions.Select(c => $"{c.Key.Replace("layer_bigslash_sword_noshield_", "")} {c.Value.Together} of {c.Value.Donor}"))}");
    }

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
        // This recording's player took a shield: since #10149 the lab wears it, and compares what happens while it's held.
        Check(report.Build is string held && !held.Contains("not played", StringComparison.Ordinal)
            && !report.NotCompared.Any(n => n.Contains("shield", StringComparison.Ordinal)),
            "a recorded shield is played: the replay leaves nothing out while it's held (#10149)");
        Check(combat.Build?.Source == BuildFile.Path, "after the replay the lab plays build.json again");
    }
    else Console.WriteLine("skip the v15 build replay: no local/recordings/experiments/l01_a-v15-stats.rbrec");
    // Kick requests while the donor holds unplayable gear aren't compared either (R10140-3): a timeline of 200 ticks with
    // a kick request inside a shield stretch (ticks 40-60) and one outside it (150) counts one request.
    {
        Vector3 eye = product.Player.Eye, look = product.Player.Forward, at = product.Player.Feet;
        var ticks = Enumerable.Range(0, 200).Select(i => new ReplayTick(1000 + i, i * (double)Tick, Tick, RustyBridge.Wire.InputButtons.None, i is 50 or 150, eye, look, at, null,
            KickHeld: i is >= 50 and < 56 or >= 150 and < 156)).ToList();
        var synthetic = new InputTimeline { Name = "synthetic", Ticks = ticks, HasCommands = true, Unplayable = [(1040, 1060, "a shield (test)")] };
        var report = CombatReplay.Run(combat, synthetic);
        Check(report.Kicks is { Requests: 1 } k && k.LabBegan == 1,
            $"a kick request in a stretch with unplayable gear isn't compared, one outside it is: {report.Kicks}");
    }
});

// The knocked-down orc as an Engine ragdoll (#10216). The test host evaluates a published instance's pose as each call
// settles (rusty-engine #10222), and the ragdoll spawns from it, so here every update is its own call, as a running
// product's are. Felling blows from around the orc until it has got up from both sides, each update's pose checked:
// down until its get-up plays, never back in the pose it was struck in (R10216-1).
{
    LabProduct? lab = null;
    ulong labStep = 0;
    void Update() => host.Call(_ => lab!.Update(new ProductUpdate(new ProductUpdateFacts(ProductLifecycleState.Running, 1, 1, 0, labStep++, 60, 1, 0, Tick), default)));
    host.Call(engine =>
    {
        var files = content.Select(kv => new ProductContentFile(Encoding.UTF8.GetBytes(kv.Key), kv.Value)).ToArray();
        lab = new LabProduct(new ProductCreateContext(engine, new ProductContent(files, engine.Content),
            new ProductInputConfiguration(default, default, default, default, InputCursorMode.PointerLock), default!));
        lab.Start();
    });
    var ragdollOrc = lab!.Combat.Orc;
    ragdollOrc.Hold = true;
    ragdollOrc.Defending.Forced = Defence.None;
    float HeadUp() => host.Call(_ => { var t = ragdollOrc.Targets()[0]; return t.Pose[t.Joints["SK_Head"]].World.Translation.Y - ragdollOrc.Feet.Y; });
    var sides = new List<(string Side, float Seconds, float Lowest, float HighestDown, bool Ragdolled)>();
    int[] around = [0, 180, 90, -90, 150, -150, 30, -30];
    for (int b = 0; b < around.Length && sides.Select(s => s.Side).Distinct().Count() < 2; b++)
    {
        host.Call(_ => { ragdollOrc.Spawn(); ragdollOrc.PlaceAt(lab.Player.Feet + Vector3.Normalize(lab.Player.Forward with { Y = 0 }) * 1.5f); });
        for (int i = 0; i < 30; i++) Update();
        host.Call(_ =>
        {
            var toPlayer = (lab.Player.Feet - ragdollOrc.Feet) with { Y = 0 };
            var from = ragdollOrc.Feet + Vector3.Transform(Vector3.Normalize(toPlayer) * 1.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, around[b] * MathF.PI / 180));
            ragdollOrc.Struck(new AttackEvent(1, false, ChanceToFall: 1, ImpactY: b % 2), 0, from, false, 1);
        });
        bool ragdolled = false;
        float down = 0, lowest = float.MaxValue, highestDown = 0;
        for (; ragdollOrc.State == OrcState.KnockedDown && !(ragdollOrc.Clip.StartsWith("StandUp_") && !ragdollOrc.Clip.EndsWith("_firstframe")) && down < 15; down += Tick)
        {
            Update();
            ragdolled |= ragdollOrc.RagdollState.Ragdolled;
            float head = HeadUp();
            lowest = MathF.Min(lowest, head);
            // From the moment its head has come down until its get-up plays, every pose lies down.
            if (lowest < .5f && !(ragdollOrc.Clip.StartsWith("StandUp_") && !ragdollOrc.Clip.EndsWith("_firstframe"))) highestDown = MathF.Max(highestDown, head);
        }
        string side = ragdollOrc.Clip.StartsWith("StandUp_front") ? "front" : "back";
        // Its get-up's first frames too, which begin lying.
        for (int i = 0; i < 3; i++) { highestDown = MathF.Max(highestDown, HeadUp()); Update(); }
        sides.Add((side, down, lowest, highestDown, ragdolled));
    }
    foreach (var s in sides) Console.WriteLine(FormattableString.Invariant($"  ragdoll knockdown: lay on its {s.Side}, head down to {s.Lowest:0.00} m, highest once down {s.HighestDown:0.00} m, get-up after {s.Seconds:0.00} s"));
    Check(sides.Count > 0 && sides.All(s => s.Ragdolled && s.Lowest < .5f),
        $"headless, every felling blow drops an Engine ragdoll and its head reaches the ground: {sides.Count(s => s.Ragdolled && s.Lowest < .5f)} of {sides.Count}");
    Check(sides.Select(s => s.Side).Distinct().Count() == 2 && sides.All(s => s.HighestDown < 1.2f),
        FormattableString.Invariant($"it gets up from both sides, and from its fall through the blend back into its get-up it never stands as it was struck (head at most {sides.Select(s => s.HighestDown).DefaultIfEmpty(0).Max():0.00} m, standing {1.7:0.0})"));
    Check(sides.All(s => s.Seconds is >= 2.9f and <= 4.2f),
        $"each get-up starts inside the donor's 2.9–4.2 s after the blow: {string.Join(", ", sides.Select(s => s.Seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))} s");
    host.Call(_ => lab.Dispose());
}
return failures == 0 ? 0 : 1;

// A recording's format version, from its first frame.
static uint RecordingVersion(string path)
{
    using var stream = File.OpenRead(path);
    return RustyBridge.Wire.RecordingReader.Frames(stream).FirstOrDefault()?.Version ?? 0;
}
