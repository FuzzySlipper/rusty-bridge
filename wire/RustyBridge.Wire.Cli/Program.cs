// rbwire: command-line tools for the tap's Wire stream and its recordings.
//
//   rbwire tail   [--wire PATH] [--seconds N]   follow the stream, report once a second
//   rbwire dump   [--wire PATH]                 print the latest snapshot in full
//   rbwire record OUT.rbrec [--wire PATH] [--seconds N]
//                                               save every frame until N seconds or Ctrl+C
//   rbwire info   IN.rbrec                      summarise a recording
//   rbwire sequences MODEL.mdl [--game DIR]     list a model's sequences in m_nSequence order
//   rbwire parrybot [--seconds N] [--agent URL] play defense: block when a nearby enemy starts
//                                               an attack, turn to, approach and swing at it
//                                               otherwise (needs rbbot.cfg and the
//                                               den-win11 playtest agent's desktop lease)
//   rbwire hold                                 hold the live donor after its next tick
//   rbwire step [N]                             release N ticks (default 1), then hold again
//   rbwire run                                  let the donor run
//   rbwire exec COMMAND...                      run a console command in the donor (staging a
//                                               hidden donor, which takes no keyboard input)
//   rbwire scenario NAME [--out OUT.rbrec] [--reuse] [--hand] [--replace]
//   rbwire close-game                         closes the game rbwire launched, and no other
//                                               launch into a save, stage a mechanic and record it,
//                                               with scripted input through the desktop lease
//                                               (scenarios/NAME.json; docs/recording.md, "Scenarios")
//
// record and sequences read model files from the game's VPKs; --game defaults
// to the game folder the Wire file sits in (<game>\mm\addons\rustybridge.wire).
//
// tail and record read every frame in order, so their counts show whether a
// reader keeps up with the donor.
using System.Diagnostics;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

string command = args.Length > 0 ? args[0] : "tail";
string wirePath = WireReader.DefaultPath;
double seconds = double.PositiveInfinity;
string? gameDir = null;
string agentUrl = "http://192.168.1.12:48300";  // the agent listens on the LAN address only
string? outPath = null;
bool reuse = false, hand = false, replace = false;
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--wire": wirePath = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
        case "--game": gameDir = args[++i]; break;
        case "--agent": agentUrl = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--reuse": reuse = true; break;
        case "--hand": hand = true; break;
        case "--replace": replace = true; break;
        default: positional.Add(args[i]); break;
    }
}

switch (command)
{
    case "tail": return Follow(null);
    case "dump": return Dump();
    case "record" when positional.Count == 1: return Follow(positional[0]);
    case "info" when positional.Count == 1: return Info(positional[0]);
    case "sequences" when positional.Count == 1: return Sequences(positional[0]);
    case "parrybot": return ParryBot.Run(OpenLive(), SequenceResolver(), agentUrl, seconds);
    case "hold": return Control(c => c.Hold());
    case "step": return Control(c => c.Step(positional.Count > 0 ? int.Parse(positional[0]) : 1));
    case "run": return Control(c => { c.Run(); return -1; });
    case "scenario" when positional.Count == 1: return RunScenario(positional[0]);
    case "close-game": return Scenario.CloseOwned();
    case "exec" when positional.Count > 0: return Control(c => { c.Command(string.Join(' ', positional)); Console.WriteLine($"sent: {string.Join(' ', positional)}"); return -2; });
    default:
        Console.Error.WriteLine("usage: rbwire tail|dump|record OUT.rbrec|info IN.rbrec|sequences MODEL.mdl|hold|step [N]|run|exec COMMAND|scenario NAME [--wire PATH] [--game DIR] [--seconds N] [--out OUT.rbrec] [--reuse] [--hand] [--replace]|close-game");
        return 2;
}

VpkIndex? OpenVpks()
{
    string game = gameDir ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(wirePath)!, "..", ".."));
    string vpks = Path.Combine(game, "vpks");
    if (!Directory.Exists(vpks)) { Console.Error.WriteLine($"no {vpks}; sequence names will be missing (pass --game)"); return null; }
    var index = new VpkIndex(vpks);
    Console.WriteLine($"{index.Count} files in {vpks}");
    return index;
}

