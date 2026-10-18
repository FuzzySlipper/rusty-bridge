using System.Globalization;
using System.Text.Json.Nodes;
using DarkMessiah.Mirror.Replay;
using DarkMessiah.Mirror.Scene;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace DarkMessiah.Mirror;

/// <summary>
/// The Frontend mirror (design Phase 0): replays a recorded Dark Messiah
/// fight from the Wire recording dm-import put beside the level's assets.
/// Every entity with an imported model is drawn where the donor had it,
/// characters hold their streamed sequence at its streamed cycle, and held
/// items hang from their holder's hands. Play, pause and step by donor tick.
/// </summary>
public sealed class MirrorProduct : IEngineProduct, IDebugCommandModuleSource
{
    readonly IEngineContext engine;
    readonly LevelAssets assets;
    readonly RecordedFight fight;
    readonly ReplayClock clock;
    readonly HeldItems held;
    readonly EntityViews views;
    readonly ReplayCamera camera;
    readonly UiStream status;
    string lastStatus = "";

    public MirrorProduct(ProductCreateContext context)
    {
        engine = context.Engine;
        string descriptor = context.Content.ReadDirectory("", false).Select(f => f.RelativePath)
            .FirstOrDefault(p => p.EndsWith(".assets.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("no <map>.assets.json in the content root; run dm-import level (docs/import.md)");
        try
        {
            assets = new LevelAssets(engine, descriptor);
            using (var recording = new MemoryStream(Scene.Content.ReadAll(engine, assets.RecordingFile)))
                fight = RecordedFight.Read(recording);
            clock = new ReplayClock(fight);
            held = new HeldItems(fight);
            views = new EntityViews(engine, fight, assets, held);
            camera = new ReplayCamera(engine, fight);
            status = engine.Ui.OpenStream(new UiStreamRequest("darkmessiah-mirror", "rusty-bridge.mirror.status"));
            Present();
        }
        catch
        {
            // Releasing what was built must not hide why building failed.
            try { Dispose(); } catch (EngineCallException) { }
            throw;
        }
    }

    internal ReplayClock Clock => clock;
    internal RecordedFight Fight => fight;
    internal EntityViews Views => views;
    internal HeldItems Held => held;
    internal LevelAssets Assets => assets;
    internal ReplayCamera Camera => camera;

    public ProductUpdateResult Update(ProductUpdate update)
    {
        foreach (var input in update.Input)
        {
            if (input.Kind != InputEventKind.MappedDigital || input.Edge != InputEdge.Pressed) continue;
            var intent = input.Intent.Span;
            if (intent.SequenceEqual("mirror.toggle"u8)) clock.Toggle();
            else if (intent.SequenceEqual("mirror.step.forward"u8)) clock.Step(1);
            else if (intent.SequenceEqual("mirror.step.back"u8)) clock.Step(-1);
            else if (intent.SequenceEqual("mirror.faster"u8)) clock.Faster();
            else if (intent.SequenceEqual("mirror.slower"u8)) clock.Slower();
            else if (intent.SequenceEqual("mirror.camera"u8)) camera.Next();
        }
        clock.Advance(update.Facts.AdmittedStepCount * update.Facts.FixedDeltaSeconds);
        Present();
        return ProductUpdateResult.None;
    }

    internal void Present()
    {
        views.Present(clock.Frame, clock.Time);
        camera.Follow(clock.Time);
        PublishStatus();
    }

    void PublishStatus()
    {
        var frame = clock.Current;
        var value = new JsonObject
        {
            ["map"] = fight.Map,
            ["tick"] = frame.Tick,
            ["firstTick"] = fight.Frames[0].Tick,
            ["lastTick"] = fight.Frames[^1].Tick,
            ["seconds"] = Math.Round(clock.Time - fight.Frames[0].Time, 2),
            ["playing"] = clock.Playing,
            ["speed"] = clock.Speed,
            ["camera"] = camera.Mode.ToString(),
            ["drawn"] = views.Drawn,
            ["held"] = views.Attached,
        };
        string text = value.ToJsonString();
        if (text == lastStatus) return;
        lastStatus = text;
        engine.Ui.PublishProjection(new(status, UiValues.FromJson(value)));
    }

    internal string Describe()
    {
        static string List(IReadOnlyCollection<string> items) => items.Count == 0 ? "0" : $"{items.Count} ({string.Join(", ", items.Take(5))})";
        string text = string.Create(CultureInfo.InvariantCulture,
            $"map={fight.Map}; tick={clock.Current.Tick}; frame={clock.Frame}/{fight.Frames.Count}; playing={clock.Playing}; speed={clock.Speed}; " +
            $"drawn={views.Drawn}; held={views.Attached}; sampled={views.Sampled}; layered={views.Layered} ({views.LayersDrawn} layers); holders={held.Count}/{held.Items}; camera={camera.Mode}; " +
            $"missingModels={List(assets.Missing)}; missingClips={List(views.MissingClips)}");
        return views.LastError is string error ? $"{text}; lastError={error}" : text;
    }

    internal string DescribeHeld()
    {
        var lines = new List<string>();
        foreach (var item in fight.Tracks.Where(t => t.LiveAt(clock.Frame) && DonorClasses.Role(t.ClassName) == EntityRole.Held && t.UpdateAt(clock.Time) >= 0))
        {
            var sample = item.Sample(clock.Time);
            string who;
            if (held.Streamed)
            {
                // v10: the streamed move parent and the attachment it names.
                var holding = held.Holder(item, clock.Frame, clock.Time);
                who = holding is Holding h
                    ? $"streamed {h.Holder.ClassName}{h.Holder.Key} attachment {h.Attachment}" +
                      (views.ModelOf(h.Holder) is Scene.LevelModel m && h.Attachment >= 1 && h.Attachment <= m.AttachmentList.Count ? $" ({m.AttachmentList[h.Attachment - 1].Name})" : "")
                    : sample.Parent is EntityKey key ? $"streamed {key} (not drawn)" : "none";
            }
            else
            {
                var holder = held.InferredHolder(item);
                who = holder is null ? "none"
                    : $"inferred {holder.ClassName}{holder.Key} live={holder.LiveAt(clock.Frame)} d={(holder.UpdateAt(clock.Time) >= 0 ? System.Numerics.Vector3.Distance(sample.Origin, holder.Sample(clock.Time).Origin) : -1):F0}";
            }
            lines.Add($"{item.ClassName}{item.Key} {fight.ModelPath(sample.ModelIndex)} holder={who} hung={views.IsHung(item)}");
        }
        return string.Join(" | ", lines);
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar) => registrar.Register(new MirrorDebugCommands(this));

    public void Start() { }
    public void Pause() { }
    public void Resume() { }
    public void Restart() { }
    public void Shutdown() { }

    public void Dispose()
    {
        status?.Dispose();
        camera?.Dispose();
        views?.Dispose();
        assets?.Dispose();
    }
}
