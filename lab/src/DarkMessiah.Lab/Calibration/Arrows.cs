using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// The donor's arrows in a recording (#10168): a damage record whose inflictor is an <c>arrowshot</c>. Its damage type
/// carries the kick's bit (0x40000) and the bow's clips are no swing's, so the readers that take that bit with no swing
/// on screen for a kick leave arrows out with <see cref="Is"/>, reading the frames through <see cref="Frames"/>. The arrow is often gone in the frame its blow arrives:
/// its class is remembered by index and serial (<see cref="EventNames"/>).
/// </summary>
internal sealed class Arrows
{
    internal const string Class = "arrowshot";
    readonly EventNames names = new();

    /// <summary>A recording's frames, each one's entities remembered before it's handed on.</summary>
    internal IEnumerable<RecordedFrame> Frames(Stream recording)
    {
        foreach (var f in RecordingReader.Frames(recording))
        {
            names.Observe(f);
            yield return f;
        }
    }

    internal bool Is(RecordedFrame frame, in WireDamage d) => names.Name(frame, d.Inflictor, d.InflictorSerial) == Class;
}
