import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import SpaceDraftApproval from './SpaceDraftApproval.vue';
import { api, ApiError } from '../api';
import { historyApi } from '../history';
import type { ObjectListener } from '../live/viewSession';

// The rows' page views (`page:{id}`): each watcher is kept so a test can tell it presence or a change.
const pageWatchers = vi.hoisted(() => new Map<string, ObjectListener>());
vi.mock('../live/viewSession', () => ({ viewSessions: () => ({ open: () => {
  const mine: string[] = [];
  return {
    watch: (id: string, listener: ObjectListener) => { pageWatchers.set(id, listener); mine.push(id); return () => pageWatchers.delete(id); },
    close() { mine.forEach(id => pageWatchers.delete(id)); },
  };
} }) }));
vi.mock('../api', async original => ({ ...(await original<typeof import('../api')>()), api: vi.fn() }));
vi.mock('../history', async original => ({ ...(await original<typeof import('../history')>()), historyApi: { revision: vi.fn() } }));
vi.mock('./HistoryRevisionPreview.vue', () => ({ default: { props: ['contentJson'], template: '<div data-preview>{{ contentJson }}</div>' } }));

const first = { id: 'first', spaceId: 'space', title: 'Nový článek', version: 3, revisionId: 'rev-3', publishedRevisionId: null, publishedVersion: null, authorId: 'ana', authorName: 'Anna', changedAt: '2026-09-25T10:00:00Z' };
const second = { ...first, id: 'second', title: 'Upravený článek', version: 7, revisionId: 'rev-7', publishedRevisionId: 'pub-5', publishedVersion: 5, authorId: 'boris', authorName: 'Boris' };
let wrapper: VueWrapper;
const tick = async () => { await flushPromises(); };
function queue(items = [first, second], total = items.length) {
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items, total } as never;
    if (options?.method === 'POST') return { id: first.id } as never;
    throw new Error(`Unexpected request: ${path}`);
  });
}
afterEach(() => { wrapper?.unmount(); vi.resetAllMocks(); pageWatchers.clear(); });
/** Autor and Typ are compact choice menus; their items are portalled into the document. */
async function choose(trigger: string, value: string) {
  await wrapper.get(trigger).trigger('click');
  await new Promise(resolve => setTimeout(resolve, 40));
  document.querySelector<HTMLElement>(`.filter-menu-panel [data-value="${value}"]`)!.click();
  await flushPromises();
}

it('sends the selected immutable version and revision only after confirmation', async () => {
  queue();
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat: Nový článek"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  expect(api).not.toHaveBeenCalledWith(expect.stringContaining('/approve'), expect.anything());
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(api).toHaveBeenCalledWith('/spaces/space/drafts/first/approve', { method: 'POST', body: JSON.stringify({ expectedVersion: 3, expectedRevisionId: 'rev-3' }) });
  expect(wrapper.emitted('published')?.[0]).toEqual(['space']);
});

it('sends the optional change summary with the approval and refuses an invalid one', async () => {
  queue();
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  const summary = wrapper.get('input[name="changeSummary"]');
  await summary.setValue('x'.repeat(501));
  expect(wrapper.get('[role=alert]').text()).toContain('Popis změny smí mít jeden řádek a nejvýše 500 znaků.');
  expect(wrapper.get('[data-confirm-approval]').attributes('disabled')).toBeDefined();
  await summary.setValue('  Schváleno po revizi  ');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  const bodies = vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST').map(([, options]) => JSON.parse(String(options!.body)));
  expect(bodies).toEqual([
    { expectedVersion: 3, expectedRevisionId: 'rev-3', changeSummary: 'Schváleno po revizi' },
    { expectedVersion: 7, expectedRevisionId: 'rev-7', changeSummary: 'Schváleno po revizi' },
  ]);
});

it('clears selection on filter changes and limits select all to loaded rows', async () => {
  queue([first], 3);
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  expect(wrapper.text()).toContain('1 vybráno');
  await choose('[data-draft-kind]', 'changes');
  await tick();
  expect(wrapper.find('[data-review-selection]').attributes('disabled')).toBeDefined();
});

it('continues after stale rows, reports each failure, and never retries an approval', async () => {
  queue();
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items: [first, second], total: 2 } as never;
    if (options?.method === 'POST' && String(path).includes('/first/')) throw new ApiError(409, 'Změněno');
    if (options?.method === 'POST') return {} as never;
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(2);
  expect(wrapper.get('.notice[role=status]').text()).toContain('1 schváleno');
  expect(wrapper.get('[role=alert]').text()).toContain('Nový článek');
  expect(wrapper.get('[role=alert]').text()).toContain('Obnovit seznam');
});

