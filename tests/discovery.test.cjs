const test = require('node:test');
const assert = require('node:assert/strict');
const { collect } = require('../tools/discover-dify.js');
const workspace = '50000000-0000-4000-8000-000000000001';
const app = { id: '60000000-0000-4000-8000-000000000001', name: 'Synthetic app', mode: 'chat' };
const origin = 'https://dify.example.invalid';
const token = 'SYNTHETIC-NOT-A-CREDENTIAL';
function fake(payload, opts = {}) {
  const calls = [];
  return { calls, fetchImpl: async (url, options) => {
    calls.push({ url, options });
    assert.equal(options.method, 'GET');
    assert.equal(options.redirect, 'error');
    assert.equal(options.headers.Authorization, 'Bearer ' + token);
    const body = url.includes('workspaces/current') ? { id: workspace } : payload;
    return { status: opts.status || 200, headers: { get: () => opts.mime || 'application/json' },
      text: async () => JSON.stringify(body) };
  }};
}
const invoke = mock => collect({ origin, expectedOrigin: origin, token, fetchImpl: mock.fetchImpl });
test('complete metadata projection excludes token and raw app content', async () => {
  const mock = fake({ total: 1, has_more: false, data: [{ ...app, prompt: 'PRIVATE-PROMPT', api_key: 'PRIVATE-KEY' }] });
  const result = await invoke(mock);
  assert.deepEqual(result.apps, [app]);
  assert.equal(result.complete, true);
  assert.equal(result.credentials_returned, false);
  assert.equal(mock.calls.length, 3);
  assert.match(mock.calls[1].url, /is_created_by_me=false/);
  const text = JSON.stringify(result);
  for (const excluded of [token, 'PRIVATE-PROMPT', 'PRIVATE-KEY']) assert.ok(!text.includes(excluded));
});
test('cross-origin target denied before any fetch', async () => {
  const mock = fake({});
  await assert.rejects(collect({ origin, expectedOrigin: 'https://other.example.invalid', token, fetchImpl: mock.fetchImpl }));
  assert.equal(mock.calls.length, 0);
});
test('missing session denied before any fetch', async () => {
  const mock = fake({});
  await assert.rejects(collect({ origin, expectedOrigin: origin, token: '', fetchImpl: mock.fetchImpl }));
  assert.equal(mock.calls.length, 0);
});
test('HTTP errors, login pages and malformed/incomplete results stop', async () => {
  for (const mock of [fake({}, { status: 401 }), fake({}, { mime: 'text/html' }),
    fake({ total: 2, has_more: false, data: [app] }),
    fake({ total: 2, has_more: false, data: [app, app] }),
    fake({ total: 1, has_more: true, data: [] }),
    fake({ total: 1, has_more: false, data: [{ ...app, mode: 'unrecognized' }] })]) {
    await assert.rejects(invoke(mock));
  }
});
test('workspace change prevents a complete checkpoint', async () => {
  let count = 0;
  const mock = fake({ total: 1, has_more: false, data: [app] });
  const base = mock.fetchImpl;
  mock.fetchImpl = async (...args) => {
    const response = await base(...args);
    if (++count === 3) response.text = async () => JSON.stringify({ id: '50000000-0000-4000-8000-000000000002' });
    return response;
  };
  await assert.rejects(invoke(mock), /Workspace changed/);
});
test('two pages use no selection filter and prove coverage', async () => {
  let page = 0;
  const calls = [];
  const result = await collect({ origin, expectedOrigin: origin, token, fetchImpl: async (url, options) => {
    calls.push(url);
    const body = url.includes('workspaces/current') ? { id: workspace } :
      { total: 2, has_more: ++page === 1, data: [{ ...app, id: app.id.slice(0, -1) + page }] };
    return { status: 200, headers: { get: () => 'application/json' }, text: async () => JSON.stringify(body) };
  }});
  assert.equal(result.total, 2);
  assert.equal(result.pages.length, 2);
  assert.ok(calls.some(url => url.includes('page=2&limit=100')));
});
