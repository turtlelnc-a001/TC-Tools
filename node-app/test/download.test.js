const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { download } = require('../lib/core');

const data = Buffer.alloc(9 * 1024 * 1024 + 13);
for (let i = 0; i < data.length; i++) data[i] = i % 251;

async function scenario(mode) {
  const ranges = [];
  const server = http.createServer((req, res) => {
    if (req.url === '/redirect') { res.writeHead(302, { Location: '/file' }); res.end(); return; }
    const match = /^bytes=(\d+)-(\d+)$/.exec(req.headers.range || '');
    if (match) ranges.push(req.headers.range);
    if (match && mode !== 'ignore' && !(mode === 'bad' && Number(match[1]) > 0)) {
      const start = Number(match[1]), end = Number(match[2]);
      res.writeHead(206, { 'Content-Range': `bytes ${start}-${end}/${data.length}`,
        'Content-Length': end - start + 1 });
      res.end(data.subarray(start, end + 1));
    } else { res.writeHead(200, { 'Content-Length': data.length }); res.end(data); }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'tctool-download-'));
  const output = path.join(dir, 'installer.bin');
  try {
    await download(`http://127.0.0.1:${server.address().port}/redirect`, output);
    assert.deepEqual(await fs.readFile(output), data);
    assert.equal((await fs.readdir(dir)).length, 1);
    return ranges;
  } finally { await new Promise((resolve) => server.close(resolve)); await fs.rm(dir, { recursive: true, force: true }); }
}

test('four concurrent ranges are assembled in order', async () => {
  const ranges = await scenario('range');
  assert.equal(ranges.length, 5); // probe plus four segments
});
test('server ignoring Range falls back to one GET', async () => {
  const ranges = await scenario('ignore');
  assert.equal(ranges.length, 1);
});
test('invalid later range falls back without mixing partial data', async () => {
  const ranges = await scenario('bad');
  assert.equal(ranges.length, 5);
});
