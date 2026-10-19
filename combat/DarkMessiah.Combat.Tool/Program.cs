// dm-combat: score the Shadow combat model (DarkMessiah.Combat) against recorded fights.
//
//   dm-combat score IN.rbrec [IN.rbrec ...] [--vpks DIR] [--import DIR] [--no-contacts] [--brief]
//       per-decision agreement and every mismatch with its reason, blow by blow
//       (--brief: the first eight of each kind of mismatch)
//
// The model's numbers are the game's own, from the .qct files in its VPKs (--vpks, default
// local/donor/vpks, then the Steam install). The contact decision sweeps each blow's trace through
// hit boxes posed by the mirror, headless in the Engine's test host, on dm-import's level (--import,
// default local/import); without an import of the recording's map it is left out. Recordings, VPKs
// and imports are derived from the game and stay in local/.
using DarkMessiah.Combat;
using DarkMessiah.Mirror.Shadow;
using Rusty.Engine.Testing;

string? vpks = null, import = null;
bool brief = false, contacts = true;
var recordings = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--vpks") vpks = args[++i];
    else if (args[i] == "--import") import = args[++i];
    else if (args[i] == "--no-contacts") contacts = false;
    else if (args[i] == "--brief") brief = true;
    else recordings.Add(args[i]);
}
if (args.Length < 2 || args[0] != "score" || recordings.Count == 0)
{
    Console.Error.WriteLine("usage: dm-combat score IN.rbrec [IN.rbrec ...] [--vpks DIR] [--import DIR] [--no-contacts] [--brief]");
    return 2;
}
vpks ??= new[]
{
    Path.Combine(FindRepository(), "local", "donor", "vpks"),
    @"C:\Program Files (x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player\vpks",
}.FirstOrDefault(Directory.Exists);
if (vpks is null) { Console.Error.WriteLine("dm-combat: no vpks folder found; pass --vpks (docs/import.md says how to copy it)"); return 2; }
import ??= Path.Combine(FindRepository(), "local", "import");
var model = new CombatModel(CombatData.FromVpks(vpks));
int unexplained = 0;
foreach (var path in recordings)
{
    FightCases fight;
    using (var stream = File.OpenRead(path)) fight = Blows.Read(Path.GetFileName(path), stream);
    var swept = contacts ? Sweep(path) : null;
    var report = Scorer.Score(fight, model, swept);
    Console.Write(report.Format(brief));
    unexplained += report.Mismatches.Count(m => m.Reason.StartsWith("unexplained", StringComparison.Ordinal));
}
Console.WriteLine(unexplained == 0 ? "every mismatch has a known reason" : $"{unexplained} unexplained mismatches");
return unexplained == 0 ? 0 : 1;

// The recording's blows swept on the imported level of its map, or null (with a note) when there is none.
IReadOnlyList<ContactCase>? Sweep(string recording)
{
    string map = RustyBridge.Wire.RecordingReader.Frames(File.OpenRead(recording)).First().Map;
    string descriptor = Path.Combine(import, $"{map}.assets.json");
    if (!File.Exists(descriptor))
    {
        Console.WriteLine($"{Path.GetFileName(recording)}: no contacts, no import of {map} in {import} (dm-import level; docs/import.md)");
        return null;
    }
    var content = Directory.GetFiles(import, "*", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith(".rbrec", StringComparison.Ordinal))
        .ToDictionary(f => Path.GetRelativePath(import, f).Replace('\\', '/'), f => (ReadOnlyMemory<byte>)File.ReadAllBytes(f));
    using var host = EngineTestHost.Create(new EngineTestHostOptions { Content = content });
    using var stream = File.OpenRead(recording);
    var swept = host.Call(engine => SwingContacts.Recompute(engine, Path.GetFileName(descriptor), stream));
    return swept.Select(c => new ContactCase(c.Tick, c.Attacker, c.Victim, c.Attack, Part(c.Donor)!, Part(c.Before), Part(c.After),
        c.DonorAlong, c.Miss, c.Stale, c.Length, c.Traced, c.Reach, c.VictimSequence)).ToList();
}

static ContactPart? Part(SwingTouch? t) => t is null ? null : new(t.Body ? Struck.Body : Classify(t.Class), t.Group, t.Hitbox);

static Struck Classify(string cls) =>
    cls.Contains("shield", StringComparison.Ordinal) ? Struck.OffHand
    : cls.StartsWith("weapon_", StringComparison.Ordinal) ? Struck.Weapon
    : Struck.Other;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
