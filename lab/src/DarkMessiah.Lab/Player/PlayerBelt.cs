using DarkMessiah.Lab.Donor;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>The potions' models.</summary>
internal static class PotionModels
{
    internal const string Life = "models/items/provisions/potions/life_potion.mdl", FullLife = "models/items/provisions/potions/full_life_potion.mdl";
}

/// <summary>
/// One kind of thing the belt holds, from its <c>.qct</c>'s <c>entity_data</c>: <c>give_health</c> (the life potion 10,
/// the full life potion 300, a red apple 3) and <c>cure_poison</c> (the cure poison potion).
/// </summary>
internal sealed record BeltItem(string Model, float GiveHealth, bool CuresPoison)
{
    internal static BeltItem Of(DonorContent donor, string model) =>
        new(VpkIndex.Normalize(model), donor.Number(model, "entity_data", "give_health") ?? 0, donor.Number(model, "entity_data", "cure_poison") is > 0);

    /// <summary>Its short name: the model's file name.</summary>
    internal string Name => Path.GetFileNameWithoutExtension(Model);
}

/// <summary>
/// The belt's nine slots, used by their keys as the donor's <c>usebelt1</c>–<c>usebelt9</c> (#10162; docs/lab.md,
/// "Health"): a slot gives its item's health at once, up to the maximum, and holds one fewer (recorded,
/// <c>l01_a-v18-health</c>: a life potion 15 to 25, the full life potion 25 to the maximum 50, each in the tick it was
/// used). The build fills it (<see cref="CharacterBuild.Belt"/>); <c>give</c> in the donor fills the belt with potions,
/// not food, whose <c>no_belt_priority</c> keeps it in the inventory.
/// </summary>
internal sealed class PlayerBelt
{
    internal const int Slots = 9;
    readonly (BeltItem Item, int Count)?[] slots = new (BeltItem, int)?[Slots];

    internal (BeltItem Item, int Count)? this[int slot] => slots[slot];

    /// <summary>The build's belt, full again.</summary>
    internal void Fill(DonorContent donor, IReadOnlyList<BeltSlot> belt)
    {
        Array.Clear(slots);
        for (int i = 0; i < Math.Min(Slots, belt.Count); i++)
            slots[i] = belt[i].Count > 0 ? (BeltItem.Of(donor, belt[i].Item), belt[i].Count) : null;
    }

    /// <summary>Uses slot <paramref name="slot"/> (0 to 8) on <paramref name="health"/>: the item it gave, or null for an empty slot.</summary>
    internal BeltItem? Use(int slot, PlayerHealth health)
    {
        if (slot is < 0 or >= Slots || slots[slot] is not { } held) return null;
        health.Heal(held.Item.GiveHealth);
        slots[slot] = held.Count > 1 ? (held.Item, held.Count - 1) : null;
        return held.Item;
    }

    /// <summary>What it holds, for the HUD: "1 life_potion ×2 · 3 full_life_potion".</summary>
    internal string Describe() => string.Join(" · ", slots.Select((s, i) => s is { } held ? $"{i + 1} {held.Item.Name}{(held.Count > 1 ? $" ×{held.Count}" : "")}" : null).OfType<string>());
}
