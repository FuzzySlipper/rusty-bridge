// Parser checks for timeline.html, without a browser: node --test viewer/tests/*.test.mjs
// The page's script runs in a bare context with an inert DOM, and its parser
// reads recordings built here byte by byte (docs/recording.md).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const page = readFileSync(new URL('../timeline.html', import.meta.url), 'utf8');
const start = page.indexOf('"use strict";');
const script = page.slice(start, page.indexOf('</script>', start));
// The page's top level looks up its canvases and controls; give it an inert stand-in for each.
const inert = new Proxy(function () {}, {
  get: (_, key) => (key === Symbol.toPrimitive ? () => 0 : key === Symbol.iterator ? [][Symbol.iterator] : key === 'textContent' ? '' : inert),
  apply: () => inert,
  construct: () => inert,
});
const browser = ['document', 'window', 'location', 'localStorage', 'getComputedStyle', 'matchMedia', 'MutationObserver',
  'ResizeObserver', 'requestAnimationFrame', 'addEventListener', 'fetch', 'devicePixelRatio'];
const context = vm.createContext({
  TextDecoder, DataView, Uint8Array, ArrayBuffer, BigInt, Map, Set, Math, Number, Infinity, NaN, Error, URL, JSON, Array, Object, String, Symbol,
  atob, btoa, console, setTimeout, clearTimeout,
  ...Object.fromEntries(browser.map((name) => [name, inert])),
});
vm.runInContext(`${script}\nglobalThis.parseRecording = parseRecording;`, context);

