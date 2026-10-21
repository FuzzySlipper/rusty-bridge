using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using RustyBridge.Wire;

/// <summary>
/// <c>rbwire scenario NAME</c> (docs/recording.md, "Scenarios"): stages and records one mechanic on den-win11.
/// <list type="number">
/// <item>Launches the game straight into a save (<c>mm.exe -game mm -steam ... +load rb_start</c>, with Steam's app id in
/// the environment, so Steam's own launch options are left alone). A game rbwire launched before (its process id and
/// start time in <c>game.pid</c> beside rbwire) is closed first, or reused with <c>--reuse</c>. Any other running game is
/// someone else's: the scenario stops and leaves it alone, unless <c>--replace</c> says to close it.</item>
/// <item>Waits for the tap's <c>dump (level+5s)</c> line: commands sent while the save loads shut the game down.</item>
/// <item>Runs the scenario's setup console lines through the tap's control file.</item>
/// <item>Records for the scenario's seconds, and meanwhile plays its scripted input through the playtest agent's
/// desktop lease, or nothing with <c>--hand</c> (someone plays over Moonlight).</item>
/// </list>
/// Input goes only to the game: before every step the game must be running and in front (it is brought forward once
/// if not), else the script stops. Run it with <c>run-on-desktop.ps1 -NoWait</c> so it outlives the SSH session; it
/// logs beside the recording.
/// </summary>
static class Scenario
{
    /// <summary>A scenario file: <c>tools/scenarios/NAME.json</c>, copied beside rbwire as <c>scenarios/NAME.json</c>.</summary>
    internal sealed record Plan(string Name, string Description, double Seconds, string[] Launch, JsonArray Setup, JsonArray? Play);

    internal static readonly string[] DefaultLaunch = ["-condebug", "-console", "-window", "-w", "1280", "-h", "720", "-novid", "+load", "rb_start"];