it('after draft_changed reads the row again and approves the newer revision only from its preview', async () => {
  const newer = { ...second, version: 9, revisionId: 'rev-9', changedAt: new Date().toISOString() };
  const posts: string[] = [];
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('pageId=second')) return { items: [newer], total: 1 } as never;
    if (String(path).includes('/drafts?')) return { items: [second], total: 1 } as never;
    if (options?.method === 'POST') {
      posts.push(String(options.body));
      if (posts.length === 1) throw new ApiError(409, 'Koncept se od načtení změnil.', 'draft_changed');
      return {} as never;
    }
    throw new Error(`Unexpected request: ${path}`);
  });
  vi.mocked(historyApi.revision).mockImplementation(async (_pageId, revisionId) => ({ id: revisionId, title: revisionId, markdown: revisionId, contentJson: '{"type":"doc","content":[]}' }) as never);
  wrapper = mount(SpaceDraftApproval, { attachTo: document.body, props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat: Upravený článek"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(posts).toEqual([JSON.stringify({ expectedVersion: 7, expectedRevisionId: 'rev-7' })]);
  expect(wrapper.get('[role=alert]').text()).toContain('v7 → v9');
  // Changed seconds ago: the row says so quietly.
  expect(wrapper.find('[data-being-edited]').exists()).toBe(true);
  await wrapper.get('[data-approve-renewed]').trigger('click');
  await tick();
  // The preview compares the newer revision with the one the admin saw, not with the publication.
  expect(historyApi.revision).toHaveBeenCalledWith('second', 'rev-9', expect.any(AbortSignal));
  expect(historyApi.revision).toHaveBeenCalledWith('second', 'rev-7', expect.any(AbortSignal));
  expect(posts).toHaveLength(1);
  await wrapper.get('[data-confirm-renewed]').trigger('click');
  await tick();
  expect(posts[1]).toBe(JSON.stringify({ expectedVersion: 9, expectedRevisionId: 'rev-9' }));
  expect(wrapper.text()).not.toContain('Upravený článek');
  expect(wrapper.emitted('published')).toEqual([['space']]);
});

it('labels the previewed row whose page someone edits right now, with one presence view at a time', async () => {
  queue([first, second]);
  vi.mocked(historyApi.revision).mockImplementation(async (_pageId, revisionId) => ({ id: revisionId, title: revisionId, markdown: revisionId, contentJson: '{"type":"doc","content":[]}' }) as never);
  wrapper = mount(SpaceDraftApproval, { attachTo: document.body, props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  // The queue itself watches nothing: the label of a row comes from its last change.
  expect(pageWatchers.size).toBe(0);
  expect(wrapper.find('[data-being-edited]').exists()).toBe(false);
  await wrapper.get('button[aria-label="Náhled: Nový článek"]').trigger('click');
  await tick();
  expect([...pageWatchers.keys()]).toEqual(['first']);
  pageWatchers.get('first')!.presence!({ objectId: 'first', userId: 'ana', displayName: 'Anna', color: '#000', payload: { editing: true }, via: 'page:first' });
  await tick();
  expect(wrapper.get('[data-being-edited]').text()).toBe('právě se upravuje');
  pageWatchers.get('first')!.presence!({ objectId: 'first', userId: 'ana', displayName: 'Anna', color: '#000', payload: { editing: false }, via: 'page:first' });
  await tick();
  expect(wrapper.find('[data-being-edited]').exists()).toBe(false);
  pageWatchers.get('first')!.changed!(null, 4);
  await tick();
  expect(wrapper.find('[data-being-edited]').exists()).toBe(true);
  expect(wrapper.find('[data-preview-being-edited]').exists()).toBe(true);
  await wrapper.get('dialog[open] button[aria-label="Zavřít"]').trigger('click');
  await tick();
  expect(pageWatchers.size).toBe(0);
});

it('stops queued approvals when the space view unmounts', async () => {
  queue();
  let finish!: (value: unknown) => void;
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items: [first, second], total: 2 } as never;
    if (options?.method === 'POST') return new Promise(resolve => { finish = resolve; });
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  wrapper.unmount(); finish({});
  await tick();
  expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
});

it('stops after authorization failure and requires reloading before another approval', async () => {
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items: [first, second], total: 2 } as never;
    if (options?.method === 'POST') throw new ApiError(403, 'Přístup odepřen');
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
  expect(wrapper.get('[data-review-selection]').attributes('disabled')).toBeDefined();
  expect(wrapper.get('[role=alert]').text()).toContain('Stav schválení je nejistý');
  await wrapper.get('[role=alert] button').trigger('click');
  await tick();
  expect(wrapper.find('.notice[role=status]').exists()).toBe(false);
});

