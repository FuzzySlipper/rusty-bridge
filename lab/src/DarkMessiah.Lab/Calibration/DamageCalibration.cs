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
/// The lab's damage against recorded donor blows: every player blow on an orc with the short sword's fast or power
/// attack, or a kick, asked again through the lab's own path (<see cref="LabCombat.PlayerAsks"/>), with the move and
/// hit group the recording gives. Where the two differ, the reason is one the lab's inputs explain:
/// <list type="bullet">
/// <item>the donor's blow was a critical (×2), which the lab doesn't roll;</item>
/// <item>the orc wasn't alert (×3 for a blow at or below the not-alerted level), and the lab's orc always is.</item>
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
            foreach (var d in fight.Damage)
            {
                // A swing that kills goes on to touch the body: those contacts ask nothing of a victim already at 0.
                if (!d.Attacker.IsPlayer || d.Victim.Class != "npc_orc_sword" || d.Defended || d.HealthBefore <= 0) continue;
                bool kick = (d.DamageType & CombatModel.KickDamage) != 0;
                // The newest move layer (an earlier move's can be fading under it), as the lab plays it: the same clip
                // (A, B, each power attack's direction), or its family's neutral move for one the lab lacks.
                string? family = kick ? "kick" : d.Attacker.Layers.Select(Replay.InputTimeline.Family).LastOrDefault(f => f is not null);
                string donorMove = d.Attacker.Layers.LastOrDefault(l => Replay.InputTimeline.Family(l) is not null) ?? "";
                string? move = kick ? clips.Kick.Clip : clips.Moves.Any(m => m.Clip == donorMove) ? donorMove
                    : family switch { "fast attack" => clips.Fast.Clip, "power attack" => clips.Power.Clip, _ => null };
                if (donorMove.Length == 0) donorMove = move ?? "";
                if (move is null || !kick && d.Weapon != "weapon_arx_short_sword") continue;
                var player = d.Attacker with { Layers = [move] };
                float lab = combat.PlayerAsks(player, d.Victim, kick ? null : d.HitGroup, kick, d.HealthBefore);
                string reason =
                    donorMove != move && donorMove.Length > 0 ? $"the donor's variant {donorMove}, which the lab plays as {move}"
                    : d.AttackerCritical || (d.DamageType & CombatModel.CriticalDamage) != 0 ? "a critical (×2): the lab rolls none"
                    : d.VictimAlert is int alert && alert <= settings.NotAlertedLevel && d.VictimNpcState != CombatModel.NotAlertedExemptState
                        ? $"the orc wasn't alert (×{settings.NotAlertedMultiplier:0.#}): the lab's always is"
                    : d.VictimAlert is null ? "before Wire v11: neither the critical byte nor the orc's alert level is on the Wire"
                    : "unexplained";
                results.Add(new DamageComparison(d.Tick, Path.GetFileNameWithoutExtension(path), move, kick ? null : d.HitGroup, d.Requested, lab, reason));
            }
            // Kicks, which Blows leaves out (it keeps slash and club blows): the player's kick damage on an orc.
            using (var stream = File.OpenRead(path))
                foreach (var f in RecordingReader.Frames(stream))
                    foreach (var d in f.Damage)
                    {
                        // A kick that pushes also makes a zero-damage call of its own, untraced, with a push force: the push, not the hit.
                        if ((d.DamageType & CombatModel.KickDamage) == 0 || d.TraceValid == 0 || !f.Entities.TryGetValue(d.Attacker, out var a) || f.Classes.GetValueOrDefault(a.ClassId) != "player"
                            || !f.Entities.TryGetValue(d.Victim, out var v) || f.Classes.GetValueOrDefault(v.ClassId) != "npc_orc_sword") continue;
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
}