Func<string, IReadOnlyList<string>>? SequenceResolver()
{
    var vpk = OpenVpks();
    if (vpk is null) return null;
    var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    return path => cache.TryGetValue(path, out var names) ? names : cache[path] = StudioModel.SequenceNames(path, vpk.Read);
}

int Sequences(string model)
{
    var resolve = SequenceResolver();
    if (resolve is null) return 1;
    var names = resolve(model);
    for (int i = 0; i < names.Count; i++) Console.WriteLine($"{i,4} {names[i]}");
    return names.Count > 0 ? 0 : 1;
}

WireReader OpenLive()
{
    var reader = new WireReader(wirePath);
    var clock = Stopwatch.StartNew();
    while (!reader.IsLive || reader.FramesWritten == 0)
    {
        if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("no frames on the Wire after 30 s");
        Thread.Sleep(50);
    }
    var h = reader.Header;
    Console.WriteLine($"wire v{h.Version} pid {h.WriterPid} map {reader.Map} level {h.LevelSerial} tick {h.TickInterval * 1000:F2} ms");
    return reader;
}

// hold, step and run: the live donor through the control file beside the Wire.
int Control(Func<TapControl, int> act)
{
    using var reader = OpenLive();
    using var control = new TapControl(TapControl.PathBeside(wirePath));
    if (!control.IsLive) { Console.Error.WriteLine("no control file from the tap; is this tap older than the hold?"); return 1; }
    int tick = act(control);
    if (tick == -2) return 0;
    if (tick < 0) { Console.WriteLine($"running (holds lapsed so far: {control.HoldsLapsed})"); return 0; }
    var snap = new WireSnapshot();
    if (!reader.TryReadLatest(snap)) { Console.WriteLine($"held at tick {tick}"); return 0; }
    string player = "";
    foreach (ref readonly var e in snap.Span)
        if (reader.ClassName(snap, e.ClassId) == "player") player = $"; player ({e.OriginX:F1} {e.OriginY:F1} {e.OriginZ:F1}) yaw {e.Yaw:F1}";
    Console.WriteLine($"held at tick {tick}; Wire frame {snap.Frame} tick {snap.Tick} curtime {snap.Curtime:F3}, {snap.Count} entities{player}");
    return 0;
}

