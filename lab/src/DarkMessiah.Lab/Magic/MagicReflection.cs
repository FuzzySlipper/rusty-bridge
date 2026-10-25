using System.Numerics;
using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Magic;

/// <summary>
/// Magic reflection (<c>spells.txt</c>'s <c>earth_2</c>; #10349, docs/lab.md "Magic reflection"): its <c>cost</c> (25) and
/// <c>effect</c> (15 s), and <c>shield_radius</c> (60). How server.dll does it: the spell's entity queries a sphere of
/// <c>shield_radius</c> about the player 20 units above its origin (server.dll+0x474606, the 20 at +0x588E00) each tick,
/// and of what it finds turns the flame arrow's, the fireball's and the freeze's missiles
/// (<c>CARXSPELL_FIRE_1_Missile</c>, <c>_FIRE_3_</c>, <c>_WATER_3_</c>), but not the player's own (+0x4746D9) and not into
/// a spell staff, which takes them. Measured (<c>l01_a-v19-reflect-probe-2</c>, <c>-3</c>):
/// <list type="bullet">
/// <item>it's cast as it's put in hand, as cure wounds is (<c>Deploy_Spell_EARTH_2</c>: no arm clip, the hand keeps
/// the spell it held), for 25 mana; its entity lasted 15.0 s at the player's feet (three casts);</item>
/// <item>a necromancer's flame arrow coming at the player turned where its origin came within <see cref="Reach"/> of the
/// sphere's centre (74.7 to 75.9 units, the query meeting the missile's box: <c>shield_radius</c> and 15), and flew off
/// from the player's origin through where it was, at its speed (720 units/s in and out; 11 of 11, none striking the
/// player, none the necromancer);</item>
/// <item>an orc archer's arrows weren't turned: all 15 loosed in two shields struck the player for 5.</item>
/// </list>
/// </summary>
internal sealed record ReflectionRules(float Cost, float Seconds, float Radius, float AdrenalineAdd)
{
    internal const string Spell = "earth_2";
    /// <summary>The sphere's centre above the player's origin, server.dll's 20.</summary>
    internal const float CentreHeight = 20;
    /// <summary>How much farther than the sphere a missile's origin is when its box meets it: the recorded turns' 15.</summary>
    internal const float MissileHalf = 15;

    internal static readonly ReflectionRules Shipped = new(25, 15, 60, 0);

    /// <summary>The missiles it turns: the flame arrow's, the fireball's and the freeze's.</summary>
    internal static readonly string[] Turned = ["fire_1", "fire_3", "water_3"];

    /// <summary>How far from the sphere's centre a missile's origin is turned.</summary>
    internal float Reach => Radius + MissileHalf;

    internal static ReflectionRules From(DonorContent donor)
    {
        float? Spell(string key) => donor.Tables.Spell(ReflectionRules.Spell, key);
        return Shipped with
        {
            Cost = Spell("cost") ?? Shipped.Cost,
            Seconds = Spell("effect") ?? Shipped.Seconds,
            Radius = Spell("shield_radius") ?? Shipped.Radius,
            AdrenalineAdd = Spell("adrenaline_add") ?? Shipped.AdrenalineAdd,
        };
    }

    /// <summary>
    /// A missile at <paramref name="at"/> (donor units) flying at <paramref name="velocity"/>, the player's origin at
    /// <paramref name="origin"/>: its new velocity if the shield turns it now, from the player's origin through where it
    /// is at its speed; null if it flies on (out of reach, or not a spell the shield turns).
    /// </summary>
    internal Vector3? Turn(string spell, Vector3 at, Vector3 velocity, Vector3 origin)
    {
        if (!Turned.Contains(spell, StringComparer.OrdinalIgnoreCase)) return null;
        if (Vector3.Distance(at, origin + Vector3.UnitZ * CentreHeight) > Reach) return null;
        var away = at - origin;
        return away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) * velocity.Length() : -velocity;
    }
}
