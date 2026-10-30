using System.Numerics;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// A recorded necromancer's flame arrow (#10357): the cast it was (1 the first of that necromancer's life), how far off the
/// player was, its cruise speed (units/s, from just after the hand let it go to its last sample), how far aside of the
/// player its line passed, whether the player's shield turned it, and what it asked of whom it struck (0: nothing).
/// </summary>
internal sealed record RecordedFlame(string Recording, int Tick, int Cast, float Distance, float Cruise, float Aside, bool Turned, string Struck, float Asked);

/// <summary>A recorded necromancer's blow with its hook (#10357): what it asked.</summary>
internal sealed record RecordedHookBlow(string Recording, int Tick, float Asked);

/// <summary>
/// What the recordings say of the necromancer (#10357; docs/lab.md, "The necromancer"): its standing casts' spacing, its
/// flame arrows (cruise speed, aim, what they struck, turned or not) and its hook's blows. Read from
/// <c>local/necromancer</c> (<c>necromancer</c>, <c>necromancer-l05</c>, <c>-l05b</c>) and <c>reflect-probe-2</c>. NPCs and
/// missiles are followed by index and serial; a recording ends what it can't read.
/// </summary>
internal static class NecromancerCalibration
{
    const string Necromancer = "npc_necromancer", Missile = "arxspellmissileshot";
    const int SpellFireDamage = 0x48, HookDamage = 0x40004;

