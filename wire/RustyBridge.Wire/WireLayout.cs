using System.Runtime.InteropServices;

namespace RustyBridge.Wire;

// Mirror of tap/darkmessiah/src/wire.h (Wire v7). Layout and protocol:
// docs/wire.md. Keep the two in step.
public static class WireLayout
{
    public const ulong Magic = 0x3130455249574252ul; // "RBWIRE01"
    public const uint Version = 9;

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
    public const ushort NoEntity = 0xFFFF;

    public const int ClassesOffset = 256;
    public const int ModelsOffset = ClassesOffset + MaxClasses * ClassNameBytes;
    public const int SlotsOffset = ModelsOffset + MaxModels * ModelPathBytes;
    public static readonly int DamageOffset = Marshal.SizeOf<WireSlotHeader>() + MaxEntities * Marshal.SizeOf<WireEntity>();
    public static readonly int DefenseOffset = DamageOffset + MaxDamage * Marshal.SizeOf<WireDamage>();
    public static readonly int LayersOffset = DefenseOffset + MaxDefense * Marshal.SizeOf<WireDefense>();
    public static readonly int SlowTimeOffset = LayersOffset + MaxLayers * Marshal.SizeOf<WireLayer>();
    public static readonly int ClientViewOffset = SlowTimeOffset + MaxSlowTime * Marshal.SizeOf<WireSlowTime>();
    public static readonly int SlotBytes = ClientViewOffset + Marshal.SizeOf<WireClientView>();
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
    public uint Caller;
    public uint PlayerSerial;
    public uint Reserved2;
}

[Flags]
public enum WireEntityFlags : byte
{
    None = 0,
    /// <summary>Health, MaxHealth and LifeState are valid.</summary>
    HasHealth = 1,
    /// <summary>Sequence, Cycle and PlaybackRate are valid.</summary>
    HasAnimation = 2,
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
    public ushort Reserved;
    public uint LinkSerial;
}

/// <summary>
/// One CBaseEntity::TakeDamage call, from the tap's hook. Field names follow
/// Arkane's CTakeDamageInfo datadesc. Entity fields are edict indices, or
/// <see cref="WireLayout.NoEntity"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct WireDamage
{
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
    public byte TraceValid, Reserved;
    public float PositionX, PositionY, PositionZ;
    public float ForceX, ForceY, ForceZ;
    public float TraceStartX, TraceStartY, TraceStartZ;
    public float TraceEndX, TraceEndY, TraceEndZ;
    public int PushForce, WeaponFlags, MMDamageFlag, MMSpell;
    public float SlowTimeDelay, SlowTimeDuration, SlowTimeFactor;
    public float MMDirectionX, MMDirectionY, MMDirectionZ;
    /// <summary>EHANDLE serials of the entities above (0 with <see cref="WireLayout.NoEntity"/>).</summary>
    public uint VictimSerial, AttackerSerial, InflictorSerial, TraceEntitySerial, WeaponSerial;
    public uint Reserved2;
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
