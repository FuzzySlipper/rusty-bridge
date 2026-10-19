using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Npc;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Replay;

/// <summary>
/// One recorded tick as the lab replays it, in lab space: the player's held buttons and kick press, where it
/// looks from and stands, and the orc it faces.
/// </summary>
/// <param name="Seconds">Game time since the previous tick: the step the lab takes.</param>
/// <param name="Kick">
/// A kick was asked for this tick. From Wire v14 that is the user command's kick action going down (Arkane's byte
/// +0x97), whether or not the donor then kicked; before v14 the press isn't on the Wire, so it is the kick clip
/// starting on the view model (docs/wire.md, "The player's input").
/// </param>
/// <param name="Move">v14: the command's movement, side and forward, as a unit direction (zero when none).</param>
/// <param name="Jump">v14: jump went down in this tick's commands.</param>
/// <param name="Use">v14: use went down in them.</param>
internal sealed record ReplayTick(int Tick, double Time, float Seconds, InputButtons Held, bool Kick, Vector3 Eye, Vector3 Forward,
    Vector3 Feet, OrcPose? Orc, Vector2 Move = default, bool Jump = false, bool Use = false);

/// <summary>A move the donor's player began: its first-person clip starting over on the view model.</summary>
internal sealed record DonorSwing(int Tick, double Time, string Clip);

/// <summary>
/// A recording read for replay (Wire v12 and later, which carry the player's input): the ticks with input, the
/// moves the donor played, and the fight's decisions (<see cref="Blows"/>) to compare the lab's with.
/// </summary>
internal sealed class InputTimeline
{
    internal const string OrcClass = "npc_orc_sword";
    /// <summary>The orc the lab follows is the one nearest the player's aim within this far, metres; else the nearest.</summary>
    const float FacingRange = 4;
    /// <summary>Dark Messiah's kick damage bit (docs/combat.md).</summary>
    const uint KickDamage = 0x40000;

    internal string Name { get; private init; } = "";
    internal IReadOnlyList<ReplayTick> Ticks { get; private init; } = [];
    internal IReadOnlyList<DonorSwing> Swings { get; private init; } = [];
    internal FightCases Fight { get; private init; } = new("", 0, [], [], []);
    /// <summary>
    /// The player's kicks that landed on an NPC: the tick, the victim (<c>#index.serial</c>, as <see cref="Blows"/>
    /// names it) and the damage asked. <see cref="Blows"/> keeps slash and club blows only.
    /// </summary>
    internal IReadOnlyList<(int Tick, string Victim, string Class, float Damage)> Kicks { get; private init; } = [];
    /// <summary>The recording carries the player's user commands (Wire v14): kicks are requests, and movement, jump and use are known.</summary>
    internal bool HasCommands { get; private init; }
    /// <summary>Recorded frames without input (older Wire, or no player): not replayed.</summary>
    internal int Skipped { get; private init; }