/** A v6 (or v7) recording: level l01_a, classes, then frames of whole entity records plus damage. */
function recording(frames, version = 6) {
  const bytes = [];
  const u8 = (n) => bytes.push(n & 0xff);
  const u16 = (n) => { u8(n); u8(n >> 8); };
  const u32 = (n) => { for (let i = 0; i < 4; i++) u8(n >>> (8 * i)); };
  const f32 = (x) => { const b = new DataView(new ArrayBuffer(4)); b.setFloat32(0, x, true); for (let i = 0; i < 4; i++) u8(b.getUint8(i)); };
  const f64 = (x) => { const b = new DataView(new ArrayBuffer(8)); b.setFloat64(0, x, true); for (let i = 0; i < 8; i++) u8(b.getUint8(i)); };
  const str = (s) => { u16(s.length); for (const c of s) u8(c.charCodeAt(0)); };
  const record = (kind, body) => { u8(kind); const at = bytes.length; u32(0); const start = bytes.length; body(); const len = bytes.length - start; for (let i = 0; i < 4; i++) bytes[at + i] = (len >>> (8 * i)) & 0xff; };
  u32(0x45524252); u32(0x31303043); u32(version); u32(0);
  record(1, () => { u32(1); f32(0.015); str('l01_a'); });
  ['npc_orc_sword', 'prop_ragdoll', 'player'].forEach((name, id) => record(2, () => { u16(id); str(name); }));
  frames.forEach(({ tick, entities, damage = [], wall = 0, slow = null, slowCalls = [], client = null, input = null, stats = null, sweeps = [] }, i) => record(4, () => {
    u32(i); u32(0); u32(tick); f32(tick * 0.015); u8(1);           // frame, tick, curtime, key flag
    u32(entities.length);
    for (const e of entities) {                                      // 56-byte WireEntity (64 from v7)
      u16(e.index); u16(e.cls); u32(0);
      for (let k = 0; k < 6; k++) f32(0);
      const alerted = version >= 18 && e.alert !== undefined;          // v18: alert and npc_state, flag 4
      f32(30); f32(30); u16(0xffff); u8(alerted ? 5 : 1); u8(0); f32(0); f32(0); u32(e.serial);
      if (version >= 7) { u16(e.link?.[0] ?? 0xffff); u16(alerted ? e.alert : 0); u32(e.link?.[1] ?? 0); }
      if (version >= 10) { u16(e.parent?.[0] ?? 0xffff); u8(e.parent?.[2] ?? 0); u8(alerted ? e.npcState ?? 0 : 0); u32(e.parent?.[1] ?? 0); u16(e.parent?.[0] ?? 0xffff); u16(0); u32(e.parent?.[1] ?? 0); }
    }
    u32(0);                                                          // removed
    u32(0); u32(damage.length);
    for (const d of damage) {                                        // 152-byte WireDamage (360 from v13)
      const rec = new Uint8Array(version >= 13 ? 360 : 152), v = new DataView(rec.buffer);
      v.setInt32(0, tick, true);
      v.setUint16(4, d.victim[0], true); v.setUint16(6, d.attacker[0], true); v.setUint16(8, 0xffff, true); v.setUint16(10, 0xffff, true);
      v.setFloat32(12, 30, true); v.setFloat32(16, 30, true); v.setFloat32(20, 0, true); v.setUint16(30, 0xffff, true);
      v.setUint32(128, d.victim[1], true); v.setUint32(132, d.attacker[1], true);
      if (version >= 13 && d.anim) {                                 // the victim's animation as the blow arrived
        v.setInt16(152, d.anim.sequence, true); v.setUint8(154, 1); v.setUint8(155, 1); v.setFloat32(156, d.anim.cycle, true);
        v.setFloat32(168, d.anim.pose0, true); v.setInt16(264, d.anim.layer, true); v.setFloat32(328, 0.5, true);
      }
      bytes.push(...rec);
    }
    u32(0); u32(0);                                                  // defense
    u32(0); u32(0);                                                  // layers
    if (version >= 7) {
      f64(wall);                                                     // wall clock
      u16(1); u8(slow ? 1 : 0); u8(0); f32(slow?.start ?? 0); f32(slow?.duration ?? 0); f32(slow?.factor ?? 0); f32(42); f32(100);  // player state
      u32(0); u32(slowCalls.length);
      for (const b of slowCalls) { u32(tick); u16(1); u8(1); u8(b.request ? 1 : 0); f32(b.start); f32(b.duration); f32(b.factor); u32(b.caller); u32(1); u32(0); }
    }
    if (version >= 8) {                                              // client view, 80 bytes (132 from v9)
      u16(1); u16(7); u32(client ? (version >= 9 ? 63 : 15) : 0);
      for (const x of client ? [...client.view, ...client.origin, 0, 90, 0, 0, 0, 64, 1, 2, 3, 0, 0, 0] : new Array(18).fill(0)) f32(x);
      if (version >= 9) {
        for (const x of client ? [...(client.camera ?? [0, 0, 0]), 0, 91, 0, 101.4, 101.4] : new Array(8).fill(0)) f32(x);
        u16(1280); u16(720); f32(1); f32(28000); u16(client?.sequence ?? 0xffff); u16(0); f32(client?.cycle ?? 0);
        u8(client?.layer ? 1 : 0); u8(0); u8(0); u8(0);
        for (let i = 0; i < 8; i++) { u16(i === 0 && client?.layer ? client.layer.seq : 0); u16(0); f32(i === 0 && client?.layer ? client.layer.cycle : 0); f32(i === 0 && client?.layer ? 1 : 0); }
      }
    }
    if (version >= 12) { u16(input ? 1 : 0xffff); u8(input ? 1 : 0); u8(input?.commands ?? 0); u32(input?.buttons ?? 0); u32(input?.pressed ?? 0); u32(0); }  // the player's input
    if (version >= 14) { f32(input?.forward ?? 0); f32(input?.side ?? 0); f32(0); u32(input?.buttons ?? 0); u32(input?.actions ?? 0); u32(0); u32(0); u32(0); }  // its commands
    if (version >= 15) {                                             // the player's stats, 80 bytes
      u16(stats ? 1 : 0xffff); u8(stats ? 1 : 0); u8(0);
      f32(stats?.stamina ?? 0); f32(stats?.maxStamina ?? 0); f32(stats?.mana ?? 0); f32(stats?.maxMana ?? 0);
      u32(stats?.modules?.[0] ?? 0); u32(stats?.modules?.[1] ?? 0); u32(stats?.skillPoints ?? 0); u32(0);
      u16(stats?.weapon ?? 0xffff); u16(0xffff); u16(0xffff); u16(0xffff); u16(0xffff); u16(0);
      for (let i = 0; i < 8; i++) u32(0);
      if (version >= 16) for (let i = 0; i < 32; i++) u8(stats?.skills?.[i] ?? 0);  // v16: skill levels
    }
    if (version >= 17) {                                             // v17: the melee sweeps
      u32(0); u32(sweeps.length);
      for (const s of sweeps) { u32(tick); u16(1); u8(s.type); u8(0); for (const x of [...s.start, ...s.direction, s.range, 0, 90, 0]) f32(x); u32(s.caller); u32(1); }
    }
  }));
  return new Uint8Array(bytes).buffer;
}

