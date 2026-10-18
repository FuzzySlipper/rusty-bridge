// rbwire: command-line tools for the tap's Wire stream and its recordings.
//
//   rbwire tail   [--wire PATH] [--seconds N]   follow the stream, report once a second
//   rbwire dump   [--wire PATH]                 print the latest snapshot in full
//   rbwire record OUT.rbrec [--wire PATH] [--seconds N]
//                                               save every frame until N seconds or Ctrl+C
//   rbwire info   IN.rbrec                      summarise a recording
//   rbwire sequences MODEL.mdl [--game DIR]     list a model's sequences in m_nSequence order
//   rbwire parrybot [--seconds N] [--agent URL] play defense: block when a nearby enemy starts
//                                               an attack, swing at it otherwise (needs the
//                                               den-win11 playtest agent's desktop lease)
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
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--wire": wirePath = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i]); break;
        case "--game": gameDir = args[++i]; break;
        case "--agent": agentUrl = args[++i]; break;
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
    default:
        Console.Error.WriteLine("usage: rbwire tail|dump|record OUT.rbrec|info IN.rbrec|sequences MODEL.mdl [--wire PATH] [--game DIR] [--seconds N]");
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

int Dump()
{
    using var reader = OpenLive();
    var snap = new WireSnapshot();
    while (!reader.TryReadLatest(snap)) Thread.Sleep(1);
    Console.WriteLine($"frame {snap.Frame} tick {snap.Tick} curtime {snap.Curtime:F3} entities {snap.Count}");
    foreach (ref readonly var e in snap.Span)
        Console.WriteLine($"  #{e.Index,4} {reader.ClassName(snap, e.ClassId),-32} " +
            $"({e.OriginX:F1} {e.OriginY:F1} {e.OriginZ:F1}) ({e.Pitch:F1} {e.Yaw:F1} {e.Roll:F1}) {reader.ModelPath(snap, e.ModelIndex)}" +
            (e.Flags.HasFlag(WireEntityFlags.HasHealth) && e.MaxHealth > 0 ? $" hp {e.Health:F1}/{e.MaxHealth:F1} life {e.LifeState}" : "") +
            (e.Flags.HasFlag(WireEntityFlags.HasAnimation) && e.ModelIndex > 0 ? $" seq {e.Sequence} cycle {e.Cycle:F3} rate {e.PlaybackRate:F2}" : ""));
    return 0;
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
    long read = 0, lost = 0, retries = 0, hits = 0, dropped = 0, defended = 0;
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
                foreach (var d in snap.DefenseSpan) if (d.Result is not (DefenseResult.Hit or DefenseResult.None)) defended++;
                dropped += snap.DamageDropped;
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
                    player = $" player ({e.OriginX:F1} {e.OriginY:F1} {e.OriginZ:F1}) yaw {e.Yaw:F1} hp {e.Health:F0}/{e.MaxHealth:F0}";
            string saved = recorder is null ? "" : $" saved {recorder.Bytes / 1024} KiB";
            Console.WriteLine($"{t,5:F1}s frame {snap.Frame} tick {snap.Tick} ({(lastTick - firstTick) / t:F1} ticks/s) " +
                $"entities {snap.Count} read {read} lost {lost} retries {retries} damage {hits}{(dropped > 0 ? $" ({dropped} dropped)" : "")} defended {defended}{saved}{player}");
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
    long damageRecords = 0, damageDropped = 0;
    var defenseResults = new Dictionary<DefenseResult, int>();
    var pairs = new Dictionary<string, (int hits, double applied)>();
    foreach (var f in RecordingReader.Frames(stream))
    {
        damageDropped += f.DamageDropped;
        foreach (var d in f.Defense) defenseResults[d.Result] = defenseResults.GetValueOrDefault(d.Result) + 1;
        foreach (var d in f.Damage)
        {
            damageRecords++;
            string Name(ushort i) => i != WireLayout.NoEntity && f.Entities.TryGetValue(i, out var e) ? f.Classes.GetValueOrDefault(e.ClassId, "?") : i == WireLayout.NoEntity ? "-" : $"#{i}";
            string key = $"{Name(d.Attacker)} -> {Name(d.Victim)}";
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
    Console.WriteLine($"damage: {damageRecords} records, {damageDropped} dropped; by attacker -> victim (hits, health removed):");
    foreach (var (key, p) in pairs.OrderByDescending(kv => kv.Value.applied).Take(15))
        Console.WriteLine($"  {key,-52} {p.hits,5} {p.applied,8:F1}");
    Console.WriteLine("defense checks: " + (defenseResults.Count == 0 ? "none" : string.Join(", ", defenseResults.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))));
    Console.WriteLine("most travelled:");
    foreach (var ((index, _), t) in travelled.OrderByDescending(kv => kv.Value.distance).Take(15))
        Console.WriteLine($"  #{index,4} {t.cls,-28} {t.distance,9:F1} units  {t.model}");
    return 0;
}
