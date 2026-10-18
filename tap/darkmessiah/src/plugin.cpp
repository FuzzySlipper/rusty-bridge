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
static bool g_streamOk;
static int g_lastTick = -1;
static uint32_t g_faults;  // SEH faults in the tick walk and the damage hook
// Damage the hook captured since the last frame was written.
static wire::Damage g_pendingDamage[wire::kMaxDamage];
static uint32_t g_pendingCount, g_pendingDropped, g_damageTotal;
// Defense checks captured since the last frame.
static wire::Defense g_pendingDefense[wire::kMaxDefense];
static uint32_t g_defenseCount, g_defenseDropped, g_defenseTotal;

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

struct NetProps { int sequence = -1, cycle = -1, rate = -1; };
static int g_sendLayout;              // 0 unchecked, 1 ok, -1 wrong
// Arkane made CBaseEntity's health a float pair, m_fHealth/m_fMaxHealth, next
// to m_lifeState. Only some classes network them (shields do), but as
// CBaseEntity members their offsets hold for every entity.
static int g_healthOff = -1, g_maxHealthOff = -1, g_lifeStateOff = -1;
static int g_ownerOff = -1;           // CBaseEntity::m_hOwnerEntity
static int g_weaponOwnerOff = -1;     // CBaseCombatWeapon::m_hOwner

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

