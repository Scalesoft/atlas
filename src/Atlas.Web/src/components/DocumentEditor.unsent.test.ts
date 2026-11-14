import { afterEach, expect, it, vi } from 'vitest';
import { computed, defineComponent, h, onMounted, ref } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import type { MarkdownEditorState } from '@atlas/markdown/core';
import DocumentEditor from './DocumentEditor.vue';
import { emptyDocument } from '../document';

const doc = (...texts: string[]) => ({ type: 'doc', content: texts.map(text => ({ type: 'paragraph', content: [{ type: 'text', text }] })) });
type Doc = ReturnType<typeof doc>;
// Each shared session: the document its editor starts from, its editor (setContent recorded) and a way to publish its state.
const sessions = vi.hoisted(() => ({
  starts: [] as Doc[],
  live: [] as { editor: { state: { doc: { toJSON(): Doc } }; isDestroyed: boolean; isEditable: boolean; commands: { setContent: ReturnType<typeof vi.fn> } };
    publish(patch: Partial<MarkdownEditorState>): void; type(next: Doc): void }[],
}));
vi.mock('../collaboration', async original => ({
  ...(await original<typeof import('../collaboration')>()),
  joinPageCollaboration: vi.fn(async () => ({ lease: { generation: 'g', currentVersion: 1 }, adapter: {} })),
}));
vi.mock('@atlas/markdown/vue', () => ({
  MarkdownSourcePanel: defineComponent({ render: () => null }),
  MarkdownDraftConfirmation: defineComponent({ render: () => null }),
  useMarkdownSourceDraft: () => ({ dirty: computed(() => false), busy: ref(false), apply: async () => true, discard() {}, close() {} }),
  MarkdownEditor: defineComponent({
    props: ['modelValue', 'collaboration', 'readonly'], emits: ['created'],
    setup(props, { expose, emit }) {
      const shared = !!props.collaboration;
      let current = shared ? sessions.starts.shift()! : undefined;
      // A ProseMirror document is immutable: typing makes a new one.
      const node = (value?: Doc) => ({ toJSON: () => value! });
      const editor = { state: { doc: node(current) }, isDestroyed: false, isEditable: true, commands: { setContent: vi.fn() } };
      let state = { lifecycle: 'ready', binding: shared ? 'collaborative' : 'local', connection: shared ? 'online' : 'local', error: null,
        editable: shared, pendingChanges: 0 } as unknown as MarkdownEditorState;
      const listeners = new Set<(state: MarkdownEditorState) => void>();
      const controller = { get state() { return state; }, query: () => ({ enabled: false }), getValue: () => (shared ? { ...emptyDocument(), document: current } : props.modelValue),
        subscribe: (listener: (state: MarkdownEditorState) => void) => { listeners.add(listener); return () => listeners.delete(listener); } };
      expose({ controller });
      if (shared) sessions.live.push({ editor,
        publish(patch) { state = { ...state, ...patch }; for (const listener of [...listeners]) listener(state); },
        type(next) { current = next; editor.state.doc = node(next); } });
      onMounted(() => emit('created', controller));
      return () => h('div', shared ? { class: 'ProseMirror', ref: (element: unknown) => { if (element) (element as { editor?: unknown }).editor = editor; } } : {});
    },
  }),
}));
afterEach(() => { vi.clearAllMocks(); sessions.live.length = 0; sessions.starts.length = 0; });

it('joins a superseded session again by itself and puts back what was typed but never sent', async () => {
  // The first session holds A B; the page then starts again from a revision whose B an agent rewrote.
  sessions.starts.push(doc('A', 'B'), doc('A', 'B by AI'));
  const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: false, dark: false, accent: 'blue', spaceId: 's', pageId: 'p',
    identity: { id: 'u', displayName: 'Test' }, collaborationAllowed: true } });
  try {
    await flushPromises();
    const first = sessions.live[0]!;
    first.type(doc('A typed', 'B'));
    first.publish({ pendingChanges: 1 });
    first.publish({ lifecycle: 'failed', connection: 'failed', editable: false,
      error: { kind: 'collaboration', message: '[SUPERSEDED] The document was replaced; join it again.', recoverable: false } });
    await flushPromises();
    // No refusal and no „Připojit znovu“: the session that was online opens the new generation quietly, and the host is told.
    expect(wrapper.find('[role=alert]').exists()).toBe(false);
    expect(wrapper.emitted('reopened')).toHaveLength(1);
    expect(sessions.live[1]!.editor.commands.setContent).toHaveBeenCalledExactlyOnceWith(doc('A typed', 'B by AI'), { emitUpdate: true });
  } finally { wrapper.unmount(); }
});

it('shows the refusal when the session that ended cannot be joined again', async () => {
  sessions.starts.push(doc('A'));
  const { joinPageCollaboration } = await import('../collaboration');
  const wrapper = mount(DocumentEditor, { props: { value: emptyDocument(), readonly: false, dark: false, accent: 'blue', spaceId: 's', pageId: 'p',
    identity: { id: 'u', displayName: 'Test' }, collaborationAllowed: true } });
  try {
    await flushPromises();
    vi.mocked(joinPageCollaboration).mockRejectedValueOnce(new Error('Stránka není dostupná.'));
    sessions.live[0]!.publish({ lifecycle: 'failed', connection: 'failed', editable: false,
      error: { kind: 'collaboration', message: '[FORBIDDEN] Access to the document has been revoked.', recoverable: false } });
    await flushPromises();
    expect(wrapper.get('[role=alert]').text()).toContain('Přístup ke stránce byl odebrán');
    expect(wrapper.emitted('reopened')).toBeUndefined();
  } finally { wrapper.unmount(); }
});
