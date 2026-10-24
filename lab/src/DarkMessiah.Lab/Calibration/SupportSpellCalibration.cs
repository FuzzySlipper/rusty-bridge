using RustyBridge.Wire;

namespace DarkMessiah.Lab.Calibration;

/// <summary>A cure in a recording (#10167): when its first heal came, the mana it took then, and each rise of the player's health after it.</summary>
/// <param name="Heals">Each rise over 0.5 within 4 s of the cast (and before the next rise of 4 or more), the cast's own
/// first: seconds after it, and how much (the <c>fast_metabolism</c> quarter-points left out).</param>
internal sealed record CureCast(double Cast, float Mana, IReadOnlyList<(double After, float Amount)> Heals)
{
    public override string ToString() =>
        FormattableString.Invariant($"cure {Cast:0.00} s: mana {Mana:0.#}, heals ") + string.Join(" ", Heals.Select(h => FormattableString.Invariant($"+{h.Amount:0.##}@{h.After:0.00}")));
}

/// <summary>
/// A blow struck after a weaken or charm (#10167): seconds after the press, the attacker's and victim's entity indexes,
/// the attacker's clip and its cycle as it struck, the damage asked and its type.
/// </summary>
internal readonly record struct SpellBlowAfter(double After, int Attacker, int Victim, string Clip, float Cycle, float Asked, uint Type, bool OnPlayer);

/// <summary>
/// A weaken or a charm in a recording (#10167), a press that took mana: the press, the mana, the orc it was cast at (the first whose
/// clip changed within 0.2 s of the press, or for a charm the first an orc struck), its clips after, and every orc's
/// blow from the press to 30 s on.
/// </summary>
internal sealed record SupportCast(string Spell, double Pressed, float Mana, int Target, IReadOnlyList<(double After, string Clip)> Clips, IReadOnlyList<SpellBlowAfter> Blows)
{
    /// <summary>The target's first clip from <paramref name="from"/> s after the press on that <paramref name="match"/>es; NaN if none.</summary>
    internal double First(double from, Func<string, bool> match) => Clips.FirstOrDefault(c => c.After >= from && match(c.Clip)) is { Clip: not null } c ? c.After : double.NaN;

    /// <summary>The target's last clip that <paramref name="match"/>es, seconds after the press; NaN if none.</summary>
    internal double Last(Func<string, bool> match) => Clips.LastOrDefault(c => match(c.Clip)) is { Clip: not null } c ? c.After : double.NaN;

