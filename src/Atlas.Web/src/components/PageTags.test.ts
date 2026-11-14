import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { api } from '../api';
import PageTags from './PageTags.vue';
vi.mock('../api', async original => ({ ...await original<typeof import('../api')>(), api: vi.fn() }));
const notice = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));
vi.mock('../composables/useToast', () => ({ useToast: () => ({ success: notice.success, error: notice.error, warning: vi.fn(), dismissAll: vi.fn() }) }));
let wrapper: VueWrapper | undefined;
const tags = [{ id: 'tag-api', name: 'API' }, { id: 'tag-guides', name: 'Návody' }];
function render(canEdit = true, userId = 'user-1') {
  return wrapper = mount(PageTags, { props: { pageId: 'page-1', userId, canEdit }, attachTo: document.body });
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ''; vi.resetAllMocks(); });
const puts = () => vi.mocked(api).mock.calls.filter(c => c[1]?.method === 'PUT');
describe('page tags', () => {
  it('retains loaded tags when editing becomes unavailable, then resumes normally', async () => {
    vi.mocked(api).mockResolvedValue({ tags });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click');
    await app.setProps({ canEdit: false }); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['API', 'Návody']);
    expect(vi.mocked(api).mock.calls.filter(c => c[0] === '/pages/page-1/tags')).toHaveLength(1);
    expect(app.find('.edit-page-tags').exists()).toBe(false);
    await app.setProps({ canEdit: true });
    expect(app.find('.edit-page-tags').exists()).toBe(true);
  });
  it('refreshes an interrupted save when editing becomes available again', async () => {
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method === 'PUT') return new Promise(() => {});
      return path.startsWith('/tags?') ? { items: [] } : { tags };
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click');
    await app.get('[aria-label="Odebrat tag API"]').trigger('click');
    await app.setProps({ canEdit: false }); await flushPromises();
    expect(app.findAll('.page-tag-link')).toHaveLength(2);
    vi.mocked(api).mockResolvedValue({ tags: [tags[1]] });
    await app.setProps({ canEdit: true }); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['Návody']);
  });
  it('shows the page the tags just chosen while the save is still on its way (P17)', async () => {
    let answer: (value: unknown) => void = () => {};
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method === 'PUT') return new Promise(done => { answer = done; });
      return path.startsWith('/tags?') ? { items: [] } : { tags };
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click');
    await app.get('[aria-label="Odebrat tag API"]').trigger('click');
    await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['Návody']);
    answer({ tags: [tags[1]] }); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['Návody']);
  });
  it('reads multiple tags, opens their page search and keeps readers readonly', async () => {
    vi.mocked(api).mockResolvedValue({ tags });
    const app = render(false); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['API', 'Návody']);
    await app.get('.page-tag-link').trigger('click');
    expect(app.emitted('selectTag')).toEqual([[tags[0]]]);
    expect(app.find('.edit-page-tags').exists()).toBe(false);
  });
  it('saves each change as it is made, conflict-safe, and keeps the server answer after reopening', async () => {
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (path.startsWith('/tags?')) return { items: tags.filter(tag => tag.name.toLowerCase().includes(decodeURIComponent(path.split('q=')[1] ?? '').toLowerCase())) };
      if (options?.method === 'PUT') {
        const names = JSON.parse(options.body as string).names as string[];
        return { tags: names.map(name => ({ id: tags.find(tag => tag.name === name)?.id ?? 'tag-new', name })) };
      }
      return { tags };
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag Návody"]').trigger('click'); await flushPromises();
    const puts = () => vi.mocked(api).mock.calls.filter(c => c[1]?.method === 'PUT').map(c => JSON.parse(c[1]?.body as string));
    expect(vi.mocked(api).mock.calls.find(c => c[1]?.method === 'PUT')?.[0]).toBe('/pages/page-1/tags');
    expect(puts()).toEqual([{ names: ['API'], expectedTagIds: ['tag-api', 'tag-guides'] }]);
    await app.get('[role="combobox"]').setValue('Integrace');
    // An Enter before the answer to the typed text waits for it, then takes the typed text as a new tag (no existing tag matches).
    await app.get('[role="combobox"]').trigger('keydown', { key: 'Enter' });
    await new Promise(done => setTimeout(done, 220)); await flushPromises();
    // The second change carries the ids the first one's answer left (nothing is lost between the two).
    expect(puts()[1]).toEqual({ names: ['API', 'Integrace'], expectedTagIds: ['tag-api'] });
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['API', 'Integrace']);
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['API', 'Integrace']);
  });
  it('sends one request at a time and the newest selection in the next one', async () => {
    const answers: Array<(value: unknown) => void> = [];
    vi.mocked(api).mockImplementation((path, options) => {
      if (path.startsWith('/tags?')) return Promise.resolve({ items: [] });
      if (options?.method === 'PUT') return new Promise(done => answers.push(done));
      return Promise.resolve({ tags });
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag API"]').trigger('click');
    await app.get('[aria-label="Odebrat tag Návody"]').trigger('click');
    // The first request is on its way; the second change waits for it instead of racing it.
    expect(answers).toHaveLength(1);
    answers[0]!({ tags: [tags[1]] }); await flushPromises();
    expect(answers).toHaveLength(2);
    const sent = vi.mocked(api).mock.calls.filter(c => c[1]?.method === 'PUT').map(c => JSON.parse(c[1]?.body as string));
    expect(sent).toEqual([{ names: ['Návody'], expectedTagIds: ['tag-api', 'tag-guides'] }, { names: [], expectedTagIds: ['tag-guides'] }]);
    answers[1]!({ tags: [] }); await flushPromises();
    expect(app.findAll('.page-tag-link')).toHaveLength(0);
  });
  it('deduplicates normalized input and sends nothing for a tag the page has; Esc closes and keeps what was saved', async () => {
    vi.mocked(api).mockImplementation(async path => path.startsWith('/tags?') ? { items: tags } : { tags });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[role="combobox"]').setValue('  api ');
    await app.get('[role="combobox"]').trigger('keydown', { key: 'Enter' });
    expect(app.findAll('.tag-draft')).toHaveLength(2);
    expect(vi.mocked(api).mock.calls.some(c => c[1]?.method === 'PUT')).toBe(false);
    await app.get('[role="combobox"]').trigger('keydown', { key: 'Escape' });
    expect((app.get('[role="combobox"]').element as HTMLInputElement).value).toBe('');
    expect(app.get('dialog').attributes()).toHaveProperty('open');
  });
  it('puts the saved tags back and says why when a save is refused, then Ctrl+Z walks the changes back and forth', async () => {
    let refuse = true;
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method === 'PUT') {
        if (refuse) throw new Error('Tagy mezitím upravil někdo jiný.');
        return { tags: (JSON.parse(options.body as string).names as string[]).map(name => ({ id: `tag-${name}`, name })) };
      }
      return path.startsWith('/tags?') ? { items: [] } : { tags };
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag API"]').trigger('click'); await flushPromises();
    expect(app.get('dialog').attributes()).toHaveProperty('open');
    expect(app.text()).toContain('Tagy mezitím upravil někdo jiný.');
    expect(app.findAll('.page-tag-link')).toHaveLength(2);
    expect(app.findAll('.tag-draft')).toHaveLength(2);
    refuse = false;
    await app.get('[aria-label="Odebrat tag API"]').trigger('click'); await flushPromises();
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Návody']);
    const field = app.get('[role="combobox"]');
    await field.trigger('keydown', { key: 'z', ctrlKey: true }); await flushPromises();
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['API', 'Návody']);
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['API', 'Návody']);
    await field.trigger('keydown', { key: 'y', ctrlKey: true }); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['Návody']);
  });
  it('says the suggestions are down when the catalog fails, keeps the typed tag possible, and Zkusit znovu preselects the first answer', async () => {
    let down = true;
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method === 'PUT') return { tags: (JSON.parse(options.body as string).names as string[]).map(name => ({ id: `tag-${name}`, name })) };
      if (path.startsWith('/tags?')) { if (down) throw new Error('offline'); return { items: [{ id: 't1', name: 'Provoz' }] }; }
      return { tags: [] };
    });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    expect(app.get('.tag-lookup-error').text()).toContain('Našeptávač není dostupný');
    down = false;
    await app.get('.tag-lookup-error button').trigger('click'); await flushPromises();
    expect(app.find('.tag-lookup-error').exists()).toBe(false);
    await app.get('[role="combobox"]').setValue('Pro');
    await new Promise(resolve => setTimeout(resolve, 200)); await flushPromises();
    await app.get('[role="combobox"]').trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Provoz']);
  });

  it('takes nothing from an empty field, the highlighted row once something is typed, and waits for the answer to a typed text', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      vi.mocked(api).mockImplementation(async (path, options) => {
        if (options?.method === 'PUT') return { tags: (JSON.parse(options.body as string).names as string[]).map(name => ({ id: `tag-${name}`, name })) };
        return path.startsWith('/tags?q=Prov') ? { items: [{ id: 't1', name: 'Provoz' }, { id: 't2', name: 'Provize' }] } : path.startsWith('/tags?') ? { items: [{ id: 't3', name: 'Vývoj' }] } : { tags: [] };
      });
      const app = render(); await flushPromises();
      await app.get('.edit-page-tags').trigger('click'); await vi.advanceTimersByTimeAsync(10); await flushPromises();
      const field = app.get('[role="combobox"]');
      // Nothing is highlighted in the untouched empty field, so Enter has nothing to take; the arrows choose a row.
      expect(app.findAll('[role="option"][aria-selected="true"]')).toHaveLength(0);
      await field.trigger('keydown', { key: 'Enter' });
      expect(app.findAll('.tag-draft')).toHaveLength(0);
      // Typed, then Enter before the answer came: it takes the answer's first tag, not the tag as typed.
      await field.setValue('Prov');
      await field.trigger('keydown', { key: 'Enter' });
      expect(app.findAll('.tag-draft')).toHaveLength(0);
      await vi.advanceTimersByTimeAsync(300); await flushPromises();
      expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Provoz']);
      // Tab takes the highlighted row too and the focus stays in the field.
      await field.setValue('Prov'); await vi.advanceTimersByTimeAsync(300); await flushPromises();
      expect(app.get('[role="option"][aria-selected="true"]').text()).toContain('Provize');
      await field.trigger('keydown', { key: 'Tab' }); await flushPromises();
      expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Provoz', 'Provize']);
    } finally { vi.useRealTimers(); }
  });
  it('keeps the suggestion list at the tallest height it reached while the dialog is open, so the centred dialog does not bounce', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const height = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.getAttribute('role') === 'listbox' ? this.querySelectorAll('[role="option"]').length * 50 : 0;
    });
    try {
      vi.mocked(api).mockImplementation(async path => path.startsWith('/tags?q=Zz') ? { items: [] } : path.startsWith('/tags?')
        ? { items: [{ id: 't1', name: 'Provoz' }, { id: 't2', name: 'Bezpečnost' }, { id: 't3', name: 'Vývoj' }] } : { tags });
      const app = render(); await flushPromises();
      await app.get('.edit-page-tags').trigger('click');
      await vi.advanceTimersByTimeAsync(200); await flushPromises();
      expect(app.findAll('[role="option"]')).toHaveLength(3);
      expect(app.get('[role="listbox"]').attributes('style')).toContain('min-height: 150px');
      // Typing leaves one row ("Vytvořit nový tag") for a moment: the list keeps its room instead of collapsing.
      await app.get('[role="combobox"]').setValue('Zz');
      await vi.advanceTimersByTimeAsync(200); await flushPromises();
      expect(app.findAll('[role="option"]')).toHaveLength(1);
      expect(app.get('[role="listbox"]').attributes('style')).toContain('min-height: 150px');
      // A closed dialog starts the next time from nothing.
      await app.get('dialog button[aria-label="Zavřít"]').trigger('click'); await flushPromises();
      expect(app.get('[role="listbox"]').attributes('style') ?? '').not.toContain('min-height');
    } finally { height.mockRestore(); vi.useRealTimers(); }
  });
  it('opens the dialog of another page with the rows an earlier one showed, and a skeleton of rows while the very first answer is on its way (P17)', async () => {
    let hold = false, answer: ((value: unknown) => void) | undefined;
    vi.mocked(api).mockImplementation(path => path.startsWith('/tags?') ? (hold ? new Promise(() => {}) : new Promise(done => { answer = done; })) : Promise.resolve({ tags }));
    const app = render(true, 'user-cache'); await flushPromises();
    await app.get('.edit-page-tags').trigger('click');
    expect(app.find('.loading-skeleton').exists()).toBe(true);
    answer!({ items: [{ id: 't1', name: 'Provoz' }] });
    await flushPromises();
    expect(app.find('.loading-skeleton').exists()).toBe(false);
    expect(app.findAll('[role="option"]').map(row => row.text())).toEqual([expect.stringContaining('Provoz')]);
    await app.get('dialog button[aria-label="Zavřít"]').trigger('click'); await flushPromises();
    hold = true;
    await app.setProps({ pageId: 'page-2' }); await flushPromises();
    await app.get('.edit-page-tags').trigger('click');
    expect(app.findAll('[role="option"]').map(row => row.text())).toEqual([expect.stringContaining('Provoz')]);
    expect(app.find('.loading-skeleton').exists()).toBe(false);
  });
  it('keeps the newest change when the dialog is reopened while it is being saved, instead of sending the older tags back', async () => {
    const answers: Array<(value: unknown) => void> = [];
    vi.mocked(api).mockImplementation((path, options) => path.startsWith('/tags?') ? Promise.resolve({ items: [] }) : options?.method === 'PUT' ? new Promise(done => answers.push(done)) : Promise.resolve({ tags }));
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag API"]').trigger('click');
    await app.get('dialog button[aria-label="Zavřít"]').trigger('click'); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Návody']);
    answers[0]!({ tags: [tags[1]] }); await flushPromises();
    expect(puts()).toHaveLength(1);
    expect(app.findAll('.tag-draft').map(t => t.text())).toEqual(['Návody']);
  });
  it('lets a save finish when the page is left, with the change queued behind it', async () => {
    const answers: Array<(value: unknown) => void> = [];
    vi.mocked(api).mockImplementation((path, options) => path.startsWith('/tags?') ? Promise.resolve({ items: [] }) : options?.method === 'PUT' ? new Promise(done => answers.push(done)) : Promise.resolve({ tags }));
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag API"]').trigger('click');
    await app.get('[aria-label="Odebrat tag Návody"]').trigger('click');
    app.unmount(); wrapper = undefined;
    expect((puts()[0]![1]!.signal as AbortSignal).aborted).toBe(false);
    answers[0]!({ tags: [tags[1]] }); await flushPromises();
    expect(puts().map(c => JSON.parse(c[1]?.body as string).names)).toEqual([['Návody'], []]);
  });
  it('offers Zpět on the page when closing the dialog after removing tags, and the notice puts them back', async () => {
    vi.mocked(api).mockImplementation(async (path, options) => options?.method === 'PUT' ? { tags: (JSON.parse(options.body as string).names as string[]).map(name => ({ id: `tag-${name}`, name })) } : path.startsWith('/tags?') ? { items: [] } : { tags });
    const app = render(); await flushPromises();
    await app.get('.edit-page-tags').trigger('click'); await flushPromises();
    await app.get('[aria-label="Odebrat tag API"]').trigger('click'); await flushPromises();
    expect(notice.success).not.toHaveBeenCalled();
    await app.get('dialog button[aria-label="Zavřít"]').trigger('click'); await flushPromises();
    expect(notice.success).toHaveBeenCalledTimes(1);
    expect(notice.success.mock.calls[0]![0]).toBe('Tag „API“ odebrán.');
    notice.success.mock.calls[0]![1].undo(); await flushPromises();
    expect(puts().map(c => JSON.parse(c[1]?.body as string).names)).toEqual([['Návody'], ['API', 'Návody']]);
  });
  it('ignores old page responses after navigation', async () => {
    let resolveOld!: (value: unknown) => void;
    vi.mocked(api).mockReturnValueOnce(new Promise(done => resolveOld = done)).mockResolvedValue({ tags: [{ id: 'new', name: 'Nové' }] });
    const app = render();
    await app.setProps({ pageId: 'page-2', userId: 'user-2' }); await flushPromises();
    resolveOld({ tags }); await flushPromises();
    expect(app.findAll('.page-tag-link').map(t => t.text())).toEqual(['Nové']);
  });
});
