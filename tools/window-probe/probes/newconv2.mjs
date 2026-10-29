// Start a fresh conversation, confirming when the pane asks because one is still running. Click only.
export default async (api) => {
  const first = await api.evaluate(`(() => {
    const b = document.querySelector('[data-testid=new-conversation]') || document.querySelector('[data-testid=new-session]');
    if (!b) return 'no button';
    b.click();
    return 'clicked ' + b.getAttribute('data-testid');
  })()`);
  await api.sleep(700);
  const confirm = await api.evaluate(`(() => {
    const y = document.querySelector('[data-testid=new-conversation-confirm-yes]');
    if (!y) return 'no confirm';
    setTimeout(() => y.click(), 50);
    return 'confirmed';
  })()`);
  return { first, confirm };
};
