import type { Editor } from '@tiptap/core';
import type { AssistModule, AssistPluginOptions, MarkdownAssistRange, MarkdownAssistTarget, MarkdownAssistTargetKind } from '@atlas/markdown/assist';
import { openMarkdownContextPopover, parseMarkdownContent } from '@atlas/markdown/core';
import type { Node as PMNode } from '@tiptap/pm/model';
import { watch } from 'vue';
import { mountAiGlyph } from '../components/ai/mountAiGlyph';
import { reportAiInteraction } from '../ai/interactions';
import t from '../i18n/assist';
import { AI_TEXT_LIMIT, aiIntentAvailable, aiIntentLabels, aiText, readingIntents, type AiIntent } from './aiIntents';
import { detectSecrets } from './rules';
import type { AtlasAssistContext } from './types';
import { writingApi, type TransformAction } from './writingApi';
type WritingContext = Pick<AtlasAssistContext, 'pageId' | 'writing' | 'ask' | 'notify' | 'warn' | 'reducedMotion' | 'reportInteraction'>;
// Reading intents act on anything one points at; the writing ones on text they can rewrite in place (an element is opaque to them).
const everywhere: readonly MarkdownAssistTargetKind[] = ['selection', 'element', 'section', 'block', 'page'];
const textual: readonly MarkdownAssistTargetKind[] = ['selection', 'section', 'block'];
function secretBlock(editor: Editor, range: MarkdownAssistRange) {
  let secret = false;
  editor.state.doc.nodesBetween(range.from, range.to, node => {
    if (/secret|conceal/i.test(node.type.name)) secret = true;
    if (node.isTextblock && detectSecrets(node.textBetween(0,node.content.size,'\n', leaf => leaf.type.name === 'hardBreak' ? '\n' : '\uFFFC')).length) secret = true;
  });
  return secret;
}
function empty(editor: Editor): boolean {
  let headings = 0, invalid = false;
  editor.state.doc.forEach(node => {
    if (node.type.name === 'heading' && ++headings === 1 && !detectSecrets(node.textContent).length) return;
    if (node.type.name === 'paragraph' && (!node.textContent.trim() || node.textContent.trim() === 'Prázdná')) return;
    invalid = true;
  });
  return !invalid;
}
function prompt(editor: Editor, label: string, initial: string, signal: AbortSignal): Promise<string|null> {
  return new Promise(resolve => {
    let resolved = false;
    const done = (value: string|null) => { if (!resolved) { resolved = true; resolve(value); } };
    if (!editor.isEditable || signal.aborted) { done(null); return; }
    openMarkdownContextPopover({ editor, anchor: editor.view.dom, title: label, signal, variant:'panel', trackSelection:false, render(container, close) {
      const doc = container.ownerDocument, form = doc.createElement('form'), fieldLabel = doc.createElement('label'), field = doc.createElement('textarea');
      fieldLabel.textContent = label; field.maxLength = 500; field.value = initial; field.required = true; field.setAttribute('aria-label',label); field.rows = 3; field.className = 'md-input'; fieldLabel.append(field);
      const submit = doc.createElement('button'); submit.type = 'submit'; submit.className = 'md-btn-primary'; submit.textContent = t.writing.prepare;
      const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.className = 'md-btn'; cancel.textContent = t.writing.cancel; cancel.onclick = close;
      form.onsubmit = event => { event.preventDefault(); field.setCustomValidity(detectSecrets(field.value).length ? t.writing.secretInInstruction : ''); if (!field.reportValidity()) return; if (field.value.trim() && field.value.length <= 500) { done(field.value.trim()); close(); } };
      form.append(fieldLabel,submit,cancel); container.append(form);
      return () => done(null);
    } });
  });
}
// „Doplnit“ (P11) is the custom transform with the author's text as its start.
const completeInstruction = 'Doplň a dokonči tento text ve stejném stylu a jazyce; to, co už je napsané, zachovej.';
// The writing actions in the menu, grouped by their intent (the menu's filter matches the intent's word).
const transforms = (): readonly { readonly id: string; readonly action: TransformAction; readonly intent: AiIntent; readonly label: string; readonly instruction?: string }[] => [
  ...(['proofread','shorten','simplify','formal','informal','bullets','steps','table','custom'] as const).map(action => ({ id: action, action, intent: 'rewrite' as const, label: t.writing.labels[action]! })),
  ...(['translate-en','translate-cs'] as const).map(action => ({ id: action, action, intent: 'translate' as const, label: t.writing.labels[action]! })),
  { id: 'complete', action: 'custom', intent: 'complete', label: aiIntentLabels.complete, instruction: completeInstruction },
];
/** „Zpět“ of an applied step (one closed history step): undone while nothing changed since; after that Ctrl+Z decides. */
export function undoApplied(editor: Editor): () => void {
  const after = editor.state.doc;
  return () => { if (!editor.isDestroyed && editor.isEditable && editor.state.doc.eq(after)) editor.commands.undo(); };
}
/** One answer per document and range: the menu asks for every action of every target, the text is read once. */
const memo = new WeakMap<object, Map<string, unknown>>();
function once<T>(editor: Editor, key: string, read: () => T): T {
  let byRange = memo.get(editor.state.doc);
  if (!byRange) memo.set(editor.state.doc, byRange = new Map());
  if (!byRange.has(key)) byRange.set(key, read());
  return byRange.get(key) as T;
}
/**
 * P11 in the page editor: the intents for every target the assist module finds (the selection or element, the heading's section or
 * the block, the page) in its „Co s tím jde dělat“ and in the selection toolbar's „AI“. Zeptat se, Vysvětlit and Shrnout hand the
 * target's Markdown to the assistant (`context.ask`); Přepsat, Přeložit and Doplnit open the module's only proposal system
 * (Použít / Zahodit, one undo step). Atlas supplies permissions, requests and a successful-apply audit fact.
 */
