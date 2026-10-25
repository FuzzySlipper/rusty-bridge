using System.Runtime.InteropServices;

namespace RustyBridge.Wire;

// Mirror of tap/darkmessiah/src/wire.h (Wire v7). Layout and protocol:
// docs/wire.md. Keep the two in step.
public static class WireLayout
{
    public const ulong Magic = 0x3130455249574252ul; // "RBWIRE01"
    public const uint Version = 19;

    public const int SlotCount = 128;
    public const int MaxEntities = 2048;
    public const int MaxClasses = 1024;
    public const int ClassNameBytes = 64;
    public const int MaxModels = 4096;
    public const int ModelPathBytes = 128;
    public const int MaxDamage = 64;
    public const int MaxDefense = 32;
    public const int MaxLayers = 256;
    public const int MaxSlowTime = 16;
    public const int MaxSweeps = 32;
    public const ushort NoEntity = 0xFFFF;

    public const int ClassesOffset = 256;
    public const int ModelsOffset = ClassesOffset + MaxClasses * ClassNameBytes;
    public const int SlotsOffset = ModelsOffset + MaxModels * ModelPathBytes;
    public static readonly int DamageOffset = Marshal.SizeOf<WireSlotHeader>() + MaxEntities * Marshal.SizeOf<WireEntity>();
    public static readonly int DefenseOffset = DamageOffset + MaxDamage * Marshal.SizeOf<WireDamage>();
    public static readonly int LayersOffset = DefenseOffset + MaxDefense * Marshal.SizeOf<WireDefense>();
    public static readonly int SlowTimeOffset = LayersOffset + MaxLayers * Marshal.SizeOf<WireLayer>();
    public static readonly int ClientViewOffset = SlowTimeOffset + MaxSlowTime * Marshal.SizeOf<WireSlowTime>();
    /// <summary>v12: the player's input, after the client view.</summary>
    public static readonly int InputOffset = ClientViewOffset + Marshal.SizeOf<WireClientView>();
    /// <summary>v15: the player's stats and equipment, after the input.</summary>
    public static readonly int StatsOffset = InputOffset + Marshal.SizeOf<WirePlayerInput>();
    /// <summary>v17: the melee sweeps since the previous frame (count, dropped, then the records), after the stats.</summary>
    public static readonly int SweepsOffset = StatsOffset + Marshal.SizeOf<WirePlayerStats>();
    public static readonly int SlotBytes = SweepsOffset + 8 + MaxSweeps * Marshal.SizeOf<WireSweep>();
    public static readonly long FileBytes = SlotsOffset + (long)SlotCount * SlotBytes;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WireHeader
{
    public ulong Magic;
    public uint Version;
    public uint HeaderBytes;
    public uint SlotCount;
    public uint SlotBytes;
    public uint MaxEntities;
    public uint MaxClasses;
    public uint ClassNameBytes;
    public uint MaxModels;
    public uint ModelPathBytes;
    public uint ClassesOffset;
    public uint ModelsOffset;
    public uint SlotsOffset;
    public uint WriterPid;
    public uint LevelSerial;
    public float TickInterval;
    public uint Reserved;
    public fixed byte Map[64];
    public ulong FramesWritten;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireSlotHeader
{
    public ulong Seq;
    public ulong Frame;
    public int Tick;
    public float Curtime;
    public uint LevelSerial;
    public uint EntityCount;
    public uint DamageCount;
    /// <summary>Damage records lost since the previous frame (more than MaxDamage).</summary>
    public uint DamageDropped;
    public uint DefenseCount;
    public uint DefenseDropped;
    public uint LayerCount;
    /// <summary>Active layers that didn't fit in the slot (over MaxLayers).</summary>
    public uint LayerDropped;
    // v7
    /// <summary>The tap's wall clock at the write (seconds since it loaded); against Curtime it shows the donor's time scale.</summary>
    public double WallSeconds;
    public uint SlowTimeCount;
    public uint SlowTimeDropped;
    public WirePlayerState Player;
}

/// <summary>
/// The player's slow motion and adrenaline at a tick (CMM_Player fields; Wire v7).
/// <see cref="Index"/> is <see cref="WireLayout.NoEntity"/> when there is no player.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WirePlayerState
{
    public ushort Index;
    /// <summary>m_bInBulletTime: slow motion is on.</summary>
    public byte InBulletTime;
    public byte BulletTimeSound;
    /// <summary>m_fBulletTimeStart: the curtime slow motion starts at; at or below 0 when none is pending.</summary>
    public float BulletTimeStart;
    public float BulletTimeDuration;
    /// <summary>m_fBulletTimeFactor: the time scale it sets.</summary>
    public float BulletTimeFactor;
    public float Adrenaline;
    public float MaxAdrenaline;
}

/// <summary>What the client's view flags say was read.</summary>
[Flags]
public enum ClientViewFlags : uint
{
    None = 0,
    ViewAngles = 1,
    Player = 2,
    Viewmodel = 4,
    ViewOffset = 8,
    /// <summary>v9: the camera the client last drew with.</summary>
    Camera = 16,
    /// <summary>v9: the client viewmodel's own sequence and cycle.</summary>
    ViewmodelAnimation = 32,
    /// <summary>v9: the client viewmodel's overlay layers.</summary>
    ViewmodelLayers = 64,
}

/// <summary>A client viewmodel overlay layer with weight (Wire v9).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireClientLayer
{
    public short Sequence;
    public short Order;
    public float Cycle;
    public float Weight;
}

/// <summary>
/// The client's view of the local player (Wire v8): read from client.dll and
/// engine.dll in the donor's process, as of the client's last frame. The eye
/// is <see cref="Origin"/> + <see cref="ViewOffset"/>, looking along
/// <see cref="ViewAngles"/>. docs/wire.md, "The client's view".
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WireClientView
{
    public ushort Player;
    public ushort Viewmodel;
    public ClientViewFlags Flags;
    public float ViewPitch, ViewYaw, ViewRoll;
    public float OriginX, OriginY, OriginZ;
    public float Pitch, Yaw, Roll;
    public float ViewOffsetX, ViewOffsetY, ViewOffsetZ;
    public float ViewmodelX, ViewmodelY, ViewmodelZ;
    public float ViewmodelPitch, ViewmodelYaw, ViewmodelRoll;
    // v9: the client's CViewSetup (the effective camera) and the client viewmodel's animation.
    public float CameraX, CameraY, CameraZ;
    public float CameraPitch, CameraYaw, CameraRoll;
    /// <summary>Horizontal, degrees.</summary>
    public float Fov;
    public float FovViewmodel;
    public ushort ScreenWidth, ScreenHeight;
    public float ZNear, ZFar;
    public short ViewmodelSequence;
    public ushort Reserved;
    public float ViewmodelCycle;
    public byte LayerCount;
    public byte Reserved2, Reserved3, Reserved4;
    fixed byte _layers[MaxLayers * 12];

    public const int MaxLayers = 8;

    /// <summary>The client viewmodel's overlay layers with weight, in its own order.</summary>
    public readonly WireClientLayer[] ViewmodelLayers
    {
        get
        {
            var result = new WireClientLayer[Math.Min((int)LayerCount, MaxLayers)];
            fixed (byte* p = _layers)
                new ReadOnlySpan<WireClientLayer>(p, result.Length).CopyTo(result);
            return result;
        }
    }

    public void SetViewmodelLayers(ReadOnlySpan<WireClientLayer> layers)
    {
        LayerCount = (byte)Math.Min(layers.Length, MaxLayers);
        fixed (byte* p = _layers)
            layers[..LayerCount].CopyTo(new Span<WireClientLayer>(p, MaxLayers));
    }

    /// <summary>The v8 record: everything up to the viewmodel's angles.</summary>
    public const int V8Bytes = 80;

    public static WireClientView None => new() { Player = WireLayout.NoEntity, Viewmodel = WireLayout.NoEntity, ViewmodelSequence = -1 };
}

/// <summary>Which bullet-time function a <see cref="WireSlowTime"/> record caught.</summary>
public enum SlowTimeSource : byte
{
    /// <summary>CMM_Player's bullet-time virtual (server.dll+0x146E80): Start is absolute.</summary>
    Setter = 0,
    /// <summary>The static request (server.dll+0x147560): Start is a delay, and the game stores Duration × Factor.</summary>
    Request = 1,
}

/// <summary>
/// One melee sweep (Wire v17), mirroring <c>wire::Sweep</c>: the box server.dll+0x3CB310 sweeps from Start along Direction for
/// Range, as its hook at +0x3CB5C8 caught it (docs/wire.md, "Melee sweeps"). Type picks the box's half-size ConVar.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireSweep
{
    public int Tick;
    public ushort Attacker;
    public byte Type;
    /// <summary><see cref="HeadEntity"/>: the player held the entity at +0x2388, whose frame turns its aim.</summary>
    public byte Flags;
    public float StartX, StartY, StartZ;
    public float DirectionX, DirectionY, DirectionZ;
    public float Range;
    /// <summary>The player's angles at +0x1F88 (pitch, yaw, roll); zero for an NPC.</summary>
    public float ViewPitch, ViewYaw, ViewRoll;
    /// <summary>The server.dll RVA the sweep returns to, or <see cref="WireSlowTime.CallerOutside"/>.</summary>
    public uint Caller;
    public uint AttackerSerial;

    public const byte HeadEntity = 1;
}

/// <summary>One bullet-time call (Wire v7): the slow motion asked for and the server.dll RVA that asked.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireSlowTime
{
    public int Tick;
    public ushort Player;
    public byte Sound;
    /// <summary>Which function was called: the player's setter or the static request (<see cref="SlowTimeSource"/>).</summary>
    public SlowTimeSource Source;
    /// <summary>Setter: the curtime slow motion starts at. Request: the delay from now.</summary>
    public float Start;
    public float Duration;
    public float Factor;
    /// <summary>The server.dll RVA the call returns to, or <see cref="CallerOutside"/>.</summary>
    public uint Caller;
    public uint PlayerSerial;
    public uint Reserved2;

    /// <summary>
    /// A return address outside server.dll: Arkane's copy-protected code (the
    /// .sll section, such as mm_player_inputs' ActiveSlowMotion input) runs from
    /// a copy in allocated memory. Taps before 2026-10-18 21:50 wrote such a
    /// caller as a meaningless RVA.
    /// </summary>
    public const uint CallerOutside = uint.MaxValue;

    /// <summary>The caller as text: <c>server.dll+0x…</c>, or "outside server.dll".</summary>
    public static string CallerName(uint caller) => caller == CallerOutside ? "outside server.dll" : $"server.dll+0x{caller:X}";
}

[Flags]
public enum WireEntityFlags : byte
{
    None = 0,
    /// <summary>Health, MaxHealth and LifeState are valid.</summary>
    HasHealth = 1,
    /// <summary>Sequence, Cycle and PlaybackRate are valid.</summary>
    HasAnimation = 2,
    /// <summary>v18: Alert and NpcState are valid (npc_* entities).</summary>
    HasAlert = 4,
    /// <summary>v19: Size and Shrinked are valid (classes that send m_fMMSize).</summary>
    HasSize = 8,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireEntity
{
    public ushort Index;
    public ushort ClassId;
    public int ModelIndex;
    public float OriginX, OriginY, OriginZ;
    public float Pitch, Yaw, Roll;
    /// <summary>CBaseEntity::m_fHealth (Arkane's float health).</summary>
    public float Health;
    public float MaxHealth;
    /// <summary>Index into the model's virtual sequence list; -1 when not animating.</summary>
    public short Sequence;
    public WireEntityFlags Flags;
    /// <summary>0 alive, 1 dying, 2 dead.</summary>
    public byte LifeState;
    /// <summary>0..1 through the current sequence.</summary>
    public float Cycle;
    public float PlaybackRate;
    /// <summary>The entity's EHANDLE serial (handle >> 12). With <c>Index</c> it identifies the
    /// entity: an index reused by a new entity gets a new serial.</summary>
    public uint Serial;
    // v7
    /// <summary>
    /// The entity this one stands in for or with: an NPC's knockdown ragdoll
    /// (m_hRagdollFall), a prop_ragdoll's NPC (m_hUnragdoll).
    /// <see cref="WireLayout.NoEntity"/> when none.
    /// </summary>
    public ushort Link;
    /// <summary>v18 (reserved before, zero): an npc_*'s alert level, the magnitude of +0x36EC, as the damage record's <see cref="WireDamage.VictimAlert"/>.</summary>
    public ushort Alert;
    public uint LinkSerial;
    /// <summary>v10: m_hMoveParent, what the entity is attached to; <see cref="WireLayout.NoEntity"/> when none.</summary>
    public ushort Parent;
    /// <summary>v10: m_iParentAttachment, 1-based into the parent model's attachments (the import's <c>asset.extras.attachments</c> order); 0 for none.</summary>
    public byte ParentAttachment;
    /// <summary>v18 (reserved before, zero): an npc_*'s state, +0x1A5C (3 combat), as <see cref="WireDamage.VictimNpcState"/>.</summary>
    public byte NpcState;
    public uint ParentSerial;
    /// <summary>v10: m_hOwnerEntity, or a weapon's m_hOwner when that is empty; <see cref="WireLayout.NoEntity"/> when none.</summary>
    public ushort Owner;
    public ushort Reserved3;
    public uint OwnerSerial;
    // v19
    /// <summary>v19: m_fMMSize (+0x12C), Arkane's render scale: 1 unscaled; a shrink ramps it down before the NPC's model is
    /// swapped for its <c>_shrinked.mdl</c> (docs/wire.md, "Shrink"). 1 in older recordings.</summary>
    public float Size;
    /// <summary>v19: m_iShrinked (+0x130), 1 once the NPC wears its <c>_shrinked.mdl</c>.</summary>
    public int Shrinked;
}

/// <summary>
/// One CBaseEntity::TakeDamage call, from the tap's hook. Field names follow
/// Arkane's CTakeDamageInfo datadesc. Entity fields are edict indices, or
/// <see cref="WireLayout.NoEntity"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WireDamage
{
    /// <summary>MAXSTUDIOPOSEPARAM: a character's pose parameters.</summary>
    public const int PoseParameters = 24;
    /// <summary>How many of the victim's overlays a v13 record carries.</summary>
    public const int VictimLayers = 8;

    public int Tick;
    public ushort Victim, Attacker, Inflictor, TraceEntity;
    /// <summary>m_flDamage as requested; HealthBefore - HealthAfter is what was applied.</summary>
    public float Damage;
    public float HealthBefore, HealthAfter;
    /// <summary>m_bitsDamageType (DMG_*).</summary>
    public uint DamageType;
    public short CustomKill;
    /// <summary>The weapon that struck, when the game named a held weapon as the attacker;
    /// Attacker is then its owner.</summary>
    public ushort Weapon;
    public short HitBox, HitGroup, PhysicsBone;
    public byte TraceValid;
    /// <summary>v11: the attacker's critical byte (+0x21F7) as the call arrived; set, the melee routine doubled the blow.</summary>
    public byte AttackerCritical;
    public float PositionX, PositionY, PositionZ;
    public float ForceX, ForceY, ForceZ;
    public float TraceStartX, TraceStartY, TraceStartZ;
    public float TraceEndX, TraceEndY, TraceEndZ;
    public int PushForce, WeaponFlags, MMDamageFlag, MMSpell;
    public float SlowTimeDelay, SlowTimeDuration, SlowTimeFactor;
    public float MMDirectionX, MMDirectionY, MMDirectionZ;
    /// <summary>EHANDLE serials of the entities above (0 with <see cref="WireLayout.NoEntity"/>).</summary>
    public uint VictimSerial, AttackerSerial, InflictorSerial, TraceEntitySerial, WeaponSerial;
    /// <summary>v11: an NPC victim's alert level (magnitude of +0x36EC), which the melee routine compares with mul_damages_Npc_Not_alerted_level.</summary>
    public short VictimAlert;
    /// <summary>v11: the NPC victim's state (+0x1A5C); 3 never takes the not-alerted multiplier.</summary>
    public byte VictimNpcState;
    /// <summary>v11: 1 when <see cref="VictimAlert"/> and <see cref="VictimNpcState"/> were read (npc_* victims).</summary>
    public byte VictimAlertValid;

    // v13: the victim's animation as the call arrived, before TakeDamage ran: the state the trace that struck it
    // posed. A character's sequence and cycle change only in its own think, so the frame's end can show the next.
    /// <summary>v13: m_nSequence; -1 when not read.</summary>
    public short VictimSequence;
    /// <summary>v13: 1 when the victim's animation fields were read.</summary>
    public byte VictimAnimValid;
    /// <summary>v13: how many of the victim's active overlays follow, at most <see cref="VictimLayers"/>.</summary>
    public byte VictimLayerCount;
    public float VictimCycle, VictimPlaybackRate;
    /// <summary>v13: m_flAnimTime, when the victim's animation last advanced (its last think).</summary>
    public float VictimAnimTime;
    fixed float _victimPose[PoseParameters];
    fixed short _victimLayerSequence[VictimLayers];
    fixed short _victimLayerOrder[VictimLayers];
    fixed float _victimLayerCycle[VictimLayers];
    fixed float _victimLayerWeight[VictimLayers];

    /// <summary>v13: the victim's m_flPoseParameter.</summary>
    public readonly float[] VictimPose
    {
        get { var a = new float[PoseParameters]; for (int i = 0; i < PoseParameters; i++) a[i] = _victimPose[i]; return a; }
    }

    /// <summary>v13: the victim's active overlays as the call arrived, in m_AnimOverlay order.</summary>
    public readonly (short Sequence, short Order, float Cycle, float Weight)[] VictimOverlays
    {
        get
        {
            int n = Math.Min((int)VictimLayerCount, VictimLayers);
            var a = new (short, short, float, float)[n];
            for (int i = 0; i < n; i++) a[i] = (_victimLayerSequence[i], _victimLayerOrder[i], _victimLayerCycle[i], _victimLayerWeight[i]);
            return a;
        }
    }

    /// <summary>Sets the v13 victim fields (tests and writers).</summary>
    public void SetVictimAnimation(short sequence, float cycle, float rate, float animTime, ReadOnlySpan<float> pose,
        ReadOnlySpan<(short Sequence, short Order, float Cycle, float Weight)> overlays)
    {
        VictimSequence = sequence;
        VictimAnimValid = 1;
        VictimCycle = cycle;
        VictimPlaybackRate = rate;
        VictimAnimTime = animTime;
        for (int i = 0; i < PoseParameters; i++) _victimPose[i] = i < pose.Length ? pose[i] : 0;
        VictimLayerCount = (byte)Math.Min(overlays.Length, VictimLayers);
        for (int i = 0; i < VictimLayerCount; i++)
            (_victimLayerSequence[i], _victimLayerOrder[i], _victimLayerCycle[i], _victimLayerWeight[i]) = overlays[i];
    }
}

/// <summary>An Arkane NPC's verdict on a melee blow (CNPC_BaseArx's defense virtual).</summary>
public enum DefenseResult
{
    /// <summary>Not produced by CNPC_BaseArx; other classes answer 0.</summary>
    None = 0,
    /// <summary>The blow struck the NPC's weapon, or it was in its parry/block state.</summary>
    Parry = 1,
    /// <summary>Checked and not defended.</summary>
    Hit = 2,
    /// <summary>The blow does nothing; the game zeroes its damage.</summary>
    NoEffect = 4,
    /// <summary>The blow struck its off-hand item, or its shield was up and the attack came from the front.</summary>
    Shield = 8,
    /// <summary>The player parried (parry state set to PARRY_STATE_PARRY).</summary>
    PlayerParried = 17,
    /// <summary>A blow broke the player's parry (PARRY_STATE_BREAKPARRY).</summary>
    PlayerParryBroken = 18,
}

/// <summary>
/// One melee defense outcome: an Arkane NPC's verdict (the tap's detour of CNPC_BaseArx's
/// defense virtual) or a change of the player's parry state (CBasePlayer slot 369).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireDefense
{
    public int Tick;
    public ushort Victim, Attacker, Weapon;
    /// <summary>The entity the blow struck: the victim, or its weapon or shield.</summary>
    public ushort Struck;
    public DefenseResult Result;
    public float DirectionX, DirectionY, DirectionZ;
    /// <summary>Player records: server.dll RVA of the code that set the parry state.</summary>
    public uint Source;
    /// <summary>EHANDLE serials of the entities above (0 with <see cref="WireLayout.NoEntity"/>).</summary>
    public uint VictimSerial, AttackerSerial, WeaponSerial, StruckSerial;
    /// <summary>v11: the NPC's defense state as its verdict read it (0x4 parry/block, 0x10 shield up); see <see cref="StateValid"/>.</summary>
    public uint VictimState;
    /// <summary>v11: 1 when <see cref="VictimState"/> was read (NPC verdicts with the state getter found).</summary>
    public byte StateValid;
    /// <summary>v11: the NPC's no-effect byte (+0x2961): set, the verdict is 4 whatever the blow struck.</summary>
    public byte NoEffect;
    public ushort Reserved;
}

/// <summary>
/// One active animation overlay layer (a gesture, parry or block playing over an
/// entity's main sequence), from CBaseAnimatingOverlay::m_AnimOverlay. Only layers
/// that are active with a weight above zero are sent.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireLayer
{
    /// <summary>Edict index of the entity it plays on.</summary>
    public ushort Index;
    /// <summary>Position in the entity's overlay vector.</summary>
    public byte Slot;
    /// <summary>m_fFlags: 1 active, 2 autokill, 4 kill me, 32 dying.</summary>
    public byte Flags;
    /// <summary>Index into the entity's model's sequence list.</summary>
    public short Sequence;
    /// <summary>Blend order among the entity's layers.</summary>
    public short Order;
    public float Cycle;
    public float Weight;
    public float PlaybackRate;
}

/// <summary>
/// v12: the player's input as the server ran it in the tick, mirroring <c>wire::PlayerInput</c>: CBasePlayer's button
/// words (<see cref="InputButtons"/>). The view angles are the client view's (<see cref="WireClientView"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WirePlayerInput
{
    public ushort Player;
    public byte Valid;
    /// <summary>v14: the user commands the player received this tick.</summary>
    public byte Commands;
    /// <summary>m_nButtons: held this tick.</summary>
    public InputButtons Buttons;
    /// <summary>m_afButtonPressed: went down in the tick's command.</summary>
    public InputButtons Pressed;
    /// <summary>m_afButtonReleased: went up in it.</summary>
    public InputButtons Released;
    // v14: the user commands themselves, as CBasePlayer::ProcessUsercmds received them, before the game consumed any.
    /// <summary>v14: the newest command's forwardmove, sidemove and upmove (Source units per second; 450 walking, 1000 running).</summary>
    public float ForwardMove, SideMove, UpMove;
    /// <summary>v14: the commands' IN_* buttons, OR'd over the tick.</summary>
    public InputButtons CommandButtons;
    /// <summary>v14: Arkane's action bytes, set when any of the tick's commands had them (<see cref="PlayerActions.Kick"/> among them).</summary>
    public PlayerActions Actions;
    /// <summary>v14: the newest command's impulse.</summary>
    public byte Impulse;
    public byte Reserved2;
    public ushort Reserved3;
    /// <summary>v14: the newest command's number.</summary>
    public int CommandNumber;
    public uint Reserved4;

    public static WirePlayerInput None => new() { Player = WireLayout.NoEntity };
}

/// <summary>
/// v15: the player's stats and equipment each tick, mirroring <c>wire::PlayerStats</c>, read from <c>CMM_Player</c>
/// (docs/wire.md, "The player's stats"): stamina, mana, the skill modules learned, skill points, and the items in hand
/// (a spell is a weapon, <c>spell_fire_1</c> and so on) and worn as entity index and serial (<see cref="WireLayout.NoEntity"/> when none).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct WirePlayerStats
{
    public ushort Player;
    public byte Valid;
    public byte Reserved;
    /// <summary>m_fStamina (+0x29C4) and m_fMaxStamina (+0x29C0).</summary>
    public float Stamina, MaxStamina;
    /// <summary>m_fMana (+0x29BC) and m_fMaxMana (+0x29B0).</summary>
    public float Mana, MaxMana;
    /// <summary>The skill modules learned (+0x2AF0), one bit each in the game's module order.</summary>
    public SkillModules Modules;
    /// <summary>m_iSkillPoints (+0x2B3C): unspent.</summary>
    public int SkillPoints;
    /// <summary>0. m_CurSpell (+0x3CB0) never showed the spell in hand; the <see cref="Weapon"/>'s class does (<c>spell_fire_1</c>...).</summary>
    public int ReservedSpell;
    /// <summary>m_hActiveWeapon, m_hActiveShield, m_hActiveHelmet, m_hActiveRing and m_hActiveArmor: entity indices.</summary>
    public ushort Weapon, Shield, Helmet, Ring, Armor;
    public ushort Reserved2;
    /// <summary>Their serials.</summary>
    public uint WeaponSerial, ShieldSerial, HelmetSerial, RingSerial, ArmorSerial;
    public uint Reserved3, Reserved4, Reserved5;
    /// <summary>v16: each skill's level, by <see cref="SkillNames.ByIndex"/> (26 used), from m_skillLevel (+0x2AC8).</summary>
    public fixed byte SkillLevels[32];

    /// <summary>v16: the level of the skill <c>mm_skills_infos.txt</c> calls <paramref name="name"/>; 0 for one the game's table lacks.</summary>
    public int SkillLevel(string name) => Array.IndexOf(SkillNames.ByIndex, name) is int i and >= 0 ? SkillLevels[i] : 0;

    public static WirePlayerStats None => new()
    {
        Player = WireLayout.NoEntity, Weapon = WireLayout.NoEntity, Shield = WireLayout.NoEntity,
        Helmet = WireLayout.NoEntity, Ring = WireLayout.NoEntity, Armor = WireLayout.NoEntity,
    };
}

/// <summary>
/// The player's skill modules as the game numbers them (its module table, copied at start-up from server.dll+0x79DF38;
/// docs/wire.md, "The player's stats"): bit i is module i. <see cref="SkillModuleNames"/> gives the names
/// <c>mm_skills_infos.txt</c> uses.
/// </summary>
[Flags]
public enum SkillModules : ulong
{
    None = 0,
    AdrenalineControl = 1ul << 0, FastMetabolism = 1ul << 1, PoisonResistance = 1ul << 2, DoubleSprint = 1ul << 3,
    ManaRecovery = 1ul << 4, Backstab = 1ul << 5, HideInShadow = 1ul << 6, SilentStep = 1ul << 7,
    BowReload = 1ul << 8, BowZoom = 1ul << 9, Stability = 1ul << 10, CombatRushAttack = 1ul << 11,
    Passthrough = 1ul << 12, CombatTrample = 1ul << 13, CombatDisarm = 1ul << 14, CombatVerticalAttack = 1ul << 15,
    CombatShieldEnabled = 1ul << 16, CombatFatality = 1ul << 17, CombatParry = 1ul << 18, CombatBigSlash = 1ul << 19,
    CombatHugeSlash = 1ul << 20, CombatChainedSlash = 1ul << 21, Lockpick = 1ul << 22, WizardEye = 1ul << 23,
    Telekinesis = 1ul << 24, FlameArrow = 1ul << 25, Fireball = 1ul << 26, CureWounds = 1ul << 27,
    LightningBolt = 1ul << 28, Shrink = 1ul << 29, MagicReflection = 1ul << 30, Firetrap = 1ul << 31,
    Inferno = 1ul << 32, Freeze = 1ul << 33, Charm = 1ul << 34, DetectTrap = 1ul << 35, DetectSecret = 1ul << 36,
}

/// <summary>
/// The game's skill table, by index (copied at start-up from server.dll+0x79DFCC; docs/wire.md, "The player's stats"):
/// the index of each skill's level in <see cref="WirePlayerStats.SkillLevels"/>. <c>backstab</c>, in
/// <c>mm_skills_infos.txt</c>, isn't in it.
/// </summary>
public static class SkillNames
{
    public static readonly string[] ByIndex =
    [
        "combat", "projectile", "stamina", "extra_damage", "extra_health", "critical_hit", "extra_mana", "adrenaline",
        "fast_metabolism", "poison_recovery", "mana_regeneration", "stealth", "mechanism", "focus", "magic_0", "magic_1",
        "magic_3", "magic_5", "magic_6", "magic_7", "magic_8", "magic_9", "magic_11", "magic_12", "magic_13", "magic_15",
    ];
}

/// <summary>The skill modules' names, by bit, as <c>mm_skills_infos.txt</c> writes them (telekinesis is Arkane's spelling).</summary>
public static class SkillModuleNames
{
    public static readonly string[] ByBit =
    [
        "adrenaline_control", "fast_metabolism", "poison_resistance", "double_sprint", "mana_recovery", "backstab",
        "hide_in_shadow", "silent_step", "bow_reload", "bow_zoom", "stability", "combat_rush_attack", "passthrough",
        "combat_trample", "combat_disarm", "combat_vertical_attack", "combat_shield_enabled", "combat_fatality",
        "combat_parry", "combat_big_slash", "combat_huge_slash", "combat_chained_slash", "lockpick", "wizard_eye",
        "telekynesis", "flame_arrow", "fireball", "cure_wounds", "lightning_bolt", "shrink", "magic_reflection",
        "firetrap", "inferno", "freeze", "charm", "detect_trap", "detect_secret",
    ];

    /// <summary>The names of the modules set in <paramref name="modules"/>.</summary>
    public static IEnumerable<string> Of(SkillModules modules)
    {
        for (int i = 0; i < ByBit.Length; i++)
            if (((ulong)modules & (1ul << i)) != 0) yield return ByBit[i];
    }
}

/// <summary>
/// Arkane's action bytes in its CUserCmd (+0x95..+0xA1), one bit each in <see cref="WirePlayerInput.Actions"/>: bit i
/// is byte +0x95+i (docs/wire.md, "The player's input"). Bytes +0x9B, +0x9D and +0x9F were seen with the character sheet
/// (bits 6, 8 and 10) but aren't named; +0x96, +0x98, +0x99, +0x9C, +0xA0 and +0xA1 never set.
/// </summary>
[Flags]
public enum PlayerActions : uint
{
    None = 0,
    /// <summary>+sprint (Megasprint), byte +0x95.</summary>
    Sprint = 1 << 0,
    /// <summary>+kick (F), byte +0x97.</summary>
    Kick = 1 << 2,
    /// <summary>+xana, byte +0x9A: set while held.</summary>
    Xana = 1 << 5,
    /// <summary>The character sheet opened (togglecharsheet or mm_open_charsheet), byte +0x9B, one tick: 8 of 8 opens, from the console or the keyboard. The server clears the player's buttons on it.</summary>
    CharSheetOpen = 1 << 6,
    /// <summary>The character sheet closed (togglecharsheet), byte +0x9D, one tick: 8 of 8 closes.</summary>
    CharSheetClose = 1 << 8,
    /// <summary>mm_previtem and mm_nextitem, byte +0x9E, one tick per press: one byte for both, likely signed by direction (the Wire keeps only that it's set).</summary>
    ItemCycle = 1 << 9,
}

/// <summary>Source's IN_* button bits as the 2006 SDK numbers them. Dark Messiah's kick isn't among them (docs/wire.md, "The player's input").</summary>
[Flags]
public enum InputButtons : uint
{
    None = 0,
    Attack = 1 << 0,
    Jump = 1 << 1,
    Duck = 1 << 2,
    Forward = 1 << 3,
    Back = 1 << 4,
    Use = 1 << 5,
    Cancel = 1 << 6,
    Left = 1 << 7,
    Right = 1 << 8,
    MoveLeft = 1 << 9,
    MoveRight = 1 << 10,
    Attack2 = 1 << 11,
    Run = 1 << 12,
    Reload = 1 << 13,
    Alt1 = 1 << 14,
    Alt2 = 1 << 15,
    Score = 1 << 16,
    Speed = 1 << 17,
    /// <summary>The SDK's IN_WALK; Dark Messiah's +zoom sets it (docs/wire.md, "The player's input").</summary>
    Walk = 1 << 18,
    Zoom = 1 << 19,
    Weapon1 = 1 << 20,
    Weapon2 = 1 << 21,
    /// <summary>The SDK's IN_BULLRUSH; Dark Messiah's +speed sets it.</summary>
    BullRush = 1 << 22,
    Grenade1 = 1 << 23,
    Grenade2 = 1 << 24,
}
