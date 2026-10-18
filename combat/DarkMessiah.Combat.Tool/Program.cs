// dm-combat: score the Shadow combat model (DarkMessiah.Combat) against recorded fights.
//
//   dm-combat score IN.rbrec [IN.rbrec ...]   per-decision agreement and every mismatch with its reason
//
// Recordings are derived from the game and stay in local/recordings.
using DarkMessiah.Combat;

if (args.Length < 2 || args[0] != "score")
{
    Console.Error.WriteLine("usage: dm-combat score IN.rbrec [IN.rbrec ...]");
    return 2;
}
int unexplained = 0;
foreach (var path in args.Skip(1))
{
    using var stream = File.OpenRead(path);
    var report = Scorer.Score(Blows.Read(Path.GetFileName(path), stream));
    Console.Write(report.Format());
    unexplained += report.Mismatches.Count(m => m.Reason == "unexplained");
}
Console.WriteLine(unexplained == 0 ? "every mismatch has a known reason" : $"{unexplained} unexplained mismatches");
return unexplained == 0 ? 0 : 1;