export function createWritingActions(assist: AssistModule, context: WritingContext): Pick<AssistPluginOptions,'selectionMenu'|'emptyAction'> {
  if ((!context.writing && !context.ask) || !assist.projectMarkdownAssistSelection) return {};
  const host = context.writing, api = host?.api ?? writingApi, report = context.reportInteraction ?? reportAiInteraction;
  const transformAllowed = () => !!host?.transformAllowed();
  // The one availability rule (aiIntents.ts) with this editor's facts.
  const allowed = (intent: AiIntent, editor: Editor) => aiIntentAvailable(intent, { editing: editor.isEditable, canAnswer: !!context.ask?.available(), transform: transformAllowed() });
  // Its plain text over the limit means its Markdown is too: checked before any serialization, so a long page costs the menu nothing (P17).
  const safe = (editor: Editor, range: MarkdownAssistRange) => editor.isEditable && !!context.pageId() && range.from >= 0 && range.to <= editor.state.doc.content.size && range.from < range.to
    && once(editor, `size:${range.from}:${range.to}`, () => editor.state.doc.textBetween(range.from, range.to, '\n').length <= AI_TEXT_LIMIT);
  const projection = (editor: Editor, range: MarkdownAssistRange) => {
    if (editor.isDestroyed || !safe(editor, range) || !transformAllowed() || secretBlock(editor, range)) return null;
    const value = assist.projectMarkdownAssistSelection(editor, range);
    if (value && (!value.text.trim() || value.text.length > AI_TEXT_LIMIT)) { value.dispose(); return null; }
    return value;
  };
  const writable = (editor: Editor, range: MarkdownAssistRange) => once(editor, `write:${range.from}:${range.to}`, () => { const value = projection(editor, range); value?.dispose(); return !!value; });
  const readText = (editor: Editor, range: MarkdownAssistRange) => once(editor, `read:${range.from}:${range.to}`, () =>
    safe(editor, range) && !secretBlock(editor, range) ? aiText(assist.readMarkdownAssistText?.(editor, range)) : null);
  const run = async (action: TransformAction, editor: Editor, range: MarkdownAssistRange, signal: AbortSignal, fixed?: string, title = t.writing.labels[action]!) => {
    if (!host) return;
    const initial = projection(editor,range); if (!initial) return;
    const pageId = context.pageId();
    const available = () => !editor.isDestroyed && editor.isEditable && host.transformAllowed() && context.pageId() === pageId;
    const pending = new AbortController(), stop = watch(available, ok => { if (!ok) pending.abort(); }, {flush:'sync'});
    signal = AbortSignal.any([signal,pending.signal]);
    let instruction: string|undefined = fixed;
    try {
      if (action === 'custom' && !instruction) { instruction = (await prompt(editor,t.writing.customPrompt,'',signal)) ?? undefined; if (!instruction) return; }
      const mapped = initial.range; if (!mapped || signal.aborted) return;
      let shown = false;
      const result = await assist.openMarkdownProposalPanel({ editor,range:mapped,title,before:initial.before,allowEdit:true,ai:true,signal,
        reducedMotion: context.reducedMotion, available,
        async load(requestSignal,current) {
          const fresh = projection(editor,current); if (!fresh) throw new Error('Unsafe or unavailable selection');
          try {
            const pageId = context.pageId()!;
            const value = await api.transform({pageId,text:fresh.text,action,...(instruction ? {instruction} : {})},requestSignal);
            if (requestSignal.aborted || context.pageId() !== pageId || !host.transformAllowed()) throw new Error('Unavailable');
            const after = fresh.restore(value.text);
            shown = true; report('editor.transform',action,'shown');
            return {after,before:fresh.before};
          } finally { fresh.dispose(); }
        },
        onFeedback: vote => report('editor.transform',action,vote),
      });
      if (result !== 'cancelled') { host.onApplied(); report('editor.transform',action,'applied'); context.notify(t.writing.applied, undoApplied(editor)); }
      else if (shown) report('editor.transform',action,'dismissed');
    } finally { stop(); initial.dispose(); }
  };
  return {
    selectionMenu: { label:'AI',renderIcon: mountAiGlyph,available: (editor,range) => safe(editor,range), actions: [
      ...readingIntents.map(intent => ({ id: intent, label: aiIntentLabels[intent], targets: everywhere,
        available: (editor: Editor, range: MarkdownAssistRange) => allowed(intent, editor) && !!readText(editor, range),
        run: ({ editor, range, target }: { editor: Editor; range: MarkdownAssistRange; target?: MarkdownAssistTarget }) => {
          const text = allowed(intent, editor) ? readText(editor, range) : null;
          if (text) void context.ask!.run(intent, text, target?.label ?? 'Výběr');
        } })),
      ...transforms().map(item => ({ id: item.id, label: item.label, intent: aiIntentLabels[item.intent], targets: textual,
        available: (editor: Editor, range: MarkdownAssistRange) => allowed(item.intent, editor) && writable(editor, range),
        run: ({ editor, range, signal }: { editor: Editor; range: MarkdownAssistRange; signal: AbortSignal }) => run(item.action,editor,range,signal,item.instruction,item.label) })),
    ] },
    ...(host ? { emptyAction: { label:t.writing.outline,renderIcon: mountAiGlyph, available: editor => editor.isEditable && !!context.pageId() && host.outlineAllowed() && empty(editor),
      async run({editor,signal}) {
        if (!host.outlineAllowed() || !empty(editor)) return;
        const initial = assist.projectMarkdownAssistSelection(editor,{from:0,to:editor.state.doc.content.size}); if (!initial) return;
        const pageId = context.pageId();
        const available = () => !editor.isDestroyed && editor.isEditable && host.outlineAllowed() && context.pageId() === pageId;
        const pending = new AbortController(), stop = watch(available, ok => { if (!ok) pending.abort(); }, {flush:'sync'});
        signal = AbortSignal.any([signal,pending.signal]);
        try {
          const topic = await prompt(editor,t.writing.outlinePrompt,host.title(),signal);
          if (!topic || !initial.range || !host.outlineAllowed() || !empty(editor)) return;
          let shown = false;
          const result = await assist.openMarkdownProposalPanel({editor,range:initial.range,title:t.writing.outline,before:initial.before,allowEdit:true,ai:true,signal,available,reducedMotion:context.reducedMotion,
            async load(requestSignal,current) {
              if (!empty(editor) || !host.outlineAllowed()) throw new Error('Page is no longer empty');
              const snapshot = assist.projectMarkdownAssistSelection(editor,current); if (!snapshot) throw new Error('Changed');
              try {
                const pageId = context.pageId()!;
                const value = await api.outline({pageId,topic},requestSignal);
                if (requestSignal.aborted || !snapshot.range || context.pageId() !== pageId || !host.outlineAllowed()) throw new Error('Changed');
                const after = parseMarkdownContent(editor,value.text); validateOutline(after);
                shown = true; report('editor.outline','outline','shown'); return {after:after.toJSON(),before:snapshot.before};
              } finally { snapshot.dispose(); }
            },onFeedback:vote=>report('editor.outline','outline',vote),
          });
          if (result !== 'cancelled') { host.onApplied(); report('editor.outline','outline','applied'); context.notify(t.writing.outlineApplied, undoApplied(editor)); }
          else if (shown) report('editor.outline','outline','dismissed');
        } finally { stop(); initial.dispose(); }
      },
    } } : {}),
  };
}
function validateOutline(document: PMNode) {
  let heading = false, tip = false;
  if (!document.childCount) throw new Error('Empty outline');
  document.forEach(node => {
    if (node.type.name === 'heading' && [2,3].includes(node.attrs.level) && node.textContent.trim()) { heading = true; tip = false; }
    else if (heading && !tip && node.type.name === 'callout' && node.attrs.variant === 'tip' && node.textContent.trim().length > 0 && node.textContent.trim().length <= 300) { tip = true; }
    else throw new Error('Invalid outline structure');
    node.descendants(child => { if (!['text','paragraph'].includes(child.type.name) || child.marks.length) throw new Error('Invalid outline content'); });
  });
}
