#!/bin/sh
# What does the pinned agent CLI send to its base URL? A listener that records method, url and header NAMES
# (values only for the two anthropic-* headers, never for authorization), answers 400, and exits.
cd /tmp
cat > /tmp/cap.js <<'JS'
const http = require('http');
let n = 0;
const s = http.createServer((req, res) => {
  const names = Object.keys(req.headers).sort();
  console.log(JSON.stringify({ method: req.method, url: req.url, headerNames: names,
    'anthropic-beta': req.headers['anthropic-beta'] || null, 'anthropic-version': req.headers['anthropic-version'] || null,
    authorizationScheme: (req.headers['authorization'] || '').split(' ')[0] || null, hasXApiKey: 'x-api-key' in req.headers }));
  req.resume();
  res.writeHead(400, { 'content-type': 'application/json' });
  res.end('{"type":"error","error":{"type":"invalid_request_error","message":"capture only"}}');
  if (++n >= 3) setTimeout(() => process.exit(0), 200);
});
s.listen(18099, '127.0.0.1');
setTimeout(() => process.exit(0), 40000);
JS
node /tmp/cap.js > /tmp/cap.out 2>&1 &
sleep 1
ANTHROPIC_BASE_URL=http://127.0.0.1:18099 ANTHROPIC_AUTH_TOKEN=dummy-not-a-credential HOME=/tmp/caphome \
  timeout 30 claude -p "say hi" --tools "" >/dev/null 2>&1
sleep 1
cat /tmp/cap.out
rm -rf /tmp/cap.js /tmp/cap.out /tmp/caphome
