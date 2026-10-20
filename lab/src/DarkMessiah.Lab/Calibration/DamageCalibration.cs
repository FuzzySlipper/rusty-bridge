using DarkMessiah.Combat;
using DarkMessiah.Lab.Combat;
using DarkMessiah.Lab.Sword;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>One recorded player blow on an orc, what the donor asked, and what the lab asks of the same blow.</summary>
internal sealed record DamageComparison(int Tick, string Recording, string Move, int? Group, float Donor, float Lab, string Reason)
{
    internal bool Agrees => MathF.Abs(Donor - Lab) <= DamageCalibration.Tolerance;

    public override string ToString() =>
        FormattableString.Invariant($"{Recording} tick {Tick}: {Move}{(Group is int g ? $" on group {g}" : "")}, donor {Donor:0.##}, lab {Lab:0.##}{(Agrees ? "" : $": {Reason}")}");
}

/// <summary>
/// The lab's damage against recorded donor blows: every player blow on an orc with the short sword's fast, power, jump
/// or rush attack, or a kick, asked again through the lab's own path (<see cref="LabCombat.PlayerAsks"/>), with the move and
/// hit group the recording gives, and the recorded critical (#10146: the lab's own roll gives way to it). Where the two
/// differ, the reason is one the lab's inputs explain:
/// <list type="bullet">
/// <item>the orc wasn't alert (×3 for a blow at or below the not-alerted level), and the lab's orc always is;</item>
/// <item>the orc was getting up (<c>StandUp_*</c>: ×2), and the lab's orc is never down.</item>
/// </list>
/// </summary>
internal static class DamageCalibration
{
    internal const float Tolerance = .01f;