    /// <param name="moves">The first-person clips whose starts count as the donor's moves (fast, power, kick).</param>
    /// <param name="kick">The kick's first-person clip.</param>
    internal static InputTimeline Read(string path, IReadOnlyCollection<string> moves, string kick)
    {
        var ticks = new List<ReplayTick>();
        var swings = new List<DonorSwing>();
        var kicks = new List<(int, string, string, float)>();
        var playing = new Dictionary<string, float>();
        var streamed = new Dictionary<string, (short Sequence, float Cycle)>();
        double lastTime = double.NaN;
        int skipped = 0;
        bool commands = false;
        var lastCommand = (Buttons: InputButtons.None, Actions: PlayerActions.None);
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                foreach (var d in f.Damage)
                    // A kick's push is a call of its own, zero damage and untraced: not the kick's blow.
                    if ((d.DamageType & KickDamage) != 0 && d.TraceValid != 0 && f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == "player"
                        && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) is { } cls && cls.StartsWith("npc_", StringComparison.Ordinal))
                        kicks.Add((d.Tick, $"#{d.Victim}.{d.VictimSerial:x}", cls, d.Damage));
                var view = f.ClientView;
                if (f.Version < 12 || f.Input.Valid == 0 || !view.Flags.HasFlag(ClientViewFlags.Player) || !view.Flags.HasFlag(ClientViewFlags.ViewAngles))
                {
                    skipped++;
                    lastTime = double.NaN;
                    continue;
                }
                // The donor's moves: a first-person clip appearing, or its cycle starting over.
                bool kicked = false;
                var vm = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");
                var now = new Dictionary<string, float>();
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is { } names)
                    foreach (var l in view.ViewmodelLayers)
                        if (l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length && moves.Contains(names[l.Sequence]))
                            now[names[l.Sequence]] = l.Cycle;
                foreach (var (clip, cycle) in now)
                    if (!playing.TryGetValue(clip, out float was) || cycle < was - .05f)
                    {
                        swings.Add(new DonorSwing(f.Tick, f.Curtime, clip));
                        kicked |= clip == kick;
                    }
                playing = now;

                var feet = SourceSpace.Position(new Vector3(view.OriginX, view.OriginY, view.OriginZ));
                var eye = SourceSpace.Position(new Vector3(view.OriginX + view.ViewOffsetX, view.OriginY + view.ViewOffsetY, view.OriginZ + view.ViewOffsetZ));
                var forward = SourceSpace.Direction(SourceForward(view.ViewPitch, view.ViewYaw));
                float seconds = double.IsNaN(lastTime) ? f.TickInterval : (float)Math.Clamp(f.Curtime - lastTime, 0, .1);
                lastTime = f.Curtime;
                // From v14 the user commands say what was asked, before the game decided: a kick request, movement,
                // jump and use. A tick without commands holds the last ones.
                var held = f.Input.Buttons;
                Vector2 move = default;
                bool jump = false, use = false;
                if (f.Version >= 14)
                {
                    commands = true;
                    var now2 = f.Input.Commands > 0 ? (f.Input.CommandButtons, f.Input.Actions) : lastCommand;
                    held = now2.Item1;
                    kicked = now2.Item2.HasFlag(PlayerActions.Kick) && !lastCommand.Actions.HasFlag(PlayerActions.Kick);
                    jump = now2.Item1.HasFlag(InputButtons.Jump) && !lastCommand.Buttons.HasFlag(InputButtons.Jump);
                    use = now2.Item1.HasFlag(InputButtons.Use) && !lastCommand.Buttons.HasFlag(InputButtons.Use);
                    lastCommand = now2;
                    var m = new Vector2(f.Input.SideMove, f.Input.ForwardMove);
                    move = m.LengthSquared() > 1 ? Vector2.Normalize(m) : default;
                }
                ticks.Add(new ReplayTick(f.Tick, f.Curtime, seconds, held, kicked, eye, forward, feet, Orc(f, eye, forward, streamed), move, jump, use));
            }
        FightCases fight;
        using (var stream = File.OpenRead(path)) fight = Blows.Read(Path.GetFileName(path), stream);
        return new InputTimeline { Name = Path.GetFileName(path), Ticks = ticks, Swings = swings, Fight = fight, Kicks = kicks, Skipped = skipped, HasCommands = commands };
    }

    /// <summary>Source's AngleVectors forward for pitch and yaw in degrees (pitch down is positive).</summary>
    static Vector3 SourceForward(float pitch, float yaw)
    {
        float p = pitch * MathF.PI / 180, y = yaw * MathF.PI / 180;
        return new Vector3(MathF.Cos(p) * MathF.Cos(y), MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p));
    }

    /// <summary>The living orc the player aims nearest within <see cref="FacingRange"/>, else the nearest; null when none is alive.</summary>
    static OrcPose? Orc(RecordedFrame f, Vector3 eye, Vector3 forward, Dictionary<string, (short, float)> streamed)
    {
        OrcPose? best = null;
        float bestScore = float.MaxValue;
        foreach (var (index, e) in f.Entities)
        {
            if (f.Classes.GetValueOrDefault(e.ClassId) != OrcClass || !e.Flags.HasFlag(WireEntityFlags.HasAnimation)
                || e.Flags.HasFlag(WireEntityFlags.HasHealth) && (e.LifeState != 0 || e.Health <= 0)) continue;
            var feet = SourceSpace.Position(new Vector3(e.OriginX, e.OriginY, e.OriginZ));
            Vector3 to = (feet - eye) with { Y = 0 };
            float distance = to.Length();
            Vector3 flat = Vector3.Normalize(forward with { Y = 0 });
            float aim = distance > 1e-3f ? Vector3.Dot(flat, to / distance) : 1;
            // In range, aim decides (0 dead ahead); beyond it, distance does.
            float score = distance <= FacingRange ? 1 - aim : 2 + distance;
            if (score >= bestScore) continue;
            string id = $"#{index}.{e.Serial:x}";
            var names = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
            string Name(int s) => s >= 0 && s < names.Length ? names[s] : "";
            var layers = f.Layers.Where(l => l.Index == index && l.Weight > 0 && Name(l.Sequence).Length > 0).OrderBy(l => l.Order).ToList();
            // The lab's orc plays one layer: a parry, else an attack on the move (layer_attack_*), else the first.
            var layer = layers.FirstOrDefault(l => Name(l.Sequence).Contains("parry", StringComparison.OrdinalIgnoreCase));
            if (layer.Weight <= 0) layer = layers.FirstOrDefault(l => Name(l.Sequence).StartsWith("layer_attack", StringComparison.OrdinalIgnoreCase));
            if (layer.Weight <= 0 && layers.Count > 0) layer = layers[0];
            bool fresh = !streamed.TryGetValue(id, out var last) || last != (e.Sequence, e.Cycle);
            float yaw = e.Yaw * MathF.PI / 180;
            best = new OrcPose(id, feet, SourceSpace.Direction(new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0)),
                e.Flags.HasFlag(WireEntityFlags.HasHealth) ? e.Health : float.MaxValue, Name(e.Sequence), e.Cycle,
                e.PlaybackRate, layer.Weight > 0 ? Name(layer.Sequence) : null, layer.Cycle, fresh);
            bestScore = score;
        }
        // Every orc's last streamed state, so freshness holds whichever one is followed.
        foreach (var (index, e) in f.Entities)
            if (f.Classes.GetValueOrDefault(e.ClassId) == OrcClass) streamed[$"#{index}.{e.Serial:x}"] = (e.Sequence, e.Cycle);
        return best;
    }
}
