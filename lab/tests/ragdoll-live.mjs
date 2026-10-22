// The knocked-down orc as an Engine ragdoll (#10216), checked live: the ragdoll spawns from the pose the orc was last
// drawn in, which the smoke's headless test host never draws, so this runs the lab under `rusty dev --headless` (a
// headless Chromium keeps the world drawing) and knocks the orc down through its live-debug commands.
//
//   node lab/tests/ragdoll-live.mjs            starts its own session in lab/, and stops it after
//   LAB_URL=http://127.0.0.1:PORT node ...     uses a running `rusty dev --live-debug` session instead
//
// Needs `rusty` and a Chromium on PATH, and local/import (docs/lab.md). Prints ok/FAIL lines as the smoke does.
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const lab = join(dirname(fileURLToPath(import.meta.url)), '..');
let url = process.env.LAB_URL?.replace(/\/$/, '');
const own = !url;
if (own) {
  const started = JSON.parse(execFileSync('rusty', ['dev', 'start', '--headless', '--live-debug', '--label', 'ragdoll-live'], { cwd: lab, encoding: 'utf8' }).trim().split('\n').pop());
  url = started.url;
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

// The donor's knockdowns (KnockdownCalibration, 35 in local/recordings): up 2.9 to 4.2 s after the blow; 12 got up
// on the other side from the first frame the blow chose.
const donor = { low: 2.9, high: 4.2, other: 12, of: 35 };
// Blows from the player's side turned this many degrees, sideways (the power attack's impactdirY 1) or straight on.
const blows = [[0, 0], [0, 1], [-30, 1], [30, 1], [-60, 1], [60, 1], [15, 1], [-15, 1], [45, 1], [-45, 1], [20, 1], [-20, 1],
  [30, 0], [-30, 0], [60, 0], [-60, 0], [180, 0], [150, 1], [-150, 1], [0, 1]];

try {
  await exec('lab.orc.hold on');
  if (!(await exec('lab.god')).includes('god on')) await exec('lab.god');
  const before = Number((await exec('lab.orc.ragdoll')).match(/knockdowns (\d+)/)[1]);
  let firstLie = '';
  for (const [degrees, sideways] of blows) {
    await exec('lab.orc.place 12 0.02 14');
    await sleep(1.2);
    await exec(`lab.orc.knockdown ${degrees} ${sideways}`);
    // Falling, its bodies are drawn low: the readout's pelvis and head heights.
    await sleep(1.5);
    const lying = await exec('lab.orc.ragdoll');
    if (!firstLie) firstLie = lying;
    await sleep(3.1);
  }
  const report = await exec('lab.orc.ragdoll');
  const runs = [...report.matchAll(/\b([bf])([bf]):([0-9.]+)/g)].slice(before).map(([, fell, lay, seconds]) => ({ fell, lay, seconds: Number(seconds) }));
  check(runs.length === blows.length, `every felling blow dropped a ragdoll and the orc got up: ${runs.length} of ${blows.length}`);
  const heights = firstLie.match(/head ([0-9.-]+), pelvis ([0-9.-]+)/);
  check(heights && Number(heights[1]) < 0.5 && Number(heights[2]) < 0.5 && firstLie.includes('lies on back'),
    `a blow from in front lays it on its back, its head and pelvis on the ground (${heights?.[1]} m, ${heights?.[2]} m)`);
  check(runs.every((r) => r.seconds >= donor.low && r.seconds <= donor.high),
    `it gets up ${[...new Set(runs.map((r) => r.seconds))].join(', ')} s after the blow, inside the donor's ${donor.low}–${donor.high} s`);
  const other = runs.filter((r) => r.fell !== r.lay).length;
  const p = donor.other / donor.of, q = other / runs.length;
  const spread = 2 * Math.sqrt(p * (1 - p) / donor.of + q * (1 - q) / runs.length) + 0.05;
  check(Math.abs(p - q) <= spread,
    `it gets up on the other side from its first frame ${other} of ${runs.length} times (the side it lies on at rest), the donor's ${donor.other} of ${donor.of} (within ${spread.toFixed(2)})`);
} catch (error) {
  check(false, `the live run: ${error.message}`);
} finally {
  if (own) execFileSync('rusty', ['dev', 'stop'], { cwd: lab });
}
console.log(failures === 0 ? 'all ok' : `${failures} FAIL`);
process.exit(failures === 0 ? 0 : 1);
