import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { defineComponent } from 'vue';
import { jsonResponse as response, mountApp, resetBrowser } from './testing/appHarness';

const appearance = { layout: 'connected', accent: 'blue', mode: 'light' };
const originalUser = { id: 'reader', username: 'reader', displayName: 'Original session', isWikiAdmin: false, appearance };
const newSession = { ...originalUser, displayName: 'New session' };
const ProfileStub = defineComponent({ emits: ['sessionChanged', 'logout'], template: '<div><button data-refresh @click="$emit(\'sessionChanged\')">Refresh session</button><button data-profile-logout @click="$emit(\'logout\')">Odhlásit se</button></div>' });
const LoginStub = defineComponent({ emits: ['signedIn'], setup: () => ({ newSession }), template: '<button data-login @click="$emit(\'signedIn\', newSession)">Sign in</button>' });
let wrapper: VueWrapper | undefined;
// The profile and the two parts of its tab Moje přístupy are chunks the shell imports on demand, cold on their first use
// in this file: import them once up front, so the first wait measures the shell and not the transform of the modules.
beforeAll(() => Promise.all([
  import('./components/ProfilePage.vue'),
  import('./components/settings/access/ProfileHubStatus.vue'),
  import('./components/settings/access/MyAccess.vue'),
]));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
// The shell's spaces are listed by the search's space filter, a FilterMenu (962562f9b) whose options render only while it is open.
async function searchSpaceNames(app: VueWrapper) {
  await app.get('[data-search-space]').trigger('click');
  await new Promise(resolve => setTimeout(resolve, 40));
  await flushPromises();
  const names = document.querySelector('.filter-menu-panel')?.textContent ?? '';
  document.querySelector<HTMLElement>('.filter-menu-panel')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await flushPromises();
  return names;
}