int Dump()
{
    using var reader = OpenLive();
    var resolve = SequenceResolver();
    var snap = new WireSnapshot();
    while (!reader.TryReadLatest(snap)) Thread.Sleep(1);
    Console.WriteLine($"frame {snap.Frame} tick {snap.Tick} curtime {snap.Curtime:F3} wall {snap.WallSeconds:F3} entities {snap.Count} layers {snap.LayerCount}");
    if (snap.Player.Index != WireLayout.NoEntity)
        Console.WriteLine($"  player #{snap.Player.Index}: {PlayerState(snap.Player)}");
    if (snap.Input.Valid != 0)
        Console.WriteLine($"  input: held {snap.Input.Buttons}, pressed {snap.Input.Pressed}, released {snap.Input.Released} (0x{(uint)snap.Input.Buttons:X}); " +
            FormattableString.Invariant($"{snap.Input.Commands} commands, move {snap.Input.ForwardMove:0}/{snap.Input.SideMove:0}, buttons {snap.Input.CommandButtons}, actions {snap.Input.Actions}"));
    if (snap.Stats.Valid != 0)
    {
        var st = snap.Stats;
        var entities = snap.Span.ToArray();
        string Item(ushort index) => index == WireLayout.NoEntity ? "none"
            : $"#{index} {(entities.FirstOrDefault(e => e.Index == index) is { Index: var i } e && i == index ? reader.ClassName(snap, e.ClassId) : "?")}";
        Console.WriteLine(FormattableString.Invariant($"  stats: stamina {st.Stamina:0.00}/{st.MaxStamina:0.##}, mana {st.Mana:0.0}/{st.MaxMana:0.#}, skill points {st.SkillPoints}; ") +
            $"weapon {Item(st.Weapon)}, shield {Item(st.Shield)}, helmet {Item(st.Helmet)}, ring {Item(st.Ring)}, armor {Item(st.Armor)}");
        Console.WriteLine($"  modules: {string.Join(", ", SkillModuleNames.Of(st.Modules).DefaultIfEmpty("none"))}");
        Console.WriteLine($"  skills: {string.Join(", ", SkillNames.ByIndex.Select(n => (n, l: st.SkillLevel(n))).Where(x => x.l > 0).Select(x => $"{x.n} {x.l}").DefaultIfEmpty("all 0"))}");
    }
    if (snap.ClientView.Flags != ClientViewFlags.None)
    {
        Console.WriteLine($"  client: {ClientView(snap.ClientView)}");
        // The client viewmodel's layers, named from the server viewmodel's model.
        var vmEntity = snap.Span.ToArray().FirstOrDefault(e => e.Index == snap.ClientView.Viewmodel);
        var vmNames = snap.ClientView.Viewmodel != WireLayout.NoEntity ? resolve?.Invoke(reader.ModelPath(snap, vmEntity.ModelIndex)) : null;
        foreach (var l in snap.ClientView.ViewmodelLayers)
            Console.WriteLine($"  client layer {(vmNames is not null && l.Sequence >= 0 && l.Sequence < vmNames.Count ? vmNames[l.Sequence] : $"#{l.Sequence}"),-36} cycle {l.Cycle:F3} weight {l.Weight:F2} order {l.Order}");
    }
    var layersOf = snap.LayerSpan.ToArray().ToLookup(l => l.Index);
    foreach (ref readonly var e in snap.Span)
        Console.WriteLine($"  #{e.Index,4} {reader.ClassName(snap, e.ClassId),-32} " +
            $"({e.OriginX:F1} {e.OriginY:F1} {e.OriginZ:F1}) ({e.Pitch:F1} {e.Yaw:F1} {e.Roll:F1}) {reader.ModelPath(snap, e.ModelIndex)}" +
            (e.Flags.HasFlag(WireEntityFlags.HasHealth) && e.MaxHealth > 0 ? $" hp {e.Health:F1}/{e.MaxHealth:F1} life {e.LifeState}" : "") +
            (e.Flags.HasFlag(WireEntityFlags.HasAnimation) && e.ModelIndex > 0 ? $" seq {e.Sequence} cycle {e.Cycle:F3} rate {e.PlaybackRate:F2}" : "") +
            (e.Link != WireLayout.NoEntity ? $" link #{e.Link}" : "") +
            (e.Parent != WireLayout.NoEntity ? $" parent #{e.Parent} att {e.ParentAttachment}" : "") +
            (e.Owner != WireLayout.NoEntity ? $" owner #{e.Owner}" : ""));
    foreach (var l in layersOf.SelectMany(g => g))
    {
        var owner = snap.Span.ToArray().FirstOrDefault(e => e.Index == l.Index);
        var names = resolve?.Invoke(reader.ModelPath(snap, owner.ModelIndex));
        string name = names is not null && l.Sequence >= 0 && l.Sequence < names.Count ? names[l.Sequence] : $"#{l.Sequence}";
        Console.WriteLine($"  layer #{l.Index,4}[{l.Slot}] {name,-28} cycle {l.Cycle:F3} weight {l.Weight:F2} order {l.Order} rate {l.PlaybackRate:F2} flags {l.Flags}");
    }
    return 0;
}

// scenario: recordings go to recordings\<map>-v<Wire version>-<scenario>.rbrec beside rbwire's folder unless --out names one.
int RunScenario(string name)
{
    var plan = Scenario.Load(name);
    string game = gameDir ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(wirePath)!, "..", ".."));
    string output = outPath ?? Path.Combine(AppContext.BaseDirectory, "..", "recordings", $"l01_a-v{WireLayout.Version}-{plan.Name}.rbrec");
    int result = Scenario.Run(plan, game, wirePath, Path.GetFullPath(output), agentUrl, reuse, hand, replace, (path, duration) => { seconds = duration; return Follow(path); });
    if (result == 0) Info(Path.GetFullPath(output));
    return result;
}

