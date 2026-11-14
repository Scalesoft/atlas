// @vitest-environment node
import { describe, expect, it, vi } from 'vitest';
import type { MarkdownAssistSuggestion } from '@atlas/markdown/assist';
import { createModelIntentSource } from './modelIntents';
const text = 'The building entrance requires contacting a colleague before arrival.';
const block = (value = text) => ({ pos: 0, text: value, trigger: 'idle' as const, node: { type: { name: 'paragraph' } }, rangeOf: (start: number, end: number) => ({ from: start + 1, to: end + 1 }) }) as never;
describe('model intents', () => {
  it('preserves the resolved suggestion preference guard after a model hint', async () => {
    let allowed = true;
    const apply = vi.fn(() => true);
    const source = createModelIntentSource({
      enabled: () => true, pageId: () => 'page',
      request: async () => ({ suggestions: [{ intent: 'contact.inline', spanStart: 10, spanEnd: 17, query: 'Person', confidence: .8 }] }),
      resolve: async () => [{ id: 'contact', kind: 'contact.inline', title: 'Contact', icon: 'user', available: () => allowed, apply }],
    });
    const suggestion = (await source.suggest(block(), { signal: new AbortController().signal }))[0] as MarkdownAssistSuggestion;
    expect(suggestion).toBeDefined();
    allowed = false;
    expect(suggestion.available?.()).toBe(false);
    expect(await suggestion.apply({} as never)).toBe(false);
    expect(apply).not.toHaveBeenCalled();
  });
  it('keeps automatic capability/preference/secret guards and rate limiting ahead of every provider call', async () => {
    let enabled = false, now = 0;
    const request = vi.fn(async () => ({ suggestions: [] }));
    const source = createModelIntentSource({ enabled: () => enabled, pageId: () => 'page', now: () => now, request, resolve: async () => [] });
    const signal = new AbortController().signal;
    await source.suggest(block(), { signal }); expect(request).not.toHaveBeenCalled();
    enabled = true; await source.suggest(block('Password: Secret123! please use this password to log in.'), { signal }); expect(request).not.toHaveBeenCalled();
    await source.suggest(block(), { signal }); await source.suggest(block(), { signal }); expect(request).toHaveBeenCalledTimes(1);
    now = 3000; await source.suggest(block(), { signal }); expect(request).toHaveBeenCalledTimes(2);
  });
  it('accepts only allowed validated intent hints and preserves rule precedence', async () => {
    const resolve = vi.fn(async (_hit: import('./rules').RuleHit) => []);
    const hints = [
      { intent: 'contact.inline', spanStart: 10, spanEnd: 17, query: 'Person', confidence: .8 },
      { intent: 'youtrack.issue', spanStart: 1, spanEnd: 5, query: 'ATL-1', confidence: 1 },
      { intent: 'contact.card', spanStart: 0, spanEnd: 5000, query: 'Person', confidence: .9 },
      { intent: 'secret.reference', spanStart: 1, spanEnd: 5, query: 'vault', confidence: .1 },
    ];
    const source = createModelIntentSource({ enabled: () => true, pageId: () => 'page', request: async () => ({ suggestions: hints }), resolve });
    await source.suggest(block(), { signal: new AbortController().signal });
    expect(resolve).toHaveBeenCalledOnce();
    expect(resolve.mock.calls[0]![0]).toMatchObject({ intent: 'contact.inline', span: { start: 10, end: 17 }, query: 'Person' });
  });
});
