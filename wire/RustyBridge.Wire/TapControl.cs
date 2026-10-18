using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace RustyBridge.Wire;

/// <summary>
/// The control file beside the Wire (<c>rustybridge.control</c>), mirroring
/// <c>wire::Control</c> in <c>tap/darkmessiah/src/wire.h</c>. A reader writes
/// <see cref="Hold"/>, <see cref="StepRequests"/> and <see cref="LeaseUntilMs"/>;
/// the tap writes the rest.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WireControl
{
    public ulong Magic;
    public uint Version;
    public uint WriterPid;
    public int Hold;
    public uint StepRequests;
    public uint StepsDone;
    public int HeldTick;
    public ulong LeaseUntilMs;
    public uint HoldsLapsed;
    public uint Reserved;
    public uint CommandSeq;
    public uint CommandDone;
    public fixed byte Command[256];
}

/// <summary>
/// Holds the live donor's frame and steps it tick by tick (docs/wire.md,
/// "Holding the donor"). The tap holds right after writing a tick's Wire
/// frame, so while held the Wire's latest frame is the tick the game shows.
/// A hold lapses at its lease (a <c>GetTickCount64</c> time), so a reader that
/// goes away can't freeze the game for good.
/// </summary>
public sealed unsafe class TapControl : IDisposable
{
    public const ulong Magic = 0x31304C5254434252ul; // "RBCTRL01"
    public const uint Version = 1;
    public const int FileBytes = 4096;

    /// <summary>How long a hold lasts unless renewed: every command renews it.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);

    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly WireControl* _c;

    public TapControl(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        _file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _c = (WireControl*)(p + _view.PointerOffset);
    }

    /// <summary>The control file beside a Wire file.</summary>
    public static string PathBeside(string wirePath) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(wirePath)!, "rustybridge.control");

    public bool IsLive => Volatile.Read(ref _c->Magic) == Magic && _c->Version == Version;
    public bool Holding => Volatile.Read(ref _c->Hold) != 0;
    /// <summary>The tick the tap is holding at, or null while the donor runs.</summary>
    public int? HeldTick => Volatile.Read(ref _c->HeldTick) is int t and >= 0 ? t : null;
    public uint HoldsLapsed => Volatile.Read(ref _c->HoldsLapsed);

    /// <summary>Holds the donor after its next tick and waits until it is held there; returns the held tick.</summary>
    public int Hold(TimeSpan? lease = null, TimeSpan? timeout = null)
    {
        Renew(lease);
        Volatile.Write(ref _c->Hold, 1);
        return WaitHeld(timeout, _ => true);
    }

    /// <summary>Releases <paramref name="ticks"/> ticks and waits until the donor is held again; returns the held tick.</summary>
    public int Step(int ticks, TimeSpan? lease = null, TimeSpan? timeout = null)
    {
        if (ticks < 1) throw new ArgumentOutOfRangeException(nameof(ticks), "steps go forward only");
        int from = HeldTick ?? Hold(lease, timeout);
        Renew(lease);
        uint target = Volatile.Read(ref _c->StepRequests) + (uint)ticks;
        Volatile.Write(ref _c->StepRequests, target);
        return WaitHeld(timeout, tick => tick != from && Volatile.Read(ref _c->StepsDone) == target);
    }

    /// <summary>
    /// Runs a console command in the donor through the client's command
    /// buffer, and waits until the tap has queued it. It runs on the next
    /// frame, so not while the donor is held.
    /// </summary>
    public void Command(string text, TimeSpan? timeout = null)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        if (bytes.Length > 255) throw new ArgumentException("a command is at most 255 characters", nameof(text));
        var command = new Span<byte>(_c->Command, 256);
        command.Clear();
        bytes.CopyTo(command);
        uint seq = Volatile.Read(ref _c->CommandSeq) + 1;
        Thread.MemoryBarrier();
        Volatile.Write(ref _c->CommandSeq, seq);
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref _c->CommandDone) != seq)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the tap didn't take the command; is the donor held or loading?");
            Thread.Sleep(1);
        }
    }

    /// <summary>Lets the donor run again.</summary>
    public void Run() => Volatile.Write(ref _c->Hold, 0);

    void Renew(TimeSpan? lease) =>
        Volatile.Write(ref _c->LeaseUntilMs, (ulong)Environment.TickCount64 + (ulong)(lease ?? DefaultLease).TotalMilliseconds);

    int WaitHeld(TimeSpan? timeout, Func<int, bool> done)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < until)
        {
            if (!Holding) throw new InvalidOperationException("the hold was released (its lease lapsed, or another reader ran the donor)");
            if (HeldTick is int tick && done(tick)) return tick;
            Thread.Sleep(1);
        }
        throw new TimeoutException("the donor didn't reach a held tick; is a level running?");
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
