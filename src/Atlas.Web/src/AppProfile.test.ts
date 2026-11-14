import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';
import { emptyDocument } from './document';
import { isMarkdownReducedMotion } from '@atlas/markdown/core';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { jsonResponse as response, memoryStorage, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from './testing/appHarness';

vi.mock('./components/DocumentEditor.vue', async () => { const { defineComponent, onMounted, watch } = await import('vue'); return { __esModule: true, default: defineComponent({
  name: 'DocumentEditor', props: ['value', 'readonly'], emits: ['update:value', 'change', 'ready', 'collaborationStatus', 'editIntent'],
  setup(props: { readonly?: boolean }, { emit, expose }) {
    onMounted(() => emit('ready'));
    watch(() => props.readonly, readonly => emit('collaborationStatus', {
      enabled: true, generation: 'g1', canSave: !readonly, connection: 'online',
      pendingChanges: 0, error: null, projectionSave: 'current',
    }), { immediate: true });
    expose({ focus: () => true, capture: () => ({ contentJson: JSON.stringify(emptyDocument()), markdown: '' }) });
  },
  template: '<textarea aria-label="Testovací dokument" @input="$emit(\'update:value\', {}); $emit(\'change\')" />',
}) }; });

let wrapper: VueWrapper | undefined;
const account = { displayName: 'Test Admin', email: null, version: 'v1', source: 'builtin', canEditDisplayName: true, canEditEmail: true, currentSessionAuthenticatedAt: null, currentSessionAuthenticationMethod: null, lastSignInAt: null, lastSignInMethod: null };
const page = { id: 'page-1', spaceId: 'space-1', title: 'Dokument', version: 1, isPublished: false, contentJson: JSON.stringify(emptyDocument()), markdown: '', publishedVersion: null, canEdit: true };
// A cold profile/section import may outlive waitFor's default deadline under parallel load.
async function settleLazyViews() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); }
const wiki: Route = path => {
  if (path === '/api/auth/me') return response({ id: 'user-1', username: 'admin', displayName: 'Test Admin', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: 'light' } });
  if (path === '/api/admin/setup') return response({ pending: false });
  if (path === '/api/spaces') return response([{ id: 'space-1', name: 'Tým', icon: 'book', color: 'blue', role: 'Admin' }]);
  if (path === '/api/spaces/space-1/tree') return response({ version: 1, items: [{ ...page, canEdit: true, sortOrder: 1 }] });
  if (path === '/api/pages/page-1/publish') return response({ ...page, isPublished: true, publishedVersion: 1 });
  if (path === '/api/pages/page-1') return response(page);
  if (path === '/api/profile/account') return response(account);
  if (path.startsWith('/api/analytics/activity')) return response({ revisions: 0, pages: 0, days: [], authors: [] });
  if (path.startsWith('/api/analytics/popular')) return response({ items: [] });
  if (path === '/api/profile/appearance') return response({ layout: 'connected', accent: 'blue', mode: 'light' });
  if (path === '/api/profile/security') return response({ hasBuiltinCredential: true, credentialState: 'active' });
  if (path === '/api/auth/methods') return response({ builtinEnabled: true, providers: [] });
  if (path === '/api/profile/identities') return response({ items: [] });
};
async function start(hash = '#/pages/page-1') {
  wrapper = mountApp({ url: '/' + hash, fetch: routedFetch(wiki, sessionRoutes, quietRoutes) });
  await flushPromises(); await flushPromises();
  return wrapper;
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
/** A button of the open UnsavedChangesDialog (Zůstat, Zahodit, Uložit). */
const leaveButton = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(item => item.textContent?.trim() === label);


describe('profile is a workspace page', () => {
  it('restores an activity deep link', async () => {
    const app = await start('#/profile/activity');
    // The activity page and its heatmap are a lazy chunk, cold on the first test.
    await vi.waitFor(() => expect(app.find('.profile-activity').exists()).toBe(true), { timeout: 5000 });
    expect(location.hash).toBe('#/profile/activity');
  });
  it('keeps notification controls in the previewed light or dark theme', async () => {
    const app = await start('#/profile/appearance');
    await vi.waitFor(() => expect(app.find('[data-appearance="mode"] [data-value="dark"]').exists()).toBe(true));
    const theme = () => document.querySelector('[data-sonner-toaster]')?.getAttribute('data-sonner-theme');
    expect(theme()).toBe('light');
    await app.get('[data-appearance="mode"] [data-value="dark"]').trigger('click');
    await vi.waitFor(() => expect(theme()).toBe('dark'));
    await app.get('[data-profile-section="appearance"] [data-save-bar-cancel]').trigger('click');
    await vi.waitFor(() => expect(theme()).toBe('light'));
  });

  it('previews reduced motion and the reading width until cancelled, and keeps the saved width in this browser', async () => {
    const { values: stored, storage } = memoryStorage();
    vi.stubGlobal('localStorage', storage);
    const app = await start('#/profile/appearance');
    await vi.waitFor(() => expect(app.find('[data-appearance="motion"]').exists()).toBe(true));
    const root = document.documentElement;
    await app.get('[data-appearance="motion"]').trigger('click');
    // Omezit animace forces the reduced path of motion.css and aiMotion for the whole page.
    expect(root.hasAttribute('data-atlas-reduced-motion')).toBe(true);
    // ... and the editor's own reduced path (geometry, scrolling, charts), not only the mapped tokens (review 3.5.1, finding 8).
    expect(isMarkdownReducedMotion(document.body)).toBe(true);
    await app.get('[data-appearance="reading-width"] [data-value="comfortable"]').trigger('click');
    expect(app.get('.atlas').attributes('data-reading-width')).toBe('comfortable');
    await app.get('[data-profile-section="appearance"] [data-save-bar-cancel]').trigger('click'); await flushPromises();
    expect(root.hasAttribute('data-atlas-reduced-motion')).toBe(false);
    expect(isMarkdownReducedMotion(document.body)).toBe(false);
    expect(app.get('.atlas').attributes('data-reading-width')).toBe('wide');
    await app.get('[data-appearance="reading-width"] [data-value="comfortable"]').trigger('click');
    await app.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click'); await flushPromises();
    expect(stored.get('atlas:reading:user-1:width')).toBe('comfortable');
  });

  it('visibly separates the island panels and reconnects them when preview is cancelled', async () => {
    const style = document.createElement('style');
    style.textContent = readFileSync(resolve(process.cwd(), 'src/shell.css'), 'utf8');
    document.head.append(style);
    try {
      const app = await start('#/profile/appearance');
      await vi.waitFor(() => expect(app.find('[data-appearance="layout"] [data-value="islands"]').exists()).toBe(true));
      const shell = app.get('.atlas').element;
      // jsdom's window is 1024px wide, where the sidebar is the drawer without a radius (shell.css, max-width: 1024px); the workspace is an island there.
      const workspace = app.get('.workspace').element;
      await app.get('[data-appearance="layout"] [data-value="islands"] [role="radio"]').trigger('click');
      expect(document.documentElement.dataset.layout).toBe('islands');
      expect(getComputedStyle(shell).padding).toBe('12px');
      expect(getComputedStyle(shell).gap).toBe('12px');
      expect(getComputedStyle(workspace).borderRadius).toBe('12px');
      await app.get('[data-profile-section="appearance"] [data-save-bar-cancel]').trigger('click');
      await flushPromises();
      expect(document.documentElement.dataset.layout).toBe('connected');
      expect(getComputedStyle(shell).padding).not.toBe('12px');
      expect(getComputedStyle(workspace).borderRadius).not.toBe('12px');
    } finally { style.remove(); }
  });

  it('opens a routed profile page and preserves the mounted dirty editor through appearance preview and cancel', async () => {
    const app = await start();
    await vi.waitFor(() => expect(app.find('textarea').exists()).toBe(true));
    const editor = app.get('textarea').element;
    // The reader points into the text: the live editor attaches (3.6-F), then types.
    app.getComponent({ name: 'DocumentEditor' }).vm.$emit('editIntent', 'point'); await flushPromises();
    await app.get('textarea').setValue('Rozepsaný text');
    const confirm = vi.spyOn(window, 'confirm');
    await app.get('.profile-launch').trigger('click'); await flushPromises();
    await vi.waitFor(() => expect(app.find('.profile-page').exists()).toBe(true));
    expect(location.hash).toBe('#/profile/account');
    expect(app.get('#profile-heading').text()).toBe('Profil');
    expect(app.find('dialog[aria-label="Profil"]').exists()).toBe(false);
    expect(confirm).not.toHaveBeenCalled();
    // Vzhled is the second half of Účet a vzhled, where the profile opens.
    await app.get('[data-appearance="mode"] [data-value="dark"]').trigger('click');
    // The theme lives on <html> now: Tailwind's dark variant is keyed to the root
    // element, and overlays portalled to document.body inherit only from above.
    expect(document.documentElement.dataset.mode).toBe('dark');
    await app.get('[data-appearance="accent"] [data-value="sunset"] [role="radio"]').trigger('click');
    expect(document.documentElement.dataset.accent).toBe("sunset");
    expect(app.get('textarea').element).toBe(editor);
    await app.get('[data-profile-section="appearance"] [data-save-bar-cancel]').trigger('click'); await flushPromises();
    expect(document.documentElement.dataset.mode).toBe('light');
    expect(document.documentElement.dataset.accent).toBe('blue');
    await app.get('.settings-back').trigger('click'); await flushPromises();
    expect(location.hash).toBe('#/pages/page-1');
    expect(app.get('textarea').element).toBe(editor);
    expect((editor as HTMLTextAreaElement).value).toBe('Rozepsaný text');
    expect(app.get('textarea').isVisible()).toBe(true);
    // The page's own changes ask nothing on the way out (3.6-B): a checkpoint goes instead.
    await app.findAll('.nav-link').find(item => item.text().trim() === 'Knihovna')!.trigger('click'); await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    expect(leaveButton('Zůstat')).toBeUndefined();
    expect(location.hash).toBe('#/library/spaces');
  });

  it('loads account deep links directly without rendering a modal', async () => {
    const app = await start('#/profile/account');
    await vi.waitFor(() => expect(app.find('.profile-page').exists()).toBe(true));
    expect(app.get('input[autocomplete="name"]').element).toBeTruthy();
    expect(app.find('dialog[open]').exists()).toBe(false);
    expect(location.hash).toBe('#/profile/account');
  });

  it('protects unsaved account edits when leaving through sidebar navigation', async () => {
    const app = await start('#/profile/account');
    await vi.waitFor(() => expect(app.find('input[autocomplete="name"]').exists()).toBe(true));
    await app.get('input[autocomplete="name"]').setValue('Změněné jméno');
    const confirm = vi.spyOn(window, 'confirm');
    // Settings and the profile hold the rail (refresh 2.5): its way back is the sidebar navigation that leaves.
    const home = () => app.get('.settings-back');
    await home().trigger('click'); await flushPromises();
    // The shared UnsavedChangesDialog asks, never window.confirm; Zůstat keeps the draft.
    expect(confirm).not.toHaveBeenCalled();
    const dialog = () => app.findAll('dialog[open]').find(candidate => candidate.text().includes('Neuložené změny'));
    await dialog()!.findAll('button').find(button => button.text() === 'Zůstat')!.trigger('click'); await flushPromises();
    expect(location.hash).toBe('#/profile/account');
    expect((app.get('input[autocomplete="name"]').element as HTMLInputElement).value).toBe('Změněné jméno');
    // Zahodit drops the draft and continues back to the wiki root, which is Domů.
    await home().trigger('click'); await flushPromises();
    await dialog()!.findAll('button').find(button => button.text() === 'Zahodit')!.trigger('click'); await flushPromises();
    expect(location.hash).toBe('#/');
    await vi.waitFor(() => expect(app.find('[data-home-dashboard]').exists()).toBe(true));
    expect(app.find('.profile-page').exists()).toBe(false);
  });

  it('guards leaveProfile through the UnsavedChangesDialog instead of a native confirm (finding 5)', async () => {
    const app = await start();
    await vi.waitFor(() => expect(app.find('textarea').exists()).toBe(true));
    await app.get('.profile-launch').trigger('click'); await settleLazyViews();
    await app.get('.settings-sidebar-rail [data-path="/profile/security"]').trigger('click'); await settleLazyViews();
    expect(app.find('[data-password-change]').exists()).toBe(true);
    await app.get('[data-password-change]').trigger('click'); await flushPromises();
    const fields = app.findAll('form[aria-label="Změna vlastního hesla"] input');
    await fields[0]!.setValue('current password'); await fields[1]!.setValue('new long password'); await fields[2]!.setValue('new long password');
    const confirm = vi.spyOn(window, 'confirm');
    history.pushState(null, '', '/#/pages/page-1');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    const dialogs = app.findAll('dialog[open]');
    const dialog = dialogs.find(candidate => candidate.text().includes('Neuložené změny'));
    expect(dialog).toBeTruthy();
    expect(dialog!.text()).not.toContain('Uložit');
    expect(app.find('.profile-page').exists()).toBe(true);
    await dialog!.findAll('button').find(button => button.text() === 'Zahodit')!.trigger('click');
    await flushPromises();
    expect(app.find('.profile-page').exists()).toBe(false);
  });

  it('never lets the appearance confirmation leak onto the document behind the profile (findings-settings §4.3)', async () => {
    const app = await start();
    await vi.waitFor(() => expect(app.find('textarea').exists()).toBe(true));
    await app.get('.profile-launch').trigger('click'); await flushPromises();
    // Vzhled is the second half of Účet a vzhled, where the profile opens.
    // Saving is explicit and offered once something changed.
    await app.get('[data-appearance="mode"] [data-value="dark"]').trigger('click'); await flushPromises();
    await app.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click'); await flushPromises();
    // The confirmation is a toast now rather than a standing global notice; the
    // guarantee this test exists for -- that it never lands on the document
    // behind the profile -- is the assertion below and is unchanged.
    await vi.waitFor(() => expect(document.body.textContent).toContain('Vzhled uložen.'));
    await app.get('.settings-back').trigger('click'); await flushPromises();
    const status = app.get('.document-status').text().trim();
    // '': a writer's draft is said in the top bar, not by the title.
    expect(['', 'Publikováno', 'Koncept', 'Nepublikované změny', 'Neuložené změny']).toContain(status);
    expect(status).not.toContain('Vzhled uložen.');
  });

  it('marks a fresh publication confirmation as transient and clears it two seconds later', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      const app = await start();
      await vi.waitFor(() => expect(app.find('textarea').exists()).toBe(true));
      // The editor attaches on the intent to write; the unpublished page then offers Publikovat (the page saves itself).
      app.getComponent({ name: 'DocumentEditor' }).vm.$emit('editIntent', 'point');
      await flushPromises();
      await app.get('textarea').trigger('input');
      await flushPromises();
      const publishButton = app.get('.document-primary-actions .publish-button');
      expect(publishButton.attributes('disabled')).toBeUndefined();
      await publishButton.trigger('click');
      await flushPromises(); await flushPromises();
      const status = app.get('.document-status');
      expect(status.text()).toContain('Stránka byla publikována.');
      expect(status.attributes('data-transient')).toBe('true');
      expect(status.find('[data-confirmed="true"] svg[data-icon="circle-check"]').exists()).toBe(true);
      await vi.advanceTimersByTimeAsync(2000);
      await flushPromises();
      const settled = app.get('.document-status');
      expect(settled.attributes('data-transient')).toBeUndefined();
      expect(settled.text()).not.toContain('Stránka byla publikována.');
    } finally { vi.useRealTimers(); }
  });
});

