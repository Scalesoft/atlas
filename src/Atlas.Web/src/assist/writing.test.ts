import { afterEach, expect, it, vi } from 'vitest';
import type { Editor } from '@tiptap/core';
import * as assist from '@atlas/markdown/assist';
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '@atlas/markdown/core';
import { createWritingActions } from './writing';
vi.mock('../ai/interactions', () => ({ reportAiInteraction: vi.fn() }));
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList; };
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect(); };
const controllers: MarkdownEditorController[] = [];
afterEach(() => { controllers.splice(0).forEach(c => c.destroy()); document.body.replaceChildren(); });
function mount(text = 'A paragraph that needs to be shortened before use.') {
  let editor!: Editor;
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: created => { editor = created.editor; } }) };
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] }] } as never } } });
  controllers.push(controller); const root = document.createElement('div'); document.body.append(root); controller.attachView(root,{ariaLabel:'Document'});
  return { editor, controller, range: { from: 1, to: 1 + text.length } };
}
const flush = async () => { for(let i=0;i<12;i++) await Promise.resolve(); };
function host(overrides = {}) { return { pageId: () => 'page', writing: { transformAllowed: () => true, outlineAllowed: () => true, intentAllowed: () => true, title: () => 'Topic', onApplied: vi.fn(), api: { transform: vi.fn(async () => ({text:'Shorter.',provenance:{location:'local',provider:'openai',model:'model'}})), outline: vi.fn(async () => ({text:'## Přístup\n\n> [!TIP]\n> Popište, kdo schvaluje přístup.',provenance:{location:'local',provider:'openai',model:'model'}})), intents: vi.fn(async () => ({suggestions:[]})) } }, notify:vi.fn(), warn:vi.fn(), ...overrides }; }
const selection = (range: { from: number; to: number }) => ({ kind: 'selection' as const, label: 'Výběr', ...range });
const offered = (actions: ReturnType<typeof createWritingActions>, editor: Editor, range: { from: number; to: number }) =>
  actions.selectionMenu!.actions.filter(action => actions.selectionMenu!.available(editor, range, selection(range)) && (action.available?.(editor, range, selection(range)) ?? true)).map(action => action.id);
it('does not offer writing without capability or for a secret anywhere in the selected block', () => {
  const { editor } = mount('Public words and password: Secret123!');
  const actions = createWritingActions(assist, host());
  expect(offered(actions, editor, { from: 1, to: 6 })).toEqual([]);
  const clean = mount();
  const denied = host(); denied.writing.transformAllowed = () => false;
  expect(offered(createWritingActions(assist,denied), clean.editor, clean.range)).toEqual([]);
});
it('offers one set of intents (P11): the reading ones while the assistant can answer, the writing ones with the capability, and „Doplnit“', async () => {
  const { editor, range } = mount();
  const ask = { available: vi.fn(() => true), run: vi.fn() };
  const actions = createWritingActions(assist, host({ ask }));
  expect(offered(actions, editor, range)).toEqual(['ask','explain','summarize','proofread','shorten','simplify','formal','informal','bullets','steps','table','custom','translate-en','translate-cs','complete']);
  expect(actions.selectionMenu!.actions.find(action => action.id === 'complete')!.label).toBe('Doplnit');
  await actions.selectionMenu!.actions.find(action => action.id === 'explain')!.run({ editor, range, signal: new AbortController().signal, target: selection(range) });
  expect(ask.run).toHaveBeenCalledWith('explain', 'A paragraph that needs to be shortened before use.', 'Výběr');
  ask.available.mockReturnValue(false);
  const denied = host({ ask }); denied.writing.transformAllowed = () => false;
  expect(offered(createWritingActions(assist, denied), editor, range)).toEqual([]);
  // Over the one limit (aiIntents.ts) nothing is offered, neither reading nor writing.
  const long = mount('slovo '.repeat(400).trim());
  ask.available.mockReturnValue(true);
  expect(offered(createWritingActions(assist, host({ ask })), long.editor, long.range)).toEqual([]);
});
it('„Doplnit“ asks the custom transform with its own instruction, no prompt, as a proposal of one undo step', async () => {
  const { editor, controller, range } = mount();
  const context = host();
  const attempt = createWritingActions(assist,context).selectionMenu!.actions.find(a=>a.id==='complete')!.run({editor,range,signal:new AbortController().signal});
  await flush();
  expect(context.writing.api.transform).toHaveBeenCalledWith(expect.objectContaining({ action: 'custom', instruction: expect.stringContaining('Doplň') }), expect.any(AbortSignal));
  expect(document.querySelector('.md-assist-proposal')!.textContent).toContain('Doplnit');
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Použít')!.click();
  await attempt;
  expect(editor.state.doc.textContent).toBe('Shorter.');
  controller.dispatch({type:'undo'}); expect(editor.state.doc.textContent).toContain('paragraph');
});
it('prepares a preview and marks AI-assisted only after a successful one-undo apply', async () => {
  const { editor, controller, range } = mount();
  const context = host();
  const actions = createWritingActions(assist,context);
  const attempt = actions.selectionMenu!.actions.find(a=>a.id==='shorten')!.run({editor,range,signal:new AbortController().signal});
  await flush();
  expect(context.writing.api.transform).toHaveBeenCalledOnce();
  expect(context.writing.onApplied).not.toHaveBeenCalled();
  expect(editor.state.doc.textContent).toContain('paragraph');
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Použít')!.click();
  await attempt;
  expect(context.writing.onApplied).toHaveBeenCalledOnce();
  expect(editor.state.doc.textContent).toBe('Shorter.');
  controller.dispatch({type:'undo'}); expect(editor.state.doc.textContent).toContain('paragraph');
});

