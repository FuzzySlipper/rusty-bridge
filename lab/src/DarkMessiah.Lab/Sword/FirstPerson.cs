using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The player's first-person arms and short sword, as Dark Messiah draws them: Arkane's <c>first_basic</c> arms with
/// the move's clip laid over their idle, placed so their <c>eyes</c> attachment sits at the camera, and the sword on
/// their <c>RightHand</c>. It also places the blade for the swing traces: the donor traces from the eye toward the
/// blade, out to the weapon's <c>rangeattack</c> (100 units for the short sword), and records each blow's trace that way.
/// </summary>
internal sealed class FirstPerson : IDisposable
{
    internal const string SwordModel = "models/Items/Weapons/Sword_Short/Sword_Short.mdl";
    internal const ulong ArmsId = 0x4C41_0001, SwordId = 0x4C41_0002;

    readonly IEngineContext engine;
    readonly Puppet arms, sword;
    readonly (string Joint, Vector3 Tip) blade;
    float idleCycle;

    internal FirstPerson(IEngineContext engine, DonorContent donor)
    {
        this.engine = engine;
        arms = new Puppet(engine, donor.Model(SwordClips.Model), ArmsId);
        sword = new Puppet(engine, donor.Model(SwordModel), SwordId);
        blade = Rig.BladeTip(sword.Model.Info);
        Range = (donor.Number(SwordModel, "entity_data", "rangeattack") ?? 100) * SourceSpace.MetresPerUnit;
        AttackSpeed = donor.Number(SwordModel, "entity_data", "speed_attack") ?? 1;
    }

    /// <summary>The sword's attack range, metres: its <c>rangeattack</c>.</summary>
    internal float Range { get; }
    /// <summary>The sword's <c>speed_attack</c>: its attack clips' playback rate.</summary>
    internal float AttackSpeed { get; }
    internal DonorModel ArmsModel => arms.Model;
    /// <summary>Where the blade's tip is, as last posed.</summary>
    internal Vector3 Tip { get; private set; }
    internal Transform Arms { get; private set; }
    internal Transform Sword { get; private set; }

    /// <summary>
    /// Poses the arms for the sword's move at the eye, looking along <paramref name="forward"/>: the idle runs on its
    /// own, the move's clip over it. Places the sword and its tip.
    /// </summary>
    internal void Pose(PlayerSword move, float seconds, Vector3 eye, Vector3 forward)
    {
        var idle = arms.Model.Clip("layer_sword_noshield_idle");
        idleCycle = (idleCycle + (idle.Seconds > 0 ? seconds / idle.Seconds : 0)) % 1;
        if (move.Move == SwordMove.Ready) arms.Play(idle.Name, idleCycle);
        else arms.Play(idle.Name, idleCycle, (move.Clip.Clip, move.Cycle, 1));
        // Facing first, then the offset that puts the model's eyes attachment at the camera.
        var facing = new Transform(Vector3.Zero, Rig.Facing(forward, Vector3.UnitY), Vector3.One);
        var atOrigin = Rig.Pose(engine, arms, facing);
        var eyes = Rig.Attached(arms.Model, atOrigin, "eyes")?.Translation ?? Vector3.Zero;
        Arms = facing with { Translation = eye - eyes };
        var pose = Rig.Pose(engine, arms, Arms);
        Sword = Rig.Attached(arms.Model, pose, "RightHand") ?? Arms;
        var swordPose = Rig.Rest(engine, sword.Instance, Sword);
        Tip = sword.Model.Joints.TryGetValue(blade.Joint, out int j) ? Vector3.Transform(blade.Tip, swordPose[j].World.Rotation) + swordPose[j].World.Translation : Sword.Translation;
    }

    /// <summary>The blade's trace now: from the eye toward the tip, out to the sword's range.</summary>
    internal (Vector3 Start, Vector3 End) Trace(Vector3 eye)
    {
        var toward = Tip - eye;
        return (eye, eye + (toward.LengthSquared() > 1e-6f ? Vector3.Normalize(toward) : Vector3.UnitZ) * Range);
    }

    internal IEnumerable<AppearanceFact> Facts() => [arms.Fact(Arms), sword.Fact(Sword)];

    public void Dispose()
    {
        arms.Dispose();
        sword.Dispose();
    }
}
