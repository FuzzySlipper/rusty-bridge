import { createPauseFlow } from './pause.js';
import { createDeveloperShell, developerAvailable } from './developer.js';

/**
 * DOM companion: composition and focus only. It observes the Engine-delivered `rusty-bridge.lab.hud` projection,
 * submits the declared `reset` intent, opens the pause menu over the Engine lifecycle port (M) and docks the Engine
 * developer shell (F3, under `rusty dev --live-debug`). C# owns scene, controller, action and world-time state; the
 * Engine owns the canvas, input delivery, pointer lock, lifecycle and the debug panels.
 *
 * Provenance: shell mounting after rusty-hotel `src/ui/main.js` (pause flow, developer shell beside play, input
 * returned to gameplay on close), reduced to the lab's two screens.
 */
const MENU_KEY = 'KeyM';
const DEVELOPER_KEY = 'F3';

export function mountProductUi(root, context) {
  const document = root.ownerDocument;
  const panel = document.createElement('div');
  panel.className = 'fp-lab';
  panel.innerHTML = `
    <style>
      .fp-lab { position:fixed; inset:0; pointer-events:none; color:#ecf3fb; font:13px/1.5 system-ui,sans-serif; }
      .fp-lab * { box-sizing:border-box; }
      .fp-lab header { position:absolute; top:24px; left:28px; border-left:3px solid #58cab5; padding-left:14px; text-shadow:0 2px 4px #000; }
      .fp-lab h1 { margin:2px 0; font-size:22px; letter-spacing:.02em; font-weight:600; }
      .fp-lab .eyebrow { color:#8ed8cf; font:11px ui-monospace,monospace; letter-spacing:.14em; }
      .fp-lab .room { margin-top:8px; color:#c7d3e4; font-size:12px; }
      .fp-lab .health-bar { margin-top:8px; width:240px; height:10px; border:1px solid #ffffff40; border-radius:5px; background:#1a0d10cc; overflow:hidden; }
      .fp-lab .health-bar > div { height:100%; width:0; background:linear-gradient(#e25b5b,#a32626); transition:width .12s linear; }
      .fp-lab .telemetry { position:absolute; top:24px; right:24px; width:270px; padding:14px 18px; border:1px solid #ffffff26; border-radius:8px; background:#111a28d9; }
      .fp-lab .telemetry strong { color:#7adebc; font:11px ui-monospace,monospace; letter-spacing:.07em; }
      .fp-lab dl { display:grid; grid-template-columns:62px 1fr; margin:10px 0 0; gap:5px 12px; font:11px/1.6 ui-monospace,monospace; }
      .fp-lab dt { color:#93a5bd; } .fp-lab dd { margin:0; overflow-wrap:anywhere; }
      .fp-lab .reticle { position:absolute; left:50%; top:50%; width:5px; height:5px; border-radius:50%; background:#fff; box-shadow:0 0 0 2px #0007; transform:translate(-50%,-50%); }
      .fp-lab .target { position:absolute; top:calc(50% + 22px); left:50%; transform:translateX(-50%); background:#111a28bf; border-radius:5px; padding:4px 10px; white-space:nowrap; }
      .fp-lab .target:empty, .fp-lab .notice:empty { display:none; }
      .fp-lab footer { position:absolute; bottom:24px; left:28px; right:28px; display:flex; align-items:flex-end; justify-content:space-between; gap:20px; }
      .fp-lab .guide { background:#111a28d9; border:1px solid #ffffff26; border-radius:8px; padding:12px 16px; }
      .fp-lab .guide p { margin:2px 0; } .fp-lab .muted { color:#a4b4c9; font-size:11px; }
      .fp-lab kbd { color:#fff; font:11px ui-monospace,monospace; background:#ffffff15; padding:1px 4px; border-radius:3px; }
      .fp-lab button { pointer-events:auto; cursor:pointer; color:#dfeafa; background:#203044; border:1px solid #667d96; border-radius:5px; padding:7px 12px; margin-top:8px; }
      .fp-lab .menu { position:absolute; inset:0; display:grid; place-items:center; background:#070b12a6; pointer-events:auto; }
      .fp-lab .menu[hidden] { display:none; }
      .fp-lab .menu section { min-width:280px; padding:20px 24px; border:1px solid #ffffff26; border-radius:10px; background:#111a28f2; display:grid; gap:6px; }
      .fp-lab .menu h2 { margin:0 0 6px; font-size:18px; font-weight:600; }
      .fp-lab .developer-host { position:absolute; inset:0; pointer-events:none; }
      .fp-lab .developer-host > * { pointer-events:auto; }
      .fp-lab .developer-error { position:absolute; top:24px; left:28px; margin:0; padding:8px 12px; border-radius:6px; background:#5a1f24; }
    </style>
    <header><div class="eyebrow">RUSTY BRIDGE / DARK MESSIAH COMBAT LAB</div><h1>Sword against orc</h1><div class="health-bar" role="meter" aria-label="Health" aria-valuemin="0" aria-valuemax="100"><div data-fill="healthFill"></div></div><div class="room" data-fact="health"></div></header>
    <aside class="telemetry" aria-label="Controller telemetry"><strong data-fact="grounded">CONNECTING</strong>
      <dl><dt>sword</dt><dd data-fact="move"></dd><dt>orc</dt><dd data-fact="orc"></dd><dt>last blow</dt><dd data-fact="blow"></dd><dt>you</dt><dd data-fact="swings"></dd><dt>orc blows</dt><dd data-fact="orcblows"></dd><dt>feet XYZ</dt><dd data-fact="position"></dd><dt>stance</dt><dd data-fact="stance"></dd><dt>world</dt><dd data-fact="world"></dd><dt>step</dt><dd data-fact="step"></dd></dl>
    </aside>
    <div class="reticle"></div><output class="target" data-fact="target"></output>
    <footer><div class="guide"><p data-fact="message">Click the scene to capture the mouse</p><p class="muted notice" data-fact="notice"></p><p><kbd>WASD</kbd> move · <kbd>Space</kbd> jump · <kbd>Shift</kbd> sprint · <kbd>Ctrl</kbd> crouch</p><p><kbd>Left</kbd> tap: fast attack, hold: power attack · <kbd>Right</kbd> hold: parry · <kbd>F</kbd> kick · <kbd>E</kbd> pick up / drop, <kbd>Left</kbd> throws</p><p><kbd>G</kbd> god · <kbd>P</kbd> hold world · <kbd>O</kbd> step world · <kbd>R</kbd> reset · <kbd>M</kbd> menu · <kbd>F3</kbd> developer · <kbd>Esc</kbd> release mouse</p><p class="muted">Clips, hit windows, damage and range are Arkane's, from the imported .qct files and clip events; content/combat.json holds the lab's own numbers.</p></div>
      <div class="guide"><div class="muted" data-fact="actions"></div><button type="button" data-reset>Reset lab</button> <button type="button" data-menu>Menu</button></div>
    </footer>
    <div class="menu" data-rusty-ui-interactive hidden><section role="dialog" aria-label="Paused"><h2>Paused</h2><p class="muted" data-lifecycle></p>
      <button type="button" data-resume>Resume</button><button type="button" data-reset>Reset lab</button><button type="button" data-developer hidden>Developer shell</button></section></div>
    <div class="developer-host"></div>`;
  root.append(panel);
  let disposed = false;
  const fields = [...panel.querySelectorAll('[data-fact]')];
  // Bars sized by a fact's percent: the health bar follows the player's health against its maximum.
  const fills = [...panel.querySelectorAll('[data-fill]')];
  const menu = panel.querySelector('.menu');
  const developerButton = panel.querySelector('[data-developer]');
  const toGameplay = () => {
    if (disposed) return;
    context.ui.setInteractionMode('gameplay');
    context.ui.focusGameplay();
  };
  const developer = createDeveloperShell(panel.querySelector('.developer-host'), { viewport: context.viewport, onClose: toGameplay });
  // The debug service may not answer while the product loads, so the menu asks again each time it opens.
  const askDeveloper = async () => {
    const available = await developerAvailable();
    if (!disposed) developerButton.hidden = !available;
    return available;
  };
  const toggleDeveloper = async () => {
    if (developer.isOpen) { developer.close(); return; }
    if (!menu.hidden || !await askDeveloper()) return;
    context.ui.setInteractionMode('interface');
    await developer.open();
  };

  // The menu follows the Engine's lifecycle state, so a pause from elsewhere (a tool, another page) shows it too.
  const drawLifecycle = ({ state, pending, error }) => {
    if (disposed) return;
    const paused = state === 'paused';
    menu.hidden = !paused && pending !== 'paused';
    if (paused) { context.ui.setInteractionMode('interface'); void askDeveloper(); }
    const message = pending ? (pending === 'paused' ? 'Pausing…' : 'Resuming…') : paused ? 'The lab is paused.' : state === 'running' ? 'Running.' : 'Lab unavailable.';
    panel.querySelector('[data-lifecycle]').textContent = error ? `${error} ${message}` : message;
    for (const button of menu.querySelectorAll('button')) button.disabled = !!pending;
  };
  const pause = createPauseFlow(context.lifecycle, drawLifecycle);
  drawLifecycle(pause.snapshot());
  const openMenu = () => { if (developer.isOpen) developer.close(); void pause.pause(); };
  const resume = async () => { if (await pause.resume()) toGameplay(); };

  const reset = () => context.intents?.claim('reset', { kind: 'digital', active: true });
  const onClick = event => {
    const button = event.target.closest('button');
    if (!button || !panel.contains(button)) return;
    if (button.hasAttribute('data-reset')) reset();
    else if (button.hasAttribute('data-menu')) openMenu();
    else if (button.hasAttribute('data-resume')) void resume();
    else if (button.hasAttribute('data-developer')) void resume().then(() => { if (menu.hidden) void toggleDeveloper(); });
  };
  const onKey = event => {
    const editing = event.target instanceof Element && event.target.closest('input,textarea,select,[contenteditable="true"]');
    if (editing || event.ctrlKey || event.altKey || event.metaKey || event.repeat) return;
    if (event.code === MENU_KEY) {
      event.preventDefault();
      if (menu.hidden) openMenu(); else void resume();
    } else if (event.code === DEVELOPER_KEY) {
      event.preventDefault();
      void toggleDeveloper();
    }
  };
  panel.addEventListener('click', onClick);
  document.addEventListener('keydown', onKey, true);
  const unsubscribe = context.projection?.subscribe(envelope => {
    if (disposed || envelope?.stream !== 'darkmessiah-lab' || envelope.contract !== 'rusty-bridge.lab.hud') return;
    const facts = envelope.value;
    for (const field of fields) field.textContent = facts[field.dataset.fact] ?? '';
    for (const fill of fills) {
      const percent = Math.min(100, Math.max(0, Number(facts[fill.dataset.fill] ?? 0) || 0));
      fill.style.width = `${percent}%`;
      fill.parentElement?.setAttribute('aria-valuenow', String(percent));
    }
  });
  return {
    dispose() {
      disposed = true;
      panel.removeEventListener('click', onClick);
      document.removeEventListener('keydown', onKey, true);
      unsubscribe?.();
      pause.dispose();
      developer.dispose();
      panel.remove();
    }
  };
}
