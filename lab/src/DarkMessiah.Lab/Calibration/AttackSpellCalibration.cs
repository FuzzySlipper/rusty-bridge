using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>
/// One cast of an attack spell in a recording (#10166): the spell in hand (by the arms' <c>layer_spellcasting_ready_*</c>
/// clip), when attack went down and up, the mana as it went, its blows on NPCs, and the struck NPC's clips after.
/// </summary>
/// <param name="Mana">Each drop of the donor's mana from the press to the next press (or 8 s on): when, and how much.</param>
/// <param name="Hits">Its damage records on NPCs (inflicted by <c>spell_&lt;id&gt;</c>): when, the damage asked, what it
/// took, and its health before.</param>
/// <param name="Clips">The first struck NPC's clips as each began, from the press to 12 s on (or the first NPC's in view
/// for a spell that strikes no blow).</param>
internal sealed record SpellCast(string Spell, double Pressed, double Released, IReadOnlyList<(double At, float Drop)> Mana,
    IReadOnlyList<(double At, float Asked, float Taken, float Before)> Hits, IReadOnlyList<(double At, string Clip)> Clips)
{
    public override string ToString() =>
        FormattableString.Invariant($"{Spell} pressed {Pressed:0.00} s, held {Released - Pressed:0.00} s: mana ")
        + string.Join(" ", Mana.Take(4).Select(m => FormattableString.Invariant($"-{m.Drop:0.#}@{m.At - Pressed:+0.00}"))) + (Mana.Count > 4 ? FormattableString.Invariant($" ... ({Mana.Count}, {Mana.Sum(m => m.Drop):0.#} in all)") : "")
        + "; hits " + string.Join(" ", Hits.Select(h => FormattableString.Invariant($"{h.Asked:0.#}/{h.Taken:0.#}@{h.At - Pressed:+0.00}")))
        + "; clips " + string.Join(" ", Clips.Take(5).Select(c => FormattableString.Invariant($"{c.Clip}@{c.At - Pressed:+0.00}")));
}

internal static class AttackSpellCalibration
{
    internal static List<SpellCast> Measure(string path)
    {
        var names = new EventNames();
        var casts = new List<(string Spell, double Pressed, double Released)>();
        var mana = new List<(double, float)>();
        var damage = new List<(double At, string Inflictor, int Victim, float Asked, float Taken, float Before)>();
        var npcClips = new Dictionary<int, List<(double, string)>>();
        var npcSeen = new Dictionary<int, string>();
        string? inHand = null;
        bool held = false;
        float lastMana = float.NaN;
        double interval = .015;
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                names.Observe(f);
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double time = f.Tick * interval;
                var view = f.ClientView;
                var vm = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "viewmodel");
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.ModelIndex) is { } clips)
                    foreach (var l in view.ViewmodelLayers.ToArray())
                        if (l.Weight > 0 && l.Sequence >= 0 && l.Sequence < clips.Length && clips[l.Sequence].StartsWith("layer_spellcasting_ready_", StringComparison.Ordinal))
                            inHand = clips[l.Sequence]["layer_spellcasting_ready_".Length..];
                bool attack = f.Input.Valid != 0 && f.Input.Buttons.HasFlag(InputButtons.Attack);
                if (attack && !held && inHand is not null) casts.Add((inHand, time, double.NaN));
                if (!attack && held && casts.Count > 0 && double.IsNaN(casts[^1].Released)) casts[^1] = casts[^1] with { Released = time };
                held = attack;
                if (f.Stats.Valid != 0)
                {
                    if (!float.IsNaN(lastMana) && lastMana - f.Stats.Mana > .2f) mana.Add((time, lastMana - f.Stats.Mana));
                    lastMana = f.Stats.Mana;
                }
                foreach (var d in f.Damage)
                {
                    string victim = names.Name(f, d.Victim, d.VictimSerial), by = names.Name(f, d.Inflictor, d.InflictorSerial);
                    if (victim.StartsWith("npc_", StringComparison.Ordinal) && by.StartsWith("spell_", StringComparison.Ordinal))
                        damage.Add((d.Tick * interval, by["spell_".Length..], d.Victim, d.Damage, d.HealthBefore - d.HealthAfter, d.HealthBefore));
                }
                foreach (var (index, e) in f.Entities)
                    if (f.Classes.GetValueOrDefault(e.ClassId, "").StartsWith("npc_orc", StringComparison.Ordinal) && f.Sequences.GetValueOrDefault(e.ModelIndex) is { } seqs
                        && e.Sequence >= 0 && e.Sequence < seqs.Length && npcSeen.GetValueOrDefault(index) != seqs[e.Sequence])
                    {
                        npcSeen[index] = seqs[e.Sequence];
                        if (!npcClips.TryGetValue(index, out var list)) npcClips[index] = list = [];
                        list.Add((time, seqs[e.Sequence]));
                    }
            }
        var result = new List<SpellCast>();
        for (int i = 0; i < casts.Count; i++)
        {
            var (spell, pressed, released) = casts[i];
            double until = Math.Min(i + 1 < casts.Count ? casts[i + 1].Pressed : double.PositiveInfinity, pressed + 8);
            var hits = damage.Where(d => d.At >= pressed && d.At < until && d.Inflictor == spell).ToList();
            // The struck NPC, or for a spell that strikes no blow the orc whose clip last began before the press.
            int? struck = hits.Count > 0 ? hits[0].Victim
                : npcClips.Where(kv => kv.Value.Any(c => c.Item1 <= pressed)).OrderByDescending(kv => kv.Value.Last(c => c.Item1 <= pressed).Item1).Select(kv => (int?)kv.Key).FirstOrDefault();
            var clips = struck is int k2 && npcClips.TryGetValue(k2, out var list2) ? list2.Where(c => c.Item1 >= pressed && c.Item1 <= pressed + 12).ToList() : [];
            result.Add(new SpellCast(spell, pressed, double.IsNaN(released) ? pressed : released, mana.Where(m => m.Item1 >= pressed - .05 && m.Item1 < until).ToList(),
                hits.Select(h => (h.At, h.Asked, h.Taken, h.Before)).ToList(), clips));
        }
        return result;
    }
}