    internal static List<DamageComparison> Measure(IEnumerable<string> recordings, LabCombat combat, SwordClips clips)
    {
        var settings = combat.Model.Data.Settings;
        var results = new List<DamageComparison>();
        foreach (var path in recordings)
        {
            FightCases fight;
            using (var stream = File.OpenRead(path)) fight = Blows.Read(Path.GetFileName(path), stream);
            // From Wire v16 the recording says the player's extra_damage level, which sets its strength; before it
            // the player had a new game's skills (level 0, strength 0).
            int extraDamage = 0;
            using (var stream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(stream))
                {
                    if (f.Version < 16) break;
                    if (f.Stats.Valid != 0) { extraDamage = f.Stats.SkillLevel("extra_damage"); break; }
                }
            float strength = combat.Model.Data.PlayerStrength(extraDamage);
            // The player's move clips and their cycles as each blow arrived (the view model's layers): a swing in parts
            // (the rush attack) asks by its clip's cycle.
            var moveCycles = new Dictionary<int, Dictionary<string, float>>();
            using (var stream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(stream))
                    if (f.Damage.Count > 0)
                        foreach (var d in f.Damage) moveCycles.TryAdd(d.Tick, MoveLayers(f));
            foreach (var d in fight.Damage)
            {
                // A swing that kills goes on to touch the body: those contacts ask nothing of a victim already at 0.
                if (!d.Attacker.IsPlayer || d.Victim.Class != "npc_orc_sword" || d.Defended || d.HealthBefore <= 0) continue;
                // The newest move layer (an earlier move's can be fading under it), as the lab plays it: the same clip
                // (A, B, each power attack's direction), or its family's neutral move for one the lab lacks. The jump
                // and rush attacks' blows carry the kick's damage bit too (they always push): a kick is one the newest
                // move doesn't say is a swing.
                string? swung = d.Attacker.Layers.Select(Replay.InputTimeline.Family).LastOrDefault(f => f is not null);
                bool kick = (d.DamageType & CombatModel.KickDamage) != 0 && swung is null or "kick";
                string? family = kick ? "kick" : swung;
                string donorMove = d.Attacker.Layers.LastOrDefault(l => Replay.InputTimeline.Family(l) is not null) ?? "";
                string? move = kick ? clips.Kick.Clip : clips.Moves.Any(m => m.Clip == donorMove) ? donorMove
                    : family switch { "fast attack" => clips.Fast.Clip, "power attack" => clips.Power.Clip, _ => null };
                if (donorMove.Length == 0) donorMove = move ?? "";
                if (move is null || !kick && d.Weapon != "weapon_arx_short_sword") continue;
                var player = d.Attacker with { Layers = [move],
                    Cycle = moveCycles.GetValueOrDefault(d.Tick)?.GetValueOrDefault(donorMove, float.NaN) is float c && !float.IsNaN(c) ? c : d.Attacker.Cycle };
                // The recorded critical (the byte from v11, or a critical power attack's damage type) is the lab's roll here.
                float lab = combat.PlayerAsks(player, d.Victim, kick ? null : d.HitGroup, kick, d.HealthBefore, strength,
                    critical: d.AttackerCritical || (d.DamageType & CombatModel.CriticalDamage) != 0);
                string reason =
                    donorMove != move && donorMove.Length > 0 ? $"the donor's variant {donorMove}, which the lab plays as {move}"
                    : d.VictimAlert is int alert && alert <= settings.NotAlertedLevel && d.VictimNpcState != CombatModel.NotAlertedExemptState
                        ? $"the orc wasn't alert (×{settings.NotAlertedMultiplier:0.#}): the lab's always is"
                    : d.Victim.Sequence.StartsWith("StandUp", StringComparison.OrdinalIgnoreCase) && MathF.Abs(d.Requested - 2 * lab) < .01f
                        ? $"the orc was getting up ({d.Victim.Sequence}): ×2, and the lab's orc is never down"
                    : d.VictimAlert is null ? "before Wire v11: neither the critical byte nor the orc's alert level is on the Wire"
                    : "unexplained";
                results.Add(new DamageComparison(d.Tick, Path.GetFileNameWithoutExtension(path), move, kick ? null : d.HitGroup, d.Requested, lab, reason));
            }
            // Kicks, which Blows leaves out (it keeps slash and club blows): the player's kick damage on an orc.
            using (var stream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(stream))
                    foreach (var d in f.Damage)
                    {
                        // A kick that pushes also makes a zero-damage call of its own, untraced, with a push force: the push, not the
                        // hit. The jump attack's strike makes a traced one (its alwayspush): no kick asks nothing. The jump and
                        // rush attacks' blows carry the kick's bit too: a kick's frame plays a kick, or no move at all.
                        if ((d.DamageType & CombatModel.KickDamage) == 0 || d.TraceValid == 0 || d.Damage == 0
                            || MoveLayers(f).Keys.Select(Replay.InputTimeline.Family).ToList() is var moves && moves.Count > 0 && !moves.Contains("kick") || !f.Entities.TryGetValue(d.Attacker, out var a) || f.Classes.GetValueOrDefault(a.ClassId) != "player"
                            || !f.Entities.TryGetValue(d.Victim, out var v) || f.Classes.GetValueOrDefault(v.ClassId) != "npc_orc_sword"
                            ) continue;
                        var victim = new Party("npc_orc_sword", "", [], 0, $"#{d.Victim}", f.Models.GetValueOrDefault(v.ModelIndex, ""));
                        var kicker = new Party("player", "", [clips.Kick.Clip], 0, $"#{d.Attacker}");
                        float lab = combat.PlayerAsks(kicker, victim, null, kick: true, d.HealthBefore);
                        string reason = d.VictimAlertValid != 0 && d.VictimAlert <= settings.NotAlertedLevel && d.VictimNpcState != CombatModel.NotAlertedExemptState
                            ? $"the orc wasn't alert (×{settings.NotAlertedMultiplier:0.#}): the lab's always is"
                            : d.VictimAlertValid == 0 ? "before Wire v11: neither the critical byte nor the orc's alert level is on the Wire" : "unexplained";
                        results.Add(new DamageComparison(d.Tick, Path.GetFileNameWithoutExtension(path), clips.Kick.Clip, null, d.Damage, lab, reason));
                    }
        }
        return results;
    }

    /// <summary>The player's first-person move clips playing in a frame (any move family), with their cycles.</summary>
    internal static Dictionary<string, float> MoveLayers(RecordedFrame f)
    {
        var layers = new Dictionary<string, float>();
        var view = f.ClientView;
        if (!view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers)) return layers;
        var vm = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");
        if (f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is not { } names) return layers;
        foreach (var l in view.ViewmodelLayers)
            if (l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length && Replay.InputTimeline.Family(names[l.Sequence]) is not null)
                layers[names[l.Sequence]] = l.Cycle;
        return layers;
    }
}