const player = { index: 1, cls: 2, serial: 1 };
const orc = { index: 7, cls: 0, serial: 0x123 };
const ragdoll = { index: 7, cls: 1, serial: 0x9ab };
const killingBlow = { attacker: [1, 1], victim: [7, 0x123] };

test('a blow names the entity its serial names, after its index was reused', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc] },
    { tick: 11, entities: [player, ragdoll], damage: [killingBlow] },
  ]));
  const [d] = rec.damage;
  assert.equal(d.victim.cls, 'npc_orc_sword');
  assert.equal(d.victim.serial, 0x123);
  assert.equal(d.attacker.cls, 'player');
});

test('a blow naming an identity never captured resolves to nothing, not to the index\'s new holder', () => {
  const rec = context.parseRecording(recording([
    { tick: 11, entities: [player, ragdoll], damage: [killingBlow] },
  ]));
  const [d] = rec.damage;
  assert.equal(d.victim, null);
  assert.equal(d.attacker.cls, 'player');
});

test('v7: slow motion, adrenaline, the wall clock and a knocked-down orc\'s ragdoll', () => {
  const knocked = { ...orc, link: [8, 0x77] }, itsRagdoll = { index: 8, cls: 1, serial: 0x77, link: [7, 0x123] };
  const call = { start: 0.165, duration: 2, factor: 0.25, caller: 0x2e1720, request: true };
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0 },
    { tick: 11, entities: [player, knocked, itsRagdoll], wall: 1.015, slowCalls: [call] },
    { tick: 12, entities: [player, knocked, itsRagdoll], wall: 1.075, slow: call },
  ], 7));
  assert.equal(rec.version, 7);
  assert.equal(rec.frames[2].wall, 1.075);
  assert.equal(rec.frames[2].player.inSlow, true);
  assert.equal(rec.frames[2].player.adrenaline, 42);
  const slow = rec.events.find((e) => e.kind === 'slow');
  assert.equal(slow.track.cls, 'player');
  assert.equal(slow.slow.caller, 0x2e1720);
  assert.equal(slow.slow.request, true);
  const knock = rec.events.find((e) => e.kind === 'knock');
  assert.equal(knock.track.cls, 'npc_orc_sword');
  assert.equal(knock.other.cls, 'prop_ragdoll');
});

test('v8: the client\'s view rides each frame', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, client: { view: [12, 90, 0], origin: [5, 6, 7] } },
    { tick: 11, entities: [player, orc], wall: 1.015 },
  ], 8));
  assert.equal(rec.version, 8);
  assert.deepEqual(Array.from(rec.frames[0].client.view), [12, 90, 0]);
  assert.equal(rec.frames[0].client.viewOffset[2], 64);
  assert.equal(rec.frames[1].client, undefined);
});

test('v9: the client\'s camera and viewmodel animation', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, client: { view: [12, 90, 0], origin: [5, 6, 7], camera: [5, 6, 80], sequence: 4, cycle: 0.25, layer: { seq: 30, cycle: 0.5 } } },
    { tick: 11, entities: [player, orc], wall: 1.015 },
  ], 9));
  const c = rec.frames[0].client;
  assert.deepEqual(Array.from(c.camera), [5, 6, 80]);
  assert.equal(c.cameraAngles[1], 91);
  assert.equal(Math.round(c.fov * 10), 1014);
  assert.deepEqual(Array.from(c.screen), [1280, 720]);
  assert.equal(c.viewmodelSequence, 4);
  assert.equal(c.viewmodelCycle, 0.25);
  assert.equal(c.viewmodelLayers.length, 1);
  assert.equal(c.viewmodelLayers[0].seq, 30);
  assert.equal(c.viewmodelLayers[0].cycle, 0.5);
  assert.equal(rec.frames[1].client, undefined);
});

