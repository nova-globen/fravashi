// Reads the Settings overview's sign-in row (backlog 94): whether the studio says it is signed in, and whether its
// Sign out button is there. Shows no identity: only whether a name is present.
export default async (api) => {
  await api.sleep(1500);
  return api.evaluate(`(() => {
    const row = document.querySelector('[data-testid=idp-session-row]');
    return {
      path: location.pathname,
      row: row ? (row.querySelector('.studio-muted')?.innerText || 'a name is shown') : 'no sign-in row',
      signOut: !!document.querySelector('[data-testid=idp-sign-out]'),
    };
  })()`);
};