static void WalkSendTable(SendTable* t, int base, NetProps& p, int depth) {
    if (!t || depth > 16) return;
    for (int i = 0; i < t->count; ++i) {
        unsigned char* sp = t->props + i * kPropStride;
        if (*(int*)(sp + kPropFlags) & kSpropExclude) continue;
        int off = base + *(int*)(sp + kPropOffset);
        if (*(int*)(sp + kPropType) == kDptDataTable) {
            WalkSendTable(*(SendTable**)(sp + kPropTable), off, p, depth + 1);
            continue;
        }
        const char* name = *(const char**)(sp + kPropName);
        if (!strcmp(name, "m_nSequence") && p.sequence < 0) p.sequence = off;
        else if (!strcmp(name, "m_flCycle") && p.cycle < 0) p.cycle = off;
        else if (!strcmp(name, "m_flPlaybackRate") && p.rate < 0) p.rate = off;
        else if (!strcmp(name, "m_fHealth") && g_healthOff < 0) { g_healthOff = off; Log("netprops: m_fHealth @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_fMaxHealth") && g_maxHealthOff < 0) { g_maxHealthOff = off; Log("netprops: m_fMaxHealth @+0x%X (CBaseEntity, all classes)", off); }
        else if (!strcmp(name, "m_hOwner") && g_weaponOwnerOff < 0) { g_weaponOwnerOff = off; Log("netprops: m_hOwner @+0x%X (CBaseCombatWeapon)", off); }
        else if (!strcmp(name, "m_hOwnerEntity") && g_ownerOff < 0) { g_ownerOff = off; Log("netprops: m_hOwnerEntity @+0x%X (CBaseEntity, all classes)", off); }
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
            Log("netprops: %s (%s/%s) sequence@+0x%X cycle@+0x%X rate@+0x%X", SafeStr(className), SafeStr(sc->networkName),
                SafeStr(sc->table->name), p.sequence, p.cycle, p.rate);
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

// The per-tick walk trusts the layout DumpEntities confirmed at
// ServerActivate, so it skips the VirtualQuery checks; one SEH frame per
// entity still turns a bad pointer into a counted fault.
static void WireWrite() {
    wire::Header* h = WireHeader();
    uint64_t f = h->frames_written;
    unsigned char* slotBase = g_wire + wire::kSlotsOffset + (f % wire::kSlotCount) * wire::kSlotBytes;
    wire::SlotHeader* s = (wire::SlotHeader*)slotBase;
    wire::Entity* out = (wire::Entity*)(slotBase + sizeof(wire::SlotHeader));

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
            ++count;
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
    g_defenseCount = 0;
    g_defenseDropped = 0;
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

// Dark Messiah names the weapon, not its wielder, as the attacker of a melee
// hit. A weapon's m_hOwnerEntity is the character holding it. Only weapon_*
// entities count: NPCs have owners too (the npc_template_maker that spawned
// them), and those stay the attacker.
static uint16_t WielderOf(uint16_t index) {
    if (index == wire::kNoEntity || g_ownerOff < 0 || !g_edicts) return wire::kNoEntity;
    unsigned char* e = g_edicts + index * g_stride;
    if (*(int*)e & kEdictFree) return wire::kNoEntity;
    IServerNetworkable* n = NetworkableAt(e, g_netOff);
    unsigned char* entity = *(unsigned char**)(e + g_unkOff);
    if (!n || !entity || strncmp(n->GetClassName(), "weapon_", 7) != 0) return wire::kNoEntity;
    // NPC weapons carry their wielder in m_hOwnerEntity; the player's in m_hOwner.
    uint16_t owner = HandleIndex(*(uint32_t*)(entity + g_ownerOff));
    if (owner == wire::kNoEntity && g_weaponOwnerOff >= 0) owner = HandleIndex(*(uint32_t*)(entity + g_weaponOwnerOff));
    return owner;
}

static void ClearPendingDamage() { g_pendingCount = 0; g_pendingDropped = 0; g_defenseCount = 0; g_defenseDropped = 0; }

static void OnDamage(void* victim, const void* info, float before, float after) {
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
        d.attacker = HandleIndex((uint32_t)I(dmg::Attacker));
        d.weapon = wire::kNoEntity;
        uint16_t owner = WielderOf(d.attacker);
        if (owner != wire::kNoEntity) { d.weapon = d.attacker; d.attacker = owner; }
        d.inflictor = HandleIndex((uint32_t)I(dmg::Inflictor));
        d.trace_entity = HandleIndex((uint32_t)I(dmg::TraceEntity));
        d.damage = F(dmg::Damage);
        d.health_before = before;
        d.health_after = after;
        d.damage_type = (uint32_t)I(dmg::Type);
        d.custom_kill = (int16_t)I(dmg::CustomKill);
        d.trace_valid = in[dmg::TraceValid] ? 1 : 0;
        d.hitbox = (int16_t)I(dmg::HitBox);
        d.hitgroup = (int16_t)I(dmg::HitGroup);
        d.physics_bone = (int16_t)I(dmg::PhysicsBone);
        d.reserved = 0;
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
        ++g_pendingCount;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        ++g_faults;
    }
}

static void __fastcall TakeDamageHook(void* self, void* /*edx*/, const void* info) {
    float before = HealthOf(self);
    g_takeDamage(self, info);
    OnDamage(self, info, before, HealthOf(self));
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

static int __fastcall DefenseHook(void* victim, void* /*edx*/, void* dir, void* out1, void* struck, void* out2, void* attacker, void* weapon) {
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
        d.result = result;
        if (dir) memcpy(d.direction, dir, sizeof d.direction);
        else memset(d.direction, 0, sizeof d.direction);
        d.source = 0;
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
    Log("defense hook: CNPC_BaseArx defense check at %p hooked", site);
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
        d.result = state == 1 ? wire::kPlayerParried : wire::kPlayerParryBroken;
        memset(d.direction, 0, sizeof d.direction);
        d.source = source;
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
        WireOpen();
        InstallDamageHook();
        InstallDefenseHook();
        InstallParryHook();
        return true;
    }
    virtual void Unload() { Log("Unload"); RemoveParryHook(); RemoveDefenseHook(); RemoveDamageHook(); WireClose(); }
    virtual void Pause() { Log("Pause"); }
    virtual void UnPause() { Log("UnPause"); }
    virtual const char* GetPluginDescription() { return "Rusty Bridge tap"; }
    virtual void LevelInit(const char* mapName) {
        Log("LevelInit: %s", SafeStr(mapName));
        g_edicts = nullptr;
        g_stride = 0;
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
                Log("wire: %llu frames, tick %d, %u entities, %u classes, %u damage calls, %u defense checks, avg write %.1f us, %u faults",
                    h->frames_written, last->tick, last->entity_count, g_classCount, g_damageTotal, g_defenseTotal,
                    1e6 * m_writeTicks / freq.QuadPart / 660, g_faults);
                m_writeTicks = 0;
            }
        }
        if (!m_delayedDumpDone && simulating && m_frames > 330) {
            m_delayedDumpDone = true;
            DumpEntities("level+5s");
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
