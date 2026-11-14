import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import SpacePageList from './SpacePageList.vue';
import { ApiError, post, type PageSummary } from '../api';
import { useWorkspacePanel } from '../composables/useWorkspacePanel';
import { memoryRelations } from '../objects/memoryRelations';

// Publishing drafts from Obsah (the list over the objects of a space): the rows come from the server's contents contract, here
// objects/memoryRelations.ts answers it; the pages of the tree say which of them are drafts.
vi.mock('../api', async original => ({ ...(await original<typeof import('../api')>()), post: vi.fn() }));
const pages: PageSummary[] = [
  { id: 'first', spaceId: 'space', parentId: null, title: 'První koncept', version: 3, isPublished: false },
  { id: 'second', spaceId: 'space', parentId: 'first', title: 'Druhý koncept', version: 7, isPublished: false },
  { id: 'live', spaceId: 'space', parentId: null, title: 'Publikováno', version: 2, isPublished: true },
];
const objectOf = (id: string, typeId: string, title: string, path: string | null = null) => ({ id, typeId, title, spaceId: 'space', spaceName: 'Prostor', readable: true, path });
const objects = [
  ...pages.map(item => objectOf(item.id, 'atlas.page', item.title, item.parentId ? 'První koncept' : null)),
  objectOf('plan', 'atlas.board', 'Plán', 'První koncept'),
];
const relations = memoryRelations(objects, []);
const panel = useWorkspacePanel();
let wrapper: VueWrapper;
afterEach(() => { wrapper?.unmount(); panel.reset(); vi.resetAllMocks(); vi.restoreAllMocks(); });
async function show(props: Record<string, unknown> = {}, tree: PageSummary[] = pages) {
  wrapper = mount(SpacePageList, { props: { pages: tree, canWrite: true, spaceId: 'space', relations, ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}
const rowTitles = () => wrapper.findAll('.content-row .space-page-title').map(title => title.text());

it('selects all drafts and publishes only the selected versions, with progress', async () => {
  let complete!: (value: unknown) => void;
  vi.mocked(post).mockImplementationOnce(() => new Promise(resolve => { complete = resolve; })).mockResolvedValueOnce({});
  await show();
  await wrapper.get('[data-select-pages]').trigger('click');
  await wrapper.get('input[aria-label="Vybrat všechny koncepty"]').setValue(true);
  await wrapper.get('button[data-publish]').trigger('click');
  expect(wrapper.get('button[data-publish]').attributes('disabled')).toBeDefined();
  expect(post).toHaveBeenCalledWith('/pages/first/publish', { expectedVersion: 3, refuseUnsavedLiveEdits: true });
  complete({}); await flushPromises();
  expect(post).toHaveBeenLastCalledWith('/pages/second/publish', { expectedVersion: 7, refuseUnsavedLiveEdits: true });
  expect(post).toHaveBeenCalledTimes(2);
  expect(wrapper.get('[role=status]').text()).toContain('2');
  expect(wrapper.emitted('published')).toHaveLength(1);
  expect(wrapper.findAll('input[type=checkbox]')).toHaveLength(0);
});

it('stops on a stale revision and keeps remaining drafts selected without retrying automatically', async () => {
  vi.mocked(post).mockResolvedValueOnce({}).mockRejectedValueOnce(new ApiError(409, 'Stránka byla změněna.'));
  await show();
  await wrapper.get('[data-select-pages]').trigger('click');
  await wrapper.get('input[aria-label="Vybrat všechny koncepty"]').setValue(true);
  await wrapper.get('button[data-publish]').trigger('click'); await flushPromises();
  expect(wrapper.get('[role=alert]').text()).toContain('Druhý koncept');
  expect(wrapper.get('[role=alert]').text()).toContain('Stránka byla změněna.');
  expect(wrapper.get<HTMLInputElement>('input[aria-label="Vybrat: Druhý koncept"]').element.checked).toBe(true);
  expect(post).toHaveBeenCalledTimes(2);
  expect(wrapper.emitted('published')).toHaveLength(1);
});

it('allows a subset selection and offers no publishing controls to readers', async () => {
  vi.mocked(post).mockResolvedValue({});
  await show();
  expect(wrapper.find('input[type=checkbox]').exists()).toBe(false);
  await wrapper.get('[data-select-pages]').trigger('click');
  await wrapper.get('input[aria-label="Vybrat: Druhý koncept"]').setValue(true);
  await wrapper.get('button[data-publish]').trigger('click'); await flushPromises();
  expect(post).toHaveBeenCalledExactlyOnceWith('/pages/second/publish', { expectedVersion: 7, refuseUnsavedLiveEdits: true });
  await wrapper.setProps({ canWrite: false });
  // The publish bar folds away through InlineReveal; it is gone once that has settled.
  await flushPromises();
  expect(wrapper.find('input[type=checkbox]').exists()).toBe(false);
  expect(wrapper.find('button[data-publish]').exists()).toBe(false);
  expect(wrapper.findAll('a.space-page-title')).toHaveLength(4);
});

it('finds titles without accents, sorts them, marks drafts and only selects visible drafts', async () => {
  vi.mocked(post).mockResolvedValue({});
  await show();
  expect(rowTitles()).toEqual(['Druhý koncept', 'Plán', 'První koncept', 'Publikováno']);
  // A draft says so under its title, with where it lives; a published page does not.
  expect(wrapper.get('[data-content-id="second"] .content-sub').text()).toBe('První koncept · koncept');
  expect(wrapper.find('[data-content-id="live"] .content-sub').exists()).toBe(false);
  await wrapper.get('input[aria-label="Hledat v obsahu prostoru"]').setValue('PRVNI');
  await new Promise(resolve => setTimeout(resolve, 260)); await flushPromises();
  expect(rowTitles()).toEqual(['První koncept']);
  await wrapper.get('[data-select-pages]').trigger('click');
  await wrapper.get('input[aria-label="Vybrat všechny koncepty"]').setValue(true);
  await wrapper.get('button[data-publish]').trigger('click'); await flushPromises();
  expect(post).toHaveBeenCalledExactlyOnceWith('/pages/first/publish', { expectedVersion: 3, refuseUnsavedLiveEdits: true });
});

it('clears selection when filters change and explains an empty result', async () => {
  await show();
  await wrapper.get('[data-select-pages]').trigger('click');
  await wrapper.get('input[aria-label="Vybrat: První koncept"]').setValue(true);
  expect(wrapper.get('button[data-publish]').attributes('disabled')).toBeUndefined();
  await wrapper.get('[data-content-tile="type:atlas.board"]').trigger('click'); await flushPromises();
  expect(rowTitles()).toEqual(['Plán']);
  await wrapper.get('[data-content-tile="type:atlas.board"]').trigger('click'); await flushPromises();
  expect(wrapper.get('button[data-publish]').attributes('disabled')).toBeDefined();
  await wrapper.get('input[aria-label="Hledat v obsahu prostoru"]').setValue('neexistující');
  await new Promise(resolve => setTimeout(resolve, 260)); await flushPromises();
  expect(wrapper.text()).toContain('Nic neodpovídá');
  await wrapper.get('[data-clear-filters]').trigger('click');
  await new Promise(resolve => setTimeout(resolve, 260)); await flushPromises();
  expect(wrapper.findAll('.content-row')).toHaveLength(4);
});

it('moves nothing in one frame when selection starts and ends: the columns glide, the checkboxes travel with them', async () => {
  // jsdom has no layout and no Web Animations: the title column stands 36 px further in while choosing, and the animations are recorded.
  const animated: { element: Element; frames: Keyframe[] }[] = [];
  vi.spyOn(Element.prototype, 'getBoundingClientRect').mockImplementation(function (this: Element) {
    const choosing = !!this.closest('.is-choosing');
    return { left: this.matches('.space-page-main') ? (choosing ? 386 : 350) : 0, top: 0, bottom: 10 } as DOMRect;
  });
  Element.prototype.animate = function (this: Element, frames: Keyframe[] | PropertyIndexedKeyframes | null) {
    animated.push({ element: this, frames: frames as Keyframe[] });
    return { finished: Promise.resolve(), addEventListener() {}, cancel() {} } as unknown as Animation;
  };
  try {
    await show();
    animated.length = 0;
    await wrapper.get('[data-select-pages]').trigger('click');
    const cells = animated.filter(({ element }) => element.matches('.space-page-main, .space-head-main'));
    // The header's cell and the four rows' cells of the column glide by what the column travelled.
    expect(cells).toHaveLength(5);
    expect(cells[0]!.frames[0]).toEqual({ translate: '-36px 0' });
    // The checkboxes come in from the edge, fading, by the same distance.
    const boxes = animated.filter(({ element }) => element.matches('.page-selection'));
    expect(boxes).toHaveLength(4);
    expect(boxes[0]!.frames[0]).toEqual({ translate: '-36px 0', opacity: 0 });

    animated.length = 0;
    await wrapper.get('[data-select-pages]').trigger('click');
    expect(animated.filter(({ element }) => element.matches('.space-page-main'))[0]!.frames[0]).toEqual({ translate: '36px 0' });
    // The checkboxes that are gone leave as copies pinned in their rows, and are hidden from assistive technology.
    const leaving = animated.filter(({ element }) => element.matches('.page-selection[aria-hidden="true"]'));
    expect(leaving).toHaveLength(4);
    expect(leaving[0]!.frames).toEqual([{ translate: '0 0', opacity: 1 }, { translate: '-36px 0', opacity: 0 }]);
  } finally {
    delete (Element.prototype as { animate?: unknown }).animate;
  }
});

it("lists the space's boards among its objects and opens a board as a board", async () => {
  await show();
  const board = wrapper.get('[data-content-row="board"]');
  expect(board.attributes('href')).toContain('#/boards/plan');
  // A board is never a draft to publish.
  expect(wrapper.get('[data-select-pages]').text()).toContain('(2)');
  board.element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
  expect(wrapper.emitted('open')).toEqual([['plan', 'board']]);
});

it("shows only the boards when the address asks for the space's boards", async () => {
  await show({ initialType: 'board' });
  expect(wrapper.findAll('[data-content-row]').map(row => row.text())).toEqual(['Plán']);
});
