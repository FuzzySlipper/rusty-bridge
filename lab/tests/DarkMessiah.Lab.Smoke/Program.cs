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
                        var (feet, _, _) = fitBody.Step(move, look, false, false, false, false, Tick);
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
                var (feet, _, _) = fitBody.Step(Vector2.UnitY, Vector3.UnitX, false, false, false, false, Tick, sprint: true);
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
        // Jumps by how long jump is held, against l01_a-v18-jump-hold (34, 36, 39, 39, 41, 41, 41, 41 units).
        foreach (float release in new[] { 2f, 2.2f, 2.4f })
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
                    var (feet, _, _) = fitBody.Step(Vector2.Zero, Vector3.UnitX, t == 0, t < hold, false, false, Tick);
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
        for (int i = 0; i < 125; i++) combat.Health.Step(Tick);
        float regained = combat.PlayerHealth - potion;
        combat.UseBelt(1);
        float full = combat.PlayerHealth;
        combat.UseBelt(1);
        Check(max == 70 && potion == 50 && MathF.Abs(regained - .5f) < 1e-4f && full == 70 && combat.Belt[0]?.Count == 2 && combat.Belt[1] is null,
            FormattableString.Invariant($"extra_health 2 makes 70 ({max}); a life potion 40 to {potion}; fast_metabolism regains {regained:0.##} in 2.08 s; the full life potion fills it ({full}) and its slot is empty after"));
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
        var perceptionRecordings = Directory.EnumerateFiles(Path.Combine(repo!, "local", "recordings"), "*.rbrec", SearchOption.AllDirectories)
            .Concat(Directory.Exists(Path.Combine(repo!, "local", "perception")) ? Directory.EnumerateFiles(Path.Combine(repo!, "local", "perception"), "*.rbrec") : []).ToList();
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
        (float Mate, float First, string Victim) HitMate(bool kick)
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
            ulong t = 0;
            for (int i = 0; i < 120; i++)
            {
                combat.Mate.PlaceAt(mateAt);
                combat.Step(Tick, t++, !kick && i < 3, false, kick && i < 3, feet + Vector3.UnitY * 1.6f, look, feet);
            }
            combat.God = wasGod;
            foreach (var o in new[] { combat.Orc, combat.Mate }) { o.Hold = false; o.Defending.Forced = null; }
            return (mate - combat.Mate.Health, first - combat.Orc.Health, combat.Victim == combat.Mate ? "the mate" : "the first orc");
        }
        var swungAt = HitMate(false);
        var kickedAt = HitMate(true);
        Check(swungAt.Mate > 0 && swungAt.First == 0 && kickedAt.Mate > 0 && kickedAt.First == 0 && swungAt.Victim == "the mate",
            FormattableString.Invariant($"the player's blows land on the squad-mate they reach (R10154-3): a fast attack takes {swungAt.Mate:0.##} and a kick {kickedAt.Mate:0.##} from it, none from the first orc 20 m off ({swungAt.First:0.##}, {kickedAt.First:0.##})"));
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
    // The body's movement over every v14 replay (#10159): the donor's and the lab's, on the same commands.
    var donorMove = MovementMeasure.Empty();
    var labMove = MovementMeasure.Empty();
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
            if (moved.Donor is { } d && moved.Lab is { } l) { MovementCalibration.Analyze(d, donorMove); MovementCalibration.Analyze(l, labMove); }
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
        static float Med(IReadOnlyList<float>? v) => v is null ? float.NaN : MovementMeasure.Median(v);
        // The fights' replays move the lab's body about its own arena, into walls the donor's level hasn't: reported.
        foreach (var (c, v) in donorMove.Steady.Where(x => x.Value.Count >= 30).OrderBy(x => x.Key.ToString()))
            Console.WriteLine(FormattableString.Invariant($"  fights' movement {c}: donor {Med(v):0} units/s, lab {Med(labMove.Steady.GetValueOrDefault(c)):0}"));
        // The calibration's own: the movement and jump-hold scenarios (#10159), replayed in the open.
        donorMove = MovementMeasure.Empty();
        labMove = MovementMeasure.Empty();
        foreach (var path in Directory.Exists(Path.Combine(recordings, "experiments")) ? Directory.GetFiles(Path.Combine(recordings, "experiments"), "*-v18-*.rbrec")
                     .Where(p => new[] { "-movement", "-jump-hold", "-sprint" }.Any(n => Path.GetFileName(p).Contains(n, StringComparison.Ordinal))).Order().ToArray() : [])
        {
            var moved = CombatReplay.Run(combat, path, product);
            if (moved.Body is { Donor: { } d, Lab: { } l }) { MovementCalibration.Analyze(d, donorMove); MovementCalibration.Analyze(l, labMove); }
            // The lab's stamina tick by tick against the donor's streamed one, through sprints, an empty gauge, kicks and
            // regeneration (#10160).
            if (Path.GetFileName(path).Contains("-sprint", StringComparison.Ordinal))
                Check(moved.Stamina is { } st && st.Ticks > 1000 && st.Near >= st.Ticks * 95 / 100,
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
        // Steady speed by command (30 ticks each at least), within 10%.
        var speeds = donorMove.Steady.Where(x => x.Value.Count >= 30 && labMove.Steady.GetValueOrDefault(x.Key) is { Count: >= 30 })
            .Select(x => (x.Key, Donor: Med(x.Value), Lab: Med(labMove.Steady[x.Key]))).OrderBy(x => x.Key.ToString()).ToList();
        foreach (var (c, d, l) in speeds) Console.WriteLine(FormattableString.Invariant($"  movement {c}: donor {d:0} units/s, lab {l:0}"));
        var off = speeds.Where(x => MathF.Abs(x.Lab - x.Donor) > .1f * x.Donor).ToList();
        Check(speeds.Count >= 4 && off.Count == 0,
            $"replayed, the lab's body moves at the donor's speed on each command within 10% ({speeds.Count - off.Count} of {speeds.Count}{(off.Count > 0 ? "; off: " + string.Join(", ", off.Select(x => x.Key)) : "")})");
        float dAccel = Med(donorMove.Accelerations.SelectMany(x => x.Value).ToList()), lAccel = Med(labMove.Accelerations.SelectMany(x => x.Value).ToList());
        // Stops from a run (160 to 200 units/s): a sprint's slide is longer.
        static bool FromRun((float Seconds, float Slide, float From) x) => x.From is >= 160 and <= 200;
        float dStop = Med(donorMove.Stops.Where(FromRun).Select(x => x.Seconds).ToList()), lStop = Med(labMove.Stops.Where(FromRun).Select(x => x.Seconds).ToList());
        float dSlide = Med(donorMove.Stops.Where(FromRun).Select(x => x.Slide).ToList()), lSlide = Med(labMove.Stops.Where(FromRun).Select(x => x.Slide).ToList());
        Console.WriteLine(FormattableString.Invariant($"  movement starts: donor {dAccel:0.00} s to 90% ({donorMove.Accelerations.Sum(x => x.Value.Count)}), lab {lAccel:0.00} ({labMove.Accelerations.Sum(x => x.Value.Count)}); stops: donor {dStop:0.00} s, {dSlide:0} units ({donorMove.Stops.Count}), lab {lStop:0.00} s, {lSlide:0} units ({labMove.Stops.Count})"));
        Check(MathF.Abs(lAccel - dAccel) <= .15f && MathF.Abs(lStop - dStop) <= .15f && MathF.Abs(lSlide - dSlide) <= .3f * dSlide,
            FormattableString.Invariant($"it starts and stops as the donor's did: to 90% in {lAccel:0.00} s against {dAccel:0.00}, stopped in {lStop:0.00} s against {dStop:0.00} (within 0.15 s), sliding {lSlide:0} units against {dSlide:0} (within 30%)"));
        float dRise = Med(donorMove.Jumps.Select(x => x.Rise).ToList()), lRise = Med(labMove.Jumps.Select(x => x.Rise).ToList());
        float dAir = Med(donorMove.Jumps.Select(x => x.Air).ToList()), lAir = Med(labMove.Jumps.Select(x => x.Air).ToList());
        Check(donorMove.Jumps.Count >= 5 && MathF.Abs(lRise - dRise) <= .1f * dRise && MathF.Abs(lAir - dAir) <= .1f,
            FormattableString.Invariant($"its jumps rise and land as the donor's: {lRise:0.0} units against {dRise:0.0} (within 10%), {lAir:0.00} s in the air against {dAir:0.00} (within 0.1 s; {donorMove.Jumps.Count} and {labMove.Jumps.Count} jumps)"));
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
