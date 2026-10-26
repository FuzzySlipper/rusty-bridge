using System.Numerics;
using DarkMessiah.Lab.Donor;
using Rusty.Engine;
using RustyBridge.Wire.Donor;

namespace DarkMessiah.Lab.Sword;

/// <summary>
/// The player's first-person arms and weapon, as Dark Messiah draws them: Arkane's <c>first_basic</c> arms with the
/// move's clip laid over their class's idle, their origin at the eye and turned with the head (<see cref="HeadMotion"/>),
/// the camera 2.1 units ahead of the eye and 9 above it (#10161, <see cref="CameraOffset"/>: the donor's view model sits
/// at the eye, its drawn camera there, both turned by the head, the strafe roll, <see cref="HeadMotion.StrafeRoll"/>, and
/// the lean), and the weapon
/// (the build's, <see cref="Equip"/>; the short sword until one is) on the attachment the donor parents it to
/// (<see cref="HandOf"/>), the daggers' second blade on <c>RightHand_Daggers</c>, a shield (<see cref="Shield"/>) on
/// <c>Leftshield</c> (attachment 6, which the donor parents <c>weapon_arxshield</c> to: <c>l01_a-v17-shield</c>), each
/// seated by its own <c>handle</c> attachment where it has one (<see cref="Seated"/>, #10395). It also places the blade
/// for the swing traces: the donor traces from the eye toward the blade, out to the weapon's <c>rangeattack</c> (100
/// units for the short sword), and records each blow's trace that way.
/// </summary>
/// <summary>
/// The first-person camera against the eye and the look (R10161-3): its offset in the look's frame (X right, Y up, Z
/// ahead) and its turn, Source degrees (pitch down, yaw left, roll right side down) about the look's axes.
/// </summary>
internal readonly record struct CameraRig(Vector3 Offset, (float Pitch, float Yaw, float Roll) Turn)
{
    /// <summary>The camera for an eye at <paramref name="eye"/> looking along <paramref name="forward"/>.</summary>
    internal (Vector3 Position, Vector3 Forward, Vector3 Up) At(Vector3 eye, Vector3 forward)
    {
        var look = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
        var up = Vector3.Cross(right, look);
        var turn = HeadMotion.Rotation(Turn, look, up);
        return (eye + right * Offset.X + up * Offset.Y + look * Offset.Z, Vector3.Transform(look, turn), Vector3.Transform(up, turn));
    }
}

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
    // The shield on the arm (null for none).
    Puppet? shield;
    (string Joint, Vector3 Tip) blade;
    float idleCycle;
    readonly HeadMotion head;
    SwordMove lastMove;

    /// <summary>
    /// Where the donor's camera sits from the eye, Source units in the view's frame (X right, Y up, Z ahead), before the
    /// head turns it (R10161-4): 9 above and 2.1 ahead on 5057 of 6000-odd ticks of <c>l01_a-v18-movement</c>, crouched
    /// (the eye 35 units up, not 61) as standing, and through every swing and kick of the hand fights. Neither the arms'
    /// animated <c>eyes</c> attachment (6.8 up, 4 to 6 ahead, moving with each clip) nor its reference pose's is it.
    /// </summary>
    internal static readonly Vector3 CameraOffset = new(0, 9, 2.1f);

    internal FirstPerson(IEngineContext engine, DonorContent donor)
    {
        this.engine = engine;
        this.donor = donor;
        arms = new Puppet(engine, donor.Model(SwordClips.Model), ArmsId);
        Weapon = LabWeapon.Of(donor, LabWeapon.ShortSword);
        sword = weapons[Weapon.Model] = new Puppet(engine, donor.Model(Weapon.Model), SwordId);
        blade = Rig.BladeTip(sword.Model.Info);
        head = new HeadMotion(engine, donor);
    }

    /// <summary>The head's motion (#10161).</summary>
    internal HeadMotion Head => head;
    /// <summary>The camera as last posed: <see cref="CameraOffset"/> from the eye, looking along the head-turned view.</summary>
    internal (Vector3 Position, Vector3 Forward, Vector3 Up) Camera { get; private set; } = (Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);

    /// <summary>
    /// The camera as last posed, against the eye and the look (R10161-3): its offset from the eye in the look's frame (X
    /// right, Y up, Z ahead), and its turn, Source degrees, about the look's axes (<see cref="HeadMotion.Rotation"/>). The
    /// player's camera composes it with the current eye and look whenever it's drawn, so looking about while the world
    /// holds turns the view.
    /// </summary>
    internal CameraRig View { get; private set; } = new(Vector3.Zero, (0, 0, 0));

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

    /// <summary>The shield on the arm, or null: a sword with one plays <c>sword_shield</c>'s clips (<see cref="Clips"/>).</summary>
    internal LabShield? Shield { get; private set; }

    /// <summary>Puts <paramref name="next"/> on the arm (null takes it off): its model is drawn from now.</summary>
    internal void Wear(LabShield? next)
    {
        Shield = next;
        shield = next is null ? null : Loaded(next.Model);
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

    /// <summary>A weapon's clips from the arms' model for its class, at its speed, with the shield's set while one is worn.</summary>
    internal SwordClips Clips(LabWeapon weapon, CombatTuning tuning) =>
        SwordClips.From(ArmsModel.Info, weapon.Class, weapon.AttackSpeed, tuning, weapon.Simple, Shield is not null);

    internal DonorModel ArmsModel => arms.Model;
    /// <summary>Where the blade's tip is, as last posed.</summary>
    internal Vector3 Tip { get; private set; }
    internal Transform Arms { get; private set; }
    internal Transform Sword { get; private set; }
    /// <summary>The daggers' second blade, as last posed; null for a weapon held in one hand.</summary>
    internal Transform? OffHand { get; private set; }
    /// <summary>The shield, as last posed; null for none.</summary>
    internal Transform? ShieldAt { get; private set; }

    /// <summary>
    /// Poses the arms for the sword's move at the eye, looking along <paramref name="forward"/>: the idle runs on its
    /// own, the move's clip over it, the head turns them and the camera (#10161). Places the sword and its tip.
    /// </summary>
    /// <param name="airborne">The body is off the ground, and <paramref name="velocity"/> its velocity: the head's fall.</param>
    /// <param name="lean">The lean (<see cref="Player.PlayerLean"/>), −1 left to 1 right: the arms and camera stand at the eye
    /// moved to that side, the view rolled.</param>
    internal void Pose(PlayerSword move, float seconds, Vector3 eye, Vector3 forward, bool airborne = false, Vector3 velocity = default, float lean = 0)
    {
        // A spell in hand (#10165): its ready loop, and the cast over it while one forms; the sword away. The bow (#10168):
        // its rest, and its draw, shot and reload over it.
        var idle = arms.Model.Clip(Spell is { } spell ? spell.Ready : Bow is not null ? Ranged.BowRules.Rest : move.Clips.Idle.Clip);
        idleCycle = (idleCycle + (idle.Seconds > 0 ? seconds / idle.Seconds : 0)) % 1;
        if (Bow is { } bow) { if (bow.Clip == Ranged.BowRules.Rest) arms.Play(idle.Name, idleCycle); else arms.Play(idle.Name, idleCycle, (bow.Clip, bow.Cycle, 1)); }
        else if (Spell is { Cast: { } cast } casting) arms.Play(idle.Name, idleCycle, (cast, casting.Cycle, 1));
        else if (Spell is not null || move.Move == SwordMove.Ready) arms.Play(idle.Name, idleCycle);
        else arms.Play(idle.Name, idleCycle, (move.Clip.Clip, move.Traced ? move.TraceCycle : move.Cycle, 1));
        // The head turns the view (#10161); the arms stand at the eye, turned with it, and the camera above it.
        if (move.Move == SwordMove.Away && lastMove != SwordMove.Away) head.Grabbed();
        lastMove = move.Move;
        if (Bow is { } held) head.Step(seconds, held.Head is { } h ? (h.Clip, h.Cycle) : null, airborne, velocity, held.Head?.Weight ?? 1);
        else head.Step(seconds, move.Move == SwordMove.Ready ? null : (move.Clip.Clip, move.Traced ? move.TraceCycle : move.Cycle), airborne, velocity);
        var look = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(look, Vector3.UnitY));
        var up = Vector3.Cross(right, look);
        var turned = head.Turn with { Roll = head.Turn.Roll + HeadMotion.StrafeRoll(velocity, look) + lean * Player.PlayerLean.Angle };
        var leaned = eye + right * (lean * Player.PlayerLean.Move * SourceSpace.MetresPerUnit);
        var turn = HeadMotion.Rotation(turned, look, up);
        var turnedForward = Vector3.Transform(look, turn);
        var turnedUp = Vector3.Transform(up, turn);
        // The arms pitch with the view by ViewmodelPitch of its pitch, turned by the head as the camera is.
        var armsLook = PitchedLook(look);
        var armsUp = Vector3.Cross(right, armsLook);
        var armsTurn = HeadMotion.Rotation(turned, armsLook, armsUp);
        Arms = new Transform(leaned, Rig.Facing(Vector3.Transform(armsLook, armsTurn), Vector3.Transform(armsUp, armsTurn)), Vector3.One);
        var pose = Rig.Pose(engine, arms, Arms);
        // The camera: the measured offset from the (leaned) eye, turned by the head and the lean.
        var rest = CameraOffset * SourceSpace.MetresPerUnit;
        Camera = (leaned + Vector3.Transform(right * rest.X + up * rest.Y + look * rest.Z, turn), turnedForward, turnedUp);
        var offset = Camera.Position - eye;
        View = new CameraRig(new Vector3(Vector3.Dot(offset, right), Vector3.Dot(offset, up), Vector3.Dot(offset, look)), turned);
        var (main, other) = HandOf(Weapon.Class);
        Sword = Seated(sword, Rig.Attached(arms.Model, pose, main) ?? Arms);
        OffHand = offHand is not null && other is not null ? Seated(offHand, Rig.Attached(arms.Model, pose, other) ?? Arms) : null;
        ShieldAt = shield is not null ? Seated(shield, Rig.Attached(arms.Model, pose, "Leftshield") ?? Arms) : null;
        if (Bow is { } drawn)
        {
            bowPuppet = Loaded(drawn.Model);
            arrowPuppet = Loaded(ArrowModel);
            BowAt = Rig.Attached(arms.Model, pose, "LeftHand_Bow") ?? Arms;
            ArrowAt = drawn.Nocked ? Rig.Attached(arms.Model, pose, "RightHand_Arrows") ?? Arms : null;
        }
        else BowAt = ArrowAt = null;
        var swordPose = Rig.Rest(engine, sword.Instance, Sword);
        Tip = sword.Model.Joints.TryGetValue(blade.Joint, out int j) ? Vector3.Transform(blade.Tip, swordPose[j].World.Rotation) + swordPose[j].World.Translation : Sword.Translation;
    }

    /// <summary>
    /// The arms' <c>eyes</c> attachment with <paramref name="clip"/> at <paramref name="cycle"/> played alone, in the arms
    /// model's frame facing <paramref name="forward"/> (#10161): what the donor's camera follows.
    /// </summary>
    internal Transform? EyesAt(string clip, float cycle, Vector3 forward)
    {
        arms.Play(clip, cycle);
        var facing = new Transform(Vector3.Zero, Rig.Facing(forward, Vector3.UnitY), Vector3.One);
        return Rig.Attached(arms.Model, Rig.Pose(engine, arms, facing), "eyes");
    }

    /// <summary>The blade's trace now: from the eye toward the tip, out to the sword's range.</summary>
    internal (Vector3 Start, Vector3 End) Trace(Vector3 eye)
    {
        var toward = Tip - eye;
        return (eye, eye + (toward.LengthSquared() > 1e-6f ? Vector3.Normalize(toward) : Vector3.UnitZ) * Range);
    }

    /// <summary>
    /// The spell in hand, for the arms (#10165): its ready loop, and the cast clip at its cycle while one forms; null
    /// for the weapon.
    /// </summary>
    internal (string Ready, string? Cast, float Cycle)? Spell { get; set; }

    /// <summary>
    /// The bow in hand, for the arms (#10168): its model, the arms' clip at its cycle, whether an arrow is nocked, and
    /// the head's clip (the draw's dip or the sway) at its cycle and weight; null for the weapon.
    /// </summary>
    internal (string Model, string Clip, float Cycle, bool Nocked, (string Clip, float Cycle, float Weight)? Head)? Bow { get; set; }

    /// <summary>The arrow drawn nocked: the classic arrow, every bow's.</summary>
    internal const string ArrowModel = "models/items/weapons/arrows/arrow_classic.mdl";
    Puppet? bowPuppet, arrowPuppet;
    /// <summary>The bow and the nocked arrow, as last posed; null without them.</summary>
    internal Transform? BowAt { get; private set; }
    internal Transform? ArrowAt { get; private set; }

    /// <summary>A clip's length in seconds on the arms (0 when the model lacks it).</summary>
    internal float ClipSeconds(string clip) => arms.Model.Clip(clip).Seconds;

    /// <summary>
    /// Where an attachment of the arms is with <paramref name="clip"/> held at <paramref name="cycle"/> alone and the arms
    /// at <paramref name="at"/> (#10395).
    /// </summary>
    internal Transform? AttachedAt(string clip, float cycle, Transform at, string attachment) =>
        arms.Model.Info.Clips.ContainsKey(clip)
            ? Rig.Attached(arms.Model, engine.Animation.SampleJointPose(AnimationPoseSampleRequest.OfClip(arms.Instance, clip, Math.Clamp(cycle, 0, 1), at, normalized: true, applyPoseControls: false)).ToArray(), attachment)
            : null;

    /// <summary>
    /// How much of the view's pitch the arms take (#10395): the donor's view model pitched 0.399 of the client's view
    /// (the 10th to 90th percentile 0.397 to 0.403, 110 samples of <c>l01_a-v14-arena-hand</c> looking 5° or more up or
    /// down; <c>held-look</c>'s 30° down, 12°), its yaw the view's. The camera takes the whole view.
    /// </summary>
    internal const float ViewmodelPitch = .4f;

    /// <summary>The arms' look for a view along <paramref name="look"/>: its heading, and <see cref="ViewmodelPitch"/> of its pitch.</summary>
    internal static Vector3 PitchedLook(Vector3 look)
    {
        var flat = look with { Y = 0 };
        if (flat.LengthSquared() < 1e-8f) return look;
        float pitch = MathF.Asin(Math.Clamp(look.Y, -1, 1)) * ViewmodelPitch;
        return Vector3.Normalize(flat) * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch);
    }

    // Each held model's handle attachment, in its model frame at rest; null for a model without one.
    readonly Dictionary<Puppet, Transform?> handles = [];

    /// <summary>
    /// Where a held item is drawn in first person: its own <c>handle</c> attachment brought to the hand's attachment
    /// (<paramref name="hand"/>), not its origin. Dark Messiah's view model draws its weapons so (#10395): posed as the
    /// recorded view model was, the short sword hung by its origin points right, out of view at rest, where the game's
    /// own screenshots (<c>held-look</c>) show it rising from below the frame to (910, 330) of 1280×720; seated by its
    /// handle (a 120° turn of its axes for every sword, the identity for the daggers) it rises to (907, 312). The
    /// server's weapon entity and the NPCs' items hang by their origin (the recordings, within 1°), and so does the
    /// mirror's.
    /// </summary>
    internal Transform Seated(Puppet item, Transform hand)
    {
        if (!handles.TryGetValue(item, out var handle))
        {
            // The handle in the model's frame at rest; with none, a one-joint item's joint, which the hand then holds.
            var rest = Rig.Rest(engine, item.Instance, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One));
            handles[item] = handle = item.Model.Info.Attachments.ContainsKey("handle") ? Rig.Attached(item.Model, rest, "handle")
                : rest.Length == 1 ? rest[0].World : null;
        }
        if (handle is not Transform h) return hand;
        var turn = Quaternion.Inverse(h.Rotation);
        return new Transform(hand.Translation - Vector3.Transform(Vector3.Transform(h.Translation, turn), hand.Rotation),
            Quaternion.Normalize(hand.Rotation * turn), hand.Scale);
    }

    /// <summary>
    /// Where the weapon's grip and blade tip are, in the arms' frame (glTF: +Z ahead, +Y up, +X to the left), with
    /// <paramref name="clip"/> held at <paramref name="cycle"/>, seated by its handle or, with <paramref name="seated"/>
    /// off, hung by its origin (#10395).
    /// </summary>
    internal (Vector3 Grip, Vector3 Tip)? BladeIn(string clip, float cycle, bool seated = true)
    {
        var (main, _) = HandOf(Weapon.Class);
        if (AttachedAt(clip, cycle, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), main) is not Transform hand) return null;
        var pose = Rig.Rest(engine, sword.Instance, seated ? Seated(sword, hand) : hand);
        if (!sword.Model.Joints.TryGetValue(blade.Joint, out int j)) return null;
        return (pose[j].World.Translation, Vector3.Transform(blade.Tip, pose[j].World.Rotation) + pose[j].World.Translation);
    }

    /// <summary>
    /// The held item's collision hull points (<paramref name="shield"/>: the shield, else the weapon) with the arms at
    /// <paramref name="armsAt"/> holding <paramref name="clip"/> at <paramref name="cycle"/>, the item seated
    /// (<see cref="Seated"/>) or hung by its origin (#10395: drawn over the donor's screenshots).
    /// </summary>
    internal List<Vector3> HullAt(bool shield, string clip, float cycle, Transform armsAt, bool seated)
    {
        var item = shield ? this.shield : sword;
        if (item is null) return [];
        var (main, _) = HandOf(Weapon.Class);
        if (AttachedAt(clip, cycle, armsAt, shield ? "Leftshield" : main) is not Transform hand) return [];
        var pose = Rig.Rest(engine, item.Instance, seated ? Seated(item, hand) : hand);
        var points = new List<Vector3>();
        foreach (var hull in item.Model.Info.Collision)
            if (item.Model.Joints.TryGetValue(hull.Joint, out int j))
                points.AddRange(hull.Points.Select(p => Vector3.Transform(p, pose[j].World.Rotation) + pose[j].World.Translation));
        return points;
    }

    internal IEnumerable<AppearanceFact> Facts()
    {
        yield return arms.Fact(Arms);
        if (Spell is not null) yield break;
        if (Bow is not null)
        {
            if (bowPuppet is not null && BowAt is { } bowAt) yield return bowPuppet.Fact(bowAt);
            if (arrowPuppet is not null && ArrowAt is { } arrowAt) yield return arrowPuppet.Fact(arrowAt);
            yield break;
        }
        yield return sword.Fact(Sword);
        if (offHand is not null && OffHand is { } left) yield return offHand.Fact(left);
        if (shield is not null && ShieldAt is { } arm) yield return shield.Fact(arm);
    }

    public void Dispose()
    {
        arms.Dispose();
        head.Dispose();
        foreach (var weapon in weapons.Values) weapon.Dispose();
    }
}
