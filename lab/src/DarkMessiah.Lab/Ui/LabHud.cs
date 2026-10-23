using System.Text;
using Rusty.Engine;

namespace DarkMessiah.Lab.Ui;

internal static class LabHud
{
    /// <summary>
    /// The health bar's fill, percent of the maximum, as the HUD draws it (R10162-1): through blows, regeneration,
    /// potions and a new build's maximum alike.
    /// </summary>
    internal static string HealthFill(float health, float max) =>
        FormattableString.Invariant($"{(max > 0 ? Math.Clamp(health / max, 0, 1) * 100 : 0):0.#}");

    internal static UiValue Create(LabObservation observation) => Value(Fields(observation));

    /// <summary>The HUD's facts by name, as the projection carries them.</summary>
    internal static (string Key, string Text)[] Fields(LabObservation observation)
    {
        LabPlayerFacts player = observation.Player;
        string Vec(float[] v) => FormattableString.Invariant($"{v[0]:0.00}, {v[1]:0.00}, {v[2]:0.00}");
        LabFightFacts f = observation.Fight;
        (string Key, string Text)[] fields = [
            ("health",FormattableString.Invariant($"{f.PlayerHealth:0.#} / {f.PlayerMaxHealth:0}{(f.God ? " · god" : "")}{(f.Belt.Length > 0 ? " · belt " + f.Belt : "")}")),
            ("healthFill",HealthFill(f.PlayerHealth, f.PlayerMaxHealth)),
            ("move",FormattableString.Invariant($"{f.Move} · {f.Clip} {f.Cycle:0.00}{(f.Traced ? " · tracing" : "")}")),
            ("orc",FormattableString.Invariant($"{f.OrcState} · {f.OrcClip} {f.OrcCycle:0.00} · {f.OrcHealth:0.#} health · {f.OrcDistance:0.0} m")),
            ("blow",f.LastBlow is { } b ? FormattableString.Invariant($"{b.Move} at {b.Cycle:0.00}: {b.Outcome}") : ""),
            ("swings",FormattableString.Invariant($"swings {f.Swings} · hits {f.Hits} · parried {f.Parried} · blocked {f.Blocked} · missed {f.Missed} · dealt {f.Dealt:0.#}")),
            ("orcblows",FormattableString.Invariant($"orc blows {f.OrcBlows} · reached {f.OrcReached} · you parried {f.PlayerParries} · broken {f.ParriesBroken} · taken {f.OrcDealt:0.#} · falls {f.Deaths}")),
            ("position",Vec(player.Feet)), ("stance",player.Stance), ("grounded",player.Grounded ? "GROUNDED" : "AIRBORNE"),
            ("target",observation.Target), ("message",observation.Message), ("notice",observation.Notice),
            ("held",observation.HeldCube is null ? "" : observation.Target),
            ("step",observation.Stamp.Step.ToString()),
            ("world",observation.WorldTime),
            ("actions",$"pickups {observation.Pickups} · throws {observation.Throws} · drops {observation.Drops}")
        ];
        return fields;
    }

    static UiValue Value((string Key, string Text)[] fields)
    {
        List<byte> utf8 = [];
        List<StructuredValueNode> nodes = [new(StructuredValueKind.Object, 0, 0, 0, 0, 0, 0, 0, (uint)fields.Length)];
        foreach ((string key, string text) in fields)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key), textBytes = Encoding.UTF8.GetBytes(text);
            uint keyOffset = (uint)utf8.Count;
            utf8.AddRange(keyBytes);
            uint textOffset = (uint)utf8.Count;
            utf8.AddRange(textBytes);
            nodes.Add(new StructuredValueNode(StructuredValueKind.String, 0, 0, keyOffset, (uint)keyBytes.Length, textOffset, (uint)textBytes.Length, 0, 0));
        }
        return new UiValue(nodes.ToArray(), Enumerable.Range(1, fields.Length).Select(value => (uint)value).ToArray(), 0, utf8.ToArray());
    }
}
