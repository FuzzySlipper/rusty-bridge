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
  frames.forEach(({ tick, entities, damage = [], wall = 0, slow = null, slowCalls = [], client = null, input = null }, i) => record(4, () => {
    u32(i); u32(0); u32(tick); f32(tick * 0.015); u8(1);           // frame, tick, curtime, key flag
    u32(entities.length);
    for (const e of entities) {                                      // 56-byte WireEntity (64 from v7)
      u16(e.index); u16(e.cls); u32(0);
      for (let k = 0; k < 6; k++) f32(0);
      f32(30); f32(30); u16(0xffff); u8(1); u8(0); f32(0); f32(0); u32(e.serial);
      if (version >= 7) { u16(e.link?.[0] ?? 0xffff); u16(0); u32(e.link?.[1] ?? 0); }
      if (version >= 10) { u16(e.parent?.[0] ?? 0xffff); u8(e.parent?.[2] ?? 0); u8(0); u32(e.parent?.[1] ?? 0); u16(e.parent?.[0] ?? 0xffff); u16(0); u32(e.parent?.[1] ?? 0); }
    }
    u32(0);                                                          // removed
    u32(0); u32(damage.length);
    for (const d of damage) {                                        // 152-byte WireDamage
      const rec = new Uint8Array(152), v = new DataView(rec.buffer);
      v.setInt32(0, tick, true);
      v.setUint16(4, d.victim[0], true); v.setUint16(6, d.attacker[0], true); v.setUint16(8, 0xffff, true); v.setUint16(10, 0xffff, true);
      v.setFloat32(12, 30, true); v.setFloat32(16, 30, true); v.setFloat32(20, 0, true); v.setUint16(30, 0xffff, true);
      v.setUint32(128, d.victim[1], true); v.setUint32(132, d.attacker[1], true);
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
    if (version >= 12) { u16(input ? 1 : 0xffff); u8(input ? 1 : 0); u8(0); u32(input?.buttons ?? 0); u32(input?.pressed ?? 0); u32(0); }  // the player's input
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
