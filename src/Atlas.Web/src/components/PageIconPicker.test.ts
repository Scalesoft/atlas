import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { api, ApiError } from '../api';
import PageIconPicker from './PageIconPicker.vue';

vi.mock('../api', async original => ({ ...await original<typeof import('../api')>(), api: vi.fn() }));
let wrapper: VueWrapper | undefined;
const original = { revisionId: 'revision-1', icon: 'folder', color: 'blue' };
function render(decoration: typeof original | null = null, canEdit = true) {
  return wrapper = mount(PageIconPicker, { props: { pageId: 'page-1', userId: 'user-1', canEdit, decoration, title: 'Dokumentace' }, attachTo: document.body });
}
const icon = (app: VueWrapper, id: string) => app.get(`[data-appearance-icons] [data-value="${id}"]`);
// Ten soft colours (tone 2) are offered first; the whole palette opens under Další barvy a tóny.
const color = (app: VueWrapper, id: string) => app.get(`[data-appearance-${id.endsWith('-2') ? 'quick-colors' : 'colors'}] [data-value="${id}"]`);
const morePalette = async (app: VueWrapper) => { await app.get('[data-appearance-more-colors]').trigger('click'); await flushPromises(); };
const checked = (element: ReturnType<typeof icon>) => element.attributes('aria-checked') === 'true';
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ''; vi.resetAllMocks(); });
describe('page icon picker', () => {
  it('unfolds in place with the named palette and reconciles a save interrupted by going offline', async () => {
    vi.mocked(api).mockImplementation(async (_path, options) => options?.method === 'PUT' ? new Promise(() => {}) : { decoration: { ...original, revisionId: 'confirmed', color: 'green-2' } });
    const app = render(original); await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    expect(document.querySelector('dialog')).toBeNull();
    expect(app.findAll('[data-appearance-quick-colors] [role="radio"]')).toHaveLength(10);
    expect(app.find('[data-appearance-colors]').exists()).toBe(false);
    await morePalette(app);
    expect(app.findAll('[data-appearance-colors] [role="radio"]')).toHaveLength(60);
    expect(color(app, 'teal-2').attributes('aria-label')).toBe('Tyrkysová · jemná');
    // A colour stored before the palette (blue) is shown as its palette tone.
    expect(checked(color(app, 'blue-2'))).toBe(true);
    await color(app, 'green-2').trigger('click'); await app.get('.page-icon-save').trigger('click');
    await app.setProps({ canEdit: false }); await flushPromises();
    expect(app.find('.page-icon-picker').exists()).toBe(false);
    expect(app.emitted('saved')).toBeUndefined();
    await app.setProps({ canEdit: true }); await flushPromises();
    expect(app.emitted('saved')).toEqual([[{ ...original, revisionId: 'confirmed', color: 'green-2' }]]);
  });
  it('searches icons and saves the selected icon and color only on explicit confirmation', async () => {
    const saved = { revisionId: 'new', icon: 'database', color: 'violet-3' };
    vi.mocked(api).mockResolvedValue({ decoration: saved });
    const app = render(); await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    // The search over the whole icon set unfolds under „Další ikony“ (2.6: the common icons come first).
    await app.get('[data-appearance-more-icons]').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    await app.get('[data-appearance-search]').setValue('databáze');
    // The icon named exactly so comes first; the icon pack adds more databases after it.
    const found = app.findAll('[data-appearance-results] [role="option"]');
    expect(found[0]!.attributes('data-value')).toBe('database');
    await found[0]!.trigger('click');
    await morePalette(app);
    await color(app, 'violet-3').trigger('click');
    expect(app.find('[data-appearance-preview] [data-icon="database"]').exists()).toBe(true);
    expect(vi.mocked(api)).not.toHaveBeenCalled();
    await app.get('.page-icon-save').trigger('click'); await flushPromises();
    expect(JSON.parse(vi.mocked(api).mock.calls[0]![1]!.body as string)).toEqual({ icon: 'database', color: 'violet-3', expectedRevisionId: null });
    expect(app.emitted('saved')).toEqual([[saved]]);
  });
  it('cancels a removal and can explicitly save no icon with the current token', async () => {
    const cleared = { revisionId: 'new', icon: null, color: null };
    vi.mocked(api).mockResolvedValue({ decoration: cleared });
    const app = render(original); await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    await app.get('.page-icon-clear').trigger('click'); await app.get('.page-icon-cancel').trigger('click');
    expect(vi.mocked(api)).not.toHaveBeenCalled();
    await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    expect(checked(icon(app, 'folder'))).toBe(true);
    await app.get('.page-icon-clear').trigger('click'); await app.get('.page-icon-save').trigger('click'); await flushPromises();
    expect(JSON.parse(vi.mocked(api).mock.calls[0]![1]!.body as string)).toEqual({ icon: null, color: null, expectedRevisionId: 'revision-1' });
    expect(app.emitted('saved')).toEqual([[cleared]]);
  });
  it('keeps the selection after failure and reloads a conflicting server state before another save', async () => {
    vi.mocked(api).mockRejectedValueOnce(new ApiError(409, 'Ikonu změnil jiný uživatel.')).mockResolvedValue({ decoration: { revisionId: 'other', icon: 'star', color: 'amber' } });
    const app = render(original); await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    await color(app, 'green-2').trigger('click'); await app.get('.page-icon-save').trigger('click'); await flushPromises();
    expect(app.get('[role="alert"]').text()).toContain('jiný uživatel');
    expect(app.get('.page-icon-save').attributes()).toHaveProperty('disabled');
    expect(checked(color(app, 'green-2'))).toBe(true);
    await app.get('.page-icon-reload').trigger('click'); await flushPromises();
    expect(checked(icon(app, 'star'))).toBe(true);
    expect(app.emitted('saved')?.[0]?.[0]).toEqual({ revisionId: 'other', icon: 'star', color: 'amber' });
  });
  it('does not expose editing to readers', () => {
    expect(render(original, false).find('.page-icon-edit').exists()).toBe(false);
  });
  it('ignores a save response from the previous page or account', async () => {
    let resolve!: (value: unknown) => void;
    vi.mocked(api).mockReturnValue(new Promise(done => resolve = done));
    const app = render(original); await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    await color(app, 'green-2').trigger('click'); await app.get('.page-icon-save').trigger('click');
    await app.setProps({ pageId: 'page-2', userId: 'user-2', decoration: null });
    resolve({ decoration: original }); await flushPromises();
    expect(app.emitted('saved')).toBeUndefined();
    expect(app.find('.page-icon-picker').exists()).toBe(false);
  });
});
