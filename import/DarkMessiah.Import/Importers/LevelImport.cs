using System.Text.Json;
using System.Text.Json.Nodes;
using DarkMessiah.Import.Images;
using DarkMessiah.Import.Source;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Import;

/// <summary>Resolved counts Wire model paths with an asset or, for brush models with nothing to draw, a deliberate absence (<see cref="Invisible"/>).</summary>
public sealed record LevelImportResult(string IndexPath, int Requested, int Resolved, IReadOnlyList<string> Invisible, IReadOnlyList<string> Unresolved, int SequencesChecked, int SequencesDiffer);

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

    public ModelImportResult Model(string donorPath) =>
        new ModelImport(_files, _materials, _log).Import(donorPath, Path.Combine(_output, ModelFile(donorPath)));

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

    public LevelImportResult FromRecording(string recordingPath)
    {
        string map = "";
        var models = new SortedDictionary<int, string>();
        var sequences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        using (var stream = File.OpenRead(recordingPath))
            foreach (var frame in RecordingReader.Frames(stream))
            {
                map = frame.Map;
                foreach (var (index, path) in frame.Models) models[index] = path;
                foreach (var (index, names) in frame.Sequences)
                    if (frame.Models.TryGetValue(index, out var path)) sequences[path] = names;
            }
        if (map.Length == 0) throw new InvalidDataException($"{recordingPath}: no level record");

        var assets = new JsonArray();
        var unresolved = new List<string>();
        var invisible = new List<string>();
        var mapResult = Map($"maps/{map}.bsp");
        string Relative(string file) => Path.GetRelativePath(_output, file).Replace('\\', '/');
        assets.Add(Asset($"maps/{map}.bsp", Relative(mapResult.Models["*0"])));

        int resolved = 0, checkedSequences = 0, differ = 0;
        var done = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in models.Values.Distinct(StringComparer.Ordinal))
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
                ["convention"] = SourceSpace.Convention,
                ["invisible"] = new JsonArray(invisible.Select(u => (JsonNode)u).ToArray()),
                ["invisibleMeans"] = "brush models with no drawable faces (triggers, clips, tool brushes): the Wire reports them, nothing draws them, no GLB",
                ["unresolved"] = new JsonArray(unresolved.Select(u => (JsonNode)u).ToArray()),
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
        return new LevelImportResult(indexPath, models.Values.Distinct(StringComparer.Ordinal).Count(), resolved, invisible, unresolved, checkedSequences, differ);
    }

    static JsonObject Asset(string id, string path, string kind = "model") => new() { ["id"] = id, ["kind"] = kind, ["path"] = path };
}
