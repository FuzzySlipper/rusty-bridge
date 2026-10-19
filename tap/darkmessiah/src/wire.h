// Wire v11: the tap's per-tick entity snapshots, in a file the tap maps and
// readers map read-only. Layout and protocol: docs/wire.md. The C# mirror is
// wire/RustyBridge.Wire/WireLayout.cs; keep the two in step.
#pragma once
#include <stdint.h>

namespace wire {

const uint64_t kMagic = 0x3130455249574252ull; // "RBWIRE01", little-endian
const uint32_t kVersion = 14;

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
    // v10
    uint16_t parent;            // m_hMoveParent: what the entity is attached to; kNoEntity when none
    uint8_t parent_attachment;  // m_iParentAttachment: 1-based into the parent model's attachments; 0 = none
    uint8_t reserved2;
    uint32_t parent_serial;
    uint16_t owner;             // m_hOwnerEntity, or a weapon's m_hOwner when that is empty; kNoEntity when none
    uint16_t reserved3;
    uint32_t owner_serial;
};

const uint16_t kNoEntity = 0xFFFF;

const uint32_t kPoseParameters = 24; // MAXSTUDIOPOSEPARAM
const uint32_t kDamageLayers = 8;    // a damage record's victim overlays (v13)

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
    uint8_t attacker_critical;  // v11: the attacker's critical byte (+0x21F7) as the call arrived: set,
                                //   Arkane's melee routine doubled the blow (DmgCrit = DmgBase x 2)
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
    // v11: an NPC victim's awareness, which Arkane's melee routine reads for
    // the "not alerted" damage multiplier (mm_game_settings.txt
    // mul_damages_Npc_Not_alerted, applied at or below _level).
    int16_t victim_alert;       // the NPC's alert level (+0x36EC, as its magnitude)
    uint8_t victim_npc_state;   // its state (+0x1A5C); 3 never takes the multiplier
    uint8_t victim_alert_valid; // 1 when the two fields above were read (npc_* victims)
    // v13: the victim's animation as the call arrived, before TakeDamage ran:
    // the state the trace that struck it posed. A character's sequence and
    // cycle change only in its own think, so the frame's end can already show
    // the next one.
    int16_t victim_sequence;    // m_nSequence; -1 when not read
    uint8_t victim_anim_valid;  // 1 when the fields below were read
    uint8_t victim_layer_count; // its active overlays below, at most kDamageLayers
    float victim_cycle;         // m_flCycle
    float victim_playback_rate; // m_flPlaybackRate
    float victim_anim_time;     // m_flAnimTime: when its animation last advanced
    float victim_pose[kPoseParameters]; // m_flPoseParameter (aim and move yaw among them)
    int16_t victim_layer_sequence[kDamageLayers]; // its active overlays, in m_AnimOverlay order
    int16_t victim_layer_order[kDamageLayers];
    float victim_layer_cycle[kDamageLayers];
    float victim_layer_weight[kDamageLayers];
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
    // v11: the NPC's defense state as its verdict read it (CNPC_BaseArx's cached
    // state word: 0x4 parry/block, 0x10 shield up), for NPC verdicts.
    uint32_t victim_state;
    uint8_t state_valid;        // 1 when victim_state was read
    uint8_t no_effect;          // the NPC's +0x2961 byte: set, the verdict is 4 (no effect) whatever struck
    uint16_t reserved;
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
// motion; caller is the server.dll RVA it returns to, naming the code path,
// or kCallerOutside for a return address outside server.dll (Arkane's
// copy-protected .sll code runs from a copy in allocated memory).
enum : uint8_t { kSlowTimeSetter = 0, kSlowTimeRequest = 1 };
constexpr uint32_t kCallerOutside = 0xFFFFFFFFu;

struct SlowTime {
    int32_t tick;
    uint16_t player;
    uint8_t sound;
    uint8_t source;             // kSlowTimeSetter or kSlowTimeRequest
    float start;                // setter: curtime slow motion starts at; request: delay from now
    float duration;             // seconds of game time (setter) or as asked (request; the game stores duration * factor)
    float factor;               // time scale while it lasts
    uint32_t caller;            // server.dll RVA of the return address, or kCallerOutside
    uint32_t player_serial;
    uint32_t reserved2;
};

// A client viewmodel overlay layer with weight (v9).
struct ClientLayer {
    int16_t sequence;
    int16_t order;
    float cycle;
    float weight;
};
const uint32_t kClientLayers = 8;

// The client's view of the local player (v8), read from client.dll and
// engine.dll in the same process: what the server doesn't know. One per slot,
// after the slow-time records. Read in the server frame, so it is the client's
// state as of its last frame.
struct ClientView {
    uint16_t player;            // the client's local player index; kNoEntity when the client isn't read
    uint16_t viewmodel;         // the client entity read as the viewmodel (the server viewmodel's index); kNoEntity if none
    uint32_t flags;             // 1 view angles, 2 player transform, 4 viewmodel transform, 8 view offset,
                                // 16 camera (v9), 32 viewmodel sequence and cycle (v9), 64 viewmodel layers (v9)
    float view_angles[3];       // the client's view: pitch, yaw, roll (IVEngineClient::GetViewAngles)
    float origin[3];            // the client's local player abs origin (predicted)
    float angles[3];            // its abs angles
    float view_offset[3];       // the server player's m_vecViewOffset; the eye is origin + view_offset
    float viewmodel_origin[3];  // the client viewmodel's abs origin (the server's is stale)
    float viewmodel_angles[3];
    // v9: the camera the client last drew with (its CViewSetup), and the client viewmodel's own animation.
    float camera_origin[3];
    float camera_angles[3];
    float fov;                  // horizontal, degrees
    float fov_viewmodel;
    uint16_t screen_width, screen_height;
    float z_near, z_far;
    int16_t viewmodel_sequence; // the client viewmodel's m_nSequence, -1 if not read
    uint16_t reserved;
    float viewmodel_cycle;      // its m_flCycle
    uint8_t layer_count;        // the client viewmodel's overlay layers with weight, in its own order
    uint8_t reserved2[3];
    ClientLayer layers[kClientLayers];
};

