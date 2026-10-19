/**
 * The Engine developer shell for the lab, docked beside play (which keeps running): an inspection view framing the
 * selected action's reach (C# inspection target `reach`), the tunables panel over the lab's page (C# LabTunables),
 * the scene tree and the console. While open it shows the action-volume overlay set (C# ActionOverlay). Closed, it
 * holds no panel and sends nothing. However it closes (its own Close, the key), `onClose` returns input to play.
 *
 * Provenance: rusty-hotel `src/ui/developer.js` (availability probe, overlay sets shown while open, load failure
 * shown in place), on the Engine's `mountDeveloperShell` `onClose` instead of hotel's element observer.
 */
const PLAYER_SETS = ['lab.combat'];

/**
 * Whether the Engine's debug service runs (a host started with --live-debug), asked through the Engine's own client.
 * Works the same in a streamed page and a window.
 */
export async function developerAvailable(loadPanel = () => import('@rusty-engine/live-debug')) {
  try {
    const { createLiveDebugHttpTransport } = await loadPanel();
    return (await createLiveDebugHttpTransport().catalog()).available === true;
  } catch {
    return false;
  }
}

export function createDeveloperShell(host, { viewport = null, onClose = () => {}, loadPanel = () => import('@rusty-engine/live-debug') } = {}) {
  let current = null;
  const close = () => {
    const previous = current;
    current = null;
    if (!previous) return;
    previous.shell?.close();
    if (previous.transport) {
      for (const set of PLAYER_SETS) void previous.transport.execute(`engine.inspect.overlay hide set ${set}`).catch(() => {});
    }
    previous.error?.remove();
    onClose();
  };
  return {
    get isOpen() { return current !== null; },
    async open() {
      if (current) return;
      const session = { shell: null, transport: null, error: null };
      current = session;
      try {
        const { mountDeveloperShell, createLiveDebugHttpTransport } = await loadPanel();
        if (current !== session) return;
        session.transport = createLiveDebugHttpTransport();
        session.shell = mountDeveloperShell(host, {
          side: 'left',
          presentation: 'dock',
          transport: session.transport,
          // The shell's own Close disposes its panels; closing follows it here.
          onClose: () => { if (current === session) close(); },
          panels: [
            ...(viewport ? [{ kind: 'view', title: 'Reach', options: { viewport, id: 'reach', subject: 'target reach', framings: ['side', 'top', 'front', 'close'] } }] : []),
            { kind: 'tunables', title: 'Lab tunables' },
            { kind: 'scene' },
            { kind: 'console' }
          ]
        });
        await session.shell.open();
        if (current !== session) { session.shell.close(); return; }
        for (const set of PLAYER_SETS) void session.transport.execute(`engine.inspect.overlay show set ${set}`).catch(() => {});
      } catch (error) {
        if (current !== session) return;
        session.error = host.ownerDocument.createElement('p');
        session.error.className = 'developer-error';
        session.error.setAttribute('role', 'alert');
        session.error.textContent = `Developer shell unavailable: ${error instanceof Error ? error.message : 'could not load the Engine panel'}`;
        host.append(session.error);
      }
    },
    close,
    dispose: close
  };
}
