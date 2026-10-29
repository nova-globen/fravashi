// A compact read of Home's Autopilot card for a supervisor: the queue (item and stage), the limits line, how many running
// blocks are shown, and every approval card waiting on a person (its first line). Observe only. It read the retired
// autopilot page until work item #358 moved the autopilot onto Home; the history is the Recent dispatches card now.
export default async (api) => api.evaluate(`(() => {
  const body = document.querySelector('[data-testid=autopilot-card]');
  if (!body) return { page: location.pathname, autopilot: false };
  const text = body.innerText;
  const queue = [...body.querySelectorAll('[data-testid=autopilot-queue-item]')].map(q => q.innerText.trim());
  const money = (text.match(/Money on this run:[^\\n]*/) || [''])[0];
  const blocks = body.querySelectorAll('[data-testid=autopilot-running-item-block]').length;
  // A running item's card is the session pane's approval-prompt inside its running block; older selectors matched
  // nothing there, so a card waited ten minutes on 2026-10-17 while this reported none.
  const cards = [...document.querySelectorAll('[data-testid=approval-prompt], [data-testid*=approval-card], [data-testid*=tool-approval], .approval-card')]
    .map(c => c.innerText.trim().split('\\n').slice(0, 3).join(' | ').slice(0, 220));
  const recent = document.querySelector('[data-testid=recent-dispatches]');
  const history = recent ? recent.innerText.trim().slice(0, 400) : null;
  return { queue, money, blocks, cards, history };
})()`);