    internal static Plan Load(string nameOrPath)
    {
        string path = File.Exists(nameOrPath) ? nameOrPath : Path.Combine(AppContext.BaseDirectory, "scenarios", nameOrPath + ".json");
        if (!File.Exists(path))
        {
            var known = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "scenarios"))
                ? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scenarios"), "*.json").Select(Path.GetFileNameWithoutExtension) : [];
            throw new FileNotFoundException($"no scenario {nameOrPath} (known: {string.Join(", ", known)})");
        }
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return new Plan(Path.GetFileNameWithoutExtension(path), (string?)json["description"] ?? "",
            (double?)json["seconds"] ?? 60,
            json["launch"]?.AsArray().Select(a => (string)a!).ToArray() ?? DefaultLaunch,
            json["setup"]?.AsArray() ?? [], json["play"]?.AsArray());
    }

    /// <param name="record">Records the live Wire to a file for the given seconds (rbwire record).</param>
    /// <param name="replace">Close a running game rbwire didn't launch (otherwise the scenario stops and leaves it).</param>
    internal static int Run(Plan plan, string game, string wirePath, string outPath, string agent, bool reuse, bool hand, bool replace,
        Func<string, double, int> record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var log = new StreamWriter(Path.ChangeExtension(outPath, ".log")) { AutoFlush = true };
        var console = Console.Out;
        Console.SetOut(new Tee(console, log));
        try
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} scenario {plan.Name}: {plan.Description}");
            if (Game() is { } running && !Owned(running) && !replace)
                throw new InvalidOperationException($"mm.exe {running.Id} is running and rbwire didn't launch it: leaving it alone (--replace closes it)");
            if (!reuse || Game() is null) Launch(game, plan.Launch);
            WaitForLevel(game);
            using var control = new TapControl(TapControl.PathBeside(wirePath));
            foreach (var step in plan.Setup) Setup(control, step!, wirePath);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} recording {plan.Seconds:F0} s to {outPath}");
            var recording = Task.Run(() => record(outPath, plan.Seconds));
            if (plan.Play is { } play && !hand) Play(play, control, agent, plan.Seconds, wirePath);
            else Console.WriteLine(hand ? "hand-played: no scripted input" : "no scripted input");
            int result = recording.Result;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} done");
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or AggregateException)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} failed: {e.GetBaseException().Message}{(Game() is null ? " (the game isn't running)" : "")}");
            return 1;
        }
        finally { Console.SetOut(console); }
    }

    /// <param name="height">Units above the NPC's feet to put the player (4 stands it on level ground; more drops it).</param>
    /// <param name="pitch">The view's pitch, degrees down (0 looks level).</param>
    static (string Setpos, string Setang) Approach(string wirePath, string cls, float distance, float height = 4, float pitch = 0)
    {
        using var reader = new WireReader(wirePath);
        var snap = new WireSnapshot();
        if (!reader.TryReadLatest(snap)) throw new InvalidOperationException("no frame on the Wire");
        var entities = snap.Span.ToArray();
        var player = entities.FirstOrDefault(e => reader.ClassName(snap, e.ClassId) == "player");
        var found = entities.Where(e => reader.ClassName(snap, e.ClassId) == cls && e.LifeState == 0 && e.Health > 0)
            .OrderBy(e => Math.Pow(e.OriginX - player.OriginX, 2) + Math.Pow(e.OriginY - player.OriginY, 2)).ToList();
        if (found.Count == 0) throw new InvalidOperationException($"no living {cls} on the Wire to approach");
        var npc = found[0];
        double dx = player.OriginX - npc.OriginX, dy = player.OriginY - npc.OriginY, d = Math.Sqrt(dx * dx + dy * dy);
        (dx, dy) = d > 1 ? (dx / d, dy / d) : (Math.Cos(npc.Yaw * Math.PI / 180), Math.Sin(npc.Yaw * Math.PI / 180));
        double x = npc.OriginX + dx * distance, y = npc.OriginY + dy * distance;
        double yaw = Math.Atan2(-dy, -dx) * 180 / Math.PI;
        Console.WriteLine(FormattableString.Invariant($"  approach {cls} #{npc.Index} at ({npc.OriginX:F0} {npc.OriginY:F0} {npc.OriginZ:F0}) from {distance:F0} units, {height:F0} up"));
        return (FormattableString.Invariant($"setpos {x:F1} {y:F1} {npc.OriginZ + height:F1}"), FormattableString.Invariant($"setang {pitch:F1} {yaw:F1} 0"));
    }

    static Process? Game() => Process.GetProcessesByName("mm").FirstOrDefault();

    /// <summary>Where rbwire notes the game it launched: its process id and start time (a reused id isn't the same game).</summary>
    static string OwnedPath => Path.Combine(AppContext.BaseDirectory, "game.pid");

    static string Identity(Process p) => FormattableString.Invariant($"{p.Id} {p.StartTime.ToUniversalTime().Ticks}");

    /// <summary>Whether rbwire launched this running game.</summary>
    internal static bool Owned(Process p)
    {
        try { return File.Exists(OwnedPath) && File.ReadAllText(OwnedPath).Trim() == Identity(p); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    /// <summary><c>rbwire close-game</c>: closes the game rbwire launched; a game it didn't launch is left running.</summary>
    internal static int CloseOwned()
    {
        if (Game() is not { } running) { Console.WriteLine("no game running"); return 0; }
        if (!Owned(running)) { Console.WriteLine($"mm.exe {running.Id} wasn't launched by rbwire: leaving it running"); return 1; }
        running.Kill();
        running.WaitForExit(15_000);
        File.Delete(OwnedPath);
        Console.WriteLine($"closed mm.exe {running.Id}");
        return 0;
    }

    static void Launch(string game, string[] arguments)
    {
        if (Game() is { } running)
        {
            // Run already refused another's game unless --replace said to close it.
            Console.WriteLine($"closing the running game ({running.Id}{(Owned(running) ? ", launched by rbwire" : ", --replace")})");
            running.Kill();
            running.WaitForExit(15_000);
        }
        string tapLog = Path.Combine(game, "mm", "addons", "rustybridge.log");
        if (File.Exists(tapLog)) File.Delete(tapLog);
        var start = new ProcessStartInfo(Path.Combine(game, "mm.exe")) { WorkingDirectory = game, UseShellExecute = false };
        foreach (var a in new[] { "-game", "mm", "-steam" }.Concat(arguments)) start.ArgumentList.Add(a);
        // Steam sets these for a game it launches; without them the game doesn't mount its content.
        start.Environment["SteamAppId"] = "2100";
        start.Environment["SteamGameId"] = "2100";
        var p = Process.Start(start)!;
        File.WriteAllText(OwnedPath, Identity(p));
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} launched {p.Id}: mm.exe {string.Join(' ', start.ArgumentList)}");
    }

    static void WaitForLevel(string game)
    {
        string tapLog = Path.Combine(game, "mm", "addons", "rustybridge.log");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 300)
        {
            if (Game() is null) throw new InvalidOperationException("the game isn't running");
            if (File.Exists(tapLog))
            {
                using var s = new FileStream(tapLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (new StreamReader(s).ReadToEnd().Contains("dump (level+5s) end", StringComparison.Ordinal))
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} level up after {clock.Elapsed.TotalSeconds:F0} s");
                    return;
                }
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException("no level after 300 s (the tap's dump (level+5s) line)");
    }

    /// <summary>
    /// A setup step: a console line, <c>{"wait": seconds}</c>, or <c>{"approach": "npc_orc_sword", "distance": 60}</c>,
    /// which finds the nearest living entity of that class on the Wire and puts the player that many units in front of
    /// it, facing it (spawned NPCs land where the view pointed, which isn't always in reach); <c>"height"</c> puts it
    /// that many units above the NPC's feet (default 4), to drop from, and <c>"pitch"</c> looks that many degrees down.
    /// </summary>
    static void Setup(TapControl control, JsonNode step, string wirePath)
    {
        if (step is JsonObject o && o["approach"] is { } target)
        {
            var (setpos, setang) = Approach(wirePath, (string)target!, (float?)o["distance"] ?? 60, (float?)o["height"] ?? 4, (float?)o["pitch"] ?? 0);
            Setup(control, setpos, wirePath);
            Setup(control, setang, wirePath);
            return;
        }
        if (step is JsonValue v && v.TryGetValue(out string? line))
        {
            if (Game() is null) throw new InvalidOperationException("the game is gone");
            control.Command(line);
            Console.WriteLine($"  exec {line}");
            Thread.Sleep(300);
        }
        else if (step["wait"] is { } wait) Thread.Sleep(TimeSpan.FromSeconds((double)wait));
        else throw new InvalidDataException($"setup step {step.ToJsonString()}: a console line or {{\"wait\": s}}");
    }

    /// <summary>
    /// The scripted input, through the playtest agent's desktop lease: <c>{"click": button, "ms": n}</c>,
    /// <c>{"hold": ["F", "W"], "ms": n}</c> (keys by letter, digit or name, or virtual-key code), <c>{"wait": s}</c>,
    /// <c>{"point": [x, y], "of": [width, height]}</c> (the cursor to a place in a desktop capture of that size, for the
    /// game's menus: the character sheet's skill icons),
    /// <c>{"repeat": n, "steps": [...]}</c>, <c>{"approach": ...}</c> as in the setup, and console lines. A key held
    /// across a click goes as a console line (<c>+forward</c>, <c>+jump</c>, <c>+sprint</c>, then <c>-forward</c>...):
    /// the agent plays its steps one after another.
    /// </summary>
    static void Play(JsonArray play, TapControl control, string agent, double seconds, string wirePath)
    {
        using var http = new HttpClient { BaseAddress = new Uri(agent), Timeout = TimeSpan.FromSeconds(30) };
        var lease = http.PostAsJsonAsync("/v1/lease", new { holder = "rbwire-scenario", instance = "", ttl_ms = (int)Math.Min(600_000, seconds * 1000 + 30_000) })
            .Result.Content.ReadFromJsonAsync<Dictionary<string, object>>().Result;
        if (lease is null || !lease.TryGetValue("id", out var id)) { Console.WriteLine($"no lease: {string.Join(", ", lease ?? new())}; recording without input"); return; }
        var clock = Stopwatch.StartNew();
        try
        {
            if (!InFront()) Console.WriteLine("the game isn't in front; input stops");
            else Steps(play);
        }
        catch (Stop stop) { Console.WriteLine($"input stopped: {stop.Message}"); }
        finally { http.DeleteAsync($"/v1/lease/{id}").Wait(); }

        void Steps(JsonArray steps)
        {
            foreach (var step in steps)
            {
                if (clock.Elapsed.TotalSeconds > seconds) throw new Stop("the recording is over");
                if (step is JsonValue v && v.TryGetValue(out string? line)) { Guard(); control.Command(line); Console.WriteLine($"  exec {line}"); }
                else if (step!["wait"] is { } wait) Thread.Sleep(TimeSpan.FromSeconds((double)wait));
                else if (step["approach"] is not null)
                {
                    Guard();
                    // No one left to approach ends the input, not the recording.
                    try { Setup(control, step, wirePath); }
                    catch (InvalidOperationException e) { throw new Stop(e.Message); }
                }
                else if (step["repeat"] is { } n) for (int i = 0; i < (int)n; i++) Steps(step["steps"]!.AsArray());
                else if (step["click"] is { } button) Input(new { kind = "click", button = (int)button, ms = (int?)step["ms"] ?? 100 });
                else if (step["hold"] is JsonArray keys) Input(new { kind = "hold", keys = keys.Select(k => Key(k!)).ToArray(), ms = (int?)step["ms"] ?? 150 });
                else if (step["point"] is JsonArray at && step["of"] is JsonArray of)
                    Input(new { kind = "point", x = (int)at[0]!, y = (int)at[1]!, width = (int)of[0]!, height = (int)of[1]!, ms = (int?)step["ms"] ?? 100 });
                else throw new InvalidDataException($"play step {step.ToJsonString()}");
            }
        }

        void Input(object step)
        {
            Guard();
            var reply = http.PostAsJsonAsync($"/v1/lease/{id}/input", new { steps = new[] { step } }).Result;
            if (!reply.IsSuccessStatusCode) throw new Stop($"the agent answered {(int)reply.StatusCode}");
        }
    }

    /// <summary>Input only while the game runs and has the foreground: otherwise it would land in another window.</summary>
    static void Guard()
    {
        if (Game() is null) throw new Stop("the game is gone");
        if (!InFront()) throw new Stop("the game isn't in front");
    }

    sealed class Stop(string message) : Exception(message);

    static int Key(JsonNode key) => key is JsonValue v && v.TryGetValue(out int code) ? code : ((string)key!).ToUpperInvariant() switch
    {
        var s when s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) => s[0],
        "SPACE" => 0x20, "SHIFT" => 0x10, "CTRL" => 0x11, "ALT" => 0x12, "ESC" => 0x1B, "ENTER" => 0x0D, "TAB" => 0x09,
        var s => throw new InvalidDataException($"key {s}: a letter, digit, SPACE, SHIFT, CTRL, ALT, ESC, ENTER, TAB or a virtual-key code"),
    };

    /// <summary>The game's window in front: minimise Steam's, restore the game's, and take the foreground (an Alt press gets past Windows' foreground lock).</summary>
    static bool InFront()
    {
        if (!OperatingSystem.IsWindows() || Game() is not { } game) return false;
        IntPtr window = game.MainWindowHandle;
        if (window == IntPtr.Zero) return false;
        if (GetForegroundWindow() == window) return true;
        foreach (var steam in Process.GetProcessesByName("steamwebhelper").Concat(Process.GetProcessesByName("steam")))
            if (steam.MainWindowHandle != IntPtr.Zero) ShowWindowAsync(steam.MainWindowHandle, 6);
        ShowWindowAsync(window, 9);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(window);
        Thread.Sleep(300);
        return GetForegroundWindow() == window;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Writes to the console and the scenario's log.</summary>
    sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        public override System.Text.Encoding Encoding => a.Encoding;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
