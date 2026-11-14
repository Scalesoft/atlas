import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ProfilePage from './ProfilePage.vue';
import { api, post, ApiError } from '../api';
import * as aiCapabilities from '../composables/useAiCapabilities';
import { aiPreferencesApi } from '../aiPreferences';
import { themeAccents } from '../profile';
import { reauthWindow } from '../protection';
const aiAvailable = (aiCapabilities as unknown as { aiAvailable: { value: boolean } }).aiAvailable;

vi.mock('../composables/useAiCapabilities', async () => {
  const { ref } = await import('vue');
  const any = ref(false);
  return { useAiCapabilities: () => ({ any, capabilities: ref({ answer: false, assistant: false }) }), aiAvailable: any };
});
vi.mock('../aiPreferences', async (original) => ({ ...(await original<typeof import('../aiPreferences')>()), aiPreferencesApi: { load: vi.fn(), save: vi.fn(), deleteAllConversations: vi.fn() } }));
vi.mock('../api', async (original) => ({ ...(await original<typeof import('../api')>()), api: vi.fn(), post: vi.fn() }));
const { mountReference, disposeReference } = vi.hoisted(() => {
  const disposeReference = vi.fn();
  return { disposeReference, mountReference: vi.fn((..._: [HTMLElement, { source: string }, unknown]) => disposeReference) };
});
// This test owns the host/cleanup seam; loading the real Scalar bundle before returning
// its mocked mount unnecessarily makes the lazy action depend on a cold library import.
vi.mock('@atlas/markdown/plugins/openapi', () => ({ mountOpenApiReference: mountReference }));
const apiDocument = { openapi: '3.1.1', info: { title: 'Atlas', version: 'v1' }, paths: {} };
const account = { displayName: 'Reader', email: null, version: 'v1', source: 'builtin', canEditDisplayName: true, canEditEmail: true,
  currentSessionAuthenticatedAt: null, currentSessionAuthenticationMethod: null, lastSignInAt: null, lastSignInMethod: null };
const catalog = { canIssueTokens: true, tokens: [], apiBaseUrl: '/api/v1', openApiUrl: '/api/profile/api-document' };
const props = { tab: 'account' as const, appearance: { layout: 'connected' as const, accent: 'blue' as const, mode: 'light' as const }, appearanceDirty: false, appearancePending: false, appearanceError: '' };
let wrapper: VueWrapper | undefined;
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes; }); return { promise, resolve }; }
function last(event: string) { return wrapper!.emitted(event)?.at(-1)?.[0]; }
/** Issuing a token is a security change (V4, SEC-A-2): it goes out through api, confirmed inside the re-authentication window. */
const issuing = (options?: RequestInit) => options?.method === 'POST';
async function confirmIssue(selector = '[data-confirm-reauth]') {
  reauthWindow.sync(new Date(Date.now() + 600_000).toISOString()); await flushPromises();
  await wrapper!.get(selector).trigger('submit');
}
// The profile sections are links of the settings navigation (#/profile/<section>).
const tab = (id: string) => `[data-path="/profile/${id}"]`;
const saveAccount = () => wrapper!.get('[data-profile-section="account"] [data-save-bar-save]').trigger('click');
// The password change opens in place under the Heslo section.
async function openPasswordChange() {
  await vi.waitFor(() => expect(wrapper!.find('[data-password-change]').exists()).toBe(true), { timeout: 5000 });
  await wrapper!.get('[data-password-change]').trigger('click'); await flushPromises();
}
function configure() {
  vi.mocked(api).mockImplementation(async path => {
    if (path === '/profile/account') return account;
    if (path === '/profile/security') return { hasBuiltinCredential: true, credentialState: 'active' };
    if (path === '/profile/api-tokens') return catalog;
    if (path === '/auth/methods') return { builtinEnabled: true, providers: [{ id: 'hub', displayName: 'Hub' }] };
    if (path === '/profile/identities') return { items: [] };
    throw new Error('Unexpected endpoint: ' + path);
  });
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; aiAvailable.value = false; vi.clearAllMocks(); vi.restoreAllMocks(); reauthWindow.close(); });

