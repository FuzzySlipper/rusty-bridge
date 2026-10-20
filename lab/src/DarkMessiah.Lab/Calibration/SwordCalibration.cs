using DarkMessiah.Lab.Sword;
using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One move of the player's sword against recorded donor fights: where its blows landed in its clip, and how fast
/// the donor played the clip, next to the lab's hit window and clip timing.
/// </summary>
/// <param name="Inside">Recorded blows landing within one of the lab's windows (widened by <see cref="SwordCalibration.Slack"/>).</param>
/// <param name="DonorCyclesPerSecond">The donor's cycle rate for the clip, from consecutive frames while it played; NaN when unseen.</param>
/// <param name="LaterWindows">The windows after the first, of a move that strikes in parts.</param>
internal sealed record MoveCalibration(string Clip, float Open, float Close, IReadOnlyList<float> Blows, int Inside,
    float LabCyclesPerSecond, float DonorCyclesPerSecond, IReadOnlyList<(float Open, float Close)>? LaterWindows = null)
{
    string Later => string.Concat((LaterWindows ?? []).Select(w => FormattableString.Invariant($", {w.Open:0.00}–{w.Close:0.00}")));

    public override string ToString() =>
        FormattableString.Invariant($"{Clip}: window {Open:0.00}–{Close:0.00}{Later}, {Inside} of {Blows.Count} recorded blows inside") +
        (Blows.Count > 0 ? FormattableString.Invariant($" (cycles {Blows.Min():0.00}–{Blows.Max():0.00})") : "") +
        FormattableString.Invariant($"; plays {LabCyclesPerSecond:0.00}/s, donor {DonorCyclesPerSecond:0.00}/s");
}

/// <summary>
/// Calibrates the lab's sword moves against recorded fights (Wire v9 and later carry the client's view-model
/// layers): for each move's clip, the cycles at which the donor's player blows landed while it played (the damage
/// records of melee and kick types), and the donor's playback rate measured from the layer's cycle over curtime.
/// </summary>
internal static class SwordCalibration
{
    /// <summary>A blow's streamed cycle lags its contact by up to a tick or two: this much either side of the window counts as inside.</summary>
    internal const float Slack = .06f;

    internal static List<MoveCalibration> Measure(IEnumerable<string> recordings, SwordClips clips)
    {
        MoveClip[] moves = clips.Moves.ToArray();
        var blows = moves.ToDictionary(m => m.Clip, _ => new List<float>());
        var rates = moves.ToDictionary(m => m.Clip, _ => new List<float>());
        foreach (var path in recordings)
        {
            (float Cycle, double Time)? last = null;
            string lastClip = "";
            using var stream = File.OpenRead(path);
            foreach (var f in RecordingReader.Frames(stream))
            {
                if (f.Version < 9 || !f.ClientView.Flags.HasFlag(ClientViewFlags.ViewmodelLayers)) continue;
                var vm = f.Entities.FirstOrDefault(kv => f.Classes.GetValueOrDefault(kv.Value.ClassId) == "viewmodel");
                if (f.Sequences.GetValueOrDefault(vm.Value.ModelIndex) is not { } names) continue;
                // The newest move on top: a kick can start while a power attack's layer still fades out under it.
                var playing = f.ClientView.ViewmodelLayers.ToArray()
                    .Where(l => l.Weight > 0 && l.Sequence >= 0 && l.Sequence < names.Length && blows.ContainsKey(names[l.Sequence]))
                    .OrderByDescending(l => l.Order).Select(l => (Clip: names[l.Sequence], l.Cycle)).FirstOrDefault();
                if (playing.Clip is null) { last = null; lastClip = ""; continue; }
                // The rate: cycle gained over curtime between frames of one play of the clip.
                if (last is { } l && lastClip == playing.Clip && playing.Cycle > l.Cycle && f.Curtime > l.Time)
                    rates[playing.Clip].Add((float)((playing.Cycle - l.Cycle) / (f.Curtime - l.Time)));
                last = (playing.Cycle, f.Curtime);
                lastClip = playing.Clip;
                foreach (var d in f.Damage)
                    if (f.Entities.TryGetValue(d.Attacker, out var a) && f.Classes.GetValueOrDefault(a.ClassId) == "player" && (d.DamageType & 0x40084) != 0
                        && f.Entities.TryGetValue(d.Victim, out var v) && f.Classes.GetValueOrDefault(v.ClassId, "").StartsWith("npc_", StringComparison.Ordinal))
                        blows[playing.Clip].Add(playing.Cycle);
            }
        }
        return moves.Select(m =>
        {
            var b = blows[m.Clip];
            var r = rates[m.Clip];
            float donor = r.Count == 0 ? float.NaN : r.Order().ElementAt(r.Count / 2);
            return new MoveCalibration(m.Clip, m.Open, m.Close, b, b.Count(c => m.Windows.Any(w => c >= w.Open - Slack && c <= w.Close + Slack)),
                m.Seconds > 0 ? 1 / m.Seconds : float.NaN, donor, m.Later);
        }).ToList();
    }
}
