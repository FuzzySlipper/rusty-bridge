using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The grips as the Engine's landmark pairs (#10395, R10395-2; docs/playtest-inspection.md, "Landmark pairs"): what a
/// right hold is, measured by the renderer on the poses it drew, published as the overlay set <see cref="Set"/> and read
/// with <c>engine.inspect.landmarks held.landmarks</c> or <c>lab.held.landmarks</c> (<c>lab/tests/landmarks-live.mjs</c>).
/// The player's right hand on its weapon: the blade along the knuckles' line (<see cref="BladeAlong"/>), the palm's
/// surface facing the grip's line (the weapon's <c>handle</c>, along its blade) within <see cref="GripReach"/> of it, the
/// web near the guard (<c>blade_start</c>, <see cref="GuardReach"/>). The orc's: the web near its cleaver's guard, the
/// middle of its forearm near its shield's middle (its hull's centre, <see cref="StrapReach"/>). The reaches are the
/// donor's own holds, measured on poses the recordings confirm (its hands grip 7.7 to 9.2 cm below the guard, its forearm
/// is 15.9 cm from its shield's middle), with a little over; the blade and palm pairs are what tell a turned grip.
/// </summary>
internal static class HeldLandmarks
{
    internal const string Set = "held.landmarks";

    /// <summary>The most the palm may turn from facing the grip's line, degrees.</summary>
    internal const float PalmFacing = 60;
    /// <summary>How near the grip's line the palm's centre is, metres.</summary>
    internal const float GripReach = .05f;
    /// <summary>How near the guard the web is, metres.</summary>
    internal const float GuardReach = .11f;
    /// <summary>The most the blade may turn from the knuckles' line (pinky to index), degrees: a fist's grip runs along it.</summary>
    internal const float BladeAlong = 35;
    /// <summary>How near the shield's middle the forearm's is, metres.</summary>
    internal const float StrapReach = .18f;

    /// <summary>A hand's joints: the wrist, then thumb, index, middle, ring and pinky chains from their bases.</summary>
    internal sealed record HandJoints(string Wrist, string[] Thumb, string[] Index, string[] Middle, string[] Ring, string[] Pinky);

    /// <summary>
    /// <c>first_basic</c>'s right hand: <c>SK_R_Finger_A</c> the index (its <c>Rightfinger_index</c> attachment), B the
    /// middle, C the ring (its <c>ring</c> attachment), <c>SK_Finger_D</c> the pinky; the joints' own cases.
    /// </summary>
    internal static readonly HandJoints PlayerRight = new("SK_R_Hand", ["Sk_R_Thumb_1", "SK_R_Thumb_2", "Sk_R_Thumb_3"],
        ["SK_R_Finger_A1", "SK_R_Finger_A2", "SK_R_Finger_A3"], ["SK_R_Finger_B1", "SK_R_Finger_B2", "SK_R_Finger_B3"],
        ["SK_R_Finger_C1", "SK_R_Finger_C2", "SK_R_Finger_C3"], ["SK_Finger_D1", "SK_Finger_D2", "SK_Finger_D3"]);

    /// <summary>
    /// The orc's right hand: three fingers and a thumb. The Engine's hand rig asks for four (a palm needs index, middle
    /// and pinky bases), so its last finger, C, stands for both the ring and the pinky.
    /// </summary>
    internal static readonly HandJoints OrcRight = new("SK_R_Hand", ["SK_R_Thumb_1", "SK_R_Thumb_2", "SK_R_Thumb_3"],
        ["SK_R_Finger_A1", "SK_R_Finger_A2", "SK_R_Finger_A3"], ["SK_R_Finger_B1", "SK_R_Finger_B2", "SK_R_Finger_B3"],
        ["SK_R_Finger_C1", "SK_R_Finger_C2", "SK_R_Finger_C3"], ["SK_R_Finger_C1", "SK_R_Finger_C2", "SK_R_Finger_C3"]);

    internal static HandRig Hand(IEngineContext engine, DonorModel holder, ulong id, HandJoints h) =>
        HandRig.FromJoints(engine.Animation.ReadJoints(holder.Mesh).Span, id, HandSide.Right, h.Wrist, h.Thumb, h.Index, h.Middle, h.Ring, h.Pinky);

