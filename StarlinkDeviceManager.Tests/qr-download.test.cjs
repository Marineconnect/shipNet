const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../wwwroot/js/qr-download.js'), 'utf8');
const url = new URL('https://portal.example/Payments/DownloadNinePayQrCard?qrUrl=https%3A%2F%2Fprovider.example%2Fqr.png');
function setup(fetch) {
  const context = vm.createContext({ fetch, URL, AbortSignal, document: { addEventListener() {} } });
  vm.runInContext(script, context);
  return context.loadQrDownloadImage;
}
test('downloads through browser even when server cannot reach provider', async () => {
  let calls = 0;
  const load = setup(async () => {
    calls++;
    return new Response(new Blob(['image']), { headers: { 'content-type': 'image/png' } });
  });
  assert.equal(await (await load(url)).text(), 'image');
  assert.equal(calls, 1);
});
test('uses proxy when provider blocks browser CORS', async () => {
  let calls = 0;
  const load = setup(async (request, options) => {
    if (++calls === 1) throw new TypeError('CORS');
    assert.equal(request.searchParams.get('rawQr'), 'true');
    assert.equal(options.credentials, 'same-origin');
    return new Response('proxy image', { headers: { 'content-type': 'application/octet-stream' } });
  });
  assert.equal(await (await load(url)).text(), 'proxy image');
});
test('does not download provider HTML as an image', async () => {
  let calls = 0;
  const load = setup(async () => ++calls === 1
    ? new Response('<html>error</html>', { headers: { 'content-type': 'text/html' } })
    : new Response('image', { headers: { 'content-type': 'image/png' } }));
  assert.equal(await (await load(url)).text(), 'image');
  assert.equal(calls, 2);
});
test('reports proxy failure and trace after browser failure', async () => {
  let calls = 0;
  const load = setup(async () => {
    if (++calls === 1) throw new TypeError('CORS');
    return Response.json({ message: 'QR provider returned HTTP 403.', traceId: 'trace-1' }, { status: 502 });
  });
  await assert.rejects(load(url), /HTTP 403.*trace-1/);
});
test('rejects login HTML returned with HTTP 200', async () => {
  const load = setup(async () => new Response('<html>login</html>', { headers: { 'content-type': 'text/html' } }));
  await assert.rejects(load(url), /did not return a QR image/);
});
