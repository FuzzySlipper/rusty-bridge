using System.Numerics;
using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// The lightning bolt (<c>spells.txt</c>'s <c>air_3</c>; #10166, docs/lab.md "Attack spells"): its <c>cost</c> (8) and
/// <c>effect</c> (15, twice on the orc: its <c>Vulnerabilities_spells</c> <c>air_3</c> 2), <c>adrenaline_add</c> (3).
/// Measured (<c>l01_a-v18-attack-spells</c>, <c>-2</c>): it strikes <see cref="FireSeconds"/> after the press, tapped or
/// held 1.5 s (1.79 to 1.88 s, 5 casts), the mana going as it strikes; the orc it strikes is stunned
/// (<c>hit_stunned_front</c>, every one); a strike that would kill leaves it at 1, and it dies
/// <see cref="DeathDelay"/> s later (one, 1.7 s).
/// </summary>
internal sealed record LightningRules(float Cost, float Effect, float AdrenalineAdd, float FireSeconds = 1.8f, float DeathDelay = 1.7f, float RangeUnits = 2000)
{
    internal const string Spell = "air_3";
    internal const string ReadyClip = "layer_spellcasting_ready_air_3", CastClip = "layer_spellcasting_cast_in_air_3",
        HoldClip = "layer_spellcasting_cast_idle_air_3", OutClip = "layer_spellcasting_cast_out_air_3";

    internal static readonly LightningRules Shipped = new(8, 15, 3);

    /// <summary>
    /// Its numbers from the donor. Its reach is <c>lightning_NPC_Bounce_Distance</c> (2000 units), the distance a bolt
    /// carries to the next NPC: the recorded bolts struck at 200, and none bounced (the second orcs never stood).
    /// </summary>
    internal static LightningRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(LightningRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Effect = Spell("effect") ?? Shipped.Effect,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
            RangeUnits = Spell("lightning_NPC_Bounce_Distance") ?? Shipped.RangeUnits,
        };
    }
}

/// <summary>
/// The freeze (<c>water_3</c>; #10166): its <c>cost</c> (15) at the press, a bolt <see cref="CastSeconds"/> later (0.27
/// s, both casts) at <see cref="SpeedUnits"/> (4320 units/s), and the orc it meets frozen where it stands for
/// <c>Stage1_Duration</c> (7 s: the recorded orc stood 7.2 s from the bolt, then fought on as before; the later stages,
/// 6 and 12 s, changed nothing seen). No damage.
/// </summary>
internal sealed record FreezeRules(float Cost, float AdrenalineAdd, float FrozenSeconds, float SpeedUnits = 4320, float LifeSeconds = 2, float CastSeconds = .27f)
{
    internal const string Spell = "water_3";
    internal const string ReadyClip = "layer_spellcasting_ready_water_3", CastClip = "layer_spellcasting_cast_water_3";

    internal static readonly FreezeRules Shipped = new(15, 2, 7);

    internal static FreezeRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(FreezeRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
            FrozenSeconds = Spell("Stage1_Duration") ?? Shipped.FrozenSeconds,
        };
    }

    internal float SpeedMetres => SpeedUnits * SourceSpace.MetresPerUnit;
}

/// <summary>
/// The inferno (<c>fire_4</c>; #10166): held, a stream of fire. Its <c>cost</c> (20) at the press, then
/// <c>maintain_cost</c> (15) a second from <see cref="UpkeepFrom"/> s into the hold (both recorded holds) until it's let
/// go or the mana runs out; <c>effect</c> (13) on the orc in its path <see cref="FirstSeconds"/> s after the press and
/// every <see cref="EverySeconds"/> s after (0.17 and 0.51, both holds); a blow that would kill leaves it at 1, and it
/// dies once it has burnt <see cref="BurnDeath"/> s (the settings' <c>Fire_Time_Ignite</c>: 5.2 s after the last
/// blow, one; the other, struck on at 1 until let go, 2.4 s after the flame caught).
/// </summary>
internal sealed record InfernoRules(float Cost, float Maintain, float Effect, float AdrenalineAdd, float BurnDeath,
    float FirstSeconds = .17f, float EverySeconds = .51f, float UpkeepFrom = 1.5f, float RangeUnits = 300, float ConeDegrees = 15)
{
    internal const string Spell = "fire_4";
    internal const string ReadyClip = "layer_spellcasting_ready_fire_4", CastClip = "layer_spellcasting_cast_in_fire_4",
        HoldClip = "layer_spellcasting_cast_idle_fire_4", OutClip = "layer_spellcasting_cast_out_fire_4";

    internal static readonly InfernoRules Shipped = new(20, 15, 13, .1f, 5);

    /// <summary>
    /// Its numbers from the donor. Its reach (<see cref="RangeUnits"/>, 300 units) and width (<see cref="ConeDegrees"/>)
    /// are the lab's: the recorded orc stood 200 units off, in the middle of the view.
    /// </summary>
    internal static InfernoRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(InfernoRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Maintain = Spell("maintain_cost") ?? Shipped.Maintain,
            Effect = Spell("effect") ?? Shipped.Effect,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
            BurnDeath = donor.Combat.Setting("Fire_Time_Ignite") ?? Shipped.BurnDeath,
        };
    }
}

/// <summary>A freeze bolt in flight: straight on until it meets something or its time is up.</summary>
internal sealed class LabBolt(Vector3 at, Vector3 velocity)
{
    internal Vector3 Position { get; private set; } = at;
    internal Vector3 Velocity { get; } = velocity;
    internal float Age { get; private set; }
    internal Vector3? Stopped { get; private set; }

    internal void Step(float seconds, float life, Func<Vector3, Vector3, Vector3?> meets)
    {
        if (Stopped is not null) return;
        Age += seconds;
        var next = Position + Velocity * seconds;
        if (meets(Position, next) is Vector3 hit) { Position = hit; Stopped = hit; return; }
        Position = next;
        if (Age >= life) Stopped = Position;
    }
}