describe('profile navigation state', () => {
  it('hides personal API navigation when the account has no permission, MCP, or owned tokens', async () => {
    configure();
    const base = vi.mocked(api).getMockImplementation()!;
    vi.mocked(api).mockImplementation(async (path, options) =>
      path === '/profile/api-tokens' ? { ...catalog, canIssueTokens: false, level: 'none', mcpEnabled: false } : base(path, options));
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    expect(wrapper.find(tab('api')).exists()).toBe(false);
  });

  it('keeps MCP and owned-token access visible with issuance denied and reuses the navigation catalog', async () => {
    configure();
    const base = vi.mocked(api).getMockImplementation()!;
    const owned = { id: 'owned', name: 'Earlier token', prefix: 'atlas_old', scopes: ['read'], activeFrom: '2026-01-01T00:00:00Z', expiresAt: '2099-01-01T00:00:00Z', activeTo: null };
    vi.mocked(api).mockImplementation(async (path, options) =>
      path === '/profile/api-tokens' ? { ...catalog, canIssueTokens: false, level: 'none', mcpEnabled: true, mcpUrl: 'https://wiki.example/mcp', tokens: [owned] } : base(path, options));
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'api' } }); await flushPromises();
    expect(wrapper.get(tab('api')).text()).toContain('MCP');
    await vi.waitFor(() => expect(wrapper!.find('[data-token-revoke]').exists()).toBe(true));
    expect(wrapper.find('[data-token-new]').exists()).toBe(false);
    expect(vi.mocked(api).mock.calls.filter(([path]) => path === '/profile/api-tokens')).toHaveLength(1);
  });
  it('keeps issued and revoked tokens current when returning to the API section', async () => {
    configure();
    const base = vi.mocked(api).getMockImplementation()!;
    const issued = { id: 'new-token', name: 'Automation', prefix: 'atlas_new', scopes: ['read'], activeFrom: '2026-01-01T00:00:00Z', expiresAt: '2099-01-01T00:00:00Z', activeTo: null };
    let tokens: Array<Omit<typeof issued, "activeTo"> & { activeTo: string | null }> = [];
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (path === '/profile/api-tokens' && issuing(options)) {
        tokens = [issued];
        return { token: issued, secret: 'one-time-secret' };
      }
      if (path === '/profile/api-tokens') return { ...catalog, mcpEnabled: true, mcpUrl: 'https://wiki.example/mcp', tokens: [...tokens] };
      if (path === '/profile/api-tokens/new-token' && options?.method === 'DELETE') {
        tokens = [{ ...issued, activeTo: '2026-10-06T00:00:00Z' }];
        return undefined;
      }
      return base(path, options);
    });
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'api' } });
    await vi.waitFor(() => expect(wrapper!.find('[data-token-new]').exists()).toBe(true));
    await wrapper.get('[data-token-new]').trigger('click');
    await wrapper.get('[data-token-name]').setValue('Automation');
    await confirmIssue();
    await flushPromises();
    await wrapper.get('[data-token-secret] input[type="checkbox"]').setValue(true);
    await wrapper.get('[data-one-time-done]').trigger('click');
    await vi.waitFor(() => expect(wrapper!.find('[data-token="new-token"]').exists()).toBe(true));

    await wrapper.setProps({ tab: 'account' }); await flushPromises();
    await wrapper.setProps({ tab: 'api' }); await flushPromises();
    expect(wrapper.find('[data-token="new-token"]').exists()).toBe(true);

    await wrapper.get('[data-token="new-token"] [data-token-revoke]').trigger('click');
    await wrapper.get('[data-confirm-action]').trigger('click');
    await vi.waitFor(() => expect(wrapper!.get('[data-token="new-token"]').text()).toContain('Zneplatněný'));
    await wrapper.setProps({ tab: 'account' }); await flushPromises();
    await wrapper.setProps({ tab: 'api' }); await flushPromises();
    expect(wrapper.get('[data-token="new-token"]').text()).toContain('Zneplatněný');
  });
  it('shows the API document in the Scalar view in place and disposes it on hide', async () => {
    configure();
    const base = vi.mocked(api).getMockImplementation()!;
    vi.mocked(api).mockImplementation(async (path, options) => path === '/profile/api-document' ? apiDocument : base(path, options));
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'api' }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.find('[data-api-docs]').exists()).toBe(true), { timeout: 5000 });
    await wrapper.get('[data-api-docs]').trigger('click');
    await vi.waitFor(() => expect(mountReference).toHaveBeenCalledTimes(1));
    const [host, data, options] = mountReference.mock.calls[0]!;
    expect(host).toBe(wrapper.get('[data-api-docs-reference]').element);
    expect(JSON.parse(data.source)).toEqual(apiDocument);
    expect(options).toEqual({ allowRequests: false });
    await wrapper.get('[data-api-docs-close]').trigger('click');
    expect(disposeReference).toHaveBeenCalledTimes(1);
    expect(wrapper.find('[data-api-docs-reference]').exists()).toBe(false);
  });

  it('locks the account form during reload and refuses a save against the stale version', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get('input[autocomplete=name]').setValue('Draft');
    vi.mocked(api).mockRejectedValueOnce(new ApiError(409, 'Conflict'));
    await saveAccount(); await flushPromises();
    const reload = deferred<typeof account>();
    vi.mocked(api).mockReturnValueOnce(reload.promise);
    const confirm = vi.spyOn(window, 'confirm');
    await wrapper.get('[role=alert] button').trigger('click');
    // Reloading over a draft asks through ConfirmDialog, never window.confirm.
    expect(confirm).not.toHaveBeenCalled();
    await wrapper.get('[data-confirm-action]').trigger('click');
    await flushPromises();
    await saveAccount();
    expect(vi.mocked(api).mock.calls.filter(([, options]) => options?.method === 'PUT')).toHaveLength(1);
    reload.resolve({ ...account, displayName: 'Current name', version: 'v2' }); await flushPromises();
    expect((wrapper.get('input[autocomplete=name]').element as HTMLInputElement).value).toBe('Current name');
    expect(wrapper.get('input[autocomplete=name]').attributes('disabled')).toBeUndefined();
  });

  it('propagates password drafts and locks profile navigation while the password changes', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'security' } });
    await openPasswordChange();
    const fields = wrapper.findAll('form[aria-label="Změna vlastního hesla"] input');
    await fields[0]!.setValue('current password'); await fields[1]!.setValue('new long password'); await fields[2]!.setValue('new long password');
    expect(last('dirty')).toBe(true);
    const saving = deferred<undefined>(); vi.mocked(post).mockReturnValueOnce(saving.promise);
    await wrapper.get('form[aria-label="Změna vlastního hesla"]').trigger('submit');
    expect(last('busy')).toBe(true);
    expect(wrapper.get(tab('account')).attributes('aria-disabled')).toBe('true');
    expect(wrapper.get('[data-profile-back]').attributes('disabled')).toBeDefined();
    saving.resolve(undefined); await flushPromises();
    expect(last('busy')).toBe(false); expect(last('dirty')).toBe(false);
  });

  it('protects a token request and its one-time secret until the user acknowledges saving it', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'api' } });
    await vi.waitFor(() => expect(wrapper!.find('[data-token-new]').exists()).toBe(true), { timeout: 5000 });
    await wrapper.get('[data-token-new]').trigger('click');
    await wrapper.get('[data-token-name]').setValue('Automation'); await flushPromises();
    expect(last('dirty')).toBe(true);
    const saving = deferred<unknown>(); const base = vi.mocked(api).getMockImplementation()!;
    vi.mocked(api).mockImplementation(async (path, options) => path === '/profile/api-tokens' && issuing(options) ? saving.promise : base(path, options));
    await confirmIssue('[aria-label="Nový osobní API token"] [data-confirm-reauth]');
    expect(last('busy')).toBe(true);
    expect(wrapper.get(tab('account')).attributes('aria-disabled')).toBe('true');
    saving.resolve({ secret: 'one-time-test-secret', token: { id: 'token-1', name: 'Automation', expiresAt: '2026-10-01T00:00:00Z' } }); await flushPromises();
    expect(last('busy')).toBe(false); expect(last('dirty')).toBe(true);
    expect(wrapper.find('[aria-label="Nově vydaný API token"]').exists()).toBe(true);
    await wrapper.get('[data-token-secret] input[type="checkbox"]').setValue(true);
    await wrapper.get('[data-one-time-done]').trigger('click');
    expect(last('dirty')).toBe(false);
  });

  it('gives every navigation item its own icon', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    const items = wrapper.get('.settings-shell-rail nav[aria-label="Nastavení profilu"]').findAll('a');
    const names = items.map(item => item.find('svg').attributes('data-icon') ?? item.html());
    expect(items).toHaveLength(7);
    expect(new Set(names).size).toBe(names.length);
  });

  it('asks before leaving a dirty section, and Zahodit navigates away', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get('input[autocomplete=name]').setValue('Draft name');
    await wrapper.get(tab('security')).trigger('click');
    expect(wrapper.emitted('navigate')).toBeUndefined();
    const dialog = wrapper.get('dialog');
    expect(dialog.text()).toContain('Zahodit');
    await wrapper.findAll('dialog button').find(b => b.text() === 'Zahodit')!.trigger('click');
    expect(wrapper.emitted('navigate')?.at(-1)).toEqual(['security']);
    expect((wrapper.get('input[autocomplete=name]').element as HTMLInputElement).value).toBe('Reader');
  });

  it('Zůstat keeps the user on the section without navigating', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get('input[autocomplete=name]').setValue('Draft name');
    await wrapper.get(tab('security')).trigger('click');
    await wrapper.findAll('dialog button').find(b => b.text() === 'Zůstat')!.trigger('click');
    expect(wrapper.emitted('navigate')).toBeUndefined();
    expect((wrapper.get('input[autocomplete=name]').element as HTMLInputElement).value).toBe('Draft name');
  });

  it('Uložit saves the account form before navigating away', async () => {
    configure();
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (path === '/profile/account' && options?.method === 'PUT') return { ...account, displayName: 'Draft name' };
      if (path === '/profile/account') return account;
      throw new Error('Unexpected endpoint: ' + path);
    });
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get('input[autocomplete=name]').setValue('Draft name');
    await wrapper.get(tab('security')).trigger('click');
    await wrapper.findAll('dialog button').find(b => b.text() === 'Uložit')!.trigger('click');
    await flushPromises();
    expect(wrapper.emitted('navigate')?.at(-1)).toEqual(['security']);
  });

  it('guards "Zpět" the same way as switching sections', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get('input[autocomplete=name]').setValue('Draft name');
    await wrapper.get('[data-profile-back]').trigger('click');
    expect(wrapper.emitted('back')).toBeUndefined();
    expect(wrapper.get('dialog').text()).toContain('Zahodit');
    await wrapper.findAll('dialog button').find(b => b.text() === 'Zahodit')!.trigger('click');
    expect(wrapper.emitted('back')).toHaveLength(1);
  });

  it('offers sign-out under the section navigation and locks it while a section is busy', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    const logout = wrapper.get('.settings-shell-rail [data-profile-logout]');
    expect(logout.text()).toContain('Odhlásit se');
    await logout.trigger('click');
    expect(wrapper.emitted('logout')).toHaveLength(1);
    await wrapper.setProps({ appearancePending: true });
    expect(wrapper.get('.settings-shell-rail [data-profile-logout]').attributes('disabled')).toBeDefined();
  });

  it('does not reopen the unsaved-changes dialog after appearance was saved (finding 4)', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'appearance' } });
    await flushPromises();
    await wrapper.get('[data-appearance="mode"] [role="radio"][data-value="dark"], [data-appearance="mode"] [data-value="dark"]').trigger('click');
    expect(last('preview')).toEqual({ layout: 'connected', accent: 'blue', mode: 'dark', motion: 'system' });
    await wrapper.setProps({ appearanceDirty: true });
    await wrapper.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click');
    expect(wrapper.emitted('saveAppearance')).toHaveLength(1);
    // The parent persists the change and echoes the saved value back down as props,
    // exactly as App.vue does once saveProfile() resolves.
    await wrapper.setProps({ appearance: { layout: 'connected', accent: 'blue', mode: 'dark' }, appearanceDirty: false });
    await wrapper.get(tab('security')).trigger('click');
    expect(wrapper.emitted('navigate')?.at(-1)).toEqual(['security']);
    expect(wrapper.find('dialog[open]').exists()).toBe(false);
  });

  it('hides Uložit on tabs without a save action, and Zahodit clears child dirty flags (finding 5)', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'security' } });
    await openPasswordChange();
    const fields = wrapper.findAll('form[aria-label="Změna vlastního hesla"] input');
    await fields[0]!.setValue('current password'); await fields[1]!.setValue('new long password'); await fields[2]!.setValue('new long password');
    expect(last('dirty')).toBe(true);
    await wrapper.get(tab('account')).trigger('click');
    const dialog = wrapper.get('dialog');
    expect(dialog.text()).not.toContain('Uložit');
    await wrapper.findAll('dialog button').find(b => b.text() === 'Zahodit')!.trigger('click');
    expect(wrapper.emitted('navigate')?.at(-1)).toEqual(['account']);
    expect(last('dirty')).toBe(false);
  });

  it('propagates identity-link draft and busy state to profile navigation', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'security' } });
    await vi.waitFor(() => expect(wrapper!.find('.identity-connections button').exists()).toBe(true), { timeout: 5000 });
    await wrapper.findAll('.identity-connections button').find(button => button.text() === 'Propojit')!.trigger('click');
    expect(last('dirty')).toBe(true);
    const saving = deferred<unknown>(); vi.mocked(post).mockReturnValueOnce(saving.promise);
    await wrapper.get('form[aria-label="Potvrzení propojení identity"]').trigger('submit');
    expect(last('busy')).toBe(true);
    expect(wrapper.get(tab('account')).attributes('aria-disabled')).toBe('true');
    saving.resolve({ loginUrl: 'javascript:invalid' }); await flushPromises();
    expect(last('busy')).toBe(false);
    expect(wrapper.text()).toContain('Přihlašovací služba vrátila neplatnou adresu.');
  });
});

