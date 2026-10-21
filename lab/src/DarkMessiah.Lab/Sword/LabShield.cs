using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The shield on the player's arm (#10149), as the build names it by model path: its <c>.qct</c>'s <c>entity_data</c>
/// <c>parry_cos</c> (0.7 for every shield) and <c>health</c>, times the settings' player <c>HealthCoeffShield</c> (3:
/// the small orc shield's 80 was 240 in <c>l01_a-v17-shield</c>). Blocked blows wear it down by their damage.
/// </summary>
/// <param name="ParryCos">How far off the facing a blow is still blocked, as a cosine.</param>
/// <param name="Health">Its health on the player's arm.</param>
internal sealed record LabShield(string Model, float ParryCos, float Health)
{
    /// <summary>A shield's model: one under <c>models/items/armors/shield*</c>.</summary>
    internal static bool IsShield(string model)
    {
        var parts = VpkIndex.Normalize(model).Split('/');
        return parts.Length >= 5 && parts[^5] == "models" && parts[^4] == "items" && parts[^3] == "armors"
            && parts[^2].StartsWith("shield", StringComparison.Ordinal);
    }

    /// <summary>The shield a model path names, read from the donor; throws for a model that isn't one.</summary>
    internal static LabShield Of(DonorContent donor, string model, float healthCoeff)
    {
        if (!IsShield(model)) throw new ArgumentException($"{model} isn't a shield (models/items/armors/shield*).", nameof(model));
        return new LabShield(VpkIndex.Normalize(model), donor.Number(model, "entity_data", "parry_cos") ?? .7f,
            (donor.Number(model, "entity_data", "health") ?? 0) * healthCoeff);
    }
}
