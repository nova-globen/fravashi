// Evaluates EXPR in the studio page itself (read-only checks) and returns the value.
export default async (api) => ({ value: await api.evaluate(process.env.EXPR) });