    /// <summary>
    /// A weapon's grip and guard on its joint (the joint its <c>handle</c> hangs from): the handle's point, along the
    /// blade (toward <c>blade_start</c>), and the guard at <c>blade_start</c>; null for a weapon without both.
    /// </summary>
    internal static (OverlayPlace Joint, Vector3 Grip, Vector3 Along, Vector3 Guard)? Weapon(DonorModel weapon, ulong id)
    {
        var a = weapon.Info.Attachments;
        if (!a.TryGetValue("handle", out var handle) || !a.TryGetValue("blade_start", out var guard) || handle.Joint != guard.Joint
            || !weapon.Joints.TryGetValue(handle.Joint, out int joint)) return null;
        var along = guard.Local.Translation - handle.Local.Translation;
        if (along.LengthSquared() < 1e-8f) return null;
        return (OverlayPlace.AtJoint(id, (uint)joint), handle.Local.Translation, Vector3.Normalize(along), guard.Local.Translation);
    }

    /// <summary>
    /// The pairs a hand holding a weapon is judged by: <c>PREFIX.blade</c>, the blade along the knuckles' line from the
    /// pinky's base (<paramref name="holder"/>'s joints) to the index's; <c>PREFIX.palm</c>, the palm's surface facing the
    /// grip's line and near it; <c>PREFIX.guard</c>, the web at the guard. With <paramref name="fingers"/> off, the guard
    /// alone: the orc's three-fingered hand gives the Engine's four-fingered hand rig no knuckles' line or palm to trust (on
    /// a pose the recordings confirm, its cleaver read 82° off the line and 90° off the palm).
    /// </summary>
    internal static IEnumerable<LandmarkPair> Grip(string prefix, HandRig hand, DonorModel holder, ulong holderId, HandJoints joints, DonorModel weapon, ulong id,
        bool fingers = true)
    {
        if (Weapon(weapon, id) is not var (place, grip, along, guard)) yield break;
        if (!fingers)
        {
            yield return LandmarkPair.Distance($"{prefix}.guard", hand.Web(), Landmark.At(place, guard), GuardReach);
            yield break;
        }
        if (holder.Joints.TryGetValue(joints.Pinky[0], out int pinky) && holder.Joints.TryGetValue(joints.Index[0], out int index))
            yield return LandmarkPair.Facing($"{prefix}.blade", Landmark.Between(holderId, (uint)pinky, (uint)index, .5f), Landmark.At(place, grip, along),
                LandmarkAim.Axis, BladeAlong);
        yield return LandmarkPair.Facing($"{prefix}.palm", hand.Palm(depth: 0), Landmark.At(place, grip, along), LandmarkAim.Line, PalmFacing, GripReach);
        yield return LandmarkPair.Distance($"{prefix}.guard", hand.Web(), Landmark.At(place, guard), GuardReach);
    }

    /// <summary>The shield's pair: the middle of the holder's left forearm (elbow to wrist) and the shield's middle.</summary>
    internal static LandmarkPair? Strap(DonorModel holder, ulong holderId, DonorModel shield, ulong shieldId)
    {
        if (!holder.Joints.TryGetValue("SK_L_Elbow", out int elbow) || !holder.Joints.TryGetValue("SK_L_Wrist", out int wrist)) return null;
        var hull = shield.Info.Collision.FirstOrDefault();
        if (hull is null || hull.Points.Length == 0 || !shield.Joints.TryGetValue(hull.Joint, out int joint)) return null;
        var middle = hull.Points.Aggregate(Vector3.Zero, (s, p) => s + p) / hull.Points.Length;
        return LandmarkPair.Distance("orc.shield", Landmark.Between(holderId, (uint)elbow, (uint)wrist, .5f),
            Landmark.At(OverlayPlace.AtJoint(shieldId, (uint)joint), middle), StrapReach);
    }

    /// <summary>The set's pairs read back as measured, one line each: its name, measures and whether within its tolerances.</summary>
    internal static string Report(IEngineContext engine)
    {
        var read = engine.Graphics.ReadLandmarks(Set);
        if (!read.Measured) return $"{Set}: not measured yet (no frame drawn, or no renderer)";
        return string.Join(Environment.NewLine, read.Pairs.ToArray().Select(m => FormattableString.Invariant(
            $"{m.Name}: {(m.Drawn ? m.Within ? "within" : "OUT" : "not drawn")}, {m.Distance * 100:0.0} cm (tolerance {m.Tolerance * 100:0}){(m.HasAngle ? $", {m.Angle:0}° (tolerance {m.AngleTolerance:0})" : "")}, offset {m.Offset.X * 100:0.0} {m.Offset.Y * 100:0.0} {m.Offset.Z * 100:0.0} cm")));
    }
}