describe('profile navigation semantics', () => {
  it('is the settings navigation: real links with the current section marked', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    const nav = wrapper.get('.settings-shell-rail nav[aria-label="Nastavení profilu"]');
    // Grouped as on board 12; Vzhled is the second half of Účet a vzhled, so it has no entry of its own.
    expect(nav.findAll('.settings-nav-heading').map(heading => heading.text())).toEqual(['Účet', 'Přístup', 'Činnost']);
    expect(nav.findAll('a').map(link => link.attributes('href'))).toEqual(['account', 'security', 'access', 'requests', 'api', 'activity', 'visits'].map(id => `#/profile/${id}`));
    expect(wrapper.get(`.settings-shell-rail ${tab('account')}`).attributes('aria-current')).toBe('page');
    expect(wrapper.get(`.settings-shell-rail ${tab('security')}`).attributes('aria-current')).toBeUndefined();
    expect(wrapper.get('h1').attributes('id')).toBe('profile-heading');
    expect(wrapper.get('h1').text()).toBe('Profil');
    expect(wrapper.get('.settings-page h2').text()).toBe('Účet a vzhled');
  });

  it('names the way back as the application shell says it, and Zpět do wiki when the shell says nothing', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    expect(wrapper.get('[data-profile-back]').text()).toBe('Zpět do wiki');
    await wrapper.setProps({ backLabel: 'Zpět do správy wiki' });
    expect(wrapper.get('[data-profile-back]').text()).toBe('Zpět do správy wiki');
  });

  it('shows Moje přístupy read-only from the caller\'s own access', async () => {
    configure();
    const base = vi.mocked(api).getMockImplementation()!;
    vi.mocked(api).mockImplementation(async (path, init) => path === '/access/me'
      ? { principal: { kind: 'user', id: 'me', name: 'Reader' }, scope: 'self', canManage: false,
        entries: [{ kind: 'space', resourceId: 'provoz', title: 'Provoz', spaceId: 'provoz', spaceName: 'Provoz', level: 'read', sources: [{ via: 'direct', label: 'Přímý přístup', level: 'read', effective: true }] }] }
      : base(path, init));
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'access' } });
    await vi.waitFor(() => expect(wrapper!.find('[data-access-entry="space:provoz"]').exists()).toBe(true), { timeout: 5000 });
    expect(wrapper.get('.settings-page h2').text()).toBe('Moje přístupy');
    expect(wrapper.get('[data-profile-section="access"]').text()).toContain('Požádejte o přístup přímo u trezoru nebo stránky.');
    expect(wrapper.find('[data-change-access]').exists()).toBe(false);
  });

  it('still routes a section click through the unsaved-changes guard', async () => {
    configure();
    wrapper = mount(ProfilePage, { props }); await flushPromises();
    await wrapper.get(tab('access')).trigger('click');
    expect(last('navigate')).toBe('access');
  });

  it('keeps Účet and Vzhled on one page whose question saves both drafts', async () => {
    configure();
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'appearance', username: 'reader' } }); await flushPromises();
    // The header names the account; #/profile/appearance shows the same page with both halves.
    expect(wrapper.get('[data-profile-identity]').text()).toContain('Reader');
    expect(wrapper.get('[data-profile-identity]').text()).toContain('reader · místní účet');
    expect(wrapper.find('[data-profile-link]').exists()).toBe(true);
    expect(wrapper.get(`.settings-shell-rail ${tab('account')}`).attributes('aria-current')).toBe('page');
    await wrapper.get('[data-profile-name]').setValue('Reader Two');
    await wrapper.setProps({ appearanceDirty: true });
    await wrapper.get(tab('security')).trigger('click');
    const dialog = wrapper.findAll('dialog').find(candidate => candidate.text().includes('Neuložené změny'))!;
    await dialog.findAll('button').find(button => button.text() === 'Uložit')!.trigger('click'); await flushPromises();
    expect(vi.mocked(api).mock.calls.some(([path, init]) => path === '/profile/account' && (init as RequestInit | undefined)?.method === 'PUT')).toBe(true);
    expect(wrapper.emitted('saveAppearance')).toHaveLength(1);
    await wrapper.setProps({ appearancePending: true }); await wrapper.setProps({ appearancePending: false, appearanceDirty: false }); await flushPromises();
    expect(last('navigate')).toBe('security');
  });
});

