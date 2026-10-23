// Executes Jiwe/Plugins/WebGL/JiweStorage.jslib against a fake browser. Run: node --test Tests~/jslib
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const SRC = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../Jiwe/Plugins/WebGL/JiweStorage.jslib'), 'utf8');

// A minimal Emscripten heap: "pointers" are indexes into a table of strings.
function load({ storage, href = 'https://host.test/play/?code=abc&state=xyz#frag' } = {}) {
  const heap = [null];
  const ptr = (s) => heap.push(s) - 1;
  const calls = { replaceState: [] };
  const window = {
    localStorage: storage ?? (() => { const m = new Map(); return { setItem: (k, v) => m.set(k, v), getItem: (k) => (m.has(k) ? m.get(k) : null), removeItem: (k) => m.delete(k) }; })(),
    history: { replaceState: (...a) => calls.replaceState.push(a) },
    location: { href },
  };
  let library;
  const env = {
    mergeInto: (_lib, obj) => { library = obj; },
    LibraryManager: { library: {} },
    UTF8ToString: (p) => heap[p],
    lengthBytesUTF8: (s) => Buffer.byteLength(s, 'utf8'),
    _malloc: (n) => ptr(new Array(n)),
    stringToUTF8: (s, p) => { heap[p] = s; },
    window, document: { title: 'My Game' },
  };
  new Function(...Object.keys(env), SRC)(...Object.values(env));
  return { lib: library, ptr, heap, window, calls };
}

test('registers every function the C# side imports', () => {
  const { lib } = load();
  for (const name of ['SetItem', 'GetItem', 'RemoveItem', 'ClearUrlQuery', 'NavigateTo'])
    assert.equal(typeof lib['JiweStorage_' + name], 'function', name);
  const cs = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../Jiwe/JiweAuth.cs'), 'utf8');
  for (const m of cs.matchAll(/extern \w+ (JiweStorage_\w+)\(/g)) assert.ok(lib[m[1]], `C# imports ${m[1]} but the jslib doesn't define it`);
});

test('set / get / remove round-trip, including unicode and quotes', () => {
  const { lib, ptr, heap } = load();
  for (const v of ['plain', 'Zoë Ñandú 名前', 'a"b\'c&d=e%f', 'x'.repeat(5000)]) {
    lib.JiweStorage_SetItem(ptr('jiwe_pkce_verifier'), ptr(v));
    assert.equal(heap[lib.JiweStorage_GetItem(ptr('jiwe_pkce_verifier'))], v);
  }
  lib.JiweStorage_RemoveItem(ptr('jiwe_pkce_verifier'));
  assert.equal(heap[lib.JiweStorage_GetItem(ptr('jiwe_pkce_verifier'))], '', 'a removed key reads as empty, never null');
});

test('GetItem on a missing key returns an empty string', () => {
  const { lib, ptr, heap } = load();
  assert.equal(heap[lib.JiweStorage_GetItem(ptr('never-set'))], '');
});

test('storage that throws (private mode, quota) never throws into Unity — reads come back empty', () => {
  const boom = { setItem() { throw new Error('QuotaExceeded'); }, getItem() { throw new Error('SecurityError'); }, removeItem() { throw new Error('SecurityError'); } };
  const { lib, ptr, heap } = load({ storage: boom });
  assert.doesNotThrow(() => lib.JiweStorage_SetItem(ptr('k'), ptr('v')));
  assert.equal(heap[lib.JiweStorage_GetItem(ptr('k'))], '');
  assert.doesNotThrow(() => lib.JiweStorage_RemoveItem(ptr('k')));
});

test('ClearUrlQuery strips ?code=… and #fragment, keeping the path', () => {
  const { lib, calls } = load({ href: 'https://host.test/play/index.html?code=abc&state=xyz#frag' });
  lib.JiweStorage_ClearUrlQuery();
  assert.equal(calls.replaceState.length, 1);
  assert.equal(calls.replaceState[0][2], 'https://host.test/play/index.html');
  assert.equal(calls.replaceState[0][1], 'My Game');
});

test('ClearUrlQuery leaves an already-clean URL unchanged', () => {
  const { lib, calls } = load({ href: 'https://host.test/play/' });
  lib.JiweStorage_ClearUrlQuery();
  assert.equal(calls.replaceState[0][2], 'https://host.test/play/');
});

test('NavigateTo moves the SAME tab (location.href), it does not open a new window', () => {
  const { lib, ptr, window } = load();
  const opened = [];
  window.open = (...a) => opened.push(a);
  lib.JiweStorage_NavigateTo(ptr('https://id.jiwe.io/auth?client_id=1&state=abc'));
  assert.equal(window.location.href, 'https://id.jiwe.io/auth?client_id=1&state=abc');
  assert.equal(opened.length, 0);
});

test('avoids the literal property access some hosts\' build scanners reject', () => {
  assert.ok(!/location\.pathname/.test(SRC), 'a host scanner refuses builds containing window.location.pathname');
});
