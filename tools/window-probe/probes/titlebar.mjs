// Presses one window title bar button by its data-window-action (ACTION=back|forward|undo|redo|palette) with a real
// mouse click and reports the location before and after. Since #287 those buttons are served by windowShell.js
// listeners instead of inline onclick attributes, so a location that moves on back or forward shows the listener
// runs under the studio's Content-Security-Policy. Never pass close, minimize or maximize: close takes the window,
// and its debugging port, down.
export default async (api) => {
  const action = process.env.ACTION;
  if (!['back', 'forward', 'undo', 'redo', 'palette'].includes(action)) return { error: 'ACTION must be back, forward, undo, redo or palette' };
  const before = await api.evaluate('location.pathname');
  const box = await api.click(`[data-window-action="${action}"]`);
  await api.sleep(Number(process.env.SETTLE || 2000));
  const after = await api.evaluate('location.pathname');
  return { action, clickedAt: box, before, after, moved: before !== after };
};
