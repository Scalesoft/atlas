// node --test tests/browser/fake-ai
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { after, before, beforeEach, describe, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { createFakeAi, EMBEDDING_MODEL, MODELS, UPSTREAM_DETAIL } from './server.mjs';
import { counts, fnv, near, vector } from './vectors.mjs';

process.env.FAKE_AI_LOG = '0';
const fixture = JSON.parse(readFileSync(fileURLToPath(new URL('../../fixtures/fake-ai-vectors.json', import.meta.url)), 'utf8'));

describe('trigram vectors match the shared fixture (the C# FakeAi checks the same file)', () => {
  test('FNV-1a hashes', () => {
    for (const { text, fnv: expected } of fixture.hashes) assert.equal(fnv(text), expected, text);
  });

  test('raw counts, near neighbours and unit vectors', () => {
    assert.equal(fixture.dimensions, 1024);
    assert.ok(fixture.entries.length > 10);
    for (const entry of fixture.entries) {
      const expected = new Array(1024).fill(0);
      for (const [slot, count] of entry.counts) expected[slot] = count;
      assert.deepEqual(Array.from(counts(entry.text)), expected, `counts of "${entry.text}"`);
      assert.equal(near(entry.text), entry.near);
      const unit = vector(entry.text);
      for (let slot = 0; slot < 1024; slot++) {
        assert.ok(Math.abs(unit[slot] - (entry.norm === 0 ? 0 : expected[slot] / entry.norm)) < 1e-12);
      }
    }
  });
});

describe('the fake model server', () => {
  const fake = createFakeAi();
  let base;

  before(async () => {
    await new Promise(resolve => fake.server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${fake.server.address().port}`;
  });
  after(() => {
    fake.release();
    fake.server.closeAllConnections();
    return new Promise(resolve => fake.server.close(resolve));
  });
  beforeEach(() => fake.reset());

  const post = (path, body) => fetch(base + path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  const mode = body => post('/__mode', body);
  const requests = async () => (await fetch(base + '/__requests')).json();
  const embed = (input, extra = {}) => post('/v1/embeddings', { model: EMBEDDING_MODEL, input, ...extra });

  test('lists both models', async () => {
    const answer = await (await fetch(base + '/v1/models')).json();
    assert.deepEqual(answer.data.map(model => model.id), MODELS);
  });

  test('embeddings are the 1 024 dimension unit vectors of the shared algorithm, as floats or as base64', async () => {
    const floats = await (await embed(['Obnova zálohy', 'second'])).json();
    assert.equal(floats.data.length, 2);
    assert.equal(floats.data[0].embedding.length, 1024);
    const expected = vector('Obnova zálohy');
    for (let slot = 0; slot < 1024; slot++) assert.ok(Math.abs(floats.data[0].embedding[slot] - expected[slot]) < 1e-6);

    const packed = await (await embed('Obnova zálohy', { encoding_format: 'base64' })).json();
    const bytes = Buffer.from(packed.data[0].embedding, 'base64');
    assert.equal(bytes.length, 1024 * 4);
    const decoded = new Float32Array(bytes.buffer, bytes.byteOffset, 1024);
    for (let slot = 0; slot < 1024; slot++) assert.ok(Math.abs(decoded[slot] - expected[slot]) < 1e-6);
  });

  test('a malformed embeddings request is a 400, not a crash', async () => {
    assert.equal((await embed(42)).status, 400);
    assert.equal((await fetch(base + '/v1/embeddings', { method: 'POST', body: '{nope' })).status, 400);
  });

  test('the chat answer of the connection test is { "ok": true }', async () => {
    const answer = await (await post('/v1/chat/completions', { model: 'x', messages: [], response_format: { type: 'json_schema', json_schema: {} } })).json();
    assert.deepEqual(JSON.parse(answer.choices[0].message.content), { ok: true });

    const inPrompt = await (await post('/v1/chat/completions', { messages: [{ role: 'system', content: 'Odpověz pouze jedním JSON objektem bez dalšího textu: {}' }] })).json();
    assert.deepEqual(JSON.parse(inPrompt.choices[0].message.content.match(/\{.*\}/s)[0]), { ok: true });
  });

  test('meaning rules give a paraphrase and a passage one vector without a shared word; near rules stay close', async () => {
    await post('/__meaning', { rules: [{ contains: 'záchrany dat', as: 'backup restore database' }, { contains: 'kudy ven', as: 'backup restore database' }, { contains: 'kousek', as: 'backup restore database', near: true }] });
    const [passage, query, close, other] = (await (await embed(['Postup záchrany dat po výpadku', 'Kudy ven z katastrofy', 'kousek textu', 'něco jiného'])).json()).data.map(item => item.embedding);
    const cosine = (a, b) => a.reduce((sum, value, slot) => sum + value * b[slot], 0);
    assert.ok(cosine(passage, query) > 0.999);
    assert.ok(cosine(passage, close) > 0.8 && cosine(passage, close) < 0.999);
    assert.ok(cosine(passage, other) < 0.5);
    assert.equal((await post('/__meaning', { rules: [{ contains: '' }] })).status, 400);
  });

  const answerRequest = {
    response_format: { type: 'json_schema', json_schema: { name: 'AnswerResult' } },
    messages: [
      { role: 'user', content: '<zdroj n="9">Old history must not be cited.</zdroj>' },
      { role: 'user', content: 'Otázka: Jak se připojit?\n<zdroj n="1" stránka="VPN" sekce="Přístup">\nSpusťte klienta VPN. Přihlaste se.\n</zdroj>\n<zdroj n="2" stránka="Certifikát" sekce="">\nVyžádejte certifikát.\n</zdroj>' },
    ],
  };
  const answer = async (body = answerRequest) => {
    const response = await post('/v1/chat/completions', body);
    const envelope = await response.json();
    return JSON.parse(envelope.choices[0].message.content.match(/\{.*\}/s)[0]);
  };

  test('answers cite only sources in the latest turn with native or prompt JSON schemas', async () => {
    const expected = { answerable: true, sentences: [
      { text: 'Spusťte klienta VPN.', sources: [1] },
      { text: 'Vyžádejte certifikát.', sources: [2] },
    ] };
    assert.deepEqual(await answer(), expected);
    assert.deepEqual(await answer({ messages: [
      { role: 'system', content: 'Odpověz pouze jedním JSON objektem' },
      ...answerRequest.messages,
    ] }), expected);
    assert.equal((await requests()).byPath['/v1/chat/completions'], 2);
  });

  const writingRequest = (task, input, native = true) => ({
    ...(native ? { response_format: { type: 'json_schema', json_schema: { name: 'WritingResult' } } } : {}),
    messages: [
      { role: 'system', content: readFileSync(new URL(`../../../src/Atlas.Server/Features/AiWriting/Prompts/${task}.system.v1.txt`, import.meta.url), 'utf8')
        + (native ? '' : '\nOdpověz pouze jedním JSON objektem') },
      { role: 'user', content: JSON.stringify(input) },
    ],
  });

  test('writing proposals match the production schemas and tags come only from supplied candidate IDs', async () => {
    const change = await answer(writingRequest('publish.change-summary', { text: 'Nově:\nOvěřte dostupnost služby z mobilního zařízení.', tags: [] }));
    assert.equal(typeof change.text, 'string');
    assert.ok(change.text.length > 0 && change.text.length <= 500 && !change.text.includes('\n'));
    const ids = ['f6ba54b2-5cb1-4e78-95bc-fd0522f4b961', '1b4aa164-1382-474e-9209-b2c80a064017'];
    const tags = await answer(writingRequest('page.tag-suggest', { text: 'Obnova služby.', tags: ids.map((id, n) => ({ Id: id, Name: `Štítek ${n}` })) }, false));
    assert.deepEqual(tags, { tagIds: ids });
    assert.deepEqual(await answer(writingRequest('publish.check', { text: 'Bez překlepů.', tags: [] })), { issues: [] });
    assert.deepEqual(await answer(writingRequest('editor.intent', { text: 'Běžný pracovní odstavec.', tags: [] })), { suggestions: [] });
  });

  test('transform preserves opaque tokens and outline uses the existing Tip callout grammar', async () => {
    const original = 'Původní text ⟦c:0123456789abcdef:1⟧ s komponentou.';
    const transform = await answer(writingRequest('editor.transform', { text: original, tags: [] }));
    assert.equal(typeof transform.text, 'string');
    assert.notEqual(transform.text, original);
    assert.deepEqual(transform.text.match(/⟦c:[0-9a-f]{16}:[1-9][0-9]{0,5}⟧/g), ['⟦c:0123456789abcdef:1⟧']);
    const outline = await answer(writingRequest('editor.outline', { text: 'Obnova služby', tags: [] }, false));
    assert.match(outline.text, /^## /);
    assert.match(outline.text, /> \[!TIP\]\n> /);
    assert.ok(!outline.text.includes(':::'));
  });

  test('published page summary returns three source-backed points without changing normal answers', async () => {
    const prompt = readFileSync(new URL('../../../src/Atlas.Server/Features/Assistant/Prompts/summary.system.v1.txt', import.meta.url), 'utf8');
    const summarized = await answer({ ...answerRequest, messages: [{ role: 'system', content: prompt }, ...answerRequest.messages] });
    assert.equal(summarized.answerable, true);
    assert.equal(summarized.sentences.length, 3);
    assert.ok(new Set(summarized.sentences.map(x => x.text)).size === 3);
    assert.ok(summarized.sentences.every(x => x.sources.length && x.sources.every(n => n === 1 || n === 2)));
    assert.equal((await answer()).sentences.length, 2);
  });
  test('answer modes model abstention and invalid citations without breaking the connection probe', async () => {
    await mode({ answer: 'unanswerable' });
    assert.deepEqual(await answer(), { answerable: false, sentences: [] });
    await mode({ answer: 'invalid-citation' });
    assert.deepEqual((await answer()).sentences[0].sources, [999]);
    assert.deepEqual(await answer({ response_format: { type: 'json_object' }, messages: [] }), { ok: true });
    assert.equal((await mode({ chat: 'error', answer: 'unknown' })).status, 400);
    assert.equal((await (await fetch(base + '/__mode')).json()).chat.mode, 'healthy');
  });

  test('a held answer observes a chat outage on release while embeddings remain available', async () => {
    await mode({ answer: 'hold' });
    let completed = false;
    const pending = post('/v1/chat/completions', answerRequest).then(response => { completed = true; return response; });
    for (let attempt = 0; attempt < 100 && !(await requests()).byPath['/v1/chat/completions']; attempt++)
      await new Promise(resolve => setTimeout(resolve, 5));
    assert.equal((await requests()).byPath['/v1/chat/completions'], 1);
    assert.equal(completed, false);
    assert.equal((await embed('VPN')).status, 200);
    await mode({ chat: 'error' });
    assert.equal((await post('/__release', {})).status, 200);
    assert.equal((await pending).status, 503);
    await post('/__reset', {});
    assert.equal((await requests()).total, 0);
    assert.equal((await answer()).answerable, true);
  });

  test('healthy, error and invalid-json modes', async () => {
    await mode({ mode: 'error' });
    const failure = await fetch(base + '/v1/models');
    assert.equal(failure.status, 503);
    assert.ok((await failure.text()).includes(UPSTREAM_DETAIL));

    await mode({ mode: 'invalid-json' });
    assert.equal((await (await embed('x')).json()).data[0].embedding.length, 768);
    assert.throws(() => JSON.parse('<html>'), SyntaxError);
    assert.equal(await (await fetch(base + '/v1/models')).text(), '<html>this is not JSON</html>');
    const chat = await (await post('/v1/chat/completions', { response_format: { type: 'json_object' } })).json();
    assert.throws(() => JSON.parse(chat.choices[0].message.content), SyntaxError);

    await mode({ mode: 'healthy' });
    assert.equal((await fetch(base + '/v1/models')).status, 200);
  });

  test('slow answers after delayMs, timeout never answers, a bad mode is refused and changes nothing', async () => {
    await mode({ mode: 'slow', delayMs: 150 });
    const started = Date.now();
    assert.equal((await fetch(base + '/v1/models')).status, 200);
    assert.ok(Date.now() - started >= 140);

    await mode({ mode: 'timeout' });
    await assert.rejects(fetch(base + '/v1/models', { signal: AbortSignal.timeout(300) }), { name: 'TimeoutError' });

    assert.equal((await mode({ mode: 'sleepy' })).status, 400);
    assert.equal((await mode({ mode: 'healthy', delayMs: -1 })).status, 400);
    assert.equal((await (await fetch(base + '/__mode')).json()).models.mode, 'timeout');
  });

  test('modes are per scope, ready for chat answers of their own', async () => {
    await mode({ embeddings: 'error', chat: 'healthy' });
    assert.equal((await embed('x')).status, 503);
    assert.equal((await post('/v1/chat/completions', {})).status, 200);
    assert.equal((await fetch(base + '/v1/models')).status, 200);
  });

  test('requests are counted without any content', async () => {
    await fetch(base + '/v1/models');
    await embed(['secret passage one', 'secret passage two']);
    await mode({ mode: 'error' });
    await fetch(base + '/v1/models');
    const answer = await requests();
    assert.equal(answer.total, 3);
    assert.deepEqual(answer.byPath, { '/v1/models': 2, '/v1/embeddings': 1 });
    assert.equal(answer.byMode.error, 1);
    assert.equal(answer.embeddingInputs, 2);
    assert.ok(!JSON.stringify(answer).includes('secret'));
    await post('/__reset', {});
    assert.equal((await requests()).total, 0);
  });

  test('the server logs method, path and status only', async () => {
    const lines = [];
    const original = console.log;
    process.env.FAKE_AI_LOG = '1';
    console.log = line => lines.push(line);
    try {
      await embed('very secret text');
      await fetch(base + '/v1/models?token=secret');
    } finally {
      console.log = original;
      process.env.FAKE_AI_LOG = '0';
    }
    assert.deepEqual(lines, ['POST /v1/embeddings 200', 'GET /v1/models 200']);
  });
});
