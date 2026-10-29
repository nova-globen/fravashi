export default async (api) => {
  const info = await api.evaluate(`({
    url: location.href,
    tree: [...document.querySelectorAll('.code-tree-body [class*=row], .code-tree-body li')].map(e => e.textContent.trim()).filter(Boolean).slice(0, 15),
    titlebar: document.querySelector('.window-titlebar, [class*=titlebar]')?.innerText.replace(/\\s+/g,' ').slice(0, 200),
    rail: !!document.querySelector('.code-session-rail'),
    agentToggle: [...document.querySelectorAll('[class*=agent-panel-toggle], [data-testid*=agent]')].map(e => ({cls: e.className, pressed: e.getAttribute('aria-pressed'), title: e.title || e.getAttribute('aria-label')})),
    bodyScroll: [document.documentElement.scrollHeight, document.documentElement.clientHeight, document.body.scrollHeight],
  })`);
  await api.shot(process.env.SHOT);
  return info;
};
