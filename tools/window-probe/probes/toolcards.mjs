// The open conversation's tool calls in order, each as its one-line card text (tool and subject), plus how many
// there were. HEAD=<n> keeps the first n. For checking HOW an agent went about a task, which the tail cannot show.
export default async (api) => {
  await api.sleep(800);
  const cards = await api.evaluate(`[...document.querySelectorAll('[data-testid=tool-call]')]
    .map(e => (e.querySelector('summary') || e).innerText.replace(/\\s+/g, ' ').trim().slice(0, 160))`);
  const head = Number(process.env.HEAD || cards.length);
  return { count: cards.length, cards: cards.slice(0, head) };
};
