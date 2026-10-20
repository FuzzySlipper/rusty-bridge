// Rusty Bridge tap for Dark Messiah of Might and Magic.
//
// A Source server plugin (ISERVERPLUGINCALLBACKS001) that streams every
// server entity's transform, health and animation state to the Wire
// (docs/wire.md) once per donor tick, together with every damage call and
// melee defense verdict, which it catches by detouring
// CBaseEntity::TakeDamage and CNPC_BaseArx's defense check. It deliberately uses no Source SDK
// headers: the few interfaces it touches are declared below in the vtable
// order read from Arkane's server.dll, field offsets come from the classes'
// send tables, and calls into the game are checked or wrapped in SEH so a
// wrong guess is logged instead of crashing the game.
//
// Output goes to rustybridge.log next to this DLL. Entity dumps happen at
// ServerActivate, again ~5 seconds into the level, and whenever a file named
// rustybridge_dump appears next to the DLL (it is deleted after the dump).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <share.h>
#include <intrin.h>
#pragma intrinsic(_ReturnAddress)

#include "wire.h"

typedef void* (*CreateInterfaceFn)(const char* name, int* returnCode);

struct edict_t;
struct model_t;

enum PLUGIN_RESULT { PLUGIN_CONTINUE = 0, PLUGIN_OVERRIDE, PLUGIN_STOP };

// --- Interfaces we call into (Episode One vtable order) ----------------------

// Arkane's CServerNetworkProperty does not follow Episode One here: two extra
// slots sit before GetEdict. Read from server.dll's vtable (2026-10-17):
// 0 returns m_pOuter, 2 forwards to the entity's GetServerClass, 3/4 read and
// OR flags at entity+0x32c, 5 returns [this+0xC], 6 returns m_iClassname.
class IServerNetworkable {
public:
    virtual void* GetEntityHandle() = 0;          // 0
    virtual void* Unknown1() = 0;                 // 1  (reads entity+0x500)
    virtual void* GetServerClass() = 0;           // 2
    virtual int GetFlags() const = 0;             // 3
    virtual void AddFlags(int) = 0;               // 4
    virtual edict_t* GetEdict() const = 0;        // 5
    virtual const char* GetClassName() const = 0; // 6
};

// CCollisionProperty's vtable (server.dll+0x59BEB4; entities use the subclass
// copy at +0x6A060C), read 2026-10-17. Matches Episode One. GetCollisionOrigin
// refreshes a dirty abs transform and returns &outer->m_vecAbsOrigin (outer+0x6C4);
// GetCollisionAngles returns &outer->m_angAbsRotation (outer+0x744) only for
// rotatable solids and a zero vector otherwise.
class ICollideable {
public:
    virtual void* GetEntityHandle() = 0;                       // 0  m_pOuter
    virtual const float* OBBMins() const = 0;                  // 1
    virtual const float* OBBMaxs() const = 0;                  // 2
    virtual void WorldSpaceTriggerBounds(float*, float*) const = 0; // 3
    virtual bool TestCollision(const void*, unsigned, void*) = 0;   // 4
    virtual bool TestHitboxes(const void*, unsigned, void*) = 0;    // 5
    virtual int GetCollisionModelIndex() = 0;                  // 6
    virtual const model_t* GetCollisionModel() = 0;            // 7
    virtual const float* GetCollisionOrigin() const = 0;       // 8
    virtual const float* GetCollisionAngles() const = 0;       // 9
};
static const int kAbsOriginOffset = 0x6C4;
static const int kAbsAnglesOffset = 0x744;

class IServerEntity {
public:
    virtual ~IServerEntity() {}                   // 0  (IHandleEntity)
    virtual void SetRefEHandle(const void*) = 0;  // 1
    virtual const void* GetRefEHandle() const = 0;// 2
    virtual ICollideable* GetCollideable() = 0;   // 3  (IServerUnknown)
    virtual IServerNetworkable* GetNetworkable() = 0; // 4
    virtual void* GetBaseEntity() = 0;            // 5
    virtual int GetModelIndex() const = 0;        // 6  (IServerEntity)
};

class IPlayerInfoManager {
public:
    virtual void* GetPlayerInfo(edict_t*) = 0;    // 0
    virtual void* GetGlobalVars() = 0;            // 1
};

// The CGlobalVarsBase fields we read, by Episode One offset. Checked at
// ServerActivate: maxClients must match the engine's clientMax and the tick
// interval must be plausible.
struct GlobalVars {
    float realtime;          // 0
    int framecount;          // 4
    float absoluteframetime; // 8
    float curtime;           // 12
    float frametime;         // 16
    int maxClients;          // 20
    int tickcount;           // 24
    float interval_per_tick; // 28
};

class IVModelInfo {
public:
    virtual ~IVModelInfo() {}                                 // 0
    virtual const model_t* GetModel(int modelindex) = 0;      // 1
    virtual int GetModelIndex(const char* name) const = 0;    // 2
    virtual const char* GetModelName(const model_t* m) const = 0; // 3
};

// --- Logging ------------------------------------------------------------------

static char g_dir[MAX_PATH];
static FILE* g_log;