int Follow(string? recordPath)
{
    using var reader = OpenLive();
    using var recorder = recordPath is null ? null : new Recorder(File.Create(recordPath), SequenceResolver());
    bool stop = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

    var snap = new WireSnapshot();
    var clock = Stopwatch.StartNew();
    ulong next = reader.FramesWritten - 1;
    long read = 0, lost = 0, retries = 0, hits = 0, dropped = 0, defended = 0, layerPeak = 0;
    int firstTick = 0, lastTick = 0;
    double reportAt = 1;
    while (!stop && clock.Elapsed.TotalSeconds < seconds)
    {
        ulong written = reader.FramesWritten;
        if (written > next + WireLayout.SlotCount)
        {
            lost += (long)(written - WireLayout.SlotCount - next);
            next = written - WireLayout.SlotCount;
        }
        while (next < written)
        {
            if (reader.TryReadFrame(next, snap))
            {
                if (read++ == 0) firstTick = snap.Tick;
                lastTick = snap.Tick;
                hits += snap.DamageCount;
                layerPeak = Math.Max(layerPeak, snap.LayerCount);
                foreach (var d in snap.DefenseSpan) if (d.Result is not (DefenseResult.Hit or DefenseResult.None)) defended++;
                dropped += snap.DamageDropped;
                foreach (var b in snap.SlowTimeSpan)
                    Console.WriteLine($"  tick {b.Tick}: slow motion {b.Source} x{b.Factor:F2} for {b.Duration:F2} s {(b.Source == SlowTimeSource.Request ? "after" : "from")} {b.Start:F2} (caller {WireSlowTime.CallerName(b.Caller)})");
                recorder?.Write(snap, reader.Map, reader.Header.TickInterval, reader);
                next++;
            }
            else if (reader.FramesWritten - next > WireLayout.SlotCount - 1) { lost++; next++; }
            else { retries++; break; }
        }
        double t = clock.Elapsed.TotalSeconds;
        if (t >= reportAt && read > 0)
        {
            reportAt = Math.Floor(t) + 1;
            string player = "";
            foreach (ref readonly var e in snap.Span)
                if (reader.ClassName(snap, e.ClassId) == "player")
                    player = $" player ({e.OriginX:F1} {e.OriginY:F1} {e.OriginZ:F1}) yaw {e.Yaw:F1} hp {e.Health:F0}/{e.MaxHealth:F0}" +
                        (snap.Player.Index != WireLayout.NoEntity ? $" {PlayerState(snap.Player)}" : "");
            string saved = recorder is null ? "" : $" saved {recorder.Bytes / 1024} KiB";
            Console.WriteLine($"{t,5:F1}s frame {snap.Frame} tick {snap.Tick} ({(lastTick - firstTick) / t:F1} ticks/s) " +
                $"entities {snap.Count} read {read} lost {lost} retries {retries} damage {hits}{(dropped > 0 ? $" ({dropped} dropped)" : "")} defended {defended} layers {snap.LayerCount} (peak {layerPeak}){saved}{player}");
        }
        Thread.Sleep(1);
    }
    if (recorder is not null)
        Console.WriteLine($"recorded {recorder.FramesWritten} frames, {recorder.Bytes / 1024} KiB to {recordPath}");
    return 0;
}