    static IEnumerable<RecordedFrame> Frames(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = RecordingReader.Frames(stream).GetEnumerator();
        while (true)
        {
            try { if (!frames.MoveNext()) yield break; }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException or InvalidDataException) { yield break; }
            yield return frames.Current;
        }
    }

    static string SequenceOf(RecordedFrame f, WireEntity e) =>
        f.Sequences.GetValueOrDefault(e.ModelIndex) is { } names && e.Sequence >= 0 && e.Sequence < names.Length ? names[e.Sequence] : "";

    static Vector3 At(WireEntity e) => new(e.OriginX, e.OriginY, e.OriginZ);

    /// <summary>The seconds between one standing <c>spell_flamearrow</c>'s start and the next's, the player still in range.</summary>
    internal static List<float> StandingGaps(string path)
    {
        var gaps = new List<float>();
        var last = new Dictionary<(ushort, uint), (int Tick, string Seq)>();
        var starts = new Dictionary<(ushort, uint), int>();
        foreach (var f in Frames(path))
            foreach (var (index, e) in f.Entities)
            {
                if (f.Classes.GetValueOrDefault(e.ClassId) != Necromancer) continue;
                string seq = SequenceOf(f, e);
                var key = (index, e.Serial);
                if (last.TryGetValue(key, out var was) && was.Seq == seq) continue;
                if (seq == "spell_flamearrow")
                {
                    // Standing: its last cast ended in combatidle, and it began again.
                    if (last.TryGetValue(key, out var prior) && prior.Seq == "combatidle" && starts.TryGetValue(key, out int began) && f.Tick - began < 200)
                        gaps.Add((f.Tick - began) * f.TickInterval);
                    starts[key] = f.Tick;
                }
                last[key] = (f.Tick, seq);
            }
        return gaps;
    }

    /// <summary>
    /// Every flame arrow of a necromancer that flew: its cast in its caster's life, the player's distance, its cruise
    /// speed, how far aside of the player's eye its line passed (2D), whether it turned back (the player's shield), and
    /// the fire damage that ended it (on the player, an NPC, or the world).
    /// </summary>
    internal static List<RecordedFlame> Flames(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var flames = new List<RecordedFlame>();
        var tracks = new Dictionary<(ushort, uint), List<(int Tick, Vector3 At, Vector3 Feet, Vector3 Eye, Vector3 Caster)>>();
        var lives = new Dictionary<uint, int>();
        var castOf = new Dictionary<(ushort, uint), int>();
        var damage = new List<(int Tick, string Victim, float Asked)>();
        float tickSeconds = .015f;
        foreach (var f in Frames(path))
        {
            tickSeconds = f.TickInterval > 0 ? f.TickInterval : tickSeconds;
            var me = f.Entities.TryGetValue(f.Player.Index, out var p) ? p : default;
            var feet = At(me);
            var eye = new Vector3(f.ClientView.OriginX, f.ClientView.OriginY, f.ClientView.OriginZ + f.ClientView.ViewOffsetZ);
            (Vector3 At, uint Serial)? caster = null;
            foreach (var e in f.Entities.Values)
                if (f.Classes.GetValueOrDefault(e.ClassId) == Necromancer && e.LifeState == 0) caster = (At(e), e.Serial);
            foreach (var (index, e) in f.Entities)
            {
                if (f.Classes.GetValueOrDefault(e.ClassId) != Missile || caster is not { } c) continue;
                var key = (index, e.Serial);
                if (!tracks.TryGetValue(key, out var track))
                {
                    tracks[key] = track = [];
                    castOf[key] = lives[c.Serial] = lives.GetValueOrDefault(c.Serial) + 1;
                }
                if (track.Count == 0 || track[^1].At != At(e)) track.Add((f.Tick, At(e), feet, eye, c.At));
            }
            foreach (var d in f.Damage)
                if (f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == Necromancer && d.DamageType == SpellFireDamage && d.Damage >= 1)
                    damage.Add((f.Tick, f.Entities.TryGetValue(d.Victim, out var v) ? f.Classes.GetValueOrDefault(v.ClassId) ?? "?" : "?", d.Damage));
        }
        foreach (var (key, l) in tracks)
        {
            float Speed(int a) => (l[a + 1].At - l[a].At).Length() / ((l[a + 1].Tick - l[a].Tick) * tickSeconds);
            // Away from the hand: the hand's own motion with it held reaches 405 units/s; its flight's samples 499 and more.
            int launch = Enumerable.Range(0, Math.Max(0, l.Count - 3)).FirstOrDefault(a => Speed(a) > 450 && Speed(a + 1) > 450 && Speed(a + 2) > 450, -1);
            if (launch < 0) continue;
            var s = l[launch];
            var heading = Vector3.Normalize(l[Math.Min(launch + 2, l.Count - 1)].At - s.At);
            // Turned: it came back toward its caster.
            int turn = Enumerable.Range(launch + 2, Math.Max(0, l.Count - 1 - (launch + 2))).FirstOrDefault(a => Vector3.Dot(l[a + 1].At - l[a].At, heading) < 0, -1);
            int last = turn > 0 ? turn : l.Count - 1, cruise0 = Math.Min(launch + 2, last);
            float cruise = last > cruise0 ? (l[last].At - l[cruise0].At).Length() / ((l[last].Tick - l[cruise0].Tick) * tickSeconds) : float.NaN;
            var flat = Vector2.Normalize(new Vector2(heading.X, heading.Y));
            var toEye = new Vector2(s.Eye.X - s.At.X, s.Eye.Y - s.At.Y);
            float aside = MathF.Abs(toEye.X * flat.Y - toEye.Y * flat.X);
            var end = l[^1];
            var hit = damage.Where(d => Math.Abs(d.Tick - end.Tick) <= 4).OrderBy(d => Math.Abs(d.Tick - end.Tick)).FirstOrDefault();
            float distance = Vector2.Distance(new Vector2(s.Feet.X, s.Feet.Y), new Vector2(s.Caster.X, s.Caster.Y));
            flames.Add(new RecordedFlame(name, s.Tick, castOf[key], distance, cruise, aside, turn > 0, hit.Victim ?? "nothing", hit.Victim is null ? 0 : hit.Asked));
        }
        return flames.OrderBy(x => x.Tick).ToList();
    }

    /// <summary>Every blow of a necromancer's hook (damage type 0x40004) on the player: what it asked.</summary>
    internal static List<RecordedHookBlow> HookBlows(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var blows = new List<RecordedHookBlow>();
        foreach (var f in Frames(path))
            foreach (var d in f.Damage)
                if (d.DamageType == HookDamage && f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == Necromancer
                    && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId) == "player")
                    blows.Add(new RecordedHookBlow(name, f.Tick, d.Damage));
        return blows;
    }
}
