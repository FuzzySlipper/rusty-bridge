using DarkMessiah.Combat;
using DarkMessiah.Shared;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Donor;

/// <summary>One imported model, opened once: its mesh, what dm-import recorded about it, its joints and its clips by name.</summary>
internal sealed class DonorModel(string donorPath, RenderResource mesh, ModelInfo info, IReadOnlyDictionary<string, int> joints,
    IReadOnlyDictionary<string, uint> clipIndices)
{
    public string DonorPath { get; } = donorPath;
    public RenderResource Mesh { get; } = mesh;
    public ModelInfo Info { get; } = info;
    public IReadOnlyDictionary<string, int> Joints { get; } = joints;
    /// <summary>Clips by name, as clip layers index them (the mesh's <c>ReadClips</c> order).</summary>
    public IReadOnlyDictionary<string, uint> ClipIndices { get; } = clipIndices;

    public ModelClip Clip(string name) => Info.Clips.TryGetValue(name, out var clip) ? clip
        : throw new InvalidOperationException($"{DonorPath} has no clip {name}");
}

/// <summary>
/// The donor's content the lab plays with: dm-import's output (models with their clips, hit boxes, hulls and events,
/// each model's .qct, the item models and tables, and the game's scripts), packed into one content container and opened at run time. The import is
/// derived from the game and never tracked, so it isn't part of the lab's staged content: the lab finds
/// <c>local/import</c> above where it runs (or <c>RUSTY_BRIDGE_IMPORT</c>, a container or an import folder), packs it
/// into <c>local/import.rpak</c> when the pack is missing or older than the import, and opens the pack.
/// </summary>
internal sealed class DonorContent : IDisposable
{
    readonly IEngineContext engine;
    readonly ProductContentBundle bundle;
    readonly Dictionary<string, DonorModel> models = new(StringComparer.OrdinalIgnoreCase);
    CombatData? combat;
    GameTables? tables;

    DonorContent(IEngineContext engine, ProductContentBundle bundle, string source)
    {
        this.engine = engine;
        this.bundle = bundle;
        Source = source;
    }

    /// <summary>Where the content came from: the container's path.</summary>
    public string Source { get; }

    /// <summary>Opens the donor's content, packing the import first when its pack is missing or stale.</summary>
    public static DonorContent Open(IEngineContext engine)
    {
        string pack = Pack(engine);
        return new DonorContent(engine, ProductContentBundle.OpenContainer(engine.Content, pack), pack);
    }

    static string Pack(IEngineContext engine)
    {
        string? given = Environment.GetEnvironmentVariable("RUSTY_BRIDGE_IMPORT");
        if (given is not null && File.Exists(given)) return given;
        string import = given is not null && Directory.Exists(given) ? given : FindImport()
            ?? throw new InvalidOperationException("No dm-import output found: run `dotnet run --project import/DarkMessiah.Import.Tool -- level local/recordings/l01_a-parrybot.rbrec local/recordings` (docs/lab.md), or set RUSTY_BRIDGE_IMPORT.");
        string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(import.TrimEnd('/', '\\')))!, "import.rpak");
        DateTime newest = Directory.EnumerateFiles(import, "*", SearchOption.AllDirectories).Max(File.GetLastWriteTimeUtc);
        if (!File.Exists(output) || File.GetLastWriteTimeUtc(output) < newest)
            ProductContentBundle.PackContainer(engine.Content, import, output, compress: false);
        return output;
    }

    /// <summary><c>local/import</c> of the repository above the lab's process or working directory, when it has an imported level.</summary>
    static string? FindImport()
    {
        // A natively hosted product has no base directory; its working directory and process are under the repository.
        string?[] starts = [AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(Environment.ProcessPath)];
        foreach (var start in starts.Where(s => !string.IsNullOrEmpty(s)))
            for (var dir = new DirectoryInfo(start!); dir is not null; dir = dir.Parent)
            {
                string import = Path.Combine(dir.FullName, "local", "import");
                if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(import) && Directory.EnumerateFiles(import, "*.assets.json").Any())
                    return import;
            }
        return null;
    }

    /// <summary>A donor file by donor path (any case, '\' or '/'), as dm-import wrote it; null when it isn't there.</summary>
    public byte[]? Read(string donorPath) =>
        bundle.TryReadFile(VpkIndex.Normalize(donorPath), out var file) ? bundle.ReadBytes(VpkIndex.Normalize(donorPath)).ToArray() : null;

    /// <summary>Arkane's combat tables from the imported .qct files and settings, with clip events from the GLBs.</summary>
    public CombatData Combat => combat ??= new CombatData(Read, model => Model(model).Info.Clips.Values
        .ToDictionary(c => c.Name, c => (IReadOnlyList<(float, int)>)c.Events, StringComparer.OrdinalIgnoreCase));

    /// <summary>The gameplay tables beyond the models': spells and skills (docs/import.md, "Gameplay tables").</summary>
    public GameTables Tables => tables ??= new GameTables(Read);

    /// <summary>A number in a block of a model's .qct (<c>entity_data</c>'s <c>speed_attack</c>, say); null when absent.</summary>
    public float? Number(string model, string block, string key) =>
        Read(Path.ChangeExtension(model, ".qct")) is byte[] qct && KeyValues.ParseAll(qct).FirstOrDefault()?.Find(block)?.Number(key) is float value ? value : null;

    /// <summary>A model by its donor path (<c>models/npc/orc/npc_orc.mdl</c>), opened on first use.</summary>
    public DonorModel Model(string donorPath)
    {
        if (models.TryGetValue(donorPath, out var model)) return model;
        string file = Path.ChangeExtension(VpkIndex.Normalize(donorPath), ".glb");
        RenderResource mesh;
        using (var reference = bundle.OpenReference(file))
            mesh = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
        var info = ModelInfo.FromGlb(bundle.ReadBytes(file).Span);
        var clips = new Dictionary<string, uint>(StringComparer.Ordinal);
        var read = engine.Animation.ReadClips(mesh).Span;
        for (int i = 0; i < read.Length; i++) { clips.TryAdd(read[i].Name, (uint)i); clips.TryAdd(read[i].Id, (uint)i); }
        return models[donorPath] = new DonorModel(donorPath, mesh, info, HitSweep.Joints(engine, mesh), clips);
    }

    public void Dispose()
    {
        foreach (var model in models.Values) model.Mesh.Dispose();
        bundle.Dispose();
    }
}
