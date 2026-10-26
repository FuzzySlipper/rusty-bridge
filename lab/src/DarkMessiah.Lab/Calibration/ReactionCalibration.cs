using System.Numerics;
using RustyBridge.Wire;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One recorded player blow that took an orc's health (#10153), and the reaction the orc began within
/// <see cref="ReactionCalibration.Within"/> ticks: a new main sequence, or a new overlay.
/// </summary>
/// <param name="Move">The player's move (its view-model clip), or <c>kick</c>.</param>
/// <param name="Side">Where the blow came from, by the damage record's direction against the orc's facing: <c>front</c>,
/// <c>back</c>, <c>left</c> or <c>right</c>.</param>
/// <param name="Angle">That direction's angle from the orc's facing, degrees, positive to its left.</param>
/// <param name="HealthFraction">Its health after the blow, of the most it was seen with.</param>
/// <param name="Busy">For a hit or push in its place (#10382): seconds from the blow until it played something else; NaN
/// for none, an overlay, or one that hadn't ended within <see cref="ReactionCalibration.Follow"/>.</param>
/// <param name="Moved">How far it went over those seconds, metres, along the ground.</param>
/// <param name="Turned">How far it turned over them, degrees, positive to its left.</param>
internal sealed record ReactionCase(string Recording, int Tick, string Move, float Damage, int HitGroup, int PushForce, int DamageFlag,
    string Side, float Angle, float HealthFraction, string Before, float BeforeCycle, string Reaction, string Overlay, int Delay, float Facing = 0, bool Critical = false, float HitHeight = float.NaN,
    string OverlayBefore = "", string Victim = "", float Busy = float.NaN, float Moved = float.NaN, float Turned = float.NaN);

/// <summary>The donor's hit reactions (#10153; docs/lab.md, "Hit reactions"), from every local recording.</summary>
internal static class ReactionCalibration
{
    internal const int Within = 16;

    /// <summary>How long a hit or push in its place is followed to its end, ticks (6 s; #10382).</summary>
    internal const int Follow = 400;

    /// <summary>The damage record's MM flag for a kick.</summary>
    internal const int KickFlag = 0x400000;

    /// <summary>The recorded reaction as the rule names it: a small hit's overlay, a hit or push, or none ("").</summary>
    internal static string Recorded(ReactionCase c) =>
        IsReaction(c.Reaction) ? c.Reaction : c.Overlay.StartsWith("Hit_", StringComparison.Ordinal) ? c.Overlay : "";

    /// <summary>
    /// The lab's rule (<see cref="Npc.OrcReactions"/>) against every recorded blow it decides (<see cref="Blows"/>). Each
    /// orc's small-hit timer runs from the rule's own small hits. Returns the blows compared, the rule's reaction and the
    /// recorded one (case aside: the rule names the model's clips in its own case).
    /// </summary>
    internal static List<(ReactionCase Case, string Lab, string Donor)> Score(IEnumerable<ReactionCase> cases, DarkMessiah.Combat.CombatData data)
    {
        var scored = new List<(ReactionCase, string, string)>();
        var lastSmall = new Dictionary<(string, string), int>();
        foreach (var (c, blow, stunned, donor) in Blows(cases, data))
        {
            int since = lastSmall.TryGetValue((c.Recording, c.Victim), out int t) ? c.Tick - t : int.MaxValue;
            var r = Npc.OrcReactions.Pick(blow, stunned, since == int.MaxValue ? double.PositiveInfinity : since / 66.0);
            if (r.Overlay) lastSmall[(c.Recording, c.Victim)] = c.Tick;
            scored.Add((c, r.Clip ?? "", donor));
        }
        return scored;
    }

    /// <summary>
    /// Every recorded blow a reaction rule decides, as the rule takes it: those whose reaction isn't a stun or knockdown
    /// (<see cref="Npc.OrcStagger"/>'s), on an orc the blow didn't kill, not dodging, grabbing, dying or getting up (it
    /// plays none then, as the lab's), one blow a tick for each orc (the daggers' second blade), in order; with whether
    /// the orc was stunned and the recorded reaction (<see cref="Recorded"/>).
    /// </summary>
    internal static List<(ReactionCase Case, Npc.ReactionBlow Blow, bool Stunned, string Donor)> Blows(IEnumerable<ReactionCase> cases, DarkMessiah.Combat.CombatData data)
    {
        var blows = new List<(ReactionCase, Npc.ReactionBlow, bool, string)>();
        var seen = new HashSet<(string, int, string)>();
        var attacks = data.Character(DarkMessiah.Combat.CombatData.PlayerAttacksModel);
        foreach (var c in cases.OrderBy(c => c.Recording, StringComparer.Ordinal).ThenBy(c => c.Tick))
        {
            if (!seen.Add((c.Recording, c.Tick, c.Victim))) continue;
            string donor = Recorded(c);
            if (donor.StartsWith("hit_stunned", StringComparison.Ordinal) || donor.StartsWith("StandUp", StringComparison.Ordinal)) continue;
            if (c.HealthFraction <= 0 || c.Before.StartsWith("Dodge_", StringComparison.Ordinal) || c.Before is "attack_grab"
                || c.Before.StartsWith("die", StringComparison.OrdinalIgnoreCase) || c.Before.StartsWith("StandUp", StringComparison.Ordinal)) continue;
            bool kick = c.Move == "kick";
            var ev = kick ? null : attacks?.Attack(c.Move)?.Events.FirstOrDefault();
            if (!kick && ev is null) continue;
            bool stunned = c.Before.StartsWith("hit_stunned", StringComparison.Ordinal) || c.Before == "Stunned";
            blows.Add((c, new Npc.ReactionBlow(ev, kick, c.Damage, c.Critical, c.HitGroup >= 0 ? c.HitGroup : null, c.Facing), stunned, donor));
        }
        return blows;
    }

