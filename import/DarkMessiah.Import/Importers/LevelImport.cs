using System.Text.Json;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Images;
using DarkMessiah.Import.Source;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>
/// The game's gameplay tables copied into an import (<see cref="LevelImport.Tables"/>): the scripts, every item
/// <c>.qct</c>, and the item models imported as GLBs (each with its <c>.qct</c> beside it), by donor path.
/// </summary>
public sealed record TablesImportResult(IReadOnlyList<string> Scripts, IReadOnlyList<string> ItemTables,
    IReadOnlyList<(string DonorPath, string File)> Models, IReadOnlyList<string> Unresolved);

/// <summary>Resolved counts Wire model paths with an asset or, for brush models with nothing to draw, a deliberate absence (<see cref="Invisible"/>).</summary>
public sealed record LevelImportResult(string IndexPath, int Requested, int Resolved, IReadOnlyList<string> Invisible, IReadOnlyList<string> Unresolved, int SequencesChecked, int SequencesDiffer,
    TablesImportResult? Tables = null);

/// <summary>
/// Imports what a level needs and indexes it by donor path. Output mirrors
/// donor paths, lowercased: models/…/x.glb for x.mdl, maps/NAME/world.glb
/// and maps/NAME/brush_N.glb for a map's BSP models, materials/…/x.png for
/// sprite materials. The index is a portable asset descriptor
/// (schemaVersion 1) whose asset ids are the model paths exactly as the Wire
/// reports them, so a Wire model index joins to its asset by string. The
/// recording is copied to recordings/ beside them.
/// </summary>
public sealed class LevelImport
{
    readonly DonorFiles _files;
    readonly ImportLog _log;
    readonly string _output;
    readonly Materials _materials;

    public LevelImport(DonorFiles files, ImportLog log, string output)
    {
        _files = files;
        _log = log;
        _output = output;
        _materials = new Materials(files, log);
    }

    /// <summary>A model's GLB.</summary>
    public static string ModelFile(string donorPath) => Path.ChangeExtension(VpkIndex.Normalize(donorPath), ".glb");

    public static string MapName(string donorPath) => Path.GetFileNameWithoutExtension(VpkIndex.Normalize(donorPath));

    public static string BrushFile(string map, int model) => model == 0 ? $"maps/{map}/world.glb" : $"maps/{map}/brush_{model}.glb";

    /// <summary>
    /// One studio model as a GLB, and its <c>.qct</c> (Arkane's commented $keyvalues: combat tables,
    /// hitbox modifiers, weapon damage) beside it under the same lowercased name, for products that read
    /// the game's numbers at run time.
    /// </summary>
    public ModelImportResult Model(string donorPath)
    {
        var result = new ModelImport(_files, _materials, _log).Import(donorPath, Path.Combine(_output, ModelFile(donorPath)));
        if (_files.Read(Path.ChangeExtension(donorPath, ".qct")) is byte[] qct)
            File.WriteAllBytes(Path.Combine(_output, Path.ChangeExtension(ModelFile(donorPath), ".qct")), qct);
        return result;
    }

    /// <summary>
    /// Game scripts products read at run time, copied under their own paths: the melee settings, spells, skills,
    /// factions, physics and surface properties, the bindable actions and the animation plugin table.
    /// </summary>
    public static readonly string[] Scripts =
    [
        "scripts/mm_game_settings.txt", "scripts/spells.txt", "scripts/spells_entityspellcaster.txt",
        "scripts/mm_skills_infos.txt", "scripts/mm_relationship.txt", "scripts/propdata.txt",
        "scripts/surfaceproperties.txt", "scripts/surfaceproperties_arkane.txt", "scripts/surfaceproperties_manifest.txt",
        "scripts/kb_act.lst", "scripts/plugin_animations.txt",
    ];

    /// <summary>Folders whose <c>.qct</c>s are item tables: weapons, armour and shields, provisions and potions, magic items.</summary>
    public static readonly string[] ItemFolders = ["models/items/weapons", "models/items/armors", "models/items/provisions", "models/items/magic"];

    /// <summary>
    /// Whether an item model is imported as a GLB: every weapon (swords, daggers, staffs, axes, clubs, bows, arrows,
    /// quivers) and every whole shield. A shield's break chunks (<c>*chunk*</c>) aren't.
    /// </summary>
    public static bool ItemModel(string donorPath) =>
        donorPath.EndsWith(".mdl", StringComparison.Ordinal) && !donorPath.Contains("chunk", StringComparison.Ordinal)
        && (donorPath.StartsWith("models/items/weapons/", StringComparison.Ordinal) || donorPath.StartsWith("models/items/armors/shield", StringComparison.Ordinal));

