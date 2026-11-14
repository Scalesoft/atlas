import type { Editor } from '@tiptap/core';
import type { Transaction } from '@tiptap/pm/state';
import type { MarkdownAssistApplyContext, MarkdownAssistBlock, MarkdownAssistSource, MarkdownAssistSuggestion } from '@atlas/markdown/assist';
import { detectIntents, detectSecrets, intentDefinition, isIntentId, type RuleHit } from './rules';
import { spansOverlap } from './rules/span';
import { AI_TEXT_LIMIT } from './aiIntents';
export interface ModelIntentRequest { readonly pageId: string; readonly text: string; readonly catalogVersion: 1 }
/** Per-editor model hints only; all identities and actual writes stay with the existing authorized resolvers. */
export function createModelIntentSource(options: {
  enabled(): boolean; pageId(): string | undefined; now?(): number;
  editor?: () => Editor | undefined;
  safe?(block: MarkdownAssistBlock): boolean;
  request(input: ModelIntentRequest, signal: AbortSignal): Promise<unknown>;
  resolve(hit: RuleHit, block: MarkdownAssistBlock, signal: AbortSignal): Promise<readonly MarkdownAssistSuggestion[]>;
}): MarkdownAssistSource {
  let last = -Infinity;
  return { id: 'atlas-model-intents', async suggest(block, { signal }) {
    const pageId = options.pageId(), text = block.text;
    if (signal.aborted || !options.enabled() || !pageId || !['idle','request'].includes(block.trigger) || block.node.type.name !== 'paragraph' || text.length < 40 || text.length > AI_TEXT_LIMIT || text.includes('\uFFFC') || /[⟦⟧]/u.test(text) || detectSecrets(text).length || options.safe?.(block) === false) return [];
    const now = (options.now ?? Date.now)(); if (now - last < 3000) return []; last = now;
    const editor = options.editor?.(), pending = new AbortController();
    let pos = block.pos;
    const moved = ({transaction}: {transaction: Transaction}) => {
      pos = transaction.mapping.map(pos,1);
      const selection = editor!.state.selection;
      if (!options.enabled() || !editor!.isEditable || !selection.$from.parent.isTextblock || selection.$from.before() !== pos || !editor!.state.doc.nodeAt(pos)?.eq(block.node)) pending.abort();
    };
    editor?.on('transaction',moved);
    const requestSignal = AbortSignal.any([signal,pending.signal]);
    try {
      const body = await options.request({ pageId, text, catalogVersion: 1 }, requestSignal);
      if (requestSignal.aborted || !options.enabled() || !body || typeof body !== 'object') return [];
      const hints = (body as { suggestions?: unknown }).suggestions; if (!Array.isArray(hints)) return [];
      const rules = detectIntents(text);
      const result: MarkdownAssistSuggestion[] = [];
      for (const value of hints.slice(0, 8)) {
        if (!value || typeof value !== 'object') continue;
        const hint = value as Record<string, unknown>;
        if (typeof hint.intent !== 'string' || !isIntentId(hint.intent) || !intentDefinition(hint.intent).modelAllowed || typeof hint.confidence !== 'number' || !Number.isFinite(hint.confidence) || hint.confidence < .5 || hint.confidence > 1 || typeof hint.spanStart !== 'number' || !Number.isInteger(hint.spanStart) || typeof hint.spanEnd !== 'number' || !Number.isInteger(hint.spanEnd) || hint.spanStart < 0 || hint.spanEnd > text.length || hint.spanStart >= hint.spanEnd || typeof hint.query !== 'string' || hint.query.length > 120) continue;
        const span = { start: hint.spanStart, end: hint.spanEnd };
        if (rules.some(hit => spansOverlap(hit.span, span))) continue;
        const hit: RuleHit = { intent: hint.intent, span, query: hint.query.trim() };
        const suggestions = await options.resolve(hit, block, requestSignal);
        if (requestSignal.aborted || !options.enabled()) return [];
        result.push(...suggestions.map(suggestion => {
          const available = () => options.enabled() && suggestion.available?.() !== false;
          return { ...suggestion, ai: true, available, apply: (context: MarkdownAssistApplyContext) => available() ? suggestion.apply(context) : false };
        }));
      }
      return result;
    } catch { return []; } finally { editor?.off('transaction',moved); }
  } };
}
