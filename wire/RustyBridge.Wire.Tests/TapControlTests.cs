using System.Runtime.InteropServices;
using RustyBridge.Wire;
using Xunit;

public sealed class TapControlTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("rbctrl-").FullName;
    string ControlPath => Path.Combine(_dir, "rustybridge.control");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void LayoutMatchesTheTapHeader()
    {
        Assert.Equal(312, Marshal.SizeOf<WireControl>());
        Assert.Equal(56, (int)Marshal.OffsetOf<WireControl>("Command"));
        Assert.Equal(16, (int)Marshal.OffsetOf<WireControl>(nameof(WireControl.Hold)));
        Assert.Equal(28, (int)Marshal.OffsetOf<WireControl>(nameof(WireControl.HeldTick)));
        Assert.Equal(32, (int)Marshal.OffsetOf<WireControl>(nameof(WireControl.LeaseUntilMs)));
    }

    /// <summary>The tap's side of the protocol (plugin.cpp HoldIfAsked), one call per tick, on its own thread.</summary>
    sealed unsafe class FakeTap : IDisposable
    {
        readonly FileStream _stream;
        readonly System.IO.MemoryMappedFiles.MemoryMappedFile _file;
        readonly System.IO.MemoryMappedFiles.MemoryMappedViewAccessor _view;
        readonly WireControl* _c;
        readonly Thread _thread;
        volatile bool _stop;
        public volatile int Tick;
        public readonly List<string> Commands = [];

        public FakeTap(string path)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            _stream.SetLength(TapControl.FileBytes);
            _file = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(_stream, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            _view = _file.CreateViewAccessor(0, TapControl.FileBytes);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _c = (WireControl*)(p + _view.PointerOffset);
            _c->Version = TapControl.Version;
            _c->HeldTick = -1;
            Volatile.Write(ref _c->Magic, TapControl.Magic);
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        bool Lapsed()
        {
            if ((ulong)Environment.TickCount64 <= Volatile.Read(ref _c->LeaseUntilMs)) return false;
            Volatile.Write(ref _c->Hold, 0);
            _c->HoldsLapsed++;
            return true;
        }

        void Run()
        {
            while (!_stop)
            {
                uint seq = Volatile.Read(ref _c->CommandSeq);
                if (seq != _c->CommandDone)
                {
                    lock (Commands) Commands.Add(System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(_c->Command, 256)).TrimEnd('\0'));
                    Volatile.Write(ref _c->CommandDone, seq);
                }
                Tick++;                                   // the tick's Wire frame is written here
                if (Volatile.Read(ref _c->Hold) != 0 && !Lapsed())
                {
                    if (Volatile.Read(ref _c->StepRequests) != _c->StepsDone) { _c->StepsDone++; continue; }
                    Volatile.Write(ref _c->HeldTick, Tick);
                    while (!_stop && Volatile.Read(ref _c->Hold) != 0 && Volatile.Read(ref _c->StepRequests) == _c->StepsDone && !Lapsed()) Thread.Sleep(1);
                    if (Volatile.Read(ref _c->Hold) != 0) _c->StepsDone++;
                    Volatile.Write(ref _c->HeldTick, -1);
                }
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
            _stream.Dispose();
        }
    }

    [Fact]
    public void HoldStopsTheTicksAndEachStepAdvancesExactlyTheTicksAsked()
    {
        using var tap = new FakeTap(ControlPath);
        using var control = new TapControl(ControlPath);
        Assert.True(control.IsLive);

        int held = control.Hold();
        Thread.Sleep(50);
        Assert.Equal(held, tap.Tick);                   // nothing ran while held
        Assert.Equal(held + 1, control.Step(1));
        Assert.Equal(held + 4, control.Step(3));
        Thread.Sleep(50);
        Assert.Equal(held + 4, tap.Tick);

        control.Run();
        Thread.Sleep(50);
        Assert.Null(control.HeldTick);
        Assert.True(tap.Tick > held + 4);
    }

    [Fact]
    public void AHoldLapsesAtItsLease()
    {
        using var tap = new FakeTap(ControlPath);
        using var control = new TapControl(ControlPath);
        control.Hold(lease: TimeSpan.FromMilliseconds(100));
        Thread.Sleep(300);
        Assert.False(control.Holding);
        Assert.Equal(1u, control.HoldsLapsed);
    }

    [Fact]
    public void ACommandReachesTheTapOnce()
    {
        using var tap = new FakeTap(ControlPath);
        using var control = new TapControl(ControlPath);
        control.Command("exec rbbot");
        control.Command("god");
        Thread.Sleep(20);
        lock (tap.Commands) Assert.Equal(["exec rbbot", "god"], tap.Commands);
    }
}
