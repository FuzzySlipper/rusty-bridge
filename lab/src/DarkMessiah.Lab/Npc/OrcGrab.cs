using DarkMessiah.Lab.Donor;

namespace DarkMessiah.Lab.Npc;

/// <summary>
/// The orc's grab (#10185; docs/lab.md, "The orc's grab"): server.dll's swordsmen (<c>CMM_Npc_BaseSwordman</c>, the orc
/// among them) keep the times of the player's blows they block, the newest <see cref="Blocks"/> of them (+0x2E0310), and
/// at their attack choice (vtable slot 903, +0x2AFF10) grab when the oldest of those is under <see cref="Window"/> s old,
/// before any other choice, the list cleared (+0x2AFF6B to +0x2AFF93): the <c>.qct</c>'s <c>NbHitBlockedToGrab</c> (4)
/// and <c>HitsBlockedToGrabDelay</c> (10). Recorded (<c>attack_grab</c>, 18 times in 14 fights): 11 of the 12 grabs no
/// sword contest led to came as the orc's 4th parry of the player's blows in 10 s did, 0.06 to 1.2 s after; the other
/// six came 0.48 s after the orc won a sword contest (<c>versus_right_out_win</c>, the <c>.qct</c>'s
/// <c>perc_grab_after_wVSw</c> 0.9), which the lab hasn't. The grab took the hands at its event 2137 (cycle 0.067, 0.25 s
/// in; 16 of 18: the other two were cut short then); the orc left it at <see cref="End"/> (3.60 s). Armed, it grabbed
/// from 33 to 89 units off (12 of the 21 attacks it began armed; the 5 from 96 units on were other attacks:
/// <see cref="Reach"/>), and never with the player's shield in hand (the shield run: armed three times within reach,
/// no grab), which the attack choice read doesn't show (its caller may): <see cref="PlayerShielded"/>. Unarmed, it
/// grabbed once in 1359 attacks.
/// </summary>
internal sealed record GrabData(int Blocks, float Window, float Reach = 90, float End = .95f)
{
    internal const string Clip = "attack_grab";
    /// <summary>The grab's event where it takes the player.</summary>
    internal const int Connect = 2137;

    /// <summary>The grab of a model whose <c>.qct</c> sets it (the orc's), else null.</summary>
    internal static GrabData? For(DonorContent donor, string model) =>
        donor.Number(model, "entity_data", "NbHitBlockedToGrab") is float blocks && donor.Number(model, "entity_data", "HitsBlockedToGrabDelay") is float window
            ? new((int)blocks, window) : null;
}

internal sealed partial class Orc
{
    GrabData? grab;
    // The player's blows it blocked, newest first, at most Blocks of them (its own clock's seconds).
    readonly List<double> blockedAt = [];

    /// <summary>Its grab's numbers, or null for an NPC that doesn't grab (#10185).</summary>
    internal GrabData? Grab => grab;
    /// <summary>The player has a shield in hand: no grab (#10185), as none came in the recorded shield fight.</summary>
    internal bool PlayerShielded { get; set; }
    /// <summary>Its grab took the player this step: its <see cref="GrabData.Connect"/> event crossed.</summary>
    internal bool GrabbedNow { get; private set; }
    /// <summary>How many of the player's blows it has blocked toward a grab, the newest <see cref="GrabData.Blocks"/>.</summary>
    internal int BlockedTowardGrab => blockedAt.Count;

    void ResetGrab()
    {
        grab = Kind.Caster ? null : GrabData.For(donor, Kind.Model);
        blockedAt.Clear();
        GrabbedNow = false;
    }

    /// <summary>It parried or blocked a blow of the player's (#10185): the time goes to the front of its list.</summary>
    internal void BlockedPlayer()
    {
        if (grab is not { } g) return;
        blockedAt.Insert(0, clock);
        if (blockedAt.Count > g.Blocks) blockedAt.RemoveAt(blockedAt.Count - 1);
    }

    /// <summary>Its list is full and its oldest under the window: its next attack choice grabs, if the player is within reach.</summary>
    bool GrabArmed(float units) =>
        grab is { } g && !Hold && !PlayerShielded && blockedAt.Count >= g.Blocks && clock - blockedAt[g.Blocks - 1] < g.Window && units <= g.Reach && ClipNamed(GrabData.Clip) is not null;

    /// <summary>The grab, in place of its attack choice; the list cleared.</summary>
    void StartGrab()
    {
        blockedAt.Clear();
        Layer = null;
        Play(OrcState.Attack, ClipNamed(GrabData.Clip)!);
        AttacksMade++;
    }

    /// <summary>The grab's step: it takes the player at its event, and leaves it at <see cref="GrabData.End"/>.</summary>
    bool StepGrab(float before)
    {
        if (grab is not { } g || !Clip.Equals(GrabData.Clip, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (float at in body.Model.Clip(Clip).At(GrabData.Connect))
            if (before < at && Cycle >= at) GrabbedNow = true;
        if (Cycle >= g.End) Play(OrcState.Ready, "combatidle");
        return true;
    }
}
