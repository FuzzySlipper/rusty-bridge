using System.Runtime.InteropServices;

namespace RustyBridge.Wire;

// Mirror of tap/darkmessiah/src/wire.h (Wire v4). Layout and protocol:
// docs/wire.md. Keep the two in step.
public static class WireLayout
{
    public const ulong Magic = 0x3130455249574252ul; // "RBWIRE01"
    public const uint Version = 4;

    public const int SlotCount = 128;
    public const int MaxEntities = 2048;
    public const int MaxClasses = 1024;
    public const int ClassNameBytes = 64;
    public const int MaxModels = 4096;
    public const int ModelPathBytes = 128;
    public const int MaxDamage = 64;
    public const int MaxDefense = 32;
    public const ushort NoEntity = 0xFFFF;

    public const int ClassesOffset = 256;
    public const int ModelsOffset = ClassesOffset + MaxClasses * ClassNameBytes;
    public const int SlotsOffset = ModelsOffset + MaxModels * ModelPathBytes;
    public static readonly int DamageOffset = Marshal.SizeOf<WireSlotHeader>() + MaxEntities * Marshal.SizeOf<WireEntity>();
    public static readonly int DefenseOffset = DamageOffset + MaxDamage * Marshal.SizeOf<WireDamage>();
    public static readonly int SlotBytes = DefenseOffset + MaxDefense * Marshal.SizeOf<WireDefense>();
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
}
