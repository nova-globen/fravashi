// Observe only: every select on the page with its data-testid and the text of the chosen option. With SET=<testid>
// and TO=<option text prefix>, chooses that option first (a change event, which Blazor binds), then reads.
export default async (api) => {
  await api.sleep(500);
  return api.evaluate(`(() => {
    const set = ${JSON.stringify(process.env.SET || '')}, to = ${JSON.stringify(process.env.TO || '')};
    let did = null;
    if (set) {
      const s = document.querySelector('[data-testid="' + set + '"]');
      const o = s && [...s.options].find(x => x.text.trim().startsWith(to));
      if (o) { s.value = o.value; s.dispatchEvent(new Event('change', { bubbles: true })); did = o.text.trim(); }
      else did = 'no such option';
    }
    return { did, selects: [...document.querySelectorAll('select')].map(s => ({
      id: s.getAttribute('data-testid'), chosen: s.selectedOptions[0] ? s.selectedOptions[0].text.trim() : null })) };
  })()`);
};
