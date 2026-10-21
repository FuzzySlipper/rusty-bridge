using System.Numerics;
using DarkMessiah.Combat;
using DarkMessiah.Lab.Npc;
using DarkMessiah.Lab.Player;
using DarkMessiah.Lab.Sword;
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
/// <param name="KickHeld">The kick button is down: from v14 the command's kick action, before it the same tick as <see cref="Kick"/>.</param>
/// <param name="Sprint">v14: the command's sprint action (Arkane's byte +0x95) with movement.</param>
/// <param name="Armed">The donor's player has a weapon in hand: its view model plays a layer other than the quick
/// draw (v9 and later; true without layers). With none in hand the donor ignores attack, block and kick.</param>
/// <param name="Adrenaline">From v7, the donor player's adrenaline this tick (and its maximum): the replay holds the
/// lab's gauge to it, since the donor also fills it on blows the lab doesn't follow.</param>
internal sealed record ReplayTick(int Tick, double Time, float Seconds, InputButtons Held, bool Kick, Vector3 Eye, Vector3 Forward,
    Vector3 Feet, OrcPose? Orc, Vector2 Move = default, bool Jump = false, bool Use = false, bool KickHeld = false, bool Sprint = false,
    bool Armed = true, float? Adrenaline = null, float AdrenalineMax = 0);

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
    /// <summary>The command's movement for a full run: what the lab's movement input of length 1 stands for.</summary>
    internal const float RunMove = 1000;
    /// <summary>Dark Messiah's kick damage bit (docs/combat.md).</summary>
    const uint KickDamage = 0x40000;

    internal string Name { get; init; } = "";
    internal IReadOnlyList<ReplayTick> Ticks { get; init; } = [];
    internal IReadOnlyList<DonorSwing> Swings { get; init; } = [];
    internal FightCases Fight { get; private init; } = new("", 0, [], [], []);
    /// <summary>
    /// The player's kicks that landed on an NPC: the tick, the victim (<c>#index.serial</c>, as <see cref="Blows"/>
    /// names it) and the damage asked. <see cref="Blows"/> keeps slash and club blows only.
    /// </summary>
    internal IReadOnlyList<(int Tick, string Victim, string Class, float Damage)> Kicks { get; private init; } = [];
    /// <summary>
    /// From Wire v15, each blow an orc landed on the player's shield, and its damage (#10149): a blocked blow wears the
    /// shield by what it asks, and leaves the player untouched.
    /// </summary>
    internal IReadOnlyList<(int Tick, float Damage)> ShieldWear { get; private init; } = [];
    /// <summary>The recording carries the player's user commands (Wire v14): kicks are requests, and movement, jump and use are known.</summary>
    internal bool HasCommands { get; init; }
    /// <summary>
    /// From Wire v15, the donor player's build at the first frame with stats (<see cref="CharacterBuild.FromRecording"/>);
    /// null before v15. <see cref="Builds"/> holds each later change of its modules, shield or (v16) skill levels, which
    /// the replay applies at its tick; the weapon's changes are <see cref="WeaponChanges"/>.
    /// </summary>
    internal CharacterBuild? Build { get; private init; }
    /// <summary>The build as it changed later in the recording, from the tick each change was first streamed.</summary>
    internal IReadOnlyList<(int Tick, CharacterBuild Build)> Builds { get; private init; } = [];
    internal int BuildChanges => Builds.Count;
    /// <summary>
    /// The stretches where the donor's player held something the lab doesn't play (a weapon of no
    /// class the lab has, such as a bow or a spell), with what: the replay compares no move or blow in them.
    /// </summary>
    internal IReadOnlyList<(int From, int To, string What)> Unplayable { get; init; } = [];
    /// <summary>
    /// From Wire v15, each switch to another weapon (#10148): the model taken, and the tick the donor began putting the
    /// last one away (its <c>_undraw</c> starting; the weapon on the Wire changes only as the draw begins). Hands emptied
    /// and filled again with the same weapon (an orc's grab) aren't switches.
    /// </summary>
    internal IReadOnlyList<(int Tick, string Model)> WeaponChanges { get; private init; } = [];
    /// <summary>Recorded frames without input (older Wire, or no player): not replayed.</summary>
    internal int Skipped { get; private init; }

    /// <summary>
    /// The move family a first-person clip plays: Dark Messiah's fast attacks (A to E), its power attacks' releases (the
    /// neutral one and the directional T, B, L and R ones), its kicks, the jump attack's strike (<c>combo_*_fall_*_out</c>)
    /// the rush attack (<c>combo_*_rush_*</c>) and the Fatality (<c>combo_*_huge_*</c>, <c>behead_*_out</c>) and the Whirlwind (<c>combo_*_trample_*</c>),
    /// the daggers' backstab (<c>backstab_out</c>), the quick draw (<c>*_quickdraw</c>), and a weapon put away
    /// (<c>*_undraw</c>) or drawn (<c>*_draw</c>). Null for anything else (charges,
    /// idles, parries, movement).
    /// </summary>
    internal static string? Family(string clip) =>
        clip.Contains("fastattack", StringComparison.Ordinal) && !clip.Contains("_end", StringComparison.Ordinal) ? "fast attack"
        : clip.Contains("bigslash", StringComparison.Ordinal) && clip.EndsWith("_out", StringComparison.Ordinal) ? "power attack"
        : clip.Contains("kick", StringComparison.Ordinal) ? "kick"
        : clip.Contains("_fall_", StringComparison.Ordinal) && clip.EndsWith("_out", StringComparison.Ordinal) ? "jump attack"
        : clip.Contains("combo_", StringComparison.Ordinal) && clip.Contains("_rush", StringComparison.Ordinal) ? "rush attack"
        : clip.Contains("combo_", StringComparison.Ordinal) && clip.Contains("_huge", StringComparison.Ordinal) || clip.Contains("behead", StringComparison.Ordinal) && clip.EndsWith("_out", StringComparison.Ordinal) ? "fatality"
        : clip.Contains("combo_", StringComparison.Ordinal) && clip.Contains("_trample", StringComparison.Ordinal) ? "whirlwind"
        : clip.Contains("backstab_out", StringComparison.Ordinal) ? "backstab"
        : clip.EndsWith("_bash", StringComparison.Ordinal) ? "bash"
        : clip.EndsWith("_quickdraw", StringComparison.Ordinal) ? "quick draw"
        : clip.EndsWith("_undraw", StringComparison.Ordinal) ? "put away"
        : clip.EndsWith("_draw", StringComparison.Ordinal) ? "draw"
        : null;

    /// <param name="moves">The lab's first-person clips (fast, power, kick); the donor's moves are their families' clips, variants included.</param>
    /// <param name="kick">The kick's first-person clip.</param>
    internal static InputTimeline Read(string path, IReadOnlyCollection<string> moves, string kick)
    {
        var ticks = new List<ReplayTick>();
        var swings = new List<DonorSwing>();
        var kicks = new List<(int, string, string, float)>();
        var shieldWear = new List<(int, float)>();
        var playing = new Dictionary<string, float>();
        bool seeded = false;
        var streamed = new Dictionary<string, (short Sequence, float Cycle)>();
        double lastTime = double.NaN;
        int skipped = 0;
        bool commands = false;
        CharacterBuild? build = null;
        (SkillModules, string?, string) lastKit = default;
        var builds = new List<(int, CharacterBuild)>();
        var unplayable = new List<(int From, int To, string What)>();
        var weaponChanges = new List<(int, string)>();
        string? lastWeapon = null;
        var lastCommand = (Buttons: InputButtons.None, Actions: PlayerActions.None);
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                foreach (var d in f.Damage)
                    // A kick's push is a call of its own, zero damage and untraced: not the kick's blow.
                    if ((d.DamageType & KickDamage) != 0 && d.TraceValid != 0 && f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == "player"
                        && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) is { } cls && cls.StartsWith("npc_", StringComparison.Ordinal))
                        kicks.Add((d.Tick, $"#{d.Victim}.{d.VictimSerial:x}", cls, d.Damage));
                if (f.Version >= 15 && f.Stats.Valid != 0)
                {
                    foreach (var d in f.Damage)
                        if (d.Victim == f.Stats.Shield && f.Entities.TryGetValue(d.Attacker, out var striker) && f.Classes.GetValueOrDefault(striker.ClassId) == OrcClass)
                            shieldWear.Add((d.Tick, d.Damage));
                    string? Model(ushort index) => index != WireLayout.NoEntity && f.Entities.TryGetValue(index, out var e) ? f.Models.GetValueOrDefault(e.ModelIndex) : null;
                    string? weapon = Model(f.Stats.Weapon);
                    var kit = (f.Stats.Modules, Model(f.Stats.Shield),
                        f.Version >= 16 ? string.Join(",", SkillNames.ByIndex.Select(n => f.Stats.SkillLevel(n))) : "");
                    if (build is null) build = CharacterBuild.FromRecording(f.Stats, f.Version, weapon, kit.Item2, Path.GetFileName(path));
                    else if (kit != lastKit)
                        builds.Add((f.Tick, CharacterBuild.FromRecording(f.Stats, f.Version, weapon, kit.Item2, $"{Path.GetFileName(path)} at tick {f.Tick}")));
                    lastKit = kit;
                    string? what = weapon is not null && LabWeapon.ClassOf(weapon) is null ? $"{VpkIndex.Normalize(weapon)}, a weapon the lab doesn't play" : null;
                    if (what is not null)
                    {
                        if (unplayable.Count > 0 && unplayable[^1].What == what && f.Tick - unplayable[^1].To <= 8) unplayable[^1] = unplayable[^1] with { To = f.Tick };
                        else unplayable.Add((f.Tick, f.Tick, what));
                    }
                    if (weapon is not null && lastWeapon is not null && weapon != lastWeapon)
                    {
                        // From the put-away that began within the last second, else now.
                        var away = swings.LastOrDefault(s => Family(s.Clip) == "put away" && s.Tick <= f.Tick && f.Tick - s.Tick < 1 / f.TickInterval);
                        weaponChanges.Add((away?.Tick ?? f.Tick, weapon));
                    }
                    if (weapon is not null) lastWeapon = weapon;
                }
                var view = f.ClientView;
                if (f.Version < 12 || f.Input.Valid == 0 || !view.Flags.HasFlag(ClientViewFlags.Player) || !view.Flags.HasFlag(ClientViewFlags.ViewAngles))
                {
                    skipped++;
                    lastTime = double.NaN;
                    continue;
                }
                // The donor's moves: a first-person clip appearing, or its cycle starting over. Of two layers playing one
                // clip (a kick starting while the last one fades), the youngest; on the first frame read, clips already
                // under way began before the recording and aren't counted.
                bool kicked = false;
                var vm = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");
                var now = new Dictionary<string, float>();
                bool armed = true;
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is { } layerNames)
                    armed = view.ViewmodelLayers.ToArray().Any(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < layerNames.Length
                        && !layerNames[l.Sequence].Contains("quickdraw", StringComparison.Ordinal));
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is { } names)
                    foreach (var l in view.ViewmodelLayers)
                        if (l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length && Family(names[l.Sequence]) is not null)
                            now[names[l.Sequence]] = MathF.Min(l.Cycle, now.GetValueOrDefault(names[l.Sequence], float.PositiveInfinity));
                foreach (var (clip, cycle) in now)
                    if (seeded && (!playing.TryGetValue(clip, out float was) || cycle < was - .05f))
                    {
                        swings.Add(new DonorSwing(f.Tick, f.Curtime, clip));
                        kicked |= Family(clip) == "kick";
                    }
                playing = now;
                seeded = true;

                var feet = SourceSpace.Position(new Vector3(view.OriginX, view.OriginY, view.OriginZ));
                var eye = SourceSpace.Position(new Vector3(view.OriginX + view.ViewOffsetX, view.OriginY + view.ViewOffsetY, view.OriginZ + view.ViewOffsetZ));
                var forward = SourceSpace.Direction(SourceForward(view.ViewPitch, view.ViewYaw));
                float seconds = double.IsNaN(lastTime) ? f.TickInterval : (float)Math.Clamp(f.Curtime - lastTime, 0, .1);
                lastTime = f.Curtime;
                // From v14 the user commands say what was asked, before the game decided: a kick request, movement,
                // jump and use. A tick without commands holds the last ones.
                var held = f.Input.Buttons;
                Vector2 move = default;
                bool jump = false, use = false, kickHeld = kicked, sprint = false;
                if (f.Version >= 14)
                {
                    commands = true;
                    var now2 = f.Input.Commands > 0 ? (f.Input.CommandButtons, f.Input.Actions) : lastCommand;
                    held = now2.Item1;
                    kicked = now2.Item2.HasFlag(PlayerActions.Kick) && !lastCommand.Actions.HasFlag(PlayerActions.Kick);
                    jump = now2.Item1.HasFlag(InputButtons.Jump) && !lastCommand.Buttons.HasFlag(InputButtons.Jump);
                    use = now2.Item1.HasFlag(InputButtons.Use) && !lastCommand.Buttons.HasFlag(InputButtons.Use);
                    lastCommand = now2;
                    // A forwardmove of 1000 is a run; 150 the slow walk the game sends while charging or guarding.
                    var m = new Vector2(f.Input.SideMove, f.Input.ForwardMove) / RunMove;
                    move = m.LengthSquared() > 1 ? Vector2.Normalize(m) : m;
                    kickHeld = now2.Item2.HasFlag(PlayerActions.Kick);
                    sprint = now2.Item2.HasFlag(PlayerActions.Sprint) && move != Vector2.Zero;
                }
                ticks.Add(new ReplayTick(f.Tick, f.Curtime, seconds, held, kicked, eye, forward, feet, Orc(f, eye, forward, streamed), move, jump, use, kickHeld, sprint, armed,
                    f.Version >= 7 && f.Player.Index != WireLayout.NoEntity ? f.Player.Adrenaline : null, f.Player.MaxAdrenaline));
            }
        FightCases fight;
        using (var stream = File.OpenRead(path)) fight = Blows.Read(Path.GetFileName(path), stream);
        return new InputTimeline { Name = Path.GetFileName(path), Ticks = ticks, Swings = swings, Fight = fight, Kicks = kicks, ShieldWear = shieldWear, Skipped = skipped, HasCommands = commands,
            Build = build, Builds = builds, Unplayable = unplayable, WeaponChanges = weaponChanges };
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
                e.PlaybackRate, layer.Weight > 0 ? Name(layer.Sequence) : null, layer.Cycle, fresh,
                f.Version >= 10 ? f.Entities.Values.Where(i => i.Owner == index && f.Classes.GetValueOrDefault(i.ClassId) != "prop_ragdoll")
                    .Select(i => f.Models.GetValueOrDefault(i.ModelIndex, "")).Where(m => m.Length > 0).ToList() : null,
                f.Models.GetValueOrDefault(e.ModelIndex), [.. layers.Select(l => (Name(l.Sequence), l.Cycle, l.Weight))]);
            bestScore = score;
        }
        // Every orc's last streamed state, so freshness holds whichever one is followed.
        foreach (var (index, e) in f.Entities)
            if (f.Classes.GetValueOrDefault(e.ClassId) == OrcClass) streamed[$"#{index}.{e.Serial:x}"] = (e.Sequence, e.Cycle);
        return best;
    }
}
