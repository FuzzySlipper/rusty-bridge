using System.Numerics;
using DarkMessiah.Lab.Donor;
using DarkMessiah.Lab.Sword;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Player;

/// <summary>
/// The player's own body under the first-person camera (#10394; docs/lab.md, "The player's body"): Dark Messiah's
/// third-person model, <c>player_basic</c>, at the player's feet, played by <see cref="BodyAnimator"/>'s rules from the
/// lab's movement, kick and jumps. Walking, running, sprinting and crouching blend the clip's two <c>move_yaw</c>
/// columns either side of the direction of travel (the import's <c>NAME@COLUMN</c> clips, 45° apart from −180).
/// The donor collapses its head and arms into their joints, so only its legs and hips show under the view; the Engine
/// can't scale a joint yet (rusty-engine #10436), so it isn't drawn (<see cref="Shown"/>) until it can.
/// </summary>
internal sealed class PlayerBody : IDisposable
{
    internal const string Model = "models/npc/player/player_basic.mdl";
    internal const ulong Id = 0x4C41_0500;

    readonly Puppet puppet;

    internal PlayerBody(IEngineContext engine, DonorContent donor)
    {
        puppet = new Puppet(engine, donor.Model(Model), Id);
        Animator = new BodyAnimator(puppet.Model.Info);
    }

    internal BodyAnimator Animator { get; }
    internal DonorModel BodyModel => puppet.Model;

    /// <summary>Drawn; off until the Engine can collapse its head and arms as the donor does (rusty-engine #10436).</summary>
    internal bool Shown { get; set; }

    /// <summary>Back standing in <c>idle_leftfoot</c>, facing <paramref name="forward"/>.</summary>
    internal void Reset(Vector3 forward) => Animator.Reset(Yaw(forward));

    /// <summary>
    /// One step: the body's horizontal <paramref name="velocity"/> (m/s), the view's <paramref name="forward"/>, a movement
    /// key held, sprinting, crouched, on the ground, and whether a kick began this step.
    /// </summary>
    internal void Step(float seconds, Vector3 velocity, Vector3 forward, bool moving, bool sprinting, bool crouched, bool grounded, bool kickStarted)
    {
        var source = new Vector2(velocity.Z, velocity.X) / SourceSpace.MetresPerUnit;
        float pitch = -MathF.Asin(Math.Clamp(forward.Y, -1, 1)) * 180 / MathF.PI;
        Animator.Step(new BodyInput(seconds, source, Yaw(forward), pitch, moving, sprinting, crouched, grounded, kickStarted));
    }

    /// <summary>The body at <paramref name="feet"/>, facing its yaw, posed as it plays; none unless <see cref="Shown"/>.</summary>
    internal IEnumerable<AppearanceFact> Facts(Vector3 feet)
    {
        if (!Shown) yield break;
        var (clip, cycle, over) = Pose();
        puppet.Play(clip, cycle, over);
        float yaw = Animator.Yaw * MathF.PI / 180;
        var facing = SourceSpace.Direction(new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0));
        yield return puppet.Fact(new Transform(feet, Rig.Facing(facing, Vector3.UnitY), Vector3.One));
    }

    /// <summary>
    /// What it plays: a blended gait as its <c>move_yaw</c> column below the direction of travel, with the one above over
    /// it by how far between them it is; then the jump layers.
    /// </summary>
    internal (string Clip, float Cycle, (string Clip, float Cycle, float Weight)[] Over) Pose()
    {
        var a = Animator;
        var over = new List<(string, float, float)>();
        string clip = a.Clip;
        if (puppet.Model.Info.Clips.ContainsKey($"{clip}@0"))
        {
            float column = (BodyAnimator.Wrap(a.MoveYaw) + 180) / 45;
            int below = Math.Clamp((int)MathF.Floor(column), 0, 7);
            over.Add(($"{clip}@{below + 1}", a.Cycle, column - below));
            clip = $"{clip}@{below}";
        }
        over.AddRange(a.Layers.Select(l => (l.Clip, l.Cycle, l.Weight)));
        return (clip, a.Cycle, [.. over]);
    }

    /// <summary>A lab direction's yaw as Source measures it, degrees.</summary>
    internal static float Yaw(Vector3 forward) => MathF.Atan2(forward.X, forward.Z) * 180 / MathF.PI;

    public void Dispose() => puppet.Dispose();
}
