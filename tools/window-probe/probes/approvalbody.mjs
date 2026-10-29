// Observe only: the full text of the pending approval's 'What it wants to do' block.
export default async (api) => api.evaluate(`(() => { const p = document.querySelector('.session-approval-body pre'); return p ? p.textContent : null; })()`);
