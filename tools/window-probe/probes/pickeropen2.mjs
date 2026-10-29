// Observe only: whether the model picker's panel is open.
export default async (api) => api.evaluate(`!!document.querySelector('[data-testid=model-picker-panel]')`);
