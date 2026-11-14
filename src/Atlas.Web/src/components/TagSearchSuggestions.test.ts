import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { api } from '../api';
import TagSearchSuggestions from './TagSearchSuggestions.vue';
vi.mock('../api', async original => ({ ...await original<typeof import('../api')>(), api: vi.fn() }));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); vi.resetAllMocks(); vi.useRealTimers(); });
describe('searching tags', () => {
  it('announces a failed tag lookup', async () => {
    vi.useFakeTimers(); vi.mocked(api).mockRejectedValue(new Error('Unavailable'));
    wrapper = mount(TagSearchSuggestions, { props: { query: 'api', userId: 'user-1' } });
    expect(wrapper.get('[role="status"]').text()).toBe('');
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(wrapper.get('[role="status"]').text()).toContain('nepodařilo');
  });
  it('finds a tag by name and emits a selection after stripping the optional hash', async () => {
    vi.useFakeTimers();
    vi.mocked(api).mockResolvedValue({ items: [{ id: 'api', name: 'API' }] });
    wrapper = mount(TagSearchSuggestions, { props: { query: '#api', userId: 'user-1' } });
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(vi.mocked(api).mock.calls[0]?.[0]).toBe('/tags?q=api');
    expect(wrapper.get('[role="status"]').text()).toContain('1');
    expect(wrapper.get('[role="status"]').text()).toContain('Tab');
    await wrapper.get('button').trigger('click');
    expect(wrapper.emitted('selectTag')).toEqual([[{ id: 'api', name: 'API' }]]);
  });
  it('clears suggestions immediately on another query and discards old responses', async () => {
    vi.useFakeTimers(); let resolveOld!: (value: unknown) => void;
    vi.mocked(api).mockReturnValueOnce(new Promise(done => resolveOld = done)).mockResolvedValue({ items: [{ id: 'new', name: 'Nové' }] });
    wrapper = mount(TagSearchSuggestions, { props: { query: 'api', userId: 'user-1' } });
    await vi.advanceTimersByTimeAsync(200);
    await wrapper.setProps({ query: 'nové', userId: 'user-2' });
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    resolveOld({ items: [{ id: 'old', name: 'Tajné' }] }); await flushPromises();
    expect(wrapper.text()).toContain('Nové'); expect(wrapper.text()).not.toContain('Tajné');
    // The block folds instead of vanishing: out of reach at once (closed, inert), gone when the fold ends.
    await wrapper.setProps({ query: '' });
    expect(wrapper.get('[data-inline-reveal]').attributes('data-state')).toBe('closed');
    expect(wrapper.get('[data-inline-reveal]').attributes('inert')).toBeDefined();
    await flushPromises(); expect(wrapper.findAll('.tag-chip')).toHaveLength(0);
  });
  it('unfolds the block when tags arrive instead of pushing the results by its height at once', async () => {
    vi.useFakeTimers(); vi.mocked(api).mockResolvedValue({ items: [{ id: 'api', name: 'API' }] });
    wrapper = mount(TagSearchSuggestions, { props: { query: 'api', userId: 'user-1' } });
    expect(wrapper.find('.tag-search-suggestions').exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(wrapper.get('[data-inline-reveal]').attributes('data-state')).toBe('open');
    expect(wrapper.get('.tag-search-suggestions').text()).toContain('API');
  });
});
