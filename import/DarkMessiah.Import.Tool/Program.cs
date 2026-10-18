// dm-import: Dark Messiah donor files to Rusty Engine assets (GLB), keyed by donor path.
//
//   dm-import level IN.rbrec            the recording's map, brush models, studio models and
//                                       sprites, plus <map>.assets.json indexing them by the
//                                       model path the Wire reports
//   dm-import model MODEL.mdl           one studio model
//   dm-import map maps/NAME.bsp         one map's world and brush models
//
//   --vpks DIR   the game's vpks folder (default: local/donor/vpks, then the Steam install)
//   --out DIR    where assets go (default: local/import)
//
// Output mirrors donor paths, lowercased, with '/' separators: models/npc/orc/npc_orc.glb,
// maps/l01_a/world.glb, maps/l01_a/brush_12.glb (for "*12").
using DarkMessiah.Import;
using RustyBridge.Wire.Donor;

string? vpks = null, output = null;
var positional = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--vpks": vpks = args[++i]; break;
        case "--out": output = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}
if (args.Length == 0 || positional.Count != 1 || args[0] is not ("level" or "model" or "map"))
{
    Console.Error.WriteLine("usage: dm-import level IN.rbrec | model MODEL.mdl | map maps/NAME.bsp [--vpks DIR] [--out DIR]");
    return 2;
}

string repo = FindRepository();
vpks ??= new[]
{
    Path.Combine(repo, "local", "donor", "vpks"),
    @"C:\Program Files (x86)\Steam\steamapps\common\Dark Messiah Might and Magic Single Player\vpks",
}.FirstOrDefault(Directory.Exists) ?? throw new DirectoryNotFoundException("no vpks folder found; pass --vpks");
output ??= Path.Combine(repo, "local", "import");

var log = new ImportLog();
var files = new DonorFiles(new VpkIndex(vpks));
var importer = new LevelImport(files, log, output);
var clock = System.Diagnostics.Stopwatch.StartNew();
switch (args[0])
{
    case "model":
        var m = importer.Model(positional[0]);
        Console.WriteLine($"{m.DonorPath}: {m.Bones} bones, {m.Triangles} triangles, {m.Clips} clips, {m.Bytes / 1024} KiB");
        break;
    case "map":
        var map = importer.Map(positional[0]);
        Console.WriteLine($"{positional[0]}: {map.Models.Count} drawable BSP models, {map.Invisible.Count} invisible, {map.Triangles} triangles, {map.Bytes / 1024} KiB");
        break;
    case "level":
        var level = importer.FromRecording(positional[0]);
        Console.WriteLine($"{level.IndexPath}: {level.Resolved} of {level.Requested} Wire model paths resolve ({level.Invisible.Count} of them invisible brush models, no GLB)");
        Console.WriteLine($"clip lists checked against the recording's sequence names: {level.SequencesChecked} models, {level.SequencesDiffer} differ");
        foreach (var missing in level.Unresolved) Console.WriteLine($"  unresolved: {missing}");
        break;
}
foreach (var warning in log.Warnings) Console.WriteLine($"warning: {warning}");
Console.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s, output in {output}");
return 0;

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "import"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