// v12: the player's input as the server ran it this tick: CBasePlayer's button
// words (IN_* bits), read after the frame. Dark Messiah's kick isn't among them
// (docs/wire.md, "The player's input").
// The view angles are the client view's.
struct PlayerInput {
    uint16_t player;            // edict index the buttons are read from; kNoEntity if none
    uint8_t valid;              // 1 when read
    uint8_t commands;           // v14: user commands the player received this tick
    uint32_t buttons;           // m_nButtons (+0x1FB8): held
    uint32_t pressed;           // m_afButtonPressed (+0x1FC8): went down in this tick's command
    uint32_t released;          // m_afButtonReleased (+0x1FCC): went up in it
    // v14: the user commands themselves, as CBasePlayer::ProcessUsercmds received
    // them (server.dll+0x430350), before the game consumed any of them.
    float forward_move;         // the newest command's forwardmove (+0x18)
    float side_move;            //   sidemove (+0x1C)
    float up_move;              //   upmove (+0x24)
    uint32_t command_buttons;   // its IN_* buttons (+0x28), OR'd over this tick's commands
    uint32_t actions;           // Arkane's action bytes (+0x95..+0xA1), bit i set when byte +0x95+i is
                                //   nonzero in any of this tick's commands; bit 2 (+0x97) is the kick
    uint8_t impulse;            // the newest command's impulse (+0x2C)
    uint8_t reserved2;
    uint16_t reserved3;
    int32_t command_number;     // the newest command's number (+0x4)
    uint32_t reserved4;
};

#pragma pack(pop)

static_assert(sizeof(PlayerInput) == 48, "PlayerInput layout");
static_assert(sizeof(ClientLayer) == 12, "ClientLayer layout");
static_assert(sizeof(ClientView) == 232, "ClientView layout");
static_assert(sizeof(Header) == 144, "Header layout");
static_assert(sizeof(SlotHeader) == 96, "SlotHeader layout");
static_assert(sizeof(SlowTime) == 32, "SlowTime layout");
static_assert(sizeof(Layer) == 20, "Layer layout");
static_assert(sizeof(Defense) == 56, "Defense layout");
static_assert(sizeof(Damage) == 360, "Damage layout");
static_assert(sizeof(Entity) == 80, "Entity layout");

const uint32_t kClassesOffset = 256;
const uint32_t kModelsOffset = kClassesOffset + kMaxClasses * kClassNameBytes;
const uint32_t kSlotsOffset = kModelsOffset + kMaxModels * kModelPathBytes;
const uint32_t kDamageOffset = sizeof(SlotHeader) + kMaxEntities * sizeof(Entity);  // within a slot
const uint32_t kDefenseOffset = kDamageOffset + kMaxDamage * sizeof(Damage);
const uint32_t kLayersOffset = kDefenseOffset + kMaxDefense * sizeof(Defense);
const uint32_t kSlowTimeOffset = kLayersOffset + kMaxLayers * sizeof(Layer);
const uint32_t kClientViewOffset = kSlowTimeOffset + kMaxSlowTime * sizeof(SlowTime);
const uint32_t kInputOffset = kClientViewOffset + sizeof(ClientView);   // v12
const uint32_t kSlotBytes = kInputOffset + sizeof(PlayerInput);
const uint32_t kFileBytes = kSlotsOffset + kSlotCount * kSlotBytes;

// The control file, rustybridge.control beside the Wire: a reader holds the
// donor's frame and steps it tick by tick. The tap creates it and polls it
// once per tick; a reader maps it read-write. docs/wire.md, "Holding the
// donor". The C# mirror is wire/RustyBridge.Wire/TapControl.cs.
const uint64_t kControlMagic = 0x31304C5254434252ull; // "RBCTRL01", little-endian
const uint32_t kControlVersion = 1;
const uint32_t kControlFileBytes = 4096;

#pragma pack(push, 4)
struct Control {
    uint64_t magic;
    uint32_t version;
    uint32_t writer_pid;
    int32_t hold;               // reader: 1 holds the donor after each tick, 0 runs it
    uint32_t step_requests;     // reader: ticks asked for while held, a running count
    uint32_t steps_done;        // tap: ticks released while held, a running count
    int32_t held_tick;          // tap: the tick it's holding at, or -1 while running
    uint64_t lease_until_ms;    // reader: a hold lapses at this GetTickCount64 time
    uint32_t holds_lapsed;      // tap: holds released because their lease ran out
    uint32_t reserved;
    uint32_t command_seq;       // reader: bumped after writing command
    uint32_t command_done;      // tap: the command_seq it last ran
    char command[256];          // reader: a console command, NUL-terminated, run through IVEngineClient::ClientCmd
};
#pragma pack(pop)
static_assert(sizeof(Control) == 312, "Control layout");

} // namespace wire
