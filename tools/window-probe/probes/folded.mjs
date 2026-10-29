// Observe only: the text inside every folded "earlier wording" block of the open conversation, read from the DOM
// (textContent, so a closed details element is read without opening it).
export default async (api) => api.evaluate(`[...document.querySelectorAll('details')].map(d =>
  d.textContent.replace(/[ \\t\\r\\n]+/g, ' ').trim().slice(0, 400))`);