describe('Stránky otevírat (owner 2026-11-07)', () => {
  it('stores jen pro čtení as the account openLocked, rovnou k úpravám being the default', async () => {
    const fetch = routedFetch(wiki, sessionRoutes, quietRoutes);
    wrapper = mountApp({ url: '/#/profile/appearance', fetch });
    await vi.waitFor(() => expect(wrapper!.find('[data-appearance="open-mode"]').exists()).toBe(true));
    // An account without the field opens pages editable.
    expect(wrapper.get('[data-appearance="open-mode"] [data-value="edit"]').attributes('data-state')).toBe('checked');
    await wrapper.get('[data-appearance="open-mode"] [data-value="read"]').trigger('click');
    await wrapper.get('[data-profile-section="appearance"] [data-save-bar-save]').trigger('click'); await flushPromises();
    const save = fetch.mock.calls.find(([path, init]) => String(path) === '/api/profile/appearance' && init?.method === 'PUT');
    expect(JSON.parse(String(save![1]!.body))).toMatchObject({ openLocked: true });
  });

  it('opens a page read-only for a writer who chose it, and Upravit makes only that page editable', async () => {
    const readOnly: Route = path => path === '/api/auth/me'
      ? response({ id: 'user-1', username: 'admin', displayName: 'Test Admin', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: 'light', openLocked: true } })
      : undefined;
    wrapper = mountApp({ url: '/#/pages/page-1', fetch: routedFetch(readOnly, wiki, sessionRoutes, quietRoutes) });
    await flushPromises(); await flushPromises();
    await vi.waitFor(() => expect(wrapper!.find('textarea').exists()).toBe(true));
    const readonly = () => wrapper!.getComponent({ name: 'DocumentEditor' }).props('readonly');
    // The same live editor, only not editable; the bar offers Upravit beside the lock.
    expect(readonly()).toBe(true);
    const bar = () => wrapper!.get('header .bar-actions .document-actions');
    expect(bar().get('[data-edit-lock]').attributes('aria-pressed')).toBe('true');
    await bar().get('[data-edit-unlock]').trigger('click'); await flushPromises();
    expect(readonly()).toBe(false);
    expect(bar().find('[data-edit-unlock]').exists()).toBe(false);
    // Locking again stays on the lock.
    await bar().get('[data-edit-lock]').trigger('click'); await flushPromises();
    expect(readonly()).toBe(true);
    expect(bar().find('[data-edit-unlock]').exists()).toBe(true);
    // Typing „hello“ fast into the locked page: its „e“ is a letter, not the unlock; E alone after a pause unlocks.
    const key = async (letter: string, after?: Event) => {
      const event = new KeyboardEvent('keydown', { key: letter, bubbles: true, cancelable: true });
      if (after) Object.defineProperty(event, 'timeStamp', { value: after.timeStamp + 5000 });
      document.body.dispatchEvent(event); await flushPromises();
      return event;
    };
    await key('h');
    const typed = await key('e');
    expect(readonly()).toBe(true);
    await key('e', typed);
    expect(readonly()).toBe(false);
  });
});
