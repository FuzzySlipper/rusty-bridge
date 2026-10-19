using System.Globalization;
using Rusty.Engine;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// What input reached the product: counts in the last update, running totals, and the last event, as one readout.
/// It explains a scripted or live session's motion ("did the key arrive? was it cleared?") and owns no gameplay state.
/// Provenance: rusty-craftsurvive <c>PlayerInputDiagnostics</c>, trimmed to the events.
/// </summary>
internal sealed class InputDiagnostics
{
    private int events, keys, pointer, buttons, axes, clears;
    private ulong totalEvents, totalKeys, totalClears, lastEventStep;
    private string lastEvent = "none";

    internal void Capture(ReadOnlySpan<ProductInputEvent> input, ulong step)
    {
        events = input.Length;
        keys = pointer = buttons = axes = clears = 0;
        foreach (ProductInputEvent e in input)
        {
            totalEvents++;
            lastEventStep = step;
            switch (e.Kind)
            {
                case InputEventKind.Key: keys++; totalKeys++; lastEvent = $"key:{e.Keyboard}:{e.Edge}"; break;
                case InputEventKind.PointerDelta:
                    pointer++;
                    lastEvent = string.Create(CultureInfo.InvariantCulture, $"pointer-delta:{e.X:F3},{e.Y:F3}");
                    break;
                case InputEventKind.PointerButton: pointer++; lastEvent = $"pointer-button:{e.PointerButton}:{e.Edge}"; break;
                case InputEventKind.ControllerButton: buttons++; lastEvent = $"controller-button:{e.ControllerButton}:{e.Edge}"; break;
                case InputEventKind.ControllerAxis:
                    axes++;
                    lastEvent = string.Create(CultureInfo.InvariantCulture, $"controller-axis:{e.ControllerAxis}:{e.X:F3}");
                    break;
                case InputEventKind.Clear: clears++; totalClears++; lastEvent = $"clear:{e.ClearReason}"; break;
                default: lastEvent = e.Kind.ToString(); break;
            }
        }
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"events={events};keys={keys};pointer={pointer};controllerButtons={buttons};controllerAxes={axes};clears={clears};totalEvents={totalEvents};totalKeys={totalKeys};totalClears={totalClears};lastEventStep={lastEventStep};lastEvent={lastEvent}");
}