it('select all excludes a row whose approval failed', async () => {
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items: [first, second], total: 2 } as never;
    if (options?.method === 'POST' && String(path).includes('/first/')) throw new ApiError(409, 'Změněno');
    if (options?.method === 'POST') return {} as never;
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat: Nový článek"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST').map(([path]) => path)).toEqual([
    '/spaces/space/drafts/first/approve', '/spaces/space/drafts/second/approve',
  ]);
});

it('holds confirmation while more rows are loading', async () => {
  let finish!: (value: unknown) => void;
  vi.mocked(api).mockImplementation(async path => {
    if (String(path).includes('offset=1')) return new Promise(resolve => { finish = resolve; });
    if (String(path).includes('/drafts?')) return { items: [first], total: 2 } as never;
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat: Nový článek"]').setValue(true);
  await wrapper.findAll('button').find(button => button.text() === 'Načíst další')!.trigger('click');
  expect(wrapper.get('[data-review-selection]').attributes('disabled')).toBeDefined();
  finish({ items: [second], total: 3 });
  await tick();
  await wrapper.get('[data-review-selection]').trigger('click');
  expect(wrapper.findAll('button').find(button => button.text() === 'Načíst další')!.attributes('disabled')).toBeDefined();
  expect(wrapper.find('[data-confirm-approval]').exists()).toBe(true);
});

it('offers authors beyond the loaded page', async () => {
  vi.mocked(api).mockImplementation(async path => {
    if (String(path).includes('/drafts?')) return { items: [first], total: 2, authors: [{ id: 'ana', name: 'Anna' }, { id: 'hidden', name: 'Hidden author' }] } as never;
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await choose('[data-draft-author]', 'ana');
  await tick();
  expect(vi.mocked(api).mock.calls.some(([path]) => String(path).includes('authorId=ana'))).toBe(true);
});

it('does not append rows from a newly typed query before it is applied', async () => {
  const hundred = Array.from({ length: 100 }, (_, index) => ({ ...first, id: `draft-${index}`, title: `Článek ${index}` }));
  vi.mocked(api).mockImplementation(async path => {
    if (String(path).includes('/drafts?')) return { items: hundred, total: 101 } as never;
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Hledat koncepty"]').setValue('Bezpečnost');
  const more = wrapper.findAll('button').find(button => button.text() === 'Načíst další')!;
  expect(more.attributes('disabled')).toBeDefined();
  await more.trigger('click');
  await tick();
  expect(vi.mocked(api).mock.calls.filter(([path]) => String(path).includes('offset=100'))).toHaveLength(0);
  await wrapper.get('form').trigger('submit');
  await tick();
  expect(vi.mocked(api).mock.calls.some(([path]) => String(path).includes('q=Bezpe%C4%8Dnost') && String(path).includes('offset=0'))).toBe(true);
});

it('stops the old queue if the space prop changes during an approval', async () => {
  let finish!: (value: unknown) => void;
  vi.mocked(api).mockImplementation(async (path, options) => {
    if (String(path).includes('/drafts?')) return { items: [first, second], total: 2 } as never;
    if (options?.method === 'POST') return new Promise(resolve => { finish = resolve; });
    throw new Error('Unexpected request');
  });
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await wrapper.setProps({ spaceId: 'other' });
  finish({}); await tick();
  expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
  expect(vi.mocked(api).mock.calls.some(([path]) => String(path).startsWith('/spaces/other/drafts?'))).toBe(true);
});

it('loads the exact draft and published revisions for preview', async () => {
  queue();
  vi.mocked(historyApi.revision).mockImplementation(async (_pageId, revisionId) => ({ id: revisionId, title: revisionId, markdown: revisionId, contentJson: '{"type":"doc","content":[]}' }) as never);
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('button[aria-label="Náhled: Upravený článek"]').trigger('click');
  await tick();
  expect(historyApi.revision).toHaveBeenCalledWith('second', 'rev-7', expect.any(AbortSignal));
  expect(historyApi.revision).toHaveBeenCalledWith('second', 'pub-5', expect.any(AbortSignal));
  expect(wrapper.text()).toContain('Porovnat změny');
});

it('gives Celá verze | Porovnat změny one thumb that is placed first and then follows the choice', async () => {
  // jsdom has no layout: Celá verze sits at 3 px (72 px wide), Porovnat změny at 77 px (110 px wide).
  const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetLeft');
  const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'offsetWidth');
  const compare = (element: HTMLElement) => element.textContent === 'Porovnat změny';
  Object.defineProperty(HTMLElement.prototype, 'offsetLeft', { configurable: true, get() { return compare(this) ? 77 : 3; } });
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, get() { return this.tagName !== 'BUTTON' ? 0 : compare(this) ? 110 : 72; } });
  const frame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
  try {
    queue();
    vi.mocked(historyApi.revision).mockImplementation(async (_pageId, revisionId) => ({ id: revisionId, title: revisionId, markdown: revisionId, contentJson: '{"type":"doc","content":[]}' }) as never);
    wrapper = mount(SpaceDraftApproval, { attachTo: document.body, props: { spaceId: 'space', dark: false, accent: 'blue' } });
    await tick();
    await wrapper.get('button[aria-label="Náhled: Upravený článek"]').trigger('click');
    await tick(); await frame(); await frame(); await tick();
    const track = wrapper.get('.approval-preview-tabs');
    expect(track.attributes('data-segmented')).toBeDefined();
    expect(track.attributes('style')).toContain('--thumb-x: 3px');
    expect(track.attributes('style')).toContain('--thumb-w: 72px');
    expect(track.attributes('data-thumb')).toBe('ready');
    await wrapper.findAll('.approval-preview-tabs button')[1]!.trigger('click');
    await tick(); await frame();
    expect(track.attributes('style')).toContain('--thumb-x: 77px');
    expect(track.attributes('style')).toContain('--thumb-w: 110px');
  } finally {
    if (left) Object.defineProperty(HTMLElement.prototype, 'offsetLeft', left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
    if (width) Object.defineProperty(HTMLElement.prototype, 'offsetWidth', width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
  }
});

it('opens preview as a dialog and returns focus to its trigger on close', async () => {
  queue();
  vi.mocked(historyApi.revision).mockImplementation(async (_pageId, revisionId) => ({ id: revisionId, title: revisionId, markdown: revisionId, contentJson: '{"type":"doc","content":[]}' }) as never);
  wrapper = mount(SpaceDraftApproval, { attachTo: document.body, props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  const trigger = wrapper.get<HTMLButtonElement>('button[aria-label="Náhled: Upravený článek"]');
  trigger.element.focus();
  await trigger.trigger('click');
  await tick();
  expect(wrapper.get('dialog[open]').attributes('aria-labelledby')).toBeDefined();
  await wrapper.get('dialog[open] button[aria-label="Zavřít"]').trigger('click');
  await tick();
  expect(document.activeElement).toBe(trigger.element);
});

it('requests one tree refresh after multiple successful approvals', async () => {
  queue();
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get('[data-review-selection]').trigger('click');
  await wrapper.get('[data-confirm-approval]').trigger('click');
  await tick();
  expect(wrapper.emitted('published')).toHaveLength(1);
});

it('keeps the shown rows in place while a search refreshes them', async () => {
  queue();
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: 'space', dark: false, accent: 'blue' } });
  await tick();
  // The spinner is rendered before the search starts, so showing it reserves no new space.
  expect(wrapper.get('.approval-count .loading-note').attributes('data-reserved')).toBe('idle');
  let finish!: (value: unknown) => void;
  vi.mocked(api).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
  await wrapper.get('input[aria-label="Hledat koncepty"]').setValue('Nový');
  await wrapper.get('form').trigger('submit');
  expect(wrapper.text()).toContain('Upravený článek');
  expect(wrapper.text()).toContain('2 z 2 konceptů');
  expect(wrapper.get('.loading-region').attributes('aria-busy')).toBe('true');
  expect(wrapper.get('.approval-count .loading-note').attributes('data-reserved')).toBe('active');
  finish({ items: [first], total: 1 });
  await tick();
  expect(wrapper.text()).not.toContain('Upravený článek');
  expect(wrapper.text()).toContain('1 z 1 konceptů');
  expect(wrapper.get('.loading-region').attributes('aria-busy')).toBe('false');
});

it.each([[1, "Publikovat 1 stránku?"], [2, "Publikovat 2 stránky?"], [5, "Publikovat 5 stránek?"]])("asks about %i selected drafts in the right Czech form", async (selected, question) => {
  queue(Array.from({ length: selected }, (_, index) => ({ ...first, id: `draft-${index}`, title: `Koncept ${index}` })));
  wrapper = mount(SpaceDraftApproval, { props: { spaceId: "space", dark: false, accent: "blue" } });
  await tick();
  await wrapper.get('input[aria-label="Vybrat načtené koncepty"]').setValue(true);
  await wrapper.get("[data-review-selection]").trigger("click");
  expect(wrapper.get(".approval-confirm strong").text()).toBe(question);
});