int Info(string path)
{
    using var stream = File.OpenRead(path);
    long frames = 0, gaps = 0;
    int firstTick = 0, lastTick = 0;
    string map = "";
    float interval = 0;
    var travelled = new Dictionary<(ushort index, uint serial), (double distance, WireEntity last, string cls, string model)>();
    RecordedFrame? last = null;
    long damageRecords = 0, damageDropped = 0, posedVictims = 0;
    var defenseResults = new Dictionary<DefenseResult, int>();
    var pairs = new Dictionary<string, (int hits, double applied)>();
    var names = new EventNames();
    var slowCallers = new Dictionary<(SlowTimeSource source, uint caller), (int calls, float factor, float duration)>();
    long slowDropped = 0, slowTicks = 0;
    var sweepCallers = new Dictionary<(uint caller, byte type, string attacker), (int sweeps, float range)>();
    long sweepDropped = 0;
    float adrenalineMax = float.NaN;
    var links = new HashSet<(string, string)>();
    double? firstWall = null, lastWall = null;
    float firstCurtime = 0, lastCurtime = 0;
    long inputFrames = 0;
    var presses = new Dictionary<InputButtons, int>();
    // v14: each action bit's presses (going up in the commands) and ticks held. v15: the stats' ranges, modules and items.
    var actionPresses = new int[32];
    var actionTicks = new int[32];
    PlayerActions lastActions = 0;
    float staminaLow = float.NaN, staminaHigh = float.NaN, manaLow = float.NaN, manaHigh = float.NaN;
    SkillModules modulesSeen = 0;
    var skillsSeen = new int[SkillNames.ByIndex.Length];
    var itemsSeen = new SortedSet<string>(StringComparer.Ordinal);
    long statsFrames = 0;
    foreach (var f in RecordingReader.Frames(stream))
    {
        if (f.Input.Valid != 0)
        {
            inputFrames++;
            foreach (var bit in Enum.GetValues<InputButtons>())
                if (bit != InputButtons.None && f.Input.Pressed.HasFlag(bit)) presses[bit] = presses.GetValueOrDefault(bit) + 1;
            if (f.Input.Commands > 0)
            {
                for (int b = 0; b < 32; b++)
                {
                    bool on = ((uint)f.Input.Actions >> b & 1) != 0;
                    if (on) actionTicks[b]++;
                    if (on && ((uint)lastActions >> b & 1) == 0) actionPresses[b]++;
                }
                lastActions = f.Input.Actions;
            }
        }
        if (f.Stats.Valid != 0)
        {
            statsFrames++;
            var st = f.Stats;
            staminaLow = float.IsNaN(staminaLow) ? st.Stamina : MathF.Min(staminaLow, st.Stamina);
            staminaHigh = float.IsNaN(staminaHigh) ? st.Stamina : MathF.Max(staminaHigh, st.Stamina);
            manaLow = float.IsNaN(manaLow) ? st.Mana : MathF.Min(manaLow, st.Mana);
            manaHigh = float.IsNaN(manaHigh) ? st.Mana : MathF.Max(manaHigh, st.Mana);
            modulesSeen |= st.Modules;
            for (int i = 0; i < skillsSeen.Length; i++) skillsSeen[i] = Math.Max(skillsSeen[i], st.SkillLevel(SkillNames.ByIndex[i]));
            foreach (var (what, index) in new[] { ("weapon", st.Weapon), ("shield", st.Shield), ("helmet", st.Helmet), ("ring", st.Ring), ("armor", st.Armor) })
                if (index != WireLayout.NoEntity && f.Entities.TryGetValue(index, out var item))
                    itemsSeen.Add($"{what} {f.Classes.GetValueOrDefault(item.ClassId, "?")}");
        }
        sweepDropped += f.SweepDropped;
        foreach (var s in f.Sweeps)
        {
            string who = f.Entities.TryGetValue(s.Attacker, out var a) ? f.Classes.GetValueOrDefault(a.ClassId, "?") : "?";
            var c = sweepCallers.GetValueOrDefault((s.Caller, s.Type, who));
            sweepCallers[(s.Caller, s.Type, who)] = (c.sweeps + 1, s.Range);
        }
        slowDropped += f.SlowTimeDropped;
        foreach (var b in f.SlowTime)
        {
            var c = slowCallers.GetValueOrDefault((b.Source, b.Caller));
            slowCallers[(b.Source, b.Caller)] = (c.calls + 1, b.Factor, b.Duration);
        }
        if (f.Player.Index != WireLayout.NoEntity)
        {
            if (f.Player.InBulletTime != 0) slowTicks++;
            adrenalineMax = float.IsNaN(adrenalineMax) ? f.Player.Adrenaline : MathF.Max(adrenalineMax, f.Player.Adrenaline);
        }
        if (!double.IsNaN(f.WallSeconds))
        {
            firstWall ??= f.WallSeconds;
            if (firstWall == f.WallSeconds) firstCurtime = f.Curtime;
            lastWall = f.WallSeconds;
            lastCurtime = f.Curtime;
        }
        foreach (var (index, e) in f.Entities)
            if (e.Link != WireLayout.NoEntity && f.Entities.TryGetValue(e.Link, out var linked))
                links.Add(($"{f.Classes.GetValueOrDefault(e.ClassId, "?")} #{index}", $"{f.Classes.GetValueOrDefault(linked.ClassId, "?")} #{e.Link}"));
        damageDropped += f.DamageDropped;
        names.Observe(f);
        foreach (var d in f.Defense) defenseResults[d.Result] = defenseResults.GetValueOrDefault(d.Result) + 1;
        foreach (var d in f.Damage)
        {
            damageRecords++;
            if (d.VictimAnimValid != 0) posedVictims++;
            string key = $"{names.Name(f, d.Attacker, d.AttackerSerial)} -> {names.Name(f, d.Victim, d.VictimSerial)}";
            var p = pairs.GetValueOrDefault(key);
            pairs[key] = (p.hits + 1, p.applied + Math.Max(0, d.HealthBefore - d.HealthAfter));
        }
        if (frames++ == 0) firstTick = f.Tick;
        lastTick = f.Tick;
        if (f.Gap) gaps++;
        map = f.Map;
        interval = f.TickInterval;
        foreach (var (index, e) in f.Entities)
        {
            var key = (index, e.Serial);
            if (travelled.TryGetValue(key, out var t))
            {
                double dx = e.OriginX - t.last.OriginX, dy = e.OriginY - t.last.OriginY, dz = e.OriginZ - t.last.OriginZ;
                travelled[key] = (t.distance + Math.Sqrt(dx * dx + dy * dy + dz * dz), e, t.cls, t.model);
            }
            else
            {
                travelled[key] = (0, e, f.Classes.GetValueOrDefault(e.ClassId, "?"), f.Models.GetValueOrDefault(e.ModelIndex, ""));
            }
        }
        last = f;
    }
    Console.WriteLine($"{path}: {new FileInfo(path).Length / 1024} KiB, map {map}, {frames} frames, {last?.Sequences.Count ?? 0} models with sequence names, " +
        $"ticks {firstTick}..{lastTick} ({(lastTick - firstTick) * interval:F2} s), {gaps} gaps, " +
        $"{last?.Entities.Count ?? 0} entities at the end");
    Console.WriteLine($"damage: {damageRecords} records, {damageDropped} dropped, {posedVictims} with the victim's animation (v13); by attacker -> victim (hits, health removed):");
    foreach (var (key, p) in pairs.OrderByDescending(kv => kv.Value.applied).Take(15))
        Console.WriteLine($"  {key,-52} {p.hits,5} {p.applied,8:F1}");
    Console.WriteLine("defense checks: " + (defenseResults.Count == 0 ? "none" : string.Join(", ", defenseResults.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))));
    if (lastWall is double wallEnd && firstWall is double wallStart && wallEnd > wallStart)
        Console.WriteLine($"time: {lastCurtime - firstCurtime:F2} s of game time in {wallEnd - wallStart:F2} s of wall time ({(lastCurtime - firstCurtime) / (wallEnd - wallStart):F2}x); " +
            $"player in slow motion {slowTicks * interval:F2} s of game time; adrenaline peak {adrenalineMax:F1}");
    if (slowCallers.Count > 0 || slowDropped > 0)
    {
        Console.WriteLine($"slow motion calls: {slowCallers.Values.Sum(c => c.calls)}, {slowDropped} dropped; by caller:");
        foreach (var ((source, caller), c) in slowCallers.OrderByDescending(kv => kv.Value.calls))
            Console.WriteLine($"  {source,-7} {WireSlowTime.CallerName(caller)}  {c.calls,4} calls  last x{c.factor:F2} for {c.duration:F2} s");
    }
    if (sweepCallers.Count > 0 || sweepDropped > 0)
    {
        Console.WriteLine($"melee sweeps: {sweepCallers.Values.Sum(c => c.sweeps)}, {sweepDropped} dropped; by caller, type and attacker:");
        foreach (var ((caller, type, who), c) in sweepCallers.OrderByDescending(kv => kv.Value.sweeps))
            Console.WriteLine($"  {WireSlowTime.CallerName(caller)}  type {type}  {who,-16} {c.sweeps,5} sweeps  range {c.range:F0}");
    }
    if (inputFrames > 0)
        Console.WriteLine($"input: {inputFrames} frames; presses " +
            (presses.Count == 0 ? "none" : string.Join(", ", presses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))));
    if (actionTicks.Any(t => t > 0))
        Console.WriteLine("actions (v14; bit: presses/ticks held): " + string.Join(", ", Enumerable.Range(0, 32).Where(b => actionTicks[b] > 0)
            .Select(b => $"{((PlayerActions)(1u << b) is var a && Enum.IsDefined(a) ? a.ToString() : $"+0x{0x95 + b:X2}")} {actionPresses[b]}/{actionTicks[b]}")));
    if (statsFrames > 0)
    {
        Console.WriteLine(FormattableString.Invariant($"stats (v15): {statsFrames} frames; stamina {staminaLow:0.00}–{staminaHigh:0.00}, mana {manaLow:0.0}–{manaHigh:0.0}"));
        Console.WriteLine($"  modules: {string.Join(", ", SkillModuleNames.Of(modulesSeen).DefaultIfEmpty("none"))}");
        Console.WriteLine($"  skills (v16, highest): {string.Join(", ", SkillNames.ByIndex.Select((n, i) => (n, l: skillsSeen[i])).Where(x => x.l > 0).Select(x => $"{x.n} {x.l}").DefaultIfEmpty("all 0"))}");
        Console.WriteLine($"  items: {string.Join(", ", itemsSeen.DefaultIfEmpty("none"))}");
    }
    if (links.Count > 0)
    {
        Console.WriteLine($"knockdown links: {links.Count}");
        foreach (var (from, to) in links.Take(15)) Console.WriteLine($"  {from} -> {to}");
    }
    Console.WriteLine("most travelled:");
    foreach (var ((index, _), t) in travelled.OrderByDescending(kv => kv.Value.distance).Take(15))
        Console.WriteLine($"  #{index,4} {t.cls,-28} {t.distance,9:F1} units  {t.model}");
    return 0;
}

