// Start a fresh conversation. Click only; the turn probe runs next.
export default async (api) => api.evaluate(`(() => {
  const b = document.querySelector('[data-testid=new-conversation]') || document.querySelector('[data-testid=new-session]');
  if (!b) return 'no button';
  setTimeout(() => b.click(), 50);
  return 'clicking ' + b.getAttribute('data-testid');
})()`);
