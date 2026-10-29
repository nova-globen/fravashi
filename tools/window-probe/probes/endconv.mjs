// Ends the folder's live conversation the way a person can: New conversation, then "Stop and start fresh" on
// the strip that asks. Reports whether the strip appeared and whether Stop is gone afterwards.
export default async (api) => {
  await api.evaluate(`document.querySelector('[data-testid=new-conversation]').click()`);
  const asked = await api.waitFor(`!!document.querySelector('[data-testid=new-conversation-confirm-yes]')`, 5000);
  if (asked) await api.evaluate(`document.querySelector('[data-testid=new-conversation-confirm-yes]').click()`);
  await api.sleep(3000);
  return api.evaluate(`({ asked: ${asked}, stop: !!document.querySelector('[data-testid=stop-session]'),
    empty: !!document.querySelector('[data-testid=session-empty-start]'), path: location.pathname })`);
};
