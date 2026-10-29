// The open work item's chips (status, level, kind, resolution, priority, version), its provider line and the start of its
// description. Run with the window on /workitems/{n}.
export default async (api) => {
  await api.waitFor(`!!document.querySelector('[data-testid=work-item-detail]')`, 10000);
  await api.sleep(800);
  return api.evaluate(`({
    chips: [...document.querySelectorAll('.work-item-chip')].map(c => c.innerText.trim()),
    provider: (document.querySelector('[data-testid=work-item-provider-line]') || {}).innerText || null,
    description: ((document.querySelector('[data-testid=work-item-description-text]') || {}).innerText || '').slice(0, 300),
  })`);
};