static void Log(const char* fmt, ...) {
    if (!g_log) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(g_log, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(g_log, fmt, ap);
    va_end(ap);
    fputc('\n', g_log);
    fflush(g_log);
}

// A string pointer we can print: readable, NUL-terminated within 256 bytes,
// and printable ASCII.
static const char* SafeStr(const char* s) {
    if (!s) return "(null)";
    __try {
        for (int i = 0; i < 256; ++i) {
            unsigned char c = (unsigned char)s[i];
            if (c == 0) return s;
            if (c < 0x20 || c > 0x7e) return "(non-text)";
        }
        return "(unterminated)";
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return "(bad pointer)";
    }
}

// --- Edict layout --------------------------------------------------------------
// Episode One: struct edict_t { int m_fStateFlags; int m_NetworkSerialNumber;
// IServerNetworkable* m_pNetworkable; IServerUnknown* m_pUnk; float freetime; }
// We confirm the stride and offsets instead of trusting them: an edict is
// valid when its networkable's GetEdict() returns the edict itself.

static unsigned char* g_edicts;
static int g_stride, g_netOff, g_unkOff;
static IVModelInfo* g_modelInfo;
static const int kMaxEdicts = 2048;
static const int kEdictFree = 1 << 1;

static IServerNetworkable* NetworkableAt(unsigned char* e, int netOff) {
    return *(IServerNetworkable**)(e + netOff);
}

static bool InImage(const void* p, bool executable) {
    MEMORY_BASIC_INFORMATION mbi;
    if (!p || !VirtualQuery(p, &mbi, sizeof mbi)) return false;
    if (mbi.State != MEM_COMMIT || mbi.Type != MEM_IMAGE) return false;
    const DWORD exec = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return executable ? (mbi.Protect & exec) != 0 : true;
}

// Only call a virtual on something that looks like a real C++ object: its
// vtable lives in a loaded module and the slot points at executable code.
// This keeps the layout probe from jumping through arbitrary data.
static bool LooksCallable(const void* obj, int slot) {
    __try {
        if (!obj) return false;
        void** vt = *(void***)obj;
        return InImage(vt, false) && InImage(vt[slot], true);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool EdictSelfConsistent(unsigned char* e, int netOff) {
    __try {
        IServerNetworkable* n = NetworkableAt(e, netOff);
        return LooksCallable(n, 5) && LooksCallable(n, 6) && (unsigned char*)n->GetEdict() == e;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// "module+rva" for an address inside a loaded image, for matching against a
// disassembly of that module.
static void DescribeAddr(const void* p, char* out, size_t size) {
    HMODULE mod = nullptr;
    char name[MAX_PATH] = "?";
    if (InImage(p, false) &&
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCSTR)p, &mod)) {
        GetModuleFileNameA(mod, name, MAX_PATH);
        const char* base = strrchr(name, '\\');
        snprintf(out, size, "%s+%X", base ? base + 1 : name, (unsigned)((const char*)p - (const char*)mod));
    } else {
        snprintf(out, size, "%p", p);
    }
}

// Raw view of the first edicts, logged when the layout probe finds nothing:
// each dword, and for each one that points at a C++ object, its vtable and
// first slots as module+rva.
static void LogRawEdicts(unsigned char* base, int count, int stride) {
    for (int i = 0; i < count; ++i) {
        unsigned char* e = base + i * stride;
        __try {
            unsigned* w = (unsigned*)e;
            Log("raw edict %d @%p: %08X %08X %08X %08X %08X %08X", i, e, w[0], w[1], w[2], w[3], w[4], w[5]);
            for (int off = 0; off < 24; off += 4) {
                void* obj = *(void**)(e + off);
                if (!LooksCallable(obj, 0)) continue;
                void** vt = *(void***)obj;
                char buf[128], line[1600];
                DescribeAddr(vt, buf, sizeof buf);
                int n = snprintf(line, sizeof line, "  +%d -> obj %p vtable %s slots:", off, obj, buf);
                for (int s = 0; s < 12 && n < (int)sizeof line - 80; ++s) {
                    DescribeAddr(vt[s], buf, sizeof buf);
                    n += snprintf(line + n, sizeof line - n, " [%d]%s", s, buf);
                }
                Log("%s", line);
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            Log("raw edict %d: exception", i);
        }
    }
}

static void DetectLayout(unsigned char* base, int edictCount) {
    int bestScore = 0;
    for (int stride = 16; stride <= 32; stride += 4) {
        for (int netOff = 4; netOff <= 12 && netOff + 8 <= stride; netOff += 4) {
            int score = 0;
            int limit = edictCount < 256 ? edictCount : 256;
            for (int i = 0; i < limit; ++i)
                if (EdictSelfConsistent(base + i * stride, netOff)) ++score;
            if (score > bestScore) {
                bestScore = score;
                g_stride = stride;
                g_netOff = netOff;
            }
        }
    }
    g_unkOff = g_netOff + 4;
    Log("layout: stride=%d networkable@+%d unknown@+%d (self-consistent edicts in first 256: %d)",
        g_stride, g_netOff, g_unkOff, bestScore);
    if (!bestScore) LogRawEdicts(base, 4, 20);
}

static bool DumpEntities(const char* reason) {
    if (!g_edicts || !g_stride) {
        Log("dump (%s): no edict list yet", reason);
        return false;
    }
    Log("dump (%s) begin", reason);
    int count = 0, withModel = 0, unkMismatch = 0, withOrigin = 0, collideMismatch = 0;
    for (int i = 0; i < kMaxEdicts; ++i) {
        unsigned char* e = g_edicts + i * g_stride;
        __try {
            int flags = *(int*)e;
            if (flags & kEdictFree) continue;
            if (!EdictSelfConsistent(e, g_netOff)) continue;
            IServerNetworkable* n = NetworkableAt(e, g_netOff);
            IServerEntity* u = *(IServerEntity**)(e + g_unkOff);
            const char* cls = SafeStr(n->GetClassName());

            int modelIndex = -1;
            const char* modelName = "";
            char where[96] = "origin=?";
            if (LooksCallable(u, 4) && LooksCallable(u, 6) && u->GetNetworkable() == n) {
                if (LooksCallable(u, 3)) {
                    ICollideable* c = u->GetCollideable();
                    const float* o = nullptr;
                    if (LooksCallable(c, 0) && LooksCallable(c, 8) && c->GetEntityHandle() == u)
                        o = c->GetCollisionOrigin();
                    if (o && o == (const float*)((const char*)u + kAbsOriginOffset)) {
                        const float* a = (const float*)((const char*)u + kAbsAnglesOffset);
                        snprintf(where, sizeof where, "origin=(%.1f %.1f %.1f) angles=(%.1f %.1f %.1f)",
                                 o[0], o[1], o[2], a[0], a[1], a[2]);
                        ++withOrigin;
                    } else if (collideMismatch++ < 3) {
                        char vt[128];
                        DescribeAddr(LooksCallable(c, 0) ? *(void**)c : nullptr, vt, sizeof vt);
                        Log("  collideable mismatch #%d: entity=%p collideable=%p vtable=%s origin=%p (expect %p)",
                            i, u, c, vt, o, (const char*)u + kAbsOriginOffset);
                    }
                }
                modelIndex = u->GetModelIndex();
                if (g_modelInfo && modelIndex > 0) {
                    const model_t* m = g_modelInfo->GetModel(modelIndex);
                    if (m) {
                        modelName = SafeStr(g_modelInfo->GetModelName(m));
                        ++withModel;
                    }
                }
            } else {
                ++unkMismatch;
            }
            Log("  #%4d %-32s %s model[%d]=%s", i, cls, where, modelIndex, modelName);
            ++count;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            Log("  #%4d (exception reading entity)", i);
        }
    }
    Log("dump (%s) end: %d entities, %d with model paths, %d with origins, "
        "%d unknown/networkable mismatches, %d collideable mismatches",
        reason, count, withModel, withOrigin, unkMismatch, collideMismatch);
    return count > 0 && unkMismatch == 0 && collideMismatch == 0;
}

// --- Wire writer -----------------------------------------------------------------
// Every server tick goes into the next ring slot of rustybridge.wire (next to
// this DLL) under a seqlock. A file-backed mapping rather than a named
// section: the game runs non-elevated, so it cannot create a Global\ name,
// and a Local\ one is invisible to readers in other sessions (SSH).

static GlobalVars* g_globals;
static unsigned char* g_wire;
static HANDLE g_wireFile, g_wireMap;
static wire::Control* g_control;
static HANDLE g_controlFile, g_controlMap;
static bool g_streamOk;
static int g_lastTick = -1;
static uint32_t g_faults;  // SEH faults in the tick walk and the damage hook
// Damage the hook captured since the last frame was written.
static wire::Damage g_pendingDamage[wire::kMaxDamage];
static uint32_t g_pendingCount, g_pendingDropped, g_damageTotal;
// Defense checks captured since the last frame.
static wire::Defense g_pendingDefense[wire::kMaxDefense];
static uint32_t g_defenseCount, g_defenseDropped, g_defenseTotal;
// Bullet-time setter calls since the last frame.
static wire::SlowTime g_pendingSlowTime[wire::kMaxSlowTime];
static uint32_t g_slowTimeCount, g_slowTimeDropped, g_slowTimeTotal;
// The wall clock frames are stamped with (seconds since Load).
static LARGE_INTEGER g_qpcStart, g_qpcFreq;

// --- Netprop offsets from send tables ----------------------------------------
// Each ServerClass carries the SendTable the engine networks it with, and
// every SendProp names a field and its offset in the entity. Walking that
// tree (nested data tables add their own offset) finds fields by name
// instead of guessing Arkane's class layouts. Episode One SendProp layout,
// 76 bytes; checked on the first table walked.
struct ServerClass { const char* networkName; struct SendTable* table; };
struct SendTable { unsigned char* props; int count; const char* name; };
const int kPropStride = 76, kPropType = 8, kPropName = 44, kPropFlags = 52, kPropTable = 64, kPropOffset = 68;
// DT_WORLD's first prop, read 2026-10-17: type 6 for a data table, so this
// build's SendPropType has DPT_VectorXY (Int, Float, Vector, VectorXY,
// String, Array, DataTable), unlike Episode One's.
const int kDptDataTable = 6;
const int kSpropExclude = 1 << 6;

struct NetProps { int sequence = -1, cycle = -1, rate = -1, poses = -1, animTime = -1; bool overlay = false; };
static int g_sendLayout;              // 0 unchecked, 1 ok, -1 wrong
// Arkane made CBaseEntity's health a float pair, m_fHealth/m_fMaxHealth, next
// to m_lifeState. Only some classes network them (shields do), but as
// CBaseEntity members their offsets hold for every entity.
static int g_healthOff = -1, g_maxHealthOff = -1, g_lifeStateOff = -1;
static int g_ownerOff = -1;           // CBaseEntity::m_hOwnerEntity
static int g_weaponOwnerOff = -1;     // CBaseCombatWeapon::m_hOwner
static int g_moveParentOff = -1;      // CBaseEntity::m_hMoveParent (sent as "moveparent")
static int g_parentAttachOff = -1;    // CBaseEntity::m_iParentAttachment (a byte)
static int g_viewOffsetOff = -1;      // CBasePlayer::m_vecViewOffset (sent as three floats, [0] first)

static bool IsText(const char* p) { return p && SafeStr(p) == p; }

static bool CheckSendLayout(SendTable* t) {
    bool sawTable = false;
    for (int i = 0; i < t->count; ++i) {
        unsigned char* sp = t->props + i * kPropStride;
        if (!IsText(*(const char**)(sp + kPropName))) return false;
        SendTable* dt = *(SendTable**)(sp + kPropTable);
        if (*(int*)(sp + kPropType) == kDptDataTable && dt && IsText(dt->name) && !strncmp(dt->name, "DT_", 3))
            sawTable = true;
    }
    return sawTable;
}

static void NoteLayerVector(unsigned char* elementProp);  // overlay layers, below

static void WalkSendTable(SendTable* t, int base, NetProps& p, int depth) {
    if (!t || depth > 16) return;
    for (int i = 0; i < t->count; ++i) {
        unsigned char* sp = t->props + i * kPropStride;
        if (*(int*)(sp + kPropFlags) & kSpropExclude) continue;
        int off = base + *(int*)(sp + kPropOffset);
        if (*(int*)(sp + kPropType) == kDptDataTable) {
            SendTable* dt = *(SendTable**)(sp + kPropTable);
            // v13: SendPropArray3 sends m_flPoseParameter as a data table of elements "000".., at the array's offset.
            if (!strcmp(*(const char**)(sp + kPropName), "m_flPoseParameter") && p.poses < 0) p.poses = off;
            if (dt && IsText(dt->name) && !strcmp(dt->name, "DT_OverlayVars")) p.overlay = true;
            if (dt && IsText(dt->name) && !strcmp(dt->name, "DT_Animationlayer")) NoteLayerVector(sp);
            WalkSendTable(dt, off, p, depth + 1);
            continue;
        }
        const char* name = *(const char**)(sp + kPropName);
        if (!strcmp(name, "m_nSequence") && p.sequence < 0) p.sequence = off;
        else if (!strcmp(name, "m_flCycle") && p.cycle < 0) p.cycle = off;
        else if (!strcmp(name, "m_flPlaybackRate") && p.rate < 0) p.rate = off;
        else if (!strcmp(name, "m_flAnimTime") && p.animTime < 0) p.animTime = off;
        else if (!strcmp(name, "m_fHealth") && g_healthOff < 0) { g_healthOff = off; Log("netprops: m_fHealth @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_fMaxHealth") && g_maxHealthOff < 0) { g_maxHealthOff = off; Log("netprops: m_fMaxHealth @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_hOwner") && g_weaponOwnerOff < 0) { g_weaponOwnerOff = off; Log("netprops: m_hOwner @+0x%X (CBaseCombatWeapon)", off); }
        else if (!strcmp(name, "moveparent") && g_moveParentOff < 0) { g_moveParentOff = off; Log("netprops: m_hMoveParent @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_iParentAttachment") && g_parentAttachOff < 0) { g_parentAttachOff = off; Log("netprops: m_iParentAttachment @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_hOwnerEntity") && g_ownerOff < 0) { g_ownerOff = off; Log("netprops: m_hOwnerEntity @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_vecViewOffset[0]") && g_viewOffsetOff < 0) { g_viewOffsetOff = off; Log("netprops: m_vecViewOffset @+0x%X (CBasePlayer)", off); }
        else if (!strcmp(name, "m_lifeState") && g_lifeStateOff < 0) { g_lifeStateOff = off; Log("netprops: m_lifeState @+0x%X (CBaseEntity, all classes)", off); }
    }
}

static NetProps DiscoverNetProps(IServerNetworkable* n, const char* className) {
    NetProps p;
    if (g_sendLayout < 0) return p;
    __try {
        ServerClass* sc = (ServerClass*)n->GetServerClass();
        if (!sc || !sc->table) return p;
        if (g_sendLayout == 0) {
            g_sendLayout = CheckSendLayout(sc->table) ? 1 : -1;
            if (g_sendLayout < 0) {
                unsigned* w = (unsigned*)sc->table->props;
                Log("netprops: SendProp layout check failed on %s; first prop: %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X",
                    SafeStr(sc->table->name), w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8], w[9], w[10], w[11], w[12], w[13], w[14], w[15], w[16], w[17], w[18]);
                return p;
            }
        }
        WalkSendTable(sc->table, 0, p, 0);
        if (p.sequence >= 0 || p.cycle >= 0)
            Log("netprops: %s (%s/%s) sequence@+0x%X cycle@+0x%X rate@+0x%X poses@+0x%X animtime@+0x%X", SafeStr(className), SafeStr(sc->networkName),
                SafeStr(sc->table->name), p.sequence, p.cycle, p.rate, p.poses, p.animTime);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("netprops: exception walking %s's send table", SafeStr(className));
        p = NetProps();
    }
    return p;
}

struct ClassSlot { const char* ptr; uint16_t id; NetProps props; };
static ClassSlot g_classHash[2 * wire::kMaxClasses];
static uint32_t g_classCount;
static bool g_modelKnown[wire::kMaxModels];

static wire::Header* WireHeader() { return (wire::Header*)g_wire; }

static void WireOpen() {
    char path[MAX_PATH];
    snprintf(path, sizeof path, "%s\\rustybridge.wire", g_dir);
    g_wireFile = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                             nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (g_wireFile == INVALID_HANDLE_VALUE) { Log("wire: CreateFile failed (%lu)", GetLastError()); return; }
    g_wireMap = CreateFileMappingA(g_wireFile, nullptr, PAGE_READWRITE, 0, wire::kFileBytes, nullptr);
    if (!g_wireMap) { Log("wire: CreateFileMapping failed (%lu)", GetLastError()); return; }
    g_wire = (unsigned char*)MapViewOfFile(g_wireMap, FILE_MAP_WRITE, 0, 0, wire::kFileBytes);
    if (!g_wire) { Log("wire: MapViewOfFile failed (%lu)", GetLastError()); return; }

    memset(g_wire, 0, wire::kFileBytes);
    wire::Header* h = WireHeader();
    h->version = wire::kVersion;
    h->header_bytes = sizeof(wire::Header);
    h->slot_count = wire::kSlotCount;
    h->slot_bytes = wire::kSlotBytes;
    h->max_entities = wire::kMaxEntities;
    h->max_classes = wire::kMaxClasses;
    h->class_name_bytes = wire::kClassNameBytes;
    h->max_models = wire::kMaxModels;
    h->model_path_bytes = wire::kModelPathBytes;
    h->classes_offset = wire::kClassesOffset;
    h->models_offset = wire::kModelsOffset;
    h->slots_offset = wire::kSlotsOffset;
    h->writer_pid = GetCurrentProcessId();
    InterlockedExchange64((volatile LONG64*)&h->magic, (LONG64)wire::kMagic);
    Log("wire: %s, %u bytes", path, wire::kFileBytes);
}

static void WireClose() {
    if (g_wire) UnmapViewOfFile(g_wire);
    if (g_wireMap) CloseHandle(g_wireMap);
    if (g_wireFile && g_wireFile != INVALID_HANDLE_VALUE) CloseHandle(g_wireFile);
    g_wire = nullptr; g_wireMap = nullptr; g_wireFile = nullptr;
}

// --- Holding the donor --------------------------------------------------------
//
// A reader holds the donor's frame through rustybridge.control. The tap holds
// inside its own GameFrame, right after writing a tick's Wire frame: the
// whole host frame waits there (the client too, in single player), so the
// game shows that tick and the Wire's latest frame is it. Each GameFrame is
// one server tick, so releasing one call steps exactly one tick, even when
// the engine runs several ticks in a frame to catch up after a hold. While
// held the tap peeks the message queue (without removing anything) so
// Windows doesn't mark the window as not responding. A hold lapses at its
// lease, so a reader that went away can't freeze the game for good.

static void ControlOpen() {
    char path[MAX_PATH];
    snprintf(path, sizeof path, "%s\\rustybridge.control", g_dir);
    g_controlFile = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (g_controlFile == INVALID_HANDLE_VALUE) { Log("control: CreateFile failed (%lu)", GetLastError()); g_controlFile = nullptr; return; }
    g_controlMap = CreateFileMappingA(g_controlFile, nullptr, PAGE_READWRITE, 0, wire::kControlFileBytes, nullptr);
    if (!g_controlMap) { Log("control: CreateFileMapping failed (%lu)", GetLastError()); return; }
    g_control = (wire::Control*)MapViewOfFile(g_controlMap, FILE_MAP_WRITE, 0, 0, wire::kControlFileBytes);
    if (!g_control) { Log("control: MapViewOfFile failed (%lu)", GetLastError()); return; }
    memset(g_control, 0, wire::kControlFileBytes);
    g_control->version = wire::kControlVersion;
    g_control->writer_pid = GetCurrentProcessId();
    g_control->held_tick = -1;
    InterlockedExchange64((volatile LONG64*)&g_control->magic, (LONG64)wire::kControlMagic);
    Log("control: %s", path);
}

static void ControlClose() {
    if (g_control) { g_control->hold = 0; UnmapViewOfFile(g_control); }
    if (g_controlMap) CloseHandle(g_controlMap);
    if (g_controlFile) CloseHandle(g_controlFile);
    g_control = nullptr; g_controlMap = nullptr; g_controlFile = nullptr;
}

static bool HoldLapsed() {
    if (GetTickCount64() <= g_control->lease_until_ms) return false;
    InterlockedExchange((volatile LONG*)&g_control->hold, 0);
    ++g_control->holds_lapsed;
    Log("control: hold lapsed at tick %d", g_globals->tickcount);
    return true;
}

// Called once per server tick, after its Wire frame is written.
static void HoldIfAsked() {
    volatile wire::Control* c = g_control;
    if (!c || !c->hold || HoldLapsed()) return;
    if (c->step_requests != c->steps_done) { ++c->steps_done; return; }  // a step released this tick
    InterlockedExchange((volatile LONG*)&c->held_tick, g_globals->tickcount);
    MSG msg;
    while (c->hold && c->step_requests == c->steps_done && !HoldLapsed()) {
        PeekMessageA(&msg, nullptr, 0, 0, PM_NOREMOVE);
        Sleep(1);
    }
    if (c->hold) ++c->steps_done;
    InterlockedExchange((volatile LONG*)&c->held_tick, -1);
}

// client.dll and engine.dll export their factories as CreateInterface.
typedef void* (*CreateInterfaceFnRaw)(const char*, int*);

// --- A hidden donor -----------------------------------------------------------
//
// Launched with -noshaderapi the game draws nothing (shaderapiempty.dll), but
// it still opens a fullscreen window and switches the desktop to 1024x768,
// ignoring -window, -w and -h. With -rbhidden on its command line as well, the
// tap puts the desktop's own display mode back and hides the game's windows
// once a level is running. The server simulation, the plugin and the Wire
// don't need either.

static BOOL CALLBACK HideOwnWindow(HWND hwnd, LPARAM count) {
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == GetCurrentProcessId() && IsWindowVisible(hwnd)) {
        ShowWindow(hwnd, SW_HIDE);
        ++*(int*)count;
    }
    return TRUE;
}

static void HideIfAsked() {
    const char* line = GetCommandLineA();
    if (!line || !strstr(line, "-rbhidden")) return;
    LONG mode = ChangeDisplaySettingsA(nullptr, 0);
    int hidden = 0;
    EnumWindows(HideOwnWindow, (LPARAM)&hidden);
    Log("hidden donor: display mode restored (%ld), %d window(s) hidden%s", mode, hidden,
        strstr(line, "-noshaderapi") ? "" : "; without -noshaderapi the game still renders");
}

// New level: model indices and class name pointers are only valid per level.
static void ClearPendingDamage();

static void WireLevelInit(const char* mapName) {
    g_streamOk = false;
    ClearPendingDamage();
    g_lastTick = -1;
    memset(g_classHash, 0, sizeof g_classHash);
    memset(g_modelKnown, 0, sizeof g_modelKnown);
    g_classCount = 0;
    if (!g_wire) return;
    wire::Header* h = WireHeader();
    memset(g_wire + wire::kClassesOffset, 0, wire::kSlotsOffset - wire::kClassesOffset);
    strncpy_s(h->map, mapName ? mapName : "", _TRUNCATE);
    InterlockedIncrement((volatile LONG*)&h->level_serial);
}

// Entities with one class name share one C++ class, so the class name
// pointer (a pooled string) keys both the class table row and the netprops.
static const ClassSlot kNoClass = { nullptr, 0xFFFF };

static const ClassSlot& ClassFor(IServerNetworkable* n) {
    const char* name = n->GetClassName();
    uint32_t mask = 2 * wire::kMaxClasses - 1;
    for (uint32_t k = ((uint32_t)(uintptr_t)name * 2654435761u) >> 20 & mask;; k = (k + 1) & mask) {
        ClassSlot& slot = g_classHash[k];
        if (slot.ptr == name) return slot;
        if (slot.ptr) continue;
        if (g_classCount == wire::kMaxClasses) return kNoClass;
        slot.ptr = name;
        slot.id = (uint16_t)g_classCount++;
        slot.props = DiscoverNetProps(n, name);
        char* row = (char*)g_wire + wire::kClassesOffset + slot.id * wire::kClassNameBytes;
        strncpy_s(row, wire::kClassNameBytes, SafeStr(name), _TRUNCATE);
        return slot;
    }
}

static void NoteModel(int index) {
    if (index <= 0 || index >= (int)wire::kMaxModels || g_modelKnown[index]) return;
    g_modelKnown[index] = true;
    const model_t* m = g_modelInfo ? g_modelInfo->GetModel(index) : nullptr;
    char* row = (char*)g_wire + wire::kModelsOffset + index * wire::kModelPathBytes;
    strncpy_s(row, wire::kModelPathBytes, m ? SafeStr(g_modelInfo->GetModelName(m)) : "", _TRUNCATE);
}

// --- Overlay layers ------------------------------------------------------------------
// CBaseAnimatingOverlay::m_AnimOverlay is a CUtlVector<CAnimationLayer>:
// element pointer at +0, count at +12. Its offset (0xF78 in
// CBaseCombatCharacter's datadesc) and element stride come from the send
// table: DT_OverlayVars networks it through a SendPropUtlVector, whose
// per-element data-table props carry a CSendPropExtra_UtlVector as extra data
// (+72): proxy, var proxy, ensure-capacity, element stride (+12), vector
// offset (+16), max elements (+20). Element fields from CAnimationLayer's
// datadesc and DT_Animationlayer: flags +0x0, sequence +0x8, cycle +0xC,
// playback rate +0x14, weight +0x18, order +0x34.
const int kPropExtra = 72;
const int kMaxOverlays = 15;   // DT_OverlayVars' utlvector holds 15
static int g_layerVector = -1, g_layerStride;
static uint32_t g_layerRejects;  // active layers whose fields were out of range (layout doubt)

static void NoteLayerVector(unsigned char* elementProp) {
    if (g_layerStride) return;
    __try {
        const int* extra = *(const int**)(elementProp + kPropExtra);
        if (!extra) return;
        int stride = extra[3], offset = extra[4], max = extra[5];
        Log("layers: CUtlVector<CAnimationLayer> at +0x%X, element stride 0x%X, max %d", offset, stride, max);
        if (stride >= 0x38 && stride <= 0x200 && offset > 0 && max > 0 && max <= 64) { g_layerVector = offset; g_layerStride = stride; }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("layers: exception reading the utlvector extra data");
    }
}

// Appends this entity's active layers to the frame's layer array.
static void WriteLayers(unsigned char* entity, uint16_t index, wire::Layer* out, uint32_t& count, uint32_t& dropped) {
    if (!g_layerStride) return;
    unsigned char* mem = *(unsigned char**)(entity + g_layerVector);
    int size = *(int*)(entity + g_layerVector + 12);
    if (!mem || size <= 0 || size > kMaxOverlays) return;
    for (int k = 0; k < size; ++k) {
        unsigned char* l = mem + k * g_layerStride;
        int flags = *(int*)l;
        float weight = *(float*)(l + 0x18), cycle = *(float*)(l + 0xC);
        if (!(flags & 1) || !(weight > 0.0f)) continue;
        if (!(weight <= 1.001f) || !(cycle >= -0.001f && cycle <= 1.001f)) { ++g_layerRejects; continue; }
        if (count == wire::kMaxLayers) { ++dropped; continue; }
        wire::Layer& w = out[count++];
        w.index = index;
        w.slot = (uint8_t)k;
        w.flags = (uint8_t)flags;
        w.sequence = (int16_t)*(int*)(l + 0x8);
        w.order = (int16_t)*(int*)(l + 0x34);
        w.cycle = *(float*)(l + 0xC);
        w.weight = weight;
        w.playback_rate = *(float*)(l + 0x14);
    }
}

// The per-tick walk trusts the layout DumpEntities confirmed at
// ServerActivate, so it skips the VirtualQuery checks; one SEH frame per
// entity still turns a bad pointer into a counted fault.
static uint32_t SerialAt(uint16_t index);
static uint16_t HandleIndex(uint32_t h);

// --- Links and player state (v7) ------------------------------------------------------
// Offsets from server.dll's data descriptions and send props (docs/wire.md
// "Knockdowns and slow motion"). Each is checked at run time: a link counts
// only when its handle names a live entity of the expected class with the
// handle's serial, and player fields are read only from the "player" class.
static const int kRagdollFallOffset = 0x29E4;   // CNPC_BaseArx::m_hRagdollFall (EHANDLE)
static const int kUnragdollOffset = 0x16A0;     // prop_ragdoll m_hUnragdoll (EHANDLE), the NPC it stands in for
static const int kBulletTimeStartOffset = 0x20A4, kBulletTimeDurationOffset = 0x20A8, kBulletTimeFactorOffset = 0x20AC;
static const int kInBulletTimeOffset = 0x20B0, kBulletTimeSoundOffset = 0x20B1;
static const int kMaxAdrenalineOffset = 0x29D4, kAdrenalineOffset = 0x29D8;
// v12: CBasePlayer's button words, from its datadesc (built at start-up: each field's offset is the immediate
// written after its name, docs/wire.md "The player's input").
static const int kButtonsOffset = 0x1FB8, kButtonsPressedOffset = 0x1FC8, kButtonsReleasedOffset = 0x1FCC;
// v15: CMM_Player's stats and equipment, from its datadesc (the same start-up table) and send props (docs/wire.md,
// "The player's stats").
static const int kStaminaOffset = 0x29C4, kMaxStaminaOffset = 0x29C0, kManaOffset = 0x29BC, kMaxManaOffset = 0x29B0;
static const int kSkillModulesOffset = 0x2AF0, kSkillPointsOffset = 0x2B3C;
// v16: m_skillLevel points at one int per skill; the game's skill table has 26 (server.dll+0x6A7674).
static const int kSkillLevelsOffset = 0x2AC8, kSkillCount = 26;
static const int kActiveWeaponOffset = 0x18F0, kActiveShieldOffset = 0x18F4, kActiveHelmetOffset = 0x18F8;
static const int kActiveRingOffset = 0x18FC, kActiveArmorOffset = 0x2250;

static const char* ClassNameAt(uint16_t index) {
    if (index == wire::kNoEntity || !g_edicts) return nullptr;
    unsigned char* e = g_edicts + index * g_stride;
    if (*(int*)e & kEdictFree) return nullptr;
    IServerNetworkable* n = NetworkableAt(e, g_netOff);
    return n ? n->GetClassName() : nullptr;
}

// The entity a handle field names, if it is live, has the handle's serial and
// a class starting with prefix.
static uint16_t LinkedEntity(const unsigned char* ent, int offset, const char* prefix, uint32_t& serial) {
    serial = 0;
    uint32_t h = *(const uint32_t*)(ent + offset);
    uint16_t index = HandleIndex(h);
    if (index == wire::kNoEntity || SerialAt(index) != h >> 12) return wire::kNoEntity;
    const char* cls = ClassNameAt(index);
    if (!cls || strncmp(cls, prefix, strlen(prefix)) != 0) return wire::kNoEntity;
    serial = h >> 12;
    return index;
}

static void WriteLink(const char* cls, const unsigned char* ent, wire::Entity& w) {
    w.link = wire::kNoEntity;
    w.link_serial = 0;
    w.reserved = 0;
    if (!cls) return;
    __try {
        if (!strncmp(cls, "npc_", 4)) w.link = LinkedEntity(ent, kRagdollFallOffset, "prop_ragdoll", w.link_serial);
        else if (!strcmp(cls, "prop_ragdoll")) w.link = LinkedEntity(ent, kUnragdollOffset, "npc_", w.link_serial);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        w.link = wire::kNoEntity;
        w.link_serial = 0;
    }
}

// The entity a handle names, if it is live and has the handle's serial.
static uint16_t HandleTarget(uint32_t h, uint32_t& serial) {
    serial = 0;
    uint16_t index = HandleIndex(h);
    if (index == wire::kNoEntity || SerialAt(index) != h >> 12) return wire::kNoEntity;
    serial = h >> 12;
    return index;
}

// v10: what the entity is attached to (its move parent and the parent's
// attachment), and who owns it: m_hOwnerEntity, or a weapon's m_hOwner when
// that is empty (the player's weapons keep their wielder there).
static void WriteParent(const char* cls, const unsigned char* ent, wire::Entity& w) {
    w.parent = w.owner = wire::kNoEntity;
    w.parent_serial = w.owner_serial = 0;
    w.parent_attachment = 0;
    w.reserved2 = 0;
    w.reserved3 = 0;
    __try {
        if (g_moveParentOff >= 0) {
            w.parent = HandleTarget(*(const uint32_t*)(ent + g_moveParentOff), w.parent_serial);
            if (w.parent != wire::kNoEntity && g_parentAttachOff >= 0) w.parent_attachment = *(ent + g_parentAttachOff);
        }
        if (g_ownerOff >= 0) {
            w.owner = HandleTarget(*(const uint32_t*)(ent + g_ownerOff), w.owner_serial);
            if (w.owner == wire::kNoEntity && g_weaponOwnerOff >= 0 && cls && !strncmp(cls, "weapon_", 7))
                w.owner = HandleTarget(*(const uint32_t*)(ent + g_weaponOwnerOff), w.owner_serial);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        w.parent = w.owner = wire::kNoEntity;
        w.parent_serial = w.owner_serial = 0;
        w.parent_attachment = 0;
    }
}

static void WritePlayerState(wire::SlotHeader* s) {
    s->player = wire::kNoEntity;
    s->in_bullet_time = s->bullet_time_sound = 0;
    s->bullet_time_start = s->bullet_time_duration = s->bullet_time_factor = 0;
    s->adrenaline = s->max_adrenaline = 0;
    const char* cls = ClassNameAt(1);
    if (!cls || strcmp(cls, "player") != 0) return;
    __try {
        const unsigned char* p = *(const unsigned char**)(g_edicts + 1 * g_stride + g_unkOff);
        if (!p) return;
        s->in_bullet_time = p[kInBulletTimeOffset];
        s->bullet_time_sound = p[kBulletTimeSoundOffset];
        s->bullet_time_start = *(const float*)(p + kBulletTimeStartOffset);
        s->bullet_time_duration = *(const float*)(p + kBulletTimeDurationOffset);
        s->bullet_time_factor = *(const float*)(p + kBulletTimeFactorOffset);
        s->adrenaline = *(const float*)(p + kAdrenalineOffset);
        s->max_adrenaline = *(const float*)(p + kMaxAdrenalineOffset);
        s->player = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
}

// v14: the player's user commands as CBasePlayer::ProcessUsercmds receives them (hooked below), gathered
// over the tick and written with the frame.
static struct {
    uint8_t commands;
    float forward, side, up;
    uint32_t buttons, actions;
    uint8_t impulse;
    int32_t number;
} g_commands;

static void WritePlayerInput(wire::PlayerInput* in) {
    memset(in, 0, sizeof *in);
    in->commands = g_commands.commands;
    in->forward_move = g_commands.forward;
    in->side_move = g_commands.side;
    in->up_move = g_commands.up;
    in->command_buttons = g_commands.buttons;
    in->actions = g_commands.actions;
    in->impulse = g_commands.impulse;
    in->command_number = g_commands.number;
    memset(&g_commands, 0, sizeof g_commands);
    in->player = wire::kNoEntity;
    const char* cls = ClassNameAt(1);
    if (!cls || strcmp(cls, "player") != 0) return;
    __try {
        const unsigned char* p = *(const unsigned char**)(g_edicts + 1 * g_stride + g_unkOff);
        if (!p) return;
        in->buttons = *(const uint32_t*)(p + kButtonsOffset);
        in->pressed = *(const uint32_t*)(p + kButtonsPressedOffset);
        in->released = *(const uint32_t*)(p + kButtonsReleasedOffset);
        in->player = 1;
        in->valid = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
}

static void WritePlayerStats(wire::PlayerStats* st) {
    memset(st, 0, sizeof *st);
    st->player = st->weapon = st->shield = st->helmet = st->ring = st->armor = wire::kNoEntity;
    const char* cls = ClassNameAt(1);
    if (!cls || strcmp(cls, "player") != 0) return;
    __try {
        const unsigned char* p = *(const unsigned char**)(g_edicts + 1 * g_stride + g_unkOff);
        if (!p) return;
        st->stamina = *(const float*)(p + kStaminaOffset);
        st->max_stamina = *(const float*)(p + kMaxStaminaOffset);
        st->mana = *(const float*)(p + kManaOffset);
        st->max_mana = *(const float*)(p + kMaxManaOffset);
        st->modules[0] = *(const uint32_t*)(p + kSkillModulesOffset);
        st->modules[1] = *(const uint32_t*)(p + kSkillModulesOffset + 4);
        st->skill_points = *(const int32_t*)(p + kSkillPointsOffset);
        if (const int32_t* levels = *(const int32_t* const*)(p + kSkillLevelsOffset))
            for (int i = 0; i < kSkillCount; i++) st->skill_levels[i] = (uint8_t)(levels[i] < 0 ? 0 : levels[i] > 255 ? 255 : levels[i]);
        st->weapon = HandleTarget(*(const uint32_t*)(p + kActiveWeaponOffset), st->weapon_serial);
        st->shield = HandleTarget(*(const uint32_t*)(p + kActiveShieldOffset), st->shield_serial);
        st->helmet = HandleTarget(*(const uint32_t*)(p + kActiveHelmetOffset), st->helmet_serial);
        st->ring = HandleTarget(*(const uint32_t*)(p + kActiveRingOffset), st->ring_serial);
        st->armor = HandleTarget(*(const uint32_t*)(p + kActiveArmorOffset), st->armor_serial);
        st->player = 1;
        st->valid = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
        st->valid = 0;
    }
}

// --- The client's view (Wire v8) --------------------------------------------
//
// In single player client.dll and engine.dll run in the tap's process. Their
// interfaces keep the 2006 SDK's layouts with Arkane's changes, read from a
// probe of the live vtables and their code (docs/wire.md, "The client's
// view"). Nothing here is called until its code is what was read:
// - IVEngineClient (VEngineClient012) slot 21 is GetViewAngles. It loads
//   cl.viewangles from a fixed address (fld dword [abs]; mov eax,[esp+4]), so
//   the tap reads the three floats at that address instead of calling it.
//   Slot 14 is GetLocalPlayer (mov eax,[abs]; add eax,1; ret), read the same way.
//   Slot 9 is ClientCmd (mov eax,[esp+4]; push eax; call Cbuf_AddText ...).
// - IClientEntityList (VClientEntityList003) slot 3 is GetClientEntity(i)
//   (mov eax,[esp+4]; push eax; add ecx,-0x10014; call ...). It is called.
// - An IClientEntity's slots 9 and 10 are GetAbsOrigin and GetAbsAngles (no
//   Release before them, unlike the SDK). Each calls CalcAbsolutePosition and
//   returns a field; GetAbsAngles picks one of two by an entity flag (0x200).
//   They're called, per entity, only when their code is that.
// - IBaseClientDLL (VClient013) slot 23 is View_Render: it calls the global
//   `view` (IViewRender*) through mov ecx,[abs], whose address the tap reads.
//   IViewRender slot 12 is GetViewSetup (lea eax,[ecx+0x8C]; ret), the
//   CViewSetup the client last drew with: the effective camera.
// - IBaseClientDLL slot 5 is GetAllClasses (mov eax,[abs]; ret): the client's
//   RecvTables give C_BaseViewModel's m_nSequence and m_flCycle offsets.

typedef void* (__thiscall* GetClientEntityFn)(void* self, int index);
typedef void (__thiscall* ClientCmdFn)(void* self, const char* text);

static const float* g_viewAngles;       // cl.viewangles
static const int* g_playerSlot;         // GetLocalPlayer's slot: the local player is slot + 1
static void* g_entityList;
static GetClientEntityFn g_getClientEntity;
static void* g_engineClient;
static ClientCmdFn g_clientCmd;
static bool g_clientTried;
static uint16_t g_viewmodelIndex = wire::kNoEntity;
static unsigned char** g_viewRender;     // &view: the client's IViewRender*
static int g_vmSequenceOff = -1, g_vmCycleOff = -1;  // C_BaseViewModel m_nSequence, m_flCycle
// Its overlay layers, m_AnimOverlay: a CUtlVector<C_AnimationLayer> received
// through a RecvPropUtlVector, whose extra data (CRecvPropExtra_UtlVector:
// proxies, resize, capacity, then element stride, vector offset, max) says
// where the vector is and how far apart its elements are. Field offsets in an
// element come from DT_Animationlayer.
static int g_vmLayersOff = -1, g_vmLayerStride = 0, g_vmLayerMax = 0;
static int g_vmLayerSeqOff = -1, g_vmLayerCycleOff = -1, g_vmLayerWeightOff = -1, g_vmLayerOrderOff = -1;

// The client's RecvTables: ClientClass {create, createEvent, networkName,
// table, next, id} and RecvProp of 60 bytes {name, type, flags, ... table at
// 40, offset at 44}, Valve's 2006 layout; a data table prop has type 6, as on
// the server. Read 2026-10-18 from DT_BaseViewModel's props.
struct RecvTableRaw { unsigned char* props; int count; void* decoder; const char* name; };
const int kRecvStride = 60, kRecvType = 4, kRecvTable = 40, kRecvOffset = 44;

const int kRecvExtra = 20;  // RecvProp::m_pExtraData

static void NoteClientLayers(RecvTableRaw* vec, int base) {
    // _ST_m_AnimOverlay_N: a length proxy table, then one DT_Animationlayer per element.
    if (!vec || vec->count < 2) return;
    unsigned char* element = vec->props + 1 * kRecvStride;
    const int* extra = *(const int**)(element + kRecvExtra);
    RecvTableRaw* layer = *(RecvTableRaw**)(element + kRecvTable);
    if (!extra || !layer || !IsText(layer->name) || strcmp(layer->name, "DT_Animationlayer")) return;
    int stride = extra[4], offset = extra[5], max = extra[6];
    if (stride < 16 || stride > 256 || max < 1 || max > 64 || offset <= 0 || offset > 0x10000) {
        Log("client view: m_AnimOverlay extra data looks wrong (stride %d offset %d max %d); no client viewmodel layers", stride, offset, max);
        return;
    }
    for (int i = 0; i < layer->count; ++i) {
        unsigned char* rp = layer->props + i * kRecvStride;
        const char* name = *(const char**)rp;
        int off = *(int*)(rp + kRecvOffset);
        if (!IsText(name)) return;
        if (!strcmp(name, "sequence")) g_vmLayerSeqOff = off;
        else if (!strcmp(name, "cycle")) g_vmLayerCycleOff = off;
        else if (!strcmp(name, "weight")) g_vmLayerWeightOff = off;
        else if (!strcmp(name, "order")) g_vmLayerOrderOff = off;
    }
    if (g_vmLayerSeqOff < 0 || g_vmLayerCycleOff < 0 || g_vmLayerWeightOff < 0 || g_vmLayerOrderOff < 0) return;
    g_vmLayersOff = base + offset;
    g_vmLayerStride = stride;
    g_vmLayerMax = max;
    Log("client view: viewmodel layers: CUtlVector @+0x%X, %d bytes apart, at most %d", g_vmLayersOff, stride, max);
}

static void WalkRecvTable(RecvTableRaw* t, int base, int depth) {
    if (!t || depth > 16 || !IsText(t->name)) return;
    for (int i = 0; i < t->count; ++i) {
        unsigned char* rp = t->props + i * kRecvStride;
        const char* name = *(const char**)rp;
        if (!IsText(name)) return;
        int off = base + *(int*)(rp + kRecvOffset);
        RecvTableRaw* dt = *(RecvTableRaw**)(rp + kRecvTable);
        if (*(int*)(rp + kRecvType) == kDptDataTable && !strcmp(name, "m_AnimOverlay") && g_vmLayersOff < 0) {
            NoteClientLayers(dt, base);
            continue;
        }
        if (*(int*)(rp + kRecvType) == kDptDataTable) { WalkRecvTable(dt, off, depth + 1); continue; }
        if (!strcmp(name, "m_nSequence") && g_vmSequenceOff < 0) g_vmSequenceOff = off;
        else if (!strcmp(name, "m_flCycle") && g_vmCycleOff < 0) g_vmCycleOff = off;
    }
}

static void FindViewmodelAnimation(unsigned char* cls) {
    for (int n = 0; cls && n < 1024; cls = *(unsigned char**)(cls + 16), n++) {
        const char* name = *(const char**)(cls + 8);
        if (IsText(name) && !strcmp(name, "CBaseViewModel")) { WalkRecvTable(*(RecvTableRaw**)(cls + 12), 0, 0); return; }
    }
    Log("client view: no CBaseViewModel client class");
}

static bool CodeIs(const unsigned char* code, const char* pattern) {  // pattern: hex bytes, ?? for any
    for (int i = 0; pattern[0]; i++) {
        while (*pattern == ' ') pattern++;
        if (!*pattern) break;
        if (pattern[0] != '?') {
            unsigned v = 0;
            sscanf_s(pattern, "%2x", &v);
            if (code[i] != (unsigned char)v) return false;
        }
        pattern += 2;
    }
    return true;
}

static void ClientOpen() {
    g_clientTried = true;
    HMODULE client = GetModuleHandleA("client.dll"), engine = GetModuleHandleA("engine.dll");
    if (!client || !engine) { Log("client view: client.dll or engine.dll not loaded"); return; }
    auto clientFactory = (CreateInterfaceFnRaw)GetProcAddress(client, "CreateInterface");
    auto engineFactory = (CreateInterfaceFnRaw)GetProcAddress(engine, "CreateInterface");
    __try {
        void* engineClient = engineFactory ? engineFactory("VEngineClient012", nullptr) : nullptr;
        void* entityList = clientFactory ? clientFactory("VClientEntityList003", nullptr) : nullptr;
        if (!engineClient || !entityList) { Log("client view: interfaces missing"); return; }
        unsigned char** ev = *(unsigned char***)engineClient;
        unsigned char** lv = *(unsigned char***)entityList;
        if (CodeIs(ev[21], "D9 05 ?? ?? ?? ?? 8B 44 24 04 D9 18"))
            g_viewAngles = *(const float**)(ev[21] + 2);
        else
            Log("client view: IVEngineClient slot 21 isn't GetViewAngles as read; no view angles");
        if (CodeIs(ev[14], "A1 ?? ?? ?? ?? 83 C0 01 C3"))
            g_playerSlot = *(const int**)(ev[14] + 1);
        else
            Log("client view: IVEngineClient slot 14 isn't GetLocalPlayer as read; no client player");
        if (CodeIs(ev[9], "8B 44 24 04 50 E8")) { g_engineClient = engineClient; g_clientCmd = (ClientCmdFn)ev[9]; }
        else Log("client view: IVEngineClient slot 9 isn't ClientCmd as read; no console commands");
        if (CodeIs(lv[3], "8B 44 24 04 50 81 C1 EC FF FE FF E8")) { g_entityList = entityList; g_getClientEntity = (GetClientEntityFn)lv[3]; }
        else Log("client view: IClientEntityList slot 3 isn't GetClientEntity as read; no client entities");
        void* clientDll = clientFactory ? clientFactory("VClient013", nullptr) : nullptr;
        unsigned char** cv = clientDll ? *(unsigned char***)clientDll : nullptr;
        if (cv && CodeIs(cv[23], "8B 44 24 04 83 78 08 00 74 ?? 83 78 0C 00 74 ?? 8B 0D"))
            g_viewRender = *(unsigned char***)(cv[23] + 0x12);
        else Log("client view: VClient013 slot 23 isn't View_Render as read; no camera");
        if (cv && CodeIs(cv[5], "A1 ?? ?? ?? ?? C3")) FindViewmodelAnimation(**(unsigned char***)(cv[5] + 1));
        else Log("client view: VClient013 slot 5 isn't GetAllClasses as read; no client viewmodel animation");
        Log("client view: view angles %p, local player slot %p, entity list %s, ClientCmd %s, camera %s, viewmodel sequence@+0x%X cycle@+0x%X",
            g_viewAngles, g_playerSlot, g_getClientEntity ? "ok" : "off", g_clientCmd ? "ok" : "off", g_viewRender ? "ok" : "off",
            g_vmSequenceOff, g_vmCycleOff);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("client view: exception reading the interfaces");
        g_viewAngles = nullptr; g_playerSlot = nullptr; g_getClientEntity = nullptr; g_clientCmd = nullptr;
    }
}

// An entity's abs origin and angles, from the displacements in its GetAbsOrigin/GetAbsAngles code.
typedef const float* (__thiscall* AbsGetterFn)(void* self);
static bool g_absLogged;

// An entity's abs origin and angles through its own getters, once their code
// is what was read: CalcAbsolutePosition first, then the field.
static bool ReadAbsTransform(void* entity, float* origin, float* angles) {
    unsigned char** vt = *(unsigned char***)entity;
    if (!CodeIs(vt[9], "56 8B F1 E8 ?? ?? ?? ?? 8D 86 ?? ?? ?? ?? 5E C3") || !CodeIs(vt[10], "56 8B F1 E8 ?? ?? ?? ?? 68 00 02 00 00")) {
        if (!g_absLogged) {
            g_absLogged = true;
            HMODULE client = GetModuleHandleA("client.dll");
            Log("client view: entity %p (vtable client.dll+0x%X): slots 9/10 aren't GetAbsOrigin/GetAbsAngles as read; not read",
                entity, (unsigned)((unsigned char*)vt - (unsigned char*)client));
        }
        return false;
    }
    memcpy(origin, ((AbsGetterFn)vt[9])(entity), 12);
    memcpy(angles, ((AbsGetterFn)vt[10])(entity), 12);
    return true;
}

static void WriteClientView(wire::ClientView* v) {
    memset(v, 0, sizeof *v);
    v->player = v->viewmodel = wire::kNoEntity;
    v->viewmodel_sequence = -1;
    if (!g_clientTried) ClientOpen();
    __try {
        if (g_viewAngles) { memcpy(v->view_angles, g_viewAngles, 12); v->flags |= 1; }
        if (g_playerSlot && g_getClientEntity) {
            int local = *g_playerSlot + 1;
            void* player = local > 0 && local < 64 ? g_getClientEntity(g_entityList, local) : nullptr;
            if (player && ReadAbsTransform(player, v->origin, v->angles)) { v->player = (uint16_t)local; v->flags |= 2; }
        }
        // The view model's index is cached, and found again when it stops naming one: loading a save
        // recreates the entity, often at another index.
        if (g_viewmodelIndex != wire::kNoEntity) {
            const char* c = ClassNameAt(g_viewmodelIndex);
            if (!c || strcmp(c, "viewmodel") != 0 || (g_getClientEntity && !g_getClientEntity(g_entityList, g_viewmodelIndex)))
                g_viewmodelIndex = wire::kNoEntity;
        }
        // A level started fresh puts it among the first edicts; a loaded save anywhere.
        if (g_viewmodelIndex == wire::kNoEntity)
            for (int i = 1; i < kMaxEdicts; i++)
                if (const char* c = ClassNameAt((uint16_t)i)) if (!strcmp(c, "viewmodel")) { g_viewmodelIndex = (uint16_t)i; break; }
        if (g_viewmodelIndex != wire::kNoEntity && g_getClientEntity) {
            void* vm = g_getClientEntity(g_entityList, g_viewmodelIndex);
            if (vm && ReadAbsTransform(vm, v->viewmodel_origin, v->viewmodel_angles)) { v->viewmodel = g_viewmodelIndex; v->flags |= 4; }
            if (vm && g_vmSequenceOff >= 0 && g_vmCycleOff >= 0) {
                v->viewmodel_sequence = (int16_t)*(int*)((unsigned char*)vm + g_vmSequenceOff);
                v->viewmodel_cycle = *(float*)((unsigned char*)vm + g_vmCycleOff);
                v->flags |= 32;
            }
            if (vm && g_vmLayersOff >= 0) {
                // CUtlVector: memory pointer, allocation count, grow size, then the element count.
                const unsigned char* vec = (const unsigned char*)vm + g_vmLayersOff;
                const unsigned char* items = *(const unsigned char* const*)vec;
                int size = *(const int*)(vec + 12);
                if (items && size >= 0 && size <= g_vmLayerMax) {
                    for (int i = 0; i < size && v->layer_count < wire::kClientLayers; i++) {
                        const unsigned char* l = items + i * g_vmLayerStride;
                        float weight = *(const float*)(l + g_vmLayerWeightOff);
                        if (!(weight > 0)) continue;
                        wire::ClientLayer& out = v->layers[v->layer_count++];
                        out.sequence = (int16_t)*(const int*)(l + g_vmLayerSeqOff);
                        out.order = (int16_t)*(const int*)(l + g_vmLayerOrderOff);
                        out.cycle = *(const float*)(l + g_vmLayerCycleOff);
                        out.weight = weight;
                    }
                    v->flags |= 64;
                }
            }
        }
        if (g_viewRender && *g_viewRender) {
            // GetViewSetup (IViewRender slot 12): lea eax,[ecx+disp32]; ret. The CViewSetup it
            // returns is the camera the client last drew with (layout in docs/wire.md).
            unsigned char* view = (unsigned char*)*g_viewRender;
            unsigned char* getter = (*(unsigned char***)view)[12];
            if (CodeIs(getter, "8D 81 ?? ?? ?? ?? C3")) {
                const unsigned char* setup = view + *(int*)(getter + 2);
                int width = *(const int*)(setup + 0x0C), height = *(const int*)(setup + 0x10);
                if (width > 0 && height > 0 && width < 65536 && height < 65536) {
                    v->screen_width = (uint16_t)width;
                    v->screen_height = (uint16_t)height;
                    v->fov = *(const float*)(setup + 0x28);
                    v->fov_viewmodel = *(const float*)(setup + 0x2C);
                    memcpy(v->camera_origin, setup + 0x30, 12);
                    memcpy(v->camera_angles, setup + 0x48, 12);
                    v->z_near = *(const float*)(setup + 0x54);
                    v->z_far = *(const float*)(setup + 0x58);
                    v->flags |= 16;
                }
            }
        }
        const char* cls = ClassNameAt(1);
        if (g_viewOffsetOff >= 0 && cls && !strcmp(cls, "player")) {
            const unsigned char* p = *(const unsigned char**)(g_edicts + 1 * g_stride + g_unkOff);
            if (p) { memcpy(v->view_offset, p + g_viewOffsetOff, 12); v->flags |= 8; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
        v->flags = 0;
    }
}

// A console command from the control file, run through the client's command buffer (next frame).
static void RunCommandIfAsked() {
    volatile wire::Control* c = g_control;
    if (!c || c->command_seq == c->command_done) return;
    if (!g_clientTried) ClientOpen();
    char text[sizeof c->command];
    memcpy(text, (const void*)c->command, sizeof text);
    text[sizeof text - 1] = 0;
    if (g_clientCmd) {
        __try { g_clientCmd(g_engineClient, text); Log("control: command \"%s\"", text); }
        __except (EXCEPTION_EXECUTE_HANDLER) { ++g_faults; }
    }
    c->command_done = c->command_seq;
}

static void WireWrite() {
    wire::Header* h = WireHeader();
    uint64_t f = h->frames_written;
    unsigned char* slotBase = g_wire + wire::kSlotsOffset + (f % wire::kSlotCount) * wire::kSlotBytes;
    wire::SlotHeader* s = (wire::SlotHeader*)slotBase;
    wire::Entity* out = (wire::Entity*)(slotBase + sizeof(wire::SlotHeader));
    wire::Layer* layers = (wire::Layer*)(slotBase + wire::kLayersOffset);
    uint32_t layerCount = 0, layerDropped = 0;

    InterlockedExchange64((volatile LONG64*)&s->seq, (LONG64)(2 * f + 1));
    uint32_t count = 0;
    for (int i = 0; i < kMaxEdicts && count < wire::kMaxEntities; ++i) {
        unsigned char* e = g_edicts + i * g_stride;
        __try {
            if (*(int*)e & kEdictFree) continue;
            IServerNetworkable* n = NetworkableAt(e, g_netOff);
            IServerEntity* u = *(IServerEntity**)(e + g_unkOff);
            if (!n || !u) continue;
            ICollideable* c = u->GetCollideable();
            const float* o = c->GetCollisionOrigin();
            const float* a = (const float*)((const char*)u + kAbsAnglesOffset);
            const ClassSlot& cls = ClassFor(n);
            const unsigned char* ent = (const unsigned char*)u;
            wire::Entity& w = out[count];
            w.index = (uint16_t)i;
            w.serial = *(const uint32_t*)u->GetRefEHandle() >> 12;
            w.class_id = cls.id;
            w.model_index = u->GetModelIndex();
            NoteModel(w.model_index);
            memcpy(w.origin, o, sizeof w.origin);
            memcpy(w.angles, a, sizeof w.angles);
            w.flags = 0;
            w.health = 0;
            w.max_health = 0;
            w.life_state = 0;
            if (g_healthOff >= 0 && g_maxHealthOff >= 0 && g_lifeStateOff >= 0) {
                w.health = *(const float*)(ent + g_healthOff);
                w.max_health = *(const float*)(ent + g_maxHealthOff);
                w.life_state = *(ent + g_lifeStateOff);
                w.flags |= wire::kHasHealth;
            }
            w.sequence = -1;
            w.cycle = 0;
            w.playback_rate = 0;
            if (cls.props.sequence >= 0 && cls.props.cycle >= 0) {
                w.sequence = (int16_t)*(const int*)(ent + cls.props.sequence);
                w.cycle = *(const float*)(ent + cls.props.cycle);
                w.playback_rate = cls.props.rate >= 0 ? *(const float*)(ent + cls.props.rate) : 1.0f;
                w.flags |= wire::kHasAnimation;
            }
            WriteLink(cls.ptr, ent, w);
            WriteParent(cls.ptr, ent, w);
            ++count;
            // After the entity is counted: a fault here loses only its layers.
            if (cls.props.overlay) WriteLayers((unsigned char*)ent, (uint16_t)i, layers, layerCount, layerDropped);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++g_faults;
        }
    }
    // Damage applied since the previous frame, in the order it happened.
    memcpy(slotBase + wire::kDamageOffset, g_pendingDamage, g_pendingCount * sizeof(wire::Damage));
    s->damage_count = g_pendingCount;
    s->damage_dropped = g_pendingDropped;
    g_pendingCount = 0;
    g_pendingDropped = 0;
    memcpy(slotBase + wire::kDefenseOffset, g_pendingDefense, g_defenseCount * sizeof(wire::Defense));
    s->defense_count = g_defenseCount;
    s->defense_dropped = g_defenseDropped;
    s->layer_count = layerCount;
    s->layer_dropped = layerDropped;
    g_defenseCount = 0;
    g_defenseDropped = 0;
    memcpy(slotBase + wire::kSlowTimeOffset, g_pendingSlowTime, g_slowTimeCount * sizeof(wire::SlowTime));
    s->slow_time_count = g_slowTimeCount;
    s->slow_time_dropped = g_slowTimeDropped;
    g_slowTimeCount = 0;
    g_slowTimeDropped = 0;
    WritePlayerState(s);
    WriteClientView((wire::ClientView*)(slotBase + wire::kClientViewOffset));
    WritePlayerInput((wire::PlayerInput*)(slotBase + wire::kInputOffset));
    WritePlayerStats((wire::PlayerStats*)(slotBase + wire::kStatsOffset));
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    s->wall_seconds = (double)(now.QuadPart - g_qpcStart.QuadPart) / (double)g_qpcFreq.QuadPart;
    s->frame = f;
    s->tick = g_globals->tickcount;
    s->curtime = g_globals->curtime;
    s->level_serial = h->level_serial;
    s->entity_count = count;
    InterlockedExchange64((volatile LONG64*)&s->seq, (LONG64)(2 * f + 2));
    InterlockedExchange64((volatile LONG64*)&h->frames_written, (LONG64)(f + 1));
}

// --- Damage hook -----------------------------------------------------------------
// CBaseEntity::TakeDamage(const CTakeDamageInfo&) is the one entry point for
// damage to any entity. Found at server.dll+0x3B9820 through its two
// "CBaseEntity::TakeDamage:  with inputInfo..." warnings. It opens with
// sub esp,0xBC (6 bytes), which the hook replaces with a jump; the trampoline
// replays it and jumps back. The bytes are checked before patching, so a
// different server.dll is left alone.

static const uint32_t kTakeDamageRva = 0x3B9820;
static const unsigned char kTakeDamagePrologue[] = { 0x81, 0xEC, 0xBC, 0x00, 0x00, 0x00, 0x56, 0x8B, 0xB4, 0x24, 0xC4, 0x00, 0x00, 0x00 };
typedef void (__thiscall* TakeDamageFn)(void* self, const void* info);
static TakeDamageFn g_takeDamage;        // trampoline to the original
static unsigned char* g_takeDamageSite;

static int EdictIndexOf(void* entity) {
    __try {
        IServerNetworkable* n = ((IServerEntity*)entity)->GetNetworkable();
        unsigned char* e = n ? (unsigned char*)n->GetEdict() : nullptr;
        if (!e || !g_edicts || !g_stride || e < g_edicts) return -1;
        int i = (int)((e - g_edicts) / g_stride);
        return i < kMaxEdicts ? i : -1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

static float HealthOf(void* entity) {
    __try { return g_healthOff >= 0 ? *(float*)((char*)entity + g_healthOff) : 0.0f; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0.0f; }
}

// Arkane's CTakeDamageInfo, from its datadesc in server.dll (2026-10-17).
// Episode One's fields up to m_iAmmoType, then trace, push, slow-time and
// spell fields of its own.
namespace dmg {
const int Force = 0x00, Position = 0x0C, Inflictor = 0x24, Attacker = 0x28, Damage = 0x2C, Type = 0x38,
          CustomKill = 0x3C, TraceValid = 0x44, HitBox = 0x48, HitGroup = 0x4C, PhysicsBone = 0x50,
          TraceEnd = 0x54, TraceStart = 0x60, TraceEntity = 0x6C, PushForce = 0x78, SlowDelay = 0x7C,
          SlowDuration = 0x80, SlowFactor = 0x84, WeaponFlags = 0x88, MMFlag = 0x8C, MMSpell = 0x90,
          MMDirection = 0x94;
}

// An EHANDLE is serial << 12 | entry index; entries past the edict range are
// server-only entities, which the Wire doesn't carry.
static uint16_t HandleIndex(uint32_t h) {
    if (h == 0xFFFFFFFF) return wire::kNoEntity;
    uint32_t i = h & 0xFFF;
    return i < (uint32_t)kMaxEdicts ? (uint16_t)i : wire::kNoEntity;
}

static uint32_t HandleSerial(uint32_t h) { return HandleIndex(h) == wire::kNoEntity ? 0 : h >> 12; }

// An entity's own EHANDLE serial (IServerEntity::GetRefEHandle), the same
// serial the game's handles to it carry. The edict's second word holds only
// its low 10 bits (the networked width).
static uint32_t SerialOf(void* entity) {
    __try { return entity ? *(const uint32_t*)((IServerEntity*)entity)->GetRefEHandle() >> 12 : 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static uint32_t SerialAt(uint16_t index) {
    if (index == wire::kNoEntity || !g_edicts) return 0;
    unsigned char* e = g_edicts + index * g_stride;
    return (*(int*)e & kEdictFree) ? 0 : SerialOf(*(void**)(e + g_unkOff));
}

// Dark Messiah names the weapon, not its wielder, as the attacker of a melee
// hit. A weapon's m_hOwnerEntity is the character holding it. Only weapon_*
// entities count: NPCs have owners too (the npc_template_maker that spawned
// them), and those stay the attacker. Returns the wielder's EHANDLE.
static uint32_t WielderOf(uint16_t index) {
    if (index == wire::kNoEntity || g_ownerOff < 0 || !g_edicts) return 0xFFFFFFFF;
    unsigned char* e = g_edicts + index * g_stride;
    if (*(int*)e & kEdictFree) return 0xFFFFFFFF;
    IServerNetworkable* n = NetworkableAt(e, g_netOff);
    unsigned char* entity = *(unsigned char**)(e + g_unkOff);
    if (!n || !entity || strncmp(n->GetClassName(), "weapon_", 7) != 0) return 0xFFFFFFFF;
    // NPC weapons carry their wielder in m_hOwnerEntity; the player's in m_hOwner.
    uint32_t owner = *(uint32_t*)(entity + g_ownerOff);
    if (HandleIndex(owner) == wire::kNoEntity && g_weaponOwnerOff >= 0) owner = *(uint32_t*)(entity + g_weaponOwnerOff);
    return owner;
}

// Read by the melee routine (server.dll+0x407D70) for the not-alerted multiplier:
// abs([npc+0x36EC]) <= mul_damages_Npc_Not_alerted_level and [npc+0x1A5C] != 3.
static const int kAlertLevelOffset = 0x36EC, kNpcStateOffset = 0x1A5C;
// The attacker's critical byte: the melee routine's DmgCrit is DmgBase x (1 + (byte != 0)).
static const int kCriticalOffset = 0x21F7;

static void ClearPendingDamage() { g_pendingCount = 0; g_pendingDropped = 0; g_defenseCount = 0; g_defenseDropped = 0; g_slowTimeCount = 0; g_slowTimeDropped = 0; }

// v13: the victim's animation as a damage call arrives, before TakeDamage can start
// a reaction: the state the trace that struck it posed (docs/wire.md, "The victim's pose").
struct VictimAnim {
    int16_t sequence = -1;
    uint8_t valid = 0, layers = 0;
    float cycle = 0, rate = 0, animTime = 0;
    float pose[wire::kPoseParameters] = {};
    int16_t layerSequence[wire::kDamageLayers] = {}, layerOrder[wire::kDamageLayers] = {};
    float layerCycle[wire::kDamageLayers] = {}, layerWeight[wire::kDamageLayers] = {};
};

static VictimAnim CaptureAnim(void* victim) {
    VictimAnim a;
    __try {
        IServerNetworkable* n = ((IServerEntity*)victim)->GetNetworkable();
        if (!n) return a;
        const ClassSlot& cls = ClassFor(n);
        const unsigned char* e = (const unsigned char*)victim;
        if (cls.props.sequence < 0 || cls.props.cycle < 0) return a;
        a.sequence = (int16_t)*(const int*)(e + cls.props.sequence);
        a.cycle = *(const float*)(e + cls.props.cycle);
        a.rate = cls.props.rate >= 0 ? *(const float*)(e + cls.props.rate) : 0;
        a.animTime = cls.props.animTime >= 0 ? *(const float*)(e + cls.props.animTime) : 0;
        if (cls.props.poses >= 0) memcpy(a.pose, e + cls.props.poses, sizeof a.pose);
        if (cls.props.overlay && g_layerStride) {
            unsigned char* mem = *(unsigned char* const*)(e + g_layerVector);
            int size = *(const int*)(e + g_layerVector + 12);
            for (int k = 0; mem && k < size && k < kMaxOverlays && a.layers < wire::kDamageLayers; ++k) {
                unsigned char* l = mem + k * g_layerStride;
                float weight = *(float*)(l + 0x18);
                if (!(*(int*)l & 1) || !(weight > 0.0f)) continue;
                a.layerSequence[a.layers] = (int16_t)*(int*)(l + 0x8);
                a.layerOrder[a.layers] = (int16_t)*(int*)(l + 0x34);
                a.layerCycle[a.layers] = *(float*)(l + 0xC);
                a.layerWeight[a.layers] = weight;
                ++a.layers;
            }
        }
        a.valid = 1;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        a = VictimAnim();
        ++g_faults;
    }
    return a;
}

static void OnDamage(void* victim, const void* info, float before, float after, const VictimAnim& anim) {
    ++g_damageTotal;
    if (!g_streamOk) return;
    if (g_pendingCount == wire::kMaxDamage) { ++g_pendingDropped; return; }
    __try {
        const unsigned char* in = (const unsigned char*)info;
        auto I = [in](int off) { return *(const int32_t*)(in + off); };
        auto F = [in](int off) { return *(const float*)(in + off); };
        wire::Damage& d = g_pendingDamage[g_pendingCount];
        int v = EdictIndexOf(victim);
        d.tick = g_globals->tickcount;
        d.victim = v < 0 ? wire::kNoEntity : (uint16_t)v;
        uint32_t attacker = (uint32_t)I(dmg::Attacker), weapon = 0xFFFFFFFF;
        uint32_t owner = WielderOf(HandleIndex(attacker));
        if (HandleIndex(owner) != wire::kNoEntity) { weapon = attacker; attacker = owner; }
        uint32_t inflictor = (uint32_t)I(dmg::Inflictor), traced = (uint32_t)I(dmg::TraceEntity);
        d.attacker = HandleIndex(attacker);
        d.weapon = HandleIndex(weapon);
        d.inflictor = HandleIndex(inflictor);
        d.trace_entity = HandleIndex(traced);
        d.victim_serial = SerialOf(victim);
        d.attacker_serial = HandleSerial(attacker);
        d.weapon_serial = HandleSerial(weapon);
        d.inflictor_serial = HandleSerial(inflictor);
        d.trace_entity_serial = HandleSerial(traced);
        d.victim_alert = 0;
        d.victim_npc_state = 0;
        d.victim_alert_valid = 0;
        if (d.victim != wire::kNoEntity) {
            const char* cls = ClassNameAt(d.victim);
            if (cls && !strncmp(cls, "npc_", 4)) {
                int alert = *(const int*)((const unsigned char*)victim + kAlertLevelOffset);
                d.victim_alert = (int16_t)(alert < 0 ? -alert : alert);
                d.victim_npc_state = *((const unsigned char*)victim + kNpcStateOffset);
                d.victim_alert_valid = 1;
            }
        }
        d.damage = F(dmg::Damage);
        d.health_before = before;
        d.health_after = after;
        d.damage_type = (uint32_t)I(dmg::Type);
        d.custom_kill = (int16_t)I(dmg::CustomKill);
        d.trace_valid = in[dmg::TraceValid] ? 1 : 0;
        d.hitbox = (int16_t)I(dmg::HitBox);
        d.hitgroup = (int16_t)I(dmg::HitGroup);
        d.physics_bone = (int16_t)I(dmg::PhysicsBone);
        d.attacker_critical = 0;
        if (d.attacker != wire::kNoEntity) {
            const char* cls = ClassNameAt(d.attacker);
            unsigned char* e = g_edicts + d.attacker * g_stride;
            const unsigned char* who = cls && (!strcmp(cls, "player") || !strncmp(cls, "npc_", 4)) ? *(const unsigned char**)(e + g_unkOff) : nullptr;
            if (who) d.attacker_critical = who[kCriticalOffset] ? 1 : 0;
        }
        memcpy(d.position, in + dmg::Position, 12);
        memcpy(d.force, in + dmg::Force, 12);
        memcpy(d.trace_start, in + dmg::TraceStart, 12);
        memcpy(d.trace_end, in + dmg::TraceEnd, 12);
        d.push_force = I(dmg::PushForce);
        d.weapon_flags = I(dmg::WeaponFlags);
        d.mm_damage_flag = I(dmg::MMFlag);
        d.mm_spell = I(dmg::MMSpell);
        d.slow_time_delay = F(dmg::SlowDelay);
        d.slow_time_duration = F(dmg::SlowDuration);
        d.slow_time_factor = F(dmg::SlowFactor);
        memcpy(d.mm_direction, in + dmg::MMDirection, 12);
        d.victim_sequence = anim.sequence;
        d.victim_anim_valid = anim.valid;
        d.victim_layer_count = anim.layers;
        d.victim_cycle = anim.cycle;
        d.victim_playback_rate = anim.rate;
        d.victim_anim_time = anim.animTime;
        memcpy(d.victim_pose, anim.pose, sizeof d.victim_pose);
        memcpy(d.victim_layer_sequence, anim.layerSequence, sizeof d.victim_layer_sequence);
        memcpy(d.victim_layer_order, anim.layerOrder, sizeof d.victim_layer_order);
        memcpy(d.victim_layer_cycle, anim.layerCycle, sizeof d.victim_layer_cycle);
        memcpy(d.victim_layer_weight, anim.layerWeight, sizeof d.victim_layer_weight);
        ++g_pendingCount;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
}

static void __fastcall TakeDamageHook(void* self, void* /*edx*/, const void* info) {
    float before = HealthOf(self);
    VictimAnim anim = CaptureAnim(self);
    g_takeDamage(self, info);
    OnDamage(self, info, before, HealthOf(self), anim);
}

static void InstallDamageHook() {
    unsigned char* base = (unsigned char*)GetModuleHandleA("server.dll");
    if (!base) { Log("damage hook: server.dll not loaded"); return; }
    unsigned char* site = base + kTakeDamageRva;
    if (memcmp(site, kTakeDamagePrologue, sizeof kTakeDamagePrologue) != 0) {
        Log("damage hook: unexpected bytes at server.dll+0x%X, not hooking", kTakeDamageRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) { Log("damage hook: VirtualAlloc failed (%lu)", GetLastError()); return; }
    memcpy(tramp, site, 6);
    tramp[6] = 0xE9;
    *(int32_t*)(tramp + 7) = (int32_t)((site + 6) - (tramp + 11));
    g_takeDamage = (TakeDamageFn)tramp;

    DWORD old;
    VirtualProtect(site, 6, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&TakeDamageHook - (site + 5));
    site[5] = 0x90;
    VirtualProtect(site, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 6);
    g_takeDamageSite = site;
    Log("damage hook: CBaseEntity::TakeDamage at %p hooked", site);
}

// --- User commands (Wire v14) -------------------------------------------------------
// CBasePlayer::ProcessUsercmds(CUserCmd* cmds, int numcmds, int totalcmds, int dropped, bool paused),
// server.dll+0x430350 (CBasePlayer's and CHL2_Player's vtable slot 0x864, called from the engine's
// command reader at +0x14880A). It copies the commands into the player's command context, to run
// later in the frame; cmds[0] is the newest of the numcmds new ones. Arkane's CUserCmd is 0xBC bytes:
// the SDK's fields up to +0x48, then its own. The reader (+0x45E950) gives their offsets; a kick is
// byte +0x97 (docs/wire.md, "The player's input"). The first 10 bytes are checked before patching.
static const uint32_t kProcessUsercmdsRva = 0x430350;
static const unsigned char kProcessUsercmdsPrologue[] = { 0x51, 0x53, 0x8B, 0xD9, 0x8B, 0x83, 0xBC, 0x23, 0x00, 0x00 };
typedef void (__thiscall* ProcessUsercmdsFn)(void* player, unsigned char* cmds, int numcmds, int totalcmds, int dropped, bool paused);
static ProcessUsercmdsFn g_processUsercmds;
static unsigned char* g_processUsercmdsSite;
static const int kUsercmdBytes = 0xBC, kActionFirst = 0x95, kActionCount = 13;

static int EdictIndexOf(void* entity);

static void __fastcall ProcessUsercmdsHook(void* player, void* /*edx*/, unsigned char* cmds, int numcmds, int totalcmds, int dropped, bool paused) {
    __try {
        if (cmds && numcmds > 0 && numcmds <= totalcmds && EdictIndexOf(player) == 1) {
            const unsigned char* newest = cmds;
            g_commands.forward = *(const float*)(newest + 0x18);
            g_commands.side = *(const float*)(newest + 0x1C);
            g_commands.up = *(const float*)(newest + 0x24);
            g_commands.impulse = newest[0x2C];
            g_commands.number = *(const int32_t*)(newest + 0x4);
            for (int i = 0; i < numcmds; ++i) {
                const unsigned char* c = cmds + i * kUsercmdBytes;
                g_commands.buttons |= *(const uint32_t*)(c + 0x28);
                for (int a = 0; a < kActionCount; ++a)
                    if (c[kActionFirst + a]) g_commands.actions |= 1u << a;
            }
            int total = g_commands.commands + numcmds;
            g_commands.commands = (uint8_t)(total > 255 ? 255 : total);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
    g_processUsercmds(player, cmds, numcmds, totalcmds, dropped, paused);
}

static void InstallUsercmdHook() {
    unsigned char* base = (unsigned char*)GetModuleHandleA("server.dll");
    if (!base) return;
    unsigned char* site = base + kProcessUsercmdsRva;
    if (memcmp(site, kProcessUsercmdsPrologue, sizeof kProcessUsercmdsPrologue) != 0) {
        Log("usercmd hook: unexpected bytes at server.dll+0x%X, not hooking", kProcessUsercmdsRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) { Log("usercmd hook: VirtualAlloc failed (%lu)", GetLastError()); return; }
    memcpy(tramp, site, sizeof kProcessUsercmdsPrologue);
    tramp[10] = 0xE9;
    *(int32_t*)(tramp + 11) = (int32_t)((site + 10) - (tramp + 15));
    g_processUsercmds = (ProcessUsercmdsFn)tramp;
    DWORD old;
    VirtualProtect(site, 10, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&ProcessUsercmdsHook - (site + 5));
    memset(site + 5, 0x90, 5);
    VirtualProtect(site, 10, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 10);
    g_processUsercmdsSite = site;
    Log("usercmd hook: CBasePlayer::ProcessUsercmds at %p hooked", site);
}

static void RemoveUsercmdHook() {
    if (!g_processUsercmdsSite) return;
    DWORD old;
    VirtualProtect(g_processUsercmdsSite, 10, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_processUsercmdsSite, kProcessUsercmdsPrologue, 10);
    VirtualProtect(g_processUsercmdsSite, 10, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_processUsercmdsSite, 10);
    g_processUsercmdsSite = nullptr;
}

// --- Defense hook ----------------------------------------------------------------
// When a melee blow lands, the game asks the victim for a verdict through a
// virtual (slot 0x624/4), called from several places: Arkane's melee damage
// routine (server.dll+0x407D70, which prints "Hit blocked or parried, dmg =
// 0") and the NPC attack code among them. CNPC_BaseArx's implementation at
// server.dll+0x2E0030 answers 2 (hit), 1 (the blow struck the NPC's weapon,
// or it is in its parry/block state), 8 (the blow struck its off-hand item,
// or its shield state is up and the attack comes from the front) or 4 (the
// blow does nothing). Other classes, the player included, inherit a version
// that answers 0. The hook detours CNPC_BaseArx's version: its first two
// instructions (6 bytes) move to a trampoline.
//
// Arguments, from the implementation: (const Vector* dir, Vector* out,
// CBaseEntity* struckEntity, Vector* outDir, CBaseEntity* attacker,
// CBaseEntity* weapon), callee-cleaned.

static const uint32_t kDefenseRva = 0x2E0030;
static const unsigned char kDefensePrologue[] = { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x10, 0x83, 0xEC, 0x18, 0x55, 0x57 };
typedef int (__thiscall* DefenseFn)(void* self, void* dir, void* out1, void* struck, void* out2, void* attacker, void* weapon);
static DefenseFn g_defense;              // trampoline
static unsigned char* g_defenseSite;

static uint16_t IndexOrNone(void* entity) {
    int i = entity ? EdictIndexOf(entity) : -1;
    return i < 0 ? wire::kNoEntity : (uint16_t)i;
}

// The NPC's defense state, as the verdict reads it: CNPC_BaseArx's getter at
// server.dll+0x2BBCA0 returns a word cached per tick (+0x23D4, stamped with
// the tick at +0x23D8), recomputed through vtable+0xC98 when stale. The
// verdict tests its bits 0x4 (parry/block) and 0x10 (shield up). Read before
// the verdict, so it is the state the verdict sees.
static const uint32_t kDefenseStateRva = 0x2BBCA0;
static const int kNoEffectOffset = 0x2961;
typedef uint32_t (__thiscall* DefenseStateFn)(void* self);
static DefenseStateFn g_defenseState;

static void FindDefenseState(unsigned char* base) {
    unsigned char* f = base + kDefenseStateRva;
    if (CodeIs(f, "A1 ?? ?? ?? ?? 8B 40 18 56 8B F1 39 86 D8 23 00 00 75 ?? 8B 8E D4 23 00 00"))
        g_defenseState = (DefenseStateFn)f;
    else
        Log("defense hook: the defense-state getter at server.dll+0x%X isn't as read; no defense states", kDefenseStateRva);
}

static int __fastcall DefenseHook(void* victim, void* /*edx*/, void* dir, void* out1, void* struck, void* out2, void* attacker, void* weapon) {
    uint32_t state = 0;
    uint8_t stateValid = 0, noEffect = 0;
    if (g_streamOk && g_defenseState) {
        __try {
            state = g_defenseState(victim);
            stateValid = 1;
            noEffect = *((unsigned char*)victim + kNoEffectOffset);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++g_faults;
        }
    }
    int result = g_defense(victim, dir, out1, struck, out2, attacker, weapon);
    ++g_defenseTotal;
    if (!g_streamOk) return result;
    if (g_defenseCount == wire::kMaxDefense) { ++g_defenseDropped; return result; }
    __try {
        wire::Defense& d = g_pendingDefense[g_defenseCount];
        d.tick = g_globals->tickcount;
        d.victim = IndexOrNone(victim);
        d.attacker = IndexOrNone(attacker);
        d.weapon = IndexOrNone(weapon);
        d.struck = IndexOrNone(struck);
        d.victim_serial = d.victim == wire::kNoEntity ? 0 : SerialOf(victim);
        d.attacker_serial = d.attacker == wire::kNoEntity ? 0 : SerialOf(attacker);
        d.weapon_serial = d.weapon == wire::kNoEntity ? 0 : SerialOf(weapon);
        d.struck_serial = d.struck == wire::kNoEntity ? 0 : SerialOf(struck);
        d.result = result;
        if (dir) memcpy(d.direction, dir, sizeof d.direction);
        else memset(d.direction, 0, sizeof d.direction);
        d.source = 0;
        d.victim_state = state;
        d.state_valid = stateValid;
        d.no_effect = noEffect;
        d.reserved = 0;
        ++g_defenseCount;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
    return result;
}

static void InstallDefenseHook() {
    unsigned char* base = (unsigned char*)GetModuleHandleA("server.dll");
    if (!base) return;
    unsigned char* site = base + kDefenseRva;
    if (memcmp(site, kDefensePrologue, sizeof kDefensePrologue) != 0) {
        Log("defense hook: unexpected bytes at server.dll+0x%X, not hooking", kDefenseRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) return;
    memcpy(tramp, site, 6);
    tramp[6] = 0xE9;
    *(int32_t*)(tramp + 7) = (int32_t)((site + 6) - (tramp + 11));
    g_defense = (DefenseFn)tramp;
    DWORD old;
    VirtualProtect(site, 6, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&DefenseHook - (site + 5));
    site[5] = 0x90;
    VirtualProtect(site, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 6);
    g_defenseSite = site;
    FindDefenseState(base);
    Log("defense hook: CNPC_BaseArx defense check at %p hooked, defense states %s", site, g_defenseState ? "on" : "off");
}

static void RemoveDefenseHook() {
    if (!g_defenseSite) return;
    DWORD old;
    VirtualProtect(g_defenseSite, 6, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_defenseSite, kDefensePrologue, 6);
    VirtualProtect(g_defenseSite, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_defenseSite, 6);
    g_defenseSite = nullptr;
    Log("defense hook: removed");
}

// --- Player parry hook --------------------------------------------------------------
// The player parries by holding the parry stance (action +0x20F8 is 0x94 or
// 0x96; CBasePlayer slot 0x5CC tests it). The outcome of a blow against
// that stance goes into the player's parry state at +0x1C78 through
// CBasePlayer slot 369 (server.dll+0x3FEC50): 0 PARRY_STATE_NOPARRY, 1
// PARRY_STATE_PARRY (parried), 2 PARRY_STATE_BREAKPARRY (a weapon with flag
// 0x10 broke the parry). The setter ignores the change while the state is 2.
// Its main caller is the attack resolver server.dll+0x3B1A90..0x3B39A9, where
// edi holds the attacking NPC. A naked stub takes the return address and edi,
// records the outcome, and jumps to a trampoline holding the setter's first
// instruction (cmp [ecx+0x1C78],1; 7 bytes).

static const uint32_t kParrySetRva = 0x3FEC50;
static const unsigned char kParrySetBytes[] = { 0x83, 0xB9, 0x78, 0x1C, 0x00, 0x00, 0x01, 0x7F, 0x0A, 0x8B, 0x44, 0x24, 0x04 };
static const uint32_t kAttackResolverStart = 0x3B1A90, kAttackResolverEnd = 0x3B39AA;
static const int kParryStateOffset = 0x1C78;
static void* g_parrySetTrampoline;
static unsigned char* g_parrySetSite;
static unsigned char* g_serverBase;

static void __stdcall OnParryState(void* player, int state, void* edi, unsigned char* ret) {
    if (!g_streamOk || (state != 1 && state != 2)) return;
    __try {
        if (*(int*)((char*)player + kParryStateOffset) > 1) return;  // the setter will ignore it
        if (g_defenseCount == wire::kMaxDefense) { ++g_defenseDropped; return; }
        uint32_t source = (uint32_t)(ret - g_serverBase);
        uint16_t attacker = wire::kNoEntity;
        if (source >= kAttackResolverStart && source < kAttackResolverEnd && LooksCallable(edi, 4)) {
            int a = EdictIndexOf(edi);
            IServerNetworkable* n = a < 0 ? nullptr : NetworkableAt(g_edicts + a * g_stride, g_netOff);
            if (n && !strncmp(n->GetClassName(), "npc_", 4)) attacker = (uint16_t)a;
        }
        wire::Defense& d = g_pendingDefense[g_defenseCount];
        d.tick = g_globals->tickcount;
        d.victim = IndexOrNone(player);
        d.attacker = attacker;
        d.weapon = wire::kNoEntity;
        d.struck = wire::kNoEntity;
        d.victim_serial = SerialOf(player);
        d.attacker_serial = SerialAt(attacker);
        d.weapon_serial = 0;
        d.struck_serial = 0;
        d.result = state == 1 ? wire::kPlayerParried : wire::kPlayerParryBroken;
        memset(d.direction, 0, sizeof d.direction);
        d.source = source;
        d.victim_state = 0;
        d.state_valid = 0;
        d.no_effect = 0;
        d.reserved = 0;
        ++g_defenseCount;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
}

// thiscall(player, int state), ret 4. Calls OnParryState(player, state, edi,
// return address), then continues in the original setter.
__declspec(naked) static void ParryStateHook() {
    __asm {
        push ecx                    // [esp] player, [esp+4] return address, [esp+8] state
        mov eax, [esp+4]
        push eax                    // return address
        push edi                    // the caller's edi
        mov eax, [esp+16]
        push eax                    // state
        push ecx                    // player
        call OnParryState
        pop ecx
        jmp g_parrySetTrampoline
    }
}

static void InstallParryHook() {
    g_serverBase = (unsigned char*)GetModuleHandleA("server.dll");
    if (!g_serverBase) return;
    unsigned char* site = g_serverBase + kParrySetRva;
    if (memcmp(site, kParrySetBytes, sizeof kParrySetBytes) != 0) {
        Log("parry hook: unexpected bytes at server.dll+0x%X, not hooking", kParrySetRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) return;
    memcpy(tramp, site, 7);
    tramp[7] = 0xE9;
    *(int32_t*)(tramp + 8) = (int32_t)((site + 7) - (tramp + 12));
    g_parrySetTrampoline = tramp;
    DWORD old;
    VirtualProtect(site, 7, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&ParryStateHook - (site + 5));
    site[5] = 0x90;
    site[6] = 0x90;
    VirtualProtect(site, 7, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 7);
    g_parrySetSite = site;
    Log("parry hook: CBasePlayer parry state setter at %p hooked", site);
}

static void RemoveParryHook() {
    if (!g_parrySetSite) return;
    DWORD old;
    VirtualProtect(g_parrySetSite, 7, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_parrySetSite, kParrySetBytes, 7);
    VirtualProtect(g_parrySetSite, 7, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_parrySetSite, 7);
    g_parrySetSite = nullptr;
    Log("parry hook: removed");
}

static void RemoveDamageHook() {
    if (!g_takeDamageSite) return;
    DWORD old;
    VirtualProtect(g_takeDamageSite, 6, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_takeDamageSite, kTakeDamagePrologue, 6);
    VirtualProtect(g_takeDamageSite, 6, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_takeDamageSite, 6);
    g_takeDamageSite = nullptr;
    Log("damage hook: removed");
}

// --- Bullet-time hook ------------------------------------------------------------
// The player's slow motion is armed through a CMM_Player virtual at
// server.dll+0x146E80: thiscall (float start, float duration, float factor,
// bool sound), ret 0x10. It stores them at +0x20A4/+0x20A8/+0x20AC/+0x20B1;
// the game frame then sends mm_host_timescale <factor> once curtime passes
// start (server.dll+0x147610). The hook records each call with the RVA it
// returns to, which names the code path that asked for slow motion. Its
// first two instructions (mov eax,[esp+4]; mov edx,[esp+8]; 8 bytes) move to
// a trampoline.

static const uint32_t kSetBulletTimeRva = 0x146E80;
static const unsigned char kSetBulletTimeBytes[] = { 0x8B, 0x44, 0x24, 0x04, 0x8B, 0x54, 0x24, 0x08, 0x89, 0x81, 0xA4, 0x20, 0x00, 0x00 };
typedef void (__thiscall* SetBulletTimeFn)(void* self, float start, float duration, float factor, int sound);
static SetBulletTimeFn g_setBulletTime;   // trampoline

// A return address as a server.dll RVA, or wire::kCallerOutside when it lies
// outside the module: Arkane's copy-protected code (the .sll section, such as
// mm_player_inputs' ActiveSlowMotion input) runs from a copy in allocated memory.
static uint32_t CallerRva(const unsigned char* ret) {
    if (!g_serverBase) return wire::kCallerOutside;
    const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)g_serverBase;
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(g_serverBase + dos->e_lfanew);
    uintptr_t rva = (uintptr_t)(ret - g_serverBase);
    return ret >= g_serverBase && rva < nt->OptionalHeader.SizeOfImage ? (uint32_t)rva : wire::kCallerOutside;
}
static unsigned char* g_setBulletTimeSite;

static void __fastcall SetBulletTimeHook(void* self, void* /*edx*/, float start, float duration, float factor, int sound) {
    unsigned char* ret = (unsigned char*)_ReturnAddress();
    if (g_streamOk) {
        __try {
            ++g_slowTimeTotal;
            if (g_slowTimeCount == wire::kMaxSlowTime) {
                ++g_slowTimeDropped;
            } else {
                wire::SlowTime& t = g_pendingSlowTime[g_slowTimeCount++];
                t.tick = g_globals->tickcount;
                t.player = IndexOrNone(self);
                t.player_serial = SerialOf(self);
                t.sound = (uint8_t)(sound & 0xFF);
                t.source = wire::kSlowTimeSetter;
                t.start = start;
                t.duration = duration;
                t.factor = factor;
                t.caller = CallerRva(ret);
                t.reserved2 = 0;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++g_faults;
        }
    }
    g_setBulletTime(self, start, duration, factor, sound);
}

static void InstallBulletTimeHook() {
    if (!g_serverBase) g_serverBase = (unsigned char*)GetModuleHandleA("server.dll");
    if (!g_serverBase) return;
    unsigned char* site = g_serverBase + kSetBulletTimeRva;
    if (memcmp(site, kSetBulletTimeBytes, sizeof kSetBulletTimeBytes) != 0) {
        Log("bullet-time hook: unexpected bytes at server.dll+0x%X, not hooking", kSetBulletTimeRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) return;
    memcpy(tramp, site, 8);
    tramp[8] = 0xE9;
    *(int32_t*)(tramp + 9) = (int32_t)((site + 8) - (tramp + 13));
    g_setBulletTime = (SetBulletTimeFn)tramp;
    DWORD old;
    VirtualProtect(site, 8, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&SetBulletTimeHook - (site + 5));
    site[5] = site[6] = site[7] = 0x90;
    VirtualProtect(site, 8, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 8);
    g_setBulletTimeSite = site;
    Log("bullet-time hook: CMM_Player bullet-time setter at %p hooked", site);
}

static void RemoveBulletTimeHook() {
    if (!g_setBulletTimeSite) return;
    DWORD old;
    VirtualProtect(g_setBulletTimeSite, 8, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_setBulletTimeSite, kSetBulletTimeBytes, 8);
    VirtualProtect(g_setBulletTimeSite, 8, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_setBulletTimeSite, 8);
    g_setBulletTimeSite = nullptr;
    Log("bullet-time hook: removed");
}

// The static bullet-time request at server.dll+0x147560, (float delay, float
// duration, float factor, bool sound), __stdcall, ret 0x10. It returns at
// once unless mm_ai_bullet_time is set, finds the local player, and writes
// its fields directly (start = curtime + delay, duration * factor, factor,
// sound) without the setter. Found from the writers of +0x20A4: one is the
// setter, this is the other. Its first two instructions are
// mov eax,[0x22816ea8] (the cvar; the address is relocated) and
// cmp dword [eax+0x2c],0, 9 bytes, which move to a trampoline. The check
// requires the relocated address to point at the same cvar.

static const uint32_t kRequestSlowTimeRva = 0x147560, kBulletTimeCvarRva = 0x816EA8;
static const unsigned char kRequestSlowTimeTail[] = { 0x83, 0x78, 0x2C, 0x00, 0x0F, 0x84 };
typedef void (__stdcall* RequestSlowTimeFn)(float delay, float duration, float factor, int sound);
static RequestSlowTimeFn g_requestSlowTime;   // trampoline
static unsigned char* g_requestSlowTimeSite;
static unsigned char g_requestSlowTimeSaved[9];

static void __stdcall RequestSlowTimeHook(float delay, float duration, float factor, int sound) {
    unsigned char* ret = (unsigned char*)_ReturnAddress();
    if (g_streamOk) {
        __try {
            ++g_slowTimeTotal;
            if (g_slowTimeCount == wire::kMaxSlowTime) {
                ++g_slowTimeDropped;
            } else {
                wire::SlowTime& t = g_pendingSlowTime[g_slowTimeCount++];
                const char* cls = ClassNameAt(1);
                void* player = cls && strcmp(cls, "player") == 0 ? *(void**)(g_edicts + 1 * g_stride + g_unkOff) : nullptr;
                t.tick = g_globals->tickcount;
                t.player = player ? 1 : wire::kNoEntity;
                t.player_serial = player ? SerialOf(player) : 0;
                t.sound = (uint8_t)(sound & 0xFF);
                t.source = wire::kSlowTimeRequest;
                t.start = delay;
                t.duration = duration;
                t.factor = factor;
                t.caller = CallerRva(ret);
                t.reserved2 = 0;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            ++g_faults;
        }
    }
    g_requestSlowTime(delay, duration, factor, sound);
}

static void InstallRequestSlowTimeHook() {
    if (!g_serverBase) return;
    unsigned char* site = g_serverBase + kRequestSlowTimeRva;
    if (site[0] != 0xA1 || *(uint32_t*)(site + 1) != (uint32_t)(g_serverBase + kBulletTimeCvarRva)
        || memcmp(site + 5, kRequestSlowTimeTail, sizeof kRequestSlowTimeTail) != 0) {
        Log("slow-time request hook: unexpected bytes at server.dll+0x%X, not hooking", kRequestSlowTimeRva);
        return;
    }
    unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) return;
    memcpy(g_requestSlowTimeSaved, site, 9);
    memcpy(tramp, site, 9);
    tramp[9] = 0xE9;
    *(int32_t*)(tramp + 10) = (int32_t)((site + 9) - (tramp + 14));
    g_requestSlowTime = (RequestSlowTimeFn)tramp;
    DWORD old;
    VirtualProtect(site, 9, PAGE_EXECUTE_READWRITE, &old);
    site[0] = 0xE9;
    *(int32_t*)(site + 1) = (int32_t)((unsigned char*)&RequestSlowTimeHook - (site + 5));
    site[5] = site[6] = site[7] = site[8] = 0x90;
    VirtualProtect(site, 9, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 9);
    g_requestSlowTimeSite = site;
    Log("slow-time request hook: static bullet-time request at %p hooked", site);
}

static void RemoveRequestSlowTimeHook() {
    if (!g_requestSlowTimeSite) return;
    DWORD old;
    VirtualProtect(g_requestSlowTimeSite, 9, PAGE_EXECUTE_READWRITE, &old);
    memcpy(g_requestSlowTimeSite, g_requestSlowTimeSaved, 9);
    VirtualProtect(g_requestSlowTimeSite, 9, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_requestSlowTimeSite, 9);
    g_requestSlowTimeSite = nullptr;
    Log("slow-time request hook: removed");
}

// --- The plugin ----------------------------------------------------------------

class RustyBridgePlugin {
public:
    virtual bool Load(CreateInterfaceFn interfaceFactory, CreateInterfaceFn gameServerFactory) {
        Log("Load: interfaceFactory=%p gameServerFactory=%p", interfaceFactory, gameServerFactory);
        const char* engineIfaces[] = { "VEngineServer021", "VModelInfoServer001", "VEngineServerStringTable001" };
        for (const char* name : engineIfaces) {
            void* p = interfaceFactory ? interfaceFactory(name, nullptr) : nullptr;
            Log("  engine %s -> %p", name, p);
            if (!strcmp(name, "VModelInfoServer001")) g_modelInfo = (IVModelInfo*)p;
        }
        const char* gameIfaces[] = { "ServerGameDLL004", "ServerGameEnts001", "ServerGameClients003", "PlayerInfoManager002" };
        for (const char* name : gameIfaces) {
            void* p = gameServerFactory ? gameServerFactory(name, nullptr) : nullptr;
            Log("  game %s -> %p", name, p);
            if (!strcmp(name, "PlayerInfoManager002") && LooksCallable(p, 1))
                g_globals = (GlobalVars*)((IPlayerInfoManager*)p)->GetGlobalVars();
        }
        Log("  gpGlobals -> %p", g_globals);
        QueryPerformanceFrequency(&g_qpcFreq);
        QueryPerformanceCounter(&g_qpcStart);
        WireOpen();
        ControlOpen();
        InstallDamageHook();
        InstallUsercmdHook();
        InstallDefenseHook();
        InstallParryHook();
        InstallBulletTimeHook();
        InstallRequestSlowTimeHook();
        return true;
    }
    virtual void Unload() { Log("Unload"); RemoveRequestSlowTimeHook(); RemoveBulletTimeHook(); RemoveParryHook(); RemoveDefenseHook(); RemoveDamageHook(); RemoveUsercmdHook(); ControlClose(); WireClose(); }
    virtual void Pause() { Log("Pause"); }
    virtual void UnPause() { Log("UnPause"); }
    virtual const char* GetPluginDescription() { return "Rusty Bridge tap"; }
    virtual void LevelInit(const char* mapName) {
        Log("LevelInit: %s", SafeStr(mapName));
        g_edicts = nullptr;
        g_stride = 0;
        g_viewmodelIndex = wire::kNoEntity;
        WireLevelInit(SafeStr(mapName));
    }
    virtual void ServerActivate(edict_t* edictList, int edictCount, int clientMax) {
        Log("ServerActivate: edictList=%p edictCount=%d clientMax=%d", edictList, edictCount, clientMax);
        g_edicts = (unsigned char*)edictList;
        DetectLayout(g_edicts, edictCount);
        bool layoutOk = DumpEntities("ServerActivate");
        bool globalsOk = false;
        __try {
            if (g_globals) {
                Log("gpGlobals: realtime=%.3f framecount=%d curtime=%.3f frametime=%.4f maxClients=%d tickcount=%d interval=%.5f",
                    g_globals->realtime, g_globals->framecount, g_globals->curtime, g_globals->frametime,
                    g_globals->maxClients, g_globals->tickcount, g_globals->interval_per_tick);
                globalsOk = g_globals->maxClients == clientMax &&
                            g_globals->interval_per_tick > 0.001f && g_globals->interval_per_tick < 0.2f;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            Log("gpGlobals: exception reading");
        }
        g_streamOk = g_wire && layoutOk && globalsOk;
        if (g_wire) WireHeader()->tick_interval = globalsOk ? g_globals->interval_per_tick : 0;
        Log("wire: streaming %s (layout %s, globals %s)", g_streamOk ? "on" : "OFF",
            layoutOk ? "ok" : "bad", globalsOk ? "ok" : "bad");
        m_frames = 0;
        m_delayedDumpDone = false;
    }
    virtual void GameFrame(bool simulating) {
        ++m_frames;
        RunCommandIfAsked();
        if (g_streamOk && g_globals->tickcount != g_lastTick) {
            g_lastTick = g_globals->tickcount;
            LARGE_INTEGER t0, t1;
            QueryPerformanceCounter(&t0);
            WireWrite();
            QueryPerformanceCounter(&t1);
            m_writeTicks += t1.QuadPart - t0.QuadPart;
            ++m_writes;
            if (m_writes % 660 == 0) {
                LARGE_INTEGER freq;
                QueryPerformanceFrequency(&freq);
                wire::Header* h = WireHeader();
                const wire::SlotHeader* last = (const wire::SlotHeader*)(g_wire + wire::kSlotsOffset +
                    ((h->frames_written - 1) % wire::kSlotCount) * wire::kSlotBytes);
                Log("wire: %llu frames, tick %d, %u entities, %u layers, %u classes, %u damage calls, %u defense checks, %u bullet-time calls, %u layer rejects, adrenaline %.1f/%.1f, avg write %.1f us, %u faults",
                    h->frames_written, last->tick, last->entity_count, last->layer_count, g_classCount, g_damageTotal, g_defenseTotal, g_slowTimeTotal, g_layerRejects,
                    last->adrenaline, last->max_adrenaline,
                    1e6 * m_writeTicks / freq.QuadPart / 660, g_faults);
                m_writeTicks = 0;
            }
            HoldIfAsked();
        }
        if (!m_delayedDumpDone && simulating && m_frames > 330) {
            m_delayedDumpDone = true;
            DumpEntities("level+5s");
            HideIfAsked();
        }
        if (m_frames % 33 == 0) {
            char trigger[MAX_PATH];
            snprintf(trigger, sizeof trigger, "%s\\rustybridge_dump", g_dir);
            if (GetFileAttributesA(trigger) != INVALID_FILE_ATTRIBUTES) {
                DeleteFileA(trigger);
                DumpEntities("trigger file");
            }
        }
    }
    virtual void LevelShutdown() { Log("LevelShutdown"); g_edicts = nullptr; g_streamOk = false; }
    virtual void ClientActive(edict_t* e) { Log("ClientActive: %p", e); }
    virtual void ClientDisconnect(edict_t* e) { Log("ClientDisconnect: %p", e); }
    virtual void ClientPutInServer(edict_t* e, const char* name) { Log("ClientPutInServer: %p %s", e, SafeStr(name)); }
    virtual void SetCommandClient(int) {}
    virtual void ClientSettingsChanged(edict_t*) {}
    virtual PLUGIN_RESULT ClientConnect(bool*, edict_t* e, const char* name, const char*, char*, int) {
        Log("ClientConnect: %p %s", e, SafeStr(name));
        return PLUGIN_CONTINUE;
    }
    virtual PLUGIN_RESULT ClientCommand(edict_t*) { return PLUGIN_CONTINUE; }
    virtual PLUGIN_RESULT NetworkIDValidated(const char*, const char*) { return PLUGIN_CONTINUE; }

private:
    int m_frames = 0;
    bool m_delayedDumpDone = false;
    uint64_t m_writes = 0;
    long long m_writeTicks = 0;
};

static RustyBridgePlugin g_plugin;

extern "C" __declspec(dllexport) void* CreateInterface(const char* name, int* returnCode) {
    Log("CreateInterface(%s)", SafeStr(name));
    if (name && !strcmp(name, "ISERVERPLUGINCALLBACKS001")) {
        if (returnCode) *returnCode = 0;
        return &g_plugin;
    }
    if (returnCode) *returnCode = 1;
    return nullptr;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        GetModuleFileNameA(module, g_dir, MAX_PATH);
        char* slash = strrchr(g_dir, '\\');
        if (slash) *slash = 0;
        char path[MAX_PATH];
        snprintf(path, sizeof path, "%s\\rustybridge.log", g_dir);
        g_log = _fsopen(path, "a", _SH_DENYNO);
        Log("---- rustybridge.dll attached (%s)", g_dir);
    } else if (reason == DLL_PROCESS_DETACH && g_log) {
        Log("---- rustybridge.dll detached");
        fclose(g_log);
        g_log = nullptr;
    }
    return TRUE;
}
