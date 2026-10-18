namespace DarkMessiah.Mirror.Replay;

/// <summary>
/// The presentation clock, in donor time (curtime), not ticks: it plays at a
/// speed, pauses, and steps by donor tick like the stepper.
///
/// A recording carries tick and curtime only, so a hit slow-motion that
/// slows the donor's ticks against wall time replays at full speed here.
/// It isn't in the data (see the Den task for slow motion and time scale).
/// </summary>
internal sealed class ReplayClock(RecordedFight fight)
{
    static readonly double[] Speeds = [0.1, 0.25, 0.5, 1, 2, 4];

    int speed = Array.IndexOf(Speeds, 1d);

    public double Time { get; private set; } = fight.Frames.Count > 0 ? fight.Frames[0].Time : 0;
    public bool Playing { get; private set; } = true;
    public double Speed => Speeds[speed];
    public int Frame => fight.FrameAt(Time);
    public FightFrame Current => fight.Frames[Frame];
    public bool AtEnd => Frame == fight.Frames.Count - 1;

    /// <summary>Moves on by <paramref name="seconds"/> of wall time while playing; stops at the last frame.</summary>
    public void Advance(double seconds)
    {
        if (!Playing || fight.Frames.Count == 0) return;
        Time = Math.Min(Time + seconds * Speed, fight.Frames[^1].Time);
        if (AtEnd) Playing = false;
    }

    public void Toggle()
    {
        // Playing again from the end starts over.
        if (!Playing && AtEnd) Time = fight.Frames[0].Time;
        Playing = !Playing;
    }

    public void Pause() => Playing = false;

    /// <summary>Pauses and moves by whole donor ticks (recorded frames).</summary>
    public void Step(int frames)
    {
        Playing = false;
        Seek(Math.Clamp(Frame + frames, 0, fight.Frames.Count - 1));
    }

    public void Seek(int frame) => Time = fight.Frames[Math.Clamp(frame, 0, fight.Frames.Count - 1)].Time;

    /// <summary>Seeks to the first frame at or after a donor tick.</summary>
    public void SeekTick(int tick)
    {
        int frame = 0;
        while (frame < fight.Frames.Count - 1 && fight.Frames[frame].Tick < tick) frame++;
        Seek(frame);
    }

    public void Faster() => speed = Math.Min(speed + 1, Speeds.Length - 1);
    public void Slower() => speed = Math.Max(speed - 1, 0);
}