    internal static string SideOf(float angle) =>
        angle is > -45 and <= 45 ? "front" : angle is > 45 and <= 135 ? "left" : angle is < -45 and >= -135 ? "right" : "back";

    internal static List<ReactionCase> Measure(IEnumerable<string> recordings)
    {
        var cases = new List<ReactionCase>();
        foreach (var path in recordings)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var most = new Dictionary<(ushort, uint), float>();
            // Blows waiting for their reaction: the frames are read one at a time (the reader reuses its frame).
            var pending = new List<(ReactionCase Case, ushort Victim, uint Serial, Dictionary<string, float> Overlays, Vector3 Start, float Cycle)>();
            // Each orc's sequence, cycle and overlays as the last frame left them: a blow's reaction can show on its own tick.
            var last = new Dictionary<(ushort, uint), (string Sequence, float Cycle, Dictionary<string, float> Overlays)>();
            try
            {
                using var stream = File.OpenRead(path);
                var arrows = new Arrows();
                foreach (var f in arrows.Frames(stream))
                {
                    for (int p = pending.Count - 1; p >= 0; p--)
                    {
                        var (c, victim, serial, overlaysBefore, start, cycle) = pending[p];
                        bool gone = !f.Entities.TryGetValue(victim, out var w) || w.Serial != serial, ended = false;
                        if (!gone)
                        {
                            var names = f.Sequences.GetValueOrDefault(w.ModelIndex) ?? [];
                            string seq = w.Sequence >= 0 && w.Sequence < names.Length ? names[w.Sequence] : "";
                            // A hit or push followed to its end (#10382): another sequence, or the same one played again.
                            if (c.Reaction.Length > 0 && f.Tick > c.Tick + c.Delay && (seq != c.Reaction || w.Cycle < cycle - .05f))
                            {
                                var moved = new Vector2(w.OriginX - start.X, w.OriginY - start.Y);
                                c = c with { Busy = (f.Tick - c.Tick) / 66f, Moved = moved.Length() * SourceSpace.MetresPerUnit, Turned = (w.Yaw - start.Z + 540) % 360 - 180 };
                                ended = true;
                            }
                            if (c.Reaction.Length == 0 && IsReaction(seq) && (seq != c.Before || w.Cycle < c.BeforeCycle - .05f)) c = c with { Reaction = seq, Delay = f.Tick - c.Tick };
                            if (c.Overlay.Length == 0 && Started(Overlays(f, victim, names), overlaysBefore) is { } o) c = c with { Overlay = o };
                            pending[p] = (c, victim, serial, overlaysBefore, start, seq == c.Reaction ? w.Cycle : cycle);
                        }
                        if (gone || ended || f.Tick >= c.Tick + (c.Reaction.Length > 0 ? Follow : Within)) { cases.Add(pending[p].Case); pending.RemoveAt(p); }
                    }
                    var seen = new Dictionary<(ushort, uint), (string, float, Dictionary<string, float>)>();
                    foreach (var (index, e) in f.Entities)
                    {
                        if (f.Classes.GetValueOrDefault(e.ClassId) != Replay.InputTimeline.OrcClass) continue;
                        if (e.Flags.HasFlag(WireEntityFlags.HasHealth)) most[(index, e.Serial)] = MathF.Max(most.GetValueOrDefault((index, e.Serial)), e.Health);
                        var orcNames = f.Sequences.GetValueOrDefault(e.ModelIndex) ?? [];
                        seen[(index, e.Serial)] = (e.Sequence >= 0 && e.Sequence < orcNames.Length ? orcNames[e.Sequence] : "", e.Cycle, Overlays(f, index, orcNames));
                    }
                    foreach (var d in f.Damage)
                    {
                        // An arrow's reaction isn't the melee rule's (#10168).
                        if (d.Damage <= 0 || arrows.Is(f, d)) continue;
                        if (!f.Entities.TryGetValue(d.Attacker, out var a) || f.Classes.GetValueOrDefault(a.ClassId) != "player") continue;
                        if (!f.Entities.TryGetValue(d.Victim, out var v) || f.Classes.GetValueOrDefault(v.ClassId) != Replay.InputTimeline.OrcClass) continue;
                        var names = f.Sequences.GetValueOrDefault(v.ModelIndex) ?? [];
                        var layers = DamageCalibration.MoveLayers(f);
                        // A kick: the kick's damage bit with no swing on screen, or the MM damage flag's kick bit (0x400000,
                        // set on every recorded kick, also those struck while a swing's clip was still showing).
                        string move = (d.DamageType & DarkMessiah.Combat.CombatModel.KickDamage) != 0 && layers.Keys.All(k => Replay.InputTimeline.Family(k) is null or "kick")
                            || (d.MMDamageFlag & KickFlag) != 0 ? "kick" : layers.Keys.LastOrDefault() ?? "?";
                        // The blow's direction: the damage record's MM direction, else attacker to victim.
                        var dir = new Vector2(d.MMDirectionX, d.MMDirectionY);
                        if (dir.LengthSquared() < 1e-6f) dir = new Vector2(v.OriginX - a.OriginX, v.OriginY - a.OriginY);
                        // Where it came from is against that direction; the orc faces its yaw.
                        float from = MathF.Atan2(-dir.Y, -dir.X) * 180 / MathF.PI;
                        float angle = (from - v.Yaw + 540) % 360 - 180;
                        float full = MathF.Max(most.GetValueOrDefault((d.Victim, v.Serial)), d.HealthBefore);
                        string now = v.Sequence >= 0 && v.Sequence < names.Length ? names[v.Sequence] : "";
                        var (before, beforeCycle, beforeOverlays) = last.TryGetValue((d.Victim, v.Serial), out var was) ? was : (now, v.Cycle, Overlays(f, d.Victim, names));
                        var c = new ReactionCase(name, f.Tick, move, d.Damage, d.TraceValid != 0 ? d.HitGroup : -1, d.PushForce, d.MMDamageFlag,
                            SideOf(angle), angle, full > 0 ? d.HealthAfter / full : 1, before, beforeCycle, "", "", -1, (a.Yaw - v.Yaw + 540) % 360 - 180, d.AttackerCritical != 0,
                            d.PositionZ - v.OriginZ, beforeOverlays.Keys.FirstOrDefault(k => k.StartsWith("Hit_", StringComparison.Ordinal) || k.StartsWith("Push_", StringComparison.Ordinal)) ?? "",
                            $"#{d.Victim}.{v.Serial:x}");
                        // Its reaction may already be on this tick.
                        if (IsReaction(now) && (now != before || v.Cycle < beforeCycle - .05f)) c = c with { Reaction = now, Delay = 0 };
                        if (Started(Overlays(f, d.Victim, names), beforeOverlays) is { } o) c = c with { Overlay = o };
                        // A reaction still being followed is cut short by this blow: its end isn't the reaction's own.
                        for (int p = pending.Count - 1; p >= 0; p--)
                            if (pending[p].Victim == d.Victim && pending[p].Serial == v.Serial && pending[p].Case.Reaction.Length > 0 && pending[p].Case.Tick < f.Tick)
                            { cases.Add(pending[p].Case); pending.RemoveAt(p); }
                        pending.Add((c, d.Victim, v.Serial, beforeOverlays, new Vector3(v.OriginX, v.OriginY, v.Yaw), c.Reaction.Length > 0 ? v.Cycle : 0));
                    }
                    foreach (var (key, value) in seen) last[key] = value;
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or EndOfStreamException) { }
            cases.AddRange(pending.Select(p => p.Case));
        }
        return cases;
    }

    /// <summary>A sequence a blow plays: a hit, a push, a stun or a knockdown (other changes are the orc's own doing).</summary>
    internal static bool IsReaction(string sequence) =>
        sequence.StartsWith("Hit_", StringComparison.Ordinal) || sequence.StartsWith("Push_", StringComparison.Ordinal)
        || sequence.StartsWith("hit_stunned", StringComparison.Ordinal) || sequence.StartsWith("StandUp_", StringComparison.Ordinal);

    /// <summary>An entity's overlays playing, by name, with their cycles.</summary>
    static Dictionary<string, float> Overlays(RecordedFrame f, ushort index, string[] names)
    {
        var playing = new Dictionary<string, float>();
        foreach (var l in f.Layers)
            if (l.Index == index && l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length) playing[names[l.Sequence]] = l.Cycle;
        return playing;
    }

    /// <summary>An overlay begun since <paramref name="before"/>: new, or played again from its start.</summary>
    static string? Started(Dictionary<string, float> now, Dictionary<string, float> before) =>
        now.Where(o => !before.TryGetValue(o.Key, out float was) || o.Value < was - .05f).Select(o => o.Key).FirstOrDefault();
}
