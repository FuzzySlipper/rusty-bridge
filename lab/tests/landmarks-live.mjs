// The held items' grips as the Engine's landmark pairs (#10395, R10395-2), checked live: the lab under
// `rusty dev --headless` (a headless Chromium keeps the world drawing, so the renderer measures the pairs on the poses it
// drew; the smoke's test host has no renderer). It reads `engine.inspect.landmarks held.landmarks` with the first-person
// sword seated by its handle, as the game draws it, then hung by its origin as before #10395, which the blade and palm
// pairs should catch; and the orc's cleaver as the donor hangs it, then drawn turned 90° in its hand, which its palm
// pair should catch.
//
//   node lab/tests/landmarks-live.mjs            starts its own session in lab/, and stops it after
//   LAB_URL=http://127.0.0.1:PORT node ...     uses a running `rusty dev --live-debug` session instead
//   RB_CHROMIUM=/path/to/chrome node ...       the Chromium the session opens, when none is on PATH
//
// Needs `rusty` and local/import (docs/lab.md). Prints ok/FAIL lines as the smoke does.
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const lab = join(dirname(fileURLToPath(import.meta.url)), '..');
let url = process.env.LAB_URL?.replace(/\/$/, '');
let session = null;
if (!url) {
  const args = ['dev', 'start', '--headless', '--live-debug', '--allow-ephemeral-port', '--label', 'landmarks-live', '--idle-timeout', '10'];
  if (process.env.RB_CHROMIUM) args.push('--chromium', process.env.RB_CHROMIUM);
  const started = JSON.parse(execFileSync('rusty', args, { cwd: lab, encoding: 'utf8' }).trim().split('\n').pop());
  url = started.url;
  session = started.id;
}

const exec = async (command) => {
  const response = await fetch(`${url}/__rusty/product/runtime/debug/execute`, { method: 'POST', headers: { 'content-type': 'text/plain; charset=utf-8' }, body: command });
  const text = (await response.text()).trim();
  if (!response.ok) throw new Error(`${command}: HTTP ${response.status} ${text.slice(0, 200)}`);
  return text;
};
const sleep = (seconds) => new Promise((resolve) => setTimeout(resolve, seconds * 1000));

let failures = 0;
const check = (ok, text) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${text}`); if (!ok) failures++; };

// The pairs as the last frame measured them, by name.
const measured = async () => {
  const answer = JSON.parse(await exec('engine.inspect.landmarks held.landmarks'));
  return Object.fromEntries((answer.sets?.[0]?.pairs ?? []).map((p) => [p.name, p]));
};
const show = (p) => p ? `${p.within ? 'within' : 'out'} ${(p.distance * 100).toFixed(1)} cm${p.angle !== undefined ? `, ${p.angle.toFixed(0)}°` : ''}` : 'absent';

try {
  await exec('lab.orc.hold on');
  await exec('lab.orc.place 12 0 14.5');
  await exec('lab.held.seat on');
  await sleep(2);
  const seated = await measured();
  const names = ['player.blade', 'player.palm', 'player.guard', 'orc.palm', 'orc.guard', 'orc.shield'];
  check(names.every((n) => seated[n]?.drawn && seated[n].within),
    `seated by its handle, every grip pair is within: ${names.map((n) => `${n} ${show(seated[n])}`).join('; ')}`);

  await exec('lab.held.seat off');
  await sleep(1.5);
  const hung = await measured();
  check(!hung['player.blade'].within && hung['player.blade'].angle > 60 && !hung['player.palm'].within,
    `hung by its origin (before #10395), the pairs catch the turned sword: player.blade ${show(hung['player.blade'])}, player.palm ${show(hung['player.palm'])}, player.guard ${show(hung['player.guard'])}`);
  await exec('lab.held.seat on');

  await exec('lab.held.turn 90');
  await sleep(1.5);
  const turned = await measured();
  check(!turned['orc.palm'].within && turned['orc.palm'].angle > 60,
    `the orc's cleaver turned 90° in its hand, its palm pair catches it: orc.palm ${show(turned['orc.palm'])}, orc.guard ${show(turned['orc.guard'])}`);
  await exec('lab.held.turn 0');
} catch (error) {
  check(false, `the run failed: ${error.message}`);
} finally {
  if (session) execFileSync('rusty', ['dev', 'stop', session], { cwd: lab, stdio: 'ignore' });
}
process.exit(failures > 0 ? 1 : 0);
