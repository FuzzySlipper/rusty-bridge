// dm-combat: score the Shadow combat model (DarkMessiah.Combat) against recorded fights.
//
//   dm-combat score IN.rbrec [IN.rbrec ...] [--vpks DIR] [--brief]
//       per-decision agreement and every mismatch with its reason, blow by blow
//       (--brief: the first eight of each kind of mismatch)
//
// The model's numbers are the game's own, from the .qct files in its VPKs (--vpks, default
// local/donor/vpks, then the Steam install). Recordings and VPKs are derived from the game and
// stay in local/.
using DarkMessiah.Combat;

string? vpks = null;
bool brief = false;
var recordings = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--vpks") vpks = args[++i];
    else if (args[i] == "--brief") brief = true;
    else recordings.Add(args[i]);
}
if (args.Length < 2 || args[0] != "score" || recordings.Count == 0)
{
    Console.Error.WriteLine("usage: dm-combat score IN.rbrec [IN.rbrec ...] [--vpks DIR] [--brief]");
    return 2;
}
vpks ??= new[]
{
    Path.Combine(FindRepository(), "local", "donor", "vpks"),
    @"C:\Program Files (x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player\vpks",
}.FirstOrDefault(Directory.Exists);
if (vpks is null) { Console.Error.WriteLine("dm-combat: no vpks folder found; pass --vpks (docs/import.md says how to copy it)"); return 2; }
var model = new CombatModel(CombatData.FromVpks(vpks));
int unexplained = 0;
foreach (var path in recordings)
{
    using var stream = File.OpenRead(path);
    var report = Scorer.Score(Blows.Read(Path.GetFileName(path), stream), model);
    Console.Write(report.Format(brief));
    unexplained += report.Mismatches.Count(m => m.Reason.StartsWith("unexplained", StringComparison.Ordinal));
}
Console.WriteLine(unexplained == 0 ? "every mismatch has a known reason" : $"{unexplained} unexplained mismatches");
return unexplained == 0 ? 0 : 1;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
