import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { jsonResponse as response, mountApp, resetBrowser } from './testing/appHarness';

let wrapper: VueWrapper | undefined;
// The profile is a chunk the shell imports on demand, cold on its first use in this file: import it up front, so the
// wait below measures the shell and not the transform of the module.
beforeAll(() => import('./components/ProfilePage.vue'));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
describe('profile history while saving', () => {
  it('restores the visible profile URL when browser history tries another tab during a save', async () => {
    let finish!: (response: Response) => void;
    const pending = new Promise<Response>(resolve => { finish = resolve; });
    const appearance = { layout: 'connected', accent: 'blue', mode: 'light' };
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return response({ id: 'reader', username: 'reader', displayName: 'Reader', isWikiAdmin: false, appearance });
      if (input === '/api/spaces') return response([]);
      if (input === '/api/auth/csrf') return response({ token: 'csrf' });
      if (input === '/api/profile/appearance') return pending;
      throw new Error('Unexpected endpoint: ' + input);
    });
    wrapper = mountApp({ url: '/#/profile/appearance', fetch: fetchMock });
    await vi.waitFor(() => expect(wrapper!.find('[data-profile-section="appearance"]').exists()).toBe(true));
    await wrapper.get('[data-appearance="mode"] [data-value="dark"]').trigger('click');
    await wrapper.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click'); await flushPromises();
    history.replaceState(null, '', '/#/profile/account');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(location.hash).toBe('#/profile/appearance');
    expect(wrapper.find('[data-profile-section="appearance"]').exists()).toBe(true);
    finish(response({ ...appearance, mode: 'dark' })); await flushPromises();
  });
});