test('v10: an entity names what it hangs from, at which attachment, and its owner', () => {
  const cleaver = { index: 9, cls: 0, serial: 0x66, parent: [7, 0x123, 2] };
  const rec = context.parseRecording(recording([{ tick: 10, entities: [player, orc, cleaver], wall: 1.0 }], 10));
  const t = rec.tracks.find((x) => x.index === 9);
  assert.equal(t.parent[0], '7:291');
  assert.equal(t.attachment[0], 2);
  assert.equal(t.owner[0], '7:291');
});

test('v12: the player\'s input, held and pressed, by button name', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, input: { buttons: 1 | 8, pressed: 1 } },
    { tick: 11, entities: [player, orc], wall: 1.015 },
  ], 12));
  assert.deepEqual({ ...rec.frames[0].input }, { buttons: 9, pressed: 1, released: 0 });
  assert.equal(rec.frames[1].input, undefined);
  assert.equal(vm.runInContext('inputText({ buttons: 9 | (1 << 11), pressed: 1 << 11, released: 0 })', context), 'attack forward attack2 (pressed attack2)');
  assert.equal(vm.runInContext('inputText({ buttons: 1 << 27, pressed: 0, released: 0 })', context), '0x8000000');
});

test('v14: the player\'s commands, the kick among Arkane\'s actions', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, input: { buttons: 8, pressed: 0, commands: 2, forward: 450, side: 0, actions: 4 } },
  ], 14));
  const i = rec.frames[0].input;
  assert.deepEqual([i.commands, i.forward, i.actions], [2, 450, 4]);
  assert.equal(vm.runInContext(`inputText(${JSON.stringify(i)})`, context), 'forward kick · move 450/0');
});

test('v15: the player\'s stats, modules by the game\'s bits', () => {
  const stats = { stamina: 6.5, maxStamina: 9, mana: 40, maxMana: 100, modules: [1 << 21, 1], skillPoints: 2, weapon: 7 };
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, input: { buttons: 8, pressed: 0, commands: 1, forward: 1000, side: 0, actions: 1 }, stats },
  ], 15));
  const s = rec.frames[0].stats;
  assert.deepEqual([s.stamina, s.maxStamina, s.skillPoints, s.weapon, s.shield], [6.5, 9, 2, 7, null]);
  assert.equal(vm.runInContext(`statsText(${JSON.stringify(s)})`, context), 'stamina 6.50/9 · mana 40.0/100 · 2 skill points · modules combat_chained_slash inferno');
  assert.equal(vm.runInContext(`inputText(${JSON.stringify(rec.frames[0].input)})`, context), 'forward sprint · move 1000/0');
});

test('v16: the skill levels after the stats', () => {
  const skills = [1, 0, 0, 3];
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, input: { buttons: 0, pressed: 0 }, stats: { stamina: 9, maxStamina: 9, mana: 20, maxMana: 20, modules: [0, 0], skills } },
  ], 16));
  const s = rec.frames[0].stats;
  assert.deepEqual(s.skills.slice(0, 4), [1, 0, 0, 3]);
  assert.match(vm.runInContext(`statsText(${JSON.stringify(s)})`, context), /skills combat 1 extra_damage 3$/);
});

test('v17: the melee sweeps after the stats', () => {
  const sweep = { type: 0, start: [1, 2, 64], direction: [0, 1, 0], range: 120, caller: 0x411c55 };
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, orc], wall: 1.0, input: { buttons: 0, pressed: 0 }, stats: { stamina: 9, maxStamina: 9, mana: 20, maxMana: 20, modules: [0, 0] }, sweeps: [sweep] },
    { tick: 11, entities: [player, orc], wall: 1.1, input: { buttons: 0, pressed: 0 } },
  ], 17));
  const s = rec.frames[0].sweeps[0];
  assert.deepEqual([s.attacker, s.type, s.range, s.caller], [1, 0, 120, 0x411c55]);
  assert.equal(JSON.stringify([s.start, s.direction, s.viewAngles]), '[[1,2,64],[0,1,0],[0,90,0]]');
  assert.equal(rec.frames[1].sweeps.length, 0);
});

