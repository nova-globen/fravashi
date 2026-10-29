// Navigate only, by clicking an in-app link whose href matches NAV (a regular expression source). Fire and return.
export default async (api) => {
  const how = await api.evaluate(`(() => {
    const re = new RegExp(${JSON.stringify(process.env.NAV || '/sessions')});
    const link = [...document.querySelectorAll('a')].find(a => re.test(a.getAttribute('href') || ''));
    if (!link) return 'no link for ' + re;
    setTimeout(() => link.click(), 50);
    return 'clicking ' + link.getAttribute('href');
  })()`);
  return { how };
};
