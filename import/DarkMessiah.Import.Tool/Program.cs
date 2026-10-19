// dm-import: Dark Messiah donor files to Rusty Engine assets (GLB), keyed by donor path.
//
//   dm-import level IN.rbrec [IN.rbrec ...|DIR]
//                                       the recordings' map, brush models, studio models and
//                                       sprites, plus <map>.assets.json indexing them by the
//                                       model path the Wire reports; several recordings (or every
//                                       .rbrec in a folder) of one map import the union of their
//                                       models, and the first travels with the assets
//   dm-import model MODEL.mdl           one studio model
//   dm-import map maps/NAME.bsp         one map's world and brush models
//   dm-import tables                    the gameplay tables alone (scripts, item .qct, item models;
//                                       `level` copies them too), added to the output's level indexes
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
if (args.Length == 0 || args[0] != "tables" && positional.Count == 0 || positional.Count > 1 && args[0] != "level" || args[0] is not ("level" or "model" or "map" or "tables"))
{
    Console.Error.WriteLine("usage: dm-import level IN.rbrec [IN.rbrec ...|DIR] | model MODEL.mdl | map maps/NAME.bsp | tables [--vpks DIR] [--out DIR]");
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
        var recordings = positional.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.rbrec").Order(StringComparer.Ordinal).ToArray() : new[] { p }).Select(Path.GetFullPath).Distinct().ToList();
        var level = importer.FromRecordings(recordings);
        Console.WriteLine($"{recordings.Count} recordings: {string.Join(", ", recordings.Select(Path.GetFileName))}");
        Console.WriteLine($"{level.IndexPath}: {level.Resolved} of {level.Requested} Wire model paths resolve ({level.Invisible.Count} of them invisible brush models, no GLB)");
        Console.WriteLine($"clip lists checked against the recording's sequence names: {level.SequencesChecked} models, {level.SequencesDiffer} differ");
        foreach (var missing in level.Unresolved) Console.WriteLine($"  unresolved: {missing}");
        if (level.Tables is { } t) Tables(t);
        break;
    case "tables":
        var (refreshed, indexes) = importer.RefreshTables();
        Tables(refreshed);
        foreach (var index in indexes) Console.WriteLine($"  item models indexed in {index}");
        break;
}
foreach (var warning in log.Warnings) Console.WriteLine($"warning: {warning}");
Console.WriteLine($"{clock.Elapsed.TotalSeconds:F1} s, output in {output}");
return 0;

static void Tables(TablesImportResult t)
{
    Console.WriteLine($"gameplay tables: {t.Scripts.Count} scripts, {t.ItemTables.Count} item .qct, {t.Models.Count} item models");
    foreach (var missing in t.Unresolved) Console.WriteLine($"  not copied: {missing}");
}

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "import"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}
