import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { computed, defineComponent, h, onMounted, ref } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import type { MarkdownEditorState } from '@atlas/markdown/core';
import DocumentEditor from './DocumentEditor.vue';
import { emptyDocument } from '../document';

const transport = vi.hoisted(() => ({ fail: (_message: string, _pendingChanges = 0) => {} }));
vi.mock('../collaboration', async original => ({
  ...(await original<typeof import('../collaboration')>()),
  joinPageCollaboration: vi.fn(async () => ({ lease: { generation: 'g', currentVersion: 1 }, adapter: {} })),
}));
// The public editor contract only emits `created` after reaching ready. A failed
// connection must be observed through its exposed controller, before that event.
vi.mock('@atlas/markdown/vue', () => ({
  MarkdownSourcePanel: defineComponent({ render: () => null }),
  MarkdownDraftConfirmation: defineComponent({ render: () => null }),
  useMarkdownSourceDraft: () => ({ dirty: computed(() => false), busy: ref(false), apply: async () => true, discard() {}, close() {} }),
  MarkdownEditor: defineComponent({
  props: ['modelValue', 'collaboration', 'readonly'], emits: ['created'],
  setup(props, { expose, emit }) {
    let state = { lifecycle: props.collaboration ? 'starting' : 'ready', binding: props.collaboration ? 'collaborative' : 'local', connection: props.collaboration ? 'connecting' : 'local', error: null, editable: false } as MarkdownEditorState;
    const listeners = new Set<(state: MarkdownEditorState) => void>();
    const controller = { get state() { return state; }, getValue: () => props.modelValue,
      subscribe: (listener: (state: MarkdownEditorState) => void) => { listeners.add(listener); return () => listeners.delete(listener); } };
    expose({ controller });
    if (props.collaboration) transport.fail = (message, pendingChanges = 0) => {
      state = { ...state, pendingChanges, lifecycle: 'failed', connection: 'failed', error: { kind: 'collaboration', message, recoverable: true } };
      for (const listener of [...listeners]) listener(state);
    };
    onMounted(() => { if (!props.collaboration) emit('created', controller); });
    return () => h('div', { 'data-editor-content': '', 'aria-readonly': props.readonly }, props.modelValue ? 'Uložený obsah' : '');
  },
}) }));
const reload = vi.fn();
beforeEach(() => { sessionStorage.clear(); vi.stubGlobal('location', { ...window.location, reload }); });
afterEach(() => { vi.clearAllMocks(); vi.unstubAllGlobals(); });
const props = { value: emptyDocument(), readonly: true, dark: false, accent: 'blue', spaceId: 's', pageId: 'p', identity: { id: 'u', displayName: 'Test' }, collaborationAllowed: true } as const;
async function failed(message: string, pendingChanges = 0) {
  const wrapper = mount(DocumentEditor, { props });
  await flushPromises();
  await wrapper.setProps({ readonly: false }); await flushPromises();
  transport.fail(message, pendingChanges); await flushPromises();
  return wrapper;
}
// A tab open over a deploy: the new server refuses its schema for good. It loads the new release by itself, unless typing would be lost.
it('reloads a tab of the previous release by itself, once, and never over changes that did not reach the server', async () => {
  const mismatch = 'HubException: [SCHEMA_MISMATCH] Different schema.';
  (await failed('Připojení selhalo.')).unmount();
  expect(reload).not.toHaveBeenCalled();
  (await failed(mismatch, 2)).unmount();
  expect(reload).not.toHaveBeenCalled();
  (await failed(mismatch)).unmount();
  expect(reload).toHaveBeenCalledTimes(1);
  // A reload that did not help (a cached shell) ends on the notice and its button: no loop.
  const again = await failed(mismatch);
  expect(reload).toHaveBeenCalledTimes(1);
  expect(again.get('[role=alert] button').text()).toBe('Obnovit stránku');
  again.unmount();
});
it.each([
  ['Připojení selhalo.', 'Připojení selhalo.', 'Připojit znovu'],
  // A tab of the previous release after a deploy: reconnecting cannot help, reloading does.
  ['HubException: [SCHEMA_MISMATCH] Different schema.', 'novější verzi editoru', 'Obnovit stránku'],
])('retains the readable document and explains a failure before ready: %s', async (message, explanation, action) => {
  const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: true, dark: false, accent: 'blue', spaceId: 's', pageId: 'p', identity: { id: 'u', displayName: 'Test' }, collaborationAllowed: true } });
  try {
    await flushPromises();
    await wrapper.setProps({ readonly: false }); await flushPromises();
    transport.fail(message); await flushPromises();
    expect(wrapper.get('[role=alert]').text()).toContain(explanation);
    // The saved content stays readable in the local read-only editor; the failed shared one is gone.
    expect(wrapper.findAll('[data-editor-content]')).toHaveLength(1);
    expect(wrapper.get('[data-editor-content]').attributes('aria-readonly')).toBe('true');
    expect(wrapper.emitted('collaborationStatus')!.at(-1)![0]).toMatchObject({ connection: 'failed', canSave: false });
    expect(wrapper.get('[role=alert] button').text()).toBe(action);
  } finally { wrapper.unmount(); }
});
