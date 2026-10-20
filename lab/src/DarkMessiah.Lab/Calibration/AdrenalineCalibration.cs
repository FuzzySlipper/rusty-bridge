using DarkMessiah.Combat;
using DarkMessiah.Lab.Combat;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>One recorded player blow that landed, the gauge's rise the donor gave it, and what the lab's gauge gives.</summary>
internal sealed record AdrenalineComparison(int Tick, string Recording, string Move, float Before, float Donor, float Lab)
{
    internal bool Agrees => MathF.Abs(Donor - Lab) <= .01f;

    public override string ToString() =>
        FormattableString.Invariant($"{Recording} tick {Tick}: {Move} from {Before:0.#}, donor +{Donor:0.##}, lab +{Lab:0.##}");
}

/// <summary>
/// The lab's adrenaline against recorded blows (docs/lab.md, "Adrenaline"): for each frame with one player blow that
/// took health from an NPC, the donor's gauge rise (Wire v7's adrenaline, frame to frame) against the lab's: the
/// blow's <c>.qct</c> event's <c>adrenalineadd</c> (its clip and cycle from the view model's layers), up to the maximum.
/// </summary>
internal static class AdrenalineCalibration
{
    internal static List<AdrenalineComparison> Measure(IEnumerable<string> recordings, LabCombat combat)
    {
        var results = new List<AdrenalineComparison>();
        foreach (var path in recordings)
        {
            float? before = null;
            using var stream = File.OpenRead(path);
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Version < 7 || f.Player.Index == WireLayout.NoEntity) { before = null; continue; }
                float now = f.Player.Adrenaline;
                var blows = f.Damage.Where(d => d.Attacker == f.Player.Index && d.HealthBefore - d.HealthAfter > 0
                    && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId, "").StartsWith("npc_", StringComparison.Ordinal)).ToList();
                if (before is float was && blows.Count == 1 && (blows[0].DamageType & CombatModel.KickDamage) == 0)
                {
                    var moves = DamageCalibration.MoveLayers(f);
                    var newest = moves.LastOrDefault();
                    if (newest.Key is { } clip)
                    {
                        var player = new Party("player", "", [clip], newest.Value, "#player");
                        float add = combat.Model.EventOf(player)?.Adrenaline ?? 0;
                        float max = f.Player.MaxAdrenaline;
                        // A Fatality spends the gauge: those blows are the Fatality's, not a rise.
                        if (Replay.InputTimeline.Family(clip) != "fatality")
                            results.Add(new AdrenalineComparison(f.Tick, Path.GetFileNameWithoutExtension(path), clip, was, now - was, MathF.Min(add, max - was)));
                    }
                }
                before = now;
            }
        }
        return results;
    }
}