    /// <summary>
    /// The gameplay tables: <see cref="Scripts"/>, every item <c>.qct</c> under <see cref="ItemFolders"/>, and the
    /// <see cref="ItemModel"/> models as GLBs. What a donor lacks is skipped and reported.
    /// </summary>
    public TablesImportResult Tables()
    {
        var scripts = new List<string>();
        var unresolved = new List<string>();
        foreach (var script in Scripts)
            if (_files.Read(script) is byte[] text) { Write(script, text); scripts.Add(script); }
            else unresolved.Add(script);
        var tables = new List<string>();
        var models = new List<(string, string)>();
        foreach (var folder in ItemFolders)
            foreach (var path in _files.Under(folder))
            {
                if (path.EndsWith(".qct", StringComparison.Ordinal) && _files.Read(path) is byte[] qct) { Write(path, qct); tables.Add(path); }
                if (!ItemModel(path)) continue;
                try
                {
                    Model(path);
                    models.Add((path, ModelFile(path)));
                }
                catch (Exception e) when (e is InvalidDataException or FileNotFoundException or NotSupportedException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                {
                    _log.Warn($"{path}: {e.Message}");
                    unresolved.Add(path);
                }
            }
        return new TablesImportResult(scripts, tables, models, unresolved);
    }

    void Write(string donorPath, byte[] bytes)
    {
        string file = Path.Combine(_output, VpkIndex.Normalize(donorPath));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    public MapImportResult Map(string donorPath)
    {
        string map = MapName(donorPath);
        return new MapImport(_files, _materials, _log).Import(donorPath, m => Path.Combine(_output, BrushFile(map, m)));
    }

    /// <summary>A sprite material's base texture as a PNG beside where its .vmt would be. Null when it has none.</summary>
    public string? Sprite(string donorPath)
    {
        string path = Materials.Normalize(donorPath);
        var material = _materials.Find(path);
        if (material?.BaseTexture is null) { _log.Warn($"{donorPath}: no sprite material"); return null; }
        var texture = _materials.Texture(material.BaseTexture, 1024);
        if (texture is null) return null;
        string file = $"materials/{path}.png";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_output, file))!);
        File.WriteAllBytes(Path.Combine(_output, file), texture.Value.Png);
        return file;
    }

    public LevelImportResult FromRecording(string recordingPath) => FromRecordings([recordingPath]);

    /// <summary>
    /// The level of one or more recordings of the same map: every model path
    /// any of them reports, imported once. The first recording is the one the
    /// assets travel with (the mirror replays it); the provenance lists all.
    /// </summary>
    public LevelImportResult FromRecordings(IReadOnlyList<string> recordingPaths)
    {
        if (recordingPaths.Count == 0) throw new ArgumentException("no recordings", nameof(recordingPaths));
        string recordingPath = recordingPaths[0];
        string map = "";
        // Model paths in first-seen order: model indices differ between recordings, the paths are the keys.
        var models = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var recording in recordingPaths)
        {
            string level = "";
            var indexed = new SortedDictionary<int, string>();
            using (var stream = File.OpenRead(recording))
                foreach (var frame in RecordingReader.Frames(stream))
                {
                    level = frame.Map;
                    foreach (var (index, path) in frame.Models) indexed[index] = path;
                    foreach (var (index, names) in frame.Sequences)
                        if (frame.Models.TryGetValue(index, out var path)) sequences[path] = names;
                }
            if (level.Length == 0) throw new InvalidDataException($"{recording}: no level record");
            if (map.Length > 0 && !level.Equals(map, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{recording} is of {level}, {recordingPath} of {map}: one level import takes recordings of one map");
            map = level;
            foreach (var path in indexed.Values)
                if (known.Add(path)) models.Add(path);
        }

        var assets = new JsonArray();
        var unresolved = new List<string>();
        var invisible = new List<string>();
        var mapResult = Map($"maps/{map}.bsp");
        var tables = Tables();
        string Relative(string file) => Path.GetRelativePath(_output, file).Replace('\\', '/');
        assets.Add(Asset($"maps/{map}.bsp", Relative(mapResult.Models["*0"])));

        int resolved = 0, checkedSequences = 0, differ = 0;
        var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in models)
        {
            string? file = null;
            string kind = "model";
            try
            {
                if (path.StartsWith('*'))
                {
                    if (mapResult.Invisible.Contains(path)) { invisible.Add(path); resolved++; continue; }
                    file = mapResult.Models.TryGetValue(path, out var brush) ? Relative(brush) : null;
                }
                else if (path.Equals($"maps/{map}.bsp", StringComparison.OrdinalIgnoreCase))
                    file = Relative(mapResult.Models["*0"]);
                else if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                {
                    if (!done.TryGetValue(path, out file))
                    {
                        var result = Model(path);
                        file = done[path] = ModelFile(path);
                        if (sequences.TryGetValue(path, out var recorded))
                        {
                            checkedSequences++;
                            if (!recorded.SequenceEqual(result.Sequences)) { differ++; _log.Warn($"{path}: clip order differs from the recording's sequence names"); }
                        }
                        Console.WriteLine($"{path}: {result.Bones} bones, {result.Triangles} triangles, {result.Clips} clips, {result.Bytes / 1024} KiB");
                    }
                }
                else if (path.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
                {
                    kind = "texture";
                    file = Sprite(path);
                }
            }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException or NotSupportedException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                _log.Warn($"{path}: {e.Message}");
            }
            if (file is null) { unresolved.Add(path); continue; }
            resolved++;
            assets.Add(Asset(path, file, kind));
        }
        // The item models: indexed by donor path like the recordings' models, though no recording names them yet.
        foreach (var (path, file) in tables.Models)
            if (!done.ContainsKey(path)) assets.Add(Asset(path, file));

        var descriptor = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assets"] = assets,
            ["provenance"] = new JsonObject
            {
                ["tool"] = "rusty-bridge dm-import",
                ["donor"] = "Dark Messiah of Might and Magic",
                ["map"] = map,
                ["recording"] = Path.GetFileName(recordingPath),
                ["recordings"] = new JsonArray(recordingPaths.Select(r => (JsonNode)Path.GetFileName(r)).ToArray()),
                ["convention"] = SourceSpace.Convention,
                ["invisible"] = new JsonArray(invisible.Select(u => (JsonNode)u).ToArray()),
                ["invisibleMeans"] = "brush models with no drawable faces (triggers, clips, tool brushes): the Wire reports them, nothing draws them, no GLB",
                ["unresolved"] = new JsonArray(unresolved.Select(u => (JsonNode)u).ToArray()),
                ["tables"] = Provenance(tables),
            },
        };
        // The recording travels with the assets it was imported for, so the
        // output folder is a whole content root for a product that replays it.
        string recordingFile = $"recordings/{Path.GetFileName(recordingPath)}";
        Directory.CreateDirectory(Path.Combine(_output, "recordings"));
        File.Copy(recordingPath, Path.Combine(_output, recordingFile), overwrite: true);
        descriptor["provenance"]!["recordingFile"] = recordingFile;

        string indexPath = Path.Combine(_output, $"{map}.assets.json");
        File.WriteAllText(indexPath, descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new LevelImportResult(indexPath, models.Count, resolved, invisible, unresolved, checkedSequences, differ, tables);
    }

    /// <summary>
    /// <see cref="Tables"/> into an existing import: copies them, then adds the item models to each level index in the
    /// output that lacks them, and records the tables in its provenance. Returns the indexes updated.
    /// </summary>
    public (TablesImportResult Tables, IReadOnlyList<string> Indexes) RefreshTables()
    {
        var tables = Tables();
        var updated = new List<string>();
        foreach (var index in Directory.Exists(_output) ? Directory.GetFiles(_output, "*.assets.json") : [])
        {
            var descriptor = JsonNode.Parse(File.ReadAllText(index))!.AsObject();
            var assets = descriptor["assets"]!.AsArray();
            var ids = assets.Select(a => (string?)a?["id"]).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, file) in tables.Models)
                if (ids.Add(path)) assets.Add(Asset(path, file));
            descriptor["provenance"]!["tables"] = Provenance(tables);
            File.WriteAllText(index, descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            updated.Add(index);
        }
        return (tables, updated);
    }

    /// <summary>What <see cref="Tables"/> copied, for an import's provenance.</summary>
    public static JsonObject Provenance(TablesImportResult tables) => new()
    {
        ["scripts"] = new JsonArray(tables.Scripts.Select(x => (JsonNode)x).ToArray()),
        ["itemFolders"] = new JsonArray(ItemFolders.Select(x => (JsonNode)x).ToArray()),
        ["itemTables"] = tables.ItemTables.Count,
        ["itemModels"] = tables.Models.Count,
        ["unresolved"] = new JsonArray(tables.Unresolved.Select(x => (JsonNode)x).ToArray()),
    };

    static JsonObject Asset(string id, string path, string kind = "model") => new() { ["id"] = id, ["kind"] = kind, ["path"] = path };
}
