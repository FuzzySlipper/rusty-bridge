using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The grips as the Engine's landmark pairs (#10395, R10395-2; docs/playtest-inspection.md, "Landmark pairs"): what a
/// right hold is, measured by the renderer on the poses it drew, published as the overlay set <see cref="Set"/> and read
/// with <c>engine.inspect.landmarks held.landmarks</c> or <c>lab.held.landmarks</c> (<c>lab/tests/landmarks-live.mjs</c>).
/// The player's right hand closes round its weapon's grip: the knuckles' line along the blade (<see cref="BladeAlong"/>),
/// the palm facing the grip's line (the weapon's <c>handle</c>, along its blade) within <see cref="GripReach"/> of it, the
/// web near the guard (<c>blade_start</c>, <see cref="GuardReach"/>). The orc's three-fingered hand holds its cleaver as
/// the donor hangs it (<see cref="Hold.OutOfPalm"/>): the haft runs out of the palm, handle before it and guard behind
/// (<see cref="HaftOut"/>), the palm on it; the middle of its forearm is near its shield's middle (its hull's centre,
/// <see cref="StrapReach"/>). The reaches are the donor's own holds, measured on poses the recordings confirm (its hands
/// grip 7.7 to 9.2 cm below the guard, the orc's palm is 1.6 cm from its haft, its forearm 15.9 cm from its shield's
/// middle), with a little over; the angles are what tell a turned grip.
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
    /// <summary>The most the orc's haft may turn from its palm's normal (guard to handle), degrees: the donor's is 8° off.</summary>
    internal const float HaftOut = 30;
    /// <summary>How near the shield's middle the forearm's is, metres.</summary>
    internal const float StrapReach = .18f;

    /// <summary>How a hand holds its weapon.</summary>
    internal enum Hold
    {
        /// <summary>Closed round the grip: the knuckles along it, the palm facing it (the player's).</summary>
        Fist,
        /// <summary>
        /// The haft out of the palm, the handle before it: the orc's cleaver, which the donor hangs at its <c>RightHand</c>
        /// with its blade across the hand's plane (its knuckles run 82° from the blade, whatever the pose).
        /// </summary>
        OutOfPalm,
    }

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
    /// The orc's right hand: three fingers and a thumb, so no ring finger and its last, C, the pinky (the Engine's
    /// three-fingered hand rig, rusty-engine #10402).
    /// </summary>
    internal static readonly HandJoints OrcRight = new("SK_R_Hand", ["SK_R_Thumb_1", "SK_R_Thumb_2", "SK_R_Thumb_3"],
        ["SK_R_Finger_A1", "SK_R_Finger_A2", "SK_R_Finger_A3"], ["SK_R_Finger_B1", "SK_R_Finger_B2", "SK_R_Finger_B3"],
        [], ["SK_R_Finger_C1", "SK_R_Finger_C2", "SK_R_Finger_C3"]);

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
    /// The pairs a hand holding a weapon is judged by. <see cref="Hold.Fist"/>: <c>PREFIX.blade</c>, the knuckles' line
    /// along the blade; <c>PREFIX.palm</c>, the palm facing the grip's line and near it. <see cref="Hold.OutOfPalm"/>:
    /// <c>PREFIX.palm</c>, the palm's normal along the haft from guard to handle and near the handle. Both:
    /// <c>PREFIX.guard</c>, the web at the guard. The palm is the Engine's default, which reads how a fist faces a handle
    /// it closes round (rusty-engine #10403).
    /// </summary>
    internal static IEnumerable<LandmarkPair> Grip(string prefix, HandRig hand, DonorModel weapon, ulong id, Hold hold)
    {
        if (Weapon(weapon, id) is not var (place, grip, along, guard)) yield break;
        if (hold == Hold.Fist)
        {
            var line = Landmark.At(place, grip, along);
            yield return LandmarkPair.Facing($"{prefix}.blade", hand.Knuckles(), line, LandmarkAim.Axis, BladeAlong);
            yield return LandmarkPair.Facing($"{prefix}.palm", hand.Palm(), line, LandmarkAim.Line, PalmFacing, GripReach);
        }
        else yield return LandmarkPair.Facing($"{prefix}.palm", hand.Palm(), Landmark.At(place, grip, -along), LandmarkAim.Axis, HaftOut, GripReach);
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