it('confirms an applied rewrite with „Zpět“ that reverts exactly that step (P6), not later typing', async () => {
  const { editor, range } = mount();
  const context = host();
  const attempt = createWritingActions(assist,context).selectionMenu!.actions.find(a=>a.id==='bullets')!.run({editor,range,signal:new AbortController().signal});
  await flush();
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Použít')!.click();
  await attempt;
  expect(context.notify).toHaveBeenCalledWith('Úprava použita', expect.any(Function));
  const undo = context.notify.mock.calls[0]![1] as () => void;
  editor.commands.insertContentAt(editor.state.doc.content.size - 1, ' typed');
  undo(); expect(editor.state.doc.textContent).toContain('typed');
  editor.commands.undo(); undo(); expect(editor.state.doc.textContent).toContain('paragraph');
});

it('never applies a ready preview after its capability was revoked', async () => {
  const { editor, range } = mount(); const context = host(); let allowed = true;
  context.writing.transformAllowed = () => allowed;
  const attempt = createWritingActions(assist,context).selectionMenu!.actions.find(a=>a.id==='shorten')!.run({editor,range,signal:new AbortController().signal});
  await flush(); allowed = false;
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Použít')!.click();
  expect(editor.state.doc.textContent).toContain('paragraph');
  expect(context.writing.onApplied).not.toHaveBeenCalled();
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Zahodit')?.click(); await attempt;
});

it('offers an outline only for an empty page, asks for its topic, and applies the existing Tip codec in one undo', async () => {
  const { editor, controller } = mount(''); const context = host(); const actions = createWritingActions(assist,context);
  expect(actions.emptyAction!.available(editor)).toBe(true);
  const attempt = actions.emptyAction!.run({editor,signal:new AbortController().signal});
  await flush();
  const field = document.querySelector<HTMLTextAreaElement>('textarea')!; field.value = 'Přístup do serverovny';
  field.form!.dispatchEvent(new Event('submit',{cancelable:true,bubbles:true})); await flush();
  expect(context.writing.api.outline).toHaveBeenCalledWith({pageId:'page',topic:'Přístup do serverovny'},expect.any(AbortSignal));
  expect(editor.state.doc.textContent).toBe('');
  [...document.querySelectorAll<HTMLButtonElement>('.md-assist-proposal button')].find(b=>b.textContent==='Použít')!.click(); await attempt;
  expect(editor.state.doc.firstChild!.type.name).toBe('heading');
  expect(editor.state.doc.child(1).type.name).toBe('callout');
  expect(context.writing.onApplied).toHaveBeenCalledOnce();
  expect(actions.emptyAction!.available(editor)).toBe(false);
  controller.dispatch({type:'undo'}); expect(editor.state.doc.textContent).toBe('');
});
