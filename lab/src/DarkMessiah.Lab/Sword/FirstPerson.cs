using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The player's first-person arms and weapon, as Dark Messiah draws them: Arkane's <c>first_basic</c> arms with the
/// move's clip laid over their class's idle, placed so their <c>eyes</c> attachment sits at the camera, and the weapon
/// (the build's, <see cref="Equip"/>; the short sword until one is) on the attachment the donor parents it to
/// (<see cref="HandOf"/>), the daggers' second blade on <c>RightHand_Daggers</c>. It also places the blade
/// for the swing traces: the donor traces from the eye toward the blade, out to the weapon's <c>rangeattack</c> (100
/// units for the short sword), and records each blow's trace that way.
/// </summary>
internal sealed class FirstPerson : IDisposable
{
    internal const ulong ArmsId = 0x4C41_0001, SwordId = 0x4C41_0002;
    // The weapons taken after the first, one id each.
    const ulong OtherWeaponIds = 0x4C41_0100;

    readonly IEngineContext engine;
    readonly DonorContent donor;
    readonly Puppet arms;
    // One puppet per weapon taken: the engine keeps an appearance it has published, so a weapon put away stays loaded, unpublished.
    readonly Dictionary<string, Puppet> weapons = new(StringComparer.Ordinal);
    Puppet sword;
    // The daggers' second blade, in the other hand (null for a weapon held in one).
    Puppet? offHand;
    (string Joint, Vector3 Tip) blade;
    float idleCycle;

    internal FirstPerson(IEngineContext engine, DonorContent donor)
    {
        this.engine = engine;
        this.donor = donor;
        arms = new Puppet(engine, donor.Model(SwordClips.Model), ArmsId);
        Weapon = LabWeapon.Of(donor, LabWeapon.ShortSword);
        sword = weapons[Weapon.Model] = new Puppet(engine, donor.Model(Weapon.Model), SwordId);
        blade = Rig.BladeTip(sword.Model.Info);
    }

    /// <summary>The weapon in hand: its model, class and numbers.</summary>
    internal LabWeapon Weapon { get; private set; }
    /// <summary>The weapon's model path, which the combat model reads its <c>WDmg</c> and <c>WCritHit</c> by.</summary>
    internal string SwordModel => Weapon.Model;
    /// <summary>The weapon's attack range, metres: its <c>rangeattack</c>.</summary>
    internal float Range => Weapon.Range;
    /// <summary>The weapon's <c>speed_attack</c>: its attack clips' playback rate.</summary>
    internal float AttackSpeed => Weapon.AttackSpeed;

    /// <summary>Puts <paramref name="weapon"/> in the hand: its model is drawn from now.</summary>
    internal void Equip(LabWeapon weapon)
    {
        if (weapon.Model == Weapon.Model) return;
        Weapon = weapon;
        sword = Loaded(weapon.Model);
        offHand = weapon.Class == WeaponClass.Daggers ? Loaded(weapon.Model + "#left") : null;
        blade = Rig.BladeTip(sword.Model.Info);
    }

    Puppet Loaded(string key)
    {
        if (!weapons.TryGetValue(key, out var puppet))
            weapons[key] = puppet = new Puppet(engine, donor.Model(key.Split('#')[0]), OtherWeaponIds + (ulong)weapons.Count);
        return puppet;
    }

    /// <summary>
    /// The arms' attachment a class's weapon hangs on: the one the donor parents the weapon entity to on its view model
    /// (Wire v10 <c>m_iParentAttachment</c>, <c>l01_a-v17-weapon-switch</c>): the swords and the club 4, <c>RightHand</c>;
    /// the kryss daggers 8, <c>LeftHand</c>; the combat staff 13, <c>LeftHand_Staff</c>. The daggers are one blade each
    /// (<c>daggers_kryss.glb</c>) and one entity: the client draws the other, which the lab puts on the arms'
    /// <c>RightHand_Daggers</c> (#10148).
    /// </summary>
    internal static (string Main, string? Other) HandOf(WeaponClass weapon) => weapon switch
    {
        WeaponClass.Daggers => ("LeftHand", "RightHand_Daggers"),
        WeaponClass.Staff => ("LeftHand_Staff", null),
        _ => ("RightHand", null),
    };

    /// <summary>A weapon's clips from the arms' model for its class, at its speed.</summary>
    internal SwordClips Clips(LabWeapon weapon, CombatTuning tuning) =>
        SwordClips.From(ArmsModel.Info, weapon.Class, weapon.AttackSpeed, tuning, weapon.Simple);

    internal DonorModel ArmsModel => arms.Model;
    /// <summary>Where the blade's tip is, as last posed.</summary>
    internal Vector3 Tip { get; private set; }
    internal Transform Arms { get; private set; }
    internal Transform Sword { get; private set; }
    /// <summary>The daggers' second blade, as last posed; null for a weapon held in one hand.</summary>
    internal Transform? OffHand { get; private set; }

    /// <summary>
    /// Poses the arms for the sword's move at the eye, looking along <paramref name="forward"/>: the idle runs on its
    /// own, the move's clip over it. Places the sword and its tip.
    /// </summary>
    internal void Pose(PlayerSword move, float seconds, Vector3 eye, Vector3 forward)
    {
        var idle = arms.Model.Clip(move.Clips.Idle.Clip);
        idleCycle = (idleCycle + (idle.Seconds > 0 ? seconds / idle.Seconds : 0)) % 1;
        if (move.Move == SwordMove.Ready) arms.Play(idle.Name, idleCycle);
        else arms.Play(idle.Name, idleCycle, (move.Clip.Clip, move.Traced ? move.TraceCycle : move.Cycle, 1));
        // Facing first, then the offset that puts the model's eyes attachment at the camera.
        var facing = new Transform(Vector3.Zero, Rig.Facing(forward, Vector3.UnitY), Vector3.One);
        var atOrigin = Rig.Pose(engine, arms, facing);
        var eyes = Rig.Attached(arms.Model, atOrigin, "eyes")?.Translation ?? Vector3.Zero;
        Arms = facing with { Translation = eye - eyes };
        var pose = Rig.Pose(engine, arms, Arms);
        var (main, other) = HandOf(Weapon.Class);
        Sword = Rig.Attached(arms.Model, pose, main) ?? Arms;
        OffHand = offHand is not null && other is not null ? Rig.Attached(arms.Model, pose, other) ?? Arms : null;
        var swordPose = Rig.Rest(engine, sword.Instance, Sword);
        Tip = sword.Model.Joints.TryGetValue(blade.Joint, out int j) ? Vector3.Transform(blade.Tip, swordPose[j].World.Rotation) + swordPose[j].World.Translation : Sword.Translation;
    }

    /// <summary>The blade's trace now: from the eye toward the tip, out to the sword's range.</summary>
    internal (Vector3 Start, Vector3 End) Trace(Vector3 eye)
    {
        var toward = Tip - eye;
        return (eye, eye + (toward.LengthSquared() > 1e-6f ? Vector3.Normalize(toward) : Vector3.UnitZ) * Range);
    }

    internal IEnumerable<AppearanceFact> Facts() =>
        offHand is not null && OffHand is { } left ? [arms.Fact(Arms), sword.Fact(Sword), offHand.Fact(left)] : [arms.Fact(Arms), sword.Fact(Sword)];

    public void Dispose()
    {
        arms.Dispose();
        foreach (var weapon in weapons.Values) weapon.Dispose();
    }
}
