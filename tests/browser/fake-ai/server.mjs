// A fake OpenAI-compatible model server for the browser scenarios and the Podman e2e (phase 2 plan, A4). Node, no
// dependencies. It exists because Playwright runs against the published Atlas, where no DI fake can be injected; it also
// exercises the real OpenAI client adapter and the HTTP path. It never calls any model.
//
//   GET  /v1/models              text-embedding-bge-m3, google/gemma-4-26b-a4b
//   POST /v1/embeddings          deterministic trigram vectors, 1 024 dimensions (vectors.mjs = FakeAi in C#)
//   POST /v1/chat/completions    answers from the latest turn's sources, or { "ok": true } (the connection test)
//
// Control (same port; the test and the pod's other containers reach it on localhost):
//   POST /__mode      { mode, delayMs? }  healthy | slow | timeout | error | invalid-json, for every scope; or per scope:
//                     { embeddings: 'timeout', chat: 'healthy', models: 'timeout' } (a value is a mode name, or { mode, delayMs })
//   POST /__meaning   { rules: [{ contains, as, near? }] }  embed `as` (or a near neighbour of it) for any input containing
//                     `contains` (case-insensitive, first rule wins): a paraphrase and a passage share a vector without sharing a
//                     word, which is how the scenarios prove a match by meaning. { rules: [] } clears it.
//   POST /__reset     healthy again, rules and counters cleared
//   POST /__release   finish held answers using the current chat mode
//   POST /__mode      { answer: 'answerable' | 'unanswerable' | 'invalid-citation' | 'hold' }
//   GET  /__requests  counts only: { total, byPath, byMode, embeddingInputs }. Never any content, never in a log.
//
// Request bodies are read by `readJson` and never stored or logged.

import { createServer } from 'node:http';
import { pathToFileURL } from 'node:url';
import { DIMENSIONS, near, vector } from './vectors.mjs';

export const MODELS = ['text-embedding-bge-m3', 'google/gemma-4-26b-a4b'];
export const EMBEDDING_MODEL = 'text-embedding-bge-m3';
export const CHAT_MODEL = 'google/gemma-4-26b-a4b';
export const MODES = ['healthy', 'slow', 'timeout', 'error', 'invalid-json'];
const ANSWER_MODES = ['answerable', 'unanswerable', 'invalid-citation', 'hold'];
/** The text of an upstream failure. A scenario asserts it never reaches any Atlas screen. */
export const UPSTREAM_DETAIL = 'FAKE-UPSTREAM-DETAIL: the model server is out of order';
/** The start of PromptSchemaChatClient.SchemaInstruction in the server. */
const SCHEMA_INSTRUCTION = 'Odpověz pouze jedním JSON objektem';
const DEFAULT_SLOW_MS = 2000;
const TIMEOUT_CEILING_MS = 120_000;
const MAX_BODY = 8 * 1024 * 1024;

/** One behaviour per scope; `mode` without a scope in /__mode sets every scope. */
const scopeNames = ['models', 'embeddings', 'chat'];

