// Wire v7: the tap's per-tick entity snapshots, in a file the tap maps and
// readers map read-only. Layout and protocol: docs/wire.md. The C# mirror is
// wire/RustyBridge.Wire/WireLayout.cs; keep the two in step.
#pragma once
#include <stdint.h>

namespace wire {

const uint64_t kMagic = 0x3130455249574252ull; // "RBWIRE01", little-endian
const uint32_t kVersion = 7;

const uint32_t kSlotCount = 128;     // ~2 s of history at 66 ticks/s
const uint32_t kMaxEntities = 2048;  // Source's edict limit
const uint32_t kMaxClasses = 1024;
const uint32_t kClassNameBytes = 64;
const uint32_t kMaxModels = 4096;
const uint32_t kModelPathBytes = 128;
const uint32_t kMaxDamage = 64;      // damage records per slot
const uint32_t kMaxDefense = 32;     // defense records per slot
const uint32_t kMaxLayers = 256;     // overlay layer records per slot
const uint32_t kMaxSlowTime = 16;    // bullet-time setter calls per slot

#pragma pack(push, 4)

struct Header {                 // at offset 0
    uint64_t magic;
    uint32_t version;
    uint32_t header_bytes;
    uint32_t slot_count;
    uint32_t slot_bytes;
    uint32_t max_entities;
    uint32_t max_classes;
    uint32_t class_name_bytes;
    uint32_t max_models;
    uint32_t model_path_bytes;
    uint32_t classes_offset;
    uint32_t models_offset;
    uint32_t slots_offset;
    uint32_t writer_pid;
    uint32_t level_serial;      // +1 on every LevelInit
    float tick_interval;        // seconds per donor tick
    uint32_t reserved;
    char map[64];
    uint64_t frames_written;    // frame f lives in slot f % slot_count; 0 = none yet
};

struct SlotHeader {
    uint64_t seq;               // 2f+1 while frame f is written, 2f+2 when complete
    uint64_t frame;
    int32_t tick;               // donor gpGlobals->tickcount
    float curtime;              // donor gpGlobals->curtime
    uint32_t level_serial;
    uint32_t entity_count;
    uint32_t damage_count;      // damage records after the entity array
    uint32_t damage_dropped;    // records lost since the previous frame (over kMaxDamage)
    uint32_t defense_count;     // defense records after the damage array
    uint32_t defense_dropped;
    uint32_t layer_count;       // overlay layer records after the defense array
    uint32_t layer_dropped;     // active layers that didn't fit (over kMaxLayers)
    // v7
    double wall_seconds;        // QueryPerformanceCounter time at the write, seconds since the tap loaded;
                                //   against curtime it shows the donor's time scale (hit slow motion)
    uint32_t slow_time_count;   // bullet-time setter calls after the layer array
    uint32_t slow_time_dropped;
    uint16_t player;            // edict index of the player the fields below are read from; kNoEntity if none
    uint8_t in_bullet_time;     // CMM_Player::m_bInBulletTime (+0x20B0): slow motion is on
    uint8_t bullet_time_sound;  // +0x20B1, set with the bullet time: whether it plays its sound
    float bullet_time_start;    // m_fBulletTimeStart (+0x20A4): curtime it starts at; <= 0 when none is pending
    float bullet_time_duration; // m_fBulletTimeDuration (+0x20A8)
    float bullet_time_factor;   // m_fBulletTimeFactor (+0x20AC): the time scale it sets (mm_host_timescale)
    float adrenaline;           // m_fAdrenaline (+0x29D8)
    float max_adrenaline;       // m_fMaxAdrenaline (+0x29D4)
};

const uint8_t kHasHealth = 1;       // health, max_health and life_state are valid
const uint8_t kHasAnimation = 2;    // sequence, cycle and playback_rate are valid

struct Entity {
    uint16_t index;             // edict index
    uint16_t class_id;          // row in the class table
    int32_t model_index;        // row in the model table; <= 0 means none
    float origin[3];            // abs origin, donor units
    float angles[3];            // abs angles, degrees (pitch, yaw, roll)
    float health;               // CBaseEntity::m_fHealth (Arkane's float health)
    float max_health;           // CBaseEntity::m_fMaxHealth
    int16_t sequence;           // CBaseAnimating::m_nSequence, index into the model's sequences
    uint8_t flags;              // kHas*
    uint8_t life_state;         // CBaseEntity::m_lifeState: 0 alive, 1 dying, 2 dead
    float cycle;                // CBaseAnimating::m_flCycle, 0..1 through the sequence
    float playback_rate;        // CBaseAnimating::m_flPlaybackRate
    uint32_t serial;            // the entity's EHANDLE serial (handle >> 12); with index, its identity
    // v7
    uint16_t link;              // the entity it stands in for or with: an NPC's knockdown ragdoll
                                //   (m_hRagdollFall, +0x29E4), a prop_ragdoll's NPC (m_hUnragdoll,
                                //   +0x16A0); kNoEntity when none
    uint16_t reserved;
    uint32_t link_serial;       // the linked entity's serial; 0 with kNoEntity
};

const uint16_t kNoEntity = 0xFFFF;

// One CBaseEntity::TakeDamage call, captured by the tap's hook. Field names
// follow Arkane's CTakeDamageInfo datadesc (offsets in plugin.cpp).
struct Damage {
    int32_t tick;               // donor tick the damage was applied in
    uint16_t victim;            // edict indices; kNoEntity when absent or server-only
    uint16_t attacker;          // m_hAttacker, or its owner when the attacker is a held weapon
    uint16_t inflictor;         // m_hInflictor
    uint16_t trace_entity;      // m_hTraceEntity
    float damage;               // m_flDamage, as requested
    float health_before;        // victim m_fHealth around the original call,
    float health_after;         //   so the applied damage and killing blows show
    uint32_t damage_type;       // m_bitsDamageType (DMG_*)
    int16_t custom_kill;        // m_iCustomKillType
    uint16_t weapon;            // the original m_hAttacker when it was a weapon_* with an owner
    int16_t hitbox;             // m_iTraceHitBox
    int16_t hitgroup;           // m_iTraceHitGroup (HITGROUP_*)
    int16_t physics_bone;       // m_iTracePhysicsBone
    uint8_t trace_valid;        // m_bTraceInfoValid
    uint8_t reserved;
    float position[3];          // m_vecDamagePosition
    float force[3];             // m_vecDamageForce
    float trace_start[3];       // m_vecTraceStartPos
    float trace_end[3];         // m_vecTraceEndPos
    int32_t push_force;         // m_iPushForce
    int32_t weapon_flags;       // m_iWeaponFlags
    int32_t mm_damage_flag;     // m_iMMDamageFlag
    int32_t mm_spell;           // m_iMMDamageSpell
    float slow_time_delay;      // m_fSlowTimeDelay
    float slow_time_duration;   // m_fSlowTimeDuration
    float slow_time_factor;     // m_fSlowTimeFactor
    float mm_direction[3];      // m_vecMMDirection
    uint32_t victim_serial;     // EHANDLE serials of the entities above, so a record
    uint32_t attacker_serial;   //   resolves to the right entity even after its index
    uint32_t inflictor_serial;  //   is reused; 0 with kNoEntity
    uint32_t trace_entity_serial;
    uint32_t weapon_serial;
    uint32_t reserved2;
};

// One melee defense outcome: an Arkane NPC's verdict on a blow that reached
// it (CNPC_BaseArx's defense virtual, server.dll+0x2E0030), or a change of
// the player's parry state (CBasePlayer slot 369, server.dll+0x3FEC50).
const int32_t kDefenseNone = 0;     // not produced by CNPC_BaseArx; other classes answer 0
const int32_t kDefenseParry = 1;    // struck its weapon, or in its parry/block state
const int32_t kDefenseHit = 2;      // checked, not defended
const int32_t kDefenseNoEffect = 4; // the blow does nothing (damage zeroed)
const int32_t kDefenseShield = 8;   // struck its off-hand item, or shield up and attack from the front
const int32_t kPlayerParried = 17;      // player's parry state set to PARRY_STATE_PARRY (1)
const int32_t kPlayerParryBroken = 18;  // player's parry state set to PARRY_STATE_BREAKPARRY (2)

struct Defense {
    int32_t tick;
    uint16_t victim;
    uint16_t attacker;
    uint16_t weapon;            // the attacker's weapon entity
    uint16_t struck;            // the entity the blow struck (the victim, or its weapon or shield)
    int32_t result;             // kDefense*
    float direction[3];         // the vector the check was given (the blow's direction); zero for player records
    uint32_t source;            // player records: server.dll RVA of the code that set the state
    uint32_t victim_serial;     // EHANDLE serials of the entities above; 0 with kNoEntity
    uint32_t attacker_serial;
    uint32_t weapon_serial;
    uint32_t struck_serial;
};

// One active animation overlay layer (CAnimationLayer in
// CBaseAnimatingOverlay::m_AnimOverlay): a gesture, parry or block playing
// over the entity's main sequence. Only layers that are active with a
// weight above zero are sent.
struct Layer {
    uint16_t index;             // edict index of the entity it plays on
    uint8_t slot;               // position in the entity's overlay vector
    uint8_t flags;              // m_fFlags: 1 active, 2 autokill, 4 kill me, 32 dying
    int16_t sequence;           // m_nSequence, in the entity's model's sequence list
    int16_t order;              // m_nOrder: blend order among the layers
    float cycle;                // m_flCycle, 0..1
    float weight;               // m_flWeight, 0..1
    float playback_rate;        // m_flPlaybackRate
};

// One call of the player's bullet-time setter (CMM_Player virtual at
// server.dll+0x146E80: start, duration, factor, sound), which arms slow
// motion; caller is the server.dll RVA it returns to, naming the code path.
enum : uint8_t { kSlowTimeSetter = 0, kSlowTimeRequest = 1 };

struct SlowTime {
    int32_t tick;
    uint16_t player;
    uint8_t sound;
    uint8_t source;             // kSlowTimeSetter or kSlowTimeRequest
    float start;                // setter: curtime slow motion starts at; request: delay from now
    float duration;             // seconds of game time (setter) or as asked (request; the game stores duration * factor)
    float factor;               // time scale while it lasts
    uint32_t caller;            // server.dll RVA of the return address
    uint32_t player_serial;
    uint32_t reserved2;
};

#pragma pack(pop)

static_assert(sizeof(Header) == 144, "Header layout");
static_assert(sizeof(SlotHeader) == 96, "SlotHeader layout");
static_assert(sizeof(SlowTime) == 32, "SlowTime layout");
static_assert(sizeof(Layer) == 20, "Layer layout");
static_assert(sizeof(Defense) == 48, "Defense layout");
static_assert(sizeof(Damage) == 152, "Damage layout");
static_assert(sizeof(Entity) == 64, "Entity layout");

const uint32_t kClassesOffset = 256;
const uint32_t kModelsOffset = kClassesOffset + kMaxClasses * kClassNameBytes;
const uint32_t kSlotsOffset = kModelsOffset + kMaxModels * kModelPathBytes;
const uint32_t kDamageOffset = sizeof(SlotHeader) + kMaxEntities * sizeof(Entity);  // within a slot
const uint32_t kDefenseOffset = kDamageOffset + kMaxDamage * sizeof(Damage);
const uint32_t kLayersOffset = kDefenseOffset + kMaxDefense * sizeof(Defense);
const uint32_t kSlowTimeOffset = kLayersOffset + kMaxLayers * sizeof(Layer);
const uint32_t kSlotBytes = kSlowTimeOffset + kMaxSlowTime * sizeof(SlowTime);
const uint32_t kFileBytes = kSlotsOffset + kSlotCount * kSlotBytes;

} // namespace wire