describe('session refresh ownership', () => {
  it('refreshes shell access after self-verification and removes revoked spaces from navigation', async () => {
    let revoked = false;
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return response(originalUser);
      if (input === '/api/spaces') return response(revoked ? [] : [{ id: 'space-1', name: 'PRIVATE SPACE', icon: 'book', color: 'blue', role: 'Reader', pinned: true }]);
      if (input === '/api/spaces/space-1/tree') return response({ version: 1, items: [] });
      if (input === '/api/profile/hub') return response({ mode: 'Enforced', identity: { state: 'Active', verifiedUntil: '2099-01-01T00:00:00Z', lastSuccessAt: null, lastAttemptAt: null, lastErrorCode: null } });
      if (input === '/api/profile/hub/verify') { revoked = true; return response({}); }
      if (input === '/api/access/kinds') return response([]);
      if (input === '/api/access/me') return response({ principal: { kind: 'user', id: 'reader', name: 'Reader' }, scope: 'self', canManage: false, entries: [] });
      if (input === '/api/auth/csrf') return response({ token: 'csrf' });
      throw new Error('Unexpected request: ' + input);
    });
    wrapper = mountApp({ url: '/#/profile/access', fetch: fetchMock, stubs: { PersonalLibraryPanel: true } });
    await vi.waitFor(() => expect(wrapper!.find('[data-profile-hub-verify]').exists()).toBe(true));
    expect(await searchSpaceNames(wrapper)).toContain('PRIVATE SPACE');
    await wrapper.get('[data-profile-hub-verify]').trigger('click');
    await flushPromises();
    expect(await searchSpaceNames(wrapper)).not.toContain('PRIVATE SPACE');
    expect(location.hash).toBe('#/profile/access');
  });

  it('clears cached space metadata when opening its tree is denied', async () => {
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return response(originalUser);
      if (input === '/api/spaces') return response([{ id: 'space-1', name: 'REVOKED SPACE', icon: 'book', color: 'blue', role: 'Reader', pinned: true }]);
      if (input === '/api/spaces/space-1/tree') return response({ detail: 'Prostor nebyl nalezen.' }, 404);
      throw new Error('Unexpected request: ' + input);
    });
    wrapper = mountApp({ url: '/#/spaces/space-1', fetch: fetchMock, stubs: { PersonalLibraryPanel: true } });
    await vi.waitFor(() => expect(wrapper!.text()).toContain('Prostor nebyl nalezen.'));
    expect(wrapper.text()).not.toContain('REVOKED SPACE');
    expect(wrapper.find('.space-hero').exists()).toBe(false);
  });

  it.each(['logout', 'same-account-login', 'late-error'] as const)('ignores a stale session refresh after %s', async scenario => {
    let finish!: (result: Response) => void;
    const pending = new Promise<Response>(resolve => { finish = resolve; });
    let reads = 0;
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return ++reads === 1 ? response(originalUser) : pending;
      if (input === '/api/spaces') return response([]);
      if (input === '/api/auth/csrf') return response({ token: 'csrf' });
      if (input === '/api/auth/logout') return response({});
      throw new Error('Unexpected request: ' + input);
    });
    wrapper = mountApp({ url: '/#/profile/security', fetch: fetchMock, stubs: { ProfilePage: ProfileStub, LoginMethods: LoginStub, PersonalLibraryPanel: true } });
    await vi.waitFor(() => expect(wrapper!.find('[data-refresh]').exists()).toBe(true));
    await wrapper.get('[data-refresh]').trigger('click'); await flushPromises();
    expect(reads).toBe(2);
    await wrapper.get('[data-profile-logout]').trigger('click'); await flushPromises();
    expect(wrapper.find('[data-login]').exists()).toBe(true);
    if (scenario === 'same-account-login') {
      await wrapper.get('[data-login]').trigger('click'); await flushPromises();
      expect(wrapper.get('.profile-launch').text()).toContain('New session');
    }
    finish(scenario === 'late-error' ? response({ detail: 'Old session failure' }, 500) : response({ ...originalUser, displayName: 'Stale response' }));
    await flushPromises();
    if (scenario === 'same-account-login') expect(wrapper.get('.profile-launch').text()).toContain('New session');
    else expect(wrapper.find('[data-login]').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('Stale response');
    expect(wrapper.text()).not.toContain('Old session failure');
  });
  it('drops a late start-up answer of the previous account after signing out and in as another', async () => {
    const first = { ...originalUser, id: 'a', displayName: 'Account A' };
    const second = { ...originalUser, id: 'b', displayName: 'Account B' };
    const accounts = [first, second];
    const TwoLogins = defineComponent({ emits: ['signedIn'], setup: () => ({ accounts }), template: '<button data-login @click="$emit(\'signedIn\', accounts.shift())">Sign in</button>' });
    let finishA!: (result: Response) => void;
    let spaceReads = 0;
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return response({ detail: 'Unauthorized' }, 401);
      if (input === '/api/spaces') return ++spaceReads === 1
        ? new Promise<Response>(resolve => { finishA = resolve; })
        : response([{ id: 'space-b', name: 'SPACE OF B', icon: 'book', color: 'blue', role: 'Reader', pinned: true }]);
      if (input === '/api/spaces/space-b/tree') return response({ version: 1, items: [] });
      if (input === '/api/auth/csrf') return response({ token: 'csrf' });
      if (input === '/api/auth/logout') return response({});
      throw new Error('Unexpected request: ' + input);
    });
    wrapper = mountApp({ url: '/', fetch: fetchMock, stubs: { ProfilePage: ProfileStub, LoginMethods: TwoLogins, PersonalLibraryPanel: true } });
    await vi.waitFor(() => expect(wrapper!.find('[data-login]').exists()).toBe(true));
    // A signs in; its spaces are still on the way when A signs out and B signs in.
    await wrapper.get('[data-login]').trigger('click'); await flushPromises();
    await wrapper.get('.profile-launch').trigger('click');
    await vi.waitFor(() => expect(wrapper!.find('[data-profile-logout]').exists()).toBe(true));
    await wrapper.get('[data-profile-logout]').trigger('click'); await flushPromises();
    await wrapper.get('[data-login]').trigger('click');
    await vi.waitFor(() => expect(wrapper!.text()).toContain('SPACE OF B'));
    finishA(response([{ id: 'space-a', name: 'SPACE OF A', icon: 'book', color: 'blue', role: 'Reader', pinned: true }]));
    await flushPromises();
    expect(wrapper.text()).toContain('SPACE OF B');
    expect(wrapper.text()).not.toContain('SPACE OF A');
  });
  it('signs out locally with the logout reset when the server has already ended the session', async () => {
    let reads = 0;
    const fetchMock = vi.fn(async (input: string) => {
      if (input === '/api/setup/status') return response({ requiresSetup: false, databaseReady: true });
      if (input === '/api/auth/me') return ++reads === 1 ? response(originalUser) : response({ detail: 'Unauthorized' }, 401);
      if (input === '/api/spaces') return response([{ id: 'space-1', name: 'PRIVATE SPACE', icon: 'book-open', color: 'blue', role: 'Reader' }]);
      throw new Error('Unexpected request: ' + input);
    });
    wrapper = mountApp({ url: '/#/profile/security', fetch: fetchMock, stubs: { ProfilePage: ProfileStub, LoginMethods: LoginStub, PersonalLibraryPanel: true } });
    await vi.waitFor(() => expect(wrapper!.find('[data-refresh]').exists()).toBe(true));
    expect(await searchSpaceNames(wrapper)).toContain('PRIVATE SPACE');
    await wrapper.get('[data-refresh]').trigger('click'); await flushPromises();
    expect(wrapper.find('[data-login]').exists()).toBe(true);
    expect(wrapper.get('[role="alert"]').text()).toContain('přihlášení skončilo');
    expect(wrapper.text()).not.toContain('PRIVATE SPACE');
    expect(location.hash).toBe('');
  });
});