export function createFakeAi() {
  const freshModes = () => Object.fromEntries(scopeNames.map(name => [name, { mode: 'healthy', delayMs: DEFAULT_SLOW_MS }]));
  const freshCounts = () => ({ total: 0, byPath: {}, byMode: {}, embeddingInputs: 0 });
  const state = { modes: freshModes(), answer: 'answerable', rules: [], counts: freshCounts() };
  const held = new Set();

  const json = (status, body) => ({ status, type: 'application/json', body: JSON.stringify(body) });

  function messageText(message) {
    return typeof message?.content === 'string' ? message.content
      : Array.isArray(message?.content) ? message.content.filter(part => part.type === 'text').map(part => part.text).join('\n') : '';
  }

  const latestInput = body => messageText(body.messages?.findLast(message => message.role === 'user'));
  const systemText = body => (body.messages ?? []).filter(message => message.role === 'system').map(messageText).join('\n');
  const isSummary = body => systemText(body).includes('Shrň poskytnuté části publikované wiki stránky');

  function answerSources(body, take = 2) {
    return [...latestInput(body).matchAll(/<zdroj\s+n="(\d+)"[^>]*>([\s\S]*?)<\/zdroj>/g)].slice(0, take)
      .map(match => ({ number: Number(match[1]), text: match[2].trim() }));
  }

  function answerResult(sources, summary) {
    if (state.answer === 'unanswerable') return { answerable: false, sentences: [] };
    let points = sources.map(source => ({ number: source.number,
      text: source.text.match(/^.*?[.!?](?=\s|$)/s)?.[0] ?? source.text }));
    if (summary) {
      points = sources.flatMap(source => (source.text.match(/[^.!?]+[.!?](?=\s|$)|[^.!?]+$/g) ?? [source.text])
        .map(text => ({ number: source.number, text }))).slice(0, 3);
      // A short fixture still yields the three UI points, all quoting facts from the same supplied passage.
      const prefixes = ['Podklad uvádí: ', 'Shrnovaná část popisuje: '];
      while (points.length < 3) points.push({ number: sources[0].number, text: prefixes[points.length - 1] + sources[0].text });
    }
    return { answerable: true, sentences: points.map(point => ({
      text: point.text.replace(/\s+/g, ' ').trim(),
      sources: [state.answer === 'invalid-citation' ? 999 : point.number],
    })) };
  }

  function writingResult(body) {
    const prompt = systemText(body);
    if (!prompt.includes('Vrať JSON') && !prompt.includes('Vrať pouze JSON')) return null;
    let input;
    try { input = JSON.parse(latestInput(body)); } catch { return null; }
    if (typeof input?.text !== 'string') return null;
    if (prompt.includes('"tagIds"')) {
      // TagDto candidates use their declared Id/Name casing in the production prompt envelope.
      const ids = (Array.isArray(input.tags) ? input.tags : []).map(tag => tag?.Id)
        .filter(id => typeof id === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(id));
      return { tagIds: [...new Set(ids)].slice(0, 5) };
    }
    if (prompt.includes('"issues"')) return { issues: [] };
    if (prompt.includes('JSON suggestions')) return { suggestions: [] };
    if (prompt.includes('Markdown osnovy')) return { text: '## Úvod\n\n> [!TIP]\n> Popište účel postupu.\n\n## Postup\n\n> [!TIP]\n> Uveďte kroky a ověření výsledku.' };
    if (prompt.includes('upravený Markdown')) return { text: 'Upravený text: ' + input.text };
    if (prompt.includes('Popis změny.')) return { text: ('Upravena stránka: ' + input.text.replace(/\s+/g, ' ').trim()).slice(0, 500) };
    return null;
  }
  /** The text an input is embedded as: itself, or what the first matching meaning rule says. */
  function embeddedText(input) {
    const lowered = input.toLowerCase();
    for (const rule of state.rules) {
      if (lowered.includes(rule.contains.toLowerCase())) return rule.near ? near(rule.as) : rule.as;
    }
    return input;
  }

  const routes = {
    'GET /v1/models': () => json(200, {
      object: 'list',
      data: MODELS.map(id => ({ id, object: 'model', owned_by: 'fake-ai' })),
    }),

    'POST /v1/embeddings': (body, mode) => {
      const inputs = typeof body.input === 'string' ? [body.input] : Array.isArray(body.input) ? body.input.filter(item => typeof item === 'string') : null;
      if (inputs === null || inputs.length === 0) return json(400, { error: { message: 'input must be a string or an array of strings', type: 'invalid_request_error' } });
      state.counts.embeddingInputs += inputs.length;
      const base64 = body.encoding_format === 'base64';
      const data = inputs.map((text, index) => {
        // invalid-json: the wrong number of dimensions, as a misconfigured model would answer; Atlas must refuse the vector.
        const values = mode === 'invalid-json' ? new Float64Array(768) : vector(embeddedText(text));
        const floats = Float32Array.from(values);
        return { object: 'embedding', index, embedding: base64 ? Buffer.from(floats.buffer).toString('base64') : Array.from(floats) };
      });
      const tokens = inputs.reduce((sum, text) => sum + Math.ceil(text.length / 4), 0);
      return json(200, { object: 'list', data, model: body.model || EMBEDDING_MODEL, usage: { prompt_tokens: tokens, total_tokens: tokens } });
    },

    'POST /v1/chat/completions': (body, mode) => {
      // Native JSON mode answers the bare object; a profile without native schemas gets the schema in the prompt (Atlas's
      // PromptSchemaChatClient) and, like a weaker model, an object wrapped in prose and a code fence.
      const wantsJson = ['json_schema', 'json_object'].includes(body.response_format?.type);
      const inPrompt = Array.isArray(body.messages) && body.messages.some(message => JSON.stringify(message?.content ?? '').includes(SCHEMA_INSTRUCTION));
      const summary = isSummary(body);
      const sources = answerSources(body, summary ? 3 : 2);
      const result = JSON.stringify(writingResult(body) ?? (sources.length ? answerResult(sources, summary) : { ok: true }));
      const content = mode === 'invalid-json' ? 'Omlouvám se, odpověď nemám.'
        : wantsJson ? result
          : inPrompt ? `Tady je výsledek:\n\`\`\`json\n${result}\n\`\`\`\nHotovo.`
            : 'připraveno';
      return json(200, {
        id: 'chatcmpl-fake', object: 'chat.completion', created: Math.floor(Date.now() / 1000), model: body.model || CHAT_MODEL,
        choices: [{ index: 0, message: { role: 'assistant', content }, finish_reason: 'stop' }],
        usage: { prompt_tokens: 8, completion_tokens: 4, total_tokens: 12 },
      });
    },
  };

  const scopeOf = path => path === '/v1/models' ? 'models' : path === '/v1/embeddings' ? 'embeddings' : 'chat';

  function assign(name, change) {
    if (change.mode !== undefined && !MODES.includes(change.mode)) throw new RangeError(`unknown mode "${change.mode}"`);
    if (change.delayMs !== undefined && !(Number.isFinite(change.delayMs) && change.delayMs >= 0 && change.delayMs <= TIMEOUT_CEILING_MS)) {
      throw new RangeError('delayMs must be 0 to 120000');
    }
    const current = state.modes[name];
    if (change.mode !== undefined) current.mode = change.mode;
    if (change.delayMs !== undefined) current.delayMs = change.delayMs;
  }

  function setModes(body) {
    // Validate everything before changing anything.
    if (body.answer !== undefined && !ANSWER_MODES.includes(body.answer)) throw new RangeError('unknown answer mode');
    const changes = [];
    if (body.mode !== undefined || body.delayMs !== undefined) {
      for (const name of scopeNames) changes.push([name, { mode: body.mode, delayMs: body.delayMs }]);
    }
    for (const name of scopeNames) {
      if (body[name] !== undefined) changes.push([name, typeof body[name] === 'string' ? { mode: body[name], delayMs: body.delayMs } : body[name]]);
    }
    const before = JSON.stringify(state.modes);
    try {
      for (const [name, change] of changes) assign(name, change);
    } catch (error) {
      state.modes = JSON.parse(before);
      throw error;
    }
    if (body.answer !== undefined) state.answer = body.answer;
    // A request held by the timeout mode is not released by a mode change: the client's timeout ends it.
  }

  function reset() {
    state.modes = freshModes();
    state.answer = 'answerable';
    state.rules = [];
    state.counts = freshCounts();
    release();
  }

  /** Releases timeout waits and held answers; answers resume using the current chat mode. */
  function release() {
    for (const done of [...held]) done();
  }

  function control(request, path, body) {
    if (request.method === 'GET' && path === '/__requests') return json(200, state.counts);
    if (request.method === 'GET' && path === '/__mode') return json(200, { ...state.modes, answer: state.answer });
    if (request.method === 'POST' && path === '/__mode') {
      try {
        setModes(body);
      } catch (error) {
        return json(400, { error: error.message });
      }
      return json(200, { ...state.modes, answer: state.answer });
    }
    if (request.method === 'POST' && path === '/__meaning') {
      const rules = body.rules;
      if (!Array.isArray(rules) || rules.some(rule => typeof rule?.contains !== 'string' || !rule.contains || typeof rule.as !== 'string')) {
        return json(400, { error: 'rules must be [{ contains, as, near? }]' });
      }
      state.rules = rules.map(rule => ({ contains: rule.contains, as: rule.as, near: rule.near === true }));
      return json(200, { rules: state.rules.length });
    }
    if (request.method === 'POST' && path === '/__reset') {
      reset();
      return json(200, { ok: true });
    }
    if (request.method === 'POST' && path === '/__release') {
      release();
      return json(200, { ok: true });
    }
    return null;
  }

  async function readJson(request) {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > MAX_BODY) throw new RangeError('body too large');
      chunks.push(chunk);
    }
    if (size === 0) return {};
    const parsed = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    return parsed !== null && typeof parsed === 'object' ? parsed : {};
  }

  function send(response, plan) {
    if (response.writableEnded || response.destroyed) return;
    response.writeHead(plan.status, { 'content-type': plan.type, 'cache-control': 'no-store' });
    response.end(plan.body);
  }

  async function handle(request, response) {
    const path = new URL(request.url, 'http://fake-ai').pathname;
    let status = 500;
    try {
      let body = {};
      if (request.method === 'POST') {
        try {
          body = await readJson(request);
        } catch {
          status = 400;
          send(response, json(400, { error: 'invalid JSON body' }));
          return;
        }
      }
      const controlled = path.startsWith('/__') ? control(request, path, body) : null;
      if (controlled) {
        status = controlled.status;
        send(response, controlled);
        return;
      }
      const route = routes[`${request.method} ${path}`];
      if (!route) {
        status = 404;
        send(response, json(404, { error: { message: 'not found', type: 'invalid_request_error' } }));
        return;
      }
      const scope = scopeOf(path);
      let { mode, delayMs } = state.modes[scope];
      state.counts.total++;
      state.counts.byPath[path] = (state.counts.byPath[path] ?? 0) + 1;
      state.counts.byMode[mode] = (state.counts.byMode[mode] ?? 0) + 1;
      if (scope === 'chat' && state.answer === 'hold' && answerSources(body).length) {
        await new Promise(resolve => {
          const done = () => {
            clearTimeout(timer);
            held.delete(done);
            response.off('close', done);
            resolve();
          };
          const timer = setTimeout(done, TIMEOUT_CEILING_MS);
          held.add(done);
          response.on('close', done);
        });
        if (response.destroyed) { status = 0; return; }
        ({ mode, delayMs } = state.modes[scope]);
      }
      if (mode === 'timeout') {
        // Never answer: the client's own timeout ends the call. The ceiling only keeps an abandoned socket from living forever.
        status = 0;
        await new Promise(resolve => {
          const done = () => { clearTimeout(timer); held.delete(done); resolve(); };
          const timer = setTimeout(done, TIMEOUT_CEILING_MS);
          held.add(done);
          response.on('close', done);
        });
        response.destroy();
        return;
      }
      if (mode === 'slow') await new Promise(resolve => setTimeout(resolve, delayMs));
      if (mode === 'error') {
        status = 503;
        send(response, json(503, { error: { message: UPSTREAM_DETAIL, type: 'server_error' } }));
        return;
      }
      if (mode === 'invalid-json' && scope === 'models') {
        status = 200;
        send(response, { status: 200, type: 'application/json', body: '<html>this is not JSON</html>' });
        return;
      }
      const plan = route(body, mode);
      status = plan.status;
      send(response, plan);
    } catch (error) {
      status = 500;
      // The type only: a message could echo request content.
      send(response, json(500, { error: { message: `fake-ai failed (${error?.name ?? 'Error'})`, type: 'server_error' } }));
    } finally {
      // Method, path and status. Never a body, a query or a header.
      if (status !== 0 && process.env.FAKE_AI_LOG !== '0') console.log(`${request.method} ${path} ${status}`);
    }
  }

  const server = createServer((request, response) => { void handle(request, response); });
  return { server, state, reset, release, DIMENSIONS };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const port = Number(process.env.PORT ?? 1234);
  const { server, release } = createFakeAi();
  server.listen(port, '0.0.0.0', () => console.log(`fake-ai listening on :${port}`));
  const stop = () => { release(); server.close(() => process.exit(0)); setTimeout(() => process.exit(0), 1000).unref(); };
  process.on('SIGTERM', stop);
  process.on('SIGINT', stop);
}
