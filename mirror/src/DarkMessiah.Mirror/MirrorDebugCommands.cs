using DarkMessiah.Mirror.Scene;
using Rusty.Engine.Debugging;

namespace DarkMessiah.Mirror;

/// <summary>Live-debug control of the replay, for harnesses and the console: each answers with the replay's state.</summary>
internal sealed class MirrorDebugCommands(MirrorProduct product) : IDebugCommandModule
{
    [DebugCommand("mirror.status", Description = "Tick, playback, entities drawn and held, anything the import lacks")]
    public string Status() => product.Describe();

    [DebugCommand("mirror.toggle", Description = "Play or pause")]
    public string Toggle() { product.Clock.Toggle(); return Then(); }

    [DebugCommand("mirror.step", Description = "Pause and move by N donor ticks (negative steps back)")]
    public string Step(int ticks) { product.Clock.Step(ticks); return Then(); }

    [DebugCommand("mirror.seek", Description = "Pause at the first frame at or after a donor tick")]
    public string Seek(int tick) { product.Clock.Pause(); product.Clock.SeekTick(tick); return Then(); }

    [DebugCommand("mirror.camera", Description = "chase, eye or overview")]
    public string Camera(string mode)
    {
        product.Camera.Mode = Enum.Parse<CameraMode>(mode, ignoreCase: true);
        return Then();
    }

    [DebugCommand("mirror.hits", Description = "Damage records as tick:attacker>victim, optionally only those naming a class")]
    public string Hits(string filter) => string.Join(" ", product.Fight.Hits
        .Where(h => filter is "" or "*" || h.Attacker.Contains(filter, StringComparison.Ordinal) || h.Victim.Contains(filter, StringComparison.Ordinal))
        .Select(h => $"{product.Fight.Frames[h.Frame].Tick}:{h.Attacker}>{h.Victim}").Take(80));

    [DebugCommand("mirror.held", Description = "Each live weapon, shield and helmet: its inferred holder, their distance, and whether it hangs from them now")]
    public string Held() => product.DescribeHeld();

    string Then()
    {
        product.Present();
        return product.Describe();
    }
}
