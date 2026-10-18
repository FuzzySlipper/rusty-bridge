using System.Numerics;
using System.Text;
using DarkMessiah.Mirror;
using DarkMessiah.Mirror.Replay;
using Rusty.Engine;
using Rusty.Engine.Debugging;
using Rusty.Engine.Testing;
using RustyBridge.Wire;

// Replay checks need nothing; the product run needs dm-import's output in local/import (donor-derived,
// never tracked) and runs the real pinned Engine without a browser. Exit 0 when every check passes.
int failures = 0;
void Check(bool condition, string message)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {message}");
    if (!condition) failures++;
}

// An entity is drawn moving from its second-to-last update to its last, over the gap between them.
static WireEntity At(float x, short sequence, float cycle) => new()
{
    OriginX = x, Sequence = sequence, Cycle = cycle, Flags = WireEntityFlags.HasAnimation,
};
var npc = new EntityTrack(new EntityKey(5, 1), "npc_orc_sword", 0);
npc.Observe(0, 0.00, At(0, 3, 0.80f));
npc.Observe(1, 0.015, At(0, 3, 0.80f));   // unchanged: not an update
npc.Observe(8, 0.12, At(12, 3, 0.20f));   // 8 ticks later, the cycle wrapped
Check(npc.Updates.Count == 2, "an unchanged state is not an update");
Check(npc.Sample(0.10).Origin.X == 0, "before its last update the entity is still at the one before");
Check(MathF.Abs(npc.Sample(0.18).Origin.X - 6) < 1e-4f, "halfway through the next interval it is halfway there");
Check(MathF.Abs(npc.Sample(0.18).Cycle - 0.0f) < 1e-4f, "the cycle runs forward through the wrap (0.8 → 1.2 ≡ 0.0 at half)");
Check(npc.Sample(0.30).Origin.X == 12, "after a full interval it is where the last update put it");
var door = new EntityTrack(new EntityKey(6, 1), "func_door", 0);
door.Observe(0, 0, At(0, -1, 0));
door.Observe(600, 9.0, At(100, -1, 0));
Check(door.Sample(9.0 + EntityTrack.MaxSpanSeconds / 2).Origin.X == 50, "a long still spell is not stretched: the move spreads over at most MaxSpanSeconds");
var swap = EntityTrack.Blend(At(0, 3, 0.9f), At(0, 7, 0.4f), 0.5f);
Check(swap.Sequence == 7 && MathF.Abs(swap.Cycle - 0.2f) < 1e-4f, "a new sequence is taken as starting at the earlier update");

Check(DonorClasses.Role("npc_orc_sword") == EntityRole.Character && DonorClasses.Role("player") == EntityRole.Character, "NPCs and the player are characters");
Check(DonorClasses.Role("weapon_mm_shield_orc") == EntityRole.Held && DonorClasses.Attachment("weapon_mm_shield_orc") == "LeftShield", "a shield hangs from LeftShield");
Check(DonorClasses.Attachment("weapon_arxorccleaver") == "RightHand", "a weapon hangs from RightHand");
Check(DonorClasses.Role("phys_bone_follower") == EntityRole.Hidden && DonorClasses.Role("viewmodel") == EntityRole.Hidden, "physics helpers and the view model are not drawn");

string? repo = null;
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(dir.FullName, "mirror"))) { repo = dir.FullName; break; }
string import = Path.Combine(repo ?? ".", "local", "import");
string? index = Directory.Exists(import) ? Directory.GetFiles(import, "*.assets.json").FirstOrDefault() : null;
if (index is null)
{
    Console.WriteLine($"skip product run: no imported level in {import} (dm-import level; docs/import.md)");
    return failures == 0 ? 0 : 1;
}

// The real product on the imported level, through the Engine's test host.
var content = Directory.GetFiles(import, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(import, path).Replace('\\', '/'), path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path));
using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions
{
    PersistenceRoot = Path.Combine(Path.GetTempPath(), "mirror-smoke-" + Guid.NewGuid()),
    Content = content,
});
host.Call(engine =>
{
    string indexName = Path.GetFileName(index);
    var files = new ProductContentFile[] { new(Encoding.UTF8.GetBytes(indexName), content[indexName]) };
    using var product = new MirrorProduct(new ProductCreateContext(engine, new ProductContent(files, engine.Content),
        new ProductInputConfiguration(default, default, default, default, InputCursorMode.PointerLock), default!));
    var commands = new Capture();
    product.RegisterDebugCommands(commands);
    var mirror = commands.Mirror!;
    ulong step = 0;
    void Advance(uint count)
    {
        product.Update(new ProductUpdate(new ProductUpdateFacts(ProductLifecycleState.Running, 1, 1, 0, step, 60, count, 0, 1d / 60), default));
        step += count;
    }

    Console.WriteLine(mirror.Status());
    int first = product.Fight.Frames[0].Tick;
    Advance(60);
    int ticks = product.Clock.Current.Tick - first;
    Check(ticks >= 64 && ticks <= 68, $"a second of play advances about 66 donor ticks (l01_a's 15 ms): {ticks}");
    Check(product.Views.Drawn > 200, $"the level's entities are drawn: {product.Views.Drawn}");
    Check(product.Assets.Missing.Count == 0, $"every Wire model path has an imported asset: {string.Join(", ", product.Assets.Missing)}");

    // The parry bot's orc swinging at the player.
    mirror.Seek(11520);
    // A holder that just appeared takes its items from the next update on.
    Advance(1);
    Console.WriteLine(mirror.Status());
    Check(product.Views.Sampled > 200, $"characters and props sample their streamed sequence: {product.Views.Sampled}");
    Check(product.Views.Attached >= 2, $"the orc's cleaver and shield hang from it: {product.Views.Attached} held");
    Check(product.Views.MissingClips.Count == 0, $"every streamed sequence is a clip: {string.Join(", ", product.Views.MissingClips)}");
    Check(product.Views.LastError is null, $"the Engine accepted every scene change: {product.Views.LastError}");

    mirror.Step(1);
    Check(product.Clock.Current.Tick == 11521 && !product.Clock.Playing, "a step pauses and moves one donor tick");
    mirror.Step(-1);
    Check(product.Clock.Current.Tick == 11520, "a step back returns");
    mirror.Toggle();
    for (int i = 0; i < 20; i++) Advance(60);
    Check(product.Views.LastError is null, $"twenty seconds of play through deaths and drops publish cleanly: {product.Views.LastError}");
});
return failures == 0 ? 0 : 1;

sealed class Capture : IDebugCommandModuleRegistrar
{
    internal MirrorDebugCommands? Mirror { get; private set; }
    DebugCommandRegistrationResult IDebugCommandModuleRegistrar.Register<TModule>(TModule module)
    {
        if (module is MirrorDebugCommands mirror) Mirror = mirror;
        return new(DebugCommandRegistrationStatus.Registered, "captured for the smoke run");
    }
}