test('v18: an NPC\'s alert level and state every tick', () => {
  const rec = context.parseRecording(recording([
    { tick: 10, entities: [player, { ...orc, alert: 0, npcState: 1 }], wall: 1.0, input: { buttons: 0, pressed: 0 } },
    { tick: 11, entities: [player, { ...orc, alert: 2, npcState: 3 }], wall: 1.1, input: { buttons: 0, pressed: 0 } },
  ], 18));
  const t = rec.tracks.find((t) => t.cls === 'npc_orc_sword');
  assert.deepEqual([...t.alert], [0, 2]);
  assert.deepEqual([...t.npcState], [1, 3]);
  assert.equal(rec.tracks.find((t) => t.cls === 'player').alert[0], null);
  const old = context.parseRecording(recording([{ tick: 10, entities: [player, { ...orc, alert: 2 }], wall: 1.0, input: { buttons: 0, pressed: 0 } }], 17));
  assert.equal(old.tracks.find((t) => t.cls === 'npc_orc_sword').alert[0], null);
});

test('v13: a blow carries the victim\'s animation as it arrived', () => {
  const blow = { ...killingBlow, anim: { sequence: 12, cycle: 0.4, pose0: 0.25, layer: 31 } };
  const rec = context.parseRecording(recording([{ tick: 10, entities: [player, orc], wall: 1.0, damage: [blow] }], 13));
  const a = rec.damage[0].victimAnim;
  assert.equal(a.sequence, 12);
  assert.ok(Math.abs(a.cycle - 0.4) < 1e-6 && Math.abs(a.pose[0] - 0.25) < 1e-6);
  assert.deepEqual(a.layers.map((l) => [l.sequence, l.weight]), [[31, 0.5]]);
  const old = context.parseRecording(recording([{ tick: 10, entities: [player, orc], wall: 1.0, damage: [killingBlow] }], 12));
  assert.equal(old.damage[0].victimAnim, null);
});

// A real slow motion when local/recordings has it (derived from the game, never committed): Arkane's
// mm_player_inputs ActiveSlowMotion input fired twice, ×0.2 for 3 s after 0.8 s and ×0.5 for 2 s
// (docs/recording.md, "Recording slow motion").
test('a recorded slow motion: the shaded frames and the game-time scale the stepper shows', (t) => {
  let bytes;
  try { bytes = readFileSync(new URL('../../local/recordings/l01_a-v11-slowmo-input.rbrec', import.meta.url)); } catch { t.skip('no local recording'); return; }
  const rec = context.parseRecording(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
  // Runs of frames with m_bInBulletTime set, as the stepper shades them.
  const runs = [];
  rec.frames.forEach((f, fi) => {
    if (!f.player?.inSlow) return;
    const last = runs.at(-1);
    if (last && last.end === fi - 1) last.end = fi; else runs.push({ start: fi, end: fi });
  });
  assert.equal(runs.length, 2);
  const scales = runs.map(({ start, end }) => {
    const a = rec.frames[start], b = rec.frames[end + 1];
    return { ticks: b.tick - a.tick, game: b.curtime - a.curtime, scale: (b.curtime - a.curtime) / (b.wall - a.wall) };
  });
  assert.deepEqual(scales.map((s) => s.ticks), [40, 66]);
  assert.ok(Math.abs(scales[0].game - 0.6) < 0.02 && Math.abs(scales[1].game - 0.99) < 0.02, JSON.stringify(scales));
  assert.ok(Math.abs(scales[0].scale - 0.2) < 0.01 && Math.abs(scales[1].scale - 0.5) < 0.01, JSON.stringify(scales));
  // The clock line's text in the middle of each run, from the page's own function.
  vm.runInContext('rec = globalThis.__rec;', Object.assign(context, { __rec: rec }));
  const text = runs.map(({ start, end }) => vm.runInContext(`timeScaleText(${Math.floor((start + end) / 2) + 10})`, context));
  assert.deepEqual(text, [' · game time ×0.20', ' · game time ×0.50']);
  // Each request names its caller; ActiveSlowMotion runs from Arkane's copy-protected code, outside server.dll.
  const calls = Array.from(rec.events.filter((e) => e.kind === 'slow'), (e) => e.text);
  assert.deepEqual(calls, ['slow motion ×0.20 for 3.00 s after 0.80 s (outside server.dll)', 'slow motion ×0.50 for 2.00 s after 0.00 s (outside server.dll)']);
});