it('previews every accent without changing layout, mode or motion', async () => {
  configure();
  wrapper = mount(ProfilePage, { props: { ...props, tab: 'appearance', appearance: { layout: 'islands', accent: 'blue', mode: 'dark' } } });
  await flushPromises();
  const choices = wrapper.findAll('[data-appearance="accent"] .accent-swatch-cell');
  expect(choices.map(choice => choice.attributes('data-value'))).toEqual(themeAccents.map(accent => accent.id));
  for (const choice of [...choices.slice(2), choices[0]!]) {
    await choice.get('[role="radio"]').trigger('click');
    expect(last('preview')).toEqual({ layout: 'islands', accent: choice.attributes('data-value'), mode: 'dark', motion: 'system' });
  }
  await wrapper.setProps({ appearanceDirty: true });
  await wrapper.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click');
  expect(wrapper.emitted('saveAppearance')).toHaveLength(1);
});

it('previews reduced motion and the reading width with the rest of Vzhled', async () => {
  configure();
  wrapper = mount(ProfilePage, { props: { ...props, tab: 'appearance', readingWidth: 'comfortable' } });
  await flushPromises();
  expect(wrapper.text()).toContain('Podle systému (vypnuto)');
  await wrapper.get('[data-appearance="motion"]').trigger('click');
  expect(last('preview')).toEqual({ ...props.appearance, motion: 'reduce' });
  expect(wrapper.text()).toContain('Zapnuto');
  await wrapper.get('[data-appearance="reading-width"] [data-value="wide"]').trigger('click');
  expect(last('previewReadingWidth')).toBe('wide');
});