    public override string ToString() =>
        FormattableString.Invariant($"{Spell} {Pressed:0.00} s: mana {Mana:0.#}, on #{Target}; blows ")
        + string.Join(" ", Blows.Select(b => FormattableString.Invariant($"#{b.Attacker}>{(b.OnPlayer ? "player" : $"#{b.Victim}")} {b.Clip} {b.Asked:0.#}@{b.After:0.00}")));
}

/// <summary>Reads the support spells from a recording (#10167; docs/lab.md, "Support spells").</summary>
internal static class SupportSpellCalibration
{
    /// <summary>The arms' clip family each spell plays in hand: charm plays the drain's.</summary>
    static string? SpellOf(string armsSpell) => armsSpell switch
    {
        "air_4" => "air_4",
        "earth_4" => "water_4",
        _ => null,
    };

    internal static (List<CureCast> Cures, List<SupportCast> Casts) Measure(string path)
    {
        var names = new EventNames();
        double interval = .015;
        var mana = new List<(double Time, float Drop)>();
        var heals = new List<(double Time, float Amount)>();
        var presses = new List<(string Spell, double Time)>();
        var npcClips = new Dictionary<int, List<(double Time, string Clip)>>();
        var blows = new List<(double Time, int Attacker, int Victim, string Clip, float Cycle, float Asked, uint Type, bool OnPlayer)>();
        float lastMana = float.NaN, lastHealth = float.NaN;
        string? inHand = null;
        bool held = false;
        using (var stream = File.OpenRead(path))
            foreach (var f in RecordingReader.Frames(stream))
            {
                names.Observe(f);
                interval = f.TickInterval > 0 ? f.TickInterval : interval;
                double time = f.Tick * interval;
                if (f.Stats.Valid != 0)
                {
                    if (!float.IsNaN(lastMana) && lastMana - f.Stats.Mana > .2f) mana.Add((time, lastMana - f.Stats.Mana));
                    lastMana = f.Stats.Mana;
                }
                if (f.Player.Index != WireLayout.NoEntity && f.Entities.TryGetValue(f.Player.Index, out var me) && me.Flags.HasFlag(WireEntityFlags.HasHealth))
                {
                    if (!float.IsNaN(lastHealth) && me.Health - lastHealth > .5f) heals.Add((time, me.Health - lastHealth));
                    lastHealth = me.Health;
                }
                var view = f.ClientView;
                var vm = f.Entities.Values.FirstOrDefault(e => f.Classes.GetValueOrDefault(e.ClassId) == "viewmodel");
                // The spell in hand while one of its casting clips plays (ready, cast, idle; not the holster): its id, the
                // clip's last two parts. None once they've stopped: the sword's taps after aren't casts.
                if (view.Flags.HasFlag(ClientViewFlags.ViewmodelLayers) && f.Sequences.GetValueOrDefault(vm.ModelIndex) is { } clips)
                {
                    inHand = null;
                    foreach (var l in view.ViewmodelLayers.ToArray())
                        if (l.Weight > 0 && l.Sequence >= 0 && l.Sequence < clips.Length && clips[l.Sequence] is var clip
                            && clip.StartsWith("layer_spellcasting_", StringComparison.Ordinal) && !clip.Contains("holster", StringComparison.Ordinal))
                            inHand = string.Join('_', clip.Split('_')[^2..]);
                }
                bool attack = f.Input.Valid != 0 && f.Input.Buttons.HasFlag(InputButtons.Attack);
                if (attack && !held && inHand is not null && SpellOf(inHand) is { } spell) presses.Add((spell, time));
                held = attack;
                foreach (var (index, e) in f.Entities)
                    if (f.Classes.GetValueOrDefault(e.ClassId, "").StartsWith("npc_orc", StringComparison.Ordinal) && f.Sequences.GetValueOrDefault(e.ModelIndex) is { } seqs
                        && e.Sequence >= 0 && e.Sequence < seqs.Length)
                    {
                        if (!npcClips.TryGetValue(index, out var list)) npcClips[index] = list = [];
                        if (list.Count == 0 || list[^1].Clip != seqs[e.Sequence]) list.Add((time, seqs[e.Sequence]));
                    }
                foreach (var d in f.Damage)
                {
                    string by = names.Name(f, d.Attacker, d.AttackerSerial), victim = names.Name(f, d.Victim, d.VictimSerial);
                    if (!by.StartsWith("npc_orc", StringComparison.Ordinal) || !(victim.StartsWith("npc_orc", StringComparison.Ordinal) || victim.StartsWith("player", StringComparison.Ordinal))) continue;
                    // The attacker's clip and cycle as it struck.
                    var (clip, cycle) = f.Entities.TryGetValue(d.Attacker, out var a) && f.Sequences.GetValueOrDefault(a.ModelIndex) is { } aseqs && a.Sequence >= 0 && a.Sequence < aseqs.Length
                        ? (aseqs[a.Sequence], a.Cycle) : ("?", 0f);
                    blows.Add((d.Tick * interval, d.Attacker, d.Victim, clip, cycle, d.Damage, d.DamageType, victim.StartsWith("player", StringComparison.Ordinal)));
                }
            }
        // A cure: a rise of 4 or more in one step, the mana going within a tick and a half of it.
        var cures = new List<CureCast>();
        foreach (var (time, amount) in heals.Where(h => h.Amount >= 4))
        {
            if (cures.Count > 0 && time - cures[^1].Cast < 4) continue;
            float cost = mana.Where(m => Math.Abs(m.Time - time) <= .025).Sum(m => m.Drop);
            if (cost <= 0) continue;
            // Up to the next rise of 4 or more (another heal, uncast: one press 2 s after a cast gave 5 for no mana).
            double until = Math.Min(time + 4, heals.Where(h => h.Time > time && h.Amount >= 4).Select(h => h.Time).DefaultIfEmpty(double.PositiveInfinity).First());
            cures.Add(new CureCast(time, cost, heals.Where(h => h.Time >= time && h.Time < until).Select(h => (h.Time - time, h.Amount)).ToList()));
        }
        var casts = new List<SupportCast>();
        foreach (var (spell, pressed) in presses)
        {
            // A press that cast: its mana went.
            float cost = mana.Where(m => m.Time >= pressed - .05 && m.Time <= pressed + .3).Sum(m => m.Drop);
            if (cost < .5f) continue;
            var after = blows.Where(b => b.Time >= pressed && b.Time <= pressed + 30).ToList();
            int target = spell == "water_4" && after.FirstOrDefault(b => !b.OnPlayer) is { Victim: var v } && after.Any(b => !b.OnPlayer) ? v
                : npcClips.Where(kv => kv.Value.Any(c => c.Time >= pressed && c.Time <= pressed + .2)).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
            var clips = npcClips.GetValueOrDefault(target)?.Where(c => c.Time >= pressed && c.Time <= pressed + 30).Select(c => (c.Time - pressed, c.Clip)).ToList() ?? [];
            casts.Add(new SupportCast(spell, pressed, cost, target, clips,
                after.Select(b => new SpellBlowAfter(b.Time - pressed, b.Attacker, b.Victim, b.Clip, b.Cycle, b.Asked, b.Type, b.OnPlayer)).ToList()));
        }
        return (cures, casts);
    }
}
