// The mirror's status line: where the replay is and how to drive it. Input is the
// product's own key mappings (DarkMessiah.Mirror.csproj); this only draws.
export function mountProductUi(root, context) {
  const panel = root.ownerDocument.createElement('div');
  panel.style.cssText = [
    'position:absolute', 'left:12px', 'bottom:12px', 'padding:8px 12px',
    'font:13px/1.4 ui-monospace, Menlo, Consolas, monospace', 'color:#f2efe6',
    'background:rgba(20,18,16,.72)', 'border-radius:6px', 'pointer-events:none', 'white-space:pre',
  ].join(';');
  root.append(panel);
  const draw = (value) => {
    if (!value) { panel.textContent = 'loading recording…'; return; }
    const state = value.playing ? 'playing' : 'paused';
    panel.textContent =
      `${value.map}  tick ${value.tick}  (${value.firstTick}–${value.lastTick})  ${value.seconds.toFixed(2)} s\n` +
      `${state} ×${value.speed}  camera ${value.camera}  drawn ${value.drawn}  held ${value.held}\n` +
      'space play/pause  ←/→ step a tick  ↑/↓ speed  C camera';
  };
  const unsubscribe = context.projection?.subscribe(draw);
  draw(context.projection?.current());
  return { dispose: () => { unsubscribe?.(); panel.remove(); } };
}