describe('AI section', () => {
  const aiPreferences = { editorSuggestions: 'on', disabledSuggestionTypes: [], version: 'p1' };
  const modeOff = () => wrapper!.findAll('[data-ai-suggestions-mode] [role="radio"]').find(item => item.text().includes('Vypnuto'))!;

  it('shows the ai section only with an ai capability and saves preferences explicitly', async () => {
    configure();
    vi.mocked(aiPreferencesApi.load).mockResolvedValue(aiPreferences as never);
    vi.mocked(aiPreferencesApi.save).mockImplementation((async (draft: object) => ({ ...draft, version: 'p2' })) as never);
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'ai' } }); await flushPromises();
    // Without a capability the navigation offers no AI section.
    expect(wrapper.find(tab('ai')).exists()).toBe(false);
    aiAvailable.value = true; await flushPromises();
    expect(wrapper.get(tab('ai')).text()).toContain('AI');
    expect(wrapper.find(tab('ai') + ' [data-ai-glyph]').exists()).toBe(false);
    await modeOff().trigger('click'); await flushPromises();
    expect(last('dirty')).toBe(true);
    // A section change with a draft asks first; nothing was saved yet.
    await wrapper.get(tab('access')).trigger('click'); await flushPromises();
    expect(wrapper.emitted('navigate')).toBeUndefined();
    expect(aiPreferencesApi.save).not.toHaveBeenCalled();
    // "Uložit" in the question saves the draft for real and then continues to the section.
    await wrapper.findAll('.app-dialog-footer button').find(button => button.text() === 'Uložit')!.trigger('click'); await flushPromises();
    expect(aiPreferencesApi.save).toHaveBeenCalledWith({ editorSuggestions: 'off', disabledSuggestionTypes: [] }, 'p1');
    // The saved preferences reach the application, which reloads auth/me.
    expect(wrapper.emitted('sessionChanged')).toHaveLength(1);
    expect(last('dirty')).toBe(false);
    expect(last('navigate')).toBe('access');
  });

  it('keeps an open ai section with a draft when the ai capability disappears, hiding only its navigation entry', async () => {
    configure();
    aiAvailable.value = true;
    vi.mocked(aiPreferencesApi.load).mockResolvedValue(aiPreferences as never);
    vi.mocked(aiPreferencesApi.save).mockImplementation((async (draft: object) => ({ ...draft, version: 'p2' })) as never);
    wrapper = mount(ProfilePage, { props: { ...props, tab: 'ai' } }); await flushPromises();
    await modeOff().trigger('click'); await flushPromises();
    aiAvailable.value = false; await flushPromises();
    expect(wrapper.find(tab('ai')).exists()).toBe(false);
    expect(wrapper.find('[data-profile-section="ai"]').exists()).toBe(true);
    expect(wrapper.emitted('navigate')).toBeUndefined();
    // The preferences are the user's own data: they save even though the AI is gone.
    await wrapper.get('[data-profile-section="ai"] [data-save-bar-save]').trigger('click'); await flushPromises();
    expect(aiPreferencesApi.save).toHaveBeenCalledTimes(1);
  });
});