static string PlayerState(WirePlayerState p) =>
    $"adrenaline {p.Adrenaline:F1}/{p.MaxAdrenaline:F1}" +
    (p.InBulletTime != 0 ? $" slow motion x{p.BulletTimeFactor:F2} from {p.BulletTimeStart:F2} for {p.BulletTimeDuration:F2} s" : "");

static string ClientView(in WireClientView c) =>
    $"flags {c.Flags}; view ({c.ViewPitch:F1} {c.ViewYaw:F1} {c.ViewRoll:F1})" +
    (c.Flags.HasFlag(ClientViewFlags.Player) ? $"; player #{c.Player} ({c.OriginX:F1} {c.OriginY:F1} {c.OriginZ:F1})" : "") +
    (c.Flags.HasFlag(ClientViewFlags.ViewOffset) ? $" eye +({c.ViewOffsetX:F1} {c.ViewOffsetY:F1} {c.ViewOffsetZ:F1})" : "") +
    (c.Flags.HasFlag(ClientViewFlags.Viewmodel) ? $"; viewmodel #{c.Viewmodel} ({c.ViewmodelX:F1} {c.ViewmodelY:F1} {c.ViewmodelZ:F1}) ({c.ViewmodelPitch:F1} {c.ViewmodelYaw:F1})" : "") +
    (c.Flags.HasFlag(ClientViewFlags.ViewmodelAnimation) ? $" seq {c.ViewmodelSequence} cycle {c.ViewmodelCycle:F3}" : "") +
    (c.Flags.HasFlag(ClientViewFlags.Camera) ? $"; camera ({c.CameraX:F1} {c.CameraY:F1} {c.CameraZ:F1}) ({c.CameraPitch:F2} {c.CameraYaw:F2} {c.CameraRoll:F2}) fov {c.Fov:F1} {c.ScreenWidth}x{c.ScreenHeight}" : "");
